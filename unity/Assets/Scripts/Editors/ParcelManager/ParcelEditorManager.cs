using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

/// <summary>
/// Central manager for parcel creation mode (STATE_EDIT_PARCEL).
/// This class's <see cref="CurrentMode"/> is the source of truth for creation modes
/// (along road / entire block / random / single parcel / subdivision), and manages point/path/parcel selection
/// in the 3D view, preview rendering, and input preset switching.
///
/// Interaction model (same as road construction):
/// - **Holding Ctrl opens the radial menu (<see cref="ParcelEditControlMenu"/>) at the cursor to switch creation modes**.
///   Switching immediately resets the input preset and selection and notifies the UI creation panel through `edit_parcel:set_mode`
///   (the panel adjusts its input fields accordingly).
/// - Set numeric values such as width, depth, angle, and average area in the Tauri creation panel.
/// - Click in the 3D view to select a point/path/parcel, then create it with the panel confirmation button.
/// - **Confirmation does not exit the mode**. The UI sends `edit_parcel:reset_selection`,
///   and <see cref="ResetSelection"/> clears only the selection, waiting for the next click in the same mode (continuous creation).
///
/// Creation modes can also be chosen from panel tiles (`world:change_state` arrives as usual),
/// but both paths converge on <see cref="ApplyCreationMode"/>.
/// </summary>
public class ParcelEditorManager : MonoBehaviour
{
    /// <summary>Instance intended to exist once per scene (self-registers in Awake).</summary>
    public static ParcelEditorManager Instance { get; private set; }

    [Header("References")]
    public ParcelPlanRenderer parcelPlanRenderer;  // Parcel preview renderer
    public Camera gameCamera;                      // Game camera
    public ParcelManager parcelManager;            // Existing manager that draws committed parcels

    /// <summary>Type of target selected in the 3D view. Each creation mode has one target type.</summary>
    public enum SelectionKind
    {
        Point,    // Click a point on terrain.
        Path,     // Click an existing path.
        Parcel,   // Click an existing parcel.
    }

    /// <summary>Definition of one creation mode (shared source for radial menu, HUD, and input preset).</summary>
    public class ModeInfo
    {
        public string Id;              // parcel_creation_mode (identifier shared with the UI)
        public string Label;           // Name shown in the radial menu and HUD
        public string IconName;        // Icon name in Resources/UIIcons (falls back to the label's first character if absent)
        public string Hint;            // Next-action prompt shown in the HUD
        public SelectionKind Kind;
        public int Preset;             // InputModePreset
        public bool HiddenFromMenu;    // Omit from the radial menu (mode not implemented in the UI).
    }

