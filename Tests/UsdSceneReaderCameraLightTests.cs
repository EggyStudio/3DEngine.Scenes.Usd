using System.Numerics;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Engine.Tests.Scenes.Usd;

/// <summary>
/// End-to-end coverage for the production camera + UsdLux light readers in
/// <see cref="UsdSceneReader"/>. Loads the small <c>two_lights_one_camera.usda</c> fixture
/// (one perspective Camera, one DistantLight, one SphereLight) and asserts the resulting
/// payload values, FOV derivation, and the inert-light spawn contract documented on
/// <see cref="SceneSpawner"/> (Phase 6: lights ride along on <see cref="SceneNode.Components"/>
/// but the runtime has no Light component yet, so the spawner only emits Transform +
/// <see cref="SceneInstance"/>).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Backend", "Usd")]
[Collection(UsdTestCollection.Name)]
public sealed class UsdSceneReaderCameraLightTests
{
    private readonly ITestOutputHelper _output;
    private readonly bool _ready;

    public UsdSceneReaderCameraLightTests(ITestOutputHelper output)
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
    public async Task Reader_Reads_Perspective_Camera_With_Physical_Inputs_And_FovY()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        var fx = FixturePath("two_lights_one_camera.usda");
        if (!File.Exists(fx)) SkipTest.With($"fixture not staged at {fx}");

