using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using System.Linq;
using GeneratedModels;

/// <summary>
/// 建物情報を扱うマネージャー。
/// バックエンドの GET /buildings/ エンドポイントから BuildingSummary（single_agentスキーマ＋座標情報）を取得し、
/// Unity空間に建物オブジェクトを生成する。
/// </summary>
public class BuildingManager : BaseApiDataManager
{
    public static BuildingManager Instance { get; private set; }

    private const string BUILDING_PREFIX = "Building_";
    private const string BUILDING_TAG = "BuildingObject";

    private Dictionary<int, GameObject> buildingObjectsCache = new Dictionary<int, GameObject>();
    private Dictionary<int, BuildingSummary> buildingDataCache = new Dictionary<int, BuildingSummary>();
    private Dictionary<Vector2Int, LodTier> cellBuildingLod = new Dictionary<Vector2Int, LodTier>();
    private Dictionary<Vector2Int, HashSet<int>> buildingIdsByCell = new Dictionary<Vector2Int, HashSet<int>>();
    private Dictionary<int, Vector2Int> buildingCellById = new Dictionary<int, Vector2Int>();

    /// <summary>
    /// インフラ接続端子ノードID（agents.infra_node_id）→ 建物agent_id の逆引きインデックス。
    /// ノードAPIは所属施設（owner）を公開しない「agent→nodeの片方向参照」の設計のため、
    /// フロントは bbox ロード済みの建物データからこのインデックスで端子ノードを識別する
    /// （インフラパス敷設時のスナップ対象判定・端子マーカー表示・網互換判定に使う）。
    /// buildingDataCache への登録は必ず RegisterBuildingDataToCache を通し、
    /// 削除は SyncBuildingsInCells / ClearAllCells で同期すること。
    /// </summary>
    private Dictionary<int, int> buildingIdByInfraNodeId = new Dictionary<int, int>();

    /// <summary>
    /// 土地区画ID（agents.parcel_id）→ その区画に建つ建物agent_idの集合の逆引きインデックス。
    /// 地区拡張ドラッグ中に「街路に面する区画」から「その区画上の建物」をフロントだけで解決し、
    /// 地区色を波及させるために使う。buildingDataCache への登録は必ず RegisterBuildingDataToCache を通し、
    /// 削除は SyncBuildingsInCells / ClearAllCells で同期すること（buildingIdByInfraNodeId と同じ管理規約）。
    /// </summary>
    private Dictionary<int, HashSet<int>> buildingIdsByParcel = new Dictionary<int, HashSet<int>>();

    /// <summary>
    /// バックエンドから取得した建物のversionをキャッシュする。
    /// 差分更新時にバックエンドのversionと比較し、変更があった場合のみオブジェクトを再作成する。
    /// 視野外のため生成を遅延した建物にはversionをキャッシュしない
    /// (次回のマージロードでも「未生成」と判定させ、遅延キューを最新データで更新し続けるため)。
    /// </summary>
    private Dictionary<int, int> buildingVersionCache = new Dictionary<int, int>();

    // ================================================================
    // 詳細メッシュ生成の視野ゲートと個数上限(LRU)
    // ================================================================

    /// <summary>
    /// フル詳細メッシュ(非Far)を同時に保持する建物オブジェクトの上限数。
    /// 上限を超えたら「最も長く視野に入っていない」建物から破棄する(LRU)。
    /// 破棄された建物はデータキャッシュに残り、視野に入れば遅延生成キュー経由で再生成される。
    /// Far(白い箱)はこの上限の対象外(十分軽量でスカイライン維持に必要なため)。
    /// </summary>
    [SerializeField] private int maxDetailedBuildingObjects = 2000;

    /// <summary>
    /// 詳細メッシュ生成の視野判定に使う水平半角(度)。カメラ前方この角度以内(+カメラ近傍)を
    /// 「視野内」とみなし、視野内の建物のみ即時生成する。視野外の建物は生成せず
    /// 遅延キュー(deferredDetailBuildings)に積み、DetailGenerationWorker が
    /// 「視野に入ったものを距離順に」フレーム予算内で生成する。
    /// 視錐台は生成可否のフィルタではなく処理順の重みであり、視野外の建物も
    /// 上限に余裕がある限りアイドル時に埋められる(振り向き時のポップイン軽減)。
    /// </summary>
    private const float BUILDING_VIEW_HALF_ANGLE_DEG = 75f;

    /// <summary>カメラ近傍(この距離以内)は向きに関わらず視野内とみなす距離(m)。</summary>
    private const float BUILDING_VIEW_NEAR_DISTANCE = 100f;

    /// <summary>LRUの可視状態スイープを何フレームごとに行うか。</summary>
    private const int VISIBILITY_SWEEP_INTERVAL_FRAMES = 15;

    /// <summary>視野外のため生成を遅延している建物のリクエスト。</summary>
    private struct DeferredDetailRequest
    {
        public BuildingSummary data;
        public bool isDynamic;
    }

    /// <summary>建物ID→遅延生成リクエスト。同一IDは最新のリクエストで上書きされる。</summary>
    private readonly Dictionary<int, DeferredDetailRequest> deferredDetailBuildings = new Dictionary<int, DeferredDetailRequest>();

    /// <summary>現在フル詳細メッシュ(非Far)のオブジェクトを持つ建物IDの集合。LRU上限の母集団。</summary>
    private readonly HashSet<int> detailedBuildingIds = new HashSet<int>();

    /// <summary>建物IDごとに「最後に視野内で観測されたフレーム番号」。LRU破棄の優先度に使う。</summary>
    private readonly Dictionary<int, int> buildingLastVisibleFrame = new Dictionary<int, int>();


    /// <summary>
    /// LoadLodCells で起動したコルーチンを追跡し、新たなカメラ移動時にキャンセルできるようにする。
    /// </summary>
    private List<Coroutine> activeLodCoroutines = new List<Coroutine>();

    /// <summary>
    /// カメラ移動によるLOD更新中は TerrainManager.OnTerrainTileReady がセルごとに発火する。
    /// ここで建物を即時再生成すると、CPU高さ配列が未再構築のため古い高さで作られ、
    /// 直後の AdjustBuildingHeightsForCellsCoroutine で作り直される二重コストがかかる。
    /// CellDataLoader が地形ロード完了後に行う AdjustBuildingHeightsForCellsCoroutine へ集約する。
    /// </summary>
    private bool deferTerrainTileBuildingRebuilds = false;

