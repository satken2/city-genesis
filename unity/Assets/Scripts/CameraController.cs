using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;
using TMPro;
using UnityEngine.UI;
using UnityEngine.EventSystems;

[DefaultExecutionOrder(-100)]
public partial class CameraController : MonoBehaviour
{
    [Header("基準速度設定（バックエンド基準）")]
    public float baseMoveSpeed = 150.0f;    // Camera movement speed (backend units).
    public float rotationSpeed = 50f; // Rotation speed (independent of scale).
    public float baseHeightSpeed = 2000f;   // Vertical movement speed (backend units).
    public float baseFocusHeight = 100f;  // Minimum approach distance for FocusOnArea (backend units).
    
    [Header("高度による速度調整設定（バックエンド基準）")]
    public float baseReferenceHeight = 150f; // Reference altitude, where the speed multiplier is 1.
    public float minSpeedRatio = 0.3f; // Minimum speed multiplier (can slow to 30%).
    public float maxSpeedRatio = 5.0f; // Maximum speed multiplier (can accelerate to 5x).
    
    [Header("初期位置設定（バックエンド基準）")]
    public float baseInitialHeight = 150f; // Initial camera altitude (backend units).

    [Header("マウス入力設定")]
    public float edgePanThreshold = 20f;              // Screen-edge threshold in pixels for mouse-driven movement.
    public float mouseWheelSensitivity = 0.2f;        // Mouse-wheel altitude adjustment factor.
    public float mouseRotationSpeed = 0.2f;           // Rotation speed while right-dragging.
    public float minOrbitPitch = 5f;                  // Minimum camera elevation angle above the ground.
    public float maxOrbitPitch = 80f;                 // Maximum camera elevation angle.
    public LayerMask rotationGroundMask = Physics.DefaultRaycastLayers; // Layer mask for finding the rotation pivot.
    public float rotationRaycastDistance = 5000f;     // Raycast distance for finding the rotation pivot.
    
    [Header("Terrain Settings")]
    public LayerMask terrainLayerMask = 1; // For raycasting against the Terrain layer.

    [Header("UI設定")]
    [SerializeField] private TextMeshProUGUI CameraText;
    [SerializeField] private Button cameraModeButton;
    [SerializeField] private Sprite camera90Icon;   // Icon in top-down mode.
    [SerializeField] private Sprite camera45Icon;   // Icon in normal mode.
    [SerializeField] private Button flyModeButton;  // Fly mode toggle button.
    [SerializeField] private Sprite flyModeOnIcon;  // Icon while fly mode is active.
    [SerializeField] private Sprite flyModeOffIcon; // Icon in normal mode (fly mode off).
    [SerializeField] private Button trafficPauseButton;  // Pause button for traffic animation, including cars and pedestrians. Cars and people can be clicked only while paused.
    [SerializeField] private Sprite trafficPauseIcon;    // Icon while moving (click to pause).
    [SerializeField] private Sprite trafficResumeIcon;   // Icon while paused (click to resume).
    [SerializeField] private Button driveModeButton;     // Button to start or stop driving.

    [Header("フライモード設定")]
    // Mouse sensitivity in fly mode (degrees/pixel). Multiply mouse displacement by this value to change the viewing angle.
    public float flyMouseSensitivity = 0.1f;
    // Fly-mode movement speed multiplier, applied to the same altitude-dependent speed as normal mode (1.0 = unchanged).
    // Applies to both WASD (view-relative movement) and R/F (world-Y movement).
    public float flySpeedMultiplier = 1.0f;

    [Header("高度による移動速度調整（前後左右）")]
    public float moveHeightSpeedMultiplier = 0.5f; // Strength of forward/backward/sideways speed variation (smaller values have less effect; 0 disables it).
    
    // Runtime parameters with scale applied.
    private float scaledMoveSpeed;
    private float scaledHeightSpeed;
    private float scaledInitialHeight;
    private float scaledReferenceHeight;

    private float currentHeight; // Stored height.
    private bool isFocusing = false; // Whether a focus transition is active.
    private Vector3 focusStartPosition;
    private Quaternion focusStartRotation;
    private Vector3 focusTargetPosition; // Focus target position.
    private Quaternion focusTargetRotation;
    private float focusStartTime; // Focus transition start time.
    private float focusActiveDuration; // Duration of the active focus transition, in seconds.
    private const float AreaFocusSeconds = 1.5f;

    // Singleton instance.
    public static CameraController Instance { get; private set; }

    // Top-down mode state.
    private bool isTopDownMode = false;
    private Vector3 savedPosition;
    private Quaternion savedRotation;
    private bool savedOrthographic;
    private float savedFieldOfView;
    private Camera cam;

    // Fly mode state.
    // In fly mode, skip all normal-mode logic that holds the camera at a fixed altitude,
    // and run only view-relative WASD movement and mouse look (requires perspective projection).
    private bool isFlyMode = false;
    private float lookYaw;   // Horizontal viewing angle in fly/capture mode (degrees about the world Y axis).
    private float lookPitch; // Pitch angle in fly/capture mode, in degrees.
    private bool tauriUiHidden; // Whether a request to hide Tauri's UI has been sent for fly/capture mode or an active drive.

    // TerrainManager reference used to clamp the camera to the map (heightmap) bounds.
    // Retained because GetMapWidth / GetMapHeight are instance methods; resolved lazily on first use.
    private TerrainManager terrainManagerRef;
    // Whether lazy TerrainManager lookup has been attempted, avoiding per-frame searches in scenes without one.
    private bool terrainManagerLookupDone = false;

    // Reference for camera-only mode captions, resolved lazily on first use.
    private ModeTextDisplay modeTextDisplayRef;
    private bool modeTextDisplayLookupDone = false;

    // Editor manager used to decide when dragging starts. Retained to avoid scanning all objects on every press;
    // look it up again only if not yet created or destroyed (Unity null semantics), including presses immediately after editing starts.
    private ParcelShapeEditManager parcelShapeEditManagerRef;
    private RoadPlanManager roadPlanManagerRef;
    private TransitPlanManager transitPlanManagerRef;
    private GradingAreaEditManager gradingAreaEditManagerRef;

    // Expose whether fly mode is active to other systems.
    public bool IsFlyMode => isFlyMode;
    public bool IsTopDownMode => isTopDownMode;

