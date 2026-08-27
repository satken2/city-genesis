using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Linq;
using Newtonsoft.Json;
using GeneratedModels;

public enum RoadEditorPhase { Inactive, Placing, Editing }

/// <summary>
/// RoadPlanManager:
/// 道路エディタの中核マネージャ。デバッグ用HTML(path_build.html)と同じ操作フローを実装する。
///
/// 操作フロー:
///   1. UIパネルでバリアント選択 → IPCでグラフィックプログラムに通知
///   2. Placingフェーズ: 左クリックで始点/ウェイポイントを配置
///   3. 右クリックで形状確定 → Editingフェーズに移行
///   4. Editingフェーズ: ドラッグでポイント/制御点を移動、右クリックで削除、辺上クリックで挿入
///   5. [確定]ボタンでバックエンドに送信
///
/// フェーズ対応:
///   Inactive = HTMLの IDLE
///   Placing  = HTMLの PLACING
///   Editing  = HTMLの EDITING
///
/// すべてのパスは三次ベジエで表現される。
/// 各ポイントは Catmull-Romスプラインに基づき自動計算された control_in / control_out を持ち、
/// Editingフェーズでドラッグにより手動調整可能。
/// バックエンドの waypoints[].control_in / control_out と同じデータ構造。
/// </summary>
public class RoadPlanManager : MonoBehaviour
{
    [Header("References")]
    public PathManager pathManager;
    public Camera gameCamera;
    public Material previewMaterial;
    public Material errorPreviewMaterial;
    public RoadRenderer roadRenderer;

    [Header("Snapping")]
    // 道路/鉄道敷設時の吸着半径(m)。ノード/パス吸着(TerrainInputHandlerへ委譲する際に
    // この値を渡す)、直角スナップ・直線延長スナップ・既存パス延長方向の検出すべてに使う。
    public float snapThreshold = 5.0f;

    [Header("Road Settings")]
    public int PathType = Constants.PATH_MOTORWAY;
    public int PathSubtype = 0;
    public bool Curved = false;

    /// <summary>
    /// 駅パスのホーム設置側（Constants.PLATFORM_SIDE_*）。
    /// UIパネルのホーム設置側トグルが `edit_path:change_platform_side` で通知し、
    /// プレビューメッシュ（RoadRenderer 経由の TrackPath）へ反映する。
    /// 建設時にのみ指定でき、駅バリアント以外では無視される。
    /// </summary>
    public int PlatformSide = Constants.PLATFORM_SIDE_OUTSIDE;

    /// <summary>
    /// 地形追従の個別上書き（Constants.VALUE_NONE=未設定[バリアント既定]/0=非追従/1=追従）。
    /// UIパネルのトグル（edit_path:set_follow_terrain）が真実源。height_editable=true の
    /// バリアントでのみ上書きでき、非追従にすると丘に差し掛かる区間が自動的に切り通し・
    /// トンネルとしてプレビュー表示される。
    /// </summary>
    public int PathFollowTerrainOverride = Constants.VALUE_NONE;

    /// <summary>
    /// 複合パス(軌道複線)プレビュー用の各線の横オフセット(m。敷設方向右が正)。
    /// 空なら単一パスとして中心線1本をプレビューする。
    /// 確定時の平行展開はUI(Tauri)側が行うため、Unityはプレビュー描画にのみ使う。
    /// 初期値は `world:change_state`(STATE_EDIT_PATH)の `lateral_offsets` params で渡され、
    /// 敷設中の線路数変更は `edit_path:change_lateral_offsets` で逐次通知される。
    /// </summary>
    public float[] LateralOffsets = System.Array.Empty<float>();

    /// <summary>
    /// 複合パス(軌道複線)プレビュー用の各線の進行方向(Constants.DIR_*)。LateralOffsets と
    /// index対応の同じ並び。駅バリアントのプレビューでホーム設置側の解決
    /// (TrackPath.ResolvePlatformExtents)が走行方向を参照するため、オフセット列と対で保持する。
    /// 要素が無い線は DIR_BOTH(双方向)として扱われる。
    /// </summary>
    public int[] TravelDirections = System.Array.Empty<int>();

    /// <summary>
    /// 編集対象の既存パスID。0 の場合は新規作成モード。
    /// </summary>
    private int pathId = 0;

    [SerializeField] private float pointCircleRadius = 8f;
    [SerializeField] private float existingNodeHalfSize = 4f;
    [SerializeField] private float dashedLineThickness = 2f;
    [SerializeField] private float solidLineThickness = 1.5f;
    [SerializeField] private float controlHandleHalfSize = 5f;
    [SerializeField] private int bezierSamplesPerSegment = 16;

    [Header("Sound Effects")]
    [SerializeField] private AudioClip placePointSound;
    [SerializeField] private AudioClip finalizeShapeSound;

    // 距離フェード（ParcelManagerと同じ定数）
    private const float DISTANCE_START_FADE = 280f;
    private const float DISTANCE_INVISIBLE = 480f;

    // 建物干渉のローカル推定用。コリドーBBOX絞り込みの追加マージンと、
    // フットプリント塗りつぶしを地形から浮かせるオフセット（Zファイト回避）。
    private const float BuildingInterferenceMargin = 2f;
    private const float BuildingInterferenceHeightOffset = 0.06f;

    // =====================================================================
    // インフラ接続端子マーカー（赤い逆四角錐）の定数
    // =====================================================================

    /// <summary>四角錐の底面の半径（中心から角までの距離、メートル）。</summary>
    public const float TERMINAL_MARKER_BASE_HALF = InfrastructureMarkerUtility.BASE_HALF;
    /// <summary>四角錐の高さ（メートル）。</summary>
    public const float TERMINAL_MARKER_HEIGHT = InfrastructureMarkerUtility.HEIGHT;
    /// <summary>下向きの先端（apex）をノードからどれだけ上空に浮かせるか（メートル）。</summary>
    public const float TERMINAL_MARKER_APEX_OFFSET = InfrastructureMarkerUtility.APEX_OFFSET;
    /// <summary>Y軸回転の角速度（度/秒）。</summary>
    public const float TERMINAL_MARKER_ROTATE_SPEED = InfrastructureMarkerUtility.ROTATE_SPEED;
    /// <summary>上下ボビングの振幅（メートル）。</summary>
    public const float TERMINAL_MARKER_BOB_AMPLITUDE = InfrastructureMarkerUtility.BOB_AMPLITUDE;
    /// <summary>上下ボビングの角速度（ラジアン/秒）。</summary>
    public const float TERMINAL_MARKER_BOB_SPEED = InfrastructureMarkerUtility.BOB_SPEED;
    /// <summary>側面の明色。無ライトの頂点カラーで立体感を出すため2色の赤を交互に貼る。</summary>
    public static readonly Color TERMINAL_MARKER_COLOR_BRIGHT = InfrastructureMarkerUtility.COLOR_BRIGHT;
    /// <summary>側面の暗色（天面のフタにも使う）。</summary>
    public static readonly Color TERMINAL_MARKER_COLOR_DARK = InfrastructureMarkerUtility.COLOR_DARK;

    /// <summary>底面4隅の回転済みオフセット計算用のワークバッファ（毎フレームの確保を避ける）。</summary>
    private readonly Vector3[] terminalMarkerCornerOffsets = new Vector3[4];

    // ステート
    private RoadEditorPhase phase = RoadEditorPhase.Inactive;

    // 配置ポイント（デバッグ用HTMLの build.points に対応）
    private List<PathBuildPoint> points = new List<PathBuildPoint>();

    // 選択・ホバー状態
    private int selectedPointIndex = -1;
    private int hoveredEdgeIndex = -1;
    private Vector3 hoverWorldPosition;

    // ドラッグ状態
    private bool isDragging = false;
    private int dragPointIndex = -1;

    /// <summary>ドラッグ対象の種類。ポイントか制御点(in/out)かを区別する。</summary>
    private enum DragTarget { None, Point, ControlIn, ControlOut }
    private DragTarget dragTarget = DragTarget.None;

    // サブコンポーネント
    private OverlayMeshRenderer overlayRenderer;
    private GameObject overlayMeshObj;
    private RoadOverlayHitDetector hitDetector;

    // TerrainInputHandlerへの参照（スナップ処理の共通化用）
    private TerrainInputHandler terrainInputHandler;

    /// <summary>
    /// UIから通知された現在の標高（相対高さ）。
    /// 新規ノード配置時にこの値を地面高さに加算する。
    /// </summary>
    private float currentHeight = 0f;

    /// <summary>
    /// UIから通知された現在の盛土オフセット(m)。-5〜+5。
    /// 新規ノード配置時にこの値をポイントのembankmentHeightに設定する。
    /// 道路のY座標自体は0のままだが、着工時にこの高さだけDynamic地形を変形させる。
    /// 切土(マイナス)も可。全道路タイプで設定可能（高架不可の街路でも盛土は可能）。
    /// </summary>
    private float currentEmbankmentHeight = 0f;

    // 効果音再生用
    private AudioSource audioSource;

    // 背景オーバーレイ（既存パスの辺・ノード）。カメラ移動時のみ再構築する。
    private OverlayMeshRenderer backgroundRenderer;
    private GameObject backgroundMeshObj;
    private Vector3 lastBgCameraPos = Vector3.zero;
    private bool bgDirty = true;
    private const float BG_REBUILD_THRESHOLD_SQ = 25f;

    /// <summary>
    /// 前回の背景オーバーレイ再構築時点の PathManager.NodeDataVersion。
    /// ノードは編集モード進入後にも非同期ロードで到着するため、カメラが動かなくても
    /// カウンタの変化を検知して背景（既存ノード表示）を再構築する。
    /// </summary>
    private int lastBgNodeDataVersion = -1;

    // 形状キャッシュ。ポイントやホバー位置の変更時に無効化される。
    // 編集中アイドル状態ではキャッシュが維持され、毎フレームの重い再計算を回避する。
    private List<Vector3> cachedRenderPoints;
    private List<(int segIndex, PathSegment otherSeg)> cachedElevationViolations;
    private List<int> cachedMaxSpanViolations;
    // 敷設中パスと干渉する建物のフットプリント（ワールドXZポリゴン）のローカル推定キャッシュ。
    private List<List<Vector3>> cachedBuildingInterference;

    // 既存パスの立体交差判定用セグメントキャッシュ。
    // 編集中は既存パスが変化しないため、一度構築したセグメントを再利用する。
    private Dictionary<int, List<ElevationSegment>> existingElevSegCache = new Dictionary<int, List<ElevationSegment>>();

    private void InvalidateShapeCache()
    {
        cachedRenderPoints = null;
        cachedElevationViolations = null;
        cachedMaxSpanViolations = null;
        cachedBuildingInterference = null;
    }

    public bool IsEditMode => phase == RoadEditorPhase.Editing;

    /// <summary>敷設セッション中(配置中または編集中)かどうか。ラジアルメニュー/HUDの表示条件に使う。</summary>
    public bool IsSessionActive => phase != RoadEditorPhase.Inactive;

    /// <summary>
    /// HUD表示用の現在標高(相対高さ)。ノード選択中はそのノードの値、
    /// 未選択(配置中含む)は次回配置ノードに適用される currentHeight を返す。
    /// </summary>
    public float DisplayHeight
    {
        get
        {
            if (selectedPointIndex >= 0 && selectedPointIndex < points.Count)
            {
                var pt = points[selectedPointIndex];
                return pt.position.y - Utils.GetGroundHeight(pt.position);
            }
            return currentHeight;
        }
    }

    /// <summary>
    /// HUD表示用の現在盛土オフセット(m)。ノード選択中はそのノードの値、
    /// 未選択(配置中含む)は次回配置ノードに適用される currentEmbankmentHeight を返す。
    /// </summary>
    public float DisplayEmbankment
    {
        get
        {
            if (selectedPointIndex >= 0 && selectedPointIndex < points.Count)
            {
                return points[selectedPointIndex].embankmentHeight;
            }
            return currentEmbankmentHeight;
        }
    }

    /// <summary>
    /// 現在の PathSubtype で曲線モードが許可されているかをマスターデータから判定する。
    /// curve_allowed が明示的に false のバリアント(送電線など)のみ禁止。未定義は許可。
    /// </summary>
    public bool IsCurveAllowed()
    {
        if (MasterDataStore.Instance == null) return true;
        try
        {
            var master = MasterDataStore.Instance.GetPathMaster(PathSubtype);
            if (master != null && master.curve_allowed.HasValue)
                return master.curve_allowed.Value;
        }
        catch
        {
            // マスター未定義の場合は許可とする
        }
        return true;
    }

    // =====================================================================
    // ライフサイクル
    // =====================================================================

    void Start()
    {
        hitDetector = new RoadOverlayHitDetector();
        EnsureOverlayRenderer();

        if (roadRenderer != null)
        {
            roadRenderer.CreatePreviewMeshComponents(previewMaterial);
        }

        terrainInputHandler = FindAnyObjectByType<TerrainInputHandler>();
        if (terrainInputHandler == null)
        {
            Debug.LogError("RoadPlanManager: TerrainInputHandlerが見つかりません");
        }

        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }

