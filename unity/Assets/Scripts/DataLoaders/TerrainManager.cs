using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;

/// <summary>
/// バックエンドの地形APIから標高を取得し、常駐セルの Terrain タイルと全域の超遠景陸地メッシュを配置するマネージャー。
/// ・常駐セル: /api/v1/terrain/surface (セル単位の float32、513格子)。一時TerrainDataへ分割適用してから差し替える
/// ・全景: /api/v1/terrain/far (マップ全体の粗い標高)から超遠景の陸地メッシュを作る
/// ・マップ寸法とセルサイズは /api/v1/terrain/settings、地表種別は /api/v1/city から取得する
/// </summary>
public partial class TerrainManager : BaseApiDataManager
{
    // タイル生成完了を通知するイベント
    public static System.Action<Vector2Int, Terrain> OnTerrainTileReady;

    /// <summary>
    /// LoadLodCellsCoroutine が全セルのロードを完了した時に発火する。
    /// CellDataLoaderがこのイベントを受けてBuilding/Path/Parcelのロードを開始する。
    /// 引数は「このロードバッチで実際に地形タイルが(再)生成・再縫合されたセル」の一覧。
    /// 受信側はこのセルだけを対象に建物・道路の地形追従更新を行い、不要な全量再生成を避ける。
    /// </summary>
    public System.Action<IReadOnlyList<Vector2Int>> OnAllTerrainLodLoaded;

    /// <summary>
    /// このロードバッチで実際にハイトマップが適用(ストリーミング完了)されたセルの集合。
    /// HeightStreamWorker が1セル分の適用を完了するたびに追記され、OnAllTerrainLodLoaded 発火時に
    /// スナップショットして受信側へ渡し、その後クリアする。
    /// コルーチンがキャンセルされた場合はクリアされずに累積し、次回完了時にまとめて通知される
    /// (キャンセルで取りこぼした追従更新を確実に拾うため)。
    /// </summary>
    private readonly HashSet<Vector2Int> updatedTerrainCellsThisLoad = new HashSet<Vector2Int>();

    /// <summary>
    /// セル一辺の長さ（メートル）。バックエンドの terrain settings から取得される。
    /// API応答前に 500m を暫定値として使う。
    /// </summary>
    public static float CellSize { get; private set; } = 500f;

    /// <summary>
    /// ワールドXZが属するUnityセルインデックス。マップ中心が原点なので負値を取る
    /// (バックエンドの0-basedセルへは UnityCellToBackendCell で変換する)。
    /// </summary>
    public static Vector2Int WorldToCell(float x, float z)
    {
        return new Vector2Int(Mathf.FloorToInt(x / CellSize), Mathf.FloorToInt(z / CellSize));
    }

    public static Vector2Int WorldToCell(Vector3 position) => WorldToCell(position.x, position.z);

    /// <summary>ワールドXZ矩形に重なるUnityセルの範囲(両端を含む)。矩形が跨ぐセルの列挙に使う。</summary>
    public static void WorldRectToCellRange(float minX, float minZ, float maxX, float maxZ, out Vector2Int minCell, out Vector2Int maxCell)
    {
        minCell = WorldToCell(minX, minZ);
        maxCell = WorldToCell(maxX, maxZ);
    }

    // === 設定 ===
    [Header("Terrain Settings")]
    public Vector3 terrainSize = new Vector3(CellSize, 1200f, CellSize);

    /// <summary>
    /// 海面(標高0)より下、すなわち海底の水深を地形として表現するために確保する
    /// 「海面下の縦方向スパン(m)」。
    ///
    /// Unity Terrain のハイトマップは [0,1]×terrainSize.y = ローカルY 0〜1200m しか表せず、
    /// 何もしないと海面(標高0)より低い負の標高(海底)を表現できずに worldY=0 へ潰れる
    /// (海の水深が0になり、深度ベースの水面シェーダーで海が透明になる)。
    ///
    /// これを解決するため、地形タイルのベースYを本値だけ下げ(<c>-SeaFloorDepthSpan</c>)、
    /// ハイトマップ正規化を <c>(標高 + SeaFloorDepthSpan) / terrainSize.y</c> にオフセットする。
    /// これにより worldY = ベースY + normalized×terrainSize.y = 実標高 が保たれ、
    /// 海底は worldY&lt;0(最深 -SeaFloorDepthSpan)として描画される。陸地・道路・建物の
    /// 高さ追従(実標高)は不変。
    ///
    /// terrainSize.y(=1200m)の内訳: 海面下 SeaFloorDepthSpan m + 陸地上限 (terrainSize.y - SeaFloorDepthSpan) m。
    /// 本値を深くするほど陸地の高さ上限が下がる。本値を超える深さの海底は最深部にクランプされる
    /// (平坦化するが、そこは十分深く深海色になるため実用上問題ない)。
    /// </summary>
    private const float SeaFloorDepthSpan = 200f;

    /// <summary>
    /// dynamicティアの各セルのversionキャッシュ。
    /// 差分更新時にバックエンドのX-Cell-Versionと比較し、変更があった場合のみ再描画する。
    /// </summary>
    private Dictionary<Vector2Int, int> dynamicCellVersions = new Dictionary<Vector2Int, int>();

    /// <summary>
    /// /terrain/cell-versions の最後の応答(Unity セル単位)。地形の版はエポックと編集でしか変わらず、
    /// その後は必ず ReloadAllData → InvalidateCellVersions が来るので、カメラ由来のバッチでは再利用する。
    /// 取得中に無効化されたら、その応答では dirty を下ろさない(cellVersionsGeneration で判定)。
    /// </summary>
    private Dictionary<Vector2Int, int> cachedCellVersions;
    private float cellVersionsFetchedAt;
    private bool cellVersionsDirty = true;
    private int cellVersionsGeneration;
    // バックエンド側の変更を取りこぼした場合でも、この秒数を超えたら取り直す。
    private const float CELL_VERSIONS_MAX_AGE = 5f;

    /// <summary>
    /// デバッグモードの有効状態。MinimapOverlay などから参照される。
    /// </summary>
    public bool IsDebugMode { get; set; } = false;

    [Header("Material Settings")]
    [SerializeField] private TerrainLayer[] customTerrainLayers;
    [SerializeField] private Material customTerrainMaterial;
    [SerializeField] private bool useCustomMaterial = false;

    /// <summary>
    /// Z-fighting防止用の Custom/DepthOffsetTerrainLit シェーダー。
    /// Shader.Find ではビルド後にシェーダーが見つからなくなることがあるため、
    /// SerializeField で直接参照することでビルドに確実に含める。
    /// </summary>
    [SerializeField] private Shader depthOffsetTerrainShader;

    [Header("UltraFar Land Mesh")]
    /// <summary>
    /// 超遠景(UltraFar)陸地メッシュ用シェーダー Custom/DepthOffsetSimpleLitFar。
    /// per-cell タイル(Offset 1,1)・海面(Offset -1,-1)より大きい正の Offset(4,4)で最奥に描画する。
    /// ビルドに確実に含めるため SerializeField で直接参照する(未設定時は Shader.Find フォールバック)。
    /// </summary>
    [SerializeField] private Shader ultraFarLandShader;
    /// <summary>UltraFar 陸地メッシュのグリッド間隔(m)。小さいほど海岸線・起伏が細かいが頂点が増える。</summary>
    [SerializeField] private float ultraFarGridStep = 80f;
    /// <summary>UltraFar 陸地を海面板の上に保つための頂点Yオフセット(m)。</summary>
    [SerializeField] private float ultraFarHeightOffset = 1.0f;
    /// <summary>
    /// UltraFar 陸地メッシュを「per-cellタイルがロード済みのセル」で地中に沈める量(m)。
    /// 粗い遠景標高と通常タイルの高さ差による突き抜けを防ぐ。
    /// 接続する三角形までロード済み領域に収まる頂点だけを沈め、境界には重なりを残す。
    /// </summary>
    [SerializeField] private float ultraFarSinkAmount = 100f;

    [Header("Default Material Settings")]
    [SerializeField] private float defaultMetallic = 0f;

    // 地表種別(Constants.GROUND_COVER_*)で変わる見た目は GroundCoverPalette のプロファイルが持つ。
    // 下地のテクスチャ・手続きの色・凹凸・スムースネス、照明後の大きな濃淡と色補正の色、岩の色味と波打ち際で、
    // ApplyTerrainMaterialSettings が都市の地表種別のプロファイルを地形タイル・超遠景・都市外背景へ同じく渡す。
    // 地面は法線マップを使わず、凹凸は手続きのディテール法線と風紋で出す
    // (シェーダーは Assets/Shaders/TerrainSurfaceAppearance.hlsl)。
    [Header("Ground Cover")]
    /// <summary>地表種別ごとの見た目。未設定なら Resources の GroundCoverPalette を、それも無ければコード既定値を使う。</summary>
    [SerializeField] private GroundCoverPalette groundCoverPalette;
    /// <summary>0以外なら都市の地表種別の代わりにこの種別(Constants.GROUND_COVER_*)で描く。エディタで種別を見比べるためのもの。</summary>
    [SerializeField] private int groundCoverPreview = 0;

    // 以下は地表種別に依存しない共通の値。
    [Header("Terrain Macro Variation Settings")]
    [SerializeField] private float terrainMacroVariationScale = 350f;

    [Header("Terrain Color Grade Settings")]
    /// <summary>低地色になる標高(m)。海面(0m)付近を想定。</summary>
    [SerializeField] private float terrainColorGradeLowHeight = 0f;
    /// <summary>高地色になる標高(m)。これ以上の標高は一様に高地色へ寄る。</summary>
    [SerializeField] private float terrainColorGradeHighHeight = 300f;

    [Header("Terrain Detail Normal Settings")]
    /// <summary>ディテール法線ノイズのタイリング周期(m)。小さいほど細かい凹凸。</summary>
    [SerializeField] private float terrainDetailNormalTilingMeters = 3.5f;

    // 崖(急傾斜)への岩テクスチャブレンド設定。シェーダー側の実装は
    // Assets/Shaders/TerrainCliffRock.hlsl(トリプラナーマッピング+法線マップ)。
    // 旧「Terrain Slope Color Settings」(terrainSlopeThreshold 等によるライティング後の
    // 単色ベタ塗り)は陰影が消える問題があったため廃止した。フィールド名を変えたのは、
    // シーンに残存する旧シリアライズ値(極端に低い閾値等)を引き継がないための意図的な措置。
    /// <summary>崖用の岩アルベド。未設定なら Resources/Textures/cliff-rock-albedo をロードする。</summary>
    [Header("Terrain Cliff Rock Settings")]
    [SerializeField] private Texture2D cliffAlbedoTexture;
    /// <summary>崖用の岩法線マップ。未設定なら Resources/Textures/cliff-rock-normal をロードする。</summary>
    [SerializeField] private Texture2D cliffNormalTexture;
    /// <summary>岩テクスチャのタイリング周期(m)。トリプラナー投影の実寸感を決める。</summary>
    [SerializeField] private float cliffTilingMeters = 12f;
    /// <summary>岩ブレンド開始の傾斜(0=水平, 1=垂直)。1 - normalWS.y と比較される。高いほど急な崖だけが岩になり、緩斜面に緑が残る。</summary>
    [SerializeField, Range(0f, 1f)] private float cliffSlopeThreshold = 0.45f;
    /// <summary>草→岩の遷移幅。小さいほど境界がくっきりする。</summary>
    [SerializeField, Range(0.01f, 1f)] private float cliffSlopeBlend = 0.18f;
    /// <summary>
    /// 閾値を乱すワールドノイズの周期(m)。境界の帯状バンディング防止用。
    /// 崖の幅(数十m)に近い大きな周期にすると、V字谷の片面と対面が別々のノイズ領域に入り、
    /// 片面まるごと岩・対面まるごと草に分かれてしまう。崖の中で細かい斑になるよう小さめにする。
    /// </summary>
    [SerializeField] private float cliffNoiseScale = 18f;
    /// <summary>閾値ノイズの振幅。0で無効(均一な帯になる)。</summary>
    [SerializeField, Range(0f, 0.3f)] private float cliffNoiseStrength = 0.09f;
    /// <summary>岩法線マップの強度。0で法線無効。</summary>
    [SerializeField, Range(0f, 2f)] private float cliffNormalStrength = 1.0f;
    /// <summary>岩部分のスムースネス。乾いた岩肌を想定して低め。</summary>
    [SerializeField, Range(0f, 1f)] private float cliffSmoothness = 0.08f;
    /// <summary>岩テクスチャが見つからない場合のフォールバック単色。</summary>
    [SerializeField] private Color cliffFallbackColor = new Color(0.42f, 0.36f, 0.30f, 1f);
    /// <summary>低地岩化の開始標高(m)。これより低い土地は傾斜に関係なく徐々に岩へ寄る(海岸・浅瀬の岩肌表現)。</summary>
    [SerializeField] private float cliffLowlandStartHeight = 2f;
    /// <summary>低地岩化が最大になる標高(m)。これ以下(海面下=Y<0を含む)は一律で岩になる。既定は海面(0m)。</summary>
    [SerializeField] private float cliffLowlandFullHeight = 0f;

    /// <summary>Resources からの岩テクスチャフォールバック解決を一度だけ行うためのフラグ。</summary>
    private bool cliffTexturesResolved = false;

    /// <summary>都市の地表種別(GET /api/v1/city の ground_cover)。セッション開始時に FetchCoastline が取得する。</summary>
    private int cityGroundCover = Constants.GROUND_COVER_GRASSLAND;
    /// <summary>Resources からのパレット解決を一度だけ行うためのフラグ。</summary>
    private bool groundCoverPaletteResolved = false;
    /// <summary>パレットのInspector編集を、次のフレームで1回だけ地形へ反映するためのフラグ。</summary>
    private bool groundCoverMaterialsDirty = false;
    private static readonly GroundCoverProfile[] DefaultGroundCoverProfiles = GroundCoverProfile.CreateDefaults();
    private readonly Dictionary<string, Texture2D> groundTextureCache = new Dictionary<string, Texture2D>();

    private static readonly int TerrainMacroVariationScaleId = Shader.PropertyToID("_TerrainMacroVariationScale");

    private static readonly int TerrainColorGradeLowHeightId = Shader.PropertyToID("_TerrainColorGradeLowHeight");
    private static readonly int TerrainColorGradeHighHeightId = Shader.PropertyToID("_TerrainColorGradeHighHeight");
    private static readonly int TerrainDetailNormalScaleId = Shader.PropertyToID("_TerrainDetailNormalScale");

    private static readonly int CliffAlbedoMapId = Shader.PropertyToID("_CliffAlbedoMap");
    private static readonly int CliffNormalMapId = Shader.PropertyToID("_CliffNormalMap");
    private static readonly int CliffUvScaleId = Shader.PropertyToID("_CliffUvScale");
    private static readonly int CliffSlopeThresholdId = Shader.PropertyToID("_CliffSlopeThreshold");
    private static readonly int CliffSlopeBlendId = Shader.PropertyToID("_CliffSlopeBlend");
    private static readonly int CliffNoiseScaleId = Shader.PropertyToID("_CliffNoiseScale");
    private static readonly int CliffNoiseStrengthId = Shader.PropertyToID("_CliffNoiseStrength");
    private static readonly int CliffNormalStrengthId = Shader.PropertyToID("_CliffNormalStrength");
    private static readonly int CliffSmoothnessId = Shader.PropertyToID("_CliffSmoothness");
    private static readonly int CliffTexturesLoadedId = Shader.PropertyToID("_CliffTexturesLoaded");
    private static readonly int CliffFallbackColorId = Shader.PropertyToID("_CliffFallbackColor");
    private static readonly int CliffLowlandStartId = Shader.PropertyToID("_CliffLowlandStart");
    private static readonly int CliffLowlandFullId = Shader.PropertyToID("_CliffLowlandFull");

    // === 設定（APIから遅延読み込み） ===
    private TerrainSettingsData terrainSettings;

    private bool settingsLoaded = false;
    private bool settingsLoading = false;

    // === 遠景ハイトマップキャッシュ（Terrainオブジェクトではなく生データ） ===
    private float[,] farTerrainHeights;

    // タイル管理
    private Dictionary<Vector2Int, Terrain> terrainTiles = new Dictionary<Vector2Int, Terrain>();


