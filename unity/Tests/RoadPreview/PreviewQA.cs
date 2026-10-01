using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class PreviewQA
{
    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-previewSourceRoot") + 1];
        string result = root + "/Temp/RoadPreviewQA/result.txt";
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (o, e) =>
            {
                foreach (string dir in new[] { "/Temp/bin/Debug/", "/Library/ScriptAssemblies/", "/Assets/Plugins/" })
                {
                    string path = root + dir + new AssemblyName(e.Name).Name + ".dll";
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            var asm = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            var masterType = asm.GetType("MasterDataStore");
            var master = new GameObject("Master").AddComponent(masterType);
            masterType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, master);
            var dict = (IDictionary)masterType.GetField("pathMasterById", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(master);
            var itemType = asm.GetType("PathMasterItem");
            var item = Activator.CreateInstance(itemType);
            itemType.GetField("width").SetValue(item, 8f);
            itemType.GetField("follow_terrain").SetValue(item, false);
            dict.Add(100, item);
            var type = asm.GetType("RoadRenderer");
            var renderer = new GameObject("Preview").AddComponent(type);
            var material = new Material(Shader.Find("Unlit/Color"));
            type.GetMethod("CreatePreviewMeshComponents").Invoke(renderer, new object[] { material });
            var before = new List<Vector3> { new Vector3(0, 10, 0), new Vector3(0, 10, 20) };
            var moving = new List<Vector3> { before[1], new Vector3(0, 10, 40) };
            void Update(List<Vector3> fixedPoints, List<Vector3> active, List<Vector3> after = null)
            {
                type.GetMethod("UpdateSections").Invoke(renderer, new object[] {
                    before, fixedPoints, active, after, 1, 100, true, 0,
                    new float[] { -5, 5 }, new int[] { 0, 0 }, "both", null, "elevated" });
            }
            Update(before, moving);
            var sections = (Array)type.GetField("sections", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(renderer);
            var fixedObject = ((Component)sections.GetValue(0)).transform.GetChild(0).gameObject;
            var filter = ((Component)sections.GetValue(1)).GetComponent<MeshFilter>();
            Mesh mesh = filter.sharedMesh;
            for (int i = 0; i < 100; i++)
            {
                moving[1] = new Vector3(i, 10 + i * 0.1f, 40);
                Update(before, moving);
                Require(fixedObject == ((Component)sections.GetValue(0)).transform.GetChild(0).gameObject, "Detailed prefix was rebuilt");
                Require(mesh == filter.sharedMesh, "Moving mesh was replaced");
                Require(mesh.vertexCount == 8, "Double track strip vertex count");
                foreach (int index in mesh.triangles) Require(index >= 0 && index < mesh.vertexCount, "Invalid triangle");
                var v = mesh.vertices;
                Require(Vector3.Cross(v[1] - v[0], v[2] - v[0]).y > 0f, "Strip winding");
            }
            var after = new List<Vector3> { moving[1], moving[1] + Vector3.forward * 20 };
            Update(before, moving, after);
            var suffix = ((Component)sections.GetValue(2)).transform.GetChild(0).gameObject;
            moving[0] += Vector3.right;
            Update(before, moving, after);
            Require(suffix == ((Component)sections.GetValue(2)).transform.GetChild(0).gameObject, "Detailed suffix was rebuilt");
            Update(before, null);
            Require(filter.sharedMesh == null, "Moving mesh remained after release");
            Update(before, moving);
            Require(filter.sharedMesh != null, "Moving mesh did not resume");
            type.GetMethod("ResetPreview").Invoke(renderer, null);
            Require(filter.sharedMesh == null, "Reset left moving mesh attached");
            Require(before.Count == 2, "Reset mutated source points");
            CheckManager(asm, renderer);
            CheckAlignment(asm, item);
            CheckControlHeights(asm);
            CheckElevatedTerrainIndependence(asm, item);
            CheckAlignmentLabels(asm);
            CheckAuthoritativeAlignment(asm);
            CheckRouteProposals(asm, item);
            CheckMaxSpan(asm, item);
            CheckSeaLakeCrossings(asm);
            CheckPowerLinePreview(asm, renderer);
            File.WriteAllText(result, "PASS: 100 preview updates; alignment curves, grades and controls; route proposal apply, undo, fixed endpoints, stale revisions, generated-point promotion and editing phase; sea and lake crossings; base_height power line preview height and waypoint pass-through.");
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(result, ex.ToString());
            EditorApplication.Exit(1);
        }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void CheckManager(Assembly asm, Component renderer)
    {
        var type = asm.GetType("RoadPlanManager");
        var manager = new GameObject("Manager").AddComponent(type);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        void Set(string name, object value) { type.GetField(name, flags).SetValue(manager, value); }
        Set("roadRenderer", renderer);
        type.GetField("PathSubtype").SetValue(manager, 100);
        var anchors = (IList)type.GetField("points", flags).GetValue(manager);
        var pointType = asm.GetType("PathBuildPoint");
        var roleType = asm.GetType("PathPointRole");
        var samples = new List<Vector3>();
        for (int i = 0; i < 7; i++)
        {
            var p = new Vector3(0, 10, i * 20);
            anchors.Add(Activator.CreateInstance(pointType, p, Enum.Parse(roleType, i == 0 ? "Start" : "Waypoint")));
            samples.Add(p);
        }
        var phaseType = asm.GetType("RoadEditorPhase");
        Set("phase", Enum.Parse(phaseType, "Placing"));
        var placedSamples = new List<Vector3>(samples) { new Vector3(10, 10, 140) };
        var update = type.GetMethod("UpdatePreviewSections", flags);
        update.Invoke(manager, new object[] { placedSamples });
        var sections = (Array)renderer.GetType().GetField("sections", flags).GetValue(renderer);
        var previewField = renderer.GetType().GetField("currentPreviewObject", flags);
        var prefix = (GameObject)previewField.GetValue(sections.GetValue(0));
        Require(prefix != null && prefix.activeSelf, "Missing active placed prefix");
        placedSamples[7] += Vector3.right;
        update.Invoke(manager, new object[] { placedSamples });
        Require(prefix == (GameObject)previewField.GetValue(sections.GetValue(0)), "Manager rebuilt placed prefix on hover");
        Set("phase", Enum.Parse(phaseType, "Editing"));
        Set("isDragging", true);
        Set("dragPointIndex", 3);
        Set("dragTarget", Enum.Parse(type.GetNestedType("DragTarget", BindingFlags.NonPublic), "Point"));
        update.Invoke(manager, new object[] { samples });
        var movingFilter = ((Component)sections.GetValue(1)).GetComponent<MeshFilter>();
        Require(movingFilter.sharedMesh != null, "Drag has no strip");
        Require(((Component)sections.GetValue(2)).transform.childCount > 0, "Drag dropped suffix");
        Set("isDragging", false);
        Set("dragPointIndex", -1);
        update.Invoke(manager, new object[] { samples });
        Require(movingFilter.sharedMesh == null, "Release did not restore detail");
        type.GetField("Curved").SetValue(manager, true);
        var curveSamples = new List<Vector3>();
        for (int i = 0; i <= 102; i++) curveSamples.Add(new Vector3(0, 10, i * 120f / 102f));
        Set("isDragging", true);
        Set("dragPointIndex", 3);
        update.Invoke(manager, new object[] { curveSamples });
        var cacheField = renderer.GetType().GetField("lastPreviewPoints", flags);
        Require(((List<Vector3>)cacheField.GetValue(sections.GetValue(0))).Count == 18, "Curved anchor drag omitted adjacent tangent influence");
        Set("dragTarget", Enum.Parse(type.GetNestedType("DragTarget", BindingFlags.NonPublic), "ControlIn"));
        update.Invoke(manager, new object[] { curveSamples });
        Require(((List<Vector3>)cacheField.GetValue(sections.GetValue(0))).Count == 35, "Control drag range");
        Set("isDragging", false);
        Set("phase", Enum.Parse(phaseType, "Placing"));
        curveSamples.Add(new Vector3(40, 10, 140));
        update.Invoke(manager, new object[] { curveSamples });
        Require(((List<Vector3>)cacheField.GetValue(sections.GetValue(0))).Count == 86, "Hover tangent must include preceding span");
        prefix = (GameObject)previewField.GetValue(sections.GetValue(0));
        var movingPoints = (List<Vector3>)type.GetMethod("GetMovingPreviewPoints", flags)
            .Invoke(manager, new object[] { curveSamples, 5, 6, true });
        Require(movingPoints.Exists(p => p.x < -0.1f), "Preceding span did not bend toward hover tangent");
        Require(movingPoints.Exists(p => p.z > 120 && Math.Abs(p.x - 2 * (p.z - 120)) > 0.1f), "Hover span is still a straight chord");
        Require(movingPoints[movingPoints.Count - 1] == curveSamples[103], "Curve missed hover endpoint");
        Require(pointType.GetField("control_in").GetValue(anchors[6]) == null, "Hover changed saved controls");
        curveSamples[103] += Vector3.right * 10;
        update.Invoke(manager, new object[] { curveSamples });
        Require(prefix == (GameObject)previewField.GetValue(sections.GetValue(0)), "Bezier hover rebuilt stable detail");
        type.GetMethod("RecomputeAllControlPoints", flags).Invoke(manager, new object[] { false });
        Require(pointType.GetField("control_in").GetValue(anchors[6]) != null, "Placed waypoint has no incoming control");
    }
    private static void CheckAlignment(Assembly asm, object master)
    {
        var type = asm.GetType("RoadPlanManager");
        var manager = new GameObject("AlignmentManager").AddComponent(type);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        void Set(string name, object value) => type.GetField(name, flags).SetValue(manager, value);
        object Call(string name, params object[] args) => type.GetMethod(name, flags).Invoke(manager, args);
        float Metric(string name) => (float)type.GetProperty(name).GetValue(manager);
        bool State(string name) => (bool)type.GetProperty(name).GetValue(manager);
        void Master(string name, object value) => master.GetType().GetField(name).SetValue(master, value);
        Set("PathSubtype", 100);
        Set("PathType", 4);
        Set("phase", Enum.Parse(asm.GetType("RoadEditorPhase"), "Editing"));
        Master("min_curve_radius", 100f);
        Master("max_grade", 4.5f);
        Master("smooth_factor", 80f);
        var anchors = (IList)type.GetField("points", flags).GetValue(manager);
        var pt = asm.GetType("PathBuildPoint");
        var role = asm.GetType("PathPointRole");
        object Add(Vector3 p, string kind)
        {
            var anchor = Activator.CreateInstance(pt, p, Enum.Parse(role, kind));
            anchors.Add(anchor);
            return anchor;
        }
        void Clear(bool curved)
        {
            anchors.Clear();
            Set("Curved", curved);
            Call("InvalidateShapeCache");
        }
        void Curve(float scale)
        {
            Clear(true);
            var a = Add(new Vector3(0, 6, 0), "Start");
            var b = Add(new Vector3(1000 * scale, 6, 100 * scale), "End");
            pt.GetField("control_out").SetValue(a, new Vector3(1000f / 3 * scale, 6, 0));
            pt.GetField("control_in").SetValue(b, new Vector3(2000f / 3 * scale, 6, 100 * scale));
            anchors[0] = a;
            anchors[1] = b;
        }
        // 1kmの緩いベジエを固定17分割で測ると半径が約321mに縮む回帰を検出する。
        Curve(1);
        float radius = Metric("AlignmentMinRadius");
        Require(radius > 1550 && radius < 1800, "Gentle Bezier radius: " + radius);
        Require(Metric("AlignmentMaxGradePct") < 0.001f, "Level viaduct acquired a grade");
        Require(!State("HasAlignmentViolation"), "Gentle level viaduct rejected");
        // 地図上の位置だけで半径が変化しないこと。判定用再標本化まで相対座標を維持する。
        foreach (float offset in new[] { 10000f, 50000f, 100000f })
        {
            Curve(1);
            Vector3 shift = new Vector3(offset, 0, offset);
            for (int i = 0; i < anchors.Count; i++)
            {
                var a = anchors[i];
                pt.GetField("position").SetValue(a, (Vector3)pt.GetField("position").GetValue(a) + shift);
                foreach (string key in new[] { "control_in", "control_out" })
                {
                    var c = pt.GetField(key).GetValue(a);
                    if (c != null) pt.GetField(key).SetValue(a, (Vector3)c + shift);
                }
                anchors[i] = a;
            }
            Call("InvalidateShapeCache");
            float translated = Metric("AlignmentMinRadius");
            Require(Mathf.Abs(translated - radius) < radius * 0.04f, "World position changed curve radius: " + translated);
        }
        Curve(0.05f);
        Require(State("HasCurveViolation") && !State("HasGradeViolation"), "Tight curve must warn independently");
        Clear(false);
        for (int x = 0; x <= 300; x++)
            Add(new Vector3(x, 6 + 1.5f * Mathf.Exp(-Mathf.Pow((x - 150) / 15f, 2)), 0),
                x == 0 ? "Start" : x == 300 ? "End" : "Waypoint");
        float smoothed = Metric("AlignmentMaxGradePct");
        Require(smoothed > 8 && State("HasGradeViolation"), "Non-follow bump must use actual deck grade: " + smoothed);
        Master("smooth_factor", 10f);
        Call("InvalidateShapeCache");
        Require(Mathf.Abs(Metric("AlignmentMaxGradePct") - smoothed) < 0.001f, "Earthwork smoothing changed elevated grade");
        Master("smooth_factor", 80f);
        Clear(false);
        Add(new Vector3(0, 6, 0), "Start");
        Add(new Vector3(500, 41, 0), "End");
        float steep = Metric("AlignmentMaxGradePct");
        Require(steep > 6.9f && steep < 7.1f && State("HasGradeViolation"), "Sustained steep grade must warn: " + steep);
        Require(!State("HasCurveViolation"), "Straight grade warned as curve");
        Clear(false);
        Add(new Vector3(0, 60, 0), "Start");
        Add(new Vector3(200, 60, 0), "Waypoint");
        Add(new Vector3(400, 80, 0), "End");
        var warnings = (List<List<Vector3>>)Call("CheckAlignmentViolations");
        Require(State("HasGradeViolation"), "Real ramp must still warn");
        foreach (var run in warnings)
        foreach (var p in run)
            Require(p.x >= 199.99f, "Ramp grade leaked onto level deck at " + p.x);
        Clear(false);
        Add(new Vector3(0, 6, 0), "Start");
        Add(new Vector3(0.3f, 6, 0), "Waypoint");
        Add(new Vector3(100.2f, 6, 0), "End");
        var samples = (List<Vector3>)Call("BuildAlignmentSamples");
        for (int i = 1; i < samples.Count; i++)
            Require(Mathf.Abs(Vector3.Distance(samples[i - 1], samples[i]) - 100.2f / 101) < 0.0001f, "Segment boundaries broke equal spacing");
        Clear(true);
        Set("phase", Enum.Parse(asm.GetType("RoadEditorPhase"), "Placing"));
        Set("currentHeight", 6f);
        Add(new Vector3(0, 6, 0), "Start");
        Add(new Vector3(100, 6, 0), "Waypoint");
        Set("hoverWorldPosition", new Vector3(200, 0, 100));
        Call("RecomputeAllControlPoints", false);
        samples = (List<Vector3>)Call("BuildAlignmentSamples");
        Require(samples.Exists(p => p.z < -0.1f), "Alignment omitted incoming hover tangent");
        Require(samples.Exists(p => p.x > 110 && p.x < 180 && Mathf.Abs(p.z - (p.x - 100)) > 1), "Alignment used straight hover chord");
        Require(Vector3.Distance(samples[samples.Count - 1], new Vector3(200, 6, 100)) < 0.001f, "Alignment appended raw ground hover");
        Require(Metric("AlignmentMaxGradePct") < 0.001f, "Hover height created fictitious slope");
        Clear(true);
        Add(new Vector3(0, 6, 0), "Start");
        Set("hoverWorldPosition", new Vector3(200, 0, 0));
        Require(!State("HasAlignmentViolation"), "Single point plus hover rejected");
        Set("hoverWorldPosition", new Vector3(0, 0, 0));
        Call("InvalidateShapeCache");
        Require(!State("HasAlignmentViolation"), "Coincident hover rejected");
        anchors.Clear();
        Call("InvalidateShapeCache");
        Require(!State("HasAlignmentViolation"), "Empty path rejected");
    }

    private static void CheckControlHeights(Assembly asm)
    {
        var terrainData = new TerrainData { heightmapResolution = 33, size = new Vector3(200, 100, 200) };
        var heights = new float[33, 33];
        for (int z = 0; z < 33; z++)
        for (int x = 0; x < 33; x++) heights[z, x] = 0.2f + x * 0.01f;
        terrainData.SetHeights(0, 0, heights);
        var terrain = Terrain.CreateTerrainGameObject(terrainData);
        asm.GetType("Utils").GetMethod("InvalidateHeightCache").Invoke(null, null);
        var type = asm.GetType("RoadPlanManager");
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var manager = new GameObject("HeightManager").AddComponent(type);
        var anchor = new Vector3(0, 60, 20);
        var control = new Vector3(100, 63, 20);
        var encoded = (List<float>)type.GetMethod("ControlToRelativeList", flags).Invoke(null, new object[] { control, anchor });
        Require(Mathf.Abs(encoded[1] - 43) < 0.02f, "Control encoded relative to control terrain instead of anchor");
        var restored = (Vector3)type.GetMethod("RestoreControl", flags).Invoke(null, new object[] { encoded[0], encoded[1], encoded[2], anchor });
        Require(Vector3.Distance(restored, control) < 0.001f, "Control height round trip over sloped terrain");
        var pt = asm.GetType("PathBuildPoint");
        var role = asm.GetType("PathPointRole");
        var anchors = (IList)type.GetField("points", flags).GetValue(manager);
        void Add(Vector3 position, string name) => anchors.Add(Activator.CreateInstance(pt, position, Enum.Parse(role, name)));
        object Call(string name, params object[] args) => type.GetMethod(name, flags).Invoke(manager, args);
        Vector3 Get(int index, string field) => (Vector3)pt.GetField(field).GetValue(anchors[index]);
        void Set(int index, string field, object value)
        {
            object point = anchors[index];
            pt.GetField(field).SetValue(point, value);
            anchors[index] = point;
        }
        float Grade(Vector3 from, Vector3 to) => (to.y - from.y) / new Vector2(to.x - from.x, to.z - from.z).magnitude;
        void RequireGrade(int index, float expected, string message)
        {
            Vector3 a = Get(index, "position");
            Require(Mathf.Abs(Grade(Get(index, "control_in"), a) - expected) < 1e-4f, message + " (in)");
            Require(Mathf.Abs(Grade(a, Get(index, "control_out")) - expected) < 1e-4f, message + " (out)");
        }
        // 地形は+X方向に16%で上るので、ハンドル直下とアンカー直下の地盤は大きく違う。
        Add(anchor, "Start");
        Add(new Vector3(100, 70, 20), "Waypoint");
        Add(new Vector3(200, 64, 20), "End");
        Call("RecomputeAllControlPoints", false);
        RequireGrade(1, 0.02f, "Automatic handle grade");
        Call("MoveControlXZ", 1, true, 80f, 35f);
        Require((bool)pt.GetField("control_in_manual").GetValue(anchors[1]), "Dragged handle lost its planar position");
        Require(Mathf.Abs(Get(1, "control_in").x - 80) < 0.001f && Mathf.Abs(Get(1, "control_in").z - 35) < 0.001f, "Handle drag moved the wrong point");
        RequireGrade(1, 0.02f, "Dragged handle kept its height instead of the anchor grade");
        Call("MoveAnchorPosition", 1, Get(1, "position") + new Vector3(10, 10, 0));
        Require(Mathf.Abs(Get(1, "control_in").x - 90) < 0.001f, "Manual handle did not follow its anchor");
        RequireGrade(1, 0.02f, "Raising the anchor changed its own grade");
        Call("MoveAnchorPosition", 2, Get(2, "position") + new Vector3(0, 20, 0));
        RequireGrade(1, 0.12f, "Manual handle ignored the neighbour height");
        Set(1, "control_height_manual", true);
        Set(1, "control_in", Get(1, "control_in") + Vector3.up * 5);
        Call("RecomputeAllControlPoints", false);
        Require(Mathf.Abs(Grade(Get(1, "control_in"), Get(1, "position")) - 0.12f) > 0.01f, "Fixed proposal height was recomputed");
        Call("MoveAnchorPosition", 0, Get(0, "position") + new Vector3(0, 4, 0));
        Require(!(bool)pt.GetField("control_height_manual").GetValue(anchors[1]), "Moving a neighbour kept the fixed height");
        RequireGrade(1, 0.1f, "Released handle did not return to the anchor grade");
        Set(1, "control_in", new Vector3(95, 62, 10));
        Call("EnforceCollinearControls", 1, true);
        Require(Mathf.Abs(Get(1, "control_in").y - 62) < 0.001f, "Planar collinearity changed control height");
        UnityEngine.Object.DestroyImmediate(terrain);
        UnityEngine.Object.DestroyImmediate(terrainData);
        asm.GetType("Utils").GetMethod("InvalidateHeightCache").Invoke(null, null);
    }

    private static void CheckElevatedTerrainIndependence(Assembly asm, object master)
    {
        var data = new TerrainData { heightmapResolution = 65, size = new Vector3(400, 100, 400) };
        var terrain = Terrain.CreateTerrainGameObject(data);
        var type = asm.GetType("RoadPlanManager");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var manager = new GameObject("TerrainInvariantManager").AddComponent(type);
        void Set(string name, object value) => type.GetField(name, flags).SetValue(manager, value);
        void Invalidate()
        {
            asm.GetType("Utils").GetMethod("InvalidateHeightCache").Invoke(null, null);
            type.GetMethod("InvalidateShapeCache", flags).Invoke(manager, null);
        }
        var anchors = (IList)type.GetField("points", flags).GetValue(manager);
        var pt = asm.GetType("PathBuildPoint");
        var role = asm.GetType("PathPointRole");
        Set("PathSubtype", 100);
        Set("PathType", 4);
        Set("phase", Enum.Parse(asm.GetType("RoadEditorPhase"), "Editing"));
        try
        {
            foreach (bool curved in new[] { false, true })
            foreach (bool follows in new[] { false, true })
            foreach (float slope in new[] { 0f, 0.02f, 0.04f, 0.045f, 0.05f, 0.07f })
            {
                Set("Curved", curved);
                master.GetType().GetField("follow_terrain").SetValue(master, follows);
                anchors.Clear();
                var a = new Vector3(0, 60, 100);
                var b = new Vector3(400, 60 + 400 * slope, 100);
                var start = Activator.CreateInstance(pt, a, Enum.Parse(role, "Start"));
                var end = Activator.CreateInstance(pt, b, Enum.Parse(role, "End"));
                pt.GetField("control_out").SetValue(start, Vector3.Lerp(a, b, 1f / 3));
                pt.GetField("control_in").SetValue(end, Vector3.Lerp(a, b, 2f / 3));
                anchors.Add(start);
                anchors.Add(end);
                List<Vector3> baseline = null;
                float baselineGrade = 0;
                for (int terrainCase = 0; terrainCase < 2; terrainCase++)
                {
                    var heights = new float[65, 65];
                    for (int z = 0; z < 65; z++)
                    for (int x = 0; x < 65; x++)
                        heights[z, x] = 0.2f + (terrainCase == 0 ? 0 : 0.25f * Mathf.Sin(x * Mathf.PI / 64));
                    data.SetHeights(0, 0, heights);
                    Invalidate();
                    var samples = (List<Vector3>)type.GetMethod("BuildAlignmentSamples", flags).Invoke(manager, null);
                    float grade = (float)type.GetProperty("AlignmentMaxGradePct").GetValue(manager);
                    Require(Mathf.Abs(grade - slope * 100) < 0.05f, "Elevated grade differs from deck: " + grade);
                    Require((bool)type.GetProperty("HasGradeViolation").GetValue(manager) == (slope > 0.045f), "Grade limit boundary misclassified: " + grade);
                    var rendered = (List<Vector3>)type.GetMethod("GetRenderPoints", flags).Invoke(manager, null);
                    foreach (var p in rendered)
                        Require(Mathf.Abs(p.y - (60 + p.x * slope)) < 0.001f, "Terrain changed rendered viaduct");
                    if (baseline != null)
                    {
                        Require(samples.Count == baseline.Count, "Terrain changed sample count");
                        for (int i = 0; i < samples.Count; i++)
                            Require(Vector3.Distance(samples[i], baseline[i]) < 0.001f, "Terrain changed elevated alignment samples");
                        Require(Mathf.Abs(grade - baselineGrade) < 0.001f, "Terrain changed elevated grade");
                    }
                    baseline = samples;
                    baselineGrade = grade;
                }
            }
        }
        finally
        {
            master.GetType().GetField("follow_terrain").SetValue(master, false);
            UnityEngine.Object.DestroyImmediate(manager.gameObject);
            UnityEngine.Object.DestroyImmediate(terrain);
            UnityEngine.Object.DestroyImmediate(data);
            asm.GetType("Utils").GetMethod("InvalidateHeightCache").Invoke(null, null);
        }
    }

    private static void CheckAlignmentLabels(Assembly asm)
    {
        var type = asm.GetType("RoadPlanManager");
        var labelType = type.GetNestedType("AlignmentLabel", BindingFlags.NonPublic);
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(labelType));
        var collect = type.GetMethod("CollectAlignmentLabels", BindingFlags.NonPublic | BindingFlags.Static);
        var samples = new List<Vector3>();
        for (int i = 0; i < 7; i++) samples.Add(new Vector3(i, 0, 0));
        var origin = new Vector3(1000, 20, 3000);
        collect.Invoke(null, new object[] { samples, origin, new float[] { 100, 60, 50, 100, 65, 100, 100 }, 70f, true, list });
        collect.Invoke(null, new object[] { samples, origin, new float[] { 0, 0, 4, 5, 0, 0, 0 }, 4.5f, false, list });
        Require(list.Count == 3, "Independent curve runs and overlapping grade run must have separate labels");
        Require((float)labelType.GetField("value").GetValue(list[0]) == 50, "First curve label must use its own minimum");
        Require((float)labelType.GetField("value").GetValue(list[1]) == 65, "Second curve label inherited global minimum");
        Require((float)labelType.GetField("value").GetValue(list[2]) == 5, "Grade label must use its own maximum");
        Require((Vector3)labelType.GetField("position").GetValue(list[0]) == origin + Vector3.right * 2, "Label must point to worst world position");
        Require((string)labelType.GetField("text").GetValue(list[2]) == "勾配  G 5.00%\n上限 4.5% 超過", "Grade label units and limit");
        Require((bool)labelType.GetField("curve").GetValue(list[0]) && !(bool)labelType.GetField("curve").GetValue(list[2]), "Reason icons must differ");
        list.Clear();
        collect.Invoke(null, new object[] { samples, origin, new float[] { 70, 80, 90, 100, 80, 70, 70 }, 70f, true, list });
        Require(list.Count == 0, "Valid geometry must have no labels");
    }

    private static void CheckAuthoritativeAlignment(Assembly asm)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = asm.GetType("RoadPlanManager");
        var manager = new GameObject("AuthoritativeAlignment").AddComponent(type);
        var phase = type.GetField("phase", flags);
        phase.SetValue(manager, Enum.Parse(phase.FieldType, "Editing"));
        string revision = (string)type.GetField("routeRevision", flags).GetValue(manager);
        string level = "{\"min_curve_radius\":null,\"max_grade\":0,\"curve_radius_limit\":0,\"grade_limit\":15,\"curve_violations\":[],\"grade_violations\":[],\"preview\":{\"samples\":[[0,0,null,0,0,10,0],[100,0,null,0,100,10,0]],\"warnings\":[]}}";
        string steep = "{\"min_curve_radius\":null,\"max_grade\":21.97,\"curve_radius_limit\":0,\"grade_limit\":15,\"curve_violations\":[],\"grade_violations\":[[10,11]],\"preview\":{\"samples\":[],\"warnings\":[{\"curve\":false,\"value\":21.97,\"limit\":15,\"position\":[10,12,20],\"points\":[[10,12,20],[11,12.2197,20]]}]}}";
        void Apply(string rev, string json)
        {
            var commandType = type.GetNestedType("ShapeCommand");
            var command = Activator.CreateInstance(commandType);
            commandType.GetField("op").SetValue(command, "apply_alignment");
            commandType.GetField("revision").SetValue(command, rev);
            commandType.GetField("alignmentJson").SetValue(command, json);
            type.GetMethod("ApplyShapeCommand").Invoke(manager, new[] { command });
        }
        try
        {
            Apply("stale", "[" + steep + "]");
            Require(type.GetField("authoritativeAlignments", flags).GetValue(manager) == null, "Stale validation was accepted");
            type.GetField("isDragging", flags).SetValue(manager, true);
            Apply(revision, "[" + level + "," + steep + "]");
            type.GetField("isDragging", flags).SetValue(manager, false);
            Require(Mathf.Abs((float)type.GetProperty("AlignmentMaxGradePct").GetValue(manager) - 21.97f) < .0001f,
                "Summary ignored a non-first track");
            var payload = (IDictionary)type.GetMethod("BuildAlignmentPayload", flags).Invoke(manager, null);
            Require((bool)payload["authoritative"] && (bool)payload["grade_violated"], "2D lost authoritative state");
            Require(((IList)payload["warnings"]).Count == 1 && ((IList)payload["labels"]).Count == 1, "2D lost backend warning");
            Require(((IList)payload["samples"]).Count == 2, "Authoritative samples were thinned a second time");
            type.GetMethod("OnEarthworkPreviewChanged", flags).Invoke(manager, null);
            Require((bool)type.GetProperty("HasGradeViolation").GetValue(manager), "Earthwork reply replaced validated warning");
            Apply(revision, "[" + level + "]");
            Require(!(bool)type.GetProperty("HasAlignmentViolation").GetValue(manager), "Successful validation left an old warning");
            type.GetMethod("InvalidateShapeCache", flags).Invoke(manager, null);
            Apply(revision, "[" + steep + "]");
            Require(type.GetField("authoritativeAlignments", flags).GetValue(manager) == null, "Editing revived an old validation");
        }
        finally { UnityEngine.Object.DestroyImmediate(manager.gameObject); }
    }

    private static void CheckMaxSpan(Assembly asm, object item)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = asm.GetType("RoadPlanManager");
        var manager = new GameObject("SpanValidation").AddComponent(type);
        type.GetField("PathSubtype").SetValue(manager, 100);
        var anchors = (IList)type.GetField("points", flags).GetValue(manager);
        var pointType = asm.GetType("PathBuildPoint");
        var roleType = asm.GetType("PathPointRole");
        var limitField = item.GetType().GetField("max_span");
        object originalLimit = limitField.GetValue(item);
        IList Evaluate(float? limit, params Vector3[] positions)
        {
            limitField.SetValue(item, limit);
            anchors.Clear();
            foreach (var p in positions)
                anchors.Add(Activator.CreateInstance(pointType, p, Enum.Parse(roleType, "Waypoint")));
            type.GetMethod("InvalidateShapeCache", flags).Invoke(manager, null);
            return (IList)type.GetMethod("BuildMaxSpanViolationsPayload", flags).Invoke(manager, null);
        }
        try
        {
            var a = Vector3.zero;
            var b = new Vector3(300, 200, 400);
            Require(Evaluate(500f, a, b).Count == 0, "Exactly 500m XZ span must pass regardless of height");
            var violations = Evaluate(500f, a, new Vector3(501, 0, 0));
            Require(violations.Count == 1, "Over-limit span must be reported");
            var detail = (IDictionary)violations[0];
            Require((int)detail["segment_index"] == 0 && (float)detail["distance"] == 501f
                && (float)detail["limit"] == 500f, "UI must receive the offending nodes, distance and limit");
            Require(Evaluate(500f, a, new Vector3(250, 0, 0), new Vector3(501, 0, 0)).Count == 0,
                "Adding an intermediate node must clear the warning");
            Require(Evaluate(0f, a, b * 10).Count == 0 && Evaluate(null, a, b * 10).Count == 0,
                "Unlimited variants must clear the warning");
            Require(Evaluate(500f, a).Count == 0 && Evaluate(500f).Count == 0,
                "Reset and insufficient points must clear the warning");
            violations = Evaluate(100f, a, b, b * 2);
            Require(violations.Count == 2 && (int)((IDictionary)violations[1])["segment_index"] == 1,
                "All offending spans must retain their node indices");
        }
        finally
        {
            limitField.SetValue(item, originalLimit);
            UnityEngine.Object.DestroyImmediate(manager.gameObject);
        }
    }

    private static void CheckRouteProposals(Assembly asm, object master)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = asm.GetType("RoadPlanManager");
        var manager = new GameObject("ProposalManager").AddComponent(type);
        master.GetType().GetField("curve_allowed").SetValue(master, true);
        master.GetType().GetField("height_editable").SetValue(master, true);
        type.GetField("PathSubtype").SetValue(manager, 100);
        var phase = type.GetField("phase", flags);
        phase.SetValue(manager, Enum.Parse(phase.FieldType, "Editing"));
        var pointType = asm.GetType("PathBuildPoint");
        var roleType = asm.GetType("PathPointRole");
        var points = (IList)type.GetField("points", flags).GetValue(manager);
        points.Add(Activator.CreateInstance(pointType, new object[] { new Vector3(0, 10, 0), Enum.Parse(roleType, "Start") }));
        points.Add(Activator.CreateInstance(pointType, new object[] { new Vector3(200, 15, 0), Enum.Parse(roleType, "End") }));
        string Revision() => (string)type.GetField("routeRevision", flags).GetValue(manager);
        string json = "{\"points\":[[0,10,0],[100,12,30],[200,15,0]],\"controls_in\":[[0,10,0],[70,11,30],[170,14,0]],\"controls_out\":[[30,11,0],[130,13,30],[200,15,0]],\"embankments\":[0,0,0],\"generated\":[0,1,0],\"height_mode\":\"fixed\"}";
        void Command(string op, string revision, string route = null)
        {
            var commandType = type.GetNestedType("ShapeCommand");
            var command = Activator.CreateInstance(commandType);
            commandType.GetField("op").SetValue(command, op);
            commandType.GetField("revision").SetValue(command, revision);
            commandType.GetField("routeJson").SetValue(command, route);
            type.GetMethod("ApplyShapeCommand").Invoke(manager, new[] { command });
        }
        try
        {
            string before = Revision();
            Command("apply_route", "stale", json);
            Require(points.Count == 2, "Stale proposal changed points");
            Command("apply_route", before, json.Replace("[0,10,0]", "[1,10,0]"));
            Require(points.Count == 2, "Proposal moved a fixed endpoint");
            Command("apply_route", before, json);
            Require(points.Count == 3, "Proposal was not applied");
            Require((bool)pointType.GetField("proposalGenerated").GetValue(points[1]), "Generated point lost provenance");
            Require((bool)pointType.GetField("control_out_manual").GetValue(points[0]), "Optimizer controls will be overwritten");
            Require(((Vector3)pointType.GetField("control_out").GetValue(points[0])).y == 11, "Proposal control height changed");
            Require((bool)pointType.GetField("control_height_manual").GetValue(points[0]), "Proposal control height is not fixed");
            Command("undo_route", before);
            Require(points.Count == 3, "Stale undo changed shape");
            Command("undo_route", Revision());
            Require(points.Count == 2 && !(bool)type.GetField("Curved").GetValue(manager), "Undo did not restore geometry and mode");
            Command("apply_route", Revision(), json);
            type.GetMethod("MoveAnchorPosition", flags).Invoke(manager, new object[] { 1, new Vector3(100, 13, 30) });
            type.GetMethod("InvalidateShapeCache", flags).Invoke(manager, null);
            Require(!(bool)pointType.GetField("proposalGenerated").GetValue(points[1]), "Manual edit did not promote generated point");
            Require(!(bool)pointType.GetField("control_height_manual").GetValue(points[0]), "Moving a neighbour kept the proposal height");
            Command("undo_route", Revision());
            Require(points.Count == 3, "Undo erased a later manual edit");
            phase.SetValue(manager, Enum.Parse(phase.FieldType, "Placing"));
            Command("apply_route", Revision(), json);
            Require(((Vector3)pointType.GetField("position").GetValue(points[1])).y == 13, "Proposal applied in placing phase");
        }
        finally { UnityEngine.Object.DestroyImmediate(manager.gameObject); }
    }

    private static void CheckSeaLakeCrossings(Assembly asm)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        // 標高10mの台地に、x=150〜250の入り江(海底-3m)とその両側の砂浜(+0.2m)、x・z=62.5〜106.25の湖の窪地(底5m)を作る。
        // 陸地ポリゴンは入り江を砂浜ごと抜き(海岸線の簡略化で水際からずれた状態)、湖は窪地だけを抜く。湖の水位は8m。
        var data = new TerrainData { heightmapResolution = 65, size = new Vector3(400, 100, 400) };
        var heights = new float[65, 65];
        for (int z = 0; z < 65; z++)
        for (int x = 0; x < 65; x++)
        {
            float wx = x * 6.25f, wz = z * 6.25f;
            float y = 10f;
            if (wx >= 137.5f && wx <= 262.5f) y = 0.2f;
            if (wx >= 150f && wx <= 250f) y = -3f;
            if (wx >= 62.5f && wx <= 106.25f && wz >= 62.5f && wz <= 106.25f) y = 5f;
            heights[z, x] = (y + 20f) / 100f;
        }
        data.SetHeights(0, 0, heights);
        var terrain = Terrain.CreateTerrainGameObject(data);
        terrain.transform.position = new Vector3(0, -20, 0);
        var utils = asm.GetType("Utils");
        var terrainManagerType = asm.GetType("TerrainManager");
        var terrainManager = new GameObject("SeaLakeTerrainManager").AddComponent(terrainManagerType);
        utils.GetMethod("RegisterTerrainManager").Invoke(null, new object[] { null });
        var settingsField = terrainManagerType.GetField("terrainSettings", flags);
        var settings = Activator.CreateInstance(settingsField.FieldType);
        void MapSize(int size)
        {
            settings.GetType().GetField("map_width").SetValue(settings, size);
            settings.GetType().GetField("map_height").SetValue(settings, size);
            settingsField.SetValue(terrainManager, settings);
        }
        var coastline = asm.GetType("CoastlineStore");
        var jtoken = coastline.GetMethod("SetFromGeoJson").GetParameters()[0].ParameterType;
        object Json(string text) => jtoken.GetMethod("Parse", new[] { typeof(string) }).Invoke(null, new object[] { text });
        var waterStore = asm.GetType("WaterSurfaceStore");
        var riverStore = asm.GetType("RiverStore");
        var masterStore = asm.GetType("MasterDataStore").GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        var dict = (IDictionary)masterStore.GetType().GetField("pathMasterById", flags).GetValue(masterStore);
        var itemType = asm.GetType("PathMasterItem");
        void Variant(int id, bool follows, bool overWater)
        {
            var item = Activator.CreateInstance(itemType);
            itemType.GetField("width").SetValue(item, 8f);
            itemType.GetField("height").SetValue(item, 5f);
            itemType.GetField("follow_terrain").SetValue(item, follows);
            itemType.GetField("over_water_allowed").SetValue(item, overWater);
            dict[id] = item;
        }
        var type = asm.GetType("RoadPlanManager");
        var manager = new GameObject("SeaLakeManager").AddComponent(type);
        type.GetField("phase", flags).SetValue(manager, Enum.Parse(asm.GetType("RoadEditorPhase"), "Editing"));
        var anchors = (IList)type.GetField("points", flags).GetValue(manager);
        var pt = asm.GetType("PathBuildPoint");
        var role = asm.GetType("PathPointRole");
        IList Evaluate(int variant, int pathType, Vector3 a, Vector3 b)
        {
            type.GetField("PathSubtype").SetValue(manager, variant);
            type.GetField("PathType", flags).SetValue(manager, pathType);
            anchors.Clear();
            anchors.Add(Activator.CreateInstance(pt, a, Enum.Parse(role, "Start")));
            anchors.Add(Activator.CreateInstance(pt, b, Enum.Parse(role, "End")));
            utils.GetMethod("InvalidateHeightCache").Invoke(null, null);
            type.GetMethod("InvalidateShapeCache", flags).Invoke(manager, null);
            return (IList)type.GetMethod("CheckSeaLakeCrossings", flags).Invoke(manager, null);
        }
        Vector3 west = new Vector3(100, 10, 200), east = new Vector3(300, 10, 200);
        try
        {
            MapSize(1000);
            Variant(900, true, false);
            Variant(901, false, false);
            Variant(902, true, true);
            coastline.GetMethod("SetFromGeoJson").Invoke(null, new[] { Json(
                "[[[-500,-500],[500,-500],[500,500],[-500,500]],[[135,10],[265,10],[265,390],[135,390]],"
                + "[[62.5,62.5],[106.25,62.5],[106.25,106.25],[62.5,106.25]]]") });
            var lake = Activator.CreateInstance(asm.GetType("WaterSurfaceRecord"));
            lake.GetType().GetField("Id").SetValue(lake, "lake_qa");
            lake.GetType().GetField("Level").SetValue(lake, 8f);
            lake.GetType().GetMethod("AddRing").Invoke(lake, new object[] { new List<Vector2> {
                new Vector2(55, 55), new Vector2(115, 55), new Vector2(115, 115), new Vector2(55, 115) } });
            waterStore.GetMethod("Add").Invoke(null, new[] { lake });

            var hits = Evaluate(900, 1, west, east);
            Require(hits.Count > 0, "Ground road across the bay must be rejected");
            foreach (var hit in hits)
            {
                var (a, b) = ((Vector3, Vector3))hit;
                Require(a.x > 140f && b.x < 260f && a.y < 0f, "Violation outside the water: " + a + " " + b);
            }
            Require(Evaluate(901, 1, west, east).Count == 0, "Bridge deck over the bay must pass");
            Require(Evaluate(901, 1, new Vector3(100, 0.3f, 200), new Vector3(300, 0.3f, 200)).Count > 0, "Deck below the sea clearance must be rejected");
            Require(Evaluate(901, 1, new Vector3(100, 0.7f, 200), new Vector3(300, 0.7f, 200)).Count == 0, "Deck above the sea clearance must pass");
            Require(Evaluate(901, 1, new Vector3(100, -12, 200), new Vector3(300, -12, 200)).Count == 0, "Buried tunnel under the bay must pass");
            Require(Evaluate(901, 1, new Vector3(100, -2, 200), new Vector3(300, -2, 200)).Count > 0, "Shallow underwater deck must be rejected");
            Require(Evaluate(900, 1, new Vector3(40, 10, 85), new Vector3(130, 10, 85)).Count > 0, "Ground road across the lake must be rejected");
            Require(Evaluate(901, 1, new Vector3(40, 8.3f, 85), new Vector3(130, 8.3f, 85)).Count > 0, "Deck below the lake clearance must be rejected");
            Require(Evaluate(901, 1, new Vector3(40, 8.7f, 85), new Vector3(130, 8.7f, 85)).Count == 0, "Deck above the lake clearance must pass");
            Require(Evaluate(900, 1, new Vector3(140, 0.2f, 50), new Vector3(140, 0.2f, 350)).Count == 0, "Dry beach outside the simplified coastline must pass");
            Require(Evaluate(900, 1, new Vector3(200, -3, 50), new Vector3(200, -3, 350)).Count > 0, "Road on the bay floor must be rejected");
            Require(Evaluate(900, 11, west, east).Count == 0, "Infra path must be exempt");
            Require(Evaluate(902, 1, west, east).Count == 0, "over_water_allowed variant must be exempt");
            riverStore.GetMethod("SetFromJson").Invoke(null, new[] { Json(
                "[{\"id\":\"river_qa\",\"polygon\":{\"type\":\"Polygon\",\"coordinates\":[[[135,150],[265,150],[265,250],[135,250]]]},"
                + "\"profile\":[[200,0.2,150],[200,0.2,250]]}]") });
            Require(Evaluate(900, 1, west, east).Count == 0, "River range must be left to the river check");
            Require(((IList)type.GetMethod("CheckRiverCrossings", flags).Invoke(manager, null)).Count == 1, "River check must report the crossing");
            riverStore.GetMethod("Clear").Invoke(null, null);
            MapSize(280);
            Require(Evaluate(900, 1, west, east).Count == 0, "Outside the city must not be treated as sea");
            MapSize(1000);
            coastline.GetMethod("Clear").Invoke(null, null);
            Require(Evaluate(900, 1, west, east).Count == 0, "Without coastline data nothing is water");
        }
        finally
        {
            coastline.GetMethod("Clear").Invoke(null, null);
            waterStore.GetMethod("Clear").Invoke(null, null);
            riverStore.GetMethod("Clear").Invoke(null, null);
            foreach (int id in new[] { 900, 901, 902 }) dict.Remove(id);
            UnityEngine.Object.DestroyImmediate(manager.gameObject);
            UnityEngine.Object.DestroyImmediate(terrainManager.gameObject);
            UnityEngine.Object.DestroyImmediate(terrain);
            UnityEngine.Object.DestroyImmediate(data);
            utils.GetMethod("InvalidateHeightCache").Invoke(null, null);
        }
    }

    private static void CheckPowerLinePreview(Assembly asm, Component renderer)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        const float baseHeight = 20f, spacing = 120f;
        var constants = asm.GetType("Constants");
        int variant = (int)constants.GetField("PATH_VARIANT_GRID").GetValue(null);
        int pathType = (int)constants.GetField("PATH_ELECTRICITY").GetValue(null);
        // 標高12mの平らな台地。送電線は設計高さを「地面+base_height」に置き、描画は base_height を引いて地面に載る。
        var data = new TerrainData { heightmapResolution = 65, size = new Vector3(400, 100, 400) };
        var heights = new float[65, 65];
        for (int z = 0; z < 65; z++)
        for (int x = 0; x < 65; x++) heights[z, x] = 0.12f;
        data.SetHeights(0, 0, heights);
        var terrain = Terrain.CreateTerrainGameObject(data);
        var utils = asm.GetType("Utils");
        utils.GetMethod("InvalidateHeightCache").Invoke(null, null);
        float Ground(Vector2 xz) => (float)utils.GetMethod("GetGroundHeight", new[] { typeof(Vector3) })
            .Invoke(null, new object[] { new Vector3(xz.x, 0, xz.y) });
        Bounds WorldBounds(MeshFilter meshFilter)
        {
            var vertices = meshFilter.sharedMesh.vertices;
            var bounds = new Bounds(meshFilter.transform.TransformPoint(vertices[0]), Vector3.zero);
            foreach (var vertex in vertices) bounds.Encapsulate(meshFilter.transform.TransformPoint(vertex));
            return bounds;
        }
        var masterStore = asm.GetType("MasterDataStore").GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        var dict = (IDictionary)masterStore.GetType().GetField("pathMasterById", flags).GetValue(masterStore);
        var itemType = asm.GetType("PathMasterItem");
        var item = Activator.CreateInstance(itemType);
        itemType.GetField("width").SetValue(item, 6f);
        itemType.GetField("follow_terrain").SetValue(item, true);
        itemType.GetField("base_height").SetValue(item, baseHeight);
        itemType.GetField("pillar_spacing").SetValue(item, spacing);
        dict[variant] = item;
        var type = asm.GetType("RoadPlanManager");
        var manager = new GameObject("PowerLineManager").AddComponent(type);
        void Set(string name, object value) => type.GetField(name, flags).SetValue(manager, value);
        Set("roadRenderer", renderer);
        Set("PathType", pathType);
        Set("PathSubtype", variant);
        Set("phase", Enum.Parse(asm.GetType("RoadEditorPhase"), "Placing"));
        Set("currentHeight", baseHeight);
        Set("hoverWorldPosition", new Vector3(350, 0, 330));
        var anchors = (IList)type.GetField("points", flags).GetValue(manager);
        var pointType = asm.GetType("PathBuildPoint");
        var roleType = asm.GetType("PathPointRole");
        var rendererType = renderer.GetType();
        var sections = (Array)rendererType.GetField("sections", flags).GetValue(renderer);
        // 敷設済みアンカー列(始点+ウェイポイント)。ウェイポイントは0個・1個・2個。
        var routes = new[]
        {
            new[] { new Vector2(50, 50), new Vector2(250, 200) },
            new[] { new Vector2(50, 50), new Vector2(250, 200), new Vector2(50, 350) },
            new[] { new Vector2(50, 50), new Vector2(250, 200), new Vector2(50, 350), new Vector2(300, 380) },
        };
        try
        {
            foreach (var route in routes)
            {
                string label = (route.Length - 2) + " waypoints: ";
                rendererType.GetMethod("ResetPreview").Invoke(renderer, null);
                anchors.Clear();
                for (int i = 0; i < route.Length; i++)
                {
                    var position = new Vector3(route[i].x, Ground(route[i]) + baseHeight, route[i].y);
                    anchors.Add(Activator.CreateInstance(pointType, position, Enum.Parse(roleType, i == 0 ? "Start" : "Waypoint")));
                }
                type.GetMethod("InvalidateShapeCache", flags).Invoke(manager, null);
                var renderPoints = type.GetMethod("GetRenderPoints", flags).Invoke(manager, null);
                type.GetMethod("UpdatePreviewSections", flags).Invoke(manager, new[] { renderPoints });

                // プレハブの無い環境では、鉄塔は1基につき1つの FarPowerTower メッシュで描かれる。
                var root = (GameObject)rendererType.GetField("currentPreviewObject", flags).GetValue(sections.GetValue(0));
                Require(root != null, label + "power line preview was not generated");
                var towers = new List<Bounds>();
                foreach (var meshFilter in root.GetComponentsInChildren<MeshFilter>(true))
                    if (meshFilter.name == "FarPowerTower") towers.Add(WorldBounds(meshFilter));
                // 鉄塔は各アンカーに立ち、区間ごとに pillar_spacing 以下の間隔で並ぶ。ウェイポイントを通らないと数も位置も合わない。
                int expected = 1;
                for (int i = 1; i < route.Length; i++)
                    expected += Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(route[i - 1], route[i]) / spacing));
                Require(towers.Count == expected, label + "tower count " + towers.Count + ", expected " + expected);
                foreach (var node in route)
                    Require(towers.Exists(t => Vector2.Distance(new Vector2(t.center.x, t.center.z), node) < 1f), label + "no tower at " + node);
                foreach (var tower in towers)
                    Require(Mathf.Abs(tower.min.y - Ground(new Vector2(tower.center.x, tower.center.z))) < 1f,
                        label + "tower base " + tower.min.y + " is not on the ground");

                var stripSection = (Component)sections.GetValue(1);
                var strip = stripSection != null ? stripSection.GetComponent<MeshFilter>() : null;
                Require(strip != null && strip.sharedMesh != null && strip.sharedMesh.vertexCount > 0, label + "moving strip is missing");
                foreach (var vertex in strip.sharedMesh.vertices)
                {
                    var world = strip.transform.TransformPoint(vertex);
                    Require(Mathf.Abs(world.y - Ground(new Vector2(world.x, world.z))) < 1f, label + "moving strip is raised: " + world);
                }
            }
        }
        finally
        {
            rendererType.GetMethod("ResetPreview").Invoke(renderer, null);
            dict.Remove(variant);
            UnityEngine.Object.DestroyImmediate(manager.gameObject);
            UnityEngine.Object.DestroyImmediate(terrain);
            UnityEngine.Object.DestroyImmediate(data);
            utils.GetMethod("InvalidateHeightCache").Invoke(null, null);
        }
    }
}
