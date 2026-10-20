using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manager for drawing earthworks areas (land grading/reclamation, STATE_EDIT_GRADING).
///
/// Uses the same interaction as drawing resource areas: starts in Placing (left click adds vertices),
/// then right click closes the shape and enters Editing (drag vertices, click edges to insert, right click to delete).
/// Sends edit_grading:update_shape to the UI on each shape change. The backend determines type (grading/reclamation), target height,
/// and estimates; the UI directly sends confirmation (POST /api/v1/grading).
/// </summary>
public class GradingAreaEditManager : PolygonAreaEditManager
{
    protected override string OverlayCanvasName => "GradingAreaEditOverlayCanvas";

    // Draw in purple to distinguish from roads/parcels (blue), resource areas (orange), selected vertices (green), and hovered edges (yellow).
    protected override Color EdgeColor => new Color(0.8f, 0.4f, 0.95f);
    protected override Color VertexColor => new Color(0.88f, 0.62f, 0.98f);

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
    }
}
