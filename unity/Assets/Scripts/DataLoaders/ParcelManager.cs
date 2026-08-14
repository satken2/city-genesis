using UnityEngine;
using System.Collections.Generic;
using UnityEngine.UI;
using System;
using System.Collections;
using GeneratedModels;

public class ParcelManager : BaseApiDataManager, IVisualModeHandler
{
    [SerializeField] private Material vacantParcelMaterial;

    private const string PARCEL_PREFIX = "Parcel_";
    private const string PARCEL_TAG = "ParcelObject";

    /// <summary>
    /// パーセル1つ分の枠線データ。ワールド座標の端点を保持し、
    /// 毎フレーム Update でスクリーン座標に変換して UILineRenderer に渡す。
    /// </summary>
    private class ParcelOutlineData
    {
        public int parcelId;
        /// <summary>
        /// 土地区画の中心座標（標高を含む）。
        /// 描画更新処理での Terrain 標高取得を省略し、距離判定を高速化するために保持される。
        /// </summary>
        public Vector3 center;
        public Vector3[] worldPoints;
    }

    private List<ParcelOutlineData> outlines = new List<ParcelOutlineData>();
    private Dictionary<int, int> parcelIdToOutlineIndex = new Dictionary<int, int>();
    private Dictionary<int, ParcelSummary> parcelCache = new Dictionary<int, ParcelSummary>();
    private Dictionary<int, GameObject> parcelObjectsCache = new Dictionary<int, GameObject>();

    /// <summary>
    /// マウス位置からの区画検索（GetParcelIdAtPoint）用の空間インデックスエントリ。
    /// 起伏地形では区画のMeshColliderが地形の下に埋まりレイキャストで拾えないことがあるため、
    /// 「地形ヒット点のXZがどの区画ポリゴンに含まれるか」の2D内外判定で区画を解決する。
    /// 毎フレームの判定でGC・再計算を発生させないよう、BBOXと変換済みポリゴンを登録時に保持する。
    /// </summary>
    private class ParcelSpatialEntry
    {
        /// <summary>ポリゴンのAABB（x=ワールドX, y=ワールドZ）。内外判定前の早期スキップに使う。</summary>
        public Rect bbox;
        /// <summary>変換済みポリゴン頂点（[i][0]=X, [i][1]=Z）。GetPolygonArray の毎回のアロケーションを避ける。</summary>
        public float[][] polygon;
        /// <summary>parcelIdsByCell に登録したセル一覧（削除時の逆引き用）。</summary>
        public List<Vector2Int> cells;
        /// <summary>接道街路のID（front_path_id）。parcelIdsByFrontPath からの削除時の逆引き用。0=接道なし。</summary>
        public int frontPathId;
    }

    /// <summary>区画ID → 空間インデックスエントリ。RegisterParcelDataForLines / RemoveParcel / ClearAllCells で同期する。</summary>
    private Dictionary<int, ParcelSpatialEntry> parcelSpatialCache = new Dictionary<int, ParcelSpatialEntry>();

    /// <summary>
    /// セル → そのセルにBBOXが重なる区画IDの集合。
    /// GetParcelIdAtPoint が全区画の線形走査をせずO(1)で候補を絞るための逆引きインデックス
    /// （BuildingManager.buildingIdsByCell と同じ全件走査回避パターン）。
    /// </summary>
    private Dictionary<Vector2Int, HashSet<int>> parcelIdsByCell = new Dictionary<Vector2Int, HashSet<int>>();

    /// <summary>
    /// 接道街路ID（front_path_id）→ その街路に面する区画IDの集合。
    /// 「この道路に面している区画」を全区画走査なしで解決するための逆引きインデックス。
    /// 登録は RegisterParcelSpatialIndex、削除は UnregisterParcelSpatialIndex / ClearAllCells で同期する
    /// （parcelIdsByCell と同じ全件走査回避パターン）。front_path_id は parcels の VERSION_TRIGGER_PROPS に
    /// 含まれるため、接道変更時は version が上がり CreateOrUpdateParcelObject 経由で再登録される。
    /// </summary>
    private Dictionary<int, HashSet<int>> parcelIdsByFrontPath = new Dictionary<int, HashSet<int>>();

    /// <summary>
    /// バックエンドから取得した区画のversionをキャッシュする。
    /// 差分更新時にバックエンドのversionと比較し、変更があった場合のみオブジェクトを再作成する。
    /// </summary>
    private Dictionary<int, int> parcelVersionCache = new Dictionary<int, int>();

    private GameObject overlayMeshObj;
    private OverlayMeshRenderer overlayRenderer;

    /// <summary>
    /// データ可視化(色分けフィル)専用のオーバーレイ。
    /// 枠線・ハイライト用の overlayRenderer(_ZTest=Always)と違い、
    /// 深度テストあり(LEqual)の地形追従シェーダー Custom/OverlayFillTerrainConform で描画する。
    /// シェーダーがGPUテッセレーションで三角形を細分化し、全頂点を地形高さ+リフトに
    /// 張り付けるため、CPU側は単純なポリゴンを投げるだけで
    /// 「地形より上(起伏でも埋もれない)・建物より下(壁に隠れる)・
    /// 地表すれすれの floor パーツより上」の深度関係になる。
    /// </summary>
    private GameObject dataVisMeshObj;
    private OverlayMeshRenderer dataVisRenderer;

    private const float LINE_THICKNESS = 1.0f;
    private Color parcelLineColor = new Color(0.8f, 0.8f, 0.8f, 1f);

    /// <summary>
    /// LoadLodCells で起動したコルーチンを追跡し、新たなカメラ移動時にキャンセルする。
    /// </summary>
    private List<Coroutine> activeLodCoroutines = new List<Coroutine>();

    /// <summary>
    /// セル跨ぎの区画を誤って非表示にしないため、アンロード判定時に
    /// 「残すべきセル」として参照するセル集合。
    /// LoadLodCells の先頭で設定され、UnloadCellObjects で使用される。
    /// </summary>
    private HashSet<Vector2Int> cellsToKeepVisible = new HashSet<Vector2Int>();

    /// <summary>
    /// セル単位のロード状態を記録する（LOD非対応だが、loadedCellsの補助として使用）
    /// </summary>
    private Dictionary<Vector2Int, bool> cellParcelLoaded = new Dictionary<Vector2Int, bool>();
    private HashSet<Vector2Int> fullyLoadedCells = new HashSet<Vector2Int>();

    /// <summary>マウスオーバー時のハイライト色。HighlightMaterial.mat と同等の半透明青(αは明瞭化のため引き上げ)</summary>
    private static readonly Color HighlightColor = new Color(0.259f, 0.437f, 1.0f, 0.6f);

    /// <summary>ハイライトを地形から浮かせるオフセット（メートル）</summary>
    private const float HighlightHeightOffset = 0.05f;

    /// <summary>現在マウスオーバー中のパーセルID。-1 のときはハイライトなし。</summary>
    private int highlightedParcelId = -1;

    /// <summary>
    /// パス干渉ハイライト対象の区画ID → 塗りつぶし色。
    /// 計画中パスが収用/干渉する区画を分類ごとの色で塗る（HighlightManager が設定）。
    /// マウスオーバーの単一ハイライトとは独立したレイヤーで、両方同時に描画され得る。
    /// </summary>
    private Dictionary<int, Color> interferenceParcelColors = new Dictionary<int, Color>();

    // カメラ変化検知による枠線更新間引き用
    private Vector3 lastCamPos = Vector3.negativeInfinity;
    private Quaternion lastCamRot = Quaternion.identity;
    private const float CAMERA_POS_THRESHOLD = 1.0f;
    private const float CAMERA_ROT_THRESHOLD = 2.0f;

    /// <summary>パーセル上面を地形からわずかに浮かせ、Zファイティングを避けるオフセット（メートル）</summary>
    private const float ParcelSurfaceOffset = 0.002f;

    /// <summary>枠線を地形から浮かせるオフセット（メートル）</summary>
    private const float OutlineHeightOffset = 0.03f;

    // ================= 接道(front_path)矢印表示 =================
    /// <summary>接道矢印の色（青）。</summary>
    private static readonly Color FrontPathArrowColor = new Color(0.15f, 0.45f, 1f, 1f);
    /// <summary>接道矢印の線の太さ（ピクセル）。枠線より太くして強調する。</summary>
    private const float FrontPathArrowThickness = 3.0f;
    /// <summary>接道矢印を地形から浮かせるオフセット（メートル）。枠線より高くして重ならないようにする。</summary>
    private const float FrontPathArrowHeightOffset = 0.1f;
    /// <summary>矢印シャフトの片側長さ（メートル）。接道辺の中点から内外にこの長さで伸ばす。</summary>
    private const float FrontPathArrowHalfLength = 4.0f;
    /// <summary>矢じりの長さ（メートル）。</summary>
    private const float FrontPathArrowHeadLength = 2.5f;
    /// <summary>矢じりの半幅（メートル）。</summary>
    private const float FrontPathArrowHeadHalfWidth = 1.5f;

