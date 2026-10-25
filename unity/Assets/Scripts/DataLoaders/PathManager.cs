using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Threading.Tasks;
using GeneratedModels;
using Dreamteck.Splines;
using NetTopologySuite.Algorithm;

/// <summary>
/// Example manager handling nodes and paths together.
/// Fetch and render node and path information per cell.
/// </summary>
public partial class PathManager : BaseApiDataManager
{
    // Road stage markers share the "Path." prefix, mirroring the "Building." markers of building generation.
    private static readonly StageMarker restoreLodMarker = new StageMarker("Path.RestoreLod");
    private static readonly StageMarker evaluateDrawLodMarker = new StageMarker("Path.EvaluateDrawLod");
    private static readonly StageMarker closeRankingMarker = new StageMarker("Path.CloseRanking");
    private static readonly StageMarker hasBodySegmentsMarker = new StageMarker("Path.HasBodySegments");
    private static readonly StageMarker anchorPolylineMarker = new StageMarker("Path.AnchorPolyline");
    private static readonly StageMarker coveredCellsMarker = new StageMarker("Path.CoveredCells");
    private static readonly StageMarker rebuildMarker = new StageMarker("Path.Rebuild");
    private static readonly StageMarker createOrUpdateFarMarker = new StageMarker("Path.CreateOrUpdateFar");
    private static readonly StageMarker createOrUpdateMarker = new StageMarker("Path.CreateOrUpdate");
    private static readonly StageMarker calculatePointsMarker = new StageMarker("Path.CalculatePoints");
    private static readonly StageMarker isTerrainCurrentMarker = new StageMarker("Path.IsTerrainCurrent");
    private static readonly StageMarker setupVisualsMarker = new StageMarker("Path.SetupVisuals");
    private static readonly StageMarker registerSegmentsMarker = new StageMarker("Path.RegisterSegments");

    public int PendingTerrainRebuildCount => queuedTerrainFollowPathIds.Count + queuedTerrainFollowNodeIds.Count;
    private readonly RenderObjectCache lodCache = new RenderObjectCache(128L * 1024 * 1024);
    private readonly HashSet<int> pendingPillarRebuildIds = new HashSet<int>();
    /// <summary>pathBuildSerialCounter at the last pendingPillarRebuildIds notification.</summary>
    private readonly Dictionary<int, long> pendingPillarRebuildSerials = new Dictionary<int, long>();
    /// <summary>Entries in pendingPillarRebuildIds containing power-line adjacency notifications. Distant power lines also need rebuilding, so do not treat them as pillar-only.</summary>
    private readonly HashSet<int> pendingPowerRebuildIds = new HashSet<int>();
    private readonly Dictionary<int, int> geometryRevisions = new Dictionary<int, int>();

    /// <summary>Discard derived path caches containing terrain heights (label positions and anchor points).</summary>
    private void InvalidateTerrainFollowPath(PathSummary path)
    {
        if (path == null) return;
        pathLabelPositions.Remove(path.path_id);
        anchorPolylineCache.Remove(path.path_id);
    }

