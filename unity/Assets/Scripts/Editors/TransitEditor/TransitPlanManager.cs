using System.Collections;
using System.Collections.Generic;
using GeneratedModels;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// TransitPlanManager:
/// 公共交通の路線設置エディタ。3Dマップ上に路線の巡回点(停留所と経由点)を巡回順に置いていく。
/// 路線は閉じたループで、最後の点の次は先頭の点に戻る(往復運行は行きと帰りの停留所を並べたループ)。
///
/// 操作フロー:
///   Placing: 左クリックで停留所を末尾に追加する。既存の停留所をクリックすると同じ停留所を再び訪れる停留所として
///            追加し、先頭の停留所をクリック(停留所2つ以上)するか右クリックで配置を終えて Editing へ移る。
///   Editing: 停留所・経由点をドラッグで移動、区間(末尾→先頭の区間を含む)をクリックで停留所を挿入、区間をドラッグで
///            経由点を挿入して動かす、停留所・経由点を右クリックで削除。
///
/// 設置制約:
///   停留所は路線種別の stop_path_variants、経由点は走行網(route_path_variants ∪ stop_path_variants)に属する
///   稼働中パスの「上」(中心線への射影距離が「パス幅/2 + STOP_ON_PATH_MARGIN(2m)」以内)にのみ置ける。
///   バックエンドの crud_transit._resolve_route_points と同じ制約で、満たさない操作は点を置かない。
///
/// 経路プレビュー:
///   巡回点が変わるたびに POST /api/v1/transit/lines/route-preview(都市を変えない試算)を少し待ってから呼び、
///   返った区間ごとの走行データ(journeys と同じ形)を TransitRouteGeometry で実際の道路・線路に沿った点列にして、
///   路線色帯(TransitOverlayManager)と同じ見た目の帯で描く。下地でつながらない区間は赤い破線の直線で示す。
///   応答が今の巡回点に追いつくまでは巡回点を結ぶ直線を描く。
///
/// 巡回点が変わるたびに IPC(edit_transit:update_points)で UI へ送る。実際の路線の作成・経路の更新は
/// UI(TransitCreationPanel)側が行うため、本マネージャは巡回点の組み立て・プレビュー・UI への通知のみを担う。
/// 経路編集(既存路線の巡回点から始めるとき)は、その路線の色帯を TransitOverlayManager に隠させる。
///
/// IpcStateCoordinator が STATE_EDIT_TRANSIT 遷移時に動的生成・初期化する。シーンに事前配置する
/// 必要はなく、依存(カメラ・PathManager・オーバーレイ用マテリアル・効果音)は実行時に自己解決する。
/// </summary>
public class TransitPlanManager : MonoBehaviour
{
    private enum Phase { Inactive, Placing, Editing }

    /// <summary>路線の巡回点。Waypoint=false が停留所、true が停車せずに必ず通る経由点。</summary>
    public readonly struct RoutePoint
    {
        public readonly Vector3 Position;
        public readonly bool Waypoint;
        public RoutePoint(Vector3 position, bool waypoint) { Position = position; Waypoint = waypoint; }
    }

    /// <summary>POST /transit/lines/route-preview の応答。legs[k] は停留所 k から次の停留所までの区間(つながらなければ null)。</summary>
    public sealed class RoutePreview
    {
        public bool ok;
        public string reason;
        public List<int> blocked_legs;
        public List<TransitJourney> legs;
    }

    // 停留所・経由点が下地パス上にあると判定する、中心線からの許容距離の余裕(m)。
    // バックエンドの transit_route.STOP_ON_PATH_MARGIN と一致させる。
    private const float STOP_ON_PATH_MARGIN = 2f;

    // 吸着の探索半径(m)。この範囲の下地パスセグメントを候補にする。
    private const float SNAP_SEARCH_RADIUS = 40f;

    // オーバーレイ描画パラメータ(ピクセル単位)。
    private const float STOP_MARKER_HALF_SIZE = 6f;
    private const float WAYPOINT_MARKER_RADIUS = 5f;
    private const float POINT_HIT_RADIUS = 12f;
    private const float EDGE_HIT_PIXELS = 8f;
    private const float LINE_THICKNESS = 2.5f;

    // 経路プレビューを取りに行くまで巡回点の変更が落ち着くのを待つ時間(秒)。ドラッグ中に毎フレーム試算させない。
    private const float PREVIEW_DEBOUNCE_SECONDS = .25f;
    // 経路プレビューの標本間隔(区間の走行距離の単位)。路線色帯(TransitOverlayManager)と同じ。
    private const float PREVIEW_SAMPLE_STEP = 2f;

    private const string PLACE_SOUND = "Audio/Effects/transit-point-place";