    // ================================================================
    // ハイトマップの部分SetHeightsストリーミング基盤
    // ================================================================

    /// <summary>隣接タイルが共有する描画格子。細分化の描画LODはUnity Terrainに委ねる。</summary>
    private const int TILE_RESOLUTION = 513;

    /// <summary>
    /// ストリーミング適用1バンドの行数。513 = 9バンド × 57行 と割り切れる値にし、
    /// 部分SetHeights 1回あたりのGPUアップロード・コライダー更新をフル解像度の約1/9に抑える。
    /// 1バンドがフレーム予算(数ms)に収まることで、適用を複数フレームへ滑らかに分散できる。
    /// </summary>
    private const int STREAM_BAND_ROWS = 57;

    /// <summary>
    /// ストリーミング順序の視野優先判定に使う水平半角(度)。
    /// カメラ前方この角度以内にあるセルを視野外のセルより常に先に処理する。
    /// 視錐台は「生成するか否か」のフィルタではなく「処理順」の重みとしてだけ使う
    /// (視野外セルも後回しで必ず処理されるため、カメラが振り向いても未生成のままにならない)。
    /// </summary>
    private const float STREAM_VIEW_HALF_ANGLE_DEG = 75f;



    /// <summary>
    /// 1セル分のハイトマップ適用リクエスト。フェッチ側のコルーチンが発行し、
    /// <see cref="HeightStreamWorker"/> が視野優先の順序で直列に消化する。
    /// </summary>
    private class HeightStreamRequest
    {
        /// <summary>適用するハイトマップ(TILE_RESOLUTION四方、正規化0..1)。帯の適用を終えたらプールへ返して参照を外す。</summary>
        public float[,] heights;
        /// <summary>LRU から取り出した非表示のタイル。非 null なら帯の適用をせず、このタイルを表示に戻す。</summary>
        public Terrain restoreTile;
        /// <summary>発行元のタイルジョブ。取り消されたら帯の途中でも破棄する。テストの直接投入では null で、取り消さない。</summary>
        public TileJob job;
        /// <summary>公開と同じステップで dynamicCellVersions に記録する地形版(X-Cell-Version)。</summary>
        public int version;
        /// <summary>処理が終了したか(成功・破棄を問わない)。成功時はタイルの公開と同じステップで立つ。</summary>
        public bool done;
        /// <summary>タイルへ適用されたか。破棄・中断時は false。</summary>
        public bool succeeded;
        /// <summary>OnTerrainTileReady の通知を終えたか。通知しないと決まった場合も真にする。</summary>
        public bool notified;
    }

    /// <summary>セルごとの未開始リクエスト。同一セルに新しいリクエストが来たら未開始の旧リクエストは置き換える(最新が勝つ)。</summary>
    private readonly Dictionary<Vector2Int, HeightStreamRequest> pendingStreamRequests = new Dictionary<Vector2Int, HeightStreamRequest>();

    /// <summary>
    /// 1セル分のタイルの取得から公開までの作業。寿命はロードバッチではなく「セルが常駐集合にいる間」で、
    /// バッチが差し替わっても後続のバッチが同じジョブの完了を待つ。途中まで進んだ受信・帯の適用を捨てないため。
    /// 取消しは UnloadCell・LoadLodCells(常駐外になったセル)・ClearAllCells(epoch)の3か所で行う。
    /// </summary>
    private sealed class TileJob
    {
        public Vector2Int cell;
        /// <summary>起動時の版一覧による必要版。これ以上を求めるバッチはこのジョブを再利用する。</summary>
        public int requiredVersion;
        public int epoch;
        /// <summary>強制再取得。LRU の同じ版のタイルを使わずに取り直す。</summary>
        public bool force;
        public bool canceled, done;
        /// <summary>取得スロットを割り当て済みか。解放はジョブの finally だけが行う。</summary>
        public bool slotGranted;
    }

    /// <summary>セルごとの実行中タイルジョブ。完了・取消しで外す。</summary>
    private readonly Dictionary<Vector2Int, TileJob> tileJobs = new Dictionary<Vector2Int, TileJob>();

    /// <summary>取得から公開までを進めているジョブ数。ClearAllCells では戻さず、各ジョブの finally だけが減らす。</summary>
    private int activeTileDownloads;

    /// <summary>同時に進めるタイルジョブの上限。HTTP 同時数と、受信済みで公開待ちの高さ配列のメモリを抑える。</summary>
    private const int MAX_PARALLEL_TILE_DOWNLOADS = 4;

    /// <summary>
    /// 取得スロットの空きを待つジョブ。空いたスロットは <see cref="GrantTileSlots"/> が視野優先の順で1件ずつ割り当てる。
    /// 各ジョブは自分の割り当てだけを待つので、スロットが1つ空くたびに待機中の全ジョブが起きることはない。
    /// </summary>
    private readonly List<TileJob> tileSlotWaiters = new List<TileJob>();

    /// <summary>
    /// 公開済みで OnTerrainTileReady の通知を待つタイル。通知は <see cref="TileNotifyWorker"/> が別の操作として出すので、
    /// 通知前の予算待ちの間も <see cref="HeightStreamWorker"/> は次のタイルへ進める。
    /// </summary>
    private readonly Queue<TileNotification> pendingTileNotifications = new Queue<TileNotification>();
    private Coroutine notifyWorkerCoroutine;

    private struct TileNotification
    {
        public Vector2Int cell;
        public Terrain tile;
        public int epoch;
        public HeightStreamRequest request;
    }

    /// <summary>
    /// 分割できない重い Unity API 呼び出し(SyncHeightmap・タイル生成)と購読者への通知を、
    /// 共通予算がほぼ未使用の時点で始めるための閾値(予算に対する使用済みの割合)と待ちの上限。
    /// 上限フレームに達したら予算に関係なく進め、飢餓を防ぐ。
    /// </summary>
    private const float FRESH_BUDGET_RATIO = 0.25f;
    private const int FRESH_BUDGET_MAX_WAIT_FRAMES = 8;

    private static bool HasFreshBudget() => RenderWorkScheduler.UsedMs <= PerformanceConfig.FrameBudgetMs * FRESH_BUDGET_RATIO;

    private bool IsJobCanceled(TileJob job) => job.canceled || job.epoch != clearEpoch;

    private bool IsRequestCanceled(HeightStreamRequest request, int epoch) =>
        clearEpoch != epoch || (request.job != null && IsJobCanceled(request.job));

    /// <summary>未開始・破棄するリクエストを完了扱いにする。待っているジョブは失敗として抜ける。</summary>
    private void DiscardRequest(HeightStreamRequest request)
    {
        ReleaseRequestResources(request);
        request.succeeded = false;
        request.done = true;
        request.notified = true;
    }

    /// <summary>
    /// リクエストが持つ高さ配列をプールへ返し、表示に戻さなかった復元タイルを LRU へ戻す。
    /// 全消去をまたいだ(epoch が違う)復元タイルは旧セッションの地形なので破棄する。
    /// </summary>
    private void ReleaseRequestResources(HeightStreamRequest request)
    {
        ReturnHeightBuffer(request.heights);
        request.heights = null;
        Terrain tile = request.restoreTile;
        request.restoreTile = null;
        if (tile == null) return;
        if (request.job != null && request.job.epoch == clearEpoch) ParkTile(request.job.cell, tile, request.version);
        else DestroyTerrainTileObject(tile.gameObject);
    }

    // === 受信高さ配列のプール ===
    /// <summary>
    /// タイル1枚分(TILE_RESOLUTION²、約1MB)の高さ配列の再利用プール。LOH 確保を移動中に繰り返さないため。
    /// 同時に使うのは取得スロット数+ワーカーの1枚程度なので、上限はそれより少し多くする。
    /// 出し入れはメインスレッドだけで行い、別スレッドが書いている配列は Task の完了後にだけ返す。
    /// </summary>
    private readonly Stack<float[,]> heightBufferPool = new Stack<float[,]>();
    private const int HEIGHT_BUFFER_POOL_MAX = 6;

    private float[,] RentHeightBuffer() =>
        heightBufferPool.Count > 0 ? heightBufferPool.Pop() : new float[TILE_RESOLUTION, TILE_RESOLUTION];

    private void ReturnHeightBuffer(float[,] buffer)
    {
        if (buffer == null || buffer.GetLength(0) != TILE_RESOLUTION || buffer.GetLength(1) != TILE_RESOLUTION) return;
        if (heightBufferPool.Count >= HEIGHT_BUFFER_POOL_MAX || heightBufferPool.Contains(buffer)) return;
        heightBufferPool.Push(buffer);
    }

    /// <summary>ストリーミングを直列消化する常駐ワーカー。最初のリクエスト発行時に起動する。</summary>
    private Coroutine streamWorkerCoroutine;

    /// <summary>バンドコピー用の再利用バッファ([STREAM_BAND_ROWS, TILE_RESOLUTION])。ワーカーが直列なので1枚でよい。</summary>
    private float[,] streamBandScratch;

    /// <summary>
    /// ClearAllCells のたびにインクリメントされる世代カウンタ。
    /// ストリーミング途中で全クリアが走った場合に、破棄済みTerrainDataへの書き込みや
    /// クリア後の孤児タイル登録を防ぐため、ワーカーが適用開始時の値と比較して中断判定する。
    /// </summary>
    private int clearEpoch = 0;

    // 超遠景: Unity Terrain ではなく、海岸線ポリゴンから生成した陸地シルエットメッシュ。
    // 海(陸地外)はメッシュを作らず、既存の海面板がそのまま海を担う。
    private GameObject ultraFarLandObject;
    private Mesh ultraFarLandMesh;
    private Material ultraFarLandMat;

    // === ロード済みセルマスク(UltraFar沈み込み用) ===
    private static readonly int LoadedCellMaskId = Shader.PropertyToID("_LoadedCellMask");
    private static readonly int LoadedCellMaskParamsId = Shader.PropertyToID("_LoadedCellMaskParams");
    private static readonly int LoadedCellMaskTexelId = Shader.PropertyToID("_LoadedCellMaskTexel");
    private static readonly int FarCityRectId = Shader.PropertyToID("_FarCityRect");
    private static readonly int FarSinkAmountId = Shader.PropertyToID("_FarSinkAmount");

    /// <summary>
    /// per-cellタイルがロード済み(=可視)のセルを示すR8マスクテクスチャ(1セル=1テクセル、バックエンドセル座標)。
    /// UltraFar 陸地メッシュのシェーダーが頂点ごとにこれを参照し、タイルのあるセルだけ地中に沈める。
    /// terrainTiles が変わったとき(loadedCellMaskDirty)だけ作り直し、内容が同じならGPUアップロードもしない。
    /// </summary>
    private Texture2D loadedCellMaskTex;
    private byte[] loadedCellMaskBuf;
    private byte[] loadedCellMaskPrevBuf;
    /// <summary>terrainTiles を書き換えたら必ず立てる。立て忘れると表示中タイルとマスクがずれ、遠景が突き抜ける。</summary>
    private bool loadedCellMaskDirty = true;

    // === 常駐外タイルの LRU ===
    /// <summary>
    /// 常駐外に出たタイル。非表示(非アクティブ)のまま版と一緒に保持し、同じ版以上を求めて再入場したら
    /// 取得・帯の適用・SyncHeightmap・コライダー生成をせずに表示へ戻す。terrainTiles には含めないので、
    /// 接地・GPU高さ窓・UltraFar のマスクは保持中のタイルを参照しない。
    /// </summary>
    private sealed class CachedTile
    {
        public Terrain terrain;
        public int version;
        public LinkedListNode<Vector2Int> node;
    }

    private readonly Dictionary<Vector2Int, CachedTile> tileCache = new Dictionary<Vector2Int, CachedTile>();
    /// <summary>tileCache の追加順。先頭が最古で、容量超過時はここから破棄する。</summary>
    private readonly LinkedList<Vector2Int> tileCacheOrder = new LinkedList<Vector2Int>();
    /// <summary>1枚あたり推定2〜4MB(高さ・GPU高さ・PhysX heightfield)。常駐81枚に対し約3割の余裕で回転の往復を吸収する。</summary>
    private const int TILE_CACHE_CAPACITY = 24;

    /// <summary>
    /// 地形タイルのGameObjectを、TerrainDataアセットもろとも破棄する。
    /// TerrainData は GameObject の破棄では解放されない独立アセットのため、
    /// 明示的に破棄しないとタイル1枚あたり約1MB(+GPUコピー)がシーン終了までリークする。
    /// タイルの破棄は必ずこのヘルパーを通すこと。
    /// </summary>
    private static void DestroyTerrainTileObject(GameObject go)
    {
        if (go == null) return;
        Terrain t = go.GetComponent<Terrain>();
        TerrainData td = t != null ? t.terrainData : null;
        Destroy(go);
        if (td != null) Destroy(td);
    }

    /// <summary>
    /// 常駐外になったタイルを非表示にして LRU の末尾へ入れる。同じセルの古い保持分と、容量を超えた最古の分は破棄する。
    /// 呼び出し側は terrainTiles からの除外と隣接タイルの再接続を自分で行うこと。
    /// </summary>
    private void ParkTile(Vector2Int cell, Terrain tile, int version)
    {
        if (tile == null) return;
        tile.SetNeighbors(null, null, null, null);
        tile.gameObject.SetActive(false);
        DropCachedTile(cell, tile.gameObject);
        tileCache[cell] = new CachedTile { terrain = tile, version = version, node = tileCacheOrder.AddLast(cell) };
        while (tileCache.Count > TILE_CACHE_CAPACITY)
        {
            DropCachedTile(tileCacheOrder.First.Value, null);
            RenderWorkScheduler.CountEvent("terrain.tileCacheEvicted");
        }
    }

    /// <summary>必要版以上の保持タイルがあれば LRU から外して返す。古い版・破棄済みの保持分はその場で破棄する。</summary>
    private bool TryTakeCachedTile(Vector2Int cell, int requiredVersion, out CachedTile cached)
    {
        cached = null;
        if (!tileCache.TryGetValue(cell, out CachedTile entry)) return false;
        if (entry.terrain == null || entry.version < requiredVersion)
        {
            DropCachedTile(cell, null);
            return false;
        }
        tileCache.Remove(cell);
        tileCacheOrder.Remove(entry.node);
        cached = entry;
        return true;
    }

    /// <summary>セルの保持タイルを LRU から外して破棄する。keep と同じ GameObject は破棄しない(二重破棄の防止)。</summary>
    private void DropCachedTile(Vector2Int cell, GameObject keep)
    {
        if (!tileCache.TryGetValue(cell, out CachedTile entry)) return;
        tileCache.Remove(cell);
        tileCacheOrder.Remove(entry.node);
        if (entry.terrain != null && entry.terrain.gameObject != keep) DestroyTerrainTileObject(entry.terrain.gameObject);
    }

    // === Unityコールバック ===
    private void Awake()
    {
        terrainSize.x = CellSize;
        terrainSize.z = CellSize;

        // Utils.GetGroundHeight が FindAnyObjectByType を毎回実行するのを防ぐため、
        // インスタンスを静的キャッシュに登録する。
        // これにより数千回/フレームの GetGroundHeight 呼び出しが O(1) になる。
        Utils.RegisterTerrainManager(this);
        GroundCoverPalette.Changed += MarkGroundCoverMaterialsDirty;
        if (GetComponent<CityBoundaryOverlay>() == null) gameObject.AddComponent<CityBoundaryOverlay>();
    }

    private void OnDestroy()
    {
        GroundCoverPalette.Changed -= MarkGroundCoverMaterialsDirty;
        // TerrainManager が破棄された際にキャッシュ参照をクリアし、
        // 以降の GetGroundHeight でフォールバックパスを使用するようにする。
        if (Utils.CachedTerrainManager == this)
        {
            Utils.RegisterTerrainManager(null);
        }
    }

    private void Update()
    {
        UpdateCameraClipDistance();
        UpdateLoadedCellMask();

        if (groundCoverMaterialsDirty)
        {
            groundCoverMaterialsDirty = false;
            RefreshGroundCoverMaterials();
        }
    }

    private void Start()
    {
        Shader.SetGlobalFloat("_ShowContour", 0f);
    }

