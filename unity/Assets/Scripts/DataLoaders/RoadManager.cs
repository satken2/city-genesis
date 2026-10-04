using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using GeneratedModels;

/// <summary>
/// 道路(名前付き道路)の一覧を保持し、道路名を3Dビューの画面ラベルとして描くマネージャー。
/// 道路は区間(パス)の名札でしかないので、描画するのは label_points ごとのクリック可能な
/// 2D UIラベル(InterchangeManager と同じ ScreenSpaceOverlay Canvas + Button 方式)だけ。
/// 一覧は件数が少ないので都市全体を一度に取得し、応答の version が前回と同じなら再構築しない。
/// ラベルは道路の向きに回転させ(逆さにしない)、重なるものと遠いものは近い順に間引く。
/// </summary>
public class RoadManager : MonoBehaviour
{
    public static RoadManager Instance { get; private set; }

    private const int CANVAS_SORTING_ORDER = 61; // 地区ラベル(60)より前、インターチェンジ(62)より奥
    private const int LABEL_FONT_SIZE = 20;
    private const float LABEL_HEIGHT_OFFSET = 3f; // 路面からの高さ(m)
    private const int LABEL_MAX_COUNT = 60;
    private const float LABEL_OVERLAP_PADDING = 6f; // 重なり判定で外接矩形に足す余白(px)
    private const float LABEL_PATH_SEARCH_RADIUS = 30f; // ラベル点の高さを所属区間から拾う探索半径(m)
    private const float GROUND_Y_RETRY_SECONDS = 2f; // 所属区間が未ロードで地表高さを使ったラベルを取り直す間隔
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

    /// <summary>最後に反映した一覧の version。-1 は未取得。</summary>
    private long loadedVersion = -1;

    /// <summary>
    /// 都市(セッション)の世代。Clear で進め、取得開始時の値と一致しない応答は捨てる
    /// (セッション切り替え前の要求の応答で、新しい都市に前の都市の道路名を描かないため)。
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
    /// セルロードの入口(初回ロード・全量リロード)から呼ぶ。まだ一度も要求していないときだけ取得する。
    /// カメラ移動のたびに取り直さないため、以後の更新は RequestRefresh に任せる。
    /// </summary>
    public void EnsureLoaded()
    {
        if (initialRequested) return;
        RequestRefresh();
    }

    /// <summary>
    /// 一覧を取り直す。エポック完了・編集確定の CellDataLoader.ReloadAllData と、
    /// UI の world:refresh_roads(道路の作成・改名・所属変更・削除の直後)から呼ばれる。
    /// 取得中なら完了後に1回だけ取り直す(進行中の応答は要求時点より古い可能性があるため)。
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
    /// 全ラベルとデータを破棄する。ClearAllManagerData(セッション切り替え・全量リロード・ゲーム終了)から呼ばれる。
    /// 取得中の要求は止めず、世代を進めて応答を捨てる。
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

    /// <summary>一覧上でそのパスが属する道路のID。属していなければ VALUE_NONE。</summary>
    public int GetRoadIdOfPath(int pathId)
    {
        return roadIdByPath.TryGetValue(pathId, out int roadId) ? roadId : Constants.VALUE_NONE;
    }

    /// <summary>
    /// 名前が空なら UI の road.unnamed と同じ「道路 #id」にする(Unity には多言語リソースが無いため、
    /// 周りのツールチップ文言と同じく日本語固定)。3D の道路名ラベルは名前の無い道路を出さないので使わない。
    /// </summary>
    public static string GetDisplayName(RoadSummary road)
    {
        if (road == null) return "";
        return string.IsNullOrWhiteSpace(road.name) ? $"道路 #{road.road_id}" : road.name;
    }

    /// <summary>"#rrggbb" を色にする。読めなければ白。</summary>
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
                // 未命名の道路は UI 側が翻訳済みの「道路 #id」を補うが、3Dでは番号だけのラベルが
                // 意味を持たないので出さない。
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
        // 道路色そのままだと暗い色が背景に沈むので、白へ寄せて読みやすくする。
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
        // クリック判定は(回転込みの)Rectの範囲なので、文字列に合わせて詰める(下の道路・建物のクリックを奪わないため)。
        var size = new Vector2(text.preferredWidth + 8f, LABEL_FONT_SIZE + 6f);
        rt.sizeDelta = size;
        var button = go.AddComponent<Button>();
        int capturedId = road.road_id;
        button.onClick.AddListener(() => OnLabelClicked(capturedId));
        go.SetActive(false); // 位置が決まる次の Update まで原点に出さない

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
    /// 表示条件はインターチェンジのラベルと同じ(世界表示中・通常モード・道路レイヤーが非表示でない)。
    /// 編集モードでは道路・地形が NORMAL 以外の入力モードになるので、それを見て隠す
    /// (ラベルは uGUI なので、出したままだと下の道路編集のクリックを奪う)。
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
    /// 画面内・距離内のラベルを近い順に置き、既に置いたラベルと外接矩形が重なるものは隠す。
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
            // 3D距離は水平距離以上なので、水平距離だけで範囲外と分かるラベルは高さを引かずに隠す。
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
    /// ワールドの道路の向きを画面へ投影した角度(度)。文字が逆さにならないよう (-90, 90] に収める。
    /// 道路がほぼ視線方向を向いて画面上の長さが潰れる場合は水平にする。
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

    /// <summary>中心・大きさ・回転角(度)の矩形を囲む軸平行の矩形(余白込み)。</summary>
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
    /// ラベル点の高さを決める。高架・橋でも路面に沿うよう、近くのロード済みの所属区間の中心線から拾い、
    /// 見つからなければ地表高さを使って GROUND_Y_RETRY_SECONDS 後に取り直す(区間のロード待ち)。
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
