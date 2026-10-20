using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Travel polyline with cumulative distances, shared by lanes and intersections.</summary>
public sealed class VehiclePolyline
{
    public readonly Vector3[] points;
    private readonly float[] distances;
    public float Length => distances[distances.Length - 1];
    private int shapeHash;
    private bool shapeHashed;

    /// <summary>Hash of the entire point list, calculated once and retained because points are immutable after construction.</summary>
    public int ShapeHash
    {
        get
        {
            if (!shapeHashed)
            {
                int h = points.Length;
                foreach (var p in points) h = unchecked(h * 31 + p.GetHashCode());
                shapeHash = h;
                shapeHashed = true;
            }
            return shapeHash;
        }
    }

    public VehiclePolyline(IReadOnlyList<Vector3> source)
    {
        if (source == null || source.Count < 2) throw new ArgumentException("走行点列には2点以上必要です。");
        points = new Vector3[source.Count];
        distances = new float[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            points[i] = source[i];
            if (i > 0) distances[i] = distances[i - 1] + Vector3.Distance(points[i - 1], points[i]);
        }
    }

    public void Sample(float s, bool forward, out Vector3 position, out Vector3 tangent)
    {
        s = Mathf.Clamp(s, 0, Length);
        int segment = FindSegment(s);
        float length = distances[segment] - distances[segment - 1];
        position = Vector3.Lerp(points[segment - 1], points[segment], length > .0001f ? (s - distances[segment - 1]) / length : 0);
        tangent = (points[segment] - points[segment - 1]).normalized * (forward ? 1 : -1);
        if (tangent.sqrMagnitude < .001f) tangent = forward ? Vector3.forward : Vector3.back;
    }

    /// <summary>Determine lateral positions per vertex and interpolate to avoid track-position jumps at segment boundaries.</summary>
    public void SampleOffset(float s, bool forward, float offset, out Vector3 position, out Vector3 tangent)
    {
        s = Mathf.Clamp(s, 0, Length);
        int segment = FindSegment(s);
        float sign = forward ? 1 : -1;
        Vector3 a = points[segment - 1] + Vector3.Cross(Vector3.up, PolylineUtils.SmoothedDirection(points, segment - 1)).normalized * (offset * sign);
        Vector3 b = points[segment] + Vector3.Cross(Vector3.up, PolylineUtils.SmoothedDirection(points, segment)).normalized * (offset * sign);
        float length = distances[segment] - distances[segment - 1];
        position = Vector3.Lerp(a, b, length > .0001f ? (s - distances[segment - 1]) / length : 0);
        tangent = (b - a).normalized * sign;
        if (tangent.sqrMagnitude < .001f) tangent = forward ? Vector3.forward : Vector3.back;
    }

    /// <summary>Retain body-center position and derive orientation from front/rear points, clamping both within the polyline at road ends.</summary>
    public void SampleOffsetPose(float s, bool forward, float offset, float length, out Vector3 position, out Vector3 tangent)
    {
        SampleOffset(s, forward, offset, out position, out tangent);
        SampleOffset(s - length * .5f, forward, offset, out var a, out _);
        SampleOffset(s + length * .5f, forward, offset, out var b, out _);
        if ((b - a).sqrMagnitude > .000001f) tangent = (b - a).normalized * (forward ? 1 : -1);
    }

