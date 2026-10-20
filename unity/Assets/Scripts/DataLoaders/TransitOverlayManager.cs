using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using GeneratedModels;

/// <summary>Display actual public transport routes as foreground colored bands, toggled independently of vehicle display/selection.</summary>
public class TransitOverlayManager : MonoBehaviour
{
    public static TransitOverlayManager Instance { get; private set; }
    private readonly HashSet<int> visibleVariants = new();
    private readonly List<Segment> segments = new();
    private readonly List<StopLabel> labels = new();
    private readonly Dictionary<int, TransitStopDetail> details = new();
    private readonly List<Rect> occupied = new();
    private readonly Dictionary<int, List<Vector3>> stopPaths = new();
    private readonly List<(TransitLineSummary data, TransitRouteGeometry.Route route)> routes = new();
    private readonly HashSet<int> routeIds = new();
    private readonly HashSet<int> watchedPaths = new(); // Underlying paths for routes/stops. Detect geometry changes using revisions in this set
    private readonly List<SegmentGroup> groups = new();
    private readonly Plane[] planes = new Plane[6];
    private static readonly GUIContent labelContent = new();
    private static readonly LabelOrder labelOrder = new();
    private OverlayMeshRenderer border, ribbon;
    private PathManager paths;
    private TransitRouteGeometry geometry;
    private GUIStyle labelStyle;
    private float nextCheck, nextSafety, nextDetails;
    private bool fetching, dirty, routesDirty = true, terrainChanged, labelsSorted;
    private int routesRevision = -1;
    private long pathSignature;
    private Vector3 labelsSortedFor;
    private int session = -1;
    private Vector3 cameraPosition;
    private Quaternion cameraRotation;
    private float cameraFov;
    private int screenWidth, screenHeight;
    /// <summary>Geometry change check interval. Without changes, rebuild only every SafetyInterval.</summary>
    private const float CheckInterval = .5f, SafetyInterval = 5f;
    private const int GroupSize = 32;
    private struct Segment { public Vector3 a, b; public int id; public Color color; }
    /// <summary>Bounding box for GroupSize consecutive segments, used for frustum culling.</summary>
    private struct SegmentGroup { public int start, count; public Bounds bounds; }
    private sealed class StopLabel { public Vector3 position; public string text; public Color color; public readonly List<int> lines = new(); public Rect box; public bool shown; public float distance, width = -1; }
    private sealed class LabelOrder : IComparer<StopLabel> { public int Compare(StopLabel a, StopLabel b) => a.distance.CompareTo(b.distance); }
    [Serializable] public sealed class Stop { public int node_id, order; public float[] position; public string name; public int? station_path_id, house_path_id; }
    [Serializable] public sealed class TransitStopDetail { public List<Stop> stops; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance == null) new GameObject("TransitOverlayManager").AddComponent<TransitOverlayManager>();
    }
    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        border = CreateRenderer("TransitOutline", 3200);
        ribbon = CreateRenderer("TransitRibbon", 3201);
        TerrainManager.OnTerrainTileReady += OnTerrainReady;
    }
    private void OnTerrainReady(Vector2Int cell, Terrain terrain) => terrainChanged = true;
    private OverlayMeshRenderer CreateRenderer(string name, int queue)
    {
        var renderer = OverlayMeshRenderer.Create(name, alwaysOnTop: true, parent: transform);
        renderer.Material.renderQueue = queue;
        return renderer;
    }
    public void SetVisibleVariants(HashSet<int> variants)
    {
        visibleVariants.Clear();
        if (variants != null) visibleVariants.UnionWith(variants);
        dirty = routesDirty = true; nextDetails = 0;
        TransitSelection.Cancel();
        if (visibleVariants.Count == 0) { segments.Clear(); groups.Clear(); labels.Clear(); border.ClearMesh(); ribbon.ClearMesh(); }
    }
    public void MarkDirty() { dirty = true; nextDetails = 0; }
    /// <summary>Line whose route is being edited (-1=none). Hide its bands/stop names to avoid overlapping the new route preview.</summary>
    public int HiddenLineId
    {
        get => hiddenLineId;
        set { if (hiddenLineId == value) return; hiddenLineId = value; dirty = routesDirty = true; }
    }
    private int hiddenLineId = -1;
    public void RefreshLines() { TransitDataStore.Ensure().Refresh(); MarkDirty(); }
    public void Clear()
    {
        StopAllCoroutines(); fetching = false;
        details.Clear(); stopPaths.Clear(); geometry?.Clear();
        SetVisibleVariants(null);
        TransitDataStore.Ensure().Clear();
    }
    private void LateUpdate()
    {
        var store = TransitDataStore.Ensure();
        if (session != store.Session)
        {
            session = store.Session; StopAllCoroutines(); fetching = false;
            details.Clear(); segments.Clear(); groups.Clear(); labels.Clear(); routes.Clear(); routeIds.Clear(); watchedPaths.Clear();
            dirty = routesDirty = true; nextDetails = 0;
            border.ClearMesh(); ribbon.ClearMesh();
            TransitSelection.Cancel();
        }
        if (visibleVariants.Count == 0 || Camera.main == null) return;
        if (GameStateManager.Instance == null || !GameStateManager.Instance.IsGameStarted()) return;
        if (paths == null) paths = FindAnyObjectByType<PathManager>();
        if (paths == null) return;
        geometry ??= new TransitRouteGeometry(paths);
        if (!fetching && Time.unscaledTime >= nextDetails) StartCoroutine(RefreshDetails());
        Camera cam = Camera.main;
        float now = Time.unscaledTime;
        bool due = dirty || now >= nextSafety;
        if (!due && now >= nextCheck)
        {
            nextCheck = now + CheckInterval;
            // Known path revisions include terrain signatures; rebuild on terrain arrival only if some path revisions are unknown.
            due = store.Revision != routesRevision || PathSignature(out bool uncertain) != pathSignature || (terrainChanged && uncertain);
        }
        if (due)
        {
            if (routesDirty || store.Revision != routesRevision) BuildRoutes(store);
            BuildGeometry();
            pathSignature = PathSignature(out _);
            dirty = terrainChanged = false; nextSafety = now + SafetyInterval; nextCheck = now + CheckInterval;
            Draw(cam);
        }
        else if (cam.transform.position != cameraPosition || cam.transform.rotation != cameraRotation || cam.fieldOfView != cameraFov || Screen.width != screenWidth || Screen.height != screenHeight) Draw(cam);
    }
    private IEnumerator RefreshDetails()
    {
        fetching = true; nextDetails = Time.unscaledTime + 10;
        int requestSession = session;
        bool changed = false;
        var source = new List<TransitLineSummary>(TransitDataStore.Ensure().Lines);
        var alive = new HashSet<int>();
        foreach (var line in source)
        {
            alive.Add(line.line_agent_id);
            if (!visibleVariants.Contains(line.path_variant)) continue;
            int id = line.line_agent_id;
            yield return ApiClient.GetBulk<TransitStopDetail>("/transit/lines/" + id, value =>
            {
                if (value == null) return;
                if (!details.TryGetValue(id, out var current)) { details[id] = value; dirty = true; return; } // Show initial results per response
                if (SameStops(current, value)) return;
                details[id] = value; changed = true;
            }, _ => { }, timingLabel: "/transit/lines/{id}", isCanceled: () => session != requestSession);
        }
        foreach (int id in new List<int>(details.Keys)) if (!alive.Contains(id) && details.Remove(id)) changed = true;
        if (changed) dirty = true;
        fetching = false;
    }
    private static bool SameStops(TransitStopDetail a, TransitStopDetail b)
    {
        if (a.stops == null || b.stops == null) return a.stops == b.stops;
        if (a.stops.Count != b.stops.Count) return false;
        for (int i = 0; i < a.stops.Count; i++)
        {
            Stop x = a.stops[i], y = b.stops[i];
            if (x == null || y == null) { if (x != y) return false; continue; }
            if (x.node_id != y.node_id || x.order != y.order || x.station_path_id != y.station_path_id || x.house_path_id != y.house_path_id
                || !string.Equals(x.name, y.name, StringComparison.Ordinal) || !SamePosition(x.position, y.position)) return false;
        }
        return true;
    }
    private static bool SamePosition(float[] a, float[] b)
    {
        if (a == null || b == null) return a == b;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    /// <summary>Build routes for displayed lines. Call only when the line list or displayed type changes.</summary>
    private void BuildRoutes(TransitDataStore store)
    {
        routes.Clear(); routeIds.Clear();
        foreach (var line in store.Lines)
        {
            if (!visibleVariants.Contains(line.path_variant) || line.line_agent_id == hiddenLineId) continue;
            // Pass vehicle type to place bands in the same lanes/tracks as vehicles, avoiding overlapping bus/LRT bands on shared roads.
            var route = new TransitRouteGeometry.Route { motion = new TransitMotionClock(line.journeys), VehicleType = VehicleModelConfig.GetTransitVehicleType(line.path_variant) };
            TransitRouteGeometry.BuildRuns(route);
            routes.Add((line, route));
            foreach (var run in route.runs) routeIds.Add(run.path);
        }
        routesRevision = store.Revision; routesDirty = false;
    }
    /// <summary>
    /// Order-independent signature combining data references, versions, and rendered geometry revisions for watched paths.
    /// If any geometry revision is -1 (uncached), set uncertain so terrain arrival counts as a change.
    /// </summary>
    private long PathSignature(out bool uncertain)
    {
        uncertain = false;
        ulong sum = 0;
        unchecked
        {
            foreach (int id in watchedPaths)
            {
                ulong term = (ulong)id;
                var data = paths.GetPathData(id);
                if (data != null)
                {
                    long revision = paths.GetPathGeometryRevision(id);
                    if (revision < 0) uncertain = true;
                    term = Mix(term ^ Mix((ulong)data.version << 32 ^ (uint)RuntimeHelpers.GetHashCode(data)));
                    term = Mix(term ^ Mix((ulong)revision));
                }
                sum += Mix(term);
            }
            return (long)sum;
        }
    }
    private static ulong Mix(ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }
    private void BuildGeometry()
    {
        segments.Clear(); groups.Clear(); labels.Clear(); stopPaths.Clear();
        watchedPaths.Clear(); watchedPaths.UnionWith(routeIds);
        labelsSorted = false;
        var candidates = new List<PathSummary>();
        foreach (int id in routeIds) { var data = paths.GetPathData(id); if (data != null) candidates.Add(data); }
        geometry.Refresh(routeIds, candidates);
        var grouped = new Dictionary<string, StopLabel>();
        foreach (var item in routes)
        {
            Color color = GetLineColor(item.data);
            // Break lines at unloaded sections. Keep fixed sample spacing; camera updates do not rebuild routes themselves.
            foreach (var run in item.route.runs)
            {
                if (run.Wrapped || !geometry.HasPath(run.path)) continue;
                int count = Mathf.Max(1, Mathf.CeilToInt((float)(run.end - run.start) / 2));
                bool connected = false; Vector3 previous = default;
                for (int i = 0; i <= count; i++)
                {
                    double distance = run.start + (run.end - run.start) * i / count;
                    bool valid = geometry.Sample(item.route, distance, distance, run.Forward, out var point, out _);
                    if (valid && connected && (point - previous).sqrMagnitude > .000001f)
                        segments.Add(new Segment { a = previous, b = point, id = item.data.line_agent_id, color = color });
                    connected = valid; previous = point;
                }
            }
            if (!details.TryGetValue(item.data.line_agent_id, out var detail) || detail.stops == null) continue;
            foreach (var stop in detail.stops)
            {
                if (stop?.station_path_id != null) watchedPaths.Add(stop.station_path_id.Value);
                if (stop == null || stop.position == null || stop.position.Length < 3 || !TryStopPosition(stop, out var position)) continue;
                string key = stop.house_path_id.HasValue ? "house:" + stop.house_path_id : "stop:" + stop.station_path_id + ":" + Mathf.RoundToInt(position.x) + ":" + Mathf.RoundToInt(position.z);
                if (!grouped.TryGetValue(key, out var label))
                {
                    label = new StopLabel { position = position, text = string.IsNullOrWhiteSpace(stop.name) ? "#" + stop.node_id : stop.name, color = color };
                    grouped[key] = label; labels.Add(label);
                }
                if (!label.lines.Contains(item.data.line_agent_id)) label.lines.Add(item.data.line_agent_id);
            }
        }
        for (int start = 0; start < segments.Count; start += GroupSize)
        {
            int count = Mathf.Min(GroupSize, segments.Count - start);
            var bounds = new Bounds(segments[start].a, Vector3.zero);
            for (int i = start; i < start + count; i++) { bounds.Encapsulate(segments[i].a); bounds.Encapsulate(segments[i].b); }
            groups.Add(new SegmentGroup { start = start, count = count, bounds = bounds });
        }
    }
    private bool TryStopPosition(Stop stop, out Vector3 position)
    {
        position = default;
        if (!stop.station_path_id.HasValue) return false;
        int id = stop.station_path_id.Value;
        if (!stopPaths.TryGetValue(id, out var points))
        {
            if (!paths.TryGetPathPolyline(id, out points, false)) return false;
            stopPaths[id] = points;
        }
        float best = float.PositiveInfinity;
        Vector2 target = new Vector2(stop.position[0], stop.position[2]);
        for (int i = 1; i < points.Count; i++)
        {
            Vector2 a = new Vector2(points[i-1].x, points[i-1].z), b = new Vector2(points[i].x, points[i].z);
            float t = PolylineUtils.ProjectOnSegment(target, a, b);
            float error = (Vector2.Lerp(a, b, t) - target).sqrMagnitude;
            if (error < best) { best = error; position = Vector3.Lerp(points[i-1], points[i], t) + Vector3.up * .3f; }
        }
        return !float.IsPositiveInfinity(best);
    }
    private void Draw(Camera cam)
    {
        border.BeginFrame(); ribbon.BeginFrame();
        GeometryUtility.CalculateFrustumPlanes(cam, planes);
        Vector3 eye = cam.transform.position;
        // Use OverlayMeshRenderer's conversion (distance×tan(fov/2)/half screen height regardless of projection) to include band outer-edge margins.
        float perPixel = Mathf.Tan(cam.fieldOfView * .5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height * .5f);
        foreach (var group in groups)
        {
            var bounds = group.bounds;
            float reach = Vector3.Distance(eye, bounds.center) + bounds.extents.magnitude;
            bounds.Expand(2 * 12 * reach * perPixel);
            if (!GeometryUtility.TestPlanesAABB(planes, bounds)) continue;
            int end = Mathf.Min(group.start + group.count, segments.Count);
            for (int i = group.start; i < end; i++)
            {
                var segment = segments[i];
                border.AddSolidLine(segment.a, segment.b, new Color(.04f,.05f,.07f,1), 12);
                ribbon.AddSolidLine(segment.a, segment.b, segment.color, 8);
            }
        }
        foreach (var label in labels)
        {
            float margin = 8 * Vector3.Distance(eye, label.position) * perPixel;
            if (!GeometryUtility.TestPlanesAABB(planes, new Bounds(label.position, Vector3.one * (2 * margin)))) continue;
            border.AddCircle(label.position, 8, Color.black);
            ribbon.AddCircle(label.position, 5, Color.white);
        }
        border.EndFrame(cam); ribbon.EndFrame(cam);
        SortLabels(eye);
        cameraPosition = cam.transform.position; cameraRotation = cam.transform.rotation; cameraFov = cam.fieldOfView;
        screenWidth = Screen.width; screenHeight = Screen.height;
    }
    /// <summary>Sort labels nearest first. OnGUI placement is first-come, giving near labels priority.</summary>
    private void SortLabels(Vector3 eye)
    {
        foreach (var label in labels) label.distance = (label.position - eye).sqrMagnitude;
        labels.Sort(labelOrder);
        labelsSorted = true; labelsSortedFor = eye;
    }
    public List<int> Pick(Camera cam, Vector2 mouse)
    {
        var found = new List<int>();
        Vector2 gui = new Vector2(mouse.x, Screen.height - mouse.y);
        foreach (var label in labels) if (label.shown && label.box.Contains(gui)) foreach (int id in label.lines) if (!found.Contains(id)) found.Add(id);
        if (found.Count > 0) return found;
        foreach (var label in labels)
        {
            Vector3 point = cam.WorldToScreenPoint(label.position);
            if (point.z > cam.nearClipPlane && ((Vector2)point-mouse).sqrMagnitude <= 100)
                foreach (int id in label.lines) if (!found.Contains(id)) found.Add(id);
        }
        if (found.Count > 0) return found;
        foreach (var s in segments)
        {
            Vector3 a = cam.WorldToScreenPoint(s.a), b = cam.WorldToScreenPoint(s.b);
            if (a.z <= cam.nearClipPlane || b.z <= cam.nearClipPlane) continue;
            float t = PolylineUtils.ProjectOnSegment(mouse, (Vector2)a, (Vector2)b);
            if ((mouse - Vector2.Lerp((Vector2)a, (Vector2)b, t)).sqrMagnitude <= 64 && !found.Contains(s.id)) found.Add(s.id);
        }
        return found;
    }
    private void OnGUI()
    {
        if (visibleVariants.Count == 0 || Camera.main == null) return;
        labelStyle ??= new GUIStyle(GUI.skin.label) { font = UiFontProvider.GetFont(14), fontSize = 14, alignment = TextAnchor.MiddleLeft };
        occupied.Clear();
        Camera cam = Camera.main;
        if (!labelsSorted || labelsSortedFor != cam.transform.position) SortLabels(cam.transform.position);
        int count = 0;
        foreach (var label in labels)
        {
            label.shown = false;
            Vector3 projected = cam.WorldToScreenPoint(label.position);
            if (count >= 80 || projected.z <= cam.nearClipPlane || projected.x < 0 || projected.x > Screen.width || projected.y < 0 || projected.y > Screen.height) continue;
            Vector2 anchor = new Vector2(projected.x, Screen.height-projected.y);
            if (label.width < 0) { labelContent.text = label.text; label.width = Mathf.Min(300, labelStyle.CalcSize(labelContent).x + 24); }
            float width = label.width;
            if (!WorldCallout.TryPlace(anchor, new Vector2(width,28), occupied, out label.box)) continue;
            label.shown = true; count++;
            WorldCallout.Draw(anchor, label.box, label.text, label.color, labelStyle);
        }
    }
    public static Color GetLineColor(TransitLineSummary line)
    {
        if (!string.IsNullOrEmpty(line.line_color) && ColorUtility.TryParseHtmlString(line.line_color, out var color)) { color.a = 1; return color; }
        return Color.HSVToRGB((line.path_variant * .61803398875f) % 1, .75f, .95f);
    }
    private void OnDestroy()
    {
        TerrainManager.OnTerrainTileReady -= OnTerrainReady;
        foreach (var renderer in new[] { border, ribbon })
        {
            if (renderer == null) continue;
            Destroy(renderer.GetComponent<MeshFilter>().sharedMesh);
            Destroy(renderer.GetComponent<MeshRenderer>().sharedMaterial);
        }
        if (Instance == this) Instance = null;
    }
}
