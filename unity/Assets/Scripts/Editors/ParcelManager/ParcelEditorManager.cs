using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

/// <summary>
/// 土地区画作成モード(STATE_EDIT_PARCEL)の中核。
/// 作成モード(道路沿い / ブロック一括 / ランダム / 単一区画 / 分割系)の
/// **真実源はこのクラスの <see cref="CurrentMode"/>** で、3Dビューでの地点・パス・区画の選択と
/// プレビュー描画、入力プリセットの切り替えまでを担う。
///
/// 操作モデル(道路敷設・地区編集と同型):
/// - **Ctrl押下でカーソル位置にラジアルメニュー(<see cref="ParcelEditControlMenu"/>)が開き、作成モードを切り替える**。
///   モードを切り替えると入力プリセットと選択状態がその場でリセットされ、`edit_parcel:set_mode` で
///   UI(作成パネル)へ通知される(パネルは入力欄の構成をそれに合わせる)。
/// - 幅・奥行き・角度・平均面積などの数値はTauriの作成パネルで設定する。
/// - 3Dビューのクリックで対象(地点/パス/区画)を選び、パネルの確定ボタンで作成する。
/// - **確定してもモードから抜けない**。UIが `edit_parcel:reset_selection` を送り、
///   <see cref="ResetSelection"/> が選択だけを捨てて同じモードのまま次のクリックを待つ(連続作成)。
///
/// 作成モードの選択はパネルのタイルからも行える(従来どおり `world:change_state` が届く)が、
/// どちらの経路でも最終的に <see cref="ApplyCreationMode"/> に集約される。
/// </summary>
public class ParcelEditorManager : MonoBehaviour
{
    /// <summary>シーンに1つだけ置かれる想定のインスタンス(Awakeで自己登録)。</summary>
    public static ParcelEditorManager Instance { get; private set; }

    [Header("References")]
    public ParcelPlanRenderer parcelPlanRenderer;  // 土地区画プレビュー描画クラス
    public Camera gameCamera;                      // ゲーム用カメラ
    public ParcelManager parcelManager;            // 確定済み区画描画を担う既存マネージャ

    /// <summary>3Dビューでの選択対象の種別。作成モードごとに1つ決まる。</summary>
    public enum SelectionKind
    {
        Point,    // 地形上の地点をクリック
        Path,     // 既存パスをクリック
        Parcel,   // 既存区画をクリック
    }

    /// <summary>作成モード1件の定義(ラジアルメニュー・HUD・入力プリセットの共通ソース)。</summary>
    public class ModeInfo
    {
        public string Id;              // parcel_creation_mode (UIと共通の識別子)
        public string Label;           // ラジアルメニュー・HUDに出す名前
        public string IconName;        // Resources/UIIcons のアイコン名(未配置ならラベル頭文字で代替)
        public string Hint;            // HUDに出す次の操作の案内
        public SelectionKind Kind;
        public int Preset;             // InputModePreset
        public bool HiddenFromMenu;    // ラジアルメニューには出さない(UI未実装のモード)
    }

