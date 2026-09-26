using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>交差点に置く信号機1基。座標はワールド。</summary>
public sealed class TrafficSignalPlacement
{
    /// <summary>この信号機が受け持つ流入路。</summary>
    public int ApproachPathId;

    /// <summary>柱の接地点。歩道があれば歩道の上面、無ければ地表。</summary>
    public Vector3 PoleBase;

    /// <summary>柱から灯器へ向かう水平の単位ベクトル(x=X, y=Z)。</summary>
    public Vector2 ArmDirection;

    /// <summary>柱の軸から灯器の吊り金具までの水平距離(m)。</summary>
    public float Reach;

    /// <summary>灯器の表示面が向く水平の単位ベクトル(x=X, y=Z)。流入してくる車の側を向く。</summary>
    public Vector2 Facing;

    /// <summary>偽なら先に置いた信号機の柱へアームだけを足す(同じ場所に柱を重ねない)。</summary>
    public bool OwnsPole = true;

    public Vector2 HeadPosition => new Vector2(PoleBase.x, PoleBase.z) + ArmDirection * Reach;
}

/// <summary>
/// 交差点の信号機の設置判定と配置。描画専用で、経済・経路探索・車両の走行には影響しない。
/// 完成済みの道路(街路・高速道路)の腕が3本以上あり、そのうち2本以上がセンターライン(または中央分離帯)で
/// 上下線の分かれた双方向の街路なら、車両が流入できる道路の腕ごとに1基を置く。
/// 配置は交差点の向こう側・通行側(左側通行なら左)の歩道で、アームを直進先の腕の同じ向きの車線の上へ張り出す。
/// 直進先の腕が無い流入路(T字路の突き当たりなど)は、正面の角の歩道へ流入車線と向かい合わせに立てる。
/// </summary>
public static class TrafficSignalLayout
{
    private const float StraightCos = 0.7071f;   // 流入方向から45度以内の腕を直進先とみなす
    private const float PoleCurbInset = 0.5f;    // 縁石から歩道側へ柱の軸までの距離。歩道がこれの2倍より狭ければ歩道の中央
    private const float PoleRoadsideGap = 0.5f;  // 歩道の無い道路で、路端から外側へ柱の軸までの距離
    private const float PoleAlong = 0.5f;        // 直進先の腕の切断面から外側へ柱の軸までの距離(横断歩道より交差点側の角に立てる)
    private const float PoleMergeDistance = 1f;  // これより近い柱は1本にまとめ、アームだけを足す
    private const float HeadMergeDistance = 1f;  // これより近い灯器は重ねて描かない
    private const float CarriagewayProbe = 40f;  // 柱が他の腕の車道へ入っていないかを見る、切断面から外側への距離

    /// <summary>
    /// 信号機を置く交差点なら配置を返す。置かない交差点、または置ける場所が無ければ null。
    /// sections・corners・setbacks は交差点メッシュの生成結果(RoadIntersectionMeshGenerator)と同じものを渡す。
    /// </summary>
    public static List<TrafficSignalPlacement> Build(int nodeId, bool horizontalPlane, List<PathSummary> paths,
        Dictionary<int, PathEndSection> sections, List<IntersectionCorner> corners, Dictionary<int, SidewalkSetback> setbacks,
        MainLineSurface mainLine, float minReach)
    {
        var arms = new List<PathSummary>();
        foreach (var path in paths)
            if (path.start_node_id != path.end_node_id && sections.ContainsKey(path.path_id)) arms.Add(path);
        arms.Sort((a, b) => a.path_id.CompareTo(b.path_id));
        if (!IsSignalized(arms)) return null;

        var context = new Context
        {
            NodeId = nodeId, HorizontalPlane = horizontalPlane, Arms = arms, Sections = sections, Corners = corners,
            Setbacks = setbacks, MainLine = mainLine, MinReach = minReach,
            Hand = CityInfoStore.TrafficHand == Constants.TRAFFIC_HAND_LEFT ? 1f : -1f,
        };
        var placements = new List<TrafficSignalPlacement>();
        foreach (var approach in arms)
        {
            if (!IsActiveRoad(approach) || !Enters(approach, nodeId)) continue;
            Vector2 inward = -sections[approach.path_id].Direction;
            PathSummary exit = StraightExit(context, approach, inward, StraightCos);
            TrafficSignalPlacement placement = exit != null ? AcrossExit(context, approach, exit) : FacingCorner(context, approach, inward);
            // 正面に角が無い(口と口の間を抜ける)変形交差点は、流入方向へいちばん近い腕を直進先として扱う。
            if (placement == null && exit == null && (exit = StraightExit(context, approach, inward, 0f)) != null)
                placement = AcrossExit(context, approach, exit);
            if (placement != null && Merge(placements, placement, minReach)) placements.Add(placement);
        }
        return placements.Count > 0 ? placements : null;
    }

