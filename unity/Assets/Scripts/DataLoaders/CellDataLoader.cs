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

    // オブジェクトを作成するシーン
    private UnityEngine.SceneManagement.Scene targetScene;

    private Vector2Int currentCenterCell = new Vector2Int(int.MinValue, int.MinValue);

    private Dictionary<Vector2Int, LodTier> currentCellTiers = new Dictionary<Vector2Int, LodTier>();
    private List<string> lastTierChangeDescriptions = new List<string>();
    private LodCellSets pendingCellSets;

    /// <summary>
    /// 現在進行中のロードバッチ。UpdateManagers ごとに新しいバッチを発行し、旧バッチは Cancel する。
    /// StopCoroutine では停止できない子の処理は、このバッチの IsCanceled を確認して協調的に停止する。
    /// 地形タイルのジョブと背景地形は寿命をセルの常駐とセッションで決めるので、バッチの取消しでは止まらない。
    /// </summary>
    private LoadBatch activeLoadBatch;

    // TerrainManager.OnAllTerrainLodLoaded 発火時に建物/道路の追従更新に使うパラメータ
    private bool pendingIsForce = false;

    /// <summary>
    /// 地形追従更新(建物・道路のメッシュ/高さ再計算)を複数フレームに分散するコルーチン。
    /// カメラが動いて新たなロードが始まったら(UpdateManagers冒頭で)キャンセルし、仕切り直す。
    /// </summary>
    private Coroutine terrainFollowUpCoroutine;

    /// <summary>
    /// 地形追従更新が必要だが、まだ処理し終えていないセルの集合。
    /// OnTerrainAllLoaded で受け取った「実際に更新されたセル」をここへ統合し、
    /// 追従コルーチンが正常完了したセルだけを取り除く。
    /// コルーチンがカメラ移動でキャンセルされても、取りこぼしたセルは次回完了時に再処理される。
    /// </summary>
    private HashSet<Vector2Int> pendingFollowUpCells = new HashSet<Vector2Int>();

    /// <summary>
    /// 初回ロードが完了したかどうか。
    /// ゲーム開始後の最初のセルロードが完了すると <c>true</c> になり、
    /// それ以降のカメラ移動によるロードではカメラ移動をロックしない。
    /// </summary>
    private bool isInitialLoadComplete = false;
    public bool IsInitialLoadComplete => isInitialLoadComplete;
    /// <summary>
    /// 初期シーンの表示準備が整ったか。間引きで保留中のカメラバッチ(hasDeferredCameraCell)も未完了として数える。
    /// 保留中は pendingObjectLoad が空のままなので、数えないと最終視点のバッチ発行前に準備完了と判定されるため。
    /// </summary>
    public bool IsInitialSceneReady => isInitialLoadComplete
        && !loadSuspendedUntilReload && !hasDeferredCameraCell && pendingObjectLoad == null
        && terrainFollowUpCoroutine == null && pendingFollowUpCells.Count == 0
        && (resourceManager == null || resourceManager.IsInitialLoadReady)
        && !RenderWorkScheduler.HasPendingInitialWork;

    /// <summary>
    /// session:reset から session:reload までの間、カメラ由来のロードを止めるフラグ。
    /// UI は新しいセッションの作成要求を出す直前に session:reset を送るため、この間の
    /// バックエンドは前の都市の破棄〜新しい都市の初期化の途中にある。ここでロードすると
    /// 中途半端な地形・オブジェクトを取り込み、後続の version 差分リロードでは同じ version の
    /// タイルが更新対象にならず古い形状が残る。ロード完了通知(session:reload)で解除する。
    /// </summary>
    private bool loadSuspendedUntilReload = false;

    // 移動中に旧バッチの取消しとやり直しが連続しないよう、カメラ由来の発行間隔の下限を置く。
    // 間隔が空いた後の最初の変化は即時に発行し、間隔内の通知は最後の1件だけを Update で発行する。
    private const float CAMERA_BATCH_MIN_INTERVAL = 0.25f;
    private float nextCameraBatchTime;
    private bool hasDeferredCameraCell;
    private Vector2Int deferredCameraCell;
    
    // BaseApiDataManagerがloadedCellsを管理するため、ここでの履歴管理は不要になるが、
    // カメラ移動時の「前回と同じか」判定のために保持してもよい。
    // ただし、厳密にはCenterCellが変われば範囲も変わるため、CenterCellのチェックで十分。

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
            // 海岸線・水面・川を先に取得してからセルロードを開始する。
            // 港湾の岸壁メッシュ判定や敷設中パスの川横断判定が、建物・道路より先に必要とする。
            RenderWorkScheduler.Start(this, FetchCoastlineThenLoad(), "CellDataLoader");
        }
        else
        {
            Debug.Log("CellDataLoader: MasterDataStore initialized. Game is not started.");
        }
    }

    private IEnumerator FetchCoastlineThenLoad()
    {
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

        // HeightmapTextureManager の初期化。
        // GPU高さ窓が地形タイルの完成通知を購読できるよう、参照を初期化する。
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

        // 地形ロード完了時に建物/道路の追従更新を行うための購読
        if (terrainManager != null)
        {
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
    /// 外部からカメラの中心セルが変わった時に呼ばれる
    /// </summary>
    public void OnCameraCellChanged(Vector2Int newCenterCellIndex)
    {
        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"OnCameraCellChanged: start {newCenterCellIndex}, current {currentCenterCell}");

        // ゲームが開始されていない場合は何もしない
        if (!GameStateManager.Instance.IsGameStarted())
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "ゲームが開始されていないため、OnCameraCellChangedをスキップします。");
            return;
        }

        // セッション切り替え中(バックエンドが新しい都市を準備している間)はロードしない
        if (loadSuspendedUntilReload)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "セッション切り替え中のため、OnCameraCellChangedをスキップします。");
            return;
        }

        // MasterDataStoreが未初期化の場合は待機
        if (MasterDataStore.Instance != null && !MasterDataStore.Instance.IsInitialized)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "MasterDataStoreが未初期化のため、OnCameraCellChangedをスキップします。");
            return;
        }

        // 中心セルは合流中も更新する。間に ReloadAllData が来たとき最新の視点で集合を作り、
        // 後で発行する合流分が同じ集合になって再取得を取り消さないようにするため。
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

        // 表示範囲が変わったため、アラートアイコンの高さを解決し直す。この時点では新しい常駐セルの地形・建物は
        // 未ロードなので、最終的な高さは建物バッチの適用時(BuildingManager.ApplyLodCellChanges)に再度合わせる。
        AlertManager.Instance?.RefreshPositions();
    }

    private void Update()
    {
        // 集合は発行時点のカメラから計算し直すので、カメラが止まった後の最後の視点も必ず1回評価される。
        if (hasDeferredCameraCell && Time.unscaledTime >= nextCameraBatchTime)
        {
            hasDeferredCameraCell = false;
            OnCameraCellChanged(deferredCameraCell);
        }
    }

    /// <summary>
    /// 現在の中心セルを基準にLODティア別のセルセットを計算する。
    /// カメラのセル内相対位置を考慮し、セル境界付近では隣接セルも超近景（Dynamic）として含める。
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
    /// マネージャー群に対してLODティア別にロード指示を出す。
    /// 1. 建物/道路/区画/地区を先にロードする。
    /// 2. その後に地形(near/far/dynamic)をロードする。
    /// 3. 地形ロード完了後、dynamic地形に更新があったセルに対して建物のY座標調整と道路形状更新を行う。
    ///
    /// forceManagerUpdate が true の場合、カメラが動かずティア変更がなくても
    /// 全マネージャーの差分更新を実行する。
    /// 道路作成・建物作成・地区作成などのバックエンドデータ変更後に呼ばれる ReloadAllData で利用する。
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

        pendingCellSets = cellSets;
        pendingIsForce = isForce;
        if (isForce) pathManager?.ClearTerrainFollowProgress();

        // 新しいロードバッチを発行し、進行中の旧バッチを協調キャンセルする。
        // 各マネージャーの親コルーチンは StopCoroutine で停止されるが、別に起動された子の処理は
        // それでは止まらないため、バッチの IsCanceled を見て自発的に停止する。
        if (activeLoadBatch != null)
        {
            activeLoadBatch.Cancel();
        }
        activeLoadBatch = new LoadBatch(cellSets, isForce);
        RenderWorkScheduler.CountEvent("camera.loadBatches");
        RenderWorkScheduler.CountEvent("camera.changedCells", tierChangedCells?.Count ?? 0);

        // 進行中の地形追従コルーチンがあれば停止する。
        // これから新しい地形をロードするため、古い地形に対する追従処理は破棄して仕切り直す。
        // 取りこぼしたセルは pendingFollowUpCells に残るため、次回完了時に再処理される。
        if (terrainFollowUpCoroutine != null)
        {
            RenderWorkScheduler.CountEvent("terrainFollow.interrupted");
            activeLoadBatch.Trace($"FOLLOWUP_RESTART pendingCells={pendingFollowUpCells.Count}");
            RenderWorkScheduler.Stop(this, terrainFollowUpCoroutine);
            terrainFollowUpCoroutine = null;
        }

        LoadBatch objectBatch = activeLoadBatch;
        pendingObjectLoad = () => RenderWorkScheduler.Start(this, LoadObjectManagers(cellSets, tierChangedCells, isForce, objectBatch), "CellDataLoader");
        if (terrainManager != null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "CellDataLoader: 地形のリロード開始");
            if (pathManager != null)
            {
                pathManager.BeginDeferredTerrainTilePathRebuilds();
            }
            if (buildingManager != null)
            {
                buildingManager.BeginDeferredTerrainTileBuildingRebuilds();
            }
            terrainManager.LoadLodCells(cellSets, null, isForce, activeLoadBatch);
        }
        else { pendingObjectLoad?.Invoke(); pendingObjectLoad = null; }
    }

    /// <summary>
    /// TerrainManager が全セルのロードを完了した時に呼ばれる。
    /// 引数 <paramref name="updatedCells"/> は「このロードバッチで実際に地形タイルが
    /// (再)生成・再縫合されたセル」の一覧。これらのセルだけを対象に、建物のY座標調整・
    /// メッシュ再生成、道路の地形追従形状更新、プロップのY座標調整を行う。
    /// 地形が変化していないセルには一切触れない。
    ///
    /// 追従更新は <see cref="TerrainFollowUpCoroutine"/> で複数フレームに分散し、
    /// メッシュ再生成の集中によるフレームヒッチ(カメラのカクつき)を防ぐ。
    /// </summary>
    private System.Action pendingObjectLoad;

    private IEnumerator LoadObjectManagers(LodCellSets cellSets, HashSet<Vector2Int> tierChangedCells, bool isForce, LoadBatch batch)
    {
        if (batch.IsCanceled) yield break;
        batch.Trace("PARCEL_START");
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
        batch.Trace("PARCEL_DONE / OBJECTS_START");
        // 1. 建物, 道路, 区画, 地区のリロード
        if (buildingManager != null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "CellDataLoader: 建物のリロード開始");
            buildingManager.LoadLodCells(cellSets, tierChangedCells, isForce, batch);
        }
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
        // インターチェンジは都市全体の一覧なので、未取得のときだけ取る(以後の更新は ReloadAllData)。
        interchangeManager?.EnsureLoaded();
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
        // 2/3. 地形(near/far/dynamic)のリロード
    }

    private void CollectDeferredTerrainNotifications()
    {
        // 抑制中のタイル通知は追従開始前に統合する。完了後に回収すると同じセルを二度処理する。
        if (pathManager != null)
        {
            pendingFollowUpCells.UnionWith(pathManager.EndDeferredTerrainTilePathRebuilds());
            pathManager.BeginDeferredTerrainTilePathRebuilds();
        }
        if (buildingManager != null)
        {
            pendingFollowUpCells.UnionWith(buildingManager.EndDeferredTerrainTileBuildingRebuilds());
            buildingManager.BeginDeferredTerrainTileBuildingRebuilds();
        }
    }

    private void OnTerrainAllLoaded(IReadOnlyList<Vector2Int> updatedCells)
    {
        activeLoadBatch?.Trace($"TERRAIN_DONE updated={updatedCells?.Count ?? 0}");
        CollectDeferredTerrainNotifications();
        // 今回実際に更新されたセルを未処理集合へ統合する。
        // (前回コルーチンがキャンセルされて取りこぼしたセルがあれば、それと合流する)
        if (updatedCells != null)
        {
            for (int i = 0; i < updatedCells.Count; i++)
                pendingFollowUpCells.Add(updatedCells[i]);
        }

        // 進行中の追従コルーチンがあれば停止して仕切り直す(最新地形に対して処理し直す)。
        if (terrainFollowUpCoroutine != null)
        {
            RenderWorkScheduler.Stop(this, terrainFollowUpCoroutine);
            terrainFollowUpCoroutine = null;
        }

        // ForceReload系では差分情報を信用せず、全dynamicセルを対象に再生成する。
        bool followUpNeeded = pendingIsForce
            ? (pendingCellSets.dynamicCells != null && pendingCellSets.dynamicCells.Count > 0)
            : pendingFollowUpCells.Count > 0;

        if (!followUpNeeded)
        {
            // 追従対象なし(地形不変のセル跨ぎ)。後処理(deferred解除・カメラロック解除)だけ即時に行う。
            FinishTerrainFollowUp();
            return;
        }

        terrainFollowUpCoroutine = RenderWorkScheduler.Start(this, TerrainFollowUpCoroutine(), "CellDataLoader");
    }

    /// <summary>
    /// 地形追従更新(建物 → 道路 → プロップ)を複数フレームに分散して実行する。
    /// 各ステップはフレーム予算(<see cref="PerformanceConfig.SliceBudgetMs"/>)を超えると次フレームに譲る。
    /// 建物・道路は <see cref="Utils.GetGroundHeight"/> を参照するため、
    /// 完成したTerrainタイルだけを参照する。
    /// </summary>
    private IEnumerator TerrainFollowUpCoroutine()
    {
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, "[TIMING] CellDataLoader TerrainFollowUp START");

        // 今回地形タイルが(再)生成・再縫合された全セル。処理完了時に pending から取り除く単位。
        List<Vector2Int> followUpCells = new List<Vector2Int>(pendingFollowUpCells);

        // 建物・道路の追従対象 = 更新セル ∩ Nearティア以上のセル(dynamicCells ⊂ nearCells)。
        // 建物・道路は地形とHTTPの完了順を競争しながら地形より先に生成されるため、
        // Nearセルでも「古い高さ」のまま作られた個体が存在しうる。Dynamicセルに限定すると
        // Nearセルの道路が地形に埋まったまま誰にも直されない(ティアがDynamicに昇格するまで放置される)。
        // 一方、Farセルの簡易表示(白い箱・直線ストリップ)は遠距離で高さずれが視認できないため
        // 対象外とし、初回ロード時の追従コストを抑える。
        // 処理内容のティア分岐は各マネージャー側が持つ:
        //   建物: Dynamicセル=メッシュ再生成 / それ以外=Y座標調整 (BuildingManager)
        //   道路: セルの現在ティアで形状を再計算 (PathManager)
        var nearSet = pendingCellSets.nearCells != null
            ? new HashSet<Vector2Int>(pendingCellSets.nearCells)
            : new HashSet<Vector2Int>();
        var rebuildSet = new HashSet<Vector2Int>();
        foreach (var c in followUpCells)
        {
            if (nearSet.Contains(c)) rebuildSet.Add(c);
        }

        // ForceReload系では差分情報を信用せず、全dynamicセルもメッシュ再生成の対象に加える。
        if (pendingIsForce && pendingCellSets.dynamicCells != null)
        {
            foreach (var c in pendingCellSets.dynamicCells)
                rebuildSet.Add(c);
        }
        List<Vector2Int> meshRebuildCells = new List<Vector2Int>(rebuildSet);
        RenderWorkScheduler.CountEvent("terrainFollow.requestedCells", meshRebuildCells.Count);

        // 1.5. 道路のLOD一括ロードがまだ進行中なら、その完了を待ってから地形追従へ進む。
        //     地形と道路が同時にロードされる初回ロード(セーブ済みの都市を起動した瞬間など)では、
        //     道路の一括ロードが地形適用より先に走り、道路が「まだ標高0の地形」を参照して
        //     生成されることがある。このとき地形ロード完了で走る本コルーチンが一括ロードと
        //     並走すると、PathManager が「このバッチで生成済みの道路」を重複スキップし
        //     (TryRebuildPathOnce の lodBatchRebuiltPathIds ガード)、地形追従の再生成が
        //     握りつぶされて誤った高さ(地面の道路が浮く/高架が地面に落ちる)のまま固定される。
        //     一括ロードが終わってから追従すれば、地形タイルの適用は完了しているため
        //     すべての対象道路が正しい標高で再生成される。
        //     一括ロードはカメラ移動でキャンセルされてもフラグがリセットされるため、待機は有界。
        while (pathManager != null && pathManager.IsLodBatchActive)
        {
            yield return null;
        }

        // 2. 建物の地形追従(Dynamicセルはメッシュ再生成、それ以外はY座標調整)。
        //    StartCoroutine ではなく IEnumerator を直接 yield return することで、
        //    この追従コルーチンと同一実行コンテキストで実行する。これにより、
        //    親(terrainFollowUpCoroutine)を StopCoroutine した際に内側も確実に停止する
        //    (StartCoroutine で起動した子コルーチンは親停止では止まらないため)。
        if (buildingManager != null && meshRebuildCells.Count > 0)
        {
            yield return buildingManager.AdjustBuildingHeightsForCellsCoroutine(meshRebuildCells);
        }

        // 3. 道路の地形追従形状再生成。(同上の理由で IEnumerator を直接 yield return する)
        if (pathManager != null && meshRebuildCells.Count > 0)
        {
            yield return pathManager.RebuildPathShapesForCellsCoroutine(meshRebuildCells);
        }

        // 4. プロップ(装飾オブジェクト)のY座標追従。プロップは常に地表に貼り付くため
        //    メッシュ再生成は不要で、Y座標の再設定のみ全ティア共通で行う。
        //    (同上の理由で IEnumerator を直接 yield return する)
        if (propManager != null && meshRebuildCells.Count > 0)
        {
            yield return propManager.AdjustPropHeightsForCellsCoroutine(meshRebuildCells);
        }

        // 駅舎(連絡橋+建屋)は駅舎パスの生成物のため、地形追従は PathManager の
        // パス再生成(RebuildPathShapesForCellsCoroutine)に含まれる。個別の追従処理は持たない。

        // 正常完了: 処理したセルを未処理集合から取り除く。
        // 途中でキャンセルされた場合はここに到達しないため、pendingFollowUpCells に残った
        // セルが次回のロード完了時にまとめて再処理される。
        for (int i = 0; i < followUpCells.Count; i++)
            pendingFollowUpCells.Remove(followUpCells[i]);

        terrainFollowUpCoroutine = null;
        FinishTerrainFollowUp();
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] CellDataLoader TerrainFollowUp END: {swTotal.ElapsedMilliseconds}ms ({followUpCells.Count} cells)");
    }

    /// <summary>
    /// 地形追従更新の後処理。地形タイル生成イベントによる再生成抑制を解除し、
    /// カメラ移動ロックを解除する。追従対象が無い場合(地形不変のセル跨ぎ)も即時に呼ばれる。
    /// deferred フラグは boolean(冪等)であり、次のロードで再度 Begin されるため、
    /// コルーチンがキャンセルされて本処理が走らなくても整合性は保たれる。
    /// </summary>
    private void FinishTerrainFollowUp()
    {
        activeLoadBatch?.Trace("FOLLOWUP_DONE");
        // defer 中に受信して破棄された OnTerrainTileReady のセルを回収し、未処理集合へ合流させる。
        // 通常、これらのセルは updatedTerrainCellsThisLoad 経由で追従処理済みのため空集合になるが、
        // バッチのキャンセルタイミングによっては取りこぼしが起こりうるため、防御として拾う。
        if (pathManager != null)
        {
            foreach (var cell in pathManager.EndDeferredTerrainTilePathRebuilds())
            {
                pendingFollowUpCells.Add(cell);
            }
        }
        if (buildingManager != null)
        {
            foreach (var cell in buildingManager.EndDeferredTerrainTileBuildingRebuilds())
            {
                pendingFollowUpCells.Add(cell);
            }
        }


        var loadObjects = pendingObjectLoad;
        pendingObjectLoad = null;
        loadObjects?.Invoke();

        if (!isInitialLoadComplete)
        {
            isInitialLoadComplete = true;
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "CellDataLoader: 初回ロード完了");
        }

        // 回収したセルが未処理のまま残っていれば、追加の追従を1回実行する。
        // (回収セルが追従済みなら pendingFollowUpCells は空なので何も起きない。
        //  2周目の FinishTerrainFollowUp では defer 集合が空のため、無限ループにはならない)
        if (pendingFollowUpCells.Count > 0 && terrainFollowUpCoroutine == null)
        {
            terrainFollowUpCoroutine = RenderWorkScheduler.Start(this, TerrainFollowUpCoroutine(), "CellDataLoader");
        }
        else if (pendingFollowUpCells.Count == 0) pathManager?.ClearTerrainFollowProgress();
    }

    /// <summary>
    /// 強制的に現在のセルのデータを更新する (Button OnClickなど)。
    /// 全オブジェクトを破棄してから再ロードを実行する。
    /// </summary>
    public void ForceUpdateCurrentCells()
    {
        ForceReloadAllData();
    }

    /// <summary>
    /// バックエンドデータが変更された後に差分更新を行う。
    /// 道路作成・建物作成・地区作成などの確定後に呼ばれる。
    /// カメラが動いていなくても全マネージャーの差分更新を実行する。
    /// 各マネージャーはversion比較により変更のないオブジェクトの再描画をスキップする。
    /// </summary>
    public void ReloadAllData()
    {
        PropManager.Instance?.InvalidateForestOverview();
        // 再取得の合図は要求時に同期で記録する。後続のバッチが取消されても、次のバッチで取り直させるため。
        terrainManager?.InvalidateCellVersions();
        buildingManager?.MarkLoadedCellsStale();
        parcelManager?.MarkLoadedCellsStale();
        Debug.Log("CellDataLoader: ReloadAllData invoked.");
        loadSuspendedUntilReload = false;
        if (currentCenterCell.x != int.MinValue && currentCenterCell.y != int.MinValue)
        {
            LodCellSets cellSets = CalculateLodCellSets();
            UpdateManagers(cellSets, isForce: false, forceManagerUpdate: true);
        }
        else
        {
            // セッション切り替え直後(ClearAllManagerData で現在セルが未設定に戻った後)は
            // 差分更新の対象が無い。視点検出を初期化し、カメラが静止していても次の
            // CameraCellChecker の判定で OnCameraCellChanged が走って全ロードが始まるようにする。
            CameraCellChecker.Instance?.ResetViewState();
        }
        // 地区は全件スナップショット方式(ZoneManager.LoadCells は未ロード時と強制時のみ
        // 再取得)のため、上の isForce=false 経路ではリロードされない。エポックごとの
        // 地区の自動作成・統合・分離とポリゴン変化に追随するため、ここで明示的に
        // version 差分リロードを要求する(変化のない地区の再描画はスキップされる)。
        zoneManager?.RequestZoneRefresh();
        // インターチェンジは道路の竣工・編集でバックエンドが再検出するため、同じく明示的に取り直す
        // (version が変わっていなければラベルは再構築されない)。
        interchangeManager?.RequestRefresh();
        // エポック完了後や編集確定後のデータ再取得に追随して、アラートアイコンも更新する。
        // クールダウンにより短時間の連続呼び出しは間引かれる。
        AlertManager.Instance?.Refresh();
    }

    /// <summary>
    /// すべてのデータを完全にクリアして再取得する。
    /// 海岸線・水面・川もセッション開始時と同じ手順で取り直してからLOD計算を行う。
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

        // ClearAllManagerData が海岸線・水面・川のストアも空にするため、
        // 建物・道路の生成より先に取り直す(港湾の岸壁判定と川横断判定が参照する)。
        if (terrainManager != null)
        {
            yield return terrainManager.FetchCoastline();
        }

        LodCellSets cellSets = CalculateLodCellSets();
        UpdateManagers(cellSets, isForce: false);

        // 全量再取得完了後にアラートアイコンも更新する。
        AlertManager.Instance?.Refresh(forceRefresh: true);
    }

    /// <summary>
    /// session:reset 用。全データをクリアしたうえで、session:reload が届くまでカメラ由来の
    /// ロードを止める(バックエンドが新しい都市を準備している間に中途半端なデータを取り込まない)。
    /// </summary>
    public void ClearAllManagerDataForSessionReset()
    {
        ClearAllManagerData();
        loadSuspendedUntilReload = true;
    }

    /// <summary>
    /// 各マネージャーの全データ、内部状態キャッシュ、および各種ストア（海岸線、水面、路線、アラート等）を完全にクリアする。
    /// 単一シーン内でゲームセッションをロード・アンロードして切り替えるため、
    /// 前回のセッションにおけるカメラのセル位置（currentCenterCell）やLODキャッシュ（currentCellTiers）、
    /// ストリーミング処理のバッチ状態をすべて初期状態に巻き戻す必要がある。
    /// </summary>
    public void ClearAllManagerData()
    {
        pendingObjectLoad = null;
        // 実行中の地形追従やデータ取得バッチをキャンセル
        if (activeLoadBatch != null)
        {
            activeLoadBatch.Cancel();
            activeLoadBatch = null;
        }

        if (terrainFollowUpCoroutine != null)
        {
            RenderWorkScheduler.Stop(this, terrainFollowUpCoroutine);
            terrainFollowUpCoroutine = null;
        }

        // 各データマネージャーをクリア
        buildingManager?.ClearAllCells();
        pathManager?.ClearAllCells();
        parcelManager?.ClearAllCells();
        zoneManager?.ClearAllCells();
        terrainManager?.ClearAllCells();
        resourceManager?.ClearAllCells();
        propManager?.ClearAllCells();
        interchangeManager?.Clear();

        // GPU高さ窓と地形高さのフレームキャッシュを無効化する。
        // 無効化しないと、次のセーブのロード中に前のマップの標高が
        // Utils.GetGroundHeight から返り、地形タイル到着前に
        // 生成される建物・道路・パスのスプラインが誤った高さで作られる。
        if (heightmapTextureManager != null)
        {
            heightmapTextureManager.Invalidate();
        }
        Utils.InvalidateHeightCache();

        // 内部状態変数のクリア
        currentCellTiers.Clear();
        currentCenterCell = new Vector2Int(int.MinValue, int.MinValue);
        isInitialLoadComplete = false;
        pendingFollowUpCells.Clear();
        pendingIsForce = false;
        loadSuspendedUntilReload = false;
        hasDeferredCameraCell = false;
        nextCameraBatchTime = 0f;

        // 視点検出も初期化する。現在セルを未設定に戻した後にカメラが静止したままだと、
        // 視点変化が検出されず OnCameraCellChanged が二度と呼ばれない(海面しか見えない)ため。
        CameraCellChecker.Instance?.ResetViewState();

        // 静的ストアや他の管理オブジェクトのクリア
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
