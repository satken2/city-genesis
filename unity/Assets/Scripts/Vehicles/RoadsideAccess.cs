using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// Shared geometry helpers for car/pedestrian building access: parcel frontage edge to a point inside the site.
/// Entrances are provisional rendering positions 3m inside from the frontage midpoint, rather than FBX door or parking locations.
/// Return only left/right relative to path geometry from start to end, without travel-direction or traffic-side restrictions.
/// Cars interpret this using traffic side and direction; pedestrians interpret it as sidewalk side.
/// </summary>
public static class RoadsideAccess
{
    public sealed class Candidate
    {
        public int parcel, building;
        public float s;
        public int side;              // Relative to path geometry start -> end: +1=right, -1=left.
        public Vector3 destination;   // Exit/spawn point within the site.
    }

    /// <summary>Whether this street allows road access. Set allowWalkway to include pedestrian-only roads.</summary>
    public static bool FrontageAllowed(PathSummary road, bool allowWalkway = false)
    {
        if (road == null || MasterDataStore.Instance == null) return false;
        if (road.path_type != Constants.PATH_STREET && !(allowWalkway && road.path_type == Constants.PATH_WALKWAY)) return false;
        return MasterDataStore.Instance.GetPathMasterDictionary().TryGetValue(road.path_variant, out var master) && master.frontage_allowed;
    }

    /// <summary>
    /// Project each parcel edge onto the road centerline. Accept only edges at least 3m from road ends, with outer-edge difference -1 to 4m, height difference at most 2m,
    /// nearly parallel alignment (dot product >=0.8), and a point 3m inward that actually lies within the parcel.
    /// Road-access IDs can also be assigned to distant parcels, so an ID alone is insufficient.
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
    /// Immediately before exit/spawn, verify that the parcel/building is still valid and the entrance lies inside the site.
    /// Prevents movement toward entrances invalidated by unloading or removal. Callers add road-side conditions.
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

    /// <summary>Return the lowest ID of active real buildings on the parcel, also updating the building-set signature. 0 if none.</summary>
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
