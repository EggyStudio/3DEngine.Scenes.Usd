using System.Numerics;
using pxr;

namespace Engine;

/// <summary>
/// Pre-pass material extractor: walks every <c>UsdShadeMaterial</c> on a stage once, builds
/// fully-resolved <see cref="SceneMaterialPayload"/> instances keyed by source prim path,
/// and exposes a binding-resolution helper used by both mesh-level and
/// <c>UsdGeomSubset</c>-level material lookup in <see cref="UsdSceneReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a pre-pass:</b> meshes (and subsets) reference materials by prim path; a single
/// stage walk dedupes the network walk and guarantees every binding resolves to the same
/// payload instance regardless of how many times it is bound. The cache is stage-scoped -
/// it is not retained across <see cref="UsdSceneReader.ReadAsync"/> calls so re-reads pick
/// up authoring edits.
/// </para>
/// <para>
/// <b>Network walk:</b> only <c>UsdPreviewSurface</c> is recognized today. For each input
/// the extractor reads the authored value (factor) and, when the input has authored
/// connections, follows the connection target to a <c>UsdUVTexture</c> shader, reads the
/// texture file / wrap modes, and chases <c>inputs:st</c> through a
/// <c>UsdPrimvarReader_float2</c> to recover the UV-set index. All connection following
/// uses <see cref="UsdAttribute.GetConnections"/> (an <see cref="SdfPath"/>-level API)
/// rather than the higher-level <c>UsdShadeConnectableAPI</c> overloads, so it works
/// uniformly across binding versions.
/// </para>
/// <para>
/// <b>AlphaMode / cutoff / doubleSided:</b> there is no stock UsdShade attribute for these,
/// so the extractor reads the engine's <c>engine3d:</c> customData keys
/// (<see cref="UsdSchemaKeys"/>) authored by <c>UsdSceneWriter</c>. Foreign USD assets that
/// don't author them get the payload defaults (Opaque / 0.5 / false).
/// </para>
/// </remarks>
internal static class UsdMaterialReader
{
    private static readonly ILogger Logger = Log.Category("Engine.Scenes.Usd");

    private static readonly TfToken TokInfoId      = new("info:id");
    private static readonly TfToken TokFile        = new("inputs:file");
    private static readonly TfToken TokWrapS       = new("inputs:wrapS");
    private static readonly TfToken TokWrapT       = new("inputs:wrapT");
    private static readonly TfToken TokSt          = new("inputs:st");
    private static readonly TfToken TokVarname     = new("inputs:varname");
    private static readonly TfToken TokScale       = new("inputs:scale");

    private const string IdUsdPreviewSurface  = "UsdPreviewSurface";
    private const string IdUsdUVTexture       = "UsdUVTexture";
    private const string IdPrimvarReaderFloat2= "UsdPrimvarReader_float2";

    // MaterialX-in-USD shader IDs. UsdShade authoring of MaterialX node graphs sets
    // info:id to the canonical "ND_<nodedef>_surfaceshader" the MaterialX standard
    // libraries use. We recognise the two PBR shaders the engine ships fixtures for;
    // the value-extraction path (TryExtractMaterialXSurface) maps their authored
    // numeric inputs onto SceneMaterialPayload factors.
    private const string IdMtlxStandardSurface = "ND_standard_surface_surfaceshader";
    private const string IdMtlxGltfPbr         = "ND_gltf_pbr_surfaceshader";
    private const string IdMtlxOpenPbrSurface  = "ND_open_pbr_surface_surfaceshader";

