using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>One intersection signal. World coordinates.</summary>
public sealed class TrafficSignalPlacement
{
    /// <summary>Incoming approach controlled by this signal.</summary>
    public int ApproachPathId;

    /// <summary>Post contact point: sidewalk top if present, otherwise ground.</summary>
    public Vector3 PoleBase;

    /// <summary>Horizontal unit vector from post toward head (x=X,y=Z).</summary>
    public Vector2 ArmDirection;

    /// <summary>Horizontal post-axis to head-hanger distance (m).</summary>
    public float Reach;

    /// <summary>Horizontal display-facing unit vector (x=X,y=Z), toward incoming vehicles.</summary>
    public Vector2 Facing;

    /// <summary>False adds only an arm to an existing post (no duplicate post at the same point).</summary>
    public bool OwnsPole = true;

    public Vector2 HeadPosition => new Vector2(PoleBase.x, PoleBase.z) + ArmDirection * Reach;
}

/// <summary>
/// Intersection signal eligibility/placement, rendering only without economic/pathfinding/vehicle-motion effects.
/// If at least three completed road arms (streets/motorways) exist, with at least two bidirectional streets
/// separated by centerlines/medians, place one signal per vehicle-entry arm.
/// Place on the far-side, traffic-hand-side sidewalk (left for left-hand traffic), arm above same-direction lanes of the straight-ahead arm.
/// Without a straight-ahead arm (e.g. T-junction stem), place on the opposite corner sidewalk facing incoming lanes.
/// </summary>
public static class TrafficSignalLayout
{
    private const float StraightCos = 0.7071f;   // Treat arms within 45° of incoming direction as straight-ahead.
    private const float PoleCurbInset = 0.5f;    // Curb-to-post-axis distance into sidewalk; use sidewalk center if narrower than twice this.
    private const float PoleRoadsideGap = 0.5f;  // Road-edge-to-post-axis outward distance without sidewalks.
    private const float PoleAlong = 0.5f;        // Post-axis distance outward from straight-arm cut (corner on intersection side of crosswalk).
    private const float PoleMergeDistance = 1f;  // Merge closer posts into one, adding only arms.
    private const float HeadMergeDistance = 1f;  // Do not draw heads closer than this over each other.
    private const float CarriagewayProbe = 40f;  // Distance outward from cut for checking post intrusion into other arm roadways.

    /// <summary>
    /// Return placement for eligible intersections; null if ineligible or no position fits.
    /// Pass sections/corners/setbacks identical to RoadIntersectionMeshGenerator results.
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
            // Irregular intersections without opposite corners (direction passes between mouths) use the arm closest to incoming direction as straight-ahead.
            if (placement == null && exit == null && (exit = StraightExit(context, approach, inward, 0f)) != null)
                placement = AcrossExit(context, approach, exit);
            if (placement != null && Merge(placements, placement, minReach)) placements.Add(placement);
        }
        return placements.Count > 0 ? placements : null;
    }

    /// <summary>At least three completed road arms, with at least two centerline-separated bidirectional streets?</summary>
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

    /// <summary>Bidirectional road with at least one lane each way and centerline/median separation?</summary>
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
        public float Hand;   // Left-hand traffic=1, right-hand=-1; multiplying section Left gives traffic-hand side.
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

    /// <summary>Arm nearest incoming direction. Null if angle difference is below minCos. Tracks are never straight-ahead targets.</summary>
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

    /// <summary>Place on straight-arm traffic-side sidewalk, extending arm above center of same-direction lanes.</summary>
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

    /// <summary>Head offset from outgoing centerline toward traffic side, above outgoing lane-group center.</summary>
    private static float HeadOffset(PathSummary exit, int nodeId)
    {
        var lanes = Lanes(exit, out int count);
        if (IsDividedTwoWay(lanes, count)) return (lanes.Outer + lanes.Inner) * 0.5f;
        // Outgoing one-way uses all lanes, hence center. One-lane bidirectional/incoming-only one-way uses center of traffic-side half.
        return lanes.OneWay && Leaves(exit, nodeId) ? 0f : lanes.Outer * 0.5f;
    }

    /// <summary>
    /// Signal without a straight-ahead arm: place at the corner reached by extending incoming-lane-group centerline (opposite T sidewalk),
    /// shifting post trafficward by minimum arm length so head faces lane-group center.
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
        // Corner sidewalks exist only if both arms have sidewalks; interpolate width along perimeter (same as BuildEdgeConnector).
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

    /// <summary>Resolve post contact height and create placement. Null if post enters roadway or has no supporting surface.</summary>
    private static TrafficSignalPlacement Place(Context context, PathSummary approach, Vector2 pole, Vector2 armDirection, float reach,
        PathSummary sidewalkOwner, float sidewalk, float inset)
    {
        if (OnCarriageway(context, pole)) return null;
        var point = new Vector3(pole.x, 0f, pole.y);
        if (sidewalkOwner != null)
        {
            // Curb/walking-surface heights differ; sample top at post lateral position (curb side=sidewalk model +X).
            point = RoadIntersectionMeshGenerator.SectionSurfacePoint(point, context.Sections, 0f, context.MainLine)
                + Vector3.up * RoadEquipmentGenerator.SurfaceHeight(sidewalkOwner, "sidewalk", 0.5f - inset / sidewalk);
        }
        else if (context.HorizontalPlane)
        {
            // Elevated intersections without sidewalks have no surface for posts.
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

    /// <summary>Whether a point lies in any arm roadway (outside cut, inside curbs).</summary>
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
    /// Adjust to avoid existing signals. Reject overlapping heads; merge nearby posts by adding arms only,
    /// preserving head positions and updating arm directions/lengths.
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
