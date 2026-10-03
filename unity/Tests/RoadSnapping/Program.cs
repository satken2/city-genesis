using System;
using UnityEngine;

internal static class Program
{
    private static int checks;

    private static void Check(bool value, string message)
    {
        checks++;
        if (!value) throw new Exception(message);
    }

    private static void Near(Vector3 actual, Vector3 expected, string message)
    {
        Check((actual - expected).magnitude < 0.003f, message + $": {actual} != {expected}");
    }

    private static void Main()
    {
        CheckSegmentProjection();
        CheckRailProjection();
        // 平行移動・回転・方向反転でも、横間隔と高さが不変で両端が揃うことを検証する。
        foreach (float degrees in new[] { 0f, 17f, 45f, 90f, 179f, 270f })
        foreach (int sign in new[] { -1, 1 })
        foreach (float offset in new[] { -40f, 20f })
        {
            float angle = degrees * MathF.PI / 180;
            Vector3 d = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * sign;
            Vector3 n = new Vector3(-d.z, 0, d.x);
            Vector3 a = new Vector3(1200, 3, -600), b = a + d * 100;
            Vector3 anchor = a + n * offset;
            Vector3 expected = anchor + d * 60;
            expected.y = 37;
            Vector3 raw = expected + n * 2;
            Check(RoadParallelSnapGeometry.TrySnap(raw, anchor, a, b, a, b, false, 5,
                out var result, out var aligned, out _), "parallel capture");
            Near(result, expected, "parallel preserves lateral offset and height");
            Check(!aligned, "alignment disabled");
            Check(!RoadParallelSnapGeometry.TrySnap(expected + n * 6, anchor, a, b, a, b, true, 5,
                out _, out _, out _), "outside line threshold");
            foreach (Vector3 endpoint in new[] { a, b })
            {
                Vector3 endExpected = endpoint + n * offset;
                endExpected.y = -12;
                raw = endExpected + d * 2 + n * 1;
                Check(RoadParallelSnapGeometry.TrySnap(raw, anchor, a, b, a, b, true, 5,
                    out result, out aligned, out var reference), "endpoint capture");
                Check(aligned, "endpoint alignment active");
                Near(result, endExpected, "aligned endpoint maintains parallel offset");
                Near(reference, endpoint, "uses actual endpoint");
                raw = endExpected + d * 2;
                Check(RoadParallelSnapGeometry.TrySnap(raw, null, a, b, a, b, true, 5,
                    out result, out aligned, out _), "first point alignment");
                Near(result, endExpected, "first point perpendicular projection");
                Check(!RoadParallelSnapGeometry.TrySnap(raw, null, a, b, a, b, false, 5,
                    out _, out _, out _), "first point free with alignment off");
            }
            Check(RoadParallelSnapGeometry.TrySnap(expected, anchor, a, b, a, b, true, 5,
                out result, out aligned, out _), "midpoint parallel");
            Check(!aligned, "midpoint not an endpoint");
            Near(result, expected, "no longitudinal movement away from ends");
            Check(!RoadParallelSnapGeometry.TrySnap(raw, anchor, a, a, a, a, true, 5,
                out _, out _, out _), "degenerate reference rejected");
        }
        var origin = Vector3.zero;
        var far = new Vector3(100, 0, 0);
        Check(MathF.Abs(RoadParallelSnapGeometry.SegmentDistance(new Vector3(103, 100, 4), origin, far) - 5) < 0.001f,
            "search distance uses segment endpoints and ignores terrain height");
        Check(!RoadParallelSnapGeometry.TrySnap(new Vector3(50, 0, 20), null, origin, far, origin, far, true, 5,
            out _, out _, out _), "first point away from both ends is free");
        Console.WriteLine($"Road snapping: {checks} checks passed.");
    }

