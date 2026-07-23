using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using TMPro;

/// <summary>
/// バックエンドAPIから3段階LOD（遠景/近景/超近景）の標高マップを取得し、Terrainを配置するマネージャー。
/// ・遠景: /api/v1/terrain/far （マップ全体、常時表示）
/// ・近景: /api/v1/terrain/near?bbox=... （カメラ周辺、中解像度）
/// ・超近景: /api/v1/terrain/dynamic?bbox=... （セル単位、最高解像度、404時は近景にフォールバック）
/// ・セルサイズは /api/v1/terrain/settings から遅延取得
/// </summary>
public class TerrainManager : BaseApiDataManager
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

    // LoD: 3D距離ベースの最大描画距離（メートル）
    [SerializeField] private float maxRenderDistance = 1000f;

    /// <summary>
    /// 未開放セルの暗色表示が有効かどうか。
    /// true の場合、未開放セルの地形(per-cellタイル・UltraFar陸地メッシュとも)は
    /// 通常のテクスチャ付き地面に unacquiredTint を乗算した「少し暗い地面」で描画される。
    /// false の場合は全域が通常色になる。
    /// </summary>
    public bool IsUnlockOverlayActive { get; private set; } = true;

    [Header("セル境界線・座標ラベル表示")]
    [SerializeField] private float cellLabelYOffset = 2.0f;
    [SerializeField] private float cellLabelFontSize = 3.0f;
    [SerializeField] private Color cellLabelColor = Color.white;

    /// <summary>
    /// セル境界線と座標ラベルの表示が有効かどうか。
    /// ToggleUnlockOverlay と連動し、同じボタンで一括制御する。
    /// </summary>
    public bool ShowCellBordersAndLabels { get; private set; } = true;

    // セルオーバーレイ（境界線）を OverlayMeshRenderer で描画するための状態
    private GameObject cellOverlayMeshObj;
    private OverlayMeshRenderer cellOverlayRenderer;
    /// <summary>境界線セグメントのワールド座標キャッシュ。acquiredCells 更新時に再構築する。</summary>
    private List<(Vector3, Vector3)> borderSegments3D = new List<(Vector3, Vector3)>();
    private bool borderSegmentsDirty = true;

    // セルオーバーレイメッシュの再構築を最小化するための状態。
    // 境界線の点線ジオメトリはワールド固定幅でカメラに依存しないため、
    // 入力・カメラが動かないアイドル時に毎フレーム作り直す必要がない。
    // メッシュを一度構築したらフラグを立て、境界の変化（borderSegmentsDirty）や
    // 表示切り替え（ShowCellBordersAndLabels の変化）があったフレームだけ再構築する。
    private bool cellOverlayMeshBuilt = false;
    private bool cellOverlayLastShowBorders = false;

    private Dictionary<Vector2Int, TextMeshPro> cellLabelObjects = new Dictionary<Vector2Int, TextMeshPro>();
    private TMP_FontAsset cachedFontAsset;

    /// <summary>
    /// dynamicティアの各セルのversionキャッシュ。
    /// 差分更新時にバックエンドのX-Cell-Versionと比較し、変更があった場合のみ再描画する。
    /// </summary>
    private Dictionary<Vector2Int, int> dynamicCellVersions = new Dictionary<Vector2Int, int>();

    /// <summary>
    /// デバッグモードの有効状態。MinimapOverlay などから参照される。
    /// </summary>
    public bool IsDebugMode { get; set; } = false;

    /// <summary>
    /// 未開放セルの濃色表示をトグルする。
    /// 全 Terrain タイルの Splatmap を再構築して即座に反映する。
    /// </summary>
    public void ToggleUnlockOverlay()
    {
        IsUnlockOverlayActive = !IsUnlockOverlayActive;
        ShowCellBordersAndLabels = IsUnlockOverlayActive;
        RefreshAllUnlockOverlays();
        RefreshAllCellOverlays();
        Debug.Log($"TerrainManager: Unlock overlay toggled to {IsUnlockOverlayActive}");
    }

    /// <summary>
    /// 全 Terrain タイルに対して、現在の IsUnlockOverlayActive 状態に基づき
    /// 未開放セルの濃色表示を適用または解除する。
    /// </summary>
    private void RefreshAllUnlockOverlays()
    {
        foreach (var kvp in terrainTiles)
        {
            if (kvp.Value != null)
            {
                ApplyUnlockOverlayToTerrain(kvp.Key, kvp.Value);
            }
        }
        // 超遠景の陸地メッシュにも濃色表示状態を反映する(未開放サブメッシュの色を切り替える)。
        RefreshUltraFarLandOverlay();
    }

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
    /// <summary>UltraFar 陸地の通常色。テクスチャ未設定時のフラット色フォールバック(テクスチャありなら白=素のテクスチャ色)。</summary>
    [SerializeField] private Color ultraFarLandColor = new Color(0.45f, 0.62f, 0.40f, 1f);
    /// <summary>
    /// 未開放セルを暗くする乗算ティント。オーバーレイ表示(IsUnlockOverlayActive)が有効なとき、
    /// 通常のテクスチャ付き地面にこの色を乗算して「少し暗い地面」にする。
    /// per-cellタイル(地形シェーダーの _TerrainTint)と UltraFar 陸地メッシュの未開放サブメッシュ
    /// (_BaseColor)の両方で共通に使い、ティア境界でも未開放の暗さが揃うようにする。
    /// </summary>
    [SerializeField] private Color unacquiredTint = new Color(0.55f, 0.60f, 0.55f, 1f);
    /// <summary>UltraFar 陸地メッシュのグリッド間隔(m)。小さいほど海岸線・起伏が細かいが頂点が増える。</summary>
    [SerializeField] private float ultraFarGridStep = 80f;
    /// <summary>UltraFar 陸地を海面板の上に保つための頂点Yオフセット(m)。</summary>
    [SerializeField] private float ultraFarHeightOffset = 1.0f;
    /// <summary>
    /// UltraFar 陸地メッシュを「per-cellタイルがロード済みのセル」で地中に沈める量(m)。
    /// UltraFar メッシュは far_map 由来の粗い起伏を持つため、near_map 由来の Near/Dynamic タイルとは
    /// 起伏の激しい場所で数十mの高低差が生じ、タイルの上に突き出て斑点状に見える。
    /// タイルが存在するセルはロード済みセルマスク(_LoadedCellMask)で頂点ごとに判定し、
    /// この量だけ大胆に沈めて突き抜けを防ぐ(タイルの無いセルは沈めないため海色の穴は出ない)。
    /// </summary>
    [SerializeField] private float ultraFarSinkAmount = 100f;

    [Header("Default Material Settings")]
    [SerializeField] private Color defaultTerrainColor = new Color(0.2f, 0.8f, 0.2f);
    [SerializeField] private Texture2D defaultDiffuseTexture;
    [SerializeField] private Texture2D defaultNormalTexture;
    [SerializeField] private Vector2 defaultTileSize = Vector2.zero;
    [SerializeField] private float defaultMetallic = 0f;
    // スタイライズ向けに完全マット(0)をやめ、柔らかいスペキュラで陰影に立体感を出す。
    // 平地の手続きディテール法線(terrainDetailNormalStrength)と併せて、太陽角度が
    // 変わったときの光の起伏を生む。注意: この値はシーン(GameScene.unity)にシリアライズ
    // されているため、既存シーンのインスタンスにはシーン側の値が優先される。実際に反映
    // するにはシーンの TerrainManager.defaultSmoothness も更新すること。
    [SerializeField] private float defaultSmoothness = 0.15f;

    [Header("Terrain Macro Variation Settings")]
    [SerializeField, Range(0f, 0.5f)] private float terrainMacroVariationStrength = 0.12f;
    [SerializeField] private float terrainMacroVariationScale = 350f;
    [SerializeField] private Color terrainMacroVariationTint = new Color(0.85f, 0.95f, 0.75f, 1f);

    // スタイライズ向けのカラーグレード・ディテール法線設定。シェーダー実装は
    // Assets/Shaders/DepthOffsetTerrainLitMacroVariation.hlsl(ApplyTerrainColorGrade /
    // ApplyGroundDetailNormal)。写真テクスチャの精細さではなく色設計・法線で
    // 「破綻なく整った」非写実の地表を作るための層。新規フィールドのためシーンに
    // シリアライズ値が無く、ここのコード既定値がそのまま実行時に反映される。
    [Header("Terrain Color Grade Settings")]
    /// <summary>カラーグレードの強度。0で無効(従来の見た目)。標高・傾斜に応じた色被せの効き。</summary>
    [SerializeField, Range(0f, 1f)] private float terrainColorGradeStrength = 0.22f;
    /// <summary>低地色になる標高(m)。海面(0m)付近を想定。</summary>
    [SerializeField] private float terrainColorGradeLowHeight = 0f;
    /// <summary>高地色になる標高(m)。これ以上の標高は一様に高地色へ寄る。</summary>
    [SerializeField] private float terrainColorGradeHighHeight = 300f;
    /// <summary>低地のティント(≈1.0付近で明度維持しつつ僅かに色付け)。やや暖色の緑。</summary>
    [SerializeField] private Color terrainLowColor = new Color(0.82f, 0.88f, 0.68f, 1f);
    /// <summary>高地のティント。寒色・低彩度で標高感を出す。</summary>
    [SerializeField] private Color terrainHighColor = new Color(0.90f, 0.92f, 0.98f, 1f);
    /// <summary>急斜面のティント。土・岩肌寄りのアースカラー。</summary>
    [SerializeField] private Color terrainSlopeColor = new Color(0.72f, 0.66f, 0.55f, 1f);

    [Header("Terrain Detail Normal Settings")]
    /// <summary>平地の手続きディテール法線の強度。0で無効。法線マップテクスチャは使わない。</summary>
    [SerializeField, Range(0f, 1f)] private float terrainDetailNormalStrength = 0.25f;
    /// <summary>ディテール法線ノイズのタイリング周期(m)。小さいほど細かい凹凸。</summary>
    [SerializeField] private float terrainDetailNormalTilingMeters = 3.5f;

    // 地面のベースアルベドの手続き生成設定。シェーダー実装は
    // DepthOffsetTerrainLitMacroVariation.hlsl の ComputeProceduralGroundAlbedo。
    // 拾い物テクスチャの単純タイリング反復を消すため、ベース色自体を多スケールノイズで作る。
    // 新規SerializeFieldのためシーンに値が無く、ここのコード既定値が実行時に反映される。
    [Header("Terrain Procedural Ground Settings")]
    /// <summary>手続き色とテクスチャ由来色の混合率。0=テクスチャのみ, 1=完全手続き(反復ゼロ)。</summary>
    [SerializeField, Range(0f, 1f)] private float terrainProceduralGroundBlend = 0.85f;
    /// <summary>大局モザイク(草↔乾いた土のパッチ)の周期(m)。大きいほどパッチが広い。</summary>
    [SerializeField] private float terrainProceduralGroundScale = 40f;
    /// <summary>ベース色(草)。実アルベド相当(概ね0.2〜0.6)。</summary>
    [SerializeField] private Color terrainGroundColorA = new Color(0.38f, 0.47f, 0.26f, 1f);
    /// <summary>乾いた土のパッチ色。大局モザイクで差し込まれる。</summary>
    [SerializeField] private Color terrainGroundColorB = new Color(0.55f, 0.48f, 0.34f, 1f);
    /// <summary>明色(乾いた草のハイライト)。細かい斑で薄く混ぜてベタ塗り感を消す。</summary>
    [SerializeField] private Color terrainGroundColorC = new Color(0.62f, 0.66f, 0.45f, 1f);

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

    private static readonly int TerrainMacroVariationStrengthId = Shader.PropertyToID("_TerrainMacroVariationStrength");
    private static readonly int TerrainMacroVariationScaleId = Shader.PropertyToID("_TerrainMacroVariationScale");
    private static readonly int TerrainMacroVariationTintId = Shader.PropertyToID("_TerrainMacroVariationTint");

    private static readonly int TerrainColorGradeStrengthId = Shader.PropertyToID("_TerrainColorGradeStrength");
    private static readonly int TerrainColorGradeLowHeightId = Shader.PropertyToID("_TerrainColorGradeLowHeight");
    private static readonly int TerrainColorGradeHighHeightId = Shader.PropertyToID("_TerrainColorGradeHighHeight");
    private static readonly int TerrainLowColorId = Shader.PropertyToID("_TerrainLowColor");
    private static readonly int TerrainHighColorId = Shader.PropertyToID("_TerrainHighColor");
    private static readonly int TerrainSlopeColorId = Shader.PropertyToID("_TerrainSlopeColor");
    private static readonly int TerrainDetailNormalStrengthId = Shader.PropertyToID("_TerrainDetailNormalStrength");
    private static readonly int TerrainDetailNormalScaleId = Shader.PropertyToID("_TerrainDetailNormalScale");

    private static readonly int ProceduralGroundBlendId = Shader.PropertyToID("_ProceduralGroundBlend");
    private static readonly int ProceduralGroundScaleId = Shader.PropertyToID("_ProceduralGroundScale");
    private static readonly int TerrainGroundColorAId = Shader.PropertyToID("_TerrainGroundColorA");
    private static readonly int TerrainGroundColorBId = Shader.PropertyToID("_TerrainGroundColorB");
    private static readonly int TerrainGroundColorCId = Shader.PropertyToID("_TerrainGroundColorC");

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

    // === 開放済みセル管理 ===
    /// <summary>
    /// バックエンドで「開放（acquire）済み」と判定されたセルのインデックスセット。
    /// 未開放セルには dynamic な超近景地形データが存在しないため、
    /// LODティア決定時に Dynamic を要求しないよう制限するために使用する。
    /// </summary>
    private HashSet<Vector2Int> acquiredCells = new HashSet<Vector2Int>();
    /// <summary>
    /// 開放済みセルリストがバックエンドから取得済みかどうか。
    /// 未取得の場合は安全のため全セルを未開放とみなす。
    /// </summary>
    private bool acquiredCellsLoaded = false;

    // === 開放済みセル取得用のAPIレスポンス型 ===
    [Serializable]
    private class AcquiredCellsResponse
    {
        public List<AcquiredCellItem> cells;
    }

    [Serializable]
    private class AcquiredCellItem
    {
        public int cell_x;
        public int cell_z;
    }


    private bool settingsLoaded = false;
    private bool settingsLoading = false;

    // === 遠景ハイトマップキャッシュ（Terrainオブジェクトではなく生データ） ===
    private float[,] farTerrainHeights;

    // === セルごとのLODレベル追跡 ===
    /// <summary>
    /// 各セルに「現在適用されているデータ世代」。Far &lt; Near &lt; Dynamic の順に精細になる。
    /// タイル(Terrain GameObject)は全セル共通解像度(<see cref="TILE_RESOLUTION"/>)で一度だけ
    /// 生成・永続化され、ティア変更はこの世代のデータをストリーミング適用するだけで行う。
    /// ティアダウン(Dynamic→Near等)では何もしない(精細なデータをそのまま保持する)ため、
    /// この値は「これまでに適用した最高の世代」を表す。
    /// </summary>
    private Dictionary<Vector2Int, TerrainLodLevel> cellLodLevel = new Dictionary<Vector2Int, TerrainLodLevel>();

    // 各セルに現在適用されている(縁ブレンド後の)ハイトマップ。
    // CPU高さ配列の全量再書き込み(ReplayHeightsToCpuHeightmap)と、
    // 後から適用される隣接セルの縁ブレンド(BlendEdgesTowardAppliedNeighbors)の参照元になる。
    private Dictionary<Vector2Int, float[,]> originalHeightsCache = new Dictionary<Vector2Int, float[,]>();

    // タイル管理
    private Dictionary<Vector2Int, Terrain> terrainTiles = new Dictionary<Vector2Int, Terrain>();

    private enum TerrainLodLevel { None = 0, Far = 1, Near = 2, Dynamic = 3 }

    // ================================================================
    // ハイトマップの部分SetHeightsストリーミング基盤
    // ================================================================

    /// <summary>
    /// 全タイル共通のハイトマップ解像度。
    /// ティアごとに解像度の異なるタイルを作り直す方式(旧実装)では、ティア変更のたびに
    /// TerrainData生成+フルSetHeights+コライダーベイクの重い処理がメインスレッドに乗り、
    /// カメラ移動時のフレームヒッチの主因になっていた。全タイルを最高解像度で統一することで、
    /// (a) ティア変更が「既存TerrainDataへの部分SetHeightsストリーミング」だけになり、
    /// (b) 隣接タイルの解像度が常に一致して Terrain.SetNeighbors が全境界で機能する。
    /// メモリは1タイルあたり約1.6MB(TerrainData+CPUキャッシュ)×ロード済みセル数を消費する。
    /// </summary>
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
        /// <summary>適用するハイトマップ(TILE_RESOLUTION四方、正規化0..1)。適用完了後は originalHeightsCache が所有する。</summary>
        public float[,] heights;
        /// <summary>このデータの世代(Far/Near/Dynamic)。適用完了時に cellLodLevel へ記録される。</summary>
        public TerrainLodLevel generation;
        /// <summary>発行元のロードバッチ。適用開始前にキャンセル済みならリクエストごと破棄する(開始後は完走する)。</summary>
        public LoadBatch batch;
        /// <summary>処理が終了したか(成功・破棄を問わない)。発行元コルーチンはこれをポーリングして待つ。</summary>
        public bool done;
        /// <summary>タイルへ適用されたか。破棄・中断時は false。</summary>
        public bool succeeded;
    }

    /// <summary>セルごとの未開始リクエスト。同一セルに新しいリクエストが来たら未開始の旧リクエストは置き換える(最新が勝つ)。</summary>
    private readonly Dictionary<Vector2Int, HeightStreamRequest> pendingStreamRequests = new Dictionary<Vector2Int, HeightStreamRequest>();

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
    private Material ultraFarLandMat;     // 開放済み(通常色)サブメッシュ用
    private Material ultraFarLandMatDark; // 未開放(暗め)サブメッシュ用
    private static readonly int UltraFarBaseColorId = Shader.PropertyToID("_BaseColor");

    /// <summary>per-cellタイル用地形シェーダー(Custom/DepthOffsetTerrainLit)の乗算ティント。未開放セルの暗色化に使う。</summary>
    private static readonly int TerrainTintId = Shader.PropertyToID("_TerrainTint");

    // === ロード済みセルマスク(UltraFar沈み込み用) ===
    private static readonly int LoadedCellMaskId = Shader.PropertyToID("_LoadedCellMask");
    private static readonly int LoadedCellMaskParamsId = Shader.PropertyToID("_LoadedCellMaskParams");
    private static readonly int LoadedCellMaskTexelId = Shader.PropertyToID("_LoadedCellMaskTexel");
    private static readonly int FarSinkAmountId = Shader.PropertyToID("_FarSinkAmount");

    /// <summary>
    /// per-cellタイルがロード済み(=可視)のセルを示すR8マスクテクスチャ(1セル=1テクセル、バックエンドセル座標)。
    /// UltraFar 陸地メッシュのシェーダーが頂点ごとにこれを参照し、タイルのあるセルだけ地中に沈める。
    /// 毎フレーム terrainTiles の内容から再構築されるが、変化がなければGPUアップロードはスキップされる。
    /// </summary>
    private Texture2D loadedCellMaskTex;
    private byte[] loadedCellMaskBuf;
    private byte[] loadedCellMaskPrevBuf;

    // === 遅延破棄プール（アンロード用） ===
    /// <summary>
    /// セルがロード範囲外に出た時に、即座にDestroyせず非表示にしてプールする。
    /// 一定時間後に実際のDestroyを行うことで、カメラがすぐ戻った場合の再ロードとの競合による
    /// 破棄・再生成の無駄を防ぐ。
    ///
    /// なおLODティア変更は退避プールを使わない: タイルは差し替えず、既存TerrainDataへ
    /// 新しい世代のデータを部分SetHeightsでストリーミング適用する(in-place更新)。
    /// </summary>
    private Dictionary<Vector2Int, GameObject> pendingDestroyTerrains = new Dictionary<Vector2Int, GameObject>();
    /// <summary>
    /// pendingDestroyTerrains に登録された時刻を記録し、TERRAIN_DESTROY_DELAY 経過後に実際の破棄を行う。
    /// </summary>
    private Dictionary<Vector2Int, float> pendingDestroyTimestamps = new Dictionary<Vector2Int, float>();
    private const float TERRAIN_DESTROY_DELAY = 1.5f;

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

    // === Unityコールバック ===
    private void Awake()
    {
        terrainSize.x = CellSize;
        terrainSize.z = CellSize;

        // Utils.GetGroundHeight が FindAnyObjectByType を毎回実行するのを防ぐため、
        // インスタンスを静的キャッシュに登録する。
        // これにより数千回/フレームの GetGroundHeight 呼び出しが O(1) になる。
        Utils.RegisterTerrainManager(this);
        EnsureCellOverlayRenderer();
    }

    private void OnDestroy()
    {
        // TerrainManager が破棄された際にキャッシュ参照をクリアし、
        // 以降の GetGroundHeight でフォールバックパスを使用するようにする。
        if (Utils.CachedTerrainManager == this)
        {
            Utils.RegisterTerrainManager(null);
        }
        if (cellOverlayMeshObj != null)
        {
            Destroy(cellOverlayMeshObj);
            cellOverlayMeshObj = null;
            cellOverlayRenderer = null;
        }
    }

    private void Update()
    {
        UpdateCellOverlayIfActive();
        UpdateLoadedCellMask();

        // 遅延破棄プールの処理：TERRAIN_DESTROY_DELAY 経過後に実際のDestroyを行う。
        // 一度に全件処理してフレームあたりのオーバーヘッドを抑えつつ、
        // プールの蓄積によるメモリ圧迫を防ぐ。
        if (pendingDestroyTerrains.Count > 0)
        {
            float now = Time.time;
            List<Vector2Int> keysToDestroy = null;
            foreach (var kvp in pendingDestroyTerrains)
            {
                if (pendingDestroyTimestamps.TryGetValue(kvp.Key, out float deactivatedTime) &&
                    now - deactivatedTime >= TERRAIN_DESTROY_DELAY)
                {
                    if (keysToDestroy == null) keysToDestroy = new List<Vector2Int>();
                    keysToDestroy.Add(kvp.Key);
                }
            }
            if (keysToDestroy != null)
            {
                foreach (var key in keysToDestroy)
                {
                    if (pendingDestroyTerrains.TryGetValue(key, out GameObject go))
                    {
                        pendingDestroyTerrains.Remove(key);
                        pendingDestroyTimestamps.Remove(key);
                        DestroyTerrainTileObject(go);
                    }
                }
            }
        }
    }

    private void Start()
    {
        Shader.SetGlobalFloat("_ShowContour", 0f);

        // ゲーム開始時に自動的にセル境界表示（オーバーレイおよび枠線・ラベル）をONにする。
        // これにより、起動直後から解放済みのセル境界と座標ラベルが正しく表示される。
        IsUnlockOverlayActive = true;
        ShowCellBordersAndLabels = true;
        RefreshAllUnlockOverlays();
        RefreshAllCellOverlays();

        if (Camera.main != null)
        {
            Camera cam = Camera.main;
            float recommendedFarPlane = maxRenderDistance * 2f;
            if (cam.farClipPlane < recommendedFarPlane)
            {
                if (Application.isEditor && !Application.isPlaying)
                {
                    cam.farClipPlane = recommendedFarPlane;
                }
            }
        }
    }

    private void OnValidate()
    {
        terrainMacroVariationScale = Mathf.Max(1f, terrainMacroVariationScale);
        cliffTilingMeters = Mathf.Max(1f, cliffTilingMeters);
        cliffNoiseScale = Mathf.Max(1f, cliffNoiseScale);

        if (Application.isPlaying && terrainTiles != null)
        {
            RefreshAllTerrainMaterials();
        }
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
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  network/far: {swFar.ElapsedMilliseconds}ms ({rawBytes?.Length ?? 0} bytes)");

        int targetResolution = GetValidTerrainResolution(Mathf.Max(farRows, farCols));
        var swProc = System.Diagnostics.Stopwatch.StartNew();
        Task<float[,]> task = ProcessHeightmapAsync(rawBytes, farRows, farCols, targetResolution);
        yield return WaitTask(task);
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  ProcessHeightmapAsync/far: {swProc.ElapsedMilliseconds}ms");
        if (task.IsFaulted || task.Result == null) yield break;

        farTerrainHeights = task.Result;

        Debug.Log($"TerrainManager: Far terrain data cached. Grid={farRows}x{farCols}, " +
                  $"resolution={targetResolution}");
    }

    // ================================================================
    // 超近景（Dynamic）のロード
    // ================================================================

    private IEnumerator TryLoadDynamicTerrain(Vector2Int cellIndex, System.Action<bool> onComplete, LoadBatch batch = null)
    {
        // dynamic API はバックエンドのセルインデックスを1セルずつ指定する。
        // BBOX範囲指定は受け付けないため、Unityセルからバックエンドセルに変換して問い合わせる。
        Vector2Int backendCell = UnityCellToBackendCell(cellIndex);
        string url = $"{ApiClient.BASE_URL}/terrain/dynamic?cell_x={backendCell.x}&cell_z={backendCell.y}";

        byte[] rawBytes = null;
        int gridDim = 0;
        bool requestSucceeded = false;

        var swNet = System.Diagnostics.Stopwatch.StartNew();
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.timeout = 10;
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                gridDim = terrainSettings.cell_size / terrainSettings.dynamic_map_spacing + 1;
                rawBytes = req.downloadHandler.data;
                // 初回ロード時もversionをキャッシュしておくことで、次回以降の差分更新で全量更新を防ぐ。
                int version = ParseIntHeader(req, "X-Cell-Version", -1);
                if (version >= 0)
                {
                    dynamicCellVersions[cellIndex] = version;
                }
                requestSucceeded = true;
            }
            else if (req.responseCode == 404)
            {
                Debug.Log($"TerrainManager: Dynamic terrain not available for cell {cellIndex} (not acquired), falling back to near");
                onComplete(false);
                yield break;
            }
            else
            {
                Debug.LogError($"TerrainManager: Dynamic terrain request failed for {cellIndex} (backendCell={backendCell}): {req.error}");
                onComplete(false);
                yield break;
            }
        }
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  network/dynamic cell{cellIndex}: {swNet.ElapsedMilliseconds}ms ({rawBytes?.Length ?? 0} bytes)");

        if (!requestSucceeded || rawBytes == null)
        {
            onComplete(false);
            yield break;
        }

        // 通信待ちの間にバッチがキャンセルされていたら、以降の重い処理(補間・SetHeights)を行わない。
        if (batch != null && batch.IsCanceled)
        {
            onComplete(false);
            yield break;
        }

        // カクつき抑制のため、バイナリ変換・補間・平滑化をバックグラウンドスレッドで実行する。
        var swProc = System.Diagnostics.Stopwatch.StartNew();
        Task<float[,]> task = ProcessHeightmapAsync(rawBytes, gridDim, gridDim, TILE_RESOLUTION);
        yield return WaitTask(task);
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  ProcessHeightmapAsync/dynamic cell{cellIndex}: {swProc.ElapsedMilliseconds}ms");
        if (task.IsFaulted || task.Result == null)
        {
            onComplete(false);
            yield break;
        }

        // 適用中断(キャンセル)を成功と報告しないよう、完了可否は onComplete 経由で受け取る。
        // キャンセル判定・部分SetHeightsによる適用はストリーミングワーカーが行う。
        yield return ApplyHeightsStreamed(cellIndex, task.Result, TerrainLodLevel.Dynamic, batch, onComplete);
    }

    // ================================================================
    // 遠景データからのフォールバック
    // ================================================================

    private IEnumerator TryLoadFarResolutionCell(Vector2Int cellIndex, System.Action<bool> onComplete, LoadBatch batch = null)
    {
        if (farTerrainHeights == null)
        {
            onComplete(false);
            yield break;
        }

        float cellSize = CellSize;
        float mapWidth = terrainSettings.map_width;
        float mapHeight = terrainSettings.map_height;

        float cellMinX = cellIndex.x * cellSize;
        float cellMaxX = (cellIndex.x + 1) * cellSize;
        float cellMinZ = cellIndex.y * cellSize;
        float cellMaxZ = (cellIndex.y + 1) * cellSize;

        float farOriginX = -mapWidth / 2f;
        float farOriginZ = -mapHeight / 2f;

        // マップ外セル（標高ソースが存在しない領域）ではタイルを捏造しない。
        // 遠景マップからの切り出し(CatmullRomInterpolateArray)は配列範囲外インデックスを端に
        // クランプするため、マップ端の標高（海なら0m）が平らに引き伸ばされた偽のタイルになる。
        // 通常は LodConfig 側でマップ外セルを除外済みだが、防御として失敗を返し、
        // 呼び出し側のフォールバック連鎖を「タイル無し」で終わらせて海面板を海として見せる。
        if (cellMaxX <= farOriginX || cellMinX >= -farOriginX ||
            cellMaxZ <= farOriginZ || cellMinZ >= -farOriginZ)
        {
            onComplete(false);
            yield break;
        }

        float uMin = (cellMinX - farOriginX) / mapWidth;
        float uMax = (cellMaxX - farOriginX) / mapWidth;
        float vMin = (cellMinZ - farOriginZ) / mapHeight;
        float vMax = (cellMaxZ - farOriginZ) / mapHeight;

        int farCols = farTerrainHeights.GetLength(1);
        int farRows = farTerrainHeights.GetLength(0);

        float sx = uMin * (farCols - 1);
        float ex = uMax * (farCols - 1);
        float sy = vMin * (farRows - 1);
        float ey = vMax * (farRows - 1);

        Task<float[,]> task = Task.Run(() =>
        {
            float[,] cellHeights = new float[TILE_RESOLUTION, TILE_RESOLUTION];
            for (int y = 0; y < TILE_RESOLUTION; y++)
            {
                for (int x = 0; x < TILE_RESOLUTION; x++)
                {
                    float ny = y / (float)(TILE_RESOLUTION - 1);
                    float nx = x / (float)(TILE_RESOLUTION - 1);
                    float fy = sy + ny * (ey - sy);
                    float fx = sx + nx * (ex - sx);
                    // 遠景(65)から高解像度タイルへの引き伸ばしはCatmull-Romで平滑化し、
                    // 遠方地形のファセット(カクつき)を抑える。
                    cellHeights[y, x] = CatmullRomInterpolateArray(farTerrainHeights, fx, fy, farCols, farRows);
                }
            }
            return cellHeights;
        });

        yield return WaitTask(task);
        if (task.IsFaulted || task.Result == null)
        {
            onComplete(false);
            yield break;
        }

        // 適用中断(キャンセル)を成功と報告しないよう、完了可否は onComplete 経由で受け取る。
        yield return ApplyHeightsStreamed(cellIndex, task.Result, TerrainLodLevel.Far, batch, onComplete);
    }

    // ================================================================
    // 近景（Near）のロード
    // ================================================================

    private IEnumerator LoadNearTerrain(Vector2Int cellIndex, System.Action<bool> onComplete, LoadBatch batch = null)
    {
        CalculateCellBounds(cellIndex, out float startX, out float endX, out float startZ, out float endZ);

        string url = $"{ApiClient.BASE_URL}/terrain/near?start_x={startX}&start_z={startZ}&end_x={endX}&end_z={endZ}";

        byte[] rawBytes = null;
        int gridRows = 0;
        int gridCols = 0;

        var swNet = System.Diagnostics.Stopwatch.StartNew();
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.timeout = 15;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"TerrainManager: Near terrain request failed for {cellIndex}: {req.error}");
                onComplete(false);
                yield break;
            }

            // レスポンスヘッダーからグリッド寸法を取得
            gridRows = ParseIntHeader(req, "X-Grid-Rows", 0);
            gridCols = ParseIntHeader(req, "X-Grid-Cols", 0);

            if (gridRows == 0 || gridCols == 0)
            {
                // ヘッダーがない場合は設定値から計算
                int nearGridPerCell = terrainSettings.cell_size / terrainSettings.near_map_spacing + 1;
                gridRows = nearGridPerCell;
                gridCols = nearGridPerCell;
            }

            rawBytes = req.downloadHandler.data;
        }
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  network/near cell{cellIndex}: {swNet.ElapsedMilliseconds}ms ({rawBytes?.Length ?? 0} bytes)");

        // 通信待ちの間にバッチがキャンセルされていたら、以降の重い処理(補間・SetHeights)を行わない。
        if (batch != null && batch.IsCanceled)
        {
            onComplete(false);
            yield break;
        }

        // カクつき抑制のため、バイナリ変換・補間・平滑化をバックグラウンドスレッドで実行する。
        var swProc = System.Diagnostics.Stopwatch.StartNew();
        Task<float[,]> task = ProcessHeightmapAsync(rawBytes, gridRows, gridCols, TILE_RESOLUTION);
        yield return WaitTask(task);
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  ProcessHeightmapAsync/near cell{cellIndex}: {swProc.ElapsedMilliseconds}ms");
        if (task.IsFaulted || task.Result == null)
        {
            onComplete(false);
            yield break;
        }

        // 適用中断(キャンセル)を成功と報告しないよう、完了可否は onComplete 経由で受け取る。
        yield return ApplyHeightsStreamed(cellIndex, task.Result, TerrainLodLevel.Near, batch, onComplete);
    }

    // ================================================================
    // BaseApiDataManager 実装
    // ================================================================

    /// <summary>
    /// ロード済み全タイルのハイトマップをCPU高さ配列へ書き直す。
    /// HeightmapTextureManager.RebuildCpuHeightmap から全量再構築時に呼ばれる。
    /// TerrainData.GetHeights(GPU→CPUの同期readback)を使わず、CPU側に保持している
    /// originalHeightsCache(各セルに現在適用されている縁ブレンド後の配列=GPU上の内容と同一)
    /// から書き込むため高速かつ正確。
    /// </summary>
    public void ReplayHeightsToCpuHeightmap(HeightmapTextureManager htm)
    {
        if (htm == null) return;
        foreach (var kvp in originalHeightsCache)
        {
            if (!terrainTiles.ContainsKey(kvp.Key)) continue;
            htm.WriteCellHeights(kvp.Key, kvp.Value, terrainSize.y, SeaFloorDepthSpan);
        }
    }

    protected override void UnloadCell(Vector2Int cellIndex)
    {
        // 未開始のストリーミングリクエストが残っていれば破棄する(範囲外セルへの無駄な適用防止)。
        if (pendingStreamRequests.TryGetValue(cellIndex, out HeightStreamRequest pendingReq))
        {
            pendingReq.done = true;
            pendingReq.succeeded = false;
            pendingStreamRequests.Remove(cellIndex);
        }

        if (terrainTiles.ContainsKey(cellIndex))
        {
            Terrain t = terrainTiles[cellIndex];
            if (t != null)
            {
                t.gameObject.SetActive(false);
                pendingDestroyTerrains[cellIndex] = t.gameObject;
                pendingDestroyTimestamps[cellIndex] = Time.time;
            }
            terrainTiles.Remove(cellIndex);
            UpdateNeighborsForAdjacents(cellIndex);
        }
        // タイルの有無に関わらず記録を消す(全ティア失敗で None が記録されたままの
        // セルの辞書エントリが、アンロード後も無際限に残るのを防ぐ)。
        cellLodLevel.Remove(cellIndex);
        originalHeightsCache.Remove(cellIndex);
        DestroyCellOverlay(cellIndex);
    }

    public override void ClearAllCells()
    {
        // 進行中・待機中のストリーミングをすべて無効化する。
        // clearEpoch のインクリメントにより、適用途中のワーカーは次のバンド適用前に中断し、
        // 破棄済みTerrainDataへの書き込みやクリア後の孤児タイル登録を行わない。
        clearEpoch++;
        foreach (var kvp in pendingStreamRequests)
        {
            kvp.Value.done = true;
            kvp.Value.succeeded = false;
        }
        pendingStreamRequests.Clear();

        // セルごとのTerrain(TerrainDataアセットも併せて破棄する)
        foreach (var kvp in terrainTiles)
        {
            if (kvp.Value != null) DestroyTerrainTileObject(kvp.Value.gameObject);
        }
        terrainTiles.Clear();
        loadedCells.Clear();
        cellLodLevel.Clear();
        originalHeightsCache.Clear();
        dynamicCellVersions.Clear();

        // 遅延破棄プールのクリーンアップ
        foreach (var kvp in pendingDestroyTerrains)
        {
            DestroyTerrainTileObject(kvp.Value);
        }
        pendingDestroyTerrains.Clear();
        pendingDestroyTimestamps.Clear();

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

        // 設定キャッシュをクリア（session.reset対応）
        terrainSettings = null;
        settingsLoaded = false;

        // 開放済みセルキャッシュをクリア
        ClearAcquiredCells();

        // セル境界線・ラベルのクリア
        ClearAllCellOverlays();
    }

    // ================================================================
    // LOD対応セルロード
    // ================================================================

    private Coroutine loadLodCellsCoroutine;

    public override void LoadLodCells(LodCellSets cellSets, bool isForce)
    {
        if (loadLodCellsCoroutine != null)
        {
            StopCoroutine(loadLodCellsCoroutine);
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
        // StopCoroutine は RunParallelCoroutines が StartCoroutine で起動した子コルーチンを
        // 停止できないため、子コルーチンはこのバッチの IsCanceled を確認して協調的に停止する。
        if (currentLoadBatch == null || currentLoadBatch.IsCanceled)
        {
            currentLoadBatch = new LoadBatch(cellSets, isForce);
        }

        loadLodCellsCoroutine = StartCoroutine(LoadLodCellsCoroutine(cellSets, isForce, currentLoadBatch));
    }

    private IEnumerator LoadLodCellsCoroutine(LodCellSets cellSets, bool isForce, LoadBatch batch)
    {
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager LoadLodCells START isForce={isForce} far={cellSets.farCells?.Count ?? -1} near={cellSets.nearCells?.Count ?? -1} dynamic={cellSets.dynamicCells?.Count ?? -1}");

        var swStep = System.Diagnostics.Stopwatch.StartNew();
        yield return EnsureSettingsLoaded();
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  EnsureSettingsLoaded: {swStep.ElapsedMilliseconds}ms");

        swStep.Restart();
        if (farTerrainHeights == null)
            yield return LoadFarTerrainData();
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  LoadFarTerrainData: {swStep.ElapsedMilliseconds}ms (skipped={farTerrainHeights != null})");

        if (GameStateManager.Instance != null && !GameStateManager.Instance.IsGameStarted())
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "[LoadLodCells] yield break: game not started");
            yield break;
        }

        // 超遠景の陸地メッシュを1回だけ生成
        swStep.Restart();
        if (ultraFarLandObject == null)
            yield return BuildUltraFarTerrain();
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  BuildUltraFarTerrain: {swStep.ElapsedMilliseconds}ms (skipped={ultraFarLandObject != null})");

        var desiredCells = new HashSet<Vector2Int>(cellSets.farCells);

        // dynamic セルについて、ロード済みのものは version ベースで差分更新する。
        swStep.Restart();

        CellVersionsResponse versionsResponse = null;
        yield return ApiClient.Get<CellVersionsResponse>("/terrain/cell-versions", res => versionsResponse = res, err => {
            Debug.LogWarning($"TerrainManager: Failed to get cell-versions batch: {err}");
        });

        var batchVersions = new Dictionary<Vector2Int, int>();
        if (versionsResponse != null && versionsResponse.cells != null)
        {
            int offsetX = Mathf.RoundToInt((GetMapWidth() / 2.0f) / GetCellSize());
            int offsetY = Mathf.RoundToInt((GetMapHeight() / 2.0f) / GetCellSize());
            foreach (var info in versionsResponse.cells)
            {
                Vector2Int unityCell = new Vector2Int(info.cell_x - offsetX, info.cell_z - offsetY);
                batchVersions[unityCell] = info.version;
            }
        }

        var dynamicCellsToCheck = new List<Vector2Int>();
        foreach (var cell in cellSets.dynamicCells)
        {
            if (loadedCells.Contains(cell))
                dynamicCellsToCheck.Add(cell);
        }
        if (dynamicCellsToCheck.Count > 0)
        {
            var checkCoroutines = new IEnumerator[dynamicCellsToCheck.Count];
            for (int i = 0; i < dynamicCellsToCheck.Count; i++)
                checkCoroutines[i] = CheckAndRefreshDynamicTerrain(dynamicCellsToCheck[i], batchVersions, batch);
            yield return RunParallelCoroutines(checkCoroutines);
        }
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  CheckAndRefreshDynamic (all, parallel): {swStep.ElapsedMilliseconds}ms ({dynamicCellsToCheck.Count} cells)");

        // 不要セルをアンロード
        var toUnload = new List<Vector2Int>();
        foreach (var cell in loadedCells)
        {
            if (!desiredCells.Contains(cell))
                toUnload.Add(cell);
        }

        foreach (var cell in toUnload)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"[LoadLodCells] セルをアンロード {cell}");
            UnloadCell(cell);
            loadedCells.Remove(cell);
        }

        // 新規またはティア変更が必要なセルをロード
        // カメラ位置に応じて各セルに求められるLODティア(Far/Near/Dynamic)が決まる。
        // ここでは「まだロードされていないセル」「強制再読み込みが指示されたセル」「LODティアが変わったセル」を検出し、
        // 該当する場合のみバックエンドから地形データを取得してTerrainタイルを生成または差し替える。
        // 変更がないセルは何もせずスキップすることで、不要なGPUアップロードとメッシュ生成を回避する。
        //
        // Phase A: 各セルの分類と前処理（unload/退避）を行い、ロードが必要なセルを収集する。
        // Phase B: 収集したセルの LoadCellDataAtTier をすべて並行に発行し完了を待機する。
        //          HTTPリクエストとバックグラウンド処理が並行するため、逐次実行に比べて
        //          ネットワークRTT×セル数の直列待ちを max(RTT) 程度に削減できる。
        // Phase C: 並行ロード完了後、loadedCells への追加など後処理を行う。
        var cellsToLoad = new List<KeyValuePair<Vector2Int, TerrainLodLevel>>();
        var cellsToAddToLoaded = new HashSet<Vector2Int>();
        foreach (var cell in cellSets.farCells)
        {
            TerrainLodLevel desiredLod = DetermineTier(cell, cellSets);
            bool hasLod = cellLodLevel.TryGetValue(cell, out TerrainLodLevel current);

            if (!loadedCells.Contains(cell) || !terrainTiles.ContainsKey(cell))
            {
                UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"[LoadLodCells] -> 新規ロード {cell} tier={desiredLod}");
                cellsToLoad.Add(new KeyValuePair<Vector2Int, TerrainLodLevel>(cell, desiredLod));
                cellsToAddToLoaded.Add(cell);
            }
            else if (isForce)
            {
                UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"[LoadLodCells] -> 強制再ロード {cell} tier={desiredLod}");
                UnloadCell(cell);
                loadedCells.Remove(cell);
                cellsToLoad.Add(new KeyValuePair<Vector2Int, TerrainLodLevel>(cell, desiredLod));
                cellsToAddToLoaded.Add(cell);
            }
            else if (!hasLod || current < desiredLod)
            {
                // ティアアップ(より精細な世代が必要になった)のみデータを取得する。
                // タイル(GameObject)は差し替えず、取得したデータを既存タイルへ
                // 部分SetHeightsでストリーミング適用する(HeightStreamWorker)。
                // 適用中も既存タイルは可視のまま維持されるため、チラつきは起きない。
                // ティアダウン(current > desiredLod)は何もしない: 精細なデータを粗い
                // データへ戻すGPUアップロードは純粋な無駄で、タイルのメモリは解像度統一
                // により変わらないため、高精細のまま保持する(cellLodLevel は「これまでに
                // 適用した最高の世代」を表す)。
                UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"[LoadLodCells] -> ティアアップのため再ロード {cell} hasLod={hasLod} currentLod={current} desiredLod={desiredLod}");
                cellsToLoad.Add(new KeyValuePair<Vector2Int, TerrainLodLevel>(cell, desiredLod));
            }
            else
            {
                UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"[LoadLodCells] -> スキップ（変更なし） {cell}");
            }
        }

        if (cellsToLoad.Count > 0)
        {
            swStep.Restart();
            var loadCoroutines = new IEnumerator[cellsToLoad.Count];
            for (int i = 0; i < cellsToLoad.Count; i++)
                loadCoroutines[i] = LoadCellDataAtTier(cellsToLoad[i].Key, cellsToLoad[i].Value, batch);
            yield return RunParallelCoroutines(loadCoroutines);
            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  LoadCellDataAtTier parallel ({cellsToLoad.Count} cells): {swStep.ElapsedMilliseconds}ms");

            foreach (var cell in cellsToAddToLoaded)
                loadedCells.Add(cell);
        }

        // 隣接関係の更新は「このバッチで実際に(再)生成されたタイル」に限定する。
        // UpdateNeighbors(cell) は cell と上下左右4隣接の SetNeighbors を張り直すため、
        // (再)生成セルを起点に呼べば必要なリンクはすべて更新される。
        // アンロードされたセルは UnloadCell 内の UpdateNeighborsForAdjacents で処理済みであり、
        // 全ロード済みセルを毎バッチ走査する必要はない。
        swStep.Restart();
        foreach (var cell in updatedTerrainCellsThisLoad)
        {
            UpdateNeighbors(cell);
        }
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  UpdateNeighbors updated: {swStep.ElapsedMilliseconds}ms ({updatedTerrainCellsThisLoad.Count} cells)");

        // 境界の再縫合(旧実装の restitch)は不要になった。
        // 新しく適用されるハイトマップは、適用開始時に「既に画面へ出ている隣接タイルの縁」へ
        // 自分の縁を合わせる(BlendEdgesTowardAppliedNeighbors)。この「後から適用する側が
        // 合わせる」不変則により、適用済みタイルを後から触り直す必要がない。
        // 解像度も全タイルで統一されているため、SetNeighbors が全境界でLOD継ぎ目を処理する。

        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager LoadLodCells END: {swTotal.ElapsedMilliseconds}ms");

        // 全セルの地形ロードが完了した時点で、正確な標高データに基づく境界線の再構築を予約する。
        borderSegmentsDirty = true;

        // 全セルの地形ロードが完了したことを通知（同期機構用）。
        // 実際に更新されたセルの一覧をスナップショットして渡し、その後クリアする。
        // 受信側がコルーチンで複数フレームに分けて処理するため、メンバ集合への参照ではなく
        // 独立したスナップショットを渡す（処理中に次バッチで上書きされないようにするため）。
        var updatedSnapshot = new List<Vector2Int>(updatedTerrainCellsThisLoad);
        updatedTerrainCellsThisLoad.Clear();
        if (OnAllTerrainLodLoaded != null)
        {
            OnAllTerrainLodLoaded(updatedSnapshot);
        }
    }

    private IEnumerator RunParallelCoroutines(IEnumerator[] coroutines)
    {
        if (coroutines == null || coroutines.Length == 0) yield break;
        int remaining = coroutines.Length;
        for (int i = 0; i < coroutines.Length; i++)
            StartCoroutine(ParallelWrapper(coroutines[i], () => remaining--));
        while (remaining > 0)
            yield return null;
    }

    private IEnumerator ParallelWrapper(IEnumerator coroutine, System.Action onComplete)
    {
        yield return StartCoroutine(coroutine);
        onComplete?.Invoke();
    }

    private TerrainLodLevel DetermineTier(Vector2Int cell, LodCellSets cellSets)
    {
        if (cellSets.dynamicCells != null && cellSets.dynamicCells.Contains(cell))
            return TerrainLodLevel.Dynamic;
        if (cellSets.nearCells != null && cellSets.nearCells.Contains(cell))
            return TerrainLodLevel.Near;
        return TerrainLodLevel.Far;
    }

    private IEnumerator RefreshDynamicTerrain(Vector2Int cellIndex, LoadBatch batch = null)
    {
        // バッチキャンセル済みなら何もせず抜ける。
        // リフレッシュ中も既存タイルは terrainTiles に登録・可視のまま維持され、
        // 取得したデータはストリーミングワーカーが既存TerrainDataへin-place適用する(チラつき防止)。
        if (batch != null && batch.IsCanceled)
        {
            dynamicCellVersions.Remove(cellIndex);
            yield break;
        }

        bool loaded = false;
        yield return TryLoadDynamicTerrain(cellIndex, result => loaded = result, batch);

        if (!loaded)
        {
            // 失敗・キャンセル時はversionキャッシュを取り消す(ロールバック)。
            // 呼び出し元(CheckAndRefreshDynamicTerrain)や TryLoadDynamicTerrain は新versionを
            // 先行してキャッシュするため、ここで取り消さないと「versionは最新・タイルは古いまま」
            // となり、Dynamic→Dynamic ではティア差分も出ないので次バッチ以降も再取得されず、
            // 古い地形が恒久的に画面に残ってしまう。取り消せば次バッチのversion比較で再試行される。
            dynamicCellVersions.Remove(cellIndex);
        }
    }

    /// <summary>
    /// dynamicセルのversionを確認し、キャッシュと異なる場合のみ再描画する。
    /// レスポンスヘッダー X-Cell-Version からversionを取得する。
    /// dynamic API はバックエンドのセルインデックスを1セルずつ指定する必要がある。
    /// </summary>
    private IEnumerator CheckAndRefreshDynamicTerrain(Vector2Int cellIndex, Dictionary<Vector2Int, int> batchVersions, LoadBatch batch = null)
    {
        if (batch != null && batch.IsCanceled) yield break;

        int version = -1;
        bool hasVersion = false;

        // 1. まずバッチ取得したバージョン情報から引く
        if (batchVersions != null && batchVersions.TryGetValue(cellIndex, out version))
        {
            hasVersion = true;
        }

        // 2. バッチにない場合は個別HEADリクエストで確認する（フォールバック）
        if (!hasVersion)
        {
            Vector2Int backendCell = UnityCellToBackendCell(cellIndex);
            string url = $"{ApiClient.BASE_URL}/terrain/dynamic?cell_x={backendCell.x}&cell_z={backendCell.y}";

            var swCheck = System.Diagnostics.Stopwatch.StartNew();
            using (UnityWebRequest req = UnityWebRequest.Head(url))
            {
                req.timeout = 10;
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    version = ParseIntHeader(req, "X-Cell-Version", -1);
                    UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  CheckAndRefresh network/dynamic cell{cellIndex} (HEAD fallback): {swCheck.ElapsedMilliseconds}ms version={version}");
                    if (version >= 0)
                    {
                        hasVersion = true;
                    }
                }
                else if (req.responseCode == 404)
                {
                    // 未開放セルなど: キャッシュから削除
                    dynamicCellVersions.Remove(cellIndex);
                    yield break;
                }
                else
                {
                    Debug.LogError($"TerrainManager: Dynamic terrain request failed for {cellIndex} (backendCell={backendCell}): {req.error}");
                    yield break;
                }
            }
        }

        // HEADフォールバックの通信中にバッチがキャンセルされた場合はここで抜ける
        // (RefreshDynamicTerrain 冒頭のチェックと重複するが、意図を明示するため残す)。
        if (batch != null && batch.IsCanceled) yield break;

        if (hasVersion && version >= 0)
        {
            if (!dynamicCellVersions.TryGetValue(cellIndex, out int cachedVersion) || cachedVersion != version)
            {
                UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"TerrainManager: Cell {cellIndex} の再描画を開始 (version {cachedVersion} -> {version})");
                dynamicCellVersions[cellIndex] = version;
                yield return RefreshDynamicTerrain(cellIndex, batch);
            }
            else
            {
                UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  CheckAndRefresh cell{cellIndex}: version unchanged, skip");
            }
        }
        else
        {
            // version情報がどうしても得られない場合は従来通り安全のため再描画
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"TerrainManager: Cell {cellIndex} の再描画を開始 (versionキャッシュなしフォールバック)");
            yield return RefreshDynamicTerrain(cellIndex, batch);
        }
    }

    /// <summary>
    /// 指定セルが既に <paramref name="tier"/> 以上の世代のデータを適用済みのタイルを持つかどうか。
    /// ティアのフォールバック連鎖(Dynamic→Near→Far)の各段で「既にそれ以上の品質がある」場合に
    /// 無駄な再取得・再適用を省くための判定。
    /// </summary>
    private bool HasAppliedGeneration(Vector2Int cellIndex, TerrainLodLevel tier)
    {
        return terrainTiles.ContainsKey(cellIndex)
            && cellLodLevel.TryGetValue(cellIndex, out TerrainLodLevel applied)
            && applied >= tier;
    }

    private IEnumerator LoadCellDataAtTier(Vector2Int cellIndex, TerrainLodLevel tier, LoadBatch batch = null)
    {
        // バッチキャンセル済みなら何もせず抜ける。
        // データ適用中も既存タイルは terrainTiles に登録・可視のまま維持されるため、
        // キャンセル・失敗時の「復元」処理は不要(旧タイルは最初から無傷)。
        if (batch != null && batch.IsCanceled) yield break;

        // 未開放セルに対しては Dynamic を要求しない。
        // LOD計算段階で既に Near に抑制されているはずだが、
        // 明示的なチェックを二重に設けることで不整合を防ぐ。
        if (tier == TerrainLodLevel.Dynamic && !IsCellAcquired(cellIndex))
        {
            Debug.Log($"TerrainManager: セル {cellIndex} は未開放のため, Dynamic -> Near にフォールバック");
            tier = TerrainLodLevel.Near;
        }

        // 適用済み世代(cellLodLevel)の記録はストリーミングワーカーが行う。
        // フォールバックの各段で HasAppliedGeneration を確認し、既に同等以上の世代が
        // 適用済みなら何もしない(例: Dynamic の取得に失敗したセルが Near 適用済みの場合、
        // Near データを再取得して同じものを再適用するのは無駄)。
        if (tier == TerrainLodLevel.Dynamic)
        {
            bool loaded = false;
            yield return TryLoadDynamicTerrain(cellIndex, result => loaded = result, batch);
            if (loaded) yield break;
            // キャンセルによる失敗をティアのフォールバックと誤認して
            // 次ティアのAPIコールを発行しないよう、ここでバッチを確認して抜ける。
            // 旧タイルは可視・登録のまま残っているため、後始末は不要。
            if (batch != null && batch.IsCanceled) yield break;
            tier = TerrainLodLevel.Near;
        }

        if (tier == TerrainLodLevel.Near)
        {
            if (HasAppliedGeneration(cellIndex, TerrainLodLevel.Near)) yield break;
            bool loaded = false;
            yield return LoadNearTerrain(cellIndex, result => loaded = result, batch);
            if (loaded) yield break;
            if (batch != null && batch.IsCanceled) yield break;
            tier = TerrainLodLevel.Far;
        }

        {
            if (HasAppliedGeneration(cellIndex, TerrainLodLevel.Far)) yield break;
            bool loaded = false;
            yield return TryLoadFarResolutionCell(cellIndex, result => loaded = result, batch);
            if (loaded) yield break;
            if (batch != null && batch.IsCanceled) yield break;
        }

        // 全ティアの取得に失敗した(非キャンセル)。既存タイルがある場合は表示・ティアともそのまま
        // 維持し、次バッチのティア差分判定で自然に再試行させる。タイルが無い場合のみ None を
        // 記録し、「ロード試行済みだがデータ無し」を表す。
        Debug.LogWarning($"TerrainManager: {cellIndex} の地形データが存在しません");
        if (!terrainTiles.ContainsKey(cellIndex))
        {
            cellLodLevel[cellIndex] = TerrainLodLevel.None;
        }
    }

    /// <summary>
    /// 超遠景(UltraFar)の陸地シルエットメッシュを生成する。
    ///
    /// Unity Terrain(マップ全体1枚の粗いハイトマップ)ではなく、バックエンドが near_map から
    /// 精密に抽出した海岸線ポリゴン(<see cref="CoastlineStore"/>)を直接三角形分割してメッシュ化する。
    /// 海(陸地外)はメッシュを作らないため、海は既存の海面板がそのまま担い、粗いハイトマップが海面板を
    /// 突き抜ける問題や Z-fighting が原理的に発生しない。起伏は far_map 高さを頂点に反映して粗く表現する。
    ///
    /// 未開放セルの濃色表示は「開放/未開放」の2サブメッシュに分け、未開放サブメッシュのマテリアル色を
    /// 切り替えて表現する(<see cref="RefreshUltraFarLandOverlay"/>)。
    ///
    /// 重い符号付き距離計算を含むメッシュデータ構築はバックグラウンドスレッドで行い、Mesh の組み立てだけ
    /// メインスレッドで行う。
    /// </summary>
    private IEnumerator BuildUltraFarTerrain()
    {
        if (farTerrainHeights == null || terrainSettings == null) yield break;

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

        // 開放済み判定。スレッド安全なスナップショットを参照する。未ロード時は全て開放扱い(濃色表示なし)。
        HashSet<Vector2Int> acquiredSnapshot = GetAcquiredCells();
        bool acquiredKnown = acquiredCellsLoaded;
        float cs = CellSize;
        Func<float, float, bool> isAcquired = (x, z) =>
        {
            if (!acquiredKnown) return true;
            int ux = Mathf.FloorToInt(x / cs);
            int uz = Mathf.FloorToInt(z / cs);
            return acquiredSnapshot.Contains(new Vector2Int(ux, uz));
        };

        float gridStep = ultraFarGridStep;
        float heightOffset = ultraFarHeightOffset;

        Task<UltraFarLandMeshBuilder.MeshData> task = Task.Run(() =>
            UltraFarLandMeshBuilder.Build(mapWidth, mapHeight, gridStep, heightOffset, sampleHeight, isAcquired));
        yield return WaitTask(task);
        if (task.IsFaulted || task.Result.vertices == null) yield break;

        UltraFarLandMeshBuilder.MeshData md = task.Result;
        if (md.vertices.Count < 3) yield break; // 陸地が無い(全域海)など。

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
        mesh.subMeshCount = 2;
        mesh.SetTriangles(md.trianglesAcquired, 0);   // サブメッシュ0 = 開放済み(通常色)
        mesh.SetTriangles(md.trianglesUnacquired, 1); // サブメッシュ1 = 未開放(濃色表示対象)
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
        mr.sharedMaterials = new Material[] { ultraFarLandMat, ultraFarLandMatDark };

        go.AddComponent<VisualTerrainObject>();

        ultraFarLandObject = go;
        RefreshUltraFarLandOverlay();

        Debug.Log($"TerrainManager: Ultra-far land mesh built. verts={md.vertices.Count}, " +
                  $"acquiredTris={md.trianglesAcquired.Count / 3}, unacquiredTris={md.trianglesUnacquired.Count / 3}");
    }

    /// <summary>
    /// UltraFar 陸地の地面テクスチャ(per-cellタイルと同じもの)とタイリングを取得する。
    /// customTerrainLayers[0] があればそれを、なければ defaultDiffuseTexture を使う。
    /// </summary>
    private Texture2D GetUltraFarDiffuseTexture(out Vector2 tileSize)
    {
        Texture2D diffuseTex = defaultDiffuseTexture;
        tileSize = defaultTileSize;

        if (customTerrainLayers != null && customTerrainLayers.Length > 0 && customTerrainLayers[0] != null)
        {
            diffuseTex = customTerrainLayers[0].diffuseTexture;
            tileSize = customTerrainLayers[0].tileSize;
        }

        if (tileSize == Vector2.zero)
        {
            tileSize = new Vector2(terrainSize.x, terrainSize.z);
        }
        return diffuseTex;
    }

    /// <summary>
    /// UltraFar 陸地メッシュ用の2マテリアル(開放済み=通常/未開放=暗め)を必要に応じて生成する。
    /// シェーダーは Custom/DepthOffsetSimpleLitFar(Offset 4,4)で per-cell 地形・海面より奥に描画する。
    /// per-cell タイルと同じ地面テクスチャを、頂点のワールドXZ座標をUVとして同じタイリングで貼る。
    /// 法線マップは設定しない(SimpleLit の _BumpMap にカラーテクスチャが流用されると不正な法線で
    /// 陰影が暗く濁るため。遠景専用なので法線ディテールは不要)。
    /// 色の設定は <see cref="RefreshUltraFarLandOverlay"/>、沈み込みマスクは
    /// <see cref="UpdateLoadedCellMask"/> が担う。
    /// </summary>
    private void EnsureUltraFarLandMaterials()
    {
        Shader sh = ultraFarLandShader;
        if (sh == null) sh = Shader.Find("Custom/DepthOffsetSimpleLitFar");
        if (sh == null)
        {
            Debug.LogWarning("TerrainManager: Custom/DepthOffsetSimpleLitFar shader not found; falling back to URP Simple Lit");
            sh = Shader.Find("Universal Render Pipeline/Simple Lit");
        }

        if (ultraFarLandMat == null) ultraFarLandMat = new Material(sh) { name = "UltraFarLand" };
        else ultraFarLandMat.shader = sh;

        if (ultraFarLandMatDark == null) ultraFarLandMatDark = new Material(sh) { name = "UltraFarLandDark" };
        else ultraFarLandMatDark.shader = sh;

        // per-cell タイルと同じ地面テクスチャ・タイリングを適用する(UVはワールドXZ座標)。
        Texture2D diffuseTex = GetUltraFarDiffuseTexture(out Vector2 tileSize);
        if (diffuseTex != null)
        {
            ultraFarLandMat.SetTexture("_BaseMap", diffuseTex);
            ultraFarLandMatDark.SetTexture("_BaseMap", diffuseTex);

            Vector4 tilingOffset = new Vector4(1f / tileSize.x, 1f / tileSize.y, 0f, 0f);
            ultraFarLandMat.SetVector("_BaseMap_ST", tilingOffset);
            ultraFarLandMatDark.SetVector("_BaseMap_ST", tilingOffset);
        }

        // ロード済みセルでの沈み込み量。判定マスクは UpdateLoadedCellMask が毎フレーム供給する。
        ultraFarLandMat.SetFloat(FarSinkAmountId, ultraFarSinkAmount);
        ultraFarLandMatDark.SetFloat(FarSinkAmountId, ultraFarSinkAmount);
    }

    /// <summary>
    /// 未開放セルの暗色表示状態を UltraFar 陸地メッシュに反映する。
    /// 開放済みサブメッシュは常に通常色(テクスチャ素の色)。未開放サブメッシュは IsUnlockOverlayActive
    /// のとき unacquiredTint を乗算して「同じ地面テクスチャを少し暗くした」見た目、無効時は通常色にする。
    /// </summary>
    private void RefreshUltraFarLandOverlay()
    {
        if (ultraFarLandMat == null || ultraFarLandMatDark == null) return;

        // テクスチャありなら白(素のテクスチャ色)、なければフラット色フォールバック。
        Color baseColor = GetUltraFarDiffuseTexture(out _) != null ? Color.white : ultraFarLandColor;

        ultraFarLandMat.SetColor(UltraFarBaseColorId, baseColor);
        ultraFarLandMatDark.SetColor(UltraFarBaseColorId,
            IsUnlockOverlayActive ? baseColor * unacquiredTint : baseColor);
    }

    /// <summary>
    /// ロード済みセルマスク(UltraFar沈み込み用)を terrainTiles の現状から再構築する。毎フレーム呼ばれる。
    ///
    /// UltraFar 陸地メッシュは per-cell タイル(Far/Near/Dynamic)と物理的に重なっており、特に近景
    /// (near_map 由来)タイルとは起伏の激しい場所で数十mの高低差が生じてタイルの上に突き出る。
    /// これを防ぐため「タイルが実際にロードされている(可視の)セル」を1セル=1テクセルのR8マスクにして
    /// UltraFar シェーダーへ渡し、頂点シェーダーがマスクの立っているセルの頂点を ultraFarSinkAmount(50m)
    /// 沈める。タイルの無いセルは沈めないので、カメラ移動やロード遅延で海色の穴が開くことはない
    /// (タイル未ロードの間は UltraFar がそのまま見えるだけ)。
    ///
    /// テクセル座標はバックエンドセル座標(0-based)。シェーダー側は floor(worldXZ/cellSize)+offset で
    /// 同じ変換を行う(UnityCellToBackendCell と同一の整数オフセット)。
    /// マスク内容(最大でも数十×数十バイト)は毎フレーム作り直すが、前回と同一ならGPUアップロードしない。
    /// </summary>
    private void UpdateLoadedCellMask()
    {
        // マスクの利用者はUltraFarマテリアルのみ。設定未取得・マテリアル未生成なら何もしない。
        if (terrainSettings == null || ultraFarLandMat == null || ultraFarLandMatDark == null) return;

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
            ultraFarLandMatDark.SetTexture(LoadedCellMaskId, loadedCellMaskTex);
            ultraFarLandMatDark.SetVector(LoadedCellMaskParamsId, maskParams);
            ultraFarLandMatDark.SetVector(LoadedCellMaskTexelId, maskTexel);
        }

        // 現在ロード済み(可視)のタイルをマスクに書き込む。
        // ティア変更・リフレッシュ中の旧タイルは terrainTiles に登録・可視のまま維持されるため
        // 自然にマスクに含まれ、差し替え完了までUltraFarメッシュの浮き上がりは起きない。
        // pendingDestroyTerrains は非表示退避なので含めない。
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
    /// ハイトマップの適用リクエストを発行し、完了(成功・破棄いずれか)まで待機する。
    /// 実際の適用は <see cref="HeightStreamWorker"/> が視野優先の順序・フレーム予算制で
    /// 直列に行う。同一セルの未開始リクエストが既にある場合は最新データで置き換える
    /// (置き換えられた側は「破棄」として完了する)。
    /// </summary>
    private IEnumerator ApplyHeightsStreamed(Vector2Int cellIndex, float[,] heights, TerrainLodLevel generation, LoadBatch batch, System.Action<bool> onComplete)
    {
        var request = new HeightStreamRequest
        {
            heights = heights,
            generation = generation,
            batch = batch,
        };

        if (pendingStreamRequests.TryGetValue(cellIndex, out HeightStreamRequest old))
        {
            old.done = true;
            old.succeeded = false;
        }
        pendingStreamRequests[cellIndex] = request;
        EnsureStreamWorker();

        while (!request.done) yield return null;
        onComplete?.Invoke(request.succeeded);
    }

    private void EnsureStreamWorker()
    {
        if (streamWorkerCoroutine == null)
        {
            streamWorkerCoroutine = StartCoroutine(HeightStreamWorker());
        }
    }

    /// <summary>
    /// ストリーミング適用の1フレームあたりの予算(ms)。
    /// </summary>
    private float GetStreamFrameBudgetMs()
    {
        return PerformanceConfig.FrameBudgetMs;
    }

    /// <summary>
    /// 待機中リクエストから「次に適用すべきセル」を選ぶ。
    /// カメラ視野内(+マージン)のセルを最優先し、次いでカメラに近い順。
    /// リクエスト数はロード対象セル数(高々数十)なので毎回の線形走査で十分軽い。
    /// </summary>
    private Vector2Int PickBestStreamCell()
    {
        Camera cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        Vector3 fwd = cam != null ? cam.transform.forward : Vector3.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        else fwd.Normalize();

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
    /// ハイトマップ適用の常駐ワーカー。待機中リクエストを視野優先で1セルずつ取り出し、
    /// バンド(<see cref="STREAM_BAND_ROWS"/>行)単位の部分SetHeightsでフレーム予算内に分割適用する。
    ///
    /// - タイルが無いセルは、切り離された(どのGameObjectにも付いていない)TerrainDataへ
    ///   ストリーミングし、全バンド適用後に <see cref="AttachTerrainTile"/> で一括GameObject化する。
    ///   これによりストリーミング途中の中途半端な地形が画面に出ず、コライダーの初期ベイクも
    ///   完成データに対して1回だけ・専有フレームで行われる。CPU高さ配列への書き込みは
    ///   attach後に行う(不可視のうちに書くと、中断時に「地形が無いのに高さだけ残る」
    ///   ゴーストが発生し、建物・道路が見えない地形の上に浮いてしまうため)。
    /// - タイルが既にあるセル(ティアアップ・dynamic差分更新)は、可視のままの既存TerrainDataへ
    ///   直接バンドを適用する(in-place更新)。CPU高さ配列も同じバンドを同期更新する。
    ///   数フレームかけて新旧データの境目がタイル内を走査していくが、
    ///   同じ地形の精細度違いなので視覚的な破綻は軽微で一時的。
    /// - キャンセル(LoadBatch)は「開始前のリクエストの破棄」のみ。適用を開始したセルは原則完走する
    ///   (途中放棄すると裂けたハイトマップが画面に残るため。1セル分の残作業は高々9バンドで有界)。
    ///   例外は不可視の新規タイル(attach前ならいつでも安全に破棄できる)。
    /// - 例外が起きてもワーカー自体は死なないよう、同期処理ブロックを個別に捕捉する。
    ///   万一ワーカーが停止した場合も finally で登録を解除し、次のリクエスト発行時に再起動される。
    /// </summary>
    private IEnumerator HeightStreamWorker()
    {
        // StartCoroutine は最初の yield まで呼び出し元のスタックで同期実行されるため、
        // まず1フレーム譲り、EnsureStreamWorker 側の streamWorkerCoroutine 代入を確定させてから
        // 処理を始める(同期実行中にイベント経由で再入すると二重起動しうるため)。
        yield return null;

        var budgetWatch = new System.Diagnostics.Stopwatch();
        HeightStreamRequest activeReq = null;

        try
        {
            while (true)
            {
                if (pendingStreamRequests.Count == 0)
                {
                    yield return null;
                    continue;
                }

                Vector2Int cell = PickBestStreamCell();
                HeightStreamRequest req = pendingStreamRequests[cell];
                pendingStreamRequests.Remove(cell);
                activeReq = req;

                // 開始前のキャンセルのみ尊重して破棄する。
                if (req.batch != null && req.batch.IsCanceled)
                {
                    req.done = true;
                    activeReq = null;
                    continue;
                }

                int epochAtStart = clearEpoch;
                int res = req.heights.GetLength(0);
                var swCell = System.Diagnostics.Stopwatch.StartNew();
                bool aborted = false;

                // 適用前に、既に画面に出ている隣接タイルの縁へ自分の縁を合わせる(CPU処理)。
                // 「後から適用する側が合わせる」不変則により、適用済みタイルへの再縫合が不要になる。
                try
                {
                    BlendEdgesTowardAppliedNeighbors(cell, req.heights, res, req.generation);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"TerrainManager: 縁ブレンドで例外 cell{cell}: {ex}");
                    aborted = true;
                }

                // 適用先のTerrainDataを決める。解像度が異なる既存タイル(想定外の遺物)は新規扱いで差し替える。
                bool isNewTile = !terrainTiles.TryGetValue(cell, out Terrain tile) || tile == null
                    || tile.terrainData == null || tile.terrainData.heightmapResolution != res;
                TerrainData tData = null;
                bool attachedTile = false;
                if (!aborted)
                {
                    if (isNewTile)
                    {
                        tData = new TerrainData();
                        tData.heightmapResolution = res;
                        // レイヤーは1枚のみでSplatmapに情報を持たせないため、コントロールマップの解像度は
                        // 最小限にしてタイルごとのメモリを節約する(デフォルトは512)。
                        tData.alphamapResolution = 16;
                        tData.size = terrainSize;
                    }
                    else
                    {
                        tData = tile.terrainData;
                    }
                }

                budgetWatch.Restart();
                for (int rowStart = 0; !aborted && rowStart < res; rowStart += STREAM_BAND_ROWS)
                {
                    // フレームを跨いだ間に全クリアやタイル破棄が起きていたら中断する
                    // (破棄済みTerrainDataへのSetHeightsは例外になる)。
                    if (clearEpoch != epochAtStart || (!isNewTile && (tile == null || tile.terrainData == null)))
                    {
                        aborted = true;
                        break;
                    }

                    int rows = Mathf.Min(STREAM_BAND_ROWS, res - rowStart);
                    try
                    {
                        ApplyHeightBand(tData, req.heights, res, rowStart, rows);

                        // 可視(in-place)タイルのみ、建物・道路が参照するCPU高さ配列も
                        // 同じバンド分だけ同期更新する。新規タイルはattach後にまとめて書く。
                        if (!isNewTile && HeightmapTextureManager.Instance != null)
                        {
                            HeightmapTextureManager.Instance.WriteCellHeightRows(cell, req.heights, terrainSize.y, SeaFloorDepthSpan, rowStart, rows);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"TerrainManager: ハイトマップ適用で例外 cell{cell}: {ex}");
                        aborted = true;
                        break;
                    }

                    if (budgetWatch.Elapsed.TotalMilliseconds > GetStreamFrameBudgetMs())
                    {
                        yield return null;
                        budgetWatch.Restart();
                    }
                }

                if (!aborted && isNewTile)
                {
                    // GameObject化はコライダーの初期ベイクを含むため専有フレームを与える
                    // (直前までのバンド適用と同一フレームに重ねない)。
                    yield return null;
                    // フレームを跨いだ後に全クリア・バッチキャンセルが確定していたら、切り離された
                    // TerrainDataを破棄して終わる。キャンセル済みバッチの残骸をattachすると、
                    // ロード範囲外のセルにどのプールにも属さない孤児タイルが登録されてしまう
                    // (切り離されたTerrainDataは不可視なので、ここで捨てても画面には何も起きない)。
                    if (clearEpoch != epochAtStart || (req.batch != null && req.batch.IsCanceled))
                    {
                        aborted = true;
                    }
                    else
                    {
                        try
                        {
                            AttachTerrainTile(cell, tData);
                            attachedTile = true;
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"TerrainManager: タイルGameObject化で例外 cell{cell}: {ex}");
                            aborted = true;
                        }
                    }

                    // attach(可視化)が済んだので、CPU高さ配列へ全行を書き込む。
                    // 行スライスで予算分割し、1フレームに集中させない。
                    if (!aborted && HeightmapTextureManager.Instance != null)
                    {
                        budgetWatch.Restart();
                        for (int rowStart = 0; rowStart < res; rowStart += STREAM_BAND_ROWS)
                        {
                            if (clearEpoch != epochAtStart) break;
                            int rows = Mathf.Min(STREAM_BAND_ROWS, res - rowStart);
                            HeightmapTextureManager.Instance.WriteCellHeightRows(cell, req.heights, terrainSize.y, SeaFloorDepthSpan, rowStart, rows);
                            if (budgetWatch.Elapsed.TotalMilliseconds > GetStreamFrameBudgetMs())
                            {
                                yield return null;
                                budgetWatch.Restart();
                            }
                        }
                    }
                }
                else if (!aborted)
                {
                    // in-place適用中にアンロード・差し替えでタイルが登録から外れていた場合、
                    // 適用先はもう画面に出ていないゾンビタイルなので、完了処理(キャッシュ更新・
                    // イベント発火)を行わない(古いデータでキャッシュを汚さないため)。
                    if (!terrainTiles.TryGetValue(cell, out Terrain currentTile) || currentTile != tile)
                    {
                        aborted = true;
                    }
                }

                if (aborted)
                {
                    // 新規タイルでattach前なら、切り離されたTerrainDataを破棄する
                    // (attach済みならタイル側の破棄経路が面倒を見る)。
                    if (isNewTile && !attachedTile && tData != null)
                    {
                        Destroy(tData);
                    }
                    req.done = true;
                    activeReq = null;
                    continue;
                }

                // 適用完了。縁ブレンド後のハイトマップをこのセルの現行データとしてキャッシュする
                // (以降の隣接セルの縁ブレンドとCPU高さ配列の全量再構築の参照元になる)。
                // 購読側(建物・道路の追従)の例外でワーカーを死なせないよう捕捉する。
                try
                {
                    originalHeightsCache[cell] = req.heights;
                    cellLodLevel[cell] = req.generation;

                    // このバッチで実際にデータが適用されたタイルを記録する。
                    // OnAllTerrainLodLoaded の受信側はこの集合だけを建物・道路の追従更新対象とする。
                    updatedTerrainCellsThisLoad.Add(cell);

                    if (OnTerrainTileReady != null && terrainTiles.TryGetValue(cell, out Terrain readyTile) && readyTile != null)
                    {
                        OnTerrainTileReady(cell, readyTile);
                    }
                    RefreshCellOverlay(cell);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"TerrainManager: タイル適用完了処理で例外 cell{cell}: {ex}");
                }

                req.succeeded = true;
                req.done = true;
                activeReq = null;

                UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  StreamApply cell{cell} gen={req.generation} new={isNewTile}: {swCell.ElapsedMilliseconds}ms");

                // 1セル完了(attach・イベント発火)ごとにフレームを譲り、処理の連続で1フレームが膨らむのを防ぐ。
                yield return null;
            }
        }
        finally
        {
            // ワーカーが例外・停止で終了した場合の自己修復。
            // 処理中のリクエストを完了扱いにして待機側の無限ポーリングを解放し、
            // 登録を解除して次の ApplyHeightsStreamed(EnsureStreamWorker) に再起動させる。
            if (activeReq != null)
            {
                activeReq.done = true;
            }
            streamWorkerCoroutine = null;
        }
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
        tData.SetHeights(0, rowStart, strip);
    }

    /// <summary>
    /// ストリーミング適用が完了したTerrainDataをGameObject化してセルに登録する。
    /// そのセルに旧タイルがあれば同一フレームで差し替え破棄する(チラつき防止)。
    /// TerrainCollider への terrainData 代入はコライダーの初期ベイクを伴い重いため、
    /// 呼び出し側(HeightStreamWorker)はこのメソッドに専有フレームを与えること。
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
        terrainComp.detailObjectDistance = 1000f;
        terrainComp.detailObjectDensity = 1f;
        terrainComp.treeBillboardDistance = 1000f;
        terrainComp.treeDistance = 1000f;
        terrainComp.treeCrossFadeLength = 200f;
        // ベースマップ切り替えを実質無効化する。デフォルト(1000m)のままだと、遠方のタイルが
        // カスタムシェーダーではなくベースマップ用シェーダーで描画され、_TerrainTint(未開放の暗色化)や
        // マクロバリエーション・深度Offsetが遠方タイルに適用されなくなるため。
        terrainComp.basemapDistance = 100000f;

        // 旧→新タイルの差し替え(チラつき防止の要)。
        // 登録と破棄を同一フレームで行い(この間に yield はない)、Unity の Destroy は当該フレームの
        // 描画前に反映されるため、新旧タイルが同時に描画されるフレームも、
        // タイルが1枚も無いフレームも存在しない。
        // (通常、既存タイルへのストリーミングはin-place更新なのでここへは来ないが、
        //  解像度不一致の遺物タイルを差し替えるケースの防御として残す)
        terrainTiles.TryGetValue(cellIndex, out Terrain prevTerrain);
        terrainTiles[cellIndex] = terrainComp;

        ApplyTerrainMaterial(terrainComp, tData);
        ApplyUnlockOverlayToTerrain(cellIndex, terrainComp);

        TerrainCollider col = terrainGO.GetComponent<TerrainCollider>();
        if (col != null) col.terrainData = tData;

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
        // アンロード直後に同セルが再ロードされた場合(UnloadCell → 再ロードの競合)、
        // 遅延破棄プールに旧タイルの残骸が残っていることがあるため、ここで併せて破棄する。
        // 同一GameObjectを二重にDestroyしないよう同一性をガードする。
        if (pendingDestroyTerrains.Remove(cellIndex, out GameObject pendingGO) && pendingGO != null)
        {
            pendingDestroyTimestamps.Remove(cellIndex);
            if (pendingGO != oldTerrainToDestroy && pendingGO != terrainGO)
            {
                DestroyTerrainTileObject(pendingGO);
            }
        }
        if (oldTerrainToDestroy != null)
        {
            DestroyTerrainTileObject(oldTerrainToDestroy);
        }

        // 新規タイルの登場で隣接タイルとのLOD継ぎ目を繋ぎ直す。
        // 全タイルが同一解像度のため、SetNeighbors は常に全境界で機能する。
        UpdateNeighbors(cellIndex);
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

    /// <summary>
    /// Task の完了をコルーチン上で待機する。ただの `while (!task.IsCompleted) yield return null` だが、
    /// 完了後に例外を再スロー／ログ出力するためのラッパー。
    /// </summary>
    private static IEnumerator WaitTask(Task task)
    {
        while (!task.IsCompleted) yield return null;
        if (task.IsFaulted && task.Exception != null)
        {
            Debug.LogError($"TerrainManager: Background task failed: {task.Exception.Flatten()}");
        }
    }

    // ================================================================
    // 補間メソッド
    // ================================================================

    /// <summary>
    /// 縁ブレンドで無視できる高さ差の閾値(正規化高さ)。terrainSize.y=1200m なら約1mmに相当し、
    /// これ以下の差は視認できないためブレンドをスキップする。
    /// </summary>
    private const float STITCH_HEIGHT_EPSILON = 1e-6f;

    /// <summary>
    /// これから適用するハイトマップの縁を、既に画面へ出ている隣接タイルの縁に合わせる。
    ///
    /// 全タイルの解像度は統一されている(<see cref="TILE_RESOLUTION"/>)が、隣接セル同士で
    /// 適用済みデータの世代(far/near/dynamic)が異なると、同じ境界線上の標高がわずかに
    /// 食い違ってセル境界に裂け目やV字の谷ができる。これを防ぐため、「後から適用する側」が
    /// 隣接の縁の値をそのまま自分の縁に採用し、内側 blendBand ピクセルへ差分を減衰させて馴染ませる。
    ///
    /// この「後から適用する側が合わせる」不変則により、既に適用済みのタイルを後から触り直す
    /// 再縫合が不要になる(適用済みタイルの縁は以後変化しないため)。隣接の縁の参照は
    /// originalHeightsCache(そのタイルに現在適用されているブレンド後の配列)から行い、
    /// GPU→CPUの同期readback(GetHeights)は発生しない。
    /// </summary>

    private void BlendEdgesTowardAppliedNeighbors(Vector2Int cellIndex, float[,] heights, int res, TerrainLodLevel myLod)
    {
        int blendBand = Mathf.Clamp(res / 64, 1, 6);
        BlendOneEdgeTowardNeighbor(cellIndex, -1, 0, heights, res, blendBand, isVertical: true, isLowEnd: true, myLod);
        BlendOneEdgeTowardNeighbor(cellIndex, 1, 0, heights, res, blendBand, isVertical: true, isLowEnd: false, myLod);
        BlendOneEdgeTowardNeighbor(cellIndex, 0, -1, heights, res, blendBand, isVertical: false, isLowEnd: true, myLod);
        BlendOneEdgeTowardNeighbor(cellIndex, 0, 1, heights, res, blendBand, isVertical: false, isLowEnd: false, myLod);
    }

    /// <summary>
    /// 1辺分の縁ブレンド。隣接タイルが可視(=terrainTiles 登録済み)で適用済みハイトマップを
    /// 持つ場合のみ、その縁の値へ自分の縁を一致させ、内側へ差分を減衰させる。
    /// 差が STITCH_HEIGHT_EPSILON 以下の点は触らない。
    /// </summary>
    private void BlendOneEdgeTowardNeighbor(Vector2Int cellIndex, int dx, int dz, float[,] heights, int res, int blendBand, bool isVertical, bool isLowEnd, TerrainLodLevel myLod)
    {
        var neighborIdx = new Vector2Int(cellIndex.x + dx, cellIndex.y + dz);
        if (!terrainTiles.ContainsKey(neighborIdx)) return;
        if (!originalHeightsCache.TryGetValue(neighborIdx, out float[,] neighborHeights)) return;
        // 解像度は全タイル統一が前提。万一の不一致(想定外の遺物)はブレンドせず放置する
        // (走査での差し替えを待つ)。
        if (neighborHeights.GetLength(0) != res || neighborHeights.GetLength(1) != res) return;

        // 隣接セルのLODレベルを取得
        if (!cellLodLevel.TryGetValue(neighborIdx, out TerrainLodLevel neighborLod)) return;

        // 自分または隣接セルが Far ティアの場合はブレンドを行わない。
        // Far ティアは解像度が極めて粗いため(200m間隔)、起伏の激しい山などで本来の標高と大きく異なり、
        // これを Near/Dynamic とブレンドすると境界に大きな段差や V字の谷が固定されてしまうため。
        if (myLod == TerrainLodLevel.Far || neighborLod == TerrainLodLevel.Far) return;

        if (isVertical)
        {
            // 自分の左縁(isLowEnd)は隣接の右縁(列res-1)と接する。逆も同様。
            int neighborCol = isLowEnd ? res - 1 : 0;
            int ourCol = isLowEnd ? 0 : res - 1;
            for (int y = 0; y < res; y++)
            {
                float diff = neighborHeights[y, neighborCol] - heights[y, ourCol];
                if (Mathf.Abs(diff) <= STITCH_HEIGHT_EPSILON) continue;
                for (int w = 0; w < blendBand; w++)
                {
                    float weight = 1f - ((float)w / blendBand);
                    int x = isLowEnd ? w : res - 1 - w;
                    heights[y, x] += diff * weight;
                }
            }
        }
        else
        {
            int neighborRow = isLowEnd ? res - 1 : 0;
            int ourRow = isLowEnd ? 0 : res - 1;
            for (int x = 0; x < res; x++)
            {
                float diff = neighborHeights[neighborRow, x] - heights[ourRow, x];
                if (Mathf.Abs(diff) <= STITCH_HEIGHT_EPSILON) continue;
                for (int w = 0; w < blendBand; w++)
                {
                    float weight = 1f - ((float)w / blendBand);
                    int y = isLowEnd ? w : res - 1 - w;
                    heights[y, x] += diff * weight;
                }
            }
        }
    }

    /// <summary>
    /// 任意解像度のハイトマップを Terrain が要求する 2^n+1 解像度に線形補間する。
    /// 純粋な配列演算なのでバックグラウンドスレッドから呼んで良い。
    /// </summary>
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
    /// 境界のワールド座標では双方が同値に収束し、縁ブレンド(BlendEdgesTowardAppliedNeighbors)を崩さない。
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

    public int GetMapWidth() => terrainSettings?.map_width ?? 10000;
    public int GetMapHeight() => terrainSettings?.map_height ?? 10000;

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
    /// ワールド座標からバックエンドの 0-based セルインデックスを直接計算する。
    /// Python 側の計算式と完全に一致させるため、
    /// マップ中央を原点とするワールド座標を左上原点のセルインデックスに変換する。
    /// </summary>
    public Vector2Int WorldPositionToBackendCell(Vector3 worldPosition)
    {
        float cellSize = GetCellSize();
        // マップ中央原点のワールド座標から、マップ左下隅を原点とするバックエンドの非負セルインデックスを計算する。
        // ここでも浮動小数点の計算誤差を回避するため、まずUnityのセルインデックス（正負）を求め、それに整数オフセットを加算する。
        int offsetX = Mathf.RoundToInt((GetMapWidth() / 2.0f) / cellSize);
        int offsetY = Mathf.RoundToInt((GetMapHeight() / 2.0f) / cellSize);

        int ux = Mathf.FloorToInt(worldPosition.x / cellSize);
        int uy = Mathf.FloorToInt(worldPosition.z / cellSize);

        return new Vector2Int(ux + offsetX, uy + offsetY);
    }

    /// <summary>
    /// バックエンドの 0-based セルインデックスを Unity のセルインデックスに変換する。
    /// マップ幅・高さがセルサイズの偶数倍であるため、バックエンドセルとUnityセルは
    /// 1対1で対応し、常に1つのセルのみを返す。
    /// </summary>
    public IEnumerable<Vector2Int> BackendCellToUnityCells(Vector2Int backendCell)
    {
        // マップの最大幅・高さがセルサイズの偶数倍であることを利用し、
        // 浮動小数点の境界値判定で生じる不要な隣接セルの誤解放（3x3化）を完全に排除する。
        // バックエンドの非負セル座標から整数オフセットを差し引いた、唯一の対応するUnityセルインデックスを返す。
        int offsetX = Mathf.RoundToInt((GetMapWidth() / 2.0f) / GetCellSize());
        int offsetY = Mathf.RoundToInt((GetMapHeight() / 2.0f) / GetCellSize());
        yield return new Vector2Int(backendCell.x - offsetX, backendCell.y - offsetY);
    }

    // ================================================================
    // 開放済みセル管理
    // ================================================================

    /// <summary>
    /// 指定セルがバックエンドで開放済み（acquire済み）かどうかを判定する。
    /// 開放済みリストが未ロードの場合は安全のため false を返す（未開放とみなす）。
    /// </summary>
    public bool IsCellAcquired(Vector2Int cellIndex)
    {
        return acquiredCellsLoaded && acquiredCells.Contains(cellIndex);
    }

    /// <summary>
    /// 開放済みセルリストがバックエンドから取得済みかどうか。
    /// </summary>
    public bool IsAcquiredCellsLoaded => acquiredCellsLoaded;

    /// <summary>
    /// 現在保持している開放済みUnityセルの件数。
    /// </summary>
    public int AcquiredCellsCount => acquiredCells.Count;

    /// <summary>
    /// 開放済みセルのセットを返す。LodConfig などで LOD ティア計算に使用する。
    /// リスト未ロード時は空のセットを返す。
    /// </summary>
    public HashSet<Vector2Int> GetAcquiredCells()
    {
        return new HashSet<Vector2Int>(acquiredCells);
    }

    /// <summary>
    /// バックエンドから開放済みセル一覧を取得し、ローカルにキャッシュする。
    /// セッション開始時やセーブデータロード後に呼ぶ必要がある。
    /// </summary>
    public IEnumerator FetchAcquiredCells()
    {
        yield return EnsureSettingsLoaded();
        if (!settingsLoaded)
        {
            Debug.LogError("TerrainManager: Settings not loaded, cannot fetch acquired cells.");
            yield break;
        }

        string error = null;

        yield return ApiClient.Get<AcquiredCellsResponse>(
            "/terrain/acquired-cells",
            response =>
            {
                acquiredCells.Clear();
                if (response.cells != null)
                {
                    foreach (var item in response.cells)
                    {
                        Vector2Int backendCell = new Vector2Int(item.cell_x, item.cell_z);
                        foreach (var unityCell in BackendCellToUnityCells(backendCell))
                        {
                            acquiredCells.Add(unityCell);
                        }
                    }
                }
                acquiredCellsLoaded = true;
            },
            err => { error = err; }
        );

        if (!string.IsNullOrEmpty(error))
        {
            Debug.LogError($"TerrainManager: Failed to fetch acquired cells: {error}");
        }
        else
        {
            Debug.Log($"TerrainManager: Fetched {acquiredCells.Count} acquired cells.");
            // 開放済みリストが更新されたら、既存のTerrainタイルの濃色表示状態を再描画する。
            // セッションリロード後など、リストの内容が変わってもSplatmapが古いままになるのを防ぐ。
            if (IsUnlockOverlayActive)
            {
                RefreshAllUnlockOverlays();
                // セルデータのロード完了時に境界線の初期構築とラベル生成を即座に実行する。
                RefreshAllCellOverlays();
            }
        }

        // 海岸線（陸地境界ポリゴン）も同じセッション初期化のタイミングで取得しておく。
        // 港湾（MODEL_PORT）の岸壁メッシュ生成で、底面のどの辺が海岸線に接しているかの
        // 判定に使う（CoastlineStore に格納）。建物のロードより先に取得しておく必要がある。
        yield return FetchCoastline();
    }

    /// <summary>
    /// 海岸線（陸地の境界ポリゴン）を取得して <see cref="CoastlineStore"/> に格納する。
    /// レスポンスは GeoJSON ライク（Polygon / MultiPolygon）。座標はワールド座標。
    /// 海岸線は far_map ベースで init_game 時に確定し以降不変なので、セッション開始時に一度取得すればよい。
    /// </summary>
    public IEnumerator FetchCoastline()
    {
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

        // 水面(海面以外の湖など)も海岸線と同じセッション初期化タイミングで取得・生成する。
        // 水面はマップ固有の静的データで、海岸線と同様 init_game 時に確定し以降不変。
        // WaterSurfaceManager は AfterSceneLoad で自己生成されるため通常は Instance が存在する。
        if (WaterSurfaceManager.Instance != null)
        {
            yield return WaterSurfaceManager.Instance.LoadAndBuild();
        }
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
    /// 開放済みセルキャッシュをクリアする。
    /// セッション終了時やリセット時に呼ぶ。
    /// </summary>
    public void ClearAcquiredCells()
    {
        acquiredCells.Clear();
        acquiredCellsLoaded = false;
    }

    /// <summary>
    /// 遠景高さマップから指定セルのおおまかな高さ範囲（min, max）を取得する。
    /// farTerrainHeights が未ロードの場合は false を返す。
    /// セルのティア判定で、カメラからの3次元最短距離を計算する際に使用する。
    /// </summary>
    public bool TryGetCellHeightRange(Vector2Int cellIndex, out float minY, out float maxY)
    {
        minY = 0f;
        maxY = 0f;

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
                float h = farTerrainHeights[y, x];
                if (h < minY) minY = h;
                if (h > maxY) maxY = h;
            }
        }

        return true;
    }

    public Terrain GetTerrainAtPosition(Vector3 position)
    {
        Vector2Int cellIndex = new Vector2Int(
            Mathf.FloorToInt(position.x / terrainSize.x),
            Mathf.FloorToInt(position.z / terrainSize.z));

        if (terrainTiles.TryGetValue(cellIndex, out Terrain terrain) && terrain != null && terrain.gameObject.activeSelf)
        {
            return terrain;
        }

        return null;
    }

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
    /// (不一致の境界は接続せず、高さは縁ブレンドで合わせ済み)。
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
    /// per-cell タイル(Far/Near/Dynamic)の Terrain にマテリアルを適用する。
    /// </summary>
    private void ApplyTerrainMaterial(Terrain terrain, TerrainData terrainData)
    {
        if (useCustomMaterial && customTerrainMaterial != null)
        {
            terrain.materialTemplate = customTerrainMaterial;
            ApplyTerrainOffsetMaterial(terrain);
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
                ApplyTerrainOffsetMaterial(terrain);
                return;
            }
        }

        ApplyDefaultTerrainMaterial(terrainData);
        ApplyTerrainOffsetMaterial(terrain);
    }

    /// <summary>
    /// タイルごとにクローンした materialTemplate を識別するための名前サフィックス。
    /// _TerrainTint(未開放セルの暗色化)をタイル単位で独立に設定するため、
    /// 共有マテリアル(アセット等)をそのまま materialTemplate に使ってはいけない。
    /// </summary>
    private const string TILE_MATERIAL_SUFFIX = " (TileInstance)";

    /// <summary>
    /// TerrainのmaterialTemplateを、Z-fighting防止用の Custom/DepthOffsetTerrainLit(Offset 1,1)に
    /// 差し替える。共有マテリアルは必ずクローンして「タイルごとの個別インスタンス」にする
    /// (_TerrainTint をタイル単位で変えるため)。クローン済みかは名前サフィックスで識別し、
    /// 再適用時の二重クローンを避ける。
    /// </summary>
    private void ApplyTerrainOffsetMaterial(Terrain terrain)
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
            }
        }
        else
        {
            terrain.materialTemplate = new Material(offsetShader) { name = "TerrainTile" + TILE_MATERIAL_SUFFIX };
        }

        ApplyTerrainMaterialSettings(terrain.materialTemplate);
    }

    private void ApplyTerrainMaterialSettings(Material material)
    {
        if (material == null) return;

        material.SetFloat(TerrainMacroVariationStrengthId, terrainMacroVariationStrength);
        material.SetFloat(TerrainMacroVariationScaleId, Mathf.Max(1f, terrainMacroVariationScale));
        material.SetColor(TerrainMacroVariationTintId, terrainMacroVariationTint);

        // スタイライズ向けカラーグレード(標高・傾斜ベースの色被せ)
        material.SetFloat(TerrainColorGradeStrengthId, terrainColorGradeStrength);
        material.SetFloat(TerrainColorGradeLowHeightId, terrainColorGradeLowHeight);
        material.SetFloat(TerrainColorGradeHighHeightId, Mathf.Max(terrainColorGradeLowHeight + 1f, terrainColorGradeHighHeight));
        material.SetColor(TerrainLowColorId, terrainLowColor);
        material.SetColor(TerrainHighColorId, terrainHighColor);
        material.SetColor(TerrainSlopeColorId, terrainSlopeColor);

        // 平地の手続きディテール法線。シェーダーは周期の逆数(1/m)を乗算で使う。
        material.SetFloat(TerrainDetailNormalStrengthId, terrainDetailNormalStrength);
        material.SetFloat(TerrainDetailNormalScaleId, 1f / Mathf.Max(0.01f, terrainDetailNormalTilingMeters));

        // 地面のベースアルベドの手続き生成(テクスチャ反復の抑制)
        material.SetFloat(ProceduralGroundBlendId, terrainProceduralGroundBlend);
        material.SetFloat(ProceduralGroundScaleId, Mathf.Max(1f, terrainProceduralGroundScale));
        material.SetColor(TerrainGroundColorAId, terrainGroundColorA);
        material.SetColor(TerrainGroundColorBId, terrainGroundColorB);
        material.SetColor(TerrainGroundColorCId, terrainGroundColorC);

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

    private void ApplyDefaultTerrainMaterial(TerrainData terrainData)
    {
        TerrainLayer normalLayer = new TerrainLayer();

        if (defaultDiffuseTexture != null)
        {
            normalLayer.diffuseTexture = defaultDiffuseTexture;
        }
        else
        {
            Texture2D tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, defaultTerrainColor);
            tex.Apply();
            normalLayer.diffuseTexture = tex;
        }

        // defaultNormalTexture が defaultDiffuseTexture と同一の場合は法線マップとして使わない。
        // シーン(GameScene.unity)には過去の設定ミスでカラーテクスチャ(草)が法線マップ欄に
        // 流用されており、これをTerrainLayerに渡すと _NORMALMAP が有効化されて
        // 地形全体の法線がデタラメな一方向に傾く(→崖の岩ブレンドが特定の向きの斜面
        // だけに出る・傾斜が過大評価される等、方向依存の描画バグの原因になる)。
        // UltraFar陸地メッシュが法線マップを設定しないのと同じ理由の防御措置。
        if (defaultNormalTexture != null && defaultNormalTexture == defaultDiffuseTexture)
        {
            Debug.LogWarning(
                "TerrainManager: defaultNormalTexture に defaultDiffuseTexture と同じカラーテクスチャが" +
                "設定されているため、法線マップとしては使用しません。シーンの設定を確認してください。");
        }
        else if (defaultNormalTexture != null)
        {
            normalLayer.normalMapTexture = defaultNormalTexture;
        }

        Vector2 tileSize = defaultTileSize;
        if (tileSize == Vector2.zero)
        {
            tileSize = new Vector2(terrainSize.x, terrainSize.z);
        }
        normalLayer.tileSize = tileSize;
        normalLayer.metallic = defaultMetallic;
        normalLayer.smoothness = defaultSmoothness;

        terrainData.terrainLayers = new TerrainLayer[] { normalLayer };
    }

    /// <summary>
    /// 指定セルの Terrain に対して、未開放セルの暗色表示状態を適用する。
    /// IsUnlockOverlayActive が true かつセルが未開放の場合、タイルの materialTemplate の
    /// 乗算ティント(_TerrainTint)を unacquiredTint にして、通常のテクスチャ付き地面を少し暗くする。
    /// それ以外は白(ティントなし=通常色)に戻す。
    /// materialTemplate は ApplyTerrainOffsetMaterial がタイルごとにクローンした個別インスタンスなので、
    /// タイル単位で独立に色を変えられる。
    /// </summary>
    private void ApplyUnlockOverlayToTerrain(Vector2Int cellIndex, Terrain terrain)
    {
        if (terrain == null || terrain.materialTemplate == null) return;

        bool useDark = IsUnlockOverlayActive && !IsCellAcquired(cellIndex);
        terrain.materialTemplate.SetColor(TerrainTintId, useDark ? unacquiredTint : Color.white);
    }

    [ContextMenu("Refresh Terrain Materials")]
    public void RefreshAllTerrainMaterials()
    {
        foreach (var kvp in terrainTiles)
        {
            if (kvp.Value != null)
            {
                ApplyTerrainMaterial(kvp.Value, kvp.Value.terrainData);
                ApplyUnlockOverlayToTerrain(kvp.Key, kvp.Value);
            }
        }

        // UltraFar は Terrain ではなく陸地メッシュなので、マテリアル色の濃色表示のみ反映する。
        RefreshUltraFarLandOverlay();
    }

    // ================================================================
    // セル境界線・座標ラベル描画
    // ================================================================

    /// <summary>
    /// 全ロード済みセルの境界線・座標ラベルを、現在の表示設定に応じて更新する。
    /// </summary>
    private void RefreshAllCellOverlays()
    {
        foreach (var cell in loadedCells)
        {
            RefreshCellOverlay(cell);
        }
        borderSegmentsDirty = true;
    }

    /// <summary>
    /// 指定セルの座標ラベルを、現在の表示設定に応じて生成または破棄する。
    /// 境界線は UpdateCellOverlayIfActive → RebuildBorderSegments で一括管理するため、ここでは扱わない。
    /// </summary>
    private void RefreshCellOverlay(Vector2Int cellIndex)
    {
        if (ShowCellBordersAndLabels)
        {
            EnsureCellLabel(cellIndex);
        }
        else
        {
            DestroyCellOverlay(cellIndex);
        }
        // 新しい地形タイルがロード・準備完了した際、高精度の地形に境界線の高さを追従させるため、再構築フラグを立てる。
        borderSegmentsDirty = true;
    }

    /// <summary>
    /// 指定セルの座標ラベルを破棄する。
    /// </summary>
    private void DestroyCellOverlay(Vector2Int cellIndex)
    {
        if (cellLabelObjects.TryGetValue(cellIndex, out TextMeshPro labelTMP) && labelTMP != null)
        {
            Destroy(labelTMP.gameObject);
            cellLabelObjects.Remove(cellIndex);
        }
    }

    /// <summary>
    /// 全セルの境界線と座標ラベルを破棄する。
    /// </summary>
    private void ClearAllCellOverlays()
    {
        borderSegments3D.Clear();
        borderSegmentsDirty = true;

        foreach (var kvp in cellLabelObjects)
        {
            if (kvp.Value != null) Destroy(kvp.Value.gameObject);
        }
        cellLabelObjects.Clear();

        if (cellOverlayRenderer != null) cellOverlayRenderer.ClearMesh();
    }

    // ================================================================
    // セルオーバーレイ（OverlayMeshRenderer 版）
    // ================================================================

    private void EnsureCellOverlayRenderer()
    {
        if (cellOverlayMeshObj != null) return;
        cellOverlayMeshObj = new GameObject("TerrainCellOverlayMesh");
        DontDestroyOnLoad(cellOverlayMeshObj);
        cellOverlayRenderer = cellOverlayMeshObj.AddComponent<OverlayMeshRenderer>();
        Material mat = new Material(Shader.Find("Custom/OverlayLine"));
        cellOverlayRenderer.Initialize(mat);
    }

    /// <summary>
    /// 毎フレーム呼び出し。IsUnlockOverlayActive のとき、開放済みセルの外枠線を OverlayMeshRenderer で描画する。
    /// 未開放セルの暗色表現は、各 Terrain タイルの乗算ティント(_TerrainTint)と、
    /// 超遠景陸地メッシュの未開放サブメッシュのティント乗算によって表現するため、ここでの半透明ポリゴンの描画は不要となる。
    ///
    /// 境界線の点線ジオメトリはワールド固定幅でカメラに依存しないため、一度構築したメッシュは
    /// そのまま描画され続ける。毎フレーム同じメッシュを作り直して GPU に再アップロードするのを避けるため、
    /// 境界の変化(borderSegmentsDirty)や表示切り替え(ShowCellBordersAndLabels の変化)があった
    /// フレームだけ再構築する。カメラ静止時に無駄な再構築が走らないようにする最適化。
    /// </summary>
    private void UpdateCellOverlayIfActive()
    {
        if (!IsUnlockOverlayActive || !acquiredCellsLoaded)
        {
            // 非表示化はメッシュを保持したまま GameObject を無効化するだけにする。
            // 再表示時は cellOverlayLastShowBorders / borderSegmentsDirty の状態次第で再構築を判断する。
            if (cellOverlayMeshObj != null) cellOverlayMeshObj.SetActive(false);
            return;
        }

        EnsureCellOverlayRenderer();
        cellOverlayMeshObj.SetActive(true);

        // 既にメッシュを構築済みで、境界にも表示状態にも変化がなければ再構築しない。
        if (cellOverlayMeshBuilt && !borderSegmentsDirty && ShowCellBordersAndLabels == cellOverlayLastShowBorders)
        {
            return;
        }

        Camera cam = Camera.main;
        if (cam == null) return;

        cellOverlayRenderer.BeginFrame();

        // 開放済みセルの外枠線を点線で追加
        if (ShowCellBordersAndLabels)
        {
            if (borderSegmentsDirty) RebuildBorderSegments();
            // 境界線を目立たせつつ他の情報と重なっても視認性を損なわないよう、半透明（アルファ50%）の白色で描画する。
            Color borderColor = new Color(1f, 1f, 1f, 0.5f);
            foreach (var (a, b) in borderSegments3D)
            {
                // ピクセル空間ではなくワールド空間の固定幅（4.0メートル）で描画することにより、遠近感が正しく適用される。
                // 太線化に伴い、視認性を維持するため点線の実線長を16.0メートル、隙間を8.0メートルに調整する。
                cellOverlayRenderer.AddDashedLineWorld(a, b, borderColor, 4.0f, 16.0f, 8.0f);
            }
        }

        cellOverlayRenderer.EndFrame(cam);

        cellOverlayMeshBuilt = true;
        cellOverlayLastShowBorders = ShowCellBordersAndLabels;
    }

    /// <summary>
    /// 開放済みセルの外周エッジ（未開放・境界側のみ）を 3D セグメントとして構築しキャッシュする。
    /// 各エッジを細かく分割し地形追従させる。
    /// </summary>
    private void RebuildBorderSegments()
    {
        borderSegments3D.Clear();
        borderSegmentsDirty = false;
        if (!acquiredCellsLoaded || acquiredCells.Count == 0) return;

        // エッジをこの分割数でサブセグメントに切る（地形追従の細かさ）
        const int borderSubdivs = 8;
        // 地形面からの浮き上がり量。ZTest LEqual による地形へのめり込みを防ぎ、高低差のある場所でも安定して浮いて見えるように1.8mを設定する。
        const float yOffset = 1.8f;
        // セル境界線上で隣の非アクティブな（未ロードの）セルの地形を誤ってサンプリングし、
        // 遠景や超遠景の低解像度な地形データにフォールバックするのを避けるため、
        // サンプリング対象の座標をわずかに解放済みセルの内側にオフセットさせる距離（メートル）。
        const float samplingInnerOffset = 1.0f;

        float s = CellSize;
        // 始点グリッド, 終点グリッド, および解放済みセルの内側を指すオフセット方向ベクトル
        var raw = new List<((int, int), (int, int), Vector3)>();
        foreach (var cell in acquiredCells)
        {
            int cx = cell.x; int cy = cell.y;
            // 上側境界（+Z方向が未開放）：内側は -Z 方向。
            if (!acquiredCells.Contains(new Vector2Int(cx, cy + 1)))
                raw.Add(((cx, cy + 1), (cx + 1, cy + 1), new Vector3(0f, 0f, -samplingInnerOffset)));
            // 下側境界（-Z方向が未開放）：内側は +Z 方向。隣接判定座標は (cx, cy - 1) を正しく参照する。
            if (!acquiredCells.Contains(new Vector2Int(cx, cy - 1)))
                raw.Add(((cx + 1, cy), (cx, cy), new Vector3(0f, 0f, samplingInnerOffset)));
            // 左側境界（-X方向が未開放）：内側は +X 方向。隣接判定座標は (cx - 1, cy) を正しく参照する。
            if (!acquiredCells.Contains(new Vector2Int(cx - 1, cy)))
                raw.Add(((cx, cy), (cx, cy + 1), new Vector3(samplingInnerOffset, 0f, 0f)));
            // 右側境界（+X方向が未開放）：内側は -X 方向。隣接判定座標は (cx + 1, cy) を正しく参照する。
            if (!acquiredCells.Contains(new Vector2Int(cx + 1, cy)))
                raw.Add(((cx + 1, cy + 1), (cx + 1, cy), new Vector3(-samplingInnerOffset, 0f, 0f)));
        }

        foreach (var (a, b, innerOffsetDir) in raw)
        {
            float ax = a.Item1 * s, az = a.Item2 * s;
            float bx = b.Item1 * s, bz = b.Item2 * s;

            // 始点の高さを、解放済みセル内側にオフセットした座標から高精度に取得する
            Vector3 prevSamplePos = new Vector3(ax, 0f, az) + innerOffsetDir;
            float prevY = Utils.GetGroundHeight(prevSamplePos) + yOffset;
            Vector3 prev = new Vector3(ax, prevY, az);

            for (int k = 1; k <= borderSubdivs; k++)
            {
                float t = k / (float)borderSubdivs;
                float wx = Mathf.Lerp(ax, bx, t);
                float wz = Mathf.Lerp(az, bz, t);

                // 各中継点の高さを、解放済みセル内側にオフセットした座標から高精度に取得する
                Vector3 nextSamplePos = new Vector3(wx, 0f, wz) + innerOffsetDir;
                float nextY = Utils.GetGroundHeight(nextSamplePos) + yOffset;
                Vector3 next = new Vector3(wx, nextY, wz);

                borderSegments3D.Add((prev, next));
                prev = next;
            }
        }
    }

    /// <summary>
    /// 指定セルの座標ラベルを生成または更新する。
    /// セル中心の地形上に、Unityセル座標とバックエンドセル座標を表示する。
    /// </summary>
    private void EnsureCellLabel(Vector2Int cellIndex)
    {
        if (cellLabelObjects.ContainsKey(cellIndex)) return;

        GameObject labelGO = new GameObject($"CellLabel_{cellIndex.x}_{cellIndex.y}");
        Transform container = GetContainer("CellOverlays");
        if (container != null) labelGO.transform.SetParent(container);

        TextMeshPro tmp = labelGO.AddComponent<TextMeshPro>();
        tmp.fontSize = cellLabelFontSize;
        tmp.color = cellLabelColor;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.font = GetCachedFontAsset();

        Vector2Int backendCell = UnityCellToBackendCell(cellIndex);
        tmp.text = $"U:{cellIndex.x},{cellIndex.y}\nB:{backendCell.x},{backendCell.y}";

        float cellSize = CellSize;
        float centerX = (cellIndex.x + 0.5f) * cellSize;
        float centerZ = (cellIndex.y + 0.5f) * cellSize;
        float groundY = Utils.GetGroundHeight(new Vector3(centerX, 0f, centerZ));

        labelGO.transform.position = new Vector3(centerX, groundY + cellLabelYOffset, centerZ);
        labelGO.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        cellLabelObjects[cellIndex] = tmp;
    }

    /// <summary>
    /// TextMeshPro で使うフォントアセットをキャッシュして返す。
    /// Resources/Fonts & Materials/LiberationSans SDF を使用する。
    /// </summary>
    private TMP_FontAsset GetCachedFontAsset()
    {
        if (cachedFontAsset == null)
        {
            cachedFontAsset = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        }
        return cachedFontAsset;
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
