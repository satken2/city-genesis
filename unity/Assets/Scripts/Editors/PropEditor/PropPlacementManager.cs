using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// PropPlacementManager:
/// Placement editor for terrain props (decorative forests, rocks, bushes, etc.). Similar interaction to
/// TransitPlanManager / ResourceAreaEditManager, placing props on the 3D map in two modes.
///
///   Individual mode (instance): left click places one PropInstance. The cursor-following ghost is
///     translucent; confirming with left click makes it an opaque model. Right click undoes the latest placement.
///     Rotation, scale, and height come from UI settings, applied incrementally through edit_prop:change_settings.
///     As with direct building placement, middle-button dragging rotates and PageUp/PageDown changes height in 3D;
///     results are written back to panel steppers through edit_prop:placement_settings (changes only the next ghost's
///     settings, leaving provisionally placed instances unchanged).
///     Switching types retains provisional placements, each recording the type used when placed.
///
///   Area mode (scatter): left click places scatter polygon vertices (PropScatter). Once there are 3 or more,
///     generate seed-based scatter positions and show a simple preview with translucent prefab ghosts. Density/seed
///     changes regenerate it. Right click undoes the latest vertex.
///
/// The UI (Tauri) directly submits confirmation (POST /api/v1/props/instances or /scatters). This manager
/// only builds placement data, reports updates (edit_prop:update_instances / edit_prop:update_shape),
/// and draws placement previews (same responsibility split as public transport placement/resource editing).
///
/// IpcStateCoordinator dynamically creates/initializes it on STATE_EDIT_PROP. No scene placement is needed.
/// </summary>
public class PropPlacementManager : MonoBehaviour
{
    private enum Mode { Instance, Scatter }


    // Gizmo drawing parameters for scatter polygons (area mode), in pixels.
    private const float VERTEX_MARKER_HALF_SIZE = 6f;
    private const float LINE_THICKNESS = 2.5f;

    private static readonly Color LineColor = new Color(0.4f, 0.9f, 0.5f, 0.95f);
    private static readonly Color VertexColor = new Color(1f, 1f, 1f, 1f);
    private static readonly Color PreviewColor = new Color(0.4f, 0.9f, 0.5f, 0.5f);

    // Height change per PageUp/PageDown press (m). Matches the panel stepper; Shift changes it to 1m.
    private const float HeightKeyStep = 0.1f;
    private const float HeightKeyShiftStep = 1.0f;

    private Mode mode = Mode.Instance;
    private bool shapeFinalized = false;
    private int propType = 0;
    private float rotation = 0f;
    private float scale = 1f;
    private float height = 0f;
    private float density = 400f;
    private int seed = 0;

    private bool active = false;

    // 3D controls for individual mode (PlacementInput.cs, shared with direct building placement).
    private readonly MiddleDragRotation middleDrag = new MiddleDragRotation();
    private readonly KeyRepeat heightUpKey = new KeyRepeat(Key.PageUp);
    private readonly KeyRepeat heightDownKey = new KeyRepeat(Key.PageDown);

    // Parent for placed props. Group under one GameObject for bulk destruction.
    private Transform container;

    // ---- Individual mode ----
    // Provisionally placed instances (opaque). Store GameObjects paired with placement data.
    private readonly List<GameObject> placedObjects = new List<GameObject>();
    private readonly List<(int propType, float x, float y, float z, float rotation, float scale)> placedInstances
        = new List<(int, float, float, float, float, float)>();
    // Cursor-following translucent ghost (individual mode only).
    private GameObject hoverGhost;
    private bool hoverValid = false;
    private Vector3 hoverPos;

    // ---- Area mode ----
    private readonly List<Vector3> scatterVertices = new List<Vector3>();
    private sealed class PreviewBatch
    {
        public PropRenderModel.Part part;
        public Matrix4x4[] matrices;
    }
    private readonly List<PreviewBatch> previewBatches = new List<PreviewBatch>();
    private PropRenderModel previewModel;
    private Coroutine previewWorker;
    private int previewRevision;
    private Vector3 previewOffset;
    private OverlayMeshRenderer overlayRenderer;
    private GameObject overlayMeshObj;

    private Camera gameCamera;
    private Material previewMaterial;

    // =====================================================================
    // Lifecycle
    // =====================================================================

