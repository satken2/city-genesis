using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// 軌道の位置が違う線路どうしのつなぎ目のS字。
///
/// ノードで真っすぐ続く2本の組(レールがつながり、偏向角が RAIL_MAX_TURN_ANGLE_DEG 以下の組を真っすぐなものから順に作る)
/// のうち、軌道数が同じで軌道の横位置が違うものは、ノードをまたぐ1本のS字を前後のパスで分け合い、
/// 各軌道を一方のパスの元の位置から他方の元の位置へ移す。駅かどうかは見ず、双方が曲がる。
/// S字は 6t⁵−15t⁴+10t³ で、全長は最小半径が2本の最小曲線半径の大きい方になる長さ。前後に半分ずつ割り当て、
/// 片端あたりパスの長さの半分までで切る。S(1−x)=1−S(x) なので、各パスが自分の受け持ちだけで重みを求めても
/// ノードで横位置と向きがそろう。
/// 軌道の横位置は左右対称(GetTrackCenterOffset)なので、ずれも左右対称になり、コリドーの中心線は動かない。
/// 線路・交差点・車両がこのクラスの同じ結果を使う。どれか1つだけ別の式にすると、レールの無い所を列車が走る。
/// </summary>
public static class TrackEndTransition
{
    /// <summary>6t⁵−15t⁴+10t³ の2階微分の絶対値の最大(10/√3)。S字の最小半径から全長を逆算するのに使う。</summary>
    private const float SMOOTHERSTEP_PEAK_CURVATURE = 5.7735027f;

    /// <summary>これより小さい横位置の差は同じ位置とみなす(m)。</summary>
    public const float LATERAL_EPSILON = 0.001f;

    /// <summary>
    /// パスの片端のS字。距離はその端のノード(アンカー)からパスに沿って測る。
    /// Delta は自分の幾何フレーム(始点→終点の右が正)での、各軌道の相手の元の位置までの差。
    /// </summary>
    public sealed class EndShift
    {
        public float[] Delta;

        /// <summary>自分が受け持つ長さ(m)。ノードからこの距離より先は元の位置。</summary>
        public float Own;

        /// <summary>S字の全長(m)。自分と相手の受け持ちの和。</summary>
        public float Span;

        /// <summary>ノードからの距離 distance での、相手の位置へ寄る割合(0〜1)。</summary>
        public float Weight(float distance)
        {
            if (distance >= Own) return 0f;
            return Smootherstep(Mathf.Clamp01((Own - Mathf.Max(0f, distance)) / Span));
        }

        /// <summary>Weight の距離微分(ノードから離れる向きを正)。</summary>
        public float WeightSlope(float distance)
        {
            if (distance >= Own || distance < 0f) return 0f;
            return -SmootherstepSlope(Mathf.Clamp01((Own - distance) / Span)) / Span;
        }

        /// <summary>右端の軌道のずれ(外へ広がると正)。ずれは左右対称なので、左端はこの符号反転。</summary>
        public float OuterShift(float distance) => Delta[Delta.Length - 1] * Weight(distance);

        /// <summary>軌道 track のずれ。</summary>
        public float Shift(int track, float distance) => Delta[track] * Weight(distance);
    }

    public static float Smootherstep(float x) => x * x * x * (x * (x * 6f - 15f) + 10f);

    public static float SmootherstepSlope(float x) => 30f * x * x * (x - 1f) * (x - 1f);

    private sealed class Entry
    {
        public PathSummary Path;
        public bool NodeIsStart;
        public Vector2 Direction;
    }

