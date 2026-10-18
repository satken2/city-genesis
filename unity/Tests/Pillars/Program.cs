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

    // 地表の道路は PathManager と同じく、スプライン(地形+0.1)に路面の持ち上げ0.1を加える。
    private static PillarClearance.Corridor Street(Vector3 a, Vector3 b, float halfWidth, float sidewalk, float medianHalf = 0f)
    {
        var road = Road(a, b, halfWidth);
        road.YOffset = 0.1f;
        road.SidewalkWidth = sidewalk;
        road.MedianHalfWidth = medianHalf;
        return road;
    }

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
        CheckPortalOffsets();
        CheckFootBands();
        CheckPortalFootBands();
        CheckOverhead();
        Console.WriteLine($"PASS: {checks} pillar clearance cases");
    }

    private static void CheckFootBands()
    {
        for (int angle = 0; angle < 180; angle += 30)
        {
            float t = angle * (float)Math.PI / 180;
            var along = new Vector3((float)Math.Cos(t), 0, (float)Math.Sin(t));
            var side = Vector3.Cross(Vector3.up, along);
            Bounds Leg(float lateral, float height = 8) => new Bounds(side * lateral + Vector3.up * height * .5f, new Vector3(1, height, 1));
            Vector3 a = -along * 30 + Vector3.up * .1f, b = along * 30 + Vector3.up * .1f;
            // 幅12m・歩道2.5mの道路の車道は中心から3.5m。外接円0.71+余裕0.25の柱は中心から4.6mの歩道に立てる。
            var street = Street(a, b, 6, 2.5f);
            Check(false, Leg(4.6f), street);
            Check(false, Leg(-4.6f), street);
            Check(true, Leg(3.8f), street);
            Check(true, Leg(-3.8f), street);
            Check(true, Leg(4.6f), Street(a, b, 6, 0));
            // 幅32m・歩道3m・中央分離帯3mの道路では、分離帯に収まる柱だけを立てる。
            var avenue = Street(a, b, 16, 3, 1.5f);
            Check(false, Leg(0), avenue);
            Check(true, new Bounds(Vector3.up * 4, new Vector3(2.4f, 8, 2.4f)), avenue);
            Check(true, Leg(8), avenue);
            Check(true, Leg(-8), avenue);
            Check(false, Leg(14), avenue);
            Check(false, Leg(-14), avenue);
            Check(true, Leg(0), Street(a, b, 16, 3));
            // 足元より高い路面(高架の床版)は、歩道も柱を通さない。
            var deck = Street(a + Vector3.up * 5, b + Vector3.up * 5, 6, 2.5f);
            Check(true, Leg(4.6f), deck);
            Check(false, Leg(4.6f, 4), deck);
        }
    }

    private static void CheckPortalFootBands()
    {
        void Offsets(Vector3 origin, PillarClearance.Corridor corridor, out float left, out float right)
            => PillarClearance.FindPortalOffsets(origin, Vector3.right, 0, 8, 1.2f, 3,
                new List<PillarClearance.Corridor> { corridor }, out left, out right);
        Vector3 a = new Vector3(0, .1f, -100), b = new Vector3(0, .1f, 100);
        Offsets(Vector3.up * 10, Street(a, b, 6, 2.5f), out float left, out float right);
        if (Math.Abs(left + 4.96f) > .002f || Math.Abs(right - 4.96f) > .002f) throw new Exception("Portal legs must stop on the sidewalks");
        // 片側の車道の上の高架は、内側の柱を中央分離帯(中心から0.04m)に、外側の柱を歩道に置く。
        Offsets(new Vector3(8, 10, 0), Street(a, b, 16, 3, 1.5f), out left, out right);
        if (Math.Abs(left + 7.96f) > .002f || Math.Abs(right - 6.46f) > .002f) throw new Exception("Inner portal leg must stand in the median");
        Offsets(Vector3.up * 10, Street(a + Vector3.up * 5, b + Vector3.up * 5, 6, 2.5f), out left, out right);
        if (left > -7.45f || right < 7.45f) throw new Exception("Portal legs must not pierce an elevated sidewalk");
        checks += 3;
    }

    private static void CheckOverhead()
    {
        void Covered(bool expected, float radius, params PillarClearance.Corridor[] corridors)
        {
            bool actual = PillarClearance.PassesAbove(Vector3.zero, radius, 2.5f, new List<PillarClearance.Corridor>(corridors));
            if (actual != expected) throw new Exception($"Overhead case {checks}: expected {expected}, got {actual}");
            checks++;
        }
        Covered(true, 2, Road(new Vector3(-30, 6, 0), new Vector3(30, 6, 0)));
        Covered(true, 2, Road(new Vector3(-30, 60, 0), new Vector3(30, 60, 0)));
        Covered(true, 2, Road(new Vector3(-30, 6, 6), new Vector3(30, 6, 6)));
        Covered(false, 0, Road(new Vector3(-30, 6, 6), new Vector3(30, 6, 6)));
        Covered(false, 2, Road(new Vector3(-30, 1, 6), new Vector3(30, 1, 6)));
        Covered(false, 2, Road(new Vector3(-30, -8, 0), new Vector3(30, -8, 0)));
        Covered(false, 2, Road(new Vector3(-30, -6, 0), new Vector3(30, 6, 0)));
        Covered(true, 2, Road(new Vector3(-30, -6, 0), new Vector3(30, 18, 0)));
        Covered(false, 2);

        var viaduct = Road(new Vector3(-30, 8, 0), new Vector3(30, 8, 0));
        var crossing = new List<Vector3>();
        for (int z = -50; z <= 50; z++) crossing.Add(new Vector3(0, 0, z));
        var parallel = new List<Vector3>();
        for (int x = -50; x <= 50; x++) parallel.Add(new Vector3(x, 0, 20));
        if (!PillarClearance.NearXZ(crossing, 12, viaduct)) throw new Exception("Crossing road must be near the viaduct");
        if (PillarClearance.NearXZ(parallel, 12, viaduct)) throw new Exception("Distant road must not be near the viaduct");
        if (!PillarClearance.NearXZ(parallel, 16, viaduct)) throw new Exception("Wide road must reach the viaduct");
        checks += 3;
    }

    private static void CheckPortalOffsets()
    {
        var parallel = Road(new Vector3(0, 0, -100), new Vector3(0, 0, 100), 12);
        var corridors = new List<PillarClearance.Corridor> { parallel };
        PillarClearance.FindPortalOffsets(Vector3.up * 10, Vector3.right, 0, 8, 1.2f, 3,
            corridors, out float left, out float right);
        if (left >= -13.45f || right <= 13.45f) throw new Exception("Portal legs must clear the whole road");
        checks++;
        foreach (float offset in new[] { left, right })
            Check(false, new Bounds(new Vector3(offset, 4, 0), new Vector3(1.5f, 8, 1.8f)), parallel);
        parallel.Points = new List<Vector3> { new Vector3(5, 0, -100), new Vector3(5, 0, 100) };
        PillarClearance.FindPortalOffsets(Vector3.up * 10, Vector3.right, 0, 8, 1.2f, 3,
            corridors, out left, out right);
        if (Math.Abs(left + right - 10) > .001f) throw new Exception("Offset road must shift portal center");
        checks++;
        corridors.Add(Road(new Vector3(50, 0, -100), new Vector3(50, 0, 100)));
        float expectedRight = right;
        PillarClearance.FindPortalOffsets(Vector3.up * 10, Vector3.right, 0, 8, 1.2f, 3,
            corridors, out left, out right);
        if (right != expectedRight) throw new Exception("Distant roads must not widen the portal");
        checks++;
        corridors.Add(Road(new Vector3(20, 0, -100), new Vector3(20, 0, 100)));
        PillarClearance.FindPortalOffsets(Vector3.up * 10, Vector3.right, 0, 8, 1.2f, 3,
            corridors, out left, out right);
        if (right <= 25.45f) throw new Exception("Overlapping corridors must move legs past both roads");
        corridors.Reverse();
        PillarClearance.FindPortalOffsets(Vector3.up * 10, Vector3.right, 0, 8, 1.2f, 3,
            corridors, out float reversedLeft, out float reversedRight);
        if (left != reversedLeft || right != reversedRight) throw new Exception("Portal must ignore registration order");
        checks += 2;
        for (int angle = 0; angle < 180; angle += 15)
        {
            float radians = angle * (float)Math.PI / 180;
            var axis = new Vector3((float)Math.Cos(radians), 0, (float)Math.Sin(radians));
            var forward = Vector3.Cross(axis, Vector3.up);
            var road = Road(-forward * 50, forward * 50, 12);
            PillarClearance.FindPortalOffsets(Vector3.up * 10, axis, 0, 8, 1.2f, 3,
                new List<PillarClearance.Corridor> { road }, out left, out right);
            if (Math.Abs(left + 13.46f) > .002f || Math.Abs(right - 13.46f) > .002f)
                throw new Exception("Portal must rotate with the high railway");
            checks++;
        }
        parallel.YOffset = 20;
        PillarClearance.FindPortalOffsets(Vector3.up * 10, Vector3.right, 0, 8, 1.2f, 3,
            new List<PillarClearance.Corridor> { parallel }, out left, out right);
        if (left != -3 || right != 3) throw new Exception("Upper road must not affect portal legs");
        checks++;
    }
}
