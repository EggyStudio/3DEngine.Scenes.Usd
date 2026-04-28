using FluentAssertions;
using pxr;
using UniversalSceneDescription;
using Xunit;

namespace Engine.Tests.Scenes.Usd;

/// <summary>
/// End-to-end smoke test that authors a real USD stage via the bundled
/// <c>UniversalSceneDescription</c> bindings, mirroring the quick-start example from
/// the package readme. Verifies the native runtime, the schema registration (UsdGeomXform,
/// UsdGeomSphere), the <c>.usda</c> file format plugin, and round-trip via reopening
/// the saved file - all the way down to the Pixar plug-in tree.
/// </summary>
/// <remarks>
/// This test is the canary for the OpenUSD native deployment: if it passes, the runtime
/// libraries shipped by the NuGet package are usable on the current OS / RID. If it fails
/// with a missing-symbol or plugInfo lookup error, that's the signal that
/// <c>runtimes/&lt;rid&gt;/native/...</c> needs attention in the build output.
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Backend", "Usd")]
public class UsdAuthoringSmokeTests : IDisposable
{
    private readonly string _tempDir;

    public UsdAuthoringSmokeTests()
    {
        UsdRuntime.Initialize();
        _tempDir = Path.Combine(Path.GetTempPath(), $"3dengine-usd-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Quickstart_Authors_And_Saves_Stage_To_Disk()
    {
        // Mirrors the package's quick-start example verbatim:
        //
        //     using var stage = UsdStage.CreateNew("hello.usda");
        //     UsdGeomXform.Define(stage,  new SdfPath("/Hello"));
        //     UsdGeomSphere.Define(stage, new SdfPath("/Hello/World"));
        //     stage.Save();
        var path = Path.Combine(_tempDir, "hello.usda");

        using (var stage = UsdStage.CreateNew(path))
        {
            UsdGeomXform.Define(stage, new SdfPath("/Hello"));
            UsdGeomSphere.Define(stage, new SdfPath("/Hello/World"));
            stage.Save();
        }

        File.Exists(path).Should().BeTrue();
        new FileInfo(path).Length.Should().BeGreaterThan(0);

        // .usda is the ASCII format; quick header sanity check confirms the file format
        // plugin actually serialized USD content (not, e.g., an empty stub).
        var head = File.ReadAllText(path);
        head.Should().StartWith("#usda", "the .usda crate format always starts with a #usda header");
    }

    [Fact]
    public void Authored_Stage_Round_Trips_Through_Reopen()
    {
        var path = Path.Combine(_tempDir, "roundtrip.usda");

        using (var stage = UsdStage.CreateNew(path))
        {
            UsdGeomXform.Define(stage, new SdfPath("/Hello"));
            UsdGeomSphere.Define(stage, new SdfPath("/Hello/World"));
            stage.Save();
        }

        // Reopen and confirm the prim hierarchy survived the save/load cycle.
        using var reopened = UsdStage.Open(path);
        reopened.Should().NotBeNull();

        var hello = reopened.GetPrimAtPath(new SdfPath("/Hello"));
        var world = reopened.GetPrimAtPath(new SdfPath("/Hello/World"));

        hello.IsValid().Should().BeTrue("'/Hello' should round-trip");
        world.IsValid().Should().BeTrue("'/Hello/World' should round-trip");
    }
}

