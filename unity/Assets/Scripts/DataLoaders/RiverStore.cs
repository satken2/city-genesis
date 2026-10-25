using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// Query data for one river.
///
/// Rivers comprise an area polygon (polygon; holes are river islands) and a water elevation profile line (profile)
/// (backend GET /terrain/rivers; authoritative specification: D_地形形状.md §D.8). Water elevation at any position
/// inside the area equals y at the nearest point on the profile polyline, a shared contract with the terrain editor
/// (RiverProfileSampler in riverGeometry.ts) and the core.
///
/// Water meshes (RiverSurfaceManager) and interference occupancy boxes (RoadPlanManager / HighlightManager)
/// share the same grid (GridStep spacing, CornerMask), so store rasterized grid results here too.
/// </summary>
public class RiverRecord
{
    public string Id;
    /// <summary>All rings (outer boundaries and holes of all parts). Apply even-odd containment across all rings.</summary>
    public readonly List<Vector2[]> Rings = new List<Vector2[]>();
    public float MinX, MinZ, MaxX, MaxZ;

    // Profile line (x, y, z) and segment grid buckets for nearest-segment searches
    private float[] px, py, pz;
    // Signed curvature per segment (1/m; positive=left turn). Turning angles at both endpoint vertices divided by average segment length,
    // smoothed over neighboring segments. Used to identify sandy banks (inner bends)
    private float[] segCurvature;
    private int segCount;
    private float bucketSize;
    private float bx0, bz0;
    private int bw, bh;
    private List<int>[] buckets;

    // Grid shared by water meshes and occupancy boxes. Grid point (i, j) → (GridOriginX + i*GridStep, GridOriginZ + j*GridStep)
    public float GridOriginX, GridOriginZ, GridStep;
    public int GridW, GridH;
    /// <summary>1 for grid points inside the polygon (row-major GridW×GridH).</summary>
    public byte[] CornerMask;

    /// <summary>Whether cell (i, j), identified by its upper-left grid point, belongs to river occupancy (any corner is inside).</summary>
    public bool CellIncluded(int i, int j)
    {
        if (i < 0 || j < 0 || i >= GridW - 1 || j >= GridH - 1) return false;
        return (CornerMask[j * GridW + i] | CornerMask[j * GridW + i + 1]
            | CornerMask[(j + 1) * GridW + i] | CornerMask[(j + 1) * GridW + i + 1]) != 0;
    }

    /// <summary>
    /// Render-only margin extending into banks. Does not expand occupancy checks or riverbed masks.
    /// Terrain and water use different grids/triangulation; clipping exactly at banks exposes water plane edges on curves.
    /// </summary>
    public bool SurfaceCellIncluded(int i, int j)
    {
        for (int y = j - 1; y <= j + 1; y++)
            for (int x = i - 1; x <= i + 1; x++)
                if (CellIncluded(x, y)) return true;
        return false;
    }

