using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using GeneratedModels;

public class CellDataLoader : MonoBehaviour
{
    public static CellDataLoader Instance { get; private set; }

    public BuildingManager buildingManager;
    public ParcelManager parcelManager;
    public PathManager pathManager;
    public ZoneManager zoneManager;
    public TerrainManager terrainManager;
    public HeightmapTextureManager heightmapTextureManager;
    public ResourceManager resourceManager;
    public PropManager propManager;
    private InterchangeManager interchangeManager;
    private RoadManager roadManager;
    private CityOverviewManager cityOverviewManager;

    // Scene in which to create objects
    private UnityEngine.SceneManagement.Scene targetScene;

    private Vector2Int currentCenterCell = new Vector2Int(int.MinValue, int.MinValue);

    private Dictionary<Vector2Int, LodTier> currentCellTiers = new Dictionary<Vector2Int, LodTier>();
    private List<string> lastTierChangeDescriptions = new List<string>();

    /// <summary>
    /// Current load batch. Issue a new batch for each UpdateManagers call and cancel the previous one.
    /// Child work that StopCoroutine cannot stop checks this batch's IsCanceled and stops cooperatively.
    /// Terrain tile jobs and background terrain live according to cell residency and session, so batch cancellation does not stop them.
    /// </summary>
    private LoadBatch activeLoadBatch;

    /// <summary>
    /// Whether initial loading has finished. True once all resident-cell terrain tiles in the first batch are published.
    /// </summary>
    private bool isInitialLoadComplete = false;
    public bool IsInitialLoadComplete => isInitialLoadComplete;
    /// <summary>
    /// Whether the initial scene is ready to display. Count throttled, pending camera batches (hasDeferredCameraCell) as unfinished too.
    /// While deferred, pendingObjectLoad is empty; excluding them would report readiness before issuing the final-viewpoint batch.
    /// </summary>
    public bool IsInitialSceneReady => isInitialLoadComplete
        && !loadSuspendedUntilReload && !hasDeferredCameraCell && pendingObjectLoad == null
        && (resourceManager == null || resourceManager.IsInitialLoadReady)
        && !RenderWorkScheduler.HasPendingInitialWork;

    /// <summary>
    /// Flag blocking camera-triggered loading between session:reset and session:reload.
    /// The UI sends session:reset immediately before requesting a new session, so during this interval
    /// the backend is between destroying the previous city and initializing the new one. Loading here
    /// would ingest incomplete terrain and objects; subsequent version-based reloads would skip tiles with the same version,
    /// leaving old geometry. Clear on the load completion notification (session:reload).
    /// </summary>
    private bool loadSuspendedUntilReload = false;

    // Set a minimum camera-triggered issue interval to avoid repeatedly canceling and restarting batches while moving.
    // Issue the first change immediately after the interval; Update issues only the latest notification received within it.
    private const float CAMERA_BATCH_MIN_INTERVAL = 0.25f;
    private float nextCameraBatchTime;
    private bool hasDeferredCameraCell;
    private Vector2Int deferredCameraCell;
    
    // BaseApiDataManager manages loadedCells, so history tracking here is unnecessary,
    // though it could be retained to compare against the previous camera movement.
    // Strictly, changing CenterCell changes the range, so checking CenterCell suffices.

    void Start()
    {
        if (MasterDataStore.Instance != null)
        {
            MasterDataStore.Instance.OnInitialized += OnMasterDataInitialized;
            if (MasterDataStore.Instance.IsInitialized)
            {
                OnMasterDataInitialized();
            }
        }
    }

    private void OnMasterDataInitialized()
    {
        if (GameStateManager.Instance.IsGameStarted())
        {
            // Fetch coastline, water surfaces, and rivers before starting cell loads.
            // Port quay mesh checks and river crossing checks for paths being placed need them before buildings and roads.
            RenderWorkScheduler.Start(this, FetchCoastlineThenLoad(), "CellDataLoader");
        }
        else
        {
            Debug.Log("CellDataLoader: MasterDataStore initialized. Game is not started.");
        }
    }

