using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;
using GeneratedModels;

/// <summary>
/// Component that overlays the coverage of infrastructure supply facilities
/// (facilities producing service goods, such as substations, sewage treatment plants, and radio towers) in 3D.
///
/// Draws two region types (circles or irregular polygons) simultaneously in different colors.
///   - Preview region (green): coverage if the selected facility is built there
///     (from backend POST /api/v1/buildings/preview-effect-range)
///   - Existing regions (blue): coverage already supplied by facilities producing the same service goods
///     (from GET /api/v1/buildings/effect-ranges, a list of regions for multiple facilities)
/// Both region types use two terrain-following layers:
///   1. Region fill: a translucent surface covering the entire area, including wilderness without parcels.
///      Splitting the region AABB into a grid and tiling cells inside it with small quads
///      supports arbitrary shapes (including concavities and holes) while following terrain.
///   2. Boundary outline: outlines the perimeter and holes to clearly show coverage limits.
///
/// Fill cells are planar and may intersect terrain, so use the default Custom/OverlayLine (LEqul + negative depth offset)
/// to follow the ground and naturally hide parts behind hills. Boundary outlines are guides that must stay visible,
/// so draw them in front of terrain with a _ZTest=Always material (as with RoadPlanManager gizmos).
///
/// Geometry is static and independent of the camera; rebuild meshes only when data changes (SetData),
/// rather than every frame.
/// </summary>
public class EffectRangeOverlay : MonoBehaviour
{
    // Region fill (terrain-following, LEqul). Parts behind hills may be hidden.
    private OverlayMeshRenderer fillRenderer;
    // Boundary and parcel outlines (always in front=ZTest Always).
    private OverlayMeshRenderer lineRenderer;
    private Camera cam;

    // Preview region surface (translucent green) and perimeter/hole outlines.
    private static readonly Color FillColor = new Color(0.30f, 0.78f, 0.45f, 0.20f);
    private static readonly Color BoundaryColor = new Color(0.36f, 0.92f, 0.52f, 0.95f);
    // Existing facility coverage surface (translucent blue) and outlines. Match the UI legend colors (BuildingConstructionPanel.css).
    private static readonly Color ExistingFillColor = new Color(0.27f, 0.59f, 0.94f, 0.15f);
    private static readonly Color ExistingBoundaryColor = new Color(0.35f, 0.70f, 1.00f, 0.80f);

    private void Awake()
    {
        cam = Camera.main;
        EnsureRenderers();
    }

    private void EnsureRenderers()
    {
        // Depth-test the fill so buildings occlude it; keep only boundaries always in front.
        if (fillRenderer == null)
            fillRenderer = OverlayMeshRenderer.Create("EffectRangeFillOverlay", alwaysOnTop: false, parent: transform);
        if (lineRenderer == null)
            lineRenderer = OverlayMeshRenderer.Create("EffectRangeLineOverlay", alwaysOnTop: true, parent: transform);
    }

    /// <summary>Clears all coverage displays.</summary>
    public void Clear()
    {
        EnsureRenderers();
        fillRenderer.ClearMesh();
        lineRenderer.ClearMesh();
    }

    /// <summary>
    /// Receives coverage data and builds fills and boundaries.
    /// Clears the display if payload is null or requests a clear.
    /// Stack existing regions (blue) first and the preview (green) afterward
    /// to make the preview color more visible where they overlap.
    /// </summary>
    public void SetData(EffectRangePayload payload)
    {
        EnsureRenderers();
        if (cam == null) cam = Camera.main;
        if (payload == null || payload.IsClear)
        {
            Clear();
            return;
        }

        // Ground heights use a per-frame 1m grid cache; invalidate it to fill using current heights.
        Utils.InvalidateHeightCache();

        fillRenderer.BeginFrame();
        lineRenderer.BeginFrame();

        foreach (var existing in payload.existing)
        {
            BuildRegion(existing, ExistingFillColor, ExistingBoundaryColor);
        }
        if (payload.HasPreviewRegion)
        {
            BuildRegion(payload.region, FillColor, BoundaryColor);
        }

        fillRenderer.EndFrame(cam);
        lineRenderer.EndFrame(cam);
    }