    /// <summary>
    /// 作成モードの一覧。ラジアルメニューの項目順もこの順序に従う。
    /// UI(ParcelCreationPanel)の CREATION_SECTIONS と同じ並び(作成4種 → 分割2種)にしてある。
    /// </summary>
    public static readonly List<ModeInfo> Modes = new List<ModeInfo>
    {
        new ModeInfo { Id = "along_path", Label = "道路沿い", IconName = "parcel-along-path",
            Hint = "区画を作る位置の道路をクリック", Kind = SelectionKind.Path, Preset = InputModePreset.PARCEL_CREATION_PATH },
        new ModeInfo { Id = "block", Label = "ブロック一括", IconName = "parcel-block",
            Hint = "ブロック内の地点をクリック", Kind = SelectionKind.Point, Preset = InputModePreset.PARCEL_CREATION_POINT },
        new ModeInfo { Id = "random", Label = "ランダム", IconName = "parcel-random",
            Hint = "地点をクリック / ドラッグで角度", Kind = SelectionKind.Point, Preset = InputModePreset.PARCEL_CREATION_POINT },
        new ModeInfo { Id = "empty_area", Label = "単一区画", IconName = "parcel-single",
            Hint = "地点をクリック / ドラッグで角度", Kind = SelectionKind.Point, Preset = InputModePreset.PARCEL_CREATION_POINT },
        new ModeInfo { Id = "split_rectangle", Label = "矩形分割", IconName = "parcel-split-rect",
            Hint = "分割する区画をクリック", Kind = SelectionKind.Parcel, Preset = InputModePreset.PARCEL_CREATION_SPLIT },
        new ModeInfo { Id = "split_random", Label = "ランダム分割", IconName = "parcel-split-random",
            Hint = "分割する区画をクリック", Kind = SelectionKind.Parcel, Preset = InputModePreset.PARCEL_CREATION_SPLIT },
        // UIに入口が無い(パネルのタイルに出ていない)ため、ラジアルメニューにも出さない。
        // world:change_state で直接指定されたときのために定義だけ持つ。
        new ModeInfo { Id = "split_perimeter", Label = "外周分割", IconName = "parcel-split-rect",
            Hint = "分割する区画をクリック", Kind = SelectionKind.Parcel, Preset = InputModePreset.PARCEL_CREATION_SPLIT,
            HiddenFromMenu = true },
    };

    /// <summary>既定の作成モード(モード指定なしで STATE_EDIT_PARCEL に入ったとき)。</summary>
    public const string DefaultMode = "empty_area";

    /// <summary>土地区画作成モードがアクティブかどうか(ラジアルメニュー・HUDの表示条件)。</summary>
    public bool IsActive => isActive;

    /// <summary>現在の作成モード(parcel_creation_mode)。真実源はここ。</summary>
    public string CurrentMode => currentMode;

    /// <summary>現在の作成モードの定義。未知のIDなら既定モードの定義を返す。</summary>
    public ModeInfo CurrentModeInfo => GetModeInfo(currentMode);

    private bool isActive = false;
    private string currentMode = DefaultMode;

    // 選択モード
    private bool pointSelectionMode = false;
    private bool pathSelectionMode = false;
    private bool parcelSelectionMode = false;

    // 選択されたオブジェクト情報
    private Vector3 selectedPoint;
    private bool hasSelectedPoint = false;
    private int selectedParcelId = -1;
    private int selectedPathId = -1;

    // along_path で選ばれた道路上のアンカー(クリック地点を中心線へ射影した点)。
    // 作成範囲が「クリック地点」のとき、UIがどこに区画を作るかの基準になる。
    private Vector3? selectedPathAnchor;
    private Vector3 selectedPathAnchorTangent = Vector3.forward;

    // アンカーマーカー用のオーバーレイ(PathTopologyEditManager の分割点マーカーと同型)。
    private OverlayMeshRenderer anchorOverlayRenderer;
    private GameObject anchorOverlayObj;

    // アンカーマーカーの色と、道路を横切る棒の長さ(m)。
    private static readonly Color AnchorMarkerColor = new Color(0.30f, 1.00f, 0.45f);
    private const float AnchorMarkerHalfLength = 8f;

    // 選択された区画のプレビューデータ
    private List<Vector3> previewBlockOutline = new List<Vector3>();
    private List<List<Vector3>> previewParcels = new List<List<Vector3>>();

    // empty_area 用の矩形パラメータと回転ドラッグ状態。
    // 幅・奥行きはUIパネルの入力欄が真実源で、edit_parcel:set_rect_params で逐次受け取る
    // (回転ドラッグ中のローカル矩形プレビューをパネルの設定値と一致させるため)。
    private float currentWidth = 10f;
    private float currentHeight = 20f;
    private float currentAngle = 0f;
    private bool isRotating = false;
    private bool justSelectedPoint = false;

