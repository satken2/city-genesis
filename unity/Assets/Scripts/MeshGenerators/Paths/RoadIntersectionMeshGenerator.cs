using UnityEngine;
using System;
using System.Collections.Generic;
using GeneratedModels;
using NetTopologySuite.Geometries;

/// <summary>
/// 道路ノードに作られる交差点メッシュを生成します。
/// 本線の切断断面と隣接入口間の外周を共有し、路面・付帯パーツを接合します。
/// 歩道・壁・中央分離帯の接続または終端を幾何情報として返します。
/// </summary>
public class RoadIntersectionMeshGenerator : INodeMeshGenerator
{
    public NodeMeshResult Generate(int nodeId, Vector3 nodePosition, List<PathSummary> paths, float nodeRelativeY = 0f)
        => GenerateWithSections(nodeId, nodePosition, paths, nodeRelativeY);

    public NodeMeshResult GenerateWithSections(int nodeId, Vector3 nodePosition, List<PathSummary> paths, float nodeRelativeY = 0f, Func<PathSummary, List<Vector3>> pointResolver = null, Func<PathSummary, float> cutLimitResolver = null)
    {
        // 架空ケーブルとパイプラインには接続点を埋める路面がない。切断断面も作らず、各スパンを
        // アンカーまで描く。導水管(PATH_WATER)は地表の管路として従来どおり交差点を描く。
        paths = paths?.FindAll(p => p.path_type != Constants.PATH_CONVEYOR
            && p.path_type != Constants.PATH_ELECTRICITY
            && p.path_type != Constants.PATH_PIPELINE);
        if (paths == null || paths.Count < 2)
        {
            return null;
        }

        try
        {
            var gf = new GeometryFactory();
            Vector2 nodePos2D = new Vector2(nodePosition.x, nodePosition.z);

            // 相対Y≠0のノード（高架同士の平面交差など）は地形をサンプリングせず、
            // ノード絶対Y（nodePosition.y）の水平面として交差点を生成する。
            // 地形基準で生成すると、空中の交差点が地面に描かれ、切断点の代入で
            // 道路端まで地面に引き下げられてしまう。
            bool horizontalPlane = nodeRelativeY != 0f;

            MotorwayMergeLayout merge = null;
            var roadResult = IntersectionPolygonUtil.ComputeIntersectionPolygon(
                nodeId,
                nodePos2D,
                paths,
                gf,
                ResolveRoadWidth, pointResolver, cutLimitResolver,
                result => merge = MotorwayMergeLayout.Create(nodeId, paths, result, pointResolver)
            );

            if (roadResult.RequiresMoreSpace)
            {
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[Intersection] node={nodeId}: 接合区間が不足するため道路の切断を行いません。");
                return null;
            }
            Dictionary<int, Vector3> connectionPoints = BuildSectionPoints(roadResult.Sections, nodePosition, horizontalPlane, pointResolver != null);
            Mesh roadMesh = ConvertGeometryToMesh(roadResult.Geometry, nodePosition, horizontalPlane, "RoadIntersection");
            if (roadMesh == null)
            {
                return null;
            }

            Material roadMaterial = LoadRoadMaterial(paths);
            foreach (var path in paths)
            {
                if (!roadResult.Sections.TryGetValue(path.path_id, out var section)) continue;
                GameObject body = ModelManager.Instance?.GetModel($"Models/Paths/p{path.path_variant}/body")
                    ?? ModelManager.Instance?.GetModel($"Models/Paths/p{path.path_type}/body");
                var source = body != null ? body.GetComponent<MeshFilter>()?.sharedMesh : null;
                section.BodyDepth = source != null ? source.bounds.size.y : 0f;
            }

            List<SidewalkConnectorSpec> sidewalkConnectors = null;
            Dictionary<int, SidewalkSetback> sidewalkSetbacks = null;
            if (GetSidewalkPaths(paths).Count >= 1)
            {
                // 歩道は車道交差点のような一枚板ではなく、通常道路で使っている sidewalk プレハブを
                // 隣り合う道路の歩道端を結ぶ短いスプラインへ流し込む。これにより、歩道プレハブの形状、
                // 厚み、マテリアル、UV の扱いを通常道路と揃えられる。
                // 歩道が1本しかない場合でもエンドキャップ（丸め終端）を生成するため、1本から呼ぶ。
                sidewalkConnectors = BuildSidewalkConnectorSpecs(nodeId, nodePosition, horizontalPlane, paths, roadResult.ConnectionPoints, out sidewalkSetbacks, roadResult.Sections, roadResult.Corners);
            }

            sidewalkConnectors ??= new List<SidewalkConnectorSpec>();
            BuildBoundaryParts(nodeId, nodePosition, horizontalPlane, paths, roadResult.Sections, sidewalkConnectors, roadResult.Corners);
            merge?.AddMarkings(sidewalkConnectors, nodePosition);
            // 線路（PATH_TRACK/PATH_MONORAIL）が2本以上接続するノードでは、
            // ポイント（分岐器）・平面交差の表現として、転回制約を満たす通行可能ペアにのみ
            // レールコネクタを敷く（TrackIntersectionMeshGenerator参照）。
            List<RailConnectorSpec> railConnectors = TrackIntersectionMeshGenerator.BuildRailConnectorSpecs(nodeId, nodePosition, horizontalPlane, paths, roadResult.ConnectionPoints, roadResult.Sections);

            sidewalkConnectors.RemoveAll(spec => spec.Part == "wall" && WallBlocksRail(spec, railConnectors));
            var meshVertices = roadMesh.vertices;
            for (int i = 0; i < meshVertices.Length; i++)
            {
                Vector3 world = meshVertices[i] + nodePosition;
                meshVertices[i] = SectionSurfacePoint(world, roadResult.Sections, RoadPartHeights.BODY_ABOVE_SPLINE) - nodePosition;
            }
            roadMesh.vertices = meshVertices;
            roadMesh.RecalculateNormals();
            roadMesh.RecalculateBounds();
            merge?.SetSurface(roadMesh, nodePosition);
            int surfaceVertexCount = roadMesh.vertexCount;
            AddBodyShell(roadMesh, nodePosition, roadResult.Sections);
            RoadMarkingPlan markings = null;
            if (paths.Exists(RoadMarkingRules.HasPaint))
            {
                // 混在ノードが未舗装・石畳を代表材質に選んだ場合、その専用の縁や投影を維持する。
                string surfaceShader = roadMaterial != null ? roadMaterial.shader.name : "";
                if (surfaceShader != "Custom/PathDirt" && surfaceShader != "Custom/PathPaving")
                {
                    markings = RoadMarkingRules.Intersection(sidewalkConnectors);
                    var coordinates = new List<Vector4>();
                    var vertices = roadMesh.vertices;
                    for (int i = 0; i < vertices.Length; i++)
                        coordinates.Add(new Vector4(vertices[i].x, vertices[i].z, i < surfaceVertexCount ? 1 : 0, 0));
                    roadMesh.SetUVs(3, coordinates);
                }
                sidewalkConnectors.RemoveAll(spec => spec.Part == "line");
            }
            if (sidewalkConnectors != null) foreach (var connector in sidewalkConnectors)
            {
                connector.StartNormal = ClosestSectionNormal(connector.Points[0] + nodePosition, roadResult.Sections);
                connector.EndNormal = ClosestSectionNormal(connector.Points[connector.Points.Count - 1] + nodePosition, roadResult.Sections);
                AdjustConnectorHeight(connector.Points, nodePosition, roadResult.Sections, connector.Part == "line" ? RoadPartHeights.LINE_ABOVE_SPLINE : RoadPartHeights.SIDEWALK_ABOVE_SPLINE, merge);
            }
            if (railConnectors != null) foreach (var connector in railConnectors)
            {
                AdjustConnectorHeight(connector.Points, nodePosition, roadResult.Sections, RoadPartHeights.TRACK_ABOVE_SPLINE);
                if (connector.EmbeddedLrt)
                    for (int i = 0; i < connector.Points.Count; i++)
                    {
                        float t = (float)i / (connector.Points.Count - 1);
                        connector.Points[i] += Vector3.up * (Mathf.Lerp(connector.StartRailBase, connector.EndRailBase, t * t * (3 - 2 * t)) - RoadPartHeights.TRACK_ABOVE_SPLINE);
                    }
            }
            return new NodeMeshResult(roadMesh, roadMaterial, connectionPoints, sidewalkConnectors, railConnectors, sidewalkSetbacks) { MotorwayMerge = merge, Markings = markings };
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RoadIntersectionMeshGenerator] ノード {nodeId} の交差点メッシュ生成に失敗: {ex}");
            return null;
        }
    }

    /// <summary>
    /// 交差点の高さの基準面（＝道路のスプライン点が置かれる平面）を求める。
    /// 地表の交差点では各地点の地形標高、高架ノードではノード絶対Yの水平面を基準にし、
    /// どちらも道路のスプライン点と同じ ROAD_SPLINE_OFFSET を加算する
    /// （道路の端点は PathManager.GetPathPointPosition で「地形+相対Y+ROAD_SPLINE_OFFSET」に
    /// 置かれるため、高架でもこのオフセットを揃える必要がある）。
    /// </summary>
    internal static float SampleBaseSurfaceY(float x, float z, Vector3 nodePosition, bool horizontalPlane)
    {
        float baseY = horizontalPlane
            ? nodePosition.y
            : Utils.GetGroundHeight(new Vector3(x, 1000f, z));
        return baseY + RoadPartHeights.ROAD_SPLINE_OFFSET;
    }

    private static SidewalkConnectorSpec BuildEdgeConnector(IntersectionCorner corner, float aWidth, float bWidth,
        int aVariant, int aType, int bVariant, int bType, Vector3 node, bool horizontalPlane, string part)
    {
        var points = new List<Vector3>(); var widths = new List<float>(); var directions = new List<Vector2>();
        for (int i = 0; i < corner.Points.Count; i++)
        {
            float t = (float)i / (corner.Points.Count - 1);
            float width = Mathf.Lerp(aWidth, bWidth, t*t*(3f-2f*t));
            Vector2 direction = corner.Directions[i];
            Vector2 outward = new Vector2(direction.y, -direction.x);
            Vector2 center = corner.Points[i] + outward * width * (part == "sidewalk" ? -0.5f : 0.5f);
            points.Add(ToLocalTerrainPoint(center, node, horizontalPlane)); widths.Add(width); directions.Add(direction);
        }
        if (part == "sidewalk")
        {
            points.Reverse(); widths.Reverse(); directions.Reverse();
            for (int i = 0; i < directions.Count; i++) directions[i] = -directions[i];
            (aVariant,bVariant) = (bVariant,aVariant); (aType,bType) = (bType,aType);
        }
        return new SidewalkConnectorSpec(points,widths,aVariant,aType) { Part=part, Directions=directions,
            StartDirection=directions[0], EndDirection=directions[directions.Count-1], EndPrefabVariant=bVariant, EndFallbackType=bType };
    }
    private static float ResolveWallWidth(PathSummary path, int nodeId, bool outwardLeft)
    {
        var master = MasterDataStore.Instance?.GetPathMaster(path.path_variant);
        if (TrackIntersectionMeshGenerator.IsTrackType(path.path_type))
        {
            TrackPath.ResolvePlatformExtents(path, master?.platform_width ?? 0f, out float left, out float right);
            bool splineLeft = path.start_node_id == nodeId ? outwardLeft : !outwardLeft;
            if ((splineLeft ? left : right) > 0f) return 0f;
        }
        return Mathf.Max(0f, master?.wall_width ?? 0f);
    }

    private static bool WallBlocksRail(SidewalkConnectorSpec wall, List<RailConnectorSpec> rails)
    {
        if (rails == null) return false;
        foreach (var rail in rails) foreach (var point in wall.Points)
            for (int i = 1; i < rail.Points.Count; i++)
            {
                Vector2 a = IntersectionPolygonUtil.XZ(rail.Points[i-1]), b = IntersectionPolygonUtil.XZ(rail.Points[i]);
                Vector2 wallPoint = IntersectionPolygonUtil.XZ(point);
                if (Vector2.Distance(wallPoint, PolylineUtils.ClosestPointOnSegment(wallPoint, a, b)) < TrackPath.PLATFORM_EDGE_OFFSET) return true;
            }
        return false;
    }
    private static void BuildBoundaryParts(int nodeId, Vector3 node, bool horizontalPlane, List<PathSummary> paths,
        Dictionary<int, PathEndSection> sections, List<SidewalkConnectorSpec> specs, List<IntersectionCorner> corners)
    {
        var ordered = new List<PathSummary>();
        foreach (var p in paths) if (sections.ContainsKey(p.path_id)) ordered.Add(p);
        ordered.Sort((a,b) => Mathf.Atan2(sections[a.path_id].Direction.y, sections[a.path_id].Direction.x)
            .CompareTo(Mathf.Atan2(sections[b.path_id].Direction.y, sections[b.path_id].Direction.x)));
        for (int i = 0; i < ordered.Count; i++)
        {
            var a = ordered[i]; var b = ordered[(i + 1) % ordered.Count];
            var sa = sections[a.path_id]; var sb = sections[b.path_id];


            float wa = ResolveWallWidth(a, nodeId, true), wb = ResolveWallWidth(b, nodeId, false);
            Vector2 start = sa.Center + sa.Left * (sa.Width + wa) * 0.5f;
            Vector2 end = sb.Center - sb.Left * (sb.Width + wb) * 0.5f;
            if (wa > 0f && wb > 0f)
            {
                var corner = corners.Find(c => c.FromPath == a.path_id && c.ToPath == b.path_id);
                if (corner == null) continue;
                var spec = BuildEdgeConnector(corner, wa, wb, a.path_variant, a.path_type, b.path_variant, b.path_type, node, horizontalPlane, "wall");
                spec.EndPrefabVariant = b.path_variant; spec.EndFallbackType = b.path_type;
                specs.Add(spec);
            }
            else
            {
                if (wa > 0f) specs.Add(MakeConnector(start, start - sa.Direction * Mathf.Min(wa * 2f, sa.Distance * 0.25f), -sa.Direction, -sa.Direction, wa, 0f, a.path_variant, a.path_type, node, horizontalPlane, "wall"));
                if (wb > 0f) specs.Add(MakeConnector(end - sb.Direction * Mathf.Min(wb * 2f, sb.Distance * 0.25f), end, sb.Direction, sb.Direction, 0f, wb, b.path_variant, b.path_type, node, horizontalPlane, "wall"));
            }
        }
        bool transition = ordered.Count == 2 && Vector2.Dot(sections[ordered[0].path_id].Direction, sections[ordered[1].path_id].Direction) < -0.8f;
        foreach (var path in ordered)
        {
            if (TrackIntersectionMeshGenerator.IsTrackType(path.path_type)) continue;
            var section = sections[path.path_id];
            var master = MasterDataStore.Instance?.GetPathMaster(path.path_variant);
            float width = Mathf.Max(0f, master?.center_width ?? 0f);
            if (width <= 0f) continue;
            var other = transition ? ordered[ordered[0] == path ? 1 : 0] : null;
            float otherWidth = other != null ? Mathf.Max(0f, MasterDataStore.Instance?.GetPathMaster(other.path_variant)?.center_width ?? 0f) : 0f;
            if (otherWidth > 0f)
            {
                if (path != ordered[0]) continue;
                var otherSection = sections[other.path_id];
                var spec = MakeConnector(section.Center, otherSection.Center, -section.Direction, otherSection.Direction,
                    width, otherWidth, path.path_variant, path.path_type, node, horizontalPlane, "center");
                spec.EndPrefabVariant = other.path_variant; spec.EndFallbackType = other.path_type;
                specs.Add(spec);
            }
            else specs.Add(MakeConnector(section.Center, section.Center - section.Direction * Mathf.Min(width * 2f, section.Distance * 0.5f),
                -section.Direction, -section.Direction, width, 0f, path.path_variant, path.path_type, node, horizontalPlane, "center"));
        }
        if (transition && !TrackIntersectionMeshGenerator.IsTrackType(ordered[0].path_type) && !TrackIntersectionMeshGenerator.IsTrackType(ordered[1].path_type))
            BuildTransitionLines(ordered[0], ordered[1], sections, specs, node, horizontalPlane);
    }

    private static void BuildTransitionLines(PathSummary a, PathSummary b, Dictionary<int, PathEndSection> sections,
        List<SidewalkConnectorSpec> specs, Vector3 node, bool horizontalPlane)
    {
        RoadPath.GetPathMasterParams(a, out float aw, out float asw, out float aside, out _, out int alanes, out _, out float acenter, out _, out _, out _, out _);
        RoadPath.GetPathMasterParams(b, out float bw, out float bsw, out float bside, out _, out int blanes, out _, out float bcenter, out _, out _, out _, out _);
        if (aside <= 0f || bside <= 0f) return;
        var sa = sections[a.path_id]; var sb = sections[b.path_id];
        float ae = aw * 0.5f - asw - aside * 0.5f, be = bw * 0.5f - bsw - bside * 0.5f;
        for (int side = -1; side <= 1; side += 2)
            specs.Add(MakeConnector(sa.Center + sa.Left * ae * side, sb.Center - sb.Left * be * side,
                -sa.Direction, sb.Direction, aside * 0.3f, bside * 0.3f, a.path_variant, a.path_type, node, horizontalPlane, "line"));
        if (acenter <= 0f && bcenter <= 0f)
        {
            int count = Mathf.Max(alanes, blanes);
            for (int lane = 1; lane < count; lane++)
            {
                float t = (float)lane / count;
                float ai = Mathf.Round(t * alanes), bi = Mathf.Round(t * blanes);
                specs.Add(MakeConnector(sa.Center + sa.Left * Mathf.Lerp(-ae, ae, ai / Mathf.Max(1, alanes)),
                    sb.Center - sb.Left * Mathf.Lerp(-be, be, bi / Mathf.Max(1, blanes)), -sa.Direction, sb.Direction,
                    aside * 0.3f, bside * 0.3f, a.path_variant, a.path_type, node, horizontalPlane, "line"));
            }
        }
    }
    private static void AddBodyShell(Mesh mesh, Vector3 node, Dictionary<int, PathEndSection> sections)
    {
        var top = mesh.vertices; var topTriangles = mesh.triangles;
        var vertices = new List<Vector3>(top); var triangles = new List<int>(topTriangles);
        bool hasDepth = false;
        for (int i = 0; i < top.Length; i++)
        {
            float weight = 0f, depth = 0f; Vector3 normal = Vector3.up;
            foreach (var section in sections.Values)
            {
                Vector2 delta = IntersectionPolygonUtil.XZ(top[i]+node-Vector3.up*RoadPartHeights.BODY_ABOVE_SPLINE)-section.Center;
                if (Mathf.Abs(Vector2.Dot(delta,section.Direction)) < 0.001f && Mathf.Abs(Vector2.Dot(delta,section.Left)) <= section.Width*0.5f+0.001f)
                { depth=section.BodyDepth; weight=1f; normal=section.Normal; break; }
                float distance = delta.sqrMagnitude;
                float w = 1f / Mathf.Max(0.01f, distance);
                depth += section.BodyDepth * w; weight += w;
            }
            depth /= Mathf.Max(1e-6f, weight);
            hasDepth |= depth > 0.001f;
            vertices.Add(top[i] - normal * depth);
        }
        if (!hasDepth) return;
        for (int i = 0; i < topTriangles.Length; i += 3)
        {
            triangles.Add(top.Length + topTriangles[i]); triangles.Add(top.Length + topTriangles[i+2]); triangles.Add(top.Length + topTriangles[i+1]);
        }
        // 輪郭の入口断面は本線のbodyと接合するため閉じず、交差点外周だけに小口を張る。
        for (int i = 0; i < top.Length; i++)
        {
            int j = (i+1) % top.Length;
            bool mouth = false;
            foreach (var section in sections.Values)
            {
                Vector2 a = IntersectionPolygonUtil.XZ(top[i]+node-Vector3.up*RoadPartHeights.BODY_ABOVE_SPLINE)-section.Center;
                Vector2 b = IntersectionPolygonUtil.XZ(top[j]+node-Vector3.up*RoadPartHeights.BODY_ABOVE_SPLINE)-section.Center;
                if (Mathf.Abs(Vector2.Dot(a,section.Direction)) < 0.02f && Mathf.Abs(Vector2.Dot(b,section.Direction)) < 0.02f) { mouth = true; break; }
            }
            if (mouth) continue;
            int k = vertices.Count;
            vertices.Add(top[i]); vertices.Add(top[j]); vertices.Add(vertices[top.Length+j]); vertices.Add(vertices[top.Length+i]);
            triangles.Add(k); triangles.Add(k+1); triangles.Add(k+2); triangles.Add(k); triangles.Add(k+2); triangles.Add(k+3);
        }
        mesh.SetVertices(vertices); mesh.SetTriangles(triangles,0);
        var colors = new Color[vertices.Count]; Array.Fill(colors,Color.white); mesh.colors=colors;
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
    }
    private static Vector3 ClosestSectionNormal(Vector3 point, Dictionary<int, PathEndSection> sections)
    {
        float best = float.PositiveInfinity; Vector3 normal = Vector3.up;
        foreach (var section in sections.Values)
        {
            Vector2 delta = IntersectionPolygonUtil.XZ(point) - section.Center;
            float distance = Mathf.Abs(Vector2.Dot(delta,section.Direction));
            if (distance < best) { best = distance; normal = section.Normal; }
        }
        return normal;
    }
    internal static Vector3 SectionSurfacePoint(Vector3 point, Dictionary<int, PathEndSection> sections, float offset)
    {
        PathEndSection nearest = null;
        float nearestDistance = float.PositiveInfinity;
        foreach (var s in sections.Values)
        {
            Vector2 delta = IntersectionPolygonUtil.XZ(point) - s.Center;
            if (Mathf.Abs(Vector2.Dot(delta, s.Direction)) > 1e-4f) continue;
            if (delta.sqrMagnitude < nearestDistance) { nearest = s; nearestDistance = delta.sqrMagnitude; }
        }
        if (nearest != null)
        {
            point.y = nearest.Position.y;
            return point + Vector3.up * offset;
        }
        point.y = SectionHeight(point, sections) + offset;
        return point;
    }
    private static float SectionHeight(Vector3 point, Dictionary<int, PathEndSection> sections)
    {
        float weighted = 0f, weight = 0f;
        foreach (var section in sections.Values)
        {
            Vector2 delta = IntersectionPolygonUtil.XZ(point) - section.Center;
            float along = Vector2.Dot(delta, section.Direction);
            float across = Mathf.Max(0f, Mathf.Abs(Vector2.Dot(delta, section.Left)) - section.Width * 0.5f);
            float distance = along * along + across * across;
            if (distance < 1e-8f) return section.Position.y;
            float w = 1f / distance;
            weighted += section.Position.y * w;
            weight += w;
        }
        return weighted / Mathf.Max(weight, 1e-8f);
    }

    private static void AdjustConnectorHeight(List<Vector3> points, Vector3 node, Dictionary<int, PathEndSection> sections, float offset, MotorwayMergeLayout merge = null)
    {
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            p = (merge != null ? merge.SurfacePoint(p + node, offset) : SectionSurfacePoint(p + node, sections, offset)) - node;
            points[i] = p;
        }
    }
    private static Dictionary<int, Vector3> BuildSectionPoints(Dictionary<int, PathEndSection> sections, Vector3 nodePosition, bool horizontalPlane, bool absolute)
    {
        var result = new Dictionary<int, Vector3>();
        foreach (var pair in sections)
        {
            var section = pair.Value;
            if (!absolute) section.Position = new Vector3(section.Position.x, SampleBaseSurfaceY(section.Position.x, section.Position.z, nodePosition, horizontalPlane), section.Position.z);
            result[pair.Key] = section.Position;
        }
        return result;
    }
    private static float ResolveRoadWidth(PathSummary path)
    {
        // 描画側の道路幅はマスターデータを正とする。PathSummary.width はバックエンド由来の値であり、
        // 通常道路メッシュ生成もマスターデータを参照するため、交差点計算も同じ基準に揃える。
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(path.path_variant) : null;
        if (master != null && master.width > 0f)
        {
            return master.width;
        }
        return path.width > 0f ? path.width : 4.0f;
    }

    private static float ResolveSidewalkWidth(PathSummary path)
    {
        if (TrackIntersectionMeshGenerator.IsTrackType(path.path_type)) return 0f;
        // sidewalk_width が 0 以下の道路は歩道を持たない道路として扱う。
        // マスターデータが読めない場合は、既存の RoadPath と同じ既定値を使う。
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(path.path_variant) : null;
        return master != null && master.sidewalk_width.HasValue ? Mathf.Max(0f, master.sidewalk_width.Value) : 0.5f;
    }

    private class SidewalkRoadEntry
    {
        // 交差点に接続している1本の道路について、歩道接続スプラインを作るために必要な情報を保持する。
        // Direction はノードから道路外側へ向かう単位ベクトルで、左右歩道端の判定と角度順ソートに使う。
        // 歩道を持たない道路もエントリに含める（歩道接続の「ブロッカー」として角度順の隣接判定に必要）。
        public int PathId;
        public Vector2 Direction;
        public Vector2 ConnectionPoint;
        public float RoadHalfWidth;
        public float SidewalkWidth;
        public int PathVariant;
        public int PathType;
        public float Angle;

        /// <summary>この道路の歩道を交差点の歩道パーツに参加させられるか（歩道あり＋接続断面が計算済み）。</summary>
        public bool CanSidewalk;
    }

    private static List<SidewalkConnectorSpec> BuildSidewalkConnectorSpecs(int nodeId, Vector3 nodePosition, bool horizontalPlane, List<PathSummary> paths, Dictionary<int, Vector2> connectionPoints, out Dictionary<int, SidewalkSetback> sidewalkSetbacks, Dictionary<int, PathEndSection> sections, List<IntersectionCorner> corners)
    {
        sidewalkSetbacks = null;
        if (connectionPoints == null || connectionPoints.Count == 0)
        {
            return null;
        }

        List<SidewalkRoadEntry> entries = BuildSidewalkRoadEntries(nodeId, paths, connectionPoints, sections);
        if (entries.Count == 0)
        {
            return null;
        }

        List<SidewalkConnectorSpec> specs = new List<SidewalkConnectorSpec>();
        Dictionary<int, SidewalkSetback> setbacks = new Dictionary<int, SidewalkSetback>();

        if (entries.Count == 1)
        {
            // 有効な方向を持つ道路が1本しかない（他は方向が計算できず脱落した）場合、
            // その歩道は左右ともエンドキャップで終端する。
            if (entries[0].CanSidewalk)
            {
                AddEndCap(specs, setbacks, nodePosition, horizontalPlane, entries[0], isLeftSide: true);
                AddEndCap(specs, setbacks, nodePosition, horizontalPlane, entries[0], isLeftSide: false);
            }
            sidewalkSetbacks = setbacks.Count > 0 ? setbacks : null;
            return specs.Count > 0 ? specs : null;
        }

        entries.Sort((a, b) => a.Angle.CompareTo(b.Angle));

        // 全接続道路を角度順に並べ、隣り合う道路のペアごとにセクター（道路間の扇形領域）を処理する。
        // 両方の道路が歩道を持つ場合のみ歩道同士を接続し、片方でも歩道なし道路が挟まる場合は
        // 歩道を接続せず、交差点の少し手前で丸めて終端するエンドキャップを作る。
        // 歩道を持つ道路だけでペアを作ると、間に挟まる歩道なし道路（高架道路など）を飛ばして
        // 交差点を横断する歩道ができてしまう。
        for (int i = 0; i < entries.Count; i++)
        {
            SidewalkRoadEntry current = entries[i];
            SidewalkRoadEntry next = entries[(i + 1) % entries.Count];
            float sectorAngle = PositiveAngleDelta(current.Angle, next.Angle);
            if (sectorAngle <= 0.01f)
            {
                continue;
            }

            if (current.CanSidewalk && next.CanSidewalk)
            {
                                var corner = corners.Find(c => c.FromPath == current.PathId && c.ToPath == next.PathId);
                if (corner != null) specs.Add(BuildEdgeConnector(corner, current.SidewalkWidth, next.SidewalkWidth,
                    current.PathVariant, current.PathType, next.PathVariant, next.PathType, nodePosition, horizontalPlane, "sidewalk"));
            }
            else
            {
                // このセクターに面している歩道端（current の左側・next の右側）を個別にキャップで終端する。
                if (current.CanSidewalk)
                {
                    AddEndCap(specs, setbacks, nodePosition, horizontalPlane, current, isLeftSide: true);
                }
                if (next.CanSidewalk)
                {
                    AddEndCap(specs, setbacks, nodePosition, horizontalPlane, next, isLeftSide: false);
                }
            }
        }

        sidewalkSetbacks = setbacks.Count > 0 ? setbacks : null;
        return specs.Count > 0 ? specs : null;
    }

    /// <summary>
    /// エンドキャップの spec を生成して追加し、成功した場合はその道路・その側の
    /// 歩道セットバック量（直線歩道を切断面から後退させる量）を記録する。
    /// </summary>
    private static void AddEndCap(List<SidewalkConnectorSpec> specs, Dictionary<int, SidewalkSetback> setbacks, Vector3 nodePosition, bool horizontalPlane, SidewalkRoadEntry entry, bool isLeftSide)
    {
        SidewalkConnectorSpec spec = BuildSidewalkEndCapSpec(nodePosition, horizontalPlane, entry, isLeftSide, out float setback);
        if (spec == null)
        {
            return;
        }
        specs.Add(spec);

        if (setback > 0f)
        {
            // 同じ道路が複数セクターで左右別々にキャップされ得るため、左右を独立に統合する。
            setbacks.TryGetValue(entry.PathId, out SidewalkSetback s);
            if (isLeftSide)
            {
                s.LeftOutwardSetback = Mathf.Max(s.LeftOutwardSetback, setback);
            }
            else
            {
                s.RightOutwardSetback = Mathf.Max(s.RightOutwardSetback, setback);
            }
            setbacks[entry.PathId] = s;
        }
    }

    private static void AddSpecIfValid(List<SidewalkConnectorSpec> specs, SidewalkConnectorSpec spec)
    {
        if (spec != null)
        {
            specs.Add(spec);
        }
    }

    private static List<SidewalkRoadEntry> BuildSidewalkRoadEntries(int nodeId, List<PathSummary> paths, Dictionary<int, Vector2> connectionPoints, Dictionary<int, PathEndSection> sections)
    {
        List<SidewalkRoadEntry> entries = new List<SidewalkRoadEntry>();
        foreach (var path in paths)
        {
            Vector2 nodePoint;
            Vector2 directionPoint;
            if (!TryGetPathDirectionFromNode(nodeId, path, out nodePoint, out directionPoint))
            {
                continue;
            }

            Vector2 direction = directionPoint - nodePoint;
            if (direction.sqrMagnitude < 1e-6f)
            {
                continue;
            }
            direction.Normalize();
            if (sections.TryGetValue(path.path_id, out var section)) direction = section.Direction;

            // 歩道を持たない道路や接続断面が計算できなかった道路もエントリには含める
            // （角度順の隣接判定で「歩道接続をブロックする道路」として必要）。
            // ただし歩道パーツの生成対象にはしない（CanSidewalk=false）。
            float sidewalkWidth = ResolveSidewalkWidth(path);
            bool hasConnection = connectionPoints.TryGetValue(path.path_id, out Vector2 connectionPoint);

            entries.Add(new SidewalkRoadEntry
            {
                PathId = path.path_id,
                Direction = direction,
                ConnectionPoint = hasConnection ? connectionPoint : Vector2.zero,
                RoadHalfWidth = ResolveRoadWidth(path) * 0.5f,
                SidewalkWidth = sidewalkWidth,
                PathVariant = path.path_variant,
                PathType = path.path_type,
                Angle = Mathf.Atan2(direction.y, direction.x),
                CanSidewalk = sidewalkWidth > 0f && hasConnection
            });
        }
        return entries;
    }

    /// <summary>道路外縁を中心とする四分円の扇形。縁石を外側へ回し、尖った先端を作らない。</summary>
    private static SidewalkConnectorSpec BuildSidewalkEndCapSpec(Vector3 nodePosition, bool horizontalPlane, SidewalkRoadEntry entry, bool isLeftSide, out float setback)
    {
        setback = 0f;
        float width = entry.SidewalkWidth;
        if (width <= 0f) return null;
        Vector2 lateral = isLeftSide ? PerpendicularLeft(entry.Direction) : -PerpendicularLeft(entry.Direction);
        Vector2 pivot = entry.ConnectionPoint + lateral * entry.RoadHalfWidth;
        var points = new List<Vector3>();
        var widths = new List<float>();
        var directions = new List<Vector2>();
        const int segments = 16;
        for (int i = 0; i <= segments; i++)
        {
            float angle = Mathf.PI * 0.5f * i / segments;
            Vector2 radial = -lateral * Mathf.Cos(angle) - entry.Direction * Mathf.Sin(angle);
            points.Add(ToLocalTerrainPoint(pivot + radial * width * 0.5f, nodePosition, horizontalPlane));
            widths.Add(width);
            directions.Add(PerpendicularLeft(radial));
        }
        // 左側用モデルの縁石(+X)を円弧側へ向ける。反対側は点列の進行方向を反転する。
        if (Vector2.Dot(directions[0], -entry.Direction) < 0f)
        {
            points.Reverse();
            widths.Reverse();
            directions.Reverse();
        }
        return new SidewalkConnectorSpec(points, widths, entry.PathVariant, entry.PathType)
        {
            Directions = directions,
            StartDirection = directions[0],
            EndDirection = directions[directions.Count - 1]
        };
    }
    public static bool TryGetPathDirectionFromNode(int nodeId, PathSummary path, out Vector2 nodePoint, out Vector2 directionPoint)
    {
        nodePoint = directionPoint = Vector2.zero;
        bool start = path.start_node_id == nodeId;
        if (!start && path.end_node_id != nodeId) return false;
        var anchor = start ? path.GetStartPointList() : path.GetEndPointList();
        if (anchor == null || anchor.Count < 3) return false;
        nodePoint = new Vector2(anchor[0], anchor[2]);
        var refs = new List<Vector2>();
        var control = start ? path.GetStartControlList() : path.GetEndControlList();
        if (path.curved && control != null && control.Count >= 3) refs.Add(new Vector2(control[0], control[2]));
        var waypoints = path.GetWaypointList();
        if (waypoints != null && waypoints.Count > 0)
        {
            var w = waypoints[start ? 0 : waypoints.Count - 1].position;
            if (w != null && w.Length >= 3) refs.Add(new Vector2(w[0], w[2]));
        }
        var other = start ? path.GetEndPointList() : path.GetStartPointList();
        if (other != null && other.Count >= 3) refs.Add(new Vector2(other[0], other[2]));
        foreach (var point in refs) if ((point - nodePoint).sqrMagnitude > 1e-6f) { directionPoint = point; return true; }
        return false;
    }
    /// <summary>端点の方向を固定した移行部・終端の中心線を生成する。</summary>
    private static SidewalkConnectorSpec MakeConnector(Vector2 start, Vector2 end, Vector2 startDirection, Vector2 endDirection,
        float startWidth, float endWidth, int variant, int type, Vector3 node, bool horizontalPlane, string part = "sidewalk")
    {
        float length = Vector2.Distance(start, end);
        float handle = length / 3f;
        Vector2 p1 = start + startDirection * handle;
        Vector2 p2 = end - endDirection * handle;
        int count = Mathf.Clamp(Mathf.CeilToInt(length / 0.5f), 4, 128);
        var points = new List<Vector3>();
        var widths = new List<float>();
        for (int i = 0; i <= count; i++)
        {
            float t = (float)i / count, u = 1f - t;
            Vector2 p = u*u*u*start + 3*u*u*t*p1 + 3*u*t*t*p2 + t*t*t*end;
            points.Add(ToLocalTerrainPoint(p, node, horizontalPlane));
            widths.Add(Mathf.Lerp(startWidth, endWidth, t*t*(3f-2f*t)));
        }
        return new SidewalkConnectorSpec(points, widths, variant, type)
        {
            Part = part, StartDirection = startDirection, EndDirection = endDirection
        };
    }
    private static Vector3 ToLocalTerrainPoint(Vector2 point, Vector3 nodePosition, bool horizontalPlane)
    {
        // 交差点オブジェクトの子として配置するため、スプライン点はノード基準のローカル座標に変換する。
        // 高さは基準面 + 歩道の相対高さで、通常道路の歩道の下端（スプライン + SIDEWALK_ABOVE_SPLINE）と
        // 段差なく揃える。
        float worldY = SampleBaseSurfaceY(point.x, point.y, nodePosition, horizontalPlane) + RoadPartHeights.SIDEWALK_ABOVE_SPLINE;
        return new Vector3(point.x - nodePosition.x, worldY - nodePosition.y, point.y - nodePosition.z);
    }

    private static Vector2 PerpendicularLeft(Vector2 direction)
    {
        return new Vector2(-direction.y, direction.x);
    }

    private static float PositiveAngleDelta(float from, float to)
    {
        // Mathf.DeltaAngle は度数法かつ -180〜180 度の範囲へ折り返すため、
        // 角度順に並べた道路間の反時計回りセクター幅を扱うここでは 0〜2π の正の差分を使う。
        float delta = to - from;
        while (delta < 0f) delta += Mathf.PI * 2f;
        while (delta >= Mathf.PI * 2f) delta -= Mathf.PI * 2f;
        return delta;
    }

    private static List<PathSummary> GetSidewalkPaths(List<PathSummary> paths)
    {
        List<PathSummary> result = new List<PathSummary>();
        foreach (var path in paths)
        {
            if (ResolveSidewalkWidth(path) > 0f)
            {
                result.Add(path);
            }
        }
        return result;
    }

    private static Material LoadRoadMaterial(List<PathSummary> paths)
    {
        // 交差点のアスファルトは、接続道路のうち代表バリアント（variant番号が最大の道路）の
        // 路面マテリアルを使い、道路本体と見た目を揃える。
        // 舗装路と未舗装路が交わるノードでは variant 番号の大きい未舗装路が代表になり、
        // 交差点も土の路面になる（舗装路どうしならアスファルト）。
        // RoadPath と同じマテリアルキャッシュ（Resources同名マテリアルの再割当＋renderQueue調整）を
        // 経由することで、交差点内だけ舗装の色味や描画設定が変わるのを防ぐ。
        if (paths != null)
        {
            // 道路と線路が混在するノード（踏切）では道路側の舗装を優先する
            // （線路バリアントは番号が大きく、単純な最大variant選択だと踏切全体が
            //  道床（バラスト）の見た目になってしまう。踏切は道路の舗装が連続し、
            //  その上をレールコネクタが横切るのが正しい見た目）。
            // 同じカテゴリ（道路どうし・線路どうし）の中では従来どおり最大variantを代表にする。
            PathSummary selectedPath = null;
            foreach (var path in paths)
            {
                if (selectedPath == null)
                {
                    selectedPath = path;
                    continue;
                }
                bool pathIsTrack = TrackIntersectionMeshGenerator.IsTrackType(path.path_type);
                bool selectedIsTrack = TrackIntersectionMeshGenerator.IsTrackType(selectedPath.path_type);
                if (selectedIsTrack && !pathIsTrack)
                {
                    selectedPath = path;
                }
                else if (pathIsTrack == selectedIsTrack && path.path_variant > selectedPath.path_variant)
                {
                    selectedPath = path;
                }
            }

            if (selectedPath != null)
            {
                // マスターの body_material（Resources直下の .mat 名）が路面マテリアルの正。
                // プレハブのマテリアル名に依存せずシートだけで交差点の路面を差し替えられる。
                var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(selectedPath.path_variant) : null;
                Material masterMaterial = master != null ? RoadPath.GetOrCreateRoadMaterialByName(master.body_material) : null;
                if (masterMaterial != null)
                {
                    return masterMaterial;
                }

                // body_material 未設定のバリアントは body プレハブのマテリアルから解決する。
                if (ModelManager.Instance != null)
                {
                    GameObject prefab = ModelManager.Instance.GetModel($"Models/Paths/p{selectedPath.path_variant}/body")
                        ?? ModelManager.Instance.GetModel($"Models/Paths/p{selectedPath.path_type}/body");
                    MeshRenderer renderer = prefab != null ? prefab.GetComponent<MeshRenderer>() : null;
                    if (renderer != null && renderer.sharedMaterial != null)
                    {
                        Material bodyMaterial = RoadPath.GetOrCreateRoadMaterial(renderer.sharedMaterial);
                        if (bodyMaterial != null)
                        {
                            return bodyMaterial;
                        }
                    }
                }
            }
        }

        // マスターもプレハブも解決できない場合の保険。共有プールを経由して
        // renderQueue 補正まで通常道路と揃える。
        Material material = RoadPath.GetOrCreateRoadMaterialByName("PATH_ASPHALT");
        if (material == null)
        {
            material = RoadPath.GetOrCreateRoadMaterialByName("NodeMaterial");
        }
        return material;
    }

    private static Mesh ConvertGeometryToMesh(Geometry geometry, Vector3 center, bool horizontalPlane, string meshName)
    {
        if (geometry == null || geometry.IsEmpty)
        {
            return null;
        }

        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();

        AddGeometryToMesh(geometry, center, horizontalPlane, vertices, triangles);

        if (vertices.Count < 3 || triangles.Count < 3)
        {
            return null;
        }

        Mesh mesh = new Mesh();
        mesh.name = meshName;
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        // 頂点カラーのアルファを「縁マスク」として読む道路マテリアル(未舗装路の
        // Custom/PathDirt)があるため、白(α=1＝侵食なし)を明示設定する。
        // 未設定メッシュへ供給される既定値に依存すると、交差点が丸ごと切り抜かれて
        // 消える事故になりうる。
        Color[] colors = new Color[vertices.Count];
        Array.Fill(colors, Color.white);
        mesh.SetColors(colors);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;
    }

    private static void AddGeometryToMesh(Geometry geometry, Vector3 center, bool horizontalPlane, List<Vector3> vertices, List<int> triangles)
    {
        if (geometry is Polygon polygon)
        {
            AddPolygonToMesh(polygon, center, horizontalPlane, vertices, triangles);
            return;
        }

        if (geometry is MultiPolygon || geometry is GeometryCollection)
        {
            for (int i = 0; i < geometry.NumGeometries; i++)
            {
                AddGeometryToMesh(geometry.GetGeometryN(i), center, horizontalPlane, vertices, triangles);
            }
        }
    }

    private static void AddPolygonToMesh(Polygon polygon, Vector3 center, bool horizontalPlane, List<Vector3> vertices, List<int> triangles)
    {
        Coordinate[] coords = polygon.ExteriorRing.Coordinates;
        if (coords == null || coords.Length < 4)
        {
            return;
        }

        Vector2[] points = new Vector2[coords.Length - 1];
        for (int i = 0; i < coords.Length - 1; i++)
        {
            points[i] = new Vector2((float)coords[i].X, (float)coords[i].Y);
        }

        if (PolygonUtils.SignedArea(points) < 0f)
        {
            Array.Reverse(points);
        }

        int baseIndex = vertices.Count;
        for (int i = 0; i < points.Length; i++)
        {
            // アスファルト面は基準面 + BODY_ABOVE_SPLINE で、通常道路の路面上面と同一平面になる。
            // 道路は切断点で交差点の縁まで切り詰められているため、領域の重なりによる
            // Zファイティングは起きない。
            float worldY = SampleBaseSurfaceY(points[i].x, points[i].y, center, horizontalPlane) + RoadPartHeights.BODY_ABOVE_SPLINE;
            vertices.Add(new Vector3(points[i].x - center.x, worldY - center.y, points[i].y - center.z));
        }

        List<int> polygonTriangles = BuildingMeshBuilder.TriangulateEarClip(points);
        if (polygonTriangles.Count == 0)
        {
            throw new InvalidOperationException("交差点の輪郭を三角形化できません。");
        }

        // Unity の XZ 平面では、2D 上で反時計回りの三角形をそのまま置くと法線が下向きになる。
        // 描画面を上向きにするため、三角形単位で巻き順を反転してから登録する。
        for (int i = 0; i < polygonTriangles.Count; i += 3)
        {
            triangles.Add(baseIndex + polygonTriangles[i]);
            triangles.Add(baseIndex + polygonTriangles[i + 2]);
            triangles.Add(baseIndex + polygonTriangles[i + 1]);
        }
    }
}
