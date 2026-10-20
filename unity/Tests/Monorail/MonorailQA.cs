using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Verify Unity axis signs and actual dimensions, which cannot be determined by reloading the assets in Blender.
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
                // The track component origin is spline + 0.12 m. Its top is spline + 1.2 m and its bottom is spline - 0.3 m (it does not sink into the ground even at ground level 0 m).
                var track = Vertices($"Paths/p{id}/track");
                Near(track.Max(v => v.y), 1.08f, "Guideway top");
                Near(track.Min(v => v.y), -.42f, "Guideway bottom");
                Near(track.Max(v => v.x), .425f, "Guideway half width including power strip");
                var rail = Vertices($"Paths/p{id}/rail");
                Check(track.SequenceEqual(rail), "Connector cross section differs");
                var pillar = Vertices($"Paths/p{id}/pillar");
                Near(pillar.Max(v => v.y), 0, "Pillar top pivot");
                var cap = Vertices($"Paths/p{id}/pillar_cap");
                Near(cap.Max(v => v.y), track.Min(v => v.y) + .12f, "Bearing joins guideway");
                // Support the two track girders of the double track (track centers ±1.5 m) on the bearings beneath each one.
                float guideway = 1.5f, halfWidth = track.Max(v => v.x);
                var bearings = cap.Where(v => Mathf.Abs(v.y - cap.Max(c => c.y)) < .001f).ToArray();
                Check(bearings.Any(v => v.x > 0) && bearings.Any(v => v.x < 0), "Bearings under both guideways");
                Check(bearings.All(v => Mathf.Abs(Mathf.Abs(v.x) - guideway) <= halfWidth), "Bearing outside its guideway");
                Check(cap.Max(v => Mathf.Abs(v.x)) >= guideway + halfWidth, "Cap must span both guideways");
                Check(pillar.Max(v => Mathf.Abs(v.x)) <= cap.Where(v => v.y < cap.Min(c => c.y) + .001f).Max(v => Mathf.Abs(v.x)), "Pillar wider than the cap seat");
            }
            var platform = Vertices("Paths/p502/platform");
            Check(platform.All(v => v.x >= .299f && v.x <= 4.501f), "Platform must face Unity +X");
            Near(platform.Where(v => Mathf.Abs(v.x - 1.5f) < .001f && v.y < 2f).Max(v => v.y), 1.5f, "Boarding edge");
            Near(platform.Max(v => v.y), 5.22f, "Canopy ridge");
            Check(platform.Any(v => Mathf.Abs(v.x - 1.5f) < .001f && v.y > 4.8f), "Canopy must cover boarding edge");
            // Only the platform doors (X=1.54–1.79 m) are above the boarding edge. The canopy columns leave the boarding area clear.
            Check(platform.Where(v => v.y > 1.51f && v.y < 4.7f && v.x > 1.8f).All(v => v.x > 3.4f), "Canopy columns must clear boarding area");
            Near(platform.Min(v => v.y), -.62f, "Platform slab hangs from the raised guideway");
            Near(platform.Max(v => v.z) - platform.Min(v => v.z), 8, "Platform tile length");
            var wall = Vertices("Paths/p502/platform_wall");
            Check(wall.All(v => v.x >= 4.299f && v.x <= 4.501f), "Outer wall location");
            Near(wall.Max(v => v.y), 4.82f, "Full height side wall meets canopy");
            Vertices("Paths/p502/platform_panel");
            // The vehicle origin is at the top of the girder. The underfloor cover is 0.38 m below the top along the full length, and drops beside both sides of the girder to 0.90 m below the top at the bogies.
            float beamDepth = Vertices("Paths/p501/track").Max(v => v.y) - Vertices("Paths/p501/track").Min(v => v.y);
            foreach (int id in new[] {308, 309})
            {
                var car = Vertices($"Props/{id}");
                var close = Vertices($"Props/{id}_close");
                var far = Vertices($"Props/{id}_far");
                for(int axis=0;axis<3;axis++)
                {
                    int a=axis;
                    Check(Mathf.Abs(car.Min(v=>v[a])-close.Min(v=>v[a]))<.05f && Mathf.Abs(car.Max(v=>v[a])-close.Max(v=>v[a]))<.05f,"Close bounds differ");
                }
                Check(close.Length>car.Length,"Close detail missing");
                Near(far.Min(v => v.y), car.Min(v => v.y), "Far keeps the bogie cover depth");
                foreach (var detail in new[] {car, close})
                {
                    Check(detail.Where(v => v.y < 0).All(v => Mathf.Abs(v.x) >= .509f), "Skirt must clear guideway and power strips");
                    Near(detail.Where(v => Mathf.Abs(v.x) < .4f).Min(v => v.y), 0, "Tire contact remains at guideway top");
                    Near(detail.Min(v => v.y), -.90f, "Bogie cover straddles the guideway");
                    Check(detail.Min(v => v.y) > -beamDepth + .3f, "Bogie cover must stay above the guideway bottom");
                    Near(detail.Where(v => Mathf.Abs(v.z) <= 2.7f).Min(v => v.y), -.38f, "Full-length skirt between bogies");
                    Check(detail.Where(v => v.y < -.39f).All(v => Mathf.Abs(v.z) >= 2.64f), "Deep cover confined to bogies");
                }
                Near(car.Max(v => v.y), 2.80f, "Vehicle height");
                Near(car.Max(v => v.z), 7, "Forward coupling limit");
                Near(car.Min(v => v.z), -7, "Rear coupling limit");
                Near(car.Max(v => v.x) + car.Min(v => v.x), 0, "Vehicle centered");
                Check(car.Max(v => Mathf.Abs(v.x)) < 1.5f, "Vehicle must clear boarding edge");
                if (id == 308)
                    Check(car.Where(v => v.z > 6.55f).All(v => v.y > -.05f), "Leading fairing rises under the nose");
            }
            // Recreate the verification wrapper with the main generator so it does not depend on a previous GUID.
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
