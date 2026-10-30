using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Prefetch nearby nonresident cells selected by LodConfig nearest-first that may become resident after turning or slight movement.
/// Generate only when resident-cell generation/publication is finished and shared budget is available; retain completed hidden tiles in LRU.
/// On residency entry, simply reshow LRU tiles without waiting for generation, band application, SyncHeightmap, or collider generation.
/// Prefetch tiles are excluded from display, grounding, GPU height windows, and overview masks, just like retained LRU tiles.
/// </summary>
public partial class TerrainManager
{
    /// <summary>Prefetch cells, nearest first. Replaced for each load batch.</summary>
    private readonly List<Vector2Int> prefetchCells = new List<Vector2Int>();

    /// <summary>Normalized prefetched heights. Discard after assembly; resident tile jobs use them instead of generating.</summary>
    private sealed class PrefetchedHeights
    {
        public float[,] heights;
        public int version;
        public LinkedListNode<Vector2Int> node;
    }

    private readonly Dictionary<Vector2Int, PrefetchedHeights> prefetchedHeights = new Dictionary<Vector2Int, PrefetchedHeights>();
    /// <summary>prefetchedHeights insertion order, oldest first; discard from here at capacity.</summary>
    private readonly LinkedList<Vector2Int> prefetchedHeightsOrder = new LinkedList<Vector2Int>();
    /// <summary>Maximum heights awaiting assembly (about 1MB each). Few needed because assembly follows generation immediately.</summary>
    private const int PREFETCH_HEIGHTS_CAPACITY = 8;
    /// <summary>Cells whose generation failed. Do not reselect within the same prefetch set, avoiding repeated failures.</summary>
    private readonly HashSet<Vector2Int> prefetchFailedCells = new HashSet<Vector2Int>();
    private Coroutine prefetchWorkerCoroutine;

    private void SetPrefetchCells(List<Vector2Int> cells)
    {
        prefetchCells.Clear();
        prefetchFailedCells.Clear();
        if (cells != null) prefetchCells.AddRange(cells);
        if (prefetchCells.Count > 0 && prefetchWorkerCoroutine == null && isActiveAndEnabled)
            prefetchWorkerCoroutine = RenderWorkScheduler.Start(this, PrefetchWorker(), "TerrainManager", () => false);
    }

    private void ClearPrefetch()
    {
        prefetchCells.Clear();
        prefetchFailedCells.Clear();
        while (prefetchedHeightsOrder.Count > 0) DropPrefetchedHeights(prefetchedHeightsOrder.First.Value);
    }

    /// <summary>Whether resident fetching/publication is finished and the previous frame barely used shared budget.</summary>
    private bool IsPrefetchIdle() =>
        tileSlotWaiters.Count == 0 && activeTileDownloads == 0 && pendingStreamRequests.Count == 0
        && RenderWorkScheduler.LastFrameMs <= PerformanceConfig.FrameBudgetMs * FRESH_BUDGET_RATIO;

    /// <summary>Whether still a prefetch target. Stop assembly on residency entry, tile job creation, or removal from the set.</summary>
    private bool IsPrefetchTarget(Vector2Int cell, int epoch) =>
        epoch == clearEpoch && prefetchCells.Contains(cell) && !residentTerrainCells.Contains(cell)
        && !tileJobs.ContainsKey(cell) && !terrainTiles.ContainsKey(cell);

    private int RequiredCellVersion(Vector2Int cell) =>
        cachedCellVersions != null && cachedCellVersions.TryGetValue(cell, out int version) ? version : 0;

    /// <summary>Next prefetch cell. Skip outside-city cells and cells with LRU tiles meeting required revisions.</summary>
    private bool TryPickPrefetchCell(out Vector2Int picked)
    {
        foreach (var cell in prefetchCells)
        {
            if (!IsCityCell(cell) || prefetchFailedCells.Contains(cell) || !IsPrefetchTarget(cell, clearEpoch)) continue;
            if (tileCache.TryGetValue(cell, out CachedTile cached) && cached.terrain != null && cached.version >= RequiredCellVersion(cell)) continue;
            picked = cell;
            return true;
        }
        picked = default;
        return false;
    }

