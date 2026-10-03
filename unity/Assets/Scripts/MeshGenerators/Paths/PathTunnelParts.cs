using System;
using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;
using UnityEngine;

/// <summary>道路・線路の地下壁と、地上設備を省く区間を共通のトンネル判定から求める。</summary>
public static class PathTunnelParts
{
    public const string WallPart = "wall_underground";
    public const float DefaultWallWidth = 0.3f;
    private static readonly Dictionary<int, GameObject> DefaultWalls = new();

    public static bool IsTunnel(PathSummary path, Vector3 position)
        => TunnelSectionUtil.IsTunnelPoint(position, TunnelSectionUtil.GetClearanceHeight(path.path_variant));

    /// <summary>坑口と同じ深さ・地形追従規則で切り、境界をスプラインの弧長割合へ写す。</summary>
    public static List<TrackCorridorInfo.PercentRange> Ranges(SplineComputer spline, PathSummary path)
    {
        var result = new List<TrackCorridorInfo.PercentRange>();
        float length = spline.CalculateLength();
        if (length <= 0f) return result;
        int count = Mathf.Max(2, Mathf.CeilToInt(length / TunnelSectionUtil.SAMPLE_INTERVAL) + 1);
        var points = new List<Vector3>(count);
        for (int i = 0; i < count; i++) points.Add(spline.EvaluatePosition((double)i / (count - 1)));
        var line = new VehiclePolyline(points);
        foreach (var run in TunnelSectionUtil.CollectTunnelRuns(points,
            TunnelSectionUtil.GetClearanceHeight(path.path_variant), PathManager.GetPathFollowTerrain(path)))
        {
            double from = spline.Travel(0, line.Project(run[0]) / line.Length * length, Spline.Direction.Forward);
            double to = spline.Travel(0, line.Project(run[run.Count - 1]) / line.Length * length, Spline.Direction.Forward);
            if (to > from) result.Add(new TrackCorridorInfo.PercentRange { From = from, To = to });
        }
        return result;
    }

    public static IEnumerable<TrackCorridorInfo.PercentRange> Outside(double from, double to,
        List<TrackCorridorInfo.PercentRange> tunnels)
    {
        double cursor = from;
        foreach (var tunnel in tunnels)
        {
            if (tunnel.To <= cursor) continue;
            if (tunnel.From >= to) break;
            if (tunnel.From > cursor) yield return new TrackCorridorInfo.PercentRange { From = cursor, To = tunnel.From };
            cursor = Math.Max(cursor, tunnel.To);
            if (cursor >= to) yield break;
        }
        if (cursor < to) yield return new TrackCorridorInfo.PercentRange { From = cursor, To = to };
    }

    /// <summary>地上壁の有無とは独立して外縁に立てる。ホームや隣接線路の開口は呼び出し側の範囲を使う。</summary>
    public static void GenerateWall(GameObject parent, SplineComputer spline, PathSummary path, float halfWidth,
        float wallWidth, bool left, List<TrackCorridorInfo.PercentRange> tunnels,
        IEnumerable<TrackCorridorInfo.PercentRange> allowed = null)
    {
        wallWidth = wallWidth > 0f ? wallWidth : DefaultWallWidth;
        float offset = (halfWidth + wallWidth * .5f) * (left ? -1f : 1f);
        allowed ??= new[] { new TrackCorridorInfo.PercentRange { From = 0, To = 1 } };
        foreach (var range in allowed)
            foreach (var tunnel in tunnels)
            {
                double from = Math.Max(range.From, tunnel.From), to = Math.Min(range.To, tunnel.To);
                if (to <= from) continue;
                RoadPath.CreateSplineMesh(parent, spline, $"Models/Paths/p{path.path_variant}/{WallPart}",
                    wallWidth, offset, RoadPartHeights.WALL_ABOVE_SPLINE, left,
                    $"Models/Paths/p{path.path_type}/{WallPart}", clipFrom: from, clipTo: to);
            }
    }

    /// <summary>
    /// 専用モデルが無い場合もトンネル側面を保つ。高さはクリアランスまでで、左右をつなぐ天井を持たない。
    /// 共有の生成元メッシュはパスの破棄対象に含めず、通常パーツと同じスプライン・LOD経路へ渡す。
    /// </summary>
    public static GameObject ResolveDefaultWall(string name)
    {
        const string prefix = "Models/Paths/p";
        if (name == null || !name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith("/" + WallPart, StringComparison.Ordinal)) return null;
        int slash = name.LastIndexOf('/');
        if (!int.TryParse(name.Substring(prefix.Length, slash - prefix.Length), out int variant)) return null;
        if (DefaultWalls.TryGetValue(variant, out var cached) && cached != null) return cached;

        float height = Mathf.Max(.1f, TunnelSectionUtil.GetClearanceHeight(variant) - RoadPartHeights.WALL_ABOVE_SPLINE);
        var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
        source.name = "DefaultTunnelWall";
        source.hideFlags = HideFlags.HideAndDontSave;
        source.SetActive(false);
        var collider = source.GetComponent<Collider>();
        collider.enabled = false;
        UnityEngine.Object.Destroy(collider);
        var mesh = UnityEngine.Object.Instantiate(source.GetComponent<MeshFilter>().sharedMesh);
        mesh.name = "DefaultTunnelWall";
        var vertices = mesh.vertices;
        for (int i = 0; i < vertices.Length; i++) vertices[i].y = (vertices[i].y + .5f) * height;
        mesh.vertices = vertices;
        mesh.RecalculateBounds();
        source.GetComponent<MeshFilter>().sharedMesh = mesh;
        source.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;
        source.GetComponent<MeshRenderer>().sharedMaterial = RoadPath.GetOrCreateRoadMaterialByName("PATH_CONCRETE");
        DefaultWalls[variant] = source;
        return source;
    }
}