    /// <summary>Discard derived road caches in a 3×3 neighborhood of updated cells, allowing margin for grounding and intersection endpoint dependencies.</summary>
    private void InvalidateTerrainFollowCell(Vector2Int cell)
    {
        for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++)
        {
            if (!pathIdsByCell.TryGetValue(cell + new Vector2Int(x, z), out var ids)) continue;
            foreach (int id in ids)
                if (pathDataCache.TryGetValue(id, out var path)) InvalidateTerrainFollowPath(path);
        }
    }

    private string PathRenderKey(PathSummary path, DrawLod lod, bool invalidate)
    {
        geometryRevisions.TryGetValue(path.path_id, out int revision);
        if (invalidate) geometryRevisions[path.path_id] = ++revision;
        return path.path_id + ":" + path.version + ":" + path.status + ":" + revision + ":" + lod + ":" + ConstructionPathBuilder.Progress(path) + ":" + UndergroundView.IsActive;
    }

    private void OnUndergroundChanged()
    {
        // Pass only paths whose visible sections change to the existing queue. Out-of-range paths generate during normal reloading.
        foreach (var path in pathDataCache.Values)
            if (TunnelSectionUtil.IsUndergroundPath(path) || !GetPathFollowTerrain(path))
                EnqueueTerrainFollowPath(path.path_id, pathBuildSerialCounter, TerrainFollowQueueReason.Other);
    }

    private bool RestorePathLod(PathSummary path, string key, DrawLod lod, bool updateIntersections)
    {
        using var scope = restoreLodMarker.Auto();
        pathObjectsCache.TryGetValue(path.path_id, out GameObject old);
        GameObject next = old != null && old.GetComponent<RenderCacheIdentity>()?.key == key ? old : lodCache.Take(key);
        if (next != null && next != old && !IsCachedBuildTerrainCurrent(next, path))
        {
            // The key excludes terrain signatures and terrain following ignores parked GameObjects; discard versions whose crossed-cell terrain changed while parked.
            RenderWorkScheduler.CountEvent("path.cachedTerrainStale");
            Destroy(next);
            next = null;
        }
        if (next == null)
        {
            RenderWorkScheduler.CountEvent("path.buildRequested");
            return false;
        }
        RenderWorkScheduler.CountEvent(next == old ? "path.reuseActive" : "path.reuseCached");
        if (next != old)
        {
            DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Path,
                old != null ? DrawLodInfo.GetDrawLod(old, DrawLod.Normal) : (DrawLod?)null, lod);
            PathPresentation.Replace(next, old, lodCache);
        }
        SetPathObject(path.path_id, next);
        next.SetActive(true);
        // The hidden set may change while parked, so resolve the filter again as during generation.
        ObjectVisibilityFilter.ApplyToObject(next, InputObjectType.PATH, path.path_id);
        EnsureEndIntersectionsVisible(path);
        if (updateIntersections) RegisterPathData(path); else SavePathToCache(path);
        RegisterPathCells(path);
        next.GetComponent<VisualPathStats>()?.InitFrom(path);
        next.GetComponent<VisualModeObject>()?.SetStatus(path.status);
        UpdatePlannedIcon(next, path, path.status == Constants.STATUS_PENDING);
        ReevaluateAwaitingIntersectionsForPath(path);
        return true;
    }

    // Node/waypoint visibility flags (public properties)
    private bool _showNodes = false;
    /// <summary>
    /// Node visibility. The setter applies changes to existing objects immediately,
    /// eliminating per-frame polling in Update.
    /// </summary>
    public bool ShowNodes
    {
        get => _showNodes;
        set
        {
            if (_showNodes == value) return;
            _showNodes = value;
            foreach (var obj in nodeObjectsCache.Values)
            {
                if (obj != null) obj.SetActive(value);
            }
        }
    }

    private bool _showWaypoints = false;
    /// <summary>
    /// Waypoint visibility. The setter applies changes to existing objects immediately.
    /// </summary>
    public bool ShowWaypoints
    {
        get => _showWaypoints;
        set
        {
            if (_showWaypoints == value) return;
            _showWaypoints = value;
            foreach (var kvp in pathObjectsCache)
            {
                GameObject pathObj = kvp.Value;
                if (pathObj == null) continue;

                if (value)
                {
                    bool hasWaypoint = false;
                    foreach (Transform child in pathObj.transform)
                    {
                        if (child.CompareTag(WAYPOINT_TAG))
                        {
                            hasWaypoint = true;
                            break;
                        }
                    }

                    if (!hasWaypoint && pathPointsCache.TryGetValue(kvp.Key, out List<Vector3> pts))
                    {
                        CreateWaypointsForPath(pathObj, pts);
                    }
                    else
                    {
                        foreach (Transform child in pathObj.transform)
                        {
                            if (child.CompareTag(WAYPOINT_TAG))
                                child.gameObject.SetActive(true);
                        }
                    }
                }
                else
                {
                    foreach (Transform child in pathObj.transform)
                    {
                        if (child.CompareTag(WAYPOINT_TAG))
                            child.gameObject.SetActive(false);
                    }
                }
            }
        }
    }

    // Naming convention constants
    private const string NODE_PREFIX = "Node_";
    private const string PATH_PREFIX = "Path_";
    private const string WAYPOINT_NAME = "Waypoint";
    
    // Tag name constants
    private const string NODE_TAG = "PathNode";
    private const string PATH_TAG = "PathObject";
    private const string WAYPOINT_TAG = "PathWaypoint";

    // Capture Dynamic terrain's 1m-grid relief to reduce road penetration by ridges between samples.
    public const float TERRAIN_SAMPLE_INTERVAL = 1f;



    // ==== Material cache ====
    private static Material cachedNodeMaterial;
    private static Material cachedWaypointMaterial;

    /// <summary>
    /// Cache materials obtained through Resources.Load to reduce call overhead.
    /// </summary>
    private static Material GetNodeMaterial()
    {
        if (cachedNodeMaterial == null)
            cachedNodeMaterial = Resources.Load<Material>("NodeMaterial");
        return cachedNodeMaterial;
    }

    private static Material GetWaypointMaterial()
    {
        if (cachedWaypointMaterial == null)
            cachedWaypointMaterial = Resources.Load<Material>("WaypointMaterial");
        return cachedWaypointMaterial;
    }

    private static Material nodeDepthMaterial;

    /// <summary>Shared DepthOffsetLit version of NodeMaterial for node cylinders.</summary>
    private static Material GetNodeDepthMaterial() => GetDepthOffsetMaterial(GetNodeMaterial(), ref nodeDepthMaterial);

    /// <summary>
    /// Create a shared material using the Z-fighting prevention shader Custom/DepthOffsetLit only once.
    /// Per-renderer copies through Renderer.materials accumulate without an owner releasing them, so share across all nodes.
    /// VisualModeObject overrides colors using material replacement and property blocks, so sharing does not affect other nodes.
    /// Use the original shared material if the shader is unavailable.
    /// </summary>
    private static Material GetDepthOffsetMaterial(Material source, ref Material cache)
    {
        if (cache != null) return cache;
        if (source == null) return null;
        Shader shader = Shader.Find("Custom/DepthOffsetLit");
        if (shader == null)
        {
            Debug.LogWarning("PathManager: Custom/DepthOffsetLit shader not found");
            cache = source;
        }
        else cache = source.shader == shader ? source : new Material(source) { name = source.name + " (DepthOffset)", shader = shader };
        return cache;
    }

    // ==== Spatial Hash ====
    private SpatialHash<NodeData> nodeGrid;
    private SpatialHash<PathSegment> segmentGrid;
    [SerializeField]
    private float gridCellSize = 5f; // Also editable in the Inspector
    /// <summary>Cell size used when building segmentGrid. Fix it so registration/removal point sequences agree even if the Inspector changes at runtime.</summary>
    private float segmentGridCellSize = 5f;
    
    private Dictionary<int, NodeData> nodeDataCache = new Dictionary<int, NodeData>();
    private Dictionary<int, PathSummary> pathDataCache = new Dictionary<int, PathSummary>();
    private Dictionary<int, HashSet<int>> pathsByNodeIndex = new Dictionary<int, HashSet<int>>();
    private Dictionary<int, GameObject> nodeIntersectionMeshes = new Dictionary<int, GameObject>();
    private readonly Dictionary<int, string> nodeMarkingSignatures = new();
    /// <summary>Per-node signature for allocation of track S-curves (TrackTransitionSignature). Changes require reconnecting tracks before/after and running alongside.</summary>
    private readonly Dictionary<int, string> nodeTransitionSignatures = new();

    /// <summary>Endpoints of tracks without nodes (planned, awaiting construction, or under construction). Indexed by position to find provisional connections (ignore Y).</summary>
    private sealed class ProvisionalTrackEnd
    {
        public int PathId;
        public bool AtStart;
        public Vector3 Position;
    }
    private SpatialHash<ProvisionalTrackEnd> provisionalEndGrid;
    private readonly Dictionary<int, List<ProvisionalTrackEnd>> provisionalEndsByPath = new();

    /// <summary>Placement preview tracks (negative path_id, registered through RoadRenderer.PreviewTrackSink) and their rendering points.</summary>
    private readonly List<PathSummary> previewTracks = new();
    private readonly Dictionary<int, List<Vector3>> previewTrackPoints = new();
    /// <summary>Plan whose geometry is being edited. Exclude from both provisional partners and adjacency candidates so preview and original plan do not pair together.</summary>
    private int previewExcludedPathId;
    private ulong previewTrackSignature;

    private static readonly System.Reflection.MethodInfo MemberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>
    /// Cell reverse index for nodeDataCache. During cell synchronization/unloading, retrieve only node GameObjects and intersection meshes
    /// in target cells without scanning all pooled nodes. Update alongside nodeDataCache additions, coordinate changes, and removals.
    /// </summary>
    private readonly Dictionary<Vector2Int, HashSet<int>> nodeIdsByCell = new Dictionary<Vector2Int, HashSet<int>>();
    private readonly Dictionary<int, Vector2Int> nodeCellById = new Dictionary<int, Vector2Int>();

    /// <summary>
    /// Path IDs in pathObjectsCache without registered crossed cells (pathCellsById). Apply position checks only to GameObjects
    /// unreachable through the cell reverse index, avoiding scans of the entire pool.
    /// </summary>
    private readonly HashSet<int> unregisteredPathObjectIds = new HashSet<int>();

    /// <summary>
    /// Input signature at the last successful intersection mesh generation (ComputeNodeIntersectionSignature).
    /// If signatures match and the intersection GameObject remains alive, UpdateNodeIntersectionMesh skips recalculation.
    /// Discard on generation failure, intersection removal, or connected road deletion.
    /// </summary>
    private readonly Dictionary<int, long> nodeIntersectionSignatures = new Dictionary<int, long>();

    /// <summary>
    /// Nodes created in an LOD batch whose intersection calculation was deferred to its intersection phase. Cancellation loses
    /// the batch's deferred set, and receiving existing nodes does not recalculate intersections, so recover them here on the next response.
    /// </summary>
    private readonly HashSet<int> deferredNewNodeIntersectionIds = new HashSet<int>();

    // GameObject Cache
    private Dictionary<int, GameObject> nodeObjectsCache = new Dictionary<int, GameObject>();
    private Dictionary<int, GameObject> pathObjectsCache = new Dictionary<int, GameObject>();

    /// <summary>
    /// Cache path versions fetched from the backend.
    /// Compare backend versions during incremental updates and recreate objects only if changed.
    /// </summary>
    private Dictionary<int, int> pathVersionCache = new Dictionary<int, int>();

    // Path connection point cache (PathID -> Point)
    // Separate start and end sides
    private Dictionary<int, Vector3> pathStartConnectionPoints = new Dictionary<int, Vector3>();
    private Dictionary<int, Vector3> pathEndConnectionPoints = new Dictionary<int, Vector3>();
    private readonly Dictionary<int, MotorwayMergeLayout> motorwayMerges = new Dictionary<int, MotorwayMergeLayout>();

    /// <summary>Merge nodes without plates (connect main lines at nodes and integrate ramps into the main-line road surface).</summary>
    private readonly HashSet<int> slablessMergeNodes = new HashSet<int>();

    public MotorwayMergeLayout GetMotorwayMerge(int nodeId)
        => motorwayMerges.TryGetValue(nodeId, out var layout) ? layout : null;

    // Path sidewalk setback cache (PathID -> SidewalkSetback).
    // At intersections where the crossing path lacks sidewalks, clip only the straight-side sidewalks
    // before the carriageway cut plane; the intersection fills the gap with end caps.
    // Like connection points, update from intersection mesh results (NodeMeshResult.SidewalkSetbacks)
    // and pass to road mesh generation (PathMeshFactory.GeneratePathMesh).
    private Dictionary<int, SidewalkSetback> pathStartSidewalkSetbacks = new Dictionary<int, SidewalkSetback>();
    private Dictionary<int, SidewalkSetback> pathEndSidewalkSetbacks = new Dictionary<int, SidewalkSetback>();

    // Registered segments per path (for removing old segments during regeneration)
    private Dictionary<int, List<PathSegment>> pathSegmentsByPathId = new Dictionary<int, List<PathSegment>>();
    private Dictionary<Vector2Int, LodTier> cellPathLod = new Dictionary<Vector2Int, LodTier>();
    private Dictionary<Vector2Int, HashSet<int>> pathIdsByCell = new Dictionary<Vector2Int, HashSet<int>>();
    private Dictionary<int, HashSet<Vector2Int>> pathCellsById = new Dictionary<int, HashSet<Vector2Int>>();
    private Dictionary<int, List<Vector3>> pathPointsCache = new Dictionary<int, List<Vector3>>();
    private readonly Dictionary<int, Vector3> pathLabelPositions = new();

    private bool isLodPathBatchActive = false;
    private HashSet<int> lodBatchRebuiltPathIds = new HashSet<int>();
    private HashSet<int> deferredIntersectionPathRebuildIds = new HashSet<int>();
    /// <summary>Build serial when enqueued in deferredIntersectionPathRebuildIds. Used to transfer to the rebuild queue if the batch ends without completion.</summary>
    private readonly Dictionary<int, long> deferredIntersectionRebuildSerials = new Dictionary<int, long>();
    private HashSet<int> deferredLodIntersectionNodeIds = new HashSet<int>();
    /// <summary>
    /// Paths the current LOD batch may rebuild. Add every received ID in phase 1; remove IDs found not to need regeneration
    /// in phase 2. The terrain-following queue and render LOD reevaluation do not take these during the batch.
    /// </summary>
    private readonly HashSet<int> lodBatchPendingBuildIds = new HashSet<int>();
    /// <summary>LOD load cancellation generation. Bulk fetches from canceled batches stop communication/decoding (ApiClient.GetBulk isCanceled).</summary>
    private int lodFetchGeneration;

    /// <summary>
    /// Whether bulk LOD loading (LoadMergedPathsAndNodesAndApplyCells) is running.
    /// During bulk loading, TryRebuildPathOnce skips duplicate regeneration using lodBatchRebuiltPathIds for roads already built in the batch.
    /// Terrain-arrival requests and render LOD reevaluation are outside the batch (batchMember=false); do not take roads the batch plans to rebuild.
    /// </summary>
    public bool IsLodBatchActive => isLodPathBatchActive;

    /// <summary>
    /// Node IDs still missing connected paths required for intersection generation.
    /// Register when some connected paths are unloaded, such as at cell boundaries;
    /// automatically reevaluate intersections when paths are added later.
    /// </summary>
    private HashSet<int> awaitingIntersectionNodes = new HashSet<int>();

    /// <summary>
    /// Node IDs being individually fetched for intersection generation.
    /// Concurrent fetch coroutines for one node could apply older intersection updates later depending on completion order,
    /// so consolidate fetching to one request per node.
    /// </summary>
    private HashSet<int> fetchingIntersectionNodeIds = new HashSet<int>();

    /// <summary>
    /// Maximum connected path count permitted for generating one node's intersection mesh.
    /// Intersection polygon calculation (IntersectionPolygonUtil) computes overlaps for every connected-path pair,
    /// an O(N²) operation; malformed backend data connecting many paths to one node could
    /// monopolize the main thread and freeze the editor. Valid intersections have at most a dozen or so paths,
    /// so treat larger counts as invalid data and skip intersection mesh generation.
    /// </summary>
    private const int MAX_INTERSECTION_CONNECTED_PATHS = 32;

    /// <summary>
    /// Node IDs already warned about excessive connected paths. Prevent repeated warnings for the same node.
    /// </summary>
    private HashSet<int> overloadedIntersectionNodeIds = new HashSet<int>();

    /// <summary>
    /// Track coroutines launched by LoadLodCells and cancel them on new camera movement.
    /// </summary>
    private List<Coroutine> activeLodCoroutines = new List<Coroutine>();

    [Header("計画中アイコン設定")]
    [Tooltip("計画中(status=3)の道路に表示する2Dアイコン")]
    public Sprite plannedPathIcon;
    [Tooltip("アイコンの大きさ")]
    public float plannedPathIconScale = 1f;
    [Tooltip("アイコンを地面からどれだけ浮かせるか")]
    public float plannedPathIconYOffset = 2f;

    /// <summary>
    /// Cells to retain during unloading checks,
    /// preventing paths spanning cells from being incorrectly hidden.
    /// Set at the start of LoadLodCells and used by UnloadCellsObjects.
    /// </summary>
    private HashSet<Vector2Int> cellsToKeepVisible = new HashSet<Vector2Int>();

    private void Awake()
    {
        UndergroundView.Changed += OnUndergroundChanged;
        if (GetComponent<PathSpeedLimitOverlay>() == null) gameObject.AddComponent<PathSpeedLimitOverlay>();
        nodeGrid = new SpatialHash<NodeData>(gridCellSize);
        provisionalEndGrid = new SpatialHash<ProvisionalTrackEnd>(gridCellSize);
        segmentGridCellSize = Mathf.Max(gridCellSize, 0.0001f);
        segmentGrid = new SpatialHash<PathSegment>(segmentGridCellSize);

        TerrainManager.OnTerrainTileReady += OnTerrainTileReadyHandler;

        // Supply nearby path points for pillar/roadside equipment interference and combined rendering of adjacent tracks.
        // PathManager owns this because it requires cached path data and the cell spatial index.
        PillarClearance.NeighborProvider = GetPillarObstacles;
        RoadEquipmentGenerator.OverheadProvider = GetOverheadObstacles;
        TrackAdjacency.NeighborProvider = GetAdjacentTrackNeighbors;
        RoadAdjacency.NeighborProvider = GetAdjacentRoadNeighbors;
        RoadAdjacency.EndJoinProvider = GetRoadEndJoin;
        TrackPath.ConnectedPathsProvider = GetTrackPathsAtNode;
        RoadRenderer.PreviewTrackSink = SetPreviewTracks;
        RoadMarkingRules.ConnectedPaths = GetPathsConnectedToNode;
        RoadMarkingRules.IsApproachEndpoint = (path, start, point) =>
        {
            var connections = start ? pathStartConnectionPoints : pathEndConnectionPoints;
            return connections.TryGetValue(path.path_id, out var cut) && Vector3.Distance(cut, point) < 1f;
        };
        PowerLinePath.ConnectedPaths = GetPathsConnectedToNode;
        PowerLinePath.FacilityTerminalPosition = GetPowerLineFacilityTerminalPosition;
        PowerLineRailGantry.TrackPolyline = GetPowerLineTrackPolyline;
        PowerLineRailGantry.NeighborProvider = GetPowerLineTrackNeighbors;
        TrackPath.InfrastructureConnectionProvider = IsInfrastructureConnection;
        TrackPath.LateralProfileProvider = GetTrackLateralProfile;
        LrtPlatformParts.PolylineProvider = GetLrtPlatformPolyline;
        BuildingRoadAccessIndex.Changed += OnBuildingRoadAccessChanged;
    }

    /// <summary>Point sequence resolving LRT platform ranges using the same uncut points and cut positions as vehicles.</summary>
    private List<Vector3> GetLrtPlatformPolyline(int pathId, bool trimIntersection) =>
        TryGetPathPolyline(pathId, out var points, trimIntersection) ? points : null;

    private List<Vector3> GetPowerLineTrackPolyline(PathSummary path) => CalculatePathPoints(path,false);
    private List<TrackNeighborInfo> GetPowerLineTrackNeighbors(PathSummary path) => BuildAdjacentTrackNeighbors(path,false);

    private Vector3? GetPowerLineFacilityTerminalPosition(int nodeId)
    {
        if (BuildingManager.Instance == null || !BuildingManager.Instance.TryGetInfraTerminalOwner(nodeId, out _)) return null;
        return GetNodeData(nodeId)?.GetAbsolutePosition();
    }

    private void OnDestroy()
    {
        UndergroundView.Changed -= OnUndergroundChanged;
        BuildingRoadAccessIndex.Changed -= OnBuildingRoadAccessChanged;
        if (RoadMarkingRules.ConnectedPaths == (Func<int, List<PathSummary>>)GetPathsConnectedToNode)
        {
            RoadMarkingRules.ConnectedPaths = null;
            RoadMarkingRules.IsApproachEndpoint = null;
            RoadMarkingAtlas.Clear();
        }
        lodCache.Clear();
        if (PowerLinePath.ConnectedPaths == (Func<int, List<PathSummary>>)GetPathsConnectedToNode)
            PowerLinePath.ConnectedPaths = null;
        if (PowerLinePath.FacilityTerminalPosition == (Func<int, Vector3?>)GetPowerLineFacilityTerminalPosition)
            PowerLinePath.FacilityTerminalPosition = null;
        if (PowerLineRailGantry.TrackPolyline == (Func<PathSummary, List<Vector3>>)GetPowerLineTrackPolyline)
            PowerLineRailGantry.TrackPolyline = null;
        if (PowerLineRailGantry.NeighborProvider == (Func<PathSummary, List<TrackNeighborInfo>>)GetPowerLineTrackNeighbors)
            PowerLineRailGantry.NeighborProvider = null;
        if (PillarClearance.NeighborProvider == (Func<PathSummary, PillarClearance.ObstacleQuery>)GetPillarObstacles)
            PillarClearance.NeighborProvider = null;
        if (RoadEquipmentGenerator.OverheadProvider == (Func<PathSummary, List<PillarClearance.Corridor>>)GetOverheadObstacles)
            RoadEquipmentGenerator.OverheadProvider = null;
        if (LrtPlatformParts.PolylineProvider == (Func<int, bool, List<Vector3>>)GetLrtPlatformPolyline)
            LrtPlatformParts.PolylineProvider = null;
        TerrainManager.OnTerrainTileReady -= OnTerrainTileReadyHandler;

        if (TrackAdjacency.NeighborProvider == (System.Func<PathSummary, List<TrackNeighborInfo>>)GetAdjacentTrackNeighbors)
        {
            TrackAdjacency.NeighborProvider = null;
            TrackPath.ConnectedPathsProvider = null;
            TrackPath.InfrastructureConnectionProvider = null;
            TrackPath.LateralProfileProvider = null;
        }
        if (RoadRenderer.PreviewTrackSink == (Action<List<PathSummary>, List<List<Vector3>>, int>)SetPreviewTracks)
        {
            RoadRenderer.PreviewTrackSink = null;
        }
        if (RoadAdjacency.NeighborProvider == (Func<PathSummary, List<RoadNeighborInfo>>)GetAdjacentRoadNeighbors)
        {
            RoadAdjacency.NeighborProvider = null;
            RoadAdjacency.EndJoinProvider = null;
        }
    }

    /// <summary>
    /// Whether this is a facility terminal (infrastructure or depot). Tracks/pipes continue into the facility, so place neither buffer stops nor intersections.
    /// Do not use node type bits (all electrified track nodes have the electricity bit). Resolve terminals through building reverse indices,
    /// so terminals cannot be identified until the facility loads (normally ready first because buildings load before paths).
    /// </summary>
    private bool IsInfrastructureConnection(int nodeId)
    {
        if (BuildingManager.Instance == null || nodeId <= 0) return false;
        return BuildingManager.Instance.TryGetInfraTerminalOwner(nodeId, out _) || BuildingManager.Instance.IsDepotTerminalNode(nodeId);
    }

    /// <summary>
    /// Whether a node needs an intersection (plate, rail connection, endpoint clipping). Only nodes connecting at least two physical traffic paths
    /// (excluding infrastructure, people mover, and logical public transport paths) that are not facility terminals.
    /// </summary>
    private bool NeedsIntersection(int nodeId, List<PathSummary> connectedPaths)
    {
        int transport = 0;
        foreach (var path in connectedPaths)
            if (!InfraNetworkUtil.IsInfraPathType(path.path_type) && path.path_type != Constants.PATH_CONVEYOR
                && path.path_type != Constants.PATH_TRANSPORT) transport++;
        return transport >= 2 && !IsInfrastructureConnection(nodeId);
    }

    /// <summary>
    /// Lateral track shape (TrackEndTransition S-curve), derived from paths connected at both end nodes.
    /// Track meshes (TrackPath), vehicle rendering, and adjacent-track joining use the same shape. Null for non-track paths.
    /// </summary>
    public TrackLateralProfile GetTrackLateralProfile(PathSummary path)
    {
        if (path == null || !TrackIntersectionMeshGenerator.IsTrackType(path.path_type)) return null;
        PathSummary self = path;
        List<PathSummary> start = ResolveTrackEnd(ref self, true);
        List<PathSummary> end = ResolveTrackEnd(ref self, false);
        return TrackEndTransition.BuildProfile(self, start, end);
    }

    /// <summary>
    /// Paths connected at a track endpoint, including itself. With a real node, use that node's connections (including provisional tracks; GetTrackPathsAtNode).
    /// For endpoints without nodes (planned, under construction, previews), search by position using completion welding rules. If the target has a real node,
    /// assign its ID to the endpoint of a self copy; otherwise assign a provisional ID for the provisional-track junction.
    /// </summary>
    private List<PathSummary> ResolveTrackEnd(ref PathSummary self, bool atStart)
    {
        int nodeId = atStart ? self.start_node_id : self.end_node_id;
        if (nodeId > 0) return GetTrackPathsAtNode(nodeId);
        if (!TryGetTrackEndPosition(self, atStart, out Vector3 position)) return null;
        int selfId = self.path_id;
        NodeData node = FindWeldNode(position);
        if (node != null)
        {
            self = WithEndNode(self, atStart, node.node_id);
            var paths = GetTrackPathsAtNode(node.node_id);
            paths.RemoveAll(p => p.path_id == selfId);
            paths.Add(self);
            return paths;
        }
        int junction = ProvisionalJunctionId(position);
        self = WithEndNode(self, atStart, junction);
        var ends = new List<(PathSummary path, bool atStart)>();
        CollectProvisionalEnds(position, selfId, ends);
        if (ends.Count == 0) return null;
        var result = new List<PathSummary> { self };
        foreach (var (other, otherAtStart) in ends) result.Add(WithEndNode(other, otherAtStart, junction));
        return result;
    }

    /// <summary>
    /// Actual node connections augmented with copies of provisional tracks whose endpoints lie at that node (node-less planned, awaiting construction, under construction,
    /// and placement previews), treating them as connected there. Used for buffer stops and S-curves (TrackPath.ConnectedPathsProvider).
    /// Do not create intersection plates or rail connections to provisional tracks (created when completed and welded).
    /// </summary>
    private List<PathSummary> GetTrackPathsAtNode(int nodeId)
    {
        var result = GetPathsConnectedToNode(nodeId);
        if (nodeId <= 0) return result;
        NodeData node = GetNodeData(nodeId);
        if (node == null) return result;
        var ends = new List<(PathSummary path, bool atStart)>();
        CollectProvisionalEnds(node.GetAbsolutePosition(), int.MinValue, ends);
        foreach (var (path, atStart) in ends)
        {
            int id = path.path_id;
            if (!result.Exists(p => p.path_id == id)) result.Add(WithEndNode(path, atStart, nodeId));
        }
        return result;
    }

    /// <summary>Endpoint position (anchor horizontal position and absolute elevation from ground plus relative height).</summary>
    private static bool TryGetTrackEndPosition(PathSummary path, bool atStart, out Vector3 position)
    {
        position = default;
        var anchor = atStart ? path.GetStartPointList() : path.GetEndPointList();
        if (anchor == null || anchor.Count < 3) return false;
        var ground = new Vector3(anchor[0], 0f, anchor[2]);
        position = new Vector3(anchor[0], Utils.GetGroundHeight(ground) + anchor[1], anchor[2]);
        return true;
    }

    /// <summary>
    /// Real node to weld at the position (nearest track-connected node within completion welding distance/elevation tolerances).
    /// Mirrors core find_nearby_node (RAIL_NODE_SNAP_THRESHOLD and elevation difference).
    /// </summary>
    private NodeData FindWeldNode(Vector3 position)
    {
        NodeData best = null;
        float bestDistance = float.MaxValue;
        foreach (NodeData node in QueryNearbyNodes(position, LocalConstants.PROVISIONAL_TRACK_WELD_DISTANCE))
        {
            Vector3 nodePosition = node.GetAbsolutePosition();
            float distance = Vector2.Distance(new Vector2(nodePosition.x, nodePosition.z), new Vector2(position.x, position.z));
            if (distance > LocalConstants.PROVISIONAL_TRACK_WELD_DISTANCE || distance >= bestDistance) continue;
            if (Mathf.Abs(nodePosition.y - position.y) >= LocalConstants.PROVISIONAL_TRACK_WELD_HEIGHT) continue;
            if (!GetPathsConnectedToNode(node.node_id).Exists(IsTrackTypePath)) continue;
            best = node;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>
    /// Provisional track ends at the position (node-less planned/constructing tracks and placement previews). Exclude excludePathId and plans whose geometry is being edited.
    /// </summary>
    private void CollectProvisionalEnds(Vector3 position, int excludePathId, List<(PathSummary path, bool atStart)> result)
    {
        float tolerance = LocalConstants.PROVISIONAL_TRACK_WELD_DISTANCE;
        bool Matches(PathSummary path, bool atStart)
        {
            if (!TryGetTrackEndPosition(path, atStart, out Vector3 end)) return false;
            return Vector2.Distance(new Vector2(end.x, end.z), new Vector2(position.x, position.z)) <= tolerance
                && Mathf.Abs(end.y - position.y) < LocalConstants.PROVISIONAL_TRACK_WELD_HEIGHT;
        }
        if (provisionalEndGrid != null)
        {
            foreach (var end in provisionalEndGrid.Query(new Vector3(position.x, 0f, position.z), tolerance))
            {
                if (end.PathId == excludePathId || (previewExcludedPathId > 0 && end.PathId == previewExcludedPathId)) continue;
                if (!pathDataCache.TryGetValue(end.PathId, out PathSummary path) || !Matches(path, end.AtStart)) continue;
                result.Add((path, end.AtStart));
            }
        }
        foreach (PathSummary preview in previewTracks)
        {
            if (preview.path_id == excludePathId) continue;
            for (int end = 0; end < 2; end++)
                if (Matches(preview, end == 0)) result.Add((preview, end == 0));
        }
    }

    /// <summary>ID for a junction between provisional tracks without a real node. Negative value derived from position, avoiding real IDs and unset -1.</summary>
    private static int ProvisionalJunctionId(Vector3 position)
    {
        int hash = (Mathf.RoundToInt(position.x * 2f) * 73856093) ^ (Mathf.RoundToInt(position.z * 2f) * 19349663);
        return -2 - (hash & 0x3FFFFFFF);
    }

    /// <summary>Assign a connected node ID to one endpoint of a path copy, allowing provisional connections through existing node-based checks.</summary>
    private static PathSummary WithEndNode(PathSummary path, bool atStart, int nodeId)
    {
        var clone = (PathSummary)MemberwiseCloneMethod.Invoke(path, null);
        if (atStart) clone.start_node_id = nodeId;
        else clone.end_node_id = nodeId;
        return clone;
    }

    /// <summary>Index endpoints of node-less tracks (planned, awaiting construction, under construction). Remove other tracks from the index.</summary>
    private void IndexProvisionalTrackEnds(PathSummary path)
    {
        UnindexProvisionalTrackEnds(path.path_id);
        if (provisionalEndGrid == null || !IsTrackTypePath(path) || !TrackAdjacency.IsProvisional(path)) return;
        var ends = new List<ProvisionalTrackEnd>(2);
        for (int end = 0; end < 2; end++)
        {
            bool atStart = end == 0;
            if ((atStart ? path.start_node_id : path.end_node_id) > 0) continue;
            var anchor = atStart ? path.GetStartPointList() : path.GetEndPointList();
            if (anchor == null || anchor.Count < 3) continue;
            var item = new ProvisionalTrackEnd { PathId = path.path_id, AtStart = atStart, Position = new Vector3(anchor[0], 0f, anchor[2]) };
            provisionalEndGrid.Add(item, item.Position);
            ends.Add(item);
        }
        if (ends.Count > 0) provisionalEndsByPath[path.path_id] = ends;
    }

    private void UnindexProvisionalTrackEnds(int pathId)
    {
        if (!provisionalEndsByPath.TryGetValue(pathId, out var ends)) return;
        foreach (var end in ends) provisionalEndGrid?.Remove(end, end.Position);
        provisionalEndsByPath.Remove(pathId);
    }

    /// <summary>
    /// Register placement preview tracks as provisional partners (RoadRenderer.PreviewTrackSink). Remove if tracks is null.
    /// Do nothing if geometry (points, variant, direction, excluded plan) matches the previous value. Otherwise queue tracks near both old and new previews
    /// for rebuilding (S-curve connections and parallel seams). excludedPathId is the plan being edited, or 0 if none.
    /// </summary>
    private void SetPreviewTracks(List<PathSummary> tracks, List<List<Vector3>> points, int excludedPathId)
    {
        ulong signature = PreviewTrackSignature(tracks, points, excludedPathId);
        if (signature == previewTrackSignature) return;
        var affected = new HashSet<int>();
        foreach (PathSummary old in previewTracks) affected.UnionWith(CollectNearbyTrackPathIds(old));
        previewTracks.Clear();
        previewTrackPoints.Clear();
        if (tracks != null && points != null)
        {
            for (int i = 0; i < tracks.Count && i < points.Count; i++)
            {
                if (tracks[i] == null || points[i] == null || points[i].Count < 2) continue;
                previewTracks.Add(tracks[i]);
                previewTrackPoints[tracks[i].path_id] = points[i];
            }
        }
        foreach (PathSummary track in previewTracks) affected.UnionWith(CollectNearbyTrackPathIds(track));
        if (previewExcludedPathId != excludedPathId)
        {
            foreach (int id in new[] { previewExcludedPathId, excludedPathId })
                if (id > 0 && pathDataCache.TryGetValue(id, out PathSummary edited))
                {
                    affected.Add(id);
                    affected.UnionWith(CollectNearbyTrackPathIds(edited));
                }
        }
        previewExcludedPathId = excludedPathId;
        previewTrackSignature = signature;
        foreach (int id in affected) QueueTrackNeighborRebuild(id);
        FlushPendingTrackNeighborRebuilds(null);
    }

    /// <summary>Preview track geometry signature (0.1m units). 0 means not registered.</summary>
    private static ulong PreviewTrackSignature(List<PathSummary> tracks, List<List<Vector3>> points, int excludedPathId)
    {
        if (tracks == null || tracks.Count == 0) return 0;
        ulong h = SignatureMix((ulong)(uint)excludedPathId ^ 0x9E3779B97F4A7C15UL);
        for (int i = 0; i < tracks.Count; i++)
        {
            PathSummary track = tracks[i];
            if (track == null) continue;
            h = SignatureMix(h ^ (uint)track.path_id ^ ((ulong)(uint)track.path_variant << 32));
            h = SignatureMix(h ^ (uint)track.travel_direction ^ ((ulong)(uint)track.platform_side << 32));
            if (points == null || i >= points.Count || points[i] == null) continue;
            foreach (Vector3 point in points[i])
                h = SignatureMix(h ^ (uint)Mathf.RoundToInt(point.x * 10f) ^ ((ulong)(uint)Mathf.RoundToInt(point.z * 10f) << 32) ^ (ulong)(uint)Mathf.RoundToInt(point.y * 10f) << 16);
        }
        return h == 0 ? 1 : h;
    }

    /// <summary>Signature of per-node track S-curve allocations (allocated length, full length, lateral offset per path). Empty without S-curves.</summary>
    private static string TrackTransitionSignature(int nodeId, List<PathSummary> connectedPaths)
    {
        var shifts = TrackEndTransition.ResolveNode(nodeId, connectedPaths);
        if (shifts.Count == 0) return "";
        var signature = new System.Text.StringBuilder("|S");
        var ids = new List<int>(shifts.Keys);
        ids.Sort();
        foreach (int id in ids)
        {
            var shift = shifts[id];
            signature.Append('|').Append(id).Append(':').Append(Mathf.RoundToInt(shift.Own * 100)).Append(':').Append(Mathf.RoundToInt(shift.Span * 100));
            foreach (float delta in shift.Delta) signature.Append(':').Append(Mathf.RoundToInt(delta * 1000));
        }
        return signature.ToString();
    }

    // ================================================================
    // View-prioritized terrain-following rebuild queue
    // ================================================================

    /// <summary>
    /// Horizontal half-angle (degrees) and unconditional nearby distance (m) used to prioritize road rebuilds by view.
    /// Match BuildingManager criteria.
    /// </summary>
    private const float PATH_VIEW_HALF_ANGLE_DEG = 75f;
    private const float PATH_VIEW_NEAR_DISTANCE = 100f;

    /// <summary>Processing penalty for paths outside the view. Always orders them after in-view paths (distance only).</summary>
    private const float PATH_OUT_OF_VIEW_PENALTY = 100000f;

    /// <summary>
    /// Path regeneration queue triggered by terrain tile updates (OnTerrainTileReadyHandler).
    /// Each path takes several ms for synchronous spline and SplineMesh generation,
    /// so avoid synchronous bulk rebuilding inside the handler; TerrainFollowRebuildWorker
    /// processes within the frame budget, prioritizing the view.
    /// </summary>
    private readonly HashSet<int> queuedTerrainFollowPathIds = new HashSet<int>();
    /// <summary>queuedTerrainFollowPathIds entries triggered only by terrain tile arrival. Skip if terrain matches generation time.</summary>
    private readonly HashSet<int> terrainOnlyQueuedPathIds = new HashSet<int>();
    /// <summary>queuedTerrainFollowPathIds entries triggered only by surrounding pillar/roadside equipment changes. Skip at Far, which draws neither.</summary>
    private readonly HashSet<int> queuedPillarOnlyIds = new HashSet<int>();
    /// <summary>
    /// Build serial at enqueue time (maximum for overlapping entries). If GameObject PathBuildStamp.buildSerial is larger,
    /// it was rebuilt after the triggering change, so skip regeneration.
    /// </summary>
    private readonly Dictionary<int, long> queuedTerrainFollowSerials = new Dictionary<int, long>();
    /// <summary>Intersections awaiting terrain-arrival rebuilds (endpoints of queued roads). Process after the road queue empties.</summary>
    private readonly HashSet<int> queuedTerrainFollowNodeIds = new HashSet<int>();

    /// <summary>Serial advanced on each actual road GameObject generation. Never reset across sessions (assumed monotonically increasing).</summary>
    private long pathBuildSerialCounter;

    private enum TerrainFollowQueueReason { Terrain, Pillar, Other }

    /// <summary>
    /// Rebuild queue processing order (ascending view-priority score). Sorting on every insertion would be quadratic,
    /// so sort collectively only when exhausted, after a fixed stale-frame interval, or after substantial camera movement.
    /// Skip IDs already removed from the queue when taking entries.
    /// </summary>
    private List<(float score, int id)> terrainFollowOrder = new List<(float score, int id)>();
    private List<(float score, int id)> terrainFollowOrderScratch = new List<(float score, int id)>();
    private readonly List<int> terrainFollowOrderIds = new List<int>();
    private int terrainFollowOrderCursor;
    private bool terrainFollowOrderStale = true;
    private int terrainFollowOrderFrame;
    private Vector3 terrainFollowOrderCamPos;
    private Vector3 terrainFollowOrderCamFwd = Vector3.forward;
    private const int TERRAIN_FOLLOW_REORDER_FRAMES = 10;
    private const float TERRAIN_FOLLOW_REORDER_MOVE = 50f;
    private const float TERRAIN_FOLLOW_REORDER_ANGLE = 20f;
    private static readonly Comparison<(float score, int id)> TerrainFollowOrderComparison =
        (a, b) => a.score != b.score ? a.score.CompareTo(b.score) : a.id.CompareTo(b.id);

    /// <summary>Persistent worker processing terrain-following regeneration requests. Starts on first insertion.</summary>
    private Coroutine terrainFollowRebuildWorker;

    /// <summary>
    /// Only entry point for the terrain-following regeneration queue. changeSerial is pathBuildSerialCounter after applying the cause.
    /// Set terrain-only/pillar-only markers only on new entries; clear if another reason overlaps (skipping always stays conservative).
    /// </summary>
    private void EnqueueTerrainFollowPath(int id, long changeSerial, TerrainFollowQueueReason reason,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "") // [Camera freeze investigation] Remove caller after investigation
    {
        bool added = queuedTerrainFollowPathIds.Add(id);
        // [Camera freeze investigation] Remove after investigation
        queuedTerrainFollowCallers[id] = !added && queuedTerrainFollowCallers.TryGetValue(id, out string callers) && !callers.Contains(caller) ? callers + "+" + caller : caller;
        if (added || !queuedTerrainFollowSerials.TryGetValue(id, out long queuedSerial) || queuedSerial < changeSerial)
            queuedTerrainFollowSerials[id] = changeSerial;
        if (added && reason == TerrainFollowQueueReason.Terrain) terrainOnlyQueuedPathIds.Add(id);
        else if (reason != TerrainFollowQueueReason.Terrain) terrainOnlyQueuedPathIds.Remove(id);
        if (added && reason == TerrainFollowQueueReason.Pillar) queuedPillarOnlyIds.Add(id);
        else if (reason != TerrainFollowQueueReason.Pillar) queuedPillarOnlyIds.Remove(id);
        terrainFollowOrderStale = true;
        EnsureTerrainFollowRebuildWorker();
    }

    /// <summary>[Camera freeze investigation] Methods that queued each path, shown in the regeneration log. Remove after investigation.</summary>
    private readonly Dictionary<int, string> queuedTerrainFollowCallers = new Dictionary<int, string>();

    private void ClearTerrainFollowQueue()
    {
        queuedTerrainFollowPathIds.Clear();
        queuedTerrainFollowCallers.Clear(); // [Camera freeze investigation] Remove after investigation
        queuedTerrainFollowNodeIds.Clear();
        terrainOnlyQueuedPathIds.Clear();
        queuedPillarOnlyIds.Clear();
        queuedTerrainFollowSerials.Clear();
        terrainFollowOrder.Clear();
        terrainFollowOrderCursor = 0;
        terrainFollowOrderStale = true;
    }

    // ================================================================
    // Render LOD (per-path detail, independent of cell data tier)
    // ================================================================

    /// <summary>Persistent worker reevaluating path render LOD on camera movement. Starts on the first LOD load.</summary>
    private Coroutine pathDrawLodWorker;

    /// <summary>
    /// Paths meeting the close-up condition around the camera, ascending by (distance, path ID). Rebuilt on first use each frame (closeRankingFrame)
    /// so every evaluation in a frame shares it; only the DrawLodConfig.MAX_CLOSE_PATHS nearest render Close.
    /// </summary>
    private readonly List<(float distance, int id)> closeRanking = new List<(float distance, int id)>();
    private readonly HashSet<int> closeRankingVisited = new HashSet<int>();
    private int closeRankingFrame = -1;

    /// <summary>Camera position at the previous render LOD evaluation. Reevaluate only after moving a specified distance.</summary>
    private Vector3 lastPathDrawLodCameraPos = new Vector3(float.NaN, 0f, 0f);

    /// <summary>
    /// Cache of anchor points for render LOD evaluation. Valid only while source references
    /// the current pathDataCache instance. Discard on data replacement, terrain tile arrival (InvalidateTerrainFollowPath),
    /// or unload. Anchor Y includes terrain height, so stale pre-update values must not remain.
    /// </summary>
    private sealed class AnchorEntry
    {
        public PathSummary source;
        public List<Vector3> anchors;
        public float length;
    }
    private readonly Dictionary<int, AnchorEntry> anchorPolylineCache = new Dictionary<int, AnchorEntry>();

    /// <summary>
    /// Determine the path's desired render LOD and detailed section.
    /// Choose Close/Normal/Far by nearest distance to anchor points (start, waypoints, end),
    /// independent of data tier; use the existing object's render LOD for hysteresis.
    /// Promote Far to detail only after terrain tiles for crossed resident cells arrive (overview-based generation
    /// would need rebuilding when tiles arrive). Roads already detailed retain their geometry while waiting for terrain updates.
    /// Use full-length route clipping and leave long-road subdivision to the fixed-section worker.
    /// </summary>
    /// <param name="distance">Nearest camera distance to the anchor points. Used to order rebuilds.</param>
    private DrawLod DeterminePathDrawLod(PathSummary path, LodTier tier, Vector3 camPos, out double detailFrom, out double detailTo, out float distance)
    {
        detailFrom = 0.0;
        detailTo = 1.0;
        return EvaluatePathDrawLod(path, camPos, true, out distance);
    }

    /// <summary>
    /// Core of DeterminePathDrawLod. limitClose=true allows Close only for the nearest close-up candidates (closeRanking);
    /// false evaluates the close-up condition alone to build that ranking.
    /// </summary>
    private DrawLod EvaluatePathDrawLod(PathSummary path, Vector3 camPos, bool limitClose, out float distance)
    {
        using var scope = evaluateDrawLodMarker.Auto();
        distance = float.MaxValue;
        List<Vector3> anchors = GetAnchorPolyline(path, out float length);
        if (anchors.Count < 2) return DrawLod.Normal;

        DrawLod current = DrawLod.Normal;
        bool hasExisting = pathObjectsCache.TryGetValue(path.path_id, out GameObject existing) && existing != null;
        if (hasExisting)
        {
            current = DrawLodInfo.GetDrawLod(existing, DrawLod.Normal);
        }
        distance = DrawLodEvaluator.MinDistanceToPolyline(anchors, camPos);
        bool allowClose = !limitClose || IsAmongNearestClosePaths(path.path_id, distance, camPos);
        DrawLod lod = DrawLodEvaluator.EvaluatePathByDistance(distance, current, allowClose);
        TerrainManager terrain = Utils.CachedTerrainManager;
        if (lod != DrawLod.Far && (!hasExisting || current == DrawLod.Far)
            && terrain != null && !terrain.IsDetailTerrainReady(GetPathCoveredCellsCached(path))) return DrawLod.Far;
        if (lod != DrawLod.Close) return lod;

        // For roads with SplinePartLod fixed-section bodies, keep the parent at Normal and let sections switch Close/Normal
        // (rebuilding parents regenerates all sections, equipment, and pillars). Sectioning depends on intersection-clipped render length, construction state, and tunnel splits,
        // so inspect actual objects if generated. Far has no sections, so estimate as for first generation.
        if (existing != null && current != DrawLod.Far) return HasBodySegments(existing) ? DrawLod.Normal : DrawLod.Close;
        return length >= 256f && IsSegmentedLongRoad(path) ? DrawLod.Normal : DrawLod.Close;
    }

    private bool IsAmongNearestClosePaths(int pathId, float distance, Vector3 camPos)
    {
        // Beyond this distance Close is impossible, so most evaluations skip building the ranking.
        if (distance > DrawLodConfig.PATH_CLOSE_EXIT_DISTANCE) return false;
        RefreshCloseRanking(camPos);
        return DrawLodEvaluator.IsAmongNearest(closeRanking, pathId, distance, DrawLodConfig.MAX_CLOSE_PATHS);
    }

    /// <summary>
    /// Rebuild closeRanking from registered paths crossing cells within the close-up distance, once per frame.
    /// Roads whose fixed sections switch Close themselves never evaluate to Close, so they take no slot.
    /// </summary>
    private void RefreshCloseRanking(Vector3 camPos)
    {
        using var scope = closeRankingMarker.Auto();
        if (closeRankingFrame == Time.frameCount) return;
        closeRankingFrame = Time.frameCount;
        closeRanking.Clear();
        closeRankingVisited.Clear();
        float reach = DrawLodConfig.PATH_CLOSE_EXIT_DISTANCE;
        Vector2Int min = TerrainManager.WorldToCell(new Vector3(camPos.x - reach, 0f, camPos.z - reach));
        Vector2Int max = TerrainManager.WorldToCell(new Vector3(camPos.x + reach, 0f, camPos.z + reach));
        for (int x = min.x; x <= max.x; x++)
        {
            for (int z = min.y; z <= max.y; z++)
            {
                if (!pathIdsByCell.TryGetValue(new Vector2Int(x, z), out HashSet<int> ids)) continue;
                foreach (int id in ids)
                {
                    if (!closeRankingVisited.Add(id) || !pathDataCache.TryGetValue(id, out PathSummary path)) continue;
                    if (DrawLodEvaluator.MinDistanceToPolyline(GetAnchorPolyline(path, out _), camPos) > reach) continue;
                    if (EvaluatePathDrawLod(path, camPos, false, out float distance) == DrawLod.Close)
                        closeRanking.Add((distance, id));
                }
            }
        }
        closeRanking.Sort();
    }

    private static readonly List<SplinePartLod> splinePartLodBuffer = new List<SplinePartLod>();

    private static bool HasBodySegments(GameObject pathGO)
    {
        using var scope = hasBodySegmentsMarker.Auto();
        pathGO.GetComponentsInChildren(true, splinePartLodBuffer);
        bool found = splinePartLodBuffer.Exists(part => part.IsBody);
        splinePartLodBuffer.Clear();
        return found;
    }

    /// <summary>
    /// Estimate whether an ungenerated path is a normal road with its body divided into SplinePartLod fixed sections.
    /// Match RoadPath.CreateSplineMesh body-sectioning criteria (base_height<=0, not single_span,
    /// and not an angular polyline suppressing detailed sections). Exclude tracks (model_type=track).
    /// Incorrect estimates converge after one rebuild based on the actual generated HasBodySegments during reevaluation.
    /// </summary>
    private static bool IsSegmentedLongRoad(PathSummary path)
    {
        if (PathMeshFactory.GetModelType(path) != "default") return false;
        if (PowerLinePath.Handles(path) || RoadPath.IsRopewayVariant(path.path_variant)) return false;
        if (path.status == Constants.STATUS_CONSTRUCTING) return false;
        var master = MasterDataStore.Instance?.GetPathMaster(path.path_variant);
        if ((master?.single_span ?? false) || (master?.base_height ?? 0f) > 0f) return false;
        return path.curved || (path.waypoints?.Count ?? 0) == 0;
    }

    /// <summary>
    /// Return anchor points and their 3D polyline length. The list is shared with the cache; treat as read-only.
    /// Build each time without caching for instances not registered in pathDataCache (such as newly received data).
    /// </summary>
    private List<Vector3> GetAnchorPolyline(PathSummary path, out float length)
    {
        using var scope = anchorPolylineMarker.Auto();
        if (anchorPolylineCache.TryGetValue(path.path_id, out AnchorEntry entry) && ReferenceEquals(entry.source, path))
        {
            length = entry.length;
            return entry.anchors;
        }
        List<Vector3> anchors = BuildAnchorPolyline(path);
        length = 0f;
        for (int i = 1; i < anchors.Count; i++) length += Vector3.Distance(anchors[i-1], anchors[i]);
        if (pathDataCache.TryGetValue(path.path_id, out PathSummary cached) && ReferenceEquals(cached, path))
        {
            entry ??= new AnchorEntry();
            entry.source = path;
            entry.anchors = anchors;
            entry.length = length;
            anchorPolylineCache[path.path_id] = entry;
        }
        return anchors;
    }

    /// <summary>Anchor points joining start, waypoints, and end in world coordinates (coarse geometry for render LOD evaluation).</summary>
    private List<Vector3> BuildAnchorPolyline(PathSummary path)
    {
        var result = new List<Vector3>();
        var startList = path.GetStartPointList();
        var endList = path.GetEndPointList();
        if (startList == null || startList.Count < 2 || endList == null || endList.Count < 2) return result;
        result.Add(GetPathPointPosition(startList));
        var waypoints = path.GetWaypointList();
        if (waypoints != null)
        {
            foreach (var wp in waypoints) result.Add(wp.GetAbsolutePosition());
        }
        result.Add(GetPathPointPosition(endList));
        return result;
    }

    /// <summary>Whether the generated path's render LOD and detailed section effectively match desired values.</summary>
    private static bool DrawLodMatches(GameObject pathGO, DrawLod lod, double detailFrom, double detailTo)
    {
        var info = pathGO != null ? pathGO.GetComponent<DrawLodInfo>() : null;
        if (info == null) return lod == DrawLod.Normal && detailFrom <= 0.0 && detailTo >= 1.0;
        if (info.drawLod != lod) return false;
        if (lod == DrawLod.Far) return true;
        return System.Math.Abs(info.detailFrom - detailFrom) <= DrawLodConfig.PATH_DETAIL_RANGE_REBUILD_TOLERANCE
            && System.Math.Abs(info.detailTo - detailTo) <= DrawLodConfig.PATH_DETAIL_RANGE_REBUILD_TOLERANCE;
    }

    private void EnsureTerrainFollowRebuildWorker()
    {
        if (terrainFollowRebuildWorker == null)
        {
            terrainFollowRebuildWorker = RenderWorkScheduler.Start(this, TerrainFollowRebuildWorker(), "PathManager", () => PendingTerrainRebuildCount > 0);
        }
    }

    /// <summary>
    /// Persistent worker reevaluating generated path render LOD on camera movement.
    /// Scan all paths after sufficient movement, switching mismatches nearest first within the shared frame budget.
    /// If the camera moves again after rebuilding for a fixed frame interval, discard the rest of the list and rescan,
    /// so roads near the current camera always come before the remainder of a list made at an old position.
    /// During an LOD batch, leave only roads the batch plans to build to the batch, as TerrainFollowRebuildWorker does.
    /// Rotation/FOV do not change distance-based LOD. Keep old objects visible until regeneration finishes.
    /// </summary>
    private LodViewState drawViewState;

    // Scans are split into shared-budget slices, so iterate a copy of keys instead of enumerating the dictionary directly.
    private readonly List<int> drawLodScanIds = new List<int>();
    private readonly List<(float distance, int id)> drawLodChangeIds = new List<(float distance, int id)>();
    private readonly System.Diagnostics.Stopwatch drawLodScanWatch = new System.Diagnostics.Stopwatch();

    private IEnumerator PathDrawLodReevaluationWorker()
    {
        int nextScanFrame = 0;
        bool rescanAfterBatch = false;
        float moveSqr = DrawLodConfig.REEVALUATE_CAMERA_MOVE_DISTANCE * DrawLodConfig.REEVALUATE_CAMERA_MOVE_DISTANCE;
        while (true)
        {
            while (Time.frameCount < nextScanFrame) yield return null;
            nextScanFrame = Time.frameCount + DrawLodConfig.REEVALUATE_INTERVAL_FRAMES;
            // Roads left to a batch stay mismatched if it is canceled before building them, so rescan once it ends even without movement.
            if (rescanAfterBatch && !isLodPathBatchActive)
            {
                rescanAfterBatch = false;
                drawViewState = default;
            }
            if (pathObjectsCache.Count == 0) continue;

            Camera cam = Camera.main;
            if (cam == null) continue;
            if (!drawViewState.UpdatePosition(cam, DrawLodConfig.REEVALUATE_CAMERA_MOVE_DISTANCE)) continue;
            Vector3 camPos = cam.transform.position;
            Vector3 scanPos = camPos;
            RenderWorkScheduler.CountEvent("path.lodScanned", pathObjectsCache.Count);

            drawLodScanIds.Clear();
            drawLodScanIds.AddRange(pathObjectsCache.Keys);
            drawLodChangeIds.Clear();
            bool aborted = false;
            drawLodScanWatch.Restart();
            for (int i = 0; i < drawLodScanIds.Count; i++)
            {
                if (drawLodScanWatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                {
                    yield return RenderWorkScheduler.Continue;
                    drawLodScanWatch.Restart();
                    cam = Camera.main;
                    // Clear the reference position to guarantee a rescan next cycle.
                    if (cam == null)
                    {
                        aborted = true;
                        drawViewState = default;
                        break;
                    }
                    camPos = cam.transform.position;
                }
                int id = drawLodScanIds[i];
                if (!pathObjectsCache.TryGetValue(id, out GameObject obj) || obj == null || !obj.activeSelf) continue;
                if (!pathDataCache.TryGetValue(id, out PathSummary path)) continue;
                DrawLod desired = DeterminePathDrawLod(path, LodTier.Near, camPos, out double from, out double to, out float distance);
                if (DrawLodMatches(obj, desired, from, to)) continue;
                drawLodChangeIds.Add((distance, id));
            }
            drawLodScanIds.Clear();
            if (aborted)
            {
                drawLodChangeIds.Clear();
                continue;
            }
            drawLodChangeIds.Sort();
            // Count the interval from the end of the scan so a scan slower than the interval still leaves time to rebuild.
            nextScanFrame = Time.frameCount + DrawLodConfig.REEVALUATE_INTERVAL_FRAMES;
            for (int i = 0; i < drawLodChangeIds.Count; i++)
            {
                cam = Camera.main;
                if (cam == null) break;
                // drawViewState still holds scanPos, so the next cycle rescans immediately.
                if (Time.frameCount >= nextScanFrame && (cam.transform.position - scanPos).sqrMagnitude >= moveSqr) break;
                int id = drawLodChangeIds[i].id;
                // Building before the batch finalizes cut points would be wasted; the batch builds these itself.
                if (isLodPathBatchActive && (lodBatchPendingBuildIds.Contains(id) || deferredIntersectionPathRebuildIds.Contains(id)))
                {
                    rescanAfterBatch = true;
                    continue;
                }
                // Data may be replaced during yields; resolve it again each time to avoid overwriting with stale instances.
                if (!pathDataCache.TryGetValue(id, out PathSummary path)) continue;
                // Outside batch duplicate tracking, so a batch can still rebuild this road after finalizing its cut points.
                TryRebuildPathOnce(path, LodTier.Near, false, false, "描画LOD", batchMember: false);
                yield return RenderWorkScheduler.Continue;
            }
            drawLodChangeIds.Clear();
        }
    }

    /// <summary>
    /// Fill outside the detailed section with Far rendering. In sections without detailed parts (PathDrawContext),
    /// repeat distant models along the curve clipped from rendering points by fraction.
    /// </summary>
    private void AddFarRemainders(GameObject pathGO, PathSummary path, List<Vector3> points, double detailFrom, double detailTo)
    {
        if (detailFrom > 1e-4) PathFarModel.Create(pathGO, "FarRemainder_Start", path.path_variant, PathDrawContext.SlicePolylineByFraction(points, 0.0, detailFrom));
        if (1.0 - detailTo > 1e-4) PathFarModel.Create(pathGO, "FarRemainder_End", path.path_variant, PathDrawContext.SlicePolylineByFraction(points, detailTo, 1.0));
    }

    /// <summary>
    /// Pending regeneration queue for combined rendering of nearby tracks (TrackAdjacency).
    /// Track data changes (creation, version change, deletion) alter nearby parallel tracks' combined state
    /// (unified ballast, omitted noise barriers, catenary poles spanning the corridor),
    /// but those tracks' own versions do not change, so incremental updates do not regenerate them.
    /// Queue nearby tracks here and pass to queuedTerrainFollowPathIds immediately outside LOD batches,
    /// or at EndLodPathBatch during batches (excluding paths already generated in that batch),
    /// so TerrainFollowRebuildWorker regenerates them.
    /// </summary>
    private readonly HashSet<int> pendingTrackNeighborRebuildIds = new HashSet<int>();

    /// <summary>
    /// Path view-priority score (lower is earlier). Take the camera distance to the nearest point of each anchor segment
    /// (the same polyline as render LOD), adding a large penalty to points outside the view, and return the minimum.
    /// </summary>
    private float ScorePathForViewPriority(PathSummary path, Vector3 camPos, Vector3 camForwardHorizontal)
    {
        List<Vector3> anchors = GetAnchorPolyline(path, out _);
        float best = float.MaxValue - 1f;
        for (int i = 0; i < anchors.Count; i++)
        {
            Vector3 closest = i == 0 ? anchors[0] : PolylineUtils.ClosestPointOnSegment(camPos, anchors[i - 1], anchors[i]);
            float dist = Vector3.Distance(closest, camPos);
            if (!Utils.IsInHorizontalView(closest, camPos, camForwardHorizontal, PATH_VIEW_NEAR_DISTANCE, PATH_VIEW_HALF_ANGLE_DEG))
            {
                dist += PATH_OUT_OF_VIEW_PENALTY;
            }
            if (dist < best) best = dist;
        }
        return best;
    }

    /// <summary>
    /// Node view-priority score (lower is earlier): horizontal distance to node plus out-of-view penalty.
    /// </summary>
    private float ScoreNodeForViewPriority(int nodeId, Vector3 camPos, Vector3 camForwardHorizontal)
    {
        if (!nodeDataCache.TryGetValue(nodeId, out NodeData node) || node?.position == null || node.position.Length < 2)
        {
            return float.MaxValue - 1f;
        }
        float x = node.position[0];
        float z = node.position.Length >= 3 ? node.position[2] : node.position[1];
        Vector3 pos = new Vector3(x, 0f, z);
        Vector3 to = pos - camPos;
        to.y = 0f;
        float dist = to.magnitude;
        if (!Utils.IsInHorizontalView(pos, camPos, camForwardHorizontal, PATH_VIEW_NEAR_DISTANCE, PATH_VIEW_HALF_ANGLE_DEG))
        {
            dist += PATH_OUT_OF_VIEW_PENALTY;
        }
        return dist;
    }

    private bool NeedsTerrainFollowReorder(Camera cam)
    {
        if (terrainFollowOrderCursor >= terrainFollowOrder.Count) return true;
        if (terrainFollowOrderStale && Time.frameCount - terrainFollowOrderFrame >= TERRAIN_FOLLOW_REORDER_FRAMES) return true;
        if (cam == null) return false;
        Vector3 moved = cam.transform.position - terrainFollowOrderCamPos;
        moved.y = 0f;
        if (moved.sqrMagnitude >= TERRAIN_FOLLOW_REORDER_MOVE * TERRAIN_FOLLOW_REORDER_MOVE) return true;
        return Vector3.Angle(Utils.GetCameraHorizontalForward(cam), terrainFollowOrderCamFwd) >= TERRAIN_FOLLOW_REORDER_ANGLE;
    }

    /// <summary>
    /// Score and reorder a queue snapshot by view priority. Split scoring with the shared budget; handle concurrent additions/removals
    /// through the next sort (insertion sets stale) and existence checks when taking entries.
    /// </summary>
    /// <param name="watch">Caller step budget timer, allowing processing to continue with that step's remaining budget after sorting.</param>
    private IEnumerator RebuildTerrainFollowOrder(System.Diagnostics.Stopwatch watch)
    {
        terrainFollowOrderStale = false;
        terrainFollowOrderFrame = Time.frameCount;
        Camera cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        Vector3 camFwd = cam != null ? Utils.GetCameraHorizontalForward(cam) : Vector3.forward;
        terrainFollowOrderCamPos = camPos;
        terrainFollowOrderCamFwd = camFwd;

        terrainFollowOrderIds.Clear();
        terrainFollowOrderIds.AddRange(queuedTerrainFollowPathIds);
        terrainFollowOrderScratch.Clear();
        for (int i = 0; i < terrainFollowOrderIds.Count; i++)
        {
            if (watch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
            {
                yield return RenderWorkScheduler.Continue;
                watch.Restart();
            }
            int id = terrainFollowOrderIds[i];
            // Prioritize removal of entries whose data is gone.
            float score = pathDataCache.TryGetValue(id, out PathSummary p) ? ScorePathForViewPriority(p, camPos, camFwd) : float.MinValue;
            terrainFollowOrderScratch.Add((score, id));
        }
        terrainFollowOrderIds.Clear();
        terrainFollowOrderScratch.Sort(TerrainFollowOrderComparison);
        (terrainFollowOrder, terrainFollowOrderScratch) = (terrainFollowOrderScratch, terrainFollowOrder);
        terrainFollowOrderScratch.Clear();
        terrainFollowOrderCursor = 0;
    }

    /// <summary>
    /// Persistent worker processing path regeneration from terrain updates or surrounding pillar/adjacent-track changes.
    /// Regenerate in-view paths nearest the camera first within the frame budget. RebuildTerrainFollowOrder sets ordering collectively.
    /// During an LOD batch, leave paths the batch plans to rebuild queued (building before cut points are final
    /// would prevent final regeneration through batch duplicate suppression).
    /// </summary>
    private IEnumerator TerrainFollowRebuildWorker()
    {
        var budgetWatch = new System.Diagnostics.Stopwatch();
        while (true)
        {
            if (queuedTerrainFollowPathIds.Count == 0)
            {
                if (terrainFollowOrder.Count > 0)
                {
                    terrainFollowOrder.Clear();
                    terrainFollowOrderCursor = 0;
                }
                if (queuedTerrainFollowNodeIds.Count > 0 && !isLodPathBatchActive) yield return RefreshQueuedIntersections();
                yield return null;
                continue;
            }

            budgetWatch.Restart();
            if (NeedsTerrainFollowReorder(Camera.main)) yield return RebuildTerrainFollowOrder(budgetWatch);

            int processed = 0;
            int heldForBatch = 0;
            while (terrainFollowOrderCursor < terrainFollowOrder.Count && budgetWatch.Elapsed.TotalMilliseconds <= PerformanceConfig.SliceBudgetMs)
            {
                int bestId = terrainFollowOrder[terrainFollowOrderCursor++].id;
                if (!queuedTerrainFollowPathIds.Contains(bestId)) continue;
                if (isLodPathBatchActive && (lodBatchPendingBuildIds.Contains(bestId) || deferredIntersectionPathRebuildIds.Contains(bestId)))
                {
                    heldForBatch++;
                    continue;
                }

                processed++;
                queuedTerrainFollowPathIds.Remove(bestId);
                queuedTerrainFollowCallers.Remove(bestId, out string queuedBy); // [Camera freeze investigation] Remove after investigation
                bool terrainOnly = terrainOnlyQueuedPathIds.Remove(bestId);
                bool pillarOnly = queuedPillarOnlyIds.Remove(bestId);
                queuedTerrainFollowSerials.TryGetValue(bestId, out long queuedSerial);
                queuedTerrainFollowSerials.Remove(bestId);
                if (!pathDataCache.TryGetValue(bestId, out PathSummary path)) continue;

                // Do not regenerate paths outside the loaded range (crossing no displayed cells).
                // If cells unloaded since enqueueing, regenerating now
                // would revive paths that should be hidden as active GameObjects.
                // Normal LOD loading handles them when cells reload.
                bool inLoadedRange = false;
                foreach (var cell in GetPathCoveredCellsCached(path))
                {
                    if (cellsToKeepVisible.Contains(cell))
                    {
                        inLoadedRange = true;
                        break;
                    }
                }
                if (!inLoadedRange) continue;
                pathObjectsCache.TryGetValue(bestId, out GameObject currentGO);
                if (currentGO != null)
                {
                    PathBuildStamp stamp = currentGO.GetComponent<PathBuildStamp>();
                    if (stamp != null && stamp.buildSerial > queuedSerial)
                    {
                        RenderWorkScheduler.CountEvent("path.queueRebuildSkipped");
                        continue;
                    }
                }
                // Far draws neither pillars nor roadside equipment. The notification already advanced revision, so next Normal generation uses current surroundings.
                if (pillarOnly && !HasDetailedObject(bestId))
                {
                    RenderWorkScheduler.CountEvent("path.queueRebuildSkipped");
                    continue;
                }
                // A tile returning with the same revision (such as LRU restoration) leaves terrain unchanged since generation.
                if (terrainOnly && IsTerrainCurrent(path))
                {
                    RenderWorkScheduler.CountEvent("terrainFollow.pathTerrainUnchanged");
                    continue;
                }

                // Regenerate using the current tier, which may have changed since enqueueing,
                // avoiding wasteful full-detail meshes for paths now at Far.
                LodTier tier = LodTier.Far;
                foreach (var cell in GetPathCoveredCellsCached(path))
                {
                    if (cellPathLod.TryGetValue(cell, out LodTier t) && t > tier) tier = t;
                }
                TryRebuildPathOnce(path, tier, updateIntersections: false, forceRebuild: true, reason: $"地形追従(キュー:{queuedBy})", batchMember: false); // [Camera freeze investigation] Restore the fixed reason after investigation
            }

            // If all remaining work waits for the active batch, wait for batch completion (or the sorting interval) instead of repeatedly sorting.
            if (processed == 0 && heldForBatch > 0 && terrainFollowOrderCursor >= terrainFollowOrder.Count)
            {
                for (int i = 0; i < TERRAIN_FOLLOW_REORDER_FRAMES && isLodPathBatchActive; i++) yield return null;
                continue;
            }
            yield return RenderWorkScheduler.Continue;
        }
    }

    public override void ClearAllCells()
    {
        GetComponent<PathSpeedLimitOverlay>()?.Clear();
        RoadMarkingAtlas.Clear();
        Debug.Log("PathManager: 全ノード・道路データをクリア");
        closeRankingFrame = -1;
        lodCache.Clear();
        geometryRevisions.Clear();
        pendingBuildingAccessRebuilds.Clear();
        anchorPolylineCache.Clear();
        untrimmedPointsCache.Clear();
        connectionRevisions.Clear();
        DrawLodStats.ResetCounts(DrawLodStats.Category.Path);

        // Stop active LOD loading before clearing; otherwise old-session responses
        // arriving after clearing would reregister caches and spatial hashes.
        CancelActiveLodCoroutines();

        // Remove nodes
        foreach (var obj in nodeObjectsCache.Values)
        {
            if (obj != null) Destroy(obj);
        }
        nodeObjectsCache.Clear();
        nodeIdsByCell.Clear();
        nodeCellById.Clear();

        // Remove paths
        foreach (var obj in pathObjectsCache.Values)
        {
            if (obj != null) Destroy(obj);
        }
        pathObjectsCache.Clear();
        unregisteredPathObjectIds.Clear();

        // Remove waypoints (managed as path children, so no independent management is needed; defensively search tags for remnants)
        GameObject[] waypointObjects = GameObject.FindGameObjectsWithTag(WAYPOINT_TAG);
        foreach (var obj in waypointObjects)
        {
            Destroy(obj);
        }

        loadedCells.Clear();
        nodeDataCache.Clear();
        pathDataCache.Clear();
        ConstructionAmbience.Clear(ConstructionAmbience.Kind.Path);
        pendingPillarRebuildIds.Clear();
        pendingPillarRebuildSerials.Clear();
        pendingPowerRebuildIds.Clear();
        pathsByNodeIndex.Clear();
        pathVersionCache.Clear();

        // The spatial hash is the sole source for snapping and road edit mode background overlays
        // (existing paths). Cell unloading only hides objects and retains this data;
        // leaving it during clearing would allow snapping to nonexistent paths/nodes.
        nodeGrid?.Clear();
        segmentGrid?.Clear();
        provisionalEndGrid?.Clear();
        provisionalEndsByPath.Clear();
        // While the camera is stationary, background overlays rebuild only when NodeDataVersion changes,
        // so omitting a clear notification would retain old-session path lines.
        NodeDataVersion++;

        ClearIntersectionMeshes();
        pathStartConnectionPoints.Clear();
        pathEndConnectionPoints.Clear();
        pathStartSidewalkSetbacks.Clear();
        pathEndSidewalkSetbacks.Clear();
        pathSegmentsByPathId.Clear();
        pathIdsByCell.Clear();
        pathCellsById.Clear();
        pathCellsVersion.Clear();
        pathPlanBoundsById.Clear();
        pathPointsCache.Clear();
        pathLabelPositions.Clear();
        cellPathLod.Clear();
        awaitingIntersectionNodes.Clear();
        fetchingIntersectionNodeIds.Clear();
        overloadedIntersectionNodeIds.Clear();
        ClearTerrainFollowQueue();
        pendingTrackNeighborRebuildIds.Clear();
        pendingLrtPlatformRebuildSerials.Clear();
        cellsToKeepVisible.Clear();
        pendingUnloadCells.Clear();
        lodBatchPendingBuildIds.Clear();
    }

    // ================================================================
    // LOD-aware cell loading
    // ================================================================

    public override void LoadLodCells(LodCellSets cellSets, bool isForce)
    {
        LoadLodCells(cellSets, null, isForce);
    }

    public override void LoadLodCells(LodCellSets cellSets, HashSet<Vector2Int> tierChangedCells, bool isForce)
    {
        // Cancel all older running coroutines
        CancelActiveLodCoroutines();
        if (pathDrawLodWorker == null)
        {
            pathDrawLodWorker = RenderWorkScheduler.Start(this, PathDrawLodReevaluationWorker(), "PathManager", () => false);
        }

        var allPathCells = new HashSet<Vector2Int>(cellSets.farCells);
        var nearSet = new HashSet<Vector2Int>(cellSets.nearCells);
        var dynamicSet = new HashSet<Vector2Int>(cellSets.dynamicCells);

        // Set the cells-to-retain set for unloading checks
        // to avoid incorrectly hiding paths spanning cells
        cellsToKeepVisible = new HashSet<Vector2Int>(allPathCells);

        var toUnload = new List<Vector2Int>();
        foreach (var cell in loadedCells)
        {
            if (!allPathCells.Contains(cell))
                toUnload.Add(cell);
        }

        // Always target every cell in cellSets.farCells.
        // Newly load unloaded cells; incrementally update loaded cells by version.
        var cellsToLoad = new List<Vector2Int>();
        var lodUpdates = new Dictionary<Vector2Int, LodTier>();
        foreach (var cell in cellSets.farCells)
        {
            LodTier desiredTier = LodTier.Far;
            if (dynamicSet.Contains(cell)) desiredTier = LodTier.Dynamic;
            else if (nearSet.Contains(cell)) desiredTier = LodTier.Near;

            cellsToLoad.Add(cell);
            RenderWorkScheduler.CountEvent(loadedCells.Contains(cell) ? "path.fetchExistingCells" : "path.fetchNewCells");
            lodUpdates[cell] = desiredTier;
        }

        if (cellsToLoad.Count > 0)
        {
            List<Vector2Int> changedCellsForLog = BuildChangedCellsForPathLog(cellsToLoad, toUnload);

            // Extract only cells whose LOD tier actually changed.
            // Camera movement redraws roads in changed-tier cells from cache,
            // but backend changes (ReloadAllData) have no tier changes;
            // passing all cells to RebuildCachedPathsForCells would redraw everything without version comparison.
            List<Vector2Int> lodTierChangedCells = new List<Vector2Int>();
            foreach (var kvp in lodUpdates)
            {
                if (!cellPathLod.TryGetValue(kvp.Key, out LodTier oldTier) || oldTier != kvp.Value)
                {
                    lodTierChangedCells.Add(kvp.Key);
                }
            }

            activeLodCoroutines.Add(RenderWorkScheduler.Start(this, LoadMergedPathsAndNodesAndApplyCells(cellsToLoad, nearSet, dynamicSet, toUnload, lodUpdates, changedCellsForLog, lodTierChangedCells), "PathManager"));
        }
        else
        {
            ApplyLodCellChanges(cellsToLoad, toUnload, lodUpdates);
        }
    }

    /// <summary>
    /// Generate roads in new cells before hiding unneeded cells.
    /// Retain existing roads during API waits and mesh generation to prevent blank frames when crossing cells.
    /// </summary>
    private IEnumerator LoadMergedPathsAndNodesAndApplyCells(List<Vector2Int> cellsToLoad, HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet, List<Vector2Int> cellsToUnload, Dictionary<Vector2Int, LodTier> lodUpdates, List<Vector2Int> changedCellsForLog, List<Vector2Int> tierChangedCells)
    {
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager LoadMergedPathsAndNodesAndApplyCells START cells={cellsToLoad.Count}");

        bool succeeded = false;
        HashSet<int> rebuiltPathIds = new HashSet<int>();
        HashSet<int> targetPathIds = CollectAffectedPathIds(changedCellsForLog);
        HashSet<Vector2Int> changedCellSet = new HashSet<Vector2Int>(changedCellsForLog);

        BeginLodPathBatch();
        try
        {
            var swPhase = System.Diagnostics.Stopwatch.StartNew();
            yield return LoadMergedPathsAndNodes(cellsToLoad, nearSet, dynamicSet, success => succeeded = success, rebuiltPathIds, targetPathIds, changedCellSet);
            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager  LoadMergedPathsAndNodes: {swPhase.ElapsedMilliseconds}ms");

            if (!succeeded)
            {
                // Save and restore deferred information so batch failures do not lose it.
                var preservedNodeIds = new HashSet<int>(deferredLodIntersectionNodeIds);
                var preservedPathIds = new HashSet<int>(deferredIntersectionPathRebuildIds);
                EndLodPathBatch();
                foreach (var id in preservedNodeIds) deferredLodIntersectionNodeIds.Add(id);
                foreach (var id in preservedPathIds) deferredIntersectionPathRebuildIds.Add(id);
                yield break;
            }

            // Pass only changed-tier cells to RebuildCachedPathsForCells.
            // Passing all cells would redraw everything without version comparison,
            // so skip roads in cells whose tiers are unchanged.
            swPhase.Restart();
            yield return RebuildCachedPathsForCells(tierChangedCells, nearSet, dynamicSet, rebuiltPathIds);
            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager  RebuildCachedPathsForCells: {swPhase.ElapsedMilliseconds}ms (tierChangedCells={tierChangedCells.Count})");

            swPhase.Restart();
            yield return UpdateDeferredLodIntersections();
            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager  UpdateDeferredLodIntersections: {swPhase.ElapsedMilliseconds}ms");

            swPhase.Restart();
            yield return RebuildDeferredIntersectionPaths(nearSet, dynamicSet, rebuiltPathIds);
            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager  RebuildDeferredIntersectionPaths: {swPhase.ElapsedMilliseconds}ms");

            ApplyLodCellChanges(cellsToLoad, cellsToUnload, lodUpdates);
            EndLodPathBatch();

            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager LoadMergedPathsAndNodesAndApplyCells END: {swTotal.ElapsedMilliseconds}ms");
        }
        finally
        {
            // Do not leave later terrain-following work waiting indefinitely after cancellation or generation failure.
            if (isLodPathBatchActive) EndLodPathBatch();
        }
    }

    private void BeginLodPathBatch()
    {
        isLodPathBatchActive = true;
        lodBatchRebuiltPathIds.Clear();
        lodBatchPendingBuildIds.Clear();
        deferredIntersectionPathRebuildIds.Clear();
        deferredIntersectionRebuildSerials.Clear();
        deferredLodIntersectionNodeIds.Clear();
    }

    /// <summary>
    /// At batch end (completion, failure, cancellation), move roads with changed intersection cut points into the rebuild queue.
    /// Canceled batches and roads excluded as already built before cut points were finalized would appear unchanged
    /// in the next batch (both roads and nodes), leaving nobody to rebuild them. Serials record when cut points changed,
    /// so the worker skips roads rebuilt afterward.
    /// </summary>
    private void RequeueUnfinishedIntersectionRebuilds()
    {
        foreach (var kvp in deferredIntersectionRebuildSerials)
            EnqueueTerrainFollowPath(kvp.Key, kvp.Value, TerrainFollowQueueReason.Other);
        deferredIntersectionRebuildSerials.Clear();
    }

    private void EndLodPathBatch()
    {
        isLodPathBatchActive = false;
        RequeueUnfinishedIntersectionRebuilds();
        // Start nearby-track regeneration queued during the batch. Exclude paths already built in this batch,
        // since they were generated after phase 1 bulk cache updates and already have correct joining.
        FlushPendingTrackNeighborRebuilds(lodBatchRebuiltPathIds);
        lodBatchRebuiltPathIds.Clear();
        lodBatchPendingBuildIds.Clear();
        deferredIntersectionPathRebuildIds.Clear();
        deferredIntersectionRebuildSerials.Clear();
        deferredLodIntersectionNodeIds.Clear();
    }

    private void CancelActiveLodCoroutines()
    {
        lodFetchGeneration++;
        foreach (var c in activeLodCoroutines)
        {
            if (c != null) RenderWorkScheduler.Stop(this, c);
        }
        activeLodCoroutines.Clear();

        if (isLodPathBatchActive)
        {
            // LOD update coroutines may stop midway on camera movement.
            // Leaving bulk-update flags or deferred queues would wrongly keep road-end regeneration deferred on the next update.
            EndLodPathBatch();
        }
    }

    private void ApplyLodCellChanges(List<Vector2Int> cellsToLoad, List<Vector2Int> cellsToUnload, Dictionary<Vector2Int, LodTier> lodUpdates)
    {
        foreach (var cell in cellsToLoad)
        {
            loadedCells.Add(cell);
            pendingUnloadCells.Remove(cell);
        }

        foreach (var kvp in lodUpdates)
        {
            cellPathLod[kvp.Key] = kvp.Value;
        }

        UnloadCellsObjects(cellsToUnload);
        foreach (var cell in cellsToUnload)
        {
            loadedCells.Remove(cell);
            cellPathLod.Remove(cell);
        }
    }

    /// <summary>
    /// Bulk-fetch paths and nodes using a merged BBOX for multiple cells.
    /// Reduce per-cell API calls (up to 49) to two total: one for paths and one for nodes.
    /// </summary>
    private IEnumerator LoadMergedPathsAndNodes(List<Vector2Int> cells, HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet, Action<bool> onComplete = null, HashSet<int> rebuiltPathIds = null, HashSet<int> targetPathIds = null, HashSet<Vector2Int> changedCellsFilter = null)
    {
        // Paths are applied after nodes, but their pages are fetched while nodes load. Only communication overlaps.
        PagedPathListResponse fetchedPaths = null;
        Coroutine pathFetch = RenderWorkScheduler.Start(this, FetchMergedPaths(cells, response => fetchedPaths = response), "PathManager");
        if (pathFetch != null) activeLodCoroutines.Add(pathFetch);

        // Bulk-fetch nodes for all visible cells, including Far cells, just like paths.
        // This avoids individual API fetches for nodes at road ends in Far cells.
        if (cells.Count > 0)
        {
            var swNode = System.Diagnostics.Stopwatch.StartNew();
            yield return LoadMergedNodes(cells);
            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager  LoadMergedNodes: {swNode.ElapsedMilliseconds}ms");
        }

        var swPath = System.Diagnostics.Stopwatch.StartNew();
        if (pathFetch != null) yield return pathFetch;
        yield return LoadMergedPaths(fetchedPaths, cells, nearSet, dynamicSet, onComplete, rebuiltPathIds, targetPathIds, changedCellsFilter);
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager  LoadMergedPaths: {swPath.ElapsedMilliseconds}ms");
    }

    /// <summary>Fetch every page of paths in the cells' merged BBOX. Passes null when a page fails, is canceled, or the cursor does not advance.</summary>
    private IEnumerator FetchMergedPaths(List<Vector2Int> cells, Action<PagedPathListResponse> onDone)
    {
        CalculateMergedBBox(cells, out float minX, out float minZ, out float maxX, out float maxZ);
        string bbox = $"{minX},{minZ},{maxX},{maxZ}";
        PagedPathListResponse response = new PagedPathListResponse { paths = new List<ConstructionPathSummary>() };
        int cursor = 0;
        int generation = lodFetchGeneration;
        Func<bool> isCanceled = () => generation != lodFetchGeneration;
        while (true)
        {
            PagedPathListResponse page = null;
            yield return ApiClient.GetBulk<PagedPathListResponse>(
                $"/paths/?bbox={bbox}&limit=500&cursor={cursor}", value => page = value,
                timingLabel: "PathManager page", isCanceled: isCanceled);
            if (page?.paths == null) { response = null; break; }
            RenderWorkScheduler.CountEvent("path.received", page.paths.Count);
            response.paths.AddRange(page.paths);
            if (!page.next_cursor.HasValue) break;
            if (page.next_cursor.Value <= cursor) { response = null; break; }
            cursor = page.next_cursor.Value;
            yield return null;
        }
        onDone(response);
    }

    /// <summary>
    /// Bulk-fetch nodes in a BBOX merging multiple cells.
    /// </summary>
    private IEnumerator LoadMergedNodes(List<Vector2Int> cells)
    {
        CalculateMergedBBox(cells, out float minX, out float minZ, out float maxX, out float maxZ);

        int generation = lodFetchGeneration;
        NodeGeometryResponse response = null;
        yield return ApiClient.GetBulk<NodeGeometryResponse>(
            $"/nodes?start_x={minX}&end_x={maxX}&start_z={minZ}&end_z={maxZ}",
            r => response = r,
            timingLabel: "PathManager nodes",
            isCanceled: () => generation != lodFetchGeneration);
        if (response == null)
        {
            yield break;
        }

        HashSet<int> receivedNodeIds = new HashSet<int>();

        int nodeCount = response?.nodes?.Count ?? 0;
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[PathManager] LoadMergedNodes: received {nodeCount} nodes.");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var swProcess = System.Diagnostics.Stopwatch.StartNew();
        if (response != null && response.nodes != null)
        {
            foreach (var n in response.nodes)
            {
                CreateOrUpdateNode(n);
                receivedNodeIds.Add(n.node_id);
                if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                {
                    yield return RenderWorkScheduler.Continue;
                    stopwatch.Restart();
                }
            }
        }
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager  CreateOrUpdateNode loop ({nodeCount} nodes): {swProcess.ElapsedMilliseconds}ms");

        SyncNodesInCells(cells, receivedNodeIds);
    }

    /// <summary>
    /// Apply paths fetched from a BBOX merging multiple cells (FetchMergedPaths). A null response reports failure.
    /// Determine each path's LOD tier from its position; use full detail for Near and simplified rendering for Far.
    /// </summary>
    private IEnumerator LoadMergedPaths(PagedPathListResponse response, List<Vector2Int> cells, HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet, Action<bool> onComplete = null, HashSet<int> rebuiltPathIds = null, HashSet<int> targetPathIds = null, HashSet<Vector2Int> changedCellsFilter = null)
    {
        if (response == null)
        {
            onComplete?.Invoke(false);
            yield break;
        }

        HashSet<int> affectedNodeIds = new HashSet<int>();

        HashSet<int> receivedIds = new HashSet<int>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Store nearby primary roads (Dynamic / Near) separately from distant roads (Far).
        // Delayed Far road rendering is visually unobtrusive, so generate gradually in a separate asynchronous coroutine.
        List<PathSummary> highPriorityPaths = new List<PathSummary>();
        List<PathSummary> lowPriorityPaths = new List<PathSummary>();
        Dictionary<int, LodTier> batchPathTiers = new Dictionary<int, LodTier>();

        // Phase 1: update the data cache for every path first.
        // Intersection calculations require all connected path data,
        // so update pathDataCache collectively before rendering. Work yields at budget boundaries;
        // register received IDs as awaiting the batch before saving, preventing the terrain-following queue from building them first during yields.
        Dictionary<int, int> oldVersions = new Dictionary<int, int>();
        Dictionary<int, int> oldStatuses = new Dictionary<int, int>();
        var oldProgress = new Dictionary<int, float>();
        if (response?.paths != null)
        {
            foreach (var p in response.paths)
            {
                receivedIds.Add(p.path_id);
                if (isLodPathBatchActive) lodBatchPendingBuildIds.Add(p.path_id);
                if (pathVersionCache.TryGetValue(p.path_id, out int oldVer))
                {
                    oldVersions[p.path_id] = oldVer;
                }
                if (pathDataCache.TryGetValue(p.path_id, out PathSummary oldPath))
                {
                    oldStatuses[p.path_id] = oldPath.status;
                    oldProgress[p.path_id] = ConstructionPathBuilder.Progress(oldPath);
                }
                SavePathToCache(p);
                if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                {
                    yield return RenderWorkScheduler.Continue;
                    stopwatch.Restart();
                }
            }
        }

        // Phase 2: recreate objects only for new paths or changed versions.
        // Check budget at loop entry so unchanged entries that continue also pass through it.
        if (response?.paths != null)
        {
            foreach (var p in response.paths)
            {
                if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                {
                    yield return RenderWorkScheduler.Continue;
                    stopwatch.Restart();
                }
                bool versionChanged = true;
                int? renderedStatus = pathObjectsCache.TryGetValue(p.path_id, out var renderedPath) && renderedPath != null
                    ? renderedPath.GetComponent<ObjectIdInfo>()?.status
                    : oldStatuses.TryGetValue(p.path_id, out int cachedStatus) ? cachedStatus : (int?)null;
                if (oldVersions.TryGetValue(p.path_id, out int cachedVersion) && cachedVersion == p.version && HasPathRepresentation(p)
                    && renderedStatus == p.status
                    && (p.status != Constants.STATUS_CONSTRUCTING || (oldProgress.TryGetValue(p.path_id, out float previousProgress)
                        && previousProgress == ConstructionPathBuilder.Progress(p))))
                {
                    versionChanged = false;
                }

                if (!versionChanged)
                {
                    lodBatchPendingBuildIds.Remove(p.path_id);
                    RenderWorkScheduler.CountEvent("path.dataUnchanged");
                    // Reshow existing hidden objects even if they do not need regeneration.
                    if (pathObjectsCache.TryGetValue(p.path_id, out GameObject existingObj) && existingObj != null && !existingObj.activeSelf)
                    {
                        existingObj.SetActive(true);
                        EnsureEndIntersectionsVisible(p);
                    }
                    continue;
                }

                rebuiltPathIds?.Add(p.path_id);

                // Detect status changes (oldStatuses was saved before overwriting in phase 1)
                if (oldStatuses.TryGetValue(p.path_id, out int oldStatus))
                {
                    if (oldStatus != p.status)
                    {
                        if (p.start_node_id > 0) affectedNodeIds.Add(p.start_node_id);
                        if (p.end_node_id > 0) affectedNodeIds.Add(p.end_node_id);
                        TriggerPathStatusEffect(p, oldStatus, p.status);
                    }
                }
                else
                {
                    if (p.start_node_id > 0) affectedNodeIds.Add(p.start_node_id);
                    if (p.end_node_id > 0) affectedNodeIds.Add(p.end_node_id);
                }

                // Changed path versions may indicate geometry changes,
                // so update crossed-cell cache (pathCellsById) before determining LOD tier.
                // DeterminePathLodTier reads this cache; do not reverse the order.
                RegisterPathCells(p);

                // Combined track rendering: this change may alter the joining state of nearby parallel tracks,
                // so queue them for regeneration (after completion if a batch is active).
                // Rebuild highway main-line/ramp partners for the same reason.
                EnqueueAdjacentTrackNeighborRebuilds(p);
                EnqueueAdjacentRoadMergeRebuilds(p, affectedNodeIds);
                NotifyDepotTerminals(p);

                LodTier tier = DeterminePathLodTier(p, nearSet, dynamicSet);

                if (isLodPathBatchActive)
                {
                    // Bulk LOD updates register all target road data before finalizing intersections.
                    // During initial loading, intersection connection-point caches are absent; building road meshes first
                    // could leave road ends permanently mismatched with intersection geometry.
                    if (p.start_node_id > 0) deferredLodIntersectionNodeIds.Add(p.start_node_id);
                    if (p.end_node_id > 0) deferredLodIntersectionNodeIds.Add(p.end_node_id);

                    batchPathTiers[p.path_id] = tier;
                    if (tier == LodTier.Far)
                    {
                        lowPriorityPaths.Add(p);
                    }
                    else
                    {
                        highPriorityPaths.Add(p);
                    }
                    continue;
                }

                // Only new paths and paths with changed versions (changed appearance) reach here,
                // so always rebuild meshes. Without forceRebuild, CreateOrUpdatePath would enter
                // the reuse-if-status-matches branch, leaving active paths with changed geometry
                // (merging, splitting, rebuilding) using old meshes.
                TryRebuildPathOnce(p, tier, updateIntersections: true, forceRebuild: true, reason: "api");
            }
        }
        if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
        {
            yield return RenderWorkScheduler.Continue;
            stopwatch.Restart();
        }

        if (highPriorityPaths.Count > 0 || lowPriorityPaths.Count > 0)
        {
            SortPathsByCameraDistance(highPriorityPaths);
            SortPathsByCameraDistance(lowPriorityPaths);
            // Show roads drawn as Far before waiting for expensive pairwise intersection finalization.
            var provisionalFarIds = new HashSet<int>();
            yield return BuildProvisionalFarPaths(highPriorityPaths, batchPathTiers, provisionalFarIds);

            // Communication is batched, so bulk-fetch missing nodes
            yield return UpdateDeferredLodIntersections();

            // Build only high-priority roads (Dynamic/Near) synchronously.
            // This gives highest priority to displaying roads immediately around the player.
            foreach (var path in highPriorityPaths)
            {
                LodTier tier = batchPathTiers.TryGetValue(path.path_id, out LodTier storedTier) ? storedTier : DeterminePathLodTier(path, nearSet, dynamicSet);
                // Only paths selected by version differences are queued, so always rebuild as with "api".
                // Roads already shown as Far were just built with this version; reuse while still Far and build only when becoming detailed.
                TryRebuildPathOnce(path, tier, updateIntersections: false, forceRebuild: !provisionalFarIds.Contains(path.path_id), reason: "api-high");

                if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                {
                    yield return RenderWorkScheduler.Continue;
                    stopwatch.Restart();
                }
            }
        }

        SyncPathsInCells(cells, receivedIds, affectedNodeIds);

        // Regenerate primary intersection meshes (connected to high-priority roads or detected as changed)
        foreach (int nodeId in affectedNodeIds)
        {
            if (isLodPathBatchActive)
            {
                yield return UpdateNodeIntersectionMeshAfterNodeReady(nodeId);
            }
            else
            {
                UpdateNodeIntersectionMesh(nodeId);
            }
            if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
            {
                yield return RenderWorkScheduler.Continue;
                stopwatch.Restart();
            }
        }

        // Once nearby detailed roads and primary intersections finish building, notify the caller (CellDataLoader, etc.)
        // that loading completed, minimizing total load wait time.
        onComplete?.Invoke(true);

        // Build low-priority Far roads in a separate coroutine after notifying load completion.
        // This avoids waiting for the entire coroutine and lets terrain/nearby-road alignment finish immediately.
        if (lowPriorityPaths.Count > 0)
        {
            Coroutine lowPriorityCoroutine = RenderWorkScheduler.Start(this, BuildLowPriorityPathsCoroutine(lowPriorityPaths, batchPathTiers, nearSet, dynamicSet), "PathManager");
            activeLodCoroutines.Add(lowPriorityCoroutine);
        }
    }

    /// <summary>
    /// During LOD batches, build roads without objects whose draw LOD is Far now, before intersections are finalized; the batch reuses them while they stay Far.
    /// Roads drawn in detail wait for intersection finalization, keeping the citywide overview band until their object exists
    /// (a provisional Far model would be discarded seconds later). Do not count these in lodBatchRebuiltPathIds duplicate suppression.
    /// </summary>
    private IEnumerator BuildProvisionalFarPaths(List<PathSummary> paths, Dictionary<int, LodTier> tiers, HashSet<int> builtIds)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Camera cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        foreach (var path in paths)
        {
            if (HasPathRepresentation(path)) continue;
            if (DeterminePathDrawLod(path, tiers[path.path_id], camPos, out _, out _, out _) != DrawLod.Far) continue;
            CreateOrUpdateFarPath(path, forceRebuild: true);
            builtIds.Add(path.path_id);
            if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
            {
                yield return RenderWorkScheduler.Continue;
                stopwatch.Restart();
            }
        }
    }

    /// <summary>Sort roads by horizontal distance from camera to their start/waypoint/end polyline. Do not sample terrain.</summary>
    private static void SortPathsByCameraDistance(List<PathSummary> paths)
    {
        Camera cam = Camera.main;
        if (cam == null || paths.Count < 2) return;
        var camXZ = new Vector2(cam.transform.position.x, cam.transform.position.z);
        var distances = new Dictionary<int, float>(paths.Count);
        foreach (var path in paths) distances[path.path_id] = HorizontalDistanceToAnchors(path, camXZ);
        paths.Sort((a, b) => distances[a.path_id].CompareTo(distances[b.path_id]));
    }

    private static float HorizontalDistanceToAnchors(PathSummary path, Vector2 point)
    {
        var anchors = new List<Vector2>();
        void Add(IList<float> position)
        {
            if (position != null && position.Count >= 2) anchors.Add(new Vector2(position[0], position.Count >= 3 ? position[2] : position[1]));
        }
        Add(path.GetStartPointList());
        var waypoints = path.GetWaypointList();
        if (waypoints != null) foreach (var waypoint in waypoints) Add(waypoint.position);
        Add(path.GetEndPointList());
        if (anchors.Count == 0) return float.MaxValue;
        float best = (anchors[0] - point).sqrMagnitude;
        for (int i = 1; i < anchors.Count; i++)
        {
            Vector2 a = anchors[i - 1], b = anchors[i], ab = b - a;
            float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(point - a, ab) / ab.sqrMagnitude) : 0f;
            best = Mathf.Min(best, (a + ab * t - point).sqrMagnitude);
        }
        return Mathf.Sqrt(best);
    }

    /// <summary>
    /// Trigger effects according to road status changes.
    /// </summary>
    private void TriggerPathStatusEffect(PathSummary path, int oldStatus, int newStatus)
    {
        if (VisualEffectManager.Instance == null || path == null) return;

        var startPointList = path.GetStartPointList();
        var endPointList = path.GetEndPointList();
        if (startPointList == null || startPointList.Count < 2 || endPointList == null || endPointList.Count < 2) return;

        Vector3 startPos = GetPathPointPosition(startPointList);
        Vector3 endPos = GetPathPointPosition(endPointList);
        Vector3 centerPos = (startPos + endPos) * 0.5f;

        float groundHeight = Utils.GetGroundHeight(new Vector3(centerPos.x, 1000f, centerPos.z));
        centerPos.y = groundHeight;

        // Completion transition from constructing (STATUS_CONSTRUCTING) to active (STATUS_ACTIVE) or completed (STATUS_COMPLETED)
        if (oldStatus == Constants.STATUS_CONSTRUCTING && 
            (newStatus == Constants.STATUS_ACTIVE || newStatus == Constants.STATUS_COMPLETED))
        {
            VisualEffectManager.Instance.PlayConstructionComplete(centerPos);
        }
        // Transition from active (STATUS_ACTIVE) to demolition (STATUS_DEMOLISHING) or planned demolition (STATUS_SCHEDULED_DEMOLITION)
        else if (oldStatus == Constants.STATUS_ACTIVE && 
                 (newStatus == Constants.STATUS_DEMOLISHING || newStatus == Constants.STATUS_SCHEDULED_DEMOLITION))
        {
            VisualEffectManager.Instance.PlayDemolition(centerPos);
        }
    }

    /// <summary>
    /// Generate low-priority Far-tier roads asynchronously in the background,
    /// splitting across frames within the frame budget (PerformanceConfig.SliceBudgetMs).
    /// </summary>
    private IEnumerator BuildLowPriorityPathsCoroutine(List<PathSummary> paths, Dictionary<int, LodTier> batchPathTiers, HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var path in paths)
        {
            if (path == null) continue;

            LodTier tier = batchPathTiers.TryGetValue(path.path_id, out LodTier storedTier) ? storedTier : DeterminePathLodTier(path, nearSet, dynamicSet);
            // Only paths selected by version differences are queued, so always rebuild as with "api".
            TryRebuildPathOnce(path, tier, updateIntersections: false, forceRebuild: true, reason: "api-low");

            if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
            {
                yield return RenderWorkScheduler.Continue;
                stopwatch.Restart();
            }
        }
    }

    /// <summary>
    /// Determine LOD tier from path start, end, and waypoint positions.
    /// Use Dynamic (highest detail) if any anchor belongs to a Dynamic cell;
    /// otherwise check Near cells, falling back to Far (simplified rendering).
    /// Sample every anchor and choose the highest-detail tier
    /// so roads crossing cell boundaries render correctly.
    ///
    /// Use registered crossed-cell cache (pathCellsById) when available.
    /// For paths whose version changed (potential geometry changes),
    /// update the cache through RegisterPathCells before calling.
    /// </summary>
    private LodTier DeterminePathLodTier(PathSummary path, HashSet<Vector2Int> nearCells, HashSet<Vector2Int> dynamicCells)
    {
        LodTier bestTier = LodTier.Far;

        foreach (var cell in GetPathCoveredCellsCached(path))
        {
            if (dynamicCells.Contains(cell)) return LodTier.Dynamic;
            if (nearCells.Contains(cell)) bestTier = LodTier.Near;
        }

        return bestTier;
    }

    /// <summary>
    /// Return registered crossed-cell cache (pathCellsById) if available; otherwise recalculate.
    /// GetPathCoveredCells scans anchors and samples cells,
    /// so calling it for every path each time affects load duration.
    /// Note: immediately after a version change (before RegisterPathCells reregistration),
    /// the cache still contains old geometry. Call RegisterPathCells first after geometry changes.
    /// </summary>
    private HashSet<Vector2Int> GetPathCoveredCellsCached(PathSummary path)
    {
        using var scope = coveredCellsMarker.Auto();
        if (path != null && pathCellsById.TryGetValue(path.path_id, out HashSet<Vector2Int> cached))
        {
            return cached;
        }
        return GetPathCoveredCells(path);
    }

    private IEnumerator RebuildCachedPathsForCells(List<Vector2Int> changedCells, HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet, HashSet<int> alreadyRebuiltPathIds)
    {
        HashSet<int> pathIds = new HashSet<int>();
        foreach (var cell in changedCells)
        {
            if (!pathIdsByCell.TryGetValue(cell, out HashSet<int> ids)) continue;
            foreach (int id in ids)
            {
                if (alreadyRebuiltPathIds != null && alreadyRebuiltPathIds.Contains(id)) continue;
                pathIds.Add(id);
            }
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (int pathId in pathIds)
        {
            if (alreadyRebuiltPathIds != null && alreadyRebuiltPathIds.Contains(pathId))
            {
                // Reshow if already regenerated but still hidden.
                if (pathObjectsCache.TryGetValue(pathId, out GameObject existing) && existing != null && !existing.activeSelf)
                {
                    existing.SetActive(true);
                    if (pathDataCache.TryGetValue(pathId, out PathSummary shownPath)) EnsureEndIntersectionsVisible(shownPath);
                }
                continue;
            }

            if (!pathDataCache.TryGetValue(pathId, out PathSummary path)) continue;

            LodTier tier = DeterminePathLodTier(path, nearSet, dynamicSet);
            // Cell reentry alone does not advance geometry revisions; use existing LOD and parked caches.
            // Terrain, data, and intersection changes force regeneration through their respective update paths.
            TryRebuildPathOnce(path, tier, updateIntersections: true, forceRebuild: false, reason: "lookup");

            if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
            {
                yield return RenderWorkScheduler.Continue;
                stopwatch.Restart();
            }
        }
    }

    private IEnumerator RebuildDeferredIntersectionPaths(HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet, HashSet<int> alreadyRebuiltPathIds)
    {
        if (deferredIntersectionPathRebuildIds.Count == 0) yield break;

        // Intersection finalization may add entries during yields; iterate snapshots until no work remains rather than enumerating the set directly.
        var visited = new HashSet<int>();
        var pathIds = new List<int>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            pathIds.Clear();
            foreach (int id in deferredIntersectionPathRebuildIds)
            {
                if (visited.Add(id)) pathIds.Add(id);
            }
            if (pathIds.Count == 0) yield break;
            foreach (int pathId in pathIds)
            {
                if (alreadyRebuiltPathIds != null && alreadyRebuiltPathIds.Contains(pathId)) continue;
                if (!pathDataCache.TryGetValue(pathId, out PathSummary path)) continue;

                LodTier tier = DeterminePathLodTier(path, nearSet, dynamicSet);
                if (TryRebuildPathOnce(path, tier, updateIntersections: false, forceRebuild: true, reason: "intersection"))
                {
                    alreadyRebuiltPathIds?.Add(pathId);
                }

                if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                {
                    yield return RenderWorkScheduler.Continue;
                    stopwatch.Restart();
                }
            }
        }
    }

    private IEnumerator UpdateDeferredLodIntersections()
    {
        if (deferredLodIntersectionNodeIds.Count == 0) yield break;

        List<int> nodeIds = new List<int>(deferredLodIntersectionNodeIds);
        deferredLodIntersectionNodeIds.Clear();
        // Finalize nearest intersections first so nearby roads can begin detail generation earlier.
        Camera cam = Camera.main;
        if (cam != null)
        {
            Vector3 camPos = cam.transform.position;
            Vector3 camFwd = Utils.GetCameraHorizontalForward(cam);
            var scores = new Dictionary<int, float>(nodeIds.Count);
            foreach (int nodeId in nodeIds) scores[nodeId] = ScoreNodeForViewPriority(nodeId, camPos, camFwd);
            nodeIds.Sort((a, b) => scores[a].CompareTo(scores[b]));
        }

        // Extract node IDs absent from nodeDataCache and not already being fetched (fetchingIntersectionNodeIds),
        // then send a bulk request to the backend.
        // This avoids communication overhead from successive individual /nodes/{id} requests.
        List<int> missingNodeIds = new List<int>();
        foreach (int nodeId in nodeIds)
        {
            if (!nodeDataCache.ContainsKey(nodeId) && !fetchingIntersectionNodeIds.Contains(nodeId))
            {
                missingNodeIds.Add(nodeId);
            }
        }

        if (missingNodeIds.Count > 0)
        {
            yield return FetchNodesBatchCoroutine(missingNodeIds);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (int nodeId in nodeIds)
        {
            yield return UpdateNodeIntersectionMeshAfterNodeReady(nodeId);
            if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
            {
                yield return RenderWorkScheduler.Continue;
                stopwatch.Restart();
            }
        }
    }

    /// <summary>
    /// Accept missing node IDs and send bulk backend requests using comma-separated ids parameters.
    /// Split into batches of at most 100 to respect URL length limits and similar constraints.
    /// </summary>
    private IEnumerator FetchNodesBatchCoroutine(List<int> nodeIds)
    {
        const int batchSize = 100;
        for (int i = 0; i < nodeIds.Count; i += batchSize)
        {
            List<int> batch = nodeIds.GetRange(i, Math.Min(batchSize, nodeIds.Count - i));
            string idsParam = string.Join(",", batch);

            // Set fetching flags to prevent duplicate in-flight requests
            foreach (int nodeId in batch)
            {
                fetchingIntersectionNodeIds.Add(nodeId);
            }

            // Clear flags on batch cancellation too (Dispose through RenderWorkScheduler.Stop).
            // Otherwise later batches wait forever for these nodes and IsLodBatchActive never clears.
            try
            {
                NodeGeometryResponse response = null;
                yield return ApiClient.GetBulk<NodeGeometryResponse>(
                    $"/nodes?ids={idsParam}",
                    r => response = r,
                    timingLabel: "PathManager nodes-batch");

                if (response?.nodes != null)
                {
                    foreach (var n in response.nodes)
                    {
                        RegisterNodeData(n, out _);
                    }
                }
            }
            finally
            {
                foreach (int nodeId in batch)
                {
                    fetchingIntersectionNodeIds.Remove(nodeId);
                }
            }
        }
    }

    private IEnumerator UpdateNodeIntersectionMeshAfterNodeReady(int nodeId)
    {
        if (!nodeDataCache.ContainsKey(nodeId))
        {
            // Bulk LOD updates wait for this fetch before proceeding to road-end regeneration.
            // Using only StartCoroutine could leave intersection connection-point application until the next update, depending on camera movement or API response order.
            if (fetchingIntersectionNodeIds.Add(nodeId))
            {
                yield return FetchNodeAndUpdateIntersection(nodeId);
            }
            else
            {
                while (fetchingIntersectionNodeIds.Contains(nodeId))
                {
                    yield return null;
                }

                if (nodeDataCache.ContainsKey(nodeId))
                {
                    UpdateNodeIntersectionMesh(nodeId);
                }
            }
            yield break;
        }

        UpdateNodeIntersectionMesh(nodeId);
    }

    private bool HasPathRepresentation(PathSummary path)
    {
        if (pathObjectsCache.TryGetValue(path.path_id, out var obj) && obj != null) return true;
        return TunnelSectionUtil.IsUndergroundPath(path) || FarPathHasHiddenTunnel(path);
    }

    /// <param name="batchMember">If false, exclude this regeneration from LOD batch duplicate tracking even while a batch is active. Prevent outside regeneration
    /// (terrain-following queue) from suppressing later batch regeneration after cut points are finalized.</param>
    private bool TryRebuildPathOnce(PathSummary path, LodTier tier, bool updateIntersections, bool forceRebuild, string reason, bool batchMember = true)
    {
        using var scope = rebuildMarker.Auto();
        if (path == null) return false;
        RenderWorkScheduler.CountEvent(forceRebuild ? "path.forceRebuildRequested" : "path.lodRebuildRequested");
        if (forceRebuild && reason.StartsWith("地形追従", StringComparison.Ordinal))
            RenderWorkScheduler.CountEvent("path.terrainRebuildRequested");

        if (batchMember && isLodPathBatchActive && !lodBatchRebuiltPathIds.Add(path.path_id))
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"PathManager: 再描画 重複スキップ path{path.path_id} reason={reason}");
            // Reshow existing hidden objects even when duplicate regeneration is skipped.
            if (pathObjectsCache.TryGetValue(path.path_id, out GameObject existing) && existing != null && !existing.activeSelf)
            {
                existing.SetActive(true);
                EnsureEndIntersectionsVisible(path);
            }
            return false;
        }

        // Even for Near/Dynamic data tiers, render paths as Far when distant and visually small.
        Camera drawCam = Camera.main;
        Vector3 drawCamPos = drawCam != null ? drawCam.transform.position : Vector3.zero;
        DrawLod drawLod = DeterminePathDrawLod(path, tier, drawCamPos, out double detailFrom, out double detailTo, out _);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"PathManager: 再描画 path{path.path_id} tier={tier} draw={drawLod} range=({detailFrom:F2}〜{detailTo:F2}) reason={reason}");
        var freezeProbe = System.Diagnostics.Stopwatch.StartNew(); // [Camera freeze investigation] Remove after investigation
        int freezeProbeWarnings = FreezeProbe.WarningCount; // [Camera freeze investigation] Remove after investigation
        string freezeProbePrevious = pathObjectsCache.TryGetValue(path.path_id, out GameObject freezeProbeOld) && freezeProbeOld != null ? DrawLodInfo.GetDrawLod(freezeProbeOld, DrawLod.Normal).ToString() : "なし"; // [Camera freeze investigation] Remove after investigation
        if (drawLod == DrawLod.Far)
            CreateOrUpdateFarPath(path, forceRebuild);
        else
            CreateOrUpdatePath(path, updateIntersections, forceRebuild, drawLod, detailFrom, detailTo);
        // [Camera freeze investigation] Remove after investigation
        if (freezeProbe.Elapsed.TotalMilliseconds > 30)
        {
            GetAnchorPolyline(path, out float probeLength);
            int probeMeshes = pathObjectsCache.TryGetValue(path.path_id, out GameObject probeGo) && probeGo != null ? probeGo.GetComponentsInChildren<MeshFilter>(true).Length : -1;
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "[カメラフリーズ調査] パス再生成 {0:F0}ms path{1} variant={2} LOD={3}→{4} tier={5} 長さ={6:F0}m 経由点={7} メッシュ={8}個 警告ログ={9}件 reason={10}",
                freezeProbe.Elapsed.TotalMilliseconds, path.path_id, path.path_variant, freezeProbePrevious, drawLod, tier, probeLength, path.waypoints?.Count ?? 0, probeMeshes, FreezeProbe.WarningCount - freezeProbeWarnings, reason);
        }

        // Do not treat API reception or enqueueing as completed rendering. Regenerate after interruption even at the same version.
        if (HasPathRepresentation(path)) pathVersionCache[path.path_id] = path.version;
        return true;
    }

    private List<Vector2Int> BuildChangedCellsForPathLog(List<Vector2Int> cellsToLoad, List<Vector2Int> cellsToUnload)
    {
        HashSet<Vector2Int> uniqueCells = new HashSet<Vector2Int>();
        if (cellsToLoad != null)
        {
            foreach (var cell in cellsToLoad) uniqueCells.Add(cell);
        }
        if (cellsToUnload != null)
        {
            foreach (var cell in cellsToUnload) uniqueCells.Add(cell);
        }

        List<Vector2Int> cells = new List<Vector2Int>(uniqueCells);
        cells.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        return cells;
    }

    private HashSet<int> CollectAffectedPathIds(List<Vector2Int> changedCells)
    {
        HashSet<int> pathIds = new HashSet<int>();
        if (changedCells == null) return pathIds;

        foreach (var cell in changedCells)
        {
            if (!pathIdsByCell.TryGetValue(cell, out HashSet<int> lookupPathIds)) continue;
            foreach (int pathId in lookupPathIds)
            {
                pathIds.Add(pathId);
            }
        }

        return pathIds;
    }

    /// <summary>
    /// Synchronize paths spanning multiple cells (for incremental updates).
    /// Remove paths within the specified cells that are absent from the API response.
    /// </summary>
    private void SyncPathsInCells(List<Vector2Int> cells, HashSet<int> receivedIds, HashSet<int> affectedNodeIds)
    {
        List<int> pathsToRemove = new List<int>();

        // Narrow registered paths through the cell reverse index. Use legacy position checks only for unregistered GameObjects.
        HashSet<int> candidateIds = new HashSet<int>();
        foreach (var cell in cells)
        {
            if (!pathIdsByCell.TryGetValue(cell, out HashSet<int> ids)) continue;
            foreach (int id in ids)
            {
                if (!receivedIds.Contains(id)) candidateIds.Add(id);
            }
        }
        foreach (int pathId in candidateIds)
        {
            if (pathObjectsCache.TryGetValue(pathId, out GameObject obj) && obj != null) pathsToRemove.Add(pathId);
        }
        foreach (int pathId in unregisteredPathObjectIds)
        {
            if (receivedIds.Contains(pathId) || candidateIds.Contains(pathId)) continue;
            if (pathObjectsCache.TryGetValue(pathId, out GameObject obj) && obj != null && IsPathInCells(pathId, obj, cells)) pathsToRemove.Add(pathId);
        }

        foreach (int pathId in pathsToRemove)
        {
            if (pathDataCache.TryGetValue(pathId, out PathSummary removedPath))
            {
                if (removedPath.start_node_id > 0) affectedNodeIds.Add(removedPath.start_node_id);
                if (removedPath.end_node_id > 0) affectedNodeIds.Add(removedPath.end_node_id);

                // Combined track rendering: queue nearby tracks to undo joining with removed tracks
                // (restore noise barriers and catenary poles).
                // Do this before UnregisterPathCells because it reads crossed-cell caches.
                EnqueueAdjacentTrackNeighborRebuilds(removedPath);
                EnqueueAdjacentRoadMergeRebuilds(removedPath, affectedNodeIds);
            }

            UnregisterPathDataById(pathId);
            if (removedPath != null) NotifyDepotTerminals(removedPath);
            UnregisterPathCells(pathId);
            UnregisterPathSegments(pathId);
            DropEndNodeIntersectionSignatures(removedPath);
            pathStartConnectionPoints.Remove(pathId);
            pathEndConnectionPoints.Remove(pathId);
            pathStartSidewalkSetbacks.Remove(pathId);
            pathEndSidewalkSetbacks.Remove(pathId);
            DropRoadEndJoins(pathId);
            pathVersionCache.Remove(pathId);

            if (pathObjectsCache.TryGetValue(pathId, out GameObject obj))
            {
                if (obj != null) DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Path, DrawLodInfo.GetDrawLod(obj, DrawLod.Normal), null);
                Destroy(obj);
                RemovePathObject(pathId);
            }
        }
    }

    /// <summary>
    /// Index nodes by their data-coordinate cells. Node GameObjects and intersection meshes use node XZ coordinates,
    /// so derive from data without reading transforms (avoid GetAbsolutePosition, which samples terrain).
    /// </summary>
    private void IndexNodeCell(int nodeId)
    {
        if (!nodeDataCache.TryGetValue(nodeId, out NodeData node) || node?.position == null || node.position.Length < 2)
        {
            UnindexNodeCell(nodeId);
            return;
        }
        Vector2Int cell = TerrainManager.WorldToCell(node.position[0], node.position.Length >= 3 ? node.position[2] : node.position[1]);
        if (nodeCellById.TryGetValue(nodeId, out Vector2Int indexed) && indexed == cell) return;
        UnindexNodeCell(nodeId);
        nodeCellById[nodeId] = cell;
        if (!nodeIdsByCell.TryGetValue(cell, out HashSet<int> ids))
        {
            ids = new HashSet<int>();
            nodeIdsByCell[cell] = ids;
        }
        ids.Add(nodeId);
    }

    private void UnindexNodeCell(int nodeId)
    {
        if (!nodeCellById.Remove(nodeId, out Vector2Int cell)) return;
        if (!nodeIdsByCell.TryGetValue(cell, out HashSet<int> ids)) return;
        ids.Remove(nodeId);
        if (ids.Count == 0) nodeIdsByCell.Remove(cell);
    }

    /// <summary>
    /// Synchronize nodes spanning multiple cells (for incremental updates).
    /// Remove nodes within the specified cells that are absent from the API response.
    /// </summary>
    private void SyncNodesInCells(List<Vector2Int> cells, HashSet<int> receivedNodeIds)
    {
        List<int> nodesToRemove = new List<int>();

        foreach (var cell in cells)
        {
            if (!nodeIdsByCell.TryGetValue(cell, out HashSet<int> ids)) continue;
            foreach (int nodeId in ids)
            {
                if (!receivedNodeIds.Contains(nodeId)) nodesToRemove.Add(nodeId);
            }
        }

        // Nodes fetched individually for intersections have no GameObjects; check the cell index rather than object presence.
        foreach (var nodeId in nodesToRemove)
        {
            if (nodeObjectsCache.TryGetValue(nodeId, out GameObject obj))
            {
                Destroy(obj);
                nodeObjectsCache.Remove(nodeId);
            }
            // Remove from the spatial hash too (otherwise snapping could target deleted nodes)
            if (nodeDataCache.TryGetValue(nodeId, out NodeData removedNode))
            {
                nodeGrid?.Remove(removedNode, removedNode.GetAbsolutePosition());
            }
            nodeDataCache.Remove(nodeId);
            UnindexNodeCell(nodeId);
            RemoveNodeIntersectionMesh(nodeId);
        }
        if (nodesToRemove.Count > 0)
        {
            NodeDataVersion++;
        }
    }

    private void CreateOrUpdateFarPath(PathSummary path, bool forceRebuild = false)
    {
        using var scope = createOrUpdateFarMarker.Auto();
        SavePathToCache(path);
        EnsureEndIntersectionsVisible(path);

        // In normal view, hide Far rendering of underground paths and non-following tunnel paths through hills.
        // Far models along a straight start-to-end line ignore buried sections and would cross the surface.
        // Tunnel portals and cut sections are invisible at distance, so hiding is acceptable. Render Far for non-following bridges crossing only valleys.
        // Retain existing Near-detail objects as inactive rather than destroying them, following pooling conventions.
        if (!UndergroundView.IsActive && (TunnelSectionUtil.IsUndergroundPath(path) || FarPathHasHiddenTunnel(path)))
        {
            if (pathObjectsCache.TryGetValue(path.path_id, out GameObject existingUnderground)
                && existingUnderground != null && existingUnderground.activeSelf)
            {
                existingUnderground.SetActive(false);
            }
            RegisterPathCells(path);
            return;
        }

        string renderKey = PathRenderKey(path, DrawLod.Far, forceRebuild);
        if (RestorePathLod(path, renderKey, DrawLod.Far, false)) return;
        geometryRevisions.TryGetValue(path.path_id, out int buildRevision);
        long buildSerial = ++pathBuildSerialCounter;
        forceRebuild = true;
        GameObject oldPath = null;
        if (pathObjectsCache.TryGetValue(path.path_id, out GameObject existingPath))
        {
            if (existingPath != null)
            {
                var existingIdInfo = existingPath.GetComponent<ObjectIdInfo>();
                int currentStatus = existingIdInfo != null ? existingIdInfo.status : 0;
                bool existingIsFar = existingIdInfo != null && existingIdInfo.isFarObject;
                // Verify this is a Far object to prevent accidental reuse of Near-detail objects.
                // When forceRebuild is true (changed version, potentially changed geometry), rebuild instead of reusing.
                if (!forceRebuild && currentStatus == path.status && existingIsFar)
                {
                    // Pool reuse: reshow a hidden object
                    if (!existingPath.activeSelf)
                        existingPath.SetActive(true);
                    SavePathToCache(path);
                    RegisterPathCells(path);
                    ReevaluateAwaitingIntersectionsForPath(path);
                    UpdatePlannedIcon(existingPath, path, path.status == Constants.STATUS_PENDING);
                    return;
                }
                oldPath = existingPath;
            }
            else
            {
                RemovePathObject(path.path_id);
            }
        }

        int status = path.status;
        GameObject pathGO = new GameObject($"{PATH_PREFIX}{path.path_id}");
        var idInfo = pathGO.AddComponent<ObjectIdInfo>();
        idInfo.id = path.path_id;
        idInfo.status = status;
        idInfo.isFarObject = true;
        Transform container = GetContainer("Paths");
        if (container != null) pathGO.transform.SetParent(container);
        
        pathGO.tag = PATH_TAG;

        var startPointList = path.GetStartPointList();
        var endPointList = path.GetEndPointList();

        if (startPointList == null || startPointList.Count < 2 || endPointList == null || endPointList.Count < 2)
        {
            Destroy(pathGO);
            return;
        }

        Vector3 startPos = GetPathPointPosition(startPointList);
        Vector3 endPos = GetPathPointPosition(endPointList);

        // Subtract base_height for 3D rendering (same shift as CalculatePathPoints).
        float farBaseHeight = GetPathBaseHeight(path.path_variant);
        startPos.y -= farBaseHeight;
        endPos.y -= farBaseHeight;

        float startRelY = (startPointList != null && startPointList.Count >= 3) ? startPointList[1] : 0f;
        float endRelY = (endPointList != null && endPointList.Count >= 3) ? endPointList[1] : 0f;

        // Do not snap endpoints to terrain for non-following paths (girder bridges, explicitly non-following paths, etc.).
        bool farVariantFollows = GetPathFollowTerrain(path);
        if (farVariantFollows && startRelY == farBaseHeight)
            startPos.y = Utils.GetGroundHeight(new Vector3(startPos.x, 1000f, startPos.z)) + RoadPartHeights.ROAD_SPLINE_OFFSET;
        if (farVariantFollows && endRelY == farBaseHeight)
            endPos.y = Utils.GetGroundHeight(new Vector3(endPos.x, 1000f, endPos.z)) + RoadPartHeights.ROAD_SPLINE_OFFSET;

        List<Vector3> points = new List<Vector3> { startPos, endPos };
        ApplyStationGrade(path, points);
        // For curves or paths with waypoints, preserve shape by thinning rendering points instead of using a straight endpoint line.
        bool hasWaypoints = (path.GetWaypointList()?.Count ?? 0) > 0;
        if (path.curved || hasWaypoints)
        {
            List<Vector3> curvePoints = CalculatePathPoints(path);
            if (curvePoints.Count >= 2)
            {
                points = DecimatePolyline(curvePoints, FAR_SAMPLE_INTERVAL);
            }
        }
        // Repeat the variant's Far model along points (PathFarModel), consistently across types and statuses.
        Mesh mesh = null;
        if (UsesTunnelMesh(path, points))
        {
            // Linear interpolation between thinned points deviates from terrain; split using the original centerline before thinning each section.
            List<Vector3> centerline = CalculateSectionCenterline(path, CalculatePathPoints(path));
            foreach (var section in TunnelSectionUtil.GetDrawSections(centerline, TunnelSectionUtil.GetClearanceHeight(path.path_variant),
                UndergroundView.IsActive))
            {
                GameObject sectionGO = PathFarModel.Create(pathGO, "PathSection", path.path_variant, DecimatePolyline(section.Points, FAR_SAMPLE_INTERVAL));
                if (sectionGO != null) sectionGO.AddComponent<PathSection>().IsUnderground = section.IsUnderground;
            }
        }
        else
        {
            mesh = PathFarModel.Build(path.path_variant, points, out Material[] farMaterials);
            if (mesh != null)
            {
                pathGO.AddComponent<MeshFilter>().mesh = mesh;
                MeshRenderer mr = pathGO.AddComponent<MeshRenderer>();
                mr.sharedMaterials = farMaterials;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        var identity = pathGO.AddComponent<RenderCacheIdentity>();
        identity.key = renderKey;
        identity.mesh = mesh;

        var stats = pathGO.AddComponent<VisualPathStats>();
        stats.InitFrom(path);
        var visual = pathGO.AddComponent<VisualModeObject>();
        visual.objectType = InputObjectType.PATH;
        visual.useChildRenderers = true;
        visual.SetStatus(status);
        visual.SetFilterHidden(ObjectVisibilityFilter.IsHidden(InputObjectType.PATH, path.path_id));

        SavePathToCache(path);
        RegisterPathCells(path);
        StampPathBuild(pathGO, path, buildRevision, buildSerial);
        UpdatePlannedIcon(pathGO, path, path.status == Constants.STATUS_PENDING);
        DrawLodInfo.Ensure(pathGO).drawLod = DrawLod.Far;
        DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Path,
            oldPath != null ? DrawLodInfo.GetDrawLod(oldPath, DrawLod.Normal) : (DrawLod?)null, DrawLod.Far);

        // Update the dictionary to the new object immediately.
        // This prevents duplicate objects if the same ID is redrawn again shortly afterward.
        SetPathObject(path.path_id, pathGO);

        // Fixed-section parts finish asynchronously; replace the previous display only after initial generation completes.
        if (oldPath != null)
        {
            PathPresentation.Replace(pathGO, oldPath, lodCache);
        }
        ReevaluateAwaitingIntersectionsForPath(path);
    }

    public List<PathSummary> GetAllPaths()
    {
        return pathDataCache.Values.ToList();
    }

    /// <summary>
    /// Enumerate rendering data for public transport paths (PATH_TRANSPORT).
    /// TransitOverlayManager uses this to draw installed public transport lines as type-colored bands.
    /// For cached PATH_TRANSPORT paths, return path_id, variant (section path variant,
    /// equal to transit_master transit_path_id), and terrain-following points.
    /// PATH_TRANSPORT exists in pathDataCache even without a mesh generator,
    /// so normal path point calculation (CalculatePathPoints) works unchanged.
    /// </summary>
    public List<(int pathId, int variant, List<Vector3> points)> GetTransitPathsForOverlay()
    {
        var result = new List<(int, int, List<Vector3>)>();
        foreach (var kvp in pathDataCache)
        {
            var path = kvp.Value;
            if (path == null || path.path_type != Constants.PATH_TRANSPORT) continue;
            // Filtering only STATUS_ACTIVE would hide planned sections,
            // so render all cached public transport paths regardless of status.
            var points = CalculatePathPoints(path);
            if (points == null || points.Count < 2) continue;
            result.Add((path.path_id, path.path_variant, points));
        }
        return result;
    }

    /// <summary>
    /// Return terrain-following points (centerline) for the specified path ID.
    /// TransitOverlayManager uses this to join actual underlying road/track geometry
    /// from route_path_ids into line rendering. Roads/tracks use Bezier sampling with curved=true.
    /// False if pathDataCache is not loaded (paths in cells not loaded yet).
    /// </summary>
    /// <summary>
    /// Reapply the per-object display filter (ObjectVisibilityFilter) to generated paths.
    /// Called when the hidden set changes. Unloaded paths resolve the filter during generation and are excluded.
    /// </summary>
    public void RefreshVisibilityFilter()
    {
        foreach (var pair in pathObjectsCache)
        {
            ObjectVisibilityFilter.ApplyToObject(pair.Value, InputObjectType.PATH, pair.Key);
        }
    }

    public bool TryGetPathPolyline(int pathId, out List<Vector3> points, bool trimIntersection = true)
    {
        points = null;
        if (!pathDataCache.TryGetValue(pathId, out PathSummary path) || path == null)
            return false;
        var pts = CalculatePathPoints(path, trimIntersection);
        if (pts == null || pts.Count < 2)
            return false;
        points = pts;
        return true;
    }

    /// <summary>
    /// Return an uncut centerline as read-only. If cached, return shared points without copying
    /// (avoids list allocation merely to read points in overlays redrawn each time). Do not modify.
    /// </summary>
    public bool TryGetUntrimmedPolylineShared(int pathId, out IReadOnlyList<Vector3> points)
    {
        points = null;
        if (!pathDataCache.TryGetValue(pathId, out PathSummary path) || path == null)
            return false;
        List<Vector3> pts = GetUntrimmedPoints(path, out _);
        if (pts == null || pts.Count < 2)
            return false;
        points = pts;
        return true;
    }

    /// <summary>Screen label center. Project the center point onto the rendered centerline, retaining elevated/sloped heights.</summary>
    public bool TryGetPathLabelPosition(int pathId, out Vector3 position)
    {
        if (pathLabelPositions.TryGetValue(pathId, out position)) return true;
        position = default;
        if (!pathDataCache.TryGetValue(pathId, out var path)) return false;
        var center = path.GetCenterPointArray();
        if (center == null || center.Length < 2) return false;
        // Old rendering points remain during Far switching, so after invalidation use shared geometry retrieval with the latest data.
        if (!TryGetPathPolyline(pathId, out var points)) return false;
        Vector3 target = new Vector3(center[0], 0f, center[1]);
        float nearest = float.PositiveInfinity;
        for (int i = 1; i < points.Count; i++)
        {
            Vector3 projected = PolylineUtils.ClosestPointOnSegmentXZ(target, points[i - 1], points[i]);
            float distance = Utils.XZDistanceSquared(projected, target);
            if (distance >= nearest) continue;
            nearest = distance;
            position = projected;
        }
        if (float.IsPositiveInfinity(nearest)) return false;
        pathLabelPositions[pathId] = position;
        return true;
    }

    /// <summary>Return road candidates in the render circle's bounding rectangle through the cell reverse index. Callers check exact circle intersections.</summary>
    public List<PathSummary> GetPathsNearCircle(Vector3 center, float radius)
    {
        float size = TerrainManager.CellSize;
        var ids = new HashSet<int>();
        var result = new List<PathSummary>();
        int minX = Mathf.FloorToInt((center.x - radius) / size), maxX = Mathf.FloorToInt((center.x + radius) / size);
        int minZ = Mathf.FloorToInt((center.z - radius) / size), maxZ = Mathf.FloorToInt((center.z + radius) / size);
        for (int x = minX; x <= maxX; x++)
        for (int z = minZ; z <= maxZ; z++)
        {
            if (!pathIdsByCell.TryGetValue(new Vector2Int(x, z), out var cellIds)) continue;
            foreach (int id in cellIds)
                if (ids.Add(id) && pathDataCache.TryGetValue(id, out var path)) result.Add(path);
        }
        return result;
    }

    public GameObject GetNodeObject(int nodeId)
    {
        if (nodeObjectsCache.TryGetValue(nodeId, out GameObject obj))
        {
            return obj;
        }
        return null;
    }

    /// <summary>
    /// Node intersection mesh GameObject (NodeMesh_* with Sidewalk / Rail / Signals children), or null.
    /// Separate from the node cylinder returned by GetNodeObject.
    /// </summary>
    public GameObject GetNodeIntersectionObject(int nodeId)
    {
        return nodeIntersectionMeshes.TryGetValue(nodeId, out GameObject obj) && obj != null ? obj : null;
    }

    /// <summary>
    /// Revision of the clipped rendering centerline (TryGetPathPolyline(trim=true) result). Always changes when the centerline changes.
    /// -1 if uncached; callers must fall back to comparing point sequences. Main thread only.
    /// </summary>
    public long GetPathGeometryRevision(int pathId)
    {
        if (!pathDataCache.TryGetValue(pathId, out PathSummary path)) return -1;
        PointsEntry entry = GetUntrimmedEntry(path);
        if (entry == null) return -1;
        connectionRevisions.TryGetValue(pathId, out int connection);
        return ((long)entry.revision << 32) | (uint)connection;
    }

    /// <summary>Add IDs of roads displaying actual objects (Far, Normal, or Close) to result, for exclusion from the citywide overview.</summary>
    public void CollectShownPathIds(HashSet<int> result)
    {
        foreach (var pair in pathObjectsCache)
            if (pair.Value != null && pair.Value.activeSelf) result.Add(pair.Key);
    }

    public GameObject GetPathObject(int pathId)
    {
        if (pathObjectsCache.TryGetValue(pathId, out GameObject obj))
        {
            return obj;
        }
        return null;
    }

    /// <summary>
    /// Get path data for the specified ID.
    /// Return null if absent from the cache.
    /// </summary>
    public PathSummary GetPathData(int pathId)
    {
        if (pathDataCache.TryGetValue(pathId, out PathSummary path))
        {
            return path;
        }
        return null;
    }

    /// <summary>
    /// Create, update, or delete the road planning icon.
    /// Display a 2D icon at center_point (accounting for terrain height) for pending roads (status=11).
    /// Manage it as a road object child, sharing the road's visibility timing.
    /// </summary>
    private void UpdatePlannedIcon(GameObject pathGO, PathSummary path, bool isPlanned)
    {
        Transform iconTrans = pathGO.transform.Find("PlannedIcon");
        if (!isPlanned)
        {
            if (iconTrans != null)
                Destroy(iconTrans.gameObject);
            return;
        }

        if (plannedPathIcon == null)
            return;

        GameObject iconGO;
        if (iconTrans == null)
        {
            iconGO = new GameObject("PlannedIcon");
            iconGO.transform.SetParent(pathGO.transform, false);
            SpriteRenderer sr = iconGO.AddComponent<SpriteRenderer>();
            sr.sprite = plannedPathIcon;
            sr.sortingOrder = 10;
            iconGO.AddComponent<Billboard>();
        }
        else
        {
            iconGO = iconTrans.gameObject;
            if (!iconGO.activeSelf)
                iconGO.SetActive(true);
        }

        float[] cp = path.GetCenterPointArray();
        Vector3 iconPos;
        if (cp != null && cp.Length >= 2)
        {
            float groundHeight = Utils.GetGroundHeight(new Vector3(cp[0], 1000f, cp[1]));
            iconPos = new Vector3(cp[0], groundHeight + plannedPathIconYOffset, cp[1]);
        }
        else
        {
            var startList = path.GetStartPointList();
            var endList = path.GetEndPointList();
            if (startList != null && startList.Count >= 2 && endList != null && endList.Count >= 2)
            {
                Vector3 start = new Vector3(startList[0], 0, startList[1]);
                Vector3 end = new Vector3(endList[0], 0, endList[1]);
                Vector3 mid = (start + end) * 0.5f;
                float groundHeight = Utils.GetGroundHeight(new Vector3(mid.x, 1000f, mid.z));
                iconPos = new Vector3(mid.x, groundHeight + plannedPathIconYOffset, mid.z);
            }
            else
            {
                iconPos = pathGO.transform.position + Vector3.up * plannedPathIconYOffset;
            }
        }

        iconGO.transform.position = iconPos;
        iconGO.transform.localScale = Vector3.one * plannedPathIconScale;
    }

    protected override void UnloadCell(Vector2Int cellIndex)
    {
        UnloadCellsObjects(new List<Vector2Int> { cellIndex });
    }

    private readonly HashSet<Vector2Int> unloadCellSet = new HashSet<Vector2Int>();

    /// <summary>
    /// Departing cell to registration generation, awaiting hiding. PathUnloadWorker processes within the frame budget.
    /// ApplyLodCellChanges removes returning cells. Retain pending entries while in cellsToKeepVisible, so even if
    /// the returning batch is canceled before application, the next departure can collect objects left visible.
    /// </summary>
    private readonly Dictionary<Vector2Int, int> pendingUnloadCells = new Dictionary<Vector2Int, int>();
    private int unloadGeneration;
    private Coroutine pathUnloadWorker;

    /// <summary>Reusable list for PathUnloadWorker to snapshot IDs for one cell, avoiding HashSet enumeration across yields.</summary>
    private readonly List<int> unloadScanPathIds = new List<int>();
    private readonly List<int> unloadScanNodeIds = new List<int>();
    private readonly List<int> unloadHiddenEndNodeIds = new List<int>();

    /// <summary>
    /// Queue the specified cells for incremental unloading. PathUnloadWorker hides nodes, paths, and intersection meshes
    /// within budget, retaining objects (SetActive(false) on hundreds of roads in one departing cell at once would cause spikes).
    /// Do not hide paths spanning other target cells (cellsToKeepVisible).
    /// This prevents roads crossing cell boundaries from briefly disappearing on camera movement.
    /// GameObjects without crossed-cell registration cannot be found through the index, so hide synchronously here (normally almost empty).
    /// </summary>
    private void UnloadCellsObjects(List<Vector2Int> cells)
    {
        if (cells == null || cells.Count == 0) return;
        unloadCellSet.Clear();
        foreach (var cell in cells)
        {
            if (!cellsToKeepVisible.Contains(cell)) unloadCellSet.Add(cell);
        }
        if (unloadCellSet.Count == 0) return;

        foreach (var cell in unloadCellSet) pendingUnloadCells[cell] = ++unloadGeneration;
        if (pathUnloadWorker == null)
            pathUnloadWorker = RenderWorkScheduler.Start(this, PathUnloadWorker(), "PathManager", () => false);

        // Collect GameObjects without crossed-cell registration before hiding, rather than hiding while enumerating the dictionary.
        List<Vector2Int> unloadCells = null;
        List<Vector2Int> keepCells = null;
        List<int> unregisteredToHide = null;
        foreach (int pathId in unregisteredPathObjectIds)
        {
            if (!pathObjectsCache.TryGetValue(pathId, out GameObject pathGO) || pathGO == null || !pathGO.activeSelf) continue;
            unloadCells ??= new List<Vector2Int>(unloadCellSet);
            keepCells ??= new List<Vector2Int>(cellsToKeepVisible);
            if (IsPathInCells(pathId, pathGO, unloadCells) && !IsPathInCells(pathId, pathGO, keepCells))
                (unregisteredToHide ??= new List<int>()).Add(pathId);
        }
        unloadCellSet.Clear();
        if (unregisteredToHide == null) return;
        List<int> hiddenEndNodeIds = null;
        foreach (int pathId in unregisteredToHide) HideUnloadedPath(pathId, pathObjectsCache[pathId], ref hiddenEndNodeIds);
        if (hiddenEndNodeIds != null)
        {
            foreach (int nodeId in hiddenEndNodeIds) HideIntersectionMeshIfOrphaned(nodeId);
        }
    }

    /// <summary>Whether a queued cell is still eligible for hiding (same registration generation and still nonresident).</summary>
    private bool IsUnloadCellCurrent(Vector2Int cell, int generation)
        => pendingUnloadCells.TryGetValue(cell, out int current) && current == generation && !cellsToKeepVisible.Contains(cell);

    /// <summary>
    /// Persistent worker hiding nodes, paths, and intersections in departing cells. Snapshot IDs per cell and split work by frame budget.
    /// On every resumption, check for residency return or reregistration; discard snapshots if changed.
    /// Immediately before hiding paths, check current cellsToKeepVisible (avoid hiding objects reshown by later batches while waiting).
    /// </summary>
    private IEnumerator PathUnloadWorker()
    {
        var watch = new System.Diagnostics.Stopwatch();
        while (true)
        {
            Vector2Int cell = default;
            int generation = 0;
            bool found = false;
            foreach (var pair in pendingUnloadCells)
            {
                if (cellsToKeepVisible.Contains(pair.Key)) continue;
                cell = pair.Key;
                generation = pair.Value;
                found = true;
                break;
            }
            if (!found)
            {
                yield return null;
                continue;
            }

            unloadScanPathIds.Clear();
            unloadScanNodeIds.Clear();
            unloadHiddenEndNodeIds.Clear();
            if (pathIdsByCell.TryGetValue(cell, out HashSet<int> pathIds)) unloadScanPathIds.AddRange(pathIds);
            if (nodeIdsByCell.TryGetValue(cell, out HashSet<int> nodeIds)) unloadScanNodeIds.AddRange(nodeIds);
            List<int> hiddenEndNodeIds = unloadHiddenEndNodeIds;

            bool interrupted = false;
            int step = 0;
            watch.Restart();
            for (int phase = 0; phase < 3 && !interrupted; phase++)
            {
                List<int> ids = phase == 1 ? unloadScanPathIds : unloadScanNodeIds;
                for (int i = 0; i < ids.Count; i++)
                {
                    int id = ids[i];
                    if (phase == 0)
                    {
                        if (nodeObjectsCache.TryGetValue(id, out GameObject nodeGO) && nodeGO != null && nodeGO.activeSelf) nodeGO.SetActive(false);
                    }
                    else if (phase == 1)
                    {
                        if (pathObjectsCache.TryGetValue(id, out GameObject pathGO) && pathGO != null && pathGO.activeSelf
                            && !(pathCellsById.TryGetValue(id, out HashSet<Vector2Int> pathCells) && AnyCellIn(pathCells, cellsToKeepVisible)))
                            HideUnloadedPath(id, pathGO, ref hiddenEndNodeIds);
                    }
                    else
                    {
                        // Intersections are not road GameObject children; hide them when all connected roads are hidden.
                        // Retain if even one road is visible so intersections remain at visible road ends.
                        if (nodeIntersectionMeshes.TryGetValue(id, out GameObject meshGO) && meshGO != null && meshGO.activeSelf
                            && !HasVisibleConnectedPath(id))
                            meshGO.SetActive(false);
                    }
                    if ((++step & 15) == 0 && watch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                    {
                        yield return RenderWorkScheduler.Continue;
                        watch.Restart();
                        if (!IsUnloadCellCurrent(cell, generation))
                        {
                            interrupted = true;
                            break;
                        }
                    }
                }
            }
            // Hide intersections in other cells too if their last connected road was hidden here. Even when interrupted, finish work for hidden roads
            // (a rescan will not find those hidden roads).
            foreach (int nodeId in unloadHiddenEndNodeIds) HideIntersectionMeshIfOrphaned(nodeId);
            unloadScanPathIds.Clear();
            unloadScanNodeIds.Clear();
            unloadHiddenEndNodeIds.Clear();
            if (!interrupted && IsUnloadCellCurrent(cell, generation))
                pendingUnloadCells.Remove(cell);
            yield return RenderWorkScheduler.Continue;
        }
    }

    private static bool AnyCellIn(HashSet<Vector2Int> cells, HashSet<Vector2Int> set)
    {
        foreach (var cell in cells)
        {
            if (set.Contains(cell)) return true;
        }
        return false;
    }

    private void HideUnloadedPath(int pathId, GameObject pathGO, ref List<int> hiddenEndNodeIds)
    {
        pathGO.SetActive(false);
        anchorPolylineCache.Remove(pathId);
        DropUntrimmedPoints(pathId);
        if (pathDataCache.TryGetValue(pathId, out PathSummary hiddenPath))
        {
            hiddenEndNodeIds ??= new List<int>();
            hiddenEndNodeIds.Add(hiddenPath.start_node_id);
            hiddenEndNodeIds.Add(hiddenPath.end_node_id);
        }
    }

    private void HideIntersectionMeshIfOrphaned(int nodeId)
    {
        if (nodeIntersectionMeshes.TryGetValue(nodeId, out GameObject meshGO) && meshGO != null && meshGO.activeSelf
            && !HasVisibleConnectedPath(nodeId))
            meshGO.SetActive(false);
    }

    /// <summary>Whether any road connected to the node has a visible (active) GameObject.</summary>
    private bool HasVisibleConnectedPath(int nodeId)
    {
        if (!pathsByNodeIndex.TryGetValue(nodeId, out var pathIds)) return false;
        foreach (int pathId in pathIds)
        {
            if (pathObjectsCache.TryGetValue(pathId, out GameObject pathGO) && pathGO != null && pathGO.activeSelf) return true;
        }
        return false;
    }

    /// <summary>
    /// Reshow endpoint intersections hidden during unloading. Always call from road reshow paths
    /// and road generation/restoration entry points (CreateOrUpdatePath / CreateOrUpdateFarPath).
    /// </summary>
    private void EnsureEndIntersectionsVisible(PathSummary path)
    {
        if (path == null) return;
        ShowIntersectionMesh(path.start_node_id);
        ShowIntersectionMesh(path.end_node_id);
    }

    private void ShowIntersectionMesh(int nodeId)
    {
        if (nodeIntersectionMeshes.TryGetValue(nodeId, out GameObject meshGO) && meshGO != null && !meshGO.activeSelf)
            meshGO.SetActive(true);
    }

    /// <summary>
    /// Node generation
    /// </summary>
    /// <summary>
    /// Actual change counter for node caches (nodeDataCache / nodeGrid).
    /// Increment only on node addition, actual position/type changes, or removal
    /// (most camera cell-crossing refetches are unchanged, so do not increment unconditionally).
    /// RoadPlanManager compares this by polling to decide when to rebuild background overlays (existing nodes).
    /// </summary>
    public int NodeDataVersion { get; private set; }

    /// <summary>
    /// Add or update a node.
    /// Nodes are assumed stationary after creation; if both cached data and an object
    /// already exist, skip processing to improve performance.
    /// </summary>
    public void CreateOrUpdateNode(NodeData node)
    {
        node = RegisterNodeData(node, out bool positionChanged);

        GameObject existingNode = GetNodeObject(node.node_id);
        if (existingNode != null)
        {
            if (positionChanged)
            {
                Vector3 newAbsolutePos = node.GetAbsolutePosition();
                newAbsolutePos.y += 0.1f;
                existingNode.transform.position = newAbsolutePos;
            }
            
            if (existingNode.activeSelf != ShowNodes)
            {
                existingNode.SetActive(ShowNodes);
            }
            if (deferredNewNodeIntersectionIds.Contains(node.node_id)) UpdateOrDeferNodeIntersection(node.node_id);
            return;
        }

        Vector3 absolutePos = node.GetAbsolutePosition();
        absolutePos.y += 0.1f; 

        int status = Constants.STATUS_ACTIVE; 
        GameObject nodeGO = CreateDefaultNodeVisual(node.node_id, status, absolutePos);
        
        // Attach NodeInputHandler for dragging to infrastructure facility terminal nodes,
        // and set their role. Identify terminals through the reverse index BuildingManager builds
        // from building agents' infra_node_id (the node API does not expose owner facilities,
        // using one-way references. CellDataLoader loads Building before Path,
        // so the building reverse index is normally ready when nodes arrive).
        if (InfraNetworkUtil.IsInfraMask(node.type)
            && BuildingManager.Instance != null
            && BuildingManager.Instance.TryGetInfraTerminalOwner(node.node_id, out _))
        {
            var handler = nodeGO.AddComponent<NodeInputHandler>();
            handler.nodeId = node.node_id;
            
            var idInfo = nodeGO.GetComponent<ObjectIdInfo>();
            if (idInfo != null)
            {
                idInfo.role = "infra_terminal";
            }
        }
        
        Transform container = GetContainer("Nodes");
        if (container != null) nodeGO.transform.SetParent(container);
        
        nodeGO.SetActive(ShowNodes);
        nodeObjectsCache[node.node_id] = nodeGO;

        UpdateOrDeferNodeIntersection(node.node_id);
    }

    /// <summary>
    /// Register node data in nodeDataCache, nodeGrid, and the cell index together; return the cached instance.
    /// Snapping and existing-node candidates read only nodeGrid, so all node reception paths
    /// (cell-range fetching and individual intersection fetches for road-end nodes outside the visible range) must pass here.
    /// For registered nodes, update position/type on the same instance (nodeGrid removes by reference, so do not replace it).
    /// </summary>
    private NodeData RegisterNodeData(NodeData node, out bool positionChanged)
    {
        positionChanged = false;
        if (nodeDataCache.TryGetValue(node.node_id, out NodeData cachedNode))
        {
            // Position or type may have changed; update data if either differs.
            positionChanged = Vector3.Distance(cachedNode.GetAbsolutePosition(), node.GetAbsolutePosition()) > 0.001f;
            if (positionChanged)
            {
                nodeGrid?.Remove(cachedNode, cachedNode.GetAbsolutePosition());

                // Update the position array
                if (cachedNode.position != null && cachedNode.position.Length >= 3)
                {
                    cachedNode.position[0] = node.position[0];
                    cachedNode.position[1] = node.position[1];
                    cachedNode.position[2] = node.position[2];
                }
                else
                {
                    cachedNode.position = new float[] { node.position[0], node.position[1], node.position[2] };
                }

                nodeGrid?.Add(cachedNode, cachedNode.GetAbsolutePosition());
            }
            if (positionChanged || cachedNode.type != node.type || cachedNode.station_path_id != node.station_path_id)
            {
                NodeDataVersion++;
            }
            cachedNode.type = node.type;
            cachedNode.station_path_id = node.station_path_id;
            node = cachedNode;
        }
        else
        {
            nodeDataCache[node.node_id] = node;
            Vector3 pos = node.GetAbsolutePosition();
            nodeGrid?.Add(node, pos);
            NodeDataVersion++;
        }
        IndexNodeCell(node.node_id);
        return node;
    }

    /// <summary>
    /// Update intersections for received nodes. During LOD batches connected roads may not have arrived,
    /// so defer calculation to the batch intersection phase (UpdateDeferredLodIntersections).
    /// After cancellation, recover through node reception (deferredNewNodeIntersectionIds) and road arrival (awaitingIntersectionNodes).
    /// </summary>
    private void UpdateOrDeferNodeIntersection(int nodeId)
    {
        if (isLodPathBatchActive)
        {
            deferredLodIntersectionNodeIds.Add(nodeId);
            awaitingIntersectionNodes.Add(nodeId);
            deferredNewNodeIntersectionIds.Add(nodeId);
            return;
        }
        UpdateNodeIntersectionMesh(nodeId);
    }

    /// <summary>
    /// Get node data for the specified ID.
    /// </summary>
    public NodeData GetNodeData(int nodeId)
    {
        if (nodeDataCache.TryGetValue(nodeId, out NodeData node))
        {
            return node;
        }
        return null;
    }

    /// <summary>
    /// Update cached node position and rebuild the spatial index (nodeGrid).
    /// Called after node dragging completes.
    /// </summary>
    public void UpdateCachedNodePosition(int nodeId, Vector3 newPosition)
    {
        if (nodeDataCache.TryGetValue(nodeId, out NodeData node))
        {
            Vector3 oldPos = node.GetAbsolutePosition();
            nodeGrid?.Remove(node, oldPos);
            
            if (node.position != null && node.position.Length >= 3)
            {
                node.position[0] = newPosition.x;
                node.position[1] = newPosition.y;
                node.position[2] = newPosition.z;
            }
            else
            {
                node.position = new float[] { newPosition.x, newPosition.y, newPosition.z };
            }
            
            nodeGrid?.Add(node, node.GetAbsolutePosition());
            Debug.Log($"PathManager: キャッシュ内のノード {nodeId} の位置を更新しました: {newPosition}");
        }
    }

    private GameObject CreateDefaultNodeVisual(int nodeId, int status, Vector3 position)
    {
        GameObject nodeGO = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        nodeGO.name = $"{NODE_PREFIX}{nodeId}";
        nodeGO.tag = NODE_TAG;
        nodeGO.transform.position = position;

        var idInfo = nodeGO.AddComponent<ObjectIdInfo>();
        idInfo.id = nodeId;
        idInfo.status = status;
        
        nodeGO.transform.localScale = new Vector3(0.5f, 0.01f, 0.5f);
        
        Material nodeMat = GetNodeDepthMaterial();
        if (nodeMat != null) nodeGO.GetComponent<Renderer>().sharedMaterial = nodeMat;

        return nodeGO;
    }

    /// <summary>
    /// Return cached paths ending at the specified node (O(1) reverse lookup through pathsByNodeIndex).
    /// Used for intersection generation and infrastructure node network-type checks (InfraNetworkUtil).
    /// </summary>
    public List<PathSummary> GetPathsConnectedToNode(int nodeId)
    {
        List<PathSummary> connectedPaths = new List<PathSummary>();
        if (pathsByNodeIndex.TryGetValue(nodeId, out HashSet<int> pathIds))
        {
            foreach (int pathId in pathIds)
            {
                if (pathDataCache.TryGetValue(pathId, out PathSummary path))
                {
                    connectedPaths.Add(path);
                }
            }
        }
        return connectedPaths;
    }

    // ================================================================
    // Support for combined adjacent-track rendering (TrackAdjacency)
    // ================================================================

    /// <summary>Whether the path type supports combined rendering (track/monorail).</summary>
    private static bool IsTrackTypePath(PathSummary path)
    {
        return path != null && (path.path_type == Constants.PATH_TRACK || path.path_type == Constants.PATH_MONORAIL);
    }

    /// <summary>
    /// Return IDs of nearby candidate tracks (same-type tracks other than self registered in crossed and neighboring cells).
    /// This is coarse cell-based filtering rather than a parallelism test;
    /// TrackAdjacency.Compute performs geometric checks.
    /// Include neighboring cells to catch parallel pairs across cell boundaries.
    /// </summary>
    private List<int> CollectNearbyTrackPathIds(PathSummary path)
    {
        var result = new List<int>();
        if (!IsTrackTypePath(path)) return result;

        var seen = new HashSet<int>();
        foreach (var cell in GetPathCoveredCellsCached(path))
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (!pathIdsByCell.TryGetValue(new Vector2Int(cell.x + dx, cell.y + dy), out HashSet<int> ids)) continue;
                    foreach (int id in ids)
                    {
                        if (id == path.path_id || !seen.Add(id)) continue;
                        if (!pathDataCache.TryGetValue(id, out PathSummary other)) continue;
                        // Do not combine paths of different types, such as tracks and monorails.
                        // LRT dedicated tracks use a separate double-track system, so do not combine with ordinary rail sharing PATH_TRACK.
                        if (other.path_type != path.path_type) continue;
                        if (LrtRoadParts.IsLrt(other.path_variant) != LrtRoadParts.IsLrt(path.path_variant)) continue;
                        result.Add(id);
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Implementation of TrackAdjacency.NeighborProvider. Return nearby track rendering points and widths,
    /// station platform left/right extents and edge lines (for island platform joining), or LRT
    /// stop platform sections (to move main-track catenary poles outside platforms), plus outward S-curve displacement at joins.
    /// Use cached pathPointsCache if generated; otherwise calculate with CalculatePathPoints
    /// when the neighbor has not been rendered yet in the same load, for example.
    /// Return unfinished tracks (planned, awaiting construction, under construction) and placement previews as provisional partners (TrackNeighborInfo.Provisional).
    /// For constructing tracks, return only completed sections (avoid removing completed-side railings before girders exist).
    /// Exclude plans being edited to avoid duplicating their previews.
    /// </summary>
    public List<TrackNeighborInfo> GetAdjacentTrackNeighbors(PathSummary path) => BuildAdjacentTrackNeighbors(path,true);

    private List<TrackNeighborInfo> BuildAdjacentTrackNeighbors(PathSummary path, bool trimIntersection)
    {
        var result = new List<TrackNeighborInfo>();
        foreach (int id in CollectNearbyTrackPathIds(path))
        {
            if (previewExcludedPathId > 0 && id == previewExcludedPathId) continue;
            if (!pathDataCache.TryGetValue(id, out PathSummary other)) continue;

            List<Vector3> points;
            if (!trimIntersection || !pathPointsCache.TryGetValue(id, out points) || points == null || points.Count < 2)
            {
                points = CalculatePathPoints(other,trimIntersection);
            }
            if (points == null || points.Count < 2) continue;
            if (other.status == Constants.STATUS_CONSTRUCTING)
            {
                points = PathDrawContext.SlicePolylineByFraction(points, 0f, ConstructionPathBuilder.BuiltFraction(other, points));
                if (points == null || points.Count < 2) continue;
            }
            result.Add(BuildTrackNeighborInfo(other, points));
        }
        // Placement preview tracks (excluding self and sections of the same line). Add separately because previews are not in the cell table.
        bool lrt = LrtRoadParts.IsLrt(path.path_variant);
        foreach (PathSummary preview in previewTracks)
        {
            if (preview.path_id == path.path_id || preview.path_type != path.path_type || LrtRoadParts.IsLrt(preview.path_variant) != lrt) continue;
            if (previewTrackPoints.TryGetValue(preview.path_id, out var points)) result.Add(BuildTrackNeighborInfo(preview, points));
        }
        return result;
    }

    /// <summary>Information for one adjacent candidate (GetAdjacentTrackNeighbors).</summary>
    private TrackNeighborInfo BuildTrackNeighborInfo(PathSummary other, List<Vector3> points)
    {
        // Resolve station platform extents to geometric left/right (point sequence start-to-end direction).
        // Resolve inner/outer to left/right (ResolvePlatformExtents) here before passing,
        // so TrackPath can use the same convention as its own platforms.
        float platformLeft = 0f;
        float platformRight = 0f;
        var otherMaster = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(other.path_variant) : null;
        if (otherMaster != null && otherMaster.platform_width.HasValue && otherMaster.platform_width.Value > 0f)
        {
            TrackPath.ResolvePlatformExtents(other, otherMaster.platform_width.Value, out platformLeft, out platformRight);
        }

        // LRT stop platform sections, using the same index/points as the neighbor's rendering and 3D cumulative distance along those points.
        List<Vector2> lrtZones = null;
        if (LrtRoadParts.IsLrt(other.path_variant))
        {
            var lrtSpans = LrtPlatformParts.Resolve(other, points, new VehiclePolyline(points).Length);
            if (lrtSpans.Count > 0)
                lrtZones = LrtPlatformParts.ZoneRanges(lrtSpans, LrtRoadParts.PlatformRampLength(other.path_variant), LrtRoadParts.PLATFORM_TRACK_ZONE_MARGIN);
        }

        // Sections where join S-curves move ballast/platform outer edges (included in neighbor extents for joining).
        TrackLateralProfile profile = GetTrackLateralProfile(other);
        float[] outerShifts = profile != null && !profile.IsUniform ? new TrackLateralSampler(profile, other, points).OuterShiftsAtPoints() : null;

        float width = GetRoadWidth(other.path_variant);
        return new TrackNeighborInfo
        {
            PathId = other.path_id,
            Points = points,
            Width = width,
            OuterShifts = outerShifts,
            Provisional = TrackAdjacency.IsProvisional(other),
            PlatformLeft = platformLeft,
            PlatformRight = platformRight,
            PlatformEdge = TrackPath.PlatformEdgeDistance(width, Mathf.Max(1, otherMaster?.lanes ?? 1), otherMaster?.center_width ?? 0f),
            LrtPlatformZones = lrtZones,
            LrtPlatformOuter = lrtZones != null ? LrtRoadParts.PlatformOuterOffset(other) : 0f,
        };
    }

    /// <summary>
    /// Queue nearby tracks for regeneration when a track's data changes (creation, version change, deletion).
    /// Do nothing for non-track targets. Flush immediately outside LOD batches.
    /// Regeneration (TryRebuildPathOnce) does not change data, so this cannot cascade.
    /// </summary>
    private void EnqueueAdjacentTrackNeighborRebuilds(PathSummary path)
    {
        if (!IsTrackTypePath(path)) return;

        foreach (int id in CollectNearbyTrackPathIds(path))
        {
            QueueTrackNeighborRebuild(id);
        }
        FlushPendingTrackNeighborRebuilds(null);
    }

    /// <summary>
    /// Queue nearby-track rebuilds and advance render revisions immediately, preventing tracks outside the view
    /// from later restoring parked GameObjects with stale joining state (same as pillar/power-line notifications).
    /// </summary>
    private void QueueTrackNeighborRebuild(int id)
    {
        if (!pendingTrackNeighborRebuildIds.Add(id)) return;
        geometryRevisions.TryGetValue(id, out int revision);
        geometryRevisions[id] = revision + 1;
    }

    /// <summary>
    /// After track registration, changes, or removal, notify depots if endpoint nodes are depot terminals.
    /// Depot sidings become single or double track depending on the track count connecting to terminals
    /// (BuildingManager.OnDepotTerminalPathsChanged; no rebuild when unchanged).
    /// On removal, call after removing from pathsByNodeIndex.
    /// </summary>
    private static void NotifyDepotTerminals(PathSummary path)
    {
        BuildingManager buildings = BuildingManager.Instance;
        if (!IsTrackTypePath(path) || buildings == null) return;
        if (buildings.IsDepotTerminalNode(path.start_node_id)) buildings.OnDepotTerminalPathsChanged(path.start_node_id);
        if (buildings.IsDepotTerminalNode(path.end_node_id)) buildings.OnDepotTerminalPathsChanged(path.end_node_id);
    }

    /// <summary>
    /// Transfer nearby-track regeneration requests to the terrain-following queue. Do nothing during LOD batches
    /// (called by EndLodPathBatch at completion, excluding paths already built in that batch).
    /// TerrainFollowRebuildWorker performs actual regeneration within the frame budget, prioritizing the view.
    /// </summary>
    private void FlushPendingTrackNeighborRebuilds(HashSet<int> excludeIds)
    {
        if (isLodPathBatchActive || pendingTrackNeighborRebuildIds.Count == 0) return;

        foreach (int id in pendingTrackNeighborRebuildIds)
        {
            if (excludeIds != null && excludeIds.Contains(id)) continue;
            EnqueueTerrainFollowPath(id, pathBuildSerialCounter, TerrainFollowQueueReason.Other);
        }
        pendingTrackNeighborRebuildIds.Clear();
    }

    // ================================================================
    // Support for combined highway main-line/ramp rendering (RoadAdjacency)
    // ================================================================

    /// <summary>
    /// Nearby highway candidates (registered in crossed and neighboring cells)
    /// whose ramp status matches wantRamps. RoadAdjacency.Compute performs geometric checks.
    /// </summary>
    private List<int> CollectNearbyRoadMergeIds(PathSummary path, bool wantRamps)
    {
        var result = new List<int>();
        if (!RoadAdjacency.IsMotorway(path)) return result;
        // New data being generated may not have registered crossed cells; recalculate only in that case.
        bool registered = pathDataCache.TryGetValue(path.path_id, out PathSummary current) && ReferenceEquals(current, path);
        var seen = new HashSet<int>();
        foreach (var cell in registered ? GetPathCoveredCellsCached(path) : GetPathCoveredCells(path))
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (!pathIdsByCell.TryGetValue(new Vector2Int(cell.x + dx, cell.y + dy), out HashSet<int> ids)) continue;
                    foreach (int id in ids)
                    {
                        if (id == path.path_id || !seen.Add(id)) continue;
                        if (!pathDataCache.TryGetValue(id, out PathSummary other)) continue;
                        if (RoadAdjacency.IsMotorway(other) && RoadAdjacency.IsRamp(other) == wantRamps) result.Add(id);
                    }
                }
        return result;
    }

    /// <summary>
    /// Implementation of RoadAdjacency.NeighborProvider. Return active nearby ramps for main lines and main lines for ramps.
    /// Points are uncut rendering points; ramps have heights adjusted toward main lines (main-line points do not depend on ramps).
    /// Exclude roads under construction because they do not draw finished surfaces.
    /// </summary>
    public List<RoadNeighborInfo> GetAdjacentRoadNeighbors(PathSummary path)
    {
        var result = new List<RoadNeighborInfo>();
        if (!RoadAdjacency.IsMotorway(path)) return result;
        foreach (int id in CollectNearbyRoadMergeIds(path, !RoadAdjacency.IsRamp(path)))
        {
            if (!pathDataCache.TryGetValue(id, out PathSummary other) || other.status != Constants.STATUS_ACTIVE) continue;
            var points = GetUntrimmedPoints(other, out _);
            if (points == null || points.Count < 2) continue;
            result.Add(new RoadNeighborInfo { PathId = id, Points = points, Width = GetRoadWidth(other.path_variant) });
        }
        return result;
    }

    /// <summary>
    /// Rebuild potential joining partners near highways with changed data (creation, version change, deletion).
    /// Main-line changes affect ramp rendering heights. Ramp point caches and both endpoint intersection input signatures include the main-line signature
    /// (ComputeRoadMergeSignature), so add ramp endpoint nodes to affectedNodeIds here for recalculation
    /// (the caller updates intersections after registering/removing the changed path).
    /// Call before UnregisterPathCells because it reads crossed-cell caches.
    /// </summary>
    private void EnqueueAdjacentRoadMergeRebuilds(PathSummary path, ICollection<int> affectedNodeIds)
    {
        if (!RoadAdjacency.IsMotorway(path)) return;
        bool ramp = RoadAdjacency.IsRamp(path);
        foreach (int id in CollectNearbyRoadMergeIds(path, !ramp))
        {
            if (!pathDataCache.TryGetValue(id, out PathSummary other)) continue;
            pendingTrackNeighborRebuildIds.Add(id);
            geometryRevisions.TryGetValue(id, out int revision);
            geometryRevisions[id] = revision + 1;
            if (ramp) continue;
            InvalidateTerrainFollowPath(other);
            if (other.start_node_id > 0) affectedNodeIds.Add(other.start_node_id);
            if (other.end_node_id > 0) affectedNodeIds.Add(other.end_node_id);
        }
        FlushPendingTrackNeighborRebuilds(null);
    }

    /// <summary>Endpoints where main lines continue through nodes without intersection plates (MotorwayMergeLayout). Keyed by RoadEndKey.</summary>
    private readonly Dictionary<long, RoadEndJoin> roadEndJoins = new Dictionary<long, RoadEndJoin>();

    private static long RoadEndKey(int pathId, bool start) => ((long)pathId << 1) | (start ? 1L : 0L);

    private RoadEndJoin GetRoadEndJoin(PathSummary path, bool start)
        => path != null && roadEndJoins.TryGetValue(RoadEndKey(path.path_id, start), out var join) ? join : default;

    private void DropRoadEndJoins(int pathId)
    {
        roadEndJoins.Remove(RoadEndKey(pathId, true));
        roadEndJoins.Remove(RoadEndKey(pathId, false));
    }

    /// <summary>Update endpoint treatment from node calculation results; true if changed (road rebuild required).</summary>
    private bool SetRoadEndJoin(PathSummary path, int nodeId, RoadEndJoin join)
    {
        long key = RoadEndKey(path.path_id, path.start_node_id == nodeId);
        roadEndJoins.TryGetValue(key, out var current);
        if (current.Continuous == join.Continuous && current.OwnsSupport == join.OwnsSupport) return false;
        if (join.Continuous) roadEndJoins[key] = join;
        else roadEndJoins.Remove(key);
        return true;
    }

    /// <param name="drawLod">Render LOD (Normal / Close). CreateOrUpdateFarPath handles Far.</param>
    /// <param name="detailFrom">Start of the detailed-parts section (fraction of full length). Fill outside with Far models.</param>
    /// <param name="detailTo">End of the detailed-parts section.</param>
    public void CreateOrUpdatePath(PathSummary path, bool updateIntersections = true, bool forceRebuild = false, DrawLod drawLod = DrawLod.Normal, double detailFrom = 0.0, double detailTo = 1.0)
    {
        using var scope = createOrUpdateMarker.Auto();
        EnsureEndIntersectionsVisible(path);
        if (drawLod == DrawLod.Far || drawLod == DrawLod.Hidden) drawLod = DrawLod.Normal;
        string renderKey = PathRenderKey(path, drawLod, forceRebuild);
        if (RestorePathLod(path, renderKey, drawLod, updateIntersections)) return;
        geometryRevisions.TryGetValue(path.path_id, out int buildRevision);
        long buildSerial = ++pathBuildSerialCounter;
        forceRebuild = true;
        GameObject oldPath = null;
        if (pathObjectsCache.TryGetValue(path.path_id, out GameObject existingPath))
        {
             if (existingPath != null && !forceRebuild)
             {
                  var existingIdInfo = existingPath.GetComponent<ObjectIdInfo>();
                  int currentStatus = existingIdInfo != null ? existingIdInfo.status : 0;
                  bool existingIsFar = existingIdInfo != null && existingIdInfo.isFarObject;
                  // Verify this is not Far to avoid reusing simplified objects. Render LOD and detailed section must also match.
                   if (currentStatus == path.status && !existingIsFar && pathDataCache.ContainsKey(path.path_id)
                       && DrawLodMatches(existingPath, drawLod, detailFrom, detailTo))
                  {
                      // Pool reuse: reshow a hidden object
                      if (!existingPath.activeSelf)
                          existingPath.SetActive(true);
                      SavePathToCache(path);
                      RegisterPathCells(path);
                      ReevaluateAwaitingIntersectionsForPath(path);

                      // Update VisualPathStats on reuse too, reflecting changes such as zone_id
                      var stats = existingPath.GetComponent<VisualPathStats>();
                      if (stats != null)
                      {
                          stats.InitFrom(path);
                      }
                      UpdatePlannedIcon(existingPath, path, path.status == Constants.STATUS_PENDING);
                      return;
                  }
                 // On tier/status changes, generate the new mesh before destroying the old one.
                 // This prevents brief disappearance (popping) between Destroy and Instantiate.
                 oldPath = existingPath;
             }
             else if (existingPath != null)
             {
                 oldPath = existingPath;
             }
        }

        int status = path.status;
        GameObject pathGO = new GameObject($"{PATH_PREFIX}{path.path_id}");
        var idInfo = pathGO.AddComponent<ObjectIdInfo>();
        idInfo.id = path.path_id;
        idInfo.status = status;
        idInfo.isFarObject = false;
        Transform container = GetContainer("Paths");
        if (container != null) pathGO.transform.SetParent(container);

        pathGO.tag = PATH_TAG;

        List<Vector3> points = CalculatePathPoints(path);

        if (points.Count < 2)
        {
            Debug.LogWarning($"PathManager: パス {path.path_id} の点が不足しています。メッシュ生成をスキップします。");
            Destroy(pathGO);
            return;
        }

        MeshFilter mf = pathGO.AddComponent<MeshFilter>();

        // Pass paths containing underground sections to the same generator per visible section.
        // Include fully buried sections only in underground view and place portal parts at boundaries (tunnel mouths).
        // During construction, assemble earthworks, pillars, and completed sections according to progress.
        // However, model_type=none paths (such as public transport lines, unrendered even when complete) remain hidden during construction.
        // Pass render LOD and detailed sections to generators through PathDrawContext. Detailed sections are fractions of full path length,
        // so do not apply to section-split generation (tunnels/construction); render all sections at the same detail.
        PathMeshResult meshResult;
        bool usePartialRange = false;
        var genWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (PowerLinePath.Handles(path))
            {
                PathDrawContext.Begin(drawLod);
                PathDrawContext.PathVariant = path.path_variant;
                meshResult = PathMeshFactory.GeneratePathMesh(path, points);
            }
            else if (status == Constants.STATUS_CONSTRUCTING && PathMeshFactory.GetModelType(path) != "none")
            {
                PathDrawContext.Begin(drawLod);
                PathDrawContext.PathVariant = path.path_variant;
                meshResult = GenerateConstructingPathMesh(path, points);
            }
            else if (UsesTunnelMesh(path, points))
            {
                // Do not apply sidewalk setbacks to paths containing tunnel sections because rendering is split into sections
                // (underground road intersection end caps are currently out of scope).
                PathDrawContext.Begin(drawLod);
                PathDrawContext.PathVariant = path.path_variant;
                meshResult = GenerateSectionedPathMesh(path, points);
            }
            else
            {
                PathDrawContext.Begin(drawLod, detailFrom, detailTo);
                PathDrawContext.PathVariant = path.path_variant;
                pathStartSidewalkSetbacks.TryGetValue(path.path_id, out SidewalkSetback startSetback);
                pathEndSidewalkSetbacks.TryGetValue(path.path_id, out SidewalkSetback endSetback);
                meshResult = PathMeshFactory.GeneratePathMesh(path, points, startSetback, endSetback);
                usePartialRange = PathDrawContext.HasDetailRange;
            }
        }
        finally
        {
            PathDrawContext.End();
        }
        DrawLodStats.AddGenerationMs(DrawLodStats.Category.Path, genWatch.Elapsed.TotalMilliseconds);

        var identity = pathGO.AddComponent<RenderCacheIdentity>();
        identity.key = renderKey;
        identity.mesh = meshResult?.Mesh;

        if (meshResult != null && meshResult.GeneratedObject != null)
        {
            meshResult.GeneratedObject.transform.SetParent(pathGO.transform, false);
        }

        var drawInfo = DrawLodInfo.Ensure(pathGO);
        drawInfo.drawLod = drawLod;
        drawInfo.detailFrom = usePartialRange ? (float)detailFrom : 0f;
        drawInfo.detailTo = usePartialRange ? (float)detailTo : 1f;
        if (usePartialRange)
        {
            AddFarRemainders(pathGO, path, points, detailFrom, detailTo);
        }

        mf.mesh = meshResult?.Mesh;
        bool modulesCreated = meshResult?.ModulesCreated ?? false;

        if (meshResult != null && meshResult.Mesh != null)
        {
            // Collider physics data generation (PhysX baking) is expensive, so run asynchronously on another thread.
            RenderWorkScheduler.Start(this, SetColliderAsync(pathGO, meshResult.Mesh), "PathManager");
        }
        PathInputHandler inputHandler = pathGO.AddComponent<PathInputHandler>();
        inputHandler.pathId = path.path_id;

        UpdatePlannedIcon(pathGO, path, path.status == Constants.STATUS_PENDING);

        SetupVisuals(pathGO, path, status, modulesCreated);

        SetupWaypoints(pathGO, path.path_id, points);

        RegisterPathSegments(path, points);

        if (updateIntersections)
        {
            RegisterPathData(path);
        }
        else
        {
            SavePathToCache(path);
        }
        RegisterPathCells(path);
        StampPathBuild(pathGO, path, buildRevision, buildSerial);

        // Update the dictionary to the new object immediately.
        // This prevents duplicate objects if the same ID is redrawn again shortly afterward.
        SetPathObject(path.path_id, pathGO);
        DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Path,
            oldPath != null ? DrawLodInfo.GetDrawLod(oldPath, DrawLod.Normal) : (DrawLod?)null, drawLod);

        // Fixed-section parts finish asynchronously; replace the previous display only after initial generation completes.
        if (oldPath != null)
        {
            PathPresentation.Replace(pathGO, oldPath, lodCache);
        }
        ReevaluateAwaitingIntersectionsForPath(path);
    }

    /// <summary>Far point spacing (m), reducing points while retaining curve shape.</summary>
    private const float FAR_SAMPLE_INTERVAL = 15f;

    /// <summary>Thin points by distance spacing, always retaining start/end.</summary>
    private static List<Vector3> DecimatePolyline(List<Vector3> points, float interval)
    {
        var result = new List<Vector3>();
        if (points == null || points.Count == 0) return result;
        result.Add(points[0]);
        float acc = 0f;
        for (int i = 1; i < points.Count - 1; i++)
        {
            acc += Vector3.Distance(points[i - 1], points[i]);
            if (acc >= interval)
            {
                result.Add(points[i]);
                acc = 0f;
            }
        }
        if (points.Count > 1) result.Add(points[points.Count - 1]);
        return result;
    }

    /// <summary>
    /// Whether fully buried path sections need mode-dependent visibility.
    /// Include underground paths with negative relative Y and effectively non-following paths
    /// with actual tunnel sections through hills (exclude bridges crossing only valleys; render normal meshes with pillars).
    /// </summary>
    private static bool UsesTunnelMesh(PathSummary path, List<Vector3> points)
    {
        // Station building path walkways always have splines on the ground; their models provide the elevated shape,
        // so depth checks on ground-based points are meaningless. Section splitting would duplicate bridges and buildings
        // once per section, so exclude them.
        if (PathMeshFactory.GetModelType(path) == "station_house") return false;
        if (TunnelSectionUtil.IsUndergroundPath(path)) return true;
        if (GetPathFollowTerrain(path)) return false;
        float clearance = TunnelSectionUtil.GetClearanceHeight(path.path_variant);
        return TunnelSectionUtil.HasTunnelSection(points, clearance, pathFollowsTerrain: false);
    }

    /// <summary>
    /// Rendering centerline for section splitting (TunnelSectionUtil.GetDrawSections). Splitting checks only point depth,
    /// so pass points with terrain-following spans above ground. Polyline path points contain only anchors (RoadPath handles corners),
    /// so subdivide spans using RoadPath polyline rules. Other point sequences are already sampled by span rules.
    /// </summary>
    private static List<Vector3> CalculateSectionCenterline(PathSummary path, List<Vector3> points)
    {
        if (!IsAnchorPolyline(path, points, out int firstSpan)) return points;
        var result = new List<Vector3>();
        for (int i = 0; i < points.Count - 1; i++)
        {
            var span = SubdividePointsWithTerrain(new List<Vector3> { points[i], points[i + 1] }, TERRAIN_SAMPLE_INTERVAL, LinearSpanFollowsTerrain(path, firstSpan + i));
            result.AddRange(i == 0 ? span : span.GetRange(1, span.Count - 1));
        }
        return result;
    }

    /// <summary>
    /// 保存された折れ線の連続区間かを判定する。交差点で端の経由点まで切り取られた場合も、残るアンカーと元のスパン番号を対応させる。
    /// 細分化済みの描画点列は対象にせず、RoadPath の角処理とスパンごとの地形追従にだけ使う。
    /// </summary>
    public static bool IsAnchorPolyline(PathSummary path, List<Vector3> points)
        => IsAnchorPolyline(path, points, out _);

    public static bool IsAnchorPolyline(PathSummary path, List<Vector3> points, out int firstSpan)
    {
        firstSpan = 0;
        var waypoints = path.GetWaypointList();
        if (waypoints == null || waypoints.Count == 0 || points == null || points.Count < 2 || points.Count > waypoints.Count + 2) return false;
        var start = path.GetStartPointList();
        var end = path.GetEndPointList();
        if (start == null || end == null) return false;
        var anchors = new List<Vector2> { new Vector2(start[0], start[start.Count - 1]) };
        foreach (var waypoint in waypoints)
            anchors.Add(new Vector2(waypoint.position[0], waypoint.position[waypoint.position.Length - 1]));
        anchors.Add(new Vector2(end[0], end[end.Count - 1]));
        var from = IntersectionPolygonUtil.XZ(points[0]);
        var to = IntersectionPolygonUtil.XZ(points[points.Count - 1]);
        for (int candidate = 0; candidate + points.Count <= anchors.Count; candidate++)
        {
            int lastSpan = candidate + points.Count - 2;
            if (PolylineUtils.DistanceToSegmentSq(from, anchors[candidate], anchors[candidate + 1]) > 1e-6f
                || PolylineUtils.DistanceToSegmentSq(to, anchors[lastSpan], anchors[lastSpan + 1]) > 1e-6f) continue;
            if (points.Count == 2 && PolylineUtils.ProjectOnSegment(from, anchors[candidate], anchors[candidate + 1])
                > PolylineUtils.ProjectOnSegment(to, anchors[candidate], anchors[candidate + 1])) continue;
            bool matches = true;
            for (int i = 1; i < points.Count - 1; i++)
                if ((IntersectionPolygonUtil.XZ(points[i]) - anchors[candidate + i]).sqrMagnitude > 1e-6f) { matches = false; break; }
            if (!matches) continue;
            firstSpan = candidate;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a polyline span (anchor span to span+1) follows terrain. Follow only if effective follow_terrain is true and both anchor relative Y values
    /// are zero; otherwise connect anchor elevations linearly (same span rules as core geo_path_depth).
    /// </summary>
    public static bool LinearSpanFollowsTerrain(PathSummary path, int span)
        => GetPathFollowTerrain(path) && AnchorRelativeY(path, span) == 0f && AnchorRelativeY(path, span + 1) == 0f;

    /// <summary>Anchor relative Y (0=start, 1 onward=waypoints, final=end).</summary>
    private static float AnchorRelativeY(PathSummary path, int index)
    {
        var waypoints = path.GetWaypointList();
        int waypointCount = waypoints?.Count ?? 0;
        if (index == 0 || index > waypointCount)
        {
            var point = index == 0 ? path.GetStartPointList() : path.GetEndPointList();
            return point != null && point.Count >= 3 ? point[1] : 0f;
        }
        var position = waypoints[index - 1].position;
        return position != null && position.Length >= 3 ? position[1] : 0f;
    }

    /// <summary>
    /// Whether this tunnel path should be hidden at Far tier. Hide only non-following paths whose straight endpoint line contains
    /// tunnel sections (paths through hills); render Far for non-following bridges crossing only valleys.
    /// </summary>
    private bool FarPathHasHiddenTunnel(PathSummary path)
    {
        if (GetPathFollowTerrain(path)) return false;
        var startList = path.GetStartPointList();
        var endList = path.GetEndPointList();
        if (startList == null || endList == null) return false;
        var line = new List<Vector3> { GetPathPointPosition(startList), GetPathPointPosition(endList) };
        float clearance = TunnelSectionUtil.GetClearanceHeight(path.path_variant);
        return TunnelSectionUtil.HasTunnelSection(line, clearance, pathFollowsTerrain: false, sampleInterval: 20f);
    }

    /// <summary>
    /// Call the normal generator for each visible section and place portals at above/below-ground boundaries.
    /// Include fully buried sections in underground view and combine rendered section meshes into the selection collider.
    /// </summary>
    private PathMeshResult GenerateSectionedPathMesh(PathSummary path, List<Vector3> points)
    {
        float clearance = TunnelSectionUtil.GetClearanceHeight(path.path_variant);
        var portals = new List<TunnelSectionUtil.PortalInfo>();
        var sections = TunnelSectionUtil.GetDrawSections(CalculateSectionCenterline(path, points), clearance, UndergroundView.IsActive, portals);

        GameObject container = new GameObject("PathSections");
        var colliderCombine = new List<CombineInstance>();

        foreach (var section in sections)
        {
            PathMeshResult runResult = PathMeshFactory.GeneratePathMesh(path, section.Points);
            if (runResult == null) continue;

            if (runResult.GeneratedObject != null)
            {
                runResult.GeneratedObject.transform.SetParent(container.transform, false);
                runResult.GeneratedObject.AddComponent<PathSection>().IsUnderground = section.IsUnderground;
            }

            if (runResult.Mesh != null)
            {
                colliderCombine.Add(new CombineInstance
                {
                    mesh = runResult.Mesh,
                    transform = Matrix4x4.identity,
                });
            }
        }

        PlaceTunnelPortals(container, path, portals);

        Mesh colliderMesh = null;
        if (colliderCombine.Count == 1)
        {
            colliderMesh = colliderCombine[0].mesh;
        }
        else if (colliderCombine.Count > 1)
        {
            colliderMesh = new Mesh();
            colliderMesh.CombineMeshes(colliderCombine.ToArray(), true, false);
            colliderMesh.RecalculateBounds();
            foreach (var part in colliderCombine) Destroy(part.mesh);
        }

        UnityLog.If(LocalConstants.UNITY_LOG_ROAD,
            () => $"[PathManager] パス {path.path_id}: 描画区間={sections.Count} 坑口={portals.Count} clearance={clearance}");

        // Even if every section is tunneled and no mesh exists, set ModulesCreated=true
        // to suppress caller fallback material application (black bands).
        return new PathMeshResult(colliderMesh, true, container);
    }

    /// <summary>Assemble progress-dependent earthworks, pillars, and road surfaces, with the same portals used when complete.</summary>
    private PathMeshResult GenerateConstructingPathMesh(PathSummary path, List<Vector3> points)
    {
        bool underground = UsesTunnelMesh(path, points);
        var result = ConstructionPathBuilder.Generate(path, underground ? CalculateSectionCenterline(path, points) : points, underground, out var portals);
        PlaceTunnelPortals(result.GeneratedObject, path, portals);
        return result;
    }

    /// <summary>
    /// Place portal parts at tunnel entrances/exits.
    /// Search prefabs by variant then type; skip if unavailable (optional part).
    /// Prefabs assume local +Z faces into the tunnel; place at boundary points with Y rotation only.
    /// </summary>
    private void PlaceTunnelPortals(GameObject container, PathSummary path, List<TunnelSectionUtil.PortalInfo> portals)
    {
        if (portals == null || portals.Count == 0 || ModelManager.Instance == null) return;

        int pathType = path.path_type;
        var master = MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized
            ? MasterDataStore.Instance.GetPathMaster(path.path_variant)
            : null;
        if (master != null) pathType = master.type;

        GameObject portalPrefab = ModelManager.Instance.GetModel($"Models/Paths/p{path.path_variant}/portal");
        if (portalPrefab == null)
        {
            portalPrefab = ModelManager.Instance.GetModel($"Models/Paths/p{pathType}/portal");
        }
        if (portalPrefab == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD,
                () => $"[PathManager] portal プレハブなし (variant={path.path_variant}, type={pathType})。坑口の配置をスキップ");
            return;
        }

        foreach (var portal in portals)
        {
            GameObject portalGO = Instantiate(portalPrefab, container.transform);
            portalGO.transform.position = portal.position;
            portalGO.transform.rotation = Quaternion.LookRotation(portal.direction, Vector3.up);
        }
    }

    /// <summary>
    /// Destroy the specified object after a few frames.
    /// Remove the old object after the new one has rendered
    /// to prevent brief disappearance (popping).
    /// The caller updates the dictionary immediately; this coroutine only handles destruction.
    /// </summary>
    private IEnumerator DestroyObjectAfterFrames(GameObject obj)
    {
        return Utils.DestroyObjectAfterFrames(this, obj);
    }

    private void ReevaluateAwaitingIntersectionsForPath(PathSummary path)
    {
        if (path == null) return;
        ReevaluateAwaitingIntersectionNode(path.start_node_id);
        ReevaluateAwaitingIntersectionNode(path.end_node_id);
    }

    private void ReevaluateAwaitingIntersectionNode(int nodeId)
    {
        if (nodeId <= 0 || !awaitingIntersectionNodes.Contains(nodeId)) return;

        if (isLodPathBatchActive)
        {
            // Generating intersections immediately during bulk updates may calculate them before later roads enter the cache.
            // Coalesce into the batch intersection phase and reevaluate once connected paths are ready.
            deferredLodIntersectionNodeIds.Add(nodeId);
            return;
        }

        UpdateNodeIntersectionMesh(nodeId);
    }

    /// <summary>
    /// Subdivide consecutive control points at fixed intervals.
    /// When followTerrain=true, align intermediate Y values to terrain heights;
    /// otherwise linearly interpolate endpoint Y values to retain smooth slopes.
    /// </summary>
    public static List<Vector3> SubdividePointsWithTerrain(List<Vector3> controlPoints, float interval = TERRAIN_SAMPLE_INTERVAL, bool followTerrain = true)
    {
        if (controlPoints == null || controlPoints.Count < 2) return controlPoints;

        List<Vector3> result = new List<Vector3>(controlPoints.Count * 3);

        for (int i = 0; i < controlPoints.Count; i++)
        {
            result.Add(controlPoints[i]);

            if (i < controlPoints.Count - 1)
            {
                Vector3 from = controlPoints[i];
                Vector3 to = controlPoints[i + 1];

                float dx = to.x - from.x;
                float dz = to.z - from.z;
                float horizontalDistance = Mathf.Sqrt(dx * dx + dz * dz);

                if (horizontalDistance > interval)
                {
                    int subdivisions = Mathf.FloorToInt(horizontalDistance / interval);
                    for (int s = 1; s <= subdivisions; s++)
                    {
                        float t = (float)s / (subdivisions + 1);
                        float x = Mathf.Lerp(from.x, to.x, t);
                        float z = Mathf.Lerp(from.z, to.z, t);

                        if (followTerrain)
                        {
                            float groundHeight = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
                            result.Add(new Vector3(x, groundHeight + RoadPartHeights.ROAD_SPLINE_OFFSET, z));
                        }
                        else
                        {
                            float y = Mathf.Lerp(from.y, to.y, t);
                            result.Add(new Vector3(x, y, z));
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Get the path variant base_height (height offset for 3D rendering).
    /// 0m (no shift) if absent from master data.
    /// Shared by committed path rendering points and road editor placement previews (RoadRenderer).
    /// </summary>
    public static float GetPathBaseHeight(int pathVariant)
    {
        if (MasterDataStore.Instance == null) return 0f;
        var master = MasterDataStore.Instance.GetPathMaster(pathVariant);
        if (master != null && master.base_height.HasValue) return master.base_height.Value;
        return 0f;
    }

    /// <summary>
    /// Get whether path variant spans follow terrain (follow_terrain, default true).
    /// Variants with false (such as girder bridges) do not snap even relY=0 spans to terrain;
    /// interpolate only absolute anchor elevations to form horizontal decks.
    /// Same definition as follow_flags in backend geo_path_depth._build_segments_3d.
    /// </summary>
    public static bool GetPathFollowTerrain(int pathVariant)
    {
        if (MasterDataStore.Instance == null) return true;
        var master = MasterDataStore.Instance.GetPathMaster(pathVariant);
        if (master != null && master.follow_terrain.HasValue) return master.follow_terrain.Value;
        return true;
    }

    /// <summary>Node snapping position without rendering offsets, prioritizing connected paths' design elevations.</summary>
    public Vector3 GetNodeSnapPosition(NodeData node)
    {
        Vector3 position = node.GetAbsolutePosition();
        position.y = Utils.GetGroundHeight(position) + node.GetRelativeHeight();
        foreach (var path in GetPathsConnectedToNode(node.node_id))
        {
            if (path.station_grade_profile == null || path.station_grade_profile.Count < 2) continue;
            position.y = Utils.GetGroundHeight(position) + path.GetRelativeHeightAtPoint(position.x, position.z);
            return position;
        }
        return position;
    }

    /// <summary>Apply stored absolute elevations to rendering points.</summary>
    public static void ApplyStationGrade(PathSummary path, List<Vector3> points)
    {
        if (path?.station_grade_profile == null || path.station_grade_profile.Count < 2) return;
        var sampler = StationGradeSampler.Get(path.GetStationGradeProfile());
        float offset = RoadPartHeights.ROAD_SPLINE_OFFSET - GetPathBaseHeight(path.path_variant);
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 point = points[i];
            point.y = sampler.Evaluate(point) + offset;
            points[i] = point;
        }
    }

    /// <summary>Project XZ onto a reference line to return the same cross-section height inside/outside curves or on reversed tracks.</summary>
    public static float EvaluateStationGrade(List<List<float>> profile, Vector3 point)
    {
        return StationGradeSampler.Get(profile).Evaluate(point);
    }

    /// <summary>
    /// Prioritize per-path terrain-following settings, falling back to variant defaults when unspecified.
    /// Same resolution order as backend geo_path_depth.path_follows_terrain.
    /// </summary>
    public static bool GetPathFollowTerrain(PathSummary path)
    {
        if (path == null) return true;
        int own = path.GetFollowTerrain();
        if (own == 1) return true;
        if (own == 0) return false;
        return GetPathFollowTerrain(path.path_variant);
    }

    /// <summary>
    /// Generate densely sampled cubic Bezier points from path anchors
    /// (start, waypoints, end) and control points.
    ///
    /// Each segment is cubic Bezier B(t) = (1-t)^3*P0 + 3(1-t)^2*t*P1 + 3(1-t)*t^2*P2 + t^3*P3:
    ///   P0 = segment start position
    ///   P1 = outgoing control at segment start (control_out or start_control)
    ///   P2 = incoming control at segment end (control_in or end_control)
    ///   P3 = segment end position
    ///
    /// If controls are unspecified, automatically place them at 1/3 and 2/3 between endpoints,
    /// producing a straight Bezier (effectively a line).
    /// This matches backend PathEntity._auto_fill_controls.
    ///
    /// Sampled point Y coordinates follow terrain elevation.
    ///
    /// base_height: for 3D rendering, subtract base_height from the path relative height (relY)
    /// without affecting backend logic/intersection checks, which retain the original relY.
    /// Example: a power line with base_height=20 and relY=20 renders at 0m (ground) in 3D;
    /// the prefab itself is 20m tall, preventing the line from appearing to lie on the ground.
    ///
    /// Each return value is a new caller-owned list and may be modified.
    /// GetUntrimmedPoints caches uncut points with terrain signatures.
    /// </summary>
    public List<Vector3> CalculatePathPoints(PathSummary path, bool trimIntersection = true)
    {
        using var scope = calculatePointsMarker.Auto();
        List<Vector3> shared = GetUntrimmedPoints(path, out bool isShared);
        List<Vector3> points = isShared ? new List<Vector3>(shared) : shared;
        return trimIntersection ? TrimIntersectionPoints(path, points) : points;
    }

    /// <summary>
    /// Uncut point cache. Points depend only on path data and terrain heights in crossed cells,
    /// so avoid resampling while source (the current pathDataCache instance) and terrain signatures match.
    /// points is shared and must not be modified (replace with a new list on recalculation).
    /// revision is a global serial advanced on every recalculation, forming the high bits of GetPathGeometryRevision.
    /// </summary>
    private sealed class PointsEntry
    {
        public PathSummary source;
        public long terrainSignature;
        public List<Vector3> points;
        public int revision;
    }
    private readonly Dictionary<int, PointsEntry> untrimmedPointsCache = new Dictionary<int, PointsEntry>();
    // Never reset across sessions, avoiding revision collisions before/after ClearAllCells.
    private int pointsRevisionCounter;
    /// <summary>Per-path revision advanced when cut points/sidewalk setbacks change (low bits of GetPathGeometryRevision).</summary>
    private readonly Dictionary<int, int> connectionRevisions = new Dictionary<int, int>();

    /// <summary>
    /// Return uncut points. When isShared=true, the list is shared with the cache and must not be modified.
    /// Cache only instances registered in pathDataCache with registered crossed cells;
    /// calculate previews, cableway snapshots, and newly received data each time (isShared=false).
    /// </summary>
    private List<Vector3> GetUntrimmedPoints(PathSummary path, out bool isShared)
    {
        PointsEntry entry = GetUntrimmedEntry(path);
        isShared = entry != null;
        return isShared ? entry.points : ComputeUntrimmedPathPoints(path);
    }

    /// <summary>
    /// Validated cache entry. Recalculate and save if invalid; null for uncached targets.
    /// Do not cache if terrain signatures are unknown (TerrainManager absent), since height changes cannot be detected.
    /// For ramps, include the target main-line signature (main-line terrain/data changes affect render heights).
    /// </summary>
    private PointsEntry GetUntrimmedEntry(PathSummary path)
    {
        if (path == null || !pathDataCache.TryGetValue(path.path_id, out PathSummary cached) || !ReferenceEquals(cached, path)
            || !pathCellsById.ContainsKey(path.path_id)) return null;
        long signature = ComputeTerrainSignature(path, out bool known);
        if (!known)
        {
            DropUntrimmedPoints(path.path_id);
            return null;
        }
        signature = (long)SignatureMix((ulong)signature ^ (ulong)ComputeRoadMergeSignature(path));
        if (untrimmedPointsCache.TryGetValue(path.path_id, out PointsEntry entry)
            && ReferenceEquals(entry.source, path) && entry.terrainSignature == signature) return entry;
        entry ??= new PointsEntry();
        entry.source = path;
        entry.terrainSignature = signature;
        entry.points = ComputeUntrimmedPathPoints(path);
        entry.revision = ++pointsRevisionCounter;
        untrimmedPointsCache[path.path_id] = entry;
        return entry;
    }

    /// <summary>
    /// Discard point caches and cut-point revisions together. The next entry receives a new global serial,
    /// so restarting cut-point revisions at 0 cannot collide with past GetPathGeometryRevision values.
    /// </summary>
    private void DropUntrimmedPoints(int pathId)
    {
        untrimmedPointsCache.Remove(pathId);
        connectionRevisions.Remove(pathId);
    }

    private void BumpConnectionRevision(int pathId)
    {
        connectionRevisions.TryGetValue(pathId, out int revision);
        connectionRevisions[pathId] = revision + 1;
    }

    /// <summary>
    /// Terrain signature of path crossed cells (TerrainManager.GetTerrainSignature). Without TerrainManager
    /// (including Unity-null destroyed instances), known=false; callers must treat this as unknown and must not skip work.
    /// </summary>
    private long ComputeTerrainSignature(PathSummary path, out bool known)
    {
        TerrainManager terrain = Utils.CachedTerrainManager;
        if (terrain == null)
        {
            known = false;
            return 0;
        }
        known = true;
        return terrain.GetTerrainSignature(GetPathCoveredCellsCached(path));
    }

    /// <summary>Range (m) around nodes where intersection corner fills or sidewalk connectors may sample terrain. Include in signatures alongside connected road cells.</summary>
    private const float NODE_SIGNATURE_TERRAIN_MARGIN = 150f;

    /// <summary>
    /// Input signature for intersection generation (NodeMeshFactory.GenerateNodeMesh and GetIntersectionCutLimit).
    /// Mix node coordinates/type/station paths, traffic side, terrain around the node and connected roads, each connected road's version/status/construction progress,
    /// and connection count at its opposite end. Sum road terms independently of order. Masters/models remain fixed after loading.
    /// known=false if terrain signatures or master data are unavailable; callers must not skip work.
    /// </summary>
    private long ComputeNodeIntersectionSignature(int nodeId, NodeData node, List<PathSummary> connected, out bool known)
    {
        known = false;
        TerrainManager terrain = Utils.CachedTerrainManager;
        if (terrain == null || node?.position == null || node.position.Length < 2) return 0;
        if (MasterDataStore.Instance == null || !MasterDataStore.Instance.IsInitialized) return 0;

        float[] pos = node.position;
        ulong h = SignatureMix((ulong)(uint)nodeId ^ ((ulong)(uint)pos.Length << 32));
        for (int i = 0; i < pos.Length; i++) h = SignatureMix(h ^ (uint)BitConverter.SingleToInt32Bits(pos[i]));
        h = SignatureMix(h ^ (uint)node.type);
        h = SignatureMix(h ^ (node.station_path_id.HasValue ? (uint)node.station_path_id.Value : 0x9E3779B9UL << 32));
        h = SignatureMix(h ^ (uint)CityInfoStore.TrafficHand);
        float x = pos[0];
        float z = pos.Length >= 3 ? pos[2] : pos[1];
        var area = new Rect(x - NODE_SIGNATURE_TERRAIN_MARGIN, z - NODE_SIGNATURE_TERRAIN_MARGIN,
            NODE_SIGNATURE_TERRAIN_MARGIN * 2f, NODE_SIGNATURE_TERRAIN_MARGIN * 2f);
        h = SignatureMix(h ^ (ulong)terrain.GetTerrainSignature(area));

        ulong pathSum = 0;
        foreach (PathSummary p in connected)
        {
            int otherNode = p.start_node_id == nodeId ? p.end_node_id : p.start_node_id;
            bool otherIsJunction = pathsByNodeIndex.TryGetValue(otherNode, out var otherPaths) && otherPaths.Count > 1;
            ulong t = SignatureMix((ulong)(uint)p.path_id ^ ((ulong)(uint)p.version << 32));
            t = SignatureMix(t ^ (uint)p.status ^ ((ulong)(otherIsJunction ? 1u : 0u) << 32) ^ ((ulong)(p.start_node_id == nodeId ? 1u : 0u) << 33));
            t = SignatureMix(t ^ (uint)BitConverter.SingleToInt32Bits(ConstructionPathBuilder.Progress(p)));
            t = SignatureMix(t ^ (ulong)ComputeTerrainSignature(p, out _));
            t = SignatureMix(t ^ (ulong)ComputeRoadMergeSignature(p));
            pathSum += t;
        }
        known = true;
        return (long)SignatureMix(h ^ pathSum ^ (ulong)connected.Count);
    }

    /// <summary>
    /// Signature of nearby active main-line set, versions, and terrain affecting ramp rendering heights. 0 for non-ramps.
    /// Include in ramp point caches and both endpoint intersection signatures to rebuild on main-line changes.
    /// </summary>
    private long ComputeRoadMergeSignature(PathSummary path)
    {
        if (!RoadAdjacency.IsRamp(path)) return 0;
        ulong sum = 0;
        foreach (int id in CollectNearbyRoadMergeIds(path, false))
        {
            if (!pathDataCache.TryGetValue(id, out PathSummary main) || main.status != Constants.STATUS_ACTIVE) continue;
            ulong t = SignatureMix((ulong)(uint)id ^ ((ulong)(uint)main.version << 32));
            sum += SignatureMix(t ^ (ulong)ComputeTerrainSignature(main, out _));
        }
        return (long)sum;
    }

    /// <summary>SplitMix64 scrambling, used to mix signature terms.</summary>
    private static ulong SignatureMix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>
    /// Whether a road GameObject (including those hidden on cell departure) was generated with current data, render revision, and terrain.
    /// If true, skip forced terrain-following regeneration. Always false for unknown terrain signatures (rebuild as before).
    /// Signatures depend on tile presence and versions; restoring the same-version tile from LRU therefore matches.
    /// </summary>
    private bool IsTerrainCurrent(PathSummary path)
    {
        using var scope = isTerrainCurrentMarker.Auto();
        if (path == null || !pathObjectsCache.TryGetValue(path.path_id, out GameObject go) || go == null) return false;
        PathBuildStamp stamp = go.GetComponent<PathBuildStamp>();
        if (stamp == null || !stamp.terrainKnown || stamp.version != path.version || stamp.status != path.status
            || stamp.progress != ConstructionPathBuilder.Progress(path)) return false;
        geometryRevisions.TryGetValue(path.path_id, out int revision);
        if (revision != stamp.geometryRevision) return false;
        long signature = ComputeTerrainSignature(path, out bool known);
        return known && signature == stamp.terrainSignature;
    }

    /// <summary>Whether a GameObject taken from the parked cache was generated with current crossed-cell terrain signatures. False if unknown.</summary>
    private bool IsCachedBuildTerrainCurrent(GameObject go, PathSummary path)
    {
        PathBuildStamp stamp = go.GetComponent<PathBuildStamp>();
        if (stamp == null || !stamp.terrainKnown) return false;
        long signature = ComputeTerrainSignature(path, out bool known);
        return known && signature == stamp.terrainSignature;
    }

    /// <summary>
    /// Record generation data and terrain signatures on a road GameObject. Call after SavePathToCache and RegisterPathCells
    /// (to sign this instance's crossed cells). Generation is synchronous, so terrain matches at start and finish.
    /// buildRevision is the revision used in the render key (capture at start because recursive intersection generation may advance it).
    /// buildSerial also advances at generation start, ensuring requests enqueued during generation are not skipped.
    /// </summary>
    private void StampPathBuild(GameObject pathGO, PathSummary path, int buildRevision, long buildSerial)
    {
        var stamp = pathGO.AddComponent<PathBuildStamp>();
        stamp.buildSerial = buildSerial;
        stamp.terrainSignature = ComputeTerrainSignature(path, out stamp.terrainKnown);
        stamp.version = path.version;
        stamp.status = path.status;
        stamp.progress = ConstructionPathBuilder.Progress(path);
        stamp.geometryRevision = buildRevision;
    }

    /// <summary>
    /// Uncut points (CalculatePathPoints implementation). Resample each time without reading caches.
    /// Adjust highway ramp heights toward main-line surfaces where joined (RoadAdjacency; rendering only).
    /// Point cache signatures (ComputeRoadMergeSignature) detect target main-line changes.
    /// </summary>
    private List<Vector3> ComputeUntrimmedPathPoints(PathSummary path)
    {
        List<Vector3> points = ComputeDesignedPathPoints(path);
        if (points == null || points.Count < 2 || !RoadAdjacency.IsRamp(path)) return points;
        var neighbors = GetAdjacentRoadNeighbors(path);
        if (neighbors.Count == 0) return points;
        var master = MasterDataStore.Instance?.GetPathMaster(path.path_variant);
        var corridor = RoadAdjacency.Compute(points, GetRoadWidth(path.path_variant), Mathf.Max(0f, master?.wall_width ?? 0f), true, neighbors);
        return RoadAdjacency.ApplyRampHeights(points, corridor);
    }

    /// <summary>Uncut points matching stored vertical alignment (anchors, controls, and station/connection design profiles).</summary>
    private List<Vector3> ComputeDesignedPathPoints(PathSummary path)
    {
        List<Vector3> points = new List<Vector3>();
        var startPointList = path.GetStartPointList();
        var endPointList = path.GetEndPointList();
        var waypointList = path.GetWaypointList();

        // Get base_height from master data.
        // This affects only 3D rendering, not backend logic.
        float baseHeight = GetPathBaseHeight(path.path_variant);

        // Terrain-following flag (per-path setting over variant default).
        // If false (girder bridges, explicitly non-following paths, etc.), do not snap even relY=0 spans to terrain.
        bool variantFollows = GetPathFollowTerrain(path);

        Vector3 startPos = Vector3.zero;
        bool hasStart = startPointList != null && startPointList.Count >= 2;
        if (hasStart)
        {
            startPos = GetPathPointPosition(startPointList);

            // Subtract base_height for 3D rendering (backend relY remains unchanged).
            startPos.y -= baseHeight;
        }

        Vector3 endPos = Vector3.zero;
        bool hasEnd = endPointList != null && endPointList.Count >= 2;
        if (hasEnd)
        {
            endPos = GetPathPointPosition(endPointList);

            endPos.y -= baseHeight;
        }

        if (!hasStart || !hasEnd) return points;

        // For curved=False, join anchors with straight segments and subdivide to follow terrain.
        // When direction changes at waypoints, calculate connection cross-sections to preserve corners,
        // then bound segments by those cross-sections.
        if (!path.curved || (MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.single_span ?? false))
        {
            List<Vector3> anchorPoints = new List<Vector3>();
            anchorPoints.Add(startPos);

            if (waypointList != null && waypointList.Count > 0)
            {
                float width = GetRoadWidth(path.path_variant);
                var gf = new NetTopologySuite.Geometries.GeometryFactory();

                Vector3 prevPos = startPos;

                for (int i = 0; i < waypointList.Count; i++)
                {
                    var wp = waypointList[i];
                    Vector3 wpPos = wp.GetAbsolutePosition();
                    wpPos.y -= baseHeight;
                    float wpRelY = (wp.position != null && wp.position.Length >= 3) ? wp.position[1] : 0f;
                    if (variantFollows && wpRelY == baseHeight)
                    {
                        float groundHeight = Utils.GetGroundHeight(new Vector3(wpPos.x, 1000f, wpPos.z));
                        wpPos.y = groundHeight + RoadPartHeights.ROAD_SPLINE_OFFSET;
                    }

                    Vector3 nextPos;
                    if (i + 1 < waypointList.Count)
                    {
                        nextPos = waypointList[i + 1].GetAbsolutePosition();
                        nextPos.y -= baseHeight;
                        float nextRelY = (waypointList[i + 1].position != null && waypointList[i + 1].position.Length >= 3) ? waypointList[i + 1].position[1] : 0f;
                        if (variantFollows && nextRelY == baseHeight)
                        {
                            float groundHeight = Utils.GetGroundHeight(new Vector3(nextPos.x, 1000f, nextPos.z));
                            nextPos.y = groundHeight + RoadPartHeights.ROAD_SPLINE_OFFSET;
                        }
                    }
                    else
                    {
                        nextPos = endPos;
                    }

                    Vector2 dirA = (new Vector2(wpPos.x, wpPos.z) - new Vector2(prevPos.x, prevPos.z)).normalized;
                    Vector2 dirB = (new Vector2(nextPos.x, nextPos.z) - new Vector2(wpPos.x, wpPos.z)).normalized;

                    if (Vector2.Dot(dirA, dirB) > 0.99f)
                    {
                        // Nearly straight, so add the waypoint directly as a passing point
                        anchorPoints.Add(wpPos);
                        prevPos = wpPos;
                        continue;
                    }

                    // For corners, add wpPos unchanged here; RoadPath.cs handles them later
                    anchorPoints.Add(wpPos);
                    prevPos = wpPos;
                }
            }

            anchorPoints.Add(endPos);
            bool singleSpan = MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.single_span ?? false;
            if ((anchorPoints.Count == 2 || path.station_grade_profile?.Count >= 2) && !singleSpan)
            {
                bool followTerrain = GetPathFollowTerrain(path) && startPointList[1] == 0f && endPointList[1] == 0f;
                var sampled = SubdividePointsWithTerrain(anchorPoints, TERRAIN_SAMPLE_INTERVAL, followTerrain);
                ApplyStationGrade(path, sampled);
                return sampled;
            }
            // Return unchanged here; RoadPath.cs and similar code process corners before subdividing
            ApplyStationGrade(path, anchorPoints);
            return anchorPoints;
        }

        float startRelY = (startPointList != null && startPointList.Count >= 3) ? startPointList[1] : 0f;
        float endRelY = (endPointList != null && endPointList.Count >= 3) ? endPointList[1] : 0f;

        // Endpoint Bezier controls (start_control / end_control). Prefer user-dragged placement values
        // stored in the backend when present in API responses.
        // If unspecified (null), fall back to automatic calculation using the same formula
        // as backend _auto_fill_controls within each branch.
        Vector3? startControlWorld = EndpointControlToWorld(path.GetStartControlList(), startPointList, baseHeight);
        Vector3? endControlWorld = EndpointControlToWorld(path.GetEndControlList(), endPointList, baseHeight);

        if (waypointList == null || waypointList.Count == 0)
        {
            // No waypoints: one segment from start to end
            // Without controls, use the backend's same 1/3 and 2/3 subdivisions (straight Bezier)
            Vector3 p1 = startControlWorld ?? Vector3.Lerp(startPos, endPos, 1f / 3f);
            Vector3 p2 = endControlWorld ?? Vector3.Lerp(startPos, endPos, 2f / 3f);
            bool followTerrain = variantFollows && startRelY == baseHeight && endRelY == baseHeight;
            points = SampleBezierSegmentWithTerrain(startPos, p1, p2, endPos, followTerrain);
        }
        else
        {
            // Build anchor list: [start, waypoint 0, ..., waypoint N, end]
            // Each anchor has (position, control_in, control_out)
            int anchorCount = waypointList.Count + 2;
            Vector3[] anchorPos = new Vector3[anchorCount];
            float[] anchorRelY = new float[anchorCount];
            Vector3?[] anchorControlIn = new Vector3?[anchorCount];
            Vector3?[] anchorControlOut = new Vector3?[anchorCount];

            anchorPos[0] = startPos;
            anchorRelY[0] = startRelY;
            anchorControlIn[0] = null;
            anchorControlOut[0] = startControlWorld;

            for (int i = 0; i < waypointList.Count; i++)
            {
                Vector3 wpPos = waypointList[i].GetAbsolutePosition();
                wpPos.y -= baseHeight;
                float wpRelY = (waypointList[i].position != null && waypointList[i].position.Length >= 3) ? waypointList[i].position[1] : 0f;
                wpPos.y += 0.01f;
                anchorPos[i + 1] = wpPos;
                anchorRelY[i + 1] = wpRelY;
                Vector3? ctrlIn = waypointList[i].GetControlInAbsolute();
                if (ctrlIn.HasValue) { var c = ctrlIn.Value; c.y -= baseHeight; anchorControlIn[i + 1] = c; }
                Vector3? ctrlOut = waypointList[i].GetControlOutAbsolute();
                if (ctrlOut.HasValue) { var c = ctrlOut.Value; c.y -= baseHeight; anchorControlOut[i + 1] = c; }
            }

            anchorPos[anchorCount - 1] = endPos;
            anchorRelY[anchorCount - 1] = endRelY;
            anchorControlIn[anchorCount - 1] = endControlWorld;
            anchorControlOut[anchorCount - 1] = null;

            // For anchors without specified controls,
            // derive default controls from 3D Catmull-Rom spline tangent vectors.
            // Tangent V_i = P_{i+1} - P_{i-1}, tension=1/6.
            // Endpoints use a one-sided vector to the adjacent anchor.
            for (int seg = 0; seg < anchorCount - 1; seg++)
            {
                Vector3 segStart = anchorPos[seg];
                Vector3 segEnd = anchorPos[seg + 1];

                // P1: outgoing control at segment start (C_{seg, out})
                Vector3 p1;
                if (anchorControlOut[seg].HasValue)
                {
                    p1 = anchorControlOut[seg].Value;
                }
                else
                {
                    Vector3 tangent;
                    if (seg == 0)
                    {
                        tangent = anchorPos[1] - anchorPos[0];
                    }
                    else
                    {
                        tangent = anchorPos[Mathf.Min(seg + 1, anchorCount - 1)] - anchorPos[seg - 1];
                    }
                    p1 = segStart + tangent * (1f / 6f);
                }

                // P2: incoming control at segment end (C_{seg+1, in})
                Vector3 p2;
                if (anchorControlIn[seg + 1].HasValue)
                {
                    p2 = anchorControlIn[seg + 1].Value;
                }
                else
                {
                    Vector3 tangent;
                    if (seg + 1 == anchorCount - 1)
                    {
                        tangent = anchorPos[anchorCount - 1] - anchorPos[anchorCount - 2];
                    }
                    else
                    {
                        tangent = anchorPos[seg + 2] - anchorPos[seg];
                    }
                    p2 = segEnd - tangent * (1f / 6f);
                }

                bool followTerrain = variantFollows && anchorRelY[seg] == baseHeight && anchorRelY[seg + 1] == baseHeight;
                List<Vector3> segPoints = SampleBezierSegmentWithTerrain(segStart, p1, p2, segEnd, followTerrain);

                // Add every point for the first segment; exclude duplicate starts afterward
                int startIdx = (seg == 0) ? 0 : 1;
                for (int j = startIdx; j < segPoints.Count; j++)
                {
                    points.Add(segPoints[j]);
                }
            }
        }

        ApplyStationGrade(path, points);
        return points;
    }

    /// <summary>両端の交差点の切断区間を分ける。通常の折れ線の経由点は交差点内に取り込めるが、single_span の構造アンカーは残す。</summary>
    private float GetIntersectionCutLimit(PathSummary path, int nodeId, List<Vector3> points)
    {
        int otherNode = path.start_node_id == nodeId ? path.end_node_id : path.start_node_id;
        bool bothEnds = pathsByNodeIndex.TryGetValue(otherNode, out var connected) && connected.Count > 1;
        float limit = IntersectionPolygonUtil.Length(points) * (bothEnds ? 0.45f : 0.95f);
        bool singleSpan = MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.single_span ?? false;
        if (singleSpan && points.Count > 1)
        {
            // Design vertical alignment subdivides rendering points, so take the next anchor from stored path waypoints.
            var waypoints = path.GetWaypointList();
            if (waypoints != null && waypoints.Count > 0)
            {
                bool fromStart = path.start_node_id == nodeId;
                Vector3 nodeAnchor = points[fromStart ? 0 : points.Count - 1];
                Vector3 nextAnchor = waypoints[fromStart ? 0 : waypoints.Count - 1].GetAbsolutePosition();
                limit = Mathf.Min(limit, Vector2.Distance(IntersectionPolygonUtil.XZ(nodeAnchor), IntersectionPolygonUtil.XZ(nextAnchor)) * 0.45f);
            }
        }
        return limit;
    }
    private List<Vector3> TrimIntersectionPoints(PathSummary path, List<Vector3> points)
    {
        Vector3? start = pathStartConnectionPoints.TryGetValue(path.path_id, out var a) ? a : (Vector3?)null;
        Vector3? end = pathEndConnectionPoints.TryGetValue(path.path_id, out var b) ? b : (Vector3?)null;
        return IntersectionPolygonUtil.Trim(points, start, end);
    }
    /// <summary>
    /// Convert API endpoint controls [x, relative height, z] to absolute Unity coordinates.
    /// Use terrain elevation beneath the associated anchor (start/end), not directly beneath the control.
    /// Using terrain under control XZ leaks intermediate relief into curve Y,
    /// conflicting with the backend rule that non-following spans interpolate only anchor elevations
    /// (same reason as PathWaypoint.GetControlInAbsolute).
    /// Return null for unspecified controls (including absent fields before schema regeneration).
    /// </summary>
    private static Vector3? EndpointControlToWorld(List<float> control, List<float> anchor, float baseHeight)
    {
        if (control == null || control.Count < 3) return null;
        if (anchor == null || anchor.Count < 2) return null;
        float anchorX = anchor[0];
        float anchorZ = anchor[anchor.Count - 1];
        float groundY = Utils.GetGroundHeight(new Vector3(anchorX, 1000f, anchorZ));
        return new Vector3(control[0], groundY + control[1] - baseHeight, control[2]);
    }

    /// <summary>
    /// Sample a cubic Bezier segment at approximately 5m spacing.
    /// Include both endpoints in results.
    /// When followTerrain=true, align each Y to terrain elevation;
    /// otherwise use cubic Bezier interpolation of control Y values unchanged to preserve 3D road geometry.
    /// </summary>
    private static List<Vector3> SampleBezierSegmentWithTerrain(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, bool followTerrain = true)
    {
        // Estimate Bezier arc length by summing control-point distances,
        // then dynamically choose the sample count for approximately 5m spacing.
        float approxLength = Vector3.Distance(p0, p1) + Vector3.Distance(p1, p2) + Vector3.Distance(p2, p3);
        int samples = Mathf.Max(1, Mathf.FloorToInt(approxLength / TERRAIN_SAMPLE_INTERVAL));

        int totalPoints = samples + 2;
        List<Vector3> result = new List<Vector3>(totalPoints);
        result.Add(p0);
        for (int s = 1; s <= samples; s++)
        {
            float t = (float)s / (samples + 1);
            Vector3 sampled = PolylineUtils.CubicBezierPoint(p0, p1, p2, p3, t);

            if (followTerrain)
            {
                float groundHeight = Utils.GetGroundHeight(new Vector3(sampled.x, 1000f, sampled.z));
                result.Add(new Vector3(sampled.x, groundHeight + RoadPartHeights.ROAD_SPLINE_OFFSET, sampled.z));
            }
            else
            {
                result.Add(sampled);
            }
        }
        result.Add(p3);

        return result;
    }

    private void SetupVisuals(GameObject pathGO, PathSummary path, int status, bool modulesCreated)
    {
        using var scope = setupVisualsMarker.Auto();
        VisualModeObject visualModeObject = pathGO.AddComponent<VisualModeObject>();
        visualModeObject.objectType = InputObjectType.PATH;
        visualModeObject.useChildRenderers = true;
        visualModeObject.ignoreSelfRenderer = true;
        visualModeObject.InitializeMaterials();

        var stats = pathGO.AddComponent<VisualPathStats>();
        stats.InitFrom(path);
        visualModeObject.SetStatus(status);
        // Per-object display filters must also affect paths generated later through incremental loading,
        // so resolve their initial state here immediately after generation.
        visualModeObject.SetFilterHidden(ObjectVisibilityFilter.IsHidden(InputObjectType.PATH, path.path_id));
    }

    private void SetupWaypoints(GameObject pathGO, int pathId, List<Vector3> points)
    {
        pathLabelPositions.Remove(pathId);
        pathPointsCache[pathId] = points;

        if (!_showWaypoints) return;

        CreateWaypointsForPath(pathGO, points);
    }

    private void CreateWaypointsForPath(GameObject pathGO, List<Vector3> points)
    {
        foreach (var pt in points)
        {
            GameObject wpGO = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            wpGO.name = WAYPOINT_NAME;
            wpGO.tag = WAYPOINT_TAG;
            wpGO.transform.position = pt;
            wpGO.transform.localScale = new Vector3(0.4f, 0.01f, 0.4f);

            Material wpMat = GetWaypointMaterial();
            if (wpMat != null)
            {
                wpGO.GetComponent<Renderer>().material = wpMat;
            }

            wpGO.transform.SetParent(pathGO.transform);
            wpGO.SetActive(true);
        }
    }

    private void RegisterPathSegments(PathSummary path, List<Vector3> points)
    {
        using var scope = registerSegmentsMarker.Auto();
        if (points == null || points.Count < 2) return;

        // Remove old segments before reregistering
        UnregisterPathSegments(path.path_id);

        List<PathSegment> newSegments = new List<PathSegment>(points.Count - 1);
        for (int i = 0; i < points.Count - 1; i++)
        {
            PathSegment seg = new PathSegment
            {
                path = path,
                segmentIndex = i,
                a = points[i],
                b = points[i + 1]
            };
            ApplySegmentToGrid(seg, true);
            newSegments.Add(seg);
        }
        pathSegmentsByPathId[path.path_id] = newSegments;
    }

    private void UnregisterPathSegments(int pathId)
    {
        if (segmentGrid == null) return;

        if (pathSegmentsByPathId.TryGetValue(pathId, out List<PathSegment> oldSegments))
        {
            foreach (var seg in oldSegments) ApplySegmentToGrid(seg, false);
            pathSegmentsByPathId.Remove(pathId);
        }
    }

    /// <summary>
    /// Register or remove each segment in grid cells touched by points sampled at half-cell spacing.
    /// SpatialHash.Remove removes only one entry per call, so registration/removal must use identical points and deduplication.
    /// Skip cells matching the preceding point to avoid duplicate registration of one segment in a cell.
    /// </summary>
    private void ApplySegmentToGrid(PathSegment segment, bool add)
    {
        if (segmentGrid == null) return;

        Vector3 start = segment.a;
        Vector3 end = segment.b;
        float cellSize = segmentGridCellSize;
        float interval = cellSize * 0.5f;
        int divisions = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(start, end) / interval));

        bool hasPrevious = false;
        int previousX = 0, previousZ = 0;
        for (int i = 0; i <= divisions; i++)
        {
            Vector3 point = Vector3.Lerp(start, end, (float)i / divisions);
            int cellX = Mathf.FloorToInt(point.x / cellSize);
            int cellZ = Mathf.FloorToInt(point.z / cellSize);
            if (hasPrevious && cellX == previousX && cellZ == previousZ) continue;
            hasPrevious = true;
            previousX = cellX;
            previousZ = cellZ;
            if (add) segmentGrid.Add(segment, point);
            else segmentGrid.Remove(segment, point);
        }
    }

    private void AddPathToNodeIndex(PathSummary path)
    {
        if (path == null) return;
        if (path.start_node_id > 0)
        {
            if (!pathsByNodeIndex.TryGetValue(path.start_node_id, out var startSet))
            {
                startSet = new HashSet<int>();
                pathsByNodeIndex[path.start_node_id] = startSet;
            }
            startSet.Add(path.path_id);
        }
        if (path.end_node_id > 0)
        {
            if (!pathsByNodeIndex.TryGetValue(path.end_node_id, out var endSet))
            {
                endSet = new HashSet<int>();
                pathsByNodeIndex[path.end_node_id] = endSet;
            }
            endSet.Add(path.path_id);
        }
    }

    private void RemovePathFromNodeIndex(int pathId, int startNodeId, int endNodeId)
    {
        if (startNodeId > 0 && pathsByNodeIndex.TryGetValue(startNodeId, out var startSet))
        {
            startSet.Remove(pathId);
            if (startSet.Count == 0) pathsByNodeIndex.Remove(startNodeId);
        }
        if (endNodeId > 0 && pathsByNodeIndex.TryGetValue(endNodeId, out var endSet))
        {
            endSet.Remove(pathId);
            if (endSet.Count == 0) pathsByNodeIndex.Remove(endNodeId);
        }
    }

    private static bool IsPillarObstacle(PathSummary path)
    {
        return path != null && (path.path_type == Constants.PATH_DEFAULT || path.path_type == Constants.PATH_MOTORWAY
            || path.path_type == Constants.PATH_STREET || path.path_type == Constants.PATH_WALKWAY || IsTrackTypePath(path));
    }

    /// <summary>Include neighboring crossed cells as candidates to cover curves, cell boundaries, and pillar overhang.</summary>
    private HashSet<int> CollectPillarNeighborIds(IEnumerable<Vector2Int> cells, int ownId)
    {
        var ids = new HashSet<int>();
        foreach (var cell in cells)
        for (int x = -1; x <= 1; x++)
        for (int z = -1; z <= 1; z++)
        {
            if (!pathIdsByCell.TryGetValue(cell + new Vector2Int(x, z), out var neighbors)) continue;
            foreach (int id in neighbors) if (id != ownId) ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// Build traffic clearance volumes from current data without consulting meshes or LOD (independent of drawing order).
    /// Candidates come from neighboring cells, but each passage space is built only when a pier or arch position reaches its plan bounds,
    /// so terrain-following points are computed for roads beside the supports alone. Share the cached uncut points of neighbors.
    /// </summary>
    public PillarClearance.ObstacleQuery GetPillarObstacles(PathSummary path)
    {
        var candidates = new List<PillarClearance.Candidate>();
        foreach (int id in CollectPillarNeighborIds(GetObstacleSearchCells(path), path.path_id))
        {
            if (!pathDataCache.TryGetValue(id, out var other) || !IsPillarObstacle(other)) continue;
            if (!pathPlanBoundsById.TryGetValue(id, out Rect bounds) && !TryComputePlanBounds(other, out bounds)) continue;
            candidates.Add(new PillarClearance.Candidate { PlanBounds = bounds, HalfWidth = ObstacleHalfWidth(other), Build = () => BuildObstacleCorridor(other) });
        }
        return new PillarClearance.ObstacleQuery(candidates);
    }

    /// <summary>Implementation of RoadEquipmentGenerator.OverheadProvider. Return only paths capable of leaving the ground.</summary>
    public List<PillarClearance.Corridor> GetOverheadObstacles(PathSummary path)
    {
        var result = new List<PillarClearance.Corridor>();
        foreach (int id in CollectPillarNeighborIds(GetObstacleSearchCells(path), path.path_id))
        {
            if (!pathDataCache.TryGetValue(id, out var other) || !IsPillarObstacle(other) || !CanLeaveGround(other)) continue;
            var corridor = BuildObstacleCorridor(other);
            if (corridor != null) result.Add(corridor);
        }
        return result;
    }

    /// <summary>Crossed cells of the path. New data being generated may not have registered crossed cells; recalculate only in that case.</summary>
    private HashSet<Vector2Int> GetObstacleSearchCells(PathSummary path)
    {
        bool registered = pathDataCache.TryGetValue(path.path_id, out PathSummary current) && ReferenceEquals(current, path);
        return registered ? GetPathCoveredCellsCached(path) : GetPathCoveredCells(path);
    }

    /// <summary>Plan bounds of the designed centerline. SamplePlan follows the same Bezier controls as the designed points, so the bounds enclose them.</summary>
    private bool TryComputePlanBounds(PathSummary path, out Rect bounds)
    {
        bounds = default;
        List<Vector3> plan = IntersectionPolygonUtil.SamplePlan(path) ?? GetPathAnchorPositions(path);
        if (plan == null || plan.Count == 0) return false;
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (Vector3 p in plan)
        {
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
        }
        bounds = Rect.MinMaxRect(minX, minZ, maxX, maxZ);
        return true;
    }

    /// <summary>Passage half width: road width plus platform extensions of station tracks.</summary>
    private float ObstacleHalfWidth(PathSummary path)
    {
        var master = MasterDataStore.Instance?.GetPathMaster(path.path_variant);
        float platform = master?.platform_width ?? 0f;
        float left = 0f, right = 0f;
        if (IsTrackTypePath(path) && platform > 0f) TrackPath.ResolvePlatformExtents(path, platform, out left, out right);
        return Mathf.Max(path.width, GetRoadWidth(path.path_variant)) * 0.5f + Mathf.Max(left, right);
    }

    private PillarClearance.Corridor BuildObstacleCorridor(PathSummary path)
    {
        var master = MasterDataStore.Instance?.GetPathMaster(path.path_variant);
        if (master?.model_type == "none") return null;
        // Use the uncut centerline to retain traffic clearance inside intersections. Rendering point caches may be stale.
        var points = GetUntrimmedPoints(path, out _);
        if (points == null || points.Count < 2) return null;
        float median = 0f, sidewalk = 0f;
        // Pillars can stand only in road medians and sidewalks. Do not use track center_width, which represents between-track space (island platforms).
        if (!IsTrackTypePath(path))
        {
            median = (master?.center_width ?? 0f) * 0.5f;
            sidewalk = master?.sidewalk_width ?? 0f;
        }
        return new PillarClearance.Corridor
        {
            Points = points,
            YOffset = RoadPartHeights.BODY_ABOVE_SPLINE,
            HalfWidth = ObstacleHalfWidth(path),
            Height = TunnelSectionUtil.GetClearanceHeight(path.path_variant),
            MedianHalfWidth = median,
            SidewalkWidth = sidewalk
        };
    }

    private bool HasDetailedObject(int pathId) =>
        pathObjectsCache.TryGetValue(pathId, out GameObject go) && go != null && !(go.GetComponent<ObjectIdInfo>()?.isFarObject ?? false);

    /// <summary>Whether a variant permits height editing or does not follow terrain. Other variants follow ground and cannot pass above other paths.</summary>
    private static bool CanLeaveGround(PathSummary path)
    {
        var master = MasterDataStore.Instance?.GetPathMaster(path.path_variant);
        return master != null && (master.height_editable == true || master.follow_terrain == false);
    }

    /// <summary>
    /// Before/after path additions, changes, or removals, rebuild nearby paths with pillars and roads with outdoor equipment the path may cross above.
    /// Almost every street has equipment, so restrict detailed roads to those close to path in plan view.
    /// Nondetailed roads skip queue regeneration and only advance revision, preventing reuse of parked GameObjects.
    /// </summary>
    private void EnqueuePillarNeighborRebuilds(PathSummary path, IEnumerable<Vector2Int> cells)
    {
        if (!IsPillarObstacle(path)) return;
        bool canPassAbove = CanLeaveGround(path);
        PillarClearance.Corridor pathCorridor = null;
        // Equipment stands within road width and extends no farther than its half-width, so search up to road width from its centerline.
        bool EquipmentNear(PathSummary road)
        {
            pathCorridor ??= BuildObstacleCorridor(path);
            return pathCorridor != null
                && PillarClearance.NearXZ(GetUntrimmedPoints(road, out _), GetRoadWidth(road.path_variant), pathCorridor);
        }
        foreach (int id in CollectPillarNeighborIds(cells, path.path_id))
        {
            if (!pathDataCache.TryGetValue(id, out var neighbor)) continue;
            var master = MasterDataStore.Instance?.GetPathMaster(neighbor.path_variant);
            bool supported = (master?.height_editable ?? false)
                && ((master.pillar_spacing ?? 0f) > 0f || BrickArcadeGenerator.HasArcade(neighbor));
            bool equipped = canPassAbove && RoadEquipmentGenerator.HasOutdoorEquipment(neighbor)
                && (!HasDetailedObject(id) || EquipmentNear(neighbor));
            if (!supported && !equipped) continue;
            bool added = pendingPillarRebuildIds.Add(id);
            pendingPillarRebuildSerials[id] = pathBuildSerialCounter;
            if (!added) continue;
            pendingPowerRebuildIds.Remove(id);
            InvalidateTerrainFollowPath(neighbor);
            geometryRevisions.TryGetValue(id, out int revision);
            geometryRevisions[id] = revision + 1;
        }
    }

    /// <summary>Paths with changed LRT stop platform ranges and build serial at notification. Transfer to the rebuild queue in LateUpdate outside LOD batches.</summary>
    private readonly Dictionary<int, long> pendingLrtPlatformRebuildSerials = new Dictionary<int, long>();

    /// <summary>
    /// Rebuild paths whose LRT stop platform ranges (LrtStopPlatforms index) changed. Advance render-key revision to prevent
    /// reuse of stale parked GameObjects; queue only paths with generated objects for terrain-following regeneration (ungenerated paths
    /// read the index on next generation). The worker skips objects rebuilt after notification.
    /// Also rebuild nearby tracks (combined corridor main tracks place catenary poles outside neighbors' platform sections).
    /// </summary>
    public void RequestLrtPlatformRebuild(IEnumerable<int> pathIds)
    {
        var targets = new HashSet<int>();
        foreach (int id in pathIds)
        {
            targets.Add(id);
            if (pathDataCache.TryGetValue(id, out PathSummary path)) targets.UnionWith(CollectNearbyTrackPathIds(path));
        }
        foreach (int id in targets)
        {
            geometryRevisions.TryGetValue(id, out int revision);
            geometryRevisions[id] = revision + 1;
            if (pathDataCache.ContainsKey(id) && pathObjectsCache.TryGetValue(id, out GameObject go) && go != null)
                pendingLrtPlatformRebuildSerials[id] = pathBuildSerialCounter;
        }
    }

    private readonly Dictionary<int, long> pendingBuildingAccessRebuilds = new();

    private void OnBuildingRoadAccessChanged(IReadOnlyList<BuildingRoadAccessIndex.Opening> openings)
    {
        var changed = new HashSet<int>();
        foreach (var opening in openings)
            foreach (var position in BuildingRoadAccessIndex.Samples(opening))
                foreach (var path in GetPathsNearCircle(position, 64f))
                {
                    if (path.path_type != Constants.PATH_STREET || changed.Contains(path.path_id)) continue;
                    if (!TryGetUntrimmedPolylineShared(path.path_id, out var points) || points.Count < 2)
                    { changed.Add(path.path_id); continue; }
                    // Cells only select candidates. Filter by distance including frontage sample spacing, avoiding regeneration of entire large cells.
                    float reach = GetRoadWidth(path.path_variant) * .5f + RoadAccessPlan.MaxGap + 16f;
                    for (int i = 1; i < points.Count; i++)
                        if (Utils.XZDistanceSquared(position, PolylineUtils.ClosestPointOnSegmentXZ(position, points[i - 1], points[i])) <= reach * reach)
                        { changed.Add(path.path_id); break; }
                }
        foreach (int id in changed)
        {
            geometryRevisions.TryGetValue(id, out int revision);
            geometryRevisions[id] = revision + 1;
            pendingBuildingAccessRebuilds[id] = pathBuildSerialCounter;
        }
    }

    private void LateUpdate()
    {
        if (isLodPathBatchActive) return;
        foreach (var entry in pendingBuildingAccessRebuilds)
            if (pathDataCache.ContainsKey(entry.Key)) EnqueueTerrainFollowPath(entry.Key, entry.Value, TerrainFollowQueueReason.Other);
        pendingBuildingAccessRebuilds.Clear();
        if (pendingLrtPlatformRebuildSerials.Count > 0)
        {
            foreach (var kvp in pendingLrtPlatformRebuildSerials)
                EnqueueTerrainFollowPath(kvp.Key, kvp.Value, TerrainFollowQueueReason.Other);
            pendingLrtPlatformRebuildSerials.Clear();
        }
        if (pendingPillarRebuildIds.Count == 0) return;
        // Wait for same-frame registration/removal to finish. Reevaluate pillars drawn mid-batch using current surroundings too.
        // Skip GameObjects created after notification (such as batch phase 2) by serial when dequeuing.
        foreach (int id in pendingPillarRebuildIds)
        {
            long serial = pendingPillarRebuildSerials.TryGetValue(id, out long notified) ? notified : pathBuildSerialCounter;
            EnqueueTerrainFollowPath(id, serial,
                pendingPowerRebuildIds.Contains(id) ? TerrainFollowQueueReason.Other : TerrainFollowQueueReason.Pillar);
        }
        pendingPillarRebuildIds.Clear();
        pendingPillarRebuildSerials.Clear();
        pendingPowerRebuildIds.Clear();
    }

    private void SavePathToCache(PathSummary path)
    {
        if (path == null) return;
        bool changed = !pathDataCache.TryGetValue(path.path_id, out var oldPath)
            || oldPath.version != path.version || oldPath.status != path.status
            || oldPath.start_node_id != path.start_node_id || oldPath.end_node_id != path.end_node_id;
        if (changed)
        {
            if (oldPath != null)
            {
                EnqueuePowerNeighbors(oldPath);
                InvalidateTerrainFollowPath(oldPath);
                EnqueuePillarNeighborRebuilds(oldPath, GetPathCoveredCellsCached(oldPath));
            }
            InvalidateTerrainFollowPath(path);
        }
        // Progress advances each epoch without increasing version; update construction-front visuals and sounds on progress differences too.
        bool progressChanged = oldPath != null && ConstructionPathBuilder.Progress(oldPath) != ConstructionPathBuilder.Progress(path);
        if (progressChanged) InvalidateTerrainFollowPath(path);
        if (oldPath != null) RemovePathFromNodeIndex(path.path_id, oldPath.start_node_id, oldPath.end_node_id);
        // Matching-version responses have identical geometry, so transfer cached anchors to the new instance.
        if (!changed && !progressChanged && !ReferenceEquals(oldPath, path)
            && anchorPolylineCache.TryGetValue(path.path_id, out AnchorEntry anchorEntry) && ReferenceEquals(anchorEntry.source, oldPath))
            anchorEntry.source = path;
        // Uncut points are independent of progress, so transfer on progress-only changes too.
        if (changed) DropUntrimmedPoints(path.path_id);
        else if (!ReferenceEquals(oldPath, path)
            && untrimmedPointsCache.TryGetValue(path.path_id, out PointsEntry pointsEntry) && ReferenceEquals(pointsEntry.source, oldPath))
            pointsEntry.source = path;
        pathDataCache[path.path_id] = path;
        AddPathToNodeIndex(path);
        IndexProvisionalTrackEnds(path);
        // Construction sound points use terrain signatures from new crossed cells; synchronize after cell registration.
        if (changed) RegisterPathCells(path);
        if (changed || progressChanged) SyncConstructionSound(path);
        if (changed)
        {
            EnqueuePowerNeighbors(path);
            EnqueuePillarNeighborRebuilds(path, GetPathCoveredCellsCached(path));
        }
    }

    private void RemovePathFromCache(int pathId)
    {
        if (pathDataCache.TryGetValue(pathId, out var path))
        {
            EnqueuePowerNeighbors(path);
            InvalidateTerrainFollowPath(path);
            EnqueuePillarNeighborRebuilds(path, GetPathCoveredCellsCached(path));
            RemovePathFromNodeIndex(pathId, path.start_node_id, path.end_node_id);
            UnindexProvisionalTrackEnds(pathId);
            pathDataCache.Remove(pathId);
            DropUntrimmedPoints(pathId);
            ConstructionAmbience.Unregister(ConstructionAmbience.Kind.Path, pathId);
        }
    }

    private void EnqueuePowerNeighbors(PathSummary path)
    {
        if (!PowerLinePath.Handles(path) && !IsTrackTypePath(path)) return;
        var nodes = new HashSet<int> { path.start_node_id, path.end_node_id };
        if (IsTrackTypePath(path))
            foreach (int id in CollectNearbyTrackPathIds(path))
                if (pathDataCache.TryGetValue(id,out var rail))
                {
                    nodes.Add(rail.start_node_id);
                    nodes.Add(rail.end_node_id);
                }
        foreach (int node in nodes)
            foreach (var neighbor in GetPathsConnectedToNode(node))
            {
                if (neighbor.path_id == path.path_id || !PowerLinePath.Handles(neighbor)) continue;
                bool added = pendingPillarRebuildIds.Add(neighbor.path_id);
                pendingPillarRebuildSerials[neighbor.path_id] = pathBuildSerialCounter;
                pendingPowerRebuildIds.Add(neighbor.path_id);
                if (!added) continue;
                InvalidateTerrainFollowPath(neighbor);
                geometryRevisions.TryGetValue(neighbor.path_id, out int revision);
                geometryRevisions[neighbor.path_id] = revision + 1;
            }
    }

    /// <summary>
    /// Synchronize construction sound sites with path data. Register one point at the work front (where construction vehicles stand) only during construction; unregister otherwise.
    /// Sounding along the entire road would include inactive sections, so emit only at the same front coordinates used for vehicle placement.
    /// Pass backend coordinates matching rendering points (without FloatingOrigin offsets).
    /// </summary>
    private void SyncConstructionSound(PathSummary path)
    {
        List<Vector3> points = path.status == Constants.STATUS_CONSTRUCTING ? CalculatePathPoints(path) : null;
        if (points == null || points.Count < 2)
        {
            ConstructionAmbience.Unregister(ConstructionAmbience.Kind.Path, path.path_id);
            return;
        }
        var front = ConstructionPathBuilder.FrontPoint(path, points, UsesTunnelMesh(path, points));
        ConstructionAmbience.Register(ConstructionAmbience.Kind.Path, path.path_id, new[] { front });
    }

    private void RegisterPathData(PathSummary path)
    {
        if (path == null) return;
        SavePathToCache(path);
        if (isLodPathBatchActive)
        {
            if (path.start_node_id > 0) deferredLodIntersectionNodeIds.Add(path.start_node_id);
            if (path.end_node_id > 0) deferredLodIntersectionNodeIds.Add(path.end_node_id);
            return;
        }

        if (path.start_node_id > 0) UpdateNodeIntersectionMesh(path.start_node_id);
        if (path.end_node_id > 0) UpdateNodeIntersectionMesh(path.end_node_id);
    }

    private void UnregisterPathData(PathSummary path)
    {
        if (path == null) return;
        RemovePathFromCache(path.path_id);
        if (path.start_node_id > 0) UpdateNodeIntersectionMesh(path.start_node_id);
        if (path.end_node_id > 0) UpdateNodeIntersectionMesh(path.end_node_id);
    }

    /// <summary>
    /// Immediately destroy local caches and the specified path GameObject.
    ///
    /// Used immediately after backend path-deleting operations such as merge, without waiting for incremental reload's
    /// remove-paths-absent-from-response checks (SyncPathsInCells).
    /// Reloading is asynchronous and cancellation may prevent it from reaching deletion checks.
    /// </summary>
    public void RemovePathLocally(int pathId)
    {
        if (!pathObjectsCache.TryGetValue(pathId, out GameObject obj)) return;

        var mergeNodes = new HashSet<int>();
        if (pathDataCache.TryGetValue(pathId, out PathSummary removedPath))
        {
            // Queue before clearing crossed-cell caches to undo joining on tracks parallel to the removed track.
            EnqueueAdjacentTrackNeighborRebuilds(removedPath);
            EnqueueAdjacentRoadMergeRebuilds(removedPath, mergeNodes);
        }

        UnregisterPathDataById(pathId);
        if (removedPath != null) NotifyDepotTerminals(removedPath);
        UnregisterPathCells(pathId);
        UnregisterPathSegments(pathId);
        DropEndNodeIntersectionSignatures(removedPath);
        pathStartConnectionPoints.Remove(pathId);
        pathEndConnectionPoints.Remove(pathId);
        pathStartSidewalkSetbacks.Remove(pathId);
        pathEndSidewalkSetbacks.Remove(pathId);
        DropRoadEndJoins(pathId);
        pathVersionCache.Remove(pathId);

        if (obj != null)
        {
            DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Path, DrawLodInfo.GetDrawLod(obj, DrawLod.Normal), null);
            Destroy(obj);
        }
        RemovePathObject(pathId);
        // Recalculate endpoint intersections of ramps formerly aligned to the removed main line using the post-removal state.
        foreach (int node in mergeNodes)
        {
            if (isLodPathBatchActive) deferredLodIntersectionNodeIds.Add(node);
            else UpdateNodeIntersectionMesh(node);
        }
    }

    private void UnregisterPathDataById(int pathId)
    {
        UnregisterPathCells(pathId);
        pathPointsCache.Remove(pathId);
        pathLabelPositions.Remove(pathId);
        if (pathDataCache.TryGetValue(pathId, out var cachedPath))
        {
            UnregisterPathData(cachedPath);
        }
        else
        {
            RemovePathFromCache(pathId);
        }
    }

    /// <summary>
    /// Called after terrain tile publication. Queue paths crossing the cell (including road width; RegisterPathCells) and their endpoint intersections
    /// for regeneration. TerrainFollowRebuildWorker performs actual regeneration with view priority and the frame budget,
    /// skipping unchanged terrain signatures (crossed-cell revisions) since generation. Roads appear as Far without waiting for tiles,
    /// so this regeneration also switches to Normal/Close detail. Replace existing objects only after new meshes are ready.
    /// </summary>
    private void OnTerrainTileReadyHandler(Vector2Int cellIndex, Terrain terrain)
    {
        Utils.InvalidateHeightCache();
        InvalidateTerrainFollowCell(cellIndex);
        if (!pathIdsByCell.TryGetValue(cellIndex, out HashSet<int> pathIds)) return;

        // Synchronous bulk regeneration here would concentrate crossed-path count × spline/SplineMesh generation cost
        // into one frame, causing hitches. Delegate to the view-prioritized, frame-budgeted worker.
        foreach (int pathId in pathIds)
        {
            EnqueueTerrainFollowPath(pathId, pathBuildSerialCounter, TerrainFollowQueueReason.Terrain);
            if (!pathDataCache.TryGetValue(pathId, out PathSummary path)) continue;
            if (path.start_node_id > 0) queuedTerrainFollowNodeIds.Add(path.start_node_id);
            if (path.end_node_id > 0) queuedTerrainFollowNodeIds.Add(path.end_node_id);
        }
    }

    /// <summary>
    /// After the road regeneration queue empties, rebuild terrain-arrival intersections with view priority.
    /// UpdateNodeIntersectionMesh skips intersections whose input signatures (including surrounding/connected-road terrain) match generation time.
    /// Do not dequeue during LOD batches, which finalize intersections themselves.
    /// </summary>
    private IEnumerator RefreshQueuedIntersections()
    {
        Camera cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        Vector3 camFwd = cam != null ? Utils.GetCameraHorizontalForward(cam) : Vector3.forward;
        var nodeIds = new List<int>(queuedTerrainFollowNodeIds);
        var scores = new Dictionary<int, float>(nodeIds.Count);
        foreach (int nodeId in nodeIds) scores[nodeId] = ScoreNodeForViewPriority(nodeId, camPos, camFwd);
        nodeIds.Sort((a, b) => scores[a].CompareTo(scores[b]));
        foreach (int nodeId in nodeIds)
        {
            if (isLodPathBatchActive || queuedTerrainFollowPathIds.Count > 0) yield break;
            if (!queuedTerrainFollowNodeIds.Remove(nodeId)) continue;
            // Catch failures per node so one failure does not stop the remaining intersections.
            try { UpdateNodeIntersectionMesh(nodeId); }
            catch (System.Exception e) { Debug.LogError($"PathManager: 地形追従の交差点更新に失敗 node={nodeId}: {e}"); }
            yield return RenderWorkScheduler.Continue;
        }
    }

    private void UpdateNodeIntersectionMesh(int nodeId)
    {
        if (!nodeDataCache.TryGetValue(nodeId, out var nodeData))
        {
            Debug.LogWarning($"[PathManager] nodeData not found for nodeId={nodeId}, fetching node...");
            if (fetchingIntersectionNodeIds.Add(nodeId))
            {
                RenderWorkScheduler.Start(this, FetchNodeAndUpdateIntersection(nodeId), "PathManager");
            }
            return;
        }
        deferredNewNodeIntersectionIds.Remove(nodeId);

        List<PathSummary> connectedPaths = GetPathsConnectedToNode(nodeId);

        // Create intersections only at nodes connecting at least two physical traffic paths (excluding infrastructure, people mover, and logical public transport paths).
        // Do not use node type bits (electrified track nodes also form power networks and have electricity bits).
        // Infrastructure paths allow vertical connections (endpoint relative Y may differ from node Y), and generators exclude them from connections,
        // so do not overwrite endpoint heights with cut points. Do not create intersections at facility terminals, where tracks/pipes continue inside.
        if (!NeedsIntersection(nodeId, connectedPaths))
        {
            RemoveNodeIntersectionMesh(nodeId);
            awaitingIntersectionNodes.Remove(nodeId);
            return;
        }

        // Defensive connected-path limit (see MAX_INTERSECTION_CONNECTED_PATHS comment).
        // Do not queue excessive-count nodes in awaitingIntersectionNodes either
        // (reevaluation on every path addition cannot succeed).
        if (connectedPaths.Count > MAX_INTERSECTION_CONNECTED_PATHS)
        {
            if (overloadedIntersectionNodeIds.Add(nodeId))
            {
                Debug.LogError($"[PathManager] nodeId={nodeId} の接続パス数({connectedPaths.Count})が" +
                               $"上限({MAX_INTERSECTION_CONNECTED_PATHS})を超えています。" +
                               $"バックエンドのデータ異常が疑われるため交差点メッシュを生成しません。");
            }
            RemoveNodeIntersectionMesh(nodeId);
            awaitingIntersectionNodes.Remove(nodeId);
            return;
        }
        overloadedIntersectionNodeIds.Remove(nodeId);

        long signature = ComputeNodeIntersectionSignature(nodeId, nodeData, connectedPaths, out bool signatureKnown);
        if (signatureKnown && nodeIntersectionSignatures.TryGetValue(nodeId, out long builtSignature) && builtSignature == signature
            && ((nodeIntersectionMeshes.TryGetValue(nodeId, out GameObject unchangedMesh) && unchangedMesh != null) || slablessMergeNodes.Contains(nodeId)))
        {
            if (unchangedMesh != null && !unchangedMesh.activeSelf) unchangedMesh.SetActive(true);
            awaitingIntersectionNodes.Remove(nodeId);
            RenderWorkScheduler.CountEvent("intersection.inputUnchanged");
            return;
        }

        // Marking signatures include traffic side and connected path types, statuses, traffic directions, and orientations. Track-end buffer stops (TrackPath) depend only on these and masters too,
        // so rebuild connected paths on changes even if cut points match (markingsChanged below).
        // Also rebuild connected paths when track join S-curve allocations (TrackEndTransition) change.
        // S-curves move ballast/platform outer edges, so recompute joining for parallel tracks too.
        string transitionSignature = TrackTransitionSignature(nodeId, connectedPaths);
        string markingSignature = RoadMarkingRules.JunctionSignature(nodeId, connectedPaths) + transitionSignature;
        bool markingsChanged = !nodeMarkingSignatures.TryGetValue(nodeId, out var oldMarkingSignature) || oldMarkingSignature != markingSignature;
        nodeTransitionSignatures.TryGetValue(nodeId, out var oldTransitionSignature);
        if ((oldTransitionSignature ?? "") != transitionSignature)
        {
            if (transitionSignature.Length > 0) nodeTransitionSignatures[nodeId] = transitionSignature;
            else nodeTransitionSignatures.Remove(nodeId);
            foreach (var path in connectedPaths) EnqueueAdjacentTrackNeighborRebuilds(path);
        }

        Vector3 absolutePos = nodeData.GetAbsolutePosition();
        // Share uncut points to align main-line slopes with cut-section heights.
        var originalPoints = new Dictionary<int, List<Vector3>>();
        foreach (var p in connectedPaths) originalPoints[p.path_id] = CalculatePathPoints(p, false);
        var gradedPath = connectedPaths.Find(p => p.station_grade_profile != null && p.station_grade_profile.Count >= 2);
        if (gradedPath != null)
        {
            var nodePoints = new List<Vector3> { absolutePos };
            ApplyStationGrade(gradedPath, nodePoints);
            absolutePos.y = nodePoints[0].y;
        }
        NodeMeshResult meshResult = NodeMeshFactory.GenerateNodeMesh(nodeId, absolutePos, connectedPaths, nodeData.GetRelativeHeight(), p => originalPoints[p.path_id], p => GetIntersectionCutLimit(p, nodeId, originalPoints[p.path_id]));
        // Straight connections of same-type/width roads, highway merges absorbing ramps, and aligned track connections have no plates.
        bool slabless = meshResult != null && meshResult.Mesh == null
            && (meshResult.RoadJoin != null || meshResult.MotorwayMerge != null || meshResult.TrackJoin != null);
        // Nodes containing only tracks without ballast have no plates; attach only rail connections to the node GameObject.
        bool railsOnly = meshResult != null && meshResult.Mesh == null && meshResult.RailsOnly;
        if (!slabless && !railsOnly && (meshResult == null || meshResult.Mesh == null))
        {
            if (connectedPaths.Count >= 2)
                Debug.LogWarning($"[PathManager] meshResult or mesh is null for nodeId={nodeId}, removing mesh.");
            RemoveNodeIntersectionMesh(nodeId);
            // Connected paths may be incomplete or intersection geometry may be incalculable,
            // so track the node for reevaluation when paths arrive later.
            awaitingIntersectionNodes.Add(nodeId);
            return;
        }

        // Remove from tracking after successful intersection generation.
        awaitingIntersectionNodes.Remove(nodeId);
        if (signatureKnown) nodeIntersectionSignatures[nodeId] = signature;
        else nodeIntersectionSignatures.Remove(nodeId);

        if (slabless)
        {
            if (nodeIntersectionMeshes.TryGetValue(nodeId, out var staleMesh))
            {
                if (staleMesh != null) Destroy(staleMesh);
                nodeIntersectionMeshes.Remove(nodeId);
            }
            slablessMergeNodes.Add(nodeId);
            nodeMarkingSignatures[nodeId] = markingSignature;
        }
        else
        {
            slablessMergeNodes.Remove(nodeId);
            UpdateNodeSlab(nodeId, absolutePos, meshResult, markingSignature);
        }
        if (meshResult.MotorwayMerge != null) motorwayMerges[nodeId] = meshResult.MotorwayMerge;
        else motorwayMerges.Remove(nodeId);

        // At node-connected road/track ends, keep median ends square and let only one side place the joining pillar.
        var joinChanged = new HashSet<int>();
        int joinA = -1, joinB = -1;
        if (slabless && meshResult.RoadJoin != null) { joinA = meshResult.RoadJoin.PathA; joinB = meshResult.RoadJoin.PathB; }
        else if (slabless && meshResult.MotorwayMerge != null) { joinA = meshResult.MotorwayMerge.MainA; joinB = meshResult.MotorwayMerge.MainB; }
        else if (slabless && meshResult.TrackJoin != null) { joinA = meshResult.TrackJoin.PathA; joinB = meshResult.TrackJoin.PathB; }
        foreach (var path in connectedPaths)
        {
            bool main = slabless && (path.path_id == joinA || path.path_id == joinB);
            var join = main ? new RoadEndJoin { Continuous = true, OwnsSupport = path.path_id == Mathf.Min(joinA, joinB) } : default;
            if (SetRoadEndJoin(path, nodeId, join)) joinChanged.Add(path.path_id);
        }
        foreach (var path in connectedPaths)
            if (joinChanged.Contains(path.path_id) && (meshResult.ConnectionPoints == null || !meshResult.ConnectionPoints.ContainsKey(path.path_id)))
                OnIntersectionCutChanged(path, rebuild: pathObjectsCache.ContainsKey(path.path_id));

        if (meshResult.ConnectionPoints != null)
        {
            foreach (var kvp in meshResult.ConnectionPoints)
            {
                int pathId = kvp.Key;
                Vector3 newPoint = kvp.Value;
                bool changed = markingsChanged || joinChanged.Contains(pathId);

                if (pathDataCache.TryGetValue(pathId, out PathSummary path))
                {
                    if (path.start_node_id == nodeId)
                    {
                        if (!pathStartConnectionPoints.TryGetValue(pathId, out Vector3 current) || Vector3.Distance(current, newPoint) > 0.01f)
                        {
                            pathStartConnectionPoints[pathId] = newPoint;
                            changed = true;
                        }
                        if (ApplySidewalkSetback(pathStartSidewalkSetbacks, pathId, meshResult.SidewalkSetbacks))
                        {
                            changed = true;
                        }
                    }
                    else if (path.end_node_id == nodeId)
                    {
                        if (!pathEndConnectionPoints.TryGetValue(pathId, out Vector3 current) || Vector3.Distance(current, newPoint) > 0.01f)
                        {
                            pathEndConnectionPoints[pathId] = newPoint;
                            changed = true;
                        }
                        if (ApplySidewalkSetback(pathEndSidewalkSetbacks, pathId, meshResult.SidewalkSetbacks))
                        {
                            changed = true;
                        }
                    }

                    if (changed) OnIntersectionCutChanged(path, rebuild: true);
                }
            }
        }
    }

    /// <summary>Replace intersection plates and accessories (sidewalks, walls, rails, markings) with generated results.</summary>
    private void UpdateNodeSlab(int nodeId, Vector3 absolutePos, NodeMeshResult meshResult, string markingSignature)
    {
        if (!nodeIntersectionMeshes.TryGetValue(nodeId, out var meshGO))
        {
            meshGO = new GameObject($"NodeMesh_{nodeId}");
            meshGO.transform.position = absolutePos;
            meshGO.transform.rotation = Quaternion.identity;

            meshGO.AddComponent<MeshFilter>();
            meshGO.AddComponent<MeshRenderer>();

            var visualModeObject = meshGO.AddComponent<VisualModeObject>();
            visualModeObject.objectType = InputObjectType.PATH;
            visualModeObject.preventMaterialChange = true;
            visualModeObject.useChildRenderers = true;
            visualModeObject.InitializeMaterials();

            nodeIntersectionMeshes[nodeId] = meshGO;
        }
        else
        {
            meshGO.transform.position = absolutePos;
            if (!meshGO.activeSelf) meshGO.SetActive(true);
        }

        var section = meshGO.GetComponent<PathSection>() ?? meshGO.AddComponent<PathSection>();
        section.IsUnderground = TunnelSectionUtil.IsTunnelPoint(absolutePos,
            TunnelSectionUtil.GetClearanceHeight(GetPathsConnectedToNode(nodeId)[0].path_variant));

        MeshFilter mf = meshGO.GetComponent<MeshFilter>();
        var owner = meshGO.GetComponent<IntersectionMeshOwner>();
        if (owner == null) owner = meshGO.AddComponent<IntersectionMeshOwner>();
        if (owner.OwnedMesh != meshResult.Mesh) MeshBakeService.Release(owner.OwnedMesh);
        owner.OwnedMesh = meshResult.Mesh;
        mf.sharedMesh = meshResult.Mesh;

        MeshRenderer mr = meshGO.GetComponent<MeshRenderer>();
        if (meshResult.Material != null)
        {
            mr.material = meshResult.Material;
        }

        var markingSurface = meshGO.GetComponent<RoadMarkingSurface>();
        if (meshResult.Markings != null)
        {
            if (markingSurface == null) markingSurface = meshGO.AddComponent<RoadMarkingSurface>();
            markingSurface.Configure(meshResult.Markings);
        }
        else if (markingSurface != null) Destroy(markingSurface);
        nodeMarkingSignatures[nodeId] = markingSignature;

        UpdateNodeSidewalkMesh(meshGO, meshResult);
        UpdateNodeRailMesh(meshGO, meshResult);
        TrafficSignalBuilder.Apply(meshGO, meshResult.TrafficSignals);
        var visual = meshGO.GetComponent<VisualModeObject>();
        visual.InitializeMaterials();
        visual.ApplyVisualMode(VisualModeHandler.Instance != null ? VisualModeHandler.Instance.GetMode(InputObjectType.PATH) : VisualMode.MODE_NORMAL);
    }

    /// <summary>
    /// Apply sidewalk setbacks from intersection mesh results to the cache.
    /// Return true only when changed (triggers caller road regeneration).
    /// Remove entries when setbacks disappear, such as when an adjacent road gains sidewalks.
    /// </summary>
    private static bool ApplySidewalkSetback(Dictionary<int, SidewalkSetback> store, int pathId, Dictionary<int, SidewalkSetback> newSetbacks)
    {
        SidewalkSetback newValue = default;
        if (newSetbacks != null)
        {
            newSetbacks.TryGetValue(pathId, out newValue);
        }
        store.TryGetValue(pathId, out SidewalkSetback current);
        bool changed = Mathf.Abs(current.LeftOutwardSetback - newValue.LeftOutwardSetback) > 0.01f
                    || Mathf.Abs(current.RightOutwardSetback - newValue.RightOutwardSetback) > 0.01f;
        if (!changed)
        {
            return false;
        }
        if (newValue.LeftOutwardSetback <= 0f && newValue.RightOutwardSetback <= 0f)
        {
            store.Remove(pathId);
        }
        else
        {
            store[pathId] = newValue;
        }
        return true;
    }



    private void UpdateNodeSidewalkMesh(GameObject nodeMeshObject, NodeMeshResult meshResult)
    {
        const string sidewalkChildName = "Sidewalk";
        Transform child = nodeMeshObject.transform.Find(sidewalkChildName);

        if (meshResult.SidewalkConnectors == null || meshResult.SidewalkConnectors.Count == 0)
        {
            // Do not retain old sidewalk connector objects at intersections no longer needing them.
            // This also removes stale visuals when roads change to variants without sidewalks.
            if (child != null)
            {
                Destroy(child.gameObject);
            }
            return;
        }

        GameObject sidewalkGO;
        if (child == null)
        {
            // Group intersection sidewalks as NodeMesh children.
            // Give each corner its own child so each SplineMesh can rebuild independently.
            sidewalkGO = new GameObject(sidewalkChildName);
            sidewalkGO.transform.SetParent(nodeMeshObject.transform, false);
        }
        else
        {
            sidewalkGO = child.gameObject;
            // Changes to connected road count/width alter required sidewalk spline counts and geometry.
            // Destroying children and rebuilding from current results is less prone to inconsistent state than incremental updates.
            for (int i = sidewalkGO.transform.childCount - 1; i >= 0; i--)
            {
                Destroy(sidewalkGO.transform.GetChild(i).gameObject);
            }
        }

        sidewalkGO.transform.localPosition = Vector3.zero;
        sidewalkGO.transform.localRotation = Quaternion.identity;
        sidewalkGO.transform.localScale = Vector3.one;

        for (int i = 0; i < meshResult.SidewalkConnectors.Count; i++)
        {
            // SidewalkConnectorSpec holds geometry calculation results only.
            // Create actual visuals by passing the same sidewalk prefab used for normal roads into SplineMesh
            // (shared through RoadPath.CreateSidewalkConnectorObject).
            RoadPath.CreateSidewalkConnectorObject(sidewalkGO.transform, meshResult.SidewalkConnectors[i], i);
        }
    }

    /// <summary>
    /// Instantiate rail connectors at track nodes (switches/level crossings) as NodeMesh children.
    /// As in UpdateNodeSidewalkMesh, destroy children and rebuild from current results
    /// (connected track additions/removals and orientation changes alter connector counts and geometry).
    /// TrackIntersectionMeshGenerator calculates geometry; TrackPath.CreateRailConnectorObject instantiates it.
    /// </summary>
    private void UpdateNodeRailMesh(GameObject nodeMeshObject, NodeMeshResult meshResult)
    {
        const string railChildName = "Rail";
        Transform child = nodeMeshObject.transform.Find(railChildName);

        if (meshResult.RailConnectors == null || meshResult.RailConnectors.Count == 0)
        {
            // Do not retain old rails at intersections where tracks were removed.
            if (child != null)
            {
                Destroy(child.gameObject);
            }
            return;
        }

        GameObject railGO;
        if (child == null)
        {
            railGO = new GameObject(railChildName);
            railGO.transform.SetParent(nodeMeshObject.transform, false);
        }
        else
        {
            railGO = child.gameObject;
            for (int i = railGO.transform.childCount - 1; i >= 0; i--)
            {
                Destroy(railGO.transform.GetChild(i).gameObject);
            }
        }

        railGO.transform.localPosition = Vector3.zero;
        railGO.transform.localRotation = Quaternion.identity;
        railGO.transform.localScale = Vector3.one;

        for (int i = 0; i < meshResult.RailConnectors.Count; i++)
        {
            TrackPath.CreateRailConnectorObject(railGO.transform, meshResult.RailConnectors[i], i);
        }
    }

    private void RemoveNodeIntersectionMesh(int nodeId)
    {
        nodeMarkingSignatures.Remove(nodeId);
        nodeTransitionSignatures.Remove(nodeId);
        motorwayMerges.Remove(nodeId);
        slablessMergeNodes.Remove(nodeId);
        // Clear clipping when intersections disappear; retaining old cut points leaves missing road ends.
        foreach (var path in GetPathsConnectedToNode(nodeId))
        {
            bool changed = SetRoadEndJoin(path, nodeId, default);
            if (path.start_node_id == nodeId)
            {
                changed |= pathStartConnectionPoints.Remove(path.path_id);
                pathStartSidewalkSetbacks.Remove(path.path_id);
            }
            if (path.end_node_id == nodeId)
            {
                changed |= pathEndConnectionPoints.Remove(path.path_id);
                pathEndSidewalkSetbacks.Remove(path.path_id);
            }
            if (changed) OnIntersectionCutChanged(path, rebuild: pathObjectsCache.ContainsKey(path.path_id));
        }
        if (nodeIntersectionMeshes.TryGetValue(nodeId, out var meshGO))
        {
            Destroy(meshGO);
            nodeIntersectionMeshes.Remove(nodeId);
        }
        nodeIntersectionSignatures.Remove(nodeId);
        deferredNewNodeIntersectionIds.Remove(nodeId);
        // Remove deleted nodes from the intersection generation wait list too.
        awaitingIntersectionNodes.Remove(nodeId);
    }

    /// <summary>
    /// Clean up roads whose intersection cut points/setbacks changed. Advance render-key revision to prevent reusing visible or parked GameObjects
    /// built with old cut points. Request rebuilding if rebuild is true. Use render LOD evaluation (DeterminePathDrawLod), so
    /// do not generate directly; enqueue in the batch intersection phase during LOD batches, or the regeneration queue otherwise.
    /// </summary>
    private void OnIntersectionCutChanged(PathSummary path, bool rebuild)
    {
        BumpConnectionRevision(path.path_id);
        geometryRevisions.TryGetValue(path.path_id, out int revision);
        geometryRevisions[path.path_id] = revision + 1;
        if (!rebuild) return;
        if (isLodPathBatchActive)
        {
            deferredIntersectionPathRebuildIds.Add(path.path_id);
            deferredIntersectionRebuildSerials[path.path_id] = pathBuildSerialCounter;
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"PathManager: LOD一括更新 交差点由来の再生成を延期 path{path.path_id}");
            return;
        }
        EnqueueTerrainFollowPath(path.path_id, pathBuildSerialCounter, TerrainFollowQueueReason.Other);
    }

    /// <summary>When deleting roads, force both endpoint intersections to recalculate on the next update.</summary>
    private void DropEndNodeIntersectionSignatures(PathSummary path)
    {
        if (path == null) return;
        nodeIntersectionSignatures.Remove(path.start_node_id);
        nodeIntersectionSignatures.Remove(path.end_node_id);
    }

    private void ClearIntersectionMeshes()
    {
        motorwayMerges.Clear();
        slablessMergeNodes.Clear();
        roadEndJoins.Clear();
        nodeIntersectionSignatures.Clear();
        deferredNewNodeIntersectionIds.Clear();
        nodeMarkingSignatures.Clear();
        nodeTransitionSignatures.Clear();
        foreach (var meshGO in nodeIntersectionMeshes.Values)
        {
            if (meshGO != null)
            {
                Destroy(meshGO);
            }
        }
        nodeIntersectionMeshes.Clear();
    }

    private bool IsPathInCells(int pathId, GameObject pathObj, List<Vector2Int> cells)
    {
        if (pathCellsById.TryGetValue(pathId, out HashSet<Vector2Int> pathCells))
        {
            foreach (var cell in cells)
            {
                if (pathCells.Contains(cell)) return true;
            }
            return false;
        }

        if (pathDataCache.TryGetValue(pathId, out PathSummary pathData))
        {
            var startPointList = pathData.GetStartPointList();
            if (startPointList != null && startPointList.Count >= 2)
            {
                return IsPointInCells(GetPathPointPosition(startPointList), cells);
            }
            var waypointList = pathData.GetWaypointList();
            if (waypointList != null && waypointList.Count > 0)
            {
                return IsPointInCells(waypointList[0].GetAbsolutePosition(), cells);
            }
        }

        if (pathObj != null)
        {
            foreach (Transform child in pathObj.transform)
            {
                if (child.CompareTag(WAYPOINT_TAG))
                {
                    return IsPointInCells(child.position, cells);
                }
            }

            MeshFilter mf = pathObj.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && mf.sharedMesh.vertexCount > 0)
            {
                return IsPointInCells(pathObj.transform.TransformPoint(mf.sharedMesh.vertices[0]), cells);
            }
        }

        return false;
    }

    /// <summary>Version when crossed cells were calculated. Geometry changes only with version, so skip reregistration at matching versions.</summary>
    private readonly Dictionary<int, int> pathCellsVersion = new Dictionary<int, int>();

    /// <summary>
    /// Plan (XZ) bounds of each registered path's designed centerline, kept with its crossed cells.
    /// Pier clearance compares these against each pier before building terrain-following points.
    /// </summary>
    private readonly Dictionary<int, Rect> pathPlanBoundsById = new Dictionary<int, Rect>();

    private void RegisterPathCells(PathSummary path)
    {
        if (path == null) return;
        if (pathCellsById.ContainsKey(path.path_id) && pathCellsVersion.TryGetValue(path.path_id, out int registeredVersion)
            && registeredVersion == path.version) return;

        UnregisterPathCells(path.path_id);
        HashSet<Vector2Int> cells = GetPathCoveredCells(path);
        pathCellsById[path.path_id] = cells;
        pathCellsVersion[path.path_id] = path.version;
        if (TryComputePlanBounds(path, out Rect planBounds)) pathPlanBoundsById[path.path_id] = planBounds;

        foreach (var cell in cells)
        {
            if (!pathIdsByCell.TryGetValue(cell, out HashSet<int> ids))
            {
                ids = new HashSet<int>();
                pathIdsByCell[cell] = ids;
            }
            ids.Add(path.path_id);
        }
        unregisteredPathObjectIds.Remove(path.path_id);
    }

    /// <summary>Register a path GameObject. Track in unregisteredPathObjectIds if crossed cells are unregistered.</summary>
    private void SetPathObject(int pathId, GameObject pathGO)
    {
        pathObjectsCache[pathId] = pathGO;
        if (!pathCellsById.ContainsKey(pathId)) unregisteredPathObjectIds.Add(pathId);
    }

    private void RemovePathObject(int pathId)
    {
        pathObjectsCache.Remove(pathId);
        unregisteredPathObjectIds.Remove(pathId);
    }

    private void UnregisterPathCells(int pathId)
    {
        pathCellsVersion.Remove(pathId);
        pathPlanBoundsById.Remove(pathId);
        if (pathObjectsCache.ContainsKey(pathId)) unregisteredPathObjectIds.Add(pathId);
        if (!pathCellsById.TryGetValue(pathId, out HashSet<Vector2Int> cells)) return;

        foreach (var cell in cells)
        {
            if (!pathIdsByCell.TryGetValue(cell, out HashSet<int> ids)) continue;
            ids.Remove(pathId);
            if (ids.Count == 0) pathIdsByCell.Remove(cell);
        }

        pathCellsById.Remove(pathId);
    }

    private HashSet<Vector2Int> GetPathCoveredCells(PathSummary path)
    {
        HashSet<Vector2Int> cells = new HashSet<Vector2Int>();
        List<Vector3> anchors = path.curved && !(MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.single_span ?? false)
            ? IntersectionPolygonUtil.SamplePlan(path) ?? GetPathAnchorPositions(path) : GetPathAnchorPositions(path);
        if (anchors == null || anchors.Count == 0) return cells;

        float halfWidth = Mathf.Max(GetRoadWidth(path.path_variant), path.width) * 0.5f;
        for (int i = 0; i < anchors.Count; i++)
        {
            AddCellsAroundPoint(cells, anchors[i], halfWidth);
            if (i < anchors.Count - 1)
            {
                AddCellsAlongSegment(cells, anchors[i], anchors[i + 1], halfWidth);
            }
        }

        return cells;
    }

    private List<Vector3> GetPathAnchorPositions(PathSummary path)
    {
        List<Vector3> anchors = new List<Vector3>();

        var startPointList = path.GetStartPointList();
        if (startPointList != null && startPointList.Count >= 2)
        {
            anchors.Add(GetPathPointFlatPosition(startPointList));
        }

        var waypointList = path.GetWaypointList();
        if (waypointList != null)
        {
            foreach (var wp in waypointList)
            {
                if (wp.position != null && wp.position.Length >= 2)
                {
                    anchors.Add(wp.GetAbsolutePosition());
                }
            }
        }

        var endPointList = path.GetEndPointList();
        if (endPointList != null && endPointList.Count >= 2)
        {
            anchors.Add(GetPathPointFlatPosition(endPointList));
        }

        return anchors;
    }

    private Vector3 GetPathPointFlatPosition(List<float> point)
    {
        if (point == null || point.Count < 2) return Vector3.zero;
        float z = point.Count >= 3 ? point[2] : point[1];
        return new Vector3(point[0], 0f, z);
    }

    private void AddCellsAlongSegment(HashSet<Vector2Int> cells, Vector3 start, Vector3 end, float margin)
    {
        float cellSize = TerrainManager.CellSize;
        float distance = Vector2.Distance(new Vector2(start.x, start.z), new Vector2(end.x, end.z));
        int samples = Mathf.Max(1, Mathf.CeilToInt(distance / (cellSize * 0.5f)));

        for (int i = 0; i <= samples; i++)
        {
            float t = samples > 0 ? (float)i / samples : 0f;
            AddCellsAroundPoint(cells, Vector3.Lerp(start, end, t), margin);
        }
    }

    private void AddCellsAroundPoint(HashSet<Vector2Int> cells, Vector3 point, float margin)
    {
        float cellSize = TerrainManager.CellSize;
        int minX = Mathf.FloorToInt((point.x - margin) / cellSize);
        int maxX = Mathf.FloorToInt((point.x + margin) / cellSize);
        int minZ = Mathf.FloorToInt((point.z - margin) / cellSize);
        int maxZ = Mathf.FloorToInt((point.z + margin) / cellSize);

        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                cells.Add(new Vector2Int(x, z));
            }
        }
    }

    private bool IsPointInCells(Vector3 position, List<Vector2Int> cells)
    {
        foreach (var cell in cells)
        {
            CalculateCellBounds(cell, out float startX, out float endX, out float startZ, out float endZ);
            if (position.x >= startX && position.x < endX && 
                position.z >= startZ && position.z < endZ)
            {
                return true;
            }
        }
        return false;
    }

    public List<NodeData> QueryNearbyNodes(Vector3 pos, float radius)
    {
        return nodeGrid?.Query(pos, radius) ?? new List<NodeData>();
    }

    public List<PathSegment> QueryNearbySegments(Vector3 pos, float radius)
    {
        return segmentGrid?.Query(pos, radius) ?? new List<PathSegment>();
    }
    
    private Vector3 GetPathPointPosition(List<float> point)
    {
        if (point == null || point.Count < 2)
        {
            Debug.LogWarning("PathManager: 不正なパス座標データ");
            return Vector3.zero;
        }

        if (point.Count >= 3)
        {
            Vector3 scaledPos = new Vector3(point[0], point[1], point[2]);
                float groundHeight = Utils.GetGroundHeight(new Vector3(scaledPos.x, 1000f, scaledPos.z));
                return new Vector3(scaledPos.x, groundHeight + scaledPos.y + RoadPartHeights.ROAD_SPLINE_OFFSET, scaledPos.z);
        }
        else
        {
            Vector3 pos = new Vector3(point[0], 1000.0f, point[1]);
            pos.y = Utils.GetGroundHeight(pos) + RoadPartHeights.ROAD_SPLINE_OFFSET;
            return pos;
        }
    }

    public float GetRoadWidth(int pathSubtype)
    {
        const float defaultBackendWidth = 4.0f;

        if (pathSubtype == 0)
        {
            return defaultBackendWidth;
        }

        var masterStore = MasterDataStore.Instance;
        if (masterStore == null || !masterStore.IsInitialized)
        {
            return defaultBackendWidth;
        }

        var masters = masterStore.GetPathMasterDictionary();
        if (masters != null && masters.TryGetValue(pathSubtype, out var master))
        {
            return master.width;
        }

        return defaultBackendWidth;
    }

    public IEnumerator FetchPath(int pathId)
    {
        yield return ApiClient.Get<ConstructionPathSummary>(
            $"/paths/{pathId}",
            path =>
            {
                if (path != null)
                    CreateOrUpdatePath(path);
            },
            error => Debug.LogError($"FetchPath({pathId}) failed: {error}")
        );
    }

    private IEnumerator FetchNodeAndUpdateIntersection(int nodeId)
    {
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[PathManager] FetchNodeAndUpdateIntersection: fetching node {nodeId}");
        // Cancellation does not invoke callbacks, so guarantee flag cleanup in finally too.
        try
        {
            yield return ApiClient.Get<NodeData>(
                $"/nodes/{nodeId}",
                node =>
                {
                    fetchingIntersectionNodeIds.Remove(nodeId);
                    if (node != null)
                    {
                        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[PathManager] FetchNodeAndUpdateIntersection: node {nodeId} fetched successfully.");
                        RegisterNodeData(node, out _);
                        UpdateNodeIntersectionMesh(node.node_id);
                    }
                    else
                    {
                        Debug.LogWarning($"[PathManager] FetchNodeAndUpdateIntersection: node {nodeId} response was null.");
                        RemoveNodeIntersectionMesh(nodeId);
                    }
                },
                error =>
                {
                    fetchingIntersectionNodeIds.Remove(nodeId);
                    Debug.LogError($"[PathManager] FetchNodeAndUpdateIntersection: node {nodeId} fetch failed: {error}");
                    RemoveNodeIntersectionMesh(nodeId);
                }
            );
        }
        finally
        {
            fetchingIntersectionNodeIds.Remove(nodeId);
        }
    }

    /// <summary>
    /// Generate mesh collider physics data asynchronously on another thread (Physics.BakeMesh),
    /// then assign it to the collider component after baking finishes.
    /// This reduces stuttering caused by physics baking on the main thread.
    /// </summary>
    private IEnumerator SetColliderAsync(GameObject targetGO, Mesh mesh)
    {
        if (mesh == null) yield break;

        Task bakeTask = MeshBakeService.Bake(mesh);
        yield return Utils.WaitTask(bakeTask);

        if (targetGO != null)
        {
            MeshCollider collider = targetGO.GetComponent<MeshCollider>();
            if (collider == null)
            {
                collider = targetGO.AddComponent<MeshCollider>();
            }
            collider.sharedMesh = mesh;
            // Visibility settings may change during baking, so apply current state to the added collider.
            var visual = targetGO.GetComponent<VisualModeObject>();
            var info = targetGO.GetComponent<ObjectIdInfo>();
            if (visual != null)
                visual.ApplyVisualMode(VisualModeHandler.Instance?.GetMode(InputObjectType.PATH) ?? VisualMode.MODE_NORMAL);
            else if (info != null)
                ObjectVisibilityFilter.ApplyToObject(targetGO, InputObjectType.PATH, info.id);
        }
    }

}

[Serializable]
public class NodeGeometryResponse
{
    public List<NodeData> nodes;
}

[System.Serializable]
public class NodeData
{
    public int node_id;
    public int? station_path_id;
    public int node_temp_id;
    public float[] position;

    /// <summary>
    /// Node type bitmask (OR of Constants.NODE_ROAD / NODE_RAIL / … and bits for each infrastructure good).
    /// Welding is permitted when this mask & path mask is nonzero. Use InfraNetworkUtil.IsInfraMask
    /// to test for infrastructure bits (whether this is an infrastructure node).
    /// Whether this is an infrastructure facility terminal node (automatically created by the backend on placement)
    /// cannot be determined from the node's own type property alone.
    /// The node API uses one-way agent-to-node references without exposing owner facilities,
    /// so identify terminals with BuildingManager's reverse index built from building agents' infra_node_id
    /// (TryGetInfraTerminalOwner).
    /// </summary>
    public int type;

    public Vector3 PositionAsVector3()
    {
        if (position.Length >= 3)
        {
            return new Vector3(position[0], position[1], position[2]);
        }
        else
        {
            float groundHeight = Utils.GetGroundHeight(new Vector3(position[0], 1000f, position[1]));
            return new Vector3(position[0], groundHeight, position[1]);
        }
    }
    
    public float GetRelativeHeight()
    {
        if (position.Length >= 3)
        {
            return position[1];
        }
        return 0f;
    }
    
    public Vector3 GetAbsolutePosition()
    {
        if (position.Length >= 3)
        {
            float groundHeight = Utils.GetGroundHeight(new Vector3(position[0], 1000f, position[2]));
            return new Vector3(position[0], groundHeight + position[1], position[2]);
        }
        else
        {
            return PositionAsVector3();
        }
    }
}

[System.Serializable]
public class PathWaypoint
{
    public float[] position;

    /// <summary>
    /// Incoming control point (cubic Bezier P2), controlling entry from the previous anchor into this waypoint.
    /// Stored in backend coordinates [x, relativeY, z].
    /// </summary>
    public float[] control_in;

    /// <summary>
    /// Outgoing control point (cubic Bezier P1), controlling departure toward the next anchor.
    /// Stored in backend coordinates [x, relativeY, z].
    /// </summary>
    public float[] control_out;
    
    public Vector3 PositionAsVector3()
    {
        if (position.Length >= 3)
        {
            return new Vector3(position[0], position[1], position[2]);
        }
        else
        {
            float groundHeight = Utils.GetGroundHeight(new Vector3(position[0], 1000f, position[1]));
            return new Vector3(position[0], groundHeight, position[1]);
        }
    }
    
    public float GetRelativeHeight()
    {
        if (position.Length >= 3)
        {
            return position[1];
        }
        return 0f;
    }
    
    public Vector3 GetAbsolutePosition()
    {
        if (position.Length >= 3)
        {
            float groundHeight = Utils.GetGroundHeight(new Vector3(position[0], 1000f, position[2]));
            return new Vector3(position[0], groundHeight + position[1], position[2]);
        }
        else
        {
            return PositionAsVector3();
        }
    }

    /// <summary>
    /// Return terrain elevation beneath the associated anchor (this waypoint).
    /// Absolute control coordinates use the anchor's terrain rather than terrain under control XZ.
    /// Using terrain beneath control XZ leaks intermediate cuts/hills into the curve,
    /// conflicting with backend non-following-span interpolation based only on absolute anchor elevations
    /// (geo_path_depth.sample_absolute_path_points).
    /// </summary>
    private float GetAnchorGroundHeight()
    {
        float x = position[0];
        float z = position.Length >= 3 ? position[2] : position[1];
        return Utils.GetGroundHeight(new Vector3(x, 1000f, z));
    }

    /// <summary>
    /// Return absolute coordinates of the incoming control.
    /// Convert backend [x, relativeY, z] into absolute Unity coordinates
    /// (terrain reference is the associated anchor; see GetAnchorGroundHeight).
    /// Return null if control_in is unset.
    /// </summary>
    public Vector3? GetControlInAbsolute()
    {
        if (control_in == null || control_in.Length < 3) return null;
        float groundHeight = GetAnchorGroundHeight();
        return new Vector3(control_in[0], groundHeight + control_in[1], control_in[2]);
    }

    /// <summary>
    /// Return absolute coordinates of the outgoing control.
    /// Convert backend [x, relativeY, z] into absolute Unity coordinates
    /// (terrain reference is the associated anchor; see GetAnchorGroundHeight).
    /// Return null if control_out is unset.
    /// </summary>
    public Vector3? GetControlOutAbsolute()
    {
        if (control_out == null || control_out.Length < 3) return null;
        float groundHeight = GetAnchorGroundHeight();
        return new Vector3(control_out[0], groundHeight + control_out[1], control_out[2]);
    }
}

public struct PathSegment
{
    public GeneratedModels.PathSummary path;
    public int segmentIndex;
    public Vector3 a;
    public Vector3 b;
}

[System.Serializable]
public class PathMasterItem
{
    public bool streetlight_type_allowed;
    public int default_streetlight_type;
    public bool fence_type_allowed;
    public int default_fence_type;
    public bool street_tree_type_allowed;
    public int default_street_tree_type;
    public bool bench_type_allowed;
    public int default_bench_type;
    public bool bin_type_allowed;
    public int default_bin_type;
    public bool planter_type_allowed;
    public int default_planter_type;
    public bool advert_type_allowed;
    public int default_advert_type;
    public bool utility_mode_allowed;
    public int default_utility_mode;

    public int name;
    public float width;
    public float? sidewalk_width;
    public float? side_width;
    public float? tree_spacing;
    public int? lanes;
    public float? wall_width;
    public float? center_width;
    public float? pillar_spacing;
    public float? max_span;
    public float? height;
    public bool? height_editable;
    public bool frontage_allowed;

    /// <summary>
    /// Whether the variant may be placed outside the coastline (offshore/underwater; submarine power lines and oil pipelines).
    /// Exempt from sea/lake crossing checks during placement. Same column as backend coastline validation exemptions.
    /// </summary>
    public bool over_water_allowed;

    /// <summary>
    /// Whether this is an interchange ramp variant. The backend detects IC/JCT from connected ramp components
    /// (displayed by InterchangeManager). Rendering treats non-ramp highways as main lines
    /// and joins/integrates ramps into their road surfaces (RoadAdjacency / MotorwayMergeLayout).
    /// </summary>
    public bool ramp;

    /// <summary>
    /// Minimum permitted height (m). Only variants with negative values (underground roads, metro tracks, etc.)
    /// allow underground construction (relative Y &lt; 0). Treat null as 0 (underground prohibited).
    /// </summary>
    public float? min_height;
    public int type;
    public string display_name;
    public string description;

    /// <summary>
    /// Product IDs of infrastructure goods carried by an infrastructure path variant (PATH_ELECTRICITY/PATH_WATER).
    /// Used to distinguish network types (power lines and water pipes are separate networks). Null for non-infrastructure variants.
    /// </summary>
    public List<int> infra_products;

    /// <summary>
    /// Track (PATH_TRACK/PATH_MONORAIL) catenary pole spacing (m).
    /// Null or nonpositive means nonelectrified: draw neither wires nor poles/beams.
    /// Null for non-track variants.
    /// </summary>
    public float? wire_spacing;

    /// <summary>
    /// Station path platform width (m). Null or nonpositive means no platforms (ordinary tracks/roads).
    /// Station variant width is the same track-corridor width as normal tracks; platforms extend
    /// this far beyond ballast on one or both sides. Single-track placement follows path
    /// platform_side (inner/outer/both, resolved using city.traffic_hand);
    /// stations with lanes at least 2 use both outer sides (TrackPath.ResolvePlatformExtents).
    /// </summary>
    public float? platform_width;

    /// <summary>
    /// Width (m) of exclusive areas (buildings) at both ends of station building paths (PATH_STATION_HOUSE).
    /// Dimension perpendicular to the terminal segment. Null uses STATION_HOUSE_DEFAULT_WIDTH.
    /// Null for non-station-building variants.
    /// </summary>
    public float? house_width;

    /// <summary>
    /// Depth (m) of exclusive areas (buildings) at both ends of station building paths (PATH_STATION_HOUSE).
    /// Dimension along the terminal segment. The rectangle extends **outward** from the endpoint by this distance
    /// (its inner edge lies on the endpoint). Null uses STATION_HOUSE_DEFAULT_DEPTH.
    /// Null for non-station-building variants.
    /// </summary>
    public float? house_depth;
    public string house_placement;

    /// <summary>
    /// Lateral offset (m) of station building path (PATH_STATION_HOUSE) exclusive areas (buildings).
    /// Positive is right when facing outward from the endpoint (=building local +X), shifting both terminal buildings
    /// in the same relative direction. Used for variants whose building geometry is offset from
    /// the connecting bridge attachment. Null/0 means no shift. Null for non-station-building variants.
    /// </summary>
    public float? house_offset;

    /// <summary>
    /// Height offset (m) for 3D rendering. Render meshes at path relative height (relY) minus base_height.
    /// Backend logic/intersection checks use unchanged relY and are unaffected by base_height.
    ///
    /// Example: with base_height=20 for high-voltage power lines, a path placed at relY=20 renders at 0m (ground),
    /// while the prefab itself provides 20m height, keeping lines visibly above ground.
    /// At relY=25, rendering height is 5m and poles extend by 5m.
    /// Null means 0m (no shift, preserving existing behavior).
    /// </summary>
    public float? base_height;

    /// <summary>
    /// Whether to stretch one body between pillars (anchors) instead of subdividing along terrain.
    /// When true, body is not tiled every MAX_EXTRUDE_TILE_LENGTH (2.5m);
    /// stretch a single piece over each straight span between anchors (start, waypoints, end).
    /// Used for viaducts/girder bridges where road surfaces span supports independently of terrain relief.
    /// Null means false (existing terrain-following subdivision).
    /// </summary>
    public bool? single_span;

    /// <summary>
    /// Mesh generator type. PathMeshFactory selects generators by this value.
    ///   "default" → RoadPath
    ///   "track"   → TrackPath
    ///   "none"    → no mesh generation (such as routing-only paths)
    /// Null or empty means "default".
    /// </summary>
    public string model_type;

    /// <summary>
    /// Whether curved placement is allowed. If false, hide the UI straight/curve toggle
    /// and allow only straight placement. Null means true (curves allowed).
    /// </summary>
    public bool? curve_allowed;

    /// <summary>
    /// Alignment constraint: minimum permitted curve radius (m). 0 or null means unrestricted. Local placement estimates
    /// (RoadPlanManager.CheckAlignmentViolations) use this for red warnings, HUD, and commit-button feedback
    /// (backend geo_path_alignment provides authoritative checks).
    /// </summary>
    public float? min_curve_radius;

    /// <summary>Alignment constraint: maximum permitted vertical gradient (%). 0 or null means unrestricted.</summary>
    public float? max_grade;
    /// <summary>Elevation smoothing distance (m) for gradient evaluation. Minimum 10m, shared with the backend.</summary>
    public float? smooth_factor;

    /// <summary>
    /// Material name representing the road surface (`Assets/Resources/` .mat name).
    /// Intersection asphalt uses this to match connected road surfaces: asphalt when all roads are paved,
    /// soil when unpaved roads are involved, for example.
    /// Null/empty resolves from the body prefab material (existing behavior).
    /// </summary>
    public string body_material;

    /// <summary>
    /// Whether spans (between anchors) follow terrain. False variants (girder bridges, etc.)
    /// do not snap even relY=0 spans to terrain; interpolate only absolute anchor elevations
    /// to form horizontal decks spanning cuts and valleys.
    /// Backend intersection checks (triggering absolute elevation comparisons) read the same flag.
    /// Null means true (existing terrain following).
    /// </summary>
    public bool? follow_terrain;
}

[System.Serializable]
public class PathMasterResponse
{
    public Dictionary<int, PathMasterItem> paths;
}

[Serializable]
public sealed class PagedPathListResponse
{
    public List<ConstructionPathSummary> paths;
    public int? next_cursor;
}

/// <summary>
/// State at road GameObject (root) generation. Travels with the object through LOD cache parking/restoration,
/// allowing identification of its terrain/data inputs (PathManager.IsTerrainCurrent).
/// buildSerial is the generation serial, identifying objects built after regeneration queue entries.
/// </summary>
public sealed class PathBuildStamp : MonoBehaviour
{
    public long buildSerial;
    public long terrainSignature;
    public bool terrainKnown;
    public int version;
    public int status;
    public float progress;
    public int geometryRevision;
}
