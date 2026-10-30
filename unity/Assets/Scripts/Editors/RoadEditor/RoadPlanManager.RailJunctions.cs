using System.Collections.Generic;
using UnityEngine;

public partial class RoadPlanManager
{
    private bool IsRailJunctionTarget => PathType == Constants.PATH_TRACK || PathType == Constants.PATH_MONORAIL;

    private bool TryProjectRailTarget(Vector3 position, float tolerance,
        out Vector3 point, out Vector3 tangent, out GeneratedModels.PathSummary target, bool useOffsets = false)
    {
        point = position;
        tangent = Vector3.zero;
        target = null;
        if (pathManager == null) return false;
        var seen = new HashSet<int>();
        float best = tolerance;
        float radius = snapThreshold;
        if (useOffsets && LateralOffsets != null)
            foreach (float offset in LateralOffsets) radius = Mathf.Max(radius, snapThreshold + Mathf.Abs(offset));
        var segments = pathManager.QueryNearbySegments(position, radius);
        if (segments == null) return false;
        foreach (var segment in segments)
        {
            var path = segment.path;
            if (path == null || path.path_id == pathId || path.path_type != PathType || !seen.Add(path.path_id)) continue;
            if (!RailJunctionGeometry.TryProject(path, position, out Vector3 projected, out Vector3 direction)) continue;
            if (!IsRailSnapPointVisible(path, projected)) continue;
            var offsets = useOffsets && LateralOffsets != null && LateralOffsets.Length > 0 ? LateralOffsets : new[] { 0f };
            Vector3 normal = new Vector3(-direction.z, 0, direction.x);
            foreach (float offset in offsets)
            for (int sign = -1; sign <= 1; sign += 2)
            {
                Vector3 center = projected + normal * offset * sign;
                float distance = Utils.XZDistance(center, position);
                if (distance > best) continue;
                best = distance;
                point = center;
                tangent = direction;
                target = path;
            }
        }
        return target != null;
    }

    private bool IsRailSnapPointVisible(GeneratedModels.PathSummary path, Vector3 position)
    {
        if (!UndergroundView.IsActive) return true;
        // Analytic projection Y is 0, so test depth against the centerline shared with rendering.
        // Do not classify relative-height-0 non-following tracks as above ground from anchor heights alone; they may enter hills.
        if (!pathManager.TryGetUntrimmedPolylineShared(path.path_id, out var points)) return false;
        float best = float.PositiveInfinity;
        Vector3 closest = position;
        for (int i = 1; i < points.Count; i++)
        {
            Vector3 candidate = PolylineUtils.ClosestPointOnSegmentXZ(position, points[i - 1], points[i]);
            float distance = Utils.XZDistanceSquared(position, candidate);
            if (distance < best) { best = distance; closest = candidate; }
        }
        return best < float.PositiveInfinity && TerrainInputHandler.IsPathSnapPointVisible(path, closest);
    }

    private bool TryGetRailHandleDirection(Vector3 position, Vector3 current, out Vector3 direction)
    {
        direction = Vector3.zero;
        if (!TryProjectRailTarget(position, Constants.RAIL_JUNCTION_POSITION_TOLERANCE,
            out _, out Vector3 tangent, out _, useOffsets: true)) return false;
        direction = Vector3.Dot(current, tangent) >= 0 ? tangent : -tangent;
        return true;
    }

    /// <summary>Plane through a branch/merge contact, sloping only along the main line tangent at the main line grade.</summary>
    private struct JunctionPlane
    {
        public Vector3 origin;
        public Vector3 tangent;
        public float grade;

        public float HeightAt(Vector3 position) => origin.y + grade * Vector3.Dot(position - origin, tangent);
    }

    /// <summary>
    /// Aligns anchor heights within RAIL_JUNCTION_PLANE_LENGTH of a track branch/merge end to the main line plane.
    /// Backend confirmation requires the siding to lie in the same plane as the main line there (geo_rail_junctions);
    /// freely editable heights would require manual alignment. Targets the far anchor of any section starting before
    /// the distance from the end reaches this length, always including point 2. Measures anchor chords, which are no longer than curves,
    /// conservatively constraining one extra point near the boundary. Excludes variants without height editing, whose profiles follow terrain/connection correction.
    /// Call immediately after anchor insertion/movement/deletion and before recalculating controls. Do not call when restoring existing paths or applying proposals,
    /// preserving saved/proposed geometry until the next edit.
    /// </summary>
    private void ConstrainJunctionPlaneHeights()
    {
        if (!IsRailJunctionTarget || !IsHeightEditable() || points.Count < 2) return;
        int last = points.Count - 1;
        JunctionPlane endPlane = default;
        bool start = TryGetJunctionPlane(0, out JunctionPlane startPlane);
        // The last point during placement is not an endpoint (the endpoint is determined on shape finalization).
        bool end = points[last].role == PathPointRole.End && TryGetJunctionPlane(last, out endPlane);
        if (start) ConstrainToJunctionPlane(0, 1, startPlane, end ? last : -1);
        if (end) ConstrainToJunctionPlane(last, -1, endPlane, start ? 0 : -1);
    }

