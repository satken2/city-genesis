using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Geometry utilities for 2D polygons: closed rings without a repeated start vertex at the end.
/// Treat Vector2 as horizontal projection with x=world X and y=world Z.
/// Containment uses even-odd ray casting, producing consistent results for non-convex rings as well.
/// See <see cref="PolylineUtils"/> for segment calculations.
/// </summary>
public static class PolygonUtils
{
    /// <summary>
    /// Whether a point lies inside the polygon. Boundary-point classification is undefined;
    /// use the boundaryTolerance overload to include the boundary.
    /// </summary>
    public static bool Contains(IReadOnlyList<Vector2> polygon, Vector2 point)
    {
        bool inside = false;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 vi = polygon[i];
            Vector2 vj = polygon[j];
            if (CrossesRay(vi.x, vi.y, vj.x, vj.y, point.x, point.y)) inside = !inside;
        }
        return inside;
    }

    /// <summary>Also treat points within boundaryTolerance of the boundary as inside, for layouts that must include points placed on edges.</summary>
    public static bool Contains(IReadOnlyList<Vector2> polygon, Vector2 point, float boundaryTolerance)
    {
        float toleranceSq = boundaryTolerance * boundaryTolerance;
        bool inside = false;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 vi = polygon[i];
            Vector2 vj = polygon[j];
            if (PolylineUtils.DistanceToSegmentSq(point, vj, vi) < toleranceSq) return true;
            if (CrossesRay(vi.x, vi.y, vj.x, vj.y, point.x, point.y)) inside = !inside;
        }
        return inside;
    }

    /// <summary>For vertices in arrays with [i][0]=X, [i][1]=Z, the unchanged parcel/district polygon response format.</summary>
    public static bool Contains(float[][] polygon, float x, float z)
    {
        bool inside = false;
        int n = polygon.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if (CrossesRay(polygon[i][0], polygon[i][1], polygon[j][0], polygon[j][1], x, z)) inside = !inside;
        }
        return inside;
    }

    /// <summary>For vertices in List&lt;List&lt;float&gt;&gt; format, as in resource polygon responses.</summary>
    public static bool Contains(List<List<float>> polygon, float x, float z)
    {
        bool inside = false;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if (CrossesRay(polygon[i][0], polygon[i][1], polygon[j][0], polygon[j][1], x, z)) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// Append X coordinates of intersections counted by <see cref="Contains"/>'s ray along horizontal z to crossings, without sorting.
    /// For points at the same z, parity of the number of intersections greater than x matches Contains.
    /// For many points on the same row, such as a grid, this avoids scanning every edge per point.
    /// </summary>
    public static void AppendRayCrossings(IReadOnlyList<Vector2> polygon, float z, List<float> crossings)
    {
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 vi = polygon[i];
            Vector2 vj = polygon[j];
            if ((vi.y > z) != (vj.y > z)) crossings.Add(RayCrossingX(vi.x, vi.y, vj.x, vj.y, z));
        }
    }

    /// <summary>
    /// Whether the +X ray from (x, z) intersects edge i-j.
    /// Use a half-open height interval to avoid double counting when the ray passes exactly through a vertex.
    /// </summary>
    private static bool CrossesRay(float xi, float zi, float xj, float zj, float x, float z)
    {
        return ((zi > z) != (zj > z)) && (x < RayCrossingX(xi, zi, xj, zj, z));
    }

    /// <summary>X coordinate where edge i-j intersects horizontal z. Call only when the edge spans z.</summary>
    private static float RayCrossingX(float xi, float zi, float xj, float zj, float z)
    {
        return (xj - xi) * (z - zi) / (zj - zi) + xi;
    }

    /// <summary>Signed area using the shoelace formula; positive means counterclockwise (CCW). Used to normalize winding.</summary>
    public static float SignedArea(IReadOnlyList<Vector2> polygon)
    {
        float area = 0f;
        int n = polygon.Count;
        for (int i = 0; i < n; i++)
        {
            Vector2 a = polygon[i];
            Vector2 b = polygon[(i + 1) % n];
            area += a.x * b.y - b.x * a.y;
        }
        return area * 0.5f;
    }

    /// <summary>Shortest distance from a point to any polygon boundary edge, unsigned regardless of containment.</summary>
    public static float DistanceToBoundary(IReadOnlyList<Vector2> polygon, Vector2 point)
    {
        float minDistanceSq = float.MaxValue;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float distanceSq = PolylineUtils.DistanceToSegmentSq(point, polygon[j], polygon[i]);
            if (distanceSq < minDistanceSq) minDistanceSq = distanceSq;
        }
        return Mathf.Sqrt(minDistanceSq);
    }
}
