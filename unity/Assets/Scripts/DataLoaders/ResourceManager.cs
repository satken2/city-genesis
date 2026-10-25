using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections;
using System.Collections.Generic;

[Serializable]
public class ResourceItem
{
    public int resource_id;
    public int resource_type;
    public int product_id;
    public string resource_type_label;
    public float resource_amount;
    public float recovery_rate;
    public List<List<float>> polygon;
    public List<float> center_point;
}

[Serializable]
public class ResourcesListResponse
{
    public List<ResourceItem> resources;
}

/// <summary>
/// Manager rendering natural resource distribution (oil fields, ore, rare metals, forests, water sources)
/// as translucent heatmap-style overlays following terrain.
///
/// Rendering:
/// - Subdivide each resource polygon AABB with a regular grid, aligning vertex heights to terrain.
///   This avoids flat planes buried beneath terrain and drapes the surface like a sheet.
/// - Fade polygon boundaries using vertex alpha; Custom/ResourceHeatmap procedural noise
///   creates a soft cloud/heatmap appearance.
///
/// Rebuild timing:
/// - Resource data loading completes
/// - TerrainManager.OnTerrainTileReady updates a cell containing resources
///   (coalesce multiple events per frame into one LateUpdate rebuild)
/// </summary>
public class ResourceManager : BaseApiDataManager
{
    public static ResourceManager Instance { get; private set; }

    private bool areResourcesLoaded = false;
    private bool isLoadInProgress = false;
    private bool isVisible = false;

    private List<ResourceItem> resourceCache = new List<ResourceItem>();

    // Dedicated mesh object for heatmap rendering.
    // Use a dedicated UV-bearing mesh rather than OverlayMeshRenderer shared with other managers.
    private GameObject heatmapMeshObj;
    private MeshFilter heatmapMeshFilter;
    private MeshRenderer heatmapMeshRenderer;
    private Mesh heatmapMesh;
    private Material heatmapMaterial;

    // Cells potentially containing resources.
    // Rebuild only when the OnTerrainTileReady cell belongs to this set,
    // avoiding overhead from unrelated cells.
    private HashSet<Vector2Int> resourceTouchedCells = new HashSet<Vector2Int>();

    // Deferred flag coalescing multiple OnTerrainTileReady events in one frame into a single rebuild.
    private bool isMeshDirty = false;

    // ---- Resource name labels (ScreenSpaceOverlay Canvas, as in ZoneManager) ----

    // Resource label Canvas, separate from ZoneManager's UI Canvas
    // to control resource visibility independently.
    private GameObject uiCanvasRoot;
    private Canvas uiCanvas;
    private RectTransform uiCanvasRect;
    private GraphicRaycaster uiRaycaster;

    // Label GameObject per resource ID.
    private Dictionary<int, GameObject> resourceLabelUIs = new Dictionary<int, GameObject>();

    // Cached center_point (XZ plane) per resource ID, populated during loading
    // to avoid linear resourceCache searches during label position updates.
    private Dictionary<int, Vector2> resourceCentersById = new Dictionary<int, Vector2>();

    // Skip WorldToScreenPoint recalculation for small camera position/rotation changes,
    // reducing CPU cost of label updates. Same thresholds as ZoneManager.
    private Vector3 lastCamPos = Vector3.negativeInfinity;
    private Quaternion lastCamRot = Quaternion.identity;
    private const float CAMERA_POS_THRESHOLD = 1.0f;
    private const float CAMERA_ROT_THRESHOLD = 2.0f;

