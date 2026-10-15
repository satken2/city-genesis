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
        public float edgeRank;
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

    /// <summary>リング内外判定。辺上(1e-5m以内)の点は内側として扱い、境界にちょうど乗った散布候補を落とさない。</summary>
    public static bool InRing(Vector2 p, IReadOnlyList<Vector2> ring) => PolygonUtils.Contains(ring, p, BOUNDARY_TOLERANCE);

    private const float BOUNDARY_TOLERANCE = 1e-5f;

    public static float EdgeWeight(Vector2 p, Region region, float width)
    {
        if (width <= 0) return 1;
        float distanceSq = float.MaxValue;
        foreach (var ring in region.rings)
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                distanceSq = Mathf.Min(distanceSq, PolylineUtils.DistanceToSegmentSq(p, ring[i], ring[j]));
        float t = Mathf.Clamp01(Mathf.Sqrt(distanceSq) / width);
        return t * t * (3 - 2 * t);
    }

    // 固定順位による間引きなので、幅を広げても残る個体の位置・回転・倍率は変わらない。
    public static bool Accept(Point point, IList<Region> regions, float width)
    {
        foreach (var region in regions)
            if (region.Contains(point.position) && point.edgeRank < EdgeWeight(point.position, region, width)) return true;
        return false;
    }

    public static bool Blocked(Vector2 p, float clearance, IReadOnlyList<Vector2> ring, Rect bounds)
    {
        if (p.x < bounds.xMin - clearance || p.x > bounds.xMax + clearance ||
            p.y < bounds.yMin - clearance || p.y > bounds.yMax + clearance) return false;
        if (InRing(p, ring)) return true;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            if (PolylineUtils.DistanceToSegmentSq(p, ring[i], ring[j]) <= clearance * clearance) return true;
        return false;
    }

    public static bool BlockedWithHoles(Vector2 p, float clearance, IReadOnlyList<Vector2> ring,
        Rect bounds, IList<Vector2[]> holes)
    {
        if (!Blocked(p, clearance, ring, bounds)) return false;
        foreach (var hole in holes)
        {
            if (!InRing(p, hole)) continue;
            // 水域の穴は島の陸地。岸から離隔分だけ内側に入った点を表示する。
            for (int i = 0, j = hole.Length - 1; i < hole.Length; j = i++)
                if (PolylineUtils.DistanceToSegmentSq(p, hole[i], hole[j]) <= clearance * clearance) return true;
            return false;
        }
        return true;
    }

    private static uint Hash(uint value)
    {
        unchecked { value ^= value >> 16; value *= 0x7feb352d; value ^= value >> 15; value *= 0x846ca68b; return value ^ (value >> 16); }
    }
    private static float Unit(uint value) => (Hash(value) & 0xffffff) / 16777216f;

    /// <summary>
    /// 世界格子 (x, z) の候補。Resources/Shaders/ForestPlacement.compute の Generate と同じ式なので、片方だけを変えると
    /// GPUの森林と、散布プレビュー・ブルドーザの選択が再現するCPUの候補の位置がずれる。
    /// </summary>
    public static Point Sample(int x, int z, float spacing, int seed)
    {
        unchecked
        {
            uint h = Hash((uint)x) ^ Hash((uint)z + 0x9e3779b9) ^ Hash((uint)seed);
            return new Point {
                position = new Vector2((x + Unit(h)) * spacing, (z + Unit(h + 1)) * spacing),
                rotation = Unit(h + 2) * 360f, scale = 0.8f + Unit(h + 3) * 0.4f, edgeRank = Unit(h + 6)
            };
        }
    }

    public static IEnumerable<Point> Scatter(Rect rect, IList<Region> regions, float density, int seed, float edgeFadeWidth = 0)
    {
        if (density <= 0 || float.IsNaN(density) || float.IsInfinity(density)) yield break;
        float spacing = Mathf.Max(0.5f, Mathf.Sqrt(10000f / density));
        for (int x = Mathf.FloorToInt(rect.xMin / spacing); x < Mathf.CeilToInt(rect.xMax / spacing); x++)
            for (int z = Mathf.FloorToInt(rect.yMin / spacing); z < Mathf.CeilToInt(rect.yMax / spacing); z++)
            {
                var p = Sample(x, z, spacing, seed);
                if (!rect.Contains(p.position)) continue;
                if (Accept(p, regions, edgeFadeWidth)) yield return p;
            }
    }
}
