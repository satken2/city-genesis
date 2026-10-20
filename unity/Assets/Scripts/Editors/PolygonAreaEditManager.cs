using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Progress phases for arbitrary polygon editing.
/// Placing: append vertices with left clicks (polygon is not yet closed).
/// Editing: after right-clicking to finalize the shape; adjust by dragging vertices, clicking edges to insert,
///          or right-clicking to remove vertices.
/// </summary>
public enum PolygonEditPhase { Inactive, Placing, Editing }

/// <summary>
/// Shared base for creating/editing arbitrary terrain polygons.
///
/// Combines a Placing phase like RoadPlanManager (left click to place points)
/// with an Editing phase (drag vertices, insert on edges, right click to delete)
/// to build closed polygons. All polygon construction mechanics—vertex editing, overlay rendering,
/// hit testing, and dragging—are centralized here.
/// Inherited by new natural resource area creation (<see cref="ResourceAreaEditManager"/>, starts in Placing)
/// and existing parcel shape editing (<see cref="ParcelShapeEditManager"/>, loads a saved polygon
/// and starts in Editing).
///
/// Derived classes replace only the IPC event that reports the completed polygon
/// to the UI (<see cref="EmitShape"/>), overlay colors/canvas name, and
/// domain-specific start/end handling (override <see cref="FinishEditMode"/>).
/// In both cases, the UI (Tauri) directly confirms changes with backend POST/PATCH requests,
/// so this class only constructs polygon geometry and reports incremental updates to the UI.
///
/// Areas are not aligned to paths or nodes, so place and drag vertices directly at the terrain cursor
/// without shared node/path snapping. TerrainInputHandler also skips snapping for clicks and hover.
/// </summary>
public abstract class PolygonAreaEditManager : MonoBehaviour
{
    [Header("References")]
    public Camera gameCamera;

    [Header("Settings")]
    [SerializeField] protected float pointCircleRadius = 8f;
    [SerializeField] protected float solidLineThickness = 1.5f;
    [SerializeField] protected float hoverLineThickness = 3f;

    protected PolygonEditPhase phase = PolygonEditPhase.Inactive;
    protected List<Vector3> vertices = new List<Vector3>();

    // Selection/hover state (Editing phase only)
    protected int selectedVertexIndex = -1;
    protected int hoveredEdgeIndex = -1;

    // Drag state (Editing phase only)
    protected bool isDragging = false;
    protected int dragVertexIndex = -1;

    // Overlay rendering. ParcelShapeEditOverlayRenderer is a generic component specializing in
    // 2D vertex/edge drawing without parcel-specific state, so reuse it unchanged.
    protected ParcelShapeEditOverlayRenderer overlayRenderer;
    protected GameObject overlayCanvasRoot;
    protected Canvas overlayCanvas;
    protected RectTransform overlayCanvasRect;

    // Pointer marker position during Placing. Do not draw while hover is interrupted (e.g. over UI), using hasHover=false.
    private Vector3 hoverWorldPosition;
    private bool hasHover;

    public bool IsEditMode => phase != PolygonEditPhase.Inactive;
    public bool IsEditingPhase => phase == PolygonEditPhase.Editing;

    // =====================================================================
    // Elements replaced by derived classes
    // =====================================================================

    /// <summary>
    /// Reports the current polygon to the UI via IPC. Derived classes use their domain's event name.
    /// polygon is a sequence of [x, z] vertices (world XZ).
    /// finalized indicates whether right click has provisionally finalized the shape (Editing phase).
    /// </summary>
    protected abstract void EmitShape(List<List<float>> polygon, bool finalized);

    /// <summary>GameObject name for the overlay canvas (debugging and duplicate prevention).</summary>
    protected abstract string OverlayCanvasName { get; }

    /// <summary>Polygon edge color. Use a domain-specific color distinct from roads and other areas.</summary>
    protected abstract Color EdgeColor { get; }

    /// <summary>Unselected vertex color.</summary>
    protected abstract Color VertexColor { get; }

    /// <summary>Selected vertex color (green by default).</summary>
    protected virtual Color SelectedVertexColor => new Color(0.2f, 1f, 0.4f);

    // =====================================================================
    // Lifecycle
    // =====================================================================

    protected virtual void Start()
    {
        EnsureOverlayCanvas();
    }

