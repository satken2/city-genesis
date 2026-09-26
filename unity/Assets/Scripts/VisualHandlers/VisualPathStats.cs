using GeneratedModels;
using UnityEngine;

/// <summary>
/// 道路の可視化用統計値を保持するシンプルなホルダー。
/// 所属地区色分けに必要な zone_id も保持する。
/// </summary>
public class VisualPathStats : MonoBehaviour
{
    private static readonly string PedestrianKey = Constants.VEHICLE_PEDESTRIAN.ToString();

    public int zone_id;           // 所属地区ID (0は未設定)
    public float utilization;
    public float durability;
    public float effective_speed; // 形状から決まる通過速度(km/h)。PathSummary.base_speed
    public float pedestrian_traffic; // 1日あたりの歩行者の通過トリップ数。path_traffic の歩行者キー

    public void InitFrom(PathSummary data)
    {
        zone_id = data.zone_id;
        utilization = data.utilization;
        durability = data.durability;
        effective_speed = data.base_speed;
        pedestrian_traffic = data.path_traffic != null && data.path_traffic.TryGetValue(PedestrianKey, out float walkers) ? walkers : 0f;
    }
}
