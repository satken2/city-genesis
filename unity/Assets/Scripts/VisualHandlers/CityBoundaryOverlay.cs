using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Near the city boundary (detailed-terrain rectangle), draw a thick translucent dashed line following ground and sea level.
/// Closer views also show a light-blue grid wall rising from the boundary.
/// Background terrain outside the city cannot be built on except in scenario mode, even if visually continuous, so indicate the playable area.
///
/// Keep line width constant in screen pixels. Per chunk, round the dash period from camera distance to a power of two in meters,
/// drawing from cumulative boundary distances that are multiples of that period. Because periods change only by factors of two,
/// dashes stay nested and fixed in world space across chunk joins and zoom changes rather than sliding with camera movement.
/// </summary>
public sealed class CityBoundaryOverlay : MonoBehaviour
{
    private const float SAMPLE_SPACING = 10f;      // Boundary-height sampling interval (m), matching the detailed-terrain grid spacing.
    private const int CHUNK_SAMPLES = 32;          // Samples per unit used for frustum culling and dash-period selection.
    private const float LIFT = 0.3f;               // Height above ground/sea surface (m).
    private const float LINE_PIXELS = 7f;          // Line width (px).
    private const float DASH_PERIOD_PIXELS = 40f;  // Target screen-space dash period, line plus gap (px).
    private const float DASH_RATIO = 0.6f;         // Drawn fraction of one period.
    private const float MIN_DASH_PERIOD = 2f;      // Minimum close-up dash period (m).
    private const float MIN_SEGMENT_PIXELS = 6f;   // Do not follow terrain variations smaller than this on screen (px).
    private const float CULL_MARGIN = 20f;         // Padding added to chunk AABBs (m).
    private const float FULL_VISIBILITY_DISTANCE = 600f;
    private const float HIDDEN_DISTANCE = 1000f;
    private const float WALL_HEIGHT = 1000f;
    private const float WALL_GRID_SPACING = 20f;
    private const float WALL_LINE_PIXELS = 1.5f;
    private const float WALL_FULL_VISIBILITY_DISTANCE = 100f;
    private const float WALL_HIDDEN_DISTANCE = 300f;
    private const float CAMERA_POS_THRESHOLD = 0.5f;
    private const float CAMERA_ROT_THRESHOLD = 0.5f;
    private static readonly Color LINE_COLOR = new Color(1f, 1f, 1f, 0.55f);
    private static readonly Color WALL_COLOR = new Color(0.1f, 0.85f, 1f, 0.7f);

    private struct Chunk
    {
        public int start;
        public int end;
        public Bounds bounds;
        public Bounds wallBounds;
    }

    private TerrainManager terrain;
    private OverlayMeshRenderer overlay;
    private OverlayMeshRenderer wallOverlay;
    private readonly List<Vector3> wallTriangles = new List<Vector3>();
    // Closed boundary polyline with the first point repeated at the end. arc is cumulative XZ-plane distance.
    private readonly List<Vector3> points = new List<Vector3>();
    private readonly List<float> arc = new List<float>();
    private readonly List<Vector2Int> sampleCells = new List<Vector2Int>();
    private readonly List<Chunk> chunks = new List<Chunk>();
    // Rectangle-corner sample indices, five including the closing point. Always retain corners when decimating.
    private readonly int[] cornerIndices = new int[5];
    private readonly HashSet<Vector2Int> tiledEdgeCells = new HashSet<Vector2Int>();
    private readonly HashSet<Vector2Int> changedCells = new HashSet<Vector2Int>();
    private readonly List<Vector2Int> droppedCells = new List<Vector2Int>();
    private readonly Plane[] frustumPlanes = new Plane[6];
    private float halfWidth, halfHeight;
    // Initial boundary heights may be provisional before overview data arrives, so resample every point on first load completion.
    private bool fullRefreshPending;
    private bool meshDirty;
    private bool meshCleared = true;
    private Vector3 lastCamPos;
    private Quaternion lastCamRot;

