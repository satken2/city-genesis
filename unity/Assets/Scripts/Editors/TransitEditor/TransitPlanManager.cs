using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// TransitPlanManager:
/// 公共交通の路線設置エディタ。RoadPlanManager と似た操作感で、3Dマップ上に停留所(ノード)を
/// 順番に配置していく。区間はベジエではなく停留所間の直線で、標高・盛土・制御点は持たない。
///
/// 操作フロー(RoadPlanManager に準じる):
///   Placing: 左クリックで停留所を追加。右クリックで確定して Editing へ。
///   Editing: 停留所をドラッグで移動、区間(辺)クリックで停留所を挿入、停留所を右クリックで削除。
///
/// 停留所の設置制約:
///   停留所は路線種別の stop_path_variants に属する稼働中パス(駅・街路など)の「上」にのみ置ける
///   (中心線への射影距離が「パス幅/2 + STOP_ON_PATH_MARGIN(2m)」以内)。バックエンドの
///   crud_transit._resolve_stop_paths と同じ制約で、これを満たさないクリックは停留所を配置しない
///   (確定時に ValidationError にならないようにするため)。
///
/// 停留所の配置・編集のたびに、停留所座標列を IPC(edit_transit:update_stops)で UI へ送出する。
/// 実際の POST /api/v1/transit/lines は UI(TransitCreationPanel)側が行うため、本マネージャは
/// 停留所の組み立てと UI への逐次通知のみを担う(道路建設で Unity が POST するのとは責務が異なる)。
///
/// IpcStateCoordinator が STATE_EDIT_TRANSIT 遷移時に動的生成・初期化する。シーンに事前配置する
/// 必要はなく、依存(カメラ・PathManager・オーバーレイ用マテリアル)は実行時に自己解決する。
/// </summary>
public class TransitPlanManager : MonoBehaviour
{
    private enum Phase { Inactive, Placing, Editing }

    // 停留所が下地パス上にあると判定する、中心線からの許容距離の余裕(m)。
    // バックエンドの transit_route.STOP_ON_PATH_MARGIN と一致させる。
    private const float STOP_ON_PATH_MARGIN = 2f;

    // 停留所吸着の探索半径(m)。この範囲の下地パスセグメントを候補にする。
    private const float SNAP_SEARCH_RADIUS = 40f;

    // オーバーレイ描画パラメータ(ピクセル単位)。
    private const float STOP_MARKER_HALF_SIZE = 6f;
    private const float STOP_HIT_RADIUS = 12f;
    private const float LINE_THICKNESS = 2.5f;

    // 停留所配置の見た目(路線設置中のギズモ色)。
    private static readonly Color LineColor = new Color(0.2f, 0.85f, 1f, 0.95f);
    private static readonly Color StopColor = new Color(1f, 1f, 1f, 1f);
    private static readonly Color SelectedStopColor = new Color(1f, 0.85f, 0.2f, 1f);
    private static readonly Color PreviewColor = new Color(0.2f, 0.85f, 1f, 0.5f);

    private Phase phase = Phase.Inactive;

    // 選択中の路線種別(区間パスのバリアント=transit_master の transit_path_id)。
    private int pathVariant = 0;
    // 停留所を設置できる下地パスのバリアント集合。
    private readonly HashSet<int> stopPathVariants = new HashSet<int>();

    // 配置済みの停留所(ワールド座標。地形高さ込み)。
    private readonly List<Vector3> stops = new List<Vector3>();

    private int selectedStopIndex = -1;
    private bool isDragging = false;
    private int dragStopIndex = -1;

    // ホバー中の停留所プレビュー(下地パス上に吸着できたときのみ有効)。
    private bool hoverValid = false;
    private Vector3 hoverPos;

    private Camera gameCamera;
    private PathManager pathManager;

    private OverlayMeshRenderer overlayRenderer;
    private GameObject overlayMeshObj;

    public bool IsEditingPhase => phase == Phase.Editing;

    // =====================================================================
    // ライフサイクル
    // =====================================================================

    private void Awake()
    {
        gameCamera = Camera.main;
        pathManager = FindAnyObjectByType<PathManager>();
        EnsureOverlayRenderer();
    }

    private void LateUpdate()
    {
        if (phase == Phase.Inactive) return;
        UpdateOverlay();
    }

    private void EnsureOverlayRenderer()
    {
        if (overlayMeshObj != null) return;

        overlayMeshObj = new GameObject("TransitEditorOverlayMesh");
        overlayRenderer = overlayMeshObj.AddComponent<OverlayMeshRenderer>();
        Material mat = new Material(Shader.Find("Custom/OverlayLine"));
        // 停留所マーカーや区間線は地形に埋もれず常に手前に出す(RoadPlanManager のギズモと同じ)。
        mat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
        overlayRenderer.Initialize(mat);
    }

    // =====================================================================
    // パブリックAPI: モード遷移
    // =====================================================================

