using FluentAssertions;
using Xunit;

namespace Engine.Tests.Scenes.Usd;

/// <summary>
/// Plugin wiring tests for <see cref="UsdScenesPlugin"/>: ensures the OpenUSD backend
/// inserts its marker resource, registers itself with the <see cref="SceneReaderRegistry"/>
/// (reader + writer), and registers <see cref="UsdSceneLoader"/> with the
/// <see cref="AssetServer"/>.
/// </summary>
/// <remarks>
/// Skipped when the native USD plug-in tree isn't reachable - see <see cref="UsdRuntimeLayout"/>.
/// </remarks>
[Trait("Category", "Unit")]
[Trait("Backend", "Usd")]
[Collection(UsdTestCollection.Name)]
public class UsdScenesPluginTests
{
    [Fact]
    public void UsdScenesPlugin_Inserts_UsdRuntimeHandle_Resource()
    {
        SkipIfUsdMissing();

        using var app = new App();
        app.AddPlugin(new ScenesPlugin())
           .AddPlugin(new UsdScenesPlugin());

        app.World.ContainsResource<UsdRuntimeHandle>().Should().BeTrue();
    }

    [Fact]
    public void UsdScenesPlugin_Registers_Reader_And_Writer_With_Registry()
    {
        SkipIfUsdMissing();

        using var app = new App();
        app.AddPlugin(new ScenesPlugin())
           .AddPlugin(new UsdScenesPlugin());

        var registry = app.World.Resource<SceneReaderRegistry>();

        registry.FindReaderByFormat("usd").Should().BeOfType<UsdSceneReader>();
        registry.FindWriterByFormat("usd").Should().BeOfType<UsdSceneWriter>();
        registry.FindReaderByExtension(".usd").Should().BeOfType<UsdSceneReader>();
        registry.FindReaderByExtension(".usda").Should().BeOfType<UsdSceneReader>();
        registry.FindReaderByExtension(".usdc").Should().BeOfType<UsdSceneReader>();
        registry.FindReaderByExtension(".usdz").Should().BeOfType<UsdSceneReader>();
    }

    [Fact]
    public void UsdScenesPlugin_Creates_Registry_Implicitly_If_ScenesPlugin_Missing()
    {
        SkipIfUsdMissing();

        // Defensive path: forgetting ScenesPlugin should not break USD setup.
        using var app = new App();
        app.AddPlugin(new UsdScenesPlugin());

        app.World.ContainsResource<SceneReaderRegistry>().Should().BeTrue();
        app.World.Resource<SceneReaderRegistry>()
            .FindReaderByFormat("usd").Should().BeOfType<UsdSceneReader>();
    }

    [Fact]
    public void UsdScenesPlugin_Registers_UsdSceneLoader_With_AssetServer()
    {
        SkipIfUsdMissing();

        using var app = new App();
        app.AddPlugin(new AssetPlugin());

        var server = app.World.Resource<AssetServer>();
        int before = server.LoaderCount;

        app.AddPlugin(new ScenesPlugin())
           .AddPlugin(new UsdScenesPlugin());

        // UsdSceneLoader registers four extensions (.usd / .usda / .usdc / .usdz), so the
        // loader-by-extension map should grow by 4.
        server.LoaderCount.Should().Be(before + 4);
    }

    private static void SkipIfUsdMissing()
    {
        if (!UsdRuntimeLayout.IsAvailable())
            SkipTest.With("OpenUSD native plug-in tree not found - skipping plugin wiring tests that initialize the runtime.");
    }
}
