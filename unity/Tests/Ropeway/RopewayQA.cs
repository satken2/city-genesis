using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Check FBX axis conversion, actual cable spacing, and the connection between the sheaves and deck.
public static class RopewayQA
{
    static int checks;
    static void Check(bool valid, string message)
    {
        checks++;
        if (!valid) throw new Exception(message);
    }
    static void Near(float value, float expected, string message)
        => Check(Mathf.Abs(value - expected) < .002f, message + $": {value} != {expected}");
    static Vector3[] Vertices(int id, string part)
    {
        string path = $"Assets/Models/Paths/p{id}/{part}.fbx";
        var importer = (ModelImporter)AssetImporter.GetAtPath(path);
        Check(importer != null && importer.isReadable, "Readable importer " + path);
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        var filters = model.GetComponentsInChildren<MeshFilter>();
        Check(filters.Length == 1, "One mesh " + path);
        Check(model.GetComponentsInChildren<MeshRenderer>().All(r => r.sharedMaterials.All(m => m != null)), "Materials " + path);
        return filters[0].sharedMesh.vertices;
    }
    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        string report = args[Array.IndexOf(args, "-ropewayReport") + 1];
        try
        {
            AssetDatabase.Refresh();
            foreach (int id in new[] {601, 602})
            {
                var body = Vertices(id, "body");
                Near(body.Max(v => v.y), 0, "Body surface pivot");
                var surface = body.Where(v => v.y >= -.002f).ToArray();
                Near(surface.Max(v => v.x) - surface.Min(v => v.x), 4.28f, "Cable outer width");
                foreach (float side in new[] {-1f, 1f})
                {
                    var cable = body.Where(v => side * v.x > 2).ToArray();
                    Near(cable.Average(v => v.x), side * 2.1f, "Cable center");
                    Near(cable.Min(v => v.y) + .1f, .02f, "Cable contact height");
                    Near(cable.Max(v => v.z) - cable.Min(v => v.z), 8, "Continuous cable length");
                }
                var pillar = Vertices(id, "pillar");
                Near(pillar.Max(v => v.y), 0, "Pillar top pivot");
                var cap = Vertices(id, "pillar_cap");
                Near(cap.Min(v => v.y), id == 601 ? -1.65f : -4.05f, "Cap to pillar joint");
                var wheel = cap.Where(v => Mathf.Abs(Mathf.Abs(v.x) - 2.1f) < .111f && v.y < .1f).ToArray();
                Near(wheel.Max(v => v.y), .02f, "Wheel contact height");
                if (id == 601) Check(body.All(v => Mathf.Abs(v.x) >= 2.059f), "No solid road deck");
                else
                {
                    var shell = Vertices(id, "station_shell");
                    Check(shell.Max(v => v.y) > 2.3f, "Vaulted roof above cables");
                    Check(shell.Max(v => Mathf.Abs(v.x)) < 4.26f, "Roof overhang");
                    Near(shell.Max(v => v.z)-shell.Min(v => v.z), 8, "Station module length");
                    var slab = shell.Where(v => Mathf.Abs(v.x) > 3.999f && Mathf.Abs(v.x) < 4.001f && v.y < -3.39f).ToArray();
                    Near(slab.Min(v => v.y) + .1f, -3.6f, "Deck underside meets support");
                    Near(slab.Max(v => v.y) + .1f, -3.3f, "Boarding deck height");
                }
            }
            ModelPrefabGenerator.SyncAll();
            foreach (int id in new[] {601, 602})
                foreach (string part in new[] {"body", "pillar", "pillar_cap"})
                    Check(AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Resources/Models/Paths/p{id}/{part}.prefab") != null, "Generated prefab");
            Check(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Models/Paths/p602/station_shell.prefab") != null, "Station shell prefab");
            File.WriteAllText(report, $"PASS ropeway: {checks} checks, 7 FBX; dimensions, cable contacts and generated prefabs");
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
