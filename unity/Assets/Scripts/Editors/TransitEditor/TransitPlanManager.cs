using System.Collections;
using System.Collections.Generic;
using GeneratedModels;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// TransitPlanManager:
/// Public transport line placement editor. Places stops/waypoints in traversal order on the 3D map.
/// Lines are closed loops, returning from the last point to the first (round trips list outbound/inbound stops in a loop).
///
/// Workflow:
///   Placing: left click appends a stop. Clicking an existing stop adds a revisit to that stop;
///            clicking the first stop (at least 2 stops) or right-clicking ends placement and enters Editing.
///   Editing: drag stops/waypoints, click sections (including last→first) to insert stops, drag sections
///            to insert/move waypoints, right click stops/waypoints to delete.
///
/// Placement constraints:
///   Stops require active paths in the line type's stop_path_variants; waypoints require the vehicle network
///   (route_path_variants ∪ stop_path_variants), with centerline projection distance within path width/2 + STOP_ON_PATH_MARGIN (2m).
///   Matches backend crud_transit._resolve_route_points; invalid operations place no point.
///
/// Route preview:
///   After traversal points change, debounce POST /api/v1/transit/lines/route-preview (a read-only estimate),
///   then use TransitRouteGeometry to convert section travel data (same format as journeys) into points following actual roads/tracks.
///   Draw bands matching TransitOverlayManager line colors; show disconnected sections as straight red dashes.
///   Until the reply matches current points, draw straight connections between them.
///
/// Send traversal point changes to the UI via IPC (edit_transit:update_points). Actual line creation/route updates
/// are performed by the UI (TransitCreationPanel); this manager only builds points, previews, and notifications.
/// Route editing (starting from an existing line's points) asks TransitOverlayManager to hide that line's band.
///
/// IpcStateCoordinator dynamically creates/initializes this on STATE_EDIT_TRANSIT. No scene placement is needed;
/// dependencies (camera, PathManager, overlay material, sound effects) resolve themselves at runtime.
/// </summary>
public class TransitPlanManager : MonoBehaviour
{
    private enum Phase { Inactive, Placing, Editing }

    /// <summary>Line traversal point. Waypoint=false for a stop, true for a mandatory pass-through without stopping.</summary>
    public readonly struct RoutePoint
    {
        public readonly Vector3 Position;
        public readonly bool Waypoint;
        public RoutePoint(Vector3 position, bool waypoint) { Position = position; Waypoint = waypoint; }
    }

    /// <summary>POST /transit/lines/route-preview response. legs[k] runs from stop k to the next stop (null if disconnected).</summary>
    public sealed class RoutePreview
    {
        public bool ok;
        public string reason;
        public List<int> blocked_legs;
        public List<TransitJourney> legs;
    }

    // Margin in allowed centerline distance when judging whether stops/waypoints lie on underlying paths (m).
    // Match backend transit_route.STOP_ON_PATH_MARGIN.
    private const float STOP_ON_PATH_MARGIN = 2f;

    // Snap search radius (m). Underlying path segments inside it are candidates.
    private const float SNAP_SEARCH_RADIUS = 40f;

    // Overlay drawing parameters (pixels).
    private const float STOP_MARKER_HALF_SIZE = 6f;
    private const float WAYPOINT_MARKER_RADIUS = 5f;
    private const float POINT_HIT_RADIUS = 12f;
    private const float EDGE_HIT_PIXELS = 8f;
    private const float LINE_THICKNESS = 2.5f;

    // Delay after traversal point changes settle before fetching a route preview (seconds). Avoid estimates every drag frame.
    private const float PREVIEW_DEBOUNCE_SECONDS = .25f;
    // Route preview sample spacing (travel distance unit), matching TransitOverlayManager line bands.
    private const float PREVIEW_SAMPLE_STEP = 2f;

    private const string PLACE_SOUND = "Audio/Effects/transit-point-place";

