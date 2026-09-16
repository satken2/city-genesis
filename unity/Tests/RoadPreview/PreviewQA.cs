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
                    new float[] { -5, 5 }, new int[] { 0, 0 }, "both" });
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
            File.WriteAllText(result, "PASS: 100 updates preserve detailed prefix and dynamic Mesh; suffix reuse, double track, slope, winding, release, resume, reset; manager placing, dragging and release partitions; alignment curves, grades, hover and uniform samples.");
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
        var point = Activator.CreateInstance(pt, anchor, Enum.Parse(role, "Start"));
        pt.GetField("control_out").SetValue(point, control);
        pt.GetField("control_out_manual").SetValue(point, true);
        anchors.Add(point);
        type.GetMethod("MoveAnchorPosition", flags).Invoke(manager, new object[] { 0, anchor + new Vector3(20, 5, 0) });
        var moved = (Vector3)pt.GetField("control_out").GetValue(anchors[0]);
        Require(Vector3.Distance(moved, control + new Vector3(20, 5, 0)) < 0.001f, "Anchor movement changed vertical tangent with underlying terrain");
        point = anchors[0];
        pt.GetField("control_in").SetValue(point, new Vector3(10, 62, 10));
        anchors[0] = point;
        type.GetMethod("EnforceCollinearControls", flags).Invoke(manager, new object[] { 0, true });
        var opposite = (Vector3)pt.GetField("control_in").GetValue(anchors[0]);
        Require(Mathf.Abs(opposite.y - 62) < 0.001f, "Planar collinearity changed control height");
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

}
