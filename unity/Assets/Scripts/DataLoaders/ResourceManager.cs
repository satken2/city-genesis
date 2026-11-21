using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

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
/// - Subdivide each resource polygon AABB with an adaptive grid, aligning vertex heights to terrain.
///   This avoids flat planes buried beneath terrain and drapes the surface like a sheet.
/// - Compute 2D grid topology, scanline containment, and boundary-band fade off the main thread,
///   emitting only vertices used by non-transparent cells.
/// - Sample terrain heights incrementally under RenderWorkScheduler; when terrain tiles arrive,
///   re-sample only vertices in the updated cell rather than rebuilding the full mesh.
/// - Fade polygon boundaries using vertex alpha; Custom/ResourceHeatmap procedural noise
///   creates a soft cloud/heatmap appearance.
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

    // Cells potentially containing resources while the 2D layout is still building.
    private HashSet<Vector2Int> resourceTouchedCells = new HashSet<Vector2Int>();

    // Cached 2D layout and vertex buffers for the current resourceCache.
    // Terrain tile updates only refresh Y coordinates for vertices belonging to the updated cell.
    private HeatmapMeshData builtData;
    private int loadGeneration = 0;
    private int meshWorkGeneration = 0;
    private Coroutine meshBuildCoroutine;
    private Coroutine heightUpdateCoroutine;
    private readonly HashSet<Vector2Int> dirtyHeightCells = new HashSet<Vector2Int>();
    private readonly Queue<Vector2Int> dirtyHeightCellQueue = new Queue<Vector2Int>();

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

    // Base subdivision grid spacing (meters) for small and medium resource polygons.
    private const float GRID_STEP = 10f;

    // Upper target for AABB grid sample points per resource.
    // Very large polygons (e.g. map-spanning rivers) widen the grid step so build cost and vertex count stay bounded.
    private const int MAX_GRID_POINTS_PER_RESOURCE = 65536;

    // Fade distance outside polygon boundaries (meters).
    // Generate vertices this far outside and gradually reduce alpha to zero for soft edges.
    private const float EDGE_FADE = 12f;

    // Lift above terrain surface (meters).
    // Combines Z-fighting prevention with a floating-cloud appearance.
    private const float HEIGHT_OFFSET = 0.5f;

    // Vertices sampled per RenderWorkScheduler cooperative yield during terrain height sampling.
    private const int HEIGHT_SAMPLE_BATCH_SIZE = 1024;

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
        StopMeshCoroutines();
    }

    private void OnDestroy()
    {
        StopMeshCoroutines();
        if (Instance == this) Instance = null;
        if (heatmapMeshObj != null) Destroy(heatmapMeshObj);
        if (heatmapMesh != null) Destroy(heatmapMesh);
        if (heatmapMaterial != null) Destroy(heatmapMaterial);
        if (uiCanvasRoot != null) Destroy(uiCanvasRoot);
    }

    /// <summary>
    /// Toggle resource visibility.
    /// Disable the mesh renderer and label Canvas when off; reuse the cached mesh when available,
    /// or start an asynchronous build when toggling on for newly loaded resources.
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (isVisible == visible) return;
        isVisible = visible;

        // SetActive on the entire Canvas controls every label together
        if (uiCanvasRoot != null)
        {
            uiCanvasRoot.SetActive(visible);
        }

        if (!visible)
        {
            if (heatmapMeshRenderer != null)
            {
                heatmapMeshRenderer.enabled = false;
            }
            StopMeshCoroutines();
            return;
        }

        // Reset camera tracking on visibility enable to guarantee label position updates next frame
        lastCamPos = Vector3.negativeInfinity;

        if (!areResourcesLoaded) return;

        if (builtData != null)
        {
            EnsureHeatmapMeshObjects();
            if (heatmapMeshRenderer != null)
            {
                heatmapMeshRenderer.enabled = true;
            }
            StartHeightUpdateIfNeeded();
        }
        else
        {
            RequestHeatmapBuild();
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
        heatmapMeshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        heatmapMeshRenderer.receiveShadows = false;

        heatmapMesh = new Mesh { name = "ResourceHeatmap", indexFormat = IndexFormat.UInt32 };
        heatmapMesh.MarkDynamic();
        heatmapMeshFilter.mesh = heatmapMesh;

        heatmapMeshRenderer.enabled = isVisible && builtData != null;
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
        loadGeneration++;
        resourceCache.Clear();
        areResourcesLoaded = false;
        resourceTouchedCells.Clear();
        InvalidateBuiltMesh();
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
        int requestGeneration = loadGeneration;

        string url = "/resources"; // ApiClient adds BASE_URL
        yield return ApiClient.Get<ResourcesListResponse>(
            url,
            response =>
            {
                if (requestGeneration != loadGeneration)
                {
                    isLoadInProgress = false;
                    return;
                }
                if (response != null && response.resources != null)
                {
                    resourceCache = response.resources;
                    areResourcesLoaded = true;
                    InvalidateBuiltMesh();
                    RecomputeResourceTouchedCells();
                    RebuildResourceLabels();
                    if (isVisible)
                    {
                        RequestHeatmapBuild();
                    }
                }
                isLoadInProgress = false;
            },
            error =>
            {
                Debug.LogError($"ResourceManager: Failed to load resources: {error}");
                if (requestGeneration == loadGeneration)
                {
                    areResourcesLoaded = false;
                    StartCoroutine(RetryInitialLoad());
                }
                isLoadInProgress = false;
            }
        );
    }

    /// <summary>
    /// Recalculate cells potentially containing resources from current resourceCache.
    /// Used by OnTerrainTileReady while the 2D layout is still building.
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
        if (!areResourcesLoaded) return;

        if (builtData != null)
        {
            if (!builtData.cellToVertices.ContainsKey(cellIndex)) return;
            EnqueueDirtyHeightCell(cellIndex);
            if (isVisible) StartHeightUpdateIfNeeded();
        }
        else if (meshBuildCoroutine != null && resourceTouchedCells.Contains(cellIndex))
        {
            // Terrain arrived while initial height sampling is in progress; re-sample this cell once built.
            EnqueueDirtyHeightCell(cellIndex);
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

    internal readonly struct ResourceLayoutInput
    {
        public readonly Vector2[] polygon;
        public readonly Color baseColor;

        public ResourceLayoutInput(Vector2[] polygon, Color baseColor)
        {
            this.polygon = polygon;
            this.baseColor = baseColor;
        }
    }

    internal sealed class HeatmapMeshData
    {
        public readonly List<Vector3> vertices = new List<Vector3>();
        public readonly List<Color> colors = new List<Color>();
        public readonly List<Vector2> uvs = new List<Vector2>();
        public readonly List<int> indices = new List<int>();
        public readonly Dictionary<Vector2Int, List<int>> cellToVertices = new Dictionary<Vector2Int, List<int>>();
    }

    private void StopMeshCoroutines()
    {
        meshWorkGeneration++;
        if (meshBuildCoroutine != null)
        {
            RenderWorkScheduler.Stop(this, meshBuildCoroutine);
            meshBuildCoroutine = null;
        }
        if (heightUpdateCoroutine != null)
        {
            RenderWorkScheduler.Stop(this, heightUpdateCoroutine);
            heightUpdateCoroutine = null;
        }
    }

    private void InvalidateBuiltMesh()
    {
        StopMeshCoroutines();
        builtData = null;
        dirtyHeightCells.Clear();
        dirtyHeightCellQueue.Clear();
        if (heatmapMesh != null)
        {
            heatmapMesh.Clear();
        }
        if (heatmapMeshRenderer != null)
        {
            heatmapMeshRenderer.enabled = false;
        }
    }

    private void EnqueueDirtyHeightCell(Vector2Int cellIndex)
    {
        if (dirtyHeightCells.Add(cellIndex))
        {
            dirtyHeightCellQueue.Enqueue(cellIndex);
        }
    }

    private void RequestHeatmapBuild()
    {
        if (!areResourcesLoaded || !isVisible) return;
        if (meshBuildCoroutine != null) return;

        EnsureHeatmapMeshObjects();
        if (heatmapMesh == null) return;

        var inputs = SnapshotLayoutInputs();
        float cellSize = TerrainManager.CellSize > 0f ? TerrainManager.CellSize : 500f;
        int generation = meshWorkGeneration;
        meshBuildCoroutine = RenderWorkScheduler.Start(
            this,
            BuildHeatmapMeshCoroutine(inputs, cellSize, generation),
            "ResourceManager");
    }

    private List<ResourceLayoutInput> SnapshotLayoutInputs()
    {
        var inputs = new List<ResourceLayoutInput>(resourceCache.Count);
        foreach (var resource in resourceCache)
        {
            if (resource.polygon == null || resource.polygon.Count < 3) continue;
            var poly = new Vector2[resource.polygon.Count];
            for (int i = 0; i < poly.Length; i++)
            {
                poly[i] = new Vector2(resource.polygon[i][0], resource.polygon[i][1]);
            }
            Color baseColor = resourceColors.TryGetValue(resource.product_id, out var c)
                ? c
                : new Color(0.5f, 0.5f, 0.5f, 1.0f);
            inputs.Add(new ResourceLayoutInput(poly, baseColor));
        }
        return inputs;
    }

    private IEnumerator BuildHeatmapMeshCoroutine(
        List<ResourceLayoutInput> inputs,
        float cellSize,
        int generation)
    {
        try
        {
            Task<HeatmapMeshData> layoutTask = BackgroundWork.Run(() => BuildHeatmapLayout(inputs, cellSize));
            while (!layoutTask.IsCompleted)
            {
                if (generation != meshWorkGeneration || !isVisible) yield break;
                yield return null;
            }
            if (generation != meshWorkGeneration || !isVisible) yield break;

            if (layoutTask.IsFaulted)
            {
                if (layoutTask.Exception != null)
                {
                    Debug.LogError($"ResourceManager: Failed to build heatmap layout: {layoutTask.Exception.Flatten()}");
                }
                yield break;
            }

            HeatmapMeshData data = layoutTask.Result;
            int vertexCount = data.vertices.Count;
            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 v = data.vertices[i];
                v.y = Utils.GetGroundHeight(new Vector3(v.x, 1000f, v.z)) + HEIGHT_OFFSET;
                data.vertices[i] = v;

                if (((i + 1) % HEIGHT_SAMPLE_BATCH_SIZE) == 0)
                {
                    yield return RenderWorkScheduler.Continue;
                    if (generation != meshWorkGeneration || !isVisible) yield break;
                }
            }

            if (generation != meshWorkGeneration || !isVisible) yield break;

            EnsureHeatmapMeshObjects();
            if (heatmapMesh == null) yield break;

            heatmapMesh.Clear();
            heatmapMesh.indexFormat = IndexFormat.UInt32;
            if (vertexCount > 0)
            {
                heatmapMesh.SetVertices(data.vertices);
                heatmapMesh.SetColors(data.colors);
                heatmapMesh.SetUVs(0, data.uvs);
                heatmapMesh.SetTriangles(data.indices, 0);
                heatmapMesh.RecalculateBounds();
            }

            builtData = data;
            if (heatmapMeshRenderer != null)
            {
                heatmapMeshRenderer.enabled = isVisible;
            }

            if (dirtyHeightCellQueue.Count > 0)
            {
                StartHeightUpdateIfNeeded();
            }
        }
        finally
        {
            if (generation == meshWorkGeneration)
            {
                meshBuildCoroutine = null;
            }
        }
    }

    private void StartHeightUpdateIfNeeded()
    {
        if (!isVisible || builtData == null || dirtyHeightCellQueue.Count == 0) return;
        if (heightUpdateCoroutine != null) return;

        int generation = meshWorkGeneration;
        heightUpdateCoroutine = RenderWorkScheduler.Start(
            this,
            UpdateDirtyHeightsCoroutine(generation),
            "ResourceManager");
    }

    private IEnumerator UpdateDirtyHeightsCoroutine(int generation)
    {
        try
        {
            int sampledInBatch = 0;
            bool modified = false;

            while (dirtyHeightCellQueue.Count > 0)
            {
                if (generation != meshWorkGeneration || !isVisible || builtData == null) yield break;

                Vector2Int cell = dirtyHeightCellQueue.Peek();
                if (!builtData.cellToVertices.TryGetValue(cell, out List<int> vertIndices))
                {
                    dirtyHeightCellQueue.Dequeue();
                    dirtyHeightCells.Remove(cell);
                    continue;
                }

                for (int i = 0; i < vertIndices.Count; i++)
                {
                    int idx = vertIndices[i];
                    Vector3 v = builtData.vertices[idx];
                    v.y = Utils.GetGroundHeight(new Vector3(v.x, 1000f, v.z)) + HEIGHT_OFFSET;
                    builtData.vertices[idx] = v;
                    modified = true;

                    sampledInBatch++;
                    if (sampledInBatch >= HEIGHT_SAMPLE_BATCH_SIZE)
                    {
                        sampledInBatch = 0;
                        yield return RenderWorkScheduler.Continue;
                        if (generation != meshWorkGeneration || !isVisible || builtData == null) yield break;
                    }
                }

                dirtyHeightCellQueue.Dequeue();
                dirtyHeightCells.Remove(cell);
            }

            if (modified && generation == meshWorkGeneration && builtData != null && heatmapMesh != null)
            {
                heatmapMesh.SetVertices(builtData.vertices);
                heatmapMesh.RecalculateBounds();
            }
        }
        finally
        {
            if (generation == meshWorkGeneration)
            {
                heightUpdateCoroutine = null;
            }
        }
    }

    /// <summary>
    /// Build 2D heatmap grid topology, edge-fade vertex colors, UVs, and per-cell vertex indices off the main thread.
    /// Leaves Y at 0; callers sample terrain heights on the main thread afterwards.
    /// </summary>
    internal static HeatmapMeshData BuildHeatmapLayout(IReadOnlyList<ResourceLayoutInput> resources, float cellSize)
    {
        var data = new HeatmapMeshData();
        if (resources == null || resources.Count == 0) return data;
        if (cellSize <= 0f) cellSize = 500f;

        var crossings = new List<float>();
        for (int i = 0; i < resources.Count; i++)
        {
            var input = resources[i];
            if (input.polygon == null || input.polygon.Length < 3) continue;
            AddTessellatedResourceLayout(input.polygon, input.baseColor, cellSize, crossings, data);
        }
        return data;
    }

    internal static float ComputeAdaptiveGridStep(float width, float height)
    {
        float step = GRID_STEP;
        double area = (double)width * height;
        if (area > 0.0)
        {
            float adaptiveStep = Mathf.Sqrt((float)(area / MAX_GRID_POINTS_PER_RESOURCE));
            if (adaptiveStep > step) step = adaptiveStep;
        }
        return step;
    }

    /// <summary>
    /// Subdivide one resource polygon into an adaptive grid using scanline containment and
    /// boundary-band edge distance updates, emitting only vertices referenced by non-transparent cells.
    /// </summary>
    private static void AddTessellatedResourceLayout(
        Vector2[] polygon,
        Color baseColor,
        float cellSize,
        List<float> crossings,
        HeatmapMeshData data)
    {
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2 pt = polygon[i];
            if (pt.x < minX) minX = pt.x;
            if (pt.x > maxX) maxX = pt.x;
            if (pt.y < minZ) minZ = pt.y;
            if (pt.y > maxZ) maxZ = pt.y;
        }
        minX -= EDGE_FADE; maxX += EDGE_FADE;
        minZ -= EDGE_FADE; maxZ += EDGE_FADE;

        float width = maxX - minX;
        float height = maxZ - minZ;
        float step = ComputeAdaptiveGridStep(width, height);

        int nx = Mathf.Max(2, Mathf.CeilToInt(width / step) + 1);
        int nz = Mathf.Max(2, Mathf.CeilToInt(height / step) + 1);

        // Scanline parity determines containment for every grid point in O(nz * V + nx * nz).
        bool[] insideGrid = new bool[nx * nz];
        for (int j = 0; j < nz; j++)
        {
            float z = minZ + j * step;
            crossings.Clear();
            PolygonUtils.AppendRayCrossings(polygon, z, crossings);
            if (crossings.Count == 0) continue;

            crossings.Sort();
            int passed = 0;
            int crossingCount = crossings.Count;
            for (int i = 0; i < nx; i++)
            {
                float x = minX + i * step;
                while (passed < crossingCount && crossings[passed] <= x) passed++;
                if (((crossingCount - passed) & 1) == 1)
                {
                    insideGrid[i * nz + j] = true;
                }
            }
        }

        // Only points within EDGE_FADE of an edge need exact boundary distance;
        // initialize to fadeSq so interior/exterior points farther away saturate to alpha 1 or 0 without Sqrt.
        float fadeSq = EDGE_FADE * EDGE_FADE;
        float[] minDistSqGrid = new float[nx * nz];
        Array.Fill(minDistSqGrid, fadeSq);

        // Subdivide long edges so diagonal segments only touch grid points inside their narrow EDGE_FADE band.
        float maxSegmentLen = Mathf.Max(step, EDGE_FADE * 2f);
        int polyCount = polygon.Length;
        for (int k = 0; k < polyCount; k++)
        {
            Vector2 a = polygon[k];
            Vector2 b = polygon[(k + 1) % polyCount];
            float edgeLen = Vector2.Distance(a, b);
            int subCount = edgeLen > maxSegmentLen ? Mathf.CeilToInt(edgeLen / maxSegmentLen) : 1;

            for (int s = 0; s < subCount; s++)
            {
                Vector2 p0 = s == 0 ? a : Vector2.Lerp(a, b, (float)s / subCount);
                Vector2 p1 = s == subCount - 1 ? b : Vector2.Lerp(a, b, (float)(s + 1) / subCount);

                float sMinX = Mathf.Min(p0.x, p1.x) - EDGE_FADE;
                float sMaxX = Mathf.Max(p0.x, p1.x) + EDGE_FADE;
                float sMinZ = Mathf.Min(p0.y, p1.y) - EDGE_FADE;
                float sMaxZ = Mathf.Max(p0.y, p1.y) + EDGE_FADE;

                int i0 = Mathf.Max(0, Mathf.FloorToInt((sMinX - minX) / step));
                int i1 = Mathf.Min(nx - 1, Mathf.CeilToInt((sMaxX - minX) / step));
                int j0 = Mathf.Max(0, Mathf.FloorToInt((sMinZ - minZ) / step));
                int j1 = Mathf.Min(nz - 1, Mathf.CeilToInt((sMaxZ - minZ) / step));

                for (int i = i0; i <= i1; i++)
                {
                    float x = minX + i * step;
                    int colBase = i * nz;
                    for (int j = j0; j <= j1; j++)
                    {
                        float z = minZ + j * step;
                        float dSq = PolylineUtils.DistanceToSegmentSq(new Vector2(x, z), p0, p1);
                        if (dSq < minDistSqGrid[colBase + j])
                        {
                            minDistSqGrid[colBase + j] = dSq;
                        }
                    }
                }
            }
        }

        float[] alphaGrid = new float[nx * nz];
        float invTwoFade = 1f / (2f * EDGE_FADE);
        for (int idx = 0; idx < alphaGrid.Length; idx++)
        {
            float dSq = minDistSqGrid[idx];
            if (dSq >= fadeSq)
            {
                alphaGrid[idx] = insideGrid[idx] ? 1f : 0f;
            }
            else
            {
                float dist = Mathf.Sqrt(dSq);
                float signedDist = insideGrid[idx] ? dist : -dist;
                alphaGrid[idx] = (signedDist + EDGE_FADE) * invTwoFade;
            }
        }

        // Emit vertices lazily only for grid corners referenced by non-transparent cells.
        int[] idxGrid = new int[nx * nz];
        Array.Fill(idxGrid, -1);

        int GetOrCreateVertex(int gi, int gj)
        {
            int flat = gi * nz + gj;
            int existing = idxGrid[flat];
            if (existing >= 0) return existing;

            float x = minX + gi * step;
            float z = minZ + gj * step;
            int vIdx = data.vertices.Count;
            idxGrid[flat] = vIdx;

            Color vc = baseColor;
            vc.a = alphaGrid[flat];
            data.vertices.Add(new Vector3(x, 0f, z));
            data.colors.Add(vc);
            // Use world XZ directly as UVs. The shader multiplies by _NoiseScale.
            data.uvs.Add(new Vector2(x, z));

            var cell = new Vector2Int(Mathf.FloorToInt(x / cellSize), Mathf.FloorToInt(z / cellSize));
            if (!data.cellToVertices.TryGetValue(cell, out List<int> list))
            {
                list = new List<int>();
                data.cellToVertices[cell] = list;
            }
            list.Add(vIdx);
            return vIdx;
        }

        // Generate two triangles per cell. Skip if all four vertices are fully transparent.
        for (int i = 0; i < nx - 1; i++)
        {
            int col0 = i * nz;
            int col1 = (i + 1) * nz;
            for (int j = 0; j < nz - 1; j++)
            {
                if (alphaGrid[col0 + j] <= 0f &&
                    alphaGrid[col1 + j] <= 0f &&
                    alphaGrid[col0 + j + 1] <= 0f &&
                    alphaGrid[col1 + j + 1] <= 0f)
                {
                    continue;
                }

                int v00 = GetOrCreateVertex(i, j);
                int v10 = GetOrCreateVertex(i + 1, j);
                int v01 = GetOrCreateVertex(i, j + 1);
                int v11 = GetOrCreateVertex(i + 1, j + 1);

                // Upward (+Y) winding.
                // Connect v00→v01→v11→v10 to face upward when viewed from above.
                data.indices.Add(v00); data.indices.Add(v01); data.indices.Add(v11);
                data.indices.Add(v00); data.indices.Add(v11); data.indices.Add(v10);
            }
        }
    }
}
