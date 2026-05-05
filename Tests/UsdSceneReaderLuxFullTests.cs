using System.Numerics;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Engine.Tests.Scenes.Usd;

/// <summary>
/// Coverage for the extended UsdLux schema family: <c>UsdLuxShadowAPI</c>,
/// <c>UsdLuxShapingAPI</c>, the <c>RectLight</c>/<c>DomeLight</c> texture inputs, and the
/// previously-unsupported shape types (<c>GeometryLight</c>, <c>PortalLight</c>). Loads
/// <c>lux_full.usda</c> and asserts that each input round-trips into the
/// <see cref="SceneLightPayload"/> nested records / new fields.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Backend", "Usd")]
[Collection(UsdTestCollection.Name)]
public sealed class UsdSceneReaderLuxFullTests
{
    private readonly ITestOutputHelper _output;
    private readonly bool _ready;

    public UsdSceneReaderLuxFullTests(ITestOutputHelper output)
    {
        _output = output;
        _ready = UsdRuntimeLayout.IsAvailable();
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "source", "tests", "fixtures", name);

    private static AssetLoadContext OpenFixture(string name)
    {
        var bytes = File.ReadAllBytes(FixturePath(name));
        return new AssetLoadContext(new MemoryStream(bytes), new AssetPath($"tests/fixtures/{name}"), _ => default);
    }

    private async Task<Scene> ReadFixtureAsync()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        var fx = FixturePath("lux_full.usda");
        if (!File.Exists(fx)) SkipTest.With($"fixture not staged at {fx}");

