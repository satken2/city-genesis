using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// アラートマネージャ。
///
/// 役割
/// ----
/// バックエンドの GET /api/v1/alerts から、alert ビットフラグが立っている
/// 建物(agent)・道路(path)の一覧を取得し、それぞれの center_point 上に
/// ビット種別ごとのアラートアイコンを表示する。
///
/// alert は version 管理外のプロパティ(VERSION_TRIGGER_PROPS に含まれない)のため、
/// BuildingManager / PathManager の version 差分ロード対象外となっており、
/// 本マネージャが独立して GET /api/v1/alerts で観測・アイコン表示を更新する。
///
/// 表示方式
/// --------
/// ZoneManager / ResourceManager と同じく ScreenSpaceOverlay Canvas 上に Image を配置する。
/// Update() で Camera.WorldToScreenPoint を使ってワールド座標からスクリーン座標に
/// 変換し、RectTransform.anchoredPosition を更新する。これにより **カメラからの距離に依存せず
/// 常に一定のスクリーンサイズで表示される**。建物/道路の子にはしないため、VisualModeObject の
/// ステータス連動や LOD アンロードの影響も受けない。
///
/// 1オブジェクトに複数のアラートビットが立っている場合は、アイコンを水平に並べて表示する。
/// 並べ方 ZoneManager.cs:763 と同じ (i - (count-1)/2f) * spacing 式で中央揃えファンアウト。
///
/// アイコンSpriteの選択
/// --------------------
/// AlertIconFactory がビット値→Spriteのマッピングを保持する。本マネージャは
/// AlertIconFactory.Instance.GetIconForAgent(bit) / GetIconForPath(bit) を呼び出して
/// 適切なSpriteを取得する。未設定ビットのSpriteはnullになり、そのビットのアイコンは
/// スキップされる。
///
/// アイコンで表現しないビット
/// --------------------------
/// 火災系の2ビット(ALERT_AGENT_ON_FIRE / ALERT_AGENT_ACCIDENTAL_FIRE)はアイコンではなく
/// 3Dパーティクル(炎・煙)で表現する。本マネージャはAPI応答をそのまま FireEffectManager へ
/// 渡すだけで、アイコンは生成しない(表示フィルタと表示トグルには追従させる)。
///
/// 表示フィルタ
/// ------------
/// UI(表示パネルのアラートタブ)から view:change_view の sub_mode でビットマスクが届く。
/// ALERT_AGENT_* と ALERT_PATH_* は同じビット値を使うため、マスク上では path 側を
/// Constants.ALERT_PATH_MASK_SHIFT だけ上位へずらして名前空間を分ける。
///
/// リフレッシュタイミング
/// --------------------
/// - ReloadAllData / ForceReloadAllData 時(エポック後・編集後を含む): CellDataLoader から Refresh() を呼ぶ。
/// - 表示フィルタ変更時: Refresh(forceRefresh: true)。
/// - カメラセル変更時: CellDataLoader から RefreshPositions() を呼ぶ。alert はエポックと編集でしか
///   変わらないため HTTP は取らず、最後の応答を再適用してアイコン高さと火災エフェクトの規模だけを
///   ロード済みの地形・建物に合わせ直す。
/// - 建物バッチの適用時: BuildingManager.ApplyLodCellChanges から RefreshPositions() を呼ぶ。カメラセル変更の
///   時点では新しい常駐セルの建物が未生成で、アイコンが地面の高さに残るため、生成後に高さを合わせ直す。
/// - Refresh / RefreshPositions はクールダウン中・取得中の要求を捨てず、Update で後から1回にまとめて実行する。
///
/// パフォーマンス設計
/// ------------------
/// - versionキャッシュは持たない(BuildingManager/PathManagerのような差分スキップはしない)。
///   応答は毎回全件を走査し、表示ビット構成と廃墟表示が前回と同じ対象はアイコンの GameObject を
///   使い回して座標だけを更新する。応答に無くなった対象は破棄する。
/// - スクリーン座標とスケールは、カメラ・画面サイズが変わったフレームか応答の適用直後にだけ再計算する。
///   背後・画面外のアイコンは非アクティブにして、ボビングの位置更新と Canvas の再バッチから外す。
/// </summary>
public class AlertManager : MonoBehaviour
{
    public static AlertManager Instance { get; private set; }

