using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Rendering-only ride mode following existing traffic animation without changing city state or economic time.
/// Depart from a selected location or select a moving car, bus, or train to watch its ongoing journey.
/// Switch between first-person views (forward from just ahead of the leading vehicle, or sideways through a window) and third person (<see cref="ChaseCamera"/>).
/// While riding, hide Tauri's UI and selection panels as in capture mode, showing status and controls in the mode caption.
/// </summary>
[DefaultExecutionOrder(100)]
public sealed class DriveMode : MonoBehaviour
{
    /// <summary>Driver eye height as a fraction of body height. Cars, buses, trucks, and trains generally place the driver's eyes around 3/4 of vehicle height.</summary>
    private const float EyeHeightRatio = .75f;

    public static DriveMode Instance { get; private set; }
    public static bool IsOpen => Instance != null && Instance.open;
    public static bool IsRiding => Instance != null && Instance.riding;
    public static float SpeedMultiplier => IsRiding && Application.isFocused && InputUtil.KeyPressed(Key.Space) ? 5f : 1f;
    public static bool BlocksCamera => IsRiding || (IsOpen && Instance.PanelRect.Contains(Instance.GuiMouse));
    public static string RideCaption => IsRiding ? Instance.rideCaption : "";
    private bool open, picking, riding, rail, watch, forward = true, thirdPerson, sideView, oppositeWindow;
    private int vehicleType = Constants.VEHICLE_STANDARD, lineId = -1, pickedPath = -1;
    private Vector3 pickedPoint, position, direction = Vector3.forward;
    private float savedFov, savedNear;
    private Camera cameraRef;
    private VehicleManager cars;
    private TransitVehicleManager trains;
    private PathManager paths;
    private IpcClient ipc;
    private string message = "", rideCaption = "";
    private float noticeUntil, nextBranches;
    private float eyeHeight, halfLength, halfWidth;
    private GameObject eyeBody;
    private bool placedView;
    private int branchPath = -1;
    private Vector2 scroll, mouseDown;
    private bool pendingClick;
    private VehiclePolyline pickedPolyline;
    private Vehicle pressedCar;
    private int pressedLine = -1, pressedTrain;
    private readonly ChaseCamera chase = new();
    private readonly List<Vehicle> carTargets = new();
    private readonly Dictionary<string, string> labels = new();
    private readonly List<TransitLineSummary> lines = new();
    private readonly List<VehicleManager.DriveBranch> branches = new();
    private GUIStyle boxStyle, labelStyle, buttonStyle;
    private Vector2 GuiMouse => new(InputUtil.MousePosition.x, Screen.height - InputUtil.MousePosition.y);
    private Rect PanelRect => new(Mathf.Max(8, (Screen.width - 520) * .5f), 20, Mathf.Min(520, Screen.width - 16),
        picking ? 280 : Mathf.Min(410, Screen.height - 120));
    private Vehicle RiddenCar => cars == null ? null : cars.DrivenVehicle ?? cars.WatchedVehicle;

    public static DriveMode Ensure()
    {
        if (Instance == null) new GameObject("DriveMode").AddComponent<DriveMode>();
        return Instance;
    }
    private void Awake() { Instance = this; }

    /// <summary>
    /// Drive button at the 3D view's top right. Request startup from the cockpit to receive selection text in the UI's current language,
    /// then call <see cref="Open"/> on the returned drive:open event. Pressing while open exits.
    /// </summary>
    public static void RequestToggle()
    {
        EventSystem.current?.SetSelectedGameObject(null);
        if (IsOpen) Instance.Close(true);
        else IpcClient.Instance?.EmitDriveRequest();
    }

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
        // Editing handlers also receive keys directly, so do not merely overlay an input gate while editing.
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
        CameraController.Instance?.SetView(cameraRef.transform.position, cameraRef.transform.rotation);
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

