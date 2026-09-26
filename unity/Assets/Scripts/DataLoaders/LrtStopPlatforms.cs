using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// LRT停留所のホーム範囲の索引。路線一覧(TransitDataStore)の stop_points から、下地パスごとに停止中心の割合と
/// デッキ長を集めて LrtPlatformParts.StopProvider で生成器へ渡し、範囲が変わったパスだけ PathManager に作り直させる。
/// stop_points は journeys と独立に返るので、運休中の路線の停留所にもホームが出る。都市切替でクリアする。
/// </summary>
public sealed class LrtStopPlatforms : MonoBehaviour
{
    private Dictionary<int, List<LrtPlatformParts.Stop>> stopsByPath = new();
    private int session = -1, revision = -1;
    private PathManager paths;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<LrtStopPlatforms>() == null)
            new GameObject("LrtStopPlatforms").AddComponent<LrtStopPlatforms>();
    }

    private void Awake() => LrtPlatformParts.StopProvider = Lookup;

    private void OnDestroy()
    {
        if (LrtPlatformParts.StopProvider == (System.Func<int, IReadOnlyList<LrtPlatformParts.Stop>>)Lookup)
            LrtPlatformParts.StopProvider = null;
    }

    private IReadOnlyList<LrtPlatformParts.Stop> Lookup(int pathId) =>
        stopsByPath.TryGetValue(pathId, out var stops) ? stops : null;

    private void Update()
    {
        var store = TransitDataStore.Ensure();
        bool sessionChanged = session != store.Session;
        if (!sessionChanged && revision == store.Revision) return;
        session = store.Session;
        revision = store.Revision;
        var next = Collect(store.Lines);
        if (sessionChanged)
        {
            // 都市切替ではパスも全て作り直されるので、再生成は要求しない。
            stopsByPath = next;
            return;
        }
        var changed = new List<int>();
        foreach (var entry in next)
            if (!stopsByPath.TryGetValue(entry.Key, out var previous) || !Same(previous, entry.Value)) changed.Add(entry.Key);
        foreach (int pathId in stopsByPath.Keys)
            if (!next.ContainsKey(pathId)) changed.Add(pathId);
        stopsByPath = next;
        if (changed.Count == 0) return;
        if (paths == null) paths = FindAnyObjectByType<PathManager>();
        if (paths != null) paths.RequestLrtPlatformRebuild(changed);
    }

    /// <summary>
    /// 車種が VEHICLE_TRAM の路線の停留所を下地パスごとに集める。デッキ長は編成長(両数×1両の長さ)に両端の余裕を
    /// 足した値。前回との比較のため割合・デッキ長の昇順に並べる。下地を解決できなかった停留所は除く。
    /// </summary>
    public static Dictionary<int, List<LrtPlatformParts.Stop>> Collect(IEnumerable<TransitLineSummary> lines)
    {
        var result = new Dictionary<int, List<LrtPlatformParts.Stop>>();
        foreach (var line in lines)
        {
            if (line?.stop_points == null || VehicleModelConfig.GetTransitVehicleType(line.path_variant) != Constants.VEHICLE_TRAM) continue;
            float consist = Mathf.Max(1, line.cars_per_train) * line.length_per_vehicle;
            if (consist <= 0f) continue;
            float deck = LrtRoadParts.PlatformDeckLength(consist);
            foreach (var point in line.stop_points)
            {
                if (point?.station_path_id == null || point.fraction == null) continue;
                int pathId = point.station_path_id.Value;
                if (!result.TryGetValue(pathId, out var stops)) result[pathId] = stops = new List<LrtPlatformParts.Stop>();
                stops.Add(new LrtPlatformParts.Stop(point.fraction.Value, deck));
            }
        }
        foreach (var stops in result.Values)
            stops.Sort((a, b) => a.Fraction != b.Fraction ? a.Fraction.CompareTo(b.Fraction) : a.DeckLength.CompareTo(b.DeckLength));
        return result;
    }

    private static bool Same(List<LrtPlatformParts.Stop> a, List<LrtPlatformParts.Stop> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Fraction != b[i].Fraction || a[i].DeckLength != b[i].DeckLength) return false;
        return true;
    }
}
