using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// Manager for district data and rendering.
/// Show district outlines, names, and buildable-building icons on 2D UI overlays (ScreenSpaceOverlay).
/// This provides consistent, accurate screen coordinates independent of terrain height changes or LOD switches.
/// </summary>
public class ZoneManager : BaseApiDataManager, IVisualModeHandler
{
    public static ZoneManager Instance { get; private set; }

    private Dictionary<int, ZoneData> zoneCache = new Dictionary<int, ZoneData>();
    private Dictionary<int, Rect> zoneBboxCache = new Dictionary<int, Rect>();

    /// <summary>
    /// Cache district versions fetched from the backend.
    /// Compare backend versions during incremental updates and recreate UI only if changed.
    /// </summary>
    private Dictionary<int, int> zoneVersionCache = new Dictionary<int, int>();

    /// <summary>
    /// District data revision, incremented by every UpdateZone / RemoveZone / ClearAllCells.
    /// Callers caching spatial district ID queries or similar data use this for invalidation.
    /// </summary>
    public int ZoneRevision { get; private set; }

    // District name labels in 2D UI (zoneId -> Text RectTransform)
    private Dictionary<int, RectTransform> zoneLabelUIs = new Dictionary<int, RectTransform>();
    // Building icons in 2D UI (zoneId -> list of Image RectTransforms)
    private Dictionary<int, List<RectTransform>> zoneIconUIs = new Dictionary<int, List<RectTransform>>();

    /// <summary>
    /// Label/icon world position (center_point XZ + terrain height + 2m).
    /// Sample only during creation and outline-height recalculation, not on every camera movement.
    /// Do not register districts without center_point (empty districts).
    /// </summary>
    private Dictionary<int, Vector3> zoneLabelAnchors = new Dictionary<int, Vector3>();

    /// <summary>Converted zone.polygon, retained to avoid array allocation on each GetZoneIdAtPoint call.</summary>
    private Dictionary<int, float[][]> zonePolygonCache = new Dictionary<int, float[][]>();

    /// <summary>Parsed zone.color. Do not register unparseable districts; GetZoneColor returns cyan for them.</summary>
    private Dictionary<int, Color> zoneColorCache = new Dictionary<int, Color>();

    /// <summary>
    /// District outline data, storing endpoints in world coordinates.
    /// Pass to OverlayMeshRenderer every frame for 3D-space mesh rendering.
    /// </summary>
    private class ZoneLineEntry
    {
        public Vector3 worldA;
        public Vector3 worldB;
    }
    private Dictionary<int, List<ZoneLineEntry>> zoneLinesByZone = new Dictionary<int, List<ZoneLineEntry>>();

    /// <summary>
    /// Outline frustum-culling unit. Split district outlines into ZONE_LINE_CHUNK_SIZE segments,
    /// using endpoint AABBs to skip groups of out-of-view segments.
    /// </summary>
    private struct ZoneLineChunk
    {
        public int start;
        public int count;
        public Bounds bounds;
        public float radius; // Bounding sphere radius of the unpadded AABB, used to estimate rotation sweep.
    }
    private Dictionary<int, List<ZoneLineChunk>> zoneLineChunks = new Dictionary<int, List<ZoneLineChunk>>();
    private readonly Plane[] frustumPlanes = new Plane[6];
    private const int ZONE_LINE_CHUNK_SIZE = 32;

    /// <summary>
    /// Amount (meters) to expand chunk AABBs in each direction.
    /// Lines are widened in pixels, so edges may enter the screen even with endpoints outside the frustum.
    /// </summary>
    private const float ZONE_CULL_MARGIN = 20f;

    /// <summary>
    /// Extra margin per meter of camera distance covering regions that could enter view between redraws (up to CAMERA_ROT_THRESHOLD rotation).
    /// Rotation displaces points by at most distance × angle in radians, so expand distant chunks more.
    /// </summary>
    private const float ZONE_CULL_SWEEP_PER_METER = CAMERA_ROT_THRESHOLD * Mathf.Deg2Rad;

    /// <summary>
    /// Districts whose outline/label heights need recalculation after terrain tile updates/removal.
    /// While hidden, defer recalculation until shown (resampling invisible outlines is wasteful).
    /// If terrain changes during recalculation, retain progress and continue until this set empties.
    /// </summary>
    private readonly HashSet<int> zoneHeightDirtyIds = new HashSet<int>();
    private Coroutine zoneHeightRefreshCoroutine;

    /// <summary>
    /// Cells notified as containing terrain tiles. On each notification, find cells whose tiles disappeared
    /// and resample their outlines at overview heights (the displayed ground).
    /// </summary>
    private readonly HashSet<Vector2Int> zoneHeightTileCells = new HashSet<Vector2Int>();
    private readonly HashSet<Vector2Int> zoneHeightChangedCells = new HashSet<Vector2Int>();
    private readonly List<Vector2Int> zoneHeightDroppedCells = new List<Vector2Int>();

    /// <summary>Expand district BBOXes by this amount (meters) when matching cells because boundary points may sample either tile.</summary>
    private const float ZONE_HEIGHT_CELL_MARGIN = 1f;

    /// <summary>Samples between scheduler yields during outline height recalculation.</summary>
    private const int ZONE_HEIGHT_SAMPLES_PER_STEP = 256;

    private const float LINE_THICKNESS = 5f;

    /// <summary>
    /// Distance (meters) to inset outlines within districts.
    /// Adjacent districts share boundary streets and polygon edges overlap exactly;
    /// without setback, both outlines occupy the same position and their colors are indistinguishable.
    /// Inset boundaries into parallel lines so both district colors remain visible.
    /// </summary>
    private const float OUTLINE_SETBACK = 3.0f;

