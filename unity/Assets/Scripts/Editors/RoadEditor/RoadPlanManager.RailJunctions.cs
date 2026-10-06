using System.Collections.Generic;
using UnityEngine;

public partial class RoadPlanManager
{
    private bool IsRailJunctionTarget => PathType == Constants.PATH_TRACK || PathType == Constants.PATH_MONORAIL;

    private bool TryProjectRailTarget(Vector3 position, float tolerance,
        out Vector3 point, out Vector3 tangent, out GeneratedModels.PathSummary target, bool useOffsets = false)
    {
        point = position;
        tangent = Vector3.zero;
        target = null;
        if (pathManager == null) return false;
        var seen = new HashSet<int>();
        float best = tolerance;
        float radius = snapThreshold;
        if (useOffsets && LateralOffsets != null)
            foreach (float offset in LateralOffsets) radius = Mathf.Max(radius, snapThreshold + Mathf.Abs(offset));
        var segments = pathManager.QueryNearbySegments(position, radius);
        if (segments == null) return false;
        foreach (var segment in segments)
        {
            var path = segment.path;
            if (path == null || path.path_id == pathId || path.path_type != PathType || !seen.Add(path.path_id)) continue;
            if (!RailJunctionGeometry.TryProject(path, position, out Vector3 projected, out Vector3 direction)) continue;
            if (!IsRailSnapPointVisible(path, projected)) continue;
            var offsets = useOffsets && LateralOffsets != null && LateralOffsets.Length > 0 ? LateralOffsets : new[] { 0f };
            Vector3 normal = new Vector3(-direction.z, 0, direction.x);
            foreach (float offset in offsets)
            for (int sign = -1; sign <= 1; sign += 2)
            {
                Vector3 center = projected + normal * offset * sign;
                float distance = Utils.XZDistance(center, position);
                if (distance > best) continue;
                best = distance;
                point = center;
                tangent = direction;
                target = path;
            }
        }
        return target != null;
    }

    private bool IsRailSnapPointVisible(GeneratedModels.PathSummary path, Vector3 position)
    {
        if (!UndergroundView.IsActive) return true;
        // 解析射影のYは0なので、描画と共有する中心線で深さを判定する。
        // 相対高さ0の非追従線路が丘に潜る場合も、アンカーの高さだけで地上と決めない。
        if (!pathManager.TryGetUntrimmedPolylineShared(path.path_id, out var points)) return false;
        float best = float.PositiveInfinity;
        Vector3 closest = position;
        for (int i = 1; i < points.Count; i++)
        {
            Vector3 candidate = PolylineUtils.ClosestPointOnSegmentXZ(position, points[i - 1], points[i]);
            float distance = Utils.XZDistanceSquared(position, candidate);
            if (distance < best) { best = distance; closest = candidate; }
        }
        return best < float.PositiveInfinity && TerrainInputHandler.IsPathSnapPointVisible(path, closest);
    }

    private bool TryGetRailHandleDirection(Vector3 position, Vector3 current, out Vector3 direction)
    {
        direction = Vector3.zero;
        if (!TryProjectRailTarget(position, Constants.RAIL_JUNCTION_POSITION_TOLERANCE,
            out _, out Vector3 tangent, out _, useOffsets: true)) return false;
        direction = Vector3.Dot(current, tangent) >= 0 ? tangent : -tangent;
        return true;
    }

    /// <summary>分岐・合流の接点を通り、本線の接線方向にだけ本線の勾配で傾く面。</summary>
    private struct JunctionPlane
    {
        public Vector3 origin;
        public Vector3 tangent;
        public float grade;

        public float HeightAt(Vector3 position) => origin.y + grade * Vector3.Dot(position - origin, tangent);
    }

    /// <summary>
    /// 線路の分岐・合流端から RAIL_JUNCTION_PLANE_LENGTH の区間に掛かるアンカーの高さを、本線の面に揃える。
    /// バックエンドはこの区間の側線が本線と共通の平面に収まることを確定の条件にしており(geo_rail_junctions)、
    /// 高さを自由にすると手で面に合わせることになるため。端から測った距離がこの長さに届く前に始まる区間の
    /// 先側アンカーが対象で、2点目は常に含む。距離はアンカー間の弦で測る。弦は曲線長以下なので、
    /// 境界付近では1点多く拘束する側に倒れる。高さを編集できないバリアントの縦断は地形と接続補正で決まるので対象外。
    /// アンカーの追加・移動・削除の直後、制御点の再計算の前に呼ぶ。既存パスの復元と経路提案の適用では呼ばず、
    /// 保存・提案された形状を次の編集まで変えない。
    /// </summary>
    private void ConstrainJunctionPlaneHeights()
    {
        if (!IsRailJunctionTarget || !IsHeightEditable() || points.Count < 2) return;
        int last = points.Count - 1;
        JunctionPlane endPlane = default;
        bool start = TryGetJunctionPlane(0, out JunctionPlane startPlane);
        // 配置中の末尾は終点ではない(終点は形状の確定で決まる)
        bool end = points[last].role == PathPointRole.End && TryGetJunctionPlane(last, out endPlane);
        if (start) ConstrainToJunctionPlane(0, 1, startPlane, end ? last : -1);
        if (end) ConstrainToJunctionPlane(last, -1, endPlane, start ? 0 : -1);
    }