    /// <summary>接道矢印を表示中かどうか。</summary>
    private bool frontPathArrowActive = false;
    /// <summary>接道矢印の対象区画ID。非同期取得のレース検出に使う（-1で対象なし）。</summary>
    private int frontPathParcelId = -1;
    /// <summary>接道取得中のコルーチン。新たな要求時に停止する。</summary>
    private Coroutine frontPathCoroutine;
    /// <summary>矢印シャフトのワールド座標（始点=区画内側, 終点=道路側の矢じり先端）。</summary>
    private Vector3 frontPathShaftStart;
    private Vector3 frontPathShaftEnd;
    /// <summary>矢じり三角形のワールド座標頂点（両面: 6頂点=2三角形）。毎フレームのGCを避けるため再利用する。</summary>
    private readonly List<Vector3> frontPathArrowHead = new List<Vector3>(6);

    /// <summary>
    /// 現在のパーセルビジュアルモード。
    /// ApplyVisualMode で設定され、UpdateAllParcelOutlines で参照される。
    /// データ可視化モード（200以上）のときは色付きポリゴンをオーバーレイで描画する。
    /// </summary>
    private int currentParcelVisualMode = VisualMode.MODE_NORMAL;

    /// <summary>
    /// 環境スコア(騒音・大気汚染・治安)の parcel_id → 0〜1 の相対値。
    /// バックエンドは区画中心でサンプリング済みのスコアを都市全体分まとめて返すため、
    /// 区画のbbox取得とは独立にモードごと1回だけ取得して引き当てる。
    /// </summary>
    private readonly Dictionary<int, float> environmentScores = new Dictionary<int, float>();

    /// <summary>environmentScores が保持しているモード(0=未取得)</summary>
    private int loadedEnvironmentMode = 0;

    /// <summary>取得を要求したモード。応答時に一致しなければ結果を捨てる</summary>
    private int requestedEnvironmentMode = 0;

    private Coroutine environmentCoroutine;

    /// <summary>
    /// パーセルの VisualParcelObject キャッシュ。
    /// 毎フレームの GetComponent 呼び出しを回避するため、
    /// CreateOrUpdateParcelObject で登録し、削除時にクリアする。
    /// </summary>
    private Dictionary<int, VisualParcelObject> parcelVisualObjects = new Dictionary<int, VisualParcelObject>();

    /// <summary>データ可視化ポリゴンの基本α値。ホバーハイライトと同じ透過度</summary>
    private const float DataVisAlpha = 0.6f;

    // 距離に応じた透明度の設定（ゲーム空間メートル。Terrain の遠方LOD表示距離に近いスケールに合わせる）
    private const float DISTANCE_FULL_VISIBLE = 150f;
    private const float DISTANCE_START_FADE = 280f;
    private const float DISTANCE_INVISIBLE = 480f;

    /// <summary>
    /// アルファのフェードアウト処理時に、平方根の計算負荷を抑えて判定するために使用される、
    /// フェード開始距離および非表示距離の2乗値。
    /// </summary>
    private const float DISTANCE_START_FADE_SQ = DISTANCE_START_FADE * DISTANCE_START_FADE;
    private const float DISTANCE_INVISIBLE_SQ = DISTANCE_INVISIBLE * DISTANCE_INVISIBLE;

    private void Awake()
    {
        RegisterWithVisualModeHandler();
    }

    private void Start()
    {
        EnsureOverlayRenderer();
        // Awake の時点で VisualModeHandler のインスタンスが未生成だった場合に備え、
        // Start でも改めて登録を試みる。既に登録済みの場合は HashSet の性質上無視される。
        RegisterWithVisualModeHandler();
        if (VisualModeHandler.Instance == null)
        {
            StartCoroutine(WaitAndRegisterVisualModeHandler());
        }
        TerrainManager.OnTerrainTileReady += OnTerrainTileReady;
    }

    private System.Collections.IEnumerator WaitAndRegisterVisualModeHandler()
    {
        while (VisualModeHandler.Instance == null)
        {
            yield return null;
        }
        RegisterWithVisualModeHandler();
    }

    private void EnsureOverlayRenderer()
    {
        if (overlayMeshObj != null) return;

        overlayMeshObj = new GameObject("ParcelOverlayMesh");
        DontDestroyOnLoad(overlayMeshObj);
        overlayRenderer = overlayMeshObj.AddComponent<OverlayMeshRenderer>();
        Material mat = new Material(Shader.Find("Custom/OverlayLine"));
        // 枠線・ハイライト・データ可視化が起伏地形で地面に埋もれるのを防ぐため、
        // このマテリアルインスタンス限定で深度テストを無効化する(RoadPlanManager のギズモと同じ対応)。
        // OverlayLine シェーダー既定の _ZTest=LEqual + ポリゴンオフセットでは、傾斜地で平板ポリゴンが
        // 曲面地形の下に食い込んで地形に負け、埋まったように見えるため。
        // 代儝: オーバーレイが丘の裏や手前の建物を貫通してX線状に見える(強調表示用途なので許容)。
        mat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
        overlayRenderer.Initialize(mat);

        // データ可視化(色分けフィル)専用レンダラー。overlayMeshObj の子にして表示ON/OFFを連動させる。
        // シェーダーは深度テストあり(LEqual)の Custom/OverlayFillTerrainConform。
        // GPUテッセレーション+高さマップ追従で三角形内部まで常に地形+リフトの高さになるため、
        // 深度テストありでも地形の起伏に埋もれず、建物には自然に隠れる。
        dataVisMeshObj = new GameObject("ParcelDataVisMesh");
        dataVisMeshObj.transform.SetParent(overlayMeshObj.transform, false);
        dataVisRenderer = dataVisMeshObj.AddComponent<OverlayMeshRenderer>();
        dataVisRenderer.Initialize(new Material(Shader.Find("Custom/OverlayFillTerrainConform")));
    }

    public void RemoveParcel(int parcelId)
    {
        if (parcelObjectsCache.TryGetValue(parcelId, out GameObject obj) && obj != null)
        {
            Destroy(obj);
        }
        parcelObjectsCache.Remove(parcelId);
        parcelVisualObjects.Remove(parcelId);

        RemoveParcelOutline(parcelId);
        UnregisterParcelSpatialIndex(parcelId);
        parcelCache.Remove(parcelId);
        // version キャッシュも除去する。残すと同一 parcel_id・同一 version で
        // 削除→再作成された区画が LoadMergedParcels の version 一致判定でスキップされ、
        // 枠線が復活しなくなる。
        parcelVersionCache.Remove(parcelId);
    }

    protected override void UnloadCell(Vector2Int cellIndex)
    {
        UnloadCellObjects(cellIndex);
        fullyLoadedCells.Remove(cellIndex);
    }

    private void UnloadCellObjects(Vector2Int cellIndex)
    {
        CalculateCellBounds(cellIndex, out float startX, out float endX, out float startZ, out float endZ);

        List<Vector2Int> remainingCells = new List<Vector2Int>(cellsToKeepVisible);
        remainingCells.Remove(cellIndex);

        foreach (var kvp in parcelObjectsCache)
        {
            if (kvp.Value != null && IsObjectInCell(kvp.Value, startX, endX, startZ, endZ))
            {
                if (!IsObjectInCells(kvp.Value, remainingCells))
                    kvp.Value.SetActive(false);
            }
        }
    }

    // ================================================================
    // LOD対応セルロード（マージBBOX版）
    // ================================================================

