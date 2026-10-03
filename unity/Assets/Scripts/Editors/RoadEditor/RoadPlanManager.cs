using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using GeneratedModels;

public enum RoadEditorPhase { Inactive, Placing, Editing }

/// <summary>
/// RoadPlanManager:
/// 道路エディタの中核マネージャ。配置・形状編集・プレビュー・UI同期を統括する。
///
/// 操作フロー:
///   1. UIパネルでバリアント選択 → IPCでグラフィックプログラムに通知
///   2. Placingフェーズ: 左クリックで始点/ウェイポイントを配置
///   3. 右クリックで形状確定 → Editingフェーズに移行
///   4. Editingフェーズ: ドラッグでポイント/制御点を移動、右クリックで削除、辺上クリックで挿入
///   5. [確定]ボタンでバックエンドに送信
///
/// 曲線パスは三次ベジエ、直線パスはアンカー間の線分で表現する。
/// 各ポイントは Catmull-Romスプラインに基づき自動計算された control_in / control_out を持ち、
/// Editingフェーズでドラッグにより手動調整可能。
/// バックエンドの waypoints[].control_in / control_out と同じデータ構造。
/// </summary>
public partial class RoadPlanManager : MonoBehaviour
{
    [Header("References")]
    public PathManager pathManager;
    public Camera gameCamera;
    public Material previewMaterial;
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
    public string HouseEnds = "both";
    public string PassageMode = "elevated";

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

    // ステート
    private RoadEditorPhase phase = RoadEditorPhase.Inactive;

    // 敷設中のアンカーと制御点（絶対座標）
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

    /// <summary>
    /// 選択中ノードをドラッグしたときに動かす軸。XZ=地面に沿った移動、Y=地面からの高さ変更。
    /// 未選択ノードのクリックでXZになり、選択済みノードを再クリックするたびにトグルする
    /// (標高編集不可のバリアントではXZ固定)。ノードの選択解除でXZへ戻る。
    /// </summary>
    private enum AnchorDragAxis { XZ, Y }
    private AnchorDragAxis anchorDragAxis = AnchorDragAxis.XZ;

    // 高さドラッグの基準値。ドラッグ開始時のカーソル位置が指す平面上のYと、そのときのノード相対高さを
    // 記録し、以後は差分で動かす。ノードの円は地面に描かれているため、絶対値で追従させると
    // 掴んだ瞬間に高架ノードが地面へ落ちる。
    private Vector3 verticalDragAnchor;
    private float verticalDragStartPlaneY;
    private float verticalDragStartRelY;

    // 高さドラッグでカメラ正面向きの鉛直平面を使えるかの下限(カメラ前方ベクトルの水平成分の2乗長)。
    // これ未満(ほぼ真上からの見下ろし)は平面とレイが平行に近づくため、画面縦ピクセル差の換算に切り替える。
    private const float VERTICAL_DRAG_MIN_PLANE_NORMAL_SQR = 0.04f;

    // サブコンポーネント
    private OverlayMeshRenderer overlayRenderer;
    private GameObject overlayMeshObj;
    private RoadOverlayHitDetector hitDetector;

    // TerrainInputHandlerへの参照（スナップ処理の共通化用）
    private TerrainInputHandler terrainInputHandler;
    // 海・湖の横断判定で都市範囲(マップ寸法)を引くための参照。初回の判定時に解決する。
    private TerrainManager terrainManager;

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

    private void UpdateShapePreview()
    {
        InvalidateShapeCache();
        Update3DPreview();
    }

    private void CommitShapeChange(bool finalized = false)
    {
        UpdateShapePreview();
        EmitShapeUpdate(finalized);
    }

    private void InvalidateShapeCache()
    {
        routeRevision = System.Guid.NewGuid().ToString("N");
        authoritativeAlignments = null;
        authoritativeCollision = false;
        cachedRenderPoints = null;
        cachedEditingElevationSegments = null;
        cachedElevationViolations = null;
        cachedRiverViolations = null;
        cachedSeaLakeViolations = null;
        cachedCollisionVolume = null;
        cachedBuildingVolume = null;
        cachedMaxSpanViolations = null;
        cachedBuildingInterference = null;
        cachedAlignmentViolations = null;
        alignmentLabels.Clear();
    }

    public void SetAuthoritativeCollision(bool value)
    {
        authoritativeCollision = value;
        cachedCollisionVolume = null;
    }

