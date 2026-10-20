
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using GeneratedModels;

/// <summary>
/// InputHandler for terrain objects.
/// Integrates point selection, markers, and parcel editor functionality.
/// </summary>
public class TerrainInputHandler : MonoBehaviour, IInputHandler
{
    public int ObjectTypeId => InputObjectType.TERRAIN;
    [Header("Marker Settings")]
    public GameObject markerPrefab;
    public Material markerMaterial;
    public float markerSize = 1.0f;
    
    [Header("References")]
    public ParcelEditorManager parcelEditorManager;
    public PathManager pathManager;
    
    [Header("Snapping Settings")]
    public float snapThreshold = 10.0f;     // Node/path snap threshold (10m radius)

    // Static flags controlling snapping.
    // These flags are the sole source of truth for snap settings, toggled by the 3D radial menu
    // (RoadEditControlMenu) and grid linkage (GridOverlay/GridSetManager).
    // Values persist across sessions (entering/leaving construction mode).
    public static bool EnableNodeSnapping = true;   // Node snapping ON/OFF
    public static bool EnablePathSnapping = true;   // Path snapping ON/OFF
    public static bool EnableGridSnapping = false;           // Grid snapping ON/OFF (linked to grid visibility)
    public static bool EnableZoneVertexSnapping = false; // District vertex snapping ON/OFF
    public static bool EnableRightAngleSnapping = false; // 90° right-angle snapping (default OFF)
    public static bool EnableStraightLineSnapping = false; // Straight-extension snapping (default OFF)
    
    /// <summary>
    /// Minimum marker world Y at sea (metres).
    /// The sea plane is at worldY=0.001, so offset above it by marker thickness.
    /// </summary>
    private const float SeaMarkerMinY = 0.05f;

    // Currently displayed marker
    private GameObject currentMarker;
    private Vector3 lastMarkerPosition;
    
    // RoadPlanManager reference
    private RoadPlanManager roadPlanManager;

    // BuildingPlanManager reference (for direct placement hover ghosts; cached to avoid per-frame searches).
    private BuildingPlanManager buildingPlanManager;

    private void Start()
    {
        // Default marker material settings
        if (markerMaterial == null)
        {
            markerMaterial = Resources.Load<Material>("MarkerMaterial");
        }

        // Automatically find ParcelEditorManager.
        if (parcelEditorManager == null)
        {
            parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        }

        if (parcelEditorManager == null)
        {
            Debug.LogError("ParcelEditorManagerが見つかりません。シーンに配置されているか確認してください。");
        }

        // Automatically find PathManager.
        if (pathManager == null)
        {
            pathManager = FindAnyObjectByType<PathManager>();
        }

        // Automatically find RoadPlanManager.
        roadPlanManager = FindAnyObjectByType<RoadPlanManager>();
        if (roadPlanManager == null)
        {
            Debug.LogError("RoadPlanManagerが見つかりません");
        }
    }

    /// <summary>
    /// Finds BuildingPlanManager if absent and returns the cached reference.
    /// It is persistent, so reuse once found to avoid searching on every hover frame.
    /// </summary>
    private BuildingPlanManager GetBuildingPlanManager()
    {
        if (buildingPlanManager == null)
        {
            buildingPlanManager = FindAnyObjectByType<BuildingPlanManager>();
        }
        return buildingPlanManager;
    }

    /// <summary>
    /// Dynamically finds ParcelShapeEditManager.
    /// Created when shape editing starts; search on each call rather than in Start().
    /// </summary>
    private ParcelShapeEditManager GetParcelShapeEditManager()
    {
        return FindAnyObjectByType<ParcelShapeEditManager>();
    }

    /// <summary>
    /// Dynamically finds ResourceAreaEditManager.
    /// Created on STATE_EDIT_RESOURCE; search on each call rather than in Start().
    /// </summary>
    private ResourceAreaEditManager GetResourceAreaEditManager()
    {
        return FindAnyObjectByType<ResourceAreaEditManager>();
    }

    /// <summary>
    /// Dynamically finds GradingAreaEditManager.
    /// Created on STATE_EDIT_GRADING; search on each call rather than in Start().
    /// </summary>
    private GradingAreaEditManager GetGradingAreaEditManager()
    {
        return FindAnyObjectByType<GradingAreaEditManager>();
    }


