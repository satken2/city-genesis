using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// S curves joining tracks with different lateral positions.
///
/// Pair straightest node continuations first (connected rails, deflection ≤ RAIL_MAX_TURN_ANGLE_DEG).
/// Pairs with equal track count but differing laterals share one S curve across the node,
/// moving each track between original positions. Both paths bend, regardless of station status.
/// Curve is 6t⁵−15t⁴+10t³; choose total length so minimum radius equals the larger path minimum radius. Allocate half to each path,
/// capped at half each path length per end. Since S(1−x)=1−S(x), independent local weights
/// still align lateral positions/directions at the node.
/// Track laterals are symmetric (GetTrackCenterOffset), so shifts are symmetric and corridor centerline stays fixed.
/// Tracks/intersections/vehicles share these results; differing formulas would make trains run off rails.
/// </summary>
public static class TrackEndTransition
{
    /// <summary>Maximum absolute second derivative of 6t⁵−15t⁴+10t³ (10/√3), used to derive length from minimum S-curve radius.</summary>
    private const float SMOOTHERSTEP_PEAK_CURVATURE = 5.7735027f;

    /// <summary>Treat smaller lateral differences as identical (m).</summary>
    public const float LATERAL_EPSILON = 0.001f;

    /// <summary>
    /// One path-end S curve. Measure distance along path from that end's node (anchor).
    /// Delta is each track's difference to neighbor original position in own geometric frame (positive rightward start→end).
    /// </summary>
    public sealed class EndShift
    {
        public float[] Delta;

        /// <summary>Own allocated length (m). Beyond this node distance, retain original position.</summary>
        public float Own;

        /// <summary>Total S-curve length (m), sum of both allocations.</summary>
        public float Span;

        /// <summary>Fraction approaching neighbor position (0–1) at node distance distance.</summary>
        public float Weight(float distance)
        {
            if (distance >= Own) return 0f;
            return Smootherstep(Mathf.Clamp01((Own - Mathf.Max(0f, distance)) / Span));
        }

        /// <summary>Distance derivative of Weight (positive away from node).</summary>
        public float WeightSlope(float distance)
        {
            if (distance >= Own || distance < 0f) return 0f;
            return -SmootherstepSlope(Mathf.Clamp01((Own - distance) / Span)) / Span;
        }

        /// <summary>Rightmost track shift (positive outward). Symmetric shifts make the leftmost its negative.</summary>
        public float OuterShift(float distance) => Delta[Delta.Length - 1] * Weight(distance);

        /// <summary>Shift of track.</summary>
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
    /// Resolve S curves for tracks connected to a node: path_id → end curve, omitting unchanged paths.
    /// paths contains all node paths including self. Results are independent of input order.
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
        // Choose straightest pairs first; break ties by path_id, independent of input ordering.
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

    /// <summary>S curve for path's node end atStart. Null if absent. nodePaths lists paths connected there.</summary>
    public static EndShift ResolveEnd(PathSummary path, bool atStart, List<PathSummary> nodePaths)
    {
        if (path == null || nodePaths == null) return null;
        int nodeId = atStart ? path.start_node_id : path.end_node_id;
        // Replace stale same-path_id entries with the supplied path before evaluation (path under regeneration).
        var paths = new List<PathSummary>(nodePaths);
        paths.RemoveAll(p => p != null && p.path_id == path.path_id);
        paths.Add(path);
        return ResolveNode(nodeId, paths).TryGetValue(path.path_id, out EndShift shift) ? shift : null;
    }

    /// <summary>Path lateral profile. startPaths/endPaths list paths at each node (null if absent).</summary>
    public static TrackLateralProfile BuildProfile(PathSummary path, List<PathSummary> startPaths, List<PathSummary> endPaths)
    {
        float[] baseLaterals = LrtRoadParts.TrackLaterals(path).ToArray();
        return new TrackLateralProfile(baseLaterals, ResolveEnd(path, true, startPaths), ResolveEnd(path, false, endPaths));
    }

    /// <summary>
    /// Assign S curves to each end of the pair. aligned means geometric frames agree
    /// (one starts at node, the other ends). Reverse neighbor laterals otherwise.
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

    /// <summary>Master minimum curve radius (m); fallback LocalConstants.TRACK_TRANSITION_DEFAULT_RADIUS.</summary>
    private static float MinCurveRadius(PathSummary path)
    {
        float radius = MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.min_curve_radius ?? 0f;
        return radius > 0f ? radius : LocalConstants.TRACK_TRANSITION_DEFAULT_RADIUS;
    }

    /// <summary>
    /// Reference path length for S-curve truncation (XZ polyline through anchors).
    /// Using only path data, not rendering points, gives identical values in tracks/intersections/vehicles.
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
/// One path's track laterals (geometric frame, positive rightward start→end), including end S curves (TrackEndTransition).
/// Specify positions by distance from start/end anchors.
/// </summary>
public sealed class TrackLateralProfile
{
    /// <summary>Lateral outside S curves (TrackPath.GetTrackCenterOffset).</summary>
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

    /// <summary>Shift of track from its original position.</summary>
    public float Shift(int track, float fromStart, float fromEnd)
        => (Start != null ? Start.Shift(track, fromStart) : 0f) + (End != null ? End.Shift(track, fromEnd) : 0f);

    public float Lateral(int track, float fromStart, float fromEnd) => Base[track] + Shift(track, fromStart, fromEnd);

    /// <summary>Lateral derivative along start→end travel distance.</summary>
    public float Slope(int track, float fromStart, float fromEnd)
        => (Start != null ? Start.Delta[track] * Start.WeightSlope(fromStart) : 0f)
            - (End != null ? End.Delta[track] * End.WeightSlope(fromEnd) : 0f);

    /// <summary>Rightmost shift (positive outward). Symmetry moves bed/parapet/pole outer edges outward by this amount.</summary>
    public float OuterShift(float fromStart, float fromEnd) => Shift(TrackCount - 1, fromStart, fromEnd);
}

/// <summary>
/// Read TrackLateralProfile on rendering points; along is cumulative 3D distance from point-list start.
/// Anchor distances = horizontal anchor-to-list-end distance + along-list distance
/// (same locations retain laterals after intersection cuts/tunnel or construction segmentation; cuts are short/nearly straight, so ignore arc/chord difference).
/// Treat spline fraction as distance fraction along points (SampleMode.Uniform).
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

    /// <summary>Rightmost shift, moving bed/girder/parapet/pole outer edges outward (negative on left).</summary>
    public float OuterShift(float along) => Shift(Profile.TrackCount - 1, along);

    public float ShiftAtPercent(int track, double percent) => Shift(track, (float)percent * Length);

    public float OuterShiftAtPercent(double percent) => OuterShift((float)percent * Length);

    /// <summary>Shift of track at each point.</summary>
    public float[] ShiftsAtPoints(int track)
    {
        var shifts = new float[cumulative.Length];
        for (int i = 0; i < shifts.Length; i++) shifts[i] = Shift(track, cumulative[i]);
        return shifts;
    }

    /// <summary>Rightmost track shift at each point.</summary>
    public float[] OuterShiftsAtPoints() => ShiftsAtPoints(Profile.TrackCount - 1);

    private static float AnchorDistance(List<float> anchor, Vector3 point)
    {
        if (anchor == null || anchor.Count < 2) return 0f;
        float z = anchor.Count >= 3 ? anchor[2] : anchor[1];
        return Vector2.Distance(new Vector2(anchor[0], z), new Vector2(point.x, point.z));
    }
}
