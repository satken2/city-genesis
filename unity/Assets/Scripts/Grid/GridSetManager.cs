using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Singleton managing grid-angle selection through two clicks in the 3D view.
/// Before the first click, display a yellow marker at the snapped position;
/// afterward, display the first point as a yellow sphere and the hovered second point as a blue sphere,
/// previewing grid rotation in real time, exactly following the mouse (clockwise from Z).
/// </summary>
public class GridSetManager : MonoBehaviour
{
    public static GridSetManager Instance { get; private set; }

    private Vector3? firstPoint = null;
    private GameObject firstMarker = null;
    private GameObject hoverMarker = null;
    private GameObject pinkLine = null;
    private LineRenderer lineRenderer = null;
    
    private float originalAngle = 0f;
    private bool isSetting = false;

    // Saved snapping flags during angle selection. The baseline's two points should snap only to nodes,
    // so save snapping state on entry, enable only node snapping, and restore it on exit.
    // Unity's TerrainInputHandler static flags are the source of truth for snapping.
    private bool hasSnapshot = false;
    private bool savedGridSnap;
    private bool savedNodeSnap;
    private bool savedPathSnap;
    private bool savedRightAngleSnap;
    private bool savedStraightLineSnap;

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

    /// <summary>
    /// Start grid-angle selection mode.
    /// </summary>
    public void StartGridSetting()
    {
        isSetting = true;
        firstPoint = null;

        if (GridOverlay.Instance != null)
        {
            originalAngle = GridOverlay.Instance.GridAngle;
            // Force the grid to be visible.
            GridOverlay.Instance.SetGridVisible(true);
            GridOverlay.Instance.SetGridOrigin(Vector3.zero);
        }

        // Save snapping state and enable only node snapping for selecting the two baseline points.
        if (!hasSnapshot)
        {
            savedGridSnap = TerrainInputHandler.EnableGridSnapping;
            savedNodeSnap = TerrainInputHandler.EnableNodeSnapping;
            savedPathSnap = TerrainInputHandler.EnablePathSnapping;
            savedRightAngleSnap = TerrainInputHandler.EnableRightAngleSnapping;
            savedStraightLineSnap = TerrainInputHandler.EnableStraightLineSnapping;
            hasSnapshot = true;
        }
        TerrainInputHandler.EnableGridSnapping = false;
        TerrainInputHandler.EnableNodeSnapping = true;
        TerrainInputHandler.EnablePathSnapping = false;
        TerrainInputHandler.EnableRightAngleSnapping = false;
        TerrainInputHandler.EnableStraightLineSnapping = false;

        ClearMarkers();
        Debug.Log("GridSetManager: グリッド設定モードを開始しました");
    }

    /// <summary>
    /// Cancel grid-angle selection and restore the original angle.
    /// </summary>
    public void CancelGridSetting()
    {
        if (!isSetting) return;

        if (GridOverlay.Instance != null)
        {
            GridOverlay.Instance.SetGridAngle(originalAngle);
            GridOverlay.Instance.SetGridOrigin(Vector3.zero);
        }
        
        ResetState();
        Debug.Log("GridSetManager: グリッド設定モードをキャンセルしました");
    }

    /// <summary>
    /// Reset state and destroy markers.
    /// Both confirmation and cancellation call this, so restore snapping flags here as well.
    /// </summary>
    private void ResetState()
    {
        isSetting = false;
        firstPoint = null;
        ClearMarkers();

        if (hasSnapshot)
        {
            TerrainInputHandler.EnableGridSnapping = savedGridSnap;
            TerrainInputHandler.EnableNodeSnapping = savedNodeSnap;
            TerrainInputHandler.EnablePathSnapping = savedPathSnap;
            TerrainInputHandler.EnableRightAngleSnapping = savedRightAngleSnap;
            TerrainInputHandler.EnableStraightLineSnapping = savedStraightLineSnap;
            hasSnapshot = false;
        }
    }

    /// <summary>
    /// Handle a terrain click, including after node snapping.
    /// </summary>
    public void OnTerrainClicked(Vector3 position)
    {
        if (!isSetting) return;

        if (firstPoint == null)
        {
            // Confirm the first point.
            firstPoint = position;
            firstMarker = CreateMarker(position, Color.yellow);
            if (GridOverlay.Instance != null)
            {
                GridOverlay.Instance.SetGridOrigin(position);
            }
            CreatePinkLine(position);
            Debug.Log($"GridSetManager: 1点目を配置しました: {position}");
        }
        else
        {
            // Confirm the second point.
            Vector3 pos1 = firstPoint.Value;
            Vector3 pos2 = position;

            if (Vector3.Distance(pos1, pos2) < 0.1f)
            {
                Debug.LogWarning("GridSetManager: 1点目とほぼ同じ位置がクリックされました。無視します。");
                return;
            }

            // Calculate the clockwise angle from Z.
            float angle = CalculateAngle(pos1, pos2);

            Debug.Log($"GridSetManager: 2点選択完了。算出角度: {angle}度");

            // Confirm the grid angle.
            if (GridOverlay.Instance != null)
            {
                GridOverlay.Instance.SetGridAngle(angle);
                GridOverlay.Instance.SetGridOrigin(pos1);
            }

            // Notify the UI over IPC with the angle and origin coordinates.
            if (IpcClient.Instance != null)
            {
                IpcClient.Instance.EmitGridAngleChanged(angle, pos1.x, pos1.z);
            }

            ResetState();
        }
    }

