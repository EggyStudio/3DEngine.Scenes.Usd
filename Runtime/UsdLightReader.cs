using System.Numerics;
using pxr;

namespace Engine;

/// <summary>
/// Reads a UsdLux light prim into a <see cref="SceneLightPayload"/>. Extracted from
/// <see cref="UsdSceneReader"/> so the full UsdLux schema family — boundable shapes
/// (sphere/disk/rect/cylinder/dome/geometry/portal), the non-boundable distant + plugin
/// lights, and the applied <c>UsdLuxShadowAPI</c> / <c>UsdLuxShapingAPI</c> — has a single
/// home that's easy to evolve.
/// </summary>
/// <remarks>
/// <para>
/// <b>API coverage of the bundled binding (UniversalSceneDescription 7.3.4 → USD.NET):</b>
/// all UsdLux types are exposed in the <c>pxr</c> namespace, including
/// <c>UsdLuxLightAPI</c>, <c>UsdLuxShadowAPI</c>, <c>UsdLuxShapingAPI</c>,
/// <c>UsdLuxDistantLight</c>, <c>UsdLuxSphereLight</c>, <c>UsdLuxRectLight</c>,
/// <c>UsdLuxDiskLight</c>, <c>UsdLuxCylinderLight</c>, <c>UsdLuxDomeLight</c>,
/// <c>UsdLuxGeometryLight</c>, <c>UsdLuxPortalLight</c>, <c>UsdLuxPluginLight</c>,
/// <c>UsdLuxLightFilter</c>, <c>UsdLuxLightListAPI</c>, <c>UsdLuxMeshLightAPI</c>,
/// <c>UsdLuxVolumeLightAPI</c>. The reader uses the typed schema helpers for shape inputs
/// and for the applied APIs.
/// </para>
/// <para>
/// <b>Shadow / Shaping detection:</b> we don't probe <c>HasAPI</c> directly — instead we
/// instantiate the API helper and check whether any of its <c>inputs:*</c> attributes have
/// authored values. This is robust against on-disk variants where the API was not formally
/// applied but the namespaced attributes were authored anyway (a common pattern in
/// hand-rolled .usda files), and it yields a <c>null</c> payload field for "untouched"
/// lights so renderers can apply their own defaults.
/// </para>
/// </remarks>
internal static class UsdLightReader
{
    /// <summary>Set of UsdLux concrete schema typeNames this reader recognizes.</summary>
    public static readonly HashSet<string> SupportedTypeNames = new(StringComparer.Ordinal)
    {
        "DistantLight", "SphereLight", "RectLight", "DiskLight",
        "CylinderLight", "DomeLight", "GeometryLight", "PortalLight", "PluginLight",
    };