    /// <summary>Set profile points and build nearest-segment search buckets.</summary>
    public void SetProfile(List<Vector3> profile, float bucket = 64f)
    {
        int n = profile.Count;
        px = new float[n]; py = new float[n]; pz = new float[n];
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            px[i] = profile[i].x; py[i] = profile[i].y; pz[i] = profile[i].z;
            minX = Mathf.Min(minX, px[i]); maxX = Mathf.Max(maxX, px[i]);
            minZ = Mathf.Min(minZ, pz[i]); maxZ = Mathf.Max(maxZ, pz[i]);
        }
        segCount = Mathf.Max(0, n - 1);
        bucketSize = Mathf.Max(1f, bucket);
        if (n == 0) { minX = maxX = minZ = maxZ = 0f; }
        bx0 = minX; bz0 = minZ;
        bw = Mathf.Max(1, Mathf.FloorToInt((maxX - minX) / bucketSize) + 1);
        bh = Mathf.Max(1, Mathf.FloorToInt((maxZ - minZ) / bucketSize) + 1);
        // Signed segment curvature: divide vertex i turning angle (atan2 of adjacent direction cross products) by average adjacent segment length,
        // then average each segment's endpoint curvatures and smooth through a window of three segments before/after
        var vertexCurv = new float[n];
        for (int i = 1; i < n - 1; i++)
        {
            float d0x = px[i] - px[i - 1], d0z = pz[i] - pz[i - 1];
            float d1x = px[i + 1] - px[i], d1z = pz[i + 1] - pz[i];
            float l0 = Mathf.Sqrt(d0x * d0x + d0z * d0z), l1 = Mathf.Sqrt(d1x * d1x + d1z * d1z);
            if (l0 < 1e-6f || l1 < 1e-6f) continue;
            float cross = d0x * d1z - d0z * d1x;
            float dot = d0x * d1x + d0z * d1z;
            vertexCurv[i] = Mathf.Atan2(cross, dot) / (0.5f * (l0 + l1));
        }
        var raw = new float[segCount];
        for (int s = 0; s < segCount; s++) raw[s] = 0.5f * (vertexCurv[s] + vertexCurv[s + 1]);
        segCurvature = new float[segCount];
        for (int s = 0; s < segCount; s++)
        {
            float sum = 0f; int count = 0;
            for (int k = -3; k <= 3; k++)
            {
                int j = s + k;
                if (j < 0 || j >= segCount) continue;
                sum += raw[j]; count++;
            }
            segCurvature[s] = count > 0 ? sum / count : 0f;
        }

