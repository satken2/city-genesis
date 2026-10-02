using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>描画専用の乗車モード。都市の状態・経済時間は変えず、既存の交通演出を追従する。</summary>
[DefaultExecutionOrder(100)]
public sealed class DriveMode : MonoBehaviour
{
    public static DriveMode Instance { get; private set; }
    public static bool IsOpen => Instance != null && Instance.open;
    public static bool IsRiding => Instance != null && Instance.riding;
    public static float SpeedMultiplier => IsRiding && Application.isFocused && InputUtil.KeyPressed(Key.Space) ? 5f : 1f;
    public static bool BlocksCamera => IsRiding || (IsOpen && Instance.PanelRect.Contains(Instance.GuiMouse));
    private bool open, picking, riding, rail, forward = true, saved;
    private int vehicleType = Constants.VEHICLE_STANDARD, lineId = -1, pickedPath = -1;
    private Vector3 pickedPoint, position, direction = Vector3.forward, savedPosition;
    private Quaternion savedRotation;
    private bool savedOrthographic;
    private float savedSize, savedFov, savedNear;
    private Camera cameraRef;
    private VehicleManager cars;
    private TransitVehicleManager trains;
    private PathManager paths;
    private IpcClient ipc;
    private string message = "";
    private float noticeUntil, nextBranches;
    private float eyeHeight;
    private int branchPath = -1;
    private Vector2 scroll, mouseDown;
    private bool pendingClick;
    private VehiclePolyline pickedPolyline;
    private readonly Dictionary<string, string> labels = new();
    private readonly List<TransitLineSummary> lines = new();
    private readonly List<VehicleManager.DriveBranch> branches = new();
    private GUIStyle boxStyle, labelStyle, buttonStyle;
    private Vector2 GuiMouse => new(InputUtil.MousePosition.x, Screen.height - InputUtil.MousePosition.y);
    private Rect PanelRect => new(Mathf.Max(8, (Screen.width - 520) * .5f), 20, Mathf.Min(520, Screen.width - 16),
        riding ? 230 : picking ? 280 : Mathf.Min(410, Screen.height - 120));

    public static DriveMode Ensure()
    {
        if (Instance == null) new GameObject("DriveMode").AddComponent<DriveMode>();
        return Instance;
    }
    private void Awake() { Instance = this; }

    public static bool TryGetLoadCenter(out Vector3 center)
    {
        center = IsRiding ? Instance.position : default;
        return IsRiding;
    }

    public void Open(Dictionary<string, object> translatedLabels)
    {
        if (GameStateManager.Instance == null || !GameStateManager.Instance.IsGameStarted()) return;
        labels.Clear();
        if (translatedLabels != null) foreach (var entry in translatedLabels) labels[entry.Key] = entry.Value?.ToString() ?? "";
        if (open) { Close(true); return; }
        // 編集ハンドラ自身もキーを受けるため、編集中に入力ゲートだけを被せない。
        var input = InputHandler.Instance;
        if (input != null && !input.IsNormalMode)
        { message = L("finishEditing"); noticeUntil = Time.unscaledTime + 5; return; }
        cameraRef = Camera.main;
        cars = FindAnyObjectByType<VehicleManager>();
        trains = TransitVehicleManager.Instance;
        paths = FindAnyObjectByType<PathManager>();
        if (cameraRef == null || paths == null) return;
        BindEvents();
        CitizenFollow.Instance?.Stop(true);
        CameraController.Instance?.SetFlyMode(false);
        RadialMenuController.Instance?.CloseAndCancel();
        savedPosition = cameraRef.transform.position; savedRotation = cameraRef.transform.rotation;
        savedOrthographic = cameraRef.orthographic; savedSize = cameraRef.orthographicSize;
        savedFov = cameraRef.fieldOfView; savedNear = cameraRef.nearClipPlane; saved = true;
        CameraController.Instance?.SetView(savedPosition, savedRotation);
        open = true; picking = riding = false; pickedPath = -1; message = ""; pendingClick = false;
        TransitDataStore.Ensure().Refresh();
        RefreshLines();
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        WindowUtil.BringToForeground();
#endif
    }

