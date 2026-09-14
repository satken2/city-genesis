using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

public static class PillarQA
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static Assembly assembly;
    static Type pathType;
    static object manager;
    static void Set(object target, string field, object value) => target.GetType().GetField(field).SetValue(target, value);
    static object Call(string method, params object[] args) => manager.GetType().GetMethod(method, Private | BindingFlags.Public).Invoke(manager, args);
    static void Check(bool value, string label) { if (!value) throw new Exception(label); }
    static object Path(int id, Vector3 start, Vector3 end, int variant = 2)
    {
        var path = Activator.CreateInstance(pathType);
        Set(path, "path_id", id); Set(path, "path_variant", variant); Set(path, "path_type", 2);
        Set(path, "width", 8f); Set(path, "version", 1);
        Set(path, "start_point", new List<object> { start.x, start.y, start.z });
        Set(path, "end_point", new List<object> { end.x, end.y, end.z });
        return path;
    }
    static int Generate(object path)
    {
        var root = new GameObject("Pillars");
        var road = assembly.GetType("RoadPath");
        var spline = road.GetMethod("BuildSpline").Invoke(null, new object[] { root,
            new List<Vector3> { new Vector3(-20, 10, 0), new Vector3(20, 10, 0) }, false });
        road.GetMethod("GeneratePillars").Invoke(null, new object[] { root, spline, 20f, 0f, path, "Models/Paths/p2", "Models/Paths/p2" });
        int count = root.GetComponentsInChildren<MeshFilter>().Length;
        UnityEngine.Object.DestroyImmediate(root);
        return count;
    }
    public static void Run()
    {
        string result = PathName("result.txt");
        try
        {
            var args = Environment.GetCommandLineArgs();
            string source = args[Array.IndexOf(args, "-pillarSourceRoot") + 1];
            AppDomain.CurrentDomain.AssemblyResolve += (o, e) =>
            {
                foreach (string dir in new[] { "/Temp/bin/Debug", "/Library/ScriptAssemblies", "/Assets/Plugins" })
                {
                    string file = source + dir + "/" + new AssemblyName(e.Name).Name + ".dll";
                    if (File.Exists(file)) return Assembly.LoadFrom(file);
                }
                return null;
            };
            assembly = Assembly.LoadFrom(source + "/Temp/bin/Debug/Assembly-CSharp.dll");
            pathType = assembly.GetType("GeneratedModels.PathSummary");
            var masterType = assembly.GetType("MasterDataStore");
            var master = new GameObject("Master").AddComponent(masterType);
            masterType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, master);
            masterType.GetProperty("IsInitialized").SetValue(master, true);
            var item = Activator.CreateInstance(assembly.GetType("PathMasterItem"));
            Set(item, "width", 8f); Set(item, "height", 5f); Set(item, "pillar_spacing", 20f); Set(item, "height_editable", true);
            ((IDictionary)masterType.GetField("pathMasterById", Private).GetValue(master)).Add(2, item);
            var models = new GameObject("Models").AddComponent(assembly.GetType("ModelManager"));
            models.GetType().GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, models);
            var cache = (IDictionary)models.GetType().GetField("_modelCache", Private).GetValue(models);
            foreach (string part in new[] { "pillar", "pillar_cap" })
            {
                var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var mesh = UnityEngine.Object.Instantiate(prefab.GetComponent<MeshFilter>().sharedMesh);
                var vertices = mesh.vertices;
                for (int i = 0; i < vertices.Length; i++) vertices[i].y -= 0.5f;
                mesh.vertices = vertices; mesh.RecalculateBounds(); prefab.GetComponent<MeshFilter>().sharedMesh = mesh;
                cache.Add("Models/Paths/p2/" + part, prefab);
            }
            manager = new GameObject("Paths").AddComponent(assembly.GetType("PathManager"));
            Call("Awake");
            var bridge = Path(1, new Vector3(-20, 10, 0), new Vector3(20, 10, 0));
            Call("SavePathToCache", bridge);
            Check(Generate(bridge) == 6, "Three supports before lower road");
            var lower = Path(2, new Vector3(0, 0, -30), new Vector3(0, 0, 30));
            Call("SavePathToCache", lower);
            var pending = (HashSet<int>)manager.GetType().GetField("pendingPillarRebuildIds", Private).GetValue(manager);
            Check(pending.Contains(1), "Adding lower road invalidates bridge");
            Check(Generate(bridge) == 4, "Only crossing support and cap disappear without road mesh");
            var revisions = (IDictionary)manager.GetType().GetField("geometryRevisions", Private).GetValue(manager);
            Check((int)revisions[1] > 0, "LOD cache revision changes");
            pending.Clear();
            var moved = Path(2, new Vector3(2000, 0, -30), new Vector3(2000, 0, 30));
            Set(moved, "version", 2); Call("SavePathToCache", moved);
            Check(pending.Contains(1), "Moving road invalidates old neighborhood");
            Check(Generate(bridge) == 6, "Support returns after move");
            pending.Clear(); Call("SavePathToCache", lower);
            Check(Generate(bridge) == 4, "Support disappears again");
            pending.Clear(); Call("RemovePathFromCache", 2);
            Check(pending.Contains(1), "Removal invalidates bridge");
            Check(Generate(bridge) == 6, "Support returns after removal");
            pending.Clear(); Call("SavePathToCache", bridge);
            Check(pending.Count == 0, "Unchanged data does not enqueue rebuilds");
            var curve = Path(3, new Vector3(2000, 0, -30), new Vector3(2000, 0, 30));
            Set(curve, "curved", true);
            Set(curve, "start_control", new List<object> { -2000f / 3f, 0f, -10f });
            Set(curve, "end_control", new List<object> { -2000f / 3f, 0f, 10f });
            Call("SavePathToCache", curve);
            Check(pending.Contains(1), "Curve bulging out of anchor cells invalidates bridge");
            Check(((IList)Call("GetPillarObstacles", bridge)).Count > 0, "Curve is found outside anchor cells");
            Check(Generate(bridge) < 6, "Curved crossing removes support");
            Call("RemovePathFromCache", 3);
            Call("RemovePathFromCache", 1);
            Call("SavePathToCache", lower); Call("SavePathToCache", bridge);
            Check(Generate(bridge) == 4, "Reversed registration order has same placement");
            Set(lower, "path_type", 4);
            Check(Generate(bridge) == 4, "Railway below bridge also removes support");
            var arcadeMaster = Activator.CreateInstance(assembly.GetType("PathMasterItem"));
            Set(arcadeMaster, "width", 5f); Set(arcadeMaster, "height_editable", true); Set(arcadeMaster, "pillar_spacing", 0f);
            ((IDictionary)masterType.GetField("pathMasterById", Private).GetValue(master)).Add(404, arcadeMaster);
            cache.Add("Models/Paths/p404/arcade", GameObject.CreatePrimitive(PrimitiveType.Cube));
            var arcade = Path(4, new Vector3(-20, 10, 0), new Vector3(20, 10, 0), 404);
            Set(arcade, "path_type", 4); Call("SavePathToCache", arcade);
            pending.Clear(); Call("RemovePathFromCache", 2);
            Check(pending.Contains(4), "Road removal invalidates arcade with zero pillar spacing");
            pending.Clear(); Call("SavePathToCache", lower);
            Check(pending.Contains(4), "Road addition invalidates arcade");
            pending.Clear(); Call("SavePathToCache", moved);
            Check(pending.Contains(4), "Road move invalidates old arcade neighborhood");
            File.WriteAllText(result, "PASS: Unity pillar placement, add/move/remove, data-only neighbor, LOD invalidation, curve cells, registration order, railway");
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(result, "FAIL: " + ex);
            Debug.LogException(ex); EditorApplication.Exit(1);
        }
    }
    static string PathName(string file) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), file);
}