        using var ctx = OpenFixture("lux_full.usda");
        return await new UsdSceneReader().ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);
    }

    [Fact]
    public async Task SphereLight_With_ShadowAPI_And_ShapingAPI_Populates_Nested_Records()
    {
        var scene = await ReadFixtureAsync();

        var spot = scene.Traverse()
            .Select(n => n.GetComponent<SceneLightPayload>())
            .First(l => l is not null && l.Name == "Spot")!;

        // Common LightAPI extras.
        spot.Type.Should().Be(SceneLightType.Sphere);
        spot.Normalize.Should().BeTrue();
        spot.Diffuse.Should().NotBeNull().And.BeApproximately(1f, 1e-4f);
        spot.Specular.Should().NotBeNull().And.BeApproximately(0.7f, 1e-4f);
        spot.ColorTemperature.Should().NotBeNull().And.BeApproximately(4500f, 1e-4f);
        spot.EnableColorTemperature.Should().BeTrue();
        spot.Radius.Should().NotBeNull().And.BeApproximately(0.1f, 1e-4f);

        // ShadowAPI
        spot.Shadow.Should().NotBeNull();
        spot.Shadow!.Enable.Should().BeTrue();
        spot.Shadow.Color.Should().NotBeNull();
        spot.Shadow.Color!.Value.Should().Be(new Vector3(0.05f, 0.05f, 0.1f));
        spot.Shadow.Distance.Should().NotBeNull().And.BeApproximately(50f, 1e-4f);
        spot.Shadow.Falloff.Should().NotBeNull().And.BeApproximately(2f, 1e-4f);
        spot.Shadow.FalloffGamma.Should().NotBeNull().And.BeApproximately(1.5f, 1e-4f);

        // ShapingAPI
        spot.Shaping.Should().NotBeNull();
        spot.Shaping!.ConeAngle.Should().NotBeNull().And.BeApproximately(22.5f, 1e-4f);
        spot.Shaping.ConeSoftness.Should().NotBeNull().And.BeApproximately(0.25f, 1e-4f);
        spot.Shaping.FocusPower.Should().NotBeNull().And.BeApproximately(4f, 1e-4f);
        spot.Shaping.IesProfilePath.Should().EndWith("spot.ies");
        spot.Shaping.IesAngleScale.Should().NotBeNull().And.BeApproximately(1.1f, 1e-4f);
        spot.Shaping.IesNormalize.Should().BeTrue();

        // Convenience shortcuts mirror the nested record.
        spot.ConeAngle.Should().Be(spot.Shaping.ConeAngle);
        spot.ConeSoftness.Should().Be(spot.Shaping.ConeSoftness);
        spot.IesProfilePath.Should().Be(spot.Shaping.IesProfilePath);

        _output.WriteLine($"[Spot] Shadow={spot.Shadow}, Shaping={spot.Shaping}");
    }

    [Fact]
    public async Task RectLight_Surfaces_TextureFile()
    {
        var scene = await ReadFixtureAsync();
        var rect = scene.Traverse().Select(n => n.GetComponent<SceneLightPayload>())
            .First(l => l is not null && l.Type == SceneLightType.Rect)!;

        rect.Width.Should().NotBeNull().And.BeApproximately(2.5f, 1e-4f);
        rect.Height.Should().NotBeNull().And.BeApproximately(1.0f, 1e-4f);
        rect.RectTexturePath.Should().NotBeNull().And.EndWith("softbox.exr");
    }

    [Fact]
    public async Task GeometryLight_Surfaces_GeometryRel_Targets()
    {
        var scene = await ReadFixtureAsync();
        var geo = scene.Traverse().Select(n => n.GetComponent<SceneLightPayload>())
            .First(l => l is not null && l.Type == SceneLightType.Geometry)!;

        geo.GeometryPaths.Should().ContainSingle().Which.Should().Be("/World/Bulb");
    }

    [Fact]
    public async Task DomeLight_Surfaces_Format_GuideRadius_And_Portals()
    {
        var scene = await ReadFixtureAsync();
        var dome = scene.Traverse().Select(n => n.GetComponent<SceneLightPayload>())
            .First(l => l is not null && l.Type == SceneLightType.Dome)!;

        dome.DomeTexturePath.Should().NotBeNull().And.EndWith("sky.exr");
        dome.DomeTextureFormat.Should().Be("latlong");
        dome.DomeGuideRadius.Should().NotBeNull().And.BeApproximately(100f, 1e-4f);
        dome.PortalPaths.Should().ContainSingle().Which.Should().Be("/World/Window");
    }

    [Fact]
    public async Task PortalLight_Recovers_Width_Height_From_Extent()
    {
        var scene = await ReadFixtureAsync();
        var portal = scene.Traverse().Select(n => n.GetComponent<SceneLightPayload>())
            .First(l => l is not null && l.Type == SceneLightType.Portal)!;

        portal.Width.Should().NotBeNull().And.BeApproximately(2f, 1e-4f);   // X span (-1..1)
        portal.Height.Should().NotBeNull().And.BeApproximately(4f, 1e-4f);  // Y span (-2..2)
    }

    [Fact]
    public async Task RoundTrip_Preserves_Shadow_And_Shaping_Through_Writer()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");

        // Build a programmatic scene carrying a SphereLight with full ShadowAPI / ShapingAPI.
        var lightNode = new SceneNode { Name = "Spot" };
        lightNode.Components.Add(new SceneLightPayload
        {
            Name = "Spot",
            Type = SceneLightType.Sphere,
            Color = new Vector3(1f, 0.6f, 0.2f),
            Intensity = 18f,
            Exposure = 0.25f,
            Radius = 0.15f,
            Normalize = true,
            ColorTemperature = 3200f,
            EnableColorTemperature = true,
            Shadow = new SceneLightShadow(
                Enable: true,
                Color: new Vector3(0.02f, 0.02f, 0.04f),
                Distance: 75f,
                Falloff: 1.5f,
                FalloffGamma: 2f),
            Shaping = new SceneLightShaping(
                ConeAngle: 30f,
                ConeSoftness: 0.5f,
                FocusPower: 2f,
                IesNormalize: true),
        });

        var src = new Scene { Name = "rt" };
        src.Roots.Add(new SceneNode { Name = "World", Children = { lightNode } });

        var dir = Path.Combine(Path.GetTempPath(), $"3dengine-usd-rtlux-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "rt.usda");
            await new UsdSceneWriter().WriteAsync(src, path, SceneExportSettings.Default, CancellationToken.None);

            var bytes = File.ReadAllBytes(path);
            using var ctx = new AssetLoadContext(new MemoryStream(bytes), new AssetPath("tests/rt.usda"), _ => default);
            var rt = await new UsdSceneReader().ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);

            var l = rt.Traverse().Select(n => n.GetComponent<SceneLightPayload>())
                .First(p => p is not null)!;

            l.Type.Should().Be(SceneLightType.Sphere);
            l.Radius.Should().NotBeNull().And.BeApproximately(0.15f, 1e-4f);
            l.Normalize.Should().BeTrue();
            l.ColorTemperature.Should().NotBeNull().And.BeApproximately(3200f, 1e-4f);
            l.EnableColorTemperature.Should().BeTrue();

            l.Shadow.Should().NotBeNull();
            l.Shadow!.Enable.Should().BeTrue();
            l.Shadow.Distance.Should().NotBeNull().And.BeApproximately(75f, 1e-4f);
            l.Shadow.Falloff.Should().NotBeNull().And.BeApproximately(1.5f, 1e-4f);
            l.Shadow.FalloffGamma.Should().NotBeNull().And.BeApproximately(2f, 1e-4f);

            l.Shaping.Should().NotBeNull();
            l.Shaping!.ConeAngle.Should().NotBeNull().And.BeApproximately(30f, 1e-4f);
            l.Shaping.ConeSoftness.Should().NotBeNull().And.BeApproximately(0.5f, 1e-4f);
            l.Shaping.FocusPower.Should().NotBeNull().And.BeApproximately(2f, 1e-4f);
            l.Shaping.IesNormalize.Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task RoundTrip_Preserves_Geometry_Portal_And_Filter_Rels()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");

        // Author /World with a GeometryLight binding /World/Bulb, a DomeLight binding
        // /World/Window via portals, and the GeometryLight referencing a LightFilter via
        // light:filters.
        var bulb = new SceneNode { Name = "Bulb" };
        var window = new SceneNode { Name = "Window" };
        window.Components.Add(new SceneLightPayload { Name = "Window", Type = SceneLightType.Portal, Width = 2f, Height = 4f });

        var geo = new SceneNode { Name = "GeoLit" };
        geo.Components.Add(new SceneLightPayload
        {
            Name = "GeoLit",
            Type = SceneLightType.Geometry,
            Intensity = 5f,
            GeometryPaths = new[] { "/World/Bulb" },
            FilterPaths   = new[] { "/World/Filt" },
        });

        var dome = new SceneNode { Name = "Sky" };
        dome.Components.Add(new SceneLightPayload
        {
            Name = "Sky",
            Type = SceneLightType.Dome,
            DomeTextureFormat = "latlong",
            DomeGuideRadius = 50f,
            PortalPaths = new[] { "/World/Window" },
        });

        var src = new Scene { Name = "rt-rels" };
        src.Roots.Add(new SceneNode { Name = "World", Children = { bulb, window, geo, dome } });

        var dir = Path.Combine(Path.GetTempPath(), $"3dengine-usd-rtrels-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "rt.usda");
            await new UsdSceneWriter().WriteAsync(src, path, SceneExportSettings.Default, CancellationToken.None);

            var bytes = File.ReadAllBytes(path);
            using var ctx = new AssetLoadContext(new MemoryStream(bytes), new AssetPath("tests/rt.usda"), _ => default);
            var rt = await new UsdSceneReader().ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);

            var rtGeo = rt.Traverse().Select(n => n.GetComponent<SceneLightPayload>())
                .First(p => p is not null && p.Type == SceneLightType.Geometry)!;
            rtGeo.GeometryPaths.Should().ContainSingle().Which.Should().Be("/World/Bulb");
            rtGeo.FilterPaths.Should().ContainSingle().Which.Should().Be("/World/Filt");

            var rtDome = rt.Traverse().Select(n => n.GetComponent<SceneLightPayload>())
                .First(p => p is not null && p.Type == SceneLightType.Dome)!;
            rtDome.PortalPaths.Should().ContainSingle().Which.Should().Be("/World/Window");
            rtDome.DomeTextureFormat.Should().Be("latlong");
            rtDome.DomeGuideRadius.Should().NotBeNull().And.BeApproximately(50f, 1e-4f);

            var rtPortal = rt.Traverse().Select(n => n.GetComponent<SceneLightPayload>())
                .First(p => p is not null && p.Type == SceneLightType.Portal)!;
            rtPortal.Width.Should().NotBeNull().And.BeApproximately(2f, 1e-4f);
            rtPortal.Height.Should().NotBeNull().And.BeApproximately(4f, 1e-4f);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}