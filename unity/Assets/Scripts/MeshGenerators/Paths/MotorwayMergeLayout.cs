using System;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// 本線2本と流入する1車線ランプの描画形状。保存中心線と占有ポリゴンは変更しない。
/// 下流の切断距離の前半で車線幅を保ち、後半だけを絞る。周辺物との干渉は調べない。
/// </summary>
public sealed class MotorwayMergeLayout
{
    public int RampId => ramp.Path.path_id;
    public int IncomingId => incoming.Path.path_id;
    public int OutgoingId => outgoing.Path.path_id;
    public float ParallelLength => outgoing.Section.Distance * .5f;
    public float LaneWidth => ramp.Lanes.Outer * 2f;

    private sealed class Arm
    {
        public PathSummary Path;
        public PathEndSection Section;
        public List<Vector3> Points;
        public RoadLaneLayout Lanes;
        public float SideWidth, CenterWidth;

        public Arm(PathSummary path, PathEndSection section, List<Vector3> points)
        {
            Path = path; Section = section; Points = points;
            RoadPath.GetPathMasterParams(path, out float width, out float sidewalk, out float side, out _,
                out int lanes, out _, out float center, out _, out _, out _, out _);
            Lanes = new RoadLaneLayout(width, sidewalk, side, center, lanes, path.travel_direction);
            SideWidth = side; CenterWidth = center;
        }
    }

    private readonly Arm ramp, incoming, outgoing;
    private readonly float side;
    private readonly Dictionary<int, PathEndSection> sections;
    private readonly List<IntersectionCorner> corners;
    private readonly Dictionary<(int from, int to, Vector3 start, Vector3 end), VehiclePolyline> connectors = new();
    private Vector3[] surfaceVertices;
    private int[] surfaceTriangles;
    private Vector3 surfaceOrigin;

    private MotorwayMergeLayout(Arm ramp, Arm incoming, Arm outgoing, float side,
        IntersectionPolygonUtil.IntersectionCalculationResult result)
    {
        this.ramp = ramp; this.incoming = incoming; this.outgoing = outgoing; this.side = side;
        sections = result.Sections; corners = result.Corners;
    }

    public static MotorwayMergeLayout Create(int nodeId, List<PathSummary> paths,
        IntersectionPolygonUtil.IntersectionCalculationResult result, Func<PathSummary, List<Vector3>> pointResolver)
    {
        if (paths.Count != 3 || result.Sections.Count != 3 || MasterDataStore.Instance == null) return null;
        Arm ramp = null;
        var main = new List<Arm>();
        foreach (var path in paths)
        {
            if (path.path_type != Constants.PATH_MOTORWAY || path.status != Constants.STATUS_ACTIVE) return null;
            var master = MasterDataStore.Instance.GetPathMaster(path.path_variant);
            if (master == null || (master.sidewalk_width ?? 0f) > 0f) return null;
            var points = new List<Vector3>(pointResolver != null ? pointResolver(path) : IntersectionPolygonUtil.SamplePlan(path));
            if (path.end_node_id == nodeId) points.Reverse();
            var arm = new Arm(path, result.Sections[path.path_id], points);
            if (master.ramp)
            {
                if (ramp != null || !arm.Lanes.OneWay || arm.Lanes.Count != 1
                    || !VehicleManager.DirectionAllowsEntry(path.travel_direction, path.end_node_id == nodeId)) return null;
                ramp = arm;
            }
            else main.Add(arm);
        }
        if (ramp == null || main.Count != 2) return null;
        // 本線の継続判定は通常の2本接合と同じ角度を使う。分岐や横断交差を合流に読み替えない。
        var a = IntersectionPolygonUtil.AtDistance(main[0].Points, 0).Direction;
        var b = IntersectionPolygonUtil.AtDistance(main[1].Points, 0).Direction;
        if (Vector2.Dot(a, b) >= -.8f) return null;
        var rampDirection = -IntersectionPolygonUtil.AtDistance(ramp.Points, 0).Direction;
        int outIndex = Vector2.Dot(rampDirection, a) > Vector2.Dot(rampDirection, b) ? 0 : 1;
        Arm outgoing = main[outIndex], incoming = main[1 - outIndex];
        if (!VehicleManager.DirectionAllowsEntry(outgoing.Path.travel_direction, outgoing.Path.start_node_id == nodeId)
            || !VehicleManager.DirectionAllowsEntry(incoming.Path.travel_direction, incoming.Path.end_node_id == nodeId)) return null;
        var start = IntersectionPolygonUtil.AtDistance(outgoing.Points, 0);
        float side = Mathf.Sign(Vector2.Dot(ramp.Section.Center - start.Center, start.Left));
        // 双方向本線では対向車線や中央分離帯を横断する接続には適用しない。
        if (!outgoing.Lanes.OneWay && side * -outgoing.Lanes.Center(0, CityInfoStore.TrafficHand) <= 0f) return null;
        var layout = new MotorwayMergeLayout(ramp, incoming, outgoing, side, result);
        layout.ReplaceOuterCorner();
        layout.ReplaceMainlineCorner();
        return layout;
    }

