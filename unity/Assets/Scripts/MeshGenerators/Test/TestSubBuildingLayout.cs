using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// Randomly generate mashup (MODEL_MASHUP) sub-building plans
/// for mesh test scenes.
///
/// Gameplay backend (genesis-core:
/// crud_buildings.generate_sub_building_layout + geo_buildings.generate_mashup_cells)
/// creates plans on building creation; Unity only renders resolved BuildingSummary.sub_buildings.
/// Tests lack backend results, so port the algorithm here
/// to assemble sub_buildings locally. Keep this port aligned when backend placement changes
/// (differences affect only test-display fidelity).
///
/// Intentional backend differences:
/// - Tests lack frontage data; always use longest-edge fallback
///   (same deterministic behavior as backend parcels without frontage).
/// - Test cell containment via four-corner containment instead of shapely within
///   (test footprints are convex, so equivalent).
/// - Omit wings lacking model_prefix (backend excludes these during delivery, so they never render).
/// </summary>
public static class TestSubBuildingLayout
{
    // Same target cell-fill ratio as backend crud_buildings.MASHUP_FILL_RATIO.
    // Intentionally leave about 20% vacant to avoid overfilling.
    private const float FILL_RATIO = 0.8f;

    /// <summary>
    /// Parcel cell-division result, corresponding to backend geo_buildings.generate_mashup_cells output.
    /// Local XZ origin at parcel center (same as test footprints).
    /// </summary>
    private class MashupGrid
    {
        public HashSet<(int i, int j)> Cells = new HashSet<(int i, int j)>();
        public Vector2 Origin;      // Grid origin (minimum bounding-box corner in grid axes).
        public Vector2 U;           // Column direction unit vector (front-edge direction).
        public Vector2 V;           // Row direction unit vector (U rotated 90° counterclockwise).
        public float Angle;         // U direction angle (degrees, CCW from +X in XZ).
        public Vector2 FrontA;      // Start of front edge used for orientation.
        public Vector2 FrontB;      // Its endpoint.
    }

