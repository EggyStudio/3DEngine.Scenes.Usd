using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Engine.Tests.Scenes.Usd;

/// <summary>
/// Verifies <c>UsdGeomSubset</c> with <c>familyName == "materialBind"</c> is materialized
/// into <see cref="SceneMeshSubset"/> entries with correct post-triangulation index ranges
/// and bound material paths.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Backend", "Usd")]
[Collection(UsdTestCollection.Name)]
public sealed class UsdSceneReaderSubsetTests
{
    private readonly ITestOutputHelper _output;
    private readonly bool _ready;

    public UsdSceneReaderSubsetTests(ITestOutputHelper output)
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
    public async Task Reader_Maps_GeomSubset_To_Disjoint_Index_Ranges()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        var fx = FixturePath("subset_cube.usda");
        if (!File.Exists(fx)) SkipTest.With($"fixture not staged at {fx}");

        var reader = new UsdSceneReader();
        using var ctx = OpenFixture("subset_cube.usda");
        var scene = await reader.ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);

        var mesh = scene.Traverse()
            .Select(n => n.GetComponent<SceneMeshPayload>())
            .First(m => m is not null)!;

        // Cube: 6 quads -> 12 triangles -> 36 indices.
        mesh.Indices.Length.Should().Be(36);
        mesh.Subsets.Should().HaveCountGreaterOrEqualTo(2,
            "two materialBind subsets cover faces [0,1,2] and [3,4,5]");

        var matA = mesh.Subsets.First(s => s.MaterialPath == "/World/Looks/A");
        var matB = mesh.Subsets.First(s => s.MaterialPath == "/World/Looks/B");

        // Each subset covers 3 quads -> 6 tris -> 18 indices, contiguous and disjoint.
        matA.IndexStart.Should().Be(0);
        matA.IndexCount.Should().Be(18);
        matB.IndexStart.Should().Be(18);
        matB.IndexCount.Should().Be(18);

        // Total covered indices match the buffer (no remainder for this fixture).
        var covered = mesh.Subsets.Sum(s => s.IndexCount);
        covered.Should().Be(mesh.Indices.Length);

        _output.WriteLine($"[subsets] {mesh.Subsets.Count} subset(s): " +
            string.Join(", ", mesh.Subsets.Select(s => $"{s.Name}=[{s.IndexStart}..{s.IndexStart+s.IndexCount})->{s.MaterialPath}")));
    }

    [Fact]
    public async Task Reader_Single_Material_Mesh_Has_Empty_Subsets()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        var fx = FixturePath("materialbound_cube.usda");
        if (!File.Exists(fx)) SkipTest.With($"fixture not staged at {fx}");

        var reader = new UsdSceneReader();
        using var ctx = OpenFixture("materialbound_cube.usda");
        var scene = await reader.ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);

        var mesh = scene.Traverse()
            .Select(n => n.GetComponent<SceneMeshPayload>())
            .First(m => m is not null)!;

        mesh.Subsets.Should().BeEmpty("mesh-level binding only - no UsdGeomSubsets authored");
    }

    [Fact]
    public async Task Reader_Subset_Materials_Are_Reachable_Via_Components()
    {
        // Subset-bound materials should be attached as Components on the mesh's SceneNode
        // (one per unique material path) so the spawner can resolve them without
        // re-walking the cache.
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        var fx = FixturePath("subset_cube.usda");
        if (!File.Exists(fx)) SkipTest.With($"fixture not staged at {fx}");

        var reader = new UsdSceneReader();
        using var ctx = OpenFixture("subset_cube.usda");
        var scene = await reader.ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);

        var meshNode = scene.Traverse()
            .First(n => n.GetComponent<SceneMeshPayload>() is not null);

        var materials = meshNode.Components.OfType<SceneMaterialPayload>().ToList();
        materials.Select(m => m.SourcePath).Should().Contain(new[] { "/World/Looks/A", "/World/Looks/B" });
    }
}