    private string L(string key) => labels.TryGetValue(key, out var value) ? value : key;
    private void RefreshLines() { trains?.GetDriveLines(lines); }
    private void BindEvents()
    {
        if (ipc == IpcClient.Instance) return;
        UnbindEvents(); ipc = IpcClient.Instance;
        if (ipc == null) return;
        ipc.OnSessionReset += ResetSession; ipc.OnGameEnd += ResetSession;
        ipc.OnSessionReload += Reload; ipc.OnSessionForceReload += Reload;
        ipc.OnWorldChangeState += ChangeState;
    }
    private void UnbindEvents()
    {
        if (ipc == null) return;
        ipc.OnSessionReset -= ResetSession; ipc.OnGameEnd -= ResetSession;
        ipc.OnSessionReload -= Reload; ipc.OnSessionForceReload -= Reload;
        ipc.OnWorldChangeState -= ChangeState;
    }
    private void ResetSession() => Close(false);
    private void Reload() => Close(true);
    private void ChangeState(string state, Dictionary<string, object> parameters) => Close(true);
    private void OnDisable() { Close(true); UnbindEvents(); ipc = null; }
    private void OnDestroy() { if (Instance == this) Instance = null; }

    public void Close(bool restoreCamera)
    {
        if (!open) return;
        open = picking = riding = pendingClick = false;
        cars?.StopDrive(); trains?.StopDrive(); branches.Clear();
        if (saved && restoreCamera && cameraRef != null)
        {
            cameraRef.orthographic = savedOrthographic; cameraRef.orthographicSize = savedSize;
            cameraRef.fieldOfView = savedFov; cameraRef.nearClipPlane = savedNear;
            CameraController.Instance?.SetView(savedPosition, savedRotation);
            cameraRef.orthographicSize = savedSize;
        }
        else if (saved && cameraRef != null)
        {
            cameraRef.orthographic = savedOrthographic; cameraRef.orthographicSize = savedSize;
            cameraRef.fieldOfView = savedFov; cameraRef.nearClipPlane = savedNear;
        }
        saved = false; message = "";
        CameraCellChecker.Instance?.ResetViewState();
    }

    private void Update()
    {
        if (!open) return;
        if (GameStateManager.Instance == null || !GameStateManager.Instance.IsGameStarted()) { Close(false); return; }
        if (!Application.isFocused) { pendingClick = false; return; }
        if (InputUtil.KeyWasPressedThisFrame(Key.Escape)) { Close(true); return; }
        if (riding)
        {
            if ((rail && (trains == null || trains.DrivenLineId < 0)) || (!rail && (cars == null || cars.DrivenVehicle == null)))
            { Close(true); return; }
            if (!rail) UpdateBranches();
        }
        else if (picking) PickStart();
    }

    private void PickStart()
    {
        if (PanelRect.Contains(GuiMouse) || (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()))
        { pendingClick = false; return; }
        if (InputUtil.LeftMouseWasPressedThisFrame) { pendingClick = true; mouseDown = InputUtil.MousePosition; }
        if (pendingClick && ((Vector2)InputUtil.MousePosition - mouseDown).sqrMagnitude > 25) pendingClick = false;
        if (!pendingClick || !InputUtil.LeftMouseWasReleasedThisFrame) return;
        pendingClick = false;
        if (!Physics.Raycast(cameraRef.ScreenPointToRay(InputUtil.MousePosition), out var hit, 100000, ~0, QueryTriggerInteraction.Ignore)) return;
        var info = hit.collider.GetComponentInParent<ObjectIdInfo>();
        if (info == null || !info.CompareTag("PathObject")) { message = L("invalidStart"); return; }
        var data = paths.GetPathData(info.id);
        if (data == null || data.status != Constants.STATUS_ACTIVE || !paths.TryGetPathPolyline(info.id, out var points, false) || points.Count < 2)
        { message = L("invalidStart"); return; }
        if (rail)
        {
            RefreshLines();
            var line = lines.Find(item => item.line_agent_id == lineId);
            if (line == null) { message = L("invalidStart"); return; }
            var clock = new TransitMotionClock(line.journeys);
            var polyline = new VehiclePolyline(points);
            float fraction = polyline.Project(hit.point) / Mathf.Max(.001f, polyline.Length);
            if (!TransitVehicleManager.TryFindDriveTime(clock, info.id, fraction, forward, out _))
            {
                if (!TransitVehicleManager.TryFindDriveTime(clock, info.id, fraction, !forward, out _))
                { message = L("invalidStart"); return; }
                forward = !forward;
            }
        }
        else
        {
            if (data.path_type != Constants.PATH_STREET && data.path_type != Constants.PATH_MOTORWAY)
            { message = L("invalidStart"); return; }
            if (!VehicleManager.DirectionAllowsEntry(data.travel_direction, forward)) forward = !forward;
        }
        pickedPath = info.id; pickedPoint = hit.point; pickedPolyline = new VehiclePolyline(points); message = "";
    }

