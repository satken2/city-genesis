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
/// Update() で毎フレーム Camera.WorldToScreenPoint を使ってワールド座標からスクリーン座標に
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
/// 表示フィルタ
/// ------------
/// UI(表示パネルのアラートタブ)から view:change_view の sub_mode でビットマスクが届く。
/// ALERT_AGENT_* と ALERT_PATH_* は同じビット値を使うため、マスク上では path 側を
/// Constants.ALERT_PATH_MASK_SHIFT だけ上位へずらして名前空間を分ける。
///
/// リフレッシュタイミング
/// --------------------
/// - game:end IPC 受信時(エポック完走後): GameStateManager から Refresh(forceRefresh: true) を呼ぶ。
/// - カメラセル変更時: CellDataLoader から Refresh() を呼ぶ。短時間の連続呼び出しはクールダウンで間引く。
/// - ReloadAllData / ForceReloadAllData 時: 同様に Refresh() を呼ぶ。
///
/// パフォーマンス設計
/// ------------------
/// - アラート対象は都市規模でも数十〜数百件程度。スクリーン座標更新も
///   カメラ移動が閾値未満のフレームはスキップして間引く(ZoneManager と同じ基準)。
/// - versionキャッシュは持たない(BuildingManager/PathManagerのような差分スキップはしない)。
///   毎回フル再構築し、HashSet<string> で前回セットとの差分を取って追加/削除する。
/// </summary>
public class AlertManager : MonoBehaviour
{
    public static AlertManager Instance { get; private set; }

    /// <summary>建物(agent)のアラートビット一覧。アイコンを並べる順序でもある。</summary>
    private static readonly int[] AGENT_ALERT_BITS =
    {
        Constants.ALERT_AGENT_CONSTRUCTION_SHORTAGE,
        Constants.ALERT_AGENT_ON_FIRE,
        Constants.ALERT_AGENT_ACCIDENTAL_FIRE,
        Constants.ALERT_AGENT_CRIME,
        Constants.ALERT_AGENT_CRIME_SIGN,
        Constants.ALERT_AGENT_INFRA_DISCONNECTED,
    };

    /// <summary>道路(path)のアラートビット一覧。アイコンを並べる順序でもある。</summary>
    private static readonly int[] PATH_ALERT_BITS =
    {
        Constants.ALERT_PATH_CONSTRUCTION_SHORTAGE,
        Constants.ALERT_PATH_ACCIDENT,
        Constants.ALERT_PATH_NO_STATION_HOUSE,
    };

    /// <summary>全種別を表示するフィルタマスク。UI から指定が来るまでの既定値。</summary>
    public static readonly int DefaultFilterMask = BuildFullFilterMask();