    // Gizmo colors during line placement.
    private static readonly Color LineColor = new Color(0.2f, 0.85f, 1f, 0.95f);
    private static readonly Color StopColor = new Color(1f, 1f, 1f, 1f);
    private static readonly Color FirstStopColor = new Color(0.45f, 0.95f, 0.55f, 1f);
    private static readonly Color WaypointColor = new Color(0.2f, 0.85f, 1f, 1f);
    private static readonly Color SelectedColor = new Color(1f, 0.85f, 0.2f, 1f);
    private static readonly Color PreviewColor = new Color(0.2f, 0.85f, 1f, 0.5f);
    private static readonly Color BlockedColor = new Color(1f, 0.35f, 0.35f, 0.95f);
    private static readonly Color RibbonBorderColor = new Color(.04f, .05f, .07f, 1f);

    private Phase phase = Phase.Inactive;

    // Selected line type (section path variant = transit_master transit_path_id).
    private int pathVariant = 0;
    // Underlying path variants for stops and vehicle network variants for waypoints.
    private readonly HashSet<int> stopPathVariants = new HashSet<int>();
    private readonly HashSet<int> routePathVariants = new HashSet<int>();
    // Line whose route is being edited (-1=new line).
    private int lineAgentId = -1;

    // Traversal points (world coordinates including underlying path heights).
    private readonly List<RoutePoint> points = new List<RoutePoint>();

    private int selectedIndex = -1;
    private bool isDragging = false;
    private int dragIndex = -1;
    // Screen position on left-button down. Terrain drags send ProcessMouseDrag without StartDrag,
    // so the first update grabs at this initial position (the position after crossing the drag threshold may miss).
    private Vector2 pressScreen;
    private bool pressPending = false;
    // Cancel a waypoint inserted by dragging a section if released without ever snapping to an underlying path.
    private bool dragInsertedWaypoint = false;
    private bool dragSnapped = false;

    // Hovered stop preview (valid only when snapped to an underlying path).
    private bool hoverValid = false;
    private Vector3 hoverPos;

    private Camera gameCamera;
    private PathManager pathManager;
    private TransitRouteGeometry geometry;

    private OverlayMeshRenderer overlayRenderer, ribbonBorder, ribbon;

    // Route preview fetch state: desired=current point request, sent=submitted request, shown=request of the displayed reply.
    private string previewDesired, previewSent, previewShown;
    private object previewPayload;
    private float previewChangedAt;
    private bool previewBusy;
    private readonly List<(Vector3 a, Vector3 b)> previewSegments = new List<(Vector3, Vector3)>();
    private readonly HashSet<int> blockedLegs = new HashSet<int>();

    private AudioSource audioSource;
    private AudioClip placeSound;
    private IpcClient ipc;
    private float soundVolume = 1f;

    public bool IsEditingPhase => phase == Phase.Editing;

    // =====================================================================
    // Lifecycle
    // =====================================================================

    private void Awake()
    {
        gameCamera = Camera.main;
        pathManager = FindAnyObjectByType<PathManager>();
        EnsureRenderers();
        // Editor GetComponent returns Unity fake null for missing components, which ?? does not recognize; use ==.
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        placeSound = Resources.Load<AudioClip>(PLACE_SOUND);
    }

    private void Update()
    {
        if (phase == Phase.Editing && InputUtil.LeftMouseWasPressedThisFrame)
        {
            pressScreen = InputUtil.MousePosition;
            pressPending = true;
        }
        // Sound effect volume follows UI settings (master × effects). Check each frame because IpcClient can be replaced later.
        if (ipc != IpcClient.Instance)
        {
            if (ipc != null) ipc.OnAmbienceVolume -= SetVolume;
            ipc = IpcClient.Instance;
            if (ipc != null) ipc.OnAmbienceVolume += SetVolume;
        }
        if (phase != Phase.Inactive && !previewBusy && previewDesired != null && previewDesired != previewSent
            && Time.unscaledTime - previewChangedAt >= PREVIEW_DEBOUNCE_SECONDS)
        {
            StartCoroutine(FetchPreview(previewDesired, previewPayload));
        }
    }

    private void LateUpdate()
    {
        if (phase == Phase.Inactive) return;
        UpdateOverlay();
    }

    private void OnDestroy()
    {
        if (ipc != null) ipc.OnAmbienceVolume -= SetVolume;
    }

    private void SetVolume(float value)
    {
        soundVolume = float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp01(value);
    }

