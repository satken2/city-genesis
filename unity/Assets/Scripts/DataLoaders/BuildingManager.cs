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
/// 建物情報を扱うマネージャー。
/// バックエンドの GET /buildings/ エンドポイントから BuildingSummary（single_agentスキーマ＋座標情報）を取得し、
/// Unity空間に建物オブジェクトを生成する。
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
    /// インフラ接続端子ノードID（agents.infra_node_id）→ 建物agent_id の逆引きインデックス。
    /// ノードAPIは所属施設（owner）を公開しない「agent→nodeの片方向参照」の設計のため、
    /// フロントは bbox ロード済みの建物データからこのインデックスで端子ノードを識別する
    /// （インフラパス敷設時のスナップ対象判定・端子マーカー表示・網互換判定に使う）。
    /// buildingDataCache への登録は必ず RegisterBuildingDataToCache を通し、
    /// 削除は SyncBuildingsInCells / ClearAllCells で同期すること。
    /// </summary>
    private Dictionary<int, int> buildingIdByInfraNodeId = new Dictionary<int, int>();

    /// <summary>
    /// 車両基地の接続端子ノードID（agents.depot_node_id）→ 建物agent_id の逆引きインデックス。
    /// 電力ビットを持たない車両基地の端子（LRT車両基地）はノードの type だけでは施設の端子と判別できないため、
    /// 線路の終端判定（車止めの有無）でこれを引く。管理規約は buildingIdByInfraNodeId と同じ。
    /// </summary>
    private Dictionary<int, int> buildingIdByDepotNodeId = new Dictionary<int, int>();

    /// <summary>
    /// 土地区画ID（agents.parcel_id）→ その区画に建つ建物agent_idの集合の逆引きインデックス。
    /// 「この区画の上に建っている建物」を全建物走査なしで解決するために使う。
    /// buildingDataCache への登録は必ず RegisterBuildingDataToCache を通し、
    /// 削除は SyncBuildingsInCells / ClearAllCells で同期すること（buildingIdByInfraNodeId と同じ管理規約）。
    /// </summary>
    private Dictionary<int, HashSet<int>> buildingIdsByParcel = new Dictionary<int, HashSet<int>>();

    /// <summary>
    /// 港（MODEL_PORT）の建物agent_idの集合。埋立地の中身が変わったときに作り直す港を、全建物走査なしで引くために使う。
    /// 管理規約は buildingIdByInfraNodeId と同じ(登録は RegisterBuildingDataToCache、削除は SyncBuildingsInCells / ClearAllCells)。
    /// </summary>
    private readonly HashSet<int> portBuildingIds = new HashSet<int>();

    /// <summary>
    /// バックエンドから取得した建物のversionをキャッシュする。
    /// 差分更新時にバックエンドのversionと比較し、変更があった場合のみオブジェクトを再作成する。
    /// 視野外のため生成を遅延した建物にはversionをキャッシュしない
    /// (次回のマージロードでも「未生成」と判定させ、遅延キューを最新データで更新し続けるため)。
    /// </summary>
    private Dictionary<int, int> buildingVersionCache = new Dictionary<int, int>();
    private readonly HashSet<int> pendingExplosions = new HashSet<int>();
    private readonly Dictionary<int, int> explodedVersions = new Dictionary<int, int>();
    private int explosionGeneration;

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

    /// <summary>建物ID→遅延生成リクエスト。同一IDは最新のリクエストで上書きされる。</summary>
    private readonly Dictionary<int, DeferredDetailRequest> deferredDetailBuildings = new Dictionary<int, DeferredDetailRequest>();

    /// <summary>現在フル詳細メッシュ(非Far)のオブジェクトを持つ建物IDの集合。LRU上限の母集団。</summary>
    private readonly HashSet<int> detailedBuildingIds = new HashSet<int>();

    /// <summary>建物IDごとに「最後に視野内で観測されたフレーム番号」。LRU破棄の優先度に使う。</summary>
    private readonly Dictionary<int, int> buildingLastVisibleFrame = new Dictionary<int, int>();

    /// <summary>遅延生成キューへの投入ごとに進める版。遅延生成ワーカーの空回り判定に使う。</summary>
    private int deferredRevision;

    /// <summary>
    /// LRU破棄の候補を最終可視フレームの古い順に並べたもの。破棄のたびに全件を走査しないよう、
    /// 尽きたときか VISIBILITY_SWEEP_INTERVAL_FRAMES ごとに作り直し、取り出し時に視野内かどうかを確かめ直す。
    /// </summary>
    private readonly List<(int lastSeen, int id)> evictionOrder = new List<(int lastSeen, int id)>();
    private int evictionCursor;
    private int evictionOrderFrame;


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

    // ================================================================
    // セル離脱の分割アンロード
    // ================================================================

    /// <summary>
    /// 非表示化(退避キャッシュへの Park)を待つ離脱セル→登録世代。CellUnloadWorker がフレーム予算内で消化する。
    /// 再入場で取り除き、再登録で世代を進める(走査中のスナップショットを古いものとして捨てるため)。
    /// </summary>
    private readonly Dictionary<Vector2Int, int> pendingUnloadCells = new Dictionary<Vector2Int, int>();
    private int unloadGeneration;

    /// <summary>
    /// 表示中の建物オブジェクトを持ちうる、アンロード中でないセル。loadedCells と別に持つのは、
    /// 取消されたロードバッチが適用前に生成・再表示した建物を、次のバッチで確実にアンロードするため。
    /// </summary>
    private readonly HashSet<Vector2Int> cellsWithShownObjects = new HashSet<Vector2Int>();

    /// <summary>loadedCells を書き換えるたびに進める版。遅延生成ワーカーの空回り判定に使う。</summary>
    private int loadedCellsRevision;

    /// <summary>CellUnloadWorker が1セル分の対象IDを写し取る再利用リスト。yield を跨いで HashSet を列挙しないために使う。</summary>
    private readonly List<int> unloadScanIds = new List<int>();

    // ================================================================
    // 取得範囲の差分化
    // ================================================================

    /// <summary>
    /// 次のバッチで取り直すロード済みセル。カメラ由来のバッチは未ロード・ここに記録されたセル・ティア変化セルだけを取得する。
    /// 記録は MarkLoadedCellsStale が要求時に行い、取得が成功して適用されるまで残す(取消されたバッチでは消さない)。
    /// </summary>
    private readonly HashSet<Vector2Int> staleBuildingCells = new HashSet<Vector2Int>();

    /// <summary>MarkLoadedCellsStale の呼び出しごとに進める版。取得中に再取得要求を跨いだバッチの結果を古いとみなすために使う。</summary>
    private int staleMarkRevision;

    // ================================================================
    // 地形タイル到着時の作り直し判定
    // ================================================================

    /// <summary>
    /// 建物ID→オブジェクトを現在の地形に合わせたときの地形署名(TerrainManager.GetTerrainSignature)。
    /// タイルの LRU 復元でも OnTerrainTileReady が届くので、署名が変わった建物だけを高さ補正・作り直しの対象にする。
    /// 記録が無い建物は常に不一致として扱う。
    /// </summary>
    private readonly Dictionary<int, long> buildingTerrainSignatures = new Dictionary<int, long>();

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
        // 公共施設ハイライトの光オーバーレイ(建物データを参照する常駐描画)を用意する。
        PublicFacilityGlowOverlay.Ensure();
        // 視野外のため生成を遅延した詳細メッシュを消化する常駐ワーカー。
        RenderWorkScheduler.Start(this, DetailGenerationWorker(), "BuildingManager", () => HasPendingInitialDetails);
        // カメラ移動に応じて建物ごとの描画LODを再判定する常駐ワーカー。
        RenderWorkScheduler.Start(this, DrawLodReevaluationWorker(), "BuildingManager", () => false);
        // 離脱セルの建物をフレーム予算内で退避キャッシュへ移す常駐ワーカー。
        RenderWorkScheduler.Start(this, CellUnloadWorker(), "BuildingManager", () => false);
    }

    /// <summary>LRU用の可視状態スイープの前回カメラ状態。視野は向きに依存するので回転も見る Update で判定する。</summary>
    private LodViewState sweepViewState;

    private void Update()
    {
        // LRU用の可視状態スイープ。毎フレームだと無駄なので一定間隔で行う。
        // 詳細メッシュを持つ建物のうち視野内のものの「最終可視フレーム」を更新する。
        // カメラ静止中は省く。静止中に生成された建物は MarkBuildingDetailed で最終可視フレームが入るため、LRU順は崩れない。
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
            // 表示中の建物は常にアクティブ(アンロードは退避キャッシュへ移す)。非アクティブは防御的な扱いで、
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
        BuildingRoadAccessIndex.Clear();
        lodCache.Clear();
        TerrainManager.OnTerrainTileReady -= OnTerrainTileReadyHandler;
        ReclaimedLandManager.OnContentChanged -= OnReclaimedLandsChanged;
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

        // ハンドラ内ではメッシュを作り直さない(1セル分の同期再生成がスパイクになるため)。
        // 高さだけ即時に合わせ、地形追従の造形は遅延キューで視野優先・予算内に作り直す。
        // 以下の処理は buildingIdsByCell を変更しないので、集合を直接列挙してよい。
        foreach (int buildingId in buildingIds)
        {
            if (!buildingObjectsCache.TryGetValue(buildingId, out GameObject obj)) continue;
            if (obj == null) continue;
            if (!buildingDataCache.TryGetValue(buildingId, out BuildingSummary buildingData)) continue;

            // LRU 復元で同じ版のタイルが再公開されただけなら、何もしない。
            bool known = TryGetBuildingTerrainSignature(buildingData, out long signature);
            if (known && buildingTerrainSignatures.TryGetValue(buildingId, out long recorded) && recorded == signature) continue;

            UpdateBuildingHeight(obj);
            // 遠景(Far)の箱は地形追従の造形を持たないので、Dynamicセルでも高さ調整だけで足りる。
            DrawLod currentDrawLod = DrawLodInfo.GetDrawLod(obj, DrawLod.Normal);
            if (isDynamic && currentDrawLod != DrawLod.Far)
                QueueDeferredDetail(buildingId, buildingData, true, currentDrawLod);

            if (known) buildingTerrainSignatures[buildingId] = signature;
        }
    }

    /// <summary>
    /// 建物の見た目が依存する範囲(建物の境界円と敷地ポリゴンの外接矩形)。再利用キーの地形版と、地形署名の両方に使う。
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

    /// <summary>TerrainManager が無い(破棄済みを含む)ときは false。呼び出し側は「不明」として作り直しを省かないこと。</summary>
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

    /// <summary>オブジェクトを現在の地形で生成・復元・高さ補正したときに呼び、その時点の地形署名を記録する。</summary>
    private void RecordBuildingTerrainSignature(BuildingSummary data)
    {
        if (TryGetBuildingTerrainSignature(data, out long signature)) buildingTerrainSignatures[data.agent_id] = signature;
        else buildingTerrainSignatures.Remove(data.agent_id);
    }

    public override void ClearAllCells()
    {
        // 旧データの取得バッチが後から適用されると、そのセルがロード済み扱いになり差分取得で取り直されなくなるので止める。
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
        closeBuildingIds.Clear();
        buildingBoundsRadius.Clear();
        pendingUnloadCells.Clear();
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
    /// ロード済みの全セルを「要再取得」にする。データ再取得(CellDataLoader.ReloadAllData)の要求時に同期で呼ぶ。
    /// 記録はバッチが取消されても残り、そのセルの取得が成功して適用されたときに消える。
    /// </summary>
    public void MarkLoadedCellsStale()
    {
        staleBuildingCells.UnionWith(loadedCells);
        staleMarkRevision++;
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
        StopActiveLodCoroutines();

        var allBuildingCells = new HashSet<Vector2Int>(cellSets.farCells);
        // 常駐外へ出たセルは再入場時に未ロードとして取り直されるので、記録は要らない。
        staleBuildingCells.IntersectWith(allBuildingCells);

        // アンロード途中で常駐へ戻ったセルは非表示化を中断する。退避しきれなかった表示物が残るので、
        // このバッチが取消されても次のバッチで回収できるよう、表示物セルとして記録し直す。
        if (pendingUnloadCells.Count > 0)
        {
            foreach (var cell in cellSets.farCells)
                if (pendingUnloadCells.Remove(cell)) cellsWithShownObjects.Add(cell);
        }

        RemoveDeferredRequestsOutside(allBuildingCells);
        var nearSet = new HashSet<Vector2Int>(cellSets.nearCells);
        var dynamicSet = new HashSet<Vector2Int>(cellSets.dynamicCells);

        // ロード済みセルに加え、取消されたバッチが適用前に表示したセル(loadedCells に入っていない)も対象にする。
        var toUnload = new List<Vector2Int>();
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

        // 取得するのは未ロード・要再取得・ティア変化のセルだけ。それ以外のロード済みセルは受信済みのデータと
        // オブジェクトをそのまま使う(バックエンドの変化は ReloadAllData が MarkLoadedCellsStale で記録する)。
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
    /// 新しいセルの建物を生成してから不要セルを非表示にする。
    /// カメラ移動時に古いセルを先に消すと、API待ち時間のあいだ都市が空白になるため、
    /// ロード成功を確認してから可視セル集合を切り替える。
    /// </summary>
    private IEnumerator LoadMergedBuildingsAndApplyCells(List<Vector2Int> cellsToLoad, HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet, List<Vector2Int> cellsToUnload, Dictionary<Vector2Int, LodTier> lodUpdates, HashSet<Vector2Int> tierChangedBuildingCells, int requestedStaleRevision)
    {
        bool succeeded = false;
        // StartCoroutine ではなく IEnumerator を直接 yield return する。
        // StartCoroutine で起動した子はカメラ移動時の親の StopCoroutine では停止せず、
        // 旧バッチの建物生成が新バッチと並走してしまうため。
        yield return LoadMergedBuildings(cellsToLoad, nearSet, dynamicSet, tierChangedBuildingCells, success => succeeded = success);

        if (!succeeded) yield break;

        ApplyLodCellChanges(cellsToLoad, cellsToUnload, lodUpdates, requestedStaleRevision);
    }

    /// <summary>
    /// 取得に成功したセルをロード済みにし、離脱セルをアンロードする。requestedStaleRevision はバッチ開始時の
    /// staleMarkRevision で、取得中に再取得要求があったなら受信データは要求前のものかもしれないので、取得セルを要再取得に残す。
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

        // マージBBOXは常駐セル集合の外接矩形なので、常駐外セルの建物も一括取得に含まれて遅延キューへ積まれる。
        // そのセルは登録されずワーカーが永久に選出しないため、登録集合が確定したここで除く。
        RemoveDeferredRequestsOutside(loadedCells);

        // アラートアイコンの高さは建物GOのメッシュ高さで決まるため、建物の生成が済んだここで再適用を要求する。
        // 地形はバッチ開始前にロード・追従済みなので、地面高さもこの時点で正しい。
        AlertManager.Instance?.RefreshPositions();
    }

    /// <summary>中心セルが cells に含まれない遅延生成要求を取り除く。中心点を持たない要求は残す。</summary>
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
    /// 取得セル群を少数の矩形(SplitFetchRects)に分けて建物を一括取得する。
    /// 各建物の位置からLOD tierを判定し、Nearならフル詳細、Farなら白い箱で処理する。
    /// どれか1つの矩形の取得に失敗したら、全体を失敗として何も適用しない。
    /// </summary>
    private IEnumerator LoadMergedBuildings(List<Vector2Int> cells, HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet, HashSet<Vector2Int> tierChangedBuildingCells, Action<bool> onComplete = null)
    {
        // 矩形の境界上の建物は隣の矩形にも届くので、IDで重複を除く。
        BuildingsListResponse response = new BuildingsListResponse { buildings = new List<BuildingSummary>() };
        var fetchedIds = new HashSet<int>();
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
                if (page?.buildings == null) { response = null; break; }
                RenderWorkScheduler.CountEvent("building.received", page.buildings.Count);
                foreach (var b in page.buildings)
                    if (b != null && fetchedIds.Add(b.agent_id)) response.buildings.Add(b);
                if (!page.next_cursor.HasValue) break;
                if (page.next_cursor.Value <= cursor) { response = null; break; }
                cursor = page.next_cursor.Value;
                yield return null;
            }
            if (response == null) break;
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
        bool viewOrtho = viewCam != null && viewCam.orthographic;

        // 矩形の端数や外接矩形への切替で、取得対象外のセル(常駐外やロード済みで取り直さないセル)の建物も届く。
        // それらは生成しない(常駐外に作るとどのセル集合にも属さず残り、ロード済みセルは既存のままでよい)。
        var fetchSet = new HashSet<Vector2Int>(cells);

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

                Vector2Int buildingCell = TerrainManager.WorldToCell(buildingPos);
                if (!fetchSet.Contains(buildingCell))
                {
                    RenderWorkScheduler.CountEvent("building.outsideFetchSkipped");
                }
                // 爆破前に開始した一括取得が遅れて届いても、瓦礫を元の建物へ戻さない。
                else if (!explodedVersions.TryGetValue(b.agent_id, out int explodedVersion) || b.version >= explodedVersion)
                {
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
                            RenderWorkScheduler.CountEvent("building.dataUnchanged");
                            // 表示中の建物は常にアクティブ(アンロードは退避キャッシュへ移す)。再表示は防御的な扱い。
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
                        // ステータス変化の検知とエフェクト再生
                        if (buildingDataCache.TryGetValue(b.agent_id, out BuildingSummary oldBuildingData))
                        {
                            if (oldBuildingData.status != b.status)
                            {
                                float groundHeight = Utils.GetGroundHeight(new Vector3(buildingPos.x, 1000f, buildingPos.z));
                                Vector3 effectPos = new Vector3(buildingPos.x, groundHeight, buildingPos.z);
                                TriggerBuildingStatusEffect(effectPos, oldBuildingData.status, b.status);
                            }
                        }

                        // 新規またはversion変更/ティア変更: オブジェクトを再作成。
                        // forceRebuild=true でプール再利用をスキップし、必ずメッシュを再生成する。
                        // isDynamic はこの時点で確定している desiredTier から渡す
                        // （cellBuildingLod はロード完了後の ApplyLodCellChanges まで旧ティアのままなので参照しない）。
                        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"BuildingManager: 建物 {b.agent_id} の再描画を開始 (version {cachedVersion} -> {b.version}, tier -> {desiredTier})");

                        // 描画LODはセルのティアでは固定せず、建物ごとの投影比(画面上の大きさ)だけで決める(E_LOD処理.md の方針)。
                        DrawLod desiredDrawLod = DetermineDesiredDrawLod(b, viewCamPos, tanHalfFov, viewOrtho);

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
                                bool keepsOldObject = buildingObjectsCache.TryGetValue(b.agent_id, out GameObject deferredOld) && deferredOld != null;
                                RegisterBuildingCell(b.agent_id, buildingPos, shown: keepsOldObject);
                            }
                            QueueDeferredDetail(b.agent_id, b, desiredTier == LodTier.Dynamic, desiredDrawLod);
                        }
                        else
                        {
                            CreateOrUpdateBuildingObject(b, desiredTier == LodTier.Dynamic, forceRebuild: true, drawLod: desiredDrawLod);
                            buildingVersionCache[b.agent_id] = b.version;
                        }
                    }
                }

                // 生成スライスを超えたら他ワーカーへ譲り、共通予算内なら同じフレームで再開する。
                // これによりオブジェクト生成の集中によるカクつきを抑制する。
                if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                {
                    // 予算スライスの区切りで詳細メッシュの個数上限を適用する
                    // (建物1件ごとに全走査すると走査コストが支配的になるため)。
                    EvictDetailedBuildingsOverCap(viewCamPos, viewCamFwd, justCreatedId: b.agent_id);
                    yield return RenderWorkScheduler.Continue;
                    stopwatch.Restart();
                    // 中断がフレームを跨ぐ場合もあるため、カメラ状態を取り直す。
                    viewCam = Camera.main;
                    viewCamPos = viewCam != null ? viewCam.transform.position : Vector3.zero;
                    viewCamFwd = viewCam != null ? GetHorizontalForward(viewCam) : Vector3.forward;
                    tanHalfFov = DrawLodEvaluator.GetTanHalfFov(viewCam);
                    viewOrtho = viewCam != null && viewCam.orthographic;
                }
            }
        }

        // ループ完了後にも個数上限を適用する(最後のスライス分)。
        EvictDetailedBuildingsOverCap(viewCamPos, viewCamFwd, justCreatedId: 0);

        // レスポンスに含まれない建物を削除
        SyncBuildingsInCells(cells, receivedIds);

        onComplete?.Invoke(true);
    }

    /// <summary>取得矩形の上限。超えるなら外接矩形1つで取る(要求の往復が1フレーム以上ずつ積み重なるため)。</summary>
    private const int MAX_FETCH_RECTS = 12;

    /// <summary>
    /// 取得セル群を覆う矩形群(両端を含むセル座標)を返す。差分取得のセル群は L 字や弧になり、外接矩形1つでは
    /// 取り直さないロード済みセルまで受信・デコードしてしまうため。z 行ごとに x の連続区間を作り、
    /// 同じ x 区間が連続する行は1つの矩形にまとめる。
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
            // 視野ゲート・LRU関連の台帳も同期して外す
            detailedBuildingIds.Remove(id);
            buildingLastVisibleFrame.Remove(id);
            deferredDetailBuildings.Remove(id);
            buildingVersionCache.Remove(id);
            closeBuildingIds.Remove(id);
            buildingBoundsRadius.Remove(id);
            buildingTerrainSignatures.Remove(id);
            buildingGeometryRevisions.Remove(id);
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
                    Vector2Int cell = TerrainManager.WorldToCell(obj.transform.position);
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
                                QueueDeferredDetail(id, buildingData, true, currentDrawLod);
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
                yield return RenderWorkScheduler.Continue;
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

    /// <summary>要求の建物のセルがロード完了集合に入っているか。未登録セルの要求は選出せず、削除もしない。</summary>
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
                // ワーカーが選出できない未登録セルの要求は待たない。取得中のセルは一括取得の処理自体が完了判定を塞ぐ。
                if (!IsDeferredCellReady(request.Value.data)) continue;
                if (!capReached) return true;
                if (!buildingDataCache.TryGetValue(request.Key, out var data)) continue;
                if (ScoreDeferredBuilding(data, cam.transform.position, GetHorizontalForward(cam)) < DEFERRED_OUT_OF_VIEW_PENALTY) return true;
            }
            // 上限超過で保留された視野外の建物は、カメラ移動後に通常のLOD処理で生成する。
            return false;
        }
    }

    /// <summary>上位候補の枠数。1回のキュー走査でこの件数まで取り出し、走査コストを償却する。</summary>
    private const int DEFERRED_TOP_CANDIDATES = 32;

    /// <summary>遅延生成ワーカーの待機を解く条件の署名。キュー・詳細数・ロード済みセル・上限のどれかが変われば変わる。</summary>
    private int DeferredWorkSignature()
    {
        unchecked
        {
            int h = deferredRevision;
            h = h * 31 + deferredDetailBuildings.Count;
            h = h * 31 + detailedBuildingIds.Count;
            h = h * 31 + loadedCells.Count;
            h = h * 31 + loadedCellsRevision;
            return h * 31 + maxDetailedBuildingObjects;
        }
    }

    /// <summary>
    /// 遅延生成キュー(deferredDetailBuildings)を消化する常駐ワーカー。
    /// フレーム予算内で「視野内かつカメラに近い」建物から順に詳細メッシュを生成する。視野外の建物は、詳細メッシュ数が
    /// 上限(maxDetailedBuildingObjects)に達していない限りアイドル的に生成する(振り向き時のポップイン軽減)。
    /// 上限に達している場合、視野外の詳細化は保留してキューに残し、Farで足りる要求だけを処理する。
    /// 候補は1回の走査で上位 DEFERRED_TOP_CANDIDATES 件を選び、使い切るか視点が変わるまで使い回す。
    /// 進められる要求が無ければ、キュー・台帳・カメラのどれかが変わるまで走査しない。
    /// </summary>
    private IEnumerator DetailGenerationWorker()
    {
        // 状態はすべてローカルに置く(テストが途中でワーカーを作り直すので、フィールドだと古い状態が残る)。
        var budgetWatch = new System.Diagnostics.Stopwatch();
        var topScore = new float[DEFERRED_TOP_CANDIDATES];
        var topId = new int[DEFERRED_TOP_CANDIDATES];
        int topCount = 0, topCursor = 0, topRevision = 0;
        // 今の候補群で生成・除去が1件でも起きたか。起きないまま使い切ったら選び直さずに待機する
        // (上限待ちの保留だけが並ぶとき、同じ候補を選び直す走査を繰り返さないため)。
        bool topUseful = false;
        LodViewState topView = default;
        bool stalled = false;
        int stalledSignature = 0;
        LodViewState stalledView = default;
        while (true)
        {
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
            // 視点が変わると処理順も保留の判定も変わるので、候補を選び直す。
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
                    if (topCount > 0 && !topUseful && topRevision == deferredRevision)
                    {
                        exhausted = true;
                        break;
                    }
                    topCount = SelectTopDeferredBuildings(camPos, fwd, topScore, topId);
                    topCursor = 0;
                    topRevision = deferredRevision;
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

                // ロード範囲外のセルに属する建物は生成しない。キュー投入からここまでの間に
                // セルがアンロードされた場合に生成すると、非表示のはずの建物がアクティブな
                // GameObjectとして復活してしまう。セルが再ロードされればマージロードが再度
                // 遅延キューへ積み直す。
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

                // 待機中にデータが変わり得るため、最新データと現在のカメラで判定し直す。
                bool isDynamic = IsBuildingInDynamicCell(latest);
                DrawLod lod = DetermineDesiredDrawLod(latest, camPos, tanHalfFov, ortho);
                // 上限到達中の視野外の詳細化は保留してキューに残し、後ろの候補(Farで足りる要求など)へ進む。
                // 候補群の外で保留が続く Far で足りる要求は、カメラ移動時に描画LOD再判定が箱にして消化する。
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

            // 進められる要求が無かった(候補選び以降の投入も無い)なら、条件が変わるまで待機する。
            if (!progressed && exhausted && topRevision == deferredRevision)
            {
                stalled = true;
                stalledSignature = DeferredWorkSignature();
                stalledView = default;
                stalledView.Update(cam);
            }
            yield return progressed ? RenderWorkScheduler.Continue : null;
        }
    }

    /// <summary>
    /// 遅延キューから処理順スコアの上位を最大 topId.Length 件、昇順に選ぶ(同点は先着を優先)。
    /// セル未ロードの要求は選ばない。center_point 欠損(スコア float.MaxValue)の要求も枠に空きがあれば選ぶ。
    /// </summary>
    private int SelectTopDeferredBuildings(Vector3 camPos, Vector3 camForwardHorizontal, float[] topScore, int[] topId)
    {
        int capacity = topId.Length;
        int count = 0;
        foreach (var kvp in deferredDetailBuildings)
        {
            if (!IsDeferredCellReady(kvp.Value.data)) continue;
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
    /// 候補は evictionOrder から順に取り出し、並べ直しは1回の呼び出しで高々1回にする。
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
                // 並べた後に視野へ入った建物は破棄しない。
                if (buildingObjectsCache.TryGetValue(id, out GameObject obj) && obj != null && obj.activeInHierarchy
                    && IsPositionInGenerationView(obj.transform.position, camPos, camForwardHorizontal)) continue;
                EvictDetailedBuilding(id);
                break;
            }
        }
    }

    /// <summary>
    /// LRU破棄の候補を最終可視フレームの古い順に並べ直す。オブジェクトを失った台帳残骸を先頭に置き、
    /// アクティブで視野内のものは除く。非アクティブのもの(防御的な扱い)は視野に関わらず候補にする。
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
    /// 詳細メッシュ建物をLRU破棄する。表示可能なセルなら Far(箱)に置き換え、詳細メッシュは退避キャッシュへ移る
    /// (カメラが近づけば描画LOD再判定が詳細化を要求する)。それ以外はオブジェクトを退避して version を消し、
    /// マージロードの差分判定で「未生成」として扱わせる。データキャッシュとセル逆引きは保持する。
    /// </summary>
    private void EvictDetailedBuilding(int buildingId)
    {
        // 表示可能なセルは、アンロード待ちでなく、ロード済みか取得中のバッチが表示物を置いたセル。
        // 差分取得ではロード済みセルを取り直さないので、ここで箱を置かないと再入場まで建物が消えたままになる。
        // 取得中のバッチが取消されて常駐外になったセルも、次の LoadLodCells が表示物セルとしてアンロードするので箱は残らない。
        // 非アクティブなオブジェクトは防御的な扱いで、箱にせず退避だけする。
        buildingObjectsCache.TryGetValue(buildingId, out GameObject obj);
        bool displayable = buildingCellById.TryGetValue(buildingId, out Vector2Int cell) && !pendingUnloadCells.ContainsKey(cell)
            && (loadedCells.Contains(cell) || cellsWithShownObjects.Contains(cell));
        bool hidden = obj != null && !obj.activeInHierarchy;
        if (displayable && !hidden && buildingDataCache.TryGetValue(buildingId, out BuildingSummary data))
        {
            CreateFarBuildingObject(data);
            return;
        }
        ParkBuildingObject(buildingId);
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
    /// 建物の望ましい描画LOD。セルのティアでは固定せず、投影比(画面上の大きさ)だけで決める(E_LOD処理.md の方針)。
    /// 既存オブジェクトの描画LODをヒステリシスの基準にする。
    /// </summary>
    private DrawLod DetermineDesiredDrawLod(BuildingSummary data, Vector3 camPos, float tanHalfFov, bool orthographic)
    {
        float[] cp = data.centerPointArray;
        if (cp == null || cp.Length < 2) return DrawLod.Normal;
        if (buildingObjectsCache.TryGetValue(data.agent_id, out GameObject existing) && existing != null)
            return EvaluateBuildingDrawLod(data, DrawLodInfo.GetDrawLod(existing, DrawLod.Normal), existing.transform.position.y, camPos, tanHalfFov, orthographic);
        return EvaluateBuildingDrawLod(data, DrawLod.Normal, Utils.GetGroundHeight(new Vector3(cp[0], 1000f, cp[1])), camPos, tanHalfFov, orthographic);
    }

    /// <summary>
    /// 描画LOD判定の中核。baseY は建物の基準高で、既存オブジェクトがあればその位置を使って地形の標本化を省く
    /// (区画標高との差は数m程度で、ヒステリシス帯に比べて無視できる)。
    /// </summary>
    private DrawLod EvaluateBuildingDrawLod(BuildingSummary data, DrawLod current, float baseY, Vector3 camPos, float tanHalfFov, bool orthographic)
    {
        float[] cp = data.centerPointArray;
        if (cp == null || cp.Length < 2) return DrawLod.Normal;
        float radius = GetBuildingBoundsRadius(data);
        float distance = Vector3.Distance(camPos, new Vector3(cp[0], baseY + radius * 0.5f, cp[1]));
        bool allowClose = closeBuildingIds.Contains(data.agent_id) || closeBuildingIds.Count < DrawLodConfig.MAX_CLOSE_BUILDINGS;
        return DrawLodEvaluator.EvaluateBySize(radius, distance, tanHalfFov, current, allowClose, orthographic);
    }

    /// <summary>
    /// 遅延生成キューへの投入口。同じIDは最新の要求で上書きする。
    /// 投入は必ずここを通すこと(deferredRevision で遅延生成ワーカーの待機を解くため)。
    /// </summary>
    private void QueueDeferredDetail(int id, BuildingSummary data, bool isDynamic, DrawLod drawLod)
    {
        deferredDetailBuildings[id] = new DeferredDetailRequest { data = data, isDynamic = isDynamic, drawLod = drawLod };
        deferredRevision++;
    }

    /// <summary>描画LOD再判定ワーカーが投影比の変化を検出するためのカメラ状態(UpdateProjection 専用)。</summary>
    private LodViewState drawViewState;

    /// <summary>再判定1パスの対象IDのスナップショット。yield を跨いで辞書を列挙しないために使う。</summary>
    private readonly List<int> drawLodScanIds = new List<int>();

    /// <summary>
    /// カメラ移動に応じて、ロード済みセルの建物の描画LODを再判定する常駐ワーカー。
    /// 投影比は位置とFOV(正射影サイズ)だけで決まるので、回転だけの操作では走査しない。
    /// 走査はフレーム予算で分割する。箱化(Far)は軽いので即時生成し、詳細化(Normal/Close)は遅延キューへ積んで
    /// DetailGenerationWorker が視野優先・フレーム予算で生成する。生成が終わるまで旧オブジェクトは残る。
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
                        // 走査しきれなかった建物を次の機会に拾うため、カメラ状態を未記録に戻す。
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
    /// 建物1棟の描画LODを再判定する。Far を生成・復元したとき true を返す(呼び出し側の予算確認用)。
    /// 待機中の遅延要求は、Far で足りるならここで箱にして消化する。オブジェクトの無い詳細化はワーカーに任せる。
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

    /// <summary>建物を地表からわずかに浮かせ、Zファイティングを避けるオフセット（メートル）。</summary>
    private const float BuildingBaseLift = 0.01f;

    /// <summary>
    /// 建物の基底高（ワールドY）を決める。陸上の建物は model_type によらず建物中心の地形標高に置く
    /// （傾斜地の谷側の隙間はモジュラー建物の基盤が埋める）。
    ///
    /// 海上区画（PARCEL_WATER）だけは区画の基準標高（parcel.altitude＝海面）に置く。
    /// 地形をサンプルすると海底（worldY&lt;0）が返り、洋上風車などが海面下へ沈むため。
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
    /// 敷地が特定できない建物のうち、地形標高が海面より下にあるものだけ、中心点を含む区画を
    /// 座標から逆引きする。一括取得のスキーマ（multi_agent）は parcel_id を持たないため、
    /// 海上構造物はこの経路でしか敷地に辿り着けず、放っておくと海底へ沈む。
    /// 陸上の建物で無駄な逆引きをしないよう、海面より下のときだけ引く。
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

            float buildingBaseHeight = ResolveBuildingBaseHeight(parcel, cx, cz);
            buildingGO.transform.position = new Vector3(cx, buildingBaseHeight, cz);
        }
        else
        {
            Vector3 p = buildingGO.transform.position;
            float groundHeight = Utils.GetGroundHeight(new Vector3(p.x, 1000f, p.z));
            buildingGO.transform.position = new Vector3(p.x, groundHeight + BuildingBaseLift, p.z);
        }
        // 遠景の結合バッチは位置の変化を自分では検知しないので、動かした側から通知する。
        FarBuildingMember.RefreshObject(buildingGO);
    }

    /// <summary>
    /// 建物をセル逆引きへ登録する。shown は表示オブジェクトを伴う登録かどうかで、真なら
    /// そのセルを表示物セル(cellsWithShownObjects)に記録する。データだけの登録(遅延・未ロードセル)は偽で呼ぶ。
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

    private readonly RenderObjectCache lodCache = new RenderObjectCache(256L * 1024 * 1024);

    // ================================================================
    // 再利用キー
    // ================================================================

    /// <summary>
    /// データの版が変わらずに見た目が変わる建物(端子位置に追従する車両基地)の形状版。再利用キーに入れて旧形状のキャッシュを使わせない。
    /// 値は全体の通し番号にして、記録を消した後も過去に発行した版と重ならないようにする。
    /// </summary>
    private readonly Dictionary<int, int> buildingGeometryRevisions = new Dictionary<int, int>();
    private int buildingGeometryRevisionCounter;

    /// <summary>再利用キー用の64bitハッシュ。FNV-1a をワード単位で混ぜ、最後に splitmix64 の撹拌をかける。</summary>
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

        /// <summary>型の決まらない値(construction_progress)。数値は型によらず double で比べ、数値にできないものは文字列で比べる。</summary>
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
    /// 建物の見た目を決めるデータ項目のハッシュ(16進)。見た目に影響する項目を追加したら、ここにも加えること。
    /// BuildingSummary は受信後に書き換えないので、インスタンスごとに1回だけ計算する。
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
    /// 区画のうち建物の見た目に効く部分(ポリゴンと基準標高)のキー片と、ポリゴンの外接矩形。
    /// 版に連動しない項目(地価・接道など)は入れない。入れると区画の再取得のたびに建物を作り直すことになる。
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
    /// 描画物の再利用キー。形状・外観・状態・サブ建物・区画・形状版・描画LOD・接地範囲の地形版・埋立地の版(港の詳細だけ)を区別し、
    /// version だけの変化では変わらない。同じデータからは常に同じキーになる(テストがキーを外から計算して照合する)。
    /// 港は区画が埋立地の岸壁に面するかで岸壁・配置が変わる(遠景も同じ組み立て)ので <see cref="ReclaimedLandStore.ContentVersion"/> を混ぜる。
    /// 港以外は埋立地に依存しないので "-"(埋立のたびに作り直さない)。
    /// </summary>
    private string BuildingRenderKey(BuildingSummary data, DrawLod lod)
    {
        ParcelSummary parcel = GetParcelManager()?.GetParcelSummary(data.parcel_id);
        var terrain = Utils.CachedTerrainManager;
        string surface = terrain != null ? terrain.GetSurfaceStamp(GetBuildingTerrainBounds(data, parcel)) : "none";
        buildingGeometryRevisions.TryGetValue(data.agent_id, out int revision);
        string reclaimed = BuildingMeshBuilder.GetModelType(data) == Constants.MODEL_PORT
            ? ReclaimedLandStore.ContentVersion.ToString(CultureInfo.InvariantCulture) : "-";
        return string.Concat(new[]
        {
            data.agent_id.ToString(CultureInfo.InvariantCulture), ":", buildingShapeKeys.GetValue(data, createBuildingShapeKey), ":",
            revision.ToString(CultureInfo.InvariantCulture), ":", parcel != null ? GetParcelKeyPart(parcel).token : "-", ":",
            ((int)lod).ToString(CultureInfo.InvariantCulture), ":", surface, ":", reclaimed,
        });
    }

    private bool RestoreBuildingLod(BuildingSummary data, DrawLod lod, string key)
    {
        buildingObjectsCache.TryGetValue(data.agent_id, out GameObject old);
        var identity = old != null ? old.GetComponent<RenderCacheIdentity>() : null;
        GameObject ready = identity != null && identity.key == key ? old : lodCache.Take(key);
        if (ready == null)
        {
            RenderWorkScheduler.CountEvent("building.buildRequested");
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
        // 退避中や再利用前に非表示集合が変わっていることがあるので、生成時と同じくフィルタを引き直す。
        ObjectVisibilityFilter.ApplyToObject(ready, InputObjectType.BUILDING, data.agent_id);
        FarBuildingMember.RefreshObject(ready);
        buildingObjectsCache[data.agent_id] = ready;
        RegisterBuildingDataToCache(data);
        UpdateBuildingHeight(ready);
        RecordBuildingTerrainSignature(data);
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
                    ObjectVisibilityFilter.ApplyToObject(existing, InputObjectType.BUILDING, buildingData.agent_id);
                    FarBuildingMember.RefreshObject(existing);
                    RegisterBuildingDataToCache(buildingData);
                    UpdateBuildingHeight(existing);
                    RecordBuildingTerrainSignature(buildingData);
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
        // 遠景GOはコライダーを持たないので、生成されたコライダー用メッシュはここで解放する。
        if (meshResult?.ColliderMesh != null && meshResult.ColliderMesh != meshResult.Mesh)
            MeshBakeService.Release(meshResult.ColliderMesh);
        if (meshResult?.Mesh != null) CollapseToSingleSubMesh(meshResult.Mesh);
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

        // 遠景LODの建物は VisualModeObject を持たない(FarBuildingBatcher が輪郭メッシュを
        // 結合するため)ので、MeshRenderer を直接落として非表示にする。FarBuildingMember は
        // この後に付けるので、登録時の enabled がそのまま結合可否に反映される。
        // 生成後に表示を変える経路は FarBuildingMember.RefreshObject で結合バッチへ通知すること。
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
    /// 生成済みの建物へ、オブジェクト単位の表示フィルタ(ObjectVisibilityFilter)を反映し直す。
    /// 非表示集合が変わったときに呼ばれる。未ロードの建物は生成時に自分で引くため対象外。
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
    /// 工事音の現場登録を建物データに同期する。建設中・解体中だけ中心点を登録し、それ以外は解除する。
    /// 座標はバックエンド座標(FloatingOriginのずれを含まない)で渡す。
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
    /// 建物データをキャッシュに登録し、接続端子ノードの逆引きインデックス
    /// （buildingIdByInfraNodeId / buildingIdByDepotNodeId）を同期する。buildingDataCache への直接代入は禁止で、
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
    /// 車両基地の接続端子ノード（agents.depot_node_id）かを返す。施設が未ロードの場合は false。
    /// </summary>
    public bool IsDepotTerminalNode(int nodeId)
    {
        return nodeId > 0 && buildingIdByDepotNodeId.ContainsKey(nodeId);
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
        // データの版は変わらないので、形状版を進めて退避キャッシュや表示中オブジェクトの再利用を外す。
        buildingGeometryRevisions[building.agent_id] = ++buildingGeometryRevisionCounter;
        CreateOrUpdateBuildingObject(building, IsBuildingInDynamicCell(building), forceRebuild: true, drawLod: GetCurrentDrawLod(building.agent_id));
    }

    /// <summary>
    /// 埋立地の中身（<see cref="ReclaimedLandStore.ContentVersion"/>）が変わったときに
    /// <see cref="ReclaimedLandManager.OnContentChanged"/> から呼ばれる。港（MODEL_PORT）は区画が埋立地の岸壁に面するかで
    /// 形が変わるが、埋立で港自身の version は変わらないので、表示中の港をここで作り直す。
    /// 遠景は即時(港は数が少なく、遠景の組み立ては軽い)、詳細は視野内なら即時・視野外なら遅延キューへ積む。
    /// 退避中・未生成の港は再利用キーに版が入っているので次の生成で新しい形になる。
    /// </summary>
    public void OnReclaimedLandsChanged()
    {
        if (portBuildingIds.Count == 0) return;
        Camera cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        Vector3 camFwd = cam != null ? GetHorizontalForward(cam) : Vector3.forward;
        // 作り直しは RegisterBuildingDataToCache を通って集合を書き換えうるので写してから回す。
        foreach (int id in new List<int>(portBuildingIds))
        {
            if (!buildingObjectsCache.TryGetValue(id, out GameObject obj) || obj == null || !obj.activeSelf) continue;
            if (!buildingDataCache.TryGetValue(id, out BuildingSummary building)) continue;
            DrawLod lod = DrawLodInfo.GetDrawLod(obj, DrawLod.Normal);
            // 1棟の生成失敗で残りの港の作り直しを止めない。
            try
            {
                if (lod == DrawLod.Far)
                {
                    CreateFarBuildingObject(building);
                    continue;
                }
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

    /// <summary>
    /// 離脱セルを分割アンロードの待ち行列へ積む。オブジェクトの非表示化は CellUnloadWorker が予算内で行う
    /// (1セル数百棟を1ステップで SetActive(false) するとスパイクになるため)。
    /// </summary>
    protected override void UnloadCell(Vector2Int cellIndex)
    {
        cellsWithShownObjects.Remove(cellIndex);
        if (!buildingIdsByCell.TryGetValue(cellIndex, out HashSet<int> ids)) return;
        pendingUnloadCells[cellIndex] = ++unloadGeneration;

        // 範囲外セルの遅延生成リクエストは破棄する(ワーカーがロード範囲外に
        // アクティブな建物を復活させないため)。セルが再ロードされれば
        // マージロードが再度キューへ積み直す。
        foreach (int id in ids) deferredDetailBuildings.Remove(id);
    }

    /// <summary>
    /// 離脱セルの建物を退避キャッシュへ移す常駐ワーカー。1セルずつIDを写し取り、フレーム予算で分割する。
    /// 再入場や再登録でセルの記録が変わったら、そのスナップショットは捨てる。
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
    /// 離脱セルの建物オブジェクトを退避キャッシュへ移す。version も消すので、再入場時は needsRebuild になり
    /// RestoreBuildingLod がキーでキャッシュから復元する(メッシュは作り直さない)。
    /// データキャッシュ・セル逆引き・端子/区画インデックス・工事音はデータの生存期間に従うので残す。
    /// </summary>
    private void ParkUnloadedBuilding(int id, Vector2Int cell)
    {
        if (loadedCells.Contains(cell)) return;
        if (!buildingCellById.TryGetValue(id, out Vector2Int currentCell) || currentCell != cell) return;
        deferredDetailBuildings.Remove(id);
        if (!buildingObjectsCache.ContainsKey(id)) return;
        ParkBuildingObject(id);
    }

    /// <summary>建物オブジェクトを退避キャッシュへ移し、オブジェクトに紐づく台帳と version を外す。</summary>
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
        closeBuildingIds.Remove(id);
        buildingLastVisibleFrame.Remove(id);
        buildingTerrainSignatures.Remove(id);
    }

    /// <summary>
    /// MeshRendererのマテリアルを、Z-fighting防止用の Custom/DepthOffsetLit へ差し替えた共有マテリアルにする。
    /// 共有物なので RenderCacheIdentity には所有させない。
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
    /// 元マテリアルごとに1つだけ作った DepthOffsetLit 版を返し、全建物で共有する。
    /// 建物ごとの色替えは VisualModeObject が sharedMaterials の差し替えとプロパティブロックで行うので、
    /// 共有して問題ない(建物レンダラーの .material を個別に書き換えてはならない)。セッションを跨いで保持する。
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
                // 広告・看板の共有材質(組み込みStandard)は画像と色を _MainTex/_Color に持つ。
                // DepthOffsetLit は URP Lit と同じく _BaseMap/_BaseColor を読むので写し替える。
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

    private static readonly List<int> collapseTriangles = new List<int>();
    private static readonly List<int> collapseSubMeshTriangles = new List<int>();

    /// <summary>
    /// 遠景は共有マテリアル1つで描くため、prebuilt_far のように複数サブメッシュを持つメッシュを1サブメッシュに畳む。
    /// マテリアル数を超えるサブメッシュは Unity が描かないので、畳まないと先頭以外が消える。
    /// </summary>
    private static void CollapseToSingleSubMesh(Mesh mesh)
    {
        if (mesh.subMeshCount <= 1) return;
        collapseTriangles.Clear();
        for (int i = 0; i < mesh.subMeshCount; i++)
        {
            mesh.GetTriangles(collapseSubMeshTriangles, i);
            collapseTriangles.AddRange(collapseSubMeshTriangles);
        }
        mesh.subMeshCount = 1;
        mesh.SetTriangles(collapseTriangles, 0);
        collapseTriangles.Clear();
        collapseSubMeshTriangles.Clear();
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
                    RecordBuildingTerrainSignature(buildingData);
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
        BuildingMeshResult meshResult = new BuildingMeshBuilder().Generate(buildingData, true, drawLod, separateExterior: true, animateCrane: true);
        ConstructionCraneAnimator.Attach(buildingGO, meshResult?.CraneTop, buildingData.agent_id);
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
        // オブジェクト単位の表示フィルタは差分ロードで後から生成された建物にも効かせる必要が
        // あるため、生成直後にここで初期状態を引く。
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

    /// <summary>対象1棟を再取得する。爆破成功通知の場合だけ、瓦礫への切替と同時に単発演出を加える。</summary>
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
                    // セッション切替中に完了した通信は、新しい都市へ持ち込まない。
                    if (generation != explosionGeneration || building == null)
                        return;
                    if (playExplosion && (building.status != Constants.STATUS_COLLAPSED ||
                        (explodedVersions.TryGetValue(agentId, out int version) && version >= building.version)))
                        return;
                    if (buildingDataCache.TryGetValue(agentId, out var cached) && cached.version > building.version)
                        return;
                    float[] cp = building.centerPointArray;
                    if (cp == null || cp.Length < 2) return;

                    // ロード範囲外のセルには表示物を作らない(データだけ更新し、再入場時のマージロードで生成させる)。
                    // 範囲外でも表示中のオブジェクトがあれば、それは更新する(分割アンロードが後で退避する)。
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
                        // 建物の子にはしない。LOD再生成で煙や音の余韻が途中で消えるのを防ぐ。
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
    /// 建物の center_point が属するセルが現在 Dynamic ティアかどうかを返す。
    /// cellBuildingLod が更新済みのタイミング（単発フェッチ等）でのみ正確。
    /// LoadMergedBuildings の最中はティア表が未更新のため、desiredTier を直接渡すこと。
    /// </summary>
    private bool IsBuildingInDynamicCell(BuildingSummary buildingData)
    {
        float[] cp = buildingData.centerPointArray;
        if (cp == null || cp.Length < 2) return false;
        Vector2Int cell = TerrainManager.WorldToCell(new Vector3(cp[0], 0f, cp[1]));
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