    private void Start()
    {
        terrain = GetComponent<TerrainManager>();
        overlay = OverlayMeshRenderer.Create("CityBoundaryOverlay", alwaysOnTop: false, parent: transform);
        // Fade by distance from each line position to the camera, keeping opacity smooth across chunk joins and small movements.
        overlay.Material.EnableKeyword("OVERLAY_DISTANCE_FADE");
        overlay.Material.SetVector("_DistanceFadeRange", new Vector4(FULL_VISIBILITY_DISTANCE, HIDDEN_DISTANCE, 0f, 0f));
        wallOverlay = OverlayMeshRenderer.Create("CityBoundaryWall", alwaysOnTop: false, parent: transform);
        wallOverlay.Material.EnableKeyword("OVERLAY_DISTANCE_FADE");
        wallOverlay.Material.EnableKeyword("OVERLAY_WORLD_GRID");
        wallOverlay.Material.SetVector("_DistanceFadeRange", new Vector4(WALL_FULL_VISIBILITY_DISTANCE, WALL_HIDDEN_DISTANCE, 0f, 0f));
        wallOverlay.Material.SetVector("_WorldGrid", new Vector4(WALL_GRID_SPACING, WALL_LINE_PIXELS, 0f, 0f));
        if (terrain != null) terrain.OnAllTerrainLodLoaded += OnTerrainLoaded;
    }

    private void OnDestroy()
    {
        if (terrain != null) terrain.OnAllTerrainLodLoaded -= OnTerrainLoaded;
    }

    private void LateUpdate()
    {
        Camera cam = Camera.main;
        bool visible = overlay != null && wallOverlay != null && terrain != null && terrain.HasTerrainSettings && cam != null
            && GameStateManager.Instance != null && GameStateManager.Instance.IsWorldVisible;
        if (!visible)
        {
            // Terrain settings clear during session switches, so rebuild the boundary for the next city.
            if (terrain == null || !terrain.HasTerrainSettings) points.Clear();
            if (!meshCleared)
            {
                if (overlay != null) overlay.ClearMesh();
                if (wallOverlay != null) wallOverlay.ClearMesh();
            }
            meshCleared = true;
            return;
        }

        float w = terrain.GetMapWidth() * 0.5f, h = terrain.GetMapHeight() * 0.5f;
        if (points.Count == 0 || w != halfWidth || h != halfHeight) BuildBoundary(w, h);

        bool moved = Vector3.Distance(cam.transform.position, lastCamPos) > CAMERA_POS_THRESHOLD
            || Quaternion.Angle(cam.transform.rotation, lastCamRot) > CAMERA_ROT_THRESHOLD;
        if (!moved && !meshDirty && !meshCleared) return;
        lastCamPos = cam.transform.position;
        lastCamRot = cam.transform.rotation;
        meshDirty = false;
        meshCleared = false;
        RebuildMesh(cam);
    }

    private void BuildBoundary(float w, float h)
    {
        halfWidth = w;
        halfHeight = h;
        points.Clear();
        arc.Clear();
        sampleCells.Clear();
        var corners = new[] { new Vector2(-w, -h), new Vector2(w, -h), new Vector2(w, h), new Vector2(-w, h) };
        float length = 0f;
        for (int e = 0; e < 4; e++)
        {
            Vector2 a = corners[e], b = corners[(e + 1) % 4];
            float edge = Vector2.Distance(a, b);
            int n = Mathf.Max(1, Mathf.CeilToInt(edge / SAMPLE_SPACING));
            cornerIndices[e] = points.Count;
            for (int j = 0; j < n; j++) AddSample(Vector2.Lerp(a, b, (float)j / n), length + edge * j / n);
            length += edge;
        }
        cornerIndices[4] = points.Count;
        AddSample(corners[0], length);

        chunks.Clear();
        for (int start = 0; start < points.Count - 1; start += CHUNK_SAMPLES)
            chunks.Add(new Chunk { start = start, end = Mathf.Min(start + CHUNK_SAMPLES, points.Count - 1) });
        RefreshChunkBounds();

        tiledEdgeCells.Clear();
        foreach (Vector2Int cell in sampleCells)
            if (terrain.HasSurfaceTile(cell)) tiledEdgeCells.Add(cell);
        fullRefreshPending = true;
        meshDirty = true;
    }

