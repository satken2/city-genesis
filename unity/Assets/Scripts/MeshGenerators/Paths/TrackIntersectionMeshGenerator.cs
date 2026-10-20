using UnityEngine;
using System;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>Straight track join at a node without an intersection plate (TrackIntersectionMeshGenerator.TryContinuousJoin).</summary>
public sealed class TrackContinuousJoin
{
    public int PathA;
    public int PathB;
}

/// <summary>
/// Render track (PATH_TRACK)/monorail (PATH_MONORAIL) switches and at-grade crossings.
///
/// RoadIntersectionMeshGenerator shares road logic for outer bed perimeter, cut points, and path trimming
/// (hull material comes from body prefab, automatically producing track-bed appearance).
/// This class adds only track-end connection decisions
/// and connector geometry (RailConnectorSpec).
///
/// For each movement from path A to path B at a node, add one rail if:
///   - RailsConnect allows the pair.
///   - Deflection (A arrival versus B departure; straight=0°, reversal=180°) meets the threshold.
///     Mirrors backend turn constraints (transit_route edge-based Dijkstra + geo_paths.path_end_tangent +
///     parameters.yaml RAIL_MAX_TURN_ANGLE_DEG); street LRT may turn left/right.
///   - A permits arrival and B permits departure (travel_direction).
///     Station-platform tracks allow stopped trains in either direction, so physical rails treat them as bidirectional.
/// Join A's arriving-direction track end to B's departing-direction track end;
/// path/travel direction uniquely determine tracks (TrackPath.TrackIndexForTravel). Deduplicate identical end pairs.
/// Only pairs involving at least three tracks ignore direction, matching all ends monotonically by travel-frame laterals.
/// Rendered rails thus match train-permitted movements, omitting Y-merge reversals,
/// 90° diamond-crossing turns, and forbidden one-way entries.
/// TrackPath buffer stops occupy unused ends under the same rules (ResolveConnectedTrackEnds).
///
/// Turn tests use logical endpoint tangents preferring control points; rendering uses cut-section tangents/grades.
/// For differing-position straight joins (TrackEndTransition S curves), match track laterals/directions at cuts to the curve.
/// Matching-lateral/width straight pairs omit plates and draw to the node (TryContinuousJoin).
///
/// Mixed road/track nodes (level crossings) also draw rails over asphalt hulls
/// when track-pair movements are permitted.
/// </summary>
public static class TrackIntersectionMeshGenerator
{
    /// <summary>Connector Bézier sampling interval (m).</summary>
    private const float SAMPLE_INTERVAL = 1.0f;

    /// <summary>Minimum connector length (m); shorter shapes would collapse.</summary>
    private const float MIN_CONNECTOR_LENGTH = 0.05f;

    /// <summary>
    /// Whether a path type has constrained node turns (runs on rigid guideways).
    /// Keep the same set as backend PATH_TYPES_TURN_RESTRICTED (consts.py).
    /// </summary>
    public static bool IsTrackType(int pathType)
    {
        return pathType == Constants.PATH_TRACK || pathType == Constants.PATH_MONORAIL;
    }

    /// <summary>
    /// Whether two paths' rails may connect at a node, ignoring turn angle/direction. LRT networks (222/242/303/431) connect
    /// across path types; LRT/regular rail and track/monorail do not.
    /// </summary>
    public static bool RailsConnect(int typeA, int variantA, int typeB, int variantB)
    {
        bool lrtA = LrtRoadParts.IsLrt(variantA), lrtB = LrtRoadParts.IsLrt(variantB);
        if (lrtA || lrtB) return lrtA && lrtB;
        return typeA == typeB;
    }

    /// <summary>One path with tracks at a node.</summary>
    private class TrackEntry
    {
        public PathSummary Path;
        public bool NodeIsStart;
        /// <summary>Logical endpoint tangent outward from node into track (turn tests).</summary>
        public Vector2 LogicalDirection;
        /// <summary>Track-center laterals ascending in geometric frame (positive rightward start→end); index=track number.</summary>
        public List<float> Laterals;
        public bool Platform;
        /// <summary>Cut-section tangent outward from node into track (connector shape).</summary>
        public Vector2 Direction;
        public float Slope;
        public Vector2 ConnectionPoint;
        /// <summary>Per-track lateral change rate at cut, away from node (nonzero only in S curves).</summary>
        public float[] LateralSlopes;
    }

    /// <summary>Two rail-connected track ends. A/B index entries, normalized to A &lt; B.</summary>
    private struct Link
    {
        public int A, TrackA, B, TrackB;
    }

