using System.Numerics;
using pxr;
using UniversalSceneDescription;

namespace Engine;

/// <summary>
/// <see cref="ISceneReader"/> for OpenUSD (<c>.usd / .usda / .usdc / .usdz</c>). Opens a
/// <c>UsdStage</c> via the bundled UniversalSceneDescription bindings, walks the prim
/// hierarchy, and emits a backend-agnostic <see cref="Scene"/> snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coordinate / unit policy:</b> the reader records <c>upAxis</c> and
/// <c>metersPerUnit</c> on <see cref="Scene.SourceCoordinateSystem"/> and
/// <see cref="Scene.SourceMetersPerUnit"/> but does <i>not</i> rotate or rescale vertex
/// data. Spawn systems apply a single root-level basis-change matrix derived from those
/// fields; this keeps a <c>read → write</c> round-trip byte-stable and avoids per-vertex
/// precision loss on large stages (cf. Plan §B and the extended remarks on <see cref="Scene"/>).
/// </para>
/// <para>
/// <b>Spool to temp file:</b> <see cref="AssetLoadContext"/> exposes only a stream
/// (Spike09 locks this contract); <c>UsdStage.Open</c> requires a filesystem path,
/// especially for <c>.usdz</c> packages whose internal asset resolution piggy-backs on the
/// archive on disk. The reader spools the stream to a temp file with the original
/// extension and disposes it after the stage closes.
/// </para>
/// <para>
/// <b>Threading:</b> called on an <see cref="AssetServer"/> background worker. The native
/// <c>UsdStage</c> is opened, traversed, and disposed entirely on this thread; the
/// returned <see cref="Scene"/> contains only managed value types and shared arrays so it
/// can cross threads safely.
/// </para>
/// <para>
/// <b>Coverage:</b> meshes (with triangulation, normals, primary UVs, material binding),
/// cameras (perspective + ortho, physical aperture/focal length), UsdLux lights
/// (distant/sphere/rect/disk/cylinder/dome), UsdPreviewSurface materials (factor inputs +
/// the texture file path on the upstream <c>UsdUVTexture</c>), and per-prim
/// <c>UsdGeomImageable.purpose</c>. <c>PointInstancer</c> prims are detected and skipped
/// with a log for now (full <see cref="SceneInstancingPayload"/> support is a follow-up
/// ticket; carrying the data losslessly is non-trivial because <c>VtMatrix4dArray</c>
/// bulk-copy is broken in the 7.0.x binding - see Spike04 notes).
/// </para>
/// </remarks>
public sealed class UsdSceneReader : ISceneReader
{
    private static readonly ILogger Logger = Log.Category("Engine.Scenes.Usd");

    private static readonly TfToken TokPrimvarsSt = new("primvars:st");
    private static readonly TfToken TokNormals = new("normals");

    /// <inheritdoc />
    public string[] Extensions => [".usd", ".usda", ".usdc", ".usdz"];

    /// <inheritdoc />
    public string FormatId => "usd";

