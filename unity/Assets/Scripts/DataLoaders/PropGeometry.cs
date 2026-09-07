using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>散布とプレビューが共用する、描画区画や走査順に依存しない配置規則。</summary>
public static class PropGeometry
{
    public struct Point
    {
        public Vector2 position;
        public float rotation, scale;
    }

    public sealed class Region
    {
        public readonly List<Vector2[]> rings = new List<Vector2[]>();
        public Rect bounds;
        public bool Contains(Vector2 p)
        {
            if (!bounds.Contains(p) || rings.Count == 0 || !InRing(p, rings[0])) return false;
            for (int i = 1; i < rings.Count; i++) if (InRing(p, rings[i])) return false;
            return true;
        }
    }

    public static List<Region> Parse(JArray data)
    {
        var result = new List<Region>();
        if (data == null) return result;
        foreach (var polygon in data)
        {
            var region = new Region();
            foreach (var ring in polygon)
            {
                var points = new List<Vector2>();
                foreach (var p in ring) points.Add(new Vector2((float)p[0], (float)p[1]));
                if (points.Count >= 3) region.rings.Add(points.ToArray());
            }
            if (region.rings.Count == 0) continue;
            region.bounds = Bounds(region.rings[0]);
            result.Add(region);
        }
        return result;
    }

    public static Rect Bounds(IList<Vector2> points)
    {
        var min = points[0]; var max = min;
        for (int i = 1; i < points.Count; i++) { min = Vector2.Min(min, points[i]); max = Vector2.Max(max, points[i]); }
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    public static bool InRing(Vector2 p, IList<Vector2> ring)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[j]; var b = ring[i];
            if (DistanceSquared(p, a, b) < 1e-10f) return true;
            if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
        }
        return inside;
    }

    public static float DistanceSquared(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0;
        return (p - a - ab * t).sqrMagnitude;
    }

    public static bool Blocked(Vector2 p, float clearance, IList<Vector2> ring, Rect bounds)
    {
        if (p.x < bounds.xMin - clearance || p.x > bounds.xMax + clearance ||
            p.y < bounds.yMin - clearance || p.y > bounds.yMax + clearance) return false;
        if (InRing(p, ring)) return true;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            if (DistanceSquared(p, ring[i], ring[j]) <= clearance * clearance) return true;
        return false;
    }

    private static uint Hash(uint value)
    {
        unchecked { value ^= value >> 16; value *= 0x7feb352d; value ^= value >> 15; value *= 0x846ca68b; return value ^ (value >> 16); }
    }
    private static float Unit(uint value) => (Hash(value) & 0xffffff) / 16777216f;

    public static Point Sample(int x, int z, float spacing, int seed)
    {
        unchecked
        {
            uint h = Hash((uint)x) ^ Hash((uint)z + 0x9e3779b9) ^ Hash((uint)seed);
            return new Point {
                position = new Vector2((x + Unit(h)) * spacing, (z + Unit(h + 1)) * spacing),
                rotation = Unit(h + 2) * 360f, scale = 0.8f + Unit(h + 3) * 0.4f
            };
        }
    }

    public static IEnumerable<Point> Scatter(Rect rect, IList<Region> regions, float density, int seed)
    {
        if (density <= 0 || float.IsNaN(density) || float.IsInfinity(density)) yield break;
        float spacing = Mathf.Max(0.5f, Mathf.Sqrt(10000f / density));
        for (int x = Mathf.FloorToInt(rect.xMin / spacing); x < Mathf.CeilToInt(rect.xMax / spacing); x++)
            for (int z = Mathf.FloorToInt(rect.yMin / spacing); z < Mathf.CeilToInt(rect.yMax / spacing); z++)
            {
                var p = Sample(x, z, spacing, seed);
                if (!rect.Contains(p.position)) continue;
                for (int r = 0; r < regions.Count; r++)
                    if (regions[r].Contains(p.position)) { yield return p; break; }
            }
    }
}
