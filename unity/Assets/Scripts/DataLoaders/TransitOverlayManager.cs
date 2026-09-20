using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>公共交通の実走行経路を前面の色帯で表示する。車両の表示・選択とは独立して切り替える。</summary>
public class TransitOverlayManager : MonoBehaviour
{
    public static TransitOverlayManager Instance { get; private set; }
    private readonly HashSet<int> visibleVariants = new();
    private readonly List<Segment> segments = new();
    private readonly List<StopLabel> labels = new();
    private readonly Dictionary<int, TransitStopDetail> details = new();
    private readonly List<Rect> occupied = new();
    private readonly Dictionary<int, List<Vector3>> stopPaths = new();
    private OverlayMeshRenderer border, ribbon;
    private PathManager paths;
    private TransitRouteGeometry geometry;
    private GUIStyle labelStyle;
    private float nextGeometry, nextDetails;
    private bool fetching, dirty;
    private int session = -1;
    private Vector3 cameraPosition;
    private Quaternion cameraRotation;
    private float cameraFov;
    private int screenWidth, screenHeight;
    private struct Segment { public Vector3 a, b; public int id; public Color color; }
    private sealed class StopLabel { public Vector3 position; public string text; public Color color; public readonly List<int> lines = new(); public Rect box; public bool shown; }
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
    }
    private OverlayMeshRenderer CreateRenderer(string name, int queue)
    {
        var go = new GameObject(name); go.transform.SetParent(transform);
        var renderer = go.AddComponent<OverlayMeshRenderer>();
        var material = new Material(Shader.Find("Custom/OverlayLine"));
        material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        material.renderQueue = queue;
        renderer.Initialize(material);
        return renderer;
    }
    public void SetVisibleVariants(HashSet<int> variants)
    {
        visibleVariants.Clear();
        if (variants != null) visibleVariants.UnionWith(variants);
        dirty = true; nextDetails = 0;
        TransitSelection.Cancel();
        if (visibleVariants.Count == 0) { segments.Clear(); labels.Clear(); border.ClearMesh(); ribbon.ClearMesh(); }
    }
    public void MarkDirty() { dirty = true; nextDetails = 0; }
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
            details.Clear(); segments.Clear(); labels.Clear(); dirty = true; nextDetails = 0;
            TransitSelection.Cancel();
        }
        if (visibleVariants.Count == 0 || Camera.main == null) return;
        if (GameStateManager.Instance == null || !GameStateManager.Instance.IsGameStarted()) return;
        if (paths == null) paths = FindAnyObjectByType<PathManager>();
        if (paths == null) return;
        geometry ??= new TransitRouteGeometry(paths);
        if (!fetching && Time.unscaledTime >= nextDetails) StartCoroutine(RefreshDetails());
        Camera cam = Camera.main;
        if (dirty || Time.unscaledTime >= nextGeometry)
        {
            BuildGeometry(); dirty = false; nextGeometry = Time.unscaledTime + .5f;
            Draw(cam);
        }
        else if (cam.transform.position != cameraPosition || cam.transform.rotation != cameraRotation || cam.fieldOfView != cameraFov || Screen.width != screenWidth || Screen.height != screenHeight) Draw(cam);
    }
    private IEnumerator RefreshDetails()
    {
        fetching = true; nextDetails = Time.unscaledTime + 10;
        var source = new List<TransitLineSummary>(TransitDataStore.Ensure().Lines);
        var alive = new HashSet<int>();
        foreach (var line in source)
        {
            alive.Add(line.line_agent_id);
            if (!visibleVariants.Contains(line.path_variant)) continue;
            int id = line.line_agent_id;
            yield return ApiClient.Get<TransitStopDetail>("/transit/lines/" + id, value =>
            {
                if (value != null) { details[id] = value; dirty = true; }
            }, _ => { });
        }
        foreach (int id in new List<int>(details.Keys)) if (!alive.Contains(id)) details.Remove(id);
        fetching = false;
    }
    private void BuildGeometry()
    {
        segments.Clear(); labels.Clear(); stopPaths.Clear();
        var routes = new List<(TransitLineSummary data, TransitRouteGeometry.Route route)>();
        var ids = new HashSet<int>();
        foreach (var line in TransitDataStore.Ensure().Lines)
        {
            if (!visibleVariants.Contains(line.path_variant)) continue;
            var route = new TransitRouteGeometry.Route { motion = new TransitMotionClock(line.journeys), VehicleType = VehicleModelConfig.GetTransitVehicleType(line.path_variant) };
            TransitRouteGeometry.BuildRuns(route);
            routes.Add((line, route));
            foreach (var run in route.runs) ids.Add(run.path);
        }
        var candidates = new List<PathSummary>();
        foreach (int id in ids) { var data = paths.GetPathData(id); if (data != null) candidates.Add(data); }
        geometry.Refresh(ids, candidates);
        var grouped = new Dictionary<string, StopLabel>();
        foreach (var item in routes)
        {
            Color color = GetLineColor(item.data);
            // 未ロードの区間では線を切る。標本間隔を固定し、カメラ更新で経路自体を作り直さない。
            foreach (var run in item.route.runs)
            {
                if (!geometry.HasPath(run.path)) continue;
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
                if (stop.position == null || stop.position.Length < 3 || !TryStopPosition(stop, out var position)) continue;
                string key = stop.house_path_id.HasValue ? "house:" + stop.house_path_id : "stop:" + stop.station_path_id + ":" + Mathf.RoundToInt(position.x) + ":" + Mathf.RoundToInt(position.z);
                if (!grouped.TryGetValue(key, out var label))
                {
                    label = new StopLabel { position = position, text = string.IsNullOrWhiteSpace(stop.name) ? "#" + stop.node_id : stop.name, color = color };
                    grouped[key] = label; labels.Add(label);
                }
                if (!label.lines.Contains(item.data.line_agent_id)) label.lines.Add(item.data.line_agent_id);
            }
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
            Vector2 d = b - a;
            float t = d.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(target-a, d)/d.sqrMagnitude) : 0;
            float error = (a+d*t-target).sqrMagnitude;
            if (error < best) { best = error; position = Vector3.Lerp(points[i-1], points[i], t) + Vector3.up * .3f; }
        }
        return !float.IsPositiveInfinity(best);
    }
    private void Draw(Camera cam)
    {
        border.BeginFrame(); ribbon.BeginFrame();
        foreach (var segment in segments)
        {
            border.AddSolidLine(segment.a, segment.b, new Color(.04f,.05f,.07f,1), 12);
            ribbon.AddSolidLine(segment.a, segment.b, segment.color, 8);
        }
        foreach (var label in labels)
        {
            border.AddCircle(label.position, 8, Color.black);
            ribbon.AddCircle(label.position, 5, Color.white);
        }
        border.EndFrame(cam); ribbon.EndFrame(cam);
        cameraPosition = cam.transform.position; cameraRotation = cam.transform.rotation; cameraFov = cam.fieldOfView;
        screenWidth = Screen.width; screenHeight = Screen.height;
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
            Vector2 d = (Vector2)(b-a);
            float t = d.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(mouse-(Vector2)a,d)/d.sqrMagnitude) : 0;
            if ((mouse-((Vector2)a+d*t)).sqrMagnitude <= 64 && !found.Contains(s.id)) found.Add(s.id);
        }
        return found;
    }
    private void OnGUI()
    {
        if (visibleVariants.Count == 0 || Camera.main == null) return;
        labelStyle ??= new GUIStyle(GUI.skin.label) { font = UiFontProvider.GetFont(14), fontSize = 14, alignment = TextAnchor.MiddleLeft };
        occupied.Clear();
        Camera cam = Camera.main;
        labels.Sort((a,b) => (a.position-cam.transform.position).sqrMagnitude.CompareTo((b.position-cam.transform.position).sqrMagnitude));
        int count = 0;
        foreach (var label in labels)
        {
            label.shown = false;
            Vector3 projected = cam.WorldToScreenPoint(label.position);
            if (count >= 80 || projected.z <= cam.nearClipPlane || projected.x < 0 || projected.x > Screen.width || projected.y < 0 || projected.y > Screen.height) continue;
            Vector2 anchor = new Vector2(projected.x, Screen.height-projected.y);
            float width = Mathf.Min(300, labelStyle.CalcSize(new GUIContent(label.text)).x + 24);
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
        foreach (var renderer in new[] { border, ribbon })
        {
            if (renderer == null) continue;
            Destroy(renderer.GetComponent<MeshFilter>().sharedMesh);
            Destroy(renderer.GetComponent<MeshRenderer>().sharedMaterial);
        }
        if (Instance == this) Instance = null;
    }
}