    /// <summary>
    /// Generate sub-building placement plans.
    ///
    /// Deterministically divide parcel into cells; place in priority order:
    /// Always place the first (minimum-ID) representative wing near the front (longest edge), then
    /// satisfy each wing's min_count, then
    /// add wings maximizing weight/(placed count+1) until FILL_RATIO is reached,
    /// using D'Hondt allocation (exclude max_count-exceeded/nonfitting candidates).
    /// Wing composition is deterministic; only positions randomize. Second/later wings of one type
    /// prefer adjacency to existing same-type wings (clustering), then positions sharing the most boundaries
    /// with site edges/existing wings.
    /// Derive wing floors by clamping parent floors to definition min_floors–max_floors
    /// (backend _attach_sub_buildings applies the same derivation at delivery).
    ///
    /// Returns:
    ///     Null for variants without subbuilding_master definitions/uninitialized masters (=ordinary single building).
    ///     Empty list for parcels too small for cells or representative wings.
    /// </summary>
    /// <param name="variant">Building variant.</param>
    /// <param name="parcelPolygon">Polygon treated as parcel (center-origin local XZ).</param>
    /// <param name="parentFloors">Parent floor count (source for per-wing floors).</param>
    /// <param name="openPit">Open pit (MODEL_OPEN_PIT), placing only in forecourt cells.</param>
    public static List<SubBuildingPlacement> Generate(int variant, Vector2[] parcelPolygon, int parentFloors, bool openPit = false)
    {
        if (MasterDataStore.Instance == null || !MasterDataStore.Instance.IsInitialized)
        {
            return null;
        }
        if (parcelPolygon == null || parcelPolygon.Length < 3)
        {
            return null;
        }

        List<KeyValuePair<int, MasterDataStore.SubBuildingMasterItem>> subs =
            MasterDataStore.Instance.GetSubBuildingsForVariant(variant);
        subs.RemoveAll(kvp => string.IsNullOrEmpty(kvp.Value.model_prefix));
        if (subs.Count == 0)
        {
            return null;
        }

        MashupGrid grid = BuildMashupCells(parcelPolygon, Constants.MASHUP_CELL_SIZE);
        if (openPit)
        {
            KeepYardCells(grid, parcelPolygon, Constants.MASHUP_CELL_SIZE);
        }
        if (grid.Cells.Count == 0)
        {
            Debug.LogWarning("[MeshTest] 区画が小さすぎるため、サブ建物を配置できませんでした。");
            return new List<SubBuildingPlacement>();
        }

        // Fallback for height_per_floor resolution at delivery (sub-building first, then main master).
        float? agentFloorHeight = null;
        try
        {
            var agentMaster = MasterDataStore.Instance.GetAgentMaster(variant);
            agentFloorHeight = agentMaster?.height_per_floor;
        }
        catch (KeyNotFoundException) { }

        var state = new LayoutState
        {
            Grid = grid,
            AgentFloorHeight = agentFloorHeight,
            ParentFloors = Mathf.Max(1, parentFloors),
        };
        foreach (var kvp in subs)
        {
            state.Counts[kvp.Key] = 0;
            state.CellsBySub[kvp.Key] = new HashSet<(int i, int j)>();
        }

        // First minimum-ID wing represents the facility; always place it at the candidate nearest front edge.
        // If it cannot fit, abandon the whole plan (site decorations only).
        if (!TryPlace(subs[0], preferFront: true, state))
        {
            Debug.LogWarning($"[MeshTest] 先頭のサブ建物{subs[0].Key}が区画に収まりませんでした。");
            return new List<SubBuildingPlacement>();
        }

        // Satisfy minimum counts, including the already placed representative. Warn/skip nonfitting wings
        // without failing the whole plan (unlike representative-wing failure).
        foreach (var kvp in subs)
        {
            (int minCount, _) = CountBounds(kvp.Value);
            while (state.Counts[kvp.Key] < minCount)
            {
                if (!TryPlace(kvp, preferFront: false, state))
                {
                    Debug.LogWarning($"[MeshTest] サブ建物{kvp.Key}が区画に収まりませんでした" +
                        $"(最低配置数{minCount}のうち{state.Counts[kvp.Key]}棟で打ち切り)。");
                    break;
                }
            }
        }

        // Add wings preserving weight proportions until target fill ratio.
        // D'Hondt chooses maximum weight/(placed count+1); ties use
        // first subs entry (ascending ID). Occupied cells only increase,
        // so permanently exclude wings that cannot fit anywhere.
        var pool = subs.FindAll(kvp => Weight(kvp.Value) > 0f);
        int fillTarget = Mathf.CeilToInt(grid.Cells.Count * FILL_RATIO);
        while (state.Occupied.Count < fillTarget)
        {
            int bestIndex = -1;
            float bestQuotient = 0f;
            for (int i = 0; i < pool.Count; i++)
            {
                (_, int? maxCount) = CountBounds(pool[i].Value);
                if (maxCount.HasValue && state.Counts[pool[i].Key] >= maxCount.Value)
                {
                    continue;
                }
                float quotient = Weight(pool[i].Value) / (state.Counts[pool[i].Key] + 1);
                if (quotient > bestQuotient)
                {
                    bestQuotient = quotient;
                    bestIndex = i;
                }
            }
            if (bestIndex < 0)
            {
                break;
            }
            if (!TryPlace(pool[bestIndex], preferFront: false, state))
            {
                pool.RemoveAt(bestIndex);
            }
        }

        return state.Placements;
    }

    /// <summary>
    /// Placement state, corresponding to backend generate_sub_building_layout locals.
    /// </summary>
    private class LayoutState
    {
        public MashupGrid Grid;
        public float? AgentFloorHeight;
        public int ParentFloors;    // Parent floors (source for per-wing floors).
        public HashSet<(int i, int j)> Occupied = new HashSet<(int i, int j)>();
        public List<SubBuildingPlacement> Placements = new List<SubBuildingPlacement>();
        public Dictionary<int, int> Counts = new Dictionary<int, int>();                                  // Wing type→placed count.
        public Dictionary<int, HashSet<(int i, int j)>> CellsBySub = new Dictionary<int, HashSet<(int i, int j)>>();  // Wing type→occupied cells (clustering).
        public Dictionary<int, bool> FirstRotated = new Dictionary<int, bool>();                          // Wing type→first placement orientation (alignment).
    }

    /// <summary>Placement weight (wing-count ratio). Default 1.0; negative treated as zero.</summary>
    private static float Weight(MasterDataStore.SubBuildingMasterItem sub)
    {
        return Mathf.Max(0f, sub.weight ?? 1f);
    }

