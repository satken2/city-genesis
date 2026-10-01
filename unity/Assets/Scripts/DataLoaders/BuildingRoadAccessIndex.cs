using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GeneratedModels;
using UnityEngine;

/// <summary>建物の表示GOから独立した道路開口の索引。建物データの登録・削除と同時に更新する。</summary>
public static class BuildingRoadAccessIndex
{
    public sealed class Opening
    {
        public int BuildingId, Slot;
        public BuildingRoadOpening Local;
        public Vector3 Center;
        public Vector3 A => Center + Local.A;
        public Vector3 B => Center + Local.B;
        public float Height(Vector3 point)
        {
            float ground = Local.FollowsTerrain ? Utils.GetGroundHeight(point) : Utils.GetGroundHeight(Center);
            return (Local.MinGround.HasValue ? Mathf.Max(ground, Local.MinGround.Value) : ground) + Local.A.y;
        }
    }
    sealed class Entry
    {
        public string Signature;
        public OpenSpaceLayout.Plan Plan;
        public readonly List<Opening> Openings = new();
    }
    static readonly Dictionary<int, Entry> entries = new();
    static readonly SpatialHash<Opening> grid = new(32f);
    public static event Action<IReadOnlyList<Opening>> Changed;

    static string Signature(BuildingSummary data)
    {
        var key = new StringBuilder().Append(data.variant).Append(':').Append(data.status).Append(':').Append(data.landmark)
            .Append(':').Append(data.bd_type).Append(':').Append(data.floors);
        void Add(float value) => key.Append(':').Append(value.ToString("R", CultureInfo.InvariantCulture));
        Add(data.visual_variant); Add(data.visual_variant2);
        if (data.centerPointArray != null) foreach (float v in data.centerPointArray) Add(v);
        if (data.polygonArray != null) foreach (var point in data.polygonArray) foreach (float v in point) Add(v);
        return key.ToString();
    }

    public static OpenSpaceLayout.Plan GetOpenSpacePlan(BuildingSummary data)
    {
        if (entries.TryGetValue(data.agent_id, out var entry) && entry.Plan != null && entry.Signature == Signature(data)) return entry.Plan;
        var master = MasterDataStore.Instance?.GetAgentMaster(data.variant);
        return OpenSpaceLayout.Create(BuildingMeshUtils.GetFootprint(data), master?.model_type ?? Constants.MODEL_PARKING, data.visual_variant2);
    }

    public static void Register(BuildingSummary data)
    {
        int? type = MasterDataStore.Instance?.GetAgentMaster(data.variant)?.model_type;
        if (type != Constants.MODEL_ROTARY && type != Constants.MODEL_PREBUILT && type != Constants.MODEL_PORT) { Remove(data.agent_id); return; }
        string signature = Signature(data);
        if (entries.TryGetValue(data.agent_id, out var previous) && previous.Signature == signature) return;
        Remove(data.agent_id);
        var entry = new Entry { Signature = signature };
        if (type == Constants.MODEL_ROTARY) entry.Plan = GetOpenSpacePlan(data);
        entries[data.agent_id] = entry;
        float[] center = data.centerPointArray;
        if (center == null || center.Length < 2) return;
        var openings = BuildingRoadAccess.Resolve(data);
        for (int i = 0; i < openings.Count; i++)
        {
            var opening = new Opening { BuildingId = data.agent_id, Slot = i, Local = openings[i], Center = new Vector3(center[0], 0, center[1]) };
            entry.Openings.Add(opening);
            foreach (var position in Samples(opening)) grid.Add(opening, position);
        }
        Changed?.Invoke(entry.Openings);
    }

    public static IEnumerable<Vector3> Samples(Opening opening)
    {
        int count = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(opening.A, opening.B) / 16f));
        for (int i = 0; i <= count; i++) yield return Vector3.Lerp(opening.A, opening.B, (float)i / count);
    }

    public static List<Opening> Query(IReadOnlyList<Vector3> road, float radius)
    {
        var found = new HashSet<Opening>();
        for (int i = 1; i < road.Count; i++)
        {
            int count = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(road[i - 1], road[i]) / 16f));
            for (int j = 0; j <= count; j++)
                foreach (var opening in grid.Query(Vector3.Lerp(road[i - 1], road[i], (float)j / count), radius + 16f)) found.Add(opening);
        }
        var result = new List<Opening>(found);
        result.Sort((a, b) => a.BuildingId != b.BuildingId ? a.BuildingId.CompareTo(b.BuildingId) : a.Slot.CompareTo(b.Slot));
        return result;
    }

    public static void Remove(int id)
    {
        if (!entries.TryGetValue(id, out var entry)) return;
        foreach (var opening in entry.Openings) foreach (var position in Samples(opening)) grid.Remove(opening, position);
        entries.Remove(id);
        Changed?.Invoke(entry.Openings);
    }

    public static void Clear()
    {
        foreach (var entry in entries.Values) Changed?.Invoke(entry.Openings);
        entries.Clear(); grid.Clear();
    }
}