    private int FindSegment(float s)
    {
        int lo = 1, hi = distances.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (distances[mid] < s) lo = mid + 1; else hi = mid;
        }
        int segment = lo;
        while (segment < distances.Length - 1 && distances[segment] - distances[segment - 1] < .0001f) segment++;
        return segment;
    }

    public float Project(Vector3 point) => ProjectRange(point, 0, Length);

    /// <summary>Whether point lists match exactly, allowing reuse of built polylines on roads with unchanged geometry.</summary>
    public static bool SameSource(VehiclePolyline line, IReadOnlyList<Vector3> source)
    {
        if (line == null || source == null || line.points.Length != source.Count) return false;
        for (int i = 0; i < source.Count; i++)
            if (!line.points[i].Equals(source[i])) return false;
        return true;
    }

    /// <summary>
    /// Map distance s on the old polyline to the same location on this one. Progress ratios would jump when intersection cuts change,
    /// so project the old position near its original progress, limiting the window to avoid snapping across looped roads.
    /// </summary>
    public float RemapFrom(VehiclePolyline previous, float s)
    {
        if (previous == null || previous == this) return Mathf.Clamp(s, 0, Length);
        previous.Sample(s, true, out var point, out _);
        float guess = previous.Length > 0 ? s / previous.Length * Length : 0;
        float window = Mathf.Abs(Length - previous.Length) + 10f;
        return ProjectRange(point, guess - window, guess + window);
    }

    /// <summary>Project only near a known distance, avoiding repeated full-road scans for vehicle-body placement checks.</summary>
    public float ProjectRange(Vector3 point, float from, float to)
    {
        float best = float.PositiveInfinity, result = 0;
        from = Mathf.Clamp(from, 0, Length);
        to = Mathf.Clamp(to, from, Length);
        int first = System.Array.BinarySearch(distances, from);
        first = Mathf.Clamp(first < 0 ? ~first : first, 1, points.Length - 1);
        for (int i = first; i < points.Length && distances[i - 1] <= to; i++)
        {
            float t = PolylineUtils.ProjectOnSegmentXZ(point, points[i - 1], points[i]);
            float length = distances[i] - distances[i - 1];
            if (length > .0001f)
                t = Mathf.Clamp(t, Mathf.Clamp01((from - distances[i - 1]) / length), Mathf.Clamp01((to - distances[i - 1]) / length));
            Vector3 closest = Vector3.Lerp(points[i - 1], points[i], t);
            float error = new Vector2(point.x - closest.x, point.z - closest.z).sqrMagnitude;
            if (error < best) { best = error; result = distances[i - 1] + t * (distances[i] - distances[i - 1]); }
        }
        return result;
    }

    /// <summary>Solve segment/circle intersections, excluding outside-circle sections even with repeated entry/exit.</summary>
    public List<Vector2> InsideIntervals(Vector3 center, float radius)
    {
        var result = new List<Vector2>();
        for (int i = 1; i < points.Length; i++)
        {
            Vector2 a = new Vector2(points[i - 1].x - center.x, points[i - 1].z - center.z);
            Vector2 delta = new Vector2(points[i].x - points[i - 1].x, points[i].z - points[i - 1].z);
            float aa = Vector2.Dot(delta, delta), from = 0, to = 1;
            if (aa < .000001f)
            {
                if (a.sqrMagnitude > radius * radius) continue;
            }
            else
            {
                float b = Vector2.Dot(a, delta), c = a.sqrMagnitude - radius * radius;
                float discriminant = b * b - aa * c;
                if (discriminant <= 0) continue;
                float root = Mathf.Sqrt(discriminant);
                from = Mathf.Max(0, (-b - root) / aa);
                to = Mathf.Min(1, (-b + root) / aa);
                if (to <= from) continue;
            }
            float length = distances[i] - distances[i - 1];
            float start = distances[i - 1] + from * length, end = distances[i - 1] + to * length;
            if (end - start < .0001f) continue;
            if (result.Count > 0 && Mathf.Abs(result[result.Count - 1].y - start) < .001f)
                result[result.Count - 1] = new Vector2(result[result.Count - 1].x, end);
            else result.Add(new Vector2(start, end));
        }
        return result;
    }

    /// <summary>Return only newly visible sections, excluding overlap with the previously managed circle.</summary>
    public List<Vector2> UncoveredIntervals(List<Vector2> current, Vector3 previousCenter, float previousRadius)
    {
        var covered = InsideIntervals(previousCenter, previousRadius);
        var result = new List<Vector2>();
        foreach (var interval in current)
        {
            float start = interval.x;
            foreach (var old in covered)
            {
                if (old.y <= start) continue;
                if (old.x >= interval.y) break;
                if (old.x > start) result.Add(new Vector2(start, old.x));
                start = Mathf.Max(start, old.y);
                if (start >= interval.y) break;
            }
            if (start < interval.y) result.Add(new Vector2(start, interval.y));
        }
        return result;
    }

    public static VehiclePolyline Connector(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float tension)
    {
        float d = Vector3.Distance(p0, p1) * Mathf.Clamp(tension, .1f, .6f);
        var points = new Vector3[33];
        for (int i = 0; i < points.Length; i++)
        {
            float t = (float)i / (points.Length - 1);
            points[i] = PolylineUtils.CubicBezierPoint(p0, p0 + t0 * d, p1 - t1 * d, p1, t);
        }
        return new VehiclePolyline(points);
    }
}

