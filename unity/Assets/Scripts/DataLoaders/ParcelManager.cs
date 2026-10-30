using UnityEngine;
using System.Collections.Generic;
using UnityEngine.UI;
using System;
using System.Collections;
using GeneratedModels;

public class ParcelManager : BaseApiDataManager, IVisualModeHandler
{
    [SerializeField] private Material vacantParcelMaterial;

    private const string PARCEL_PREFIX = "Parcel_";
    private const string PARCEL_TAG = "ParcelObject";

    /// <summary>
    /// Outline data for one parcel. Store endpoints in world coordinates;
    /// UpdateAllParcelOutlines passes them to OverlayMeshRenderer when the camera moves.
    /// CreateParcelOutline replaces the instance when geometry/heights change (contents are immutable).
    /// </summary>
    private class ParcelOutlineData
    {
        public int parcelId;
        /// <summary>
        /// Parcel center coordinates, including elevation.
        /// Retained to avoid Terrain elevation queries during rendering updates and speed up distance checks.
        /// </summary>
        public Vector3 center;
        public Vector3[] worldPoints;
        /// <summary>
        /// Fan-triangulated data visualization triangles (lazily generated from worldPoints).
        /// OverlayMeshRenderer.AddPreTriangulated retains references until EndFrame, so do not modify after generation.
        /// Geometry/height changes replace the entire outline instance, requiring no invalidation.
        /// </summary>
        public List<Vector3> fillTriangles;
        /// <summary>District ID at the parcel center (spatial query result). Valid only while zoneRevision matches ZoneManager.ZoneRevision.</summary>
        public int zoneId;
        public int zoneRevision = -1;
    }

    private List<ParcelOutlineData> outlines = new List<ParcelOutlineData>();
    private Dictionary<int, int> parcelIdToOutlineIndex = new Dictionary<int, int>();
    private Dictionary<int, ParcelSummary> parcelCache = new Dictionary<int, ParcelSummary>();
    private Dictionary<int, GameObject> parcelObjectsCache = new Dictionary<int, GameObject>();

    /// <summary>
    /// Spatial index entry for finding parcels under the mouse (GetParcelIdAtPoint).
    /// On uneven terrain, parcel MeshColliders may lie below terrain and evade raycasts,
    /// so resolve parcels with a 2D point-in-polygon test using the terrain hit XZ.
    /// Cache BBOXes and converted polygons on registration to avoid per-frame GC and recalculation.
    /// </summary>
    private class ParcelSpatialEntry
    {
        /// <summary>Polygon AABB (x=world X, y=world Z). Used for early rejection before point-in-polygon checks.</summary>
        public Rect bbox;
        /// <summary>Converted polygon vertices ([i][0]=X, [i][1]=Z). Avoid allocating GetPolygonArray results each time.</summary>
        public float[][] polygon;
        /// <summary>Cells registered in parcelIdsByCell (reverse lookup for removal).</summary>
        public List<Vector2Int> cells;
        /// <summary>Fronting street ID (front_path_id). Reverse lookup when removing from parcelIdsByFrontPath. 0=no road access.</summary>
        public int frontPathId;
        /// <summary>Center point (x=world X, y=world Z). BBOX center if center_point is absent.</summary>
        public Vector2 center;
    }

    /// <summary>Parcel ID to spatial index entry. Synchronized by RegisterConvertedParcelData / RemoveParcel / ClearAllCells.</summary>
    private Dictionary<int, ParcelSpatialEntry> parcelSpatialCache = new Dictionary<int, ParcelSpatialEntry>();

    /// <summary>
    /// Cell to parcel IDs whose BBOXes overlap that cell.
    /// Reverse index allowing GetParcelIdAtPoint to narrow candidates in O(1) without scanning every parcel
    /// (same full-scan avoidance pattern as BuildingManager.buildingIdsByCell).
    /// </summary>
    private Dictionary<Vector2Int, HashSet<int>> parcelIdsByCell = new Dictionary<Vector2Int, HashSet<int>>();

    /// <summary>
    /// Fronting street ID (front_path_id) to IDs of parcels facing that street.
    /// Reverse index finding parcels facing a road without scanning every parcel.
    /// Register through RegisterParcelSpatialIndex; synchronize removals through UnregisterParcelSpatialIndex / ClearAllCells
    /// (same full-scan avoidance pattern as parcelIdsByCell). front_path_id is absent from parcels VERSION_TRIGGER_PROPS,
    /// so road access changes alone do not advance version; update links with RefreshCachedSummary even when versions match.
    /// </summary>
    private Dictionary<int, HashSet<int>> parcelIdsByFrontPath = new Dictionary<int, HashSet<int>>();

    /// <summary>
    /// Cache parcel versions fetched from the backend.
    /// Compare backend versions during incremental updates and recreate objects only if changed.
    /// </summary>
    private Dictionary<int, int> parcelVersionCache = new Dictionary<int, int>();

    private GameObject overlayMeshObj;
    private OverlayMeshRenderer overlayRenderer;

    /// <summary>
    /// Overlay dedicated to data visualization (colored fills).
    /// Unlike overlayRenderer for outlines/highlights (_ZTest=Always),
    /// render with the depth-tested (LEqual), terrain-following Custom/OverlayFillTerrainConform shader.
    /// The shader subdivides triangles with GPU tessellation and places every vertex at terrain height plus lift,
    /// so the CPU submits simple polygons while depth ordering remains
    /// above terrain (never buried on slopes), below buildings (occluded by walls),
    /// and above floor parts sitting just above the ground.
    /// </summary>
    private GameObject dataVisMeshObj;
    private OverlayMeshRenderer dataVisRenderer;

    private const float LINE_THICKNESS = 1.0f;
    private Color parcelLineColor = new Color(0.8f, 0.8f, 0.8f, 1f);

    /// <summary>
    /// Track coroutines launched by LoadLodCells and cancel them on new camera movement.
    /// </summary>
    private List<Coroutine> activeLodCoroutines = new List<Coroutine>();
    public bool IsLoading { get; private set; }

    private HashSet<Vector2Int> fullyLoadedCells = new HashSet<Vector2Int>();

    /// <summary>
    /// Centroid cell (containing center_point = GameObject XZ position) to parcel IDs.
    /// Unloading visibility changes and removal of parcels absent from fetch results (SyncParcelsInCells) use the centroid cell,
    /// so this retrieves only parcels within a cell without scanning the entire cache.
    /// Register through RegisterConvertedParcelData; synchronize removal through RemoveParcel / ClearAllCells.
    /// </summary>
    private readonly Dictionary<Vector2Int, HashSet<int>> parcelIdsByCenterCell = new Dictionary<Vector2Int, HashSet<int>>();
    private readonly Dictionary<int, Vector2Int> parcelCenterCellById = new Dictionary<int, Vector2Int>();

    /// <summary>
    /// Cells that left residency and await parcel hiding. UnloadWorker processes them incrementally within budget.
    /// LoadLodCells removes cells that return before or during processing, and the worker stops processing those cells.
    /// </summary>
    private readonly HashSet<Vector2Int> pendingDeactivateCells = new HashSet<Vector2Int>();
    private readonly List<int> unloadScratch = new List<int>();
    private Coroutine unloadCoroutine;
    private const int UnloadDeactivateBatch = 32;
    private const int UnloadDestroyBatch = 16;

    /// <summary>
    /// Cell where UnloadWorker yielded midway through hiding, and whether it returned to residency meanwhile.
    /// A reshown cell that departs again remains pending, so checking only queue membership would miss the reshow.
    /// </summary>
    private bool hasDeactivatingCell;
    private Vector2Int deactivatingCell;
    private bool deactivatingCellReentered;

    /// <summary>
    /// Oldest-first list of nonresident cells already hidden. Retain hidden parcels to avoid regeneration on reentry,
    /// but discard parcels from oldest cells over capacity so exploration does not cause unbounded growth (UnloadWorker).
    /// LoadLodCells removes cells returning to residency.
    /// </summary>
    private readonly LinkedList<Vector2Int> retainedUnloadedCells = new LinkedList<Vector2Int>();
    private readonly Dictionary<Vector2Int, LinkedListNode<Vector2Int>> retainedCellNodes = new Dictionary<Vector2Int, LinkedListNode<Vector2Int>>();
    private const int MAX_RETAINED_UNLOADED_CELLS = LodConfig.MAX_RESIDENT_CELLS;

    /// <summary>
    /// Loaded cells requiring fetching (incremental application): newly resident cells and all cells on MarkLoadedCellsStale, force, or unchanged residency.
    /// Remove only successfully fetched and applied cells, so canceled loads are refetched by the next LoadLodCells.
    /// Always maintain as a subset of loadedCells.
    /// </summary>
    private readonly HashSet<Vector2Int> cellsNeedingFetch = new HashSet<Vector2Int>();

    /// <summary>
    /// Resident set from the preceding LoadLodCells. If called again with the same set (refetch or force), safely refetch every cell.
    /// </summary>
    private HashSet<Vector2Int> lastResidentCells;

    /// <summary>
    /// Increment on every MarkLoadedCellsStale. If refetch is requested during fetching, prevent completion from clearing refetch markers.
    /// </summary>
    private int staleMarkSerial;

    /// <summary>
    /// Parcel IDs queued for terrain alignment when tiles arrive. terrainRefreshQueued prevents duplicate insertion.
    /// Synchronously rebuilding every parcel in one tile makes long frames, so TerrainRefreshWorker processes within budget.
    /// </summary>
    private readonly Queue<int> terrainRefreshQueue = new Queue<int>();
    private readonly HashSet<int> terrainRefreshQueued = new HashSet<int>();
    private Coroutine terrainRefreshCoroutine;

    /// <summary>
    /// If resampled heights differ from stored outline values by no more than this amount, consider terrain unchanged and skip rebuilding.
    /// OnTerrainTileReady also fires on tile reattachment or refetching without a version change.
    /// </summary>
    private const float TerrainHeightEpsilon = 0.005f;

    /// <summary>Number of unchanged-height parcels TerrainRefreshWorker batches before yielding its budget.</summary>
    private const int TerrainRefreshSkipBatch = 16;
    private const float PolygonXZEpsilon = 1e-4f;

    /// <summary>Scratch space for terrain-following height samples. Used only synchronously; never retained across yields.</summary>
    private float[] groundSampleScratch = new float[32];

    /// <summary>Hover highlight color. Translucent blue matching HighlightMaterial.mat (alpha increased for clarity).</summary>
    private static readonly Color HighlightColor = new Color(0.259f, 0.437f, 1.0f, 0.6f);

    /// <summary>Offset (meters) lifting highlights above terrain</summary>
    private const float HighlightHeightOffset = 0.05f;

    /// <summary>Currently hovered parcel ID. -1 means no highlight.</summary>
    private int highlightedParcelId = -1;

