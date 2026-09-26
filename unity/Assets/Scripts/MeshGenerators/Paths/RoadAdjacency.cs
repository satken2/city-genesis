using System;
using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>結合判定に渡す隣接道路1本分。PathManager が RoadAdjacency.NeighborProvider 経由で渡す。</summary>
public sealed class RoadNeighborInfo
{
    public int PathId;

    /// <summary>切断前の描画点列(ワールド座標)。ランプは本線へ寄せた後の高さを持つ。</summary>
    public List<Vector3> Points;

    public float Width;
}

/// <summary>ノードで本線が交差点の板を挟まずに次の本線へ続く端の扱い。</summary>
public struct RoadEndJoin
{
    /// <summary>本線の続きへ直接つながる。中央分離帯の端を丸めない。</summary>
    public bool Continuous;

    /// <summary>つなぎ目の橋脚を立てる側。つながる本線の2本のうち片方だけが真になる。</summary>
    public bool OwnsSupport;
}

/// <summary>
/// 自道路の点列に沿った、高速道路の本線とランプの結合区間。添字は点列のインデックス。
/// 左右は点列の進行方向に対する水平の向き(right = Cross(up, forward))。
/// </summary>
public sealed class RoadCorridorInfo
{
    public List<Vector3> Points;
    public Vector3[] RightDir;
    public float[] CumulativeLength;
    public float TotalLength;

    /// <summary>相手の路面が隣り合う・重なる側。壁を立てず、路側線を描き分ける。</summary>
    public bool[] LeftMerged;
    public bool[] RightMerged;

    /// <summary>本線だけが持つ、隙間を舗装する外端(自中心線から相手の路面の縁までの距離)。NaN は埋めない。</summary>
    public float[] LeftFillTo;
    public float[] RightFillTo;

    /// <summary>壁の位置に相手の路面が掛かる点。</summary>
    public bool[] LeftWallBlocked;
    public bool[] RightWallBlocked;

    /// <summary>ランプだけが持つ、結合した本線の路面の高さ(スプライン点の高さ)。NaN は結合していない。</summary>
    public float[] TargetY;

    /// <summary>指定側に壁を立ててよい区間(スプライン percent)。</summary>
    public List<TrackCorridorInfo.PercentRange> GetWallPercentIntervals(bool left, float minRunLength)
    {
        bool[] merged = left ? LeftMerged : RightMerged;
        bool[] blocked = left ? LeftWallBlocked : RightWallBlocked;
        return TrackCorridorInfo.ClearPercentIntervals(CumulativeLength, TotalLength, i => merged[i] || blocked[i], minRunLength);
    }

    /// <summary>指定側で相手と結合している区間(スプライン percent)。left が null なら左右どちらか。</summary>
    public List<TrackCorridorInfo.PercentRange> GetMergedPercentRanges(bool? left)
    {
        if (TotalLength <= 0f) return new List<TrackCorridorInfo.PercentRange>();
        return TrackCorridorInfo.ClearPercentIntervals(CumulativeLength, TotalLength,
            i => !(left != false && LeftMerged[i]) && !(left != true && RightMerged[i]), 0f);
    }
}

/// <summary>
/// 高速道路の本線(ランプでない高速道路)とランプの結合描画の判定。近接して並走・合流する本線とランプを
/// 一つの路面として描くため、間の壁と路側線を省き、隙間を舗装し、ランプを本線の高さへ寄せる。
/// 高さはランプだけを動かし、本線は自分の縦断を保つ。描画専用で、バックエンドの形状・判定には影響しない。
/// 一般道路は対象外。
/// </summary>
public static class RoadAdjacency
{
    /// <summary>相手側(ランプなら本線、本線ならランプ)の稼働中の近接候補。PathManager.Awake が設定する。</summary>
    public static Func<PathSummary, List<RoadNeighborInfo>> NeighborProvider;

    /// <summary>ノードで本線が交差点の板を挟まずに続く端か。PathManager が交差点の計算結果から答える。</summary>
    public static Func<PathSummary, bool, RoadEndJoin> EndJoinProvider;

    private const float LONGITUDINAL_TOLERANCE = 2.5f; // 最近接点が相手の端点へ張り付いた虚像を除く、進行方向成分の許容量(m)
    private const float WALL_BLOCK_CLEARANCE = 0.3f; // 相手の路面が壁に接して見えないよう壁の外側へ取る余裕(m)
    private const float FILL_OVERLAP = 0.3f; // 路面が重なり始めても隙間の舗装を続ける重なり幅(m)

    public static bool IsMotorway(PathSummary path) => path != null && path.path_type == Constants.PATH_MOTORWAY;

    public static bool IsRamp(PathSummary path, Func<int, PathMasterItem> masters = null)
    {
        if (!IsMotorway(path)) return false;
        var master = masters != null ? masters(path.path_variant) : MasterDataStore.Instance?.GetPathMaster(path.path_variant);
        return master != null && master.ramp;
    }

