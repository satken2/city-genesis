using System.Collections.Generic;
using Newtonsoft.Json.Linq;

/// <summary>
/// world:highlight_objects のペイロード。
/// 計画中／未着工パスが干渉する区画・建物・既存パスを保持する。
/// バックエンドの compute_path_interference と同形（区画は着工可否の4分類、
/// 建物は当該区画に立地する agent_id、パスは立体交差する既存 path_id）。
/// </summary>
public class PathInterferencePayload
{
    public List<int> BlockingParcels = new List<int>();
    public List<int> RemovableParcels = new List<int>();
    public List<int> ShavableParcels = new List<int>();
    public List<int> ExemptParcels = new List<int>();
    public List<int> BuildingIds = new List<int>();
    public List<int> CollidingPathIds = new List<int>();

    public bool IsEmpty =>
        BlockingParcels.Count == 0 && RemovableParcels.Count == 0
        && ShavableParcels.Count == 0 && ExemptParcels.Count == 0
        && BuildingIds.Count == 0 && CollidingPathIds.Count == 0;

    /// <summary>
    /// IPC の params から組み立てる。
    /// parcelsToken は {blocking,removable,shavable,exempt} を持つ JObject、
    /// buildingIds / collidingPathIds は int 配列（null 可）。
    /// </summary>
    public static PathInterferencePayload FromParams(
        JToken parcelsToken, int[] buildingIds, int[] collidingPathIds)
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