    private void EnsureRenderers()
    {
        // Always draw traversal markers/lines in front of terrain (same as RoadPlanManager gizmos).
        if (overlayRenderer == null) overlayRenderer = OverlayMeshRenderer.Create("TransitEditorOverlayMesh", alwaysOnTop: true);
        // Route preview bands use the same drawing method as line bands (TransitOverlayManager 3200/3201), layered in front.
        if (ribbonBorder == null) ribbonBorder = CreateRibbonRenderer("TransitEditorRouteOutline", 3202);
        if (ribbon == null) ribbon = CreateRibbonRenderer("TransitEditorRouteRibbon", 3203);
    }

    private OverlayMeshRenderer CreateRibbonRenderer(string name, int queue)
    {
        var renderer = OverlayMeshRenderer.Create(name, alwaysOnTop: true, parent: transform);
        renderer.Material.renderQueue = queue;
        return renderer;
    }

    // =====================================================================
    // Public API: mode transitions
    // =====================================================================

    /// <summary>
    /// Initializes line placement mode.
    /// pathVariant is the section path variant (=transit_master transit_path_id), stopVariants lists stop paths,
    /// and routeVariants lists waypoint-capable vehicle network variants. If initialPoints is supplied (route editing),
    /// start in Editing with those points and hide the band for editedLineId.
    /// </summary>
    public void InitializeTransitPlacement(int pathVariant, int[] stopVariants, int[] routeVariants,
        List<RoutePoint> initialPoints, int editedLineId)
    {
        EnsureRenderers();
        if (gameCamera == null) gameCamera = Camera.main;
        if (pathManager == null) pathManager = FindAnyObjectByType<PathManager>();

        this.pathVariant = pathVariant;
        stopPathVariants.Clear();
        routePathVariants.Clear();
        if (stopVariants != null) foreach (int v in stopVariants) { stopPathVariants.Add(v); routePathVariants.Add(v); }
        if (routeVariants != null) foreach (int v in routeVariants) routePathVariants.Add(v);

        points.Clear();
        if (initialPoints != null) points.AddRange(initialPoints);
        phase = points.Count > 0 ? Phase.Editing : Phase.Placing;
        selectedIndex = -1;
        isDragging = false;
        dragIndex = -1;
        hoverValid = false;
        ResetPreview();

        lineAgentId = editedLineId;
        if (TransitOverlayManager.Instance != null) TransitOverlayManager.Instance.HiddenLineId = editedLineId;

        PointsChanged();
        Debug.Log($"TransitPlanManager: 路線設置モードを初期化 path_variant={pathVariant}, stop_variants={stopPathVariants.Count}, points={points.Count}, line={editedLineId}");
    }

    /// <summary>
    /// Ends line placement mode, clearing state and overlays.
    /// </summary>
    public void FinishPlacement()
    {
        phase = Phase.Inactive;
        points.Clear();
        selectedIndex = -1;
        isDragging = false;
        dragIndex = -1;
        hoverValid = false;
        ResetPreview();
        if (lineAgentId >= 0 && TransitOverlayManager.Instance != null) TransitOverlayManager.Instance.HiddenLineId = -1;
        lineAgentId = -1;
        if (overlayRenderer != null) overlayRenderer.ClearMesh();
        if (ribbonBorder != null) ribbonBorder.ClearMesh();
        if (ribbon != null) ribbon.ClearMesh();
    }

    // =====================================================================
    // Public API: input handlers (called by TerrainInputHandler)
    // =====================================================================

