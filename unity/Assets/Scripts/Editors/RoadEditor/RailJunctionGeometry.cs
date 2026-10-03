using System.Collections.Generic;
using UnityEngine;

/// <summary>線路の吸着位置と接線を元のベジエから求める。可否の権威は /paths/validate。</summary>
public static class RailJunctionGeometry
{
    public static void ProjectCubic(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 target,
        out Vector3 point, out Vector3 tangent)
    {
        // 大きなワールド座標でも接続誤差を抑えるため、始点を原点として射影する。
        Vector3 origin = p0;
        p1 -= origin;
        p2 -= origin;
        p3 -= origin;
        target -= origin;
        p0 = Vector3.zero;
        float best = float.MaxValue, bestT = 0;
        for (int i = 0; i <= 64; i++)
        {
            float t = i / 64f;
            float distance = Utils.XZDistanceSquared(PolylineUtils.CubicBezierPoint(p0, p1, p2, p3, t), target);
            if (distance < best) { best = distance; bestT = t; }
        }
        float lo = Mathf.Max(0, bestT - 1 / 64f), hi = Mathf.Min(1, bestT + 1 / 64f);
        for (int i = 0; i < 32; i++)
        {
            float a = lo + (hi - lo) / 3, b = hi - (hi - lo) / 3;
            float da = Utils.XZDistanceSquared(PolylineUtils.CubicBezierPoint(p0, p1, p2, p3, a), target);
            float db = Utils.XZDistanceSquared(PolylineUtils.CubicBezierPoint(p0, p1, p2, p3, b), target);
            if (da < db) hi = b; else lo = a;
        }
        float resultT = (lo + hi) * 0.5f;
        Vector3 result = PolylineUtils.CubicBezierPoint(p0, p1, p2, p3, resultT);
        // 真の端点が最良なら、その座標を保持する(端点同士の延長を内点へずらさない)。
        if (best < Utils.XZDistanceSquared(result, target)) resultT = bestT;
        float u = 1 - resultT;
        point = origin + PolylineUtils.CubicBezierPoint(p0, p1, p2, p3, resultT);
        tangent = 3 * (u * u * (p1 - p0) + 2 * u * resultT * (p2 - p1) + resultT * resultT * (p3 - p2));
        tangent.y = 0;
        tangent.Normalize();
    }

    public static bool TryProject(GeneratedModels.PathSummary path, Vector3 target,
        out Vector3 point, out Vector3 tangent)
    {
        point = target;
        tangent = Vector3.zero;
        var start = path.GetStartPointList();
        var end = path.GetEndPointList();
        if (start == null || end == null || start.Count < 3 || end.Count < 3) return false;
        var anchors = new List<Vector3> { Vector(start) };
        var incoming = new List<Vector3?> { null };
        var outgoing = new List<Vector3?> { Control(path.start_control) };
        foreach (var waypoint in path.GetWaypointList() ?? new List<PathWaypoint>())
        {
            anchors.Add(Vector(waypoint.position));
            incoming.Add(waypoint.control_in == null ? null : Vector(waypoint.control_in));
            outgoing.Add(waypoint.control_out == null ? null : Vector(waypoint.control_out));
        }
        anchors.Add(Vector(end));
        incoming.Add(Control(path.end_control));
        float best = float.MaxValue;
        for (int i = 0; i < anchors.Count - 1; i++)
        {
            Vector3 a = anchors[i], b = anchors[i + 1];
            Vector3 p1 = path.curved && outgoing[i].HasValue ? outgoing[i].Value : Vector3.Lerp(a, b, 1 / 3f);
            Vector3 p2 = path.curved && incoming[i + 1].HasValue ? incoming[i + 1].Value : Vector3.Lerp(a, b, 2 / 3f);
            ProjectCubic(a, p1, p2, b, target, out Vector3 projected, out Vector3 direction);
            float distance = Utils.XZDistanceSquared(projected, target);
            if (distance < best) { best = distance; point = projected; tangent = direction; }
        }
        return best < float.MaxValue && tangent.sqrMagnitude > 0;
    }

    private static Vector3 Vector(IList<float> p) => new Vector3(p[0], 0, p[2]);
    private static Vector3? Control(List<object> p)
    {
        var values = GeneratedModelUtils.ToFloatList(p);
        return values != null && values.Count >= 3 ? Vector(values) : (Vector3?)null;
    }
}