    /// <summary>Aligns anchors from end in direction step until section starts reach RAIL_JUNCTION_PLANE_LENGTH. stop is the opposite contact.</summary>
    private void ConstrainToJunctionPlane(int end, int step, JunctionPlane plane, int stop)
    {
        float distance = 0f;
        for (int i = end + step; i >= 0 && i < points.Count && i != stop
            && distance < Constants.RAIL_JUNCTION_PLANE_LENGTH; i += step)
        {
            distance += Utils.XZDistance(points[i - step].position, points[i].position);
            var point = points[i];
            float height = plane.HeightAt(point.position);
            if (Mathf.Abs(point.position.y - height) <= 1e-4f) continue;
            point.position.y = height;
            points[i] = point;
            // As with anchor movement, release fixed heights through both neighbors whose automatic grades change.
            ReleaseControlHeights(i - 1, i + 1);
        }
    }

    /// <summary>
    /// Returns the main line plane if endpoint anchor index meets the same track type at the same height in a branch/merge.
    /// Excludes simple endpoint extensions in opposite directions, which the backend also exempts from a common plane. For a main line
    /// split into two paths at a node, use the side that is not an extension.
    /// </summary>
    private bool TryGetJunctionPlane(int index, out JunctionPlane plane)
    {
        plane = default;
        if (pathManager == null) return false;
        Vector3 position = points[index].position;
        Vector3 inward = points[index == 0 ? 1 : index - 1].position - position;
        inward.y = 0f;
        var segments = pathManager.QueryNearbySegments(position, snapThreshold);
        if (segments == null) return false;
        var seen = new HashSet<int>();
        foreach (var segment in segments)
        {
            var path = segment.path;
            if (path == null || path.path_id == pathId || path.path_type != PathType || !seen.Add(path.path_id)) continue;
            if (!RailJunctionGeometry.TryProject(path, position, out Vector3 contact, out Vector3 tangent)) continue;
            if (Utils.XZDistance(contact, position) > Constants.RAIL_JUNCTION_POSITION_TOLERANCE) continue;
            // Grade-separated crossings sharing only XZ are not connections. Tolerance matches backend connection correction.
            if (Mathf.Abs(RailSurfaceHeight(path, contact) - position.y) > RoadShapeGeometry.AbsoluteHeightTolerance) continue;
            if (IsRailExtension(path, contact, tangent, inward)) continue;
            plane = new JunctionPlane { origin = position, tangent = tangent, grade = RailGrade(path, contact, tangent) };
            return true;
        }
        return false;
    }

    /// <summary>Whether contact is at the other's endpoint and both extend oppositely. Analytic tangents point start→end, so inward direction is tangent at start and reversed at end.</summary>
    private static bool IsRailExtension(GeneratedModels.PathSummary path, Vector3 contact, Vector3 tangent, Vector3 inward)
    {
        var start = path.GetStartPointList();
        var end = path.GetEndPointList();
        if (start != null && start.Count >= 2 && Utils.XZDistance(contact, new Vector3(start[0], 0f, start[start.Count - 1]))
            <= Constants.RAIL_JUNCTION_POSITION_TOLERANCE) return Vector3.Dot(tangent, inward) < 0f;
        if (end != null && end.Count >= 2 && Utils.XZDistance(contact, new Vector3(end[0], 0f, end[end.Count - 1]))
            <= Constants.RAIL_JUNCTION_POSITION_TOLERANCE) return Vector3.Dot(-tangent, inward) < 0f;
        return false;
    }

    /// <summary>Absolute track surface elevation. Uses the same source as snapping (saved design elevation, otherwise interpolated anchor relative Y).</summary>
    private static float RailSurfaceHeight(GeneratedModels.PathSummary path, Vector3 position)
    {
        return Utils.GetPathSurfaceHeight(position) + path.GetRelativeHeightAtPoint(position.x, position.z);
    }

    /// <summary>Main line tangent grade, measured by the secant across RAIL_JUNCTION_PLANE_LENGTH on both sides of the contact where the backend fits its plane.</summary>
    private static float RailGrade(GeneratedModels.PathSummary path, Vector3 contact, Vector3 tangent)
    {
        float length = Constants.RAIL_JUNCTION_PLANE_LENGTH;
        RailJunctionGeometry.TryProject(path, contact + tangent * length, out Vector3 ahead, out _);
        RailJunctionGeometry.TryProject(path, contact - tangent * length, out Vector3 behind, out _);
        float run = Vector3.Dot(ahead - behind, tangent);
        return run > 1e-3f ? (RailSurfaceHeight(path, ahead) - RailSurfaceHeight(path, behind)) / run : 0f;
    }
}