    private static float Smooth(float t) => Mathf.SmoothStep(0f, 1f, t);
    private float Taper(float distance) => Smooth((distance - ParallelLength) / ParallelLength);
    private float ParallelOuter => outgoing.Lanes.Outer + LaneWidth + ramp.SideWidth * .5f;
    private float ParallelCenter => outgoing.Lanes.Outer + LaneWidth * .5f;

    private Vector3 DownstreamPoint(float distance, float offset)
    {
        var sample = IntersectionPolygonUtil.AtDistance(outgoing.Points, distance);
        return sample.Position + new Vector3(sample.Left.x, 0f, sample.Left.y) * offset;
    }

    private static List<Vector3> Curve(Vector3 start, Vector2 fromDirection, Vector3 end, Vector2 toDirection)
    {
        // 平面の接線付き補間も通常の車両接続と共用し、高さは生成後に路面へ合わせる。
        start.y = end.y = 0f;
        return new List<Vector3>(VehiclePolyline.Connector(start, new Vector3(fromDirection.x, 0f, fromDirection.y),
            end, new Vector3(toDirection.x, 0f, toDirection.y), 1f / 3f).points);
    }

    private void ReplaceOuterCorner()
    {
        var corner = corners.Find(c => c.FromPath == RampId && c.ToPath == OutgoingId
            || c.FromPath == OutgoingId && c.ToPath == RampId);
        bool reverse = corner.FromPath != RampId;
        Vector2 first = corner.Points[reverse ? corner.Points.Count - 1 : 0];
        var points = Curve(new Vector3(first.x, 0f, first.y), -ramp.Section.Direction,
            DownstreamPoint(0, side * ParallelOuter), IntersectionPolygonUtil.AtDistance(outgoing.Points, 0).Direction);
        int count = Mathf.Clamp(Mathf.CeilToInt(outgoing.Section.Distance / .5f), 16, 256);
        // 偶数分割で並走・絞り込み境界を必ず含める。
        count += count % 2;
        for (int i = 1; i <= count; i++)
        {
            float distance = outgoing.Section.Distance * i / count;
            float width = Mathf.Lerp(ParallelOuter, outgoing.Section.Width * .5f, Taper(distance));
            points.Add(DownstreamPoint(distance, side * width));
        }
        SetCorner(corner, points, reverse);
    }

    private void ReplaceMainlineCorner()
    {
        var corner = corners.Find(c => c.FromPath == IncomingId && c.ToPath == OutgoingId
            || c.FromPath == OutgoingId && c.ToPath == IncomingId);
        SetCorner(corner, MainLine(-side * incoming.Section.Width * .5f, -side * outgoing.Section.Width * .5f),
            corner.FromPath != IncomingId);
    }

    private void SetCorner(IntersectionCorner corner, List<Vector3> points, bool reverse)
    {
        if (reverse) points.Reverse();
        corner.Points.Clear(); corner.Directions.Clear();
        for (int i = 0; i < points.Count; i++)
        {
            corner.Points.Add(IntersectionPolygonUtil.XZ(points[i]));
            corner.Directions.Add(IntersectionPolygonUtil.XZ(PolylineUtils.SmoothedDirection(points, i)));
        }
        corner.Directions[0] = -sections[corner.FromPath].Direction;
        corner.Directions[corner.Directions.Count - 1] = sections[corner.ToPath].Direction;
    }

    /// <summary>上面の三角形だけを保持し、生成済み路面と白線・走行位置の高さを一致させる。</summary>
    internal void SetSurface(Mesh mesh, Vector3 origin)
    {
        surfaceVertices = mesh.vertices; surfaceTriangles = mesh.triangles; surfaceOrigin = origin;
    }

