using UnityEngine;
using System;
using System.Collections.Generic;
using GeneratedModels;
using NetTopologySuite.Geometries;

/// <summary>
/// Generate road-node intersection meshes.
/// Share main-line cut cross-sections and boundaries between adjacent entrances, joining surfaces/attached parts.
/// Return geometry for sidewalk/wall/median connections or termination.
/// </summary>
public class RoadIntersectionMeshGenerator : INodeMeshGenerator
{
    public NodeMeshResult Generate(int nodeId, Vector3 nodePosition, List<PathSummary> paths, float nodeRelativeY = 0f)
        => GenerateWithSections(nodeId, nodePosition, paths, nodeRelativeY);

    public NodeMeshResult GenerateWithSections(int nodeId, Vector3 nodePosition, List<PathSummary> paths, float nodeRelativeY = 0f, Func<PathSummary, List<Vector3>> pointResolver = null, Func<PathSummary, float> cutLimitResolver = null)
    {
        // Exclude logical public-transport paths from underlying road/track junction geometry. Aerial cables/pipelines also
        // have no surface filling connections; draw to anchors. Water conduits (PATH_WATER) create intersections as surface pipes.
        paths = paths?.FindAll(p => p.path_type != Constants.PATH_CONVEYOR
            && p.path_type != Constants.PATH_TRANSPORT
            && p.path_type != Constants.PATH_FERRY
            && p.path_type != Constants.PATH_ELECTRICITY
            && p.path_type != Constants.PATH_PIPELINE);
        if (paths == null || paths.Count < 2)
        {
            return null;
        }

        try
        {
            var roadJoin = TryContinuousRoadJoin(nodeId, paths, pointResolver, out var roadJoinPoints);
            if (roadJoin != null)
            {
                return new NodeMeshResult(null, null, roadJoinPoints) { RoadJoin = roadJoin };
            }

            var gf = new GeometryFactory();
            Vector2 nodePos2D = new Vector2(nodePosition.x, nodePosition.z);

            // Nodes with relative Y≠0 (e.g. at-grade elevated crossings) skip terrain sampling,
            // generating horizontal intersections at absolute node Y (nodePosition.y).
            // Terrain-based generation would put aerial intersections on the ground, and assigning cut points
            // would pull road ends down there too.
            bool horizontalPlane = nodeRelativeY != 0f;

            // Prioritize motorway main lines. With only absorbable ramps, omit plates and join main lines at the node.
            MotorwayMergeLayout merge = MotorwayMergeLayout.Create(nodeId, paths, pointResolver, cutLimitResolver);
            if (merge != null && !merge.NeedsSlab)
            {
                return new NodeMeshResult(null, null, merge.ConnectionPoints) { MotorwayMerge = merge };
            }
            MainLineSurface mainLine = merge?.Surface;

            // Straight-track S join for differing track positions (TrackEndTransition). Both paths match lateral position/width at the node.
            var trackShifts = TrackEndTransition.ResolveNode(nodeId, paths);
            // Straight connections of matching track positions/widths omit plates, drawing both to the node (continuous wires/girders/platforms).
            TrackContinuousJoin trackJoin = TrackIntersectionMeshGenerator.TryContinuousJoin(nodeId, paths, pointResolver, trackShifts, out var joinPoints);
            if (trackJoin != null)
            {
                return new NodeMeshResult(null, null, joinPoints) { TrackJoin = trackJoin };
            }

            // S-curve widths vary with node distance. Determine cut distance from maximum transition width (larger of original/node widths),
            // but cross-section width from the cut position (narrow cuts can make wide section outlines intersect, preventing plate generation).
            var roadResult = IntersectionPolygonUtil.ComputeIntersectionPolygon(
                nodeId,
                nodePos2D,
                paths,
                gf,
                p => ResolveRoadWidth(p) + Mathf.Max(0f, TrackWidthShift(p, 0f, trackShifts)), pointResolver, cutLimitResolver,
                (p, distance) => ResolveRoadWidth(p) + TrackWidthShift(p, distance, trackShifts)
            );

            if (roadResult.RequiresMoreSpace)
            {
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[Intersection] node={nodeId}: 接合区間が不足するため道路の切断を行いません。");
                return null;
            }
            Dictionary<int, Vector3> connectionPoints = BuildSectionPoints(roadResult.Sections, nodePosition, horizontalPlane, pointResolver != null);
            // Nodes containing only bedless tracks (monorail, etc.) omit plates, retaining rail connectors/attached parts.
            bool railsOnly = paths.TrueForAll(p => TrackIntersectionMeshGenerator.IsTrackType(p.path_type) && !HasSurfaceSource(p));
            Mesh roadMesh = railsOnly ? null : BuildSurfaceMesh(roadResult.Geometry, nodePosition, roadResult.Sections, mainLine, "RoadIntersection");
            if (!railsOnly && roadMesh == null)
            {
                return null;
            }

            Material roadMaterial = railsOnly ? null : LoadRoadMaterial(paths);
            foreach (var path in paths)
            {
                if (!roadResult.Sections.TryGetValue(path.path_id, out var section)) continue;
                GameObject body = ModelManager.Instance?.GetModel($"Models/Paths/p{path.path_variant}/body", suppressWarning: true)
                    ?? ModelManager.Instance?.GetModel($"Models/Paths/p{path.path_type}/body");
                var source = body != null ? body.GetComponent<MeshFilter>()?.sharedMesh : null;
                section.BodyDepth = source != null ? source.bounds.size.y : 0f;
            }

            List<SidewalkConnectorSpec> sidewalkConnectors = null;
            Dictionary<int, SidewalkSetback> sidewalkSetbacks = null;
            if (GetSidewalkPaths(paths).Count >= 1)
            {
                // Sidewalks use the same sidewalk prefabs as normal roads on short splines
                // connecting neighboring sidewalk endpoints, rather than a single roadway-style plate. This matches
                // prefab shape, thickness, material, and UV handling to ordinary roads.
                // Call even for one sidewalk road to generate rounded endcaps.
                sidewalkConnectors = BuildSidewalkConnectorSpecs(nodeId, nodePosition, horizontalPlane, paths, roadResult.ConnectionPoints, out sidewalkSetbacks, roadResult.Sections, roadResult.Corners);
            }

            sidewalkConnectors ??= new List<SidewalkConnectorSpec>();
            BuildBoundaryParts(nodeId, nodePosition, horizontalPlane, paths, roadResult.Sections, sidewalkConnectors, roadResult.Corners);
            // For nodes connecting at least two track paths (PATH_TRACK/PATH_MONORAIL),
            // represent switches/at-grade crossings by joining direction-selected track ends with rail connectors
            // for every movement satisfying turn constraints and traffic direction (see TrackIntersectionMeshGenerator).
            List<RailConnectorSpec> railConnectors = TrackIntersectionMeshGenerator.BuildRailConnectorSpecs(nodeId, nodePosition, horizontalPlane, paths, roadResult.ConnectionPoints, roadResult.Sections);

            sidewalkConnectors.RemoveAll(spec => spec.Part == "wall" && WallBlocksRail(spec, railConnectors));
            int surfaceVertexCount = roadMesh != null ? roadMesh.vertexCount : 0;
            if (roadMesh != null) AddBodyShell(roadMesh, nodePosition, roadResult.Sections);
            RoadMarkingPlan markings = null;
            if (roadMesh != null && paths.Exists(RoadMarkingRules.HasPaint))
            {
                // Preserve specialized edges/projection when mixed nodes select dirt/cobblestone as representative material.
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
                AdjustConnectorHeight(connector.Points, nodePosition, roadResult.Sections, connector.Part == "line" ? RoadPartHeights.LINE_ABOVE_SPLINE : RoadPartHeights.SIDEWALK_ABOVE_SPLINE, mainLine);
            }
            if (railConnectors != null) foreach (var connector in railConnectors)
            {
                AdjustConnectorHeight(connector.Points, nodePosition, roadResult.Sections, RoadPartHeights.TRACK_ABOVE_SPLINE, mainLine);
                if (connector.EmbeddedLrt)
                    for (int i = 0; i < connector.Points.Count; i++)
                    {
                        float t = (float)i / (connector.Points.Count - 1);
                        connector.Points[i] += Vector3.up * (Mathf.Lerp(connector.StartRailBase, connector.EndRailBase, t * t * (3 - 2 * t)) - RoadPartHeights.TRACK_ABOVE_SPLINE);
                    }
            }
            var trafficSignals = TrafficSignalLayout.Build(nodeId, horizontalPlane, paths, roadResult.Sections, roadResult.Corners,
                sidewalkSetbacks, mainLine, TrafficSignalStyle.Current.MinReach);
            return new NodeMeshResult(roadMesh, roadMaterial, connectionPoints, sidewalkConnectors, railConnectors, sidewalkSetbacks) { MotorwayMerge = merge, Markings = markings, TrafficSignals = trafficSignals, RailsOnly = railsOnly };
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RoadIntersectionMeshGenerator] ノード {nodeId} の交差点メッシュ生成に失敗: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Determine the intersection height reference (= road spline-point plane).
    /// Surface intersections use local terrain elevation; elevated nodes use a horizontal plane at absolute node Y,
    /// both adding ROAD_SPLINE_OFFSET like road spline points
    /// (PathManager.GetPathPointPosition places endpoints at terrain+relativeY+ROAD_SPLINE_OFFSET,
    /// so elevated intersections must match this offset too).
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
        var top = mesh.vertices; var topTriangles = mesh.triangles; var topNormals = mesh.normals;
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
        // Leave entrance cross-sections open to join main-line body; add edge faces only around the intersection perimeter.
        foreach (var (i, j) in OutlineEdges(topTriangles))
        {
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
        var normals = mesh.normals; Array.Copy(topNormals, normals, top.Length); mesh.normals = normals;
    }

    /// <summary>
    /// Return top perimeter edges as vertex pairs counterclockwise in XZ.
    /// With interior vertices, vertex order is not perimeter order; find edges with a triangle on only one side.
    /// </summary>
    private static List<(int from, int to)> OutlineEdges(int[] triangles)
    {
        var directed = new HashSet<(int, int)>();
        for (int i = 0; i < triangles.Length; i += 3)
        {
            directed.Add((triangles[i], triangles[i+1])); directed.Add((triangles[i+1], triangles[i+2])); directed.Add((triangles[i+2], triangles[i]));
        }
        // Top triangles are XZ-clockwise for upward faces, so traverse their boundary edges in reverse.
        var outline = new List<(int from, int to)>();
        for (int i = 0; i < triangles.Length; i += 3)
            for (int k = 0; k < 3; k++)
            {
                int u = triangles[i + k], v = triangles[i + (k + 1) % 3];
                if (!directed.Contains((v, u))) outline.Add((v, u));
            }
        return outline;
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
    /// <param name="mainLine">
    /// Motorway main line. If specified, surface above it follows its exact longitudinal height; omit main-line entrances from interpolation,
    /// interpolating outside from main-line surface edges and other branch entrances.
    /// </param>
    internal static Vector3 SectionSurfacePoint(Vector3 point, Dictionary<int, PathEndSection> sections, float offset, MainLineSurface mainLine = null)
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
        point.y = SectionHeight(point, sections, mainLine) + offset;
        return point;
    }
    /// <summary>
    /// Intersection surface height. Extend each entrance's main-line grade inward, blending heights with weights inversely proportional
    /// to squared distance to entrances. Height/grade match at entrances without kinks; uniform slopes remain nearly planar.
    /// Smoothly cap extension using twice the entrance-node distance, preventing distant grades from creating peaks/valleys.
    /// </summary>
    private static float SectionHeight(Vector3 point, Dictionary<int, PathEndSection> sections, MainLineSurface mainLine)
    {
        float weighted = 0f, weight = 0f;
        if (mainLine != null)
        {
            float height = mainLine.Height(IntersectionPolygonUtil.XZ(point), out float outside);
            if (outside < 1e-4f) return height;
            float w = 1f / (outside * outside);
            weighted += height * w;
            weight += w;
        }
        foreach (var pair in sections)
        {
            if (mainLine != null && (pair.Key == mainLine.ArmA || pair.Key == mainLine.ArmB)) continue;
            var section = pair.Value;
            Vector2 delta = IntersectionPolygonUtil.XZ(point) - section.Center;
            float along = Vector2.Dot(delta, section.Direction);
            float across = Mathf.Max(0f, Mathf.Abs(Vector2.Dot(delta, section.Left)) - section.Width * 0.5f);
            float distance = along * along + across * across;
            if (distance < 1e-8f) return section.Position.y;
            float reach = Mathf.Max(1f, section.Distance * 2f);
            float height = section.Position.y + SectionGrade(section) * reach * (float)Math.Tanh(along / reach);
            float w = 1f / distance;
            weighted += height * w;
            weight += w;
        }
        return weighted / Mathf.Max(weight, 1e-8f);
    }

    /// <summary>Whether every entrance has equal height and zero grade. Then surface height is constant and planar.</summary>
    private static bool IsLevel(Dictionary<int, PathEndSection> sections)
    {
        float? height = null;
        foreach (var section in sections.Values)
        {
            height ??= section.Position.y;
            if (Mathf.Abs(SectionGrade(section)) > 1e-4f || Mathf.Abs(section.Position.y - height.Value) > 1e-3f) return false;
        }
        return true;
    }

    /// <summary>Main-line grade at an entrance (uphill positive outward from node).</summary>
    private static float SectionGrade(PathEndSection section)
    {
        float run = new Vector2(section.Tangent.x, section.Tangent.z).magnitude;
        return run > 1e-4f ? section.Tangent.y / run : 0f;
    }

    /// <summary>
    /// Compute curved-surface normals by central differences, independent of triangle shapes and matching main-line section normals at entrances.
    /// </summary>
    internal static Vector3 SurfaceNormal(Vector3 point, Dictionary<int, PathEndSection> sections, MainLineSurface mainLine = null)
    {
        const float step = 0.05f;
        float dx = SectionHeight(point + Vector3.right * step, sections, mainLine) - SectionHeight(point - Vector3.right * step, sections, mainLine);
        float dz = SectionHeight(point + Vector3.forward * step, sections, mainLine) - SectionHeight(point - Vector3.forward * step, sections, mainLine);
        return new Vector3(-dx, 2f * step, -dz).normalized;
    }

    private static void AdjustConnectorHeight(List<Vector3> points, Vector3 node, Dictionary<int, PathEndSection> sections, float offset, MainLineSurface mainLine)
    {
        for (int i = 0; i < points.Count; i++)
        {
            points[i] = SectionSurfacePoint(points[i] + node, sections, offset, mainLine) - node;
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
    /// <summary>Width expansion at distance from node in a track-join S curve (negative for narrowing, symmetric left/right).</summary>
    private static float TrackWidthShift(PathSummary path, float distance, Dictionary<int, TrackEndTransition.EndShift> shifts)
    {
        return shifts != null && shifts.TryGetValue(path.path_id, out var shift) ? 2f * shift.OuterShift(distance) : 0f;
    }

    /// <summary>
    /// Whether a path has a plate-material source (master body_material or bed body part).
    /// Bedless tracks such as monorails do not; nodes containing only these omit plates.
    /// </summary>
    private static bool HasSurfaceSource(PathSummary path)
    {
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(path.path_variant) : null;
        if (master != null && !string.IsNullOrEmpty(master.body_material) && RoadPath.GetOrCreateRoadMaterialByName(master.body_material) != null) return true;
        if (ModelManager.Instance == null) return true;
        return ModelManager.Instance.GetModel($"Models/Paths/p{path.path_variant}/body", true) != null
            || ModelManager.Instance.GetModel($"Models/Paths/p{path.path_type}/body", true) != null;
    }

    /// <summary>
    /// Nodes with only two straight roads of identical variant/width draw surfaces/sidewalks/medians to their ends.
    /// Treat width/position differences ≤1cm and unit-direction difference ≤0.001 as numeric error; do not join corners without plates.
    /// </summary>
    public static RoadContinuousJoin TryContinuousRoadJoin(int nodeId, List<PathSummary> paths,
        Func<PathSummary, List<Vector3>> pointResolver, out Dictionary<int, Vector3> connectionPoints)
    {
        connectionPoints = null;
        if (paths == null || paths.Count != 2) return null;
        var a = paths[0]; var b = paths[1];
        if (!RoadMarkingRules.IsRoad(a) || !RoadMarkingRules.IsRoad(b)
            || a.path_type != b.path_type || a.path_variant != b.path_variant) return null;
        if (Mathf.Abs(ResolveRoadWidth(a) - ResolveRoadWidth(b)) > 0.01f) return null;

        var ends = new List<PathEndSection>(2);
        foreach (var path in paths)
        {
            bool start = path.start_node_id == nodeId, end = path.end_node_id == nodeId;
            if (start == end) return null;
            var source = pointResolver != null ? pointResolver(path) : IntersectionPolygonUtil.SamplePlan(path);
            if (source == null || source.Count < 2) return null;
            var points = new List<Vector3>(source);
            if (end) points.Reverse();
            if (IntersectionPolygonUtil.Length(points) < 1e-6f) return null;
            var section = IntersectionPolygonUtil.AtDistance(points, 0f);
            ends.Add(section);
        }
        if (Vector3.Distance(ends[0].Position, ends[1].Position) > 0.01f
            || (ends[0].Direction + ends[1].Direction).sqrMagnitude > 1e-6f) return null;

        connectionPoints = new Dictionary<int, Vector3> { { a.path_id, ends[0].Position }, { b.path_id, ends[1].Position } };
        return new RoadContinuousJoin { PathA = a.path_id, PathB = b.path_id };
    }

    private static float ResolveRoadWidth(PathSummary path)
    {
        // Master data defines rendered road width. PathSummary.width comes from the backend;
        // ordinary road meshes also use master data, so intersections must use the same basis.
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(path.path_variant) : null;
        if (master != null && master.width > 0f)
        {
            return master.width;
        }
        return path.width > 0f ? path.width : 4.0f;
    }

    internal static float ResolveSidewalkWidth(PathSummary path)
    {
        if (TrackIntersectionMeshGenerator.IsTrackType(path.path_type)) return 0f;
        // Treat sidewalk_width <= 0 as no sidewalk.
        // If master data is unavailable, use existing RoadPath defaults.
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(path.path_variant) : null;
        return master != null && master.sidewalk_width.HasValue ? Mathf.Max(0f, master.sidewalk_width.Value) : 0.5f;
    }

    private class SidewalkRoadEntry
    {
        // Holds one connected road's information needed for sidewalk-connector splines.
        // Direction is a unit vector outward from node, used for left/right sidewalk-end identification and angular sorting.
        // Include sidewalk-free roads as blockers needed for angular adjacency tests.
        public int PathId;
        public Vector2 Direction;
        public Vector2 ConnectionPoint;
        public float RoadHalfWidth;
        public float SidewalkWidth;
        public int PathVariant;
        public int PathType;
        public float Angle;

        /// <summary>Whether this road can participate in intersection sidewalks (has sidewalk and calculated connection cross-section).</summary>
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
            // If only one road has a valid direction (others were rejected because direction could not be calculated),
            // terminate both sidewalks with endcaps.
            if (entries[0].CanSidewalk)
            {
                AddEndCap(specs, setbacks, nodePosition, horizontalPlane, entries[0], isLeftSide: true);
                AddEndCap(specs, setbacks, nodePosition, horizontalPlane, entries[0], isLeftSide: false);
            }
            sidewalkSetbacks = setbacks.Count > 0 ? setbacks : null;
            return specs.Count > 0 ? specs : null;
        }

        entries.Sort((a, b) => a.Angle.CompareTo(b.Angle));

        // Sort all connected roads by angle; process sectors (wedges between roads) for adjacent pairs.
        // Connect sidewalks only if both roads have them; if either lacks sidewalks,
        // terminate with rounded endcaps slightly before the intersection.
        // Pairing only sidewalk-bearing roads would skip intervening sidewalk-free roads (e.g. elevated roads),
        // creating sidewalks crossing the intersection.
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
                // Cap each sidewalk end facing this sector (current left, next right) independently.
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
    /// Generate/add an endcap spec; on success, record the setback for that road/side
    /// (distance shortening the straight sidewalk from its cut cross-section).
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
            // Multiple sectors may cap different sides of one road; merge left/right independently.
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

            // Include roads without sidewalks or calculated connection sections
            // (needed as sidewalk-connection blockers for angular adjacency),
            // but exclude them from sidewalk-part generation (CanSidewalk=false).
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

    /// <summary>Quarter-circle sector centered on road outer edge. Turn the curb outward without pointed ends.</summary>
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
        // Face left-model curb (+X) toward the arc. Reverse point progression on the opposite side.
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
    /// <summary>Generate transition/termination centerlines with fixed endpoint directions.</summary>
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
        // Convert spline points to node-local coordinates for placement as intersection children.
        // Height is reference plane + relative sidewalk height, matching ordinary sidewalk bottoms
        // (spline + SIDEWALK_ABOVE_SPLINE) without steps.
        float worldY = SampleBaseSurfaceY(point.x, point.y, nodePosition, horizontalPlane) + RoadPartHeights.SIDEWALK_ABOVE_SPLINE;
        return new Vector3(point.x - nodePosition.x, worldY - nodePosition.y, point.y - nodePosition.z);
    }

    private static Vector2 PerpendicularLeft(Vector2 direction)
    {
        return new Vector2(-direction.y, direction.x);
    }

    private static float PositiveAngleDelta(float from, float to)
    {
        // Mathf.DeltaAngle uses degrees wrapped to -180–180;
        // use positive 0–2π differences for CCW sectors between angle-sorted roads.
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
        // Intersection asphalt uses the representative connected variant's road material
        // (highest variant number), matching road-body appearance.
        // At paved/unpaved junctions, higher-numbered unpaved variants become representative,
        // making intersections dirt (asphalt when both are paved).
        // Use the same RoadPath material cache (same-name Resources reassignment + renderQueue adjustment)
        // so intersection pavement color/render settings match ordinary roads.
        if (paths != null)
        {
            // Prioritize road pavement at mixed road/track nodes (level crossings).
            // Track variants have higher numbers; simply choosing maximum variant would make
            // the entire crossing look like ballast. Correct appearance has continuous road pavement
            // with rail connectors crossing above it.
            // Within the same category (roads/tracks), retain highest-variant selection.
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
                // Master body_material (.mat name directly under Resources) defines the road-surface material.
                // Allows sheet-only intersection material replacement, independent of prefab material names.
                var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(selectedPath.path_variant) : null;
                Material masterMaterial = master != null ? RoadPath.GetOrCreateRoadMaterialByName(master.body_material) : null;
                if (masterMaterial != null)
                {
                    return masterMaterial;
                }

                // Variants without body_material resolve from body-prefab materials.
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

        // Fallback if neither master nor prefab resolves. Use the shared pool
        // to match ordinary-road renderQueue corrections too.
        Material material = RoadPath.GetOrCreateRoadMaterialByName("PATH_ASPHALT");
        if (material == null)
        {
            material = RoadPath.GetOrCreateRoadMaterialByName("NodeMaterial");
        }
        return material;
    }

    /// <summary>
    /// Create intersection top-surface mesh. Derive height from <see cref="SectionSurfacePoint"/>, normals from <see cref="SurfaceNormal"/>.
    /// </summary>
    private static Mesh BuildSurfaceMesh(Geometry geometry, Vector3 center, Dictionary<int, PathEndSection> sections, MainLineSurface mainLine, string meshName)
    {
        if (geometry == null || geometry.IsEmpty)
        {
            return null;
        }

        var points = new List<Vector2>();
        List<int> surface = TriangulateSurface(geometry, points, mainLine != null || !IsLevel(sections));
        if (points.Count < 3 || surface.Count < 3)
        {
            return null;
        }

        var vertices = new List<Vector3>(points.Count);
        var normals = new List<Vector3>(points.Count);
        foreach (Vector2 point in points)
        {
            // At cut sections, surface height matches ordinary road top (spline + BODY_ABOVE_SPLINE).
            // Roads are trimmed to intersection edges at cut points, so no overlapping-region Z-fighting occurs.
            Vector3 world = SectionSurfacePoint(new Vector3(point.x, 0f, point.y), sections, RoadPartHeights.BODY_ABOVE_SPLINE, mainLine);
            vertices.Add(world - center);
            normals.Add(SurfaceNormal(world, sections, mainLine));
        }

        // On Unity's XZ plane, directly placed 2D-counterclockwise triangles have downward normals.
        // Reverse each triangle's winding before registration for upward rendering.
        var triangles = new List<int>(surface.Count);
        for (int i = 0; i < surface.Count; i += 3)
        {
            triangles.Add(surface[i]);
            triangles.Add(surface[i + 2]);
            triangles.Add(surface[i + 1]);
        }

        Mesh mesh = new Mesh();
        mesh.name = meshName;
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        // Some road materials read vertex alpha as an edge mask (unpaved
        // Custom/PathDirt); explicitly set white (α=1=no erosion).
        // Depending on defaults for meshes without vertex colors could cut out
        // and hide the entire intersection.
        Color[] colors = new Color[vertices.Count];
        Array.Fill(colors, Color.white);
        mesh.SetColors(colors);
        mesh.SetNormals(normals);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;
    }

    /// <summary>
    /// Triangulate intersection outlines for road surfaces; Delaunay flips remove thin ear-clipping triangle fans.
    /// With refine, insert an equilateral-triangle grid at INTERSECTION_SURFACE_VERTEX_SPACING and split perimeter edges
    /// longer than twice that spacing (e.g. entrance sections). Perimeter-only vertices approximate graded surfaces with large planes,
    /// making diagonals visible as creases. Planar surfaces need no interior vertices.
    /// Append world-XZ vertices to points; return XZ-counterclockwise triangle indices.
    /// </summary>
    internal static List<int> TriangulateSurface(Geometry geometry, List<Vector2> points, bool refine)
    {
        var triangles = new List<int>();
        if (geometry is Polygon polygon)
        {
            AppendPolygonSurface(polygon, points, triangles, refine);
        }
        else if (geometry is MultiPolygon || geometry is GeometryCollection)
        {
            for (int i = 0; i < geometry.NumGeometries; i++)
            {
                triangles.AddRange(TriangulateSurface(geometry.GetGeometryN(i), points, refine));
            }
        }
        return triangles;
    }

    private static void AppendPolygonSurface(Polygon polygon, List<Vector2> points, List<int> triangles, bool refine)
    {
        Coordinate[] coords = polygon.ExteriorRing.Coordinates;
        if (coords == null || coords.Length < 4)
        {
            return;
        }

        Vector2[] ring = new Vector2[coords.Length - 1];
        for (int i = 0; i < coords.Length - 1; i++)
        {
            ring[i] = new Vector2((float)coords[i].X, (float)coords[i].Y);
        }
        if (PolygonUtils.SignedArea(ring) < 0f)
        {
            Array.Reverse(ring);
        }

        List<int> earClip = BuildingMeshBuilder.TriangulateEarClip(ring);
        if (earClip.Count == 0)
        {
            throw new InvalidOperationException("交差点の輪郭を三角形化できません。");
        }
        const float spacing = LocalConstants.INTERSECTION_SURFACE_VERTEX_SPACING;
        var local = new List<Vector2>(ring);
        var coarse = new List<(int a, int b, int c)>(earClip.Count / 3);
        for (int i = 0; i < earClip.Count; i += 3) coarse.Add((earClip[i], earClip[i + 1], earClip[i + 2]));
        BuildingMeshBuilder.FlipToDelaunay(local, coarse);
        var fine = coarse;
        if (refine)
        {
            BuildingMeshBuilder.InsertInteriorPoints(local, coarse, SurfaceLattice(ring, spacing));
            var flat = new List<int>(coarse.Count * 3);
            foreach (var (a, b, c) in coarse) { flat.Add(a); flat.Add(b); flat.Add(c); }
            fine = BuildingMeshBuilder.SubdivideTrianglesByEdgeLength(local, flat, spacing * 2f);
            BuildingMeshBuilder.FlipToDelaunay(local, fine);
        }

        int baseIndex = points.Count;
        points.AddRange(local);
        foreach (var (a, b, c) in fine)
        {
            triangles.Add(baseIndex + a);
            triangles.Add(baseIndex + b);
            triangles.Add(baseIndex + c);
        }
    }

    /// <summary>
    /// Equilateral-grid points inside the outline. Exclude points within half-spacing of perimeter to avoid thin boundary triangles.
    /// Fix grid to world coordinates for deterministic vertices from identical outlines.
    /// </summary>
    private static IEnumerable<Vector2> SurfaceLattice(Vector2[] ring, float spacing)
    {
        Vector2 min = ring[0], max = ring[0];
        foreach (Vector2 p in ring) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
        float row = spacing * Mathf.Sqrt(3f) * 0.5f;
        var crossings = new List<float>();
        for (int j = Mathf.FloorToInt(min.y / row); j <= Mathf.CeilToInt(max.y / row); j++)
        {
            float z = j * row, offset = (j & 1) * spacing * 0.5f;
            crossings.Clear();
            PolygonUtils.AppendRayCrossings(ring, z, crossings);
            crossings.Sort();
            for (int k = 0; k + 1 < crossings.Count; k += 2)
            {
                for (int i = Mathf.CeilToInt((crossings[k] - offset) / spacing); i * spacing + offset < crossings[k + 1]; i++)
                {
                    var point = new Vector2(i * spacing + offset, z);
                    if (PolygonUtils.DistanceToBoundary(ring, point) >= spacing * 0.5f) yield return point;
                }
            }
        }
    }
}