    /// <summary>
    /// ノードにつながる線路のS字を求める。キーは path_id、値はその端のS字。S字の無いパスは含まない。
    /// paths は同じノードにつながるパスの一覧(自分を含む)。並び順によらず同じ結果を返す。
    /// </summary>
    public static Dictionary<int, EndShift> ResolveNode(int nodeId, List<PathSummary> paths)
    {
        var result = new Dictionary<int, EndShift>();
        if (paths == null || paths.Count < 2) return result;

        var entries = new List<Entry>();
        var seen = new HashSet<int>();
        foreach (PathSummary path in paths)
        {
            if (path == null || !TrackIntersectionMeshGenerator.IsTrackType(path.path_type) || !seen.Add(path.path_id)) continue;
            bool start = path.start_node_id == nodeId, end = path.end_node_id == nodeId;
            if (start == end) continue;
            if (!RoadIntersectionMeshGenerator.TryGetPathDirectionFromNode(nodeId, path, out Vector2 nodePoint, out Vector2 directionPoint)) continue;
            entries.Add(new Entry { Path = path, NodeIsStart = start, Direction = (directionPoint - nodePoint).normalized });
        }
        if (entries.Count < 2) return result;

        float cosLimit = Mathf.Cos(LocalConstants.RAIL_MAX_TURN_ANGLE_DEG * Mathf.Deg2Rad);
        var pairs = new List<(Entry a, Entry b, float straightness)>();
        for (int i = 0; i < entries.Count; i++)
        {
            for (int j = i + 1; j < entries.Count; j++)
            {
                Entry a = entries[i], b = entries[j];
                if (!TrackIntersectionMeshGenerator.RailsConnect(a.Path.path_type, a.Path.path_variant, b.Path.path_type, b.Path.path_variant)) continue;
                float straightness = Vector2.Dot(-a.Direction, b.Direction);
                if (straightness >= cosLimit) pairs.Add((a, b, straightness));
            }
        }
        // 真っすぐな組から決める。同点は path_id で決め、入力の並びに依存させない。
        pairs.Sort((x, y) =>
        {
            int byStraightness = y.straightness.CompareTo(x.straightness);
            if (byStraightness != 0) return byStraightness;
            int byMin = Mathf.Min(x.a.Path.path_id, x.b.Path.path_id).CompareTo(Mathf.Min(y.a.Path.path_id, y.b.Path.path_id));
            return byMin != 0 ? byMin : Mathf.Max(x.a.Path.path_id, x.b.Path.path_id).CompareTo(Mathf.Max(y.a.Path.path_id, y.b.Path.path_id));
        });

        var paired = new HashSet<int>();
        foreach (var (a, b, _) in pairs)
        {
            if (paired.Contains(a.Path.path_id) || paired.Contains(b.Path.path_id)) continue;
            paired.Add(a.Path.path_id);
            paired.Add(b.Path.path_id);
            AddPairShift(result, a, b);
        }
        return result;
    }

    /// <summary>パス path の、ノードが atStart 側の端のS字。無ければ null。nodePaths はそのノードにつながるパスの一覧。</summary>
    public static EndShift ResolveEnd(PathSummary path, bool atStart, List<PathSummary> nodePaths)
    {
        if (path == null || nodePaths == null) return null;
        int nodeId = atStart ? path.start_node_id : path.end_node_id;
        // 一覧に同じ path_id の古いデータがあっても、渡されたパスで置き換えて判定する(作り直し中のパス)。
        var paths = new List<PathSummary>(nodePaths);
        paths.RemoveAll(p => p != null && p.path_id == path.path_id);
        paths.Add(path);
        return ResolveNode(nodeId, paths).TryGetValue(path.path_id, out EndShift shift) ? shift : null;
    }

    /// <summary>パスの軌道の横位置の形。startPaths / endPaths は両端のノードにつながるパスの一覧(無ければ null)。</summary>
    public static TrackLateralProfile BuildProfile(PathSummary path, List<PathSummary> startPaths, List<PathSummary> endPaths)
    {
        float[] baseLaterals = LrtRoadParts.TrackLaterals(path).ToArray();
        return new TrackLateralProfile(baseLaterals, ResolveEnd(path, true, startPaths), ResolveEnd(path, false, endPaths));
    }

