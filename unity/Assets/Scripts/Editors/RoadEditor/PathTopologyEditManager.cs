using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// パスの結合(merge)・分割(split)における「3Dビュー上での対象選択」を司るシングルトン。
///
/// パス詳細パネルの結合/分割ボタンから `STATE_MERGE_PATH` / `STATE_SPLIT_PATH` で開始し、
/// - 結合: 対象パスと共有ノードで繋がる候補パスを色分けし、クリックされた1本をUIへ通知する
/// - 分割: 対象パスの中心線へカーソルを射影した点をマーカー表示し、クリックされた点をUIへ通知する
/// を担う。選択後の長さプレビュー(dry_run)と確定送信はUI(Tauri)側が
/// `POST /paths/merge`・`POST /paths/{id}/split` へ直接行う(建設パネルと同じ責務分担)。
///
/// 結合可否の最終判定はバックエンドが持つ(偏向角・通行方向・自己ループ等)。ここでの候補
/// 絞り込みは「共有ノードがあり、そのノードに他のパスが繋がっておらず、同一バリアント」
/// という明らかな条件だけを見る前段フィルタで、残りの拒否理由はdry_runのエラーとして
/// パネルに表示される。
/// </summary>
public class PathTopologyEditManager : MonoBehaviour
{
    public static PathTopologyEditManager Instance { get; private set; }

    private enum EditMode { Inactive, Merge, Split }

    // 操作対象(パス詳細パネルで開いているパス)の色。結合では survivor、分割では分割元。
    private static readonly Color TargetColor = new Color(0.25f, 0.70f, 1.00f);
    // 結合候補パスの色。
    private static readonly Color CandidateColor = new Color(1.00f, 0.85f, 0.20f);
    // 選択済み(結合相手として選ばれた)パスの色。
    private static readonly Color SelectedColor = new Color(0.30f, 1.00f, 0.45f);

    // 分割点マーカーの色(カーソル追従の候補点 / 確定済みの選択点)。
    private static readonly Color SplitHoverColor = new Color(1.00f, 1.00f, 1.00f, 0.75f);
    private static readonly Color SplitSelectedColor = new Color(0.30f, 1.00f, 0.45f);

    // 分割点として受け付けるカーソルと中心線の最大距離(m)。パス幅の半分にこの値を足した
    // 範囲内なら、パスの脇の地形をクリックしても垂線の足が分割点になる。
    private const float SplitPickMarginMeters = 20f;

    // 分割点マーカーの、パス幅に対する横棒の長さ倍率。
    private const float SplitMarkerWidthScale = 1.6f;

    private EditMode mode = EditMode.Inactive;
    private int targetPathId = -1;

    // 結合: 前段フィルタを通過した候補パスIDと、現在選択中の相手。
    private readonly HashSet<int> mergeCandidates = new HashSet<int>();
    private int selectedMergePathId = -1;

    // 分割: 対象パスの中心線(地形追従済み)と、カーソル追従の候補点・確定済みの選択点。
    private List<Vector3> targetPolyline;
    private float targetWidth = 6f;
    private Vector3? hoverSplitPoint;
    private Vector3? selectedSplitPoint;

    // 色を戻すために着色したパスの VisualModeObject を保持する。
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

        // 分割点マーカーは道路面や起伏に埋もれず常に手前に出す(RoadPlanManager のギズモと同じ)。
        overlayRenderer = OverlayMeshRenderer.Create("PathTopologyEditOverlayMesh", alwaysOnTop: true);
        overlayMeshObj = overlayRenderer.gameObject;
    }

    // =====================================================================
    // モード遷移
    // =====================================================================

    /// <summary>
    /// パス結合の相手選択モードを開始する。対象パスを青、結合できる候補を黄で着色する。
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
    /// パス分割の分割点選択モードを開始する。対象パスを青で着色し、中心線を射影用に取得する。
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
    /// 選択モードを終了し、着色とマーカーを元に戻す。STATE_NORMAL への遷移で呼ばれる。
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

        // オーバーレイは LateUpdate が回らなくなるため、ここで明示的に空フレームを描いて消す。
        if (gameCamera == null) gameCamera = Camera.main;
        if (overlayRenderer != null && gameCamera != null)
        {
            overlayRenderer.BeginFrame();
            overlayRenderer.EndFrame(gameCamera);
        }
    }

    // =====================================================================
    // 入力(PathInputHandler / TerrainInputHandler から呼ばれる)
    // =====================================================================

    /// <summary>
    /// 結合モードでパスがクリックされた。候補パスなら選択して UI へ通知する。
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
    /// 分割モードでクリックされた。カーソル位置を中心線へ射影した点を分割点として UI へ通知する。
    /// パス上のクリックと、パス脇の地形クリックの双方からこのメソッドに入る。
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

    /// <summary>結合モードで、指定パスがホバーハイライトの対象(=候補)かどうか。</summary>
    public bool IsMergeCandidate(int pathId)
    {
        return mode == EditMode.Merge && mergeCandidates.Contains(pathId);
    }

    // =====================================================================
    // 結合: 候補の絞り込みと着色
    // =====================================================================

    /// <summary>
    /// 対象パスの両端ノードから、結合できる可能性のあるパスを集める。
    /// 条件は「共有ノードに繋がるパスが対象を含めて2本だけ」「稼働中」「同一バリアント」。
    /// 曲線の折れ・通行方向の矛盾などの残りの条件はバックエンドが dry_run で判定する。
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
            // 共有ノードに対象以外のパスが2本以上ぶら下がっていると結合できない(交差点が壊れるため)。
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

    /// <summary>対象パス・候補パス・選択済みパスをそれぞれの色で塗り直す。</summary>
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
    // 分割: カーソルの射影とマーカー描画
    // =====================================================================

    /// <summary>
    /// カーソルのワールド位置を対象パスの中心線へ射影し、受付範囲内なら候補点を更新する。
    /// 受付範囲は「パス幅の半分 + SplitPickMarginMeters」で、範囲外ならマーカーを出さない。
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
    /// 折れ線(中心線)上で、点 p にもっとも近い位置をXZ平面で求める。Yは線分上で線形補間する。
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
    /// 分割点マーカー(パスを横切る棒＋円)を描く。確定済みの選択点は緑、カーソル追従の候補点は白。
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
        // 候補点が確定点とほぼ同じなら二重描画しない。
        if (hoverSplitPoint.HasValue &&
            (!selectedSplitPoint.HasValue || (hoverSplitPoint.Value - selectedSplitPoint.Value).sqrMagnitude > 0.25f))
        {
            DrawSplitMarker(hoverSplitPoint.Value, SplitHoverColor, 2f, 6f);
        }

        overlayRenderer.EndFrame(gameCamera);
    }

    /// <summary>指定点に、パスの進行方向と直交する棒と円のマーカーを描く。</summary>
    private void DrawSplitMarker(Vector3 point, Color color, float lineThickness, float circleRadius)
    {
        Vector3 tangent = TangentAt(point);
        Vector3 normal = new Vector3(-tangent.z, 0f, tangent.x);
        float half = Mathf.Max(2f, targetWidth * 0.5f * SplitMarkerWidthScale);

        Vector3 lifted = point + Vector3.up * 0.3f;
        overlayRenderer.AddSolidLine(lifted - normal * half, lifted + normal * half, color, lineThickness);
        overlayRenderer.AddCircle(lifted, circleRadius, color);
    }

    /// <summary>中心線上の点における進行方向(単位ベクトル、XZ平面)を返す。</summary>
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