        buckets = new List<int>[bw * bh];
        for (int s = 0; s < segCount; s++)
        {
            int cMin = Mathf.FloorToInt((Mathf.Min(px[s], px[s + 1]) - bx0) / bucketSize);
            int cMax = Mathf.FloorToInt((Mathf.Max(px[s], px[s + 1]) - bx0) / bucketSize);
            int rMin = Mathf.FloorToInt((Mathf.Min(pz[s], pz[s + 1]) - bz0) / bucketSize);
            int rMax = Mathf.FloorToInt((Mathf.Max(pz[s], pz[s + 1]) - bz0) / bucketSize);
            for (int r = rMin; r <= rMax; r++)
                for (int c = cMin; c <= cMax; c++)
                {
                    int bi = r * bw + c;
                    (buckets[bi] ??= new List<int>()).Add(s);
                }
        }
    }

    /// <summary>Return the profile segment nearest (x, z), its projection parameter, and distance.</summary>
    public void Nearest(float x, float z, out int seg, out float t, out float dist)
    {
        seg = 0; t = 0f; dist = float.PositiveInfinity;
        if (segCount == 0) return;
        int cq = Mathf.FloorToInt((x - bx0) / bucketSize);
        int rq = Mathf.FloorToInt((z - bz0) / bucketSize);
        int maxRing = Mathf.Max(bw, bh) + Mathf.Abs(cq) + Mathf.Abs(rq) + 2;
        for (int ring = 0; ring <= maxRing; ring++)
        {
            // Segments outside this ring are at least (ring-1)*bucketSize away, so stop searching
            if (dist <= (ring - 1) * bucketSize) break;
            int rMin = rq - ring, rMax = rq + ring, cMin = cq - ring, cMax = cq + ring;
            for (int r = rMin; r <= rMax; r++)
            {
                if (r < 0 || r >= bh) continue;
                bool edgeRow = r == rMin || r == rMax;
                for (int c = cMin; c <= cMax; c++)
                {
                    if (c < 0 || c >= bw) continue;
                    if (!edgeRow && c != cMin && c != cMax) continue;
                    var list = buckets[r * bw + c];
                    if (list == null) continue;
                    foreach (int s in list)
                    {
                        float ax = px[s], az = pz[s];
                        float dx = px[s + 1] - ax, dz = pz[s + 1] - az;
                        float len2 = dx * dx + dz * dz;
                        float tt = len2 > 1e-12f ? ((x - ax) * dx + (z - az) * dz) / len2 : 0f;
                        tt = Mathf.Clamp01(tt);
                        float ex = ax + dx * tt - x, ez = az + dz * tt - z;
                        float d = Mathf.Sqrt(ex * ex + ez * ez);
                        if (d < dist) { dist = d; seg = s; t = tt; }
                    }
                }
            }
        }
    }

    /// <summary>Water elevation at (x, z), linearly interpolating y on the nearest segment.</summary>
    public float WaterLevelAt(float x, float z)
    {
        if (segCount == 0) return py != null && py.Length > 0 ? py[0] : 0f;
        Nearest(x, z, out int seg, out float t, out _);
        return py[seg] + (py[seg + 1] - py[seg]) * t;
    }

    /// <summary>
    /// Inner-bank factor (0..1) at (x, z). If nearest-segment signed curvature and the point's side sign
    /// match (inside the bend), return curvature magnitude normalized by curvRef. Outer banks/straight sections return 0.
    /// </summary>
    public float InnerBankFactorAt(float x, float z, float curvRef)
    {
        if (segCount == 0) return 0f;
        Nearest(x, z, out int seg, out float t, out _);
        float curv = segCurvature[seg];
        if (Mathf.Abs(curv) < 1e-9f) return 0f;
        float dx = px[seg + 1] - px[seg], dz = pz[seg + 1] - pz[seg];
        float nx = px[seg] + dx * t, nz = pz[seg] + dz * t;
        // Positive on the left of downstream direction (left-turn inner banks are left)
        float side = dx * (z - nz) - dz * (x - nx);
        if (side * curv <= 0f) return 0f;
        return Mathf.Clamp01(Mathf.Abs(curv) / Mathf.Max(curvRef, 1e-6f));
    }

    /// <summary>Downstream direction at (x, z), the nearest segment's unit tangent in XZ.</summary>
    public Vector2 FlowDirAt(float x, float z)
    {
        if (segCount == 0) return new Vector2(1f, 0f);
        Nearest(x, z, out int seg, out _, out _);
        var d = new Vector2(px[seg + 1] - px[seg], pz[seg + 1] - pz[seg]);
        return d.sqrMagnitude > 1e-12f ? d.normalized : new Vector2(1f, 0f);
    }

    /// <summary>Whether (x, z) lies in the area (bounding rectangle rejection followed by even-odd across all rings).</summary>
    public bool Contains(float x, float z)
    {
        if (x < MinX || x > MaxX || z < MinZ || z > MaxZ) return false;
        Vector2 point = new Vector2(x, z);
        bool inside = false;
        foreach (var ring in Rings)
        {
            if (PolygonUtils.Contains(ring, point)) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// Rasterize polygons into grid-point masks (scanline + even-odd).
    /// Same rules as rasterizePolygonMask in terrain editor riverGeometry.ts.
    /// </summary>
    public void BuildGrid(float step)
    {
        GridStep = step;
        GridOriginX = Mathf.Floor(MinX / step) * step - 2 * step;
        GridOriginZ = Mathf.Floor(MinZ / step) * step - 2 * step;
        GridW = Mathf.CeilToInt((MaxX - GridOriginX) / step) + 3;
        GridH = Mathf.CeilToInt((MaxZ - GridOriginZ) / step) + 3;
        CornerMask = new byte[GridW * GridH];

        var edges = new List<float>();
        foreach (var ring in Rings)
        {
            int n = ring.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if (ring[j].y == ring[i].y) continue;
                edges.Add(ring[j].x); edges.Add(ring[j].y); edges.Add(ring[i].x); edges.Add(ring[i].y);
            }
        }
        int edgeCount = edges.Count / 4;
        var crossings = new List<float>();
        for (int j = 0; j < GridH; j++)
        {
            float z = GridOriginZ + j * step;
            crossings.Clear();
            for (int e = 0; e < edgeCount; e++)
            {
                float x1 = edges[e * 4], z1 = edges[e * 4 + 1], x2 = edges[e * 4 + 2], z2 = edges[e * 4 + 3];
                if ((z1 > z) == (z2 > z)) continue;
                crossings.Add(x1 + (z - z1) * (x2 - x1) / (z2 - z1));
            }
            if (crossings.Count < 2) continue;
            crossings.Sort();
            for (int k = 0; k + 1 < crossings.Count; k += 2)
            {
                int iStart = Mathf.Max(0, Mathf.CeilToInt((crossings[k] - GridOriginX) / step));
                int iEnd = Mathf.Min(GridW - 1, Mathf.FloorToInt((crossings[k + 1] - GridOriginX) / step));
                for (int i = iStart; i <= iEnd; i++) CornerMask[j * GridW + i] = 1;
            }
        }
    }
}

