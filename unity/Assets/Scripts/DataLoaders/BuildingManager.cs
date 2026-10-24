using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using System.Linq;
using System.Globalization;
using System.Runtime.CompilerServices;
using GeneratedModels;

/// <summary>
/// Manager for building information.
/// Fetch BuildingSummary (single_agent schema plus coordinates) from the backend GET /buildings/ endpoint
/// and create building objects in Unity space.
/// </summary>
public class BuildingManager : BaseApiDataManager
{
    public static BuildingManager Instance { get; private set; }
    public int PendingDetailCount => deferredDetailBuildings.Count;

    private const string BUILDING_PREFIX = "Building_";
    private const string BUILDING_TAG = "BuildingObject";

    private Dictionary<int, GameObject> buildingObjectsCache = new Dictionary<int, GameObject>();
    private Dictionary<int, BuildingSummary> buildingDataCache = new Dictionary<int, BuildingSummary>();
    private Dictionary<Vector2Int, LodTier> cellBuildingLod = new Dictionary<Vector2Int, LodTier>();
    private Dictionary<Vector2Int, HashSet<int>> buildingIdsByCell = new Dictionary<Vector2Int, HashSet<int>>();
    private Dictionary<int, Vector2Int> buildingCellById = new Dictionary<int, Vector2Int>();

    /// <summary>
    /// Reverse index from infrastructure terminal node ID (agents.infra_node_id) to building agent_id.
    /// The node API exposes no owner facility, using a one-way agent-to-node reference, so
    /// the frontend identifies terminal nodes with this index built from buildings loaded within the bbox
    /// (used for infrastructure path snapping, terminal markers, and network compatibility checks).
    /// Always register entries in buildingDataCache through RegisterBuildingDataToCache,
    /// and synchronize removals in SyncBuildingsInCells / ClearAllCells.
    /// </summary>
    private Dictionary<int, int> buildingIdByInfraNodeId = new Dictionary<int, int>();

    /// <summary>
    /// Reverse index from depot terminal node ID (agents.depot_node_id) to building agent_id.
    /// Depot terminals without an electricity bit (LRT depots) cannot be identified as facility terminals from node type alone,
    /// so track endpoint checks (whether to add buffer stops) use this index. Follow the same rules as buildingIdByInfraNodeId.
    /// </summary>
    private Dictionary<int, int> buildingIdByDepotNodeId = new Dictionary<int, int>();

    /// <summary>
    /// Reverse index from parcel ID (agents.parcel_id) to the set of building agent_ids on that parcel.
    /// Used to find buildings on a parcel without scanning every building.
    /// Always register entries in buildingDataCache through RegisterBuildingDataToCache,
    /// and synchronize removals in SyncBuildingsInCells / ClearAllCells (same rules as buildingIdByInfraNodeId).
    /// </summary>
    private Dictionary<int, HashSet<int>> buildingIdsByParcel = new Dictionary<int, HashSet<int>>();

    /// <summary>
    /// Set of building agent_ids for ports (MODEL_PORT). Finds ports to rebuild when reclaimed land contents change without scanning every building.
    /// Follow the same rules as buildingIdByInfraNodeId (register through RegisterBuildingDataToCache; remove in SyncBuildingsInCells / ClearAllCells).
    /// </summary>
    private readonly HashSet<int> portBuildingIds = new HashSet<int>();

    /// <summary>
    /// Cache building versions fetched from the backend.
    /// Compare them against backend versions during incremental updates and recreate objects only when changed.
    /// Record received versions even when only outlines are shown; track unfinished detail generation in the deferred queue.
    /// </summary>
    private Dictionary<int, int> buildingVersionCache = new Dictionary<int, int>();
    private readonly HashSet<int> pendingExplosions = new HashSet<int>();
    private readonly Dictionary<int, int> explodedVersions = new Dictionary<int, int>();
    private int explosionGeneration;

    // ================================================================
    // View gate and count limit (LRU) for detail mesh generation
    // ================================================================

    /// <summary>
    /// Maximum number of building objects holding full detail meshes (non-Far) simultaneously.
    /// When exceeded, discard buildings that have been outside the view longest (LRU).
    /// Discarded buildings remain in the data cache and are regenerated through the deferred queue when viewed.
    /// Far (white boxes) is exempt because it is lightweight and needed to preserve the skyline.
    /// </summary>
    [SerializeField] private int maxDetailedBuildingObjects = 2000;

    /// <summary>
    /// Horizontal half-angle (degrees) for detail generation view checks. Positions within this angle of camera forward (plus nearby positions)
    /// are considered in view. Fetch batches finish restoration and outline display first; DetailGenerationWorker then handles
    /// restorable deferredDetailBuildings requests, followed by ungenerated requests in view.
    /// The view frustum weights processing order rather than filtering generation eligibility; buildings outside the view
    /// can also be filled in during idle time while capacity remains (reducing pop-in when turning).
    /// </summary>
    private const float BUILDING_VIEW_HALF_ANGLE_DEG = 75f;

    /// <summary>Distance (m) within which positions count as in view regardless of camera direction.</summary>
    private const float BUILDING_VIEW_NEAR_DISTANCE = 100f;

    /// <summary>Frame interval for the LRU visibility sweep.</summary>
    private const int VISIBILITY_SWEEP_INTERVAL_FRAMES = 15;

    /// <summary>Building request whose generation is deferred because it is outside the view.</summary>
    private struct DeferredDetailRequest
    {
        public BuildingSummary data;
        public bool isDynamic;
        /// <summary>Render LOD to generate (Normal / Close). Far is lightweight and generated immediately without deferral.</summary>
        public DrawLod drawLod;
    }

    // ================================================================
    // Render LOD (per-building detail, independent of the cell data tier)
    // ================================================================

    /// <summary>
    /// Buildings meeting the close-up condition around the camera, ascending by (distance, ID). Rebuilt on first use each frame (closeRankingFrame)
    /// so every evaluation in a frame shares it; only the DrawLodConfig.MAX_CLOSE_BUILDINGS nearest render Close.
    /// </summary>
    private readonly List<(float distance, int id)> closeRanking = new List<(float distance, int id)>();
    private int closeRankingFrame = -1;

    /// <summary>Bounding sphere radius per building ID (from the farthest footprint point and height). Used for projected ratio checks.</summary>
    private readonly Dictionary<int, float> buildingBoundsRadius = new Dictionary<int, float>();

    /// <summary>Building ID to deferred generation request. The latest request replaces earlier requests for the same ID.</summary>
    private readonly Dictionary<int, DeferredDetailRequest> deferredDetailBuildings = new Dictionary<int, DeferredDetailRequest>();

    /// <summary>Building IDs currently holding full detail mesh objects (non-Far). Population counted by the LRU limit.</summary>
    private readonly HashSet<int> detailedBuildingIds = new HashSet<int>();

    /// <summary>Last frame each building ID was observed in view. Used to prioritize LRU eviction.</summary>
    private readonly Dictionary<int, int> buildingLastVisibleFrame = new Dictionary<int, int>();

    /// <summary>Revision advanced whenever work enters the deferred queue. Used to detect idle deferred workers.</summary>
    private int deferredRevision;

    /// <summary>Revision advanced on each terrain tile publication. Signals reselecting requests awaiting terrain (without repeating restoration scans).</summary>
    private int detailTerrainRevision;

    /// <summary>Revision indicating whether candidates need reselection. Changes on queue insertion or terrain tile publication.</summary>
    private long DeferredSelectionRevision => ((long)deferredRevision << 32) | (uint)detailTerrainRevision;

    /// <summary>
    /// LRU eviction candidates ordered by oldest last-visible frame. To avoid scanning all entries per eviction,
    /// rebuild when exhausted or every VISIBILITY_SWEEP_INTERVAL_FRAMES, and recheck visibility when taking a candidate.
    /// </summary>
    private readonly List<(int lastSeen, int id)> evictionOrder = new List<(int lastSeen, int id)>();
    private int evictionCursor;
    private int evictionOrderFrame;


    /// <summary>
    /// Track coroutines started by LoadLodCells so new camera movement can cancel them.
    /// </summary>
    private List<Coroutine> activeLodCoroutines = new List<Coroutine>();

    // ================================================================
    // Incremental unloading of departing cells
    // ================================================================

    /// <summary>
    /// Departing cell to registration generation, awaiting detail parking and outline switching. CellUnloadWorker processes these within the frame budget.
    /// Remove on reentry and advance the generation on registration (to discard snapshots being scanned as stale).
    /// </summary>
    private readonly Dictionary<Vector2Int, int> pendingUnloadCells = new Dictionary<Vector2Int, int>();
    private int unloadGeneration;

    /// <summary>
    /// Cells that may hold visible building objects and are not currently unloading. Kept separately from loadedCells so
    /// the next batch reliably unloads buildings generated or reshown by a canceled batch before application.
    /// </summary>
    private readonly HashSet<Vector2Int> cellsWithShownObjects = new HashSet<Vector2Int>();

    /// <summary>Revision advanced whenever loadedCells changes. Used to detect idle deferred workers.</summary>
    private int loadedCellsRevision;

    /// <summary>Reusable list for CellUnloadWorker to snapshot IDs for one cell, avoiding HashSet enumeration across yields.</summary>
    private readonly List<int> unloadScanIds = new List<int>();

    // ================================================================
    // Incremental fetch ranges
    // ================================================================

    /// <summary>
    /// Loaded cells to fetch again in the next batch. Camera-triggered batches fetch only unloaded cells, cells recorded here, and cells with tier changes.
    /// MarkLoadedCellsStale records them on request; retain until successfully fetched and applied (canceled batches do not clear them).
    /// </summary>
    private readonly HashSet<Vector2Int> staleBuildingCells = new HashSet<Vector2Int>();

    /// <summary>Revision advanced on every MarkLoadedCellsStale call, identifying stale results from batches that span a refetch request.</summary>
    private int staleMarkRevision;

    // ================================================================
    // Rebuild decisions when terrain tiles arrive
    // ================================================================

    /// <summary>
    /// Building ID to terrain signature when the object was aligned to the current terrain (TerrainManager.GetTerrainSignature).
    /// OnTerrainTileReady also fires on LRU tile restoration, so adjust heights or rebuild only buildings whose signature changed.
    /// Buildings without a recorded signature always count as mismatches.
    /// </summary>
    private readonly Dictionary<int, long> buildingTerrainSignatures = new Dictionary<int, long>();
    private readonly HashSet<Vector2Int> previewCellsWithObjects = new HashSet<Vector2Int>();
    private int preparingRepresentations;

