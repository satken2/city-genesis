using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manager for drawing earthworks areas (land grading/reclamation, STATE_EDIT_GRADING).
///
/// Uses the same interaction as drawing resource areas: starts in Placing (left click adds vertices),
/// then right click closes the shape and enters Editing (drag vertices, click edges to insert, right click to delete).
/// Sends edit_grading:update_shape to the UI on each shape change. The backend determines type (grading/reclamation), target height,
/// and estimates; the UI directly sends confirmation (POST /api/v1/grading).
///
/// For reclamation, the UI sends the height in its panel (edit_grading:preview_height) and this manager draws a translucent
/// plane over the closed area at that height. The plane is depth-tested, so existing land above the height shows through it.
/// </summary>
public class GradingAreaEditManager : PolygonAreaEditManager
{
    protected override string OverlayCanvasName => "GradingAreaEditOverlayCanvas";

    // Draw in purple to distinguish from roads/parcels (blue), resource areas (orange), selected vertices (green), and hovered edges (yellow).
    protected override Color EdgeColor => new Color(0.8f, 0.4f, 0.95f);
    protected override Color VertexColor => new Color(0.88f, 0.62f, 0.98f);

    private static readonly Color PreviewPlaneColor = new Color(0.8f, 0.4f, 0.95f, 0.3f);

    private OverlayMeshRenderer previewPlaneRenderer;
    private float? previewHeight;

    /// <summary>
    /// Esc does not exit. If only Unity returned to normal, the window would stay open and the display preset would remain.
    /// Exit only when the window closes (Rust sends STATE_NORMAL) or on edit_grading:finish after confirmation.
    /// </summary>
    protected override bool CancelOnEscape => false;

    /// <summary>
    /// Reports the current polygon to the UI through edit_grading:update_shape.
    /// The UI requests an estimate only when finalized (after closing the shape).
    /// </summary>
    protected override void EmitShape(List<List<float>> polygon, bool finalized)
    {
        IpcClient.Instance?.EmitEditGradingUpdateShape(polygon, finalized);
        RebuildPreviewPlane();
    }

    public override void FinishEditMode()
    {
        base.FinishEditMode();
        previewHeight = null;
        RebuildPreviewPlane();
    }

    /// <summary>
    /// edit_grading:preview_height. height is in meters above sea level, which equals world Y; null hides the plane.
    /// </summary>
    public void SetPreviewHeight(float? height)
    {
        previewHeight = height;
        RebuildPreviewPlane();
    }

    /// <summary>
    /// Rebuilds the plane only when the shape or height changes; it does not depend on the camera.
    /// Draws only in Editing, because the UI estimates closed shapes only.
    /// </summary>
    private void RebuildPreviewPlane()
    {
        bool visible = previewHeight.HasValue && IsEditingPhase && vertices.Count >= 3;
        if (previewPlaneRenderer == null)
        {
            if (!visible) return;
            previewPlaneRenderer = OverlayMeshRenderer.Create("GradingPreviewPlane", alwaysOnTop: false, parent: transform);
        }
        if (!visible)
        {
            previewPlaneRenderer.ClearMesh();
            return;
        }

        var ring = new List<Vector2>(vertices.Count);
        foreach (var v in vertices) ring.Add(new Vector2(v.x, v.z));
        if (PolygonUtils.SignedArea(ring) < 0f) ring.Reverse();
        Vector2[] polygon = ring.ToArray();
        // Ear clipping returns nothing for a self-intersecting shape (possible mid-drag), so no plane is drawn for it.
        List<int> indices = BuildingMeshBuilder.TriangulateEarClip(polygon);

        float y = previewHeight.Value;
        var triangles = new List<Vector3>(indices.Count);
        foreach (int i in indices) triangles.Add(new Vector3(polygon[i].x, y, polygon[i].y));

        previewPlaneRenderer.BeginFrame();
        previewPlaneRenderer.AddPreTriangulated(triangles, PreviewPlaneColor);
        previewPlaneRenderer.EndFrame(gameCamera != null ? gameCamera : Camera.main);
    }
}
