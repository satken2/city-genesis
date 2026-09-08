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
    /// 「この区画の上に建っている建物」を全建物走査なしで解決するために使う。
    /// buildingDataCache への登録は必ず RegisterBuildingDataToCache を通し、
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
        /// <summary>生成する描画LOD(Normal / Close)。Far は軽量なので遅延せず即時生成する。</summary>
        public DrawLod drawLod;
    }

    // ================================================================
    // 描画LOD(建物1棟ごとの詳細度。セルのデータティアとは独立)
    // ================================================================

    /// <summary>接写メッシュを持つ建物IDの集合。DrawLodConfig.MAX_CLOSE_BUILDINGS の母集団。</summary>
    private readonly HashSet<int> closeBuildingIds = new HashSet<int>();

    /// <summary>建物IDごとの境界球半径(フットプリント中心からの最遠点と高さから算出)。投影比の判定に使う。</summary>
    private readonly Dictionary<int, float> buildingBoundsRadius = new Dictionary<int, float>();

    /// <summary>描画LODを前回判定したときのカメラ位置。一定距離動いたときだけ再判定する。</summary>
    private Vector3 lastDrawLodCameraPos = new Vector3(float.NaN, 0f, 0f);

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
        RenderWorkScheduler.Start(this, DetailGenerationWorker(), "BuildingManager");
        // カメラ移動に応じて建物ごとの描画LODを再判定する常駐ワーカー。
        RenderWorkScheduler.Start(this, DrawLodReevaluationWorker(), "BuildingManager");
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
        lodCache.Clear();
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
            isDynamic = tier != LodTier.UltraFar;
        }

        Camera cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        Vector3 camFwd = cam != null ? GetHorizontalForward(cam) : Vector3.forward;

        foreach (int buildingId in new List<int>(buildingIds))
        {
            if (!buildingObjectsCache.TryGetValue(buildingId, out GameObject obj)) continue;
            if (obj == null) continue;

            // 遠景(Far)の箱は地形追従の造形を持たないので、Dynamicセルでも高さ調整だけで足りる。
            DrawLod currentDrawLod = DrawLodInfo.GetDrawLod(obj, DrawLod.Normal);
            if (isDynamic && currentDrawLod != DrawLod.Far)
            {
                if (buildingDataCache.TryGetValue(buildingId, out BuildingSummary buildingData))
                {
                    // メッシュ再生成は重いので視野内の建物のみ即時に行い、視野外は遅延キューへ
                    // 積む(視野に入ったら DetailGenerationWorker が現在の地形高さで作り直す)。
                    if (IsPositionInGenerationView(obj.transform.position, camPos, camFwd))
                    {
                        CreateOrUpdateBuildingObject(buildingData, isDynamic: true, forceRebuild: true, drawLod: currentDrawLod);
                    }
                    else
                    {
                        deferredDetailBuildings[buildingId] = new DeferredDetailRequest
                        {
                            data = buildingData,
                            isDynamic = true,
                            drawLod = currentDrawLod,
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

        lodCache.Clear();
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
        closeBuildingIds.Clear();
        buildingBoundsRadius.Clear();
        DrawLodStats.ResetCounts(DrawLodStats.Category.Building);
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
            if (c != null) RenderWorkScheduler.Stop(this, c);
        }
        activeLodCoroutines.Clear();

        var allBuildingCells = new HashSet<Vector2Int>(cellSets.farCells);
        var obsoleteRequests = new List<int>();
        foreach (var item in deferredDetailBuildings)
        {
            float[] point = item.Value.data.centerPointArray;
            if (point != null && point.Length >= 2 && !allBuildingCells.Contains(PositionToCell(new Vector3(point[0], 0f, point[1]))))
                obsoleteRequests.Add(item.Key);
        }
        foreach (int id in obsoleteRequests) deferredDetailBuildings.Remove(id);
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
            activeLodCoroutines.Add(RenderWorkScheduler.Start(this, LoadMergedBuildingsAndApplyCells(cellsToLoad, nearSet, dynamicSet, toUnload, lodUpdates, tierChangedBuildingCells), "BuildingManager"));
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

        BuildingsListResponse response = new BuildingsListResponse { buildings = new List<BuildingSummary>() };
        int cursor = 0;
        while (true)
        {
            BuildingsListResponse page = null;
            yield return ApiClient.GetBulk<BuildingsListResponse>(
                $"/buildings/?bbox={minX},{minZ},{maxX},{maxZ}&limit=500&cursor={cursor}", value => page = value,
                timingLabel: "BuildingManager page");
            if (page?.buildings == null) { response = null; break; }
            response.buildings.AddRange(page.buildings);
            if (!page.next_cursor.HasValue) break;
            if (page.next_cursor.Value <= cursor) { response = null; break; }
            cursor = page.next_cursor.Value;
            yield return null;
        }
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
        float tanHalfFov = DrawLodEvaluator.GetTanHalfFov(viewCam);

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
                            needsRebuild = isFarTransition;
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

                    // 描画LODはセルのティアとは別に、建物ごとの画面上の大きさで決める。
                    // Farセルは箱に固定し、Near/Dynamicセルでも遠くに小さく見える建物は箱にする。
                    DrawLod desiredDrawLod = DetermineDesiredDrawLod(b, desiredTier, viewCamPos, tanHalfFov);

                    if (desiredDrawLod == DrawLod.Far)
                    {
                        // Far(箱)は軽量なので視野に関わらず即時生成する(スカイライン維持)。
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
                            drawLod = desiredDrawLod,
                        };
                    }
                    else
                    {
                        CreateOrUpdateBuildingObject(b, desiredTier == LodTier.Dynamic, forceRebuild: true, drawLod: desiredDrawLod);
                        buildingVersionCache[b.agent_id] = b.version;
                    }
                }

                // フレーム予算: 目標FPSに基づく動的予算を超えたら次フレームに譲る。
                // これによりオブジェクト生成の集中によるカクつきを抑制する。
                if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
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
                    tanHalfFov = DrawLodEvaluator.GetTanHalfFov(viewCam);
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
                if (obj != null) DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Building, DrawLodInfo.GetDrawLod(obj, DrawLod.Normal), null);
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
            closeBuildingIds.Remove(id);
            buildingBoundsRadius.Remove(id);
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
                    DrawLod currentDrawLod = DrawLodInfo.GetDrawLod(obj, DrawLod.Normal);
                    if (cellBuildingLod.TryGetValue(cell, out LodTier tier) && tier != LodTier.UltraFar && currentDrawLod != DrawLod.Far)
                    {
                        // Dynamicセル: フェンス・地面パネルをロード完了後の地形に追従させるため、
                        // メッシュを強制的に再生成する。再生成は重いので視野内の建物のみ即時に行い、
                        // 視野外は遅延キューへ積む(視野に入ったらワーカーが現在の地形高さで作り直す)。
                        // 遠景の箱は地形追従の造形を持たないため高さ調整だけにする。
                        if (buildingDataCache.TryGetValue(id, out BuildingSummary buildingData))
                        {
                            if (IsPositionInGenerationView(obj.transform.position, camPos, camFwd))
                            {
                                CreateOrUpdateBuildingObject(buildingData, isDynamic: true, forceRebuild: true, drawLod: currentDrawLod);
                            }
                            else
                            {
                                deferredDetailBuildings[id] = new DeferredDetailRequest
                                {
                                    data = buildingData,
                                    isDynamic = true,
                                    drawLod = currentDrawLod,
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

            if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
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
    private bool IsDeferredCellReady(BuildingSummary data)
    {
        float[] point = data.centerPointArray;
        return point == null || point.Length < 2 || loadedCells.Contains(PositionToCell(new Vector3(point[0], 0f, point[1])));
    }

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
            while (deferredDetailBuildings.Count > 0 && budgetWatch.Elapsed.TotalMilliseconds <= PerformanceConfig.SliceBudgetMs)
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
                    // 一括取得の適用完了までは未登録セルの要求を取り除かない。
                    if (!IsDeferredCellReady(kvp.Value.data)) continue;
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

                // 接写の保持上限に達していれば通常で生成する(降格は次の再判定で投影比が決める)。
                DrawLod lod = req.drawLod == DrawLod.Close && closeBuildingIds.Count >= DrawLodConfig.MAX_CLOSE_BUILDINGS && !closeBuildingIds.Contains(bestId)
                    ? DrawLod.Normal
                    : (req.drawLod == DrawLod.Far ? DrawLod.Normal : req.drawLod);
                CreateOrUpdateBuildingObject(req.data, req.isDynamic, forceRebuild: true, drawLod: lod);
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
        if (buildingDataCache.TryGetValue(buildingId, out BuildingSummary data))
        {
            CreateFarBuildingObject(data);
            return;
        }
        if (buildingObjectsCache.TryGetValue(buildingId, out GameObject obj) && obj != null)
        {
            DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Building, DrawLodInfo.GetDrawLod(obj, DrawLod.Normal), null);
            lodCache.Park(obj);
        }
        buildingObjectsCache.Remove(buildingId);
        detailedBuildingIds.Remove(buildingId);
        closeBuildingIds.Remove(buildingId);
        buildingLastVisibleFrame.Remove(buildingId);
    }

    /// <summary>
    /// 建物IDを「フル詳細メッシュ保持中」として登録し、最終可視フレームを現在に更新する。
    /// CreateOrUpdateBuildingObject の生成・プール再利用の両経路から呼ぶ。
    /// </summary>
    private void MarkBuildingDetailed(int buildingId, DrawLod drawLod)
    {
        detailedBuildingIds.Add(buildingId);
        buildingLastVisibleFrame[buildingId] = Time.frameCount;
        if (drawLod == DrawLod.Close) closeBuildingIds.Add(buildingId);
        else closeBuildingIds.Remove(buildingId);
    }

    // ================================================================
    // 描画LODの判定と再判定ワーカー
    // ================================================================

    /// <summary>
    /// 建物の境界球半径(m)。フットプリントの中心からの最遠点と建物高さの合成で、投影比の判定に使う。
    /// 建物ごとにキャッシュし、version が変わっても形状は変わらない前提で使い回す
    /// (階数変更で多少ずれても、しきい値の粗さに比べれば無視できる)。
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
    /// 建物の望ましい描画LOD。Farセルは箱に固定し、Near/Dynamicセルでは投影比(画面上の大きさ)で決める。
    /// 既存オブジェクトの描画LODをヒステリシスの基準にする。
    /// </summary>
    private DrawLod DetermineDesiredDrawLod(BuildingSummary data, LodTier tier, Vector3 camPos, float tanHalfFov)
    {

        float[] cp = data.centerPointArray;
        if (cp == null || cp.Length < 2) return DrawLod.Normal;
        DrawLod current = DrawLod.Normal;
        if (buildingObjectsCache.TryGetValue(data.agent_id, out GameObject existing) && existing != null)
        {
            current = DrawLodInfo.GetDrawLod(existing, DrawLod.Normal);
        }
        float groundY = HeightmapTextureManager.GetTerrainHeight(new Vector3(cp[0], 1000f, cp[1]));
        float radius = GetBuildingBoundsRadius(data);
        float distance = Vector3.Distance(camPos, new Vector3(cp[0], groundY + radius * 0.5f, cp[1]));
        bool allowClose = closeBuildingIds.Contains(data.agent_id) || closeBuildingIds.Count < DrawLodConfig.MAX_CLOSE_BUILDINGS;
        return DrawLodEvaluator.EvaluateBySize(radius, distance, tanHalfFov, current, allowClose, Camera.main != null && Camera.main.orthographic);
    }

    /// <summary>
    /// カメラ移動に応じて、生成済みの建物の描画LODを再判定する常駐ワーカー。
    /// 一定フレームごと、かつカメラが一定距離動いたときだけ全建物を走査する(数千件の距離計算で1ms未満)。
    /// 変化があった建物は、箱化(Far)なら軽いので即時生成、詳細化(Normal/Close)は遅延キューへ積んで
    /// DetailGenerationWorker が視野優先・フレーム予算で生成する。生成が終わるまで旧オブジェクトは残る。
    /// </summary>
    private LodViewState drawViewState;

    private IEnumerator DrawLodReevaluationWorker()
    {
        while (true)
        {
            for (int i = 0; i < DrawLodConfig.REEVALUATE_INTERVAL_FRAMES; i++) yield return null;
            if (buildingObjectsCache.Count == 0) continue;

            Camera cam = Camera.main;
            if (cam == null) continue;
            Vector3 camPos = cam.transform.position;
            if (!drawViewState.Update(cam)) continue;
            float tanHalfFov = DrawLodEvaluator.GetTanHalfFov(cam);

            // 走査中に辞書を書き換えないよう、変更対象を先に集める。
            List<(int id, DrawLod lod)> changes = null;
            foreach (var kvp in buildingObjectsCache)
            {
                GameObject obj = kvp.Value;
                if (obj == null || !obj.activeSelf) continue;
                if (!buildingDataCache.TryGetValue(kvp.Key, out BuildingSummary data)) continue;
                if (!buildingCellById.TryGetValue(kvp.Key, out Vector2Int cell)) continue;
                if (!loadedCells.Contains(cell)) continue;
                LodTier tier = GetCellLodTier(cell);
                DrawLod current = DrawLodInfo.GetDrawLod(obj, DrawLod.Normal);
                DrawLod desired = DetermineDesiredDrawLod(data, tier, camPos, tanHalfFov);
                if (desired == current) continue;
                changes ??= new List<(int, DrawLod)>();
                changes.Add((kvp.Key, desired));
            }
            if (changes == null) continue;

            foreach (var (id, lod) in changes)
            {
                if (!buildingDataCache.TryGetValue(id, out BuildingSummary data)) continue;
                if (lod == DrawLod.Far)
                {
                    CreateFarBuildingObject(data);
                    buildingVersionCache[id] = data.version;
                }
                else
                {
                    bool isDynamic = buildingCellById.TryGetValue(id, out Vector2Int cell) && GetCellLodTier(cell) == LodTier.Dynamic;
                    deferredDetailBuildings[id] = new DeferredDetailRequest { data = data, isDynamic = isDynamic, drawLod = lod };
                }
            }
        }
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

    private readonly RenderObjectCache lodCache = new RenderObjectCache(256L * 1024 * 1024);

    private string BuildingRenderKey(BuildingSummary data, DrawLod lod)
    {
        ParcelSummary parcel = GetParcelManager()?.GetParcelSummary(data.parcel_id);
        float[] cp = data.centerPointArray;
        float radius = GetBuildingBoundsRadius(data);
        var bounds = new Rect(cp != null ? cp[0]-radius : 0, cp != null ? cp[1]-radius : 0, 2*radius, 2*radius);
        var parcelPolygon = parcel?.GetPolygonArray();
        if (parcelPolygon != null)
            foreach (float[] point in parcelPolygon)
            {
                bounds.xMin = Mathf.Min(bounds.xMin, point[0]); bounds.xMax = Mathf.Max(bounds.xMax, point[0]);
                bounds.yMin = Mathf.Min(bounds.yMin, point[1]); bounds.yMax = Mathf.Max(bounds.yMax, point[1]);
            }
        var terrain = Utils.CachedTerrainManager;
        string surface = terrain != null ? terrain.GetSurfaceStamp(bounds) : "none";
        return Newtonsoft.Json.JsonConvert.SerializeObject(new {
            data.agent_id, data.variant, data.bd_type, data.floors, data.status, data.status_reason,
            data.construction_progress, data.polygon, data.center_point, data.visual_variant,
            data.visual_variant2, data.visual_variant3, data.sub_buildings, data.infra_node_id, data.depot_node_id,
            parcelPolygon = parcel?.GetPolygonArray(), altitude = parcel?.altitude, lod, surface
        });
    }

    private bool RestoreBuildingLod(BuildingSummary data, DrawLod lod, string key)
    {
        buildingObjectsCache.TryGetValue(data.agent_id, out GameObject old);
        var identity = old != null ? old.GetComponent<RenderCacheIdentity>() : null;
        GameObject ready = identity != null && identity.key == key ? old : lodCache.Take(key);
        if (ready == null) return false;
        if (ready != old)
        {
            DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Building,
                old != null ? DrawLodInfo.GetDrawLod(old, DrawLod.Normal) : (DrawLod?)null, lod);
            lodCache.Park(old);
        }
        ready.SetActive(true);
        buildingObjectsCache[data.agent_id] = ready;
        RegisterBuildingDataToCache(data);
        UpdateBuildingHeight(ready);
        RegisterBuildingCell(data.agent_id, ready.transform.position);
        UpdatePlannedIcon(ready, data);
        if (lod == DrawLod.Far) { detailedBuildingIds.Remove(data.agent_id); closeBuildingIds.Remove(data.agent_id); }
        else MarkBuildingDetailed(data.agent_id, lod);
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
                // 既存が遠景LODオブジェクトかつステータス一致ならプールから再利用
                if (currentStatus == buildingData.status && existingIsFar && existing.GetComponent<RenderCacheIdentity>()?.key == renderKey)
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

        // 遠景はパーツを読まない箱(フットプリント押し出し)。フル詳細と同じ組み立てを走らせない。
        var farWatch = System.Diagnostics.Stopwatch.StartNew();
        BuildingMeshResult meshResult = new BuildingMeshBuilder().Generate(buildingData, false, DrawLod.Far);
        var renderIdentity = buildingGO.AddComponent<RenderCacheIdentity>();
        renderIdentity.key = renderKey;
        renderIdentity.mesh = meshResult?.Mesh;
        renderIdentity.colliderMesh = meshResult?.ColliderMesh;
        DrawLodStats.AddGenerationMs(DrawLodStats.Category.Building, farWatch.Elapsed.TotalMilliseconds);
        if (meshResult?.Mesh != null)
        {
            mf.mesh = meshResult.Mesh;
        }

        mr.sharedMaterial = GetFarBuildingMaterial();
        DrawLodInfo.Ensure(buildingGO).drawLod = DrawLod.Far;

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

        if (meshResult?.Mesh != null) buildingGO.AddComponent<FarBuildingMember>().Configure(mr, meshResult.Mesh);

        // 辞書は即座に新しいオブジェクトに更新する。
        // これにより、短時間に同じIDで再描画が呼ばれてもオブジェクトが増殖しない。
        buildingObjectsCache[buildingData.agent_id] = buildingGO;
        DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Building,
            oldBuilding != null ? DrawLodInfo.GetDrawLod(oldBuilding, DrawLod.Normal) : (DrawLod?)null, DrawLod.Far);

        // Far(箱)になったので、詳細メッシュのLRU台帳と遅延リクエストからは外す。
        detailedBuildingIds.Remove(buildingData.agent_id);
        closeBuildingIds.Remove(buildingData.agent_id);
        buildingLastVisibleFrame.Remove(buildingData.agent_id);
        deferredDetailBuildings.Remove(buildingData.agent_id);

        // 古いオブジェクトの破棄だけを数フレーム遅延させる。
        if (oldBuilding != null)
        {
            lodCache.Park(oldBuilding);
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
        buildingBoundsRadius.Remove(buildingData.agent_id);

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
    /// 「この区画の上に建っている建物」を全建物走査なしで解決するための汎用の逆引き。
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

    /// <summary>
    /// 接続端子ノードの移動確定後（PATCH /nodes/{id} 成功時）に呼ばれる。
    /// 端子位置から敷地内の線路を組み立てる車両基地（MODEL_RAIL_YARD）は、
    /// version が変わらなくても見た目がノード位置に依存するため、所属施設を
    /// 逆引きして新しい端子位置でメッシュを再生成する。他の model_type は
    /// 端子位置が見た目に影響しないので何もしない。
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
        CreateOrUpdateBuildingObject(building, IsBuildingInDynamicCell(building), forceRebuild: true, drawLod: GetCurrentDrawLod(building.agent_id));
    }

    /// <summary>生成済みオブジェクトの描画LOD(未生成なら Normal)。個別更新で現在の詳細度を維持するために使う。</summary>
    private DrawLod GetCurrentDrawLod(int buildingId)
    {
        if (buildingObjectsCache.TryGetValue(buildingId, out GameObject obj) && obj != null)
        {
            DrawLod lod = DrawLodInfo.GetDrawLod(obj, DrawLod.Normal);
            return lod == DrawLod.Far ? DrawLod.Normal : lod;
        }
        return DrawLod.Normal;
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
    /// Custom/DepthOffsetLit に変更する。
    /// materialプロパティを介してインスタンスをクローンし、元のアセットには影響を与えない。
    /// </summary>
    /// <summary>
    private static void ApplyDepthOffsetShader(MeshRenderer renderer)
    {
        Utils.ApplyDepthOffsetShader(renderer, "BuildingManager", materials =>
        {
            var owner = renderer.GetComponent<RenderCacheIdentity>();
            if (owner != null) owner.materials = materials;
        });
    }

    private static Material farBuildingMaterial;
    private static Material GetFarBuildingMaterial()
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
    /// <param name="drawLod">描画LOD(Normal / Close)。Far は CreateFarBuildingObject が担当する。</param>
    private void CreateOrUpdateBuildingObject(BuildingSummary buildingData, bool isDynamic, bool forceRebuild = false, DrawLod drawLod = DrawLod.Normal)
    {
        if (drawLod == DrawLod.Far || drawLod == DrawLod.Hidden) drawLod = DrawLod.Normal;
        string renderKey = BuildingRenderKey(buildingData, drawLod);
        if (RestoreBuildingLod(buildingData, drawLod, renderKey)) return;
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
                // 遠景LODオブジェクト（箱）は近景では再利用しない。
                // メッシュの詳細度が全く異なるため、破棄して近景用に再生成する。描画LODが違う場合も同様。
                // forceRebuild 時はメッシュの作り直しが目的なので再利用しない。
                if (!forceRebuild && currentStatus == buildingData.status && !existingIsFar && sameDrawLod)
                {
                    // プールから再利用: 非表示だったオブジェクトを再表示し、位置を更新
                    if (!existingBuilding.activeSelf)
                        existingBuilding.SetActive(true);
                    RegisterBuildingDataToCache(buildingData);
                    UpdateBuildingHeight(existingBuilding);
                    RegisterBuildingCell(buildingData.agent_id, existingBuilding.transform.position);
                    MarkBuildingDetailed(buildingData.agent_id, drawLod);
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

        var genWatch = System.Diagnostics.Stopwatch.StartNew();
        BuildingMeshResult meshResult = new BuildingMeshBuilder().Generate(buildingData, true, drawLod, separateExterior: true);
        var renderIdentity = buildingGO.AddComponent<RenderCacheIdentity>();
        renderIdentity.key = renderKey;
        renderIdentity.mesh = meshResult?.Mesh;
        renderIdentity.colliderMesh = meshResult?.ColliderMesh;
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
            RenderWorkScheduler.Start(this, SetColliderAsync(buildingGO, meshResult.ColliderMesh), "BuildingManager");
        }
        BuildingInputHandler inputHandler = buildingGO.AddComponent<BuildingInputHandler>();
        inputHandler.buildingId = buildingData.agent_id;

        VisualModeObject visualModeObject = buildingGO.AddComponent<VisualModeObject>();
        visualModeObject.objectType = InputObjectType.BUILDING;
        visualModeObject.useChildRenderers = true;
        visualModeObject.InitializeMaterials();
        visualModeObject.SetStatus(status);
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

        // 辞書は即座に新しいオブジェクトに更新する。
        // これにより、短時間に同じIDで再描画が呼ばれてもオブジェクトが増殖しない。
        buildingObjectsCache[buildingData.agent_id] = buildingGO;
        MarkBuildingDetailed(buildingData.agent_id, drawLod);
        DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Building,
            oldBuilding != null ? DrawLodInfo.GetDrawLod(oldBuilding, DrawLod.Normal) : (DrawLod?)null, drawLod);
        // 詳細メッシュを生成したので、残っていた遅延リクエストは不要になる。
        deferredDetailBuildings.Remove(buildingData.agent_id);

        // 古いオブジェクトの破棄だけを数フレーム遅延させる。
        if (oldBuilding != null)
        {
            lodCache.Park(oldBuilding);
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
                    CreateOrUpdateBuildingObject(building, IsBuildingInDynamicCell(building), drawLod: GetCurrentDrawLod(building.agent_id));
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
    public int? next_cursor;
    public List<BuildingSummary> buildings;
}
