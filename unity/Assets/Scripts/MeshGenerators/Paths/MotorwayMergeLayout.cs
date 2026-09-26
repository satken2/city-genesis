using System;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// 高速道路のノードで本線を優先する描画計画。本線は、稼働中でランプでない高速道路のうちノードで最も一直線に続く2本。
/// 浅い角度で入る稼働中のランプは本線の路面へ取り込み、ランプの路面が本線の縁の内側へ入りきる位置で切る
/// (そこから先のランプは本線の路面の下にある。高さは RoadAdjacency が本線へ寄せる)。
/// 本線がほぼ一直線で同じ幅、かつ本線以外の枝をすべて取り込めるときは交差点の板を作らず、本線の2本をノードまで描いてつなぐ。
/// それ以外は通常どおり全枝で板を作り、板の高さだけを本線の縦断に合わせる。
/// 保存形状・占有範囲・交通容量は変えない。
/// </summary>
public sealed class MotorwayMergeLayout
{
    private sealed class Arm
    {
        public PathSummary Path;
        public List<Vector3> Points;
        public Vector2 Direction;
        public float Width;
        public PathMasterItem Master;
    }

    public int MainA { get; }
    public int MainB { get; }

    /// <summary>交差点の板を作るか。偽なら本線の2本をノードでつなぎ、取り込んだランプだけを切る。</summary>
    public bool NeedsSlab { get; }

    /// <summary>板を作らないときの切断位置。本線はノード端、取り込んだランプは本線の路面へ入りきる位置。</summary>
    public Dictionary<int, Vector3> ConnectionPoints { get; } = new Dictionary<int, Vector3>();

    /// <summary>板の高さを本線の縦断に合わせるための本線の中心線。</summary>
    public MainLineSurface Surface { get; }

    private readonly Vector2 node;
    private readonly Dictionary<int, Arm> mains = new Dictionary<int, Arm>();
    private readonly Dictionary<int, Vector2> absorbedReference = new Dictionary<int, Vector2>();

    private MotorwayMergeLayout(Vector2 node, Arm a, Arm b, bool needsSlab)
    {
        this.node = node;
        MainA = a.Path.path_id;
        MainB = b.Path.path_id;
        NeedsSlab = needsSlab;
        mains[MainA] = a;
        mains[MainB] = b;
        Surface = new MainLineSurface(MainA, a.Points, MainB, b.Points, Mathf.Max(a.Width, b.Width));
    }

    public bool IsAbsorbed(int pathId) => absorbedReference.ContainsKey(pathId);

    /// <param name="masters">バリアント→マスター。null なら MasterDataStore。</param>
    public static MotorwayMergeLayout Create(int nodeId, List<PathSummary> paths, Func<PathSummary, List<Vector3>> pointResolver,
        Func<PathSummary, float> cutLimitResolver, Func<int, PathMasterItem> masters = null)
    {
        if (nodeId <= 0 || paths == null || paths.Count < 2) return null;
        masters ??= variant => MasterDataStore.Instance?.GetPathMaster(variant);
        var arms = new List<Arm>();
        foreach (var path in paths)
        {
            bool start = path.start_node_id == nodeId, end = path.end_node_id == nodeId;
            if (!start && !end) continue;
            if (start && end) return null;
            var source = pointResolver != null ? pointResolver(path) : IntersectionPolygonUtil.SamplePlan(path);
            if (source == null || source.Count < 2) return null;
            var points = new List<Vector3>(source);
            if (end) points.Reverse();
            var master = masters(path.path_variant);
            arms.Add(new Arm
            {
                Path = path, Points = points, Master = master,
                Direction = IntersectionPolygonUtil.AtDistance(points, 0f).Direction,
                Width = master != null && master.width > 0f ? master.width : path.width,
            });
        }
        arms.Sort((x, y) => x.Path.path_id.CompareTo(y.Path.path_id));

        // 通常の2本接合と同じ角度で本線の継続を判定し、分岐や横断を本線に読み替えない。
        Arm a = null, b = null;
        float best = -0.8f;
        for (int i = 0; i < arms.Count; i++)
            for (int j = i + 1; j < arms.Count; j++)
            {
                if (!IsMain(arms[i]) || !IsMain(arms[j])) continue;
                float dot = Vector2.Dot(arms[i].Direction, arms[j].Direction);
                if (dot < best) { best = dot; a = arms[i]; b = arms[j]; }
            }
        if (a == null) return null;

        Vector2 nodeXZ = IntersectionPolygonUtil.XZ(a.Points[0]);
        bool straight = best <= -Mathf.Cos(LocalConstants.ROAD_MERGE_MAX_MAIN_BEND_DEG * Mathf.Deg2Rad)
            && Mathf.Abs(a.Width - b.Width) < 0.01f;
        var absorbed = new Dictionary<int, Vector3>();
        bool allAbsorbed = straight && arms.Count > 2;
        foreach (var arm in arms)
        {
            if (!allAbsorbed) break;
            if (arm == a || arm == b) continue;
            if (TryAbsorb(arm, a, b, cutLimitResolver, out Vector3 cut)) absorbed[arm.Path.path_id] = cut;
            else allAbsorbed = false;
        }

        var layout = new MotorwayMergeLayout(nodeXZ, a, b, !allAbsorbed);
        if (!allAbsorbed) return layout;
        layout.ConnectionPoints[a.Path.path_id] = a.Points[0];
        layout.ConnectionPoints[b.Path.path_id] = b.Points[0];
        foreach (var pair in absorbed)
        {
            layout.ConnectionPoints[pair.Key] = pair.Value;
            layout.absorbedReference[pair.Key] = IntersectionPolygonUtil.XZ(pair.Value);
        }
        return layout;
    }