    /// <summary>
    /// Return (minimum, maximum) wing counts.
    /// Undefined min_count/max_count give zero/unlimited (null). For conflicting max &lt; min,
    /// raise maximum to honor minimum.
    /// </summary>
    private static (int minCount, int? maxCount) CountBounds(MasterDataStore.SubBuildingMasterItem sub)
    {
        int minCount = sub.min_count ?? 0;
        int? maxCount = sub.max_count;
        if (maxCount.HasValue)
        {
            maxCount = Mathf.Max(maxCount.Value, minCount);
        }
        return (minCount, maxCount);
    }

    /// <summary>
    /// Place one wing. False if no location fits.
    /// preferFront=true selects nearest front-edge candidate (ties deterministic by grid coordinates);
    /// false prefers same-type adjacency (clustering), randomly choosing among candidates sharing most boundaries
    /// with site edges/existing wings.
    /// Try grid-aligned and 90° orientations.
    /// </summary>
    private static bool TryPlace(
        KeyValuePair<int, MasterDataStore.SubBuildingMasterItem> sub, bool preferFront, LayoutState state)
    {
        MashupGrid grid = state.Grid;
        (int cellW, int cellH) = CellSpan(sub.Value);

        var candidates = new List<((int i, int j) anchor, int w, int h, bool rotated)>();
        foreach ((bool rotated, int w, int h) in new[] { (false, cellW, cellH), (true, cellH, cellW) })
        {
            foreach ((int i, int j) anchor in FindBlockAnchors(grid.Cells, state.Occupied, w, h))
            {
                candidates.Add((anchor, w, h, rotated));
            }
        }
        if (candidates.Count == 0)
        {
            return false;
        }

        ((int i, int j) anchor, int w, int h, bool rotated) chosen;
        if (preferFront)
        {
            chosen = candidates[0];
            (float d, int j, int i, int r) bestKey = FrontKey(chosen, grid);
            for (int c = 1; c < candidates.Count; c++)
            {
                (float d, int j, int i, int r) key = FrontKey(candidates[c], grid);
                if (key.CompareTo(bestKey) < 0)
                {
                    bestKey = key;
                    chosen = candidates[c];
                }
            }
        }
        else
        {
            // Choose maximum boundary-sharing candidates, keeping vacant space contiguous.
            var narrowed = ClusterCandidates(sub.Key, candidates, state);
            int bestContact = -1;
            foreach (var candidate in narrowed)
            {
                bestContact = Mathf.Max(bestContact, Contact(candidate, state));
            }
            narrowed = narrowed.FindAll(c => Contact(c, state) == bestContact);
            chosen = narrowed[Random.Range(0, narrowed.Count)];
        }

        for (int a = 0; a < chosen.w; a++)
        {
            for (int b = 0; b < chosen.h; b++)
            {
                state.Occupied.Add((chosen.anchor.i + a, chosen.anchor.j + b));
                state.CellsBySub[sub.Key].Add((chosen.anchor.i + a, chosen.anchor.j + b));
            }
        }
        state.Counts[sub.Key]++;
        if (!state.FirstRotated.ContainsKey(sub.Key))
        {
            state.FirstRotated[sub.Key] = chosen.rotated;
        }

        Vector2 blockCenter = BlockCenter(grid, chosen.anchor.i, chosen.anchor.j, chosen.w, chosen.h);
        // Wing floors = parent floors clamped to wing definition range (same as delivery derivation).
        int minFloors = Mathf.Max(1, sub.Value.min_floors ?? 1);
        int maxFloors = Mathf.Max(minFloors, sub.Value.max_floors ?? minFloors);
        state.Placements.Add(new SubBuildingPlacement
        {
            subbuilding_id = sub.Key,
            model_prefix = sub.Value.model_prefix,
            // Parcel polygons are center-origin local coordinates, so block center directly gives relative position.
            position = new[] { blockCenter.x, blockCenter.y },
            rotation = grid.Angle + (chosen.rotated ? 90f : 0f),
            floors = Mathf.Clamp(state.ParentFloors, minFloors, maxFloors),
            height_per_floor = sub.Value.height_per_floor ?? state.AgentFloorHeight,
        });
        return true;
    }

