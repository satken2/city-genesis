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
    /// カメラが動いたときに UpdateAllParcelOutlines が OverlayMeshRenderer へ渡す。
    /// 形状・高さが変わるときは CreateParcelOutline が新しいインスタンスに差し替える(中身は書き換えない)。
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
        /// <summary>
        /// データ可視化用のファン分割済み三角形(worldPoints から遅延生成)。
        /// OverlayMeshRenderer.AddPreTriangulated は EndFrame まで参照を保持するので、生成後は変更しない。
        /// 形状・高さが変わると枠線ごと新しいインスタンスに差し替わるため、無効化は不要。
        /// </summary>
        public List<Vector3> fillTriangles;
        /// <summary>区画中心の所属地区ID(空間判定の結果)。zoneRevision が ZoneManager.ZoneRevision と一致する間だけ有効。</summary>
        public int zoneId;
        public int zoneRevision = -1;
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
        /// <summary>中心点（x=ワールドX, y=ワールドZ）。center_point が無いときは BBOX 中心。</summary>
        public Vector2 center;
    }

    /// <summary>区画ID → 空間インデックスエントリ。RegisterConvertedParcelData / RemoveParcel / ClearAllCells で同期する。</summary>
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
    /// 含まれず、接道だけが変わっても version は上がらないため、version 一致時も RefreshCachedSummary で付け替える。
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
    public bool IsLoading { get; private set; }

    private HashSet<Vector2Int> fullyLoadedCells = new HashSet<Vector2Int>();

    /// <summary>
    /// 重心セル（center_point が属するセル＝GameObject の XZ 位置のセル）→ 区画IDの集合。
    /// アンロード時の非表示化と、取得結果に無い区画の削除(SyncParcelsInCells)は重心セルで判定するため、
    /// 全キャッシュ走査をせずセル内の区画だけを引くのに使う。
    /// 登録は RegisterConvertedParcelData、削除は RemoveParcel / ClearAllCells で同期する。
    /// </summary>
    private readonly Dictionary<Vector2Int, HashSet<int>> parcelIdsByCenterCell = new Dictionary<Vector2Int, HashSet<int>>();
    private readonly Dictionary<int, Vector2Int> parcelCenterCellById = new Dictionary<int, Vector2Int>();

    /// <summary>
    /// 常駐から外れ、区画の非表示化を待っているセル。UnloadWorker が予算内で少しずつ処理する。
    /// 処理前・処理中に常駐へ戻ったセルは LoadLodCells が取り除き、ワーカーはそのセルの処理を打ち切る。
    /// </summary>
    private readonly HashSet<Vector2Int> pendingDeactivateCells = new HashSet<Vector2Int>();
    private readonly List<int> unloadScratch = new List<int>();
    private Coroutine unloadCoroutine;
    private const int UnloadDeactivateBatch = 32;
    private const int UnloadDestroyBatch = 16;

    /// <summary>
    /// UnloadWorker が非表示化の途中で制御を返しているセルと、その間に常駐へ戻ったか。
    /// 戻って再表示された後にまた外れると非表示化待ちには残っているので、待ちの有無だけでは再表示を見逃す。
    /// </summary>
    private bool hasDeactivatingCell;
    private Vector2Int deactivatingCell;
    private bool deactivatingCellReentered;

    /// <summary>
    /// 非表示化を終えた常駐外セルの古い順リスト。再入場時の再生成を避けるため区画を非表示のまま保持するが、
    /// 探索に比例して増え続けないよう、上限を超えた古いセルから区画を破棄する(UnloadWorker)。
    /// 常駐へ戻ったセルは LoadLodCells が取り除く。
    /// </summary>
    private readonly LinkedList<Vector2Int> retainedUnloadedCells = new LinkedList<Vector2Int>();
    private readonly Dictionary<Vector2Int, LinkedListNode<Vector2Int>> retainedCellNodes = new Dictionary<Vector2Int, LinkedListNode<Vector2Int>>();
    private const int MAX_RETAINED_UNLOADED_CELLS = LodConfig.MAX_RESIDENT_CELLS;

    /// <summary>
    /// 取得(差分適用)が必要なロード済みセル。新規に常駐へ入ったセルと、MarkLoadedCellsStale・強制・常駐集合不変時の全セル。
    /// 取得が成功して適用し終えたセルだけ外すので、取消されたロードの分は次の LoadLodCells で取り直される。
    /// 常にロード済みセル(loadedCells)の部分集合に保つ。
    /// </summary>
    private readonly HashSet<Vector2Int> cellsNeedingFetch = new HashSet<Vector2Int>();

    /// <summary>
    /// 直前の LoadLodCells の常駐集合。同じ集合で再び呼ばれたら(データ再取得か強制)全セルを取り直す安全網に使う。
    /// </summary>
    private HashSet<Vector2Int> lastResidentCells;

    /// <summary>
    /// MarkLoadedCellsStale のたびに増える。取得中に再取得が要求されたら、その取得の完了で要再取得の記録を消さないために使う。
    /// </summary>
    private int staleMarkSerial;

    /// <summary>
    /// /parcels/ の1回の取得件数上限。バックエンドにカーソルが無く先頭から打ち切るため、
    /// 上限に達した応答は不完全とみなしてセル集合を二分して取り直す。
    /// </summary>
    private const int PARCEL_FETCH_LIMIT = 5000;
    private const int FetchSkipBatch = 64;

    /// <summary>
    /// 地形タイル到着で地形追従をやり直す区画IDのキュー。terrainRefreshQueued は二重投入防止用。
    /// 1タイルでセル内の全区画を同期で作り直すと長いフレームになるため、TerrainRefreshWorker が予算内で処理する。
    /// </summary>
    private readonly Queue<int> terrainRefreshQueue = new Queue<int>();
    private readonly HashSet<int> terrainRefreshQueued = new HashSet<int>();
    private Coroutine terrainRefreshCoroutine;

    /// <summary>
    /// 再サンプルした高さと枠線の保持値の差がこれ以下なら、地形は変わっていないとみなして作り直さない。
    /// タイルの再アタッチや版の変わらない再取得でも OnTerrainTileReady は発火するため。
    /// </summary>
    private const float TerrainHeightEpsilon = 0.005f;

    /// <summary>TerrainRefreshWorker が、高さ不変で作り直さなかった区画を何件ごとにまとめて予算を返すか。</summary>
    private const int TerrainRefreshSkipBatch = 16;
    private const float PolygonXZEpsilon = 1e-4f;

    /// <summary>地形追従の高さサンプルの作業領域。同期処理の中でだけ使い、yield をまたいで保持しない。</summary>
    private float[] groundSampleScratch = new float[32];

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

    /// <summary>
    /// 海上区画を海面板より上に出すオフセット（メートル）。
    /// 海面板はシーン上で worldY=0.001 に置かれており、地形用の微小オフセットだけでは
    /// 差が1mmしかなく、レイキャストが海面に勝てる保証がない。
    /// </summary>
    private const float SeaSurfaceClearance = 0.05f;

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
            RenderWorkScheduler.Start(this, WaitAndRegisterVisualModeHandler(), "ParcelManager");
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

        // 枠線・ハイライトは起伏地形で地面に埋もれないよう深度テストなし(alwaysOnTop)で描く。
        overlayRenderer = OverlayMeshRenderer.Create("ParcelOverlayMesh", alwaysOnTop: true, persistent: true);
        overlayMeshObj = overlayRenderer.gameObject;

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
        if (parcelObjectsCache.TryGetValue(parcelId, out GameObject obj))
        {
            DestroyParcelObject(obj);
        }
        parcelObjectsCache.Remove(parcelId);
        parcelVisualObjects.Remove(parcelId);

        RemoveParcelOutline(parcelId);
        UnregisterParcelSpatialIndex(parcelId);
        UnregisterParcelCenterCell(parcelId);
        parcelCache.Remove(parcelId);
        // version キャッシュも除去する。残すと同一 parcel_id・同一 version で
        // 削除→再作成された区画が FetchAndApplyCells の version 一致判定でスキップされ、
        // 枠線が復活しなくなる。
        parcelVersionCache.Remove(parcelId);
    }

    /// <summary>
    /// 区画GOを、生成時に new した上面メッシュごと破棄する(GO の破棄だけでは Mesh が残る)。
    /// MeshCollider は同じ Mesh を共有しているので別に破棄しない。
    /// </summary>
    private static void DestroyParcelObject(GameObject obj)
    {
        if (obj == null) return;
        MeshFilter mf = obj.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
        Destroy(obj);
    }

    /// <summary>
    /// セルを非表示化待ちに積む。区画は破棄せず非表示にするだけで、実際の SetActive(false) は UnloadWorker が行う。
    /// </summary>
    protected override void UnloadCell(Vector2Int cellIndex)
    {
        pendingDeactivateCells.Add(cellIndex);
        fullyLoadedCells.Remove(cellIndex);
        cellsNeedingFetch.Remove(cellIndex);
    }

    private bool HasUnloadWork => pendingDeactivateCells.Count > 0 || retainedUnloadedCells.Count > MAX_RETAINED_UNLOADED_CELLS;

    private void StartUnloadWorker()
    {
        if (unloadCoroutine != null || !HasUnloadWork || !isActiveAndEnabled) return;
        unloadCoroutine = RenderWorkScheduler.Start(this, UnloadWorker(), "ParcelManager", () => HasUnloadWork);
    }

    private void AddRetainedCell(Vector2Int cell)
    {
        RemoveRetainedCell(cell);
        retainedCellNodes[cell] = retainedUnloadedCells.AddLast(cell);
    }

    private void RemoveRetainedCell(Vector2Int cell)
    {
        if (!retainedCellNodes.TryGetValue(cell, out LinkedListNode<Vector2Int> node)) return;
        retainedUnloadedCells.Remove(node);
        retainedCellNodes.Remove(cell);
    }

    /// <summary>
    /// 非表示化待ちのセルを1つずつ取り出し、重心がそのセルにある区画を予算内で非表示にする。
    /// 非表示化待ちが無くなったら、保持上限を超えた古いセルの区画を予算内で破棄する。
    /// カメラ移動で止めないよう activeLodCoroutines には入れない。
    /// </summary>
    private IEnumerator UnloadWorker()
    {
        try
        {
            while (HasUnloadWork)
            {
                if (pendingDeactivateCells.Count > 0)
                {
                    Vector2Int cell = default;
                    foreach (Vector2Int c in pendingDeactivateCells) { cell = c; break; }

                    unloadScratch.Clear();
                    if (parcelIdsByCenterCell.TryGetValue(cell, out HashSet<int> ids)) unloadScratch.AddRange(ids);

                    hasDeactivatingCell = true;
                    deactivatingCell = cell;
                    deactivatingCellReentered = false;
                    bool reentered = false;
                    for (int i = 0; i < unloadScratch.Count; i++)
                    {
                        if (parcelObjectsCache.TryGetValue(unloadScratch[i], out GameObject obj) && obj != null && obj.activeSelf)
                            obj.SetActive(false);
                        if ((i + 1) % UnloadDeactivateBatch == 0)
                        {
                            yield return RenderWorkScheduler.Continue;
                            // 常駐へ戻ったセルは打ち切る。非表示済みの区画は取得側の版一致分岐が再表示する。
                            // 戻った後にまた外れていれば非表示化待ちに残っているので、次の周回で先頭からやり直す。
                            if (deactivatingCellReentered || !pendingDeactivateCells.Contains(cell)) { reentered = true; break; }
                        }
                    }
                    hasDeactivatingCell = false;
                    if (!reentered)
                    {
                        pendingDeactivateCells.Remove(cell);
                        AddRetainedCell(cell);
                    }
                    yield return RenderWorkScheduler.Continue;
                    continue;
                }

                Vector2Int oldest = retainedUnloadedCells.First.Value;
                RemoveRetainedCell(oldest);
                unloadScratch.Clear();
                if (parcelIdsByCenterCell.TryGetValue(oldest, out HashSet<int> oldestIds)) unloadScratch.AddRange(oldestIds);
                for (int i = 0; i < unloadScratch.Count; i++)
                {
                    // 常駐へ戻ったセルの区画は破棄しない(残りは取得側の版一致分岐が再表示する)
                    if (loadedCells.Contains(oldest)) break;
                    RemoveParcel(unloadScratch[i]);
                    if ((i + 1) % UnloadDestroyBatch == 0) yield return RenderWorkScheduler.Continue;
                }
                yield return RenderWorkScheduler.Continue;
            }
        }
        finally
        {
            unloadCoroutine = null;
            hasDeactivatingCell = false;
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
            if (c != null) RenderWorkScheduler.Stop(this, c);
        }
        activeLodCoroutines.Clear();
        IsLoading = false;

        var resident = new HashSet<Vector2Int>(cellSets.farCells);
        bool sameResident = lastResidentCells != null && lastResidentCells.SetEquals(resident);
        lastResidentCells = resident;

        // 不要セルをアンロード
        var toUnload = new List<Vector2Int>();
        foreach (var cell in loadedCells)
        {
            if (!resident.Contains(cell))
                toUnload.Add(cell);
        }
        foreach (var cell in toUnload)
        {
            UnloadCell(cell);
            loadedCells.Remove(cell);
        }

        // 取得するのは新規に常駐へ入ったセルと要再取得のセルだけ。ロード済みセルの区画は、
        // データ再取得(MarkLoadedCellsStale)・強制・常駐集合不変(再取得の安全網)のときに取り直す。
        foreach (var cell in resident)
        {
            if (loadedCells.Add(cell))
            {
                cellsNeedingFetch.Add(cell);
                pendingDeactivateCells.Remove(cell);
                if (hasDeactivatingCell && deactivatingCell == cell) deactivatingCellReentered = true;
                RemoveRetainedCell(cell);
            }
            else if (isForce || sameResident)
            {
                cellsNeedingFetch.Add(cell);
            }
        }

        StartUnloadWorker();

        if (cellsNeedingFetch.Count > 0)
        {
            IsLoading = true;
            activeLodCoroutines.Add(RenderWorkScheduler.Start(this, TrackParcelLoad(new List<Vector2Int>(cellsNeedingFetch)), "ParcelManager"));
        }
    }

    /// <summary>
    /// 要取得セルの区画を取得・適用し終えるまで IsLoading を立てておく(建物は区画の確定後に生成するため)。
    /// </summary>
    private IEnumerator TrackParcelLoad(List<Vector2Int> cells)
    {
        try { yield return FetchAndApplyCells(cells); }
        finally { IsLoading = false; }
    }

    /// <summary>
    /// セル群の外接矩形で区画を一括取得し、重心がセル群にある区画だけを差分適用する。
    /// 外接矩形は常駐外のセルも含み得るので、重心セルで絞らないと常駐外に区画GOが作られ、どのセルからも非表示化されない。
    /// 応答が件数上限に達したら、打ち切られた応答は適用せずセル群を二分して取り直す。
    /// 単一セルでも上限に達した場合は適用するが、応答に無い区画の削除(同期)はしない。
    /// </summary>
    private IEnumerator FetchAndApplyCells(List<Vector2Int> cells)
    {
        int serialAtRequest = staleMarkSerial;
        CalculateMergedBBox(cells, out float minX, out float minZ, out float maxX, out float maxZ);
        ParcelListResponse response = null;
        yield return ApiClient.GetBulk<ParcelListResponse>(
            $"/parcels/?bbox={minX},{minZ},{maxX},{maxZ}&limit={PARCEL_FETCH_LIMIT}",
            r => response = r,
            timingLabel: "ParcelManager parcels");
        // 失敗したセルは cellsNeedingFetch に残り、次の LoadLodCells で取り直される
        if (response == null) yield break;

        List<ParcelSummary> parcels = response.parcels;
        int count = parcels != null ? parcels.Count : 0;
        if (count >= PARCEL_FETCH_LIMIT && cells.Count > 1)
        {
            SplitCells(cells, out List<Vector2Int> first, out List<Vector2Int> second);
            response = null;
            parcels = null;
            yield return FetchAndApplyCells(first);
            yield return FetchAndApplyCells(second);
            yield break;
        }
        bool truncated = count >= PARCEL_FETCH_LIMIT;

        var cellSet = new HashSet<Vector2Int>(cells);
        var receivedIds = new HashSet<int>();
        int skipped = 0;
        for (int i = 0; i < count; i++)
        {
            ParcelSummary p = parcels[i];
            if (p == null || !TryGetCenterCell(p, out Vector2Int centerCell) || !cellSet.Contains(centerCell))
            {
                if (++skipped % FetchSkipBatch == 0) yield return RenderWorkScheduler.Continue;
                continue;
            }
            receivedIds.Add(p.parcel_id);

            // version比較: キャッシュと同じversionなら再作成しない
            if (parcelVersionCache.TryGetValue(p.parcel_id, out int cachedVersion) && cachedVersion == p.version)
            {
                // 非表示だった既存オブジェクトがあれば再表示する
                if (parcelObjectsCache.TryGetValue(p.parcel_id, out GameObject existing) && existing != null && !existing.activeSelf)
                {
                    existing.SetActive(true);
                }
                RefreshCachedSummary(p);
            }
            else
            {
                CreateOrUpdateParcelObject(p);
                parcelVersionCache[p.parcel_id] = p.version;
            }
            yield return RenderWorkScheduler.Continue;
        }

        // 取得セル群に重心がある区画のうち、応答に含まれないものを削除する(完全な応答のときだけ)
        if (!truncated)
        {
            SyncParcelsInCells(cellSet, receivedIds);
        }
        else
        {
            Debug.LogWarning($"ParcelManager: セル {cells[0]} の区画が取得上限 {PARCEL_FETCH_LIMIT} 件に達したため、応答に無い区画の削除を見送ります。");
        }

        // 取得中に再取得が要求されていたら、この応答は古い可能性があるので要再取得のまま残す
        bool keepStale = serialAtRequest != staleMarkSerial;
        foreach (var cell in cells)
        {
            if (!keepStale) cellsNeedingFetch.Remove(cell);
            fullyLoadedCells.Add(cell);
        }
        ForceOutlineRedraw();
    }

    private static bool TryGetCenterCell(ParcelSummary p, out Vector2Int cell)
    {
        cell = default;
        if (p.center_point == null || p.center_point.Count < 2) return false;
        cell = TerrainManager.WorldToCell(GeneratedModelUtils.ToFloat(p.center_point[0]), GeneratedModelUtils.ToFloat(p.center_point[1]));
        return true;
    }

    /// <summary>
    /// セル群を、セル座標の外接矩形の長辺方向に並べて半分に分ける。
    /// </summary>
    private static void SplitCells(List<Vector2Int> cells, out List<Vector2Int> first, out List<Vector2Int> second)
    {
        int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
        foreach (Vector2Int c in cells)
        {
            if (c.x < minX) minX = c.x;
            if (c.x > maxX) maxX = c.x;
            if (c.y < minZ) minZ = c.y;
            if (c.y > maxZ) maxZ = c.y;
        }
        bool alongX = maxX - minX >= maxZ - minZ;
        var sorted = new List<Vector2Int>(cells);
        sorted.Sort((a, b) =>
        {
            int order = alongX ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y);
            if (order != 0) return order;
            return alongX ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x);
        });
        int half = sorted.Count / 2;
        first = sorted.GetRange(0, half);
        second = sorted.GetRange(half, sorted.Count - half);
    }

    /// <summary>
    /// 重心が指定セル群にあり、APIレスポンスに含まれない区画を削除する。
    /// </summary>
    private void SyncParcelsInCells(HashSet<Vector2Int> cellSet, HashSet<int> receivedIds)
    {
        List<int> idsToRemove = new List<int>();
        foreach (Vector2Int cell in cellSet)
        {
            if (!parcelIdsByCenterCell.TryGetValue(cell, out HashSet<int> ids)) continue;
            foreach (int id in ids)
            {
                if (!receivedIds.Contains(id)) idsToRemove.Add(id);
            }
        }

        foreach (int id in idsToRemove)
        {
            RemoveParcel(id);
        }
    }

    public override void ClearAllCells()
    {
        foreach (var c in activeLodCoroutines)
        {
            if (c != null) RenderWorkScheduler.Stop(this, c);
        }
        activeLodCoroutines.Clear();
        IsLoading = false;
        RenderWorkScheduler.Stop(this, unloadCoroutine);
        unloadCoroutine = null;
        hasDeactivatingCell = false;
        RenderWorkScheduler.Stop(this, terrainRefreshCoroutine);
        terrainRefreshCoroutine = null;
        pendingDeactivateCells.Clear();
        retainedUnloadedCells.Clear();
        retainedCellNodes.Clear();
        cellsNeedingFetch.Clear();
        lastResidentCells = null;
        terrainRefreshQueue.Clear();
        terrainRefreshQueued.Clear();

        foreach (var obj in parcelObjectsCache.Values)
        {
            DestroyParcelObject(obj);
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
        parcelIdsByCenterCell.Clear();
        parcelCenterCellById.Clear();

        if (overlayRenderer != null)
        {
            overlayRenderer.ClearMesh();
        }
        if (dataVisRenderer != null)
        {
            dataVisRenderer.ClearMesh();
        }

        loadedCells.Clear();
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
                RegisterConvertedParcelData(parcelData, polygon, centerPoint);
                UpdateParcelStats(parcelData.parcel_id, parcelData);
                // 形状・高さが枠線の保持値と違うときだけメッシュ・コライダー・枠線を作り直す
                if (centerPoint != null && centerPoint.Length >= 2)
                {
                    RefreshParcelGeometry(parcelData, polygon, new Vector2(centerPoint[0], centerPoint[1]));
                }
            }
        }
        else
        {
            needRecreate = true;
        }

        if (needRecreate)
        {
            RegisterConvertedParcelData(parcelData, polygon, centerPoint);

            Vector2 center = new Vector2(centerPoint[0], centerPoint[1]);
            float[] vertexGround = SampleParcelGround(parcelData, polygon, center, out float centerGround);

            if (existingParcel == null && CellDataLoader.Instance != null && CellDataLoader.Instance.IsInitialLoadComplete)
            {
                Vector2Int cellIndex = TerrainManager.WorldToCell(center.x, center.y);
                if (fullyLoadedCells.Contains(cellIndex) && VisualEffectManager.Instance != null)
                {
                    VisualEffectManager.Instance.PlayConstructionComplete(new Vector3(center.x, centerGround, center.y));
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

            parcelGO.AddComponent<MeshFilter>();
            MeshRenderer mr = parcelGO.AddComponent<MeshRenderer>();
            if (vacantParcelMaterial != null)
            {
                mr.sharedMaterial = vacantParcelMaterial;
            }
            mr.enabled = false;
            parcelGO.AddComponent<MeshCollider>();

            ApplyParcelGeometry(parcelGO, parcelData, polygon, center, vertexGround, centerGround);

            ParcelInputHandler inputHandler = parcelGO.AddComponent<ParcelInputHandler>();
            inputHandler.parcelId = parcelData.parcel_id;

            var vpoComp = parcelGO.AddComponent<VisualParcelObject>();
            parcelVisualObjects[parcelData.parcel_id] = vpoComp;

            var stats = parcelGO.AddComponent<VisualParcelStats>();
            stats.InitFrom(parcelData);

            parcelObjectsCache[parcelData.parcel_id] = parcelGO;
        }
    }

    public IEnumerator FetchParcel(int parcelId)
    {
        yield return ApiClient.Get<ParcelSummary>(
            $"/parcels/{parcelId}",
            parcel =>
            {
                if (parcel == null) return;
                CreateOrUpdateParcelObject(parcel);
                parcelVersionCache[parcel.parcel_id] = parcel.version;
            },
            error => Debug.LogError($"FetchParcel({parcelId}) failed: {error}")
        );
    }

    private void RegisterParcelDataForLines(ParcelSummary parcelData)
    {
        RegisterConvertedParcelData(parcelData, parcelData.GetPolygonArray(), parcelData.GetCenterPointArray());
    }

    /// <summary>
    /// RegisterParcelDataForLines の変換済み座標版。テストがリフレクションで名前だけで引くため、多重定義にしない。
    /// </summary>
    private void RegisterConvertedParcelData(ParcelSummary parcelData, float[][] polygon, float[] centerPoint)
    {
        parcelCache[parcelData.parcel_id] = parcelData;
        RegisterParcelSpatialIndex(parcelData, polygon, centerPoint);
        RegisterParcelCenterCell(parcelData.parcel_id, centerPoint);
    }

    /// <summary>
    /// 区画を重心セルの逆引き(parcelIdsByCenterCell)へ登録する。重心セルが変わった場合は旧セルから外す。
    /// center_point が無い区画は GameObject を持てないため、登録しない。
    /// </summary>
    private void RegisterParcelCenterCell(int parcelId, float[] centerPoint)
    {
        if (centerPoint == null || centerPoint.Length < 2)
        {
            UnregisterParcelCenterCell(parcelId);
            return;
        }
        Vector2Int cell = TerrainManager.WorldToCell(centerPoint[0], centerPoint[1]);
        if (parcelCenterCellById.TryGetValue(parcelId, out Vector2Int oldCell))
        {
            if (oldCell == cell) return;
            UnregisterParcelCenterCell(parcelId);
        }
        if (!parcelIdsByCenterCell.TryGetValue(cell, out HashSet<int> ids))
        {
            ids = new HashSet<int>();
            parcelIdsByCenterCell[cell] = ids;
        }
        ids.Add(parcelId);
        parcelCenterCellById[parcelId] = cell;
    }

    private void UnregisterParcelCenterCell(int parcelId)
    {
        if (!parcelCenterCellById.TryGetValue(parcelId, out Vector2Int cell)) return;
        if (parcelIdsByCenterCell.TryGetValue(cell, out HashSet<int> ids))
        {
            ids.Remove(parcelId);
            if (ids.Count == 0) parcelIdsByCenterCell.Remove(cell);
        }
        parcelCenterCellById.Remove(parcelId);
    }

    /// <summary>
    /// 区画を空間インデックス（parcelSpatialCache / parcelIdsByCell）に登録する。
    /// ポリゴンのAABBを計算し、AABBが重なる全セル（区画は小さいので通常1〜4セル）へ逆引き登録する。
    /// 既登録の場合は一度削除してから登録し直す（形状変更時のセル移動に対応）。
    /// </summary>
    private void RegisterParcelSpatialIndex(ParcelSummary parcelData, float[][] polygon, float[] centerPoint)
    {
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
            frontPathId = frontPathId,
            center = centerPoint != null && centerPoint.Length >= 2
                ? new Vector2(centerPoint[0], centerPoint[1])
                : new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f)
        };

        AddFrontPathIndex(frontPathId, parcelData.parcel_id);

        TerrainManager.WorldRectToCellRange(minX, minZ, maxX, maxZ, out Vector2Int cellMin, out Vector2Int cellMax);
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

        RemoveFrontPathIndex(entry.frontPathId, parcelId);

        parcelSpatialCache.Remove(parcelId);
    }

    /// <summary>接道街路の逆引きインデックスに登録する（front_path_id が未取得=0 のときは登録しない）。</summary>
    private void AddFrontPathIndex(int frontPathId, int parcelId)
    {
        if (frontPathId <= 0) return;
        if (!parcelIdsByFrontPath.TryGetValue(frontPathId, out HashSet<int> byPath))
        {
            byPath = new HashSet<int>();
            parcelIdsByFrontPath[frontPathId] = byPath;
        }
        byPath.Add(parcelId);
    }

    private void RemoveFrontPathIndex(int frontPathId, int parcelId)
    {
        if (frontPathId <= 0 || !parcelIdsByFrontPath.TryGetValue(frontPathId, out HashSet<int> byPath)) return;
        byPath.Remove(parcelId);
        if (byPath.Count == 0) parcelIdsByFrontPath.Remove(frontPathId);
    }

    /// <summary>
    /// version 一致時に、キャッシュの要約を最新の応答へ差し替える。
    /// land_price と front_path_id は VERSION_TRIGGER_PROPS に含まれず、version が同じでも変わり得るため。
    /// version が同じならジオメトリは同じなので、メッシュと空間インデックスの形状は触らない。
    /// </summary>
    private void RefreshCachedSummary(ParcelSummary parcelData)
    {
        if (!parcelCache.ContainsKey(parcelData.parcel_id)) return;
        parcelCache[parcelData.parcel_id] = parcelData;
        UpdateParcelStats(parcelData.parcel_id, parcelData);
        UpdateFrontPathIndex(parcelData.parcel_id, parcelData.GetFrontPathId());
    }

    private void UpdateParcelStats(int parcelId, ParcelSummary parcelData)
    {
        if (!parcelVisualObjects.TryGetValue(parcelId, out VisualParcelObject vpo) || vpo == null) return;
        VisualParcelStats stats = vpo.Stats;
        if (stats != null) stats.InitFrom(parcelData);
    }

    private void UpdateFrontPathIndex(int parcelId, int frontPathId)
    {
        if (!parcelSpatialCache.TryGetValue(parcelId, out ParcelSpatialEntry entry) || entry.frontPathId == frontPathId) return;
        RemoveFrontPathIndex(entry.frontPathId, parcelId);
        AddFrontPathIndex(frontPathId, parcelId);
        entry.frontPathId = frontPathId;
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
        Vector2Int cell = TerrainManager.WorldToCell(worldPos);
        if (!parcelIdsByCell.TryGetValue(cell, out HashSet<int> ids)) return -1;

        Vector2 point = new Vector2(worldPos.x, worldPos.z);
        foreach (int id in ids)
        {
            if (!parcelSpatialCache.TryGetValue(id, out ParcelSpatialEntry entry)) continue;
            if (!entry.bbox.Contains(point)) continue;
            if (PolygonUtils.Contains(entry.polygon, point.x, point.y))
            {
                // 区画同士は重ならないため、最初にマッチした区画を返してよい
                return id;
            }
        }
        return -1;
    }

    /// <summary>
    /// 指定ワールド座標（XZ）が指定区画のポリゴン内にあるかを判定する。
    /// 区画が未ロード（空間キャッシュに無い）場合は false を返す。
    /// </summary>
    public bool IsPointInParcel(int parcelId, Vector3 worldPos)
    {
        if (!parcelSpatialCache.TryGetValue(parcelId, out ParcelSpatialEntry entry)
            || entry.polygon == null || entry.polygon.Length < 3)
        {
            return false;
        }
        return PolygonUtils.Contains(entry.polygon, worldPos.x, worldPos.z);
    }

    /// <summary>
    /// 空間インデックスが持つ変換済みポリゴン(ワールドXZ)を確保なしで返す。共有配列なので書き換えないこと。
    /// 区画が未ロードなら false。
    /// </summary>
    public bool TryGetParcelPolygon(int parcelId, out float[][] polygon)
    {
        polygon = parcelSpatialCache.TryGetValue(parcelId, out ParcelSpatialEntry entry) ? entry.polygon : null;
        return polygon != null;
    }

    /// <summary>
    /// ロード済みの全セルを「要再取得」にする。データ再取得(CellDataLoader.ReloadAllData)の要求時に同期で呼ぶ。
    /// 記録はバッチが取消されても残り、そのセルの取得が成功して適用されたときに消える。
    /// </summary>
    public void MarkLoadedCellsStale()
    {
        cellsNeedingFetch.UnionWith(loadedCells);
        staleMarkSerial++;
    }

    // 区画境界へクランプした点を境界からどれだけ内側へ寄せるか(m)。
    // 境界線上ちょうどの座標は、バックエンド側の座標正規化(丸め)で僅かに区画外へ
    // ずれて内包判定に落ちる可能性があるため、明確に内側へ寄せて送る。
    private const float ParcelClampInnerMargin = 0.05f;

    /// <summary>
    /// ワールド座標（XZ）を指定区画のポリゴン内へクランプする。
    /// 点が区画内ならそのまま、区画外ならポリゴン境界上の最近傍点（から僅かに内側へ
    /// 寄せた点）を返す。Y座標は入力値を維持する。
    /// 接続端子ノードの「施設が立地する区画内でのみ移動可」の制限（ConnectorEditManager）
    /// のように、ドラッグ座標を区画内へ制限する用途で使う。
    /// 区画が未ロード（空間キャッシュに無い）場合は false を返し、clamped には入力値を返す。
    /// </summary>
    public bool TryClampPointToParcel(int parcelId, Vector3 worldPos, out Vector3 clamped)
    {
        clamped = worldPos;
        if (!parcelSpatialCache.TryGetValue(parcelId, out ParcelSpatialEntry entry)
            || entry.polygon == null || entry.polygon.Length < 3)
        {
            return false;
        }

        Vector2 point = new Vector2(worldPos.x, worldPos.z);
        if (PolygonUtils.Contains(entry.polygon, point.x, point.y))
        {
            return true;
        }

        // 区画外: 全エッジへの最近傍点のうち最短のものへ射影する。
        float[][] polygon = entry.polygon;
        float bestSqDist = float.PositiveInfinity;
        Vector2 bestPoint = point;
        for (int i = 0; i < polygon.Length; i++)
        {
            int next = (i + 1) % polygon.Length;
            Vector2 a = new Vector2(polygon[i][0], polygon[i][1]);
            Vector2 b = new Vector2(polygon[next][0], polygon[next][1]);
            Vector2 candidate = PolylineUtils.ClosestPointOnSegment(point, a, b);
            float sqDist = (candidate - point).sqrMagnitude;
            if (sqDist < bestSqDist)
            {
                bestSqDist = sqDist;
                bestPoint = candidate;
            }
        }

        // 境界点から進入方向（外部点→射影点）へ僅かに進めて確実に内側へ寄せる。
        // 鋭角の角などで内側に入らなかった場合は境界点をそのまま使う。
        Vector2 inward = bestPoint - point;
        if (inward.sqrMagnitude > 1e-8f)
        {
            Vector2 nudged = bestPoint + inward.normalized * ParcelClampInnerMargin;
            if (PolygonUtils.Contains(polygon, nudged.x, nudged.y))
            {
                bestPoint = nudged;
            }
        }

        clamped = new Vector3(bestPoint.x, worldPos.y, bestPoint.y);
        return true;
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
    /// セル単位の高解像Terrainが後から生成されたとき、そのセルに中心または頂点がある区画を再追従キューへ積む。
    /// 候補は parcelIdsByCell(BBOXが重なるセル)から引き、実際の再サンプルと作り直しは TerrainRefreshWorker が行う。
    /// </summary>
    private void OnTerrainTileReady(Vector2Int cellIndex, Terrain terrain)
    {
        Utils.InvalidateHeightCache();
        if (!parcelIdsByCell.TryGetValue(cellIndex, out HashSet<int> ids)) return;

        CalculateCellBounds(cellIndex, out float startX, out float endX, out float startZ, out float endZ);
        foreach (int id in ids)
        {
            if (!parcelSpatialCache.TryGetValue(id, out ParcelSpatialEntry entry)) continue;
            if (!ParcelTouchesCell(entry, startX, endX, startZ, endZ)) continue;
            if (terrainRefreshQueued.Add(id)) terrainRefreshQueue.Enqueue(id);
        }
        StartTerrainRefreshWorker();
    }

    private static bool ParcelTouchesCell(ParcelSpatialEntry entry, float startX, float endX, float startZ, float endZ)
    {
        if (entry.center.x >= startX && entry.center.x < endX &&
            entry.center.y >= startZ && entry.center.y < endZ)
        {
            return true;
        }

        foreach (float[] pt in entry.polygon)
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

    private void StartTerrainRefreshWorker()
    {
        if (terrainRefreshCoroutine != null || terrainRefreshQueue.Count == 0 || !isActiveAndEnabled) return;
        terrainRefreshCoroutine = RenderWorkScheduler.Start(this, TerrainRefreshWorker(), "ParcelManager", () => terrainRefreshQueue.Count > 0);
    }

    /// <summary>
    /// 再追従キューの区画を1件ずつ再サンプルし、高さが変わった区画だけ作り直す。
    /// 作り直し(メッシュ生成・コライダーのクッキング・枠線)は1件ごとに、高さ不変の判定だけの区画はまとめて予算を返す。
    /// </summary>
    private IEnumerator TerrainRefreshWorker()
    {
        try
        {
            int unchangedSinceYield = 0;
            while (terrainRefreshQueue.Count > 0)
            {
                int parcelId = terrainRefreshQueue.Dequeue();
                terrainRefreshQueued.Remove(parcelId);
                if (!parcelCache.TryGetValue(parcelId, out ParcelSummary summary)) continue;
                // 処理までにタイルが外れた区画を概観の高さで作り直さない(再入場時のタイル到着で再投入される)
                if (!TouchesAnySurfaceTile(parcelId)) continue;

                if (RefreshParcelConformingToTerrain(summary) || ++unchangedSinceYield >= TerrainRefreshSkipBatch)
                {
                    unchangedSinceYield = 0;
                    yield return RenderWorkScheduler.Continue;
                }
            }
        }
        finally
        {
            terrainRefreshCoroutine = null;
        }
    }

    private bool TouchesAnySurfaceTile(int parcelId)
    {
        TerrainManager terrainManager = Utils.CachedTerrainManager;
        if (terrainManager == null) return true;
        if (!parcelSpatialCache.TryGetValue(parcelId, out ParcelSpatialEntry entry)) return true;
        foreach (Vector2Int cell in entry.cells)
        {
            if (terrainManager.HasSurfaceTile(cell)) return true;
        }
        return false;
    }

    /// <summary>
    /// 区画を現在の地形へ追従させ直す。作り直したときだけ true を返す。
    /// ポリゴンと中心は空間インデックスの変換済み値を使う(GetPolygonArray の毎回の確保を避ける)。
    /// </summary>
    private bool RefreshParcelConformingToTerrain(ParcelSummary parcelData)
    {
        if (parcelSpatialCache.TryGetValue(parcelData.parcel_id, out ParcelSpatialEntry entry))
        {
            return RefreshParcelGeometry(parcelData, entry.polygon, entry.center);
        }
        float[] centerPoint = parcelData.GetCenterPointArray();
        if (centerPoint == null || centerPoint.Length < 2) return false;
        return RefreshParcelGeometry(parcelData, parcelData.GetPolygonArray(), new Vector2(centerPoint[0], centerPoint[1]));
    }

    /// <summary>
    /// 地形を再サンプルし、形状(XZ)・高さのどちらかが枠線の保持値と違う区画だけ作り直す。作り直したときだけ true。
    /// メッシュと枠線は常に同じサンプルから作るので、枠線との比較でメッシュの鮮度も判定できる。
    /// </summary>
    private bool RefreshParcelGeometry(ParcelSummary parcelData, float[][] polygon, Vector2 center)
    {
        GameObject parcelGO = GetParcelObject(parcelData.parcel_id);
        if (parcelGO == null || polygon == null || polygon.Length < 3) return false;

        float[] vertexGround = SampleParcelGround(parcelData, polygon, center, out float centerGround);
        if (HasSameGeometry(parcelData.parcel_id, polygon, center, vertexGround, centerGround)) return false;

        ApplyParcelGeometry(parcelGO, parcelData, polygon, center, vertexGround, centerGround);
        return true;
    }

    /// <summary>
    /// 区画の各頂点と中心の地面高さ(GetParcelBaseY)を1回ずつサンプルする。
    /// 返す配列は作業領域の共有配列なので、長さではなく polygon.Length まで読み、呼び出し元の同期処理の中だけで使うこと。
    /// </summary>
    private float[] SampleParcelGround(ParcelSummary parcelData, float[][] polygon, Vector2 center, out float centerGround)
    {
        if (groundSampleScratch.Length < polygon.Length)
        {
            groundSampleScratch = new float[Mathf.NextPowerOfTwo(polygon.Length)];
        }
        for (int i = 0; i < polygon.Length; i++)
        {
            groundSampleScratch[i] = GetParcelBaseY(parcelData, polygon[i][0], polygon[i][1]);
        }
        centerGround = GetParcelBaseY(parcelData, center.x, center.y);
        return groundSampleScratch;
    }

    private bool HasSameGeometry(int parcelId, float[][] polygon, Vector2 center, float[] vertexGround, float centerGround)
    {
        if (!parcelIdToOutlineIndex.TryGetValue(parcelId, out int index)) return false;
        ParcelOutlineData outline = outlines[index];
        if (outline.worldPoints == null || outline.worldPoints.Length != polygon.Length) return false;
        if (Mathf.Abs(outline.center.x - center.x) > PolygonXZEpsilon
            || Mathf.Abs(outline.center.z - center.y) > PolygonXZEpsilon
            || Mathf.Abs(outline.center.y - centerGround) > TerrainHeightEpsilon)
        {
            return false;
        }
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector3 p = outline.worldPoints[i];
            if (Mathf.Abs(p.x - polygon[i][0]) > PolygonXZEpsilon
                || Mathf.Abs(p.z - polygon[i][1]) > PolygonXZEpsilon
                || Mathf.Abs(p.y - OutlineHeightOffset - vertexGround[i]) > TerrainHeightEpsilon)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// サンプル済みの地面高さから、区画の位置・上面メッシュ・コライダー・枠線をまとめて設定する。
    /// 上面は重心を原点としたローカル座標で、頂点ごとのローカルYを地形に沿わせる(海上区画は全て0)。
    /// </summary>
    private void ApplyParcelGeometry(GameObject parcelGO, ParcelSummary parcelData, float[][] polygon, Vector2 center, float[] vertexGround, float centerGround)
    {
        parcelGO.transform.position = new Vector3(center.x, centerGround + ParcelSurfaceOffset, center.y);

        MeshFilter mf = parcelGO.GetComponent<MeshFilter>();
        if (mf != null)
        {
            Vector2[] points = new Vector2[polygon.Length];
            float[] topLocalY = new float[polygon.Length];
            for (int i = 0; i < polygon.Length; i++)
            {
                points[i] = new Vector2(polygon[i][0], polygon[i][1]);
                topLocalY[i] = vertexGround[i] - centerGround;
            }

            Mesh oldMesh = mf.sharedMesh;
            Mesh newMesh = ParcelMeshFactory.GenerateParcelMesh(points, center, 0f, parcelData.usage_id, topLocalY);
            mf.sharedMesh = newMesh;
            MeshCollider col = parcelGO.GetComponent<MeshCollider>();
            if (col != null)
            {
                col.sharedMesh = newMesh;
            }
            if (oldMesh != null && oldMesh != newMesh)
            {
                Destroy(oldMesh);
            }
        }

        CreateParcelOutline(parcelData.parcel_id, polygon, center, vertexGround, centerGround);
    }

    /// <summary>
    /// 区画が載る面のワールドY（メートル）を返す。
    /// 海上区画（PARCEL_WATER）は地形をサンプルすると海底（worldY&lt;0）が返るため、
    /// バックエンドが海面基準で持つ altitude を使い、さらに海面板を確実に上回らせる。
    /// </summary>
    private static float GetParcelBaseY(ParcelSummary parcelData, float x, float z)
    {
        if (parcelData != null && parcelData.parcel_type == Constants.PARCEL_WATER)
        {
            return parcelData.altitude + SeaSurfaceClearance;
        }
        return Utils.GetGroundHeight(new Vector3(x, 1000f, z));
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

        if (environmentCoroutine != null) RenderWorkScheduler.Stop(this, environmentCoroutine);
        environmentCoroutine = RenderWorkScheduler.Start(this, FetchEnvironmentScoresCoroutine(mode, endpoint), "ParcelManager");
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

            // キャッシュされた中心点を用いてカメラとの距離（の2乗）を算出する。
            // 毎フレームのループ処理内での重い Terrain 標高取得（Utils.GetGroundHeight）を回避するための設計。
            Vector3 center = outline.center;
            float distSq = (camPos.x - center.x) * (camPos.x - center.x) + 
                           (camPos.y - center.y) * (camPos.y - center.y) + 
                           (camPos.z - center.z) * (camPos.z - center.z);

            // 完全に非表示となる距離の閾値を超えている場合は、平方根の計算を行わずに即座にスキップする。
            // 大半の遠方区画に対する不要な計算（Mathf.Sqrt）を削減し、CPU負荷を軽減するためのカリング処理。
            if (distSq >= DISTANCE_INVISIBLE_SQ) continue;
            if (!parcelCache.ContainsKey(outline.parcelId)) continue;

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

    /// <summary>DrawHighlightPolygon の作業用リスト。OverlayMeshRenderer.AddPolygon が中身を複製するので使い回せる。</summary>
    private readonly List<Vector3> highlightPolygonScratch = new List<Vector3>(16);

    /// <summary>
    /// ハイライト対象パーセルのポリゴンをワールド座標のまま塗りつぶし描画に追加する。
    /// </summary>
    private void DrawHighlightPolygon(Camera cam, ParcelOutlineData outline, Color color)
    {
        Vector3[] pts = outline.worldPoints;
        List<Vector3> worldPoints = highlightPolygonScratch;
        worldPoints.Clear();

        // 枠線頂点は既に区画の基準面（海上区画なら海面）へ載っているので、
        // オフセットを差し替えるだけでよい（地形の再サンプルは海上で海底を拾う）。
        for (int i = 0; i < pts.Length; i++)
        {
            Vector3 worldPt = pts[i];
            worldPt.y += HighlightHeightOffset - OutlineHeightOffset;
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

        Vector3[] pts = outline.worldPoints;
        if (pts.Length < 3) return;

        Color? colorNullable = ResolveDataVisColor(currentParcelVisualMode, stats, outline, parcelId);
        if (!colorNullable.HasValue) return;

        Color color = colorNullable.Value;
        color.a = DataVisAlpha * alpha;

        if (outline.fillTriangles == null)
        {
            List<Vector3> triVerts = new List<Vector3>((pts.Length - 2) * 3);
            for (int i = 1; i < pts.Length - 1; i++)
            {
                triVerts.Add(pts[0]);
                triVerts.Add(pts[i]);
                triVerts.Add(pts[i + 1]);
            }
            outline.fillTriangles = triVerts;
        }

        dataVisRenderer.AddPreTriangulated(outline.fillTriangles, color);
    }

    /// <summary>
    /// 現在のデータ可視化モードとパーセルの統計値から、描画すべき色を決定する。
    /// 建築不可やデータなしの場合は null を返し、描画をスキップする。
    ///
    /// 各 FILTER モードでは、建築規制（allowed_bd_mask）に加えて
    /// 土地区画種類（parcel_type）の立地条件も判定する。
    ///
    /// ZONE_COLOR モードでは、区画中心(outline.center)から
    /// 所属地区を空間判定(ZoneManager.GetZoneIdAtPoint)で求め、その地区の識別色を返す。
    /// ParcelSummary には zone_id が含まれないため、地区は空間的に解決する。
    /// 判定結果は枠線データに ZoneManager.ZoneRevision と組でキャッシュし、地区データが変わるまで再判定しない。
    /// どの地区にも属さない区画は null を返して描画をスキップする。
    /// </summary>
    private Color? ResolveDataVisColor(int mode, VisualParcelStats stats, ParcelOutlineData outline, int parcelId)
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
                ZoneManager zoneManager = ZoneManager.Instance;
                if (zoneManager == null) return null;
                int zoneRevision = zoneManager.ZoneRevision;
                if (outline.zoneRevision != zoneRevision)
                {
                    outline.zoneId = zoneManager.GetZoneIdAtPoint(outline.center);
                    outline.zoneRevision = zoneRevision;
                }
                if (outline.zoneId <= 0) return null;
                return zoneManager.GetZoneColor(outline.zoneId);
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
    /// 建築規制（allowed_bd_mask）と立地条件（parcel_type）の両方を満たす場合にtrueを返す。
    /// 立地条件は建設モードで選択中のバリアントの parcel_type を優先する
    /// （bd_type が同じでも陸・沿岸・海上に分かれるため。洋上風力発電所など）。
    /// </summary>
    private bool IsBuildable(VisualParcelStats stats, int buildingTypeBit)
    {
        if ((stats.allowed_bd_mask & buildingTypeBit) == 0) return false;
        return BuildingPlanManager.IsParcelTypeAllowed(stats.parcel_type, buildingTypeBit);
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
    /// 高さは上面メッシュと同じサンプル(SampleParcelGround)を受け取り、同じ座標を二重にサンプルしない。
    /// 既存の枠線は同じ位置で差し替えるので、ホバー中のハイライトは維持される。
    /// </summary>
    private void CreateParcelOutline(int parcelId, float[][] polygon, Vector2 center, float[] vertexGround, float centerGround)
    {
        EnsureOverlayRenderer();

        Vector3[] worldPoints = new Vector3[polygon.Length];
        for (int i = 0; i < polygon.Length; i++)
        {
            worldPoints[i] = new Vector3(polygon[i][0], vertexGround[i] + OutlineHeightOffset, polygon[i][1]);
        }

        // 中心点は地面高さ込みで保持し、毎フレームの距離判定で地形をサンプルしないようにする。
        ParcelOutlineData data = new ParcelOutlineData
        {
            parcelId = parcelId,
            center = new Vector3(center.x, centerGround, center.y),
            worldPoints = worldPoints
        };

        if (parcelIdToOutlineIndex.TryGetValue(parcelId, out int index))
        {
            outlines[index] = data;
        }
        else
        {
            parcelIdToOutlineIndex[parcelId] = outlines.Count;
            outlines.Add(data);
        }

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
            RenderWorkScheduler.Stop(this, frontPathCoroutine);
            frontPathCoroutine = null;
        }
        frontPathCoroutine = RenderWorkScheduler.Start(this, FetchFrontPathCoroutine(parcelId), "ParcelManager");
    }

    /// <summary>
    /// 接道矢印の表示をクリアする。土地区画詳細パネルを閉じたときに呼ばれる。
    /// </summary>
    public void HideFrontPathIndicator()
    {
        frontPathParcelId = -1;
        if (frontPathCoroutine != null)
        {
            RenderWorkScheduler.Stop(this, frontPathCoroutine);
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