    /// <summary>
    /// Creation mode list. Radial menu items follow this order.
    /// Matches CREATION_SECTIONS in the UI (ParcelCreationPanel): 4 creation modes, then 2 subdivision modes.
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
        // Omit from the radial menu because there is no UI entry (no panel tile).
        // Retain the definition for direct world:change_state requests.
        new ModeInfo { Id = "split_perimeter", Label = "外周分割", IconName = "parcel-split-rect",
            Hint = "分割する区画をクリック", Kind = SelectionKind.Parcel, Preset = InputModePreset.PARCEL_CREATION_SPLIT,
            HiddenFromMenu = true },
    };

    /// <summary>Default creation mode when entering STATE_EDIT_PARCEL without specifying a mode.</summary>
    public const string DefaultMode = "along_path";

    /// <summary>Whether parcel creation mode is active (condition for showing the radial menu and HUD).</summary>
    public bool IsActive => isActive;

    /// <summary>Current creation mode (parcel_creation_mode). This is its source of truth.</summary>
    public string CurrentMode => currentMode;

    /// <summary>Current creation mode definition. Returns the default definition for an unknown ID.</summary>
    public ModeInfo CurrentModeInfo => GetModeInfo(currentMode);

    private bool isActive = false;
    private string currentMode = DefaultMode;

    // Selection mode
    private bool pointSelectionMode = false;
    private bool pathSelectionMode = false;
    private bool parcelSelectionMode = false;

    // Selected object information
    private Vector3 selectedPoint;
    private bool hasSelectedPoint = false;
    private int selectedParcelId = -1;
    private int selectedPathId = -1;

    // Road anchor selected in along_path (click position projected onto the centerline).
    // Reference for where the UI creates parcels when the creation extent is the clicked point.
    private Vector3? selectedPathAnchor;
    private Vector3 selectedPathAnchorTangent = Vector3.forward;

    // Anchor marker overlay (same as the split-point marker in PathTopologyEditManager).
    private OverlayMeshRenderer anchorOverlayRenderer;
    private GameObject anchorOverlayObj;

    // Anchor marker color and length of the bar across the road (m).
    private static readonly Color AnchorMarkerColor = new Color(0.30f, 1.00f, 0.45f);
    private const float AnchorMarkerHalfLength = 8f;

    // Selected parcel preview data
    private List<Vector3> previewBlockOutline = new List<Vector3>();
    private List<List<Vector3>> previewParcels = new List<List<Vector3>>();

    // Rectangle parameters and rotation drag state for empty_area.
    // UI panel fields own width/depth, received incrementally through edit_parcel:set_rect_params
    // (keeps the local rectangle preview during rotation dragging consistent with panel settings).
    private float currentWidth = 10f;
    private float currentHeight = 20f;
    private float currentAngle = 0f;
    private bool isRotating = false;
    private bool justSelectedPoint = false;

    // Layer mask for raycasts
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
        // Initialize the layer mask.
        groundLayerMask = LayerMask.GetMask("Ground", "Terrain");
        pathLayerMask = LayerMask.GetMask("Path");
        parcelLayerMask = LayerMask.GetMask("Parcel");

        // Automatically find/create ParcelPlanRenderer.
        if (parcelPlanRenderer == null)
        {
            parcelPlanRenderer = FindAnyObjectByType<ParcelPlanRenderer>();
            if (parcelPlanRenderer == null)
            {
                Debug.Log("ParcelPlanRendererが見つかりません。このGameObjectに追加します。");
                parcelPlanRenderer = gameObject.AddComponent<ParcelPlanRenderer>();
            }
        }

        // Select nothing before entering creation mode (the input preset also rejects input, but keep no state).
        ClearWorldSelectionState();
    }

    void Update()
    {
        // Process only in creation mode. Do not test selection-mode flags:
        // they clear as soon as a point/path/parcel is selected, which would disable Esc
        // exactly when it is most useful: resetting a completed selection.
        if (!isActive) return;

        // Esc resets selection (also works over the UI). Stay in the same mode to select again.
        if (InputUtil.WorldEscapeWasPressed)
        {
            ResetSelection(notifyUi: true);
            return;
        }

        // Note: HandleXXXSelection functions are called by the InputHandler system.
        // The actual selection is handled by the individual InputHandler classes,
        // so no special processing is needed here.
    }

    void LateUpdate()
    {
        DrawPathAnchorMarker();
    }

    /// <summary>
    /// Draw a circle and cross-road bar at the anchor (clicked point) selected for roadside creation.
    /// Even frames without an anchor must draw an empty frame to clear the marker,
    /// so run every frame after creating the overlay.
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
    /// Prepare the anchor marker overlay. Disable depth testing so it is not buried in road surfaces or terrain
    /// (same handling as RoadPlanManager gizmos).
    /// </summary>
    private void EnsureAnchorOverlay()
    {
        if (anchorOverlayObj != null) return;

        anchorOverlayRenderer = OverlayMeshRenderer.Create("ParcelPathAnchorOverlay", alwaysOnTop: true);
        anchorOverlayObj = anchorOverlayRenderer.gameObject;
    }

    #region 作成モードの制御

    /// <summary>
    /// Gets a creation mode definition. Returns the default (single parcel) for unknown IDs.
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
    /// Starts parcel creation mode (called by IpcStateCoordinator on STATE_EDIT_PARCEL).
    /// This transition originates in the UI, so do not send the mode back (notifyUi=false).
    /// </summary>
    public void EnterCreationMode(string modeId)
    {
        isActive = true;
        ApplyCreationMode(modeId, notifyUi: false);
    }

    /// <summary>
    /// Switches creation modes (called by the Ctrl radial menu).
    /// This switch originates in Unity, so notify the UI creation panel through `edit_parcel:set_mode`
    /// to adjust its fields. Reselecting the same mode only resets the selection.
    /// </summary>
    public void SetCreationMode(string modeId)
    {
        if (!isActive) return;
        if (modeId == currentMode)
        {
            // Interpret reselecting the current mode as selecting again, clearing only the selection
            // (notify the panel to clear its selection display as well).
            ResetSelection(notifyUi: true);
            return;
        }
        ApplyCreationMode(modeId, notifyUi: true);
    }

    /// <summary>
    /// Applies a creation mode, switching the input preset and resetting selection together
    /// (both UI and radial menu paths converge here).
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
    /// Clears selection and preview, returning to accept another selection in the same mode.
    /// Called after creation confirmation (UI sends `edit_parcel:reset_selection`), Esc, or mode switching;
    /// essential for continuous creation without exiting after every parcel.
    ///
    /// If notifyUi=true, send `edit_parcel:set_mode` with the unchanged mode to the UI.
    /// The panel clears its selection and preview in response, preventing a mismatch where pressing Esc in Unity
    /// leaves only the panel able to confirm a supposedly selected point.
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
    /// Ends parcel creation mode (on transitions to STATE_NORMAL or another state).
    /// The radial menu and cursor HUD close automatically based on IsActive.
    /// </summary>
    public void ExitCreationMode()
    {
        isActive = false;
        ClearWorldSelectionState();

    }

    /// <summary>
    /// Receives width/depth from the UI panel for local rectangle previews during rotation dragging
    /// (`edit_parcel:set_rect_params`). Backend previews redraw using final values, but
    /// local drag calculations need panel inputs to avoid inconsistent shapes.
    /// </summary>
    public void SetRectPreviewSize(float width, float height)
    {
        if (width > 0f) currentWidth = width;
        if (height > 0f) currentHeight = height;
    }

    #endregion

    #region 選択モード制御

    /// <summary>
    /// Disable all selection modes.
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

        // Clear visual feedback.
        ClearPreview();
    }

    /// <summary>
    /// Enable point selection mode.
    /// </summary>
    public void EnablePointSelectionMode()
    {
        ClearWorldSelectionState();
        pointSelectionMode = true;
        Debug.Log($"地点選択モードを有効化しました。pointSelectionMode = {pointSelectionMode}");
    }

    /// <summary>
    /// Enable road selection mode.
    /// </summary>
    public void EnablePathSelectionMode()
    {
        ClearWorldSelectionState();
        pathSelectionMode = true;
        Debug.Log("道路選択モードを有効化しました");
    }

    /// <summary>
    /// Enable parcel selection mode.
    /// </summary>
    public void EnableParcelSelectionMode()
    {
        ClearWorldSelectionState();
        parcelSelectionMode = true;
        Debug.Log("土地区画選択モードを有効化しました");
    }

    /// <summary>
    /// Returns whether point selection mode is active.
    /// </summary>
    public bool IsPointSelectionMode()
    {
        return pointSelectionMode;
    }

    /// <summary>
    /// Returns whether road selection mode is active.
    /// </summary>
    public bool IsPathSelectionMode()
    {
        return pathSelectionMode;
    }

    /// <summary>
    /// Returns whether parcel selection mode is active.
    /// </summary>
    public bool IsParcelSelectionMode()
    {
        return parcelSelectionMode;
    }

    /// <summary>
    /// Whether a point/path/parcel has been selected in the 3D view. Used for cursor HUD prompts.
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
    /// Handles point selection.
    /// If a point is already selected and the distance is short (less than 1m), treat it as a drag start
    /// and do not update the point.
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

        // IPC: notify the UI of point selection for parcel creation mode.
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitEditParcelSelectPoint(point);
        }

        // Disable selection mode (used to detect drag start on the next click).
        pointSelectionMode = false;
    }

    /// <summary>
    /// Handles road selection.
    ///
    /// Project the click onto the path centerline before storing/reporting it. When the UI creation extent is
    /// the clicked point, this anchors which cells will be created,
    /// so the marker appearance must match the actual creation location.
    /// </summary>
    public void OnPathSelected(int pathId, Vector3[] waypoints, Vector3 clickPosition)
    {
        selectedPathId = pathId;
        selectedPathAnchor = ProjectOntoPath(pathId, clickPosition, out selectedPathAnchorTangent);
        EnsureAnchorOverlay();
        Debug.Log($"道路が選択されました: Path_{pathId} anchor={selectedPathAnchor}");

        // IPC: notify the UI of path selection for parcel creation mode.
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitEditParcelSelectPath(pathId, selectedPathAnchor.Value);
        }

        // Disable selection mode.
        pathSelectionMode = false;
    }

    /// <summary>
    /// Projects a point onto the specified path centerline in XZ and returns the direction there.
    /// Returns the clicked position unchanged if the centerline is unavailable
    /// (the backend also projects it, so the resulting creation position remains valid).
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
            float t = PolylineUtils.ProjectOnSegmentXZ(point, a, b);
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
    /// Handles parcel selection.
    /// </summary>
    public void OnParcelSelected(int parcelId)
    {
        selectedParcelId = parcelId;
        Debug.Log($"土地区画が選択されました: Parcel_{parcelId}");

        // IPC: notify the UI of parcel selection for parcel creation mode.
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitEditParcelSelectParcel(parcelId);
        }

        // Disable selection mode.
        parcelSelectionMode = false;
    }

    /// <summary>
    /// Returns whether a reference point has already been selected.
    /// </summary>
    public bool HasSelectedPoint()
    {
        return hasSelectedPoint;
    }

    /// <summary>
    /// Starts changing the angle by left-button dragging.
    /// </summary>
    public void StartRotationDrag()
    {
        if (!hasSelectedPoint) return;
        if (justSelectedPoint)
        {
            // Distinguish point selection clicks from drag starts within the same frame.
            // Do not start angle changes on the initial point-selection click; enable them from the next click.
            justSelectedPoint = false;
            return;
        }
        isRotating = true;
    }

    /// <summary>
    /// Calculates the angle between the mouse and reference point during dragging and draws a temporary rectangle preview.
    /// Calculates locally for real-time feedback without waiting for backend communication.
    /// </summary>
    public void UpdateRotationPreview(Vector3 currentPosition)
    {
        if (!isRotating) return;

        Vector3 diff = currentPosition - selectedPoint;
        currentAngle = Mathf.Atan2(diff.x, diff.z) * Mathf.Rad2Deg;

        // Calculate and draw a temporary rectangle preview (outline using width/depth currently set in the panel).
        List<Vector3> polygon = CalculateRectanglePolygon(selectedPoint, currentWidth, currentHeight, currentAngle);
        previewBlockOutline = null;
        previewParcels = new List<List<Vector3>> { polygon };

        if (parcelPlanRenderer != null)
        {
            parcelPlanRenderer.RenderBlockPreview(null, previewParcels);
        }
    }

    /// <summary>
    /// Ends the angle drag and reports the final angle to the UI.
    /// The UI then fetches an official preview from the backend and sends it to Unity.
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
    /// Calculates rotated rectangle vertices from the center, dimensions, and angle.
    /// 0° points toward Z+ (forward), with positive angles counterclockwise (X+ is +90°).
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
    /// Draws an empty parcel preview.
    /// For empty_area, the source and new parcels are identical, so
    /// set blockPolygon to null to draw only the new parcel (blue).
    /// </summary>
    public void RenderEmptyParcelPreview(float[][] polygonPoints)
    {
        ClearPreview();

        // Convert vertex coordinates to Vector3.
        List<Vector3> parcelPolygon = ConvertFloatArrayToVector3List(polygonPoints);

        // For empty parcel creation, the source and new parcels are identical, so
        // set blockPolygon to null and draw only the new parcel in blue.
        previewBlockOutline = null;

        // Draw as parcels.
        List<List<Vector3>> parcels = new List<List<Vector3>>();
        parcels.Add(parcelPolygon);
        previewParcels = parcels;

        // Draw with the renderer.
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
    /// Clear the preview display.
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
    /// Helper converting server [ [x,z], [x,z], ... ] data to a List of Vector3
    /// (y comes from terrain).
    /// </summary>
    private List<Vector3> ConvertFloatArrayToVector3List(float[][] coords)
    {
        List<Vector3> list = new List<Vector3>();
        foreach (var c in coords)
        {
            float x = c[0];
            float z = c[1];

            // Get terrain height.
            float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z));

            list.Add(new Vector3(x, y, z));
        }
        return list;
    }

    #endregion
}
