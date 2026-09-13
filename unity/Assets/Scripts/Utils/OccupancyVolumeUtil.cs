using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>干渉警告用の占有箱。道路面から上空のクリアランスを含めて可視化する。</summary>
public static class OccupancyVolumeUtil
{
    public static readonly Color WarningColor = new Color(1f, 0.12f, 0.12f, 0.22f);

    public static void AddSegment(List<Vector3> triangles, Vector3 a, Vector3 b, float width, float height)
    {
        var forward = new Vector3(b.x - a.x, 0f, b.z - a.z);
        if (forward.sqrMagnitude < 0.0001f || width <= 0f || height <= 0f) return;
        var side = Vector3.Cross(Vector3.up, forward.normalized) * (width * 0.5f);
        var up = Vector3.up * height;
        var corners = new[] { a - side, a + side, b + side, b - side,
            a - side + up, a + side + up, b + side + up, b - side + up };
        AddQuad(triangles, corners[0], corners[3], corners[2], corners[1]);
        AddQuad(triangles, corners[4], corners[5], corners[6], corners[7]);
        for (int i = 0; i < 4; i++)
        {
            int next = (i + 1) % 4;
            AddQuad(triangles, corners[i], corners[next], corners[next + 4], corners[i + 4]);
        }
    }

    private static void AddQuad(List<Vector3> triangles, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        triangles.Add(a); triangles.Add(b); triangles.Add(c);
        triangles.Add(a); triangles.Add(c); triangles.Add(d);
    }

    public static void AddBuilding(List<Vector3> triangles, BuildingSummary building)
    {
        var polygon = building?.polygonArray;
        var center = building?.centerPointArray;
        if (polygon == null || polygon.Length < 3 || center == null || center.Length < 2) return;
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        foreach (var point in polygon)
        {
            if (point == null || point.Length < 2) continue;
            minX = Mathf.Min(minX, point[0] + center[0]); maxX = Mathf.Max(maxX, point[0] + center[0]);
            minZ = Mathf.Min(minZ, point[1] + center[1]); maxZ = Mathf.Max(maxZ, point[1] + center[1]);
        }
        if (minX >= maxX || minZ >= maxZ) return;
        float ground = Utils.GetGroundHeight(new Vector3(center[0], 0f, center[1]));
        AddSegment(triangles, new Vector3((minX + maxX) * 0.5f, ground, minZ),
            new Vector3((minX + maxX) * 0.5f, ground, maxZ), maxX - minX, building.height ?? 0f);
    }

    public static void AddPath(List<Vector3> triangles, PathManager manager, int pathId)
    {
        var path = manager.GetPathData(pathId);
        if (path == null || !manager.TryGetPathPolyline(pathId, out var points, false)) return;
        var master = MasterDataStore.Instance?.GetPathMaster(path.path_variant);
        float height = master?.height ?? 0f;
        float offset = (master?.base_height ?? 0f) - RoadPartHeights.ROAD_SPLINE_OFFSET;
        for (int i = 1; i < points.Count; i++)
            AddSegment(triangles, points[i - 1] + Vector3.up * offset, points[i] + Vector3.up * offset,
                manager.GetRoadWidth(path.path_variant), height);
    }
}
