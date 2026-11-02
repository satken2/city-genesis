using System.Collections.Generic;
using UnityEngine;

/// <summary>Road editor section geometry. Do not include render-only lift in saved coordinates or collision checks.</summary>
public static class RoadShapeGeometry
{
    // A span follows terrain when both anchors sit at the variant base_height within this tolerance (core UNDERGROUND_REL_Y_EPS).
    public const float GroundSpanTolerance = 0.01f;
    // Two paths touch in height when their absolute elevations differ by less than this (core ELEVATION_INTERSECTION_EPSILON_ABS).
    // Absolute elevations include terrain quantization/sampler differences.
    public const float AbsoluteHeightTolerance = 0.5f;

    public struct Span
    {
        public Vector3 start, controlOut, controlIn, end;
        public float relativeStart, relativeEnd;
        public bool curved, follows;

        public Vector3 Sample(float t, Vector3 origin = default, float surfaceOffset = 0f)
        {
            Vector3 point = curved
                ? PolylineUtils.CubicBezierPoint(start - origin, controlOut - origin, controlIn - origin, end - origin, t)
                : Vector3.Lerp(start - origin, end - origin, t);
            if (follows)
                point.y = Utils.GetPathSurfaceHeight(point + origin) - origin.y
                    + Mathf.Lerp(relativeStart, relativeEnd, t) + surfaceOffset;
            return point;
        }

        public int DenseSteps => Mathf.Clamp(Mathf.CeilToInt((curved
            ? Vector3.Distance(start, controlOut) + Vector3.Distance(controlOut, controlIn) + Vector3.Distance(controlIn, end)
            : Vector3.Distance(start, end)) / 2f), curved ? 16 : 1, 4096);
    }

    public struct ElevationSegment
    {
        public Vector3 a, b;
        public float relYA, relYB, absYA, absYB;
        public bool follows;

        /// <summary>Absolute elevation at t. Following spans use the reference surface at the point, not the interpolated polyline.</summary>
        public float HeightAt(float t, float groundAtCrossing)
            => follows ? groundAtCrossing + Mathf.Lerp(relYA, relYB, t) : Mathf.Lerp(absYA, absYB, t);
    }

    public static Span CreateSpan(PathBuildPoint a, PathBuildPoint b, bool curved, bool followsTerrain, float groundRelativeHeight)
    {
        float relativeA = a.position.y - Utils.GetPathSurfaceHeight(a.position);
        float relativeB = b.position.y - Utils.GetPathSurfaceHeight(b.position);
        return new Span
        {
            start = a.position + Vector3.up * a.embankmentHeight,
            end = b.position + Vector3.up * b.embankmentHeight,
            controlOut = a.control_out.HasValue ? a.control_out.Value + Vector3.up * a.embankmentHeight
                : Vector3.Lerp(a.position + Vector3.up * a.embankmentHeight, b.position + Vector3.up * b.embankmentHeight, 1f / 3f),
            controlIn = b.control_in.HasValue ? b.control_in.Value + Vector3.up * b.embankmentHeight
                : Vector3.Lerp(a.position + Vector3.up * a.embankmentHeight, b.position + Vector3.up * b.embankmentHeight, 2f / 3f),
            relativeStart = relativeA + a.embankmentHeight,
            relativeEnd = relativeB + b.embankmentHeight,
            curved = curved,
            follows = followsTerrain && Mathf.Abs(relativeA - groundRelativeHeight) <= GroundSpanTolerance
                && Mathf.Abs(relativeB - groundRelativeHeight) <= GroundSpanTolerance,
        };
    }

    public static List<ElevationSegment> SampleElevationSegments(IReadOnlyList<Span> spans, int curvedSteps)
    {
        var result = new List<ElevationSegment>();
        foreach (var span in spans)
        {
            int steps = span.curved ? curvedSteps : 1;
            Vector3 previous = span.Sample(0f);
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                Vector3 next = span.Sample(t);
                // a/b Y values are relative heights. Store non-following absolute profiles separately so intermediate terrain does not bend them.
                float relativeA = span.follows ? Mathf.Lerp(span.relativeStart, span.relativeEnd, (float)(i - 1) / steps)
                    : previous.y - Utils.GetPathSurfaceHeight(previous);
                float relativeB = span.follows ? Mathf.Lerp(span.relativeStart, span.relativeEnd, t)
                    : next.y - Utils.GetPathSurfaceHeight(next);
                result.Add(new ElevationSegment
                {
                    a = new Vector3(previous.x, relativeA, previous.z),
                    b = new Vector3(next.x, relativeB, next.z),
                    relYA = relativeA, relYB = relativeB, absYA = previous.y, absYB = next.y, follows = span.follows,
                });
                previous = next;
            }
        }
        return result;
    }

    public static List<float> ControlToRelativeList(Vector3? control, Vector3 anchor)
    {
        if (!control.HasValue) return null;
        Vector3 point = control.Value;
        return new List<float> { point.x, point.y - Utils.GetPathSurfaceHeight(anchor), point.z };
    }

    public static Vector3 RestoreControl(float x, float relativeY, float z, Vector3 anchor)
        => new Vector3(x, Utils.GetPathSurfaceHeight(anchor) + relativeY, z);

    public static bool SegmentsIntersectXZ(Vector3 a1, Vector3 a2, Vector3 b1, Vector3 b2, out float tA, out float tB)
    {
        tA = tB = 0f;
        float ax = a2.x - a1.x, az = a2.z - a1.z, bx = b2.x - b1.x, bz = b2.z - b1.z;
        float denominator = ax * bz - az * bx;
        if (Mathf.Abs(denominator) < 1e-6f) return false;
        float dx = b1.x - a1.x, dz = b1.z - a1.z;
        tA = (dx * bz - dz * bx) / denominator;
        tB = (dx * az - dz * ax) / denominator;
        return tA >= -1e-4f && tA <= 1f + 1e-4f && tB >= -1e-4f && tB <= 1f + 1e-4f;
    }

    /// <summary>
    /// Whether two segments collide at their XZ crossing, comparing absolute elevations (core validate_path_elevation_intersections).
    /// Only pairs that connect (sameHeightAllowed: their node masks share a bit) may meet at the same height as an
    /// intersection or level crossing; any other pair collides whenever the occupied ranges [y, y + height] overlap.
    /// </summary>
    public static bool ElevationOverlaps(ElevationSegment a, float heightA, ElevationSegment b, float heightB, bool sameHeightAllowed)
    {
        if (!SegmentsIntersectXZ(a.a, a.b, b.a, b.b, out float tA, out float tB)) return false;
        float ground = a.follows || b.follows ? Utils.GetPathSurfaceHeight(Vector3.Lerp(a.a, a.b, tA)) : 0f;
        float ya = a.HeightAt(tA, ground), yb = b.HeightAt(tB, ground);
        if (sameHeightAllowed && Mathf.Abs(ya - yb) < AbsoluteHeightTolerance) return false;
        return ya < yb + heightB && ya + heightA > yb;
    }
}