    public static SceneLightPayload Read(UsdPrim prim, string typeName, UsdTimeCode time)
    {
        var lightApi = new UsdLuxLightAPI(prim);

        // Common UsdLuxLightAPI inputs.
        TryGetVec3(lightApi.GetColorAttr(), time, out var color, fallback: Vector3.One);
        float intensity = ReadFloat(lightApi.GetIntensityAttr(), time, 1f);
        float exposure  = ReadFloat(lightApi.GetExposureAttr(),  time, 0f);
        bool?   normalize           = ReadOptionalBool (lightApi.GetNormalizeAttr(),              time);
        float?  diffuse             = ReadOptionalFloat(lightApi.GetDiffuseAttr(),                time);
        float?  specular            = ReadOptionalFloat(lightApi.GetSpecularAttr(),               time);
        float?  colorTemperature    = ReadOptionalFloat(lightApi.GetColorTemperatureAttr(),       time);
        bool?   enableColorTemp     = ReadOptionalBool (lightApi.GetEnableColorTemperatureAttr(), time);

        // Per-shape inputs (only the relevant subset is populated).
        float? radius = null, width = null, height = null, length = null;
        string? domeTexture = null, domeTextureFormat = null, rectTexture = null;
        float? domeGuideRadius = null;
        IReadOnlyList<string> geometryPaths = Array.Empty<string>();
        IReadOnlyList<string> portalPaths   = Array.Empty<string>();

        switch (typeName)
        {
            case "SphereLight":
                radius = ReadOptionalFloat(new UsdLuxSphereLight(prim).GetRadiusAttr(), time);
                break;
            case "DiskLight":
                radius = ReadOptionalFloat(new UsdLuxDiskLight(prim).GetRadiusAttr(), time);
                break;
            case "RectLight":
                var rect = new UsdLuxRectLight(prim);
                width  = ReadOptionalFloat(rect.GetWidthAttr(),  time);
                height = ReadOptionalFloat(rect.GetHeightAttr(), time);
                rectTexture = ReadAssetPath(rect.GetTextureFileAttr(), time);
                break;
            case "CylinderLight":
                var cyl = new UsdLuxCylinderLight(prim);
                radius = ReadOptionalFloat(cyl.GetRadiusAttr(), time);
                length = ReadOptionalFloat(cyl.GetLengthAttr(), time);
                break;
            case "DomeLight":
                var dome = new UsdLuxDomeLight(prim);
                domeTexture       = ReadAssetPath(dome.GetTextureFileAttr(), time);
                domeTextureFormat = ReadOptionalToken(dome.GetTextureFormatAttr(), time);
                domeGuideRadius   = ReadOptionalFloat(dome.GetGuideRadiusAttr(), time);
                portalPaths       = ReadRelTargets(dome.GetPortalsRel());
                break;
            case "GeometryLight":
                geometryPaths = ReadRelTargets(new UsdLuxGeometryLight(prim).GetGeometryRel());
                break;
            case "PortalLight":
                // Portal width/height come from the schema's (computed) extent, but the
                // schema doesn't expose dedicated width/height attrs - the .usda authoring
                // pattern is to author width/height as custom inputs that downstream
                // renderers honor. Falling back to extent attribute when present.
                ReadPortalExtent(new UsdLuxPortalLight(prim), time, out width, out height);
                break;
            case "PluginLight":
                // Opaque to us beyond the common LightAPI inputs; renderer dispatches on
                // info:id (UsdLuxPluginLight.GetNodeDefAPI()).
                break;
        }

        var shadow  = ReadShadow (prim, time);
        var shaping = ReadShaping(prim, time);
        var filters = ReadRelTargets(lightApi.GetFiltersRel());

        return new SceneLightPayload
        {
            Name = prim.GetName().ToString(),
            Type = TypeNameToEnum(typeName),
            Color = color,
            Intensity = intensity,
            Exposure = exposure,
            Normalize = normalize,
            Diffuse = diffuse,
            Specular = specular,
            ColorTemperature = colorTemperature,
            EnableColorTemperature = enableColorTemp,
            Radius = radius,
            Width = width,
            Height = height,
            Length = length,
            ConeAngle      = shaping?.ConeAngle,
            ConeSoftness   = shaping?.ConeSoftness,
            IesProfilePath = shaping?.IesProfilePath,
            DomeTexturePath   = domeTexture,
            DomeTextureFormat = domeTextureFormat,
            DomeGuideRadius   = domeGuideRadius,
            RectTexturePath   = rectTexture,
            GeometryPaths = geometryPaths,
            PortalPaths   = portalPaths,
            FilterPaths   = filters,
            Shadow  = shadow,
            Shaping = shaping,
        };
    }

    // -- Applied APIs --

    private static SceneLightShadow? ReadShadow(UsdPrim prim, UsdTimeCode time)
    {
        var api = new UsdLuxShadowAPI(prim);
        bool?  enable  = ReadOptionalBool (api.GetShadowEnableAttr(),       time);
        Vector3? color = TryGetOptionalVec3(api.GetShadowColorAttr(),       time);
        float? distance     = ReadOptionalFloat(api.GetShadowDistanceAttr(), time);
        float? falloff      = ReadOptionalFloat(api.GetShadowFalloffAttr(),  time);
        float? falloffGamma = ReadOptionalFloat(api.GetShadowFalloffGammaAttr(), time);

        if (enable is null && color is null && distance is null && falloff is null && falloffGamma is null)
            return null;

        return new SceneLightShadow(enable, color, distance, falloff, falloffGamma);
    }

    private static SceneLightShaping? ReadShaping(UsdPrim prim, UsdTimeCode time)
    {
        var api = new UsdLuxShapingAPI(prim);
        float?  cone     = ReadOptionalFloat(api.GetShapingConeAngleAttr(),    time);
        float?  soft     = ReadOptionalFloat(api.GetShapingConeSoftnessAttr(), time);
        float?  focus    = ReadOptionalFloat(api.GetShapingFocusAttr(),        time);
        Vector3? tint    = TryGetOptionalVec3(api.GetShapingFocusTintAttr(),   time);
        string? iesPath  = ReadAssetPath    (api.GetShapingIesFileAttr(),      time);
        float?  iesScale = ReadOptionalFloat(api.GetShapingIesAngleScaleAttr(),time);
        bool?   iesNorm  = ReadOptionalBool (api.GetShapingIesNormalizeAttr(), time);

        if (cone is null && soft is null && focus is null && tint is null
            && iesPath is null && iesScale is null && iesNorm is null)
            return null;

        return new SceneLightShaping(cone, soft, focus, tint, iesPath, iesScale, iesNorm);
    }