    /// <summary>
    /// Walks <paramref name="stage"/> once and produces a path-keyed cache of
    /// <see cref="SceneMaterialPayload"/> for every <c>UsdShadeMaterial</c> prim.
    /// Returns an empty cache when <paramref name="resolution"/> is
    /// <see cref="MaterialNetworkResolution.None"/>.
    /// </summary>
    public static Dictionary<string, SceneMaterialPayload> BuildCache(
        UsdStage stage,
        UsdTimeCode time,
        MaterialNetworkResolution resolution,
        CancellationToken ct)
    {
        var cache = new Dictionary<string, SceneMaterialPayload>(StringComparer.Ordinal);
        if (resolution == MaterialNetworkResolution.None) return cache;

        // Depth-first walk; UsdStage.Traverse() exists but signature varies across the
        // 7.0.x binding - keep the explicit recursion to match the rest of the reader.
        Walk(stage.GetPseudoRoot());
        return cache;

        void Walk(UsdPrim prim)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var child in prim.GetChildren())
            {
                if (child.GetTypeName().ToString() == "Material")
                {
                    var payload = Extract(child, stage, time, resolution);
                    if (payload is not null)
                        cache[child.GetPath().GetString()] = payload;
                    // Don't recurse into Material - shaders inside aren't materials themselves.
                }
                else
                {
                    Walk(child);
                }
            }
        }
    }

    /// <summary>
    /// Computes the bound-material prim path for <paramref name="gprim"/> via
    /// <see cref="UsdShadeMaterialBindingAPI"/>. Returns <c>null</c> when no material is
    /// bound. Used for both mesh-level and <c>UsdGeomSubset</c>-level binding.
    /// </summary>
    public static string? ResolveBoundMaterialPath(UsdPrim gprim)
    {
        try
        {
            var api = new UsdShadeMaterialBindingAPI(gprim);
            var bound = api.ComputeBoundMaterial();
            var matPrim = bound.GetPrim();
            return matPrim.IsValid() ? matPrim.GetPath().GetString() : null;
        }
        catch (Exception ex)
        {
            Logger.Debug($"UsdMaterialReader: ComputeBoundMaterial failed for '{gprim.GetPath().GetString()}': {ex.Message}");
            return null;
        }
    }

    // -- Single-material extraction --

    private static SceneMaterialPayload? Extract(
        UsdPrim matPrim,
        UsdStage stage,
        UsdTimeCode time,
        MaterialNetworkResolution resolution)
    {
        var matPath = matPrim.GetPath().GetString();

        // Find the surface shader. Prefer ComputeSurfaceSource when it returns a valid
        // result; fall back to the first child UsdPreviewSurface shader (handles the
        // common "no explicit outputs:surface connection" authoring style).
        UsdShadeShader? surface = null;
        try
        {
            var mat = new UsdShadeMaterial(matPrim);
            var src = mat.ComputeSurfaceSource();
            var srcPrim = src.GetPrim();
            if (srcPrim.IsValid())
                surface = new UsdShadeShader(srcPrim);
        }
        catch (Exception ex)
        {
            Logger.Debug($"UsdMaterialReader: ComputeSurfaceSource threw on '{matPath}': {ex.Message}; falling back to child-shader walk.");
        }

        if (surface is null)
        {
            foreach (var child in matPrim.GetChildren())
            {
                if (child.GetTypeName().ToString() != "Shader") continue;
                if (ReadShaderId(child) == IdUsdPreviewSurface)
                {
                    surface = new UsdShadeShader(child);
                    break;
                }
            }
        }

        // MaterialX-in-USD bridge: if we didn't find a UsdPreviewSurface, look for an
        // embedded MaterialX surfaceshader whose info:id is one of the canonical
        // "ND_*_surfaceshader" tokens. This covers UsdShade graphs authored from a
        // MaterialX nodedef (USD's UsdMtlx file-format plugin emits exactly this shape).
        if (surface is null)
        {
            foreach (var child in matPrim.GetChildren())
            {
                if (child.GetTypeName().ToString() != "Shader") continue;
                var id = ReadShaderId(child);
                if (id is IdMtlxStandardSurface or IdMtlxGltfPbr or IdMtlxOpenPbrSurface)
                {
                    var mtlxPayload = TryExtractMaterialXSurface(matPrim, child, id, time);
                    if (mtlxPayload is not null)
                    {
                        Logger.Debug($"UsdMaterialReader: '{matPath}' resolved via MaterialX bridge ({id}).");
                        return mtlxPayload;
                    }
                }
            }
        }

        // Initialize defaults matching SceneMaterialPayload.
        Vector4 baseColor = Vector4.One;
        float metallic = 0f;
        float roughness = 1f;
        Vector3 emissive = Vector3.Zero;
        float normalScale = 1f;
        SceneTextureRef? baseColorTex = null;
        SceneTextureRef? metallicTex = null;
        SceneTextureRef? roughnessTex = null;
        SceneTextureRef? normalTex = null;
        SceneTextureRef? emissiveTex = null;
        SceneTextureRef? occlusionTex = null;

        if (surface is not null && resolution != MaterialNetworkResolution.None)
        {
            foreach (var input in surface.GetInputs())
            {
                var name = input.GetBaseName().ToString();
                switch (name)
                {
                    case "diffuseColor":
                        if (TryFollowToTexture(input, stage, out var bcTex, out _))
                            baseColorTex = bcTex;
                        else if (TryGetFloat3(input, time, out var rgb))
                            baseColor = new Vector4(rgb, baseColor.W);
                        break;

                    case "opacity":
                        if (TryGetFloat(input, time, out var opacity))
                            baseColor = new Vector4(baseColor.X, baseColor.Y, baseColor.Z, opacity);
                        break;

                    case "metallic":
                        if (TryFollowToTexture(input, stage, out var mTex, out _))
                            metallicTex = mTex;
                        else
                            TryGetFloat(input, time, out metallic);
                        break;

                    case "roughness":
                        if (TryFollowToTexture(input, stage, out var rTex, out _))
                            roughnessTex = rTex;
                        else
                            TryGetFloat(input, time, out roughness);
                        break;

                    case "emissiveColor":
                        if (TryFollowToTexture(input, stage, out var eTex, out _))
                            emissiveTex = eTex;
                        else
                            TryGetFloat3(input, time, out emissive);
                        break;

                    case "normal":
                        if (TryFollowToTexture(input, stage, out var nTex, out var nUvShader))
                        {
                            normalTex = nTex;
                            // UsdPreviewSurface convention: scale on the upstream UsdUVTexture
                            // is the normal-map intensity. Read R of the float4 scale if present.
                            if (nUvShader is not null && TryReadTextureScaleX(nUvShader, time, out var s))
                                normalScale = s;
                        }
                        break;

                    case "occlusion":
                        if (TryFollowToTexture(input, stage, out var oTex, out _))
                            occlusionTex = oTex;
                        break;
                }
            }
        }

        // glTF-style metallic-roughness packing: if both factors point to the same texture
        // file, fuse them into MetallicRoughnessTexture and clear the individual refs.
        // (UsdPreviewSurface keeps them as separate UsdUVTexture shaders, but it is common
        // to author both pointing at one packed RGBA file; this mirrors the writer story.)
        SceneTextureRef? mrPacked = null;
        if (metallicTex is not null && roughnessTex is not null &&
            string.Equals(metallicTex.AssetPath, roughnessTex.AssetPath, StringComparison.Ordinal))
        {
            mrPacked = metallicTex with { /* keep wrap/uvSet from metallic side */ };
            metallicTex = null;
            roughnessTex = null;
        }
        else if (metallicTex is not null || roughnessTex is not null)
        {
            // Distinct files: payload has no separate slots, fall back to factors and log once-ish.
            Logger.Debug($"UsdMaterialReader: '{matPath}' uses distinct metallic/roughness textures; falling back to factor-only.");
            metallicTex = null;
            roughnessTex = null;
        }

        // engine3d:* customData (alpha mode / cutoff / double-sided).
        var alphaMode = SceneAlphaMode.Opaque;
        var alphaCutoff = 0.5f;
        var doubleSided = false;
        try
        {
            VtValue v = matPrim.GetCustomDataByKey(UsdSchemaKeys.TokAlphaMode);
            if (!v.IsEmpty()) alphaMode = UsdSchemaKeys.ParseAlphaMode(((TfToken)v).ToString());
        }
        catch
        {
            // Some bindings throw on token<->string conversion if the value was authored as
            // a plain string; try once more as a string.
            try
            {
                VtValue v = matPrim.GetCustomDataByKey(UsdSchemaKeys.TokAlphaMode);
                if (!v.IsEmpty()) alphaMode = UsdSchemaKeys.ParseAlphaMode((string)v);
            }
            catch { /* leave default */ }
        }
        try
        {
            VtValue v = matPrim.GetCustomDataByKey(UsdSchemaKeys.TokAlphaCutoff);
            if (!v.IsEmpty()) alphaCutoff = (float)v;
        }
        catch { /* leave default */ }
        try
        {
            VtValue v = matPrim.GetCustomDataByKey(UsdSchemaKeys.TokDoubleSided);
            if (!v.IsEmpty()) doubleSided = (bool)v;
        }
        catch { /* leave default */ }

        return new SceneMaterialPayload
        {
            Name = matPrim.GetName().ToString(),
            SourcePath = matPath,
            BaseColorFactor = baseColor,
            BaseColorTexture = baseColorTex,
            MetallicFactor = metallic,
            RoughnessFactor = roughness,
            MetallicRoughnessTexture = mrPacked,
            NormalTexture = normalTex,
            NormalScale = normalScale,
            EmissiveFactor = emissive,
            EmissiveTexture = emissiveTex,
            OcclusionTexture = occlusionTex,
            AlphaMode = alphaMode,
            AlphaCutoff = alphaCutoff,
            DoubleSided = doubleSided,
        };
    }

    // -- MaterialX-in-USD bridge --

    /// <summary>
    /// Reads a USD shader prim whose <c>info:id</c> is one of the MaterialX
    /// <c>ND_*_surfaceshader</c> tokens and projects its authored numeric inputs onto
    /// a <see cref="SceneMaterialPayload"/>. Mirrors the input set
    /// <see cref="MaterialXMaterialReader"/> handles on the pure-MTLX side, so a USD
    /// stage that embeds a MaterialX network and a side-by-side <c>.mtlx</c> file produce
    /// the same payload.
    /// </summary>
    /// <remarks>
    /// Texture / connection following is intentionally out of scope for the first slice
    /// (the engine's UsdUVTexture helpers don't apply: MaterialX uses
    /// <c>image</c>/<c>tiledimage</c> nodes with their own input naming). Authored
    /// factor values cover the test fixtures and the common "no textures" authoring
    /// path; richer extraction can land alongside the wider MaterialX value-getter
    /// support in future MaterialX.Net releases.
    /// </remarks>
    private static SceneMaterialPayload? TryExtractMaterialXSurface(
        UsdPrim matPrim,
        UsdPrim shaderPrim,
        string shaderId,
        UsdTimeCode time)
    {
        Vector4 baseColor = Vector4.One;
        float metallic = 0f;
        float roughness = 1f;
        Vector3 emissive = Vector3.Zero;

        foreach (var attr in shaderPrim.GetAttributes())
        {
            var name = attr.GetName().ToString();
            if (!name.StartsWith("inputs:", StringComparison.Ordinal)) continue;
            if (!attr.HasAuthoredValue()) continue;

            var inputName = name.Substring("inputs:".Length);
            switch (shaderId, inputName)
            {
                case (IdMtlxStandardSurface, "base_color"):
                case (IdMtlxOpenPbrSurface,  "base_color"):
                    if (TryReadColor3(attr, time, out var sc)) baseColor = new Vector4(sc, baseColor.W);
                    break;
                case (IdMtlxStandardSurface, "metalness"):
                case (IdMtlxOpenPbrSurface,  "base_metalness"):
                    if (TryReadFloat(attr, time, out var sm)) metallic = sm;
                    break;
                case (IdMtlxStandardSurface, "specular_roughness"):
                case (IdMtlxOpenPbrSurface,  "specular_roughness"):
                    if (TryReadFloat(attr, time, out var sr)) roughness = sr;
                    break;
                case (IdMtlxStandardSurface, "emission_color"):
                case (IdMtlxOpenPbrSurface,  "emission_color"):
                    if (TryReadColor3(attr, time, out var se)) emissive = se;
                    break;

                case (IdMtlxGltfPbr, "base_color"):
                    if (TryReadColor4(attr, time, out var gc)) baseColor = gc;
                    else if (TryReadColor3(attr, time, out var gc3)) baseColor = new Vector4(gc3, 1f);
                    break;
                case (IdMtlxGltfPbr, "metallic"):
                    if (TryReadFloat(attr, time, out var gm)) metallic = gm;
                    break;
                case (IdMtlxGltfPbr, "roughness"):
                    if (TryReadFloat(attr, time, out var gr)) roughness = gr;
                    break;
                case (IdMtlxGltfPbr, "emissive"):
                    if (TryReadColor3(attr, time, out var ge)) emissive = ge;
                    break;
            }
        }

        return new SceneMaterialPayload
        {
            Name = matPrim.GetName().ToString(),
            SourcePath = matPrim.GetPath().GetString(),
            BaseColorFactor = baseColor,
            MetallicFactor = metallic,
            RoughnessFactor = roughness,
            EmissiveFactor = emissive,
        };
    }

    private static bool TryReadFloat(UsdAttribute attr, UsdTimeCode time, out float value)
    {
        value = 0f;
        try { value = (float)attr.Get(time); return true; }
        catch { return false; }
    }

    private static bool TryReadColor3(UsdAttribute attr, UsdTimeCode time, out Vector3 value)
    {
        value = default;
        try
        {
            VtValue v = attr.Get(time);
            GfVec3f vec = v;
            value = new Vector3(vec[0], vec[1], vec[2]);
            return true;
        }
        catch { return false; }
    }

    private static bool TryReadColor4(UsdAttribute attr, UsdTimeCode time, out Vector4 value)
    {
        value = default;
        try
        {
            VtValue v = attr.Get(time);
            GfVec4f vec = v;
            value = new Vector4(vec[0], vec[1], vec[2], vec[3]);
            return true;
        }
        catch { return false; }
    }

    // -- Connection following --

    // If <paramref name="input"/> is connected to a UsdUVTexture shader, reads the
    // texture's file/wrapS/wrapT and resolves the UV-set index by chasing the texture's
    // st input through a UsdPrimvarReader_float2. Returns false when unconnected or the
    // upstream shader is not a UsdUVTexture. The resolved UsdUVTexture shader is exposed
    // via <paramref name="uvTexShader"/> for callers that need extra inputs (e.g. the
    // UPS-convention "scale" used as normal-map intensity).
    private static bool TryFollowToTexture(
        UsdShadeInput input,
        UsdStage stage,
        out SceneTextureRef texture,
        out UsdShadeShader? uvTexShader)
    {
        texture = null!;
        uvTexShader = null;
        if (!TryFollowConnection(input.GetAttr(), stage, out var srcPrim))
            return false;

        if (ReadShaderId(srcPrim) != IdUsdUVTexture) return false;
        var shader = new UsdShadeShader(srcPrim);
        uvTexShader = shader;

        // file (asset path)
        string assetPath = "";
        try
        {
            var fileAttr = srcPrim.GetAttribute(TokFile);
            if (fileAttr.IsValid() && fileAttr.HasAuthoredValue())
            {
                VtValue v = fileAttr.Get();
                SdfAssetPath ap = v;
                var resolved = ap.GetResolvedPath();
                assetPath = !string.IsNullOrEmpty(resolved) ? resolved : ap.GetAssetPath();
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"UsdMaterialReader: failed to read inputs:file on '{srcPrim.GetPath().GetString()}': {ex.Message}");
        }
        if (string.IsNullOrEmpty(assetPath)) return false;

        // wrapS / wrapT
        var wrapS = ReadWrap(srcPrim, TokWrapS);
        var wrapT = ReadWrap(srcPrim, TokWrapT);

        // UV set: follow inputs:st -> UsdPrimvarReader_float2.varname
        int uvSet = 0;
        try
        {
            var stAttr = srcPrim.GetAttribute(TokSt);
            if (stAttr.IsValid() && TryFollowConnection(stAttr, stage, out var readerPrim))
            {
                if (ReadShaderId(readerPrim) == IdPrimvarReaderFloat2)
                {
                    var varAttr = readerPrim.GetAttribute(TokVarname);
                    if (varAttr.IsValid() && varAttr.HasAuthoredValue())
                    {
                        VtValue v = varAttr.Get();
                        string varname;
                        try { varname = ((TfToken)v).ToString(); }
                        catch { varname = (string)v; }
                        uvSet = MapVarnameToUvSet(varname);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"UsdMaterialReader: UV-set resolution failed for '{srcPrim.GetPath().GetString()}': {ex.Message}");
        }

        texture = new SceneTextureRef(assetPath, uvSet, wrapS, wrapT);
        return true;
    }

    /// <summary>
    /// Resolves the first connected source of <paramref name="attr"/> to a prim. Uses the
    /// Sdf-level <see cref="UsdAttribute.GetConnections(SdfPathVector)"/> overload so it
    /// works regardless of which higher-level <c>UsdShadeConnectableAPI</c> overloads the
    /// binding exposes.
    /// </summary>
    private static bool TryFollowConnection(UsdAttribute attr, UsdStage stage, out UsdPrim sourcePrim)
    {
        sourcePrim = default!;
        if (!attr.IsValid()) return false;
        try
        {
            if (!attr.HasAuthoredConnections()) return false;
            var targets = new SdfPathVector();
            attr.GetConnections(targets);
            if (targets.Count == 0) return false;
            var primPath = targets[0].GetPrimPath();
            var prim = stage.GetPrimAtPath(primPath);
            if (!prim.IsValid()) return false;
            sourcePrim = prim;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug($"UsdMaterialReader: GetConnections failed on '{attr.GetPath().GetString()}': {ex.Message}");
            return false;
        }
    }

    private static SceneWrapMode ReadWrap(UsdPrim shaderPrim, TfToken token)
    {
        try
        {
            var attr = shaderPrim.GetAttribute(token);
            if (!attr.IsValid() || !attr.HasAuthoredValue()) return SceneWrapMode.Repeat;
            VtValue v = attr.Get();
            string s;
            try { s = ((TfToken)v).ToString(); }
            catch { s = (string)v; }
            return s switch
            {
                "clamp"  => SceneWrapMode.Clamp,
                "mirror" => SceneWrapMode.Mirror,
                "black"  => SceneWrapMode.Black,
                _        => SceneWrapMode.Repeat,
            };
        }
        catch { return SceneWrapMode.Repeat; }
    }

    private static int MapVarnameToUvSet(string varname) => varname switch
    {
        "st" or "st0" or "uv" or "uv0" => 0,
        "st1" or "uv1"                 => 1,
        _                              => 0, // unknown sets fall back to UV0
    };

    private static string? ReadShaderId(UsdPrim prim)
    {
        try
        {
            var attr = prim.GetAttribute(TokInfoId);
            if (!attr.IsValid() || !attr.HasAuthoredValue()) return null;
            VtValue v = attr.Get();
            try { return ((TfToken)v).ToString(); }
            catch { return (string)v; }
        }
        catch { return null; }
    }

    private static bool TryReadTextureScaleX(UsdShadeShader shader, UsdTimeCode time, out float scale)
    {
        scale = 1f;
        try
        {
            var attr = shader.GetPrim().GetAttribute(TokScale);
            if (!attr.IsValid() || !attr.HasAuthoredValue()) return false;
            VtValue v = attr.Get(time);
            // UsdUVTexture.scale is a float4 in modern UsdPreviewSurface; older assets
            // sometimes author it as float. Try both.
            try { GfVec4f vec = v; scale = vec[0]; return true; }
            catch { }
            try { scale = (float)v; return true; }
            catch { return false; }
        }
        catch { return false; }
    }

    private static bool TryGetFloat(UsdShadeInput input, UsdTimeCode time, out float value)
    {
        var attr = input.GetAttr();
        if (!attr.IsValid() || !attr.HasAuthoredValue()) { value = 0; return false; }
        try { value = (float)attr.Get(time); return true; }
        catch { value = 0; return false; }
    }

    private static bool TryGetFloat3(UsdShadeInput input, UsdTimeCode time, out Vector3 value)
    {
        var attr = input.GetAttr();
        if (!attr.IsValid() || !attr.HasAuthoredValue()) { value = default; return false; }
        try
        {
            VtValue v = attr.Get(time);
            GfVec3f vec = v;
            value = new Vector3(vec[0], vec[1], vec[2]);
            return true;
        }
        catch { value = default; return false; }
    }
}