        // 敷設中の設定操作(Ctrlラジアルメニュー・カーソルHUD)のドライバを常駐させる。
        // セッションの開始/終了は RoadEditControlMenu 側が IsSessionActive をポーリングして検知する。
        var controlMenu = GetComponent<RoadEditControlMenu>();
        if (controlMenu == null)
        {
            controlMenu = gameObject.AddComponent<RoadEditControlMenu>();
        }
        controlMenu.Initialize(this);
    }

    void Update()
    {
        if (phase == RoadEditorPhase.Inactive) return;

        if (phase == RoadEditorPhase.Editing)
        {
            UpdateHoverState();
            HandleHeightInput();
            HandleEmbankmentInput();
        }
        else if (phase == RoadEditorPhase.Placing)
        {
            // 配置中もキーで「次に置くノード」の標高・盛土(currentHeight/currentEmbankmentHeight)を
            // 変更できるようにする。設定UIはパネルではなくUnity側(キー+カーソルHUD)が担うため、
            // 配置前に高さを決める手段がキー入力しかない。
            HandleHeightInput();
            HandleEmbankmentInput();
        }
    }

    void LateUpdate()
    {
        if (phase == RoadEditorPhase.Inactive) return;

        // InputHandler.Update より後に実行される LateUpdate でオーバーレイを描画し、
        // ProcessMouseDrag による points 更新が確実に反映されるようにする。
        UpdateOverlay();
    }

    // =====================================================================
    // Canvas / Overlay セットアップ
    // =====================================================================

    private void EnsureOverlayRenderer()
    {
        if (overlayMeshObj == null)
        {
            overlayMeshObj = new GameObject("RoadEditorOverlayMesh");
            overlayRenderer = overlayMeshObj.AddComponent<OverlayMeshRenderer>();
            Material mat = new Material(Shader.Find("Custom/OverlayLine"));
            // ノード・ベジエ制御点・補助線などの編集ギズモは地形に埋もれず常に手前に出す。
            // OverlayLine の既定 _ZTest=LEqual + ポリゴンオフセットでは、起伏のある地形で
            // ハンドルが地面の裏に回ると深く埋もれてしまう。このマテリアルインスタンス限定で
            // _ZTest を Always(8) にして深度テストを無効化する（他オーバーレイには影響しない）。
            mat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
            overlayRenderer.Initialize(mat);
        }

        if (backgroundMeshObj == null)
        {
            backgroundMeshObj = new GameObject("RoadEditorBackgroundMesh");
            backgroundRenderer = backgroundMeshObj.AddComponent<OverlayMeshRenderer>();
            Material bgMat = new Material(Shader.Find("Custom/OverlayLine"));
            // 既存パスの辺・ノードも同様に地形へ埋もれず手前に表示する
            bgMat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
            backgroundRenderer.Initialize(bgMat);
            bgDirty = true;
        }
    }

    // =====================================================================
    // パブリックAPI: モード遷移
    // =====================================================================

    public void SetPathType(int pathType)
    {
        PathType = pathType;
        bgDirty = true;
    }
    public void SetPathSubtype(int pathSubtype)
    {
        PathSubtype = pathSubtype;
        // 標高編集不可のバリアント（街路など）に切り替えた場合は標高をリセットする。
        // 前のバリアント（自動車専用道路など）で設定した標高が残らないようにする。
        // 地形追従の個別上書きも標高編集不可バリアントでは意味を失うため未設定へ戻す。
        if (!IsHeightEditable())
        {
            currentHeight = 0f;
            PathFollowTerrainOverride = Constants.VALUE_NONE;
        }
        bgDirty = true;
    }
    public void SetPathId(int id) => pathId = id;

    /// <summary>
    /// 駅パスのホーム設置側を設定し、プレビューメッシュへ即座に反映する。
    /// UIパネルのホーム設置側トグル(edit_path:change_platform_side)から呼ばれる。
    /// 敷設セッション中(配置中・編集中)に切り替えても、その場でプラットフォームの
    /// 張り出し側が更新される。
    /// </summary>
    public void SetPlatformSide(int side)
    {
        if (PlatformSide == side) return;
        PlatformSide = side;
        // 点列やパラメータは変わらないが platform_side が変わったので、
        // RoadRenderer 側の重複判定を通過させて再生成させる。
        Update3DPreview();
    }

    /// <summary>
    /// 複合パス(軌道複線)プレビューの各線横オフセット・進行方向を設定し、プレビューメッシュへ
    /// 即座に反映する。UIパネルの線路数・進行方向の変更(edit_path:change_lateral_offsets)から呼ばれる。
    /// 敷設セッション中(配置中・編集中)に切り替えても、その場で平行プレビューの本数・
    /// ホーム設置側・進行方向矢印が更新される。
    /// </summary>
    public void SetLateralOffsets(float[] offsets, int[] directions)
    {
        float[] normalized = offsets ?? System.Array.Empty<float>();
        int[] normalizedDirs = directions ?? System.Array.Empty<int>();
        bool same = LateralOffsets.Length == normalized.Length
            && TravelDirections.Length == normalizedDirs.Length;
        if (same)
        {
            for (int i = 0; i < normalized.Length; i++)
            {
                if (Mathf.Abs(LateralOffsets[i] - normalized[i]) > 1e-4f) { same = false; break; }
            }
        }
        if (same)
        {
            for (int i = 0; i < normalizedDirs.Length; i++)
            {
                if (TravelDirections[i] != normalizedDirs[i]) { same = false; break; }
            }
        }
        if (same) return;
        LateralOffsets = normalized;
        TravelDirections = normalizedDirs;
        Update3DPreview();
    }

    /// <summary>
    /// 敷設中パスの実効地形追従を解決する。個別上書き（0/1）を最優先し、
    /// 未設定（VALUE_NONE）のときのみバリアント既定に従う。
    /// バックエンド geo_path_depth.path_follows_terrain / PathManager.GetPathFollowTerrain と同一。
    /// </summary>
    public bool EffectiveFollowTerrain()
    {
        if (PathFollowTerrainOverride == 1) return true;
        if (PathFollowTerrainOverride == 0) return false;
        return PathManager.GetPathFollowTerrain(PathSubtype);
    }

    /// <summary>
    /// 地形追従の個別上書きを設定する。UIパネルのトグル（edit_path:set_follow_terrain）から呼ばれる。
    /// height_editable=false のバリアントでは上書きを無視して未設定へ戻す。
    /// 変更後はプレビュー点列・トンネル可視化を作り直し、確定送信用に UI へエコーバックする。
    /// </summary>
    public void SetFollowTerrain(int value)
    {
        int normalized = (value == 0 || value == 1) ? value : Constants.VALUE_NONE;
        if (normalized != Constants.VALUE_NONE && !IsHeightEditable())
        {
            normalized = Constants.VALUE_NONE;
        }
        if (PathFollowTerrainOverride == normalized) return;
        PathFollowTerrainOverride = normalized;
        InvalidateShapeCache();
        Update3DPreview();
        EmitShapeUpdate();
    }

    public void SetCurved(bool curved)
    {
        // curve_allowed=false のバリアント(送電線など)では曲線化を拒否する
        Curved = curved && IsCurveAllowed();
        // 曲線モードでは接続先の既存パスへ滑らかにつなぐため「直線延長」吸着を自動でONにする
        // (端点制御点の延長方向整列がこのフラグでゲートされている)。不要ならラジアルメニューでOFFにできる。
        if (Curved)
        {
            TerrainInputHandler.EnableStraightLineSnapping = true;
        }
        // 曲線⇔直線の切り替えで端点整列の適用有無も変わるため制御点を再計算する
        RecomputeAllControlPoints();
        InvalidateShapeCache();
        Update3DPreview();
        EmitShapeUpdate();
    }

    /// <summary>
    /// 道路敷設モードを初期化する。
    /// existingPathId が指定されている場合は既存パス編集モードとなり、
    /// バックエンドから該当パスの形状データを取得して points にロードする。
    /// </summary>
    public void InitializeRoadPlacement(int? existingPathId = null)
    {
        Debug.Log($"RoadPlanManager: 道路敷設モードを初期化 (existingPathId={existingPathId})");
        EnsureOverlayRenderer();

        phase = RoadEditorPhase.Placing;
        points.Clear();
        selectedPointIndex = -1;
        hoveredEdgeIndex = -1;
        isDragging = false;
        dragTarget = DragTarget.None;
        // 初期高度は地上(0)とする。min_height は地下対応バリアントの下限であって初期値ではない。
        // 地下道路や送電線なども含め一律 0m から敷設を開始し、必要に応じてパネル/吸着で高さを変える。
        // Max(0f, …) により万一 min_height が正のバリアントがあっても下限を割らないようにする。
        currentHeight = Mathf.Max(0f, GetMinHeightFromMaster());
        currentEmbankmentHeight = 0f;
        // 地形追従の個別上書きは未設定（バリアント既定に従う）から始める。
        // 既存パス編集の場合は LoadExistingPath でバックエンドの follow_terrain に上書きされる。
        PathFollowTerrainOverride = Constants.VALUE_NONE;
        // 曲線許可バリアントは曲線モードをデフォルトとする(曲線/直線の真実源はUnity側)。
        // 既存パス編集の場合は LoadExistingPath でバックエンドの curved 値に上書きされる。
        Curved = IsCurveAllowed();
        // 曲線モードで開始する場合は「直線延長」吸着を自動でONにする(端点制御点の
        // 延長方向整列がこのフラグでゲートされているため。SetCurved と同じ方針)。
        if (Curved)
        {
            TerrainInputHandler.EnableStraightLineSnapping = true;
        }
        InvalidateShapeCache();
        existingElevSegCache.Clear();
        bgDirty = true;

        if (roadRenderer != null)
        {
            roadRenderer.CreatePreviewMeshComponents(previewMaterial);
            roadRenderer.errorPreviewMaterial = errorPreviewMaterial;
        }

        if (existingPathId.HasValue && existingPathId.Value > 0)
        {
            pathId = existingPathId.Value;
            StartCoroutine(LoadExistingPath(existingPathId.Value));
        }
        else
        {
            pathId = 0;
            // 曲線/直線の真実源はUnity側のため、点列が空でも初期状態の is_curved を
            // UIパネル(形状プレビュー・確定送信・コスト見積り)へ通知しておく。
            // 既存パス編集の場合は LoadExistingPath 完了時に通知される。
            EmitShapeUpdate();
        }
    }

    /// <summary>
    /// バックエンドから既存パスの形状データを取得し、points に展開する。
    /// 取得後は Editing フェーズに遷移し、3DプレビューとUIへの通知を行う。
    /// </summary>
    private IEnumerator LoadExistingPath(int existingPathId)
    {
        yield return ApiClient.Get<PathSummary>(
            $"/paths/{existingPathId}",
            response =>
            {
                if (response == null)
                {
                    Debug.LogError($"RoadPlanManager: パス{existingPathId}の取得に失敗しました");
                    return;
                }

                points.Clear();
                Curved = response.curved && IsCurveAllowed();
                // 曲線パスの編集中も「直線延長」吸着を自動でONにする(新規敷設と同じ方針)。
                if (Curved)
                {
                    TerrainInputHandler.EnableStraightLineSnapping = true;
                }
                // 地形追従の個別上書きを復元する（スキーマ再生成前は VALUE_NONE=未設定になり、
                // バリアント既定に従う。height_editable=false のバリアントでは意味を持たない）。
                PathFollowTerrainOverride = response.GetFollowTerrain();

                // start_point を始点として追加
                // バックエンドの座標は [x, relativeY, z] であり、
                // Unity 空間での正しい Y は地形高さ + relativeY とする。
                var startList = GeneratedModelUtils.ToFloatList(response.start_point);
                if (startList != null && startList.Count >= 2)
                {
                    float startRelY = startList.Count > 2 ? startList[1] : 0f;
                    float startGroundY = Utils.GetGroundHeight(new Vector3(startList[0], 0f, startList[startList.Count - 1]));
                    Vector3 startPos = new Vector3(startList[0], startGroundY + startRelY, startList[startList.Count - 1]);
                    points.Add(new PathBuildPoint(startPos, PathPointRole.Start));
                }

                // waypoints を追加
                var waypointList = GeneratedModelUtils.ToWaypointList(response.waypoints);
                if (waypointList != null)
                {
                    foreach (var wp in waypointList)
                    {
                        if (wp.position != null && wp.position.Length >= 2)
                        {
                            float wpRelY = wp.position.Length > 2 ? wp.position[1] : 0f;
                            float wpGroundY = Utils.GetGroundHeight(new Vector3(wp.position[0], 0f, wp.position[wp.position.Length - 1]));
                            Vector3 wpPos = new Vector3(wp.position[0], wpGroundY + wpRelY, wp.position[wp.position.Length - 1]);
                            var pt = new PathBuildPoint(wpPos, PathPointRole.Waypoint);
                            if (wp.control_in != null && wp.control_in.Length >= 2)
                            {
                                float ciRelY = wp.control_in.Length > 2 ? wp.control_in[1] : 0f;
                                float ciGroundY = Utils.GetGroundHeight(new Vector3(wp.control_in[0], 0f, wp.control_in[wp.control_in.Length - 1]));
                                pt.control_in = new Vector3(wp.control_in[0], ciGroundY + ciRelY, wp.control_in[wp.control_in.Length - 1]);
                            }
                            if (wp.control_out != null && wp.control_out.Length >= 2)
                            {
                                float coRelY = wp.control_out.Length > 2 ? wp.control_out[1] : 0f;
                                float coGroundY = Utils.GetGroundHeight(new Vector3(wp.control_out[0], 0f, wp.control_out[wp.control_out.Length - 1]));
                                pt.control_out = new Vector3(wp.control_out[0], coGroundY + coRelY, wp.control_out[wp.control_out.Length - 1]);
                            }
                            points.Add(pt);
                        }
                    }
                }

                // end_point を終点として追加
                var endList = GeneratedModelUtils.ToFloatList(response.end_point);
                if (endList != null && endList.Count >= 2)
                {
                    float endRelY = endList.Count > 2 ? endList[1] : 0f;
                    float endGroundY = Utils.GetGroundHeight(new Vector3(endList[0], 0f, endList[endList.Count - 1]));
                    Vector3 endPos = new Vector3(endList[0], endGroundY + endRelY, endList[endList.Count - 1]);
                    var endPt = new PathBuildPoint(endPos, PathPointRole.End);
                    points.Add(endPt);
                }

                // 端点の制御点(start_control / end_control)を復元する。
                // ウェイポイントの制御点と同じ [x, 相対高さ, z] 形式で保存されている。
                // スキーマ再生成前はレスポンスにフィールドが無く null になるため、
                // その場合は後段の自動計算に任せる。
                var startControlList = response.GetStartControlList();
                if (startControlList != null && startControlList.Count >= 2 && points.Count >= 1)
                {
                    float scRelY = startControlList.Count > 2 ? startControlList[1] : 0f;
                    float scGroundY = Utils.GetGroundHeight(new Vector3(startControlList[0], 0f, startControlList[startControlList.Count - 1]));
                    var startPt = points[0];
                    startPt.control_out = new Vector3(startControlList[0], scGroundY + scRelY, startControlList[startControlList.Count - 1]);
                    points[0] = startPt;
                }
                var endControlList = response.GetEndControlList();
                if (endControlList != null && endControlList.Count >= 2 && points.Count >= 1)
                {
                    float ecRelY = endControlList.Count > 2 ? endControlList[1] : 0f;
                    float ecGroundY = Utils.GetGroundHeight(new Vector3(endControlList[0], 0f, endControlList[endControlList.Count - 1]));
                    var endPtRef = points[points.Count - 1];
                    endPtRef.control_in = new Vector3(endControlList[0], ecGroundY + ecRelY, endControlList[endControlList.Count - 1]);
                    points[points.Count - 1] = endPtRef;
                }

                // バックエンドから受信した盛土オフセットを各ポイントに復元
                // embankment_heights は [始点, ウェイポイント..., 終点] の順
                var embankmentList = GeneratedModelUtils.ToFloatList(response.embankment_heights);
                if (embankmentList != null && embankmentList.Count > 0)
                {
                    for (int i = 0; i < points.Count && i < embankmentList.Count; i++)
                    {
                        var pt = points[i];
                        pt.embankmentHeight = embankmentList[i];
                        points[i] = pt;
                    }
                }

                if (points.Count >= 2)
                {
                    phase = RoadEditorPhase.Editing;
                    InvalidateShapeCache();

                    // RecomputeAllControlPoints は全制御点を無条件に上書きするため、
                    // バックエンドに保存済みの制御点(ユーザーがドラッグで調整した曲線)を
                    // 退避してから自動計算を実行し、保存値がある箇所だけ上書き復元する。
                    // 保存値が無い箇所(スキーマ再生成前の端点など)は自動計算値のまま残る。
                    var savedControlIn = new Vector3?[points.Count];
                    var savedControlOut = new Vector3?[points.Count];
                    for (int i = 0; i < points.Count; i++)
                    {
                        savedControlIn[i] = points[i].control_in;
                        savedControlOut[i] = points[i].control_out;
                    }

                    RecomputeAllControlPoints();

                    for (int i = 0; i < points.Count; i++)
                    {
                        var pt = points[i];
                        if (savedControlIn[i].HasValue) pt.control_in = savedControlIn[i];
                        if (savedControlOut[i].HasValue) pt.control_out = savedControlOut[i];
                        points[i] = pt;
                    }

                    Update3DPreview();
                    EmitShapeUpdate();
                    Debug.Log($"RoadPlanManager: 既存パス{existingPathId}をロードしました ({points.Count}点) → Editingフェーズ");
                }
                else
                {
                    Debug.LogWarning($"RoadPlanManager: パス{existingPathId}の形状データが不十分です。新規作成モードで開始します。");
                }
            },
            error => Debug.LogError($"RoadPlanManager: パス{existingPathId}の取得失敗: {error}")
        );
    }

    public void FinishRoadPlacement()
    {
        phase = RoadEditorPhase.Inactive;
        points.Clear();
        selectedPointIndex = -1;
        hoveredEdgeIndex = -1;
        isDragging = false;
        dragTarget = DragTarget.None;
        currentHeight = 0f;
        currentEmbankmentHeight = 0f;
        InvalidateShapeCache();
        existingElevSegCache.Clear();

        if (overlayRenderer != null)
        {
            overlayRenderer.ClearMesh();
        }
        if (backgroundRenderer != null)
        {
            backgroundRenderer.ClearMesh();
            bgDirty = true;
        }
        if (roadRenderer != null)
        {
            roadRenderer.ResetPreview();
            roadRenderer.CreatePreviewMeshComponents(previewMaterial);
            roadRenderer.errorPreviewMaterial = errorPreviewMaterial;
        }
    }

    // =====================================================================
    // パブリックAPI: 入力ハンドラ（TerrainInputHandlerから呼ばれる）
    // =====================================================================

    /// <summary>
    /// 地形左クリック。デバッグ用HTMLの handleLeftClick に対応。
    /// Placing: 始点/ウェイポイントを追加
    /// Editing: 制御点・ポイントのドラッグ開始、または辺上にウェイポイント挿入
    /// </summary>
    public void OnTerrainLeftClick(Vector3 worldPos)
    {
        if (phase == RoadEditorPhase.Placing)
        {
            Vector3 snapped = ApplySnapping(worldPos);
            // 高架のノード/パスへ吸着した場合はその標高を引き継ぐ（ResolvePlacementHeight 内で
            // currentHeight とUIの標高表示にも同期する）。それ以外は currentHeight を使う。
            snapped.y = Utils.GetGroundHeight(snapped) + ResolvePlacementHeight();

            if (points.Count == 0)
            {
                var pt = new PathBuildPoint(snapped, PathPointRole.Start);
                pt.embankmentHeight = currentEmbankmentHeight;
                points.Add(pt);
                Debug.Log($"RoadPlanManager: 始点配置 {snapped}");
            }
            else
            {
                var pt = new PathBuildPoint(snapped, PathPointRole.Waypoint);
                pt.embankmentHeight = currentEmbankmentHeight;
                points.Add(pt);
                Debug.Log($"RoadPlanManager: ウェイポイント追加 {snapped} (total: {points.Count})");
            }

            InvalidateShapeCache();
            Update3DPreview();
            EmitShapeUpdate();
            PlaySound(placePointSound);
        }
        else if (phase == RoadEditorPhase.Editing)
        {
            HandleEditingClick(worldPos);
        }
    }

    /// <summary>
    /// 地形右クリック。デバッグ用HTMLの handleRightClick に対応。
    /// Placing: 形状を確定してEditingフェーズに移行
    /// Editing: ポイントを削除
    /// </summary>
    public void OnTerrainRightClick(Vector3 worldPos)
    {
        if (phase == RoadEditorPhase.Placing)
        {
            FinalizePath(worldPos);
        }
        else if (phase == RoadEditorPhase.Editing)
        {
            HandleEditingRightClick(worldPos);
        }
    }

    /// <summary>
    /// 地形ホバー。プレビュー線の表示に使用する。
    /// </summary>
    public void OnTerrainHover(Vector3 worldPos)
    {
        if (phase == RoadEditorPhase.Placing)
        {
            hoverWorldPosition = ApplySnapping(worldPos);
            // 高架エッジへ吸着中はホバープレビュー線もその標高で描くため保存する
            hoverSnapWasElevated = lastSnapWasElevated && IsHeightEditable();
            hoverSnapRelHeight = lastSnapRelHeight;
            InvalidateShapeCache();
            Update3DPreview();
        }
        else if (phase == RoadEditorPhase.Editing)
        {
            hoverWorldPosition = worldPos;
        }
    }

    /// <summary>
    /// InputHandlerからのドラッグ通知。Editingフェーズでポイントまたは制御点を移動する。
    ///
    /// worldPos は InputHandler によってカメラレイと地面（地形または y=0 平面）との交点として計算されており、
    /// どの角度から見下ろしていてもマウスカーソルに対応する正確な地面座標を表す。
    /// ここではその値を直接利用し、ScreenToWorldPoint による固定深度変換は行わない。
    /// </summary>
    public void ProcessMouseDrag(Vector3 worldPos)
    {
        if (phase != RoadEditorPhase.Editing) return;

        // ドラッグ未開始の場合、現在のマウス位置でドラッグ対象を決定する。
        // InputHandler がマウスダウンとドラッグを区別するようになったため、
        // ドラッグ確定時には既に HandleEditingClick が呼ばれていない可能性がある。
        if (!isDragging)
        {
            Camera cam = gameCamera != null ? gameCamera : Camera.main;
            if (cam == null) return;

            Vector2 mouseScreen = InputUtil.MousePosition;
            RoadOverlayHit hit = hitDetector.TestHit(mouseScreen, points, cam);

            if (hit.type == HitTargetType.ControlOut)
            {
                isDragging = true;
                dragTarget = DragTarget.ControlOut;
                dragPointIndex = hit.index;
            }
            else if (hit.type == HitTargetType.ControlIn)
            {
                isDragging = true;
                dragTarget = DragTarget.ControlIn;
                dragPointIndex = hit.index;
            }
            else if (hit.type == HitTargetType.Point)
            {
                selectedPointIndex = hit.index;
                isDragging = true;
                dragTarget = DragTarget.Point;
                dragPointIndex = hit.index;
                EmitSelectNode();
            }
            else
            {
                // ドラッグ対象がない場合は何もしない
                return;
            }
        }

        // InputHandler から渡された座標は Y=0 のため、実際の地面高さに合わせる
        Vector3 newWorldPos = worldPos;
        newWorldPos.y = Utils.GetGroundHeight(newWorldPos);

        if (dragTarget == DragTarget.Point)
        {
            if (dragPointIndex < 0 || dragPointIndex >= points.Count) return;

            // ポイント移動時もスナップを適用する（グリッド/ノード/パス/直角スナップ）
            Vector3 snappedPos = ApplySnapping(newWorldPos);

            var pt = points[dragPointIndex];
            // XZ移動時に相対高さを維持する
            float relY = pt.position.y - Utils.GetGroundHeight(pt.position);
            snappedPos.y = Utils.GetGroundHeight(snappedPos) + relY;
            pt.position = snappedPos;
            points[dragPointIndex] = pt;

            // ポイント移動に追従して全制御点を再計算
            RecomputeAllControlPoints();
        }
        else if (dragTarget == DragTarget.ControlIn)
        {
            if (dragPointIndex < 0 || dragPointIndex >= points.Count) return;
            var pt = points[dragPointIndex];
            // 制御点のYはアンカーの相対高さ（標高）を維持する。地面高さへ潰すと
            // 高架パスでアンカーだけ高く制御点が地面に落ち、ベジエが垂れ下がる（吊り橋状）。
            // 地上道路では anchorRelY≈0 のため従来と同一挙動。
            float anchorRelY = pt.position.y - Utils.GetGroundHeight(pt.position);
            Vector3 nc = worldPos;
            nc.y = Utils.GetGroundHeight(nc) + anchorRelY;
            pt.control_in = nc;
            points[dragPointIndex] = pt;
            // 反対側(control_out)を頂点を挟んで一直線に保ち、道路が折れないようにする
            EnforceCollinearControls(dragPointIndex, movedControlOut: false);
        }
        else if (dragTarget == DragTarget.ControlOut)
        {
            if (dragPointIndex < 0 || dragPointIndex >= points.Count) return;
            var pt = points[dragPointIndex];
            // 制御点のYはアンカーの相対高さ（標高）を維持する（ControlIn と同様）。
            float anchorRelY = pt.position.y - Utils.GetGroundHeight(pt.position);
            Vector3 nc = worldPos;
            nc.y = Utils.GetGroundHeight(nc) + anchorRelY;
            pt.control_out = nc;
            points[dragPointIndex] = pt;
            // 反対側(control_in)を頂点を挟んで一直線に保ち、道路が折れないようにする
            EnforceCollinearControls(dragPointIndex, movedControlOut: true);
        }

        InvalidateShapeCache();
        Update3DPreview();
    }

    /// <summary>
    /// InputHandlerからのドラッグ終了通知。
    /// </summary>
    public void ProcessMouseDragEnd(Vector3 worldPos)
    {
        if (isDragging && phase == RoadEditorPhase.Editing)
        {
            isDragging = false;
            dragPointIndex = -1;
            dragTarget = DragTarget.None;
            EmitShapeUpdate();
        }
    }

    /// <summary>
    /// 指定スクリーン座標が編集中のポイントまたは制御点上にあるか判定する。
    /// CameraController との入力優先競合を解決するために使用する。
    /// </summary>
    public bool IsMouseOverEditHandle(Vector2 screenPos, Camera cam)
    {
        // [DIAG] ドラッグ掴み判定の実態を可視化する一時ログ。原因特定後に削除する。
        // 計画中パス編集で「掴めない/カメラが動く」場合、ここで phase・points 数・hit 種別を確認する。
        if (!IsEditMode)
        {
            Debug.Log($"[DIAG] IsMouseOverEditHandle: phase={phase} points={points.Count} → false (not Editing)");
            return false;
        }
        if (cam == null) return false;

        RoadOverlayHit hit = hitDetector.TestHit(screenPos, points, cam);
        bool over = hit.type == HitTargetType.Point ||
                    hit.type == HitTargetType.ControlIn ||
                    hit.type == HitTargetType.ControlOut;
        Debug.Log($"[DIAG] IsMouseOverEditHandle: phase={phase} points={points.Count} hit={hit.type} cam={(cam != null ? cam.name : "null")} → {over}");
        return over;
    }

    // =====================================================================
    // 形状確定（Placing → Editing の移行）
    // =====================================================================

    /// <summary>
    /// 配置モードを終了し、形状を確定する。
    /// ウェイポイントがあれば最後のウェイポイントを終点に昇格する。
    /// ウェイポイントがない場合は右クリック位置を終点とする。
    /// 確定後に全ポイントの制御点を自動計算する。
    /// </summary>
    private void FinalizePath(Vector3 rightClickPos)
    {
        if (points.Count == 0) return;

        if (points.Count == 1)
        {
            Vector3 endPos = ApplySnapping(rightClickPos);
            // 高架のノード/パスへ吸着した場合はその標高を引き継ぐ
            endPos.y = Utils.GetGroundHeight(endPos) + ResolvePlacementHeight();
            var endPt = new PathBuildPoint(endPos, PathPointRole.End);
            endPt.embankmentHeight = currentEmbankmentHeight;
            points.Add(endPt);
        }
        else
        {
            var last = points[points.Count - 1];
            last.role = PathPointRole.End;
            points[points.Count - 1] = last;
        }

        phase = RoadEditorPhase.Editing;
        selectedPointIndex = -1;
        hoveredEdgeIndex = -1;

        InvalidateShapeCache();
        RecomputeAllControlPoints();
        Update3DPreview();
        // finalized=true で送り、UI側に権威あるバックエンド干渉問い合わせを促す。
        EmitShapeUpdate(finalized: true);
        PlaySound(finalizeShapeSound);
        Debug.Log($"RoadPlanManager: 形状確定 ({points.Count}点) → Editingフェーズ");
    }

    /// <summary>
    /// 全ポイントの制御点を3次元Catmull-Romスプラインの接線ベクトルから計算する。
    ///
    /// 接線ベクトル V_i = P_{i+1} - P_{i-1}、テンション=1/6。
    /// 端点は片側の隣接アンカーとのベクトルを使用する。
    /// これによりX・Y・Zすべての軸で滑らかなベジェ曲線が得られる。
    /// </summary>
    private void RecomputeAllControlPoints()
    {
        if (points.Count < 2) return;

        int n = points.Count;

        for (int i = 0; i < n; i++)
        {
            var pt = points[i];

            // control_out: このポイントから次へ向かう出口制御点
            if (pt.role != PathPointRole.End)
            {
                Vector3 curr = pt.position;
                Vector3 next = points[i + 1].position;
                Vector3 prev = i > 0 ? points[i - 1].position : curr;

                Vector3 tangent;
                if (i == 0)
                {
                    tangent = next - curr;
                }
                else
                {
                    tangent = next - prev;
                }

                pt.control_out = curr + tangent * (1f / 6f);
            }

            // control_in: 前のポイントからこのポイントへ入る入口制御点
            if (pt.role != PathPointRole.Start)
            {
                Vector3 curr = pt.position;
                Vector3 prev = points[i - 1].position;
                Vector3 after = i < n - 1 ? points[i + 1].position : curr;

                Vector3 tangent;
                if (i == n - 1)
                {
                    tangent = curr - prev;
                }
                else
                {
                    tangent = after - prev;
                }

                pt.control_in = curr - tangent * (1f / 6f);
            }

            points[i] = pt;
        }

        // 始点・終点が既存パスの終端に接続している場合、その端点制御点を
        // 既存パスの延長線方向へ吸着させ、接続部で道路が折れないようにする。
        AlignEndpointControlsToExistingPaths();
    }

    /// <summary>
    /// 指定ポイントの2本のベジエ制御点(control_in / control_out)を常に一直線（180度）に保つ。
    /// 制御点をドラッグしたとき反対側の制御点を頂点を挟んだ反対方向へ再配置し、
    /// 頂点で道路が折れないようにする（フロント側の親切設計。バックエンド検証は不要）。
    /// 反対側ハンドルの長さ（アンカーからの距離）は維持し、向きだけを反転させる。
    /// 両方の制御点を持つポイント（ウェイポイント）でのみ機能する。
    /// 始点/終点は片側しか持たないため何もしない。
    /// XZ平面上で一直線にし、Y座標はアンカーの相対高さ（標高）を維持する
    /// （地面高さへ潰すと高架パスで制御点だけ地面に落ちベジエが垂れ下がる）。
    /// </summary>
    /// <param name="index">対象ポイントのインデックス</param>
    /// <param name="movedControlOut">true なら control_out を動かした（control_in を追従させる）、false ならその逆</param>
    private void EnforceCollinearControls(int index, bool movedControlOut)
    {
        if (index < 0 || index >= points.Count) return;
        var pt = points[index];
        if (!pt.control_in.HasValue || !pt.control_out.HasValue) return;

        Vector3 anchor = pt.position;
        float anchorRelY = anchor.y - Utils.GetGroundHeight(anchor);

        if (movedControlOut)
        {
            Vector3 outDir = pt.control_out.Value - anchor;
            outDir.y = 0f;
            if (outDir.sqrMagnitude < 1e-6f) return;
            Vector3 inVec = pt.control_in.Value - anchor;
            inVec.y = 0f;
            float inLen = inVec.magnitude;
            Vector3 newIn = anchor - outDir.normalized * inLen;
            newIn.y = Utils.GetGroundHeight(newIn) + anchorRelY;
            pt.control_in = newIn;
        }
        else
        {
            Vector3 inDir = pt.control_in.Value - anchor;
            inDir.y = 0f;
            if (inDir.sqrMagnitude < 1e-6f) return;
            Vector3 outVec = pt.control_out.Value - anchor;
            outVec.y = 0f;
            float outLen = outVec.magnitude;
            Vector3 newOut = anchor - inDir.normalized * outLen;
            newOut.y = Utils.GetGroundHeight(newOut) + anchorRelY;
            pt.control_out = newOut;
        }

        points[index] = pt;
    }

    /// <summary>
    /// 始点・終点が既存パスの終端に接続している場合、その端点のベジエ制御点を
    /// 既存パスの延長線方向へ吸着させ、接続部で道路が折れないようにする（フロント側の親切設計）。
    /// 「直線延長」吸着フラグ(TerrainInputHandler.EnableStraightLineSnapping)がONのときのみ
    /// 適用する（曲線モードON時に自動でONになる。OFFにすれば描いた方向がそのまま使われる）。
    /// 自動計算された接線と延長線の角度差が PATH_TANGENT_ALIGN_MAX_ANGLE_DEG を超える場合は、
    /// 明らかに別方向へ引いたものとみなして整列せずユーザーの方向を尊重する。
    /// RecomputeAllControlPoints の自動計算結果を上書きする形で適用し、
    /// 制御点の長さ（アンカーからの距離）は自動計算値を維持して向きだけを揃える。
    /// 確定送信される制御点にもこの結果が載るため、バックエンドに保存される形状と一致する。
    /// </summary>
    private void AlignEndpointControlsToExistingPaths()
    {
        if (!TerrainInputHandler.EnableStraightLineSnapping) return;
        if (points.Count < 2) return;

        // 始点: control_out を延長方向へ吸着
        {
            var pt = points[0];
            if (pt.control_out.HasValue)
            {
                Vector3 cur = pt.control_out.Value - pt.position;
                cur.y = 0f;
                float len = cur.magnitude;
                if (len > 1e-4f
                    && TryGetExistingPathExtensionDir(pt.position, cur, out Vector3 aligned))
                {
                    // 制御点のYはアンカーの相対高さ（標高）を維持する。地面へ潰すと
                    // 高架接続部で端点ハンドルが地面に落ちベジエが垂れ下がる。
                    float anchorRelY = pt.position.y - Utils.GetGroundHeight(pt.position);
                    Vector3 newOut = pt.position + aligned * len;
                    newOut.y = Utils.GetGroundHeight(newOut) + anchorRelY;
                    pt.control_out = newOut;
                    points[0] = pt;
                }
            }
        }

        // 終点: control_in を延長方向へ吸着
        {
            int last = points.Count - 1;
            var pt = points[last];
            if (pt.control_in.HasValue)
            {
                Vector3 cur = pt.control_in.Value - pt.position;
                cur.y = 0f;
                float len = cur.magnitude;
                if (len > 1e-4f
                    && TryGetExistingPathExtensionDir(pt.position, cur, out Vector3 aligned))
                {
                    // 制御点のYはアンカーの相対高さ（標高）を維持する（始点側と同様）。
                    float anchorRelY = pt.position.y - Utils.GetGroundHeight(pt.position);
                    Vector3 newIn = pt.position + aligned * len;
                    newIn.y = Utils.GetGroundHeight(newIn) + anchorRelY;
                    pt.control_in = newIn;
                    points[last] = pt;
                }
            }
        }
    }

    /// <summary>
    /// 指定ワールド位置(XZ)に端点を持つ既存パスセグメントを探し、handleDir（自動計算された
    /// 端点ハンドル方向）に最も角度が近い延長方向（XZ平面・正規化・±の向き解決済み）を返す。
    /// 新規パスの端点が既存パスの終端に接続するとき、接続部を滑らかにするために
    /// 端点の制御点をこの方向へ吸着させる用途で使う。
    ///
    /// - pos が端点にほぼ一致するセグメントのみ候補とする（中間で交差しているだけのパスは無視）。
    /// - 網互換でない相手・編集中パス自身のセグメントは除外する（自分の方向に吸着して折り返すのを防ぐ）。
    /// - 候補が複数ある場合（交差点への接続など）は handleDir との角度差が最小のものを採用する。
    /// - 最小角度差が PATH_TANGENT_ALIGN_MAX_ANGLE_DEG を超える場合は false
    ///   （ユーザーが明らかに別方向へ引いており、整列すると描いた曲線を壊すため）。
    /// - 延長線の向き(±)は handleDir と同じ側（内積が正の側）を採用し、意図せぬ折り返しを防ぐ。
    /// </summary>
    private bool TryGetExistingPathExtensionDir(Vector3 pos, Vector3 handleDir, out Vector3 extensionDir)
    {
        extensionDir = Vector3.zero;
        if (pathManager == null) return false;

        Vector3 handleFlat = new Vector3(handleDir.x, 0f, handleDir.z);
        if (handleFlat.sqrMagnitude < 1e-8f) return false;
        handleFlat.Normalize();

        var segs = pathManager.QueryNearbySegments(pos, snapThreshold);
        if (segs == null) return false;

        float bestAngle = float.MaxValue;
        bool found = false;
        foreach (var seg in segs)
        {
            // 網互換でない相手（別タイプ・別網のインフラパス等）は接続対象ではないため無視
            if (seg.path != null && !InfraNetworkUtil.IsPathSnappable(seg.path.path_variant, PathSubtype)) continue;
            // 編集中のパス自身のセグメントは除外（自分の方向へ吸着すると折り返してしまう）
            if (seg.path != null && pathId != 0 && seg.path.path_id == pathId) continue;

            float dA = Vector2.Distance(new Vector2(seg.a.x, seg.a.z), new Vector2(pos.x, pos.z));
            float dB = Vector2.Distance(new Vector2(seg.b.x, seg.b.z), new Vector2(pos.x, pos.z));

            Vector3 near, far;
            float endpointDist;
            if (dA <= dB) { near = seg.a; far = seg.b; endpointDist = dA; }
            else { near = seg.b; far = seg.a; endpointDist = dB; }

            // pos がセグメントの端点にほぼ一致する場合のみ延長方向の候補とする
            if (endpointDist >= snapThreshold) continue;

            Vector3 dir = new Vector3(near.x - far.x, 0f, near.z - far.z);
            if (dir.sqrMagnitude < 1e-6f) continue;
            dir.Normalize();

            Vector3 candidate = Vector3.Dot(handleFlat, dir) >= 0f ? dir : -dir;
            float angle = Vector3.Angle(handleFlat, candidate);
            if (angle <= Constants.PATH_TANGENT_ALIGN_MAX_ANGLE_DEG && angle < bestAngle)
            {
                bestAngle = angle;
                extensionDir = candidate;
                found = true;
            }
        }
        return found;
    }

    // =====================================================================
    // Editingフェーズの入力処理
    // =====================================================================

    private void HandleEditingClick(Vector3 worldPos)
    {
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        Vector2 mouseScreen = InputUtil.MousePosition;
        RoadOverlayHit hit = hitDetector.TestHit(mouseScreen, points, cam);

        // クリック時はドラッグ状態を設定しない。
        // ドラッグ開始は ProcessMouseDrag 内で isDragging == false の際に
        // ハンドルを再検出して行う。これによりクリックとドラッグの
        // 状態管理が InputHandler 側のドラッグ閾値判定と整合する。
        if (hit.type == HitTargetType.Point)
        {
            selectedPointIndex = hit.index;
            EmitSelectNode();
        }
        else if (hit.type == HitTargetType.Edge)
        {
            InsertPointOnEdge(hit.index, worldPos);
        }
        else
        {
            selectedPointIndex = -1;
        }
    }

    private void HandleEditingRightClick(Vector3 worldPos)
    {
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        Vector2 mouseScreen = InputUtil.MousePosition;
        RoadOverlayHit hit = hitDetector.TestHit(mouseScreen, points, cam);

        // 制御点上の右クリックは無視する
        if (hit.type == HitTargetType.ControlIn || hit.type == HitTargetType.ControlOut)
            return;

        if (hit.type == HitTargetType.Point && points.Count > 2)
        {
            RemovePoint(hit.index);
        }
    }

    // =====================================================================
    // ポイント操作
    // =====================================================================

    /// <summary>
    /// 辺上にウェイポイントを挿入する。挿入後に制御点を再計算する。
    /// </summary>
    private void InsertPointOnEdge(int edgeIndex, Vector3 worldPos)
    {
        if (edgeIndex < 0 || edgeIndex >= points.Count - 1) return;

        worldPos.y = Utils.GetGroundHeight(worldPos) + currentHeight;
        var insertedPt = new PathBuildPoint(worldPos, PathPointRole.Waypoint);
        insertedPt.embankmentHeight = currentEmbankmentHeight;
        points.Insert(edgeIndex + 1, insertedPt);

        if (selectedPointIndex > edgeIndex) selectedPointIndex++;

        InvalidateShapeCache();
        RecomputeAllControlPoints();
        Update3DPreview();
        EmitShapeUpdate();
        Debug.Log($"RoadPlanManager: 辺{edgeIndex}にウェイポイント挿入 (total: {points.Count})");
    }

    /// <summary>
    /// ポイントを削除する。常に最低2点（始点+終点）は維持する。
    /// 削除後に制御点を再計算する。
    /// </summary>
    private void RemovePoint(int index)
    {
        if (points.Count <= 2) return;
        if (index < 0 || index >= points.Count) return;

        PathPointRole removedRole = points[index].role;

        if (removedRole == PathPointRole.Start)
        {
            points.RemoveAt(0);
            var first = points[0];
            first.role = PathPointRole.Start;
            points[0] = first;
        }
        else if (removedRole == PathPointRole.End)
        {
            points.RemoveAt(points.Count - 1);
            var last = points[points.Count - 1];
            last.role = PathPointRole.End;
            points[points.Count - 1] = last;
        }
        else
        {
            points.RemoveAt(index);
        }

        if (selectedPointIndex == index) selectedPointIndex = -1;
        else if (selectedPointIndex > index) selectedPointIndex--;

        InvalidateShapeCache();
        RecomputeAllControlPoints();
        Update3DPreview();
        EmitShapeUpdate();
        Debug.Log($"RoadPlanManager: ポイント{index}を削除 (残り: {points.Count})");
    }

    // =====================================================================
    // ホバー状態更新
    // =====================================================================

    private void UpdateHoverState()
    {
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        Vector2 mouseScreen = InputUtil.MousePosition;
        RoadOverlayHit hit = hitDetector.TestHit(mouseScreen, points, cam);

        hoveredEdgeIndex = (hit.type == HitTargetType.Edge) ? hit.index : -1;
    }

    // =====================================================================
    // スナップ
    // =====================================================================

    // 直角スナップ補助線描画用の一時状態
    private bool isRightAngleSnapActive;
    private Vector3 rightAngleSnapOrigin;
    private Vector3 rightAngleSnapTarget;
    private Vector3 rightAngleSnapSnappedPos;

    // 直線延長スナップ補助線描画用の一時状態
    private bool isStraightLineSnapActive;
    private Vector3 straightLineSnapAnchor;
    private Vector3 straightLineSnapExtension;
    private Vector3 straightLineSnapSnappedPos;

    // 直近の ApplySnapping が高架のノード/パス/建設中端点へ吸着したか、その相対高さ（標高）。
    // 高架エッジに吸着したとき配置ノードの高さを引き継ぐために使う。
    // グリッド/直線延長/直角スナップなど Y を地面へ潰す経路では false にリセットされる。
    private bool lastSnapWasElevated;
    private float lastSnapRelHeight;
    // ホバープレビュー線を吸着高さで接続するための保存値
    private bool hoverSnapWasElevated;
    private float hoverSnapRelHeight;

    /// <summary>
    /// 直線延長スナップを適用する。
    ///
    /// Placingフェーズ: 直前の2点の延長線上に吸着する。
    ///   P[n-2] → P[n-1] の方向ベクトルを延長し、basePos をその延長線上の
    ///   最も近い点に射影する。
    ///
    /// Editingフェーズ: 現在ドラッグ中のポイントの隣接2点を基準に吸着する。
    ///   - 先頭以外のポイント: 手前2点の延長線、または後2点の延長線、
    ///     または中間点の場合は両脇のノードを結ぶ線分への射影
    ///   - 基準線上の最も近い点がスナップ閾値内であれば吸着する。
    ///
    /// XZ平面（水平面）で計算し、Y座標は地形高さを反映する。
    /// </summary>
    private Vector3 ApplyStraightLineSnap(Vector3 basePos)
    {
        isStraightLineSnapActive = false;

        if (phase == RoadEditorPhase.Placing)
        {
            // Placingフェーズ: 直前の2点の延長線
            return ApplyPlacingStraightLineSnap(basePos);
        }
        else if (phase == RoadEditorPhase.Editing)
        {
            // Editingフェーズ: ドラッグ中ポイントの隣接ノードを基準
            return ApplyEditingStraightLineSnap(basePos);
        }

        return basePos;
    }

    /// <summary>
    /// Placingフェーズでの直線延長スナップ。
    /// 直前の2点(points[n-2], points[n-1])の方向ベクトルを無限に延長し、
    /// basePos からその延長線への射影点を求める。射影点とのXZ距離が
    /// snapThreshold以内であればその点に吸着する。
    /// </summary>
    private Vector3 ApplyPlacingStraightLineSnap(Vector3 basePos)
    {
        if (points.Count < 2) return basePos;

        Vector3 pA = points[points.Count - 2].position;
        Vector3 pB = points[points.Count - 1].position;

        // 延長線の方向ベクトル（XZ平面）
        Vector3 dir = new Vector3(pB.x - pA.x, 0f, pB.z - pA.z);
        if (dir.sqrMagnitude < 1e-6f) return basePos;

        // basePos から延長線への射影。延長線は pB を通る dir 方向の半直線ではなく
        // 無限直線とし、どこでも吸着可能とする（制約が厳しすぎると使いにくいため）。
        Vector3 toBase = new Vector3(basePos.x - pB.x, 0f, basePos.z - pB.z);
        float t = Vector3.Dot(toBase, dir.normalized);
        Vector3 projected = pB + dir.normalized * t;
        projected.y = Utils.GetGroundHeight(projected);

        float dist = Vector2.Distance(
            new Vector2(basePos.x, basePos.z),
            new Vector2(projected.x, projected.z)
        );

        if (dist <= snapThreshold)
        {
            isStraightLineSnapActive = true;
            straightLineSnapAnchor = pB;
            straightLineSnapExtension = pA;
            straightLineSnapSnappedPos = projected;
            return projected;
        }

        return basePos;
    }

    /// <summary>
    /// Editingフェーズでの直線延長スナップ。
    /// 現在ドラッグ対象のインデックス(dragPointIndex)に基づいて、
    /// 適切な基準線を決定し吸着する。
    ///
    /// - 先頭ポイント(0): 次の2点(points[1], points[2])の延長線に吸着
    /// - 末尾ポイント(n-1): 手前2点(points[n-3], points[n-2])の延長線に吸着
    /// - 中間ポイント: 前後の隣接ノード(points[i-1], points[i+1])を結ぶ線分に吸着。
    ///   また手前2点の延長線、後2点の延長線のうち最も近いものにも吸着。
    /// </summary>
    private Vector3 ApplyEditingStraightLineSnap(Vector3 basePos)
    {
        // ドラッグまたは選択中のポイントインデックス
        int idx = isDragging ? dragPointIndex : selectedPointIndex;
        if (idx < 0 || idx >= points.Count || points.Count < 2) return basePos;

        int n = points.Count;
        float bestDist = float.MaxValue;
        Vector3 bestSnapped = basePos;
        Vector3 bestAnchor = Vector3.zero;
        Vector3 bestExtension = Vector3.zero;
        bool found = false;

        // 候補1: 手前2点の延長線（idx >= 2 の場合）
        if (idx >= 2)
        {
            TrySnapToExtensionLine(basePos, points[idx - 2].position, points[idx - 1].position, ref bestDist, ref bestSnapped, ref bestAnchor, ref bestExtension, ref found);
        }

        // 候補2: 後2点の延長線（idx <= n - 3 の場合）
        if (idx <= n - 3)
        {
            TrySnapToExtensionLine(basePos, points[idx + 2].position, points[idx + 1].position, ref bestDist, ref bestSnapped, ref bestAnchor, ref bestExtension, ref found);
        }

        // 候補3: 中間点の場合、両脇のノードを結ぶ線分に射影
        if (idx > 0 && idx < n - 1)
        {
            Vector3 prev = points[idx - 1].position;
            Vector3 next = points[idx + 1].position;
            Vector3 segDir = new Vector3(next.x - prev.x, 0f, next.z - prev.z);
            float segLen = segDir.magnitude;
            if (segLen > 1e-6f)
            {
                Vector3 segDirNorm = segDir / segLen;
                Vector3 toBase = new Vector3(basePos.x - prev.x, 0f, basePos.z - prev.z);
                float t = Vector3.Dot(toBase, segDirNorm);
                // 線分の範囲内（0 <= t <= segLen）に射影
                t = Mathf.Clamp(t, 0f, segLen);
                Vector3 projected = prev + segDirNorm * t;
                projected.y = Utils.GetGroundHeight(projected);
                float dist = Vector2.Distance(
                    new Vector2(basePos.x, basePos.z),
                    new Vector2(projected.x, projected.z)
                );
                if (dist <= snapThreshold && dist < bestDist)
                {
                    bestDist = dist;
                    bestSnapped = projected;
                    bestAnchor = prev;
                    bestExtension = next;
                    found = true;
                }
            }
        }

        // 候補4: 先頭ポイントの場合、次の2点の延長線
        if (idx == 0 && n >= 3)
        {
            // points[1] → points[2] の方向だが、ポイント0は points[1] の手前にあるべき
            // なので points[2] → points[1] の方向（逆方向）の延長線に吸着
            TrySnapToExtensionLine(basePos, points[2].position, points[1].position, ref bestDist, ref bestSnapped, ref bestAnchor, ref bestExtension, ref found);
        }
        else if (idx == 0 && n == 2)
        {
            // 2点のみの場合: points[1] から points[0] の方向の延長線
            TrySnapToExtensionLine(basePos, points[1].position, points[0].position, ref bestDist, ref bestSnapped, ref bestAnchor, ref bestExtension, ref found);
        }

        // 候補5: 末尾ポイントの場合、手前2点の延長線
        if (idx == n - 1 && n >= 3)
        {
            TrySnapToExtensionLine(basePos, points[n - 3].position, points[n - 2].position, ref bestDist, ref bestSnapped, ref bestAnchor, ref bestExtension, ref found);
        }
        else if (idx == n - 1 && n == 2)
        {
            // 2点のみの場合: points[0] → points[1] の方向の延長線
            TrySnapToExtensionLine(basePos, points[0].position, points[n - 2].position, ref bestDist, ref bestSnapped, ref bestAnchor, ref bestExtension, ref found);
        }

        if (found)
        {
            isStraightLineSnapActive = true;
            straightLineSnapAnchor = bestAnchor;
            straightLineSnapExtension = bestExtension;
            straightLineSnapSnappedPos = bestSnapped;
            return bestSnapped;
        }

        return basePos;
    }

    /// <summary>
    /// 指定した2点(extB → extA)の方向の延長線上に basePos を射影し、
    /// スナップ閾値以内であれば候補として記録する。
    /// extA を通る (extA - extB) 方向の無限直線に射影する。
    /// </summary>
    private void TrySnapToExtensionLine(
        Vector3 basePos, Vector3 extB, Vector3 extA,
        ref float bestDist, ref Vector3 bestSnapped,
        ref Vector3 bestAnchor, ref Vector3 bestExtension,
        ref bool found)
    {
        Vector3 dir = new Vector3(extA.x - extB.x, 0f, extA.z - extB.z);
        if (dir.sqrMagnitude < 1e-6f) return;

        Vector3 dirNorm = dir.normalized;
        Vector3 toBase = new Vector3(basePos.x - extA.x, 0f, basePos.z - extA.z);
        float t = Vector3.Dot(toBase, dirNorm);
        Vector3 projected = extA + dirNorm * t;
        projected.y = Utils.GetGroundHeight(projected);

        float dist = Vector2.Distance(
            new Vector2(basePos.x, basePos.z),
            new Vector2(projected.x, projected.z)
        );

        if (dist <= snapThreshold && dist < bestDist)
        {
            bestDist = dist;
            bestSnapped = projected;
            bestAnchor = extA;
            bestExtension = extB;
            found = true;
        }
    }

    /// <summary>
    /// 指定位置に対してグリッド/ノード/パス/直角/直線延長スナップを適用する。
    /// TerrainInputHandler の共通スナップ処理を利用し、直角スナップと直線延長スナップは
    /// 道路敷設特有のロジックとして追加する。
    /// </summary>
    private Vector3 ApplySnapping(Vector3 basePos)
    {
        // まずTerrainInputHandlerの共通スナップ（グリッド/ノード/パス/ゾーン頂点）を適用。
        // 建設中バリアントを渡すことでインフラ網の互換性フィルタが働く
        // （道路はインフラノード/パスに吸着せず、インフラパスは同一網にのみ吸着する）。
        lastSnapWasElevated = false;
        lastSnapRelHeight = 0f;
        // terrainInputHandler は地形タイル上のコンポーネントで、LODによるタイルの生成・破棄で
        // Start() 時にキャッシュした参照が破棄済み(Unityの疑似null)になりうる。null なら都度
        // 再解決する。これを怠るとグリッド/ノード/パス吸着が丸ごと無効化される。
        // 吸着ロジックはインスタンス非依存(フラグはstatic・pathManagerとthreshold override付き)
        // なので、生きているどのハンドラを引いても結果は同じ。
        if (terrainInputHandler == null)
        {
            terrainInputHandler = FindAnyObjectByType<TerrainInputHandler>();
        }

        // 軌道複線(0以外の横オフセットあり)の敷設中は、ノード/パス吸着をオフセット考慮版に
        // 差し替える。素朴に中心線を既存線路へ吸着させると全線路が横にズレて接続できないため、
        // 「グループ内のいずれかの線路が吸着対象に乗る」中心線位置へ吸着させる。
        // グリッド吸着が有効な間は共通スナップと同じ序列でグリッドを最優先する。
        bool offsetSnapApplied = false;
        if (HasNonZeroLateralOffsets()
            && !(TerrainInputHandler.EnableGridSnapping && GridOverlay.Instance != null)
            && TryOffsetAwareRailSnap(basePos, out Vector3 offsetSnapped, out float offsetSnapRelHeight))
        {
            basePos = offsetSnapped;
            lastSnapWasElevated = true;
            lastSnapRelHeight = offsetSnapRelHeight;
            offsetSnapApplied = true;
        }

        // オフセット考慮吸着がヒットしたら共通のノード/パス吸着は行わない
        // (吸着後の中心線位置は既存線路から横オフセット分離れており、共通スナップに通すと
        //  その既存線路の中心線へ引き戻されてしまうため)。
        if (!offsetSnapApplied && terrainInputHandler != null)
        {
            basePos = terrainInputHandler.ApplySnapping(basePos, PathSubtype, out lastSnapWasElevated, out lastSnapRelHeight, snapThreshold);
        }

        // 直線延長スナップ（直角スナップより優先）
        if (TerrainInputHandler.EnableStraightLineSnapping && points.Count >= 2)
        {
            Vector3 straightSnapped = ApplyStraightLineSnap(basePos);
            if (isStraightLineSnapActive)
            {
                isRightAngleSnapActive = false;
                // 直線延長スナップは Y を地面へ潰すため高架高さの引き継ぎは無効化する
                lastSnapWasElevated = false;
                return straightSnapped;
            }
        }
        else
        {
            isStraightLineSnapActive = false;
        }

        // 直角スナップが無効、またはポイントが1つもない場合は共通スナップ結果を返す
        if (!TerrainInputHandler.EnableRightAngleSnapping || points.Count == 0)
        {
            isRightAngleSnapActive = false;
            return basePos;
        }

        // 直前のポイント（最後に配置済みのポイント）
        Vector3 lastPoint = points[points.Count - 1].position;

        // lastPoint と basePos のうち、少なくとも一方が既存パス/ノード上にあるかを判定
        // 判定方法: lastPoint または basePos の周辺に既存パスセグメントがあるか
        bool lastOnExisting = false;
        bool currentOnExisting = false;
        Vector3 existingSegA = Vector3.zero;
        Vector3 existingSegB = Vector3.zero;
        Vector3 existingSnapPoint = Vector3.zero;

        // lastPoint が既存パス/ノード上にあるか確認
        if (pathManager != null)
        {
            Vector3 snapResult;
            NodeData node = TerrainInputHandler.FindNearestNode(lastPoint, snapThreshold, pathManager, out snapResult, PathSubtype);
            if (node != null)
            {
                lastOnExisting = true;
                existingSnapPoint = snapResult;
                // ノードに接続するセグメントの方向を取得（最も近い網互換セグメント）
                if (TryGetFirstSnappableSegment(lastPoint, out Vector3 sa1, out Vector3 sb1))
                {
                    existingSegA = sa1;
                    existingSegB = sb1;
                }
            }
            else
            {
                GeneratedModels.PathSummary path;
                int segIdx;
                if (TerrainInputHandler.FindNearestPathSegment(lastPoint, snapThreshold, pathManager, out snapResult, out path, out segIdx, PathSubtype))
                {
                    lastOnExisting = true;
                    existingSnapPoint = snapResult;
                    if (TryGetFirstSnappableSegment(lastPoint, out Vector3 sa2, out Vector3 sb2))
                    {
                        existingSegA = sa2;
                        existingSegB = sb2;
                    }
                }
            }

            // basePos が既存パス/ノード上にあるか確認
            node = TerrainInputHandler.FindNearestNode(basePos, snapThreshold, pathManager, out snapResult, PathSubtype);
            if (node != null)
            {
                currentOnExisting = true;
                existingSnapPoint = snapResult;
                if (TryGetFirstSnappableSegment(basePos, out Vector3 sa3, out Vector3 sb3))
                {
                    existingSegA = sa3;
                    existingSegB = sb3;
                }
            }
            else
            {
                GeneratedModels.PathSummary path;
                int segIdx;
                if (TerrainInputHandler.FindNearestPathSegment(basePos, snapThreshold, pathManager, out snapResult, out path, out segIdx, PathSubtype))
                {
                    currentOnExisting = true;
                    existingSnapPoint = snapResult;
                    if (TryGetFirstSnappableSegment(basePos, out Vector3 sa4, out Vector3 sb4))
                    {
                        existingSegA = sa4;
                        existingSegB = sb4;
                    }
                }
            }
        }

        // どちらか一方が既存パス上にない場合は直角スナップしない
        if (!lastOnExisting && !currentOnExisting)
        {
            isRightAngleSnapActive = false;
            return basePos;
        }

        // 既存パスセグメントの方向ベクトル（XZ平面）
        Vector3 pathDir = existingSegB - existingSegA;
        pathDir.y = 0;
        if (pathDir.sqrMagnitude < 1e-6f)
        {
            isRightAngleSnapActive = false;
            return basePos;
        }
        pathDir.Normalize();

        // pathDir に垂直な方向（XZ平面）
        Vector3 perpDir = new Vector3(-pathDir.z, 0, pathDir.x);

        // 基準点: 既存パス上にある方の点
        Vector3 anchor = lastOnExisting ? lastPoint : basePos;
        Vector3 other = lastOnExisting ? basePos : lastPoint;

        // other から anchor に向かうベクトルを perpDir に射影
        Vector3 toOther = other - anchor;
        toOther.y = 0;
        float projLen = Vector3.Dot(toOther, perpDir);

        // スナップ後の位置 = anchor + perpDir * projLen
        Vector3 snapped = anchor + perpDir * projLen;
        snapped.y = Utils.GetGroundHeight(snapped);
        // 直角スナップは Y を地面へ潰すため高架高さの引き継ぎは無効化する
        lastSnapWasElevated = false;

        // 補助線描画用に状態を保存
        isRightAngleSnapActive = true;
        rightAngleSnapOrigin = anchor;
        rightAngleSnapTarget = other;
        rightAngleSnapSnappedPos = snapped;

        return snapped;
    }

    // =====================================================================
    // 軌道複線のオフセット考慮吸着
    // =====================================================================

    /// <summary>オフセット考慮吸着で点ターゲット(ノード/端点)の接線を探す近傍セグメント検索半径(m)。</summary>
    private const float OFFSET_SNAP_TANGENT_SEARCH_RADIUS = 2.0f;

    /// <summary>セグメント端点が点ターゲットに接しているとみなすXZ距離(m)。</summary>
    private const float OFFSET_SNAP_TOUCH_TOLERANCE = 0.5f;

    /// <summary>
    /// 軌道複線(0以外の横オフセットを持つ平行敷設)の最中かどうか。
    /// オフセット考慮吸着(TryOffsetAwareRailSnap)を使うかの判定。
    /// 単線([0]のみ)・道路・既存パス編集(空配列)は対象外で、従来の共通スナップを使う
    /// (単線はオフセット0なので両者は同じ結果になる)。
    /// </summary>
    private bool HasNonZeroLateralOffsets()
    {
        if (LateralOffsets == null) return false;
        foreach (float offset in LateralOffsets)
        {
            if (Mathf.Abs(offset) > 0.01f) return true;
        }
        return false;
    }

    /// <summary>
    /// 軌道複線敷設用のオフセット考慮吸着。
    ///
    /// 複線グループの内部表現は「グループ中心線」1本のポリラインだが、確定時には
    /// 平行な単線パス群に分解されて保存され、既存パス側に「グループ」の実体は残らない。
    /// そのため中心線をそのまま既存線路の中心線へ吸着させると、全線路が横に
    /// 半間隔ズレて1本も接続できない。ここでは吸着対象(ノード/建設中端点/セグメント)の
    /// 位置Eとその接線tから「グループ内のいずれかの線路(横オフセットo)がEにちょうど乗る
    /// 中心線位置 C = E ± perp(t)·o」を候補として列挙し、生カーソルに最も近い候補へ吸着する。
    ///
    /// - 既存グループの中心付近にカーソルを置くと、左右の線路それぞれから導かれる候補が
    ///   同じ位置に重なるため、同構成のグループ延長では全線路の端点が一致する。
    /// - 接線の向き(±t)は始点クリック時点では敷設方向が未確定で決められないため、
    ///   両符号の候補を列挙して実際の敷設方向に委ねる(対称なオフセット配列では同一集合)。
    /// - ノード系優先→セグメントの序列、網互換フィルタ、ノード/パス吸着フラグ、
    ///   高架相対高さの引き継ぎは共通スナップ(TerrainInputHandler.ApplySnapping)と揃える。
    ///   ゾーン頂点吸着だけは対象外(軌道敷設では実用場面がないため)。
    /// </summary>
    private bool TryOffsetAwareRailSnap(Vector3 basePos, out Vector3 snapped, out float snapRelHeight)
    {
        snapped = basePos;
        snapRelHeight = 0f;
        if (pathManager == null) return false;
        if (!TerrainInputHandler.EnableNodeSnapping && !TerrainInputHandler.EnablePathSnapping) return false;

        // 吸着対象は候補位置からオフセット分離れているため、検索半径を最大オフセットぶん広げる
        float maxOffset = 0f;
        foreach (float offset in LateralOffsets)
        {
            maxOffset = Mathf.Max(maxOffset, Mathf.Abs(offset));
        }
        float searchRadius = snapThreshold + maxOffset;

        float bestDist = snapThreshold;
        bool found = false;

        // ノード・建設中端点(共通スナップと同じくノード系を優先し、当たればセグメントは見ない)
        if (TerrainInputHandler.EnableNodeSnapping)
        {
            var nodes = pathManager.QueryNearbyNodes(basePos, searchRadius);
            if (nodes != null)
            {
                foreach (var node in nodes)
                {
                    if (!InfraNetworkUtil.IsNodeSnappable(node, PathSubtype, pathManager)) continue;
                    EvaluatePointTargetOffsetCandidates(basePos, node.GetAbsolutePosition(), node.GetRelativeHeight(), ref bestDist, ref snapped, ref snapRelHeight, ref found);
                }
            }

            // 建設が完了するまで端点にノードが作られないため、計画中/建設中パスの
            // start_point / end_point を直接判定対象とする(共通スナップの
            // FindNearestConstructionEndpoint と同じ理由)。
            EvaluateConstructionEndpointOffsetCandidates(basePos, searchRadius, ref bestDist, ref snapped, ref snapRelHeight, ref found);

            if (found) return true;
        }

        // パスセグメント(横オフセット分平行移動した線への射影として吸着)
        if (TerrainInputHandler.EnablePathSnapping)
        {
            var segments = pathManager.QueryNearbySegments(basePos, searchRadius);
            if (segments != null)
            {
                foreach (var seg in segments)
                {
                    if (seg.path != null && !InfraNetworkUtil.IsPathSnappable(seg.path.path_variant, PathSubtype)) continue;
                    Vector3 dir = seg.b - seg.a;
                    dir.y = 0f;
                    if (dir.sqrMagnitude < 1e-6f) continue;
                    dir.Normalize();
                    Vector3 perp = new Vector3(-dir.z, 0f, dir.x);
                    foreach (float offset in LateralOffsets)
                    {
                        for (int sign = -1; sign <= 1; sign += 2)
                        {
                            Vector3 shift = perp * (offset * sign);
                            Vector3 candidate = TerrainInputHandler.GetClosestPointOnSegment(seg.a - shift, seg.b - shift, basePos);
                            float dist = Vector2.Distance(new Vector2(basePos.x, basePos.z), new Vector2(candidate.x, candidate.z));
                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                snapped = candidate;
                                // 標高は元パス中心線上の対応点(candidate + shift)のアンカー相対Y補間から
                                // 引き継ぐ(描画Y−地形サンプルの逆算はノイズが乗るため使わない)
                                snapRelHeight = seg.path != null
                                    ? seg.path.GetRelativeHeightAtPoint(candidate.x + shift.x, candidate.z + shift.z)
                                    : 0f;
                                found = true;
                            }
                        }
                    }
                }
            }
        }

        return found;
    }

    /// <summary>
    /// 点ターゲット(既存ノード/建設中パス端点)に対するオフセット考慮吸着の候補を評価する。
    /// ターゲットに接するパスの接線ごとに候補 C = ターゲット ± perp(接線)·オフセット を列挙し、
    /// 生カーソルに最も近い候補で最良値を更新する。接線はターゲットの近傍セグメントのうち
    /// 端点が一致するものを使い(交差点では全アームを試す)、無ければ最寄りセグメントで代用する。
    /// </summary>
    private void EvaluatePointTargetOffsetCandidates(Vector3 basePos, Vector3 targetPos, float targetRelHeight, ref float bestDist, ref Vector3 bestPos, ref float bestRelHeight, ref bool found)
    {
        var segs = pathManager.QueryNearbySegments(targetPos, OFFSET_SNAP_TANGENT_SEARCH_RADIUS);
        if (segs == null || segs.Count == 0) return;

        // 標高はターゲット(ノード/端点)が保持する相対高さをそのまま引き継ぐ
        // (絶対Y−地形サンプルの逆算は高さ源の差がノイズとして乗る)
        float relHeight = targetRelHeight;
        Vector2 targetXZ = new Vector2(targetPos.x, targetPos.z);

        bool anyTouching = false;
        Vector3 fallbackDir = Vector3.zero;
        float fallbackDist = float.MaxValue;
        foreach (var seg in segs)
        {
            if (seg.path != null && !InfraNetworkUtil.IsPathSnappable(seg.path.path_variant, PathSubtype)) continue;
            Vector3 dir = seg.b - seg.a;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) continue;

            float distA = Vector2.Distance(targetXZ, new Vector2(seg.a.x, seg.a.z));
            float distB = Vector2.Distance(targetXZ, new Vector2(seg.b.x, seg.b.z));
            if (Mathf.Min(distA, distB) <= OFFSET_SNAP_TOUCH_TOLERANCE)
            {
                anyTouching = true;
                EvaluateTangentOffsetCandidates(basePos, targetPos, dir, relHeight, ref bestDist, ref bestPos, ref bestRelHeight, ref found);
            }
            else if (!anyTouching)
            {
                Vector3 closest = TerrainInputHandler.GetClosestPointOnSegment(seg.a, seg.b, targetPos);
                float d = Vector2.Distance(targetXZ, new Vector2(closest.x, closest.z));
                if (d < fallbackDist)
                {
                    fallbackDist = d;
                    fallbackDir = dir;
                }
            }
        }
        if (!anyTouching && fallbackDir.sqrMagnitude > 1e-6f)
        {
            EvaluateTangentOffsetCandidates(basePos, targetPos, fallbackDir, relHeight, ref bestDist, ref bestPos, ref bestRelHeight, ref found);
        }
    }

    /// <summary>
    /// 点ターゲットと接線方向1本に対する候補 C = ターゲット ± perp·オフセット を列挙し最良値を更新する。
    /// 候補のYは「候補位置の地形高さ + ターゲットの相対高さ」(高架端点からの延長で高さを引き継ぐ)。
    /// </summary>
    private void EvaluateTangentOffsetCandidates(Vector3 basePos, Vector3 targetPos, Vector3 tangent, float relHeight, ref float bestDist, ref Vector3 bestPos, ref float bestRelHeight, ref bool found)
    {
        Vector3 dir = tangent.normalized;
        Vector3 perp = new Vector3(-dir.z, 0f, dir.x);
        foreach (float offset in LateralOffsets)
        {
            for (int sign = -1; sign <= 1; sign += 2)
            {
                Vector3 candidate = targetPos + perp * (offset * sign);
                float dist = Vector2.Distance(new Vector2(basePos.x, basePos.z), new Vector2(candidate.x, candidate.z));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    candidate.y = Utils.GetGroundHeight(candidate) + relHeight;
                    bestPos = candidate;
                    bestRelHeight = relHeight;
                    found = true;
                }
            }
        }
    }

    /// <summary>
    /// 計画中/建設中パスの端点(start_point / end_point)に対するオフセット考慮吸着の候補を評価する。
    /// </summary>
    private void EvaluateConstructionEndpointOffsetCandidates(Vector3 basePos, float searchRadius, ref float bestDist, ref Vector3 bestPos, ref float bestRelHeight, ref bool found)
    {
        var allPaths = pathManager.GetAllPaths();
        if (allPaths == null) return;

        foreach (var path in allPaths)
        {
            if (path.status != Constants.STATUS_SCHEDULED_CONSTRUCTION &&
                path.status != Constants.STATUS_PENDING &&
                path.status != Constants.STATUS_CONSTRUCTING)
            {
                continue;
            }
            if (!InfraNetworkUtil.IsPathSnappable(path.path_variant, PathSubtype)) continue;

            EvaluateEndpointOffsetCandidates(basePos, GeneratedModelUtils.ToFloatList(path.start_point), searchRadius, ref bestDist, ref bestPos, ref bestRelHeight, ref found);
            EvaluateEndpointOffsetCandidates(basePos, GeneratedModelUtils.ToFloatList(path.end_point), searchRadius, ref bestDist, ref bestPos, ref bestRelHeight, ref found);
        }
    }

    /// <summary>[x, 相対高さ, z] 形式の端点1つ分を絶対座標に組み立てて点ターゲット候補の評価へ渡す。</summary>
    private void EvaluateEndpointOffsetCandidates(Vector3 basePos, List<float> pointList, float searchRadius, ref float bestDist, ref Vector3 bestPos, ref float bestRelHeight, ref bool found)
    {
        if (pointList == null || pointList.Count < 2) return;
        float x = pointList[0];
        float z = pointList[pointList.Count - 1];
        float rel = pointList.Count >= 3 ? pointList[1] : 0f;
        if (Vector2.Distance(new Vector2(basePos.x, basePos.z), new Vector2(x, z)) > searchRadius) return;
        Vector3 endpoint = new Vector3(x, Utils.GetGroundHeight(new Vector3(x, 0f, z)) + rel, z);
        EvaluatePointTargetOffsetCandidates(basePos, endpoint, rel, ref bestDist, ref bestPos, ref bestRelHeight, ref found);
    }

    /// <summary>
    /// 指定位置の近傍セグメントのうち、建設中バリアント（PathSubtype）と網互換な最初のものを返す。
    /// 直角スナップの基準方向を決めるために使う。道路建設中に近くのインフラパス（送電線等）の
    /// 方向を拾ってしまうのを防ぐ。
    /// </summary>
    private bool TryGetFirstSnappableSegment(Vector3 pos, out Vector3 segA, out Vector3 segB)
    {
        segA = Vector3.zero;
        segB = Vector3.zero;
        if (pathManager == null) return false;

        var segs = pathManager.QueryNearbySegments(pos, snapThreshold * 2f);
        if (segs == null) return false;

        foreach (var seg in segs)
        {
            if (seg.path != null && !InfraNetworkUtil.IsPathSnappable(seg.path.path_variant, PathSubtype)) continue;
            segA = seg.a;
            segB = seg.b;
            return true;
        }
        return false;
    }

    // =====================================================================
    // 2Dオーバーレイ描画（毎フレームUpdateから呼ばれる）
    // =====================================================================

    private void UpdateOverlay()
    {
        if (overlayRenderer == null) return;

        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        UpdateBackgroundOverlay(cam);

        overlayRenderer.BeginFrame();

        DrawPlacedEdges(cam);

        // インフラ敷設時の接続端子マーカー（回転＋ボビングのアニメーションを行うため
        // 背景メッシュではなく毎フレームのこのパスで描画する）
        DrawInfraTerminalMarkers(cam);

        if (phase == RoadEditorPhase.Placing)
        {
            if (points.Count > 0)
            {
                DrawPreviewLine(cam);
            }
            DrawPlacingMarker(cam);
        }

        DrawPoints(cam);
        DrawControlHandles(cam);

        if (hoveredEdgeIndex >= 0)
        {
            DrawHoverHighlight(cam);
        }

        if (isRightAngleSnapActive)
        {
            DrawRightAngleSnapGuide(cam);
        }

        if (isStraightLineSnapActive)
        {
            DrawStraightLineSnapGuide(cam);
        }

        DrawTunnelSections(cam);
        DrawMaxSpanWarnings(cam);
        DrawElevationIntersections(cam);
        DrawBuildingInterference(cam);

        overlayRenderer.EndFrame(cam);
    }

    /// <summary>
    /// 編集中の地下パスのトンネル区間（深さ &gt; クリアランス高さ）をオレンジの線で
    /// オーバーレイ表示する。トンネル区間は建設後メッシュが生成されず地形にも隠れるため、
    /// 編集中はどこがトンネルになるかをこのオーバーレイ（_ZTest=Always）で可視化する。
    /// 建設前で地形が未掘削のため、現在地形との深さ判定はバックエンドの区間分類と一致する。
    /// </summary>
    private void DrawTunnelSections(Camera cam)
    {
        if (overlayRenderer == null || points.Count < 2) return;
        if (!IsHeightEditable()) return;

        // 編集中の点に負の相対高さがあれば地下パス。加えて、実効非追従（follow_terrain=0）の
        // パスは相対高さ0でも丘に潜ってトンネルになり得るため、可視化対象とする。
        bool followTerrain = EffectiveFollowTerrain();
        bool underground = false;
        if (followTerrain)
        {
            foreach (var pt in points)
            {
                if (pt.position.y - Utils.GetGroundHeight(pt.position) < -TunnelSectionUtil.UNDERGROUND_REL_Y_EPS)
                {
                    underground = true;
                    break;
                }
            }
            if (!underground) return;
        }

        float clearance = GetPathHeightFromMaster(PathSubtype);
        var tunnelRuns = TunnelSectionUtil.CollectTunnelRuns(GetRenderPoints(), clearance, followTerrain);
        if (tunnelRuns.Count == 0) return;

        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;
        Color tunnelColor = new Color(1f, 0.55f, 0.1f, 0.85f);

        foreach (var run in tunnelRuns)
        {
            for (int i = 0; i < run.Count - 1; i++)
            {
                Vector3 a = run[i];
                Vector3 b = run[i + 1];
                if (Vector3.Dot(a - camPos, camFwd) < 0f && Vector3.Dot(b - camPos, camFwd) < 0f)
                    continue;
                overlayRenderer.AddSolidLine(a, b, tunnelColor, dashedLineThickness * 1.5f);
            }
        }
    }

    /// <summary>
    /// 背景オーバーレイ（既存パスの辺・ノード）を更新する。
    /// カメラが閾値以上に移動した場合のみメッシュを再構築し、
    /// 静止中は既存のメッシュをそのまま使い回す。
    /// </summary>
    private void UpdateBackgroundOverlay(Camera cam)
    {
        if (backgroundRenderer == null) return;

        Vector3 camPos = cam.transform.position;
        // ノードキャッシュの実変更（非同期ロードの到着・削除）も再構築条件に含める
        bool nodeDataChanged = pathManager != null && pathManager.NodeDataVersion != lastBgNodeDataVersion;
        if (!bgDirty && !nodeDataChanged
            && (camPos - lastBgCameraPos).sqrMagnitude < BG_REBUILD_THRESHOLD_SQ)
            return;

        bgDirty = false;
        lastBgCameraPos = camPos;
        lastBgNodeDataVersion = pathManager != null ? pathManager.NodeDataVersion : -1;

        backgroundRenderer.BeginFrame();
        DrawExistingEdges(cam);
        DrawExistingNodes(cam);
        backgroundRenderer.EndFrame(cam);
    }



    /// <summary>
    /// 既存パスの辺を描画する（薄い白線）。
    /// ワールド座標をそのまま OverlayMeshRenderer に渡す。
    /// UpdateBackgroundOverlay の backgroundRenderer.BeginFrame/EndFrame 括り内からのみ
    /// 呼ばれるため、出力先は必ず backgroundRenderer にすること。overlayRenderer へ出力すると
    /// 毎フレームの UpdateOverlay 冒頭の BeginFrame でクリアされ、同フレームで消えてしまう。
    /// </summary>
    private void DrawExistingEdges(Camera cam)
    {
        if (pathManager == null) return;

        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;
        var segments = pathManager.QueryNearbySegments(camPos, DISTANCE_INVISIBLE);
        if (segments == null) return;

        Color lineColor = Color.white;

        foreach (var seg in segments)
        {
            Vector3 mid = (seg.a + seg.b) * 0.5f;
            float dist = Vector3.Distance(camPos, mid);
            float alpha = CalculateAlphaByDistance(dist);
            if (alpha <= 0.01f) continue;

            Vector3 sA = seg.a;
            Vector3 sB = seg.b;
            sA.y = Utils.GetGroundHeight(sA) + 0.05f;
            sB.y = Utils.GetGroundHeight(sB) + 0.05f;

            // ニアプレーン後方カリング
            if (Vector3.Dot(sA - camPos, camFwd) < 0f && Vector3.Dot(sB - camPos, camFwd) < 0f)
                continue;

            Color c = lineColor;
            c.a *= alpha;

            // 吸着できない相手（道路建設中のインフラパス、インフラ建設中の道路や別網のインフラパス等）は
            // 薄いグレーに減色して「接続対象ではない」ことを示す（ノード表示の減色と同じ規約）
            if (seg.path != null && !InfraNetworkUtil.IsPathSnappable(seg.path.path_variant, PathSubtype))
            {
                c = new Color(0.5f, 0.5f, 0.5f, alpha * 0.3f);
            }

            backgroundRenderer.AddSolidLine(sA, sB, c, solidLineThickness);
        }
    }

    /// <summary>
    /// 既存のノードを描画する。
    /// 現在敷設中のパスがインフラ用か道路用かに応じて、接続可能な対象（インフラノードまたは道路ノード）を
    /// 視覚的に強調し、接続できない対象は薄いグレーに減色して表示する。
    /// UpdateBackgroundOverlay の backgroundRenderer.BeginFrame/EndFrame 括り内からのみ
    /// 呼ばれるため、出力先は必ず backgroundRenderer にすること。overlayRenderer へ出力すると
    /// 毎フレームの UpdateOverlay 冒頭の BeginFrame でクリアされ、同フレームで消えてしまう。
    /// </summary>
    private void DrawExistingNodes(Camera cam)
    {
        if (pathManager == null) return;

        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;
        var nodes = pathManager.QueryNearbyNodes(camPos, DISTANCE_INVISIBLE);
        if (nodes == null) return;

        bool drawingInfra = InfraNetworkUtil.IsInfraPathType(PathType);

        foreach (var node in nodes)
        {
            Vector3 worldPos = node.GetAbsolutePosition();
            float dist = Vector3.Distance(camPos, worldPos);
            float alpha = CalculateAlphaByDistance(dist);
            if (alpha <= 0.01f) continue;

            // ニアプレーン後方カリング
            if (Vector3.Dot(worldPos - camPos, camFwd) < 0f) continue;

            worldPos.y += 0.1f;

            // 吸着可能かどうか（網互換判定込み。道路×インフラは不可、インフラ同士でも
            // 別網（送電線×導水管）は不可。詳細は InfraNetworkUtil.IsNodeSnappable）
            bool snappable = InfraNetworkUtil.IsNodeSnappable(node, PathSubtype, pathManager);
            bool isInfraNode = InfraNetworkUtil.IsInfraMask(node.type);

            if (!snappable)
            {
                // 吸着できないノードは薄いグレーにして「接続対象ではない」ことを示す
                Color c = new Color(0.5f, 0.5f, 0.5f, alpha * 0.3f);
                backgroundRenderer.AddSquare(worldPos, existingNodeHalfSize * 0.8f, c);
            }
            else if (drawingInfra && isInfraNode)
            {
                // インフラ敷設時は、同一網のインフラノード（施設の接続端子ノード含む）を
                // シアンで大きく強調表示し、内側に白枠を描画する
                Color c = new Color(0f, 0.8f, 1f, alpha);
                backgroundRenderer.AddSquare(worldPos, existingNodeHalfSize * 1.8f, c);
                backgroundRenderer.AddSquare(worldPos, existingNodeHalfSize * 0.8f, new Color(1f, 1f, 1f, alpha));
            }
            else
            {
                // 通常の吸着可能ノードは標準サイズかつ白色で描画する
                Color c = Color.white;
                c.a = alpha;
                backgroundRenderer.AddSquare(worldPos, existingNodeHalfSize, c);
            }
        }
    }

    /// <summary>
    /// インフラパス敷設中に、接続先となる施設の接続端子ノードの上空へ
    /// 「反転した赤い四角錐」マーカーを描画する（ゆっくり回転＋上下ボビング）。
    /// アニメーションするため、カメラ移動時にしか再構築されない backgroundRenderer ではなく
    /// 毎フレーム再構築される overlayRenderer 側に描画する（端子ノードは少数のため負荷は無視できる）。
    /// 表示対象は「敷設中バリアントと同一網（網互換）の施設端子」のみ。別網の端子は
    /// DrawExistingNodes の灰色減色表示に任せ、道路敷設中は一切表示しない。
    /// </summary>
    private void DrawInfraTerminalMarkers(Camera cam)
    {
        if (pathManager == null) return;

        // インフラパス敷設中のみ表示する（PathType が未設定のケースに備えて
        // マスターからの導出判定も併用する）
        bool drawingInfra = InfraNetworkUtil.IsInfraPathType(PathType)
            || InfraNetworkUtil.IsInfraPathVariant(PathSubtype);
        if (!drawingInfra) return;

        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;

        // 検索半径は DrawExistingNodes と同じ 480m。SpatialHash の巨大半径 Query は
        // フリーズを招くため、これ以上拡大しないこと。
        var nodes = pathManager.QueryNearbyNodes(camPos, DISTANCE_INVISIBLE);
        if (nodes == null) return;

        foreach (var node in nodes)
        {
            // 表示対象の判定はここに集約する:
            // インフラノード、かつ施設の接続端子（BuildingManager の infra_node_id 逆引き）、
            // かつ敷設中バリアントと網互換（吸着可能）であること。
            if (!InfraNetworkUtil.IsInfraMask(node.type)) continue;
            if (BuildingManager.Instance == null
                || !BuildingManager.Instance.TryGetInfraTerminalOwner(node.node_id, out _)) continue;
            if (!InfraNetworkUtil.IsNodeSnappable(node, PathSubtype, pathManager)) continue;

            Vector3 worldPos = node.GetAbsolutePosition();
            float dist = Vector3.Distance(camPos, worldPos);
            float alpha = CalculateAlphaByDistance(dist);
            if (alpha <= 0.01f) continue;

            // ニアプレーン後方カリング
            if (Vector3.Dot(worldPos - camPos, camFwd) < 0f) continue;

            // 共通の描画ユーティリティを呼び出し、同一ロジック・同一マテリアルで描画する。
            InfrastructureMarkerUtility.DrawPyramid(overlayRenderer, worldPos, TERMINAL_MARKER_COLOR_BRIGHT, alpha);
        }
    }

    /// <summary>
    /// 配置中のポイント間を青い実線（Editing）または点線（Placing）で描画する。
    /// ワールド座標をそのまま OverlayMeshRenderer に渡す。
    /// </summary>
    private void DrawPlacedEdges(Camera cam)
    {
        if (points.Count < 2) return;

        Color edgeColor = new Color(0.3f, 0.6f, 1f);
        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 p1 = points[i].position;
            Vector3 p2 = points[i + 1].position;

            if (Vector3.Dot(p1 - camPos, camFwd) < 0f && Vector3.Dot(p2 - camPos, camFwd) < 0f)
                continue;

            if (phase == RoadEditorPhase.Editing)
            {
                overlayRenderer.AddSolidLine(p1, p2, edgeColor, dashedLineThickness);
            }
            else
            {
                overlayRenderer.AddDashedLine(p1, p2, edgeColor, dashedLineThickness);
            }
        }
    }

    /// <summary>
    /// Placingフェーズで、最後のポイントからマウス位置へのプレビュー線を描画する。
    /// ワールド座標をそのまま OverlayMeshRenderer に渡す。
    /// </summary>
    private void DrawPreviewLine(Camera cam)
    {
        Vector3 lastPos = points[points.Count - 1].position;
        Vector3 hoverPos = hoverWorldPosition;
        hoverPos.y = Utils.GetGroundHeight(hoverPos) + currentHeight;

        Color previewColor = new Color(0.3f, 0.6f, 1f, 0.5f);
        overlayRenderer.AddDashedLine(lastPos, hoverPos, previewColor, dashedLineThickness);
    }

    /// <summary>
    /// Placingフェーズで、マウスカーソル位置に追従する配置マーカーを描画する。
    /// グリッドスナップ後の hoverWorldPosition を緑色の円として表示し、
    /// ユーザーがどこに次のノードが配置されるかを視覚的に示す。
    /// マーカーは地形表面の高さに投影して表示する。
    /// </summary>
    private void DrawPlacingMarker(Camera cam)
    {
        Vector3 markerPos = hoverWorldPosition;
        markerPos.y = Utils.GetGroundHeight(markerPos);

        if (Vector3.Dot(markerPos - cam.transform.position, cam.transform.forward) < 0f)
            return;

        Color markerColor = new Color(0.65f, 0.89f, 0.63f);
        overlayRenderer.AddCircle(markerPos, pointCircleRadius * 1.2f, markerColor);
    }

    /// <summary>
    /// 配置ポイントをロールに応じて色分けして描画する。
    /// Start=緑, End=赤, Waypoint=青。
    /// 標高が設定されていても、マーカーは地形表面に投影して表示する。
    /// </summary>
    private void DrawPoints(Camera cam)
    {
        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;

        for (int i = 0; i < points.Count; i++)
        {
            Vector3 groundPos = points[i].position;
            groundPos.y = Utils.GetGroundHeight(groundPos);

            if (Vector3.Dot(groundPos - camPos, camFwd) < 0f) continue;

            Color c;
            switch (points[i].role)
            {
                case PathPointRole.Start:
                    c = new Color(0.65f, 0.89f, 0.63f);
                    break;
                case PathPointRole.End:
                    c = new Color(0.95f, 0.55f, 0.66f);
                    break;
                default:
                    c = new Color(0.54f, 0.71f, 0.98f);
                    break;
            }

            if (i == selectedPointIndex)
            {
                // 選択中のノードは外側に白色のリングを描画して目立たせる。
                Color ringColor = new Color(1f, 1f, 1f, 0.9f);
                overlayRenderer.AddCircle(groundPos, pointCircleRadius * 1.5f, ringColor);
                c = new Color(0.2f, 1f, 0.4f);
            }

            overlayRenderer.AddCircle(groundPos, pointCircleRadius, c);
        }
    }

    /// <summary>
    /// 各ポイントの制御点ハンドルを四角で描画し、アンカーポイントへ破線で接続する。
    /// Editingフェーズでのみ表示する。
    /// 各ポイントは最大2つのハンドル（control_in / control_out）を持つ。
    /// アンカーポイントと制御点ともに、地形表面の高さに投影して表示する。
    /// </summary>
    private void DrawControlHandles(Camera cam)
    {
        if (phase != RoadEditorPhase.Editing) return;
        if (points.Count < 2) return;
        if (!Curved) return;

        Color outColor = new Color(0.976f, 0.886f, 0.686f); // 出口制御点（黄色）
        Color inColor = new Color(0.878f, 0.686f, 0.976f);  // 入口制御点（紫色）
        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;

        for (int i = 0; i < points.Count; i++)
        {
            Vector3 anchorGround = points[i].position;
            anchorGround.y = Utils.GetGroundHeight(anchorGround);

            // 出口制御点（control_out）
            if (points[i].control_out.HasValue)
            {
                Vector3 coGround = points[i].control_out.Value;
                coGround.y = Utils.GetGroundHeight(coGround);

                if (Vector3.Dot(anchorGround - camPos, camFwd) >= 0f || Vector3.Dot(coGround - camPos, camFwd) >= 0f)
                {
                    overlayRenderer.AddDashedLine(anchorGround, coGround, outColor, 1.5f);
                    overlayRenderer.AddSquare(coGround, controlHandleHalfSize, outColor);
                }
            }

            // 入口制御点（control_in）
            if (points[i].control_in.HasValue)
            {
                Vector3 ciGround = points[i].control_in.Value;
                ciGround.y = Utils.GetGroundHeight(ciGround);

                if (Vector3.Dot(anchorGround - camPos, camFwd) >= 0f || Vector3.Dot(ciGround - camPos, camFwd) >= 0f)
                {
                    overlayRenderer.AddDashedLine(anchorGround, ciGround, inColor, 1.5f);
                    overlayRenderer.AddSquare(ciGround, controlHandleHalfSize, inColor);
                }
            }
        }
    }

    /// <summary>
    /// ホバー中の辺を黄色い太線でハイライトする。
    /// ハイライトは地形表面の高さに投影して表示する。
    /// </summary>
    private void DrawHoverHighlight(Camera cam)
    {
        if (hoveredEdgeIndex < 0 || hoveredEdgeIndex >= points.Count - 1) return;

        Vector3 p1Ground = points[hoveredEdgeIndex].position;
        p1Ground.y = Utils.GetGroundHeight(p1Ground);
        Vector3 p2Ground = points[hoveredEdgeIndex + 1].position;
        p2Ground.y = Utils.GetGroundHeight(p2Ground);

        if (Vector3.Dot(p1Ground - cam.transform.position, cam.transform.forward) < 0f
            && Vector3.Dot(p2Ground - cam.transform.position, cam.transform.forward) < 0f)
            return;

        Color highlightColor = new Color(1f, 1f, 0.3f, 0.8f);
        overlayRenderer.AddSolidLine(p1Ground, p2Ground, highlightColor, dashedLineThickness * 2.5f);
    }

    /// <summary>
    /// 指定ピクセルサイズを、指定ワールド位置におけるワールド空間のサイズに換算する。
    /// </summary>
    private float GetWorldPixelScale(Camera cam, Vector3 worldPos, float pixelSize)
    {
        float distance = Vector3.Distance(cam.transform.position, worldPos);
        float tanHalfFov = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float worldPerPixel = distance * tanHalfFov / (Screen.height * 0.5f);
        return pixelSize * worldPerPixel;
    }

    /// <summary>
    /// 直角スナップ補助線を描画する。
    /// <summary>
    /// 直線延長スナップの補助線を描画する。
    /// 基準となる2点を結ぶ方向の延長線を薄いオレンジ色の破線で示し、
    /// スナップ位置に緑色の円マーカーを表示する。
    /// </summary>
    private void DrawStraightLineSnapGuide(Camera cam)
    {
        if (!isStraightLineSnapActive) return;

        Color guideLineColor = new Color(1f, 0.7f, 0.3f, 0.5f);
        Color markerColor = new Color(0.65f, 0.89f, 0.63f, 0.8f);

        // 延長線の基準2点（地形高さに投影）
        Vector3 anchor = straightLineSnapAnchor;
        anchor.y = Utils.GetGroundHeight(anchor);
        Vector3 ext = straightLineSnapExtension;
        ext.y = Utils.GetGroundHeight(ext);

        // 延長線を十分な長さで描画
        Vector3 dir = new Vector3(anchor.x - ext.x, 0f, anchor.z - ext.z);
        if (dir.sqrMagnitude < 1e-6f) return;
        dir.Normalize();

        // 基準セグメントの両側に50m延長した線を描画
        Vector3 guideStart = anchor - dir * 50f;
        guideStart.y = Utils.GetGroundHeight(guideStart);
        Vector3 guideEnd = anchor + dir * 50f;
        guideEnd.y = Utils.GetGroundHeight(guideEnd);

        if (Vector3.Dot(guideStart - cam.transform.position, cam.transform.forward) >= 0f
            || Vector3.Dot(guideEnd - cam.transform.position, cam.transform.forward) >= 0f)
        {
            overlayRenderer.AddDashedLine(guideStart, guideEnd, guideLineColor, 1.5f);
        }

        // スナップ位置にマーカー
        Vector3 snappedPos = straightLineSnapSnappedPos;
        snappedPos.y = Utils.GetGroundHeight(snappedPos);
        if (Vector3.Dot(snappedPos - cam.transform.position, cam.transform.forward) >= 0f)
        {
            overlayRenderer.AddCircle(snappedPos, pointCircleRadius * 1.2f, markerColor);
        }
    }

    /// スナップ対象の既存パスセグメントを黄色の太線でハイライトし、
    /// スナップ後の予想位置に緑色の円マーカーを表示し、
    /// 元のマウス位置からスナップ後の位置へ破線を引く。
    /// </summary>
    private void DrawRightAngleSnapGuide(Camera cam)
    {
        if (!isRightAngleSnapActive) return;

        Color targetLineColor = new Color(1f, 0.92f, 0.016f, 0.9f); // 黄色
        Color guideLineColor = new Color(0.65f, 0.89f, 0.63f, 0.6f); // 緑の破線
        Color markerColor = new Color(0.65f, 0.89f, 0.63f, 0.8f);   // 緑のマーカー

        Vector3 origin = rightAngleSnapOrigin;
        origin.y = Utils.GetGroundHeight(origin);

        // スナップ基準点を中心に小さな十字を描画（ピクセル長さをワールド空間に換算）
        if (Vector3.Dot(origin - cam.transform.position, cam.transform.forward) >= 0f)
        {
            float crossLen = GetWorldPixelScale(cam, origin, 12f);
            Vector3 right = Vector3.Cross(cam.transform.forward, Vector3.up).normalized;
            if (right.sqrMagnitude < 0.0001f) right = Vector3.Cross(cam.transform.forward, Vector3.right).normalized;
            Vector3 up = Vector3.Cross(right, cam.transform.forward).normalized;
            overlayRenderer.AddSolidLine(origin - right * crossLen, origin + right * crossLen, targetLineColor, 2f);
            overlayRenderer.AddSolidLine(origin - up * crossLen, origin + up * crossLen, targetLineColor, 2f);
        }

        // 元のマウス位置からスナップ後の位置へ破線を描画
        Vector3 targetPos = rightAngleSnapTarget;
        targetPos.y = Utils.GetGroundHeight(targetPos);
        Vector3 snappedPos = rightAngleSnapSnappedPos;
        snappedPos.y = Utils.GetGroundHeight(snappedPos);

        if (Vector3.Dot(targetPos - cam.transform.position, cam.transform.forward) >= 0f
            || Vector3.Dot(snappedPos - cam.transform.position, cam.transform.forward) >= 0f)
        {
            overlayRenderer.AddDashedLine(targetPos, snappedPos, guideLineColor, 1.5f);
        }

        // スナップ後の予想位置に緑色の円マーカーを表示
        if (Vector3.Dot(snappedPos - cam.transform.position, cam.transform.forward) >= 0f)
        {
            overlayRenderer.AddCircle(snappedPos, pointCircleRadius * 1.2f, markerColor);
        }
    }

    // =====================================================================
    // 3Dプレビュー（ベジエサンプリング）
    // =====================================================================

    /// <summary>
    /// 編集中の道路形状をベジエサンプリングして、描画および立体交差判定用の
    /// 細分化済み点群を生成する。
    /// Update3DPreview と立体交差バリデーションで同じ点群を使うため、
    /// 生成ロジックを共通化している。
    /// </summary>
    private List<Vector3> GetRenderPoints()
    {
        if (cachedRenderPoints != null)
            return cachedRenderPoints;

        List<Vector3> renderPoints = new List<Vector3>();

        if (!Curved)
        {
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 pos = points[i].position;
                pos.y += points[i].embankmentHeight;
                renderPoints.Add(pos);
            }
        }
        else
        {
            for (int i = 0; i < points.Count; i++)
            {
                // アンカー点に盛土オフセットを加算してプレビューに反映
                Vector3 anchorPos = points[i].position;
                anchorPos.y += points[i].embankmentHeight;
                renderPoints.Add(anchorPos);

                if (i < points.Count - 1)
                {
                    Vector3 p0 = points[i].position;
                    p0.y += points[i].embankmentHeight;
                    Vector3 p3 = points[i + 1].position;
                    p3.y += points[i + 1].embankmentHeight;

                    // 制御点にもアンカーと同じ盛土オフセットを加算する
                    Vector3 p1;
                    if (points[i].control_out.HasValue)
                    {
                        p1 = points[i].control_out.Value;
                        p1.y += points[i].embankmentHeight;
                    }
                    else
                    {
                        p1 = Vector3.Lerp(p0, p3, 1f / 3f);
                    }

                    Vector3 p2;
                    if (points[i + 1].control_in.HasValue)
                    {
                        p2 = points[i + 1].control_in.Value;
                        p2.y += points[i + 1].embankmentHeight;
                    }
                    else
                    {
                        p2 = Vector3.Lerp(p0, p3, 2f / 3f);
                    }

                    // セグメント両端の相対高さ（標高）を求め、両端とも地面高さ（≒0）の
                    // 地上道路なら各サンプル点を地形に追従させる。
                    // これを行わないと、曲線モードではベジエがアンカー間の起伏を無視して
                    // Yを滑らかに補間するため、丘や谷の区間で道路が地形に埋まってしまう
                    // （直線モードは RoadPath.SubdividePointsWithTerrain で追従するため埋まらない）。
                    // 実道路を生成する PathManager.SampleBezierSegmentWithTerrain と同じロジック。
                    // 閾値は立体交差判定（ElevationSegment.follows）と同じ0.01mに揃え、
                    // follow_terrain=false のバリアント（桁橋など）は常に非追従とする。
                    float relStart = points[i].position.y - Utils.GetGroundHeight(points[i].position);
                    float relEnd = points[i + 1].position.y - Utils.GetGroundHeight(points[i + 1].position);
                    bool followTerrain = EffectiveFollowTerrain()
                        && Mathf.Abs(relStart) <= ELEVATION_EPSILON_REL
                        && Mathf.Abs(relEnd) <= ELEVATION_EPSILON_REL;
                    float embStart = points[i].embankmentHeight;
                    float embEnd = points[i + 1].embankmentHeight;

                    for (int s = 1; s <= bezierSamplesPerSegment; s++)
                    {
                        float t = (float)s / (bezierSamplesPerSegment + 1);
                        Vector3 sampled = SampleCubicBezier(p0, p1, p2, p3, t);
                        if (followTerrain)
                        {
                            // XZはベジエ曲線のまま、Yだけ地形高さ＋盛土(線形補間)に追従させる
                            float gh = HeightmapTextureManager.GetTerrainHeight(new Vector3(sampled.x, 1000f, sampled.z));
                            sampled.y = gh + Mathf.Lerp(embStart, embEnd, t) + 0.1f;
                        }
                        renderPoints.Add(sampled);
                    }
                }
            }
        }

        if (phase == RoadEditorPhase.Placing && points.Count > 0)
        {
            Vector3 hover = hoverWorldPosition;
            // 高架エッジへ吸着中はその標高でプレビュー線を接続する
            float hoverRel = hoverSnapWasElevated ? hoverSnapRelHeight : currentHeight;
            hover.y = Utils.GetGroundHeight(hover) + hoverRel + currentEmbankmentHeight;
            renderPoints.Add(hover);
        }

        cachedRenderPoints = renderPoints;
        return cachedRenderPoints;
    }

    /// <summary>
    /// 3Dプレビューを更新する。
    /// 各セグメントを三次ベジエとしてサンプリングし、細分化済みの点群を生成する。
    /// セグメント構成: P0[i].control_out → P1[i+1].control_in → P1[i+1].position
    /// 制御点がない場合は直線で補間する。
    /// </summary>
    private void Update3DPreview()
    {
        if (roadRenderer == null) return;

        List<Vector3> renderPoints = GetRenderPoints();

        if (renderPoints.Count >= 2)
        {
            bool isValid = CheckElevationIntersections().Count == 0;
            roadRenderer.UpdateRoadPreview(renderPoints, PathType, PathSubtype, Curved, isValid, PlatformSide, LateralOffsets, TravelDirections);
        }
        else
        {
            roadRenderer.ResetPreview();
        }
    }

    /// <summary>
    /// 三次ベジエ曲線のパラメータ t における点を計算する。
    /// B(t) = (1-t)^3 * P0 + 3(1-t)^2*t * P1 + 3(1-t)*t^2 * P2 + t^3 * P3
    /// </summary>
    private static Vector3 SampleCubicBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float u = 1f - t;
        float u2 = u * u;
        float u3 = u2 * u;
        float t2 = t * t;
        float t3 = t2 * t;

        return u3 * p0 + 3f * u2 * t * p1 + 3f * u * t2 * p2 + t3 * p3;
    }

    // =====================================================================
    // IPC: ノード標高・盛土変更（パネルのステッパーから受信）
    // =====================================================================

    /// <summary>
    /// IPC経由でノードの標高（相対高さ）変更を受信したときの処理。
    /// nodeIndex が -1 の場合は currentHeight（次回配置時の標高）を更新する。
    /// 曲線/直線・吸着はUnityのラジアルメニューが真実源だが、標高・盛土は
    /// パネルのステッパーとUnityキー操作の双方向で編集できるようにしている。
    /// </summary>
    public void OnChangeNodeFromIpc(int nodeIndex, float height)
    {
        // 標高編集不可のバリアント（街路など）ではIPCによる標高変更を無視する
        if (!IsHeightEditable()) return;

        // 下限はマスターの min_height（負の値を持つ地下対応バリアントのみ地下へ潜れる）
        height = Mathf.Max(height, GetMinHeightFromMaster());

        if (nodeIndex == -1)
        {
            currentHeight = height;
            return;
        }

        if (phase != RoadEditorPhase.Editing) return;
        if (nodeIndex < 0 || nodeIndex >= points.Count) return;

        var pt = points[nodeIndex];
        pt.position.y = Utils.GetGroundHeight(pt.position) + height;
        points[nodeIndex] = pt;
        InvalidateShapeCache();
        RecomputeAllControlPoints();
        Update3DPreview();
        EmitShapeUpdate();
        EmitSelectNode();
    }

    /// <summary>
    /// IPC経由でノードの盛土オフセット変更を受信したときの処理。
    /// nodeIndex が -1 の場合は currentEmbankmentHeight（次回配置時の盛土）を更新する。
    /// 盛土は全道路タイプで利用可能（高架不可の街路でも盛土は可能）。
    /// </summary>
    public void OnChangeEmbankmentFromIpc(int nodeIndex, float embankmentHeight)
    {
        // -5〜+5の範囲にクランプ
        embankmentHeight = Mathf.Clamp(embankmentHeight, -5f, 5f);

        if (nodeIndex == -1)
        {
            currentEmbankmentHeight = embankmentHeight;
            return;
        }

        if (phase != RoadEditorPhase.Editing) return;
        if (nodeIndex < 0 || nodeIndex >= points.Count) return;

        var pt = points[nodeIndex];
        pt.embankmentHeight = embankmentHeight;
        points[nodeIndex] = pt;
        InvalidateShapeCache();
        Update3DPreview();
        EmitShapeUpdate();
        EmitSelectNode();
    }

    // =====================================================================
    // IPC: 選択ノード通知
    // =====================================================================

    /// <summary>
    /// 配置ノードに採用する高さ（相対高さ）を決定する。
    /// 直近の吸着が高架のノード/パス/建設中端点なら、その相対高さを採用し、
    /// currentHeight とUIの標高表示にも同期する（続く新規ノードも同じ高架高さで置かれる）。
    /// 高さ編集不可バリアントや高架でない吸着では currentHeight をそのまま用いる。
    /// </summary>
    private float ResolvePlacementHeight()
    {
        if (lastSnapWasElevated && IsHeightEditable())
        {
            float relH = Mathf.Max(lastSnapRelHeight, GetMinHeightFromMaster());
            if (!Mathf.Approximately(relH, currentHeight))
            {
                currentHeight = relH;
                EmitCurrentHeight();
            }
            return relH;
        }
        return currentHeight;
    }

    /// <summary>
    /// 配置中（ノード未選択）の currentHeight を edit_path:select_node (node_id=-1) で通知する。
    /// UIパネルの標高・盛土ステッパーはこの値を表示し、キー操作や吸着でUnity側が値を変えたときに同期する。
    /// </summary>
    private void EmitCurrentHeight()
    {
        if (IpcClient.Instance == null) return;
        IpcClient.Instance.EmitEditPathSelectNode(
            pathId,
            -1,
            new float[] { 0f, currentHeight, 0f },
            "waypoint",
            currentHeight,
            currentEmbankmentHeight
        );
    }

    /// <summary>
    /// 現在選択中のノード情報を edit_path:select_node で通知する。
    /// ノードを選択したタイミングや高さが変化したタイミングで呼び出し、
    /// UIパネルの標高・盛土ステッパーの表示と編集対象ノードを同期する。
    /// </summary>
    private void EmitSelectNode()
    {
        if (IpcClient.Instance == null) return;
        if (selectedPointIndex < 0 || selectedPointIndex >= points.Count) return;

        var pt = points[selectedPointIndex];
        float groundY = Utils.GetGroundHeight(pt.position);
        float relY = pt.position.y - groundY;
        IpcClient.Instance.EmitEditPathSelectNode(
            pathId,
            selectedPointIndex,
            new float[] { pt.position.x, pt.position.y, pt.position.z },
            pt.role.ToString().ToLower(),
            relY,
            pt.embankmentHeight
        );
    }

    // =====================================================================
    // キー入力: ノード高さ変更
    // =====================================================================

    /// <summary>
    /// キーボード入力により標高(相対高さ)を変更する。
    /// PageUp / 1 で +1.0m、PageDown / 2 で -1.0m。
    /// ノード選択中はそのノードの標高を、未選択(配置中含む)は次回配置ノードに適用される
    /// currentHeight を変更する。変更後に3DプレビューとUIへの通知を行う。
    /// </summary>
    private void HandleHeightInput()
    {
        // 標高編集不可のバリアント（街路など）ではキーボードによる標高変更を無視する
        if (!IsHeightEditable()) return;

        float delta = 0f;
        if (InputUtil.KeyWasPressedThisFrame(Key.PageUp) || InputUtil.KeyWasPressedThisFrame(Key.Digit1))
        {
            delta = 1.0f;
        }
        else if (InputUtil.KeyWasPressedThisFrame(Key.PageDown) || InputUtil.KeyWasPressedThisFrame(Key.Digit2))
        {
            delta = -1.0f;
        }

        if (Mathf.Abs(delta) < 0.01f) return;

        if (selectedPointIndex < 0 || selectedPointIndex >= points.Count)
        {
            // ノード未選択: 次回配置ノードの標高を変更する
            // 下限はマスターの min_height（負の値を持つ地下対応バリアントのみ地下へ潜れる）
            currentHeight = Mathf.Max(currentHeight + delta, GetMinHeightFromMaster());
            EmitCurrentHeight();
            return;
        }

        var pt = points[selectedPointIndex];
        float groundY = Utils.GetGroundHeight(pt.position);
        // 下限はマスターの min_height（負の値を持つ地下対応バリアントのみ地下へ潜れる）
        float newRelY = Mathf.Max((pt.position.y - groundY) + delta, GetMinHeightFromMaster());
        pt.position.y = groundY + newRelY;
        points[selectedPointIndex] = pt;

        InvalidateShapeCache();
        Update3DPreview();
        EmitShapeUpdate();
        EmitSelectNode();
    }

    /// <summary>
    /// キーボード入力により盛土オフセットを変更する。
    /// 3キーで +1.0m、4キーで -1.0m。
    /// ノード選択中はそのノードの盛土を、未選択(配置中含む)は次回配置ノードに適用される
    /// currentEmbankmentHeight を変更する。
    /// 全道路タイプで利用可能（高架不可の街路でも盛土は可能）。
    /// 変更後に3DプレビューとUIへの通知を行う。
    /// </summary>
    private void HandleEmbankmentInput()
    {
        float delta = 0f;
        if (InputUtil.KeyWasPressedThisFrame(Key.Digit3))
        {
            delta = 1.0f;
        }
        else if (InputUtil.KeyWasPressedThisFrame(Key.Digit4))
        {
            delta = -1.0f;
        }

        if (Mathf.Abs(delta) < 0.01f) return;

        if (selectedPointIndex < 0 || selectedPointIndex >= points.Count)
        {
            // ノード未選択: 次回配置ノードの盛土を変更する
            currentEmbankmentHeight = Mathf.Clamp(currentEmbankmentHeight + delta, -5f, 5f);
            EmitCurrentHeight();
            return;
        }

        var pt = points[selectedPointIndex];
        float newEmbankment = Mathf.Clamp(pt.embankmentHeight + delta, -5f, 5f);
        if (Mathf.Approximately(newEmbankment, pt.embankmentHeight)) return;
        pt.embankmentHeight = newEmbankment;
        points[selectedPointIndex] = pt;

        InvalidateShapeCache();
        Update3DPreview();
        EmitShapeUpdate();
        EmitSelectNode();
    }

    // =====================================================================
    // ユーティリティ
    // =====================================================================

    /// <summary>
    /// 指定された AudioClip を再生する。
    /// クリップまたは AudioSource が未設定の場合は何もしない。
    /// </summary>
    private void PlaySound(AudioClip clip)
    {
        if (clip == null || audioSource == null) return;
        audioSource.PlayOneShot(clip);
    }

    private float CalculateAlphaByDistance(float distance)
    {
        if (distance <= DISTANCE_START_FADE) return 1f;
        if (distance >= DISTANCE_INVISIBLE) return 0f;
        float fadeRange = DISTANCE_INVISIBLE - DISTANCE_START_FADE;
        return 1f - (distance - DISTANCE_START_FADE) / fadeRange;
    }

    /// <summary>
    /// 現在のポイント位置をIPC経由でUIパネルに通知する。
    ///
    /// バックエンドは地形を考慮せず相対高さで座標を扱うため、
    /// 送信するY座標は地面高さを差し引いた相対高さとする。
    /// UI側ではこの相対高さをそのままバックエンド送信に使用する。
    /// </summary>
    private void EmitShapeUpdate(bool finalized = false)
    {
        if (IpcClient.Instance == null) return;

        var pointsList = new List<List<float>>();
        foreach (var pt in points)
        {
            float groundY = Utils.GetGroundHeight(pt.position);
            float relY = pt.position.y - groundY;
            pointsList.Add(new List<float> { pt.position.x, relY, pt.position.z });
        }

        // 制御点をバックエンド座標系 [x, 相対高さ, z] へ変換する(未設定は null)
        List<float> ControlToRelativeList(Vector3? control)
        {
            if (!control.HasValue) return null;
            var c = control.Value;
            float groundY = Utils.GetGroundHeight(c);
            return new List<float> { c.x, c.y - groundY, c.z };
        }

        List<float> startControl = points.Count > 0 ? ControlToRelativeList(points[0].control_out) : null;
        List<float> endControl = points.Count > 0 ? ControlToRelativeList(points[points.Count - 1].control_in) : null;

        // 中間アンカー(ウェイポイント)の制御点。UIが確定送信の waypoints に
        // {position, control_in, control_out} 形式で含めることで、ドラッグで調整した
        // 曲線がバックエンドに永続化される(未送信の制御点はバックエンドが自動補完する)。
        List<Dictionary<string, object>> waypointControls = null;
        if (points.Count > 2)
        {
            waypointControls = new List<Dictionary<string, object>>();
            for (int i = 1; i < points.Count - 1; i++)
            {
                waypointControls.Add(new Dictionary<string, object>
                {
                    ["control_in"] = ControlToRelativeList(points[i].control_in),
                    ["control_out"] = ControlToRelativeList(points[i].control_out),
                });
            }
        }

        // 各ポイントの盛土オフセットを送信
        var embankmentHeights = new List<float>();
        foreach (var pt in points)
        {
            embankmentHeights.Add(pt.embankmentHeight);
        }

        IpcClient.Instance.EmitEditPathUpdateShape(pathId, PathType, pointsList, startControl, endControl, embankmentHeights, waypointControls, Curved, PathFollowTerrainOverride, finalized);
    }

    // =====================================================================
    // max_span バリデーション
    // =====================================================================

    /// <summary>
    /// 現在の道路形状で max_span を超えるセグメントを検出する。
    /// 水平距離（XZ平面のみ）で計算し、曲線は考慮しない。
    /// </summary>
    private List<int> CheckMaxSpanViolations()
    {
        if (cachedMaxSpanViolations != null)
            return cachedMaxSpanViolations;

        List<int> violations = new List<int>();
        if (MasterDataStore.Instance == null)
        {
            cachedMaxSpanViolations = violations;
            return cachedMaxSpanViolations;
        }

        var master = MasterDataStore.Instance.GetPathMaster(PathSubtype);
        if (master == null || !master.max_span.HasValue)
        {
            cachedMaxSpanViolations = violations;
            return cachedMaxSpanViolations;
        }

        float maxSpan = master.max_span.Value;
        if (maxSpan <= 0f)
        {
            cachedMaxSpanViolations = violations;
            return cachedMaxSpanViolations;
        }

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 a = points[i].position;
            Vector3 b = points[i + 1].position;
            float dist = Mathf.Sqrt((b.x - a.x) * (b.x - a.x) + (b.z - a.z) * (b.z - a.z));
            if (dist > maxSpan)
            {
                violations.Add(i);
            }
        }

        cachedMaxSpanViolations = violations;
        return cachedMaxSpanViolations;
    }

    /// <summary>
    /// max_span を超えるセグメントを赤色の太線でハイライトする。
    /// </summary>
    private void DrawMaxSpanWarnings(Camera cam)
    {
        if (overlayRenderer == null) return;

        List<int> violations = CheckMaxSpanViolations();
        if (violations.Count == 0) return;

        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;
        Color warningColor = new Color(1f, 0.2f, 0.2f, 0.9f);
        foreach (int i in violations)
        {
            Vector3 a = points[i].position;
            Vector3 b = points[i + 1].position;
            if (Vector3.Dot(a - camPos, camFwd) < 0f && Vector3.Dot(b - camPos, camFwd) < 0f) continue;
            overlayRenderer.AddSolidLine(a, b, warningColor, dashedLineThickness * 2.5f);
        }
    }

    // =====================================================================
    // 立体交差バリデーション
    // =====================================================================

    /// <summary>
    /// 同じ高さ例外の許容誤差（相対比較＝追従×追従のペア）。バックエンドの
    /// ELEVATION_INTERSECTION_EPSILON と同値。
    /// </summary>
    private const float ELEVATION_EPSILON_REL = 0.01f;

    /// <summary>
    /// 同じ高さ例外の許容誤差（絶対標高比較＝非追従スパンが絡むペア）。
    /// 絶対標高は float16 地形の量子化とサンプラ差のノイズを含むため広めに取る。
    /// バックエンドの ELEVATION_INTERSECTION_EPSILON_ABS と同値。
    /// </summary>
    private const float ELEVATION_EPSILON_ABS = 0.5f;

    /// <summary>
    /// 立体交差判定用のサンプリング済みセグメント。
    /// 通常は純粋な相対高さ空間で交差判定を行うが、非追従スパン（高さ編集された
    /// スパン・橋バリアント）が絡むペアではバックエンドと同様に絶対標高
    /// （地形標高＋相対高さ）で判定するため、端点の絶対標高（absYA/absYB）と
    /// 地形追従フラグ（follows）も保持する。
    /// follows が立つセグメント上の標高は補間値ではなく交差点位置の地形標高
    /// そのものを使う（補間誤差による地表どうしの誤判定を防ぐ）。
    /// </summary>
    private struct ElevationSegment
    {
        public Vector3 a;
        public Vector3 b;
        public float relYA;
        public float relYB;
        public float absYA;
        public float absYB;
        public bool follows;

        public ElevationSegment(Vector3 a, Vector3 b, float relYA, float relYB, bool variantFollows)
        {
            this.a = a;
            this.b = b;
            this.relYA = relYA;
            this.relYB = relYB;
            // 相対高さ0どうしのセグメントは地形追従（描画上は地面に張り付く）。
            // follow_terrain=false のバリアント（桁橋など）は relY=0 でも非追従
            // （バックエンド geo_path_depth._build_segments_3d と同一の定義）。
            this.follows = variantFollows
                && Mathf.Abs(relYA) <= ELEVATION_EPSILON_REL
                && Mathf.Abs(relYB) <= ELEVATION_EPSILON_REL;
            this.absYA = relYA + Utils.GetGroundHeight(new Vector3(a.x, 0f, a.z));
            this.absYB = relYB + Utils.GetGroundHeight(new Vector3(b.x, 0f, b.z));
        }
    }

    /// <summary>
    /// path_variant に対応するマスターから height（上空占有高さ）を取得する。
    /// マスターに定義がない場合は 0.0m とする（地面に張り付く道路）。
    /// </summary>
    private float GetPathHeightFromMaster(int variant)
    {
        if (MasterDataStore.Instance == null) return 0f;
        try
        {
            var master = MasterDataStore.Instance.GetPathMaster(variant);
            if (master != null && master.height.HasValue)
                return master.height.Value;
        }
        catch
        {
            // マスター未定義の場合は無視
        }
        return 0f;
    }

    /// <summary>
    /// 現在の PathSubtype の許容最小高さ(m)をマスターデータから取得する。
    /// 負の値を持つバリアント（地下道路・地下鉄用線路など）だけが地下（相対Y&lt;0）に
    /// 建設できる。マスター未定義時は 0（地下禁止）。バックエンドの
    /// _validate_path_height_range と同じ制約のエディタ側ガード。
    /// </summary>
    private float GetMinHeightFromMaster()
    {
        if (MasterDataStore.Instance == null) return 0f;
        try
        {
            var master = MasterDataStore.Instance.GetPathMaster(PathSubtype);
            if (master != null && master.min_height.HasValue)
                return master.min_height.Value;
        }
        catch
        {
            // マスター未定義の場合は 0 とする
        }
        return 0f;
    }

    /// <summary>
    /// 現在の PathSubtype が標高編集可能（height_editable=true）かどうかを
    /// マスターデータから判定する。マスターデータ未取得時は false とする。
    /// 標高編集不可のバリアント（街路など）では標高の設定・変更を禁止するために使用する。
    /// </summary>
    public bool IsHeightEditable()
    {
        if (MasterDataStore.Instance == null) return false;
        try
        {
            var master = MasterDataStore.Instance.GetPathMaster(PathSubtype);
            return master != null && master.height_editable.HasValue && master.height_editable.Value;
        }
        catch
        {
            // マスター未定義の場合は編集不可とする
        }
        return false;
    }

    /// <summary>
    /// 編集中パスの点群をベジエサンプリングして、立体交差判定用の
    /// ElevationSegment リストを生成する。
    /// 地形高さの影響を排除するため、入力点の相対高さのみを用いてサンプリングする。
    /// </summary>
    private List<ElevationSegment> BuildEditingElevationSegments()
    {
        List<ElevationSegment> segments = new List<ElevationSegment>();
        List<Vector3> relPoints = new List<Vector3>();
        List<Vector3?> relControlOut = new List<Vector3?>();
        List<Vector3?> relControlIn = new List<Vector3?>();

        // 全ポイントの相対高さを計算
        for (int i = 0; i < points.Count; i++)
        {
            var pt = points[i];
            float groundY = Utils.GetGroundHeight(pt.position);
            float relY = pt.position.y - groundY;
            relPoints.Add(new Vector3(pt.position.x, relY, pt.position.z));
            
            if (pt.control_out.HasValue) {
                float gY = Utils.GetGroundHeight(pt.control_out.Value);
                relControlOut.Add(new Vector3(pt.control_out.Value.x, pt.control_out.Value.y - gY, pt.control_out.Value.z));
            } else {
                relControlOut.Add(null);
            }
            
            if (pt.control_in.HasValue) {
                float gY = Utils.GetGroundHeight(pt.control_in.Value);
                relControlIn.Add(new Vector3(pt.control_in.Value.x, pt.control_in.Value.y - gY, pt.control_in.Value.z));
            } else {
                relControlIn.Add(null);
            }
        }

        // Placingフェーズのマウス位置（高架エッジ吸着中はその標高を使う）
        if (phase == RoadEditorPhase.Placing && points.Count > 0)
        {
            float hoverRel = hoverSnapWasElevated ? hoverSnapRelHeight : currentHeight;
            relPoints.Add(new Vector3(hoverWorldPosition.x, hoverRel, hoverWorldPosition.z));
            relControlOut.Add(null);
            relControlIn.Add(null);
        }

        if (relPoints.Count < 2) return segments;

        // 相対高さ空間でベジエサンプリング
        List<Vector3> sampledPoints = new List<Vector3>();
        if (!Curved)
        {
            sampledPoints.AddRange(relPoints);
        }
        else
        {
            for (int i = 0; i < relPoints.Count - 1; i++)
            {
                sampledPoints.Add(relPoints[i]);

                Vector3 p0 = relPoints[i];
                Vector3 p3 = relPoints[i + 1];

                Vector3 p1 = relControlOut[i] ?? Vector3.Lerp(p0, p3, 1f / 3f);
                Vector3 p2 = relControlIn[i + 1] ?? Vector3.Lerp(p0, p3, 2f / 3f);

                for (int s = 1; s <= bezierSamplesPerSegment; s++)
                {
                    float t = (float)s / (bezierSamplesPerSegment + 1);
                    Vector3 sampled = SampleCubicBezier(p0, p1, p2, p3, t);
                    sampledPoints.Add(sampled);
                }
            }
            sampledPoints.Add(relPoints[relPoints.Count - 1]);
        }

        bool variantFollows = EffectiveFollowTerrain();
        for (int i = 0; i < sampledPoints.Count - 1; i++)
        {
            Vector3 a = sampledPoints[i];
            Vector3 b = sampledPoints[i + 1];
            segments.Add(new ElevationSegment(a, b, a.y, b.y, variantFollows));
        }

        return segments;
    }

    /// <summary>
    /// 既存パスの形状データから、立体交差判定用の ElevationSegment リストを生成する。
    /// バックエンドの座標は [x, relativeY, z] なので、相対高さを直接サンプリングできる。
    /// </summary>
    private List<ElevationSegment> BuildExistingElevationSegments(PathSummary path)
    {
        List<ElevationSegment> segments = new List<ElevationSegment>();
        var startList = GeneratedModelUtils.ToFloatList(path.start_point);
        var endList = GeneratedModelUtils.ToFloatList(path.end_point);
        if (startList == null || endList == null || startList.Count < 2 || endList.Count < 2) return segments;

        Vector3 sp = new Vector3(startList[0], startList.Count > 2 ? startList[1] : 0f, startList[startList.Count - 1]);
        Vector3 ep = new Vector3(endList[0], endList.Count > 2 ? endList[1] : 0f, endList[endList.Count - 1]);

        var waypoints = GeneratedModelUtils.ToWaypointList(path.waypoints) ?? new List<PathWaypoint>();

        List<Vector3> relPoints = new List<Vector3>();
        List<Vector3?> relControlOut = new List<Vector3?>();
        List<Vector3?> relControlIn = new List<Vector3?>();

        relPoints.Add(sp);
        // PathSummary には start_control/end_control が含まれないため null とし、
        // 後の処理で自動的に補間されるようにする
        relControlOut.Add(null);
        relControlIn.Add(null);

        foreach (var wp in waypoints)
        {
            if (wp.position == null || wp.position.Length < 2) continue;
            Vector3 pos = new Vector3(wp.position[0], wp.position.Length > 2 ? wp.position[1] : 0f, wp.position[wp.position.Length - 1]);
            relPoints.Add(pos);

            if (wp.control_in != null && wp.control_in.Length >= 2)
                relControlIn.Add(new Vector3(wp.control_in[0], wp.control_in.Length > 2 ? wp.control_in[1] : 0f, wp.control_in[wp.control_in.Length - 1]));
            else
                relControlIn.Add(null);

            if (wp.control_out != null && wp.control_out.Length >= 2)
                relControlOut.Add(new Vector3(wp.control_out[0], wp.control_out.Length > 2 ? wp.control_out[1] : 0f, wp.control_out[wp.control_out.Length - 1]));
            else
                relControlOut.Add(null);
        }

        relPoints.Add(ep);
        relControlIn.Add(null);
        relControlOut.Add(null);

        List<Vector3> sampledPoints = new List<Vector3>();
        if (!path.curved)
        {
            sampledPoints.AddRange(relPoints);
        }
        else
        {
            for (int i = 0; i < relPoints.Count - 1; i++)
            {
                sampledPoints.Add(relPoints[i]);
                Vector3 p0 = relPoints[i];
                Vector3 p3 = relPoints[i + 1];
                Vector3 p1 = relControlOut[i] ?? Vector3.Lerp(p0, p3, 1f / 3f);
                Vector3 p2 = relControlIn[i + 1] ?? Vector3.Lerp(p0, p3, 2f / 3f);

                for (int s = 1; s <= bezierSamplesPerSegment; s++)
                {
                    float t = (float)s / (bezierSamplesPerSegment + 1);
                    Vector3 sampled = SampleCubicBezier(p0, p1, p2, p3, t);
                    sampledPoints.Add(sampled);
                }
            }
            sampledPoints.Add(relPoints[relPoints.Count - 1]);
        }

        bool variantFollows = PathManager.GetPathFollowTerrain(path);
        for (int i = 0; i < sampledPoints.Count - 1; i++)
        {
            Vector3 a = sampledPoints[i];
            Vector3 b = sampledPoints[i + 1];
            segments.Add(new ElevationSegment(a, b, a.y, b.y, variantFollows));
        }
        return segments;
    }

    /// <summary>
    /// 2つの線分（XZ平面投影）が交差するかを判定し、交差地点の
    /// 両線分上のパラメータ t を out で返す。
    /// 交差しない場合は false を返す。
    /// </summary>
    private static bool SegmentsIntersectXZ(Vector3 a1, Vector3 a2, Vector3 b1, Vector3 b2, out float tA, out float tB)
    {
        tA = tB = 0f;
        float dax = a2.x - a1.x;
        float daz = a2.z - a1.z;
        float dbx = b2.x - b1.x;
        float dbz = b2.z - b1.z;

        float denom = dax * dbz - daz * dbx;
        if (Mathf.Abs(denom) < 1e-6f) return false;

        float d1x = b1.x - a1.x;
        float d1z = b1.z - a1.z;

        tA = (d1x * dbz - d1z * dbx) / denom;
        tB = (d1x * daz - d1z * dax) / denom;

        return tA >= -1e-4f && tA <= 1f + 1e-4f && tB >= -1e-4f && tB <= 1f + 1e-4f;
    }

    /// <summary>
    /// セグメント列に非追従スパン（高さ編集されたスパン、または follow_terrain=false
    /// バリアントのスパン）が含まれるかを返す。ElevationSegment.follows は
    /// バリアントフラグと relY≈0 の両方を織り込み済みのため、その否定を見るだけでよい。
    /// バックエンド geo_path_depth.path_has_non_follow_span と同義。
    /// </summary>
    private static bool HasNonFollowSegment(List<ElevationSegment> segments)
    {
        foreach (var seg in segments)
        {
            if (!seg.follows) return true;
        }
        return false;
    }

    /// <summary>
    /// 編集中パスと既存パスセグメントの間で立体交差（高さ方向の衝突）を検出する。
    ///
    /// 判定ルール:
    /// - XZ平面で2つのセグメントが交差する場合、交差地点での両パスの
    ///   高さ範囲 [Y, Y + height] が重なるかをチェックする。
    /// - ただし、交差地点における両パスの高さの差が許容誤差以下の場合は
    ///   「同じ高さの交差点・踏切」として許可する（相対比較1cm／絶対比較0.5m）。
    /// - 追従×追従のペアは地形高さを無視し、すべてのパスは仮想的にフラットな
    ///   平面上にあるとみなして相対高さで判定する。
    /// - 非追従スパン（高さ編集されたスパン・follow_terrain=false の橋バリアント）が
    ///   絡むペアは、バックエンドの validate_path_elevation_intersections と同様に
    ///   絶対標高（地形標高＋相対高さ）で判定する。丘の下を通るトンネルと丘の上の
    ///   地表道路、掘割を跨ぐ橋と掘割底の道路を正しく立体交差として許可するため。
    ///   地形追従セグメント（follows）は交差点位置の地形標高を使う。
    /// - 自分自身（既存パス編集中の場合）や新規パスの自己交差も判定の対象とする。
    /// </summary>
    private List<(int segIndex, PathSegment otherSeg)> CheckElevationIntersections()
    {
        if (cachedElevationViolations != null)
            return cachedElevationViolations;

        var violations = new List<(int segIndex, PathSegment otherSeg)>();
        if (pathManager == null || points.Count < 2)
        {
            cachedElevationViolations = violations;
            return cachedElevationViolations;
        }

        float myHeight = GetPathHeightFromMaster(PathSubtype);
        List<ElevationSegment> mySegments = BuildEditingElevationSegments();
        if (mySegments.Count == 0)
        {
            cachedElevationViolations = violations;
            return cachedElevationViolations;
        }

        // 編集中パスが非追従スパン（高さ編集されたスパン、または follow_terrain=false
        // バリアントのスパン）を持つか。非追従スパンの補間relYは交点の物理量に
        // ならないため、絡むペアは絶対標高で判定する（バックエンドの
        // path_has_non_follow_span と同一の判定）。
        bool editingNonFollow = HasNonFollowSegment(mySegments);

        HashSet<int> candidatePathIds = new HashSet<int>();
        for (int i = 0; i < mySegments.Count; i++)
        {
            var seg = mySegments[i];
            Vector3 mid = (seg.a + seg.b) * 0.5f;
            float radius = Vector3.Distance(seg.a, seg.b) * 0.5f + 5.0f;

            var nearby = pathManager.QueryNearbySegments(new Vector3(mid.x, 0, mid.z), radius);
            if (nearby != null)
            {
                foreach (var other in nearby)
                {
                    if (other.path != null)
                        candidatePathIds.Add(other.path.path_id);
                }
            }
        }

        // --- 既存パスとの交差判定 ---
        foreach (int otherPathId in candidatePathIds)
        {
            if (otherPathId == pathId) continue;

            PathSummary exPath = pathManager.GetPathData(otherPathId);
            if (exPath == null) continue;

            // 既存パスのセグメントは編集中に変化しないためキャッシュする
            if (!existingElevSegCache.TryGetValue(otherPathId, out List<ElevationSegment> exSegments))
            {
                exSegments = BuildExistingElevationSegments(exPath);
                existingElevSegCache[otherPathId] = exSegments;
            }

            float otherHeight = GetPathHeightFromMaster(exPath.path_variant);

            if (myHeight <= 0f && otherHeight <= 0f) continue;

            // 非追従スパンが絡むペアは絶対標高（地形標高＋相対高さ）で比較する
            bool useAbsolute = editingNonFollow || HasNonFollowSegment(exSegments);
            float sameHeightEps = useAbsolute ? ELEVATION_EPSILON_ABS : ELEVATION_EPSILON_REL;

            for (int i = 0; i < mySegments.Count; i++)
            {
                for (int j = 0; j < exSegments.Count; j++)
                {
                    var mySeg = mySegments[i];
                    var exSeg = exSegments[j];

                    float tA, tB;
                    if (!SegmentsIntersectXZ(mySeg.a, mySeg.b, exSeg.a, exSeg.b, out tA, out tB))
                        continue;

                    float myYAtIntersect;
                    float otherYAtIntersect;
                    if (useAbsolute)
                    {
                        // 地形追従セグメントは補間値ではなく交差点位置の地形標高を使う
                        // （両者が同じ値になり、補間誤差による平面交差の誤判定を防ぐ）
                        float groundAtCrossing = 0f;
                        if (mySeg.follows || exSeg.follows)
                        {
                            Vector3 crossing = Vector3.Lerp(mySeg.a, mySeg.b, tA);
                            groundAtCrossing = Utils.GetGroundHeight(new Vector3(crossing.x, 0f, crossing.z));
                        }
                        myYAtIntersect = mySeg.follows ? groundAtCrossing : Mathf.Lerp(mySeg.absYA, mySeg.absYB, tA);
                        otherYAtIntersect = exSeg.follows ? groundAtCrossing : Mathf.Lerp(exSeg.absYA, exSeg.absYB, tB);
                    }
                    else
                    {
                        myYAtIntersect = Mathf.Lerp(mySeg.relYA, mySeg.relYB, tA);
                        otherYAtIntersect = Mathf.Lerp(exSeg.relYA, exSeg.relYB, tB);
                    }

                    // 同じ高さ例外（相対比較1cm / 絶対比較0.5m）
                    if (Mathf.Abs(myYAtIntersect - otherYAtIntersect) <= sameHeightEps)
                        continue;

                    float myMin = myYAtIntersect;
                    float myMax = myYAtIntersect + myHeight;
                    float otherMin = otherYAtIntersect;
                    float otherMax = otherYAtIntersect + otherHeight;

                    bool overlap = myMin < otherMax && myMax > otherMin;
                    if (overlap)
                    {
                        // 描画用に絶対座標に変換したダミーのPathSegmentを作成
                        Vector3 aAbs = new Vector3(exSeg.a.x, Utils.GetGroundHeight(new Vector3(exSeg.a.x, 0, exSeg.a.z)) + exSeg.relYA, exSeg.a.z);
                        Vector3 bAbs = new Vector3(exSeg.b.x, Utils.GetGroundHeight(new Vector3(exSeg.b.x, 0, exSeg.b.z)) + exSeg.relYB, exSeg.b.z);
                        PathSegment dummyOther = new PathSegment { a = aAbs, b = bAbs };
                        violations.Add((i, dummyOther));
                    }
                }
            }
        }

        // --- 自己交差判定 ---
        // 非追従スパンを持つパスは絶対標高（地形標高＋相対高さ）で判定する
        float selfSameHeightEps = editingNonFollow ? ELEVATION_EPSILON_ABS : ELEVATION_EPSILON_REL;
        for (int i = 0; i < mySegments.Count; i++)
        {
            for (int j = i + 2; j < mySegments.Count; j++) // 隣接セグメントは無視
            {
                var mySeg1 = mySegments[i];
                var mySeg2 = mySegments[j];

                float tA, tB;
                if (!SegmentsIntersectXZ(mySeg1.a, mySeg1.b, mySeg2.a, mySeg2.b, out tA, out tB))
                    continue;

                float myRelY1;
                float myRelY2;
                if (editingNonFollow)
                {
                    float groundAtCrossing = 0f;
                    if (mySeg1.follows || mySeg2.follows)
                    {
                        Vector3 crossing = Vector3.Lerp(mySeg1.a, mySeg1.b, tA);
                        groundAtCrossing = Utils.GetGroundHeight(new Vector3(crossing.x, 0f, crossing.z));
                    }
                    myRelY1 = mySeg1.follows ? groundAtCrossing : Mathf.Lerp(mySeg1.absYA, mySeg1.absYB, tA);
                    myRelY2 = mySeg2.follows ? groundAtCrossing : Mathf.Lerp(mySeg2.absYA, mySeg2.absYB, tB);
                }
                else
                {
                    myRelY1 = Mathf.Lerp(mySeg1.relYA, mySeg1.relYB, tA);
                    myRelY2 = Mathf.Lerp(mySeg2.relYA, mySeg2.relYB, tB);
                }

                if (Mathf.Abs(myRelY1 - myRelY2) <= selfSameHeightEps)
                    continue;

                float min1 = myRelY1;
                float max1 = myRelY1 + myHeight;
                float min2 = myRelY2;
                float max2 = myRelY2 + myHeight;

                bool overlap = min1 < max2 && max1 > min2;
                if (overlap)
                {
                    Vector3 aAbs = new Vector3(mySeg2.a.x, Utils.GetGroundHeight(new Vector3(mySeg2.a.x, 0, mySeg2.a.z)) + mySeg2.relYA, mySeg2.a.z);
                    Vector3 bAbs = new Vector3(mySeg2.b.x, Utils.GetGroundHeight(new Vector3(mySeg2.b.x, 0, mySeg2.b.z)) + mySeg2.relYB, mySeg2.b.z);
                    PathSegment dummyOther = new PathSegment { a = aAbs, b = bAbs };
                    violations.Add((i, dummyOther));
                }
            }
        }

        cachedElevationViolations = violations;
        return cachedElevationViolations;
    }

    /// <summary>
    /// 立体交差しているセグメントを赤色の太線でハイライトする。
    /// </summary>
    private void DrawElevationIntersections(Camera cam)
    {
        if (overlayRenderer == null) return;

        var intersections = CheckElevationIntersections();
        if (intersections.Count == 0) return;

        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;
        Color warningColor = new Color(1f, 0.2f, 0.2f, 0.9f);
        List<Vector3> renderPoints = GetRenderPoints();

        foreach (var (segIndex, otherSeg) in intersections)
        {
            if (segIndex < 0 || segIndex >= renderPoints.Count - 1) continue;
            Vector3 a = renderPoints[segIndex];
            Vector3 b = renderPoints[segIndex + 1];
            if (Vector3.Dot(a - camPos, camFwd) >= 0f || Vector3.Dot(b - camPos, camFwd) >= 0f)
            {
                overlayRenderer.AddSolidLine(a, b, warningColor, dashedLineThickness * 2.5f);
            }

            // 交差相手セグメントもハイライト
            if (Vector3.Dot(otherSeg.a - camPos, camFwd) >= 0f || Vector3.Dot(otherSeg.b - camPos, camFwd) >= 0f)
            {
                Color faded = warningColor;
                faded.a *= 0.5f;
                overlayRenderer.AddSolidLine(otherSeg.a, otherSeg.b, faded, dashedLineThickness * 2.5f);
            }
        }
    }

    /// <summary>
    /// 敷設中パスの敷地コリドー(中心線をパス幅で膨らませた帯)と重なる建物を
    /// フロント側でローカル推定し、そのフットプリントを返す。
    ///
    /// バックエンドへ問い合わせず BuildingManager の空間インデックス + 建物フットプリント
    /// (BuildingSummary.polygonArray は center_point 相対のローカルXZ座標。
    /// center_point を加算してワールドXZへ変換してから判定する)で幾何判定するため、
    /// ドラッグ追従でリアルタイムに使える。所有権・収用可否は判別せず、幾何的な重なりの
    /// 推定にとどまる(権威判定は確定時のバックエンド問い合わせ)。編集中はキャッシュする。
    /// </summary>
    private List<List<Vector3>> CheckBuildingInterference()
    {
        if (cachedBuildingInterference != null)
            return cachedBuildingInterference;

        var result = new List<List<Vector3>>();
        cachedBuildingInterference = result;

        if (BuildingManager.Instance == null || pathManager == null) return result;

        // GetRenderPoints() はキャッシュ共有参照のため複製してから編集する。
        var centerline = new List<Vector3>(GetRenderPoints());
        // 配置中は確定済みアンカーに加え、スナップ済みホバー位置を暫定終点として繋ぎ、
        // ドラッグ追従で干渉をリアルタイム表示する（ホバー移動で毎回キャッシュ無効化される）。
        if (phase == RoadEditorPhase.Placing && points.Count >= 1)
        {
            if (centerline.Count == 0 ||
                (centerline[centerline.Count - 1] - hoverWorldPosition).sqrMagnitude > 0.01f)
            {
                centerline.Add(hoverWorldPosition);
            }
        }
        if (centerline.Count < 2) return result;

        float halfWidth = pathManager.GetRoadWidth(PathSubtype) * 0.5f;
        if (halfWidth <= 0f) return result;

        // コリドーのBBOX(中心線のXZ範囲をhalfWidth+マージンで膨らませる)で候補を絞る。
        float minX = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var p in centerline)
        {
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.z < minZ) minZ = p.z;
            if (p.z > maxZ) maxZ = p.z;
        }
        float margin = halfWidth + BuildingInterferenceMargin;
        var candidates = BuildingManager.Instance.QueryBuildingIdsInBounds(
            minX - margin, minZ - margin, maxX + margin, maxZ + margin);

        // インフラを流せるバリアント(送電線・導水管・電化線路など)の敷設中は、同一網の
        // インフラ需給施設を干渉対象から除外する。接続端子ノードは施設の立地区画内に
        // あるため、端子への引き込みでパスが施設建物と重なるのは正常な配線形状であり、
        // 警告する意味がない。バックエンド権威判定(compute_path_interference の
        // building_ids)も同じ基準で除外するため、ローカル推定と権威ハイライトが揃う。
        bool drawingInfraCarrier = InfraNetworkUtil.IsInfraPathType(PathType)
            || InfraNetworkUtil.GetPathInfraProducts(PathSubtype) != null;

        foreach (int buildingId in candidates)
        {
            var summary = BuildingManager.Instance.GetBuildingSummary(buildingId);
            float[][] poly = summary?.polygonArray;
            if (poly == null || poly.Length < 3) continue;

            // 同一網のインフラ施設かどうかは、施設の接続端子ノードの網ビットと敷設中
            // バリアントの網互換(IsNodeSnappable と同じビット積)で判定する。
            // 端子ノードが未ロードの場合は除外しない(安全側=従来通りハイライト表示)。
            if (drawingInfraCarrier && summary.infra_node_id > 0)
            {
                var terminalNode = pathManager.GetNodeData(summary.infra_node_id);
                if (terminalNode != null
                    && InfraNetworkUtil.IsNodeSnappable(terminalNode, PathSubtype, pathManager))
                {
                    continue;
                }
            }

            // polygonArray は center_point 相対(ミニマップ・メッシュ生成と同じ規約)のため、
            // center_point を加算してワールドXZへ変換する。center_point 欠損時は位置を
            // 確定できないので判定対象から外す(相対座標のまま扱うと地図中心に誤検出が出る)。
            float[] cp = summary.centerPointArray;
            if (cp == null || cp.Length < 2) continue;

            var footprint = new List<Vector2>(poly.Length);
            foreach (var v in poly)
            {
                if (v != null && v.Length >= 2) footprint.Add(new Vector2(v[0] + cp[0], v[1] + cp[1]));
            }
            if (footprint.Count < 3) continue;

            if (CorridorOverlapsPolygon(centerline, halfWidth, footprint))
            {
                var worldPoly = new List<Vector3>(footprint.Count);
                foreach (var f in footprint)
                {
                    worldPoly.Add(new Vector3(f.x, 0f, f.y));
                }
                result.Add(worldPoly);
            }
        }

        return result;
    }

    /// <summary>
    /// 中心線をhalfWidthで膨らませたコリドーが、指定フットプリント(XZポリゴン)と重なるか。
    /// (a)フットプリント頂点が中心線から halfWidth 以内、(b)中心線頂点がフットプリント内、
    /// (c)中心線セグメントとフットプリント辺が交差、のいずれかで重なりとみなす近似。
    /// </summary>
    private static bool CorridorOverlapsPolygon(List<Vector3> centerline, float halfWidth, List<Vector2> footprint)
    {
        float halfWidthSq = halfWidth * halfWidth;

        // (a) フットプリント頂点が中心線コリドー内(いずれかのセグメントから halfWidth 以内)
        for (int fi = 0; fi < footprint.Count; fi++)
        {
            Vector2 fp = footprint[fi];
            for (int ci = 0; ci < centerline.Count - 1; ci++)
            {
                Vector2 a = new Vector2(centerline[ci].x, centerline[ci].z);
                Vector2 b = new Vector2(centerline[ci + 1].x, centerline[ci + 1].z);
                if (SqDistancePointToSegmentXZ(fp, a, b) <= halfWidthSq) return true;
            }
        }

        // (b) 中心線頂点がフットプリント内部
        for (int ci = 0; ci < centerline.Count; ci++)
        {
            if (PointInPolygonXZ(new Vector2(centerline[ci].x, centerline[ci].z), footprint)) return true;
        }

        // (c) 中心線セグメントとフットプリント辺の交差
        for (int ci = 0; ci < centerline.Count - 1; ci++)
        {
            Vector3 a = centerline[ci];
            Vector3 b = centerline[ci + 1];
            for (int fi = 0; fi < footprint.Count; fi++)
            {
                Vector2 f0 = footprint[fi];
                Vector2 f1 = footprint[(fi + 1) % footprint.Count];
                if (SegmentsIntersectXZ(a, b,
                        new Vector3(f0.x, 0f, f0.y), new Vector3(f1.x, 0f, f1.y),
                        out _, out _))
                    return true;
            }
        }

        return false;
    }

    /// <summary>点Pから線分ABまでのXZ平面上の距離の2乗。</summary>
    private static float SqDistancePointToSegmentXZ(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        if (lenSq < 1e-9f) return (p - a).sqrMagnitude;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
        Vector2 proj = a + t * ab;
        return (p - proj).sqrMagnitude;
    }

    /// <summary>点PがXZポリゴン内部にあるか（レイキャスティング法）。</summary>
    private static bool PointInPolygonXZ(Vector2 p, List<Vector2> poly)
    {
        bool inside = false;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 pi = poly[i];
            Vector2 pj = poly[j];
            if (((pi.y > p.y) != (pj.y > p.y)) &&
                (p.x < (pj.x - pi.x) * (p.y - pi.y) / (pj.y - pi.y) + pi.x))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>
    /// ローカル推定した干渉建物のフットプリントを警告色でオーバーレイ表示する。
    /// バックエンドの権威結果が来ると HighlightManager 側の色分けで上書きされる。
    /// </summary>
    private void DrawBuildingInterference(Camera cam)
    {
        if (overlayRenderer == null) return;

        var footprints = CheckBuildingInterference();
        if (footprints.Count == 0) return;

        Color fillColor = new Color(1f, 0.2f, 0.2f, 0.35f);
        Color outlineColor = new Color(1f, 0.2f, 0.2f, 0.9f);
        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;

        foreach (var footprint in footprints)
        {
            if (footprint.Count < 3) continue;

            // 各頂点を地形高さに載せる（少し浮かせてZファイト回避）
            var worldPoints = new List<Vector3>(footprint.Count);
            foreach (var pt in footprint)
            {
                float y = Utils.GetGroundHeight(new Vector3(pt.x, 1000f, pt.z)) + BuildingInterferenceHeightOffset;
                worldPoints.Add(new Vector3(pt.x, y, pt.z));
            }

            overlayRenderer.AddPolygon(worldPoints, fillColor);

            // 外周線
            for (int i = 0; i < worldPoints.Count; i++)
            {
                Vector3 a = worldPoints[i];
                Vector3 b = worldPoints[(i + 1) % worldPoints.Count];
                if (Vector3.Dot(a - camPos, camFwd) >= 0f || Vector3.Dot(b - camPos, camFwd) >= 0f)
                {
                    overlayRenderer.AddSolidLine(a, b, outlineColor, dashedLineThickness * 2f);
                }
            }
        }
    }
}

