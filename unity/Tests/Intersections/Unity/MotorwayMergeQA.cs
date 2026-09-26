using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 本編の生成器と道路FBXで、高速道路の本線とランプの結合描画を検証する。
/// 板を作らない合流ノードとランプの切断位置、結合区間の壁の省略・隙間の舗装・路側線の描き分け、
/// 取り込めない枝が残るノードの本線優先の板の高さを確認し、描画例を出力する。
/// </summary>
public static class MotorwayMergeQA
{
    const int Node = 1, Main = 9100, Ramp = 9102;
    static readonly Dictionary<int, List<Vector3>> samples = new();
    static readonly Dictionary<int, bool> ramps = new();
    static readonly Dictionary<int, float> widths = new();
    static Type pathType, neighborType;
    static List<Vector3> Samples<T>(T p) => samples[Id(p)];
    static float CutLimit<T>(T p) => 1000f;
    static TResult Neighbors<T, TResult>(T p) => (TResult)BuildNeighbors(p);
    static int Id(object p) => (int)pathType.GetField("path_id").GetValue(p);
    static void Set(object target, string field, object value) => target.GetType().GetField(field).SetValue(target, value);
    static object Get(object target, string property) => target.GetType().GetProperty(property).GetValue(target);
    static void Check(bool value, string message) { if (!value) throw new Exception("MotorwayMerge: " + message); }