    /// <summary>
    /// Preview the angle in real time while hovering over terrain.
    /// </summary>
    public void OnTerrainHover(Vector3 position)
    {
        if (!isSetting) return;

        if (firstPoint != null)
        {
            Vector3 pos1 = firstPoint.Value;
            Vector3 pos2 = position;

            if (Vector3.Distance(pos1, pos2) >= 0.1f)
            {
                // Calculate the clockwise angle from Z and rotate the grid in real time.
                float angle = CalculateAngle(pos1, pos2);
                if (GridOverlay.Instance != null)
                {
                    GridOverlay.Instance.SetGridAngle(angle);
                }
            }

            // Update the pink line.
            UpdatePinkLine(pos1, pos2);
        }

        // Update the hover marker, changing color depending on whether the first point is set.
        Color markerColor = (firstPoint == null) ? Color.yellow : Color.cyan;

        if (hoverMarker == null)
        {
            hoverMarker = CreateMarker(position, markerColor);
        }
        else
        {
            hoverMarker.transform.position = position;
            UpdateMarkerColor(hoverMarker, markerColor);
            hoverMarker.SetActive(true);
        }
    }

    /// <summary>
    /// Handle the mouse leaving the terrain (hover end).
    /// </summary>
    public void OnTerrainHoverEnd()
    {
        if (hoverMarker != null)
        {
            hoverMarker.SetActive(false);
        }
    }

    /// <summary>
    /// Calculate the XZ angle between two points using Unity's Y-rotation convention (positive Z = 0 degrees, clockwise).
    /// </summary>
    private float CalculateAngle(Vector3 pos1, Vector3 pos2)
    {
        Vector3 diff = pos2 - pos1;
        float angle = Mathf.Atan2(diff.x, diff.z) * Mathf.Rad2Deg;
        if (angle < 0)
        {
            angle += 360f;
        }
        return angle;
    }

    /// <summary>
    /// Create a 3D marker and remove its collider so it cannot interfere with raycasts.
    /// </summary>
    private GameObject CreateMarker(Vector3 position, Color color)
    {
        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        
        // Remove the collider immediately to prevent raycasts hitting the marker itself.
        Collider col = marker.GetComponent<Collider>();
        if (col != null)
        {
            Destroy(col);
        }

        marker.transform.position = position;
        marker.transform.localScale = Vector3.one * 1.5f;

        Renderer renderer = marker.GetComponent<Renderer>();
        if (renderer != null)
        {
            Material mat = Resources.Load<Material>("MarkerMaterial");
            if (mat != null)
            {
                renderer.material = Instantiate(mat);
            }
            else
            {
                renderer.material = new Material(Shader.Find("Standard"));
            }
            renderer.material.color = color;
            renderer.material.EnableKeyword("_EMISSION");
            renderer.material.SetColor("_EmissionColor", color * 0.5f);
        }

        return marker;
    }

    /// <summary>
    /// Update marker color.
    /// </summary>
    private void UpdateMarkerColor(GameObject marker, Color color)
    {
        Renderer renderer = marker.GetComponent<Renderer>();
        if (renderer != null && renderer.material != null)
        {
            renderer.material.color = color;
            renderer.material.SetColor("_EmissionColor", color * 0.5f);
        }
    }

    /// <summary>
    /// Destroy all generated markers.
    /// </summary>
    private void ClearMarkers()
    {
        if (firstMarker != null)
        {
            Destroy(firstMarker);
            firstMarker = null;
        }
        if (hoverMarker != null)
        {
            Destroy(hoverMarker);
            hoverMarker = null;
        }
        if (pinkLine != null)
        {
            Destroy(pinkLine);
            pinkLine = null;
            lineRenderer = null;
        }
    }

    /// <summary>
    /// Create a pink LineRenderer connecting the first and second points.
    /// </summary>
    private void CreatePinkLine(Vector3 startPos)
    {
        if (pinkLine != null)
        {
            Destroy(pinkLine);
        }

        pinkLine = new GameObject("GridSetPinkLine");
        lineRenderer = pinkLine.AddComponent<LineRenderer>();

        // Find a shader with fallbacks for different build environments.
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("UI/Default");
        if (shader == null) shader = Shader.Find("Standard");

        Material lineMat = new Material(shader);
        Color pinkColor = new Color(1f, 0.08f, 0.58f, 0.8f); // Vivid pink.
        lineMat.color = pinkColor;
        
        lineRenderer.material = lineMat;
        lineRenderer.startColor = pinkColor;
        lineRenderer.endColor = pinkColor;
        
        // Line width, slightly thickened for visibility.
        lineRenderer.startWidth = 0.5f;
        lineRenderer.endWidth = 0.5f;
        
        lineRenderer.positionCount = 2;
        lineRenderer.useWorldSpace = true;
        
        Vector3 offsetPos = startPos + Vector3.up * 0.2f;
        lineRenderer.SetPosition(0, offsetPos);
        lineRenderer.SetPosition(1, offsetPos);
    }

    /// <summary>
    /// Update the LineRenderer endpoint.
    /// </summary>
    private void UpdatePinkLine(Vector3 startPos, Vector3 endPos)
    {
        if (lineRenderer != null)
        {
            // Raise the line 0.2m along Y to prevent Z-fighting with ground objects.
            Vector3 offsetStart = startPos + Vector3.up * 0.2f;
            Vector3 offsetEnd = endPos + Vector3.up * 0.2f;
            lineRenderer.SetPosition(0, offsetStart);
            lineRenderer.SetPosition(1, offsetEnd);
        }
    }
}