/// <summary>
/// Static river store. RiverSurfaceManager builds it from GET /terrain/rivers during session initialization;
/// water mesh generation, river-crossing checks during path placement (RoadPlanManager), and authoritative interference highlights
/// (HighlightManager) read it. Equivalent role to CoastlineStore for coastlines.
/// </summary>
public static class RiverStore
{
    /// <summary>Grid spacing (m) for water meshes/occupancy boxes. Finer than terrain grids (10m) to follow banks.</summary>
    public const float GRID_STEP = 4f;
    /// <summary>Depth (m) of occupancy box bottoms below water. Use a fixed value because riverbed depths vary per river.</summary>
    public const float OCCUPANCY_BOX_DEPTH = 4f;

    private static readonly List<RiverRecord> rivers = new List<RiverRecord>();

    public static IReadOnlyList<RiverRecord> Rivers => rivers;
    public static bool HasRivers => rivers.Count > 0;

    public static void Clear()
    {
        rivers.Clear();
    }

    /// <summary>Build the store from the GET /terrain/rivers rivers array (JToken).</summary>
    public static void SetFromJson(JToken riversToken)
    {
        rivers.Clear();
        if (!(riversToken is JArray arr)) return;
        foreach (JToken item in arr)
        {
            var rec = Parse(item);
            if (rec != null) rivers.Add(rec);
        }
    }

    private static RiverRecord Parse(JToken item)
    {
        if (item == null) return null;
        var rec = new RiverRecord { Id = item["id"]?.ToString() ?? "" };
        JToken polygon = item["polygon"];
        string gtype = polygon?["type"]?.ToString();
        JToken coords = polygon?["coordinates"];
        if (coords == null) return null;
        if (gtype == "Polygon") AddRings(rec, coords);
        else if (gtype == "MultiPolygon") foreach (JToken part in coords) AddRings(rec, part);
        if (rec.Rings.Count == 0) return null;

        rec.MinX = rec.MinZ = float.PositiveInfinity;
        rec.MaxX = rec.MaxZ = float.NegativeInfinity;
        foreach (var ring in rec.Rings)
            foreach (var p in ring)
            {
                rec.MinX = Mathf.Min(rec.MinX, p.x); rec.MaxX = Mathf.Max(rec.MaxX, p.x);
                rec.MinZ = Mathf.Min(rec.MinZ, p.y); rec.MaxZ = Mathf.Max(rec.MaxZ, p.y);
            }

        var profile = new List<Vector3>();
        if (item["profile"] is JArray prof)
            foreach (JToken p in prof)
                if (p is JArray pa && pa.Count >= 3)
                    profile.Add(new Vector3((float)pa[0], (float)pa[1], (float)pa[2]));
        if (profile.Count < 2) return null;
        rec.SetProfile(profile);
        rec.BuildGrid(GRID_STEP);
        return rec;
    }

