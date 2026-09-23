using System;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;
using Dreamteck.Splines;

/// <summary>車両と路線ハイライトが共有する方向付き経路。欠けた下地やレール接続を直線で補わない。</summary>
public sealed class TransitRouteGeometry
{
    private readonly PathManager paths;
    private readonly Dictionary<int, Geometry> geometry = new();
    private readonly Dictionary<int, List<VehiclePolyline>> railConnectors = new();
    public class Route
    {
        public TransitMotionClock motion;
        public int VehicleType;
        public readonly List<Run> runs = new();
    }
    public sealed class Run
    {
        public int path;
        public double start, end;
        public float from, to;
        public bool Forward => to > from;
    }
    private sealed class Geometry
    {
        public PathSummary data;
        public VehiclePolyline full, trimmed;
        public float trimStart, trimEnd, offset, tramOffset;
        public bool rail;
        public RoadLaneLayout lanes;
        public RoadMarkingTurns startTurns, endTurns;
        public Vector2 startIncoming, endIncoming;
    }

    public TransitRouteGeometry(PathManager paths) { this.paths = paths; }
    public void Clear() { geometry.Clear(); railConnectors.Clear(); }
    public bool HasPath(int path) => geometry.ContainsKey(path);
    public float GetOffset(int path) => geometry.TryGetValue(path, out var g) ? g.offset : 0;
    public static void BuildRuns(Route line)
    {
        line.runs.Clear();
        for (int i = 0; i < line.motion.Steps.Count; i++)
        {
            var s = line.motion.Steps[i];
            Run previous = line.runs.Count > 0 ? line.runs[line.runs.Count-1] : null;
            bool forward = s.to_fraction > s.from_fraction;
            if (previous != null && previous.path == s.path_id && previous.Forward == forward && Math.Abs(previous.to-s.from_fraction) < .0001)
            { previous.to = s.to_fraction; previous.end += s.length_m; }
            else line.runs.Add(new Run { path = s.path_id, start = line.motion.Distances[i], end = line.motion.Distances[i]+s.length_m, from = s.from_fraction, to = s.to_fraction });
        }
    }
    public void Refresh(HashSet<int> routePaths, IEnumerable<PathSummary> candidates)
    {
        Clear();
        foreach (var data in candidates)
        {
            if (!routePaths.Contains(data.path_id) || data.status != Constants.STATUS_ACTIVE || !paths.TryGetPathPolyline(data.path_id, out var full, false) || full.Count < 2) continue;
            if (!paths.TryGetPathPolyline(data.path_id, out var trimmed) || trimmed.Count < 2) continue;
            var g = new Geometry { data = data, full = new VehiclePolyline(full), trimmed = new VehiclePolyline(trimmed), rail = TrackIntersectionMeshGenerator.IsTrackType(data.path_type) };
            g.trimStart = g.full.Project(trimmed[0]); g.trimEnd = g.full.Project(trimmed[trimmed.Count-1]);
            if (g.rail)
            {
                TrackPath.GetTrackMasterParams(data, out float width, out int lanes, out _, out _, out _, out _, out _, out _);
                g.offset = Math.Abs(TrackPath.GetTrackCenterOffset(width, Math.Max(1,lanes), 0));
                ReadRailConnectors(data.start_node_id); ReadRailConnectors(data.end_node_id);
            }
            else
            {
                RoadPath.GetPathMasterParams(data, out float width, out float sidewalk, out float side, out _, out int lanes, out _, out float divider, out _, out _, out _, out _);
                g.offset = Math.Abs(new RoadLaneLayout(width,sidewalk,side,divider,lanes,data.travel_direction).Center(0,CityInfoStore.TrafficHand));
                g.lanes = new RoadLaneLayout(width,sidewalk,side,divider,lanes,data.travel_direction);
                g.startIncoming = -RoadMarkingRules.Outward(data, data.start_node_id);
                g.endIncoming = -RoadMarkingRules.Outward(data, data.end_node_id);
                if (RoadMarkingRules.HasPaint(data) && g.trimmed.Length >= 25f)
                {
                    g.startTurns = RoadMarkingRules.Available(data, data.start_node_id);
                    g.endTurns = RoadMarkingRules.Available(data, data.end_node_id);
                }
            }
            g.tramOffset = LrtRoadParts.IsEmbedded(data.path_variant) ? LrtRoadParts.Offset(data) : g.offset;
            if (LrtRoadParts.IsEmbedded(data.path_variant))
            { ReadRailConnectors(data.start_node_id); ReadRailConnectors(data.end_node_id); }
            geometry[data.path_id] = g;
        }
    }

