using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;

/// <summary>
/// Manager fetching backend terrain elevations and placing resident-cell Terrain tiles and citywide UltraFar land meshes.
/// - Resident cells: /api/v1/terrain/surface (float32 per cell, 513 grid). Apply incrementally to temporary TerrainData before swapping.
/// - Overview: build UltraFar land meshes from /api/v1/terrain/far coarse map elevations.
/// - Fetch map dimensions/cell size from /api/v1/terrain/settings and ground cover type from /api/v1/city.
/// </summary>
public partial class TerrainManager : BaseApiDataManager
{
    // Event notifying tile generation completion
    public static System.Action<Vector2Int, Terrain> OnTerrainTileReady;

    /// <summary>
    /// Fired when LoadLodCellsCoroutine finishes loading all cells.
    /// Argument lists cells whose terrain tiles were actually generated/regenerated or restitched in this batch.
    /// </summary>
    public System.Action<IReadOnlyList<Vector2Int>> OnAllTerrainLodLoaded;

    /// <summary>
    /// Fired once the batch has revision lists, overview/background terrain, and finalized resident cells.
    /// Grounding can then use overview heights where tiles are absent, so CellDataLoader starts loading buildings, roads, etc.
    /// </summary>
    public System.Action OnTerrainOverviewReady;

    /// <summary>
    /// Cells whose heightmaps were actually applied (streaming finished) in this load batch.
    /// Append whenever HeightStreamWorker completes one cell, snapshot for recipients when OnAllTerrainLodLoaded fires,
    /// then clear.
    /// If the coroutine is canceled, retain and accumulate until the next completion notification
    /// (ensures cancellation cannot lose terrain-following updates).
    /// </summary>
    private readonly HashSet<Vector2Int> updatedTerrainCellsThisLoad = new HashSet<Vector2Int>();

    /// <summary>
    /// Cell side length (meters), fetched from backend terrain settings.
    /// Use provisional 500m before the API responds.
    /// </summary>
    public static float CellSize { get; private set; } = 500f;

    /// <summary>
    /// Unity cell index containing world XZ. May be negative because map center is the origin.
    /// Convert to backend zero-based cells through UnityCellToBackendCell.
    /// </summary>
    public static Vector2Int WorldToCell(float x, float z)
    {
        return new Vector2Int(Mathf.FloorToInt(x / CellSize), Mathf.FloorToInt(z / CellSize));
    }

    public static Vector2Int WorldToCell(Vector3 position) => WorldToCell(position.x, position.z);

    /// <summary>Inclusive Unity cell range overlapping a world XZ rectangle, used to enumerate crossed cells.</summary>
    public static void WorldRectToCellRange(float minX, float minZ, float maxX, float maxZ, out Vector2Int minCell, out Vector2Int maxCell)
    {
        minCell = WorldToCell(minX, minZ);
        maxCell = WorldToCell(maxX, maxZ);
    }

    // === Settings ===
    [Header("Terrain Settings")]
    public Vector3 terrainSize = new Vector3(CellSize, 1200f, CellSize);

    /// <summary>
    /// Vertical span below sea level (m), reserved to represent
    /// seabed depths as terrain.
    ///
    /// Unity Terrain heightmaps represent only [0,1]×terrainSize.y = local Y 0–1200m;
    /// without adjustment, negative elevations below sea level collapse to worldY=0
    /// (zero sea depth makes depth-based water shaders render the sea transparent).
    ///
    /// Lower terrain tile base Y by this amount (<c>-SeaFloorDepthSpan</c>)
    /// and offset normalization to <c>(elevation + SeaFloorDepthSpan) / terrainSize.y</c>.
    /// This preserves worldY = base Y + normalized×terrainSize.y = actual elevation,
    /// rendering seabeds at worldY&lt;0 (down to -SeaFloorDepthSpan). Land, road, and building
    /// height following uses unchanged actual elevations.
    ///
    /// terrainSize.y (=1200m) comprises SeaFloorDepthSpan m below sea plus (terrainSize.y - SeaFloorDepthSpan) m of land height.
    /// Increasing this depth lowers the land height ceiling. Deeper seabeds clamp to the minimum
    /// (flattened, but sufficiently deep to appear as deep water, so acceptable in practice).
    /// </summary>
    private const float SeaFloorDepthSpan = 200f;

    /// <summary>
    /// Version cache per dynamic-tier cell.
    /// Compare backend X-Cell-Version during incremental updates and redraw only if changed.
    /// </summary>
    private Dictionary<Vector2Int, int> dynamicCellVersions = new Dictionary<Vector2Int, int>();

    /// <summary>
    /// Latest /terrain/cell-versions response in Unity cell coordinates. Terrain revisions change only during epochs/edits,
    /// always followed by ReloadAllData → InvalidateCellVersions, so reuse for camera-triggered batches.
    /// If invalidated during fetching, do not clear dirty using that response (check cellVersionsGeneration).
    /// </summary>
    private Dictionary<Vector2Int, int> cachedCellVersions;
    private float cellVersionsFetchedAt;
    private bool cellVersionsDirty = true;
    private int cellVersionsGeneration;
    // Refetch beyond this many seconds even if backend changes were missed.
    private const float CELL_VERSIONS_MAX_AGE = 5f;

    /// <summary>
    /// Whether debug mode is enabled. Read by MinimapOverlay and similar callers.
    /// </summary>
    public bool IsDebugMode { get; set; } = false;

    [Header("Material Settings")]
    [SerializeField] private TerrainLayer[] customTerrainLayers;
    [SerializeField] private Material customTerrainMaterial;
    [SerializeField] private bool useCustomMaterial = false;

    /// <summary>
    /// Custom/DepthOffsetTerrainLit shader preventing Z-fighting.
    /// Shader.Find may fail to locate shaders after building;
    /// reference directly through SerializeField to guarantee build inclusion.
    /// </summary>
    [SerializeField] private Shader depthOffsetTerrainShader;

    [Header("UltraFar Land Mesh")]
    /// <summary>
    /// Custom/DepthOffsetSimpleLitFar shader for UltraFar land meshes.
    /// Render farthest back using positive Offset (4,4), larger than per-cell tiles (1,1) and sea (-1,-1).
    /// Reference directly through SerializeField for build inclusion (fall back to Shader.Find if unset).
    /// </summary>
    [SerializeField] private Shader ultraFarLandShader;
    /// <summary>UltraFar land mesh grid spacing (m). Smaller values improve coastlines/relief but increase vertices.</summary>
    [SerializeField] private float ultraFarGridStep = 80f;
    /// <summary>Vertex Y offset (m) keeping UltraFar land above the sea plane.</summary>
    [SerializeField] private float ultraFarHeightOffset = 1.0f;
    /// <summary>
    /// Amount (m) to sink UltraFar land meshes beneath cells with loaded per-cell tiles.
    /// Prevents protrusions caused by coarse distant elevations differing from normal tiles.
    /// Sink only vertices whose connected triangles fit inside loaded areas; retain overlap at boundaries.
    /// </summary>
    [SerializeField] private float ultraFarSinkAmount = 100f;

    [Header("Default Material Settings")]
    [SerializeField] private float defaultMetallic = 0f;

    // GroundCoverPalette profiles hold appearance varying by ground cover type (Constants.GROUND_COVER_*).
    // These include base textures, procedural colors/relief/smoothness, post-lighting large-scale shading/color correction, rock tint, and shore appearance;
    // ApplyTerrainMaterialSettings passes the city's profile identically to terrain tiles, UltraFar, and outside-city backgrounds.
    // Ground uses no normal map; procedural detail normals and wind ripples provide relief
    // (shader: Assets/Shaders/TerrainSurfaceAppearance.hlsl).
    [Header("Ground Cover")]
    /// <summary>Appearance per ground cover type. Fall back to Resources GroundCoverPalette, then code defaults if unset.</summary>
    [SerializeField] private GroundCoverPalette groundCoverPalette;
    /// <summary>If nonzero, render this ground cover type (Constants.GROUND_COVER_*) instead of the city's, for editor comparisons.</summary>
    [SerializeField] private int groundCoverPreview = 0;

    // The following values are shared and independent of ground cover type.
    [Header("Terrain Macro Variation Settings")]
    [SerializeField] private float terrainMacroVariationScale = 350f;

    [Header("Terrain Color Grade Settings")]
    /// <summary>Elevation (m) for lowland color, intended near sea level (0m).</summary>
    [SerializeField] private float terrainColorGradeLowHeight = 0f;
    /// <summary>Elevation (m) for highland color. Higher elevations uniformly approach this color.</summary>
    [SerializeField] private float terrainColorGradeHighHeight = 300f;

    [Header("Terrain Detail Normal Settings")]
    /// <summary>Detail normal noise tiling period (m). Smaller values give finer relief.</summary>
    [SerializeField] private float terrainDetailNormalTilingMeters = 3.5f;

    // Cliff (steep-slope) rock texture blending. Shader implementation:
    // Assets/Shaders/TerrainCliffRock.hlsl (triplanar mapping + normal map).
    // The former Terrain Slope Color Settings (post-lighting solid fills using terrainSlopeThreshold, etc.)
    // removed shading and were retired. Field names changed deliberately
    // to avoid inheriting old scene-serialized values such as extremely low thresholds.
    /// <summary>Cliff rock albedo. Load Resources/Textures/cliff-rock-albedo if unset.</summary>
    [Header("Terrain Cliff Rock Settings")]
    [SerializeField] private Texture2D cliffAlbedoTexture;
    /// <summary>Cliff rock normal map. Load Resources/Textures/cliff-rock-normal if unset.</summary>
    [SerializeField] private Texture2D cliffNormalTexture;
    /// <summary>Rock texture tiling period (m), setting real-world scale of triplanar projection.</summary>
    [SerializeField] private float cliffTilingMeters = 12f;
    /// <summary>
    /// World noise period (m) perturbing thresholds to prevent boundary banding.
    /// Large periods near cliff width (tens of meters) place opposing V-valley sides in different noise regions,
    /// making one side entirely rock and the other grass. Use smaller periods for fine patches within cliffs.
    /// </summary>
    [SerializeField] private float cliffNoiseScale = 18f;
    /// <summary>Threshold noise amplitude. 0 disables noise, producing uniform bands.</summary>
    [SerializeField, Range(0f, 0.3f)] private float cliffNoiseStrength = 0.09f;
    /// <summary>Rock normal map strength. 0 disables normals.</summary>
    [SerializeField, Range(0f, 2f)] private float cliffNormalStrength = 1.0f;
    /// <summary>Rock smoothness, kept low for dry rock surfaces.</summary>
    [SerializeField, Range(0f, 1f)] private float cliffSmoothness = 0.08f;
    /// <summary>Fallback solid color when rock textures are unavailable.</summary>
    [SerializeField] private Color cliffFallbackColor = new Color(0.42f, 0.36f, 0.30f, 1f);
    /// <summary>Elevation (m) where lowland rock blending begins. Lower land near the coastline gradually becomes rocky regardless of slope (coasts/shallow-water rock).</summary>
    [SerializeField] private float cliffLowlandStartHeight = 2f;
    /// <summary>Elevation (m) of maximum lowland rock blending. The seabed below it is fully rocky. Default sea level (0m).</summary>
    [SerializeField] private float cliffLowlandFullHeight = 0f;
    /// <summary>
    /// Inland distance (m) from the coastline where lowland rock fades out. Lowland rock is strongest at the coastline,
    /// so low plains far from the sea such as river deltas keep their ground cover. Applied when the coastline is next fetched.
    /// </summary>
    [SerializeField] private float cliffLowlandCoastDistance = 50f;
    /// <summary>Coastline distance mask over the city map for lowland rock. Null without a coastline.</summary>
    private Texture2D cliffCoastMask;

    /// <summary>Flag resolving Resources rock texture fallbacks only once.</summary>
    private bool cliffTexturesResolved = false;

    /// <summary>City ground cover type (GET /api/v1/city ground_cover), fetched by FetchCoastline at session start.</summary>
    private int cityGroundCover = Constants.GROUND_COVER_GRASSLAND;
    /// <summary>Flag resolving the Resources palette only once.</summary>
    private bool groundCoverPaletteResolved = false;
    /// <summary>Flag applying Inspector palette edits to terrain once in the next frame.</summary>
    private bool groundCoverMaterialsDirty = false;
    private static readonly GroundCoverProfile[] DefaultGroundCoverProfiles = GroundCoverProfile.CreateDefaults();
    private readonly Dictionary<string, Texture2D> groundTextureCache = new Dictionary<string, Texture2D>();

    private static readonly int TerrainMacroVariationScaleId = Shader.PropertyToID("_TerrainMacroVariationScale");

    private static readonly int TerrainColorGradeLowHeightId = Shader.PropertyToID("_TerrainColorGradeLowHeight");
    private static readonly int TerrainColorGradeHighHeightId = Shader.PropertyToID("_TerrainColorGradeHighHeight");
    private static readonly int TerrainDetailNormalScaleId = Shader.PropertyToID("_TerrainDetailNormalScale");

    private static readonly int CliffAlbedoMapId = Shader.PropertyToID("_CliffAlbedoMap");
    private static readonly int CliffNormalMapId = Shader.PropertyToID("_CliffNormalMap");
    private static readonly int CliffUvScaleId = Shader.PropertyToID("_CliffUvScale");
    private static readonly int CliffNoiseScaleId = Shader.PropertyToID("_CliffNoiseScale");
    private static readonly int CliffNoiseStrengthId = Shader.PropertyToID("_CliffNoiseStrength");
    private static readonly int CliffNormalStrengthId = Shader.PropertyToID("_CliffNormalStrength");
    private static readonly int CliffSmoothnessId = Shader.PropertyToID("_CliffSmoothness");
    private static readonly int CliffTexturesLoadedId = Shader.PropertyToID("_CliffTexturesLoaded");
    private static readonly int CliffFallbackColorId = Shader.PropertyToID("_CliffFallbackColor");
    private static readonly int CliffLowlandStartId = Shader.PropertyToID("_CliffLowlandStart");
    private static readonly int CliffLowlandFullId = Shader.PropertyToID("_CliffLowlandFull");
    private static readonly int CliffCoastMaskId = Shader.PropertyToID("_CliffCoastMask");
    private static readonly int CliffCoastMaskRectId = Shader.PropertyToID("_CliffCoastMaskRect");

    // === Settings (lazily loaded from API) ===
    private TerrainSettingsData terrainSettings;

    private bool settingsLoaded = false;
    private bool settingsLoading = false;

    // === Far heightmap cache (raw data, not Terrain objects) ===
    private float[,] farTerrainHeights;

    // Tile management
    private Dictionary<Vector2Int, Terrain> terrainTiles = new Dictionary<Vector2Int, Terrain>();


    // ================================================================
    // Incremental SetHeights heightmap streaming infrastructure
    // ================================================================

    /// <summary>Render grid shared by adjacent tiles. Leave subdivision render LOD to Unity Terrain.</summary>
    private const int TILE_RESOLUTION = 513;

    /// <summary>
    /// Rows per streaming band. Use a divisor: 513 = 9 bands × 57 rows,
    /// reducing each incremental SetHeights GPU upload/collider update to roughly 1/9 of full resolution.
    /// Keeping each band within the few-ms frame budget spreads application smoothly across frames.
    /// </summary>
    private const int STREAM_BAND_ROWS = 57;

    /// <summary>
    /// Horizontal half-angle (degrees) for prioritizing streaming by view.
    /// Always process cells within this angle of camera forward before out-of-view cells.
    /// Use the frustum only to weight processing order, not decide generation eligibility
    /// (out-of-view cells always process later, so turning never leaves them ungenerated).
    /// </summary>
    private const float STREAM_VIEW_HALF_ANGLE_DEG = 75f;