    // Mouse controls.
    private bool isMouseRotating = false;
    private Vector3 rotationPivot;
    private float orbitYaw;
    private float orbitPitch;
    private float rotationDistance;
    private Vector3 previousMousePosition;

    // Pending right-drag rotation state.
    // Do not rotate immediately on right-button press; commit rotation after the cursor moves a set number of pixels.
    // This distinguishes a stationary right-click from a rotation drag,
    // preventing rotation startup from consuming right-click confirmation (FinalizePath) while laying roads.
    private bool isPendingRotate = false;
    private Vector3 rotateStartScreenPosition;

    // Mouse-drag camera panning, as in Google Maps.
    private bool isMouseDragging = false;
    private bool isPendingDrag = false;
    private Vector3 dragStartScreenPosition;
    private Vector3 dragStartCameraPosition;     // Camera position at drag start.
    private Quaternion dragStartCameraRotation;  // Camera rotation at drag start.
    private Vector3 dragStartGroundPosition;     // Ground coordinates at drag start.
    public float dragStartThreshold = 5f; // Screen-pixel threshold for committing a drag.
    public float maxDragDistance = 1500f; // Do not start panning if the grabbed point is farther away than this distance.

    // Mouse-drag inertia.
    private Vector3 dragVelocity; // Velocity at drag end, in world units per second.
    public float dragInertiaDecay = 3.0f; // Inertia damping factor (larger values stop sooner).

    public bool IsMouseDragging => isMouseDragging;
    public bool IsPendingDrag => isPendingDrag;

    /// <summary>
    /// Default camera position restored at game start.
    /// Use the scene-placed XZ position and the scaled initial altitude for Y.
    /// </summary>
    private Vector3 defaultPosition;

    /// <summary>
    /// Orientation when the session has no saved camera state; use the scene-placed rotation unchanged.
    /// </summary>
    private Quaternion defaultRotation;

    // --- Session camera state synchronization (backend City.camera) ---
    // The view is included in save data, so PATCH the backend once the camera stops moving.
    // Avoid sending every frame: detect movement or rotation from the latest pose,
    // then send once after a period without movement.
    private bool syncEnabled;            // True after applying the saved view; false on session:reset / game:end.
    private bool syncDirty;              // The camera has moved since the last send.
    private bool syncInFlight;           // A PATCH request is in progress.
    private float syncStillSince;        // Time of the last detected movement (unscaled).
    private Vector3 syncLastPosition;
    private Quaternion syncLastRotation;
    private Coroutine applySessionCameraCoroutine;
    private const float SYNC_MOVE_THRESHOLD_M = 1.0f;
    private const float SYNC_ANGLE_THRESHOLD_DEG = 0.5f;
    private const float SYNC_STILL_SECONDS = 1.0f;
    private const float SYNC_RETRY_SECONDS = 5.0f;

    /// <summary>Reads only the camera state from the GET /api/v1/city response.</summary>
    private class CityCameraResponse
    {
        public CityCameraProps city;
    }

    private class CityCameraProps
    {
        public CameraStatePayload camera;
    }

    /// <summary>
    /// Camera state shared with the backend (City.camera). Position is in world coordinates (m);
    /// yaw is clockwise degrees from north (+Z), pitch is downward viewing angle in degrees (positive = down). Roll is always 0.
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
        // Calculate scaled values.
        UpdateScaledValues();

        // Save the default position (scene-placed XZ, initial altitude for Y).
        defaultPosition = transform.position;
        defaultPosition.y = scaledInitialHeight;
        defaultRotation = transform.rotation;

        // Set the initial position to the default.
        ResetToDefaultPosition();

        // Subscribe to game-state changes and reset the camera position when the game starts.
        if (GameStateManager.Instance != null)
        {
            GameStateManager.Instance.OnStateChanged += HandleGameStateChanged;
        }
        
        if (CameraText != null)
        {
            CameraText.text = "";
        }

        // Register button events and update their initial appearance.
        if (cameraModeButton != null)
        {
            cameraModeButton.onClick.AddListener(ToggleTopDownMode);
        }
        if (flyModeButton != null)
        {
            flyModeButton.onClick.AddListener(ToggleFlyMode);
        }
        if (cinematicModeButton != null)
        {
            cinematicModeButton.onClick.AddListener(ToggleCinematicMode);
        }
        if (trafficPauseButton != null)
        {
            trafficPauseButton.onClick.AddListener(ToggleTrafficPause);
        }
        if (driveModeButton != null)
        {
            driveModeButton.onClick.AddListener(DriveMode.RequestToggle);
        }
        TrafficClock.PausedChanged += HandleTrafficPausedChanged;
        UpdateUIButtons();

