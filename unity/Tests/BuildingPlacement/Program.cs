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
    private static void Near(Vector3 a, Vector3 b, string message) => Check((a - b).magnitude < 0.003f, message);

    private static void Main()
    {
        foreach (float degrees in new[] { 0f, 17f, 45f, 90f, 179f, 270f })
        foreach (float side in new[] { -1f, 1f })
        foreach (float depth in new[] { 1f, 18f, 120f, 500f })
        {
            var origin = new Vector3(1200, 0, -800);
            float r = degrees * MathF.PI / 180;
            Vector3 tangent = new Vector3(MathF.Cos(r), 0, MathF.Sin(r));
            Vector3 outward = new Vector3(-tangent.z, 0, tangent.x) * side;
            Vector3 front = origin + outward * 6;
            float angle = BuildingPlacementGeometry.AngleFromOutward(outward);
            Vector3 center = BuildingPlacementGeometry.CenterFromFront(front, outward, depth);
            BuildingPlacementGeometry.Axes(angle, out var u, out var v);
            Near(center - v * (depth * 0.5f), front, "front edge stays on roadside after resizing");
            Near(v, outward, "depth grows away from the road on either side");
            Check(!BuildingPlacementGeometry.OverlapsRoad(center, 24, depth, angle,
                origin - tangent * 100, origin + tangent * 100, 6), "touching road is permitted");
            Check(BuildingPlacementGeometry.OverlapsRoad(center - outward, 24, depth, angle,
                origin - tangent * 100, origin + tangent * 100, 6), "road intrusion is rejected");
            Check(BuildingPlacementGeometry.OverlapsRoad(center, 24, depth, angle,
                center - v * (depth + 10), center + v * (depth + 10), 3), "crossing road detected without a corner inside");
            Check(!BuildingPlacementGeometry.OverlapsRoad(center, 24, depth, angle,
                center + u * 20 - v * 10, center + u * 20 + v * 10, 3), "separate parallel road does not block");
            Near(BuildingPlacementGeometry.Closest(origin + tangent * 30 + outward * 50,
                origin, origin + tangent * 100), origin + tangent * 30, "projection works at large coordinates");
        }
        Vector3 c = new Vector3(0, 0, 16);
        Check(BuildingPlacementGeometry.OverlapsRoad(c, 24, 20, 0,
            new Vector3(-12, 0, 2), new Vector3(0, 0, 0), 6), "curve bulging into front corner is rejected");
        Check(!BuildingPlacementGeometry.OverlapsRoad(c, 24, 20, 0,
            new Vector3(-12, 0, -2), new Vector3(0, 0, 0), 6), "curve turning away from frontage is permitted");
        Check(BuildingPlacementGeometry.OverlapsRoad(c, 24, 20, 0,
            new Vector3(14, 0, 16), new Vector3(14, 0, 16), 3), "short road cap near building is checked");
        Near(BuildingPlacementGeometry.Closest(new Vector3(100, 0, 100), Vector3.zero, Vector3.zero),
            Vector3.zero, "zero length candidate does not produce NaN");
        Console.WriteLine($"PASS: {checks} building placement checks");
    }
}