    /// <summary>完成済みの道路の腕が3本以上あり、うち2本以上がセンターラインで上下線の分かれた双方向の街路か。</summary>
    public static bool IsSignalized(IEnumerable<PathSummary> arms)
    {
        int roads = 0, centerLined = 0;
        foreach (var path in arms)
        {
            if (!IsActiveRoad(path)) continue;
            roads++;
            if (path.path_type == Constants.PATH_STREET && HasCenterLine(path)) centerLined++;
        }
        return roads >= 3 && centerLined >= 2;
    }

    /// <summary>双方向で片側1車線以上を持ち、中央線(または中央分離帯)で上下線が分かれている道路か。</summary>
    public static bool HasCenterLine(PathSummary path)
    {
        var lanes = Lanes(path, out int count);
        return IsDividedTwoWay(lanes, count);
    }

    private sealed class Context
    {
        public int NodeId;
        public bool HorizontalPlane;
        public List<PathSummary> Arms;
        public Dictionary<int, PathEndSection> Sections;
        public List<IntersectionCorner> Corners;
        public Dictionary<int, SidewalkSetback> Setbacks;
        public MainLineSurface MainLine;
        public float MinReach;
        public float Hand;   // 左側通行=1、右側通行=-1。断面の Left に掛けると通行側になる
    }

    private static bool IsActiveRoad(PathSummary path) => path.status == Constants.STATUS_ACTIVE
        && (path.path_type == Constants.PATH_STREET || path.path_type == Constants.PATH_MOTORWAY);

    private static bool Enters(PathSummary path, int nodeId) => VehicleManager.DirectionAllowsEntry(path.travel_direction, path.end_node_id == nodeId);

    private static bool Leaves(PathSummary path, int nodeId) => VehicleManager.DirectionAllowsEntry(path.travel_direction, path.start_node_id == nodeId);

    private static bool IsDividedTwoWay(RoadLaneLayout lanes, int count) => !lanes.OneWay && count >= 2 && lanes.Outer > lanes.Inner;

    private static RoadLaneLayout Lanes(PathSummary path, out int count)
    {
        RoadPath.GetPathMasterParams(path, out float width, out float sidewalk, out float side, out _, out count,
            out _, out float center, out _, out _, out _, out _);
        return new RoadLaneLayout(width, sidewalk, side, center, count, path.travel_direction);
    }

    /// <summary>流入方向にいちばん近い腕。minCos 未満の角度差しか無ければ null。線路は直進先にしない。</summary>
    private static PathSummary StraightExit(Context context, PathSummary approach, Vector2 inward, float minCos)
    {
        PathSummary best = null;
        float bestCos = minCos;
        foreach (var path in context.Arms)
        {
            if (path == approach || TrackIntersectionMeshGenerator.IsTrackType(path.path_type)) continue;
            float cos = Vector2.Dot(inward, context.Sections[path.path_id].Direction);
            if (cos > bestCos)
            {
                bestCos = cos;
                best = path;
            }
        }
        return best;
    }

