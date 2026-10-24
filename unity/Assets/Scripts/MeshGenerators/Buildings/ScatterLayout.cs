using System.Collections.Generic;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using UnityEngine;

/// <summary>
/// Pure data determining scatter-part (scatter1, 2, … / scatter-single1, 2, …) positions. Randomly place in site space outside the footprint
/// and the entry corridor, avoiding bounding-box overlaps. Choose among four orientations aligned to front edge (vertex 0→1).
/// Every kind is drawn with equal probability; single kinds leave the draw once placed, so each appears at most once per building.
/// Per-building seeds ensure stable regeneration.
/// All positions use local XZ relative to center_point. Specification: city-genesis/docs/ja/geometries/F_建物メッシュの生成.md.
/// </summary>
public static class ScatterLayout
{
    /// <summary>Fraction of vacant area filled by part bounds. Stop once exceeded.</summary>
    public const float FILL_RATIO = 0.25f;

    /// <summary>Clearance between parts and site perimeter, footprint, or other parts (m).</summary>
    public const float CLEARANCE = 0.5f;

    private const int MAX_ITEMS = 16;      // Maximum placements per building (limits vertices on large sites).
    private const int MAX_ATTEMPTS = 400;  // Maximum placement attempts.

    public struct Kind
    {
        /// <summary>Placement bounding box of the standard model.</summary>
        public Bounds Bounds;
        /// <summary>At most one per building (scatter-single{n}).</summary>
        public bool Single;
    }

    public struct Item
    {
        /// <summary>Part number (kinds index).</summary>
        public int Kind;
        /// <summary>Placement position for part origin.</summary>
        public Vector2 Position;
        public Quaternion Rotation;
        /// <summary>Four bounding-box corners (CCW), used for terrain contact.</summary>
        public Vector2[] Corners;
    }

    /// <summary>
    /// Determine placements. site=parcel, footprint=building base, keepClear=area to leave open such as the entry corridor (all CCW; keepClear may be null),
    /// seed=per-building random seed. Return empty when no vacant space or parts exist.
    /// </summary>
    public static List<Item> Plan(Vector2[] site, Vector2[] footprint, Vector2[] keepClear, IReadOnlyList<Kind> kinds, int seed)
    {
        var items = new List<Item>();
        if (site == null || site.Length < 3 || footprint == null || footprint.Length < 3 || kinds == null || kinds.Count == 0)
        {
            return items;
        }

        var gf = new GeometryFactory();
        IPreparedGeometry allowed;
        Envelope envelope;
        double budget;
        try
        {
            Geometry siteGeom = PortLayout.ToPolygon(gf, site);
            Geometry body = PortLayout.ToPolygon(gf, footprint);
            Geometry blocked = body.IsValid ? body : body.Buffer(0);
            if (keepClear != null && keepClear.Length >= 3)
            {
                blocked = blocked.Union(PortLayout.ToPolygon(gf, keepClear).Buffer(0));
            }
            Geometry free = (siteGeom.IsValid ? siteGeom : siteGeom.Buffer(0)).Buffer(-CLEARANCE)
                .Difference(blocked.Buffer(CLEARANCE));
            if (free.IsEmpty)
            {
                return items;
            }
            allowed = PreparedGeometryFactory.Prepare(free);
            envelope = free.EnvelopeInternal;
            budget = free.Area * FILL_RATIO;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[ScatterLayout] 空き地の計算に失敗しました: {ex.Message}。散布パーツを置きません。");
            return items;
        }

        Vector2 front = footprint[1] - footprint[0];
        Quaternion facing = front.sqrMagnitude > 1e-6f
            ? Quaternion.LookRotation(new Vector3(front.x, 0f, front.y), Vector3.up)
            : Quaternion.identity;
        var random = new System.Random(seed);
        var placed = new List<Polygon>();
        var available = new List<int>(kinds.Count);
        for (int i = 0; i < kinds.Count; i++)
        {
            available.Add(i);
        }
        double filled = 0.0;
        for (int attempt = 0; attempt < MAX_ATTEMPTS && filled < budget && items.Count < MAX_ITEMS && available.Count > 0; attempt++)
        {
            int pick = random.Next(available.Count);
            int kind = available[pick];
            Bounds bounds = kinds[kind].Bounds;
            Quaternion rotation = facing * Quaternion.Euler(0f, 90f * random.Next(4), 0f);
            var at = new Vector2(
                (float)(envelope.MinX + random.NextDouble() * envelope.Width),
                (float)(envelope.MinY + random.NextDouble() * envelope.Height));
            Vector2[] corners = PortLayout.RotatedRect(at, rotation, bounds);
            Polygon box = PortLayout.ToPolygon(gf, corners);
            if (!allowed.Covers(box) || placed.Exists(other => other.IsWithinDistance(box, CLEARANCE)))
            {
                continue;
            }
            placed.Add(box);
            items.Add(new Item { Kind = kind, Position = at, Rotation = rotation, Corners = corners });
            filled += bounds.size.x * bounds.size.z;
            if (kinds[kind].Single)
            {
                available.RemoveAt(pick);
            }
        }
        return items;
    }
}
