using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using GeneratedModels;

/// <summary>
/// Manager storing named roads and drawing road-name screen labels in the 3D view.
/// Roads merely name their sections (paths), so render only clickable
/// 2D UI labels at label_points (ScreenSpaceOverlay Canvas + Button, like InterchangeManager).
/// Fetch the small citywide list at once; do not rebuild if response version matches the previous one.
/// Rotate labels along roads without turning upside down; prioritize nearer labels when removing overlaps and distant ones.
/// </summary>
public class RoadManager : MonoBehaviour
{
    public static RoadManager Instance { get; private set; }

    private const int CANVAS_SORTING_ORDER = 61; // In front of district labels (60), behind interchanges (62)
    private const int LABEL_FONT_SIZE = 20;
    private const float LABEL_HEIGHT_OFFSET = 3f; // Height above road surface (m)
    private const int LABEL_MAX_COUNT = 60;
    private const float LABEL_OVERLAP_PADDING = 6f; // Margin (px) added to bounding rectangles for overlap checks
    private const float LABEL_PATH_SEARCH_RADIUS = 30f; // Search radius (m) for label point heights from owning sections
    private const float GROUND_Y_RETRY_SECONDS = 2f; // Retry interval for labels using ground heights because their sections are unloaded
    private const float CAMERA_POS_THRESHOLD = 1.0f;
    private const float CAMERA_ROT_THRESHOLD = 1.0f;

    private sealed class Label
    {
        public int roadId;
        public Vector2 xz;
        public Vector2 direction;
        public RectTransform rect;
        public Vector2 size;
        public bool hasY;
        public bool yFromPath;
        public float y;
        public float yResolvedAt;
        public Vector2 screen;
        public float screenAngle;
        public float distanceSqr;
    }

    private sealed class DistanceOrder : IComparer<Label>
    {
        public int Compare(Label a, Label b) => a.distanceSqr.CompareTo(b.distanceSqr);
    }

    private static readonly DistanceOrder distanceOrder = new DistanceOrder();

    private readonly Dictionary<int, RoadSummary> roads = new Dictionary<int, RoadSummary>();
    private readonly Dictionary<int, int> roadIdByPath = new Dictionary<int, int>();
    private readonly List<Label> labels = new List<Label>();
    private readonly List<Label> candidates = new List<Label>();
    private readonly List<Rect> occupied = new List<Rect>();

    private Canvas canvas;
    private RectTransform canvasRect;
    private TerrainManager terrainManager;
    private PathManager pathManager;

    /// <summary>Last applied list version. -1 means not fetched.</summary>
    private long loadedVersion = -1;

    /// <summary>
    /// City (session) generation. Advance on Clear; discard responses not matching their fetch-start generation
    /// (prevents pre-switch requests from drawing old-city road names in the new city).
    /// </summary>
    private int generation;
    private bool inFlight;
    private bool refreshQueued;
    private bool initialRequested;
    private bool labelsVisible;
    private float nextForcedLayout = float.PositiveInfinity;

    private Vector3 lastCamPos = Vector3.negativeInfinity;
    private Quaternion lastCamRot = Quaternion.identity;

