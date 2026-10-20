using System;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// Motorway-node drawing plan prioritizing the main line: the two active non-ramp motorway paths forming the straightest continuation.
/// Absorb shallow-angle active ramps into the main-line surface, cutting where the ramp surface fits completely inside the main-line edge
/// (beyond that, ramps lie beneath the main-line surface; RoadAdjacency aligns their height).
/// If main-line paths are nearly straight/equal-width and all other branches can be absorbed, omit the intersection plate and join both main paths at the node.
/// Otherwise create the normal all-branch plate, adjusting only its height to the main-line profile.
/// Do not change saved geometry, occupancy, or traffic capacity.
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

    /// <summary>Whether to create an intersection plate. False joins main-line paths at the node, clipping only absorbed ramps.</summary>
    public bool NeedsSlab { get; }

    /// <summary>Cut positions without a plate. Main line at node ends; absorbed ramps where fully inside the main-line surface.</summary>
    public Dictionary<int, Vector3> ConnectionPoints { get; } = new Dictionary<int, Vector3>();

    /// <summary>Main-line centerline used to fit plate height to the longitudinal profile.</summary>
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

    /// <param name="masters">Variant→master lookup. Null uses MasterDataStore.</param>
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

        // Use the normal two-path join angle for main-line continuity; do not reinterpret branches/crossings as the main line.
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
    /// Find the outer end (distance from node) of the ramp interval fully inside the nearer main-line surface.
    /// At the first ramp outer-edge crossing of the main-line edge; reject absorption if beyond the cut limit or orientation/height differ too much.
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
    /// Vehicles from absorbed ramps enter the main-line lane nearest the ramp. Determine inner/outer independently of traffic side.
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

/// <summary>Main-line centerline across a node. Fits plate height to the longitudinal profile.</summary>
public sealed class MainLineSurface
{
    private readonly List<Vector3> line;
    private readonly float half;

    public int ArmA { get; }
    public int ArmB { get; }

    /// <param name="a">Points of one main-line path (outward from node).</param>
    /// <param name="b">Points of the other main-line path (outward from node).</param>
    public MainLineSurface(int armA, List<Vector3> a, int armB, List<Vector3> b, float width)
    {
        ArmA = armA;
        ArmB = armB;
        half = width * 0.5f;
        line = new List<Vector3>(a.Count + b.Count);
        for (int i = a.Count - 1; i >= 0; i--) line.Add(a[i]);
        for (int i = 1; i < b.Count; i++) line.Add(b[i]);
    }

    /// <summary>Main-line surface height (spline-point height). outside is horizontal distance beyond the surface edge.</summary>
    public float Height(Vector2 point, out float outside)
    {
        outside = float.PositiveInfinity;
        if (!TrackAdjacency.TryClosestPointOnPolylineXZ(line, new Vector3(point.x, 0f, point.y), out Vector3 closest, out _, out _, out _))
            return 0f;
        outside = Mathf.Max(0f, Vector2.Distance(point, IntersectionPolygonUtil.XZ(closest)) - half);
        return closest.y;
    }
}