    /// <summary>
    /// Terrain left click.
    /// Placing: append a stop (revisit an existing stop, or finish when clicking the first).
    /// Editing: select a point, or click a section to insert a stop.
    /// </summary>
    public void OnTerrainLeftClick(Vector3 worldPos)
    {
        if (phase == Phase.Placing)
        {
            int hit = HitTestPoint(InputUtil.MousePosition);
            if (hit >= 0 && !points[hit].Waypoint)
            {
                if (hit == FirstStopIndex() && StopCount() >= 2)
                {
                    // Clicking the first stop closes the loop and ends placement (the line returns from its last stop to its first).
                    phase = Phase.Editing;
                    selectedIndex = -1;
                    return;
                }
                if (hit != LastStopIndex())
                {
                    AddPoint(points.Count, new RoutePoint(points[hit].Position, false));
                }
                return;
            }
            if (TrySnap(worldPos, stopPathVariants, out Vector3 snapped))
            {
                AddPoint(points.Count, new RoutePoint(snapped, false));
            }
            // Do not place off underlying paths (matches backend placement constraints).
        }
        else if (phase == Phase.Editing)
        {
            int hit = HitTestPoint(InputUtil.MousePosition);
            if (hit >= 0)
            {
                selectedIndex = hit;
                return;
            }

            int edgeIdx = HitTestEdge(InputUtil.MousePosition);
            if (edgeIdx >= 0 && TrySnap(worldPos, stopPathVariants, out Vector3 snapped))
            {
                AddPoint(edgeIdx + 1, new RoutePoint(snapped, false));
                selectedIndex = edgeIdx + 1;
                return;
            }

            selectedIndex = -1;
        }
    }

    /// <summary>
    /// Terrain right click.
    /// Placing: end placement and enter Editing.
    /// Editing: delete stops/waypoints.
    /// </summary>
    public void OnTerrainRightClick(Vector3 worldPos)
    {
        if (phase == Phase.Placing)
        {
            if (points.Count >= 1)
            {
                phase = Phase.Editing;
                selectedIndex = -1;
            }
        }
        else if (phase == Phase.Editing)
        {
            int hit = HitTestPoint(InputUtil.MousePosition);
            if (hit >= 0)
            {
                points.RemoveAt(hit);
                if (selectedIndex == hit) selectedIndex = -1;
                else if (selectedIndex > hit) selectedIndex--;
                PointsChanged();
            }
        }
    }

    /// <summary>
    /// Terrain hover. Previews the next stop position during Placing.
    /// </summary>
    public void OnTerrainHover(Vector3 worldPos)
    {
        if (phase != Phase.Placing)
        {
            hoverValid = false;
            return;
        }
        hoverValid = TrySnap(worldPos, stopPathVariants, out hoverPos);
    }

    // =====================================================================
    // Dragging (called by InputHandler)
    // =====================================================================

    /// <summary>
    /// Tests whether screen coordinates are over a movable stop/waypoint or a section where a waypoint can be inserted.
    /// InputHandler uses this to select the drag-start target.
    /// </summary>
    public bool IsMouseOverDraggable(Vector2 screenPos)
    {
        if (phase != Phase.Editing) return false;
        return HitTestPoint(screenPos) >= 0 || HitTestEdge(screenPos) >= 0;
    }

    /// <summary>Starts dragging (called by InputHandler only when the ray misses terrain).</summary>
    public void StartDrag(Vector2 screenPos)
    {
        BeginDrag(screenPos);
    }

    /// <summary>Grabs a point if over one, inserts/grabs a waypoint if over a section, otherwise does nothing.</summary>
    private void BeginDrag(Vector2 screenPos)
    {
        pressPending = false;
        if (phase != Phase.Editing) return;
        int hit = HitTestPoint(screenPos);
        dragInsertedWaypoint = false;
        dragSnapped = false;
        if (hit < 0)
        {
            int edge = HitTestEdge(screenPos);
            if (edge < 0) return;
            // Leave insertion at the section midpoint until the first drag update moves it to a snap target.
            Vector3 a = points[edge].Position, b = points[(edge + 1) % points.Count].Position;
            hit = edge + 1;
            points.Insert(hit, new RoutePoint((a + b) * .5f, true));
            dragInsertedWaypoint = true;
        }
        isDragging = true;
        dragIndex = hit;
        selectedIndex = hit;
    }

    /// <summary>During dragging, snaps/moves the grabbed point to underlying paths valid for its type (grabs at mouse-down if none yet).</summary>
    public void ProcessMouseDrag(Vector3 worldPos)
    {
        if (!isDragging && pressPending) BeginDrag(pressScreen);
        if (!isDragging || dragIndex < 0 || dragIndex >= points.Count) return;
        var point = points[dragIndex];
        if (TrySnap(worldPos, point.Waypoint ? routePathVariants : stopPathVariants, out Vector3 snapped))
        {
            points[dragIndex] = new RoutePoint(snapped, point.Waypoint);
            dragSnapped = true;
            RequestPreview();
        }
    }