    /// <summary>
    /// Compute rail-connector geometry joining permitted track ends.
    /// Null for fewer than two tracks or no connected ends.
    /// Connectors run from earlier to later tracks in entries (paths order).
    /// </summary>
    /// <param name="connectionPoints">
    /// pathId → cut point on intersection-hull edge (XZ), calculated by RoadIntersectionMeshGenerator.
    /// </param>
    public static List<RailConnectorSpec> BuildRailConnectorSpecs(int nodeId, Vector3 nodePosition, bool horizontalPlane, List<PathSummary> paths, Dictionary<int, Vector2> connectionPoints, Dictionary<int, PathEndSection> sections = null)
    {
        if (paths == null || connectionPoints == null)
        {
            return null;
        }

        List<TrackEntry> entries = CollectEntries(nodeId, paths);
        entries.RemoveAll(entry => !connectionPoints.ContainsKey(entry.Path.path_id));
        if (entries.Count < 2)
        {
            return null;
        }
        var shifts = TrackEndTransition.ResolveNode(nodeId, paths);
        Vector2 nodePoint = new Vector2(nodePosition.x, nodePosition.z);
        foreach (TrackEntry entry in entries)
        {
            entry.ConnectionPoint = connectionPoints[entry.Path.path_id];
            entry.Direction = entry.LogicalDirection;
            float distance = Vector2.Distance(entry.ConnectionPoint, nodePoint);
            if (sections != null && sections.TryGetValue(entry.Path.path_id, out var section))
            {
                entry.Direction = section.Direction;
                entry.Slope = section.Tangent.y / Mathf.Max(1e-5f, IntersectionPolygonUtil.XZ(section.Tangent).magnitude);
                distance = section.Distance;
            }
            // In S sections, derive track laterals/directions at cuts with the same formula as TrackPath.
            entry.LateralSlopes = new float[entry.Laterals.Count];
            if (!shifts.TryGetValue(entry.Path.path_id, out var shift) || shift.Delta.Length != entry.Laterals.Count) continue;
            for (int i = 0; i < entry.Laterals.Count; i++)
            {
                entry.Laterals[i] += shift.Shift(i, distance);
                entry.LateralSlopes[i] = shift.Delta[i] * shift.WeightSlope(distance);
            }
        }

        List<RailConnectorSpec> specs = new List<RailConnectorSpec>();
        foreach (Link link in ResolveLinks(entries))
        {
            AddConnector(specs, entries[link.A], link.TrackA, entries[link.B], link.TrackB, nodePosition, horizontalPlane);
        }
        return specs.Count > 0 ? specs : null;
    }

    /// <summary>
    /// Return connected track ends (path ID/track number) using intersection-connector rules.
    /// Place buffer stops on ends outside this set. No cut points needed, so resolve before intersection generation.
    /// </summary>
    public static HashSet<(int pathId, int track)> ResolveConnectedTrackEnds(int nodeId, List<PathSummary> paths)
    {
        var ends = new HashSet<(int pathId, int track)>();
        if (paths == null) return ends;
        List<TrackEntry> entries = CollectEntries(nodeId, paths);
        foreach (Link link in ResolveLinks(entries))
        {
            ends.Add((entries[link.A].Path.path_id, link.TrackA));
            ends.Add((entries[link.B].Path.path_id, link.TrackB));
        }
        return ends;
    }

    /// <summary>Collect node paths with tracks (railway/monorail/street LRT).</summary>
    private static List<TrackEntry> CollectEntries(int nodeId, List<PathSummary> paths)
    {
        var entries = new List<TrackEntry>();
        foreach (PathSummary path in paths)
        {
            if (!IsTrackType(path.path_type) && !LrtRoadParts.IsEmbedded(path.path_variant)) continue;
            if (!RoadIntersectionMeshGenerator.TryGetPathDirectionFromNode(nodeId, path, out Vector2 nodePoint, out Vector2 directionPoint)) continue;
            entries.Add(new TrackEntry
            {
                Path = path,
                NodeIsStart = path.start_node_id == nodeId,
                LogicalDirection = (directionPoint - nodePoint).normalized,
                Laterals = LrtRoadParts.TrackLaterals(path),
                Platform = (MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.platform_width ?? 0f) > 0f,
            });
        }
        return entries;
    }