    public IReadOnlyDictionary<int, RoadSummary> Roads => roads;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        terrainManager = FindAnyObjectByType<TerrainManager>();
        if (terrainManager != null)
        {
            terrainManager.OnAllTerrainLodLoaded += OnTerrainAllLoaded;
        }
    }

    private void OnDestroy()
    {
        if (terrainManager != null)
        {
            terrainManager.OnAllTerrainLodLoaded -= OnTerrainAllLoaded;
        }
        if (canvas != null)
        {
            Destroy(canvas.gameObject);
        }
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// Called from cell load entry points (initial/full reload). Fetch only if never requested.
    /// Leave later updates to RequestRefresh, avoiding refetches on every camera move.
    /// </summary>
    public void EnsureLoaded()
    {
        if (initialRequested) return;
        RequestRefresh();
    }

    /// <summary>
    /// Refetch the list. Called by CellDataLoader.ReloadAllData after epoch completion/committed edits,
    /// and UI world:refresh_roads immediately after road creation, renaming, membership changes, or deletion.
    /// If already fetching, refetch once afterward (the in-flight response may predate the request).
    /// </summary>
    public void RequestRefresh()
    {
        initialRequested = true;
        if (inFlight)
        {
            refreshQueued = true;
            return;
        }
        inFlight = true;
        int requestGeneration = generation;
        StartCoroutine(ApiClient.Get<RoadListResponse>(
            "/roads/",
            response =>
            {
                if (requestGeneration != generation) return;
                if (response != null) Apply(response);
                FinishRequest();
            },
            _ =>
            {
                if (requestGeneration != generation) return;
                FinishRequest();
            }));
    }

    private void FinishRequest()
    {
        inFlight = false;
        if (refreshQueued)
        {
            refreshQueued = false;
            RequestRefresh();
        }
    }

    /// <summary>
    /// Destroy all labels and data. Called by ClearAllManagerData (session switch, full reload, game exit).
    /// Do not stop in-flight requests; advance generation and discard responses.
    /// </summary>
    public void Clear()
    {
        generation++;
        inFlight = false;
        refreshQueued = false;
        initialRequested = false;
        loadedVersion = -1;
        DestroyLabels();
        roads.Clear();
        roadIdByPath.Clear();
    }

    public bool TryGetRoad(int roadId, out RoadSummary road) => roads.TryGetValue(roadId, out road);

    /// <summary>ID of the road containing this path in the list. VALUE_NONE if none.</summary>
    public int GetRoadIdOfPath(int pathId)
    {
        return roadIdByPath.TryGetValue(pathId, out int roadId) ? roadId : Constants.VALUE_NONE;
    }

    /// <summary>
    /// For empty names, use the same "道路 #id" as UI road.unnamed (Unity lacks multilingual resources,
    /// so this remains Japanese like surrounding tooltips). Not used for 3D road labels, which omit unnamed roads.
    /// </summary>
    public static string GetDisplayName(RoadSummary road)
    {
        if (road == null) return "";
        return string.IsNullOrWhiteSpace(road.name) ? $"道路 #{road.road_id}" : road.name;
    }

    /// <summary>Parse "#rrggbb" as a color. White if invalid.</summary>
    public static Color ParseColor(string hex)
    {
        if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out Color color))
        {
            color.a = 1f;
            return color;
        }
        return Color.white;
    }

    private void Apply(RoadListResponse response)
    {
        if (response.version == loadedVersion) return;
        loadedVersion = response.version;

        roads.Clear();
        roadIdByPath.Clear();
        DestroyLabels();

        if (response.roads != null)
        {
            foreach (var road in response.roads)
            {
                if (road == null) continue;
                roads[road.road_id] = road;
                if (road.path_ids != null)
                {
                    foreach (int pathId in road.path_ids) roadIdByPath[pathId] = road.road_id;
                }
                // UI supplies localized "道路 #id" for unnamed roads, but number-only labels have
                // no meaning in 3D, so omit them.
                if (string.IsNullOrWhiteSpace(road.name) || road.label_points == null) continue;
                foreach (var point in road.label_points)
                {
                    if (point == null) continue;
                    CreateLabel(road, point);
                }
            }
        }

        lastCamPos = Vector3.negativeInfinity;
    }

    private void EnsureCanvas()
    {
        if (canvas != null) return;
        canvas = Utils.CreateOverlayCanvas("RoadNameUICanvas", CANVAS_SORTING_ORDER, persistent: true);
        canvasRect = canvas.GetComponent<RectTransform>();
        canvas.gameObject.SetActive(labelsVisible);
    }

    private void CreateLabel(RoadSummary road, RoadLabelPoint point)
    {
        EnsureCanvas();
        var go = new GameObject($"RoadNameLabelUI_{road.road_id}");
        go.transform.SetParent(canvasRect, false);
        var text = go.AddComponent<Text>();
        text.fontSize = LABEL_FONT_SIZE;
        text.font = UiFontProvider.GetFont(LABEL_FONT_SIZE);
        // Dark road colors disappear against backgrounds; blend toward white for readability.
        text.color = Color.Lerp(ParseColor(road.color), Color.white, 0.45f);
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.text = road.name;
        var outline = go.AddComponent<Outline>();
        outline.effectColor = Color.black;
        outline.effectDistance = new Vector2(1.5f, 1.5f);
        var rt = text.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);
        // Clicks use Rect bounds including rotation, so fit to text to avoid stealing clicks from roads/buildings underneath.
        var size = new Vector2(text.preferredWidth + 8f, LABEL_FONT_SIZE + 6f);
        rt.sizeDelta = size;
        var button = go.AddComponent<Button>();
        int capturedId = road.road_id;
        button.onClick.AddListener(() => OnLabelClicked(capturedId));
        go.SetActive(false); // Keep hidden at the origin until the next Update determines the position

        float rad = point.angle_deg * Mathf.Deg2Rad;
        labels.Add(new Label
        {
            roadId = road.road_id,
            xz = new Vector2(point.x, point.z),
            direction = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)),
            rect = rt,
            size = size,
        });
    }

    private void DestroyLabels()
    {
        foreach (var label in labels)
        {
            if (label.rect != null) Destroy(label.rect.gameObject);
        }
        labels.Clear();
        candidates.Clear();
        nextForcedLayout = float.PositiveInfinity;
    }

    private void OnLabelClicked(int roadId)
    {
        IpcClient.Instance?.EmitSelectObject("road_detail", "road", roadId.ToString());
    }

    private void OnTerrainAllLoaded(IReadOnlyList<Vector2Int> updatedCells)
    {
        foreach (var label in labels) label.hasY = false;
        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// Same visibility conditions as interchange labels: world visible, normal mode, and road layer not hidden.
    /// Editing switches road/terrain input modes away from NORMAL; hide accordingly
    /// (uGUI labels left visible would steal road editing clicks underneath).
    /// </summary>
    private static bool ShouldShowLabels()
    {
        var game = GameStateManager.Instance;
        if (game == null || !game.IsWorldVisible) return false;
        if (DriveMode.IsOpen) return false;
        var input = InputHandler.Instance;
        if (input == null) return false;
        if (input.GetMode(InputObjectType.PATH) != InputMode.MODE_NORMAL) return false;
        if (input.GetMode(InputObjectType.TERRAIN) != InputMode.MODE_NORMAL) return false;
        var visual = VisualModeHandler.Instance;
        return visual == null || visual.GetMode(InputObjectType.PATH) != VisualMode.MODE_HIDDEN;
    }

    private void Update()
    {
        bool show = labels.Count > 0 && ShouldShowLabels();
        if (show != labelsVisible)
        {
            labelsVisible = show;
            if (canvas != null) canvas.gameObject.SetActive(show);
            lastCamPos = Vector3.negativeInfinity;
        }
        if (!show) return;

        Camera cam = Camera.main;
        if (cam == null) return;
        Vector3 camPos = cam.transform.position;
        Quaternion camRot = cam.transform.rotation;
        bool forced = Time.unscaledTime >= nextForcedLayout;
        if (!forced
            && Vector3.Distance(camPos, lastCamPos) <= CAMERA_POS_THRESHOLD
            && Quaternion.Angle(camRot, lastCamRot) <= CAMERA_ROT_THRESHOLD)
        {
            return;
        }
        lastCamPos = camPos;
        lastCamRot = camRot;
        Layout(cam, camPos);
    }

    /// <summary>
    /// Place onscreen, in-range labels nearest first; hide those whose bounding rectangles overlap existing labels.
    /// </summary>
    private void Layout(Camera cam, Vector3 camPos)
    {
        nextForcedLayout = float.PositiveInfinity;
        candidates.Clear();
        float maxDistance = Utils.GetMapLabelMaxDistance(camPos);
        float maxSqr = maxDistance * maxDistance;
        foreach (var label in labels)
        {
            if (label.rect == null) continue;
            // 3D distance is at least horizontal distance; hide labels already out of range horizontally without height queries.
            float dx = label.xz.x - camPos.x;
            float dz = label.xz.y - camPos.z;
            if (dx * dx + dz * dz > maxSqr)
            {
                Hide(label);
                continue;
            }
            EnsureLabelY(label);
            Vector3 world = new Vector3(label.xz.x, label.y + LABEL_HEIGHT_OFFSET, label.xz.y);
            Vector3 screen = cam.WorldToScreenPoint(world);
            float distanceSqr = (world - camPos).sqrMagnitude;
            if (screen.z <= cam.nearClipPlane || distanceSqr > maxSqr
                || screen.x < 0f || screen.x > Screen.width || screen.y < 0f || screen.y > Screen.height)
            {
                Hide(label);
                continue;
            }
            label.screen = new Vector2(screen.x, screen.y);
            label.distanceSqr = distanceSqr;
            label.screenAngle = ScreenAngle(cam, world, label.direction, label.screen);
            candidates.Add(label);
        }

        candidates.Sort(distanceOrder);
        occupied.Clear();
        int shown = 0;
        foreach (var label in candidates)
        {
            Rect bounds = RotatedBounds(label.screen, label.size, label.screenAngle);
            bool overlaps = shown >= LABEL_MAX_COUNT;
            for (int i = 0; !overlaps && i < occupied.Count; i++)
            {
                overlaps = occupied[i].Overlaps(bounds);
            }
            if (overlaps)
            {
                Hide(label);
                continue;
            }
            occupied.Add(bounds);
            shown++;
            label.rect.anchoredPosition = label.screen;
            label.rect.localEulerAngles = new Vector3(0f, 0f, label.screenAngle);
            if (!label.rect.gameObject.activeSelf) label.rect.gameObject.SetActive(true);
        }
    }

    private static void Hide(Label label)
    {
        if (label.rect != null && label.rect.gameObject.activeSelf) label.rect.gameObject.SetActive(false);
    }

    /// <summary>
    /// Screen-projected world road angle (degrees), kept in (-90, 90] to avoid upside-down text.
    /// Use horizontal labels if the road points nearly along the view and its screen length collapses.
    /// </summary>
    private static float ScreenAngle(Camera cam, Vector3 world, Vector2 direction, Vector2 screen)
    {
        Vector3 ahead = cam.WorldToScreenPoint(world + new Vector3(direction.x, 0f, direction.y) * 10f);
        Vector2 delta = new Vector2(ahead.x, ahead.y) - screen;
        if (ahead.z <= cam.nearClipPlane || delta.sqrMagnitude < 1f) return 0f;
        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        if (angle > 90f) angle -= 180f;
        else if (angle <= -90f) angle += 180f;
        return angle;
    }

    /// <summary>Axis-aligned rectangle enclosing a centered, sized, rotated (degrees) rectangle, including margins.</summary>
    private static Rect RotatedBounds(Vector2 center, Vector2 size, float angleDeg)
    {
        float rad = angleDeg * Mathf.Deg2Rad;
        float c = Mathf.Abs(Mathf.Cos(rad));
        float s = Mathf.Abs(Mathf.Sin(rad));
        float w = c * size.x + s * size.y + LABEL_OVERLAP_PADDING;
        float h = s * size.x + c * size.y + LABEL_OVERLAP_PADDING;
        return new Rect(center.x - w * 0.5f, center.y - h * 0.5f, w, h);
    }

    /// <summary>
    /// Determine label height from nearby loaded owning-section centerlines, following elevated/bridge surfaces.
    /// If unavailable, use ground height and retry after GROUND_Y_RETRY_SECONDS while awaiting section loading.
    /// </summary>
    private void EnsureLabelY(Label label)
    {
        float now = Time.unscaledTime;
        if (label.hasY && (label.yFromPath || now - label.yResolvedAt < GROUND_Y_RETRY_SECONDS))
        {
            if (!label.yFromPath) nextForcedLayout = Mathf.Min(nextForcedLayout, label.yResolvedAt + GROUND_Y_RETRY_SECONDS);
            return;
        }
        label.hasY = true;
        label.yResolvedAt = now;
        label.yFromPath = TryGetPathY(label, out label.y);
        if (!label.yFromPath)
        {
            label.y = Utils.GetGroundHeight(new Vector3(label.xz.x, 1000f, label.xz.y));
            nextForcedLayout = Mathf.Min(nextForcedLayout, now + GROUND_Y_RETRY_SECONDS);
        }
    }

    private bool TryGetPathY(Label label, out float y)
    {
        y = 0f;
        if (pathManager == null) pathManager = FindAnyObjectByType<PathManager>();
        if (pathManager == null) return false;
        Vector3 target = new Vector3(label.xz.x, 0f, label.xz.y);
        float best = LABEL_PATH_SEARCH_RADIUS * LABEL_PATH_SEARCH_RADIUS;
        bool found = false;
        foreach (var path in pathManager.GetPathsNearCircle(target, LABEL_PATH_SEARCH_RADIUS))
        {
            if (path == null || GetRoadIdOfPath(path.path_id) != label.roadId) continue;
            if (!pathManager.TryGetPathPolyline(path.path_id, out var points, false)) continue;
            for (int i = 1; i < points.Count; i++)
            {
                Vector3 projected = PolylineUtils.ClosestPointOnSegmentXZ(target, points[i - 1], points[i]);
                float distance = Utils.XZDistanceSquared(projected, target);
                if (distance >= best) continue;
                best = distance;
                y = projected.y;
                found = true;
            }
        }
        return found;
    }
}