        var reader = new UsdSceneReader();
        using var ctx = OpenFixture("two_lights_one_camera.usda");
        var scene = await reader.ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);

        var camNode = scene.Traverse().First(n => n.GetComponent<SceneCameraPayload>() is not null);
        var cam = camNode.GetComponent<SceneCameraPayload>()!;

        cam.Projection.Should().Be(SceneProjection.Perspective);
        cam.HorizontalAperture.Should().BeApproximately(36f, 1e-4f);
        cam.VerticalAperture.Should().BeApproximately(24f, 1e-4f);
        cam.FocalLength.Should().BeApproximately(50f, 1e-4f);
        cam.NearClip.Should().BeApproximately(0.5f, 1e-4f);
        cam.FarClip.Should().BeApproximately(250f, 1e-4f);
        cam.FocusDistance.Should().NotBeNull().And.BeApproximately(5f, 1e-4f);
        cam.FStop.Should().NotBeNull().And.BeApproximately(2.8f, 1e-4f);

        // VerticalFovRadians: 2 * atan(verticalAperture / (2 * focalLength)).
        var expectedFov = 2f * MathF.Atan(24f / (2f * 50f));
        cam.VerticalFovRadians.Should().BeApproximately(expectedFov, 1e-5f);

        // Local transform was decomposed; the camera should sit at (0, 1.6, 5).
        camNode.LocalTransform.Position.X.Should().BeApproximately(0f, 1e-4f);
        camNode.LocalTransform.Position.Y.Should().BeApproximately(1.6f, 1e-4f);
        camNode.LocalTransform.Position.Z.Should().BeApproximately(5f, 1e-4f);
    }

    [Fact]
    public async Task Reader_Reads_DistantLight_And_SphereLight_With_Type_Specific_Params()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        var fx = FixturePath("two_lights_one_camera.usda");
        if (!File.Exists(fx)) SkipTest.With($"fixture not staged at {fx}");

        var reader = new UsdSceneReader();
        using var ctx = OpenFixture("two_lights_one_camera.usda");
        var scene = await reader.ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);

        var lights = scene.Traverse()
            .Select(n => n.GetComponent<SceneLightPayload>())
            .Where(l => l is not null)
            .Select(l => l!)
            .ToList();

        lights.Should().HaveCount(2);

        var sun = lights.First(l => l.Type == SceneLightType.Distant);
        sun.Color.X.Should().BeApproximately(1f, 1e-4f);
        sun.Color.Y.Should().BeApproximately(0.95f, 1e-4f);
        sun.Color.Z.Should().BeApproximately(0.9f, 1e-4f);
        sun.Intensity.Should().BeApproximately(3f, 1e-4f);
        sun.Exposure.Should().BeApproximately(0.5f, 1e-4f);
        sun.Radius.Should().BeNull("DistantLight has no radius");

        var bulb = lights.First(l => l.Type == SceneLightType.Sphere);
        bulb.Color.X.Should().BeApproximately(0.4f, 1e-4f);
        bulb.Color.Y.Should().BeApproximately(0.6f, 1e-4f);
        bulb.Color.Z.Should().BeApproximately(1f, 1e-4f);
        bulb.Intensity.Should().BeApproximately(12f, 1e-4f);
        bulb.Radius.Should().NotBeNull().And.BeApproximately(0.25f, 1e-4f);

        _output.WriteLine($"[lights] DistantLight color={sun.Color}, intensity={sun.Intensity}, exposure={sun.Exposure}");
        _output.WriteLine($"[lights] SphereLight color={bulb.Color}, intensity={bulb.Intensity}, radius={bulb.Radius}");
    }

    [Fact]
    public async Task Reader_Skips_Cameras_When_Camera_Flag_Cleared()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        var fx = FixturePath("two_lights_one_camera.usda");
        if (!File.Exists(fx)) SkipTest.With($"fixture not staged at {fx}");

        var reader = new UsdSceneReader();
        using var ctx = OpenFixture("two_lights_one_camera.usda");
        var settings = new SceneImportSettings
        {
            // Drop only Cameras; lights and meshes still come through.
            LoadPayloads = LoadPayloads.All & ~LoadPayloads.Cameras,
        };

        var scene = await reader.ReadAsync(ctx, settings, CancellationToken.None);

        scene.Traverse().Any(n => n.GetComponent<SceneCameraPayload>() is not null)
            .Should().BeFalse();
        scene.Traverse().Any(n => n.GetComponent<SceneLightPayload>() is not null)
            .Should().BeTrue("light payloads remain when only Cameras is masked off");
    }

    [Fact]
    public async Task Reader_Skips_Lights_When_Light_Flag_Cleared()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        var fx = FixturePath("two_lights_one_camera.usda");
        if (!File.Exists(fx)) SkipTest.With($"fixture not staged at {fx}");

        var reader = new UsdSceneReader();
        using var ctx = OpenFixture("two_lights_one_camera.usda");
        var settings = new SceneImportSettings
        {
            LoadPayloads = LoadPayloads.All & ~LoadPayloads.Lights,
        };

        var scene = await reader.ReadAsync(ctx, settings, CancellationToken.None);

        scene.Traverse().Any(n => n.GetComponent<SceneLightPayload>() is not null)
            .Should().BeFalse();
        scene.Traverse().Any(n => n.GetComponent<SceneCameraPayload>() is not null)
            .Should().BeTrue("camera payloads remain when only Lights is masked off");
    }

    [Fact]
    public async Task Spawner_End_To_End_Camera_Becomes_Camera_Component_Lights_Are_Inert()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        var fx = FixturePath("two_lights_one_camera.usda");
        if (!File.Exists(fx)) SkipTest.With($"fixture not staged at {fx}");

        var reader = new UsdSceneReader();
        using var ctx = OpenFixture("two_lights_one_camera.usda");
        var scene = await reader.ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);

        var ecs = new EcsWorld();
        var spawned = SceneSpawner.Spawn(ecs, scene);

        // Camera + 2 lights = 3 spawned entities (each carries Transform + SceneInstance;
        // lights have no runtime component yet, but they still get an entity for editor
        // pickability and the world-transform plumbing).
        spawned.Should().HaveCount(3);

        int cameraComponents = spawned.Count(e => ecs.Has<Camera>(e));
        cameraComponents.Should().Be(1);

        int meshComponents = spawned.Count(e => ecs.Has<Mesh>(e));
        meshComponents.Should().Be(0, "fixture has no meshes");

        // Lights are inert: each light entity has only Transform + SceneInstance.
        var lightEntities = spawned.Where(e => !ecs.Has<Camera>(e)).ToList();
        lightEntities.Should().HaveCount(2);
        foreach (var e in lightEntities)
        {
            ecs.Has<Transform>(e).Should().BeTrue();
            ecs.Has<SceneInstance>(e).Should().BeTrue();
            ecs.Has<Material>(e).Should().BeFalse();
        }
    }
}

