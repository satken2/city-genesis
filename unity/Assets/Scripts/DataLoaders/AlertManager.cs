using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Alert manager.
///
/// Responsibilities
/// ----
/// Fetch buildings (agents) and roads (paths) with alert bits set
/// from the backend GET /api/v1/alerts endpoint, and display an icon
/// for each bit type above their center_point.
/// The same API provides L1 spatial warnings for resources, buildings, and residential parcels.
/// These depend only on the alert filter, independent of resource/parcel render objects and visibility.
///
/// Since alert is not versioned (it is absent from VERSION_TRIGGER_PROPS),
/// BuildingManager / PathManager do not load it through version-based updates;
/// this manager independently observes GET /api/v1/alerts and updates icons.
///
/// Display method
/// --------
/// Place Images on a ScreenSpaceOverlay Canvas, as ZoneManager / ResourceManager do.
/// Update() uses Camera.WorldToScreenPoint to convert world coordinates to screen coordinates
/// and updates RectTransform.anchoredPosition. Icons therefore **always retain a constant
/// screen size regardless of camera distance**. They are not children of buildings/roads, so
/// VisualModeObject status changes and LOD unloading do not affect them.
///
/// When several alert bits are set on one object, display the icons side by side.
/// Center the fan-out with (i - (count-1)/2f) * spacing, as in ZoneManager.cs:763.
///
/// Selecting icon Sprites
/// --------------------
/// AlertIconFactory holds the bit-to-Sprite mapping. This manager calls
/// AlertIconFactory.Instance.GetIconForAgent(bit) / GetIconForPath(bit)
/// to obtain the appropriate Sprite. An unconfigured bit has a null Sprite, so
/// its icon is skipped.
///
/// Bits represented without icons
/// --------------------------
/// Fire (ALERT_AGENT_ON_FIRE) is represented by
/// 3D particles (flames and smoke). This manager only forwards the API response to FireEffectManager
/// and creates no icon (the effects still follow the display filter and visibility toggle).
///
/// Display filter
/// ------------
/// The UI (the Alerts tab of the display panel) sends a bitmask in sub_mode of view:change_view.
/// ALERT_AGENT_* and ALERT_PATH_* share bit values, so the mask shifts path bits
/// upward by Constants.ALERT_PATH_MASK_SHIFT to separate their namespaces.
/// L1 warnings use the display-only ALERT_DISPLAY_*_WARNING region.
///
/// Refresh timing
/// --------------------
/// - ReloadAllData / ForceReloadAllData (including after an epoch or edit): CellDataLoader calls Refresh().
/// - Display filter changes: Refresh(forceRefresh: true).
/// - Camera cell changes: CellDataLoader calls RefreshPositions(). Alerts change only during epochs and edits,
///   so no HTTP request is made; the last response is reapplied to adjust icon heights and fire effect sizes
///   to the loaded terrain and buildings.
/// - Building batch application: BuildingManager.ApplyLodCellChanges calls RefreshPositions(). Buildings in newly resident cells
///   do not exist yet when the camera cell changes, leaving icons at ground height; realign them after generation.
/// - Terrain tiles for resident cells become ready: CellDataLoader calls RefreshPositions(). Buildings are placed using overview
///   heights without waiting for tiles, so realign them to the terrain heights once ready.
/// - Refresh / RefreshPositions retain requests during cooldowns or fetches, coalescing them into one later Update call.
///
/// Performance design
/// ------------------
/// - No version cache is held (there is no differential skip as in BuildingManager/PathManager).
///   Each response is scanned in full. Reuse icon GameObjects and update only positions when the displayed bit set
///   and ruin visibility match the previous response. Destroy entries absent from the response.
/// - Recalculate screen coordinates and scale only when the camera or screen size changes, or immediately after applying a response.
///   Deactivate icons behind the camera or offscreen to exclude their bobbing updates and Canvas rebatching.
/// </summary>
public class AlertManager : MonoBehaviour
{
    public static AlertManager Instance { get; private set; }

    /// <summary>
    /// Icon bits for buildings (agents), also defining their display order.
    /// Use the shared constants synchronized to Constants.cs; do not redeclare them here.
    /// The backend GET /api/v1/alerts uses the same list to filter results, so both always agree.
    /// </summary>
    private static readonly int[] AGENT_ALERT_BITS = Constants.ALERT_AGENT_ICON_BITS;

    /// <summary>
    /// Building (agent) alert bits represented by 3D particles instead of icons.
    /// No icons are created, but the bits remain in the display filter mask so the display panel toggle
    /// can turn flames and smoke on or off.
    /// </summary>
    private static readonly int[] AGENT_VFX_BITS = Constants.ALERT_AGENT_VFX_BITS;

