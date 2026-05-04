using System.Diagnostics;
using FluentAssertions;
using pxr;
using UniversalSceneDescription;
using Xunit;
using Xunit.Abstractions;

namespace Engine.Tests.Scenes.Usd;

/// <summary>
/// Throwaway spike tests that exercise the <c>UniversalSceneDescription</c> 7.0.x binding
/// surface against the bundled <c>teapot.usdz</c> asset. Their purpose is NOT to assert
/// engine behavior - it is to verify, end-to-end on the actual native runtime, every API
/// the upcoming proper <see cref="UsdSceneReader"/> implementation depends on:
/// stage open from <c>.usdz</c>, stage metadata helpers, prim traversal, mesh attribute
/// extraction (points / faceVertexCounts / faceVertexIndices / UVs / normals),
/// in-place polygon triangulation, xformable transform composition, light/camera schema
/// bindings, and parallel stage opens (AssetServer worker threading).
///
/// Each test logs structured findings via <see cref="ITestOutputHelper"/>; together they
/// form the "spike report" that locks the implementation contract for the real reader.
///
/// Once the production reader lands, the file may be deleted (or kept as a smoke test
/// that pins the binding surface against accidental package upgrades).
/// </summary>
[Trait("Category", "Spike")]
[Trait("Backend", "Usd")]
[Collection(UsdTestCollection.Name)]
public sealed class UsdReaderSpikeTests
{
    private readonly ITestOutputHelper _output;
    private readonly bool _ready;
    private readonly string? _teapotPath;

    public UsdReaderSpikeTests(ITestOutputHelper output)
    {
        _output = output;
        _ready = UsdRuntimeLayout.IsAvailable();
        if (_ready)
        {
            var (pluginDir, nativeDir) = UsdRuntimeLayout.Resolve();
            if (pluginDir is null && nativeDir is null) UsdRuntime.Initialize();
            else UsdRuntime.Initialize(pluginDir, nativeDir);
        }

        // The test runner stages Modules/Engine.Scenes/Source/teapot.usdz into
        // {AppContext.BaseDirectory}/source/teapot.usdz (see 3DEngine.Tests.csproj None block).
        var candidate = Path.Combine(AppContext.BaseDirectory, "source", "teapot.usdz");
        _teapotPath = File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Opens the bundled teapot.usdz with stock <c>UsdStage.Open(string)</c>. Validates
    /// that .usdz packaged-asset resolution works without any extra ArResolver setup on
    /// Linux/Flatpak (per the runtime logs).
    /// </summary>
    [Fact]
    public void Spike01_Open_Usdz_From_Filesystem_Path()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        if (_teapotPath is null) SkipTest.With("teapot.usdz not staged next to test binary.");

        _output.WriteLine($"[spike] Opening: {_teapotPath}");
        _output.WriteLine($"[spike] UsdStage.IsSupportedFile: {UsdStage.IsSupportedFile(_teapotPath)}");

        using var stage = UsdStage.Open(_teapotPath);

        stage.Should().NotBeNull("UsdStage.Open(path) must succeed for a .usdz package");
        var pseudoRoot = stage.GetPseudoRoot();
        pseudoRoot.IsValid().Should().BeTrue();

        var defaultPrim = stage.HasDefaultPrim() ? stage.GetDefaultPrim() : null;
        _output.WriteLine($"[spike] Has defaultPrim: {stage.HasDefaultPrim()}, defaultPrim path: {defaultPrim?.GetPath().GetString() ?? "<none>"}");
    }

    /// <summary>
    /// Verifies the stage-metadata helpers we'll need for basis/unit normalization at
    /// spawn time (per Plan §B): <c>UsdGeomGetStageUpAxis</c> and
    /// <c>UsdGeomGetStageMetersPerUnit</c>.
    /// </summary>
    [Fact]
    public void Spike02_Stage_Metadata_UpAxis_And_MetersPerUnit()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        if (_teapotPath is null) SkipTest.With("teapot.usdz not staged.");

        using var stage = UsdStage.Open(_teapotPath);

