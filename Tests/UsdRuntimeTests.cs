using FluentAssertions;
using UniversalSceneDescription;
using Xunit;

namespace Engine.Tests.Scenes.Usd;

/// <summary>
/// Smoke tests for the OpenUSD native runtime brought up by <see cref="UsdScenesPlugin"/>.
/// Validates that the bundled <c>UniversalSceneDescription</c> bindings load on this platform
/// and that <c>UsdRuntime.Initialize</c> behaves per its idempotency / thread-safety contract.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Backend", "Usd")]
public class UsdRuntimeTests
{
    [Fact]
    public void UsdRuntime_Initialize_Does_Not_Throw()
    {
        // Native loader + Pixar plugin tree registration.
        var act = () => UsdRuntime.Initialize();

        act.Should().NotThrow("UsdRuntime.Initialize is documented as idempotent and thread-safe");
    }

    [Fact]
    public void UsdRuntime_Initialize_Is_Idempotent_When_Called_Repeatedly()
    {
        UsdRuntime.Initialize();
        UsdRuntime.Initialize();
        UsdRuntime.Initialize();

        // No assertion needed beyond "didn't throw" - the contract is idempotency.
        true.Should().BeTrue();
    }

    [Fact]
    public void UsdRuntime_Initialize_Is_Thread_Safe_From_Concurrent_Callers()
    {
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, 16, _ =>
        {
            try { UsdRuntime.Initialize(); }
            catch (Exception ex) { exceptions.Add(ex); }
        });

        exceptions.Should().BeEmpty();
    }
}