    internal Vector3 SurfacePoint(Vector3 point, float offset)
    {
        Vector3 local = point - surfaceOrigin;
        if (surfaceTriangles != null) for (int i = 0; i < surfaceTriangles.Length; i += 3)
        {
            Vector3 a = surfaceVertices[surfaceTriangles[i]], b = surfaceVertices[surfaceTriangles[i + 1]], c = surfaceVertices[surfaceTriangles[i + 2]];
            float determinant = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
            if (Mathf.Abs(determinant) < 1e-8f) continue;
            float u = ((b.z - c.z) * (local.x - c.x) + (c.x - b.x) * (local.z - c.z)) / determinant;
            float v = ((c.z - a.z) * (local.x - c.x) + (a.x - c.x) * (local.z - c.z)) / determinant;
            if (u < -1e-5f || v < -1e-5f || u + v > 1.00001f) continue;
            point.y = a.y * u + b.y * v + c.y * (1f - u - v) + surfaceOrigin.y + offset - RoadPartHeights.BODY_ABOVE_SPLINE;
            return point;
        }
        // 壁の中心は路面外縁の外側にあるため、通常の断面補間で高さを延長する。
        return RoadIntersectionMeshGenerator.SectionSurfacePoint(point, sections, offset);
    }

    /// <summary>外側・内側は通行側と独立に求め、右側ランプも隣接する車線へつなぐ。</summary>
    public int TargetLane(int fromPath, int toPath, int lane, int trafficHand)
    {
        if (fromPath != RampId || toPath != OutgoingId) return lane;
        int target = 0;
        float best = float.NegativeInfinity;
        for (int i = 0; i < outgoing.Lanes.Count; i++)
        {
            float position = -outgoing.Lanes.Center(i, trafficHand) * side;
            if (position > best) { best = position; target = i; }
        }
        return target;
    }

    private List<Vector3> MainLine(float startOffset, float endOffset)
    {
        var points = new List<Vector3>();
        float before = incoming.Section.Distance, after = outgoing.Section.Distance, total = before + after;
        int count = Mathf.Clamp(Mathf.CeilToInt(total / .5f), 16, 256);
        for (int i = 0; i <= count; i++)
        {
            float distance = total * i / count;
            bool entering = distance <= before;
            var sample = IntersectionPolygonUtil.AtDistance(entering ? incoming.Points : outgoing.Points,
                entering ? before - distance : distance - before);
            Vector2 left = sample.Left * (entering ? -1f : 1f);
            float offset = Mathf.Lerp(startOffset, endOffset, Smooth(distance / total));
            points.Add(sample.Position + new Vector3(left.x, 0f, left.y) * offset);
        }
        return points;
    }

    private List<Vector3> RampLine(Vector3 start, float targetOffset)
    {
        var points = Curve(start, -ramp.Section.Direction, DownstreamPoint(0, side * ParallelCenter),
            IntersectionPolygonUtil.AtDistance(outgoing.Points, 0).Direction);
        int count = Mathf.Clamp(Mathf.CeilToInt(outgoing.Section.Distance / .5f), 16, 256);
        count += count % 2;
        for (int i = 1; i <= count; i++)
        {
            float distance = outgoing.Section.Distance * i / count;
            points.Add(DownstreamPoint(distance, Mathf.Lerp(side * ParallelCenter, targetOffset, Taper(distance))));
        }
        return points;
    }

    /// <summary>路面と同じ高さ補間を使い、車両ごとの端点高さ（バス等の接地差も含む）を維持する。</summary>
    public VehiclePolyline Connector(int fromPath, int toPath, Vector3 start, Vector3 end)
    {
        var key = (fromPath, toPath, start, end);
        if (connectors.TryGetValue(key, out var cached)) return cached;
        List<Vector3> points;
        if (fromPath == RampId && toPath == OutgoingId)
            points = RampLine(start, Vector2.Dot(IntersectionPolygonUtil.XZ(end) - outgoing.Section.Center, outgoing.Section.Left));
        else if (fromPath == IncomingId && toPath == OutgoingId)
            points = MainLine(-Vector2.Dot(IntersectionPolygonUtil.XZ(start) - incoming.Section.Center, incoming.Section.Left),
                Vector2.Dot(IntersectionPolygonUtil.XZ(end) - outgoing.Section.Center, outgoing.Section.Left));
        else if (fromPath == OutgoingId && toPath == IncomingId)
        {
            points = MainLine(-Vector2.Dot(IntersectionPolygonUtil.XZ(end) - incoming.Section.Center, incoming.Section.Left),
                Vector2.Dot(IntersectionPolygonUtil.XZ(start) - outgoing.Section.Center, outgoing.Section.Left));
            points.Reverse();
        }
        else return null;
        float fromHeight = start.y - sections[fromPath].Position.y, toHeight = end.y - sections[toPath].Position.y;
        for (int i = 0; i < points.Count; i++)
            points[i] = SurfacePoint(points[i], Mathf.Lerp(fromHeight, toHeight, (float)i / (points.Count - 1)));
        points[0] = start; points[points.Count - 1] = end;
        var connector = new VehiclePolyline(points);
        connectors[key] = connector;
        return connector;
    }