    /// <summary>Icon bits for roads (paths), also defining their display order.</summary>
    private static readonly int[] PATH_ALERT_BITS = Constants.ALERT_PATH_ICON_BITS;

    /// <summary>Filter mask that shows every type. Used until the UI specifies a value.</summary>
    public static readonly int DefaultFilterMask = BuildFullFilterMask();

    private static int BuildFullFilterMask()
    {
        int mask = Constants.ALERT_DISPLAY_RUIN;
        foreach (int bit in AGENT_ALERT_BITS) mask |= bit;
        foreach (int bit in AGENT_VFX_BITS) mask |= bit;
        foreach (int bit in PATH_ALERT_BITS) mask |= bit << Constants.ALERT_PATH_MASK_SHIFT;
        foreach (int bit in Constants.ALERT_WARNING_DISPLAY_BITS) mask |= bit;
        return mask;
    }

    // Display filter: ALERT_AGENT_* in the low byte and ALERT_PATH_* in the high byte (shifted by ALERT_PATH_MASK_SHIFT).
    // This separates the overlapping agent/path bit namespaces; the UI (DisplayOptionsPanel) constructs its mask using the same convention.
    private int _filterMask = DefaultFilterMask;

    public void SetFilterMask(int mask)
    {
        if (_filterMask == mask) return;
        _filterMask = mask;
        Refresh(forceRefresh: true);
    }

    private bool _isVisible = true;

    /// <summary>
    /// Gets or sets alert visibility.
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
    /// Toggles alert visibility.
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

    /// <summary>Icons and cached screen layout for one alert target (key entityType:id).</summary>
    private sealed class AlertIcon
    {
        public readonly List<RectTransform> icons = new();
        public int bits;         // Displayed alert bits after filtering. Reuse GameObjects when the set is unchanged.
        public int warningBits;  // L1 spatial warning, in a separate namespace from entity alerts.
        public bool ruin;        // Whether the ruin icon is displayed
        public Vector3 world;
        public Vector2 screen;
        public float scale = 1f; // localScale already applied to icons
        public float phase;      // Bobbing phase derived from the key hash to keep icons from moving in sync
        public bool shown = true;
    }

    private readonly Dictionary<string, AlertIcon> _entries = new();
    private readonly HashSet<string> _seenKeys = new();
    private readonly Dictionary<string, int> _warningMasks = new();
    private readonly List<string> _staleKeys = new();

    private bool _isRefreshing;
    private float _lastRefreshTime = -1f;
    private bool _refreshQueued;
    private bool _queuedForce;
    // Generation advanced by ClearAll and disabling, to discard older in-flight responses.
    private int _generation;

    private AlertsResponse _lastResponse;
    private bool _positionsQueued;
    private float _lastPositionsTime = -1f;

