using GeneratedModels;
using UnityEngine;

/// <summary>
/// Simple holder for road visualization statistics.
/// Also stores zone_id for district colors.
/// </summary>
public class VisualPathStats : MonoBehaviour
{
    private static readonly string PedestrianKey = Constants.VEHICLE_PEDESTRIAN.ToString();

    public int zone_id;           // District ID, 0 if unset.
    public float utilization;
    public float durability;
    public float effective_speed; // Geometry-derived traversal speed (km/h), PathSummary.base_speed.
    public float pedestrian_traffic; // Daily pedestrian traversal trips, the pedestrian key in path_traffic.

    public void InitFrom(PathSummary data)
    {
        zone_id = data.zone_id;
        utilization = data.utilization;
        durability = data.durability;
        effective_speed = data.base_speed;
        pedestrian_traffic = data.path_traffic != null && data.path_traffic.TryGetValue(PedestrianKey, out float walkers) ? walkers : 0f;
    }
}
