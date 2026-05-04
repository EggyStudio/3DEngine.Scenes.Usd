using System.Numerics;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Engine.Tests.Scenes.Usd;

/// <summary>
/// Cross-module integration test: a USD stage that embeds a MaterialX
/// <c>standard_surface</c> shader (info:id = <c>ND_standard_surface_surfaceshader</c>)
/// must round-trip through <see cref="UsdMaterialReader"/> via the MaterialX-in-USD
/// bridge and produce the same <see cref="SceneMaterialPayload"/> field shape the
/// pure-MTLX <c>MaterialXMaterialReader</c> emits for the analogous .mtlx fixture.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Backend", "Usd")]
[Trait("Bridge", "MaterialX")]
[Collection(UsdTestCollection.Name)]
public sealed class UsdSceneReaderMaterialXTests
{
    private readonly ITestOutputHelper _output;
    private readonly bool _ready;

    public UsdSceneReaderMaterialXTests(ITestOutputHelper output)
    {
        _output = output;
        _ready = UsdRuntimeLayout.IsAvailable();
    }

    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "source", "tests", "fixtures", name);

    private static AssetLoadContext OpenFixture(string name)
    {
        var bytes = File.ReadAllBytes(FixturePath(name));
        return new AssetLoadContext(new MemoryStream(bytes), new AssetPath($"tests/fixtures/{name}"), _ => default);
    }

    [Fact]
    public async Task UsdReader_Bridges_To_MaterialX_StandardSurface_When_Embedded_In_UsdShade()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        var fx = FixturePath("materialx_embedded_cube.usda");
        if (!File.Exists(fx)) SkipTest.With($"fixture not staged at {fx}");

        var reader = new UsdSceneReader();
        using var ctx = OpenFixture("materialx_embedded_cube.usda");
        var scene = await reader.ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);

        var mat = scene.Traverse()
            .Select(n => n.GetComponent<SceneMaterialPayload>())
            .FirstOrDefault(m => m is not null);
        mat.Should().NotBeNull("the MaterialX-in-USD bridge must produce a SceneMaterialPayload for ND_standard_surface_surfaceshader");

        _output.WriteLine($"[mtlx-bridge] base={mat!.BaseColorFactor} metallic={mat.MetallicFactor} roughness={mat.RoughnessFactor} emissive={mat.EmissiveFactor}");

        mat.SourcePath.Should().Be("/World/Looks/MtlxMat");
        mat.BaseColorFactor.Should().Be(new Vector4(0f, 0.25f, 1f, 1f));
        mat.MetallicFactor.Should().BeApproximately(0.4f, 1e-5f);
        mat.RoughnessFactor.Should().BeApproximately(0.6f, 1e-5f);
        mat.EmissiveFactor.Should().BeEquivalentTo(new Vector3(0.1f, 0.2f, 0.3f),
            o => o.Using<float>(c => c.Subject.Should().BeApproximately(c.Expectation, 1e-5f)).WhenTypeIs<float>());
    }
}

