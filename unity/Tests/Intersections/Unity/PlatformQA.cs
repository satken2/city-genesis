using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// 実FBXと本編のホーム生成器で、左右と上屋柱の繰り返し周期を検査する。
public static class PlatformQA
{
    static Assembly assembly;
    static int checks;
    public static void Run()
    {
        string[] args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-intersectionSourceRoot") + 1];
        string report = root + "/Temp/IntersectionUnityQA/result.txt";
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => {
                string name = new AssemblyName(e.Name).Name + ".dll";
                foreach (string dir in new[] { "Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins" }) {
                    string file = Path.Combine(root, dir, name);
                    if (File.Exists(file)) return Assembly.LoadFrom(file);
                }
                return null;
            };
            assembly = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            Type modelType = assembly.GetType("ModelManager");
            var model = new GameObject("Models").AddComponent(modelType);
            modelType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, model);
            var cache = (IDictionary)modelType.GetField("_modelCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(model);
            foreach (int variant in new[] { 451, 452, 461, 462 })
                foreach (string part in new[] { "platform", "platform_wall" }) {
                    string asset = $"Assets/Paths/p{variant}/{part}.fbx";
                    var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
                    if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
                    cache[$"Models/Paths/p{variant}/{part}"] = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
                }
            foreach (int variant in new[] { 451, 452, 461, 462 })
                foreach (bool left in new[] { false, true })
                    foreach (float direction in new[] { 1f, -1f })
                        Check(variant, left, direction);
            File.WriteAllText(report, $"PASS platforms: {checks} checks across 16 station sides; canopy 4m, fence 1.6m");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { File.WriteAllText(report, e.ToString()); Debug.LogException(e); EditorApplication.Exit(1); }
    }

    static void Check(int variant, bool left, float direction)
    {
        var parent = new GameObject("Station");
        var points = new List<Vector3> { Vector3.zero, new Vector3(0, 0, direction * 64) };
        object spline = assembly.GetType("RoadPath").GetMethod("BuildSpline").Invoke(null, new object[] { parent, points, true });
        var material = new Material(Shader.Find("Standard"));
        assembly.GetType("TrackPath").GetMethod("CreatePlatformSide", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] {
            parent, spline, null, points, new[] { Vector3.right * direction, Vector3.right * direction },
            new[] { 0f, 64f }, left, 2.5f, 3f, material, $"Models/Paths/p{variant}", "Models/Paths/p4"
        });
        foreach (var component in parent.GetComponentsInChildren<Component>()) {
            if (component == null || component.GetType().Name != "SplineMesh") continue;
            MethodInfo rebuild = component.GetType().GetMethod("RebuildImmediate");
            rebuild.Invoke(component, rebuild.GetParameters().Length == 0 ? null : new object[] { true });
        }
        float side = (left ? -1 : 1) * direction;
        foreach (var filter in parent.GetComponentsInChildren<MeshFilter>()) {
            Require(filter.sharedMesh != null, "Missing generated mesh");
            var vertices = filter.sharedMesh.vertices.Select(v => filter.transform.TransformPoint(v)).ToArray();
            if (filter.name.EndsWith("/platform") || filter.name.EndsWith("/platform_wall")) {
                Require(vertices.All(v => v.x * side > 1.25f && v.x * side < 5.65f), $"p{variant} wrong side {left}/{direction}");
                bool wall = filter.name.EndsWith("/platform_wall");
                // 柱断面の端だけを拾い、長手分割で増えた同位置の頂点はまとめる。
                var locations = vertices.Where(v => wall
                    ? Mathf.Abs(v.y - 1.68f) < .001f && Mathf.Abs(v.x * side - 5.45f) < .001f
                    : Mathf.Abs(v.y - 4.34f) < .001f && Mathf.Abs(v.x * side - 3.585f) < .001f)
                    .Select(v => v.z * direction).OrderBy(z => z).ToList();
                var groups = new List<List<float>>();
                foreach (float z in locations) {
                    if (groups.Count == 0 || z - groups.Last().Last() > .25f) groups.Add(new List<float>());
                    groups.Last().Add(z);
                }
                float period = wall ? 1.6f : 4f;
                Require(groups.Count == (wall ? 40 : 16), $"p{variant} {filter.name} count={groups.Count}");
                var centers = groups.Select(g => (g.Min() + g.Max()) / 2).ToArray();
                for (int i = 1; i < centers.Length; i++) Require(Mathf.Abs(centers[i] - centers[i-1] - period) < .005f, "Compressed pillar spacing");
            }
            else if (filter.name.StartsWith("PlatformDeck"))
                Require(vertices.All(v => v.x * side >= 1.499f && v.x * side <= 5.501f), "Deck side mismatch");
        }
        UnityEngine.Object.DestroyImmediate(parent);
        UnityEngine.Object.DestroyImmediate(material);
    }
    static void Require(bool valid, string message) { if (!valid) throw new Exception(message); checks++; }
}