    public override void LoadLodCells(LodCellSets cellSets, bool isForce)
    {
        // 実行中の古いコルーチンをすべてキャンセル（カメラ移動による再呼び出し対策）
        foreach (var c in activeLodCoroutines)
        {
            if (c != null) StopCoroutine(c);
        }
        activeLodCoroutines.Clear();

        var allParcelCells = new HashSet<Vector2Int>(cellSets.farCells);

        // セル跨ぎの区画を誤って非表示にしないために、
        // アンロード判定時に参照する「残すべきセル」集合を設定する
        cellsToKeepVisible = new HashSet<Vector2Int>(allParcelCells);

        // 不要セルをアンロード
        var toUnload = new List<Vector2Int>();
        foreach (var cell in loadedCells)
        {
            if (!allParcelCells.Contains(cell))
                toUnload.Add(cell);
        }
        foreach (var cell in toUnload)
        {
            UnloadCell(cell);
            loadedCells.Remove(cell);
            cellParcelLoaded.Remove(cell);
        }

        // 常に cellSets.farCells の全セルを対象とする。
        // 未ロードセルは新規ロード、ロード済みセルは version ベースで差分更新される。
        var cellsToLoad = new List<Vector2Int>();
        foreach (var cell in cellSets.farCells)
        {
            if (isForce && loadedCells.Contains(cell))
            {
                UnloadCell(cell);
                loadedCells.Remove(cell);
            }
            if (!loadedCells.Contains(cell))
            {
                cellsToLoad.Add(cell);
                loadedCells.Add(cell);
                cellParcelLoaded[cell] = true;
            }
            else
            {
                cellsToLoad.Add(cell);
            }
        }

        if (cellsToLoad.Count > 0)
        {
            activeLodCoroutines.Add(StartCoroutine(LoadMergedParcels(cellsToLoad)));
        }
    }

    /// <summary>
    /// 複数セルのマージBBOXを使って区画を一括取得する。
    /// 従来のセルごとのAPIコール(最大49回)を1回に削減する。
    /// 区画はLODティア分けがないため、全データを同一の処理で描画する。
    /// </summary>
    private IEnumerator LoadMergedParcels(List<Vector2Int> cells)
    {
        CalculateMergedBBox(cells, out float minX, out float minZ, out float maxX, out float maxZ);
        ParcelListResponse response = null;
        yield return ApiClient.GetBulk<ParcelListResponse>(
            $"/parcels/?bbox={minX},{minZ},{maxX},{maxZ}&limit=5000",
            r => response = r,
            timingLabel: "ParcelManager parcels");
        if (response == null)
        {
            yield break;
        }

        HashSet<int> receivedIds = new HashSet<int>();

        if (response != null && response.parcels != null)
        {
            foreach (var p in response.parcels)
            {
                receivedIds.Add(p.parcel_id);

                // version比較: キャッシュと同じversionなら再作成しない
                if (parcelVersionCache.TryGetValue(p.parcel_id, out int cachedVersion) && cachedVersion == p.version)
                {
                    // 非表示だった既存オブジェクトがあれば再表示する
                    if (parcelObjectsCache.TryGetValue(p.parcel_id, out GameObject existing) && existing != null && !existing.activeSelf)
                    {
                        existing.SetActive(true);
                    }
                }
                else
                {
                    CreateOrUpdateParcelObject(p);
                    parcelVersionCache[p.parcel_id] = p.version;
                }
            }
        }

        // ロード対象セル群に含まれる区画のうち、APIレスポンスに含まれないものを削除する
        SyncParcelsInCells(cells, receivedIds);

        foreach (var cell in cells)
        {
            fullyLoadedCells.Add(cell);
        }
    }

    /// <summary>
    /// 複数セルにまたがる区画の同期処理。
    /// APIレスポンスに含まれない区画のうち、指定セル群に含まれるものを削除する。
    /// </summary>
    private void SyncParcelsInCells(List<Vector2Int> cells, HashSet<int> receivedIds)
    {
        List<int> idsToRemove = new List<int>();
        HashSet<Vector2Int> cellSet = new HashSet<Vector2Int>(cells);

        foreach (var kvp in parcelCache)
        {
            int id = kvp.Key;
            GameObject obj = GetParcelObject(id);

            Vector3 pos;
            if (obj != null)
            {
                pos = obj.transform.position;
            }
            else
            {
                // GameObject が破棄済みでも parcelCache の中心点でセル判定する
                float[] cp = kvp.Value.GetCenterPointArray();
                if (cp == null || cp.Length < 2) continue;
                pos = new Vector3(cp[0], 0, cp[1]);
            }

            Vector2Int objCell = PositionToCell(pos);
            if (cellSet.Contains(objCell) && !receivedIds.Contains(id))
            {
                idsToRemove.Add(id);
            }
        }

        foreach (int id in idsToRemove)
        {
            RemoveParcel(id);
        }
    }

    public override void ClearAllCells()
    {
        foreach (var obj in parcelObjectsCache.Values)
        {
            if (obj != null) Destroy(obj);
        }

        outlines.Clear();
        parcelIdToOutlineIndex.Clear();
        parcelCache.Clear();
        parcelObjectsCache.Clear();
        parcelVisualObjects.Clear();
        parcelVersionCache.Clear();
        parcelSpatialCache.Clear();
        parcelIdsByCell.Clear();
        parcelIdsByFrontPath.Clear();

        if (overlayRenderer != null)
        {
            overlayRenderer.ClearMesh();
        }
        if (dataVisRenderer != null)
        {
            dataVisRenderer.ClearMesh();
        }

        loadedCells.Clear();
        cellParcelLoaded.Clear();
        fullyLoadedCells.Clear();
    }

    public GameObject GetParcelObject(int parcelId)
    {
        if (parcelObjectsCache.TryGetValue(parcelId, out GameObject obj) && obj != null)
            return obj;
        return null;
    }

    public List<ParcelSummary> getAllParcels()
    {
        List<ParcelSummary> parcels = new List<ParcelSummary>();
        foreach (var kvp in parcelCache)
        {
            parcels.Add(kvp.Value);
        }
        return parcels;
    }

    /// <summary>
    /// parcel_id に対応する ParcelSummary をキャッシュから取得する。
    /// 存在しない場合は null を返す。
    /// </summary>
    public ParcelSummary GetParcelSummary(int parcelId)
    {
        parcelCache.TryGetValue(parcelId, out ParcelSummary summary);
        return summary;
    }

    /// <summary>
    /// パーセルの建物用途ID（空き地の場合は VALUE_NONE=-1）を取得する。
    /// 用途IDが変化した際にパーセルオブジェクトを再生成するために使用される。
    /// </summary>
    private int GetParcelStatus(ParcelSummary parcelData)
    {
        return parcelData.usage_id;
    }

    private void CreateOrUpdateParcelObject(ParcelSummary parcelData)
    {
        float[][] polygon = parcelData.GetPolygonArray();
        float[] centerPoint = parcelData.GetCenterPointArray();

        GameObject existingParcel = GetParcelObject(parcelData.parcel_id);
        bool needRecreate = false;

        if (existingParcel != null)
        {
            var existingIdInfo = existingParcel.GetComponent<ObjectIdInfo>();
            int currentStatus = existingIdInfo != null ? existingIdInfo.status : 0;
            int newStatus = GetParcelStatus(parcelData);

            if (currentStatus != newStatus)
            {
                RemoveParcel(parcelData.parcel_id);
                needRecreate = true;
            }
            else
            {
                // プールから再利用: 非表示だったオブジェクトを再表示
                if (!existingParcel.activeSelf)
                    existingParcel.SetActive(true);
                // version変更で形状が変わっている可能性があるため、空間インデックスも含めて登録し直す
                RegisterParcelDataForLines(parcelData);
            }
        }
        else
        {
            needRecreate = true;
        }

        if (needRecreate)
        {
            RegisterParcelDataForLines(parcelData);

            if (existingParcel == null && CellDataLoader.Instance != null && CellDataLoader.Instance.IsInitialLoadComplete)
            {
                if (centerPoint != null && centerPoint.Length >= 2)
                {
                    float cx = centerPoint[0];
                    float cz = centerPoint[1];
                    Vector2Int cellIndex = PositionToCell(new Vector3(cx, 0f, cz));
                    if (fullyLoadedCells.Contains(cellIndex))
                    {
                        if (VisualEffectManager.Instance != null)
                        {
                            float cy = Utils.GetGroundHeight(new Vector3(cx, 1000f, cz));
                            VisualEffectManager.Instance.PlayConstructionComplete(new Vector3(cx, cy, cz));
                        }
                    }
                }
            }

            int status = GetParcelStatus(parcelData);
            GameObject parcelGO = new GameObject($"{PARCEL_PREFIX}{parcelData.parcel_id}");
            var idInfo = parcelGO.AddComponent<ObjectIdInfo>();
            idInfo.id = parcelData.parcel_id;
            idInfo.status = status;
            Transform container = GetContainer("Parcels");
            if (container != null) parcelGO.transform.SetParent(container);

            parcelGO.tag = PARCEL_TAG;

            {
                MeshFilter mf = parcelGO.AddComponent<MeshFilter>();
                MeshRenderer mr = parcelGO.AddComponent<MeshRenderer>();
                if (vacantParcelMaterial != null)
                {
                    mr.material = vacantParcelMaterial;
                }
                mr.enabled = false;

                Vector2[] points = new Vector2[polygon.Length];
                for (int i = 0; i < polygon.Length; i++)
                {
                    points[i] = new Vector2(polygon[i][0], polygon[i][1]);
                }

                Vector2 centroid = new Vector2(
                    centerPoint[0],
                    centerPoint[1]);

                BuildParcelTopLocalYOffsets(points, centroid, out float[] topLocalY, out float parcelBaseHeight);
                Mesh mesh = ParcelMeshFactory.GenerateParcelMesh(points, centroid, 0f, parcelData.usage_id, topLocalY);
                mf.mesh = mesh;

                float cx = centerPoint[0];
                float cz = centerPoint[1];
                parcelGO.transform.position = new Vector3(cx, parcelBaseHeight, cz);

                MeshCollider collider = parcelGO.AddComponent<MeshCollider>();
                collider.sharedMesh = mf.mesh;
                ParcelInputHandler inputHandler = parcelGO.AddComponent<ParcelInputHandler>();
                inputHandler.parcelId = parcelData.parcel_id;
            }

            var vpoComp = parcelGO.AddComponent<VisualParcelObject>();
            parcelVisualObjects[parcelData.parcel_id] = vpoComp;

            var stats = parcelGO.AddComponent<VisualParcelStats>();
            stats.InitFrom(parcelData);

            parcelObjectsCache[parcelData.parcel_id] = parcelGO;
        }

        CreateParcelOutline(parcelData.parcel_id, parcelData);
    }

