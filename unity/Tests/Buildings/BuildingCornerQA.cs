using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Use the main generator to verify that each arm of the actual FBX faces the corresponding edge at both convex and concave corners.
public static class BuildingCornerQA
{
    static Type builder;
    static int checks, parts, staticParts;
    static readonly Dictionary<GameObject, object> cornerSources = new Dictionary<GameObject, object>();
    static readonly List<Mesh> temporary = new List<Mesh>();
    [InitializeOnLoadMethod]
    static void ResumeInPlayMode()
    {
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool("BuildingCornerQA.Pending", false) && EditorApplication.isPlaying)
            {
                SessionState.SetBool("BuildingCornerQA.Pending", false);
                Run();
            }
        };
    }
    static Mesh Generate(GameObject prefab, float angle, float heading)
    {
        Vector2 next = Direction(heading), previous = Direction(heading + angle);
        object result = builder.GetMethod("GenerateCornerSegment").Invoke(null,
            new object[] { previous, Vector2.zero, next, 0f, prefab, new Material[0], 0f });
        Mesh reference = (Mesh)result.GetType().GetField("Mesh").GetValue(result);
        var placement = builder.GetMethod("CreateCornerPlacement", BindingFlags.NonPublic | BindingFlags.Static);
        if (!cornerSources.TryGetValue(prefab, out object cache))
        {
            cache = Activator.CreateInstance(placement.GetParameters()[7].ParameterType);
            cornerSources.Add(prefab, cache);
        }
        object shared = placement.Invoke(null, new object[] { previous, Vector2.zero, next, 0f, prefab, new Material[0], 0f, cache, temporary });
        Type type = shared.GetType();
        Mesh mesh = (Mesh)type.GetField("Mesh").GetValue(shared);
        Require((mesh == null) == (reference == null), "Shared corner changed omission rules");
        if (mesh != null)
        {
            Matrix4x4 matrix = (Matrix4x4)type.GetField("Transform").GetValue(shared);
            Vector3[] expected = reference.vertices, actual = mesh.vertices;
            Require(expected.Length == actual.Length, "Shared corner vertex count changed");
            for (int i = 0; i < actual.Length; i++)
                Require(Vector3.Distance(expected[i], matrix.MultiplyPoint3x4(actual[i])) < .002f, "Shared corner geometry changed");
            int owned = temporary.Count;
            object upper = placement.Invoke(null, new object[] { previous, Vector2.zero, next, 3f, prefab, new Material[0], 4f, cache, temporary });
            Require(ReferenceEquals(mesh, type.GetField("Mesh").GetValue(upper)) && owned == temporary.Count,
                "Equal corner angle rebaked at another floor/height");
        }
        return reference;
    }
    static Vector2 Direction(float angle) => new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
    static void Require(bool value, string message)
    {
        checks++;
        if (!value) throw new Exception(message);
    }
    static void CheckFenceFallback()
    {
        var assembly = builder.Assembly;
        var managerType = assembly.GetType("ModelManager");
        var managerObject = new GameObject("FenceFallbackQA");
        var manager = managerObject.AddComponent(managerType);
        var cache = (IDictionary)managerType.GetField("_modelCache", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(manager);
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var corner = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var fence = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var closeFence = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var meshes = new List<Mesh>();
        var utils = assembly.GetType("BuildingMeshUtils");
        var drawLod = utils.GetProperty("CurrentDrawLod");
        var originalLod = drawLod.GetValue(null);
        const string prefix = "__fence_qa";
        string path = "Models/Buildings/" + prefix + "/0/";
        var load = builder.GetMethod("LoadPartWithFallback", BindingFlags.NonPublic | BindingFlags.Static);
        Func<string, string, int, GameObject> resolve = (level, type, position) =>
            (GameObject)load.Invoke(null, new object[] { prefix, level, type, position, 0, null });
        try
        {
            drawLod.SetValue(null, Enum.Parse(assembly.GetType("DrawLod"), "Normal"));
            cache[path + "mid-w1"] = wall;
            cache[path + "mid-c1"] = corner;
            Require(resolve("fence", "w", 3) == null, "Missing fence wall resolves to a building wall");
            Require(resolve("fence", "c", 3) == null, "Missing fence corner resolves to a building corner");
            Require(resolve("base", "w", 3) == wall, "Base wall lost its mid fallback");
            Require(resolve("top", "c", 3) == corner, "Top corner lost its mid fallback");
            var combines = new List<CombineInstance>();
            var materials = new List<Material>();
            var polygon = new[] { Vector2.zero, new Vector2(20, 0), new Vector2(20, 20), new Vector2(0, 20) };
            builder.GetMethod("GenerateSiteDecorations").Invoke(null, new object[] {
                prefix, 0, polygon, null, null, null, Vector3.zero, false,
                combines, materials, meshes, null, 0f, null, 0f
            });
            Require(combines.Count == 0, "A site without fence assets generated exterior walls");
            cache[path + "fence-w1"] = fence;
            Require(resolve("fence", "w", 3) == fence, "Fence position fallback missing");
            Require(resolve("fence", "c", 3) == null, "Absent fence corner uses a building corner");
            cache[path + "fence-w3"] = fence;
            cache[path + "fence-w1"] = wall;
            Require(resolve("fence", "w", 3) == fence, "Specified fence position lost priority");
            cache.Remove(path + "fence-w3");
            cache[path + "fence-w1"] = fence;
            cache[path + "fence-w1_close"] = closeFence;
            drawLod.SetValue(null, Enum.Parse(assembly.GetType("DrawLod"), "Close"));
            Require(resolve("fence", "w", 3) == closeFence, "Close fence position fallback missing");
        }
        finally
        {
            drawLod.SetValue(null, originalLod);
            foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
            foreach (var go in new[] { wall, corner, fence, closeFence, managerObject }) UnityEngine.Object.DestroyImmediate(go);
        }
    }
    public static void Run()
    {
        if (!EditorApplication.isPlaying)
        {
            SessionState.SetBool("BuildingCornerQA.Pending", true);
            EditorApplication.EnterPlaymode();
            return;
        }
        string[] args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-sourceRoot") + 1];
        string report = root + "/Temp/BuildingCornerQA/result.txt";
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => {
                foreach (string dir in new[] { "Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins" })
                {
                    string file = Path.Combine(root, dir, new AssemblyName(e.Name).Name + ".dll");
                    if (File.Exists(file)) return Assembly.LoadFrom(file);
                }
                return null;
            };
            builder = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll").GetType("ModularBodyBuilder");
            CheckFenceFallback();
            foreach (string file in Directory.GetFiles("Assets/Buildings", "*-c1.fbx", SearchOption.AllDirectories))
            {
                string path = file.Replace('\\', '/');
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var smr = prefab.GetComponentInChildren<SkinnedMeshRenderer>();
                Debug.Log($"Corner QA: {path}, bones={(smr == null ? 0 : smr.bones.Length)}");
                if (smr == null || smr.bones.Length < 2)
                {
                    staticParts++;
                    foreach (float heading in new[] { 0f, 37f, 180f })
                    {
                        Require(Generate(prefab, 270f, heading) == null, path + ": static concave corner protrudes");
                        foreach (bool next in new[] { true, false })
                        {
                            var measure = builder.GetMethod("MeasureCornerExtentAlongEdge", BindingFlags.NonPublic | BindingFlags.Static);
                            float extent = (float)measure.Invoke(null, new object[] { prefab, Direction(heading + 270f), Vector2.zero, Direction(heading), 0f, next });
                            Require(extent == 0f, path + ": skipped corner leaves a wall gap");
                        }
                    }
                    Mesh convex = Generate(prefab, 90f, 0f);
                    Require(convex != null && convex.vertexCount > 0, path + ": static convex corner missing");
                    UnityEngine.Object.DestroyImmediate(convex);
                    continue;
                }
                parts++;
                Mesh baseline = Generate(prefab, 90f, 0f);
                Vector3[] reference = baseline.vertices;
                BoneWeight[] weights = smr.sharedMesh.boneWeights;
                foreach (float angle in new[] { 60f, 90f, 135f, 179.99f, 180f, 180.01f, 225f, 270f, 300f })
                foreach (float heading in new[] { 0f, 37f, 180f })
                {
                    Mesh mesh = Generate(prefab, angle, heading);
                    Vector3[] vertices = mesh.vertices;
                    int[] tips = new int[2];
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        BoneWeight weight = weights[i];
                        if (weight.weight0 < .999f || weight.boneIndex0 > 1) continue;
                        int arm = weight.boneIndex0;
                        // Vertices on a single bone, including those at the connected end, match the positions obtained by rigidly rotating the 90-degree shape for each edge.
                        float turn = heading + (arm == 0 ? 0f : angle - 90f);
                        Vector3 expected = Quaternion.Euler(0, -turn, 0) * reference[i];
                        Require(Vector3.Distance(vertices[i], expected) < .002f,
                            $"{path}: angle={angle}, heading={heading}, arm={arm}, vertex={i}, expected={expected}, actual={vertices[i]}");
                        tips[arm]++;
                    }
                    Require(tips[0] > 0 && tips[1] > 0, path + ": missing rigid arm vertices");
                    // Even for asymmetric arms, the sender-side and receiver-side wall offsets do not swap at concave corners.
                    foreach (bool next in new[] { true, false })
                    {
                        var measure = builder.GetMethod("MeasureCornerExtentAlongEdge", BindingFlags.NonPublic | BindingFlags.Static);
                        float extent = (float)measure.Invoke(null, new object[] { prefab, Direction(heading + angle), Vector2.zero, Direction(heading), 0f, next });
                        float referenceExtent = (float)measure.Invoke(null, new object[] { prefab, Vector2.up, Vector2.zero, Vector2.right, 0f, next });
                        Require(Mathf.Abs(extent - referenceExtent) < .002f, path + ": corner extent changed");
                    }
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
                UnityEngine.Object.DestroyImmediate(baseline);
            }
            Require(parts > 0, "No rigged corner FBX tested");
            foreach (Mesh mesh in temporary) UnityEngine.Object.DestroyImmediate(mesh);
            File.WriteAllText(report, $"PASS: {checks} checks, {parts} rigged FBX corners (9 angles x 3 headings), {staticParts} static FBX corners");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(report, e.ToString());
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }
}