    protected virtual void Update()
    {
        if (!IsEditMode) return;

        if (phase == PolygonEditPhase.Editing)
        {
            UpdateHoverState();
        }
        UpdateOverlay();

        if (CancelOnEscape && InputUtil.WorldEscapeWasPressed)
        {
            CancelEditMode();
        }
    }

    /// <summary>
    /// Whether Esc means cancel editing and return to normal input mode.
    /// Return false when a higher-level manager owns Esc's meaning, as in arbitrary polygon parcel creation
    /// where Esc resets selection while retaining creation mode.
    /// If both handle it while true, this base would switch back to
    /// `InputModePreset.ALL_NORMAL` immediately after the higher manager rebuilds selection,
    /// leaving edit mode active but all clicks ineffective.
    /// </summary>
    protected virtual bool CancelOnEscape => true;

    // =====================================================================
    // Canvas / Overlay setup
    // =====================================================================

    protected void EnsureOverlayCanvas()
    {
        if (overlayCanvasRoot != null) return;

        overlayCanvasRoot = new GameObject(OverlayCanvasName);
        overlayCanvas = overlayCanvasRoot.AddComponent<Canvas>();
        overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        overlayCanvas.sortingOrder = 52; // Draw in front of normal UI, as with ParcelShapeEdit.
        overlayCanvasRect = overlayCanvasRoot.GetComponent<RectTransform>();
        overlayCanvasRoot.AddComponent<CanvasScaler>();

        overlayRenderer = overlayCanvasRoot.AddComponent<ParcelShapeEditOverlayRenderer>();
        overlayRenderer.Initialize(overlayCanvasRect);
    }

    // =====================================================================
    // Public API: mode transitions
    // =====================================================================

    /// <summary>
    /// Initializes new arbitrary polygon creation with an empty polygon in Placing.
    /// </summary>
    public void InitializeCreateMode()
    {
        Debug.Log($"{GetType().Name}: 任意ポリゴン編集モード開始(新規作成)");
        EnsureOverlayCanvas();
        ResetEditState(PolygonEditPhase.Placing);
        EmitShapeUpdate();
    }

    /// <summary>
    /// Loads an existing polygon and starts in Editing without vertex placement (Placing).
    /// Used for editing saved shapes, such as parcel geometry.
    /// </summary>
    protected void InitializeEditingWithPolygon(IEnumerable<Vector3> polygon)
    {
        EnsureOverlayCanvas();
        ResetEditState(PolygonEditPhase.Editing);
        vertices.AddRange(polygon);
        EmitShapeUpdate();
    }

    /// <summary>
    /// Ends edit mode and clears state.
    /// Derived classes override this to add domain-specific cleanup (e.g. restoring hidden original objects).
    /// </summary>
    public virtual void FinishEditMode()
    {
        if (overlayRenderer != null)
        {
            overlayRenderer.ClearMesh();
        }
        ResetEditState(PolygonEditPhase.Inactive);
    }

    /// <summary>Clears vertices and selection/hover/drag state, then enters the specified phase.</summary>
    private void ResetEditState(PolygonEditPhase nextPhase)
    {
        phase = nextPhase;
        vertices.Clear();
        selectedVertexIndex = -1;
        hoveredEdgeIndex = -1;
        isDragging = false;
        dragVertexIndex = -1;
        hasHover = false;
    }

