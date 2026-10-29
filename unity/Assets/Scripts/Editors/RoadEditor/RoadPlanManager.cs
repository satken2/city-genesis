using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using GeneratedModels;

public enum RoadEditorPhase { Inactive, Placing, Editing }

/// <summary>
/// RoadPlanManager:
/// Central road editor manager, coordinating placement, shape editing, previews, and UI synchronization.
///
/// Workflow:
///   1. Select a variant in the UI panel → notify the graphics program through IPC.
///   2. Placing phase: left click to place the start/waypoints.
///   3. Right click to finalize the shape → enter Editing.
///   4. Editing phase: drag points/controls, right click to delete, click edges to insert.
///   5. Confirm button submits to the backend.
///
/// Curved paths use cubic Beziers; straight paths use segments between anchors.
/// Each point has control_in / control_out automatically computed from a Catmull-Rom spline,
/// manually adjustable by dragging in Editing.
/// Same structure as backend waypoints[].control_in / control_out.
/// </summary>
public partial class RoadPlanManager : MonoBehaviour
{
    [Header("References")]
    public PathManager pathManager;
    public Camera gameCamera;
    public Material previewMaterial;
    public RoadRenderer roadRenderer;

    [Header("Snapping")]
    // Snap radius for road/rail construction (m). Used for node/path snapping (passed when delegating
    // to TerrainInputHandler), right-angle snapping, straight extensions, and existing path extension detection.
    public float snapThreshold = 5.0f;

    [Header("Road Settings")]
    public int PathType = Constants.PATH_MOTORWAY;
    public int PathSubtype = 0;
    public bool Curved = false;

    /// <summary>
    /// Platform side for station paths (Constants.PLATFORM_SIDE_*).
    /// The UI panel toggle reports it via `edit_path:change_platform_side`,
    /// applying it to the preview mesh (TrackPath through RoadRenderer).
    /// Can only be specified during construction; ignored outside station variants.
    /// </summary>
    public int PlatformSide = Constants.PLATFORM_SIDE_OUTSIDE;
    public string HouseEnds = "both";
    public string PassageMode = "elevated";

    /// <summary>
    /// Individual terrain-following override (Constants.VALUE_NONE=unset [variant default]/0=off/1=on).
    /// UI panel toggle (edit_path:set_follow_terrain) is authoritative. Overridable only for
    /// height_editable=true variants. Disabling following previews sections entering hills as cuttings
    /// or tunnels automatically.
    /// </summary>
    public int PathFollowTerrainOverride = Constants.VALUE_NONE;

    /// <summary>
    /// Lateral offset of each line for compound path (parallel track) previews (m; right of construction direction is positive).
    /// Empty means preview one centerline as a single path.
    /// The UI (Tauri) expands parallel tracks on confirmation; Unity uses these only for previews.
    /// Initial values arrive in `lateral_offsets` params of `world:change_state` (STATE_EDIT_PATH);
    /// changes to track count during construction arrive incrementally through `edit_path:change_lateral_offsets`.
    /// </summary>
    public float[] LateralOffsets = System.Array.Empty<float>();

    /// <summary>
    /// Direction of each line for compound path (parallel track) previews (Constants.DIR_*), indexed like LateralOffsets.
    /// Station variant previews reference running direction when resolving platform sides
    /// (TrackPath.ResolvePlatformExtents), so retain this paired with offsets.
    /// Lines without a corresponding entry use DIR_BOTH (bidirectional).
    /// </summary>
    public int[] TravelDirections = System.Array.Empty<int>();

    /// <summary>
    /// Existing path ID being edited. 0 means new creation.
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

    // State
    private RoadEditorPhase phase = RoadEditorPhase.Inactive;

    // Construction anchors and controls (absolute coordinates)
    private List<PathBuildPoint> points = new List<PathBuildPoint>();

    // Selection/hover state
    private int selectedPointIndex = -1;
    private int hoveredEdgeIndex = -1;
    private Vector3 hoverWorldPosition;

    // Drag state
    private bool isDragging = false;
    private int dragPointIndex = -1;

    /// <summary>Drag target type: distinguishes points from controls (in/out).</summary>
    private enum DragTarget { None, Point, ControlIn, ControlOut }
    private DragTarget dragTarget = DragTarget.None;

