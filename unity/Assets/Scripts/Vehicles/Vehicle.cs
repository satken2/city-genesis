using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>車線と交差点で共用する、累積距離付きの走行点列。</summary>
public sealed class VehiclePolyline
{
    public readonly Vector3[] points;
    private readonly float[] distances;
    public float Length => distances[distances.Length - 1];

    public VehiclePolyline(IList<Vector3> source)
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
        int lo = 1, hi = distances.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (distances[mid] < s) lo = mid + 1; else hi = mid;
        }
        int segment = lo;
        while (segment < distances.Length - 1 && distances[segment] - distances[segment - 1] < .0001f) segment++;
        float length = distances[segment] - distances[segment - 1];
        position = Vector3.Lerp(points[segment - 1], points[segment], length > .0001f ? (s - distances[segment - 1]) / length : 0);
        tangent = (points[segment] - points[segment - 1]).normalized * (forward ? 1 : -1);
        if (tangent.sqrMagnitude < .001f) tangent = forward ? Vector3.forward : Vector3.back;
    }

    public float Project(Vector3 point)
    {
        float best = float.PositiveInfinity, result = 0;
        for (int i = 1; i < points.Length; i++)
        {
            Vector3 delta = points[i] - points[i - 1]; delta.y = 0;
            Vector3 offset = point - points[i - 1]; offset.y = 0;
            float t = delta.sqrMagnitude > .00001f ? Mathf.Clamp01(Vector3.Dot(offset, delta) / delta.sqrMagnitude) : 0;
            float error = (offset - delta * t).sqrMagnitude;
            if (error < best) { best = error; result = distances[i - 1] + t * (distances[i] - distances[i - 1]); }
        }
        return result;
    }

    /// <summary>線分と円の交点を解き、複数回出入りする場合も円外区間を含めない。</summary>
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

    public static VehiclePolyline Connector(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float tension)
    {
        float d = Vector3.Distance(p0, p1) * Mathf.Clamp(tension, .1f, .6f);
        var points = new Vector3[33];
        for (int i = 0; i < points.Length; i++)
        {
            float t = (float)i / (points.Length - 1), u = 1 - t;
            points[i] = u * u * u * p0 + 3 * u * u * t * (p0 + t0 * d) + 3 * u * t * t * (p1 - t1 * d) + t * t * t * p1;
        }
        return new VehiclePolyline(points);
    }
}

/// <summary>
/// 描画実体に依存しない車1台の状態。交差点内では車線オフセット済みの点列を走る。
/// 到着予約はパスを跨いで保持し、十分な道路内区間がある場所でだけ路肩へ寄せる。
/// </summary>
public class Vehicle
{
    public int pathId, vehicleType, variantId, lane;
    public bool forward;
    public float s, speed, age, distanceSinceJunction;
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
        road.Sample(s, forward, out position, out tangent);
        float blend = Mathf.Clamp01(spawnBlendRemaining / 15f);
        if (arrivalDistance >= 0) blend = Mathf.Max(blend, 1 - Mathf.Clamp01(arrivalDistance / Mathf.Max(.1f, arrivalBlendLength)));
        float offset = Mathf.Lerp(laneOffset, shoulderOffset, blend);
        position += Vector3.Cross(Vector3.up, tangent).normalized * offset;
    }

    /// <summary>状態の端まで進め、未消費時間を返す。短いパスでも移動時間を捨てない。</summary>
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
        velocity = Mathf.Max(.1f, velocity);
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

    /// <summary>車道上では実寸を保ち、90度旋回して道路全幅の外へ抜けてから縮小する。</summary>
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
        // 歩道を含む道路外縁から3m先まで進み、標準車の後端も車道から離す。
        exitLength = Mathf.Max(3, roadHalfWidth + 3 - currentOffset);
        exitRotationElapsed = 0; exitTravelled = 0;
        arrivalPending = true;
        ExitingRoad = true;
    }

    /// <summary>検証済みの敷地内入口へ退出する。入口のない場所で任意距離の退出を作らない。</summary>
    public void BeginParcelExit(Vector3 destination)
    {
        BeginRoadsideExit(shoulderOffset < 0 ? Constants.TRAFFIC_HAND_LEFT : Constants.TRAFFIC_HAND_RIGHT);
        Vector3 delta = destination - exitStart;
        exitLength = delta.magnitude;
        exitSide = exitLength > .001f ? delta / exitLength : exitSide;
    }

    public int AheadNodeId(int startNodeId, int endNodeId) => forward ? endNodeId : startNodeId;
}