    /// <summary>Builds the fill and boundary of one region (circle or polygon) in the specified color.</summary>
    private void BuildRegion(EffectRangeRegionData region, Color fillColor, Color boundaryColor)
    {
        if (region == null || string.IsNullOrEmpty(region.type)) return;
        var rings = region.Rings();
        FillRings(rings, fillColor);
        foreach (var ring in rings)
        {
            DrawWorldRing(lineRenderer, ring, boundaryColor, 1.6f, 6f);
        }
    }

    // =====================================================================
    // Region fill (grid tiling)
    // =====================================================================

    /// <summary>
    /// Splits the rings' AABB into a grid and fills cells whose centers lie inside (circles use the same polygon as their boundary).
    /// For each row, find all ring intersections with a horizontal line through cell centers once, then fill alternating sorted pairs (even-odd rule).
    /// Holes remain empty without distinguishing them from the perimeter. Checking all vertices per cell would take seconds for coverage with thousands of vertices along roads.
    /// Cells are small, so sampling terrain height at all four corners follows the relief.
    /// </summary>
    private void FillRings(List<List<Vector2>> rings, Color fillColor)
    {
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var ring in rings)
        {
            foreach (var p in ring)
            {
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minZ) minZ = p.y;
                if (p.y > maxZ) maxZ = p.y;
            }
        }
        float span = Mathf.Max(maxX - minX, maxZ - minZ);
        if (span <= 0f) return;
        // Adjust the step to the coverage size (4m–20m) to keep the cell count manageable.
        float step = Mathf.Clamp(span / 70f, 4f, 20f);
        int columns = Mathf.CeilToInt((maxX - minX) / step);
        int rows = Mathf.CeilToInt((maxZ - minZ) / step);

        var crossings = new List<float>();
        var quad = new List<Vector3>(4);
        for (int row = 0; row < rows; row++)
        {
            float z = minZ + row * step;
            crossings.Clear();
            foreach (var ring in rings)
            {
                PolygonUtils.AppendRayCrossings(ring, z + step * 0.5f, crossings);
            }
            crossings.Sort();
            for (int i = 0; i + 1 < crossings.Count; i += 2)
            {
                int left = Mathf.Max(0, Mathf.CeilToInt((crossings[i] - minX) / step - 0.5f));
                int right = Mathf.Min(columns - 1, Mathf.FloorToInt((crossings[i + 1] - minX) / step - 0.5f));
                for (int col = left; col <= right; col++)
                {
                    float x = minX + col * step;
                    quad.Clear();
                    quad.Add(GroundPoint(x, z));
                    quad.Add(GroundPoint(x + step, z));
                    quad.Add(GroundPoint(x + step, z + step));
                    quad.Add(GroundPoint(x, z + step));
                    fillRenderer.AddPolygon(quad, fillColor);
                }
            }
        }
    }

    // =====================================================================
    // Drawing helpers
    // =====================================================================

    /// <summary>
    /// Draws a closed ring as a solid terrain-following line of fixed world width.
    /// Subdivides each edge at subdivideStep intervals and samples terrain height at every vertex.
    /// </summary>
    private void DrawWorldRing(OverlayMeshRenderer renderer, List<Vector2> ring, Color color, float worldThickness, float subdivideStep)
    {
        int count = ring.Count;
        if (count < 2) return;
        for (int i = 0; i < count; i++)
        {
            Vector2 a = ring[i];
            Vector2 b = ring[(i + 1) % count];
            float d = Vector2.Distance(a, b);
            int seg = Mathf.Max(1, Mathf.CeilToInt(d / subdivideStep));
            Vector3 prev = GroundPoint(a.x, a.y);
            for (int k = 1; k <= seg; k++)
            {
                float t = (float)k / seg;
                Vector2 p = Vector2.Lerp(a, b, t);
                Vector3 cur = GroundPoint(p.x, p.y);
                // Set gap to 0 for a solid line (use a sufficiently large dashLen).
                renderer.AddDashedLineWorld(prev, cur, color, worldThickness, 1000f, 0f);
                prev = cur;
            }
        }
    }

    /// <summary>Returns a terrain point at world (x, z). Treat backend coordinates directly as Unity world coordinates.</summary>
    private Vector3 GroundPoint(float x, float z)
    {
        float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
        return new Vector3(x, y + 0.05f, z);
    }
}

