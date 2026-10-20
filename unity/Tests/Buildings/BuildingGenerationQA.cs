using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

// Verify the multi-story frame in the actual FBX, comparing generation time and the number of temporary meshes for identical input.
public static class BuildingGenerationQA
{
    static Type builder, utils;
    static int checks;
    static string root, output;
    static void Require(bool ok, string message)
    {
        checks++;
        if (!ok) throw new Exception(message);
    }
    static string Arg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, name);
        return index < 0 ? null : args[index + 1];
    }
    [InitializeOnLoadMethod]
    static void Resume()
    {
        EditorApplication.delayCall += () => {
            if (EditorApplication.isPlaying && SessionState.GetBool("BuildingGenerationQA.Pending", false))
            {
                SessionState.SetBool("BuildingGenerationQA.Pending", false);
                Run();
            }
        };
    }
    static Mesh Body(Vector2[] polygon, int floors, float variant, string lod, out int temporaryCount)
    {
        utils.GetProperty("CurrentDrawLod").SetValue(null, Enum.Parse(builder.Assembly.GetType("DrawLod"), lod));
        object data = builder.GetMethod("GenerateBody").Invoke(null, new object[] {
            polygon, floors, "a2001", 3f, variant, 2, 1, null, Vector3.zero, false, null, null, null, 0f
        });
        Type type = data.GetType();
        var instances = (List<CombineInstance>)type.GetField("CombineInstances").GetValue(data);
        var materials = (List<Material>)type.GetField("Materials").GetValue(data);
        var temporary = (List<Mesh>)type.GetField("TemporaryMeshes").GetValue(data);
        temporaryCount = temporary.Count;
        Require(instances.Count == materials.Count && instances.Count > 0, "Invalid material/instance correspondence");
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        try
        {
            mesh.CombineMeshes(instances.ToArray(), false, true);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            Require(mesh.vertexCount > 0 && mesh.bounds.max.y >= (floors - 1) * 3f, "Missing upper floors");
            Require(new HashSet<Mesh>(temporary).Count == temporary.Count, "Mesh ownership repeated");
            return mesh;
        }
        catch { UnityEngine.Object.DestroyImmediate(mesh); throw; }
        finally { foreach (Mesh item in temporary) UnityEngine.Object.DestroyImmediate(item); }
    }
    static void Snapshot(string name, Mesh mesh)
    {
        string file = Path.Combine(output, name + ".bin");
        string baseline = Arg("-baselineDir");
        if (baseline != null)
        {
            using (var reader = new BinaryReader(File.OpenRead(Path.Combine(baseline, name + ".bin"))))
            {
                var vertices = mesh.vertices;
                Require(reader.ReadInt32() == vertices.Length, name + ": vertex count changed");
                foreach (Vector3 v in vertices)
                    Require(Vector3.Distance(v, new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())) < .002f,
                        name + ": geometry changed");
                var uv = mesh.uv;
                Require(reader.ReadInt32() == uv.Length, name + ": UV count changed");
                foreach (Vector2 v in uv)
                    Require(Vector2.Distance(v, new Vector2(reader.ReadSingle(), reader.ReadSingle())) < .00001f, name + ": UV changed");
                var normals = mesh.normals;
                Require(reader.ReadInt32() == normals.Length, name + ": normal count changed");
                foreach (Vector3 v in normals)
                    Require(Vector3.Distance(v, new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())) < .01f,
                        name + ": normal changed");
                Require(reader.ReadInt32() == mesh.subMeshCount, name + ": material slots changed");
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    int[] indices = mesh.GetTriangles(sub);
                    Require(reader.ReadInt32() == indices.Length, name + ": triangle count changed");
                    foreach (int index in indices) Require(reader.ReadInt32() == index, name + ": topology changed");
                }
            }
        }
        using (var writer = new BinaryWriter(File.Create(file)))
        {
            var vertices = mesh.vertices;
            writer.Write(vertices.Length);
            foreach (Vector3 v in vertices) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
            var uv = mesh.uv;
            writer.Write(uv.Length);
            foreach (Vector2 v in uv) { writer.Write(v.x); writer.Write(v.y); }
            var normals = mesh.normals;
            writer.Write(normals.Length);
            foreach (Vector3 v in normals) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
            writer.Write(mesh.subMeshCount);
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                int[] indices = mesh.GetTriangles(sub);
                writer.Write(indices.Length);
                foreach (int index in indices) writer.Write(index);
            }
        }
    }
    static Mesh Combine(List<CombineInstance> instances)
    {
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.CombineMeshes(instances.ToArray(), false, true);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
    static void CheckScaffolding(IDictionary cache, Vector2[] polygon)
    {
        var wall = (GameObject)cache["Models/Buildings/a2001/0/mid-w1"];
        var corner = (GameObject)cache["Models/Buildings/a2001/0/mid-c1"];
        var wallMaterials = (Material[])utils.GetMethod("GetModuleMaterials").Invoke(null, new object[] { wall });
        var cornerMaterials = (Material[])utils.GetMethod("GetModuleMaterials").Invoke(null, new object[] { corner });
        var actual = new List<CombineInstance>();
        var expected = new List<CombineInstance>();
        var actualMaterials = new List<Material>();
        var expectedMaterials = new List<Material>();
        var actualOwned = new List<Mesh>();
        var expectedOwned = new List<Mesh>();
        var measure = builder.GetMethod("MeasureCornerExtentAlongEdge", BindingFlags.Static | BindingFlags.NonPublic);
        var append = builder.GetMethod("AddSegmentToCombineList", BindingFlags.Static | BindingFlags.NonPublic);
        Mesh actualMesh = null, expectedMesh = null;
        try
        {
            builder.GetMethod("GenerateScaffoldSegments").Invoke(null, new object[] {
                polygon, wall, wallMaterials, corner, cornerMaterials, 3f, 12, actual, actualMaterials, actualOwned
            });
            for (int floor = 0; floor < 12; floor++)
            for (int i = 0; i < polygon.Length; i++)
            {
                int n = polygon.Length;
                Vector2 p0 = polygon[(i + n - 1) % n], p1 = polygon[i], p2 = polygon[(i + 1) % n], p3 = polygon[(i + 2) % n];
                float start = (float)measure.Invoke(null, new object[] { corner, p0, p1, p2, 3f, true });
                float end = (float)measure.Invoke(null, new object[] { corner, p1, p2, p3, 3f, false });
                var parts = (IEnumerable)builder.GetMethod("GenerateWallSegments").Invoke(null,
                    new object[] { p1, p2, floor * 3f, wall, wallMaterials, 3f, start, end });
                foreach (object part in parts) append.Invoke(null, new object[] { part, expected, expectedMaterials, expectedOwned });
                object joint = builder.GetMethod("GenerateCornerSegment").Invoke(null,
                    new object[] { p0, p1, p2, floor * 3f, corner, cornerMaterials, 3f });
                if ((bool)joint.GetType().GetProperty("IsValid").GetValue(joint))
                    append.Invoke(null, new object[] { joint, expected, expectedMaterials, expectedOwned });
            }
            Require(actual.Count == expected.Count && actualMaterials.Count == expectedMaterials.Count, "Scaffold parts changed");
            for (int i = 0; i < actualMaterials.Count; i++) Require(actualMaterials[i] == expectedMaterials[i], "Scaffold material changed");
            actualMesh = Combine(actual); expectedMesh = Combine(expected);
            Vector3[] a = actualMesh.vertices, e = expectedMesh.vertices;
            Require(a.Length == e.Length, "Scaffold vertex count changed");
            for (int i = 0; i < a.Length; i++) Require(Vector3.Distance(a[i], e[i]) < .002f, "Scaffold geometry changed");
            Vector3[] an = actualMesh.normals, en = expectedMesh.normals;
            for (int i = 0; i < an.Length; i++) Require(Vector3.Distance(an[i], en[i]) < .01f, "Scaffold normal changed");
            for (int sub = 0; sub < actualMesh.subMeshCount; sub++)
            {
                int[] ai = actualMesh.GetTriangles(sub), ei = expectedMesh.GetTriangles(sub);
                Require(ai.Length == ei.Length, "Scaffold triangle count changed");
                for (int i = 0; i < ai.Length; i++) Require(ai[i] == ei[i], "Scaffold topology changed");
            }
            Require(actualOwned.Count < expectedOwned.Count, "Scaffold still allocates individual wall meshes");
        }
        finally
        {
            if (actualMesh != null) UnityEngine.Object.DestroyImmediate(actualMesh);
            if (expectedMesh != null) UnityEngine.Object.DestroyImmediate(expectedMesh);
            foreach (Mesh mesh in actualOwned) UnityEngine.Object.DestroyImmediate(mesh);
            foreach (Mesh mesh in expectedOwned) UnityEngine.Object.DestroyImmediate(mesh);
        }
    }
    public static void Run()
    {
        if (!EditorApplication.isPlaying)
        {
            SessionState.SetBool("BuildingGenerationQA.Pending", true);
            EditorApplication.EnterPlaymode();
            return;
        }
        root = Arg("-sourceRoot");
        output = Arg("-generationOutput");
        Directory.CreateDirectory(output);
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
            var assembly = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            builder = assembly.GetType("ModularBodyBuilder");
            utils = assembly.GetType("BuildingMeshUtils");
            var managerType = assembly.GetType("ModelManager");
            var managerObject = new GameObject("BuildingGenerationQA");
            var manager = managerObject.AddComponent(managerType);
            var cache = (IDictionary)managerType.GetField("_modelCache", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(manager);
            var sourceVertices = new Dictionary<Mesh, Vector3[]>();
            foreach (string path in Directory.GetFiles("Assets/Models/Buildings/a2001", "*.fbx", SearchOption.AllDirectories))
            {
                string asset = path.Replace('\\', '/');
                var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
                if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
                cache[asset.Substring("Assets/".Length, asset.Length - "Assets/".Length - 4)] = prefab;
                foreach (MeshFilter filter in prefab.GetComponentsInChildren<MeshFilter>())
                    if (filter.sharedMesh != null) sourceVertices[filter.sharedMesh] = filter.sharedMesh.vertices;
                foreach (SkinnedMeshRenderer renderer in prefab.GetComponentsInChildren<SkinnedMeshRenderer>())
                    if (renderer.sharedMesh != null) sourceVertices[renderer.sharedMesh] = renderer.sharedMesh.vertices;
            }
            var rectangle = new[] { new Vector2(-19.4935887f, -38.9871774f), new Vector2(19.4935887f, -38.9871774f),
                new Vector2(19.4935887f, 38.9871774f), new Vector2(-19.4935887f, 38.9871774f) };
            var concave = new[] { Vector2.zero, new Vector2(32, 0), new Vector2(32, 12),
                new Vector2(12, 12), new Vector2(12, 32), new Vector2(0, 32) };
            var rotated = new Vector2[rectangle.Length];
            for (int i = 0; i < rotated.Length; i++)
            {
                Vector3 p = Quaternion.Euler(0, 37, 0) * new Vector3(rectangle[i].x, 0, rectangle[i].y);
                rotated[i] = new Vector2(p.x, p.z);
            }
            var report = new StringBuilder();
            report.AppendLine("style,lod,floors,mean_ms,temporary_meshes,vertices");
            foreach (string lod in new[] { "Normal", "Close" })
            for (int style = 0; style < 5; style++)
            {
                float variant = style * .2f + .05f;
                int count;
                Mesh mesh = Body(rectangle, 12, variant, lod, out count);
                Snapshot("rect_" + style + "_" + lod, mesh);
                UnityEngine.Object.DestroyImmediate(mesh);
                double total = 0;
                long vertices = 0;
                for (int sample = 0; sample < 4; sample++)
                {
                    var watch = Stopwatch.StartNew();
                    mesh = Body(rectangle, 12, variant, lod, out count);
                    total += watch.Elapsed.TotalMilliseconds;
                    vertices = mesh.vertexCount;
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
                report.AppendLine($"{style},{lod},12,{total / 4:F3},{count},{vertices}");
            }
            foreach (var item in new[] { ("concave", concave), ("rotated", rotated) })
            foreach (int floors in new[] { 1, 2, 12 })
            {
                Mesh mesh = Body(item.Item2, floors, .65f, "Normal", out _);
                Snapshot(item.Item1 + "_" + floors, mesh);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
            CheckScaffolding(cache, concave);
            foreach (var source in sourceVertices)
            {
                Vector3[] after = source.Key.vertices;
                Require(after.Length == source.Value.Length, "Shared source vertex count changed");
                for (int i = 0; i < after.Length; i++) Require(after[i] == source.Value[i], "Shared source mesh was modified");
            }
            File.WriteAllText(Path.Combine(output, "timings.csv"), report.ToString());
            File.WriteAllText(Path.Combine(output, "result.txt"), $"PASS: {checks} checks; 5 styles x Normal/Close, 1/2/12 floors, rotated/concave footprints; source meshes unchanged");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(output, "result.txt"), e.ToString());
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }
}
