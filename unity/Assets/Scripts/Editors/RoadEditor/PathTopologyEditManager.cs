using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// Singleton for selecting targets in the 3D view for path merging/splitting.
///
/// Starts with `STATE_MERGE_PATH` / `STATE_SPLIT_PATH` from the path detail panel buttons.
/// - Merge: colors candidates connected to the target by a shared node and reports the clicked path to the UI.
/// - Split: marks the cursor projected onto the target centerline and reports the clicked point to the UI.
/// The UI (Tauri) directly requests subsequent length previews (dry_run) and confirmation
/// through `POST /paths/merge` and `POST /paths/{id}/split` (same responsibility split as construction panels).
///
/// The backend makes the final merge decision (deflection angle, direction, self-loops, etc.). Candidate
/// filtering here checks only clear prerequisites: a shared node with no other paths connected
/// and the same variant. Other rejection reasons appear in the panel
/// as dry_run errors.
/// </summary>
public class PathTopologyEditManager : MonoBehaviour
{
    public static PathTopologyEditManager Instance { get; private set; }

    private enum EditMode { Inactive, Merge, Split }

    // Target path color (path open in the detail panel). The survivor for merging, source for splitting.
    private static readonly Color TargetColor = new Color(0.25f, 0.70f, 1.00f);
    // Merge candidate path color.
    private static readonly Color CandidateColor = new Color(1.00f, 0.85f, 0.20f);
    // Color of the selected merge partner.
    private static readonly Color SelectedColor = new Color(0.30f, 1.00f, 0.45f);

    // Split marker color (cursor-following candidate / confirmed selection).
    private static readonly Color SplitHoverColor = new Color(1.00f, 1.00f, 1.00f, 0.75f);
    private static readonly Color SplitSelectedColor = new Color(0.30f, 1.00f, 0.45f);

    // Maximum cursor distance from the centerline accepted for splitting (m). Within half the path width plus this margin,
    // clicking terrain beside the path selects the perpendicular projection as the split point.
    private const float SplitPickMarginMeters = 20f;

    // Split marker crossbar length relative to path width.
    private const float SplitMarkerWidthScale = 1.6f;

    private EditMode mode = EditMode.Inactive;
    private int targetPathId = -1;

    // Merge: candidate path IDs passing the preliminary filter and the currently selected partner.
    private readonly HashSet<int> mergeCandidates = new HashSet<int>();
    private int selectedMergePathId = -1;

    // Split: target centerline (terrain-following), cursor-following candidate, and confirmed selection.
    private List<Vector3> targetPolyline;
    private float targetWidth = 6f;
    private Vector3? hoverSplitPoint;
    private Vector3? selectedSplitPoint;

    // Keep colored paths' VisualModeObjects to restore their colors.
    private readonly List<VisualModeObject> tintedObjects = new List<VisualModeObject>();

    private PathManager pathManager;
    private Camera gameCamera;
    private OverlayMeshRenderer overlayRenderer;
    private GameObject overlayMeshObj;

    public bool IsActive => mode != EditMode.Inactive;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        gameCamera = Camera.main;
        pathManager = FindAnyObjectByType<PathManager>();
        EnsureOverlayRenderer();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void LateUpdate()
    {
        if (mode != EditMode.Split) return;
        UpdateSplitHoverPoint();
        DrawSplitOverlay();
    }

    private void EnsureOverlayRenderer()
    {
        if (overlayMeshObj != null) return;

        // Always draw split markers in front of roads/terrain, as with RoadPlanManager gizmos.
        overlayRenderer = OverlayMeshRenderer.Create("PathTopologyEditOverlayMesh", alwaysOnTop: true);
        overlayMeshObj = overlayRenderer.gameObject;
    }

    // =====================================================================
    // Mode transitions
    // =====================================================================

    /// <summary>
    /// Starts merge partner selection. Colors the target blue and potential merge partners yellow.
    /// </summary>
    public void BeginMergeMode(int pathId)
    {
        ResetState();
        mode = EditMode.Merge;
        targetPathId = pathId;

        CollectMergeCandidates();
        ApplyMergeTints();

        if (mergeCandidates.Count == 0)
        {
            TooltipManager.Instance?.Show("このパスに結合できるパスがありません");
        }
        Debug.Log($"PathTopologyEditManager: パス結合モード開始 target={pathId}, candidates={mergeCandidates.Count}");
    }

    /// <summary>
    /// Starts split-point selection. Colors the target blue and fetches its centerline for projection.
    /// </summary>
    public void BeginSplitMode(int pathId)
    {
        ResetState();
        mode = EditMode.Split;
        targetPathId = pathId;

        PathManager pm = GetPathManager();
        if (pm != null)
        {
            if (!pm.TryGetPathPolyline(pathId, out targetPolyline))
            {
                targetPolyline = null;
            }
            PathSummary target = pm.GetPathData(pathId);
            if (target != null && target.width > 0f) targetWidth = target.width;
            Tint(pm.GetPathObject(pathId), TargetColor);
        }

        Debug.Log($"PathTopologyEditManager: パス分割モード開始 target={pathId}, 中心線点数={targetPolyline?.Count ?? 0}");
    }

