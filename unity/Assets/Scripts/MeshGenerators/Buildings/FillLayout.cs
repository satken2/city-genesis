using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pure data determining fill-part (fill1, 2, …) positions. Tile the site with a grid fixed to world coordinates, one part per cell.
/// The kind, one of four world-axis orientations, and the offset inside the cell come only from the cell's integer indices,
/// so when the site grows or shrinks (its vertices and center_point move every epoch), parts in the cells it keeps do not move.
/// A cell holds its part only when the rotated bounding box lies inside the site. The footprint and entry are not avoided,
/// because fill parts are meant for ground models whose footprint has no body.
/// Positions use local XZ relative to center_point. Specification: city-genesis/docs/ja/geometries/F_建物メッシュの生成.md.
/// </summary>
public static class FillLayout
{
    /// <summary>Minimum gap (m) between parts in neighboring cells, like a narrow alley. The cell side is the largest part's bounding-box side plus this.</summary>
    public const float GAP = 1f;

    /// <summary>
    /// Determine placements. site=parcel (local XZ), origin=world XZ of center_point that site is relative to,
    /// kinds=placement bounding boxes of the parts. Return empty without a site or parts.
    /// </summary>
    public static List<ScatterLayout.Item> Plan(Vector2[] site, Vector2 origin, IReadOnlyList<Bounds> kinds)
    {
        var items = new List<ScatterLayout.Item>();
        if (site == null || site.Length < 3 || kinds == null || kinds.Count == 0)
        {
            return items;
        }

        float largest = 0f;
        foreach (Bounds bounds in kinds)
        {
            largest = Mathf.Max(largest, Mathf.Max(bounds.size.x, bounds.size.z));
        }
        float cell = largest + GAP;

        var ring = new Vector2[site.Length];
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        for (int i = 0; i < site.Length; i++)
        {
            ring[i] = site[i] + origin;
            min = Vector2.Min(min, ring[i]);
            max = Vector2.Max(max, ring[i]);
        }
        int firstColumn = Mathf.FloorToInt(min.x / cell), lastColumn = Mathf.FloorToInt(max.x / cell);
        int firstRow = Mathf.FloorToInt(min.y / cell), lastRow = Mathf.FloorToInt(max.y / cell);

        // Sites can span thousands of cells, so give each grid row only the edges overlapping it. A box stays inside its row,
        // so these are all the edges that can touch it or cross the ray of its center test.
        var rows = new List<(Vector2 a, Vector2 b)>[lastRow - firstRow + 1];
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            Vector2 a = ring[j], b = ring[i];
            int from = Mathf.FloorToInt(Mathf.Min(a.y, b.y) / cell) - firstRow;
            int to = Mathf.FloorToInt(Mathf.Max(a.y, b.y) / cell) - firstRow;
            for (int r = from; r <= to; r++)
            {
                (rows[r] ??= new List<(Vector2 a, Vector2 b)>()).Add((a, b));
            }
        }

        for (int row = firstRow; row <= lastRow; row++)
        {
            var edges = rows[row - firstRow];
            if (edges == null) continue;
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                uint h = CellHash(column, row);
                int kind = (int)(h % (uint)kinds.Count);
                h = Hash(h);
                int turn = (int)(h & 3u);
                Bounds bounds = kinds[kind];
                Quaternion rotation = Quaternion.Euler(0f, 90f * turn, 0f);
                float width = turn % 2 == 0 ? bounds.size.x : bounds.size.z;
                float depth = turn % 2 == 0 ? bounds.size.z : bounds.size.x;
                h = Hash(h);
                float minX = column * cell + GAP * 0.5f + Unit(h) * (cell - GAP - width);
                h = Hash(h);
                float minZ = row * cell + GAP * 0.5f + Unit(h) * (cell - GAP - depth);
                if (!Inside(edges, minX, minZ, minX + width, minZ + depth)) continue;

                Vector3 center = rotation * bounds.center;
                Vector2 at = new Vector2(minX + width * 0.5f - center.x, minZ + depth * 0.5f - center.z) - origin;
                items.Add(new ScatterLayout.Item
                {
                    Kind = kind, Position = at, Rotation = rotation, Corners = PortLayout.RotatedRect(at, rotation, bounds),
                });
            }
        }
        return items;
    }

    /// <summary>
    /// Whether an axis-aligned box lies inside the ring, given the ring edges overlapping the box's row: no edge touches the box,
    /// and its center is inside (even-odd +X ray with the same half-open rule as <see cref="PolygonUtils.Contains(IReadOnlyList{Vector2}, Vector2)"/>).
    /// </summary>
    private static bool Inside(List<(Vector2 a, Vector2 b)> edges, float minX, float minZ, float maxX, float maxZ)
    {
        float x = (minX + maxX) * 0.5f, z = (minZ + maxZ) * 0.5f;
        bool inside = false;
        foreach (var (a, b) in edges)
        {
            if (Touches(a, b, minX, minZ, maxX, maxZ)) return false;
            if ((a.y > z) != (b.y > z) && x < (b.x - a.x) * (z - a.y) / (b.y - a.y) + a.x) inside = !inside;
        }
        return inside;
    }

    /// <summary>Whether segment a-b touches the axis-aligned box (separating-axis test on the box axes and the segment normal).</summary>
    private static bool Touches(Vector2 a, Vector2 b, float minX, float minZ, float maxX, float maxZ)
    {
        if (Mathf.Max(a.x, b.x) < minX || Mathf.Min(a.x, b.x) > maxX || Mathf.Max(a.y, b.y) < minZ || Mathf.Min(a.y, b.y) > maxZ)
        {
            return false;
        }
        Vector2 d = b - a;
        float s0 = d.x * (minZ - a.y) - d.y * (minX - a.x);
        float s1 = d.x * (minZ - a.y) - d.y * (maxX - a.x);
        float s2 = d.x * (maxZ - a.y) - d.y * (maxX - a.x);
        float s3 = d.x * (maxZ - a.y) - d.y * (minX - a.x);
        return !(s0 > 0f && s1 > 0f && s2 > 0f && s3 > 0f) && !(s0 < 0f && s1 < 0f && s2 < 0f && s3 < 0f);
    }

    private static uint CellHash(int column, int row)
    {
        unchecked { return Hash((uint)column ^ Hash((uint)row + 0x9e3779b9u)); }
    }

    private static uint Hash(uint value)
    {
        unchecked { value ^= value >> 16; value *= 0x7feb352d; value ^= value >> 15; value *= 0x846ca68b; return value ^ (value >> 16); }
    }

    private static float Unit(uint value) => (value & 0xffffff) / 16777216f;
}