    /// <summary>
    /// Axis used when dragging a selected node. XZ=moving along ground; Y=changing height above ground.
    /// Clicking an unselected node selects XZ; each click on an already selected node toggles it
    /// (fixed to XZ for variants without elevation editing). Deselecting resets to XZ.
    /// </summary>
    private enum AnchorDragAxis { XZ, Y }
    private AnchorDragAxis anchorDragAxis = AnchorDragAxis.XZ;

    // Height drag references: record the Y on the plane under the cursor at drag start and the node's relative height,
    // then move by differences. Node circles are drawn on the ground, so following absolute values
    // would drop an elevated node to the ground immediately when grabbed.
    private Vector3 verticalDragAnchor;
    private float verticalDragStartPlaneY;
    private float verticalDragStartRelY;

    // Minimum squared horizontal camera-forward length for using a camera-facing vertical plane in height dragging.
    // Below this (nearly top-down), ray and plane are almost parallel; switch to vertical screen pixel conversion.
    private const float VERTICAL_DRAG_MIN_PLANE_NORMAL_SQR = 0.04f;

    // Subcomponents
    private OverlayMeshRenderer overlayRenderer;
    private GameObject overlayMeshObj;
    private RoadOverlayHitDetector hitDetector;

    // TerrainInputHandler reference (shared snapping)
    private TerrainInputHandler terrainInputHandler;
    // Reference for city bounds (map dimensions) in sea/lake crossing checks. Resolved on first check.
    private TerrainManager terrainManager;

    /// <summary>
    /// Current elevation from UI (relative height).
    /// Added to ground height when placing new nodes.
    /// </summary>
    private float currentHeight = 0f;

    /// <summary>
    /// Current embankment offset from UI (m), -5 to +5.
    /// Sets each new point's embankmentHeight.
    /// Road Y itself remains 0; Dynamic terrain deforms by this height at construction start.
    /// Negative values allow cuttings. Available for every road type (even streets that cannot be elevated).
    /// </summary>
    private float currentEmbankmentHeight = 0f;

