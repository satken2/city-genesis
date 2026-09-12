using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// 実FBXの地面原点を保ち、橋の描画とクリック面が同じ高さになることを検査する。
public static class StationHouseQA
{
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
            var assembly = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            Type modelType = assembly.GetType("ModelManager");
            var model = new GameObject("Models").AddComponent(modelType);
            modelType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, model);
            var cache = (IDictionary)modelType.GetField("_modelCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(model);
            Type pathType = assembly.GetType("GeneratedModels.PathSummary");
            Type stationType = assembly.GetType("StationHousePath");
            object setback = Activator.CreateInstance(assembly.GetType("SidewalkSetback"));
            int count = 0;
            var results = new List<string>();
            var resolve = stationType.GetMethod("ResolveHousePosition");
            float margin = Convert.ToSingle(assembly.GetType("Constants").GetField("STATION_HOUSE_TRACK_CLEARANCE_MARGIN").GetRawConstantValue());
            foreach (var test in new[] {
                (intervals: new List<Vector2> { new Vector2(-11, 11) }, inside: true, expected: 6f - margin),
                (intervals: new List<Vector2> { new Vector2(-11, 11) }, inside: false, expected: 11f),
                (intervals: new List<Vector2> { new Vector2(-31, -9) }, inside: true, expected: -14f - margin),
                (intervals: new List<Vector2>(), inside: true, expected: -5f),
                (intervals: new List<Vector2>(), inside: false, expected: 0f),
            }) {
                float actual = (float)resolve.Invoke(null, new object[] { test.intervals, 5f, test.inside });
                Require(Mathf.Abs(actual - test.expected) < .001f, "Station footprint placement mismatch");
            }
            foreach (int variant in new[] { 2001, 2011 })
            {
                string asset = $"Assets/Paths/p{variant}/body.fbx";
                var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
                importer.isReadable = true;
                importer.SaveAndReimport();
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
                cache[$"Models/Paths/p{variant}/body"] = prefab;
                Bounds original = prefab.GetComponent<MeshFilter>().sharedMesh.bounds;
                Require(original.min.y > 1f, "Bridge fixture must be above its ground origin");
                foreach (float length in new[] { 32f, 320f, 2048f })
                foreach (float direction in new[] { -1f, 1f })
                foreach (string ends in new[] { "both", "start", "end" })
                {
                    object path = Activator.CreateInstance(pathType);
                    pathType.GetField("path_id").SetValue(path, 1);
                    pathType.GetField("path_type").SetValue(path, 20);
                    pathType.GetField("path_variant").SetValue(path, variant);
                    pathType.GetField("house_ends").SetValue(path, ends);
                    pathType.GetField("start_point").SetValue(path, new List<object> { 0f, 0f, 0f });
                    pathType.GetField("end_point").SetValue(path, new List<object> { 0f, 0f, length * direction });
                    var points = new List<Vector3> { Vector3.zero, new Vector3(0f, 0f, length * direction) };
                    object result = stationType.GetMethod("Generate").Invoke(Activator.CreateInstance(stationType), new[] { path, points, setback, setback });
                    var go = (GameObject)result.GetType().GetProperty("GeneratedObject").GetValue(result);
                    Require(go.GetComponentsInChildren<Transform>().Count(t => t.name == "House") == (ends == "both" ? 2 : 1), "Wrong number of station buildings");
                    var bridge = go.GetComponentsInChildren<MeshFilter>().Where(f => f.name.StartsWith("Models/Paths/") && f.sharedMesh != null).ToArray();
                    Require(bridge.Length > 0, "Bridge mesh missing");
                    var ys = bridge.SelectMany(f => f.sharedMesh.vertices.Select(v => f.transform.TransformPoint(v).y)).ToArray();
                    float splineY = .1f;
                    Require(Mathf.Abs(ys.Min() - (original.min.y + splineY)) < .002f, $"p{variant} {length}m bridge bottom {ys.Min()}");
                    Require(Mathf.Abs(ys.Max() - (original.max.y + splineY)) < .002f, $"p{variant} {length}m bridge top {ys.Max()}");
                    var collider = (Mesh)result.GetType().GetProperty("Mesh").GetValue(result);
                    Require(Mathf.Abs(collider.bounds.max.y - ys.Max()) < .002f, "Bridge collider differs from visible top");
                    Require(go.GetComponentsInChildren<Component>().All(c => c == null || c.GetType().Name != "SplinePartLod"), "Bridge uses road strip LOD");
                    results.Add($"p{variant} length={length * direction}: y={ys.Min():F3}..{ys.Max():F3}, meshes={bridge.Length}");
                    UnityEngine.Object.DestroyImmediate(go);
                    UnityEngine.Object.DestroyImmediate(collider);
                    count++;
                }
            }
            File.WriteAllText(report, $"PASS station bridges: {count} cases\n" + string.Join("\n", results));
            EditorApplication.Exit(0);
        }
        catch (Exception e) { File.WriteAllText(report, e.ToString()); Debug.LogException(e); EditorApplication.Exit(1); }
    }

    static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
}
