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
            bool connectorsOnly = args.Contains("-railConnectorsOnly");
            if (!endsOnly && !connectorsOnly) CheckStationGrade();
            Type modelType = assembly.GetType("ModelManager");
            var model = new GameObject("Models").AddComponent(modelType);
            modelType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, model);
            var cache = (IDictionary)modelType.GetField("_modelCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(model);
            if (endsOnly)
            {
                foreach (string part in new[] { "buffer_stop", "platform_end" })
                    cache[$"Models/Paths/p4/{part}"] = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Paths/p4/{part}.fbx");
                CheckTerminations();
                File.WriteAllText(report, $"PASS platform ends: {checks} checks; continuation floors, real encroachment, rail and monorail end walls, bundled double track ends and platforms");
                EditorApplication.Exit(0);
                return;
            }
            foreach (int variant in connectorsOnly ? new[] { 411 } : new[] { 401, 411, 451, 461 })
            {
                string asset = $"Assets/Paths/p{variant}/track.fbx";
                var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
                importer.isReadable = true;
                importer.SaveAndReimport();
                cache[$"Models/Paths/p{variant}/track"] = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
                if (!connectorsOnly)
                {
                    CheckTrack(variant, 32f);
                    CheckTrack(variant, 256f);
                    var source = ((GameObject)cache[$"Models/Paths/p{variant}/track"]).GetComponent<MeshFilter>().sharedMesh;
                    CheckTrack(variant, source.bounds.size.z * (60000 / source.vertexCount + 1));
                }
            }
            foreach (string part in new[] { "buffer_stop", "platform_end" })
                cache[$"Models/Paths/p4/{part}"] = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Paths/p4/{part}.fbx");
            CheckRailConnectors(cache);
            if (connectorsOnly)
            {
                File.WriteAllText(report, $"PASS rail connectors: {checks} checks; real FBX sleeper thickness, vertex budget, normal/close, Y junction wire endpoints and non-electrified branch");
                EditorApplication.Exit(0);
                return;
            }
            CheckTerminations();
            foreach (int variant in new[] { 451, 461, 462 })
                foreach (string part in new[] { "platform", "platform_wall" }) {
                    string asset = $"Assets/Paths/p{variant}/{part}.fbx";
                    var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
                    if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
                    cache[$"Models/Paths/p{variant}/{part}"] = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
                }
            foreach (int variant in new[] { 451, 461, 462 })
                foreach (bool left in new[] { false, true })
                    foreach (float direction in new[] { 1f, -1f })
                        Check(variant, left, direction);
            RenderEnds(root, cache);
            File.WriteAllText(report, $"PASS platforms: {checks} checks across 12 station sides; authored part spacing, track sections and vertex-budget splits");
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
            new[] { 0f, 64f }, left, 0f, 2.5f, 3f, material, $"Models/Paths/p{variant}", "Models/Paths/p4",
            TrackData(assembly.GetType("GeneratedModels.PathSummary"), 1, variant, 1, 2, 0f, direction * 64f)
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
                var source = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Paths/p{variant}/{(wall ? "platform_wall" : "platform")}.fbx").GetComponent<MeshFilter>().sharedMesh;
                // 最も長い垂直辺から柱の断面を選び、上屋の高さ・支柱位置が異なるモデルにも同じ測定規則を使う。
                Vector3 columnTop = ColumnTop(source);
                var locations = vertices.Where(v => Mathf.Abs(v.y - columnTop.y) < .001f && Mathf.Abs(v.x * side - columnTop.x) < .001f)
                    .Select(v => v.z * direction);
                var authored = source.vertices.Where(v => Mathf.Abs(v.y - columnTop.y) < .001f && Mathf.Abs(v.x - columnTop.x) < .001f).ToArray();
                // 著作タイルを実寸で64mに並べた位置と比較し、支柱・壁の構造ごとの周期を保つことを検査する。
                var expected = ClusterCenters(Enumerable.Range(0, Mathf.RoundToInt(64f / source.bounds.size.z))
                    .SelectMany(tile => authored.Select(v => tile * source.bounds.size.z + v.z - source.bounds.min.z)));
                var centers = ClusterCenters(locations);
                Require(centers.Length == expected.Length, $"p{variant} {filter.name} count={centers.Length}, expected={expected.Length}");
                for (int i = 0; i < centers.Length; i++) Require(Mathf.Abs(centers[i] - expected[i]) < .005f, "Compressed or shifted pillar spacing");
            }
            else if (filter.name.StartsWith("PlatformDeck"))
                Require(vertices.All(v => v.x * side >= 1.499f && v.x * side <= 5.501f), "Deck side mismatch");
        }
        UnityEngine.Object.DestroyImmediate(parent);
        UnityEngine.Object.DestroyImmediate(material);
    }
    static void Require(bool valid, string message) { if (!valid) throw new Exception(message); checks++; }

    static float[] ClusterCenters(IEnumerable<float> locations)
    {
        var groups = new List<List<float>>();
        foreach (float z in locations.OrderBy(z => z))
        {
            if (groups.Count == 0 || z - groups.Last().Last() > .25f) groups.Add(new List<float>());
            groups.Last().Add(z);
        }
        return groups.Select(g => (g.Min() + g.Max()) * .5f).ToArray();
    }

    static Vector3 ColumnTop(Mesh source)
    {
        var vertices = source.vertices;
        var triangles = source.triangles;
        Vector3 top = default;
        float height = 0f;
        for (int i = 0; i < triangles.Length; i += 3)
            for (int edge = 0; edge < 3; edge++)
            {
                Vector3 a = vertices[triangles[i + edge]], b = vertices[triangles[i + (edge + 1) % 3]];
                if (Mathf.Abs(a.x - b.x) > .001f || Mathf.Abs(a.z - b.z) > .001f || Mathf.Abs(a.y - b.y) <= height) continue;
                height = Mathf.Abs(a.y - b.y);
                top = a.y > b.y ? a : b;
            }
        Require(height > .5f, "Platform column measurement missing");
        return top;
    }

    static void CheckTrack(int variant, float length)
    {
        var parent = new GameObject("TrackPeriodQA");
        var road = assembly.GetType("RoadPath");
        object spline = road.GetMethod("BuildSpline").Invoke(null, new object[] {
            parent, new List<Vector3> { Vector3.zero, new Vector3(0,0,length) }, false });
        // 寸法の検査はカメラに依存させず、256m以上でも詳細メッシュを全区間に生成する。
        var context = assembly.GetType("PathDrawContext");
        context.GetMethod("Begin").Invoke(null, new object[] { Enum.Parse(assembly.GetType("DrawLod"), "Normal"), 0.0, 1.0 });
        context.GetProperty("SuppressDetailRange").SetValue(null, true);
        try
        {
            road.GetMethod("CreateSplineMesh").Invoke(null, new object[] {
                parent, spline, $"Models/Paths/p{variant}/track", 5f, 0f, .12f, false, null,
                false, false, 0.0, 1.0, false, true, 0f, true });
        }
        finally { context.GetMethod("End").Invoke(null, null); }
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
        Require(intervals.Count > 0, $"p{variant} track mesh missing on {length}m path");
        Require(Mathf.Abs(intervals[0].x) < .01f && Mathf.Abs(intervals.Last().y-length) < .01f, "Track extent changed");
        for (int i=1; i<intervals.Count; i++) Require(Mathf.Abs(intervals[i].x-intervals[i-1].y) < .01f, "Track split gap");
        var modelType = assembly.GetType("ModelManager");
        var model = modelType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var cache = (IDictionary)modelType.GetField("_modelCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(model);
        var source = ((GameObject)cache[$"Models/Paths/p{variant}/track"]).GetComponent<MeshFilter>().sharedMesh;
        if ((long)source.vertexCount * Mathf.RoundToInt(length / source.bounds.size.z) > 60000)
            Require(intervals.Count > 1, "Track over vertex budget was not split");
        UnityEngine.Object.DestroyImmediate(parent);
    }

    static void CheckRailConnectors(IDictionary cache)
    {
        // 分岐器のtrack代用にも本線と同じ実FBXを使い、タイル圧縮と頂点上限による伸長を検出する。
        foreach (string part in new[] { "track_close", "wire" })
        {
            string asset = $"Assets/Paths/p411/{part}.fbx";
            var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
            importer.isReadable = true;
            importer.SaveAndReimport();
            cache[$"Models/Paths/p411/{part}"] = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
        }
        var specType = assembly.GetType("RailConnectorSpec");
        var create = assembly.GetType("TrackPath").GetMethod("CreateRailConnectorObject");
        var context = assembly.GetType("PathDrawContext");
        foreach (bool close in new[] { false, true })
        foreach (float length in new[] { 7.7f, 32f, 256f })
        foreach (bool wire in new[] { false, true })
        {
            var parent = new GameObject("RailConnectorPeriodQA");
            parent.transform.position = new Vector3(12f, 6f, -15f);
            var points = new List<Vector3> { Vector3.up * .12f, new Vector3(0, .12f, length) };
            var spec = Activator.CreateInstance(specType, new object[] { points, 411, 4 });
            specType.GetField("HasWire").SetValue(spec, wire);
            var lod = Enum.Parse(assembly.GetType("DrawLod"), close ? "Close" : "Normal");
            context.GetMethod("Begin").Invoke(null, new object[] { lod, 0.0, 1.0 });
            context.GetProperty("SuppressDetailRange").SetValue(null, true);
            try { create.Invoke(null, new object[] { parent.transform, spec, 0 }); }
            finally { context.GetMethod("End").Invoke(null, null); }
            var meshes = parent.GetComponentsInChildren<MeshFilter>();
            var tracks = meshes.Where(f => f.name.EndsWith("/track") && f.sharedMesh != null).ToArray();
            Require(tracks.Length > 0, "Connector track missing");
            var source = ((GameObject)cache[$"Models/Paths/p411/track{(close ? "_close" : "")}"]).GetComponent<MeshFilter>().sharedMesh;
            var original = source.vertices;
            int first = Array.FindIndex(original, v => Mathf.Abs(v.y - .2f) < .002f && Mathf.Abs(v.x) > 1f);
            int last = Array.FindIndex(original, v => first >= 0 && Mathf.Abs(v.x-original[first].x) < .002f
                && Mathf.Abs(v.y-original[first].y) < .002f && Mathf.Abs(v.z-original[first].z) > .1f
                && Mathf.Abs(v.z-original[first].z) < .3f);
            Require(first >= 0 && last >= 0, "Sleeper thickness measurement missing");
            foreach (var filter in tracks)
            {
                var mesh = filter.sharedMesh;
                Require(mesh.vertexCount <= 60000, "Connector exceeds vertex budget");
                var vertices = mesh.vertices;
                for (int tile = 0; tile < vertices.Length / original.Length; tile++)
                {
                    float thickness = Vector3.Distance(vertices[tile*original.Length+first], vertices[tile*original.Length+last]);
                    float expected = Mathf.Abs(original[last].z-original[first].z);
                    Require(Mathf.Abs(thickness / expected - 1f) < .06f, "Connector sleeper thickness differs from authored size");
                }
            }
            var all = tracks.SelectMany(f => f.sharedMesh.vertices.Select(v => parent.transform.InverseTransformPoint(f.transform.TransformPoint(v)))).ToArray();
            Require(Mathf.Abs(all.Min(v => v.z)) < .01f && Mathf.Abs(all.Max(v => v.z)-length) < .01f, "Connector extent changed");
            if ((long)source.vertexCount * Mathf.RoundToInt(length/source.bounds.size.z) > 60000)
                Require(tracks.Length > 1, "Connector over vertex budget was not split");
            var wires = meshes.Where(f => f.name.EndsWith("/wire") && f.sharedMesh != null).ToArray();
            Require((wires.Length > 0) == wire, "Connector wire presence mismatch");
            if (wire)
            {
                var wireSource = ((GameObject)cache["Models/Paths/p411/wire"]).GetComponent<MeshFilter>().sharedMesh;
                var vertices = wires.SelectMany(f => f.sharedMesh.vertices.Select(v => parent.transform.InverseTransformPoint(f.transform.TransformPoint(v)))).ToArray();
                Require(Mathf.Abs(vertices.Min(v => v.y)-wireSource.bounds.min.y-.12f) < .002f, "Wire height differs from main track");
                Require(Mathf.Abs(vertices.Min(v => v.z)) < .01f && Mathf.Abs(vertices.Max(v => v.z)-length) < .01f, "Wire does not reach connector ends");
            }
            UnityEngine.Object.DestroyImmediate(parent);
        }
        CheckWireSpecs(cache);
    }

    static void CheckWireSpecs(IDictionary cache)
    {
        var summary = assembly.GetType("GeneratedModels.PathSummary");
        var storeType = assembly.GetType("MasterDataStore");
        var instance = storeType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        var store = new GameObject("WireMasterFixture").AddComponent(storeType);
        instance.SetValue(null, store);
        var masters = (IDictionary)storeType.GetField("pathMasterById", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(store);
        foreach (int variant in new[] { 401, 411 })
        {
            var item = Activator.CreateInstance(assembly.GetType("PathMasterItem"));
            item.GetType().GetField("width").SetValue(item, 5f);
            item.GetType().GetField("lanes").SetValue(item, (int?)1);
            item.GetType().GetField("wire_spacing").SetValue(item, variant == 411 ? (float?)35f : null);
            masters[variant] = item;
        }
        try
        {
            foreach (int branchVariant in new[] { 411, 401 })
            {
                var paths = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(summary));
                paths.Add(TrackData(summary, 1, 411, 1, 2, 0f, -32f));
                paths.Add(TrackData(summary, 2, 411, 1, 3, 0f, 32f));
                var branch = TrackData(summary, 3, branchVariant, 1, 4, 0f, 32f);
                summary.GetField("end_point").SetValue(branch, new List<object> { 8f, 0f, 32f });
                paths.Add(branch);
                var cuts = new Dictionary<int, Vector2> { { 1, new Vector2(0,-8) }, { 2, new Vector2(0,8) }, { 3, new Vector2(2,8) } };
                var specs = (IList)assembly.GetType("TrackIntersectionMeshGenerator").GetMethod("BuildRailConnectorSpecs")
                    .Invoke(null, new object[] { 1, Vector3.zero, true, paths, cuts, null });
                Require(specs != null && specs.Count == 2, "Y junction connectors missing");
                int count = specs.Cast<object>().Count(s => (bool)s.GetType().GetField("HasWire").GetValue(s));
                Require(count == (branchVariant == 411 ? 2 : 1), "Wire must connect electrified ends only");
                var parent = new GameObject("YJunctionWireQA");
                foreach (var spec in specs)
                    assembly.GetType("TrackPath").GetMethod("CreateRailConnectorObject").Invoke(null, new object[] { parent.transform, spec, 0 });
                var wires = parent.GetComponentsInChildren<MeshFilter>().Where(f => f.name.EndsWith("/wire") && f.sharedMesh != null).ToArray();
                Require(wires.Length == count, "Y junction wire geometry missing");
                var source = ((GameObject)cache["Models/Paths/p411/wire"]).GetComponent<MeshFilter>().sharedMesh;
                foreach (var spec in specs.Cast<object>().Where(s => (bool)s.GetType().GetField("HasWire").GetValue(s)))
                {
                    var points = (List<Vector3>)spec.GetType().GetProperty("Points").GetValue(spec);
                    foreach (var end in new[] { points[0], points[points.Count-1] })
                    {
                        var expected = end + Vector3.up * source.bounds.min.y;
                        Require(wires.SelectMany(f => f.sharedMesh.vertices.Select(v => f.transform.TransformPoint(v)))
                            .Min(v => Vector3.Distance(v, expected)) < .06f, "Wire does not reach curved junction endpoint");
                    }
                }
                UnityEngine.Object.DestroyImmediate(parent);
            }
        }
        finally { instance.SetValue(null, null); UnityEngine.Object.DestroyImmediate(store.gameObject); }
    }

    static readonly List<object> connected = new List<object>();
    static List<T> Connections<T>(int node) { return connected.Cast<T>().ToList(); }
    static List<T> NodeConnections<T>(int node)
    {
        return connected.Cast<T>().Where(p => (int)typeof(T).GetField("start_node_id").GetValue(p) == node
            || (int)typeof(T).GetField("end_node_id").GetValue(p) == node).ToList();
    }

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
            parent, spline, null, points, new[] { Vector3.right, Vector3.right }, new[] { 0f,32f }, false, 0f, 2.5f, 3f,
            material, "Models/Paths/p461", "Models/Paths/p4", path });
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

    // 始点ノード・終点ノードと中心線の両端(z)を持つ線路。車止めの判定は交差点のコネクタと同じく転回角と進む向きを見る。
    static object TrackData(Type summary, int id, int variant, int startNode, int endNode, float startZ, float endZ, int direction = 0)
    {
        var path = Activator.CreateInstance(summary);
        foreach (var pair in new[] { ("path_id", id), ("path_type", 4), ("path_variant", variant), ("start_node_id", startNode), ("end_node_id", endNode), ("travel_direction", direction) })
            summary.GetField(pair.Item1).SetValue(path, pair.Item2);
        summary.GetField("start_point").SetValue(path, new List<object> { 0f, 0f, startZ });
        summary.GetField("end_point").SetValue(path, new List<object> { 0f, 0f, endZ });
        return path;
    }

    static void CheckTerminations()
    {
        var track = assembly.GetType("TrackPath");
        var summary = assembly.GetType("GeneratedModels.PathSummary");
        var path = TrackData(summary, 1, 461, 1, 2, 0f, 32f);
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
                // 両端とも直進で続く線路。端点がノードに無い線路は接続判定から外れる。
                connected.Add(TrackData(summary, 2, 0, 3, 1, -32f, 0f));
                connected.Add(TrackData(summary, 3, 0, 2, 4, 32f, 64f));
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
        CheckBundledTracks();
    }

    // まとめた複線(軌道数2)と駅のホームの軌道端。パスと進む向きで軌道が決まり、使われない軌道の端にだけ車止めが付く。
    static void CheckBundledTracks()
    {
        var track = assembly.GetType("TrackPath");
        var summary = assembly.GetType("GeneratedModels.PathSummary");
        var storeType = assembly.GetType("MasterDataStore");
        var instance = storeType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        var store = new GameObject("MasterFixture").AddComponent(storeType);
        instance.SetValue(null, store);
        var masters = (IDictionary)storeType.GetField("pathMasterById", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(store);
        foreach (var (id, width, lanes, platform, center) in new[] { (421, 10f, 2, 0f, 0f), (411, 5f, 1, 0f, 0f), (461, 5f, 1, 3f, 0f), (472, 16f, 2, 3f, 6f) })
        {
            var item = Activator.CreateInstance(assembly.GetType("PathMasterItem"));
            item.GetType().GetField("type").SetValue(item, 4);
            item.GetType().GetField("width").SetValue(item, width);
            item.GetType().GetField("lanes").SetValue(item, (int?)lanes);
            item.GetType().GetField("platform_width").SetValue(item, platform > 0f ? (float?)platform : null);
            item.GetType().GetField("center_width").SetValue(item, (float?)center);
            masters[id] = item;
        }
        var provider = track.GetField("ConnectedPathsProvider");
        provider.SetValue(null, Delegate.CreateDelegate(provider.FieldType,
            typeof(PlatformQA).GetMethod("Connections", BindingFlags.Static|BindingFlags.NonPublic).MakeGenericMethod(summary)));
        var terminations = track.GetMethod("CreateTerminations", BindingFlags.Static|BindingFlags.NonPublic);
        var connectors = assembly.GetType("TrackIntersectionMeshGenerator").GetMethod("BuildRailConnectorSpecs");
        const int forward = 1, backward = 2;
        var points = new List<Vector3> { Vector3.zero, new Vector3(0, 0, 32) };
        try
        {
            // 複線の終点(ノード2)から出ていく単線。一方通行なら、ノード2へ入ってくる向き(左側通行で+x側)の軌道だけが行き止まり。
            var bundled = TrackData(summary, 1, 421, 1, 2, 0f, 32f);
            foreach (int direction in new[] { forward, 0 })
            {
                var single = TrackData(summary, 2, 411, 2, 3, 32f, 64f, direction);
                connected.Clear(); connected.Add(bundled); connected.Add(single);
                var parent = new GameObject("BundledEndsQA");
                terminations.Invoke(null, new object[] { parent, points, null, bundled, 10f, 2, 0f, 0f, "Models/Paths/p421", "Models/Paths/p4" });
                var stops = parent.GetComponentsInChildren<Transform>().Where(t => t.name == "TrackBufferStop").ToArray();
                Require(stops.Count(t => t.position.z < 16f) == 2, "Bundled dead end must close both tracks");
                var joined = stops.Where(t => t.position.z > 16f).ToArray();
                Require(joined.Length == (direction == forward ? 1 : 0), $"Bundled end stops mismatch for direction {direction}");
                Require(joined.All(t => t.position.x > 2f), "Buffer stop placed on the track used by the one-way single");
                UnityEngine.Object.DestroyImmediate(parent);

                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(summary));
                list.Add(bundled); list.Add(single);
                var cuts = new Dictionary<int, Vector2> { { 1, new Vector2(0, 28) }, { 2, new Vector2(0, 36) } };
                var specs = (IList)connectors.Invoke(null, new object[] { 2, Vector3.zero, true, list, cuts, null });
                Require(specs != null && specs.Count == (direction == forward ? 1 : 2), $"Bundled connector count for direction {direction}");
                foreach (var spec in specs)
                {
                    var line = (List<Vector3>)spec.GetType().GetProperty("Points").GetValue(spec);
                    Require(Mathf.Abs(Mathf.Abs(line[0].x) - 2.5f) < .001f && Mathf.Abs(line[line.Count - 1].x) < .001f, "Connector does not join the track ends");
                    if (direction == forward) Require(line[0].x < 0f, "One-way single joined the wrong track of the bundled path");
                }
            }

            // 向きの食い違う一方通行でも、駅のホームは停車する列車が折り返すので両方向に走れるものとしてつなぐ。
            foreach (float platform in new[] { 3f, 0f })
            {
                var station = TrackData(summary, 1, platform > 0f ? 461 : 411, 1, 2, 0f, 32f, forward);
                var incoming = TrackData(summary, 2, 411, 2, 3, 32f, 64f, backward);
                connected.Clear(); connected.Add(station); connected.Add(incoming);
                var parent = new GameObject("PlatformDirectionQA");
                terminations.Invoke(null, new object[] { parent, points, null, station, 5f, 1, 0f, 0f, "Models/Paths/p461", "Models/Paths/p4" });
                int stops = parent.GetComponentsInChildren<Transform>().Count(t => t.name == "TrackBufferStop" && t.position.z > 16f);
                Require(stops == (platform > 0f ? 0 : 1), "Platform must accept trains in both directions");
                UnityEngine.Object.DestroyImmediate(parent);
            }

            // まとめた複線の駅(幅10m・2軌道・ホーム3m)は、各側の外側の軌道(±2.5m)を原点に単線の駅の部品を置き、
            // デッキは淵ライン(軌道から1.5m)から外縁(width/2+platform_width)まで張る。
            var modelType = assembly.GetType("ModelManager");
            var model = modelType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var cache = (IDictionary)modelType.GetField("_modelCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(model);
            foreach (string part in new[] { "platform", "platform_wall" })
            {
                string asset = $"Assets/Paths/p461/{part}.fbx";
                var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
                if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
                cache[$"Models/Paths/p461/{part}"] = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            }
            foreach (bool left in new[] { false, true })
            {
                var parent = new GameObject("BundledStationQA");
                var line = new List<Vector3> { Vector3.zero, new Vector3(0, 0, 64) };
                object spline = assembly.GetType("RoadPath").GetMethod("BuildSpline").Invoke(null, new object[] { parent, line, true });
                var material = new Material(Shader.Find("Standard"));
                track.GetMethod("CreatePlatformSide", BindingFlags.Static|BindingFlags.NonPublic).Invoke(null, new object[] {
                    parent, spline, null, line, new[] { Vector3.right, Vector3.right }, new[] { 0f, 64f }, left, left ? -2.5f : 2.5f, 5f, 3f,
                    material, "Models/Paths/p461", "Models/Paths/p4", TrackData(summary, 1, 461, 1, 2, 0f, 64f) });
                foreach (var component in parent.GetComponentsInChildren<Component>()) {
                    if (component == null || component.GetType().Name != "SplineMesh") continue;
                    MethodInfo rebuild = component.GetType().GetMethod("RebuildImmediate");
                    rebuild.Invoke(component, rebuild.GetParameters().Length == 0 ? null : new object[] { true });
                }
                float side = left ? -1f : 1f;
                var filters = parent.GetComponentsInChildren<MeshFilter>();
                Require(filters.Any(f => f.name.EndsWith("/platform")) && filters.Any(f => f.name.EndsWith("/platform_wall")), "Bundled station parts missing");
                Require(filters.Any(f => f.name.StartsWith("TrackPlatformDeck")), "Bundled station deck missing");
                foreach (var filter in filters)
                {
                    var vertices = filter.sharedMesh.vertices.Select(v => filter.transform.TransformPoint(v)).ToArray();
                    if (filter.name.EndsWith("/platform") || filter.name.EndsWith("/platform_wall"))
                        Require(vertices.All(v => v.x * side > 3.75f && v.x * side < 8.15f), $"Bundled station part is not on its outer track (left={left})");
                    else if (filter.name.StartsWith("TrackPlatformDeck"))
                        Require(vertices.All(v => v.x * side >= 3.999f && v.x * side <= 8.001f), $"Bundled station deck exceeds edge line or outer edge (left={left})");
                }
                UnityEngine.Object.DestroyImmediate(parent);
                UnityEngine.Object.DestroyImmediate(material);
            }

            CheckIslandStation(track, summary, terminations);
            CheckTrackTransition(track, summary, masters);
        }
        finally
        {
            provider.SetValue(null, null);
            connected.Clear();
            instance.SetValue(null, null);
            UnityEngine.Object.DestroyImmediate(store.gameObject);
        }
    }

    // 島式ホームの駅(幅16m・2軌道・中央の間隔6m)。軌道は ±5.5m で、単線の駅の淵を左右の軌道から中央へ向けて置き、
    // デッキは両淵の間(±4.0m)に1枚だけ張る。外側へは張り出さず、ホーム端は続く島式の駅が無い端だけ淵から淵まで閉じる。
    static void CheckIslandStation(Type track, Type summary, MethodInfo terminations)
    {
        var laterals = (List<float>)assembly.GetType("LrtRoadParts").GetMethod("TrackLaterals").Invoke(null, new[] { TrackData(summary, 1, 472, 1, 2, 0f, 32f) });
        Require(laterals.Count == 2 && Mathf.Abs(laterals[0] + 5.5f) < .001f && Mathf.Abs(laterals[1] - 5.5f) < .001f, "Island station tracks are not opened by the center gap");
        var extents = new object[] { TrackData(summary, 1, 472, 1, 2, 0f, 32f), 3f, 0f, 0f };
        track.GetMethod("ResolvePlatformExtents").Invoke(null, extents);
        Require((float)extents[2] == 0f && (float)extents[3] == 0f, "Island station must not extend platforms outward");

        var parent = new GameObject("IslandStationQA");
        var line = new List<Vector3> { Vector3.zero, new Vector3(0, 0, 64) };
        object spline = assembly.GetType("RoadPath").GetMethod("BuildSpline").Invoke(null, new object[] { parent, line, true });
        track.GetMethod("CreateIslandPlatformParts", BindingFlags.Static|BindingFlags.NonPublic).Invoke(null, new object[] {
            parent, spline, line, null, 16f, 2, 6f, "Models/Paths/p461", "Models/Paths/p4", TrackData(summary, 1, 472, 1, 2, 0f, 64f) });
        foreach (var component in parent.GetComponentsInChildren<Component>()) {
            if (component == null || component.GetType().Name != "SplineMesh") continue;
            MethodInfo rebuild = component.GetType().GetMethod("RebuildImmediate");
            rebuild.Invoke(component, rebuild.GetParameters().Length == 0 ? null : new object[] { true });
        }
        var filters = parent.GetComponentsInChildren<MeshFilter>();
        var edges = filters.Where(f => f.name.EndsWith("/platform")).ToArray();
        Require(edges.Length == 2, "Island station needs one platform edge per track");
        Require(!filters.Any(f => f.name.EndsWith("/platform_wall")), "Island station has no outer wall");
        foreach (var edge in edges)
        {
            var vertices = edge.sharedMesh.vertices.Select(v => edge.transform.TransformPoint(v)).ToArray();
            bool leftTrack = vertices.Average(v => v.x) < 0f;
            float track0 = leftTrack ? -5.5f : 5.5f, side = leftTrack ? 1f : -1f;
            Require(vertices.All(v => (v.x - track0) * side > 1.25f && (v.x - track0) * side < 5.65f), "Island platform edge does not face the center");
        }
        var decks = filters.Where(f => f.name.StartsWith("TrackPlatformDeck")).ToArray();
        Require(decks.Length == 1, "Island station needs a single deck");
        Require(decks[0].sharedMesh.vertices.All(v => Mathf.Abs(decks[0].transform.TransformPoint(v).x) <= 4.001f), "Island deck exceeds the edge lines");
        UnityEngine.Object.DestroyImmediate(parent);

        var points = new List<Vector3> { Vector3.zero, new Vector3(0, 0, 32) };
        var island = TrackData(summary, 1, 472, 1, 2, 0f, 32f);
        var provider = track.GetField("ConnectedPathsProvider");
        provider.SetValue(null, Delegate.CreateDelegate(provider.FieldType,
            typeof(PlatformQA).GetMethod("NodeConnections", BindingFlags.Static|BindingFlags.NonPublic).MakeGenericMethod(summary)));
        foreach (bool continues in new[] { false, true })
        {
            connected.Clear(); connected.Add(island);
            if (continues) connected.Add(TrackData(summary, 2, 472, 2, 3, 32f, 64f));
            var ends = new GameObject("IslandEndsQA");
            terminations.Invoke(null, new object[] { ends, points, null, island, 16f, 2, 0f, 0f, "Models/Paths/p472", "Models/Paths/p4" });
            var walls = ends.GetComponentsInChildren<Transform>().Where(t => t.name == "TrackPlatformEnd").ToArray();
            Require(walls.Length == (continues ? 7 : 14), $"Island platform ends mismatch (continues={continues})");
            Require(walls.All(t => Mathf.Abs(t.position.x) <= 4.001f), "Island platform end outside the deck");
            Require(!walls.Any(t => continues && t.position.z > 16f), "Island platform closed toward a continuing island station");
            var stops = ends.GetComponentsInChildren<Transform>().Where(t => t.name == "TrackBufferStop").ToArray();
            Require(stops.All(t => Mathf.Abs(Mathf.Abs(t.position.x) - 5.5f) < .001f), "Buffer stop is not on an opened track");
            UnityEngine.Object.DestroyImmediate(ends);
        }
    }

    static readonly Dictionary<int, List<Vector3>> polylines = new Dictionary<int, List<Vector3>>();
    static List<Vector3> Polyline<T>(T path) { return polylines[(int)typeof(T).GetField("path_id").GetValue(path)]; }

    // 本編の PathManager.GetTrackLateralProfile と同じく、両端のノードにつながる線路から横位置の形を作る。
    static R LateralProfile<T, R>(T path)
    {
        int start = (int)typeof(T).GetField("start_node_id").GetValue(path);
        int end = (int)typeof(T).GetField("end_node_id").GetValue(path);
        return (R)assembly.GetType("TrackEndTransition").GetMethod("BuildProfile").Invoke(null, new object[] { path, NodeConnections<T>(start), NodeConnections<T>(end) });
    }

    static float Smootherstep(float x) { return x * x * x * (x * (x * 6f - 15f) + 10f); }

    // 軌道の位置が違う線路のつなぎ目(N§4.6・§4.7)。複線421(±2.5m)と島式の駅472(±5.5m)が真っすぐつながると、
    // 双方が最小曲線半径(既定100m)のS字を20.8mずつ受け持ち、ノードで両方の軌道が±4.0mにそろって板なしでつながる。
    // 線路のメッシュの軌道と道床もS字に沿う。道床の無いモノレールだけの分岐には板を作らずレールだけを置く。
    static void CheckTrackTransition(Type track, Type summary, IDictionary masters)
    {
        var transition = assembly.GetType("TrackEndTransition");
        var bundled = TrackData(summary, 1, 421, 1, 2, -64f, 0f);
        var island = TrackData(summary, 2, 472, 2, 3, 0f, 64f);
        summary.GetField("curved").SetValue(bundled, true);
        summary.GetField("curved").SetValue(island, true);
        var pair = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(summary));
        pair.Add(bundled); pair.Add(island);
        var shifts = (IDictionary)transition.GetMethod("ResolveNode").Invoke(null, new object[] { 2, pair });
        Require(shifts.Count == 2, "421-472 transition pair missing");
        float own = 0f;
        foreach (DictionaryEntry entry in shifts)
        {
            var shift = entry.Value;
            var outerShift = shift.GetType().GetMethod("OuterShift");
            own = (float)shift.GetType().GetField("Own").GetValue(shift);
            Require(Mathf.Abs(own - 20.81f) < .05f, $"Transition half length {own}");
            float outer = (float)outerShift.Invoke(shift, new object[] { 0f });
            Require(Mathf.Abs(((int)entry.Key == 1 ? 2.5f : 5.5f) + outer - 4f) < .001f, $"Track of path {entry.Key} does not meet at 4.0m ({outer})");
            Require(Mathf.Abs((float)outerShift.Invoke(shift, new object[] { own })) < .001f, "Transition extends beyond its length");
        }

        polylines.Clear();
        polylines[1] = Enumerable.Range(0, 33).Select(i => new Vector3(0, 0, -64 + i * 2)).ToList();
        polylines[2] = Enumerable.Range(0, 33).Select(i => new Vector3(0, 0, i * 2)).ToList();
        var resolver = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(summary, typeof(List<Vector3>)),
            typeof(PlatformQA).GetMethod("Polyline", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(summary));
        var generator = Activator.CreateInstance(assembly.GetType("RoadIntersectionMeshGenerator"));
        var generate = generator.GetType().GetMethod("GenerateWithSections");
        var joined = generate.Invoke(generator, new object[] { 2, Vector3.zero, pair, 0f, resolver, null });
        Require(joined != null && joined.GetType().GetProperty("TrackJoin").GetValue(joined) != null, "Straight 421-472 junction must join without a slab");
        Require(joined.GetType().GetProperty("Mesh").GetValue(joined) == null, "Continuous track junction built a slab");

        var modelType = assembly.GetType("ModelManager");
        var model = modelType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var cache = (IDictionary)modelType.GetField("_modelCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(model);
        foreach (string part in new[] { "body", "track" })
        {
            string asset = $"Assets/Paths/p421/{part}.fbx";
            var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
            if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
            cache[$"Models/Paths/p421/{part}"] = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
        }
        connected.Clear(); connected.Add(bundled); connected.Add(island);
        var connections = track.GetField("ConnectedPathsProvider");
        connections.SetValue(null, Delegate.CreateDelegate(connections.FieldType,
            typeof(PlatformQA).GetMethod("NodeConnections", BindingFlags.Static|BindingFlags.NonPublic).MakeGenericMethod(summary)));
        var lateral = track.GetField("LateralProfileProvider");
        lateral.SetValue(null, Delegate.CreateDelegate(lateral.FieldType,
            typeof(PlatformQA).GetMethod("LateralProfile", BindingFlags.Static|BindingFlags.NonPublic).MakeGenericMethod(summary, assembly.GetType("TrackLateralProfile"))));
        GameObject container = null;
        try
        {
            var setback = Activator.CreateInstance(assembly.GetType("SidewalkSetback"));
            var generated = track.GetMethod("Generate").Invoke(Activator.CreateInstance(track), new object[] { bundled, polylines[1], setback, setback });
            container = (GameObject)generated.GetType().GetProperty("GeneratedObject").GetValue(generated);
            foreach (var component in container.GetComponentsInChildren<Component>()) {
                if (component == null || component.GetType().Name != "SplineMesh") continue;
                MethodInfo rebuild = component.GetType().GetMethod("RebuildImmediate");
                rebuild.Invoke(component, rebuild.GetParameters().Length == 0 ? null : new object[] { true });
            }
            var filters = container.GetComponentsInChildren<MeshFilter>().Where(f => f.sharedMesh != null).ToArray();
            var tracks = filters.Where(f => f.name.EndsWith("/track")).ToArray();
            var bodies = filters.Where(f => f.name.EndsWith("/body")).ToArray();
            Require(tracks.Length >= 2 && bodies.Length >= 1, "Transition track or body missing");
            // Z平面と三角形辺の交点で断面を測る。頂点の配置間隔や砕石の裾野で軌道中心・道床天端を変えない。
            Vector2 LateralRange(MeshFilter[] parts, float sign, float z, float surfaceHeight)
            {
                var xs = new List<float>();
                foreach (var part in parts)
                {
                    var vertices = part.sharedMesh.vertices.Select(v => part.transform.TransformPoint(v)).ToArray();
                    var triangles = part.sharedMesh.triangles;
                    for (int i = 0; i < triangles.Length; i += 3)
                        for (int edge = 0; edge < 3; edge++)
                        {
                            Vector3 a = vertices[triangles[i + edge]], b = vertices[triangles[i + (edge + 1) % 3]];
                            if (Mathf.Abs(b.z - a.z) < .00001f || z < Mathf.Min(a.z, b.z) || z > Mathf.Max(a.z, b.z)) continue;
                            Vector3 at = Vector3.Lerp(a, b, (z - a.z) / (b.z - a.z));
                            if (Mathf.Abs(at.y - surfaceHeight) < .002f && at.x * sign > 0f) xs.Add(at.x * sign);
                        }
                }
                Require(xs.Count > 0, $"No surface at z={z}, height={surfaceHeight}");
                return new Vector2(xs.Min(), xs.Max());
            }
            var trackSource = ((GameObject)cache["Models/Paths/p421/track"]).GetComponent<MeshFilter>().sharedMesh;
            var heights = assembly.GetType("RoadPartHeights");
            float railTop = trackSource.bounds.max.y + (float)heights.GetField("TRACK_ABOVE_SPLINE").GetRawConstantValue();
            float bodyTop = (float)heights.GetField("BODY_ABOVE_SPLINE").GetRawConstantValue();
            float nodeShift = 3f * Smootherstep((own - .7f) / (2f * own));
            foreach (float sign in new[] { -1f, 1f })
            {
                Vector2 nearTrack = LateralRange(tracks, sign, -.7f, railTop), farTrack = LateralRange(tracks, sign, -55f, railTop);
                float nearCenter = (nearTrack.x + nearTrack.y) * .5f, farCenter = (farTrack.x + farTrack.y) * .5f;
                Require(Mathf.Abs(nearCenter - (2.5f + nodeShift)) < .12f, $"Track does not follow the transition near the node ({nearCenter})");
                Require(Mathf.Abs(farCenter - 2.5f) < .05f, $"Track moved outside the transition ({farCenter})");
                Require(Mathf.Abs(LateralRange(bodies, sign, -.7f, bodyTop).y - (5f + nodeShift)) < .15f, "Ballast does not widen with the tracks");
                Require(Mathf.Abs(LateralRange(bodies, sign, -55f, bodyTop).y - 5f) < .05f, "Ballast widened outside the transition");
            }
            Require(!container.GetComponentsInChildren<Transform>().Any(t => t.name == "TrackBufferStop" && t.position.z > -32f), "Buffer stop on the track joined to the island station");
        }
        finally
        {
            if (container != null) UnityEngine.Object.DestroyImmediate(container);
            lateral.SetValue(null, null);
            connections.SetValue(null, null);
            connected.Clear();
        }

        // 道床の無いモノレールだけの分岐ノードは、板を作らずにレールのつなぎだけを置く。
        var monorailMaster = Activator.CreateInstance(assembly.GetType("PathMasterItem"));
        monorailMaster.GetType().GetField("type").SetValue(monorailMaster, 5);
        monorailMaster.GetType().GetField("width").SetValue(monorailMaster, 6f);
        monorailMaster.GetType().GetField("lanes").SetValue(monorailMaster, (int?)2);
        masters[501] = monorailMaster;
        var branch = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(summary));
        var ends = new[] { new Vector3(0, 0, -64), new Vector3(0, 0, 64), new Vector3(16, 0, 62) };
        for (int i = 0; i < 3; i++)
        {
            int id = 11 + i;
            var path = TrackData(summary, id, 501, i == 0 ? 10 : 5, i == 0 ? 5 : 20 + i, 0f, 0f);
            summary.GetField("path_type").SetValue(path, 5);
            summary.GetField("curved").SetValue(path, true);
            Vector3 start = i == 0 ? ends[0] : Vector3.zero, end = i == 0 ? Vector3.zero : ends[i];
            summary.GetField("start_point").SetValue(path, new List<object> { start.x, 0f, start.z });
            summary.GetField("end_point").SetValue(path, new List<object> { end.x, 0f, end.z });
            polylines[id] = Enumerable.Range(0, 33).Select(k => Vector3.Lerp(start, end, k / 32f)).ToList();
            branch.Add(path);
        }
        var railsOnly = generate.Invoke(generator, new object[] { 5, Vector3.zero, branch, 0f, resolver, null });
        Require(railsOnly != null && (bool)railsOnly.GetType().GetProperty("RailsOnly").GetValue(railsOnly), "Monorail branch must be a rails-only node");
        Require(railsOnly.GetType().GetProperty("Mesh").GetValue(railsOnly) == null, "Monorail branch built a slab");
        var rails = (IList)railsOnly.GetType().GetProperty("RailConnectors").GetValue(railsOnly);
        Require(rails != null && rails.Count >= 2, "Monorail branch lost its rail connectors");
        masters.Remove(501);
        polylines.Clear();
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
                new object[] { 1, points, width, 0f, 3f, 3f, neighbors, 0f });
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
                new object[] { 1, new List<Vector3> { new Vector3(0,6,0), new Vector3(0,6,32) }, 3f, 0f, 3f, 3f, neighbors, 0f });
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
                1, new List<Vector3> { Vector3.zero, new Vector3(0,0,80) }, 5f, 0f, 0f, 3f, neighbors, 0f
            });
            bool joined = corridor != null && ((bool[])corridor.GetType().GetField("RightDeckJoined").GetValue(corridor)).Any(v => v);
            Require(joined == (difference <= .02f), "Island deck height tolerance mismatch");
        }
    }
}