        var upAxis = UsdGeom.UsdGeomGetStageUpAxis(stage);
        var metersPerUnit = UsdGeom.UsdGeomGetStageMetersPerUnit(stage);
        var hasAuthoredMpu = UsdGeom.UsdGeomStageHasAuthoredMetersPerUnit(stage);
        var fallbackUp = UsdGeom.UsdGeomGetFallbackUpAxis();

        _output.WriteLine($"[spike] upAxis = '{upAxis}' (fallback = '{fallbackUp}')");
        _output.WriteLine($"[spike] metersPerUnit = {metersPerUnit} (authored: {hasAuthoredMpu})");

        upAxis.Should().NotBeNull();
        upAxis.ToString().Should().BeOneOf("Y", "Z");
        metersPerUnit.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Recursively walks the prim tree and tallies prim type names. Confirms our
    /// dispatcher can rely on <see cref="UsdPrim.GetTypeName"/> + <see cref="UsdPrim.GetChildren"/>.
    /// Logs the full classification of teapot.usdz so the implementation knows exactly
    /// what payload types it needs to handle for this fixture.
    /// </summary>
    [Fact]
    public void Spike03_Walk_PrimTree_And_Classify_Types()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        if (_teapotPath is null) SkipTest.With("teapot.usdz not staged.");

        using var stage = UsdStage.Open(_teapotPath);
        var typeCounts = new SortedDictionary<string, int>();
        var prims = new List<UsdPrim>();

        Walk(stage.GetPseudoRoot(), 0);
        void Walk(UsdPrim prim, int depth)
        {
            if (depth > 0)
            {
                prims.Add(prim);
                var typeName = prim.GetTypeName().ToString();
                if (string.IsNullOrEmpty(typeName)) typeName = "<untyped>";
                typeCounts.TryGetValue(typeName, out var c);
                typeCounts[typeName] = c + 1;
                _output.WriteLine($"  {new string(' ', depth * 2)}{prim.GetPath().GetString()}  type='{typeName}'");
            }
            foreach (var child in prim.GetChildren())
                Walk(child, depth + 1);
        }

        _output.WriteLine($"[spike] Visited {prims.Count} prim(s).");
        _output.WriteLine("[spike] Type histogram:");
        foreach (var (k, v) in typeCounts) _output.WriteLine($"        {k}: {v}");