    /// <summary>
    /// 組の片端ずつにS字を割り当てる。aligned は2本の幾何フレームの向きがそろっているか
    /// (片方がノードで始まり、もう片方がノードで終わる)。逆向きなら相手の横位置を左右反転して読む。
    /// </summary>
    private static void AddPairShift(Dictionary<int, EndShift> result, Entry a, Entry b)
    {
        List<float> lateralsA = LrtRoadParts.TrackLaterals(a.Path);
        List<float> lateralsB = LrtRoadParts.TrackLaterals(b.Path);
        int count = lateralsA.Count;
        if (count != lateralsB.Count) return;

        bool aligned = a.NodeIsStart != b.NodeIsStart;
        var deltaA = new float[count];
        var deltaB = new float[count];
        float maxDelta = 0f;
        for (int i = 0; i < count; i++)
        {
            deltaA[i] = (aligned ? lateralsB[i] : -lateralsB[count - 1 - i]) - lateralsA[i];
            deltaB[i] = (aligned ? lateralsA[i] : -lateralsA[count - 1 - i]) - lateralsB[i];
            maxDelta = Mathf.Max(maxDelta, Mathf.Abs(deltaA[i]));
        }
        if (maxDelta < LATERAL_EPSILON) return;

        float radius = Mathf.Max(MinCurveRadius(a.Path), MinCurveRadius(b.Path));
        float half = Mathf.Sqrt(SMOOTHERSTEP_PEAK_CURVATURE * maxDelta * radius) * 0.5f;
        float ownA = Mathf.Min(half, PathLength(a.Path) * 0.5f);
        float ownB = Mathf.Min(half, PathLength(b.Path) * 0.5f);
        float span = ownA + ownB;
        if (span < 0.01f) return;

        result[a.Path.path_id] = new EndShift { Delta = deltaA, Own = ownA, Span = span };
        result[b.Path.path_id] = new EndShift { Delta = deltaB, Own = ownB, Span = span };
    }

    /// <summary>マスターの最小曲線半径(m)。無ければ LocalConstants.TRACK_TRANSITION_DEFAULT_RADIUS。</summary>
    private static float MinCurveRadius(PathSummary path)
    {
        float radius = MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.min_curve_radius ?? 0f;
        return radius > 0f ? radius : LocalConstants.TRACK_TRANSITION_DEFAULT_RADIUS;
    }

    /// <summary>
    /// S字を切る長さの基準にするパスの長さ(アンカーを結ぶ折れ線のXZ長)。
    /// 描画点列を使わずパスのデータだけで決めるので、線路・交差点・車両のどこで求めても同じ値になる。
    /// </summary>
    public static float PathLength(PathSummary path)
    {
        var anchors = new List<Vector2>();
        var start = path.GetStartPointList();
        if (start != null && start.Count >= 3) anchors.Add(new Vector2(start[0], start[2]));
        var waypoints = path.GetWaypointList();
        if (waypoints != null)
            foreach (var waypoint in waypoints)
                if (waypoint.position != null && waypoint.position.Length >= 3) anchors.Add(new Vector2(waypoint.position[0], waypoint.position[2]));
        var end = path.GetEndPointList();
        if (end != null && end.Count >= 3) anchors.Add(new Vector2(end[0], end[2]));
        float length = 0f;
        for (int i = 1; i < anchors.Count; i++) length += Vector2.Distance(anchors[i - 1], anchors[i]);
        return length;
    }
}

/// <summary>
/// 1本の線路の軌道の横位置(幾何フレーム。始点→終点の右が正)。両端のS字(TrackEndTransition)を含む。
/// 位置は「始点のアンカーからの距離」と「終点のアンカーからの距離」で指定する。
/// </summary>
public sealed class TrackLateralProfile
{
    /// <summary>S字の外の横位置(TrackPath.GetTrackCenterOffset)。</summary>
    public readonly float[] Base;
    public readonly TrackEndTransition.EndShift Start;
    public readonly TrackEndTransition.EndShift End;

    public TrackLateralProfile(float[] baseLaterals, TrackEndTransition.EndShift start, TrackEndTransition.EndShift end)
    {
        Base = baseLaterals;
        Start = start != null && start.Delta.Length == baseLaterals.Length ? start : null;
        End = end != null && end.Delta.Length == baseLaterals.Length ? end : null;
    }