    /// <summary>
    /// Heightmap application request for one cell, issued by fetch coroutines
    /// and processed sequentially by <see cref="HeightStreamWorker"/> in view-priority order.
    /// </summary>
    private class HeightStreamRequest
    {
        /// <summary>Heightmap to apply (TILE_RESOLUTION square, normalized 0..1). Return to the pool and clear references after bands finish.</summary>
        public float[,] heights;
        /// <summary>Hidden tile from LRU. If non-null, reshow it without applying bands.</summary>
        public Terrain restoreTile;
        /// <summary>Issuing tile job. Discard even mid-band if canceled. Null for direct test insertion, which is never canceled.</summary>
        public TileJob job;
        /// <summary>Terrain revision (X-Cell-Version) recorded in dynamicCellVersions in the same step as publication.</summary>
        public int version;
        /// <summary>Whether processing ended (success or discard). Set in the same step as tile publication on success.</summary>
        public bool done;
        /// <summary>Whether applied to a tile. False on discard/interruption.</summary>
        public bool succeeded;
        /// <summary>Whether OnTerrainTileReady notification finished. Also true when notification is deliberately skipped.</summary>
        public bool notified;
    }

    /// <summary>Unstarted request per cell. New requests replace older unstarted requests for that cell (latest wins).</summary>
    private readonly Dictionary<Vector2Int, HeightStreamRequest> pendingStreamRequests = new Dictionary<Vector2Int, HeightStreamRequest>();

    /// <summary>
    /// Work from fetching through publishing one tile. Lifetime follows cell residency rather than load batches,
    /// so successor batches wait for the same job after replacement, retaining partially completed reception/band application.
    /// Cancel in UnloadCell, LoadLodCells for departed cells, or ClearAllCells (epoch).
    /// </summary>
    private sealed class TileJob
    {
        public Vector2Int cell;
        /// <summary>Required revision from the startup revision list. Batches requesting this revision or newer reuse this job.</summary>
        public int requiredVersion;
        public int epoch;
        /// <summary>Force refetch without using same-version LRU tiles.</summary>
        public bool force;
        public bool canceled, done;
        /// <summary>Whether a fetch slot is assigned. Only ReleaseTileSlot releases it (when publication starts or the job ends).</summary>
        public bool slotGranted;
    }

    /// <summary>Running tile job per cell. Remove on completion/cancellation.</summary>
    private readonly Dictionary<Vector2Int, TileJob> tileJobs = new Dictionary<Vector2Int, TileJob>();

    /// <summary>Jobs holding fetch slots (fetching or received and awaiting publication). ClearAllCells does not reset this; only slot returns decrement it.</summary>
    private int activeTileDownloads;

    /// <summary>
    /// Fetch slot count. Publication workers return slots when starting tile publication, limiting concurrent HTTP requests
    /// and memory for received height arrays awaiting publication, while allowing next-tile fetching during publication.
    /// </summary>
    private const int MAX_PARALLEL_TILE_DOWNLOADS = 4;

    /// <summary>
    /// Jobs waiting for fetch slots. <see cref="GrantTileSlots"/> assigns available slots one at a time with view priority.
    /// Each job waits only for its own assignment, avoiding waking every waiting job whenever one slot opens.
    /// </summary>
    private readonly List<TileJob> tileSlotWaiters = new List<TileJob>();

    /// <summary>
    /// Published tiles awaiting OnTerrainTileReady notification. <see cref="TileNotifyWorker"/> sends notifications as separate operations,
    /// allowing <see cref="HeightStreamWorker"/> to advance to the next tile while notification waits for budget.
    /// </summary>
    private readonly Queue<TileNotification> pendingTileNotifications = new Queue<TileNotification>();
    private Coroutine notifyWorkerCoroutine;

    private struct TileNotification
    {
        public Vector2Int cell;
        public Terrain tile;
        public int epoch;
        public HeightStreamRequest request;
    }

    /// <summary>
    /// Used-budget fraction threshold and maximum wait for starting indivisible expensive Unity API calls
    /// (SyncHeightmap/tile creation) and subscriber notifications while shared budget is mostly unused.
    /// Proceed regardless of budget after the maximum frame wait to prevent starvation.
    /// </summary>
    private const float FRESH_BUDGET_RATIO = 0.25f;
    private const int FRESH_BUDGET_MAX_WAIT_FRAMES = 8;

    private static bool HasFreshBudget() => RenderWorkScheduler.UsedMs <= PerformanceConfig.FrameBudgetMs * FRESH_BUDGET_RATIO;

    private bool IsJobCanceled(TileJob job) => job.canceled || job.epoch != clearEpoch;

    private bool IsRequestCanceled(HeightStreamRequest request, int epoch) =>
        clearEpoch != epoch || (request.job != null && IsJobCanceled(request.job));

    /// <summary>Mark unstarted/discarded requests complete. Waiting jobs exit as failures.</summary>
    private void DiscardRequest(HeightStreamRequest request)
    {
        ReleaseRequestResources(request);
        request.succeeded = false;
        request.done = true;
        request.notified = true;
    }

    /// <summary>
    /// Return request height arrays to the pool and restored tiles not reshown to LRU.
    /// Destroy restored tiles crossing a full clear (different epoch), since they belong to the old session.
    /// </summary>
    private void ReleaseRequestResources(HeightStreamRequest request)
    {
        ReturnHeightBuffer(request.heights);
        request.heights = null;
        Terrain tile = request.restoreTile;
        request.restoreTile = null;
        if (tile == null) return;
        if (request.job != null && request.job.epoch == clearEpoch) ParkTile(request.job.cell, tile, request.version);
        else DestroyTerrainTileObject(tile.gameObject);
    }

    // === Received height array pool ===
    /// <summary>
    /// Reusable tile-height array pool (TILE_RESOLUTION², about 1MB), avoiding repeated LOH allocation while moving.
    /// Concurrent use is roughly fetch slots plus one worker array; keep the cap slightly above that.
    /// Rent/return only on the main thread; return arrays written by other threads only after their Tasks finish.
    /// </summary>
    private readonly Stack<float[,]> heightBufferPool = new Stack<float[,]>();
    private const int HEIGHT_BUFFER_POOL_MAX = 6;

    private float[,] RentHeightBuffer() =>
        heightBufferPool.Count > 0 ? heightBufferPool.Pop() : new float[TILE_RESOLUTION, TILE_RESOLUTION];

    private void ReturnHeightBuffer(float[,] buffer)
    {
        if (buffer == null || buffer.GetLength(0) != TILE_RESOLUTION || buffer.GetLength(1) != TILE_RESOLUTION) return;
        if (heightBufferPool.Count >= HEIGHT_BUFFER_POOL_MAX || heightBufferPool.Contains(buffer)) return;
        heightBufferPool.Push(buffer);
    }

    /// <summary>Persistent worker processing streaming serially. Starts on first request.</summary>
    private Coroutine streamWorkerCoroutine;

    /// <summary>Reusable band-copy buffer ([STREAM_BAND_ROWS, TILE_RESOLUTION]). One suffices because the worker is serial.</summary>
    private float[,] streamBandScratch;

    /// <summary>
    /// Generation counter incremented by each ClearAllCells.
    /// Workers compare against the application-start value to stop on full clears mid-stream,
    /// preventing writes to destroyed TerrainData and orphaned tile registration afterward.
    /// </summary>
    private int clearEpoch = 0;

    // UltraFar: land silhouette meshes generated from coastline polygons rather than Unity Terrain.
    // Generate no mesh for sea (outside land); existing sea planes continue to represent water.
    private GameObject ultraFarLandObject;
    private Mesh ultraFarLandMesh;
    private Material ultraFarLandMat;

    // === Loaded cell mask (UltraFar sinking) ===
    private static readonly int LoadedCellMaskId = Shader.PropertyToID("_LoadedCellMask");
    private static readonly int LoadedCellMaskParamsId = Shader.PropertyToID("_LoadedCellMaskParams");
    private static readonly int LoadedCellMaskTexelId = Shader.PropertyToID("_LoadedCellMaskTexel");
    private static readonly int FarCityRectId = Shader.PropertyToID("_FarCityRect");
    private static readonly int FarSinkAmountId = Shader.PropertyToID("_FarSinkAmount");

    /// <summary>
    /// R8 mask of loaded (=visible) per-cell tiles (one texel per backend cell).
    /// UltraFar land shaders read this per vertex and sink only cells containing tiles.
    /// Rebuild only when terrainTiles changes (loadedCellMaskDirty); skip GPU upload if contents match.
    /// </summary>
    private Texture2D loadedCellMaskTex;
    private byte[] loadedCellMaskBuf;
    private byte[] loadedCellMaskPrevBuf;
    /// <summary>Always set when modifying terrainTiles. Otherwise visible tiles and masks diverge, allowing Far terrain to protrude.</summary>
    private bool loadedCellMaskDirty = true;

    // === Nonresident tile LRU ===
    /// <summary>
    /// Tiles that left residency, retained inactive with revisions. On reentry requiring the same revision or newer,
    /// reshow without fetching, band application, SyncHeightmap, or collider generation. Excluded from terrainTiles,
    /// so grounding, GPU height windows, and UltraFar masks never reference retained tiles.
    /// </summary>
    private sealed class CachedTile
    {
        public Terrain terrain;
        public int version;
        public LinkedListNode<Vector2Int> node;
    }

    private readonly Dictionary<Vector2Int, CachedTile> tileCache = new Dictionary<Vector2Int, CachedTile>();
    /// <summary>tileCache insertion order. Oldest first, discarded from here when capacity is exceeded.</summary>
    private readonly LinkedList<Vector2Int> tileCacheOrder = new LinkedList<Vector2Int>();
    /// <summary>Estimated 2–4MB per tile (heights, GPU heights, PhysX heightfield). Retain prefetched nearby cells and cells lost during back-and-forth rotation together.</summary>
    private const int TILE_CACHE_CAPACITY = 48;

    /// <summary>
    /// Destroy terrain tile GameObjects together with TerrainData assets.
    /// TerrainData is an independent asset not released by GameObject destruction;
    /// without explicit destruction, about 1MB plus GPU copies leaks per tile until scene exit.
    /// Always destroy tiles through this helper.
    /// </summary>
    private static void DestroyTerrainTileObject(GameObject go)
    {
        if (go == null) return;
        Terrain t = go.GetComponent<Terrain>();
        TerrainData td = t != null ? t.terrainData : null;
        Destroy(go);
        if (td != null) Destroy(td);
    }

    /// <summary>
    /// Hide departing resident tiles and append to LRU. Destroy older retained entries for the same cell and oldest entries beyond capacity.
    /// Callers must remove from terrainTiles and reconnect adjacent tiles themselves.
    /// </summary>
    private void ParkTile(Vector2Int cell, Terrain tile, int version)
    {
        if (tile == null) return;
        tile.SetNeighbors(null, null, null, null);
        tile.gameObject.SetActive(false);
        DropCachedTile(cell, tile.gameObject);
        tileCache[cell] = new CachedTile { terrain = tile, version = version, node = tileCacheOrder.AddLast(cell) };
        while (tileCache.Count > TILE_CACHE_CAPACITY)
        {
            DropCachedTile(tileCacheOrder.First.Value, null);
            RenderWorkScheduler.CountEvent("terrain.tileCacheEvicted");
        }
    }

    /// <summary>Remove and return an LRU tile meeting the required revision. Immediately discard stale/destroyed retained entries.</summary>
    private bool TryTakeCachedTile(Vector2Int cell, int requiredVersion, out CachedTile cached)
    {
        cached = null;
        if (!tileCache.TryGetValue(cell, out CachedTile entry)) return false;
        if (entry.terrain == null || entry.version < requiredVersion)
        {
            DropCachedTile(cell, null);
            return false;
        }
        tileCache.Remove(cell);
        tileCacheOrder.Remove(entry.node);
        cached = entry;
        return true;
    }

    /// <summary>Remove and destroy a cell's retained LRU tile, except the same GameObject as keep (avoids double destruction).</summary>
    private void DropCachedTile(Vector2Int cell, GameObject keep)
    {
        if (!tileCache.TryGetValue(cell, out CachedTile entry)) return;
        tileCache.Remove(cell);
        tileCacheOrder.Remove(entry.node);
        if (entry.terrain != null && entry.terrain.gameObject != keep) DestroyTerrainTileObject(entry.terrain.gameObject);
    }

    // === Unity callbacks ===
    private void Awake()
    {
        terrainSize.x = CellSize;
        terrainSize.z = CellSize;

        // Register the instance in a static cache so Utils.GetGroundHeight
        // does not run FindAnyObjectByType on each call.
        // This makes thousands of GetGroundHeight calls per frame O(1).
        Utils.RegisterTerrainManager(this);
        GroundCoverPalette.Changed += MarkGroundCoverMaterialsDirty;
        if (GetComponent<CityBoundaryOverlay>() == null) gameObject.AddComponent<CityBoundaryOverlay>();
    }

    private void OnDestroy()
    {
        GroundCoverPalette.Changed -= MarkGroundCoverMaterialsDirty;
        ClearCliffCoastMask();
        // Clear cached references when TerrainManager is destroyed,
        // allowing subsequent GetGroundHeight calls to use the fallback path.
        if (Utils.CachedTerrainManager == this)
        {
            Utils.RegisterTerrainManager(null);
        }
    }

    private void Update()
    {
        UpdateCameraClipDistance();
        UpdateLoadedCellMask();

        if (groundCoverMaterialsDirty)
        {
            groundCoverMaterialsDirty = false;
            RefreshGroundCoverMaterials();
        }
    }

    private void Start()
    {
        Shader.SetGlobalFloat("_ShowContour", 0f);
    }

    private void UpdateCameraClipDistance()
    {
        Camera camera = Camera.main;
        if (terrainSettings == null || camera == null) return;

        // Overview meshes also cover outside resident cells, so keep even the farthest map corner within clipping range.
        // Include terrain vertical bounds after viewpoint/altitude changes and never shorten the scene-specified distance.
        Vector3 position = camera.transform.position;
        float dx = Mathf.Abs(position.x) + terrainSettings.map_width * 0.5f;
        float dz = Mathf.Abs(position.z) + terrainSettings.map_height * 0.5f;
        float dy = Mathf.Max(Mathf.Abs(position.y + SeaFloorDepthSpan),
            Mathf.Abs(position.y - (terrainSize.y - SeaFloorDepthSpan + ultraFarHeightOffset)));
        if (backgroundHeights != null)
        {
            dy = Mathf.Max(dy, Mathf.Max(Mathf.Abs(position.y-backgroundMinHeight), Mathf.Abs(position.y-backgroundMaxHeight)));
            Rect bounds = BackgroundBounds;
            dx = Mathf.Max(dx, Mathf.Max(Mathf.Abs(position.x-bounds.xMin), Mathf.Abs(position.x-bounds.xMax)));
            dz = Mathf.Max(dz, Mathf.Max(Mathf.Abs(position.z-bounds.yMin), Mathf.Abs(position.z-bounds.yMax)));
        }
        float requiredDistance = new Vector3(dx, dy, dz).magnitude + CellSize;
        if (camera.farClipPlane < requiredDistance) camera.farClipPlane = requiredDistance;
    }

    private void OnValidate()
    {
        terrainMacroVariationScale = Mathf.Max(1f, terrainMacroVariationScale);
        cliffTilingMeters = Mathf.Max(1f, cliffTilingMeters);
        cliffNoiseScale = Mathf.Max(1f, cliffNoiseScale);

        MarkGroundCoverMaterialsDirty();
    }

    /// <summary>
    /// Apply ground appearance settings (Inspector/palette) changed during Play once in the next Update.
    /// Slider drags send repeated notifications; do not rebuild tiles immediately on each one.
    /// </summary>
    private void MarkGroundCoverMaterialsDirty()
    {
        if (Application.isPlaying && terrainTiles != null) groundCoverMaterialsDirty = true;
    }

    // ================================================================
    // Lazy settings loading
    // ================================================================

    private IEnumerator EnsureSettingsLoaded()
    {
        if (settingsLoaded) yield break;

        if (settingsLoading)
        {
            while (settingsLoading) yield return null;
            yield break;
        }

        settingsLoading = true;
        bool success = false;
        string error = null;

        yield return ApiClient.Get<TerrainSettingsData>(
            "/terrain/settings",
            settings =>
            {
                terrainSettings = settings;
                success = true;
            },
            err => { error = err; }
        );

        settingsLoading = false;

        if (success && terrainSettings != null)
        {
            settingsLoaded = true;
            CellSize = terrainSettings.cell_size;
            terrainSize.x = CellSize;
            terrainSize.z = CellSize;
            Debug.Log($"TerrainManager: Settings loaded. Map={terrainSettings.map_width}x{terrainSettings.map_height}m, " +
                      $"cell={terrainSettings.cell_size}m, far={terrainSettings.far_map_spacing}m, " +
                      $"near={terrainSettings.near_map_spacing}m, dynamic={terrainSettings.dynamic_map_spacing}m");
        }
        else
        {
            Debug.LogError($"TerrainManager: Failed to load settings: {error}");
        }
    }