    /// <summary>
    /// 建物(agent)のアイコン対象ビット一覧。アイコンを並べる順序でもある。
    /// 正は consts.py(sync_consts.py が生成する Constants.cs 経由)で、ここで再宣言しない。
    /// バックエンドの GET /api/v1/alerts も同じリストで配信対象を絞るため、両者は常に一致する。
    /// </summary>
    private static readonly int[] AGENT_ALERT_BITS = Constants.ALERT_AGENT_ICON_BITS;

    /// <summary>
    /// 建物(agent)のうち、アイコンではなく3Dパーティクルで表現するアラートビット一覧。
    /// アイコンは作らないが、表示フィルタのマスクには含める(表示パネルのトグルで
    /// 炎・煙の表示をON/OFFできるようにするため)。
    /// </summary>
    private static readonly int[] AGENT_VFX_BITS = Constants.ALERT_AGENT_VFX_BITS;

    /// <summary>道路(path)のアイコン対象ビット一覧。アイコンを並べる順序でもある。</summary>
    private static readonly int[] PATH_ALERT_BITS = Constants.ALERT_PATH_ICON_BITS;

    /// <summary>全種別を表示するフィルタマスク。UI から指定が来るまでの既定値。</summary>
    public static readonly int DefaultFilterMask = BuildFullFilterMask();

    private static int BuildFullFilterMask()
    {
        int mask = Constants.ALERT_DISPLAY_RUIN;
        foreach (int bit in AGENT_ALERT_BITS) mask |= bit;
        foreach (int bit in AGENT_VFX_BITS) mask |= bit;
        foreach (int bit in PATH_ALERT_BITS) mask |= bit << Constants.ALERT_PATH_MASK_SHIFT;
        return mask;
    }

    // 表示フィルタ。下位バイトに ALERT_AGENT_*、上位バイト(ALERT_PATH_MASK_SHIFT シフト)に ALERT_PATH_* を載せる。
    // agent と path でビット値が重複するための名前空間分離で、UI(DisplayOptionsPanel)も同じ規約でマスクを組む。
    private int _filterMask = DefaultFilterMask;

    public void SetFilterMask(int mask)
    {
        if (_filterMask == mask) return;
        _filterMask = mask;
        Refresh(forceRefresh: true);
    }

    private bool _isVisible = true;