    // 路線設置中のギズモ色。
    private static readonly Color LineColor = new Color(0.2f, 0.85f, 1f, 0.95f);
    private static readonly Color StopColor = new Color(1f, 1f, 1f, 1f);
    private static readonly Color FirstStopColor = new Color(0.45f, 0.95f, 0.55f, 1f);
    private static readonly Color WaypointColor = new Color(0.2f, 0.85f, 1f, 1f);
    private static readonly Color SelectedColor = new Color(1f, 0.85f, 0.2f, 1f);
    private static readonly Color PreviewColor = new Color(0.2f, 0.85f, 1f, 0.5f);
    private static readonly Color BlockedColor = new Color(1f, 0.35f, 0.35f, 0.95f);
    private static readonly Color RibbonBorderColor = new Color(.04f, .05f, .07f, 1f);

    private Phase phase = Phase.Inactive;

    // 選択中の路線種別(区間パスのバリアント=transit_master の transit_path_id)。
    private int pathVariant = 0;
    // 停留所を設置できる下地パスのバリアント集合と、経由点を置ける走行網のバリアント集合。
    private readonly HashSet<int> stopPathVariants = new HashSet<int>();
    private readonly HashSet<int> routePathVariants = new HashSet<int>();
    // 経路を編集中の路線(-1=新設)。
    private int lineAgentId = -1;

    // 巡回点(ワールド座標。下地パスの高さ込み)。
    private readonly List<RoutePoint> points = new List<RoutePoint>();

    private int selectedIndex = -1;
    private bool isDragging = false;
    private int dragIndex = -1;
    // 左ボタンを押した画面位置。地形上のドラッグでは StartDrag が呼ばれず ProcessMouseDrag だけが届くので、
    // 最初の ProcessMouseDrag で押した位置から掴む対象を決める(ドラッグ判定の閾値ぶん動いた後の位置では外れうる)。
    private Vector2 pressScreen;
    private bool pressPending = false;
    // 区間のドラッグで挿入した経由点は、一度も下地パスに吸着できないまま離したら取り消す。
    private bool dragInsertedWaypoint = false;
    private bool dragSnapped = false;

    // ホバー中の停留所プレビュー(下地パス上に吸着できたときのみ有効)。
    private bool hoverValid = false;
    private Vector3 hoverPos;

    private Camera gameCamera;
    private PathManager pathManager;
    private TransitRouteGeometry geometry;

    private OverlayMeshRenderer overlayRenderer, ribbonBorder, ribbon;

    // 経路プレビューの取得状態。desired=今の巡回点の要求、sent=送信済み、shown=描画中の応答の要求。
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
    // ライフサイクル
    // =====================================================================