    /// <summary>
    /// Inspect all movements entries[a]→entries[b], returning connected track-end pairs
    /// (see class comments for rules).
    /// </summary>
    private static List<Link> ResolveLinks(List<TrackEntry> entries)
    {
        var links = new List<Link>();
        // Deflection threshold compared by cos (angle ≤ threshold ⇔ cos(angle) ≥ cos(threshold)).
        float cosLimit = Mathf.Cos(LocalConstants.RAIL_MAX_TURN_ANGLE_DEG * Mathf.Deg2Rad);
        for (int a = 0; a < entries.Count; a++)
        {
            for (int b = 0; b < entries.Count; b++)
            {
                if (a == b) continue;
                TrackEntry from = entries[a];
                TrackEntry to = entries[b];
                if (!RailsConnect(from.Path.path_type, from.Path.path_variant, to.Path.path_type, to.Path.path_variant)) continue;

                // Turn limits apply only between guideway paths. Street LRT may turn, matching backend rules.
                // Connectable pairs are both LRT or both non-LRT, so inspecting either classifies the pair.
                bool turnRestricted = !LrtRoadParts.IsLrt(from.Path.path_variant)
                    || (IsTrackType(from.Path.path_type) && IsTrackType(to.Path.path_type));
                if (turnRestricted && Vector2.Dot(-from.LogicalDirection, to.LogicalDirection) < cosLimit) continue;

                if (from.Laterals.Count > 2 || to.Laterals.Count > 2)
                {
                    if (a < b) AddLateralOrderLinks(links, from, a, to, b);
                    continue;
                }

                // End-node arrival follows geometric forward; start-node departure follows forward.
                bool arriveForward = !from.NodeIsStart;
                bool departForward = to.NodeIsStart;
                if (!CanRun(from, arriveForward) || !CanRun(to, departForward)) continue;
                AddLink(links, a, TrackPath.TrackIndexForTravel(from.Laterals.Count, arriveForward),
                    b, TrackPath.TrackIndexForTravel(to.Laterals.Count, departForward));
            }
        }
        return links;
    }

    /// <summary>Whether a path permits the specified direction for rail connection (forward=geometric start→end).</summary>
    private static bool CanRun(TrackEntry entry, bool forward)
    {
        return entry.Platform || VehicleManager.DirectionAllowsEntry(entry.Path.travel_direction, forward);
    }

    /// <summary>
    /// Match pairs involving at least three tracks monotonically by travel-frame lateral position
    /// (positive rightward from from to to); merge on the lower-count side, connecting every end.
    /// </summary>
    private static void AddLateralOrderLinks(List<Link> links, TrackEntry from, int a, TrackEntry to, int b)
    {
        // Laterals use geometric frame; arriving start-node paths/departing end-node paths reverse left/right in travel frame.
        List<int> orderFrom = SortByTravelLateral(from, from.NodeIsStart ? -1f : 1f);
        List<int> orderTo = SortByTravelLateral(to, to.NodeIsStart ? 1f : -1f);
        int count = Mathf.Max(orderFrom.Count, orderTo.Count);
        for (int k = 0; k < count; k++)
        {
            float t = count == 1 ? 0f : (float)k / (count - 1);
            AddLink(links, a, orderFrom[Mathf.RoundToInt(t * (orderFrom.Count - 1))],
                b, orderTo[Mathf.RoundToInt(t * (orderTo.Count - 1))]);
        }
    }

    /// <summary>Sort track indices ascending by travel-frame lateral (geometric lateral × sign).</summary>
    private static List<int> SortByTravelLateral(TrackEntry entry, float sign)
    {
        var order = new List<int>(entry.Laterals.Count);
        for (int i = 0; i < entry.Laterals.Count; i++) order.Add(i);
        order.Sort((x, y) => (entry.Laterals[x] * sign).CompareTo(entry.Laterals[y] * sign));
        return order;
    }

    /// <summary>Add a track-end pair, deduplicating identical pairs from reverse movements.</summary>
    private static void AddLink(List<Link> links, int a, int trackA, int b, int trackB)
    {
        if (a > b)
        {
            (a, b) = (b, a);
            (trackA, trackB) = (trackB, trackA);
        }
        foreach (Link link in links)
        {
            if (link.A == a && link.TrackA == trackA && link.B == b && link.TrackB == trackB) return;
        }
        links.Add(new Link { A = a, TrackA = trackA, B = b, TrackB = trackB });
    }

