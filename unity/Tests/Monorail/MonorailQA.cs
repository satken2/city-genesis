using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Blenderの再読み込みでは判定できないUnityの軸符号と実寸を検査する。
public static class MonorailQA
{
    static int checks;
    static void Check(bool valid, string message)
    {
        checks++;
        if (!valid) throw new Exception(message);
    }
    static Vector3[] Vertices(string relative)
    {
        string asset = "Assets/Models/" + relative + ".fbx";
        var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
        Check(importer != null, "Missing importer: " + relative);
        if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
        var filters = model.GetComponentsInChildren<MeshFilter>();
        Check(filters.Length == 1, "Expected one mesh: " + relative);
        var mesh = filters[0].sharedMesh;
        Check(mesh.vertexCount > 0, "Empty mesh: " + relative);
        Check(model.GetComponentsInChildren<MeshRenderer>().All(r => r.sharedMaterials.All(m => m != null)), "Missing material: " + relative);
        return mesh.vertices;
    }
    static void Near(float actual, float expected, string message)
    {
        Check(Mathf.Abs(actual - expected) < .005f, message + $": {actual} != {expected}");
    }
    public static void Run()
    {
        string report = Path.GetFullPath("result.txt");
        var args = Environment.GetCommandLineArgs();
        report = args[Array.IndexOf(args, "-monorailReport") + 1];
        try
        {
            AssetDatabase.Refresh();
            foreach (int id in new[] {501, 502})
            {
                var track = Vertices($"Paths/p{id}/track");
                Near(track.Max(v => v.y), 0, "Guideway top");
                Near(track.Min(v => v.y), -1.2f, "Guideway bottom");
                Near(track.Max(v => v.x), .425f, "Guideway half width including power strip");
                var rail = Vertices($"Paths/p{id}/rail");
                Check(track.SequenceEqual(rail), "Connector cross section differs");
                var pillar = Vertices($"Paths/p{id}/pillar");
                Near(pillar.Max(v => v.y), 0, "Pillar top pivot");
                var cap = Vertices($"Paths/p{id}/pillar_cap");
                Near(cap.Max(v => v.y), track.Min(v => v.y) + .12f, "Bearing joins guideway");
            }
            var platform = Vertices("Paths/p502/platform");
            Check(platform.All(v => v.x >= .299f && v.x <= 4.501f), "Platform must face Unity +X");
            Near(platform.Where(v => Mathf.Abs(v.x - 1.5f) < .001f && v.y < 2f).Max(v => v.y), 1.5f, "Boarding edge");
            Near(platform.Max(v => v.y), 5.22f, "Canopy ridge");
            Check(platform.Any(v => Mathf.Abs(v.x - 1.5f) < .001f && v.y > 4.8f), "Canopy must cover boarding edge");
            Check(platform.Where(v => v.y > 1.51f && v.y < 4.7f).All(v => v.x > 3.4f), "Canopy columns must clear boarding area");
            Near(platform.Max(v => v.z) - platform.Min(v => v.z), 8, "Platform tile length");
            var wall = Vertices("Paths/p502/platform_wall");
            Check(wall.All(v => v.x >= 4.299f && v.x <= 4.501f), "Outer wall location");
            Near(wall.Max(v => v.y), 4.82f, "Full height side wall meets canopy");
            Vertices("Paths/p502/platform_panel");
            foreach (int id in new[] {308, 309})
            {
                var car = Vertices($"Props/{id}");
                var close = Vertices($"Props/{id}_close");
                for(int axis=0;axis<3;axis++)
                {
                    int a=axis;
                    Check(Mathf.Abs(car.Min(v=>v[a])-close.Min(v=>v[a]))<.05f && Mathf.Abs(car.Max(v=>v[a])-close.Max(v=>v[a]))<.05f,"Close bounds differ");
                }
                Check(close.Length>car.Length,"Close detail missing");
                Check(close.Where(v=>v.y<0).All(v=>Mathf.Abs(v.x)>=.509f),"Close skirt intrudes into guideway");
                Near(close.Where(v=>Mathf.Abs(v.x)<.4f).Min(v=>v.y),0,"Close tire contact");
                Near(car.Min(v => v.y), -.70f, "Skirt extends below guideway top");
                Near(car.Max(v => v.y), 3.85f, "Vehicle height");
                Near(car.Max(v => v.z), 7, "Forward coupling limit");
                Near(car.Min(v => v.z), -7, "Rear coupling limit");
                Near(car.Max(v => v.x) + car.Min(v => v.x), 0, "Vehicle centered");
                Check(car.Max(v => Mathf.Abs(v.x)) < 1.5f, "Vehicle must clear boarding edge");
                Check(car.Where(v => v.y < 0).All(v => Mathf.Abs(v.x) >= .509f), "Skirt must clear guideway and power strips");
                Near(car.Where(v => Mathf.Abs(v.x) < .4f).Min(v => v.y), 0, "Tire contact remains at guideway top");
                if (id == 308)
                {
                    Near(car.Where(v => v.y < -.69f).Max(v => v.z), 5.60f, "Leading skirt lower edge setback");
                    Check(car.Any(v => Mathf.Abs(v.y-1.24f)<.001f && Mathf.Abs(v.z-6.55f)<.001f), "Leading skirt upper edge");
                }
            }
            // 検証用ラッパーは本編の生成器で再作成し、以前のGUIDに依存させない。
            foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] {"Assets/Models"}))
            {
                string source = AssetDatabase.GUIDToAssetPath(guid);
                if (!source.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
                string wrapper = Path.ChangeExtension(source.Replace("Assets/Models/", "Assets/Resources/Models/"), ".prefab");
                ModelPrefabGenerator.CreateWrapperPrefab(source, wrapper);
            }
            ModelPrefabGenerator.SyncAll();
            foreach (string relative in new[] {"Paths/p501/track", "Paths/p502/platform", "Props/308", "Props/309", "Props/308_close", "Props/309_close"})
                Check(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Models/" + relative + ".prefab") != null, "Missing generated prefab " + relative);
            File.WriteAllText(report, $"PASS monorail: {checks} checks, normal/close FBX, Unity mesh axes, guideway clearance and generated wrappers");
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