    public bool IsEditMode => phase == RoadEditorPhase.Editing;
    public bool CanEditControls => IsEditMode && Curved && points.Count >= 2;

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

    /// <summary>Editingフェーズでノードを選択中か(カーソルHUDの移動軸矢印の表示条件)。</summary>
    public bool HasSelectedNode => phase == RoadEditorPhase.Editing && selectedPointIndex >= 0 && selectedPointIndex < points.Count;

    /// <summary>選択中ノードのドラッグが高さ(Y)方向モードか。未選択時はfalse。</summary>
    public bool IsVerticalDragMode => HasSelectedNode && anchorDragAxis == AnchorDragAxis.Y;

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

    /// <summary>
    /// 敷設中バリアントが駅舎パス(type=PATH_STATION_HOUSE)かをマスターデータから判定する。
    /// マスター未取得時は STATE_EDIT_PATH で受け取った PathType にフォールバックする。
    /// </summary>
    public bool IsStationHouse()
    {
        if (MasterDataStore.Instance != null)
        {
            try
            {
                var master = MasterDataStore.Instance.GetPathMaster(PathSubtype);
                if (master != null) return master.type == Constants.PATH_STATION_HOUSE;
            }
            catch
            {
                // マスター未定義の場合は PathType で判定する
            }
        }
        return PathType == Constants.PATH_STATION_HOUSE;
    }

    /// <summary>
    /// 既存の地物へ引き寄せる吸着(ノード/パス/直線延長/直角/並行・終端揃え)を無効化するか。
    /// 駅舎パスはプラットフォーム群を跨ぐ連絡橋で、線路・道路と交差してもノードを作らず接続もしない。
    /// 線路へ吸着すると端点が線路上に乗って建屋が線路を避けきれないため、吸着自体を持たせない。
    /// グリッド吸着はプレイヤーが明示的に出したグリッドへの整列なので対象外(有効なまま)。
    /// </summary>
    public bool SnappingSuppressed => IsStationHouse();

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
        if (!IsSessionActive || TutorialRuntime.PathRestricted || TutorialRuntime.BlocksWorldInput) return;
        if (phase == RoadEditorPhase.Editing) UpdateHoverState();
        HandleHeightInput();
        HandleEmbankmentInput();
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
        // ノード・ベジエ制御点・補助線などの編集ギズモと既存パスの辺・ノードは、
        // 起伏地形でハンドルが地面の裏に回っても埋もれないよう深度テストなしで描く。
        if (overlayMeshObj == null)
        {
            overlayRenderer = OverlayMeshRenderer.Create("RoadEditorOverlayMesh", alwaysOnTop: true);
            overlayMeshObj = overlayRenderer.gameObject;
        }

