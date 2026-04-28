using Xunit;

namespace Engine.Tests.Scenes.Usd;

/// <summary>
/// xunit 2.9 doesn't expose <c>Assert.Skip(string)</c> directly; instead the convention
/// is to throw an exception whose message starts with <c>"$XunitDynamicSkip$"</c>
/// (see <c>Xunit.Sdk.DynamicSkipToken.Value</c>, which is internal to xunit). The runner
/// recognizes the prefix and reports the test as skipped with the trailing reason.
/// </summary>
internal static class SkipTest
{
    // Mirrors the internal Xunit.Sdk.DynamicSkipToken.Value constant.
    private const string DynamicSkipToken = "$XunitDynamicSkip$";

    /// <summary>Skips the current test with <paramref name="reason"/>. Always throws.</summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    public static void With(string reason) => 
        throw new InvalidOperationException(DynamicSkipToken + reason);

    /// <summary>Skips the current test if <paramref name="condition"/> is <c>true</c>.</summary>
    public static void If(bool condition, string reason)
    {
        if (condition) With(reason);
    }
}


