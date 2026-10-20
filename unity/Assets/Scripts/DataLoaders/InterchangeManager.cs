using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using GeneratedModels;

/// <summary>
/// Manager rendering name labels for backend-detected interchanges (IC/JCT).
/// PathManager renders interchange roads as ordinary paths, so this manager owns only clickable
/// 2D UI labels at their centers (ScreenSpaceOverlay Canvas + Button, like ZoneManager district names).
/// Fetch the small citywide list at once; do not rebuild if the response version matches the previous one.
/// </summary>
public class InterchangeManager : MonoBehaviour
{
    public static InterchangeManager Instance { get; private set; }

    private const int CANVAS_SORTING_ORDER = 62; // In front of district labels (60), behind alerts (70)
    private const int LABEL_FONT_SIZE = 26;
    private const float LABEL_HEIGHT_OFFSET = 6f; // Height above ground (m)
    private const float LABEL_MAX_DISTANCE = 6000f; // Hide interchanges beyond this distance (avoiding labels scattered across distant views)
    private const float CAMERA_POS_THRESHOLD = 1.0f;
    private const float CAMERA_ROT_THRESHOLD = 2.0f;
    private static readonly Color LABEL_COLOR = new Color(1f, 0.92f, 0.62f);

    private readonly Dictionary<int, InterchangeSummary> interchanges = new Dictionary<int, InterchangeSummary>();
    private readonly Dictionary<int, RectTransform> labels = new Dictionary<int, RectTransform>();

    /// <summary>
    /// Ground height at label positions (interchange_id to Y). Cache to avoid sampling terrain for every interchange on each camera move.
    /// Discard when terrain finishes loading, lists update, or Clear runs; resolve again when entering the visible range.
    /// </summary>
    private readonly Dictionary<int, float> labelGroundY = new Dictionary<int, float>();

    private Canvas canvas;
    private RectTransform canvasRect;
    private TerrainManager terrainManager;

    /// <summary>Last applied list version. -1 means not fetched.</summary>
    private int loadedVersion = -1;

    /// <summary>
    /// City (session) generation. Advance on Clear and discard responses not matching their fetch-start generation.
    /// Prevents requests sent before a session switch from drawing the old city's interchanges in the new city.
    /// </summary>
    private int generation;
    private bool inFlight;
    private bool refreshQueued;
    private bool initialRequested;
    private bool labelsVisible;

    private Vector3 lastCamPos = Vector3.negativeInfinity;
    private Quaternion lastCamRot = Quaternion.identity;

    public IReadOnlyDictionary<int, InterchangeSummary> Interchanges => interchanges;

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
    /// Called from cell load entry points (initial load and full reload). Fetch only if never requested.
    /// Leave later updates to RequestRefresh, avoiding refetches on every camera move.
    /// </summary>
    public void EnsureLoaded()
    {
        if (initialRequested) return;
        RequestRefresh();
    }