    private void AddSample(Vector2 xz, float distance)
    {
        points.Add(new Vector3(xz.x, LineHeight(xz), xz.y));
        arc.Add(distance);
        // Boundary points also touch outside cells; assign ownership to the cell slightly inside the city.
        sampleCells.Add(TerrainManager.WorldToCell(
            Mathf.Clamp(xz.x, -halfWidth + 0.01f, halfWidth - 0.01f),
            Mathf.Clamp(xz.y, -halfHeight + 0.01f, halfHeight - 0.01f)));
    }

    /// <summary>Line height, using sea level (world Y=0) over water.</summary>
    private static float LineHeight(Vector2 xz) => Mathf.Max(Utils.GetGroundHeight(new Vector3(xz.x, 1000f, xz.y)), 0f) + LIFT;

    /// <summary>
    /// On terrain-tile arrival/removal, resample only boundary samples in cells whose heights changed.
    /// Cells losing tiles revert to overview heights, so resample them just like arriving cells.
    /// </summary>
    private void OnTerrainLoaded(IReadOnlyList<Vector2Int> updatedCells)
    {
        if (points.Count == 0) return;
        changedCells.Clear();
        if (updatedCells != null)
            foreach (Vector2Int cell in updatedCells) changedCells.Add(cell);
        droppedCells.Clear();
        foreach (Vector2Int cell in tiledEdgeCells)
            if (!terrain.HasSurfaceTile(cell)) droppedCells.Add(cell);
        foreach (Vector2Int cell in droppedCells)
        {
            tiledEdgeCells.Remove(cell);
            changedCells.Add(cell);
        }
        if (changedCells.Count == 0 && !fullRefreshPending) return;

        bool refreshed = false;
        for (int i = 0; i < points.Count; i++)
        {
            Vector2Int cell = sampleCells[i];
            if (!fullRefreshPending && !TouchesChangedCell(cell)) continue;
            Vector3 p = points[i];
            p.y = LineHeight(new Vector2(p.x, p.z));
            points[i] = p;
            if (terrain.HasSurfaceTile(cell)) tiledEdgeCells.Add(cell);
            refreshed = true;
        }
        fullRefreshPending = false;
        if (!refreshed) return;
        RefreshChunkBounds();
        meshDirty = true;
    }

    /// <summary>Cell-boundary samples can obtain heights from adjacent tiles, so also detect changes within one neighboring cell.</summary>
    private bool TouchesChangedCell(Vector2Int cell)
    {
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                if (changedCells.Contains(new Vector2Int(cell.x + dx, cell.y + dz))) return true;
        return false;
    }

    private void RefreshChunkBounds()
    {
        for (int c = 0; c < chunks.Count; c++)
        {
            Chunk chunk = chunks[c];
            Vector3 min = points[chunk.start], max = min;
            for (int i = chunk.start; i <= chunk.end; i++)
            {
                min = Vector3.Min(min, points[i]);
                max = Vector3.Max(max, points[i]);
            }
            chunk.bounds = new Bounds();
            chunk.bounds.SetMinMax(min, max);
            chunk.bounds.Expand(CULL_MARGIN * 2f);
            // Cull the wall independently of ground dashes so its upper portion can remain visible.
            chunk.wallBounds = new Bounds();
            chunk.wallBounds.SetMinMax(min, max + Vector3.up * WALL_HEIGHT);
            chunk.wallBounds.Expand(CULL_MARGIN * 2f);
            chunks[c] = chunk;
        }
    }

