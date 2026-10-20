using System;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

internal static class RoadContinuousJoinQA
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static int Run()
    {
        int checks = 0;
        foreach (int type in new[] { Constants.PATH_STREET, Constants.PATH_MOTORWAY })
        foreach (bool aStart in new[] { false, true })
        foreach (bool bStart in new[] { false, true })
        foreach (bool reversed in new[] { false, true })
        {
            var node = new Vector3(-3300f, 4f, -370f);
            var a = Arm(65, type, aStart);
            var b = Arm(209, type, bStart);
            var paths = new List<PathSummary> { a, b };
            if (reversed) paths.Reverse();
            var points = new Dictionary<int, List<Vector3>> {
                [65] = Line(node, node + new Vector3(0f, 6f, 630f), aStart),
                [209] = Line(node, node + new Vector3(0f, -2f, -150f), bStart),
            };
            List<Vector3> Resolve(PathSummary path) => points[path.path_id];
            var result = NodeMeshFactory.GenerateNodeMesh(184, node, paths, 0f, Resolve);
            Check(result != null && result.RoadJoin != null && result.Mesh == null,
                "同種・同幅の道路の直進接続に交差点メッシュが作られました。");
            Check(result.ConnectionPoints.Count == 2 && result.ConnectionPoints[65] == node && result.ConnectionPoints[209] == node,
                "直進接続の道路端がノードより手前で切られました。");
            Check(points[65][aStart ? 0 : 1] == node && points[209][bStart ? 0 : 1] == node,
                "直進接続の判定が入力点列を書き換えました。");
            checks += 3;

            paths.Add(new PathSummary { path_id = 300, path_type = Constants.PATH_TRANSPORT,
                path_variant = 1001, width = 0f, start_node_id = 184, end_node_id = 301 });
            var withTransit = NodeMeshFactory.GenerateNodeMesh(184, node, paths, 0f, Resolve);
            Check(withTransit?.RoadJoin != null && withTransit.Mesh == null && withTransit.ConnectionPoints.Count == 2,
                "公共交通の論理パスによって道路の直進接続が変わりました。");
            checks++;
        }

        var first = Arm(65, Constants.PATH_STREET, false);
        var second = Arm(209, Constants.PATH_STREET, true);
        var pair = new List<PathSummary> { first, second };
        var plans = new Dictionary<int, List<Vector3>> {
            [65] = Line(Vector3.zero, Vector3.forward * 40f, false),
            [209] = Line(Vector3.zero, Vector3.back * 40f, true),
        };
        RoadContinuousJoin Join() => RoadIntersectionMeshGenerator.TryContinuousRoadJoin(184, pair,
            p => plans[p.path_id], out _);
        Check(Join() != null, "短い道路の直進接続が拒否されました。");
        second.path_variant++;
        Check(Join() == null, "異なるバリアントの道路を直接つなぎました。");
        second.path_variant--;
        second.width = 10f;
        Check(Join() == null, "幅の違う道路を直接つなぎました。");
        second.width = 12f;
        second.path_type = Constants.PATH_MOTORWAY;
        Check(Join() == null, "異なる種類の道路を直接つなぎました。");
        second.path_type = Constants.PATH_STREET;
        pair.Add(Arm(210, Constants.PATH_STREET, true));
        Check(Join() == null, "分岐のある交差点を省略しました。");
        pair.RemoveAt(2);
        checks += 5;

        foreach (var line in new[] {
            new List<Vector3> { Vector3.zero, new Vector3(1f, 0f, -40f) },
            new List<Vector3> { Vector3.zero, Vector3.forward * 40f },
            new List<Vector3> { Vector3.up, new Vector3(0f, 1f, -40f) },
            new List<Vector3> { Vector3.zero, Vector3.zero },
        })
        {
            plans[209] = line;
            Check(Join() == null, "折れ角・向き・位置の不一致、または長さのない道路を直接つなぎました。");
            checks++;
        }
        return checks;
    }

    private static PathSummary Arm(int id, int type, bool start) => new PathSummary {
        path_id = id, path_type = type, path_variant = 221, width = 12f,
        start_node_id = start ? 184 : id, end_node_id = start ? id : 184,
        status = Constants.STATUS_ACTIVE,
    };

    private static List<Vector3> Line(Vector3 node, Vector3 outer, bool start)
        => start ? new List<Vector3> { node, outer } : new List<Vector3> { outer, node };
}
