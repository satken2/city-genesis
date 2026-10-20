using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UI;

/// <summary>
/// Singleton managing 2D/3D grid overlays and snapping.
///
/// Display modes:
///   - Off: hidden.
///   - Line: draw terrain-following lines in a 2D screen overlay.
///
/// Performance measures:
///   - LINE mode: pool GameObjects; do not Destroy them every frame.
///   - Rebuild only after camera movement of at least 0.5m or rotation of at least 1 degree.
///   - Share one Material instead of allocating it repeatedly.
/// </summary>
public class GridOverlay : MonoBehaviour
{
    public static GridOverlay Instance { get; private set; }

    // Grid visibility change event.
    public delegate void GridVisibilityChanged(bool isVisible);
    public static event GridVisibilityChanged OnGridVisibilityChanged;

    // Grid-mode change event and mode definitions.
    public enum GridDisplayMode { Off, Line }
    public delegate void GridModeChanged(GridDisplayMode mode);
    public static event GridModeChanged OnGridModeChanged;

    [Header("Grid Settings")]
    [SerializeField] private float gridSize = 5f;
    [SerializeField] private Material lineMaterial;
    /// <summary>Base color for the whole grid. Translucency keeps it visible without obstructing the view.</summary>
    [SerializeField] private Color gridColor = new Color(1f, 1f, 1f, 0.4f);
    /// <summary>Grid line width. Defaults to 3.0px for visibility.</summary>
    [SerializeField] private float lineThickness = 3f;
    /// <summary>Grid drawing radius in meters from the view center, balancing rendering cost and coverage.</summary>
    [SerializeField] private float gridRadius = 300f;
    /// <summary>Distance ratio (0.0 to 1.0) relative to gridRadius where fading starts.</summary>
    [SerializeField] private float fadeStartFactor = 0.5f;
    /// <summary>Distance ratio (0.0 to 1.0) relative to gridRadius where fading ends at full transparency.</summary>
    [SerializeField] private float fadeEndFactor = 1.0f;
    /// <summary>Segment length in meters for splitting long grid lines, allowing opacity to fade smoothly toward the edges.</summary>
    [SerializeField] private float segmentLength = 30f;

    /// <summary>
    /// Data for one grid line drawn on screen.
    /// Stores its transformed 2D position and opacity for fading based on distance from the center.
    /// </summary>
    private struct ScreenLineInfo
    {
        public Vector2 start;
        public Vector2 end;
        public float opacity;
    }

    // For LINE mode.
    private GameObject lineCanvasRoot;
    private Canvas lineCanvas;
    private RectTransform lineCanvasRect;
    /// <summary>Currently active, visible lines.</summary>
    private readonly List<GameObject> activeLines = new List<GameObject>();
    /// <summary>Inactive lines awaiting reuse.</summary>
    private readonly Queue<GameObject> pooledLines = new Queue<GameObject>();

    public GridDisplayMode Mode { get; private set; } = GridDisplayMode.Off;

    public float GridAngle { get; private set; } = 0f;
    public bool IsVisible => Mode != GridDisplayMode.Off;
    public Vector3 GridOrigin { get; private set; } = Vector3.zero;

    public void SetGridOrigin(Vector3 origin)
    {
        GridOrigin = origin;
        if (Mode == GridDisplayMode.Line)
        {
            RebuildOverlayLines();
        }
    }

    // For detecting camera changes.
    private Vector3 lastCameraPos;
    private Quaternion lastCameraRot;
    private const float CAMERA_POS_THRESHOLD = 0.5f;
    private const float CAMERA_ROT_THRESHOLD = 1.0f;

