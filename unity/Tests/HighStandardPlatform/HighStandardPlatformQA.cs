using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class HighStandardPlatformQA
{
    static int checks;
    static void Require(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
        checks++;
    }

    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-sourceRoot") + 1];
        string report = Path.Combine(Directory.GetCurrentDirectory(), "result.txt");
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => {
                foreach (string dir in new[] { "Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins" })
                {
                    string path = Path.Combine(root, dir, new AssemblyName(e.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            var assembly = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            Type modelType = assembly.GetType("ModelManager");
            var model = new GameObject("Models").AddComponent(modelType);
            modelType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, model);
            var cache = (IDictionary)modelType.GetField("_modelCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(model);
            foreach (var path in Directory.GetFiles("Assets/Models/Paths/p462", "*.fbx"))
            {
                string asset = path.Replace('\\', '/');
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
                Require(((ModelImporter)AssetImporter.GetAtPath(asset)).isReadable, "Unreadable FBX");
                cache["Models/Paths/p462/" + Path.GetFileNameWithoutExtension(path)] = prefab;
            }
            ModelPrefabGenerator.SyncAll();
            Require(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Models/Paths/p462/platform.prefab") != null,
                "Wrapper prefab not generated");
            // 462 is 10 m wide with two tracks. Platform components on each side are placed at the center of the outer track (±2.5 m), and the outer walls are 7.86–8.13 m from the centerline.
            const float trackOffset = 2.5f, wallInner = 7.85f, wallOuter = 8.13f;
            Bounds MeshBounds(string part) => ((GameObject)cache["Models/Paths/p462/" + part]).GetComponentInChildren<MeshFilter>().sharedMesh.bounds;
            // The elevated girder fits the overall bounding box to the width, so it is exactly ±5.0 (lateral scale 1). The pier beam supports the bearing beam inside the outer wall.
            var girder = MeshBounds("body_elevated");
            Require(Mathf.Abs(girder.min.x + 5f) < .002f && Mathf.Abs(girder.max.x - 5f) < .002f, "Girder must span the 10m width");
            var cap = MeshBounds("pillar_cap");
            Require(cap.max.x < wallInner && cap.max.x > trackOffset + 1.3f, "Pier cap must reach the platform beams inside the outer wall");
            Require(Mathf.Abs(MeshBounds("beam").size.x - 17f) < .002f, "Portal beam spans the poles outside both platforms");
            var foundation = assembly.GetType("PlatformFoundationGenerator").GetMethod("Generate");
            var road = assembly.GetType("RoadPath");
            int cases = 0;
            foreach (float height in new[] { 6f, 15f, 30f })
            foreach (bool left in new[] { false, true })
            foreach (float direction in new[] { 1f, -1f })
            foreach (bool clipped in new[] { false, true })
            {
                var parent = new GameObject("FoundationQA");
                // Even on a sloped elevated track, connect below the window and bring all lower ends down to the same ground level.
                var points = new List<Vector3> { new Vector3(0,height,0), new Vector3(0,height+2,direction*32) };
                object spline = road.GetMethod("BuildSpline").Invoke(null, new object[] { parent, points, false });
                double from = clipped ? .25 : 0, to = clipped ? .75 : 1;
                foundation.Invoke(null, new[] { parent, spline, "Models/Paths/p462", "Models/Paths/p4", (object)left, left ? -trackOffset : trackOffset, from, to });
                var meshes = parent.GetComponentsInChildren<MeshFilter>();
                Require(meshes.Length > 0, "Foundation missing");
                float sign = (left ? -1f : 1f) * direction;
                foreach (var filter in meshes)
                {
                    var vs = filter.sharedMesh.vertices.Select(v => filter.transform.TransformPoint(v)).ToArray();
                    Require(vs.All(v => v.x*sign >= wallInner && v.x*sign <= wallOuter), "Foundation on wrong side");
                    Require(Mathf.Abs(vs.Min(v=>v.y)+.15f)<.002f, "Wall does not reach ground");
                    Require(vs.All(v=> v.z*direction >= from*32-.01 && v.z*direction <= to*32+.01), "Wall exceeds visible interval");
                    foreach (var section in vs.GroupBy(v=>Mathf.Round(v.z*1000)))
                    {
                        Require(Mathf.Abs(section.Min(v=>v.y)+.15f)<.002f, "Ground edge floats");
                        var top = section.OrderByDescending(v=>v.y).First();
                        Require(Mathf.Abs(top.y-(height+2*top.z*direction/32))<.01f, "Wall top detached from track");
                    }
                    var normals = filter.sharedMesh.normals;
                    Require(normals.Any(n=>n.x*sign>.9f), "Mirrored outer wall has wrong winding");
                }
                UnityEngine.Object.DestroyImmediate(parent);
                cases++;
            }
            // Existing platforms have no new parts, so generate nothing.
            var empty = new GameObject("ExistingPlatform");
            object emptySpline = road.GetMethod("BuildSpline").Invoke(null, new object[] { empty,
                new List<Vector3> { new Vector3(0,6,0), new Vector3(0,6,32) }, false });
            foundation.Invoke(null, new[] { empty, emptySpline, "Models/Paths/p461", "Models/Paths/p4", (object)false, 0f, 0d, 1d });
            Require(empty.GetComponentsInChildren<MeshFilter>().Length==0, "Existing platform acquired a wall");
            AssetDatabase.SaveAssets();
            int fbxCount = Directory.GetFiles("Assets/Models/Paths/p462", "*.fbx").Length;
            File.WriteAllText(report, $"PASS: {cases} height/side/direction/clip cases, {checks} checks, {fbxCount} FBX and wrapper generation");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(report,e.ToString());
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }
}