    /// <summary>Ends dragging, commits the point, and notifies the UI (cancels new waypoints that could not be placed).</summary>
    public void ProcessMouseDragEnd(Vector3 worldPos)
    {
        pressPending = false;
        if (!isDragging) return;
        isDragging = false;
        if (dragInsertedWaypoint && !dragSnapped && dragIndex >= 0 && dragIndex < points.Count)
        {
            points.RemoveAt(dragIndex);
            selectedIndex = -1;
        }
        else if (dragSnapped)
        {
            PlayPlaceSound();
        }
        dragIndex = -1;
        dragInsertedWaypoint = false;
        PointsChanged();
    }

    // =====================================================================
    // Traversal point operations
    // =====================================================================

    private void AddPoint(int index, RoutePoint point)
    {
        points.Insert(index, point);
        PlayPlaceSound();
        PointsChanged();
    }

    private void PointsChanged()
    {
        EmitPoints();
        RequestPreview();
    }

    private int StopCount()
    {
        int count = 0;
        foreach (var p in points) if (!p.Waypoint) count++;
        return count;
    }

    private int FirstStopIndex()
    {
        for (int i = 0; i < points.Count; i++) if (!points[i].Waypoint) return i;
        return -1;
    }

    private int LastStopIndex()
    {
        for (int i = points.Count - 1; i >= 0; i--) if (!points[i].Waypoint) return i;
        return -1;
    }

    private void PlayPlaceSound()
    {
        if (placeSound == null || audioSource == null) return;
        audioSource.PlayOneShot(placeSound, soundVolume);
    }

    // =====================================================================
    // Underlying path snapping
    // =====================================================================

    /// <summary>
    /// Projects world coordinates onto centerlines of active paths in variants. Snaps to the nearest path
    /// within path width/2 + STOP_ON_PATH_MARGIN, taking height from its centerline.
    /// false if none qualifies.
    /// </summary>
    private bool TrySnap(Vector3 worldPos, HashSet<int> variants, out Vector3 snapped)
    {
        snapped = worldPos;
        if (pathManager == null) return false;

        var segments = pathManager.QueryNearbySegments(worldPos, SNAP_SEARCH_RADIUS);
        if (segments == null || segments.Count == 0) return false;

        float bestDist = float.MaxValue;
        Vector3 bestPoint = worldPos;
        bool found = false;
        Vector2 target = new Vector2(worldPos.x, worldPos.z);

        foreach (var seg in segments)
        {
            if (seg.path == null) continue;
            if (!variants.Contains(seg.path.path_variant)) continue;
            if (seg.path.status != Constants.STATUS_ACTIVE) continue;

            float t = PolylineUtils.ProjectOnSegment(target, new Vector2(seg.a.x, seg.a.z), new Vector2(seg.b.x, seg.b.z));
            Vector3 proj = Vector3.Lerp(seg.a, seg.b, t);
            float dist = Utils.XZDistance(worldPos, proj);

            float width = pathManager.GetRoadWidth(seg.path.path_variant);
            float margin = width * 0.5f + STOP_ON_PATH_MARGIN;

            if (dist <= margin && dist < bestDist)
            {
                bestDist = dist;
                bestPoint = proj;
                found = true;
            }
        }

        if (found)
        {
            bestPoint.y += 0.15f;
            snapped = bestPoint;
        }
        return found;
    }

    // =====================================================================
    // Hit testing (screen coordinates)
    // =====================================================================