    /// <summary>
    /// 路線設置モードを初期化する。
    /// pathVariant は区間パスのバリアント(=transit_master の transit_path_id)、
    /// stopVariants は停留所を設置できる下地パスのバリアント一覧。
    /// </summary>
    public void InitializeTransitPlacement(int pathVariant, int[] stopVariants)
    {
        EnsureOverlayRenderer();
        if (gameCamera == null) gameCamera = Camera.main;
        if (pathManager == null) pathManager = FindAnyObjectByType<PathManager>();

        this.pathVariant = pathVariant;
        stopPathVariants.Clear();
        if (stopVariants != null)
        {
            foreach (int v in stopVariants) stopPathVariants.Add(v);
        }

        phase = Phase.Placing;
        stops.Clear();
        selectedStopIndex = -1;
        isDragging = false;
        dragStopIndex = -1;
        hoverValid = false;

        EmitStops();
        Debug.Log($"TransitPlanManager: 路線設置モードを初期化 path_variant={pathVariant}, stop_variants={stopPathVariants.Count}");
    }

    /// <summary>
    /// 路線設置モードを終了し、状態とオーバーレイをクリアする。
    /// </summary>
    public void FinishPlacement()
    {
        phase = Phase.Inactive;
        stops.Clear();
        selectedStopIndex = -1;
        isDragging = false;
        dragStopIndex = -1;
        hoverValid = false;
        if (overlayRenderer != null)
        {
            overlayRenderer.ClearMesh();
        }
    }

    // =====================================================================
    // パブリックAPI: 入力ハンドラ(TerrainInputHandler から呼ばれる)
    // =====================================================================

    /// <summary>
    /// 地形左クリック。
    /// Placing: 下地パス上に吸着できれば停留所を追加する。
    /// Editing: 停留所を選択、または区間(辺)上クリックで停留所を挿入する。
    /// </summary>
    public void OnTerrainLeftClick(Vector3 worldPos)
    {
        if (phase == Phase.Placing)
        {
            if (TrySnapStop(worldPos, out Vector3 snapped))
            {
                stops.Add(snapped);
                EmitStops();
            }
            // 下地パス上でなければ配置しない(バックエンドの設置制約に合わせる)。
        }
        else if (phase == Phase.Editing)
        {
            int stopIdx = HitTestStop(InputUtil.MousePosition);
            if (stopIdx >= 0)
            {
                selectedStopIndex = stopIdx;
                return;
            }

            int edgeIdx = HitTestEdge(InputUtil.MousePosition);
            if (edgeIdx >= 0 && TrySnapStop(worldPos, out Vector3 snapped))
            {
                stops.Insert(edgeIdx + 1, snapped);
                selectedStopIndex = edgeIdx + 1;
                EmitStops();
                return;
            }

            selectedStopIndex = -1;
        }
    }

    /// <summary>
    /// 地形右クリック。
    /// Placing: 形状を確定して Editing へ移行する。
    /// Editing: 停留所を削除する。
    /// </summary>
    public void OnTerrainRightClick(Vector3 worldPos)
    {
        if (phase == Phase.Placing)
        {
            if (stops.Count >= 1)
            {
                phase = Phase.Editing;
                selectedStopIndex = -1;
            }
        }
        else if (phase == Phase.Editing)
        {
            int stopIdx = HitTestStop(InputUtil.MousePosition);
            if (stopIdx >= 0)
            {
                stops.RemoveAt(stopIdx);
                if (selectedStopIndex == stopIdx) selectedStopIndex = -1;
                else if (selectedStopIndex > stopIdx) selectedStopIndex--;
                EmitStops();
            }
        }
    }

    /// <summary>
    /// 地形ホバー。Placing 中は次の停留所位置のプレビューに使う。
    /// </summary>
    public void OnTerrainHover(Vector3 worldPos)
    {
        if (phase != Phase.Placing)
        {
            hoverValid = false;
            return;
        }
        hoverValid = TrySnapStop(worldPos, out hoverPos);
    }

    // =====================================================================
    // ドラッグ(InputHandler から呼ばれる)
    // =====================================================================

    /// <summary>
    /// 指定スクリーン座標が停留所マーカー上にあるか判定する。
    /// InputHandler がドラッグ開始対象を決めるために使う。
    /// </summary>
    public bool IsMouseOverStop(Vector2 screenPos, Camera cam)
    {
        if (phase != Phase.Editing) return false;
        return HitTestStop(screenPos) >= 0;
    }

    /// <summary>ドラッグを開始する。カーソル下の停留所を掴む。</summary>
    public void StartDrag(Vector2 screenPos)
    {
        int idx = HitTestStop(screenPos);
        if (idx >= 0)
        {
            isDragging = true;
            dragStopIndex = idx;
            selectedStopIndex = idx;
        }
    }

    /// <summary>ドラッグ中。掴んでいる停留所を下地パス上へ吸着して移動する。</summary>
    public void ProcessMouseDrag(Vector3 worldPos)
    {
        if (!isDragging || dragStopIndex < 0 || dragStopIndex >= stops.Count) return;
        if (TrySnapStop(worldPos, out Vector3 snapped))
        {
            stops[dragStopIndex] = snapped;
            EmitStops();
        }
    }

