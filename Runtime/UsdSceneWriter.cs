using System.Numerics;
using pxr;
using UniversalSceneDescription;

namespace Engine;

/// <summary>
/// <see cref="ISceneWriter"/> for OpenUSD. Authors a <see cref="Scene"/> snapshot into a
/// <c>.usd / .usda / .usdc / .usdz</c> file via the bundled UniversalSceneDescription
/// bindings. Symmetric with <see cref="UsdSceneReader"/>: the on-disk shape produced here
/// reads back into an equivalent <see cref="Scene"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Container selection:</b> <c>UsdStage.CreateNew</c> picks the file format from the
/// target extension (ASCII <c>.usda</c> / crate <c>.usdc</c> / package <c>.usdz</c>). The
/// writer never spools to a temp file - USD writes directly to the target path
/// (USD's <c>SdfFileFormat</c> plug-ins handle the binary/zip framing).
/// </para>
/// <para>
/// <b>Coordinate / unit policy:</b> mirrors <see cref="UsdSceneReader"/>: vertex data is
/// emitted as-is. <see cref="SceneExportSettings.CoordinateSystem"/> and
/// <see cref="SceneExportSettings.MetersPerUnit"/> are written as stage metadata
/// (<c>upAxis</c> / <c>metersPerUnit</c>); no per-vertex transformation. A read-write
/// round-trip therefore matches byte-for-byte on those fields.
/// </para>
/// <para>
/// <b>Transforms:</b> a single <c>xformOp:transform</c> (composed <c>GfMatrix4d</c>) is
/// emitted per node. This is lossless for any <see cref="Transform"/> (TRS) and avoids
/// the precision-loss / op-order asymmetry of writing translate/orient/scale separately
/// when the source authored a single op stack we never inspected.
/// </para>
/// <para>
/// <b>Materials:</b> a stage-wide pre-pass collects every unique
/// <see cref="SceneMaterialPayload"/> (deduped by <see cref="SceneMaterialPayload.SourcePath"/>)
/// and emits each as a <c>UsdShadeMaterial</c> with a single <c>UsdPreviewSurface</c>
/// shader child. Inputs authored: <c>diffuseColor</c>, <c>opacity</c>, <c>metallic</c>,
/// <c>roughness</c>, <c>emissiveColor</c>. <c>engine3d:</c> customData
/// (<see cref="UsdSchemaKeys"/>) is written so AlphaMode/cutoff/doubleSided round-trip.
/// Texture-network authoring (UsdUVTexture + UsdPrimvarReader_float2) is not yet emitted
/// in v1; payloads carrying texture refs log a one-time Debug message and degrade to
/// factor-only.
/// </para>
/// <para>
/// <b>Material binding:</b> mesh-level binding uses
/// <c>UsdShadeMaterialBindingAPI.Apply(...).Bind(...)</c>. Per-subset binding uses
/// <c>UsdGeomSubset</c> children with <c>familyName == "materialBind"</c> plus the same
/// binding API on the subset prim.
/// </para>
/// <para>
/// <b>Threading:</b> called on an <see cref="AssetServer"/> background worker. The native
/// <c>UsdStage</c> is created, populated, saved, and disposed entirely on this thread.
/// </para>
/// </remarks>
public sealed class UsdSceneWriter : ISceneWriter
{
    private static readonly ILogger Logger = Log.Category("Engine.Scenes.Usd");

    private static readonly TfToken TokYAxis = new("Y");
    private static readonly TfToken TokZAxis = new("Z");
    private static readonly TfToken TokInfoId = new("info:id");
    private static readonly TfToken TokSurface = new("surface");
    private static readonly TfToken TokDiffuseColor = new("diffuseColor");
    private static readonly TfToken TokOpacity = new("opacity");
    private static readonly TfToken TokMetallic = new("metallic");
    private static readonly TfToken TokRoughness = new("roughness");
    private static readonly TfToken TokEmissiveColor = new("emissiveColor");
    private static readonly TfToken TokFamilyMaterialBind = new("materialBind");
    private static readonly TfToken TokFaceElement = new("face");