    /// <summary>
    /// End the drive. If riding, restore the previous field of view; when handOverView is true, hand the current view
    /// to normal camera controls, raising it to the surface if underground. Do not carry old-city views across city switches.
    /// </summary>
    public void Close(bool handOverView)
    {
        if (!open) return;
        bool wasRiding = riding;
        open = picking = riding = pendingClick = false;
        cars?.StopDrive(); trains?.StopDrive(); branches.Clear();
        rideCaption = ""; eyeBody = null; message = "";
        if (wasRiding && cameraRef != null)
        {
            cameraRef.fieldOfView = savedFov; cameraRef.nearClipPlane = savedNear;
            if (handOverView && CameraController.Instance != null)
            {
                Vector3 view = cameraRef.transform.position;
                view.y = Mathf.Max(view.y, Utils.GetGroundHeight(view));
                CameraController.Instance.SetView(view, cameraRef.transform.rotation);
            }
        }
        if (wasRiding && CameraController.Instance != null) CameraController.Instance.RefreshCameraModePresentation();
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
            if ((rail && (trains == null || trains.DrivenLineId < 0)) || (!rail && RiddenCar == null))
            { Close(true); return; }
            if (InputUtil.KeyWasPressedThisFrame(Key.V)) ToggleView();
            // Start window view on the traffic side and switch left/right with S. F restores forward first-person view.
            if (InputUtil.KeyWasPressedThisFrame(Key.S))
            {
                oppositeWindow = sideView && !oppositeWindow;
                thirdPerson = false; sideView = true;
            }
            if (InputUtil.KeyWasPressedThisFrame(Key.F)) { thirdPerson = false; sideView = false; }
            if (!rail && cars.DrivenVehicle != null) UpdateBranches();
            RefreshRideCaption();
        }
        else if (picking) PickStart();
    }

    private void PickStart()
    {
        if (PanelRect.Contains(GuiMouse) || (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()))
        { pendingClick = false; return; }
        if (InputUtil.LeftMouseWasPressedThisFrame)
        {
            pendingClick = true; mouseDown = InputUtil.MousePosition;
            // Moving vehicles may advance before release, so select the entity hit at button press.
            if (watch) PickVehicle(cameraRef.ScreenPointToRay(mouseDown));
        }
        if (pendingClick && ((Vector2)InputUtil.MousePosition - mouseDown).sqrMagnitude > 25) pendingClick = false;
        if (!pendingClick || !InputUtil.LeftMouseWasReleasedThisFrame) return;
        pendingClick = false;
        if (watch) { StartWatch(); return; }
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

    /// <summary>Nearest car or train hit by the ray. Reject if a building or terrain blocks it.</summary>
    private void PickVehicle(Ray ray)
    {
        pressedCar = null; pressedLine = -1;
        float nearest = float.PositiveInfinity;
        if (cars != null)
        {
            carTargets.Clear();
            cars.CollectPickTargets(carTargets, GeometryUtility.CalculateFrustumPlanes(cameraRef));
            foreach (var v in carTargets)
                if (cars.RayHit(v, ray, out float d) && d < nearest) { nearest = d; pressedCar = v; }
        }
        if (trains != null && trains.TryPickTrain(ray, out int line, out int train, out float distance) && distance < nearest)
        { nearest = distance; pressedCar = null; pressedLine = line; pressedTrain = train; }
        if (!float.IsPositiveInfinity(nearest) && Physics.Raycast(ray, out _, Mathf.Max(0, nearest - .05f), ~0, QueryTriggerInteraction.Ignore))
        { pressedCar = null; pressedLine = -1; }
    }

    private void StartWatch()
    {
        bool car = pressedCar != null;
        bool started = car ? cars.Watch(pressedCar) : pressedLine >= 0 && trains.Watch(pressedLine, pressedTrain);
        pressedCar = null; pressedLine = -1;
        if (!started) { message = L("invalidVehicle"); return; }
        rail = !car;
        BeginRide();
    }

    private void StartRide()
    {
        bool started = rail ? trains != null && trains.StartDrive(lineId, pickedPath, pickedPoint, forward)
            : cars != null && cars.StartDrive(pickedPath, pickedPoint, forward, vehicleType);
        if (!started) { message = L("notReady"); return; }
        BeginRide();
    }

    private void BeginRide()
    {
        riding = true; picking = false; message = ""; nextBranches = 0;
        // Exit top-down orthographic mode when boarding, because disembarking hands the current view to normal controls.
        CameraController.Instance?.SetTopDownMode(false);
        savedFov = cameraRef.fieldOfView; savedNear = cameraRef.nearClipPlane;
        cameraRef.orthographic = false; cameraRef.fieldOfView = 65; cameraRef.nearClipPlane = .1f;
        eyeBody = null; placedView = false;
        ReadPose();
        var car = rail ? null : RiddenCar;
        halfLength = car == null ? trains.DriveCarLength * .5f : VehicleModelResolver.ModelBounds(car.go.transform).max.z;
        chase.Reset();
        ApplyCamera();
        CameraCellChecker.Instance?.ResetViewState();
        RefreshRideCaption();
    }

    private void ReadPose()
    {
        GameObject body;
        if (rail) { position = trains.DrivePosition; direction = trains.DriveDirection; body = trains.DriveBody; }
        else
        {
            var car = RiddenCar;
            if (car == null) return;
            car.Sample(out position, out direction); body = car.go;
        }
        // Derive eye height and window lateral position from the leading body model. Remeasure when reversal changes the leading vehicle.
        if (body != null && body != eyeBody)
        {
            var bounds = VehicleModelResolver.ModelBounds(body.transform);
            eyeBody = body; eyeHeight = EyeHeightRatio * bounds.max.y; halfWidth = bounds.max.x;
        }
    }
    private void LateUpdate()
    {
        if (!riding || cameraRef == null) return;
        ReadPose(); ApplyCamera();
    }
    private void ApplyCamera()
    {
        if (thirdPerson) { chase.Apply(cameraRef, position, direction, Time.unscaledDeltaTime * SpeedMultiplier); return; }
        // The train renders on the frame after boarding, so do not place the camera until its leading body can be measured.
        if (eyeBody == null) return;
        // Place first-person views just outside the body edge in the viewing direction, forward or sideways, to exclude the body. Apply no terrain/structure constraints.
        Vector3 look = sideView ? WindowSide(direction) * (oppositeWindow ? -1f : 1f) : direction;
        Vector3 eye = position + look * ((sideView ? halfWidth : halfLength) + 1f) + Vector3.up * eyeHeight;
        Quaternion rotation = Quaternion.LookRotation(look, Vector3.up) * Quaternion.Euler(5, 0, 0);
        // Smooth only rotation; position lag would embed the view in the body at high speed. On initial placement, do not rotate from the pre-ride orientation.
        cameraRef.transform.SetPositionAndRotation(eye, !placedView ? rotation : Quaternion.Slerp(cameraRef.transform.rotation, rotation,
            1 - Mathf.Exp(-8 * Time.unscaledDeltaTime * SpeedMultiplier)));
        placedView = true;
    }

    /// <summary>Initial window direction: face the traffic side, left for left-hand traffic, to view roadsides or the outside of double track.</summary>
    private static Vector3 WindowSide(Vector3 heading)
    {
        Vector3 left = Vector3.Cross(heading, Vector3.up).normalized;
        return CityInfoStore.TrafficHand == Constants.TRAFFIC_HAND_LEFT ? left : -left;
    }

    private void ToggleView()
    {
        thirdPerson = !thirdPerson;
        if (thirdPerson) chase.Reset();
    }

    private void UpdateBranches()
    {
        var vehicle = cars.DrivenVehicle;
        if (branchPath != vehicle.pathId || Time.unscaledTime >= nextBranches)
        { cars.GetDriveBranches(branches); nextBranches = Time.unscaledTime + .15f; branchPath = vehicle.pathId; }
        if (!AtJunction(vehicle) || branches.Count == 0) return;
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

    /// <summary>Branch-selection zone: from five seconds of travel before the intersection, at least 70m, until entering it.</summary>
    private static bool AtJunction(Vehicle vehicle)
        => !vehicle.InIntersection && vehicle.Remaining <= Mathf.Max(70, vehicle.speed * SpeedMultiplier * 5);

    /// <summary>
    /// Show ride status and controls in the same caption as capture mode, only on changes. Use English because the caption font supports only Latin text.
    /// List branch options as arrows relative to the travel direction, enclosing the selected option in [ ].
    /// </summary>
    private void RefreshRideCaption()
    {
        var vehicle = rail ? null : cars.DrivenVehicle;
        string status = rail ? (trains.DriveWaiting ? "Loading track" : "On route")
            : vehicle == null ? "NPC driving"
            : AtJunction(vehicle) ? RouteCaption()
            : "";
        string caption = $"Drive {(thirdPerson ? "3rd person" : sideView ? "side window" : "1st person")} | x{SpeedMultiplier:0}{(status.Length > 0 ? " | " + status : "")}\n"
            + "Hold Space: x5 | V: view | S: left/right | F: front | Esc: exit"
            + (vehicle != null ? " | ←/→: next road | ↑: straightest" : "")
            + (thirdPerson ? "\nWheel: distance | Right-drag: angle" : "");
        if (caption == rideCaption) return;
        rideCaption = caption;
        if (CameraController.Instance != null) CameraController.Instance.RefreshCameraModePresentation();
    }

    private string RouteCaption()
    {
        if (branches.Count == 0) return "Dead end / waiting";
        string text = "Next road:";
        foreach (var branch in branches)
        {
            string arrow = Mathf.Abs(branch.Angle) > 150 ? "↓" : branch.Angle < -25 ? "←" : branch.Angle > 25 ? "→" : "↑";
            text += cars.RequestedDrivePath == branch.Path ? " [" + arrow + "]" : " " + arrow;
        }
        return text;
    }

    private void OnGUI()
    {
        if (riding || (!open && Time.unscaledTime >= noticeUntil)) return;
        if (boxStyle == null)
        {
            boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(16, 16, 12, 12) };
            labelStyle = new GUIStyle(GUI.skin.label) { font = UiFontProvider.GetFont(16), fontSize = 16, wordWrap = true };
            buttonStyle = new GUIStyle(GUI.skin.button) { font = UiFontProvider.GetFont(16), fontSize = 16, wordWrap = true, padding = new RectOffset(8, 8, 6, 6) };
        }
        GUILayout.BeginArea(PanelRect, boxStyle);
        GUILayout.Label(L("title"), labelStyle);
        if (!open) GUILayout.Label(message, labelStyle);
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
        if (GUILayout.Button(L("running"), buttonStyle)) { watch = true; picking = true; pickedPath = -1; message = ""; }
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
                { lineId = line.line_agent_id; watch = false; picking = true; pickedPath = -1; }
        }
        GUILayout.EndScrollView();
        if (GUILayout.Button(L("close"), buttonStyle)) Close(true);
    }
    private void SelectCar(int type) { vehicleType = type; watch = false; picking = true; pickedPath = -1; }
    private void DrawPickHud()
    {
        GUILayout.Label(L(watch ? "pickVehicle" : rail ? "pickRail" : "pickRoad"), labelStyle);
        if (!watch) GUILayout.Label(pickedPath >= 0 ? L("startSelected") : L("clickStart"), labelStyle);
        GUILayout.BeginHorizontal();
        if (!watch)
        {
            if (GUILayout.Button(L("reverse"), buttonStyle)) forward = !forward;
            GUI.enabled = pickedPath >= 0;
            if (GUILayout.Button(L("start"), buttonStyle)) StartRide();
            GUI.enabled = true;
        }
        if (GUILayout.Button(L("back"), buttonStyle)) { picking = false; pickedPath = -1; message = ""; }
        if (GUILayout.Button(L("close"), buttonStyle)) Close(true);
        GUILayout.EndHorizontal();
        if (message.Length > 0) GUILayout.Label(message, labelStyle);
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
