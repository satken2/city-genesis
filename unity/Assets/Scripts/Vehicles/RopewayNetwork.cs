using System;
using System.Collections.Generic;
using System.Linq;
using GeneratedModels;

/// <summary>描画専用の索道網。セルの常駐範囲ではなく、全パスのスナップショットから駅間を取り出す。</summary>
public static class RopewayNetwork
{
    public sealed class Link
    {
        public PathSummary Path;
        public bool Forward;
    }

    public sealed class Route
    {
        public int Id;
        public bool Closed;
        public readonly List<Link> Links = new();
    }

    public static bool Eligible(PathSummary path) => path != null && RoadPath.IsRopewayVariant(path.path_variant)
        && path.status == Constants.STATUS_ACTIVE && path.start_node_id != path.end_node_id;

    public static bool Station(PathSummary path) => path.path_variant == Constants.PATH_VARIANT_ROPEWAY_STATION;

    public static List<Route> Build(IEnumerable<PathSummary> source)
    {
        var paths = source.Where(Eligible).OrderBy(p => p.path_id).ToList();
        var byNode = new Dictionary<int, List<PathSummary>>();
        foreach (var path in paths)
        foreach (int node in new[] { path.start_node_id, path.end_node_id })
        {
            if (!byNode.TryGetValue(node, out var edges)) byNode[node] = edges = new();
            edges.Add(path);
        }
        var visited = new HashSet<int>();
        var result = new List<Route>();
        foreach (var seed in paths)
        {
            if (visited.Contains(seed.path_id)) continue;
            var component = new List<PathSummary>();
            var pending = new Stack<PathSummary>();
            pending.Push(seed);
            bool branched = false;
            int endNode = int.MaxValue;
            while (pending.Count > 0)
            {
                var path = pending.Pop();
                if (!visited.Add(path.path_id)) continue;
                component.Add(path);
                foreach (int node in new[] { path.start_node_id, path.end_node_id })
                {
                    var edges = byNode[node];
                    branched |= edges.Count > 2;
                    if (edges.Count == 1) endNode = Math.Min(endNode, node);
                    foreach (var next in edges) if (!visited.Contains(next.path_id)) pending.Push(next);
                }
            }
            // 不正な分岐を見た目だけで推測してつながず、その成分を描画対象から外す。
            if (branched) continue;
            bool closed = endNode == int.MaxValue;
            int current = closed ? Math.Min(seed.start_node_id, seed.end_node_id) : endNode;
            var route = new Route { Id = component.Min(p => p.path_id), Closed = closed };
            int previous = -1;
            for (int i = 0; i < component.Count; i++)
            {
                var edge = byNode[current].Find(p => p.path_id != previous);
                if (edge == null) break;
                bool forward = edge.start_node_id == current;
                route.Links.Add(new Link { Path = edge, Forward = forward });
                previous = edge.path_id;
                current = forward ? edge.end_node_id : edge.start_node_id;
            }
            int groups = 0, first = -1, last = -1;
            bool wasStation = closed && Station(route.Links[route.Links.Count - 1].Path);
            for (int i = 0; i < route.Links.Count; i++)
            {
                bool station = Station(route.Links[i].Path);
                if (station)
                {
                    if (!wasStation) groups++;
                    if (first < 0) first = i;
                    last = i;
                }
                wasStation = station;
            }
            // 駅パスの分割は駅数を増やさない。駅の無い枝先にも搬器を出さない。
            if (groups < 2) continue;
            if (!closed)
            {
                route.Links.RemoveRange(last + 1, route.Links.Count - last - 1);
                route.Links.RemoveRange(0, first);
                route.Id = route.Links[0].Path.path_id;
            }
            result.Add(route);
        }
        return result;
    }
}
