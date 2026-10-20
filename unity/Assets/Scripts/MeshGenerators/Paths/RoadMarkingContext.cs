using System;
using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;
using UnityEngine;

/// <summary>Distance coordinates/approach marking plan shared by fixed LOD intervals of the same spline.</summary>
public sealed class RoadMarkingContext : MonoBehaviour
{
    public RoadMarkingPlan Plan { get; private set; }
    public RoadLaneLayout Lanes { get; private set; }
    public float LineWidth { get; private set; }
    public float Length { get; private set; }
    public float Phase { get; private set; }
    public Vector4 Approaches { get; private set; }
    public Vector4[] Bands { get; private set; }

    /// <summary>Rendering intervals indexed like Bands (x=start, y=end spline distance, m). Omit each line outside its interval.</summary>
    public Vector4[] BandSpans { get; private set; }
    public int BandCount { get; private set; }
    private readonly List<Vector3> points = new();
    private readonly List<float> distances = new();
    private readonly Dictionary<Vector2Int, List<int>> cells = new();
    private readonly List<Vector4> bandList = new();
    private readonly List<Vector4> spanList = new();
    private const float CellSize = 16f;
    private const float Unbounded = 1e9f;

    public static RoadMarkingContext Create(GameObject parent, SplineComputer spline, PathSummary path,
        bool startApproach, bool endApproach)
    {
        if (!RoadMarkingRules.HasPaint(path)) return null;
        var context = parent.AddComponent<RoadMarkingContext>();
        RoadPath.GetPathMasterParams(path, out float width, out float sidewalk, out float side, out _, out int lanes,
            out _, out float center, out _, out _, out _, out _);
        context.Lanes = new RoadLaneLayout(width, sidewalk, side, center, lanes, path.travel_direction);
        context.LineWidth = Mathf.Max(.1f, side * .3f);
        int count = Mathf.Clamp(Mathf.CeilToInt(spline.CalculateLength()), 2, 32768);
        float distance = 0;
        for (int i = 0; i <= count; i++)
        {
            Vector3 p = spline.EvaluatePosition((double)i / count);
            if (i > 0) distance += Vector3.Distance(context.points[i - 1], p);
            context.points.Add(p); context.distances.Add(distance);
            if (i > 0) context.Index(i - 1, width * .6f + 1f);
        }
        context.Length = distance;
        context.Phase = ProjectPlanDistance(path, context.points[0]);
        // First two bands are left/right edge lines (OverrideSideLine references their indices).
        context.bandList.Add(new Vector4(-context.Lanes.Outer, context.LineWidth, 0, 0));
        context.bandList.Add(new Vector4(context.Lanes.Outer, context.LineWidth, 0, 0));
        foreach (var boundary in context.Lanes.Boundaries()) context.bandList.Add(new Vector4(boundary.offset, context.LineWidth, boundary.dashed ? 1 : 0, 1));
        foreach (var _ in context.bandList) context.spanList.Add(new Vector4(-Unbounded, Unbounded, 0, 0));
        context.RefreshBands();
        context.Plan = new RoadMarkingPlan();
        if (startApproach) context.BuildApproach(path, true, sidewalk, center);
        if (endApproach) context.BuildApproach(path, false, sidewalk, center);
        return context;
    }

    /// <summary>
    /// Vary edge lines by interval. Inside ranges (ascending spline distances m, x=start/y=end), draw dashed if dashed is true,
    /// otherwise omit; keep solid outside. Used for motorway main-line/ramp joins (RoadAdjacency).
    /// Call before body generation, which passes this to shaders.
    /// </summary>
    public void OverrideSideLine(bool left, List<Vector2> ranges, bool dashed)
    {
        if (ranges == null || ranges.Count == 0) return;
        int index = left ? 0 : 1;
        Vector4 band = bandList[index];
        var pieces = new List<(Vector2 span, bool inside)>();
        float cursor = -Unbounded;
        foreach (var range in ranges)
        {
            // Extend intervals touching road ends past them, avoiding zero-length solid remnants.
            float from = range.x <= 1e-3f ? -Unbounded : range.x;
            float to = range.y >= Length - 1e-3f ? Unbounded : range.y;
            if (from > cursor) pieces.Add((new Vector2(cursor, from), false));
            pieces.Add((new Vector2(from, to), true));
            cursor = Mathf.Max(cursor, to);
        }
        if (cursor < Unbounded) pieces.Add((new Vector2(cursor, Unbounded), false));
        bool first = true;
        foreach (var (span, inside) in pieces)
        {
            if (inside && !dashed) continue;
            var piece = new Vector4(band.x, band.y, inside ? 1 : band.z, band.w);
            var spanValue = new Vector4(span.x, span.y, 0, 0);
            if (first)
            {
                bandList[index] = piece;
                spanList[index] = spanValue;
                first = false;
            }
            else
            {
                bandList.Add(piece);
                spanList.Add(spanValue);
            }
        }
        RefreshBands();
    }