    private static int BuildFullFilterMask()
    {
        int mask = Constants.ALERT_DISPLAY_RUIN;
        foreach (int bit in AGENT_ALERT_BITS) mask |= bit;
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

    // 1オブジェクトに複数アイコンを並べるため、キー entityType:id に対して List<GameObject> を保持する。
    // ZoneManager.zoneIconUIs と静的な構造。
    private readonly Dictionary<string, List<GameObject>> _icons = new();
    private readonly Dictionary<string, Vector3> _worldPositions = new();
    private readonly Dictionary<string, Vector2> _baseScreenPositions = new();
    private readonly Dictionary<string, bool> _isBehindCache = new();

    private bool _isRefreshing;
    private float _lastRefreshTime = -1f;

    // カメラ変化検知によるUI位置更新間引き用(ZoneManager と同じ基準)。
    private Vector3 _lastCamPos = Vector3.negativeInfinity;
    private Quaternion _lastCamRot = Quaternion.identity;
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

    /// <summary>
    /// アラートアイコンを更新する。
    /// game:end IPC受信時やカメラセル変更時に呼ばれる。
    /// 短時間に連続呼び出しされた場合はクールダウンとしてスキップする(forceRefresh=trueで回避)。
    /// </summary>
    public void Refresh(bool forceRefresh = false)
    {
        if (!forceRefresh && Time.time - _lastRefreshTime < refreshCooldownSeconds) return;
        _lastRefreshTime = Time.time;

        if (_isRefreshing) return;
        StartCoroutine(RefreshCoroutine());
    }

    /// <summary>
    /// 全てのアラートアイコンをクリアする。ForceReloadAllData 等で一括破棄したい場合に使う。
    /// </summary>
    public void ClearAll()
    {
        foreach (var kvp in _icons)
        {
            foreach (var iconGO in kvp.Value)
            {
                if (iconGO != null) Destroy(iconGO);
            }
        }
        _icons.Clear();
        _worldPositions.Clear();
        _baseScreenPositions.Clear();
        _isBehindCache.Clear();
    }

    private IEnumerator RefreshCoroutine()
    {
        _isRefreshing = true;

        AlertsResponse response = null;
        string errorMsg = null;
        yield return ApiClient.Get<AlertsResponse>(
            "/alerts/",
            r => response = r,
            e => errorMsg = e
        );

        if (errorMsg != null)
        {
            // セーブロード前等で503が返る通常パスなので Warning レベル。
            Debug.LogWarning($"Alert refresh failed: {errorMsg}");
        }
        else if (response != null)
        {
            ApplyAlerts(response);
        }

        _isRefreshing = false;
    }

    private void ApplyAlerts(AlertsResponse response)
    {
        EnsureUICanvas();
        if (_uiCanvasRect == null) return;

        if (AlertIconFactory.Instance == null)
        {
            // AlertIconFactory がシーンに存在しない場合は表示できない。
            ClearAll();
            return;
        }

        var newKeys = new HashSet<string>();
        if (response.agents != null)
        {
            foreach (var item in response.agents)
            {
                string key = MakeKey("agent", item.id);
                newKeys.Add(key);
                UpdateIcons(key, item, "agent");
            }
        }
        if (response.paths != null)
        {
            foreach (var item in response.paths)
            {
                string key = MakeKey("path", item.id);
                newKeys.Add(key);
                UpdateIcons(key, item, "path");
            }
        }

        // 不要になったアイコン(アラートが解消された対象)を破棄
        var toRemove = new List<string>();
        foreach (var kvp in _icons)
        {
            if (!newKeys.Contains(kvp.Key)) toRemove.Add(kvp.Key);
        }
        foreach (var key in toRemove)
        {
            DestroyIcons(key);
        }

        // 直ちにスクリーン座標を更新するため、カメラ位置トラッキングをリセット。
        _lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// 指定キーのアイコンを破棄し、位置・スクリーン座標のキャッシュごと登録を消す。
    /// キャッシュを残すと `_icons` との件数不一致でスクリーン座標更新が毎フレーム走る。
    /// </summary>
    private void DestroyIcons(string key)
    {
        if (_icons.TryGetValue(key, out var iconList))
        {
            foreach (var iconGO in iconList)
            {
                if (iconGO != null) Destroy(iconGO);
            }
            _icons.Remove(key);
        }
        _worldPositions.Remove(key);
        _baseScreenPositions.Remove(key);
        _isBehindCache.Remove(key);
    }

    private void UpdateIcons(string key, AlertItem item, string entityType)
    {
        if (item.center_point == null || item.center_point.Count < 2) return;

        // 廃墟(倒壊・焼失)アイコンは alert ビットではなく status 由来。撤去するまで出続けるため、
        // 表示フィルタの専用ビット ALERT_DISPLAY_RUIN で agent/path まとめてON/OFFできる。
        bool isRuin = System.Array.IndexOf(Constants.STATUSES_RUIN, item.status) >= 0;
        bool showRuin = isRuin && (_filterMask & Constants.ALERT_DISPLAY_RUIN) != 0;
        List<int> activeBits = EnumerateActiveBits(item.alert, entityType);

        // 表示対象が1つも無ければ(フィルタで全て落ちた場合を含む)、残っているアイコンを破棄して終了する。
        if (!showRuin && activeBits.Count == 0)
        {
            DestroyIcons(key);
            return;
        }

        // アイコンを表示すべきワールド座標を計算。
        // 建物の場合は建物メッシュ高さを加えて屋上より上に出す。道路の場合は地形高さ+オフセット。
        float x = item.center_point[0];
        float z = item.center_point[1];
        float groundHeight = HeightmapTextureManager.GetTerrainHeight(new Vector3(x, 1000f, z));

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
        _worldPositions[key] = worldPos;

        // 立っているビットを列挙し、ビットごとにアイコンを(再)生成する。
        // 既存のアイコンリストは一度全破棄してから再構築するのが最も単純で安全。
        // アイコン数は高々ビット数(=alert定数の数)なので数件程度、再生成コストは無視できる。
        if (_icons.TryGetValue(key, out var existingList))
        {
            foreach (var iconGO in existingList)
            {
                if (iconGO != null) Destroy(iconGO);
            }
            existingList.Clear();
        }
        else
        {
            existingList = new List<GameObject>();
            _icons[key] = existingList;
        }

        // 廃墟アイコン。
        if (showRuin)
        {
            Sprite collapsedSprite = AlertIconFactory.Instance.GetIconForStatusCollapsed(entityType);
            if (collapsedSprite != null)
            {
                var iconGO = CreateIconObject($"Status_{entityType}_{item.id}_collapsed", collapsedSprite);
                existingList.Add(iconGO);
            }
        }

        // アラートビットアイコン。
        foreach (int bit in activeBits)
        {
            Sprite sprite = entityType == "agent"
                ? AlertIconFactory.Instance.GetIconForAgent(bit)
                : AlertIconFactory.Instance.GetIconForPath(bit);
            if (sprite == null) continue; // そのビットに対応するSprite未設定時はスキップ

            var iconGO = CreateIconObject($"Alert_{entityType}_{item.id}_bit{bit}", sprite);
            existingList.Add(iconGO);
        }

        if (existingList.Count == 0)
        {
            // 全ビットのSpriteが未設定等でアイコンが1つも無かった場合、エントリごと削除。
            DestroyIcons(key);
        }
    }

    /// <summary>
    /// アイコンGameObjectを生成してCanvasにアタッチする。
    /// </summary>
    private GameObject CreateIconObject(string name, Sprite sprite)
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

        return iconGO;
    }

    /// <summary>
    /// alert ビットフラグのうち、立っていてかつ表示フィルタで有効なビット値を抽出する。
    /// path のフィルタビットは上位バイトに置かれているため、比較前にマスク側をシフトして下ろす。
    /// STATUS_* による廃墟アイコンはここではなく UpdateIcons で処理する。
    /// </summary>
    private List<int> EnumerateActiveBits(int alert, string entityType)
    {
        bool isAgent = entityType == "agent";
        int[] targetBits = isAgent ? AGENT_ALERT_BITS : PATH_ALERT_BITS;
        int filter = isAgent ? _filterMask : (_filterMask >> Constants.ALERT_PATH_MASK_SHIFT);

        var bits = new List<int>(targetBits.Length);
        foreach (int bit in targetBits)
        {
            if ((alert & bit) != 0 && (filter & bit) != 0) bits.Add(bit);
        }
        return bits;
    }

    private void Update()
    {
        if (_uiCanvasRoot == null || !_uiCanvasRoot.activeSelf) return;
        if (_icons.Count == 0) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        // 1. カメラが動いた場合は、ベースのスクリーン座標と距離ベースのスケールを更新する。
        Vector3 currentCamPos = cam.transform.position;
        Quaternion currentCamRot = cam.transform.rotation;
        bool posChanged = Vector3.Distance(currentCamPos, _lastCamPos) > CAMERA_POS_THRESHOLD;
        bool rotChanged = Quaternion.Angle(currentCamRot, _lastCamRot) > CAMERA_ROT_THRESHOLD;

        // キャッシュサイズがアイコン数と異なる場合もリフレッシュが必要
        bool needScreenPosUpdate = posChanged || rotChanged || _baseScreenPositions.Count != _icons.Count;

        if (needScreenPosUpdate)
        {
            _lastCamPos = currentCamPos;
            _lastCamRot = currentCamRot;

            foreach (var kvp in _icons)
            {
                string key = kvp.Key;
                if (!_worldPositions.TryGetValue(key, out Vector3 worldPos)) continue;

                Vector3 screenPos = cam.WorldToScreenPoint(worldPos);
                bool isBehind = screenPos.z < 0;

                _baseScreenPositions[key] = new Vector2(screenPos.x, screenPos.y);
                _isBehindCache[key] = isBehind;

                // 距離に応じたスケールを計算して適用
                if (useDistanceScaling)
                {
                    float dist = Vector3.Distance(currentCamPos, worldPos);
                    float scale = scaleReferenceDistance / Mathf.Max(dist, 0.1f);
                    scale = Mathf.Clamp(scale, minScale, maxScale);
                    Vector3 scaleVec = new Vector3(scale, scale, 1f);

                    List<GameObject> iconList = kvp.Value;
                    if (iconList != null)
                    {
                        for (int i = 0; i < iconList.Count; i++)
                        {
                            GameObject iconGO = iconList[i];
                            if (iconGO != null)
                            {
                                iconGO.transform.localScale = scaleVec;
                            }
                        }
                    }
                }
                else
                {
                    List<GameObject> iconList = kvp.Value;
                    if (iconList != null)
                    {
                        for (int i = 0; i < iconList.Count; i++)
                        {
                            GameObject iconGO = iconList[i];
                            if (iconGO != null)
                            {
                                iconGO.transform.localScale = Vector3.one;
                            }
                        }
                    }
                }
            }
        }

        // 2. 毎フレーム、ふわふわアニメーション（および位置）を更新する。
        // ボビングが有効、またはカメラが移動した（needScreenPosUpdate == true）場合に適用。
        if (useBobbing || needScreenPosUpdate)
        {
            foreach (var kvp in _icons)
            {
                string key = kvp.Key;
                List<GameObject> iconList = kvp.Value;
                if (iconList == null || iconList.Count == 0) continue;

                if (!_baseScreenPositions.TryGetValue(key, out Vector2 baseScreenPos)) continue;
                if (!_isBehindCache.TryGetValue(key, out bool isBehind)) continue;

                // 複数アイコンが同期して動くのを避けるため、キーのハッシュで位相をずらす
                // 桁落ちによるカクつきを防ぐため、位相シフトは [0, 2π] の範囲に収める
                float phaseShift = useBobbing ? ((Mathf.Abs(key.GetHashCode()) % 1000) / 1000f * Mathf.PI * 2f) : 0f;
                float bobbingOffset = useBobbing ? (Mathf.Sin(Time.time * bobbingSpeed + phaseShift) * bobbingAmplitude) : 0f;

                int count = iconList.Count;
                for (int i = 0; i < count; i++)
                {
                    GameObject iconGO = iconList[i];
                    if (iconGO == null) continue;

                    iconGO.SetActive(!isBehind);
                    if (!isBehind)
                    {
                        var rt = iconGO.GetComponent<RectTransform>();
                        if (rt != null)
                        {
                            // スケールを考慮してアイコン同士の間隔を補正する
                            float currentScale = rt.localScale.x;
                            float offsetX = (i - (count - 1) / 2f) * alertIconHorizontalSpacing * currentScale;
                            rt.anchoredPosition = new Vector2(baseScreenPos.x + offsetX, baseScreenPos.y + bobbingOffset);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// ScreenSpaceOverlay Canvas を生成する。ZoneManager.EnsureUICanvas と同じ構成。
    /// Canvas を使わず SpriteRenderer + Billboard 方式も考えられるが、距離に依存せず
    /// 常に一定サイズで表示するには ScreenSpaceOverlay が適している。
    /// </summary>
    private void EnsureUICanvas()
    {
        if (_uiCanvasRoot != null) return;

        _uiCanvasRoot = new GameObject("AlertUICanvas");
        _uiCanvas = _uiCanvasRoot.AddComponent<Canvas>();
        _uiCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _uiCanvas.sortingOrder = canvasSortingOrder; // ZoneManager(60)・旧ConstructionWarning(65)より前面
        _uiCanvasRect = _uiCanvasRoot.GetComponent<RectTransform>();
        _uiCanvasRoot.AddComponent<CanvasScaler>();
        _uiCanvasRoot.AddComponent<GraphicRaycaster>();
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
