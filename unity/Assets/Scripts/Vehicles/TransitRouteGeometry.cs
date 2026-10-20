using System;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;
using Dreamteck.Splines;

/// <summary>Directed route shared by vehicles and line highlights. Do not bridge missing underlying paths or rail connections with straight lines.</summary>
public sealed class TransitRouteGeometry
{
    /// <summary>Crossover length (m) for a train reversing on the same path, moving from arrival to departure track.</summary>
    public const float CrossoverLength = 50f;
    private readonly PathManager paths;
    private readonly Dictionary<int, Geometry> geometry = new();
    private readonly Dictionary<int, List<VehiclePolyline>> railConnectors = new();
    public class Route
    {
        public TransitMotionClock motion;
        public int VehicleType;
        /// <summary>Rendered train length (m). 0 for displays without trains, such as color bands, disables reversal crossovers.</summary>
        public float ConsistLength;
        /// <summary>Path containing the stop. As in the core, the train may traverse its own platform regardless of allowed direction.</summary>
        public readonly HashSet<int> StopPaths = new();
        public readonly List<Run> runs = new();
    }
    public sealed class Run
    {
        public int path;
        public double start, end;
        public float from, to;
        /// <summary>Copy shifted by one circuit to solve vehicle ends across the seam. Skip when rendering the route itself, such as color bands.</summary>
        public bool Wrapped;
        /// <summary>Section departing a stop in reverse on the same path as the preceding run. Paths with at least two tracks switch the running track.</summary>
        public bool Turnback;
        public bool Forward => to > from;
    }
    private sealed class Geometry
    {
        public PathSummary data;
        public VehiclePolyline full, trimmed;
        public float trimStart, trimEnd, offset, tramOffset;
        /// <summary>Lateral track position for tracks with TrackEndTransition S-curves. null without an S-curve, using constant offset.</summary>
        public TrackLateralProfile lateral;
        public bool rail;
        /// <summary>Station platform, a variant with platform_width. Lines can run both ways at their stops regardless of permitted direction.</summary>
        public bool platform;
        /// <summary>Island-platform station. Reversing trains cross over after leaving the path to avoid crossing the platform between tracks.</summary>
        public bool island;
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
        line.StopPaths.Clear();
        int count = line.motion.Steps.Count;
        for (int i = 0; i < count; i++)
        {
            var s = line.motion.Steps[i];
            // Stops are boundaries between stop-to-stop sections, on the arriving section's last path and departing section's first path.
            if (i == 0 || line.motion.JourneyIndices[i] != line.motion.JourneyIndices[i-1])
            { line.StopPaths.Add(s.path_id); line.StopPaths.Add(line.motion.Steps[(i+count-1)%count].path_id); }
            Run previous = line.runs.Count > 0 ? line.runs[line.runs.Count-1] : null;
            bool forward = s.to_fraction > s.from_fraction;
            if (previous != null && previous.path == s.path_id && previous.Forward == forward && Math.Abs(previous.to-s.from_fraction) < .0001)
            { previous.to = s.to_fraction; previous.end += s.length_m; }
            else line.runs.Add(new Run { path = s.path_id, start = line.motion.Distances[i], end = line.motion.Distances[i]+s.length_m, from = s.from_fraction, to = s.to_fraction });
        }
        MarkTurnbacks(line);
        // For lines not reversing at the seam, add one-circuit-shifted copies before/after the circuit so vehicle ends spanning it resolve as adjacent run sections.
        if (!line.motion.Closed || line.runs.Count == 0) return;
        double length = line.motion.Length;
        var body = new List<Run>(line.runs);
        line.runs.Clear();
        foreach (var r in body) line.runs.Add(Shifted(r, -length));
        line.runs.AddRange(body);
        foreach (var r in body) line.runs.Add(Shifted(r, length));
    }
    private static Run Shifted(Run r, double by)
        => new Run { path = r.path, start = r.start + by, end = r.end + by, from = r.from, to = r.to, Wrapped = true, Turnback = r.Turnback };

