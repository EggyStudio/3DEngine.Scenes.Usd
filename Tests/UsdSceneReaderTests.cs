using System.Numerics;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Engine.Tests.Scenes.Usd;

/// <summary>
/// End-to-end tests for the production <see cref="UsdSceneReader"/> against the bundled
/// <c>teapot.usdz</c> fixture. Verifies the full path: spool stream to disk → open stage
/// → walk prim tree → emit <see cref="Scene"/> with payloads. Distinct from
/// <c>UsdReaderSpikeTests</c> (which exercises the raw binding surface) and
/// <c>UsdSceneLoaderTests</c> (which pins type-level contracts only).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Backend", "Usd")]
[Collection(UsdTestCollection.Name)]
public sealed class UsdSceneReaderTests
{
    private readonly ITestOutputHelper _output;
    private readonly bool _ready;
    private readonly string? _teapotPath;
    private readonly byte[]? _teapotBytes;

    public UsdSceneReaderTests(ITestOutputHelper output)
    {
        _output = output;
        _ready = UsdRuntimeLayout.IsAvailable();

        var candidate = Path.Combine(AppContext.BaseDirectory, "source", "teapot.usdz");
        _teapotPath = File.Exists(candidate) ? candidate : null;
        _teapotBytes = _teapotPath is not null ? File.ReadAllBytes(_teapotPath) : null;
    }

    [Fact]
    public async Task Reader_Walks_Teapot_Hierarchy_Into_SceneNodes()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        if (_teapotBytes is null) SkipTest.With("teapot.usdz not staged next to test binary.");

        var reader = new UsdSceneReader();
        using var ctx = new AssetLoadContext(new MemoryStream(_teapotBytes), new AssetPath("scenes/teapot.usdz"), _ => default);

