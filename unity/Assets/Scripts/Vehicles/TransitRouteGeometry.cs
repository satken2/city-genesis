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
        public float trimStart, trimEnd, offset;
        public bool rail;
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
            }
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
            var points = new List<Vector3>();
            for (int i = 0; i <= 48; i++) points.Add(spline.EvaluatePosition(i/48.0));
            list.Add(new VehiclePolyline(points));
        }
    }

    public bool Sample(Route line, double distance, double reference, bool referenceForward, out Vector3 position, out Vector3 tangent)
    {
        position = default; tangent = Vector3.forward;
        if (line.runs.Count == 0) return false;
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
        { SampleTrack(g,run.Forward,s,out position,out tangent); return true; }
        bool ahead = run.Forward ? s > g.trimEnd : s < g.trimStart;
        int otherIndex = index+(ahead ? 1 : -1);
        if (otherIndex < 0 || otherIndex >= line.runs.Count)
        { SampleTrack(g,run.Forward,s,out position,out tangent); return true; }
        var other = line.runs[otherIndex];
        if (other.path == run.path)
        { SampleTrack(g,run.Forward,s,out position,out tangent); return true; }
        if (!geometry.TryGetValue(other.path,out var next)) return false;
        var a = ahead ? g : next; var b = ahead ? next : g;
        var ra = ahead ? run : other; var rb = ahead ? other : run;
        int node = ra.Forward ? a.data.end_node_id : a.data.start_node_id;
        if (node != (rb.Forward ? b.data.start_node_id : b.data.end_node_id)) return false;
        SampleTrack(a,ra.Forward,ra.Forward?a.trimEnd:a.trimStart,out var p0,out var t0);
        SampleTrack(b,rb.Forward,rb.Forward?b.trimStart:b.trimEnd,out var p1,out var t1);
        float tail = ra.Forward?a.full.Length-a.trimEnd:a.trimStart;
        float head = rb.Forward?b.trimStart:b.full.Length-b.trimEnd;
        float along = ahead ? (run.Forward?s-g.trimEnd:g.trimStart-s) : tail+(run.Forward?s:g.full.Length-s);
        VehiclePolyline connector = null;
        bool reverse = false;
        if (g.rail)
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
        else connector = VehiclePolyline.Connector(p0,t0,p1,t1,1f/3f);
        float progress = Mathf.Clamp01(along/Math.Max(.001f,tail+head));
        connector.Sample((reverse?1-progress:progress)*connector.Length,!reverse,out position,out tangent);
        if (g.rail) position.y += .2f;
        return true;
    }

    private static void SampleTrack(Geometry g,bool forward,float s,out Vector3 p,out Vector3 t)
    {
        g.full.Sample(s,forward,out p,out t);
        float sign = CityInfoStore.TrafficHand == Constants.TRAFFIC_HAND_LEFT ? -1 : 1;
        p += Vector3.Cross(Vector3.up,t).normalized*(g.offset*sign);
        p.y += g.rail ? RoadPartHeights.TRACK_ABOVE_SPLINE+.2f : RoadPartHeights.BODY_ABOVE_SPLINE;
    }
    private static int FindRun(List<Run> runs,double distance)
    {
        int lo=0,hi=runs.Count-1;
        while(lo<hi) { int mid=(lo+hi+1)/2; if(runs[mid].start<=distance)lo=mid;else hi=mid-1; }
        return lo;
    }
}