    // -- Helpers --

    private static SceneLightType TypeNameToEnum(string typeName) => typeName switch
    {
        "DistantLight"  => SceneLightType.Distant,
        "SphereLight"   => SceneLightType.Sphere,
        "RectLight"     => SceneLightType.Rect,
        "DiskLight"     => SceneLightType.Disk,
        "CylinderLight" => SceneLightType.Cylinder,
        "DomeLight"     => SceneLightType.Dome,
        "GeometryLight" => SceneLightType.Geometry,
        "PortalLight"   => SceneLightType.Portal,
        "PluginLight"   => SceneLightType.Plugin,
        _               => SceneLightType.Sphere,
    };

    private static IReadOnlyList<string> ReadRelTargets(UsdRelationship rel)
    {
        if (!rel.IsValid() || !rel.HasAuthoredTargets()) return Array.Empty<string>();
        try
        {
            var targets = rel.GetTargets();
            int count = (int)targets.Count;
            if (count == 0) return Array.Empty<string>();
            var list = new string[count];
            for (int i = 0; i < count; i++)
                list[i] = targets[i].GetString();
            return list;
        }
        catch { return Array.Empty<string>(); }
    }

    private static void ReadPortalExtent(UsdLuxPortalLight portal, UsdTimeCode time, out float? width, out float? height)
    {
        width = height = null;
        var attr = portal.GetExtentAttr();
        if (!attr.IsValid() || !attr.HasAuthoredValue()) return;
        try
        {
            VtValue v = attr.Get(time);
            VtVec3fArray pts = v;
            if (pts.size() < 2) return;
            var min = pts[0]; var max = pts[1];
            // Portal local extents lie in the XY plane; width = X span, height = Y span.
            width  = MathF.Abs(max[0] - min[0]);
            height = MathF.Abs(max[1] - min[1]);
        }
        catch { /* leave null */ }
    }

    private static float ReadFloat(UsdAttribute attr, UsdTimeCode time, float fallback)
    {
        if (!attr.IsValid() || !attr.HasAuthoredValue()) return fallback;
        try { return (float)attr.Get(time); } catch { return fallback; }
    }

    private static float? ReadOptionalFloat(UsdAttribute attr, UsdTimeCode time)
    {
        if (!attr.IsValid() || !attr.HasAuthoredValue()) return null;
        try { return (float)attr.Get(time); } catch { return null; }
    }

    private static bool? ReadOptionalBool(UsdAttribute attr, UsdTimeCode time)
    {
        if (!attr.IsValid() || !attr.HasAuthoredValue()) return null;
        try { return (bool)attr.Get(time); } catch { return null; }
    }

    private static string? ReadOptionalToken(UsdAttribute attr, UsdTimeCode time)
    {
        if (!attr.IsValid() || !attr.HasAuthoredValue()) return null;
        try
        {
            VtValue v = attr.Get(time);
            return ((TfToken)v).ToString();
        }
        catch { return null; }
    }

    private static string? ReadAssetPath(UsdAttribute attr, UsdTimeCode time)
    {
        if (!attr.IsValid() || !attr.HasAuthoredValue()) return null;
        try
        {
            VtValue v = attr.Get(time);
            SdfAssetPath ap = v;
            var resolved = ap.GetResolvedPath();
            var raw = !string.IsNullOrEmpty(resolved) ? resolved : ap.GetAssetPath();
            return UsdEmbeddedTextureResolver.Resolve(raw);
        }
        catch { return null; }
    }

    private static bool TryGetVec3(UsdAttribute attr, UsdTimeCode time, out Vector3 value, Vector3 fallback)
    {
        if (!attr.IsValid() || !attr.HasAuthoredValue()) { value = fallback; return false; }
        try
        {
            VtValue v = attr.Get(time);
            GfVec3f vec = v;
            value = new Vector3(vec[0], vec[1], vec[2]);
            return true;
        }
        catch { value = fallback; return false; }
    }

    private static Vector3? TryGetOptionalVec3(UsdAttribute attr, UsdTimeCode time)
    {
        if (!attr.IsValid() || !attr.HasAuthoredValue()) return null;
        try
        {
            VtValue v = attr.Get(time);
            GfVec3f vec = v;
            return new Vector3(vec[0], vec[1], vec[2]);
        }
        catch { return null; }
    }
}