    [Header("計画中アイコン設定")]
    [Tooltip("計画中(status=3)の建物に表示する2Dアイコン")]
    public Sprite plannedBuildingIcon;
    [Tooltip("アイコンの大きさ")]
    public float plannedBuildingIconScale = 1f;
    [Tooltip("アイコンを建物の中心からどれだけ上にずらすか")]
    public float plannedBuildingIconYOffset = 2f;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        BuildingMeshUtils.InitializeCache();
        TerrainManager.OnTerrainTileReady += OnTerrainTileReadyHandler;
        ReclaimedLandManager.OnContentChanged += OnReclaimedLandsChanged;
        // Prepare the light overlay for public facility highlighting (persistent rendering that references building data).
        PublicFacilityGlowOverlay.Ensure();
        // Persistent worker processing detail meshes deferred because they were outside the view.
        RenderWorkScheduler.Start(this, DetailGenerationWorker(), "BuildingManager", () => HasPendingInitialDetails);
        // Persistent worker reevaluating per-building render LOD as the camera moves.
        RenderWorkScheduler.Start(this, DrawLodReevaluationWorker(), "BuildingManager", () => false);
        // Persistent worker moving buildings in departing cells into the parked cache within the frame budget.
        RenderWorkScheduler.Start(this, CellUnloadWorker(), "BuildingManager", () => false);
    }

    /// <summary>Previous camera state for the LRU visibility sweep. Check in Update, including rotation, because view depends on direction.</summary>
    private LodViewState sweepViewState;

    private void Update()
    {
        // LRU visibility sweep at fixed intervals to avoid unnecessary work every frame.
        // Update the last-visible frame for detailed buildings currently in view.
        // Skip while the camera is stationary. Buildings generated then receive their last-visible frame in MarkBuildingDetailed, preserving LRU order.
        if (Time.frameCount % VISIBILITY_SWEEP_INTERVAL_FRAMES != 0) return;
        if (detailedBuildingIds.Count == 0) return;

        Camera cam = Camera.main;
        if (cam == null) return;
        if (!sweepViewState.Update(cam)) return;
        Vector3 camPos = cam.transform.position;
        Vector3 fwd = GetHorizontalForward(cam);

        foreach (int id in detailedBuildingIds)
        {
            if (!buildingObjectsCache.TryGetValue(id, out GameObject obj) || obj == null) continue;
            // Visible buildings are always active (unloading moves them to the parked cache). Handle inactive objects defensively:
            // do not update their last-visible frame, which would keep them out of LRU eviction indefinitely.
            if (!obj.activeInHierarchy) continue;
            if (IsPositionInGenerationView(obj.transform.position, camPos, fwd))
            {
                buildingLastVisibleFrame[id] = Time.frameCount;
            }
        }
    }

    /// <summary>Normalized horizontal component of camera forward. Implemented by the shared Utils helper.</summary>
    private static Vector3 GetHorizontalForward(Camera cam)
    {
        return Utils.GetCameraHorizontalForward(cam);
    }

    /// <summary>
    /// Whether the position is in view for immediate detail mesh generation.
    /// Nearby positions (within BUILDING_VIEW_NEAR_DISTANCE) count as in view regardless of direction.
    /// </summary>
    private static bool IsPositionInGenerationView(Vector3 position, Vector3 camPos, Vector3 camForwardHorizontal)
    {
        return Utils.IsInHorizontalView(position, camPos, camForwardHorizontal, BUILDING_VIEW_NEAR_DISTANCE, BUILDING_VIEW_HALF_ANGLE_DEG);
    }

    private void OnDestroy()
    {
        BuildingRoadAccessIndex.Clear();
        lodCache.Clear();
        TerrainManager.OnTerrainTileReady -= OnTerrainTileReadyHandler;
        ReclaimedLandManager.OnContentChanged -= OnReclaimedLandsChanged;
    }

    /// <summary>
    /// Align buildings in a cell to new terrain after its tile is published. Buildings are placed at overview heights without waiting for tiles, so
    /// only adjust Far heights and rebuild details with changed terrain signatures through the deferred queue. Also resume details awaiting terrain.
    /// </summary>
    private void OnTerrainTileReadyHandler(Vector2Int cellIndex, Terrain terrain)
    {
        Utils.InvalidateHeightCache();
        // Have the deferred worker reselect detail requests previously skipped while awaiting terrain.
        if (deferredDetailBuildings.Count > 0) detailTerrainRevision++;

        if (!buildingIdsByCell.TryGetValue(cellIndex, out HashSet<int> buildingIds)) return;

        bool isDynamic = false;
        if (cellBuildingLod.TryGetValue(cellIndex, out LodTier tier))
        {
            isDynamic = tier != LodTier.UltraFar;
        }

        // Do not rebuild meshes inside the handler (synchronously rebuilding an entire cell causes spikes).
        // Align heights immediately and rebuild terrain-following geometry through the deferred queue, prioritizing view and respecting the budget.
        // The following operations do not modify buildingIdsByCell, so the set can be enumerated directly.
        foreach (int buildingId in buildingIds)
        {
            if (!buildingObjectsCache.TryGetValue(buildingId, out GameObject obj)) continue;
            if (obj == null) continue;
            if (!buildingDataCache.TryGetValue(buildingId, out BuildingSummary buildingData)) continue;

            // If the terrain data revision at completion matches, do not rebuild details solely because a tile returned.
            DrawLod objectLod = DrawLodInfo.GetDrawLod(obj, DrawLod.Normal);
            string contentKey = BuildingRenderKey(buildingData, objectLod);
            var identity = obj.GetComponent<RenderCacheIdentity>();
            if (identity != null && (identity.key == contentKey || identity.key == BuildingGenerationKey(buildingData, objectLod, contentKey)))
            {
                UpdateBuildingHeight(obj);
                RecordBuildingTerrainSignature(buildingData);
                continue;
            }

            // Do nothing if LRU restoration merely republishes the same tile revision.
            bool known = TryGetBuildingTerrainSignature(buildingData, out long signature);
            if (known && buildingTerrainSignatures.TryGetValue(buildingId, out long recorded) && recorded == signature) continue;

            UpdateBuildingHeight(obj);
            // Far boxes have no terrain-following geometry, so height adjustment suffices even in Dynamic cells.
            DrawLod currentDrawLod = DrawLodInfo.GetDrawLod(obj, DrawLod.Normal);
            if (isDynamic && currentDrawLod != DrawLod.Far)
                QueueDeferredDetail(buildingId, buildingData, true, currentDrawLod);

            if (known) buildingTerrainSignatures[buildingId] = signature;
        }
    }

    /// <summary>
    /// Area affecting building appearance (building bounding circle and site polygon bounding rectangle). Used for both the reuse key terrain revision and terrain signature.
    /// </summary>
    private Rect GetBuildingTerrainBounds(BuildingSummary data, ParcelSummary parcel)
    {
        float[] cp = data.centerPointArray;
        float radius = GetBuildingBoundsRadius(data);
        var bounds = new Rect(cp != null ? cp[0]-radius : 0, cp != null ? cp[1]-radius : 0, 2*radius, 2*radius);
        if (parcel == null) return bounds;
        ParcelKeyPart part = GetParcelKeyPart(parcel);
        if (part.hasBounds)
        {
            bounds.xMin = Mathf.Min(bounds.xMin, part.bounds.xMin); bounds.xMax = Mathf.Max(bounds.xMax, part.bounds.xMax);
            bounds.yMin = Mathf.Min(bounds.yMin, part.bounds.yMin); bounds.yMax = Mathf.Max(bounds.yMax, part.bounds.yMax);
        }
        return bounds;
    }

    /// <summary>False when TerrainManager is absent (including after destruction). Callers must treat this as unknown and must not skip rebuilding.</summary>
    private bool TryGetBuildingTerrainSignature(BuildingSummary data, out long signature)
    {
        TerrainManager terrain = Utils.CachedTerrainManager;
        if (terrain == null)
        {
            signature = 0;
            return false;
        }
        signature = terrain.GetTerrainSignature(GetBuildingTerrainBounds(data, GetParcelManager()?.GetParcelSummary(data.parcel_id)));
        return true;
    }

    /// <summary>Record the current terrain signature when generating, restoring, or adjusting an object's height to the current terrain.</summary>
    private void RecordBuildingTerrainSignature(BuildingSummary data)
    {
        if (TryGetBuildingTerrainSignature(data, out long signature)) buildingTerrainSignatures[data.agent_id] = signature;
        else buildingTerrainSignatures.Remove(data.agent_id);
    }

    public override void ClearAllCells()
    {
        // Reject late application of old fetch batches; otherwise the cell becomes loaded and incremental fetching never refetches it.
        StopActiveLodCoroutines();
        explosionGeneration++;
        pendingExplosions.Clear();
        explodedVersions.Clear();
        VisualEffectManager.Instance?.ClearTransientEffects();
        Debug.Log("BuildingManager: 全建物データをクリア");
        foreach (var obj in buildingObjectsCache.Values)
        {
            if (obj != null) Destroy(obj);
        }

        lodCache.Clear();
        buildingObjectsCache.Clear();
        buildingDataCache.Clear();
        BuildingRoadAccessIndex.Clear();
        ConstructionAmbience.Clear(ConstructionAmbience.Kind.Building);
        buildingIdByInfraNodeId.Clear();
        buildingIdByDepotNodeId.Clear();
        buildingIdsByParcel.Clear();
        portBuildingIds.Clear();
        buildingIdsByCell.Clear();
        buildingCellById.Clear();
        loadedCells.Clear();
        cellBuildingLod.Clear();
        buildingVersionCache.Clear();
        deferredDetailBuildings.Clear();
        detailedBuildingIds.Clear();
        buildingLastVisibleFrame.Clear();
        closeRankingFrame = -1;
        buildingBoundsRadius.Clear();
        pendingUnloadCells.Clear();
        previewCellsWithObjects.Clear();
        cellsWithShownObjects.Clear();
        staleBuildingCells.Clear();
        staleMarkRevision++;
        buildingTerrainSignatures.Clear();
        buildingGeometryRevisions.Clear();
        evictionOrder.Clear();
        evictionCursor = 0;
        loadedCellsRevision++;
        DrawLodStats.ResetCounts(DrawLodStats.Category.Building);
    }

    private void StopActiveLodCoroutines()
    {
        foreach (var c in activeLodCoroutines)
        {
            if (c != null) RenderWorkScheduler.Stop(this, c);
        }
        activeLodCoroutines.Clear();
    }

    /// <summary>
    /// Mark every loaded cell as needing refetch. Called synchronously when data refetching (CellDataLoader.ReloadAllData) is requested.
    /// The marker survives batch cancellation and is cleared when that cell is successfully fetched and applied.
    /// </summary>
    public void MarkLoadedCellsStale()
    {
        staleBuildingCells.UnionWith(loadedCells);
        staleMarkRevision++;
    }

    // ================================================================
    // LOD-aware cell loading (merged BBOX)
    // ================================================================

    public override void LoadLodCells(LodCellSets cellSets, bool isForce)
    {
        LoadLodCells(cellSets, null, isForce);
    }

    public override void LoadLodCells(LodCellSets cellSets, HashSet<Vector2Int> tierChangedCells, bool isForce)
    {
        // Cancel all older running coroutines (handling repeated calls on camera movement)
        StopActiveLodCoroutines();

        var allBuildingCells = new HashSet<Vector2Int>(cellSets.farCells);
        // Cells leaving residency are fetched as unloaded on reentry, so no marker is needed.
        staleBuildingCells.IntersectWith(allBuildingCells);

        // Stop hiding cells that return to residency during unloading. Some visible objects may not yet be parked, so
        // record them again as cells with shown objects, allowing the next batch to collect them even if this batch is canceled.
        if (pendingUnloadCells.Count > 0)
        {
            foreach (var cell in cellSets.farCells)
                if (pendingUnloadCells.Remove(cell)) cellsWithShownObjects.Add(cell);
        }

        RemoveDeferredRequestsOutside(allBuildingCells);
        var nearSet = new HashSet<Vector2Int>(cellSets.nearCells);
        var dynamicSet = new HashSet<Vector2Int>(cellSets.dynamicCells);

        // Include both loaded cells and cells shown by canceled batches before application (absent from loadedCells).
        var toUnload = new List<Vector2Int>();
        foreach (var cell in previewCellsWithObjects)
            if (!allBuildingCells.Contains(cell) && !KeepBuildingPreviewCell(cell)) toUnload.Add(cell);
        foreach (var cell in loadedCells)
        {
            if (!allBuildingCells.Contains(cell))
                toUnload.Add(cell);
        }
        foreach (var cell in cellsWithShownObjects)
        {
            if (!allBuildingCells.Contains(cell) && !loadedCells.Contains(cell))
                toUnload.Add(cell);
        }

        var lodUpdates = new Dictionary<Vector2Int, LodTier>();
        foreach (var cell in cellSets.farCells)
        {
            lodUpdates[cell] = dynamicSet.Contains(cell) ? LodTier.Dynamic : (nearSet.Contains(cell) ? LodTier.Near : LodTier.Far);
        }

        // Identify cells whose tier actually changed.
        // Even if building versions are unchanged, a Far-to-Near/Dynamic cell tier transition
        // requires regeneration from distant to full detail geometry.
        var tierChangedBuildingCells = new HashSet<Vector2Int>();
        foreach (var kvp in lodUpdates)
        {
            if (!cellBuildingLod.TryGetValue(kvp.Key, out LodTier oldTier) || oldTier != kvp.Value)
            {
                tierChangedBuildingCells.Add(kvp.Key);
            }
        }

        // Fetch only unloaded cells, cells needing refetch, and cells with tier changes. Other loaded cells keep their received data
        // and objects (ReloadAllData records backend changes through MarkLoadedCellsStale).
        var cellsToLoad = new List<Vector2Int>();
        foreach (var cell in cellSets.farCells)
        {
            bool loaded = loadedCells.Contains(cell);
            if (isForce || !loaded || staleBuildingCells.Contains(cell) || tierChangedBuildingCells.Contains(cell))
            {
                cellsToLoad.Add(cell);
                RenderWorkScheduler.CountEvent(loaded ? "building.fetchExistingCells" : "building.fetchNewCells");
            }
            else
            {
                RenderWorkScheduler.CountEvent("building.fetchSkippedCells");
            }
        }

        if (cellsToLoad.Count > 0)
        {
            activeLodCoroutines.Add(RenderWorkScheduler.Start(this, LoadMergedBuildingsAndApplyCells(cellsToLoad, nearSet, dynamicSet, toUnload, lodUpdates, tierChangedBuildingCells, staleMarkRevision), "BuildingManager"));
        }
        else
        {
            ApplyLodCellChanges(cellsToLoad, toUnload, lodUpdates, staleMarkRevision);
        }
    }

    /// <summary>
    /// Generate buildings in new cells before hiding unneeded cells.
    /// Removing old cells first on camera movement would leave the city blank during API waits,
    /// so switch the visible cell set only after confirming a successful load.
    /// </summary>
    private IEnumerator LoadMergedBuildingsAndApplyCells(List<Vector2Int> cellsToLoad, HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet, List<Vector2Int> cellsToUnload, Dictionary<Vector2Int, LodTier> lodUpdates, HashSet<Vector2Int> tierChangedBuildingCells, int requestedStaleRevision)
    {
        bool succeeded = false;
        // Yield return the IEnumerator directly instead of using StartCoroutine.
        // Children launched with StartCoroutine survive StopCoroutine on their parent during camera movement,
        // allowing old-batch building generation to run alongside the new batch.
        yield return LoadMergedBuildings(cellsToLoad, nearSet, dynamicSet, tierChangedBuildingCells, success => succeeded = success);

        if (!succeeded) yield break;

        ApplyLodCellChanges(cellsToLoad, cellsToUnload, lodUpdates, requestedStaleRevision);
    }

    /// <summary>
    /// Mark successfully fetched cells as loaded and unload departing cells. requestedStaleRevision is the batch-start
    /// staleMarkRevision; if a refetch was requested during fetching, received data may predate it, so retain the refetch markers.
    /// </summary>
    private void ApplyLodCellChanges(List<Vector2Int> cellsToLoad, List<Vector2Int> cellsToUnload, Dictionary<Vector2Int, LodTier> lodUpdates, int requestedStaleRevision)
    {
        foreach (var cell in cellsToLoad)
        {
            loadedCells.Add(cell);
        }
        if (requestedStaleRevision == staleMarkRevision) staleBuildingCells.ExceptWith(cellsToLoad);
        else staleBuildingCells.UnionWith(cellsToLoad);

        foreach (var kvp in lodUpdates)
        {
            cellBuildingLod[kvp.Key] = kvp.Value;
        }

        foreach (var cell in cellsToUnload)
        {
            loadedCells.Remove(cell);
            cellBuildingLod.Remove(cell);
            staleBuildingCells.Remove(cell);
            UnloadCell(cell);
        }

        loadedCellsRevision++;

        // The merged BBOX bounds the resident cell set, so bulk fetching also queues buildings from nonresident cells.
        // Those cells are never registered and the worker can never select them; remove them now that registration is finalized.
        RemoveDeferredRequestsOutside(loadedCells);

        // Alert icon heights depend on building GameObject mesh heights, so request reapplication here after generation finishes.
        AlertManager.Instance?.RefreshPositions();
    }

    /// <summary>Remove deferred requests whose center cell is absent from cells. Retain requests without center points.</summary>
    private void RemoveDeferredRequestsOutside(HashSet<Vector2Int> cells)
    {
        var obsoleteRequests = new List<int>();
        foreach (var item in deferredDetailBuildings)
        {
            float[] point = item.Value.data.centerPointArray;
            if (point != null && point.Length >= 2 && !cells.Contains(TerrainManager.WorldToCell(new Vector3(point[0], 0f, point[1]))))
                obsoleteRequests.Add(item.Key);
        }
        foreach (int id in obsoleteRequests) deferredDetailBuildings.Remove(id);
    }

    /// <summary>
    /// Split fetched cells into a few rectangles (SplitFetchRects) and fetch buildings in bulk.
    /// Restore buildings and display outlines as each page arrives, without waiting for all pages.
    /// If any page fails, do not apply cells or delete buildings absent from the response
    /// (cellsWithShownObjects retains shown buildings for reconciliation in the next batch).
    /// </summary>
    private IEnumerator LoadMergedBuildings(List<Vector2Int> cells, HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet, HashSet<Vector2Int> tierChangedBuildingCells, Action<bool> onComplete = null)
    {
        // Buildings on rectangle boundaries also appear in adjacent rectangles, so deduplicate by ID.
        var receivedIds = new HashSet<int>();
        // Rectangle rounding or fallback to a bounding rectangle may return buildings in cells outside the fetch set (nonresident or loaded cells not being refetched).
        // Do not generate them (nonresident objects would remain outside every cell set; loaded cells can retain their existing objects).
        var fetchSet = new HashSet<Vector2Int>(cells);
        float cellSize = TerrainManager.CellSize;
        foreach (var rect in SplitFetchRects(cells))
        {
            float minX = rect.min.x * cellSize, minZ = rect.min.y * cellSize;
            float maxX = (rect.max.x + 1) * cellSize, maxZ = (rect.max.y + 1) * cellSize;
            int cursor = 0;
            while (true)
            {
                BuildingsListResponse page = null;
                yield return ApiClient.GetBulk<BuildingsListResponse>(
                    $"/buildings/?bbox={minX},{minZ},{maxX},{maxZ}&limit=500&cursor={cursor}", value => page = value,
                    timingLabel: "BuildingManager page");
                if (page?.buildings == null || (page.next_cursor.HasValue && page.next_cursor.Value <= cursor))
                {
                    onComplete?.Invoke(false);
                    yield break;
                }
                RenderWorkScheduler.CountEvent("building.received", page.buildings.Count);
                yield return PrepareReceivedBuildings(page.buildings, receivedIds, fetchSet, nearSet, dynamicSet, tierChangedBuildingCells);
                if (!page.next_cursor.HasValue) break;
                cursor = page.next_cursor.Value;
                yield return null;
            }
        }

        // Remove buildings absent from the response
        SyncBuildingsInCells(cells, receivedIds);

        onComplete?.Invoke(true);
    }

    /// <summary>Classify one received page into unchanged buildings, cache restoration, and outline display with deferred detail requests.</summary>
    private IEnumerator PrepareReceivedBuildings(List<BuildingSummary> buildings, HashSet<int> receivedIds, HashSet<Vector2Int> fetchSet,
        HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet, HashSet<Vector2Int> tierChangedBuildingCells)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Camera state for the detail generation view gate. The loop spans multiple frames, so
        // refresh it after every yield.
        Camera viewCam = Camera.main;
        Vector3 viewCamPos = viewCam != null ? viewCam.transform.position : Vector3.zero;
        Vector3 viewCamFwd = viewCam != null ? GetHorizontalForward(viewCam) : Vector3.forward;
        float tanHalfFov = DrawLodEvaluator.GetTanHalfFov(viewCam);
        bool viewOrtho = viewCam != null && viewCam.orthographic;

        preparingRepresentations++;
        try
        {
            foreach (var b in buildings)
            {
                if (b == null || !receivedIds.Add(b.agent_id)) continue;

                // Determine the LOD tier from the building center_point
                float[] cp = b.centerPointArray;
                Vector3 buildingPos = (cp != null && cp.Length >= 2)
                    ? new Vector3(cp[0], 0, cp[1])
                    : Vector3.zero;

                Vector2Int buildingCell = TerrainManager.WorldToCell(buildingPos);
                if (!fetchSet.Contains(buildingCell))
                {
                    RenderWorkScheduler.CountEvent("building.outsideFetchSkipped");
                }
                // Do not restore rubble to its original building when a bulk fetch started before the explosion arrives late.
                else if (!explodedVersions.TryGetValue(b.agent_id, out int explodedVersion) || b.version >= explodedVersion)
                {
                    LodTier desiredTier = dynamicSet.Contains(buildingCell) ? LodTier.Dynamic : (nearSet.Contains(buildingCell) ? LodTier.Near : LodTier.Far);

                    bool needsRebuild = false;

                    if (buildingVersionCache.TryGetValue(b.agent_id, out int cachedVersion) && cachedVersion == b.version)
                    {
                        // Matching version: normally no recreation is needed, but regenerate if an existing object
                        // conflicts with the new tier in a cell whose tier changed.
                        // For example, Far (white box) to Near/Dynamic, or transitions between Near and Dynamic (to enable terrain-following meshes).
                        if (tierChangedBuildingCells != null && tierChangedBuildingCells.Contains(buildingCell))
                        {
                            if (buildingObjectsCache.TryGetValue(b.agent_id, out GameObject existingTier) && existingTier != null)
                            {
                                cellBuildingLod.TryGetValue(buildingCell, out LodTier oldTier);
                                bool isFarTransition = (oldTier == LodTier.Far) || (desiredTier == LodTier.Far);
                                needsRebuild = isFarTransition;
                            }
                        }

                        if (!needsRebuild)
                        {
                            RenderWorkScheduler.CountEvent("building.dataUnchanged");
                            // Visible buildings are always active (unloading moves them to the parked cache). Reactivation is a defensive measure.
                            if (buildingObjectsCache.TryGetValue(b.agent_id, out GameObject existing) && existing != null)
                            {
                                if (!existing.activeSelf) existing.SetActive(true);
                                cellsWithShownObjects.Add(buildingCell);
                            }
                        }
                    }
                    else
                    {
                        needsRebuild = true;
                    }

                    if (needsRebuild)
                    {
                        // Detect status changes and play effects
                        if (buildingDataCache.TryGetValue(b.agent_id, out BuildingSummary oldBuildingData))
                        {
                            if (oldBuildingData.status != b.status)
                            {
                                float groundHeight = Utils.GetGroundHeight(new Vector3(buildingPos.x, 1000f, buildingPos.z));
                                Vector3 effectPos = new Vector3(buildingPos.x, groundHeight, buildingPos.z);
                                TriggerBuildingStatusEffect(effectPos, oldBuildingData.status, b.status);
                            }
                        }

                        // Reuse details when the geometry key matches even if the received version changed; display outlines first for ungenerated geometry.
                        // Pass isDynamic from desiredTier, which is already determined here
                        // (do not read cellBuildingLod, which retains the old tier until ApplyLodCellChanges after loading finishes).
                        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"BuildingManager: 建物 {b.agent_id} の再描画を開始 (version {cachedVersion} -> {b.version}, tier -> {desiredTier})");

                        // Determine render LOD solely from each building's projected ratio (screen size), independent of cell tier (per E_LOD処理.md).
                        DrawLod desiredDrawLod = DetermineDesiredDrawLod(b, viewCamPos, tanHalfFov, viewOrtho);

                        PrepareBuildingRepresentation(b, desiredTier == LodTier.Dynamic, desiredDrawLod);
                    }
                }

                // Yield to other workers when the generation slice expires; resume within the same frame if shared budget remains.
                // This reduces stuttering caused by concentrated object generation.
                if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                {
                    // Enforce the detail mesh count limit at budget slice boundaries
                    // (scanning all buildings after each one would make scanning the dominant cost).
                    EvictDetailedBuildingsOverCap(viewCamPos, viewCamFwd, justCreatedId: b.agent_id);
                    yield return RenderWorkScheduler.Continue;
                    stopwatch.Restart();
                    // Refresh camera state because the pause may span frames.
                    viewCam = Camera.main;
                    viewCamPos = viewCam != null ? viewCam.transform.position : Vector3.zero;
                    viewCamFwd = viewCam != null ? GetHorizontalForward(viewCam) : Vector3.forward;
                    tanHalfFov = DrawLodEvaluator.GetTanHalfFov(viewCam);
                    viewOrtho = viewCam != null && viewCam.orthographic;
                }
            }
        }
        finally { preparingRepresentations--; }

        // Enforce the count limit for the final slice too.
        EvictDetailedBuildingsOverCap(viewCamPos, viewCamFwd, justCreatedId: 0);
    }

    /// <summary>Fetch batches finish restoration and outline display first, then begin expensive detail generation.</summary>
    private void PrepareBuildingRepresentation(BuildingSummary data, bool isDynamic, DrawLod lod)
    {
        RegisterBuildingDataToCache(data);
        if (lod == DrawLod.Far) CreateFarBuildingObject(data);
        else if (!RestoreBuildingDetail(data, lod, keepCurrentNormal: true))
        {
            CreateFarBuildingObject(data);
            QueueDeferredDetail(data.agent_id, data, isDynamic, lod);
        }
        buildingVersionCache[data.agent_id] = data.version;
    }

    private bool RestoreBuildingDetail(BuildingSummary data, DrawLod lod, bool keepCurrentNormal = false)
    {
        if (RestoreBuildingLod(data, lod, BuildingRenderKey(data, lod), false)) return true;
        // Exclude visible normal-detail buildings from restoration scans to avoid repeatedly requeuing close-up requests.
        bool needsFallback = keepCurrentNormal || !buildingObjectsCache.TryGetValue(data.agent_id, out var shown) || shown == null
            || DrawLodInfo.GetDrawLod(shown, DrawLod.Far) < DrawLod.Normal;
        if (lod == DrawLod.Close && needsFallback && RestoreBuildingLod(data, DrawLod.Normal, BuildingRenderKey(data, DrawLod.Normal), false))
        {
            QueueDeferredDetail(data.agent_id, data, IsBuildingInDynamicCell(data), lod);
            return true;
        }
        return false;
    }

    /// <summary>Maximum fetch rectangles. Fall back to one bounding rectangle above this limit (each request round trip adds at least one frame).</summary>
    private const int MAX_FETCH_RECTS = 12;

    /// <summary>
    /// Return rectangles covering fetched cells (inclusive cell coordinates). Incremental fetch sets may form L shapes or arcs; one bounding rectangle
    /// would receive and decode loaded cells that do not need refetching. Build contiguous x intervals per z row,
    /// then merge successive rows with identical x intervals into rectangles.
    /// </summary>
    private static List<(Vector2Int min, Vector2Int max)> SplitFetchRects(List<Vector2Int> cells)
    {
        var rects = new List<(Vector2Int min, Vector2Int max)>();
        if (cells.Count == 0) return rects;
        var sorted = new List<Vector2Int>(cells);
        sorted.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));

        var open = new Dictionary<(int x0, int x1), int>();
        var next = new Dictionary<(int x0, int x1), int>();
        int prevZ = int.MinValue;
        int i = 0;
        while (i < sorted.Count)
        {
            int z = sorted[i].y;
            if (prevZ != int.MinValue && z != prevZ + 1)
            {
                foreach (var run in open) rects.Add((new Vector2Int(run.Key.x0, run.Value), new Vector2Int(run.Key.x1, prevZ)));
                open.Clear();
            }
            next.Clear();
            while (i < sorted.Count && sorted[i].y == z)
            {
                int x0 = sorted[i].x, x1 = x0;
                i++;
                while (i < sorted.Count && sorted[i].y == z && sorted[i].x <= x1 + 1) x1 = Math.Max(x1, sorted[i++].x);
                if (open.TryGetValue((x0, x1), out int z0)) open.Remove((x0, x1));
                else z0 = z;
                next[(x0, x1)] = z0;
            }
            foreach (var run in open) rects.Add((new Vector2Int(run.Key.x0, run.Value), new Vector2Int(run.Key.x1, prevZ)));
            (open, next) = (next, open);
            prevZ = z;
        }
        foreach (var run in open) rects.Add((new Vector2Int(run.Key.x0, run.Value), new Vector2Int(run.Key.x1, prevZ)));

        if (rects.Count <= MAX_FETCH_RECTS) return rects;
        Vector2Int min = sorted[0], max = sorted[0];
        foreach (var cell in sorted)
        {
            min = Vector2Int.Min(min, cell);
            max = Vector2Int.Max(max, cell);
        }
        rects.Clear();
        rects.Add((min, max));
        return rects;
    }

    /// <summary>
    /// Trigger effects according to building status changes.
    /// </summary>
    private void TriggerBuildingStatusEffect(Vector3 position, int oldStatus, int newStatus)
    {
        if (VisualEffectManager.Instance == null) return;

        // Completion transition from constructing (STATUS_CONSTRUCTING) to active (STATUS_ACTIVE)
        if (oldStatus == Constants.STATUS_CONSTRUCTING && newStatus == Constants.STATUS_ACTIVE)
        {
            VisualEffectManager.Instance.PlayConstructionComplete(position);
        }
        // Transition from active (STATUS_ACTIVE) to ruins (STATUS_COLLAPSED) or demolition (STATUS_DEMOLISHING)
        else if (oldStatus == Constants.STATUS_ACTIVE &&
                 (newStatus == Constants.STATUS_COLLAPSED
                  || newStatus == Constants.STATUS_DEMOLISHING))
        {
            VisualEffectManager.Instance.PlayDemolition(position);
        }
    }

    /// <summary>
    /// Synchronize buildings spanning multiple cells (for incremental updates).
    /// Remove buildings within the specified cells that are absent from the API response.
    /// </summary>
    private void SyncBuildingsInCells(List<Vector2Int> cells, HashSet<int> receivedIds)
    {
        List<int> idsToRemove = new List<int>();

        foreach (var cell in cells)
        {
            if (!buildingIdsByCell.TryGetValue(cell, out HashSet<int> ids)) continue;
            foreach (int id in ids)
            {
                if (!receivedIds.Contains(id))
                {
                    idsToRemove.Add(id);
                }
            }
        }

        foreach (int id in idsToRemove)
        {
            if (buildingObjectsCache.TryGetValue(id, out GameObject obj))
            {
                if (obj != null) DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Building, DrawLodInfo.GetDrawLod(obj, DrawLod.Normal), null);
                Destroy(obj);
                buildingObjectsCache.Remove(id);
            }
            UnregisterBuildingCell(id);
            // Remove terminal node and parcel reverse index entries together
            if (buildingDataCache.TryGetValue(id, out BuildingSummary removedData))
            {
                if (removedData.infra_node_id > 0)
                {
                    buildingIdByInfraNodeId.Remove(removedData.infra_node_id);
                }
                if (removedData.depot_node_id > 0)
                {
                    buildingIdByDepotNodeId.Remove(removedData.depot_node_id);
                }
                if (removedData.parcel_id > 0
                    && buildingIdsByParcel.TryGetValue(removedData.parcel_id, out HashSet<int> byParcel))
                {
                    byParcel.Remove(id);
                    if (byParcel.Count == 0) buildingIdsByParcel.Remove(removedData.parcel_id);
                }
            }
            portBuildingIds.Remove(id);
            buildingDataCache.Remove(id);
            BuildingRoadAccessIndex.Remove(id);
            ConstructionAmbience.Unregister(ConstructionAmbience.Kind.Building, id);
            // Remove view gate and LRU bookkeeping together
            detailedBuildingIds.Remove(id);
            buildingLastVisibleFrame.Remove(id);
            deferredDetailBuildings.Remove(id);
            buildingVersionCache.Remove(id);
            buildingBoundsRadius.Remove(id);
            buildingTerrainSignatures.Remove(id);
            buildingGeometryRevisions.Remove(id);
        }
    }

    // ================================================================
    // Deferred generation worker and LRU eviction
    // ================================================================

    /// <summary>Whether the requested building's cell belongs to the completed load set. Neither select nor delete requests for unregistered cells.</summary>
    private bool IsDeferredCellReady(BuildingSummary data)
    {
        float[] point = data.centerPointArray;
        return point == null || point.Length < 2 || loadedCells.Contains(TerrainManager.WorldToCell(new Vector3(point[0], 0f, point[1])));
    }

    private bool HasPendingInitialDetails
    {
        get
        {
            var cam = Camera.main;
            bool capReached = cam != null && detailedBuildingIds.Count >= maxDetailedBuildingObjects;
            foreach (var request in deferredDetailBuildings)
            {
                // Do not wait for requests in unregistered cells that the worker cannot select. Bulk fetching itself blocks completion for cells being fetched.
                if (!IsDeferredCellReady(request.Value.data)) continue;
                if (!capReached) return true;
                if (!buildingDataCache.TryGetValue(request.Key, out var data)) continue;
                if (ScoreDeferredBuilding(data, cam.transform.position, GetHorizontalForward(cam)) < DEFERRED_OUT_OF_VIEW_PENALTY) return true;
            }
            // Buildings deferred outside the view at capacity are generated by normal LOD processing after camera movement.
            return false;
        }
    }

    /// <summary>Number of top candidate slots. Take up to this many per queue scan to amortize scanning cost.</summary>
    private const int DEFERRED_TOP_CANDIDATES = 32;

    /// <summary>Signature of conditions that wake the deferred worker. Changes with the queue, terrain tiles, detail count, loaded cells, or limit.</summary>
    private int DeferredWorkSignature()
    {
        unchecked
        {
            int h = deferredRevision;
            h = h * 31 + detailTerrainRevision;
            h = h * 31 + deferredDetailBuildings.Count;
            h = h * 31 + detailedBuildingIds.Count;
            h = h * 31 + loadedCells.Count;
            h = h * 31 + loadedCellsRevision;
            return h * 31 + maxDetailedBuildingObjects;
        }
    }

    /// <summary>
    /// Persistent worker processing the deferred generation queue (deferredDetailBuildings).
    /// Generate detail meshes within the frame budget, starting with buildings in view and closest to the camera. Generate buildings outside the view
    /// during idle time while the count remains below maxDetailedBuildingObjects (reducing pop-in when turning).
    /// At capacity, leave out-of-view detail requests queued and process only requests satisfied by Far.
    /// Select the top DEFERRED_TOP_CANDIDATES in one scan and reuse them until exhausted or the viewpoint changes.
    /// If no request can progress, do not scan again until the queue, bookkeeping, or camera changes.
    /// </summary>
    private IEnumerator DetailGenerationWorker()
    {
        // Keep all state local (tests recreate the worker midway, so fields would retain stale state).
        var budgetWatch = new System.Diagnostics.Stopwatch();
        var topScore = new float[DEFERRED_TOP_CANDIDATES];
        var topId = new int[DEFERRED_TOP_CANDIDATES];
        int topCount = 0, topCursor = 0;
        long topRevision = 0;
        // Whether this candidate set generated or removed anything. If exhausted without progress, wait instead of reselecting
        // (to avoid repeated scans selecting the same candidates when all are deferred for capacity).
        bool topUseful = false;
        LodViewState topView = default;
        bool stalled = false;
        int stalledSignature = 0;
        LodViewState stalledView = default;
        int restoreRevision = -1;
        while (true)
        {
            if (preparingRepresentations > 0)
            {
                yield return null;
                continue;
            }
            if (deferredDetailBuildings.Count == 0)
            {
                stalled = false;
                topCount = topCursor = 0;
                yield return null;
                continue;
            }

            Camera cam = Camera.main;
            if (cam == null)
            {
                yield return null;
                continue;
            }
            // Collect all restorable requests before beginning expensive generation in distance order.
            if (restoreRevision != deferredRevision)
            {
                restoreRevision = deferredRevision;
                yield return RestoreDeferredBuildingRepresentations();
                topCount = topCursor = 0;
                stalled = false;
                continue;
            }
            if (stalled)
            {
                if (DeferredWorkSignature() == stalledSignature && !stalledView.Update(cam))
                {
                    yield return null;
                    continue;
                }
                stalled = false;
                topCount = topCursor = 0;
            }
            // Viewpoint changes affect both processing order and deferral decisions, so reselect candidates.
            if (topView.Update(cam)) topCount = topCursor = 0;
            Vector3 camPos = cam.transform.position;
            Vector3 fwd = GetHorizontalForward(cam);
            float tanHalfFov = DrawLodEvaluator.GetTanHalfFov(cam);
            bool ortho = cam.orthographic;

            bool progressed = false;
            bool exhausted = false;
            budgetWatch.Restart();
            while (deferredDetailBuildings.Count > 0 && budgetWatch.Elapsed.TotalMilliseconds <= PerformanceConfig.SliceBudgetMs)
            {
                if (topCursor >= topCount)
                {
                    if (topCount > 0 && !topUseful && topRevision == DeferredSelectionRevision)
                    {
                        exhausted = true;
                        break;
                    }
                    topCount = SelectTopDeferredBuildings(camPos, fwd, topScore, topId);
                    topCursor = 0;
                    topRevision = DeferredSelectionRevision;
                    topUseful = false;
                    if (topCount == 0)
                    {
                        exhausted = true;
                        break;
                    }
                }
                int id = topId[topCursor];
                float score = topScore[topCursor];
                topCursor++;

                if (!deferredDetailBuildings.TryGetValue(id, out DeferredDetailRequest req)) continue;
                if (!IsDeferredCellReady(req.data)) continue;
                if (!buildingDataCache.TryGetValue(id, out BuildingSummary latest))
                {
                    deferredDetailBuildings.Remove(id);
                    topUseful = true;
                    continue;
                }

                // Do not generate buildings in cells outside the loaded range. If the cell was unloaded
                // between enqueueing and processing here, generation would revive a building that should be hidden
                // as an active GameObject. When the cell loads again, merged loading
                // requeues it for deferred generation.
                float[] reqCp = latest.centerPointArray;
                if (reqCp != null && reqCp.Length >= 2)
                {
                    Vector2Int cell = TerrainManager.WorldToCell(new Vector3(reqCp[0], 0f, reqCp[1]));
                    if (!loadedCells.Contains(cell))
                    {
                        deferredDetailBuildings.Remove(id);
                        topUseful = true;
                        continue;
                    }
                }

                // Data may change while waiting, so reevaluate using the latest data and current camera.
                bool isDynamic = IsBuildingInDynamicCell(latest);
                DrawLod lod = DetermineDesiredDrawLod(latest, camPos, tanHalfFov, ortho);
                // At capacity, leave out-of-view detail requests queued and advance to later candidates (such as requests satisfied by Far).
                // Far-satisfied requests remaining deferred outside the candidate set are consumed as boxes when camera movement reevaluates render LOD.
                if (lod != DrawLod.Far && score >= DEFERRED_OUT_OF_VIEW_PENALTY && detailedBuildingIds.Count >= maxDetailedBuildingObjects) continue;
                deferredDetailBuildings.Remove(id);
                progressed = true;
                topUseful = true;
                if (lod != req.drawLod) RenderWorkScheduler.CountEvent("building.deferredLodChanged");
                if (lod == DrawLod.Far) CreateFarBuildingObject(latest);
                else CreateOrUpdateBuildingObject(latest, isDynamic, forceRebuild: true, drawLod: lod);
                buildingVersionCache[id] = latest.version;
                EvictDetailedBuildingsOverCap(camPos, fwd, justCreatedId: id);
            }

            // If no request could progress (and none was added since candidate selection), wait until conditions change.
            if (!progressed && exhausted && topRevision == DeferredSelectionRevision)
            {
                stalled = true;
                stalledSignature = DeferredWorkSignature();
                stalledView = default;
                stalledView.Update(cam);
            }
            yield return progressed ? RenderWorkScheduler.Continue : null;
        }
    }

    private IEnumerator RestoreDeferredBuildingRepresentations()
    {
        var ids = new List<int>(deferredDetailBuildings.Keys);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (int id in ids)
        {
            if (preparingRepresentations > 0) yield break;
            var cam = Camera.main;
            if (cam == null) yield break;
            if (deferredDetailBuildings.TryGetValue(id, out var request) && IsDeferredCellReady(request.data)
                && buildingDataCache.TryGetValue(id, out var latest))
            {
                DrawLod lod = DetermineDesiredDrawLod(latest, cam.transform.position, DrawLodEvaluator.GetTanHalfFov(cam), cam.orthographic);
                bool canRestore = lod == DrawLod.Far || detailedBuildingIds.Count < maxDetailedBuildingObjects
                    || ScoreDeferredBuilding(latest, cam.transform.position, GetHorizontalForward(cam)) < DEFERRED_OUT_OF_VIEW_PENALTY;
                if (canRestore && RestoreBuildingDetail(latest, lod))
                    EvictDetailedBuildingsOverCap(cam.transform.position, GetHorizontalForward(cam), id);
            }
            if (watch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
            {
                yield return RenderWorkScheduler.Continue;
                watch.Restart();
            }
        }
    }

    /// <summary>
    /// Select up to topId.Length requests with the best processing scores from the deferred queue, in ascending order (ties favor earlier requests).
    /// Exclude requests in unloaded cells or whose underlying resident-cell terrain tiles have not arrived
    /// (details built at overview heights would need rebuilding on tile arrival). Tile arrival advances detailTerrainRevision and triggers reselection.
    /// Requests missing center_point (score float.MaxValue) are also selected if slots remain.
    /// </summary>
    private int SelectTopDeferredBuildings(Vector3 camPos, Vector3 camForwardHorizontal, float[] topScore, int[] topId)
    {
        int capacity = topId.Length;
        int count = 0;
        TerrainManager terrain = Utils.CachedTerrainManager;
        foreach (var kvp in deferredDetailBuildings)
        {
            if (!IsDeferredCellReady(kvp.Value.data)) continue;
            if (terrain != null && !terrain.IsDetailTerrainReady(GetBuildingTerrainBounds(kvp.Value.data, GetParcelManager()?.GetParcelSummary(kvp.Value.data.parcel_id)))) continue;
            float score = ScoreDeferredBuilding(kvp.Value.data, camPos, camForwardHorizontal);
            if (count == capacity && !(score < topScore[capacity - 1])) continue;
            int at = count < capacity ? count : capacity - 1;
            while (at > 0 && score < topScore[at - 1])
            {
                topScore[at] = topScore[at - 1];
                topId[at] = topId[at - 1];
                at--;
            }
            topScore[at] = score;
            topId[at] = kvp.Key;
            if (count < capacity) count++;
        }
        return count;
    }

    /// <summary>Processing penalty added to deferred buildings outside the view. Always places them after in-view requests (distance only).</summary>
    private const float DEFERRED_OUT_OF_VIEW_PENALTY = 100000f;

    /// <summary>Deferred queue processing score (lower is earlier). Distance in view; distance plus a large penalty outside it.</summary>
    private static float ScoreDeferredBuilding(BuildingSummary data, Vector3 camPos, Vector3 camForwardHorizontal)
    {
        float[] cp = data.centerPointArray;
        if (cp == null || cp.Length < 2) return float.MaxValue;
        Vector3 to = new Vector3(cp[0] - camPos.x, 0f, cp[1] - camPos.z);
        float dist = to.magnitude;
        if (dist > BUILDING_VIEW_NEAR_DISTANCE && Vector3.Angle(camForwardHorizontal, to) > BUILDING_VIEW_HALF_ANGLE_DEG)
        {
            dist += DEFERRED_OUT_OF_VIEW_PENALTY;
        }
        return dist;
    }

    /// <summary>
    /// While detail mesh count exceeds the limit, discard out-of-view buildings that have been unseen longest.
    /// Discard only the GameObject: retain the data cache and cell reverse index, clear the version cache, and
    /// return the request to the deferred queue for regeneration when viewed. Do not discard in-view buildings
    /// (temporarily exceed the limit to preserve appearance if in-view buildings alone exceed it).
    /// Take candidates from evictionOrder in order and rebuild the ordering at most once per call.
    /// </summary>
    private void EvictDetailedBuildingsOverCap(Vector3 camPos, Vector3 camForwardHorizontal, int justCreatedId)
    {
        bool rebuilt = false;
        while (detailedBuildingIds.Count > maxDetailedBuildingObjects)
        {
            if (evictionCursor >= evictionOrder.Count || Time.frameCount - evictionOrderFrame >= VISIBILITY_SWEEP_INTERVAL_FRAMES)
            {
                if (rebuilt) break;
                RebuildEvictionOrder(camPos, camForwardHorizontal);
                rebuilt = true;
            }
            while (evictionCursor < evictionOrder.Count)
            {
                int id = evictionOrder[evictionCursor++].id;
                if (id == justCreatedId || !detailedBuildingIds.Contains(id)) continue;
                // Do not evict buildings that entered the view after sorting.
                if (buildingObjectsCache.TryGetValue(id, out GameObject obj) && obj != null && obj.activeInHierarchy
                    && IsPositionInGenerationView(obj.transform.position, camPos, camForwardHorizontal)) continue;
                EvictDetailedBuilding(id);
                break;
            }
        }
    }

    /// <summary>
    /// Reorder LRU eviction candidates by oldest last-visible frame. Put orphaned bookkeeping entries first,
    /// exclude active in-view objects, and include inactive objects (defensive handling) regardless of view.
    /// </summary>
    private void RebuildEvictionOrder(Vector3 camPos, Vector3 camForwardHorizontal)
    {
        evictionOrder.Clear();
        evictionCursor = 0;
        evictionOrderFrame = Time.frameCount;
        foreach (int id in detailedBuildingIds)
        {
            int lastSeen;
            if (!buildingObjectsCache.TryGetValue(id, out GameObject obj) || obj == null) lastSeen = int.MinValue;
            else if (obj.activeInHierarchy && IsPositionInGenerationView(obj.transform.position, camPos, camForwardHorizontal)) continue;
            else lastSeen = buildingLastVisibleFrame.TryGetValue(id, out int frame) ? frame : 0;
            evictionOrder.Add((lastSeen, id));
        }
        evictionOrder.Sort();
    }

    /// <summary>
    /// Park a detailed building through LRU eviction. Replace it with Far (a box) in resident cells and the outline retention range, moving detail into the cache.
    /// Outside that range, park the visible object too. Retain the data cache and cell reverse index.
    /// </summary>
    private void EvictDetailedBuilding(int buildingId)
    {
        // Keep a box within the outline retention range even while awaiting unload, preserving visibility when capacity eviction overlaps cell departure.
        buildingObjectsCache.TryGetValue(buildingId, out GameObject obj);
        bool knownCell = buildingCellById.TryGetValue(buildingId, out Vector2Int cell);
        bool displayable = knownCell && !pendingUnloadCells.ContainsKey(cell)
            && (loadedCells.Contains(cell) || cellsWithShownObjects.Contains(cell));
        bool keepPreview = knownCell && KeepBuildingPreviewCell(cell);
        bool hidden = obj != null && !obj.activeInHierarchy;
        if ((displayable || keepPreview) && !hidden && buildingDataCache.TryGetValue(buildingId, out BuildingSummary data))
        {
            CreateFarBuildingObject(data);
            if (!displayable)
            {
                buildingVersionCache.Remove(buildingId);
                cellsWithShownObjects.Remove(cell);
                previewCellsWithObjects.Add(cell);
            }
            return;
        }
        ParkBuildingObject(buildingId);
    }

    /// <summary>
    /// Register the building ID as holding a full detail mesh and set its last-visible frame to the current frame.
    /// Called by both generation and pooled reuse paths in CreateOrUpdateBuildingObject.
    /// </summary>
    private void MarkBuildingDetailed(int buildingId)
    {
        detailedBuildingIds.Add(buildingId);
        buildingLastVisibleFrame[buildingId] = Time.frameCount;
    }

    // ================================================================
    // Render LOD selection and reevaluation worker
    // ================================================================

    /// <summary>
    /// Building bounding sphere radius (m). Combines the farthest point from the footprint center with building height for projected ratio checks.
    /// Cache per building and reuse assuming geometry remains unchanged across versions
    /// (minor discrepancies from floor count changes are negligible relative to coarse thresholds).
    /// </summary>
    private float GetBuildingBoundsRadius(BuildingSummary data)
    {
        if (buildingBoundsRadius.TryGetValue(data.agent_id, out float cached)) return cached;
        float horizontal = 0f;
        var poly = data.polygonArray;
        if (poly != null)
        {
            for (int i = 0; i < poly.Length; i++)
            {
                if (poly[i] == null || poly[i].Length < 2) continue;
                float d = Mathf.Sqrt(poly[i][0] * poly[i][0] + poly[i][1] * poly[i][1]);
                if (d > horizontal) horizontal = d;
            }
        }
        float height = Mathf.Max(1, data.floors ?? 1) * BuildingMeshUtils.GetFloorHeight(data);
        float radius = Mathf.Max(3f, Mathf.Sqrt(horizontal * horizontal + 0.25f * height * height));
        buildingBoundsRadius[data.agent_id] = radius;
        return radius;
    }

    /// <summary>
    /// Desired building render LOD, determined solely by projected ratio (screen size), independent of cell tier (per E_LOD処理.md).
    /// Use the existing object's render LOD as the hysteresis reference.
    /// </summary>
    private DrawLod DetermineDesiredDrawLod(BuildingSummary data, Vector3 camPos, float tanHalfFov, bool orthographic)
    {
        float[] cp = data.centerPointArray;
        if (cp == null || cp.Length < 2) return DrawLod.Normal;
        ResolveBuildingLodBase(data, cp, out DrawLod current, out float baseY);
        return EvaluateBuildingDrawLod(data, current, baseY, camPos, tanHalfFov, orthographic);
    }

    /// <summary>Hysteresis reference and base height for render LOD: the existing object's values, otherwise Normal at the terrain height.</summary>
    private void ResolveBuildingLodBase(BuildingSummary data, float[] cp, out DrawLod current, out float baseY)
    {
        if (buildingObjectsCache.TryGetValue(data.agent_id, out GameObject existing) && existing != null)
        {
            current = DrawLodInfo.GetDrawLod(existing, DrawLod.Normal);
            baseY = existing.transform.position.y;
            return;
        }
        current = DrawLod.Normal;
        baseY = Utils.GetGroundHeight(new Vector3(cp[0], 1000f, cp[1]));
    }

    private DrawLod EvaluateBuildingDrawLod(BuildingSummary data, DrawLod current, float baseY, Vector3 camPos, float tanHalfFov, bool orthographic)
        => EvaluateBuildingDrawLod(data, current, baseY, camPos, tanHalfFov, orthographic, true, out _);

    /// <summary>
    /// Core render LOD decision. baseY is the building reference height; reuse an existing object's position to avoid sampling terrain
    /// (the few-meter difference from parcel elevation is negligible relative to the hysteresis band).
    /// limitClose=true allows Close only for the nearest close-up candidates (closeRanking); false evaluates the close-up condition alone to build that ranking.
    /// </summary>
    private DrawLod EvaluateBuildingDrawLod(BuildingSummary data, DrawLod current, float baseY, Vector3 camPos, float tanHalfFov, bool orthographic, bool limitClose, out float distance)
    {
        distance = float.MaxValue;
        float[] cp = data.centerPointArray;
        if (cp == null || cp.Length < 2) return DrawLod.Normal;
        float radius = GetBuildingBoundsRadius(data);
        distance = Vector3.Distance(camPos, new Vector3(cp[0], baseY + radius * 0.5f, cp[1]));
        bool allowClose = !limitClose || IsAmongNearestCloseBuildings(data.agent_id, distance, camPos, tanHalfFov, orthographic);
        return DrawLodEvaluator.EvaluateBySize(radius, distance, tanHalfFov, current, allowClose, orthographic);
    }

    private bool IsAmongNearestCloseBuildings(int id, float distance, Vector3 camPos, float tanHalfFov, bool orthographic)
    {
        // Beyond this distance Close is impossible, so most evaluations skip building the ranking.
        if (distance > DrawLodConfig.CLOSE_EXIT_MAX_DISTANCE) return false;
        RefreshCloseRanking(camPos, tanHalfFov, orthographic);
        return DrawLodEvaluator.IsAmongNearest(closeRanking, id, distance, DrawLodConfig.MAX_CLOSE_BUILDINGS);
    }

    /// <summary>
    /// Rebuild closeRanking from registered buildings whose centers lie within the close-up distance, once per frame.
    /// Candidates use the same hysteresis inputs as their own evaluation, so a ranked building stays consistent with its result.
    /// </summary>
    private void RefreshCloseRanking(Vector3 camPos, float tanHalfFov, bool orthographic)
    {
        if (closeRankingFrame == Time.frameCount) return;
        closeRankingFrame = Time.frameCount;
        closeRanking.Clear();
        float reach = DrawLodConfig.CLOSE_EXIT_MAX_DISTANCE;
        Vector2Int min = TerrainManager.WorldToCell(new Vector3(camPos.x - reach, 0f, camPos.z - reach));
        Vector2Int max = TerrainManager.WorldToCell(new Vector3(camPos.x + reach, 0f, camPos.z + reach));
        for (int x = min.x; x <= max.x; x++)
        {
            for (int z = min.y; z <= max.y; z++)
            {
                if (!buildingIdsByCell.TryGetValue(new Vector2Int(x, z), out HashSet<int> ids)) continue;
                foreach (int id in ids)
                {
                    if (!buildingDataCache.TryGetValue(id, out BuildingSummary data)) continue;
                    float[] cp = data.centerPointArray;
                    if (cp == null || cp.Length < 2) continue;
                    float dx = cp[0] - camPos.x, dz = cp[1] - camPos.z;
                    if (dx * dx + dz * dz > reach * reach) continue;
                    ResolveBuildingLodBase(data, cp, out DrawLod current, out float baseY);
                    if (EvaluateBuildingDrawLod(data, current, baseY, camPos, tanHalfFov, orthographic, false, out float distance) == DrawLod.Close)
                        closeRanking.Add((distance, id));
                }
            }
        }
        closeRanking.Sort();
    }

    /// <summary>
    /// Entry point for the deferred generation queue. The latest request replaces earlier ones for the same ID.
    /// Always enqueue here (deferredRevision wakes the deferred generation worker).
    /// </summary>
    private void QueueDeferredDetail(int id, BuildingSummary data, bool isDynamic, DrawLod drawLod)
    {
        deferredDetailBuildings[id] = new DeferredDetailRequest { data = data, isDynamic = isDynamic, drawLod = drawLod };
        deferredRevision++;
    }

    /// <summary>Camera state for detecting projected ratio changes in the render LOD worker (UpdateProjection only).</summary>
    private LodViewState drawViewState;

    /// <summary>Snapshot of IDs for one reevaluation pass, avoiding dictionary enumeration across yields.</summary>
    private readonly List<int> drawLodScanIds = new List<int>();

    /// <summary>
    /// Persistent worker reevaluating render LOD for buildings in loaded cells as the camera moves.
    /// Projected ratio depends only on position and FOV (orthographic size), so rotation alone does not trigger scans.
    /// Split scans by frame budget. Far boxes are cheap and generated immediately; enqueue Normal/Close detail requests for
    /// DetailGenerationWorker to generate within budget, prioritizing the view. Keep old objects until generation finishes.
    /// </summary>
    private IEnumerator DrawLodReevaluationWorker()
    {
        var watch = new System.Diagnostics.Stopwatch();
        while (true)
        {
            for (int i = 0; i < DrawLodConfig.REEVALUATE_INTERVAL_FRAMES; i++) yield return null;
            if (loadedCells.Count == 0) continue;
            Camera cam = Camera.main;
            if (cam == null) continue;
            if (!drawViewState.UpdateProjection(cam, DrawLodConfig.REEVALUATE_CAMERA_MOVE_DISTANCE)) continue;

            drawLodScanIds.Clear();
            foreach (var cell in loadedCells)
                if (buildingIdsByCell.TryGetValue(cell, out HashSet<int> ids)) drawLodScanIds.AddRange(ids);
            RenderWorkScheduler.CountEvent("building.lodScanned", drawLodScanIds.Count);

            Vector3 camPos = cam.transform.position;
            float tanHalfFov = DrawLodEvaluator.GetTanHalfFov(cam);
            bool ortho = cam.orthographic;
            float slice = PerformanceConfig.SliceBudgetMs;
            watch.Restart();
            for (int i = 0; i < drawLodScanIds.Count; i++)
            {
                bool heavy = ReevaluateBuildingDrawLod(drawLodScanIds[i], camPos, tanHalfFov, ortho);
                if ((heavy || (i & 31) == 31) && watch.Elapsed.TotalMilliseconds > slice)
                {
                    yield return RenderWorkScheduler.Continue;
                    watch.Restart();
                    cam = Camera.main;
                    if (cam == null)
                    {
                        // Clear the recorded camera state so buildings not reached in this scan are picked up next time.
                        drawViewState = default;
                        break;
                    }
                    camPos = cam.transform.position;
                    tanHalfFov = DrawLodEvaluator.GetTanHalfFov(cam);
                    ortho = cam.orthographic;
                    slice = PerformanceConfig.SliceBudgetMs;
                }
            }
            drawLodScanIds.Clear();
        }
    }

    /// <summary>
    /// Reevaluate one building's render LOD. Return true if Far was generated or restored (for caller budget checks).
    /// Consume pending deferred requests here as boxes when Far suffices. Leave detail requests without objects to the worker.
    /// </summary>
    private bool ReevaluateBuildingDrawLod(int id, Vector3 camPos, float tanHalfFov, bool ortho)
    {
        if (!buildingDataCache.TryGetValue(id, out BuildingSummary data)) return false;
        if (!buildingCellById.TryGetValue(id, out Vector2Int cell) || !loadedCells.Contains(cell)) return false;
        bool hasObj = buildingObjectsCache.TryGetValue(id, out GameObject obj) && obj != null;
        bool pending = deferredDetailBuildings.ContainsKey(id);
        if (!hasObj && !pending) return false;
        if (hasObj && !obj.activeSelf) return false;

        DrawLod current = hasObj ? DrawLodInfo.GetDrawLod(obj, DrawLod.Normal) : DrawLod.Normal;
        float[] cp = data.centerPointArray;
        float baseY = hasObj ? obj.transform.position.y
            : (cp != null && cp.Length >= 2 ? Utils.GetGroundHeight(new Vector3(cp[0], 1000f, cp[1])) : 0f);
        DrawLod desired = EvaluateBuildingDrawLod(data, current, baseY, camPos, tanHalfFov, ortho);
        if (desired == DrawLod.Far)
        {
            if (current == DrawLod.Far && !pending) return false;
            CreateFarBuildingObject(data);
            buildingVersionCache[id] = data.version;
            return true;
        }
        if (!hasObj || desired == current) return false;
        QueueDeferredDetail(id, data, GetCellLodTier(cell) == LodTier.Dynamic, desired);
        return false;
    }

    /// <summary>Offset (meters) lifting buildings slightly above the ground to avoid Z-fighting.</summary>
    private const float BuildingBaseLift = 0.01f;

    /// <summary>
    /// Determine building base height (world Y). Place land buildings at terrain elevation at their center, regardless of model_type
    /// (modular building foundations fill downhill gaps on slopes).
    ///
    /// Only water parcels (PARCEL_WATER) use parcel reference elevation (parcel.altitude = sea level).
    /// Terrain sampling returns the seabed (worldY&lt;0), which would submerge offshore wind turbines and similar structures.
    /// </summary>
    private float ResolveBuildingBaseHeight(ParcelSummary parcel, float cx, float cz)
    {
        if (parcel == null)
        {
            parcel = ResolveParcelBelowSeaLevel(cx, cz);
        }
        float ground = parcel != null && parcel.parcel_type == Constants.PARCEL_WATER
            ? parcel.altitude
            : Utils.GetGroundHeight(new Vector3(cx, 1000f, cz));
        return ground + BuildingBaseLift;
    }

    /// <summary>
    /// For buildings with no identifiable site and terrain elevation below sea level, find the parcel containing the center point
    /// by coordinates. Even if the parcel ID is unset or missing from the parcel cache,
    /// place offshore structures at parcel reference elevation to prevent sinking to the seabed.
    /// Perform this lookup only below sea level to avoid unnecessary work for land buildings.
    /// </summary>
    private ParcelSummary ResolveParcelBelowSeaLevel(float cx, float cz)
    {
        if (Utils.GetGroundHeight(new Vector3(cx, 1000f, cz)) >= 0f)
        {
            return null;
        }
        ParcelManager parcelManager = GetParcelManager();
        if (parcelManager == null)
        {
            return null;
        }
        int parcelId = parcelManager.GetParcelIdAtPoint(new Vector3(cx, 0f, cz));
        return parcelId > 0 ? parcelManager.GetParcelSummary(parcelId) : null;
    }

    /// <summary>
    /// Cached ParcelManager referenced by UpdateBuildingHeight.
    /// Retained to avoid calling FindAnyObjectByType per building
    /// when processing many buildings for terrain alignment.
    /// </summary>
    private ParcelManager cachedParcelManager;

    private ParcelManager GetParcelManager()
    {
        if (cachedParcelManager == null)
        {
            cachedParcelManager = UnityEngine.Object.FindAnyObjectByType<ParcelManager>();
        }
        return cachedParcelManager;
    }

    private void UpdateBuildingHeight(GameObject buildingGO)
    {
        var idInfo = buildingGO.GetComponent<ObjectIdInfo>();
        if (idInfo != null && buildingDataCache.TryGetValue(idInfo.id, out BuildingSummary buildingData))
        {
            ParcelSummary parcel = null;
            if (buildingData.parcel_id > 0)
            {
                ParcelManager parcelManager = GetParcelManager();
                if (parcelManager != null)
                {
                    parcel = parcelManager.GetParcelSummary(buildingData.parcel_id);
                }
            }

            float[] cp = buildingData.centerPointArray;
            float cx = cp != null && cp.Length >= 2 ? cp[0] : buildingGO.transform.position.x;
            float cz = cp != null && cp.Length >= 2 ? cp[1] : buildingGO.transform.position.z;

            var terrain = Utils.CachedTerrainManager;
            if (terrain != null && !terrain.HasSurfaceTile(TerrainManager.WorldToCell(new Vector3(cx, 0f, cz)))) return;
            float buildingBaseHeight = ResolveBuildingBaseHeight(parcel, cx, cz);
            buildingGO.transform.position = new Vector3(cx, buildingBaseHeight, cz);
        }
        else
        {
            Vector3 p = buildingGO.transform.position;
            float groundHeight = Utils.GetGroundHeight(new Vector3(p.x, 1000f, p.z));
            buildingGO.transform.position = new Vector3(p.x, groundHeight + BuildingBaseLift, p.z);
        }
        // Distant combined batches and instanced scatter parts do not detect position changes themselves, so notify them from the code moving the object.
        FarBuildingMember.RefreshObject(buildingGO);
        BuildingScatterMember.RefreshObject(buildingGO);
    }

    /// <summary>
    /// Register a building in the cell reverse index. shown indicates registration with a visible object; if true,
    /// record the cell in cellsWithShownObjects. Use false for data-only registration (deferred or unloaded cells).
    /// </summary>
    private void RegisterBuildingCell(int buildingId, Vector3 position, bool shown = true)
    {
        Vector2Int cell = TerrainManager.WorldToCell(position);
        if (shown) cellsWithShownObjects.Add(cell);
        if (buildingCellById.TryGetValue(buildingId, out Vector2Int previousCell) && previousCell != cell)
        {
            RemoveBuildingFromCellLookup(buildingId, previousCell);
        }

        if (!buildingIdsByCell.TryGetValue(cell, out HashSet<int> ids))
        {
            ids = new HashSet<int>();
            buildingIdsByCell[cell] = ids;
        }

        ids.Add(buildingId);
        buildingCellById[buildingId] = cell;
    }

    private void UnregisterBuildingCell(int buildingId)
    {
        if (!buildingCellById.TryGetValue(buildingId, out Vector2Int cell)) return;
        RemoveBuildingFromCellLookup(buildingId, cell);
        buildingCellById.Remove(buildingId);
    }

    private void RemoveBuildingFromCellLookup(int buildingId, Vector2Int cell)
    {
        if (!buildingIdsByCell.TryGetValue(cell, out HashSet<int> ids)) return;
        ids.Remove(buildingId);
        if (ids.Count == 0) buildingIdsByCell.Remove(cell);
    }

    // Query memory usage through Unity APIs when parking on the main thread, not during construction.
    private readonly RenderObjectCache lodCache = new RenderObjectCache(() => PerformanceConfig.BuildingCacheBytes);

    // ================================================================
    // Reuse key
    // ================================================================

    /// <summary>
    /// Geometry revision for buildings whose appearance changes without a data version change (depots following terminal positions). Include it in reuse keys to reject cached old geometry.
    /// Use a global sequence so revisions never collide with previously issued values even after records are cleared.
    /// </summary>
    private readonly Dictionary<int, int> buildingGeometryRevisions = new Dictionary<int, int>();
    private int buildingGeometryRevisionCounter;

    /// <summary>64-bit reuse key hash. Mix FNV-1a by word, then apply splitmix64 scrambling.</summary>
    private struct RenderKeyHasher
    {
        private const ulong NullMarker = 0x9E3779B97F4A7C15UL;
        private ulong h;

        public static RenderKeyHasher Create() => new RenderKeyHasher { h = 14695981039346656037UL };

        public void Add(ulong v) { h ^= v; h *= 1099511628211UL; }
        public void Add(int v) => Add((ulong)(uint)v);
        public void Add(bool v) => Add(v ? 1UL : 0UL);
        public void Add(float v) => Add(BitConverter.SingleToInt32Bits(v));
        public void Add(double v) => Add((ulong)BitConverter.DoubleToInt64Bits(v));
        public void Add(int? v) { Add(v.HasValue); if (v.HasValue) Add(v.Value); }
        public void Add(float? v) { Add(v.HasValue); if (v.HasValue) Add(v.Value); }

        public void Add(string s)
        {
            if (s == null) { Add(NullMarker); return; }
            Add(s.Length);
            for (int i = 0; i < s.Length; i++) Add((int)s[i]);
        }

        public void Add(float[] values)
        {
            if (values == null) { Add(NullMarker); return; }
            Add(values.Length);
            for (int i = 0; i < values.Length; i++) Add(values[i]);
        }

        public void Add(float[][] points)
        {
            if (points == null) { Add(NullMarker); return; }
            Add(points.Length);
            for (int i = 0; i < points.Length; i++) Add(points[i]);
        }

        /// <summary>Untyped value (construction_progress). Compare numeric values as double regardless of type; compare nonnumeric values as strings.</summary>
        public void AddValue(object value)
        {
            if (value is Newtonsoft.Json.Linq.JValue token) value = token.Value;
            if (value == null) { Add(NullMarker); return; }
            if (value is string text) { Add(text); return; }
            if (value is IConvertible convertible && TryToDouble(convertible, out double number)) { Add(number); return; }
            Add(value.ToString());
        }

        private static bool TryToDouble(IConvertible value, out double number)
        {
            try
            {
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                number = 0;
                return false;
            }
        }

        public ulong Finish()
        {
            ulong z = h + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>
    /// Hash (hexadecimal) of fields determining building appearance. Add any new appearance-affecting fields here too.
    /// BuildingSummary is immutable after reception, so compute only once per instance.
    /// </summary>
    private static readonly ConditionalWeakTable<BuildingSummary, string> buildingShapeKeys = new ConditionalWeakTable<BuildingSummary, string>();
    private static readonly ConditionalWeakTable<BuildingSummary, string>.CreateValueCallback createBuildingShapeKey = d => HashBuildingShape(d).ToString("x16");

    private static ulong HashBuildingShape(BuildingSummary d)
    {
        var hasher = RenderKeyHasher.Create();
        hasher.Add(d.agent_id);
        hasher.Add(d.variant);
        hasher.Add(d.bd_type);
        hasher.Add(d.floors);
        hasher.Add(d.status);
        hasher.Add(d.status_reason);
        hasher.AddValue(d.construction_progress);
        hasher.Add(d.polygonArray);
        hasher.Add(d.centerPointArray);
        hasher.Add(d.visual_variant);
        hasher.Add(d.visual_variant2);
        hasher.Add(d.visual_variant3);
        if (d.sub_buildings == null) hasher.Add(-1);
        else
        {
            hasher.Add(d.sub_buildings.Count);
            foreach (SubBuildingPlacement sub in d.sub_buildings)
            {
                hasher.Add(sub != null);
                if (sub == null) continue;
                hasher.Add(sub.subbuilding_id);
                hasher.Add(sub.model_prefix);
                hasher.Add(sub.position);
                hasher.Add(sub.rotation);
                hasher.Add(sub.floors);
                hasher.Add(sub.height_per_floor);
            }
        }
        hasher.Add(d.infra_node_id);
        hasher.Add(d.depot_node_id);
        return hasher.Finish();
    }

    /// <summary>
    /// Key fragment for parcel fields affecting building appearance (polygon and reference elevation), and the polygon bounding rectangle.
    /// Exclude fields unrelated to versions (land price, road access, etc.), or every parcel refetch would rebuild the building.
    /// </summary>
    private sealed class ParcelKeyPart
    {
        public string token;
        public Rect bounds;
        public bool hasBounds;
    }

    private static readonly ConditionalWeakTable<ParcelSummary, ParcelKeyPart> parcelKeyParts = new ConditionalWeakTable<ParcelSummary, ParcelKeyPart>();
    private static readonly ConditionalWeakTable<ParcelSummary, ParcelKeyPart>.CreateValueCallback createParcelKeyPart = CreateParcelKeyPart;

    private static ParcelKeyPart CreateParcelKeyPart(ParcelSummary parcel)
    {
        float[][] polygon = parcel.GetPolygonArray();
        var hasher = RenderKeyHasher.Create();
        hasher.Add(polygon);
        hasher.Add(parcel.altitude);
        var part = new ParcelKeyPart { token = parcel.parcel_id.ToString(CultureInfo.InvariantCulture) + "/" + hasher.Finish().ToString("x16") };
        if (polygon == null) return part;
        foreach (float[] point in polygon)
        {
            if (point == null || point.Length < 2) continue;
            if (!part.hasBounds) part.bounds = new Rect(point[0], point[1], 0f, 0f);
            else
            {
                part.bounds.xMin = Mathf.Min(part.bounds.xMin, point[0]); part.bounds.xMax = Mathf.Max(part.bounds.xMax, point[0]);
                part.bounds.yMin = Mathf.Min(part.bounds.yMin, point[1]); part.bounds.yMax = Mathf.Max(part.bounds.yMax, point[1]);
            }
            part.hasBounds = true;
        }
        return part;
    }

    private static ParcelKeyPart GetParcelKeyPart(ParcelSummary parcel) => parcelKeyParts.GetValue(parcel, createParcelKeyPart);

    /// <summary>
    /// Render object reuse key. Distinguishes geometry, appearance, status, sub-buildings, parcel, geometry revision, render LOD, underlying terrain revision, reclaimed land revision (port detail only), and
    /// siding track spacing (depot detail only); version-only changes leave it unchanged.
    /// Identical data always produces the same key (tests calculate and compare keys externally).
    /// Mix in <see cref="ReclaimedLandStore.ContentVersion"/> for port detail because quay geometry and placement depend on whether the parcel faces a reclaimed land quay.
    /// Far ports and non-port buildings do not depend on reclaimed land, so use "-" (avoiding rebuilds on each reclamation).
    /// Depot sidings switch between single and double track according to the track count connecting to the terminal (distant boxes remain unchanged).
    /// </summary>
    private string BuildingRenderKey(BuildingSummary data, DrawLod lod)
    {
        ParcelSummary parcel = GetParcelManager()?.GetParcelSummary(data.parcel_id);
        var terrain = Utils.CachedTerrainManager;
        string surface = lod == DrawLod.Far ? "-" : terrain != null ? terrain.GetContentStamp(GetBuildingTerrainBounds(data, parcel)) : "none";
        buildingGeometryRevisions.TryGetValue(data.agent_id, out int revision);
        int modelType = BuildingMeshBuilder.GetModelType(data);
        string reclaimed = modelType == Constants.MODEL_PORT && lod != DrawLod.Far
            ? ReclaimedLandStore.ContentVersion.ToString(CultureInfo.InvariantCulture) : "-";
        string yardLead = modelType == Constants.MODEL_RAIL_YARD && lod != DrawLod.Far
            ? RailYardBuildingMeshBuilder.ResolveLeadHalfSpacing(data).ToString("R", CultureInfo.InvariantCulture) : "-";
        return string.Concat(new[]
        {
            data.agent_id.ToString(CultureInfo.InvariantCulture), ":", buildingShapeKeys.GetValue(data, createBuildingShapeKey), ":",
            revision.ToString(CultureInfo.InvariantCulture), ":", parcel != null ? GetParcelKeyPart(parcel).token : "-", ":",
            ((int)lod).ToString(CultureInfo.InvariantCulture), ":", surface, ":", reclaimed, ":", yardLead,
        });
    }

    /// <summary>Keep objects generated using overview grounding separate from geometry built with complete terrain.</summary>
    private string BuildingGenerationKey(BuildingSummary data, DrawLod lod, string key)
    {
        var terrain = Utils.CachedTerrainManager;
        if (lod == DrawLod.Far || terrain == null) return key;
        Rect bounds = GetBuildingTerrainBounds(data, GetParcelManager()?.GetParcelSummary(data.parcel_id));
        return terrain.HasCurrentSurface(bounds) ? key : key + ":sampling:" + terrain.GetTerrainSignature(bounds).ToString(CultureInfo.InvariantCulture);
    }

    private bool RestoreBuildingLod(BuildingSummary data, DrawLod lod, string key, bool reportMiss = true)
    {
        buildingObjectsCache.TryGetValue(data.agent_id, out GameObject old);
        var identity = old != null ? old.GetComponent<RenderCacheIdentity>() : null;
        string sampledKey = BuildingGenerationKey(data, lod, key);
        GameObject ready = identity != null && (identity.key == key || identity.key == sampledKey) ? old : lodCache.Take(key);
        if (ready == null && sampledKey != key) ready = lodCache.Take(sampledKey);
        if (ready == null)
        {
            if (reportMiss) RenderWorkScheduler.CountEvent("building.buildRequested");
            return false;
        }
        RenderWorkScheduler.CountEvent(ready == old ? "building.reuseActive" : "building.reuseCached");
        if (ready != old)
        {
            DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Building,
                old != null ? DrawLodInfo.GetDrawLod(old, DrawLod.Normal) : (DrawLod?)null, lod);
            lodCache.Park(old);
        }
        ready.SetActive(true);
        // The hidden set may change while parked or before reuse; resolve the filter again as during generation.
        ObjectVisibilityFilter.ApplyToObject(ready, InputObjectType.BUILDING, data.agent_id);
        FarBuildingMember.RefreshObject(ready);
        buildingObjectsCache[data.agent_id] = ready;
        RegisterBuildingDataToCache(data);
        // Retain the completed grounding position instead of moving to coarse overview heights before terrain returns.
        var terrain = Utils.CachedTerrainManager;
        if (terrain == null || terrain.HasSurfaceTile(TerrainManager.WorldToCell(ready.transform.position))) UpdateBuildingHeight(ready);
        RecordBuildingTerrainSignature(data);
        RegisterBuildingCell(data.agent_id, ready.transform.position);
        UpdatePlannedIcon(ready, data);
        if (lod == DrawLod.Far) detailedBuildingIds.Remove(data.agent_id);
        else MarkBuildingDetailed(data.agent_id);
        deferredDetailBuildings.Remove(data.agent_id);
        buildingVersionCache[data.agent_id] = data.version;
        return true;
    }

    private void CreateFarBuildingObject(BuildingSummary buildingData)
    {
        string renderKey = BuildingRenderKey(buildingData, DrawLod.Far);
        if (RestoreBuildingLod(buildingData, DrawLod.Far, renderKey)) return;
        GameObject oldBuilding = null;
        if (buildingObjectsCache.TryGetValue(buildingData.agent_id, out GameObject existing))
        {
            if (existing != null)
            {
                var existingIdInfo = existing.GetComponent<ObjectIdInfo>();
                int currentStatus = existingIdInfo != null ? existingIdInfo.status : 0;
                bool existingIsFar = existingIdInfo != null && existingIdInfo.isFarObject;
                // Reuse from the pool if the existing object is Far LOD and its status matches
                if (currentStatus == buildingData.status && existingIsFar && existing.GetComponent<RenderCacheIdentity>()?.key == renderKey)
                {
                    if (!existing.activeSelf)
                        existing.SetActive(true);
                    ObjectVisibilityFilter.ApplyToObject(existing, InputObjectType.BUILDING, buildingData.agent_id);
                    FarBuildingMember.RefreshObject(existing);
                    RegisterBuildingDataToCache(buildingData);
                    UpdateBuildingHeight(existing);
                    RecordBuildingTerrainSignature(buildingData);
                    RegisterBuildingCell(buildingData.agent_id, existing.transform.position);
                    UpdatePlannedIcon(existing, buildingData);
                    // The tier is now Far, so invalidate any remaining deferred detail request.
                    deferredDetailBuildings.Remove(buildingData.agent_id);
                    return;
                }
                oldBuilding = existing;
            }
            else
            {
                buildingObjectsCache.Remove(buildingData.agent_id);
            }
        }

        RegisterBuildingDataToCache(buildingData);

        int status = buildingData.status;
        GameObject buildingGO = new GameObject($"{BUILDING_PREFIX}{buildingData.agent_id}");
        var idInfo = buildingGO.AddComponent<ObjectIdInfo>();
        idInfo.id = buildingData.agent_id;
        idInfo.status = status;
        idInfo.isFarObject = true;
        Transform container = GetContainer("Buildings");
        if (container != null) buildingGO.transform.SetParent(container);

        buildingGO.tag = BUILDING_TAG;

        MeshFilter mf = buildingGO.AddComponent<MeshFilter>();
        MeshRenderer mr = buildingGO.AddComponent<MeshRenderer>();

        // Distant rendering stretches the Far model to the outline or uses a box (GenerateFarBuilding). Do not run full-detail assembly.
        var farWatch = System.Diagnostics.Stopwatch.StartNew();
        BuildingMeshResult meshResult = new BuildingMeshBuilder().Generate(buildingData, false, DrawLod.Far);
        // Far GameObjects have no colliders, so release the generated collider mesh here.
        if (meshResult?.ColliderMesh != null && meshResult.ColliderMesh != meshResult.Mesh)
            MeshBakeService.Release(meshResult.ColliderMesh);
        var renderIdentity = buildingGO.AddComponent<RenderCacheIdentity>();
        renderIdentity.key = renderKey;
        renderIdentity.mesh = meshResult?.Mesh;
        DrawLodStats.AddGenerationMs(DrawLodStats.Category.Building, farWatch.Elapsed.TotalMilliseconds);
        if (meshResult?.Mesh != null)
        {
            mf.mesh = meshResult.Mesh;
        }

        mr.sharedMaterial = GetFarBuildingMaterial();
        DrawLodInfo.Ensure(buildingGO).drawLod = DrawLod.Far;

        // Far LOD buildings have no VisualModeObject (FarBuildingBatcher combines outline meshes),
        // so hide them by directly disabling MeshRenderer. FarBuildingMember is attached
        // after this, so its initial enabled state determines inclusion in the combined batch.
        // Later visibility changes must notify the combined batch through FarBuildingMember.RefreshObject.
        ObjectVisibilityFilter.ApplyToObject(buildingGO, InputObjectType.BUILDING, buildingData.agent_id);

        float[] cp = buildingData.centerPointArray;
        if (cp != null && cp.Length >= 2)
        {
            float cx = cp[0];
            float cz = cp[1];
            ParcelSummary parcel = null;
            if (buildingData.parcel_id > 0)
            {
                ParcelManager parcelManager = GetParcelManager();
                if (parcelManager != null)
                {
                    parcel = parcelManager.GetParcelSummary(buildingData.parcel_id);
                }
            }

            float buildingBaseHeight = ResolveBuildingBaseHeight(parcel, cx, cz);
            buildingGO.transform.position = new Vector3(cx, buildingBaseHeight, cz);
            RecordBuildingTerrainSignature(buildingData);
            RegisterBuildingCell(buildingData.agent_id, buildingGO.transform.position);
        }

        UpdatePlannedIcon(buildingGO, buildingData);

        if (meshResult?.Mesh != null) buildingGO.AddComponent<FarBuildingMember>().Configure(mr, meshResult.Mesh);
        buildingGO.AddComponent<UndergroundGhostObject>();

        // Update the dictionary to the new object immediately.
        // This prevents duplicate objects if the same ID is redrawn again shortly afterward.
        buildingObjectsCache[buildingData.agent_id] = buildingGO;
        DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Building,
            oldBuilding != null ? DrawLodInfo.GetDrawLod(oldBuilding, DrawLod.Normal) : (DrawLod?)null, DrawLod.Far);

        // Now that the building is Far (a box), remove it from detail LRU bookkeeping and deferred requests.
        detailedBuildingIds.Remove(buildingData.agent_id);
        buildingLastVisibleFrame.Remove(buildingData.agent_id);
        deferredDetailBuildings.Remove(buildingData.agent_id);

        // Delay only destruction of the old object by a few frames.
        if (oldBuilding != null)
        {
            lodCache.Park(oldBuilding);
        }
    }

    /// <summary>Add IDs of buildings displaying actual objects (Far, Normal, or Close) to result, for exclusion from the citywide overview.</summary>
    public void CollectShownBuildingIds(HashSet<int> result)
    {
        foreach (var pair in buildingObjectsCache)
            if (pair.Value != null && pair.Value.activeInHierarchy) result.Add(pair.Key);
    }

    public GameObject GetBuildingObject(int buildingId)
    {
        if (buildingObjectsCache.TryGetValue(buildingId, out GameObject obj))
        {
            return obj;
        }
        return null;
    }

    public List<BuildingSummary> GetAllBuildings()
    {
        return buildingDataCache.Values.ToList();
    }

    /// <summary>
    /// Collect building IDs registered in cells overlapping the specified world XZ rectangle.
    /// Used to narrow nearby building candidates without scanning everything, such as local interference estimates during path placement
    /// (O(cell count) spatial filtering through buildingIdsByCell).
    /// </summary>
    public HashSet<int> QueryBuildingIdsInBounds(float minX, float minZ, float maxX, float maxZ)
    {
        var result = new HashSet<int>();
        TerrainManager.WorldRectToCellRange(minX, minZ, maxX, maxZ, out Vector2Int minCell, out Vector2Int maxCell);
        for (int cx = minCell.x; cx <= maxCell.x; cx++)
        {
            for (int cz = minCell.y; cz <= maxCell.y; cz++)
            {
                if (buildingIdsByCell.TryGetValue(new Vector2Int(cx, cz), out HashSet<int> ids))
                {
                    foreach (int id in ids) result.Add(id);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Get cached building data for the specified ID.
    /// Loaded with the multi_agent schema (including visual_variant), so
    /// used to read appearance variants, for example when initializing BuildingPlanManager edit mode.
    /// Return null if absent from the cache (such as cells not loaded yet).
    /// </summary>
    public BuildingSummary GetBuildingSummary(int buildingId)
    {
        if (buildingDataCache.TryGetValue(buildingId, out BuildingSummary data))
        {
            return data;
        }
        return null;
    }

    /// <summary>
    /// Reapply the per-object display filter (ObjectVisibilityFilter) to generated buildings.
    /// Called when the hidden set changes. Unloaded buildings resolve the filter themselves during generation and are excluded.
    /// </summary>
    public void RefreshVisibilityFilter()
    {
        foreach (var pair in buildingObjectsCache)
        {
            ObjectVisibilityFilter.ApplyToObject(pair.Value, InputObjectType.BUILDING, pair.Key);
            FarBuildingMember.RefreshObject(pair.Value);
        }
    }

    /// <summary>
    /// Synchronize construction sound site registration with building data. Register center points only during construction or demolition; unregister otherwise.
    /// Pass backend coordinates (without FloatingOrigin offsets).
    /// </summary>
    private static void SyncConstructionSound(BuildingSummary data)
    {
        bool site = data.status == Constants.STATUS_CONSTRUCTING || data.status == Constants.STATUS_DEMOLISHING;
        float[] cp = data.centerPointArray;
        if (!site || cp == null || cp.Length < 2)
        {
            ConstructionAmbience.Unregister(ConstructionAmbience.Kind.Building, data.agent_id);
            return;
        }
        float y = Utils.GetGroundHeight(new Vector3(cp[0], 1000f, cp[1]));
        ConstructionAmbience.Register(ConstructionAmbience.Kind.Building, data.agent_id, new[] { new Vector3(cp[0], y, cp[1]) });
    }

    /// <summary>
    /// Register building data in the cache and synchronize the terminal node reverse indices
    /// (buildingIdByInfraNodeId / buildingIdByDepotNodeId). Direct assignment to buildingDataCache is prohibited;
    /// always use this method to preserve reverse index consistency.
    /// </summary>
    private void RegisterBuildingDataToCache(BuildingSummary buildingData)
    {
        // Remove old reverse entries if the previous data referenced different terminal nodes or parcels
        // (normally immutable for a facility, but synchronize defensively)
        if (buildingDataCache.TryGetValue(buildingData.agent_id, out BuildingSummary oldData))
        {
            if (oldData.infra_node_id > 0 && oldData.infra_node_id != buildingData.infra_node_id)
            {
                buildingIdByInfraNodeId.Remove(oldData.infra_node_id);
            }
            if (oldData.depot_node_id > 0 && oldData.depot_node_id != buildingData.depot_node_id)
            {
                buildingIdByDepotNodeId.Remove(oldData.depot_node_id);
            }
            if (oldData.parcel_id > 0 && oldData.parcel_id != buildingData.parcel_id
                && buildingIdsByParcel.TryGetValue(oldData.parcel_id, out HashSet<int> oldByParcel))
            {
                oldByParcel.Remove(buildingData.agent_id);
                if (oldByParcel.Count == 0) buildingIdsByParcel.Remove(oldData.parcel_id);
            }
        }

        buildingDataCache[buildingData.agent_id] = buildingData;
        BuildingRoadAccessIndex.Register(buildingData);
        buildingBoundsRadius.Remove(buildingData.agent_id);
        SyncConstructionSound(buildingData);

        if (BuildingMeshBuilder.GetModelType(buildingData) == Constants.MODEL_PORT) portBuildingIds.Add(buildingData.agent_id);
        else portBuildingIds.Remove(buildingData.agent_id);

        if (buildingData.infra_node_id > 0)
        {
            buildingIdByInfraNodeId[buildingData.infra_node_id] = buildingData.agent_id;
        }
        if (buildingData.depot_node_id > 0)
        {
            buildingIdByDepotNodeId[buildingData.depot_node_id] = buildingData.agent_id;
        }

        if (buildingData.parcel_id > 0)
        {
            if (!buildingIdsByParcel.TryGetValue(buildingData.parcel_id, out HashSet<int> byParcel))
            {
                byParcel = new HashSet<int>();
                buildingIdsByParcel[buildingData.parcel_id] = byParcel;
            }
            byParcel.Add(buildingData.agent_id);
        }
    }

    /// <summary>
    /// Return the set of building agent_ids on the specified parcel.
    /// General reverse lookup finding buildings on a parcel without scanning all buildings.
    /// Return an empty set if none match.
    /// </summary>
    public IReadOnlyCollection<int> GetBuildingIdsByParcel(int parcelId)
    {
        if (parcelId > 0 && buildingIdsByParcel.TryGetValue(parcelId, out HashSet<int> ids))
        {
            return ids;
        }
        return System.Array.Empty<int>();
    }

    /// <summary>
    /// Find owner facility building data by infrastructure terminal node ID (agents.infra_node_id).
    /// The node API uses one-way references without exposing the owner facility, so terminal identification
    /// uses this method (for infrastructure path snapping, terminal markers, and network compatibility checks).
    /// Return false if the facility is not loaded (outside the bbox).
    /// </summary>
    public bool TryGetInfraTerminalOwner(int nodeId, out BuildingSummary building)
    {
        building = null;
        if (nodeId <= 0) return false;
        return buildingIdByInfraNodeId.TryGetValue(nodeId, out int agentId)
            && buildingDataCache.TryGetValue(agentId, out building)
            && building != null;
    }

    /// <summary>
    /// Whether this is a depot terminal node (agents.depot_node_id). False if the facility is not loaded.
    /// </summary>
    public bool IsDepotTerminalNode(int nodeId)
    {
        return nodeId > 0 && buildingIdByDepotNodeId.ContainsKey(nodeId);
    }

    /// <summary>
    /// Called after terminal node movement is committed (PATCH /nodes/{id} succeeds).
    /// Depots (MODEL_RAIL_YARD) build internal tracks from the terminal position,
    /// so their appearance depends on the node position even without a version change. Find the owner
    /// through the reverse index and regenerate its mesh at the new terminal position. Other model_type values
    /// do nothing because terminal positions do not affect their appearance.
    /// </summary>
    public void OnTerminalNodeMoved(int nodeId)
    {
        if (!TryGetInfraTerminalOwner(nodeId, out BuildingSummary building))
        {
            return;
        }
        if (BuildingMeshBuilder.GetModelType(building) != Constants.MODEL_RAIL_YARD)
        {
            return;
        }
        // The data version is unchanged, so advance the geometry revision to prevent reuse of parked or visible objects.
        buildingGeometryRevisions[building.agent_id] = ++buildingGeometryRevisionCounter;
        CreateOrUpdateBuildingObject(building, IsBuildingInDynamicCell(building), forceRebuild: true, drawLod: GetCurrentDrawLod(building.agent_id));
    }

    /// <summary>
    /// Called by PathManager when tracks connecting to a depot terminal (depot_node_id) are added, changed, or removed.
    /// Depot (MODEL_RAIL_YARD) sidings are single or double track depending on the track count connecting to the terminal.
    /// The building version is unchanged, so rebuild visible detail objects only when their reuse key changes.
    /// Parked or ungenerated depots include sidings in their reuse key and take the new shape on next generation.
    /// </summary>
    public void OnDepotTerminalPathsChanged(int nodeId)
    {
        if (nodeId <= 0 || !buildingIdByDepotNodeId.TryGetValue(nodeId, out int agentId)
            || !buildingDataCache.TryGetValue(agentId, out BuildingSummary building)) return;
        if (BuildingMeshBuilder.GetModelType(building) != Constants.MODEL_RAIL_YARD) return;
        if (!buildingObjectsCache.TryGetValue(agentId, out GameObject obj) || obj == null || !obj.activeSelf) return;
        DrawLod lod = DrawLodInfo.GetDrawLod(obj, DrawLod.Normal);
        if (lod == DrawLod.Far) return;
        var identity = obj.GetComponent<RenderCacheIdentity>();
        if (identity != null && identity.key == BuildingRenderKey(building, lod)) return;
        CreateOrUpdateBuildingObject(building, IsBuildingInDynamicCell(building), drawLod: lod);
    }

    /// <summary>
    /// Called when reclaimed land contents (<see cref="ReclaimedLandStore.ContentVersion"/>) change
    /// by <see cref="ReclaimedLandManager.OnContentChanged"/>. Port (MODEL_PORT) geometry depends on whether the parcel faces a reclaimed land quay,
    /// but reclamation does not change the port's version, so rebuild visible ports here.
    /// Far geometry is independent of reclaimed land and remains unchanged; rebuild detail immediately in view or enqueue it outside the view.
    /// Parked or ungenerated ports include the revision in their reuse key and take the new shape on next generation.
    /// </summary>
    public void OnReclaimedLandsChanged()
    {
        if (portBuildingIds.Count == 0) return;
        Camera cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        Vector3 camFwd = cam != null ? GetHorizontalForward(cam) : Vector3.forward;
        // Rebuilding may modify the set through RegisterBuildingDataToCache, so copy before iterating.
        foreach (int id in new List<int>(portBuildingIds))
        {
            if (!buildingObjectsCache.TryGetValue(id, out GameObject obj) || obj == null || !obj.activeSelf) continue;
            if (!buildingDataCache.TryGetValue(id, out BuildingSummary building)) continue;
            DrawLod lod = DrawLodInfo.GetDrawLod(obj, DrawLod.Normal);
            // A single building generation failure must not stop rebuilding the remaining ports.
            try
            {
                if (lod == DrawLod.Far) continue;
                bool isDynamic = IsBuildingInDynamicCell(building);
                if (cam == null || IsPositionInGenerationView(obj.transform.position, camPos, camFwd))
                    CreateOrUpdateBuildingObject(building, isDynamic, forceRebuild: true, drawLod: lod);
                else
                    QueueDeferredDetail(id, building, isDynamic, lod);
            }
            catch (Exception e)
            {
                Debug.LogError($"BuildingManager: 埋立地の変更に伴う港の作り直しに失敗 agent_id={id}: {e}");
            }
        }
    }

    /// <summary>Render LOD of the generated object (Normal if ungenerated). Used to retain current detail during individual updates.</summary>
    private DrawLod GetCurrentDrawLod(int buildingId)
    {
        if (buildingObjectsCache.TryGetValue(buildingId, out GameObject obj) && obj != null)
        {
            DrawLod lod = DrawLodInfo.GetDrawLod(obj, DrawLod.Normal);
            return lod == DrawLod.Far ? DrawLod.Normal : lod;
        }
        return DrawLod.Normal;
    }

    /// <summary>
    /// Queue departing cells for incremental unloading. CellUnloadWorker parks detail and displays outlines within budget
    /// (calling SetActive(false) on hundreds of buildings in a cell at once would cause spikes).
    /// </summary>
    protected override void UnloadCell(Vector2Int cellIndex)
    {
        cellsWithShownObjects.Remove(cellIndex);
        if (!KeepBuildingPreviewCell(cellIndex)) previewCellsWithObjects.Remove(cellIndex);
        if (!buildingIdsByCell.TryGetValue(cellIndex, out HashSet<int> ids)) return;
        pendingUnloadCells[cellIndex] = ++unloadGeneration;

        // Discard deferred requests for cells outside the range (to keep the worker from reviving
        // active buildings outside the loaded range). When the cell reloads,
        // merged loading enqueues them again.
        foreach (int id in ids) deferredDetailBuildings.Remove(id);
    }

    /// <summary>
    /// Persistent worker parking detail in departing cells and retaining nearby outlines. Snapshot IDs per cell and split work by frame budget.
    /// Discard a snapshot if reentry or reregistration changes its cell record.
    /// </summary>
    private IEnumerator CellUnloadWorker()
    {
        var watch = new System.Diagnostics.Stopwatch();
        while (true)
        {
            if (pendingUnloadCells.Count == 0)
            {
                yield return null;
                continue;
            }

            Vector2Int cell = default;
            int generation = 0;
            foreach (var pair in pendingUnloadCells)
            {
                cell = pair.Key;
                generation = pair.Value;
                break;
            }
            unloadScanIds.Clear();
            if (buildingIdsByCell.TryGetValue(cell, out HashSet<int> ids)) unloadScanIds.AddRange(ids);

            bool interrupted = false;
            watch.Restart();
            for (int i = 0; i < unloadScanIds.Count; i++)
            {
                ParkUnloadedBuilding(unloadScanIds[i], cell);
                if ((i & 15) == 15 && watch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                {
                    yield return RenderWorkScheduler.Continue;
                    watch.Restart();
                    if (!pendingUnloadCells.ContainsKey(cell))
                    {
                        interrupted = true;
                        break;
                    }
                }
            }
            unloadScanIds.Clear();
            if (!interrupted && pendingUnloadCells.TryGetValue(cell, out int current) && current == generation)
                pendingUnloadCells.Remove(cell);
            yield return RenderWorkScheduler.Continue;
        }
    }

    /// <summary>
    /// Park detail in departing cells and retain nearby outlines. Clear received versions to compare current data and restoration keys on reentry.
    /// Retain the data cache, cell reverse index, terminal/parcel indices, and construction sounds according to data lifetime.
    /// </summary>
    private void ParkUnloadedBuilding(int id, Vector2Int cell)
    {
        if (loadedCells.Contains(cell)) return;
        if (!buildingCellById.TryGetValue(id, out Vector2Int currentCell) || currentCell != cell) return;
        deferredDetailBuildings.Remove(id);
        if (!buildingObjectsCache.ContainsKey(id)) return;
        if (KeepBuildingPreviewCell(cell) && buildingDataCache.TryGetValue(id, out BuildingSummary data))
        {
            CreateFarBuildingObject(data);
            buildingVersionCache.Remove(id);
            cellsWithShownObjects.Remove(cell);
            previewCellsWithObjects.Add(cell);
        }
        else ParkBuildingObject(id);
    }

    /// <summary>Retain lightweight outlines by distance independent of camera direction; pass only more distant objects to normal parking.</summary>
    private bool KeepBuildingPreviewCell(Vector2Int cell)
    {
        var camera = Camera.main;
        if (camera == null) return false;
        float size = TerrainManager.CellSize;
        var position = camera.transform.position;
        float dx = Mathf.Max(cell.x * size - position.x, Mathf.Max(0f, position.x - (cell.x + 1) * size));
        float dz = Mathf.Max(cell.y * size - position.z, Mathf.Max(0f, position.z - (cell.y + 1) * size));
        float radius = LodConfig.MAX_FOCUS_RADIUS * 2f;
        return dx * dx + dz * dz <= radius * radius;
    }

    /// <summary>Release CPU copies only for completed display meshes requiring neither further processing nor physics baking.</summary>
    private static void ReleaseBuildingCpuCopy(BuildingMeshResult result)
    {
        if (result?.Mesh != null && result.Mesh != result.ColliderMesh && result.Mesh.isReadable)
            result.Mesh.UploadMeshData(true);
    }

    /// <summary>Move the building object into the parked cache and remove its bookkeeping and version.</summary>
    private void ParkBuildingObject(int id)
    {
        if (buildingObjectsCache.TryGetValue(id, out GameObject obj) && obj != null)
        {
            DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Building, DrawLodInfo.GetDrawLod(obj, DrawLod.Normal), null);
            lodCache.Park(obj);
        }
        buildingObjectsCache.Remove(id);
        buildingVersionCache.Remove(id);
        detailedBuildingIds.Remove(id);
        buildingLastVisibleFrame.Remove(id);
        buildingTerrainSignatures.Remove(id);
    }

    /// <summary>
    /// Use shared replacements of MeshRenderer materials with Custom/DepthOffsetLit to prevent Z-fighting.
    /// These are shared, so RenderCacheIdentity must not own them.
    /// </summary>
    private static void ApplyDepthOffsetShader(MeshRenderer renderer)
    {
        if (renderer == null) return;
        renderer.sharedMaterials = GetSharedDepthOffsetMaterials(renderer.sharedMaterials);
    }

    private static readonly Dictionary<Material, Material> depthOffsetMaterials = new Dictionary<Material, Material>();
    private static Shader depthOffsetShader;
    private static bool depthOffsetShaderMissingWarned;

    /// <summary>
    /// Return one DepthOffsetLit version per original material, shared across all buildings.
    /// VisualModeObject changes individual building colors through sharedMaterials replacement and property blocks, so
    /// sharing is safe (never modify each building renderer's .material). Retain across sessions.
    /// </summary>
    internal static Material[] GetSharedDepthOffsetMaterials(Material[] source)
    {
        if (source == null) return null;
        if (depthOffsetShader == null) depthOffsetShader = Shader.Find("Custom/DepthOffsetLit");
        if (depthOffsetShader == null)
        {
            if (!depthOffsetShaderMissingWarned) Debug.LogWarning("BuildingManager: Custom/DepthOffsetLit shader not found");
            depthOffsetShaderMissingWarned = true;
            return source;
        }
        var result = new Material[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            Material src = source[i];
            if (src == null || src.shader == depthOffsetShader)
            {
                result[i] = src;
                continue;
            }
            if (!depthOffsetMaterials.TryGetValue(src, out Material shared) || shared == null)
            {
                shared = new Material(src) { name = src.name + " (DepthOffset)" };
                shared.shader = depthOffsetShader;
                // Shared advertisement/sign materials (built-in Standard) store images and colors in _MainTex/_Color.
                // DepthOffsetLit reads _BaseMap/_BaseColor like URP Lit, so copy the values.
                if (!src.HasProperty("_BaseMap") && src.HasProperty("_MainTex"))
                {
                    shared.SetTexture("_BaseMap", src.GetTexture("_MainTex"));
                    shared.SetTextureScale("_BaseMap", src.GetTextureScale("_MainTex"));
                    shared.SetTextureOffset("_BaseMap", src.GetTextureOffset("_MainTex"));
                    if (src.HasProperty("_Color")) shared.SetColor("_BaseColor", src.GetColor("_Color"));
                }
                depthOffsetMaterials[src] = shared;
            }
            result[i] = shared;
        }
        return result;
    }

    private static Material farBuildingMaterial;
    // Distant materials derive from default building materials (asset pack materials when available), so recreate them when switching cities.
    static BuildingManager() => AssetPackStore.CityChanging += () => farBuildingMaterial = null;

    public static Material GetFarBuildingMaterial()
    {
        if (farBuildingMaterial != null) return farBuildingMaterial;
        var source = BuildingMeshBuilder.GetDefaultBuildingMaterial();
        var shader = Shader.Find("Custom/DepthOffsetLit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        if (source == null && shader == null) return null;
        farBuildingMaterial = source != null ? new Material(source) : new Material(shader);
        farBuildingMaterial.name = "FarBuildings";
        if (shader != null) farBuildingMaterial.shader = shader;
        return farBuildingMaterial;
    }

    /// <summary>
    /// Create or update the full-detail building object.
    /// </summary>
    /// <param name="isDynamic">
    /// Whether to generate for a Dynamic cell. When true,
    /// fences and ground panels follow terrain during mesh generation. Pass the tier already determined by the caller
    /// (cellBuildingLod retains the old tier during LOD loading, so reading the table would return the wrong value).
    /// </param>
    /// <param name="forceRebuild">
    /// When true, skip pooled reuse of the existing object and always regenerate the mesh.
    /// Used on tier changes or terrain-following regeneration after dynamic terrain updates.
    /// </param>
    /// <param name="drawLod">Render LOD (Normal / Close). CreateFarBuildingObject handles Far.</param>
    private void CreateOrUpdateBuildingObject(BuildingSummary buildingData, bool isDynamic, bool forceRebuild = false, DrawLod drawLod = DrawLod.Normal)
    {
        if (drawLod == DrawLod.Far || drawLod == DrawLod.Hidden) drawLod = DrawLod.Normal;
        string renderKey = BuildingRenderKey(buildingData, drawLod);
        if (RestoreBuildingLod(buildingData, drawLod, renderKey)) return;
        renderKey = BuildingGenerationKey(buildingData, drawLod, renderKey);
        forceRebuild = true;
        GameObject oldBuilding = null;
        if (buildingObjectsCache.TryGetValue(buildingData.agent_id, out GameObject existingBuilding))
        {
            if (existingBuilding != null)
            {
                var existingIdInfo = existingBuilding.GetComponent<ObjectIdInfo>();
                int currentStatus = existingIdInfo != null ? existingIdInfo.status : 0;
                bool existingIsFar = existingIdInfo != null && existingIdInfo.isFarObject;
                bool sameDrawLod = DrawLodInfo.GetDrawLod(existingBuilding, DrawLod.Normal) == drawLod;
                // Do not reuse Far LOD objects (boxes) for near views.
                // Their mesh detail differs entirely, so destroy and regenerate for near views. The same applies when render LOD differs.
                // forceRebuild explicitly requests mesh regeneration, so do not reuse.
                if (!forceRebuild && currentStatus == buildingData.status && !existingIsFar && sameDrawLod)
                {
                    // Pool reuse: reshow the hidden object and update its position
                    if (!existingBuilding.activeSelf)
                        existingBuilding.SetActive(true);
                    RegisterBuildingDataToCache(buildingData);
                    UpdateBuildingHeight(existingBuilding);
                    RecordBuildingTerrainSignature(buildingData);
                    RegisterBuildingCell(buildingData.agent_id, existingBuilding.transform.position);
                    MarkBuildingDetailed(buildingData.agent_id);
                    VisualModeObject vmo = existingBuilding.GetComponent<VisualModeObject>();
                    if (vmo != null)
                    {
                        vmo.useChildRenderers = true;
                        vmo.InitializeMaterials();
                    }
                    UpdatePlannedIcon(existingBuilding, buildingData);
                    return;
                }
                // On tier or status changes, generate the new mesh before destroying the old one.
                // This prevents brief disappearance (popping) between Destroy and Instantiate.
                oldBuilding = existingBuilding;
            }
            else
            {
                buildingObjectsCache.Remove(buildingData.agent_id);
            }
        }

        RegisterBuildingDataToCache(buildingData);

        int status = buildingData.status;
        GameObject buildingGO = new GameObject($"{BUILDING_PREFIX}{buildingData.agent_id}");
        var idInfo = buildingGO.AddComponent<ObjectIdInfo>();
        idInfo.id = buildingData.agent_id;
        idInfo.status = status;
        idInfo.isFarObject = false;
        Transform container = GetContainer("Buildings");
        if (container != null) buildingGO.transform.SetParent(container);

        buildingGO.tag = BUILDING_TAG;

        MeshFilter mf = buildingGO.AddComponent<MeshFilter>();
        MeshRenderer mr = buildingGO.AddComponent<MeshRenderer>();

        var genWatch = System.Diagnostics.Stopwatch.StartNew();
        BuildingMeshResult meshResult = new BuildingMeshBuilder().Generate(buildingData, true, drawLod, separateExterior: true, animateCrane: true);
        ConstructionCraneAnimator.Attach(buildingGO, meshResult?.CraneTop, buildingData.agent_id);
        var renderIdentity = buildingGO.AddComponent<RenderCacheIdentity>();
        renderIdentity.key = renderKey;
        renderIdentity.mesh = meshResult?.Mesh;
        renderIdentity.colliderMesh = meshResult?.ColliderMesh;
        ReleaseBuildingCpuCopy(meshResult);
        DrawLodStats.AddGenerationMs(DrawLodStats.Category.Building, genWatch.Elapsed.TotalMilliseconds);
        DrawLodInfo.Ensure(buildingGO).drawLod = drawLod;
        if (meshResult is not null && meshResult.Mesh != null)
        {
            mf.mesh = meshResult.Mesh;
        }
        else
        {
            mf.mesh = new Mesh();
        }

        Material[] sharedMaterials = meshResult?.Materials;
        if (sharedMaterials != null && sharedMaterials.Length > 0)
        {
            mr.sharedMaterials = sharedMaterials;
        }
        else
        {
            Material fallbackMaterial = BuildingMeshBuilder.GetDefaultBuildingMaterial();
            if (fallbackMaterial != null)
            {
                mr.sharedMaterial = fallbackMaterial;
            }
        }
        ApplyDepthOffsetShader(mr);

        float[] cp = buildingData.centerPointArray;
        if (cp != null && cp.Length >= 2)
        {
            float cx = cp[0];
            float cz = cp[1];
            ParcelSummary parcel = null;
            if (buildingData.parcel_id > 0)
            {
                ParcelManager parcelManager = GetParcelManager();
                if (parcelManager != null)
                {
                    parcel = parcelManager.GetParcelSummary(buildingData.parcel_id);
                }
            }

            float buildingBaseHeight = ResolveBuildingBaseHeight(parcel, cx, cz);
            buildingGO.transform.position = new Vector3(cx, buildingBaseHeight, cz);
            RecordBuildingTerrainSignature(buildingData);
            RegisterBuildingCell(buildingData.agent_id, buildingGO.transform.position);
        }

        UpdatePlannedIcon(buildingGO, buildingData);

        if (meshResult != null && meshResult.ColliderMesh != null)
        {
            // Collider physics data generation (PhysX baking) is expensive, so run it asynchronously on another thread.
            RenderWorkScheduler.Start(this, SetColliderAsync(buildingGO, meshResult.ColliderMesh), "BuildingManager");
        }
        BuildingInputHandler inputHandler = buildingGO.AddComponent<BuildingInputHandler>();
        inputHandler.buildingId = buildingData.agent_id;

        VisualModeObject visualModeObject = buildingGO.AddComponent<VisualModeObject>();
        visualModeObject.objectType = InputObjectType.BUILDING;
        visualModeObject.useChildRenderers = true;
        visualModeObject.InitializeMaterials();
        visualModeObject.SetStatus(status);
        // The per-object display filter must also affect buildings generated later through incremental loading,
        // so resolve its initial state here immediately after generation.
        visualModeObject.SetFilterHidden(
            ObjectVisibilityFilter.IsHidden(InputObjectType.BUILDING, buildingData.agent_id));
        buildingGO.AddComponent<VisualBuildingObject>();

        if (meshResult?.ExteriorJobs != null && meshResult.ExteriorJobs.Count > 0)
            buildingGO.AddComponent<BuildingExteriorRenderer>().Configure(meshResult.ExteriorJobs);

        var stats = buildingGO.AddComponent<VisualBuildingStats>();
        stats.InitFrom(buildingData);

        if (mr != null && (buildingData.status == Constants.STATUS_SCHEDULED_CONSTRUCTION || buildingData.status == Constants.STATUS_PENDING))
        {
            int flags = VisualModeHandler.Instance != null ? VisualModeHandler.Instance.GetFlags(InputObjectType.BUILDING) : VisualFlag.SHOW_PLAN;
            mr.enabled = (flags & VisualFlag.SHOW_PLAN) != 0;
        }
        BuildingScatterMember.Add(buildingGO, meshResult?.Scatter);

        // Update the dictionary to the new object immediately.
        // This prevents duplicate objects if the same ID is redrawn again shortly afterward.
        buildingObjectsCache[buildingData.agent_id] = buildingGO;
        MarkBuildingDetailed(buildingData.agent_id);
        DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Building,
            oldBuilding != null ? DrawLodInfo.GetDrawLod(oldBuilding, DrawLod.Normal) : (DrawLod?)null, drawLod);
        // The detail mesh is generated, so remaining deferred requests are unnecessary.
        deferredDetailBuildings.Remove(buildingData.agent_id);

        // Delay only destruction of the old object by a few frames.
        if (oldBuilding != null)
        {
            lodCache.Park(oldBuilding);
        }
    }

    /// <summary>
    /// Destroy the specified object after a few frames.
    /// Remove the old object after the new one has rendered
    /// to prevent brief disappearance (popping).
    /// The caller updates the dictionary immediately; this coroutine only handles destruction.
    /// </summary>
    private IEnumerator DestroyObjectAfterFrames(GameObject obj)
    {
        return Utils.DestroyObjectAfterFrames(this, obj);
    }

    /// <summary>
    /// Create, update, or delete the building's planning icon.
    /// Display a 2D icon above the roof for planned buildings (status=3).
    /// Manage the icon as a child of the building object, subject to VisualModeObject visibility control.
    /// </summary>
    private void UpdatePlannedIcon(GameObject buildingGO, BuildingSummary buildingData)
    {
        bool isPlanned = buildingData.status == Constants.STATUS_PENDING;
        Transform iconTrans = buildingGO.transform.Find("PlannedIcon");
        if (!isPlanned)
        {
            if (iconTrans != null)
                Destroy(iconTrans.gameObject);
            return;
        }

        if (plannedBuildingIcon == null)
            return;

        GameObject iconGO;
        if (iconTrans == null)
        {
            iconGO = new GameObject("PlannedIcon");
            iconGO.transform.SetParent(buildingGO.transform, false);
            SpriteRenderer sr = iconGO.AddComponent<SpriteRenderer>();
            sr.sprite = plannedBuildingIcon;
            sr.sortingOrder = 10;
            iconGO.AddComponent<Billboard>();
        }
        else
        {
            iconGO = iconTrans.gameObject;
            if (!iconGO.activeSelf)
                iconGO.SetActive(true);
        }

        // Get the building mesh height and place the icon above the roof
        float buildingHeight = 0f;
        MeshFilter mf = buildingGO.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            buildingHeight = mf.sharedMesh.bounds.size.y;
        }
        else if (buildingData != null && buildingData.floors > 0)
        {
            // Without a mesh, estimate using floors * default floor height (3.5m)
            buildingHeight = (buildingData.floors ?? 0) * 3.5f;
        }

        iconGO.transform.localPosition = Vector3.up * (buildingHeight + plannedBuildingIconYOffset);
        iconGO.transform.localScale = Vector3.one * plannedBuildingIconScale;
    }

    /// <summary>Refetch one building. Only explosion success notifications add a one-shot effect while switching to rubble.</summary>
    public IEnumerator FetchBuilding(int agentId, bool playExplosion = false)
    {
        if (agentId <= 0 || (playExplosion && !pendingExplosions.Add(agentId))) yield break;
        int generation = explosionGeneration;
        try
        {
            yield return ApiClient.Get<BuildingSummary>(
                $"/buildings/{agentId}",
                building =>
                {
                    // Do not carry requests completed during a session switch into the new city.
                    if (generation != explosionGeneration || building == null)
                        return;
                    if (playExplosion && (building.status != Constants.STATUS_COLLAPSED ||
                        (explodedVersions.TryGetValue(agentId, out int version) && version >= building.version)))
                        return;
                    if (buildingDataCache.TryGetValue(agentId, out var cached) && cached.version > building.version)
                        return;
                    float[] cp = building.centerPointArray;
                    if (cp == null || cp.Length < 2) return;

                    // Do not create visible objects in cells outside the loaded range (update data only; merged loading generates them on reentry).
                    // Update existing visible objects even outside the range (incremental unloading parks them later).
                    Vector3 center = new Vector3(cp[0], 0f, cp[1]);
                    bool shownOutside = buildingObjectsCache.TryGetValue(agentId, out GameObject shownObj) && shownObj != null && shownObj.activeSelf;
                    if (loadedCells.Contains(TerrainManager.WorldToCell(center)) || shownOutside)
                    {
                        DrawLod lod = GetCurrentDrawLod(agentId);
                        if (lod == DrawLod.Far) CreateFarBuildingObject(building);
                        else CreateOrUpdateBuildingObject(building, IsBuildingInDynamicCell(building), forceRebuild: true, drawLod: lod);
                        buildingVersionCache[agentId] = building.version;
                    }
                    else
                    {
                        RegisterBuildingDataToCache(building);
                        RegisterBuildingCell(agentId, center, shown: false);
                        buildingVersionCache.Remove(agentId);
                    }
                    if (playExplosion)
                    {
                        explodedVersions[agentId] = building.version;
                        float radius = FireEffectManager.ResolveFootprintRadius(building, cp[0], cp[1]);
                        var parcel = building.parcel_id > 0 ? GetParcelManager()?.GetParcelSummary(building.parcel_id) : null;
                        float baseHeight = ResolveBuildingBaseHeight(parcel, cp[0], cp[1]);
                        // Do not parent to the building, preventing LOD regeneration from cutting off lingering smoke or sound.
                        VisualEffectManager.Instance?.PlayExplosion(new Vector3(cp[0], baseHeight + 0.5f, cp[1]), radius);
                    }
                },
                error => Debug.LogError($"FetchBuilding({agentId}) failed: {error}"));
        }
        finally
        {
            if (playExplosion && generation == explosionGeneration) pendingExplosions.Remove(agentId);
        }
    }

    /// <summary>
    /// Whether the cell containing the building center_point is currently Dynamic.
    /// Accurate only when cellBuildingLod has been updated (such as individual fetches).
    /// During LoadMergedBuildings the tier table is stale; pass desiredTier directly.
    /// </summary>
    private bool IsBuildingInDynamicCell(BuildingSummary buildingData)
    {
        float[] cp = buildingData.centerPointArray;
        if (cp == null || cp.Length < 2) return false;
        Vector2Int cell = TerrainManager.WorldToCell(new Vector3(cp[0], 0f, cp[1]));
        return GetCellLodTier(cell) == LodTier.Dynamic;
    }

    /// <summary>
    /// Generate mesh collider physics data asynchronously on another thread (Physics.BakeMesh),
    /// then assign it to the collider component after baking finishes.
    /// This reduces stuttering caused by physics baking on the main thread.
    /// </summary>
    private IEnumerator SetColliderAsync(GameObject targetGO, Mesh mesh)
    {
        if (mesh == null) yield break;

        Task bakeTask = MeshBakeService.Bake(mesh);
        yield return Utils.WaitTask(bakeTask);

        if (targetGO != null)
        {
            MeshCollider collider = targetGO.GetComponent<MeshCollider>();
            if (collider == null)
            {
                collider = targetGO.AddComponent<MeshCollider>();
            }
            collider.sharedMesh = mesh;
        }
    }

    /// <summary>
    /// Return the current LOD tier of the specified cell.
    /// </summary>
    public LodTier GetCellLodTier(Vector2Int cell)
    {
        if (cellBuildingLod.TryGetValue(cell, out LodTier tier))
        {
            return tier;
        }
        return LodTier.Far;
    }

}

/// <summary>
/// Building list API (GET /buildings/) response.
/// The response body is {"buildings": [BuildingSummary, ...]}.
/// </summary>
[Serializable]
public class BuildingsListResponse
{
    public int? next_cursor;
    public List<BuildingSummary> buildings;
}