    public IEnumerator FetchParcel(int parcelId)
    {
        yield return ApiClient.Get<ParcelSummary>(
            $"/parcels/{parcelId}",
            parcel =>
            {
                if (parcel != null)
                    CreateOrUpdateParcelObject(parcel);
            },
            error => Debug.LogError($"FetchParcel({parcelId}) failed: {error}")
        );
    }

    private void RegisterParcelDataForLines(ParcelSummary parcelData)
    {
        parcelCache[parcelData.parcel_id] = parcelData;
        RegisterParcelSpatialIndex(parcelData);
    }

    /// <summary>
    /// 区画を空間インデックス（parcelSpatialCache / parcelIdsByCell）に登録する。
    /// ポリゴンのAABBを計算し、AABBが重なる全セル（区画は小さいので通常1〜4セル）へ逆引き登録する。
    /// 既登録の場合は一度削除してから登録し直す（形状変更時のセル移動に対応）。
    /// </summary>
    private void RegisterParcelSpatialIndex(ParcelSummary parcelData)
    {
        float[][] polygon = parcelData.GetPolygonArray();
        if (polygon == null || polygon.Length < 3) return;

        UnregisterParcelSpatialIndex(parcelData.parcel_id);

        float minX = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxZ = float.MinValue;
        foreach (float[] pt in polygon)
        {
            if (pt == null || pt.Length < 2) continue;
            if (pt[0] < minX) minX = pt[0];
            if (pt[0] > maxX) maxX = pt[0];
            if (pt[1] < minZ) minZ = pt[1];
            if (pt[1] > maxZ) maxZ = pt[1];
        }
        if (minX > maxX || minZ > maxZ) return;

        int frontPathId = parcelData.GetFrontPathId();
        var entry = new ParcelSpatialEntry
        {
            bbox = Rect.MinMaxRect(minX, minZ, maxX, maxZ),
            polygon = polygon,
            cells = new List<Vector2Int>(),
            frontPathId = frontPathId
        };

        // 接道街路の逆引きインデックスに登録する（front_path_id が未取得=0 のときは登録しない）。
        if (frontPathId > 0)
        {
            if (!parcelIdsByFrontPath.TryGetValue(frontPathId, out HashSet<int> byPath))
            {
                byPath = new HashSet<int>();
                parcelIdsByFrontPath[frontPathId] = byPath;
            }
            byPath.Add(parcelData.parcel_id);
        }

        Vector2Int cellMin = PositionToCell(new Vector3(minX, 0f, minZ));
        Vector2Int cellMax = PositionToCell(new Vector3(maxX, 0f, maxZ));
        for (int cx = cellMin.x; cx <= cellMax.x; cx++)
        {
            for (int cz = cellMin.y; cz <= cellMax.y; cz++)
            {
                Vector2Int cell = new Vector2Int(cx, cz);
                if (!parcelIdsByCell.TryGetValue(cell, out HashSet<int> ids))
                {
                    ids = new HashSet<int>();
                    parcelIdsByCell[cell] = ids;
                }
                ids.Add(parcelData.parcel_id);
                entry.cells.Add(cell);
            }
        }

        parcelSpatialCache[parcelData.parcel_id] = entry;
    }

    /// <summary>
    /// 区画を空間インデックスから削除する。RemoveParcel から呼ばれる。
    /// </summary>
    private void UnregisterParcelSpatialIndex(int parcelId)
    {
        if (!parcelSpatialCache.TryGetValue(parcelId, out ParcelSpatialEntry entry)) return;

        foreach (Vector2Int cell in entry.cells)
        {
            if (parcelIdsByCell.TryGetValue(cell, out HashSet<int> ids))
            {
                ids.Remove(parcelId);
                if (ids.Count == 0) parcelIdsByCell.Remove(cell);
            }
        }

        // 接道街路の逆引きインデックスからも外す
        if (entry.frontPathId > 0
            && parcelIdsByFrontPath.TryGetValue(entry.frontPathId, out HashSet<int> byPath))
        {
            byPath.Remove(parcelId);
            if (byPath.Count == 0) parcelIdsByFrontPath.Remove(entry.frontPathId);
        }

        parcelSpatialCache.Remove(parcelId);
    }

    /// <summary>
    /// 指定した街路（front_path_id）に面する区画IDの集合を返す。
    /// 「この道路に面している区画」をバックエンド通信なしで解決するための汎用の逆引き。
    /// front_path_id 未取得（スキーマ未再生成）の場合は常に空集合を返す。
    /// </summary>
    public IReadOnlyCollection<int> GetParcelIdsByFrontPath(int pathId)
    {
        if (pathId > 0 && parcelIdsByFrontPath.TryGetValue(pathId, out HashSet<int> ids))
        {
            return ids;
        }
        return System.Array.Empty<int>();
    }

    /// <summary>
    /// 指定ワールド座標（XZ）を含む区画のIDを返す。見つからなければ -1。
    /// 起伏地形で区画のMeshColliderが地形の下に埋まってレイキャストで拾えない場合に、
    /// InputHandler が地形ヒット点から区画を解決するためのフォールバックとして使う
    /// （区画は概念的に「地面上の領域」なので、2Dの内外判定で所属を決めてよい）。
    /// セル逆引きインデックス → BBOX → ray casting 内外判定の順で絞り込むため、
    /// マウス移動フレームごとに呼ばれても全区画の線形走査は発生しない。
    /// </summary>
    public int GetParcelIdAtPoint(Vector3 worldPos)
    {
        Vector2Int cell = PositionToCell(worldPos);
        if (!parcelIdsByCell.TryGetValue(cell, out HashSet<int> ids)) return -1;

        Vector2 point = new Vector2(worldPos.x, worldPos.z);
        foreach (int id in ids)
        {
            if (!parcelSpatialCache.TryGetValue(id, out ParcelSpatialEntry entry)) continue;
            if (!entry.bbox.Contains(point)) continue;
            if (IsPointInPolygon(point, entry.polygon))
            {
                // 区画同士は重ならないため、最初にマッチした区画を返してよい
                return id;
            }
        }
        return -1;
    }

    /// <summary>
    /// 2Dの点が多角形の内部にあるかを判定する（ray casting algorithm。ZoneManager と同アルゴリズム）。
    /// polygon は [i][0]=X, [i][1]=Z のワールド座標。
    /// </summary>
    private static bool IsPointInPolygon(Vector2 point, float[][] polygon)
    {
        bool inside = false;
        int j = polygon.Length - 1;
        for (int i = 0; i < polygon.Length; i++)
        {
            float xi = polygon[i][0], zi = polygon[i][1];
            float xj = polygon[j][0], zj = polygon[j][1];
            if (((zi > point.y) != (zj > point.y)) &&
                (point.x < (xj - xi) * (point.y - zi) / (zj - zi) + xi))
            {
                inside = !inside;
            }
            j = i;
        }
        return inside;
    }

