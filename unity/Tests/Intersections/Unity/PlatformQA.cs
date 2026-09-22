using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// 実FBXと本編のホーム生成器で、左右と上屋柱の繰り返し周期を検査する。
public static class PlatformQA
{
    static Assembly assembly;
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
                foreach (string dir in new[] { "Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins" }) {
                    string file = Path.Combine(root, dir, name);
                    if (File.Exists(file)) return Assembly.LoadFrom(file);
                }
                return null;
            };
            assembly = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            CheckPlatformContinuation();
            bool endsOnly = args.Contains("-platformEndsOnly");
            if (!endsOnly) CheckStationGrade();
            Type modelType = assembly.GetType("ModelManager");
            var model = new GameObject("Models").AddComponent(modelType);
            modelType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, model);
            var cache = (IDictionary)modelType.GetField("_modelCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(model);
            if (endsOnly)
            {
                foreach (string part in new[] { "buffer_stop", "platform_end" })
                    cache[$"Models/Paths/p4/{part}"] = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Paths/p4/{part}.fbx");
                CheckTerminations();
                File.WriteAllText(report, $"PASS platform ends: {checks} checks; continuation floors, real encroachment, rail and monorail end walls");
                EditorApplication.Exit(0);
                return;
            }
            foreach (int variant in new[] { 401, 411, 451, 461 })
            {
                string asset = $"Assets/Paths/p{variant}/track.fbx";
                var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
                importer.isReadable = true;
                importer.SaveAndReimport();
                cache[$"Models/Paths/p{variant}/track"] = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
                CheckTrack(variant, 32f);
                CheckTrack(variant, 256f);
            }
            foreach (string part in new[] { "buffer_stop", "platform_end" })
                cache[$"Models/Paths/p4/{part}"] = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Paths/p4/{part}.fbx");
            CheckTerminations();
            foreach (int variant in new[] { 451, 452, 461, 462 })
                foreach (string part in new[] { "platform", "platform_wall" }) {
                    string asset = $"Assets/Paths/p{variant}/{part}.fbx";
                    var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
                    if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
                    cache[$"Models/Paths/p{variant}/{part}"] = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
                }
            foreach (int variant in new[] { 451, 452, 461, 462 })
                foreach (bool left in new[] { false, true })
                    foreach (float direction in new[] { 1f, -1f })
                        Check(variant, left, direction);
            RenderEnds(root, cache);
            File.WriteAllText(report, $"PASS platforms: {checks} checks across 16 station sides; canopy 4m, fence 1.6m");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { File.WriteAllText(report, e.ToString()); Debug.LogException(e); EditorApplication.Exit(1); }
    }

    static void Check(int variant, bool left, float direction)
    {
        var parent = new GameObject("Station");
        var points = new List<Vector3> { Vector3.zero, new Vector3(0, 0, direction * 64) };
        object spline = assembly.GetType("RoadPath").GetMethod("BuildSpline").Invoke(null, new object[] { parent, points, true });
        var material = new Material(Shader.Find("Standard"));
        assembly.GetType("TrackPath").GetMethod("CreatePlatformSide", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] {
            parent, spline, null, points, new[] { Vector3.right * direction, Vector3.right * direction },
            new[] { 0f, 64f }, left, 2.5f, 3f, material, $"Models/Paths/p{variant}", "Models/Paths/p4"
        });
        foreach (var component in parent.GetComponentsInChildren<Component>()) {
            if (component == null || component.GetType().Name != "SplineMesh") continue;
            MethodInfo rebuild = component.GetType().GetMethod("RebuildImmediate");
            rebuild.Invoke(component, rebuild.GetParameters().Length == 0 ? null : new object[] { true });
        }
        float side = (left ? -1 : 1) * direction;
        foreach (var filter in parent.GetComponentsInChildren<MeshFilter>()) {
            Require(filter.sharedMesh != null, "Missing generated mesh");
            var vertices = filter.sharedMesh.vertices.Select(v => filter.transform.TransformPoint(v)).ToArray();
            if (filter.name.EndsWith("/platform") || filter.name.EndsWith("/platform_wall")) {
                Require(vertices.All(v => v.x * side > 1.25f && v.x * side < 5.65f), $"p{variant} wrong side {left}/{direction}");
                bool wall = filter.name.EndsWith("/platform_wall");
                // 柱断面の端だけを拾い、長手分割で増えた同位置の頂点はまとめる。
                var locations = vertices.Where(v => wall
                    ? Mathf.Abs(v.y - 1.68f) < .001f && Mathf.Abs(v.x * side - 5.45f) < .001f
                    : Mathf.Abs(v.y - 4.34f) < .001f && Mathf.Abs(v.x * side - 3.585f) < .001f)
                    .Select(v => v.z * direction).OrderBy(z => z).ToList();
                var groups = new List<List<float>>();
                foreach (float z in locations) {
                    if (groups.Count == 0 || z - groups.Last().Last() > .25f) groups.Add(new List<float>());
                    groups.Last().Add(z);
                }
                float period = wall ? 1.6f : 4f;
                Require(groups.Count == (wall ? 40 : 16), $"p{variant} {filter.name} count={groups.Count}");
                var centers = groups.Select(g => (g.Min() + g.Max()) / 2).ToArray();
                for (int i = 1; i < centers.Length; i++) Require(Mathf.Abs(centers[i] - centers[i-1] - period) < .005f, "Compressed pillar spacing");
            }
            else if (filter.name.StartsWith("PlatformDeck"))
                Require(vertices.All(v => v.x * side >= 1.499f && v.x * side <= 5.501f), "Deck side mismatch");
        }
        UnityEngine.Object.DestroyImmediate(parent);
        UnityEngine.Object.DestroyImmediate(material);
    }
    static void Require(bool valid, string message) { if (!valid) throw new Exception(message); checks++; }

    static void CheckTrack(int variant, float length)
    {
        var parent = new GameObject("TrackPeriodQA");
        var road = assembly.GetType("RoadPath");
        object spline = road.GetMethod("BuildSpline").Invoke(null, new object[] {
            parent, new List<Vector3> { Vector3.zero, new Vector3(0,0,length) }, false });
        road.GetMethod("CreateSplineMesh").Invoke(null, new object[] {
            parent, spline, $"Models/Paths/p{variant}/track", 5f, 0f, .12f, false, null,
            false, false, 0.0, 1.0, false, true, 0f, true });
        var intervals = new List<Vector2>();
        foreach (var component in parent.GetComponentsInChildren<Component>())
        {
            if (component == null || component.GetType().Name != "SplineMesh") continue;
            var rebuild = component.GetType().GetMethod("RebuildImmediate");
            rebuild.Invoke(component, rebuild.GetParameters().Length == 0 ? null : new object[] { true });
            var mesh = component.GetComponent<MeshFilter>().sharedMesh;
            Require(mesh.vertexCount <= 60000, "Track section exceeds vertex budget");
            // 枕木上面はZ断面ごとにまとまる。長さの異なるパスでも0.5m周期を維持する。
            var zs = mesh.vertices.Where(v => Mathf.Abs(v.y - .32f) < .002f && Mathf.Abs(v.x) > 1f)
                .Select(v => v.z).Distinct().OrderBy(v => v).ToArray();
            Require(zs.Length > 4, "Sleeper section measurement missing");
            if (zs.Length > 4)
            {
                var gaps = zs.Zip(zs.Skip(1), (a,b) => b-a).Where(v => v > .02f).ToArray();
                Require(gaps.All(v => v < .51f), $"p{variant} stretched sleepers on {length}m path");
            }
            intervals.Add(new Vector2(mesh.bounds.min.z, mesh.bounds.max.z));
        }
        intervals.Sort((a,b) => a.x.CompareTo(b.x));
        Require(Mathf.Abs(intervals[0].x) < .01f && Mathf.Abs(intervals.Last().y-length) < .01f, "Track extent changed");
        for (int i=1; i<intervals.Count; i++) Require(Mathf.Abs(intervals[i].x-intervals[i-1].y) < .01f, "Track split gap");
        if (length > 200) Require(intervals.Count > 1, "Long track was not split");
        UnityEngine.Object.DestroyImmediate(parent);
    }

    static readonly List<object> connected = new List<object>();
    static List<T> Connections<T>(int node) { return connected.Cast<T>().ToList(); }

    static void RenderEnds(string root, IDictionary cache)
    {
        var parent = new GameObject("TerminalPreview");
        var road = assembly.GetType("RoadPath");
        var track = assembly.GetType("TrackPath");
        var summary = assembly.GetType("GeneratedModels.PathSummary");
        var path = Activator.CreateInstance(summary);
        foreach (var pair in new[] { ("path_id", 1), ("path_type", 4), ("path_variant", 461), ("start_node_id", 1), ("end_node_id", 2) })
            summary.GetField(pair.Item1).SetValue(path, pair.Item2);
        connected.Clear(); connected.Add(path);
        var provider = track.GetField("ConnectedPathsProvider");
        provider.SetValue(null, Delegate.CreateDelegate(provider.FieldType,
            typeof(PlatformQA).GetMethod("Connections", BindingFlags.Static|BindingFlags.NonPublic).MakeGenericMethod(summary)));
        var points = new List<Vector3> { Vector3.zero, new Vector3(0,0,32) };
        object spline = road.GetMethod("BuildSpline").Invoke(null, new object[] { parent, points, true });
        foreach (string part in new[] { "body", "track" }) {
            string asset = $"Assets/Paths/p461/{part}.fbx";
            var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
            importer.isReadable = true; importer.SaveAndReimport();
            cache[$"Models/Paths/p461/{part}"] = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            road.GetMethod("CreateSplineMesh").Invoke(null, new object[] {
                parent, spline, $"Models/Paths/p461/{part}", 5f, 0f, part=="body" ? .1f : .12f, false, null,
                false, part=="body", 0.0, 1.0, false, true, 0f, true });
        }
        var material = new Material(Shader.Find("Standard")) { color = new Color(.28f,.3f,.32f) };
        track.GetMethod("CreatePlatformSide", BindingFlags.Static|BindingFlags.NonPublic).Invoke(null, new object[] {
            parent, spline, null, points, new[] { Vector3.right, Vector3.right }, new[] { 0f,32f }, false, 2.5f, 3f,
            material, "Models/Paths/p461", "Models/Paths/p4" });
        track.GetMethod("CreateTerminations", BindingFlags.Static|BindingFlags.NonPublic).Invoke(null, new object[] {
            parent, points, null, path, 5f, 1, 0f, 3f, "Models/Paths/p461", "Models/Paths/p4" });
        foreach (var comp in parent.GetComponentsInChildren<Component>()) {
            if (comp == null || comp.GetType().Name != "SplineMesh") continue;
            var rebuild = comp.GetType().GetMethod("RebuildImmediate");
            rebuild.Invoke(comp, rebuild.GetParameters().Length == 0 ? null : new object[] { true });
        }
        var cam = new GameObject("PreviewCamera").AddComponent<Camera>();
        cam.transform.position = new Vector3(15,10,-15); cam.transform.LookAt(new Vector3(1.5f,1.4f,5));
        cam.orthographic = true; cam.orthographicSize = 8;
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(.19f,.23f,.26f);
        var light = new GameObject("PreviewLight").AddComponent<Light>(); light.type=LightType.Directional;
        light.transform.rotation = Quaternion.Euler(45,-25,0);
        RenderSettings.ambientLight = Color.gray;
        var rt = new RenderTexture(1200,900,24); cam.targetTexture=rt; cam.Render(); RenderTexture.active=rt;
        var tex = new Texture2D(1200,900,TextureFormat.RGB24,false); tex.ReadPixels(new Rect(0,0,1200,900),0,0); tex.Apply();
        File.WriteAllBytes(root+"/Temp/IntersectionUnityQA/terminal-preview.png",tex.EncodeToPNG());
        RenderTexture.active=null; cam.targetTexture=null;
        UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex);
        UnityEngine.Object.DestroyImmediate(parent); UnityEngine.Object.DestroyImmediate(cam.gameObject); UnityEngine.Object.DestroyImmediate(light.gameObject);
        provider.SetValue(null,null);
    }

    static void CheckTerminations()
    {
        var track = assembly.GetType("TrackPath");
        var summary = assembly.GetType("GeneratedModels.PathSummary");
        var path = Activator.CreateInstance(summary);
        foreach (var pair in new[] { ("path_id", 1), ("path_type", 4), ("path_variant", 461), ("start_node_id", 1), ("end_node_id", 2) })
            summary.GetField(pair.Item1).SetValue(path, pair.Item2);
        var provider = track.GetField("ConnectedPathsProvider");
        provider.SetValue(null, Delegate.CreateDelegate(provider.FieldType,
            typeof(PlatformQA).GetMethod("Connections", BindingFlags.Static|BindingFlags.NonPublic).MakeGenericMethod(summary)));
        connected.Clear(); connected.Add(path);
        var infrastructure = track.GetField("InfrastructureConnectionProvider");
        foreach (int mode in new[] { 0, 1, 2 })
        {
            connected.Clear(); connected.Add(path);
            infrastructure.SetValue(null, mode == 2 ? (Func<int, bool>)(node => node == 2) : null);
            if (mode == 1) {
                var other = Activator.CreateInstance(summary);
                summary.GetField("path_id").SetValue(other, 2);
                summary.GetField("path_type").SetValue(other, 4);
                connected.Add(other);
            }
            var parent = new GameObject("EndsQA");
            var points = new List<Vector3> { new Vector3(0,6,0), new Vector3(0,6,32) };
            track.GetMethod("CreateTerminations", BindingFlags.Static|BindingFlags.NonPublic).Invoke(null, new object[] {
                parent, points, null, path, 5f, 1, 0f, 3f, "Models/Paths/p461", "Models/Paths/p4" });
            var stops = parent.GetComponentsInChildren<Transform>().Where(t=>t.name=="TrackBufferStop").ToArray();
            Require(stops.Length == (mode == 1 ? 0 : mode == 2 ? 1 : 2), "Buffer stop on through track/infrastructure connection or missing terminal stop");
            foreach (var stop in stops) Require(Vector3.Dot(stop.forward, new Vector3(0,0,16)-stop.position)>0, "Buffer stop faces away from track");
            Require(parent.GetComponentsInChildren<Transform>().Count(t=>t.name=="TrackPlatformEnd") == 8, "Platform ends not closed");
            UnityEngine.Object.DestroyImmediate(parent);
        }
        provider.SetValue(null, null);
        infrastructure.SetValue(null, null);

        summary.GetField("path_type").SetValue(path, 5);
        summary.GetField("path_variant").SetValue(path, 502);
        foreach (bool left in new[] { false, true })
        {
            var parent = new GameObject("MonorailEndsQA");
            track.GetMethod("CreateTerminations", BindingFlags.Static|BindingFlags.NonPublic).Invoke(null, new object[] {
                parent, new List<Vector3> { Vector3.zero, new Vector3(0,0,32) }, null, path,
                3f, 1, left ? 3f : 0f, left ? 0f : 3f, "Models/Paths/p502", "Models/Paths/p5" });
            var ends = parent.GetComponentsInChildren<Transform>().Where(t => t.name == "TrackPlatformEnd").ToArray();
            Require(ends.Length == 6, "Monorail platform ends not closed");
            Require(ends.All(t => Mathf.Abs(t.position.x) >= 1.5f && Mathf.Abs(t.position.x) <= 4.5f), "Monorail end outside deck");
            Require(!parent.GetComponentsInChildren<Transform>().Any(t => t.name == "TrackBufferStop"), "Rail buffer stop on monorail");
            UnityEngine.Object.DestroyImmediate(parent);
        }
    }

    static void CheckPlatformContinuation()
    {
        Type neighborType = assembly.GetType("TrackNeighborInfo");
        foreach (float width in new[] { 3f, 5f })
        foreach (float offset in new[] { -.001f, 0f, .001f })
        foreach (bool reverse in new[] { false, true })
        {
            var points = Enumerable.Range(0, 33).Select(z => new Vector3(0, 6, z)).ToList();
            if (reverse) points.Reverse();
            var neighbors = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(neighborType));
            foreach (float end in new[] { 0f, 32f })
            {
                var neighbor = Activator.CreateInstance(neighborType);
                neighborType.GetField("PathId").SetValue(neighbor, end == 0 ? 2 : 3);
                neighborType.GetField("Width").SetValue(neighbor, width);
                neighborType.GetField("Points").SetValue(neighbor, new List<Vector3> {
                    new Vector3(offset, 6, end), new Vector3(offset, 6, end == 0 ? -32 : 64) });
                neighbors.Add(neighbor);
            }
            object corridor = assembly.GetType("TrackAdjacency").GetMethod("Compute").Invoke(null,
                new object[] { 1, points, width, 0f, 3f, 3f, neighbors });
            foreach (string side in new[] { "Left", "Right" })
            {
                var far = corridor == null ? null : (float[])corridor.GetType().GetField(side + "DeckFar").GetValue(corridor);
                Require(far == null || far.All(f => Mathf.Abs(f - (width / 2 + 3)) < .001f),
                    $"Continuation removes {side} platform floor: width={width}, offset={offset}, reverse={reverse}");
                if (corridor == null) continue;
                var omitted = (bool[])corridor.GetType().GetField(side + "PlatformWallOmit").GetValue(corridor);
                Require(!omitted.Any(v => v), "Continuation removes platform outer wall");
            }
        }
        foreach (float side in new[] { -1f, 1f })
        {
            var neighbor = Activator.CreateInstance(neighborType);
            neighborType.GetField("PathId").SetValue(neighbor, 2);
            neighborType.GetField("Width").SetValue(neighbor, 3f);
            neighborType.GetField("Points").SetValue(neighbor, new List<Vector3> {
                new Vector3(side * 4, 6, -10), new Vector3(side * 4, 6, 42) });
            var neighbors = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(neighborType));
            neighbors.Add(neighbor);
            object corridor = assembly.GetType("TrackAdjacency").GetMethod("Compute").Invoke(null,
                new object[] { 1, new List<Vector3> { new Vector3(0,6,0), new Vector3(0,6,32) }, 3f, 0f, 3f, 3f, neighbors });
            Require(corridor != null, "Real neighboring track was ignored");
            var far = (float[])corridor.GetType().GetField((side < 0 ? "Left" : "Right") + "DeckFar").GetValue(corridor);
            Require(far.All(f => f > 1.5f && f < 2.5f), "Real encroachment no longer trims deck");
        }
    }

    static void CheckStationGrade()
    {
        var manager = assembly.GetType("PathManager");
        var evaluate = manager.GetMethod("EvaluateStationGrade");
        var profile = new List<List<float>> {
            new List<float> { 0, 100, 0 }, new List<float> { 0, 100.4f, 40 }, new List<float> { 20, 100.8f, 80 }
        };
        var samples = new[] { new Vector3(-4, 999, 20), new Vector3(4, -999, 20) };
        foreach (var sample in samples)
            Require(Mathf.Abs((float)evaluate.Invoke(null, new object[] { profile, sample }) - 100.2f) < .0001f, "Cross slope grade mismatch");
        profile.Reverse();
        Require(Mathf.Abs((float)evaluate.Invoke(null, new object[] { profile, samples[0] }) - 100.2f) < .0001f, "Reversed grade mismatch");

        var summaryType = assembly.GetType("GeneratedModels.PathSummary");
        var path = Activator.CreateInstance(summaryType);
        summaryType.GetField("station_grade_profile").SetValue(path, profile.Select(p => (object)p).ToList());
        summaryType.GetField("path_variant").SetValue(path, 451);
        var relative = (float)assembly.GetType("PathSummaryExtensions").GetMethod("GetRelativeHeightAtPoint").Invoke(null, new object[] { path, 0f, 20f });
        var ground = (float)assembly.GetType("Utils").GetMethod("GetGroundHeight").Invoke(null, new object[] { new Vector3(0,0,20) });
        Require(Mathf.Abs(relative + ground - 100.2f) < .0001f, "Snapping lost fractional design height");
        var snap = (Vector3)assembly.GetType("PolylineUtils").GetMethod("ClosestPointOnSegmentXZ").Invoke(null,
            new object[] { new Vector3(3,0,40), new Vector3(0,100,0), new Vector3(0,104,80) });
        Require(Mathf.Abs(snap.z - 40f) < .0001f && Mathf.Abs(snap.y - 102f) < .0001f, "Ground cursor shifted grade snap along track");
        Type planType = assembly.GetType("RoadPlanManager");
        var planObject = new GameObject("SnapQA");
        var plan = planObject.AddComponent(planType);
        planType.GetField("LateralOffsets").SetValue(plan, new[] { -4f, 4f });
        object[] offsetArgs = { Vector3.zero, new Vector3(4, 2.375f, 0), Vector3.forward, 0f, 5f, Vector3.zero, 0f, false };
        planType.GetMethod("EvaluateTangentOffsetCandidates", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(plan, offsetArgs);
        Require((bool)offsetArgs[7] && Mathf.Abs(((Vector3)offsetArgs[5]).y - 2.375f) < .0001f, "Parallel offset lost target absolute height");
        UnityEngine.Object.DestroyImmediate(planObject);
        var own = new List<Vector3> { new Vector3(0, 999, 0), new Vector3(0, -999, 40) };
        manager.GetMethod("ApplyStationGrade").Invoke(null, new object[] { path, own });
        float firstHeight = own[0].y;
        Require(Mathf.Abs(own[1].y - own[0].y - .4f) < .0001f, "Stored grade slope lost");
        own[0] = new Vector3(0, 500, 0);
        manager.GetMethod("ApplyStationGrade").Invoke(null, new object[] { path, own });
        Require(Mathf.Abs(own[0].y - firstHeight) < .0001f, "Terrain reload changed stored grade");

        Type neighborType = assembly.GetType("TrackNeighborInfo");
        foreach (float difference in new[] { 0f, .01f, .03f, .5f })
        {
            var neighbor = Activator.CreateInstance(neighborType);
            neighborType.GetField("PathId").SetValue(neighbor, 2);
            neighborType.GetField("Width").SetValue(neighbor, 5f);
            neighborType.GetField("PlatformLeft").SetValue(neighbor, 3f);
            neighborType.GetField("Points").SetValue(neighbor, new List<Vector3> { new Vector3(8, difference, 0), new Vector3(8, difference, 80) });
            var neighbors = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(neighborType));
            neighbors.Add(neighbor);
            var corridor = assembly.GetType("TrackAdjacency").GetMethod("Compute").Invoke(null, new object[] {
                1, new List<Vector3> { Vector3.zero, new Vector3(0,0,80) }, 5f, 0f, 0f, 3f, neighbors
            });
            bool joined = corridor != null && ((bool[])corridor.GetType().GetField("RightDeckJoined").GetValue(corridor)).Any(v => v);
            Require(joined == (difference <= .02f), "Island deck height tolerance mismatch");
        }
    }
}