        if (backgroundMeshObj == null)
        {
            backgroundRenderer = OverlayMeshRenderer.Create("RoadEditorBackgroundMesh", alwaysOnTop: true);
            backgroundMeshObj = backgroundRenderer.gameObject;
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
        InvalidateShapeCache();
        PathSubtype = pathSubtype;
        // 標高編集不可のバリアント（街路など）に切り替えた場合は標高をリセットする。
        // 前のバリアント（高速道路など）で設定した標高が残らないようにする。
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
    public void SetPassageMode(string mode)
    {
        if (mode != "elevated" && mode != "ground" && mode != "underground") return;
        if (PassageMode == mode) return;
        PassageMode = mode;
        CommitShapeChange();
    }

    public void SetHouseEnds(string ends)
    {
        if (ends != "both" && ends != "start" && ends != "end") return;
        if (HouseEnds == ends) return;
        HouseEnds = ends;
        CommitShapeChange();
    }

    public void SetPlatformSide(int side)
    {
        if (PlatformSide == side) return;
        PlatformSide = side;
        CommitShapeChange();
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
        CommitShapeChange();
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
        CommitShapeChange();
    }

    public void SetCurved(bool curved)
    {
        if (TutorialRuntime.PathRestricted && curved) return;
        ResetParallelSnap();
        // curve_allowed=false のバリアント(送電線など)では曲線化を拒否する
        Curved = curved && IsCurveAllowed();
        // 曲線⇔直線の切り替えで端点整列の適用有無も変わるため、手動調整も捨てて制御点を全て作り直す
        RecomputeAllControlPoints(resetManual: true);
        RefreshHoverSnapping();
        CommitShapeChange();
    }

    /// <summary>
    /// 道路敷設モードを初期化する。
    /// existingPathId が指定されている場合は既存パス編集モードとなり、
    /// バックエンドから該当パスの形状データを取得して points にロードする。
    /// </summary>
    public void InitializeRoadPlacement(int? existingPathId = null, float? initialHeight = null, float? initialEmbankment = null, bool? initialCurved = null, int? initialFollowTerrain = null)
    {
        routeUndoPoints = null;
        ResetParallelSnap();
        hasRawSnapPosition = false;
        Debug.Log($"RoadPlanManager: 道路敷設モードを初期化 (existingPathId={existingPathId})");
        EnsureOverlayRenderer();

        phase = RoadEditorPhase.Placing;
        points.Clear();
        selectedPointIndex = -1;
        anchorDragAxis = AnchorDragAxis.XZ;
        hoveredEdgeIndex = -1;
        isDragging = false;
        dragTarget = DragTarget.None;
        // 継続配置ではUIが敷設設定を渡す。指定のない新規開始だけ既定値を使う。
        currentHeight = Mathf.Max(IsHeightEditable() ? initialHeight ?? 0f : 0f, GetMinHeightFromMaster());
        currentEmbankmentHeight = Mathf.Clamp(initialEmbankment ?? 0f, -5f, 5f);
        // 既存パス編集の設定は、後続のLoadExistingPathで保存値に置き換える。
        PathFollowTerrainOverride = IsHeightEditable() ? initialFollowTerrain ?? Constants.VALUE_NONE : Constants.VALUE_NONE;
        Curved = IsCurveAllowed() && (initialCurved ?? true);
        ApplyTutorialPathSettings();
        // 「直線延長」吸着は曲線/直線・道路/軌道/インフラを問わず常にOFFから始める
        // (敷設中に既存パスの延長線へ引き寄せられるのを既定の挙動にしない)。
        // 継続配置(UIが敷設設定を渡す)ではセッション中の手動の吸着設定をそのまま保つ。
        if (!initialCurved.HasValue)
        {
            TerrainInputHandler.EnableStraightLineSnapping = false;
        }
        InvalidateShapeCache();
        existingElevSegCache.Clear();
        bgDirty = true;

        if (roadRenderer != null)
        {
            roadRenderer.CreatePreviewMeshComponents(previewMaterial);
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
                HouseEnds = response.house_ends ?? "both";
                PassageMode = response.passage_mode;
                // 地形追従の個別上書きを復元する（height_editable=false のバリアントでは意味を持たない）。
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
                                pt.control_in = RestoreControl(wp.control_in[0], ciRelY, wp.control_in[wp.control_in.Length - 1], wpPos);
                            }
                            if (wp.control_out != null && wp.control_out.Length >= 2)
                            {
                                float coRelY = wp.control_out.Length > 2 ? wp.control_out[1] : 0f;
                                pt.control_out = RestoreControl(wp.control_out[0], coRelY, wp.control_out[wp.control_out.Length - 1], wpPos);
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
                    var startPt = points[0];
                    startPt.control_out = RestoreControl(startControlList[0], scRelY, startControlList[startControlList.Count - 1], startPt.position);
                    points[0] = startPt;
                }
                var endControlList = response.GetEndControlList();
                if (endControlList != null && endControlList.Count >= 2 && points.Count >= 1)
                {
                    float ecRelY = endControlList.Count > 2 ? endControlList[1] : 0f;
                    var endPtRef = points[points.Count - 1];
                    endPtRef.control_in = RestoreControl(endControlList[0], ecRelY, endControlList[endControlList.Count - 1], endPtRef.position);
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

                    // バックエンドに保存済みの制御点(ユーザーがドラッグで調整した曲線)を
                    // 退避してから自動計算を実行し、保存値がある箇所だけ上書き復元する。
                    // 保存値が無い箇所(スキーマ再生成前の端点など)は自動計算値のまま残る。
                    // 保存値は自動計算結果も含む(確定時に整列済み制御点を明示送信するため)ので、
                    // 自動計算値と一致する制御点は未調整扱いにし、以後の頂点移動で再計算されるようにする。
                    // XZが乖離している制御点だけ手動調整済み扱いにして保持する。高さが縦断勾配の規則から
                    // 外れる制御点(経路提案の結果など)は、そのアンカーの高さを固定して保存値を再現する。
                    var savedControlIn = new Vector3?[points.Count];
                    var savedControlOut = new Vector3?[points.Count];
                    for (int i = 0; i < points.Count; i++)
                    {
                        savedControlIn[i] = points[i].control_in;
                        savedControlOut[i] = points[i].control_out;
                    }

                    RecomputeAllControlPoints(resetManual: true);

                    const float manualEpsilon = 0.05f;
                    for (int i = 0; i < points.Count; i++)
                    {
                        var pt = points[i];
                        float grade = HorizontalGrade(AutoTangent(i));
                        bool heightManual = false;
                        if (savedControlIn[i].HasValue)
                        {
                            Vector3 saved = savedControlIn[i].Value;
                            pt.control_in_manual = !pt.control_in.HasValue
                                || Utils.XZDistance(saved, pt.control_in.Value) > manualEpsilon;
                            heightManual |= Mathf.Abs(saved.y - WithGrade(saved, pt.position, -grade).y) > manualEpsilon;
                            pt.control_in = saved;
                        }
                        if (savedControlOut[i].HasValue)
                        {
                            Vector3 saved = savedControlOut[i].Value;
                            pt.control_out_manual = !pt.control_out.HasValue
                                || Utils.XZDistance(saved, pt.control_out.Value) > manualEpsilon;
                            heightManual |= Mathf.Abs(saved.y - WithGrade(saved, pt.position, grade).y) > manualEpsilon;
                            pt.control_out = saved;
                        }
                        if (heightManual)
                        {
                            // 高さの固定は手動の制御点にだけ効くので、片側だけ自動に戻って縦断が折れないよう両側を手動にする
                            pt.control_in_manual |= pt.control_in.HasValue;
                            pt.control_out_manual |= pt.control_out.HasValue;
                        }
                        pt.control_height_manual = heightManual;
                        points[i] = pt;
                    }

                    CommitShapeChange();
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
        earthworkPreview?.Clear();
        ResetParallelSnap();
        hasRawSnapPosition = false;
        phase = RoadEditorPhase.Inactive;
        points.Clear();
        selectedPointIndex = -1;
        anchorDragAxis = AnchorDragAxis.XZ;
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
        }
    }

    // =====================================================================
    // パブリックAPI: 入力ハンドラ（TerrainInputHandlerから呼ばれる）
    // =====================================================================

    /// <summary>
    /// 地形左クリック。
    /// Placing: 始点/ウェイポイントを追加
    /// Editing: 制御点・ポイントのドラッグ開始、または辺上にウェイポイント挿入
    /// </summary>
    public void OnTerrainLeftClick(Vector3 worldPos)
    {
        if (!TutorialRuntime.AllowRoadClick(worldPos, points.Count, false, PathSubtype, phase == RoadEditorPhase.Placing)) return;
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

            SyncTutorialPlacementHeight();
            if (Curved) RecomputeAllControlPoints();
            CommitShapeChange();
            PlaySound(placePointSound);
        }
        else if (phase == RoadEditorPhase.Editing)
        {
            HandleEditingClick(worldPos);
        }
    }

