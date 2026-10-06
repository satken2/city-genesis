using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>干渉警告用の占有箱。道路面から上空のクリアランスを含めて可視化する。</summary>
public static class OccupancyVolumeUtil
{
    public static readonly Color WarningColor = new Color(1f, 0.12f, 0.12f, 0.1f);
    // 自動買収で市が取り壊す民間建物の色。区画の「削除で着工可」と同じ橙系
    public static readonly Color AcquiredBuildingColor = new Color(1f, 0.55f, 0.1f, 0.1f);

    /// <summary>
    /// 干渉建物の占有箱を、取り壊しの扱いで色分けして溜める。
    /// 公共施設は敷地が市有地で自動買収の対象外のため、プレイヤーが取り壊すまで着工できない(赤)。
    /// 民間の建物は用地の自動買収が成立すると市が取り壊す(橙)。
    /// </summary>
    public sealed class BuildingVolumes
    {
        private readonly List<Vector3> publicFacilities = new List<Vector3>();
        private readonly List<Vector3> privateBuildings = new List<Vector3>();

        public bool IsEmpty => publicFacilities.Count == 0 && privateBuildings.Count == 0;

        public void Add(BuildingSummary building)
        {
            if (building == null) return;
            AddBuilding(building.public_facility ? publicFacilities : privateBuildings, building);
        }

        public void Draw(OverlayMeshRenderer renderer)
        {
            renderer.AddPreTriangulated(publicFacilities, WarningColor);
            renderer.AddPreTriangulated(privateBuildings, AcquiredBuildingColor);
        }
    }

    /// <summary>
    /// パスの中心線に沿った占有筒(底面・上面・左右側面)を追加する。進行方向を横切る面は持たない。
    /// 面は両面描画の半透明なので、区間の境目ごとに横板があると重なって中が見えなくなるため。
    /// 左右の縁は道路本体と同じ横オフセット(折れ点はマイター継ぎ)で求める。
    /// </summary>
    public static void AddTube(List<Vector3> triangles, List<Vector3> line, float width, float height)
    {
        if (line == null || line.Count < 2 || width <= 0f || height <= 0f) return;
        var left = RoadRenderer.OffsetPolylineLateral(line, -width * 0.5f);
        var right = RoadRenderer.OffsetPolylineLateral(line, width * 0.5f);
        var up = Vector3.up * height;
        for (int i = 1; i < line.Count; i++)
        {
            AddQuad(triangles, left[i - 1], left[i], right[i], right[i - 1]);
            AddQuad(triangles, left[i - 1] + up, right[i - 1] + up, right[i] + up, left[i] + up);
            AddQuad(triangles, left[i - 1], left[i - 1] + up, left[i] + up, left[i]);
            AddQuad(triangles, right[i - 1], right[i], right[i] + up, right[i - 1] + up);
        }
    }

    /// <summary>a から b へ伸びる、幅 width・高さ height の閉じた直方体を追加する。</summary>
    public static void AddBox(List<Vector3> triangles, Vector3 a, Vector3 b, float width, float height)
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

    private static void AddBuilding(List<Vector3> triangles, BuildingSummary building)
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
        AddBox(triangles, new Vector3((minX + maxX) * 0.5f, ground, minZ),
            new Vector3((minX + maxX) * 0.5f, ground, maxZ), maxX - minX, building.height ?? 0f);
    }

    public static void AddPath(List<Vector3> triangles, PathManager manager, int pathId)
    {
        var path = manager.GetPathData(pathId);
        if (path == null || !manager.TryGetPathPolyline(pathId, out var points, false)) return;
        var master = MasterDataStore.Instance?.GetPathMaster(path.path_variant);
        float height = master?.height ?? 0f;
        var offset = Vector3.up * ((master?.base_height ?? 0f) - RoadPartHeights.ROAD_SPLINE_OFFSET);
        for (int i = 0; i < points.Count; i++)
            points[i] += offset;
        AddTube(triangles, points, manager.GetRoadWidth(path.path_variant), height);
    }
}