    public void AddMarkings(List<SidewalkConnectorSpec> specs, Vector3 node)
    {
        // 本線の中央分離帯を交差点口で終端せず、同じ本線点列に沿って継続する。
        if (incoming.CenterWidth > 0f && outgoing.CenterWidth > 0f)
        {
            specs.RemoveAll(s => s.Part == "center");
            AddPart(specs, MainLine(0, 0), incoming, outgoing, incoming.CenterWidth, outgoing.CenterWidth, node, "center");
        }
        if (incoming.SideWidth <= 0f || outgoing.SideWidth <= 0f || ramp.SideWidth <= 0f) return;
        var a = new List<(float offset, bool dashed)>(incoming.Lanes.Boundaries());
        var b = new List<(float offset, bool dashed)>(outgoing.Lanes.Boundaries());
        // Boundariesは保存パスの向きに対して左右対称なので、本線進行方向の左オフセットへそのまま対応付ける。
        int count = Mathf.Max(a.Count, b.Count);
        for (int i = 0; i < count; i++)
        {
            float from = a.Count == 0 ? 0 : a[Mathf.Min(i, a.Count - 1)].offset;
            float to = b.Count == 0 ? 0 : b[Mathf.Min(i, b.Count - 1)].offset;
            bool dashed = (a.Count == 0 || a[Mathf.Min(i, a.Count - 1)].dashed)
                && (b.Count == 0 || b[Mathf.Min(i, b.Count - 1)].dashed);
            AddPart(specs, MainLine(from, to), incoming, outgoing, incoming.SideWidth * .3f, outgoing.SideWidth * .3f, node, "line", dashed);
        }
        foreach (var corner in corners)
        {
            Arm from = FindArm(corner.FromPath), to = FindArm(corner.ToPath);
            var points = new List<Vector3>();
            for (int i = 0; i < corner.Points.Count; i++)
            {
                float inset = Mathf.Lerp(from.SideWidth, to.SideWidth, Smooth((float)i / (corner.Points.Count - 1))) * .5f;
                Vector2 direction = corner.Directions[i];
                Vector2 p = corner.Points[i] + new Vector2(-direction.y, direction.x) * inset;
                points.Add(new Vector3(p.x, 0f, p.y));
            }
            AddPart(specs, points, from, to, from.SideWidth * .3f, to.SideWidth * .3f, node, "line");
        }
        var boundary = new List<Vector3>();
        for (int i = 0; i <= 32; i++)
            boundary.Add(DownstreamPoint(outgoing.Section.Distance * i / 32f, side * outgoing.Lanes.Outer));
        AddPart(specs, boundary, outgoing, outgoing, outgoing.SideWidth * .3f, outgoing.SideWidth * .3f, node, "line", true);
    }

    private Arm FindArm(int path) => path == RampId ? ramp : path == IncomingId ? incoming : outgoing;

    private static void AddPart(List<SidewalkConnectorSpec> specs, List<Vector3> points, Arm from, Arm to,
        float fromWidth, float toWidth, Vector3 node, string part, bool dashed = false)
    {
        var widths = new List<float>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            points[i] -= node;
            widths.Add(Mathf.Lerp(fromWidth, toWidth, Smooth((float)i / (points.Count - 1))));
        }
        specs.Add(new SidewalkConnectorSpec(points, widths, from.Path.path_variant, from.Path.path_type)
        {
            Part = part, Dashed = dashed, EndPrefabVariant = to.Path.path_variant, EndFallbackType = to.Path.path_type,
            StartDirection = IntersectionPolygonUtil.XZ(points[1] - points[0]).normalized,
            EndDirection = IntersectionPolygonUtil.XZ(points[points.Count - 1] - points[points.Count - 2]).normalized,
        });
    }
}