    /// <summary>端 end から step の向きへ、区間の始まりが RAIL_JUNCTION_PLANE_LENGTH に届くまでのアンカーを面に揃える。stop は反対側の接点。</summary>
    private void ConstrainToJunctionPlane(int end, int step, JunctionPlane plane, int stop)
    {
        float distance = 0f;
        for (int i = end + step; i >= 0 && i < points.Count && i != stop
            && distance < Constants.RAIL_JUNCTION_PLANE_LENGTH; i += step)
        {
            distance += Utils.XZDistance(points[i - step].position, points[i].position);
            var point = points[i];
            float height = plane.HeightAt(point.position);
            if (Mathf.Abs(point.position.y - height) <= 1e-4f) continue;
            point.position.y = height;
            points[i] = point;
            // アンカーを動かしたときと同じく、自動の縦断勾配が変わる両隣までの高さ固定を解除する
            ReleaseControlHeights(i - 1, i + 1);
        }
    }

    /// <summary>
    /// 端のアンカー index が同種の線路へ同じ高さで接し、分岐・合流になっているなら、その本線の面を返す。
    /// 端点どうしを逆向きにつなぐ単純延長はバックエンドも共通平面を求めないので対象外。節点で2本に分かれた
    /// 本線では、延長にならない側を本線とする。
    /// </summary>
    private bool TryGetJunctionPlane(int index, out JunctionPlane plane)
    {
        plane = default;
        if (pathManager == null) return false;
        Vector3 position = points[index].position;
        Vector3 inward = points[index == 0 ? 1 : index - 1].position - position;
        inward.y = 0f;
        var segments = pathManager.QueryNearbySegments(position, snapThreshold);
        if (segments == null) return false;
        var seen = new HashSet<int>();
        foreach (var segment in segments)
        {
            var path = segment.path;
            if (path == null || path.path_id == pathId || path.path_type != PathType || !seen.Add(path.path_id)) continue;
            if (!RailJunctionGeometry.TryProject(path, position, out Vector3 contact, out Vector3 tangent)) continue;
            if (Utils.XZDistance(contact, position) > Constants.RAIL_JUNCTION_POSITION_TOLERANCE) continue;
            // 平面位置だけ重なる立体交差は接続ではない。許容幅はバックエンドの接続補正と同じ。
            if (Mathf.Abs(RailSurfaceHeight(path, contact) - position.y) > RoadShapeGeometry.AbsoluteHeightTolerance) continue;
            if (IsRailExtension(path, contact, tangent, inward)) continue;
            plane = new JunctionPlane { origin = position, tangent = tangent, grade = RailGrade(path, contact, tangent) };
            return true;
        }
        return false;
    }

    /// <summary>接点が相手の端点にあり、互いに反対へ伸びるか。解析接線は始点から終点へ向くので、相手の内向きは始点で接線、終点で逆向き。</summary>
    private static bool IsRailExtension(GeneratedModels.PathSummary path, Vector3 contact, Vector3 tangent, Vector3 inward)
    {
        var start = path.GetStartPointList();
        var end = path.GetEndPointList();
        if (start != null && start.Count >= 2 && Utils.XZDistance(contact, new Vector3(start[0], 0f, start[start.Count - 1]))
            <= Constants.RAIL_JUNCTION_POSITION_TOLERANCE) return Vector3.Dot(tangent, inward) < 0f;
        if (end != null && end.Count >= 2 && Utils.XZDistance(contact, new Vector3(end[0], 0f, end[end.Count - 1]))
            <= Constants.RAIL_JUNCTION_POSITION_TOLERANCE) return Vector3.Dot(-tangent, inward) < 0f;
        return false;
    }

    /// <summary>線路の路面の絶対標高。吸着と同じ高さ源(保存設計標高、無ければアンカー相対Yの補間)を使う。</summary>
    private static float RailSurfaceHeight(GeneratedModels.PathSummary path, Vector3 position)
    {
        return Utils.GetGroundHeight(position) + path.GetRelativeHeightAtPoint(position.x, position.z);
    }

    /// <summary>本線の接線方向の勾配。バックエンドが平面を当てはめる接点の前後 RAIL_JUNCTION_PLANE_LENGTH の割線で測る。</summary>
    private static float RailGrade(GeneratedModels.PathSummary path, Vector3 contact, Vector3 tangent)
    {
        float length = Constants.RAIL_JUNCTION_PLANE_LENGTH;
        RailJunctionGeometry.TryProject(path, contact + tangent * length, out Vector3 ahead, out _);
        RailJunctionGeometry.TryProject(path, contact - tangent * length, out Vector3 behind, out _);
        float run = Vector3.Dot(ahead - behind, tangent);
        return run > 1e-3f ? (RailSurfaceHeight(path, ahead) - RailSurfaceHeight(path, behind)) / run : 0f;
    }
}
