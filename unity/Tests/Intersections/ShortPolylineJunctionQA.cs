using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using GeneratedModels;
using NetTopologySuite.Geometries;
using UnityEngine;

internal static class ShortPolylineJunctionQA
{
    public static int Run()
    {
        var manager = (PathManager)RuntimeHelpers.GetUninitializedObject(typeof(PathManager));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(PathManager).GetField("pathsByNodeIndex", flags).SetValue(manager, new Dictionary<int, HashSet<int>> {
            [183] = new() { 273, 274 }, [186] = new() { 282, 283 }, [188] = new() { 284, 285 },
        });
        var cutLimit = typeof(PathManager).GetMethod("GetIntersectionCutLimit", flags);
        int checks = 0;
        foreach (bool reverse in new[] { false, true })
        {
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/node187.json"));
            var paths = Newtonsoft.Json.JsonConvert.DeserializeObject<List<PathSummary>>(json);
            if (reverse) foreach (var path in paths) Reverse(path);
            var points = paths.ToDictionary(p => p.path_id, IntersectionPolygonUtil.SamplePlan);
            float Limit(PathSummary path) => (float)cutLimit.Invoke(manager, new object[] { path, 187, points[path.path_id] });
            var node = points[273][reverse ? points[273].Count - 1 : 0];
            var result = IntersectionPolygonUtil.ComputeIntersectionPolygon(187, IntersectionPolygonUtil.XZ(node), paths,
                new GeometryFactory(), null, p => points[p.path_id], Limit);
            Check(!result.RequiresMoreSpace, "短い折れ線を接続する交差点を生成できません。");
            Check(result.Sections[273].Distance > 17f, "路地の接続断面が幅24mの本線の内側に置かれています。");
            foreach (var corner in result.Corners.Where(c => c.FromPath == 273 || c.ToPath == 273))
            {
                bool fromWide = corner.ToPath == 273;
                var wide = result.Sections[fromWide ? corner.FromPath : corner.ToPath];
                int end = fromWide ? 0 : corner.Points.Count - 1;
                var straight = corner.Points[fromWide ? 1 : end - 1] - corner.Points[end];
                Check(Math.Abs(Vector2.Dot(straight, wide.Left)) < .002f && straight.magnitude > 1f,
                    "本線の外縁が短い路地へ引き込まれています。");
                checks++;
            }
            var section = result.Sections[273];
            var trimmed = IntersectionPolygonUtil.Trim(points[273], reverse ? (Vector3?)null : section.Position,
                reverse ? section.Position : (Vector3?)null);
            var positions = string.Join(";", trimmed.Select(p => p.x + "," + p.z));
            Check(PathManager.IsAnchorPolyline(paths.Find(p => p.path_id == 273), trimmed),
                $"交差点に最初の経由点を取り込むと、残りの折れ線を識別できません。 reverse={reverse} points={positions}");
            Check(trimmed.Count == 4, "交差点の外側に残る経由点が失われました。");
            checks += 4;
        }
        return checks + CheckSpanMapping();
    }

    private static int CheckSpanMapping()
    {
        var path = Newtonsoft.Json.JsonConvert.DeserializeObject<PathSummary>(
            "{\"curved\":false,\"start_point\":[0,0,0],\"end_point\":[50,0,0],\"waypoints\":[{\"position\":[10,0,0]},{\"position\":[20,5,0]},{\"position\":[30,0,0]},{\"position\":[40,0,0]}]}");
        var plan = IntersectionPolygonUtil.SamplePlan(path);
        var from = IntersectionPolygonUtil.AtDistance(plan, 15f).Position;
        var to = IntersectionPolygonUtil.AtDistance(plan, 35f).Position;
        var trimmed = IntersectionPolygonUtil.Trim(plan, from, to);
        Check(PathManager.IsAnchorPolyline(path, trimmed, out int firstSpan) && firstSpan == 1,
            "両端の経由点を切り取ると、元の高さ指定に対応するスパン番号がずれます。");
        Check(trimmed.Count == 4 && trimmed[1] == plan[2] && trimmed[2] == plan[3],
            "交差点の外に残る経由点の位置または高さが変わりました。");
        from = IntersectionPolygonUtil.AtDistance(plan, 22f).Position;
        to = IntersectionPolygonUtil.AtDistance(plan, 28f).Position;
        trimmed = IntersectionPolygonUtil.Trim(plan, from, to);
        Check(PathManager.IsAnchorPolyline(path, trimmed, out firstSpan) && firstSpan == 2 && trimmed.Count == 2,
            "経由点を含まない残区間が元のスパンに対応しません。");
        trimmed.Reverse();
        Check(!PathManager.IsAnchorPolyline(path, trimmed), "逆走する点列を元の折れ線と判定しました。");
        var subdivided = new List<Vector3> { plan[1], (plan[1] + plan[2]) * .5f, plan[2] };
        Check(!PathManager.IsAnchorPolyline(path, subdivided), "細分化済みの描画点を経由点と判定しました。");
        return 5;
    }

    private static void Reverse(PathSummary path)
    {
        var waypoints = path.GetWaypointList();
        path.waypoints = waypoints.AsEnumerable().Reverse().Select(w => (object)Newtonsoft.Json.Linq.JObject.FromObject(new PathWaypoint {
            position = w.position, control_in = w.control_out, control_out = w.control_in,
        })).ToList();
        (path.start_point, path.end_point) = (path.end_point, path.start_point);
        (path.start_control, path.end_control) = (path.end_control, path.start_control);
        (path.start_node_id, path.end_node_id) = (path.end_node_id, path.start_node_id);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