    private void RegisterWithVisualModeHandler()
    {
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.RegisterObject(InputObjectType.PARCEL, this);
        }
    }

    private void OnDestroy()
    {
        TerrainManager.OnTerrainTileReady -= OnTerrainTileReady;
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.UnregisterObject(InputObjectType.PARCEL, this);
        }
    }

    /// <summary>
    /// セル単位の高解像Terrainが後から生成されたとき、既存パーセルの上面・枠線を再サンプルする。
    /// </summary>
    private void OnTerrainTileReady(Vector2Int cellIndex, Terrain terrain)
    {
        Utils.InvalidateHeightCache();
        CalculateCellBounds(cellIndex, out float startX, out float endX, out float startZ, out float endZ);
        foreach (var kvp in parcelCache)
        {
            if (ParcelRoughlyInCell(kvp.Value, startX, endX, startZ, endZ))
            {
                RefreshParcelConformingToTerrain(kvp.Value);
            }
        }
    }

    private bool ParcelRoughlyInCell(ParcelSummary parcelData, float startX, float endX, float startZ, float endZ)
    {
        float[] centerPoint = parcelData.GetCenterPointArray();
        if (centerPoint[0] >= startX && centerPoint[0] < endX &&
            centerPoint[1] >= startZ && centerPoint[1] < endZ)
        {
            return true;
        }

        float[][] polygon = parcelData.GetPolygonArray();
        if (polygon == null) return false;
        foreach (float[] pt in polygon)
        {
            if (pt != null && pt.Length >= 2 &&
                pt[0] >= startX && pt[0] < endX &&
                pt[1] >= startZ && pt[1] < endZ)
            {
                return true;
            }
        }

        return false;
    }

    private void RefreshParcelConformingToTerrain(ParcelSummary parcelData)
    {
        GameObject parcelGO = GetParcelObject(parcelData.parcel_id);
        if (parcelGO == null) return;

        float[][] polygon = parcelData.GetPolygonArray();
        float[] centerPoint = parcelData.GetCenterPointArray();
        Vector2[] points = new Vector2[polygon.Length];
        for (int i = 0; i < polygon.Length; i++)
        {
            points[i] = new Vector2(polygon[i][0], polygon[i][1]);
        }

        Vector2 centroid = new Vector2(centerPoint[0], centerPoint[1]);
        BuildParcelTopLocalYOffsets(points, centroid, out float[] topLocalY, out float baseWorldY);

        parcelGO.transform.position = new Vector3(centroid.x, baseWorldY, centroid.y);

        MeshFilter mf = parcelGO.GetComponent<MeshFilter>();
        if (mf == null) return;

        Mesh oldMesh = mf.sharedMesh;
        Mesh newMesh = ParcelMeshFactory.GenerateParcelMesh(points, centroid, 0f, parcelData.usage_id, topLocalY);
        mf.mesh = newMesh;
        if (oldMesh != null)
        {
            Destroy(oldMesh);
        }

        MeshCollider col = parcelGO.GetComponent<MeshCollider>();
        if (col != null)
        {
            col.sharedMesh = newMesh;
        }

        RemoveParcelOutline(parcelData.parcel_id);
        CreateParcelOutline(parcelData.parcel_id, parcelData);
    }

    /// <summary>
    /// パーセル上面の各頂点が地形の標高に沿うよう、重心を基準としたローカルY（メートル）を求める。
    /// </summary>
    private void BuildParcelTopLocalYOffsets(Vector2[] points, Vector2 centroid, out float[] topVertexLocalY, out float baseWorldY)
    {
        float baseGround = Utils.GetGroundHeight(new Vector3(centroid.x, 1000f, centroid.y));
        baseWorldY = baseGround + ParcelSurfaceOffset;
        topVertexLocalY = new float[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            float h = Utils.GetGroundHeight(new Vector3(points[i].x, 1000f, points[i].y)) + ParcelSurfaceOffset;
            topVertexLocalY[i] = h - baseWorldY;
        }
    }

    /// <summary>
    /// 環境スコア色分けモードに対応する取得先エンドポイント。対象外モードは null。
    /// </summary>
    private static string ResolveEnvironmentEndpoint(int mode)
    {
        switch (mode)
        {
            case VisualModeParcel.NOISE: return "/city/noise";
            case VisualModeParcel.AIR_POLLUTION: return "/city/pollution";
            case VisualModeParcel.CRIME: return "/city/crime";
            case VisualModeParcel.SUNSHINE: return "/city/sunshine";
            default: return null;
        }
    }

    /// <summary>
    /// 環境スコア色分けモードに切り替わったとき、そのモードのスコアを取得する。
    /// エポック実行で値が変わるため、同じモードを選び直したときは取得し直す。
    /// </summary>
    private void RequestEnvironmentScores(int mode)
    {
        string endpoint = ResolveEnvironmentEndpoint(mode);
        requestedEnvironmentMode = endpoint != null ? mode : 0;
        if (endpoint == null) return;

        if (environmentCoroutine != null) StopCoroutine(environmentCoroutine);
        environmentCoroutine = StartCoroutine(FetchEnvironmentScoresCoroutine(mode, endpoint));
    }

    private IEnumerator FetchEnvironmentScoresCoroutine(int mode, string endpoint)
    {
        yield return ApiClient.Get<EnvironmentHeatmapResponse>(
            endpoint,
            data =>
            {
                // 取得完了までに別モードへ切り替わっていた場合はレスポンスを破棄する。
                if (requestedEnvironmentMode != mode) return;

                environmentScores.Clear();
                var ids = data?.parcel_ids;
                var values = data?.values;
                if (ids != null && values != null)
                {
                    int count = Mathf.Min(ids.Count, values.Count);
                    for (int i = 0; i < count; i++)
                    {
                        environmentScores[ids[i]] = values[i] / 65535f;
                    }
                }
                loadedEnvironmentMode = mode;
                ForceOutlineRedraw();
            },
            error => Debug.LogError($"ParcelManager: Failed to fetch environment scores ({endpoint}): {error}"));
        environmentCoroutine = null;
    }

    public void ApplyVisualMode(int mode)
    {
        EnsureOverlayRenderer();

        currentParcelVisualMode = mode;

        // 環境スコアの色分けはバックエンドから別途取得した値を使うため、
        // モード切り替えのタイミングで取得を仕掛ける。
        RequestEnvironmentScores(mode);

        // データ可視化モードでは枠線も描画し、さらに色付きポリゴンを重ねる。
        // MODE_HIDDEN ではオーバーレイ全体を非表示にする。
        bool shouldShowOverlay = mode != VisualMode.MODE_HIDDEN;
        if (overlayMeshObj != null)
        {
            // dataVisMeshObj は overlayMeshObj の子のため、表示ON/OFFはここで連動する。
            overlayMeshObj.SetActive(shouldShowOverlay);
        }

        // モード変更時に即時再描画を要求する。
        // カメラが動いていなくても新しいデータ可視化が反映されるようにする。
        if (shouldShowOverlay)
        {
            ForceOutlineRedraw();
        }
    }

    private void Update()
    {
        if (overlayMeshObj == null || !overlayMeshObj.activeSelf)
            return;

        Camera cam = Camera.main;
        if (cam == null) return;

        // カメラの位置・回転が閾値未満の変化しかない場合は、
        // 3D空間メッシュの再構築をスキップする。
        // 枠線はカメラが 1m 未満動いたり 2° 未満回転した場合、
        // 視覚的に大きな変化はないため、毎フレーム更新する必要性が低い。
        Vector3 currentCamPos = cam.transform.position;
        Quaternion currentCamRot = cam.transform.rotation;
        bool posChanged = Vector3.Distance(currentCamPos, lastCamPos) > CAMERA_POS_THRESHOLD;
        bool rotChanged = Quaternion.Angle(currentCamRot, lastCamRot) > CAMERA_ROT_THRESHOLD;
        if (!posChanged && !rotChanged)
        {
            return;
        }

        lastCamPos = currentCamPos;
        lastCamRot = currentCamRot;

        // 枠線が1つも無い場合は、持続オーバーレイメッシュを一度クリアして return する。
        // early-return せずここまで来ることで、最後の1区画を削除した際に
        // 前フレームのメッシュが残り続ける問題を防ぐ。
        // 削除側の ForceOutlineRedraw() が lastCamPos をリセットするため、
        // カメラ静止中でもこの分岐に到達する。
        if (outlines.Count == 0)
        {
            overlayRenderer.ClearMesh();
            dataVisRenderer.ClearMesh();
            return;
        }

        UpdateAllParcelOutlines(cam);
    }

    /// <summary>
    /// 全パーセルの枠線とハイライトを更新する。
    /// ワールド座標をそのまま OverlayMeshRenderer に渡して3D空間メッシュとしてバッチ描画する。
    /// highlightedParcelId が -1 でない場合、該当パーセルの塗りつぶしポリゴンも追加する。
    /// データ可視化モード（地価・建築可能判定など）の場合は、全パーセルに色付きポリゴンを描画する。
    /// </summary>
    private void UpdateAllParcelOutlines(Camera cam)
    {
        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;
        float nearClipWithMargin = cam.nearClipPlane + 0.001f;
        Plane nearPlane = new Plane(camFwd, camPos + camFwd * nearClipWithMargin);

        overlayRenderer.BeginFrame();
        dataVisRenderer.BeginFrame();

        // ハイライト対象パーセルの塗りつぶしポリゴン
        if (highlightedParcelId >= 0 && parcelIdToOutlineIndex.TryGetValue(highlightedParcelId, out int hlIdx))
        {
            DrawHighlightPolygon(cam, outlines[hlIdx], HighlightColor);
        }

        // パス干渉ハイライト対象の区画を分類色で塗りつぶす（マウスオーバーとは独立）
        if (interferenceParcelColors.Count > 0)
        {
            foreach (var kv in interferenceParcelColors)
            {
                if (parcelIdToOutlineIndex.TryGetValue(kv.Key, out int ifIdx))
                {
                    DrawHighlightPolygon(cam, outlines[ifIdx], kv.Value);
                }
            }
        }

        // データ可視化モードかどうかを判定
        bool isDataVisMode = currentParcelVisualMode >= 200;

        for (int oi = 0; oi < outlines.Count; oi++)
        {
            ParcelOutlineData outline = outlines[oi];
            Vector3[] pts = outline.worldPoints;

            int parcelId = outline.parcelId;
            ParcelSummary parcelData;
            if (!parcelCache.TryGetValue(parcelId, out parcelData)) continue;

            // キャッシュされた中心点を用いてカメラとの距離（の2乗）を算出する。
            // 毎フレームのループ処理内での重い Terrain 標高取得（Utils.GetGroundHeight）を回避するための設計。
            Vector3 center = outline.center;
            float distSq = (camPos.x - center.x) * (camPos.x - center.x) + 
                           (camPos.y - center.y) * (camPos.y - center.y) + 
                           (camPos.z - center.z) * (camPos.z - center.z);

            // 完全に非表示となる距離の閾値を超えている場合は、平方根の計算を行わずに即座にスキップする。
            // 大半の遠方区画に対する不要な計算（Mathf.Sqrt）を削減し、CPU負荷を軽減するためのカリング処理。
            if (distSq >= DISTANCE_INVISIBLE_SQ) continue;

            // 2乗距離をベースにアルファ値を求める。
            float alpha = CalculateAlphaByDistanceSq(distSq);
            if (alpha <= 0.01f) continue;

            // データ可視化モードの場合は色付きポリゴンを描画する
            if (isDataVisMode)
            {
                DrawDataVisPolygon(outline, alpha);
            }

            // 境界線はデータ可視化モードでも描画する（ポリゴンの輪郭として機能する）
            int ptCount = pts.Length;
            for (int i = 0; i < ptCount; i++)
            {
                int nextI = (i + 1) % ptCount;
                Vector3 ws = pts[i];
                Vector3 we = pts[nextI];

                Vector3 ca = ws;
                Vector3 cb = we;
                if (!ClipLineToNearPlaneWithPlane(ref ca, ref cb, nearPlane))
                    continue;

                Color c = parcelLineColor;
                c.a = alpha;
                overlayRenderer.AddSolidLine(ca, cb, c, LINE_THICKNESS);
            }
        }

        // 選択中区画の接道(front_path)矢印を重ねて描画する。
        DrawFrontPathArrow(nearPlane);

        overlayRenderer.EndFrame(cam);
        dataVisRenderer.EndFrame(cam);
    }

    /// <summary>
    /// ハイライト対象パーセルのポリゴンをワールド座標のまま塗りつぶし描画に追加する。
    /// </summary>
    private void DrawHighlightPolygon(Camera cam, ParcelOutlineData outline, Color color)
    {
        Vector3[] pts = outline.worldPoints;
        List<Vector3> worldPoints = new List<Vector3>(pts.Length);

        for (int i = 0; i < pts.Length; i++)
        {
            Vector3 worldPt = pts[i];
            worldPt.y = Utils.GetGroundHeight(new Vector3(worldPt.x, 1000f, worldPt.z)) + HighlightHeightOffset;
            worldPoints.Add(worldPt);
        }

        if (worldPoints.Count >= 3)
        {
            overlayRenderer.AddPolygon(worldPoints, color);
        }
    }

    /// <summary>
    /// データ可視化モードに応じてパーセルを色付きポリゴンとして描画する。
    /// UIの凡例定義（DisplayOptionsPanel の getLegendInfo）と同じ配色を使用する。
    /// FILTER系モードでは建築不可のパーセルは描画しない（透明）。
    /// 描画先は dataVisRenderer(Custom/OverlayFillTerrainConform)。三角形内部の
    /// 地形追従はシェーダーのテッセレーションが担うため、CPU側は区画ポリゴンの
    /// 単純なファン分割を投げるだけでよい(頂点Yは高さマップ未アップロード時の
    /// フォールバック値として枠線と同じ角の地形高さを使う)。
    /// </summary>
    private void DrawDataVisPolygon(ParcelOutlineData outline, float alpha)
    {
        int parcelId = outline.parcelId;
        VisualParcelObject vpo = null;
        parcelVisualObjects.TryGetValue(parcelId, out vpo);
        if (vpo == null) return;

        VisualParcelStats stats = vpo.Stats;
        if (stats == null) return;

        Color? colorNullable = ResolveDataVisColor(currentParcelVisualMode, stats, outline.center, parcelId);
        if (!colorNullable.HasValue) return;

        Color color = colorNullable.Value;
        color.a = DataVisAlpha * alpha;

        Vector3[] pts = outline.worldPoints;
        if (pts.Length < 3) return;

        List<Vector3> triVerts = new List<Vector3>((pts.Length - 2) * 3);
        for (int i = 1; i < pts.Length - 1; i++)
        {
            triVerts.Add(pts[0]);
            triVerts.Add(pts[i]);
            triVerts.Add(pts[i + 1]);
        }

        if (triVerts.Count >= 3)
        {
            dataVisRenderer.AddPreTriangulated(triVerts, color);
        }
    }

    /// <summary>
    /// 現在のデータ可視化モードとパーセルの統計値から、描画すべき色を決定する。
    /// 建築不可やデータなしの場合は null を返し、描画をスキップする。
    ///
    /// 各 FILTER モードでは、建築規制（allowed_bd_mask）に加えて
    /// 土地区画種類（parcel_type）との物理的互換性も判定する。
    /// - 港湾（FILTER_PORT）は沿岸土地区画（PARCEL_SHORE）にのみ建設可能
    /// - それ以外は通常土地区画（PARCEL_LAND）にのみ建設可能
    ///
    /// ZONE_COLOR モードでは、worldCenter(パーセル中心のワールド座標)から
    /// 所属地区を空間判定(ZoneManager.GetZoneIdAtPoint)で求め、その地区の識別色を返す。
    /// ParcelSummary には zone_id が含まれないため、地区は空間的に解決する。
    /// どの地区にも属さない区画は null を返して描画をスキップする。
    /// </summary>
    private Color? ResolveDataVisColor(int mode, VisualParcelStats stats, Vector3 worldCenter, int parcelId)
    {
        switch (mode)
        {
            case VisualModeParcel.LAND_PRICE:
            {
                float t = Mathf.Clamp01(stats.land_price / 10000f);
                // UI凡例: 白(#ffffff) → 緑(#4caf50)
                return Color.Lerp(new Color(1f, 1f, 1f), HexToColor(0x4c, 0xaf, 0x50), t);
            }
            case VisualModeParcel.ZONE_COLOR:
            {
                if (ZoneManager.Instance == null) return null;
                int zoneId = ZoneManager.Instance.GetZoneIdAtPoint(worldCenter);
                if (zoneId <= 0) return null;
                return ZoneManager.Instance.GetZoneColor(zoneId);
            }
            case VisualModeParcel.FILTER_HOUSE:
                return IsBuildable(stats, Constants.BUILDING_HOUSE) ? HexToColor(0xa5, 0xd6, 0xa7) : (Color?)null;
            case VisualModeParcel.FILTER_APARTMENT:
                return IsBuildable(stats, Constants.BUILDING_APARTMENT) ? HexToColor(0x2e, 0x7d, 0x32) : (Color?)null;
            case VisualModeParcel.FILTER_COMMERCIAL:
                return IsBuildable(stats, Constants.BUILDING_RETAIL) ? HexToColor(0x21, 0x96, 0xf3) : (Color?)null;
            case VisualModeParcel.FILTER_OFFICE:
                return IsBuildable(stats, Constants.BUILDING_OFFICE) ? HexToColor(0x9c, 0x27, 0xb0) : (Color?)null;
            case VisualModeParcel.FILTER_FACTORY:
                return IsBuildable(stats, Constants.BUILDING_FACTORY) ? HexToColor(0xfb, 0xc0, 0x2d) : (Color?)null;
            case VisualModeParcel.FILTER_FARM:
                return IsBuildable(stats, Constants.BUILDING_FARM) ? HexToColor(0x79, 0x55, 0x48) : (Color?)null;
            case VisualModeParcel.FILTER_MINE:
                return IsBuildable(stats, Constants.BUILDING_MINE) ? HexToColor(0xff, 0x98, 0x00) : (Color?)null;
            case VisualModeParcel.FILTER_PORT:
                return IsBuildable(stats, Constants.BUILDING_PORT) ? HexToColor(0x00, 0xbc, 0xd4) : (Color?)null;
            case VisualModeParcel.FILTER_OTHER:
                return IsBuildable(stats, Constants.BUILDING_OTHER) ? HexToColor(0x9e, 0x9e, 0x9e) : (Color?)null;
            case VisualModeParcel.NOISE:
                // UI凡例: 薄黄(#fde68a) → 濃橙(#e65100)
                return ResolveEnvironmentColor(mode, parcelId, HexToColor(0xfd, 0xe6, 0x8a), HexToColor(0xe6, 0x51, 0x00));
            case VisualModeParcel.AIR_POLLUTION:
                // UI凡例: 薄緑灰(#d1e7dd) → 濃紫(#4a148c)
                return ResolveEnvironmentColor(mode, parcelId, HexToColor(0xd1, 0xe7, 0xdd), HexToColor(0x4a, 0x14, 0x8c));
            case VisualModeParcel.CRIME:
                // UI凡例: 薄赤(#ffcdd2) → 濃赤(#b71c1c)
                return ResolveEnvironmentColor(mode, parcelId, HexToColor(0xff, 0xcd, 0xd2), HexToColor(0xb7, 0x1c, 0x1c));
            case VisualModeParcel.SUNSHINE:
                // UI凡例: 薄黄(#fff59d 日向) → 濃青灰(#37474f 影)
                return ResolveEnvironmentColor(mode, parcelId, HexToColor(0xff, 0xf5, 0x9d), HexToColor(0x37, 0x47, 0x4f));
            default:
                return null;
        }
    }

    /// <summary>
    /// 環境スコアの色を解決する。スコア未取得の区画は塗らない(null)。
    /// 騒音・大気汚染・治安のスコアはそのエポックの最小値〜最大値を 0〜1 まで
    /// 引き伸ばした相対値(色の濃さ=都市内での相対的な悪さ)。日照だけは
    /// 0〜1 固定の絶対値(影時間割合ベース)で、色の濃さがそのまま絶対水準を表す。
    /// </summary>
    private Color? ResolveEnvironmentColor(int mode, int parcelId, Color low, Color high)
    {
        if (loadedEnvironmentMode != mode) return null;
        if (!environmentScores.TryGetValue(parcelId, out float score)) return null;
        return Color.Lerp(low, high, Mathf.Clamp01(score));
    }

    /// <summary>
    /// 指定した建物タイプがパーセルに建設可能かを判定する。
    /// 建築規制（allowed_bd_mask）と物理的互換性（parcel_type）の両方を満たす場合にtrueを返す。
    /// - 港湾建物（BUILDING_PORT）は沿岸土地区画（PARCEL_SHORE）にのみ建設可能
    /// - それ以外の建物は通常土地区画（PARCEL_LAND）にのみ建設可能
    /// </summary>
    private bool IsBuildable(VisualParcelStats stats, int buildingTypeBit)
    {
        // 建築規制チェック
        if ((stats.allowed_bd_mask & buildingTypeBit) == 0) return false;

        // 物理的互換性チェック
        if ((buildingTypeBit & Constants.BUILDING_PORT) != 0)
        {
            return stats.parcel_type == Constants.PARCEL_SHORE;
        }
        return stats.parcel_type == Constants.PARCEL_LAND;
    }

    /// <summary>
    /// 0〜255 のRGB値から Unity の Color を生成する。
    /// UI側の16進数カラーコードと直感的に対応させるためのヘルパー。
    /// </summary>
    private static Color HexToColor(int r, int g, int b)
    {
        return new Color(r / 255f, g / 255f, b / 255f);
    }

    /// <summary>
    /// パーセルの枠線データを登録する。ポリゴン頂点をワールド座標で保持し、
    /// 実際のスクリーン座標変換は毎フレームの UpdateAllParcelOutlines で行う。
    /// </summary>
    private void CreateParcelOutline(int parcelId, ParcelSummary parcelData)
    {
        EnsureOverlayRenderer();

        RemoveParcelOutline(parcelId);

        float[][] polygon = parcelData.GetPolygonArray();
        Vector3[] worldPoints = new Vector3[polygon.Length];

        for (int i = 0; i < polygon.Length; i++)
        {
            float x = polygon[i][0];
            float z = polygon[i][1];
            float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z)) + OutlineHeightOffset;
            worldPoints[i] = new Vector3(x, y, z);
        }

        // 土地区画の中心座標を取得し、その位置の標高を含めた3D中心点（center）を計算して保持する。
        // これにより、毎フレームの描画更新処理での Terrain 標高取得を省略し、距離判定を高速化する。
        float[] centerArr = parcelData.GetCenterPointArray();
        float cx = centerArr[0];
        float cz = centerArr[1];
        float cy = Utils.GetGroundHeight(new Vector3(cx, 1000f, cz));
        Vector3 center = new Vector3(cx, cy, cz);

        ParcelOutlineData data = new ParcelOutlineData
        {
            parcelId = parcelId,
            center = center,
            worldPoints = worldPoints
        };

        parcelIdToOutlineIndex[parcelId] = outlines.Count;
        outlines.Add(data);

        // カメラが動いていなくても新規区画の枠線を次フレームで即描画させる。
        ForceOutlineRedraw();
    }

    private void RemoveParcelOutline(int parcelId)
    {
        if (!parcelIdToOutlineIndex.TryGetValue(parcelId, out int index))
            return;

        int lastIndex = outlines.Count - 1;
        if (index != lastIndex)
        {
            ParcelOutlineData moved = outlines[lastIndex];
            outlines[index] = moved;
            parcelIdToOutlineIndex[moved.parcelId] = index;
        }

        outlines.RemoveAt(lastIndex);
        parcelIdToOutlineIndex.Remove(parcelId);

        if (highlightedParcelId == parcelId)
        {
            highlightedParcelId = -1;
        }

        // カメラが動いていなくても削除された枠線を次フレームで即消去させる。
        ForceOutlineRedraw();
    }

    /// <summary>
    /// 指定パーセルをハイライト表示する。ParcelInputHandler.onHoverStart から呼び出される。
    /// ハイライト状態の変更を次のフレームで即座に反映するため、カメラ位置キャッシュをリセットして強制再描画をトリガーする。
    /// </summary>
    public void SetHighlightedParcel(int parcelId)
    {
        highlightedParcelId = parcelId;
        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// ハイライトを解除する。ParcelInputHandler.onHoverEnd から呼び出される。
    /// ハイライト状態の変更を次のフレームで即座に反映するため、カメラ位置キャッシュをリセットして強制再描画をトリガーする。
    /// </summary>
    public void ClearHighlightedParcel()
    {
        highlightedParcelId = -1;
        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// パス干渉の区画ハイライトを設定する。区画ID→塗りつぶし色のマップを丸ごと差し替える。
    /// 計画中パスが収用/干渉する区画を分類色（blocking=赤/removable=橙/shavable=黄）で塗る。
    /// マウスオーバーの単一ハイライトとは独立で共存する。HighlightManager から呼ぶ。
    /// </summary>
    public void SetInterferenceParcels(Dictionary<int, Color> parcelColors)
    {
        interferenceParcelColors = parcelColors ?? new Dictionary<int, Color>();
        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// パス干渉の区画ハイライトを全て解除する。敷設終了・詳細パネルを閉じたときに呼ぶ。
    /// </summary>
    public void ClearInterferenceParcels()
    {
        if (interferenceParcelColors.Count == 0) return;
        interferenceParcelColors.Clear();
        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// 枠線の強制再描画を要求する。
    /// カメラが動いていなくても次のフレームで必ず枠線が更新されるよう、カメラ位置キャッシュをリセットする。
    /// 土地区画の形状編集確定後など、データが変わったのにカメラが動いていない場合に呼び出す。
    /// </summary>
    public void ForceOutlineRedraw()
    {
        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// カメラと区画の2乗距離を基に、フェード用のアルファ値を計算する。
    /// フェードの計算が必要な中間領域に入った場合のみ平方根を計算することで、
    /// 完全に表示される近距離領域や、完全に非表示となる遠距離領域での平方根（Mathf.Sqrt）の計算負荷を抑える。
    /// </summary>
    private float CalculateAlphaByDistanceSq(float distanceSq)
    {
        if (distanceSq <= DISTANCE_START_FADE_SQ)
        {
            return 1f;
        }
        if (distanceSq >= DISTANCE_INVISIBLE_SQ)
        {
            return 0f;
        }

        // フェード対象となる境界距離範囲内のみで平方根を求める
        float distance = Mathf.Sqrt(distanceSq);
        float fadeRange = DISTANCE_INVISIBLE - DISTANCE_START_FADE;
        float fadeProgress = (distance - DISTANCE_START_FADE) / fadeRange;
        return 1f - fadeProgress;
    }

    /// <summary>
    /// 事前に作成済みの nearPlane を使って線分をクリップする。
    /// 毎フレーム new Plane() を呼ぶオーバーヘッドを避けるため UpdateAllParcelOutlines で一括生成した Plane を使い回す。
    /// </summary>
    private bool ClipLineToNearPlaneWithPlane(ref Vector3 a, ref Vector3 b, Plane nearPlane)
    {
        float da = nearPlane.GetDistanceToPoint(a);
        float db = nearPlane.GetDistanceToPoint(b);

        if (da < 0f && db < 0f) return false;

        if (da < 0f || db < 0f)
        {
            Vector3 dir = b - a;
            float t = da / (da - db);
            Vector3 p = a + dir * t;
            if (da < 0f) a = p; else b = p;
        }

        return true;
    }

    // ================================================================
    // 接道(front_path)矢印表示
    // ================================================================

    /// <summary>
    /// 指定区画の接道(front_path)を青い矢印で3D表示する。
    /// GET /parcels/{id} で詳細を取得し、facade_edge_index の接道辺をまたぐ矢印を描く。
    /// 土地区画詳細パネルを開いたときに IpcEventHandler 経由で呼ばれる。
    /// </summary>
    public void ShowFrontPathIndicator(int parcelId)
    {
        frontPathParcelId = parcelId;
        if (frontPathCoroutine != null)
        {
            StopCoroutine(frontPathCoroutine);
            frontPathCoroutine = null;
        }
        frontPathCoroutine = StartCoroutine(FetchFrontPathCoroutine(parcelId));
    }

    /// <summary>
    /// 接道矢印の表示をクリアする。土地区画詳細パネルを閉じたときに呼ばれる。
    /// </summary>
    public void HideFrontPathIndicator()
    {
        frontPathParcelId = -1;
        if (frontPathCoroutine != null)
        {
            StopCoroutine(frontPathCoroutine);
            frontPathCoroutine = null;
        }
        if (frontPathArrowActive)
        {
            frontPathArrowActive = false;
            ForceOutlineRedraw();
        }
    }

    private IEnumerator FetchFrontPathCoroutine(int parcelId)
    {
        yield return ApiClient.Get<ParcelData>(
            $"/parcels/{parcelId}",
            data =>
            {
                // 取得完了までに別区画へ切り替わっていた場合はレスポンスを破棄する。
                if (frontPathParcelId != parcelId) return;
                BuildFrontPathArrow(data);
                ForceOutlineRedraw();
            },
            error => Debug.LogError($"ParcelManager: Failed to fetch parcel detail for front path: {error}"));
        frontPathCoroutine = null;
    }

    /// <summary>
    /// ParcelData の polygon / center_point / facade_edge_index から接道矢印のワールド座標を構築する。
    /// front_path が無い、または facade_edge_index が不正な場合は非表示にする。
    /// </summary>
    private void BuildFrontPathArrow(ParcelData data)
    {
        frontPathArrowActive = false;

        if (data == null || data.front_path_id <= 0) return;

        float[][] polygon = GeneratedModelUtils.ToFloatJaggedArray(data.polygon);
        float[] center = GeneratedModelUtils.ToFloatArray(data.center_point);
        if (polygon == null || polygon.Length < 2 || center == null || center.Length < 2) return;

        int n = polygon.Length;
        int edgeIndex = data.facade_edge_index;
        if (edgeIndex < 0 || edgeIndex >= n) return;

        float[] a = polygon[edgeIndex];
        float[] b = polygon[(edgeIndex + 1) % n];
        if (a == null || a.Length < 2 || b == null || b.Length < 2) return;

        Vector2 p0 = new Vector2(a[0], a[1]);
        Vector2 p1 = new Vector2(b[0], b[1]);
        Vector2 mid = (p0 + p1) * 0.5f;
        Vector2 centroid = new Vector2(center[0], center[1]);

        // 外向き(道路側)方向: 区画重心から接道辺中点へ向かうベクトル。ポリゴンの CCW/CW に依存せず堅牢。
        Vector2 outward = mid - centroid;
        if (outward.sqrMagnitude < 1e-6f)
        {
            // 退化ケース(中点≒重心)では接道辺の法線を代用する。
            Vector2 edgeDir = (p1 - p0).normalized;
            outward = new Vector2(edgeDir.y, -edgeDir.x);
        }
        outward = outward.normalized;
        Vector2 perp = new Vector2(-outward.y, outward.x);

        Vector2 shaftStart2 = mid - outward * FrontPathArrowHalfLength; // 区画内側
        Vector2 shaftEnd2 = mid + outward * FrontPathArrowHalfLength;   // 道路側(矢じり先端)
        Vector2 headBase2 = shaftEnd2 - outward * FrontPathArrowHeadLength;
        Vector2 headL2 = headBase2 + perp * FrontPathArrowHeadHalfWidth;
        Vector2 headR2 = headBase2 - perp * FrontPathArrowHeadHalfWidth;

        frontPathShaftStart = LiftToGround(shaftStart2);
        frontPathShaftEnd = LiftToGround(shaftEnd2);

        Vector3 tip = frontPathShaftEnd;
        Vector3 left = LiftToGround(headL2);
        Vector3 right = LiftToGround(headR2);

        // カリング方向に依存せず表示されるよう、両面(2三角形)で矢じりを構築する。
        frontPathArrowHead.Clear();
        frontPathArrowHead.Add(tip);
        frontPathArrowHead.Add(left);
        frontPathArrowHead.Add(right);
        frontPathArrowHead.Add(tip);
        frontPathArrowHead.Add(right);
        frontPathArrowHead.Add(left);

        frontPathArrowActive = true;
    }

    /// <summary>XZ座標を地表高さ+オフセットのワールド座標に持ち上げる。</summary>
    private Vector3 LiftToGround(Vector2 xz)
    {
        float y = Utils.GetGroundHeight(new Vector3(xz.x, 1000f, xz.y)) + FrontPathArrowHeightOffset;
        return new Vector3(xz.x, y, xz.y);
    }

    /// <summary>接道矢印をオーバーレイに描画する。UpdateAllParcelOutlines から毎フレーム呼ばれる。</summary>
    private void DrawFrontPathArrow(Plane nearPlane)
    {
        if (!frontPathArrowActive) return;

        Vector3 a = frontPathShaftStart;
        Vector3 b = frontPathShaftEnd;
        if (ClipLineToNearPlaneWithPlane(ref a, ref b, nearPlane))
        {
            overlayRenderer.AddSolidLine(a, b, FrontPathArrowColor, FrontPathArrowThickness);
        }

        if (frontPathArrowHead.Count >= 3)
        {
            overlayRenderer.AddPreTriangulated(frontPathArrowHead, FrontPathArrowColor);
        }
    }

}
