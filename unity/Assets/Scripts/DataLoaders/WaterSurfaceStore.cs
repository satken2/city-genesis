using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Query data for one non-sea water surface such as a lake (one GET /terrain/water element).
/// Water renders horizontally at Level over its entire footprint (polygon), but terrain above Level remains land
/// even inside the footprint. Actual water exists only where terrain inside the footprint is below Level
/// (the backend makes this area a coastline polygon hole; D_地形形状.md §D.7).
/// </summary>
public class WaterSurfaceRecord
{
    public string Id;
    public float Level;
    /// <summary>Outer boundaries and holes of all parts. Apply even-odd containment across every ring.</summary>
    public readonly List<Vector2[]> Rings = new List<Vector2[]>();
    public float MinX = float.PositiveInfinity, MinZ = float.PositiveInfinity;
    public float MaxX = float.NegativeInfinity, MaxZ = float.NegativeInfinity;

    public void AddRing(List<Vector2> ring)
    {
        if (ring == null) return;
        var pts = new List<Vector2>(ring);
        // Exclude GeoJSON closing points (start=end)
        if (pts.Count >= 2 && (pts[0] - pts[pts.Count - 1]).sqrMagnitude < 1e-6f) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 3) return;
        foreach (var p in pts)
        {
            MinX = Mathf.Min(MinX, p.x); MaxX = Mathf.Max(MaxX, p.x);
            MinZ = Mathf.Min(MinZ, p.y); MaxZ = Mathf.Max(MaxZ, p.y);
        }
        Rings.Add(pts.ToArray());
    }

    /// <summary>Whether (x, z) lies in the footprint (bounding rectangle rejection followed by even-odd across all rings).</summary>
    public bool Contains(float x, float z)
    {
        if (x < MinX || x > MaxX || z < MinZ || z > MaxZ) return false;
        var point = new Vector2(x, z);
        bool inside = false;
        foreach (var ring in Rings)
        {
            if (PolygonUtils.Contains(ring, point)) inside = !inside;
        }
        return inside;
    }
}

/// <summary>
/// Static lake water store. WaterSurfaceManager builds it from GET /terrain/water during session initialization;
/// RoadPlanManager reads it for sea/lake crossing checks during path placement. Equivalent to CoastlineStore for coastlines
/// and RiverStore for rivers.
/// </summary>
public static class WaterSurfaceStore
{
    private static readonly List<WaterSurfaceRecord> surfaces = new List<WaterSurfaceRecord>();

    public static IReadOnlyList<WaterSurfaceRecord> Surfaces => surfaces;

    public static void Clear()
    {
        surfaces.Clear();
    }

    public static void Add(WaterSurfaceRecord record)
    {
        if (record != null && record.Rings.Count > 0) surfaces.Add(record);
    }

    /// <summary>
    /// Return water whose footprint contains (x, z), or null. For overlapping footprints, choose the higher water level
    /// (same rule as backend geo_sea_lake water elevations).
    /// </summary>
    public static WaterSurfaceRecord FindAt(float x, float z)
    {
        WaterSurfaceRecord found = null;
        foreach (var rec in surfaces)
        {
            if (rec.Contains(x, z) && (found == null || rec.Level > found.Level)) found = rec;
        }
        return found;
    }
}