    /// <summary>
    /// Ends selection mode and restores colors/markers. Called on transition to STATE_NORMAL.
    /// </summary>
    public void EndMode()
    {
        if (mode == EditMode.Inactive) return;
        ResetState();
        Debug.Log("PathTopologyEditManager: パス結合/分割の選択モードを終了しました");
    }

    private void ResetState()
    {
        mode = EditMode.Inactive;
        targetPathId = -1;
        mergeCandidates.Clear();
        selectedMergePathId = -1;
        targetPolyline = null;
        hoverSplitPoint = null;
        selectedSplitPoint = null;
        ClearTints();

        // Explicitly draw an empty frame to clear the overlay here, since LateUpdate stops running.
        if (gameCamera == null) gameCamera = Camera.main;
        if (overlayRenderer != null && gameCamera != null)
        {
            overlayRenderer.BeginFrame();
            overlayRenderer.EndFrame(gameCamera);
        }
    }

    // =====================================================================
    // Input (called by PathInputHandler / TerrainInputHandler)
    // =====================================================================

    /// <summary>
    /// A path was clicked in merge mode. Select and report it if it is a candidate.
    /// </summary>
    public void OnPathClickedForMerge(int pathId)
    {
        if (mode != EditMode.Merge) return;

        if (pathId == targetPathId)
        {
            TooltipManager.Instance?.Show("結合する相手のパスを選んでください");
            return;
        }
        if (!mergeCandidates.Contains(pathId))
        {
            TooltipManager.Instance?.Show("このパスとは結合できません");
            return;
        }

        selectedMergePathId = pathId;
        ApplyMergeTints();

        PathSummary selected = GetPathManager()?.GetPathData(pathId);
        IpcClient.Instance?.EmitPathMergeTargetSelected(pathId, selected?.name ?? "");
    }

    /// <summary>
    /// A click in split mode. Report the cursor projected onto the centerline as the split point.
    /// Both clicks on the path and on terrain beside it enter here.
    /// </summary>
    public void OnClickForSplit()
    {
        if (mode != EditMode.Split) return;

        if (!hoverSplitPoint.HasValue)
        {
            TooltipManager.Instance?.Show("分割するパスの上をクリックしてください");
            return;
        }

        selectedSplitPoint = hoverSplitPoint;
        Vector3 p = selectedSplitPoint.Value;
        IpcClient.Instance?.EmitPathSplitPointSelected(p);
    }

    /// <summary>Whether the specified path is a hover-highlight target (=candidate) in merge mode.</summary>
    public bool IsMergeCandidate(int pathId)
    {
        return mode == EditMode.Merge && mergeCandidates.Contains(pathId);
    }

    // =====================================================================
    // Merge: candidate filtering and coloring
    // =====================================================================

    /// <summary>
    /// Collects potential merge partners from both target endpoint nodes.
    /// Requires exactly 2 paths at the shared node (including the target), active status, and the same variant.
    /// Backend dry_run checks other conditions, such as curve kinks and conflicting directions.
    /// </summary>
    private void CollectMergeCandidates()
    {
        mergeCandidates.Clear();

        PathManager pm = GetPathManager();
        if (pm == null) return;
        PathSummary target = pm.GetPathData(targetPathId);
        if (target == null) return;

        foreach (int nodeId in new[] { target.start_node_id, target.end_node_id })
        {
            if (nodeId <= 0) continue;

            List<PathSummary> connected = pm.GetPathsConnectedToNode(nodeId);
            // Cannot merge if 2 or more other paths connect to the shared node (would break the intersection).
            if (connected.Count != 2) continue;

            foreach (PathSummary other in connected)
            {
                if (other == null || other.path_id == targetPathId) continue;
                if (other.status != Constants.STATUS_ACTIVE) continue;
                if (other.path_variant != target.path_variant) continue;
                mergeCandidates.Add(other.path_id);
            }
        }
    }

    /// <summary>Recolors the target, candidates, and selected path with their respective colors.</summary>
    private void ApplyMergeTints()
    {
        ClearTints();

        PathManager pm = GetPathManager();
        if (pm == null) return;

        Tint(pm.GetPathObject(targetPathId), TargetColor);
        foreach (int pathId in mergeCandidates)
        {
            Tint(pm.GetPathObject(pathId), pathId == selectedMergePathId ? SelectedColor : CandidateColor);
        }
    }