    /// <summary>
    /// defer 中に受信して破棄した OnTerrainTileReady のセル。
    /// EndDeferredTerrainTileBuildingRebuilds で回収して呼び出し元(CellDataLoader)に返し、
    /// 追従更新の未処理集合(pendingFollowUpCells)へ合流させる。
    /// 通常は地形ロード完了後の一括追従で処理済みのセルばかりだが、
    /// バッチのキャンセルタイミングによる取りこぼしへの防御として記録する。
    /// </summary>
    private readonly HashSet<Vector2Int> deferredTerrainReadyCells = new HashSet<Vector2Int>();

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
        // 公共施設ハイライトの光オーバーレイ(建物データを参照する常駐描画)を用意する。
        PublicFacilityGlowOverlay.Ensure();
        // 視野外のため生成を遅延した詳細メッシュを消化する常駐ワーカー。
        StartCoroutine(DetailGenerationWorker());
    }

    private void Update()
    {
        // LRU用の可視状態スイープ。毎フレームだと無駄なので一定間隔で行う。
        // 詳細メッシュを持つ建物のうち視野内のものの「最終可視フレーム」を更新する。
        if (Time.frameCount % VISIBILITY_SWEEP_INTERVAL_FRAMES != 0) return;
        if (detailedBuildingIds.Count == 0) return;

        Camera cam = Camera.main;
        if (cam == null) return;
        Vector3 camPos = cam.transform.position;
        Vector3 fwd = GetHorizontalForward(cam);

        foreach (int id in detailedBuildingIds)
        {
            if (!buildingObjectsCache.TryGetValue(id, out GameObject obj) || obj == null) continue;
            // 非アクティブ(アンロード済みセルのプール退避中)のオブジェクトは「見えていない」ので
            // 最終可視フレームを更新しない(更新するとLRU破棄の対象から外れ続けてしまう)。
            if (!obj.activeInHierarchy) continue;
            if (IsPositionInGenerationView(obj.transform.position, camPos, fwd))
            {
                buildingLastVisibleFrame[id] = Time.frameCount;
            }
        }
    }

    /// <summary>カメラ前方ベクトルの水平成分(正規化済み)。実装は Utils の共通ヘルパー。</summary>
    private static Vector3 GetHorizontalForward(Camera cam)
    {
        return Utils.GetCameraHorizontalForward(cam);
    }

    /// <summary>
    /// 指定位置が「詳細メッシュを即時生成すべき視野内」かどうか。
    /// カメラ近傍(BUILDING_VIEW_NEAR_DISTANCE以内)は向きに関わらず視野内とみなす。
    /// </summary>
    private static bool IsPositionInGenerationView(Vector3 position, Vector3 camPos, Vector3 camForwardHorizontal)
    {
        return Utils.IsInHorizontalView(position, camPos, camForwardHorizontal, BUILDING_VIEW_NEAR_DISTANCE, BUILDING_VIEW_HALF_ANGLE_DEG);
    }

    private void OnDestroy()
    {
        TerrainManager.OnTerrainTileReady -= OnTerrainTileReadyHandler;
    }

    public void BeginDeferredTerrainTileBuildingRebuilds()
    {
        deferTerrainTileBuildingRebuilds = true;
    }

    /// <summary>
    /// 地形タイル生成イベントによる建物再生成の抑制を解除し、抑制中に受信して破棄した
    /// イベントのセル一覧を返す(返却後、内部の記録はクリアされる)。
    /// </summary>
    public HashSet<Vector2Int> EndDeferredTerrainTileBuildingRebuilds()
    {
        deferTerrainTileBuildingRebuilds = false;
        var dropped = new HashSet<Vector2Int>(deferredTerrainReadyCells);
        deferredTerrainReadyCells.Clear();
        return dropped;
    }

    private void OnTerrainTileReadyHandler(Vector2Int cellIndex, Terrain terrain)
    {
        Utils.InvalidateHeightCache();

        if (deferTerrainTileBuildingRebuilds)
        {
            // 破棄したイベントのセルを記録しておき、EndDeferred 時に呼び出し元へ返す。
            deferredTerrainReadyCells.Add(cellIndex);
            return;
        }

        if (!buildingIdsByCell.TryGetValue(cellIndex, out HashSet<int> buildingIds)) return;

        bool isDynamic = false;
        if (cellBuildingLod.TryGetValue(cellIndex, out LodTier tier))
        {
            isDynamic = (tier == LodTier.Dynamic);
        }

        Camera cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        Vector3 camFwd = cam != null ? GetHorizontalForward(cam) : Vector3.forward;

        foreach (int buildingId in new List<int>(buildingIds))
        {
            if (!buildingObjectsCache.TryGetValue(buildingId, out GameObject obj)) continue;
            if (obj == null) continue;

            if (isDynamic)
            {
                if (buildingDataCache.TryGetValue(buildingId, out BuildingSummary buildingData))
                {
                    // メッシュ再生成は重いので視野内の建物のみ即時に行い、視野外は遅延キューへ
                    // 積む(視野に入ったら DetailGenerationWorker が現在の地形高さで作り直す)。
                    if (IsPositionInGenerationView(obj.transform.position, camPos, camFwd))
                    {
                        CreateOrUpdateBuildingObject(buildingData, isDynamic: true, forceRebuild: true);
                    }
                    else
                    {
                        deferredDetailBuildings[buildingId] = new DeferredDetailRequest
                        {
                            data = buildingData,
                            isDynamic = true,
                        };
                    }
                }
            }
            else
            {
                UpdateBuildingHeight(obj);
            }
        }
    }

    public override void ClearAllCells()
    {
        Debug.Log("BuildingManager: 全建物データをクリア");
        foreach (var obj in buildingObjectsCache.Values)
        {
            if (obj != null) Destroy(obj);
        }

        buildingObjectsCache.Clear();
        buildingDataCache.Clear();
        buildingIdByInfraNodeId.Clear();
        buildingIdsByParcel.Clear();
        buildingIdsByCell.Clear();
        buildingCellById.Clear();
        loadedCells.Clear();
        cellBuildingLod.Clear();
        buildingVersionCache.Clear();
        deferredDetailBuildings.Clear();
        detailedBuildingIds.Clear();
        buildingLastVisibleFrame.Clear();
    }

    // ================================================================
    // LOD対応セルロード（マージBBOX版）
    // ================================================================

    public override void LoadLodCells(LodCellSets cellSets, bool isForce)
    {
        LoadLodCells(cellSets, null, isForce);
    }

    public override void LoadLodCells(LodCellSets cellSets, HashSet<Vector2Int> tierChangedCells, bool isForce)
    {
        // 実行中の古いコルーチンをすべてキャンセル（カメラ移動による再呼び出し対策）
        foreach (var c in activeLodCoroutines)
        {
            if (c != null) StopCoroutine(c);
        }
        activeLodCoroutines.Clear();

        var allBuildingCells = new HashSet<Vector2Int>(cellSets.farCells);
        var nearSet = new HashSet<Vector2Int>(cellSets.nearCells);
        var dynamicSet = new HashSet<Vector2Int>(cellSets.dynamicCells);

        var toUnload = new List<Vector2Int>();
        foreach (var cell in loadedCells)
        {
            if (!allBuildingCells.Contains(cell))
                toUnload.Add(cell);
        }

        // 常に cellSets.farCells の全セルを対象とする。
        // 未ロードセルは新規ロード、ロード済みセルは version ベースで差分更新される。
        var cellsToLoad = new List<Vector2Int>();
        var lodUpdates = new Dictionary<Vector2Int, LodTier>();
        foreach (var cell in cellSets.farCells)
        {
            LodTier desiredTier = dynamicSet.Contains(cell) ? LodTier.Dynamic : (nearSet.Contains(cell) ? LodTier.Near : LodTier.Far);
            cellsToLoad.Add(cell);
            lodUpdates[cell] = desiredTier;
        }

        // ティアが実際に変更されたセルを特定する。
        // 建物のversionが変わっていなくても、ティアがFar→Near/Dynamicに変化した場合は
        // 白い箱(_far)からフル詳細への再生成が必要。
        var tierChangedBuildingCells = new HashSet<Vector2Int>();
        foreach (var kvp in lodUpdates)
        {
            if (!cellBuildingLod.TryGetValue(kvp.Key, out LodTier oldTier) || oldTier != kvp.Value)
            {
                tierChangedBuildingCells.Add(kvp.Key);
            }
        }

        if (cellsToLoad.Count > 0)
        {
            activeLodCoroutines.Add(StartCoroutine(LoadMergedBuildingsAndApplyCells(cellsToLoad, nearSet, dynamicSet, toUnload, lodUpdates, tierChangedBuildingCells)));
        }
        else
        {
            ApplyLodCellChanges(cellsToLoad, toUnload, lodUpdates);
        }
    }

    /// <summary>
    /// 新しいセルの建物を生成してから不要セルを非表示にする。
    /// カメラ移動時に古いセルを先に消すと、API待ち時間のあいだ都市が空白になるため、
    /// ロード成功を確認してから可視セル集合を切り替える。
    /// </summary>
    private IEnumerator LoadMergedBuildingsAndApplyCells(List<Vector2Int> cellsToLoad, HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet, List<Vector2Int> cellsToUnload, Dictionary<Vector2Int, LodTier> lodUpdates, HashSet<Vector2Int> tierChangedBuildingCells)
    {
        bool succeeded = false;
        // StartCoroutine ではなく IEnumerator を直接 yield return する。
        // StartCoroutine で起動した子はカメラ移動時の親の StopCoroutine では停止せず、
        // 旧バッチの建物生成が新バッチと並走してしまうため。
        yield return LoadMergedBuildings(cellsToLoad, nearSet, dynamicSet, tierChangedBuildingCells, success => succeeded = success);

        if (!succeeded) yield break;

        ApplyLodCellChanges(cellsToLoad, cellsToUnload, lodUpdates);
    }

    private void ApplyLodCellChanges(List<Vector2Int> cellsToLoad, List<Vector2Int> cellsToUnload, Dictionary<Vector2Int, LodTier> lodUpdates)
    {
        foreach (var cell in cellsToLoad)
        {
            loadedCells.Add(cell);
        }

        foreach (var kvp in lodUpdates)
        {
            cellBuildingLod[kvp.Key] = kvp.Value;
        }

        foreach (var cell in cellsToUnload)
        {
            UnloadCell(cell);
            loadedCells.Remove(cell);
            cellBuildingLod.Remove(cell);
        }
    }

    /// <summary>
    /// 複数セルのマージBBOXを使って建物を一括取得する。
    /// 従来のセルごとのAPIコール(最大49回)を1回に削減する。
    /// 各建物の位置からLOD tierを判定し、Nearならフル詳細、Farなら白い箱で処理する。
    /// </summary>
    private IEnumerator LoadMergedBuildings(List<Vector2Int> cells, HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet, HashSet<Vector2Int> tierChangedBuildingCells, Action<bool> onComplete = null)
    {
        CalculateMergedBBox(cells, out float minX, out float minZ, out float maxX, out float maxZ);

        BuildingsListResponse response = null;
        yield return ApiClient.GetBulk<BuildingsListResponse>(
            $"/buildings/?bbox={minX},{minZ},{maxX},{maxZ}&limit=5000",
            r => response = r,
            timingLabel: "BuildingManager buildings");
        if (response == null)
        {
            onComplete?.Invoke(false);
            yield break;
        }

        HashSet<int> receivedIds = new HashSet<int>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 詳細メッシュ生成の視野ゲート用カメラ状態。複数フレームにまたがるループなので、
        // フレームを譲るたびに取り直す。
        Camera viewCam = Camera.main;
        Vector3 viewCamPos = viewCam != null ? viewCam.transform.position : Vector3.zero;
        Vector3 viewCamFwd = viewCam != null ? GetHorizontalForward(viewCam) : Vector3.forward;

        if (response?.buildings != null)
        {
            foreach (var b in response.buildings)
            {
                receivedIds.Add(b.agent_id);

                // 建物のcenter_point位置からLOD tierを判定
                float[] cp = b.centerPointArray;
                Vector3 buildingPos = (cp != null && cp.Length >= 2)
                    ? new Vector3(cp[0], 0, cp[1])
                    : Vector3.zero;

                Vector2Int buildingCell = PositionToCell(buildingPos);
                LodTier desiredTier = dynamicSet.Contains(buildingCell) ? LodTier.Dynamic : (nearSet.Contains(buildingCell) ? LodTier.Near : LodTier.Far);

                bool needsRebuild = false;

                if (buildingVersionCache.TryGetValue(b.agent_id, out int cachedVersion) && cachedVersion == b.version)
                {
                    // version一致: 通常は再作成不要だが、ティア変更セル内で
                    // 既存オブジェクトが新しいティアと矛盾する場合は再生成する。
                    // 例: Far(白い箱)からNear/Dynamicへの遷移や、NearとDynamicの間の遷移（メッシュの地形追従化のため）。
                    if (tierChangedBuildingCells != null && tierChangedBuildingCells.Contains(buildingCell))
                    {
                        if (buildingObjectsCache.TryGetValue(b.agent_id, out GameObject existingTier) && existingTier != null)
                        {
                            cellBuildingLod.TryGetValue(buildingCell, out LodTier oldTier);
                            bool isFarTransition = (oldTier == LodTier.Far) || (desiredTier == LodTier.Far);
                            bool isDynamicTransition = (oldTier == LodTier.Dynamic) || (desiredTier == LodTier.Dynamic);
                            needsRebuild = isFarTransition || isDynamicTransition;
                        }
                    }

                    if (!needsRebuild)
                    {
                        // 非表示だった既存オブジェクトがあれば再表示する
                        if (buildingObjectsCache.TryGetValue(b.agent_id, out GameObject existing) && existing != null && !existing.activeSelf)
                        {
                            existing.SetActive(true);
                        }
                    }
                }
                else
                {
                    needsRebuild = true;
                }

                if (needsRebuild)
                {
                    // ステータス変化の検知とエフェクト再生
                    if (buildingDataCache.TryGetValue(b.agent_id, out BuildingSummary oldBuildingData))
                    {
                        if (oldBuildingData.status != b.status)
                        {
                            float groundHeight = HeightmapTextureManager.GetTerrainHeight(new Vector3(buildingPos.x, 1000f, buildingPos.z));
                            Vector3 effectPos = new Vector3(buildingPos.x, groundHeight, buildingPos.z);
                            TriggerBuildingStatusEffect(effectPos, oldBuildingData.status, b.status);
                        }
                    }

                    // 新規またはversion変更/ティア変更: オブジェクトを再作成。
                    // forceRebuild=true でプール再利用をスキップし、必ずメッシュを再生成する。
                    // isDynamic はこの時点で確定している desiredTier から渡す
                    // （cellBuildingLod はロード完了後の ApplyLodCellChanges まで旧ティアのままなので参照しない）。
                    UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"BuildingManager: 建物 {b.agent_id} の再描画を開始 (version {cachedVersion} -> {b.version}, tier -> {desiredTier})");

                    if (desiredTier == LodTier.Far)
                    {
                        // Far(白い箱)は軽量なので視野に関わらず即時生成する(スカイライン維持)。
                        CreateFarBuildingObject(b);
                        buildingVersionCache[b.agent_id] = b.version;
                    }
                    else if (!IsPositionInGenerationView(buildingPos, viewCamPos, viewCamFwd))
                    {
                        // 視野外: 詳細メッシュは今は生成せず、遅延キューへ積む。
                        // versionキャッシュは消す(旧オブジェクトのversionが残っていると、
                        // 次回のマージロードで「生成済み・変更なし」と誤判定されるため。
                        // DetailGenerationWorker が生成した時点で改めてキャッシュされる)。
                        // 既存オブジェクトがあれば古いversion/ティアの見た目のまま画面外に残し、
                        // 視野に入ったらワーカーが作り直す(視野外で建物を消滅させないため)。
                        // データキャッシュとセル逆引きはオブジェクトが無くても登録し、
                        // 削除同期(SyncBuildingsInCells)や各種参照が機能するようにする。
                        buildingVersionCache.Remove(b.agent_id);
                        RegisterBuildingDataToCache(b);
                        if (cp != null && cp.Length >= 2)
                        {
                            RegisterBuildingCell(b.agent_id, buildingPos);
                        }
                        deferredDetailBuildings[b.agent_id] = new DeferredDetailRequest
                        {
                            data = b,
                            isDynamic = desiredTier == LodTier.Dynamic,
                        };
                    }
                    else
                    {
                        CreateOrUpdateBuildingObject(b, desiredTier == LodTier.Dynamic, forceRebuild: true);
                        buildingVersionCache[b.agent_id] = b.version;
                    }
                }

                // フレーム予算: 目標FPSに基づく動的予算を超えたら次フレームに譲る。
                // これによりオブジェクト生成の集中によるカクつきを抑制する。
                if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.FrameBudgetMs)
                {
                    // 予算スライスの区切りで詳細メッシュの個数上限を適用する
                    // (建物1件ごとに全走査すると走査コストが支配的になるため)。
                    EvictDetailedBuildingsOverCap(viewCamPos, viewCamFwd, justCreatedId: b.agent_id);
                    yield return null;
                    stopwatch.Restart();
                    // フレームを跨いだのでカメラ状態を取り直す。
                    viewCam = Camera.main;
                    viewCamPos = viewCam != null ? viewCam.transform.position : Vector3.zero;
                    viewCamFwd = viewCam != null ? GetHorizontalForward(viewCam) : Vector3.forward;
                }
            }
        }

        // ループ完了後にも個数上限を適用する(最後のスライス分)。
        EvictDetailedBuildingsOverCap(viewCamPos, viewCamFwd, justCreatedId: 0);

        // レスポンスに含まれない建物を削除
        SyncBuildingsInCells(cells, receivedIds);

        onComplete?.Invoke(true);
    }

    /// <summary>
    /// 建物のステータス変化に応じたエフェクトを呼び出します。
    /// </summary>
    private void TriggerBuildingStatusEffect(Vector3 position, int oldStatus, int newStatus)
    {
        if (VisualEffectManager.Instance == null) return;

        // 建設中 (STATUS_CONSTRUCTING) から アクティブ (STATUS_ACTIVE) への完成遷移
        if (oldStatus == Constants.STATUS_CONSTRUCTING && newStatus == Constants.STATUS_ACTIVE)
        {
            VisualEffectManager.Instance.PlayConstructionComplete(position);
        }
        // アクティブ (STATUS_ACTIVE) から 廃墟 (STATUS_COLLAPSED) または 解体 (STATUS_DEMOLISHING) への遷移
        else if (oldStatus == Constants.STATUS_ACTIVE &&
                 (newStatus == Constants.STATUS_COLLAPSED
                  || newStatus == Constants.STATUS_DEMOLISHING))
        {
            VisualEffectManager.Instance.PlayDemolition(position);
        }
    }

    /// <summary>
    /// 複数セルにまたがる建物の同期処理（差分更新用）。
    /// APIレスポンスに含まれない建物のうち、指定セル群に含まれるものを削除する。
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
                Destroy(obj);
                buildingObjectsCache.Remove(id);
            }
            UnregisterBuildingCell(id);
            // 端子ノード・区画の逆引きインデックスも同期して外す
            if (buildingDataCache.TryGetValue(id, out BuildingSummary removedData))
            {
                if (removedData.infra_node_id > 0)
                {
                    buildingIdByInfraNodeId.Remove(removedData.infra_node_id);
                }
                if (removedData.parcel_id > 0
                    && buildingIdsByParcel.TryGetValue(removedData.parcel_id, out HashSet<int> byParcel))
                {
                    byParcel.Remove(id);
                    if (byParcel.Count == 0) buildingIdsByParcel.Remove(removedData.parcel_id);
                }
            }
            buildingDataCache.Remove(id);
            // 視野ゲート・LRU関連の台帳も同期して外す
            detailedBuildingIds.Remove(id);
            buildingLastVisibleFrame.Remove(id);
            deferredDetailBuildings.Remove(id);
            buildingVersionCache.Remove(id);
        }
    }

    /// <summary>
    /// 指定セル群に含まれる建物のY座標を、現在の地形高さに合わせて再調整する。
    /// dynamic地形の更新後に CellDataLoader から呼び出される。
    /// </summary>
    public IEnumerator AdjustBuildingHeightsForCellsCoroutine(List<Vector2Int> cells)
    {
        HashSet<int> buildingIds = new HashSet<int>();
        foreach (var cell in cells)
        {
            if (!buildingIdsByCell.TryGetValue(cell, out HashSet<int> ids)) continue;
            foreach (int id in ids) buildingIds.Add(id);
        }

        // メッシュ再生成の集中によるカクつきを抑えるため、フレーム予算を超えたら次フレームに譲る。
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 視野ゲート用カメラ状態。フレームを譲るたびに取り直す。
        Camera cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        Vector3 camFwd = cam != null ? GetHorizontalForward(cam) : Vector3.forward;

        foreach (int id in buildingIds)
        {
            // 本コルーチンは CellDataLoader.TerrainFollowUpCoroutine から直接 yield されるため、
            // 1棟のメッシュ生成例外を漏らすと残りの建物・道路・プロップ調整・FinishTerrainFollowUp
            // (deferred抑制とカメラロックの解除)まで巻き込んで中断し、都市全体の地形追従が
            // 恒久的に止まる。1棟の失敗はその建物だけに留めて続行する(yield をまたがない
            // 再生成呼び出しのみを包む)。
            try
            {
                if (buildingObjectsCache.TryGetValue(id, out GameObject obj) && obj != null)
                {
                    Vector2Int cell = PositionToCell(obj.transform.position);
                    if (cellBuildingLod.TryGetValue(cell, out LodTier tier) && tier == LodTier.Dynamic)
                    {
                        // Dynamicセル: フェンス・地面パネルをロード完了後の地形に追従させるため、
                        // メッシュを強制的に再生成する。再生成は重いので視野内の建物のみ即時に行い、
                        // 視野外は遅延キューへ積む(視野に入ったらワーカーが現在の地形高さで作り直す)。
                        if (buildingDataCache.TryGetValue(id, out BuildingSummary buildingData))
                        {
                            if (IsPositionInGenerationView(obj.transform.position, camPos, camFwd))
                            {
                                CreateOrUpdateBuildingObject(buildingData, isDynamic: true, forceRebuild: true);
                            }
                            else
                            {
                                deferredDetailBuildings[id] = new DeferredDetailRequest
                                {
                                    data = buildingData,
                                    isDynamic = true,
                                };
                            }
                        }
                    }
                    else
                    {
                        UpdateBuildingHeight(obj);
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"BuildingManager: 地形追従の高さ調整に失敗 agent_id={id}: {e}");
            }

            if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.FrameBudgetMs)
            {
                yield return null;
                stopwatch.Restart();
                cam = Camera.main;
                camPos = cam != null ? cam.transform.position : Vector3.zero;
                camFwd = cam != null ? GetHorizontalForward(cam) : Vector3.forward;
            }
        }
    }

    // ================================================================
    // 遅延生成ワーカーとLRU破棄
    // ================================================================

    /// <summary>
    /// 遅延生成キュー(deferredDetailBuildings)を消化する常駐ワーカー。
    /// 毎フレーム、フレーム予算内で「視野内かつカメラに近い」建物から順に詳細メッシュを生成する。
    /// 視野外の建物は、詳細メッシュ数が上限(maxDetailedBuildingObjects)に達していない限り
    /// アイドル的に生成する(振り向き時のポップイン軽減)。上限に達している場合、
    /// 視野外の建物は生成せずキューに残す(視野に入れば最優先で処理される)。
    /// </summary>
    private IEnumerator DetailGenerationWorker()
    {
        var budgetWatch = new System.Diagnostics.Stopwatch();
        while (true)
        {
            if (deferredDetailBuildings.Count == 0)
            {
                yield return null;
                continue;
            }

            Camera cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
            Vector3 fwd = cam != null ? GetHorizontalForward(cam) : Vector3.forward;

            budgetWatch.Restart();
            while (deferredDetailBuildings.Count > 0 && budgetWatch.Elapsed.TotalMilliseconds <= PerformanceConfig.FrameBudgetMs)
            {
                // 最優先の1件を線形走査で選ぶ。キューは高々数千件・単純なベクトル演算のみなので、
                // 毎回の走査コストは生成コストに比べて無視できる。
                // 比較は「未選択なら無条件採用」のフラグ方式にする(center_point欠損などで
                // スコアが float.MaxValue になる建物も必ず選出・消化されるようにするため。
                // センチネル値の大小比較は float の精度で破綻しうる)。
                int bestId = 0;
                float bestScore = float.MaxValue;
                bool found = false;
                foreach (var kvp in deferredDetailBuildings)
                {
                    float score = ScoreDeferredBuilding(kvp.Value.data, camPos, fwd);
                    if (!found || score < bestScore)
                    {
                        bestScore = score;
                        bestId = kvp.Key;
                        found = true;
                    }
                }
                if (!found) break;

                // 視野外しか残っておらず上限到達済みなら、このフレームでは何もしない
                // (視野に入るかLRU破棄で枠が空くのを待つ)。
                bool bestInView = bestScore < DEFERRED_OUT_OF_VIEW_PENALTY;
                if (!bestInView && detailedBuildingIds.Count >= maxDetailedBuildingObjects) break;

                DeferredDetailRequest req = deferredDetailBuildings[bestId];
                deferredDetailBuildings.Remove(bestId);

                // データキャッシュから消えた建物(削除済み)は生成しない。
                if (!buildingDataCache.ContainsKey(bestId)) continue;

                // ロード範囲外のセルに属する建物は生成しない。キュー投入からここまでの間に
                // セルがアンロードされた場合に生成すると、非表示のはずの建物がアクティブな
                // GameObjectとして復活してしまう。セルが再ロードされればマージロードが再度
                // 遅延キューへ積み直す。
                float[] reqCp = req.data.centerPointArray;
                if (reqCp != null && reqCp.Length >= 2)
                {
                    Vector2Int cell = PositionToCell(new Vector3(reqCp[0], 0f, reqCp[1]));
                    if (!loadedCells.Contains(cell)) continue;
                }

                CreateOrUpdateBuildingObject(req.data, req.isDynamic, forceRebuild: true);
                buildingVersionCache[bestId] = req.data.version;
                EvictDetailedBuildingsOverCap(camPos, fwd, justCreatedId: bestId);
            }

            yield return null;
        }
    }

    /// <summary>視野外の遅延建物に加算する処理順ペナルティ。視野内(距離のみ)より必ず後になる。</summary>
    private const float DEFERRED_OUT_OF_VIEW_PENALTY = 100000f;

    /// <summary>遅延キューの処理順スコア(小さいほど優先)。視野内は距離、視野外は距離+大ペナルティ。</summary>
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
    /// 詳細メッシュ数が上限を超えている間、「視野外で最も長く視野に入っていない」建物から破棄する。
    /// 破棄するのはGameObjectのみ: データキャッシュ・セル逆引きは保持し、versionキャッシュを消して
    /// 遅延キューに戻すことで、視野に入れば再生成される。視野内の建物は破棄しない
    /// (視野内の建物だけで上限を超える場合は、見た目を守るため一時的な超過を許容する)。
    /// </summary>
    private void EvictDetailedBuildingsOverCap(Vector3 camPos, Vector3 camForwardHorizontal, int justCreatedId)
    {
        while (detailedBuildingIds.Count > maxDetailedBuildingObjects)
        {
            int evictId = 0;
            int oldestFrame = int.MaxValue;
            bool found = false;
            foreach (int id in detailedBuildingIds)
            {
                if (id == justCreatedId) continue;
                if (!buildingObjectsCache.TryGetValue(id, out GameObject obj) || obj == null)
                {
                    // オブジェクトが既に破棄済みの台帳残骸は最優先で片付ける。
                    evictId = id;
                    found = true;
                    break;
                }
                // 非アクティブ(アンロード済みセルのプール退避中)は無条件に破棄候補。
                // アクティブなオブジェクトは視野内なら破棄しない。
                if (obj.activeInHierarchy && IsPositionInGenerationView(obj.transform.position, camPos, camForwardHorizontal)) continue;
                int lastSeen = buildingLastVisibleFrame.TryGetValue(id, out int f) ? f : 0;
                if (!found || lastSeen < oldestFrame)
                {
                    oldestFrame = lastSeen;
                    evictId = id;
                    found = true;
                }
            }
            if (!found) break;
            EvictDetailedBuilding(evictId);
        }
    }

    /// <summary>
    /// 詳細メッシュ建物のGameObjectをLRU破棄する。データキャッシュとセル逆引きは保持し、
    /// 現在のティアで遅延キューへ戻す(視野に入れば DetailGenerationWorker が再生成する)。
    /// versionキャッシュを消すことで、マージロードの差分判定でも「未生成」として扱われる。
    /// </summary>
    private void EvictDetailedBuilding(int buildingId)
    {
        if (buildingObjectsCache.TryGetValue(buildingId, out GameObject obj) && obj != null)
        {
            Destroy(obj);
        }
        buildingObjectsCache.Remove(buildingId);
        detailedBuildingIds.Remove(buildingId);
        buildingLastVisibleFrame.Remove(buildingId);
        buildingVersionCache.Remove(buildingId);

        if (buildingDataCache.TryGetValue(buildingId, out BuildingSummary data))
        {
            float[] cp = data.centerPointArray;
            Vector2Int cell = (cp != null && cp.Length >= 2)
                ? PositionToCell(new Vector3(cp[0], 0f, cp[1]))
                : default;
            bool isDynamic = GetCellLodTier(cell) == LodTier.Dynamic;
            deferredDetailBuildings[buildingId] = new DeferredDetailRequest { data = data, isDynamic = isDynamic };
        }
    }

    /// <summary>
    /// 建物IDを「フル詳細メッシュ保持中」として登録し、最終可視フレームを現在に更新する。
    /// CreateOrUpdateBuildingObject の生成・プール再利用の両経路から呼ぶ。
    /// </summary>
    private void MarkBuildingDetailed(int buildingId)
    {
        detailedBuildingIds.Add(buildingId);
        buildingLastVisibleFrame[buildingId] = Time.frameCount;
    }

    private bool IsBuildingApplicableForAltitude(BuildingSummary buildingData)
    {
        int modelType = BuildingMeshBuilder.GetModelType(buildingData);
        return BuildingMeshBuilder.IsApplicableModelType(modelType);
    }

    /// <summary>
    /// UpdateBuildingHeight で参照する ParcelManager のキャッシュ。
    /// 地形追従で多数の建物を処理する際、建物ごとに FindAnyObjectByType を
    /// 呼ぶコストを避けるために保持する。
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
            float buildingBaseHeight;
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

            if (parcel != null && IsBuildingApplicableForAltitude(buildingData))
            {
                buildingBaseHeight = parcel.altitude + 0.01f;
            }
            else
            {
                float groundHeight = HeightmapTextureManager.GetTerrainHeight(new Vector3(cx, 1000f, cz));
                buildingBaseHeight = groundHeight + 0.01f;
            }
            buildingGO.transform.position = new Vector3(cx, buildingBaseHeight, cz);
        }
        else
        {
            Vector3 p = buildingGO.transform.position;
            float groundHeight = HeightmapTextureManager.GetTerrainHeight(new Vector3(p.x, 1000f, p.z));
            buildingGO.transform.position = new Vector3(p.x, groundHeight + 0.01f, p.z);
        }
    }

    private void RegisterBuildingCell(int buildingId, Vector3 position)
    {
        Vector2Int cell = PositionToCell(position);
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

    private void CreateFarBuildingObject(BuildingSummary buildingData)
    {
        GameObject oldBuilding = null;
        if (buildingObjectsCache.TryGetValue(buildingData.agent_id, out GameObject existing))
        {
            if (existing != null)
            {
                var existingIdInfo = existing.GetComponent<ObjectIdInfo>();
                int currentStatus = existingIdInfo != null ? existingIdInfo.status : 0;
                bool existingIsFar = existingIdInfo != null && existingIdInfo.isFarObject;
                // 既存が遠景LODオブジェクトかつステータス一致ならプールから再利用
                if (currentStatus == buildingData.status && existingIsFar)
                {
                    if (!existing.activeSelf)
                        existing.SetActive(true);
                    RegisterBuildingDataToCache(buildingData);
                    UpdateBuildingHeight(existing);
                    RegisterBuildingCell(buildingData.agent_id, existing.transform.position);
                    UpdatePlannedIcon(existing, buildingData);
                    // Farティアになったので、残っていた詳細メッシュの遅延リクエストは無効化する。
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

        BuildingMeshResult meshResult = new BuildingMeshBuilder().Generate(buildingData);
        if (meshResult?.Mesh != null)
        {
            mf.mesh = meshResult.Mesh;
        }

        mr.sharedMaterial = BuildingMeshBuilder.GetDefaultBuildingMaterial();
        ApplyDepthOffsetShader(mr);

        float[] cp = buildingData.centerPointArray;
        if (cp != null && cp.Length >= 2)
        {
            float cx = cp[0];
            float cz = cp[1];
            float buildingBaseHeight;
            ParcelSummary parcel = null;
            if (buildingData.parcel_id > 0)
            {
                ParcelManager parcelManager = UnityEngine.Object.FindAnyObjectByType<ParcelManager>();
                if (parcelManager != null)
                {
                    parcel = parcelManager.GetParcelSummary(buildingData.parcel_id);
                }
            }

            if (parcel != null && IsBuildingApplicableForAltitude(buildingData))
            {
                buildingBaseHeight = parcel.altitude + 0.01f;
            }
            else
            {
                float groundHeight = HeightmapTextureManager.GetTerrainHeight(new Vector3(cx, 1000f, cz));
                buildingBaseHeight = groundHeight + 0.01f;
            }
            buildingGO.transform.position = new Vector3(cx, buildingBaseHeight, cz);
            RegisterBuildingCell(buildingData.agent_id, buildingGO.transform.position);
        }

        UpdatePlannedIcon(buildingGO, buildingData);

        // 辞書は即座に新しいオブジェクトに更新する。
        // これにより、短時間に同じIDで再描画が呼ばれてもオブジェクトが増殖しない。
        buildingObjectsCache[buildingData.agent_id] = buildingGO;

        // Far(白い箱)になったので、詳細メッシュのLRU台帳と遅延リクエストからは外す。
        detailedBuildingIds.Remove(buildingData.agent_id);
        buildingLastVisibleFrame.Remove(buildingData.agent_id);
        deferredDetailBuildings.Remove(buildingData.agent_id);

        // 古いオブジェクトの破棄だけを数フレーム遅延させる。
        if (oldBuilding != null)
        {
            StartCoroutine(DestroyObjectAfterFrames(oldBuilding));
        }
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
    /// 指定したワールドXZ矩形に重なるセルに登録された建物IDを収集する。
    /// パス敷設中のローカル干渉推定など、近傍建物の候補を全件走査せず絞り込むために使う
    /// （buildingIdsByCell によるO(セル数)の空間絞り込み）。
    /// </summary>
    public HashSet<int> QueryBuildingIdsInBounds(float minX, float minZ, float maxX, float maxZ)
    {
        var result = new HashSet<int>();
        float cellSize = TerrainManager.CellSize;
        int cx0 = Mathf.FloorToInt(minX / cellSize);
        int cx1 = Mathf.FloorToInt(maxX / cellSize);
        int cz0 = Mathf.FloorToInt(minZ / cellSize);
        int cz1 = Mathf.FloorToInt(maxZ / cellSize);
        for (int cx = cx0; cx <= cx1; cx++)
        {
            for (int cz = cz0; cz <= cz1; cz++)
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
    /// 指定IDの建物データキャッシュを取得する。
    /// multi_agentスキーマ(visual_variant等を含む)でロードされているため、
    /// BuildingPlanManagerの編集モード初期値取得などで外観バリアントを読むために使われる。
    /// キャッシュに存在しない場合はnullを返す（まだロードされていないセル等）。
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
    /// 建物データをキャッシュに登録し、インフラ接続端子ノードの逆引きインデックス
    /// （buildingIdByInfraNodeId）を同期する。buildingDataCache への直接代入は禁止で、
    /// 必ずこのメソッドを通すこと（逆引きインデックスの整合性を保つため）。
    /// </summary>
    private void RegisterBuildingDataToCache(BuildingSummary buildingData)
    {
        // 旧データが別の端子ノード・区画を指していた場合は古い逆引きを外す
        // （端子ノード・区画は施設に対して不変のため通常は発生しないが、防御的に同期する）
        if (buildingDataCache.TryGetValue(buildingData.agent_id, out BuildingSummary oldData))
        {
            if (oldData.infra_node_id > 0 && oldData.infra_node_id != buildingData.infra_node_id)
            {
                buildingIdByInfraNodeId.Remove(oldData.infra_node_id);
            }
            if (oldData.parcel_id > 0 && oldData.parcel_id != buildingData.parcel_id
                && buildingIdsByParcel.TryGetValue(oldData.parcel_id, out HashSet<int> oldByParcel))
            {
                oldByParcel.Remove(buildingData.agent_id);
                if (oldByParcel.Count == 0) buildingIdsByParcel.Remove(oldData.parcel_id);
            }
        }

        buildingDataCache[buildingData.agent_id] = buildingData;

        if (buildingData.infra_node_id > 0)
        {
            buildingIdByInfraNodeId[buildingData.infra_node_id] = buildingData.agent_id;
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
    /// 指定した土地区画に建つ建物のagent_id集合を返す。
    /// 地区拡張ドラッグ中に、街路に面する区画上の建物へ地区色を波及させるために使う。
    /// 該当が無ければ空集合を返す。
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
    /// インフラ接続端子ノードID（agents.infra_node_id）から所属施設の建物データを逆引きする。
    /// ノードAPIは所属施設（owner）を公開しない片方向参照の設計のため、端子ノードの識別は
    /// このメソッドで行う（インフラパス敷設時のスナップ判定・端子マーカー表示・網互換判定用）。
    /// 施設が未ロード（bbox範囲外）の場合は false を返す。
    /// </summary>
    public bool TryGetInfraTerminalOwner(int nodeId, out BuildingSummary building)
    {
        building = null;
        if (nodeId <= 0) return false;
        return buildingIdByInfraNodeId.TryGetValue(nodeId, out int agentId)
            && buildingDataCache.TryGetValue(agentId, out building)
            && building != null;
    }

    protected override void UnloadCell(Vector2Int cellIndex)
    {
        if (!buildingIdsByCell.TryGetValue(cellIndex, out HashSet<int> ids)) return;

        foreach (int id in ids)
        {
            if (buildingObjectsCache.TryGetValue(id, out GameObject obj) && obj != null)
            {
                obj.SetActive(false);
            }
            // 範囲外セルの遅延生成リクエストは破棄する(ワーカーがロード範囲外に
            // アクティブな建物を復活させないため)。セルが再ロードされれば
            // マージロードが再度キューへ積み直す。
            deferredDetailBuildings.Remove(id);
        }
    }

    /// <summary>
    /// MeshRendererに設定されたマテリアルのシェーダーを、Z-fighting防止用の
    /// Custom/DepthOffsetSimpleLit に変更する。
    /// materialプロパティを介してインスタンスをクローンし、元のアセットには影響を与えない。
    /// </summary>
    /// <summary>
    private static void ApplyDepthOffsetShader(MeshRenderer renderer)
    {
        Utils.ApplyDepthOffsetShader(renderer, "BuildingManager");
    }

    /// <summary>
    /// 建物のフル詳細オブジェクトを作成または更新する。
    /// </summary>
    /// <param name="isDynamic">
    /// 超近景(Dynamic)セルの建物として生成するか。true の場合、メッシュ生成時に
    /// フェンス・地面パネルが地形に追従する。呼び出し側で確定済みのティアを渡すこと
    /// （LODロード中は cellBuildingLod が旧ティアのままなので、テーブル参照では正しい値が取れない）。
    /// </param>
    /// <param name="forceRebuild">
    /// true の場合、既存オブジェクトのプール再利用をスキップして必ずメッシュを再生成する。
    /// ティア変更時や dynamic 地形更新後の地形追従再生成に使用する。
    /// </param>
    private void CreateOrUpdateBuildingObject(BuildingSummary buildingData, bool isDynamic, bool forceRebuild = false)
    {
        GameObject oldBuilding = null;
        if (buildingObjectsCache.TryGetValue(buildingData.agent_id, out GameObject existingBuilding))
        {
            if (existingBuilding != null)
            {
                var existingIdInfo = existingBuilding.GetComponent<ObjectIdInfo>();
                int currentStatus = existingIdInfo != null ? existingIdInfo.status : 0;
                bool existingIsFar = existingIdInfo != null && existingIdInfo.isFarObject;
                // 遠景LODオブジェクト（白い箱）は近景では再利用しない。
                // メッシュの詳細度が全く異なるため、破棄して近景用に再生成する。
                // forceRebuild 時はメッシュの作り直しが目的なので再利用しない。
                if (!forceRebuild && currentStatus == buildingData.status && !existingIsFar)
                {
                    // プールから再利用: 非表示だったオブジェクトを再表示し、位置を更新
                    if (!existingBuilding.activeSelf)
                        existingBuilding.SetActive(true);
                    RegisterBuildingDataToCache(buildingData);
                    UpdateBuildingHeight(existingBuilding);
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
                // ティア変更やステータス変更時は、新しいメッシュを生成してから古いものを破棄する。
                // これにより、Destroy と Instantiate の間に生じる一瞬の消滅（ポッピング）を防ぐ。
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

        BuildingMeshResult meshResult = new BuildingMeshBuilder().Generate(buildingData, isDynamic);
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
            float buildingBaseHeight;
            ParcelSummary parcel = null;
            if (buildingData.parcel_id > 0)
            {
                ParcelManager parcelManager = UnityEngine.Object.FindAnyObjectByType<ParcelManager>();
                if (parcelManager != null)
                {
                    parcel = parcelManager.GetParcelSummary(buildingData.parcel_id);
                }
            }

            if (parcel != null && IsBuildingApplicableForAltitude(buildingData))
            {
                buildingBaseHeight = parcel.altitude + 0.01f;
            }
            else
            {
                float groundHeight = HeightmapTextureManager.GetTerrainHeight(new Vector3(cx, 1000f, cz));
                buildingBaseHeight = groundHeight + 0.01f;
            }
            buildingGO.transform.position = new Vector3(cx, buildingBaseHeight, cz);
            RegisterBuildingCell(buildingData.agent_id, buildingGO.transform.position);
        }

        UpdatePlannedIcon(buildingGO, buildingData);

        if (meshResult != null && meshResult.ColliderMesh != null)
        {
            // コライダーの物理データ生成（PhysXベイク）は処理負荷が高いため、別スレッドで非同期に実行する。
            StartCoroutine(SetColliderAsync(buildingGO, meshResult.ColliderMesh));
        }
        BuildingInputHandler inputHandler = buildingGO.AddComponent<BuildingInputHandler>();
        inputHandler.buildingId = buildingData.agent_id;

        VisualModeObject visualModeObject = buildingGO.AddComponent<VisualModeObject>();
        visualModeObject.objectType = InputObjectType.BUILDING;
        visualModeObject.useChildRenderers = true;
        visualModeObject.InitializeMaterials();
        visualModeObject.SetStatus(status);
        buildingGO.AddComponent<VisualBuildingObject>();

        var stats = buildingGO.AddComponent<VisualBuildingStats>();
        stats.InitFrom(buildingData);

        if (mr != null && (buildingData.status == Constants.STATUS_SCHEDULED_CONSTRUCTION || buildingData.status == Constants.STATUS_PENDING))
        {
            int flags = VisualModeHandler.Instance != null ? VisualModeHandler.Instance.GetFlags(InputObjectType.BUILDING) : VisualFlag.SHOW_PLAN;
            mr.enabled = (flags & VisualFlag.SHOW_PLAN) != 0;
        }

        // 辞書は即座に新しいオブジェクトに更新する。
        // これにより、短時間に同じIDで再描画が呼ばれてもオブジェクトが増殖しない。
        buildingObjectsCache[buildingData.agent_id] = buildingGO;
        MarkBuildingDetailed(buildingData.agent_id);
        // 詳細メッシュを生成したので、残っていた遅延リクエストは不要になる。
        deferredDetailBuildings.Remove(buildingData.agent_id);

        // 古いオブジェクトの破棄だけを数フレーム遅延させる。
        if (oldBuilding != null)
        {
            StartCoroutine(DestroyObjectAfterFrames(oldBuilding));
        }
    }

    /// <summary>
    /// 指定されたオブジェクトを数フレーム後に破棄する。
    /// 新しいオブジェクトのレンダリングが確定してから古いものを消すことで、
    /// 一瞬の消滅（ポッピング）を防ぐ。
    /// 辞書の更新は呼び出し元で即座に行うため、このコルーチンでは破棄のみを行う。
    /// </summary>
    private IEnumerator DestroyObjectAfterFrames(GameObject obj)
    {
        return Utils.DestroyObjectAfterFrames(this, obj);
    }

    /// <summary>
    /// 建物の計画中アイコンを作成・更新・削除する。
    /// 計画中(status=3)の建物に対して、建物の屋上の上に2Dアイコンを表示する。
    /// アイコンは建物オブジェクトの子として管理され、VisualModeObjectの表示制御の対象に含まれる。
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

        // 建物メッシュの高さを取得して、屋上の上に配置する
        float buildingHeight = 0f;
        MeshFilter mf = buildingGO.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            buildingHeight = mf.sharedMesh.bounds.size.y;
        }
        else if (buildingData != null && buildingData.floors > 0)
        {
            // メッシュがない場合は floors * 既定の床高(3.5m) で概算
            buildingHeight = (buildingData.floors ?? 0) * 3.5f;
        }

        iconGO.transform.localPosition = Vector3.up * (buildingHeight + plannedBuildingIconYOffset);
        iconGO.transform.localScale = Vector3.one * plannedBuildingIconScale;
    }

    public IEnumerator FetchBuilding(int agentId)
    {
        yield return ApiClient.Get<BuildingSummary>(
            $"/buildings/{agentId}",
            building =>
            {
                if (building != null)
                    CreateOrUpdateBuildingObject(building, IsBuildingInDynamicCell(building));
            },
            error => Debug.LogError($"FetchBuilding({agentId}) failed: {error}")
        );
    }

    /// <summary>
    /// 建物の center_point が属するセルが現在 Dynamic ティアかどうかを返す。
    /// cellBuildingLod が更新済みのタイミング（単発フェッチ等）でのみ正確。
    /// LoadMergedBuildings の最中はティア表が未更新のため、desiredTier を直接渡すこと。
    /// </summary>
    private bool IsBuildingInDynamicCell(BuildingSummary buildingData)
    {
        float[] cp = buildingData.centerPointArray;
        if (cp == null || cp.Length < 2) return false;
        Vector2Int cell = PositionToCell(new Vector3(cp[0], 0f, cp[1]));
        return GetCellLodTier(cell) == LodTier.Dynamic;
    }

    /// <summary>
    /// メッシュコライダーの物理データを別スレッド（Physics.BakeMesh）で非同期に生成し、
    /// ベイク完了後にコライダーコンポーネントに割り当てる。
    /// これにより、メインスレッドでの物理ベイクによる処理落ち（カクつき）を低減する。
    /// </summary>
    private IEnumerator SetColliderAsync(GameObject targetGO, Mesh mesh)
    {
        if (mesh == null) yield break;

        int meshId = mesh.GetInstanceID();
        Task bakeTask = Task.Run(() => Physics.BakeMesh(meshId, false));
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
    /// 指定されたセルの現在のLODティアを返す。
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
/// 建物一覧API（GET /buildings/）のレスポンス。
/// レスポンスボディは {"buildings": [BuildingSummary, ...]} の形式。
/// </summary>
[Serializable]
public class BuildingsListResponse
{
    public List<BuildingSummary> buildings;
}
