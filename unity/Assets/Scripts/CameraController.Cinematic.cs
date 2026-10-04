using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public partial class CameraController
{
    [Header("撮影モード設定")]
    [SerializeField] private Button cinematicModeButton;
    [Min(0f)] public float cinematicOrbitSpeed = 3f;
    [Min(0f)] public float cinematicMoveSpeed = 5f;
    [Min(0f)] public float cinematicManualMoveSpeed = 20f;
    [Min(0f)] public float cinematicHeightSpeed = 5f;

    private enum CinematicMode { Off, Orbit, Travel }
    private CinematicMode cinematicMode;
    private bool cinematicPaused;
    private bool cinematicHasPivot;
    private float cinematicDirection = 1f;
    private Vector3 cinematicTravelDirection;
    private int cameraModeExitFrame = -1;

    public bool IsCinematicMode => cinematicMode != CinematicMode.Off;

    // Escでフライ・撮影を終えたフレームも遮断し、同じ押下を背後の選択解除へ渡さない。
    public bool BlocksWorldInput => isFlyMode || IsCinematicMode || cameraModeExitFrame == Time.frameCount;

    private bool CanStartCinematicMode()
    {
        return !DriveMode.IsOpen
            && (GameStateManager.Instance == null ||
                (GameStateManager.Instance.IsGameStarted() && GameStateManager.Instance.IsWorldVisible))
            && (InputHandler.Instance == null || InputHandler.Instance.IsNormalMode);
    }

    public void ToggleCinematicMode()
    {
        if (IsCinematicMode)
        {
            StopCinematicMode();
            return;
        }
        if (!CanStartCinematicMode()) return;
        if (cam == null) cam = GetComponent<Camera>();
        if (cam == null) return;

        SetFlyMode(false);
        CancelCameraGestures();
        RadialMenuController.Instance?.CloseAndCancel();
        cinematicPaused = false;
        cinematicDirection = 1f;
        SetCinematicMode(CinematicMode.Orbit);

        // Buttonの選択を残すとSpace/方向キーがSubmit/Navigationとしても処理される。
        EventSystem.current?.SetSelectedGameObject(null);
    }

    public void StopCinematicMode()
    {
        if (!IsCinematicMode) return;
        cinematicMode = CinematicMode.Off;
        cameraModeExitFrame = Time.frameCount;
        cinematicHasPivot = false;
        cinematicPaused = false;
        currentHeight = transform.position.y;
        CancelCameraGestures();
        RefreshCameraModePresentation();
    }

    private void CancelCameraGestures()
    {
        EndMouseRotation();
        StopFocus();
        isMouseDragging = false;
        isPendingDrag = false;
        dragVelocity = Vector3.zero;
    }

    private void SetCinematicMode(CinematicMode mode)
    {
        cinematicMode = mode;
        ResetLookAngles();
        if (mode == CinematicMode.Orbit)
        {
            cinematicHasPivot = TryGetRotationPivotPoint(out Vector3 pivot, true);
            if (cinematicHasPivot) InitializeOrbit(pivot, false);
        }
        else
        {
            cinematicTravelDirection = Utils.GetCameraHorizontalForward(cam);
        }
        RefreshCameraModePresentation();
    }

    private void HandleCinematicMovement(float deltaTime)
    {
        EventSystem.current?.SetSelectedGameObject(null);
        if (InputUtil.KeyWasPressedThisFrame(Key.Escape))
        {
            StopCinematicMode();
            return;
        }
        if (InputUtil.KeyWasPressedThisFrame(Key.LeftShift) || InputUtil.KeyWasPressedThisFrame(Key.RightShift))
        {
            SetCinematicMode(cinematicMode == CinematicMode.Orbit ? CinematicMode.Travel : CinematicMode.Orbit);
        }
        if (InputUtil.KeyWasPressedThisFrame(Key.Space))
        {
            if (cinematicMode == CinematicMode.Orbit && !cinematicHasPivot)
                SetCinematicMode(CinematicMode.Orbit);
            else cinematicPaused = !cinematicPaused;
            RefreshCameraModePresentation();
        }
        if (InputUtil.KeyWasPressedThisFrame(Key.Backspace))
        {
            cinematicDirection = -cinematicDirection;
            RefreshCameraModePresentation();
        }

        if (cinematicMode == CinematicMode.Orbit)
        {
            if (!cinematicPaused && cinematicHasPivot)
            {
                // 周回した角度だけ視線も回し、マウスで見回した向きを注視点に対して保つ。
                float step = cinematicOrbitSpeed * cinematicDirection * deltaTime;
                orbitYaw += step;
                lookYaw += step;
                ApplyOrbitPosition();
            }
        }
        else
        {
            float heading = 0f;
            if (InputUtil.KeyWasPressedThisFrame(Key.A)) heading -= 5f;
            if (InputUtil.KeyWasPressedThisFrame(Key.D)) heading += 5f;
            cinematicTravelDirection = Quaternion.AngleAxis(heading, Vector3.up) * cinematicTravelDirection;

            Vector3 forward = Utils.GetCameraHorizontalForward(cam);
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            float horizontal = 0f, vertical = 0f, height = 0f;
            if (InputUtil.KeyPressed(Key.LeftArrow)) horizontal -= 1f;
            if (InputUtil.KeyPressed(Key.RightArrow)) horizontal += 1f;
            if (InputUtil.KeyPressed(Key.DownArrow)) vertical -= 1f;
            if (InputUtil.KeyPressed(Key.UpArrow)) vertical += 1f;
            if (InputUtil.KeyPressed(Key.R)) height += 1f;
            if (InputUtil.KeyPressed(Key.F)) height -= 1f;

            Vector3 velocity = Vector3.ClampMagnitude(forward * vertical + right * horizontal, 1f)
                * cinematicManualMoveSpeed + Vector3.up * (height * cinematicHeightSpeed);
            if (!cinematicPaused) velocity += cinematicTravelDirection * (cinematicMoveSpeed * cinematicDirection);
            Vector3 position = transform.position + velocity * deltaTime;
            if (height < 0f) position.y = Mathf.Max(0f, position.y);
            transform.position = position;
        }
        ApplyMouseLook();

        // 撮影中のYは地形追従させない。境界制限と通常操作へ戻すための高度同期だけを行う。
        ClampPositionToCityBoundary();
        currentHeight = transform.position.y;
    }
}
