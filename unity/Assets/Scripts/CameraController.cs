using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;
using TMPro;
using UnityEngine.UI;
using UnityEngine.EventSystems;

[DefaultExecutionOrder(-100)]
public class CameraController : MonoBehaviour
{
    [Header("基準速度設定（バックエンド基準）")]
    public float baseMoveSpeed = 150.0f;    // カメラの移動速度（バックエンド基準）
    public float rotationSpeed = 50f; // 回転速度（スケール無関係）
    public float baseHeightSpeed = 2000f;   // 上下移動速度（バックエンド基準）
    public float focusDuration = 0.2f;    // フォーカス移動にかける時間（秒）
    public float baseFocusHeight = 100f;  // フォーカス時のカメラ高度（バックエンド基準）
    
    [Header("高度による速度調整設定（バックエンド基準）")]
    public float baseReferenceHeight = 150f; // 基準高度（この高度で等倍速度）
    public float minSpeedRatio = 0.3f; // 最小速度比率（30%まで減速可能）
    public float maxSpeedRatio = 5.0f; // 最大速度比率（5倍まで加速可能）
    
    [Header("初期位置設定（バックエンド基準）")]
    public float baseInitialHeight = 150f; // 初期カメラ高度（バックエンド基準）

    [Header("マウス入力設定")]
    public float edgePanThreshold = 20f;              // 画面端からのマウス移動開始閾値（ピクセル）
    public float mouseWheelSensitivity = 0.2f;        // マウスホイールによる高度変更係数
    public float mouseRotationSpeed = 0.2f;           // 右クリックドラッグ時の回転速度
    public float minOrbitPitch = 5f;                  // カメラの仰角（地表からの角度）の下限
    public float maxOrbitPitch = 80f;                 // カメラの仰角の上限
    public LayerMask rotationGroundMask = Physics.DefaultRaycastLayers; // 回転軸取得用レイヤーマスク
    public float rotationRaycastDistance = 5000f;     // 回転軸取得時のレイキャスト距離
    
    [Header("Terrain Settings")]
    public LayerMask terrainLayerMask = 1; // Terrain レイヤーへのレイキャスト用

    [Header("UI設定")]
    [SerializeField] private TextMeshProUGUI CameraText;
    [SerializeField] private Button cameraModeButton;
    [SerializeField] private Sprite camera90Icon;   // 見下ろしモード時のアイコン
    [SerializeField] private Sprite camera45Icon;   // 通常モード時のアイコン
    [SerializeField] private Button flyModeButton;  // フライモード切り替えボタン
    [SerializeField] private Sprite flyModeOnIcon;  // フライモード中のアイコン
    [SerializeField] private Sprite flyModeOffIcon; // 通常時（フライモードOFF）のアイコン

    [Header("フライモード設定")]
    // フライモード時のマウス感度（度/ピクセル）。マウスの移動量にこの値を掛けて視線角を変化させる。
    public float flyMouseSensitivity = 0.1f;
    // フライモード時の移動速度倍率。通常モードと同じ高度連動速度に対してこの値を掛ける（1.0で等倍）。
    // WASD（視線基準の前後左右）と R/F（ワールドYの上下）の両方に適用される。
    public float flySpeedMultiplier = 1.0f;

    [Header("高度による移動速度調整（前後左右）")]
    public float moveHeightSpeedMultiplier = 0.5f; // 前後左右の速度変化の強さ（値が小さいほど影響が弱い、0=無効）
    
    // スケール適用済み実行時パラメータ
    private float scaledMoveSpeed;
    private float scaledHeightSpeed;
    private float scaledInitialHeight;
    private float scaledReferenceHeight;

    private float currentHeight; // 高さを記録
    private bool isFocusing = false; // フォーカス中フラグ
    private Vector3 focusTargetPosition; // フォーカス対象位置
    private float focusStartTime; // フォーカス開始時間

    // シングルトンインスタンス
    public static CameraController Instance { get; private set; }

    // 真上モード管理用
    private bool isTopDownMode = false;
    private Vector3 savedPosition;
    private Quaternion savedRotation;
    private bool savedOrthographic;
    private float savedFieldOfView;
    private Camera cam;

    // フライモード管理用。
    // フライモード中はカメラを固定高度に保つ通常モードのロジックを全てスキップし、
    // 「視線基準のWASD移動＋マウス視点回転」の専用処理だけを実行する（透視投影が前提）。
    private bool isFlyMode = false;
    private float flyYaw;   // フライモードの水平視線角（ワールドY軸まわり、度）
    private float flyPitch; // フライモードの仰俯角（度、[-89,89]でクランプしてジンバルロックを防ぐ）

    // マップ範囲（=標高マップの範囲）へのカメラクランプに使う TerrainManager 参照。
    // GetMapWidth / GetMapHeight はインスタンスメソッドのため保持する（初回に遅延取得）。
    private TerrainManager terrainManagerRef;
    // TerrainManager の遅延取得を一度だけ試みたか。存在しないシーンで毎フレーム走査するのを防ぐ。
    private bool terrainManagerLookupDone = false;

    // フライモードのキャプション表示に使う ModeTextDisplay 参照（初回に遅延取得）。
    private ModeTextDisplay modeTextDisplayRef;
    private bool modeTextDisplayLookupDone = false;

    // ドラッグ開始判定で参照する編集マネージャー。全オブジェクト走査を押下ごとに行わないよう保持し、
    // 未生成・破棄済み(Unity の null 判定)のときだけ探し直す。編集開始直後の押下も取りこぼさない。
    private ParcelShapeEditManager parcelShapeEditManagerRef;
    private RoadPlanManager roadPlanManagerRef;
    private TransitPlanManager transitPlanManagerRef;
    private GradingAreaEditManager gradingAreaEditManagerRef;

    // フライモードの有効状態を外部へ公開する（他システムからの参照用）。
    public bool IsFlyMode => isFlyMode;

    // マウス操作用
    private bool isMouseRotating = false;
    private Vector3 rotationPivot;
    private float orbitYaw;
    private float orbitPitch;
    private float rotationDistance;
    private Vector3 previousMousePosition;

    // 右ドラッグによる回転の保留状態。
    // 右押下では即座に回転を開始せず、一定ピクセル動いたら回転を確定する。
    // これにより「右クリック（その場）」と「右ドラッグ（回転）」を区別でき、
    // 道路敷設中の右クリック確定（FinalizePath）が回転開始に食われるのを防ぐ。
    private bool isPendingRotate = false;
    private Vector3 rotateStartScreenPosition;

    // マウスドラッグによるカメラ移動（Google マップ方式）
    private bool isMouseDragging = false;
    private bool isPendingDrag = false;
    private Vector3 dragStartScreenPosition;
    private Vector3 dragStartCameraPosition;     // ドラッグ開始時のカメラ位置
    private Quaternion dragStartCameraRotation;  // ドラッグ開始時のカメラ回転
    private Vector3 dragStartGroundPosition;     // ドラッグ開始時の地面座標
    public float dragStartThreshold = 5f; // ドラッグ確定までの画面ピクセル閾値
    public float maxDragDistance = 1500f; // この距離以上離れた地点を掴んだ場合はドラッグ移動を開始しない

    // マウスドラッグによる慣性
    private Vector3 dragVelocity; // ドラッグ終了時の速度（ワールド座標系/秒）
    public float dragInertiaDecay = 3.0f; // 慣性の減衰係数（大きいほど早く止まる）