    private IEnumerator FetchCoastlineThenLoad()
    {
        CityDataCache.Ensure().RequestSync();
        if (terrainManager != null)
        {
            yield return terrainManager.FetchCoastline();
        }
        OnCameraCellChanged(new Vector2Int(0, -1));
    }
    
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        targetScene = gameObject.scene;
        Debug.Log($"CellDataLoader: ターゲットシーンを '{targetScene.name}' に設定しました");

        OptimizeHttpSettings();

        if (buildingManager == null) buildingManager = FindAnyObjectByType<BuildingManager>();
        if (parcelManager == null) parcelManager = FindAnyObjectByType<ParcelManager>();
        if (pathManager == null) pathManager = FindAnyObjectByType<PathManager>();
        if (zoneManager == null) zoneManager = FindAnyObjectByType<ZoneManager>();
        if (terrainManager == null) terrainManager = FindAnyObjectByType<TerrainManager>();
        if (resourceManager == null) resourceManager = FindAnyObjectByType<ResourceManager>();
        if (resourceManager == null)
        {
            GameObject go = new GameObject("ResourceManager");
            DontDestroyOnLoad(go);
            resourceManager = go.AddComponent<ResourceManager>();
        }

        if (propManager == null) propManager = FindAnyObjectByType<PropManager>();
        if (propManager == null)
        {
            GameObject go = new GameObject("PropManager");
            DontDestroyOnLoad(go);
            propManager = go.AddComponent<PropManager>();
        }

        interchangeManager = FindAnyObjectByType<InterchangeManager>();
        if (interchangeManager == null)
        {
            GameObject go = new GameObject("InterchangeManager");
            DontDestroyOnLoad(go);
            interchangeManager = go.AddComponent<InterchangeManager>();
        }

        roadManager = FindAnyObjectByType<RoadManager>();
        if (roadManager == null)
        {
            GameObject go = new GameObject("RoadManager");
            DontDestroyOnLoad(go);
            roadManager = go.AddComponent<RoadManager>();
        }

        cityOverviewManager = FindAnyObjectByType<CityOverviewManager>();
        if (cityOverviewManager == null)
        {
            GameObject go = new GameObject("CityOverviewManager");
            DontDestroyOnLoad(go);
            cityOverviewManager = go.AddComponent<CityOverviewManager>();
        }
        cityOverviewManager.Initialize(terrainManager, buildingManager, pathManager);

        // Initialize HeightmapTextureManager.
        // Initialize references so the GPU height window can subscribe to terrain tile completion notifications.
        if (heightmapTextureManager == null) heightmapTextureManager = FindAnyObjectByType<HeightmapTextureManager>();
        if (heightmapTextureManager == null)
        {
            GameObject go = new GameObject("HeightmapTextureManager");
            DontDestroyOnLoad(go);
            heightmapTextureManager = go.AddComponent<HeightmapTextureManager>();
        }
        if (terrainManager != null)
        {
            heightmapTextureManager.Initialize(terrainManager);
        }