    /// <summary>
    /// For second/later same-type wings, prefer candidates adjacent to existing same-type wings.
    /// Retain four-neighbor cell contacts; if available, prefer orientation matching the first
    /// placement (90° rotation flag). If none qualify, do not filter,
    /// allowing placement in isolated vacancies.
    /// </summary>
    private static List<((int i, int j) anchor, int w, int h, bool rotated)> ClusterCandidates(
        int subbuildingId, List<((int i, int j) anchor, int w, int h, bool rotated)> candidates,
        LayoutState state)
    {
        HashSet<(int i, int j)> anchorCells = state.CellsBySub[subbuildingId];
        if (anchorCells.Count == 0)
        {
            return candidates;
        }

        var adjacent = new List<((int i, int j) anchor, int w, int h, bool rotated)>();
        foreach (var candidate in candidates)
        {
            bool touches = false;
            for (int a = 0; a < candidate.w && !touches; a++)
            {
                for (int b = 0; b < candidate.h && !touches; b++)
                {
                    int ci = candidate.anchor.i + a;
                    int cj = candidate.anchor.j + b;
                    touches = anchorCells.Contains((ci + 1, cj)) || anchorCells.Contains((ci - 1, cj))
                        || anchorCells.Contains((ci, cj + 1)) || anchorCells.Contains((ci, cj - 1));
                }
            }
            if (touches)
            {
                adjacent.Add(candidate);
            }
        }
        if (adjacent.Count == 0)
        {
            return candidates;
        }

        var aligned = adjacent.FindAll(c => c.rotated == state.FirstRotated[subbuildingId]);
        return aligned.Count > 0 ? aligned : adjacent;
    }

    /// <summary>Block-perimeter cell edges touching site edges (absent cells) or existing wings.</summary>
    private static int Contact(((int i, int j) anchor, int w, int h, bool rotated) candidate, LayoutState state)
    {
        int count = 0;
        void Check(int ci, int cj)
        {
            if (!state.Grid.Cells.Contains((ci, cj)) || state.Occupied.Contains((ci, cj)))
            {
                count++;
            }
        }
        for (int a = 0; a < candidate.w; a++)
        {
            Check(candidate.anchor.i + a, candidate.anchor.j - 1);
            Check(candidate.anchor.i + a, candidate.anchor.j + candidate.h);
        }
        for (int b = 0; b < candidate.h; b++)
        {
            Check(candidate.anchor.i - 1, candidate.anchor.j + b);
            Check(candidate.anchor.i + candidate.w, candidate.anchor.j + b);
        }
        return count;
    }

    /// <summary>Representative-wing front-placement key, compared ascending (front-edge distance,j,i,rotation) for deterministic choice.</summary>
    private static (float d, int j, int i, int r) FrontKey(
        ((int i, int j) anchor, int w, int h, bool rotated) candidate, MashupGrid grid)
    {
        Vector2 center = BlockCenter(grid, candidate.anchor.i, candidate.anchor.j, candidate.w, candidate.h);
        float distance = PolylineUtils.DistanceToSegment(center, grid.FrontA, grid.FrontB);
        return (distance, candidate.anchor.j, candidate.anchor.i, candidate.rotated ? 1 : 0);
    }

    /// <summary>
    /// Return occupied cell count (front-edge direction, depth) for a sub-building footprint.
    /// Master width/height are cells (one=MASHUP_CELL_SIZE m); default one, round fractions up.
    /// </summary>
    private static (int cellW, int cellH) CellSpan(MasterDataStore.SubBuildingMasterItem sub)
    {
        float width = (sub.width.HasValue && sub.width.Value > 0f) ? sub.width.Value : 1f;
        float height = (sub.height.HasValue && sub.height.Value > 0f) ? sub.height.Value : 1f;
        int cellW = Mathf.Max(1, Mathf.CeilToInt(width - 1e-6f));
        int cellH = Mathf.Max(1, Mathf.CeilToInt(height - 1e-6f));
        return (cellW, cellH);
    }

    /// <summary>List minimum corners (i,j) where a cellW×cellH block fits vacant cells.</summary>
    private static List<(int i, int j)> FindBlockAnchors(
        HashSet<(int i, int j)> cells, HashSet<(int i, int j)> occupied, int cellW, int cellH)
    {
        var anchors = new List<(int i, int j)>();
        foreach ((int i, int j) in cells)
        {
            bool blockOk = true;
            for (int a = 0; a < cellW && blockOk; a++)
            {
                for (int b = 0; b < cellH && blockOk; b++)
                {
                    (int, int) cell = (i + a, j + b);
                    if (!cells.Contains(cell) || occupied.Contains(cell))
                    {
                        blockOk = false;
                    }
                }
            }
            if (blockOk)
            {
                anchors.Add((i, j));
            }
        }
        return anchors;
    }