    public bool IsMouseDragging => isMouseDragging;
    public bool IsPendingDrag => isPendingDrag;

    /// <summary>
    /// ゲーム開始時にカメラを戻すデフォルト位置。
    /// シーン配置時のXZ位置を基準とし、Yはスケール適用済みの初期高度とする。
    /// </summary>
    private Vector3 defaultPosition;

    /// <summary>
    /// セッションにカメラ状態が保存されていないときの向き。シーン配置時の回転をそのまま使う。
    /// </summary>
    private Quaternion defaultRotation;

    // --- セッションのカメラ状態同期(バックエンドの City.camera) ---
    // 視点はセーブデータに含めるため、カメラが静止した時点でバックエンドへ PATCH する。
    // 毎フレーム送ると通信が無駄になるので、直近の姿勢からの移動・回転で「動いた」を検出し、
    // 一定時間静止してから1回だけ送る。
    private bool syncEnabled;            // 保存視点を適用してから true。session:reset / game:end で false
    private bool syncDirty;              // 前回送信以降にカメラが動いた
    private bool syncInFlight;           // PATCH 送信中
    private float syncStillSince;        // 最後に動きを検出した時刻(unscaled)
    private Vector3 syncLastPosition;
    private Quaternion syncLastRotation;
    private Coroutine applySessionCameraCoroutine;
    private const float SYNC_MOVE_THRESHOLD_M = 1.0f;
    private const float SYNC_ANGLE_THRESHOLD_DEG = 0.5f;
    private const float SYNC_STILL_SECONDS = 1.0f;
    private const float SYNC_RETRY_SECONDS = 5.0f;

    /// <summary>GET /api/v1/city の応答のうちカメラ状態だけを読む型。</summary>
    private class CityCameraResponse
    {
        public CityCameraProps city;
    }

    private class CityCameraProps
    {
        public CameraStatePayload camera;
    }

