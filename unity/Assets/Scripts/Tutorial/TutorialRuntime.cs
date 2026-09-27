using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>都市データへ登録しない案内と入力制約。シナリオ進行はTauriが所有する。</summary>
public sealed class TutorialRuntime : MonoBehaviour
{
    [Serializable] public sealed class PathRule { public float[][] points; public float radius; public int variant; public string bind; }
    [Serializable] public sealed class WorldRule { public string mode; public float[][] markers; public PathRule path; }
    [Serializable] public sealed class Directive { public bool active; public string runId; public int revision; public WorldRule world; }
    public static TutorialRuntime Instance { get; private set; }
    private Directive current;
    private readonly HashSet<string> retiredRuns = new HashSet<string>();
    private OverlayMeshRenderer overlay;
    public static bool BlocksWorldInput => Instance != null && Instance.current?.active == true && Instance.current.world.mode == "blocked";
    public static bool PathRestricted => Instance != null && Instance.current?.active == true && Instance.current.world.path != null;
    public static PathRule CurrentPath => PathRestricted ? Instance.current.world.path : null;

    public static void ApplyFromIpc(Dictionary<string, object> data)
    {
        var directive = JsonConvert.DeserializeObject<Directive>(JsonConvert.SerializeObject(data));
        if (Instance == null)
        {
            if (directive == null || !directive.active) return;
            var go = new GameObject("TutorialRuntime");
            DontDestroyOnLoad(go);
            go.AddComponent<TutorialRuntime>();
        }
        Instance.Apply(directive);
    }
    private void Awake() { Instance = this; }
    public void Apply(Directive directive)
    {
        if (directive == null || string.IsNullOrEmpty(directive.runId)) return;
        if (retiredRuns.Contains(directive.runId)) return;
        if (current != null && current.runId == directive.runId && directive.revision <= current.revision) return;
        if (!directive.active && current != null && current.runId != directive.runId) return;
        if (directive.active && !Valid(directive.world)) { Debug.LogError("TutorialRuntime: invalid directive"); Clear(); return; }
        if (current != null && current.runId != directive.runId) retiredRuns.Add(current.runId);
        current = directive;
        if (!directive.active) { retiredRuns.Add(directive.runId); HideOverlay(); return; }
        if (PathRestricted) FindAnyObjectByType<RoadPlanManager>()?.ApplyTutorialPathSettings();
        IpcClient.Instance?.EmitTutorialApplied(directive.runId, directive.revision);
    }
    private static bool ValidPoint(float[] p) => p != null && p.Length == 3 && Array.TrueForAll(p, n => !float.IsNaN(n) && !float.IsInfinity(n));
    public static bool Valid(WorldRule rule)
    {
        if (rule == null || (rule.mode != "observe" && rule.mode != "blocked" && rule.mode != "path")) return false;
        if (rule.markers != null && !Array.TrueForAll(rule.markers, ValidPoint)) return false;
        if (rule.mode == "path" && rule.path == null) return false;
        return rule.path == null || (rule.path.points?.Length == 2 && Array.TrueForAll(rule.path.points, ValidPoint)
            && rule.path.radius > 0 && !float.IsInfinity(rule.path.radius) && rule.path.variant > 0
            && Array.TrueForAll(rule.path.points, p => p[1] == 0)
            && Vector2.Distance(new Vector2(rule.path.points[0][0], rule.path.points[0][2]), new Vector2(rule.path.points[1][0], rule.path.points[1][2])) >= 1);
    }
    private static Vector3 GroundPoint(float[] p) { var pos = new Vector3(p[0], 0, p[2]); pos.y = Utils.GetGroundHeight(pos) + p[1]; return pos; }
    /// <summary>始終点を左クリックで置き、2点を置いた後の右クリックで形状を確定する。</summary>
    public static bool AllowRoadClick(Vector3 raw, int count, bool rightClick, int variant, bool placing)
    {
        if (!PathRestricted) return !BlocksWorldInput;
        if (Instance.current.world.mode != "path" || variant != CurrentPath.variant || !placing) return false;
        // 右クリックは配置済みの終点を確定するため、ポインタの位置を制限しない。
        if (rightClick) return count == 2;
        if (count < 0 || count > 1) return false;
        return TrySnap(raw, count, out _);
    }
    public static bool TrySnap(Vector3 raw, int index, out Vector3 position)
    {
        position = raw;
        if (!PathRestricted || index < 0 || index > 1) return false;
        var p = CurrentPath.points[index];
        if (Vector2.Distance(new Vector2(raw.x, raw.z), new Vector2(p[0], p[2])) > CurrentPath.radius) return false;
        position = GroundPoint(p); return true;
    }
    public static bool AcceptsPath(IList<PathBuildPoint> points, int variant, bool curved)
    {
        if (!PathRestricted) return !BlocksWorldInput;
        if (Instance.current.world.mode != "path" || points.Count != 2 || curved || variant != CurrentPath.variant) return false;
        for (int i = 0; i < 2; i++)
            if (Vector3.Distance(points[i].position, GroundPoint(CurrentPath.points[i])) > .05f || Mathf.Abs(points[i].embankmentHeight) > .001f) return false;
        return true;
    }
    private void LateUpdate()
    {
        if (current?.active != true || Camera.main == null) { HideOverlay(); return; }
        if (overlay == null) overlay = OverlayMeshRenderer.Create("TutorialMarkers", true, transform);
        overlay.gameObject.SetActive(true);
        overlay.BeginFrame();
        if (current.world.path != null)
        {
            // 画面上の固定サイズではなく、実際に受け付ける半径を地面へ描く。
            for (int index = 0; index < 2; index++)
            {
                var center = GroundPoint(current.world.path.points[index]) + Vector3.up * .1f;
                var color = index == 0 ? Color.cyan : Color.yellow;
                for (int i = 0; i < 48; i++)
                {
                    float a = i * Mathf.PI * 2 / 48, b = (i + 1) * Mathf.PI * 2 / 48;
                    overlay.AddSolidLine(center + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * current.world.path.radius,
                        center + new Vector3(Mathf.Cos(b), 0, Mathf.Sin(b)) * current.world.path.radius, color, 3);
                }
            }
            overlay.AddDashedLine(GroundPoint(current.world.path.points[0]) + Vector3.up * .1f, GroundPoint(current.world.path.points[1]) + Vector3.up * .1f, Color.yellow, 3);
        }
        else foreach (var marker in current.world.markers ?? Array.Empty<float[]>()) overlay.AddCircle(GroundPoint(marker) + Vector3.up * .1f, 14f, Color.yellow);
        overlay.EndFrame(Camera.main);
    }
    private void HideOverlay() { if (overlay != null) overlay.gameObject.SetActive(false); }
    public static void Clear()
    {
        if (Instance == null) return;
        if (Instance.current != null) Instance.retiredRuns.Add(Instance.current.runId);
        Instance.current = null;
        Instance.HideOverlay();
    }
    private void OnDisable() { Clear(); }
    private void OnDestroy() { if (Instance == this) Instance = null; }
}
