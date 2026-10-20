using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Reclaimed-land perimeter sections (quays/walls). World XZ coordinates (x=X, y=Z); Normal is the outward unit vector.
/// </summary>
public readonly struct ReclaimedLandSegment
{
    public readonly Vector2 A;
    public readonly Vector2 B;
    public readonly Vector2 Normal;

    public ReclaimedLandSegment(Vector2 a, Vector2 b, Vector2 normal)
    {
        A = a;
        B = b;
        Normal = normal;
    }
}

/// <summary>One quay section and absolute crest elevation of its reclaimed land (for port crane placement).</summary>
public readonly struct ReclaimedQuay
{
    public readonly int LandId;
    public readonly ReclaimedLandSegment Segment;
    public readonly float TopHeight;

    public ReclaimedQuay(int landId, ReclaimedLandSegment segment, float topHeight)
    {
        LandId = landId;
        Segment = segment;
        TopHeight = topHeight;
    }
}

/// <summary>
/// One reclaimed-land record (one GET /terrain/reclaimed-lands element). Id, Version, and Ring are stored values;
/// crest heights, quays, walls, and bands are backend (geo_reclaimed_land) derivatives from coastline and terrain.
/// Unity only renders and queries these (duplicating quay rules would misalign endpoints/crests).
/// </summary>
public sealed class ReclaimedLandRecord
{
    public int Id;
    public int Version;
    public Vector2[] Ring;                  // Outer boundary, excluding closing point
    public float? TopHeight;                // Absolute crest elevation; null without quays
    public ReclaimedLandSegment[] Quays;    // Quay sections facing the sea
    public ReclaimedLandSegment[] Walls;    // Sections with vertical faces (quays and returns where bands extend along land-side edges)
    public Vector2[][][] Band;              // Band polygons covered by crests. Each polygon is [outer boundary, holes...]
    public Rect[] BandBounds;               // Bounding rectangle per Band polygon
    public Rect Bounds;                     // Bounding rectangle covering Ring, Band, and Walls
}

/// <summary>
/// Static reclaimed-land store. <see cref="ReclaimedLandManager"/> builds it from GET /terrain/reclaimed-lands;
/// grounding (crest within bands in <see cref="Utils.GetGroundHeight"/>) and port quay checks read it.
/// Equivalent role to CoastlineStore for coastlines and WaterSurfaceStore for lakes.
/// </summary>
public static class ReclaimedLandStore
{
    private static readonly List<ReclaimedLandRecord> lands = new List<ReclaimedLandRecord>();
    // Bounding rectangle of all reclaimed-land bands. Grounding is a hot path, so return in one comparison if no bands or outside.
    private static bool hasBand;
    private static float bandMinX, bandMinZ, bandMaxX, bandMaxZ;

    /// <summary>Reclaimed land in ascending Id order.</summary>
    public static IReadOnlyList<ReclaimedLandRecord> Lands => lands;

    /// <summary>
    /// Content generation, advanced only when the set of reclaimed land or any contents (geometry, crest, quays, walls, bands) change.
    /// Advancing on every refetch would rebuild every port using this in its rendering key each epoch.
    /// </summary>
    public static int ContentVersion { get; private set; }

    public static ReclaimedLandRecord Find(int id)
    {
        for (int i = 0; i < lands.Count; i++)
        {
            if (lands[i].Id == id) return lands[i];
        }
        return null;
    }

    /// <summary>
    /// Replace contents with records. Do nothing and return false if identical.
    /// On changes, advance <see cref="ContentVersion"/> and discard the grounding frame cache.
    /// </summary>
    public static bool Replace(IReadOnlyList<ReclaimedLandRecord> records)
    {
        var sorted = new List<ReclaimedLandRecord>();
        if (records != null)
        {
            foreach (var record in records)
            {
                if (record != null) sorted.Add(record);
            }
        }
        sorted.Sort((a, b) => a.Id.CompareTo(b.Id));

        if (SameContent(lands, sorted)) return false;

        lands.Clear();
        lands.AddRange(sorted);
        RebuildBandBounds();
        ContentVersion++;
        Utils.InvalidateHeightCache();
        return true;
    }

    /// <summary>Discard stored reclaimed land. Called on session switches.</summary>
    public static void Clear()
    {
        if (lands.Count == 0) return;
        lands.Clear();
        RebuildBandBounds();
        ContentVersion++;
        Utils.InvalidateHeightCache();
    }