    private void ReadRailConnectors(int node)
    {
        if (railConnectors.ContainsKey(node)) return;
        var list = new List<VehiclePolyline>(); railConnectors[node] = list;
        var parent = paths.GetNodeObject(node)?.transform.Find("Rail");
        if (parent == null) return;
        foreach (var spline in parent.GetComponentsInChildren<SplineComputer>())
        {
            if (!spline.gameObject.name.StartsWith("RailConnector_", StringComparison.Ordinal)) continue;
            var points = new List<Vector3>();
            for (int i = 0; i <= 48; i++) points.Add(spline.EvaluatePosition(i/48.0));
            list.Add(new VehiclePolyline(points));
        }
    }

    public bool Sample(Route line, double distance, double reference, bool referenceForward, out Vector3 position, out Vector3 tangent)
    {
        position = default; tangent = Vector3.forward;
        if (line.runs.Count == 0) return false;
        bool tram = line.VehicleType == Constants.VEHICLE_TRAM;
        int refRun = FindRun(line.runs, reference), index = FindRun(line.runs,distance);
        if (line.runs[refRun].Forward != referenceForward && refRun > 0 && Math.Abs(line.runs[refRun].start-reference)<.001) refRun--;
        // 終点で逆方向の脚へ車体を曲げず、同じ線路上に編成を保つ。
        if (index != refRun && line.runs[index].Forward != line.runs[refRun].Forward && line.runs[index].path == line.runs[refRun].path) index = refRun;
        var run = line.runs[index];
        if (!geometry.TryGetValue(run.path,out var g) || !VehicleManager.DirectionAllowsEntry(g.data.travel_direction,run.Forward)) return false;
        float fraction = run.from+(float)((distance-run.start)/Math.Max(.001,run.end-run.start))*(run.to-run.from);
        float s = fraction*g.full.Length;
        if (s < -.01 || s > g.full.Length+.01) return false;
        if (s >= g.trimStart && s <= g.trimEnd)
        { SampleRun(line,index,s,tram,out position,out tangent); return true; }
        bool ahead = run.Forward ? s > g.trimEnd : s < g.trimStart;
        int otherIndex = index+(ahead ? 1 : -1);
        if (otherIndex < 0 || otherIndex >= line.runs.Count)
        { SampleRun(line,index,s,tram,out position,out tangent); return true; }
        var other = line.runs[otherIndex];
        if (other.path == run.path)
        { SampleRun(line,index,s,tram,out position,out tangent); return true; }
        if (!geometry.TryGetValue(other.path,out var next)) return false;
        var a = ahead ? g : next; var b = ahead ? next : g;
        var ra = ahead ? run : other; var rb = ahead ? other : run;
        int node = ra.Forward ? a.data.end_node_id : a.data.start_node_id;
        if (node != (rb.Forward ? b.data.start_node_id : b.data.end_node_id)) return false;
        SampleRun(line,ahead?index:otherIndex,ra.Forward?a.trimEnd:a.trimStart,tram,out var p0,out var t0);
        SampleRun(line,ahead?otherIndex:index,rb.Forward?b.trimStart:b.trimEnd,tram,out var p1,out var t1);
        float tail = ra.Forward?a.full.Length-a.trimEnd:a.trimStart;
        float head = rb.Forward?b.trimStart:b.full.Length-b.trimEnd;
        float along = ahead ? (run.Forward?s-g.trimEnd:g.trimStart-s) : tail+(run.Forward?s:g.full.Length-s);
        VehiclePolyline connector = null;
        bool reverse = false;
        if (g.rail || tram)
        {
            float best = float.PositiveInfinity;
            if (railConnectors.TryGetValue(node,out var options)) foreach (var option in options)
            {
                float f = (option.points[0]-p0).sqrMagnitude+(option.points[option.points.Length-1]-p1).sqrMagnitude;
                float r = (option.points[0]-p1).sqrMagnitude+(option.points[option.points.Length-1]-p0).sqrMagnitude;
                if (Math.Min(f,r)<best) { best=Math.Min(f,r); connector=option; reverse=r<f; }
            }
            if (connector == null || best > 4) return false;
        }
        else connector = paths.GetMotorwayMerge(node)?.Connector(a.data.path_id, b.data.path_id, p0, p1)
            ?? VehiclePolyline.Connector(p0,t0,p1,t1,1f/3f);
        float progress = Mathf.Clamp01(along/Math.Max(.001f,tail+head));
        connector.Sample((reverse?1-progress:progress)*connector.Length,!reverse,out position,out tangent);
        if (tram) position.y += LrtRoadParts.IsEmbedded(g.data.path_variant) || LrtRoadParts.IsEmbedded(next.data.path_variant) ? LrtRoadParts.RailTop : .355f;
        else if (g.rail) position.y += .2f;
        return true;
    }

