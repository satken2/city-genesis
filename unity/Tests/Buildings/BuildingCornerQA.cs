using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// 実FBXの各アームが、凸角・凹角とも対応する辺へ向くことを本編の生成器で検査する。
public static class BuildingCornerQA
{
    static Type builder;
    static int checks, parts, staticParts;
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
        return (Mesh)result.GetType().GetField("Mesh").GetValue(result);
    }
    static Vector2 Direction(float angle) => new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
    static void Require(bool value, string message)
    {
        checks++;
        if (!value) throw new Exception(message);
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
                        // 接続端を含む単一ボーンの頂点は、90°時の形を辺ごとに剛体回転した位置に一致する。
                        float turn = heading + (arm == 0 ? 0f : angle - 90f);
                        Vector3 expected = Quaternion.Euler(0, -turn, 0) * reference[i];
                        Require(Vector3.Distance(vertices[i], expected) < .002f,
                            $"{path}: angle={angle}, heading={heading}, arm={arm}, vertex={i}, expected={expected}, actual={vertices[i]}");
                        tips[arm]++;
                    }
                    Require(tips[0] > 0 && tips[1] > 0, path + ": missing rigid arm vertices");
                    // 非対称アームでも、凹角で送り側・受け側の壁オフセットが入れ替わらない。
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
