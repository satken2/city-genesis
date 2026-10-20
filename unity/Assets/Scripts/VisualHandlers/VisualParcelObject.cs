using UnityEngine;

/// <summary>
/// Lightweight component passing VisualParcelStats references to ParcelManager's data-visualization loop.
/// ParcelManager's OverlayMeshRenderer batches all parcel outlines, highlights, and data fills,
/// so individual parcels do not register with VisualModeHandler, avoiding per-parcel registration, periodic scans, and mode notifications.
/// Leave parcel MeshRenderers disabled from creation onward.
/// </summary>
public class VisualParcelObject : MonoBehaviour
{
    private VisualParcelStats stats;

    /// <summary>
    /// Return VisualParcelStats for use in ParcelManager's data-visualization loop.
    /// Fetch with GetComponent on first access and cache it.
    /// </summary>
    public VisualParcelStats Stats
    {
        get
        {
            if (stats == null) stats = GetComponent<VisualParcelStats>();
            return stats;
        }
    }
}