    /// <summary>
    /// Cancels editing and returns to normal mode.
    /// </summary>
    public void CancelEditMode()
    {
        if (!IsEditMode) return;
        Debug.Log($"{GetType().Name}: 編集キャンセル");
        FinishEditMode();

        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.ALL_NORMAL);
        }

        var viewCoordinator = FindAnyObjectByType<IpcViewCoordinator>();
        if (viewCoordinator != null)
        {
            viewCoordinator.ApplyAllLayerModes();
        }
    }

    // =====================================================================
    // Input handlers (called by TerrainInputHandler)
    // =====================================================================

    /// <summary>
    /// Terrain left click.
    /// Placing: append a vertex.
    /// Editing: start dragging a vertex or insert a vertex on an edge.
    /// </summary>
    public void OnTerrainLeftClick(Vector3 worldPos)
    {
        if (!IsEditMode) return;

        if (phase == PolygonEditPhase.Placing)
        {
            vertices.Add(worldPos);
            EmitShapeUpdate();
            Debug.Log($"{GetType().Name}: 頂点配置 {worldPos} (total: {vertices.Count})");
            return;
        }

        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        Vector2 mouseScreen = InputUtil.MousePosition;
        var hit = TestHit(mouseScreen, cam);

        if (hit.type == HitTargetType.Point)
        {
            selectedVertexIndex = hit.index;
            isDragging = true;
            dragVertexIndex = hit.index;
            Debug.Log($"{GetType().Name}: 頂点{hit.index}を選択してドラッグ開始");
        }
        else if (hit.type == HitTargetType.Edge)
        {
            InsertVertexOnEdge(hit.index, worldPos);
            Debug.Log($"{GetType().Name}: 辺{hit.index}に頂点を挿入");
        }
        else
        {
            selectedVertexIndex = -1;
        }
    }

    /// <summary>
    /// Terrain right click.
    /// Placing: finalize the polygon and enter Editing if there are at least 3 vertices.
    /// Editing: delete a vertex (retain at least 3).
    /// </summary>
    public void OnTerrainRightClick(Vector3 worldPos)
    {
        if (!IsEditMode) return;

        if (phase == PolygonEditPhase.Placing)
        {
            if (vertices.Count >= 3)
            {
                phase = PolygonEditPhase.Editing;
                EmitShapeUpdate();
                Debug.Log($"{GetType().Name}: 形状確定、編集フェーズへ移行 (頂点数: {vertices.Count})");
            }
            return;
        }

        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        Vector2 mouseScreen = InputUtil.MousePosition;
        var hit = TestHit(mouseScreen, cam);

        if (hit.type == HitTargetType.Point && vertices.Count > 3)
        {
            RemoveVertex(hit.index);
        }
    }

    /// <summary>
    /// Terrain hover. Saves the Placing marker position.
    /// Editing edge hover is updated every frame by <see cref="UpdateHoverState"/>.
    /// </summary>
    public void OnTerrainHover(Vector3 worldPos)
    {
        hoverWorldPosition = worldPos;
        hasHover = true;
    }

    /// <summary>
    /// Pointer left terrain (e.g. moved over UI). Hide the Placing marker.
    /// </summary>
    public void OnTerrainHoverEnd()
    {
        hasHover = false;
    }

    /// <summary>
    /// Drag start (called by InputHandler). Valid only in Editing.
    /// </summary>
    public void StartDrag(Vector2 screenPos)
    {
        if (phase != PolygonEditPhase.Editing) return;

        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        var hit = TestHit(screenPos, cam);
        if (hit.type == HitTargetType.Point)
        {
            selectedVertexIndex = hit.index;
            isDragging = true;
            dragVertexIndex = hit.index;
            Debug.Log($"{GetType().Name}: 頂点{hit.index}を選択してドラッグ開始");
        }
    }

    /// <summary>
    /// Tests whether screen coordinates are over an edited vertex (Editing only).
    /// Used to resolve input priority conflicts with CameraController.
    /// </summary>
    public bool IsMouseOverVertex(Vector2 screenPos, Camera cam)
    {
        if (phase != PolygonEditPhase.Editing) return false;
        if (cam == null) return false;

        var hit = TestHit(screenPos, cam);
        return hit.type == HitTargetType.Point;
    }

    /// <summary>
    /// Drag update (called by InputHandler). Valid only in Editing.
    /// </summary>
    public void ProcessMouseDrag(Vector3 worldPos)
    {
        if (phase != PolygonEditPhase.Editing) return;

        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        if (!isDragging)
        {
            Vector2 mouseScreen = InputUtil.MousePosition;
            var hit = TestHit(mouseScreen, cam);

            if (hit.type == HitTargetType.Point)
            {
                selectedVertexIndex = hit.index;
                isDragging = true;
                dragVertexIndex = hit.index;
                Debug.Log($"{GetType().Name}: 頂点{hit.index}を選択してドラッグ開始");
            }
            else
            {
                return;
            }
        }

        // Use the terrain intersection XZ from input; unprojecting at fixed depth deviates from the ground at oblique angles.
        Vector3 newWorldPos = worldPos;
        newWorldPos.y = Utils.GetGroundHeight(newWorldPos);

        if (dragVertexIndex >= 0 && dragVertexIndex < vertices.Count)
        {
            vertices[dragVertexIndex] = newWorldPos;
            EmitShapeUpdate();
        }
    }

    /// <summary>
    /// Drag end (called by InputHandler).
    /// </summary>
    public void ProcessMouseDragEnd(Vector3 worldPos)
    {
        if (isDragging && phase == PolygonEditPhase.Editing)
        {
            isDragging = false;
            dragVertexIndex = -1;
            EmitShapeUpdate();
        }
    }

    // =====================================================================
    // Vertex operations (Editing phase)
    // =====================================================================

    /// <summary>
    /// Inserts a vertex on an edge.
    /// </summary>
    protected void InsertVertexOnEdge(int edgeIndex, Vector3 worldPos)
    {
        if (edgeIndex < 0 || edgeIndex >= vertices.Count) return;

        worldPos.y = Utils.GetGroundHeight(worldPos);
        int insertIndex = edgeIndex + 1;
        vertices.Insert(insertIndex, worldPos);

        if (selectedVertexIndex >= insertIndex) selectedVertexIndex++;

        EmitShapeUpdate();
        Debug.Log($"{GetType().Name}: 辺{edgeIndex}に頂点挿入 (total: {vertices.Count})");
    }

    /// <summary>
    /// Deletes a vertex, always retaining at least 3.
    /// </summary>
    protected void RemoveVertex(int index)
    {
        if (vertices.Count <= 3) return;
        if (index < 0 || index >= vertices.Count) return;

        vertices.RemoveAt(index);

        if (selectedVertexIndex == index) selectedVertexIndex = -1;
        else if (selectedVertexIndex > index) selectedVertexIndex--;

        EmitShapeUpdate();
        Debug.Log($"{GetType().Name}: 頂点{index}を削除 (残り: {vertices.Count})");
    }

    // =====================================================================
    // Hit testing (meaningful only in Editing; always returns None during Placing)
    // =====================================================================

    protected enum HitTargetType { None, Point, Edge }

    protected struct HitResult
    {
        public HitTargetType type;
        public int index;
    }

    /// <summary>
    /// Hit-tests in screen coordinates.
    /// Prioritizes vertices (circles), then edges (segments).
    /// </summary>
    protected HitResult TestHit(Vector2 mouseScreen, Camera cam)
    {
        if (phase != PolygonEditPhase.Editing)
        {
            return new HitResult { type = HitTargetType.None, index = -1 };
        }

        // Vertex hits (priority)
        float pointHitRadius = pointCircleRadius + 4f;
        for (int i = 0; i < vertices.Count; i++)
        {
            Vector3 screen = cam.WorldToScreenPoint(vertices[i]);
            if (screen.z < 0) continue;
            if (Vector2.Distance(mouseScreen, screen) <= pointHitRadius)
            {
                return new HitResult { type = HitTargetType.Point, index = i };
            }
        }

        // Edge hits
        float edgeHitThreshold = 8f;
        for (int i = 0; i < vertices.Count; i++)
        {
            int next = (i + 1) % vertices.Count;
            Vector3 s1 = cam.WorldToScreenPoint(vertices[i]);
            Vector3 s2 = cam.WorldToScreenPoint(vertices[next]);
            if (s1.z < 0 && s2.z < 0) continue;

            float dist = PolylineUtils.DistanceToSegment(mouseScreen, s1, s2);
            if (dist <= edgeHitThreshold)
            {
                return new HitResult { type = HitTargetType.Edge, index = i };
            }
        }

        return new HitResult { type = HitTargetType.None, index = -1 };
    }

    // =====================================================================
    // Hover state update (Editing phase)
    // =====================================================================

    protected void UpdateHoverState()
    {
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        Vector2 mouseScreen = InputUtil.MousePosition;
        var hit = TestHit(mouseScreen, cam);
        hoveredEdgeIndex = (hit.type == HitTargetType.Edge) ? hit.index : -1;
    }

    // =====================================================================
    // 2D overlay rendering
    // =====================================================================

    protected void UpdateOverlay()
    {
        if (overlayRenderer == null) return;

        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        overlayRenderer.BeginFrame();

        DrawEdges(cam);
        DrawVertices(cam);

        if (phase == PolygonEditPhase.Placing)
        {
            DrawPlacingPreviewLine(cam);
            DrawPlacingMarker(cam);
        }

        if (phase == PolygonEditPhase.Editing && hoveredEdgeIndex >= 0)
        {
            DrawHoverHighlight(cam);
        }

        overlayRenderer.EndFrame();
    }

    /// <summary>
    /// Draws polygon edges.
    /// During Placing, draws an open polyline (do not connect the last vertex to the first).
    /// </summary>
    protected void DrawEdges(Camera cam)
    {
        Color edgeColor = EdgeColor;

        int segmentCount = phase == PolygonEditPhase.Placing ? vertices.Count - 1 : vertices.Count;
        for (int i = 0; i < segmentCount; i++)
        {
            int next = (i + 1) % vertices.Count;
            Vector3 s1 = cam.WorldToScreenPoint(vertices[i]);
            Vector3 s2 = cam.WorldToScreenPoint(vertices[next]);

            if (s1.z < 0 && s2.z < 0) continue;

            overlayRenderer.AddSolidLine(s1.x, s1.y, s2.x, s2.y, edgeColor, solidLineThickness);
        }
    }

    /// <summary>
    /// Draws a preview line from the last vertex to the mouse during Placing.
    /// </summary>
    protected void DrawPlacingPreviewLine(Camera cam)
    {
        if (vertices.Count == 0) return;

        Vector3 lastScreen = cam.WorldToScreenPoint(vertices[vertices.Count - 1]);
        if (lastScreen.z < 0) return;

        Vector2 mouseScreen = InputUtil.MousePosition;
        Color baseColor = EdgeColor;
        Color previewColor = new Color(baseColor.r, baseColor.g, baseColor.b, 0.5f);
        overlayRenderer.AddSolidLine(lastScreen.x, lastScreen.y, mouseScreen.x, mouseScreen.y, previewColor, solidLineThickness);
    }

    /// <summary>
    /// Draws a marker circle where the next left click will place a vertex during Placing.
    /// Projects the saved position onto terrain using the same screen-position calculation as vertices (<see cref="DrawVertices"/>),
    /// so the vertex stays at the marker after clicking. Draw slightly larger than vertices to distinguish it.
    /// </summary>
    protected void DrawPlacingMarker(Camera cam)
    {
        if (!hasHover) return;

        Vector3 markerPos = hoverWorldPosition;
        markerPos.y = Utils.GetGroundHeight(markerPos);

        Vector3 screen = cam.WorldToScreenPoint(markerPos);
        if (screen.z < 0) return;

        overlayRenderer.AddCircle(screen.x, screen.y, pointCircleRadius * 1.2f, VertexColor);
    }

    /// <summary>
    /// Draws vertices as circles.
    /// Uses the selection color for selected vertices and the normal color for others.
    /// </summary>
    protected void DrawVertices(Camera cam)
    {
        for (int i = 0; i < vertices.Count; i++)
        {
            Vector3 screen = cam.WorldToScreenPoint(vertices[i]);
            if (screen.z < 0) continue;

            Color c = (i == selectedVertexIndex) ? SelectedVertexColor : VertexColor;
            overlayRenderer.AddCircle(screen.x, screen.y, pointCircleRadius, c);
        }
    }

    /// <summary>
    /// Highlights the hovered edge with a thick yellow line.
    /// </summary>
    protected void DrawHoverHighlight(Camera cam)
    {
        if (hoveredEdgeIndex < 0 || hoveredEdgeIndex >= vertices.Count) return;

        int next = (hoveredEdgeIndex + 1) % vertices.Count;
        Vector2 s1 = cam.WorldToScreenPoint(vertices[hoveredEdgeIndex]);
        Vector2 s2 = cam.WorldToScreenPoint(vertices[next]);

        Color highlightColor = new Color(1f, 1f, 0.3f, 0.8f);
        overlayRenderer.AddSolidLine(s1.x, s1.y, s2.x, s2.y, highlightColor, hoverLineThickness);
    }

    // =====================================================================
    // IPC
    // =====================================================================

    /// <summary>
    /// Converts the current polygon to [x, z] vertices and reports it through the derived <see cref="EmitShape"/>.
    /// </summary>
    protected void EmitShapeUpdate()
    {
        var polygon = new List<List<float>>();
        foreach (var v in vertices)
        {
            polygon.Add(new List<float> { v.x, v.z });
        }

        EmitShape(polygon, phase == PolygonEditPhase.Editing);
    }
}