    /// <summary>
    /// バックエンドと共有するカメラ状態(City.camera)。位置はワールド座標(m)、
    /// yaw は北(+Z)を0とする時計回りの度、pitch は見下ろし角の度(正=下向き)。ロールは常に0。
    /// </summary>
    public class CameraStatePayload
    {
        public float x;
        public float y;
        public float z;
        public float yaw;
        public float pitch;
    }

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(gameObject);
        }
    }

    void Start()
    {
        cam = GetComponent<Camera>();
        // スケール適用済みの値を計算
        UpdateScaledValues();

        // デフォルト位置を保存（XZはシーン配置位置を尊重、Yは初期高度）
        defaultPosition = transform.position;
        defaultPosition.y = scaledInitialHeight;
        defaultRotation = transform.rotation;

        // 初期位置をデフォルト位置に設定
        ResetToDefaultPosition();

        // ゲーム状態の変化を購読し、ゲーム開始時にカメラ位置をリセットする
        if (GameStateManager.Instance != null)
        {
            GameStateManager.Instance.OnStateChanged += HandleGameStateChanged;
        }
        
        if (CameraText != null)
        {
            CameraText.text = "";
        }

        // ボタンのイベント登録と初期表示更新
        if (cameraModeButton != null)
        {
            cameraModeButton.onClick.AddListener(ToggleTopDownMode);
        }
        if (flyModeButton != null)
        {
            flyModeButton.onClick.AddListener(ToggleFlyMode);
        }
        UpdateUIButtons();

        currentHeight = transform.position.y; // 初期の高さを記録
        Debug.Log($"CameraController: スケール適用済み設定 - 移動速度: {scaledMoveSpeed}, 高度速度: {scaledHeightSpeed}, 初期高度: {scaledInitialHeight}");
    }

    private void OnDisable()
    {
        // MonoBehaviour が無効化される際にイベント購読を解除し、メモリリークを防ぐ
        if (GameStateManager.Instance != null)
        {
            GameStateManager.Instance.OnStateChanged -= HandleGameStateChanged;
        }

        // フライモード中に無効化された場合、ロックしたカーソルを必ず元に戻す
        // （ロックしたまま放置するとゲーム全体でカーソルが使えなくなるため）。
        if (isFlyMode)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    /// <summary>
    /// ゲーム状態が変化した際に呼ばれるハンドラ。
    /// ゲーム開始（Game 状態）になったらセッションに保存された視点を適用し、
    /// ゲーム終了（Menu 状態）になったら視点の同期を止める。
    /// </summary>
    private void HandleGameStateChanged(GameStateManager.GameState state)
    {
        if (state == GameStateManager.GameState.Game)
        {
            ApplySessionCamera();
        }
        else
        {
            SuspendCameraSync();
        }
    }

    /// <summary>
    /// カメラをデフォルト位置にリセットする。
    /// 真上モードの場合は orthographicSize も合わせて更新する。
    /// </summary>
    private void ResetToDefaultPosition()
    {
        transform.position = defaultPosition;
        currentHeight = defaultPosition.y;

        if (isTopDownMode && cam != null)
        {
            cam.orthographicSize = currentHeight;
        }
    }

    /// <summary>
    /// セッションに保存されたカメラ状態(GET /api/v1/city の camera)を取得してカメラへ適用し、
    /// 以後の視点変化のバックエンド同期を開始する。ゲーム開始(game:start)と、既にゲーム中での
    /// セッション切り替えの両方から呼ばれる。取得中は同期を止め、前セッションの視点が
    /// 新しい都市の保存値を上書きしないようにする。
    /// </summary>
    public bool IsSessionCameraReady => syncEnabled && applySessionCameraCoroutine == null;

    public void ApplySessionCamera()
    {
        SuspendCameraSync();
        applySessionCameraCoroutine = StartCoroutine(ApplySessionCameraCoroutine());
    }

    /// <summary>
    /// 視点のバックエンド同期を止める。session:reset(新しい都市の準備中)と game:end で呼ぶ。
    /// 保存視点の取得が進行中なら、それも打ち切る(古いセッションの値を適用しないため)。
    /// </summary>
    public void SuspendCameraSync()
    {
        syncEnabled = false;
        syncDirty = false;
        if (applySessionCameraCoroutine != null)
        {
            StopCoroutine(applySessionCameraCoroutine);
            applySessionCameraCoroutine = null;
        }
    }

    private IEnumerator ApplySessionCameraCoroutine()
    {
        // 取得失敗(ロード直後の一時的な通信不良など)は数回待って再試行し、それでも駄目なら
        // 既定視点で開始する。視点が決まらないままロードだけ進む状態を長引かせない。
        CameraStatePayload saved = null;
        bool fetched = false;
        for (int attempt = 0; attempt < 3 && !fetched; attempt++)
        {
            yield return ApiClient.Get<CityCameraResponse>(
                "/city/",
                response =>
                {
                    fetched = true;
                    saved = response?.city?.camera;
                },
                error => Debug.LogWarning($"CameraController: カメラ状態の取得に失敗: {error}"));
            if (!fetched) yield return new WaitForSecondsRealtime(2f);
        }

        UpdateScaledValues();
        if (saved != null)
        {
            SetView(new Vector3(saved.x, saved.y, saved.z), Quaternion.Euler(saved.pitch, saved.yaw, 0f));
        }
        else
        {
            // 未設定の都市は原点の既定視点(シーン配置時の向き・初期高度)で始める。
            SetView(new Vector3(0f, scaledInitialHeight, 0f), defaultRotation);
        }
        defaultPosition = new Vector3(transform.position.x, scaledInitialHeight, transform.position.z);

        // 適用した姿勢を「送信済み」として同期を開始する(適用直後の姿勢は送らない)。
        syncLastPosition = transform.position;
        syncLastRotation = transform.rotation;
        syncDirty = false;
        syncStillSince = Time.unscaledTime;
        syncEnabled = true;
        applySessionCameraCoroutine = null;
    }

    /// <summary>
    /// 視点変化の検出と、静止後のバックエンド送信。Update の早期 return(フライモード・
    /// ラジアルメニュー表示中など)に左右されないよう LateUpdate で行う。
    /// </summary>
    private void LateUpdate()
    {
        if (DriveMode.IsOpen) return;
        if (!syncEnabled) return;
        if (GameStateManager.Instance != null && !GameStateManager.Instance.IsGameStarted()) return;

        bool moved = (transform.position - syncLastPosition).sqrMagnitude >= SYNC_MOVE_THRESHOLD_M * SYNC_MOVE_THRESHOLD_M
            || Quaternion.Angle(transform.rotation, syncLastRotation) >= SYNC_ANGLE_THRESHOLD_DEG;
        if (moved)
        {
            syncLastPosition = transform.position;
            syncLastRotation = transform.rotation;
            syncStillSince = Time.unscaledTime;
            syncDirty = true;
            return;
        }
        if (syncDirty && !syncInFlight && Time.unscaledTime - syncStillSince >= SYNC_STILL_SECONDS)
        {
            syncDirty = false;
            StartCoroutine(PushCameraStateCoroutine());
        }
    }

    /// <summary>現在のカメラ姿勢をバックエンドと共有する形(CameraStatePayload)にする。</summary>
    public CameraStatePayload BuildCameraStatePayload()
    {
        Vector3 euler = transform.rotation.eulerAngles;
        return new CameraStatePayload
        {
            x = transform.position.x,
            y = transform.position.y,
            z = transform.position.z,
            yaw = euler.y,
            pitch = NormalizePitchAngle(euler.x),
        };
    }

    private IEnumerator PushCameraStateCoroutine()
    {
        syncInFlight = true;
        var payload = new System.Collections.Generic.Dictionary<string, object>
        {
            ["camera"] = BuildCameraStatePayload()
        };
        bool failed = false;
        yield return ApiClient.Patch("/city/", payload, null, (code, message) => failed = true);
        syncInFlight = false;
        if (failed && syncEnabled)
        {
            // 失敗時は間隔を空けて再送する(セッション切り替え中の一時的な失敗で連打しない)。
            syncDirty = true;
            syncStillSince = Time.unscaledTime + SYNC_RETRY_SECONDS - SYNC_STILL_SECONDS;
        }
    }

    /// <summary>
    /// カメラの位置・回転を即座に指定し、内部の高さ状態も同期する。
    /// 生成物に合わせて任意の視点へ一発でフレーミングするための入口
    /// （メッシュテストシーンなど、フォーカス演出ではなく即時配置したい場面で使う）。
    /// currentHeight を更新しないと、Update 末尾の「高さをワールド基準で維持する」処理で
    /// 直後に元の高さへ戻されてしまうため、ここで必ず同期する。
    /// </summary>
    public void SetView(Vector3 position, Quaternion rotation)
    {
        if (cam == null)
        {
            cam = GetComponent<Camera>();
        }
        EndMouseRotation();
        StopFocus();
        isMouseDragging = false;
        isPendingDrag = false;
        dragVelocity = Vector3.zero;

        transform.position = position;
        transform.rotation = rotation;
        currentHeight = Mathf.Max(0f, position.y);

        // フライモード中は視線角を内部変数で持っているため、置いた向きから再初期化する
        // (更新しないと次のマウス入力で以前の向きへ飛ぶ)。
        if (isFlyMode)
        {
            Vector3 euler = rotation.eulerAngles;
            flyYaw = euler.y;
            flyPitch = NormalizePitchAngle(euler.x);
        }

        if (isTopDownMode && cam != null)
        {
            cam.orthographicSize = currentHeight;
        }
    }

    void Update()
    {
        if (GameStateManager.Instance != null && !GameStateManager.Instance.IsWorldVisible) return;
        if (DriveMode.BlocksCamera) return;
        // GameStateManager を持つシーン（ゲーム本編）では、ゲーム開始前やロードによる
        // カメラロック中はカメラ操作を一切受け付けない。
        // GameStateManager が存在しないシーン（メッシュテスト等の単体シーン）では、
        // これらの制約はなく常に操作可能とする。
        if (GameStateManager.Instance != null)
        {
            if (!GameStateManager.Instance.IsGameStarted())
            {
                return;
            }


        }

        // Shift+F でフライモードをトグルする。
        // トグルしたフレームは他のカメラ入力（F単押しによる高度下降など）を処理せず即抜けし、
        // 切り替えの瞬間に通常モードの操作が混入しないようにする。
        bool shiftHeld = InputUtil.KeyPressed(Key.LeftShift) || InputUtil.KeyPressed(Key.RightShift);
        if (shiftHeld && InputUtil.KeyWasPressedThisFrame(Key.F))
        {
            ToggleFlyMode();
            ClampPositionToCityBoundary();
            return;
        }

        // フライモード中は専用処理のみを実行する（通常モードのドラッグ・回転・高度維持は行わない）。
        if (isFlyMode)
        {
            HandleFlyModeMovement();
            ClampPositionToCityBoundary();
            return;
        }

        // ラジアルメニュー表示中はカメラの回転・パン・移動・ズームを受け付けない。
        // メニュー操作(マウス移動で項目ハイライト)がカメラを動かしてしまうのを防ぐ。
        // メニュー側がマウスボタン押下中(ドラッグ・回転中)の展開を抑止しているため、
        // ここで即抜けしても回転/パンの終了処理が取り残されることはない。
        if (RadialMenuController.IsOpen)
        {
            return;
        }



        if (isFocusing)
        {
            bool cancelFocus = InputUtil.AnyKeyDown || InputUtil.LeftMouseWasPressedThisFrame || InputUtil.RightMouseWasPressedThisFrame || Mathf.Abs(InputUtil.ScrollY) > 0.0001f;
            if (cancelFocus)
            {
                StopFocus();
            }
            else
            {
                UpdateFocusMovement();
                return;
            }
        }

        // フォーカス中の場合はフォーカス処理を優先
        // Uキーで真上モードを切り替え
        if (InputUtil.KeyWasPressedThisFrame(Key.U))
        {
            ToggleTopDownMode();
        }

        bool isPointerOverUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        if (!isPointerOverUI || isMouseRotating)
        {
            HandleMouseRotationInput();
        }

        // マウスドラッグによるカメラ移動を処理
        HandleMouseDragPan(isPointerOverUI);

        // 前後左右の移動
        float moveX = GetAxisRawHorizontal();
        float moveZ = GetAxisRawVertical();

        float combinedX = moveX;
        float combinedZ = moveZ;

        // 現在の高度に基づいて移動速度を調整
        float currentMoveSpeed = GetHeightAdjustedMoveSpeed();
        
        Vector3 move;
        if (isTopDownMode)
        {
            // 真上モードの場合：
            // 入力Z（上下）は画面上の上下（Local Y）に対応
            // 入力X（左右）は画面上の左右（Local X）に対応
            move = new Vector3(combinedX, combinedZ, 0) * currentMoveSpeed * Time.deltaTime;
        }
        else
        {
            // 通常モードの場合：
            // 入力Z（上下）はカメラの前方（Local Z）に対応
            // 入力X（左右）はカメラの右（Local X）に対応
            move = new Vector3(combinedX, 0, combinedZ) * currentMoveSpeed * Time.deltaTime;
        }

        transform.Translate(move, Space.Self);

        // ドラッグによる慣性を適用（キー入力がない場合のみ）
        if (!isMouseDragging && dragVelocity.sqrMagnitude > 0.01f)
        {
            if (Mathf.Abs(moveX) > 0.01f || Mathf.Abs(moveZ) > 0.01f ||
                isMouseRotating || isFocusing)
            {
                dragVelocity = Vector3.zero;
            }
            else
            {
                Vector3 inertiaMove = dragVelocity * Time.deltaTime;
                transform.position += new Vector3(inertiaMove.x, 0f, inertiaMove.z);

                // 速度を減衰させる
                dragVelocity *= Mathf.Max(0f, 1f - dragInertiaDecay * Time.deltaTime);

                currentHeight = transform.position.y;
                if (isTopDownMode && cam != null)
                {
                    cam.orthographicSize = currentHeight;
                }
            }
        }

        // 高さ変更（ワールド座標基準）
        if (InputUtil.KeyPressed(Key.R)) currentHeight += GetHeightAdjustedHeightSpeed() * Time.deltaTime;
        if (InputUtil.KeyPressed(Key.F)) currentHeight -= GetHeightAdjustedHeightSpeed() * Time.deltaTime;

        float scroll = InputUtil.ScrollY;
        if (!isPointerOverUI && Mathf.Abs(scroll) > Mathf.Epsilon)
        {
            if (isTopDownMode)
            {
                // 真上モードでは、マウスカーソル位置を中心に拡大縮小する。
                // orthographicSize を変更し、カメラを平行移動させて
                // マウスカーソルが指していたワールド座標が同じスクリーン位置に留まるようにする。

                // 変更前のマウス位置に対応するワールド座標を取得
                Vector3 mouseWorldBefore = cam.ScreenToWorldPoint(
                    new Vector3(InputUtil.MousePosition.x, InputUtil.MousePosition.y, cam.nearClipPlane + 10f)
                );

                float zoomFactor = 1f - scroll * mouseWheelSensitivity;
                float newSize = Mathf.Max(cam.orthographicSize * zoomFactor, 1f);

                cam.orthographicSize = newSize;
                currentHeight = newSize;

                // 変更後のマウス位置に対応するワールド座標を取得
                Vector3 mouseWorldAfter = cam.ScreenToWorldPoint(
                    new Vector3(InputUtil.MousePosition.x, InputUtil.MousePosition.y, cam.nearClipPlane + 10f)
                );

                // 差分をオフセットとしてカメラに適用（高さは維持）
                Vector3 offset = mouseWorldBefore - mouseWorldAfter;
                offset.y = 0f;
                transform.position += offset;
                currentHeight = transform.position.y;
                cam.orthographicSize = currentHeight;
            }
            else
            {
                // 通常モードでは、マウスカーソル位置を中心に拡大縮小する。
                // マウス位置から地面（または y=0 平面）へのレイを飛ばし、交点を取得。
                Ray ray = cam.ScreenPointToRay(InputUtil.MousePosition);
                Vector3 zoomTarget;
                bool hasTarget = false;

                if (Physics.Raycast(ray, out RaycastHit hit, rotationRaycastDistance, rotationGroundMask))
                {
                    zoomTarget = hit.point;
                    hasTarget = true;
                }
                else
                {
                    Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
                    if (groundPlane.Raycast(ray, out float enter))
                    {
                        zoomTarget = ray.origin + ray.direction * enter;
                        zoomTarget.y = Utils.GetGroundHeight(zoomTarget);
                        hasTarget = true;
                    }
                    else
                    {
                        zoomTarget = Vector3.zero;
                    }
                }

                if (hasTarget)
                {
                    // カメラと対象点の距離を計算
                    float distance = Vector3.Distance(transform.position, zoomTarget);

                    // マウスホイールの入力に応じて距離を変化させる。
                    // scroll > 0 で距離が縮まり（ズームイン）、scroll < 0 で距離が広がる（ズームアウト）。
                    float zoomFactor = 1f - scroll * mouseWheelSensitivity;
                    float newDistance = distance * zoomFactor;
                    newDistance = Mathf.Max(newDistance, 0.1f); // あまり近づきすぎないように制限

                    // 対象点を中心に、距離比率に応じてカメラ位置を移動
                    Vector3 direction = (transform.position - zoomTarget).normalized;
                    Vector3 newPosition = zoomTarget + direction * newDistance;

                    // 地面貫通を防止
                    float newGroundHeight = Utils.GetGroundHeight(newPosition);
                    if (newPosition.y < newGroundHeight)
                    {
                        newPosition.y = newGroundHeight;
                    }

                    transform.position = newPosition;
                    currentHeight = transform.position.y;
                }
                else
                {
                    // 対象点が取得できない場合（空を向いているなど）は、従来通り視線方向に沿って移動
                    float groundHeight = Utils.GetGroundHeight(transform.position);
                    float heightAboveGround = Mathf.Max(0f, transform.position.y - groundHeight);
                    float speedRatio = heightAboveGround / scaledReferenceHeight;
                    speedRatio = Mathf.Clamp(speedRatio, minSpeedRatio, maxSpeedRatio);

                    float wheelMoveSpeed = scaledHeightSpeed * mouseWheelSensitivity * speedRatio;
                    Vector3 moveDelta = transform.forward * scroll * wheelMoveSpeed;

                    // 移動後の地面貫通を防止する。
                    Vector3 newPosition = transform.position + moveDelta;
                    float newGroundHeight = Utils.GetGroundHeight(newPosition);
                    if (newPosition.y < newGroundHeight)
                    {
                        newPosition.y = newGroundHeight;
                    }
                    transform.position = newPosition;
                    currentHeight = transform.position.y;
                }
            }
        }

        // 安全装置として高さが地面以下にならないようクランプする
        currentHeight = Mathf.Max(0f, currentHeight);

        // カメラの高さをワールド基準で維持
        transform.position = new Vector3(transform.position.x, currentHeight, transform.position.z);
        if (isTopDownMode && cam != null)
        {
            cam.orthographicSize = currentHeight;
        }

        // 回転（Q/EキーでワールドY軸基準）
        // 回転（Q/EキーでワールドY軸基準）
        // 真上モードでも回転を許可する
        float rotation = 0;
        if (InputUtil.KeyPressed(Key.Q)) rotation = -1;
        if (InputUtil.KeyPressed(Key.E)) rotation = 1;
        if (rotation != 0)
        {
            transform.Rotate(Vector3.up, rotation * rotationSpeed * Time.deltaTime, Space.World);
        }

        // 任意のキー入力でフォーカスを中断
        if (InputUtil.AnyKeyDown)
        {
            StopFocus();
        }

        if (InputUtil.LeftMouseWasPressedThisFrame || InputUtil.RightMouseWasPressedThisFrame || Mathf.Abs(scroll) > Mathf.Epsilon)
        {
            StopFocus();
        }

        // フライモードか否かに関わらず、カメラをマップ範囲（標高マップの範囲）の外へ出さない。
        ClampPositionToCityBoundary();
    }



    public void SetTopDownMode(bool enable)
    {
        if (cam == null) return;
        if (isTopDownMode == enable) return;

        if (enable)
        {
            EndMouseRotation();
            savedPosition = transform.position;
            savedRotation = transform.rotation;
            savedFieldOfView = cam.fieldOfView;
            savedOrthographic = cam.orthographic;

            transform.rotation = Quaternion.Euler(90f, transform.eulerAngles.y, 0f);
            cam.orthographic = true;
            cam.orthographicSize = currentHeight;
            isTopDownMode = true;
        }
        else
        {
            transform.position = savedPosition;
            transform.rotation = savedRotation;
            cam.orthographic = savedOrthographic;
            cam.fieldOfView = savedFieldOfView;
            currentHeight = transform.position.y;
            isTopDownMode = false;
        }
        UpdateUIButtons();
    }

    public void ToggleTopDownMode()
    {
        if (DriveMode.IsOpen) return;
        SetTopDownMode(!isTopDownMode);
    }

    /// <summary>
    /// フライモードの有効・無効を切り替える。
    /// フライモードは透視投影を前提とするため、真上（正射）モード中に有効化した場合はまず真上モードを解除する。
    /// 有効化時は現在のカメラ向きから視線角を初期化し、連続したマウス視点入力を得るためカーソルをロック・非表示にする。
    /// </summary>
    public void SetFlyMode(bool enable)
    {
        if (cam == null)
        {
            cam = GetComponent<Camera>();
        }
        if (isFlyMode == enable) return;

        if (enable)
        {
            // フライモードは透視投影が前提。真上（正射）モード中なら解除する。
            if (isTopDownMode)
            {
                SetTopDownMode(false);
            }

            // 進行中のマウス操作・フォーカス演出を打ち切り、フライモードの入力と競合させない。
            EndMouseRotation();
            StopFocus();
            isMouseDragging = false;
            isPendingDrag = false;
            dragVelocity = Vector3.zero;

            // 現在のカメラ向きからヨー・ピッチを初期化し、切り替え時に視線が飛ばないようにする。
            Vector3 euler = transform.rotation.eulerAngles;
            flyYaw = euler.y;
            flyPitch = NormalizePitchAngle(euler.x);

            // 画面端に妨げられない連続したマウス視点入力を得るため、カーソルをロック・非表示にする。
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            isFlyMode = true;

            // ステートキャプションを STATE_FLY_MODE の案内に切り替える。
            // （マウス入力の無効化自体は InputHandler が IsFlyMode を見て行う。）
            NotifyFlyModeCaption(true);
        }
        else
        {
            // ロックしたカーソルを元に戻す。
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // 通常モードの高度維持ロジックが直後に働くため、現在の高さを記録して同期する。
            currentHeight = transform.position.y;

            isFlyMode = false;

            // キャプションを直前のステートへ戻す。
            NotifyFlyModeCaption(false);
        }

        UpdateUIButtons();
    }

    /// <summary>
    /// フライモードの開始・終了に合わせてステートキャプション（ModeTextDisplay）を切り替える。
    /// ModeTextDisplay が存在しないシーン（メッシュテスト等）では何もしない。
    /// </summary>
    private void NotifyFlyModeCaption(bool entering)
    {
        if (modeTextDisplayRef == null)
        {
            if (modeTextDisplayLookupDone) return;
            modeTextDisplayLookupDone = true;
            modeTextDisplayRef = FindAnyObjectByType<ModeTextDisplay>();
            if (modeTextDisplayRef == null) return;
        }

        if (entering)
        {
            modeTextDisplayRef.ShowFlyMode();
        }
        else
        {
            modeTextDisplayRef.HideFlyMode();
        }
    }

    public void ToggleFlyMode()
    {
        if (DriveMode.IsOpen) return;
        SetFlyMode(!isFlyMode);
    }

    /// <summary>
    /// フライモードの毎フレーム処理。
    /// マウスの移動量で視線（ヨー・ピッチ）を変え、現在の視線を基準に WASD でカメラを移動する。
    /// 上下を向いて前進すれば上昇・下降もできる（視線基準の自由飛行）。加えて R/F でワールドY軸方向の上下移動も行える。
    /// </summary>
    private void HandleFlyModeMovement()
    {
        // マウス移動で視線を変える。カーソルは Locked のため画面端に関係なく連続した delta が得られる。
        Vector2 mouseDelta = Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
        flyYaw += mouseDelta.x * flyMouseSensitivity;
        flyPitch -= mouseDelta.y * flyMouseSensitivity;
        // 真上・真下付近でのジンバルロックを避けるため仰俯角を制限する。
        flyPitch = Mathf.Clamp(flyPitch, -89f, 89f);
        transform.rotation = Quaternion.Euler(flyPitch, flyYaw, 0f);

        // 現在の視線を基準に WASD で移動する。前方ベクトルはピッチを含むため、視線方向へまっすぐ進む。
        float moveX = GetAxisRawHorizontal(); // A/D（左右ストレイフ）
        float moveZ = GetAxisRawVertical();   // W/S（前後）
        Vector3 moveDir = transform.forward * moveZ + transform.right * moveX;

        float moveSpeed = GetHeightAdjustedMoveSpeed() * flySpeedMultiplier;
        transform.position += moveDir * moveSpeed * Time.deltaTime;

        // R/F でワールドY軸方向の上下移動。フライモード切り替えの Shift+F と競合しないよう Shift 非押下時のみ受け付ける。
        bool shiftHeld = InputUtil.KeyPressed(Key.LeftShift) || InputUtil.KeyPressed(Key.RightShift);
        if (!shiftHeld)
        {
            float vertical = 0f;
            if (InputUtil.KeyPressed(Key.R)) vertical += 1f;
            if (InputUtil.KeyPressed(Key.F)) vertical -= 1f;
            if (vertical != 0f)
            {
                transform.position += Vector3.up * (vertical * GetHeightAdjustedHeightSpeed() * flySpeedMultiplier * Time.deltaTime);
            }
        }

        // 地面貫通を防止する（地表より下へは潜らない）。
        float groundHeight = Utils.GetGroundHeight(transform.position);
        if (transform.position.y < groundHeight)
        {
            transform.position = new Vector3(transform.position.x, groundHeight, transform.position.z);
        }

        // 高度連動の速度計算のため、現在の高さを同期しておく。
        currentHeight = transform.position.y;
    }

    /// <summary>
    /// Quaternion.eulerAngles が返す [0,360) のピッチ角を、扱いやすい [-180,180) の範囲へ正規化する。
    /// </summary>
    private float NormalizePitchAngle(float pitch)
    {
        if (pitch > 180f) pitch -= 360f;
        return pitch;
    }

    /// <summary>
    /// カメラを詳細地形と背景地形を合わせた範囲にクランプする。
    /// マップは中心が原点のワールド座標で扱われる（CameraCellChecker / UnityCellToBackendCell と同じ慣習）ため、
    /// 有効範囲は X が [-mapWidth/2, +mapWidth/2]、Z が [-mapHeight/2, +mapHeight/2] となる。
    /// TerrainManager が存在しないシーン（メッシュテスト等）ではクランプを行わない。
    /// </summary>
    private void ClampPositionToCityBoundary()
    {
        if (terrainManagerRef == null)
        {
            // ここに到達するのはゲーム開始済み・カメラロック解除後（=地形設定ロード済み）なので、
            // このタイミングで一度だけ取得を試みれば十分。存在しないシーンでは以後クランプしない。
            if (terrainManagerLookupDone) return;
            terrainManagerLookupDone = true;
            terrainManagerRef = FindAnyObjectByType<TerrainManager>();
            if (terrainManagerRef == null) return;
        }

        float halfWidth = terrainManagerRef.GetMapWidth() / 2f;
        float halfHeight = terrainManagerRef.GetMapHeight() / 2f;

        Vector3 pos = transform.position;
        Rect background = terrainManagerRef.BackgroundBounds;
        pos.x = Mathf.Clamp(pos.x, Mathf.Min(-halfWidth, background.xMin), Mathf.Max(halfWidth, background.xMax));
        pos.z = Mathf.Clamp(pos.z, Mathf.Min(-halfHeight, background.yMin), Mathf.Max(halfHeight, background.yMax));
        transform.position = pos;
    }

    private void UpdateUIButtons()
    {
        // Mode Buttonの更新
        if (cameraModeButton != null && cameraModeButton.image != null)
        {
            if (isTopDownMode)
            {
                // 見下ろしモード: camera90
                cameraModeButton.image.sprite = camera90Icon;
            }
            else
            {
                // 通常モード: camera45
                cameraModeButton.image.sprite = camera45Icon;
            }
            cameraModeButton.image.enabled = true; // 常に表示
            var c = cameraModeButton.image.color;
            c.a = 1f;
            cameraModeButton.image.color = c;
        }

        // Fly Mode Buttonの更新。
        // ON/OFF両方のアイコンが割り当てられている場合のみ状態に応じてスプライトを切り替える。
        if (flyModeButton != null && flyModeButton.image != null)
        {
            if (flyModeOnIcon != null && flyModeOffIcon != null)
            {
                flyModeButton.image.sprite = isFlyMode ? flyModeOnIcon : flyModeOffIcon;
            }
            flyModeButton.image.enabled = true; // 常に表示
            var fc = flyModeButton.image.color;
            fc.a = 1f;
            flyModeButton.image.color = fc;
        }
    }

    private float GetAxisRawHorizontal()
    {
        float val = 0f;
        if (InputUtil.KeyPressed(Key.D) || InputUtil.KeyPressed(Key.RightArrow)) val += 1f;
        if (InputUtil.KeyPressed(Key.A) || InputUtil.KeyPressed(Key.LeftArrow))  val -= 1f;
        return val;
    }

    private float GetAxisRawVertical()
    {
        float val = 0f;
        if (InputUtil.KeyPressed(Key.W) || InputUtil.KeyPressed(Key.UpArrow))    val += 1f;
        if (InputUtil.KeyPressed(Key.S) || InputUtil.KeyPressed(Key.DownArrow))  val -= 1f;
        return val;
    }

    private void UpdateScaledValues()
    {
        scaledMoveSpeed = baseMoveSpeed;
        scaledHeightSpeed = baseHeightSpeed;
        scaledInitialHeight = baseInitialHeight;
        scaledReferenceHeight = baseReferenceHeight;
    }

    private float GetHeightAdjustedMoveSpeed()
    {
        // 現在の高度と基準高度の比率を計算
        float heightRatio = currentHeight / scaledReferenceHeight;
        
        // 高度による速度調整係数を計算（指数関数的な調整）
        float speedMultiplier;
        if (moveHeightSpeedMultiplier <= 0.0f)
        {
            // 調整無効の場合は等倍
            speedMultiplier = 1.0f;
        }
        else
        {
            // より弱い指数調整: ratio^(multiplier)
            // multiplier=0.5の場合、高度2倍で速度1.414倍、高度0.5倍で速度0.707倍となり変化が緩やかになる
            float exponent = moveHeightSpeedMultiplier;
            speedMultiplier = Mathf.Pow(heightRatio, exponent);
        }
        
        // 最小・最大速度比率でクランプ
        speedMultiplier = Mathf.Clamp(speedMultiplier, minSpeedRatio, maxSpeedRatio);
        
        float adjustedSpeed = scaledMoveSpeed * speedMultiplier;
        
        return adjustedSpeed;
    }

    private float GetHeightAdjustedHeightSpeed()
    {
        // 高度0でゼロ、高度が高いほど速度が上がるように調整
        // 現在の高度と基準高度の比率を計算
        float heightRatio = currentHeight / scaledReferenceHeight;
        
        // 高度に応じた速度調整（基準高度で最大速度に到達）
        // heightRatio が 0～1なら比率そのまま、1以上なら1（最大速度）
        float heightSpeedAdjustment = Mathf.Min(heightRatio, 1.0f);
        
        float adjustedSpeed = scaledHeightSpeed * heightSpeedAdjustment;
        
        return adjustedSpeed;
    }

    private void UpdateFocusMovement()
    {
        // 経過時間を計算
        float elapsedTime = Time.time - focusStartTime;
        float t = Mathf.Clamp01(elapsedTime / focusDuration);

        Vector3 currentPosition = transform.position;
        Vector3 newPosition = Vector3.Lerp(currentPosition, focusTargetPosition, t);
        transform.position = newPosition;
        
        // 目標位置に到達したかチェック（誤差0.1以内）
        if (Vector3.Distance(newPosition, focusTargetPosition) < 0.1f)    
        {
            StopFocus();
        }
    }

    private void StopFocus()
    {
        if (isFocusing)
        {
            isFocusing = false;
            currentHeight = transform.position.y; // 現在の高さを記録
            Debug.Log("カメラフォーカス終了");
        }
    }

    private void HandleMouseRotationInput()
    {
        if (cam == null || isTopDownMode) return;

        // 右押下では即座に回転を開始せず、保留状態にする。
        if (InputUtil.RightMouseWasPressedThisFrame)
        {
            isPendingRotate = true;
            rotateStartScreenPosition = InputUtil.MousePosition;
        }

        // 右リリース：回転中なら終了、保留を解除する。
        // 保留のまま閾値未満で離した場合は「右クリック」であり、回転は一度も開始されない。
        if (InputUtil.RightMouseWasReleasedThisFrame)
        {
            if (isMouseRotating)
            {
                EndMouseRotation();
            }
            isPendingRotate = false;
            return;
        }

        // 保留中：閾値を超えて動いたら回転を確定して開始する（左ドラッグパンと同じ判定）。
        if (isPendingRotate && !isMouseRotating)
        {
            if ((InputUtil.MousePosition - rotateStartScreenPosition).magnitude >= dragStartThreshold)
            {
                StartMouseRotation();
            }
        }

        if (isMouseRotating)
        {
            if (!InputUtil.RightMouseIsPressed)
            {
                EndMouseRotation();
            }
            else
            {
                UpdateMouseRotation();
            }
        }
    }

    private void StartMouseRotation()
    {
        StopFocus();

        previousMousePosition = InputUtil.MousePosition;
        rotationPivot = GetRotationPivotPoint();
        rotationDistance = Vector3.Distance(transform.position, rotationPivot);
        rotationDistance = Mathf.Max(rotationDistance, 0.01f);

        Vector3 toCamera = transform.position - rotationPivot;
        Vector3 toCameraNormalized = toCamera.normalized;
        orbitYaw = Mathf.Atan2(toCamera.x, toCamera.z) * Mathf.Rad2Deg;
        orbitPitch = Mathf.Asin(toCameraNormalized.y) * Mathf.Rad2Deg;
        float clampedPitch = Mathf.Clamp(orbitPitch, minOrbitPitch, maxOrbitPitch);
        if (!Mathf.Approximately(clampedPitch, orbitPitch))
        {
            orbitPitch = clampedPitch;
            ApplyOrbitPosition();
        }

        isMouseRotating = true;
    }

    private void UpdateMouseRotation()
    {
        Vector3 mouseDelta = InputUtil.MousePosition - previousMousePosition;
        previousMousePosition = InputUtil.MousePosition;

        if (mouseDelta.sqrMagnitude <= Mathf.Epsilon) return;

        orbitYaw += mouseDelta.x * mouseRotationSpeed;
        orbitPitch = Mathf.Clamp(orbitPitch - mouseDelta.y * mouseRotationSpeed, minOrbitPitch, maxOrbitPitch);

        ApplyOrbitPosition();
    }

    private void EndMouseRotation()
    {
        isMouseRotating = false;
        isPendingRotate = false;
    }

    private void ApplyOrbitPosition()
    {
        float yawRad = orbitYaw * Mathf.Deg2Rad;
        float pitchRad = orbitPitch * Mathf.Deg2Rad;

        float cosPitch = Mathf.Cos(pitchRad);
        Vector3 offset = new Vector3(
            Mathf.Sin(yawRad) * cosPitch,
            Mathf.Sin(pitchRad),
            Mathf.Cos(yawRad) * cosPitch
        ) * rotationDistance;

        transform.position = rotationPivot + offset;
        transform.LookAt(rotationPivot);
        currentHeight = transform.position.y;

        if (isTopDownMode && cam != null)
        {
            cam.orthographicSize = currentHeight;
        }
    }

    private Vector3 GetRotationPivotPoint()
    {
        Ray ray = cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));

        // 回転中心は「画面中央が指している地表の点」でなければならない。
        // 地形タイルは "Ground" レイヤーのコライダー(TerrainCollider)を持つため、
        // これに限定してレイキャストすることで、起伏のある地形でも実際の地表面(丘の上など)と
        // 交差した点が得られる。
        //
        // rotationGroundMask(既定 DefaultRaycastLayers)のように全レイヤーを対象にすると、
        // ワールドY≈0に配置された海面板(SeaObject)に当たってしまい、回転中心が地表ではなく
        // Y=0へ落ちる。起伏地形で「意図しないY=0を中心に回ってしまう」症状の原因になるため、
        // ここでは Ground レイヤーのみを対象にして海面板を除外する。
        int groundMask = LayerMask.GetMask("Ground");
        if (groundMask != 0 && Physics.Raycast(ray, out RaycastHit hit, rotationRaycastDistance, groundMask))
        {
            return hit.point;
        }

        // 地形コライダーに当たらない領域(コライダーを持たない超遠景メッシュ領域など)では、
        // CPU高さ配列に対してレイマーチし、起伏を考慮した地表面との交点を求める。
        // これも失敗した場合のみ、旧来の y=0 平面フォールバックに頼らず地表高さで補正する。
        if (RaycastTerrainHeightField(ray, out Vector3 marchPoint))
        {
            return marchPoint;
        }

        // 地平線より上を向いているなど地表交点が取れない場合の最終フォールバック。
        return transform.position + transform.forward * 10f;
    }

    /// <summary>
    /// 画面中央のレイをCPU高さ配列(HeightmapTextureManager)に対してレイマーチし、
    /// 地表面との交点を求める。地形コライダーが存在しない領域でも、起伏を考慮した
    /// 正しい地表点を回転中心として得るために使う。
    /// レイの高さが地形高さを初めて下回った区間を線形補間して交点とする。
    /// </summary>
    private bool RaycastTerrainHeightField(Ray ray, out Vector3 hitPoint)
    {
        hitPoint = Vector3.zero;

        // 下向き成分がほぼ無い(地平線より上を向いている)場合は地表と交わらない。
        // 無駄なマーチングを避けて早期リターンする。
        if (ray.direction.y >= -0.001f)
        {
            return false;
        }

        const float step = 5f; // サンプリング間隔(m)
        Vector3 prevPos = ray.origin;
        float prevDiff = ray.origin.y - Utils.GetGroundHeight(ray.origin);

        for (float traveled = step; traveled <= rotationRaycastDistance; traveled += step)
        {
            Vector3 pos = ray.origin + ray.direction * traveled;
            float diff = pos.y - Utils.GetGroundHeight(pos);

            if (diff <= 0f)
            {
                // prevPos(地表より上) と pos(地表より下) の間を線形補間して交点を求める。
                float denom = prevDiff - diff;
                float t = Mathf.Approximately(denom, 0f) ? 0f : prevDiff / denom;
                Vector3 point = Vector3.Lerp(prevPos, pos, t);
                point.y = Utils.GetGroundHeight(point);
                hitPoint = point;
                return true;
            }

            prevPos = pos;
            prevDiff = diff;
        }

        return false;
    }

    /// <summary>
    /// マウス位置から地面（Terrain）または y=0 平面との交点を取得する。
    /// 道路上や建物上にマウスがあっても、Terrain レイヤーへのレイキャストを試みて
    /// 地面座標を返す。Terrain に当たらない場合は y=0 平面との交点を返す。
    /// </summary>
    private Vector3? GetGroundPosition()
    {
        Ray ray = cam.ScreenPointToRay(InputUtil.MousePosition);

        int mask = terrainLayerMask.value;
        int groundMask = LayerMask.GetMask("Ground");
        if (groundMask != 0)
        {
            mask |= groundMask;
        }

        // Terrain レイヤーに対してレイキャストを行い、地面座標を取得する
        if (Physics.Raycast(ray, out RaycastHit hit, rotationRaycastDistance, mask))
        {
            return hit.point;
        }

        // Terrain に当たらない場合は y=0 平面との交点を計算し、その点の地形高さを取得する
        Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
        if (groundPlane.Raycast(ray, out float enter))
        {
            Vector3 point = ray.origin + ray.direction * enter;
            point.y = Utils.GetGroundHeight(point);
            return point;
        }

        return null;
    }

    /// <summary>
    /// マウス位置が土地区画・造成範囲の編集中の頂点または道路編集中のハンドル上にあるか判定する。
    /// 編集ハンドル上ではカメラドラッグを開始しないようにするために使用する。
    /// </summary>
    private bool IsMouseOverEditHandle()
    {
        Vector2 mouseScreen = InputUtil.MousePosition;

        // ParcelShapeEditManager の頂点判定
        if (parcelShapeEditManagerRef == null) parcelShapeEditManagerRef = FindAnyObjectByType<ParcelShapeEditManager>();
        var parcelShapeEditManager = parcelShapeEditManagerRef;
        if (parcelShapeEditManager != null && parcelShapeEditManager.IsEditMode)
        {
            if (parcelShapeEditManager.IsMouseOverVertex(mouseScreen, cam))
            {
                return true;
            }
        }

        // GradingAreaEditManager(造成範囲の作図)の頂点判定。IsMouseOverVertex は Editing フェーズの間だけ真を返す。
        if (gradingAreaEditManagerRef == null) gradingAreaEditManagerRef = FindAnyObjectByType<GradingAreaEditManager>();
        if (gradingAreaEditManagerRef != null && gradingAreaEditManagerRef.IsEditMode
            && gradingAreaEditManagerRef.IsMouseOverVertex(mouseScreen, cam))
        {
            return true;
        }

        // RoadPlanManager の編集ハンドル判定
        if (roadPlanManagerRef == null) roadPlanManagerRef = FindAnyObjectByType<RoadPlanManager>();
        var roadPlanManager = roadPlanManagerRef;
        if (roadPlanManager != null && roadPlanManager.IsEditMode)
        {
            if (roadPlanManager.IsMouseOverEditHandle(mouseScreen, cam))
            {
                return true;
            }
        }

        // TransitPlanManager の巡回点・区間の判定。停留所・経由点の移動と、区間のドラッグでの経由点の挿入を
        // カメラの移動ドラッグに奪わせない(IsMouseOverDraggable は編集フェーズの間だけ真を返す)。
        if (transitPlanManagerRef == null) transitPlanManagerRef = FindAnyObjectByType<TransitPlanManager>();
        if (transitPlanManagerRef != null && transitPlanManagerRef.IsMouseOverDraggable(mouseScreen))
        {
            return true;
        }

        // ConnectorEditManager の接続端子マーカー判定。
        // マーカー上でドラッグを開始した場合はカメラ移動を抑止し、端子ノードの移動ドラッグを
        // 成立させる(これを行わないと DefaultExecutionOrder が先の CameraController が
        // カメラドラッグを確定させ、InputHandler 側のドラッグ開始を奪ってしまう)。
        var connectorEditManager = ConnectorEditManager.Instance;
        if (connectorEditManager != null && connectorEditManager.IsEditMode)
        {
            if (connectorEditManager.IsMouseOverConnector(mouseScreen, cam))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// マウス左ボタンによるドラッグでカメラを移動する（Google マップ方式）。
    /// 地面を掴んでドラッグした方向と逆にカメラを動かす。
    /// オブジェクト上で開始した場合はカメラ移動を開始しない。
    ///
    /// 振動を防ぐため、ドラッグ開始時のカメラ状態（位置・回転）を固定して記録し、
    /// ドラッグ中の地面座標計算には常にその保存したカメラ状態を使う。
    /// これによりカメラ移動と地面座標計算の間にフィードバックループが生じない。
    /// </summary>
    private void HandleMouseDragPan(bool isPointerOverUI)
    {
        // マウスダウン時にドラッグ開始判定
        if (InputUtil.LeftMouseWasPressedThisFrame)
        {
            if (isPointerOverUI || isMouseRotating)
            {
                return;
            }

            // 編集中の頂点やハンドル上でドラッグを開始した場合はカメラ移動を開始しない。
            // InputHandler 側で頂点ドラッグが処理されるようにする。
            if (IsMouseOverEditHandle())
            {
                return;
            }

            Vector3? groundPos = GetGroundPosition();
            if (groundPos.HasValue)
            {
                // カメラと掴んだ座標の距離を判定し、遠すぎる場合はドラッグを開始しない
                float dist = Vector3.Distance(transform.position, groundPos.Value);
                if (dist <= maxDragDistance)
                {
                    isPendingDrag = true;
                    dragStartScreenPosition = InputUtil.MousePosition;
                    dragStartGroundPosition = groundPos.Value;
                    dragStartCameraPosition = transform.position;
                    dragStartCameraRotation = transform.rotation;
                    dragVelocity = Vector3.zero; // 慣性をリセット
                }
            }
        }

        // マウスアップ時にドラッグ終了
        if (InputUtil.LeftMouseWasReleasedThisFrame)
        {
            isMouseDragging = false;
            isPendingDrag = false;
            return;
        }

        // ドラッグ待機中：画面ピクセル閾値を超えたらドラッグ確定
        if (isPendingDrag && !isMouseDragging)
        {
            if ((InputUtil.MousePosition - dragStartScreenPosition).magnitude >= dragStartThreshold)
            {
                isMouseDragging = true;
                StopFocus();
            }
        }

        // ドラッグ確定中：カメラを移動
        if (isMouseDragging)
        {
            Vector3 prevPosition = transform.position;

            // ドラッグ開始時のカメラ状態を使って、現在のマウス位置に対応する地面座標を計算
            Vector3 currentGroundPos = GetGroundPositionWithSavedCamera(InputUtil.MousePosition);

            Vector3 delta = currentGroundPos - dragStartGroundPosition;

            // 基準カメラ位置からの差分の逆方向にカメラを配置
            // これにより「マウスが指した地面の点がマウスに追従する」挙動になる
            transform.position = new Vector3(
                dragStartCameraPosition.x - delta.x,
                dragStartCameraPosition.y,
                dragStartCameraPosition.z - delta.z
            );

            // フレーム間の移動量から慣性速度を計算
            dragVelocity = (transform.position - prevPosition) / Time.deltaTime;

            currentHeight = transform.position.y;

            // orthographicSize も更新
            if (isTopDownMode && cam != null)
            {
                cam.orthographicSize = currentHeight;
            }
        }
    }

    /// <summary>
    /// ドラッグ開始時に保存したカメラ状態を使って、指定スクリーン座標に対応する地面座標を取得する。
    /// カメラが移動した後でも、保存した状態に基づいて計算するため結果が不変となる。
    /// </summary>
    private Vector3 GetGroundPositionWithSavedCamera(Vector3 screenPos)
    {
        Ray ray = GetRayFromSavedCameraState(screenPos);

        // 地面平面（ドラッグ開始時の高さ）との交点
        Plane groundPlane = new Plane(Vector3.up, dragStartGroundPosition);
        if (groundPlane.Raycast(ray, out float enter))
        {
            return ray.GetPoint(enter);
        }

        return dragStartGroundPosition;
    }

    /// <summary>
    /// ドラッグ開始時に保存したカメラ位置・回転を使って、スクリーン座標からレイを構築する。
    /// Perspective投影の内部計算を手動で再現することで、現在のカメラ位置に依存しない。
    /// </summary>
    private Ray GetRayFromSavedCameraState(Vector3 screenPos)
    {
        float aspect = (float)Screen.width / Screen.height;
        float tanHalfFov = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);

        // スクリーン座標を正規化デバイス座標 (NDC) の XY に変換
        float ndcX = (screenPos.x / Screen.width * 2f - 1f) * aspect * tanHalfFov;
        float ndcY = (screenPos.y / Screen.height * 2f - 1f) * tanHalfFov;

        // カメラ空間（右手系、Z=+1 が前方）での方向ベクトル
        Vector3 localDir = new Vector3(ndcX, ndcY, 1f).normalized;

        // ドラッグ開始時の回転を使ってワールド空間に変換
        Vector3 worldDir = dragStartCameraRotation * localDir;

        return new Ray(dragStartCameraPosition, worldDir);
    }
}