    /// <summary>
    /// Maximum setback miter length as a multiple of setback distance.
    /// Clamp because inward offset miter lengths diverge at acute vertices.
    /// </summary>
    private const float OUTLINE_MITER_LIMIT = 4.0f;

    private GameObject uiCanvasRoot;
    private Canvas uiCanvas;
    private RectTransform uiCanvasRect;
    private GraphicRaycaster uiRaycaster;

    private GameObject overlayMeshObj;
    private OverlayMeshRenderer overlayRenderer;

    private TerrainManager terrainManager;

    private bool areZonesLoaded = false;

    /// <summary>
    /// Whether full district loading has started. StartZoneLoad sets this; LoadAllZones finally always clears it
    /// so interruption cannot block later reloads.
    /// Iterators stopped before their first step do not execute finally, so stopping code clears it too.
    /// </summary>
    private bool isZoneLoadInProgress = false;
    private Coroutine zoneLoadCoroutine;

    /// <summary>Number incremented on each start, preventing old-load finally blocks from clearing new-load progress flags.</summary>
    private int zoneLoadToken = 0;

    /// <summary>
    /// Additional reload reservation when RequestZoneRefresh runs during loading.
    /// The active load may contain an old snapshot,
    /// so reload once more afterward to follow current state.
    /// </summary>
    private bool zoneRefreshQueued = false;

    // Throttle UI position updates by detecting camera changes
    private Vector3 lastCamPos = Vector3.negativeInfinity;
    private Quaternion lastCamRot = Quaternion.identity;
    private const float CAMERA_POS_THRESHOLD = 1.0f;
    private const float CAMERA_ROT_THRESHOLD = 2.0f;

    private const float ZONE_UI_SCALE = 2f / 3f;
    private const int ICONS_PER_ROW = 5;

    private bool IsOverlayVisible => uiCanvasRoot != null && uiCanvasRoot.activeSelf;

    public Dictionary<int, ZoneData> GetAllZones()
    {
        return zoneCache;
    }

