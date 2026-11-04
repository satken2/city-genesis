using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pure data determining fill-part (fill1, 2, …) positions. Scatter one part per cell of a grid fixed to world coordinates (cell side = the largest
/// part's bounding-box side) with the terrain-prop rule (<see cref="PropGeometry.Sample"/>): the bounding-box center anywhere in the cell, any heading,
/// and a scale of 0.8–1.2, so neighbors may overlap.
/// The kind also comes only from the cell's integer indices, so when the site grows or shrinks (its vertices and center_point move every epoch),
/// parts in the cells it keeps do not move. A cell holds its part only when the rotated, scaled bounding box lies inside the site.
/// The footprint and entry are not avoided, because fill parts are meant for ground models whose footprint has no body.
/// Positions use local XZ relative to center_point. Specification: city-genesis/docs/ja/geometries/F_建物メッシュの生成.md.
/// </summary>
public static class FillLayout
{
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

        float cell = 0f;
        foreach (Bounds bounds in kinds)
        {
            cell = Mathf.Max(cell, Mathf.Max(bounds.size.x, bounds.size.z));
        }

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

        // Sites can span thousands of cells, so give each grid row only the edges overlapping it.
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
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                PropGeometry.Point point = PropGeometry.Sample(column, row, cell, 0);
                int kind = (int)(CellHash(column, row) % (uint)kinds.Count);
                Bounds bounds = kinds[kind];
                var scaled = new Bounds(bounds.center * point.scale, bounds.size * point.scale);
                Quaternion rotation = Quaternion.Euler(0f, point.rotation, 0f);
                if (!Inside(rows, firstRow, cell, point.position, rotation, scaled.extents)) continue;

                Vector3 center = rotation * scaled.center;
                Vector2 at = point.position - new Vector2(center.x, center.z) - origin;
                items.Add(new ScatterLayout.Item
                {
                    Kind = kind, Position = at, Rotation = rotation, Scale = point.scale,
                    Corners = PortLayout.RotatedRect(at, rotation, scaled),
                });
            }
        }
        return items;
    }

    /// <summary>
    /// Whether the box (world center, heading, half extents) lies inside the ring: no edge in the rows its axis-aligned bounds span touches it,
    /// and its center is inside (even-odd +X ray over its own row's edges, with the same half-open rule as <see cref="PolygonUtils.Contains(IReadOnlyList{Vector2}, Vector2)"/>).
    /// </summary>
    private static bool Inside(List<(Vector2 a, Vector2 b)>[] rows, int firstRow, float cell, Vector2 center, Quaternion rotation, Vector3 extents)
    {
        int centerRow = Mathf.FloorToInt(center.y / cell) - firstRow;
        if (centerRow < 0 || centerRow >= rows.Length || rows[centerRow] == null) return false;

        Vector3 x = rotation * Vector3.right, z = rotation * Vector3.forward;
        var axisX = new Vector2(x.x, x.z);
        var axisZ = new Vector2(z.x, z.z);
        float reachZ = Mathf.Abs(axisX.y) * extents.x + Mathf.Abs(axisZ.y) * extents.z;
        int from = Mathf.Max(0, Mathf.FloorToInt((center.y - reachZ) / cell) - firstRow);
        int to = Mathf.Min(rows.Length - 1, Mathf.FloorToInt((center.y + reachZ) / cell) - firstRow);
        for (int r = from; r <= to; r++)
        {
            if (rows[r] == null) continue;
            foreach (var (a, b) in rows[r])
            {
                Vector2 da = a - center, db = b - center;
                var localA = new Vector2(Vector2.Dot(da, axisX), Vector2.Dot(da, axisZ));
                var localB = new Vector2(Vector2.Dot(db, axisX), Vector2.Dot(db, axisZ));
                if (Touches(localA, localB, -extents.x, -extents.z, extents.x, extents.z)) return false;
            }
        }

        bool inside = false;
        foreach (var (a, b) in rows[centerRow])
        {
            if ((a.y > center.y) != (b.y > center.y) && center.x < (b.x - a.x) * (center.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
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
}