    private void Awake()
    {
        gameCamera = Camera.main;
        // Reuse the translucent building construction preview material (alpha≈0.39).
        var sourceMaterial = Resources.Load<Material>("PreviewBuildingMaterial");
        if (sourceMaterial != null) previewMaterial = CreatePreviewMaterial(sourceMaterial);
        EnsureContainer();
        EnsureOverlayRenderer();
    }

    /// <summary>
    /// 3D individual-mode controls. Middle-button dragging rotates (immediate ghost rotation during dragging,
    /// final angle reported to UI on release); PageUp/PageDown adjusts ground-relative height (report each change).
    /// Both change only the next ghost's settings, leaving provisionally placed instances unchanged.
    /// </summary>
    private void Update()
    {
        if (!active || mode != Mode.Instance || PlacementInput.IsBlocked())
        {
            // Clear state if the mode is left, camera controls start, etc. during dragging; return the current angle to the UI.
            if (middleDrag.IsDragging) { middleDrag.Cancel(); EmitPlacementSettings(); }
            return;
        }

        switch (middleDrag.Update(out int deltaDegrees))
        {
            case MiddleDragRotation.Result.Rotated:
                rotation = Mathf.Repeat(rotation + deltaDegrees, 360f);
                RefreshHoverGhost();
                break;
            case MiddleDragRotation.Result.Released:
                EmitPlacementSettings();
                break;
        }
        if (middleDrag.IsDragging) return;

        float step = PlacementInput.ShiftHeld ? HeightKeyShiftStep : HeightKeyStep;
        float heightDelta = 0f;
        if (heightUpKey.Fired()) heightDelta += step;
        if (heightDownKey.Fired()) heightDelta -= step;
        if (heightDelta == 0f) return;
        // Round to cm to avoid accumulating floating-point remainders from 0.1m increments.
        height = Mathf.Round((height + heightDelta) * 100f) / 100f;
        RefreshHoverGhost();
        EmitPlacementSettings();
    }

    private void LateUpdate()
    {
        if (!active || mode != Mode.Scatter) return;
        UpdateOverlay();
        var manager = PropManager.Instance;
        if (manager == null) return;
        if (previewRevision != manager.PreviewRevision || previewOffset != manager.SceneOffset) RegenerateScatterPreview();
        if (previewMaterial == null) return;
        foreach (var batch in previewBatches)
            Graphics.RenderMeshInstanced(new RenderParams(previewMaterial) { camera = gameCamera,
                shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off, receiveShadows = false },
                batch.part.mesh, batch.part.submesh, batch.matrices);
    }

    private void EnsureContainer()
    {
        if (container != null) return;
        var go = new GameObject("PropPlacementContainer");
        container = go.transform;
        container.SetParent(transform, false);
    }

    private void EnsureOverlayRenderer()
    {
        if (overlayMeshObj != null) return;
        // Always draw scatter vertices/edges in front, never buried in terrain (same as other editor gizmos).
        overlayRenderer = OverlayMeshRenderer.Create("PropEditorOverlayMesh", alwaysOnTop: true);
        overlayMeshObj = overlayRenderer.gameObject;
    }

    // =====================================================================
    // Public API: mode transitions
    // =====================================================================

    /// <summary>
    /// Initializes prop placement mode. placementMode is "instance" or "scatter".
    /// Uses rotation / scale / height as initial individual settings, or density / seed for area mode.
    /// </summary>
    public void InitializePlacement(string placementMode, int propType, float rotation, float scale, float density, int seed, float height = 0f)
    {
        if (gameCamera == null) gameCamera = Camera.main;
        EnsureContainer();
        EnsureOverlayRenderer();
        if (previewMaterial == null)
        {
            var sourceMaterial = Resources.Load<Material>("PreviewBuildingMaterial");
            if (sourceMaterial != null) previewMaterial = CreatePreviewMaterial(sourceMaterial);
        }

        // Changing types in individual mode retains placed instances and rebuilds only the next ghost.
        bool preserveInstances = active && mode == Mode.Instance && placementMode == "instance";
        if (preserveInstances) ClearHoverGhost();
        else ClearAll();

        mode = (placementMode == "scatter") ? Mode.Scatter : Mode.Instance;
        this.propType = propType;
        this.rotation = rotation;
        this.scale = scale;
        this.height = height;
        this.density = density;
        this.seed = seed;
        this.shapeFinalized = false;
        active = true;

        // Report provisional placements of all types to the UI after switching types as well.
        if (mode == Mode.Instance) EmitInstances();
        else EmitShape();

        Debug.Log($"PropPlacementManager: 配置モード初期化 mode={placementMode}, prop_type={propType}");
    }

