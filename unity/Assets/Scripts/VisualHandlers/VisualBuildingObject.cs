using UnityEngine;

/// <summary>
/// Calculates overlay colors and opacity for building display modes.
/// Reads durability, rent, capacity, and other statistics from VisualBuildingStats,
/// providing mode-dependent colors to VisualModeObject.
/// VisualModeObject centrally manages materials and rendering switches,
/// so this component does not implement IVisualModeHandler
/// or register with VisualModeHandler.
/// </summary>
public class VisualBuildingObject : MonoBehaviour
{
    private VisualBuildingStats stats;

    private void Start()
    {
        stats = GetComponent<VisualBuildingStats>();
    }

    /// <summary>
    /// Color at durability 0.0 (#ff4444), matching meterStartColor in the UI legend.
    /// </summary>
    private static readonly Color DurabilityMinColor = new Color(1f, 0.267f, 0.267f);

    /// <summary>
    /// Calculate and return the overlay color for the specified visual mode.
    /// Return null for modes that need no solid-color overlay.
    /// </summary>
    public Color? GetOverlayColor(int mode)
    {
        if (stats == null) stats = GetComponent<VisualBuildingStats>();
        if (stats == null) return null;

        switch (mode)
        {
            case VisualModeBuilding.RENT:
                // Rent: 0=green, 10000=red.
                return Color.Lerp(Color.green, Color.red, Mathf.Clamp01(NullableToZero(stats.rent) / 10000f));
            case VisualModeBuilding.DURABILITY:
                // Durability: 0=red (#ff4444), 1=white (#ffffff).
                return Color.Lerp(DurabilityMinColor, Color.white, Mathf.Clamp01(NullableToZero(stats.durability)));
            default:
                return null;
        }
    }

    /// <summary>
    /// Return whether the building has floors of the specified space-type mode.
    /// Check the VisualBuildingStats spaceTypes bitmask.
    /// Space-type constants are powers of two (SPACE_RESIDENCE=2, SPACE_RETAIL=4, ...),
    /// so bitwise AND directly tests presence.
    /// </summary>
    public bool HasSpaceType(int spaceType)
    {
        if (stats == null) stats = GetComponent<VisualBuildingStats>();
        if (stats == null) return false;

        return (stats.spaceTypes & spaceType) != 0;
    }

    private float NullableToZero(float? v) => v.HasValue ? v.Value : 0f;
}