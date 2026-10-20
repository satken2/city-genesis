using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Draws parcel previews, subdivisions, roadside parcels, and markers.
/// Builds meshes directly in 3D using OverlayMeshRenderer + Custom/OverlayLine,
/// drawing in world coordinates in the same way as ParcelManager outlines.
/// </summary>
public class ParcelPlanRenderer : MonoBehaviour
{
    [Header("Line Settings")]
    /// <summary>Preview parcel outline color. Blue distinguishes it from existing white parcel outlines.</summary>
    public Color previewLineColor = new Color(0.2f, 0.6f, 1.0f, 1.0f);
    /// <summary>Source parcel outline color (before subdivision, roads, etc.). Light gray distinguishes it from previews.</summary>
    public Color originalLineColor = new Color(0.6f, 0.6f, 0.6f, 0.8f);
    public float lineThickness = 1.0f;

    [Header("Marker Settings")]
    public Material tempMarkerMaterial;
    public Color markerColor = Color.red;
    public float markerSize = 1.0f;

    /// <summary>Reference world Y (metres) for sea preview outlines. Above the sea plane (worldY=0.001).</summary>
    private const float SeaSurfaceY = 0.01f;

    private GameObject overlayMeshObj;
    private OverlayMeshRenderer overlayRenderer;

    // Preview polygon data (world coordinates)
    private List<List<Vector3>> previewPolygons = new List<List<Vector3>>();
    private List<Vector3> originalPolygon = new List<Vector3>();
    private List<Vector3> pathPoints = new List<Vector3>();
    private bool hasPreview = false;

    // Marker object
    private GameObject markerObject;

    private void Start()
    {
        CreateMarkerObject();
        HideMarker();
    }

    private void EnsureOverlayRenderer()
    {
        if (overlayMeshObj != null) return;

        overlayRenderer = OverlayMeshRenderer.Create("ParcelPlanOverlayMesh", alwaysOnTop: false);
        overlayMeshObj = overlayRenderer.gameObject;
    }

    private void Update()
    {
        if (!hasPreview || overlayRenderer == null) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        overlayRenderer.BeginFrame();

        // Source parcel and road outlines (gray)
        DrawPolygon(originalPolygon, originalLineColor);
        DrawPath(pathPoints, originalLineColor);

        // Preview parcel outlines (blue)
        foreach (var poly in previewPolygons)
        {
            DrawPolygon(poly, previewLineColor);
        }

        overlayRenderer.EndFrame(cam);
    }

    /// <summary>
    /// Adds a closed polygon outline to OverlayMeshRenderer.
    /// </summary>
    private void DrawPolygon(List<Vector3> points, Color color)
    {
        if (points == null || points.Count < 2) return;
        int count = points.Count;
        for (int i = 0; i < count; i++)
        {
            int nextI = (i + 1) % count;
            overlayRenderer.AddSolidLine(points[i], points[nextI], color, lineThickness);
        }
    }

    /// <summary>
    /// Adds open path segments to OverlayMeshRenderer.
    /// </summary>
    private void DrawPath(List<Vector3> points, Color color)
    {
        if (points == null || points.Count < 2) return;
        for (int i = 0; i < points.Count - 1; i++)
        {
            overlayRenderer.AddSolidLine(points[i], points[i + 1], color, lineThickness);
        }
    }

    /// <summary>
    /// Sets polygon vertex Y slightly above terrain height.
    /// At sea (terrain below sea level), clamp to sea level so the sea plane does not occlude it.
    /// Completed sea parcels also use sea-relative altitude, keeping preview and result heights consistent.
    /// </summary>
    private List<Vector3> AdjustPolygonHeight(List<Vector3> polygonPoints)
    {
        if (polygonPoints == null || polygonPoints.Count == 0) return new List<Vector3>();
        List<Vector3> adjusted = new List<Vector3>(polygonPoints.Count);
        foreach (var pt in polygonPoints)
        {
            float ground = Mathf.Max(Utils.GetGroundHeight(new Vector3(pt.x, 1000f, pt.z)), SeaSurfaceY);
            adjusted.Add(new Vector3(pt.x, ground + 0.1f, pt.z));
        }
        return adjusted;
    }

    /// <summary>
    /// Draws the whole block polygon and its enclosed parcel polygons.
    /// </summary>
    public void RenderBlockPreview(List<Vector3> blockPolygon, List<List<Vector3>> parcelPolygons)
    {
        ClearPreview();
        EnsureOverlayRenderer();

        if (blockPolygon != null && blockPolygon.Count > 0)
        {
            originalPolygon = AdjustPolygonHeight(blockPolygon);
        }

        if (parcelPolygons != null)
        {
            foreach (var poly in parcelPolygons)
            {
                if (poly != null && poly.Count > 0)
                {
                    previewPolygons.Add(AdjustPolygonHeight(poly));
                }
            }
        }

        hasPreview = (originalPolygon.Count >= 2) || (previewPolygons.Count > 0);
    }

    /// <summary>
    /// Draws parcel subdivision previews (the block being split and resulting parcels).
    /// </summary>
    public void RenderSplitPreview(List<Vector3> originalBlockPolygon, List<List<Vector3>> splitParcelPolygons)
    {
        RenderBlockPreview(originalBlockPolygon, splitParcelPolygons);
    }

    /// <summary>
    /// Clears all previews.
    /// </summary>
    public void ClearPreview()
    {
        previewPolygons.Clear();
        originalPolygon.Clear();
        pathPoints.Clear();
        hasPreview = false;
        if (overlayRenderer != null)
        {
            overlayRenderer.ClearMesh();
        }
        HideMarker();
    }

    /// <summary>
    /// Hides the marker.
    /// </summary>
    public void HideMarker()
    {
        if (markerObject != null)
        {
            markerObject.SetActive(false);
        }
    }

    private void CreateMarkerObject()
    {
        if (markerObject != null) Destroy(markerObject);

        markerObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        markerObject.name = "SelectionMarker";
        markerObject.transform.parent = transform;
        markerObject.transform.localScale = new Vector3(markerSize, markerSize, markerSize);

        Renderer renderer = markerObject.GetComponent<Renderer>();
        if (renderer != null)
        {
            if (tempMarkerMaterial != null)
            {
                renderer.material = tempMarkerMaterial;
            }
            else
            {
                renderer.material = new Material(Shader.Find("Standard"));
                renderer.material.color = markerColor;
            }
        }

        Collider collider = markerObject.GetComponent<Collider>();
        if (collider != null) Destroy(collider);

        markerObject.SetActive(false);
    }

    private void OnDestroy()
    {
        ClearPreview();
        if (overlayMeshObj != null)
        {
            Destroy(overlayMeshObj);
        }
    }
}