// =====================================================================
// Coverage payload (raw data parsed from JSON received via IPC)
// =====================================================================

/// <summary>Coverage region: a circle or irregular polygon.</summary>
public class EffectRangeRegionData
{
    public string type;                                  // "circle" | "polygon"
    public Vector2 center;                               // circle
    public float radius;                                 // circle
    public List<EffectRangePolygonData> polygons = new List<EffectRangePolygonData>(); // polygon

    /// <summary>
    /// Enumerates closed boundary rings (world XZ). Circles use a radius-dependent polygon; polygons use the perimeter and holes.
    /// </summary>
    public List<List<Vector2>> Rings()
    {
        var rings = new List<List<Vector2>>();
        if (type == "circle")
        {
            if (radius <= 0f) return rings;
            int segments = Mathf.Clamp(Mathf.CeilToInt(radius / 4f), 24, 160);
            var ring = new List<Vector2>(segments);
            for (int i = 0; i < segments; i++)
            {
                float a = (float)i / segments * Mathf.PI * 2f;
                ring.Add(new Vector2(center.x + Mathf.Cos(a) * radius, center.y + Mathf.Sin(a) * radius));
            }
            rings.Add(ring);
            return rings;
        }
        foreach (var poly in polygons)
        {
            rings.Add(poly.exterior);
            rings.AddRange(poly.holes);
        }
        return rings;
    }

    /// <summary>Parses a JObject for one region returned by the core. Returns null for null or invalid formats.</summary>
    public static EffectRangeRegionData Parse(JToken regionToken)
    {
        if (regionToken == null || regionToken.Type == JTokenType.Null) return null;
        var obj = regionToken as JObject;
        if (obj == null) return null;

        string type = (string)obj["type"];
        var region = new EffectRangeRegionData { type = type };
        if (type == "circle")
        {
            var c = obj["center"] as JArray;
            if (c != null && c.Count >= 2)
                region.center = new Vector2((float)c[0], (float)c[1]);
            region.radius = obj["radius"] != null ? (float)obj["radius"] : 0f;
        }
        else if (type == "polygon")
        {
            var polys = obj["polygons"] as JArray;
            if (polys != null)
            {
                foreach (var p in polys)
                {
                    var pd = new EffectRangePolygonData
                    {
                        exterior = GeneratedModelUtils.ParseRing(p["exterior"]),
                    };
                    var holes = p["holes"] as JArray;
                    if (holes != null)
                    {
                        foreach (var h in holes)
                        {
                            var hr = GeneratedModelUtils.ParseRing(h);
                            if (hr.Count >= 3) pd.holes.Add(hr);
                        }
                    }
                    if (pd.exterior.Count >= 3) region.polygons.Add(pd);
                }
            }
        }
        return region;
    }
}

/// <summary>One polygon in an irregular region (perimeter + holes). Coordinates use world XZ.</summary>
public class EffectRangePolygonData
{
    public List<Vector2> exterior = new List<Vector2>();
    public List<List<Vector2>> holes = new List<List<Vector2>>();
}

/// <summary>Payload for edit_building:update_effect_range.</summary>
public class EffectRangePayload
{
    public EffectRangeRegionData region;   // Preview region. null means no preview.
    // List of regions covered by existing facilities. Empty means no existing display.
    public List<EffectRangeRegionData> existing = new List<EffectRangeRegionData>();

    /// <summary>Whether both preview and existing regions are absent (=clear command).</summary>
    public bool IsClear => !HasPreviewRegion && existing.Count == 0;

    /// <summary>Whether a preview region is available to draw.</summary>
    public bool HasPreviewRegion => region != null && !string.IsNullOrEmpty(region.type);

    /// <summary>
    /// Builds a payload from IPC region (JObject or null) and existing (JArray or null)
    /// tokens.
    /// </summary>
    public static EffectRangePayload FromJson(JToken regionToken, JToken existingToken = null)
    {
        var payload = new EffectRangePayload();
        payload.region = EffectRangeRegionData.Parse(regionToken);
        var existingArr = existingToken as JArray;
        if (existingArr != null)
        {
            foreach (var item in existingArr)
            {
                var r = EffectRangeRegionData.Parse(item);
                if (r != null) payload.existing.Add(r);
            }
        }
        return payload;
    }
}
