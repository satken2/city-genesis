using System;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;
using Dreamteck.Splines;

/// <summary>確定した路線時計から編成を配置する。画面外の進行と停車も同じ時計で再現する。</summary>
public sealed class TransitVehicleManager : MonoBehaviour
{
    [SerializeField] private float renderRadius = 480;
    [SerializeField] private int maxVisibleCars = 300;
    private PathManager paths;
    private double clock;
    private float scanTimer;
    private int revision = -1, session = -1;
    private readonly List<Line> lines = new();
    private readonly Dictionary<int, Geometry> geometry = new();
    private readonly Dictionary<string, GameObject> visible = new();
    private readonly Dictionary<string, Stack<GameObject>> pool = new();
    private readonly Dictionary<int, List<VehiclePolyline>> railConnectors = new();
    private readonly HashSet<string> used = new();
    private readonly Dictionary<GameObject,float> nominalLengths = new();
    private sealed class Line
    {
        public TransitLineSummary data;
        public TransitMotionClock motion;
        public readonly List<Run> runs = new();
        public int type;
        public Vector3[] positions, directions;
    }
    private sealed class Run
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

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<TransitVehicleManager>() == null)
            new GameObject("TransitVehicleManager").AddComponent<TransitVehicleManager>();
    }

    private void Update()
    {
        var store = TransitDataStore.Ensure();
        if (session != store.Session)
        {
            session = store.Session; clock = 0; revision = -1; lines.Clear(); geometry.Clear();
            HideUnused(true);
        }
        if (GameStateManager.Instance == null || !GameStateManager.Instance.IsGameStarted())
        { HideUnused(true); return; }
        if (paths == null) paths = FindAnyObjectByType<PathManager>();
        if (paths == null || CameraCellChecker.Instance == null) return;
        clock += Time.deltaTime;
        if (revision != store.Revision) { revision = store.Revision; RebuildLines(store.Lines); }
        Vector3 center = CameraCellChecker.Instance.CurrentWorldPosition;
        scanTimer -= Time.deltaTime;
        if (scanTimer <= 0) { scanTimer = .5f; RefreshGeometry(center); }
        used.Clear();
        foreach (var line in lines) DrawLine(line, center);
        HideUnused(false);
    }

    private void RebuildLines(List<TransitLineSummary> source)
    {
        lines.Clear();
        foreach (var data in source)
        {
            if (data.status != Constants.STATUS_ACTIVE || data.vehicle_count < Math.Max(1, data.cars_per_train)) continue;
            var line = new Line { data = data, motion = new TransitMotionClock(data.journeys), type = VehicleModelConfig.GetTransitVehicleType(data.path_variant) };
            if (line.motion.Duration <= 0 || line.motion.Length <= 0 || line.type == 0 || data.cars_per_train > maxVisibleCars) continue;
            line.positions = new Vector3[Math.Max(1,data.cars_per_train)]; line.directions = new Vector3[line.positions.Length];
            for (int i = 0; i < line.motion.Steps.Count; i++)
            {
                var s = line.motion.Steps[i];
                Run previous = line.runs.Count > 0 ? line.runs[line.runs.Count-1] : null;
                bool forward = s.to_fraction > s.from_fraction;
                if (previous != null && previous.path == s.path_id && previous.Forward == forward && Math.Abs(previous.to-s.from_fraction) < .0001)
                { previous.to = s.to_fraction; previous.end += s.length_m; }
                else line.runs.Add(new Run { path = s.path_id, start = line.motion.Distances[i], end = line.motion.Distances[i]+s.length_m, from = s.from_fraction, to = s.to_fraction });
            }
            lines.Add(line);
        }
        scanTimer = 0;
    }

    private void RefreshGeometry(Vector3 center)
    {
        geometry.Clear(); railConnectors.Clear();
        float margin = 0;
        foreach (var l in lines) margin = Mathf.Max(margin, l.data.cars_per_train*Mathf.Max(1,l.data.length_per_vehicle));
        foreach (var data in paths.GetPathsNearCircle(center, renderRadius + margin))
        {
            if (data.status != Constants.STATUS_ACTIVE || !paths.TryGetPathPolyline(data.path_id, out var full, false) || full.Count < 2) continue;
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

    private void DrawLine(Line line, Vector3 center)
    {
        int cars = Math.Max(1,line.data.cars_per_train), trains = line.data.vehicle_count/cars;
        float spacing = line.data.length_per_vehicle > 0 ? line.data.length_per_vehicle : VehicleModelResolver.GetVehicleLength(line.type);
        int stride = Math.Max(1,(int)Math.Ceiling((double)trains / Math.Max(1,maxVisibleCars/cars*4)));
        for (int train = 0; train < trains; train += stride)
        {
            double trainTime = clock + line.motion.Duration*train/trains;
            int step = line.motion.AtTime(trainTime, out double distance, out bool stopped);
            if (step < 0) continue;
            bool returning = line.motion.Returning[step];
            var positions = line.positions; var directions = line.directions;
            bool ready = true, near = false;
            for (int car = 0; car < cars; car++)
            {
                double offset = ((cars-1)*.5-car)*spacing;
                if (!Sample(line, distance+offset, distance, line.motion.Steps[step].to_fraction > line.motion.Steps[step].from_fraction, out positions[car], out directions[car])) { ready = false; break; }
                if (line.type == Constants.VEHICLE_BUS && stopped) BusTurnaround(line,step,trainTime,ref positions[car],ref directions[car]);
                Vector3 delta = positions[car]-center; delta.y = 0;
                near |= delta.sqrMagnitude <= renderRadius*renderRadius;
            }
            if (!ready || !near || used.Count+cars > maxVisibleCars) continue;
            for (int car = 0; car < cars; car++)
            {
                // 折り返しでは車体の前後を維持し、進行方向に対する編成順を逆転する。
                int identity = returning ? cars-1-car : car;
                bool middle = identity > 0 && identity < cars-1;
                string modelKey = line.type + (middle ? ":middle" : ":0");
                string key = line.data.line_agent_id+":"+train+":"+identity+":"+modelKey;
                used.Add(key);
                if (!visible.TryGetValue(key, out var go))
                {
                    if (pool.TryGetValue(modelKey,out var stack) && stack.Count > 0) go = stack.Pop();
                    else go = VehicleModelResolver.CreateTransitCar(line.type,middle,transform);
                    go.name = modelKey; visible[key] = go; go.SetActive(true);
                }
                if (!nominalLengths.TryGetValue(go,out float fitted) || fitted != spacing)
                { VehicleModelResolver.FitTransitLength(go,spacing); nominalLengths[go]=spacing; }
                Vector3 forward = directions[car] * (returning && line.type != Constants.VEHICLE_BUS ? -1 : 1);
                if (identity == cars-1 && cars > 1 && line.type != Constants.VEHICLE_BUS) forward = -forward;
                go.transform.SetPositionAndRotation(positions[car],Quaternion.LookRotation(forward,Vector3.up));
            }
        }
    }

    private void BusTurnaround(Line line,int index,double time,ref Vector3 position,ref Vector3 direction)
    {
        var step=line.motion.Steps[index];
        var next=line.motion.Steps[(index+1)%line.motion.Steps.Count];
        if (next.path_id!=step.path_id || (next.to_fraction-next.from_fraction)*(step.to_fraction-step.from_fraction)>=0 || !geometry.TryGetValue(step.path_id,out var g)) return;
        double phase=((time%line.motion.Duration)+line.motion.Duration)%line.motion.Duration;
        double elapsed=phase-line.motion.Times[index]-step.duration_seconds;
        double duration=Math.Min(5,step.dwell_seconds);
        if(duration<=0)return;
        float t=Mathf.Clamp01((float)((elapsed-(step.dwell_seconds-duration))/duration));
        Vector3 right=Vector3.Cross(Vector3.up,direction).normalized;
        float offset=g.offset*(CityInfoStore.TrafficHand==Constants.TRAFFIC_HAND_LEFT?-1:1);
        float angle=t*Mathf.PI;
        if(Math.Abs(offset)<.001f) { direction=Quaternion.AngleAxis(t*180,Vector3.up)*direction; return; }
        position+=right*offset*(Mathf.Cos(angle)-1)+direction*Math.Abs(offset)*Mathf.Sin(angle);
        direction=(direction*Mathf.Cos(angle)-right*Mathf.Sign(offset)*Mathf.Sin(angle)).normalized;
    }

    private bool Sample(Line line, double distance, double reference, bool referenceForward, out Vector3 position, out Vector3 tangent)
    {
        position = default; tangent = Vector3.forward;
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
    private void HideUnused(bool all)
    {
        var remove = new List<string>();
        foreach(var entry in visible)
        {
            if(!all && used.Contains(entry.Key)) continue;
            var go=entry.Value; go.SetActive(false);
            if(!pool.TryGetValue(go.name,out var stack)) { stack=new Stack<GameObject>();pool[go.name]=stack; }
            stack.Push(go);remove.Add(entry.Key);
        }
        foreach(var key in remove)visible.Remove(key);
    }
}