    private void UpdateCameraClipDistance()
    {
        Camera camera = Camera.main;
        if (terrainSettings == null || camera == null) return;

        // 常駐セルの範囲外も全景メッシュで描くため、最遠のマップ隅までクリップしない。
        // 視点移動・高度変更後も地形の上下端を含め、シーンで指定された距離は縮めない。
        Vector3 position = camera.transform.position;
        float dx = Mathf.Abs(position.x) + terrainSettings.map_width * 0.5f;
        float dz = Mathf.Abs(position.z) + terrainSettings.map_height * 0.5f;
        float dy = Mathf.Max(Mathf.Abs(position.y + SeaFloorDepthSpan),
            Mathf.Abs(position.y - (terrainSize.y - SeaFloorDepthSpan + ultraFarHeightOffset)));
        if (backgroundHeights != null)
        {
            dy = Mathf.Max(dy, Mathf.Max(Mathf.Abs(position.y-backgroundMinHeight), Mathf.Abs(position.y-backgroundMaxHeight)));
            Rect bounds = BackgroundBounds;
            dx = Mathf.Max(dx, Mathf.Max(Mathf.Abs(position.x-bounds.xMin), Mathf.Abs(position.x-bounds.xMax)));
            dz = Mathf.Max(dz, Mathf.Max(Mathf.Abs(position.z-bounds.yMin), Mathf.Abs(position.z-bounds.yMax)));
        }
        float requiredDistance = new Vector3(dx, dy, dz).magnitude + CellSize;
        if (camera.farClipPlane < requiredDistance) camera.farClipPlane = requiredDistance;
    }

    private void OnValidate()
    {
        terrainMacroVariationScale = Mathf.Max(1f, terrainMacroVariationScale);
        cliffTilingMeters = Mathf.Max(1f, cliffTilingMeters);
        cliffNoiseScale = Mathf.Max(1f, cliffNoiseScale);

        MarkGroundCoverMaterialsDirty();
    }

    /// <summary>
    /// Play中に地表の見た目の設定(Inspector・パレット)が変わったとき、次の Update で1回だけ反映させる。
    /// 値の変更通知はスライダーのドラッグ中に何度も来るため、その場でタイルを作り直さない。
    /// </summary>
    private void MarkGroundCoverMaterialsDirty()
    {
        if (Application.isPlaying && terrainTiles != null) groundCoverMaterialsDirty = true;
    }

    // ================================================================
    // 設定の遅延読み込み
    // ================================================================

    private IEnumerator EnsureSettingsLoaded()
    {
        if (settingsLoaded) yield break;

        if (settingsLoading)
        {
            while (settingsLoading) yield return null;
            yield break;
        }

        settingsLoading = true;
        bool success = false;
        string error = null;

        yield return ApiClient.Get<TerrainSettingsData>(
            "/terrain/settings",
            settings =>
            {
                terrainSettings = settings;
                success = true;
            },
            err => { error = err; }
        );

        settingsLoading = false;

        if (success && terrainSettings != null)
        {
            settingsLoaded = true;
            CellSize = terrainSettings.cell_size;
            terrainSize.x = CellSize;
            terrainSize.z = CellSize;
            Debug.Log($"TerrainManager: Settings loaded. Map={terrainSettings.map_width}x{terrainSettings.map_height}m, " +
                      $"cell={terrainSettings.cell_size}m, far={terrainSettings.far_map_spacing}m, " +
                      $"near={terrainSettings.near_map_spacing}m, dynamic={terrainSettings.dynamic_map_spacing}m");
        }
        else
        {
            Debug.LogError($"TerrainManager: Failed to load settings: {error}");
        }
    }

    // ================================================================
    // float16 LE バイナリ → float[,] 変換
    // ================================================================

    /// <summary>
    /// バックエンドから受信した float16 LE バイナリをハイトマップ float[,] に変換する。
    /// バックグラウンドスレッドから安全に呼べるよう static とし、Unity のインスタンス状態には触れない。
    ///
    /// バックエンドの float16 はワールド標高（メートル）で、海面を標高0とし、海岸線の外側は
    /// 負の標高（海底の水深）を持つ。Terrain のハイトマップは [0,1] でローカル Y=0〜verticalSpan に
    /// 対応するため、正規化時に <paramref name="belowSeaSpan"/> のオフセットを加えて
    /// <c>(標高 + belowSeaSpan) / verticalSpan</c> に写像する。これにより海面(標高0)は
    /// <c>belowSeaSpan/verticalSpan</c> の高さに、海底(負の標高)は 0〜その高さの範囲に収まる。
    /// タイルのベースYを <c>-belowSeaSpan</c> 下げることで worldY = 実標高 が保たれる
    /// (詳細は <see cref="SeaFloorDepthSpan"/>)。
    /// </summary>
    /// <param name="verticalSpan">ローカルY 0〜1 が対応するワールド高さスパン(m)(= terrainSize.y)</param>
    /// <param name="belowSeaSpan">海面下(海底)に割り当てる縦方向スパン(m)(= <see cref="SeaFloorDepthSpan"/>)</param>
    private static float[,] ConvertFloat16BinaryToHeights(byte[] rawBytes, int rows, int cols, float verticalSpan, float belowSeaSpan)
    {
        int count = rows * cols;
        int expectedBytes = count * 2;
        if (rawBytes.Length < expectedBytes)
        {
            Debug.LogError($"TerrainManager: Binary size mismatch. Expected {expectedBytes}, got {rawBytes.Length}");
            return new float[rows, cols];
        }

        float[,] heights = new float[rows, cols];
        float safeSpan = verticalSpan > 1e-3f ? verticalSpan : 1e-3f;
        float invSpan = 1f / safeSpan;
        for (int i = 0; i < count; i++)
        {
            float elevationMeters = HalfToFloat(rawBytes, i * 2);
            // 海面下オフセットを加えてから正規化する。負の標高(海底)は belowSeaSpan 以内なら
            // 0〜(belowSeaSpan/verticalSpan) に収まり、それより深い海底は 0 にクランプされる(最深部)。
            float normalized = (elevationMeters + belowSeaSpan) * invSpan;
            if (normalized < 0f) normalized = 0f;
            else if (normalized > 1f) normalized = 1f;
            heights[i / cols, i % cols] = normalized;
        }
        return heights;
    }

    /// <summary>
    /// IEEE 754 半精度浮動小数点数 (float16, little-endian) を float に変換する。
    /// </summary>
    private static float HalfToFloat(byte[] bytes, int offset)
    {
        ushort bits = (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

        int sign = (bits >> 15) & 0x1;
        int exponent = (bits >> 10) & 0x1F;
        int mantissa = bits & 0x3FF;

        float result;
        if (exponent == 0)
        {
            if (mantissa == 0)
            {
                // ±zero
                result = 0f;
            }
            else
            {
                // 非正規化数: (-1)^sign * 2^(−14) * (0.mantissa)
                result = Mathf.Pow(2f, -14f) * (mantissa / 1024f);
            }
        }
        else if (exponent == 31)
        {
            if (mantissa == 0)
            {
                // ±infinity → クランプ
                result = sign == 0 ? float.MaxValue : float.MinValue;
            }
            else
            {
                // NaN → 0
                result = 0f;
            }
        }
        else
        {
            // 正規化数: (-1)^sign * 2^(exponent−15) * (1 + mantissa/1024)
            result = Mathf.Pow(2f, exponent - 15) * (1f + mantissa / 1024f);
        }

        return sign == 0 ? result : -result;
    }

    // ================================================================
    // 遠景ハイトマップデータのロード（Terrainオブジェクトは生成しない）
    // ================================================================

    private IEnumerator LoadFarTerrainData()
    {
        if (terrainSettings == null) yield break;
        if (farTerrainHeights != null) yield break;
        int epoch = clearEpoch;

        int farRows = terrainSettings.map_height / terrainSettings.far_map_spacing + 1;
        int farCols = terrainSettings.map_width / terrainSettings.far_map_spacing + 1;

        string url = $"{ApiClient.BASE_URL}/terrain/far";
        byte[] rawBytes = null;

        var swFar = System.Diagnostics.Stopwatch.StartNew();
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.timeout = 30;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"TerrainManager: Far terrain data fetch failed: {req.error}");
                yield break;
            }