    private static void SampleTrack(Geometry g,bool forward,float s,bool tram,out Vector3 p,out Vector3 t)
    {
        g.full.Sample(s,forward,out p,out t);
        float sign = CityInfoStore.TrafficHand == Constants.TRAFFIC_HAND_LEFT ? -1 : 1;
        float offset = tram ? g.tramOffset : g.offset;
        p += Vector3.Cross(Vector3.up,t).normalized*(offset*sign);
        p.y += g.rail ? RoadPartHeights.TRACK_ABOVE_SPLINE + (tram ? .355f : .2f)
            : RoadPartHeights.BODY_ABOVE_SPLINE + (tram ? LrtRoadParts.RailTop : 0);
    }

    private void SampleRun(Route route, int index, float s, bool tram, out Vector3 position, out Vector3 tangent)
    {
        var run = route.runs[index]; var g = geometry[run.path];
        SampleTrack(g, run.Forward, s, tram, out position, out tangent);
        if (g.rail || tram || index + 1 >= route.runs.Count) return;
        var available = run.Forward ? g.endTurns : g.startTurns;
        if (available == RoadMarkingTurns.None) return;
        var nextRun = route.runs[index + 1];
        if (!geometry.TryGetValue(nextRun.path, out var next) || nextRun.path == run.path) return;
        int node = run.Forward ? g.data.end_node_id : g.data.start_node_id;
        if (node != (nextRun.Forward ? next.data.start_node_id : next.data.end_node_id)) return;
        Vector2 outgoing = -(nextRun.Forward ? next.startIncoming : next.endIncoming);
        if (outgoing.sqrMagnitude < .1f) outgoing = RoadMarkingRules.Outward(next.data, node);
        var turn = RoadMarkingRules.Turn(run.Forward ? g.endIncoming : g.startIncoming, outgoing);
        int lane = RoadMarkingRules.LaneForTurn(available, turn, g.lanes.Count, CityInfoStore.TrafficHand);
        float target = g.lanes.Center(lane, CityInfoStore.TrafficHand);
        float original = g.offset * (CityInfoStore.TrafficHand == Constants.TRAFFIC_HAND_LEFT ? -1 : 1);
        float remaining = run.Forward ? g.trimEnd - s : s - g.trimStart;
        float length = Mathf.Min(30f, g.trimmed.Length * .4f);
        float blend = Mathf.SmoothStep(0f, 1f, 1f - Mathf.Clamp01((remaining - 12f) / Mathf.Max(1f, length)));
        Vector3 right = Vector3.Cross(Vector3.up, tangent).normalized;
        position += right * ((target - original) * blend);
        float progress = Mathf.Clamp01(1f - (remaining - 12f) / Mathf.Max(1f, length));
        tangent = (tangent + right * ((target - original) * 6f * progress * (1f - progress) / Mathf.Max(1f, length))).normalized;
    }
    private static int FindRun(List<Run> runs,double distance)
    {
        int lo=0,hi=runs.Count-1;
        while(lo<hi) { int mid=(lo+hi+1)/2; if(runs[mid].start<=distance)lo=mid;else hi=mid-1; }
        return lo;
    }
}
