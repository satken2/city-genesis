using GeneratedModels;
using UnityEngine;

/// <summary>
/// Simple holder for parcel visualization statistics.
/// </summary>
public class VisualParcelStats : MonoBehaviour
{
    public float land_price; // Total price for the parcel.
    public int allowed_bd_mask;
    public int? owner_agent_id;
    // Parcel type: PARCEL_LAND / PARCEL_SHORE / PARCEL_WATER / PARCEL_RECLAIMED.
    // Used for physical compatibility checks against building types.
    public int parcel_type;
    // Water depth just outside the quay (m), checked against the selected port's berth_depth.
    public float water_depth;

    public void InitFrom(ParcelSummary data)
    {
        land_price = data.land_price;
        allowed_bd_mask = data.allowed_bd_mask;
        owner_agent_id = data.owner_agent_id;
        parcel_type = data.parcel_type;
        water_depth = data.water_depth;
    }
}