    private void RefreshBands()
    {
        // Limit only markings beyond supported lane counts, respecting shader array bounds.
        Bands = new Vector4[32];
        BandSpans = new Vector4[32];
        BandCount = Mathf.Min(Bands.Length, bandList.Count);
        bandList.CopyTo(0, Bands, 0, BandCount);
        spanList.CopyTo(0, BandSpans, 0, BandCount);
    }

    private static float ProjectPlanDistance(PathSummary path, Vector3 p)
    {
        var line = IntersectionPolygonUtil.SamplePlan(path);
        float best = float.PositiveInfinity, result = 0, s = 0;
        Vector2 q = IntersectionPolygonUtil.XZ(p);
        for (int i = 1; i < line.Count; i++)
        {
            Vector2 a = IntersectionPolygonUtil.XZ(line[i - 1]), delta = IntersectionPolygonUtil.XZ(line[i]) - a;
            float t = Mathf.Clamp01(Vector2.Dot(q - a, delta) / Mathf.Max(.000001f, delta.sqrMagnitude));
            float d = (q - a - delta * t).sqrMagnitude;
            if (d < best) { best = d; result = s + delta.magnitude * t; }
            s += delta.magnitude;
        }
        return result;
    }

    private void BuildApproach(PathSummary path, bool start, float sidewalk, float center)
    {
        // Do not treat tunnel-section rendering endpoints or internal polyline endpoints as intersections.
        if (RoadMarkingRules.IsApproachEndpoint != null && !RoadMarkingRules.IsApproachEndpoint(path, start, start ? points[0] : points[points.Count - 1])) return;
        int node = start ? path.start_node_id : path.end_node_id;
        var turns = RoadMarkingRules.Available(path, node);
        if (turns == RoadMarkingTurns.None || Length < 16f) return;
        float S(float fromEnd) => start ? fromEnd : Length - fromEnd;
        bool inbound = VehicleManager.DirectionAllowsEntry(path.travel_direction, !start);
        float approach = Mathf.Min(18f, Length * .4f);
        var values = Approaches;
        if (start) values.x = approach; else values.y = approach;
        bool crossing = sidewalk > 0f && RoadMarkingRules.ConnectedPaths(node).Exists(p => p.path_id != path.path_id && p.path_type == Constants.PATH_STREET && p.status == Constants.STATUS_ACTIVE
            && (MasterDataStore.Instance?.GetPathMaster(p.path_variant)?.sidewalk_width ?? 0f) > 0);
        if (crossing)
        {
            // Keep markings on road surfaces outside intersections, off medians.
            for (float t = -Lanes.Outer + .25f; t < Lanes.Outer - .25f; t += .9f)
            {
                if (center > 0 && Mathf.Abs(t) < center * .5f + .3f) continue;
                Plan.Strip(new Vector2(S(1f), t), new Vector2(S(3.5f), t), .45f);
            }
            if (start) values.z = 4f; else values.w = 4f;
        }
        Approaches = values;
        if (!inbound) return;
        float sideSign = CityInfoStore.TrafficHand == Constants.TRAFFIC_HAND_LEFT ? -1 : 1;
        float directionSign = start ? -1 : 1;
        float outer = Lanes.OneWay ? Lanes.Outer : Lanes.Outer * sideSign * directionSign;
        float inner = Lanes.OneWay ? -Lanes.Outer : Lanes.Inner * sideSign * directionSign;
        Plan.Strip(new Vector2(S(crossing ? 5f : 1.5f), inner), new Vector2(S(crossing ? 5f : 1.5f), outer), .35f);
        if (Length < 25f) return;
        for (int lane = 0; lane < Lanes.Count; lane++)
        {
            float offset = Lanes.Center(lane, CityInfoStore.TrafficHand) * directionSign;
            var allowed = RoadMarkingRules.ForLane(turns, lane, Lanes.Count, CityInfoStore.TrafficHand);
            float laneWidth = Lanes.OneWay ? 2f * Lanes.Outer / Lanes.Count : (Lanes.Outer - Lanes.Inner) / Lanes.Count;
            Plan.Arrow(new Vector2(S(10f), offset), new Vector2(directionSign, 0), allowed, Mathf.Min(1f, laneWidth / 3f));
        }
    }