    /// <summary>
    /// Parcel ID to fill color for path interference highlights.
    /// Fill parcels acquired/intersected by planned paths using category colors (set by HighlightManager).
    /// Independent of the single hover highlight layer; both may be rendered simultaneously.
    /// </summary>
    private Dictionary<int, Color> interferenceParcelColors = new Dictionary<int, Color>();

    // Throttle outline updates by detecting camera changes
    private Vector3 lastCamPos = Vector3.negativeInfinity;
    private Quaternion lastCamRot = Quaternion.identity;
    private const float CAMERA_POS_THRESHOLD = 1.0f;
    private const float CAMERA_ROT_THRESHOLD = 2.0f;

    /// <summary>Offset (meters) slightly lifting parcel tops above terrain to avoid Z-fighting</summary>
    private const float ParcelSurfaceOffset = 0.002f;

    /// <summary>
    /// Offset (meters) raising water parcels above the sea plane.
    /// The scene sea plane is at worldY=0.001; the tiny terrain offset alone
    /// leaves only 1mm separation, insufficient to guarantee raycasts hit parcels before the sea.
    /// </summary>
    private const float SeaSurfaceClearance = 0.05f;

    /// <summary>Offset (meters) lifting outlines above terrain</summary>
    private const float OutlineHeightOffset = 0.03f;

    // ================= Road access (front_path) arrow display =================
    /// <summary>Road access arrow color (blue).</summary>
    private static readonly Color FrontPathArrowColor = new Color(0.15f, 0.45f, 1f, 1f);
    /// <summary>Road access arrow line width (pixels), thicker than outlines for emphasis.</summary>
    private const float FrontPathArrowThickness = 3.0f;
    /// <summary>Offset (meters) lifting road access arrows above terrain, higher than outlines to avoid overlap.</summary>
    private const float FrontPathArrowHeightOffset = 0.1f;
    /// <summary>Arrow shaft half-length (meters). Extend this far inward/outward from the fronting edge midpoint.</summary>
    private const float FrontPathArrowHalfLength = 4.0f;
    /// <summary>Arrowhead length (meters).</summary>
    private const float FrontPathArrowHeadLength = 2.5f;
    /// <summary>Arrowhead half-width (meters).</summary>
    private const float FrontPathArrowHeadHalfWidth = 1.5f;

    /// <summary>Whether the road access arrow is visible.</summary>
    private bool frontPathArrowActive = false;
    /// <summary>Target parcel ID for the road access arrow. Detects asynchronous fetch races (-1 means none).</summary>
    private int frontPathParcelId = -1;
    /// <summary>Road access fetch coroutine. Stop on a new request.</summary>
    private Coroutine frontPathCoroutine;
    /// <summary>Arrow shaft world coordinates (start=inside parcel, end=road-side arrowhead tip).</summary>
    private Vector3 frontPathShaftStart;
    private Vector3 frontPathShaftEnd;
    /// <summary>Arrowhead triangle vertices in world coordinates (double-sided: 6 vertices=2 triangles). Reused to avoid per-frame GC.</summary>
    private readonly List<Vector3> frontPathArrowHead = new List<Vector3>(6);

    /// <summary>
    /// Current parcel visual mode.
    /// Set by ApplyVisualMode and read by UpdateAllParcelOutlines.
    /// Data visualization modes (200 and above) draw colored polygon overlays.
    /// </summary>
    private int currentParcelVisualMode = VisualMode.MODE_NORMAL;

    /// <summary>
    /// Environmental scores (noise, air pollution, public safety) as parcel_id to relative values from 0 to 1.
    /// The backend returns citywide scores already sampled at parcel centers, so
    /// fetch once per mode independently of parcel bbox requests and look up by ID.
    /// </summary>
    private readonly Dictionary<int, float> environmentScores = new Dictionary<int, float>();

    /// <summary>Mode held by environmentScores (0=not fetched)</summary>
    private int loadedEnvironmentMode = 0;

    /// <summary>Requested mode. Discard results if it differs when the response arrives</summary>
    private int requestedEnvironmentMode = 0;

    private Coroutine environmentCoroutine;

    /// <summary>
    /// Cached VisualParcelObjects for parcels.
    /// Register in CreateOrUpdateParcelObject and clear on removal
    /// to avoid GetComponent calls every frame.
    /// </summary>
    private Dictionary<int, VisualParcelObject> parcelVisualObjects = new Dictionary<int, VisualParcelObject>();

    /// <summary>Base alpha for data visualization polygons, matching hover highlight opacity</summary>
    private const float DataVisAlpha = 0.6f;

    // Distance-based opacity (game-space meters, on a scale near Terrain's distant LOD display range)
    private const float DISTANCE_FULL_VISIBLE = 150f;
    private const float DISTANCE_START_FADE = 280f;
    private const float DISTANCE_INVISIBLE = 480f;

    /// <summary>
    /// Squared fade-start and hide distances,
    /// used to reduce square-root costs during alpha fade checks.
    /// </summary>
    private const float DISTANCE_START_FADE_SQ = DISTANCE_START_FADE * DISTANCE_START_FADE;
    private const float DISTANCE_INVISIBLE_SQ = DISTANCE_INVISIBLE * DISTANCE_INVISIBLE;

    private void Awake()
    {
        RegisterWithVisualModeHandler();
    }

    private void Start()
    {
        EnsureOverlayRenderer();
        // If VisualModeHandler was not yet instantiated during Awake,
        // retry registration in Start. HashSet semantics ignore already registered entries.
        RegisterWithVisualModeHandler();
        if (VisualModeHandler.Instance == null)
        {
            RenderWorkScheduler.Start(this, WaitAndRegisterVisualModeHandler(), "ParcelManager");
        }
        TerrainManager.OnTerrainTileReady += OnTerrainTileReady;
    }

    private System.Collections.IEnumerator WaitAndRegisterVisualModeHandler()
    {
        while (VisualModeHandler.Instance == null)
        {
            yield return null;
        }
        RegisterWithVisualModeHandler();
    }

    private void EnsureOverlayRenderer()
    {
        if (overlayMeshObj != null) return;

        // Render outlines/highlights without depth testing (alwaysOnTop) to keep them above uneven terrain.
        overlayRenderer = OverlayMeshRenderer.Create("ParcelOverlayMesh", alwaysOnTop: true, persistent: true);
        overlayMeshObj = overlayRenderer.gameObject;

        // Dedicated renderer for data visualization (colored fills). Parent under overlayMeshObj to share visibility toggles.
        // Use the depth-tested (LEqual) Custom/OverlayFillTerrainConform shader.
        // GPU tessellation and heightmap following place triangle interiors at terrain height plus lift,
        // keeping them above uneven terrain while naturally occluded by buildings with depth testing enabled.
        dataVisMeshObj = new GameObject("ParcelDataVisMesh");
        dataVisMeshObj.transform.SetParent(overlayMeshObj.transform, false);
        dataVisRenderer = dataVisMeshObj.AddComponent<OverlayMeshRenderer>();
        dataVisRenderer.Initialize(new Material(Shader.Find("Custom/OverlayFillTerrainConform")));
    }

    public void RemoveParcel(int parcelId)
    {
        if (parcelObjectsCache.TryGetValue(parcelId, out GameObject obj))
        {
            DestroyParcelObject(obj);
        }
        parcelObjectsCache.Remove(parcelId);
        parcelVisualObjects.Remove(parcelId);

        RemoveParcelOutline(parcelId);
        UnregisterParcelSpatialIndex(parcelId);
        UnregisterParcelCenterCell(parcelId);
        parcelCache.Remove(parcelId);
        // Remove the version cache too. Otherwise a deleted/recreated parcel with the same parcel_id and version
        // would be skipped by FetchAndApplyCells version matching,
        // and its outline would never return.
        parcelVersionCache.Remove(parcelId);
    }

    /// <summary>
    /// Destroy the parcel GameObject together with its newly created top mesh (destroying only the GameObject leaves the Mesh).
    /// MeshCollider shares the same Mesh, so do not destroy it separately.
    /// </summary>
    private static void DestroyParcelObject(GameObject obj)
    {
        if (obj == null) return;
        MeshFilter mf = obj.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
        Destroy(obj);
    }

    /// <summary>
    /// Queue the cell for hiding. Parcels are hidden rather than destroyed; UnloadWorker performs the actual SetActive(false).
    /// </summary>
    protected override void UnloadCell(Vector2Int cellIndex)
    {
        pendingDeactivateCells.Add(cellIndex);
        fullyLoadedCells.Remove(cellIndex);
        cellsNeedingFetch.Remove(cellIndex);
    }

    private bool HasUnloadWork => pendingDeactivateCells.Count > 0 || retainedUnloadedCells.Count > MAX_RETAINED_UNLOADED_CELLS;

    private void StartUnloadWorker()
    {
        if (unloadCoroutine != null || !HasUnloadWork || !isActiveAndEnabled) return;
        unloadCoroutine = RenderWorkScheduler.Start(this, UnloadWorker(), "ParcelManager", () => HasUnloadWork);
    }

    private void AddRetainedCell(Vector2Int cell)
    {
        RemoveRetainedCell(cell);
        retainedCellNodes[cell] = retainedUnloadedCells.AddLast(cell);
    }

    private void RemoveRetainedCell(Vector2Int cell)
    {
        if (!retainedCellNodes.TryGetValue(cell, out LinkedListNode<Vector2Int> node)) return;
        retainedUnloadedCells.Remove(node);
        retainedCellNodes.Remove(cell);
    }