    static object BuildNeighbors(object path)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(neighborType));
        int id = Id(path);
        foreach (var pair in samples)
        {
            if (pair.Key == id || ramps[pair.Key] == ramps[id]) continue;
            var info = Activator.CreateInstance(neighborType);
            Set(info, "PathId", pair.Key); Set(info, "Points", pair.Value); Set(info, "Width", widths[pair.Key]);
            list.Add(info);
        }
        return list;
    }

    public static int Run(Assembly asm, IDictionary masters, IDictionary models, string root)
    {
        pathType = asm.GetType("GeneratedModels.PathSummary");
        neighborType = asm.GetType("RoadNeighborInfo");
        Type itemType = asm.GetType("PathMasterItem"), constants = asm.GetType("Constants");
        int C(string name) => (int)constants.GetField(name).GetValue(null);
        foreach (int variant in new[] { Main, Ramp })
        {
            var master = Activator.CreateInstance(itemType);
            bool ramp = variant == Ramp;
            Set(master, "width", ramp ? 5.5f : 14f); Set(master, "lanes", ramp ? 1 : 4);
            Set(master, "sidewalk_width", 0f); Set(master, "side_width", .5f); Set(master, "wall_width", .2f);
            Set(master, "center_width", ramp ? 0f : 1f); Set(master, "follow_terrain", false); Set(master, "height_editable", true);
            Set(master, "ramp", ramp); Set(master, "body_material", "PATH_ASPHALT");
            masters[variant] = master;
            foreach (string part in new[] { "body", "wall", "line", "center" })
                models["Models/Paths/p" + variant + "/" + part] = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Paths/p1/" + part + ".fbx")
                    ?? (GameObject)models["Models/Paths/p100/" + part];
        }
        var bodyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Paths/p1/body.fbx");
        foreach (var material in bodyPrefab.GetComponent<MeshRenderer>().sharedMaterials)
            asm.GetType("RoadPath").GetMethod("GetOrCreateRoadMaterial").Invoke(null, new object[] { material });
        var provider = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(pathType, typeof(List<Vector3>)),
            typeof(MotorwayMergeQA).GetMethod("Samples", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(pathType));
        var limit = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(pathType, typeof(float)),
            typeof(MotorwayMergeQA).GetMethod("CutLimit", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(pathType));
        var generator = Activator.CreateInstance(asm.GetType("RoadIntersectionMeshGenerator"));
        var generate = generator.GetType().GetMethod("GenerateWithSections");
        object Junction(IList paths, Vector3 node) => generate.Invoke(generator, new object[] { Node, node, paths, 12f, provider, limit });
        object MakePath(int id, int variant, int start, int end, List<Vector3> points, int direction)
        {
            var path = Activator.CreateInstance(pathType);
            Set(path, "path_id", id); Set(path, "path_variant", variant); Set(path, "path_type", C("PATH_MOTORWAY"));
            Set(path, "status", C("STATUS_ACTIVE")); Set(path, "start_node_id", start); Set(path, "end_node_id", end);
            Set(path, "travel_direction", direction); Set(path, "curved", true); Set(path, "follow_terrain", C("VALUE_NONE"));
            Set(path, "width", variant == Ramp ? 5.5f : 14f);
            Set(path, "start_point", new List<object> { points[0].x, points[0].y, points[0].z });
            Set(path, "end_point", new List<object> { points[points.Count - 1].x, points[points.Count - 1].y, points[points.Count - 1].z });
            samples[id] = points; ramps[id] = variant == Ramp; widths[id] = variant == Ramp ? 5.5f : 14f;
            return path;
        }
        IList NewPaths() => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pathType));
        int checks = 0;

        // 一直線の本線と浅い角度のランプ: 板を作らず、本線はノードまで、ランプは本線の縁の内側へ入りきる位置で切る。
        foreach (float angle in new[] { 12f, 25f })
        foreach (bool reversed in new[] { false, true })
        foreach (bool bothSides in new[] { false, true })
        {
            samples.Clear(); ramps.Clear(); widths.Clear();
            var node = new Vector3(0, 12, 0);
            var paths = NewPaths();
            paths.Add(MakePath(301, Main, 101, Node, Line(new Vector3(-150, 12, 0), node), C("DIR_FORWARD")));
            paths.Add(MakePath(302, Main, Node, 102, Line(node, new Vector3(150, 12, 0)), C("DIR_FORWARD")));
            Vector3 far = new Vector3(-150 * Mathf.Cos(angle * Mathf.Deg2Rad), 12, -150 * Mathf.Sin(angle * Mathf.Deg2Rad));
            paths.Add(reversed ? MakePath(303, Ramp, Node, 103, Line(node, far), C("DIR_BACKWARD"))
                : MakePath(303, Ramp, 103, Node, Line(far, node), C("DIR_FORWARD")));
            if (bothSides) paths.Add(MakePath(304, Ramp, Node, 104, Line(node, new Vector3(150 * Mathf.Cos(angle * Mathf.Deg2Rad), 12, 150 * Mathf.Sin(angle * Mathf.Deg2Rad))), C("DIR_FORWARD")));
            var result = Junction(paths, node);
            Check(result != null && Get(result, "Mesh") == null, "取り込めるランプしか無い合流ノードに板が作られました");
            var layout = Get(result, "MotorwayMerge");
            Check(layout != null && !(bool)Get(layout, "NeedsSlab"), "合流計画がありません");
            var cuts = (IDictionary)Get(result, "ConnectionPoints");
            Check(Vector3.Distance((Vector3)cuts[301], node) < .001f && Vector3.Distance((Vector3)cuts[302], node) < .001f, "本線がノードで切られました");
            float expected = (7f - 2.75f) / Mathf.Sin(angle * Mathf.Deg2Rad);
            foreach (int id in bothSides ? new[] { 303, 304 } : new[] { 303 })
            {
                Vector3 cut = (Vector3)cuts[id];
                Check(Mathf.Abs(new Vector2(cut.x, cut.z).magnitude - expected) < .05f, "ランプの切断位置が本線の縁と一致しません");
            }
            foreach (bool rightHand in new[] { false, true })
            {
                int hand = C(rightHand ? "TRAFFIC_HAND_RIGHT" : "TRAFFIC_HAND_LEFT");
                int target = (int)layout.GetType().GetMethod("TargetLane").Invoke(layout, new object[] { 303, 302, 1, hand });
                Check(target == (rightHand ? 0 : 3), "ランプから本線の離れた車線へ入ります");
            }
            checks++;
        }

        // 角度の大きい枝が残るノードは板を作る。板の上の本線部分は、勾配のある本線の縦断そのものの高さ。
        {
            samples.Clear(); ramps.Clear(); widths.Clear();
            var node = new Vector3(0, 12, 0);
            var paths = NewPaths();
            paths.Add(MakePath(301, Main, 101, Node, Line(new Vector3(-150, 12 - 150 * .05f, 0), node), C("DIR_BOTH")));
            paths.Add(MakePath(302, Main, Node, 102, Line(node, new Vector3(150, 12 + 150 * .05f, 0)), C("DIR_BOTH")));
            paths.Add(MakePath(303, Ramp, Node, 103, Line(node, new Vector3(-75, 12, -130)), C("DIR_BOTH")));
            var result = Junction(paths, node);
            var mesh = (Mesh)Get(result, "Mesh");
            Check(mesh != null && Get(result, "MotorwayMerge") != null, "角度の大きいランプの交差点に板がありません");
            // 上面の頂点(UV3.z=1)のうち本線の路面の縁の上にあるものは、本線の縦断そのものの高さになる。
            var top = new List<Vector4>(); mesh.GetUVs(3, top);
            var vertices = mesh.vertices;
            float worst = 0; int measured = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                if (top.Count != vertices.Length || top[i].z < .5f) continue;
                Vector3 world = vertices[i] + node;
                if (Mathf.Abs(world.z) > 7.001f) continue;
                measured++;
                worst = Mathf.Max(worst, Mathf.Abs(world.y - (12 + .05f * world.x + .1f)));
            }
            Check(measured > 8 && worst < .002f, "板の本線部分の高さが本線の縦断から外れます: " + worst + " (" + measured + "点)");
            UnityEngine.Object.DestroyImmediate(mesh);
            checks++;
        }

        // 並走してから合流するランプ: 本線の壁を外して隙間を舗装し、路側線を合流車線の点線にする。
        {
            samples.Clear(); ramps.Clear(); widths.Clear();
            var node = new Vector3(0, 12, 0);
            var rampPoints = new List<Vector3>();
            for (int x = -260; x <= 0; x++)
            {
                float z = x < -150 ? -10.25f - (-150 - x) * .3f : x < -60 ? -10.25f : -10.25f * -x / 60f;
                rampPoints.Add(new Vector3(x, x < -150 ? 12 - (-150 - x) * .05f : 12, z));
            }
            var paths = NewPaths();
            paths.Add(MakePath(401, Main, 101, Node, Line(new Vector3(-300, 12, 0), node), C("DIR_FORWARD")));
            paths.Add(MakePath(402, Main, Node, 102, Line(node, new Vector3(150, 12, 0)), C("DIR_FORWARD")));
            paths.Add(MakePath(403, Ramp, 103, Node, rampPoints, C("DIR_FORWARD")));
            var adjacency = asm.GetType("RoadAdjacency");
            var neighborsField = adjacency.GetField("NeighborProvider");
            var listType = typeof(List<>).MakeGenericType(neighborType);
            neighborsField.SetValue(null, Delegate.CreateDelegate(neighborsField.FieldType,
                typeof(MotorwayMergeQA).GetMethod("Neighbors", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(pathType, listType)));
            try
            {
                // 描画点列: ランプは本線と結合する区間を本線の高さへ寄せる(PathManager.ComputeUntrimmedPathPoints と同じ)。
                var mainNeighbors = BuildNeighbors(paths[2]);
                var corridor = adjacency.GetMethod("Compute").Invoke(null, new object[] { rampPoints, 5.5f, .2f, true, mainNeighbors });
                var lifted = (List<Vector3>)adjacency.GetMethod("ApplyRampHeights").Invoke(null, new object[] { rampPoints, corridor });
                Check(Mathf.Abs(lifted[200].y - (12 - .02f)) < .001f, "並走区間のランプが本線の高さへ寄りません");
                Check(Mathf.Abs(lifted[0].y - rampPoints[0].y) < .001f, "ランプの遠い端の高さが変わりました");
                samples[403] = lifted;
                var result = Junction(paths, node);
                Check(result != null && Get(result, "Mesh") == null, "並走して合流するランプのノードに板が作られました");
                var cuts = (IDictionary)Get(result, "ConnectionPoints");
                Vector3 cut = (Vector3)cuts[403];
                Check(Mathf.Abs(cut.x - (-60f * 4.25f / 10.25f)) < .3f, "並走ランプの切断位置が本線の縁と一致しません: " + cut.x);
                var trim = asm.GetType("IntersectionPolygonUtil").GetMethod("Trim");
                var road = Activator.CreateInstance(asm.GetType("RoadPath"));
                var setback = Activator.CreateInstance(asm.GetType("SidewalkSetback"));
                var preview = new GameObject("MotorwayMergePreview");
                var objects = new Dictionary<int, GameObject>();
                foreach (var path in paths)
                {
                    int id = Id(path);
                    var points = id == 403 ? (List<Vector3>)trim.Invoke(null, new object[] { samples[403], null, cut }) : samples[id];
                    var generated = road.GetType().GetMethod("Generate").Invoke(road, new object[] { path, points, setback, setback });
                    var go = (GameObject)Get(generated, "GeneratedObject");
                    go.transform.SetParent(preview.transform, false);
                    objects[id] = go;
                }
                // 本線の右側(ランプ側)の壁は並走・合流区間で消え、ランプの路面が本線の縁の内側へ入りきった先で戻る。
                var walls = PartVertices(objects[401], "/wall");
                Check(walls.Any(v => v.z > 6.9f && v.x < -250) && walls.Any(v => v.z > 6.9f && v.x > -60), "ランプと反対側の壁が欠けました");
                Check(!walls.Any(v => v.z < -6.9f && v.x > -140 && v.x < -30), "ランプとの間に本線の壁が残っています");
                Check(walls.Any(v => v.z < -6.9f && v.x > -20) && walls.Any(v => v.z < -6.9f && v.x < -170), "合流区間の外の本線の壁が消えました");
                var rampWalls = PartVertices(objects[403], "/wall");
                Check(!rampWalls.Any(v => v.z > -9f && v.x > -140 && v.x < -65), "本線との間にランプの壁が残っています");
                Check(rampWalls.Any(v => v.z < -12.9f && v.x > -140 && v.x < -65), "ランプの外側の壁が欠けました");
                var filler = objects[401].GetComponentsInChildren<MeshFilter>().Where(f => f.name.StartsWith("RoadMergeFiller")).ToList();
                Check(filler.Count == 1, "本線とランプの隙間が舗装されません");
                var fillerVertices = filler[0].sharedMesh.vertices.Select(v => filler[0].transform.TransformPoint(v)).ToList();
                Check(fillerVertices.Min(v => v.z) < -7.4f && fillerVertices.Max(v => v.z) > -7.2f && fillerVertices.All(v => v.y < 12.1f - .03f),
                    "隙間の舗装が両側の路面の下へ潜っていません");
                // 路側線: 本線のランプ側は結合区間だけ点線、ランプの本線側は結合区間で描かない。
                var mainBands = Bands(asm, objects[401]);
                Check(mainBands.Any(b => Mathf.Abs(b.band.x - 6.75f) < .01f && b.band.z > .5f && b.span.x < 160 && b.span.y > 260),
                    "本線の路側線が合流区間で点線になりません");
                Check(mainBands.Any(b => Mathf.Abs(b.band.x - 6.75f) < .01f && b.band.z < .5f && b.span.x > 260),
                    "合流区間の先で本線の路側線が実線に戻りません");
                Check(mainBands.Any(b => Mathf.Abs(b.band.x + 6.75f) < .01f && b.band.z < .5f && b.span.x < 0 && b.span.y > 300),
                    "ランプと反対側の路側線が変わりました");
                var rampBands = Bands(asm, objects[403]);
                Check(!rampBands.Any(b => Mathf.Abs(b.band.x + 2.5f) < .01f && b.span.y > 150 && b.span.x < 250), "ランプの本線側の路側線が残っています");
                Check(rampBands.Any(b => Mathf.Abs(b.band.x - 2.5f) < .01f && b.span.x < 0 && b.span.y > 300), "ランプの外側の路側線が欠けました");
                checks += 11;
                Render(preview, root);
                UnityEngine.Object.DestroyImmediate(preview);
            }
            finally { neighborsField.SetValue(null, null); }
        }
        return checks;
    }

    static List<Vector3> Line(Vector3 from, Vector3 to)
    {
        int count = Mathf.CeilToInt(Vector3.Distance(from, to));
        var points = new List<Vector3>();
        for (int i = 0; i <= count; i++) points.Add(Vector3.Lerp(from, to, (float)i / count));
        return points;
    }

    static List<Vector3> PartVertices(GameObject root, string suffix)
    {
        var result = new List<Vector3>();
        foreach (var filter in root.GetComponentsInChildren<MeshFilter>())
        {
            if (!filter.gameObject.name.EndsWith(suffix) || filter.sharedMesh == null) continue;
            foreach (var v in filter.sharedMesh.vertices) result.Add(filter.transform.TransformPoint(v));
        }
        return result;
    }

    static List<(Vector4 band, Vector4 span)> Bands(Assembly asm, GameObject root)
    {
        var context = root.GetComponent(asm.GetType("RoadMarkingContext"));
        Check(context != null, "路面標示の距離座標がありません");
        var bands = (Vector4[])Get(context, "Bands");
        var spans = (Vector4[])Get(context, "BandSpans");
        int count = (int)Get(context, "BandCount");
        var result = new List<(Vector4, Vector4)>();
        for (int i = 0; i < count; i++) result.Add((bands[i], spans[i]));
        return result;
    }

    static void Render(GameObject preview, string root)
    {
        preview.transform.position = new Vector3(3000, 0, 3000);
        var camera = new GameObject("MergeCamera").AddComponent<Camera>();
        camera.orthographic = true; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.16f, .2f, .23f);
        var light = new GameObject("MergeLight").AddComponent<Light>();
        light.type = LightType.Directional; light.transform.rotation = Quaternion.Euler(55, -25, 0);
        var center = preview.transform.position + new Vector3(-70, 12, -5);
        foreach (bool top in new[] { true, false })
        {
            camera.orthographicSize = top ? 45 : 30;
            camera.transform.position = center + (top ? new Vector3(0, 100, 0) : new Vector3(-60, 45, -70));
            camera.transform.rotation = top ? Quaternion.Euler(90, 0, 0) : Quaternion.LookRotation(center - camera.transform.position);
            var texture = new RenderTexture(1600, 800, 24); camera.targetTexture = texture; camera.Render(); RenderTexture.active = texture;
            var image = new Texture2D(1600, 800, TextureFormat.RGB24, false); image.ReadPixels(new Rect(0, 0, 1600, 800), 0, 0); image.Apply();
            File.WriteAllBytes(root + "/Temp/IntersectionUnityQA/merge-" + (top ? "top" : "perspective") + ".png", image.EncodeToPNG());
            camera.targetTexture = null; RenderTexture.active = null; UnityEngine.Object.DestroyImmediate(image); texture.Release(); UnityEngine.Object.DestroyImmediate(texture);
        }
        UnityEngine.Object.DestroyImmediate(camera.gameObject);
        UnityEngine.Object.DestroyImmediate(light.gameObject);
    }
}