/// <summary>
/// Steady-state density for newly managed sections. Sample shortages within the overall population cap without rounding up road fractions.
/// Used to separate camera-movement initial placement from ordinary inflow at entrances/boundaries.
/// </summary>
public sealed class TrafficPopulationSeed
{
    private readonly List<Vector2> intervals;
    private readonly float length;
    private float remaining;
    public float Weight => Mathf.Max(0, remaining);

    public TrafficPopulationSeed(List<Vector2> intervals, float density)
    {
        this.intervals = intervals;
        foreach (var interval in intervals) length += interval.y - interval.x;
        remaining = length * density;
    }

    public void Observe(float s)
    {
        foreach (var interval in intervals)
            if (s >= interval.x && s <= interval.y) { remaining--; break; }
    }

    public float Sample()
    {
        float pick = UnityEngine.Random.value * length;
        foreach (var interval in intervals)
        {
            float size = interval.y - interval.x;
            if (pick <= size) return interval.x + pick;
            pick -= size;
        }
        return intervals[intervals.Count - 1].y;
    }

    public void Consume() => remaining--;
    public void Skip() => remaining = 0;
}

/// <summary>
/// One car's state, independent of rendering objects. Inside intersections, use polylines already offset to the lane.
/// Retain scheduled arrivals across paths, pulling to the shoulder only where sufficient interior road length exists.
/// </summary>
public class Vehicle
{
    public int pathId, vehicleType, variantId, lane;
    public int previousPath = -1, previousPath2 = -1;   // Previous one and two paths traversed, passed to citizen sampling for a clicked entity.
    public bool forward;
    public float s, speed, age, distanceSinceJunction;
    public float motionRemaining, motionFreeSpeed;   // motionRemaining is remaining seconds to road end using core travel time, carried across roads; negative means late.
    public bool holdAtEnd;   // Whether the next node is an intersection with connection count other than 2. Do not wait at simple split joints; carry delay to the next intersection.
    public float stuckSeconds;   // Time without position changes after the deadline, on the traffic-animation clock.
    public int entryPath = -1, entryLane;   // Road/lane before entering the intersection, retained while inside so the car remains the leader for following traffic there.
    public bool entryForward;
    public float entryLength;
    public float laneOffset, shoulderOffset;
    public float roadHalfWidth;
    public int destinationParcel, destinationBuilding;
    public int accessGeneration = -1, searchedPath = -1;
    public Vector3 destinationPoint;
    public GameObject go;
    public VehiclePolyline road;
    public VehiclePolyline connector;
    public float connectorS, connectorSpeed;
    public bool arrivalPending, fading;
    public float arrivalDistance = -1, fadeElapsed;
    public float arrivalBlendLength = 15;
    public float spawnBlendRemaining;
    public bool ExitingRoad { get; private set; }
    public bool ExitComplete => ExitingRoad && exitRotationElapsed >= ExitRotationDuration && exitTravelled >= exitLength;
    private const float ExitRotationDuration = .5f;
    private Vector3 exitStart, exitForward, exitSide;
    private float exitRotationElapsed, exitTravelled, exitLength;
    public bool InIntersection => connector != null;
    public float TotalLength => road.Length;
    public float Remaining => forward ? TotalLength - s : s;
    /// <summary>Lane-to-shoulder blend for a scheduled arrival (0 to 1); 0 without an arrival.</summary>
    public float ArrivalBlend => arrivalDistance >= 0 ? 1 - Mathf.Clamp01(arrivalDistance / Mathf.Max(.1f, arrivalBlendLength)) : 0;

    /// <summary>
    /// Cancel the scheduled arrival. Spawn merging (spawnBlendRemaining) uses the same lane/shoulder interpolation,
    /// so transfer the current blend there and return to the lane by distance traveled without a lateral jump.
    /// </summary>
    public void ReleaseArrival()
    {
        spawnBlendRemaining = Mathf.Max(spawnBlendRemaining, ArrivalBlend * 15f);
        arrivalDistance = -1;
        destinationParcel = 0;
    }

    public void SetPath(int id, VehiclePolyline points, bool travelForward, float startS = -1)
    {
        pathId = id; road = points; forward = travelForward;
        s = startS < 0 ? (forward ? 0 : TotalLength) : Mathf.Clamp(startS, 0, TotalLength);
        distanceSinceJunction = 0;
    }