    private void StartRide()
    {
        bool started = rail ? trains != null && trains.StartDrive(lineId, pickedPath, pickedPoint, forward)
            : cars != null && cars.StartDrive(pickedPath, pickedPoint, forward, vehicleType);
        if (!started) { message = L("notReady"); return; }
        riding = true; picking = false; message = ""; nextBranches = 0;
        cameraRef.orthographic = false; cameraRef.fieldOfView = 65; cameraRef.nearClipPlane = .1f;
        ReadPose();
        eyeHeight = rail ? 5f : 2f;
        if (!rail && cars.DrivenVehicle?.go != null)
            foreach (var renderer in cars.DrivenVehicle.go.GetComponentsInChildren<Renderer>())
                eyeHeight = Mathf.Max(eyeHeight, renderer.bounds.max.y - position.y + .5f);
        ApplyCamera(true);
        CameraCellChecker.Instance?.ResetViewState();
    }

    private void ReadPose()
    {
        if (rail) { position = trains.DrivePosition; direction = trains.DriveDirection; }
        else if (cars.DrivenVehicle != null) cars.DrivenVehicle.Sample(out position, out direction);
    }
    private void LateUpdate()
    {
        if (!riding || cameraRef == null) return;
        ReadPose(); ApplyCamera(false);
    }
    private void ApplyCamera(bool immediate)
    {
        Vector3 eye = position + Vector3.up * eyeHeight;
        Quaternion rotation = Quaternion.LookRotation(direction, Vector3.up) * Quaternion.Euler(5, 0, 0);
        // 位置の遅延は高速走行時の車体へのめり込みを招くため、回転だけを平滑化する。
        cameraRef.transform.SetPositionAndRotation(eye, immediate ? rotation : Quaternion.Slerp(cameraRef.transform.rotation, rotation,
            1 - Mathf.Exp(-8 * Time.unscaledDeltaTime * SpeedMultiplier)));
    }

    private void UpdateBranches()
    {
        var vehicle = cars.DrivenVehicle;
        if (branchPath != vehicle.pathId || Time.unscaledTime >= nextBranches)
        { cars.GetDriveBranches(branches); nextBranches = Time.unscaledTime + .15f; branchPath = vehicle.pathId; }
        if (vehicle.InIntersection || vehicle.Remaining > Mathf.Max(70, vehicle.speed * SpeedMultiplier * 5) || branches.Count == 0) return;
        int selected = branches.FindIndex(branch => branch.Path == cars.RequestedDrivePath);
        if (InputUtil.KeyWasPressedThisFrame(Key.LeftArrow)) selected = (selected - 1 + branches.Count) % branches.Count;
        if (InputUtil.KeyWasPressedThisFrame(Key.RightArrow)) selected = (selected + 1) % branches.Count;
        if (InputUtil.KeyWasPressedThisFrame(Key.UpArrow))
        {
            selected = 0;
            for (int i = 1; i < branches.Count; i++) if (Mathf.Abs(branches[i].Angle) < Mathf.Abs(branches[selected].Angle)) selected = i;
        }
        if (selected >= 0) cars.RequestedDrivePath = branches[selected].Path;
    }

