using GeneratedModels;
using UnityEngine;

/// <summary>
/// Stores building statistics for visualization.
/// Contains scalar values such as durability/rent and a capacity dictionary by floor type.
/// capacity keys are space type IDs as strings ("1"=residential, "2"=retail, etc.);
/// values are floor-area capacities for that type.
/// </summary>
public class VisualBuildingStats : MonoBehaviour
{
    public float rent;       // 0..10000
    public float durability; // 0..1
    // District ID used in district-color mode; 0 means unassigned.
    public int zone_id;

    /// <summary>
    /// Bitmask of the building's space types.
    /// Bit positions match space type IDs (0=OTHER, 1=RESIDENCE, ...).
    /// Test presence with (space_types &amp; (1 &lt;&lt; spaceTypeId)) != 0.
    /// </summary>
    public int spaceTypes;

    public void InitFrom(BuildingSummary data)
    {
        rent = data.rent.HasValue ? data.rent.Value : 0f;
        durability = data.durability ?? 0f;
        spaceTypes = data.space_types;
        zone_id = data.zone_id;
    }
}