    /// <summary>
    /// Add a rail connector from (from, trackFrom) to (to, trackTo).
    /// Smoothly connect cut track centers with cubic Bézier handles following endpoint tangents.
    /// </summary>
    private static void AddConnector(List<RailConnectorSpec> specs, TrackEntry from, int trackFrom, TrackEntry to, int trackTo, Vector3 nodePosition, bool horizontalPlane)
    {
        // Travel from from into node, then outward to to. S curves tilt track directions from centerlines.
        Vector2 travelIn = -TrackDirection(from, trackFrom);
        Vector2 travelOut = TrackDirection(to, trackTo);
        Vector2 start = TrackEndPoint(from, trackFrom);
        Vector2 end = TrackEndPoint(to, trackTo);

        float dist = Vector2.Distance(start, end);
        if (dist < MIN_CONNECTOR_LENGTH) return;

        Vector2 p1 = start + travelIn * (dist / 3f);
        Vector2 p2 = end - travelOut * (dist / 3f);

        int sampleCount = Mathf.Clamp(Mathf.CeilToInt(dist / SAMPLE_INTERVAL) + 1, 4, 24);
        List<Vector3> points = new List<Vector3>(sampleCount);
        for (int s = 0; s < sampleCount; s++)
        {
            float t = (float)s / (sampleCount - 1);
            Vector2 pt = PolylineUtils.CubicBezierPoint(start, p1, p2, end, t);
            points.Add(ToLocalTrackPoint(pt, nodePosition, horizontalPlane));
        }

        // Select the larger variant's rail prefab for appearance, like sidewalk connectors.
        int fromVariant = from.Path.path_variant;
        int toVariant = to.Path.path_variant;
        TrackEntry prefabSource = fromVariant >= toVariant ? from : to;
        bool embedded = LrtRoadParts.IsEmbedded(fromVariant) || LrtRoadParts.IsEmbedded(toVariant);
        if (embedded) prefabSource = LrtRoadParts.IsEmbedded(fromVariant) ? from : to;

        specs.Add(new RailConnectorSpec(points, prefabSource.Path.path_variant, prefabSource.Path.path_type) {
            StartDirection = travelIn, EndDirection = travelOut, StartSlope = -from.Slope, EndSlope = to.Slope,
            EmbeddedLrt = embedded,
            HasWire = HasOverheadWire(fromVariant) && HasOverheadWire(toVariant),
            StartRailBase = embedded ? LrtRoadParts.RailTopAboveSpline(fromVariant) - LrtRoadParts.EMBEDDED_RAIL_TOP : RoadPartHeights.BODY_ABOVE_SPLINE,
            EndRailBase = embedded ? LrtRoadParts.RailTopAboveSpline(toVariant) - LrtRoadParts.EMBEDDED_RAIL_TOP : RoadPartHeights.BODY_ABOVE_SPLINE,
            StartWireOffset = LrtRoadParts.IsEmbedded(fromVariant) ? 0 : -.28f,
            EndWireOffset = LrtRoadParts.IsEmbedded(toVariant) ? 0 : -.28f
        });
    }

    private static bool HasOverheadWire(int variant)
    {
        var spacing = MasterDataStore.Instance?.GetPathMaster(variant)?.wire_spacing;
        return spacing.HasValue && spacing.Value > 0f;
    }

    /// <summary>
    /// Track-center XZ at cut. Laterals match main-line geometric frame
    /// (TrackPath.GetTrackCenterOffset / LrtRoadParts.TrackLaterals), precisely aligning rails at cuts.
    /// </summary>
    private static Vector2 TrackEndPoint(TrackEntry entry, int track)
    {
        Vector2 geometricForward = entry.NodeIsStart ? entry.Direction : -entry.Direction;
        return entry.ConnectionPoint + RightOf(geometricForward) * entry.Laterals[track];
    }

    /// <summary>Direction of track at cut, away from node into path (including S-curve lateral changes).</summary>
    private static Vector2 TrackDirection(TrackEntry entry, int track)
    {
        float slope = entry.LateralSlopes != null && track < entry.LateralSlopes.Length ? entry.LateralSlopes[track] : 0f;
        if (Mathf.Abs(slope) < 1e-6f) return entry.Direction;
        Vector2 geometricForward = entry.NodeIsStart ? entry.Direction : -entry.Direction;
        return (entry.Direction + RightOf(geometricForward) * slope).normalized;
    }