    // Colors by resource type.
    // Use RGB only as base color. Calculate alpha per vertex from edge fade.
    // 1: Oil, 2: Ore, 3: Rare Metal (precious metals), 4: Forest, 5: Water (water sources), 6: Coal, 7: Natural Gas
    private Dictionary<int, Color> resourceColors = new Dictionary<int, Color>
    {
        { 1, new Color(0.1f, 0.1f, 0.1f, 1.0f) },       // Oil (black)
        { 2, new Color(0.6f, 0.4f, 0.2f, 1.0f) },       // Ore (brown)
        { 3, new Color(1.0f, 0.85f, 0.0f, 1.0f) },      // Rare Metal / Precious Metal (yellow)
        { 4, new Color(0.18f, 0.49f, 0.2f, 1.0f) },     // Forest (green)
        { 5, new Color(0.0f, 0.6f, 0.9f, 1.0f) },       // Water (light blue)
        { 6, new Color(0.5f, 0.5f, 0.5f, 1.0f) },       // Coal (gray)
        { 7, new Color(0.95f, 0.95f, 0.95f, 1.0f) }     // Natural Gas (white)
    };

    // Subdivision grid spacing (meters).
    // Too large gives coarse terrain following; too small increases vertex count.
    private const float GRID_STEP = 10f;

    // Fade distance outside polygon boundaries (meters).
    // Generate vertices this far outside and gradually reduce alpha to zero for soft edges.
    private const float EDGE_FADE = 12f;

    // Lift above terrain surface (meters).
    // Combines Z-fighting prevention with a floating-cloud appearance.
    private const float HEIGHT_OFFSET = 0.5f;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void OnEnable()
    {
        // Subscribe because terrain tile updates require resource mesh height realignment.
        // Same subscription pattern as PathManager / BuildingManager.
        TerrainManager.OnTerrainTileReady += OnTerrainTileReadyHandler;
    }

    private void OnDisable()
    {
        TerrainManager.OnTerrainTileReady -= OnTerrainTileReadyHandler;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (heatmapMeshObj != null) Destroy(heatmapMeshObj);
        if (heatmapMesh != null) Destroy(heatmapMesh);
        if (heatmapMaterial != null) Destroy(heatmapMaterial);
        if (uiCanvasRoot != null) Destroy(uiCanvasRoot);
    }

    /// <summary>
    /// Toggle resource visibility.
    /// Disable the mesh renderer and label Canvas when off; rebuild immediately when on.
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (isVisible == visible) return;
        isVisible = visible;

        if (heatmapMeshRenderer != null)
        {
            heatmapMeshRenderer.enabled = visible;
        }

        // SetActive on the entire Canvas controls every label together
        if (uiCanvasRoot != null)
        {
            uiCanvasRoot.SetActive(visible);
        }

