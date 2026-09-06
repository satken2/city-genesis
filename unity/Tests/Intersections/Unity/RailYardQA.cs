using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// 本編の配置計画と実FBXによる結合メッシュを、独立したUnityで検証する。
public static class RailYardQA
{
    static int checks;
    static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    static void Require(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    public static void Run()
    {
        string[] args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-intersectionSourceRoot") + 1];
        string report = root + "/Temp/IntersectionUnityQA/result.txt";
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => {
                foreach (string dir in new[] { "Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins" }) {
                    string file = Path.Combine(root, dir, new AssemblyName(e.Name).Name + ".dll");
                    if (File.Exists(file)) return Assembly.LoadFrom(file);
                }
                return null;
            };
            var assembly = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            Type yard = assembly.GetType("RailYardBuildingMeshBuilder");
            CheckPlan(yard);
            Type modelType = assembly.GetType("ModelManager");
            var model = new GameObject("Models").AddComponent(modelType);
            modelType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, model);
            var cache = (IDictionary)modelType.GetField("_modelCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(model);
            foreach (string part in new[] { "yard_track", "yard_wire", "yard_pole", "yard_beam", "yard_stop", "yard_shed", "floor" })
            {
                string asset = $"Assets/Buildings/a1060/0/{part}.fbx";
                var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
                importer.isReadable = true; importer.SaveAndReimport();
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
                cache[$"Models/Buildings/b0/0/{part}"] = prefab;
                cache[$"Models/Buildings/a1060/0/{part}"] = prefab;
            }
            var summary = assembly.GetType("GeneratedModels.BuildingSummary");
            var building = Activator.CreateInstance(summary);
            summary.GetField("variant").SetValue(building, 1060);
            summary.GetField("_polygonArrayCache", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(building,
                new[] { new[] { -19f, -80f }, new[] { 19f, -80f }, new[] { 19f, 80f }, new[] { -19f, 80f } });
            object result = yard.GetMethod("Generate").Invoke(Activator.CreateInstance(yard), new[] { building, (object)false });
            var mesh = (Mesh)result.GetType().GetProperty("Mesh").GetValue(result);
            var materials = (Material[])result.GetType().GetProperty("Materials").GetValue(result);
            Require(mesh.vertexCount > 5000, "Yard fell back to a box");
            Require(mesh.vertices.All(v => !float.IsNaN(v.x) && !float.IsInfinity(v.z)), "Non-finite mesh");
            Require(mesh.bounds.max.z <= 80.01f, "Lead extends beyond terminal");
            Require(!Covers(mesh, new Vector2(18f, 60f)), "Floor covers empty corner");
            Require(Covers(mesh, new Vector2(0f, 77f)), "Missing terminal lead");
            Render(root, mesh, materials);
            File.WriteAllText(report, $"PASS rail yard: {checks} checks; {mesh.vertexCount} vertices\n");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { File.WriteAllText(report, "FAIL " + e); Debug.LogException(e); EditorApplication.Exit(1); }
    }

    static void CheckPlan(Type yard)
    {
        foreach (int count in new[] { 1, 2, 3, 6, 9, 10 })
        {
            var polygon = new[] { new Vector2(-180,-30), new Vector2(180,-30), new Vector2(180,30), new Vector2(-180,30) };
            var node = new Vector2(180, 0);
            var rows = ((IEnumerable)yard.GetMethod("PlanRows", PrivateStatic).Invoke(null, new object[] { polygon, node, 0f, count })).Cast<object>().ToArray();
            Require(rows.Length == count, "Unexpected missing tracks in a large yard");
            Func<object, string, float> scalar = (row, field) => (float)row.GetType().GetField(field).GetValue(row);
            var targets = rows.Select(row => (Vector2)row.GetType().GetField("Target").GetValue(row)).ToArray();
            Require(targets.Distinct().Count() == count, "Multiple tracks converge at one point");
            Require(rows.Count(row => (bool)row.GetType().GetField("IsSpine").GetValue(row)) == 1, "Collector must have one owner");
            var direction = (Vector2)rows[0].GetType().GetField("Direction").GetValue(rows[0]);
            Require(rows.Min(row => scalar(row,"StraightEnd")-scalar(row,"StraightStart")) > 180f,
                "Collector consumes more than half the length of a large yard");
            Require(Mathf.Abs(rows.Max(row=>scalar(row,"StraightEnd"))-rows.Min(row=>scalar(row,"StraightEnd"))-(count-1)*10f)<.001f,
                "Straight lengths should differ only by the diagonal collector spacing");
            for (int i = 0; i < rows.Length; i++)
            {
                var target = targets[i];
                Vector2 delta = target-targets[0];
                Require(Mathf.Abs(delta.x*direction.y-delta.y*direction.x)<.001f, "Merge is off the shared diagonal");
                Require(target.x>scalar(rows[i],"StraightEnd"), "Merge behind track end");
            }
        }
    }

    static bool Covers(Mesh mesh, Vector2 p)
    {
        var v = mesh.vertices; var triangles = mesh.triangles;
        Func<Vector2, Vector2, float> cross = (a, b) => a.x*b.y-a.y*b.x;
        for (int i = 0; i < triangles.Length; i += 3) {
            Vector3 va=v[triangles[i]], vb=v[triangles[i+1]], vc=v[triangles[i+2]];
            if (Mathf.Max(va.y, Mathf.Max(vb.y, vc.y)) > .15f) continue;
            var a=new Vector2(va.x,va.z); var b=new Vector2(vb.x,vb.z); var c=new Vector2(vc.x,vc.z);
            if (Mathf.Abs(cross(b-a,c-a)) < .00001f) continue;
            float x=cross(b-a,p-a), y=cross(c-b,p-b), z=cross(a-c,p-c);
            if ((x>=0 && y>=0 && z>=0) || (x<=0 && y<=0 && z<=0)) return true;
        }
        return false;
    }

    static void Render(string root, Mesh mesh, Material[] source)
    {
        var go = new GameObject("RailYard"); go.AddComponent<MeshFilter>().sharedMesh=mesh;
        go.AddComponent<MeshRenderer>().sharedMaterials = source.Select(m => new Material(Shader.Find("Standard")) {
            color = m != null ? m.color : Color.gray }).ToArray();
        RenderSettings.ambientLight = new Color(.65f,.65f,.65f);
        var light=new GameObject("Sun").AddComponent<Light>(); light.type=LightType.Directional;
        light.transform.rotation=Quaternion.Euler(50,-30,0); light.intensity=1.3f;
        var camera=new GameObject("Camera").AddComponent<Camera>();
        camera.orthographic=true; camera.orthographicSize=90;
        camera.transform.position=new Vector3(90,145,155); camera.transform.LookAt(Vector3.zero);
        camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(.23f,.30f,.25f);
        Save(camera,root+"/Temp/IntersectionUnityQA/rail-yard-preview.png");
        camera.orthographicSize=40;
        camera.transform.position=new Vector3(45,80,110); camera.transform.LookAt(new Vector3(0,0,35));
        Save(camera,root+"/Temp/IntersectionUnityQA/rail-yard-throat.png");
    }

    static void Save(Camera camera, string path)
    {
        var rt=new RenderTexture(1400,1100,24); camera.targetTexture=rt; camera.Render(); RenderTexture.active=rt;
        var tex=new Texture2D(1400,1100,TextureFormat.RGB24,false); tex.ReadPixels(new Rect(0,0,1400,1100),0,0); tex.Apply();
        File.WriteAllBytes(path,tex.EncodeToPNG()); RenderTexture.active=null; camera.targetTexture=null;
        UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex);
    }
}
