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
        var parent = paths.GetNodeIntersectionObject(node)?.transform.Find("Rail");   // 破棄済みでも本物の null が返る
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
        { SampleTrack(g,run.Forward,s,tram,out position,out tangent); return true; }
        bool ahead = run.Forward ? s > g.trimEnd : s < g.trimStart;
        int otherIndex = index+(ahead ? 1 : -1);
        if (otherIndex < 0 || otherIndex >= line.runs.Count)
        { SampleTrack(g,run.Forward,s,tram,out position,out tangent); return true; }
        var other = line.runs[otherIndex];
        if (other.path == run.path)
        { SampleTrack(g,run.Forward,s,tram,out position,out tangent); return true; }
        if (!geometry.TryGetValue(other.path,out var next)) return false;
        var a = ahead ? g : next; var b = ahead ? next : g;
        var ra = ahead ? run : other; var rb = ahead ? other : run;
        int node = ra.Forward ? a.data.end_node_id : a.data.start_node_id;
        if (node != (rb.Forward ? b.data.start_node_id : b.data.end_node_id)) return false;
        SampleTrack(a,ra.Forward,ra.Forward?a.trimEnd:a.trimStart,tram,out var p0,out var t0);
        SampleTrack(b,rb.Forward,rb.Forward?b.trimStart:b.trimEnd,tram,out var p1,out var t1);
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
        // レールコネクタのスプラインは軌道パーツの原点の高さにあるので踏面までを足す。埋め込み軌道を含む接続は lrt_track で描かれる。
        float height = tram ? (LrtRoadParts.IsEmbedded(g.data.path_variant) || LrtRoadParts.IsEmbedded(next.data.path_variant)
                ? LrtRoadParts.EMBEDDED_RAIL_TOP : LrtRoadParts.DEDICATED_RAIL_TOP)
            : g.rail ? .2f : 0f;
        position.y += height;
        if (g.rail || tram)
        {
            // 描画点列とスプライン評価の端部の差を吸収し、接続境界の位置を連続にする。
            Vector3 start = connector.points[reverse ? connector.points.Length-1 : 0];
            Vector3 end = connector.points[reverse ? 0 : connector.points.Length-1];
            position += Vector3.Lerp(p0-start-Vector3.up*height,p1-end-Vector3.up*height,progress);
        }
        return true;
    }

    /// <summary>停止中心から前後へ剛体の車体を並べる。隣接車は同じ車端位置を共有する。</summary>
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
            // 奇数編成は中央車の前後を対称に解き、折り返しても同じ剛体配置を再現する。
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

    private bool FindCarEnds(Route line,double reference,bool forward,double origin,float length,int side,
        out double rearDistance,out double frontDistance,out Vector3 rear,out Vector3 front)
    {
        rearDistance = frontDistance = origin; rear = front = default;
        var run = line.runs[FindRun(line.runs,origin)];
        if (!geometry.TryGetValue(run.path,out var g)) return false;
        // 時計の距離は実寸ではない。換算は探索の刻みだけに使い、車長はワールド座標で検査する。
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
            // パスを跨いで論理距離の倍率が変わる場合も、実際に進んだ距離で次の刻みを調整する。
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
            // Unityの座標精度は原点から離れるほど粗くなるため、到達不能なmm精度を要求しない。
            float magnitude = Mathf.Sqrt(Mathf.Max(a.sqrMagnitude,b.sqrMagnitude));
            float tolerance = Mathf.Max(.001f,magnitude*2e-7f);
            if (Math.Abs(chord-length) <= tolerance)
            { rearDistance=r; frontDistance=f; rear=a; front=b; solved=true; break; }
            if (chord < length) low = span; else high = span;
        }
        if (!solved) return false;
        // 車端の間に未ロードの短いパスや未生成の接続があれば、車体で跨いで補完しない。
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

    private static void SampleTrack(Geometry g,bool forward,float s,bool tram,out Vector3 p,out Vector3 t)
    {
        float sign = CityInfoStore.TrafficHand == Constants.TRAFFIC_HAND_LEFT ? -1 : 1;
        float offset = tram ? g.tramOffset : g.offset;
        if (g.rail || tram) g.full.SampleOffset(s,forward,offset*sign,out p,out t);
        else
        {
            g.full.Sample(s,forward,out p,out t);
            p += Vector3.Cross(Vector3.up,t).normalized*(offset*sign);
        }
        p.y += tram ? LrtRoadParts.RailTopAboveSpline(g.data.path_variant)
            : g.rail ? RoadPartHeights.TRACK_ABOVE_SPLINE + .2f : RoadPartHeights.BODY_ABOVE_SPLINE;
    }
    private static int FindRun(List<Run> runs,double distance)
    {
        int lo=0,hi=runs.Count-1;
        while(lo<hi) { int mid=(lo+hi+1)/2; if(runs[mid].start<=distance)lo=mid;else hi=mid-1; }
        return lo;
    }
}