            rawBytes = req.downloadHandler.data;
        }
        if (epoch != clearEpoch) yield break;
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  network/far: {swFar.ElapsedMilliseconds}ms ({rawBytes?.Length ?? 0} bytes)");

        int targetResolution = GetValidTerrainResolution(Mathf.Max(farRows, farCols));
        var swProc = System.Diagnostics.Stopwatch.StartNew();
        Task<float[,]> task = ProcessHeightmapAsync(rawBytes, farRows, farCols, targetResolution);
        yield return Utils.WaitTask(task);
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  ProcessHeightmapAsync/far: {swProc.ElapsedMilliseconds}ms");
        if (epoch != clearEpoch || task.IsFaulted || task.IsCanceled || task.Result == null) yield break;

        farTerrainHeights = task.Result;

        Debug.Log($"TerrainManager: Far terrain data cached. Grid={farRows}x{farCols}, " +
                  $"resolution={targetResolution}");
    }

    // ================================================================
    // 超近景（Dynamic）のロード
    // ================================================================

    /// <summary>
    /// セルのタイルジョブを返す。必要版を満たす実行中のジョブがあり force でなければそれを再利用し、
    /// そうでなければ旧ジョブを取り消して新しく起動する。
    /// </summary>
    private TileJob EnsureTileJob(Vector2Int cell, int requiredVersion, bool force)
    {
        if (tileJobs.TryGetValue(cell, out TileJob existing) && !existing.done && !IsJobCanceled(existing))
        {
            if (!force && existing.requiredVersion >= requiredVersion)
            {
                RenderWorkScheduler.CountEvent("terrain.tileJobReused");
                return existing;
            }
            CancelTileJob(cell);
        }
        var job = new TileJob { cell = cell, requiredVersion = requiredVersion, epoch = clearEpoch, force = force };
        tileJobs[cell] = job;
        if (RenderWorkScheduler.Start(this, TileJobRoutine(job), "TerrainManager") == null)
        {
            job.done = true;
            tileJobs.Remove(cell);
        }
        return job;
    }

    /// <summary>セルの実行中ジョブを取り消し、そのジョブの未開始リクエストも破棄する。</summary>
    private void CancelTileJob(Vector2Int cell)
    {
        if (!tileJobs.TryGetValue(cell, out TileJob job)) return;
        job.canceled = true;
        tileJobs.Remove(cell);
        if (pendingStreamRequests.TryGetValue(cell, out HeightStreamRequest request) && request.job == job)
        {
            DiscardRequest(request);
            pendingStreamRequests.Remove(cell);
        }
    }

    private static bool AllTileJobsDone(List<TileJob> jobs)
    {
        for (int i = 0; i < jobs.Count; i++) if (!jobs[i].done) return false;
        return true;
    }

    /// <summary>空いている取得スロットを、待機中のジョブへ視野優先の順(<see cref="ScoreStreamCell"/>)で割り当てる。取り消されたジョブは外す。</summary>
    private void GrantTileSlots()
    {
        for (int i = tileSlotWaiters.Count - 1; i >= 0; i--)
            if (tileSlotWaiters[i].done || IsJobCanceled(tileSlotWaiters[i])) tileSlotWaiters.RemoveAt(i);
        if (tileSlotWaiters.Count == 0 || activeTileDownloads >= MAX_PARALLEL_TILE_DOWNLOADS) return;
        GetStreamViewpoint(out Vector3 camPos, out Vector3 fwd);
        while (activeTileDownloads < MAX_PARALLEL_TILE_DOWNLOADS && tileSlotWaiters.Count > 0)
        {
            int best = 0;
            float bestScore = float.MaxValue;
            for (int i = 0; i < tileSlotWaiters.Count; i++)
            {
                float score = ScoreStreamCell(tileSlotWaiters[i].cell, camPos, fwd);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            TileJob granted = tileSlotWaiters[best];
            tileSlotWaiters.RemoveAt(best);
            granted.slotGranted = true;
            activeTileDownloads++;
        }
    }

    /// <summary>
    /// 1セルのタイルを取得・正規化し、ストリーミング適用と OnTerrainTileReady の通知まで待つ。
    /// LRU に必要版以上のタイルがあれば、取得スロットを使わずに復元だけを依頼する(force では使わない)。
    /// 待機はすべてネイティブ待機(WaitUntil)にし、待っている間は共通予算の Step を消費しない。
    /// </summary>
    private IEnumerator TileJobRoutine(TileJob job)
    {
        Vector2Int cell = job.cell;
        float[,] buffer = null;
        Task normalizeTask = null;
        try
        {
            // 初回の Step より前に差し替えられたジョブが、後継ジョブの使う LRU のタイルを先に取らないようにする。
            if (IsJobCanceled(job)) yield break;
            if (!job.force && TryTakeCachedTile(cell, job.requiredVersion, out CachedTile cached))
            {
                RenderWorkScheduler.CountEvent("terrain.tileCacheHit");
                var restoreRequest = new HeightStreamRequest { job = job, version = cached.version, restoreTile = cached.terrain };
                yield return EnqueueAndWait(cell, restoreRequest);
                // 保持中に破棄されたなどで戻せなかったときだけ、取得へ進む。
                if (restoreRequest.succeeded || IsJobCanceled(job)) yield break;
            }

            tileSlotWaiters.Add(job);
            GrantTileSlots();
            if (!job.slotGranted) yield return new WaitUntil(() => job.slotGranted || IsJobCanceled(job));
            if (IsJobCanceled(job)) yield break;

            Vector2Int backend = UnityCellToBackendCell(cell);
            string url = $"{ApiClient.BASE_URL}/terrain/surface?cell_x={backend.x}&cell_z={backend.y}&resolution={TILE_RESOLUTION}";
            byte[] bytes;
            int version;
            RenderWorkScheduler.CountEvent("terrain.tileDownload");
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = 30;
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                yield return new WaitUntil(() => operation.isDone || IsJobCanceled(job));
                if (!operation.isDone)
                {
                    request.Abort();
                    RenderWorkScheduler.CountEvent("terrain.tileDownloadAborted");
                    yield break;
                }
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"地形セル{cell}の取得失敗: {request.error}");
                    yield break;
                }
                bytes = request.downloadHandler.data;
                version = ParseIntHeader(request, "X-Cell-Version", 0);
            }
            if (IsJobCanceled(job)) yield break;
            float span = terrainSize.y;
            float below = SeaFloorDepthSpan;
            float[,] heights = buffer = RentHeightBuffer();
            // float32 の受信列を多次元配列へ直接写し、その場で正規化する(中間の1次元配列を作らない)。
            normalizeTask = Task.Run(() =>
            {
                if (bytes.Length != TILE_RESOLUTION * TILE_RESOLUTION * sizeof(float)) throw new InvalidOperationException("地形配列のサイズが不正です");
                Buffer.BlockCopy(bytes, 0, heights, 0, bytes.Length);
                for (int z = 0; z < TILE_RESOLUTION; z++)
                    for (int x = 0; x < TILE_RESOLUTION; x++)
                    {
                        float value = heights[z, x];
                        if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException("地形に非有限値があります");
                        heights[z, x] = Mathf.Clamp01((value + below) / span);
                    }
            });
            Task task = normalizeTask;
            yield return new WaitUntil(() => task.IsCompleted || IsJobCanceled(job));
            if (IsJobCanceled(job)) yield break;
            if (task.IsFaulted || task.IsCanceled)
            {
                Debug.LogError($"地形セル{cell}の正規化失敗: {task.Exception?.Flatten()}");
                yield break;
            }
            var streamRequest = new HeightStreamRequest { heights = buffer, job = job, version = version };
            buffer = null;
            yield return EnqueueAndWait(cell, streamRequest);
        }
        finally
        {
            // 正規化の Task がまだ書いている配列はプールへ返さず、GC に任せる。
            if (buffer != null && (normalizeTask == null || normalizeTask.IsCompleted)) ReturnHeightBuffer(buffer);
            job.done = true;
            if (job.slotGranted)
            {
                job.slotGranted = false;
                activeTileDownloads--;
                GrantTileSlots();
            }
            else tileSlotWaiters.Remove(job);
            if (tileJobs.TryGetValue(cell, out TileJob current) && current == job) tileJobs.Remove(cell);
        }
    }

    // ================================================================
    // BaseApiDataManager 実装
    // ================================================================

    protected override void UnloadCell(Vector2Int cellIndex)
    {
        // 取得中のジョブと未開始のストリーミングリクエストを破棄する(範囲外セルへの無駄な適用防止)。
        CancelTileJob(cellIndex);
        if (pendingStreamRequests.TryGetValue(cellIndex, out HeightStreamRequest pendingReq))
        {
            DiscardRequest(pendingReq);
            pendingStreamRequests.Remove(cellIndex);
        }

        if (terrainTiles.TryGetValue(cellIndex, out Terrain t))
        {
            terrainTiles.Remove(cellIndex);
            loadedCellMaskDirty = true;
            // 版が分からないタイルは再入場で使えないので保持しない。
            if (t != null)
            {
                if (dynamicCellVersions.TryGetValue(cellIndex, out int version)) ParkTile(cellIndex, t, version);
                else DestroyTerrainTileObject(t.gameObject);
            }
            dynamicCellVersions.Remove(cellIndex);
            Utils.InvalidateHeightCache();
            HeightmapTextureManager.Instance?.MarkSurfaceChanged(cellIndex);
            UpdateNeighborsForAdjacents(cellIndex);
        }
    }

    public override void ClearAllCells()
    {
        ClearBackground();
        currentLoadBatch?.Cancel();
        if (loadLodCellsCoroutine != null)
        {
            RenderWorkScheduler.Stop(this, loadLodCellsCoroutine);
            loadLodCellsCoroutine = null;
        }
        settingsLoading = false;
        // 進行中・待機中のストリーミングをすべて無効化する。
        // clearEpoch のインクリメントにより、適用途中のワーカーは次のバンド適用前に中断し、
        // 破棄済みTerrainDataへの書き込みやクリア後の孤児タイル登録を行わない。
        clearEpoch++;
        foreach (var job in tileJobs.Values) job.canceled = true;
        tileJobs.Clear();
        foreach (var kvp in pendingStreamRequests) DiscardRequest(kvp.Value);
        pendingStreamRequests.Clear();
        foreach (var notification in pendingTileNotifications) notification.request.notified = true;
        pendingTileNotifications.Clear();
        heightBufferPool.Clear();

        // セルごとのTerrain(TerrainDataアセットも併せて破棄する)
        foreach (var kvp in terrainTiles)
        {
            if (kvp.Value != null) DestroyTerrainTileObject(kvp.Value.gameObject);
        }
        terrainTiles.Clear();
        loadedCellMaskDirty = true;
        Utils.InvalidateHeightCache();
        loadedCells.Clear();
        dynamicCellVersions.Clear();

        foreach (var entry in tileCache.Values)
        {
            if (entry.terrain != null) DestroyTerrainTileObject(entry.terrain.gameObject);
        }
        tileCache.Clear();
        tileCacheOrder.Clear();

        // 超遠景の陸地メッシュ
        if (ultraFarLandObject != null)
        {
            Destroy(ultraFarLandObject);
            ultraFarLandObject = null;
        }
        if (ultraFarLandMesh != null)
        {
            Destroy(ultraFarLandMesh);
            ultraFarLandMesh = null;
        }

        // 遠景データキャッシュをクリア
        farTerrainHeights = null;
        overviewDataRevision = -1;
        overviewRevision = -1;
        cachedCellVersions = null;
        cellVersionsDirty = true;
        cellVersionsGeneration++;

        // 設定キャッシュをクリア（session.reset対応）
        terrainSettings = null;
        settingsLoaded = false;
    }

    // ================================================================
    // LOD対応セルロード
    // ================================================================

    private Coroutine loadLodCellsCoroutine;

    public override void LoadLodCells(LodCellSets cellSets, bool isForce)
    {
        if (loadLodCellsCoroutine != null)
        {
            RenderWorkScheduler.Stop(this, loadLodCellsCoroutine);
            // StopCoroutineで中断されたコルーチン内のsettingsLoading=falseが実行されず
            // 孤児化するのを防ぐため、ここで明示的にリセットする。
            // リセットしないと次回のEnsureSettingsLoadedがwhile(settingsLoading)で
            // 無限ループに陥り、地形・建物・道路が一切ロードされなくなる。
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "TerrainManager: loadLodCellsCoroutine != null");
            settingsLoading = false;
        }

        // CellDataLoader 経由(LoadBatch付きオーバーロード)で呼ばれた場合は currentLoadBatch に
        // 新しいバッチが設定済み。直接呼ばれた場合(バッチ未設定または前回バッチがキャンセル済み)は
        // このロード用のバッチを発行する。
        if (currentLoadBatch == null || currentLoadBatch.IsCanceled)
        {
            currentLoadBatch = new LoadBatch(cellSets, isForce);
        }

        // タイルジョブはバッチの取消しでは止まらないので、常駐集合から外れたセルのジョブはここで取り消す。
        if (tileJobs.Count > 0)
        {
            var resident = new HashSet<Vector2Int>(cellSets.farCells ?? new List<Vector2Int>());
            var jobCells = new List<Vector2Int>(tileJobs.Keys);
            foreach (var cell in jobCells)
                if (!resident.Contains(cell)) CancelTileJob(cell);
        }

        loadLodCellsCoroutine = RenderWorkScheduler.Start(this, LoadLodCellsCoroutine(cellSets, isForce, currentLoadBatch), "TerrainManager");
    }

    private IEnumerator LoadLodCellsCoroutine(LodCellSets cellSets, bool isForce, LoadBatch batch)
    {
        yield return EnsureSettingsLoaded();
        if (terrainSettings == null || batch.IsCanceled) yield break;
        if (isForce || cellVersionsDirty || cachedCellVersions == null || Time.unscaledTime - cellVersionsFetchedAt > CELL_VERSIONS_MAX_AGE)
        {
            int generation = cellVersionsGeneration;
            CellVersionsResponse response = null;
            while (response == null && !batch.IsCanceled)
            {
                yield return ApiClient.Get<CellVersionsResponse>("/terrain/cell-versions", value => response = value,
                    error => Debug.LogWarning($"地形の版情報取得失敗: {error}"));
                if (response == null) yield return new WaitForSecondsRealtime(2f);
            }
            if (batch.IsCanceled) yield break;
            var fetched = new Dictionary<Vector2Int, int>();
            if (response.cells != null)
                foreach (var item in response.cells)
                    foreach (var cell in BackendCellToUnityCells(new Vector2Int(item.cell_x, item.cell_z)))
                        fetched[cell] = item.version;
            cachedCellVersions = fetched;
            cellVersionsFetchedAt = Time.unscaledTime;
            if (generation == cellVersionsGeneration) cellVersionsDirty = false;
            RenderWorkScheduler.CountEvent("terrain.cellVersionsFetched");
        }
        else RenderWorkScheduler.CountEvent("terrain.cellVersionsReused");
        // 以降の yield の間に再取得で差し替わっても、このバッチは取得時点の一覧を読み取り専用で使う。
        Dictionary<Vector2Int, int> versions = cachedCellVersions;
        long revision = 0;
        foreach (int version in versions.Values) revision += version;
        yield return EnsureOverview(revision, batch);
        yield return EnsureBackground(batch);
        if (batch.IsCanceled) yield break;
        var desired = new HashSet<Vector2Int>(cellSets.farCells);
        desired.RemoveWhere(cell => !IsCityCell(cell));
        var remove = new List<Vector2Int>();
        foreach (var cell in loadedCells) if (!desired.Contains(cell)) remove.Add(cell);
        foreach (var cell in remove)
        {
            UnloadCell(cell);
            loadedCells.Remove(cell);
            yield return RenderWorkScheduler.Continue;
            if (batch.IsCanceled) yield break;
        }
        var pending = new List<Vector2Int>(desired);
        Vector3 camera = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
        pending.Sort((a, b) => (new Vector2(a.x * CellSize - camera.x, a.y * CellSize - camera.z)).sqrMagnitude.CompareTo(
            (new Vector2(b.x * CellSize - camera.x, b.y * CellSize - camera.z)).sqrMagnitude));
        var jobs = new List<TileJob>();
        bool firstPass = true;
        bool incomplete;
        do
        {
            incomplete = false;
            jobs.Clear();
            foreach (var cell in pending)
            {
                int version = versions.TryGetValue(cell, out int value) ? value : 0;
                bool force = isForce && firstPass;
                if (!force && HasSurfaceTile(cell) && dynamicCellVersions.TryGetValue(cell, out int current) && current >= version)
                {
                    // 公開済みでも通知前のジョブは待つ。OnAllTerrainLodLoaded を全タイルの通知より後にするため。
                    if (tileJobs.TryGetValue(cell, out TileJob running) && !running.done && !IsJobCanceled(running)) jobs.Add(running);
                    continue;
                }
                jobs.Add(EnsureTileJob(cell, version, force));
            }
            // ジョブ自体はバッチの取消しで止めない。後続のバッチが同じジョブを再利用する。
            if (jobs.Count > 0) yield return new WaitUntil(() => batch.IsCanceled || AllTileJobsDone(jobs));
            if (batch.IsCanceled) yield break;
            foreach (var cell in pending)
            {
                int required = versions.TryGetValue(cell, out int value) ? value : 0;
                if (!HasSurfaceTile(cell) || !dynamicCellVersions.TryGetValue(cell, out int current) || current < required)
                    incomplete = true;
            }
            firstPass = false;
            if (incomplete) yield return new WaitForSecondsRealtime(2f);
            if (batch.IsCanceled) yield break;
        } while (incomplete);
        foreach (var cell in updatedTerrainCellsThisLoad) UpdateNeighbors(cell);
        var updated = new List<Vector2Int>(updatedTerrainCellsThisLoad);
        updatedTerrainCellsThisLoad.Clear();
        OnAllTerrainLodLoaded?.Invoke(updated);
    }

    public bool HasSurfaceTile(Vector2Int cell) => terrainTiles.TryGetValue(cell, out var tile) && tile != null;

    public string GetSurfaceStamp(Rect area)
    {
        var key = new System.Text.StringBuilder();
        key.Append(clearEpoch);
        WorldRectToCellRange(area.xMin, area.yMin, area.xMax, area.yMax, out Vector2Int minCell, out Vector2Int maxCell);
        for (int z = minCell.y; z <= maxCell.y; z++) for (int x = minCell.x; x <= maxCell.x; x++)
        {
            var cell = new Vector2Int(x, z);
            key.Append(':').Append(x).Append(',').Append(z).Append('=').Append(dynamicCellVersions.TryGetValue(cell, out int version) ? version : -1);
        }
        return key.ToString();
    }

    /// <summary>ClearAllCells の世代。署名や版を記録した側が、全クリアをまたいだかの判定に使う。</summary>
    public int TerrainEpoch => clearEpoch;

    /// <summary>
    /// 表示中タイルの地形版(/terrain/cell-versions の版。一覧に無いセルは 0)を返す。
    /// タイルが無い・常駐外・非アクティブのときは -1。メインスレッド専用。
    /// </summary>
    public int GetAttachedTerrainVersion(Vector2Int cell)
    {
        if (!terrainTiles.TryGetValue(cell, out Terrain tile) || tile == null || !tile.gameObject.activeSelf) return -1;
        return dynamicCellVersions.TryGetValue(cell, out int version) ? version : -1;
    }

    /// <summary>
    /// 地形の版一覧キャッシュを無効化する。データ再取得(CellDataLoader.ReloadAllData)の要求時に同期で呼ぶ。
    /// </summary>
    public void InvalidateCellVersions()
    {
        cellVersionsDirty = true;
        cellVersionsGeneration++;
    }

    /// <summary>
    /// area に重なる全セルの地形署名。同じ版・同じ全景・同じ世代なら、再入場後も同じ値を返す。
    /// GetSurfaceStamp の文字列を作らない版で、値が一致すれば高さも同じとみなしてよい。
    /// </summary>
    public long GetTerrainSignature(Rect area)
    {
        WorldRectToCellRange(area.xMin, area.yMin, area.xMax, area.yMax, out Vector2Int minCell, out Vector2Int maxCell);
        ulong sum = 0;
        for (int z = minCell.y; z <= maxCell.y; z++)
            for (int x = minCell.x; x <= maxCell.x; x++)
                sum += CellSignatureTerm(new Vector2Int(x, z));
        return FinishTerrainSignature(sum);
    }

    /// <summary>任意のセル集合の地形署名(順序に依存しない)。</summary>
    public long GetTerrainSignature(IReadOnlyList<Vector2Int> cells)
    {
        ulong sum = 0;
        if (cells != null) for (int i = 0; i < cells.Count; i++) sum += CellSignatureTerm(cells[i]);
        return FinishTerrainSignature(sum);
    }

    /// <summary>任意のセル集合の地形署名(順序に依存しない)。</summary>
    public long GetTerrainSignature(HashSet<Vector2Int> cells)
    {
        ulong sum = 0;
        if (cells != null) foreach (var cell in cells) sum += CellSignatureTerm(cell);
        return FinishTerrainSignature(sum);
    }

    /// <summary>
    /// TerrainManager が無い(破棄済みを含む)ときは false。呼び出し側は「不明」として扱い、作り直しを省かないこと。
    /// </summary>
    public static bool TryGetTerrainSignature(IReadOnlyList<Vector2Int> cells, out long signature)
    {
        TerrainManager manager = Utils.CachedTerrainManager;
        if (manager == null)
        {
            signature = 0;
            return false;
        }
        signature = manager.GetTerrainSignature(cells);
        return true;
    }

    // タイルの無いセルは全景の高さを使うので、全景データの版も混ぜる。
    private ulong CellSignatureTerm(Vector2Int cell)
    {
        int version = GetAttachedTerrainVersion(cell);
        ulong term = SplitMix64((ulong)(((long)cell.x << 32) | (uint)cell.y) ^ SplitMix64((ulong)(version + 1L)));
        if (version == -1) term = SplitMix64(term ^ (ulong)(farTerrainHeights != null ? overviewDataRevision : -1L));
        return term;
    }

    private long FinishTerrainSignature(ulong sum) => (long)SplitMix64(sum ^ SplitMix64((ulong)clearEpoch));

    private static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    // データ受信と描画完成を分け、中断後は受信済みデータから未完成メッシュを再生成する。
    private long overviewDataRevision = -1;
    private long overviewRevision = -1;

    private IEnumerator EnsureOverview(long revision, LoadBatch batch)
    {
        int epoch = clearEpoch;
        while (overviewRevision != revision && !batch.IsCanceled && epoch == clearEpoch)
        {
            if (farTerrainHeights == null || overviewDataRevision != revision)
            {
                farTerrainHeights = null;
                yield return LoadFarTerrainData();
                if (batch.IsCanceled || epoch != clearEpoch) yield break;
                if (farTerrainHeights != null) overviewDataRevision = revision;
            }
            if (farTerrainHeights != null)
            {
                GameObject previous = ultraFarLandObject;
                Mesh previousMesh = ultraFarLandMesh;
                bool completed = false;
                yield return BuildUltraFarTerrain(batch, () => completed = true);
                if (batch.IsCanceled || epoch != clearEpoch) yield break;
                if (completed)
                {
                    if (previous != null && previous != ultraFarLandObject) Destroy(previous);
                    if (previousMesh != null && previousMesh != ultraFarLandMesh) Destroy(previousMesh);
                    overviewRevision = revision;
                    yield break;
                }
            }
            yield return new WaitForSecondsRealtime(2f);
        }
    }

    private IEnumerator BuildUltraFarTerrain(LoadBatch batch, Action onCompleted)
    {
        if (farTerrainHeights == null || terrainSettings == null || batch.IsCanceled) yield break;
        int epoch = clearEpoch;

        float mapWidth = terrainSettings.map_width;
        float mapHeight = terrainSettings.map_height;
        float verticalSpan = terrainSize.y;
        float belowSeaSpan = SeaFloorDepthSpan;
        float farOriginX = -mapWidth / 2f;
        float farOriginZ = -mapHeight / 2f;

        // バックグラウンドスレッドから触る値はローカルにキャプチャする(Unity API・可変フィールドに触れない)。
        float[,] far = farTerrainHeights;
        int farCols = far.GetLength(1);
        int farRows = far.GetLength(0);

        // far_map(正規化0..1)からワールドY(メートル)をバイリニアサンプリングする。
        // 正規化には海面下オフセット(belowSeaSpan)が入っているため、per-cellタイルの
        // ベースY(-SeaFloorDepthSpan)と同じ分だけ差し引いて実標高のワールドYに戻す。
        Func<float, float, float> sampleHeight = (x, z) =>
        {
            float u = Mathf.Clamp01((x - farOriginX) / mapWidth);
            float v = Mathf.Clamp01((z - farOriginZ) / mapHeight);
            float fx = u * (farCols - 1);
            float fy = v * (farRows - 1);
            // 超遠景メッシュの起伏も遠景ハイトマップからCatmull-Romで滑らかにサンプルする。
            return CatmullRomInterpolateArray(far, fx, fy, farCols, farRows) * verticalSpan - belowSeaSpan;
        };

        float gridStep = ultraFarGridStep;
        float heightOffset = ultraFarHeightOffset;

        Task<UltraFarLandMeshBuilder.MeshData> task = Task.Run(() =>
            UltraFarLandMeshBuilder.Build(mapWidth, mapHeight, gridStep, heightOffset, sampleHeight));
        yield return Utils.WaitTask(task);
        if (batch.IsCanceled || epoch != clearEpoch || task.IsFaulted || task.IsCanceled || task.Result.vertices == null) yield break;

        UltraFarLandMeshBuilder.MeshData md = task.Result;
        if (md.vertices.Count < 3)
        {
            // 三角形が生成されない場合も、以前の陸地を残さず空の結果を版として確定する。
            ultraFarLandObject = null;
            ultraFarLandMesh = null;
            onCompleted();
            yield break;
        }

        // メインスレッドで Mesh を組み立てる。
        Mesh mesh = new Mesh { name = "UltraFarLandMesh" };
        if (md.vertices.Count > 65000)
        {
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        }
        mesh.SetVertices(md.vertices);
        if (md.uvs != null && md.uvs.Count == md.vertices.Count)
        {
            mesh.SetUVs(0, md.uvs);
        }
        mesh.SetTriangles(md.triangles, 0);
        // ビルダーが位置溶接済みのスムース法線を返す。RecalculateNormals は重複頂点を
        // 溶接しないためセル境界でファセット状の陰影になるので使わない。
        if (md.normals != null && md.normals.Count == md.vertices.Count)
        {
            mesh.SetNormals(md.normals);
        }
        else
        {
            mesh.RecalculateNormals();
        }
        mesh.RecalculateBounds();
        ultraFarLandMesh = mesh;

        GameObject go = new GameObject("UltraFar_Land");
        Transform container = GetContainer("Terrains");
        if (container != null) go.transform.SetParent(container);
        go.layer = LayerMask.NameToLayer("Ground");

        MeshFilter mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        MeshRenderer mr = go.AddComponent<MeshRenderer>();

        EnsureUltraFarLandMaterials();
        mr.sharedMaterial = ultraFarLandMat;

        go.AddComponent<VisualTerrainObject>();

        ultraFarLandObject = go;
        onCompleted();

        Debug.Log($"TerrainManager: Ultra-far land mesh built. verts={md.vertices.Count}, " +
                  $"tris={md.triangles.Count / 3}");
    }

    /// <summary>
    /// UltraFar 陸地の地面テクスチャ(per-cellタイルと同じもの)とタイリングを取得する。
    /// customTerrainLayers[0] があればそれを、なければ地表種別のテクスチャを使う。
    /// </summary>
    private Texture2D GetUltraFarDiffuseTexture(GroundCoverProfile ground, out Vector2 tileSize)
    {
        Texture2D diffuseTex = ResolveGroundTexture(ground);
        tileSize = ground.textureTileSize;

        if (customTerrainLayers != null && customTerrainLayers.Length > 0 && customTerrainLayers[0] != null)
        {
            diffuseTex = customTerrainLayers[0].diffuseTexture;
            tileSize = customTerrainLayers[0].tileSize;
        }

        if (tileSize.x <= 0f || tileSize.y <= 0f)
        {
            tileSize = new Vector2(terrainSize.x, terrainSize.z);
        }
        return diffuseTex;
    }

    /// <summary>
    /// UltraFar 陸地メッシュ用のマテリアルを必要に応じて生成する。
    /// シェーダーは Custom/DepthOffsetSimpleLitFar(Offset 4,4)で per-cell 地形・海面より奥に描画する。
    /// 見た目の設定は <see cref="ApplyFarTerrainMaterialSettings"/>、沈み込みマスクは <see cref="UpdateLoadedCellMask"/> が担う。
    /// </summary>
    private void EnsureUltraFarLandMaterials()
    {
        Shader sh = ResolveFarTerrainShader();
        if (ultraFarLandMat == null) ultraFarLandMat = new Material(sh) { name = "UltraFarLand" };
        else ultraFarLandMat.shader = sh;
        ApplyFarTerrainMaterialSettings(ultraFarLandMat);
        // ロード済みセルでの沈み込み量。判定マスクは UpdateLoadedCellMask が供給する。
        ultraFarLandMat.SetFloat(FarSinkAmountId, ultraFarSinkAmount);
    }

    private Shader ResolveFarTerrainShader()
    {
        Shader sh = ultraFarLandShader;
        if (sh == null) sh = Shader.Find("Custom/DepthOffsetSimpleLitFar");
        if (sh == null)
        {
            Debug.LogWarning("TerrainManager: Custom/DepthOffsetSimpleLitFar shader not found; falling back to URP Simple Lit");
            sh = Shader.Find("Universal Render Pipeline/Simple Lit");
        }
        return sh;
    }

    /// <summary>
    /// 遠景メッシュ(UltraFar 陸地・都市外背景)のマテリアルへ、per-cell タイルと同じ見た目の設定を直接書き込む。
    /// per-cell タイルと同じ地面テクスチャを、頂点のワールドXZ座標をUVとして同じタイリングで貼る。
    /// 地表種別の手続き色、岩肌、法線ディテールとPBR照明はTerrainと共通の設定・HLSLを使う。
    /// 手続き色や色被せの値はシェーダーの Properties に宣言していないため、new Material(既存マテリアル) の
    /// 複製では落ちる。遠景マテリアルを増やすときも複製せず、必ずこの関数で設定する。
    /// </summary>
    private void ApplyFarTerrainMaterialSettings(Material material)
    {
        GroundCoverProfile ground = ActiveGroundCover;
        // テクスチャが無い場合は白の既定テクスチャに下地色Aを掛け、タイルの単色代用と揃える。
        Texture2D diffuseTex = GetUltraFarDiffuseTexture(ground, out Vector2 tileSize);
        material.SetTexture("_BaseMap", diffuseTex != null ? diffuseTex : Texture2D.whiteTexture);
        material.SetVector("_BaseMap_ST", new Vector4(1f / tileSize.x, 1f / tileSize.y, 0f, 0f));

        ApplyTerrainMaterialSettings(material);
        TerrainLayer groundLayer = customTerrainLayers != null && customTerrainLayers.Length > 0
            ? customTerrainLayers[0] : null;
        material.SetFloat("_GroundMetallic", groundLayer != null ? groundLayer.metallic : defaultMetallic);
        material.SetFloat("_Smoothness", groundLayer != null ? groundLayer.smoothness : ground.smoothness);
        material.SetColor("_BaseColor", diffuseTex != null ? Color.white : ground.groundColorA);
        material.SetFloat("_FarGridStep", Mathf.Max(1f, ultraFarGridStep));
    }

    /// <summary>
    /// ロード済みセルマスク(UltraFar沈み込み用)を terrainTiles の現状から再構築する。毎フレーム呼ばれるが、
    /// terrainTiles が変わった(loadedCellMaskDirty)ときとテクスチャを作り直したときだけ中身を作る。
    ///
    /// 遠景と通常地形の高さ差による突き抜けを防ぐため、可視タイルのセルをR8マスクにする。
    /// シェーダーは頂点の周囲1グリッド分も調べ、接続面がロード済み範囲内に収まる場合だけ
    /// 地中へ沈める。境界をまたぐ面は元の高さに保ち、未ロード側が海面下へ下がるのを防ぐ。
    /// 都市の外には遠景の面が無いので、都市範囲(_FarCityRect)の外側の近傍は境界上のセルとして判定する。
    ///
    /// テクセル座標はバックエンドセル座標(0-based)。シェーダー側は floor(worldXZ/cellSize)+offset で
    /// 同じ変換を行う(UnityCellToBackendCell と同一の整数オフセット)。
    /// 前回と同一ならGPUアップロードしない。
    /// </summary>
    private void UpdateLoadedCellMask()
    {
        // マスクの利用者はUltraFarマテリアルのみ。設定未取得・マテリアル未生成なら何もしない。
        if (terrainSettings == null || ultraFarLandMat == null) return;

        int w = GetCellsX();
        int h = GetCellsY();
        if (w <= 0 || h <= 0) return;

        // テクスチャ・バッファの生成(マップサイズ変更時は作り直し)
        if (loadedCellMaskTex == null || loadedCellMaskTex.width != w || loadedCellMaskTex.height != h)
        {
            if (loadedCellMaskTex != null) Destroy(loadedCellMaskTex);
            loadedCellMaskTex = new Texture2D(w, h, TextureFormat.R8, false)
            {
                name = "LoadedCellMask",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            loadedCellMaskBuf = new byte[w * h];
            loadedCellMaskPrevBuf = null; // 初回は必ずアップロードさせる

            // マスクと座標変換パラメータをUltraFarマテリアルに結び付ける。
            // params: x = 1/cellSize, y = セルオフセットX, z = セルオフセットY (UnityCellToBackendCellと同じ)
            // texel:  x = 1/セル数X, y = 1/セル数Y
            float cs = GetCellSize();
            int offsetX = Mathf.RoundToInt((GetMapWidth() / 2.0f) / cs);
            int offsetY = Mathf.RoundToInt((GetMapHeight() / 2.0f) / cs);
            Vector4 maskParams = new Vector4(1f / cs, offsetX, offsetY, 0f);
            Vector4 maskTexel = new Vector4(1f / w, 1f / h, 0f, 0f);

            ultraFarLandMat.SetTexture(LoadedCellMaskId, loadedCellMaskTex);
            ultraFarLandMat.SetVector(LoadedCellMaskParamsId, maskParams);
            ultraFarLandMat.SetVector(LoadedCellMaskTexelId, maskTexel);
            ultraFarLandMat.SetVector(FarCityRectId, new Vector4(
                -GetMapWidth() * 0.5f, -GetMapHeight() * 0.5f, GetMapWidth() * 0.5f, GetMapHeight() * 0.5f));
            loadedCellMaskDirty = true;
        }
        if (!loadedCellMaskDirty) return;
        loadedCellMaskDirty = false;

        // 現在ロード済み(可視)のタイルをマスクに書き込む。
        // ティア変更・リフレッシュ中の旧タイルは terrainTiles に登録・可視のまま維持されるため
        // 自然にマスクに含まれ、差し替え完了までUltraFarメッシュの浮き上がりは起きない。
        // LRU で保持中の非表示タイルは terrainTiles に無いので含まれない。
        Array.Clear(loadedCellMaskBuf, 0, loadedCellMaskBuf.Length);
        foreach (var kvp in terrainTiles)
        {
            if (kvp.Value == null) continue;
            Vector2Int backendCell = UnityCellToBackendCell(kvp.Key);
            if (backendCell.x < 0 || backendCell.x >= w || backendCell.y < 0 || backendCell.y >= h) continue;
            loadedCellMaskBuf[backendCell.y * w + backendCell.x] = 255;
        }

        // 変化がなければGPUアップロードをスキップする。
        if (loadedCellMaskPrevBuf != null && ((ReadOnlySpan<byte>)loadedCellMaskBuf).SequenceEqual(loadedCellMaskPrevBuf))
        {
            return;
        }

        loadedCellMaskTex.SetPixelData(loadedCellMaskBuf, 0);
        loadedCellMaskTex.Apply(false, false);

        if (loadedCellMaskPrevBuf == null) loadedCellMaskPrevBuf = new byte[loadedCellMaskBuf.Length];
        Buffer.BlockCopy(loadedCellMaskBuf, 0, loadedCellMaskPrevBuf, 0, loadedCellMaskBuf.Length);
    }

    // ================================================================
    // Terrain タイルへのハイトマップ・ストリーミング適用
    // ================================================================

    /// <summary>
    /// ハイトマップの適用リクエストを発行し、完了(破棄、または公開と OnTerrainTileReady の通知)まで待機する。
    /// 実際の適用は <see cref="HeightStreamWorker"/> が視野優先の順序・フレーム予算制で
    /// 直列に行う。同一セルの未開始リクエストが既にある場合は最新データで置き換える
    /// (置き換えられた側は「破棄」として完了する)。
    /// </summary>
    private IEnumerator EnqueueAndWait(Vector2Int cellIndex, HeightStreamRequest request)
    {
        if (pendingStreamRequests.TryGetValue(cellIndex, out HeightStreamRequest old)) DiscardRequest(old);
        pendingStreamRequests[cellIndex] = request;
        EnsureStreamWorker();
        yield return new WaitUntil(() => request.done && (!request.succeeded || request.notified));
    }

    private void EnsureStreamWorker()
    {
        if (streamWorkerCoroutine == null)
        {
            streamWorkerCoroutine = RenderWorkScheduler.Start(this, HeightStreamWorker(), "TerrainManager", () => pendingStreamRequests.Count > 0);
        }
    }

    /// <summary>
    /// 待機中リクエストから「次に適用すべきセル」を選ぶ。
    /// カメラ視野内(+マージン)のセルを最優先し、次いでカメラに近い順。
    /// リクエスト数はロード対象セル数(高々数十)なので毎回の線形走査で十分軽い。
    /// </summary>
    private Vector2Int PickBestStreamCell()
    {
        GetStreamViewpoint(out Vector3 camPos, out Vector3 fwd);
        Vector2Int best = default;
        float bestScore = float.MaxValue;
        foreach (var kvp in pendingStreamRequests)
        {
            float score = ScoreStreamCell(kvp.Key, camPos, fwd);
            if (score < bestScore)
            {
                bestScore = score;
                best = kvp.Key;
            }
        }
        return best;
    }

    /// <summary>視野優先の順序付けに使うカメラ位置と水平前方向。カメラが無ければ原点から +Z を向いているとみなす。</summary>
    private static void GetStreamViewpoint(out Vector3 camPos, out Vector3 fwd)
    {
        Camera cam = Camera.main;
        camPos = cam != null ? cam.transform.position : Vector3.zero;
        fwd = cam != null ? cam.transform.forward : Vector3.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        else fwd.Normalize();
    }

    /// <summary>
    /// ストリーミング順序決定用のスコア(小さいほど優先)。
    /// カメラからセル矩形への水平最短距離をベースに、視野外(前方 STREAM_VIEW_HALF_ANGLE_DEG 超)の
    /// セルへ大きなペナルティを加算する。「カメラの真下・目の前の地形が最後に来る」ことを防ぎ、
    /// 振り向き時も視野に入ったセルが即座にキューの先頭へ来る。
    /// </summary>
    private float ScoreStreamCell(Vector2Int cell, Vector3 camPos, Vector3 camForwardHorizontal)
    {
        float cellSize = CellSize;
        float closestX = Mathf.Clamp(camPos.x, cell.x * cellSize, (cell.x + 1) * cellSize);
        float closestZ = Mathf.Clamp(camPos.z, cell.y * cellSize, (cell.y + 1) * cellSize);
        Vector3 toCell = new Vector3(closestX - camPos.x, 0f, closestZ - camPos.z);
        float dist = toCell.magnitude;

        // カメラ直下(セル内)のセルは常に視野内扱い。それ以外は水平角で視野判定する。
        if (dist > 1f)
        {
            float angle = Vector3.Angle(camForwardHorizontal, toCell);
            if (angle > STREAM_VIEW_HALF_ANGLE_DEG)
            {
                dist += 100000f;
            }
        }
        return dist;
    }

    /// <summary>
    /// 表示と独立したTerrainDataへ行単位で書き込み、完成後だけ公開する。
    /// 全消去・ジョブ取消しでは候補を破棄し、既存の表示を不完全な高さへ変更しない。
    /// 分割できない SyncHeightmap・タイル公開はそれぞれ別ステップにし、
    /// 共通予算がほぼ未使用になるまで(最大 FRESH_BUDGET_MAX_WAIT_FRAMES)待ってから、待ちと同じステップで始める。
    /// 版・loadedCells・更新セルの記録と done はタイル公開と同じステップで行い、通知の時点で購読者が新しい版を読めるようにする。
    /// OnTerrainTileReady の通知は <see cref="TileNotifyWorker"/> へ渡し、ワーカーは通知を待たずに次のタイルへ進む。
    /// テストが直接投入する要求(job==null)は辞書のキーのセルで処理し、取り消さない。
    /// </summary>
    private IEnumerator HeightStreamWorker()
    {
        yield return null;
        HeightStreamRequest active = null;
        TerrainData staged = null;
        try
        {
            while (true)
            {
                if (pendingStreamRequests.Count == 0) { yield return null; continue; }
                Vector2Int cell = PickBestStreamCell();
                active = pendingStreamRequests[cell];
                pendingStreamRequests.Remove(cell);
                int epoch = clearEpoch;
                bool canceled = IsRequestCanceled(active, epoch);
                Terrain published = null;
                if (active.restoreTile != null)
                {
                    if (!canceled)
                    {
                        for (int wait = 0; wait < FRESH_BUDGET_MAX_WAIT_FRAMES && !HasFreshBudget(); wait++) yield return null;
                        if (!IsRequestCanceled(active, epoch))
                        {
                            try
                            {
                                published = RestoreCachedTile(cell, active);
                                active.succeeded = published != null;
                            }
                            catch (Exception ex) { Debug.LogException(ex); }
                        }
                    }
                }
                else if (!canceled && active.heights != null)
                {
                    int res = active.heights.GetLength(0);
                    staged = new TerrainData { heightmapResolution = res, alphamapResolution = 16, size = terrainSize };
                    for (int row = 0; row < res; row += STREAM_BAND_ROWS)
                    {
                        canceled = IsRequestCanceled(active, epoch);
                        if (canceled) break;
                        try { ApplyHeightBand(staged, active.heights, res, row, Mathf.Min(STREAM_BAND_ROWS, res-row)); }
                        catch (Exception ex) { Debug.LogException(ex); canceled = true; break; }
                        yield return RenderWorkScheduler.Continue;
                    }
                    ReturnHeightBuffer(active.heights);
                    active.heights = null;
                    if (!canceled)
                    {
                        for (int wait = 0; wait < FRESH_BUDGET_MAX_WAIT_FRAMES && !HasFreshBudget(); wait++) yield return null;
                        canceled = IsRequestCanceled(active, epoch);
                        if (!canceled)
                        {
                            try { staged.SyncHeightmap(); }
                            catch (Exception ex) { Debug.LogException(ex); canceled = true; }
                            yield return RenderWorkScheduler.Continue;
                        }
                    }
                    if (!canceled)
                    {
                        for (int wait = 0; wait < FRESH_BUDGET_MAX_WAIT_FRAMES && !HasFreshBudget(); wait++) yield return null;
                        if (!IsRequestCanceled(active, epoch))
                        {
                            try
                            {
                                // 表示と接地の参照先を同じフレームで入れ替える。未完成の行を公開しない。
                                AttachTerrainTile(cell, staged);
                                staged = null;
                                published = terrainTiles[cell];
                                dynamicCellVersions[cell] = active.version;
                                loadedCells.Add(cell);
                                updatedTerrainCellsThisLoad.Add(cell);
                                active.succeeded = true;
                            }
                            catch (Exception ex) { Debug.LogException(ex); }
                        }
                    }
                }
                if (staged != null) { Destroy(staged); staged = null; }
                ReleaseRequestResources(active);
                active.done = true;
                if (published != null) EnqueueTileNotification(new TileNotification { cell = cell, tile = published, epoch = epoch, request = active });
                else active.notified = true;
                active = null;
                yield return RenderWorkScheduler.Continue;
            }
        }
        finally
        {
            if (staged != null) Destroy(staged);
            if (active != null)
            {
                ReturnHeightBuffer(active.heights);
                active.heights = null;
                if (active.restoreTile != null) DestroyTerrainTileObject(active.restoreTile.gameObject);
                active.restoreTile = null;
                active.done = true;
                active.notified = true;
            }
            streamWorkerCoroutine = null;
        }
    }

    /// <summary>
    /// 公開したタイルの OnTerrainTileReady を通知待ちに積み、通知ワーカーを起こす。
    /// 自身が非アクティブでワーカーを起動できないとき(テストの直接駆動)は、その場で通知する。
    /// </summary>
    private void EnqueueTileNotification(TileNotification notification)
    {
        pendingTileNotifications.Enqueue(notification);
        if (notifyWorkerCoroutine == null && isActiveAndEnabled)
            notifyWorkerCoroutine = RenderWorkScheduler.Start(this, TileNotifyWorker(), "TerrainManager", () => pendingTileNotifications.Count > 0);
        if (notifyWorkerCoroutine == null)
            while (pendingTileNotifications.Count > 0) DispatchTileNotification(pendingTileNotifications.Dequeue());
    }

    /// <summary>
    /// 通知待ちを公開順に1件ずつ、共通予算がほぼ未使用になるまで(最大 FRESH_BUDGET_MAX_WAIT_FRAMES)待ってから通知する。
    /// 待ちを挟まず取り出して通知するので、yield をまたいで手元に持つ通知は無い。待ちが空になったら終了する。
    /// </summary>
    private IEnumerator TileNotifyWorker()
    {
        try
        {
            while (pendingTileNotifications.Count > 0)
            {
                for (int wait = 0; wait < FRESH_BUDGET_MAX_WAIT_FRAMES && !HasFreshBudget(); wait++) yield return null;
                if (pendingTileNotifications.Count == 0) break;
                DispatchTileNotification(pendingTileNotifications.Dequeue());
                yield return RenderWorkScheduler.Continue;
            }
        }
        finally
        {
            notifyWorkerCoroutine = null;
        }
    }

    private void DispatchTileNotification(TileNotification notification)
    {
        // 待つ間に全クリア・アンロード・別タイルへの差し替えがあれば、そのタイルはもう表示されていない。
        if (notification.epoch == clearEpoch && terrainTiles.TryGetValue(notification.cell, out Terrain current) && current == notification.tile)
        {
            try { OnTerrainTileReady?.Invoke(notification.cell, notification.tile); }
            catch (Exception ex) { Debug.LogException(ex); }
        }
        notification.request.notified = true;
    }

    /// <summary>
    /// ハイトマップの行範囲 [rowStart, rowStart+rows) を部分SetHeightsでTerrainDataへ適用する。
    /// float[,] は行優先の連続メモリなので、行範囲の切り出しは BlockCopy 一発で済む。
    /// 標準バンドサイズのコピーは再利用バッファ(streamBandScratch)を使いGC割り当てを避ける。
    /// </summary>
    private void ApplyHeightBand(TerrainData tData, float[,] heights, int res, int rowStart, int rows)
    {
        float[,] strip;
        if (rows == STREAM_BAND_ROWS && res == TILE_RESOLUTION)
        {
            if (streamBandScratch == null)
            {
                streamBandScratch = new float[STREAM_BAND_ROWS, TILE_RESOLUTION];
            }
            strip = streamBandScratch;
        }
        else
        {
            strip = new float[rows, res];
        }

        Buffer.BlockCopy(heights, rowStart * res * sizeof(float), strip, 0, rows * res * sizeof(float));
        tData.SetHeightsDelayLOD(0, rowStart, strip);
        Utils.InvalidateHeightCache();
    }

    /// <summary>
    /// ストリーミング適用が完了したTerrainDataをGameObject化してセルに登録する。
    /// そのセルに旧タイルがあれば同一フレームで差し替え破棄する(チラつき防止)。
    /// タイル生成(TerrainCollider の初期ベイクを含む)は分割できず重いため、
    /// 呼び出し側(HeightStreamWorker)は共通予算の空きを待ってから呼ぶこと。
    /// TerrainCollider.terrainData は Terrain.CreateTerrainGameObject が設定済みなので再代入しない。
    /// </summary>
    private void AttachTerrainTile(Vector2Int cellIndex, TerrainData tData)
    {
        GameObject terrainGO = Terrain.CreateTerrainGameObject(tData);
        Transform container = GetContainer("Terrains");
        if (container != null) terrainGO.transform.SetParent(container);

        terrainGO.name = $"Terrain_{cellIndex.x}_{cellIndex.y}";
        terrainGO.layer = LayerMask.NameToLayer("Ground");

        // ベースYを SeaFloorDepthSpan だけ下げる。ハイトマップ正規化に同じオフセットを入れて
        // いるため、worldY = ベースY + normalized×terrainSize.y = 実標高 となり、海面(標高0)は
        // worldY=0、海底は worldY<0(最深 -SeaFloorDepthSpan)として描画される。
        Vector3 pos = new Vector3(cellIndex.x * terrainSize.x, -SeaFloorDepthSpan, cellIndex.y * terrainSize.z);
        terrainGO.transform.position = pos;

        Terrain terrainComp = terrainGO.GetComponent<Terrain>();
        terrainComp.drawInstanced = false;
        terrainComp.heightmapPixelError = LocalConstants.TERRAIN_HEIGHTMAP_PIXEL_ERROR;
        terrainComp.detailObjectDistance = 1000f;
        terrainComp.detailObjectDensity = 1f;
        terrainComp.treeBillboardDistance = 1000f;
        terrainComp.treeDistance = 1000f;
        terrainComp.treeCrossFadeLength = 200f;
        // ベースマップ切り替えを実質無効化する。デフォルト(1000m)のままだと、遠方のタイルが
        // カスタムシェーダーではなくベースマップ用シェーダーで描画され、マクロバリエーションや
        // 深度Offsetが遠方タイルに適用されなくなるため。
        terrainComp.basemapDistance = 100000f;

        // 旧→新タイルの差し替え(チラつき防止の要)。
        // 登録と破棄を同一フレームで行い(この間に yield はない)、Unity の Destroy は当該フレームの
        // 描画前に反映されるため、新旧タイルが同時に描画されるフレームも、
        // タイルが1枚も無いフレームも存在しない。
        terrainTiles.TryGetValue(cellIndex, out Terrain prevTerrain);
        terrainTiles[cellIndex] = terrainComp;
        loadedCellMaskDirty = true;
        Utils.InvalidateHeightCache();

        ApplyTerrainMaterial(terrainComp, tData);

        terrainGO.AddComponent<TerrainInputHandler>();

        var vmo = terrainGO.AddComponent<VisualModeObject>();
        vmo.objectType = InputObjectType.TERRAIN;
        terrainGO.AddComponent<VisualTerrainObject>();

        // 直前まで登録されていた旧タイルを破棄する(新タイルと同一GOでないことをガード)。
        GameObject oldTerrainToDestroy = null;
        if (prevTerrain != null && prevTerrain.gameObject != terrainGO)
        {
            oldTerrainToDestroy = prevTerrain.gameObject;
        }
        // LRU に同じセルの保持分があれば、新しく受信したデータを優先して破棄する。
        DropCachedTile(cellIndex, oldTerrainToDestroy);
        if (oldTerrainToDestroy != null)
        {
            DestroyTerrainTileObject(oldTerrainToDestroy);
        }

        // 新規タイルの登場で隣接タイルとのLOD継ぎ目を繋ぎ直す。
        // 全タイルが同一解像度のため、SetNeighbors は常に全境界で機能する。
        UpdateNeighbors(cellIndex);
    }

    /// <summary>
    /// LRU から取り出したタイルを表示に戻し、版・loadedCells・更新セルを同じステップで記録する。
    /// 同じセルに別のタイルが表示されていれば破棄する。保持中に破棄されていたら null を返す(呼び出し側は取得へ進む)。
    /// 通知は新規公開と同じく、呼び出し側が次のステップで行う。
    /// </summary>
    private Terrain RestoreCachedTile(Vector2Int cellIndex, HeightStreamRequest request)
    {
        Terrain tile = request.restoreTile;
        request.restoreTile = null;
        if (tile == null) return null;
        if (terrainTiles.TryGetValue(cellIndex, out Terrain previous) && previous != null && previous != tile)
            DestroyTerrainTileObject(previous.gameObject);
        tile.gameObject.SetActive(true);
        terrainTiles[cellIndex] = tile;
        loadedCellMaskDirty = true;
        dynamicCellVersions[cellIndex] = request.version;
        loadedCells.Add(cellIndex);
        updatedTerrainCellsThisLoad.Add(cellIndex);
        Utils.InvalidateHeightCache();
        UpdateNeighbors(cellIndex);
        return tile;
    }

    /// <summary>
    /// 受信バイナリからハイトマップを組み立てる重いCPU処理をバックグラウンドスレッドで実行する。
    /// Unity API や MonoBehaviour 上のフィールドに触れないよう、必要な値はスナップショットして渡す。
    /// </summary>
    private Task<float[,]> ProcessHeightmapAsync(byte[] rawBytes, int gridRows, int gridCols, int targetResolution)
    {
        // メインスレッドで必要な値をキャプチャしてからスレッドに投入する。
        float verticalSpan = terrainSize.y;
        float belowSeaSpan = SeaFloorDepthSpan;

        return Task.Run(() =>
        {
            float[,] heights = ConvertFloat16BinaryToHeights(rawBytes, gridRows, gridCols, verticalSpan, belowSeaSpan);
            return (gridRows == targetResolution && gridCols == targetResolution)
                ? heights
                : InterpolateHeightmapArray(heights, gridRows, gridCols, targetResolution);
        });
    }

    // ================================================================
    // 補間メソッド
    // ================================================================

    private static float[,] InterpolateHeightmapArray(float[,] source, int srcRows, int srcCols, int targetResolution)
    {
        float[,] result = new float[targetResolution, targetResolution];
        float denom = targetResolution > 1 ? (targetResolution - 1) : 1;
        for (int y = 0; y < targetResolution; y++)
        {
            for (int x = 0; x < targetResolution; x++)
            {
                float ny = y / denom;
                float nx = x / denom;
                float sy = ny * (srcRows - 1);
                float sx = nx * (srcCols - 1);
                // 近景・遠景ハイトマップの解像度変換もCatmull-Romで平滑化する。
                // (格子点は元値を通るので縫合は崩れない)
                result[y, x] = CatmullRomInterpolateArray(source, sx, sy, srcCols, srcRows);
            }
        }
        return result;
    }

    /// <summary>
    /// Catmull-Rom(3次)による2Dスムージング補間。正規化ハイトマップ[0,1]の
    /// 引き伸ばし(遠景65・超遠景の粗い格子 → 高解像度タイル)に使う。
    ///
    /// バイリニアはC0連続(値は繋がるが傾きが折れる)なので、低解像度ハイトマップを
    /// 高解像度へ引き伸ばすと格子ごとに折れ面が出て遠方地形がカクカク/ローポリに見える。
    /// Catmull-RomはC1連続で傾きも滑らかに繋がるためファセットが大幅に減る。
    ///
    /// 制御点(元の格子点)を必ず通る補間なので、格子点のワールド座標では元サンプルと
    /// 同値になる。隣接タイルは同じ元ハイトマップを接する範囲でサンプリングするため、
    /// 境界のワールド座標では双方が同値に収束し、タイル間に段差ができない(高さの縁ブレンド処理は無い)。
    ///
    /// Catmull-Romは急勾配でわずかにオーバーシュートし得るため、正規化前提で[0,1]に
    /// クランプする(ConvertFloat16BinaryToHeights の Clamp01 と整合)。
    /// </summary>
    private static float CatmullRomInterpolateArray(float[,] h, float x, float y, int width, int height)
    {
        if (x < 0f) x = 0f; else if (x > width - 1) x = width - 1;
        if (y < 0f) y = 0f; else if (y > height - 1) y = height - 1;

        int xi = (int)Math.Floor(x);
        int yi = (int)Math.Floor(y);
        float fx = x - xi;
        float fy = y - yi;

        // 4行をそれぞれX方向にCatmull-Rom補間し、その4値をY方向に補間する。
        float r0 = CatmullRomRow(h, xi, yi - 1, fx, width, height);
        float r1 = CatmullRomRow(h, xi, yi, fx, width, height);
        float r2 = CatmullRomRow(h, xi, yi + 1, fx, width, height);
        float r3 = CatmullRomRow(h, xi, yi + 2, fx, width, height);

        float v = CatmullRom1D(r0, r1, r2, r3, fy);
        return v < 0f ? 0f : (v > 1f ? 1f : v);
    }

    private static float CatmullRomRow(float[,] h, int xi, int row, float fx, int width, int height)
    {
        float p0 = SampleClampedArray(h, xi - 1, row, width, height);
        float p1 = SampleClampedArray(h, xi, row, width, height);
        float p2 = SampleClampedArray(h, xi + 1, row, width, height);
        float p3 = SampleClampedArray(h, xi + 2, row, width, height);
        return CatmullRom1D(p0, p1, p2, p3, fx);
    }

    private static float CatmullRom1D(float p0, float p1, float p2, float p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        // 標準的なCatmull-Rom基底(張力0.5)。t=0でp1、t=1でp2を通る。
        return 0.5f * (
            (2f * p1) +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    private static float SampleClampedArray(float[,] h, int x, int y, int width, int height)
    {
        if (x < 0) x = 0; else if (x > width - 1) x = width - 1;
        if (y < 0) y = 0; else if (y > height - 1) y = height - 1;
        return h[y, x];
    }

    // ================================================================
    // 公開メソッド
    // ================================================================

    /// <summary>都市の地形設定を受信済みか。未受信の間 GetMapWidth / GetMapHeight は暫定値を返す。</summary>
    public bool HasTerrainSettings => terrainSettings != null;
    public int GetMapWidth() => terrainSettings?.map_width ?? 10000;
    public int GetMapHeight() => terrainSettings?.map_height ?? 10000;

    /// <summary>
    /// ロード済み(可視)の地形タイル一覧。キーはUnityセルインデックス。
    /// HeightmapTextureManager が移動窓ハイトマップの切り出し元として参照する。
    /// 列挙中にロード処理と競合しないよう、yield をまたがず同一フレーム内で列挙を完了させること。
    /// </summary>
    public System.Collections.Generic.IReadOnlyDictionary<Vector2Int, Terrain> LoadedTerrainTiles => terrainTiles;

    public int GetCellsX() => terrainSettings != null ? terrainSettings.map_width / terrainSettings.cell_size : 20;
    public int GetCellsY() => terrainSettings != null ? terrainSettings.map_height / terrainSettings.cell_size : 20;
    public float GetCellSize() => terrainSettings?.cell_size ?? CellSize;

    // ================================================================
    // セル座標変換 (Unity ↔ バックエンド)
    // ================================================================

    /// <summary>
    /// Unity のセルインデックスをバックエンドの 0-based セルインデックスに変換する。
    /// バックエンドはマップ中央を原点とするワールド座標を、
    /// 配列の左上を基準にした非負のセルインデックスに変換している。
    /// Unity セルの中心点に対応するバックエンドセルを返す。
    /// </summary>
    public Vector2Int UnityCellToBackendCell(Vector2Int unityCell)
    {
        // マップの最大幅・高さがセルサイズの偶数倍（奇数セルの中央割が発生しない）であることを利用し、
        // 浮動小数点計算に伴う丸め誤差を排除して、安全な整数オフセットによる 1対1 のマッピングを行う。
        int offsetX = Mathf.RoundToInt((GetMapWidth() / 2.0f) / GetCellSize());
        int offsetY = Mathf.RoundToInt((GetMapHeight() / 2.0f) / GetCellSize());
        return new Vector2Int(unityCell.x + offsetX, unityCell.y + offsetY);
    }

    /// <summary>
    /// バックエンドの 0-based セルインデックスを Unity のセルインデックスに変換する。
    /// マップ幅・高さがセルサイズの偶数倍であるため、バックエンドセルとUnityセルは
    /// 1対1で対応し、常に1つのセルのみを返す。
    /// </summary>
    public IEnumerable<Vector2Int> BackendCellToUnityCells(Vector2Int backendCell)
    {
        // マップの最大幅・高さがセルサイズの偶数倍であることを利用し、
        // 浮動小数点の境界値判定で1つのバックエンドセルが複数のUnityセル（3x3化）へ割れるのを完全に排除する。
        // バックエンドの非負セル座標から整数オフセットを差し引いた、唯一の対応するUnityセルインデックスを返す。
        int offsetX = Mathf.RoundToInt((GetMapWidth() / 2.0f) / GetCellSize());
        int offsetY = Mathf.RoundToInt((GetMapHeight() / 2.0f) / GetCellSize());
        yield return new Vector2Int(backendCell.x - offsetX, backendCell.y - offsetY);
    }

    /// <summary>
    /// <see cref="FetchCoastline"/> が海岸線・湖・川・埋立地を取り終えるたびに進む版。
    /// <see cref="CoastlineStore.Version"/> は海岸線だけを入れた時点で進むので、埋立地の岸壁まで揃ってから使う処理
    /// （入港する船の接岸位置）はこちらを見る。
    /// </summary>
    public static int ShoreDataVersion { get; private set; }

    /// <summary>
    /// 海岸線（陸地の境界ポリゴン）を取得して <see cref="CoastlineStore"/> に格納し、続けて湖・川・埋立地を取得する。
    /// レスポンスは GeoJSON ライク（Polygon / MultiPolygon）。座標はワールド座標。
    /// 海岸線は埋立の完了で広がるので、初回ロード・強制リロードに加えて session:reload のたびに取り直す。
    /// </summary>
    public IEnumerator FetchCoastline()
    {
        // 呼び出し元(セッション開始・全量リロード)はこの直後にLODの常駐セル集合を算出する。
        // 算出はマップ寸法とセルサイズに依存し、未取得だと既定値でマップ外のセルを要求してしまうため、
        // 地形設定をここで確定させる。
        yield return EnsureSettingsLoaded();
        if (!settingsLoaded)
        {
            Debug.LogError("TerrainManager: 地形設定を取得できないため海岸線の取得を中断します。");
            yield break;
        }

        // 地表種別は地形タイル・超遠景のマテリアル生成より前に確定させる(後続のロードがこの値で塗る)。
        yield return FetchCityGroundCover();

        string error = null;

        yield return ApiClient.Get<CoastlineResponse>(
            "/terrain/coastline",
            response =>
            {
                CoastlineStore.SetFromGeoJson(response?.coordinates);
            },
            err => { error = err; }
        );

        if (!string.IsNullOrEmpty(error))
        {
            Debug.LogError($"TerrainManager: Failed to fetch coastline: {error}");
            CoastlineStore.Clear();
        }
        else
        {
            Debug.Log($"TerrainManager: Coastline loaded (hasCoastline={CoastlineStore.HasCoastline}).");
        }

        // 水面(海面以外の湖など)も海岸線と同じタイミングで取得・生成する。
        // WaterSurfaceManager は AfterSceneLoad で自己生成されるため通常は Instance が存在する。
        if (WaterSurfaceManager.Instance != null)
        {
            yield return WaterSurfaceManager.Instance.LoadAndBuild();
        }
        // 川(範囲ポリゴン+水面プロファイル線)も同じタイミングで取得し、RiverStore と水面メッシュを作る。
        // 敷設中パスの川横断判定(RoadPlanManager)が RiverStore を参照するため、道路編集より先に必要。
        if (RiverSurfaceManager.Instance != null)
        {
            yield return RiverSurfaceManager.Instance.LoadAndBuild();
        }
        // 埋立地(岸壁の天端と面)。接地(Utils.GetGroundHeight)が帯の中で天端を返し、港が岸壁区間を参照するので、
        // 区画・建物のロードより先に ReclaimedLandStore を揃える。中身が変わらなければ作り直さない。
        if (ReclaimedLandManager.Instance != null)
        {
            yield return ReclaimedLandManager.Instance.LoadAndBuild();
        }
        ShoreDataVersion++;
    }

    /// <summary>
    /// 都市の地表種別を GET /api/v1/city から取得し、変わっていれば表示中の地形マテリアルへ反映する。
    /// 取得に失敗したら前回の種別のまま描く(見た目だけの属性なので、地形のロードは止めない)。
    /// </summary>
    private IEnumerator FetchCityGroundCover()
    {
        int groundCover = cityGroundCover;
        yield return ApiClient.Get<CityGroundCoverEnvelope>(
            "/city/",
            response => { if (response?.city != null) groundCover = response.city.ground_cover; },
            err => Debug.LogWarning($"TerrainManager: 地表種別の取得に失敗したため前回の種別で描きます: {err}")
        );
        if (groundCover == cityGroundCover) yield break;
        cityGroundCover = groundCover;
        RefreshGroundCoverMaterials();
    }

    /// <summary>GET /api/v1/city のうち地表種別だけを読むレスポンス。</summary>
    [System.Serializable]
    private class CityGroundCoverEnvelope
    {
        public CityGroundCoverFields city;
    }

    [System.Serializable]
    private class CityGroundCoverFields
    {
        public int ground_cover = Constants.GROUND_COVER_GRASSLAND;
    }

    /// <summary>
    /// 海岸線エンドポイント（GeoJSON ライク）のレスポンス。
    /// coordinates は Polygon / MultiPolygon でネスト構造が異なるため、
    /// 解釈を <see cref="CoastlineStore.SetFromGeoJson"/> に委ね、ここでは生の JToken のまま受け取る。
    /// </summary>
    [System.Serializable]
    private class CoastlineResponse
    {
        public string type;
        public Newtonsoft.Json.Linq.JToken coordinates;
    }

    /// <summary>
    /// 遠景高さマップから指定セルのおおまかな高さ範囲（min, max）を取得する。
    /// farTerrainHeights が未ロードの場合は false を返す。
    /// セルのティア判定で、カメラからの3次元最短距離を計算する際に使用する。
    /// </summary>
    public bool TryGetCellHeightRange(Vector2Int cellIndex, out float minY, out float maxY)
    {
        if (!IsCityCell(cellIndex) && TryGetBackgroundHeightRange(cellIndex, out minY, out maxY)) return true;
        minY = -SeaFloorDepthSpan;
        maxY = terrainSize.y - SeaFloorDepthSpan;

        if (farTerrainHeights == null || terrainSettings == null) return false;

        float cellSize = terrainSettings.cell_size;
        float mapWidth = terrainSettings.map_width;
        float mapHeight = terrainSettings.map_height;

        float cellMinX = cellIndex.x * cellSize;
        float cellMaxX = (cellIndex.x + 1) * cellSize;
        float cellMinZ = cellIndex.y * cellSize;
        float cellMaxZ = (cellIndex.y + 1) * cellSize;

        float farOriginX = -mapWidth / 2f;
        float farOriginZ = -mapHeight / 2f;

        float uMin = (cellMinX - farOriginX) / mapWidth;
        float uMax = (cellMaxX - farOriginX) / mapWidth;
        float vMin = (cellMinZ - farOriginZ) / mapHeight;
        float vMax = (cellMaxZ - farOriginZ) / mapHeight;

        int farCols = farTerrainHeights.GetLength(1);
        int farRows = farTerrainHeights.GetLength(0);

        int sx = Mathf.FloorToInt(uMin * (farCols - 1));
        int ex = Mathf.CeilToInt(uMax * (farCols - 1));
        int sy = Mathf.FloorToInt(vMin * (farRows - 1));
        int ey = Mathf.CeilToInt(vMax * (farRows - 1));

        sx = Mathf.Clamp(sx, 0, farCols - 1);
        ex = Mathf.Clamp(ex, 0, farCols - 1);
        sy = Mathf.Clamp(sy, 0, farRows - 1);
        ey = Mathf.Clamp(ey, 0, farRows - 1);

        minY = float.MaxValue;
        maxY = float.MinValue;

        for (int y = sy; y <= ey; y++)
        {
            for (int x = sx; x <= ex; x++)
            {
                float h = farTerrainHeights[y, x] * terrainSize.y - SeaFloorDepthSpan;
                if (h < minY) minY = h;
                if (h > maxY) maxY = h;
            }
        }

        return true;
    }

    /// <summary>未常駐部分を通る長い道路向けの背景高さ。編集を含む全景と同じ面を参照する。</summary>
    public bool TrySampleOverview(Vector3 position, out float height)
    {
        height = 0;
        if (farTerrainHeights == null || terrainSettings == null) return false;
        float u = (position.x + GetMapWidth()*.5f)/GetMapWidth();
        float v = (position.z + GetMapHeight()*.5f)/GetMapHeight();
        if (u < 0 || u > 1 || v < 0 || v > 1) return TrySampleBackground(position, out height);
        int cols = farTerrainHeights.GetLength(1), rows = farTerrainHeights.GetLength(0);
        height = CatmullRomInterpolateArray(farTerrainHeights, u*(cols-1), v*(rows-1), cols, rows) * terrainSize.y - SeaFloorDepthSpan;
        return true;
    }

    /// <summary>
    /// 座標を含む表示中のタイル。セルの負側の辺ちょうどの点は負側の隣タイルの縁でもあるので、
    /// 自セルが未常駐なら隣を見る(常駐境界の格子点が全景の高さへ落ちないようにする)。
    /// </summary>
    public Terrain GetTerrainAtPosition(Vector3 position)
    {
        int cx = Mathf.FloorToInt(position.x / terrainSize.x);
        int cz = Mathf.FloorToInt(position.z / terrainSize.z);
        if (TryGetVisibleTile(cx, cz, out Terrain terrain)) return terrain;
        bool onMinX = cx * terrainSize.x == position.x;
        bool onMinZ = cz * terrainSize.z == position.z;
        if (onMinX && TryGetVisibleTile(cx - 1, cz, out terrain)) return terrain;
        if (onMinZ && TryGetVisibleTile(cx, cz - 1, out terrain)) return terrain;
        if (onMinX && onMinZ && TryGetVisibleTile(cx - 1, cz - 1, out terrain)) return terrain;
        return null;
    }

    private bool TryGetVisibleTile(int cx, int cz, out Terrain terrain) =>
        terrainTiles.TryGetValue(new Vector2Int(cx, cz), out terrain) && terrain != null && terrain.gameObject.activeSelf;

    private bool showContourLines = false;

    /// <summary>
    /// 等高線の表示/非表示を切り替える。
    /// グローバルシェーダー変数を更新して、すべての地形タイルに即座に反映させる。
    /// </summary>
    public void SetContourLinesVisible(bool visible)
    {
        showContourLines = visible;
        Shader.SetGlobalFloat("_ShowContour", visible ? 1f : 0f);
        Debug.Log($"TerrainManager: Contour lines visibility set to {visible}");
    }

    // ================================================================
    // 隣接タイル管理
    // ================================================================

    /// <summary>
    /// <see cref="Terrain.SetNeighbors"/> は隣接 Terrain 同士の heightmapResolution が一致している必要がある。
    /// 現在は全タイルが TILE_RESOLUTION で統一されているため常に一致するが、
    /// 想定外の解像度不一致(遺物タイル等)への防御として判定を残す
    /// (不一致の境界は接続しない)。SetNeighbors は LOD の継ぎ目処理だけで、高さは変えない。
    /// </summary>
    private static Terrain NeighborIfSameResolution(Terrain self, Terrain candidate)
    {
        if (self == null || candidate == null) return null;
        TerrainData sd = self.terrainData;
        TerrainData nd = candidate.terrainData;
        if (sd == null || nd == null) return null;
        return sd.heightmapResolution == nd.heightmapResolution ? candidate : null;
    }

    private Terrain GetMatchingNeighbor(Terrain self, Vector2Int neighborCellIndex)
    {
        return NeighborIfSameResolution(self, GetTile(neighborCellIndex));
    }

    private void UpdateNeighbors(Vector2Int cellIndex)
    {
        Vector2Int leftIndex = new Vector2Int(cellIndex.x - 1, cellIndex.y);
        Vector2Int rightIndex = new Vector2Int(cellIndex.x + 1, cellIndex.y);
        Vector2Int topIndex = new Vector2Int(cellIndex.x, cellIndex.y + 1);
        Vector2Int bottomIndex = new Vector2Int(cellIndex.x, cellIndex.y - 1);

        terrainTiles.TryGetValue(cellIndex, out Terrain center);
        terrainTiles.TryGetValue(leftIndex, out Terrain left);
        terrainTiles.TryGetValue(rightIndex, out Terrain right);
        terrainTiles.TryGetValue(topIndex, out Terrain top);
        terrainTiles.TryGetValue(bottomIndex, out Terrain bottom);

        if (center != null)
        {
            center.SetNeighbors(
                GetMatchingNeighbor(center, leftIndex),
                GetMatchingNeighbor(center, topIndex),
                GetMatchingNeighbor(center, rightIndex),
                GetMatchingNeighbor(center, bottomIndex));
        }
        if (left != null)
        {
            left.SetNeighbors(
                null,
                GetMatchingNeighbor(left, new Vector2Int(leftIndex.x, leftIndex.y + 1)),
                GetMatchingNeighbor(left, cellIndex),
                GetMatchingNeighbor(left, new Vector2Int(leftIndex.x, leftIndex.y - 1)));
        }
        if (right != null)
        {
            right.SetNeighbors(
                GetMatchingNeighbor(right, cellIndex),
                GetMatchingNeighbor(right, new Vector2Int(rightIndex.x, rightIndex.y + 1)),
                null,
                GetMatchingNeighbor(right, new Vector2Int(rightIndex.x, rightIndex.y - 1)));
        }
        if (top != null)
        {
            top.SetNeighbors(
                GetMatchingNeighbor(top, new Vector2Int(topIndex.x - 1, topIndex.y)),
                null,
                GetMatchingNeighbor(top, new Vector2Int(topIndex.x + 1, topIndex.y)),
                GetMatchingNeighbor(top, cellIndex));
        }
        if (bottom != null)
        {
            bottom.SetNeighbors(
                GetMatchingNeighbor(bottom, new Vector2Int(bottomIndex.x - 1, bottomIndex.y)),
                GetMatchingNeighbor(bottom, cellIndex),
                GetMatchingNeighbor(bottom, new Vector2Int(bottomIndex.x + 1, bottomIndex.y)),
                null);
        }
    }

    private void UpdateNeighborsForAdjacents(Vector2Int centerCell)
    {
        Vector2Int[] neighbors = new Vector2Int[]
        {
            new Vector2Int(centerCell.x - 1, centerCell.y),
            new Vector2Int(centerCell.x + 1, centerCell.y),
            new Vector2Int(centerCell.x, centerCell.y - 1),
            new Vector2Int(centerCell.x, centerCell.y + 1)
        };

        foreach (var neighbor in neighbors)
        {
            if (terrainTiles.ContainsKey(neighbor))
            {
                UpdateNeighbors(neighbor);
            }
        }
    }

    private Terrain GetTile(Vector2Int index)
    {
        terrainTiles.TryGetValue(index, out Terrain t);
        return t;
    }

    // ================================================================
    // マテリアル設定
    // ================================================================

    /// <summary>
    /// 常駐セルのタイルの Terrain にマテリアルを適用する。
    /// </summary>
    private void ApplyTerrainMaterial(Terrain terrain, TerrainData terrainData)
    {
        var resources = terrain.GetComponent<TerrainTileResources>();
        if (resources == null) resources = terrain.gameObject.AddComponent<TerrainTileResources>();
        resources.Clear();
        terrain.materialTemplate = null;
        if (useCustomMaterial && customTerrainMaterial != null)
        {
            terrain.materialTemplate = customTerrainMaterial;
            ApplyTerrainOffsetMaterial(terrain, resources);
            return;
        }

        if (customTerrainLayers != null && customTerrainLayers.Length > 0)
        {
            List<TerrainLayer> validLayers = new List<TerrainLayer>();
            foreach (var layer in customTerrainLayers)
            {
                if (layer != null) validLayers.Add(layer);
            }
            if (validLayers.Count > 0)
            {
                terrainData.terrainLayers = validLayers.ToArray();
                ApplyTerrainOffsetMaterial(terrain, resources);
                return;
            }
        }

        ApplyDefaultTerrainMaterial(terrainData, resources);
        ApplyTerrainOffsetMaterial(terrain, resources);
    }

    /// <summary>
    /// タイルごとにクローンした materialTemplate を識別するための名前サフィックス。
    /// マテリアルパラメータをタイル単位で設定するため、共有マテリアル(アセット等)を
    /// そのまま materialTemplate に使うとアセット本体を書き換えてしまう。
    /// </summary>
    private const string TILE_MATERIAL_SUFFIX = " (TileInstance)";

    /// <summary>
    /// TerrainのmaterialTemplateを、Z-fighting防止用の Custom/DepthOffsetTerrainLit(Offset 1,1)に
    /// 差し替える。共有マテリアルは必ずクローンして「タイルごとの個別インスタンス」にする
    /// (アセット本体の書き換えを防ぐため)。クローン済みかは名前サフィックスで識別し、
    /// 再適用時の二重クローンを避ける。
    /// </summary>
    private void ApplyTerrainOffsetMaterial(Terrain terrain, TerrainTileResources resources)
    {
        if (terrain == null) return;

        // Shader.Find はビルド後にシェーダーが見つからないことがあるため、
        // SerializeField での直接参照を優先し、未設定時のみフォールバックとして Find する。
        Shader offsetShader = depthOffsetTerrainShader;
        if (offsetShader == null)
        {
            offsetShader = Shader.Find("Custom/DepthOffsetTerrainLit");
        }

        if (offsetShader == null)
        {
            Debug.LogWarning("TerrainManager: Custom/DepthOffsetTerrainLit shader not found");
            return;
        }

        Material mat = terrain.materialTemplate;
        if (mat != null)
        {
            if (mat.shader != offsetShader || !mat.name.EndsWith(TILE_MATERIAL_SUFFIX))
            {
                Material cloned = new Material(mat);
                cloned.shader = offsetShader;
                cloned.name = mat.name + TILE_MATERIAL_SUFFIX;
                terrain.materialTemplate = cloned;
                resources.Own(cloned);
            }
        }
        else
        {
            terrain.materialTemplate = new Material(offsetShader) { name = "TerrainTile" + TILE_MATERIAL_SUFFIX };
            resources.Own(terrain.materialTemplate);
        }

        ApplyTerrainMaterialSettings(terrain.materialTemplate);
    }

    private void ApplyTerrainMaterialSettings(Material material)
    {
        if (material == null) return;

        // 地表種別で変わる値(下地の色・凹凸・照明後の色被せ・岩の色味と波打ち際)
        ActiveGroundCover.ApplyTo(material);

        // 以下は地表種別に依存しない共通の値。
        material.SetFloat(TerrainMacroVariationScaleId, Mathf.Max(1f, terrainMacroVariationScale));
        material.SetFloat(TerrainColorGradeLowHeightId, terrainColorGradeLowHeight);
        material.SetFloat(TerrainColorGradeHighHeightId, Mathf.Max(terrainColorGradeLowHeight + 1f, terrainColorGradeHighHeight));
        // シェーダーはディテール法線の周期の逆数(1/m)を乗算で使う
        material.SetFloat(TerrainDetailNormalScaleId, 1f / Mathf.Max(0.01f, terrainDetailNormalTilingMeters));

        EnsureCliffTextures();

        // 崖の岩ブレンド設定(シェーダー側: Assets/Shaders/TerrainCliffRock.hlsl)。
        // アルベドが無い場合はシェーダーが単色フォールバック(_CliffFallbackColor)に切り替わる。
        bool cliffTexturesAvailable = cliffAlbedoTexture != null && cliffNormalTexture != null;
        if (cliffAlbedoTexture != null) material.SetTexture(CliffAlbedoMapId, cliffAlbedoTexture);
        if (cliffNormalTexture != null) material.SetTexture(CliffNormalMapId, cliffNormalTexture);
        material.SetFloat(CliffTexturesLoadedId, cliffTexturesAvailable ? 1f : 0f);
        // シェーダーではタイリング周期の逆数(1/m)を乗算で使うため、ここで逆数化して渡す
        material.SetFloat(CliffUvScaleId, 1f / Mathf.Max(1f, cliffTilingMeters));
        material.SetFloat(CliffSlopeThresholdId, cliffSlopeThreshold);
        material.SetFloat(CliffSlopeBlendId, Mathf.Max(0.01f, cliffSlopeBlend));
        material.SetFloat(CliffNoiseScaleId, Mathf.Max(1f, cliffNoiseScale));
        material.SetFloat(CliffNoiseStrengthId, cliffNoiseStrength);
        material.SetFloat(CliffNormalStrengthId, cliffNormalStrength);
        material.SetFloat(CliffSmoothnessId, cliffSmoothness);
        material.SetColor(CliffFallbackColorId, cliffFallbackColor);
        material.SetFloat(CliffLowlandStartId, cliffLowlandStartHeight);
        material.SetFloat(CliffLowlandFullId, cliffLowlandFullHeight);
    }

    /// <summary>
    /// 崖用の岩テクスチャを解決する。Inspector(シーン)で未設定の場合は
    /// Resources/Textures/cliff-rock-albedo / cliff-rock-normal をフォールバックとして
    /// ロードする。シーンファイルを編集しなくてもコード側のデフォルトだけで動作させるための措置
    /// (過去に customTerrainLayers 等のシーン側設定漏れによる事故があったため)。
    /// どちらかが見つからない場合は警告を出し、シェーダーの単色フォールバックで動作を継続する。
    /// </summary>
    private void EnsureCliffTextures()
    {
        if (cliffTexturesResolved) return;
        cliffTexturesResolved = true;

        if (cliffAlbedoTexture == null)
        {
            cliffAlbedoTexture = Resources.Load<Texture2D>("Textures/cliff-rock-albedo");
        }
        if (cliffNormalTexture == null)
        {
            cliffNormalTexture = Resources.Load<Texture2D>("Textures/cliff-rock-normal");
        }

        if (cliffAlbedoTexture == null || cliffNormalTexture == null)
        {
            Debug.LogWarning(
                "TerrainManager: 崖用の岩テクスチャが見つかりません" +
                $"(albedo={(cliffAlbedoTexture != null)}, normal={(cliffNormalTexture != null)})。" +
                "Resources/Textures/cliff-rock-albedo, cliff-rock-normal を配置するか、" +
                "InspectorのTerrain Cliff Rock Settingsに割り当ててください。" +
                "それまでは単色フォールバックで描画します。");
        }
    }

    /// <summary>
    /// 地表種別のテクスチャ1枚の TerrainLayer をタイルに設定する。
    /// 法線マップは設定しない(凹凸は手続きのディテール法線と風紋。UltraFar陸地メッシュも法線マップを持たないため揃う)。
    /// テクスチャが見つからない場合は下地色Aの単色テクスチャで代用する。
    /// </summary>
    private void ApplyDefaultTerrainMaterial(TerrainData terrainData, TerrainTileResources resources)
    {
        GroundCoverProfile ground = ActiveGroundCover;
        TerrainLayer groundLayer = resources.Own(new TerrainLayer());

        Texture2D diffuseTex = ResolveGroundTexture(ground);
        if (diffuseTex == null)
        {
            diffuseTex = resources.Own(new Texture2D(1, 1));
            diffuseTex.SetPixel(0, 0, ground.groundColorA);
            diffuseTex.Apply();
        }
        groundLayer.diffuseTexture = diffuseTex;

        Vector2 tileSize = ground.textureTileSize;
        if (tileSize.x <= 0f || tileSize.y <= 0f)
        {
            tileSize = new Vector2(terrainSize.x, terrainSize.z);
        }
        groundLayer.tileSize = tileSize;
        groundLayer.metallic = defaultMetallic;
        groundLayer.smoothness = ground.smoothness;

        terrainData.terrainLayers = new TerrainLayer[] { groundLayer };
    }

    [ContextMenu("Refresh Terrain Materials")]
    public void RefreshAllTerrainMaterials()
    {
        foreach (var kvp in terrainTiles)
        {
            if (kvp.Value != null)
            {
                ApplyTerrainMaterial(kvp.Value, kvp.Value.terrainData);
            }
        }
        // LRU の保持分も塗り直し、地表種別の変更前の見た目で復元しないようにする。
        foreach (var entry in tileCache.Values)
        {
            if (entry.terrain != null) ApplyTerrainMaterial(entry.terrain, entry.terrain.terrainData);
        }
    }

    /// <summary>
    /// 地表の見た目の変更(地表種別の切り替え・Inspectorでの調整)を、表示中の地形タイル・超遠景・都市外背景へ反映する。
    /// </summary>
    private void RefreshGroundCoverMaterials()
    {
        RefreshAllTerrainMaterials();
        if (ultraFarLandMat != null) EnsureUltraFarLandMaterials();
        if (backgroundMaterial != null) ApplyFarTerrainMaterialSettings(backgroundMaterial);
    }

    /// <summary>
    /// 現在描いている地表種別のプロファイル。Inspector の groundCoverPreview が0以外ならそれを優先する。
    /// パレットに無い種別はコード既定値を使い、未知の種別は草原で描く。
    /// </summary>
    private GroundCoverProfile ActiveGroundCover
    {
        get
        {
            int groundCover = groundCoverPreview != 0 ? groundCoverPreview : cityGroundCover;
            if (groundCoverPalette == null && !groundCoverPaletteResolved)
            {
                groundCoverPaletteResolved = true;
                groundCoverPalette = Resources.Load<GroundCoverPalette>(GroundCoverPalette.ResourcePath);
            }
            GroundCoverProfile profile = groundCoverPalette != null ? groundCoverPalette.Find(groundCover) : null;
            return profile ?? FindDefaultGroundCover(groundCover) ?? FindDefaultGroundCover(Constants.GROUND_COVER_GRASSLAND);
        }
    }

    private static GroundCoverProfile FindDefaultGroundCover(int groundCover)
    {
        foreach (GroundCoverProfile profile in DefaultGroundCoverProfiles)
        {
            if (profile.groundCover == groundCover) return profile;
        }
        return null;
    }

    /// <summary>プロファイルの地面テクスチャ。見つからなければ警告を1回だけ出して null を返す。</summary>
    private Texture2D ResolveGroundTexture(GroundCoverProfile ground)
    {
        if (string.IsNullOrEmpty(ground.textureResource)) return null;
        if (!groundTextureCache.TryGetValue(ground.textureResource, out Texture2D texture))
        {
            texture = Resources.Load<Texture2D>(ground.textureResource);
            if (texture == null)
            {
                Debug.LogWarning($"TerrainManager: 地面テクスチャ Resources/{ground.textureResource} が見つからないため、下地色Aの単色で描きます。");
            }
            groundTextureCache[ground.textureResource] = texture;
        }
        return texture;
    }

    // ================================================================
    // ユーティリティ
    // ================================================================

    private int GetValidTerrainResolution(int desiredSize)
    {
        // 描画負荷を抑えるため、ハイトマップ解像度の上限を513に制限する。
        // 500mセルで1m間隔のデータでも501ポイント→513解像度で十分に表現できる。
        int[] validResolutions = { 33, 65, 129, 257, 513 };
        foreach (int resolution in validResolutions)
        {
            if (resolution >= desiredSize) return resolution;
        }
        return validResolutions[validResolutions.Length - 1];
    }

    private int ParseIntHeader(UnityWebRequest req, string headerName, int defaultValue)
    {
        var headers = req.GetResponseHeaders();
        if (headers != null)
        {
            foreach (var kvp in headers)
            {
                if (kvp.Key.Equals(headerName, System.StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(kvp.Value, out int val)) return val;
                }
            }
        }
        return defaultValue;
    }
}

/// <summary>
/// /api/v1/terrain/settings レスポンス用データモデル
/// </summary>
[System.Serializable]
public class TerrainSettingsData
{
    public int map_height;
    public int map_width;
    public int cell_size;
    public int far_map_spacing;
    public int near_map_spacing;
    public int dynamic_map_spacing;
}

/// <summary>
/// /api/v1/terrain/cell-versions レレスポンス用データモデル
/// </summary>
[System.Serializable]
public class CellVersionInfo
{
    public int cell_x;
    public int cell_z;
    public int version;
}

[System.Serializable]
public class CellVersionsResponse
{
    public System.Collections.Generic.List<CellVersionInfo> cells;
}