    private void OnGUI()
    {
        if (!open && Time.unscaledTime >= noticeUntil) return;
        // Spaceによる早送りを、直前に押した終了ボタンのキーボード操作として解釈させない。
        if (riding) GUIUtility.keyboardControl = 0;
        if (boxStyle == null)
        {
            boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(16, 16, 12, 12) };
            labelStyle = new GUIStyle(GUI.skin.label) { font = UiFontProvider.GetFont(16), fontSize = 16, wordWrap = true };
            buttonStyle = new GUIStyle(GUI.skin.button) { font = UiFontProvider.GetFont(16), fontSize = 16, wordWrap = true, padding = new RectOffset(8, 8, 6, 6) };
        }
        GUILayout.BeginArea(PanelRect, boxStyle);
        GUILayout.Label(L("title"), labelStyle);
        if (!open) GUILayout.Label(message, labelStyle);
        else if (riding) DrawRideHud();
        else if (picking) DrawPickHud();
        else DrawMenu();
        GUILayout.EndArea();
        if (picking && pickedPath >= 0) DrawStartMarker();
    }

    private void DrawMenu()
    {
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(L("car"), buttonStyle)) { rail = false; message = ""; }
        if (GUILayout.Button(L("rail"), buttonStyle)) { rail = true; RefreshLines(); message = ""; }
        GUILayout.EndHorizontal();
        scroll = GUILayout.BeginScrollView(scroll);
        if (!rail)
        {
            if (GUILayout.Button(L("standard"), buttonStyle)) SelectCar(Constants.VEHICLE_STANDARD);
            if (GUILayout.Button(L("commercial"), buttonStyle)) SelectCar(Constants.VEHICLE_COMMERCIAL);
            if (GUILayout.Button(L("truck"), buttonStyle)) SelectCar(Constants.VEHICLE_TRUCK);
        }
        else
        {
            RefreshLines();
            if (lines.Count == 0) GUILayout.Label(L("noLines"), labelStyle);
            foreach (var line in lines)
                if (GUILayout.Button(string.IsNullOrEmpty(line.name) ? "#" + line.line_agent_id : line.name, buttonStyle))
                { lineId = line.line_agent_id; picking = true; pickedPath = -1; }
        }
        GUILayout.EndScrollView();
        if (GUILayout.Button(L("close"), buttonStyle)) Close(true);
    }
    private void SelectCar(int type) { vehicleType = type; picking = true; pickedPath = -1; }
    private void DrawPickHud()
    {
        GUILayout.Label(L(rail ? "pickRail" : "pickRoad"), labelStyle);
        GUILayout.Label(pickedPath >= 0 ? L("startSelected") : L("clickStart"), labelStyle);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(L("reverse"), buttonStyle)) forward = !forward;
        GUI.enabled = pickedPath >= 0;
        if (GUILayout.Button(L("start"), buttonStyle)) StartRide();
        GUI.enabled = true;
        if (GUILayout.Button(L("back"), buttonStyle)) { picking = false; pickedPath = -1; message = ""; }
        if (GUILayout.Button(L("close"), buttonStyle)) Close(true);
        GUILayout.EndHorizontal();
        if (message.Length > 0) GUILayout.Label(message, labelStyle);
    }
    private void DrawRideHud()
    {
        GUILayout.Label(L("controls") + (SpeedMultiplier > 1 ? "  ×5" : "  ×1"), labelStyle);
        if (rail) GUILayout.Label(L(trains.DriveWaiting ? "loading" : "onRoute"), labelStyle);
        else
        {
            var vehicle = cars.DrivenVehicle;
            if (vehicle != null && !vehicle.InIntersection && vehicle.Remaining <= Mathf.Max(70, vehicle.speed * SpeedMultiplier * 5))
            {
                GUILayout.Label(branches.Count == 0 ? L("waiting") : L("turnControls"), labelStyle);
                GUILayout.BeginHorizontal();
                foreach (var branch in branches)
                {
                    string arrow = Mathf.Abs(branch.Angle) > 150 ? "↓" : branch.Angle < -25 ? "←" : branch.Angle > 25 ? "→" : "↑";
                    string text = arrow + " " + (cars.RequestedDrivePath == branch.Path ? "●" : "○");
                    if (GUILayout.Button(text, buttonStyle)) cars.RequestedDrivePath = branch.Path;
                }
                GUILayout.EndHorizontal();
            }
        }
        if (GUILayout.Button(L("close"), buttonStyle)) Close(true);
    }
    private void DrawStartMarker()
    {
        if (pickedPolyline == null) return;
        pickedPolyline.Sample(pickedPolyline.Project(pickedPoint), forward, out var point, out var tangent);
        var screen = cameraRef.WorldToScreenPoint(point + Vector3.up);
        var ahead = cameraRef.WorldToScreenPoint(point + tangent * 12 + Vector3.up);
        if (screen.z <= 0 || ahead.z <= 0) return;
        GUI.Label(new Rect(screen.x - 30, Screen.height - screen.y - 15, 100, 30), "● " + L("start"), labelStyle);
        var pivot = new Vector2(ahead.x, Screen.height - ahead.y);
        var matrix = GUI.matrix;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(screen.y - ahead.y, ahead.x - screen.x) * Mathf.Rad2Deg, pivot);
        GUI.Label(new Rect(pivot.x - 12, pivot.y - 15, 30, 30), "▶", labelStyle);
        GUI.matrix = matrix;
    }
}