    /// <inheritdoc />
    public string FormatId => "usd";

    /// <inheritdoc />
    public Task WriteAsync(Scene scene, string targetPath, SceneExportSettings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(settings);

        EnsureRuntimeReady();

        // Make sure the destination directory exists (USD won't create parents).
        var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using (var stage = UsdStage.CreateNew(targetPath))
        {
            if (stage is null)
                throw new InvalidOperationException($"UsdStage.CreateNew returned null for '{targetPath}'.");

            // Stage metadata - load-bearing for round-trip with the reader (Plan §B).
            UsdGeom.UsdGeomSetStageUpAxis(stage,
                settings.CoordinateSystem == SceneCoordinateSystem.ZUp ? TokZAxis : TokYAxis);
            UsdGeom.UsdGeomSetStageMetersPerUnit(stage, settings.MetersPerUnit);

            // Materials pre-pass: define every unique material under "/Looks" so subset
            // bindings further down the tree can reference them by path.
            var materialPathsByKey = WriteAllMaterials(stage, scene, ct);

            // Write the prim hierarchy.
            var usedRootNames = new HashSet<string>(StringComparer.Ordinal);
            string? defaultPrimName = null;
            foreach (var root in scene.Roots)
            {
                ct.ThrowIfCancellationRequested();
                var name = UniqueName(SanitizeIdent(root.Name, "Root"), usedRootNames);
                var path = new SdfPath("/" + name);
                WritePrim(stage, path, root, materialPathsByKey, ct);
                defaultPrimName ??= name;
            }

            // Set defaultPrim so consumers know where to start. .usdz especially relies on
            // this for the package's intrinsic asset path.
            if (defaultPrimName is not null)
            {
                var defaultPrim = stage.GetPrimAtPath(new SdfPath("/" + defaultPrimName));
                if (defaultPrim.IsValid())
                    stage.SetDefaultPrim(defaultPrim);
            }

            stage.Save();
        }

        if (settings.EmbedTextures)
        {
            // .usdz packaging via UsdZipFileWriter is not yet bound in 7.0.x C# surface;
            // when the target is .usdz, UsdStage.CreateNew already produces a valid
            // package container and embedded references resolve through Ar. For now log
            // the request so callers see the gap; texture embedding lands with the PBR
            // material upgrade.
            Logger.Debug($"UsdSceneWriter: EmbedTextures requested for '{targetPath}'; texture embedding is a follow-up (texture-authoring path is not yet implemented).");
        }

        return Task.CompletedTask;
    }

    // -- Materials --

    /// <summary>
    /// Walks <see cref="Scene.Roots"/> once to collect every distinct
    /// <see cref="SceneMaterialPayload"/> by <c>SourcePath</c> (or, when missing, by
    /// reference identity), defines a <c>UsdShadeMaterial</c> per entry under <c>/Looks</c>,
    /// and returns a map from the dedup key to the authored prim path. The map is then
    /// consulted by <see cref="WritePrim"/> for material binding.
    /// </summary>
    private static Dictionary<object, SdfPath> WriteAllMaterials(
        UsdStage stage,
        Scene scene,
        CancellationToken ct)
    {
        // Dedup: prefer SourcePath when the payload was authored by the reader; fall back
        // to reference identity for synthetic / programmatically-built scenes. Object key
        // accommodates either (string or SceneMaterialPayload).
        var byKey = new Dictionary<object, SceneMaterialPayload>(EqualityComparer<object>.Default);
        foreach (var root in scene.Roots)
            CollectMaterials(root, byKey);

        var result = new Dictionary<object, SdfPath>(EqualityComparer<object>.Default);
        if (byKey.Count == 0) return result;

        // Define the /Looks scope once.
        UsdGeomXform.Define(stage, new SdfPath("/Looks"));

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        bool anyTextureRefSeen = false;
        foreach (var (key, mat) in byKey)
        {
            ct.ThrowIfCancellationRequested();
            var name = UniqueName(SanitizeIdent(string.IsNullOrEmpty(mat.Name) ? "Mat" : mat.Name, "Mat"), usedNames);
            var matPath = new SdfPath("/Looks/" + name);
            WriteOneMaterial(stage, matPath, mat);
            result[key] = matPath;

            if (!anyTextureRefSeen && HasAnyTextureRef(mat))
            {
                anyTextureRefSeen = true;
                Logger.Debug($"UsdSceneWriter: material '{mat.SourcePath}' carries texture references but UsdUVTexture network authoring is not yet implemented; emitting factor-only.");
            }
        }
        return result;
    }