    /// <summary>
    /// Whether two tracks connect directly at a node without a plate. Requires exactly two track paths,
    /// connectable rails, centerline bend ≤ TRACK_JOIN_MAX_BEND_DEG, and matching node track laterals/widths
    /// (including S shifts). No trimming keeps wires/girders/platforms continuous.
    /// connectionPoints are node-side endpoints (cut=end). Null if conditions fail.
    /// </summary>
    public static TrackContinuousJoin TryContinuousJoin(int nodeId, List<PathSummary> paths, Func<PathSummary, List<Vector3>> pointResolver,
        Dictionary<int, TrackEndTransition.EndShift> shifts, out Dictionary<int, Vector3> connectionPoints)
    {
        connectionPoints = null;
        if (paths == null || paths.Count != 2) return null;
        PathSummary a = paths[0], b = paths[1];
        if (!IsTrackType(a.path_type) || !IsTrackType(b.path_type)
            || !RailsConnect(a.path_type, a.path_variant, b.path_type, b.path_variant)) return null;
        bool aStart = a.start_node_id == nodeId, bStart = b.start_node_id == nodeId;
        if (aStart == (a.end_node_id == nodeId) || bStart == (b.end_node_id == nodeId)) return null;

        if (!TryNodeEnd(a, aStart, pointResolver, out Vector3 nodeA, out Vector2 outA)
            || !TryNodeEnd(b, bStart, pointResolver, out Vector3 nodeB, out Vector2 outB)) return null;
        if (Vector2.Dot(outA, outB) > -Mathf.Cos(LocalConstants.TRACK_JOIN_MAX_BEND_DEG * Mathf.Deg2Rad)) return null;

        List<float> lateralsA = LrtRoadParts.TrackLaterals(a), lateralsB = LrtRoadParts.TrackLaterals(b);
        int count = lateralsA.Count;
        if (count != lateralsB.Count) return null;
        shifts.TryGetValue(a.path_id, out var shiftA);
        shifts.TryGetValue(b.path_id, out var shiftB);
        bool aligned = aStart != bStart;
        for (int i = 0; i < count; i++)
        {
            int j = aligned ? i : count - 1 - i;
            float lateralA = lateralsA[i] + (shiftA != null ? shiftA.Shift(i, 0f) : 0f);
            float lateralB = lateralsB[j] + (shiftB != null ? shiftB.Shift(j, 0f) : 0f);
            if (Mathf.Abs(lateralA - (aligned ? lateralB : -lateralB)) > LocalConstants.TRACK_JOIN_LATERAL_TOLERANCE) return null;
        }
        float widthA = MasterWidth(a) + (shiftA != null ? 2f * shiftA.OuterShift(0f) : 0f);
        float widthB = MasterWidth(b) + (shiftB != null ? 2f * shiftB.OuterShift(0f) : 0f);
        if (Mathf.Abs(widthA - widthB) > LocalConstants.TRACK_JOIN_LATERAL_TOLERANCE) return null;

        connectionPoints = new Dictionary<int, Vector3> { { a.path_id, nodeA }, { b.path_id, nodeB } };
        return new TrackContinuousJoin { PathA = a.path_id, PathB = b.path_id };
    }

    /// <summary>Node-side path endpoint and inward-to-path centerline direction (XZ).</summary>
    private static bool TryNodeEnd(PathSummary path, bool nodeIsStart, Func<PathSummary, List<Vector3>> pointResolver, out Vector3 nodeEnd, out Vector2 outward)
    {
        nodeEnd = default;
        outward = default;
        var source = pointResolver != null ? pointResolver(path) : IntersectionPolygonUtil.SamplePlan(path);
        if (source == null || source.Count < 2) return false;
        var points = new List<Vector3>(source);
        if (!nodeIsStart) points.Reverse();
        nodeEnd = points[0];
        outward = IntersectionPolygonUtil.AtDistance(points, 0f).Direction;
        return outward.sqrMagnitude > 1e-6f;
    }

    private static float MasterWidth(PathSummary path)
    {
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(path.path_variant) : null;
        return master != null && master.width > 0f ? master.width : path.width;
    }

    /// <summary>
    /// Return rightward travel vector for a 2D XZ direction.
    /// Same as 3D Cross(up, forward)=right; Vector2 stores (x,z).
    /// </summary>
    private static Vector2 RightOf(Vector2 direction)
    {
        return new Vector2(direction.y, -direction.x);
    }

    /// <summary>
    /// Convert XZ to node-local position.
    /// Height=reference plane + TRACK_ABOVE_SPLINE, flush with main track-part bottoms.
    /// </summary>
    private static Vector3 ToLocalTrackPoint(Vector2 point, Vector3 nodePosition, bool horizontalPlane)
    {
        float worldY = RoadIntersectionMeshGenerator.SampleBaseSurfaceY(point.x, point.y, nodePosition, horizontalPlane) + RoadPartHeights.TRACK_ABOVE_SPLINE;
        return new Vector3(point.x - nodePosition.x, worldY - nodePosition.y, point.y - nodePosition.z);
    }
}