        var scene = await reader.ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);

        scene.Should().NotBeNull();
        scene.Name.Should().Be("teapot");
        scene.Roots.Should().NotBeEmpty("teapot.usdz has at least one root prim");

        var allNodes = scene.Traverse().ToList();
        allNodes.Should().NotBeEmpty();
        _output.WriteLine($"[reader] teapot.usdz produced {allNodes.Count} SceneNode(s) under {scene.Roots.Count} root(s).");

        // Every node should have a stable source path that begins with a slash.
        allNodes.Should().OnlyContain(n => n.SourcePath.StartsWith("/"));
    }

    [Fact]
    public async Task Reader_Records_Stage_Metadata_Without_Renormalizing()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        if (_teapotBytes is null) SkipTest.With("teapot.usdz not staged.");

        var reader = new UsdSceneReader();
        using var ctx = new AssetLoadContext(new MemoryStream(_teapotBytes), new AssetPath("scenes/teapot.usdz"), _ => default);

        var scene = await reader.ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);

        // Per Plan §B / Scene policy, the reader records authored basis & units verbatim
        // rather than normalizing in place. Both must be plausible (positive, known axes).
        scene.SourceMetersPerUnit.Should().BeGreaterThan(0);
        scene.SourceCoordinateSystem.Should().BeOneOf(SceneCoordinateSystem.YUp, SceneCoordinateSystem.ZUp);
        _output.WriteLine($"[reader] teapot upAxis={scene.SourceCoordinateSystem}, metersPerUnit={scene.SourceMetersPerUnit}");
    }

    [Fact]
    public async Task Reader_Triangulates_Mesh_Payloads()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        if (_teapotBytes is null) SkipTest.With("teapot.usdz not staged.");

        var reader = new UsdSceneReader();
        using var ctx = new AssetLoadContext(new MemoryStream(_teapotBytes), new AssetPath("scenes/teapot.usdz"), _ => default);
        var scene = await reader.ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);

        var meshes = scene.Traverse()
            .Select(n => n.GetComponent<SceneMeshPayload>())
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();

        meshes.Should().NotBeEmpty("teapot.usdz contains at least one Mesh prim");
        foreach (var m in meshes)
        {
            m.Positions.Length.Should().BeGreaterThan(0);
            m.Indices.Length.Should().BeGreaterThan(0);
            (m.Indices.Length % 3).Should().Be(0, $"mesh '{m.Name}' index buffer must be triangulated");
            m.LocalBounds.IsValid.Should().BeTrue($"mesh '{m.Name}' bounds should be computed");
            // All indices must be in range.
            for (int i = 0; i < m.Indices.Length; i++)
                m.Indices[i].Should().BeInRange(0, m.Positions.Length - 1, $"mesh '{m.Name}' index out of range");
        }
        _output.WriteLine($"[reader] {meshes.Count} mesh payload(s) extracted; total triangles = {meshes.Sum(m => m.Indices.Length / 3)}.");
    }

    [Fact]
    public async Task Reader_Skips_Mesh_Payloads_When_Mesh_Flag_Cleared()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        if (_teapotBytes is null) SkipTest.With("teapot.usdz not staged.");

        var reader = new UsdSceneReader();
        using var ctx = new AssetLoadContext(new MemoryStream(_teapotBytes), new AssetPath("scenes/teapot.usdz"), _ => default);
        var settings = new SceneImportSettings { LoadPayloads = LoadPayloads.None };

        var scene = await reader.ReadAsync(ctx, settings, CancellationToken.None);

        scene.Roots.Should().NotBeEmpty("hierarchy is still emitted");
        scene.Traverse().Any(n => n.GetComponent<SceneMeshPayload>() is not null)
            .Should().BeFalse("LoadPayloads.None must skip mesh payloads");
    }

    [Fact]
    public async Task Reader_Skips_Materials_When_Resolution_Is_None()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        if (_teapotBytes is null) SkipTest.With("teapot.usdz not staged.");

        var reader = new UsdSceneReader();
        using var ctx = new AssetLoadContext(new MemoryStream(_teapotBytes), new AssetPath("scenes/teapot.usdz"), _ => default);
        var settings = new SceneImportSettings { MaterialResolution = MaterialNetworkResolution.None };

        var scene = await reader.ReadAsync(ctx, settings, CancellationToken.None);

        scene.Traverse().Any(n => n.GetComponent<SceneMaterialPayload>() is not null)
            .Should().BeFalse("MaterialNetworkResolution.None must skip material payloads");
    }

    [Fact]
    public async Task Reader_Cancellation_Throws_OperationCanceled()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        if (_teapotBytes is null) SkipTest.With("teapot.usdz not staged.");

        var reader = new UsdSceneReader();
        using var ctx = new AssetLoadContext(new MemoryStream(_teapotBytes), new AssetPath("scenes/teapot.usdz"), _ => default);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await reader.ReadAsync(ctx, SceneImportSettings.Default, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Reader_Material_Cache_Dedupes_Across_Mesh_Subsets()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        if (_teapotBytes is null) SkipTest.With("teapot.usdz not staged.");

        var reader = new UsdSceneReader();
        using var ctx = new AssetLoadContext(new MemoryStream(_teapotBytes), new AssetPath("scenes/teapot.usdz"), _ => default);
        var scene = await reader.ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);

        var materials = scene.Traverse()
            .Select(n => n.GetComponent<SceneMaterialPayload>())
            .Where(m => m is not null)
            .ToList();

        // If the cache works, payloads bound to the same SourcePath are reference-equal
        // (the reader caches by prim-path - cf. SceneMaterialPayload remarks).
        var byPath = materials.GroupBy(m => m!.SourcePath);
        foreach (var group in byPath)
        {
            var distinct = group.Distinct().Count();
            distinct.Should().Be(1, $"material '{group.Key}' must be a single shared payload (cache dedupe)");
        }
        _output.WriteLine($"[reader] {byPath.Count()} unique material(s) across {materials.Count} binding(s).");
    }
}

