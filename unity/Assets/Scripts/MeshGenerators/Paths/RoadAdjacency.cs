using System;
using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>One adjacent road for joining tests. Provided by PathManager through RoadAdjacency.NeighborProvider.</summary>
public sealed class RoadNeighborInfo
{
    public int PathId;

    /// <summary>Uncut rendered points (world coordinates). Ramp heights are already aligned to main lines.</summary>
    public List<Vector3> Points;

    public float Width;
}

/// <summary>Pair of equal-cross-section roads joined at a node without an intersection plate.</summary>
public sealed class RoadContinuousJoin
{
    public int PathA;
    public int PathB;
}

/// <summary>End handling where roads/tracks continue through a node without an intersection plate.</summary>
public struct RoadEndJoin
{
    /// <summary>Connects directly to the next path. Do not round median ends.</summary>
    public bool Continuous;

    /// <summary>Side responsible for join piers. True for only one of the connected paths.</summary>
    public bool OwnsSupport;
}

/// <summary>
/// Motorway main-line/ramp joining intervals along this road's points. Indices refer to point-list positions.
/// Sides use horizontal directions relative to point progression (right = Cross(up, forward)).
/// </summary>
public sealed class RoadCorridorInfo
{
    public List<Vector3> Points;
    public Vector3[] RightDir;
    public float[] CumulativeLength;
    public float TotalLength;

    /// <summary>Side where the neighboring surface adjoins/overlaps. Omit walls and adjust edge-line rendering.</summary>
    public bool[] LeftMerged;
    public bool[] RightMerged;

    /// <summary>Main-line-only outer paving endpoint (distance from own centerline to neighbor surface edge). NaN means no fill.</summary>
    public float[] LeftFillTo;
    public float[] RightFillTo;

    /// <summary>Points where neighboring surfaces overlap wall positions.</summary>
    public bool[] LeftWallBlocked;
    public bool[] RightWallBlocked;

    /// <summary>Ramp-only joined main-line surface height (spline-point height). NaN means unjoined.</summary>
    public float[] TargetY;

    /// <summary>Intervals allowing walls on the specified side (spline percent).</summary>
    public List<TrackCorridorInfo.PercentRange> GetWallPercentIntervals(bool left, float minRunLength)
    {
        bool[] merged = left ? LeftMerged : RightMerged;
        bool[] blocked = left ? LeftWallBlocked : RightWallBlocked;
        return TrackCorridorInfo.ClearPercentIntervals(CumulativeLength, TotalLength, i => merged[i] || blocked[i], minRunLength);
    }

    /// <summary>Joined intervals on the specified side (spline percent). Null left means either side.</summary>
    public List<TrackCorridorInfo.PercentRange> GetMergedPercentRanges(bool? left)
    {
        if (TotalLength <= 0f) return new List<TrackCorridorInfo.PercentRange>();
        return TrackCorridorInfo.ClearPercentIntervals(CumulativeLength, TotalLength,
            i => !(left != false && LeftMerged[i]) && !(left != true && RightMerged[i]), 0f);
    }
}

/// <summary>
/// Rendering join tests for motorway main lines (non-ramp motorways) and ramps. Draw nearby parallel/merging paths
/// as one surface by omitting intervening walls/edge lines, paving gaps, and aligning ramps to main-line height.
/// Move only ramp heights; preserve main-line profiles. Rendering only, without affecting backend geometry/tests.
/// Excludes general roads.
/// </summary>
public static class RoadAdjacency
{
    /// <summary>Active nearby opposite-role candidates (main lines for ramps, ramps for main lines), set by PathManager.Awake.</summary>
    public static Func<PathSummary, List<RoadNeighborInfo>> NeighborProvider;

    /// <summary>Whether this end continues as main line through the node without a plate. PathManager answers from intersection results.</summary>
    public static Func<PathSummary, bool, RoadEndJoin> EndJoinProvider;

    private const float LONGITUDINAL_TOLERANCE = 2.5f; // Allowed forward component (m), rejecting false nearest points pinned to neighboring endpoints.
    private const float WALL_BLOCK_CLEARANCE = 0.3f; // Margin outside walls (m), preventing neighboring surfaces merely touching walls from staying invisible.
    private const float FILL_OVERLAP = 0.3f; // Overlap width (m) through which gap paving continues after surfaces begin overlapping.

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
    /// Resolve joining intervals along this road's points. Null if no points join.
    /// Join where neighbor surface-edge gap ≤ ROAD_MERGE_MAX_EDGE_GAP and direction difference ≤ ROAD_MERGE_MAX_ANGLE_DEG.
    /// Allow height differences up to ROAD_MERGE_OVERLAP_MAX_DY for overlaps, ROAD_MERGE_MAX_DY for merely adjacent surfaces.
    /// Overlaps represent ramp attachments at merge nodes; treat as one surface even if ramp profiles differ from main lines.
    /// Ramps also join where fully inside main-line surfaces; main lines join only where neighbor surfaces cross their edges.
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
                    // Near nodes on the main-line centerline, neither side contains the main line; join both sides.
                    if (lateral > -0.01f) info.RightMerged[i] = true;
                    if (lateral < 0.01f) info.LeftMerged[i] = true;
                    if (gap < nearestGap)
                    {
                        nearestGap = gap;
                        info.TargetY[i] = closest.y;
                    }
                    continue;
                }

                // A ramp fully inside the main-line edge is already its surface; leave main-line walls/edge lines unchanged.
                if (gap <= -2f * neighborHalf) continue;
                bool onRight = lateral > 0f;
                if (onRight) info.RightMerged[i] = true;
                else info.LeftMerged[i] = true;
                // Continue paving to the point before the gap closes, avoiding wedge-shaped holes between surface edges.
                if (gap <= -FILL_OVERLAP) continue;
                float fill = Mathf.Max(ownHalf, Mathf.Abs(lateral) - neighborHalf);
                float[] fills = onRight ? info.RightFillTo : info.LeftFillTo;
                if (float.IsNaN(fills[i]) || fill < fills[i]) fills[i] = fill;
            }
        }
        return any ? info : null;
    }

    /// <summary>
    /// Return new ramp points with joined heights aligned to main-line surface (original points if no joined intervals).
    /// Sink joined sections ROAD_MERGE_SINK below main line, hiding overlapping ramp surfaces underneath.
    /// Outside joins, smoothly return displacement to zero over length proportional to height difference (added grade ≤ ROAD_MERGE_MAX_ADDED_GRADE).
    /// For short sections, interpolate displacements from both sides or shorten the transition to reach zero at road ends.
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

    // SmoothStep's maximum slope is 1.5; multiplying length by 1.5 makes maximum added grade match the limit.
    private static float TransitionLength(float offset)
        => Mathf.Clamp(1.5f * Mathf.Abs(offset) / LocalConstants.ROAD_MERGE_MAX_ADDED_GRADE,
            LocalConstants.ROAD_MERGE_MIN_TRANSITION, LocalConstants.ROAD_MERGE_MAX_TRANSITION);

    private static float Decay(float distance, float length)
        => length <= 1e-4f ? 0f : 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(distance / length));
}