    /// <inheritdoc />
    public Task<Scene> ReadAsync(AssetLoadContext context, SceneImportSettings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        EnsureRuntimeReady();

        string tempPath = SpoolToTempFile(context);
        try
        {
            ct.ThrowIfCancellationRequested();
            using var stage = UsdStage.Open(tempPath);
            if (stage is null)
                throw new InvalidOperationException($"UsdStage.Open returned null for '{context.Path}'.");

            return Task.FromResult(BuildScene(stage, context, settings, ct));
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    // -- Stage → Scene --

    private static Scene BuildScene(UsdStage stage, AssetLoadContext context, SceneImportSettings settings, CancellationToken ct)
    {
        var upAxis = UsdGeom.UsdGeomGetStageUpAxis(stage)?.ToString() ?? "Y";
        var metersPerUnit = UsdGeom.UsdGeomStageHasAuthoredMetersPerUnit(stage)
            ? UsdGeom.UsdGeomGetStageMetersPerUnit(stage)
            : 1.0;
        var time = settings.TimeCode.HasValue
            ? new UsdTimeCode(settings.TimeCode.Value)
            : UsdTimeCode.Default();

        var scene = new Scene
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(context.Path.Path),
            SourceCoordinateSystem = string.Equals(upAxis, "Z", StringComparison.OrdinalIgnoreCase)
                ? SceneCoordinateSystem.ZUp
                : SceneCoordinateSystem.YUp,
            SourceMetersPerUnit = metersPerUnit,
        };

        // Pre-pass: build a stage-wide path → SceneMaterialPayload cache so mesh-level
        // and UsdGeomSubset bindings both look up the same shared payload instance. The
        // pre-pass is skipped when materials are turned off via SceneImportSettings.
        var materialCache = (settings.LoadPayloads.HasFlag(LoadPayloads.Materials)
            ? UsdMaterialReader.BuildCache(stage, time, settings.MaterialResolution, ct)
            : new Dictionary<string, SceneMaterialPayload>(StringComparer.Ordinal));

        foreach (var rootPrim in stage.GetPseudoRoot().GetChildren())
        {
            ct.ThrowIfCancellationRequested();
            var node = ConvertPrim(rootPrim, settings, time, materialCache, ct);
            if (node is not null) scene.Roots.Add(node);
        }

        // One-shot summary log: lets us confirm at a glance that the stage parsed and
        // produced the expected payload mix. Single recursive walk over the
        // already-built node tree, and only runs once per scene load.
        LogSceneSummary(context, scene, materialCache.Count);

        return scene;
    }

    private static void LogSceneSummary(AssetLoadContext context, Scene scene, int materialCount)
    {
        int nodes = 0, meshes = 0, cameras = 0, lights = 0, materials = 0, totalTris = 0;
        foreach (var root in scene.Roots) Tally(root);

        Logger.Info(
            $"UsdSceneReader: '{context.Path}' parsed - upAxis={scene.SourceCoordinateSystem}, " +
            $"mpu={scene.SourceMetersPerUnit:0.###}, roots={scene.Roots.Count}, nodes={nodes}, " +
            $"meshes={meshes} ({totalTris} tris), cameras={cameras}, lights={lights}, " +
            $"materials={materials} (cache={materialCount}).");

        void Tally(SceneNode n)
        {
            nodes++;
            foreach (var c in n.Components)
            {
                switch (c)
                {
                    case SceneMeshPayload m:
                        meshes++;
                        totalTris += m.Indices.Length / 3;
                        break;
                    case SceneCameraPayload: cameras++; break;
                    case SceneLightPayload:  lights++; break;
                    case SceneMaterialPayload: materials++; break;
                }
            }
            foreach (var ch in n.Children) Tally(ch);
        }
    }

    private static SceneNode? ConvertPrim(
        UsdPrim prim,
        SceneImportSettings settings,
        UsdTimeCode time,
        Dictionary<string, SceneMaterialPayload> materialCache,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!prim.IsValid()) return null;

        var typeName = prim.GetTypeName().ToString();
        var purpose = ResolvePurpose(prim);
        bool purposeIncluded = settings.IncludePurposes.HasFlag(PurposeFlag(purpose));

        var node = new SceneNode
        {
            Name = prim.GetName().ToString(),
            SourcePath = prim.GetPath().GetString(),
            Purpose = purpose,
            Enabled = purposeIncluded && IsPrimVisible(prim, time),
            LocalTransform = ReadLocalTransform(prim, time),
        };

        if (purposeIncluded)
            AttachPayloads(prim, typeName, node, settings, time, materialCache);

        // Recurse regardless: an excluded parent purpose does not strictly inherit; spawn
        // systems make the final per-node decision via SceneNode.Purpose + their own mask.
        foreach (var child in prim.GetChildren())
        {
            ct.ThrowIfCancellationRequested();
            var childNode = ConvertPrim(child, settings, time, materialCache, ct);
            if (childNode is not null) node.Children.Add(childNode);
        }

        return node;
    }

