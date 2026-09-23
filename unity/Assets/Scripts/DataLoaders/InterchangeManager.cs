using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using GeneratedModels;

/// <summary>
/// バックエンドが自動検出したインターチェンジ(IC/JCT)の名前ラベルを描画するマネージャー。
/// ICの道路自体は普通のパスとして PathManager が描くため、ここが持つのは中心に置くクリック可能な
/// 2D UIラベル(ZoneManager の地区名と同じ ScreenSpaceOverlay Canvas + Button 方式)だけ。
/// 一覧は件数が少ないので都市全体を一度に取得し、応答の version が前回と同じなら再構築しない。
/// </summary>
public class InterchangeManager : MonoBehaviour
{
    public static InterchangeManager Instance { get; private set; }

    private const int CANVAS_SORTING_ORDER = 62; // 地区ラベル(60)より前、アラート(70)より奥
    private const int LABEL_FONT_SIZE = 26;
    private const float LABEL_HEIGHT_OFFSET = 6f; // 地面からの高さ(m)
    private const float LABEL_MAX_DISTANCE = 6000f; // これより遠いICは隠す(遠景でラベルが画面に散らばるため)
    private const float CAMERA_POS_THRESHOLD = 1.0f;
    private const float CAMERA_ROT_THRESHOLD = 2.0f;
    private static readonly Color LABEL_COLOR = new Color(1f, 0.92f, 0.62f);

    private readonly Dictionary<int, InterchangeSummary> interchanges = new Dictionary<int, InterchangeSummary>();
    private readonly Dictionary<int, RectTransform> labels = new Dictionary<int, RectTransform>();

    private Canvas canvas;
    private RectTransform canvasRect;
    private TerrainManager terrainManager;

    /// <summary>最後に反映した一覧の version。-1 は未取得。</summary>
    private int loadedVersion = -1;

    /// <summary>
    /// 都市(セッション)の世代。Clear で進め、取得開始時の値と一致しない応答は捨てる。
    /// セッション切り替え前に出した要求の応答で、新しい都市に前の都市のICを描かないため。
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
    /// セルロードの入口(初回ロード・全量リロード)から呼ぶ。まだ一度も要求していないときだけ取得する。
    /// カメラ移動のたびに取り直さないため、以後の更新は RequestRefresh に任せる。
    /// </summary>
    public void EnsureLoaded()
    {
        if (initialRequested) return;
        RequestRefresh();
    }

    /// <summary>
    /// 一覧を取り直す。エポック完了・編集確定・session:reload の CellDataLoader.ReloadAllData から呼ばれる。
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
        foreach (var label in labels.Values)
        {
            if (label != null) Destroy(label.gameObject);
        }
        labels.Clear();
        interchanges.Clear();
    }

    private void Apply(InterchangesListResponse response)
    {
        if (response.version == loadedVersion) return;
        loadedVersion = response.version;

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

    /// <summary>名前が空なら種別と番号の略号にする(Unityには多言語リソースが無いため翻訳不要な略号を使う)。</summary>
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
            go.SetActive(false); // 位置が決まる次の Update まで原点に出さない
            labels[ic.interchange_id] = rt;
        }

        var label = rt.GetComponent<Text>();
        label.text = GetDisplayName(ic);
        // クリック判定はRectの範囲なので、文字列に合わせて詰める(下の建物・道路のクリックを奪わないため)。
        rt.sizeDelta = new Vector2(label.preferredWidth + 12f, LABEL_FONT_SIZE + 10f);
    }

    private void OnLabelClicked(int interchangeId)
    {
        IpcClient.Instance?.EmitSelectObject("intersection_detail", "interchange", interchangeId.ToString());
    }

    private void OnTerrainAllLoaded(IReadOnlyList<Vector2Int> updatedCells)
    {
        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// 通常(閲覧)状態のときだけラベルを出す。編集モードでは道路・地形が NORMAL 以外の入力モードになるので、
    /// それを見て隠す(ラベルは uGUI なので、出したままだと下の道路編集のクリックを奪う)。
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
            float groundY = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
            Vector3 world = new Vector3(x, groundY + LABEL_HEIGHT_OFFSET, z);
            Vector3 screen = cam.WorldToScreenPoint(world);
            bool visible = screen.z > 0f && Vector3.Distance(camPos, world) <= LABEL_MAX_DISTANCE;
            if (rt.gameObject.activeSelf != visible) rt.gameObject.SetActive(visible);
            if (visible) rt.anchoredPosition = new Vector2(screen.x, screen.y);
        }
    }
}