    /// <summary>Returns the nearest stop/waypoint index within the screen-space hit radius.</summary>
    private int HitTestPoint(Vector2 screenPos)
    {
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return -1;

        int best = -1;
        float bestDist = POINT_HIT_RADIUS;
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 sp = cam.WorldToScreenPoint(points[i].Position);
            if (sp.z < 0f) continue;
            float d = Vector2.Distance(new Vector2(sp.x, sp.y), screenPos);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// Tests straight sections between traversal points (including last→first) at screen coordinates, returning the section's start index.
    /// -1 if absent. Tests straight connections rather than route preview bands, which become stale while waiting for replies.
    /// </summary>
    private int HitTestEdge(Vector2 screenPos)
    {
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null || points.Count < 2) return -1;

        int best = -1;
        float bestDist = EDGE_HIT_PIXELS;
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 s1 = cam.WorldToScreenPoint(points[i].Position);
            Vector3 s2 = cam.WorldToScreenPoint(points[(i + 1) % points.Count].Position);
            if (s1.z < 0f || s2.z < 0f) continue;
            float d = Mathf.Sqrt(PolylineUtils.DistanceToSegmentSq(screenPos, new Vector2(s1.x, s1.y), new Vector2(s2.x, s2.y)));
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    // =====================================================================
    // IPC emission
    // =====================================================================

    /// <summary>
    /// Reports points through IPC as {kind, position:[x, 0, z]} (public transport paths have relative Y=0, so y is 0).
    /// Backend coordinates are treated like Unity world coordinates; pass x,z unchanged.
    /// </summary>
    private void EmitPoints()
    {
        if (IpcClient.Instance == null) return;
        IpcClient.Instance.EmitEditTransitUpdatePoints(PointsPayload());
    }

    private List<Dictionary<string, object>> PointsPayload()
    {
        var payload = new List<Dictionary<string, object>>(points.Count);
        foreach (var p in points)
        {
            payload.Add(new Dictionary<string, object>
            {
                ["kind"] = p.Waypoint ? "waypoint" : "stop",
                ["position"] = new List<float> { p.Position.x, 0f, p.Position.z },
            });
        }
        return payload;
    }

    // =====================================================================
    // Route preview
    // =====================================================================

    private void ResetPreview()
    {
        previewDesired = previewSent = previewShown = null;
        previewPayload = null;
        previewSegments.Clear();
        blockedLegs.Clear();
    }

    /// <summary>Schedules a route preview for current points. Fetches after changes settle for PREVIEW_DEBOUNCE_SECONDS.</summary>
    private void RequestPreview()
    {
        if (StopCount() < 2)
        {
            ResetPreview();
            return;
        }
        var payload = new Dictionary<string, object>
        {
            ["path_variant"] = pathVariant,
            ["points"] = PointsPayload(),
        };
        string key = JsonConvert.SerializeObject(payload);
        if (key == previewDesired) return;
        previewDesired = key;
        previewPayload = payload;
        previewChangedAt = Time.unscaledTime;
    }

    private IEnumerator FetchPreview(string key, object payload)
    {
        previewBusy = true;
        previewSent = key;
        try
        {
            yield return ApiClient.Post<RoutePreview>("/transit/lines/route-preview", payload, response =>
            {
                // Do not draw replies arriving after traversal points change or editing ends.
                if (previewDesired == key && phase != Phase.Inactive) ApplyPreview(response, key);
            }, (code, message) => Debug.LogWarning($"路線の経路プレビューを取得できませんでした: {code} {message}"));
        }
        finally { previewBusy = false; }
    }

    /// <summary>Converts section travel data to road/track-following segments using the same rules as line bands.</summary>
    private void ApplyPreview(RoutePreview response, string key)
    {
        previewSegments.Clear();
        blockedLegs.Clear();
        previewShown = key;
        if (response == null || pathManager == null) return;
        if (response.blocked_legs != null) blockedLegs.UnionWith(response.blocked_legs);
        if (response.legs == null) return;

        geometry ??= new TransitRouteGeometry(pathManager);
        int vehicleType = VehicleModelConfig.GetTransitVehicleType(pathVariant);
        var routes = new List<TransitRouteGeometry.Route>();
        var routeIds = new HashSet<int>();
        foreach (var leg in response.legs)
        {
            if (leg?.steps == null) continue;
            // Sections are drawn as separate routes, so do not create a loop-seam duplicate (TransitMotionClock.Closed).
            leg.turnback = true;
            var route = new TransitRouteGeometry.Route { motion = new TransitMotionClock(new List<TransitJourney> { leg }), VehicleType = vehicleType };
            TransitRouteGeometry.BuildRuns(route);
            routes.Add(route);
            foreach (var run in route.runs) routeIds.Add(run.path);
        }
        var candidates = new List<PathSummary>();
        foreach (int id in routeIds) { var data = pathManager.GetPathData(id); if (data != null) candidates.Add(data); }
        geometry.Refresh(routeIds, candidates);
        foreach (var route in routes)
        {
            foreach (var run in route.runs)
            {
                if (run.Wrapped || !geometry.HasPath(run.path)) continue;
                int count = Mathf.Max(1, Mathf.CeilToInt((float)(run.end - run.start) / PREVIEW_SAMPLE_STEP));
                bool connected = false; Vector3 previous = default;
                for (int i = 0; i <= count; i++)
                {
                    double distance = run.start + (run.end - run.start) * i / count;
                    bool valid = geometry.Sample(route, distance, distance, run.Forward, out var point, out _);
                    if (valid && connected && (point - previous).sqrMagnitude > .000001f) previewSegments.Add((previous, point));
                    connected = valid; previous = point;
                }
            }
        }
    }

    // =====================================================================
    // Overlay drawing
    // =====================================================================

    private void UpdateOverlay()
    {
        if (overlayRenderer == null) return;
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        overlayRenderer.BeginFrame();
        ribbonBorder.BeginFrame();
        ribbon.BeginFrame();

        bool previewCurrent = previewDesired != null && previewShown == previewDesired;
        if (previewCurrent)
        {
            // Bands following actual roads/tracks, and red dashes for disconnected sections.
            foreach (var (a, b) in previewSegments)
            {
                ribbonBorder.AddSolidLine(a, b, RibbonBorderColor, 12);
                ribbon.AddSolidLine(a, b, LineColor, 8);
            }
            DrawBlockedLegs();
        }
        else
        {
            // While awaiting replies, draw straight point connections. In Editing, include last→first to show the loop.
            int edges = phase == Phase.Editing && points.Count >= 2 ? points.Count : points.Count - 1;
            for (int i = 0; i < edges; i++)
            {
                overlayRenderer.AddSolidLine(points[i].Position, points[(i + 1) % points.Count].Position, LineColor, LINE_THICKNESS);
            }
        }

        // During Placing, preview the last point→hover position and hover→first stop connections.
        if (phase == Phase.Placing && hoverValid)
        {
            if (points.Count > 0)
            {
                overlayRenderer.AddSolidLine(points[points.Count - 1].Position, hoverPos, PreviewColor, LINE_THICKNESS);
                overlayRenderer.AddDashedLine(hoverPos, points[0].Position, PreviewColor, LINE_THICKNESS);
            }
            overlayRenderer.AddSquare(hoverPos, STOP_MARKER_HALF_SIZE, PreviewColor);
        }

        // Traversal point markers: squares for stops (first in green), circles for waypoints.
        int first = FirstStopIndex();
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            Color c = i == selectedIndex ? SelectedColor : p.Waypoint ? WaypointColor : i == first ? FirstStopColor : StopColor;
            if (p.Waypoint) overlayRenderer.AddCircle(p.Position, WAYPOINT_MARKER_RADIUS, c);
            else overlayRenderer.AddSquare(p.Position, STOP_MARKER_HALF_SIZE, c);
        }

        overlayRenderer.EndFrame(cam);
        ribbonBorder.EndFrame(cam);
        ribbon.EndFrame(cam);
    }

    /// <summary>Shows disconnected sections as red dashed connections through their traversal points (stop→waypoints…→next stop).</summary>
    private void DrawBlockedLegs()
    {
        if (blockedLegs.Count == 0) return;
        var stops = new List<int>();
        for (int i = 0; i < points.Count; i++) if (!points[i].Waypoint) stops.Add(i);
        for (int k = 0; k < stops.Count; k++)
        {
            if (!blockedLegs.Contains(k)) continue;
            int from = stops[k], to = stops[(k + 1) % stops.Count];
            int i = from;
            do
            {
                int next = (i + 1) % points.Count;
                overlayRenderer.AddDashedLine(points[i].Position, points[next].Position, BlockedColor, LINE_THICKNESS);
                i = next;
            } while (i != to);
        }
    }
}
