using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// 独立プロジェクトで実FBXと本編アセンブリを使い、軌間・配置・接続・プレハブ参照を検証する。
public static class LrtQA
{
    static Assembly asm;
    static IDictionary masters, models;
    static int checks;
    static string root;
    static void Check(bool ok, string text) { checks++; if (!ok) throw new Exception(text); }
    static void Set(object target, string field, object value) => target.GetType().GetField(field).SetValue(target, value);
    static IDictionary Manager(string name, string field)
    {
        var type = asm.GetType(name);
        var component = new GameObject(name).AddComponent(type);
        type.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, component);
        return (IDictionary)type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(component);
    }
    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        root = args[Array.IndexOf(args, "-sourceRoot") + 1];
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
                foreach (string dir in new[] { "Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins" })
                {
                    string path = Path.Combine(root, dir, new AssemblyName(e.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            asm = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            masters = Manager("MasterDataStore", "pathMasterById");
            models = Manager("ModelManager", "_modelCache");
            foreach (string file in Directory.GetFiles("Assets/Paths", "*.fbx", SearchOption.AllDirectories))
            {
                string asset = file.Replace('\\', '/');
                var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
                if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
                models["Models/Paths/" + asset.Substring("Assets/Paths/".Length).Replace(".fbx", "")] = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            }
            foreach (int id in new[] { 222, 242, 303, 431 })
            {
                Master(id);
                ModelDimensions(id);
                foreach (float slope in new[] { 0f, .08f, -.08f }) Road(id, slope, false);
                Road(id, 0, true);
            }
            foreach (int a in new[] { 222, 242, 303, 431 })
                foreach (int b in new[] { 222, 242, 303, 431 }) { Connections(a, b); Connections(a, b, true); }
            foreach (int id in new[] { 303, 304, 305 }) Mall(id);
            ExportPrefabs();
            File.WriteAllText(root + "/Temp/LrtUnityQA/result.txt", "PASS LRT: " + checks + " checks; FBX, straight/curve/slope, all LRT connections, wrapper prefabs");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(root + "/Temp/LrtUnityQA/result.txt", e.ToString());
            Debug.LogException(e); EditorApplication.Exit(1);
        }
    }
    static void Master(int id)
    {
        var m = Activator.CreateInstance(asm.GetType("PathMasterItem"));
        Set(m, "width", id == 222 ? 24f : id == 242 ? 32f : id == 303 ? 8f : 5f);
        Set(m, "lanes", id == 222 ? 4 : id == 242 ? 6 : 1);
        Set(m, "sidewalk_width", id < 300 ? 3f : 0f);
        Set(m, "side_width", id < 300 ? .5f : 0f);
        Set(m, "center_width", id < 300 ? 1f : 0f);
        Set(m, "wire_spacing", 30f); Set(m, "follow_terrain", false);
        masters[id] = m;
    }
    static object PathData(int id, int key = 1, bool start = true)
    {
        var p = Activator.CreateInstance(asm.GetType("GeneratedModels.PathSummary"));
        Set(p, "path_id", key); Set(p, "path_variant", id);
        Set(p, "path_type", id < 300 ? 1 : id == 303 ? 3 : 4);
        Set(p, "start_node_id", start ? 1 : key + 10); Set(p, "end_node_id", start ? key + 10 : 1);
        Set(p, "start_point", new List<object> { 0f, 0f, start ? 0f : -50f });
        Set(p, "end_point", new List<object> { 0f, 0f, start ? 50f : 0f });
        Set(p, "follow_terrain", false); Set(p, "curved", true);
        return p;
    }
    static void ModelDimensions(int id)
    {
        string part = id == 431 ? "track" : "lrt_track";
        var model = (GameObject)models[$"Models/Paths/p{id}/{part}"];
        var mesh = model.GetComponent<MeshFilter>().sharedMesh;
        float top = id == 431 ? .355f : .025f;
        Check(Mathf.Abs(mesh.bounds.max.y - top) < .002f, "rail top " + id);
        var xs = mesh.vertices.Where(v => Mathf.Abs(v.y - top) < .002f).Select(v => v.x).ToArray();
        Check(xs.Any(x => x > .68f && x < .76f) && xs.Any(x => x < -.68f && x > -.76f), "rail gauge " + id);
        if (id == 431) return;
        mesh = ((GameObject)models[$"Models/Paths/p{id}/lrt_wire"]).GetComponent<MeshFilter>().sharedMesh;
        Check(Mathf.Abs(mesh.bounds.center.y - 5.3f) < .002f, "wire height " + id);
        float offset = (float)asm.GetType("LrtRoadParts").GetMethod("Offset").Invoke(null, new[] { PathData(id) });
        Check(Mathf.Abs(offset - (id == 222 ? 2.5625f : id == 242 ? 2.541667f : 0f)) < .0001f, "inner lane offset " + id);
    }
    static void Road(int id, float slope, bool curved)
    {
        var path = PathData(id);
        var points = new List<Vector3>();
        for (int i = 0; i <= 16; i++) points.Add(new Vector3(curved ? 8 * Mathf.Sin(i / 16f * Mathf.PI) : 0, slope * i * 5, i * 5));
        var type = asm.GetType(id == 431 ? "TrackPath" : "RoadPath");
        var setback = Activator.CreateInstance(asm.GetType("SidewalkSetback"));
        var result = type.GetMethod("Generate").Invoke(Activator.CreateInstance(type), new[] { path, points, setback, setback });
        var go = (GameObject)result.GetType().GetProperty("GeneratedObject").GetValue(result);
        Check(go != null, "generated " + id);
        var filters = go.GetComponentsInChildren<MeshFilter>();
        foreach (var f in filters) if (f.sharedMesh != null)
        {
            Check(f.sharedMesh.vertexCount <= 60000, "vertex budget " + f.name);
            Check(f.sharedMesh.vertices.All(v => !float.IsNaN(v.x + v.y + v.z)), "finite " + f.name);
        }
        if (id != 431)
        {
            Check(filters.Count(f => f.name.EndsWith("/lrt_track")) == (id == 303 ? 1 : 2), "track count " + id);
            Check(filters.Count(f => f.name.EndsWith("/lrt_wire")) == (id == 303 ? 1 : 2), "wire count " + id);
            var poles = go.GetComponentsInChildren<Transform>().Where(t => t.name == "LrtPole").ToArray();
            Check(poles.Length >= 2, "poles present " + id);
            Check(poles.All(t => Vector3.Dot(t.up, Vector3.up) > .9999f), "poles vertical " + id);
        }
        UnityEngine.Object.DestroyImmediate(go);
    }
    static void Connections(int a, int b, bool turn = false)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(asm.GetType("GeneratedModels.PathSummary")));
        list.Add(PathData(a, 1, false)); list.Add(PathData(b, 2, true));
        var cuts = new Dictionary<int, Vector2> { { 1, new Vector2(0, -12) }, { 2, new Vector2(0, 12) } };
        if (turn)
        {
            Set(list[1], "end_point", new List<object> { 50f, 0f, 0f });
            cuts[2] = new Vector2(12, 0);
        }
        var specs = (IList)asm.GetType("TrackIntersectionMeshGenerator").GetMethod("BuildRailConnectorSpecs").Invoke(null, new object[] { 1, Vector3.zero, true, list, cuts, null });
        int expected = a < 300 || b < 300 ? 2 : 1;
        if (turn && a == 431 && b == 431) { Check(specs == null, "dedicated track turn restriction"); return; }
        Check(specs != null && specs.Count == expected, $"connections {a}-{b}");
        foreach (var spec in specs)
        {
            var points = (List<Vector3>)spec.GetType().GetProperty("Points").GetValue(spec);
            float ao = a < 300 ? (a == 222 ? 2.5625f : 2.541667f) : 0;
            float bo = b < 300 ? (b == 222 ? 2.5625f : 2.541667f) : 0;
            Check(Mathf.Abs(Mathf.Abs(points[0].x) - ao) < .0001f, "start rail alignment");
            Check(Mathf.Abs(Mathf.Abs(turn ? points[points.Count - 1].z : points[points.Count - 1].x) - bo) < .0001f, "end rail alignment");
            var go = new GameObject("ConnectorTest");
            asm.GetType("TrackPath").GetMethod("CreateRailConnectorObject").Invoke(null, new object[] { go.transform, spec, 0 });
            Check(go.GetComponentsInChildren<MeshFilter>().Length > 0, "connector mesh");
            UnityEngine.Object.DestroyImmediate(go);
        }
    }
    static void Mall(int id)
    {
        Master(id);
        float width = id == 303 ? 8f : id == 304 ? 12f : 20f;
        var m = masters[id];
        Set(m, "width", width); Set(m, "tree_spacing", 24f);
        if (id != 303) Set(m, "wire_spacing", 0f);
        string[] fields = { "streetlight_type", "street_tree_type", "bench_type", "bin_type", "planter_type", "advert_type" };
        foreach (string f in fields) Set(m, f + "_allowed", true);
        var path = PathData(id);
        Set(path, "path_type", 3);
        foreach (string f in fields) Set(path, f, 1);
        foreach (bool curved in new[] { false, true })
        {
            var points = new List<Vector3>();
            for (int i = 0; i <= 20; i++) points.Add(new Vector3(curved ? 4 * Mathf.Sin(i * Mathf.PI / 20) : 0, .02f*i*4, i*4));
            var type = asm.GetType("RoadPath");
            var setback = Activator.CreateInstance(asm.GetType("SidewalkSetback"));
            var result = type.GetMethod("Generate").Invoke(Activator.CreateInstance(type), new[] { path, points, setback, setback });
            var go = (GameObject)result.GetType().GetProperty("GeneratedObject").GetValue(result);
            foreach (string part in new[] { "bench", "bin", "planter", "advert", "tree" })
            {
                var placed = go.GetComponentsInChildren<MeshFilter>().Where(f => f.name.StartsWith(part + "(")).ToArray();
                Check(placed.Length > 0, "mall part present " + id + " " + part);
                foreach (var f in placed)
                {
                    Check(Vector3.Dot(f.transform.up, Vector3.up) > .9999f, "mall upright " + part);
                    Check(f.sharedMesh.vertices.All(v => !float.IsNaN(v.x+v.y+v.z)), "mall finite " + part);
                    if (!curved)
                    {
                        var b = f.GetComponent<Renderer>().bounds;
                        Check(b.min.x > -width/2 && b.max.x < width/2, "mall footprint " + id + " " + part);
                        Check(b.min.z > 1.5f && b.max.z < 78.5f, "mall end clearance " + part);
                        if (id == 304) Check(b.min.x >= -1.7f && b.max.x <= 1.7f, "central furniture island");
                        if (id == 305) Check(Mathf.Abs(f.transform.position.x) > 3.9f && Mathf.Abs(f.transform.position.x) < 4.1f, "two furniture rows");
                        if (id == 303) Check(b.max.x < -2.2f || b.min.x > 2.2f, "LRT clearance");
                    }
                }
            }
            UnityEngine.Object.DestroyImmediate(go);
        }
        foreach (string f in fields) Set(path, f, 0);
        var empty = new GameObject("NoEquipment");
        asm.GetType("RoadEquipmentGenerator").GetMethod("Generate").Invoke(null, new object[] { empty, null, path, width, 0f, 24f });
        Check(empty.transform.childCount == 0, "all equipment disabled " + id);
        UnityEngine.Object.DestroyImmediate(empty);
    }
    static void ExportPrefabs()
    {
        foreach (int id in new[] { 222, 242, 303, 304, 305, 431 })
            foreach (string file in Directory.GetFiles("Assets/Paths/p" + id, "*.fbx"))
            {
                string asset = file.Replace('\\', '/');
                string relative = "Paths/p" + id + "/" + Path.GetFileName(file);
                string fbx = root + "/Assets/Models/" + relative;
                string guid = AssetDatabase.AssetPathToGUID(asset);
                if (File.Exists(fbx + ".meta")) Check(File.ReadAllText(fbx + ".meta").Contains("guid: " + guid), "FBX GUID " + relative);
                else File.Copy(asset + ".meta", fbx + ".meta");
                string prefab = "Assets/Resources/Models/" + Path.ChangeExtension(relative, ".prefab");
                Check(ModelPrefabGenerator.CreateWrapperPrefab(asset, prefab), "prefab " + relative);
                string output = root + "/" + prefab;
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                if (File.Exists(output)) Check(File.ReadAllText(output).Contains("guid: " + guid), "prefab GUID " + relative);
                else { File.Copy(prefab, output); File.Copy(prefab + ".meta", output + ".meta"); }
            }
    }
}