    public static RoadEndJoin EndJoin(PathSummary path, bool start)
        => EndJoinProvider != null && path != null ? EndJoinProvider(path, start) : default;

    /// <summary>
    /// 自道路の点列に沿った結合区間を求める。結合が1点も無ければ null。
    /// 相手の路面の縁との隙間が ROAD_MERGE_MAX_EDGE_GAP 以内で、向きの差が ROAD_MERGE_MAX_ANGLE_DEG 以内の点を結合する。
    /// 高さの差は、路面が重なる点では ROAD_MERGE_OVERLAP_MAX_DY、隣り合うだけの点では ROAD_MERGE_MAX_DY まで許す。
    /// 重なる区間はノードで合流するランプの取り付け部で、ランプの縦断が本線から離れていても同じ路面として扱う。
    /// ランプは本線の路面の内側へ入りきった点も結合し、本線は相手の路面が縁に掛かる点だけを結合する。
    /// </summary>
    public static RoadCorridorInfo Compute(List<Vector3> ownPoints, float ownWidth, float ownWallWidth, bool ownIsRamp, List<RoadNeighborInfo> neighbors)
    {
        if (ownPoints == null || ownPoints.Count < 2 || neighbors == null || neighbors.Count == 0) return null;
        int n = ownPoints.Count;
        float ownHalf = ownWidth * 0.5f;
        var info = new RoadCorridorInfo
        {
            Points = ownPoints,
            RightDir = new Vector3[n],
            CumulativeLength = new float[n],
            LeftMerged = new bool[n],
            RightMerged = new bool[n],
            LeftFillTo = new float[n],
            RightFillTo = new float[n],
            LeftWallBlocked = new bool[n],
            RightWallBlocked = new bool[n],
            TargetY = new float[n],
        };
        for (int i = 0; i < n; i++)
        {
            if (i > 0) info.CumulativeLength[i] = info.CumulativeLength[i - 1] + Vector3.Distance(ownPoints[i - 1], ownPoints[i]);
            info.LeftFillTo[i] = info.RightFillTo[i] = info.TargetY[i] = float.NaN;
        }
        info.TotalLength = info.CumulativeLength[n - 1];

        var bounds = new Rect[neighbors.Count];
        for (int j = 0; j < neighbors.Count; j++)
        {
            float reach = (neighbors[j]?.Width ?? 0f) * 0.5f + ownHalf + ownWallWidth + WALL_BLOCK_CLEARANCE
                + LocalConstants.ROAD_MERGE_MAX_EDGE_GAP + LONGITUDINAL_TOLERANCE;
            bounds[j] = TrackAdjacency.ComputeExpandedBoundsXZ(neighbors[j]?.Points, reach);
        }
        float cosMaxAngle = Mathf.Cos(LocalConstants.ROAD_MERGE_MAX_ANGLE_DEG * Mathf.Deg2Rad);
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            Vector3 point = ownPoints[i];
            Vector3 forward = ownPoints[Mathf.Min(i + 1, n - 1)] - ownPoints[Mathf.Max(i - 1, 0)];
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f)
            {
                info.RightDir[i] = i > 0 ? info.RightDir[i - 1] : Vector3.right;
                continue;
            }
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            info.RightDir[i] = right;
            float nearestGap = float.PositiveInfinity;

