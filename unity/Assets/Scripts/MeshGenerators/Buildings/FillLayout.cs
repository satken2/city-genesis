using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pure data determining fill-part (fill1, 2, …) positions. Scatter one part per cell of a grid fixed to world coordinates (cell side = the largest
/// part's bounding-box side) with the terrain-prop rule (<see cref="PropGeometry.Sample"/>): the bounding-box center anywhere in the cell, any heading,
/// and a scale of 0.8–1.2, so neighbors may overlap.
/// The kind also comes only from the cell's integer indices, so when the site grows or shrinks (its vertices and center_point move every epoch),
/// parts in the cells it keeps do not move. A cell holds its part when the rotated, scaled bounding box lies inside the site.
/// With a drawn range (a slum's fringe_polygon), a box inside it but not inside the site is kept when the cell's fixed rank is below a density
/// falling linearly from 1 at the site's edge to 0 at the drawn range's edge. Where the drawn range coincides with the site (the slum was stopped there),
/// parts end at the site's edge; on a free frontier they thin out beyond it. Fixed ranks let an advancing frontier only add parts.
/// The footprint and entry are not avoided, because fill parts are meant for ground models whose footprint has no body.
/// Positions use local XZ relative to center_point. Specification: city-genesis/docs/ja/geometries/F_建物メッシュの生成.md.
/// </summary>
public static class FillLayout
{
    /// <summary>
    /// Determine placements. site=parcel (local XZ), fringe=drawn range (local XZ, null without one), origin=world XZ of center_point that both are
    /// relative to, kinds=placement bounding boxes of the parts. Return empty without a site or parts.
    /// </summary>
    public static List<ScatterLayout.Item> Plan(Vector2[] site, Vector2[] fringe, Vector2 origin, IReadOnlyList<Bounds> kinds)
    {
        var items = new List<ScatterLayout.Item>();
        if (site == null || site.Length < 3 || kinds == null || kinds.Count == 0)
        {
            return items;
        }
        if (fringe != null && fringe.Length < 3)
        {
            fringe = null;
        }

        float cell = 0f;
        foreach (Bounds bounds in kinds)
        {
            cell = Mathf.Max(cell, Mathf.Max(bounds.size.x, bounds.size.z));
        }

        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (Vector2[] ring in fringe != null ? new[] { site, fringe } : new[] { site })
        {
            foreach (Vector2 point in ring)
            {
                min = Vector2.Min(min, point + origin);
                max = Vector2.Max(max, point + origin);
            }
        }
        int firstColumn = Mathf.FloorToInt(min.x / cell), lastColumn = Mathf.FloorToInt(max.x / cell);
        int firstRow = Mathf.FloorToInt(min.y / cell), lastRow = Mathf.FloorToInt(max.y / cell);
        var inner = new Ring(site, origin, cell, firstRow, lastRow);
        Ring outer = fringe != null ? new Ring(fringe, origin, cell, firstRow, lastRow) : null;

        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                PropGeometry.Point point = PropGeometry.Sample(column, row, cell, 0);
                int kind = (int)(CellHash(column, row) % (uint)kinds.Count);
                Bounds bounds = kinds[kind];
                var scaled = new Bounds(bounds.center * point.scale, bounds.size * point.scale);
                Quaternion rotation = Quaternion.Euler(0f, point.rotation, 0f);
                if (!inner.Contains(point.position, rotation, scaled.extents)
                    && (outer == null || !outer.Contains(point.position, rotation, scaled.extents)
                        || point.edgeRank >= Density(point.position, inner, outer)))
                {
                    continue;
                }

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

    /// <summary>Density at p inside the drawn range: 1 inside the site, falling linearly to 0 at the drawn range's edge.</summary>
    private static float Density(Vector2 p, Ring inner, Ring outer)
    {
        if (inner.Contains(p)) return 1f;
        float toInner = inner.Distance(p), toOuter = outer.Distance(p);
        return toOuter / Mathf.Max(toInner + toOuter, 1e-6f);
    }

    /// <summary>A ring in world XZ with its edges bucketed by grid row, since sites can span thousands of cells.</summary>
    private sealed class Ring
    {
        private readonly List<(Vector2 a, Vector2 b)>[] rows;
        private readonly int firstRow;
        private readonly float cell;

        /// <summary>Bucket the ring (local XZ + origin) into rows firstRow..lastRow, which must cover it.</summary>
        public Ring(Vector2[] local, Vector2 origin, float cell, int firstRow, int lastRow)
        {
            this.cell = cell;
            this.firstRow = firstRow;
            rows = new List<(Vector2 a, Vector2 b)>[lastRow - firstRow + 1];
            for (int i = 0, j = local.Length - 1; i < local.Length; j = i++)
            {
                Vector2 a = local[j] + origin, b = local[i] + origin;
                int from = Mathf.FloorToInt(Mathf.Min(a.y, b.y) / cell) - firstRow;
                int to = Mathf.FloorToInt(Mathf.Max(a.y, b.y) / cell) - firstRow;
                for (int r = from; r <= to; r++)
                {
                    (rows[r] ??= new List<(Vector2 a, Vector2 b)>()).Add((a, b));
                }
            }
        }

        /// <summary>
        /// Whether the box (world center, heading, half extents) lies inside the ring: no edge in the rows its axis-aligned bounds span touches it,
        /// and its center is inside.
        /// </summary>
        public bool Contains(Vector2 center, Quaternion rotation, Vector3 extents)
        {
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
            return Contains(center);
        }

        /// <summary>
        /// Whether p is inside the ring (even-odd +X ray over its own row's edges, with the same half-open rule as
        /// <see cref="PolygonUtils.Contains(IReadOnlyList{Vector2}, Vector2)"/>).
        /// </summary>
        public bool Contains(Vector2 p)
        {
            int row = Mathf.FloorToInt(p.y / cell) - firstRow;
            if (row < 0 || row >= rows.Length || rows[row] == null) return false;
            bool inside = false;
            foreach (var (a, b) in rows[row])
            {
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        /// <summary>
        /// Distance from p (inside the grid's rows) to the nearest edge. Search rows outward from p's row and stop once a row is farther than the best edge,
        /// since an edge first met k rows away lies at least (k - 1) cells away.
        /// </summary>
        public float Distance(Vector2 p)
        {
            int row = Mathf.Clamp(Mathf.FloorToInt(p.y / cell) - firstRow, 0, rows.Length - 1);
            float bestSq = float.MaxValue;
            for (int k = 0; row - k >= 0 || row + k < rows.Length; k++)
            {
                float gap = (k - 1) * cell;
                if (k > 1 && gap * gap >= bestSq) break;
                Visit(row - k, p, ref bestSq);
                if (k > 0) Visit(row + k, p, ref bestSq);
            }
            return Mathf.Sqrt(bestSq);
        }

        private void Visit(int row, Vector2 p, ref float bestSq)
        {
            if (row < 0 || row >= rows.Length || rows[row] == null) return;
            foreach (var (a, b) in rows[row])
            {
                bestSq = Mathf.Min(bestSq, PolylineUtils.DistanceToSegmentSq(p, a, b));
            }
        }
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
