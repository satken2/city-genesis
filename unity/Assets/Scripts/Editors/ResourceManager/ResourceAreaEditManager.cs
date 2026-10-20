using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Resource area edit manager.
///
/// The shared base <see cref="PolygonAreaEditManager"/> centralizes arbitrary polygon construction,
/// including vertex placement, dragging, and overlay rendering. This subclass replaces
/// only reporting the completed polygon to the UI through edit_resource:update_shape
/// and the overlay appearance (warm colors).
///
/// Unlike existing parcels/roads, confirmation (backend POST /api/v1/resources)
/// is sent directly by the UI (Tauri). This manager only builds polygon geometry and reports incremental updates.
/// </summary>
public class ResourceAreaEditManager : PolygonAreaEditManager
{
    protected override string OverlayCanvasName => "ResourceAreaEditOverlayCanvas";

    // Draw resource areas in warm orange, distinct from blue roads and parcel boundaries.
    protected override Color EdgeColor => new Color(0.95f, 0.65f, 0.2f);
    protected override Color VertexColor => new Color(0.95f, 0.75f, 0.35f);

    /// <summary>
    /// Reports the current polygon through edit_resource:update_shape.
    /// Sends only polygon, as resource areas do not use finalized (UI allows confirmation with 3 or more vertices).
    /// </summary>
    protected override void EmitShape(List<List<float>> polygon, bool finalized)
    {
        if (IpcClient.Instance == null) return;
        IpcClient.Instance.EmitEditResourceUpdateShape(polygon);
    }
}
