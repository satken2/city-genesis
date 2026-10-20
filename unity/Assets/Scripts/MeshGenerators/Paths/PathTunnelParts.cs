using System;
using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;
using UnityEngine;

/// <summary>Resolve underground road/track walls/ceilings and outdoor-equipment/platform-shelter exclusion intervals from shared tunnel tests.</summary>
public static class PathTunnelParts
{
    public const string WallPart = "wall_underground";
    public const string CeilingPart = "ceiling_underground";
    public const float DefaultWallWidth = 0.3f;
    private static readonly Dictionary<int, GameObject> DefaultWalls = new();
    private static GameObject defaultCeiling;
    private static readonly Dictionary<Mesh, Mesh> PlatformsWithoutRoof = new();

    // Default wall/ceiling materials resolve by name (from asset packs if present); rebuild on city switches.
    static PathTunnelParts() => AssetPackStore.CityChanging += ForgetDefaultParts;

    private static void ForgetDefaultParts()
    {
        foreach (var wall in DefaultWalls.Values) if (wall != null) UnityEngine.Object.Destroy(wall);
        DefaultWalls.Clear();
        if (defaultCeiling != null) UnityEngine.Object.Destroy(defaultCeiling);
        defaultCeiling = null;
    }

    // Authoring boundary separating platform floors/doors from shelters. Shelters/supports are independent members above this height.
    public const float PlatformRoofHeight = 3.2f;

    public static bool IsTunnel(PathSummary path, Vector3 position)
        => TunnelSectionUtil.IsTunnelPoint(position, TunnelSectionUtil.GetClearanceHeight(path.path_variant));

    /// <summary>Clip using the same depth/terrain-conformity rules as portals; map boundaries to spline arc-length fractions.</summary>
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

    /// <summary>
    /// Place at outer edges independently of aboveground walls. Use caller ranges for platform/adjacent-track openings.
    /// Dedicated models are right-side parts with origin at bottom (X-mirror left); heights are unscaled, so author to master height clearance.
    /// </summary>
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

    /// <summary>Add ceiling at clearance height. Like default ceilings, dedicated models are one-sided at origin height, facing downward.</summary>
    public static void GenerateCeiling(GameObject parent, SplineComputer spline, PathSummary path,
        float leftExtent, float rightExtent, List<TrackCorridorInfo.PercentRange> tunnels)
    {
        float height = TunnelSectionUtil.GetClearanceHeight(path.path_variant);
        float lateral = (rightExtent - leftExtent) * .5f;
        var previous = PathDrawContext.PartCreated;
        var warp = PathDrawContext.Warp;
        PathDrawContext.PartCreated = (part, source, mesh, from, to) =>
            TunnelDepthMarkers.Create(part, source, height + mesh.bounds.max.y, lateral, warp, from, to);
        try
        {
            foreach (var tunnel in tunnels)
                RoadPath.CreateSplineMesh(parent, spline, $"Models/Paths/p{path.path_variant}/{CeilingPart}",
                    leftExtent + rightExtent, lateral, height,
                    fallbackPrefabName: $"Models/Paths/p{path.path_type}/{CeilingPart}", clipFrom: tunnel.From, clipTo: tunnel.To);
        }
        finally { PathDrawContext.PartCreated = previous; }
    }

    /// <summary>Preserve platform floors/doors; remove only independent members including shelters from underground sections.</summary>
    public static GameObject GeneratePlatform(GameObject parent, SplineComputer spline, PathSummary path,
        string variantBase, string typeBase, float trackOffset, bool mirror)
    {
        var tunnels = Ranges(spline, path);
        var warp = PathDrawContext.Warp;
        GameObject result = null;
        void Draw(double from, double to)
        {
            result = RoadPath.CreateSplineMesh(parent, spline, variantBase + "/platform", 0f, trackOffset,
                RoadPartHeights.PLATFORM_BASE_ABOVE_SPLINE, mirror, typeBase + "/platform",
                stretchToWidth: false, clipFrom: from, clipTo: to, compensatePivot: false, maxTileLength: 0f);
        }
        foreach (var ground in Outside(0, 1, tunnels)) Draw(ground.From, ground.To);
        try
        {
            PathDrawContext.Warp = new PartWarp { Shift = warp?.Shift, Scale = warp?.Scale, SourceMesh = WithoutPlatformRoof };
            foreach (var tunnel in tunnels) Draw(tunnel.From, tunnel.To);
        }
        finally { PathDrawContext.Warp = warp; }
        return result;
    }

