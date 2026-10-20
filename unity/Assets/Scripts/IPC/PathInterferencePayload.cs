using System.Collections.Generic;
using Newtonsoft.Json.Linq;

/// <summary>
/// Payload for world:highlight_objects.
/// Contains parcels, buildings, existing paths, and rivers affected by planned/unstarted paths.
/// Matches backend compute_path_interference: 4 parcel construction eligibility classes,
/// agent_id for buildings on those parcels, existing path_id for elevation collisions,
/// and river ID strings for ground-level crossings.
/// </summary>
public class PathInterferencePayload
{
    public List<int> BlockingParcels = new List<int>();
    public List<int> RemovableParcels = new List<int>();
    public List<int> ShavableParcels = new List<int>();
    public List<int> ExemptParcels = new List<int>();
    public List<int> BuildingIds = new List<int>();
    public List<int> CollidingPathIds = new List<int>();
    public List<string> CollidingRiverIds = new List<string>();

    public bool IsEmpty =>
        BlockingParcels.Count == 0 && RemovableParcels.Count == 0
        && ShavableParcels.Count == 0 && ExemptParcels.Count == 0
        && BuildingIds.Count == 0 && CollidingPathIds.Count == 0
        && CollidingRiverIds.Count == 0;

    /// <summary>
    /// Builds from IPC params.
    /// parcelsToken is a JObject with {blocking,removable,shavable,exempt};
    /// buildingIds / collidingPathIds are nullable int arrays;
    /// collidingRiverIdsToken is a nullable JArray of strings.
    /// </summary>
    public static PathInterferencePayload FromParams(
        JToken parcelsToken, int[] buildingIds, int[] collidingPathIds, JToken collidingRiverIdsToken = null)
    {
        var payload = new PathInterferencePayload();

        if (parcelsToken is JObject parcels)
        {
            payload.BlockingParcels = ReadIntList(parcels, "blocking");
            payload.RemovableParcels = ReadIntList(parcels, "removable");
            payload.ShavableParcels = ReadIntList(parcels, "shavable");
            payload.ExemptParcels = ReadIntList(parcels, "exempt");
        }

        if (buildingIds != null) payload.BuildingIds.AddRange(buildingIds);
        if (collidingPathIds != null) payload.CollidingPathIds.AddRange(collidingPathIds);
        if (collidingRiverIdsToken is JArray riverArr)
        {
            foreach (var item in riverArr)
            {
                string id = item?.ToString();
                if (!string.IsNullOrEmpty(id)) payload.CollidingRiverIds.Add(id);
            }
        }

        return payload;
    }

    private static List<int> ReadIntList(JObject obj, string key)
    {
        var list = new List<int>();
        if (obj.TryGetValue(key, out var tok) && tok is JArray arr)
        {
            foreach (var item in arr)
            {
                list.Add((int)item);
            }
        }
        return list;
    }
}
