using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// 道路沿いの建物入口(区画の接道辺→敷地内の点)を車と歩行者で共有する幾何ヘルパー。
/// 入口はFBXの玄関や駐車場の位置ではなく、接道辺の中点から敷地側へ3m入った仮の描画位置である。
/// 進行方向や通行側の制約は持たず、パス幾何(始点→終点)に対する左右だけを返す。
/// 車はこれを通行側と走行方向へ、歩行者は歩道の左右へ読み替える。
/// </summary>
public static class RoadsideAccess
{
    public sealed class Candidate
    {
        public int parcel, building;
        public float s;
        public int side;              // パス幾何(始点→終点)に対して +1=右 / -1=左
        public Vector3 destination;   // 敷地内の退出・発生点
    }

    /// <summary>接道できる街路か。歩行者専用道路も許す場合は allowWalkway を立てる。</summary>
    public static bool FrontageAllowed(PathSummary road, bool allowWalkway = false)
    {
        if (road == null || MasterDataStore.Instance == null) return false;
        if (road.path_type != Constants.PATH_STREET && !(allowWalkway && road.path_type == Constants.PATH_WALKWAY)) return false;
        return MasterDataStore.Instance.GetPathMasterDictionary().TryGetValue(road.path_variant, out var master) && master.frontage_allowed;
    }

    /// <summary>
    /// 区画の各辺を道路中心線へ射影し、道路端から3m以上・外縁差 −1〜4m・高低差2m以内・
    /// 辺が道路とほぼ平行(内積0.8以上)・敷地内3mの点が本当に区画内、を満たす辺だけを入口にする。
    /// 接道IDは離れた区画にも付くため、IDだけで入口とはしない。
    /// </summary>
    public static void BuildCandidates(VehiclePolyline line, float halfWidth, ParcelSummary parcel, int building,
        ParcelManager parcelManager, List<Candidate> result)
    {
        if (parcel == null || parcel.status != Constants.STATUS_ACTIVE || parcelManager == null) return;
        var polygon = GeneratedModelUtils.ToFloatJaggedArray(parcel.polygon);
        if (polygon == null || polygon.Length < 3) return;
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
            if (a.Length < 2 || b.Length < 2) continue;
            Vector3 mid = new Vector3((a[0] + b[0]) * .5f, parcel.altitude, (a[1] + b[1]) * .5f);
            float s = line.Project(mid);
            line.Sample(s, true, out var point, out var tangent);
            Vector3 offset = mid - point; offset.y = 0;
            if (s < 3 || line.Length - s < 3 || Mathf.Abs(mid.y - point.y) > 2) continue;
            if (offset.magnitude < halfWidth - 1 || offset.magnitude > halfWidth + 4) continue;
            Vector3 edge = new Vector3(b[0] - a[0], 0, b[1] - a[1]).normalized;
            if (Mathf.Abs(Vector3.Dot(edge, tangent)) < .8f) continue;
            Vector3 destination = mid + offset.normalized * 3;
            if (!parcelManager.IsPointInParcel(parcel.parcel_id, destination)) continue;
            float side = Vector3.Dot(Vector3.Cross(Vector3.up, tangent), offset);
            result.Add(new Candidate { parcel = parcel.parcel_id, building = building, s = s,
                side = side < 0 ? -1 : 1, destination = destination });
        }
    }

    /// <summary>
    /// 退出・発生の直前に、区画と建物がまだ有効で入口の点が敷地内にあることを確かめる。
    /// 未ロードや撤去で条件を失った入口へ向かわないための検査で、道路側の条件は呼び出し側が足す。
    /// </summary>
    public static bool EntranceValid(ParcelManager parcelManager, BuildingManager buildingManager,
        int parcelId, int buildingId, int pathId, Vector3 destination)
    {
        if (parcelManager == null || buildingManager == null) return false;
        var parcel = parcelManager.GetParcelSummary(parcelId);
        var building = buildingManager.GetBuildingSummary(buildingId);
        return parcel != null && parcel.front_path_id == pathId && parcel.status == Constants.STATUS_ACTIVE
            && building != null && building.is_bd && building.parcel_id == parcelId && building.status == Constants.STATUS_ACTIVE
            && parcelManager.IsPointInParcel(parcelId, destination);
    }

    /// <summary>区画に建つ稼働中の実建物のうち最小IDを返し、建物集合の署名も更新する。無ければ0。</summary>
    public static int ActiveBuilding(BuildingManager buildingManager, int parcelId, ref int signature)
    {
        int building = 0;
        if (buildingManager == null) return 0;
        foreach (int bid in buildingManager.GetBuildingIdsByParcel(parcelId))
        {
            var data = buildingManager.GetBuildingSummary(bid);
            if (data == null || !data.is_bd || data.status != Constants.STATUS_ACTIVE) continue;
            signature = unchecked(signature * 31 + bid);
            if (building == 0 || bid < building) building = bid;
        }
        return building;
    }
}
