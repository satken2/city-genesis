using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class BusStopsQA
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-busStopsSourceRoot") + 1];
        string output = root + "/Temp/BusStopsQA/result.txt";
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (o, e) => {
                foreach (string dir in new[] {"Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins"})
                {
                    string file = Path.Combine(root, dir, new AssemblyName(e.Name).Name + ".dll");
                    if (File.Exists(file)) return Assembly.LoadFrom(file);
                }
                return null;
            };
            var asm = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            var place = asm.GetType("BusStopManager").GetMethod("TryPlace");
            var road = new List<Vector3> {new Vector3(0, 10, 0), new Vector3(0, 14, 20)};
            foreach (int side in new[] {-1, 1})
            {
                object[] values = {new Vector3(side, 0, 10), road, 12f, 2.5f, null, null};
                Check((bool)place.Invoke(null, values), "road placement failed");
                var position = (Vector3)values[4]; var rotation = (Quaternion)values[5];
                Check(Mathf.Abs(position.x - side * 4.8f) < .001f, "shelter must fit inside sidewalk");
                Check(Mathf.Abs(position.y - 12.3f) < .001f, "sloped road height lost");
                Check(Vector3.Dot(rotation * Vector3.forward, Vector3.left * side) > .999f, "bench faces away from road");
                Check(Vector3.Dot(rotation * Vector3.up, Vector3.up) > .999f, "roof must remain level");
                road.Reverse();
            }
            object[] narrow = {new Vector3(1, 0, 10), road, 4f, 0f, null, null};
            Check((bool)place.Invoke(null, narrow) && Mathf.Abs(((Vector3)narrow[4]).x - 3.2f) < .001f,
                "narrow road shelter obstructs carriageway");
            object[] empty = {Vector3.zero, new List<Vector3> {Vector3.zero, Vector3.zero}, 4f, 0f, null, null};
            Check(!(bool)place.Invoke(null, empty), "degenerate geometry accepted");
            ModelImportSettings.EnableReadWriteAll();
            ModelPrefabGenerator.SyncAll();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Models/Props/bus_stop.prefab");
            Check(prefab != null, "automatic prefab missing");
            var model = UnityEngine.Object.Instantiate(prefab);
            var renderers = model.GetComponentsInChildren<Renderer>();
            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            Check(Mathf.Abs(bounds.size.x - 5.4f) < .01f && Mathf.Abs(bounds.size.z - 2.2f) < .01f, "FBX horizontal scale");
            Check(Mathf.Abs(bounds.min.y + 2f) < .01f && Mathf.Abs(bounds.max.y - 2.64f) < .01f, "FBX underground supports or roof height");
            int triangles = 0;
            foreach (var f in model.GetComponentsInChildren<MeshFilter>()) triangles += f.sharedMesh.triangles.Length / 3;
            Check(triangles == 272, "low polygon budget");
            var glass = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            asm.GetType("BusStopManager").GetMethod("ConfigureGlass").Invoke(null, new object[] {glass});
            Check(glass.renderQueue == 3000 && glass.GetFloat("_ZWrite") == 0 && glass.GetFloat("_Surface") == 1,
                "glass must use transparent blend without depth writes");
            Check(glass.enableInstancing && glass.GetColor("_BaseColor").a < .3f, "glass opacity/instancing");
            File.WriteAllText(output, "PASS: road direction, both sides, slope height, narrow road, degenerate geometry, actual FBX dimensions/supports/272 triangles, automatic prefab");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { File.WriteAllText(output, e.ToString()); EditorApplication.Exit(1); }
    }
}
