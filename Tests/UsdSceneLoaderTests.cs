using FluentAssertions;
using Xunit;

namespace Engine.Tests.Scenes.Usd;

/// <summary>
/// Direct tests for <see cref="UsdSceneLoader"/>, <see cref="UsdSceneReader"/>, and
/// <see cref="UsdSceneWriter"/> that don't require running through the asset pipeline.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Backend", "Usd")]
public class UsdSceneLoaderTests
{
    [Fact]
    public void UsdSceneLoader_Declares_All_Three_Usd_Extensions()
    {
        var loader = new UsdSceneLoader();

        loader.Extensions.Should().BeEquivalentTo([".usd", ".usda", ".usdc"]);
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
    public async Task UsdSceneReader_Stub_Returns_Empty_Scene_With_Source_Metadata()
    {
        // Stub status: until prim traversal is implemented, the reader hands back an empty
        // Scene whose Name reflects the source path. Lock that contract so the asset
        // pipeline plumbing (Handle<SceneAsset>, AssetEvent<SceneAsset>) keeps working.
        var reader = new UsdSceneReader();
        using var ctx = MakeContext("scenes/empty.usda", []);

        var scene = await reader.ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);

        scene.Should().NotBeNull();
        scene.Roots.Should().BeEmpty();
        scene.SourceCoordinateSystem.Should().Be(SceneCoordinateSystem.YUp);
        scene.SourceMetersPerUnit.Should().Be(1.0);
        scene.Name.Should().Contain("empty.usda");
    }

    [Fact]
    public async Task UsdSceneLoader_Wraps_Reader_Output_Into_SceneAsset()
    {
        var loader = new UsdSceneLoader();
        using var ctx = MakeContext("scenes/empty.usda", []);

        var result = await loader.LoadAsync(ctx, CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.Asset.Should().NotBeNull();
        result.Asset!.SourceFormat.Should().Be("usd");
        result.Asset.SourcePath.Should().Be("scenes/empty.usda");
        result.Asset.Scene.Should().NotBeNull();
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
        var writer = new UsdSceneWriter();
        var scene = new Scene { Name = "out" };

        await writer.WriteAsync(scene, "/tmp/out.usda", SceneExportSettings.Default, CancellationToken.None);
        // No assertion - the contract is just "doesn't throw" until authoring is implemented.
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