    /// <summary>
    /// アラートの表示/非表示状態を取得または設定する。
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            if (_uiCanvasRoot != null)
            {
                _uiCanvasRoot.SetActive(_isVisible);
            }
            if (FireEffectManager.Instance != null)
            {
                FireEffectManager.Instance.IsVisible = _isVisible;
            }
        }
    }

    /// <summary>
    /// アラートの表示/非表示をトグルする。
    /// </summary>
    public void ToggleVisibility()
    {
        IsVisible = !IsVisible;
    }

    [Header("Icon")]
    [Tooltip("アイコンのスクリーン上のサイズ(px)。距離に依存せず常にこの大きさで表示される。")]
    public float alertIconSize = 100f;

    [Tooltip("地形表面(または建物屋上)からのYオフセット(m)。ワールド座標 → スクリーン座標変換時に使う。")]
    public float alertIconYOffset = 3f;

    [Tooltip("1オブジェクトに複数ビットが立っている場合のアイコン水平間隔(px)。")]
    public float alertIconHorizontalSpacing = 110f;

    [Header("Distance Scaling")]
    [Tooltip("距離によるスケールを有効にするか")]
    public bool useDistanceScaling = true;

    [Tooltip("スケールが1.0（等倍）になる基準距離(m)")]
    public float scaleReferenceDistance = 100f;

    [Tooltip("最小スケール倍率")]
    public float minScale = 0.3f;

    [Tooltip("最大スケール倍率")]
    public float maxScale = 1.5f;

    [Header("Bobbing Effect")]
    [Tooltip("ふわふわ上下エフェクトを有効にするか")]
    public bool useBobbing = true;

    [Tooltip("上下する振幅(px)")]
    public float bobbingAmplitude = 10f;

    [Tooltip("上下する速度(周波数)")]
    public float bobbingSpeed = 3f;

    [Header("Canvas")]
    [Tooltip("ScreenSpaceOverlay Canvas の sortingOrder。ZoneManager(60)・ConstructionWarning旧(65)より前面、Tooltip(100)より奥。70を既定とする。")]
    public int canvasSortingOrder = 70;

    [Header("Refresh")]
    [Tooltip("連続リフレッシュの最小間隔(秒)。カメラ移動等で頻発する呼び出しを間引く。")]
    public float refreshCooldownSeconds = 0.5f;

    private GameObject _uiCanvasRoot;
    private Canvas _uiCanvas;
    private RectTransform _uiCanvasRect;

    /// <summary>アラート対象1件(キー entityType:id)のアイコン群と、スクリーン配置のキャッシュ。</summary>
    private sealed class AlertIcon
    {
        public readonly List<RectTransform> icons = new();
        public int bits;         // 表示中のアラートビット(フィルタ適用後)。構成が同じなら GameObject を使い回す。
        public bool ruin;        // 廃墟アイコンを表示中か
        public Vector3 world;
        public Vector2 screen;
        public float scale = 1f; // icons に書き込み済みの localScale
        public float phase;      // ボビングの位相。複数アイコンが同期して動かないようにキーのハッシュで決める
        public bool shown = true;
    }

    private readonly Dictionary<string, AlertIcon> _entries = new();
    private readonly HashSet<string> _seenKeys = new();
    private readonly List<string> _staleKeys = new();

    private bool _isRefreshing;
    private float _lastRefreshTime = -1f;
    private bool _refreshQueued;
    private bool _queuedForce;
    // ClearAll・無効化で進める。取得中の旧応答を捨てるための世代番号。
    private int _generation;

    private AlertsResponse _lastResponse;
    private bool _positionsQueued;
    private float _lastPositionsTime = -1f;

    // カメラ変化検知によるUI位置更新間引き用(ZoneManager と同じ基準)。
    private Vector3 _lastCamPos = Vector3.negativeInfinity;
    private Quaternion _lastCamRot = Quaternion.identity;
    private float _lastCamFov = -1f;
    private int _lastScreenWidth = -1;
    private int _lastScreenHeight = -1;
    private bool _screenDirty = true;
    private const float CAMERA_POS_THRESHOLD = 0.005f;
    private const float CAMERA_ROT_THRESHOLD = 0.01f;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void OnDisable()
    {
        // GameObject の非アクティブ化で RefreshCoroutine が止まると _isRefreshing が戻らないため、
        // 取得中の世代を捨て、再び有効になった後の Update で取り直す。
        if (!_isRefreshing) return;
        _generation++;
        _isRefreshing = false;
        _refreshQueued = true;
    }

    /// <summary>
    /// /alerts を取り直してアラートアイコンを更新する。
    /// ReloadAllData / ForceReloadAllData・表示フィルタ変更時に呼ばれる。
    /// クールダウン中(forceRefresh=false のとき)・取得中・無効中の要求は捨てずに保留し、
    /// Update で実行できるようになった時点で1回にまとめて取得する。forceRefresh はクールダウンを無視する。
    /// </summary>
    public void Refresh(bool forceRefresh = false)
    {
        if (_isRefreshing || !isActiveAndEnabled
            || (!forceRefresh && Time.time - _lastRefreshTime < refreshCooldownSeconds))
        {
            _refreshQueued = true;
            _queuedForce |= forceRefresh;
            return;
        }
        StartRefresh();
    }

    /// <summary>
    /// 表示範囲の変化に合わせて、最後に取得した /alerts 応答をアイコン高さと火災エフェクトへ再適用する。
    /// カメラセル変更時と建物バッチの適用時に呼ばれる。HTTP は取らず、Update でクールダウン単位にまとめて実行する。
    /// 応答が未取得(直前の取得が失敗した等)なら、再適用の代わりに取得を要求する。
    /// </summary>
    public void RefreshPositions()
    {
        if (_lastResponse == null) { Refresh(); return; }
        _positionsQueued = true;
    }

    /// <summary>
    /// 全てのアラート表現(アイコンと火災エフェクト)をクリアする。
    /// ForceReloadAllData・セッションリセットで呼ばれ、取得中の応答と保留中の要求も破棄する。
    /// </summary>
    public void ClearAll()
    {
        _generation++;
        _isRefreshing = false;
        _refreshQueued = false;
        _queuedForce = false;
        _lastResponse = null;
        _positionsQueued = false;
        ClearIcons();
        if (FireEffectManager.Instance != null)
        {
            FireEffectManager.Instance.ClearAll();
        }
    }

    /// <summary>アイコンだけをクリアする。火災エフェクトは FireEffectManager が別に保持する。</summary>
    private void ClearIcons()
    {
        foreach (var entry in _entries.Values)
        {
            DestroyIconObjects(entry);
        }
        _entries.Clear();
    }

    private void StartRefresh()
    {
        _lastRefreshTime = Time.time;
        _isRefreshing = true;
        StartCoroutine(RefreshCoroutine(_generation));
    }

    private IEnumerator RefreshCoroutine(int generation)
    {
        AlertsResponse response = null;
        string errorMsg = null;
        yield return ApiClient.GetBulk<AlertsResponse>(
            "/alerts/",
            r => response = r,
            e => errorMsg = e,
            timingLabel: "/alerts",
            isCanceled: () => generation != _generation
        );

        // 世代を進めた側(ClearAll / OnDisable)が _isRefreshing を戻している。
        if (generation != _generation) yield break;
        _isRefreshing = false;

        if (errorMsg != null)
        {
            // セーブロード前等で503が返る通常パスなので Warning レベル。
            Debug.LogWarning($"Alert refresh failed: {errorMsg}");
        }
        else if (response != null)
        {
            // 新しい応答の適用でアイコン高さも解決し直すため、保留中の再適用は不要になる。
            _positionsQueued = false;
            ApplyAlerts(response);
        }
    }

    /// <summary>保留中の取得要求と再適用要求を、実行できるようになった時点で消化する。</summary>
    private void ProcessQueuedRequests()
    {
        if (_refreshQueued && !_isRefreshing
            && (_queuedForce || Time.time - _lastRefreshTime >= refreshCooldownSeconds))
        {
            _refreshQueued = false;
            _queuedForce = false;
            StartRefresh();
        }

        if (_positionsQueued && _lastResponse != null && !_isRefreshing
            && Time.time - _lastPositionsTime >= refreshCooldownSeconds)
        {
            _positionsQueued = false;
            _lastPositionsTime = Time.time;
            ApplyAlerts(_lastResponse);
        }
    }

    private void ApplyAlerts(AlertsResponse response)
    {
        _lastResponse = response;

        // 火災エフェクトはアイコン用のCanvasにもAlertIconFactoryにも依存しないため、
        // アイコン側の前提が揃っているかに関わらず先に同期する。
        if (FireEffectManager.Instance != null)
        {
            FireEffectManager.Instance.IsVisible = _isVisible;
            FireEffectManager.Instance.ApplyAlerts(response.agents, _filterMask);
        }

        EnsureUICanvas();
        if (_uiCanvasRect == null) return;

        if (AlertIconFactory.Instance == null)
        {
            // AlertIconFactory がシーンに存在しない場合はアイコンを表示できない。
            ClearIcons();
            return;
        }

        _seenKeys.Clear();
        if (response.agents != null)
        {
            foreach (var item in response.agents)
            {
                string key = MakeKey("agent", item.id);
                _seenKeys.Add(key);
                UpdateIcons(key, item, "agent");
            }
        }
        if (response.paths != null)
        {
            foreach (var item in response.paths)
            {
                string key = MakeKey("path", item.id);
                _seenKeys.Add(key);
                UpdateIcons(key, item, "path");
            }
        }

        // 不要になったアイコン(アラートが解消された対象)を破棄
        _staleKeys.Clear();
        foreach (var key in _entries.Keys)
        {
            if (!_seenKeys.Contains(key)) _staleKeys.Add(key);
        }
        foreach (var key in _staleKeys)
        {
            DestroyIcons(key);
        }
        _staleKeys.Clear();
        _seenKeys.Clear();

        _screenDirty = true;
    }

    /// <summary>指定キーのアイコンを破棄し、スクリーン配置のキャッシュごと登録を消す。</summary>
    private void DestroyIcons(string key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return;
        DestroyIconObjects(entry);
        _entries.Remove(key);
    }

    private static void DestroyIconObjects(AlertIcon entry)
    {
        foreach (var rt in entry.icons)
        {
            if (rt != null) Destroy(rt.gameObject);
        }
        entry.icons.Clear();
    }

    private void UpdateIcons(string key, AlertItem item, string entityType)
    {
        if (item.center_point == null || item.center_point.Count < 2) return;

        // 廃墟(倒壊)アイコンは alert ビットではなく status 由来。撤去するまで出続けるため、
        // 表示フィルタの専用ビット ALERT_DISPLAY_RUIN で agent/path まとめてON/OFFできる。
        bool isRuin = item.status == Constants.STATUS_COLLAPSED;
        bool showRuin = isRuin && (_filterMask & Constants.ALERT_DISPLAY_RUIN) != 0;
        int[] targetBits = entityType == "agent" ? AGENT_ALERT_BITS : PATH_ALERT_BITS;
        int bits = ActiveBitMask(item.alert, entityType);

        // 表示対象が1つも無ければ(フィルタで全て落ちた場合を含む)、残っているアイコンを破棄して終了する。
        if (!showRuin && bits == 0)
        {
            DestroyIcons(key);
            return;
        }

        // アイコンを表示すべきワールド座標を計算。
        // 建物の場合は建物メッシュ高さを加えて屋上より上に出す。道路の場合は地形高さ+オフセット。
        float x = item.center_point[0];
        float z = item.center_point[1];
        float groundHeight = Utils.GetGroundHeight(new Vector3(x, 1000f, z));

        float yOffset = alertIconYOffset;
        if (entityType == "agent" && BuildingManager.Instance != null)
        {
            GameObject buildingGO = BuildingManager.Instance.GetBuildingObject(item.id);
            if (buildingGO != null)
            {
                var mf = buildingGO.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    yOffset += mf.sharedMesh.bounds.size.y;
                }
            }
        }

        Vector3 worldPos = new Vector3(x, groundHeight + yOffset, z);

        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.bits == bits && entry.ruin == showRuin && entry.icons.Count > 0)
            {
                entry.world = worldPos;
                return;
            }
            DestroyIconObjects(entry);
        }
        else
        {
            // 桁落ちによるカクつきを防ぐため、位相シフトは [0, 2π] の範囲に収める。
            entry = new AlertIcon { phase = (Mathf.Abs(key.GetHashCode()) % 1000) / 1000f * Mathf.PI * 2f };
            _entries[key] = entry;
        }

        entry.bits = bits;
        entry.ruin = showRuin;
        entry.world = worldPos;
        entry.scale = 1f;
        entry.shown = true;

        // 廃墟アイコン。
        if (showRuin)
        {
            Sprite collapsedSprite = AlertIconFactory.Instance.GetIconForStatusCollapsed(entityType);
            if (collapsedSprite != null)
            {
                entry.icons.Add(CreateIconObject($"Status_{entityType}_{item.id}_collapsed", collapsedSprite));
            }
        }

        // アラートビットアイコン。並び順は targetBits の順。
        foreach (int bit in targetBits)
        {
            if ((bits & bit) == 0) continue;
            Sprite sprite = entityType == "agent"
                ? AlertIconFactory.Instance.GetIconForAgent(bit)
                : AlertIconFactory.Instance.GetIconForPath(bit);
            if (sprite == null) continue; // そのビットに対応するSprite未設定時はスキップ

            entry.icons.Add(CreateIconObject($"Alert_{entityType}_{item.id}_bit{bit}", sprite));
        }

        if (entry.icons.Count == 0)
        {
            // 全ビットのSpriteが未設定等でアイコンが1つも無かった場合、エントリごと削除。
            DestroyIcons(key);
        }
    }

    /// <summary>
    /// アイコンGameObjectを生成してCanvasにアタッチする。
    /// </summary>
    private RectTransform CreateIconObject(string name, Sprite sprite)
    {
        var iconGO = new GameObject(name);
        iconGO.transform.SetParent(_uiCanvasRect, false);

        var image = iconGO.AddComponent<Image>();
        image.sprite = sprite;
        image.raycastTarget = false; // InputHandler の RaycastAll に干渉させない

        var rt = image.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(alertIconSize, alertIconSize);

        return rt;
    }

    /// <summary>
    /// alert ビットフラグのうち、立っていてかつ表示フィルタで有効なビットの和を返す。
    /// path のフィルタビットは上位バイトに置かれているため、比較前にマスク側をシフトして下ろす。
    /// STATUS_* による廃墟アイコンはここではなく UpdateIcons で処理する。
    /// </summary>
    private int ActiveBitMask(int alert, string entityType)
    {
        bool isAgent = entityType == "agent";
        int[] targetBits = isAgent ? AGENT_ALERT_BITS : PATH_ALERT_BITS;
        int filter = isAgent ? _filterMask : (_filterMask >> Constants.ALERT_PATH_MASK_SHIFT);

        int bits = 0;
        foreach (int bit in targetBits)
        {
            if ((alert & bit) != 0 && (filter & bit) != 0) bits |= bit;
        }
        return bits;
    }

    private void Update()
    {
        ProcessQueuedRequests();

        if (_uiCanvasRoot == null || !_uiCanvasRoot.activeSelf) return;
        if (_entries.Count == 0) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        // 1. カメラ・画面サイズが変わった場合は、ベースのスクリーン座標・距離スケール・画面内判定を更新する。
        Vector3 currentCamPos = cam.transform.position;
        Quaternion currentCamRot = cam.transform.rotation;
        float currentFov = cam.orthographic ? cam.orthographicSize : cam.fieldOfView;
        int screenWidth = Screen.width;
        int screenHeight = Screen.height;
        bool camChanged = _screenDirty
            || screenWidth != _lastScreenWidth
            || screenHeight != _lastScreenHeight
            || currentFov != _lastCamFov
            || Vector3.Distance(currentCamPos, _lastCamPos) > CAMERA_POS_THRESHOLD
            || Quaternion.Angle(currentCamRot, _lastCamRot) > CAMERA_ROT_THRESHOLD;

        if (camChanged)
        {
            _screenDirty = false;
            _lastCamPos = currentCamPos;
            _lastCamRot = currentCamRot;
            _lastCamFov = currentFov;
            _lastScreenWidth = screenWidth;
            _lastScreenHeight = screenHeight;

            foreach (var entry in _entries.Values)
            {
                Vector3 screenPos = cam.WorldToScreenPoint(entry.world);
                entry.screen = new Vector2(screenPos.x, screenPos.y);

                float scale = 1f;
                if (useDistanceScaling)
                {
                    float dist = Vector3.Distance(currentCamPos, entry.world);
                    scale = Mathf.Clamp(scaleReferenceDistance / Mathf.Max(dist, 0.1f), minScale, maxScale);
                }
                if (scale != entry.scale)
                {
                    entry.scale = scale;
                    Vector3 scaleVec = new Vector3(scale, scale, 1f);
                    foreach (var rt in entry.icons)
                    {
                        if (rt != null) rt.localScale = scaleVec;
                    }
                }

                // 横並びの端とボビングの振れ幅まで含めて、アイコンが少しでも画面に掛かる範囲を表示対象にする。
                float margin = (alertIconSize + alertIconHorizontalSpacing * entry.icons.Count) * Mathf.Max(1f, scale)
                               + bobbingAmplitude;
                bool visible = screenPos.z >= 0
                               && screenPos.x >= -margin && screenPos.x <= screenWidth + margin
                               && screenPos.y >= -margin && screenPos.y <= screenHeight + margin;
                if (visible != entry.shown)
                {
                    entry.shown = visible;
                    foreach (var rt in entry.icons)
                    {
                        if (rt != null) rt.gameObject.SetActive(visible);
                    }
                }
            }
        }

        // 2. ボビングが有効なら毎フレーム、無効ならカメラ変化時だけ、表示中のアイコンの位置を更新する。
        if (useBobbing || camChanged)
        {
            float bobbingTime = Time.time * bobbingSpeed;
            foreach (var entry in _entries.Values)
            {
                if (!entry.shown) continue;

                float bobbingOffset = useBobbing ? Mathf.Sin(bobbingTime + entry.phase) * bobbingAmplitude : 0f;
                int count = entry.icons.Count;
                for (int i = 0; i < count; i++)
                {
                    RectTransform rt = entry.icons[i];
                    if (rt == null) continue;

                    // スケールを考慮してアイコン同士の間隔を補正する
                    float offsetX = (i - (count - 1) / 2f) * alertIconHorizontalSpacing * entry.scale;
                    rt.anchoredPosition = new Vector2(entry.screen.x + offsetX, entry.screen.y + bobbingOffset);
                }
            }
        }
    }

    /// <summary>
    /// ScreenSpaceOverlay Canvas を生成する。
    /// Canvas を使わず SpriteRenderer + Billboard 方式も考えられるが、距離に依存せず
    /// 常に一定サイズで表示するには ScreenSpaceOverlay が適している。
    /// </summary>
    private void EnsureUICanvas()
    {
        if (_uiCanvasRoot != null) return;

        // Canvas がシーン遷移等で破棄されていれば、子のアイコンも消えているので登録を捨てて作り直させる。
        ClearIcons();
        _screenDirty = true;
        _uiCanvas = Utils.CreateOverlayCanvas("AlertUICanvas", canvasSortingOrder, persistent: false); // ZoneManager(60)より前面
        _uiCanvasRoot = _uiCanvas.gameObject;
        _uiCanvasRect = _uiCanvasRoot.GetComponent<RectTransform>();
        _uiCanvasRoot.SetActive(_isVisible);
    }

    private static string MakeKey(string entityType, int id)
    {
        return entityType + ":" + id;
    }
}

/// <summary>アラート対象1件分。建物(agent)・道路(path)共通。</summary>
[Serializable]
public class AlertItem
{
    public int id;
    public string name;
    public List<float> center_point;
    public int status;
    public int alert;   // ALERT_AGENT_* / ALERT_PATH_* のビット和
}

/// <summary>アラートAPI(/api/v1/alerts)のレスポンス。</summary>
[Serializable]
public class AlertsResponse
{
    public List<AlertItem> agents;
    public List<AlertItem> paths;
}