    /// <summary>
    /// Mark reversals where a section departs opposite to the previous moving section on the same path. Skip zero-length dwell-only sections.
    /// For lines reversing at the seam, compare the first section with the last section arriving at the first stop.
    /// </summary>
    private static void MarkTurnbacks(Route line)
    {
        Run previous = null;
        if (!line.motion.Closed)
            for (int i = line.runs.Count-1; i >= 0 && previous == null; i--) if (Moves(line.runs[i])) previous = line.runs[i];
        foreach (var run in line.runs)
        {
            if (!Moves(run)) continue;
            run.Turnback = previous != null && previous.path == run.path && previous.Forward != run.Forward && Math.Abs(previous.to-run.from) < .0001;
            previous = run;
        }
    }
    private static bool Moves(Run run) => Math.Abs(run.to-run.from) > 1e-6f;
    public void Refresh(HashSet<int> routePaths, IEnumerable<PathSummary> candidates)
    {
        Clear();
        foreach (var data in candidates)
        {
            if (!routePaths.Contains(data.path_id) || data.status != Constants.STATUS_ACTIVE || !paths.TryGetPathPolyline(data.path_id, out var full, false) || full.Count < 2) continue;
            if (!paths.TryGetPathPolyline(data.path_id, out var trimmed) || trimmed.Count < 2) continue;
            var g = new Geometry { data = data, full = new VehiclePolyline(full), trimmed = new VehiclePolyline(trimmed), rail = TrackIntersectionMeshGenerator.IsTrackType(data.path_type),
                platform = (MasterDataStore.Instance?.GetPathMaster(data.path_variant)?.platform_width ?? 0f) > 0f };
            g.trimStart = g.full.Project(trimmed[0]); g.trimEnd = g.full.Project(trimmed[trimmed.Count-1]);
            if (g.rail)
            {
                TrackPath.GetTrackMasterParams(data, out float width, out int lanes, out _, out _, out _, out _, out _, out _);
                g.offset = Math.Abs(TrackPath.GetTrackCenterOffset(width, Math.Max(1,lanes), 0, TrackPath.GetTrackCenterWidth(data)));
                TrackLateralProfile profile = paths.GetTrackLateralProfile(data);
                g.lateral = profile != null && !profile.IsUniform ? profile : null;
                g.island = TrackPath.IsIslandPlatform(data);
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
            g.tramOffset = LrtRoadParts.IsLrt(data.path_variant) ? LrtRoadParts.Offset(data) : g.offset;
            if (LrtRoadParts.IsEmbedded(data.path_variant))
            { ReadRailConnectors(data.start_node_id); ReadRailConnectors(data.end_node_id); }
            geometry[data.path_id] = g;
        }
    }

    private void ReadRailConnectors(int node)
    {
        if (railConnectors.ContainsKey(node)) return;
        var list = new List<VehiclePolyline>(); railConnectors[node] = list;
        var parent = paths.GetNodeIntersectionObject(node)?.transform.Find("Rail");   // Returns a real null even after destruction.
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
        int refRun = FindRun(line.runs, reference), index = FindRun(line.runs,distance);
        if (line.runs[refRun].Forward != referenceForward && refRun > 0 && Math.Abs(line.runs[refRun].start-reference)<.001) refRun--;
        // At the terminus, keep the train on the same track rather than bending bodies toward the opposite leg.
        if (index != refRun && line.runs[index].Forward != line.runs[refRun].Forward && line.runs[index].path == line.runs[refRun].path) index = refRun;
        if (!CrossoverProgress(line,index,distance,out float progress))
            return SampleOnTrack(line,index,distance,false,out position,out tangent);
        if (progress <= 0) return SampleOnTrack(line,index,distance,true,out position,out tangent);
        if (!SampleOnTrack(line,index,distance,false,out position,out tangent)
            || !SampleOnTrack(line,index,distance,true,out var arrival,out var arrivalTangent)) return false;
        float weight = 1-Mathf.SmoothStep(0,1,progress);
        // Include lateral-shift slope, the distance derivative of weight, in the tangent for continuous crossover orientation.
        float slope = -6*progress*(1-progress)/CrossoverLength;
        tangent = (Vector3.Lerp(tangent,arrivalTangent,weight)+(arrival-position)*slope).normalized;
        position = Vector3.Lerp(position,arrival,weight);
        return true;
    }

    /// <summary>
    /// Reversing-train crossover progress: ≤0 on the arrival track, ≥1 on the departure track. Cross over CrossoverLength from the stopped train's leading
    /// end in departure direction. Each car samples its own position, so the train crosses progressively from the front.
    /// At island-platform reversals, start crossing only after leaving the station path to avoid crossing the platform between tracks.
    /// Treat clock distance, the core's horizontal length, as meters. Buses turn while stopped and have no crossover.
    /// </summary>
    private bool CrossoverProgress(Route line, int index, double distance, out float progress)
    {
        progress = 1;
        if (line.ConsistLength <= 0 || line.VehicleType == Constants.VEHICLE_BUS) return false;
        double head = line.ConsistLength*.5;
        for (int i = index; i >= 0; i--)
        {
            var run = line.runs[i];
            double begin = run.start+head;
            if (run.Turnback)
            {
                if (geometry.TryGetValue(run.path, out var g) && g.island) begin = Math.Max(begin, run.end);
                progress = (float)((distance-begin)/CrossoverLength);
                return progress < 1;
            }
            if (begin+CrossoverLength <= distance) return false;
        }
        return false;
    }

    /// <summary>Render clock distance on run section index. arrivalTrack uses the opposite travel-side track, where the train arrived before reversing.</summary>
    private bool SampleOnTrack(Route line, int index, double distance, bool arrivalTrack, out Vector3 position, out Vector3 tangent)
    {
        position = default; tangent = Vector3.forward;
        bool tram = line.VehicleType == Constants.VEHICLE_TRAM;
        var run = line.runs[index];
        if (!geometry.TryGetValue(run.path,out var g) || !((g.platform && line.StopPaths.Contains(run.path)) || VehicleManager.DirectionAllowsEntry(g.data.travel_direction,run.Forward))) return false;
        float fraction = run.from+(float)((distance-run.start)/Math.Max(.001,run.end-run.start))*(run.to-run.from);
        float s = fraction*g.full.Length;
        if (s < -.01 || s > g.full.Length+.01) return false;
        if (s >= g.trimStart && s <= g.trimEnd)
        { SampleRun(line,index,s,tram,arrivalTrack,out position,out tangent); return true; }
        bool ahead = run.Forward ? s > g.trimEnd : s < g.trimStart;
        int otherIndex = index+(ahead ? 1 : -1);
        if (otherIndex < 0 || otherIndex >= line.runs.Count)
        { SampleRun(line,index,s,tram,arrivalTrack,out position,out tangent); return true; }
        var other = line.runs[otherIndex];
        if (other.path == run.path)
        { SampleRun(line,index,s,tram,arrivalTrack,out position,out tangent); return true; }
        if (!geometry.TryGetValue(other.path,out var next)) return false;
        var a = ahead ? g : next; var b = ahead ? next : g;
        var ra = ahead ? run : other; var rb = ahead ? other : run;
        int node = ra.Forward ? a.data.end_node_id : a.data.start_node_id;
        if (node != (rb.Forward ? b.data.start_node_id : b.data.end_node_id)) return false;
        int indexA = ahead?index:otherIndex, indexB = ahead?otherIndex:index;
        float endA = ra.Forward?a.trimEnd:a.trimStart, endB = rb.Forward?b.trimStart:b.trimEnd;
        SampleRun(line,indexA,endA,tram,arrivalTrack,out var p0,out var t0);
        SampleRun(line,indexB,endB,tram,arrivalTrack,out var p1,out var t1);
        float tail = ra.Forward?a.full.Length-a.trimEnd:a.trimStart;
        float head = rb.Forward?b.trimStart:b.full.Length-b.trimEnd;
        float along = ahead ? (run.Forward?s-g.trimEnd:g.trimStart-s) : tail+(run.Forward?s:g.full.Length-s);
        // Straight track connections without slabs render both paths to the node and have no rail connector. Clamp endpoint error to the path end.
        if ((g.rail || tram) && tail+head < .05f)
        { SampleRun(line,index,Mathf.Clamp(s,g.trimStart,g.trimEnd),tram,arrivalTrack,out position,out tangent); return true; }
        VehiclePolyline connector = null;
        bool reverse = false;
        if (g.rail || tram)
        {
            if (!FindRailConnector(node,p0,p1,out connector,out reverse))
            {
                // At nodes lacking a connection on the arrival track used for crossover, such as branches into one-way tracks,
                // shift the departure-track connection to the arrival-track endpoint using the endpoint alignment below.
                if (!arrivalTrack) return false;
                SampleRun(line,indexA,endA,tram,false,out var q0,out _);
                SampleRun(line,indexB,endB,tram,false,out var q1,out _);
                if (!FindRailConnector(node,q0,q1,out connector,out reverse)) return false;
            }
        }
        else connector = VehiclePolyline.Connector(p0,t0,p1,t1,1f/3f);
        float progress = Mathf.Clamp01(along/Math.Max(.001f,tail+head));
        connector.Sample((reverse?1-progress:progress)*connector.Length,!reverse,out position,out tangent);
        // Rail-connector splines lie at the track-part origin height, so add the tread height. Connections involving embedded track render with lrt_track.
        float height = tram ? (LrtRoadParts.IsEmbedded(g.data.path_variant) || LrtRoadParts.IsEmbedded(next.data.path_variant)
                ? LrtRoadParts.EMBEDDED_RAIL_TOP : LrtRoadParts.DEDICATED_RAIL_TOP)
            : g.rail ? RunningSurface(g) : 0f;
        position.y += height;
        if (g.rail || tram)
        {
            // Absorb endpoint differences between rendered polylines and spline evaluation to keep connection boundaries continuous.
            Vector3 start = connector.points[reverse ? connector.points.Length-1 : 0];
            Vector3 end = connector.points[reverse ? 0 : connector.points.Length-1];
            position += Vector3.Lerp(p0-start-Vector3.up*height,p1-end-Vector3.up*height,progress);
        }
        return true;
    }

    /// <summary>Rendered node rail connection with endpoints nearest p0/p1. Treat as absent if summed squared endpoint error exceeds 4m².</summary>
    private bool FindRailConnector(int node, Vector3 p0, Vector3 p1, out VehiclePolyline connector, out bool reverse)
    {
        connector = null; reverse = false;
        float best = float.PositiveInfinity;
        if (railConnectors.TryGetValue(node,out var options)) foreach (var option in options)
        {
            float f = (option.points[0]-p0).sqrMagnitude+(option.points[option.points.Length-1]-p1).sqrMagnitude;
            float r = (option.points[0]-p1).sqrMagnitude+(option.points[option.points.Length-1]-p0).sqrMagnitude;
            if (Math.Min(f,r)<best) { best=Math.Min(f,r); connector=option; reverse=r<f; }
        }
        return connector != null && best <= 4;
    }

    /// <summary>Arrange rigid vehicle bodies forward/backward from the stopping center, sharing end positions between adjacent cars.</summary>
    public bool SampleConsist(Route line, double distance, bool referenceForward, float carLength,
        Vector3[] positions, Vector3[] directions)
    {
        int count = positions.Length, middle = count / 2;
        if (count == 0 || directions.Length != count || carLength <= 0) return false;
        if (!Sample(line,distance,distance,referenceForward,out var center,out _)) return false;
        double frontDistance = distance, rearDistance = distance;
        Vector3 front = center, rear = center;
        if (count % 2 != 0)
        {
            // For odd-length trains, solve both ends of the center car symmetrically to reproduce the same rigid placement after reversal.
            if (!FindCarEnds(line,distance,referenceForward,distance,carLength,0,
                out rearDistance,out frontDistance,out rear,out front)) return false;
            positions[middle] = (front+rear)*.5f;
            directions[middle] = (front-rear).normalized;
        }
        for (int car = middle-1; car >= 0; car--)
        {
            if (!FindCarEnds(line,distance,referenceForward,frontDistance,carLength,1,
                out _,out double nextDistance,out var a,out var b)) return false;
            positions[car] = (a+b)*.5f; directions[car] = (b-a).normalized;
            frontDistance = nextDistance;
        }
        for (int car = middle+count%2; car < count; car++)
        {
            if (!FindCarEnds(line,distance,referenceForward,rearDistance,carLength,-1,
                out double nextDistance,out _,out var a,out var b)) return false;
            positions[car] = (a+b)*.5f; directions[car] = (b-a).normalized;
            rearDistance = nextDistance;
        }
        return true;
    }

    /// <summary>Retain the stop's specified center and derive bus pose with the same vehicle-end search as rail.</summary>
    public bool SampleBus(Route line, double distance, double reference, bool forward, float length,
        out Vector3 position, out Vector3 tangent)
    {
        if (!Sample(line,distance,reference,forward,out position,out tangent)) return false;
        if (FindCarEnds(line,reference,forward,distance,length,0,out _,out _,out var rear,out var front))
        { tangent = (front-rear).normalized; return true; }
        // Bus stopping centers may lie at road ends. If front/rear points exceed the route, derive the pose within the current road to keep rendering.
        int index = FindRun(line.runs,reference);
        if (line.runs[index].Forward != forward && index > 0 && Math.Abs(line.runs[index].start-reference)<.001) index--;
        var run = line.runs[index]; var g = geometry[run.path];
        float s = (run.from+(float)((distance-run.start)/Math.Max(.001,run.end-run.start))*(run.to-run.from))*g.full.Length;
        float sign = CityInfoStore.TrafficHand == Constants.TRAFFIC_HAND_LEFT ? -1 : 1;
        g.full.SampleOffsetPose(s,run.Forward,g.offset*sign,length,out _,out tangent);
        return true;
    }

    private bool FindCarEnds(Route line,double reference,bool forward,double origin,float length,int side,
        out double rearDistance,out double frontDistance,out Vector3 rear,out Vector3 front)
    {
        rearDistance = frontDistance = origin; rear = front = default;
        var run = line.runs[FindRun(line.runs,origin)];
        if (!geometry.TryGetValue(run.path,out var g)) return false;
        // Clock distance is not physical distance. Use conversion only for search steps; check vehicle length in world coordinates.
        double unitsPerMetre = (run.end-run.start)/Math.Max(.001,g.full.Length*Math.Abs(run.to-run.from));
        double stride = Math.Max(.00001,length*.25*unitsPerMetre);
        double low = 0, high = stride;
        if (!Sample(line,origin,reference,forward,out var previousRear,out _)) return false;
        Vector3 previousFront = previousRear;
        bool found = false;
        for (int probe = 0; probe < 32; probe++,high += stride)
        {
            bool valid = SampleCarEnds(line,reference,forward,origin,high,side,out _,out _,out var a,out var b);
            if (!valid || Vector3.Distance(a,b) >= length) { found = true; break; }
            // Adjust the next step by actual distance traveled even when logical-distance scale changes across paths.
            double travelled = Vector3.Distance(a,previousRear)+Vector3.Distance(b,previousFront);
            stride *= Math.Clamp(length*.25/Math.Max(.00001,travelled),.25,4);
            previousRear = a; previousFront = b;
            low = high;
        }
        if (!found) return false;
        bool solved = false;
        for (int iteration = 0; iteration < 20; iteration++)
        {
            double span = (low+high)*.5;
            bool valid = SampleCarEnds(line,reference,forward,origin,span,side,
                out double r,out double f,out var a,out var b);
            if (!valid) { high = span; continue; }
            float chord = Vector3.Distance(a,b);
            // Unity coordinate precision decreases away from the origin, so do not require unattainable millimeter accuracy.
            float magnitude = Mathf.Sqrt(Mathf.Max(a.sqrMagnitude,b.sqrMagnitude));
            float tolerance = Mathf.Max(.001f,magnitude*2e-7f);
            if (Math.Abs(chord-length) <= tolerance)
            { rearDistance=r; frontDistance=f; rear=a; front=b; solved=true; break; }
            if (chord < length) low = span; else high = span;
        }
        if (!solved) return false;
        // Do not bridge unloaded short paths or ungenerated connections between vehicle ends using the body itself.
        int first = FindRun(line.runs,rearDistance)+1;
        for (int i = first; i < line.runs.Count && line.runs[i].start < frontDistance; i++)
            if (!Sample(line,line.runs[i].start,reference,forward,out _,out _)) return false;
        return true;
    }

    private bool SampleCarEnds(Route line,double reference,bool forward,double origin,double span,int side,
        out double rearDistance,out double frontDistance,out Vector3 rear,out Vector3 front)
    {
        rearDistance = origin - (side < 0 ? span : side == 0 ? span*.5 : 0);
        frontDistance = origin + (side > 0 ? span : side == 0 ? span*.5 : 0);
        front = default;
        return Sample(line,rearDistance,reference,forward,out rear,out _)
            && Sample(line,frontDistance,reference,forward,out front,out _);
    }

    private static void SampleTrack(Geometry g,bool forward,float s,bool tram,bool arrivalTrack,out Vector3 p,out Vector3 t)
    {
        float sign = CityInfoStore.TrafficHand == Constants.TRAFFIC_HAND_LEFT ? -1 : 1;
        float offset = tram ? g.tramOffset : g.offset;
        // Use the outermost traffic-side track in travel direction, matching TrackPath.TrackIndexForTravel. Arrival track is opposite, or identical for single track.
        if (g.rail && !tram && g.lateral != null)
        {
            // S-curve joints vary lateral track position by location, so use the rail mesh's shape and add lateral-position derivative to direction.
            int track = TrackPath.TrackIndexForTravel(g.lateral.TrackCount, arrivalTrack ? !forward : forward);
            float fromEnd = g.full.Length - s;
            float lateral = g.lateral.Lateral(track, s, fromEnd);
            g.full.SampleOffset(s, forward, forward ? lateral : -lateral, out p, out t);
            t = (t + Vector3.Cross(Vector3.up, t).normalized * g.lateral.Slope(track, s, fromEnd)).normalized;
        }
        else g.full.SampleOffset(s,forward,offset*sign*(arrivalTrack ? -1 : 1),out p,out t);
        p.y += tram ? LrtRoadParts.RailTopAboveSpline(g.data.path_variant)
            : g.rail ? RoadPartHeights.TRACK_ABOVE_SPLINE + RunningSurface(g) : RoadPartHeights.BODY_ABOVE_SPLINE;
    }

    /// <summary>Height from track-part origin to wheel contact: rail head for railways, beam top for straddle monorails.</summary>
    private static float RunningSurface(Geometry g) => g.data.path_type == Constants.PATH_MONORAIL ? RoadPartHeights.MONORAIL_BEAM_TOP_ABOVE_TRACK : .2f;

    private void SampleRun(Route route, int index, float s, bool tram, bool arrivalTrack, out Vector3 position, out Vector3 tangent)
    {
        var run = route.runs[index]; var g = geometry[run.path];
        SampleTrack(g, run.Forward, s, tram, arrivalTrack, out position, out tangent);
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
        // Also interpolate lane offsets on vertex cross-sections to prevent lateral jumps at centerline segment transitions.
        g.full.SampleOffset(s,run.Forward,Mathf.Lerp(original,target,blend),out position,out tangent);
        position.y += RoadPartHeights.BODY_ABOVE_SPLINE;
        g.full.SampleOffset(s,run.Forward,1f,out var side,out _);
        g.full.SampleOffset(s,run.Forward,0f,out var center,out _);
        Vector3 right = side - center;
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
