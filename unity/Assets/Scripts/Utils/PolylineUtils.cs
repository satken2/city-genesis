using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Geometry utilities for nearest points/distances on segments, cubic Bezier evaluation, polyline directions, and corner rounding.
/// See <see cref="PolygonUtils"/> for closed-ring polygon calculations.
/// </summary>
public static class PolylineUtils
{
    // Lengths at or below 0.0001 coordinate units collapse to the start point: 0.1mm in world space or 0.0001px on screen.
    private const float DEGENERATE_LENGTH_SQ = 1e-8f;

    private static float ProjectionParameter(float dot, float lengthSq)
    {
        return lengthSq <= DEGENERATE_LENGTH_SQ ? 0f : Mathf.Clamp01(dot / lengthSq);
    }

    /// <summary>Nearest-point parameter on a segment (0=start, 1=end), useful for hit tests retaining the position along an edge.</summary>
    public static float ProjectOnSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        Vector2 delta = end - start;
        return ProjectionParameter(Vector2.Dot(point - start, delta), delta.sqrMagnitude);
    }

    /// <summary>XZ projection parameter (0=start, 1=end), for centerline progress while ignoring height differences.</summary>
    public static float ProjectOnSegmentXZ(Vector3 point, Vector3 start, Vector3 end)
    {
        Vector2 delta = new Vector2(end.x - start.x, end.z - start.z);
        Vector2 offset = new Vector2(point.x - start.x, point.z - start.z);
        return ProjectionParameter(Vector2.Dot(offset, delta), delta.sqrMagnitude);
    }

    /// <summary>Point and segment must use the same 2D coordinate system; screen-coordinate units are pixels.</summary>
    public static Vector2 ClosestPointOnSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        Vector2 delta = end - start;
        float t = ProjectionParameter(Vector2.Dot(point - start, delta), delta.sqrMagnitude);
        return start + delta * t;
    }

    /// <summary>Project using full 3D distance, including height. Use the XZ version for horizontal terrain-cursor snapping.</summary>
    public static Vector3 ClosestPointOnSegment(Vector3 point, Vector3 start, Vector3 end)
    {
        Vector3 delta = end - start;
        float t = ProjectionParameter(Vector3.Dot(point - start, delta), delta.sqrMagnitude);
        return start + delta * t;
    }

    /// <summary>Project in XZ only and interpolate Y between endpoints. Return the start for degenerate XZ segments.</summary>
    public static Vector3 ClosestPointOnSegmentXZ(Vector3 point, Vector3 start, Vector3 end)
    {
        Vector2 delta = new Vector2(end.x - start.x, end.z - start.z);
        Vector2 offset = new Vector2(point.x - start.x, point.z - start.z);
        float t = ProjectionParameter(Vector2.Dot(offset, delta), delta.sqrMagnitude);
        return Vector3.Lerp(start, end, t);
    }

    public static float DistanceToSegmentSq(Vector2 point, Vector2 start, Vector2 end)
    {
        return (point - ClosestPointOnSegment(point, start, end)).sqrMagnitude;
    }

    public static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        return Mathf.Sqrt(DistanceToSegmentSq(point, start, end));
    }

    /// <summary>
    /// Intersection of ray origin + t*direction (t&gt;0) and a segment. distance is t in units of direction's length;
    /// segmentT is the segment parameter (0=start, 1=end). False if parallel.
    /// </summary>
    public static bool IntersectRaySegment(Vector2 origin, Vector2 direction, Vector2 start, Vector2 end, out float distance, out float segmentT)
    {
        Vector2 edge = end - start;
        float denominator = direction.x * edge.y - direction.y * edge.x;
        distance = segmentT = 0f;
        if (Mathf.Abs(denominator) < 1e-8f) return false;
        Vector2 offset = start - origin;
        distance = (offset.x * edge.y - offset.y * edge.x) / denominator;
        segmentT = (offset.x * direction.y - offset.y * direction.x) / denominator;
        return distance > 0f && segmentT >= 0f && segmentT <= 1f;
    }

    /// <summary>
    /// Point on a cubic Bezier curve at parameter t.
    /// B(t) = (1-t)^3 P0 + 3(1-t)^2 t P1 + 3(1-t) t^2 P2 + t^3 P3
    /// Road previews (RoadPlanManager) and actual roads (PathManager) share this formula to avoid curve-shape differences.
    /// </summary>
    public static Vector3 CubicBezierPoint(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float u = 1f - t;
        float u2 = u * u;
        float t2 = t * t;
        return u2 * u * p0 + 3f * u2 * t * p1 + 3f * u * t2 * p2 + t2 * t * p3;
    }

    public static Vector2 CubicBezierPoint(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1f - t;
        float u2 = u * u;
        float t2 = t * t;
        return u2 * u * p0 + 3f * u2 * t * p1 + 3f * u * t2 * p2 + t2 * t * p3;
    }

    /// <summary>
    /// Direction at polyline index, averaging adjacent edge unit vectors. Used to place strip-mesh sections smoothly
    /// without abrupt twisting at vertices. Endpoints use only one edge; a 180-degree reversal whose edges cancel
    /// prefers the forward edge, falling back to Vector3.forward if that too is degenerate.
    /// </summary>
    public static Vector3 SmoothedDirection(IReadOnlyList<Vector3> points, int index)
    {
        Vector3 forward = index < points.Count - 1 ? points[index + 1] - points[index] : Vector3.zero;
        Vector3 backward = index > 0 ? points[index] - points[index - 1] : Vector3.zero;
        bool hasForward = forward.sqrMagnitude >= 1e-6f;
        bool hasBackward = backward.sqrMagnitude >= 1e-6f;
        if (!hasForward && !hasBackward) return Vector3.forward;

        Vector3 aggregate = Vector3.zero;
        if (hasForward) aggregate += forward.normalized;
        if (hasBackward) aggregate += backward.normalized;
        if (aggregate.sqrMagnitude < 1e-6f)
        {
            return hasForward ? forward.normalized : backward.normalized;
        }
        return aggregate.normalized;
    }

    // Angles at or below this threshold are nearly straight and pass through without rounding (degrees).
    private const float STRAIGHT_ANGLE_DEG = 4f;

    /// <summary>
    /// Round only polyline corners.
    /// Retain the actual sampled road geometry and chamfer only vertices where direction changes,
    /// using radius clamped to half the adjacent segment lengths, then smooth the interval
    /// with a quadratic Bezier using the original vertex as its control point.
    /// segsPerCorner is the interpolation subdivision count per corner.
    /// </summary>
    public static List<Vector3> RoundCorners(List<Vector3> pts, float radius, int segsPerCorner)
    {
        if (pts == null || pts.Count < 3 || radius <= 0f)
            return pts;
        if (segsPerCorner < 1) segsPerCorner = 1;

        var result = new List<Vector3>(pts.Count + pts.Count / 2);
        result.Add(pts[0]);

        for (int i = 1; i < pts.Count - 1; i++)
        {
            Vector3 prev = pts[i - 1];
            Vector3 cur = pts[i];
            Vector3 next = pts[i + 1];

            Vector3 vIn = cur - prev;
            Vector3 vOut = next - cur;
            float lenIn = vIn.magnitude;
            float lenOut = vOut.magnitude;
            if (lenIn < 1e-4f || lenOut < 1e-4f)
            {
                // Degenerate duplicate point; pass through unchanged.
                result.Add(cur);
                continue;
            }

            Vector3 dirIn = vIn / lenIn;
            Vector3 dirOut = vOut / lenOut;
            float angle = Vector3.Angle(dirIn, dirOut);
            if (angle < STRAIGHT_ANGLE_DEG)
            {
                // Nearly straight; do not round.
                result.Add(cur);
                continue;
            }

            // Chamfer distance: clamp radius to half each adjacent segment length to avoid collapsed or overlapping corners.
            float trim = Mathf.Min(radius, lenIn * 0.5f, lenOut * 0.5f);
            Vector3 pA = cur - dirIn * trim;   // Entry tangent point.
            Vector3 pB = cur + dirOut * trim;  // Exit tangent point.

            // Round with a quadratic Bezier from pA through control point cur to pB, tangent to both segments.
            result.Add(pA);
            for (int s = 1; s < segsPerCorner; s++)
            {
                float t = (float)s / segsPerCorner;
                float u = 1f - t;
                Vector3 b = u * u * pA + 2f * u * t * cur + t * t * pB;
                result.Add(b);
            }
            result.Add(pB);
        }

        result.Add(pts[pts.Count - 1]);
        return result;
    }
}
