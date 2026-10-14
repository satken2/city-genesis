using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 常駐外の近傍セルの先読み。カメラを振ったり少し動かしたりしたときに常駐へ入るセル(LodConfig が近い順に選ぶ)を、
/// 常駐セルの取得・公開が残っておらず共通予算が空いているときにだけ取得し、非表示の完成タイルとして LRU へ置く。
/// 常駐へ入ったセルは LRU から表示へ戻すだけになり、取得・帯の適用・SyncHeightmap・コライダー生成を待たない。
/// 先読みのタイルは表示・接地・GPU高さ窓・全景のマスクに使わない(LRU の保持タイルと同じ扱い)。
/// </summary>
public partial class TerrainManager
{
    /// <summary>先読みするセル(近い順)。ロードバッチごとに差し替える。</summary>
    private readonly List<Vector2Int> prefetchCells = new List<Vector2Int>();

    /// <summary>先読みで取得した正規化済みの高さ。組み立てを終えたら捨て、常駐へ入ったセルのタイルジョブは取得の代わりに使う。</summary>
    private sealed class PrefetchedHeights
    {
        public float[,] heights;
        public int version;
        public LinkedListNode<Vector2Int> node;
    }

    private readonly Dictionary<Vector2Int, PrefetchedHeights> prefetchedHeights = new Dictionary<Vector2Int, PrefetchedHeights>();
    /// <summary>prefetchedHeights の追加順。先頭が最古で、容量超過時はここから捨てる。</summary>
    private readonly LinkedList<Vector2Int> prefetchedHeightsOrder = new LinkedList<Vector2Int>();
    /// <summary>組み立て待ちの高さの上限(1枚約1MB)。取得の直後に組み立てるので多くは要らない。</summary>
    private const int PREFETCH_HEIGHTS_CAPACITY = 8;
    /// <summary>取得に失敗したセル。同じ先読み集合の間は選び直さず、失敗を繰り返さない。</summary>
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

    /// <summary>常駐セルの取得・公開が残っておらず、直前のフレームで共通予算がほとんど使われていないか。</summary>
    private bool IsPrefetchIdle() =>
        tileSlotWaiters.Count == 0 && activeTileDownloads == 0 && pendingStreamRequests.Count == 0
        && RenderWorkScheduler.LastFrameMs <= PerformanceConfig.FrameBudgetMs * FRESH_BUDGET_RATIO;

    /// <summary>先読みの対象として残っているか。常駐へ入った・タイルジョブが立った・集合から外れたら組み立てを止める。</summary>
    private bool IsPrefetchTarget(Vector2Int cell, int epoch) =>
        epoch == clearEpoch && prefetchCells.Contains(cell) && !residentTerrainCells.Contains(cell)
        && !tileJobs.ContainsKey(cell) && !terrainTiles.ContainsKey(cell);

    private int RequiredCellVersion(Vector2Int cell) =>
        cachedCellVersions != null && cachedCellVersions.TryGetValue(cell, out int version) ? version : 0;

    /// <summary>次に先読みするセル。必要版以上のタイルを LRU に持つセルと、都市外のセルは飛ばす。</summary>
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
    /// 先読みセルを近い順に1枚ずつ、高さの取得と非表示タイルの組み立てまで進める。各工程の前に常駐の仕事と予算の空きを待ち、
    /// 常駐の表示を遅らせない。組み立ての途中で常駐へ入ったセルは中断し、取得済みの高さは常駐のタイルジョブに譲る。
    /// </summary>
    private IEnumerator PrefetchWorker()
    {
        TerrainData staged = null;
        try
        {
            while (true)
            {
                yield return null;
                if (terrainSettings == null || cachedCellVersions == null || cellVersionsDirty || !IsPrefetchIdle()) continue;
                if (!TryPickPrefetchCell(out Vector2Int cell)) continue;
                int epoch = clearEpoch;
                if (!prefetchedHeights.TryGetValue(cell, out PrefetchedHeights entry) || entry.version < RequiredCellVersion(cell))
                {
                    yield return DownloadPrefetchHeights(cell, epoch);
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

    /// <summary>組み立て中のセルが先読みの対象のままで、使っている高さが差し替えられていないか。</summary>
    private bool IsPrefetchBuildValid(Vector2Int cell, int epoch, PrefetchedHeights entry) =>
        IsPrefetchTarget(cell, epoch) && prefetchedHeights.TryGetValue(cell, out PrefetchedHeights current) && current == entry;

    /// <summary>先読みセルの高さを取得・正規化して保持する。常駐のタイル取得と同じAPIを使い、取得枠は使わない。</summary>
    private IEnumerator DownloadPrefetchHeights(Vector2Int cell, int epoch)
    {
        Vector2Int backend = UnityCellToBackendCell(cell);
        string path = $"/terrain/surface?cell_x={backend.x}&cell_z={backend.y}&resolution={TILE_RESOLUTION}";
        byte[] bytes;
        int version;
        RenderWorkScheduler.CountEvent("terrain.prefetchDownload");
        using (ApiRequest call = ApiClient.Request("GET", path, timeoutSeconds: 30))
        {
            yield return call.Send(() => epoch != clearEpoch);
            if (call.Aborted) yield break;
            if (call.Web.result != UnityWebRequest.Result.Success)
            {
                prefetchFailedCells.Add(cell);
                yield break;
            }
            bytes = call.Web.downloadHandler.data;
            version = ParseIntHeader(call.Web, "X-Cell-Version", 0);
        }
        float span = terrainSize.y;
        float below = SeaFloorDepthSpan;
        float[,] heights = RentHeightBuffer();
        Task task = BackgroundWork.Run(() => NormalizeTileHeights(bytes, heights, span, below));
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

    /// <summary>必要版以上の先読みの高さがあれば取り出す。取り出した配列は呼び出し側の公開要求が所有し、公開後にプールへ返る。</summary>
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