    /// <summary>直進先の腕の通行側の歩道に立て、アームを同じ向きの車線群の中央の上まで張り出す。</summary>
    private static TrafficSignalPlacement AcrossExit(Context context, PathSummary approach, PathSummary exit)
    {
        var section = context.Sections[exit.path_id];
        Vector2 side = section.Left * context.Hand;
        float half = section.Width * 0.5f;
        float sidewalk = RoadIntersectionMeshGenerator.ResolveSidewalkWidth(exit);
        float inset = Mathf.Min(PoleCurbInset, sidewalk * 0.5f);
        float lateral = sidewalk > 0f ? half - sidewalk + inset : half + PoleRoadsideGap;
        float setback = 0f;
        if (context.Setbacks != null && context.Setbacks.TryGetValue(exit.path_id, out var s))
            setback = context.Hand > 0f ? s.LeftOutwardSetback : s.RightOutwardSetback;
        Vector2 pole = section.Center + section.Direction * (setback + PoleAlong) + side * lateral;
        float reach = Mathf.Max(context.MinReach, lateral - HeadOffset(exit, context.NodeId));
        return Place(context, approach, pole, -side, reach, sidewalk > 0f ? exit : null, sidewalk, inset);
    }

    /// <summary>流出路の中心線から通行側へ灯器を吊る距離。流出する向きの車線群の中央の上に来る。</summary>
    private static float HeadOffset(PathSummary exit, int nodeId)
    {
        var lanes = Lanes(exit, out int count);
        if (IsDividedTwoWay(lanes, count)) return (lanes.Outer + lanes.Inner) * 0.5f;
        // 流出する一方通行は全車線が対象なので中央。1車線の双方向・流入専用の一方通行は通行側の半分の中央。
        return lanes.OneWay && Leaves(exit, nodeId) ? 0f : lanes.Outer * 0.5f;
    }

    /// <summary>
    /// 直進先の腕が無い流入路の信号機。流入車線群の中央線を延ばした先の角(T字路なら突き当たりの歩道)に立て、
    /// 灯器が流入車線群の正面に来るように柱を通行側へ最短アーム長だけずらす。
    /// </summary>
    private static TrafficSignalPlacement FacingCorner(Context context, PathSummary approach, Vector2 inward)
    {
        var section = context.Sections[approach.path_id];
        var lanes = Lanes(approach, out int count);
        float inbound = IsDividedTwoWay(lanes, count) ? (lanes.Outer + lanes.Inner) * 0.5f : 0f;
        Vector2 side = -section.Left * context.Hand;
        Vector2 origin = section.Center + side * (inbound + context.MinReach);
        IntersectionCorner corner = null;
        Vector2 hit = default;
        int index = 0;
        float nearest = float.PositiveInfinity;
        foreach (var candidate in context.Corners)
        {
            if (candidate.FromPath == approach.path_id || candidate.ToPath == approach.path_id) continue;
            for (int i = 1; i < candidate.Points.Count; i++)
            {
                if (!PolylineUtils.IntersectRaySegment(origin, inward, candidate.Points[i - 1], candidate.Points[i], out float distance, out float t)
                    || distance >= nearest) continue;
                nearest = distance;
                corner = candidate;
                hit = origin + inward * distance;
                index = t < 0.5f ? i - 1 : i;
            }
        }
        if (corner == null) return null;
        PathSummary from = context.Arms.Find(p => p.path_id == corner.FromPath);
        PathSummary to = context.Arms.Find(p => p.path_id == corner.ToPath);
        if (from == null || to == null) return null;
        // 角の歩道は両側の腕に歩道があるときだけ敷かれ、幅は外周に沿って両者の間を補間する(BuildEdgeConnector と同じ)。
        float fromWidth = RoadIntersectionMeshGenerator.ResolveSidewalkWidth(from);
        float toWidth = RoadIntersectionMeshGenerator.ResolveSidewalkWidth(to);
        float u = index / (float)(corner.Points.Count - 1);
        float sidewalk = fromWidth > 0f && toWidth > 0f ? Mathf.Lerp(fromWidth, toWidth, u * u * (3f - 2f * u)) : 0f;
        Vector2 tangent = corner.Directions[index];
        Vector2 outward = new Vector2(tangent.y, -tangent.x);
        float inset = Mathf.Min(PoleCurbInset, sidewalk * 0.5f);
        Vector2 pole = sidewalk > 0f ? hit - outward * (sidewalk - inset) : hit + outward * PoleRoadsideGap;
        return Place(context, approach, pole, -side, context.MinReach, sidewalk > 0f ? (u < 0.5f ? from : to) : null, sidewalk, inset);
    }

