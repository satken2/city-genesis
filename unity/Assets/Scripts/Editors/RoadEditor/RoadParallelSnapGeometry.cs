using UnityEngine;

/// <summary>XZ calculations for parallel assistance. The caller's construction/drag logic determines height.</summary>
public static class RoadParallelSnapGeometry
{
    public static Vector3 Project(Vector3 position, Vector3 origin, Vector3 direction)
    {
        direction.y = 0;
        float lengthSquared = direction.sqrMagnitude;
        if (lengthSquared < 1e-6f) return position;
        Vector3 delta = position - origin;
        delta.y = 0;
        Vector3 result = origin + direction * (Vector3.Dot(delta, direction) / lengthSquared);
        result.y = position.y;
        return result;
    }

    public static float DistanceXZ(Vector3 a, Vector3 b)
    {
        float x = a.x - b.x, z = a.z - b.z;
        return (float)System.Math.Sqrt(x * x + z * z);
    }

    public static float SegmentDistance(Vector3 position, Vector3 a, Vector3 b)
    {
        return DistanceXZ(position, PolylineUtils.ClosestPointOnSegmentXZ(position, a, b));
    }

    /// <summary>
    /// Projects onto an endpoint perpendicular before placing the start, or a parallel line afterward.
    /// End alignment corrects only the longitudinal position on the parallel line, preserving road spacing.
    /// </summary>
    public static bool TrySnap(Vector3 raw, Vector3? anchor, Vector3 a, Vector3 b,
        Vector3 start, Vector3 end, bool alignEnds, float threshold,
        out Vector3 snapped, out bool aligned, out Vector3 alignedEnd)
    {
        snapped = raw;
        aligned = false;
        alignedEnd = default;
        Vector3 direction = b - a;
        direction.y = 0;
        if (direction.sqrMagnitude < 1e-6f) return false;
        if (anchor.HasValue)
        {
            snapped = Project(raw, anchor.Value, direction);
            if (DistanceXZ(raw, snapped) > threshold) return false;
        }
        else if (!alignEnds) return false;

        if (alignEnds)
        {
            Vector3 perpendicular = new Vector3(-direction.z, 0, direction.x);
            Vector3 atStart = Project(snapped, start, perpendicular);
            Vector3 atEnd = Project(snapped, end, perpendicular);
            float startDistance = DistanceXZ(snapped, atStart);
            float endDistance = DistanceXZ(snapped, atEnd);
            if (Mathf.Min(startDistance, endDistance) <= threshold)
            {
                aligned = true;
                alignedEnd = startDistance <= endDistance ? start : end;
                snapped = startDistance <= endDistance ? atStart : atEnd;
            }
        }
        return anchor.HasValue || aligned;
    }
}
