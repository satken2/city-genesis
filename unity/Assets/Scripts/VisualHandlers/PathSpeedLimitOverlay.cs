using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>物理パスの設定制限速度を、アラートと同じ画面Canvas方式で中心位置に固定表示する。</summary>
public sealed class PathSpeedLimitOverlay : MonoBehaviour
{
    private sealed class Entry
    {
        public int id;
        public Vector3 position;
        public GameObject pathObject;
        public VisualModeObject visual;
        public DrawLodInfo lod;
        public PathPresentation presentation;
        public SplinePartLod[] parts;
        public SpeedLimitSign sign;

        public void Bind(GameObject current)
        {
            if (pathObject == current) return;
            pathObject = current;
            visual = current.GetComponent<VisualModeObject>();
            lod = current.GetComponent<DrawLodInfo>();
            presentation = current.GetComponent<PathPresentation>();
            parts = current.GetComponentsInChildren<SplinePartLod>(true);
        }

        public bool IsDetailVisible()
        {
            if (lod == null || (lod.drawLod != DrawLod.Normal && lod.drawLod != DrawLod.Close)
                || (presentation != null && !presentation.IsReady)) return false;
            // 長い道路の親はNormalのままなので、標識位置の本体区間を確認する。
            foreach (var part in parts)
            {
                if (part == null || !part.IsBody) continue;
                var segmentLod = part.GetDrawLodAtPosition(position);
                if (segmentLod != DrawLod.Normal && segmentLod != DrawLod.Close) return false;
            }
            return true;
        }
    }

    private readonly Dictionary<int, Entry> entries = new();
    private readonly HashSet<int> seen = new();
    private readonly List<int> removed = new();
    private PathManager paths;
    private Canvas canvas;
    private SpeedLimitSignStyle style = SpeedLimitSignStyle.Japanese;
    private float nextRefresh;
    private bool showing;

    private void Awake() => paths = GetComponent<PathManager>();

    public void SetSignStyle(SpeedLimitSignStyle value)
    {
        style = value ?? SpeedLimitSignStyle.Japanese;
        foreach (var entry in entries.Values) entry.sign.SetStyle(style);
    }

    private void LateUpdate()
    {
        var modes = VisualModeHandler.Instance;
        var camera = Camera.main;
        bool enabled = GameStateManager.Instance != null && GameStateManager.Instance.IsWorldVisible
            && camera != null && modes != null && paths != null
            && (modes.GetFlags(InputObjectType.PATH) & VisualFlag.SPEED_LIMIT_SIGNS) != 0
            && modes.GetMode(InputObjectType.PATH) != VisualMode.MODE_HIDDEN;
        if (!enabled)
        {
            if (canvas != null) canvas.gameObject.SetActive(false);
            showing = false;
            return;
        }
        if (canvas == null) canvas = Utils.CreateOverlayCanvas("PathSpeedLimitCanvas", 68, persistent: false);
        canvas.gameObject.SetActive(true);
        if (!showing || Time.unscaledTime >= nextRefresh)
        {
            Refresh();
            nextRefresh = Time.unscaledTime + .5f;
        }
        showing = true;
        foreach (var entry in entries.Values)
        {
            var pathObject = paths.GetPathObject(entry.id);
            if (pathObject != null) entry.Bind(pathObject);
            Vector3 screen = camera.WorldToScreenPoint(entry.position);
            bool visible = pathObject != null && pathObject.activeInHierarchy && paths.GetPathData(entry.id) != null
                && entry.visual != null && entry.visual.IsVisualVisible
                && entry.IsDetailVisible()
                && !ObjectVisibilityFilter.IsHidden(InputObjectType.PATH, entry.id)
                && screen.z > camera.nearClipPlane && screen.z < camera.farClipPlane
                && screen.x >= 0 && screen.x <= Screen.width && screen.y >= 0 && screen.y <= Screen.height;
            entry.sign.gameObject.SetActive(visible);
            if (!visible) continue;
            entry.sign.rectTransform.anchoredPosition = new Vector2(screen.x, screen.y);
            float distance = Vector3.Distance(camera.transform.position, entry.position);
            float scale = Mathf.Clamp(100f / Mathf.Max(distance, .1f), .3f, 1.5f);
            entry.sign.rectTransform.localScale = new Vector3(scale, scale, 1f);
        }
    }

    private void Refresh()
    {
        seen.Clear();
        // 常駐範囲と同じ上限でセル逆引きする。都市全件の走査や道路ごとの追加HTTP取得は行わない。
        Vector3 focus = CameraCellChecker.Instance != null ? CameraCellChecker.Instance.CurrentWorldPosition : Camera.main.transform.position;
        Include(paths.GetPathsNearCircle(focus, LodConfig.MAX_FOCUS_RADIUS + TerrainManager.CellSize));
        Include(paths.GetPathsNearCircle(Camera.main.transform.position, LodConfig.PRELOAD_MARGIN + TerrainManager.CellSize));
        removed.Clear();
        foreach (int id in entries.Keys) if (!seen.Contains(id)) removed.Add(id);
        foreach (int id in removed)
        {
            Destroy(entries[id].sign.gameObject);
            entries.Remove(id);
        }
    }

    private void Include(List<PathSummary> candidates)
    {
        foreach (var path in candidates)
        {
            if (!TryGetSpeedLimit(path, out float speed) || !seen.Add(path.path_id)) continue;
            var pathObject = paths.GetPathObject(path.path_id);
            if (pathObject == null || !pathObject.activeInHierarchy || !paths.TryGetPathLabelPosition(path.path_id, out var position))
            {
                seen.Remove(path.path_id);
                continue;
            }
            Vector3 screen = Camera.main.WorldToScreenPoint(position);
            if (screen.z <= Camera.main.nearClipPlane || screen.z >= Camera.main.farClipPlane
                || screen.x < -100 || screen.x > Screen.width + 100 || screen.y < -100 || screen.y > Screen.height + 100)
            {
                seen.Remove(path.path_id);
                continue;
            }
            if (!entries.TryGetValue(path.path_id, out var entry))
            {
                entry = new Entry { id = path.path_id, sign = SpeedLimitSign.Create(canvas.transform) };
                entry.sign.SetStyle(style);
                entries.Add(path.path_id, entry);
            }
            entry.position = position;
            entry.Bind(pathObject);
            entry.sign.SetSpeed(speed);
        }
    }

    public static bool TryGetSpeedLimit(PathSummary path, out float speed)
    {
        speed = 0f;
        if (path == null || (path.path_type != Constants.PATH_STREET && path.path_type != Constants.PATH_MOTORWAY
            && path.path_type != Constants.PATH_TRACK && path.path_type != Constants.PATH_MONORAIL)) return false;
        if (path.max_speed == null) return false;
        speed = GeneratedModelUtils.ToFloat(path.max_speed);
        return speed > 0f && !float.IsNaN(speed) && !float.IsInfinity(speed);
    }

    public void Clear()
    {
        foreach (var entry in entries.Values) Destroy(entry.sign.gameObject);
        entries.Clear();
        if (canvas != null) canvas.gameObject.SetActive(false);
        showing = false;
        nextRefresh = 0f;
    }

    private void OnDisable() => Clear();
    private void OnDestroy()
    {
        if (canvas != null) Destroy(canvas.gameObject);
    }
}
