using System;
using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// 道路ノードが「幹線に細い脇道だけが繋がる本線優先ノード」かを判定する。
///
/// コア(junction_speed.py)と同じ妨害率 p = W_cross / (W_main + W_cross) を使い、
/// p が Constants.JUNCTION_MINOR_MAX_IMPEDANCE 未満の街路のみのノードを非交差点扱いとする。
/// 描画演出(信号機・本線の横断歩道/停止線/矢印・中央分離帯と車線の貫通)の0/1判定を1か所に集約する。
/// </summary>
public static class RoadJunctionPriority
{
    public readonly struct MinorJunctionMain
    {
        public readonly PathSummary A;
        public readonly PathSummary B;
        public readonly float Impedance;

        public MinorJunctionMain(PathSummary a, PathSummary b, float impedance)
        {
            A = a;
            B = b;
            Impedance = impedance;
        }

        public bool IsMain(int pathId) => A != null && B != null && (A.path_id == pathId || B.path_id == pathId);
    }

    private static bool IsFlowType(int pathType)
        => pathType == Constants.PATH_STREET
            || pathType == Constants.PATH_MOTORWAY
            || pathType == Constants.PATH_TRACK
            || pathType == Constants.PATH_MONORAIL;

    private static float ResolveWidth(PathSummary path)
    {
        if (path.width > 0f) return path.width;
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(path.path_variant) : null;
        return master != null && master.width > 0f ? master.width : 4.0f;
    }

    private static bool CanPass(PathSummary a, PathSummary b, int nodeId)
    {
        bool aEnters = VehicleManager.DirectionAllowsEntry(a.travel_direction, a.end_node_id == nodeId);
        bool aLeaves = VehicleManager.DirectionAllowsEntry(a.travel_direction, a.start_node_id == nodeId);
        bool bEnters = VehicleManager.DirectionAllowsEntry(b.travel_direction, b.end_node_id == nodeId);
        bool bLeaves = VehicleManager.DirectionAllowsEntry(b.travel_direction, b.start_node_id == nodeId);
        return (aEnters && bLeaves) || (bEnters && aLeaves);
    }

    /// <summary>
    /// 稼働中の流れが3本以上かつ街路のみで構成され、直進継続する本線ペア(A, B)に対する
    /// 対立流の幅の割合(妨害率 p)が閾値未満なら true を返し、本線ペアを出力する。
    /// 線路・モノレール(踏切)や高速道路/ランプを含むノードは常に false(通常交差点)とする。
    /// </summary>
    public static bool TryResolve(int nodeId, IReadOnlyList<PathSummary> paths,
        Func<PathSummary, Vector2> outwardResolver, out MinorJunctionMain main)
    {
        main = default;
        if (nodeId <= 0 || paths == null || outwardResolver == null) return false;

        var flows = new List<PathSummary>();
        var widths = new List<float>();
        var directions = new List<Vector2>();
        float totalWidth = 0f;

        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            if (path == null || path.status != Constants.STATUS_ACTIVE || !IsFlowType(path.path_type)) continue;
            bool start = path.start_node_id == nodeId, end = path.end_node_id == nodeId;
            if (start == end) continue;
            // 踏切(軌道)や高速道路/ランプを含むノードは、幅差にかかわらず通常の交差点として扱う。
            if (path.path_type != Constants.PATH_STREET) return false;
            Vector2 dir = outwardResolver(path);
            if (dir.sqrMagnitude < 1e-6f) return false;
            dir.Normalize();
            float width = ResolveWidth(path);
            flows.Add(path);
            widths.Add(width);
            directions.Add(dir);
            totalWidth += width;
        }

        if (flows.Count < 3 || totalWidth <= 0f) return false;

        float maxDot = -Mathf.Cos(Constants.JUNCTION_MAIN_MAX_DEFLECTION_DEG * Mathf.Deg2Rad);
        int bestI = -1, bestJ = -1;
        float bestWidth = -1f, bestDot = float.PositiveInfinity;

        for (int i = 0; i < flows.Count; i++)
        {
            for (int j = i + 1; j < flows.Count; j++)
            {
                if (!CanPass(flows[i], flows[j], nodeId)) continue;
                float dot = Vector2.Dot(directions[i], directions[j]);
                if (dot > maxDot) continue;
                float pairWidth = widths[i] + widths[j];
                bool better = pairWidth > bestWidth + 1e-4f
                    || (Mathf.Abs(pairWidth - bestWidth) <= 1e-4f && dot < bestDot - 1e-5f)
                    || (Mathf.Abs(pairWidth - bestWidth) <= 1e-4f && Mathf.Abs(dot - bestDot) <= 1e-5f
                        && ComparePairIds(flows[i].path_id, flows[j].path_id, flows[bestI].path_id, flows[bestJ].path_id) < 0);
                if (better)
                {
                    bestI = i;
                    bestJ = j;
                    bestWidth = pairWidth;
                    bestDot = dot;
                }
            }
        }

        if (bestI < 0) return false;

        float minMainWidth = Mathf.Min(widths[bestI], widths[bestJ]);
        for (int k = 0; k < flows.Count; k++)
        {
            if (k == bestI || k == bestJ) continue;
            if (widths[k] >= minMainWidth) return false;
        }

        float impedance = (totalWidth - bestWidth) / totalWidth;
        if (impedance >= Constants.JUNCTION_MINOR_MAX_IMPEDANCE) return false;

        var a = flows[bestI];
        var b = flows[bestJ];
        if (a.path_id > b.path_id) (a, b) = (b, a);
        main = new MinorJunctionMain(a, b, impedance);
        return true;
    }

    private static int ComparePairIds(int a1, int b1, int a2, int b2)
    {
        if (a1 > b1) (a1, b1) = (b1, a1);
        if (a2 > b2) (a2, b2) = (b2, a2);
        int c = a1.CompareTo(a2);
        return c != 0 ? c : b1.CompareTo(b2);
    }
}
