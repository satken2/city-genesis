using GeneratedModels;
using UnityEngine;

/// <summary>
/// 建物の可視化用統計値を保持するコンポーネント。
/// 耐久度・家賃といった単一値と、フロア種別の容量（capacity）辞書を保持する。
/// capacity のキーはスペース種別IDの文字列表現（"1"=居住, "2"=小売 など）で、
/// 値はその種別の床面積容量。
/// </summary>
public class VisualBuildingStats : MonoBehaviour
{
    public float rent;       // 0..10000
    public float durability; // 0..1
    // 建物が所属する地区ID。「地区別色塗り」モードで使用する。0は未所属。
    public int zone_id;

    /// <summary>
    /// 建物が持つスペース種別のビットマスク。
    /// ビット位置がスペース種別ID（0=OTHER, 1=RESIDENCE, ...）と一致する。
    /// (space_types &amp; (1 &lt;&lt; spaceTypeId)) != 0 で該当種別の有無を判定する。
    /// </summary>
    public int spaceTypes;

    // 市政府が建てた/所有する公共施設かどうか。「公共施設ハイライト」フラグの対象判定に使う。
    public bool publicFacility;

    public void InitFrom(BuildingSummary data)
    {
        rent = data.rent.HasValue ? data.rent.Value : 0f;
        durability = data.durability ?? 0f;
        spaceTypes = data.space_types;
        zone_id = data.zone_id;
        publicFacility = data.public_facility;
    }
}