    // ================================================================
    // float16 LE binary → float[,] conversion
    // ================================================================

    /// <summary>
    /// Convert backend float16 LE binary into heightmap float[,].
    /// Static for safe background-thread calls; do not access Unity instance state.
    ///
    /// Backend float16 stores world elevation (meters), with sea level at 0 and negative seabed elevations
    /// outside the coastline. Terrain heightmaps map [0,1] to local Y=0–verticalSpan,
    /// so add the <paramref name="belowSeaSpan"/> offset during normalization
    /// and map to <c>(elevation + belowSeaSpan) / verticalSpan</c>. Sea level (elevation 0) then lies at
    /// <c>belowSeaSpan/verticalSpan</c>, with seabeds (negative elevations) between 0 and that height.
    /// Lower tile base Y by <c>-belowSeaSpan</c> to preserve worldY = actual elevation
    /// (see <see cref="SeaFloorDepthSpan"/>).
    /// </summary>
    /// <param name="verticalSpan">World height span (m) corresponding to local Y 0–1 (= terrainSize.y)</param>
    /// <param name="belowSeaSpan">Vertical span (m) allocated below sea level (= <see cref="SeaFloorDepthSpan"/>)</param>
    private static float[,] ConvertFloat16BinaryToHeights(byte[] rawBytes, int rows, int cols, float verticalSpan, float belowSeaSpan)
    {
        int count = rows * cols;
        int expectedBytes = count * 2;
        if (rawBytes.Length < expectedBytes)
        {
            Debug.LogError($"TerrainManager: Binary size mismatch. Expected {expectedBytes}, got {rawBytes.Length}");
            return new float[rows, cols];
        }

        float[,] heights = new float[rows, cols];
        float safeSpan = verticalSpan > 1e-3f ? verticalSpan : 1e-3f;
        float invSpan = 1f / safeSpan;
        for (int i = 0; i < count; i++)
        {
            float elevationMeters = HalfToFloat(rawBytes, i * 2);
            // Add the below-sea offset before normalizing. Negative seabed elevations within belowSeaSpan
            // fit in 0–(belowSeaSpan/verticalSpan); deeper seabeds clamp to 0 (minimum depth).
            float normalized = (elevationMeters + belowSeaSpan) * invSpan;
            if (normalized < 0f) normalized = 0f;
            else if (normalized > 1f) normalized = 1f;
            heights[i / cols, i % cols] = normalized;
        }
        return heights;
    }

    /// <summary>
    /// Convert IEEE 754 half-precision floating point (float16, little-endian) to float.
    /// </summary>
    private static float HalfToFloat(byte[] bytes, int offset)
    {
        ushort bits = (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

        int sign = (bits >> 15) & 0x1;
        int exponent = (bits >> 10) & 0x1F;
        int mantissa = bits & 0x3FF;

        float result;
        if (exponent == 0)
        {
            if (mantissa == 0)
            {
                // ±zero
                result = 0f;
            }
            else
            {
                // Subnormal: (-1)^sign * 2^(−14) * (0.mantissa)
                result = Mathf.Pow(2f, -14f) * (mantissa / 1024f);
            }
        }
        else if (exponent == 31)
        {
            if (mantissa == 0)
            {
                // ±infinity → clamp
                result = sign == 0 ? float.MaxValue : float.MinValue;
            }
            else
            {
                // NaN → 0
                result = 0f;
            }
        }
        else
        {
            // Normal: (-1)^sign * 2^(exponent−15) * (1 + mantissa/1024)
            result = Mathf.Pow(2f, exponent - 15) * (1f + mantissa / 1024f);
        }

        return sign == 0 ? result : -result;
    }

    // ================================================================
    // Load Far heightmap data without creating Terrain objects
    // ================================================================

    private IEnumerator LoadFarTerrainData()
    {
        if (terrainSettings == null) yield break;
        if (farTerrainHeights != null) yield break;
        int epoch = clearEpoch;

        int farRows = terrainSettings.map_height / terrainSettings.far_map_spacing + 1;
        int farCols = terrainSettings.map_width / terrainSettings.far_map_spacing + 1;

        byte[] rawBytes = null;

        var swFar = System.Diagnostics.Stopwatch.StartNew();
        using (ApiRequest call = ApiClient.Request("GET", "/terrain/far", timeoutSeconds: 30))
        {
            yield return call.Send();
            UnityWebRequest req = call.Web;

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"TerrainManager: Far terrain data fetch failed: {req.error}");
                yield break;
            }

            rawBytes = req.downloadHandler.data;
        }
        if (epoch != clearEpoch) yield break;
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  network/far: {swFar.ElapsedMilliseconds}ms ({rawBytes?.Length ?? 0} bytes)");