            for (int j = 0; j < neighbors.Count; j++)
            {
                RoadNeighborInfo neighbor = neighbors[j];
                if (neighbor?.Points == null || neighbor.Points.Count < 2 || !bounds[j].Contains(new Vector2(point.x, point.z))) continue;
                if (!TrackAdjacency.TryClosestPointOnPolylineXZ(neighbor.Points, point, out Vector3 closest, out Vector3 tangent, out _, out _)) continue;
                Vector3 delta = closest - point;
                delta.y = 0f;
                if (Mathf.Abs(Vector3.Dot(forward, delta)) > LONGITUDINAL_TOLERANCE) continue;
                if (Mathf.Abs(Vector3.Dot(forward, tangent)) < cosMaxAngle) continue;
                float lateral = Vector3.Dot(right, delta);
                float neighborHalf = neighbor.Width * 0.5f;
                float gap = Mathf.Abs(lateral) - ownHalf - neighborHalf;
                if (gap > LocalConstants.ROAD_MERGE_MAX_EDGE_GAP) continue;
                float maxDy = gap < 0f ? LocalConstants.ROAD_MERGE_OVERLAP_MAX_DY : LocalConstants.ROAD_MERGE_MAX_DY;
                if (Mathf.Abs(closest.y - point.y) > maxDy) continue;
                any = true;

                if (ownWallWidth > 0f)
                {
                    float zone = ownHalf + ownWallWidth + WALL_BLOCK_CLEARANCE;
                    if (lateral + neighborHalf > ownHalf && lateral - neighborHalf < zone) info.RightWallBlocked[i] = true;
                    if (-lateral + neighborHalf > ownHalf && -lateral - neighborHalf < zone) info.LeftWallBlocked[i] = true;
                }

                if (ownIsRamp)
                {
                    // 本線の中心線上(ノード付近)では本線がどちら側にあるとも言えないので、両側を結合する。
                    if (lateral > -0.01f) info.RightMerged[i] = true;
                    if (lateral < 0.01f) info.LeftMerged[i] = true;
                    if (gap < nearestGap)
                    {
                        nearestGap = gap;
                        info.TargetY[i] = closest.y;
                    }
                    continue;
                }

                // 本線の縁の内側へ入りきったランプの路面は本線の路面そのものなので、本線の壁・路側線を変えない。
                if (gap <= -2f * neighborHalf) continue;
                bool onRight = lateral > 0f;
                if (onRight) info.RightMerged[i] = true;
                else info.LeftMerged[i] = true;
                // 隙間が閉じる手前の点まで舗装を続け、両側の路面の縁の間に楔形の抜けを残さない。
                if (gap <= -FILL_OVERLAP) continue;
                float fill = Mathf.Max(ownHalf, Mathf.Abs(lateral) - neighborHalf);
                float[] fills = onRight ? info.RightFillTo : info.LeftFillTo;
                if (float.IsNaN(fills[i]) || fill < fills[i]) fills[i] = fill;
            }
        }
        return any ? info : null;
    }

    /// <summary>
    /// ランプの点列の高さを結合区間で本線の路面へ寄せた新しい点列を返す(結合区間が無ければ元の点列)。
    /// 結合区間は本線の路面より ROAD_MERGE_SINK だけ下げ、本線と重なる路面を本線の下へ隠す。
    /// 結合区間の外は、寄せた量を高さの差に比例した長さで滑らかに0へ戻す(加わる勾配は最大 ROAD_MERGE_MAX_ADDED_GRADE)。
    /// 戻しきれない短い区間は、両側の寄せ量を補間するか、道路の端で0になるよう縮める。
    /// </summary>
    public static List<Vector3> ApplyRampHeights(List<Vector3> points, RoadCorridorInfo corridor)
    {
        if (points == null || corridor?.TargetY == null || corridor.TargetY.Length != points.Count) return points;
        int n = points.Count;
        var offsets = new float[n];
        var merged = new bool[n];
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            float target = corridor.TargetY[i];
            if (float.IsNaN(target)) continue;
            merged[i] = true;
            any = true;
            offsets[i] = target - LocalConstants.ROAD_MERGE_SINK - points[i].y;
        }
        if (!any) return points;

        float[] s = corridor.CumulativeLength;
        int k = 0;
        while (k < n)
        {
            if (merged[k]) { k++; continue; }
            int first = k;
            while (k < n && !merged[k]) k++;
            int last = k - 1;
            bool hasBefore = first > 0, hasAfter = last < n - 1;
            float from = hasBefore ? s[first - 1] : s[first];
            float to = hasAfter ? s[last + 1] : s[last];
            float span = to - from;
            float before = hasBefore ? offsets[first - 1] : 0f;
            float after = hasAfter ? offsets[last + 1] : 0f;
            float lengthBefore = hasBefore ? Mathf.Min(TransitionLength(before), span) : 0f;
            float lengthAfter = hasAfter ? Mathf.Min(TransitionLength(after), span) : 0f;
            bool bridge = hasBefore && hasAfter && lengthBefore + lengthAfter >= span;
            for (int i = first; i <= last; i++)
            {
                if (bridge)
                {
                    float t = span > 1e-4f ? (s[i] - from) / span : 0.5f;
                    offsets[i] = Mathf.Lerp(before, after, Mathf.SmoothStep(0f, 1f, t));
                    continue;
                }
                float value = 0f;
                if (hasBefore) value += before * Decay(s[i] - from, lengthBefore);
                if (hasAfter) value += after * Decay(to - s[i], lengthAfter);
                offsets[i] = value;
            }
        }

        var result = new List<Vector3>(n);
        for (int i = 0; i < n; i++)
        {
            Vector3 p = points[i];
            p.y += offsets[i];
            result.Add(p);
        }
        return result;
    }

    // SmoothStep の最大傾きは1.5なので、長さに1.5を掛けると加わる勾配の最大値が上限に一致する。
    private static float TransitionLength(float offset)
        => Mathf.Clamp(1.5f * Mathf.Abs(offset) / LocalConstants.ROAD_MERGE_MAX_ADDED_GRADE,
            LocalConstants.ROAD_MERGE_MIN_TRANSITION, LocalConstants.ROAD_MERGE_MAX_TRANSITION);

    private static float Decay(float distance, float length)
        => length <= 1e-4f ? 0f : 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(distance / length));
}