    private static bool IsMain(Arm arm)
        => arm.Path.path_type == Constants.PATH_MOTORWAY && arm.Path.status == Constants.STATUS_ACTIVE
            && arm.Master != null && !arm.Master.ramp;

    /// <summary>
    /// ランプが近い方の本線の路面へ入りきっている区間の外端(ノードからの距離)を求める。
    /// ランプの路面の外縁が本線の縁を越える最初の位置で、切断の上限より遠い・向きが離れすぎる・高さが離れすぎる場合は取り込まない。
    /// </summary>
    private static bool TryAbsorb(Arm ramp, Arm a, Arm b, Func<PathSummary, float> cutLimitResolver, out Vector3 cut)
    {
        cut = default;
        if (ramp.Path.path_type != Constants.PATH_MOTORWAY || ramp.Path.status != Constants.STATUS_ACTIVE
            || ramp.Master == null || !ramp.Master.ramp) return false;
        Arm into = Vector2.Dot(ramp.Direction, a.Direction) >= Vector2.Dot(ramp.Direction, b.Direction) ? a : b;
        if (Vector2.Dot(ramp.Direction, into.Direction) < Mathf.Cos(LocalConstants.ROAD_MERGE_MAX_ANGLE_DEG * Mathf.Deg2Rad)) return false;
        float mainHalf = into.Width * 0.5f, rampHalf = ramp.Width * 0.5f;
        if (rampHalf >= mainHalf) return false;
        float limit = cutLimitResolver != null ? cutLimitResolver(ramp.Path) : IntersectionPolygonUtil.Length(ramp.Points) * 0.95f;
        float previous = rampHalf - mainHalf, traveled = 0f;
        for (int i = 1; i < ramp.Points.Count; i++)
        {
            float step = Vector2.Distance(IntersectionPolygonUtil.XZ(ramp.Points[i - 1]), IntersectionPolygonUtil.XZ(ramp.Points[i]));
            if (step < 1e-6f) continue;
            Vector3 point = ramp.Points[i];
            if (!TrackAdjacency.TryClosestPointOnPolylineXZ(into.Points, point, out Vector3 closest, out _, out _, out _)) return false;
            if (Mathf.Abs(closest.y - point.y) > LocalConstants.ROAD_MERGE_OVERLAP_MAX_DY) return false;
            float excess = Vector2.Distance(IntersectionPolygonUtil.XZ(point), IntersectionPolygonUtil.XZ(closest)) + rampHalf - mainHalf;
            if (excess > 0f)
            {
                float distance = traveled + step * (-previous / (excess - previous));
                if (distance < 0.5f || distance > limit) return false;
                cut = IntersectionPolygonUtil.AtDistance(ramp.Points, distance).Position;
                return true;
            }
            previous = excess;
            traveled += step;
            if (traveled > limit) return false;
        }
        return false;
    }

    /// <summary>
    /// 取り込んだランプから本線へ入る車両は、ランプの側に最も近い車線へ入る。外側・内側は通行側と独立に求める。
    /// </summary>
    public int TargetLane(int fromPath, int toPath, int lane, int trafficHand)
    {
        if (!absorbedReference.TryGetValue(fromPath, out Vector2 reference) || !mains.TryGetValue(toPath, out Arm to)) return lane;
        RoadPath.GetPathMasterParams(to.Path, out float width, out float sidewalk, out float side, out _,
            out int lanes, out _, out float center, out _, out _, out _, out _);
        var layout = new RoadLaneLayout(width, sidewalk, side, center, lanes, to.Path.travel_direction);
        float rampSide = Mathf.Sign(Vector2.Dot(reference - node, new Vector2(-to.Direction.y, to.Direction.x)));
        int target = lane;
        float best = float.NegativeInfinity;
        for (int i = 0; i < layout.Count; i++)
        {
            float position = -layout.Center(i, trafficHand) * rampSide;
            if (position > best) { best = position; target = i; }
        }
        return target;
    }
}

/// <summary>ノードをまたぐ本線の中心線。交差点の板の高さを本線の縦断に合わせる。</summary>
public sealed class MainLineSurface
{
    private readonly List<Vector3> line;
    private readonly float half;

    public int ArmA { get; }
    public int ArmB { get; }

    /// <param name="a">本線の片方の点列(ノードから外向き)。</param>
    /// <param name="b">本線のもう片方の点列(ノードから外向き)。</param>
    public MainLineSurface(int armA, List<Vector3> a, int armB, List<Vector3> b, float width)
    {
        ArmA = armA;
        ArmB = armB;
        half = width * 0.5f;
        line = new List<Vector3>(a.Count + b.Count);
        for (int i = a.Count - 1; i >= 0; i--) line.Add(a[i]);
        for (int i = 1; i < b.Count; i++) line.Add(b[i]);
    }

    /// <summary>本線の路面の高さ(スプライン点の高さ)。outside は本線の路面の縁から外側への水平距離。</summary>
    public float Height(Vector2 point, out float outside)
    {
        outside = float.PositiveInfinity;
        if (!TrackAdjacency.TryClosestPointOnPolylineXZ(line, new Vector3(point.x, 0f, point.y), out Vector3 closest, out _, out _, out _))
            return 0f;
        outside = Mathf.Max(0f, Vector2.Distance(point, IntersectionPolygonUtil.XZ(closest)) - half);
        return closest.y;
    }
}
