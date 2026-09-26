using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// 独立プロジェクトで実FBXと本編アセンブリを使い、軌間・配置・接続・プレハブ参照を検証する。
public static class LrtQA
{
    static Assembly asm;
    static IDictionary masters, models;
    static int checks;
    static string root;
    static void Check(bool ok, string text) { checks++; if (!ok) throw new Exception(text); }
    static void Set(object target, string field, object value) => target.GetType().GetField(field).SetValue(target, value);
    static IDictionary Manager(string name, string field)
    {
        var type = asm.GetType(name);
        var component = new GameObject(name).AddComponent(type);
        type.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, component);
        return (IDictionary)type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(component);
    }
    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        root = args[Array.IndexOf(args, "-sourceRoot") + 1];
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
                foreach (string dir in new[] { "Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins" })
                {
                    string path = Path.Combine(root, dir, new AssemblyName(e.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            asm = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            masters = Manager("MasterDataStore", "pathMasterById");
            models = Manager("ModelManager", "_modelCache");
            foreach (string file in Directory.GetFiles("Assets/Paths", "*.fbx", SearchOption.AllDirectories))
            {
                string asset = file.Replace('\\', '/');
                var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
                if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
                models["Models/Paths/" + asset.Substring("Assets/Paths/".Length).Replace(".fbx", "")] = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            }
            foreach (int id in new[] { 222, 242, 303, 431 })
            {
                Master(id);
                ModelDimensions(id);
                foreach (float slope in new[] { 0f, .08f, -.08f }) Road(id, slope, false);
                Road(id, 0, true);
            }
            foreach (int a in new[] { 222, 242, 303, 431 })
                foreach (int b in new[] { 222, 242, 303, 431 }) { Connections(a, b); Connections(a, b, true); }
            Terminations();
            StopIndex();
            PlatformLayout();
            foreach (int id in new[] { 222, 242, 303, 431 }) { PlatformModels(id); Platforms(id); }
            PlatformFallback();
            ElevatedPlatformWalls();
            foreach (int id in new[] { 303, 304, 305 }) Mall(id);
            ExportPrefabs();
            File.WriteAllText(root + "/Temp/LrtUnityQA/result.txt", "PASS LRT: " + checks + " checks; FBX, double track straight/curve/slope, all LRT connections, buffer stops, stop index, stop platforms, wrapper prefabs");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(root + "/Temp/LrtUnityQA/result.txt", e.ToString());
            Debug.LogException(e); EditorApplication.Exit(1);
        }
    }
    // 仕様値。303・431とも軌道中心は±1.75m(間隔3.5m)、LRT車体の半幅は1.27m。
    const float MallOffset = 1.75f, TramHalfWidth = 1.27f;
    static float ExpectedOffset(int id) => id == 222 ? 2.5625f : id == 242 ? 2.541667f : MallOffset;
    static readonly List<object> connected = new List<object>();
    static List<T> ConnectedPaths<T>(int node) => node == 1 ? connected.Cast<T>().ToList() : new List<T>();
    static void Master(int id)
    {
        var m = Activator.CreateInstance(asm.GetType("PathMasterItem"));
        Set(m, "width", id == 222 ? 24f : id == 242 ? 32f : id == 303 ? 12f : id == 431 ? 7f : 5f);
        Set(m, "lanes", id == 222 ? 4 : id == 242 ? 6 : id == 431 ? 2 : 1);
        Set(m, "sidewalk_width", id < 300 ? 3f : 0f);
        Set(m, "side_width", id < 300 ? .5f : 0f);
        Set(m, "center_width", id < 300 ? 1f : 0f);
        Set(m, "wire_spacing", 30f); Set(m, "follow_terrain", false);
        masters[id] = m;
    }
    static object PathData(int id, int key = 1, bool start = true)
    {
        var p = Activator.CreateInstance(asm.GetType("GeneratedModels.PathSummary"));
        Set(p, "path_id", key); Set(p, "path_variant", id);
        Set(p, "path_type", id < 300 ? 1 : id == 303 ? 3 : 4);
        Set(p, "start_node_id", start ? 1 : key + 10); Set(p, "end_node_id", start ? key + 10 : 1);
        Set(p, "start_point", new List<object> { 0f, 0f, start ? 0f : -50f });
        Set(p, "end_point", new List<object> { 0f, 0f, start ? 50f : 0f });
        Set(p, "follow_terrain", 0); Set(p, "curved", true);
        return p;
    }
    static void ModelDimensions(int id)
    {
        string part = id == 431 ? "track" : "lrt_track";
        var model = (GameObject)models[$"Models/Paths/p{id}/{part}"];
        var mesh = model.GetComponent<MeshFilter>().sharedMesh;
        float top = id == 431 ? .355f : .025f;
        Check(Mathf.Abs(mesh.bounds.max.y - top) < .002f, "rail top " + id);
        var xs = mesh.vertices.Where(v => Mathf.Abs(v.y - top) < .002f).Select(v => v.x).ToArray();
        Check(xs.Any(x => x > .68f && x < .76f) && xs.Any(x => x < -.68f && x > -.76f), "rail gauge " + id);
        var parts = asm.GetType("LrtRoadParts");
        float Float(string method, object arg) => (float)parts.GetMethod(method).Invoke(null, new[] { arg });
        float offset = Float("Offset", PathData(id));
        Check(Mathf.Abs(offset - ExpectedOffset(id)) < .0001f, "track center offset " + id);
        var laterals = (List<float>)parts.GetMethod("TrackLaterals").Invoke(null, new[] { PathData(id) });
        Check(laterals.Count == 2 && Mathf.Abs(laterals[0] + offset) < .0001f && Mathf.Abs(laterals[1] - offset) < .0001f, "double track laterals " + id);
        // 踏面: 埋め込み軌道はスプライン+0.1(路面)+0.025、431はスプライン+0.12(軌道下端)+0.355。
        Check(Mathf.Abs(Float("RailTopAboveSpline", id) - (id == 431 ? .475f : .125f)) < .0001f, "rail top above spline " + id);
        float width = id == 222 || id == 242 ? 1.2f : 2f;
        Check(Mathf.Abs(Float("PlatformInnerOffset", PathData(id)) - (ExpectedOffset(id) + 1.35f)) < .0001f, "platform inner edge " + id);
        Check(Mathf.Abs(Float("PlatformOuterOffset", PathData(id)) - (ExpectedOffset(id) + 1.35f + width)) < .0001f, "platform outer edge " + id);
        Check(Mathf.Abs(Float("PlatformSurfaceAboveSpline", id) - ((id == 431 ? .475f : .125f) + .3f)) < .0001f, "platform surface " + id);
        Check(Mathf.Abs(Float("PlatformRampLength", id) - (id == 431 ? 6f : 3f)) < .0001f, "platform ramp " + id);
        // 舗装・道床は複線の全幅で著作し、マスター幅(303=12m、431=7m)へ伸縮させない。
        if (id == 303 || id == 431)
            foreach (string body in id == 431 ? new[] { "body", "body_close" } : new[] { "body" })
            {
                var bounds = ModelMesh(id, body).bounds;
                Check(Mathf.Abs(bounds.size.x - (id == 303 ? 12f : 7f)) < .01f && Mathf.Abs(bounds.center.x) < .01f, $"{body} width {id}: {bounds.size.x}");
            }
        if (id == 303) MallPole((float)parts.GetField("MALL_POLE_OFFSET").GetRawConstantValue());
        if (id == 431) return;
        mesh = ((GameObject)models[$"Models/Paths/p{id}/lrt_wire"]).GetComponent<MeshFilter>().sharedMesh;
        Check(Mathf.Abs(mesh.bounds.center.y - 5.3f) < .002f, "wire height " + id);
    }
    static void MallPole(float poleOffset)
    {
        // 303 の両側柱は足元が±5.4m(RoadEquipmentGenerator が予約する位置)で、吊り金具は近い方の軌道(±1.75m)の真上。
        Check(Mathf.Abs(poleOffset - 5.4f) < .0001f, "mall pole offset constant");
        var pole = (GameObject)models["Models/Paths/p303/lrt_pole"];
        var mesh = pole.GetComponent<MeshFilter>().sharedMesh;
        var vertices = mesh.vertices;
        var feet = vertices.Where(v => v.y < .05f).Select(v => v.x).ToArray();
        Check(feet.Any(x => x < 0) && feet.Any(x => x > 0) && feet.All(x => Mathf.Abs(Mathf.Abs(x) - poleOffset) < .25f), "mall pole feet");
        var materials = pole.GetComponent<MeshRenderer>().sharedMaterials;
        int hangerSubmeshes = 0;
        for (int sub = 0; sub < mesh.subMeshCount; sub++)
        {
            if (!materials[sub].name.StartsWith("PATH_RAIL_METAL")) continue;
            hangerSubmeshes++;
            var xs = mesh.GetTriangles(sub).Select(i => vertices[i].x).ToArray();
            Check(xs.Any(x => x < 0) && xs.Any(x => x > 0) && xs.All(x => Mathf.Abs(Mathf.Abs(x) - MallOffset) < .05f), "mall pole hangers above tracks");
        }
        Check(hangerSubmeshes == 1, "mall pole hanger material");
    }
    static void Road(int id, float slope, bool curved)
    {
        var path = PathData(id);
        var points = new List<Vector3>();
        for (int i = 0; i <= 16; i++) points.Add(new Vector3(curved ? 8 * Mathf.Sin(i / 16f * Mathf.PI) : 0, slope * i * 5, i * 5));
        var type = asm.GetType(id == 431 ? "TrackPath" : "RoadPath");
        var setback = Activator.CreateInstance(asm.GetType("SidewalkSetback"));
        var result = type.GetMethod("Generate").Invoke(Activator.CreateInstance(type), new[] { path, points, setback, setback });
        var go = (GameObject)result.GetType().GetProperty("GeneratedObject").GetValue(result);
        Check(go != null, "generated " + id);
        var filters = go.GetComponentsInChildren<MeshFilter>();
        foreach (var f in filters) if (f.sharedMesh != null)
        {
            Check(f.sharedMesh.vertexCount <= 60000, "vertex budget " + f.name);
            Check(f.sharedMesh.vertices.All(v => !float.IsNaN(v.x + v.y + v.z)), "finite " + f.name);
        }
        string trackPart = id == 431 ? "/track" : "/lrt_track", wirePart = id == 431 ? "/wire" : "/lrt_wire";
        // 431 は地上・高架の区間ごとに軌道数ぶんのメッシュを敷く。
        var tracks = filters.Where(f => f.name.EndsWith(trackPart) || (id == 431 && f.name.EndsWith("/track_elevated"))).ToArray();
        Check(id == 431 ? tracks.Length >= 2 && tracks.Length % 2 == 0 : tracks.Length == 2, "track count " + id);
        Check(filters.Count(f => f.name.EndsWith(wirePart)) == 2, "wire count " + id);
        if (!curved && slope == 0)
        {
            // 直線では各軌道メッシュの横中心が軌道中心(左右対称の±offset)に一致する。
            var centers = tracks.Select(f => f.GetComponent<Renderer>().bounds.center.x).ToArray();
            Check(centers.All(x => Mathf.Abs(Mathf.Abs(x) - ExpectedOffset(id)) < .02f) && centers.Any(x => x < 0) && centers.Any(x => x > 0), "track lateral " + id);
        }
        if (id != 431)
        {
            var poles = go.GetComponentsInChildren<Transform>().Where(t => t.name == "LrtPole").ToArray();
            Check(poles.Length >= 2, "poles present " + id);
            Check(poles.All(t => Vector3.Dot(t.up, Vector3.up) > .9999f), "poles vertical " + id);
        }
        UnityEngine.Object.DestroyImmediate(go);
    }
    static void Connections(int a, int b, bool turn = false)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(asm.GetType("GeneratedModels.PathSummary")));
        list.Add(PathData(a, 1, false)); list.Add(PathData(b, 2, true));
        var cuts = new Dictionary<int, Vector2> { { 1, new Vector2(0, -12) }, { 2, new Vector2(0, 12) } };
        if (turn)
        {
            Set(list[1], "end_point", new List<object> { 50f, 0f, 0f });
            cuts[2] = new Vector2(12, 0);
        }
        var specs = (IList)asm.GetType("TrackIntersectionMeshGenerator").GetMethod("BuildRailConnectorSpecs").Invoke(null, new object[] { 1, Vector3.zero, true, list, cuts, null });
        if (turn && a == 431 && b == 431) { Check(specs == null, "dedicated track turn restriction"); return; }
        Check(specs != null && specs.Count == 2, $"connections {a}-{b}");
        foreach (var spec in specs)
        {
            var points = (List<Vector3>)spec.GetType().GetProperty("Points").GetValue(spec);
            float ao = ExpectedOffset(a), bo = ExpectedOffset(b);
            Check(Mathf.Abs(Mathf.Abs(points[0].x) - ao) < .0001f, "start rail alignment");
            Check(Mathf.Abs(Mathf.Abs(turn ? points[points.Count - 1].z : points[points.Count - 1].x) - bo) < .0001f, "end rail alignment");
            // 複線どうしの直進は上下線を取り違えず、左の軌道は左へ、右の軌道は右へつながる。
            if (!turn) Check(points[0].x * points[points.Count - 1].x > 0, $"straight connector keeps its side {a}-{b}");
            var go = new GameObject("ConnectorTest");
            asm.GetType("TrackPath").GetMethod("CreateRailConnectorObject").Invoke(null, new object[] { go.transform, spec, 0 });
            Check(go.GetComponentsInChildren<MeshFilter>().Length > 0, "connector mesh");
            UnityEngine.Object.DestroyImmediate(go);
        }
    }
    static void Terminations()
    {
        var track = asm.GetType("TrackPath");
        var summary = asm.GetType("GeneratedModels.PathSummary");
        // 431 は車止めを p4 の共通部品へフォールバックする。独立プロジェクトには無いので代用品で数える。
        var stub = new GameObject("BufferStopStub");
        models["Models/Paths/p4/buffer_stop"] = stub;
        var provider = track.GetField("ConnectedPathsProvider");
        provider.SetValue(null, Delegate.CreateDelegate(provider.FieldType,
            typeof(LrtQA).GetMethod("ConnectedPaths", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(summary)));
        try
        {
            // LRTの走行網へ続く431の端には車止めを立てず、通常鉄道とだけ接する端は2軌道とも閉じる。
            foreach (int other in new[] { 222, 242, 303, 431, 411 })
            {
                var path = PathData(431, 1, true);
                connected.Clear(); connected.Add(path); connected.Add(PathData(other, 2, false));
                var parent = new GameObject("LrtEndsQA");
                track.GetMethod("CreateTerminations", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] {
                    parent, new List<Vector3> { Vector3.zero, new Vector3(0, 0, 50) }, null, path, 7f, 2, 0f, 0f, "Models/Paths/p431", "Models/Paths/p4" });
                int stops = parent.GetComponentsInChildren<Transform>().Count(t => t.name == "TrackBufferStop");
                Check(stops == (other == 411 ? 2 : 0), $"buffer stops at 431-{other}");
                UnityEngine.Object.DestroyImmediate(parent);
            }
        }
        finally
        {
            provider.SetValue(null, null);
            models.Remove("Models/Paths/p4/buffer_stop");
            UnityEngine.Object.DestroyImmediate(stub);
            connected.Clear();
        }
    }
    static void Mall(int id)
    {
        Master(id);
        float width = id == 305 ? 20f : 12f;
        var m = masters[id];
        Set(m, "width", width); Set(m, "tree_spacing", 24f);
        if (id != 303) Set(m, "wire_spacing", 0f);
        string[] fields = { "streetlight_type", "street_tree_type", "bench_type", "bin_type", "planter_type", "advert_type" };
        foreach (string f in fields) Set(m, f + "_allowed", true);
        var path = PathData(id);
        Set(path, "path_type", 3);
        foreach (string f in fields) Set(path, f, 1);
        foreach (bool curved in new[] { false, true })
        {
            var points = new List<Vector3>();
            for (int i = 0; i <= 20; i++) points.Add(new Vector3(curved ? 4 * Mathf.Sin(i * Mathf.PI / 20) : 0, .02f*i*4, i*4));
            var type = asm.GetType("RoadPath");
            var setback = Activator.CreateInstance(asm.GetType("SidewalkSetback"));
            var result = type.GetMethod("Generate").Invoke(Activator.CreateInstance(type), new[] { path, points, setback, setback });
            var go = (GameObject)result.GetType().GetProperty("GeneratedObject").GetValue(result);
            foreach (string part in new[] { "bench", "bin", "planter", "advert", "tree" })
            {
                var placed = go.GetComponentsInChildren<MeshFilter>().Where(f => f.name.StartsWith(part + "(")).ToArray();
                Check(placed.Length > 0, "mall part present " + id + " " + part);
                foreach (var f in placed)
                {
                    Check(Vector3.Dot(f.transform.up, Vector3.up) > .9999f, "mall upright " + part);
                    Check(f.sharedMesh.vertices.All(v => !float.IsNaN(v.x+v.y+v.z)), "mall finite " + part);
                    if (!curved)
                    {
                        var b = f.GetComponent<Renderer>().bounds;
                        Check(b.min.x > -width/2 && b.max.x < width/2, "mall footprint " + id + " " + part);
                        Check(b.min.z > 1.5f && b.max.z < 78.5f, "mall end clearance " + part);
                        if (id == 304) Check(b.min.x >= -1.7f && b.max.x <= 1.7f, "central furniture island");
                        if (id == 305) Check(Mathf.Abs(f.transform.position.x) > 3.9f && Mathf.Abs(f.transform.position.x) < 4.1f, "two furniture rows");
                        if (id == 303)
                        {
                            // 複線の車体(軌道中心±1.75m・半幅1.27m)の上へ設備や樹冠を張り出さない。
                            float envelope = MallOffset + TramHalfWidth;
                            Check(b.max.x < -envelope || b.min.x > envelope, "LRT clearance " + part);
                            // 両側の架線柱(±5.4m、端から5m・30m間隔)の足元には、最小間隔1.2mより近く置かない。
                            Vector3 p = f.transform.position;
                            foreach (float z in new[] { 5f, 35f, 65f })
                                foreach (float x in new[] { -5.4f, 5.4f })
                                    Check(new Vector2(p.x - x, p.z - z).magnitude > 1.15f, "LRT pole foot reserved " + part);
                        }
                    }
                }
            }
            UnityEngine.Object.DestroyImmediate(go);
        }
        if (id == 303)
        {
            // 停留所のホーム区間(デッキ29〜51m+スロープ3m+余裕1m)には点設備を置かず、その外側には残す。
            UseStops(Stop(.5f, 22));
            try
            {
                var points = new List<Vector3>();
                for (int i = 0; i <= 20; i++) points.Add(new Vector3(0, 0, i * 4));
                var go = Generate(303, path, points);
                string[] equipment = { "bench", "bin", "planter", "advert", "tree", "streetlight" };
                var placed = go.GetComponentsInChildren<Transform>().Where(t => equipment.Any(e => t.name.StartsWith(e + "("))).ToArray();
                Check(placed.Length > 0, "mall equipment outside platform");
                Check(placed.All(t => t.position.z < 25f || t.position.z > 55f), "mall equipment kept clear of platform");
                Check(go.GetComponentsInChildren<Transform>().Count(t => t.name == "LrtShelter") == 2, "mall platform shelters");
                UnityEngine.Object.DestroyImmediate(go);
            }
            finally { UseStops(); }
        }
        foreach (string f in fields) Set(path, f, 0);
        var empty = new GameObject("NoEquipment");
        asm.GetType("RoadEquipmentGenerator").GetMethod("Generate").Invoke(null, new object[] { empty, null, path, width, 0f, 24f, null });
        Check(empty.transform.childCount == 0, "all equipment disabled " + id);
        UnityEngine.Object.DestroyImmediate(empty);
    }
    // 停留所のホーム。StopProvider はパス1にだけ停留所を返す。
    static readonly List<object> stops = new List<object>();
    static IReadOnlyList<T> Stops<T>(int path) => path == 1 && stops.Count > 0 ? stops.Cast<T>().ToList() : null;
    static Type PlatformParts => asm.GetType("LrtPlatformParts");
    static Type StopType => asm.GetType("LrtPlatformParts+Stop");
    static object Stop(float fraction, float deck) => Activator.CreateInstance(StopType, fraction, deck);
    static IList StopList(params object[] items)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(StopType));
        foreach (var item in items) list.Add(item);
        return list;
    }
    static void UseStops(params object[] items)
    {
        stops.Clear(); stops.AddRange(items);
        var provider = PlatformParts.GetField("StopProvider");
        provider.SetValue(null, items.Length == 0 ? null : Delegate.CreateDelegate(provider.FieldType,
            typeof(LrtQA).GetMethod("Stops", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(StopType)));
    }
    static void StopIndex()
    {
        // 路線サマリの stop_points から、車種がLRTの路線だけを下地パスごとに集める。下地未解決(null)の停留所は除き、
        // デッキ長は両数×車長+両端1m。独立プロジェクトには車種表が無いので、LRT(1003→14)とバス(1→10)を差し込む。
        var types = asm.GetType("VehicleModelConfig").GetField("transitTypes", BindingFlags.Static | BindingFlags.NonPublic);
        object saved = types.GetValue(null);
        types.SetValue(null, new Dictionary<string, int> { { "1", 10 }, { "1003", 14 } });
        try
        {
            var summaryType = asm.GetType("GeneratedModels.TransitLineSummary");
            var pointType = asm.GetType("GeneratedModels.TransitStopPoint");
            object Point(int node, int? path, float? fraction)
            {
                var point = Activator.CreateInstance(pointType);
                Set(point, "node_id", node); Set(point, "station_path_id", path); Set(point, "fraction", fraction);
                return point;
            }
            object Summary(int variant, int cars, params object[] points)
            {
                var summary = Activator.CreateInstance(summaryType);
                Set(summary, "path_variant", variant); Set(summary, "cars_per_train", cars); Set(summary, "length_per_vehicle", 10f);
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pointType));
                foreach (var point in points) list.Add(point);
                Set(summary, "stop_points", list);
                return summary;
            }
            var lines = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(summaryType));
            lines.Add(Summary(1003, 3, Point(4, 7, .75f)));
            lines.Add(Summary(1003, 2, Point(1, 7, .25f), Point(2, 8, .5f), Point(3, null, null)));
            lines.Add(Summary(1, 1, Point(5, 9, .5f)));
            var index = (IDictionary)asm.GetType("LrtStopPlatforms").GetMethod("Collect").Invoke(null, new object[] { lines });
            Check(index.Count == 2 && index.Contains(7) && index.Contains(8), "stop index keeps LRT underlays only");
            var shared = (IList)index[7];
            Check(shared.Count == 2 && Mathf.Abs(Number(shared[0], "Fraction") - .25f) < 1e-6f && Mathf.Abs(Number(shared[0], "DeckLength") - 22f) < 1e-4f
                && Mathf.Abs(Number(shared[1], "Fraction") - .75f) < 1e-6f && Mathf.Abs(Number(shared[1], "DeckLength") - 32f) < 1e-4f, "stop index sorted with deck length");
            Check(((IList)index[8]).Count == 1, "stop index per underlay");
        }
        finally { types.SetValue(null, saved); }
    }
    static List<Vector3> Line(float from, float to)
    {
        var line = new List<Vector3>();
        for (float z = from; z <= to + .001f; z += 1f) line.Add(new Vector3(0, 0, z));
        return line;
    }
    static IList Layout(IList stopList, List<Vector3> full, List<Vector3> trimmed, List<Vector3> drawn, float splineLength, float ramp) =>
        (IList)PlatformParts.GetMethod("Layout").Invoke(null, new object[] { stopList, full, trimmed, drawn, splineLength, ramp });
    static float Number(object target, string field) => Convert.ToSingle(target.GetType().GetField(field).GetValue(target));
    static void ExpectSpan(IList spans, int index, float from, float to, bool rampFrom, bool rampTo, string label)
    {
        Check(spans.Count > index, label + ": missing span");
        object span = spans[index];
        Check(Mathf.Abs(Number(span, "From") - from) < .01f && Mathf.Abs(Number(span, "To") - to) < .01f,
            $"{label}: {Number(span, "From")}..{Number(span, "To")} != {from}..{to}");
        Check((bool)span.GetType().GetField("RampAtFrom").GetValue(span) == rampFrom
            && (bool)span.GetType().GetField("RampAtTo").GetValue(span) == rampTo, label + ": ramps");
    }
    static void PlatformLayout()
    {
        // 未切断100m・切断済み10〜90m。停止中心50m・デッキ22mは、描画点列(=切断済み)の始点から29〜51m。
        List<Vector3> full = Line(0, 100), trimmed = Line(10, 90);
        var spans = Layout(StopList(Stop(.5f, 22)), full, trimmed, trimmed, 80, 3);
        Check(spans.Count == 1, "single platform"); ExpectSpan(spans, 0, 29, 51, true, true, "single platform");
        ExpectSpan(Layout(StopList(Stop(.5f, 22)), full, trimmed, trimmed, 160, 3), 0, 58, 102, true, true, "spline length scale");
        // 重なりと2m未満の隙間は併合し、2m以上なら別のホームにする。
        spans = Layout(StopList(Stop(.5f, 22), Stop(.68f, 12)), full, trimmed, trimmed, 80, 3);
        Check(spans.Count == 1, "merge gap < 2m"); ExpectSpan(spans, 0, 29, 64, true, true, "merge gap < 2m");
        Check(Layout(StopList(Stop(.5f, 22), Stop(.72f, 12)), full, trimmed, trimmed, 80, 3).Count == 2, "separate gap >= 2m");
        spans = Layout(StopList(Stop(.5f, 32), Stop(.5f, 22)), full, trimmed, trimmed, 80, 3);
        Check(spans.Count == 1, "shared stop"); ExpectSpan(spans, 0, 24, 56, true, true, "shared stop keeps longest");
        // 両端スロープぶんを残して切断済み区間へ収め、収めた後に4m未満なら描かない。
        ExpectSpan(Layout(StopList(Stop(.1f, 22)), full, trimmed, trimmed, 80, 3), 0, 3, 11, true, true, "clamped to cut");
        ExpectSpan(Layout(StopList(Stop(.1f, 22)), full, trimmed, trimmed, 80, 6), 0, 6, 11, true, true, "clamped with 431 ramp");
        Check(Layout(StopList(Stop(.05f, 22)), full, trimmed, trimmed, 80, 3).Count == 0, "too short after clamp");
        // トンネルで分けた描画区間では区間内だけを描き、区間で切られた端にはスロープを置かない。
        ExpectSpan(Layout(StopList(Stop(.5f, 22)), full, trimmed, Line(40, 90), 50, 3), 0, 0, 21, false, true, "tunnel run");
        Check(Layout(StopList(Stop(.5f, 22)), full, trimmed, Line(70, 90), 20, 3).Count == 0, "platform outside run");
        // ホーム区間(デッキ+スロープ+余裕)は重なれば1つにまとめる。
        var zones = (IList)PlatformParts.GetMethod("ZoneRanges").Invoke(null, new object[] {
            Layout(StopList(Stop(.5f, 22), Stop(.72f, 12)), full, trimmed, trimmed, 80, 3), 3f, 1f });
        Check(zones.Count == 1 && Mathf.Abs(((Vector2)zones[0]).x - 25) < .01f && Mathf.Abs(((Vector2)zones[0]).y - 72) < .01f, "platform zones merged");
    }
    static Mesh ModelMesh(int id, string part) => ((GameObject)models[$"Models/Paths/p{id}/{part}"]).GetComponent<MeshFilter>().sharedMesh;
    static float PlatformWidth(int id) => id == 222 || id == 242 ? 1.2f : 2f;
    static void PlatformModels(int id)
    {
        float width = PlatformWidth(id), ramp = id == 431 ? 6f : 3f;
        // Unity座標で右側用(+Xが軌道から外向き)。乗降縁1.35m・外縁1.35+W、著作長4m、上面はレール踏面+0.30m。
        var deck = ModelMesh(id, "lrt_platform");
        Check(Mathf.Abs(deck.bounds.min.x - 1.35f) < .02f && Mathf.Abs(deck.bounds.max.x - (1.35f + width)) < .02f,
            $"platform model lateral {id}: {deck.bounds.min.x}..{deck.bounds.max.x}");
        Check(Mathf.Abs(deck.bounds.size.z - 4f) < .01f, "platform model length " + id);
        Check(deck.vertices.Any(v => Mathf.Abs(v.y - .3f) < .003f), "platform model surface " + id);
        // スロープは断面中央原点・左右対称で、デッキ端(Z=0)の0.30mから+Zへ下る。
        var end = ModelMesh(id, "lrt_platform_end");
        Check(Mathf.Abs(end.bounds.min.x + width / 2) < .02f && Mathf.Abs(end.bounds.max.x - width / 2) < .02f, "ramp model width " + id);
        Check(end.bounds.min.z > -.02f && Mathf.Abs(end.bounds.max.z - ramp) < .05f, "ramp model length " + id);
        // 下端は前後の地面に揃う(埋め込み軌道は路面=踏面-0.025m、431はスプライン面=踏面-0.475m)。
        var roadParts = asm.GetType("LrtRoadParts");
        float foot = id == 431 ? -(float)roadParts.GetMethod("RailTopAboveSpline").Invoke(null, new object[] { id })
            : -(float)roadParts.GetField("EMBEDDED_RAIL_TOP").GetRawConstantValue();
        float footTop = end.vertices.Where(v => v.z > ramp - .05f).Max(v => v.y);
        Check(end.vertices.Where(v => v.z < .05f).Max(v => v.y) > .28f && Mathf.Abs(footTop - foot) < .01f, $"ramp model slope {id}: foot {footTop} != {foot}");
        // 上屋は設置面原点で、床に接する柱・脚・背板は背面(+X)側だけにあり、軌道側(-X)が開いている。
        var shelter = ModelMesh(id, "lrt_shelter");
        Check(Mathf.Abs(shelter.bounds.min.y) < .02f && shelter.bounds.size.x <= width && shelter.bounds.max.z < 2.05f && shelter.bounds.min.z > -2.05f,
            $"shelter model footprint {id}: {shelter.bounds}");
        Check(shelter.vertices.Where(v => v.y < .1f).All(v => v.x > 0f), "shelter open to track " + id);
    }
    static GameObject Generate(int id, object path, List<Vector3> points)
    {
        var type = asm.GetType(id == 431 ? "TrackPath" : "RoadPath");
        var setback = Activator.CreateInstance(asm.GetType("SidewalkSetback"));
        var result = type.GetMethod("Generate").Invoke(Activator.CreateInstance(type), new[] { path, points, setback, setback });
        return (GameObject)result.GetType().GetProperty("GeneratedObject").GetValue(result);
    }
    static List<Vector3> Straight(float height = 0f)
    {
        var points = new List<Vector3>();
        for (int i = 0; i <= 24; i++) points.Add(new Vector3(0, height, i * 5));
        return points;
    }
    static void Platforms(int id)
    {
        Master(id);
        // 120mの直線の中央に2両(デッキ22m)。デッキは49〜71m、両側の相対式。
        UseStops(Stop(.5f, 22));
        try
        {
            var go = Generate(id, PathData(id), Straight());
            float offset = ExpectedOffset(id), width = PlatformWidth(id), inner = offset + 1.35f, outer = inner + width;
            float rail = id == 431 ? .475f : .125f;
            var model = ModelMesh(id, "lrt_platform").bounds;
            var decks = go.GetComponentsInChildren<MeshRenderer>().Where(r => r.name.EndsWith("/lrt_platform")).ToArray();
            Check(decks.Length == 2, "platform decks " + id);
            foreach (var deck in decks)
            {
                var b = deck.bounds;
                float near = Mathf.Min(Mathf.Abs(b.min.x), Mathf.Abs(b.max.x)), far = Mathf.Max(Mathf.Abs(b.min.x), Mathf.Abs(b.max.x));
                Check(Mathf.Abs(near - (offset + model.min.x)) < .02f && Mathf.Abs(far - (offset + model.max.x)) < .02f, $"platform lateral {id}: {b}");
                Check(Mathf.Abs(b.min.z - 49) < .1f && Mathf.Abs(b.max.z - 71) < .1f, $"platform extent {id}: {b}");
                Check(Mathf.Abs(b.max.y - (rail + model.max.y)) < .02f && Mathf.Abs(b.min.y - (rail + model.min.y)) < .02f, $"platform height {id}: {b}");
            }
            Check(decks.Any(d => d.bounds.center.x > 0) && decks.Any(d => d.bounds.center.x < 0), "platforms on both tracks " + id);
            var ends = go.GetComponentsInChildren<Transform>().Where(t => t.name == "LrtPlatformEnd").ToArray();
            Check(ends.Length == 4, "platform ramps " + id);
            foreach (var t in ends)
            {
                bool start = t.position.z < 60;
                Check(Mathf.Abs(Mathf.Abs(t.position.x) - (inner + width / 2)) < .02f && Mathf.Abs(t.position.y - rail) < .02f, "ramp position " + id);
                Check(Mathf.Abs(t.position.z - (start ? 49 : 71)) < .05f && Vector3.Dot(t.forward, start ? Vector3.back : Vector3.forward) > .999f, "ramp faces outward " + id);
            }
            float back = ModelMesh(id, "lrt_shelter").bounds.max.x;
            var shelters = go.GetComponentsInChildren<Transform>().Where(t => t.name == "LrtShelter").ToArray();
            Check(shelters.Length == 2, "platform shelters " + id);
            foreach (var t in shelters)
            {
                Check(Mathf.Abs(Mathf.Abs(t.position.x) - (outer - back)) < .02f && Mathf.Abs(t.position.y - (rail + .3f)) < .02f && Mathf.Abs(t.position.z - 60) < .05f, "shelter position " + id);
                Check(Vector3.Dot(t.right, new Vector3(Mathf.Sign(t.position.x), 0, 0)) > .999f && Vector3.Dot(t.up, Vector3.up) > .9999f, "shelter back to outer edge " + id);
            }
            if (id == 431)
            {
                // ホーム区間(デッキ±スロープ6m+2m=41〜79m)の架線柱はホーム外縁+0.5mへ、外側は道床端+0.5mのまま。
                var poles = go.GetComponentsInChildren<Transform>().Where(t => t.name == "pole(Clone)").ToArray();
                Check(poles.Any(t => t.position.z > 41 && t.position.z < 79) && poles.Any(t => t.position.z < 41 || t.position.z > 79), "431 poles around platform");
                foreach (var t in poles)
                {
                    bool zone = t.position.z > 41 && t.position.z < 79;
                    Check(Mathf.Abs(Mathf.Abs(t.position.x) - (zone ? outer + .5f : 4f)) < .02f, $"431 pole offset z={t.position.z} x={t.position.x}");
                }
            }
            UnityEngine.Object.DestroyImmediate(go);
            // 停留所が無いパスにはホームを描かない。
            UseStops();
            go = Generate(id, PathData(id), Straight());
            Check(!go.GetComponentsInChildren<Transform>().Any(t => t.name == "LrtPlatformEnd" || t.name.EndsWith("/lrt_platform")), "no platform without stop " + id);
            UnityEngine.Object.DestroyImmediate(go);
        }
        finally { UseStops(); }
    }
    static void PlatformFallback()
    {
        // lrt_platform が無ければ、内縁〜外縁・上面=踏面+0.30mの簡易スラブで代替する。
        const int id = 242;
        Master(id);
        string key = $"Models/Paths/p{id}/lrt_platform";
        var model = models[key];
        // 空の登録で「モデル無し」にする(前回の実行で書き出したラッパープレハブを Resources から拾わせない)。
        models[key] = null;
        UseStops(Stop(.5f, 22));
        try
        {
            var go = Generate(id, PathData(id), Straight());
            var slabs = go.GetComponentsInChildren<MeshFilter>().Where(f => f.name == "LrtPlatformSlab").ToArray();
            Check(slabs.Length == 2, "fallback slabs");
            float inner = ExpectedOffset(id) + 1.35f;
            foreach (var f in slabs)
            {
                var b = f.GetComponent<Renderer>().bounds;
                float near = Mathf.Min(Mathf.Abs(b.min.x), Mathf.Abs(b.max.x)), far = Mathf.Max(Mathf.Abs(b.min.x), Mathf.Abs(b.max.x));
                Check(Mathf.Abs(near - inner) < .02f && Mathf.Abs(far - (inner + 1.2f)) < .02f, "fallback slab lateral");
                Check(Mathf.Abs(b.max.y - .425f) < .02f && Mathf.Abs(b.min.y + .3f) < .02f && Mathf.Abs(b.min.z - 49) < .1f && Mathf.Abs(b.max.z - 71) < .1f, $"fallback slab extent {b}");
                // 上面の三角形は巻き順から求めた法線が上を向く。
                var mesh = f.sharedMesh; var v = mesh.vertices; var tri = mesh.triangles;
                bool upward = false, inverted = false;
                for (int i = 0; i < tri.Length; i += 3)
                {
                    Vector3 a = v[tri[i]], c = v[tri[i + 2]], n = Vector3.Cross(v[tri[i + 1]] - a, c - a).normalized;
                    if (Mathf.Abs(a.y - .425f) > .01f || Mathf.Abs(c.y - .425f) > .01f || Mathf.Abs(v[tri[i + 1]].y - .425f) > .01f) continue;
                    upward |= n.y > .99f; inverted |= n.y < -.99f;
                }
                Check(upward && !inverted, "fallback slab winding");
            }
            UnityEngine.Object.DestroyImmediate(go);
        }
        finally { models[key] = model; UseStops(); }
    }
    static void ElevatedPlatformWalls()
    {
        // 高架の431はホーム区間(41〜79m)の両側で高欄を省き、区間の外は従来どおり立てる。
        Master(431);
        Set(masters[431], "wall_width", .3f);
        UseStops(Stop(.5f, 22));
        try
        {
            var go = Generate(431, PathData(431), Straight(10f));
            var walls = go.GetComponentsInChildren<MeshRenderer>().Where(r => r.name.EndsWith("/wall")).ToArray();
            Check(walls.Any(w => w.bounds.max.z < 42f) && walls.Any(w => w.bounds.min.z > 78f), "elevated walls outside platform");
            Check(walls.All(w => w.bounds.max.z < 41.5f || w.bounds.min.z > 78.5f), "elevated walls omitted at platform");
            Check(go.GetComponentsInChildren<MeshRenderer>().Count(r => r.name.EndsWith("/lrt_platform")) == 2, "elevated platforms");
            UnityEngine.Object.DestroyImmediate(go);
        }
        finally { UseStops(); Master(431); }
    }
    static void ExportPrefabs()
    {
        foreach (int id in new[] { 222, 242, 303, 304, 305, 431 })
            foreach (string file in Directory.GetFiles("Assets/Paths/p" + id, "*.fbx"))
            {
                string asset = file.Replace('\\', '/');
                string relative = "Paths/p" + id + "/" + Path.GetFileName(file);
                string fbx = root + "/Assets/Models/" + relative;
                string guid = AssetDatabase.AssetPathToGUID(asset);
                if (File.Exists(fbx + ".meta")) Check(File.ReadAllText(fbx + ".meta").Contains("guid: " + guid), "FBX GUID " + relative);
                else File.Copy(asset + ".meta", fbx + ".meta");
                string prefab = "Assets/Resources/Models/" + Path.ChangeExtension(relative, ".prefab");
                Check(ModelPrefabGenerator.CreateWrapperPrefab(asset, prefab), "prefab " + relative);
                string output = root + "/" + prefab;
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                if (File.Exists(output)) Check(File.ReadAllText(output).Contains("guid: " + guid), "prefab GUID " + relative);
                else { File.Copy(prefab, output); File.Copy(prefab + ".meta", output + ".meta"); }
            }
    }
}
