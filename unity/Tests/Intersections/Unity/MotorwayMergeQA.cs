using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>本編の接合生成器と道路FBXで、一定幅・絞り込み・保存形状の不変性を検証する。</summary>
public static class MotorwayMergeQA
{
    static readonly Dictionary<int, List<Vector3>> samples = new();
    static Type pathType;
    static List<Vector3> Samples<T>(T p) => samples[(int)pathType.GetField("path_id").GetValue(p)];
    static void Set(object target, string field, object value) => target.GetType().GetField(field).SetValue(target, value);
    static object Get(object target, string property) => target.GetType().GetProperty(property).GetValue(target);
    static void Check(bool value, string message) { if (!value) throw new Exception("MotorwayMerge: " + message); }

    public static int Run(Assembly asm, IDictionary masters, IDictionary models, string root)
    {
        pathType = asm.GetType("GeneratedModels.PathSummary");
        Type itemType = asm.GetType("PathMasterItem"), constants = asm.GetType("Constants");
        int C(string name) => (int)constants.GetField(name).GetValue(null);
        int motorway = C("PATH_MOTORWAY"), active = C("STATUS_ACTIVE");
        var provider = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(pathType, typeof(List<Vector3>)),
            typeof(MotorwayMergeQA).GetMethod("Samples", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(pathType));
        var generator = Activator.CreateInstance(asm.GetType("RoadIntersectionMeshGenerator"));
        var generate = generator.GetType().GetMethod("GenerateWithSections");
        var bodyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Paths/p1/body.fbx");
        foreach (var material in bodyPrefab.GetComponent<MeshRenderer>().sharedMaterials)
            asm.GetType("RoadPath").GetMethod("GetOrCreateRoadMaterial").Invoke(null, new object[] { material });
        int checks = 0;
        object preview = null; IList previewPaths = null;
        Dictionary<int, List<Vector3>> previewSamples = null;
        foreach (float width in new[] { 7f, 14f })
        foreach (float side in new[] { -1f, 1f })
        foreach (float angle in new[] { 15f, 30f, 60f })
        foreach (bool reversed in new[] { false, true })
        foreach (float slope in new[] { 0f, .12f })
        {
            var paths = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pathType));
            samples.Clear();
            for (int i = 0; i < 3; i++)
            {
                int variant = 9100 + i, id = 301 + i;
                var master = Activator.CreateInstance(itemType);
                Set(master, "width", i == 2 ? 5.5f : width); Set(master, "lanes", i == 2 ? 1 : width == 7 ? 2 : 4);
                Set(master, "sidewalk_width", 0f); Set(master, "side_width", .5f); Set(master, "wall_width", .2f);
                Set(master, "center_width", 0f); Set(master, "follow_terrain", false); Set(master, "height_editable", true);
                Set(master, "ramp", i == 2); masters[variant] = master;
                Set(master, "body_material", "PATH_ASPHALT");
                foreach (string part in new[] { "body", "wall", "line", "center" })
                    models["Models/Paths/p" + variant + "/" + part] = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Paths/p1/" + part + ".fbx")
                        ?? (GameObject)models["Models/Paths/p100/" + part];
                float x = i == 0 ? -120 : i == 1 ? 120 : -120 * Mathf.Cos(angle * Mathf.Deg2Rad);
                float z = i == 2 ? side * 120 * Mathf.Sin(angle * Mathf.Deg2Rad) : 0;
                Vector3 node = new(0, 12, 0), end = new(x, 12 + slope * x, z);
                var path = Activator.CreateInstance(pathType);
                Set(path, "path_id", id); Set(path, "path_variant", variant); Set(path, "path_type", motorway); Set(path, "status", active);
                Set(path, "start_node_id", reversed ? 100 + i : 1); Set(path, "end_node_id", reversed ? 1 : 100 + i);
                Set(path, "travel_direction", C((i == 1) != reversed ? "DIR_FORWARD" : "DIR_BACKWARD"));
                Set(path, "curved", false); Set(path, "follow_terrain", C("VALUE_NONE"));
                Vector3 first = reversed ? end : node, last = reversed ? node : end;
                Set(path, "start_point", new List<object> { first.x, first.y, first.z });
                Set(path, "end_point", new List<object> { last.x, last.y, last.z });
                samples[id] = new List<Vector3> { first, last }; paths.Add(path);
            }
            var original = samples.ToDictionary(p => p.Key, p => p.Value.ToArray());
            var result = generate.Invoke(generator, new object[] { 1, new Vector3(0, 12, 0), paths, 12f, provider, null });
            Check(result != null, "node mesh missing");
            var layout = Get(result, "MotorwayMerge"); Check(layout != null, "layout missing");
            foreach (bool rightHand in new[] { false, true })
            {
                int hand = C(rightHand ? "TRAFFIC_HAND_RIGHT" : "TRAFFIC_HAND_LEFT");
                int target = (int)layout.GetType().GetMethod("TargetLane").Invoke(layout, new object[] { 303, 302, 0, hand });
                int expected = (side > 0) != rightHand ? 0 : (width == 7 ? 1 : 3);
                Check(target == expected, "ramp joins a nonadjacent mainline lane");
            }
            float parallel = (float)Get(layout, "ParallelLength"), laneWidth = (float)Get(layout, "LaneWidth");
            var mesh = (Mesh)Get(result, "Mesh");
            var corners = (IList)layout.GetType().GetField("corners", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(layout);
            int topCount = corners.Cast<object>().Sum(c => ((IList)c.GetType().GetField("Points").GetValue(c)).Count);
            Check(Mathf.Abs(OuterAt(mesh, parallel * .5f, side, topCount) - (width * .5f + laneWidth)) < .002f, "parallel lane narrows");
            Check(Mathf.Abs(OuterAt(mesh, parallel, side, topCount) - (width * .5f + laneWidth)) < .002f, "parallel end loses width");
            Check(OuterAt(mesh, parallel * 1.5f, side, topCount) < width * .5f + laneWidth - .1f, "taper does not narrow");
            Check(Mathf.Abs(OuterAt(mesh, parallel * 2f, side, topCount) - width * .5f) < .002f, "outgoing mouth widened");
            Check(mesh.vertices.All(p => !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsNaN(p.z)), "nonfinite mesh");
            foreach (var entry in original) Check(samples[entry.Key].SequenceEqual(entry.Value), "saved centerline changed");
            var cuts = (IDictionary)Get(result, "ConnectionPoints");
            foreach (var path in paths)
            {
                int id = (int)pathType.GetField("path_id").GetValue(path);
                Vector3 cut = (Vector3)cuts[id];
                Check(Mathf.Abs(cut.y - (12 + slope * cut.x)) < .001f, "cut elevation changed");
            }
            Vector3 p0 = (Vector3)cuts[303], p1 = (Vector3)cuts[302];
            p1.z = side * (width * .5f - .25f - (width - .5f) / (width == 7 ? 2 : 4) * .5f);
            var connector = layout.GetType().GetMethod("Connector").Invoke(layout, new object[] { 303, 302, p0, p1 });
            var line = (Vector3[])connector.GetType().GetField("points").GetValue(connector);
            float center = width * .5f - .25f + laneWidth * .5f;
            Check(line.Any(p => p.x >= 0 && p.x <= parallel && Mathf.Abs(p.z - side * center) < .001f), "vehicle misses parallel lane");
            Check(Vector3.Distance(line[0], p0) < .001f && Vector3.Distance(line[line.Length - 1], p1) < .001f, "vehicle endpoints mismatch");
            Check(ReferenceEquals(connector, layout.GetType().GetMethod("Connector").Invoke(layout, new object[] { 303, 302, p0, p1 })), "connector cache unused");
            CheckSurface(mesh, line, new Vector3(0, 12, 0));
            var specs = (IList)Get(result, "SidewalkConnectors");
            Check(specs.Cast<object>().Any(s => (bool)s.GetType().GetField("Dashed").GetValue(s)), "merge boundary missing");
            if (width == 14 && side == 1 && angle == 15 && !reversed && slope == 0)
            { preview = result; previewPaths = paths; previewSamples = samples.ToDictionary(p => p.Key, p => new List<Vector3>(p.Value)); }
            else UnityEngine.Object.DestroyImmediate(mesh);
            checks++;
        }
        checks += CheckCurved(asm, masters, previewPaths, generate, generator, provider, C);
        // 2車線ランプと流出方向では専用形状を適用しない。
        samples.Clear();
        foreach (var pair in previewSamples) samples[pair.Key] = pair.Value;
        foreach (bool twoLanes in new[] { true, false })
        {
            var paths = previewPaths;
            Set(masters[9102], "lanes", twoLanes ? 2 : 1);
            Set(paths[2], "travel_direction", C(twoLanes ? "DIR_BACKWARD" : "DIR_FORWARD"));
            var result = generate.Invoke(generator, new object[] { 1, new Vector3(0, 12, 0), paths, 12f, provider, null });
            Check(result != null, "ordinary junction missing");
            Check(Get(result, "MotorwayMerge") == null, "unsupported ramp modified");
            UnityEngine.Object.DestroyImmediate((Mesh)Get(result, "Mesh"));
        }
        Set(masters[9102], "lanes", 1); Set(previewPaths[2], "travel_direction", C("DIR_BACKWARD"));
        Render(asm, preview, previewPaths, root);
        return checks;
    }

    static int CheckCurved(Assembly asm, IDictionary masters, IList paths, MethodInfo generate, object generator, Delegate provider, Func<string, int> constant)
    {
        var hand = asm.GetType("CityInfoStore").GetProperty("TrafficHand");
        int originalHand = (int)hand.GetValue(null), checks = 0;
        var node = new Vector3(0, 12, 0);
        foreach (bool rightHand in new[] { false, true })
        foreach (bool twoWay in new[] { false, true })
        foreach (bool reversed in new[] { false, true })
        {
            hand.SetValue(null, constant(rightHand ? "TRAFFIC_HAND_RIGHT" : "TRAFFIC_HAND_LEFT"));
            float side = rightHand ? -1 : 1;
            for (int i = 0; i < 3; i++)
            {
                Set(masters[9100 + i], "center_width", twoWay && i != 2 ? 1f : 0f);
                Set(paths[i], "start_node_id", reversed ? 100 + i : 1); Set(paths[i], "end_node_id", reversed ? 1 : 100 + i);
                Set(paths[i], "travel_direction", constant(twoWay && i != 2 ? "DIR_BOTH" : (i == 1) != reversed ? "DIR_FORWARD" : "DIR_BACKWARD"));
                var points = new List<Vector3>();
                for (int j = 0; j <= 120; j++)
                {
                    float x = i == 1 ? j : -j;
                    points.Add(new Vector3(x, 12 + x * .12f, i == 2 ? j * .6f * side : x * x * .001f));
                }
                if (reversed) points.Reverse();
                samples[301 + i] = points;
            }
            var result = generate.Invoke(generator, new object[] { 1, node, paths, 12f, provider, null });
            Check(result != null && Get(result, "MotorwayMerge") != null, "curved layout missing");
            var layout = Get(result, "MotorwayMerge"); var mesh = (Mesh)Get(result, "Mesh");
            var sections = (IDictionary)layout.GetType().GetField("sections", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(layout);
            Vector3 Position(int id, float offset)
            {
                var section = sections[id];
                var left = (Vector2)Get(section, "Left");
                return (Vector3)section.GetType().GetField("Position").GetValue(section) + new Vector3(left.x, 0, left.y) * offset;
            }
            foreach (int from in new[] { 301, 303 })
            {
                Vector3 start = Position(from, from == 303 ? 0 : -side * 5), end = Position(302, side * 5);
                var connector = layout.GetType().GetMethod("Connector").Invoke(layout, new object[] { from, 302, start, end });
                CheckSurface(mesh, (Vector3[])connector.GetType().GetField("points").GetValue(connector), node);
            }
            var shuffled = (IList)Activator.CreateInstance(paths.GetType());
            for (int i = paths.Count - 1; i >= 0; i--) shuffled.Add(paths[i]);
            var reordered = generate.Invoke(generator, new object[] { 1, node, shuffled, 12f, provider, null });
            Check(reordered != null, "reordered curved layout missing");
            var reorderedMesh = (Mesh)Get(reordered, "Mesh");
            Check(mesh.vertices.SequenceEqual(reorderedMesh.vertices), "merge depends on input order");
            UnityEngine.Object.DestroyImmediate(reorderedMesh); UnityEngine.Object.DestroyImmediate(mesh); checks++;
        }
        hand.SetValue(null, originalHand);
        for (int i = 0; i < 3; i++)
        {
            Set(masters[9100 + i], "center_width", 0f);
            Set(paths[i], "start_node_id", 1); Set(paths[i], "end_node_id", 100 + i);
            Set(paths[i], "travel_direction", constant(i == 1 ? "DIR_FORWARD" : "DIR_BACKWARD"));
        }
        return checks;
    }

    static void CheckSurface(Mesh mesh, Vector3[] points, Vector3 node)
    {
        var go = new GameObject("MergeSurfaceProbe"); go.transform.position = node;
        var collider = go.AddComponent<MeshCollider>(); collider.sharedMesh = mesh;
        for (int i = 1; i < points.Length - 1; i++)
        {
            var ray = new Ray(points[i] + Vector3.up * 20, Vector3.down);
            Check(collider.Raycast(ray, out var hit, 40), "vehicle leaves road surface");
            Check(Mathf.Abs(points[i].y + .1f - hit.point.y) < .002f, "vehicle height differs from rendered surface");
        }
        UnityEngine.Object.DestroyImmediate(go);
    }

    static float OuterAt(Mesh mesh, float x, float side, int topCount)
    {
        float max = float.NegativeInfinity;
        var vertices = mesh.vertices; var triangles = mesh.triangles;
        for (int i = 0; i < triangles.Length; i += 3)
        for (int j = 0; j < 3; j++)
        {
            if (triangles[i] >= topCount || triangles[i + 1] >= topCount || triangles[i + 2] >= topCount) continue;
            Vector3 a = vertices[triangles[i + j]], b = vertices[triangles[i + (j + 1) % 3]];
            if (x < Mathf.Min(a.x, b.x) - .0001f || x > Mathf.Max(a.x, b.x) + .0001f) continue;
            float t = Mathf.Abs(b.x - a.x) < .0001f ? 0 : (x - a.x) / (b.x - a.x);
            max = Mathf.Max(max, Mathf.Lerp(a.z, b.z, t) * side);
        }
        return max;
    }

    static void Render(Assembly asm, object result, IList paths, string root)
    {
        var preview = new GameObject("MotorwayMergePreview");
        var surface = new GameObject("Surface"); surface.transform.SetParent(preview.transform, false); surface.transform.localPosition = new Vector3(0, 12, 0);
        surface.AddComponent<MeshFilter>().sharedMesh = (Mesh)Get(result, "Mesh");
        surface.AddComponent<MeshRenderer>().sharedMaterial = (Material)Get(result, "Material");
        var specs = (IList)Get(result, "SidewalkConnectors");
        var create = asm.GetType("RoadPath").GetMethod("CreateSidewalkConnectorObject");
        for (int i = 0; i < specs.Count; i++) create.Invoke(null, new object[] { surface.transform, specs[i], i });
        var cuts = (IDictionary)Get(result, "ConnectionPoints");
        var setback = Activator.CreateInstance(asm.GetType("SidewalkSetback"));
        var generator = Activator.CreateInstance(asm.GetType("RoadPath"));
        for (int i = 0; i < paths.Count; i++)
        {
            int id = 301 + i;
            Vector3 cut = (Vector3)cuts[id];
            Vector3 end = i == 0 ? new Vector3(-80, 12, 0) : i == 1 ? new Vector3(80, 12, 0)
                : new Vector3(-80 * Mathf.Cos(15 * Mathf.Deg2Rad), 12, 80 * Mathf.Sin(15 * Mathf.Deg2Rad));
            var resultPath = generator.GetType().GetMethod("Generate").Invoke(generator, new object[] { paths[i], new List<Vector3> { cut, end }, setback, setback });
            var go = (GameObject)Get(resultPath, "GeneratedObject"); go.transform.SetParent(preview.transform, false);
        }
        preview.transform.position = new Vector3(3000, 0, 3000);
        var camera = new GameObject("MergeCamera").AddComponent<Camera>();
        camera.orthographic = true; camera.orthographicSize = 47; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.16f,.2f,.23f);
        var center = preview.transform.position + new Vector3(-10,12,5);
        foreach (bool top in new[] { true, false })
        {
            camera.transform.position = center + (top ? new Vector3(0,100,0) : new Vector3(-15,75,-65));
            camera.transform.rotation = top ? Quaternion.Euler(90,0,0) : Quaternion.LookRotation(center-camera.transform.position);
            var texture = new RenderTexture(1400,800,24); camera.targetTexture = texture; camera.Render(); RenderTexture.active = texture;
            var image = new Texture2D(1400,800,TextureFormat.RGB24,false); image.ReadPixels(new Rect(0,0,1400,800),0,0); image.Apply();
            File.WriteAllBytes(root + "/Temp/IntersectionUnityQA/merge-" + (top ? "top" : "perspective") + ".png", image.EncodeToPNG());
            camera.targetTexture = null; RenderTexture.active = null; UnityEngine.Object.DestroyImmediate(image); texture.Release(); UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}