    /// <summary>
    /// Take pending cells one at a time and hide parcels whose centroids belong to them within budget.
    /// After all pending cells are hidden, destroy parcels from old cells over the retention limit within budget.
    /// Exclude from activeLodCoroutines so camera movement does not stop it.
    /// </summary>
    private IEnumerator UnloadWorker()
    {
        try
        {
            while (HasUnloadWork)
            {
                if (pendingDeactivateCells.Count > 0)
                {
                    Vector2Int cell = default;
                    foreach (Vector2Int c in pendingDeactivateCells) { cell = c; break; }

                    unloadScratch.Clear();
                    if (parcelIdsByCenterCell.TryGetValue(cell, out HashSet<int> ids)) unloadScratch.AddRange(ids);

                    hasDeactivatingCell = true;
                    deactivatingCell = cell;
                    deactivatingCellReentered = false;
                    bool reentered = false;
                    for (int i = 0; i < unloadScratch.Count; i++)
                    {
                        if (parcelObjectsCache.TryGetValue(unloadScratch[i], out GameObject obj) && obj != null && obj.activeSelf)
                            obj.SetActive(false);
                        if ((i + 1) % UnloadDeactivateBatch == 0)
                        {
                            yield return RenderWorkScheduler.Continue;
                            // Stop when the cell returns to residency. Fetch-side version matching reshows already hidden parcels.
                            // If it leaves again after returning, it remains pending; restart from the beginning on the next pass.
                            if (deactivatingCellReentered || !pendingDeactivateCells.Contains(cell)) { reentered = true; break; }
                        }
                    }
                    hasDeactivatingCell = false;
                    if (!reentered)
                    {
                        pendingDeactivateCells.Remove(cell);
                        AddRetainedCell(cell);
                    }
                    yield return RenderWorkScheduler.Continue;
                    continue;
                }

                Vector2Int oldest = retainedUnloadedCells.First.Value;
                RemoveRetainedCell(oldest);
                unloadScratch.Clear();
                if (parcelIdsByCenterCell.TryGetValue(oldest, out HashSet<int> oldestIds)) unloadScratch.AddRange(oldestIds);
                for (int i = 0; i < unloadScratch.Count; i++)
                {
                    // Do not destroy parcels in cells returning to residency (fetch-side version matching reshows the rest)
                    if (loadedCells.Contains(oldest)) break;
                    RemoveParcel(unloadScratch[i]);
                    if ((i + 1) % UnloadDestroyBatch == 0) yield return RenderWorkScheduler.Continue;
                }
                yield return RenderWorkScheduler.Continue;
            }
        }
        finally
        {
            unloadCoroutine = null;
            hasDeactivatingCell = false;
        }
    }

    // ================================================================
    // LOD-aware cell loading (merged BBOX)
    // ================================================================

    public override void LoadLodCells(LodCellSets cellSets, bool isForce)
    {
        // Cancel all older running coroutines (handling repeated calls on camera movement)
        foreach (var c in activeLodCoroutines)
        {
            if (c != null) RenderWorkScheduler.Stop(this, c);
        }
        activeLodCoroutines.Clear();
        IsLoading = false;

        var resident = new HashSet<Vector2Int>(cellSets.farCells);
        bool sameResident = lastResidentCells != null && lastResidentCells.SetEquals(resident);
        lastResidentCells = resident;

        // Unload unneeded cells
        var toUnload = new List<Vector2Int>();
        foreach (var cell in loadedCells)
        {
            if (!resident.Contains(cell))
                toUnload.Add(cell);
        }
        foreach (var cell in toUnload)
        {
            UnloadCell(cell);
            loadedCells.Remove(cell);
        }

        // Fetch only newly resident cells and cells needing refetch. Refetch loaded-cell parcels
        // on data refetch (MarkLoadedCellsStale), force, or unchanged residency (a refetch safety net).
        foreach (var cell in resident)
        {
            if (loadedCells.Add(cell))
            {
                cellsNeedingFetch.Add(cell);
                pendingDeactivateCells.Remove(cell);
                if (hasDeactivatingCell && deactivatingCell == cell) deactivatingCellReentered = true;
                RemoveRetainedCell(cell);
            }
            else if (isForce || sameResident)
            {
                cellsNeedingFetch.Add(cell);
            }
        }

        StartUnloadWorker();

        if (cellsNeedingFetch.Count > 0)
        {
            IsLoading = true;
            // Building generation waits for this load, so it runs ahead of normal render work like terrain tile publishing.
            activeLodCoroutines.Add(RenderWorkScheduler.Start(this, TrackParcelLoad(new List<Vector2Int>(cellsNeedingFetch)), "ParcelManager",
                priority: RenderWorkPriority.High));
        }
    }

    /// <summary>
    /// Keep IsLoading true until required parcel fetches and application finish (buildings generate after parcels are finalized).
    /// </summary>
    private IEnumerator TrackParcelLoad(List<Vector2Int> cells)
    {
        try { yield return FetchAndApplyCells(cells); }
        finally { IsLoading = false; }
    }

    /// <summary>
    /// Read parcels overlapping the cells' bounding rectangle from the city cache and incrementally apply only parcels with centroids in the set.
    /// The rectangle can include nonresident cells; filtering by centroid cell prevents creating GameObjects that no cell will hide.
    /// </summary>
    private IEnumerator FetchAndApplyCells(List<Vector2Int> cells)
    {
        int serialAtRequest = staleMarkSerial;
        CalculateMergedBBox(cells, out float minX, out float minZ, out float maxX, out float maxZ);
        var area = Rect.MinMaxRect(minX, minZ, maxX, maxZ);
        bool available = false;
        CityDataCache cache = CityDataCache.Ensure();
        yield return cache.EnsureRect(CityDataCache.Kind.Parcels, area, null, ok => available = ok);
        // Failed cells remain in cellsNeedingFetch and are refetched by the next LoadLodCells
        if (!available) yield break;

        List<ParcelSummary> parcels = cache.ParcelsIn(area);
        int count = parcels.Count;

        var cellSet = new HashSet<Vector2Int>(cells);
        var receivedIds = new HashSet<int>();
        var slice = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            // Yield by elapsed time: one parcel takes well under a millisecond, and yielding per parcel would wait
            // a whole scheduler round behind heavy workers each time.
            if (slice.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
            {
                yield return RenderWorkScheduler.Continue;
                slice.Restart();
            }
            ParcelSummary p = parcels[i];
            if (p == null || !TryGetCenterCell(p, out Vector2Int centerCell) || !cellSet.Contains(centerCell)) continue;
            receivedIds.Add(p.parcel_id);

            // Version comparison: do not recreate if the cached version matches
            if (parcelVersionCache.TryGetValue(p.parcel_id, out int cachedVersion) && cachedVersion == p.version)
            {
                // Reshow an existing object if it was hidden
                if (parcelObjectsCache.TryGetValue(p.parcel_id, out GameObject existing) && existing != null && !existing.activeSelf)
                {
                    existing.SetActive(true);
                }
                RefreshCachedSummary(p);
            }
            else
            {
                CreateOrUpdateParcelObject(p);
                parcelVersionCache[p.parcel_id] = p.version;
            }
        }

        // Remove parcels whose centroids lie in fetched cells but are absent from the result
        SyncParcelsInCells(cellSet, receivedIds);

        // If refetch was requested during fetching, this response may be old; retain the refetch marker
        bool keepStale = serialAtRequest != staleMarkSerial;
        foreach (var cell in cells)
        {
            if (!keepStale) cellsNeedingFetch.Remove(cell);
            fullyLoadedCells.Add(cell);
        }
        ForceOutlineRedraw();
    }

    private static bool TryGetCenterCell(ParcelSummary p, out Vector2Int cell)
    {
        cell = default;
        if (p.center_point == null || p.center_point.Count < 2) return false;
        cell = TerrainManager.WorldToCell(GeneratedModelUtils.ToFloat(p.center_point[0]), GeneratedModelUtils.ToFloat(p.center_point[1]));
        return true;
    }

    /// <summary>
    /// Remove parcels whose centroids lie in the specified cells but are absent from the API response.
    /// </summary>
    private void SyncParcelsInCells(HashSet<Vector2Int> cellSet, HashSet<int> receivedIds)
    {
        List<int> idsToRemove = new List<int>();
        foreach (Vector2Int cell in cellSet)
        {
            if (!parcelIdsByCenterCell.TryGetValue(cell, out HashSet<int> ids)) continue;
            foreach (int id in ids)
            {
                if (!receivedIds.Contains(id)) idsToRemove.Add(id);
            }
        }

        foreach (int id in idsToRemove)
        {
            RemoveParcel(id);
        }
    }

    public override void ClearAllCells()
    {
        foreach (var c in activeLodCoroutines)
        {
            if (c != null) RenderWorkScheduler.Stop(this, c);
        }
        activeLodCoroutines.Clear();
        IsLoading = false;
        RenderWorkScheduler.Stop(this, unloadCoroutine);
        unloadCoroutine = null;
        hasDeactivatingCell = false;
        RenderWorkScheduler.Stop(this, terrainRefreshCoroutine);
        terrainRefreshCoroutine = null;
        pendingDeactivateCells.Clear();
        retainedUnloadedCells.Clear();
        retainedCellNodes.Clear();
        cellsNeedingFetch.Clear();
        lastResidentCells = null;
        terrainRefreshQueue.Clear();
        terrainRefreshQueued.Clear();

        foreach (var obj in parcelObjectsCache.Values)
        {
            DestroyParcelObject(obj);
        }

        outlines.Clear();
        parcelIdToOutlineIndex.Clear();
        parcelCache.Clear();
        parcelObjectsCache.Clear();
        parcelVisualObjects.Clear();
        parcelVersionCache.Clear();
        parcelSpatialCache.Clear();
        parcelIdsByCell.Clear();
        parcelIdsByFrontPath.Clear();
        parcelIdsByCenterCell.Clear();
        parcelCenterCellById.Clear();

        if (overlayRenderer != null)
        {
            overlayRenderer.ClearMesh();
        }
        if (dataVisRenderer != null)
        {
            dataVisRenderer.ClearMesh();
        }

        loadedCells.Clear();
        fullyLoadedCells.Clear();
    }

    public GameObject GetParcelObject(int parcelId)
    {
        if (parcelObjectsCache.TryGetValue(parcelId, out GameObject obj) && obj != null)
            return obj;
        return null;
    }

    public List<ParcelSummary> getAllParcels()
    {
        List<ParcelSummary> parcels = new List<ParcelSummary>();
        foreach (var kvp in parcelCache)
        {
            parcels.Add(kvp.Value);
        }
        return parcels;
    }

    /// <summary>
    /// Get the ParcelSummary for parcel_id from the cache.
    /// Return null if absent.
    /// </summary>
    public ParcelSummary GetParcelSummary(int parcelId)
    {
        parcelCache.TryGetValue(parcelId, out ParcelSummary summary);
        return summary;
    }

    /// <summary>
    /// Get the parcel building use ID (VALUE_NONE=-1 for vacant land).
    /// Used to regenerate parcel objects when the use ID changes.
    /// </summary>
    private int GetParcelStatus(ParcelSummary parcelData)
    {
        return parcelData.usage_id;
    }