    // Throttle UI position updates by detecting camera changes (same criteria as ZoneManager).
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
        // If deactivating the GameObject stops RefreshCoroutine, _isRefreshing is never reset, so
        // discard the in-flight generation and fetch again from Update after reactivation.
        if (!_isRefreshing) return;
        _generation++;
        _isRefreshing = false;
        _refreshQueued = true;
    }

    /// <summary>
    /// Fetch /alerts again and update alert icons.
    /// Called by ReloadAllData / ForceReloadAllData and display filter changes.
    /// Retain requests made during cooldown (when forceRefresh=false), fetching, or inactivity;
    /// coalesce them into one fetch when Update can execute it. forceRefresh bypasses the cooldown.
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
    /// Reapply the last /alerts response to icon heights and fire effects as the visible area changes.
    /// Called on camera cell changes and building batch application. No HTTP request is made; Update coalesces work per cooldown.
    /// If no response is available (for example, the last fetch failed), request a fetch instead.
    /// </summary>
    public void RefreshPositions()
    {
        if (_lastResponse == null) { Refresh(); return; }
        _positionsQueued = true;
    }

    /// <summary>
    /// Clear all alert representations (icons and fire effects).
    /// Called by ForceReloadAllData and session resets; also discard in-flight responses and pending requests.
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

    /// <summary>Clear icons only. FireEffectManager retains fire effects separately.</summary>
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

        // The caller that advanced the generation (ClearAll / OnDisable) has already reset _isRefreshing.
        if (generation != _generation) yield break;
        _isRefreshing = false;

        if (errorMsg != null)
        {
            // Use Warning level because returning 503 before loading save data is an expected path.
            Debug.LogWarning($"Alert refresh failed: {errorMsg}");
        }
        else if (response != null)
        {
            // Applying the new response resolves icon heights again, so pending reapplication is unnecessary.
            _positionsQueued = false;
            ApplyAlerts(response);
        }
    }

    /// <summary>Process pending fetch and reapplication requests once execution becomes possible.</summary>
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

        // Fire effects depend on neither the icon Canvas nor AlertIconFactory, so
        // synchronize them first regardless of whether the icon prerequisites are ready.
        if (FireEffectManager.Instance != null)
        {
            FireEffectManager.Instance.IsVisible = _isVisible;
            FireEffectManager.Instance.ApplyAlerts(response.agents, _filterMask);
        }

        EnsureUICanvas();
        if (_uiCanvasRect == null) return;

        if (AlertIconFactory.Instance == null)
        {
            // Icons cannot be displayed without AlertIconFactory in the scene.
            ClearIcons();
            return;
        }

        _seenKeys.Clear();
        _warningMasks.Clear();
        if (response.warnings != null)
        {
            foreach (var item in response.warnings)
            {
                string key = MakeKey(item.entity_type, item.id);
                _warningMasks.TryGetValue(key, out int current);
                _warningMasks[key] = current | item.warning;
            }
        }
        if (response.agents != null)
        {
            foreach (var item in response.agents)
            {
                string key = MakeKey("agent", item.id);
                _seenKeys.Add(key);
                _warningMasks.TryGetValue(key, out int warnings);
                UpdateIcons(key, item, "agent", warnings);
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

        // Merge building warnings into the existing alert row. Resources and parcels require no render objects;
        // their warnings use only API positions and remain visible when their layers are hidden.
        if (response.warnings != null)
        {
            foreach (var item in response.warnings)
            {
                string key = MakeKey(item.entity_type, item.id);
                if (!_seenKeys.Add(key)) continue;
                var target = new AlertItem { id = item.id, center_point = item.center_point };
                UpdateIcons(key, target, item.entity_type, _warningMasks[key]);
            }
        }
        _warningMasks.Clear();

        // Destroy icons no longer needed (targets whose alerts have cleared)
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

    /// <summary>Destroy icons for the specified key and remove the entry, including its screen layout cache.</summary>
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

    private void UpdateIcons(string key, AlertItem item, string entityType, int warningMask = 0)
    {
        if (item.center_point == null || item.center_point.Count < 2) return;

        // Ruin (collapse) icons come from status, not alert bits. They persist until removal, so
        // the dedicated ALERT_DISPLAY_RUIN display filter bit toggles both agent/path icons together.
        bool isRuin = (entityType == "agent" || entityType == "path") && item.status == Constants.STATUS_COLLAPSED;
        bool showRuin = isRuin && (_filterMask & Constants.ALERT_DISPLAY_RUIN) != 0;
        int[] targetBits = AlertBitsFor(entityType);
        int bits = ActiveBitMask(item.alert, entityType);
        int warningBits = warningMask & _filterMask;

        // If no items are visible (including when filtering removes all), destroy remaining icons and return.
        if (!showRuin && bits == 0 && warningBits == 0)
        {
            DestroyIcons(key);
            return;
        }

        // Calculate the world coordinates for the icons.
        // For buildings, add the building mesh height to place icons above the roof. For roads, use terrain height plus an offset.
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
            if (entry.bits == bits && entry.warningBits == warningBits && entry.ruin == showRuin && entry.icons.Count > 0)
            {
                entry.world = worldPos;
                return;
            }
            DestroyIconObjects(entry);
        }
        else
        {
            // Keep the phase shift within [0, 2π] to avoid jitter from loss of precision.
            entry = new AlertIcon { phase = (Mathf.Abs(key.GetHashCode()) % 1000) / 1000f * Mathf.PI * 2f };
            _entries[key] = entry;
        }

        entry.bits = bits;
        entry.warningBits = warningBits;
        entry.ruin = showRuin;
        entry.world = worldPos;
        entry.scale = 1f;
        entry.shown = true;

        // Ruin icon.
        if (showRuin)
        {
            Sprite collapsedSprite = AlertIconFactory.Instance.GetIconForStatusCollapsed(entityType);
            if (collapsedSprite != null)
            {
                entry.icons.Add(CreateIconObject($"Status_{entityType}_{item.id}_collapsed", collapsedSprite));
            }
        }

        // Alert bit icons, ordered by targetBits.
        foreach (int bit in targetBits)
        {
            if ((bits & bit) == 0) continue;
            Sprite sprite = entityType == "agent"
                ? AlertIconFactory.Instance.GetIconForAgent(bit)
                : AlertIconFactory.Instance.GetIconForPath(bit);
            if (sprite == null) continue; // Skip if no Sprite is configured for the bit

            entry.icons.Add(CreateIconObject($"Alert_{entityType}_{item.id}_bit{bit}", sprite));
        }

        foreach (int bit in Constants.ALERT_WARNING_DISPLAY_BITS)
        {
            if ((warningBits & bit) == 0) continue;
            Sprite sprite = AlertIconFactory.Instance.GetIconForWarning(bit);
            if (sprite != null)
                entry.icons.Add(CreateIconObject($"Warning_{entityType}_{item.id}_bit{bit}", sprite, isWarning: true));
        }

        if (entry.icons.Count == 0)
        {
            // Remove the entire entry if no icons were created, for example when every bit has no Sprite.
            DestroyIcons(key);
        }
    }

    /// <summary>
    /// Create an icon GameObject and attach it to the Canvas.
    /// </summary>
    private RectTransform CreateIconObject(string name, Sprite sprite, bool isWarning = false)
    {
        var iconGO = new GameObject(name);
        iconGO.transform.SetParent(_uiCanvasRect, false);

        var image = iconGO.AddComponent<Image>();
        image.sprite = sprite;
        image.raycastTarget = false; // Do not interfere with InputHandler RaycastAll
        if (isWarning)
        {
            // Add a white outline so black resource icons remain legible over dark terrain.
            var outline = iconGO.AddComponent<Outline>();
            outline.effectColor = Color.white;
            outline.effectDistance = new Vector2(1.5f, -1.5f);
        }

        var rt = image.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(alertIconSize, alertIconSize);

        return rt;
    }

    /// <summary>
    /// Return the union of alert bits that are set and enabled in the display filter.
    /// Since path filter bits occupy the high byte, shift the mask down before comparing.
    /// Ruin icons based on STATUS_* are handled by UpdateIcons, not here.
    /// </summary>
    private int ActiveBitMask(int alert, string entityType)
    {
        bool isAgent = entityType == "agent";
        int[] targetBits = AlertBitsFor(entityType);
        int filter = isAgent ? _filterMask : (_filterMask >> Constants.ALERT_PATH_MASK_SHIFT);

        int bits = 0;
        foreach (int bit in targetBits)
        {
            if ((alert & bit) != 0 && (filter & bit) != 0) bits |= bit;
        }
        return bits;
    }

    private static int[] AlertBitsFor(string entityType)
    {
        return entityType == "agent" ? AGENT_ALERT_BITS
            : entityType == "path" ? PATH_ALERT_BITS : Array.Empty<int>();
    }

    private void Update()
    {
        ProcessQueuedRequests();

        if (_uiCanvasRoot == null || !_uiCanvasRoot.activeSelf) return;
        if (_entries.Count == 0) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        // 1. If the camera or screen size changes, update base screen coordinates, distance scale, and screen visibility.
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

                // Include the row edges and bobbing amplitude so icons are displayed whenever any portion overlaps the screen.
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

        // 2. Update visible icon positions every frame when bobbing is enabled; otherwise only on camera changes.
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

                    // Adjust icon spacing for the scale
                    float offsetX = (i - (count - 1) / 2f) * alertIconHorizontalSpacing * entry.scale;
                    rt.anchoredPosition = new Vector2(entry.screen.x + offsetX, entry.screen.y + bobbingOffset);
                }
            }
        }
    }

    /// <summary>
    /// Create a ScreenSpaceOverlay Canvas.
    /// SpriteRenderer + Billboard could be used without a Canvas, but ScreenSpaceOverlay is suited to
    /// keeping a constant display size regardless of distance.
    /// </summary>
    private void EnsureUICanvas()
    {
        if (_uiCanvasRoot != null) return;

        // If a scene transition or similar event destroyed the Canvas, its child icons are gone too; discard entries so they can be recreated.
        ClearIcons();
        _screenDirty = true;
        _uiCanvas = Utils.CreateOverlayCanvas("AlertUICanvas", canvasSortingOrder, persistent: false); // In front of ZoneManager (60)
        _uiCanvasRoot = _uiCanvas.gameObject;
        _uiCanvasRect = _uiCanvasRoot.GetComponent<RectTransform>();
        _uiCanvasRoot.SetActive(_isVisible);
    }

    private static string MakeKey(string entityType, int id)
    {
        return entityType + ":" + id;
    }
}

/// <summary>One alert target, shared by buildings (agents) and roads (paths).</summary>
[Serializable]
public class AlertItem
{
    public int id;
    public string name;
    public List<float> center_point;
    public int status;
    public int alert;   // Union of ALERT_AGENT_* / ALERT_PATH_* bits
}

/// <summary>Alert API (/api/v1/alerts) response.</summary>
[Serializable]
public class AlertsResponse
{
    public List<AlertItem> agents;
    public List<AlertItem> paths;
    public List<SpatialWarningItem> warnings;
}

/// <summary>L1 spatial warning. Position is [x,z]; warning is a union of display-filter-only bits.</summary>
[Serializable]
public class SpatialWarningItem
{
    public string entity_type;
    public int id;
    public List<float> center_point;
    public int warning;
}