    /// <summary>
    /// If (x, z) lies within any reclaimed-land band, return its absolute crest elevation.
    /// Band polygons may have holes; apply even-odd tests across outer and hole rings.
    /// </summary>
    public static bool TryGetBandTop(float x, float z, out float top)
    {
        top = 0f;
        if (!hasBand || x < bandMinX || x > bandMaxX || z < bandMinZ || z > bandMaxZ) return false;

        bool found = false;
        var point = new Vector2(x, z);
        for (int l = 0; l < lands.Count; l++)
        {
            ReclaimedLandRecord land = lands[l];
            if (!land.TopHeight.HasValue || !Contains(land.Bounds, x, z)) continue;
            Vector2[][][] band = land.Band;
            for (int p = 0; p < band.Length; p++)
            {
                if (!Contains(land.BandBounds[p], x, z)) continue;
                bool inside = false;
                Vector2[][] rings = band[p];
                for (int r = 0; r < rings.Length; r++)
                {
                    if (PolygonUtils.Contains(rings[r], point)) inside = !inside;
                }
                if (!inside) continue;
                float landTop = land.TopHeight.Value;
                if (!found || landTop > top) top = landTop;
                found = true;
                break;
            }
        }
        return found;
    }

    /// <summary>
    /// Add quay sections whose bounding rectangles overlap area to result (clear result first).
    /// Entry point for ports checking whether they face a reclaimed-land quay, avoiding duplicate quay rules in port code.
    /// </summary>
    public static void GetQuaySegments(Rect area, List<ReclaimedQuay> result)
    {
        result.Clear();
        for (int l = 0; l < lands.Count; l++)
        {
            ReclaimedLandRecord land = lands[l];
            if (!land.TopHeight.HasValue || !Overlaps(land.Bounds, area)) continue;
            foreach (var quay in land.Quays)
            {
                float minX = Mathf.Min(quay.A.x, quay.B.x), maxX = Mathf.Max(quay.A.x, quay.B.x);
                float minZ = Mathf.Min(quay.A.y, quay.B.y), maxZ = Mathf.Max(quay.A.y, quay.B.y);
                if (maxX < area.xMin || minX > area.xMax || maxZ < area.yMin || minZ > area.yMax) continue;
                result.Add(new ReclaimedQuay(land.Id, quay, land.TopHeight.Value));
            }
        }
    }

    /// <summary>Whether two reclaimed-land records have identical contents (all stored and derived values).</summary>
    public static bool SameContent(ReclaimedLandRecord a, ReclaimedLandRecord b)
    {
        if (a == null || b == null) return a == b;
        return a.Id == b.Id && a.Version == b.Version && a.TopHeight == b.TopHeight &&
               SamePoints(a.Ring, b.Ring) && SameSegments(a.Quays, b.Quays) && SameSegments(a.Walls, b.Walls) &&
               SameBand(a.Band, b.Band);
    }

    private static bool SameContent(IReadOnlyList<ReclaimedLandRecord> a, IReadOnlyList<ReclaimedLandRecord> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!SameContent(a[i], b[i])) return false;
        }
        return true;
    }

    private static bool SamePoints(Vector2[] a, Vector2[] b)
    {
        if (a == null || b == null) return a == b;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private static bool SameSegments(ReclaimedLandSegment[] a, ReclaimedLandSegment[] b)
    {
        if (a == null || b == null) return a == b;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].A != b[i].A || a[i].B != b[i].B || a[i].Normal != b[i].Normal) return false;
        }
        return true;
    }

    private static bool SameBand(Vector2[][][] a, Vector2[][][] b)
    {
        if (a == null || b == null) return a == b;
        if (a.Length != b.Length) return false;
        for (int p = 0; p < a.Length; p++)
        {
            if (a[p] == null || b[p] == null)
            {
                if (a[p] != b[p]) return false;
                continue;
            }
            if (a[p].Length != b[p].Length) return false;
            for (int r = 0; r < a[p].Length; r++)
            {
                if (!SamePoints(a[p][r], b[p][r])) return false;
            }
        }
        return true;
    }

    private static void RebuildBandBounds()
    {
        hasBand = false;
        bandMinX = bandMinZ = float.PositiveInfinity;
        bandMaxX = bandMaxZ = float.NegativeInfinity;
        foreach (var land in lands)
        {
            if (!land.TopHeight.HasValue || land.Band == null) continue;
            foreach (Rect rect in land.BandBounds)
            {
                hasBand = true;
                bandMinX = Mathf.Min(bandMinX, rect.xMin);
                bandMinZ = Mathf.Min(bandMinZ, rect.yMin);
                bandMaxX = Mathf.Max(bandMaxX, rect.xMax);
                bandMaxZ = Mathf.Max(bandMaxZ, rect.yMax);
            }
        }
    }

    // Rect.Contains excludes upper edges; use comparisons counting boundary points as inside.
    private static bool Contains(Rect rect, float x, float z) =>
        x >= rect.xMin && x <= rect.xMax && z >= rect.yMin && z <= rect.yMax;

    private static bool Overlaps(Rect a, Rect b) =>
        a.xMin <= b.xMax && a.xMax >= b.xMin && a.yMin <= b.yMax && a.yMax >= b.yMin;
}
