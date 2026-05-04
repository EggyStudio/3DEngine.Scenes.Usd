using pxr;

namespace Engine;

/// <summary>
/// Single source of truth for the engine's custom-metadata key strings used to round-trip
/// engine-specific material/mesh fields that don't have a stock <c>UsdGeom</c> /
/// <c>UsdShade</c> attribute (e.g. <see cref="SceneAlphaMode"/>'s tri-state, alpha cutoff,
/// double-sided overrides). Both <see cref="UsdSceneReader"/> and the upcoming
/// <c>UsdSceneWriter</c> consume these constants so a read → write round-trip is byte-stable.
/// </summary>
/// <remarks>
/// Per Plan §F: keys live in the <c>engine3d:</c> customData namespace and are intentionally
/// short. Tokens are pre-allocated to avoid per-prim allocation during stage traversal.
/// </remarks>
internal static class UsdSchemaKeys
{
    /// <summary>Reserved namespace prefix for all engine3d custom keys.</summary>
    public const string CustomDataNamespace = "engine3d";

    public const string AlphaMode   = "engine3d:alphaMode";    // token: "OPAQUE" | "MASK" | "BLEND"
    public const string AlphaCutoff = "engine3d:alphaCutoff";  // float
    public const string DoubleSided = "engine3d:doubleSided";  // bool (mirrors UsdGeomGprim where applicable)

    public static readonly TfToken TokAlphaMode   = new(AlphaMode);
    public static readonly TfToken TokAlphaCutoff = new(AlphaCutoff);
    public static readonly TfToken TokDoubleSided = new(DoubleSided);

    /// <summary>Parses an alpha-mode token (case-insensitive). Returns <see cref="SceneAlphaMode.Opaque"/> on unknown input.</summary>
    public static SceneAlphaMode ParseAlphaMode(string? value) => value?.ToUpperInvariant() switch
    {
        "MASK"  => SceneAlphaMode.Mask,
        "BLEND" => SceneAlphaMode.Blend,
        _       => SceneAlphaMode.Opaque,
    };

    /// <summary>Stringifies an alpha mode in the same casing the writer authors.</summary>
    public static string FormatAlphaMode(SceneAlphaMode mode) => mode switch
    {
        SceneAlphaMode.Mask  => "MASK",
        SceneAlphaMode.Blend => "BLEND",
        _                    => "OPAQUE",
    };
}