        int targetResolution = GetValidTerrainResolution(Mathf.Max(farRows, farCols));
        var swProc = System.Diagnostics.Stopwatch.StartNew();
        Task<float[,]> task = ProcessHeightmapAsync(rawBytes, farRows, farCols, targetResolution);
        yield return Utils.WaitTask(task);
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] TerrainManager  ProcessHeightmapAsync/far: {swProc.ElapsedMilliseconds}ms");
        if (epoch != clearEpoch || task.IsFaulted || task.IsCanceled || task.Result == null) yield break;

        farTerrainHeights = task.Result;

        Debug.Log($"TerrainManager: Far terrain data cached. Grid={farRows}x{farCols}, " +
                  $"resolution={targetResolution}");
    }

    // ================================================================
    // Load Dynamic terrain
    // ================================================================

    /// <summary>
    /// Return the cell tile job. Reuse a running job meeting the required revision unless force is true;
    /// otherwise cancel the old job and start a new one.
    /// </summary>
    private TileJob EnsureTileJob(Vector2Int cell, int requiredVersion, bool force)
    {
        if (tileJobs.TryGetValue(cell, out TileJob existing) && !existing.done && !IsJobCanceled(existing))
        {
            if (!force && existing.requiredVersion >= requiredVersion)
            {
                RenderWorkScheduler.CountEvent("terrain.tileJobReused");
                return existing;
            }
            CancelTileJob(cell);
        }
        var job = new TileJob { cell = cell, requiredVersion = requiredVersion, epoch = clearEpoch, force = force };
        tileJobs[cell] = job;
        if (RenderWorkScheduler.Start(this, TileJobRoutine(job), "TerrainManager") == null)
        {
            job.done = true;
            tileJobs.Remove(cell);
        }
        return job;
    }

    /// <summary>Cancel the cell's running job and discard its unstarted requests.</summary>
    private void CancelTileJob(Vector2Int cell)
    {
        if (!tileJobs.TryGetValue(cell, out TileJob job)) return;
        job.canceled = true;
        tileJobs.Remove(cell);
        if (pendingStreamRequests.TryGetValue(cell, out HeightStreamRequest request) && request.job == job)
        {
            DiscardRequest(request);
            pendingStreamRequests.Remove(cell);
        }
    }

    private static bool AllTileJobsDone(List<TileJob> jobs)
    {
        for (int i = 0; i < jobs.Count; i++) if (!jobs[i].done) return false;
        return true;
    }

    /// <summary>Return the job's fetch slot and reassign it to waiting jobs.</summary>
    private void ReleaseTileSlot(TileJob job)
    {
        if (job == null || !job.slotGranted) return;
        job.slotGranted = false;
        activeTileDownloads--;
        GrantTileSlots();
    }

    /// <summary>Assign free fetch slots to waiting jobs in view-priority order (<see cref="ScoreStreamCell"/>). Remove canceled jobs.</summary>
    private void GrantTileSlots()
    {
        for (int i = tileSlotWaiters.Count - 1; i >= 0; i--)
            if (tileSlotWaiters[i].done || IsJobCanceled(tileSlotWaiters[i])) tileSlotWaiters.RemoveAt(i);
        if (tileSlotWaiters.Count == 0 || activeTileDownloads >= MAX_PARALLEL_TILE_DOWNLOADS) return;
        GetStreamViewpoint(out Vector3 camPos, out Vector3 fwd);
        while (activeTileDownloads < MAX_PARALLEL_TILE_DOWNLOADS && tileSlotWaiters.Count > 0)
        {
            int best = 0;
            float bestScore = float.MaxValue;
            for (int i = 0; i < tileSlotWaiters.Count; i++)
            {
                float score = ScoreStreamCell(tileSlotWaiters[i].cell, camPos, fwd);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            TileJob granted = tileSlotWaiters[best];
            tileSlotWaiters.RemoveAt(best);
            granted.slotGranted = true;
            activeTileDownloads++;
        }
    }

    /// <summary>
    /// Fetch and normalize one cell tile, then wait for streaming application and OnTerrainTileReady notification.
    /// If LRU has a tile meeting the required revision, request restoration without consuming a fetch slot (unused with force).
    /// Use native waits (WaitUntil) throughout, consuming no shared-budget Steps while waiting.
    /// </summary>
    private IEnumerator TileJobRoutine(TileJob job)
    {
        Vector2Int cell = job.cell;
        float[,] buffer = null;
        Task normalizeTask = null;
        try
        {
            // Jobs replaced before their first Step must not take LRU tiles needed by successor jobs.
            if (IsJobCanceled(job)) yield break;
            if (!job.force && TryTakeCachedTile(cell, job.requiredVersion, out CachedTile cached))
            {
                RenderWorkScheduler.CountEvent("terrain.tileCacheHit");
                var restoreRequest = new HeightStreamRequest { job = job, version = cached.version, restoreTile = cached.terrain };
                yield return EnqueueAndWait(cell, restoreRequest);
                // Proceed to fetching only if restoration failed, such as destruction while retained.
                if (restoreRequest.succeeded || IsJobCanceled(job)) yield break;
            }
            // If prefetched heights are available, skip fetching and request publication only.
            if (!job.force && TryTakePrefetchedHeights(cell, job.requiredVersion, out float[,] prefetched, out int prefetchedVersion))
            {
                RenderWorkScheduler.CountEvent("terrain.prefetchHeightsHit");
                yield return EnqueueAndWait(cell, new HeightStreamRequest { heights = prefetched, job = job, version = prefetchedVersion });
                yield break;
            }

            tileSlotWaiters.Add(job);
            GrantTileSlots();
            if (!job.slotGranted) yield return new WaitUntil(() => job.slotGranted || IsJobCanceled(job));
            if (IsJobCanceled(job)) yield break;

            Vector2Int backend = UnityCellToBackendCell(cell);
            string path = $"/terrain/surface?cell_x={backend.x}&cell_z={backend.y}&resolution={TILE_RESOLUTION}";
            byte[] bytes;
            int version;
            RenderWorkScheduler.CountEvent("terrain.tileDownload");
            using (ApiRequest call = ApiClient.Request("GET", path, timeoutSeconds: 30))
            {
                yield return call.Send(() => IsJobCanceled(job));
                if (call.Aborted)
                {
                    RenderWorkScheduler.CountEvent("terrain.tileDownloadAborted");
                    yield break;
                }
                UnityWebRequest request = call.Web;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"地形セル{cell}の取得失敗: {request.error}");
                    yield break;
                }
                bytes = request.downloadHandler.data;
                version = ParseIntHeader(request, "X-Cell-Version", 0);
            }
            if (IsJobCanceled(job)) yield break;
            float span = terrainSize.y;
            float below = SeaFloorDepthSpan;
            float[,] heights = buffer = RentHeightBuffer();
            normalizeTask = BackgroundWork.Run(() => NormalizeTileHeights(bytes, heights, span, below));
            Task task = normalizeTask;
            yield return new WaitUntil(() => task.IsCompleted || IsJobCanceled(job));
            if (IsJobCanceled(job)) yield break;
            if (task.IsFaulted || task.IsCanceled)
            {
                Debug.LogError($"地形セル{cell}の正規化失敗: {task.Exception?.Flatten()}");
                yield break;
            }
            var streamRequest = new HeightStreamRequest { heights = buffer, job = job, version = version };
            buffer = null;
            yield return EnqueueAndWait(cell, streamRequest);
        }
        finally
        {
            // Leave arrays still being written by normalization Tasks to GC rather than returning them to the pool.
            if (buffer != null && (normalizeTask == null || normalizeTask.IsCompleted)) ReturnHeightBuffer(buffer);
            job.done = true;
            if (job.slotGranted) ReleaseTileSlot(job);
            else tileSlotWaiters.Remove(job);
            if (tileJobs.TryGetValue(cell, out TileJob current) && current == job) tileJobs.Remove(cell);
        }
    }

    /// <summary>
    /// Copy received float32 elevations directly into tile arrays while normalizing to 0–1, avoiding intermediate 1D arrays.
    /// Called on another thread; do not access Unity APIs.
    /// </summary>
    private static void NormalizeTileHeights(byte[] bytes, float[,] heights, float span, float below)
    {
        if (bytes.Length != TILE_RESOLUTION * TILE_RESOLUTION * sizeof(float)) throw new InvalidOperationException("地形配列のサイズが不正です");
        Buffer.BlockCopy(bytes, 0, heights, 0, bytes.Length);
        for (int z = 0; z < TILE_RESOLUTION; z++)
            for (int x = 0; x < TILE_RESOLUTION; x++)
            {
                float value = heights[z, x];
                if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException("地形に非有限値があります");
                heights[z, x] = Mathf.Clamp01((value + below) / span);
            }
    }

    // ================================================================
    // BaseApiDataManager implementation
    // ================================================================

    protected override void UnloadCell(Vector2Int cellIndex)
    {
        // Discard fetching jobs and unstarted streaming requests, avoiding wasteful application outside the range.
        CancelTileJob(cellIndex);
        if (pendingStreamRequests.TryGetValue(cellIndex, out HeightStreamRequest pendingReq))
        {
            DiscardRequest(pendingReq);
            pendingStreamRequests.Remove(cellIndex);
        }

        if (terrainTiles.TryGetValue(cellIndex, out Terrain t))
        {
            terrainTiles.Remove(cellIndex);
            loadedCellMaskDirty = true;
            // Do not retain tiles with unknown versions, since they cannot be reused on reentry.
            if (t != null)
            {
                if (dynamicCellVersions.TryGetValue(cellIndex, out int version)) ParkTile(cellIndex, t, version);
                else DestroyTerrainTileObject(t.gameObject);
            }
            dynamicCellVersions.Remove(cellIndex);
            Utils.InvalidateHeightCache();
            HeightmapTextureManager.Instance?.MarkSurfaceChanged(cellIndex);
            UpdateNeighborsForAdjacents(cellIndex);
        }
    }

    public override void ClearAllCells()
    {
        ClearBackground();
        currentLoadBatch?.Cancel();
        if (loadLodCellsCoroutine != null)
        {
            RenderWorkScheduler.Stop(this, loadLodCellsCoroutine);
            loadLodCellsCoroutine = null;
        }
        settingsLoading = false;
        // Invalidate all running and pending streaming work.
        // Incrementing clearEpoch stops active workers before their next band,
        // preventing writes to destroyed TerrainData or orphaned tile registration after clearing.
        clearEpoch++;
        foreach (var job in tileJobs.Values) job.canceled = true;
        tileJobs.Clear();
        foreach (var kvp in pendingStreamRequests) DiscardRequest(kvp.Value);
        pendingStreamRequests.Clear();
        foreach (var notification in pendingTileNotifications) notification.request.notified = true;
        pendingTileNotifications.Clear();
        heightBufferPool.Clear();

        // Terrain per cell (destroy TerrainData assets too)
        foreach (var kvp in terrainTiles)
        {
            if (kvp.Value != null) DestroyTerrainTileObject(kvp.Value.gameObject);
        }
        terrainTiles.Clear();
        loadedCellMaskDirty = true;
        Utils.InvalidateHeightCache();
        loadedCells.Clear();
        dynamicCellVersions.Clear();

        foreach (var entry in tileCache.Values)
        {
            if (entry.terrain != null) DestroyTerrainTileObject(entry.terrain.gameObject);
        }
        tileCache.Clear();
        tileCacheOrder.Clear();

        // UltraFar land meshes
        if (ultraFarLandObject != null)
        {
            Destroy(ultraFarLandObject);
            ultraFarLandObject = null;
        }
        if (ultraFarLandMesh != null)
        {
            Destroy(ultraFarLandMesh);
            ultraFarLandMesh = null;
        }

        // Clear Far data caches
        farTerrainHeights = null;
        overviewDataRevision = -1;
        overviewRevision = -1;
        cachedCellVersions = null;
        cellVersionsDirty = true;
        cellVersionsGeneration++;

        residentTerrainCells.Clear();
        ClearPrefetch();

        // Clear settings caches (session.reset support)
        terrainSettings = null;
        settingsLoaded = false;
    }

    // ================================================================
    // LOD-aware cell loading
    // ================================================================

    private Coroutine loadLodCellsCoroutine;

    public override void LoadLodCells(LodCellSets cellSets, bool isForce)
    {
        if (loadLodCellsCoroutine != null)
        {
            RenderWorkScheduler.Stop(this, loadLodCellsCoroutine);
            // Explicitly reset settingsLoading here because StopCoroutine may prevent
            // settingsLoading=false inside the interrupted coroutine from running.
            // Otherwise the next EnsureSettingsLoaded loops forever in while(settingsLoading),
            // preventing all terrain, buildings, and roads from loading.
            UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "TerrainManager: loadLodCellsCoroutine != null");
            settingsLoading = false;
        }

        // When called through CellDataLoader (LoadBatch overload), currentLoadBatch
        // already contains the new batch. For direct calls (no batch or previous batch canceled),
        // issue a batch for this load.
        if (currentLoadBatch == null || currentLoadBatch.IsCanceled)
        {
            currentLoadBatch = new LoadBatch(cellSets, isForce);
        }

        // Tile jobs survive batch cancellation; cancel jobs for cells leaving residency here.
        if (tileJobs.Count > 0)
        {
            var resident = new HashSet<Vector2Int>(cellSets.farCells ?? new List<Vector2Int>());
            var jobCells = new List<Vector2Int>(tileJobs.Keys);
            foreach (var cell in jobCells)
                if (!resident.Contains(cell)) CancelTileJob(cell);
        }
        SetPrefetchCells(cellSets.prefetchCells);

        loadLodCellsCoroutine = RenderWorkScheduler.Start(this, LoadLodCellsCoroutine(cellSets, isForce, currentLoadBatch), "TerrainManager");
    }

    private IEnumerator LoadLodCellsCoroutine(LodCellSets cellSets, bool isForce, LoadBatch batch)
    {
        yield return EnsureSettingsLoaded();
        if (terrainSettings == null || batch.IsCanceled) yield break;
        if (isForce || cellVersionsDirty || cachedCellVersions == null || Time.unscaledTime - cellVersionsFetchedAt > CELL_VERSIONS_MAX_AGE)
        {
            int generation = cellVersionsGeneration;
            CellVersionsResponse response = null;
            while (response == null && !batch.IsCanceled)
            {
                yield return ApiClient.Get<CellVersionsResponse>("/terrain/cell-versions", value => response = value,
                    error => Debug.LogWarning($"地形の版情報取得失敗: {error}"));
                if (response == null) yield return new WaitForSecondsRealtime(2f);
            }
            if (batch.IsCanceled) yield break;
            var fetched = new Dictionary<Vector2Int, int>();
            if (response.cells != null)
                foreach (var item in response.cells)
                    foreach (var cell in BackendCellToUnityCells(new Vector2Int(item.cell_x, item.cell_z)))
                        fetched[cell] = item.version;
            cachedCellVersions = fetched;
            cellVersionsFetchedAt = Time.unscaledTime;
            if (generation == cellVersionsGeneration) cellVersionsDirty = false;
            RenderWorkScheduler.CountEvent("terrain.cellVersionsFetched");
        }
        else RenderWorkScheduler.CountEvent("terrain.cellVersionsReused");
        // Keep using this fetched list read-only for the batch even if later refetches replace it during yields.
        Dictionary<Vector2Int, int> versions = cachedCellVersions;
        long revision = 0;
        foreach (int version in versions.Values) revision += version;
        yield return EnsureOverview(revision, batch);
        yield return EnsureBackground(batch);
        if (batch.IsCanceled) yield break;
        var desired = new HashSet<Vector2Int>(cellSets.farCells);
        desired.RemoveWhere(cell => !IsCityCell(cell));
        residentTerrainCells.Clear();
        residentTerrainCells.UnionWith(desired);
        OnTerrainOverviewReady?.Invoke();
        var remove = new List<Vector2Int>();
        foreach (var cell in loadedCells) if (!desired.Contains(cell)) remove.Add(cell);
        foreach (var cell in remove)
        {
            UnloadCell(cell);
            loadedCells.Remove(cell);
            yield return RenderWorkScheduler.Continue;
            if (batch.IsCanceled) yield break;
        }
        var pending = new List<Vector2Int>(desired);
        Vector3 camera = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
        pending.Sort((a, b) => (new Vector2(a.x * CellSize - camera.x, a.y * CellSize - camera.z)).sqrMagnitude.CompareTo(
            (new Vector2(b.x * CellSize - camera.x, b.y * CellSize - camera.z)).sqrMagnitude));
        var jobs = new List<TileJob>();
        bool firstPass = true;
        bool incomplete;
        do
        {
            incomplete = false;
            jobs.Clear();
            foreach (var cell in pending)
            {
                int version = versions.TryGetValue(cell, out int value) ? value : 0;
                bool force = isForce && firstPass;
                if (!force && HasSurfaceTile(cell) && dynamicCellVersions.TryGetValue(cell, out int current) && current >= version)
                {
                    // Wait for published jobs whose notifications are pending too, ensuring OnAllTerrainLodLoaded follows all tile notifications.
                    if (tileJobs.TryGetValue(cell, out TileJob running) && !running.done && !IsJobCanceled(running)) jobs.Add(running);
                    continue;
                }
                jobs.Add(EnsureTileJob(cell, version, force));
            }
            // Do not stop jobs on batch cancellation; subsequent batches reuse them.
            if (jobs.Count > 0) yield return new WaitUntil(() => batch.IsCanceled || AllTileJobsDone(jobs));
            if (batch.IsCanceled) yield break;
            foreach (var cell in pending)
            {
                int required = versions.TryGetValue(cell, out int value) ? value : 0;
                if (!HasSurfaceTile(cell) || !dynamicCellVersions.TryGetValue(cell, out int current) || current < required)
                    incomplete = true;
            }
            firstPass = false;
            if (incomplete) yield return new WaitForSecondsRealtime(2f);
            if (batch.IsCanceled) yield break;
        } while (incomplete);
        foreach (var cell in updatedTerrainCellsThisLoad) UpdateNeighbors(cell);
        var updated = new List<Vector2Int>(updatedTerrainCellsThisLoad);
        updatedTerrainCellsThisLoad.Clear();
        OnAllTerrainLodLoaded?.Invoke(updated);
    }

    public bool HasSurfaceTile(Vector2Int cell) => terrainTiles.TryGetValue(cell, out var tile) && tile != null;

    /// <summary>Actual terrain data revision, unchanged by camera-driven tile residency changes.</summary>
    public string GetContentStamp(Rect area)
    {
        if (cachedCellVersions == null || cellVersionsDirty)
            return "unverified:" + cellVersionsGeneration + ":" + GetSurfaceStamp(area);
        var key = new System.Text.StringBuilder();
        key.Append(clearEpoch);
        WorldRectToCellRange(area.xMin, area.yMin, area.xMax, area.yMax, out Vector2Int minCell, out Vector2Int maxCell);
        for (int z = minCell.y; z <= maxCell.y; z++) for (int x = minCell.x; x <= maxCell.x; x++)
        {
            var cell = new Vector2Int(x, z);
            key.Append(':').Append(x).Append(',').Append(z).Append('=').Append(cachedCellVersions.TryGetValue(cell, out int version) ? version : 0);
        }
        return key.ToString();
    }

    /// <summary>Resident in-city cells from the latest load batch, determining where detail generation must wait.</summary>
    private readonly HashSet<Vector2Int> residentTerrainCells = new HashSet<Vector2Int>();

    /// <summary>
    /// Whether all resident cells overlapping the area have published tiles (meeting required revisions if the revision list is finalized). Ignore nonresident cells, whose tiles never arrive.
    /// Buildings/roads render as Far until true, avoiding generation at overview heights followed immediately by rebuilding.
    /// While refetching revision lists, check published tiles (stale ones trigger rebuilding on new-tile publication).
    /// </summary>
    public bool IsDetailTerrainReady(Rect area)
    {
        WorldRectToCellRange(area.xMin, area.yMin, area.xMax, area.yMax, out Vector2Int minCell, out Vector2Int maxCell);
        for (int z = minCell.y; z <= maxCell.y; z++)
            for (int x = minCell.x; x <= maxCell.x; x++)
                if (!IsCellDetailReady(new Vector2Int(x, z))) return false;
        return true;
    }

    /// <inheritdoc cref="IsDetailTerrainReady(Rect)"/>
    public bool IsDetailTerrainReady(HashSet<Vector2Int> cells)
    {
        if (cells != null) foreach (var cell in cells) if (!IsCellDetailReady(cell)) return false;
        return true;
    }

    private bool IsCellDetailReady(Vector2Int cell)
    {
        if (!residentTerrainCells.Contains(cell)) return true;
        int attached = GetAttachedTerrainVersion(cell);
        if (attached < 0) return false;
        if (cachedCellVersions == null || cellVersionsDirty) return true;
        return attached >= (cachedCellVersions.TryGetValue(cell, out int version) ? version : 0);
    }

    /// <summary>Whether grounding can be calculated without overview substitution, used when registering completed terrain-dependent meshes.</summary>
    public bool HasCurrentSurface(Rect area)
    {
        if (cachedCellVersions == null || cellVersionsDirty) return false;
        WorldRectToCellRange(area.xMin, area.yMin, area.xMax, area.yMax, out Vector2Int minCell, out Vector2Int maxCell);
        for (int z = minCell.y; z <= maxCell.y; z++) for (int x = minCell.x; x <= maxCell.x; x++)
        {
            var cell = new Vector2Int(x, z);
            if (terrainSettings != null && !IsCityCell(cell)) continue;
            int required = cachedCellVersions.TryGetValue(cell, out int version) ? version : 0;
            if (GetAttachedTerrainVersion(cell) != required) return false;
        }
        return true;
    }

    public string GetSurfaceStamp(Rect area)
    {
        var key = new System.Text.StringBuilder();
        key.Append(clearEpoch);
        WorldRectToCellRange(area.xMin, area.yMin, area.xMax, area.yMax, out Vector2Int minCell, out Vector2Int maxCell);
        for (int z = minCell.y; z <= maxCell.y; z++) for (int x = minCell.x; x <= maxCell.x; x++)
        {
            var cell = new Vector2Int(x, z);
            key.Append(':').Append(x).Append(',').Append(z).Append('=').Append(dynamicCellVersions.TryGetValue(cell, out int version) ? version : -1);
        }
        return key.ToString();
    }

    /// <summary>ClearAllCells generation, allowing signature/revision holders to detect full clears.</summary>
    public int TerrainEpoch => clearEpoch;

    /// <summary>
    /// Return the visible tile's terrain revision (/terrain/cell-versions revision; 0 for absent list entries).
    /// -1 for absent, nonresident, or inactive tiles. Main thread only.
    /// </summary>
    public int GetAttachedTerrainVersion(Vector2Int cell)
    {
        if (!terrainTiles.TryGetValue(cell, out Terrain tile) || tile == null || !tile.gameObject.activeSelf) return -1;
        return dynamicCellVersions.TryGetValue(cell, out int version) ? version : -1;
    }

    /// <summary>
    /// Invalidate cached terrain revisions. Called synchronously on data refetch requests (CellDataLoader.ReloadAllData).
    /// </summary>
    public void InvalidateCellVersions()
    {
        cellVersionsDirty = true;
        cellVersionsGeneration++;
    }

    /// <summary>
    /// Terrain signature for all cells overlapping area. Matching revisions, overview, and generation produce identical values after reentry.
    /// Version of GetSurfaceStamp without string construction; equal values imply equal heights.
    /// </summary>
    public long GetTerrainSignature(Rect area)
    {
        WorldRectToCellRange(area.xMin, area.yMin, area.xMax, area.yMax, out Vector2Int minCell, out Vector2Int maxCell);
        ulong sum = 0;
        for (int z = minCell.y; z <= maxCell.y; z++)
            for (int x = minCell.x; x <= maxCell.x; x++)
                sum += CellSignatureTerm(new Vector2Int(x, z));
        return FinishTerrainSignature(sum);
    }

    /// <summary>Order-independent terrain signature for an arbitrary cell set.</summary>
    public long GetTerrainSignature(IReadOnlyList<Vector2Int> cells)
    {
        ulong sum = 0;
        if (cells != null) for (int i = 0; i < cells.Count; i++) sum += CellSignatureTerm(cells[i]);
        return FinishTerrainSignature(sum);
    }

    /// <summary>Order-independent terrain signature for an arbitrary cell set.</summary>
    public long GetTerrainSignature(HashSet<Vector2Int> cells)
    {
        ulong sum = 0;
        if (cells != null) foreach (var cell in cells) sum += CellSignatureTerm(cell);
        return FinishTerrainSignature(sum);
    }

    /// <summary>
    /// False if TerrainManager is absent (including destroyed). Callers must treat this as unknown and must not skip rebuilding.
    /// </summary>
    public static bool TryGetTerrainSignature(IReadOnlyList<Vector2Int> cells, out long signature)
    {
        TerrainManager manager = Utils.CachedTerrainManager;
        if (manager == null)
        {
            signature = 0;
            return false;
        }
        signature = manager.GetTerrainSignature(cells);
        return true;
    }

    // Cells without tiles use overview heights, so mix in the overview data revision too.
    private ulong CellSignatureTerm(Vector2Int cell)
    {
        int version = GetAttachedTerrainVersion(cell);
        ulong term = SplitMix64((ulong)(((long)cell.x << 32) | (uint)cell.y) ^ SplitMix64((ulong)(version + 1L)));
        if (version == -1) term = SplitMix64(term ^ (ulong)(farTerrainHeights != null ? overviewDataRevision : -1L));
        return term;
    }

    private long FinishTerrainSignature(ulong sum) => (long)SplitMix64(sum ^ SplitMix64((ulong)clearEpoch));

    private static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    // Separate data reception from completed rendering; after interruption regenerate unfinished meshes from received data.
    private long overviewDataRevision = -1;
    private long overviewRevision = -1;

    /// <summary>Revision of received overview elevations. Objects placed at overview heights use this to decide rebuilding. -1 before reception.</summary>
    public long OverviewDataRevision => farTerrainHeights != null ? overviewDataRevision : -1;

    private IEnumerator EnsureOverview(long revision, LoadBatch batch)
    {
        int epoch = clearEpoch;
        while (overviewRevision != revision && !batch.IsCanceled && epoch == clearEpoch)
        {
            if (farTerrainHeights == null || overviewDataRevision != revision)
            {
                farTerrainHeights = null;
                yield return LoadFarTerrainData();
                if (batch.IsCanceled || epoch != clearEpoch) yield break;
                if (farTerrainHeights != null) overviewDataRevision = revision;
            }
            if (farTerrainHeights != null)
            {
                GameObject previous = ultraFarLandObject;
                Mesh previousMesh = ultraFarLandMesh;
                bool completed = false;
                yield return BuildUltraFarTerrain(batch, () => completed = true);
                if (batch.IsCanceled || epoch != clearEpoch) yield break;
                if (completed)
                {
                    if (previous != null && previous != ultraFarLandObject) Destroy(previous);
                    if (previousMesh != null && previousMesh != ultraFarLandMesh) Destroy(previousMesh);
                    overviewRevision = revision;
                    yield break;
                }
            }
            yield return new WaitForSecondsRealtime(2f);
        }
    }

    private IEnumerator BuildUltraFarTerrain(LoadBatch batch, Action onCompleted)
    {
        if (farTerrainHeights == null || terrainSettings == null || batch.IsCanceled) yield break;
        int epoch = clearEpoch;

        float mapWidth = terrainSettings.map_width;
        float mapHeight = terrainSettings.map_height;
        float verticalSpan = terrainSize.y;
        float belowSeaSpan = SeaFloorDepthSpan;
        float farOriginX = -mapWidth / 2f;
        float farOriginZ = -mapHeight / 2f;

        // Capture background-thread inputs locally, avoiding Unity APIs and mutable fields.
        float[,] far = farTerrainHeights;
        int farCols = far.GetLength(1);
        int farRows = far.GetLength(0);

        // Bilinearly sample world Y (meters) from far_map (normalized 0..1).
        // Normalization includes belowSeaSpan, so subtract the same amount as per-cell tile
        // base Y (-SeaFloorDepthSpan) to recover actual world elevations.
        Func<float, float, float> sampleHeight = (x, z) =>
        {
            float u = Mathf.Clamp01((x - farOriginX) / mapWidth);
            float v = Mathf.Clamp01((z - farOriginZ) / mapHeight);
            float fx = u * (farCols - 1);
            float fy = v * (farRows - 1);
            // Smoothly sample UltraFar relief from Far heightmaps with Catmull-Rom too.
            return CatmullRomInterpolateArray(far, fx, fy, farCols, farRows) * verticalSpan - belowSeaSpan;
        };

        float gridStep = ultraFarGridStep;
        float heightOffset = ultraFarHeightOffset;

        Task<UltraFarLandMeshBuilder.MeshData> task = BackgroundWork.Run(() =>
            UltraFarLandMeshBuilder.Build(mapWidth, mapHeight, gridStep, heightOffset, sampleHeight));
        yield return Utils.WaitTask(task);
        if (batch.IsCanceled || epoch != clearEpoch || task.IsFaulted || task.IsCanceled || task.Result.vertices == null) yield break;

        UltraFarLandMeshBuilder.MeshData md = task.Result;
        if (md.vertices.Count < 3)
        {
            // Even without generated triangles, finalize an empty result for this revision rather than retaining old land.
            ultraFarLandObject = null;
            ultraFarLandMesh = null;
            onCompleted();
            yield break;
        }

        // Assemble Meshes on the main thread.
        Mesh mesh = new Mesh { name = "UltraFarLandMesh" };
        if (md.vertices.Count > 65000)
        {
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        }
        mesh.SetVertices(md.vertices);
        if (md.uvs != null && md.uvs.Count == md.vertices.Count)
        {
            mesh.SetUVs(0, md.uvs);
        }
        mesh.SetTriangles(md.triangles, 0);
        // Builders return position-welded smooth normals. Avoid RecalculateNormals, which does not weld duplicate vertices
        // and would create faceted shading at cell boundaries.
        if (md.normals != null && md.normals.Count == md.vertices.Count)
        {
            mesh.SetNormals(md.normals);
        }
        else
        {
            mesh.RecalculateNormals();
        }
        mesh.RecalculateBounds();
        ultraFarLandMesh = mesh;

        GameObject go = new GameObject("UltraFar_Land");
        Transform container = GetContainer("Terrains");
        if (container != null) go.transform.SetParent(container);
        go.layer = LayerMask.NameToLayer("Ground");

        MeshFilter mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        MeshRenderer mr = go.AddComponent<MeshRenderer>();

        EnsureUltraFarLandMaterials();
        mr.sharedMaterial = ultraFarLandMat;

        go.AddComponent<VisualTerrainObject>();

        ultraFarLandObject = go;
        onCompleted();

        Debug.Log($"TerrainManager: Ultra-far land mesh built. verts={md.vertices.Count}, " +
                  $"tris={md.triangles.Count / 3}");
    }

    /// <summary>
    /// Get UltraFar ground textures (same as per-cell tiles) and tiling.
    /// Use customTerrainLayers[0] if available; otherwise use ground-cover textures.
    /// </summary>
    private Texture2D GetUltraFarDiffuseTexture(GroundCoverProfile ground, out Vector2 tileSize)
    {
        Texture2D diffuseTex = ResolveGroundTexture(ground);
        tileSize = ground.textureTileSize;

        if (customTerrainLayers != null && customTerrainLayers.Length > 0 && customTerrainLayers[0] != null)
        {
            diffuseTex = customTerrainLayers[0].diffuseTexture;
            tileSize = customTerrainLayers[0].tileSize;
        }

        if (tileSize.x <= 0f || tileSize.y <= 0f)
        {
            tileSize = new Vector2(terrainSize.x, terrainSize.z);
        }
        return diffuseTex;
    }

    /// <summary>
    /// Create UltraFar land materials as needed.
    /// Custom/DepthOffsetSimpleLitFar (Offset 4,4) renders behind per-cell terrain and sea.
    /// <see cref="ApplyFarTerrainMaterialSettings"/> sets appearance; <see cref="UpdateLoadedCellMask"/> supplies sinking masks.
    /// </summary>
    private void EnsureUltraFarLandMaterials()
    {
        Shader sh = ResolveFarTerrainShader();
        if (ultraFarLandMat == null) ultraFarLandMat = new Material(sh) { name = "UltraFarLand" };
        else ultraFarLandMat.shader = sh;
        ApplyFarTerrainMaterialSettings(ultraFarLandMat);
        // Sink amount in loaded cells. UpdateLoadedCellMask supplies the mask.
        ultraFarLandMat.SetFloat(FarSinkAmountId, ultraFarSinkAmount);
    }

    private Shader ResolveFarTerrainShader()
    {
        Shader sh = ultraFarLandShader;
        if (sh == null) sh = Shader.Find("Custom/DepthOffsetSimpleLitFar");
        if (sh == null)
        {
            Debug.LogWarning("TerrainManager: Custom/DepthOffsetSimpleLitFar shader not found; falling back to URP Simple Lit");
            sh = Shader.Find("Universal Render Pipeline/Simple Lit");
        }
        return sh;
    }

    /// <summary>
    /// Write the same appearance settings as per-cell tiles directly onto Far materials (UltraFar land/outside-city backgrounds).
    /// Apply the same ground textures and tiling using vertex world XZ as UVs.
    /// Share procedural ground-cover colors, rock, detail normals, PBR lighting settings, and HLSL with Terrain.
    /// Procedural color/overlay values are not declared in shader Properties and are lost when cloning through new Material(existing).
    /// Always configure additional Far materials with this function rather than cloning.
    /// </summary>
    private void ApplyFarTerrainMaterialSettings(Material material)
    {
        GroundCoverProfile ground = ActiveGroundCover;
        // Without textures, multiply the default white texture by base color A to match tile solid-color fallbacks.
        Texture2D diffuseTex = GetUltraFarDiffuseTexture(ground, out Vector2 tileSize);
        material.SetTexture("_BaseMap", diffuseTex != null ? diffuseTex : Texture2D.whiteTexture);
        material.SetVector("_BaseMap_ST", new Vector4(1f / tileSize.x, 1f / tileSize.y, 0f, 0f));

        ApplyTerrainMaterialSettings(material);
        TerrainLayer groundLayer = customTerrainLayers != null && customTerrainLayers.Length > 0
            ? customTerrainLayers[0] : null;
        material.SetFloat("_GroundMetallic", groundLayer != null ? groundLayer.metallic : defaultMetallic);
        material.SetFloat("_Smoothness", groundLayer != null ? groundLayer.smoothness : ground.smoothness);
        material.SetColor("_BaseColor", diffuseTex != null ? Color.white : ground.groundColorA);
        material.SetFloat("_FarGridStep", Mathf.Max(1f, ultraFarGridStep));
    }

    /// <summary>
    /// Rebuild the loaded-cell mask for UltraFar sinking from terrainTiles. Called every frame, but create contents only
    /// when terrainTiles changes (loadedCellMaskDirty) or the texture is recreated.
    ///
    /// Mask visible tile cells in R8 to prevent protrusions caused by Far/normal terrain height differences.
    /// Shaders also check one surrounding grid step per vertex and sink only when connected faces
    /// fit inside loaded areas. Keep boundary-crossing faces at original heights, preventing unloaded sides from sinking below sea.
    /// No Far faces exist outside the city, so treat neighbors outside _FarCityRect as boundary cells.
    ///
    /// Texel coordinates use zero-based backend cells. Shaders convert with floor(worldXZ/cellSize)+offset,
    /// using the same integer offset as UnityCellToBackendCell.
    /// Skip GPU uploads when unchanged.
    /// </summary>
    private void UpdateLoadedCellMask()
    {
        // Only UltraFar materials use this mask. Do nothing before settings or materials are ready.
        if (terrainSettings == null || ultraFarLandMat == null) return;

        int w = GetCellsX();
        int h = GetCellsY();
        if (w <= 0 || h <= 0) return;

        // Create textures/buffers (recreate on map size changes)
        if (loadedCellMaskTex == null || loadedCellMaskTex.width != w || loadedCellMaskTex.height != h)
        {
            if (loadedCellMaskTex != null) Destroy(loadedCellMaskTex);
            loadedCellMaskTex = new Texture2D(w, h, TextureFormat.R8, false)
            {
                name = "LoadedCellMask",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            loadedCellMaskBuf = new byte[w * h];
            loadedCellMaskPrevBuf = null; // Always upload initially

            // Bind mask and coordinate conversion parameters to UltraFar materials.
            // params: x = 1/cellSize, y = cell offset X, z = cell offset Y (same as UnityCellToBackendCell)
            // texel: x = 1/cell count X, y = 1/cell count Y
            float cs = GetCellSize();
            int offsetX = Mathf.RoundToInt((GetMapWidth() / 2.0f) / cs);
            int offsetY = Mathf.RoundToInt((GetMapHeight() / 2.0f) / cs);
            Vector4 maskParams = new Vector4(1f / cs, offsetX, offsetY, 0f);
            Vector4 maskTexel = new Vector4(1f / w, 1f / h, 0f, 0f);

            ultraFarLandMat.SetTexture(LoadedCellMaskId, loadedCellMaskTex);
            ultraFarLandMat.SetVector(LoadedCellMaskParamsId, maskParams);
            ultraFarLandMat.SetVector(LoadedCellMaskTexelId, maskTexel);
            ultraFarLandMat.SetVector(FarCityRectId, new Vector4(
                -GetMapWidth() * 0.5f, -GetMapHeight() * 0.5f, GetMapWidth() * 0.5f, GetMapHeight() * 0.5f));
            loadedCellMaskDirty = true;
        }
        if (!loadedCellMaskDirty) return;
        loadedCellMaskDirty = false;

        // Write currently loaded (visible) tiles into the mask.
        // Old tiles remain registered in terrainTiles and visible during tier changes/refreshes,
        // naturally staying masked until replacement finishes, preventing UltraFar meshes from rising through them.
        // Hidden LRU tiles are absent from terrainTiles and excluded.
        Array.Clear(loadedCellMaskBuf, 0, loadedCellMaskBuf.Length);
        foreach (var kvp in terrainTiles)
        {
            if (kvp.Value == null) continue;
            Vector2Int backendCell = UnityCellToBackendCell(kvp.Key);
            if (backendCell.x < 0 || backendCell.x >= w || backendCell.y < 0 || backendCell.y >= h) continue;
            loadedCellMaskBuf[backendCell.y * w + backendCell.x] = 255;
        }

        // Skip GPU upload when unchanged.
        if (loadedCellMaskPrevBuf != null && ((ReadOnlySpan<byte>)loadedCellMaskBuf).SequenceEqual(loadedCellMaskPrevBuf))
        {
            return;
        }

        loadedCellMaskTex.SetPixelData(loadedCellMaskBuf, 0);
        loadedCellMaskTex.Apply(false, false);

        if (loadedCellMaskPrevBuf == null) loadedCellMaskPrevBuf = new byte[loadedCellMaskBuf.Length];
        Buffer.BlockCopy(loadedCellMaskBuf, 0, loadedCellMaskPrevBuf, 0, loadedCellMaskBuf.Length);
    }

    // ================================================================
    // Terrain tile heightmap streaming application
    // ================================================================

    /// <summary>
    /// Issue a heightmap application request and wait for completion (discard or publication plus OnTerrainTileReady notification).
    /// <see cref="HeightStreamWorker"/> applies sequentially with view priority
    /// and frame budgets. Replace any unstarted request for the same cell with current data
    /// (the replaced request completes as discarded).
    /// </summary>
    private IEnumerator EnqueueAndWait(Vector2Int cellIndex, HeightStreamRequest request)
    {
        if (pendingStreamRequests.TryGetValue(cellIndex, out HeightStreamRequest old)) DiscardRequest(old);
        pendingStreamRequests[cellIndex] = request;
        EnsureStreamWorker();
        yield return new WaitUntil(() => request.done && (!request.succeeded || request.notified));
    }

    private void EnsureStreamWorker()
    {
        if (streamWorkerCoroutine == null)
        {
            streamWorkerCoroutine = RenderWorkScheduler.Start(this, HeightStreamWorker(), "TerrainManager", () => pendingStreamRequests.Count > 0, RenderWorkPriority.High);
        }
    }

    /// <summary>
    /// Select the next cell to apply from waiting requests.
    /// Prioritize camera-view cells plus margin, then camera distance.
    /// Requests number only the loaded cells (at most a few dozen), so a linear scan is inexpensive.
    /// </summary>
    private Vector2Int PickBestStreamCell()
    {
        GetStreamViewpoint(out Vector3 camPos, out Vector3 fwd);
        Vector2Int best = default;
        float bestScore = float.MaxValue;
        foreach (var kvp in pendingStreamRequests)
        {
            float score = ScoreStreamCell(kvp.Key, camPos, fwd);
            if (score < bestScore)
            {
                bestScore = score;
                best = kvp.Key;
            }
        }
        return best;
    }

    /// <summary>Camera position/horizontal forward used for view prioritization. Without a camera, assume origin facing +Z.</summary>
    private static void GetStreamViewpoint(out Vector3 camPos, out Vector3 fwd)
    {
        Camera cam = Camera.main;
        camPos = cam != null ? cam.transform.position : Vector3.zero;
        fwd = cam != null ? cam.transform.forward : Vector3.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        else fwd.Normalize();
    }

    /// <summary>
    /// Streaming priority score (lower is earlier).
    /// Use nearest horizontal distance from camera to cell rectangle; add a large penalty
    /// outside STREAM_VIEW_HALF_ANGLE_DEG from camera forward. Prevent terrain directly below or ahead
    /// from arriving last, and immediately prioritize newly visible cells when turning.
    /// </summary>
    private float ScoreStreamCell(Vector2Int cell, Vector3 camPos, Vector3 camForwardHorizontal)
    {
        float cellSize = CellSize;
        float closestX = Mathf.Clamp(camPos.x, cell.x * cellSize, (cell.x + 1) * cellSize);
        float closestZ = Mathf.Clamp(camPos.z, cell.y * cellSize, (cell.y + 1) * cellSize);
        Vector3 toCell = new Vector3(closestX - camPos.x, 0f, closestZ - camPos.z);
        float dist = toCell.magnitude;

        // Always treat the cell beneath the camera as in view; use horizontal angle for other cells.
        if (dist > 1f)
        {
            float angle = Vector3.Angle(camForwardHorizontal, toCell);
            if (angle > STREAM_VIEW_HALF_ANGLE_DEG)
            {
                dist += 100000f;
            }
        }
        return dist;
    }

    /// <summary>
    /// Write rows to TerrainData independent of visible terrain and publish only when complete.
    /// On full clears/job cancellation, discard candidates without changing visible terrain to incomplete heights.
    /// Separate indivisible SyncHeightmap and publication into steps,
    /// waiting for mostly unused shared budget (up to FRESH_BUDGET_MAX_WAIT_FRAMES), then starting in the same step as the wait.
    /// Record revisions, loadedCells, updated cells, and done in the publication step, letting subscribers read new revisions during notifications.
    /// Pass OnTerrainTileReady to <see cref="TileNotifyWorker"/> and advance to the next tile without waiting.
    /// Direct test requests (job==null) use dictionary-key cells and cannot be canceled.
    /// </summary>
    private IEnumerator HeightStreamWorker()
    {
        yield return null;
        HeightStreamRequest active = null;
        TerrainData staged = null;
        try
        {
            while (true)
            {
                if (pendingStreamRequests.Count == 0) { yield return null; continue; }
                Vector2Int cell = PickBestStreamCell();
                active = pendingStreamRequests[cell];
                pendingStreamRequests.Remove(cell);
                // Return fetch slots when publication begins, allowing next-tile fetching during publication.
                ReleaseTileSlot(active.job);
                int epoch = clearEpoch;
                bool canceled = IsRequestCanceled(active, epoch);
                Terrain published = null;
                if (active.restoreTile != null)
                {
                    if (!canceled)
                    {
                        for (int wait = 0; wait < FRESH_BUDGET_MAX_WAIT_FRAMES && !HasFreshBudget(); wait++) yield return null;
                        if (!IsRequestCanceled(active, epoch))
                        {
                            try
                            {
                                published = RestoreCachedTile(cell, active);
                                active.succeeded = published != null;
                            }
                            catch (Exception ex) { Debug.LogException(ex); }
                        }
                    }
                }
                else if (!canceled && active.heights != null)
                {
                    int res = active.heights.GetLength(0);
                    staged = new TerrainData { heightmapResolution = res, alphamapResolution = 16, size = terrainSize };
                    for (int row = 0; row < res; row += STREAM_BAND_ROWS)
                    {
                        canceled = IsRequestCanceled(active, epoch);
                        if (canceled) break;
                        try { ApplyHeightBand(staged, active.heights, res, row, Mathf.Min(STREAM_BAND_ROWS, res-row)); }
                        catch (Exception ex) { Debug.LogException(ex); canceled = true; break; }
                        yield return RenderWorkScheduler.Continue;
                    }
                    ReturnHeightBuffer(active.heights);
                    active.heights = null;
                    if (!canceled)
                    {
                        for (int wait = 0; wait < FRESH_BUDGET_MAX_WAIT_FRAMES && !HasFreshBudget(); wait++) yield return null;
                        canceled = IsRequestCanceled(active, epoch);
                        if (!canceled)
                        {
                            try { staged.SyncHeightmap(); }
                            catch (Exception ex) { Debug.LogException(ex); canceled = true; }
                            yield return RenderWorkScheduler.Continue;
                        }
                    }
                    if (!canceled)
                    {
                        for (int wait = 0; wait < FRESH_BUDGET_MAX_WAIT_FRAMES && !HasFreshBudget(); wait++) yield return null;
                        if (!IsRequestCanceled(active, epoch))
                        {
                            try
                            {
                                // Swap rendering and grounding references within the same frame. Never publish unfinished rows.
                                AttachTerrainTile(cell, staged);
                                staged = null;
                                published = terrainTiles[cell];
                                dynamicCellVersions[cell] = active.version;
                                loadedCells.Add(cell);
                                updatedTerrainCellsThisLoad.Add(cell);
                                active.succeeded = true;
                            }
                            catch (Exception ex) { Debug.LogException(ex); }
                        }
                    }
                }
                if (staged != null) { Destroy(staged); staged = null; }
                ReleaseRequestResources(active);
                active.done = true;
                if (published != null) EnqueueTileNotification(new TileNotification { cell = cell, tile = published, epoch = epoch, request = active });
                else active.notified = true;
                active = null;
                yield return RenderWorkScheduler.Continue;
            }
        }
        finally
        {
            if (staged != null) Destroy(staged);
            if (active != null)
            {
                ReturnHeightBuffer(active.heights);
                active.heights = null;
                if (active.restoreTile != null) DestroyTerrainTileObject(active.restoreTile.gameObject);
                active.restoreTile = null;
                active.done = true;
                active.notified = true;
            }
            streamWorkerCoroutine = null;
        }
    }

    /// <summary>
    /// Queue published tile OnTerrainTileReady notifications and wake the notification worker.
    /// If inactive and unable to start workers (direct test driving), notify immediately.
    /// </summary>
    private void EnqueueTileNotification(TileNotification notification)
    {
        pendingTileNotifications.Enqueue(notification);
        if (notifyWorkerCoroutine == null && isActiveAndEnabled)
            notifyWorkerCoroutine = RenderWorkScheduler.Start(this, TileNotifyWorker(), "TerrainManager", () => pendingTileNotifications.Count > 0, RenderWorkPriority.High);
        if (notifyWorkerCoroutine == null)
            while (pendingTileNotifications.Count > 0) DispatchTileNotification(pendingTileNotifications.Dequeue());
    }

    /// <summary>
    /// Notify pending entries in publication order after waiting for mostly unused shared budget (up to FRESH_BUDGET_MAX_WAIT_FRAMES).
    /// Dequeue and notify without intervening waits, retaining no notifications across yields. Exit when empty.
    /// </summary>
    private IEnumerator TileNotifyWorker()
    {
        try
        {
            while (pendingTileNotifications.Count > 0)
            {
                for (int wait = 0; wait < FRESH_BUDGET_MAX_WAIT_FRAMES && !HasFreshBudget(); wait++) yield return null;
                if (pendingTileNotifications.Count == 0) break;
                DispatchTileNotification(pendingTileNotifications.Dequeue());
                yield return RenderWorkScheduler.Continue;
            }
        }
        finally
        {
            notifyWorkerCoroutine = null;
        }
    }

    private void DispatchTileNotification(TileNotification notification)
    {
        // If full clear, unload, or replacement happened while waiting, this tile is no longer visible.
        if (notification.epoch == clearEpoch && terrainTiles.TryGetValue(notification.cell, out Terrain current) && current == notification.tile)
        {
            try { OnTerrainTileReady?.Invoke(notification.cell, notification.tile); }
            catch (Exception ex) { Debug.LogException(ex); }
        }
        notification.request.notified = true;
    }

    /// <summary>
    /// Apply heightmap rows [rowStart, rowStart+rows) to TerrainData using partial SetHeights.
    /// float[,] is contiguous row-major memory, so one BlockCopy extracts the rows.
    /// Use reusable streamBandScratch for standard bands to avoid GC allocations.
    /// </summary>
    private void ApplyHeightBand(TerrainData tData, float[,] heights, int res, int rowStart, int rows)
    {
        float[,] strip;
        if (rows == STREAM_BAND_ROWS && res == TILE_RESOLUTION)
        {
            if (streamBandScratch == null)
            {
                streamBandScratch = new float[STREAM_BAND_ROWS, TILE_RESOLUTION];
            }
            strip = streamBandScratch;
        }
        else
        {
            strip = new float[rows, res];
        }

        Buffer.BlockCopy(heights, rowStart * res * sizeof(float), strip, 0, rows * res * sizeof(float));
        tData.SetHeightsDelayLOD(0, rowStart, strip);
        Utils.InvalidateHeightCache();
    }

    /// <summary>
    /// Create GameObjects from streamed TerrainData and register cell tiles.
    /// Replace/destroy old tiles in the same frame to avoid flicker.
    /// Tile creation (including initial TerrainCollider baking) is expensive and indivisible,
    /// so HeightStreamWorker must wait for shared budget availability before calling.
    /// </summary>
    private void AttachTerrainTile(Vector2Int cellIndex, TerrainData tData)
    {
        Terrain terrainComp = CreateTerrainTile(cellIndex, tData);
        GameObject terrainGO = terrainComp.gameObject;

        // Old-to-new tile replacement, essential to avoiding flicker.
        // Register and destroy in the same frame without yielding. Unity Destroy applies before rendering,
        // so no frame renders old and new tiles together,
        // and no frame has neither tile.
        terrainTiles.TryGetValue(cellIndex, out Terrain prevTerrain);
        terrainTiles[cellIndex] = terrainComp;
        loadedCellMaskDirty = true;
        Utils.InvalidateHeightCache();

        // Destroy the previously registered old tile, guarding against the same GameObject as the new tile.
        GameObject oldTerrainToDestroy = null;
        if (prevTerrain != null && prevTerrain.gameObject != terrainGO)
        {
            oldTerrainToDestroy = prevTerrain.gameObject;
        }
        // Discard retained LRU tiles for the same cell, prioritizing newly received data.
        DropCachedTile(cellIndex, oldTerrainToDestroy);
        if (oldTerrainToDestroy != null)
        {
            DestroyTerrainTileObject(oldTerrainToDestroy);
        }

        // Reconnect neighboring tile LOD seams when new tiles appear.
        // All tiles share resolution, so SetNeighbors works across every boundary.
        UpdateNeighbors(cellIndex);
    }

    /// <summary>
    /// Create a cell tile GameObject from completed TerrainData and configure rendering/input. Callers register visibility
    /// (<see cref="AttachTerrainTile"/> for publication; prefetch retains hidden tiles in LRU).
    /// Terrain.CreateTerrainGameObject already sets TerrainCollider.terrainData; do not reassign.
    /// </summary>
    private Terrain CreateTerrainTile(Vector2Int cellIndex, TerrainData tData)
    {
        GameObject terrainGO = Terrain.CreateTerrainGameObject(tData);
        Transform container = GetContainer("Terrains");
        if (container != null) terrainGO.transform.SetParent(container);

        terrainGO.name = $"Terrain_{cellIndex.x}_{cellIndex.y}";
        terrainGO.layer = LayerMask.NameToLayer("Ground");

        // Lower base Y by SeaFloorDepthSpan. Heightmap normalization includes the same offset,
        // so worldY = base Y + normalized×terrainSize.y = actual elevation. Sea level (elevation 0)
        // renders at worldY=0, seabeds at worldY<0 (down to -SeaFloorDepthSpan).
        Vector3 pos = new Vector3(cellIndex.x * terrainSize.x, -SeaFloorDepthSpan, cellIndex.y * terrainSize.z);
        terrainGO.transform.position = pos;

        Terrain terrainComp = terrainGO.GetComponent<Terrain>();
        terrainComp.drawInstanced = false;
        terrainComp.heightmapPixelError = LocalConstants.TERRAIN_HEIGHTMAP_PIXEL_ERROR;
        terrainComp.detailObjectDistance = 1000f;
        terrainComp.detailObjectDensity = 1f;
        terrainComp.treeBillboardDistance = 1000f;
        terrainComp.treeDistance = 1000f;
        terrainComp.treeCrossFadeLength = 200f;
        // Effectively disable basemap switching. At the default 1000m, distant tiles
        // use basemap shaders instead of custom shaders, losing macro variation
        // and depth Offset on distant terrain.
        terrainComp.basemapDistance = 100000f;

        ApplyTerrainMaterial(terrainComp, tData);

        terrainGO.AddComponent<TerrainInputHandler>();

        var vmo = terrainGO.AddComponent<VisualModeObject>();
        vmo.objectType = InputObjectType.TERRAIN;
        terrainGO.AddComponent<VisualTerrainObject>();
        return terrainComp;
    }

    /// <summary>
    /// Reshow an LRU tile and record revision, loadedCells, and updated cells in the same step.
    /// Destroy another tile currently displayed in the cell. Return null if destroyed while retained (caller proceeds to fetch).
    /// As with new publication, the caller notifies in the next step.
    /// </summary>
    private Terrain RestoreCachedTile(Vector2Int cellIndex, HeightStreamRequest request)
    {
        Terrain tile = request.restoreTile;
        request.restoreTile = null;
        if (tile == null) return null;
        if (terrainTiles.TryGetValue(cellIndex, out Terrain previous) && previous != null && previous != tile)
            DestroyTerrainTileObject(previous.gameObject);
        tile.gameObject.SetActive(true);
        terrainTiles[cellIndex] = tile;
        loadedCellMaskDirty = true;
        dynamicCellVersions[cellIndex] = request.version;
        loadedCells.Add(cellIndex);
        updatedTerrainCellsThisLoad.Add(cellIndex);
        Utils.InvalidateHeightCache();
        UpdateNeighbors(cellIndex);
        return tile;
    }

    /// <summary>
    /// Build heightmaps from received binary on a background thread for expensive CPU processing.
    /// Pass snapshots of required values to avoid Unity APIs or MonoBehaviour fields.
    /// </summary>
    private Task<float[,]> ProcessHeightmapAsync(byte[] rawBytes, int gridRows, int gridCols, int targetResolution)
    {
        // Capture required values on the main thread before dispatching to another thread.
        float verticalSpan = terrainSize.y;
        float belowSeaSpan = SeaFloorDepthSpan;

        return BackgroundWork.Run(() =>
        {
            float[,] heights = ConvertFloat16BinaryToHeights(rawBytes, gridRows, gridCols, verticalSpan, belowSeaSpan);
            return (gridRows == targetResolution && gridCols == targetResolution)
                ? heights
                : InterpolateHeightmapArray(heights, gridRows, gridCols, targetResolution);
        });
    }

    // ================================================================
    // Interpolation methods
    // ================================================================

    private static float[,] InterpolateHeightmapArray(float[,] source, int srcRows, int srcCols, int targetResolution)
    {
        float[,] result = new float[targetResolution, targetResolution];
        float denom = targetResolution > 1 ? (targetResolution - 1) : 1;
        for (int y = 0; y < targetResolution; y++)
        {
            for (int x = 0; x < targetResolution; x++)
            {
                float ny = y / denom;
                float nx = x / denom;
                float sy = ny * (srcRows - 1);
                float sx = nx * (srcCols - 1);
                // Smooth Near/Far heightmap resolution conversion with Catmull-Rom too.
                // (Interpolation passes through original grid values, preserving seams)
                result[y, x] = CatmullRomInterpolateArray(source, sx, sy, srcCols, srcRows);
            }
        }
        return result;
    }

    /// <summary>
    /// 2D smooth interpolation using cubic Catmull-Rom. Used to upscale normalized [0,1] heightmaps
    /// (Far 65 and coarse UltraFar grids into high-resolution tiles).
    ///
    /// Bilinear interpolation is C0-continuous (values connect but slopes break), producing facets at grid boundaries
    /// when low-resolution heightmaps are enlarged, making distant terrain look angular/low-poly.
    /// Catmull-Rom is C1-continuous with smooth slopes, greatly reducing facets.
    ///
    /// Interpolation always passes through control points (original grid points), matching original samples
    /// at those world coordinates. Adjacent tiles sample adjoining areas of the same source heightmap,
    /// converging to identical boundary values without steps (no edge height blending).
    ///
    /// Catmull-Rom may overshoot slightly on steep slopes, so clamp normalized values to [0,1]
    /// consistent with ConvertFloat16BinaryToHeights Clamp01.
    /// </summary>
    private static float CatmullRomInterpolateArray(float[,] h, float x, float y, int width, int height)
    {
        if (x < 0f) x = 0f; else if (x > width - 1) x = width - 1;
        if (y < 0f) y = 0f; else if (y > height - 1) y = height - 1;

        int xi = (int)Math.Floor(x);
        int yi = (int)Math.Floor(y);
        float fx = x - xi;
        float fy = y - yi;

        // Interpolate four rows in X with Catmull-Rom, then interpolate those four values in Y.
        float r0 = CatmullRomRow(h, xi, yi - 1, fx, width, height);
        float r1 = CatmullRomRow(h, xi, yi, fx, width, height);
        float r2 = CatmullRomRow(h, xi, yi + 1, fx, width, height);
        float r3 = CatmullRomRow(h, xi, yi + 2, fx, width, height);

        float v = CatmullRom1D(r0, r1, r2, r3, fy);
        return v < 0f ? 0f : (v > 1f ? 1f : v);
    }

    private static float CatmullRomRow(float[,] h, int xi, int row, float fx, int width, int height)
    {
        float p0 = SampleClampedArray(h, xi - 1, row, width, height);
        float p1 = SampleClampedArray(h, xi, row, width, height);
        float p2 = SampleClampedArray(h, xi + 1, row, width, height);
        float p3 = SampleClampedArray(h, xi + 2, row, width, height);
        return CatmullRom1D(p0, p1, p2, p3, fx);
    }

    private static float CatmullRom1D(float p0, float p1, float p2, float p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        // Standard Catmull-Rom basis (tension 0.5). Passes through p1 at t=0 and p2 at t=1.
        return 0.5f * (
            (2f * p1) +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    private static float SampleClampedArray(float[,] h, int x, int y, int width, int height)
    {
        if (x < 0) x = 0; else if (x > width - 1) x = width - 1;
        if (y < 0) y = 0; else if (y > height - 1) y = height - 1;
        return h[y, x];
    }

    // ================================================================
    // Public methods
    // ================================================================

    /// <summary>Whether city terrain settings were received. GetMapWidth / GetMapHeight return provisional values until then.</summary>
    public bool HasTerrainSettings => terrainSettings != null;
    public int GetMapWidth() => terrainSettings?.map_width ?? 10000;
    public int GetMapHeight() => terrainSettings?.map_height ?? 10000;

    /// <summary>
    /// Loaded (visible) terrain tiles, keyed by Unity cell index.
    /// HeightmapTextureManager uses these as moving heightmap window sources.
    /// Finish enumeration within one frame without yielding to avoid conflicts with loading.
    /// </summary>
    public System.Collections.Generic.IReadOnlyDictionary<Vector2Int, Terrain> LoadedTerrainTiles => terrainTiles;

    public int GetCellsX() => terrainSettings != null ? terrainSettings.map_width / terrainSettings.cell_size : 20;
    public int GetCellsY() => terrainSettings != null ? terrainSettings.map_height / terrainSettings.cell_size : 20;
    public float GetCellSize() => terrainSettings?.cell_size ?? CellSize;

    // ================================================================
    // Cell coordinate conversion (Unity ↔ backend)
    // ================================================================

    /// <summary>
    /// Convert Unity cell indices to backend zero-based indices.
    /// The backend converts world coordinates centered on the map
    /// into nonnegative cell indices referenced to the array's upper-left corner.
    /// Return the backend cell corresponding to the Unity cell center.
    /// </summary>
    public Vector2Int UnityCellToBackendCell(Vector2Int unityCell)
    {
        // Map maximum width/height are even multiples of cell size (no odd-cell center splitting),
        // allowing safe one-to-one integer offset mapping without floating-point rounding errors.
        int offsetX = Mathf.RoundToInt((GetMapWidth() / 2.0f) / GetCellSize());
        int offsetY = Mathf.RoundToInt((GetMapHeight() / 2.0f) / GetCellSize());
        return new Vector2Int(unityCell.x + offsetX, unityCell.y + offsetY);
    }

    /// <summary>
    /// Convert backend zero-based indices to Unity cell indices.
    /// Map width/height are even multiples of cell size, so backend and Unity cells
    /// correspond one-to-one; always return exactly one cell.
    /// </summary>
    public IEnumerable<Vector2Int> BackendCellToUnityCells(Vector2Int backendCell)
    {
        // Use map dimensions being even multiples of cell size
        // to eliminate floating-point boundary checks splitting one backend cell into multiple Unity cells (3x3).
        // Return the unique matching Unity cell by subtracting integer offsets from nonnegative backend coordinates.
        int offsetX = Mathf.RoundToInt((GetMapWidth() / 2.0f) / GetCellSize());
        int offsetY = Mathf.RoundToInt((GetMapHeight() / 2.0f) / GetCellSize());
        yield return new Vector2Int(backendCell.x - offsetX, backendCell.y - offsetY);
    }

    /// <summary>
    /// Revision advanced whenever <see cref="FetchCoastline"/> finishes fetching coastlines, lakes, rivers, and reclaimed land.
    /// <see cref="CoastlineStore.Version"/> advances after coastlines alone, so callers requiring completed quays
    /// such as incoming ship docking positions use this instead.
    /// </summary>
    public static int ShoreDataVersion { get; private set; }

    /// <summary>
    /// Fetch coastline (land boundary polygons) into <see cref="CoastlineStore"/>, followed by lakes, rivers, and reclaimed land.
    /// GeoJSON-like response (Polygon / MultiPolygon), using world coordinates.
    /// Completed reclamation expands coastlines; refetch every session:reload as well as initial/forced loads.
    /// </summary>
    public IEnumerator FetchCoastline()
    {
        // Callers (session start/full reload) calculate LOD resident cell sets immediately afterward.
        // This depends on map dimensions/cell size; defaults could request cells outside the map,
        // so finalize terrain settings here.
        yield return EnsureSettingsLoaded();
        if (!settingsLoaded)
        {
            Debug.LogError("TerrainManager: 地形設定を取得できないため海岸線の取得を中断します。");
            yield break;
        }

        // Finalize ground cover before tile/UltraFar material generation (subsequent loading renders using this value).
        yield return FetchCityGroundCover();

        string error = null;

        yield return ApiClient.Get<CoastlineResponse>(
            "/terrain/coastline",
            response =>
            {
                CoastlineStore.SetFromGeoJson(response?.coordinates);
            },
            err => { error = err; }
        );

        if (!string.IsNullOrEmpty(error))
        {
            Debug.LogError($"TerrainManager: Failed to fetch coastline: {error}");
            CoastlineStore.Clear();
        }
        else
        {
            Debug.Log($"TerrainManager: Coastline loaded (hasCoastline={CoastlineStore.HasCoastline}).");
        }
        // Build the lowland rock coast mask in the background while the remaining shore data is fetched.
        Coroutine coastMask = StartCoroutine(BuildCliffCoastMask());

        // Fetch and generate non-sea water surfaces such as lakes alongside coastlines.
        // WaterSurfaceManager creates itself in AfterSceneLoad, so Instance normally exists.
        if (WaterSurfaceManager.Instance != null)
        {
            yield return WaterSurfaceManager.Instance.LoadAndBuild();
        }
        // Fetch rivers (area polygons and water elevation profiles) too, building RiverStore and water meshes.
        // RoadPlanManager river-crossing checks read RiverStore, so this must precede road editing.
        if (RiverSurfaceManager.Instance != null)
        {
            yield return RiverSurfaceManager.Instance.LoadAndBuild();
        }
        // Reclaimed land (quay crests/faces). Grounding (Utils.GetGroundHeight) returns crests inside bands, and ports query quay sections,
        // so prepare ReclaimedLandStore before parcel/building loads. Skip rebuilding unchanged contents.
        if (ReclaimedLandManager.Instance != null)
        {
            yield return ReclaimedLandManager.Instance.LoadAndBuild();
        }
        yield return coastMask;
        ShoreDataVersion++;
    }

    /// <summary>
    /// Publish the coastline distance mask read by lowland rock (Assets/Shaders/TerrainCliffRock.hlsl) over the city map.
    /// Texels hold inland distance from the coastline divided by cliffLowlandCoastDistance (0 at the coastline and at sea, 1 beyond it).
    /// Texels span half that distance, enough for the linear fade under bilinear filtering.
    /// Without a coastline, clear the mask so lowland rock depends on elevation alone.
    /// </summary>
    private IEnumerator BuildCliffCoastMask()
    {
        if (!CoastlineStore.HasCoastline)
        {
            ClearCliffCoastMask();
            yield break;
        }
        float width = GetMapWidth();
        float height = GetMapHeight();
        float range = Mathf.Max(1f, cliffLowlandCoastDistance);
        float texel = Mathf.Max(range * 0.5f, Mathf.Max(width, height) / 4096f);
        int nx = Mathf.CeilToInt(width / texel);
        int nz = Mathf.CeilToInt(height / texel);
        var xs = new float[nx];
        var zs = new float[nz];
        for (int i = 0; i < nx; i++) xs[i] = -width * 0.5f + (i + 0.5f) * width / nx;
        for (int j = 0; j < nz; j++) zs[j] = -height * 0.5f + (j + 0.5f) * height / nz;

        int version = CoastlineStore.Version;
        Task<byte[]> task = BackgroundWork.Run(() =>
        {
            float[,] signed = CoastlineStore.SampleSignedDistanceGrid(xs, zs, range);
            var data = new byte[nx * nz];
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                    data[j * nx + i] = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(signed[i, j] / range));
            return data;
        });
        yield return Utils.WaitTask(task);
        if (task.IsFaulted || task.IsCanceled || version != CoastlineStore.Version) yield break;

        var mask = new Texture2D(nx, nz, TextureFormat.R8, false, true) { name = "CliffCoastMask" };
        mask.SetPixelData(task.Result, 0);
        mask.Apply(false, true);
        ClearCliffCoastMask();
        cliffCoastMask = mask;
        Shader.SetGlobalTexture(CliffCoastMaskId, mask);
        Shader.SetGlobalVector(CliffCoastMaskRectId, new Vector4(-width * 0.5f, -height * 0.5f, 1f / width, 1f / height));
    }

    /// <summary>Discard the coast mask. The shader treats a zero rect as no mask.</summary>
    private void ClearCliffCoastMask()
    {
        Shader.SetGlobalVector(CliffCoastMaskRectId, Vector4.zero);
        if (cliffCoastMask != null) Destroy(cliffCoastMask);
        cliffCoastMask = null;
    }

    /// <summary>
    /// Fetch city ground cover from GET /api/v1/city and apply changes to visible terrain materials.
    /// On failure keep the previous cover type (appearance only, so do not block terrain loading).
    /// </summary>
    private IEnumerator FetchCityGroundCover()
    {
        int groundCover = cityGroundCover;
        yield return ApiClient.Get<CityGroundCoverEnvelope>(
            "/city/",
            response => { if (response?.city != null) groundCover = response.city.ground_cover; },
            err => Debug.LogWarning($"TerrainManager: 地表種別の取得に失敗したため前回の種別で描きます: {err}")
        );
        if (groundCover == cityGroundCover) yield break;
        cityGroundCover = groundCover;
        RefreshGroundCoverMaterials();
    }

    /// <summary>GET /api/v1/city response reading only ground cover type.</summary>
    [System.Serializable]
    private class CityGroundCoverEnvelope
    {
        public CityGroundCoverFields city;
    }

    [System.Serializable]
    private class CityGroundCoverFields
    {
        public int ground_cover = Constants.GROUND_COVER_GRASSLAND;
    }

    /// <summary>
    /// Coastline endpoint response (GeoJSON-like).
    /// coordinates nesting differs between Polygon and MultiPolygon,
    /// so receive raw JToken here and delegate interpretation to <see cref="CoastlineStore.SetFromGeoJson"/>.
    /// </summary>
    [System.Serializable]
    private class CoastlineResponse
    {
        public string type;
        public Newtonsoft.Json.Linq.JToken coordinates;
    }

    /// <summary>
    /// Get an approximate height range (min, max) for the specified cell from Far heightmaps.
    /// Return false if farTerrainHeights is unloaded.
    /// Used for nearest 3D camera distance during cell tier evaluation.
    /// </summary>
    public bool TryGetCellHeightRange(Vector2Int cellIndex, out float minY, out float maxY)
    {
        if (!IsCityCell(cellIndex) && TryGetBackgroundHeightRange(cellIndex, out minY, out maxY)) return true;
        minY = -SeaFloorDepthSpan;
        maxY = terrainSize.y - SeaFloorDepthSpan;

        if (farTerrainHeights == null || terrainSettings == null) return false;

        float cellSize = terrainSettings.cell_size;
        float mapWidth = terrainSettings.map_width;
        float mapHeight = terrainSettings.map_height;

        float cellMinX = cellIndex.x * cellSize;
        float cellMaxX = (cellIndex.x + 1) * cellSize;
        float cellMinZ = cellIndex.y * cellSize;
        float cellMaxZ = (cellIndex.y + 1) * cellSize;

        float farOriginX = -mapWidth / 2f;
        float farOriginZ = -mapHeight / 2f;

        float uMin = (cellMinX - farOriginX) / mapWidth;
        float uMax = (cellMaxX - farOriginX) / mapWidth;
        float vMin = (cellMinZ - farOriginZ) / mapHeight;
        float vMax = (cellMaxZ - farOriginZ) / mapHeight;

        int farCols = farTerrainHeights.GetLength(1);
        int farRows = farTerrainHeights.GetLength(0);

        int sx = Mathf.FloorToInt(uMin * (farCols - 1));
        int ex = Mathf.CeilToInt(uMax * (farCols - 1));
        int sy = Mathf.FloorToInt(vMin * (farRows - 1));
        int ey = Mathf.CeilToInt(vMax * (farRows - 1));

        sx = Mathf.Clamp(sx, 0, farCols - 1);
        ex = Mathf.Clamp(ex, 0, farCols - 1);
        sy = Mathf.Clamp(sy, 0, farRows - 1);
        ey = Mathf.Clamp(ey, 0, farRows - 1);

        minY = float.MaxValue;
        maxY = float.MinValue;

        for (int y = sy; y <= ey; y++)
        {
            for (int x = sx; x <= ex; x++)
            {
                float h = farTerrainHeights[y, x] * terrainSize.y - SeaFloorDepthSpan;
                if (h < minY) minY = h;
                if (h > maxY) maxY = h;
            }
        }

        return true;
    }

    /// <summary>Background heights for long roads crossing nonresident areas. Reference the same overview surface, including edits.</summary>
    public bool TrySampleOverview(Vector3 position, out float height)
    {
        height = 0;
        if (farTerrainHeights == null || terrainSettings == null) return false;
        float u = (position.x + GetMapWidth()*.5f)/GetMapWidth();
        float v = (position.z + GetMapHeight()*.5f)/GetMapHeight();
        if (u < 0 || u > 1 || v < 0 || v > 1) return TrySampleBackground(position, out height);
        int cols = farTerrainHeights.GetLength(1), rows = farTerrainHeights.GetLength(0);
        height = CatmullRomInterpolateArray(farTerrainHeights, u*(cols-1), v*(rows-1), cols, rows) * terrainSize.y - SeaFloorDepthSpan;
        return true;
    }

    /// <summary>
    /// Visible tile containing the coordinates. Points exactly on the cell's negative edge also lie on the neighboring tile edge,
    /// so check that neighbor if this cell is nonresident (prevent boundary grid points from falling to overview heights).
    /// </summary>
    public Terrain GetTerrainAtPosition(Vector3 position)
    {
        int cx = Mathf.FloorToInt(position.x / terrainSize.x);
        int cz = Mathf.FloorToInt(position.z / terrainSize.z);
        if (TryGetVisibleTile(cx, cz, out Terrain terrain)) return terrain;
        bool onMinX = cx * terrainSize.x == position.x;
        bool onMinZ = cz * terrainSize.z == position.z;
        if (onMinX && TryGetVisibleTile(cx - 1, cz, out terrain)) return terrain;
        if (onMinZ && TryGetVisibleTile(cx, cz - 1, out terrain)) return terrain;
        if (onMinX && onMinZ && TryGetVisibleTile(cx - 1, cz - 1, out terrain)) return terrain;
        return null;
    }

    private bool TryGetVisibleTile(int cx, int cz, out Terrain terrain) =>
        terrainTiles.TryGetValue(new Vector2Int(cx, cz), out terrain) && terrain != null && terrain.gameObject.activeSelf;

    private bool showContourLines = false;

    /// <summary>
    /// Toggle contour visibility.
    /// Update global shader variables to apply immediately to every terrain tile.
    /// </summary>
    public void SetContourLinesVisible(bool visible)
    {
        showContourLines = visible;
        Shader.SetGlobalFloat("_ShowContour", visible ? 1f : 0f);
        Debug.Log($"TerrainManager: Contour lines visibility set to {visible}");
    }

    // ================================================================
    // Adjacent tile management
    // ================================================================

    /// <summary>
    /// <see cref="Terrain.SetNeighbors"/> requires matching heightmapResolution between adjacent Terrains.
    /// All tiles currently use TILE_RESOLUTION, so they always match,
    /// but retain checks against unexpected mismatches such as legacy tiles
    /// (leave mismatched boundaries disconnected). SetNeighbors only handles LOD seams, never changes heights.
    /// </summary>
    private static Terrain NeighborIfSameResolution(Terrain self, Terrain candidate)
    {
        if (self == null || candidate == null) return null;
        TerrainData sd = self.terrainData;
        TerrainData nd = candidate.terrainData;
        if (sd == null || nd == null) return null;
        return sd.heightmapResolution == nd.heightmapResolution ? candidate : null;
    }

    private Terrain GetMatchingNeighbor(Terrain self, Vector2Int neighborCellIndex)
    {
        return NeighborIfSameResolution(self, GetTile(neighborCellIndex));
    }

    private void UpdateNeighbors(Vector2Int cellIndex)
    {
        Vector2Int leftIndex = new Vector2Int(cellIndex.x - 1, cellIndex.y);
        Vector2Int rightIndex = new Vector2Int(cellIndex.x + 1, cellIndex.y);
        Vector2Int topIndex = new Vector2Int(cellIndex.x, cellIndex.y + 1);
        Vector2Int bottomIndex = new Vector2Int(cellIndex.x, cellIndex.y - 1);

        terrainTiles.TryGetValue(cellIndex, out Terrain center);
        terrainTiles.TryGetValue(leftIndex, out Terrain left);
        terrainTiles.TryGetValue(rightIndex, out Terrain right);
        terrainTiles.TryGetValue(topIndex, out Terrain top);
        terrainTiles.TryGetValue(bottomIndex, out Terrain bottom);

        if (center != null)
        {
            center.SetNeighbors(
                GetMatchingNeighbor(center, leftIndex),
                GetMatchingNeighbor(center, topIndex),
                GetMatchingNeighbor(center, rightIndex),
                GetMatchingNeighbor(center, bottomIndex));
        }
        if (left != null)
        {
            left.SetNeighbors(
                null,
                GetMatchingNeighbor(left, new Vector2Int(leftIndex.x, leftIndex.y + 1)),
                GetMatchingNeighbor(left, cellIndex),
                GetMatchingNeighbor(left, new Vector2Int(leftIndex.x, leftIndex.y - 1)));
        }
        if (right != null)
        {
            right.SetNeighbors(
                GetMatchingNeighbor(right, cellIndex),
                GetMatchingNeighbor(right, new Vector2Int(rightIndex.x, rightIndex.y + 1)),
                null,
                GetMatchingNeighbor(right, new Vector2Int(rightIndex.x, rightIndex.y - 1)));
        }
        if (top != null)
        {
            top.SetNeighbors(
                GetMatchingNeighbor(top, new Vector2Int(topIndex.x - 1, topIndex.y)),
                null,
                GetMatchingNeighbor(top, new Vector2Int(topIndex.x + 1, topIndex.y)),
                GetMatchingNeighbor(top, cellIndex));
        }
        if (bottom != null)
        {
            bottom.SetNeighbors(
                GetMatchingNeighbor(bottom, new Vector2Int(bottomIndex.x - 1, bottomIndex.y)),
                GetMatchingNeighbor(bottom, cellIndex),
                GetMatchingNeighbor(bottom, new Vector2Int(bottomIndex.x + 1, bottomIndex.y)),
                null);
        }
    }

    private void UpdateNeighborsForAdjacents(Vector2Int centerCell)
    {
        Vector2Int[] neighbors = new Vector2Int[]
        {
            new Vector2Int(centerCell.x - 1, centerCell.y),
            new Vector2Int(centerCell.x + 1, centerCell.y),
            new Vector2Int(centerCell.x, centerCell.y - 1),
            new Vector2Int(centerCell.x, centerCell.y + 1)
        };

        foreach (var neighbor in neighbors)
        {
            if (terrainTiles.ContainsKey(neighbor))
            {
                UpdateNeighbors(neighbor);
            }
        }
    }

    private Terrain GetTile(Vector2Int index)
    {
        terrainTiles.TryGetValue(index, out Terrain t);
        return t;
    }

    // ================================================================
    // Material settings
    // ================================================================

    /// <summary>
    /// Apply materials to Terrain tiles in resident cells.
    /// </summary>
    private void ApplyTerrainMaterial(Terrain terrain, TerrainData terrainData)
    {
        var resources = terrain.GetComponent<TerrainTileResources>();
        if (resources == null) resources = terrain.gameObject.AddComponent<TerrainTileResources>();
        resources.Clear();
        terrain.materialTemplate = null;
        if (useCustomMaterial && customTerrainMaterial != null)
        {
            terrain.materialTemplate = customTerrainMaterial;
            ApplyTerrainOffsetMaterial(terrain, resources);
            return;
        }

        if (customTerrainLayers != null && customTerrainLayers.Length > 0)
        {
            List<TerrainLayer> validLayers = new List<TerrainLayer>();
            foreach (var layer in customTerrainLayers)
            {
                if (layer != null) validLayers.Add(layer);
            }
            if (validLayers.Count > 0)
            {
                terrainData.terrainLayers = validLayers.ToArray();
                ApplyTerrainOffsetMaterial(terrain, resources);
                return;
            }
        }

        ApplyDefaultTerrainMaterial(terrainData, resources);
        ApplyTerrainOffsetMaterial(terrain, resources);
    }

    /// <summary>
    /// Name suffix identifying per-tile cloned materialTemplate instances.
    /// Material parameters are per tile; using shared materials/assets directly as
    /// materialTemplate would modify the original asset.
    /// </summary>
    private const string TILE_MATERIAL_SUFFIX = " (TileInstance)";

    /// <summary>
    /// Replace Terrain materialTemplate with Custom/DepthOffsetTerrainLit (Offset 1,1) to prevent Z-fighting.
    /// Always clone shared materials into per-tile instances
    /// to avoid modifying source assets. Identify existing clones by name suffix,
    /// avoiding double cloning on reapplication.
    /// </summary>
    private void ApplyTerrainOffsetMaterial(Terrain terrain, TerrainTileResources resources)
    {
        if (terrain == null) return;

        // Shader.Find may fail to locate shaders after building,
        // so prioritize direct SerializeField references and use Find only as a fallback when unset.
        Shader offsetShader = depthOffsetTerrainShader;
        if (offsetShader == null)
        {
            offsetShader = Shader.Find("Custom/DepthOffsetTerrainLit");
        }

        if (offsetShader == null)
        {
            Debug.LogWarning("TerrainManager: Custom/DepthOffsetTerrainLit shader not found");
            return;
        }

        Material mat = terrain.materialTemplate;
        if (mat != null)
        {
            if (mat.shader != offsetShader || !mat.name.EndsWith(TILE_MATERIAL_SUFFIX))
            {
                Material cloned = new Material(mat);
                cloned.shader = offsetShader;
                cloned.name = mat.name + TILE_MATERIAL_SUFFIX;
                terrain.materialTemplate = cloned;
                resources.Own(cloned);
            }
        }
        else
        {
            terrain.materialTemplate = new Material(offsetShader) { name = "TerrainTile" + TILE_MATERIAL_SUFFIX };
            resources.Own(terrain.materialTemplate);
        }

        ApplyTerrainMaterialSettings(terrain.materialTemplate);
    }

    private void ApplyTerrainMaterialSettings(Material material)
    {
        if (material == null) return;

        // Ground-cover-dependent values (base colors, relief, post-lighting overlays, rock tint and slope angles, shoreline)
        ActiveGroundCover.ApplyTo(material);

        // The following values are shared and independent of ground cover type.
        material.SetFloat(TerrainMacroVariationScaleId, Mathf.Max(1f, terrainMacroVariationScale));
        material.SetFloat(TerrainColorGradeLowHeightId, terrainColorGradeLowHeight);
        material.SetFloat(TerrainColorGradeHighHeightId, Mathf.Max(terrainColorGradeLowHeight + 1f, terrainColorGradeHighHeight));
        // Shaders multiply by inverse detail-normal period (1/m)
        material.SetFloat(TerrainDetailNormalScaleId, 1f / Mathf.Max(0.01f, terrainDetailNormalTilingMeters));

        EnsureCliffTextures();

        // Cliff rock blending settings (shader: Assets/Shaders/TerrainCliffRock.hlsl).
        // Without albedo, the shader uses solid fallback _CliffFallbackColor.
        bool cliffTexturesAvailable = cliffAlbedoTexture != null && cliffNormalTexture != null;
        if (cliffAlbedoTexture != null) material.SetTexture(CliffAlbedoMapId, cliffAlbedoTexture);
        if (cliffNormalTexture != null) material.SetTexture(CliffNormalMapId, cliffNormalTexture);
        material.SetFloat(CliffTexturesLoadedId, cliffTexturesAvailable ? 1f : 0f);
        // Shaders multiply by inverse tiling period (1/m); invert here before passing
        material.SetFloat(CliffUvScaleId, 1f / Mathf.Max(1f, cliffTilingMeters));
        material.SetFloat(CliffNoiseScaleId, Mathf.Max(1f, cliffNoiseScale));
        material.SetFloat(CliffNoiseStrengthId, cliffNoiseStrength);
        material.SetFloat(CliffNormalStrengthId, cliffNormalStrength);
        material.SetFloat(CliffSmoothnessId, cliffSmoothness);
        material.SetColor(CliffFallbackColorId, cliffFallbackColor);
        material.SetFloat(CliffLowlandStartId, cliffLowlandStartHeight);
        material.SetFloat(CliffLowlandFullId, cliffLowlandFullHeight);
    }

    /// <summary>
    /// Resolve cliff rock textures. If absent in Inspector (scene),
    /// load Resources/Textures/cliff-rock-albedo / cliff-rock-normal as fallbacks.
    /// Allows code defaults to work without editing scene files
    /// (past omissions of scene settings such as customTerrainLayers caused failures).
    /// Warn if either is unavailable and continue with the shader's solid-color fallback.
    /// </summary>
    private void EnsureCliffTextures()
    {
        if (cliffTexturesResolved) return;
        cliffTexturesResolved = true;

        if (cliffAlbedoTexture == null)
        {
            cliffAlbedoTexture = Resources.Load<Texture2D>("Textures/cliff-rock-albedo");
        }
        if (cliffNormalTexture == null)
        {
            cliffNormalTexture = Resources.Load<Texture2D>("Textures/cliff-rock-normal");
        }

        if (cliffAlbedoTexture == null || cliffNormalTexture == null)
        {
            Debug.LogWarning(
                "TerrainManager: 崖用の岩テクスチャが見つかりません" +
                $"(albedo={(cliffAlbedoTexture != null)}, normal={(cliffNormalTexture != null)})。" +
                "Resources/Textures/cliff-rock-albedo, cliff-rock-normal を配置するか、" +
                "InspectorのTerrain Cliff Rock Settingsに割り当ててください。" +
                "それまでは単色フォールバックで描画します。");
        }
    }

    /// <summary>
    /// Set a TerrainLayer using one ground-cover texture per tile.
    /// Use no normal map (procedural detail normals/wind ripples provide relief, matching UltraFar land without normal maps).
    /// If textures are unavailable, substitute a solid texture using base color A.
    /// </summary>
    private void ApplyDefaultTerrainMaterial(TerrainData terrainData, TerrainTileResources resources)
    {
        GroundCoverProfile ground = ActiveGroundCover;
        TerrainLayer groundLayer = resources.Own(new TerrainLayer());

        Texture2D diffuseTex = ResolveGroundTexture(ground);
        if (diffuseTex == null)
        {
            diffuseTex = resources.Own(new Texture2D(1, 1));
            diffuseTex.SetPixel(0, 0, ground.groundColorA);
            diffuseTex.Apply();
        }
        groundLayer.diffuseTexture = diffuseTex;

        Vector2 tileSize = ground.textureTileSize;
        if (tileSize.x <= 0f || tileSize.y <= 0f)
        {
            tileSize = new Vector2(terrainSize.x, terrainSize.z);
        }
        groundLayer.tileSize = tileSize;
        groundLayer.metallic = defaultMetallic;
        groundLayer.smoothness = ground.smoothness;

        terrainData.terrainLayers = new TerrainLayer[] { groundLayer };
    }

    [ContextMenu("Refresh Terrain Materials")]
    public void RefreshAllTerrainMaterials()
    {
        foreach (var kvp in terrainTiles)
        {
            if (kvp.Value != null)
            {
                ApplyTerrainMaterial(kvp.Value, kvp.Value.terrainData);
            }
        }
        // Repaint retained LRU tiles too, preventing restoration with appearance from before ground-cover changes.
        foreach (var entry in tileCache.Values)
        {
            if (entry.terrain != null) ApplyTerrainMaterial(entry.terrain, entry.terrain.terrainData);
        }
    }

    /// <summary>
    /// Apply ground appearance changes (cover type switches/Inspector adjustments) to visible tiles, UltraFar, and outside-city backgrounds.
    /// </summary>
    private void RefreshGroundCoverMaterials()
    {
        RefreshAllTerrainMaterials();
        if (ultraFarLandMat != null) EnsureUltraFarLandMaterials();
        if (backgroundMaterial != null) ApplyFarTerrainMaterialSettings(backgroundMaterial);
    }

    /// <summary>
    /// Profile for the currently rendered ground cover. Prioritize Inspector groundCoverPreview when nonzero.
    /// Use code defaults for types absent from the palette and grassland for unknown types.
    /// </summary>
    private GroundCoverProfile ActiveGroundCover
    {
        get
        {
            int groundCover = groundCoverPreview != 0 ? groundCoverPreview : cityGroundCover;
            if (groundCoverPalette == null && !groundCoverPaletteResolved)
            {
                groundCoverPaletteResolved = true;
                groundCoverPalette = Resources.Load<GroundCoverPalette>(GroundCoverPalette.ResourcePath);
            }
            GroundCoverProfile profile = groundCoverPalette != null ? groundCoverPalette.Find(groundCover) : null;
            return profile ?? FindDefaultGroundCover(groundCover) ?? FindDefaultGroundCover(Constants.GROUND_COVER_GRASSLAND);
        }
    }

    private static GroundCoverProfile FindDefaultGroundCover(int groundCover)
    {
        foreach (GroundCoverProfile profile in DefaultGroundCoverProfiles)
        {
            if (profile.groundCover == groundCover) return profile;
        }
        return null;
    }

    /// <summary>Profile ground texture. Warn only once and return null if unavailable.</summary>
    private Texture2D ResolveGroundTexture(GroundCoverProfile ground)
    {
        if (string.IsNullOrEmpty(ground.textureResource)) return null;
        if (!groundTextureCache.TryGetValue(ground.textureResource, out Texture2D texture))
        {
            texture = Resources.Load<Texture2D>(ground.textureResource);
            if (texture == null)
            {
                Debug.LogWarning($"TerrainManager: 地面テクスチャ Resources/{ground.textureResource} が見つからないため、下地色Aの単色で描きます。");
            }
            groundTextureCache[ground.textureResource] = texture;
        }
        return texture;
    }

    // ================================================================
    // Utilities
    // ================================================================

    private int GetValidTerrainResolution(int desiredSize)
    {
        // Limit heightmap resolution to 513 to reduce rendering load.
        // Even 1m data in 500m cells (501 points) is adequately represented at resolution 513.
        int[] validResolutions = { 33, 65, 129, 257, 513 };
        foreach (int resolution in validResolutions)
        {
            if (resolution >= desiredSize) return resolution;
        }
        return validResolutions[validResolutions.Length - 1];
    }

    private int ParseIntHeader(UnityWebRequest req, string headerName, int defaultValue)
    {
        var headers = req.GetResponseHeaders();
        if (headers != null)
        {
            foreach (var kvp in headers)
            {
                if (kvp.Key.Equals(headerName, System.StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(kvp.Value, out int val)) return val;
                }
            }
        }
        return defaultValue;
    }
}

/// <summary>
/// Data model for /api/v1/terrain/settings responses
/// </summary>
[System.Serializable]
public class TerrainSettingsData
{
    public int map_height;
    public int map_width;
    public int cell_size;
    public int far_map_spacing;
    public int near_map_spacing;
    public int dynamic_map_spacing;
}

/// <summary>
/// Data model for /api/v1/terrain/cell-versions responses
/// </summary>
[System.Serializable]
public class CellVersionInfo
{
    public int cell_x;
    public int cell_z;
    public int version;
}

[System.Serializable]
public class CellVersionsResponse
{
    public System.Collections.Generic.List<CellVersionInfo> cells;
}
