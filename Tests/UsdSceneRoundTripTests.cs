using System.Numerics;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Engine.Tests.Scenes.Usd;

/// <summary>
/// Phase 7 (Plan §H-6): write → read → spawn round-trip coverage for
/// <see cref="UsdSceneWriter"/>. Each test programmatically builds a small <see cref="Scene"/>
/// (no fixture file required), feeds it through <c>UsdSceneWriter</c> into a temp
/// <c>.usda</c>, opens that file with <see cref="UsdSceneReader"/>, and asserts that the
/// payloads (transform, mesh topology, material, camera, light) survived intact.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Backend", "Usd")]
[Collection(UsdTestCollection.Name)]
public sealed class UsdSceneRoundTripTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly bool _ready;
    private readonly string _tempDir;

    public UsdSceneRoundTripTests(ITestOutputHelper output)
    {
        _output = output;
        _ready = UsdRuntimeLayout.IsAvailable();
        _tempDir = Path.Combine(Path.GetTempPath(), $"3dengine-usd-rt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private async Task<Scene> RoundTripAsync(Scene source, string fileName, SceneExportSettings? settings = null)
    {
        var path = Path.Combine(_tempDir, fileName);
        var writer = new UsdSceneWriter();
        await writer.WriteAsync(source, path, settings ?? SceneExportSettings.Default, CancellationToken.None);

        File.Exists(path).Should().BeTrue();
        new FileInfo(path).Length.Should().BeGreaterThan(0);

        // Reopen via the production reader path.
        var bytes = File.ReadAllBytes(path);
        using var ctx = new AssetLoadContext(new MemoryStream(bytes), new AssetPath($"tests/{fileName}"), _ => default);
        var reader = new UsdSceneReader();
        return await reader.ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);
    }

    [Fact]
    public async Task Writer_Then_Reader_Round_Trips_Mesh_With_Material_And_Transform()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");

        var material = new SceneMaterialPayload
        {
            Name = "Brick",
            SourcePath = "/Looks/Brick",
            BaseColorFactor = new Vector4(0.8f, 0.2f, 0.1f, 1f),
            MetallicFactor = 0.0f,
            RoughnessFactor = 0.7f,
            EmissiveFactor = new Vector3(0.05f, 0f, 0f),
            AlphaMode = SceneAlphaMode.Opaque,
            DoubleSided = true,
        };

        // A unit triangle so all topology slots are exercised but the assertions are tiny.
        var mesh = new SceneMeshPayload
        {
            Name = "Tri",
            Positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
            Indices = [0, 1, 2],
            Normals = [new(0, 0, 1), new(0, 0, 1), new(0, 0, 1)],
            Uv0 = [new(0, 0), new(1, 0), new(0, 1)],
        };

        var node = new SceneNode
        {
            Name = "Tri",
            LocalTransform = new Transform
            {
                Position = new Vector3(1, 2, 3),
                Rotation = Quaternion.Identity,
                Scale = new Vector3(2, 2, 2),
            },
            Components = { mesh, material },
        };

        var scene = new Scene { Name = "rt", Roots = { node } };

        var rt = await RoundTripAsync(scene, "mesh_material.usda");

        var rtMeshNode = rt.Traverse().First(n => n.GetComponent<SceneMeshPayload>() is not null);
        var rtMesh = rtMeshNode.GetComponent<SceneMeshPayload>()!;
        var rtMat = rtMeshNode.GetComponent<SceneMaterialPayload>();

        rtMesh.Positions.Should().HaveCount(3);
        rtMesh.Indices.Should().Equal(0, 1, 2);
        rtMesh.Uv0.Should().NotBeNull().And.HaveCount(3);
        rtMesh.Normals.Should().NotBeNull().And.HaveCount(3);

        rtMeshNode.LocalTransform.Position.Should().Be(new Vector3(1, 2, 3));
        rtMeshNode.LocalTransform.Scale.X.Should().BeApproximately(2f, 1e-4f);

        rtMat.Should().NotBeNull("the material binding should round-trip via UsdShadeMaterialBindingAPI");
        rtMat!.BaseColorFactor.X.Should().BeApproximately(0.8f, 1e-4f);
        rtMat.BaseColorFactor.Y.Should().BeApproximately(0.2f, 1e-4f);
        rtMat.BaseColorFactor.Z.Should().BeApproximately(0.1f, 1e-4f);
        rtMat.MetallicFactor.Should().BeApproximately(0.0f, 1e-4f);
        rtMat.RoughnessFactor.Should().BeApproximately(0.7f, 1e-4f);
        rtMat.EmissiveFactor.X.Should().BeApproximately(0.05f, 1e-4f);
        rtMat.AlphaMode.Should().Be(SceneAlphaMode.Opaque);
        rtMat.DoubleSided.Should().BeTrue("doubleSided rides on engine3d:* customData");

        _output.WriteLine($"[rt] vtx={rtMesh.Positions.Length} idx={rtMesh.Indices.Length} mat={rtMat.SourcePath}");
    }

    [Fact]
    public async Task Writer_Then_Reader_Round_Trips_Camera_Physical_Inputs()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");

        var cam = new SceneCameraPayload
        {
            Name = "Cam",
            Projection = SceneProjection.Perspective,
            HorizontalAperture = 36f,
            VerticalAperture = 24f,
            FocalLength = 50f,
            NearClip = 0.5f,
            FarClip = 250f,
            FocusDistance = 5f,
            FStop = 2.8f,
        };

        var node = new SceneNode
        {
            Name = "Cam",
            LocalTransform = new Transform
            {
                Position = new Vector3(0, 1.6f, 5f),
                Rotation = Quaternion.Identity,
                Scale = Vector3.One,
            },
            Components = { cam },
        };
        var scene = new Scene { Name = "rt-cam", Roots = { node } };

        var rt = await RoundTripAsync(scene, "camera.usda");

        var camNode = rt.Traverse().First(n => n.GetComponent<SceneCameraPayload>() is not null);
        var rtCam = camNode.GetComponent<SceneCameraPayload>()!;

        rtCam.Projection.Should().Be(SceneProjection.Perspective);
        rtCam.HorizontalAperture.Should().BeApproximately(36f, 1e-4f);
        rtCam.VerticalAperture.Should().BeApproximately(24f, 1e-4f);
        rtCam.FocalLength.Should().BeApproximately(50f, 1e-4f);
        rtCam.NearClip.Should().BeApproximately(0.5f, 1e-4f);
        rtCam.FarClip.Should().BeApproximately(250f, 1e-4f);
        rtCam.FocusDistance.Should().NotBeNull().And.BeApproximately(5f, 1e-4f);
        rtCam.FStop.Should().NotBeNull().And.BeApproximately(2.8f, 1e-4f);

        camNode.LocalTransform.Position.Y.Should().BeApproximately(1.6f, 1e-4f);
        camNode.LocalTransform.Position.Z.Should().BeApproximately(5f, 1e-4f);
    }

    [Fact]
    public async Task Writer_Then_Reader_Round_Trips_Distant_And_Sphere_Lights()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");

        var sun = new SceneNode
        {
            Name = "Sun",
            Components =
            {
                new SceneLightPayload
                {
                    Type = SceneLightType.Distant,
                    Color = new Vector3(1f, 0.95f, 0.9f),
                    Intensity = 3f,
                    Exposure = 0.5f,
                },
            },
        };
        var bulb = new SceneNode
        {
            Name = "Bulb",
            Components =
            {
                new SceneLightPayload
                {
                    Type = SceneLightType.Sphere,
                    Color = new Vector3(0.4f, 0.6f, 1f),
                    Intensity = 12f,
                    Radius = 0.25f,
                },
            },
        };
        var scene = new Scene { Name = "rt-lights", Roots = { sun, bulb } };

        var rt = await RoundTripAsync(scene, "lights.usda");

        var lights = rt.Traverse()
            .Select(n => n.GetComponent<SceneLightPayload>())
            .Where(l => l is not null).Select(l => l!).ToList();
        lights.Should().HaveCount(2);

        var rtSun = lights.First(l => l.Type == SceneLightType.Distant);
        rtSun.Color.X.Should().BeApproximately(1f, 1e-4f);
        rtSun.Color.Y.Should().BeApproximately(0.95f, 1e-4f);
        rtSun.Color.Z.Should().BeApproximately(0.9f, 1e-4f);
        rtSun.Intensity.Should().BeApproximately(3f, 1e-4f);
        rtSun.Exposure.Should().BeApproximately(0.5f, 1e-4f);
        rtSun.Radius.Should().BeNull("DistantLight has no radius");

        var rtBulb = lights.First(l => l.Type == SceneLightType.Sphere);
        rtBulb.Intensity.Should().BeApproximately(12f, 1e-4f);
        rtBulb.Radius.Should().NotBeNull().And.BeApproximately(0.25f, 1e-4f);
    }

    [Fact]
    public async Task Writer_Then_Reader_Preserves_Mesh_Subsets_With_Per_Subset_Materials()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");

        var matA = new SceneMaterialPayload { Name = "A", SourcePath = "/Looks/A", BaseColorFactor = new Vector4(1, 0, 0, 1) };
        var matB = new SceneMaterialPayload { Name = "B", SourcePath = "/Looks/B", BaseColorFactor = new Vector4(0, 1, 0, 1) };

        // Two-triangle mesh; subset A owns triangle 0, subset B owns triangle 1.
        var mesh = new SceneMeshPayload
        {
            Name = "Pair",
            Positions =
            [
                new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), // tri 0
                new(2, 0, 0), new(3, 0, 0), new(2, 1, 0), // tri 1
            ],
            Indices = [0, 1, 2, 3, 4, 5],
            Subsets = new[]
            {
                new SceneMeshSubset("A", IndexStart: 0, IndexCount: 3, MaterialPath: "/Looks/A"),
                new SceneMeshSubset("B", IndexStart: 3, IndexCount: 3, MaterialPath: "/Looks/B"),
            },
        };

        var node = new SceneNode
        {
            Name = "Pair",
            // Anchor materials in the scene so the writer's pre-pass discovers them.
            Components = { mesh, matA, matB },
        };
        var scene = new Scene { Name = "rt-subsets", Roots = { node } };

        var rt = await RoundTripAsync(scene, "subsets.usda");
        var rtMesh = rt.Traverse().Select(n => n.GetComponent<SceneMeshPayload>()).First(p => p is not null)!;

        rtMesh.Subsets.Should().HaveCount(2);
        rtMesh.Subsets.Sum(s => s.IndexCount).Should().Be(6);
        rtMesh.Subsets.Select(s => s.MaterialPath).Should().NotContain((string?)null,
            "subsets must round-trip with their material binding");
        rtMesh.Subsets.Select(s => s.MaterialPath!).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task Writer_Stage_Metadata_Round_Trips_UpAxis_And_MetersPerUnit()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");

        var scene = new Scene { Name = "rt-meta", Roots = { new SceneNode { Name = "Empty" } } };
        var settings = new SceneExportSettings
        {
            CoordinateSystem = SceneCoordinateSystem.ZUp,
            MetersPerUnit = 0.01,
        };

        var path = Path.Combine(_tempDir, "meta.usda");
        await new UsdSceneWriter().WriteAsync(scene, path, settings, CancellationToken.None);

        var ascii = File.ReadAllText(path);
        ascii.Should().Contain("upAxis").And.Contain("\"Z\"");
        ascii.Should().Contain("metersPerUnit");
    }
}