    /// <summary>
    /// Ends placement mode and clears all state and previews.
    /// </summary>
    public void FinishPlacement()
    {
        active = false;
        ClearAll();
        if (overlayRenderer != null)
        {
            overlayRenderer.ClearMesh();
        }
    }

    /// <summary>
    /// UI settings update (edit_prop:change_settings). Apply only keys received.
    /// In individual mode, apply rotation / scale / height to the ghost; in area mode, density / seed to the scatter preview.
    /// </summary>
    public void OnChangeSettingsFromIpc(Dictionary<string, object> settings)
    {
        if (settings == null) return;

        bool scatterDirty = false;
        if (settings.TryGetValue("rotation", out object rObj) && rObj != null)
            rotation = System.Convert.ToSingle(rObj);
        if (settings.TryGetValue("scale", out object sObj) && sObj != null)
            scale = System.Convert.ToSingle(sObj);
        if (settings.TryGetValue("height", out object hObj) && hObj != null)
            height = System.Convert.ToSingle(hObj);
        if (settings.TryGetValue("density", out object dObj) && dObj != null)
        {
            density = System.Convert.ToSingle(dObj);
            scatterDirty = true;
        }
        if (settings.TryGetValue("seed", out object seedObj) && seedObj != null)
        {
            seed = System.Convert.ToInt32(seedObj);
            scatterDirty = true;
        }

        if (mode == Mode.Instance)
        {
            RefreshHoverGhost();
        }
        else if (scatterDirty)
        {
            RegenerateScatterPreview();
        }
    }

    /// <summary>Immediately applies current rotation/scale/height to the hover ghost (does nothing if not created).</summary>
    private void RefreshHoverGhost()
    {
        if (hoverGhost != null && hoverValid)
        {
            ApplyTransform(hoverGhost, hoverPos, rotation, scale);
        }
    }

    /// <summary>Writes rotation/height changes made in 3D back to the panel (the panel does not echo them to Unity).</summary>
    private void EmitPlacementSettings()
    {
        IpcClient.Instance?.EmitEditPropPlacementSettings(rotation, height);
    }

    // =====================================================================
    // Input handlers (called by TerrainInputHandler)
    // =====================================================================

    public void OnTerrainLeftClick(Vector3 worldPos)
    {
        if (!active) return;

        if (mode == Mode.Instance)
        {
            // Do not place on left click during middle-button rotation (avoids accidental placement mid-rotation).
            if (middleDrag.IsDragging) return;

            // Commit an opaque model at this position.
            GameObject go = InstantiateProp(worldPos, rotation, scale, ghost: false);
            if (go != null)
            {
                placedObjects.Add(go);
                Vector3 offset = PropManager.Instance != null ? PropManager.Instance.SceneOffset : Vector3.zero;
                placedInstances.Add((propType, worldPos.x - offset.x, height, worldPos.z - offset.z, rotation, scale));
                EmitInstances();
            }
        }
        else
        {
            // Do not add vertices after provisional finalization.
            if (shapeFinalized) return;

            // Add a scatter polygon vertex.
            scatterVertices.Add(worldPos);
            EmitShape();
            if (scatterVertices.Count >= 3)
            {
                RegenerateScatterPreview();
            }
        }
    }

    public void OnTerrainRightClick(Vector3 worldPos)
    {
        if (!active) return;

        if (mode == Mode.Instance)
        {
            // Undo the latest placement (reverts mistakes; deleting existing props is a separate feature).
            if (placedObjects.Count > 0)
            {
                int last = placedObjects.Count - 1;
                if (placedObjects[last] != null) Destroy(placedObjects[last]);
                placedObjects.RemoveAt(last);
                placedInstances.RemoveAt(last);
                EmitInstances();
            }
        }
        else
        {
            // Right click provisionally finalizes the shape.
            // Enter the provisionally finalized state when there are at least 3 vertices.
            if (scatterVertices.Count >= 3)
            {
                shapeFinalized = true;
                EmitShape();
                RegenerateScatterPreview();
            }
        }
    }

