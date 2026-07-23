using GeneratedModels;
using UnityEngine;

/// <summary>
/// パーセルの可視化用統計値を保持するシンプルなホルダー。
/// </summary>
public class VisualParcelStats : MonoBehaviour
{
    public float land_price; // 0..10000
    public int allowed_bd_mask;
    // 土地区画種類（PARCEL_LAND / PARCEL_SHORE / PARCEL_WATER / PARCEL_RECLAIMED）。
    // 建物種類との物理的互換性チェックで使用する。
    public int parcel_type;

    public void InitFrom(ParcelSummary data)
    {
        land_price = data.land_price;
        allowed_bd_mask = data.allowed_bd_mask;
        parcel_type = data.parcel_type;
    }
}