        if (visible && areResourcesLoaded)
        {
            BuildHeatmapMesh();
            // Reset camera tracking on visibility enable to guarantee label position updates next frame
            lastCamPos = Vector3.negativeInfinity;
        }
    }

    /// <summary>
    /// Create heatmap GameObject / Mesh / Material as needed.
    /// Use ordinary MeshRenderer with UVs and vertex colors rather than OverlayMeshRenderer.
    /// </summary>
    private void EnsureHeatmapMeshObjects()
    {
        if (heatmapMeshObj != null) return;

        heatmapMeshObj = new GameObject("ResourceHeatmapMesh");
        DontDestroyOnLoad(heatmapMeshObj);
        heatmapMeshFilter = heatmapMeshObj.AddComponent<MeshFilter>();
        heatmapMeshRenderer = heatmapMeshObj.AddComponent<MeshRenderer>();

        Shader shader = Shader.Find("Custom/ResourceHeatmap");
        if (shader == null)
        {
            Debug.LogError("ResourceManager: Custom/ResourceHeatmap シェーダーが見つかりません。");
            return;
        }
        heatmapMaterial = new Material(shader);
        heatmapMeshRenderer.material = heatmapMaterial;
        heatmapMeshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        heatmapMeshRenderer.receiveShadows = false;

        heatmapMesh = new Mesh();
        heatmapMesh.MarkDynamic();
        heatmapMeshFilter.mesh = heatmapMesh;

        heatmapMeshRenderer.enabled = isVisible;
    }

    public bool IsInitialLoadReady => areResourcesLoaded && !isLoadInProgress;

    public override void LoadCells(List<Vector2Int> neededCells, bool isForce)
    {
        if (!isLoadInProgress && (isForce || !areResourcesLoaded))
        {
            StartCoroutine(LoadAllResources());
        }
    }

    protected override void UnloadCell(Vector2Int cellIndex)
    {
        // Global loading only, not per-cell
    }

    public override void ClearAllCells()
    {
        resourceCache.Clear();
        areResourcesLoaded = false;
        resourceTouchedCells.Clear();
        if (heatmapMesh != null)
        {
            heatmapMesh.Clear();
        }
        ClearAllResourceLabels();
    }

    private IEnumerator RetryInitialLoad()
    {
        yield return new WaitForSecondsRealtime(2f);
        if (GameStateManager.Instance != null && GameStateManager.Instance.IsGameStarted() && !areResourcesLoaded)
            LoadCells(new List<Vector2Int>(), false);
    }

    private IEnumerator LoadAllResources()
    {
        if (isLoadInProgress) yield break;
        isLoadInProgress = true;

        string url = "/resources"; // ApiClient adds BASE_URL
        yield return ApiClient.Get<ResourcesListResponse>(
            url,
            response =>
            {
                if (response != null && response.resources != null)
                {
                    resourceCache = response.resources;
                    areResourcesLoaded = true;
                    RecomputeResourceTouchedCells();
                    RebuildResourceLabels();
                    if (isVisible)
                    {
                        BuildHeatmapMesh();
                    }
                }
                isLoadInProgress = false;
            },
            error =>
            {
                Debug.LogError($"ResourceManager: Failed to load resources: {error}");
                areResourcesLoaded = false;
                isLoadInProgress = false;
                StartCoroutine(RetryInitialLoad());
            }
        );
    }

    /// <summary>
    /// Recalculate cells potentially containing resources from current resourceCache.
    /// Used by OnTerrainTileReady to skip unrelated-cell rebuilds.
    /// Register every cell covered by each polygon AABB including fade margins.
    /// </summary>
    private void RecomputeResourceTouchedCells()
    {
        resourceTouchedCells.Clear();
        foreach (var resource in resourceCache)
        {
            if (resource.polygon == null || resource.polygon.Count < 3) continue;

            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
            foreach (var pt in resource.polygon)
            {
                if (pt[0] < minX) minX = pt[0];
                if (pt[0] > maxX) maxX = pt[0];
                if (pt[1] < minZ) minZ = pt[1];
                if (pt[1] > maxZ) maxZ = pt[1];
            }
            // Expand by fade distance to include boundary cells
            minX -= EDGE_FADE; maxX += EDGE_FADE;
            minZ -= EDGE_FADE; maxZ += EDGE_FADE;

            TerrainManager.WorldRectToCellRange(minX, minZ, maxX, maxZ, out Vector2Int minCell, out Vector2Int maxCell);
            for (int cx = minCell.x; cx <= maxCell.x; cx++)
            {
                for (int cz = minCell.y; cz <= maxCell.y; cz++)
                {
                    resourceTouchedCells.Add(new Vector2Int(cx, cz));
                }
            }
        }
    }

    private void OnTerrainTileReadyHandler(Vector2Int cellIndex, Terrain terrain)
    {
        if (!isVisible || !areResourcesLoaded) return;
        // Set rebuild flags only for cells containing resources
        if (resourceTouchedCells.Contains(cellIndex))
        {
            isMeshDirty = true;
        }
    }

    private void LateUpdate()
    {
        // Even if multiple OnTerrainTileReady events arrive in one frame,
        // coalesce into one rebuild (the frame's final terrain state suffices).
        if (isMeshDirty)
        {
            isMeshDirty = false;
            BuildHeatmapMesh();
        }
    }

    // ---- Resource name labels (ScreenSpaceOverlay Canvas, as in ZoneManager) ----

    private void Update()
    {
        Utils.SyncOverlayCanvasClickable(uiRaycaster);

        // Heatmap meshes use world space and need no rebuilding on camera movement,
        // but labels project to screen coordinates and need updated positions.
        if (!isVisible || !areResourcesLoaded) return;
        if (uiCanvasRoot == null || !uiCanvasRoot.activeSelf) return;
        if (resourceLabelUIs.Count == 0) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        // Skip camera position/rotation changes below thresholds,
        // reducing WorldToScreenPoint calls with the same throttling criteria as ZoneManager.
        Vector3 currentCamPos = cam.transform.position;
        Quaternion currentCamRot = cam.transform.rotation;
        bool posChanged = Vector3.Distance(currentCamPos, lastCamPos) > CAMERA_POS_THRESHOLD;
        bool rotChanged = Quaternion.Angle(currentCamRot, lastCamRot) > CAMERA_ROT_THRESHOLD;
        if (!posChanged && !rotChanged) return;

        lastCamPos = currentCamPos;
        lastCamRot = currentCamRot;

        UpdateResourceLabelPositions(cam);
    }

    /// <summary>
    /// Recalculate screen coordinates for all resource labels and update RectTransform.anchoredPosition.
    /// Project resource center_point world positions at terrain height +2m.
    /// Hide labels behind the camera.
    /// </summary>
    private void UpdateResourceLabelPositions(Camera cam)
    {
        foreach (var kvp in resourceLabelUIs)
        {
            int resourceId = kvp.Key;
            GameObject labelObj = kvp.Value;
            if (labelObj == null) continue;
            if (!resourceCentersById.TryGetValue(resourceId, out Vector2 center)) continue;

            float groundY = Utils.GetGroundHeight(new Vector3(center.x, 1000f, center.y));
            Vector3 worldPos = new Vector3(center.x, groundY + 2f, center.y);
            Vector3 screenPos = cam.WorldToScreenPoint(worldPos);
            bool isBehind = screenPos.z < 0;

            labelObj.SetActive(!isBehind);
            if (!isBehind)
            {
                RectTransform rt = labelObj.GetComponent<RectTransform>();
                rt.anchoredPosition = new Vector2(screenPos.x, screenPos.y);
            }
        }
    }

    /// <summary>
    /// Create a ScreenSpaceOverlay Canvas for resource names as needed.
    /// Separate from ZoneManager's Canvas (sortingOrder=60) to control resource
    /// visibility independently. Resources are display options with lower priority than districts,
    /// so use sortingOrder 55 (district labels appear in front).
    /// </summary>
    private void EnsureUICanvas()
    {
        if (uiCanvasRoot != null) return;

        uiCanvas = Utils.CreateOverlayCanvas("ResourceUICanvas", 55, persistent: true);
        uiCanvasRoot = uiCanvas.gameObject;
        uiCanvasRect = uiCanvasRoot.GetComponent<RectTransform>();
        uiRaycaster = uiCanvasRoot.GetComponent<GraphicRaycaster>();

        // Match current resource visibility
        uiCanvasRoot.SetActive(isVisible);
    }

    /// <summary>
    /// Create one resource name label in 2D UI.
    /// Update positions each frame in Update.
    /// Clicks use Rect bounds, so fit the Rect around text to avoid stealing clicks from buildings/roads underneath.
    /// </summary>
    private void CreateResourceLabelUI(ResourceItem resource)
    {
        EnsureUICanvas();
        if (uiCanvasRect == null) return;

        GameObject labelGO = new GameObject($"ResourceLabelUI_{resource.resource_id}");
        labelGO.transform.SetParent(uiCanvasRect, false);

        Text text = labelGO.AddComponent<Text>();
        text.text = resource.resource_type_label ?? "";
        text.fontSize = 35;
        text.font = UiFontProvider.GetFont(text.fontSize);
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;

        // Add a black text outline for readability.
        // Keep labels legible regardless of heatmap colors (oil=black, water=cyan, etc.).
        Outline outline = labelGO.AddComponent<Outline>();
        outline.effectColor = Color.black;
        outline.effectDistance = new Vector2(2.5f, 2.5f);

        RectTransform rt = text.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(text.preferredWidth, text.preferredHeight);

        // Make clickable to open natural resource details
        Button button = labelGO.AddComponent<Button>();
        int capturedResourceId = resource.resource_id;
        button.onClick.AddListener(() => OnResourceLabelClicked(capturedResourceId));

        resourceLabelUIs[resource.resource_id] = labelGO;
    }

    /// <summary>
    /// Handle natural resource label clicks.
    /// Send an IPC event asking Tauri UI to open resource_detail.
    /// </summary>
    private void OnResourceLabelClicked(int resourceId)
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitSelectObject("resource_detail", "resource", resourceId.ToString());
        }
    }

    /// <summary>
    /// Rebuild resource name labels from current resourceCache.
    /// Destroy existing labels before creating replacements. Called when LoadAllResources completes.
    /// </summary>
    private void RebuildResourceLabels()
    {
        ClearAllResourceLabels();
        EnsureUICanvas();

        resourceCentersById.Clear();
        foreach (var resource in resourceCache)
        {
            if (resource.center_point == null || resource.center_point.Count < 2) continue;
            resourceCentersById[resource.resource_id] = new Vector2(resource.center_point[0], resource.center_point[1]);
            CreateResourceLabelUI(resource);
        }

        // Reset tracking to guarantee label positions update next frame
        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// Destroy every resource label. Called by ClearAllCells / RebuildResourceLabels.
    /// </summary>
    private void ClearAllResourceLabels()
    {
        foreach (var kvp in resourceLabelUIs)
        {
            if (kvp.Value != null) Destroy(kvp.Value);
        }
        resourceLabelUIs.Clear();
        resourceCentersById.Clear();
    }

    /// <summary>
    /// Rebuild heatmap meshes from current resourceCache and terrain data.
    /// Combine grid-subdivided vertices for each resource into one Mesh.
    /// </summary>
    private void BuildHeatmapMesh()
    {
        if (!areResourcesLoaded) return;

        EnsureHeatmapMeshObjects();
        if (heatmapMesh == null) return;

        List<Vector3> verts = new List<Vector3>();
        List<Color> colors = new List<Color>();
        List<Vector2> uvs = new List<Vector2>();
        List<int> indices = new List<int>();

        foreach (var resource in resourceCache)
        {
            if (resource.polygon == null || resource.polygon.Count < 3) continue;
            AddTessellatedResource(resource, verts, colors, uvs, indices);
        }

        heatmapMesh.Clear();
        if (verts.Count > 0)
        {
            heatmapMesh.SetVertices(verts);
            heatmapMesh.SetColors(colors);
            heatmapMesh.SetUVs(0, uvs);
            heatmapMesh.SetTriangles(indices, 0);
            heatmapMesh.RecalculateBounds();
        }
    }

    /// <summary>
    /// Subdivide one resource polygon into a grid; append vertices, colors, UVs, and triangles to the supplied lists.
    /// Extend vertices EDGE_FADE beyond the boundary and gradually reduce alpha to zero
    /// for soft edges without outlines.
    /// </summary>
    private void AddTessellatedResource(
        ResourceItem resource,
        List<Vector3> verts, List<Color> colors, List<Vector2> uvs, List<int> indices)
    {
        // Calculate AABB and expand by edge fade distance
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        foreach (var pt in resource.polygon)
        {
            if (pt[0] < minX) minX = pt[0];
            if (pt[0] > maxX) maxX = pt[0];
            if (pt[1] < minZ) minZ = pt[1];
            if (pt[1] > maxZ) maxZ = pt[1];
        }
        minX -= EDGE_FADE; maxX += EDGE_FADE;
        minZ -= EDGE_FADE; maxZ += EDGE_FADE;

        int nx = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / GRID_STEP) + 1);
        int nz = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ) / GRID_STEP) + 1);

        Color baseColor = resourceColors.TryGetValue(resource.product_id, out var c)
            ? c
            : new Color(0.5f, 0.5f, 0.5f, 1.0f);

        // Generate this resource's grid vertices. baseVertCount is the offset before appending.
        int baseVertCount = verts.Count;
        // Vertex index grid (used during triangle generation)
        int[,] idxGrid = new int[nx, nz];
        // Edge-fade alpha per vertex (skip triangles in cells whose vertices all have zero alpha)
        float[,] alphaGrid = new float[nx, nz];

        for (int i = 0; i < nx; i++)
        {
            for (int j = 0; j < nz; j++)
            {
                float x = minX + i * GRID_STEP;
                float z = minZ + j * GRID_STEP;

                float signedDist = SignedDistanceToPolygon(x, z, resource.polygon);
                // signedDist in [EDGE_FADE, +∞) gives alpha=1
                //                (-EDGE_FADE, EDGE_FADE) gives a linear alpha=0..1 ramp
                //                (-∞, -EDGE_FADE] gives alpha=0 (fully outside polygon)
                float alpha;
                if (signedDist >= EDGE_FADE)
                {
                    alpha = 1f;
                }
                else if (signedDist <= -EDGE_FADE)
                {
                    alpha = 0f;
                }
                else
                {
                    alpha = (signedDist + EDGE_FADE) / (2f * EDGE_FADE);
                }

                alphaGrid[i, j] = alpha;
                idxGrid[i, j] = baseVertCount + (i * nz) + j;

                // Sample terrain heights and lift slightly to prevent Z-fighting
                float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z)) + HEIGHT_OFFSET;

                Color vc = baseColor;
                vc.a = alpha;
                verts.Add(new Vector3(x, y, z));
                colors.Add(vc);
                // Use world XZ directly as UVs. The shader multiplies by _NoiseScale.
                uvs.Add(new Vector2(x, z));
            }
        }

        // Generate two triangles per cell. Skip if all four vertices are fully transparent,
        // reducing unnecessary fragment shader work far from polygons.
        for (int i = 0; i < nx - 1; i++)
        {
            for (int j = 0; j < nz - 1; j++)
            {
                if (alphaGrid[i, j] <= 0f &&
                    alphaGrid[i + 1, j] <= 0f &&
                    alphaGrid[i, j + 1] <= 0f &&
                    alphaGrid[i + 1, j + 1] <= 0f)
                {
                    continue;
                }

                int v00 = idxGrid[i, j];
                int v10 = idxGrid[i + 1, j];
                int v01 = idxGrid[i, j + 1];
                int v11 = idxGrid[i + 1, j + 1];

                // Upward (+Y) winding.
                // Connect v00→v01→v11→v10 to face upward when viewed from above.
                indices.Add(v00); indices.Add(v01); indices.Add(v11);
                indices.Add(v00); indices.Add(v11); indices.Add(v10);
            }
        }
    }

    /// <summary>
    /// Calculate signed distance to a polygon (positive inside, negative outside).
    /// Use PolygonUtils.Contains for containment and minimum distance to all edges for magnitude.
    /// Works for nonconvex polygons too (containment supplies sign; nearest edge distance supplies magnitude).
    /// </summary>
    private static float SignedDistanceToPolygon(float x, float z, List<List<float>> polygon)
    {
        bool inside = PolygonUtils.Contains(polygon, x, z);
        float minDist = float.PositiveInfinity;
        int count = polygon.Count;
        for (int i = 0; i < count; i++)
        {
            int nextI = (i + 1) % count;
            float ax = polygon[i][0], az = polygon[i][1];
            float bx = polygon[nextI][0], bz = polygon[nextI][1];
            float d = PolylineUtils.DistanceToSegment(new Vector2(x, z), new Vector2(ax, az), new Vector2(bx, bz));
            if (d < minDist) minDist = d;
        }
        return inside ? minDist : -minDist;
    }
}