    public void OnTerrainHover(Vector3 worldPos)
    {
        if (!active) return;

        if (mode == Mode.Instance)
        {
            // Fix the ghost position during middle-button rotation (anchor it so horizontal cursor movement does not make it slide).
            if (middleDrag.IsDragging && hoverGhost != null) return;

            hoverValid = true;
            hoverPos = worldPos;
            if (hoverGhost == null)
            {
                hoverGhost = InstantiateProp(worldPos, rotation, scale, ghost: true);
            }
            else
            {
                ApplyTransform(hoverGhost, worldPos, rotation, scale);
                hoverGhost.SetActive(true);
            }
        }
        else
        {
            // Disable to hide hover preview lines after provisional finalization.
            if (shapeFinalized)
            {
                hoverValid = false;
                return;
            }

            // In area mode, remember only the hover position for the Placing preview line.
            hoverValid = true;
            hoverPos = worldPos;
        }
    }

    // =====================================================================
    // Prop creation
    // =====================================================================

    /// <summary>
    /// Loads and places a prefab for prop_type. If ghost=true, overrides every Renderer
    /// with the translucent preview material. Returns null if loading fails.
    /// </summary>
    private GameObject InstantiateProp(Vector3 worldPos, float rot, float scl, bool ghost)
    {
        GameObject prefab = GetPrefab();
        if (prefab == null) return null;

        GameObject go = Instantiate(prefab, container);
        go.SetActive(true);
        ApplyTransform(go, worldPos, rot, scl);

        if (ghost && previewMaterial != null)
        {
            ApplyGhostMaterial(go);
        }
        return go;
    }

    /// <summary>Loads the prefab for prop_type through ModelManager (Resources/Models/Props/{id}).</summary>
    private GameObject GetPrefab()
    {
        if (ModelManager.Instance == null) return null;
        return ModelManager.Instance.GetModel($"Models/Props/{propType}");
    }

    /// <summary>Adds the specified height to terrain elevation. Does not adjust placement for overlaps or type.</summary>
    private void ApplyTransform(GameObject go, Vector3 worldPos, float rot, float scl)
    {
        worldPos.y = Utils.GetGroundHeight(worldPos) + height;
        go.transform.position = worldPos;
        go.transform.rotation = Quaternion.Euler(0f, rot, 0f);
        go.transform.localScale = Vector3.one * scl;
    }