    /// <summary>
    /// Generate heights and assemble hidden tiles one at a time, nearest first. Wait before each stage for resident work and budget availability,
    /// avoiding resident display delays. Abort assembly if the cell becomes resident, yielding fetched heights to its resident tile job.
    /// </summary>
    private IEnumerator PrefetchWorker()
    {
        TerrainData staged = null;
        try
        {
            while (true)
            {
                yield return null;
                if (terrainSettings == null || surfaceSampler == null || cachedCellVersions == null || cellVersionsDirty || !IsPrefetchIdle()) continue;
                if (!TryPickPrefetchCell(out Vector2Int cell)) continue;
                int epoch = clearEpoch;
                if (!prefetchedHeights.TryGetValue(cell, out PrefetchedHeights entry) || entry.version < RequiredCellVersion(cell))
                {
                    yield return GeneratePrefetchHeights(cell, epoch);
                    continue;
                }

                staged = new TerrainData { heightmapResolution = TILE_RESOLUTION, alphamapResolution = 16, size = terrainSize };
                bool valid = true;
                for (int row = 0; row < TILE_RESOLUTION && valid; row += STREAM_BAND_ROWS)
                {
                    while ((valid = IsPrefetchBuildValid(cell, epoch, entry)) && !IsPrefetchIdle()) yield return null;
                    if (valid) ApplyHeightBand(staged, entry.heights, TILE_RESOLUTION, row, Mathf.Min(STREAM_BAND_ROWS, TILE_RESOLUTION - row));
                    yield return null;
                }
                while (valid && (valid = IsPrefetchBuildValid(cell, epoch, entry)) && !(IsPrefetchIdle() && HasFreshBudget())) yield return null;
                if (valid)
                {
                    staged.SyncHeightmap();
                    yield return null;
                }
                while (valid && (valid = IsPrefetchBuildValid(cell, epoch, entry)) && !(IsPrefetchIdle() && HasFreshBudget())) yield return null;
                if (valid)
                {
                    Terrain tile = CreateTerrainTile(cell, staged);
                    staged = null;
                    ParkTile(cell, tile, entry.version);
                    DropPrefetchedHeights(cell);
                    RenderWorkScheduler.CountEvent("terrain.prefetchBuilt");
                }
                if (staged != null)
                {
                    Destroy(staged);
                    staged = null;
                }
            }
        }
        finally
        {
            if (staged != null) Destroy(staged);
            prefetchWorkerCoroutine = null;
        }
    }

    /// <summary>Whether the cell being assembled remains a prefetch target and its heights have not been replaced.</summary>
    private bool IsPrefetchBuildValid(Vector2Int cell, int epoch, PrefetchedHeights entry) =>
        IsPrefetchTarget(cell, epoch) && prefetchedHeights.TryGetValue(cell, out PrefetchedHeights current) && current == entry;

    /// <summary>Generate, normalize, and retain prefetch heights without consuming generation slots.</summary>
    private IEnumerator GeneratePrefetchHeights(Vector2Int cell, int epoch)
    {
        TerrainSurfaceSampler sampler = surfaceSampler;
        Vector2Int backend = UnityCellToBackendCell(cell);
        int version = sampler.Version(cell);
        RenderWorkScheduler.CountEvent("terrain.prefetchGenerate");
        float span = terrainSize.y;
        float below = SeaFloorDepthSpan;
        float[,] heights = RentHeightBuffer();
        Task task = BackgroundWork.Run(() => GenerateTileHeights(sampler, backend, heights, span, below));
        yield return new WaitUntil(() => task.IsCompleted);
        if (task.IsFaulted || task.IsCanceled)
        {
            Debug.LogError($"先読みした地形セル{cell}の正規化失敗: {task.Exception?.Flatten()}");
            prefetchFailedCells.Add(cell);
            ReturnHeightBuffer(heights);
            yield break;
        }
        if (epoch != clearEpoch)
        {
            ReturnHeightBuffer(heights);
            yield break;
        }
        DropPrefetchedHeights(cell);
        prefetchedHeights[cell] = new PrefetchedHeights { heights = heights, version = version, node = prefetchedHeightsOrder.AddLast(cell) };
        while (prefetchedHeights.Count > PREFETCH_HEIGHTS_CAPACITY) DropPrefetchedHeights(prefetchedHeightsOrder.First.Value);
    }

    private void DropPrefetchedHeights(Vector2Int cell)
    {
        if (!prefetchedHeights.TryGetValue(cell, out PrefetchedHeights entry)) return;
        prefetchedHeights.Remove(cell);
        prefetchedHeightsOrder.Remove(entry.node);
        ReturnHeightBuffer(entry.heights);
    }

    /// <summary>Take prefetched heights meeting required revisions. The caller's publication request owns the array and returns it to the pool afterward.</summary>
    private bool TryTakePrefetchedHeights(Vector2Int cell, int requiredVersion, out float[,] heights, out int version)
    {
        heights = null;
        version = 0;
        if (!prefetchedHeights.TryGetValue(cell, out PrefetchedHeights entry)) return false;
        if (entry.version < requiredVersion)
        {
            DropPrefetchedHeights(cell);
            return false;
        }
        prefetchedHeights.Remove(cell);
        prefetchedHeightsOrder.Remove(entry.node);
        heights = entry.heights;
        version = entry.version;
        return true;
    }
}