    /// <summary>ドラッグ終了。</summary>
    public void ProcessMouseDragEnd(Vector3 worldPos)
    {
        if (isDragging)
        {
            isDragging = false;
            dragStopIndex = -1;
            EmitStops();
        }
    }

    // =====================================================================
    // 停留所の吸着(stop_path_variants のパス上への射影)
    // =====================================================================

    /// <summary>
    /// 指定ワールド座標を、停留所を設置できる下地パス(stop_path_variants に属する稼働中パス)の
    /// 中心線へ射影する。射影距離が「パス幅/2 + STOP_ON_PATH_MARGIN」以内の最近傍パスへ吸着する。
    /// 該当パスが無ければ false。
    /// </summary>
    private bool TrySnapStop(Vector3 worldPos, out Vector3 snapped)
    {
        snapped = worldPos;
        if (pathManager == null) return false;

        var segments = pathManager.QueryNearbySegments(worldPos, SNAP_SEARCH_RADIUS);
        if (segments == null || segments.Count == 0) return false;

        float bestDist = float.MaxValue;
        Vector3 bestPoint = worldPos;
        bool found = false;

        foreach (var seg in segments)
        {
            if (seg.path == null) continue;
            if (!stopPathVariants.Contains(seg.path.path_variant)) continue;
            if (seg.path.status != Constants.STATUS_ACTIVE) continue;

            Vector3 proj = PolylineUtils.ClosestPointOnSegmentXZ(worldPos, seg.a, seg.b);
            float dist = HorizontalDistance(worldPos, proj);

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
            bestPoint.y = HeightmapTextureManager.GetTerrainHeight(new Vector3(bestPoint.x, 1000f, bestPoint.z)) + 0.15f;
            snapped = bestPoint;
        }
        return found;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // =====================================================================
    // ヒット判定(スクリーン座標)
    // =====================================================================

    /// <summary>スクリーン座標に最も近い停留所のインデックスを返す(ヒット半径内)。</summary>
    private int HitTestStop(Vector2 screenPos)
    {
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return -1;

        int best = -1;
        float bestDist = STOP_HIT_RADIUS;
        for (int i = 0; i < stops.Count; i++)
        {
            Vector3 sp = cam.WorldToScreenPoint(stops[i]);
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
    /// スクリーン座標が停留所間の区間(辺)上にあるか判定し、辺の始点インデックスを返す。
    /// 見つからなければ -1。
    /// </summary>
    private int HitTestEdge(Vector2 screenPos)
    {
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null || stops.Count < 2) return -1;

        const float edgeHitPixels = 8f;
        int best = -1;
        float bestDist = edgeHitPixels;
        for (int i = 0; i < stops.Count - 1; i++)
        {
            Vector3 s1 = cam.WorldToScreenPoint(stops[i]);
            Vector3 s2 = cam.WorldToScreenPoint(stops[i + 1]);
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
    // IPC 送出
    // =====================================================================

    /// <summary>
    /// 停留所座標列を IPC で UI に通知する。
    /// 各停留所は [x, 0, z](公共交通パスは相対Y=0のため y は0)。
    /// バックエンドの座標系はUnityワールドと同じ扱いのため x,z をそのまま渡す
    /// (資源エリア編集 EmitEditResourceUpdateShape と同じ規約)。
    /// </summary>
    private void EmitStops()
    {
        if (IpcClient.Instance == null) return;
        var payload = new List<List<float>>(stops.Count);
        foreach (var s in stops)
        {
            payload.Add(new List<float> { s.x, 0f, s.z });
        }
        IpcClient.Instance.EmitEditTransitUpdateStops(payload);
    }

    // =====================================================================
    // オーバーレイ描画
    // =====================================================================

    private void UpdateOverlay()
    {
        if (overlayRenderer == null) return;
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        overlayRenderer.BeginFrame();

        // 区間線(停留所間の直線)。
        for (int i = 0; i < stops.Count - 1; i++)
        {
            overlayRenderer.AddSolidLine(stops[i], stops[i + 1], LineColor, LINE_THICKNESS);
        }

        // Placing 中は最後の停留所からホバー位置までのプレビュー線。
        if (phase == Phase.Placing && hoverValid)
        {
            if (stops.Count > 0)
            {
                overlayRenderer.AddSolidLine(stops[stops.Count - 1], hoverPos, PreviewColor, LINE_THICKNESS);
            }
            overlayRenderer.AddSquare(hoverPos, STOP_MARKER_HALF_SIZE, PreviewColor);
        }

        // 停留所マーカー。
        for (int i = 0; i < stops.Count; i++)
        {
            Color c = (i == selectedStopIndex) ? SelectedStopColor : StopColor;
            overlayRenderer.AddSquare(stops[i], STOP_MARKER_HALF_SIZE, c);
        }

        overlayRenderer.EndFrame(cam);
    }
}