    /// <summary>Overrides all child Renderers with translucent material for the pre-placement ghost.</summary>
    private void ApplyGhostMaterial(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            var mats = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = previewMaterial;
            r.sharedMaterials = mats;
        }
    }

    // =====================================================================
    // Scatter preview generation (area mode)
    // =====================================================================

    /// <summary>Uses the same coordinate hash, subcells, and obstacle checks as persistent rendering.</summary>
    private void RegenerateScatterPreview()
    {
        ClearScatterGhosts();
        var manager = PropManager.Instance;
        if (manager == null) return;
        previewRevision = manager.PreviewRevision;
        previewOffset = manager.SceneOffset;
        if (scatterVertices.Count < 3 || density <= 0) return;
        previewModel = new PropRenderModel(propType);
        previewWorker = StartCoroutine(BuildScatterPreview(manager));
    }

    private System.Collections.IEnumerator BuildScatterPreview(PropManager manager)
    {
        var ring = scatterVertices.ConvertAll(v => new Vector2(v.x - previewOffset.x, v.z - previewOffset.z)).ToArray();
        var region = new PropGeometry.Region { bounds = PropGeometry.Bounds(ring) };
        region.rings.Add(ring);
        var regions = new List<PropGeometry.Region> { region };
        int candidates = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        foreach (var rect in manager.PreviewRects(region.bounds))
        {
            var matrices = new List<Matrix4x4>();
            foreach (var point in PropGeometry.Scatter(rect, regions, density, seed))
            {
                if (++candidates > manager.CandidateBudget) yield break;
                var position = new Vector3(point.position.x, 0, point.position.y) + previewOffset;
                position.y = Utils.GetGroundHeight(position);
                if (manager.CanPreview(position, propType, point.scale))
                    matrices.Add(Matrix4x4.TRS(position, Quaternion.Euler(0,point.rotation,0), Vector3.one * point.scale));
                if ((candidates & 127) == 0 && timer.ElapsedMilliseconds > PerformanceConfig.FrameBudgetMs)
                { yield return null; timer.Restart(); }
            }
            foreach (var part in previewModel.normal)
                for (int start = 0; start < matrices.Count; start += 511)
                {
                    int count = Mathf.Min(511, matrices.Count - start);
                    var values = new Matrix4x4[count];
                    for (int i = 0; i < count; i++) values[i] = matrices[start+i] * part.local;
                    previewBatches.Add(new PreviewBatch { part = part, matrices = values });
                    if (timer.ElapsedMilliseconds > PerformanceConfig.FrameBudgetMs)
                    { yield return null; timer.Restart(); }
                }
        }
        previewWorker = null;
    }

    // =====================================================================
    // Cleanup
    // =====================================================================

    private void ClearHoverGhost()
    {
        middleDrag.Cancel();
        if (hoverGhost != null) { Destroy(hoverGhost); hoverGhost = null; }
        hoverValid = false;
    }

    private void ClearAll()
    {
        ClearHoverGhost();
        // Provisional placements in individual mode.
        foreach (var go in placedObjects) if (go != null) Destroy(go);
        placedObjects.Clear();
        placedInstances.Clear();

        // Area mode vertices and scatter ghosts.
        scatterVertices.Clear();
        ClearScatterGhosts();
        shapeFinalized = false;
    }

    /// <summary>
    /// Clears all placed instances, vertices, and provisional finalization when the UI reset button is pressed.
    /// </summary>
    public void ResetPlacement()
    {
        if (!active) return;
        ClearAll();
        if (mode == Mode.Instance) EmitInstances();
        else EmitShape();
        Debug.Log("PropPlacementManager: 配置状態をリセットしました");
    }

    private void ClearScatterGhosts()
    {
        if (previewWorker != null) StopCoroutine(previewWorker);
        previewWorker = null;
        previewBatches.Clear();
        previewModel?.Dispose(); previewModel = null;
    }

    // =====================================================================
    // IPC emission
    // =====================================================================

    /// <summary>Reports provisional placements of all types in placement order (array of {prop_type, x, y, z, rotation, scale}).</summary>
    private void EmitInstances()
    {
        if (IpcClient.Instance == null) return;
        var list = new List<Dictionary<string, object>>(placedInstances.Count);
        foreach (var inst in placedInstances)
        {
            list.Add(new Dictionary<string, object>
            {
                ["prop_type"] = inst.propType,
                ["x"] = inst.x,
                ["y"] = inst.y,
                ["z"] = inst.z,
                ["rotation"] = inst.rotation,
                ["scale"] = inst.scale,
            });
        }
        IpcClient.Instance.EmitEditPropUpdateInstances(list);
    }

    /// <summary>Reports scatter polygon vertices to the UI (array of [x, z]).</summary>
    private void EmitShape()
    {
        if (IpcClient.Instance == null) return;
        var polygon = new List<List<float>>(scatterVertices.Count);
        foreach (var v in scatterVertices)
        {
            polygon.Add(new List<float> { v.x, v.z });
        }
        IpcClient.Instance.EmitEditPropUpdateShape(polygon, shapeFinalized);
    }

    // =====================================================================
    // Overlay drawing (area mode polygon)
    // =====================================================================

    private void UpdateOverlay()
    {
        if (overlayRenderer == null) return;
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        overlayRenderer.BeginFrame();

        // Confirmed edges (connect vertices around the perimeter).
        int n = scatterVertices.Count;
        for (int i = 0; i < n - 1; i++)
        {
            overlayRenderer.AddSolidLine(scatterVertices[i], scatterVertices[i + 1], LineColor, LINE_THICKNESS);
        }
        // Draw the closing edge with 3 or more vertices.
        if (n >= 3)
        {
            overlayRenderer.AddSolidLine(scatterVertices[n - 1], scatterVertices[0], LineColor, LINE_THICKNESS);
        }

        // Preview line from the last vertex to the cursor.
        // Draw only before provisional finalization.
        if (!shapeFinalized && hoverValid && n > 0)
        {
            overlayRenderer.AddSolidLine(scatterVertices[n - 1], hoverPos, PreviewColor, LINE_THICKNESS);
        }

        // Vertex markers.
        for (int i = 0; i < n; i++)
        {
            overlayRenderer.AddSquare(scatterVertices[i], VERTEX_MARKER_HALF_SIZE, VertexColor);
        }

        overlayRenderer.EndFrame(cam);
    }

    private static Material CreatePreviewMaterial(Material source)
    {
        // Alpha alone does not enable URP Lit transparency, so configure render state on the copy as well.
        var material = new Material(source) { enableInstancing = true };
        material.SetFloat("_Surface", 1f);
        material.SetFloat("_ZWrite", 0f);
        material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.SetOverrideTag("RenderType", "Transparent");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        return material;
    }

    private void OnDestroy()
    {
        if (previewMaterial != null) Destroy(previewMaterial);
        ClearAll();
        if (overlayMeshObj != null) Destroy(overlayMeshObj);
    }
}