    /// <summary>
    /// Find the district polygon containing the world coordinates and return its ID.
    /// Return -1 if no district contains them.
    /// </summary>
    public int GetZoneIdAtPoint(Vector3 worldPos)
    {
        Vector2 point = new Vector2(worldPos.x, worldPos.z);
        foreach (var kvp in zoneCache)
        {
            int zoneId = kvp.Key;
            if (zoneBboxCache.TryGetValue(zoneId, out Rect bbox))
            {
                if (!bbox.Contains(point)) continue;
            }

            if (!zonePolygonCache.TryGetValue(zoneId, out float[][] poly) || poly == null || poly.Length < 3) continue;
            if (PolygonUtils.Contains(poly, point.x, point.y))
            {
                return zoneId;
            }
        }
        return -1;
    }

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
        EnsureUICanvas();
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.RegisterObject(InputObjectType.ZONE, this);
        }

        terrainManager = FindAnyObjectByType<TerrainManager>();
        if (terrainManager != null)
        {
            terrainManager.OnAllTerrainLodLoaded += OnTerrainAllLoaded;
        }
    }

    public override void LoadCells(List<Vector2Int> neededCells, bool isForce)
    {
        // Run only when forced or unloaded to avoid frequent loading
        if (isForce || !areZonesLoaded)
        {
            StartZoneLoad();
        }
    }

    /// <summary>
    /// Start full district loading. Do nothing if already started.
    /// Set the progress flag at startup to prevent duplicate same-frame launches from multiple paths.
    /// </summary>
    private void StartZoneLoad()
    {
        if (isZoneLoadInProgress) return;
        isZoneLoadInProgress = true;
        zoneLoadCoroutine = RenderWorkScheduler.Start(this, LoadAllZones(++zoneLoadToken), "ZoneManager");
        if (zoneLoadCoroutine == null) isZoneLoadInProgress = false;
    }

    private void StopZoneLoad()
    {
        RenderWorkScheduler.Stop(this, zoneLoadCoroutine);
        zoneLoadToken++;
        zoneLoadCoroutine = null;
        isZoneLoadInProgress = false;
    }

    protected override void UnloadCell(Vector2Int cellIndex)
    {
        // Unused
    }

    public override void ClearAllCells()
    {
        ZoneRevision++;
        StopZoneLoad();
        StopZoneLineHeightRefresh();
        zoneRefreshQueued = false;
        zoneHeightDirtyIds.Clear();
        zoneHeightTileCells.Clear();

        foreach (var kv in zoneLabelUIs)
        {
            if (kv.Value != null)
            {
                Destroy(kv.Value.gameObject);
            }
        }
        zoneLabelUIs.Clear();

        foreach (var kvIcons in zoneIconUIs)
        {
            DestroyIcons(kvIcons.Value);
        }
        zoneIconUIs.Clear();

        zoneLinesByZone.Clear();
        zoneLineChunks.Clear();
        zoneLabelAnchors.Clear();

        zoneCache.Clear();
        zoneBboxCache.Clear();
        zonePolygonCache.Clear();
        zoneColorCache.Clear();
        zoneVersionCache.Clear();
        loadedCells.Clear();
        areZonesLoaded = false;
    }

    /// <summary>
    /// Refetch every district using version differences, redrawing only changed versions.
    /// Called by CellDataLoader.ReloadAllData after epoch completion/committed edits.
    ///
    /// The system automatically creates, merges, and splits districts and changes polygons each epoch
    /// (district consistency phase). LoadCells refetches only when unloaded/forced,
    /// so ReloadAllData with isForce=false would never update districts again.
    /// Use this method to follow epoch-driven district additions/removals/reshaping.
    /// </summary>
    public void RequestZoneRefresh()
    {
        if (isZoneLoadInProgress)
        {
            zoneRefreshQueued = true;
            return;
        }
        StartZoneLoad();
    }

    /// <summary>
    /// Fetch every district and rebuild changed versions one district at a time within budget.
    /// Start only from StartZoneLoad (caller sets the progress flag; finally here clears it).
    /// </summary>
    private IEnumerator LoadAllZones(int token)
    {
        try
        {
            ZonesListResponse response = null;
            yield return ApiClient.GetBulk<ZonesListResponse>(
                "/zones/",
                r => response = r,
                timingLabel: "ZoneManager zones");
            if (response == null)
            {
                areZonesLoaded = false;
                yield break;
            }

            HashSet<int> receivedIds = new HashSet<int>();

            if (response.zones != null)
            {
                foreach (var zone in response.zones)
                {
                    bool updated = false;
                    try
                    {
                        receivedIds.Add(zone.zone_id);

                        // Version comparison: do not recreate if cached version matches
                        if (!zoneVersionCache.TryGetValue(zone.zone_id, out int cachedVersion) || cachedVersion != zone.version)
                        {
                            UpdateZone(zone);
                            zoneVersionCache[zone.zone_id] = zone.version;
                            updated = true;
                        }
                    }
                    catch (Exception e)
                    {
                        int zoneId = zone != null ? zone.zone_id : -1;
                        Debug.LogError($"ZoneManager: ゾーン {zoneId} の描画生成に失敗しました: {e.Message}\n{e.StackTrace}");
                    }
                    // Outline sampling and UI generation are expensive per district; check budget after each rebuilt district.
                    if (updated) yield return RenderWorkScheduler.Continue;
                }
            }

            SyncZones(receivedIds);
            areZonesLoaded = true;
            // Reset camera position tracking to guarantee overlay redraw
            // after district data updates. The next Update then forces
            // recalculation of outline and label positions.
            lastCamPos = Vector3.negativeInfinity;
            Debug.Log($"ZoneManager: ゾーン一覧を読み込みました count={receivedIds.Count}");
        }
        finally
        {
            if (token == zoneLoadToken)
            {
                isZoneLoadInProgress = false;
                zoneLoadCoroutine = null;
            }
        }

        // If RequestZoneRefresh was reserved during loading, reload once more
        // to follow current state (this snapshot may predate the reservation).
        if (zoneRefreshQueued)
        {
            zoneRefreshQueued = false;
            StartZoneLoad();
        }
    }

    private void SyncZones(HashSet<int> receivedIds)
    {
        List<int> toRemove = new List<int>();
        foreach (int zoneId in zoneCache.Keys)
        {
            if (!receivedIds.Contains(zoneId))
                toRemove.Add(zoneId);
        }
        foreach (int zoneId in toRemove)
        {
            RemoveZone(zoneId);
        }
    }

    public void RemoveZone(int zoneId)
    {
        ZoneRevision++;
        RemoveZoneVisuals(zoneId);

        zoneCache.Remove(zoneId);
        zoneBboxCache.Remove(zoneId);
        zonePolygonCache.Remove(zoneId);
        zoneColorCache.Remove(zoneId);
        // Retaining versions would prevent drawing when the same ID returns with the same version.
        zoneVersionCache.Remove(zoneId);
    }

    /// <summary>
    /// Destroy district outlines, labels, icons, and position caches, shared by RemoveZone and UpdateZone rebuilding.
    /// </summary>
    private void RemoveZoneVisuals(int zoneId)
    {
        if (zoneLabelUIs.TryGetValue(zoneId, out RectTransform label))
        {
            if (label != null) Destroy(label.gameObject);
            zoneLabelUIs.Remove(zoneId);
        }

        if (zoneIconUIs.TryGetValue(zoneId, out List<RectTransform> icons))
        {
            DestroyIcons(icons);
            zoneIconUIs.Remove(zoneId);
        }

        zoneLabelAnchors.Remove(zoneId);
        RemoveZoneLines(zoneId);
        // New outlines/labels sample current terrain, so no height recalculation is needed.
        zoneHeightDirtyIds.Remove(zoneId);
    }

    private void DestroyIcons(List<RectTransform> icons)
    {
        if (icons == null) return;
        foreach (var icon in icons)
        {
            if (icon != null) Destroy(icon.gameObject);
        }
    }

    public IEnumerator FetchZone(int zoneId)
    {
        // Backend GET /zones/{id} returns wrapped { zone: { ... } } data,
        // so receive ZoneSingleResponse then extract .zone.
        yield return ApiClient.Get<ZoneSingleResponse>(
            $"/zones/{zoneId}",
            response =>
            {
                if (response != null && response.zone != null)
                    UpdateZone(response.zone);
            },
            error => Debug.LogError($"FetchZone({zoneId}) failed: {error}")
        );
    }

    private void EnsureUICanvas()
    {
        if (uiCanvasRoot != null) return;

        uiCanvas = Utils.CreateOverlayCanvas("ZoneUICanvas", 60, persistent: true); // In front of 3D, behind modals
        uiCanvasRoot = uiCanvas.gameObject;
        uiCanvasRect = uiCanvas.GetComponent<RectTransform>();
        uiRaycaster = uiCanvas.GetComponent<GraphicRaycaster>();
    }

    private void EnsureOverlayRenderer()
    {
        if (overlayMeshObj != null) return;

        overlayRenderer = OverlayMeshRenderer.Create("ZoneOverlayMesh", alwaysOnTop: false, persistent: true);
        overlayMeshObj = overlayRenderer.gameObject;
    }

    /// <summary>
    /// Return the district's identifying color.
    /// Convert backend per-district color (HEX #RRGGBB) to Color.
    /// Fall back to cyan if unloaded, unset, or unparseable.
    /// Shared by building/parcel/road district-color modes and district outlines.
    /// </summary>
    public Color GetZoneColor(int zoneId)
    {
        if (zoneId > 0 && zoneColorCache.TryGetValue(zoneId, out Color c))
        {
            return c;
        }
        return new Color(0.2f, 0.8f, 1f, 1f); // Fallback to cyan
    }

    /// <summary>
    /// Return district identifying color with arbitrary opacity for internal rendering such as outlines.
    /// </summary>
    private Color GetZoneColor(int zoneId, float alpha)
    {
        Color c = GetZoneColor(zoneId);
        c.a = alpha;
        return c;
    }

    public void UpdateZone(ZoneData zone)
    {
        ZoneRevision++;
        // Reevaluate distance and position after district regeneration even if the camera is stationary.
        lastCamPos = Vector3.negativeInfinity;
        float[] centerPoint = zone.GetCenterPointArray();
        zoneCache[zone.zone_id] = zone;

        string hex = zone.color;
        if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out Color zoneColor))
            zoneColorCache[zone.zone_id] = zoneColor;
        else
            zoneColorCache.Remove(zone.zone_id);

        // Calculate and cache BBOX (bounding rectangle) from the district polygon
        float[][] polygon = zone.GetPolygonArray();
        if (polygon != null)
            zonePolygonCache[zone.zone_id] = polygon;
        else
            zonePolygonCache.Remove(zone.zone_id);
        if (polygon != null && polygon.Length > 0)
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var pt in polygon)
            {
                if (pt.Length >= 2)
                {
                    if (pt[0] < minX) minX = pt[0];
                    if (pt[0] > maxX) maxX = pt[0];
                    if (pt[1] < minZ) minZ = pt[1];
                    if (pt[1] > maxZ) maxZ = pt[1];
                }
            }
            zoneBboxCache[zone.zone_id] = new Rect(minX, minZ, maxX - minX, maxZ - minZ);
        }
        else
        {
            zoneBboxCache.Remove(zone.zone_id);
        }

        // Always destroy previous outlines, labels, and icons first.
        // Districts emptied by encroaching expansion (polygon=[], center_point=None) return early below
        // and are not regenerated; without removal here, old outlines/labels would remain
        // even after their district disappears.
        RemoveZoneVisuals(zone.zone_id);

        // Do not redraw districts without center_point (empty districts).
        // Previous rendering was already destroyed, leaving outlines/labels hidden.
        if (centerPoint == null || centerPoint.Length < 2)
        {
            return;
        }

        zoneIconUIs[zone.zone_id] = new List<RectTransform>();
        float anchorY = Utils.GetGroundHeight(new Vector3(centerPoint[0], 1000f, centerPoint[1])) + 2f;
        zoneLabelAnchors[zone.zone_id] = new Vector3(centerPoint[0], anchorY, centerPoint[1]);

        CreateZoneLines(zone.zone_id, polygon);
        CreateZoneLabelUI(zone);
        CreateZoneIconsUI(zone);

        // Apply current visual mode immediately after generation.
        int currentZoneMode = VisualModeHandler.Instance != null
            ? VisualModeHandler.Instance.GetMode(InputObjectType.ZONE)
            : VisualMode.MODE_NORMAL;
        ApplyVisualMode(currentZoneMode);
    }

    /// <summary>
    /// Create district name text on a 2D UI Canvas.
    /// Update actual positions each frame in Update.
    /// Make clickable and send an IPC event opening zone_detail on click.
    /// Clicks use Rect bounds; fit around text to avoid stealing clicks from buildings/roads underneath.
    /// </summary>
    private void CreateZoneLabelUI(ZoneData zone)
    {
        EnsureUICanvas();
        if (uiCanvasRect == null) return;

        GameObject labelGO = new GameObject($"ZoneLabelUI_{zone.zone_id}");
        labelGO.transform.SetParent(uiCanvasRect, false);

        Text text = labelGO.AddComponent<Text>();
        text.text = zone.name;
        text.fontSize = 35;

        // District names may be multilingual; prioritize OS multilingual fonts (UiFontProvider).
        text.font = UiFontProvider.GetFont(text.fontSize);
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;

        // Add a black text outline for readability
        Outline outline = labelGO.AddComponent<Outline>();
        outline.effectColor = Color.black;
        outline.effectDistance = new Vector2(2.5f, 2.5f);

        RectTransform rt = text.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(text.preferredWidth, text.preferredHeight);
        rt.localScale = Vector3.one * ZONE_UI_SCALE;

        // Make clickable to open district details
        Button button = labelGO.AddComponent<Button>();
        int capturedZoneId = zone.zone_id;
        button.onClick.AddListener(() => OnZoneLabelClicked(capturedZoneId));

        zoneLabelUIs[zone.zone_id] = rt;
    }

    /// <summary>
    /// Create building icons on a 2D UI Canvas.
    /// Update actual positions each frame in Update.
    /// Icons ignore clicks; only district names open details, keeping underlying 3D objects accessible beneath icon rows.
    /// </summary>
    private void CreateZoneIconsUI(ZoneData zone)
    {
        EnsureUICanvas();
        if (uiCanvasRect == null) return;

        List<int> allowedBuildingTypes = GeneratedModelUtils.ExtractBuildingTypes(zone.allowed_bd_mask);
        if (allowedBuildingTypes == null || allowedBuildingTypes.Count == 0)
            return;

        if (BuildingIconFactory.Instance == null)
        {
            Debug.LogWarning("BuildingIconFactory.Instance が null です。アイコンを表示できません。");
            return;
        }

        var icons = new List<RectTransform>();
        const float iconSize = 60f * ZONE_UI_SCALE;

        for (int i = 0; i < allowedBuildingTypes.Count; i++)
        {
            int buildingTypeId = allowedBuildingTypes[i];
            Sprite iconSprite = BuildingIconFactory.Instance.GetIcon(buildingTypeId);
            if (iconSprite == null)
            {
                Debug.LogWarning($"BuildingTypeId {buildingTypeId} のアイコンが見つかりません。");
                continue;
            }

            GameObject iconGO = new GameObject($"ZoneIconUI_{zone.zone_id}_{buildingTypeId}");
            iconGO.transform.SetParent(uiCanvasRect, false);

            Image image = iconGO.AddComponent<Image>();
            image.sprite = iconSprite;
            image.raycastTarget = false;

            RectTransform rt = image.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(iconSize, iconSize);

            icons.Add(rt);
        }

        zoneIconUIs[zone.zone_id] = icons;
    }

    /// <summary>
    /// Handle district name label clicks.
    /// Send an IPC event asking Tauri UI to open zone_detail.
    /// </summary>
    private void OnZoneLabelClicked(int zoneId)
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitSelectObject("zone_detail", "zone", zoneId.ToString());
        }
    }

    /// <summary>
    /// Generate district outlines with world coordinates.
    /// Inset polygons by OUTLINE_SETBACK before drawing so neighboring outlines do not overlap.
    /// Subdivide long segments and sample ground heights per endpoint to follow terrain relief.
    /// OverlayMeshRenderer performs actual rendering.
    /// </summary>
    private void CreateZoneLines(int zoneId, float[][] polygon)
    {
        EnsureOverlayRenderer();

        if (polygon == null || polygon.Length < 3) return;

        Vector2[] outline = BuildSetbackOutline(polygon, OUTLINE_SETBACK);
        if (outline == null) return;

        // 1. Generate outlines, subdividing long segments to follow terrain relief
        var entries = new List<ZoneLineEntry>();
        for (int i = 0; i < outline.Length; i++)
        {
            int nextI = (i + 1) % outline.Length;

            Vector2 p1 = outline[i];
            Vector2 p2 = outline[nextI];

            float dist = Vector2.Distance(p1, p2);
            const float maxSegmentLength = 5.0f;
            int segmentsCount = Mathf.Max(1, Mathf.FloorToInt(dist / maxSegmentLength));

            for (int j = 0; j < segmentsCount; j++)
            {
                float t1 = (float)j / segmentsCount;
                float t2 = (float)(j + 1) / segmentsCount;

                Vector2 subP1 = Vector2.Lerp(p1, p2, t1);
                Vector2 subP2 = Vector2.Lerp(p1, p2, t2);

                float y1 = Utils.GetGroundHeight(new Vector3(subP1.x, 1000f, subP1.y)) + 0.05f;
                float y2 = Utils.GetGroundHeight(new Vector3(subP2.x, 1000f, subP2.y)) + 0.05f;

                entries.Add(new ZoneLineEntry
                {
                    worldA = new Vector3(subP1.x, y1, subP1.y),
                    worldB = new Vector3(subP2.x, y2, subP2.y)
                });
            }
        }
        zoneLinesByZone[zoneId] = entries;
        RebuildZoneLineChunks(zoneId, entries);
    }

    /// <summary>
    /// Split outlines into ZONE_LINE_CHUNK_SIZE segments and rebuild frustum-culling AABBs.
    /// Call when endpoint heights change (creation or height recalculation).
    /// </summary>
    private void RebuildZoneLineChunks(int zoneId, List<ZoneLineEntry> entries)
    {
        if (!zoneLineChunks.TryGetValue(zoneId, out List<ZoneLineChunk> chunks))
        {
            chunks = new List<ZoneLineChunk>((entries.Count + ZONE_LINE_CHUNK_SIZE - 1) / ZONE_LINE_CHUNK_SIZE);
            zoneLineChunks[zoneId] = chunks;
        }
        chunks.Clear();
        for (int start = 0; start < entries.Count; start += ZONE_LINE_CHUNK_SIZE)
        {
            int count = Mathf.Min(ZONE_LINE_CHUNK_SIZE, entries.Count - start);
            Vector3 min = entries[start].worldA;
            Vector3 max = min;
            for (int i = start; i < start + count; i++)
            {
                min = Vector3.Min(min, Vector3.Min(entries[i].worldA, entries[i].worldB));
                max = Vector3.Max(max, Vector3.Max(entries[i].worldA, entries[i].worldB));
            }
            var bounds = new Bounds();
            bounds.SetMinMax(min, max);
            float radius = bounds.extents.magnitude;
            bounds.Expand(ZONE_CULL_MARGIN * 2f);
            chunks.Add(new ZoneLineChunk { start = start, count = count, bounds = bounds, radius = radius });
        }
    }

    /// <summary>
    /// Return district polygon (XZ) outlines offset inward by the specified amount.
    ///
    /// Move each vertex along the average of adjacent inward edge normals, scaled by miter length
    /// (= 1/dot(bisector direction, inward edge normal)), so both edges
    /// intersect after exactly setback displacement (standard miter offset).
    /// Clamp diverging miter lengths at acute vertices with OUTLINE_MITER_LIMIT.
    ///
    /// In narrow districts, offsets exceeding width would reverse/self-intersect outlines, but districts are street-enclosed areas
    /// assumed sufficiently wide relative to the 3m setback; use a simple offset.
    /// </summary>
    private static Vector2[] BuildSetbackOutline(float[][] polygon, float setback)
    {
        int n = polygon.Length;
        var src = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float[] p = polygon[i];
            if (p == null || p.Length < 2) return null;
            src[i] = new Vector2(p[0], p[1]);
        }

        // Determine winding from signed area. Inward normals switch sides with winding.
        // Counterclockwise (positive area): interior is left of travel; clockwise: right.
        float inwardSign = PolygonUtils.SignedArea(src) > 0f ? 1f : -1f;

        var result = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            Vector2 prev = src[(i - 1 + n) % n];
            Vector2 cur = src[i];
            Vector2 next = src[(i + 1) % n];

            Vector2 dirIn = (cur - prev);
            Vector2 dirOut = (next - cur);
            if (dirIn.sqrMagnitude < 1e-8f || dirOut.sqrMagnitude < 1e-8f)
            {
                result[i] = cur;
                continue;
            }
            dirIn.Normalize();
            dirOut.Normalize();

            // Left normal relative to travel (-y, x). Align inward using inwardSign.
            Vector2 nIn = new Vector2(-dirIn.y, dirIn.x) * inwardSign;
            Vector2 nOut = new Vector2(-dirOut.y, dirOut.x) * inwardSign;

            Vector2 bisector = nIn + nOut;
            if (bisector.sqrMagnitude < 1e-8f)
            {
                // Miter is undefined at 180° turnbacks; use a one-sided normal
                result[i] = cur + nOut * setback;
                continue;
            }
            bisector.Normalize();

            // Miter length = 1 / cos(half-angle) = 1 / dot(bisector, normal)
            float cos = Vector2.Dot(bisector, nOut);
            float miter = Mathf.Abs(cos) < 1e-4f ? OUTLINE_MITER_LIMIT : 1f / cos;
            miter = Mathf.Clamp(miter, -OUTLINE_MITER_LIMIT, OUTLINE_MITER_LIMIT);

            result[i] = cur + bisector * (setback * miter);
        }
        return result;
    }

    private void RemoveZoneLines(int zoneId)
    {
        zoneLinesByZone.Remove(zoneId);
        zoneLineChunks.Remove(zoneId);
    }

    private void Update()
    {
        Utils.SyncOverlayCanvasClickable(uiRaycaster);
        if (!IsOverlayVisible) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        // For camera position/rotation changes below thresholds,
        // skip WorldToScreenPoint and RectTransform updates for every district.
        // District labels/outlines change little visually for movement below 1m or rotation below 2°,
        // so per-frame updates are rarely needed.
        Vector3 currentCamPos = cam.transform.position;
        Quaternion currentCamRot = cam.transform.rotation;
        bool posChanged = Vector3.Distance(currentCamPos, lastCamPos) > CAMERA_POS_THRESHOLD;
        bool rotChanged = Quaternion.Angle(currentCamRot, lastCamRot) > CAMERA_ROT_THRESHOLD;
        // Skip while stationary because outline and label positions remain unchanged
        // (lastCamPos is reset when line width, selected district, or height changes require redrawing).
        if (!posChanged && !rotChanged)
        {
            return;
        }

        lastCamPos = currentCamPos;
        lastCamRot = currentCamRot;

        const float epsilon = 0.001f;
        Plane nearPlane = new Plane(
            cam.transform.forward,
            cam.transform.position + cam.transform.forward * (cam.nearClipPlane + epsilon));

        // 1. Update outlines (batched 3D-space mesh rendering with OverlayMeshRenderer)
        // District rendering consists only of outlines, name labels, and icons; do not draw filled polygon planes.
        // District coloring uses each building/parcel/road district-color mode.
        if (overlayRenderer != null)
        {
            overlayRenderer.BeginFrame();
            GeometryUtility.CalculateFrustumPlanes(cam, frustumPlanes);

            foreach (var kvp in zoneLinesByZone)
            {
                int zoneId = kvp.Key;
                var entries = kvp.Value;
                Color outlineColor = GetZoneColor(zoneId, 1f); // 100% opacity

                if (!zoneLineChunks.TryGetValue(zoneId, out List<ZoneLineChunk> chunks))
                {
                    AddZoneLines(entries, 0, entries.Count, nearPlane, outlineColor);
                    continue;
                }
                for (int c = 0; c < chunks.Count; c++)
                {
                    ZoneLineChunk chunk = chunks[c];
                    Bounds cullBounds = chunk.bounds;
                    float sweep = (Vector3.Distance(currentCamPos, cullBounds.center) + chunk.radius) * ZONE_CULL_SWEEP_PER_METER;
                    cullBounds.Expand(sweep * 2f);
                    if (!GeometryUtility.TestPlanesAABB(frustumPlanes, cullBounds)) continue;
                    AddZoneLines(entries, chunk.start, chunk.start + chunk.count, nearPlane, outlineColor);
                }
            }

            overlayRenderer.EndFrame(cam);
        }

        // 2. Update district name label and icon positions
        float maxDistance = Utils.GetMapLabelMaxDistance(currentCamPos);
        float maxDistanceSqr = maxDistance * maxDistance;
        foreach (var kvp in zoneLabelAnchors)
        {
            int zoneId = kvp.Key;
            Vector3 screenPos = cam.WorldToScreenPoint(kvp.Value);
            bool visible = screenPos.z >= 0
                && (kvp.Value - currentCamPos).sqrMagnitude <= maxDistanceSqr;

            // Labels
            if (zoneLabelUIs.TryGetValue(zoneId, out RectTransform label) && label != null)
            {
                SetUiActive(label, visible);
                if (visible)
                {
                    label.anchoredPosition = new Vector2(screenPos.x, screenPos.y);
                }
            }

            // Icons
            if (zoneIconUIs.TryGetValue(zoneId, out List<RectTransform> iconList) && iconList != null)
            {
                for (int i = 0; i < iconList.Count; i++)
                {
                    RectTransform icon = iconList[i];
                    if (icon == null) continue;
                    SetUiActive(icon, visible);
                    if (visible)
                    {
                        int row = i / ICONS_PER_ROW;
                        int column = i % ICONS_PER_ROW;
                        int rowCount = Mathf.Min(ICONS_PER_ROW, iconList.Count - row * ICONS_PER_ROW);
                        float spacing = 70f * ZONE_UI_SCALE;
                        float offsetX = (column - (rowCount - 1) / 2f) * spacing;
                        float offsetY = 62.5f * ZONE_UI_SCALE + row * spacing;
                        icon.anchoredPosition = new Vector2(screenPos.x + offsetX, screenPos.y - offsetY);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Clip entries[start..end) against the near plane and append to the overlay.
    /// Retain only front portions of segments crossing behind the camera, avoiding inverted offscreen rendering.
    /// </summary>
    private void AddZoneLines(List<ZoneLineEntry> entries, int start, int end, Plane nearPlane, Color color)
    {
        end = Mathf.Min(end, entries.Count);
        for (int i = start; i < end; i++)
        {
            ZoneLineEntry entry = entries[i];

            Vector3 ca = entry.worldA;
            Vector3 cb = entry.worldB;

            float da = nearPlane.GetDistanceToPoint(ca);
            float db = nearPlane.GetDistanceToPoint(cb);

            if (da < 0f && db < 0f)
                continue;

            if (da < 0f || db < 0f)
            {
                Vector3 dir = cb - ca;
                float t = da / (da - db);
                Vector3 p = ca + dir * t;
                if (da < 0f) ca = p; else cb = p;
            }

            overlayRenderer.AddSolidLine(ca, cb, color, LINE_THICKNESS);
        }
    }

    private static void SetUiActive(RectTransform rt, bool active)
    {
        GameObject go = rt.gameObject;
        if (go.activeSelf != active) go.SetActive(active);
    }

    private void OnDestroy()
    {
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.UnregisterObject(InputObjectType.ZONE, this);
        }
        if (terrainManager != null)
        {
            terrainManager.OnAllTerrainLodLoaded -= OnTerrainAllLoaded;
        }
    }

    /// <summary>
    /// Called when TerrainManager load batches complete.
    /// Mark only districts overlapping generated/regenerated/LRU-restored tiles or cells whose tiles disappeared since last time (returning to overview heights)
    /// for recalculation; if visible, begin within budget. If already running, enqueue and let the worker continue.
    /// While hidden, start when ApplyVisualMode makes them visible.
    /// </summary>
    private void OnTerrainAllLoaded(IReadOnlyList<Vector2Int> updatedCells)
    {
        if (terrainManager == null) return;
        zoneHeightChangedCells.Clear();
        if (updatedCells != null)
        {
            for (int i = 0; i < updatedCells.Count; i++)
            {
                zoneHeightChangedCells.Add(updatedCells[i]);
                zoneHeightTileCells.Add(updatedCells[i]);
            }
        }
        zoneHeightDroppedCells.Clear();
        foreach (Vector2Int cell in zoneHeightTileCells)
        {
            if (!terrainManager.HasSurfaceTile(cell)) zoneHeightDroppedCells.Add(cell);
        }
        foreach (Vector2Int cell in zoneHeightDroppedCells)
        {
            zoneHeightTileCells.Remove(cell);
            zoneHeightChangedCells.Add(cell);
        }
        if (zoneHeightChangedCells.Count == 0) return;

        foreach (int zoneId in zoneCache.Keys)
        {
            if (ZoneTouchesCells(zoneId, zoneHeightChangedCells)) zoneHeightDirtyIds.Add(zoneId);
        }
        if (IsOverlayVisible) StartZoneLineHeightRefresh();
    }

    /// <summary>Whether district outlines (within BBOX) or label anchors overlap any specified cell.</summary>
    private bool ZoneTouchesCells(int zoneId, HashSet<Vector2Int> cells)
    {
        if (zoneLabelAnchors.TryGetValue(zoneId, out Vector3 anchor) && cells.Contains(TerrainManager.WorldToCell(anchor)))
            return true;
        if (!zoneBboxCache.TryGetValue(zoneId, out Rect bbox)) return false;
        TerrainManager.WorldRectToCellRange(
            bbox.xMin - ZONE_HEIGHT_CELL_MARGIN, bbox.yMin - ZONE_HEIGHT_CELL_MARGIN,
            bbox.xMax + ZONE_HEIGHT_CELL_MARGIN, bbox.yMax + ZONE_HEIGHT_CELL_MARGIN,
            out Vector2Int minCell, out Vector2Int maxCell);
        foreach (Vector2Int cell in cells)
        {
            if (cell.x >= minCell.x && cell.x <= maxCell.x && cell.y >= minCell.y && cell.y <= maxCell.y) return true;
        }
        return false;
    }

    /// <summary>Start if districts need recalculation and no worker runs. Active workers continue processing newly queued districts too.</summary>
    private void StartZoneLineHeightRefresh()
    {
        if (zoneHeightRefreshCoroutine != null || zoneHeightDirtyIds.Count == 0) return;
        zoneHeightRefreshCoroutine = RenderWorkScheduler.Start(this, RefreshZoneLineHeightsWorker(), "ZoneManager");
    }

    /// <summary>
    /// Stop height recalculation. Iterators stopped before their first step skip finally, so clear handles here.
    /// </summary>
    private void StopZoneLineHeightRefresh()
    {
        RenderWorkScheduler.Stop(this, zoneHeightRefreshCoroutine);
        zoneHeightRefreshCoroutine = null;
    }

    /// <summary>
    /// Update dirty districts' (zoneHeightDirtyIds) outline and label heights from current terrain.
    /// ZoneManager loads independently of terrain readiness;
    /// creation before Terrain is ready uses overview heights or 0, requiring correction after tiles arrive.
    /// Yield every ZONE_HEIGHT_SAMPLES_PER_STEP samples; abort districts whose outlines
    /// UpdateZone rebuilt meanwhile (already sampled against current terrain).
    /// Continue newly queued districts until the set empties.
    /// </summary>
    private IEnumerator RefreshZoneLineHeightsWorker()
    {
        bool hasCurrentZone = false;
        int currentZoneId = 0;
        try
        {
            var ids = new List<int>();
            int samples = 0;
            while (zoneHeightDirtyIds.Count > 0)
            {
                ids.Clear();
                ids.AddRange(zoneHeightDirtyIds);
                foreach (int zoneId in ids)
                {
                    if (!zoneHeightDirtyIds.Remove(zoneId)) continue;
                    hasCurrentZone = true;
                    currentZoneId = zoneId;
                    bool aborted = false;
                    if (zoneLinesByZone.TryGetValue(zoneId, out List<ZoneLineEntry> entries))
                    {
                        bool hasPrev = false;
                        Vector3 prevB = Vector3.zero;
                        for (int i = 0; i < entries.Count; i++)
                        {
                            if (samples >= ZONE_HEIGHT_SAMPLES_PER_STEP)
                            {
                                samples = 0;
                                yield return RenderWorkScheduler.Continue;
                                if (!zoneLinesByZone.TryGetValue(zoneId, out List<ZoneLineEntry> current) || !ReferenceEquals(current, entries))
                                {
                                    aborted = true;
                                    break;
                                }
                            }

                            ZoneLineEntry entry = entries[i];
                            Vector3 wa = entry.worldA;
                            Vector3 wb = entry.worldB;

                            // Adjacent subdivisions of one edge share endpoints, so reuse height samples.
                            float newY1;
                            if (hasPrev && prevB.x == wa.x && prevB.z == wa.z)
                            {
                                newY1 = prevB.y;
                            }
                            else
                            {
                                newY1 = Utils.GetGroundHeight(new Vector3(wa.x, 1000f, wa.z)) + 0.05f;
                                samples++;
                            }
                            float newY2 = Utils.GetGroundHeight(new Vector3(wb.x, 1000f, wb.z)) + 0.05f;
                            samples++;

                            entry.worldA = new Vector3(wa.x, newY1, wa.z);
                            entry.worldB = new Vector3(wb.x, newY2, wb.z);
                            prevB = entry.worldB;
                            hasPrev = true;
                        }
                        if (!aborted) RebuildZoneLineChunks(zoneId, entries);
                    }

                    if (!aborted && zoneLabelAnchors.TryGetValue(zoneId, out Vector3 anchor))
                    {
                        anchor.y = Utils.GetGroundHeight(new Vector3(anchor.x, 1000f, anchor.z)) + 2f;
                        zoneLabelAnchors[zoneId] = anchor;
                        samples++;
                    }
                    hasCurrentZone = false;
                }

                // Height-only changes do not trigger stationary-camera Update redraws; force the next Update to draw.
                lastCamPos = Vector3.negativeInfinity;
            }
        }
        finally
        {
            zoneHeightRefreshCoroutine = null;
            // Requeue interrupted districts, whose correction is incomplete (ClearAllCells empties the set after stopping).
            if (hasCurrentZone && zoneCache.ContainsKey(currentZoneId)) zoneHeightDirtyIds.Add(currentZoneId);
        }
    }

    public void ApplyVisualMode(int mode)
    {
        if (uiCanvasRoot == null) return;

        bool isVisible = mode != VisualMode.MODE_HIDDEN;
        uiCanvasRoot.SetActive(isVisible);

        if (overlayMeshObj != null)
        {
            overlayMeshObj.SetActive(isVisible);
        }

        // Start height recalculation deferred while hidden when visibility turns on.
        if (isVisible) StartZoneLineHeightRefresh();

        // If districts are unloaded when visibility turns on, trigger a full load from this action.
        // Camera cell updates normally load them; this also prevents an empty Canvas when only the display panel is used.
        if (isVisible && !areZonesLoaded)
        {
            StartZoneLoad();
        }
    }
}

[Serializable]
public class ZonesListResponse
{
    public List<GeneratedModels.ZoneData> zones;
}

[Serializable]
public class ZoneSingleResponse
{
    public GeneratedModels.ZoneData zone;
}
