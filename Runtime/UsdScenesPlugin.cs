namespace Engine;

/// <summary>
/// Plugin that brings up the OpenUSD backend for the scene system. Registers
/// <see cref="UsdSceneLoader"/> with the <see cref="AssetServer"/> and the matching
/// <see cref="UsdSceneReader"/> / <see cref="UsdSceneWriter"/> with the
/// <see cref="SceneReaderRegistry"/>, and ensures the OpenUSD native runtime is initialized.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order:</b> add <i>after</i> <see cref="ScenesPlugin"/> and <see cref="AssetPlugin"/>;
/// <see cref="DefaultPlugins"/> already wires this up. Standalone consumers can opt in:
/// <code>
/// app.AddPlugin(new ScenesPlugin())
///    .AddPlugin(new UsdScenesPlugin());
/// </code>
/// </para>
/// <para>
/// <b>Threading:</b> per the OpenUSD contract <see cref="UniversalSceneDescription.UsdRuntime.Initialize"/>
/// is idempotent and thread-safe, so this plugin can be registered multiple times safely
/// (e.g. from tests). The actual stage I/O happens on <see cref="AssetServer"/> background
/// workers via <see cref="UsdSceneLoader.LoadAsync"/>.
/// </para>
/// </remarks>
/// <seealso cref="ScenesPlugin"/>
/// <seealso cref="UsdSceneLoader"/>
public sealed class UsdScenesPlugin : IPlugin
{
    private static readonly ILogger Logger = Log.Category("Engine.Scenes.Usd");

    /// <inheritdoc />
    public void Build(App app)
    {
        Logger.Info("UsdScenesPlugin: Initializing OpenUSD backend...");

        try
        {
            // Idempotent + thread-safe per UniversalSceneDescription contract.
            // Configures the native loader and registers the bundled Pixar plugin tree
            // (plugInfo.json discovery, schema registration, file format plugins).
            //
            // Auto-detect the on-disk layout: the UniversalSceneDescription NuGet ships its
            // native assets under runtimes/<rid>/native/usd/, but the .NET SDK's runtime-asset
            // deployment FLATTENS those into the bin root (libusd_*.so + plugInfo.json + schema*.usda
            // all live next to the host assembly). UsdRuntime.Initialize() with no args probes
            // BaseDirectory/usd, which doesn't exist in that layout, so we point it at the
            // actual location explicitly.
            var (pluginDir, nativeDir) = UsdRuntimeLayout.Resolve();
            if (pluginDir is not null || nativeDir is not null)
                UniversalSceneDescription.UsdRuntime.Initialize(pluginDir, nativeDir);
            else
                UniversalSceneDescription.UsdRuntime.Initialize();
        }
        catch (Exception ex)
        {
            Logger.Error($"UsdScenesPlugin: UsdRuntime.Initialize() failed: {ex.Message}");
            throw;
        }

        // Marker resource so other systems can express "depends on USD runtime"
        // via SystemDescriptor.Read<UsdRuntimeHandle>() and order/parallelize correctly.
        app.World.InsertResource(new UsdRuntimeHandle());

        // Register reader/writer with the backend-agnostic registry. ScenesPlugin is at
        var registry = app.World.Resource<SceneReaderRegistry>();
        registry.RegisterReader(new UsdSceneReader());
        registry.RegisterWriter(new UsdSceneWriter());

        // Register the asset loader so AssetServer can load Handle<SceneAsset> directly from
        // .usd / .usda / .usdc files. Mirrors how DefaultPlugins registers GlslLoader.
        var server = app.World.Resource<AssetServer>();
        server.RegisterLoader(new UsdSceneLoader());
        Logger.Debug("UsdScenesPlugin: UsdSceneLoader registered with AssetServer.");

        Logger.Info("UsdScenesPlugin: OpenUSD backend ready.");
    }
}

/// <summary>
/// Marker resource indicating that the OpenUSD native runtime has been initialized
/// by <see cref="UsdScenesPlugin"/>. Systems that touch USD types should declare a
/// <c>Read&lt;UsdRuntimeHandle&gt;()</c> dependency on their <see cref="SystemDescriptor"/>
/// so the parallel scheduler sees the order constraint.
/// </summary>
public sealed class UsdRuntimeHandle;