    private void RebuildMesh(Camera cam)
    {
        overlay.BeginFrame();
        GeometryUtility.CalculateFrustumPlanes(cam, frustumPlanes);
        Vector3 camPos = cam.transform.position;
        Plane nearPlane = new Plane(cam.transform.forward, camPos + cam.transform.forward * (cam.nearClipPlane + 0.001f));
        float worldPerPixelAtUnit = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / (Screen.height * 0.5f);

        foreach (Chunk chunk in chunks)
        {
            if (!GeometryUtility.TestPlanesAABB(frustumPlanes, chunk.bounds)) continue;
            float distance = Mathf.Max(Vector3.Distance(camPos, chunk.bounds.ClosestPoint(camPos)), cam.nearClipPlane);
            if (distance >= HIDDEN_DISTANCE) continue;
            float worldPerPixel = cam.orthographic ? cam.orthographicSize * 2f / Screen.height : distance * worldPerPixelAtUnit;
            float period = Mathf.Pow(2f, Mathf.Round(Mathf.Log(Mathf.Max(DASH_PERIOD_PIXELS * worldPerPixel, MIN_DASH_PERIOD), 2f)));
            int stride = Mathf.Max(1, Mathf.FloorToInt(MIN_SEGMENT_PIXELS * worldPerPixel / SAMPLE_SPACING));
            float s0 = arc[chunk.start], s1 = arc[chunk.end];
            for (float k = Mathf.Floor(s0 / period) * period; k < s1; k += period)
            {
                float a = Mathf.Max(k, s0), b = Mathf.Min(k + period * DASH_RATIO, s1);
                if (b > a) AddDash(a, b, stride, nearPlane);
            }
        }
        overlay.EndFrame(cam);

        wallOverlay.BeginFrame();
        wallTriangles.Clear();
        foreach (Chunk chunk in chunks)
        {
            if (!GeometryUtility.TestPlanesAABB(frustumPlanes, chunk.wallBounds)) continue;
            if ((camPos - chunk.wallBounds.ClosestPoint(camPos)).sqrMagnitude >= WALL_HIDDEN_DISTANCE * WALL_HIDDEN_DISTANCE) continue;
            AddWallChunk(chunk);
        }
        wallOverlay.AddPreTriangulated(wallTriangles, WALL_COLOR);
        wallOverlay.EndFrame(cam);
    }

    private void AddWallChunk(Chunk chunk)
    {
        for (int i = chunk.start; i < chunk.end; i++)
        {
            Vector3 a = points[i], b = points[i + 1];
            Vector3 topA = a + Vector3.up * WALL_HEIGHT, topB = b + Vector3.up * WALL_HEIGHT;
            wallTriangles.Add(a); wallTriangles.Add(topA); wallTriangles.Add(topB);
            wallTriangles.Add(a); wallTriangles.Add(topB); wallTriangles.Add(b);
        }
    }

    /// <summary>Draw cumulative-distance interval a to b as a polyline taking every stride-th sample, always retaining corners.</summary>
    private void AddDash(float a, float b, int stride, Plane nearPlane)
    {
        int i = IndexAt(a);
        Vector3 previous = PointAt(i, a);
        int next = NextBreak(i, stride);
        while (next < points.Count && arc[next] < b)
        {
            AddSegment(previous, points[next], nearPlane);
            previous = points[next];
            next = NextBreak(next, stride);
        }
        AddSegment(previous, PointAt(IndexAt(b), b), nearPlane);
    }

    private int NextBreak(int index, int stride)
    {
        int next = (index / stride + 1) * stride;
        foreach (int corner in cornerIndices)
            if (corner > index && corner < next) return corner;
        return next;
    }

    /// <summary>Largest i satisfying arc[i] ≤ s, capped at the start of the final segment.</summary>
    private int IndexAt(float s)
    {
        int index = arc.BinarySearch(s);
        if (index < 0) index = ~index - 1;
        return Mathf.Clamp(index, 0, points.Count - 2);
    }

    private Vector3 PointAt(int index, float s)
    {
        float span = arc[index + 1] - arc[index];
        return Vector3.Lerp(points[index], points[index + 1], span > 0f ? (s - arc[index]) / span : 0f);
    }

    /// <summary>Clip segments extending behind the camera at the near plane, preventing billboard reversal across the screen.</summary>
    private void AddSegment(Vector3 a, Vector3 b, Plane nearPlane)
    {
        float da = nearPlane.GetDistanceToPoint(a), db = nearPlane.GetDistanceToPoint(b);
        if (da < 0f && db < 0f) return;
        if (da < 0f || db < 0f)
        {
            Vector3 p = a + (b - a) * (da / (da - db));
            if (da < 0f) a = p; else b = p;
        }
        overlay.AddSolidLine(a, b, LINE_COLOR, LINE_PIXELS);
    }
}