    private void Tint(GameObject go, Color color)
    {
        if (go == null) return;
        var vmo = go.GetComponent<VisualModeObject>();
        if (vmo == null) vmo = go.GetComponentInChildren<VisualModeObject>(true);
        if (vmo == null) return;
        vmo.SetInterferenceHighlight(color);
        tintedObjects.Add(vmo);
    }

    private void ClearTints()
    {
        foreach (var vmo in tintedObjects)
        {
            if (vmo != null) vmo.SetInterferenceHighlight(null);
        }
        tintedObjects.Clear();
    }

    // =====================================================================
    // Split: cursor projection and marker drawing
    // =====================================================================

    /// <summary>
    /// Projects the world cursor onto the target centerline and updates the candidate if within range.
    /// The range is half the path width + SplitPickMarginMeters; hide the marker outside it.
    /// </summary>
    private void UpdateSplitHoverPoint()
    {
        hoverSplitPoint = null;
        if (targetPolyline == null || targetPolyline.Count < 2) return;
        if (InputHandler.Instance == null) return;

        Vector3 cursor = new Vector3(InputHandler.Instance.posX, 0f, InputHandler.Instance.posZ);
        Vector3 projected = ProjectOntoPolyline(targetPolyline, cursor, out float distance);
        if (distance > targetWidth * 0.5f + SplitPickMarginMeters) return;

        hoverSplitPoint = projected;
    }

    /// <summary>
    /// Finds the closest XZ point to p on a polyline (centerline). Linearly interpolates Y along the segment.
    /// </summary>
    private static Vector3 ProjectOntoPolyline(List<Vector3> polyline, Vector3 p, out float distance)
    {
        Vector3 best = polyline[0];
        float bestSqr = float.MaxValue;

        for (int i = 0; i < polyline.Count - 1; i++)
        {
            Vector3 a = polyline[i];
            Vector3 b = polyline[i + 1];
            float t = PolylineUtils.ProjectOnSegmentXZ(p, a, b);
            Vector3 point = Vector3.Lerp(a, b, t);

            float dx = p.x - point.x;
            float dz = p.z - point.z;
            float sqr = dx * dx + dz * dz;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                best = point;
            }
        }

        distance = Mathf.Sqrt(bestSqr);
        return best;
    }

    /// <summary>
    /// Draws the split marker (cross-path bar + circle). Confirmed selection is green; cursor candidate is white.
    /// </summary>
    private void DrawSplitOverlay()
    {
        if (overlayRenderer == null) return;
        if (gameCamera == null) gameCamera = Camera.main;
        if (gameCamera == null) return;

        overlayRenderer.BeginFrame();

        if (selectedSplitPoint.HasValue)
        {
            DrawSplitMarker(selectedSplitPoint.Value, SplitSelectedColor, 3f, 9f);
        }
        // Avoid drawing twice when the candidate is almost identical to the confirmed point.
        if (hoverSplitPoint.HasValue &&
            (!selectedSplitPoint.HasValue || (hoverSplitPoint.Value - selectedSplitPoint.Value).sqrMagnitude > 0.25f))
        {
            DrawSplitMarker(hoverSplitPoint.Value, SplitHoverColor, 2f, 6f);
        }

        overlayRenderer.EndFrame(gameCamera);
    }

    /// <summary>Draws a circle and bar perpendicular to the path direction at the specified point.</summary>
    private void DrawSplitMarker(Vector3 point, Color color, float lineThickness, float circleRadius)
    {
        Vector3 tangent = TangentAt(point);
        Vector3 normal = new Vector3(-tangent.z, 0f, tangent.x);
        float half = Mathf.Max(2f, targetWidth * 0.5f * SplitMarkerWidthScale);

        Vector3 lifted = point + Vector3.up * 0.3f;
        overlayRenderer.AddSolidLine(lifted - normal * half, lifted + normal * half, color, lineThickness);
        overlayRenderer.AddCircle(lifted, circleRadius, color);
    }

    /// <summary>Returns the direction at a centerline point (unit vector in XZ).</summary>
    private Vector3 TangentAt(Vector3 point)
    {
        if (targetPolyline == null || targetPolyline.Count < 2) return Vector3.forward;

        int bestIndex = 0;
        float bestSqr = float.MaxValue;
        for (int i = 0; i < targetPolyline.Count - 1; i++)
        {
            Vector3 mid = (targetPolyline[i] + targetPolyline[i + 1]) * 0.5f;
            float dx = mid.x - point.x;
            float dz = mid.z - point.z;
            float sqr = dx * dx + dz * dz;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                bestIndex = i;
            }
        }

        Vector3 dir = targetPolyline[bestIndex + 1] - targetPolyline[bestIndex];
        dir.y = 0f;
        return dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;
    }

    private PathManager GetPathManager()
    {
        if (pathManager == null) pathManager = FindAnyObjectByType<PathManager>();
        return pathManager;
    }
}