    private static void AttachPayloads(
        UsdPrim prim,
        string typeName,
        SceneNode node,
        SceneImportSettings settings,
        UsdTimeCode time,
        Dictionary<string, SceneMaterialPayload> materialCache)
    {
        switch (typeName)
        {
            case "Mesh" when settings.LoadPayloads.HasFlag(LoadPayloads.Meshes):
                var mesh = ReadMesh(prim, time, materialCache, settings);
                if (mesh is not null)
                {
                    node.Components.Add(mesh);
                    if (settings.MaterialResolution != MaterialNetworkResolution.None &&
                        settings.LoadPayloads.HasFlag(LoadPayloads.Materials))
                    {
                        // Mesh-level binding (always attached when present so a spawner
                        // that doesn't grok subsets still gets a single material payload).
                        var meshMatPath = UsdMaterialReader.ResolveBoundMaterialPath(prim);
                        if (meshMatPath is not null && materialCache.TryGetValue(meshMatPath, out var bound))
                            node.Components.Add(bound);

                        // Subset-bound materials: attach each unique payload referenced by
                        // a SceneMeshSubset.MaterialPath so the spawner has them in the
                        // node's component bag without re-walking the cache.
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        if (meshMatPath is not null) seen.Add(meshMatPath);
                        foreach (var subset in mesh.Subsets)
                        {
                            if (subset.MaterialPath is null) continue;
                            if (!seen.Add(subset.MaterialPath)) continue;
                            if (materialCache.TryGetValue(subset.MaterialPath, out var subMat))
                                node.Components.Add(subMat);
                        }
                    }
                }
                break;

            case "Camera" when settings.LoadPayloads.HasFlag(LoadPayloads.Cameras):
                var cam = ReadCamera(prim, time);
                if (cam is not null) node.Components.Add(cam);
                break;

            case "DistantLight" or "SphereLight" or "RectLight" or "DiskLight"
                or "CylinderLight" or "DomeLight"
                when settings.LoadPayloads.HasFlag(LoadPayloads.Lights):
                node.Components.Add(ReadLight(prim, typeName, time));
                break;

            case "PointInstancer" when settings.LoadPayloads.HasFlag(LoadPayloads.Instancing):
                // VtMatrix4dArray bulk-copy is broken in the 7.0.x binding; carrying
                // instancing data losslessly requires the per-element indexer round-trip
                // discovered in Spike04. Track separately and ship in a follow-up.
                Logger.Debug($"UsdSceneReader: PointInstancer at '{node.SourcePath}' detected but not yet materialized into SceneInstancingPayload.");
                break;
        }
    }

    // -- Transforms / visibility / purpose --

    private static Transform ReadLocalTransform(UsdPrim prim, UsdTimeCode time)
    {
        var xform = new UsdGeomXformable(prim);
        if (!xform.GetPrim().IsValid())
            return new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One };