    public bool IsUniform => Start == null && End == null;

    public int TrackCount => Base.Length;

    /// <summary>軌道 track の、元の位置からのずれ。</summary>
    public float Shift(int track, float fromStart, float fromEnd)
        => (Start != null ? Start.Shift(track, fromStart) : 0f) + (End != null ? End.Shift(track, fromEnd) : 0f);

    public float Lateral(int track, float fromStart, float fromEnd) => Base[track] + Shift(track, fromStart, fromEnd);

    /// <summary>横位置の、始点→終点へ進む向きの距離微分。</summary>
    public float Slope(int track, float fromStart, float fromEnd)
        => (Start != null ? Start.Delta[track] * Start.WeightSlope(fromStart) : 0f)
            - (End != null ? End.Delta[track] * End.WeightSlope(fromEnd) : 0f);

    /// <summary>右端の軌道のずれ(外へ広がると正)。左右対称なので、道床・高欄・架線柱の外端はこれだけ外へ動く。</summary>
    public float OuterShift(float fromStart, float fromEnd) => Shift(TrackCount - 1, fromStart, fromEnd);
}

/// <summary>
/// 描画点列の上で横位置の形(TrackLateralProfile)を読む。along は点列の先頭からの3D累積距離。
/// 両端のアンカーからの距離は、アンカーから点列の端までの水平距離に点列に沿った距離を足して求める
/// (交差点の切断・トンネルや工事の区間分割で点列が短くなっても、同じ地点が同じ横位置になる。切断は短く直線に近いので弧と弦の差は無視する)。
/// スプラインの割合は点列に沿った距離の割合とみなす(SampleMode.Uniform)。
/// </summary>
public sealed class TrackLateralSampler
{
    public readonly TrackLateralProfile Profile;
    private readonly float startOffset;
    private readonly float endOffset;
    private readonly float[] cumulative;

    public TrackLateralSampler(TrackLateralProfile profile, PathSummary path, List<Vector3> points)
    {
        Profile = profile;
        cumulative = new float[points.Count];
        for (int i = 1; i < points.Count; i++) cumulative[i] = cumulative[i - 1] + Vector3.Distance(points[i - 1], points[i]);
        startOffset = AnchorDistance(path.GetStartPointList(), points[0]);
        endOffset = AnchorDistance(path.GetEndPointList(), points[points.Count - 1]);
    }

    public float Length => cumulative[cumulative.Length - 1];

    public float AlongAt(int index) => cumulative[index];

    public float Shift(int track, float along) => Profile.Shift(track, startOffset + along, endOffset + Length - along);

    public float Lateral(int track, float along) => Profile.Base[track] + Shift(track, along);

    /// <summary>右端の軌道のずれ。道床・桁・高欄・架線柱の外端はこれだけ外へ動く(左は符号反転)。</summary>
    public float OuterShift(float along) => Shift(Profile.TrackCount - 1, along);

    public float ShiftAtPercent(int track, double percent) => Shift(track, (float)percent * Length);

    public float OuterShiftAtPercent(double percent) => OuterShift((float)percent * Length);

    /// <summary>点列の各点での軌道 track のずれ。</summary>
    public float[] ShiftsAtPoints(int track)
    {
        var shifts = new float[cumulative.Length];
        for (int i = 0; i < shifts.Length; i++) shifts[i] = Shift(track, cumulative[i]);
        return shifts;
    }

    /// <summary>点列の各点での右端の軌道のずれ。</summary>
    public float[] OuterShiftsAtPoints() => ShiftsAtPoints(Profile.TrackCount - 1);

    private static float AnchorDistance(List<float> anchor, Vector3 point)
    {
        if (anchor == null || anchor.Count < 2) return 0f;
        float z = anchor.Count >= 3 ? anchor[2] : anchor[1];
        return Vector2.Distance(new Vector2(anchor[0], z), new Vector2(point.x, point.z));
    }
}