    /// <summary>柱の接地高さを決めて配置を作る。柱が車道に入る・立てる床が無い場合は null。</summary>
    private static TrafficSignalPlacement Place(Context context, PathSummary approach, Vector2 pole, Vector2 armDirection, float reach,
        PathSummary sidewalkOwner, float sidewalk, float inset)
    {
        if (OnCarriageway(context, pole)) return null;
        var point = new Vector3(pole.x, 0f, pole.y);
        if (sidewalkOwner != null)
        {
            // 歩道の上面は縁石と歩行面で高さが違うので、柱を立てる横位置の上面を取る(縁石側=歩道モデルの+X)。
            point = RoadIntersectionMeshGenerator.SectionSurfacePoint(point, context.Sections, 0f, context.MainLine)
                + Vector3.up * RoadEquipmentGenerator.SurfaceHeight(sidewalkOwner, "sidewalk", 0.5f - inset / sidewalk);
        }
        else if (context.HorizontalPlane)
        {
            // 高架の交差点で歩道が無ければ柱を立てる床が無い。
            return null;
        }
        else
        {
            point.y = Utils.GetGroundHeight(new Vector3(pole.x, 1000f, pole.y));
        }
        return new TrafficSignalPlacement
        {
            ApproachPathId = approach.path_id,
            PoleBase = point,
            ArmDirection = armDirection.normalized,
            Reach = reach,
            Facing = context.Sections[approach.path_id].Direction,
        };
    }

    /// <summary>点がどれかの腕の車道(切断面から外側、縁石の内側)に入るか。</summary>
    private static bool OnCarriageway(Context context, Vector2 point)
    {
        foreach (var path in context.Arms)
        {
            var section = context.Sections[path.path_id];
            Vector2 delta = point - section.Center;
            float along = Vector2.Dot(delta, section.Direction);
            if (along < 0f || along > CarriagewayProbe) continue;
            float curb = section.Width * 0.5f - RoadIntersectionMeshGenerator.ResolveSidewalkWidth(path);
            if (Mathf.Abs(Vector2.Dot(delta, section.Left)) < curb) return true;
        }
        return false;
    }

    /// <summary>
    /// 既に置いた信号機と重ならないよう調整する。灯器が重なる候補は捨て、柱が近ければその柱へアームだけを足す
    /// (灯器の位置は保ち、アームの向きと長さを付け替える)。
    /// </summary>
    private static bool Merge(List<TrafficSignalPlacement> placed, TrafficSignalPlacement candidate, float minReach)
    {
        Vector2 head = candidate.HeadPosition;
        foreach (var other in placed)
            if (Vector2.Distance(other.HeadPosition, head) < HeadMergeDistance) return false;
        Vector2 pole = new Vector2(candidate.PoleBase.x, candidate.PoleBase.z);
        foreach (var other in placed)
        {
            if (!other.OwnsPole) continue;
            Vector2 otherPole = new Vector2(other.PoleBase.x, other.PoleBase.z);
            if (Vector2.Distance(otherPole, pole) >= PoleMergeDistance) continue;
            Vector2 arm = head - otherPole;
            if (arm.magnitude < minReach) return false;
            candidate.PoleBase = other.PoleBase;
            candidate.ArmDirection = arm.normalized;
            candidate.Reach = arm.magnitude;
            candidate.OwnsPole = false;
            break;
        }
        return true;
    }
}
