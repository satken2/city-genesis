using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using GeneratedModels;

public static class IntersectionPolygonUtil
{
    private const float MinDirectionSqrMagnitude = 1e-6f;

    // a, b: (x, z) を Vector2 で持つと仮定
    private static Polygon MakeRoadRect(Vector2 a, Vector2 b, float width, GeometryFactory gf)
    {
        float ax = a.x;
        float az = a.y;
        float bx = b.x;
        float bz = b.y;

        float dx = bx - ax;
        float dz = bz - az;
        float length = Mathf.Sqrt(dx * dx + dz * dz);
        if (length == 0f)
        {
            throw new ArgumentException("startPoint と endPoint が同じです。");
        }

        float ux = dx / length;
        float uz = dz / length;
        float nx = -uz;
        float nz = ux;
        float hw = width * 0.5f;

        var c1 = new Coordinate(ax + nx * hw, az + nz * hw);
        var c2 = new Coordinate(ax - nx * hw, az - nz * hw);
        var c3 = new Coordinate(bx - nx * hw, bz - nz * hw);
        var c4 = new Coordinate(bx + nx * hw, bz + nz * hw);

        // ポリゴン閉じるために先頭を最後にも入れる
        var coords = new[]
        {
            c1, c2, c3, c4, c1
        };

        return gf.CreatePolygon(coords);
    }

    public class IntersectionCalculationResult
    {
        public Geometry Geometry { get; set; }
        public Dictionary<int, Vector2> ConnectionPoints { get; set; } = new Dictionary<int, Vector2>();
    }