        currentHeight = transform.position.y; // Record the initial height.
        Debug.Log($"CameraController: スケール適用済み設定 - 移動速度: {scaledMoveSpeed}, 高度速度: {scaledHeightSpeed}, 初期高度: {scaledInitialHeight}");
    }

    private void OnDestroy()
    {
        TrafficClock.PausedChanged -= HandleTrafficPausedChanged;
    }

    private void ToggleTrafficPause()
    {
        TrafficClock.Toggle();
        // Leaving a Button selected also treats Space/arrow keys as Submit/Navigation.
        EventSystem.current?.SetSelectedGameObject(null);
    }

    private void HandleTrafficPausedChanged(bool paused) => UpdateUIButtons();

    private void OnDisable()
    {
        StopCinematicMode();
        // Unsubscribe from events when the MonoBehaviour is disabled to prevent memory leaks.
        if (GameStateManager.Instance != null)
        {
            GameStateManager.Instance.OnStateChanged -= HandleGameStateChanged;
        }

        // Always restore the locked cursor when disabled during fly mode,
        // otherwise the cursor becomes unusable throughout the game.
        if (isFlyMode)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    /// <summary>
    /// Handler called when the game state changes.
    /// On game start (Game state), apply the session's saved view;
    /// on game end (Menu state), stop view synchronization.
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
    /// Reset the camera to its default position.
    /// Also update orthographicSize in top-down mode.
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
    /// Fetch the session's saved camera state (camera in GET /api/v1/city), apply it,
    /// and start synchronizing subsequent view changes to the backend. Called on game:start and on session
    /// switches during a game. Suspend synchronization while fetching so the previous session's view
    /// cannot overwrite the new city's saved value.
    /// </summary>
    public bool IsSessionCameraReady => syncEnabled && applySessionCameraCoroutine == null;

    public void ApplySessionCamera()
    {
        SuspendCameraSync();
        applySessionCameraCoroutine = StartCoroutine(ApplySessionCameraCoroutine());
    }

    /// <summary>
    /// Stop backend view synchronization on session:reset (preparing a new city) and game:end.
    /// Also cancel any saved-view fetch in progress to avoid applying values from the old session.
    /// </summary>
    public void SuspendCameraSync()
    {
        // Fly/capture mode hides Tauri's UI, so always exit before switching cities or returning to the menu.
        StopCinematicMode();
        SetFlyMode(false);
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
        // Retry fetch failures a few times, such as transient network errors immediately after loading;
        // then start with the default view so loading does not proceed indefinitely without a resolved view.
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
            // Cities without a saved view start with the default at the origin (scene-placed orientation and initial altitude).
            SetView(new Vector3(0f, scaledInitialHeight, 0f), defaultRotation);
        }
        defaultPosition = new Vector3(transform.position.x, scaledInitialHeight, transform.position.z);

        // Mark the applied pose as already sent when starting synchronization, avoiding an immediate redundant send.
        syncLastPosition = transform.position;
        syncLastRotation = transform.rotation;
        syncDirty = false;
        syncStillSince = Time.unscaledTime;
        syncEnabled = true;
        applySessionCameraCoroutine = null;
    }

    /// <summary>
    /// Detect view changes and send to the backend after movement stops. Use LateUpdate so early returns from Update
    /// in fly mode, while a radial menu is open, and similar cases do not interfere.
    /// </summary>
    private void LateUpdate()
    {
        if (DriveMode.IsOpen) return;
        // Do not save views from the observation-only window; the saved view belongs to the working window.
        if (!syncEnabled || ViewerSession.Enabled) return;
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

    /// <summary>Convert the current camera pose to the shared backend format (CameraStatePayload).</summary>
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
            // Space retries after failures to avoid rapid requests during transient session-switch errors.
            syncDirty = true;
            syncStillSince = Time.unscaledTime + SYNC_RETRY_SECONDS - SYNC_STILL_SECONDS;
        }
    }

    /// <summary>
    /// Set camera position and rotation immediately, synchronizing its internal height state as well.
    /// Entry point for framing generated objects from any view in one step,
    /// such as mesh test scenes requiring immediate placement instead of a focus animation.
    /// Without updating currentHeight, the end-of-Update logic that maintains world-space height
    /// would immediately restore the previous height, so always synchronize it here.
    /// </summary>
    public void SetView(Vector3 position, Quaternion rotation)
    {
        StopCinematicMode();
        if (cam == null)
        {
            cam = GetComponent<Camera>();
        }
        CancelCameraGestures();

        transform.position = position;
        transform.rotation = rotation;
        currentHeight = Mathf.Max(0f, position.y);

        // Fly mode stores viewing angles internally, so reinitialize them from the newly assigned orientation
        // to prevent the next mouse input from jumping back to the old orientation.
        if (isFlyMode) ResetLookAngles();

        if (isTopDownMode && cam != null)
        {
            cam.orthographicSize = currentHeight;
        }
    }

    void Update()
    {
        if (cinematicModeButton != null)
            cinematicModeButton.interactable = IsCinematicMode || CanStartCinematicMode();
        if (IsCinematicMode)
        {
            if (!CanStartCinematicMode()) StopCinematicMode();
            else if (Application.isFocused) HandleCinematicMovement(Time.unscaledDeltaTime);
            return;
        }
        if (GameStateManager.Instance != null && !GameStateManager.Instance.IsWorldVisible) return;
        if (DriveMode.BlocksCamera || CitizenFollow.BlocksCamera) return;
        // In scenes with GameStateManager (the main game), reject all camera controls before game start
        // or while loading locks the camera.
        // Scenes without GameStateManager, such as standalone mesh tests,
        // have no such restrictions and always allow control.
        if (GameStateManager.Instance != null)
        {
            if (!GameStateManager.Instance.IsGameStarted())
            {
                return;
            }


        }

        // Enter fly mode with Shift+F and exit with Esc.
        // Return immediately on the toggle frame without processing other camera inputs, such as F lowering altitude,
        // to prevent normal-mode controls from leaking into the transition.
        bool shiftHeld = InputUtil.KeyPressed(Key.LeftShift) || InputUtil.KeyPressed(Key.RightShift);
        if (isFlyMode ? InputUtil.KeyWasPressedThisFrame(Key.Escape) : shiftHeld && InputUtil.KeyWasPressedThisFrame(Key.F))
        {
            ToggleFlyMode();
            ClampPositionToCityBoundary();
            return;
        }

        // Fly mode runs only its dedicated controls, without normal dragging, rotation, or altitude maintenance.
        if (isFlyMode)
        {
            HandleFlyModeMovement();
            ClampPositionToCityBoundary();
            return;
        }

        // While the radial menu is open, reject camera rotation, panning, movement, and zoom.
        // This prevents menu interactions, such as moving the mouse to highlight an item, from moving the camera.
        // The menu prevents opening while a mouse button is held for dragging or rotation,
        // so this early return cannot leave rotation or panning cleanup pending.
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

        // Prioritize an active focus transition.
        // Toggle top-down mode with U.
        if (InputUtil.KeyWasPressedThisFrame(Key.U))
        {
            ToggleTopDownMode();
        }

        bool isPointerOverUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        if (!isPointerOverUI || isMouseRotating)
        {
            HandleMouseRotationInput();
        }

        // Handle mouse-drag camera panning.
        HandleMouseDragPan(isPointerOverUI);

        // Forward/backward/sideways movement.
        float moveX = GetAxisRawHorizontal();
        float moveZ = GetAxisRawVertical();

        float combinedX = moveX;
        float combinedZ = moveZ;

        // Adjust movement speed based on the current altitude.
        float currentMoveSpeed = GetHeightAdjustedMoveSpeed();
        
        Vector3 move;
        if (isTopDownMode)
        {
            // In top-down mode:
            // Z input (up/down) maps to screen up/down (local Y).
            // X input (left/right) maps to screen left/right (local X).
            move = new Vector3(combinedX, combinedZ, 0) * currentMoveSpeed * Time.deltaTime;
        }
        else
        {
            // In normal mode:
            // Z input (up/down) maps to the camera's forward direction (local Z).
            // X input (left/right) maps to the camera's right direction (local X).
            move = new Vector3(combinedX, 0, combinedZ) * currentMoveSpeed * Time.deltaTime;
        }

        transform.Translate(move, Space.Self);

        // Apply drag inertia only when there is no keyboard input.
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

                // Dampen velocity.
                dragVelocity *= Mathf.Max(0f, 1f - dragInertiaDecay * Time.deltaTime);

                currentHeight = transform.position.y;
                if (isTopDownMode && cam != null)
                {
                    cam.orthographicSize = currentHeight;
                }
            }
        }

        // Change height in world coordinates.
        if (InputUtil.KeyPressed(Key.R)) currentHeight += GetHeightAdjustedHeightSpeed() * Time.deltaTime;
        if (InputUtil.KeyPressed(Key.F)) currentHeight -= GetHeightAdjustedHeightSpeed() * Time.deltaTime;

        float scroll = InputUtil.ScrollY;
        if (!isPointerOverUI && Mathf.Abs(scroll) > Mathf.Epsilon)
        {
            if (isTopDownMode)
            {
                // In top-down mode, zoom around the mouse cursor.
                // Change orthographicSize and translate the camera
                // so the world point under the cursor remains at the same screen position.

                // Get the world point under the mouse before the change.
                Vector3 mouseWorldBefore = cam.ScreenToWorldPoint(
                    new Vector3(InputUtil.MousePosition.x, InputUtil.MousePosition.y, cam.nearClipPlane + 10f)
                );

                float zoomFactor = 1f - scroll * mouseWheelSensitivity;
                float newSize = Mathf.Max(cam.orthographicSize * zoomFactor, 1f);

                cam.orthographicSize = newSize;
                currentHeight = newSize;

                // Get the world point under the mouse after the change.
                Vector3 mouseWorldAfter = cam.ScreenToWorldPoint(
                    new Vector3(InputUtil.MousePosition.x, InputUtil.MousePosition.y, cam.nearClipPlane + 10f)
                );

                // Apply the difference as a camera offset, preserving height.
                Vector3 offset = mouseWorldBefore - mouseWorldAfter;
                offset.y = 0f;
                transform.position += offset;
                currentHeight = transform.position.y;
                cam.orthographicSize = currentHeight;
            }
            else
            {
                // In normal mode, zoom around the mouse cursor.
                // Cast a ray from the mouse to the ground or y=0 plane and obtain the intersection.
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
                    // Calculate the distance between the camera and the target point.
                    float distance = Vector3.Distance(transform.position, zoomTarget);

                    // Adjust the distance according to mouse-wheel input.
                    // scroll > 0 reduces distance (zoom in); scroll < 0 increases it (zoom out).
                    float zoomFactor = 1f - scroll * mouseWheelSensitivity;
                    float newDistance = distance * zoomFactor;
                    newDistance = Mathf.Max(newDistance, 0.1f); // Prevent approaching too closely.

                    // Move the camera around the target point according to the distance ratio.
                    Vector3 direction = (transform.position - zoomTarget).normalized;
                    Vector3 newPosition = zoomTarget + direction * newDistance;

                    // Prevent passing through the ground.
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
                    // If no target point is available, such as when looking at the sky, move along the viewing direction as usual.
                    float groundHeight = Utils.GetGroundHeight(transform.position);
                    float heightAboveGround = Mathf.Max(0f, transform.position.y - groundHeight);
                    float speedRatio = heightAboveGround / scaledReferenceHeight;
                    speedRatio = Mathf.Clamp(speedRatio, minSpeedRatio, maxSpeedRatio);

                    float wheelMoveSpeed = scaledHeightSpeed * mouseWheelSensitivity * speedRatio;
                    Vector3 moveDelta = transform.forward * scroll * wheelMoveSpeed;

                    // Prevent passing through the ground after movement.
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

        // As a safeguard, clamp height so it cannot fall below the ground.
        currentHeight = Mathf.Max(0f, currentHeight);

        // Maintain the camera's world-space height.
        transform.position = new Vector3(transform.position.x, currentHeight, transform.position.z);
        if (isTopDownMode && cam != null)
        {
            cam.orthographicSize = currentHeight;
        }

        // Rotate around the world Y axis with Q/E.
        // Rotate around the world Y axis with Q/E.
        // Allow rotation in top-down mode as well.
        float rotation = 0;
        if (InputUtil.KeyPressed(Key.Q)) rotation = -1;
        if (InputUtil.KeyPressed(Key.E)) rotation = 1;
        if (rotation != 0)
        {
            transform.Rotate(Vector3.up, rotation * rotationSpeed * Time.deltaTime, Space.World);
        }

        // Interrupt the focus transition on any key input.
        if (InputUtil.AnyKeyDown)
        {
            StopFocus();
        }

        if (InputUtil.LeftMouseWasPressedThisFrame || InputUtil.RightMouseWasPressedThisFrame || Mathf.Abs(scroll) > Mathf.Epsilon)
        {
            StopFocus();
        }

        // Keep the camera within the map (heightmap) bounds, including in fly mode.
        ClampPositionToCityBoundary();
    }



    public void SetTopDownMode(bool enable)
    {
        if (cam == null) return;
        if (isTopDownMode == enable) return;
        StopCinematicMode();

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
    /// Toggle fly mode.
    /// Fly mode requires perspective projection, so first exit top-down (orthographic) mode if active.
    /// Initialize viewing angles from the current camera orientation when enabling it.
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
            StopCinematicMode();
            // Fly mode requires perspective projection; exit top-down (orthographic) mode if active.
            if (isTopDownMode)
            {
                SetTopDownMode(false);
            }

            // Cancel active mouse operations and focus animations so they do not conflict with fly-mode input.
            CancelCameraGestures();

            // Initialize yaw and pitch from the current camera orientation to prevent a view jump on switching.
            ResetLookAngles();

            isFlyMode = true;
        }
        else
        {
            // Record and synchronize the current height because normal-mode altitude maintenance takes effect immediately.
            currentHeight = transform.position.y;
            cameraModeExitFrame = Time.frameCount;

            isFlyMode = false;
        }

        // InputHandler itself blocks world input such as clicks by checking BlocksWorldInput.
        RefreshCameraModePresentation();
        UpdateUIButtons();
    }

    /// <summary>
    /// Update camera-only presentation for fly mode, capture mode, or an active drive.
    /// Hide Tauri's UI to show only the 3D view, notifying the UI only on changes, and display controls in the caption.
    /// Fly/capture modes lock and hide the cursor for continuous mouse look without screen-edge limits
    /// (third-person driving uses right-drag at the cursor position, so it does not lock the cursor).
    /// Do not switch captions in scenes without ModeTextDisplay, such as mesh tests.
    /// </summary>
    public void RefreshCameraModePresentation()
    {
        bool exclusive = isFlyMode || IsCinematicMode;
        Cursor.lockState = exclusive ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !exclusive;
        bool hideUi = exclusive || DriveMode.IsRiding;
        if (tauriUiHidden != hideUi)
        {
            tauriUiHidden = hideUi;
            IpcClient.Instance?.EmitUiVisibility(!hideUi);
        }

        if (modeTextDisplayRef == null)
        {
            if (modeTextDisplayLookupDone) return;
            modeTextDisplayLookupDone = true;
            modeTextDisplayRef = FindAnyObjectByType<ModeTextDisplay>();
            if (modeTextDisplayRef == null) return;
        }

        if (IsCinematicMode)
        {
            modeTextDisplayRef.ShowCinematicMode(cinematicMode == CinematicMode.Orbit,
                cinematicPaused, cinematicHasPivot, cinematicDirection < 0f);
        }
        else if (isFlyMode)
        {
            modeTextDisplayRef.ShowFlyMode();
        }
        else if (DriveMode.IsRiding)
        {
            modeTextDisplayRef.ShowDriveMode(DriveMode.RideCaption);
        }
        else
        {
            modeTextDisplayRef.RestoreState();
        }
    }

    public void ToggleFlyMode()
    {
        if (DriveMode.IsOpen) return;
        SetFlyMode(!isFlyMode);
    }

    /// <summary>
    /// Per-frame fly-mode processing.
    /// Use mouse displacement to change yaw/pitch and WASD to move relative to the current viewing direction.
    /// Looking up/down while moving forward also ascends/descends (view-relative free flight). R/F additionally moves along world Y.
    /// </summary>
    private void HandleFlyModeMovement()
    {
        ApplyMouseLook();

        // Move with WASD relative to the view. The forward vector includes pitch, so movement follows the viewing direction directly.
        float moveX = GetAxisRawHorizontal(); // A/D (strafe left/right).
        float moveZ = GetAxisRawVertical();   // W/S (forward/backward).
        Vector3 moveDir = transform.forward * moveZ + transform.right * moveX;

        float moveSpeed = GetHeightAdjustedMoveSpeed() * flySpeedMultiplier;
        transform.position += moveDir * moveSpeed * Time.deltaTime;

        // R/F moves vertically along the world Y axis.
        float vertical = 0f;
        if (InputUtil.KeyPressed(Key.R)) vertical += 1f;
        if (InputUtil.KeyPressed(Key.F)) vertical -= 1f;
        if (vertical != 0f)
        {
            transform.position += Vector3.up * (vertical * GetHeightAdjustedHeightSpeed() * flySpeedMultiplier * Time.deltaTime);
        }

        // Prevent passing through the ground; do not descend below the surface.
        float groundHeight = Utils.GetGroundHeight(transform.position);
        if (transform.position.y < groundHeight)
        {
            transform.position = new Vector3(transform.position.x, groundHeight, transform.position.z);
        }

        // Synchronize the current height for altitude-dependent speed calculations.
        currentHeight = transform.position.y;
    }

    /// <summary>
    /// Update viewing angles from mouse displacement and apply them to the camera, shared by fly/capture modes.
    /// A Locked cursor provides continuous deltas regardless of screen edges.
    /// Mouse input cannot tilt pitch beyond +/-89 degrees, preventing flips past straight up/down
    /// (capture starting straight down retains that angle).
    /// </summary>
    private void ApplyMouseLook()
    {
        Vector2 mouseDelta = Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
        lookYaw += mouseDelta.x * flyMouseSensitivity;
        lookPitch = Mathf.Clamp(lookPitch - mouseDelta.y * flyMouseSensitivity,
            Mathf.Min(-89f, lookPitch), Mathf.Max(89f, lookPitch));
        transform.rotation = Quaternion.Euler(lookPitch, lookYaw, 0f);
    }

    private void ResetLookAngles()
    {
        Vector3 euler = transform.rotation.eulerAngles;
        lookYaw = euler.y;
        lookPitch = NormalizePitchAngle(euler.x);
    }

    /// <summary>
    /// Normalize Quaternion.eulerAngles pitch from [0,360) to the more convenient [-180,180) range.
    /// </summary>
    private float NormalizePitchAngle(float pitch)
    {
        if (pitch > 180f) pitch -= 360f;
        return pitch;
    }

    /// <summary>
    /// Clamp the camera to the combined detailed and background terrain bounds.
    /// The map uses world coordinates centered at the origin, as in CameraCellChecker / UnityCellToBackendCell,
    /// so valid bounds are X [-mapWidth/2, +mapWidth/2] and Z [-mapHeight/2, +mapHeight/2].
    /// Do not clamp in scenes without TerrainManager, such as mesh tests.
    /// </summary>
    private void ClampPositionToCityBoundary()
    {
        if (terrainManagerRef == null)
        {
            // This point is reached after game start and camera unlock, when terrain settings are loaded,
            // so one lookup attempt is sufficient. If absent, leave clamping disabled for that scene.
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
        // Update the Mode Button.
        if (cameraModeButton != null && cameraModeButton.image != null)
        {
            if (isTopDownMode)
            {
                // Top-down mode: camera90.
                cameraModeButton.image.sprite = camera90Icon;
            }
            else
            {
                // Normal mode: camera45.
                cameraModeButton.image.sprite = camera45Icon;
            }
            cameraModeButton.image.enabled = true; // Always visible.
            var c = cameraModeButton.image.color;
            c.a = 1f;
            cameraModeButton.image.color = c;
        }

        // Update the Fly Mode Button.
        // Switch sprites by state only when both ON/OFF icons are assigned.
        if (flyModeButton != null && flyModeButton.image != null)
        {
            if (flyModeOnIcon != null && flyModeOffIcon != null)
            {
                flyModeButton.image.sprite = isFlyMode ? flyModeOnIcon : flyModeOffIcon;
            }
            flyModeButton.image.enabled = true; // Always visible.
            var fc = flyModeButton.image.color;
            fc.a = 1f;
            flyModeButton.image.color = fc;
        }

        // Switch pause-button icons only when both icons are assigned as well.
        if (trafficPauseButton != null && trafficPauseButton.image != null
            && trafficPauseIcon != null && trafficResumeIcon != null)
        {
            trafficPauseButton.image.sprite = TrafficClock.Paused ? trafficResumeIcon : trafficPauseIcon;
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
        // Calculate the ratio of current altitude to reference altitude.
        float heightRatio = currentHeight / scaledReferenceHeight;
        
        // Calculate the altitude-dependent speed factor (exponential adjustment).
        float speedMultiplier;
        if (moveHeightSpeedMultiplier <= 0.0f)
        {
            // Use a multiplier of 1 when adjustment is disabled.
            speedMultiplier = 1.0f;
        }
        else
        {
            // Weaker exponential adjustment: ratio^(multiplier).
            // With multiplier=0.5, doubling altitude gives 1.414x speed and halving it gives 0.707x, producing gentler changes.
            float exponent = moveHeightSpeedMultiplier;
            speedMultiplier = Mathf.Pow(heightRatio, exponent);
        }
        
        // Clamp to the minimum and maximum speed multipliers.
        speedMultiplier = Mathf.Clamp(speedMultiplier, minSpeedRatio, maxSpeedRatio);
        
        float adjustedSpeed = scaledMoveSpeed * speedMultiplier;
        
        return adjustedSpeed;
    }

    private float GetHeightAdjustedHeightSpeed()
    {
        // Set speed to zero at altitude 0 and increase it with altitude.
        // Calculate the ratio of current altitude to reference altitude.
        float heightRatio = currentHeight / scaledReferenceHeight;
        
        // Adjust speed by altitude, reaching maximum speed at the reference altitude.
        // Use heightRatio directly from 0 to 1; use 1 (maximum speed) above that.
        float heightSpeedAdjustment = Mathf.Min(heightRatio, 1.0f);
        
        float adjustedSpeed = scaledHeightSpeed * heightSpeedAdjustment;
        
        return adjustedSpeed;
    }

    /// <summary>
    /// Smoothly move to a view of the target from the specified distance, bearing, and downward angle.
    /// Bearing is clockwise from north (+Z); downward angle is measured from horizontal in degrees, matching saved views (camera:state).
    /// Keyboard, click, or wheel input interrupts the transition immediately, giving player controls priority.
    /// Exit fly and capture modes because they control the view independently. In top-down mode, keep looking straight down
    /// and move directly above the target at the same height.
    /// </summary>
    public void FocusOn(Vector3 pivot, float distance, float yawDegrees, float pitchDegrees, float duration)
    {
        StopCinematicMode();
        if (isFlyMode) SetFlyMode(false);
        if (cam == null) cam = GetComponent<Camera>();
        CancelCameraGestures();

        Quaternion rotation = Quaternion.Euler(pitchDegrees, yawDegrees, 0f);
        Vector3 position = pivot - rotation * Vector3.forward * distance;
        if (isTopDownMode)
        {
            rotation = Quaternion.Euler(90f, yawDegrees, 0f);
            position = new Vector3(pivot.x, pivot.y + distance * Mathf.Sin(pitchDegrees * Mathf.Deg2Rad), pivot.z);
        }
        position.y = Mathf.Max(position.y, Utils.GetGroundHeight(position) + 1f);

        focusStartPosition = transform.position;
        focusStartRotation = transform.rotation;
        focusTargetPosition = position;
        focusTargetRotation = rotation;
        focusActiveDuration = Mathf.Max(duration, 0.01f);
        focusStartTime = Time.time;
        isFocusing = true;
    }

    /// <summary>
    /// Smoothly move, retaining the current bearing, to a distance that fits the world-space XZ bounding rectangle on screen.
    /// Keep pitch within normal-mode limits. Do nothing while citizen tracking or driving controls the camera.
    /// </summary>
    public void FocusOnArea(float minX, float minZ, float maxX, float maxZ)
    {
        if (DriveMode.IsRiding || CitizenFollow.IsFollowing) return;
        if (cam == null) cam = GetComponent<Camera>();

        Vector3 pivot = new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
        pivot.y = Utils.GetGroundHeight(pivot);
        float radius = new Vector2(maxX - minX, maxZ - minZ).magnitude * 0.5f;
        float fov = Mathf.Min(cam.fieldOfView, Camera.VerticalToHorizontalFieldOfView(cam.fieldOfView, cam.aspect));
        float distance = Mathf.Max(baseFocusHeight, radius / Mathf.Sin(fov * 0.5f * Mathf.Deg2Rad));

        Vector3 euler = transform.rotation.eulerAngles;
        float pitch = Mathf.Clamp(NormalizePitchAngle(euler.x), minOrbitPitch, maxOrbitPitch);
        FocusOn(pivot, distance, euler.y, pitch, AreaFocusSeconds);
    }

    private void UpdateFocusMovement()
    {
        float t = Mathf.Clamp01((Time.time - focusStartTime) / focusActiveDuration);
        float eased = t * t * (3f - 2f * t);
        transform.position = Vector3.Lerp(focusStartPosition, focusTargetPosition, eased);
        transform.rotation = Quaternion.Slerp(focusStartRotation, focusTargetRotation, eased);
        if (isTopDownMode && cam != null)
        {
            cam.orthographicSize = transform.position.y;
        }
        if (t >= 1f)
        {
            StopFocus();
        }
    }

    private void StopFocus()
    {
        if (isFocusing)
        {
            isFocusing = false;
            currentHeight = transform.position.y; // Record the current height.
            Debug.Log("カメラフォーカス終了");
        }
    }

    private void HandleMouseRotationInput()
    {
        if (cam == null || isTopDownMode) return;

        // On right-button press, enter a pending state without starting rotation immediately.
        if (InputUtil.RightMouseWasPressedThisFrame)
        {
            isPendingRotate = true;
            rotateStartScreenPosition = InputUtil.MousePosition;
        }

        // On right-button release, end active rotation and clear the pending state.
        // Releasing below the threshold while pending is a right-click; rotation never starts.
        if (InputUtil.RightMouseWasReleasedThisFrame)
        {
            if (isMouseRotating)
            {
                EndMouseRotation();
            }
            isPendingRotate = false;
            return;
        }

        // While pending, commit and start rotation after movement exceeds the threshold, as with left-drag panning.
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
        InitializeOrbit(GetRotationPivotPoint(), true);
        isMouseRotating = true;
    }

    private void InitializeOrbit(Vector3 pivot, bool clampPitch)
    {
        rotationPivot = pivot;
        rotationDistance = Vector3.Distance(transform.position, rotationPivot);
        rotationDistance = Mathf.Max(rotationDistance, 0.01f);

        Vector3 toCamera = transform.position - rotationPivot;
        Vector3 toCameraNormalized = toCamera.normalized;
        orbitYaw = toCamera.x * toCamera.x + toCamera.z * toCamera.z < 0.0001f
            ? transform.eulerAngles.y - 180f
            : Mathf.Atan2(toCamera.x, toCamera.z) * Mathf.Rad2Deg;
        orbitPitch = Mathf.Asin(toCameraNormalized.y) * Mathf.Rad2Deg;
        float clampedPitch = clampPitch ? Mathf.Clamp(orbitPitch, minOrbitPitch, maxOrbitPitch) : orbitPitch;
        if (!Mathf.Approximately(clampedPitch, orbitPitch))
        {
            orbitPitch = clampedPitch;
            ApplyOrbitPosition();
        }
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
        // Preserve yaw even when capturing straight down. LookAt cannot determine bearing when view and up vectors are parallel.
        transform.rotation = Quaternion.Euler(orbitPitch, orbitYaw + 180f, 0f);
        currentHeight = transform.position.y;

        if (isTopDownMode && cam != null)
        {
            cam.orthographicSize = currentHeight;
        }
    }

    private Vector3 GetRotationPivotPoint()
    {
        return TryGetRotationPivotPoint(out Vector3 pivot, false)
            ? pivot : transform.position + transform.forward * 10f;
    }

    private bool TryGetRotationPivotPoint(out Vector3 pivot, bool includeBuildingsAndSea)
    {
        Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        int groundMask = LayerMask.GetMask("Ground");
        bool hasGround = false;
        bool found = false;
        float closest = float.PositiveInfinity;
        pivot = default;

        // Normal right-drag targets only the ground; capture mode also targets buildings and the sea surface.
        // Parcel and road selection colliders in front must not take over the rotation pivot.
        int mask = includeBuildingsAndSea ? rotationGroundMask.value | groundMask : groundMask;
        foreach (RaycastHit hit in Physics.RaycastAll(ray, rotationRaycastDistance, mask, QueryTriggerInteraction.Ignore))
        {
            bool isGround = (groundMask & (1 << hit.collider.gameObject.layer)) != 0;
            hasGround |= isGround;
            if (!isGround && !(includeBuildingsAndSea &&
                (hit.collider.CompareTag("BuildingObject") || hit.collider.CompareTag("SeaObject")))) continue;
            if (hit.distance >= closest) continue;
            closest = hit.distance;
            pivot = hit.point;
            found = true;
        }

        // Include intersections with the existing height array even for distant terrain without colliders.
        if (!hasGround && RaycastTerrainHeightField(ray, out Vector3 marchPoint)
            && Vector3.Distance(ray.origin, marchPoint) < closest)
        {
            pivot = marchPoint;
            found = true;
        }
        return found;
    }

    /// <summary>
    /// Ray-march the screen-center ray against the CPU height array (HeightmapTextureManager)
    /// to find the ground intersection. This provides a terrain-aware ground point as the rotation pivot
    /// even where terrain colliders are absent.
    /// Linearly interpolate the first interval where ray height drops below terrain height to find the intersection.
    /// </summary>
    private bool RaycastTerrainHeightField(Ray ray, out Vector3 hitPoint)
    {
        hitPoint = Vector3.zero;

        // A ray with almost no downward component, looking above the horizon, does not intersect the ground.
        // Return early to avoid unnecessary marching.
        if (ray.direction.y >= -0.001f)
        {
            return false;
        }

        const float step = 5f; // Sampling interval (m).
        Vector3 prevPos = ray.origin;
        float prevDiff = ray.origin.y - Utils.GetGroundHeight(ray.origin);
        // Fixed-altitude capture may enter terrain. Do not correct a downward ray starting below the surface
        // to the ground overhead: that point is not at screen center and would make the view jump.
        if (prevDiff < 0f) return false;

        for (float traveled = step; traveled <= rotationRaycastDistance; traveled += step)
        {
            Vector3 pos = ray.origin + ray.direction * traveled;
            float diff = pos.y - Utils.GetGroundHeight(pos);

            if (diff <= 0f)
            {
                // Linearly interpolate between prevPos (above ground) and pos (below ground) to find the intersection.
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
    /// Get the mouse ray's intersection with the ground (Terrain) or the y=0 plane.
    /// Even over roads or buildings, try raycasting against the Terrain layer to return ground coordinates.
    /// If Terrain is not hit, return the intersection with the y=0 plane.
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

        // Raycast against the Terrain layer to obtain ground coordinates.
        if (Physics.Raycast(ray, out RaycastHit hit, rotationRaycastDistance, mask))
        {
            return hit.point;
        }

        // If Terrain is not hit, intersect the y=0 plane and obtain terrain height at that point.
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
    /// Check whether the mouse is over an edited parcel/earthworks vertex or a road editing handle.
    /// Used to prevent camera dragging from starting on editing handles.
    /// </summary>
    private bool IsMouseOverEditHandle()
    {
        Vector2 mouseScreen = InputUtil.MousePosition;

        // ParcelShapeEditManager vertex hit test.
        if (parcelShapeEditManagerRef == null) parcelShapeEditManagerRef = FindAnyObjectByType<ParcelShapeEditManager>();
        var parcelShapeEditManager = parcelShapeEditManagerRef;
        if (parcelShapeEditManager != null && parcelShapeEditManager.IsEditMode)
        {
            if (parcelShapeEditManager.IsMouseOverVertex(mouseScreen, cam))
            {
                return true;
            }
        }

        // GradingAreaEditManager vertex hit test for drawing earthworks areas. IsMouseOverVertex is true only during the Editing phase.
        if (gradingAreaEditManagerRef == null) gradingAreaEditManagerRef = FindAnyObjectByType<GradingAreaEditManager>();
        if (gradingAreaEditManagerRef != null && gradingAreaEditManagerRef.IsEditMode
            && gradingAreaEditManagerRef.IsMouseOverVertex(mouseScreen, cam))
        {
            return true;
        }

        // RoadPlanManager editing-handle hit test.
        if (roadPlanManagerRef == null) roadPlanManagerRef = FindAnyObjectByType<RoadPlanManager>();
        var roadPlanManager = roadPlanManagerRef;
        if (roadPlanManager != null && roadPlanManager.IsEditMode)
        {
            if (roadPlanManager.IsMouseOverEditHandle(mouseScreen, cam))
            {
                return true;
            }
        }

        // TransitPlanManager circuit-point and section hit tests. Keep stop/waypoint movement and dragging a section to insert
        // a waypoint from being captured by camera panning (IsMouseOverDraggable is true only during editing).
        if (transitPlanManagerRef == null) transitPlanManagerRef = FindAnyObjectByType<TransitPlanManager>();
        if (transitPlanManagerRef != null && transitPlanManagerRef.IsMouseOverDraggable(mouseScreen))
        {
            return true;
        }

        // ConnectorEditManager terminal-marker hit test.
        // Suppress camera movement for drags starting on markers so terminal nodes can be dragged
        // (otherwise CameraController, with its earlier DefaultExecutionOrder,
        // would commit a camera drag and consume InputHandler's drag start).
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
    /// Pan the camera by left-dragging, as in Google Maps.
    /// Grab the ground and move the camera opposite to the drag direction.
    /// Do not start camera movement if the drag begins over an object.
    ///
    /// To prevent jitter, snapshot the camera position and rotation at drag start
    /// and always use that snapshot to calculate ground coordinates during the drag.
    /// This avoids feedback between camera movement and ground-coordinate calculation.
    /// </summary>
    private void HandleMouseDragPan(bool isPointerOverUI)
    {
        // Check drag startup on mouse down.
        if (InputUtil.LeftMouseWasPressedThisFrame)
        {
            if (isPointerOverUI || isMouseRotating)
            {
                return;
            }

            // Do not start camera movement when dragging an edited vertex or handle.
            // Allow InputHandler to process the vertex drag.
            if (IsMouseOverEditHandle())
            {
                return;
            }

            Vector3? groundPos = GetGroundPosition();
            if (groundPos.HasValue)
            {
                // Check the distance from the camera to the grabbed point; do not start dragging if too far away.
                float dist = Vector3.Distance(transform.position, groundPos.Value);
                if (dist <= maxDragDistance)
                {
                    isPendingDrag = true;
                    dragStartScreenPosition = InputUtil.MousePosition;
                    dragStartGroundPosition = groundPos.Value;
                    dragStartCameraPosition = transform.position;
                    dragStartCameraRotation = transform.rotation;
                    dragVelocity = Vector3.zero; // Reset inertia.
                }
            }
        }

        // End dragging on mouse up.
        if (InputUtil.LeftMouseWasReleasedThisFrame)
        {
            isMouseDragging = false;
            isPendingDrag = false;
            return;
        }

        // Pending drag: commit after exceeding the screen-pixel threshold.
        if (isPendingDrag && !isMouseDragging)
        {
            if ((InputUtil.MousePosition - dragStartScreenPosition).magnitude >= dragStartThreshold)
            {
                isMouseDragging = true;
                StopFocus();
            }
        }

        // Committed drag: move the camera.
        if (isMouseDragging)
        {
            Vector3 prevPosition = transform.position;

            // Use the drag-start camera snapshot to calculate ground coordinates under the current mouse position.
            Vector3 currentGroundPos = GetGroundPositionWithSavedCamera(InputUtil.MousePosition);

            Vector3 delta = currentGroundPos - dragStartGroundPosition;

            // Place the camera opposite the displacement from its reference position.
            // This makes the ground point under the cursor follow the mouse.
            transform.position = new Vector3(
                dragStartCameraPosition.x - delta.x,
                dragStartCameraPosition.y,
                dragStartCameraPosition.z - delta.z
            );

            // Calculate inertial velocity from frame-to-frame displacement.
            dragVelocity = (transform.position - prevPosition) / Time.deltaTime;

            currentHeight = transform.position.y;

            // Update orthographicSize as well.
            if (isTopDownMode && cam != null)
            {
                cam.orthographicSize = currentHeight;
            }
        }
    }

    /// <summary>
    /// Get ground coordinates for the specified screen coordinates using the camera snapshot from drag start.
    /// Results remain independent of subsequent camera movement because they use the saved state.
    /// </summary>
    private Vector3 GetGroundPositionWithSavedCamera(Vector3 screenPos)
    {
        Ray ray = GetRayFromSavedCameraState(screenPos);

        // Intersection with the ground plane at the drag-start height.
        Plane groundPlane = new Plane(Vector3.up, dragStartGroundPosition);
        if (groundPlane.Raycast(ray, out float enter))
        {
            return ray.GetPoint(enter);
        }

        return dragStartGroundPosition;
    }

    /// <summary>
    /// Build a ray from screen coordinates using the camera position and rotation saved at drag start.
    /// Reproduce the perspective projection calculation directly to avoid dependence on the current camera position.
    /// </summary>
    private Ray GetRayFromSavedCameraState(Vector3 screenPos)
    {
        float aspect = (float)Screen.width / Screen.height;
        float tanHalfFov = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);

        // Convert screen coordinates to normalized device coordinates (NDC) XY.
        float ndcX = (screenPos.x / Screen.width * 2f - 1f) * aspect * tanHalfFov;
        float ndcY = (screenPos.y / Screen.height * 2f - 1f) * tanHalfFov;

        // Direction vector in camera space (right-handed, Z=+1 forward).
        Vector3 localDir = new Vector3(ndcX, ndcY, 1f).normalized;

        // Transform to world space using the drag-start rotation.
        Vector3 worldDir = dragStartCameraRotation * localDir;

        return new Ray(dragStartCameraPosition, worldDir);
    }
}