    private static void CollectMaterials(SceneNode node, Dictionary<object, SceneMaterialPayload> sink)
    {
        foreach (var c in node.Components)
        {
            if (c is not SceneMaterialPayload mat) continue;
            object key = !string.IsNullOrEmpty(mat.SourcePath) ? mat.SourcePath : (object)mat;
            sink.TryAdd(key, mat);
        }
        foreach (var child in node.Children) CollectMaterials(child, sink);
    }

    private static void WriteOneMaterial(UsdStage stage, SdfPath matPath, SceneMaterialPayload mat)
    {
        var material = UsdShadeMaterial.Define(stage, matPath);

        var shaderPath = matPath.AppendChild(new TfToken("PBR"));
        var shader = UsdShadeShader.Define(stage, shaderPath);

        // info:id = "UsdPreviewSurface"
        var idAttr = shader.GetPrim().CreateAttribute(TokInfoId, SdfValueTypeNames.Token);
        idAttr.Set(new VtValue(new TfToken("UsdPreviewSurface")));

        // diffuseColor (color3f) - splits payload's RGBA into RGB + opacity.
        CreateColor3Input(shader, TokDiffuseColor, new Vector3(mat.BaseColorFactor.X, mat.BaseColorFactor.Y, mat.BaseColorFactor.Z));
        // opacity (float)
        CreateFloatInput(shader, TokOpacity, mat.BaseColorFactor.W);
        // metallic / roughness
        CreateFloatInput(shader, TokMetallic, mat.MetallicFactor);
        CreateFloatInput(shader, TokRoughness, mat.RoughnessFactor);
        // emissiveColor (color3f)
        CreateColor3Input(shader, TokEmissiveColor, mat.EmissiveFactor);

        // Wire material's surface output to the shader's surface output so
        // UsdShadeMaterial.ComputeSurfaceSource() resolves on read-back. Done via the
        // lower-level CreateOutput + ConnectToSource APIs because the binding does not
        // expose a single-call CreateSurfaceOutput in this version.
        var matOut = material.CreateOutput(TokSurface, SdfValueTypeNames.Token);
        var shaderOut = shader.CreateOutput(TokSurface, SdfValueTypeNames.Token);
        matOut.ConnectToSource(shaderOut);

        // engine3d:* customData (alpha mode / cutoff / double sided). Use a nested dict
        // because USD's GetCustomDataByKey treats ":" as a path separator (this is what
        // UsdMaterialReader probes for; flat colon-name keys would be unreachable).
        var dict = new VtDictionary();
        dict.SetValueAtPath("engine3d:alphaMode", new VtValue(new TfToken(UsdSchemaKeys.FormatAlphaMode(mat.AlphaMode))));
        dict.SetValueAtPath("engine3d:alphaCutoff", new VtValue(mat.AlphaCutoff));
        dict.SetValueAtPath("engine3d:doubleSided", new VtValue(mat.DoubleSided));
        material.GetPrim().SetCustomData(dict);
    }

    private static void CreateColor3Input(UsdShadeShader shader, TfToken name, Vector3 rgb)
    {
        var input = shader.CreateInput(name, SdfValueTypeNames.Color3f);
        input.Set(new VtValue(new GfVec3f(rgb.X, rgb.Y, rgb.Z)));
    }

