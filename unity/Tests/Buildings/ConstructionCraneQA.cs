using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class ConstructionCraneQA
{
    static int checks;
    static void Require(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    [InitializeOnLoadMethod]
    static void Resume()
    {
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool("ConstructionCraneQA.Pending", false) && EditorApplication.isPlaying)
            {
                SessionState.SetBool("ConstructionCraneQA.Pending", false);
                Run();
            }
        };
    }

    public static void Run()
    {
        if (!EditorApplication.isPlaying)
        {
            SessionState.SetBool("ConstructionCraneQA.Pending", true);
            EditorApplication.EnterPlaymode();
            return;
        }
        string[] args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-sourceRoot") + 1];
        string report = root + "/Temp/ConstructionCraneQA/result.txt";
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
            Type animator = assembly.GetType("ConstructionCraneAnimator");
            var evaluate = (Func<int, double, float>)Delegate.CreateDelegate(typeof(Func<int, double, float>), animator.GetMethod("EvaluateYaw"));
            UnityEngine.Random.InitState(274);
            var randomBefore = UnityEngine.Random.state;
            float maxSpeed = 0;
            bool clockwise = false, counterclockwise = false, paused = false;
            foreach (int id in new[] { 1, 2, 91, 700, int.MaxValue })
            for (int i = 0; i < 12000; i++)
            {
                double time = i * .1;
                float delta = Mathf.DeltaAngle(evaluate(id, time), evaluate(id, time + .1));
                maxSpeed = Mathf.Max(maxSpeed, Mathf.Abs(delta) / .1f);
                Require(Mathf.Abs(delta) < .43f, "Angular jump or excessive speed");
                clockwise |= delta > .01f;
                counterclockwise |= delta < -.01f;
                paused |= delta == 0f;
            }
            Require(clockwise && counterclockwise && paused, "Missing turns or pauses");
            Require(randomBefore.Equals(UnityEngine.Random.state), "Global random state changed");
            Require(Mathf.Abs(Mathf.DeltaAngle(evaluate(1, 100), evaluate(2, 100))) > 1, "Cranes synchronized");
            Require(Mathf.Abs(Mathf.DeltaAngle(evaluate(91, 1e8), evaluate(91, 1e8 + .1))) < .43f,
                "Long-running clock loses smooth motion");

            Type managerType = assembly.GetType("ModelManager");
            var manager = new GameObject("Models").AddComponent(managerType);
            var cache = (IDictionary)managerType.GetField("_modelCache", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(manager);
            foreach (string part in new[] { "crane-base", "crane-mid", "crane-top" })
            {
                string path = "Assets/Crane/" + part + ".fbx";
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                importer.isReadable = true;
                importer.SaveAndReimport();
                cache["Models/Buildings/common/" + part] = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }
            MethodInfo add = assembly.GetType("ConstructingBuildingMeshBuilder").GetMethod("AddCrane", BindingFlags.NonPublic | BindingFlags.Static);
            var footprint = new[] { new Vector2(-8,-6), new Vector2(8,-6), new Vector2(8,6), new Vector2(-8,6) };
            foreach (int floors in new[] { 1, 7, 30 })
            {
                var staticParts = new List<CombineInstance>();
                var movingParts = new List<CombineInstance>();
                var temporary = new List<Mesh>();
                object Make(bool moving, List<CombineInstance> parts) => add.Invoke(null, new object[] {
                    footprint, "a999999", .3f, .7f, 4f, floors, "crane-base", "crane-mid", "crane-top",
                    parts, new List<Material>(), temporary, moving });
                Require(Make(false, staticParts) == null, "Static path unexpectedly split");
                object placement = Make(true, movingParts);
                Require(placement != null, "Missing animated top");
                var type = placement.GetType();
                Vector3 position = (Vector3)type.GetField("Position").GetValue(placement);
                Quaternion rotation = (Quaternion)type.GetField("Rotation").GetValue(placement);
                Require(Mathf.Abs(position.y - (floors + 2) * 4f) < .001f, "Wrong connection height");
                Require(staticParts.Count > movingParts.Count, "Top remains in static body");

                var building = new GameObject("Building");
                animator.GetMethod("Attach").Invoke(null, new object[] { building, placement, 91 });
                Transform top = building.transform.GetChild(0);
                Mesh topMesh = top.GetComponent<MeshFilter>().sharedMesh;
                Vector3[] staticVertices = staticParts[movingParts.Count].mesh.vertices;
                Vector3[] localVertices = topMesh.vertices;
                Require(localVertices.Length == staticVertices.Length, "Top geometry changed");
                for (int i = 0; i < localVertices.Length; i++)
                    Require(Vector3.Distance(position + rotation * localVertices[i], staticVertices[i]) < .001f, "Pivot or scale changed");
                Require(top.GetComponent<Collider>() == null, "Unexpected crane collider");
                Require(top.GetComponent(assembly.GetType("RenderCacheIdentity")) != null, "Mesh owner missing");
                Quaternion pose = top.localRotation;
                building.SetActive(false);
                building.SetActive(true);
                Require(Quaternion.Angle(pose, top.localRotation) < .001f, "Cache restore resets rotation");
                var rebuilt = new GameObject("Rebuilt");
                animator.GetMethod("Attach").Invoke(null, new object[] { rebuilt, placement, 91 });
                Require(Quaternion.Angle(pose, rebuilt.transform.GetChild(0).localRotation) < .001f, "Rebuild resets rotation");
                UnityEngine.Object.DestroyImmediate(building);
                UnityEngine.Object.DestroyImmediate(rebuilt);
                foreach (Mesh mesh in temporary) UnityEngine.Object.DestroyImmediate(mesh);
            }
            cache.Remove("Models/Buildings/common/crane-base");
            cache.Remove("Models/Buildings/common/crane-mid");
            object missing = add.Invoke(null, new object[] { footprint, "a999999", 0f, 0f, 4f, 1,
                "absent-base", "absent-mid", "crane-top", new List<CombineInstance>(), new List<Material>(), new List<Mesh>(), true });
            Require(missing == null, "Floating top without mast");
            File.WriteAllText(report, $"PASS: {checks} checks; real crane FBX, 3 heights, static/split geometry, pose continuity, random isolation; maximum sampled speed {maxSpeed:F3} degrees/s");
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
