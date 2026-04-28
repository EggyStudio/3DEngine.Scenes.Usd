using FluentAssertions;
using Xunit;

namespace Engine.Tests.Scenes.Usd;

/// <summary>
/// Direct tests for <see cref="UsdSceneLoader"/>, <see cref="UsdSceneReader"/>, and
/// <see cref="UsdSceneWriter"/> that don't require running through the asset pipeline.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Backend", "Usd")]
[Collection(UsdTestCollection.Name)]
public class UsdSceneLoaderTests
{
    [Fact]
    public void UsdSceneLoader_Declares_All_Four_Usd_Extensions()
    {
        var loader = new UsdSceneLoader();

        loader.Extensions.Should().BeEquivalentTo([".usd", ".usda", ".usdc", ".usdz"]);
    }

    [Fact]
    public void UsdSceneReader_Declares_FormatId_And_Extensions()
    {
        var reader = new UsdSceneReader();

        reader.FormatId.Should().Be("usd");
        reader.Extensions.Should().BeEquivalentTo([".usd", ".usda", ".usdc", ".usdz"]);
    }

    [Fact]
    public void UsdSceneWriter_Declares_FormatId()
    {
        new UsdSceneWriter().FormatId.Should().Be("usd");
    }

    [Fact]
    public async Task UsdSceneReader_Empty_Stream_Fails_Gracefully()
    {
        // The production reader spools the stream to disk and asks UsdStage.Open to parse
        // it; an empty buffer can't be parsed as USD, so the reader should surface the
        // failure (either via exception or null stage), and the loader should turn it into
        // a Fail result. Lock that contract here.
        var reader = new UsdSceneReader();
        using var ctx = MakeContext("scenes/empty.usda", []);

        var act = async () => await reader.ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>(
            "an empty stream is not a valid USD payload");
    }

    [Fact]
    public async Task UsdSceneLoader_Honours_Cancellation()
    {
        var loader = new UsdSceneLoader();
        using var ctx = MakeContext("scenes/empty.usda", []);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await loader.LoadAsync(ctx, cts.Token);

        // The loader's try/catch turns the OperationCanceledException into a Fail result.
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task UsdSceneWriter_Stub_Completes_Without_Error()
    {
        // Smoke-only: the production writer authors a real (but empty) stage when given
        // an empty Scene. Real round-trip coverage lives in UsdSceneRoundTripTests.
        var writer = new UsdSceneWriter();
        var scene = new Scene { Name = "out" };
        var path = Path.Combine(Path.GetTempPath(), $"3dengine-usd-empty-{Guid.NewGuid():N}.usda");

        try
        {
            await writer.WriteAsync(scene, path, SceneExportSettings.Default, CancellationToken.None);
            File.Exists(path).Should().BeTrue("the writer must produce the target file even for an empty scene");
            new FileInfo(path).Length.Should().BeGreaterThan(0);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task UsdSceneWriter_Rejects_Empty_TargetPath()
    {
        var writer = new UsdSceneWriter();
        var scene = new Scene();

        var act = () => writer.WriteAsync(scene, "  ", SceneExportSettings.Default, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// Builds an <see cref="AssetLoadContext"/> for tests. The internal constructor is visible
    /// here because Engine declares <c>[InternalsVisibleTo("Engine.Tests")]</c>.
    /// </summary>
    private static AssetLoadContext MakeContext(string path, byte[] bytes)
        => new(new MemoryStream(bytes), new AssetPath(path), _ => default);
}