    /// <summary>
    /// 地形右クリック。
    /// Placing: 形状を確定してEditingフェーズに移行
    /// Editing: ポイントを削除
    /// </summary>
    public void OnTerrainRightClick(Vector3 worldPos)
    {
        if (!TutorialRuntime.AllowRoadClick(worldPos, points.Count, true, PathSubtype, phase == RoadEditorPhase.Placing)) return;
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
            UpdateHoverSnapping(worldPos);
            UpdateShapePreview();
        }
        else if (phase == RoadEditorPhase.Editing) hoverWorldPosition = worldPos;
    }

    private void UpdateHoverSnapping(Vector3 worldPos)
    {
        hoverWorldPosition = ApplySnapping(worldPos);
        hoverSnapWasElevated = lastSnapWasElevated && IsHeightEditable();
        hoverSnapRelHeight = lastSnapRelHeight;
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
        if (TutorialRuntime.PathRestricted || TutorialRuntime.BlocksWorldInput) return;
        if (phase != RoadEditorPhase.Editing) return;

        // ドラッグ未開始の場合、現在のマウス位置でドラッグ対象を決定する。
        // InputHandler がマウスダウンとドラッグを区別するようになったため、
        // ドラッグ確定時には既に HandleEditingClick が呼ばれていない可能性がある。
        if (!isDragging)
        {
            Camera cam = gameCamera != null ? gameCamera : Camera.main;
            if (cam == null) return;

            Vector2 mouseScreen = InputUtil.MousePosition;
            RoadOverlayHit hit = hitDetector.TestHit(mouseScreen, points, cam, CanEditControls);

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
                SelectPoint(hit.index);
                isDragging = true;
                dragTarget = DragTarget.Point;
                dragPointIndex = hit.index;
                if (anchorDragAxis == AnchorDragAxis.Y)
                {
                    BeginVerticalDrag(cam);
                }
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

        if (dragTarget == DragTarget.Point && anchorDragAxis == AnchorDragAxis.Y)
        {
            if (dragPointIndex < 0 || dragPointIndex >= points.Count) return;
            UpdateVerticalDrag();
        }
        else if (dragTarget == DragTarget.Point)
        {
            if (dragPointIndex < 0 || dragPointIndex >= points.Count) return;

            // ポイント移動時もスナップを適用する（グリッド/ノード/パス/直角スナップ）
            Vector3 snappedPos = ApplySnapping(newWorldPos);

            var pt = points[dragPointIndex];
            // XZ移動時に相対高さを維持する
            float relY = lastSnapWasElevated && IsHeightEditable()
                ? lastSnapRelHeight : pt.position.y - Utils.GetGroundHeight(pt.position);
            snappedPos.y = Utils.GetGroundHeight(snappedPos) + relY;

            // 手動調整済みの制御点はアンカーに追従させ、未調整の制御点だけ再計算する
            MoveAnchorPosition(dragPointIndex, snappedPos);
        }
        else if (dragTarget == DragTarget.ControlIn || dragTarget == DragTarget.ControlOut)
        {
            if (dragPointIndex < 0 || dragPointIndex >= points.Count) return;
            // 制御点直下の地形は使わない。高さはアンカーの縦断勾配から決まる。
            MoveControlXZ(dragPointIndex, dragTarget == DragTarget.ControlIn, worldPos.x, worldPos.z);
        }

        UpdateShapePreview();
    }

    /// <summary>
    /// 高さ(Y)ドラッグの基準を記録する。ノードの現在位置と、開始時のカーソルが指す高さを保存し、
    /// 以後の UpdateVerticalDrag はここからの差分でノード標高を動かす。
    /// </summary>
    private void BeginVerticalDrag(Camera cam)
    {
        var pt = points[dragPointIndex];
        verticalDragAnchor = pt.position;
        verticalDragStartRelY = pt.position.y - Utils.GetGroundHeight(pt.position);
        verticalDragStartPlaneY = ResolveVerticalDragY(cam, verticalDragAnchor);
    }

    /// <summary>
    /// 高さ(Y)ドラッグの1フレーム分。カーソルが指す高さの変化量をノードの相対高さに加え、
    /// キー入力と同じく min_height で下限を切って MoveAnchorPosition へ渡す(XZは動かさない)。
    /// XZ吸着は高さに無関係なので適用しない。
    /// </summary>
    private void UpdateVerticalDrag()
    {
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        float planeY = ResolveVerticalDragY(cam, verticalDragAnchor);
        float relY = Mathf.Max(verticalDragStartRelY + (planeY - verticalDragStartPlaneY), GetMinHeightFromMaster());

        Vector3 newPos = points[dragPointIndex].position;
        newPos.y = Utils.GetGroundHeight(newPos) + relY;
        MoveAnchorPosition(dragPointIndex, newPos);
    }

    /// <summary>
    /// カーソルが指す「高さ」をワールドYで返す。基準ノードを通りカメラ正面を向いた鉛直平面と
    /// マウスレイの交点を使うので、斜め視点ではカーソルの上下とノードの上下が一致する。
    /// ほぼ真上からの視点では平面とレイが平行に近づき交点が飛ぶため、ノード奥行きでの
    /// 1ピクセルあたりの長さに画面縦座標を掛けた値で代用する(差分でしか使わないので原点は任意)。
    /// </summary>
    private float ResolveVerticalDragY(Camera cam, Vector3 anchor)
    {
        Vector2 mouse = InputUtil.MousePosition;
        Ray ray = cam.ScreenPointToRay(mouse);

        Vector3 normal = cam.transform.forward;
        normal.y = 0f;
        if (normal.sqrMagnitude >= VERTICAL_DRAG_MIN_PLANE_NORMAL_SQR)
        {
            var plane = new Plane(normal.normalized, anchor);
            if (plane.Raycast(ray, out float enter))
            {
                return ray.GetPoint(enter).y;
            }
        }

        float depth = Mathf.Max(Vector3.Dot(anchor - cam.transform.position, cam.transform.forward), 1f);
        float metersPerPixel = 2f * depth * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Screen.height;
        return mouse.y * metersPerPixel;
    }

    /// <summary>
    /// InputHandlerからのドラッグ終了通知。
    /// </summary>
    public void ProcessMouseDragEnd(Vector3 worldPos)
    {
        if (isDragging && phase == RoadEditorPhase.Editing)
        {
            bool movedAnchor = dragTarget == DragTarget.Point;
            isDragging = false;
            dragPointIndex = -1;
            dragTarget = DragTarget.None;
            Update3DPreview();
            EmitShapeUpdate();
            // 頂点移動はUIパネルの選択ノード情報(位置・標高)も更新する
            if (movedAnchor) EmitSelectNode();
        }
    }

    /// <summary>
    /// 指定スクリーン座標が編集中のポイントまたは制御点上にあるか判定する。
    /// CameraController との入力優先競合を解決するために使用する。
    /// </summary>
    public bool IsMouseOverEditHandle(Vector2 screenPos, Camera cam)
    {
        if (!IsEditMode || cam == null) return false;
        RoadOverlayHit hit = hitDetector.TestHit(screenPos, points, cam, CanEditControls);
        return hit.type == HitTargetType.Point || hit.type == HitTargetType.ControlIn || hit.type == HitTargetType.ControlOut;
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
        anchorDragAxis = AnchorDragAxis.XZ;
        hoveredEdgeIndex = -1;

        RecomputeAllControlPoints();
        // finalized=true で送り、UI側に権威あるバックエンド干渉問い合わせを促す。
        CommitShapeChange(finalized: true);
        PlaySound(finalizeShapeSound);
        Debug.Log($"RoadPlanManager: 形状確定 ({points.Count}点) → Editingフェーズ");
    }

    // =====================================================================
    // Editingフェーズの入力処理
    // =====================================================================

    private void HandleEditingClick(Vector3 worldPos)
    {
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        Vector2 mouseScreen = InputUtil.MousePosition;
        RoadOverlayHit hit = hitDetector.TestHit(mouseScreen, points, cam, CanEditControls);

        // クリック時はドラッグ状態を設定しない。
        // ドラッグ開始は ProcessMouseDrag 内で isDragging == false の際に
        // ハンドルを再検出して行う。これによりクリックとドラッグの
        // 状態管理が InputHandler 側のドラッグ閾値判定と整合する。
        if (hit.type == HitTargetType.Point)
        {
            if (hit.index == selectedPointIndex)
            {
                // 選択済みノードの再クリックは移動軸のトグル。標高編集不可のバリアントはXZ固定。
                anchorDragAxis = (anchorDragAxis == AnchorDragAxis.XZ && IsHeightEditable())
                    ? AnchorDragAxis.Y : AnchorDragAxis.XZ;
            }
            else
            {
                SelectPoint(hit.index);
            }
            EmitSelectNode();
        }
        else if (hit.type == HitTargetType.Edge)
        {
            InsertPointOnEdge(hit.index, worldPos);
        }
        else
        {
            SelectPoint(-1);
        }
    }

    /// <summary>
    /// ノードの選択を切り替える。別ノード(または未選択)への切り替えで移動軸はXZに戻す。
    /// 同じノードの再指定では軸を保つ(ドラッグ開始時の再検出で軸が勝手に戻らないようにするため)。
    /// </summary>
    private void SelectPoint(int index)
    {
        if (index != selectedPointIndex)
        {
            anchorDragAxis = AnchorDragAxis.XZ;
        }
        selectedPointIndex = index;
    }

    private void HandleEditingRightClick(Vector3 worldPos)
    {
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        Vector2 mouseScreen = InputUtil.MousePosition;
        RoadOverlayHit hit = hitDetector.TestHit(mouseScreen, points, cam, CanEditControls);

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

        ReleaseControlHeights(edgeIndex, edgeIndex + 2);
        RecomputeAllControlPoints();
        CommitShapeChange();
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

        if (selectedPointIndex == index)
        {
            selectedPointIndex = -1;
            anchorDragAxis = AnchorDragAxis.XZ;
        }
        else if (selectedPointIndex > index) selectedPointIndex--;

        ReleaseControlHeights(index - 1, index);
        RecomputeAllControlPoints();
        CommitShapeChange();
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
        RoadOverlayHit hit = hitDetector.TestHit(mouseScreen, points, cam, CanEditControls);

        hoveredEdgeIndex = (hit.type == HitTargetType.Edge) ? hit.index : -1;
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
        if (TutorialRuntime.PathRestricted || !IsHeightEditable()) return;
        height = Mathf.Max(height, GetMinHeightFromMaster());
        if (nodeIndex == -1)
        {
            currentHeight = height;
            UpdateShapePreview();
            return;
        }
        if (!IsEditMode || nodeIndex < 0 || nodeIndex >= points.Count) return;
        Vector3 position = points[nodeIndex].position;
        position.y = Utils.GetGroundHeight(position) + height;
        MoveAnchorPosition(nodeIndex, position);
        CommitShapeChange();
        EmitSelectNode();
    }

    /// <summary>
    /// IPC経由でノードの盛土オフセット変更を受信したときの処理。
    /// nodeIndex が -1 の場合は currentEmbankmentHeight（次回配置時の盛土）を更新する。
    /// 盛土は全道路タイプで利用可能（高架不可の街路でも盛土は可能）。
    /// </summary>
    public void OnChangeEmbankmentFromIpc(int nodeIndex, float embankmentHeight)
    {
        if (TutorialRuntime.PathRestricted) return;
        embankmentHeight = Mathf.Clamp(embankmentHeight, -5f, 5f);
        if (nodeIndex == -1)
        {
            currentEmbankmentHeight = embankmentHeight;
            UpdateShapePreview();
            return;
        }
        if (!IsEditMode || nodeIndex < 0 || nodeIndex >= points.Count) return;
        var point = points[nodeIndex];
        if (Mathf.Approximately(point.embankmentHeight, embankmentHeight)) return;
        point.embankmentHeight = embankmentHeight;
        point.proposalGenerated = false;
        points[nodeIndex] = point;
        CommitShapeChange();
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

        int index = HasSelectedNode ? selectedPointIndex : -1;
        OnChangeNodeFromIpc(index, DisplayHeight + delta);
        if (index == -1) EmitCurrentHeight();
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

        int index = HasSelectedNode ? selectedPointIndex : -1;
        OnChangeEmbankmentFromIpc(index, DisplayEmbankment + delta);
        if (index == -1) EmitCurrentHeight();
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

    /// <summary>制御点のYは所属アンカー直下の地盤基準。バックエンドの非追従スパンと共通。</summary>
    private static List<float> ControlToRelativeList(Vector3? control, Vector3 anchor)
    {
        return RoadShapeGeometry.ControlToRelativeList(control, anchor);
    }

    private static Vector3 RestoreControl(float x, float relativeY, float z, Vector3 anchor)
    {
        return RoadShapeGeometry.RestoreControl(x, relativeY, z, anchor);
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

        List<float> startControl = points.Count > 0 ? ControlToRelativeList(points[0].control_out, points[0].position) : null;
        List<float> endControl = points.Count > 0 ? ControlToRelativeList(points[points.Count - 1].control_in, points[points.Count - 1].position) : null;

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
                    ["control_in"] = ControlToRelativeList(points[i].control_in, points[i].position),
                    ["control_out"] = ControlToRelativeList(points[i].control_out, points[i].position),
                });
            }
        }

        // 各ポイントの盛土オフセットを送信
        var embankmentHeights = new List<float>();
        foreach (var pt in points)
        {
            embankmentHeights.Add(pt.embankmentHeight);
        }

        // 線形は地形・保存設計標高・複線展開を解決する /paths/validate の全線検証で確定する。
        // 描画用のローカル推定をゲートにすると、権威検証自体が呼ばれず誤判定から復帰できない。
        IpcClient.Instance.EmitEditPathUpdateShape(pathId, PathType, pointsList, startControl, endControl, embankmentHeights, waypointControls, Curved, PathFollowTerrainOverride, finalized,
            phase == RoadEditorPhase.Editing && points.Count >= 2 && CheckElevationIntersections().Count == 0
            && CheckRiverCrossings().Count == 0 && CheckSeaLakeCrossings().Count == 0 && CheckMaxSpanViolations().Count == 0
            && (!RoadPath.IsRopewayVariant(PathSubtype) || !HasAlignmentViolation)
            && TutorialRuntime.AcceptsPath(points, PathSubtype, Curved),
            BuildShapeSyncExtras());
    }

}
