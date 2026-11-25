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
        checks += CheckMinorJunctionPriority();
        return checks;
    }

    private static int CheckMinorJunctionPriority()
    {
        int checks = 0;
        PathSummary Street(int id, float width, float deg, int dir = Constants.DIR_BOTH)
        {
            float rad = deg * MathF.PI / 180f;
            return new PathSummary {
                path_id = id, path_type = Constants.PATH_STREET, path_variant = width >= 24f ? 231 : width >= 12f ? 221 : width >= 6f ? 212 : 201,
                width = width, status = Constants.STATUS_ACTIVE, travel_direction = dir,
                start_node_id = 1, end_node_id = 100 + id,
                start_point = new List<object> { 0f, 0f, 0f },
                end_point = new List<object> { MathF.Cos(rad) * 60f, 0f, MathF.Sin(rad) * 60f },
            };
        }
        Vector2 Dir(PathSummary p) => RoadMarkingRules.Outward(p, 1);

        // 4車線(24m)本線＋未舗装(3m)T字: p = 3/51 < 0.15 → 本線優先(非交差点)
        var arterialMinorT = new List<PathSummary> { Street(1, 24f, 0f), Street(2, 24f, 180f), Street(3, 3f, 90f) };
        Check(RoadJunctionPriority.TryResolve(1, arterialMinorT, Dir, out var main) && main.IsMain(1) && main.IsMain(2) && !main.IsMain(3),
            "4車線＋未舗装T字が本線優先になりません。");
        // 本線側だけアプローチ標示を消し、脇道側は残す
        var prevConnected = RoadMarkingRules.ConnectedPaths;
        RoadMarkingRules.ConnectedPaths = _ => arterialMinorT;
        try
        {
            Check(RoadMarkingRules.Available(arterialMinorT[0], 1) == RoadMarkingTurns.None, "本線優先ノードの本線にアプローチ標示が残りました。");
            Check(RoadMarkingRules.Available(arterialMinorT[2], 1) != RoadMarkingTurns.None, "本線優先ノードの脇道のアプローチ標示が消えました。");
        }
        finally { RoadMarkingRules.ConnectedPaths = prevConnected; }
        checks += 3;

        // 4車線(24m)本線＋1車線(6m)T字(p=6/54=0.111)は非交差点、十字(p=12/60=0.20)は通常交差点
        var arterialOneLaneT = new List<PathSummary> { Street(1, 24f, 0f), Street(2, 24f, 180f), Street(3, 6f, 90f) };
        Check(RoadJunctionPriority.TryResolve(1, arterialOneLaneT, Dir, out _), "4車線＋1車線T字が本線優先になりません。");
        var arterialOneLaneCross = new List<PathSummary> { Street(1, 24f, 0f), Street(2, 24f, 180f), Street(3, 6f, 90f), Street(4, 6f, 270f) };
        Check(!RoadJunctionPriority.TryResolve(1, arterialOneLaneCross, Dir, out _), "4車線＋1車線十字が通常交差点になりません。");
        checks += 2;

        // 1車線(6m)＋極小路地(3m)T字(p=3/15=0.20)と4車線＋2車線T字(p=12/60=0.20)は通常交差点
        var oneLaneAlley = new List<PathSummary> { Street(1, 6f, 0f), Street(2, 6f, 180f), Street(3, 3f, 90f) };
        Check(!RoadJunctionPriority.TryResolve(1, oneLaneAlley, Dir, out _), "1車線＋極小路地T字が通常交差点になりません。");
        var arterialTwoLane = new List<PathSummary> { Street(1, 24f, 0f), Street(2, 24f, 180f), Street(3, 12f, 90f) };
        Check(!RoadJunctionPriority.TryResolve(1, arterialTwoLane, Dir, out _), "4車線＋2車線T字が通常交差点になりません。");
        checks += 2;

        // 踏切(線路)や高速ランプが混ざれば幅比にかかわらず通常交差点
        var railArm = Street(3, 3f, 90f); railArm.path_type = Constants.PATH_TRACK;
        Check(!RoadJunctionPriority.TryResolve(1, new List<PathSummary> { Street(1, 24f, 0f), Street(2, 24f, 180f), railArm }, Dir, out _),
            "踏切が本線優先として扱われました。");
        var rampArm = Street(3, 5.5f, 90f); rampArm.path_type = Constants.PATH_MOTORWAY;
        Check(!RoadJunctionPriority.TryResolve(1, new List<PathSummary> { Street(1, 24f, 0f), Street(2, 24f, 180f), rampArm }, Dir, out _),
            "高速ランプ接続が本線優先として扱われました。");
        checks += 2;
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