    public static Mesh WithoutPlatformRoof(Mesh source)
    {
        if (PlatformsWithoutRoof.TryGetValue(source, out var cached) && cached != null) return cached;
        var vertices = source.vertices;
        var parents = new int[vertices.Length];
        int Root(int vertex)
        {
            while (parents[vertex] != vertex) { parents[vertex] = parents[parents[vertex]]; vertex = parents[vertex]; }
            return vertex;
        }
        void Join(int a, int b) => parents[Root(a)] = Root(b);
        var positions = new Dictionary<Vector3, int>();
        for (int i = 0; i < vertices.Length; i++)
        {
            parents[i] = i;
            // Treat colocated vertices split by FBX normals/UV boundaries as the same member.
            if (positions.TryGetValue(vertices[i], out int other)) Join(i, other);
            else positions[vertices[i]] = i;
        }
        var triangles = new int[source.subMeshCount][];
        for (int sub = 0; sub < triangles.Length; sub++)
        {
            triangles[sub] = source.GetTriangles(sub);
            var indices = triangles[sub];
            for (int i = 0; i < indices.Length; i += 3) { Join(indices[i], indices[i + 1]); Join(indices[i], indices[i + 2]); }
        }
        var roof = new bool[vertices.Length];
        for (int i = 0; i < vertices.Length; i++) if (vertices[i].y > PlatformRoofHeight) roof[Root(i)] = true;
        var mesh = UnityEngine.Object.Instantiate(source);
        mesh.name = source.name + "_WithoutRoof";
        Bounds bounds = default;
        bool hasBounds = false;
        for (int sub = 0; sub < triangles.Length; sub++)
        {
            var remaining = new List<int>();
            var indices = triangles[sub];
            for (int i = 0; i < indices.Length; i += 3)
            {
                if (roof[Root(indices[i])]) continue;
                for (int j = 0; j < 3; j++)
                {
                    int vertex = indices[i + j];
                    remaining.Add(vertex);
                    if (hasBounds) bounds.Encapsulate(vertices[vertex]);
                    else { bounds = new Bounds(vertices[vertex], Vector3.zero); hasBounds = true; }
                }
            }
            mesh.SetTriangles(remaining, sub, false);
        }
        mesh.bounds = bounds;
        PlatformsWithoutRoof[source] = mesh;
        return mesh;
    }

    public static GameObject ResolveDefaultPart(string name)
    {
        if (name != null && name.EndsWith("/" + CeilingPart, StringComparison.Ordinal))
        {
            if (defaultCeiling != null) return defaultCeiling;
            var mesh = new Mesh { name = "DefaultTunnelCeiling" };
            mesh.vertices = new[] { new Vector3(-.5f,0,-.5f), new Vector3(-.5f,0,.5f), new Vector3(.5f,0,.5f), new Vector3(.5f,0,-.5f) };
            // Face winding as well as normals downward, allowing backface-culling transparency from above.
            mesh.triangles = new[] { 0,2,1,0,3,2 };
            mesh.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            defaultCeiling = new GameObject("DefaultTunnelCeiling");
            defaultCeiling.hideFlags = HideFlags.HideAndDontSave;
            defaultCeiling.SetActive(false);
            defaultCeiling.AddComponent<MeshFilter>().sharedMesh = mesh;
            defaultCeiling.AddComponent<MeshRenderer>().sharedMaterial = RoadPath.GetOrCreateRoadMaterialByName("PATH_CONCRETE");
            defaultCeiling.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;
            return defaultCeiling;
        }
        return ResolveDefaultWall(name);
    }

    /// <summary>
    /// Preserve tunnel sides without dedicated models. Extend up to ceiling clearance.
    /// Shared source meshes are not destroyed with paths; pass through normal spline/LOD pipelines.
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