    private void Awake()
    {
        gameCamera = Camera.main;
        pathManager = FindAnyObjectByType<PathManager>();
        EnsureRenderers();
        audioSource = GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();
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
        // 効果音の音量は UI の設定(マスター×効果音)に従う。IpcClient は後から差し替わるので毎フレーム照合する。
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
        // 巡回点のマーカーや直線は地形に埋もれず常に手前に出す(RoadPlanManager のギズモと同じ)。
        if (overlayRenderer == null) overlayRenderer = OverlayMeshRenderer.Create("TransitEditorOverlayMesh", alwaysOnTop: true);
        // 経路プレビューの帯は路線色帯(TransitOverlayManager の 3200/3201)と同じ描き方で、その手前に重ねる。
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
    // パブリックAPI: モード遷移
    // =====================================================================

    /// <summary>
    /// 路線設置モードを初期化する。
    /// pathVariant は区間パスのバリアント(=transit_master の transit_path_id)、stopVariants は停留所を、
    /// routeVariants は経由点を置ける走行網のバリアント一覧。initialPoints があれば(経路編集)その巡回点から
    /// Editing で始め、editedLineId の路線の色帯を隠す。
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
    /// 路線設置モードを終了し、状態とオーバーレイをクリアする。
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
    // パブリックAPI: 入力ハンドラ(TerrainInputHandler から呼ばれる)
    // =====================================================================

    /// <summary>
    /// 地形左クリック。
    /// Placing: 停留所を末尾に追加する(既存の停留所なら再訪、先頭の停留所なら配置を終える)。
    /// Editing: 点を選択、または区間上クリックで停留所を挿入する。
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
                    // 先頭の停留所をクリックしたら一周が閉じたものとして配置を終える(路線は最後の停留所から先頭へ戻る)。
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
            // 下地パス上でなければ配置しない(バックエンドの設置制約に合わせる)。
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
    /// 地形右クリック。
    /// Placing: 配置を終えて Editing へ移行する。
    /// Editing: 停留所・経由点を削除する。
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
    /// 地形ホバー。Placing 中は次の停留所位置のプレビューに使う。
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
    // ドラッグ(InputHandler から呼ばれる)
    // =====================================================================

    /// <summary>
    /// 指定スクリーン座標が、ドラッグで動かせる点(停留所・経由点)か、経由点を挿入できる区間の上にあるか判定する。
    /// InputHandler がドラッグ開始対象を決めるために使う。
    /// </summary>
    public bool IsMouseOverDraggable(Vector2 screenPos)
    {
        if (phase != Phase.Editing) return false;
        return HitTestPoint(screenPos) >= 0 || HitTestEdge(screenPos) >= 0;
    }

    /// <summary>ドラッグを開始する(地形にレイが当たらなかったときだけ InputHandler から呼ばれる)。</summary>
    public void StartDrag(Vector2 screenPos)
    {
        BeginDrag(screenPos);
    }

    /// <summary>点の上ならその点を掴み、区間の上なら経由点を挿入して掴む。どちらでもなければ何もしない。</summary>
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
            // 挿入位置は最初のドラッグ更新で吸着先へ移すので、それまでは区間の中点に置いておく。
            Vector3 a = points[edge].Position, b = points[(edge + 1) % points.Count].Position;
            hit = edge + 1;
            points.Insert(hit, new RoutePoint((a + b) * .5f, true));
            dragInsertedWaypoint = true;
        }
        isDragging = true;
        dragIndex = hit;
        selectedIndex = hit;
    }

    /// <summary>ドラッグ中。掴んでいる点を、点の種類に応じた下地パス上へ吸着して移動する(未掴みなら押した位置で掴む)。</summary>
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

    /// <summary>ドラッグ終了。点を確定して UI へ通知する(置けなかった新しい経由点は取り消す)。</summary>
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
    // 巡回点の操作
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
    // 下地パスへの吸着
    // =====================================================================

    /// <summary>
    /// 指定ワールド座標を、variants に属する稼働中パスの中心線へ射影する。射影距離が
    /// 「パス幅/2 + STOP_ON_PATH_MARGIN」以内の最近傍パスへ吸着し、高さはそのパスの中心線から取る。
    /// 該当パスが無ければ false。
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
    // ヒット判定(スクリーン座標)
    // =====================================================================

    /// <summary>スクリーン座標に最も近い停留所・経由点のインデックスを返す(ヒット半径内)。</summary>
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
    /// スクリーン座標が巡回点の間の区間(末尾→先頭の区間を含む)の直線上にあるか判定し、区間の始点インデックスを返す。
    /// 見つからなければ -1。経路プレビューの帯ではなく巡回点を結ぶ直線で判定する(帯は応答待ちの間に古くなる)。
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
    // IPC 送出
    // =====================================================================

    /// <summary>
    /// 巡回点を IPC で UI に通知する。各点は {kind, position:[x, 0, z]}(公共交通パスは相対Y=0のため y は0)。
    /// バックエンドの座標系はUnityワールドと同じ扱いのため x,z をそのまま渡す。
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
    // 経路プレビュー
    // =====================================================================

    private void ResetPreview()
    {
        previewDesired = previewSent = previewShown = null;
        previewPayload = null;
        previewSegments.Clear();
        blockedLegs.Clear();
    }

    /// <summary>今の巡回点の経路プレビューを予約する。取得は変更が PREVIEW_DEBOUNCE_SECONDS 落ち着いてから行う。</summary>
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
                // 巡回点が変わった後や編集終了後に届いた応答は描かない。
                if (previewDesired == key && phase != Phase.Inactive) ApplyPreview(response, key);
            }, (code, message) => Debug.LogWarning($"路線の経路プレビューを取得できませんでした: {code} {message}"));
        }
        finally { previewBusy = false; }
    }

    /// <summary>区間ごとの走行データを、路線色帯と同じ規則で道路・線路に沿った線分列にする。</summary>
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
            // 区間ごとに別の経路として描くので、1周の継ぎ目の写し(TransitMotionClock.Closed)を作らせない。
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
    // オーバーレイ描画
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
            // 実際に走る道路・線路に沿った帯と、下地でつながらない区間の赤い破線。
            foreach (var (a, b) in previewSegments)
            {
                ribbonBorder.AddSolidLine(a, b, RibbonBorderColor, 12);
                ribbon.AddSolidLine(a, b, LineColor, 8);
            }
            DrawBlockedLegs();
        }
        else
        {
            // 応答待ちの間は巡回点を結ぶ直線。Editing では末尾→先頭の区間も描いて一周を示す。
            int edges = phase == Phase.Editing && points.Count >= 2 ? points.Count : points.Count - 1;
            for (int i = 0; i < edges; i++)
            {
                overlayRenderer.AddSolidLine(points[i].Position, points[(i + 1) % points.Count].Position, LineColor, LINE_THICKNESS);
            }
        }

        // Placing 中は最後の点からホバー位置までと、ホバー位置から先頭の停留所へ戻る区間のプレビュー線。
        if (phase == Phase.Placing && hoverValid)
        {
            if (points.Count > 0)
            {
                overlayRenderer.AddSolidLine(points[points.Count - 1].Position, hoverPos, PreviewColor, LINE_THICKNESS);
                overlayRenderer.AddDashedLine(hoverPos, points[0].Position, PreviewColor, LINE_THICKNESS);
            }
            overlayRenderer.AddSquare(hoverPos, STOP_MARKER_HALF_SIZE, PreviewColor);
        }

        // 巡回点のマーカー。停留所は四角(巡回の先頭は緑)、経由点は丸。
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

    /// <summary>下地でつながらない区間を、その区間の巡回点(停留所→経由点…→次の停留所)を結ぶ赤い破線で示す。</summary>
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