        if (terrainManager != null)
        {
            terrainManager.OnTerrainOverviewReady += OnTerrainOverviewReady;
            terrainManager.OnAllTerrainLodLoaded += OnTerrainAllLoaded;
        }
    }

    private void OnDestroy()
    {
        if (MasterDataStore.Instance != null)
        {
            MasterDataStore.Instance.OnInitialized -= OnMasterDataInitialized;
        }
        if (terrainManager != null)
        {
            terrainManager.OnTerrainOverviewReady -= OnTerrainOverviewReady;
            terrainManager.OnAllTerrainLodLoaded -= OnTerrainAllLoaded;
        }
    }

    private void OptimizeHttpSettings()
    {
        try
        {
            System.Net.ServicePointManager.DefaultConnectionLimit = 50;
            System.Net.ServicePointManager.Expect100Continue = false;
            System.Net.ServicePointManager.UseNagleAlgorithm = false;
            System.Net.ServicePointManager.MaxServicePointIdleTime = 30000;
            System.Net.ServicePointManager.DnsRefreshTimeout = 60000;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"CellDataLoader: HTTP設定最適化で警告: {ex.Message}");
        }
    }

    /// <summary>
    /// Called externally when the camera center cell changes
    /// </summary>
    public void OnCameraCellChanged(Vector2Int newCenterCellIndex)
    {
        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"OnCameraCellChanged: start {newCenterCellIndex}, current {currentCenterCell}");

        // Do nothing before the game starts
        if (!GameStateManager.Instance.IsGameStarted())
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "ゲームが開始されていないため、OnCameraCellChangedをスキップします。");
            return;
        }

        // Do not load during session switching (while the backend prepares the new city)
        if (loadSuspendedUntilReload)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "セッション切り替え中のため、OnCameraCellChangedをスキップします。");
            return;
        }

        // Wait if MasterDataStore is not initialized
        if (MasterDataStore.Instance != null && !MasterDataStore.Instance.IsInitialized)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "MasterDataStoreが未初期化のため、OnCameraCellChangedをスキップします。");
            return;
        }

        // Update the center cell even while coalescing, so ReloadAllData creates sets from the latest viewpoint
        // and a later coalesced issue uses the same sets without canceling the refetch.
        currentCenterCell = newCenterCellIndex;
        if (Time.unscaledTime < nextCameraBatchTime)
        {
            deferredCameraCell = newCenterCellIndex;
            hasDeferredCameraCell = true;
            RenderWorkScheduler.CountEvent("camera.batchDeferred");
            return;
        }
        hasDeferredCameraCell = false;

        LodCellSets cellSets = CalculateLodCellSets();
        HashSet<Vector2Int> tierChangedCells = CalculateTierChangedCells(cellSets, isForce: false);
        if (tierChangedCells.Count == 0)
        {
            RenderWorkScheduler.CountEvent("camera.sameCells");
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "CellDataLoader: セルティア変更がないため、3D更新をスキップします。");
            return;
        }

        if (lastTierChangeDescriptions.Count > 0)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"CellDataLoader: ティア変更セル: {string.Join(", ", lastTierChangeDescriptions)}");
        }

        UpdateManagers(cellSets, tierChangedCells, isForce: false);
        nextCameraBatchTime = Time.unscaledTime + CAMERA_BATCH_MIN_INTERVAL;

        // The visible range changed, so resolve alert icon heights again. Terrain and buildings in newly resident cells
        // are not loaded yet; align final heights again on building batch application (BuildingManager.ApplyLodCellChanges).
        AlertManager.Instance?.RefreshPositions();
    }

    private void Update()
    {
        // Recalculate sets from the camera at issue time, ensuring the final viewpoint is evaluated once after the camera stops.
        if (hasDeferredCameraCell && Time.unscaledTime >= nextCameraBatchTime)
        {
            hasDeferredCameraCell = false;
            OnCameraCellChanged(deferredCameraCell);
        }
    }

    /// <summary>
    /// Calculate cell sets per LOD tier around the current center cell.
    /// Account for the camera's position within its cell and include adjacent cells as Dynamic near cell boundaries.
    /// </summary>
    private LodCellSets CalculateLodCellSets()
    {
        float cellSize = TerrainManager.CellSize;
        int mapWidth = terrainManager != null ? terrainManager.GetMapWidth() : 10000;
        int mapHeight = terrainManager != null ? terrainManager.GetMapHeight() : 10000;

        Vector3 viewCenterPosition = CameraCellChecker.Instance != null
            ? CameraCellChecker.Instance.CurrentWorldPosition
            : Vector3.zero;

        Vector3 cameraPosition = Camera.main != null
            ? Camera.main.transform.position
            : Vector3.zero;

        Vector3 cameraForward = Camera.main != null
            ? Camera.main.transform.forward
            : Vector3.forward;

        return LodConfig.CalculateCellSets(currentCenterCell, viewCenterPosition, cameraPosition, cameraForward, terrainManager, cellSize, mapWidth, mapHeight, currentCellTiers.Keys);
    }

    private HashSet<Vector2Int> CalculateTierChangedCells(LodCellSets cellSets, bool isForce)
    {
        Dictionary<Vector2Int, LodTier> nextTiers = BuildVisibleCellTierMap(cellSets);
        HashSet<Vector2Int> changedCells = new HashSet<Vector2Int>();
        lastTierChangeDescriptions.Clear();

        foreach (var kvp in nextTiers)
        {
            if (isForce || !currentCellTiers.TryGetValue(kvp.Key, out LodTier currentTier) || currentTier != kvp.Value)
            {
                changedCells.Add(kvp.Key);
                LodTier previousTier = currentCellTiers.TryGetValue(kvp.Key, out LodTier oldTier) ? oldTier : LodTier.UltraFar;
                lastTierChangeDescriptions.Add($"{FormatCell(kvp.Key)}({FormatTier(previousTier)}->{FormatTier(kvp.Value)})");
            }
        }

        foreach (var kvp in currentCellTiers)
        {
            if (!nextTiers.ContainsKey(kvp.Key))
            {
                changedCells.Add(kvp.Key);
                lastTierChangeDescriptions.Add($"{FormatCell(kvp.Key)}({FormatTier(kvp.Value)}->{FormatTier(LodTier.UltraFar)})");
            }
        }

        currentCellTiers = nextTiers;
        return changedCells;
    }

    private string FormatCell(Vector2Int cell)
    {
        return $"セル({cell.x},{cell.y})";
    }

    private string FormatTier(LodTier tier)
    {
        return tier.ToString().ToLowerInvariant();
    }

    private Dictionary<Vector2Int, LodTier> BuildVisibleCellTierMap(LodCellSets cellSets)
    {
        Dictionary<Vector2Int, LodTier> tiers = new Dictionary<Vector2Int, LodTier>();

        AddCellsAtTier(tiers, cellSets.farCells, LodTier.Far);
        AddCellsAtTier(tiers, cellSets.nearCells, LodTier.Near);
        AddCellsAtTier(tiers, cellSets.dynamicCells, LodTier.Dynamic);

        return tiers;
    }

    private void AddCellsAtTier(Dictionary<Vector2Int, LodTier> tiers, List<Vector2Int> cells, LodTier tier)
    {
        if (cells == null) return;

        foreach (var cell in cells)
        {
            if (!tiers.TryGetValue(cell, out LodTier current) || tier > current)
            {
                tiers[cell] = tier;
            }
        }
    }

    /// <summary>
    /// Instruct managers to load by LOD tier.
    /// Start fetching terrain tiles, then begin fetching buildings, roads, etc. once overview terrain (grounding heights where tiles are absent) is ready.
    /// Run alongside detail terrain tile loading; managers align objects through OnTerrainTileReady as cell tiles arrive.
    /// Render buildings and roads as Far until tiles for resident cells they touch or cross arrive (avoiding detail rebuilds immediately after overview-based generation).
    ///
    /// When forceManagerUpdate is true, run incremental updates for all managers
    /// even without camera movement or tier changes.
    /// Used by ReloadAllData after backend changes such as road, building, or district creation.
    /// </summary>
    private void UpdateManagers(LodCellSets cellSets, bool isForce, bool forceManagerUpdate = false)
    {
        HashSet<Vector2Int> tierChangedCells = CalculateTierChangedCells(cellSets, isForce);
        UpdateManagers(cellSets, tierChangedCells, isForce, forceManagerUpdate);
    }

    private void UpdateManagers(LodCellSets cellSets, HashSet<Vector2Int> tierChangedCells, bool isForce, bool forceManagerUpdate = false)
    {
        if (cellSets.farCells == null) return;

        if (!forceManagerUpdate && !isForce && (tierChangedCells == null || tierChangedCells.Count == 0))
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "CellDataLoader: セルティア変更がないため、マネージャー更新をスキップします。");
            return;
        }

        // Issue a new load batch and cooperatively cancel the previous in-flight batch.
        // StopCoroutine stops each manager's parent coroutine, but independently started child work
        // survives it, so children check the batch IsCanceled flag and stop themselves.
        if (activeLoadBatch != null)
        {
            activeLoadBatch.Cancel();
        }
        activeLoadBatch = new LoadBatch(cellSets, isForce);
        RenderWorkScheduler.CountEvent("camera.loadBatches");
        RenderWorkScheduler.CountEvent("camera.changedCells", tierChangedCells?.Count ?? 0);

        LoadBatch objectBatch = activeLoadBatch;
        pendingObjectLoad = () => RenderWorkScheduler.Start(this, LoadObjectManagers(cellSets, tierChangedCells, isForce, objectBatch), "CellDataLoader");
        if (terrainManager != null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "CellDataLoader: 地形のリロード開始");
            terrainManager.LoadLodCells(cellSets, null, isForce, activeLoadBatch);
        }
        else
        {
            overviewReadyBatch = activeLoadBatch;
            StartPendingObjectLoad();
        }
    }

    /// <summary>Object load waiting for overview terrain. Replaced by new batches; never launch work for old batches.</summary>
    private System.Action pendingObjectLoad;
    /// <summary>Load batch whose overview terrain is ready. Begin object loading only once this is the current batch.</summary>
    private LoadBatch overviewReadyBatch;
    private Coroutine objectLoadWaiter;

    private void OnTerrainOverviewReady()
    {
        overviewReadyBatch = activeLoadBatch;
        StartPendingObjectLoad();
        cityOverviewManager?.OnTerrainOverviewReady();
    }

    private void StartPendingObjectLoad()
    {
        if (pendingObjectLoad == null || objectLoadWaiter != null) return;
        objectLoadWaiter = RenderWorkScheduler.Start(this, StartObjectLoadWhenSettled(), "CellDataLoader");
    }

    /// <summary>
    /// Start object loading once the current batch's overview terrain is ready and the camera stops. While moving, batches are canceled by successors at each throttle interval;
    /// fetching and discarding them would congest the core and delay terrain tile responses too.
    /// The citywide overview fills the resident area meanwhile.
    /// </summary>
    private IEnumerator StartObjectLoadWhenSettled()
    {
        try
        {
            while (pendingObjectLoad != null
                && (overviewReadyBatch != activeLoadBatch || hasDeferredCameraCell || Time.unscaledTime < nextCameraBatchTime))
                yield return null;
        }
        finally { objectLoadWaiter = null; }
        var loadObjects = pendingObjectLoad;
        pendingObjectLoad = null;
        loadObjects?.Invoke();
    }

    private IEnumerator LoadObjectManagers(LodCellSets cellSets, HashSet<Vector2Int> tierChangedCells, bool isForce, LoadBatch batch)
    {
        if (batch.IsCanceled) yield break;
        batch.Trace("OBJECTS_START");
        // Start objects that do not use parcels without waiting for parcel fetching.
        if (pathManager != null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "CellDataLoader: 道路のリロード開始");
            pathManager.LoadLodCells(cellSets, tierChangedCells, isForce, batch);
        }
        if (zoneManager != null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "CellDataLoader: 地区のリロード開始");
            zoneManager.LoadLodCells(cellSets, isForce);
        }
        // Interchanges are a citywide list; fetch only if not already available (later updates use ReloadAllData).
        interchangeManager?.EnsureLoaded();
        roadManager?.EnsureLoaded();
        if (resourceManager != null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "CellDataLoader: 天然資源のリロード開始");
            resourceManager.LoadLodCells(cellSets, isForce);
        }
        if (propManager != null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "CellDataLoader: プロップのリロード開始");
            propManager.LoadLodCells(cellSets, tierChangedCells, isForce, batch);
        }
        // Buildings use water-parcel reference elevations and parcel geometry (reuse keys), so wait until parcels are applied.
        if (parcelManager != null)
        {
            parcelManager.LoadLodCells(cellSets, isForce);
            while (parcelManager.IsLoading)
            {
                if (batch.IsCanceled) yield break;
                yield return null;
            }
        }
        if (batch.IsCanceled) yield break;
        batch.Trace("PARCEL_DONE / BUILDINGS_START");
        if (buildingManager != null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "CellDataLoader: 建物のリロード開始");
            buildingManager.LoadLodCells(cellSets, tierChangedCells, isForce, batch);
        }
    }

    private void OnTerrainAllLoaded(IReadOnlyList<Vector2Int> updatedCells)
    {
        activeLoadBatch?.Trace($"TERRAIN_DONE updated={updatedCells?.Count ?? 0}");
        // Align icons that still used overview heights during building batch application to the completed terrain.
        AlertManager.Instance?.RefreshPositions();
        if (isInitialLoadComplete) return;
        isInitialLoadComplete = true;
        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "CellDataLoader: 初回ロード完了");
    }

    /// <summary>
    /// Force updates of current cell data (such as Button OnClick).
    /// Destroy all objects before reloading.
    /// </summary>
    public void ForceUpdateCurrentCells()
    {
        ForceReloadAllData();
    }

    /// <summary>Incremental update waiting for the city cache to synchronize. Stopped by ClearAllManagerData.</summary>
    private Coroutine reloadAfterSync;

    /// <summary>
    /// Apply incremental updates after backend data changes.
    /// Called after committing road, building, or district creation, epoch completion, and session:reload, for example.
    /// First synchronize the city cache with the core, then run incremental updates for all managers even if the camera has not moved
    /// (marking cells stale before synchronization ends would let a camera batch clear the marks with old data).
    /// Each manager compares versions to skip redrawing unchanged objects.
    /// </summary>
    public void ReloadAllData()
    {
        Debug.Log("CellDataLoader: ReloadAllData invoked.");
        loadSuspendedUntilReload = false;
        int ticket = CityDataCache.Ensure().RequestSync();
        if (reloadAfterSync != null) RenderWorkScheduler.Stop(this, reloadAfterSync);
        reloadAfterSync = RenderWorkScheduler.Start(this, ReloadAfterSync(ticket), "CellDataLoader", () => false);
    }

    private IEnumerator ReloadAfterSync(int ticket)
    {
        while (!CityDataCache.Instance.IsSyncedThrough(ticket)) yield return null;
        reloadAfterSync = null;
        ApplyReload();
    }

    private void ApplyReload()
    {
        PropManager.Instance?.InvalidateForestOverview();
        // Record refetch signals before issuing the batch, ensuring the next batch refetches even if subsequent batches are canceled.
        terrainManager?.InvalidateCellVersions();
        buildingManager?.MarkLoadedCellsStale();
        parcelManager?.MarkLoadedCellsStale();
        cityOverviewManager?.RequestRefresh();
        RopewayNetworkStore.Instance?.RequestRefresh();
        TransitDataStore.Instance?.Refresh();
        if (currentCenterCell.x != int.MinValue && currentCenterCell.y != int.MinValue)
        {
            LodCellSets cellSets = CalculateLodCellSets();
            UpdateManagers(cellSets, isForce: false, forceManagerUpdate: true);
        }
        else
        {
            // Immediately after session switching (after ClearAllManagerData resets the current cell),
            // there is nothing to update incrementally. Reset viewpoint detection so the next
            // CameraCellChecker check calls OnCameraCellChanged and starts a full load even if the camera remains stationary.
            CameraCellChecker.Instance?.ResetViewState();
        }
        // Districts use full snapshots (ZoneManager.LoadCells refetches only when unloaded or forced),
        // so the isForce=false path above does not reload them. To follow automatic district creation,
        // merging, splitting, and polygon changes each epoch, explicitly request
        // a version-based reload here (unchanged districts skip redrawing).
        zoneManager?.RequestZoneRefresh();
        // The backend redetects interchanges when roads complete or are edited, so explicitly refetch them too
        // (labels are not rebuilt if versions match).
        interchangeManager?.RequestRefresh();
        // Refetch road-name labels too, since section completion, removal, and splitting change their positions and membership.
        roadManager?.RequestRefresh();
        // Update alert icons alongside data refetches after epoch completion or committed edits.
        // Cooldown throttles repeated calls in a short period.
        AlertManager.Instance?.Refresh();
    }

    /// <summary>
    /// Completely clear and refetch all data.
    /// Refetch coastline, water surfaces, and rivers as at session start before calculating LOD.
    /// </summary>
    public void ForceReloadAllData()
    {
        Debug.Log("CellDataLoader: ForceReloadAllData invoked. Will clear all objects and reload.");
        if (currentCenterCell.x != int.MinValue && currentCenterCell.y != int.MinValue)
        {

            RenderWorkScheduler.Start(this, ForceReloadAllDataCoroutine(), "CellDataLoader");
        }
    }

    private IEnumerator ForceReloadAllDataCoroutine()
    {
        ClearAllManagerData();
        CityDataCache.Ensure().RequestSync();

        // ClearAllManagerData empties coastline, water surface, river, and reclaimed land stores too, so
        // refetch before generating buildings and roads (used for port quay checks, river crossings, and grounding inside bands).
        if (terrainManager != null)
        {
            yield return terrainManager.FetchCoastline();
        }

        LodCellSets cellSets = CalculateLodCellSets();
        UpdateManagers(cellSets, isForce: false);

        // Update alert icons after the full refetch completes.
        AlertManager.Instance?.Refresh(forceRefresh: true);
    }

    /// <summary>
    /// For session:reset. Clear all data and block camera-triggered loading
    /// until session:reload arrives (avoid ingesting incomplete data while the backend prepares the new city).
    /// </summary>
    public void ClearAllManagerDataForSessionReset()
    {
        ClearAllManagerData();
        loadSuspendedUntilReload = true;
    }

    /// <summary>
    /// Completely clear every manager's data, internal state caches, and stores (coastline, water surfaces, rivers, reclaimed land, lines, alerts, etc.).
    /// Sessions are loaded, unloaded, and switched within one scene, so
    /// the previous session's camera cell (currentCenterCell), LOD cache (currentCellTiers),
    /// and streaming batch state must all return to their initial states.
    /// </summary>
    public void ClearAllManagerData()
    {
        pendingObjectLoad = null;
        overviewReadyBatch = null;
        // Cancel the running data fetch batch
        if (activeLoadBatch != null)
        {
            activeLoadBatch.Cancel();
            activeLoadBatch = null;
        }

        // Clear each data manager
        buildingManager?.ClearAllCells();
        pathManager?.ClearAllCells();
        parcelManager?.ClearAllCells();
        zoneManager?.ClearAllCells();
        terrainManager?.ClearAllCells();
        resourceManager?.ClearAllCells();
        propManager?.ClearAllCells();
        interchangeManager?.Clear();
        roadManager?.Clear();
        cityOverviewManager?.Clear();
        CityDataCache.Ensure().Clear();
        if (reloadAfterSync != null) RenderWorkScheduler.Stop(this, reloadAfterSync);
        reloadAfterSync = null;

        // Invalidate the GPU height window and per-frame terrain height cache.
        // Otherwise, while loading the next save, Utils.GetGroundHeight would return
        // elevations from the previous map, causing buildings, roads, and path splines
        // generated before terrain tiles arrive to use incorrect heights.
        if (heightmapTextureManager != null)
        {
            heightmapTextureManager.Invalidate();
        }
        Utils.InvalidateHeightCache();

        // Clear internal state variables
        currentCellTiers.Clear();
        currentCenterCell = new Vector2Int(int.MinValue, int.MinValue);
        isInitialLoadComplete = false;
        loadSuspendedUntilReload = false;
        hasDeferredCameraCell = false;
        nextCameraBatchTime = 0f;

        // Reset viewpoint detection too. If the camera stays stationary after resetting the current cell,
        // no viewpoint change would be detected and OnCameraCellChanged would never run again (only the sea would be visible).
        CameraCellChecker.Instance?.ResetViewState();

        // Clear static stores and other management objects
        CoastlineStore.Clear();
        if (WaterSurfaceManager.Instance != null)
        {
            WaterSurfaceManager.Instance.Clear();
        }
        if (RiverSurfaceManager.Instance != null)
        {
            RiverSurfaceManager.Instance.Clear();
        }
        else
        {
            RiverStore.Clear();
        }
        // The reclaimed land store affects grounding (crest height inside bands), so empty it before the next city's terrain.
        if (ReclaimedLandManager.Instance != null)
        {
            ReclaimedLandManager.Instance.Clear();
        }
        else
        {
            ReclaimedLandStore.Clear();
        }
        if (TransitOverlayManager.Instance != null)
        {
            TransitOverlayManager.Instance.Clear();
        }
        if (AlertManager.Instance != null)
        {
            AlertManager.Instance.ClearAll();
        }
    }

    public void FetchBuilding(int agentId, bool playExplosion = false)
    {
        if (buildingManager != null)
            RenderWorkScheduler.Start(this, buildingManager.FetchBuilding(agentId, playExplosion), "CellDataLoader");
    }

    public void FetchPath(int pathId)
    {
        if (pathManager != null)
            RenderWorkScheduler.Start(this, pathManager.FetchPath(pathId), "CellDataLoader");
    }

    public void FetchParcel(int parcelId)
    {
        if (parcelManager != null)
            RenderWorkScheduler.Start(this, parcelManager.FetchParcel(parcelId), "CellDataLoader");
    }

    public void FetchZone(int zoneId)
    {
        if (zoneManager != null)
            RenderWorkScheduler.Start(this, zoneManager.FetchZone(zoneId), "CellDataLoader");
    }
}
