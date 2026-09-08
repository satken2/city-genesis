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
}
