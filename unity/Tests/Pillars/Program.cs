using System;
using System.Collections.Generic;
using UnityEngine;

internal static class Program
{
    private static int checks;
    private static void Check(bool expected, Bounds pillar, params PillarClearance.Corridor[] corridors)
    {
        bool actual = PillarClearance.Intersects(pillar, new List<PillarClearance.Corridor>(corridors));
        if (actual != expected) throw new Exception($"Case {checks}: expected {expected}, got {actual}");
        checks++;
    }

    private static PillarClearance.Corridor Road(Vector3 a, Vector3 b, float halfWidth = 4f, float height = 5f)
        => new PillarClearance.Corridor { Points = new List<Vector3> { a, b }, HalfWidth = halfWidth, Height = height };

    private static void Main()
    {
        var pillar = new Bounds(new Vector3(0, 5, 0), new Vector3(2, 10, 2));
        for (int angle = 0; angle < 180; angle += 15)
        {
            float t = angle * (float)Math.PI / 180;
            var direction = new Vector3((float)Math.Cos(t), 0, (float)Math.Sin(t));
            Check(true, pillar, Road(-direction * 30, direction * 30));
            Check(false, pillar, Road(-direction * 30 + Vector3.up * 12, direction * 30 + Vector3.up * 12));
            Check(false, pillar, Road(-direction * 30 - Vector3.up * 10, direction * 30 - Vector3.up * 10));
        }
        Check(false, pillar, Road(new Vector3(-20, 0, 10), new Vector3(20, 0, 10)));
        Check(true, pillar, Road(new Vector3(-20, 0, 5.5f), new Vector3(20, 0, 5.5f)));
        Check(true, pillar, Road(new Vector3(-20, 0, 8), new Vector3(20, 0, 8), 7));
        Check(false, pillar, Road(new Vector3(-20, 30, 0), new Vector3(20, 30, 0)));
        Check(true, pillar, Road(new Vector3(-10, -10, 0), new Vector3(10, 30, 0)));
        Check(false, pillar, Road(new Vector3(-10, 20, 0), new Vector3(10, 40, 0)));
        Check(true, pillar, Road(Vector3.zero, Vector3.zero));
        Check(false, pillar);

        var cap = new Bounds(new Vector3(0, 9, 0), new Vector3(12, 2, 2));
        var sideRoad = Road(new Vector3(-20, 0, 8), new Vector3(20, 0, 8));
        Check(false, cap, sideRoad);
        sideRoad.Height = 10;
        Check(true, cap, sideRoad);
        Check(false, pillar, sideRoad);
        // 同じ高さの接続先路面はキャップより上にあり、橋脚を不要に省かない。
        Check(false, cap, Road(new Vector3(-20, 10.1f, 0), new Vector3(20, 10.1f, 0)));

        var curve = new PillarClearance.Corridor
        {
            Points = new List<Vector3> { new Vector3(-20, 0, 20), new Vector3(0, 0, 0), new Vector3(20, 0, 20) },
            HalfWidth = 2, Height = 5
        };
        Check(true, pillar, curve);
        curve.Points.Reverse();
        Check(true, pillar, curve);
        Console.WriteLine($"PASS: {checks} pillar clearance cases");
    }
}