    private static void CheckRailProjection()
    {
        Vector3 a = new Vector3(0, 0, 0), b = new Vector3(70, 0, 0);
        Vector3 c = new Vector3(150, 0, 200), d = new Vector3(200, 0, 200);
        foreach (float offset in new[] { 0f, 10000f, 100000f })
        foreach (float t in new[] { 0f, .03123f, .371234f, .89123f, 1f })
        {
            Vector3 shift = new Vector3(offset, 0, -offset);
            Vector3 expected = shift + PolylineUtils.CubicBezierPoint(a, b, c, d, t);
            RailJunctionGeometry.ProjectCubic(a+shift, b+shift, c+shift, d+shift, expected, out Vector3 point, out Vector3 tangent);
            Check(Utils.XZDistance(point, expected) < .03f, "Rail projection stays within connection tolerance at large coordinates");
            float u = 1 - t;
            Vector3 direction = 3 * (u * u * (b - a) + 2 * u * t * (c - b) + t * t * (d - c));
            Check(Vector3.Dot(tangent, direction.normalized) > .999999f, "Rail tangent uses the cubic derivative");
        }
    }

    private static void CheckSegmentProjection()
    {
        var a = new Vector3(0, 10, 0);
        var b = new Vector3(10, 20, 0);
        foreach (float y in new[] { -1000f, 0f, 1000f })
        {
            Near(PolylineUtils.ClosestPointOnSegmentXZ(new Vector3(4, y, 3), a, b),
                new Vector3(4, 14, 0), "XZ projection ignores cursor height and interpolates endpoint height");
        }
        Near(PolylineUtils.ClosestPointOnSegment(new Vector3(4, 10, 3), a, b),
            new Vector3(2, 12, 0), "3D projection includes height");
        Near(PolylineUtils.ClosestPointOnSegmentXZ(new Vector3(-2, 0, 3), a, b), a, "clamp before start");
        Near(PolylineUtils.ClosestPointOnSegmentXZ(new Vector3(12, 0, 3), a, b), b, "clamp after end");
        Near(PolylineUtils.ClosestPointOnSegmentXZ(new Vector3(4, 0, 3), b, a),
            new Vector3(4, 14, 0), "reversed segment");

        var vertical = new Vector3(0, 20, 0);
        Near(PolylineUtils.ClosestPointOnSegmentXZ(vertical, a, vertical), a, "vertical segment degenerates in XZ");
        Near(PolylineUtils.ClosestPointOnSegment(vertical, a, vertical), vertical, "vertical segment valid in 3D");
        Near(PolylineUtils.ClosestPointOnSegment(b, a, a), a, "zero length 3D remains finite");
        Near(PolylineUtils.ClosestPointOnSegmentXZ(b, a, a), a, "zero length XZ remains finite");

        Check(MathF.Abs(PolylineUtils.DistanceToSegmentSq(new Vector2(5, 3), Vector2.zero, new Vector2(10, 0)) - 9) < 1e-6f,
            "2D perpendicular squared distance");
        Check(MathF.Abs(PolylineUtils.DistanceToSegmentSq(new Vector2(13, 4), Vector2.zero, new Vector2(10, 0)) - 25) < 1e-6f,
            "2D distance clamps to endpoint");
        Check(MathF.Abs(PolylineUtils.DistanceToSegmentSq(new Vector2(3, 4), Vector2.zero, Vector2.zero) - 25) < 1e-6f,
            "zero length 2D remains finite");
        foreach (float length in new[] { 0.00005f, 0.0002f, 0.0005f })
        {
            var end = new Vector3(length, 0, 0);
            float expected = length < 0.0001f ? 0 : length;
            Check(MathF.Abs(PolylineUtils.ClosestPointOnSegmentXZ(end, Vector3.zero, end).x - expected) < 1e-8f,
                "XZ tiny segment threshold");
            Check(MathF.Abs(PolylineUtils.ClosestPointOnSegment(end, Vector3.zero, end).x - expected) < 1e-8f,
                "3D tiny segment threshold");
            Check(MathF.Abs(PolylineUtils.ClosestPointOnSegment(new Vector2(length, 0), Vector2.zero, new Vector2(length, 0)).x - expected) < 1e-8f,
                "2D tiny segment threshold");
        }
    }
}