    private void CreateOrUpdateParcelObject(ParcelSummary parcelData)
    {
        float[][] polygon = parcelData.GetPolygonArray();
        float[] centerPoint = parcelData.GetCenterPointArray();

        GameObject existingParcel = GetParcelObject(parcelData.parcel_id);
        bool needRecreate = false;

        if (existingParcel != null)
        {
            var existingIdInfo = existingParcel.GetComponent<ObjectIdInfo>();
            int currentStatus = existingIdInfo != null ? existingIdInfo.status : 0;
            int newStatus = GetParcelStatus(parcelData);

            if (currentStatus != newStatus)
            {
                RemoveParcel(parcelData.parcel_id);
                needRecreate = true;
            }
            else
            {
                // Pool reuse: reshow a hidden object
                if (!existingParcel.activeSelf)
                    existingParcel.SetActive(true);
                // Version changes may alter geometry, so reregister including the spatial index
                RegisterConvertedParcelData(parcelData, polygon, centerPoint);
                UpdateParcelStats(parcelData.parcel_id, parcelData);
                // Rebuild meshes, colliders, and outlines only if geometry/heights differ from stored outline values
                if (centerPoint != null && centerPoint.Length >= 2)
                {
                    RefreshParcelGeometry(parcelData, polygon, new Vector2(centerPoint[0], centerPoint[1]));
                }
            }
        }
        else
        {
            needRecreate = true;
        }

        if (needRecreate)
        {
            RegisterConvertedParcelData(parcelData, polygon, centerPoint);

            Vector2 center = new Vector2(centerPoint[0], centerPoint[1]);
            float[] vertexGround = SampleParcelGround(parcelData, polygon, center, out float centerGround);

            if (existingParcel == null && CellDataLoader.Instance != null && CellDataLoader.Instance.IsInitialLoadComplete)
            {
                Vector2Int cellIndex = TerrainManager.WorldToCell(center.x, center.y);
                if (fullyLoadedCells.Contains(cellIndex) && VisualEffectManager.Instance != null)
                {
                    VisualEffectManager.Instance.PlayConstructionComplete(new Vector3(center.x, centerGround, center.y));
                }
            }

            int status = GetParcelStatus(parcelData);
            GameObject parcelGO = new GameObject($"{PARCEL_PREFIX}{parcelData.parcel_id}");
            var idInfo = parcelGO.AddComponent<ObjectIdInfo>();
            idInfo.id = parcelData.parcel_id;
            idInfo.status = status;
            Transform container = GetContainer("Parcels");
            if (container != null) parcelGO.transform.SetParent(container);

            parcelGO.tag = PARCEL_TAG;

            parcelGO.AddComponent<MeshFilter>();
            MeshRenderer mr = parcelGO.AddComponent<MeshRenderer>();
            if (vacantParcelMaterial != null)
            {
                mr.sharedMaterial = vacantParcelMaterial;
            }
            mr.enabled = false;
            parcelGO.AddComponent<MeshCollider>();

            ApplyParcelGeometry(parcelGO, parcelData, polygon, center, vertexGround, centerGround);

            ParcelInputHandler inputHandler = parcelGO.AddComponent<ParcelInputHandler>();
            inputHandler.parcelId = parcelData.parcel_id;

            var vpoComp = parcelGO.AddComponent<VisualParcelObject>();
            parcelVisualObjects[parcelData.parcel_id] = vpoComp;

            var stats = parcelGO.AddComponent<VisualParcelStats>();
            stats.InitFrom(parcelData);

            parcelObjectsCache[parcelData.parcel_id] = parcelGO;
        }
    }

    /// <summary>Refetch one parcel in the list form (the same form the city cache holds) and redraw it.</summary>
    public IEnumerator FetchParcel(int parcelId)
    {
        yield return ApiClient.GetBulk<ParcelListResponse>(
            $"/parcels/?ids={parcelId}",
            response =>
            {
                if (response?.parcels == null || response.parcels.Count == 0) return;
                ParcelSummary parcel = response.parcels[0];
                CityDataCache.Ensure().Register(parcel);
                CreateOrUpdateParcelObject(parcel);
                parcelVersionCache[parcel.parcel_id] = parcel.version;
            },
            error => Debug.LogError($"FetchParcel({parcelId}) failed: {error}")
        );
    }

    private void RegisterParcelDataForLines(ParcelSummary parcelData)
    {
        RegisterConvertedParcelData(parcelData, parcelData.GetPolygonArray(), parcelData.GetCenterPointArray());
    }

    /// <summary>
    /// Converted-coordinate version of RegisterParcelDataForLines. Do not overload; tests resolve it by name through reflection.
    /// </summary>
    private void RegisterConvertedParcelData(ParcelSummary parcelData, float[][] polygon, float[] centerPoint)
    {
        parcelCache[parcelData.parcel_id] = parcelData;
        RegisterParcelSpatialIndex(parcelData, polygon, centerPoint);
        RegisterParcelCenterCell(parcelData.parcel_id, centerPoint);
    }

    /// <summary>
    /// Register the parcel in the centroid-cell reverse index (parcelIdsByCenterCell). Remove from the old cell if its centroid cell changed.
    /// Do not register parcels without center_point, since they cannot have GameObjects.
    /// </summary>
    private void RegisterParcelCenterCell(int parcelId, float[] centerPoint)
    {
        if (centerPoint == null || centerPoint.Length < 2)
        {
            UnregisterParcelCenterCell(parcelId);
            return;
        }
        Vector2Int cell = TerrainManager.WorldToCell(centerPoint[0], centerPoint[1]);
        if (parcelCenterCellById.TryGetValue(parcelId, out Vector2Int oldCell))
        {
            if (oldCell == cell) return;
            UnregisterParcelCenterCell(parcelId);
        }
        if (!parcelIdsByCenterCell.TryGetValue(cell, out HashSet<int> ids))
        {
            ids = new HashSet<int>();
            parcelIdsByCenterCell[cell] = ids;
        }
        ids.Add(parcelId);
        parcelCenterCellById[parcelId] = cell;
    }

    private void UnregisterParcelCenterCell(int parcelId)
    {
        if (!parcelCenterCellById.TryGetValue(parcelId, out Vector2Int cell)) return;
        if (parcelIdsByCenterCell.TryGetValue(cell, out HashSet<int> ids))
        {
            ids.Remove(parcelId);
            if (ids.Count == 0) parcelIdsByCenterCell.Remove(cell);
        }
        parcelCenterCellById.Remove(parcelId);
    }

    /// <summary>
    /// Register the parcel in the spatial index (parcelSpatialCache / parcelIdsByCell).
    /// Calculate its polygon AABB and add reverse entries in every overlapping cell (usually 1–4 because parcels are small).
    /// If already registered, remove before reregistering to handle cell changes caused by geometry updates.
    /// </summary>
    private void RegisterParcelSpatialIndex(ParcelSummary parcelData, float[][] polygon, float[] centerPoint)
    {
        if (polygon == null || polygon.Length < 3) return;

        UnregisterParcelSpatialIndex(parcelData.parcel_id);

        float minX = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxZ = float.MinValue;
        foreach (float[] pt in polygon)
        {
            if (pt == null || pt.Length < 2) continue;
            if (pt[0] < minX) minX = pt[0];
            if (pt[0] > maxX) maxX = pt[0];
            if (pt[1] < minZ) minZ = pt[1];
            if (pt[1] > maxZ) maxZ = pt[1];
        }
        if (minX > maxX || minZ > maxZ) return;

        int frontPathId = parcelData.GetFrontPathId();
        var entry = new ParcelSpatialEntry
        {
            bbox = Rect.MinMaxRect(minX, minZ, maxX, maxZ),
            polygon = polygon,
            cells = new List<Vector2Int>(),
            frontPathId = frontPathId,
            center = centerPoint != null && centerPoint.Length >= 2
                ? new Vector2(centerPoint[0], centerPoint[1])
                : new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f)
        };

        AddFrontPathIndex(frontPathId, parcelData.parcel_id);

        TerrainManager.WorldRectToCellRange(minX, minZ, maxX, maxZ, out Vector2Int cellMin, out Vector2Int cellMax);
        for (int cx = cellMin.x; cx <= cellMax.x; cx++)
        {
            for (int cz = cellMin.y; cz <= cellMax.y; cz++)
            {
                Vector2Int cell = new Vector2Int(cx, cz);
                if (!parcelIdsByCell.TryGetValue(cell, out HashSet<int> ids))
                {
                    ids = new HashSet<int>();
                    parcelIdsByCell[cell] = ids;
                }
                ids.Add(parcelData.parcel_id);
                entry.cells.Add(cell);
            }
        }