    // Sound effect playback
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
        junctionLabels.Clear();
    }

    public bool IsEditMode => phase == RoadEditorPhase.Editing;
    public bool CanEditControls => IsEditMode && Curved && points.Count >= 2;

    /// <summary>Whether a construction session is active (placing/editing). Controls radial menu/HUD visibility.</summary>
    public bool IsSessionActive => phase != RoadEditorPhase.Inactive;

    /// <summary>
    /// Current HUD elevation (relative height). Returns the selected node's value,
    /// or currentHeight for the next node when none is selected (including during placement).
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
    /// Current HUD embankment offset (m). Returns the selected node's value,
    /// or currentEmbankmentHeight for the next node when none is selected (including during placement).
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

    /// <summary>Whether a node is selected in Editing (condition for the cursor HUD movement-axis arrow).</summary>
    public bool HasSelectedNode => phase == RoadEditorPhase.Editing && selectedPointIndex >= 0 && selectedPointIndex < points.Count;

    /// <summary>Whether the selected node drag uses height (Y). false without a selection.</summary>
    public bool IsVerticalDragMode => HasSelectedNode && anchorDragAxis == AnchorDragAxis.Y;

    /// <summary>
    /// Checks master data for curve support on the current PathSubtype.
    /// Only variants with explicit curve_allowed=false (e.g. power lines) disallow curves. Undefined permits them.
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
            // Allow when absent from master data.
        }
        return true;
    }

    /// <summary>
    /// Checks master data to determine whether the current variant is a station building path (type=PATH_STATION_HOUSE).
    /// Falls back to PathType received in STATE_EDIT_PATH if master data is unavailable.
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
                // Use PathType when absent from master data.
            }
        }
        return PathType == Constants.PATH_STATION_HOUSE;
    }

    /// <summary>
    /// Whether to suppress snapping to existing features (nodes/paths/straight extensions/right angles/parallel and end alignment).
    /// Station building paths bridge platforms without connecting to or creating nodes where they cross tracks/roads.
    /// Snapping would place endpoints on tracks and prevent the building from clearing them, so disable it entirely.
    /// Grid snapping remains enabled because it aligns to a grid the player explicitly displayed.
    /// </summary>
    public bool SnappingSuppressed => IsStationHouse();

    // =====================================================================
    // Lifecycle
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

        // Keep the construction settings driver (Ctrl radial menu/cursor HUD) attached.
        // RoadEditControlMenu detects session start/end by polling IsSessionActive.
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

        // Draw overlays in LateUpdate, after InputHandler.Update,
        // so point updates from ProcessMouseDrag are always reflected.
        UpdateOverlay();
    }

    // =====================================================================
    // Canvas / Overlay setup
    // =====================================================================

    private void EnsureOverlayRenderer()
    {
        // Draw edit gizmos (nodes, Bezier controls, guides) and existing path edges/nodes
        // without depth testing so terrain relief cannot hide handles behind the ground.
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
    // Public API: mode transitions
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
        // Reset elevation when switching to a variant without elevation editing (e.g. streets).
        // Avoid retaining elevation from the previous variant (e.g. highways).
        // Also clear the terrain-following override, which has no meaning for variants without elevation editing.
        if (!IsHeightEditable())
        {
            currentHeight = 0f;
            PathFollowTerrainOverride = Constants.VALUE_NONE;
        }
        bgDirty = true;
    }
    public void SetPathId(int id) => pathId = id;

    /// <summary>
    /// Sets station platform side and immediately updates the preview mesh.
    /// Called by the UI panel platform side toggle (edit_path:change_platform_side).
    /// Changes during a construction session (placing/editing) immediately
    /// update the side where platforms extend.
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
    /// Sets lateral offsets/directions for compound path (parallel track) previews and immediately updates
    /// preview meshes. Called on panel track count/direction changes (edit_path:change_lateral_offsets).
    /// Changes during a construction session (placing/editing) immediately update track count,
    /// platform sides, and direction arrows in the parallel preview.
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
    /// Resolves effective terrain following for the path being laid. Individual override (0/1) takes priority;
    /// use the variant default only when unset (VALUE_NONE).
    /// Matches backend geo_path_depth.path_follows_terrain / PathManager.GetPathFollowTerrain.
    /// </summary>
    public bool EffectiveFollowTerrain()
    {
        if (PathFollowTerrainOverride == 1) return true;
        if (PathFollowTerrainOverride == 0) return false;
        return PathManager.GetPathFollowTerrain(PathSubtype);
    }

    /// <summary>
    /// Sets the terrain-following override from the UI panel toggle (edit_path:set_follow_terrain).
    /// Ignore overrides and reset to unset for height_editable=false variants.
    /// Rebuild preview samples/tunnel visualization afterward and echo to the UI for confirmation submission.
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
        // Reject curves for curve_allowed=false variants (e.g. power lines).
        Curved = curved && IsCurveAllowed();
        // Curve/straight switching also changes endpoint alignment, so discard manual adjustments and rebuild all controls.
        RecomputeAllControlPoints(resetManual: true);
        RefreshHoverSnapping();
        CommitShapeChange();
    }

    /// <summary>
    /// Initializes road construction mode.
    /// If existingPathId is supplied, enter existing path editing
    /// and fetch the backend geometry into points.
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
        gridLayoutLines = null;
        selectedPointIndex = -1;
        anchorDragAxis = AnchorDragAxis.XZ;
        hoveredEdgeIndex = -1;
        isDragging = false;
        dragTarget = DragTarget.None;
        // For continued placement, the UI supplies construction settings. Use defaults only for a new start without settings.
        currentHeight = Mathf.Max(IsHeightEditable() ? initialHeight ?? 0f : 0f, GetMinHeightFromMaster());
        currentEmbankmentHeight = Mathf.Clamp(initialEmbankment ?? 0f, -5f, 5f);
        // Existing path settings are replaced with saved values by the subsequent LoadExistingPath.
        PathFollowTerrainOverride = IsHeightEditable() ? initialFollowTerrain ?? Constants.VALUE_NONE : Constants.VALUE_NONE;
        Curved = IsCurveAllowed() && (initialCurved ?? true);
        ApplyTutorialPathSettings();
        // Straight-extension snapping always starts OFF, regardless of curved/straight or road/track/infrastructure type.
        // (do not default to pulling new construction toward existing path extensions).
        // Continued placement (UI supplies construction settings) retains manual snap settings from the session.
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
            // Unity owns curved/straight mode, so report the initial is_curved even with no points
            // to the UI panel (shape preview, confirmation submission, and cost estimate).
            // Existing path editing reports it when LoadExistingPath completes.
            EmitShapeUpdate();
        }
    }

    /// <summary>
    /// Fetches existing path geometry from the backend and expands it into points.
    /// Then enters Editing, updates the 3D preview, and notifies the UI.
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
                // Restore the individual terrain-following override (meaningless for height_editable=false variants).
                PathFollowTerrainOverride = response.GetFollowTerrain();

                // Add start_point as the start.
                // Backend coordinates are [x, relativeY, z];
                // the correct Unity Y is terrain height + relativeY.
                var startList = GeneratedModelUtils.ToFloatList(response.start_point);
                if (startList != null && startList.Count >= 2)
                {
                    float startRelY = startList.Count > 2 ? startList[1] : 0f;
                    float startGroundY = Utils.GetGroundHeight(new Vector3(startList[0], 0f, startList[startList.Count - 1]));
                    Vector3 startPos = new Vector3(startList[0], startGroundY + startRelY, startList[startList.Count - 1]);
                    points.Add(new PathBuildPoint(startPos, PathPointRole.Start));
                }

                // Add waypoints.
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

                // Add end_point as the end.
                var endList = GeneratedModelUtils.ToFloatList(response.end_point);
                if (endList != null && endList.Count >= 2)
                {
                    float endRelY = endList.Count > 2 ? endList[1] : 0f;
                    float endGroundY = Utils.GetGroundHeight(new Vector3(endList[0], 0f, endList[endList.Count - 1]));
                    Vector3 endPos = new Vector3(endList[0], endGroundY + endRelY, endList[endList.Count - 1]);
                    var endPt = new PathBuildPoint(endPos, PathPointRole.End);
                    points.Add(endPt);
                }

                // Restore endpoint controls (start_control / end_control).
                // Saved in the same [x, relative height, z] format as waypoint controls.
                // Before schema regeneration, these response fields are absent and null;
                // leave them to subsequent automatic calculation in that case.
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

                // Restore backend embankment offsets to each point.
                // embankment_heights order: [start, waypoints..., end]
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

                    // Preserve backend-saved controls (curves adjusted by user dragging)
                    // before automatic calculation, then restore only those with saved values.
                    // Controls without saved values (e.g. endpoints before schema regeneration) retain automatic values.
                    // Saved values also include automatic results (aligned controls are explicitly submitted on confirmation), so
                    // treat controls matching automatic values as unadjusted, allowing recalculation on later vertex moves.
                    // Retain as manual only controls whose XZ differs. For controls whose heights deviate from the grade rule
                    // (e.g. route proposal results), fix that anchor's control heights to reproduce saved values.
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
                            // Fixed heights affect only manual controls, so make both sides manual to avoid one reverting to automatic and kinking the profile.
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
        gridLayoutLines = null;
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
    // Public API: input handlers (called by TerrainInputHandler)
    // =====================================================================

    /// <summary>
    /// Terrain left click.
    /// Placing: add a start/waypoint.
    /// Editing: start dragging a control/point or insert a waypoint on an edge.
    /// </summary>
    public void OnTerrainLeftClick(Vector3 worldPos)
    {
        if (!TutorialRuntime.AllowRoadClick(worldPos, points.Count, false, PathSubtype, phase == RoadEditorPhase.Placing)) return;
        if (IsGridBuild)
        {
            OnGridLeftClick(worldPos);
            return;
        }
        var freezeProbe = System.Diagnostics.Stopwatch.StartNew(); // [Freeze investigation] Remove after investigation.
        if (phase == RoadEditorPhase.Placing)
        {
            Vector3 snapped = ApplySnapping(worldPos);
            // Inherit elevation when snapping to an elevated node/path (ResolvePlacementHeight also
            // synchronizes currentHeight and the UI elevation display). Otherwise use currentHeight.
            snapped.y = PlacementSurfaceY(snapped) + ResolvePlacementHeight();

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
            ConstrainJunctionPlaneHeights();
            if (Curved) RecomputeAllControlPoints();
            CommitShapeChange();
            PlaySound(placePointSound);
        }
        else if (phase == RoadEditorPhase.Editing)
        {
            HandleEditingClick(worldPos);
        }
        Debug.Log($"[フリーズ調査] 左クリック処理 {freezeProbe.Elapsed.TotalMilliseconds:F1}ms (点数={points.Count}, 曲線={Curved})"); // Remove after investigation.
    }

    /// <summary>
    /// Terrain right click.
    /// Placing: finalize the shape and enter Editing.
    /// Editing: delete a point.
    /// </summary>
    public void OnTerrainRightClick(Vector3 worldPos)
    {
        if (!TutorialRuntime.AllowRoadClick(worldPos, points.Count, true, PathSubtype, phase == RoadEditorPhase.Placing)) return;
        if (IsGridBuild) return;
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
    /// Terrain hover. Used to display preview lines.
    /// </summary>
    public void OnTerrainHover(Vector3 worldPos)
    {
        if (phase == RoadEditorPhase.Placing)
        {
            UpdateHoverSnapping(worldPos);
            if (IsGridBuild && points.Count == 2) hoverWorldPosition = ConstrainGridCorner(hoverWorldPosition);
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
    /// Drag notification from InputHandler. Moves a point/control in Editing.
    ///
    /// InputHandler calculates worldPos as the camera ray intersection with terrain (or the y=0 plane),
    /// giving the precise ground position under the cursor at any viewing angle.
    /// Use it directly here without fixed-depth ScreenToWorldPoint conversion.
    /// </summary>
    public void ProcessMouseDrag(Vector3 worldPos)
    {
        if (TutorialRuntime.PathRestricted || TutorialRuntime.BlocksWorldInput) return;
        if (phase != RoadEditorPhase.Editing || IsGridBuild) return;

        // If dragging has not started, determine the drag target at the current mouse position.
        // InputHandler distinguishes mouse-down from dragging, so
        // HandleEditingClick may not have been called when dragging is confirmed.
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
                // Do nothing without a drag target.
                return;
            }
        }

        // InputHandler coordinates have Y=0, so align to actual ground height.
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

            // Also apply snapping when moving points (grid/node/path/right-angle snapping).
            Vector3 snappedPos = ApplySnapping(newWorldPos);

            var pt = points[dragPointIndex];
            // Preserve relative height during XZ movement.
            float relY = lastSnapWasElevated && IsHeightEditable()
                ? lastSnapRelHeight : pt.position.y - PlacementSurfaceY(pt.position);
            snappedPos.y = PlacementSurfaceY(snappedPos) + relY;

            // Make manual controls follow the anchor and recalculate only unadjusted controls.
            MoveAnchorPosition(dragPointIndex, snappedPos);
        }
        else if (dragTarget == DragTarget.ControlIn || dragTarget == DragTarget.ControlOut)
        {
            if (dragPointIndex < 0 || dragPointIndex >= points.Count) return;
            // Do not use terrain beneath the control; its height comes from the anchor's longitudinal grade.
            MoveControlXZ(dragPointIndex, dragTarget == DragTarget.ControlIn, worldPos.x, worldPos.z);
        }

        UpdateShapePreview();
    }

    /// <summary>
    /// Records height (Y) drag references: the node's current position and cursor-indicated height at start.
    /// Subsequent UpdateVerticalDrag moves node elevation by differences from these references.
    /// </summary>
    private void BeginVerticalDrag(Camera cam)
    {
        var pt = points[dragPointIndex];
        verticalDragAnchor = pt.position;
        verticalDragStartRelY = pt.position.y - Utils.GetGroundHeight(pt.position);
        verticalDragStartPlaneY = ResolveVerticalDragY(cam, verticalDragAnchor);
    }

    /// <summary>
    /// One height (Y) drag frame. Adds the cursor-indicated height change to node relative height,
    /// clamps to min_height as keyboard input does, and passes it to MoveAnchorPosition (no XZ movement).
    /// Do not apply XZ snapping, which is unrelated to height.
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
    /// Returns the cursor-indicated height as world Y. Uses the mouse ray intersection with a camera-facing vertical plane
    /// through the reference node, aligning cursor/node vertical movement at oblique angles.
    /// Near top-down views make ray and plane nearly parallel and the intersection unstable, so instead multiply vertical
    /// screen coordinates by length per pixel at node depth (the origin is arbitrary since only differences are used).
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
    /// Drag-end notification from InputHandler.
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
            // Vertex movement also updates selected node information (position/elevation) in the UI panel.
            if (movedAnchor) EmitSelectNode();
        }
    }

    /// <summary>
    /// Tests whether screen coordinates are over an edited point/control.
    /// Used to resolve input priority conflicts with CameraController.
    /// </summary>
    public bool IsMouseOverEditHandle(Vector2 screenPos, Camera cam)
    {
        if (!IsEditMode || IsGridBuild || cam == null) return false;
        RoadOverlayHit hit = hitDetector.TestHit(screenPos, points, cam, CanEditControls);
        return hit.type == HitTargetType.Point || hit.type == HitTargetType.ControlIn || hit.type == HitTargetType.ControlOut;
    }

    // =====================================================================
    // Shape finalization (Placing → Editing)
    // =====================================================================

    /// <summary>
    /// Ends placement and finalizes the shape.
    /// If waypoints exist, promotes the last one to the endpoint.
    /// Without waypoints, uses the right-click position as the endpoint.
    /// Automatically computes all controls after finalization.
    /// </summary>
    private void FinalizePath(Vector3 rightClickPos)
    {
        if (points.Count == 0) return;

        if (points.Count == 1)
        {
            Vector3 endPos = ApplySnapping(rightClickPos);
            // Inherit elevation when snapping to an elevated node/path.
            endPos.y = PlacementSurfaceY(endPos) + ResolvePlacementHeight();
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

        ConstrainJunctionPlaneHeights();
        RecomputeAllControlPoints();
        // Send finalized=true to prompt the UI to query authoritative backend interference validation.
        CommitShapeChange(finalized: true);
        PlaySound(finalizeShapeSound);
        Debug.Log($"RoadPlanManager: 形状確定 ({points.Count}点) → Editingフェーズ");
    }

    // =====================================================================
    // Editing phase input
    // =====================================================================

    private void HandleEditingClick(Vector3 worldPos)
    {
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        Vector2 mouseScreen = InputUtil.MousePosition;
        RoadOverlayHit hit = hitDetector.TestHit(mouseScreen, points, cam, CanEditControls);

        // Do not set drag state on click.
        // Start dragging by detecting the handle again in ProcessMouseDrag
        // when isDragging == false. This keeps click/drag state
        // consistent with InputHandler's drag threshold detection.
        if (hit.type == HitTargetType.Point)
        {
            if (hit.index == selectedPointIndex)
            {
                // Clicking a selected node toggles movement axes. Variants without elevation editing stay in XZ.
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
    /// Changes node selection. Switching to another node (or none) resets the movement axis to XZ.
    /// Reselecting the same node retains the axis (prevents drag-start redetection from resetting it).
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

        // Ignore right clicks on controls.
        if (hit.type == HitTargetType.ControlIn || hit.type == HitTargetType.ControlOut)
            return;

        if (hit.type == HitTargetType.Point && points.Count > 2)
        {
            RemovePoint(hit.index);
        }
    }

    // =====================================================================
    // Point operations
    // =====================================================================

    /// <summary>
    /// Inserts a waypoint on an edge and recalculates controls.
    /// </summary>
    private void InsertPointOnEdge(int edgeIndex, Vector3 worldPos)
    {
        if (edgeIndex < 0 || edgeIndex >= points.Count - 1) return;

        worldPos.y = PlacementSurfaceY(worldPos) + currentHeight;
        var insertedPt = new PathBuildPoint(worldPos, PathPointRole.Waypoint);
        insertedPt.embankmentHeight = currentEmbankmentHeight;
        points.Insert(edgeIndex + 1, insertedPt);

        if (selectedPointIndex > edgeIndex) selectedPointIndex++;

        ReleaseControlHeights(edgeIndex, edgeIndex + 2);
        ConstrainJunctionPlaneHeights();
        RecomputeAllControlPoints();
        CommitShapeChange();
        Debug.Log($"RoadPlanManager: 辺{edgeIndex}にウェイポイント挿入 (total: {points.Count})");
    }

    /// <summary>
    /// Deletes a point while retaining at least 2 (start + end).
    /// Recalculates controls afterward.
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
        ConstrainJunctionPlaneHeights();
        RecomputeAllControlPoints();
        CommitShapeChange();
        Debug.Log($"RoadPlanManager: ポイント{index}を削除 (残り: {points.Count})");
    }

    // =====================================================================
    // Hover state update
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
    // IPC: node elevation/embankment changes (from panel steppers)
    // =====================================================================

    /// <summary>
    /// Handles node elevation (relative height) changes received via IPC.
    /// nodeIndex=-1 updates currentHeight (elevation for the next placement).
    /// Curved/straight and snapping are owned by Unity's radial menu, but elevation/embankment
    /// can be edited bidirectionally through panel steppers and Unity keys.
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
    /// Handles node embankment offset changes received via IPC.
    /// nodeIndex=-1 updates currentEmbankmentHeight (embankment for the next placement).
    /// Embankment is available for every road type (even streets that cannot be elevated).
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
    // IPC: selected node notification
    // =====================================================================

    /// <summary>
    /// Determines the relative height for a node being placed.
    /// If the last snap was to an elevated node/path/construction endpoint, use its relative height
    /// and synchronize currentHeight/UI elevation (subsequent nodes use the same elevated height).
    /// Use currentHeight unchanged for variants without height editing or non-elevated snaps.
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
    /// Reports currentHeight while placing (no node selected) through edit_path:select_node (node_id=-1).
    /// UI elevation/embankment steppers show these values and synchronize changes made in Unity by keys or snapping.
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
    /// Reports the currently selected node through edit_path:select_node.
    /// Called on node selection and height changes
    /// to synchronize UI elevation/embankment steppers and their target node.
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
    // Key input: node height changes
    // =====================================================================

    /// <summary>
    /// Changes elevation (relative height) using the keyboard.
    /// PageUp / 1 adds +1.0m; PageDown / 2 adds -1.0m.
    /// Changes the selected node's elevation, or currentHeight for the next node
    /// when none is selected (including during placement). Updates the 3D preview and notifies the UI afterward.
    /// </summary>
    private void HandleHeightInput()
    {
        // Ignore keyboard elevation changes for variants without elevation editing (e.g. streets).
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
    /// Changes embankment offset using the keyboard.
    /// 3 adds +1.0m; 4 adds -1.0m.
    /// Changes the selected node's embankment, or currentEmbankmentHeight for the next node
    /// when none is selected (including during placement).
    /// Available for every road type (even streets that cannot be elevated).
    /// Updates the 3D preview and notifies the UI afterward.
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
    // Utilities
    // =====================================================================

    /// <summary>
    /// Plays the specified AudioClip.
    /// Does nothing if the clip or AudioSource is unset.
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

    /// <summary>Control Y is relative to terrain beneath its anchor, matching backend non-following spans.</summary>
    private static List<float> ControlToRelativeList(Vector3? control, Vector3 anchor)
    {
        return RoadShapeGeometry.ControlToRelativeList(control, anchor);
    }

    private static Vector3 RestoreControl(float x, float relativeY, float z, Vector3 anchor)
    {
        return RoadShapeGeometry.RestoreControl(x, relativeY, z, anchor);
    }

    /// <summary>
    /// Reports current point positions to the UI panel via IPC.
    ///
    /// The backend handles relative-height coordinates without accounting for terrain, so
    /// subtract ground height from transmitted Y to send relative height.
    /// The UI passes this relative height unchanged to the backend.
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

        // Intermediate anchor (waypoint) controls. The UI includes these in confirmation waypoints
        // as {position, control_in, control_out}, persisting user-adjusted curves
        // in the backend (missing controls are automatically filled by the backend).
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

        // Send each point's embankment offset.
        var embankmentHeights = new List<float>();
        foreach (var pt in points)
        {
            embankmentHeights.Add(pt.embankmentHeight);
        }

        // Alignment is finalized by full-path /paths/validate, which resolves terrain, saved design elevations, and parallel expansion.
        // Gating on render-only local estimates would prevent authoritative validation and make false rejections unrecoverable.
        // Grid points specify an area; the core expands and validates actual paths, so check only whether point placement is complete.
        bool canConfirm = IsGridBuild
            ? phase == RoadEditorPhase.Editing && points.Count == 3
            : phase == RoadEditorPhase.Editing && points.Count >= 2 && CheckElevationIntersections().Count == 0
                && CheckRiverCrossings().Count == 0 && CheckSeaLakeCrossings().Count == 0 && CheckMaxSpanViolations().Count == 0
                && (!RoadPath.IsRopewayVariant(PathSubtype) || !HasAlignmentViolation)
                && TutorialRuntime.AcceptsPath(points, PathSubtype, Curved);
        IpcClient.Instance.EmitEditPathUpdateShape(pathId, PathType, pointsList, startControl, endControl, embankmentHeights, waypointControls, Curved, PathFollowTerrainOverride, finalized,
            canConfirm, BuildShapeSyncExtras());
    }

}