    /// <summary>Local center of cellW×cellH block with minimum corner cell (i,j).</summary>
    private static Vector2 BlockCenter(MashupGrid grid, int i, int j, int cellW, int cellH)
    {
        float su = (i + cellW * 0.5f) * Constants.MASHUP_CELL_SIZE;
        float sv = (j + cellH * 0.5f) * Constants.MASHUP_CELL_SIZE;
        return grid.Origin + grid.U * su + grid.V * sv;
    }

    /// <summary>
    /// Retain cells entirely within the open-pit forecourt (OPEN_PIT_YARD_DEPTH from front edge).
    /// Determine inward direction by testing whether a point slightly along V from front-edge midpoint lies inside parcel.
    /// </summary>
    private static void KeepYardCells(MashupGrid grid, Vector2[] polygon, float cellSize)
    {
        Vector2 middle = (grid.FrontA + grid.FrontB) * 0.5f;
        Vector2 inward = PolygonUtils.Contains(polygon, middle + grid.V * 0.05f) ? grid.V : -grid.V;
        float baseDepth = Vector2.Dot(grid.Origin - grid.FrontA, inward);
        float step = Vector2.Dot(grid.V, inward);
        grid.Cells.RemoveWhere(c => baseDepth + Mathf.Max(c.j * cellSize * step, (c.j + 1) * cellSize * step)
            > Constants.OPEN_PIT_YARD_DEPTH + 1e-6f);
    }

    /// <summary>
    /// Deterministically divide polygons into cell-size squares.
    /// Align grid to longest edge, origin at minimum bounding-box corner in grid axes.
    /// Keep only fully contained cells; leave edge remainders as gaps.
    /// </summary>
    private static MashupGrid BuildMashupCells(Vector2[] polygon, float cellSize)
    {
        var grid = new MashupGrid();
        int n = polygon.Length;

        // Front edge=longest edge (same deterministic fallback as backend without frontage data).
        int bestIdx = 0;
        float bestSq = -1f;
        for (int i = 0; i < n; i++)
        {
            float sq = (polygon[(i + 1) % n] - polygon[i]).sqrMagnitude;
            if (sq > bestSq)
            {
                bestSq = sq;
                bestIdx = i;
            }
        }
        grid.FrontA = polygon[bestIdx];
        grid.FrontB = polygon[(bestIdx + 1) % n];

        Vector2 edge = grid.FrontB - grid.FrontA;
        grid.U = edge.sqrMagnitude > 1e-12f ? edge.normalized : Vector2.right;
        grid.V = new Vector2(-grid.U.y, grid.U.x);
        grid.Angle = Mathf.Atan2(grid.U.y, grid.U.x) * Mathf.Rad2Deg;

        // Project all vertices onto grid axes U/V; origin at minimum bounding-box corner.
        float minU = float.PositiveInfinity, minV = float.PositiveInfinity;
        float maxU = float.NegativeInfinity, maxV = float.NegativeInfinity;
        foreach (Vector2 p in polygon)
        {
            float su = Vector2.Dot(p, grid.U);
            float sv = Vector2.Dot(p, grid.V);
            minU = Mathf.Min(minU, su);
            maxU = Mathf.Max(maxU, su);
            minV = Mathf.Min(minV, sv);
            maxV = Mathf.Max(maxV, sv);
        }
        grid.Origin = grid.U * minU + grid.V * minV;

        int cols = Mathf.Max(0, Mathf.CeilToInt((maxU - minU) / cellSize - 1e-9f));
        int rows = Mathf.Max(0, Mathf.CeilToInt((maxV - minV) / cellSize - 1e-9f));

        // Test slightly shrunken squares so floating-point errors do not reject boundary cells.
        // Convex-only test footprints make four-corner containment equivalent to full-cell containment.
        const float inset = 0.01f;
        for (int i = 0; i < cols; i++)
        {
            for (int j = 0; j < rows; j++)
            {
                float a = i * cellSize + inset;
                float b = (i + 1) * cellSize - inset;
                float c = j * cellSize + inset;
                float d = (j + 1) * cellSize - inset;
                bool allInside =
                    PolygonUtils.Contains(polygon, grid.Origin + grid.U * a + grid.V * c) &&
                    PolygonUtils.Contains(polygon, grid.Origin + grid.U * b + grid.V * c) &&
                    PolygonUtils.Contains(polygon, grid.Origin + grid.U * b + grid.V * d) &&
                    PolygonUtils.Contains(polygon, grid.Origin + grid.U * a + grid.V * d);
                if (allInside)
                {
                    grid.Cells.Add((i, j));
                }
            }
        }
        return grid;
    }
}