    // For detecting cursor-position changes.
    private Vector3 lastCursorPos;
    private const float CURSOR_POS_THRESHOLD = 0.5f;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnViewSetGrid += HandleViewSetGrid;
        }
    }

    private void OnDestroy()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnViewSetGrid -= HandleViewSetGrid;
        }
    }

    /// <summary>
    /// Handler for the UI's view:set_grid event.
    /// Apply the grid display mode, size, and angle,
    /// and automatically enable or disable grid snapping.
    /// </summary>
    private void HandleViewSetGrid(string mode, float size, float angle, float? originX, float? originZ)
    {
        GridDisplayMode targetMode = mode.ToLower() switch
        {
            "line" => GridDisplayMode.Line,
            _ => GridDisplayMode.Off,
        };

        SetMode(targetMode);

        if (size > 0f)
        {
            SetGridSize(size);
        }

        SetGridAngle(angle);

        if (originX.HasValue && originZ.HasValue)
        {
            SetGridOrigin(new Vector3(originX.Value, 0f, originZ.Value));
        }
        else
        {
            SetGridOrigin(Vector3.zero);
        }

        // Enable terrain-click snapping while the grid is visible.
        TerrainInputHandler.EnableGridSnapping = (targetMode != GridDisplayMode.Off);
    }

    public void SetGridVisible(bool visible)
    {
        SetMode(visible ? GridDisplayMode.Line : GridDisplayMode.Off);
    }

    /// <summary>
    /// Toggle the grid from the radial menu, switching visibility and snapping together;
    /// notify via view:grid_toggled so the grid settings panel stays in sync.
    /// Retain size, angle, and origin from the latest view:set_grid, or serialized defaults if none has arrived.
    /// </summary>
    public void ToggleVisibility()
    {
        bool newVisible = !IsVisible;
        SetMode(newVisible ? GridDisplayMode.Line : GridDisplayMode.Off);
        TerrainInputHandler.EnableGridSnapping = newVisible;
        IpcClient.Instance?.EmitViewGridToggled(newVisible);
    }

    public void SetMode(GridDisplayMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;

        switch (Mode)
        {
            case GridDisplayMode.Off:
                HideLineCanvas();
                break;
            case GridDisplayMode.Line:
                EnsureLineCanvas();
                RebuildOverlayLines();
                break;
        }

        OnGridVisibilityChanged?.Invoke(Mode != GridDisplayMode.Off);
        OnGridModeChanged?.Invoke(Mode);
    }

    // =====================================================================
    // Visible-cell lookup.
    // =====================================================================
    // Cell-index lookup was removed when rendering switched to a cell-independent radius-based approach.

    // =====================================================================
    // Lifecycle (camera-change detection).
    // =====================================================================

    private void Update()
    {
        if (Mode == GridDisplayMode.Off) return;

        Camera mainCamera = Camera.main;
        if (mainCamera == null) return;

        Vector3 currentPos = mainCamera.transform.position;
        Quaternion currentRot = mainCamera.transform.rotation;

        bool posChanged = Vector3.Distance(currentPos, lastCameraPos) > CAMERA_POS_THRESHOLD;
        bool rotChanged = Quaternion.Angle(currentRot, lastCameraRot) > CAMERA_ROT_THRESHOLD;

        if (Mode == GridDisplayMode.Line)
        {
            Vector3 currentCursorPos = InputHandler.Instance != null
                ? new Vector3(InputHandler.Instance.posX, 0f, InputHandler.Instance.posZ)
                : Vector3.zero;
            bool cursorChanged = Vector3.Distance(currentCursorPos, lastCursorPos) > CURSOR_POS_THRESHOLD;

            if (posChanged || rotChanged || cursorChanged)
            {
                RebuildOverlayLines();
                lastCameraPos = currentPos;
                lastCameraRot = currentRot;
                lastCursorPos = currentCursorPos;
            }
        }
    }

    // =====================================================================
    // LINE mode (2D overlay with pooling).
    // =====================================================================

    private void EnsureLineCanvas()
    {
        if (lineCanvasRoot != null) return;

        lineCanvasRoot = new GameObject("GridOverlayCanvas");
        DontDestroyOnLoad(lineCanvasRoot);
        lineCanvas = lineCanvasRoot.AddComponent<Canvas>();
        lineCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        lineCanvas.sortingOrder = 60;
        lineCanvasRect = lineCanvasRoot.GetComponent<RectTransform>();
        lineCanvasRoot.AddComponent<CanvasScaler>();
        lineCanvasRoot.AddComponent<GraphicRaycaster>();
    }

    private void HideLineCanvas()
    {
        if (lineCanvasRoot != null)
        {
            // Return all active lines to the pool.
            foreach (var go in activeLines)
            {
                if (go != null) ReturnLineToPool(go);
            }
            activeLines.Clear();
            lineCanvasRoot.SetActive(false);
        }
    }

    private void RebuildOverlayLines()
    {
        if (Camera.main == null) return;
        EnsureLineCanvas();
        lineCanvasRoot.SetActive(true);

        // Build the line definitions needed for this update.
        List<ScreenLineInfo> requiredLines = new List<ScreenLineInfo>();
        CollectRequiredLines(requiredLines);

        // First return all existing lines to the pool.
        foreach (var go in activeLines)
        {
            if (go != null) ReturnLineToPool(go);
        }
        activeLines.Clear();

        // Restore required lines from the pool or create them.
        foreach (var line in requiredLines)
        {
            GameObject go = GetOrCreateLine();
            UpdateLineTransform(go, line.start, line.end);

            // Multiply the grid's translucent base color by distance-based opacity from the center.
            Image img = go.GetComponent<Image>();
            if (img != null)
            {
                img.color = new Color(gridColor.r, gridColor.g, gridColor.b, gridColor.a * line.opacity);
            }

            go.SetActive(true);
            activeLines.Add(go);
        }
    }

    /// <summary>
    /// Calculate grid lines within a fixed radius of the view center (camera target) and collect them in screen coordinates.
    /// Render smoothly by distance from the view center, independently of cell boundaries.
    /// Split long grid lines into segmentLength intervals and calculate each segment's opacity
    /// from the distance between its midpoint and the view center, producing a smooth fade toward the edges.
    /// </summary>
    private void CollectRequiredLines(List<ScreenLineInfo> requiredLines)
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 cameraPos = cam.transform.position;
        // Grid center: the cursor/terrain intersection. Fall back to the view center if InputHandler is not initialized.
        Vector3 centerWorldPos;
        if (InputHandler.Instance != null)
            centerWorldPos = new Vector3(InputHandler.Instance.posX, 0f, InputHandler.Instance.posZ);
        else if (CameraCellChecker.Instance != null)
            centerWorldPos = CameraCellChecker.Instance.CurrentWorldPosition;
        else
            centerWorldPos = new Vector3(cameraPos.x, 0f, cameraPos.z);
        centerWorldPos.y = 0f; // Measure distances on the horizontal plane only to avoid distortion from height differences.

        // Center coordinates relative to the grid origin.
        Vector3 relativeCenter = centerWorldPos - GridOrigin;

        // Transform to local coordinates accounting for grid rotation.
        Quaternion rot = Quaternion.Euler(0, GridAngle, 0);
        Quaternion invRot = Quaternion.Inverse(rot);
        Vector3 localCenter = invRot * relativeCenter;

        // Drawing bounds in local coordinates.
        float minLocalX = localCenter.x - gridRadius;
        float maxLocalX = localCenter.x + gridRadius;
        float minLocalZ = localCenter.z - gridRadius;
        float maxLocalZ = localCenter.z + gridRadius;

        float dStart = gridRadius * fadeStartFactor;
        float dEnd = gridRadius * fadeEndFactor;

        // 1. Vertical lines (fixed X, extending along Z).
        int startIdxX = Mathf.CeilToInt(minLocalX / gridSize);
        int endIdxX = Mathf.FloorToInt(maxLocalX / gridSize);
        for (int i = startIdxX; i <= endIdxX; i++)
        {
            float lx = i * gridSize;
            // Draw Z-direction lines split into segmentLength intervals.
            for (float lz = minLocalZ; lz < maxLocalZ; lz += segmentLength)
            {
                float nextLz = Mathf.Min(lz + segmentLength, maxLocalZ);
                Vector3 localA = new Vector3(lx, 0f, lz);
                Vector3 localB = new Vector3(lx, 0f, nextLz);

                // Transform back to world coordinates, applying the origin offset.
                Vector3 worldA = rot * localA + GridOrigin;
                Vector3 worldB = rot * localB + GridOrigin;
                worldA.y = Utils.GetGroundHeight(worldA);
                worldB.y = Utils.GetGroundHeight(worldB);

                // Calculate horizontal distance from each segment midpoint to the center.
                Vector3 mid = (worldA + worldB) * 0.5f;
                mid.y = 0f;
                float dist = Vector3.Distance(mid, centerWorldPos);

                if (dist < dEnd)
                {
                    float opacity = 1.0f;
                    if (dist > dStart)
                    {
                        opacity = Mathf.Clamp01(1.0f - (dist - dStart) / (dEnd - dStart));
                    }
                    TryAddScreenLine(requiredLines, cam, worldA, worldB, opacity);
                }
            }
        }

        // 2. Horizontal lines (fixed Z, extending along X).
        int startIdxZ = Mathf.CeilToInt(minLocalZ / gridSize);
        int endIdxZ = Mathf.FloorToInt(maxLocalZ / gridSize);
        for (int i = startIdxZ; i <= endIdxZ; i++)
        {
            float lz = i * gridSize;
            // Draw X-direction lines split into segmentLength intervals.
            for (float lx = minLocalX; lx < maxLocalX; lx += segmentLength)
            {
                float nextLx = Mathf.Min(lx + segmentLength, maxLocalX);
                Vector3 localA = new Vector3(lx, 0f, lz);
                Vector3 localB = new Vector3(nextLx, 0f, lz);

                // Transform back to world coordinates, applying the origin offset.
                Vector3 worldA = rot * localA + GridOrigin;
                Vector3 worldB = rot * localB + GridOrigin;
                worldA.y = Utils.GetGroundHeight(worldA);
                worldB.y = Utils.GetGroundHeight(worldB);

                // Calculate horizontal distance from each segment midpoint to the center.
                Vector3 mid = (worldA + worldB) * 0.5f;
                mid.y = 0f;
                float dist = Vector3.Distance(mid, centerWorldPos);

                if (dist < dEnd)
                {
                    float opacity = 1.0f;
                    if (dist > dStart)
                    {
                        opacity = Mathf.Clamp01(1.0f - (dist - dStart) / (dEnd - dStart));
                    }
                    TryAddScreenLine(requiredLines, cam, worldA, worldB, opacity);
                }
            }
        }
    }

    /// <summary>
    /// Clip two 3D line endpoints against the camera's near plane, convert to screen coordinates, and append to the list.
    /// </summary>
    private void TryAddScreenLine(List<ScreenLineInfo> list, Camera cam, Vector3 worldA, Vector3 worldB, float opacity)
    {
        Vector3 ca = worldA;
        Vector3 cb = worldB;
        if (!ClipLineToNearPlane(ref ca, ref cb, cam)) return;

        Vector3 spA = cam.WorldToScreenPoint(ca);
        Vector3 spB = cam.WorldToScreenPoint(cb);
        list.Add(new ScreenLineInfo
        {
            start = new Vector2(spA.x, spA.y),
            end = new Vector2(spB.x, spB.y),
            opacity = opacity
        });
    }

    private GameObject GetOrCreateLine()
    {
        if (pooledLines.Count > 0)
        {
            return pooledLines.Dequeue();
        }

        GameObject go = new GameObject("GridLine");
        go.transform.SetParent(lineCanvasRect, false);

        var image = go.AddComponent<Image>();
        image.color = gridColor;
        // Grid lines are purely visual and must not block the pointer.
        // Leaving raycastTarget at its default true makes EventSystem.IsPointerOverGameObject()
        // true while the cursor is over a grid line, causing InputHandler to discard clicks/hovers
        // as UI input. Snapping targets line intersections, so most clicks land on lines,
        // preventing placement of the second point. Always disable raycasts for these visual-only lines.
        image.raycastTarget = false;

        RectTransform rt = image.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);

        return go;
    }

    private void ReturnLineToPool(GameObject go)
    {
        if (go == null) return;
        go.SetActive(false);
        pooledLines.Enqueue(go);
    }

    private void UpdateLineTransform(GameObject go, Vector2 start, Vector2 end)
    {
        RectTransform rt = go.GetComponent<RectTransform>();
        if (rt == null) return;

        Vector2 dir = end - start;
        float length = dir.magnitude;
        if (length < 0.01f)
        {
            rt.sizeDelta = Vector2.zero;
            return;
        }

        float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;

        rt.sizeDelta = new Vector2(length, lineThickness);
        rt.anchoredPosition = start + dir * 0.5f;
        rt.localRotation = Quaternion.Euler(0, 0, angle);
    }

    private bool ClipLineToNearPlane(ref Vector3 a, ref Vector3 b, Camera cam)
    {
        const float epsilon = 0.001f;
        Vector3 planePoint = cam.transform.position + cam.transform.forward * (cam.nearClipPlane + epsilon);
        Plane nearPlane = new Plane(cam.transform.forward, planePoint);

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

    // =====================================================================
    // Public API.
    // =====================================================================

    public void SetGridAngle(float angle)
    {
        GridAngle = angle;
        // Changing the angle requires rebuilding the lines.
        if (Mode == GridDisplayMode.Line)
        {
            RebuildOverlayLines();
        }
    }

    public void SetGridSize(float size)
    {
        gridSize = Mathf.Max(0.1f, size);
        if (Mode == GridDisplayMode.Line)
        {
            RebuildOverlayLines();
        }
    }

    public Vector3 SnapPosition(Vector3 worldPos)
    {
        Vector3 relativePos = worldPos - GridOrigin;
        Quaternion inv = Quaternion.Inverse(Quaternion.Euler(0, GridAngle, 0));
        Vector3 local = inv * relativePos;
        float x = Mathf.Round(local.x / gridSize) * gridSize;
        float z = Mathf.Round(local.z / gridSize) * gridSize;

        // Convert snapped local XZ coordinates to world XZ coordinates to obtain ground height.
        Vector3 localFlat = new Vector3(x, 0f, z);
        Vector3 worldFlat = Quaternion.Euler(0, GridAngle, 0) * localFlat + GridOrigin;
        float groundHeight = Utils.GetGroundHeight(worldFlat);

        // Use ground height at the snapped world position.
        return new Vector3(worldFlat.x, groundHeight, worldFlat.z);
    }
}
