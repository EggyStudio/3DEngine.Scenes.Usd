using Xunit;

namespace Engine.Tests.Scenes.Usd;

/// <summary>
/// Forces every test class in this namespace that opts in via
/// <c>[Collection(UsdTestCollection.Name)]</c> to run serially. The USD C# bindings
/// (SWIG-wrapped Pixar USD) have process-wide state - the schema registry, the Tf type
/// system, the Sdf layer cache, and the Usd plug-in registry are all globals. Having
/// multiple <see cref="UsdSceneReader"/> + <see cref="UsdSceneWriter"/> instances racing
/// against those caches in parallel xUnit collections has produced sporadic native
/// double-frees in CI. Serializing them via this collection costs &lt;1s for the whole
/// suite and removes the flake.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UsdTestCollection
{
    public const string Name = "Usd";
}