        prims.Should().NotBeEmpty("teapot.usdz must contain at least one prim");
    }

    /// <summary>
    /// For every <c>Mesh</c> prim, extracts <c>points</c>, <c>faceVertexCounts</c>,
    /// <c>faceVertexIndices</c>, optional <c>normals</c> and <c>primvars:st</c>.
    /// Confirms the <c>VtValue → VtVec3fArray / VtIntArray / VtVec2fArray</c> implicit
    /// conversion path discovered via reflection. Then runs
    /// <see cref="UsdGeomMesh.Triangulate"/> in place and logs before/after counts to
    /// confirm the library handles polygon triangulation for us (plan §D step 3).
    /// </summary>
    [Fact]
    public void Spike04_Extract_Mesh_Topology_And_Triangulate()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        if (_teapotPath is null) SkipTest.With("teapot.usdz not staged.");

        using var stage = UsdStage.Open(_teapotPath);
        var meshCount = 0;
        var stPrimvarToken = new TfToken("primvars:st");
        var normalsToken = new TfToken("normals");

        foreach (var prim in EnumerateAllPrims(stage))
        {
            if (prim.GetTypeName().ToString() != "Mesh") continue;
            meshCount++;
            var mesh = new UsdGeomMesh(prim);

            // Extract attribute values by going through VtValue (no typed Get<T> in 7.0.x).
            // The implicit conversion operator pxr.VtValue->VtVec3fArray was confirmed via
            // reflection: pxr.VtValue.op_Implicit(VtValue) -> VtVec3fArray.
            VtVec3fArray points = mesh.GetPointsAttr().Get();
            VtIntArray fvCounts = mesh.GetFaceVertexCountsAttr().Get();
            VtIntArray fvIndices = mesh.GetFaceVertexIndicesAttr().Get();

            uint pointCount = points.size();
            uint faceCount = fvCounts.size();
            uint indexCount = fvIndices.size();

            // Normals (optional)
            VtVec3fArray? normals = null;
            var nAttr = prim.GetAttribute(normalsToken);
            if (nAttr.IsValid() && nAttr.HasAuthoredValue())
                normals = nAttr.Get();

            // primvars:st (UV0, optional)
            VtVec2fArray? uvs = null;
            var stAttr = prim.GetAttribute(stPrimvarToken);
            if (stAttr.IsValid() && stAttr.HasAuthoredValue())
                uvs = stAttr.Get();

            _output.WriteLine($"[spike] Mesh {prim.GetPath().GetString()}");
            _output.WriteLine($"        points={pointCount}  faces={faceCount}  faceVertexIndices={indexCount}");
            _output.WriteLine($"        normals={(normals is null ? "<none>" : normals.size().ToString())}  primvars:st={(uvs is null ? "<none>" : uvs.size().ToString())}");

            // Round-trip points to a managed array. Note: VtVec3fArray.CopyToArray(GfVec3f[])
            // throws MarshalDirectiveException at runtime in 7.0.x ("Signature is not Interop
            // compatible") - the SWIG-generated bulk-copy binding for arrays-of-struct is
            // broken. The reliable path is the indexer (one P/Invoke per element). For
            // 50k-vertex meshes this is acceptable; if it ever becomes a hot path the
            // workaround is to use the IntPtr CopyToArray overload with a pinned buffer
            // or the USD.NET IntrinsicTypeConverter helpers.
            if (pointCount > 0)
            {
                var p0 = points[0];
                var pN = points[(int)pointCount - 1];
                _output.WriteLine($"        points[0] = ({p0[0]:F4}, {p0[1]:F4}, {p0[2]:F4})  points[{pointCount-1}] = ({pN[0]:F4}, {pN[1]:F4}, {pN[2]:F4})  (indexer round-trip OK)");
            }

            // Triangulate in place. The library mutates fvIndices and fvCounts so that
            // every face becomes a triangle (count=3). After this call, indices.size()
            // == 3 * (sum of (origCount - 2) for each original face).
            uint preIndexCount = fvIndices.size();
            uint preFaceCount = fvCounts.size();
            UsdGeomMesh.Triangulate(fvIndices, fvCounts);
            uint postIndexCount = fvIndices.size();
            uint postFaceCount = fvCounts.size();
            _output.WriteLine($"        Triangulate(): indices {preIndexCount} -> {postIndexCount}, faces {preFaceCount} -> {postFaceCount}");

            // Sanity check: triangulated form should have indexCount divisible by 3, and
            // every faceVertexCount entry should equal 3.
            (postIndexCount % 3).Should().Be(0u, "triangulated index buffer must be a multiple of 3");
            for (int i = 0; i < (int)postFaceCount; i++)
                fvCounts[i].Should().Be(3, $"face {i} should be a triangle after Triangulate()");
        }

        _output.WriteLine($"[spike] Total Mesh prim(s) found in teapot.usdz: {meshCount}");
        meshCount.Should().BeGreaterThan(0, "teapot.usdz is expected to contain at least one mesh");
    }

    /// <summary>
    /// For every xformable prim, computes its local transform via
    /// <see cref="UsdGeomXformable.GetLocalTransformation(GfMatrix4d,out bool,UsdTimeCode)"/>
    /// and logs the decomposed translation/rotation/scale. Validates the path the spawn
    /// system will use (Plan §D <c>FillXform</c>).
    /// </summary>
    [Fact]
    public void Spike05_Compose_Local_Transforms_For_Every_Xformable()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        if (_teapotPath is null) SkipTest.With("teapot.usdz not staged.");

        using var stage = UsdStage.Open(_teapotPath);
        var xformableCount = 0;
        var nonIdentityCount = 0;

        foreach (var prim in EnumerateAllPrims(stage))
        {
            // UsdGeomXformable wraps any prim that derives from Xformable; constructing
            // it on a non-xformable prim leaves the wrapper invalid (GetPrim().IsValid()
            // returns false on the underlying schema check). Quick filter:
            var xform = new UsdGeomXformable(prim);
            if (!xform.GetPrim().IsValid()) continue;

            // Some prims (e.g. Material, Shader) are valid prims but not xformable;
            // calling GetLocalTransformation on them returns the identity. We still
            // count them for visibility.
            xformableCount++;

            var matrix = new GfMatrix4d(1.0); // identity placeholder
            xform.GetLocalTransformation(matrix, out bool resetsStack, UsdTimeCode.Default());

            var t = matrix.ExtractTranslation();
            var q = matrix.ExtractRotationQuat();
            // Scale via row magnitudes of the upper-3x3 (cheap; full Factor() is an option).
            var r0 = matrix.GetRow3(0);
            var r1 = matrix.GetRow3(1);
            var r2 = matrix.GetRow3(2);
            double sx = Math.Sqrt(r0[0]*r0[0] + r0[1]*r0[1] + r0[2]*r0[2]);
            double sy = Math.Sqrt(r1[0]*r1[0] + r1[1]*r1[1] + r1[2]*r1[2]);
            double sz = Math.Sqrt(r2[0]*r2[0] + r2[1]*r2[1] + r2[2]*r2[2]);

            bool isIdentity = Math.Abs(t[0]) < 1e-6 && Math.Abs(t[1]) < 1e-6 && Math.Abs(t[2]) < 1e-6
                              && Math.Abs(sx - 1) < 1e-6 && Math.Abs(sy - 1) < 1e-6 && Math.Abs(sz - 1) < 1e-6;
            if (!isIdentity) nonIdentityCount++;

            _output.WriteLine($"[spike] {prim.GetPath().GetString()}  type='{prim.GetTypeName()}'  resetsStack={resetsStack}");
            _output.WriteLine($"        T=({t[0]:F4},{t[1]:F4},{t[2]:F4})  S=({sx:F4},{sy:F4},{sz:F4})  Qreal={q.GetReal():F4}");
        }

        _output.WriteLine($"[spike] Xformable prims considered: {xformableCount}, non-identity local transforms: {nonIdentityCount}");
    }

    /// <summary>
    /// Exercises camera + light schema dispatch. We don't assert the teapot fixture
    /// has any camera/light - the test logs whether it does, and exercises the
    /// constructors / typed accessors so we can confirm the schemas link at runtime.
    /// </summary>
    [Fact]
    public void Spike06_Camera_And_Light_Schema_Bindings_Are_Reachable()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        if (_teapotPath is null) SkipTest.With("teapot.usdz not staged.");

        using var stage = UsdStage.Open(_teapotPath);
        int cameras = 0, distant = 0, sphere = 0, rect = 0, disk = 0, dome = 0, cyl = 0;

        foreach (var prim in EnumerateAllPrims(stage))
        {
            switch (prim.GetTypeName().ToString())
            {
                case "Camera":         cameras++;  _ = new UsdGeomCamera(prim); break;
                case "DistantLight":   distant++;  _ = new UsdLuxDistantLight(prim); break;
                case "SphereLight":    sphere++;   _ = new UsdLuxSphereLight(prim); break;
                case "RectLight":      rect++;     _ = new UsdLuxRectLight(prim); break;
                case "DiskLight":      disk++;     _ = new UsdLuxDiskLight(prim); break;
                case "DomeLight":      dome++;     _ = new UsdLuxDomeLight(prim); break;
                case "CylinderLight":  cyl++;      _ = new UsdLuxCylinderLight(prim); break;
            }
        }

        _output.WriteLine($"[spike] cameras={cameras}  distant={distant}  sphere={sphere}  rect={rect}  disk={disk}  dome={dome}  cylinder={cyl}");
        // Just constructing the wrappers without throwing is the assertion we care about.
    }

    /// <summary>
    /// Exercises material binding lookup via <see cref="UsdShadeMaterialBindingAPI"/>
    /// and walks the bound material's surface shader inputs. Logs whichever PBR
    /// inputs (diffuseColor, metallic, roughness, ...) are present so the
    /// production reader knows what to expect for the teapot fixture.
    /// </summary>
    [Fact]
    public void Spike07_Material_Binding_And_PreviewSurface_Inputs()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        if (_teapotPath is null) SkipTest.With("teapot.usdz not staged.");

        using var stage = UsdStage.Open(_teapotPath);
        var meshes = EnumerateAllPrims(stage).Where(p => p.GetTypeName().ToString() == "Mesh").ToList();
        _output.WriteLine($"[spike] Inspecting material bindings for {meshes.Count} mesh prim(s).");

        foreach (var meshPrim in meshes)
        {
            var bindingApi = new UsdShadeMaterialBindingAPI(meshPrim);
            var bound = bindingApi.ComputeBoundMaterial();
            var matPrim = bound.GetPrim();
            if (!matPrim.IsValid())
            {
                _output.WriteLine($"  {meshPrim.GetPath().GetString()}: no bound material.");
                continue;
            }

            _output.WriteLine($"  {meshPrim.GetPath().GetString()} -> {matPrim.GetPath().GetString()}");

            // Walk the material's child shaders and dump their input names + ids.
            foreach (var child in matPrim.GetChildren())
            {
                if (child.GetTypeName().ToString() != "Shader") continue;
                var shader = new UsdShadeShader(child);
                var idAttr = shader.GetIdAttr();
                string idText = "<no id authored>";
                if (idAttr.IsValid() && idAttr.HasAuthoredValue())
                {
                    VtValue v = idAttr.Get();
                    idText = v.GetTypeName();
                }
                _output.WriteLine($"    Shader {child.GetPath().GetString()}  idAttrType={idText}");
                foreach (var input in shader.GetInputs())
                {
                    _output.WriteLine($"      input '{input.GetBaseName()}' typeName={input.GetTypeName().GetAsToken()}");
                }
            }
        }
    }

    /// <summary>
    /// AssetServer worker threading: confirm two stages can be opened concurrently from
    /// independent threads without crashing or deadlocking. The production reader is
    /// invoked on background workers, so this is a hard prerequisite.
    /// </summary>
    [Fact]
    public void Spike08_Parallel_Stage_Opens_Are_Safe()
    {
        if (!_ready) SkipTest.With("OpenUSD native plug-in tree not found.");
        if (_teapotPath is null) SkipTest.With("teapot.usdz not staged.");

        const int parallelism = 4;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, parallelism, i =>
        {
            using var stage = UsdStage.Open(_teapotPath);
            // Touch some metadata so the open is not optimized away.
            var up = UsdGeom.UsdGeomGetStageUpAxis(stage);
            var children = stage.GetPseudoRoot().GetChildren();
            GC.KeepAlive(up);
            GC.KeepAlive(children);
        });
        sw.Stop();
        _output.WriteLine($"[spike] {parallelism} parallel UsdStage.Open(teapot.usdz) calls completed in {sw.ElapsedMilliseconds} ms (no crash, no deadlock).");
    }

    /// <summary>
    /// Verifies the engine's <see cref="AssetLoadContext"/> contract: it exposes a
    /// stream and an <see cref="AssetPath"/> but no resolved filesystem path. This
    /// is the basis for the production reader's "spool to temp file" strategy
    /// described in Plan §I.5 - confirmed by source inspection, locked here so a
    /// future change to <c>AssetLoadContext</c> that adds a filesystem path will
    /// fail this test and prompt revisiting the spool path.
    /// </summary>
    [Fact]
    public void Spike09_AssetLoadContext_Has_No_Filesystem_Path_Surface()
    {
        var ctx = new AssetLoadContext(new MemoryStream([1, 2, 3]), new AssetPath("scenes/x.usdz"), _ => default);

        var props = typeof(AssetLoadContext).GetProperties()
            .Select(p => p.Name)
            .ToArray();

        _output.WriteLine($"[spike] AssetLoadContext public properties: {string.Join(", ", props)}");
        props.Should().Contain("Path");
        props.Should().NotContain("FilePath", "spool-to-temp is required for .usdz; if a real path is added, revisit UsdSceneReader");
        props.Should().NotContain("ResolvedPath");
        props.Should().NotContain("AbsolutePath");
        ctx.GetStream().Should().NotBeNull();
    }

    private static IEnumerable<UsdPrim> EnumerateAllPrims(UsdStage stage)
    {
        var stack = new Stack<UsdPrim>();
        foreach (var root in stage.GetPseudoRoot().GetChildren()) stack.Push(root);
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            yield return p;
            foreach (var c in p.GetChildren()) stack.Push(c);
        }
    }
}