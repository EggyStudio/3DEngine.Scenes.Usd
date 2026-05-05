using System.Numerics;
using pxr;

namespace Engine;

/// <summary>
/// Writes a <see cref="SceneLightPayload"/> back to a UsdLux prim. Symmetric counterpart
/// to <see cref="UsdLightReader"/>: the on-disk shape produced here reads back into an
/// equivalent payload via the reader.
/// </summary>
/// <remarks>
/// <para>
/// Common UsdLuxLightAPI inputs (color/intensity/exposure/diffuse/specular/normalize/
/// colorTemperature/enableColorTemperature) are emitted on every light. Per-shape inputs
/// (radius/width/height/length, dome+rect texture) are emitted only when populated on the
/// payload. <see cref="UsdLuxShadowAPI"/> and <see cref="UsdLuxShapingAPI"/> are applied
/// (so they appear in <c>apiSchemas = ["ShadowAPI", "ShapingAPI"]</c>) and their
/// <c>inputs:shadow:*</c> / <c>inputs:shaping:*</c> attributes authored only when the
/// payload carries the corresponding nested record / convenience field.
/// </para>
/// </remarks>
internal static class UsdLightWriter
{
    public static UsdPrim Write(UsdStage stage, SdfPath path, SceneLightPayload light)
    {
        // Define the appropriate schema and write per-shape inputs.
        UsdPrim prim;
        switch (light.Type)
        {
            case SceneLightType.Distant:
                prim = UsdLuxDistantLight.Define(stage, path).GetPrim();
                break;

            case SceneLightType.Sphere:
                prim = UsdLuxSphereLight.Define(stage, path).GetPrim();
                if (light.Radius is { } sr)
                    SetFloat(prim, "inputs:radius", sr);
                break;

            case SceneLightType.Disk:
                prim = UsdLuxDiskLight.Define(stage, path).GetPrim();
                if (light.Radius is { } dr)
                    SetFloat(prim, "inputs:radius", dr);
                break;

            case SceneLightType.Rect:
                prim = UsdLuxRectLight.Define(stage, path).GetPrim();
                if (light.Width  is { } w) SetFloat(prim, "inputs:width",  w);
                if (light.Height is { } h) SetFloat(prim, "inputs:height", h);
                if (!string.IsNullOrEmpty(light.RectTexturePath))
                    SetAsset(prim, "inputs:texture:file", light.RectTexturePath);
                break;

            case SceneLightType.Cylinder:
                prim = UsdLuxCylinderLight.Define(stage, path).GetPrim();
                if (light.Radius is { } cr) SetFloat(prim, "inputs:radius", cr);
                if (light.Length is { } cl) SetFloat(prim, "inputs:length", cl);
                break;

            case SceneLightType.Dome:
                prim = UsdLuxDomeLight.Define(stage, path).GetPrim();
                if (!string.IsNullOrEmpty(light.DomeTexturePath))
                    SetAsset(prim, "inputs:texture:file", light.DomeTexturePath);
                if (!string.IsNullOrEmpty(light.DomeTextureFormat))
                    SetToken(prim, "inputs:texture:format", light.DomeTextureFormat);
                if (light.DomeGuideRadius is { } gr)
                    SetFloat(prim, "guideRadius", gr);
                if (light.PortalPaths.Count > 0)
                    SetRelTargets(new UsdLuxDomeLight(prim).CreatePortalsRel(), light.PortalPaths);
                break;

            case SceneLightType.Geometry:
                prim = UsdLuxGeometryLight.Define(stage, path).GetPrim();
                if (light.GeometryPaths.Count > 0)
                    SetRelTargets(new UsdLuxGeometryLight(prim).CreateGeometryRel(), light.GeometryPaths);
                break;

            case SceneLightType.Portal:
                prim = UsdLuxPortalLight.Define(stage, path).GetPrim();
                // PortalLight has no width/height attrs; the renderer derives extents from
                // the portal's extent / xform. We write a centered XY extent when payload
                // carries width/height so a round-trip preserves the visual size.
                if (light.Width is { } pw && light.Height is { } ph)
                    SetExtent(prim, pw, ph);
                break;

            case SceneLightType.Plugin:
                prim = UsdLuxPluginLight.Define(stage, path).GetPrim();
                break;

            default:
                prim = UsdLuxSphereLight.Define(stage, path).GetPrim();
                break;
        }

        // Common UsdLuxLightAPI inputs.
        SetColor3(prim, "inputs:color", light.Color);
        SetFloat (prim, "inputs:intensity", light.Intensity);
        SetFloat (prim, "inputs:exposure",  light.Exposure);
        if (light.Normalize              is { } n)   SetBool (prim, "inputs:normalize", n);
        if (light.Diffuse                is { } d)   SetFloat(prim, "inputs:diffuse",   d);
        if (light.Specular               is { } s)   SetFloat(prim, "inputs:specular",  s);
        if (light.ColorTemperature       is { } ct)  SetFloat(prim, "inputs:colorTemperature", ct);
        if (light.EnableColorTemperature is { } ect) SetBool (prim, "inputs:enableColorTemperature", ect);

        // ShadowAPI / ShapingAPI: apply + write authored fields. Apply() formally lists the
        // schema in the prim's apiSchemas metadata so consumers (incl. UsdLightReader on
        // round-trip) see them as applied APIs.
        WriteShadow(prim, light.Shadow);
        WriteShaping(prim, light);

        // Filter linkage.
        if (light.FilterPaths.Count > 0)
            SetRelTargets(new UsdLuxLightAPI(prim).CreateFiltersRel(), light.FilterPaths);

        return prim;
    }

