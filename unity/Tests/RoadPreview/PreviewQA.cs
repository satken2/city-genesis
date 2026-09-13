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
            File.WriteAllText(result, "PASS: 100 updates preserve detailed prefix and dynamic Mesh; suffix reuse, double track, slope, winding, release, resume, reset; manager placing, dragging and release partitions.");
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
}