    private static void CreateFloatInput(UsdShadeShader shader, TfToken name, float value)
    {
        var input = shader.CreateInput(name, SdfValueTypeNames.Float);
        input.Set(new VtValue(value));
    }

    private static bool HasAnyTextureRef(SceneMaterialPayload m)
        => m.BaseColorTexture is not null
        || m.MetallicRoughnessTexture is not null
        || m.NormalTexture is not null
        || m.EmissiveTexture is not null
        || m.OcclusionTexture is not null;

    // -- Recursion --

    private static void WritePrim(
        UsdStage stage,
        SdfPath path,
        SceneNode node,
        Dictionary<object, SdfPath> materialPaths,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Pick prim type from the first payload that drives one. Materials don't define a
        // prim type (they live under /Looks); a node carrying only a material falls back
        // to Xform so its hierarchy / binding still serializes.
        SceneMeshPayload? mesh = null;
        SceneCameraPayload? cam = null;
        SceneLightPayload? light = null;
        SceneMaterialPayload? boundMat = null;
        foreach (var c in node.Components)
        {
            switch (c)
            {
                case SceneMeshPayload m: mesh ??= m; break;
                case SceneCameraPayload cc: cam ??= cc; break;
                case SceneLightPayload l: light ??= l; break;
                case SceneMaterialPayload mat: boundMat ??= mat; break;
            }
        }

        UsdPrim prim;
        if (mesh is not null)
        {
            var meshSchema = UsdGeomMesh.Define(stage, path);
            WriteMeshTopology(meshSchema, mesh);
            prim = meshSchema.GetPrim();

            if (boundMat is not null)
                BindMaterial(prim, boundMat, materialPaths);

            // Subsets: emit one UsdGeomSubset per SceneMeshSubset whose IndexCount > 0.
            // Index count must be a multiple of 3 (post-triangulation) - convert ranges
            // back to original-face indices via division by 3 (the writer's mesh is
            // always triangulated). Each subset has its own material binding when set.
            WriteMeshSubsets(stage, path, mesh, materialPaths);
        }
        else if (cam is not null)
        {
            var camSchema = UsdGeomCamera.Define(stage, path);
            WriteCameraAttrs(camSchema, cam);
            prim = camSchema.GetPrim();
        }
        else if (light is not null)
        {
            prim = WriteLight(stage, path, light);
        }
        else
        {
            prim = UsdGeomXform.Define(stage, path).GetPrim();
        }

        // Local transform (single composed matrix - lossless and symmetric with the
        // reader's GetLocalTransformation path).
        WriteLocalTransform(prim, node.LocalTransform);

        // Recurse into children with sanitized + sibling-unique names.
        var usedChildNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in node.Children)
        {
            var childName = UniqueName(SanitizeIdent(child.Name, "Child"), usedChildNames);
            var childPath = path.AppendChild(new TfToken(childName));
            WritePrim(stage, childPath, child, materialPaths, ct);
        }
    }

    private static void BindMaterial(UsdPrim gprim, SceneMaterialPayload mat, Dictionary<object, SdfPath> materialPaths)
    {
        object key = !string.IsNullOrEmpty(mat.SourcePath) ? mat.SourcePath : (object)mat;
        if (!materialPaths.TryGetValue(key, out var matPath)) return;

        var matPrim = gprim.GetStage().GetPrimAtPath(matPath);
        if (!matPrim.IsValid()) return;
        var material = new UsdShadeMaterial(matPrim);

        var bindingApi = UsdShadeMaterialBindingAPI.Apply(gprim);
        bindingApi.Bind(material);
    }

    // -- Mesh --

    private static void WriteMeshTopology(UsdGeomMesh meshSchema, SceneMeshPayload payload)
    {
        // Points (point3f[]).
        var pointsAttr = meshSchema.CreatePointsAttr();
        var pointsArr = new VtVec3fArray((uint)payload.Positions.Length);
        for (int i = 0; i < payload.Positions.Length; i++)
        {
            var p = payload.Positions[i];
            pointsArr[i] = new GfVec3f(p.X, p.Y, p.Z);
        }
        pointsAttr.Set(new VtValue(pointsArr));

        // Triangulated topology: indices are already a flat triangle list. Counts is
        // [3, 3, ..., 3] of length triangleCount.
        int triCount = payload.Indices.Length / 3;
        var countsAttr = meshSchema.CreateFaceVertexCountsAttr();
        var countsArr = new VtIntArray((uint)triCount);
        for (int i = 0; i < triCount; i++) countsArr[i] = 3;
        countsAttr.Set(new VtValue(countsArr));

        var idxAttr = meshSchema.CreateFaceVertexIndicesAttr();
        var idxArr = new VtIntArray((uint)payload.Indices.Length);
        for (int i = 0; i < payload.Indices.Length; i++) idxArr[i] = payload.Indices[i];
        idxAttr.Set(new VtValue(idxArr));

        // Optional channels.
        if (payload.Normals is { Length: > 0 } normals && normals.Length == payload.Positions.Length)
        {
            var attr = meshSchema.GetPrim().CreateAttribute(new TfToken("normals"), SdfValueTypeNames.Normal3fArray);
            var arr = new VtVec3fArray((uint)normals.Length);
            for (int i = 0; i < normals.Length; i++) arr[i] = new GfVec3f(normals[i].X, normals[i].Y, normals[i].Z);
            attr.Set(new VtValue(arr));
        }

        if (payload.Uv0 is { Length: > 0 } uv0 && uv0.Length == payload.Positions.Length)
        {
            // primvars:st in TexCoord2fArray (the reader probes for this exact key).
            var attr = meshSchema.GetPrim().CreateAttribute(new TfToken("primvars:st"), SdfValueTypeNames.TexCoord2fArray);
            var arr = new VtVec2fArray((uint)uv0.Length);
            for (int i = 0; i < uv0.Length; i++) arr[i] = new GfVec2f(uv0[i].X, uv0[i].Y);
            attr.Set(new VtValue(arr));
        }
    }

    private static void WriteMeshSubsets(
        UsdStage stage,
        SdfPath meshPath,
        SceneMeshPayload payload,
        Dictionary<object, SdfPath> materialPaths)
    {
        if (payload.Subsets.Count == 0) return;

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subset in payload.Subsets)
        {
            if (subset.IndexCount <= 0 || (subset.IndexCount % 3) != 0) continue;

            // Map post-triangulation index range -> original-face indices: since the
            // writer's mesh is fully triangulated, every face is one triangle, so the
            // subset's faceIndices are simply [IndexStart/3, IndexStart/3 + 1, ...].
            int firstFace = subset.IndexStart / 3;
            int faceCount = subset.IndexCount / 3;
            var faceArr = new VtIntArray((uint)faceCount);
            for (int i = 0; i < faceCount; i++) faceArr[i] = firstFace + i;

            var name = UniqueName(SanitizeIdent(subset.Name, "Subset"), usedNames);
            var subsetPath = meshPath.AppendChild(new TfToken(name));
            var subsetSchema = UsdGeomSubset.Define(stage, subsetPath);
            subsetSchema.CreateElementTypeAttr().Set(new VtValue(TokFaceElement));
            subsetSchema.CreateFamilyNameAttr().Set(new VtValue(TokFamilyMaterialBind));
            subsetSchema.CreateIndicesAttr().Set(new VtValue(faceArr));

            if (subset.MaterialPath is not null &&
                materialPaths.TryGetValue(subset.MaterialPath, out var matPath))
            {
                var matPrim = stage.GetPrimAtPath(matPath);
                if (matPrim.IsValid())
                {
                    var material = new UsdShadeMaterial(matPrim);
                    var api = UsdShadeMaterialBindingAPI.Apply(subsetSchema.GetPrim());
                    api.Bind(material);
                }
            }
        }
    }

    // -- Camera --

    private static void WriteCameraAttrs(UsdGeomCamera camSchema, SceneCameraPayload cam)
    {
        var prim = camSchema.GetPrim();

        // Projection token (reader maps "perspective" / "orthographic").
        var projAttr = prim.CreateAttribute(new TfToken("projection"), SdfValueTypeNames.Token);
        projAttr.Set(new VtValue(new TfToken(cam.Projection == SceneProjection.Orthographic ? "orthographic" : "perspective")));

        // Physical inputs - all single-precision floats.
        prim.CreateAttribute(new TfToken("horizontalAperture"), SdfValueTypeNames.Float).Set(new VtValue(cam.HorizontalAperture));
        prim.CreateAttribute(new TfToken("verticalAperture"), SdfValueTypeNames.Float).Set(new VtValue(cam.VerticalAperture));
        prim.CreateAttribute(new TfToken("focalLength"), SdfValueTypeNames.Float).Set(new VtValue(cam.FocalLength));

        // Clip range as float2 (the reader reads GfVec2f).
        var clipAttr = prim.CreateAttribute(new TfToken("clippingRange"), SdfValueTypeNames.Float2);
        clipAttr.Set(new VtValue(new GfVec2f(cam.NearClip, cam.FarClip)));

        if (cam.FocusDistance is { } fd)
            prim.CreateAttribute(new TfToken("focusDistance"), SdfValueTypeNames.Float).Set(new VtValue(fd));
        if (cam.FStop is { } fs)
            prim.CreateAttribute(new TfToken("fStop"), SdfValueTypeNames.Float).Set(new VtValue(fs));
    }

    // -- Lights --

    private static UsdPrim WriteLight(UsdStage stage, SdfPath path, SceneLightPayload light)
    {
        UsdPrim prim;
        switch (light.Type)
        {
            case SceneLightType.Distant:
                prim = UsdLuxDistantLight.Define(stage, path).GetPrim();
                break;
            case SceneLightType.Sphere:
                var sphere = UsdLuxSphereLight.Define(stage, path);
                prim = sphere.GetPrim();
                if (light.Radius is { } sr)
                    prim.CreateAttribute(new TfToken("inputs:radius"), SdfValueTypeNames.Float).Set(new VtValue(sr));
                break;
            case SceneLightType.Disk:
                var disk = UsdLuxDiskLight.Define(stage, path);
                prim = disk.GetPrim();
                if (light.Radius is { } dr)
                    prim.CreateAttribute(new TfToken("inputs:radius"), SdfValueTypeNames.Float).Set(new VtValue(dr));
                break;
            case SceneLightType.Rect:
                var rect = UsdLuxRectLight.Define(stage, path);
                prim = rect.GetPrim();
                if (light.Width is { } w)
                    prim.CreateAttribute(new TfToken("inputs:width"), SdfValueTypeNames.Float).Set(new VtValue(w));
                if (light.Height is { } h)
                    prim.CreateAttribute(new TfToken("inputs:height"), SdfValueTypeNames.Float).Set(new VtValue(h));
                break;
            case SceneLightType.Cylinder:
                var cyl = UsdLuxCylinderLight.Define(stage, path);
                prim = cyl.GetPrim();
                if (light.Radius is { } cr)
                    prim.CreateAttribute(new TfToken("inputs:radius"), SdfValueTypeNames.Float).Set(new VtValue(cr));
                if (light.Length is { } cl)
                    prim.CreateAttribute(new TfToken("inputs:length"), SdfValueTypeNames.Float).Set(new VtValue(cl));
                break;
            case SceneLightType.Dome:
                var dome = UsdLuxDomeLight.Define(stage, path);
                prim = dome.GetPrim();
                if (!string.IsNullOrEmpty(light.DomeTexturePath))
                    prim.CreateAttribute(new TfToken("inputs:texture:file"), SdfValueTypeNames.Asset)
                        .Set(new VtValue(new SdfAssetPath(light.DomeTexturePath)));
                break;
            default:
                prim = UsdLuxSphereLight.Define(stage, path).GetPrim();
                break;
        }

        // Common UsdLux inputs (color / intensity / exposure) authored as inputs:* per the
        // UsdLux schema; the reader picks them up via UsdLuxLightAPI.
        prim.CreateAttribute(new TfToken("inputs:color"), SdfValueTypeNames.Color3f)
            .Set(new VtValue(new GfVec3f(light.Color.X, light.Color.Y, light.Color.Z)));
        prim.CreateAttribute(new TfToken("inputs:intensity"), SdfValueTypeNames.Float)
            .Set(new VtValue(light.Intensity));
        prim.CreateAttribute(new TfToken("inputs:exposure"), SdfValueTypeNames.Float)
            .Set(new VtValue(light.Exposure));

        return prim;
    }

    // -- Transforms --

    private static void WriteLocalTransform(UsdPrim prim, Transform t)
    {
        // Skip identity transforms entirely - keeps the .usda diff minimal and round-trips
        // through the reader (which returns identity when no xformOps are authored).
        if (IsIdentity(t)) return;

        var xformable = new UsdGeomXformable(prim);
        if (!xformable.GetPrim().IsValid()) return;

        var op = xformable.AddTransformOp(UsdGeomXformOp.Precision.PrecisionDouble);
        op.Set(ComposeMatrixDouble(t));
    }

    private static bool IsIdentity(Transform t)
        => t.Position == Vector3.Zero
        && t.Rotation == Quaternion.Identity
        && t.Scale == Vector3.One;

    private static GfMatrix4d ComposeMatrixDouble(Transform t)
    {
        // System.Numerics composes left-to-right (row-vector); USD's GfMatrix4d uses the
        // same row-major convention so the entries copy 1:1.
        var m = Matrix4x4.CreateScale(t.Scale)
              * Matrix4x4.CreateFromQuaternion(t.Rotation)
              * Matrix4x4.CreateTranslation(t.Position);

        var g = new GfMatrix4d(1.0);
        g.Set(
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44);
        return g;
    }

    // -- Naming --

    /// <summary>
    /// Sanitizes an arbitrary string into a USD identifier (alphanumeric + underscore,
    /// not starting with a digit). Empty / null / all-invalid input falls back to
    /// <paramref name="fallback"/>.
    /// </summary>
    private static string SanitizeIdent(string? input, string fallback)
    {
        if (string.IsNullOrEmpty(input)) return fallback;
        Span<char> buf = stackalloc char[input.Length];
        int n = 0;
        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') buf[n++] = ch;
            else buf[n++] = '_';
        }
        if (n == 0) return fallback;
        if (char.IsDigit(buf[0]))
        {
            // Prepend underscore - allocate once.
            return "_" + new string(buf[..n]);
        }
        return new string(buf[..n]);
    }

    private static string UniqueName(string baseName, HashSet<string> used)
    {
        if (used.Add(baseName)) return baseName;
        for (int i = 1; ; i++)
        {
            var candidate = baseName + "_" + i;
            if (used.Add(candidate)) return candidate;
        }
    }

    // -- Runtime --

    private static int s_runtimeReady;

    private static void EnsureRuntimeReady()
    {
        if (Interlocked.CompareExchange(ref s_runtimeReady, 1, 0) != 0) return;
        try
        {
            var (pluginDir, nativeDir) = UsdRuntimeLayout.Resolve();
            if (pluginDir is null && nativeDir is null) UsdRuntime.Initialize();
            else UsdRuntime.Initialize(pluginDir, nativeDir);
        }
        catch
        {
            Interlocked.Exchange(ref s_runtimeReady, 0);
            throw;
        }
    }
}