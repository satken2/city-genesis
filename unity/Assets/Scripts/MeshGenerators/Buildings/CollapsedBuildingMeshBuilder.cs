// Assets/Scripts/MeshGenerators/Buildings/CollapsedBuildingMeshBuilder.cs
using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// Debris field of a collapsed building (STATUS_COLLAPSED). BuildingMeshBuilder dispatches here by status before any
/// model_type handling: Normal and Close draw the heap (Close adds broken columns), Far draws one plate (GenerateFar).
///
/// One rule shapes every collapse: the building's debris (DEBRIS_VOLUME_RATIO of footprint area × height) settles at the
/// angle of repose. Solving that volume for a flat-topped heap over the footprint whose slopes run outward gives the heap
/// height and how far it spreads past the footprint, so taller buildings leave higher, wider debris that covers neighboring
/// parcels and roads. The spread wanders around the building (SpreadScale), so the debris edge does not trace the footprint's
/// shape. Rubble and sand piles stand on a rubble bed at BED_SHARE of the heap: cones on a jittered grid spaced
/// by PILE_SPACING of the spread, whose peaks follow the heap and whose sides stand at each material's angle of repose.
/// Each material is one heightfield; where the two intersect, the higher shows.
///
/// Fire damage (status_reason == REASON_FIRE) uses the charred materials. The collider extrudes the footprint to the heap
/// height, so debris over roads and neighbors does not take their clicks.
/// </summary>
public class CollapsedBuildingMeshBuilder : IBuildingMeshGenerator
{
    private const float DEBRIS_VOLUME_RATIO = 0.33f; // Debris volume per building volume (USACE debris estimate: L × W × H × 0.33).
    private const float RUBBLE_REPOSE_DEG = 38f;     // Angle of repose of broken concrete and masonry.
    private const float SAND_REPOSE_DEG = 33f;       // Angle of repose of sand and crushed fines.
    private const float SAND_PILE_SHARE = 0.3f;      // Share of piles made of sand and fines.
    private const float SAND_PEAK = 0.8f;            // Sand pile peaks relative to rubble piles.
    private const float BED_SHARE = 0.4f;            // Rubble-bed height relative to the heap, keeping ground hidden between piles.
    private const float PILE_SPACING = 0.7f;         // Pile spacing relative to the spread.
    private const float PEAK_MIN = 0.6f;             // Pile peak relative to the heap height at the pile center (lower bound).
    private const float PEAK_MAX = 1.2f;             // Pile peak relative to the heap height at the pile center (upper bound).
    private const float PILE_JITTER = 0.35f;         // Pile-center offset from its grid cell center, relative to pile spacing.
    private const int MAX_PILES_PER_SIDE = 24;       // Wide, low sites space piles farther apart instead of multiplying them.
    private const int CELLS_PER_PILE = 10;           // Heightfield cells per pile spacing.
    private const int MAX_GRID_CELLS = 32;           // Heightfield cells per side, bounding generation time and vertices per building.
    private const float OUTLINE_VARIATION = 0.5f;    // Share by which the spread varies around the building (SpreadScale).
    private const float OUTLINE_WAVELENGTH = 2f;     // Size of the outline's lobes relative to the spread.
    private const int FAR_OUTLINE_POINTS = 16;       // Outline points of the far plate.
    private const float MIN_CELL = 0.5f;             // Smallest heightfield cell (m).
    private const float RUBBLE_ROUGHNESS = 0.35f;    // Vertex height jitter relative to cell size (blocky rubble).
    private const float SAND_ROUGHNESS = 0.15f;      // Vertex height jitter relative to cell size (smoother sand).
    private const float GROUND_SINK = 0.3f;          // Depth (m) below ground of vertices off the debris, so its edge cuts the terrain.
    private const float COLUMN_SPACING = 6f;         // Structural grid of the standing columns (m), a typical span of framed buildings.
    private const int MAX_COLUMN_GRID = 12;          // Wide sites space the grid farther apart instead of multiplying columns.
    private const float COLUMN_SHARE = 0.6f;         // Share of grid points whose column still stands.
    private const float COLUMN_WIDTH = 0.6f;         // Column section (m).
    private const float COLUMN_TILT_DEG = 12f;       // Largest lean of a standing column.
    private const float COLUMN_BREAK = 0.5f;         // Largest drop (m) of the broken top below the column's highest point.
    private const float REBAR_SIZE = 0.04f;          // Rebar section (m).
    private const float REBAR_INSET = 0.08f;         // Rebar distance from the column faces (m).
    private const float FAR_PLATE_LIFT = 0.3f;       // Far plate height above the ground (m), over road surfaces.

    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false)
    {
        if (!TryResolveSite(buildingData, out Vector2[] source, out Vector3 centerPointWorld)) return EmptyResult(buildingData);

        float floorHeight = BuildingMeshUtils.GetFloorHeight(buildingData);
        float buildingHeight = Mathf.Max(1, buildingData.floors ?? 1) * floorHeight;
        bool brokenColumns = BuildingMeshUtils.CurrentDrawLod == DrawLod.Close;
        Mesh mesh = BuildDebrisMesh(source, buildingHeight, floorHeight, buildingData.agent_id, centerPointWorld, isDynamic,
            brokenColumns, out float heapHeight);

        bool burned = buildingData.status_reason == Constants.REASON_FIRE;
        Material[] materials =
        {
            burned ? BuildingMeshBuilder.GetBurnedRubbleMaterial() : BuildingMeshBuilder.GetCollapsedRubbleMaterial(),
            burned ? BuildingMeshBuilder.GetBurnedSandMaterial() : BuildingMeshBuilder.GetCollapsedSandMaterial(),
            BuildingMeshBuilder.GetCollapsedRebarMaterial(),
        };
        Mesh colliderMesh = BuildingMeshBuilder.GenerateColliderMesh(source, Mathf.Max(heapHeight, 0.1f));
        return BuildingMeshBuilder.MergeSubMeshes(new BuildingMeshResult(mesh, materials, colliderMesh));
    }

    /// <summary>
    /// Far LOD: one plate in the rubble material, enough to show where the debris lies and its color. Far objects have no
    /// collider, so none is built.
    /// </summary>
    public static BuildingMeshResult GenerateFar(BuildingSummary buildingData)
    {
        if (!TryResolveSite(buildingData, out Vector2[] source, out Vector3 centerPointWorld)) return EmptyResult(buildingData);

        float buildingHeight = Mathf.Max(1, buildingData.floors ?? 1) * BuildingMeshUtils.GetFloorHeight(buildingData);
        Mesh mesh = BuildFarPlate(source, buildingHeight, buildingData.agent_id, centerPointWorld, true);
        Material rubble = buildingData.status_reason == Constants.REASON_FIRE
            ? BuildingMeshBuilder.GetBurnedRubbleMaterial()
            : BuildingMeshBuilder.GetCollapsedRubbleMaterial();
        return BuildingMeshBuilder.MergeSubMeshes(new BuildingMeshResult(mesh, new[] { rubble }));
    }

    /// <summary>
    /// The building's footprint (the parcel when the footprint is invalid) and its placement reference. Uses the same height
    /// reference as construction and completed buildings, so state transitions do not shift the object vertically.
    /// </summary>
    private static bool TryResolveSite(BuildingSummary buildingData, out Vector2[] source, out Vector3 centerPointWorld)
    {
        source = null;
        centerPointWorld = Vector3.zero;
        if (buildingData == null)
        {
            Debug.LogError("CollapsedBuildingMeshBuilder: buildingData が null です。");
            return false;
        }
        BuildingMeshUtils.InitializeCache();
        Vector2[] footprint = (buildingData.polygonArray != null && buildingData.polygonArray.Length >= 3)
            ? BuildingMeshUtils.GetFootprint(buildingData)
            : null;
        Vector2[] parcelFootprint = BuildingMeshUtils.ResolveParcelContext(buildingData, out _, out centerPointWorld);
        source = footprint ?? parcelFootprint;
        if (source != null) return true;
        Debug.LogWarning($"CollapsedBuildingMeshBuilder: polygon も parcel も不正なため空メッシュを返します (agent_id={buildingData.agent_id})。");
        return false;
    }

    private static BuildingMeshResult EmptyResult(BuildingSummary buildingData) =>
        buildingData == null ? null : new BuildingMeshResult(new Mesh(), null);

    /// <summary>
    /// Far plate: a fan from the footprint's bounding-box center out to FAR_OUTLINE_POINTS outline points, each where the ray
    /// last leaves the footprint plus the local spread (the same SpreadScale as the heap), lifted FAR_PLATE_LIFT above the
    /// ground at each vertex (local Y=0 without followTerrain).
    /// </summary>
    internal static Mesh BuildFarPlate(Vector2[] footprint, float buildingHeight, int seed, Vector3 centerPointWorld, bool followTerrain)
    {
        float rubbleSlope = Mathf.Tan(RUBBLE_REPOSE_DEG * Mathf.Deg2Rad);
        float spread = HeapHeight(footprint, buildingHeight, rubbleSlope) / rubbleSlope;
        Vector2 min = footprint[0], max = footprint[0];
        foreach (Vector2 p in footprint)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }
        Vector2 center = (min + max) * 0.5f;

        var outline = new Vector2[FAR_OUTLINE_POINTS + 1];
        outline[0] = center;
        for (int k = 0; k < FAR_OUTLINE_POINTS; k++)
        {
            float angle = 2f * Mathf.PI * k / FAR_OUTLINE_POINTS;
            Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            float exit = 0f;
            for (int i = 0, j = footprint.Length - 1; i < footprint.Length; j = i++)
            {
                Vector2 edge = footprint[i] - footprint[j];
                float denominator = direction.x * edge.y - direction.y * edge.x;
                if (Mathf.Abs(denominator) < 1e-6f) continue;
                Vector2 offset = footprint[j] - center;
                float t = (offset.x * edge.y - offset.y * edge.x) / denominator;
                float u = (offset.x * direction.y - offset.y * direction.x) / denominator;
                if (t > exit && u >= 0f && u <= 1f) exit = t;
            }
            Vector2 toe = center + direction * (exit + spread);
            outline[k + 1] = center + direction * (exit + spread * SpreadScale(seed, toe, spread));
        }

        var vertices = new Vector3[outline.Length];
        var triangles = new int[FAR_OUTLINE_POINTS * 3];
        for (int i = 0; i < outline.Length; i++)
        {
            Vector2 p = outline[i];
            float ground = followTerrain
                ? Utils.GetGroundHeight(new Vector3(p.x + centerPointWorld.x, 1000f, p.y + centerPointWorld.z)) - centerPointWorld.y
                : 0f;
            vertices[i] = new Vector3(p.x, ground + FAR_PLATE_LIFT, p.y);
        }
        for (int k = 0; k < FAR_OUTLINE_POINTS; k++)
        {
            triangles[k * 3] = 0;
            triangles[k * 3 + 1] = (k + 1) % FAR_OUTLINE_POINTS + 1;
            triangles[k * 3 + 2] = k + 1;
        }
        var mesh = new Mesh { vertices = vertices, uv = outline, triangles = triangles };
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Debris mesh around a footprint (local XZ, relative to centerPointWorld). Submesh 0 is rubble, 1 is sand and the
    /// concrete of broken columns, 2 is their rebar. Broken columns are close-up detail (brokenColumns).
    /// The seed (agent_id) fixes the pile and column layout, so regeneration and LOD changes redraw the same debris.
    /// With followTerrain, vertices sit on the ground at their own XZ; otherwise on local Y=0.
    /// UVs are in meters; the materials' tiling sets the texture period.
    /// </summary>
    internal static Mesh BuildDebrisMesh(Vector2[] footprint, float buildingHeight, float floorHeight, int seed,
        Vector3 centerPointWorld, bool followTerrain, bool brokenColumns, out float heapHeight)
    {
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        Vector2 min = footprint[0], max = footprint[0];
        foreach (Vector2 p in footprint)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }

        float rubbleSlope = Mathf.Tan(RUBBLE_REPOSE_DEG * Mathf.Deg2Rad);
        float sandSlope = Mathf.Tan(SAND_REPOSE_DEG * Mathf.Deg2Rad);
        heapHeight = HeapHeight(footprint, buildingHeight, rubbleSlope);
        if (heapHeight < 0.01f) return mesh;
        float spread = heapHeight / rubbleSlope;

        // Pile grid over the footprint and its widest spread. Each vertex checks only the piles whose cones can reach it.
        float maxSpread = spread * (1f + OUTLINE_VARIATION);
        Vector2 pileOrigin = min - Vector2.one * maxSpread;
        Vector2 pileArea = max - min + Vector2.one * (2f * maxSpread);
        float spacing = Mathf.Max(PILE_SPACING * spread, Mathf.Max(pileArea.x, pileArea.y) / MAX_PILES_PER_SIDE);
        int pilesX = Mathf.Max(1, Mathf.CeilToInt(pileArea.x / spacing));
        int pilesZ = Mathf.Max(1, Mathf.CeilToInt(pileArea.y / spacing));
        var pileCenters = new Vector2[pilesX, pilesZ];
        var pilePeaks = new float[pilesX, pilesZ];
        var pileIsSand = new bool[pilesX, pilesZ];
        for (int px = 0; px < pilesX; px++)
        {
            for (int pz = 0; pz < pilesZ; pz++)
            {
                Vector2 jitter = new Vector2(Hash01(seed, px, pz, 0) - 0.5f, Hash01(seed, px, pz, 1) - 0.5f) * (2f * PILE_JITTER);
                Vector2 center = pileOrigin + (new Vector2(px + 0.5f, pz + 0.5f) + jitter) * spacing;
                pileCenters[px, pz] = center;
                pileIsSand[px, pz] = Hash01(seed, px, pz, 3) < SAND_PILE_SHARE;
                pilePeaks[px, pz] = HeapHeightAt(footprint, center, heapHeight, rubbleSlope, spread, seed)
                    * Mathf.Lerp(PEAK_MIN, PEAK_MAX, Hash01(seed, px, pz, 2)) * (pileIsSand[px, pz] ? SAND_PEAK : 1f);
            }
        }
        float reach = PEAK_MAX * heapHeight * Mathf.Max(1f / rubbleSlope, SAND_PEAK / sandSlope);
        int searchRadius = Mathf.FloorToInt(reach / spacing + 0.5f + PILE_JITTER);

        // Heightfield grid fitted to the bed and every cone, with one cell to spare so the outer ring lies off the debris.
        Vector2 low = min - Vector2.one * maxSpread, high = max + Vector2.one * maxSpread;
        for (int px = 0; px < pilesX; px++)
        {
            for (int pz = 0; pz < pilesZ; pz++)
            {
                if (pilePeaks[px, pz] <= 0f) continue;
                float radius = pilePeaks[px, pz] / (pileIsSand[px, pz] ? sandSlope : rubbleSlope);
                low = Vector2.Min(low, pileCenters[px, pz] - Vector2.one * radius);
                high = Vector2.Max(high, pileCenters[px, pz] + Vector2.one * radius);
            }
        }
        Vector2 extent = high - low;
        float cell = Mathf.Max(MIN_CELL, spacing / CELLS_PER_PILE, Mathf.Max(extent.x, extent.y) / MAX_GRID_CELLS);
        Vector2 gridOrigin = low - Vector2.one * cell;
        int cellsX = Mathf.CeilToInt(extent.x / cell) + 2;
        int cellsZ = Mathf.CeilToInt(extent.y / cell) + 2;
        int pointsX = cellsX + 1, pointsZ = cellsZ + 1;
        var rubbleHeights = new float[pointsX * pointsZ];
        var sandHeights = new float[pointsX * pointsZ];
        for (int gz = 0; gz < pointsZ; gz++)
        {
            for (int gx = 0; gx < pointsX; gx++)
            {
                Vector2 p = gridOrigin + new Vector2(gx, gz) * cell;
                float rubble = BED_SHARE * HeapHeightAt(footprint, p, heapHeight, rubbleSlope, spread, seed);
                float sand = float.MinValue;
                int cx = Mathf.FloorToInt((p.x - pileOrigin.x) / spacing);
                int cz = Mathf.FloorToInt((p.y - pileOrigin.y) / spacing);
                for (int px = Mathf.Max(0, cx - searchRadius); px <= Mathf.Min(pilesX - 1, cx + searchRadius); px++)
                {
                    for (int pz = Mathf.Max(0, cz - searchRadius); pz <= Mathf.Min(pilesZ - 1, cz + searchRadius); pz++)
                    {
                        float peak = pilePeaks[px, pz];
                        if (peak <= 0f) continue;
                        float distance = Vector2.Distance(p, pileCenters[px, pz]);
                        if (pileIsSand[px, pz]) sand = Mathf.Max(sand, peak - sandSlope * distance);
                        else rubble = Mathf.Max(rubble, peak - rubbleSlope * distance);
                    }
                }
                int index = gz * pointsX + gx;
                rubbleHeights[index] = rubble > 0f ? rubble + (Hash01(seed, gx, gz, 4) - 0.5f) * RUBBLE_ROUGHNESS * cell : rubble;
                sandHeights[index] = sand > 0f ? sand + (Hash01(seed, gx, gz, 5) - 0.5f) * SAND_ROUGHNESS * cell : sand;
            }
        }

        var grounds = new float[pointsX * pointsZ];
        for (int i = 0; i < grounds.Length; i++) grounds[i] = float.NaN;
        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        List<int> rubbleTriangles = AppendLayer(rubbleHeights, grounds, gridOrigin, cell, pointsX, pointsZ,
            centerPointWorld, followTerrain, vertices, uvs);
        List<int> sandTriangles = AppendLayer(sandHeights, grounds, gridOrigin, cell, pointsX, pointsZ,
            centerPointWorld, followTerrain, vertices, uvs);
        var rebarTriangles = new List<int>();
        if (brokenColumns)
        {
            AppendBrokenColumns(footprint, buildingHeight, heapHeight, floorHeight, seed, centerPointWorld, followTerrain,
                vertices, uvs, sandTriangles, rebarTriangles);
        }

        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = 3;
        mesh.SetTriangles(rubbleTriangles, 0);
        mesh.SetTriangles(sandTriangles, 1);
        mesh.SetTriangles(rebarTriangles, 2);
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Columns left standing in the debris: on a structural grid aligned with the footprint's longest edge, COLUMN_SHARE of the
    /// grid points inside the footprint keep a leaning column broken off 0.3–1 floor above the heap (never above the building),
    /// with a jagged top and bent rebar.
    /// </summary>
    private static void AppendBrokenColumns(Vector2[] footprint, float buildingHeight, float heapHeight, float floorHeight,
        int seed, Vector3 centerPointWorld, bool followTerrain, List<Vector3> vertices, List<Vector2> uvs,
        List<int> concrete, List<int> rebar)
    {
        LongestEdgeFrame(footprint, out Vector2 along, out Vector2 across, out float minA, out float maxA, out float minB, out float maxB);
        float spacing = Mathf.Max(COLUMN_SPACING, Mathf.Max(maxA - minA, maxB - minB) / MAX_COLUMN_GRID);
        Quaternion frame = Quaternion.LookRotation(new Vector3(across.x, 0f, across.y), Vector3.up);

        for (int ia = 0; minA + (ia + 0.5f) * spacing < maxA; ia++)
        {
            for (int ib = 0; minB + (ib + 0.5f) * spacing < maxB; ib++)
            {
                Vector2 p = along * (minA + (ia + 0.5f) * spacing) + across * (minB + (ib + 0.5f) * spacing);
                if (Hash01(seed, ia, ib, 10) >= COLUMN_SHARE || !PolygonUtils.Contains(footprint, p)) continue;

                float ground = followTerrain
                    ? Utils.GetGroundHeight(new Vector3(p.x + centerPointWorld.x, 1000f, p.y + centerPointWorld.z)) - centerPointWorld.y
                    : 0f;
                float height = Mathf.Min(buildingHeight, heapHeight + Mathf.Lerp(0.3f, 1f, Hash01(seed, ia, ib, 11)) * floorHeight)
                    + GROUND_SINK;
                float leanAngle = Hash01(seed, ia, ib, 12) * 360f;
                Quaternion lean = Quaternion.AngleAxis(Hash01(seed, ia, ib, 13) * COLUMN_TILT_DEG,
                    Quaternion.Euler(0f, leanAngle, 0f) * Vector3.forward);
                Quaternion rotation = lean * frame;
                Vector3 basePoint = new Vector3(p.x, ground - GROUND_SINK, p.y);
                Vector3 Place(float x, float y, float z) => basePoint + rotation * new Vector3(x, y, z);

                // Sides rise to four broken corner heights; the top is a fan around a raised or sunken center.
                float w = COLUMN_WIDTH * 0.5f;
                var cx = new[] { -w, w, w, -w };
                var cz = new[] { -w, -w, w, w };
                var top = new float[4];
                for (int k = 0; k < 4; k++) top[k] = height - Hash01(seed, ia * 4 + k, ib, 14) * COLUMN_BREAK;
                for (int k = 0; k < 4; k++)
                {
                    int n = (k + 1) % 4;
                    int start = vertices.Count;
                    vertices.Add(Place(cx[n], 0f, cz[n]));
                    vertices.Add(Place(cx[n], top[n], cz[n]));
                    vertices.Add(Place(cx[k], top[k], cz[k]));
                    vertices.Add(Place(cx[k], 0f, cz[k]));
                    uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(0f, top[n]));
                    uvs.Add(new Vector2(COLUMN_WIDTH, top[k])); uvs.Add(new Vector2(COLUMN_WIDTH, 0f));
                    concrete.AddRange(new[] { start + 3, start + 2, start + 1, start + 3, start + 1, start });
                }
                float centerTop = height - Hash01(seed, ia, ib, 15) * COLUMN_BREAK;
                for (int k = 0; k < 4; k++)
                {
                    int n = (k + 1) % 4;
                    int start = vertices.Count;
                    vertices.Add(Place(0f, centerTop, 0f));
                    vertices.Add(Place(cx[n], top[n], cz[n]));
                    vertices.Add(Place(cx[k], top[k], cz[k]));
                    uvs.Add(Vector2.zero); uvs.Add(new Vector2(cx[n], cz[n])); uvs.Add(new Vector2(cx[k], cz[k]));
                    concrete.AddRange(new[] { start, start + 1, start + 2 });
                }

                // Rebar starts inside the column below the break and bends outward above it.
                float inner = w - REBAR_INSET;
                for (int k = 0; k < 4; k++)
                {
                    float sx = Mathf.Sign(cx[k]) * inner, sz = Mathf.Sign(cz[k]) * inner;
                    float rise = Mathf.Lerp(0.3f, 0.9f, Hash01(seed, ia * 4 + k, ib, 16));
                    float bend = Mathf.Lerp(20f, 70f, Hash01(seed, ia * 4 + k, ib, 17)) * Mathf.Deg2Rad;
                    float tail = Mathf.Lerp(0.2f, 0.6f, Hash01(seed, ia * 4 + k, ib, 18));
                    Vector3 outward = new Vector3(Mathf.Sign(cx[k]), 0f, Mathf.Sign(cz[k])).normalized;
                    Vector3 a = Place(sx, height - COLUMN_BREAK, sz);
                    Vector3 b = Place(sx, height + rise - COLUMN_BREAK * 0.5f, sz);
                    Vector3 c = b + rotation * (Vector3.up * Mathf.Cos(bend) + outward * Mathf.Sin(bend)) * tail;
                    AppendBar(a, b, vertices, uvs, rebar);
                    AppendBar(b, c, vertices, uvs, rebar);
                }
            }
        }
    }

    /// <summary>
    /// Frame along the footprint's longest edge (along) and its left normal (across, a proper rotation of XZ), with the
    /// footprint's extent in that frame.
    /// </summary>
    private static void LongestEdgeFrame(Vector2[] footprint, out Vector2 along, out Vector2 across,
        out float minA, out float maxA, out float minB, out float maxB)
    {
        along = Vector2.right;
        float longest = 0f;
        for (int i = 0, j = footprint.Length - 1; i < footprint.Length; j = i++)
        {
            Vector2 edge = footprint[i] - footprint[j];
            if (edge.magnitude > longest) { longest = edge.magnitude; along = edge / longest; }
        }
        across = new Vector2(-along.y, along.x);
        minA = float.MaxValue; maxA = float.MinValue; minB = float.MaxValue; maxB = float.MinValue;
        foreach (Vector2 p in footprint)
        {
            float a = Vector2.Dot(p, along), b = Vector2.Dot(p, across);
            minA = Mathf.Min(minA, a); maxA = Mathf.Max(maxA, a);
            minB = Mathf.Min(minB, b); maxB = Mathf.Max(maxB, b);
        }
    }

    /// <summary>Square bar of REBAR_SIZE from a to b (four sides, open ends that sit inside concrete or are too small to see).</summary>
    private static void AppendBar(Vector3 a, Vector3 b, List<Vector3> vertices, List<Vector2> uvs, List<int> triangles)
    {
        Vector3 axis = (b - a).normalized;
        Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized * (REBAR_SIZE * 0.5f);
        Vector3 v = Vector3.Cross(axis, u);
        var ring = new[] { u, v, -u, -v };
        float length = Vector3.Distance(a, b);
        for (int k = 0; k < 4; k++)
        {
            Vector3 o0 = ring[k], o1 = ring[(k + 1) % 4];
            int start = vertices.Count;
            vertices.Add(a + o0); vertices.Add(a + o1); vertices.Add(b + o1); vertices.Add(b + o0);
            uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(REBAR_SIZE, 0f));
            uvs.Add(new Vector2(REBAR_SIZE, length)); uvs.Add(new Vector2(0f, length));
            triangles.AddRange(new[] { start, start + 2, start + 3, start, start + 1, start + 2 });
        }
    }

    /// <summary>
    /// Heap height that holds the building's debris volume: a flat top over the footprint with sides at the angle of repose
    /// (slope = tan). The volume of that solid is A·h + P·h²/(2·slope) + π·h³/(3·slope²) (exact for convex footprints),
    /// which grows monotonically with h and stays below A·h, so bisection on [0, volume / A] converges.
    /// </summary>
    private static float HeapHeight(Vector2[] footprint, float buildingHeight, float slope)
    {
        float area = Mathf.Abs(PolygonUtils.SignedArea(footprint));
        float perimeter = 0f;
        for (int i = 0, j = footprint.Length - 1; i < footprint.Length; j = i++) perimeter += Vector2.Distance(footprint[j], footprint[i]);
        float volume = DEBRIS_VOLUME_RATIO * area * buildingHeight;
        if (volume <= 0f || area <= 0f) return 0f;
        float low = 0f, high = volume / area;
        for (int i = 0; i < 32; i++)
        {
            float h = (low + high) * 0.5f;
            float v = area * h + perimeter * h * h / (2f * slope) + Mathf.PI * h * h * h / (3f * slope * slope);
            if (v < volume) low = h;
            else high = h;
        }
        return (low + high) * 0.5f;
    }

    /// <summary>
    /// Heap height at a point: full height over the footprint, falling outside it over the local spread (SpreadScale).
    /// Negative past the toe, so the bed's edge vertices sink below the ground instead of lying flush with it.
    /// </summary>
    private static float HeapHeightAt(Vector2[] footprint, Vector2 point, float heapHeight, float slope, float spread, int seed)
    {
        if (PolygonUtils.Contains(footprint, point)) return heapHeight;
        return heapHeight - slope * PolygonUtils.DistanceToBoundary(footprint, point) / SpreadScale(seed, point, spread);
    }

    /// <summary>
    /// How far the debris reaches here relative to the mean spread: walls fall unevenly, so the reach wanders by
    /// ±OUTLINE_VARIATION around the building over lobes of OUTLINE_WAVELENGTH spreads instead of tracing the footprint.
    /// </summary>
    private static float SpreadScale(int seed, Vector2 point, float spread)
    {
        Vector2 q = point / (OUTLINE_WAVELENGTH * spread);
        int ix = Mathf.FloorToInt(q.x), iz = Mathf.FloorToInt(q.y);
        float fx = q.x - ix, fz = q.y - iz;
        fx = fx * fx * (3f - 2f * fx);
        fz = fz * fz * (3f - 2f * fz);
        float noise = Mathf.Lerp(
            Mathf.Lerp(Hash01(seed, ix, iz, 20), Hash01(seed, ix + 1, iz, 20), fx),
            Mathf.Lerp(Hash01(seed, ix, iz + 1, 20), Hash01(seed, ix + 1, iz + 1, 20), fx), fz);
        return 1f + OUTLINE_VARIATION * (2f * noise - 1f);
    }

    /// <summary>
    /// Emit the cells of one heightfield that rise above the ground and return their triangles (facing up).
    /// Vertices below ground sink to GROUND_SINK, so the debris edge is where its slope meets the terrain.
    /// Ground heights are sampled once per grid point and shared by both layers.
    /// </summary>
    private static List<int> AppendLayer(float[] heights, float[] grounds, Vector2 origin, float cell, int pointsX, int pointsZ,
        Vector3 centerPointWorld, bool followTerrain, List<Vector3> vertices, List<Vector2> uvs)
    {
        var triangles = new List<int>();
        var vertexIndex = new int[heights.Length];
        for (int i = 0; i < vertexIndex.Length; i++) vertexIndex[i] = -1;

        int Vertex(int gx, int gz)
        {
            int index = gz * pointsX + gx;
            if (vertexIndex[index] >= 0) return vertexIndex[index];
            Vector2 p = origin + new Vector2(gx, gz) * cell;
            if (float.IsNaN(grounds[index]))
            {
                grounds[index] = followTerrain
                    ? Utils.GetGroundHeight(new Vector3(p.x + centerPointWorld.x, 1000f, p.y + centerPointWorld.z)) - centerPointWorld.y
                    : 0f;
            }
            vertexIndex[index] = vertices.Count;
            vertices.Add(new Vector3(p.x, grounds[index] + Mathf.Max(heights[index], -GROUND_SINK), p.y));
            uvs.Add(p);
            return vertexIndex[index];
        }

        for (int gz = 0; gz < pointsZ - 1; gz++)
        {
            for (int gx = 0; gx < pointsX - 1; gx++)
            {
                int i00 = gz * pointsX + gx;
                if (heights[i00] <= 0f && heights[i00 + 1] <= 0f && heights[i00 + pointsX] <= 0f && heights[i00 + pointsX + 1] <= 0f) continue;
                int a = Vertex(gx, gz), b = Vertex(gx + 1, gz), c = Vertex(gx + 1, gz + 1), d = Vertex(gx, gz + 1);
                // Alternate the diagonal so the facets do not line up in one direction.
                bool diagonalAC = ((gx + gz) & 1) == 0;
                triangles.Add(a); triangles.Add(diagonalAC ? c : d); triangles.Add(b);
                triangles.Add(diagonalAC ? a : b); triangles.Add(d); triangles.Add(c);
            }
        }
        return triangles;
    }

    /// <summary>Deterministic value in [0, 1) for a seed, grid coordinates, and a stream number.</summary>
    private static float Hash01(int seed, int x, int z, int stream)
    {
        unchecked
        {
            uint h = (uint)seed * 0x9E3779B1u ^ (uint)x * 0x85EBCA77u ^ (uint)z * 0xC2B2AE3Du ^ (uint)stream * 0x27D4EB2Fu;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            h *= 0x297A2D39u;
            h ^= h >> 15;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }
}