    /// <summary>
    /// 交差点ポリゴン生成
    /// 戻り値は Polygon または MultiPolygon になり得るので Geometry を返す。
    /// </summary>
    public static IntersectionCalculationResult ComputeIntersectionPolygon(
        int intersectionNodeId,
        Vector2 intersectionPos,   // (x, z)
        List<PathSummary> paths,
        GeometryFactory gf,
        Func<PathSummary, float> widthResolver = null
    )
    {
        double ix = intersectionPos.x;
        double iz = intersectionPos.y;

        // 交差点に接続している道路だけ抽出
        var connectedPaths = new List<PathSummary>();
        foreach (var p in paths)
        {
            if (p.start_node_id == intersectionNodeId || p.end_node_id == intersectionNodeId)
            {
                connectedPaths.Add(p);
            }
        }

        if (connectedPaths.Count < 2)
        {
            throw new ArgumentException("少なくとも2本の道路が必要です。");
        }

        // (PathSummary, 交差点側, 交差点側とは反対の端, 道路ポリゴン)
        var roadPolys = new List<(PathSummary path, Vector2 intersectionSide, Vector2 outerSide, float width, Polygon poly)>();
        foreach (var p in connectedPaths)
        {
            float resolvedWidth = widthResolver != null ? widthResolver(p) : p.width;
            if (resolvedWidth <= 0f)
            {
                continue;
            }

            // 交差点中心座標（始点 or 終点）と、その直近の点（ウェイポイント or 反対側の端点）を取得して
            // 道路の進入角度を決定する。
            Vector2 intersectionSide;
            Vector2 directionPoint; // 角度計算用の参照点

            // 端点の座標
            var sp = p.GetStartPointList();
            var ep = p.GetEndPointList();
            if (!TryGetPoint2D(sp, out Vector2 pStart) || !TryGetPoint2D(ep, out Vector2 pEnd))
            {
                Debug.LogWarning($"[IntersectionPolygonUtil] Node {intersectionNodeId}: path {p.path_id} has invalid endpoints. skipped.");
                continue;
            }

            if (p.start_node_id == intersectionNodeId)
            {
                intersectionSide = pStart;
                directionPoint = TryFindWaypointDirectionPoint(p.GetWaypointList(), intersectionSide, true, out Vector2 startWaypointPoint) ? startWaypointPoint : pEnd;
            }
            else
            {
                intersectionSide = pEnd;
                directionPoint = TryFindWaypointDirectionPoint(p.GetWaypointList(), intersectionSide, false, out Vector2 endWaypointPoint) ? endWaypointPoint : pStart;
            }

            // 進行方向ベクトル
            Vector2 dir = directionPoint - intersectionSide;
            // ウェイポイントがノードと同じ座標に重なる場合は、元の端点方向を使う。
            if (dir.sqrMagnitude < MinDirectionSqrMagnitude)
            {
                if (p.start_node_id == intersectionNodeId)
                {
                    dir = pEnd - pStart;
                }
                else
                {
                    dir = pStart - pEnd;
                }
            }

            if (dir.sqrMagnitude < MinDirectionSqrMagnitude)
            {
                Debug.LogWarning($"[IntersectionPolygonUtil] Node {intersectionNodeId}: path {p.path_id} has no valid direction. skipped.");
                continue;
            }
            dir.Normalize();

            // 矩形生成用の「外側の点」を計算
            // ウェイポイントが交差点に近い場合、そのまま使うと矩形が短くなりすぎて交差判定に失敗する可能性があるため、
            // 元の道路長（あるいは十分な長さ）まで延長した点を使用する。
            float originalLength = Vector2.Distance(pStart, pEnd);
            // 交差計算のために最低限の長さを確保 (例えば道路幅の数倍あれば確実だが、元の長さがあれば概ねOK)
            float length = Mathf.Max(originalLength, resolvedWidth * 5f);
            
            Vector2 outerSide = intersectionSide + dir * length;

            var poly = MakeRoadRect(intersectionSide, outerSide, resolvedWidth, gf);
            roadPolys.Add((p, intersectionSide, outerSide, resolvedWidth, poly));
        }

        if (roadPolys.Count < 2)
        {
            throw new ArgumentException("交差点計算に使える道路方向が2本未満です。");
        }

        // 全ての道路ペアで交差領域を取る
        var pairInters = new List<Geometry>();
        int n = roadPolys.Count;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                // 3本以上の道路が集まる交差点において、ほぼ一直線に並ぶ道路ペアの交差判定を除外する。
                // わずかに曲がった直進道路（例えば179.9度）の交差領域は無限遠まで伸びる超巨大な平行四辺形となり、
                // それが交差点全体のサイズを異常に広げてしまうバグを防ぐため。
                if (n >= 3)
                {
                    Vector2 dirI = (roadPolys[i].outerSide - roadPolys[i].intersectionSide).normalized;
                    Vector2 dirJ = (roadPolys[j].outerSide - roadPolys[j].intersectionSide).normalized;
                    if (Vector2.Dot(dirI, dirJ) < -0.95f)
                    {
                        continue;
                    }
                }

                var polyI = roadPolys[i].poly;
                var polyJ = roadPolys[j].poly;

                var inter = polyI.Intersection(polyJ);
                if (!inter.IsEmpty && inter.Area > 1e-6)
                {
                    pairInters.Add(inter);
                }
            }
        }

        var result = new IntersectionCalculationResult();

        // 1つも重なりがなければ交点のみ
        if (pairInters.Count == 0)
        {
            Debug.LogWarning($"[IntersectionPolygonUtil] Node {intersectionNodeId}: no pair intersections, returning point.");
            result.Geometry = gf.CreatePoint(new Coordinate(ix, iz));
            // 接続点は計算できない（あるいは交差点中心とするか）が、
            // 重なりがない＝交差点として成立していない可能性が高いので空で返す
            return result;
        }

        // unary_union 相当（順次 Union）
        Geometry overlapRegion = pairInters[0];
        for (int i = 1; i < pairInters.Count; i++)
        {
            overlapRegion = overlapRegion.Union(pairInters[i]);
        }

        var clippedRoads = new List<Geometry>();
        var terminalCorners = new List<Vector2>();

        foreach (var rp in roadPolys)
        {
            var p = rp.path;
            var intersectionSide = rp.intersectionSide;
            var outerSide = rp.outerSide;
            var poly = rp.poly;

            var inter = poly.Intersection(overlapRegion);
            if (inter.IsEmpty || inter.Area < 1e-6)
            {
                continue;
            }

            // 交差点側の端点を start に固定
            Vector2 start = intersectionSide;
            Vector2 end = outerSide;

            double sx = start.x;
            double sz = start.y;
            double ex = end.x;
            double ez = end.y;

            double vx = ex - sx;
            double vz = ez - sz;
            double vlen2 = vx * vx + vz * vz;
            if (vlen2 == 0.0)
            {
                continue;
            }

            double vlen = Math.Sqrt(vlen2);
            double maxT = 0.0;

            // 交差領域の各頂点を道路方向に射影して最大距離を取る
            foreach (var coord in CollectCoords(inter))
            {
                double x = coord.X;
                double z = coord.Y;
                double dx = x - sx;
                double dz = z - sz;
                double t = (dx * vx + dz * vz) / vlen;
                if (t > maxT)
                {
                    maxT = t;
                }
            }

            if (maxT <= 0.0)
            {
                continue;
            }

            double ux = vx / vlen;
            double uz = vz / vlen;
            double nx = -uz;
            double nz = ux;

            double hw = rp.width * 0.5;
            double cutX = sx + ux * maxT;
            double cutZ = sz + uz * maxT;

            var corner1 = new Vector2(
                (float)(cutX + nx * hw),
                (float)(cutZ + nz * hw)
            );
            var corner2 = new Vector2(
                (float)(cutX - nx * hw),
                (float)(cutZ - nz * hw)
            );
            terminalCorners.Add(corner1);
            terminalCorners.Add(corner2);

            // 接続点を記録 (PathID -> Point)
            result.ConnectionPoints[p.path_id] = new Vector2((float)cutX, (float)cutZ);

            // 交差点側だけを残すための半平面ポリゴンを構築
            double L = 1e4;
            var hp1 = new Coordinate(cutX + nx * L, cutZ + nz * L);
            var hp2 = new Coordinate(cutX - nx * L, cutZ - nz * L);
            var hp3 = new Coordinate(cutX - nx * L - ux * L, cutZ - nz * L - uz * L);
            var hp4 = new Coordinate(cutX + nx * L - ux * L, cutZ + nz * L - uz * L);

            var halfPlane = gf.CreatePolygon(new[]
            {
                hp1, hp2, hp3, hp4, hp1
            });

            var clipped = poly.Intersection(halfPlane);
            if (!clipped.IsEmpty && clipped.Area > 1e-6)
            {
                clippedRoads.Add(clipped);
            }
        }

        // クリップ済み道路群を union
        Geometry basePoly;
        if (clippedRoads.Count == 0)
        {
            basePoly = overlapRegion;
        }
        else
        {
            basePoly = clippedRoads[0];
            for (int i = 1; i < clippedRoads.Count; i++)
            {
                basePoly = basePoly.Union(clippedRoads[i]);
            }
        }

        // 終端面の外周多角形を凸包として union する。
        // 交差点は各道路が重なった部分だけを足し合わせると内側に凹みが残りやすい。
        // 接続断面の両端点群を凸包化して重ねることで、道路同士の間に発生する隙間を埋める。
        if (terminalCorners.Count >= 3)
        {
            // Python 版と同様に安定ソートとなるよう OrderBy を使用し、角度が同じ点は半径・追加順で整列
            var sortedCorners = terminalCorners
                .Select((corner, index) =>
                {
                    double dxCorner = corner.x - ix;
                    double dzCorner = corner.y - iz;
                    double angle = Math.Atan2(dzCorner, dxCorner);
                    double radiusSq = dxCorner * dxCorner + dzCorner * dzCorner;
                    return new
                    {
                        Corner = corner,
                        Angle = angle,
                        RadiusSq = radiusSq,
                        Index = index
                    };
                })
                .OrderByDescending(item => item.Angle)
                .ThenByDescending(item => item.RadiusSq)
                .ThenBy(item => item.Index)
                .Select(item => item.Corner)
                .ToList();

            var coords = new Coordinate[sortedCorners.Count + 1];
            for (int i = 0; i < sortedCorners.Count; i++)
            {
                coords[i] = new Coordinate(sortedCorners[i].x, sortedCorners[i].y);
            }
            coords[coords.Length - 1] = new Coordinate(coords[0].X, coords[0].Y);

            var endPolygon = gf.CreatePolygon(coords);
            if (!endPolygon.IsEmpty && endPolygon.IsValid && endPolygon.Area > 1e-6)
            {
                basePoly = basePoly.Union(endPolygon).ConvexHull();
            }
        }

        result.Geometry = basePoly;
        return result;
    }

    private static bool TryGetPoint2D(List<float> point, out Vector2 result)
    {
        result = Vector2.zero;
        if (point == null || point.Count < 2)
        {
            return false;
        }

        float z = point.Count >= 3 ? point[2] : point[1];
        result = new Vector2(point[0], z);
        return true;
    }

    private static bool TryFindWaypointDirectionPoint(List<PathWaypoint> waypoints, Vector2 intersectionSide, bool fromStart, out Vector2 result)
    {
        result = Vector2.zero;
        if (waypoints == null || waypoints.Count == 0)
        {
            return false;
        }

        int index = fromStart ? 0 : waypoints.Count - 1;
        int step = fromStart ? 1 : -1;
        while (index >= 0 && index < waypoints.Count)
        {
            if (TryGetWaypointPoint2D(waypoints[index], out Vector2 point) && (point - intersectionSide).sqrMagnitude >= MinDirectionSqrMagnitude)
            {
                result = point;
                return true;
            }
            index += step;
        }

        return false;
    }

    private static bool TryGetWaypointPoint2D(PathWaypoint waypoint, out Vector2 result)
    {
        result = Vector2.zero;
        if (waypoint == null || waypoint.position == null || waypoint.position.Length < 2)
        {
            return false;
        }

        float z = waypoint.position.Length >= 3 ? waypoint.position[2] : waypoint.position[1];
        result = new Vector2(waypoint.position[0], z);
        return true;
    }

    /// <summary>
    /// Shapely 版 collect_coords に相当
    /// Geometry の頂点列を列挙
    /// </summary>
    private static IEnumerable<Coordinate> CollectCoords(Geometry geom)
    {
        // Polygon, LineString 等の場合はそのまま座標列を返す
        if (geom is Polygon || geom is LineString)
        {
            foreach (var c in geom.Coordinates)
            {
                yield return c;
            }
        }
        // MultiPolygon, GeometryCollection の場合は再帰
        else if (geom is MultiPolygon || geom is GeometryCollection)
        {
            for (int i = 0; i < geom.NumGeometries; i++)
            {
                var g = geom.GetGeometryN(i);
                foreach (var c in CollectCoords(g))
                {
                    yield return c;
                }
            }
        }
        else
        {
            // その他はひとまず Coordinates をそのまま出す
            foreach (var c in geom.Coordinates)
            {
                yield return c;
            }
        }
    }
}