    private Vector2Int Cell(Vector3 p) => new(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.z / CellSize));
    private void Index(int i, float margin)
    {
        Vector3 a = points[i], b = points[i + 1];
        var min = Cell(Vector3.Min(a, b) - Vector3.one * margin);
        var max = Cell(Vector3.Max(a, b) + Vector3.one * margin);
        for (int z = min.y; z <= max.y; z++) for (int x = min.x; x <= max.x; x++)
        {
            var key = new Vector2Int(x, z);
            if (!cells.TryGetValue(key, out var indices)) cells[key] = indices = new List<int>();
            indices.Add(i);
        }
    }

    public Vector4 Coordinate(Vector3 world, Vector3 normal)
    {
        if (!cells.TryGetValue(Cell(world), out var indices)) return Vector4.zero;
        float best = float.PositiveInfinity, s = 0f, t = 0f, height = 0f;
        foreach (int i in indices)
        {
            Vector3 a = points[i], delta = points[i + 1] - a;
            Vector2 dxz = IntersectionPolygonUtil.XZ(delta), q = IntersectionPolygonUtil.XZ(world - a);
            float fraction = Mathf.Clamp01(Vector2.Dot(q, dxz) / Mathf.Max(.000001f, dxz.sqrMagnitude));
            float d = (world - Vector3.Lerp(a, a + delta, fraction)).sqrMagnitude;
            if (d >= best) continue;
            best = d; s = Mathf.Lerp(distances[i], distances[i + 1], fraction);
            t = Vector2.Dot(q - dxz * fraction, new Vector2(dxz.y, -dxz.x).normalized);
            height = a.y + delta.y * fraction + RoadPartHeights.BODY_ABOVE_SPLINE;
        }
        float top = normal.y > .35f && world.y >= height - .08f ? 1f : 0f;
        return new Vector4(s, t, top, 0f);
    }

    public Vector3 World(Vector2 coordinate)
    {
        int lo = 0, hi = distances.Count - 1;
        while (lo + 1 < hi) { int mid = (lo + hi) / 2; if (distances[mid] < coordinate.x) lo = mid; else hi = mid; }
        float t = Mathf.InverseLerp(distances[lo], distances[hi], coordinate.x);
        Vector3 tangent = points[hi] - points[lo];
        return Vector3.Lerp(points[lo], points[hi], t) + Vector3.Cross(Vector3.up, tangent).normalized * coordinate.y;
    }

    public void Apply(GameObject body)
    {
        var filter = body.GetComponent<MeshFilter>(); var renderer = body.GetComponent<MeshRenderer>();
        if (filter == null || filter.sharedMesh == null || renderer == null) return;
        var mesh = filter.sharedMesh;
        var vertices = mesh.vertices; var normals = mesh.normals; var uv = new List<Vector4>(vertices.Length);
        for (int i = 0; i < vertices.Length; i++) uv.Add(Coordinate(body.transform.TransformPoint(vertices[i]),
            normals.Length == vertices.Length ? body.transform.TransformDirection(normals[i]) : Vector3.up));
        mesh.SetUVs(3, uv);
        var surface = body.GetComponent<RoadMarkingSurface>();
        if (surface == null) surface = body.AddComponent<RoadMarkingSurface>();
        surface.Configure(Plan, this);
    }

    private void OnDestroy() { Plan?.ReleaseMesh(); }
}