        var matrix = new GfMatrix4d(1.0);
        xform.GetLocalTransformation(matrix, out _, time);
        return DecomposeMatrix(matrix);
    }

    /// <summary>
    /// Decomposes a USD <c>GfMatrix4d</c> into translation/rotation/scale. Row magnitudes
    /// give the per-axis scale; <c>ExtractRotationQuat</c> handles un-scaling internally.
    /// </summary>
    private static Transform DecomposeMatrix(GfMatrix4d m)
    {
        var t = m.ExtractTranslation();
        var r0 = m.GetRow3(0);
        var r1 = m.GetRow3(1);
        var r2 = m.GetRow3(2);
        double sx = Math.Sqrt(r0[0] * r0[0] + r0[1] * r0[1] + r0[2] * r0[2]);
        double sy = Math.Sqrt(r1[0] * r1[0] + r1[1] * r1[1] + r1[2] * r1[2]);
        double sz = Math.Sqrt(r2[0] * r2[0] + r2[1] * r2[1] + r2[2] * r2[2]);
        var q = m.ExtractRotationQuat();
        var img = q.GetImaginary();

        return new Transform
        {
            Position = new Vector3((float)t[0], (float)t[1], (float)t[2]),
            Rotation = new Quaternion((float)img[0], (float)img[1], (float)img[2], (float)q.GetReal()),
            Scale = new Vector3((float)sx, (float)sy, (float)sz),
        };
    }

    private static bool IsPrimVisible(UsdPrim prim, UsdTimeCode time)
    {
        var imageable = new UsdGeomImageable(prim);
        if (!imageable.GetPrim().IsValid()) return true;
        var visAttr = imageable.GetVisibilityAttr();
        if (!visAttr.IsValid() || !visAttr.HasAuthoredValue()) return true;
        try
        {
            VtValue v = visAttr.Get(time);
            var token = (TfToken)v;
            return token.ToString() != "invisible";
        }
        catch { return true; }
    }

    private static ScenePurpose ResolvePurpose(UsdPrim prim)
    {
        var imageable = new UsdGeomImageable(prim);
        if (!imageable.GetPrim().IsValid()) return ScenePurpose.Default;
        var attr = imageable.GetPurposeAttr();
        if (!attr.IsValid() || !attr.HasAuthoredValue()) return ScenePurpose.Default;
        try
        {
            VtValue v = attr.Get();
            return ((TfToken)v).ToString() switch
            {
                "render" => ScenePurpose.Render,
                "proxy"  => ScenePurpose.Proxy,
                "guide"  => ScenePurpose.Guide,
                _        => ScenePurpose.Default,
            };
        }
        catch { return ScenePurpose.Default; }
    }

    private static ScenePurposeMask PurposeFlag(ScenePurpose p) => p switch
    {
        ScenePurpose.Render => ScenePurposeMask.Render,
        ScenePurpose.Proxy  => ScenePurposeMask.Proxy,
        ScenePurpose.Guide  => ScenePurposeMask.Guide,
        _                   => ScenePurposeMask.Default,
    };

    // -- Mesh --

    private static SceneMeshPayload? ReadMesh(
        UsdPrim prim,
        UsdTimeCode time,
        IReadOnlyDictionary<string, SceneMaterialPayload> _,
        SceneImportSettings settings)
    {
        var mesh = new UsdGeomMesh(prim);
        VtVec3fArray pointsVt = mesh.GetPointsAttr().Get(time);
        VtIntArray fvCounts = mesh.GetFaceVertexCountsAttr().Get(time);
        VtIntArray fvIndices = mesh.GetFaceVertexIndicesAttr().Get(time);

        uint pointCount = pointsVt.size();
        if (pointCount == 0 || fvIndices.size() == 0) return null;

        // Snapshot the *original* per-face vertex counts before triangulating: subset
        // index lists reference original face indices and the prefix-sum-of-tris is what
        // we need to remap them to post-triangulation index ranges.
        uint origFaceCount = fvCounts.size();
        var originalCounts = new int[origFaceCount];
        for (int i = 0; i < origFaceCount; i++) originalCounts[i] = fvCounts[i];

        VtVec3fArray? normalsVt = null;
        var nAttr = prim.GetAttribute(TokNormals);
        if (nAttr.IsValid() && nAttr.HasAuthoredValue())
        {
            try { normalsVt = nAttr.Get(time); } catch { /* ignore */ }
        }

        VtVec2fArray? uvVt = null;
        var stAttr = prim.GetAttribute(TokPrimvarsSt);
        if (stAttr.IsValid() && stAttr.HasAuthoredValue())
        {
            try { uvVt = stAttr.Get(time); } catch { /* ignore */ }
        }

        // Triangulate in place: faceVertexCounts becomes all 3s, faceVertexIndices.size()
        // becomes a multiple of 3.
        UsdGeomMesh.Triangulate(fvIndices, fvCounts);
        uint indexCount = fvIndices.size();

        // Per-element indexer round-trip (Spike04 notes: bulk CopyToArray on arrays-of-
        // struct throws MarshalDirectiveException in the 7.0.x binding).
        var positions = new Vector3[pointCount];
        for (int i = 0; i < pointCount; i++)
        {
            var p = pointsVt[i];
            positions[i] = new Vector3(p[0], p[1], p[2]);
        }

        var indices = new int[indexCount];
        for (int i = 0; i < indexCount; i++)
            indices[i] = fvIndices[i];

        // Per-vertex (varying / vertex interpolation) channels share Positions' layout.
        // Face-varying channels need re-indexing through Triangulate's mapping; deferred.
        Vector3[]? normals = null;
        if (normalsVt is not null && normalsVt.size() == pointCount)
        {
            normals = new Vector3[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                var n = normalsVt[i];
                normals[i] = new Vector3(n[0], n[1], n[2]);
            }
        }

        Vector2[]? uv0 = null;
        if (uvVt is not null && uvVt.size() == pointCount)
        {
            uv0 = new Vector2[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                var uv = uvVt[i];
                uv0[i] = new Vector2(uv[0], uv[1]);
            }
        }

        // Build prefix sums: triPrefix[i] is the starting *triangle* index of original
        // face i in the post-Triangulate buffer; triPrefix[origFaceCount] is the total
        // triangle count. Polygon of N verts contributes (N - 2) triangles via fan;
        // degenerate faces (N < 3) contribute 0 (defensive).
        var triPrefix = new int[origFaceCount + 1];
        for (int i = 0; i < origFaceCount; i++)
        {
            int contrib = originalCounts[i] >= 3 ? originalCounts[i] - 2 : 0;
            triPrefix[i + 1] = triPrefix[i] + contrib;
        }

        // Material subsets: only meaningful when materials are enabled. Each subset's
        // face index list is mapped through triPrefix into one or more contiguous index
        // ranges in the triangulated buffer.
        IReadOnlyList<SceneMeshSubset> subsets = Array.Empty<SceneMeshSubset>();
        if (settings.LoadPayloads.HasFlag(LoadPayloads.Materials) &&
            settings.MaterialResolution != MaterialNetworkResolution.None)
        {
            subsets = ExtractMaterialBindSubsets(prim, time, triPrefix);
        }

        return new SceneMeshPayload
        {
            Name = prim.GetName().ToString(),
            Positions = positions,
            Indices = indices,
            Normals = normals,
            Uv0 = uv0,
            Subsets = subsets,
            LocalBounds = SceneBounds.FromPositions(positions),
        };
    }

    /// <summary>
    /// Enumerates <c>UsdGeomSubset</c> children with <c>familyName == "materialBind"</c>
    /// and converts each into one or more <see cref="SceneMeshSubset"/> entries by mapping
    /// the original face indices through <paramref name="triPrefix"/> (the prefix-sum-of-
    /// triangles built in <see cref="ReadMesh"/>). Non-contiguous face lists produce
    /// multiple subsets sharing the same <c>Name</c>/<c>MaterialPath</c>.
    /// </summary>
    private static IReadOnlyList<SceneMeshSubset> ExtractMaterialBindSubsets(
        UsdPrim meshPrim,
        UsdTimeCode time,
        int[] triPrefix)
    {
        List<SceneMeshSubset>? result = null;
        var bound = new bool[triPrefix.Length - 1]; // covered-face mask, for "remainder" detection

        foreach (var child in meshPrim.GetChildren())
        {
            UsdGeomSubset subset;
            try
            {
                subset = new UsdGeomSubset(child);
                if (!subset.GetPrim().IsValid()) continue;
            }
            catch { continue; }

            // familyName check
            string family = "";
            try
            {
                var fa = subset.GetFamilyNameAttr();
                if (fa.IsValid() && fa.HasAuthoredValue())
                {
                    VtValue fv = fa.Get();
                    family = ((TfToken)fv).ToString();
                }
            }
            catch { /* leave empty */ }
            if (family != "materialBind") continue;

            // indices (face indices into the *original* face list)
            int[] faceIndices;
            try
            {
                VtIntArray vt = subset.GetIndicesAttr().Get(time);
                faceIndices = new int[vt.size()];
                for (int i = 0; i < faceIndices.Length; i++) faceIndices[i] = vt[i];
            }
            catch { continue; }

            if (faceIndices.Length == 0) continue;
            Array.Sort(faceIndices);

            var matPath = UsdMaterialReader.ResolveBoundMaterialPath(child);
            var name = child.GetName().ToString();

            // Walk the sorted face indices and emit one SceneMeshSubset per contiguous run
            // of (face, face+1, face+2, ...). Each run maps to a contiguous index range
            // [triPrefix[runStart], triPrefix[runEndExclusive]) * 3 in the triangulated
            // buffer.
            result ??= new List<SceneMeshSubset>();
            int runStart = faceIndices[0];
            int prev = runStart;
            for (int k = 1; k <= faceIndices.Length; k++)
            {
                if (k < faceIndices.Length && faceIndices[k] == prev + 1)
                {
                    prev = faceIndices[k];
                    continue;
                }
                // Emit run [runStart, prev].
                int runEndExclusive = prev + 1;
                if (runStart >= 0 && runEndExclusive <= triPrefix.Length - 1)
                {
                    int triStart = triPrefix[runStart];
                    int triEnd   = triPrefix[runEndExclusive];
                    int idxStart = triStart * 3;
                    int idxCount = (triEnd - triStart) * 3;
                    if (idxCount > 0)
                        result.Add(new SceneMeshSubset(name, idxStart, idxCount, matPath));

                    for (int f = runStart; f < runEndExclusive; f++) bound[f] = true;
                }

                if (k < faceIndices.Length)
                {
                    runStart = faceIndices[k];
                    prev = runStart;
                }
            }
        }

        if (result is null || result.Count == 0) return Array.Empty<SceneMeshSubset>();

        // Synthesize a "remainder" subset for any faces not covered by an explicit
        // materialBind subset, bound to the mesh-level material so the renderer can keep
        // a complete coverage of the index buffer.
        var meshMat = UsdMaterialReader.ResolveBoundMaterialPath(meshPrim);
        int faceCount = bound.Length;
        int rs = 0;
        while (rs < faceCount)
        {
            while (rs < faceCount && bound[rs]) rs++;
            if (rs >= faceCount) break;
            int re = rs;
            while (re < faceCount && !bound[re]) re++;

            int triStart = triPrefix[rs];
            int triEnd   = triPrefix[re];
            int idxCount = (triEnd - triStart) * 3;
            if (idxCount > 0)
                result.Add(new SceneMeshSubset("__remainder", triStart * 3, idxCount, meshMat));
            rs = re;
        }

        return result;
    }

    // -- Material --
    //
    // Material extraction lives in UsdMaterialReader (stage-wide pre-pass + connection-
    // followed UsdPreviewSurface walk). Mesh-level + UsdGeomSubset bindings are resolved
    // through that cache by AttachPayloads / ExtractMaterialBindSubsets above.

    // -- Camera --

    private static SceneCameraPayload? ReadCamera(UsdPrim prim, UsdTimeCode time)
    {
        var cam = new UsdGeomCamera(prim);
        if (!cam.GetPrim().IsValid()) return null;

        var projection = SceneProjection.Perspective;
        var projAttr = cam.GetProjectionAttr();
        if (projAttr.IsValid() && projAttr.HasAuthoredValue())
        {
            try
            {
                VtValue v = projAttr.Get(time);
                if (((TfToken)v).ToString() == "orthographic")
                    projection = SceneProjection.Orthographic;
            }
            catch { /* keep perspective */ }
        }

        return new SceneCameraPayload
        {
            Name = prim.GetName().ToString(),
            Projection = projection,
            HorizontalAperture = ReadFloat(cam.GetHorizontalApertureAttr(), time, 20.955f),
            VerticalAperture = ReadFloat(cam.GetVerticalApertureAttr(), time, 15.2908f),
            FocalLength = ReadFloat(cam.GetFocalLengthAttr(), time, 50f),
            NearClip = ReadClip(cam.GetClippingRangeAttr(), time, 0.1f, takeMin: true),
            FarClip = ReadClip(cam.GetClippingRangeAttr(), time, 1000f, takeMin: false),
            FocusDistance = ReadOptionalFloat(cam.GetFocusDistanceAttr(), time),
            FStop = ReadOptionalFloat(cam.GetFStopAttr(), time),
        };
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

    private static float ReadClip(UsdAttribute attr, UsdTimeCode time, float fallback, bool takeMin)
    {
        if (!attr.IsValid() || !attr.HasAuthoredValue()) return fallback;
        try
        {
            VtValue v = attr.Get(time);
            GfVec2f pair = v;
            return takeMin ? pair[0] : pair[1];
        }
        catch { return fallback; }
    }

    // -- Lights --

    private static SceneLightPayload ReadLight(UsdPrim prim, string typeName, UsdTimeCode time)
    {
        var lightApi = new UsdLuxLightAPI(prim);
        TryGetFloat3FromAttr(lightApi.GetColorAttr(), time, out var color);
        float intensity = ReadFloat(lightApi.GetIntensityAttr(), time, 1f);
        float exposure = ReadFloat(lightApi.GetExposureAttr(), time, 0f);

        float? radius = null, width = null, height = null, length = null;
        string? domeTexture = null;

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
                width = ReadOptionalFloat(rect.GetWidthAttr(), time);
                height = ReadOptionalFloat(rect.GetHeightAttr(), time);
                break;
            case "CylinderLight":
                var cyl = new UsdLuxCylinderLight(prim);
                radius = ReadOptionalFloat(cyl.GetRadiusAttr(), time);
                length = ReadOptionalFloat(cyl.GetLengthAttr(), time);
                break;
            case "DomeLight":
                var dome = new UsdLuxDomeLight(prim);
                var fileAttr = dome.GetTextureFileAttr();
                if (fileAttr.IsValid() && fileAttr.HasAuthoredValue())
                {
                    try
                    {
                        VtValue v = fileAttr.Get(time);
                        SdfAssetPath ap = v;
                        var resolved = ap.GetResolvedPath();
                        domeTexture = !string.IsNullOrEmpty(resolved) ? resolved : ap.GetAssetPath();
                    }
                    catch { /* leave null */ }
                }
                break;
        }

        return new SceneLightPayload
        {
            Name = prim.GetName().ToString(),
            Type = typeName switch
            {
                "DistantLight"  => SceneLightType.Distant,
                "SphereLight"   => SceneLightType.Sphere,
                "RectLight"     => SceneLightType.Rect,
                "DiskLight"     => SceneLightType.Disk,
                "CylinderLight" => SceneLightType.Cylinder,
                "DomeLight"     => SceneLightType.Dome,
                _               => SceneLightType.Sphere,
            },
            Color = color,
            Intensity = intensity,
            Exposure = exposure,
            Radius = radius,
            Width = width,
            Height = height,
            Length = length,
            DomeTexturePath = domeTexture,
        };
    }

    private static bool TryGetFloat3FromAttr(UsdAttribute attr, UsdTimeCode time, out Vector3 value)
    {
        if (!attr.IsValid() || !attr.HasAuthoredValue()) { value = Vector3.One; return false; }
        try
        {
            VtValue v = attr.Get(time);
            GfVec3f vec = v;
            value = new Vector3(vec[0], vec[1], vec[2]);
            return true;
        }
        catch { value = Vector3.One; return false; }
    }

    // -- Runtime / I/O plumbing --

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

    private static string SpoolToTempFile(AssetLoadContext context)
    {
        var ext = context.Path.Extension;
        if (string.IsNullOrEmpty(ext)) ext = ".usda";
        var tempPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"usdspool-{Guid.NewGuid():N}{ext}");

        var stream = context.GetStream();
        if (stream.CanSeek) stream.Position = 0;
        using (var file = File.Create(tempPath))
            stream.CopyTo(file);
        return tempPath;
    }

    private static void TryDeleteTempFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Logger.Debug($"UsdSceneReader: failed to delete temp '{path}': {ex.Message}"); }
    }
}