    /// <summary>
    /// Dynamically finds TransitPlanManager.
    /// Created on STATE_EDIT_TRANSIT; search on each call rather than in Start().
    /// </summary>
    private TransitPlanManager GetTransitPlanManager()
    {
        return FindAnyObjectByType<TransitPlanManager>();
    }

    /// <summary>
    /// Dynamically finds PropPlacementManager.
    /// Created on STATE_EDIT_PROP; search on each call rather than in Start().
    /// </summary>
    private PropPlacementManager GetPropPlacementManager()
    {
        return FindAnyObjectByType<PropPlacementManager>();
    }

    public void onClick(int mode)
    {
        Vector3 clickPosition = new Vector3(InputHandler.Instance.posX, 0, InputHandler.Instance.posZ);
        clickPosition.y = Utils.GetGroundHeight(clickPosition);
        // Road/rail construction snaps once in RoadPlanManager using its own snapThreshold and variant
        // filter. Applying shared snapping here (default instance radius without filters)
        // would double-snap and make RoadPlanManager's radius setting ineffective.
        // Split-point selection also skips shared snapping (node attraction would force splits
        // near endpoints and cause rejection; match onHover conditions).
        // Arbitrary polygon drawing (parcel shapes, resource areas, earthworks) also skips shared snapping. Areas are not aligned to paths/nodes;
        // placing earthworks vertices on road centerlines would overlap paths and always cause order rejection.
        if (mode != InputMode.MODE_ROAD_CONSTRUCTION && mode != InputMode.MODE_PATH_SPLIT
            && mode != InputMode.MODE_BUILDING_CONSTRUCTION && mode != InputMode.MODE_EDIT_PROP
            && mode != InputMode.MODE_PARCEL_SHAPE_EDIT
            && mode != InputMode.MODE_EDIT_RESOURCE
            && mode != InputMode.MODE_EDIT_GRADING)
        {
            clickPosition = ApplySnapping(clickPosition);
        }

        switch (mode)
        {
            case InputMode.MODE_NORMAL:
                HandleNormalClick(clickPosition, mode);
                break;
            case InputMode.MODE_ROAD_CONSTRUCTION:
                HandleRoadConstructionClick(clickPosition);
                break;
            case InputMode.MODE_BUILDING_CONSTRUCTION:
                {
                    // Direct placement passes through parcels to select terrain. BuildingPlanManager ignores
                    // terrain clicks during parcel placement. That manager applies snapping once.
                    var bpm = GetBuildingPlanManager();
                    if (bpm != null)
                    {
                        bpm.OnTerrainClickDirect(clickPosition);
                    }
                }
                break;
            case InputMode.MODE_PARCEL_SHAPE_EDIT:
                {
                    var shapeEditManager = GetParcelShapeEditManager();
                    Debug.Log($"TerrainInputHandler.onClick: MODE_PARCEL_SHAPE_EDIT shapeEditManager={shapeEditManager != null}");
                    if (shapeEditManager != null)
                    {
                        shapeEditManager.OnTerrainLeftClick(clickPosition);
                    }
                }
                break;
            case InputMode.MODE_SET_GRID:
                {
                    // As with hover, use dedicated snapping independent of grid/global flags.
                    // clickPosition has already passed through normal ApplySnapping above, so resnap from raw terrain coordinates.
                    Vector3 raw = new Vector3(InputHandler.Instance.posX, 0, InputHandler.Instance.posZ);
                    raw.y = Utils.GetGroundHeight(raw);
                    GridSetManager.Instance.OnTerrainClicked(ApplySnappingForGridSet(raw));
                }
                break;
            case InputMode.MODE_EDIT_RESOURCE:
                {
                    var resourceEditManager = GetResourceAreaEditManager();
                    if (resourceEditManager != null)
                    {
                        resourceEditManager.OnTerrainLeftClick(clickPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_GRADING:
                GetGradingAreaEditManager()?.OnTerrainLeftClick(clickPosition);
                break;
            case InputMode.MODE_EDIT_TRANSIT:
                {
                    var transitPlanManager = GetTransitPlanManager();
                    if (transitPlanManager != null)
                    {
                        transitPlanManager.OnTerrainLeftClick(clickPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_PROP:
                {
                    var propPlacementManager = GetPropPlacementManager();
                    if (propPlacementManager != null)
                    {
                        propPlacementManager.OnTerrainLeftClick(clickPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_CONNECTOR:
                // Terrain clicks do nothing in connector editing (terminals move only through dragging).
                break;
            case InputMode.MODE_NONE:
                // Do nothing.
                break;
            case InputMode.MODE_REMOVE:
                // Bare terrain clicks do nothing in bulldoze mode.
                // InputHandler's terrain→parcel substitution redirects buried empty parcels
                // to ParcelInputHandler.onClick(MODE_REMOVE).
                break;
            case InputMode.MODE_PATH_SPLIT:
                // Also accept terrain clicks beside paths for split selection (use perpendicular centerline projection).
                PathTopologyEditManager.Instance?.OnClickForSplit();
                break;
            default:
                HandleNormalClick(clickPosition, mode);
                break;
        }
    }

    public void onRightClick(int mode)
    {
        Vector3 clickPosition = new Vector3(InputHandler.Instance.posX, 0, InputHandler.Instance.posZ);
        clickPosition.y = Utils.GetGroundHeight(clickPosition);
        // Skip shared snapping in road/rail construction because RoadPlanManager uses its own radius
        // (same reason as onClick: double snapping would make that radius ineffective).
        if (mode != InputMode.MODE_ROAD_CONSTRUCTION && mode != InputMode.MODE_PARCEL_SHAPE_EDIT
            && mode != InputMode.MODE_EDIT_RESOURCE
            && mode != InputMode.MODE_EDIT_GRADING)
        {
            clickPosition = ApplySnapping(clickPosition);
        }
        switch (mode)
        {
            case InputMode.MODE_ROAD_CONSTRUCTION:
                if (roadPlanManager != null)
                {
                    // Right click during road construction: finalize the road.
                    roadPlanManager.OnTerrainRightClick(clickPosition);
                }
                break;
            case InputMode.MODE_PARCEL_SHAPE_EDIT:
                {
                    var shapeEditManager = GetParcelShapeEditManager();
                    Debug.Log($"TerrainInputHandler.onRightClick: MODE_PARCEL_SHAPE_EDIT shapeEditManager={shapeEditManager != null}");
                    if (shapeEditManager != null)
                    {
                        shapeEditManager.OnTerrainRightClick(clickPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_RESOURCE:
                {
                    var resourceEditManager = GetResourceAreaEditManager();
                    if (resourceEditManager != null)
                    {
                        resourceEditManager.OnTerrainRightClick(clickPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_GRADING:
                GetGradingAreaEditManager()?.OnTerrainRightClick(clickPosition);
                break;
            case InputMode.MODE_EDIT_TRANSIT:
                {
                    var transitPlanManager = GetTransitPlanManager();
                    if (transitPlanManager != null)
                    {
                        transitPlanManager.OnTerrainRightClick(clickPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_PROP:
                {
                    var propPlacementManager = GetPropPlacementManager();
                    if (propPlacementManager != null)
                    {
                        propPlacementManager.OnTerrainRightClick(clickPosition);
                    }
                }
                break;
            default:
                break;
        }
    }

    public void onHoverStart(int mode)
    {
        // Do nothing here; onHover continuously updates markers.
    }

    public void onHoverEnd(int mode)
    {
        HideMarker();
        if (mode == InputMode.MODE_SET_GRID && GridSetManager.Instance != null)
        {
            GridSetManager.Instance.OnTerrainHoverEnd();
        }
        if (mode == InputMode.MODE_BUILDING_CONSTRUCTION)
        {
            // Hide direct placement hover ghosts when leaving empty terrain (e.g. moving over parcels).
            GetBuildingPlanManager()?.HideDirectGhost();
        }
        // Hide polygon Placing markers when moving over UI or off terrain.
        if (mode == InputMode.MODE_PARCEL_SHAPE_EDIT)
        {
            GetParcelShapeEditManager()?.OnTerrainHoverEnd();
        }
        else if (mode == InputMode.MODE_EDIT_RESOURCE)
        {
            GetResourceAreaEditManager()?.OnTerrainHoverEnd();
        }
        else if (mode == InputMode.MODE_EDIT_GRADING)
        {
            GetGradingAreaEditManager()?.OnTerrainHoverEnd();
        }
    }

    public void onHover(int mode)
    {
        Vector3 basePosition = new Vector3(InputHandler.Instance.posX, 0, InputHandler.Instance.posZ);
        basePosition.y = Utils.GetGroundHeight(basePosition);

        // Hover snapping writes back to cursor coordinates, affecting subsequent clicks/drags.
        // Therefore modes without snapping must skip it for hover as well as dragging.
        // Do not snap in connector editing. Leaving 10m node snapping enabled would make
        // the dragged terminal snap to its own pre-move cached location (nodeGrid keeps the old location
        // until PATCH completes), preventing movement within 10m and causing a jump at its boundary.
        // Also skip shared snapping in road/rail construction. RoadPlanManager.OnTerrainHover uses its own
        // snapThreshold to draw previews; double snapping here would make
        // RoadPlanManager's radius setting ineffective.
        // Split-point selection also skips shared snapping. With 10m node snapping, the cursor is pulled to
        // endpoint nodes, making every nearby split too close to an endpoint
        // and rejected by the backend. PathTopologyEditManager determines split points by centerline projection.
        bool skipSnap = mode == InputMode.MODE_EDIT_CONNECTOR
                     || mode == InputMode.MODE_PARCEL_SHAPE_EDIT
                     || mode == InputMode.MODE_EDIT_RESOURCE
                     || mode == InputMode.MODE_EDIT_GRADING
                     || mode == InputMode.MODE_EDIT_PROP
                     || mode == InputMode.MODE_BUILDING_CONSTRUCTION
                     || mode == InputMode.MODE_ROAD_CONSTRUCTION
                     || mode == InputMode.MODE_PATH_SPLIT;
        Vector3 finalPosition = skipSnap
            ? basePosition
            : ApplySnapping(basePosition);
        if (Vector3.Distance(basePosition, finalPosition) > 0.01f)
        {
            InputHandler.Instance.OverrideMousePosition(finalPosition.x, finalPosition.z);
        }

        switch (mode)
        {
            case InputMode.MODE_ROAD_CONSTRUCTION:
                if (roadPlanManager != null)
                {
                    roadPlanManager.OnTerrainHover(finalPosition);
                }
                break;
            case InputMode.MODE_BUILDING_CONSTRUCTION:
                // Free/roadside placement ghosts. Do not apply shared road-centerline snapping.
                // Retain raw coordinates during parcel placement too, for reevaluation immediately after closing the Ctrl menu.
                GetBuildingPlanManager()?.OnTerrainHoverDirect(finalPosition);
                break;
            case InputMode.MODE_PARCEL_CREATION_POINT:
                // Update only after moving a certain distance from the previous position (performance).
                if (Vector3.Distance(finalPosition, lastMarkerPosition) > 0.1f)
                {
                    ShowMarker(finalPosition);
                    lastMarkerPosition = finalPosition;
                }
                break;
            case InputMode.MODE_PARCEL_SHAPE_EDIT:
                {
                    var shapeEditManager = GetParcelShapeEditManager();
                    if (shapeEditManager != null)
                    {
                        shapeEditManager.OnTerrainHover(finalPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_RESOURCE:
                {
                    var resourceEditManager = GetResourceAreaEditManager();
                    if (resourceEditManager != null)
                    {
                        resourceEditManager.OnTerrainHover(finalPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_GRADING:
                GetGradingAreaEditManager()?.OnTerrainHover(finalPosition);
                break;
            case InputMode.MODE_EDIT_TRANSIT:
                {
                    var transitPlanManager = GetTransitPlanManager();
                    if (transitPlanManager != null)
                    {
                        transitPlanManager.OnTerrainHover(finalPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_PROP:
                {
                    var propPlacementManager = GetPropPlacementManager();
                    if (propPlacementManager != null)
                    {
                        propPlacementManager.OnTerrainHover(finalPosition);
                    }
                }
                break;
            case InputMode.MODE_SET_GRID:
                {
                    // Grid angle setting excludes snapping to the grid being edited, which would be meaningless;
                    // snap directly to existing nodes/paths independently of global snap flags (which the UI may change).
                    Vector3 gridSnap = ApplySnappingForGridSet(basePosition);
                    if (Vector3.Distance(basePosition, gridSnap) > 0.01f)
                    {
                        InputHandler.Instance.OverrideMousePosition(gridSnap.x, gridSnap.z);
                    }
                    GridSetManager.Instance.OnTerrainHover(gridSnap);
                }
                break;
            default:
                HideMarker();
                break;
        }
    }

    private void HandleNormalClick(Vector3 position, int mode)
    {
        Debug.Log($"TerrainInputHandler: 地形がクリックされました: {position}");
        
        if (mode == InputMode.MODE_PARCEL_CREATION_POINT)
        {
            HandleParcelEditorPointSelection(position);
            return;
        }
    }

    private void HandleParcelEditorPointSelection(Vector3 position)
    {
        // Notify ParcelEditorManager of point selection.
        if (parcelEditorManager != null)
        {
            parcelEditorManager.OnPointSelected(position);
        }
        else
        {
            Debug.LogError("parcelEditorManagerが見つかりません");
        }
    }

    private void HandleRoadConstructionClick(Vector3 position)
    {
        if (roadPlanManager == null)
        {
            Debug.LogError("RoadPlanManagerが見つかりません");
            return;
        }

        // Delegate the already-snapped position directly to RoadPlanManager.
        roadPlanManager.OnTerrainLeftClick(position);
    }

    private void ShowMarker(Vector3 position)
    {
        // At sea, terrain elevation is the seabed (worldY<0), so placing directly there hides
        // the marker behind the sea plane and makes its target invisible.
        position.y = Mathf.Max(position.y, SeaMarkerMinY);

        // Update the existing marker position.
        if (currentMarker != null)
        {
            currentMarker.transform.position = position;
            return;
        }
        
        // Create a new marker.
        if (markerPrefab != null)
        {
            currentMarker = Instantiate(markerPrefab, position, Quaternion.identity);
        }
        else
        {
            // Create the marker from a primitive.
            currentMarker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            currentMarker.name = "TerrainMarker";
            
            // Set marker scale.
            currentMarker.transform.localScale = new Vector3(markerSize, 0.1f, markerSize);
            currentMarker.transform.position = position;
            
            // Apply material.
            if (markerMaterial != null)
            {
                Renderer renderer = currentMarker.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.material = markerMaterial;
                }
            }
            
            // Remove the collider (not a click target).
            Collider markerCollider = currentMarker.GetComponent<Collider>();
            if (markerCollider != null)
            {
                Destroy(markerCollider);
            }
        }
    }

    private void HideMarker()
    {
        if (currentMarker != null)
        {
            Destroy(currentMarker);
            currentMarker = null;
        }
    }

    private void OnDestroy()
    {
        if (roadPlanManager != null)
        {
        }
        HideMarker();
    }


    #region パーセルエディター統合機能

    private void Update()
    {
        // ParcelEditorManager.Update handles Esc selection reset itself
        // (retains creation mode to allow reselection in the same mode).
        // This only hides the terrain hover marker. This component exists per terrain tile,
        // so keep the condition lightweight.
        if (parcelEditorManager != null
            && parcelEditorManager.IsActive
            && InputUtil.WorldEscapeWasPressed)
        {
            HideMarker();
        }
    }

    #endregion
    
    #region Snapping Functions

    /// <summary>
    /// Dedicated snapping for grid angle setting.
    /// Ignores the grid itself (the edit target, so snapping is meaningless) and global flags that UI may change
    /// (EnableNodeSnapping / EnablePathSnapping, etc.), snapping directly to existing nodes, then paths.
    /// Returns basePosition unchanged if neither is within threshold.
    /// </summary>
    public Vector3 ApplySnappingForGridSet(Vector3 basePosition)
    {
        if (pathManager == null)
        {
            pathManager = FindAnyObjectByType<PathManager>();
        }
        if (pathManager == null) return basePosition;

        float actualThreshold = (snapThreshold > 0.01f) ? snapThreshold : 10.0f;

        // Prioritize nodes (for precise alignment to intersections).
        Vector3 snappedPos;
        if (FindNearestNode(basePosition, actualThreshold, pathManager, out snappedPos) != null)
        {
            return snappedPos;
        }

        // Then snap onto paths (roads).
        GeneratedModels.PathSummary snappedPath;
        int segmentIndex;
        if (FindNearestPathSegment(basePosition, actualThreshold, pathManager, out snappedPos, out snappedPath, out segmentIndex))
        {
            return snappedPos;
        }

        return basePosition;
    }

    public Vector3 ApplySnapping(Vector3 basePosition)
    {
        return ApplySnapping(basePosition, -1);
    }

    public Vector3 ApplySnapping(Vector3 basePosition, int drawingPathVariant)
    {
        return ApplySnapping(basePosition, drawingPathVariant, out _, out _);
    }

    /// <summary>
    /// Applies shared snapping (grid/district vertices/nodes/construction endpoints/paths).
    ///
    /// drawingPathVariant is the current construction variant (-1=unrestricted).
    /// When specified, infrastructure network compatibility filters prevent roads from snapping to infrastructure
    /// nodes/paths, and restrict infrastructure to same-network nodes, paths, and facility terminals
    /// (overlapping infra_products; see InfraNetworkUtil).
    ///
    /// snappedToElevatable / snapRelHeight provide height-inheritance metadata.
    /// Returns true and target relative height for node/path/construction endpoint snaps,
    /// letting callers inherit elevated connection heights for placed nodes.
    /// Grid/district vertices/no snap return false, 0.
    ///
    /// Positive thresholdOverride replaces the instance snapThreshold.
    /// Used when editors such as road/rail construction have their own radius,
    /// controlling snap range independently of shared defaults (unspecified/negative uses the instance value).
    /// skipGrid supports road parallel assistance in connection→parallel→grid order.
    /// </summary>
    public Vector3 ApplySnapping(Vector3 basePosition, int drawingPathVariant, out bool snappedToElevatable, out float snapRelHeight, float thresholdOverride = -1f, bool skipGrid = false)
    {
        snappedToElevatable = false;
        snapRelHeight = 0f;

        if (pathManager == null)
        {
            pathManager = FindAnyObjectByType<PathManager>();
        }

        float actualThreshold = (thresholdOverride > 0.01f) ? thresholdOverride : ((snapThreshold > 0.01f) ? snapThreshold : 10.0f);

        // Grid snapping has highest priority (independent of visibility).
        if (!skipGrid && EnableGridSnapping && GridOverlay.Instance != null)
        {
            return GridOverlay.Instance.SnapPosition(basePosition);
        }

        // Try district vertex snapping.
        if (EnableZoneVertexSnapping)
        {
            Vector3 snappedPos;
            if (FindNearestZoneVertex(basePosition, actualThreshold, out snappedPos))
            {
                return snappedPos;
            }
        }

        // Try node snapping (including planned/under-construction endpoints).
        if (EnableNodeSnapping)
        {
            Vector3 snappedPos;
            NodeData snappedNode = FindNearestNode(basePosition, actualThreshold, pathManager, out snappedPos, drawingPathVariant);
            if (snappedNode != null)
            {
                // Build snap positions from design elevation or node relative Y, excluding render Y.
                snappedToElevatable = true;
                snapRelHeight = snappedPos.y - Utils.GetGroundHeight(snappedPos);
                return snappedPos;
            }

            // If no existing node snaps, try planned/under-construction endpoints.
            // Nodes do not exist at endpoints until construction completes and activates the path,
            // so test start_point / end_point of planned/under-construction paths directly.
            Vector3 endpointPos;
            float endpointRelHeight;
            if (FindNearestConstructionEndpoint(basePosition, actualThreshold, pathManager, out endpointPos, out endpointRelHeight, drawingPathVariant))
            {
                snappedToElevatable = true;
                snapRelHeight = endpointRelHeight;
                return endpointPos;
            }
        }

        // Try path snapping.
        if (EnablePathSnapping)
        {
            Vector3 snappedPos;
            GeneratedModels.PathSummary snappedPath;
            int segmentIndex;
            if (FindNearestPathSegment(basePosition, actualThreshold, pathManager, out snappedPos, out snappedPath, out segmentIndex, drawingPathVariant))
            {
                // Inherit elevation by interpolating the target path's anchor relative Y at the snap point.
                // Never invert absolute render-polyline Y minus terrain samples
                // (see GetRelativeHeightAtPoint comments: even ground-path snaps would
                // treat endpoints as underground/airborne and silently break infrastructure T-taps).
                snappedToElevatable = true;
                snapRelHeight = snappedPath != null
                    ? snappedPath.GetRelativeHeightAtPoint(snappedPos.x, snappedPos.z)
                    : 0f;
                return snappedPos;
            }
        }

        return basePosition;
    }

    /// <summary>In underground view, excludes translucent surface sections from construction display/snap candidates.</summary>
    public static bool IsPathSnapPointVisible(GeneratedModels.PathSummary path, Vector3 position)
    {
        return !UndergroundView.IsActive || (path != null && TunnelSectionUtil.IsTunnelPoint(position,
            TunnelSectionUtil.GetClearanceHeight(path.path_variant)));
    }

    public static bool IsNodeSnapVisible(NodeData node, PathManager pathManager)
    {
        if (!UndergroundView.IsActive) return true;
        var paths = pathManager.GetPathsConnectedToNode(node.node_id);
        // Use the same connected paths as intersection material classification. Classify isolated terminals by depth alone.
        float clearance = paths.Count > 0 ? TunnelSectionUtil.GetClearanceHeight(paths[0].path_variant) : 0f;
        return TunnelSectionUtil.IsTunnelPoint(pathManager.GetNodeSnapPosition(node), clearance);
    }

    /// <summary>Splits mixed paths at the same boundaries as rendering, returning only underground candidates.</summary>
    public static IEnumerable<PathSegment> GetVisiblePathSnapSegments(IEnumerable<PathSegment> segments)
    {
        if (segments == null) yield break;
        foreach (var segment in segments)
        {
            if (!UndergroundView.IsActive)
            {
                yield return segment;
                continue;
            }
            if (segment.path == null) continue;
            var runs = TunnelSectionUtil.CollectTunnelRuns(new List<Vector3> { segment.a, segment.b },
                TunnelSectionUtil.GetClearanceHeight(segment.path.path_variant),
                PathManager.GetPathFollowTerrain(segment.path));
            foreach (var run in runs)
                for (int i = 1; i < run.Count; i++)
                    yield return new PathSegment { path = segment.path, segmentIndex = segment.segmentIndex,
                        a = run[i - 1], b = run[i] };
        }
    }

    public static NodeData FindNearestNode(Vector3 basePos, float threshold, PathManager pathManager, out Vector3 snappedPos, int drawingPathVariant = -1)
    {
        if (pathManager == null)
        {
            snappedPos = basePos;
            return null;
        }

        // === Grid search for nearby nodes ===
        var nearby = pathManager.QueryNearbyNodes(basePos, threshold);
        snappedPos = basePos;
        if (nearby == null || nearby.Count == 0) return null;

        float minDist = float.MaxValue;
        NodeData resultNode = null;

        foreach (var node in nearby)
        {
            // Infrastructure network compatibility filter (no road × infrastructure or cross-network infrastructure snaps).
            if (!InfraNetworkUtil.IsNodeSnappable(node, drawingPathVariant, pathManager)) continue;
            if (drawingPathVariant >= 0 && !IsNodeSnapVisible(node, pathManager)) continue;

            Vector3 nodePos = pathManager.GetNodeSnapPosition(node);

            float dist = Vector2.Distance(new Vector2(basePos.x, basePos.z), new Vector2(nodePos.x, nodePos.z));
            if (dist < minDist && dist <= threshold)
            {
                minDist = dist;
                resultNode = node;
                snappedPos = nodePos;
            }
        }
        return resultNode;
    }

    public static bool FindNearestPathSegment(Vector3 basePos, float threshold, PathManager pathManager, out Vector3 snappedPos, out GeneratedModels.PathSummary snappedPath, out int snappedSegmentIndex, int drawingPathVariant = -1)
    {
        // Initialization
        snappedPos = basePos;
        snappedPath = null;
        snappedSegmentIndex = -1;

        if (pathManager == null)
        {
            return false;
        }

        IEnumerable<PathSegment> segments = pathManager.QueryNearbySegments(basePos, threshold);
        if (drawingPathVariant >= 0) segments = GetVisiblePathSnapSegments(segments);
        float minDist = float.MaxValue;
        bool found = false;

        foreach (var seg in segments)
        {
            // Infrastructure network compatibility filter (no road × infrastructure or cross-network infrastructure snaps).
            if (seg.path != null && !InfraNetworkUtil.IsPathSnappable(seg.path.path_variant, drawingPathVariant)) continue;

            Vector3 closest = PolylineUtils.ClosestPointOnSegmentXZ(basePos, seg.a, seg.b);
            float dist = Vector2.Distance(new Vector2(basePos.x, basePos.z), new Vector2(closest.x, closest.z));
            if (dist < minDist && dist <= threshold)
            {
                minDist = dist;
                snappedPos = closest;
                snappedPath = seg.path;
                snappedSegmentIndex = seg.segmentIndex;
                found = true;
            }
        }

        return found;
    }

    public static bool FindNearestZoneVertex(Vector3 basePos, float threshold, out Vector3 snappedPos)
    {
        snappedPos = basePos;
        if (ZoneManager.Instance == null) return false;

        var zones = ZoneManager.Instance.GetAllZones();
        if (zones == null || zones.Count == 0) return false;

        float minDist = float.MaxValue;
        bool found = false;

        foreach (var kv in zones)
        {
            int zoneId = kv.Key;
            var zone = kv.Value;
            float[][] polygonArray = zone.GetPolygonArray();
            if (polygonArray == null) continue;

            foreach (float[] point in polygonArray)
            {
                if (point == null || point.Length < 2) continue;
                float x = point[0];
                float z = point[1];
                // Align height to terrain.
                float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
                Vector3 vertexPos = new Vector3(x, y, z);

                float dist = Vector2.Distance(new Vector2(basePos.x, basePos.z), new Vector2(vertexPos.x, vertexPos.z));
                if (dist < minDist && dist <= threshold)
                {
                    minDist = dist;
                    snappedPos = vertexPos;
                    found = true;
                }
            }
        }

        return found;
    }
    
    /// <summary>
    /// Finds the endpoint (start_point / end_point) nearest basePos among paths under construction
    /// (STATUS_CONSTRUCTING) or planned (STATUS_SCHEDULED_CONSTRUCTION).
    /// Endpoint nodes do not exist until construction completes and activates the path,
    /// but this allows snapping to their positions beforehand.
    /// </summary>
    public static bool FindNearestConstructionEndpoint(Vector3 basePos, float threshold, PathManager pathManager, out Vector3 snappedPos, out float snapRelHeight, int drawingPathVariant = -1)
    {
        snappedPos = basePos;
        snapRelHeight = 0f;
        if (pathManager == null) return false;

        var allPaths = pathManager.GetAllPaths();
        if (allPaths == null) return false;

        float minDist = float.MaxValue;
        bool found = false;

        foreach (var path in allPaths)
        {
            if (path.status != Constants.STATUS_SCHEDULED_CONSTRUCTION &&
                path.status != Constants.STATUS_PENDING &&
                path.status != Constants.STATUS_CONSTRUCTING)
            {
                continue;
            }

            // Infrastructure network compatibility filter (no road × infrastructure or cross-network infrastructure snaps).
            if (!InfraNetworkUtil.IsPathSnappable(path.path_variant, drawingPathVariant))
            {
                continue;
            }

            // start_point / end_point use [x, relative height, z]. Build absolute Y from the middle relative-height
            // value to inherit height when snapping to elevated planned/under-construction paths.
            var startList = GeneratedModelUtils.ToFloatList(path.start_point);
            if (startList != null && startList.Count >= 2)
            {
                float sx = startList[0];
                float sz = startList[startList.Count - 1];
                float sRel = path.GetRelativeHeightAtPoint(sx, sz);
                float sy = Utils.GetGroundHeight(new Vector3(sx, 0f, sz)) + sRel;
                Vector3 startPos = new Vector3(sx, sy, sz);
                float dist = Vector2.Distance(new Vector2(basePos.x, basePos.z), new Vector2(sx, sz));
                if (dist < minDist && dist <= threshold
                    && (drawingPathVariant < 0 || IsPathSnapPointVisible(path, startPos)))
                {
                    minDist = dist;
                    snappedPos = startPos;
                    snapRelHeight = sRel;
                    found = true;
                }
            }

            var endList = GeneratedModelUtils.ToFloatList(path.end_point);
            if (endList != null && endList.Count >= 2)
            {
                float ex = endList[0];
                float ez = endList[endList.Count - 1];
                float eRel = path.GetRelativeHeightAtPoint(ex, ez);
                float ey = Utils.GetGroundHeight(new Vector3(ex, 0f, ez)) + eRel;
                Vector3 endPos = new Vector3(ex, ey, ez);
                float dist = Vector2.Distance(new Vector2(basePos.x, basePos.z), new Vector2(ex, ez));
                if (dist < minDist && dist <= threshold
                    && (drawingPathVariant < 0 || IsPathSnapPointVisible(path, endPos)))
                {
                    minDist = dist;
                    snappedPos = endPos;
                    snapRelHeight = eRel;
                    found = true;
                }
            }
        }

        return found;
    }

    
    #endregion
}