    // レイキャスト用レイヤーマスク
    private int groundLayerMask;
    private int pathLayerMask;
    private int parcelLayerMask;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (anchorOverlayObj != null) Destroy(anchorOverlayObj);
    }

    void Start()
    {
        // レイヤーマスクを初期化
        groundLayerMask = LayerMask.GetMask("Ground", "Terrain");
        pathLayerMask = LayerMask.GetMask("Path");
        parcelLayerMask = LayerMask.GetMask("Parcel");

        // ParcelPlanRendererの自動検索・生成
        if (parcelPlanRenderer == null)
        {
            parcelPlanRenderer = FindAnyObjectByType<ParcelPlanRenderer>();
            if (parcelPlanRenderer == null)
            {
                Debug.Log("ParcelPlanRendererが見つかりません。このGameObjectに追加します。");
                parcelPlanRenderer = gameObject.AddComponent<ParcelPlanRenderer>();
            }
        }

        // 作成モードに入るまでは何も選択しない(入力プリセット側でも弾かれるが状態も持たない)
        ClearWorldSelectionState();
    }

    void Update()
    {
        // 作成モード中のみ処理する。選択モードのフラグで判定してはいけない:
        // 地点/パス/区画を選んだ時点でフラグは落ちるため、「選択済みをEscで選び直す」という
        // 一番Escを使いたい場面で反応しなくなる。
        if (!isActive) return;

        // Escキーで選択をリセット(UI上でも有効)。モードからは抜けず、同じモードで選び直せる。
        if (InputUtil.KeyWasPressedThisFrame(Key.Escape))
        {
            ResetSelection(notifyUi: true);
            return;
        }

        // 注：各種HandleXXXSelection関数はInputHandlerシステムから呼び出される形に修正
        // 実際の選択処理は各InputHandlerクラスで実行されるため、
        // ここでは特別な処理は不要
    }

    void LateUpdate()
    {
        DrawPathAnchorMarker();
    }

    /// <summary>
    /// 道路沿い作成で選んだアンカー（クリック地点）に、道路を横切る棒と円のマーカーを描く。
    /// アンカーが無いフレームでも空フレームを描いてマーカーを消す必要があるため、
    /// オーバーレイを作ったあとは毎フレーム通す。
    /// </summary>
    private void DrawPathAnchorMarker()
    {
        if (anchorOverlayRenderer == null) return;
        if (gameCamera == null) gameCamera = Camera.main;
        if (gameCamera == null) return;

        anchorOverlayRenderer.BeginFrame();

        if (isActive && CurrentModeInfo.Kind == SelectionKind.Path && selectedPathAnchor.HasValue)
        {
            Vector3 anchor = selectedPathAnchor.Value + Vector3.up * 0.3f;
            Vector3 normal = new Vector3(-selectedPathAnchorTangent.z, 0f, selectedPathAnchorTangent.x);
            anchorOverlayRenderer.AddSolidLine(
                anchor - normal * AnchorMarkerHalfLength,
                anchor + normal * AnchorMarkerHalfLength,
                AnchorMarkerColor, 3f);
            anchorOverlayRenderer.AddCircle(anchor, 9f, AnchorMarkerColor);
        }

        anchorOverlayRenderer.EndFrame(gameCamera);
    }

    /// <summary>
    /// アンカーマーカー用のオーバーレイを用意する。道路面や起伏に埋もれないよう
    /// 深度テストを無効化する（RoadPlanManager のギズモと同じ扱い）。
    /// </summary>
    private void EnsureAnchorOverlay()
    {
        if (anchorOverlayObj != null) return;

        anchorOverlayObj = new GameObject("ParcelPathAnchorOverlay");
        anchorOverlayRenderer = anchorOverlayObj.AddComponent<OverlayMeshRenderer>();
        Material mat = new Material(Shader.Find("Custom/OverlayLine"));
        mat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
        anchorOverlayRenderer.Initialize(mat);
    }

    #region 作成モードの制御

    /// <summary>
    /// 作成モードの定義を取得する。未知のIDには既定モード(単一区画)の定義を返す。
    /// </summary>
    public static ModeInfo GetModeInfo(string modeId)
    {
        foreach (var info in Modes)
        {
            if (info.Id == modeId) return info;
        }
        return Modes[0];
    }

    /// <summary>
    /// 土地区画作成モードを開始する(STATE_EDIT_PARCEL 受信時に IpcStateCoordinator から呼ばれる)。
    /// UI起点の遷移なので、モードをUIへ返す必要はない(notifyUi=false)。
    /// </summary>
    public void EnterCreationMode(string modeId)
    {
        isActive = true;
        ApplyCreationMode(modeId, notifyUi: false);
    }

    /// <summary>
    /// 作成モードを切り替える(Ctrlラジアルメニューから呼ばれる)。
    /// Unity起点の切り替えなので、UIの作成パネルへ `edit_parcel:set_mode` で通知して
    /// 入力欄の構成を合わせてもらう。同じモードを選び直した場合は選択のリセットだけ行う。
    /// </summary>
    public void SetCreationMode(string modeId)
    {
        if (!isActive) return;
        if (modeId == currentMode)
        {
            // 同じモードを選び直したときは「選び直し」の意味に取り、選択だけ捨てる
            // (パネル側の選択表示も一緒に消すため通知する)。
            ResetSelection(notifyUi: true);
            return;
        }
        ApplyCreationMode(modeId, notifyUi: true);
    }

    /// <summary>
    /// 作成モードを適用する。入力プリセットの切り替えと選択状態のリセットをまとめて行う
    /// (UI経由・ラジアルメニュー経由のどちらもここに集約する)。
    /// </summary>
    private void ApplyCreationMode(string modeId, bool notifyUi)
    {
        ModeInfo info = GetModeInfo(modeId);
        currentMode = info.Id;

        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(info.Preset);
        }

        ResetSelection();

        if (notifyUi)
        {
            IpcClient.Instance?.EmitEditParcelSetMode(currentMode);
        }

        Debug.Log($"ParcelEditorManager: 作成モード -> {currentMode} (preset={info.Preset})");
    }

    /// <summary>
    /// 選択状態とプレビューを捨てて、現在のモードのまま次の選択を受け付ける状態に戻す。
    /// 作成の確定後(UIが `edit_parcel:reset_selection` を送る)・Escキー・モード切替から呼ばれ、
    /// 「1回作ったらモードを抜ける」ことなく連続して区画を作れるようにするための要。
    ///
    /// notifyUi=true のときは `edit_parcel:set_mode`(モードは据え置き)をUIへ送る。
    /// パネルはこれを受けて選択済み表示・プレビューを捨てるため、Unity側でEscを押したのに
    /// パネルだけ「地点を選択済み」のまま確定できてしまう食い違いを防げる。
    /// </summary>
    public void ResetSelection(bool notifyUi = false)
    {
        ClearWorldSelectionState();
        if (!isActive) return;

        ModeInfo info = CurrentModeInfo;
        switch (info.Kind)
        {
            case SelectionKind.Point:
                pointSelectionMode = true;
                break;
            case SelectionKind.Path:
                pathSelectionMode = true;
                break;
            case SelectionKind.Parcel:
                parcelSelectionMode = true;
                break;
        }

        if (notifyUi)
        {
            IpcClient.Instance?.EmitEditParcelSetMode(currentMode);
        }
    }

    /// <summary>
    /// 土地区画作成モードを終了する(STATE_NORMAL など他ステートへの遷移時)。
    /// ラジアルメニュー・カーソルHUDは IsActive を見て自動的に閉じる。
    /// </summary>
    public void ExitCreationMode()
    {
        isActive = false;
        ClearWorldSelectionState();

    }

    /// <summary>
    /// 回転ドラッグ中のローカル矩形プレビューに使う幅・奥行きをUIパネルから受け取る
    /// (`edit_parcel:set_rect_params`)。バックエンドのプレビューは確定値で描き直されるが、
    /// ドラッグ中はローカル計算のためパネルの入力値を持っていないと形が食い違う。
    /// </summary>
    public void SetRectPreviewSize(float width, float height)
    {
        if (width > 0f) currentWidth = width;
        if (height > 0f) currentHeight = height;
    }

    #endregion

    #region 選択モード制御

    /// <summary>
    /// すべての選択モードを無効化
    /// </summary>
    public void ClearWorldSelectionState()
    {
        pointSelectionMode = false;
        pathSelectionMode = false;
        parcelSelectionMode = false;
        hasSelectedPoint = false;
        isRotating = false;
        justSelectedPoint = false;
        currentAngle = 0f;
        selectedParcelId = -1;
        selectedPathId = -1;
        selectedPathAnchor = null;

        // 視覚フィードバックをクリア
        ClearPreview();
    }

    /// <summary>
    /// 地点選択モードを有効化
    /// </summary>
    public void EnablePointSelectionMode()
    {
        ClearWorldSelectionState();
        pointSelectionMode = true;
        Debug.Log($"地点選択モードを有効化しました。pointSelectionMode = {pointSelectionMode}");
    }

    /// <summary>
    /// 道路選択モードを有効化
    /// </summary>
    public void EnablePathSelectionMode()
    {
        ClearWorldSelectionState();
        pathSelectionMode = true;
        Debug.Log("道路選択モードを有効化しました");
    }

    /// <summary>
    /// 土地区画選択モードを有効化
    /// </summary>
    public void EnableParcelSelectionMode()
    {
        ClearWorldSelectionState();
        parcelSelectionMode = true;
        Debug.Log("土地区画選択モードを有効化しました");
    }

    /// <summary>
    /// 地点選択モードかどうかを返す
    /// </summary>
    public bool IsPointSelectionMode()
    {
        return pointSelectionMode;
    }

    /// <summary>
    /// 道路選択モードかどうかを返す
    /// </summary>
    public bool IsPathSelectionMode()
    {
        return pathSelectionMode;
    }

    /// <summary>
    /// 土地区画選択モードかどうかを返す
    /// </summary>
    public bool IsParcelSelectionMode()
    {
        return parcelSelectionMode;
    }

    /// <summary>
    /// 3Dビューで対象(地点/パス/区画)を選択済みかどうか。カーソルHUDの案内文に使う。
    /// </summary>
    public bool HasSelection()
    {
        switch (CurrentModeInfo.Kind)
        {
            case SelectionKind.Point: return hasSelectedPoint;
            case SelectionKind.Path: return selectedPathId >= 0;
            case SelectionKind.Parcel: return selectedParcelId >= 0;
            default: return false;
        }
    }

    /// <summary>
    /// 地点が選択されたときの処理。
    /// 既に地点が選ばれていて、かつ近距離（1m未満）の場合はドラッグ開始とみなし、
    /// 地点の更新を行わない。
    /// </summary>
    public void OnPointSelected(Vector3 point)
    {
        if (hasSelectedPoint)
        {
            float dist = Vector2.Distance(
                new Vector2(selectedPoint.x, selectedPoint.z),
                new Vector2(point.x, point.z)
            );
            if (dist < 1.0f)
            {
                return;
            }
        }

        selectedPoint = point;
        hasSelectedPoint = true;
        justSelectedPoint = true;
        Debug.Log($"地点が選択されました: {point}");

        // IPC: 土地区画作成モード用の地点選択イベントをUIに通知
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitEditParcelSelectPoint(point);
        }

        // 選択モードを無効化（次のクリックでドラッグ開始判定に使う）
        pointSelectionMode = false;
    }

    /// <summary>
    /// 道路が選択されたときの処理。
    ///
    /// クリック地点はパスの中心線へ射影してから保持・通知する。UIの作成範囲が
    /// 「クリック地点」のとき、この点がどのセルを作るかのアンカーになるため、
    /// マーカーの見た目と実際に作られる位置を一致させる必要がある。
    /// </summary>
    public void OnPathSelected(int pathId, Vector3[] waypoints, Vector3 clickPosition)
    {
        selectedPathId = pathId;
        selectedPathAnchor = ProjectOntoPath(pathId, clickPosition, out selectedPathAnchorTangent);
        EnsureAnchorOverlay();
        Debug.Log($"道路が選択されました: Path_{pathId} anchor={selectedPathAnchor}");

        // IPC: 土地区画作成モード用のパス選択イベントをUIに通知
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitEditParcelSelectPath(pathId, selectedPathAnchor.Value);
        }

        // 選択モードを無効化
        pathSelectionMode = false;
    }

    /// <summary>
    /// 点を指定パスの中心線へXZ平面で射影し、その位置での進行方向も返す。
    /// 中心線を取得できない場合はクリック地点をそのまま返す
    /// （バックエンド側でも射影されるため、作られる位置は破綻しない）。
    /// </summary>
    private Vector3 ProjectOntoPath(int pathId, Vector3 point, out Vector3 tangent)
    {
        tangent = Vector3.forward;

        var pathManager = FindAnyObjectByType<PathManager>();
        if (pathManager == null || !pathManager.TryGetPathPolyline(pathId, out List<Vector3> polyline))
        {
            return point;
        }

        Vector3 best = polyline[0];
        float bestSqr = float.MaxValue;
        for (int i = 0; i < polyline.Count - 1; i++)
        {
            Vector3 a = polyline[i];
            Vector3 b = polyline[i + 1];
            Vector2 ab = new Vector2(b.x - a.x, b.z - a.z);
            float lenSqr = ab.sqrMagnitude;

            float t = 0f;
            if (lenSqr > 1e-6f)
            {
                t = Mathf.Clamp01(Vector2.Dot(new Vector2(point.x - a.x, point.z - a.z), ab) / lenSqr);
            }
            Vector3 candidate = Vector3.Lerp(a, b, t);

            float dx = point.x - candidate.x;
            float dz = point.z - candidate.z;
            float sqr = dx * dx + dz * dz;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                best = candidate;
                Vector3 dir = new Vector3(b.x - a.x, 0f, b.z - a.z);
                tangent = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;
            }
        }
        return best;
    }

    /// <summary>
    /// 土地区画が選択されたときの処理
    /// </summary>
    public void OnParcelSelected(int parcelId)
    {
        selectedParcelId = parcelId;
        Debug.Log($"土地区画が選択されました: Parcel_{parcelId}");

        // IPC: 土地区画作成モード用の区画選択イベントをUIに通知
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitEditParcelSelectParcel(parcelId);
        }

        // 選択モードを無効化
        parcelSelectionMode = false;
    }

    /// <summary>
    /// 基準点が既に選択済みかどうかを返す
    /// </summary>
    public bool HasSelectedPoint()
    {
        return hasSelectedPoint;
    }

    /// <summary>
    /// 左クリックドラッグで角度変更を開始する
    /// </summary>
    public void StartRotationDrag()
    {
        if (!hasSelectedPoint) return;
        if (justSelectedPoint)
        {
            // 同一フレーム内の地点選択クリックとドラッグ開始を区別する。
            // 地点を選んだ直後のクリックでは角度変更を開始せず、次のクリックから有効にする。
            justSelectedPoint = false;
            return;
        }
        isRotating = true;
    }

    /// <summary>
    /// ドラッグ中にマウス位置と基準点の角度を計算し、仮の矩形プレビューを描画する。
    /// リアルタイムフィードバックのため、バックエンド通信を待たずにローカルで計算する。
    /// </summary>
    public void UpdateRotationPreview(Vector3 currentPosition)
    {
        if (!isRotating) return;

        Vector3 diff = currentPosition - selectedPoint;
        currentAngle = Mathf.Atan2(diff.x, diff.z) * Mathf.Rad2Deg;

        // 仮の矩形ポリゴンを計算してプレビュー描画（パネルで設定中の幅・奥行きで概形を示す）
        List<Vector3> polygon = CalculateRectanglePolygon(selectedPoint, currentWidth, currentHeight, currentAngle);
        previewBlockOutline = null;
        previewParcels = new List<List<Vector3>> { polygon };

        if (parcelPlanRenderer != null)
        {
            parcelPlanRenderer.RenderBlockPreview(null, previewParcels);
        }
    }

    /// <summary>
    /// 角度変更ドラッグを終了し、確定した角度をUIに通知する。
    /// UI側は角度を受信後、バックエンドから正式なプレビューを取得してUnityに送信する。
    /// </summary>
    public void EndRotationDrag()
    {
        if (!isRotating) return;
        isRotating = false;

        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitEditParcelUpdateRotation(currentAngle);
        }
    }

    /// <summary>
    /// 中心点、縦横、角度から回転矩形の頂点リストを計算する。
    /// 角度0°はZ+方向（前方）とし、正の値で反時計回り（X+方向が+90°）となる。
    /// </summary>
    private List<Vector3> CalculateRectanglePolygon(Vector3 center, float width, float height, float angleDeg)
    {
        float halfW = width * 0.5f;
        float halfH = height * 0.5f;
        float rad = angleDeg * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad);
        float sin = Mathf.Sin(rad);

        Vector3[] localCorners = new Vector3[]
        {
            new Vector3(-halfW, 0f, -halfH),
            new Vector3( halfW, 0f, -halfH),
            new Vector3( halfW, 0f,  halfH),
            new Vector3(-halfW, 0f,  halfH),
        };

        List<Vector3> worldCorners = new List<Vector3>();
        foreach (var local in localCorners)
        {
            float x = local.x * cos - local.z * sin;
            float z = local.x * sin + local.z * cos;
            worldCorners.Add(new Vector3(center.x + x, center.y, center.z + z));
        }

        return worldCorners;
    }

    #endregion

    #region 地点選択の処理

    /// <summary>
    /// 空の土地区画のプレビューを描画
    /// empty_area の場合、元区画と新規区画が同じなので、
    /// 新規区画のみ（青色）で描画するよう blockPolygon は null にする。
    /// </summary>
    public void RenderEmptyParcelPreview(float[][] polygonPoints)
    {
        ClearPreview();

        // 頂点座標をVector3に変換
        List<Vector3> parcelPolygon = ConvertFloatArrayToVector3List(polygonPoints);

        // 空き地作成の場合、元区画と新規区画が同じなので
        // blockPolygon を null にして、新規区画のみ青色で描画する
        previewBlockOutline = null;

        // 土地区画として描画
        List<List<Vector3>> parcels = new List<List<Vector3>>();
        parcels.Add(parcelPolygon);
        previewParcels = parcels;

        // レンダラーで描画
        if (parcelPlanRenderer != null)
        {
            Debug.Log($"ParcelEditorManager: RenderBlockPreviewを呼び出します (Parcels Count: {previewParcels.Count})");
            parcelPlanRenderer.RenderBlockPreview(null, previewParcels);
        }
        else
        {
            Debug.LogError("ParcelEditorManager: parcelPlanRendererがnullです");
        }
    }

    #endregion

    #region 道路選択の処理

    #endregion

    #region 土地区画選択と分割の処理

    #endregion

    #region 共通処理

    /// <summary>
    /// プレビュー表示をクリア
    /// </summary>
    public void ClearPreview()
    {
        if (parcelPlanRenderer != null)
        {
            parcelPlanRenderer.ClearPreview();
        }
        if (previewBlockOutline != null)
            previewBlockOutline.Clear();
        else
            previewBlockOutline = new List<Vector3>();
        if (previewParcels != null)
            previewParcels.Clear();
        else
            previewParcels = new List<List<Vector3>>();
    }

    /// <summary>
    /// サーバから返却された [ [x,z], [x,z], ... ] を Vector3 の List に変換するヘルパー
    /// （y は地形から取得）
    /// </summary>
    private List<Vector3> ConvertFloatArrayToVector3List(float[][] coords)
    {
        List<Vector3> list = new List<Vector3>();
        foreach (var c in coords)
        {
            float x = c[0];
            float z = c[1];

            // 地形の高さを取得
            float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z));

            list.Add(new Vector3(x, y, z));
        }
        return list;
    }

    #endregion
}