    private static void WriteShadow(UsdPrim prim, SceneLightShadow? shadow)
    {
        if (shadow is null) return;
        var api = UsdLuxShadowAPI.Apply(prim);
        if (shadow.Enable       is { } e)  api.CreateShadowEnableAttr      (new VtValue(e), false);
        if (shadow.Color        is { } c)  api.CreateShadowColorAttr       (new VtValue(new GfVec3f(c.X, c.Y, c.Z)), false);
        if (shadow.Distance     is { } d)  api.CreateShadowDistanceAttr    (new VtValue(d), false);
        if (shadow.Falloff      is { } f)  api.CreateShadowFalloffAttr     (new VtValue(f), false);
        if (shadow.FalloffGamma is { } fg) api.CreateShadowFalloffGammaAttr(new VtValue(fg), false);
    }

    private static void WriteShaping(UsdPrim prim, SceneLightPayload light)
    {
        // Prefer the nested record; fall back to the convenience shortcut fields. Either
        // path implies an applied UsdLuxShapingAPI.
        var s = light.Shaping;
        var coneAngle      = s?.ConeAngle      ?? light.ConeAngle;
        var coneSoftness   = s?.ConeSoftness   ?? light.ConeSoftness;
        var iesProfilePath = s?.IesProfilePath ?? light.IesProfilePath;
        var focusPower     = s?.FocusPower;
        var focusTint      = s?.FocusTint;
        var iesAngleScale  = s?.IesAngleScale;
        var iesNormalize   = s?.IesNormalize;

        if (coneAngle is null && coneSoftness is null && iesProfilePath is null
            && focusPower is null && focusTint is null && iesAngleScale is null && iesNormalize is null)
            return;

        var api = UsdLuxShapingAPI.Apply(prim);
        if (coneAngle      is { } ca)  api.CreateShapingConeAngleAttr   (new VtValue(ca), false);
        if (coneSoftness   is { } cs)  api.CreateShapingConeSoftnessAttr(new VtValue(cs), false);
        if (focusPower     is { } fp)  api.CreateShapingFocusAttr       (new VtValue(fp), false);
        if (focusTint      is { } ft)  api.CreateShapingFocusTintAttr   (new VtValue(new GfVec3f(ft.X, ft.Y, ft.Z)), false);
        if (!string.IsNullOrEmpty(iesProfilePath))
            api.CreateShapingIesFileAttr(new VtValue(new SdfAssetPath(iesProfilePath)), false);
        if (iesAngleScale  is { } ias) api.CreateShapingIesAngleScaleAttr(new VtValue(ias), false);
        if (iesNormalize   is { } ino) api.CreateShapingIesNormalizeAttr (new VtValue(ino), false);
    }

    // -- Attribute helpers --

    private static void SetFloat(UsdPrim prim, string name, float value) =>
        prim.CreateAttribute(new TfToken(name), SdfValueTypeNames.Float).Set(new VtValue(value));

    private static void SetBool(UsdPrim prim, string name, bool value) =>
        prim.CreateAttribute(new TfToken(name), SdfValueTypeNames.Bool).Set(new VtValue(value));

    private static void SetToken(UsdPrim prim, string name, string token) =>
        prim.CreateAttribute(new TfToken(name), SdfValueTypeNames.Token).Set(new VtValue(new TfToken(token)));

    private static void SetAsset(UsdPrim prim, string name, string path) =>
        prim.CreateAttribute(new TfToken(name), SdfValueTypeNames.Asset).Set(new VtValue(new SdfAssetPath(path)));

    private static void SetColor3(UsdPrim prim, string name, Vector3 rgb) =>
        prim.CreateAttribute(new TfToken(name), SdfValueTypeNames.Color3f).Set(new VtValue(new GfVec3f(rgb.X, rgb.Y, rgb.Z)));

    private static void SetExtent(UsdPrim prim, float width, float height)
    {
        var arr = new VtVec3fArray(2);
        var hw = width  * 0.5f;
        var hh = height * 0.5f;
        arr[0] = new GfVec3f(-hw, -hh, 0);
        arr[1] = new GfVec3f( hw,  hh, 0);
        prim.CreateAttribute(new TfToken("extent"), SdfValueTypeNames.Float3Array).Set(new VtValue(arr));
    }

    private static void SetRelTargets(UsdRelationship rel, IReadOnlyList<string> paths)
    {
        if (!rel.IsValid()) return;
        var v = new SdfPathVector();
        foreach (var p in paths)
        {
            if (string.IsNullOrEmpty(p)) continue;
            v.Add(new SdfPath(p));
        }
        rel.SetTargets(v);
    }
}