        parcelSpatialCache[parcelData.parcel_id] = entry;
    }

    /// <summary>
    /// Remove the parcel from the spatial index. Called by RemoveParcel.
    /// </summary>
    private void UnregisterParcelSpatialIndex(int parcelId)
    {
        if (!parcelSpatialCache.TryGetValue(parcelId, out ParcelSpatialEntry entry)) return;

        foreach (Vector2Int cell in entry.cells)
        {
            if (parcelIdsByCell.TryGetValue(cell, out HashSet<int> ids))
            {
                ids.Remove(parcelId);
                if (ids.Count == 0) parcelIdsByCell.Remove(cell);
            }
        }

        RemoveFrontPathIndex(entry.frontPathId, parcelId);

        parcelSpatialCache.Remove(parcelId);
    }

    /// <summary>Register in the fronting street reverse index (skip if front_path_id is unavailable=0).</summary>
    private void AddFrontPathIndex(int frontPathId, int parcelId)
    {
        if (frontPathId <= 0) return;
        if (!parcelIdsByFrontPath.TryGetValue(frontPathId, out HashSet<int> byPath))
        {
            byPath = new HashSet<int>();
            parcelIdsByFrontPath[frontPathId] = byPath;
        }
        byPath.Add(parcelId);
    }

    private void RemoveFrontPathIndex(int frontPathId, int parcelId)
    {
        if (frontPathId <= 0 || !parcelIdsByFrontPath.TryGetValue(frontPathId, out HashSet<int> byPath)) return;
        byPath.Remove(parcelId);
        if (byPath.Count == 0) parcelIdsByFrontPath.Remove(frontPathId);
    }

    /// <summary>
    /// Replace the cached summary with the latest response when versions match.
    /// land_price and front_path_id are absent from VERSION_TRIGGER_PROPS and may change with the same version.
    /// Matching versions imply matching geometry, so leave meshes and spatial index geometry untouched.
    /// </summary>
    private void RefreshCachedSummary(ParcelSummary parcelData)
    {
        if (!parcelCache.ContainsKey(parcelData.parcel_id)) return;
        parcelCache[parcelData.parcel_id] = parcelData;
        UpdateParcelStats(parcelData.parcel_id, parcelData);
        UpdateFrontPathIndex(parcelData.parcel_id, parcelData.GetFrontPathId());
    }

    private void UpdateParcelStats(int parcelId, ParcelSummary parcelData)
    {
        if (!parcelVisualObjects.TryGetValue(parcelId, out VisualParcelObject vpo) || vpo == null) return;
        VisualParcelStats stats = vpo.Stats;
        if (stats != null) stats.InitFrom(parcelData);
    }

    private void UpdateFrontPathIndex(int parcelId, int frontPathId)
    {
        if (!parcelSpatialCache.TryGetValue(parcelId, out ParcelSpatialEntry entry) || entry.frontPathId == frontPathId) return;
        RemoveFrontPathIndex(entry.frontPathId, parcelId);
        AddFrontPathIndex(frontPathId, parcelId);
        entry.frontPathId = frontPathId;
    }

    /// <summary>
    /// Return IDs of parcels facing the specified street (front_path_id).
    /// General reverse lookup resolving parcels facing a road without backend communication.
    /// Always return an empty set if front_path_id is unavailable (schema not regenerated).
    /// </summary>
    public IReadOnlyCollection<int> GetParcelIdsByFrontPath(int pathId)
    {
        if (pathId > 0 && parcelIdsByFrontPath.TryGetValue(pathId, out HashSet<int> ids))
        {
            return ids;
        }
        return System.Array.Empty<int>();
    }

    /// <summary>
    /// Return the ID of the parcel containing the specified world XZ position, or -1 if absent.
    /// When uneven terrain buries a parcel MeshCollider below the ground, preventing raycast hits,
    /// InputHandler uses this fallback to resolve a parcel from a terrain hit point
    /// (parcels conceptually represent ground areas, so 2D containment suffices).
    /// Narrow candidates through cell reverse index, BBOX, then ray-casting containment tests,
    /// avoiding full linear scans even when called every mouse-movement frame.
    /// </summary>
    public int GetParcelIdAtPoint(Vector3 worldPos)
    {
        Vector2Int cell = TerrainManager.WorldToCell(worldPos);
        if (!parcelIdsByCell.TryGetValue(cell, out HashSet<int> ids)) return -1;

        Vector2 point = new Vector2(worldPos.x, worldPos.z);
        foreach (int id in ids)
        {
            if (!parcelSpatialCache.TryGetValue(id, out ParcelSpatialEntry entry)) continue;
            if (!entry.bbox.Contains(point)) continue;
            if (PolygonUtils.Contains(entry.polygon, point.x, point.y))
            {
                // Parcels do not overlap, so return the first match
                return id;
            }
        }
        return -1;
    }

    /// <summary>
    /// Check whether the specified world XZ position lies inside the specified parcel polygon.
    /// Return false if the parcel is unloaded (absent from the spatial cache).
    /// </summary>
    public bool IsPointInParcel(int parcelId, Vector3 worldPos)
    {
        if (!parcelSpatialCache.TryGetValue(parcelId, out ParcelSpatialEntry entry)
            || entry.polygon == null || entry.polygon.Length < 3)
        {
            return false;
        }
        return PolygonUtils.Contains(entry.polygon, worldPos.x, worldPos.z);
    }

    /// <summary>
    /// Return the spatial index's converted polygon (world XZ) without allocation. This shared array must not be modified.
    /// False if the parcel is unloaded.
    /// </summary>
    public bool TryGetParcelPolygon(int parcelId, out float[][] polygon)
    {
        polygon = parcelSpatialCache.TryGetValue(parcelId, out ParcelSpatialEntry entry) ? entry.polygon : null;
        return polygon != null;
    }

    /// <summary>
    /// Mark every loaded cell as needing refetch. Called synchronously when data refetching (CellDataLoader.ReloadAllData) is requested.
    /// Markers survive batch cancellation and clear when their cell is successfully fetched and applied.
    /// </summary>
    public void MarkLoadedCellsStale()
    {
        cellsNeedingFetch.UnionWith(loadedCells);
        staleMarkSerial++;
    }

    // Distance (m) to move a point inward after clamping to the parcel boundary.
    // Coordinates exactly on the boundary may shift slightly outside during backend coordinate normalization (rounding)
    // and fail containment checks, so send a clearly interior point.
    private const float ParcelClampInnerMargin = 0.05f;

    /// <summary>
    /// Clamp world XZ coordinates inside the specified parcel polygon.
    /// Return the original point if inside, or the nearest polygon boundary point (moved slightly inward)
    /// if outside. Preserve the input Y coordinate.
    /// Used to constrain dragged coordinates to a parcel, such as terminal nodes permitted to move only
    /// within the facility's parcel (ConnectorEditManager).
    /// If the parcel is unloaded (absent from the spatial cache), return false and set clamped to the input.
    /// </summary>
    public bool TryClampPointToParcel(int parcelId, Vector3 worldPos, out Vector3 clamped)
    {
        clamped = worldPos;
        if (!parcelSpatialCache.TryGetValue(parcelId, out ParcelSpatialEntry entry)
            || entry.polygon == null || entry.polygon.Length < 3)
        {
            return false;
        }

        Vector2 point = new Vector2(worldPos.x, worldPos.z);
        if (PolygonUtils.Contains(entry.polygon, point.x, point.y))
        {
            return true;
        }

        // Outside the parcel: project to the nearest among closest points on every edge.
        float[][] polygon = entry.polygon;
        float bestSqDist = float.PositiveInfinity;
        Vector2 bestPoint = point;
        for (int i = 0; i < polygon.Length; i++)
        {
            int next = (i + 1) % polygon.Length;
            Vector2 a = new Vector2(polygon[i][0], polygon[i][1]);
            Vector2 b = new Vector2(polygon[next][0], polygon[next][1]);
            Vector2 candidate = PolylineUtils.ClosestPointOnSegment(point, a, b);
            float sqDist = (candidate - point).sqrMagnitude;
            if (sqDist < bestSqDist)
            {
                bestSqDist = sqDist;
                bestPoint = candidate;
            }
        }

        // Move slightly from the boundary along the inward direction (external point to projected point) to ensure containment.
        // If this still lies outside, such as at sharp corners, use the boundary point unchanged.
        Vector2 inward = bestPoint - point;
        if (inward.sqrMagnitude > 1e-8f)
        {
            Vector2 nudged = bestPoint + inward.normalized * ParcelClampInnerMargin;
            if (PolygonUtils.Contains(polygon, nudged.x, nudged.y))
            {
                bestPoint = nudged;
            }
        }

        clamped = new Vector3(bestPoint.x, worldPos.y, bestPoint.y);
        return true;
    }

    private void RegisterWithVisualModeHandler()
    {
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.RegisterObject(InputObjectType.PARCEL, this);
        }
    }

    private void OnDestroy()
    {
        TerrainManager.OnTerrainTileReady -= OnTerrainTileReady;
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.UnregisterObject(InputObjectType.PARCEL, this);
        }
    }

    /// <summary>
    /// When high-resolution Terrain is generated later for a cell, queue parcels with centers or vertices in that cell for realignment.
    /// Find candidates through parcelIdsByCell (overlapping BBOX cells); TerrainRefreshWorker handles resampling and rebuilding.
    /// </summary>
    private void OnTerrainTileReady(Vector2Int cellIndex, Terrain terrain)
    {
        Utils.InvalidateHeightCache();
        if (!parcelIdsByCell.TryGetValue(cellIndex, out HashSet<int> ids)) return;

        CalculateCellBounds(cellIndex, out float startX, out float endX, out float startZ, out float endZ);
        foreach (int id in ids)
        {
            if (!parcelSpatialCache.TryGetValue(id, out ParcelSpatialEntry entry)) continue;
            if (!ParcelTouchesCell(entry, startX, endX, startZ, endZ)) continue;
            if (terrainRefreshQueued.Add(id)) terrainRefreshQueue.Enqueue(id);
        }
        StartTerrainRefreshWorker();
    }

    private static bool ParcelTouchesCell(ParcelSpatialEntry entry, float startX, float endX, float startZ, float endZ)
    {
        if (entry.center.x >= startX && entry.center.x < endX &&
            entry.center.y >= startZ && entry.center.y < endZ)
        {
            return true;
        }

        foreach (float[] pt in entry.polygon)
        {
            if (pt != null && pt.Length >= 2 &&
                pt[0] >= startX && pt[0] < endX &&
                pt[1] >= startZ && pt[1] < endZ)
            {
                return true;
            }
        }
        return false;
    }

    private void StartTerrainRefreshWorker()
    {
        if (terrainRefreshCoroutine != null || terrainRefreshQueue.Count == 0 || !isActiveAndEnabled) return;
        terrainRefreshCoroutine = RenderWorkScheduler.Start(this, TerrainRefreshWorker(), "ParcelManager", () => terrainRefreshQueue.Count > 0);
    }

    /// <summary>
    /// Resample queued parcels one at a time and rebuild only those with changed heights.
    /// Yield budget after each rebuild (meshes, collider cooking, outlines); batch parcels requiring only unchanged-height checks.
    /// </summary>
    private IEnumerator TerrainRefreshWorker()
    {
        try
        {
            int unchangedSinceYield = 0;
            while (terrainRefreshQueue.Count > 0)
            {
                int parcelId = terrainRefreshQueue.Dequeue();
                terrainRefreshQueued.Remove(parcelId);
                if (!parcelCache.TryGetValue(parcelId, out ParcelSummary summary)) continue;
                // Do not rebuild parcels at overview heights if tiles left before processing (requeue when tiles arrive on reentry)
                if (!TouchesAnySurfaceTile(parcelId)) continue;

                if (RefreshParcelConformingToTerrain(summary) || ++unchangedSinceYield >= TerrainRefreshSkipBatch)
                {
                    unchangedSinceYield = 0;
                    yield return RenderWorkScheduler.Continue;
                }
            }
        }
        finally
        {
            terrainRefreshCoroutine = null;
        }
    }

    private bool TouchesAnySurfaceTile(int parcelId)
    {
        TerrainManager terrainManager = Utils.CachedTerrainManager;
        if (terrainManager == null) return true;
        if (!parcelSpatialCache.TryGetValue(parcelId, out ParcelSpatialEntry entry)) return true;
        foreach (Vector2Int cell in entry.cells)
        {
            if (terrainManager.HasSurfaceTile(cell)) return true;
        }
        return false;
    }

    /// <summary>
    /// Realign the parcel to current terrain. Return true only if rebuilt.
    /// Use converted polygon/center values from the spatial index (avoids allocating GetPolygonArray each time).
    /// </summary>
    private bool RefreshParcelConformingToTerrain(ParcelSummary parcelData)
    {
        if (parcelSpatialCache.TryGetValue(parcelData.parcel_id, out ParcelSpatialEntry entry))
        {
            return RefreshParcelGeometry(parcelData, entry.polygon, entry.center);
        }
        float[] centerPoint = parcelData.GetCenterPointArray();
        if (centerPoint == null || centerPoint.Length < 2) return false;
        return RefreshParcelGeometry(parcelData, parcelData.GetPolygonArray(), new Vector2(centerPoint[0], centerPoint[1]));
    }

    /// <summary>
    /// Resample terrain and rebuild only parcels whose geometry (XZ) or heights differ from stored outline values. True only if rebuilt.
    /// Meshes and outlines always share samples, so comparing outlines also determines mesh freshness.
    /// </summary>
    private bool RefreshParcelGeometry(ParcelSummary parcelData, float[][] polygon, Vector2 center)
    {
        GameObject parcelGO = GetParcelObject(parcelData.parcel_id);
        if (parcelGO == null || polygon == null || polygon.Length < 3) return false;

        float[] vertexGround = SampleParcelGround(parcelData, polygon, center, out float centerGround);
        if (HasSameGeometry(parcelData.parcel_id, polygon, center, vertexGround, centerGround)) return false;

        ApplyParcelGeometry(parcelGO, parcelData, polygon, center, vertexGround, centerGround);
        return true;
    }

    /// <summary>
    /// Sample ground height (GetParcelBaseY) once per parcel vertex and center.
    /// The returned scratch array is shared; read only through polygon.Length, not array length, and use only synchronously within the caller.
    /// </summary>
    private float[] SampleParcelGround(ParcelSummary parcelData, float[][] polygon, Vector2 center, out float centerGround)
    {
        if (groundSampleScratch.Length < polygon.Length)
        {
            groundSampleScratch = new float[Mathf.NextPowerOfTwo(polygon.Length)];
        }
        for (int i = 0; i < polygon.Length; i++)
        {
            groundSampleScratch[i] = GetParcelBaseY(parcelData, polygon[i][0], polygon[i][1]);
        }
        centerGround = GetParcelBaseY(parcelData, center.x, center.y);
        return groundSampleScratch;
    }

    private bool HasSameGeometry(int parcelId, float[][] polygon, Vector2 center, float[] vertexGround, float centerGround)
    {
        if (!parcelIdToOutlineIndex.TryGetValue(parcelId, out int index)) return false;
        ParcelOutlineData outline = outlines[index];
        if (outline.worldPoints == null || outline.worldPoints.Length != polygon.Length) return false;
        if (Mathf.Abs(outline.center.x - center.x) > PolygonXZEpsilon
            || Mathf.Abs(outline.center.z - center.y) > PolygonXZEpsilon
            || Mathf.Abs(outline.center.y - centerGround) > TerrainHeightEpsilon)
        {
            return false;
        }
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector3 p = outline.worldPoints[i];
            if (Mathf.Abs(p.x - polygon[i][0]) > PolygonXZEpsilon
                || Mathf.Abs(p.z - polygon[i][1]) > PolygonXZEpsilon
                || Mathf.Abs(p.y - OutlineHeightOffset - vertexGround[i]) > TerrainHeightEpsilon)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Set parcel position, top mesh, collider, and outline together from sampled ground heights.
    /// The top uses local coordinates centered at the centroid, with per-vertex local Y following terrain (all zero for water parcels).
    /// </summary>
    private void ApplyParcelGeometry(GameObject parcelGO, ParcelSummary parcelData, float[][] polygon, Vector2 center, float[] vertexGround, float centerGround)
    {
        parcelGO.transform.position = new Vector3(center.x, centerGround + ParcelSurfaceOffset, center.y);

        MeshFilter mf = parcelGO.GetComponent<MeshFilter>();
        if (mf != null)
        {
            Vector2[] points = new Vector2[polygon.Length];
            float[] topLocalY = new float[polygon.Length];
            for (int i = 0; i < polygon.Length; i++)
            {
                points[i] = new Vector2(polygon[i][0], polygon[i][1]);
                topLocalY[i] = vertexGround[i] - centerGround;
            }

            Mesh oldMesh = mf.sharedMesh;
            Mesh newMesh = ParcelMeshFactory.GenerateParcelMesh(points, center, 0f, parcelData.usage_id, topLocalY);
            mf.sharedMesh = newMesh;
            MeshCollider col = parcelGO.GetComponent<MeshCollider>();
            if (col != null)
            {
                col.sharedMesh = newMesh;
            }
            if (oldMesh != null && oldMesh != newMesh)
            {
                Destroy(oldMesh);
            }
        }

        CreateParcelOutline(parcelData.parcel_id, polygon, center, vertexGround, centerGround);
    }

    /// <summary>
    /// Return world Y (meters) of the parcel's supporting surface.
    /// Terrain sampling for water parcels (PARCEL_WATER) returns the seabed (worldY&lt;0),
    /// so use backend altitude referenced to sea level and ensure it remains above the sea plane.
    /// </summary>
    private static float GetParcelBaseY(ParcelSummary parcelData, float x, float z)
    {
        if (parcelData != null && parcelData.parcel_type == Constants.PARCEL_WATER)
        {
            return parcelData.altitude + SeaSurfaceClearance;
        }
        return Utils.GetGroundHeight(new Vector3(x, 1000f, z));
    }

    /// <summary>
    /// Fetch endpoint for each environmental score coloring mode. Null for unsupported modes.
    /// </summary>
    private static string ResolveEnvironmentEndpoint(int mode)
    {
        switch (mode)
        {
            case VisualModeParcel.NOISE: return "/city/noise";
            case VisualModeParcel.AIR_POLLUTION: return "/city/pollution";
            case VisualModeParcel.CRIME: return "/city/crime";
            case VisualModeParcel.SUNSHINE: return "/city/sunshine";
            default: return null;
        }
    }

    /// <summary>
    /// Fetch scores when switching to an environmental score coloring mode.
    /// Values change during epochs, so refetch when the same mode is selected again.
    /// </summary>
    private void RequestEnvironmentScores(int mode)
    {
        string endpoint = ResolveEnvironmentEndpoint(mode);
        requestedEnvironmentMode = endpoint != null ? mode : 0;
        if (endpoint == null) return;

        if (environmentCoroutine != null) RenderWorkScheduler.Stop(this, environmentCoroutine);
        environmentCoroutine = RenderWorkScheduler.Start(this, FetchEnvironmentScoresCoroutine(mode, endpoint), "ParcelManager");
    }

    private IEnumerator FetchEnvironmentScoresCoroutine(int mode, string endpoint)
    {
        yield return ApiClient.Get<EnvironmentHeatmapResponse>(
            endpoint,
            data =>
            {
                // Discard the response if another mode was selected before fetching completed.
                if (requestedEnvironmentMode != mode) return;

                environmentScores.Clear();
                var ids = data?.parcel_ids;
                var values = data?.values;
                if (ids != null && values != null)
                {
                    int count = Mathf.Min(ids.Count, values.Count);
                    for (int i = 0; i < count; i++)
                    {
                        environmentScores[ids[i]] = values[i] / 65535f;
                    }
                }
                loadedEnvironmentMode = mode;
                ForceOutlineRedraw();
            },
            error => Debug.LogError($"ParcelManager: Failed to fetch environment scores ({endpoint}): {error}"));
        environmentCoroutine = null;
    }

    public void ApplyVisualMode(int mode)
    {
        EnsureOverlayRenderer();

        currentParcelVisualMode = mode;

        // Environmental coloring uses values fetched separately from the backend,
        // so initiate fetching when the mode changes.
        RequestEnvironmentScores(mode);

        // Data visualization modes draw outlines and overlay colored polygons.
        // MODE_HIDDEN hides the entire overlay.
        bool shouldShowOverlay = mode != VisualMode.MODE_HIDDEN;
        if (overlayMeshObj != null)
        {
            // dataVisMeshObj is a child of overlayMeshObj, so visibility toggles propagate here.
            overlayMeshObj.SetActive(shouldShowOverlay);
        }

        // Request immediate redraw on mode changes.
        // This applies new data visualization even while the camera is stationary.
        if (shouldShowOverlay)
        {
            ForceOutlineRedraw();
        }
    }

    private void Update()
    {
        if (overlayMeshObj == null || !overlayMeshObj.activeSelf)
            return;

        Camera cam = Camera.main;
        if (cam == null) return;

        // If camera position/rotation changed less than their thresholds,
        // skip rebuilding the 3D-space mesh.
        // Moving less than 1m or rotating less than 2° changes outlines little visually,
        // so updating every frame is rarely necessary.
        Vector3 currentCamPos = cam.transform.position;
        Quaternion currentCamRot = cam.transform.rotation;
        bool posChanged = Vector3.Distance(currentCamPos, lastCamPos) > CAMERA_POS_THRESHOLD;
        bool rotChanged = Quaternion.Angle(currentCamRot, lastCamRot) > CAMERA_ROT_THRESHOLD;
        if (!posChanged && !rotChanged)
        {
            return;
        }

        lastCamPos = currentCamPos;
        lastCamRot = currentCamRot;

        // If there are no outlines, clear the persistent overlay mesh once and return.
        // Reaching this point without an earlier return prevents the previous frame's mesh
        // from remaining after the final parcel is deleted.
        // Deletion calls ForceOutlineRedraw(), resetting lastCamPos,
        // so this branch runs even while the camera is stationary.
        if (outlines.Count == 0)
        {
            overlayRenderer.ClearMesh();
            dataVisRenderer.ClearMesh();
            return;
        }

        UpdateAllParcelOutlines(cam);
    }

    /// <summary>
    /// Update outlines and highlights for every parcel.
    /// Pass world coordinates directly to OverlayMeshRenderer for batched 3D-space mesh rendering.
    /// If highlightedParcelId is not -1, also add that parcel's filled polygon.
    /// Data visualization modes (land price, buildability, etc.) draw colored polygons for every parcel.
    /// </summary>
    private void UpdateAllParcelOutlines(Camera cam)
    {
        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;
        float nearClipWithMargin = cam.nearClipPlane + 0.001f;
        Plane nearPlane = new Plane(camFwd, camPos + camFwd * nearClipWithMargin);

        overlayRenderer.BeginFrame();
        dataVisRenderer.BeginFrame();

        // Filled polygon for the highlighted parcel
        if (highlightedParcelId >= 0 && parcelIdToOutlineIndex.TryGetValue(highlightedParcelId, out int hlIdx))
        {
            DrawHighlightPolygon(cam, outlines[hlIdx], HighlightColor);
        }

        // Fill path-interference parcels using category colors (independent of hover)
        if (interferenceParcelColors.Count > 0)
        {
            foreach (var kv in interferenceParcelColors)
            {
                if (parcelIdToOutlineIndex.TryGetValue(kv.Key, out int ifIdx))
                {
                    DrawHighlightPolygon(cam, outlines[ifIdx], kv.Value);
                }
            }
        }

        // Check whether this is a data visualization mode
        bool isDataVisMode = currentParcelVisualMode >= 200;

        for (int oi = 0; oi < outlines.Count; oi++)
        {
            ParcelOutlineData outline = outlines[oi];
            Vector3[] pts = outline.worldPoints;

            // Calculate squared camera distance using the cached center point.
            // This avoids expensive Terrain elevation queries (Utils.GetGroundHeight) in the per-frame loop.
            Vector3 center = outline.center;
            float distSq = (camPos.x - center.x) * (camPos.x - center.x) + 
                           (camPos.y - center.y) * (camPos.y - center.y) + 
                           (camPos.z - center.z) * (camPos.z - center.z);

            // Skip immediately without a square root beyond the fully hidden distance threshold.
            // This culling reduces CPU load by avoiding unnecessary Mathf.Sqrt calls for most distant parcels.
            if (distSq >= DISTANCE_INVISIBLE_SQ) continue;
            if (!parcelCache.ContainsKey(outline.parcelId)) continue;

            // Calculate alpha from squared distance.
            float alpha = CalculateAlphaByDistanceSq(distSq);
            if (alpha <= 0.01f) continue;

            // Draw colored polygons in data visualization modes
            if (isDataVisMode)
            {
                DrawDataVisPolygon(outline, alpha);
            }

            // Draw boundaries in data visualization modes too, serving as polygon outlines
            int ptCount = pts.Length;
            for (int i = 0; i < ptCount; i++)
            {
                int nextI = (i + 1) % ptCount;
                Vector3 ws = pts[i];
                Vector3 we = pts[nextI];

                Vector3 ca = ws;
                Vector3 cb = we;
                if (!ClipLineToNearPlaneWithPlane(ref ca, ref cb, nearPlane))
                    continue;

                Color c = parcelLineColor;
                c.a = alpha;
                overlayRenderer.AddSolidLine(ca, cb, c, LINE_THICKNESS);
            }
        }

        // Overlay the selected parcel's road access (front_path) arrow.
        DrawFrontPathArrow(nearPlane);

        overlayRenderer.EndFrame(cam);
        dataVisRenderer.EndFrame(cam);
    }

    /// <summary>DrawHighlightPolygon scratch list. Reusable because OverlayMeshRenderer.AddPolygon copies its contents.</summary>
    private readonly List<Vector3> highlightPolygonScratch = new List<Vector3>(16);

    /// <summary>
    /// Add the highlighted parcel polygon as a filled drawing using world coordinates directly.
    /// </summary>
    private void DrawHighlightPolygon(Camera cam, ParcelOutlineData outline, Color color)
    {
        Vector3[] pts = outline.worldPoints;
        List<Vector3> worldPoints = highlightPolygonScratch;
        worldPoints.Clear();

        // Outline vertices already lie on the parcel reference plane (sea level for water parcels),
        // so simply replace the offset (resampling terrain offshore would return the seabed).
        for (int i = 0; i < pts.Length; i++)
        {
            Vector3 worldPt = pts[i];
            worldPt.y += HighlightHeightOffset - OutlineHeightOffset;
            worldPoints.Add(worldPt);
        }

        if (worldPoints.Count >= 3)
        {
            overlayRenderer.AddPolygon(worldPoints, color);
        }
    }

    /// <summary>
    /// Draw parcels as colored polygons according to data visualization mode.
    /// Use the same palette as UI legend definitions (DisplayOptionsPanel getLegendInfo).
    /// FILTER modes do not draw unbuildable parcels (transparent).
    /// Render to dataVisRenderer (Custom/OverlayFillTerrainConform). Shader tessellation handles
    /// terrain following inside triangles, so the CPU only submits
    /// simple fan triangulation of parcel polygons (vertex Y uses the same corner terrain heights
    /// as outlines as a fallback before heightmap upload).
    /// </summary>
    private void DrawDataVisPolygon(ParcelOutlineData outline, float alpha)
    {
        int parcelId = outline.parcelId;
        VisualParcelObject vpo = null;
        parcelVisualObjects.TryGetValue(parcelId, out vpo);
        if (vpo == null) return;

        VisualParcelStats stats = vpo.Stats;
        if (stats == null) return;

        Vector3[] pts = outline.worldPoints;
        if (pts.Length < 3) return;

        Color? colorNullable = ResolveDataVisColor(currentParcelVisualMode, stats, outline, parcelId);
        if (!colorNullable.HasValue) return;

        Color color = colorNullable.Value;
        color.a = DataVisAlpha * alpha;

        if (outline.fillTriangles == null)
        {
            List<Vector3> triVerts = new List<Vector3>((pts.Length - 2) * 3);
            for (int i = 1; i < pts.Length - 1; i++)
            {
                triVerts.Add(pts[0]);
                triVerts.Add(pts[i]);
                triVerts.Add(pts[i + 1]);
            }
            outline.fillTriangles = triVerts;
        }

        dataVisRenderer.AddPreTriangulated(outline.fillTriangles, color);
    }

    /// <summary>
    /// Determine render color from the current data visualization mode and parcel statistics.
    /// Return null to skip drawing if unbuildable or data is unavailable.
    ///
    /// Each FILTER mode checks building regulations (allowed_bd_mask)
    /// as well as parcel type (parcel_type) location requirements.
    ///
    /// ZONE_COLOR spatially resolves the district from the parcel center (outline.center)
    /// using ZoneManager.GetZoneIdAtPoint and returns its identifying color.
    /// ParcelSummary lacks zone_id, so districts are resolved spatially.
    /// Cache results with ZoneManager.ZoneRevision in outline data until district data changes.
    /// Return null and skip parcels belonging to no district.
    /// </summary>
    private Color? ResolveDataVisColor(int mode, VisualParcelStats stats, ParcelOutlineData outline, int parcelId)
    {
        switch (mode)
        {
            case VisualModeParcel.LAND_PRICE:
            {
                float t = Mathf.Clamp01(stats.land_price / 10000f);
                // UI legend: white (#ffffff) to green (#4caf50)
                return Color.Lerp(new Color(1f, 1f, 1f), HexToColor(0x4c, 0xaf, 0x50), t);
            }
            case VisualModeParcel.ZONE_COLOR:
            {
                ZoneManager zoneManager = ZoneManager.Instance;
                if (zoneManager == null) return null;
                int zoneRevision = zoneManager.ZoneRevision;
                if (outline.zoneRevision != zoneRevision)
                {
                    outline.zoneId = zoneManager.GetZoneIdAtPoint(outline.center);
                    outline.zoneRevision = zoneRevision;
                }
                if (outline.zoneId <= 0) return null;
                return zoneManager.GetZoneColor(outline.zoneId);
            }
            case VisualModeParcel.FILTER_HOUSE:
                return IsBuildable(stats, Constants.BUILDING_HOUSE) ? HexToColor(0xa5, 0xd6, 0xa7) : (Color?)null;
            case VisualModeParcel.FILTER_APARTMENT:
                return IsBuildable(stats, Constants.BUILDING_APARTMENT) ? HexToColor(0x2e, 0x7d, 0x32) : (Color?)null;
            case VisualModeParcel.FILTER_COMMERCIAL:
                return IsBuildable(stats, Constants.BUILDING_RETAIL) ? HexToColor(0x21, 0x96, 0xf3) : (Color?)null;
            case VisualModeParcel.FILTER_OFFICE:
                return IsBuildable(stats, Constants.BUILDING_OFFICE) ? HexToColor(0x9c, 0x27, 0xb0) : (Color?)null;
            case VisualModeParcel.FILTER_FACTORY:
                return IsBuildable(stats, Constants.BUILDING_FACTORY) ? HexToColor(0xfb, 0xc0, 0x2d) : (Color?)null;
            case VisualModeParcel.FILTER_FARM:
                return IsBuildable(stats, Constants.BUILDING_FARM) ? HexToColor(0x79, 0x55, 0x48) : (Color?)null;
            case VisualModeParcel.FILTER_MINE:
                return IsBuildable(stats, Constants.BUILDING_MINE) ? HexToColor(0xff, 0x98, 0x00) : (Color?)null;
            case VisualModeParcel.FILTER_PORT:
                return IsBuildable(stats, Constants.BUILDING_PORT) ? HexToColor(0x00, 0xbc, 0xd4) : (Color?)null;
            case VisualModeParcel.FILTER_OTHER:
                return IsBuildable(stats, Constants.BUILDING_OTHER) ? HexToColor(0x9e, 0x9e, 0x9e) : (Color?)null;
            case VisualModeParcel.NOISE:
                // UI legend: pale yellow (#fde68a) to dark orange (#e65100)
                return ResolveEnvironmentColor(mode, parcelId, HexToColor(0xfd, 0xe6, 0x8a), HexToColor(0xe6, 0x51, 0x00));
            case VisualModeParcel.AIR_POLLUTION:
                // UI legend: pale gray-green (#d1e7dd) to dark purple (#4a148c)
                return ResolveEnvironmentColor(mode, parcelId, HexToColor(0xd1, 0xe7, 0xdd), HexToColor(0x4a, 0x14, 0x8c));
            case VisualModeParcel.CRIME:
                // UI legend: pale red (#ffcdd2) to dark red (#b71c1c)
                return ResolveEnvironmentColor(mode, parcelId, HexToColor(0xff, 0xcd, 0xd2), HexToColor(0xb7, 0x1c, 0x1c));
            case VisualModeParcel.SUNSHINE:
                // UI legend: pale yellow (#fff59d sunlight) to dark blue-gray (#37474f shade)
                return ResolveEnvironmentColor(mode, parcelId, HexToColor(0xff, 0xf5, 0x9d), HexToColor(0x37, 0x47, 0x4f));
            default:
                return null;
        }
    }

    /// <summary>
    /// Resolve environmental score colors. Do not fill parcels with unavailable scores (null).
    /// Noise, air pollution, and public safety scores are relative values scaled from that epoch's minimum/maximum to 0–1
    /// (color intensity represents relative severity within the city). Sunlight alone uses
    /// absolute values fixed at 0–1 (based on time in shadow), so intensity directly represents the absolute level.
    /// </summary>
    private Color? ResolveEnvironmentColor(int mode, int parcelId, Color low, Color high)
    {
        if (loadedEnvironmentMode != mode) return null;
        if (!environmentScores.TryGetValue(parcelId, out float score)) return null;
        return Color.Lerp(low, high, Mathf.Clamp01(score));
    }

    /// <summary>
    /// Check whether the specified building type can be built on the parcel.
    /// True only if both building regulations (allowed_bd_mask) and site requirements (parcel_type and berth water depth) are satisfied.
    /// Prioritize the site requirements of the currently selected construction variant
    /// because identical bd_type values may distinguish land, coastal, and offshore locations (such as offshore wind farms).
    /// </summary>
    private bool IsBuildable(VisualParcelStats stats, int buildingTypeBit)
    {
        if ((stats.allowed_bd_mask & buildingTypeBit) == 0) return false;
        return BuildingPlanManager.ResolveSiteIncompatibility(stats.parcel_type, stats.water_depth, buildingTypeBit) == null;
    }

    /// <summary>
    /// Create a Unity Color from RGB values in 0–255.
    /// Helper providing an intuitive correspondence with UI hexadecimal color codes.
    /// </summary>
    private static Color HexToColor(int r, int g, int b)
    {
        return new Color(r / 255f, g / 255f, b / 255f);
    }

    /// <summary>
    /// Register parcel outline data, storing polygon vertices in world coordinates.
    /// UpdateAllParcelOutlines performs actual screen coordinate conversion each frame.
    /// Receive the same sampled heights (SampleParcelGround) as the top mesh to avoid sampling identical positions twice.
    /// Replace existing outlines in place, preserving hover highlights.
    /// </summary>
    private void CreateParcelOutline(int parcelId, float[][] polygon, Vector2 center, float[] vertexGround, float centerGround)
    {
        EnsureOverlayRenderer();

        Vector3[] worldPoints = new Vector3[polygon.Length];
        for (int i = 0; i < polygon.Length; i++)
        {
            worldPoints[i] = new Vector3(polygon[i][0], vertexGround[i] + OutlineHeightOffset, polygon[i][1]);
        }

        // Store ground height in the center point to avoid terrain sampling during per-frame distance checks.
        ParcelOutlineData data = new ParcelOutlineData
        {
            parcelId = parcelId,
            center = new Vector3(center.x, centerGround, center.y),
            worldPoints = worldPoints
        };

        if (parcelIdToOutlineIndex.TryGetValue(parcelId, out int index))
        {
            outlines[index] = data;
        }
        else
        {
            parcelIdToOutlineIndex[parcelId] = outlines.Count;
            outlines.Add(data);
        }

        // Draw new parcel outlines on the next frame even while the camera is stationary.
        ForceOutlineRedraw();
    }

    private void RemoveParcelOutline(int parcelId)
    {
        if (!parcelIdToOutlineIndex.TryGetValue(parcelId, out int index))
            return;

        int lastIndex = outlines.Count - 1;
        if (index != lastIndex)
        {
            ParcelOutlineData moved = outlines[lastIndex];
            outlines[index] = moved;
            parcelIdToOutlineIndex[moved.parcelId] = index;
        }

        outlines.RemoveAt(lastIndex);
        parcelIdToOutlineIndex.Remove(parcelId);

        if (highlightedParcelId == parcelId)
        {
            highlightedParcelId = -1;
        }

        // Remove deleted outlines on the next frame even while the camera is stationary.
        ForceOutlineRedraw();
    }

    /// <summary>
    /// Highlight the specified parcel. Called by ParcelInputHandler.onHoverStart.
    /// Reset cached camera position to force redrawing, applying highlight changes on the next frame immediately.
    /// </summary>
    public void SetHighlightedParcel(int parcelId)
    {
        highlightedParcelId = parcelId;
        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// Clear the highlight. Called by ParcelInputHandler.onHoverEnd.
    /// Reset cached camera position to force redrawing, applying highlight changes on the next frame immediately.
    /// </summary>
    public void ClearHighlightedParcel()
    {
        highlightedParcelId = -1;
        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// Set path interference parcel highlights, replacing the entire parcel ID-to-fill-color map.
    /// Fill parcels acquired/intersected by planned paths using category colors (blocking=red/removable=orange/shavable=yellow).
    /// Independent of and coexisting with the single hover highlight. Called by HighlightManager.
    /// </summary>
    public void SetInterferenceParcels(Dictionary<int, Color> parcelColors)
    {
        interferenceParcelColors = parcelColors ?? new Dictionary<int, Color>();
        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// Clear every path interference parcel highlight. Called when placement ends or the details panel closes.
    /// </summary>
    public void ClearInterferenceParcels()
    {
        if (interferenceParcelColors.Count == 0) return;
        interferenceParcelColors.Clear();
        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// Request forced outline redraw.
    /// Reset cached camera position to guarantee outline updates next frame even while stationary.
    /// Called when data changes without camera movement, such as after committing parcel geometry edits.
    /// </summary>
    public void ForceOutlineRedraw()
    {
        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// Calculate fade alpha from squared camera-to-parcel distance.
    /// Calculate square roots only in the intermediate region requiring fade,
    /// reducing Mathf.Sqrt costs in fully visible near regions and fully hidden distant regions.
    /// </summary>
    private float CalculateAlphaByDistanceSq(float distanceSq)
    {
        if (distanceSq <= DISTANCE_START_FADE_SQ)
        {
            return 1f;
        }
        if (distanceSq >= DISTANCE_INVISIBLE_SQ)
        {
            return 0f;
        }

        // Calculate square roots only within the boundary distance range requiring fade
        float distance = Mathf.Sqrt(distanceSq);
        float fadeRange = DISTANCE_INVISIBLE - DISTANCE_START_FADE;
        float fadeProgress = (distance - DISTANCE_START_FADE) / fadeRange;
        return 1f - fadeProgress;
    }

    /// <summary>
    /// Clip a segment using the prebuilt nearPlane.
    /// Reuse the Plane created collectively in UpdateAllParcelOutlines to avoid calling new Plane() every frame.
    /// </summary>
    private bool ClipLineToNearPlaneWithPlane(ref Vector3 a, ref Vector3 b, Plane nearPlane)
    {
        float da = nearPlane.GetDistanceToPoint(a);
        float db = nearPlane.GetDistanceToPoint(b);

        if (da < 0f && db < 0f) return false;

        if (da < 0f || db < 0f)
        {
            Vector3 dir = b - a;
            float t = da / (da - db);
            Vector3 p = a + dir * t;
            if (da < 0f) a = p; else b = p;
        }

        return true;
    }

    // ================================================================
    // Road access (front_path) arrow display
    // ================================================================

    /// <summary>
    /// Display the parcel's road access (front_path) as a blue 3D arrow.
    /// Fetch details with GET /parcels/{id} and draw an arrow crossing the fronting edge at facade_edge_index.
    /// Called through IpcEventHandler when the parcel details panel opens.
    /// </summary>
    public void ShowFrontPathIndicator(int parcelId)
    {
        frontPathParcelId = parcelId;
        if (frontPathCoroutine != null)
        {
            RenderWorkScheduler.Stop(this, frontPathCoroutine);
            frontPathCoroutine = null;
        }
        frontPathCoroutine = RenderWorkScheduler.Start(this, FetchFrontPathCoroutine(parcelId), "ParcelManager");
    }

    /// <summary>
    /// Clear the road access arrow. Called when the parcel details panel closes.
    /// </summary>
    public void HideFrontPathIndicator()
    {
        frontPathParcelId = -1;
        if (frontPathCoroutine != null)
        {
            RenderWorkScheduler.Stop(this, frontPathCoroutine);
            frontPathCoroutine = null;
        }
        if (frontPathArrowActive)
        {
            frontPathArrowActive = false;
            ForceOutlineRedraw();
        }
    }

    private IEnumerator FetchFrontPathCoroutine(int parcelId)
    {
        yield return ApiClient.Get<ParcelData>(
            $"/parcels/{parcelId}",
            data =>
            {
                // Discard the response if another parcel was selected before fetching completed.
                if (frontPathParcelId != parcelId) return;
                BuildFrontPathArrow(data);
                ForceOutlineRedraw();
            },
            error => Debug.LogError($"ParcelManager: Failed to fetch parcel detail for front path: {error}"));
        frontPathCoroutine = null;
    }

    /// <summary>
    /// Build road access arrow world coordinates from ParcelData polygon / center_point / facade_edge_index.
    /// Hide if front_path is absent or facade_edge_index is invalid.
    /// </summary>
    private void BuildFrontPathArrow(ParcelData data)
    {
        frontPathArrowActive = false;

        if (data == null || data.front_path_id <= 0) return;

        float[][] polygon = GeneratedModelUtils.ToFloatJaggedArray(data.polygon);
        float[] center = GeneratedModelUtils.ToFloatArray(data.center_point);
        if (polygon == null || polygon.Length < 2 || center == null || center.Length < 2) return;

        int n = polygon.Length;
        int edgeIndex = data.facade_edge_index;
        if (edgeIndex < 0 || edgeIndex >= n) return;

        float[] a = polygon[edgeIndex];
        float[] b = polygon[(edgeIndex + 1) % n];
        if (a == null || a.Length < 2 || b == null || b.Length < 2) return;

        Vector2 p0 = new Vector2(a[0], a[1]);
        Vector2 p1 = new Vector2(b[0], b[1]);
        Vector2 mid = (p0 + p1) * 0.5f;
        Vector2 centroid = new Vector2(center[0], center[1]);

        // Outward (road-side) direction: vector from parcel centroid to fronting edge midpoint, robust regardless of polygon CCW/CW winding.
        Vector2 outward = mid - centroid;
        if (outward.sqrMagnitude < 1e-6f)
        {
            // Use the fronting edge normal for degenerate cases (midpoint approximately equals centroid).
            Vector2 edgeDir = (p1 - p0).normalized;
            outward = new Vector2(edgeDir.y, -edgeDir.x);
        }
        outward = outward.normalized;
        Vector2 perp = new Vector2(-outward.y, outward.x);

        Vector2 shaftStart2 = mid - outward * FrontPathArrowHalfLength; // Inside the parcel
        Vector2 shaftEnd2 = mid + outward * FrontPathArrowHalfLength;   // Road side (arrowhead tip)
        Vector2 headBase2 = shaftEnd2 - outward * FrontPathArrowHeadLength;
        Vector2 headL2 = headBase2 + perp * FrontPathArrowHeadHalfWidth;
        Vector2 headR2 = headBase2 - perp * FrontPathArrowHeadHalfWidth;

        frontPathShaftStart = LiftToGround(shaftStart2);
        frontPathShaftEnd = LiftToGround(shaftEnd2);

        Vector3 tip = frontPathShaftEnd;
        Vector3 left = LiftToGround(headL2);
        Vector3 right = LiftToGround(headR2);

        // Build a double-sided arrowhead (2 triangles) so it displays regardless of culling direction.
        frontPathArrowHead.Clear();
        frontPathArrowHead.Add(tip);
        frontPathArrowHead.Add(left);
        frontPathArrowHead.Add(right);
        frontPathArrowHead.Add(tip);
        frontPathArrowHead.Add(right);
        frontPathArrowHead.Add(left);

        frontPathArrowActive = true;
    }

    /// <summary>Lift XZ coordinates to world coordinates at ground height plus offset.</summary>
    private Vector3 LiftToGround(Vector2 xz)
    {
        float y = Utils.GetGroundHeight(new Vector3(xz.x, 1000f, xz.y)) + FrontPathArrowHeightOffset;
        return new Vector3(xz.x, y, xz.y);
    }

    /// <summary>Draw the road access arrow in the overlay. Called every frame by UpdateAllParcelOutlines.</summary>
    private void DrawFrontPathArrow(Plane nearPlane)
    {
        if (!frontPathArrowActive) return;

        Vector3 a = frontPathShaftStart;
        Vector3 b = frontPathShaftEnd;
        if (ClipLineToNearPlaneWithPlane(ref a, ref b, nearPlane))
        {
            overlayRenderer.AddSolidLine(a, b, FrontPathArrowColor, FrontPathArrowThickness);
        }

        if (frontPathArrowHead.Count >= 3)
        {
            overlayRenderer.AddPreTriangulated(frontPathArrowHead, FrontPathArrowColor);
        }
    }

}
