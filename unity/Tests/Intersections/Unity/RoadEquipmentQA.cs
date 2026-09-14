using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// 本編と同じ生成器・実FBXを使い、道路面と設備の接地点を比較する。
public static class RoadEquipmentQA
{
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static Type pathType, itemType;
    static Assembly assembly;
    static IDictionary masters, models;
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
                foreach (string dir in new[] { "Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins" })
                {
                    string file = Path.Combine(root, dir, name);
                    if (File.Exists(file)) return Assembly.LoadFrom(file);
                }
                return null;
            };
            assembly = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            pathType = assembly.GetType("GeneratedModels.PathSummary");
            itemType = assembly.GetType("PathMasterItem");
            masters = CreateManager("MasterDataStore", "pathMasterById");
            models = CreateManager("ModelManager", "_modelCache");
            foreach (string file in Directory.GetFiles("Assets/Paths", "*.fbx", SearchOption.AllDirectories))
            {
                string asset = file.Replace('\\', '/');
                var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
                if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
                models["Models/Paths/" + asset.Substring("Assets/Paths/".Length).Replace(".fbx", "")] = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            }
            foreach (int variant in new[] { 100, 101, 201, 211, 212, 221, 222, 231, 301 })
                foreach (float grade in new[] { 0f, .15f, -.15f })
                    foreach (float sign in new[] { 1f, -1f })
                        foreach (int utilityMode in new[] { 1, 2 })
                            CheckRoad(variant, grade, sign, utilityMode);
            File.WriteAllText(report, "PASS equipment: " + checks + " contact/direction checks across 108 roads");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(report, e.ToString());
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    static IDictionary CreateManager(string name, string field)
    {
        Type type = assembly.GetType(name);
        var component = new GameObject(name).AddComponent(type);
        type.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, component);
        return (IDictionary)type.GetField(field, PrivateInstance).GetValue(component);
    }

    static void Set(object target, string name, object value) => target.GetType().GetField(name).SetValue(target, value);

    static void CheckRoad(int variant, float grade, float sign, int utilityMode)
    {
        int type = variant / 100;
        float width = variant == 231 ? 24 : variant == 221 || variant == 222 ? 12 : variant == 201 || variant == 211 ? 3 : variant == 212 ? 6 : 7;
        float sidewalk = variant == 231 ? 3 : variant == 221 || variant == 222 ? 2.5f : 0;
        var item = Activator.CreateInstance(itemType);
        Set(item, "width", width); Set(item, "sidewalk_width", sidewalk); Set(item, "lanes", 2);
        Set(item, "follow_terrain", false);
        Set(item, "streetlight_type_allowed", variant != 201);
        Set(item, "street_tree_type_allowed", sidewalk > 0);
        Set(item, "fence_type_allowed", sidewalk > 0);
        Set(item, "utility_mode_allowed", type == 2);
        masters[variant] = item;
        var path = Activator.CreateInstance(pathType);
        Set(path, "path_id", variant); Set(path, "path_variant", variant); Set(path, "path_type", type);
        Set(path, "follow_terrain", false);
        foreach (string field in new[] { "streetlight_type", "street_tree_type", "fence_type", "utility_mode" }) Set(path, field, 1);
        Set(path, "utility_mode", utilityMode);
        Type roadType = assembly.GetType("RoadPath");
        var setback = Activator.CreateInstance(assembly.GetType("SidewalkSetback"));
        var result = roadType.GetMethod("Generate").Invoke(Activator.CreateInstance(roadType), new[] {
            path, new List<Vector3> { Vector3.zero, new Vector3(0, grade * 80, sign * 80) }, setback, setback
        });
        var road = (GameObject)result.GetType().GetProperty("GeneratedObject").GetValue(result);
        foreach (var component in road.GetComponentsInChildren<Component>())
        {
            if (component == null || component.GetType().Name != "SplineMesh") continue;
            MethodInfo rebuild = component.GetType().GetMethod("RebuildImmediate");
            rebuild.Invoke(component, rebuild.GetParameters().Length == 0 ? null : new object[] { true });
        }
        var surfaces = road.GetComponentsInChildren<MeshFilter>().Where(f => f.name.EndsWith("/body") || f.name.EndsWith("/sidewalk")).ToArray();
        var lamps = road.GetComponentsInChildren<MeshFilter>().Where(f => f.name.StartsWith("streetlight")).ToArray();
        if (variant == 201)
        {
            Require(!road.GetComponentsInChildren<MeshFilter>().Any(f =>
                new[] { "streetlight", "utility_box" }.Any(n => f.name.StartsWith(n))), "Unpaved road lamps and boxes must be absent");
            Require(road.GetComponentsInChildren<MeshFilter>().Any(f => f.name.StartsWith("utility_pole")) == (utilityMode == 1), "Unpaved road must retain overhead poles only");
            Require(road.GetComponentsInChildren<MeshFilter>().Any(f => f.name == "EquipmentMetal") == (utilityMode == 1), "Unpaved overhead wires must remain");
        }
        else if (variant == 211 || variant == 212)
            Require(lamps.Length > 0 && lamps.All(f => f.transform.position.x * sign > 0), variant + " lamps must stay on the forward right side");
        else
            Require(lamps.Any(f => f.transform.position.x < 0) && lamps.Any(f => f.transform.position.x > 0), variant + " lamps must cover both sides");
        foreach (var filter in road.GetComponentsInChildren<MeshFilter>())
        {
            if (!new[] { "streetlight", "utility_pole", "utility_box", "tree" }.Any(n => filter.name.StartsWith(n))) continue;
            Vector3 foot = filter.transform.TransformPoint(new Vector3(0, filter.sharedMesh.bounds.min.y, 0));
            float surface = SurfaceAt(surfaces, foot);
            if (float.IsNegativeInfinity(surface)) surface = grade * sign * foot.z + .1f;
            Require(Mathf.Abs(foot.y - surface) < .005f, variant + " grade=" + grade + " floating " + filter.name + " delta=" + (foot.y - surface));
            if (filter.name.StartsWith("streetlight") && Mathf.Abs(filter.sharedMesh.bounds.center.x) > .01f)
            {
                Vector3 head = filter.transform.TransformPoint(filter.sharedMesh.bounds.center);
                Require((head.x - foot.x) * foot.x < 0, variant + " outward lamp");
            }
        }
        // 支柱は設備バッチへ結合される。下端の高さも路面と比較する。
        foreach (var filter in road.GetComponentsInChildren<MeshFilter>().Where(f => f.name == "EquipmentMetal" && sidewalk > 0))
            foreach (var vertex in filter.sharedMesh.vertices)
            {
                Vector3 world = filter.transform.TransformPoint(vertex);
                float surface = SurfaceAt(surfaces, world);
                if (Mathf.Abs(world.x) < width / 2 - sidewalk + .18f && world.y - surface < .1f)
                    Require(Mathf.Abs(world.y - surface) < .02f, variant + " grade=" + grade + " sign=" + sign + " floating fence post at=" + world.ToString("F4") + " surface=" + surface);
            }
        UnityEngine.Object.DestroyImmediate(road);
    }

    static void Require(bool valid, string message) { if (!valid) throw new Exception(message); checks++; }

    static float SurfaceAt(MeshFilter[] filters, Vector3 point)
    {
        float height = float.NegativeInfinity;
        foreach (var filter in filters)
        {
            Vector3[] vertices = filter.sharedMesh.vertices;
            int[] triangles = filter.sharedMesh.triangles;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = filter.transform.TransformPoint(vertices[triangles[i]]);
                Vector3 b = filter.transform.TransformPoint(vertices[triangles[i + 1]]);
                Vector3 c = filter.transform.TransformPoint(vertices[triangles[i + 2]]);
                float det = (b.z-c.z)*(a.x-c.x)+(c.x-b.x)*(a.z-c.z);
                if (Mathf.Abs(det) < 1e-8f) continue;
                float u = ((b.z-c.z)*(point.x-c.x)+(c.x-b.x)*(point.z-c.z))/det;
                float v = ((c.z-a.z)*(point.x-c.x)+(a.x-c.x)*(point.z-c.z))/det;
                if (u >= -.0001f && v >= -.0001f && u+v <= 1.0001f)
                    height = Mathf.Max(height, u*a.y+v*b.y+(1-u-v)*c.y);
            }
        }
        return height;
    }
}