    /// <summary>
    /// Refetch the list. Called by CellDataLoader.ReloadAllData after epoch completion, committed edits, and session:reload.
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
        StartCoroutine(ApiClient.Get<InterchangesListResponse>(
            "/interchanges/",
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
    /// Destroy every label and all data. Called by ClearAllManagerData (session switch, full reload, game exit).
    /// Do not stop in-flight requests; advance the generation and discard responses.
    /// </summary>
    public void Clear()
    {
        generation++;
        inFlight = false;
        refreshQueued = false;
        initialRequested = false;
        loadedVersion = -1;
        foreach (var label in labels.Values)
        {
            if (label != null) Destroy(label.gameObject);
        }
        labels.Clear();
        interchanges.Clear();
        labelGroundY.Clear();
    }

    private void Apply(InterchangesListResponse response)
    {
        if (response.version == loadedVersion) return;
        loadedVersion = response.version;
        labelGroundY.Clear();

        var received = new HashSet<int>();
        if (response.interchanges != null)
        {
            foreach (var ic in response.interchanges)
            {
                if (ic == null || ic.center == null || ic.center.Length < 2) continue;
                received.Add(ic.interchange_id);
                interchanges[ic.interchange_id] = ic;
                UpsertLabel(ic);
            }
        }

        var removed = new List<int>();
        foreach (int id in interchanges.Keys)
        {
            if (!received.Contains(id)) removed.Add(id);
        }
        foreach (int id in removed)
        {
            interchanges.Remove(id);
            if (labels.TryGetValue(id, out var label))
            {
                if (label != null) Destroy(label.gameObject);
                labels.Remove(id);
            }
        }

        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>Use type/number abbreviations for empty names (Unity lacks localized resources, so use abbreviations requiring no translation).</summary>
    public static string GetDisplayName(InterchangeSummary ic)
    {
        if (!string.IsNullOrWhiteSpace(ic.name)) return ic.name;
        string prefix = ic.kind == Constants.INTERCHANGE_KIND_JCT ? "JCT" : "IC";
        return $"{prefix} {ic.interchange_id}";
    }

    private void EnsureCanvas()
    {
        if (canvas != null) return;
        canvas = Utils.CreateOverlayCanvas("InterchangeUICanvas", CANVAS_SORTING_ORDER, persistent: true);
        canvasRect = canvas.GetComponent<RectTransform>();
        canvas.gameObject.SetActive(labelsVisible);
    }

    private void UpsertLabel(InterchangeSummary ic)
    {
        EnsureCanvas();
        if (!labels.TryGetValue(ic.interchange_id, out var rt) || rt == null)
        {
            var go = new GameObject($"InterchangeLabelUI_{ic.interchange_id}");
            go.transform.SetParent(canvasRect, false);
            var text = go.AddComponent<Text>();
            text.fontSize = LABEL_FONT_SIZE;
            text.font = UiFontProvider.GetFont(LABEL_FONT_SIZE);
            text.color = LABEL_COLOR;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            var outline = go.AddComponent<Outline>();
            outline.effectColor = Color.black;
            outline.effectDistance = new Vector2(2f, 2f);
            rt = text.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            var button = go.AddComponent<Button>();
            int capturedId = ic.interchange_id;
            button.onClick.AddListener(() => OnLabelClicked(capturedId));
            go.SetActive(false); // Keep hidden at the origin until the next Update determines the position
            labels[ic.interchange_id] = rt;
        }

        var label = rt.GetComponent<Text>();
        label.text = GetDisplayName(ic);
        // Clicks use the Rect bounds, so fit them to the text to avoid stealing clicks from buildings/roads underneath.
        rt.sizeDelta = new Vector2(label.preferredWidth + 12f, LABEL_FONT_SIZE + 10f);
    }

    private void OnLabelClicked(int interchangeId)
    {
        IpcClient.Instance?.EmitSelectObject("intersection_detail", "interchange", interchangeId.ToString());
    }

    private void OnTerrainAllLoaded(IReadOnlyList<Vector2Int> updatedCells)
    {
        labelGroundY.Clear();
        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// Show labels only in normal browsing state. Editing switches road/terrain input modes away from NORMAL,
    /// so hide based on those modes (uGUI labels left visible would steal road editing clicks underneath).
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
        if (Vector3.Distance(camPos, lastCamPos) <= CAMERA_POS_THRESHOLD
            && Quaternion.Angle(camRot, lastCamRot) <= CAMERA_ROT_THRESHOLD)
        {
            return;
        }
        lastCamPos = camPos;
        lastCamRot = camRot;

        foreach (var kv in labels)
        {
            RectTransform rt = kv.Value;
            if (rt == null || !interchanges.TryGetValue(kv.Key, out var ic)) continue;
            float x = ic.center[0];
            float z = ic.center[1];
            // 3D distance is at least horizontal distance; hide interchanges already out of range horizontally without sampling terrain.
            float dx = x - camPos.x;
            float dz = z - camPos.z;
            if (dx * dx + dz * dz > LABEL_MAX_DISTANCE * LABEL_MAX_DISTANCE)
            {
                if (rt.gameObject.activeSelf) rt.gameObject.SetActive(false);
                continue;
            }
            if (!labelGroundY.TryGetValue(kv.Key, out float groundY))
            {
                groundY = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
                labelGroundY[kv.Key] = groundY;
            }
            Vector3 world = new Vector3(x, groundY + LABEL_HEIGHT_OFFSET, z);
            Vector3 screen = cam.WorldToScreenPoint(world);
            bool visible = screen.z > 0f && Vector3.Distance(camPos, world) <= LABEL_MAX_DISTANCE;
            if (rt.gameObject.activeSelf != visible) rt.gameObject.SetActive(visible);
            if (visible) rt.anchoredPosition = new Vector2(screen.x, screen.y);
        }
    }
}