    private static void AddRings(RiverRecord rec, JToken rings)
    {
        if (!(rings is JArray arr)) return;
        foreach (JToken ringToken in arr)
        {
            if (!(ringToken is JArray ring)) continue;
            var pts = new List<Vector2>(ring.Count);
            foreach (JToken p in ring)
                if (p is JArray pa && pa.Count >= 2) pts.Add(new Vector2((float)pa[0], (float)pa[1]));
            // Exclude GeoJSON closing points (start=end)
            if (pts.Count >= 2 && (pts[0] - pts[pts.Count - 1]).sqrMagnitude < 1e-6f) pts.RemoveAt(pts.Count - 1);
            if (pts.Count >= 3) rec.Rings.Add(pts.ToArray());
        }
    }

    /// <summary>Return the river containing (x, z), or null.</summary>
    public static RiverRecord FindAt(float x, float z)
    {
        foreach (var rec in rivers)
            if (rec.Contains(x, z)) return rec;
        return null;
    }

    public static RiverRecord FindById(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var rec in rivers)
            if (rec.Id == id) return rec;
        return null;
    }

    /// <summary>
    /// Append translucent river occupancy boxes (top=water+ε, bottom=water-OCCUPANCY_BOX_DEPTH, perimeter sidewalls)
    /// to triangle lists. With nearPoints, create boxes only for cells within radius of any point
    /// (show only near crossings of the path being placed). Null means the entire river.
    /// Assumes the same warning color as road occupancy tubes (OccupancyVolumeUtil.AddTube).
    /// </summary>
    public static void AddOccupancyBox(List<Vector3> triangles, RiverRecord river, List<Vector3> nearPoints, float radius)
    {
        if (river == null || river.CornerMask == null) return;
        int w = river.GridW, h = river.GridH;
        float step = river.GridStep;
        float r2 = radius * radius;
        var cells = new HashSet<int>();
        for (int j = 0; j < h - 1; j++)
        {
            for (int i = 0; i < w - 1; i++)
            {
                if (!river.CellIncluded(i, j)) continue;
                if (nearPoints != null)
                {
                    float cx = river.GridOriginX + (i + 0.5f) * step;
                    float cz = river.GridOriginZ + (j + 0.5f) * step;
                    bool near = false;
                    foreach (var p in nearPoints)
                    {
                        float dx = p.x - cx, dz = p.z - cz;
                        if (dx * dx + dz * dz <= r2) { near = true; break; }
                    }
                    if (!near) continue;
                }
                cells.Add(j * w + i);
            }
        }
        if (cells.Count == 0) return;

        Vector3 Corner(int i, int j, float dy)
        {
            float x = river.GridOriginX + i * step;
            float z = river.GridOriginZ + j * step;
            return new Vector3(x, river.WaterLevelAt(x, z) + dy, z);
        }
        const float top = 0.1f;
        float bottom = -OCCUPANCY_BOX_DEPTH;
        foreach (int cell in cells)
        {
            int i = cell % w, j = cell / w;
            Vector3 tl = Corner(i, j, top), tr = Corner(i + 1, j, top), bl = Corner(i, j + 1, top), br = Corner(i + 1, j + 1, top);
            Vector3 tl2 = Corner(i, j, bottom), tr2 = Corner(i + 1, j, bottom), bl2 = Corner(i, j + 1, bottom), br2 = Corner(i + 1, j + 1, bottom);
            AddQuad(triangles, tl, tr, br, bl);      // Top
            AddQuad(triangles, tl2, bl2, br2, tr2);  // Bottom
            if (!cells.Contains(j * w + i - 1) || i == 0) AddQuad(triangles, tl, bl, bl2, tl2);           // Left wall
            if (!cells.Contains(j * w + i + 1)) AddQuad(triangles, tr, tr2, br2, br);                     // Right wall
            if (!cells.Contains((j - 1) * w + i) || j == 0) AddQuad(triangles, tl, tl2, tr2, tr);        // Near wall
            if (!cells.Contains((j + 1) * w + i)) AddQuad(triangles, bl, br, br2, bl2);                   // Far wall
        }
    }

    private static void AddQuad(List<Vector3> triangles, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        triangles.Add(a); triangles.Add(b); triangles.Add(c);
        triangles.Add(a); triangles.Add(c); triangles.Add(d);
    }
}
