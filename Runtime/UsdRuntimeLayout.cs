namespace Engine;

/// <summary>
/// Locates the OpenUSD plug-in directory and native-library directory at runtime so
/// <c>UniversalSceneDescription.UsdRuntime.Initialize</c> can be called with explicit paths
/// regardless of how the build staged the package's <c>runtimes/&lt;rid&gt;/native/usd/</c> tree.
/// </summary>
/// <remarks>
/// <para>
/// The <c>UniversalSceneDescription</c> NuGet ships:
/// <list type="bullet">
///   <item><description><c>runtimes/&lt;rid&gt;/native/libusd_*.{so,dll,dylib}</c> (the native libraries)</description></item>
///   <item><description><c>runtimes/&lt;rid&gt;/native/usd/...</c> (the Pixar plug-in tree with <c>plugInfo.json</c>)</description></item>
/// </list>
/// Depending on how the consuming project is set up, those land in different shapes in the
/// build output:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Flat layout</b> (default for apps with <c>RuntimeIdentifier</c> set): everything
///     ends up next to the host assembly - <c>plugInfo.json</c> at <c>BaseDirectory</c>, no
///     <c>usd/</c> subdirectory. The shipped <c>UniversalSceneDescription.targets</c> tries
///     to preserve the tree but the SDK's runtime-asset deployment wins.
///   </description></item>
///   <item><description>
///     <b>Nested layout</b> (when the build successfully preserves structure): plug-ins live
///     under <c>BaseDirectory/usd/</c> - this is the layout <see cref="UniversalSceneDescription.UsdRuntime.Initialize"/>
///     defaults to.
///   </description></item>
///   <item><description>
///     <b>Dev fallback</b> (test runs without staged native assets): probe the NuGet cache
///     at <c>$NUGET_PACKAGES/universalscenedescription/&lt;version&gt;/runtimes/&lt;rid&gt;/native/usd</c>.
///   </description></item>
/// </list>
/// </remarks>
public static class UsdRuntimeLayout
{
    /// <summary>
    /// Resolves the plug-in and native-library directories. Returns <c>(null, null)</c> if the
    /// nested layout is detected (caller should pass no args to <c>UsdRuntime.Initialize</c>).
    /// Returns explicit paths for the flat or NuGet-cache layouts.
    /// </summary>
    public static (string? PluginDir, string? NativeDir) Resolve()
    {
        var baseDir = AppContext.BaseDirectory;

        // 1. Nested layout - the runtime's default already works.
        if (Directory.Exists(Path.Combine(baseDir, "usd")))
            return (null, null);

        // 2. Flat layout - plugInfo.json sits next to the host assembly alongside libusd_*.
        if (File.Exists(Path.Combine(baseDir, "plugInfo.json")))
            return (baseDir, baseDir);

        // 3. Dev fallback - locate in the NuGet cache.
        var fromCache = TryFindInNuGetCache();
        if (fromCache is not null)
            return (fromCache, Path.GetDirectoryName(fromCache));

        // 4. Nothing found - let the caller's default Initialize() throw with its own message.
        return (null, null);
    }

    /// <summary>
    /// Returns <c>true</c> if a USD plug-in tree appears reachable on this machine
    /// (either deployed next to the assembly or in the NuGet cache). Useful for tests
    /// that need to skip when the native assets aren't available.
    /// </summary>
    public static bool IsAvailable()
    {
        var baseDir = AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(baseDir, "usd"))) return true;
        if (File.Exists(Path.Combine(baseDir, "plugInfo.json"))) return true;
        return TryFindInNuGetCache() is not null;
    }

    private static string? TryFindInNuGetCache()
    {
        var rid = CurrentNativeRid();
        if (rid is null) return null;

        var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                        ".nuget", "packages");
        var pkgRoot = Path.Combine(nugetRoot, "universalscenedescription");
        if (!Directory.Exists(pkgRoot)) return null;

        foreach (var version in Directory.EnumerateDirectories(pkgRoot).OrderByDescending(d => d, StringComparer.Ordinal))
        {
            var candidate = Path.Combine(version, "runtimes", rid, "native", "usd");
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string? CurrentNativeRid()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (arch is null) return null;

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
            return $"linux-{arch}";
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            return $"win-{arch}";
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            return $"osx-{arch}";
        return null;
    }
}