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

    private bool TryGetRailHandleDirection(Vector3 position, Vector3 current, out Vector3 direction)
    {
        direction = Vector3.zero;
        if (!TryProjectRailTarget(position, Constants.RAIL_JUNCTION_POSITION_TOLERANCE,
            out _, out Vector3 tangent, out _, useOffsets: true)) return false;
        direction = Vector3.Dot(current, tangent) >= 0 ? tangent : -tangent;
        return true;
    }
}
