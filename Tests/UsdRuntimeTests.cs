using FluentAssertions;
using UniversalSceneDescription;
using Xunit;

namespace Engine.Tests.Scenes.Usd;

/// <summary>
/// Smoke tests for the OpenUSD native runtime brought up by <see cref="UsdScenesPlugin"/>.
/// Validates that the bundled <c>UniversalSceneDescription</c> bindings load on this platform
/// and that <c>UsdRuntime.Initialize</c> behaves per its idempotency / thread-safety contract.
/// </summary>
/// <remarks>
/// Tests are <b>skipped</b> (not failed) when the native USD plug-in tree hasn't been deployed
/// next to the test assembly AND can't be located in the NuGet cache - that condition is a
/// build/deployment issue, not a logic bug. See <see cref="UsdRuntimeLayout"/>.
/// </remarks>
[Trait("Category", "Unit")]
[Trait("Backend", "Usd")]
public class UsdRuntimeTests
{
    [Fact]
    public void UsdRuntime_Initialize_Does_Not_Throw()
    {
        if (!UsdRuntimeLayout.IsAvailable())
            SkipTest.With("OpenUSD native plug-in tree not found.");

        var (pluginDir, nativeDir) = UsdRuntimeLayout.Resolve();
        var act = () => InitializeWith(pluginDir, nativeDir);

        act.Should().NotThrow("UsdRuntime.Initialize is documented as idempotent and thread-safe");
    }

    [Fact]
    public void UsdRuntime_Initialize_Is_Idempotent_When_Called_Repeatedly()
    {
        if (!UsdRuntimeLayout.IsAvailable())
            SkipTest.With("OpenUSD native plug-in tree not found.");

        var (pluginDir, nativeDir) = UsdRuntimeLayout.Resolve();
        InitializeWith(pluginDir, nativeDir);
        InitializeWith(pluginDir, nativeDir);
        InitializeWith(pluginDir, nativeDir);

        // No assertion needed beyond "didn't throw" - the contract is idempotency.
        true.Should().BeTrue();
    }

    [Fact]
    public void UsdRuntime_Initialize_Is_Thread_Safe_From_Concurrent_Callers()
    {
        if (!UsdRuntimeLayout.IsAvailable())
            SkipTest.With("OpenUSD native plug-in tree not found.");

        var (pluginDir, nativeDir) = UsdRuntimeLayout.Resolve();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, 16, _ =>
        {
            try { InitializeWith(pluginDir, nativeDir); }
            catch (Exception ex) { exceptions.Add(ex); }
        });

        exceptions.Should().BeEmpty();
    }

    private static void InitializeWith(string? pluginDir, string? nativeDir)
    {
        if (pluginDir is null && nativeDir is null)
            UsdRuntime.Initialize();
        else
            UsdRuntime.Initialize(pluginDir, nativeDir);
    }
}