    public void Sample(out Vector3 position, out Vector3 tangent)
    {
        if (ExitingRoad)
        {
            position = exitStart + exitSide * exitTravelled;
            tangent = Vector3.Slerp(exitForward, exitSide, Mathf.SmoothStep(0, 1, exitRotationElapsed / ExitRotationDuration));
            return;
        }
        if (InIntersection) { connector.Sample(connectorS, true, out position, out tangent); return; }
        float blend = Mathf.Max(Mathf.Clamp01(spawnBlendRemaining / 15f), ArrivalBlend);
        float offset = Mathf.Lerp(laneOffset, shoulderOffset, blend);
        // Directly using short terrain-following segment slopes would jump pitch at vertices; derive pose from points a standard car length apart.
        road.SampleOffsetPose(s, forward, offset, 4.5f, out position, out tangent);
    }

    /// <summary>Advance to the end of the state and return unused time, preserving travel time even on short paths.</summary>
    public float Advance(float seconds)
    {
        if (ExitingRoad)
        {
            float turning = Mathf.Min(seconds, Mathf.Max(0, ExitRotationDuration - exitRotationElapsed));
            exitRotationElapsed += turning;
            seconds -= turning;
            float moving = Mathf.Min(seconds, Mathf.Max(0, exitLength - exitTravelled) / 3f);
            exitTravelled = Mathf.Min(exitLength, exitTravelled + moving * 3f);
            age += turning + moving;
            return Mathf.Max(0, seconds - moving);
        }
        float velocity = InIntersection ? connectorSpeed : speed;
        if (arrivalDistance >= 0) velocity *= Mathf.Lerp(.3f, 1, Mathf.Clamp01(arrivalDistance / Mathf.Max(.1f, arrivalBlendLength)));
        velocity = Mathf.Max(0, velocity);
        if (velocity <= .00001f) { age += seconds; return 0; }
        float remaining = InIntersection ? connector.Length - connectorS : Remaining;
        if (!InIntersection && arrivalDistance >= 0) remaining = Mathf.Min(remaining, arrivalDistance);
        float used = Mathf.Min(seconds, Mathf.Max(0, remaining) / velocity);
        float distance = used * velocity;
        age += used;
        if (InIntersection) connectorS += distance;
        else
        {
            s = Mathf.Clamp(s + (forward ? distance : -distance), 0, TotalLength);
            distanceSinceJunction += distance;
            spawnBlendRemaining = Mathf.Max(0, spawnBlendRemaining - distance);
            if (arrivalDistance >= 0) arrivalDistance = Mathf.Max(0, arrivalDistance - distance);
        }
        return Mathf.Max(0, seconds - used);
    }

    /// <summary>Retain physical size on the carriageway, turn 90 degrees, and shrink only after leaving the full road width.</summary>
    public void BeginRoadsideExit(int trafficHand)
    {
        Sample(out exitStart, out exitForward);
        exitForward.y = 0;
        exitForward.Normalize();
        if (exitForward.sqrMagnitude < .001f) exitForward = Vector3.forward;
        float sign = trafficHand == Constants.TRAFFIC_HAND_LEFT ? -1 : 1;
        exitSide = Vector3.Cross(Vector3.up, exitForward).normalized * sign;
        road.Sample(s, forward, out Vector3 center, out _);
        float currentOffset = Vector3.Dot(exitStart - center, exitSide);
        // Continue 3m beyond the outer road edge, including sidewalks, so the standard car's rear also clears the carriageway.
        exitLength = Mathf.Max(3, roadHalfWidth + 3 - currentOffset);
        exitRotationElapsed = 0; exitTravelled = 0;
        arrivalPending = true;
        ExitingRoad = true;
    }

    /// <summary>Exit into a validated on-site entrance. Do not invent arbitrary-distance exits where no entrance exists.</summary>
    public void BeginParcelExit(Vector3 destination)
    {
        BeginRoadsideExit(shoulderOffset < 0 ? Constants.TRAFFIC_HAND_LEFT : Constants.TRAFFIC_HAND_RIGHT);
        Vector3 delta = destination - exitStart;
        exitLength = delta.magnitude;
        exitSide = exitLength > .001f ? delta / exitLength : exitSide;
    }

    public int AheadNodeId(int startNodeId, int endNodeId) => forward ? endNodeId : startNodeId;
}
