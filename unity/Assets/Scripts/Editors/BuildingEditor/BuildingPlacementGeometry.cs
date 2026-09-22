using System;
using UnityEngine;

/// <summary>道路沿い矩形の基準点と干渉判定。Yに依存しない純粋な幾何計算。</summary>
public static class BuildingPlacementGeometry
{
    /// <summary>線分へのXZ最近接点をY=0に落として返す。以降の矩形計算がYに依存しないための正規化。</summary>
    public static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 result = PolylineUtils.ClosestPointOnSegmentXZ(p, a, b);
        result.y = 0;
        return result;
    }

    public static Vector3 CenterFromFront(Vector3 front, Vector3 outward, float depth)
        => front + outward * (depth * 0.5f);

    public static float AngleFromOutward(Vector3 outward)
        => (float)(Math.Atan2(-outward.x, outward.z) * 180 / Math.PI);

    public static void Axes(float angle, out Vector3 widthAxis, out Vector3 depthAxis)
    {
        double rad = angle * Math.PI / 180;
        widthAxis = new Vector3((float)Math.Cos(rad), 0, (float)Math.Sin(rad));
        depthAxis = new Vector3(-widthAxis.z, 0, widthAxis.x);
    }

    /// <summary>
    /// 道路中心線のカプセルと矩形の内部が重なるか。四隅だけでなく辺を横切る道路も検出する。
    /// 接するだけの配置を許容し、曲線内側・交差点側へはみ出す吸着候補を除外する。
    /// </summary>
    public static bool OverlapsRoad(Vector3 center, float width, float depth, float angle,
        Vector3 a, Vector3 b, float roadHalfWidth)
    {
        Axes(angle, out var u, out var v);
        Vector3 da = a - center, db = b - center;
        var p = new Vector3(Vector3.Dot(da, u), 0, Vector3.Dot(da, v));
        var q = new Vector3(Vector3.Dot(db, u), 0, Vector3.Dot(db, v));
        float w = width * 0.5f, d = depth * 0.5f;
        if ((Math.Abs(p.x) < w && Math.Abs(p.z) < d) || (Math.Abs(q.x) < w && Math.Abs(q.z) < d)) return true;
        float distance = float.PositiveInfinity;
        Vector3 c0 = new Vector3(-w, 0, -d), c1 = new Vector3(w, 0, -d);
        Vector3 c2 = new Vector3(w, 0, d), c3 = new Vector3(-w, 0, d);
        distance = Math.Min(distance, SegmentDistance(p, q, c0, c1));
        distance = Math.Min(distance, SegmentDistance(p, q, c1, c2));
        distance = Math.Min(distance, SegmentDistance(p, q, c2, c3));
        distance = Math.Min(distance, SegmentDistance(p, q, c3, c0));
        return distance < Math.Max(0, roadHalfWidth - 0.01f);
    }

    private static float Cross(Vector3 a, Vector3 b) => a.x * b.z - a.z * b.x;

    private static float SegmentDistance(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        Vector3 ab = b - a, cd = d - c;
        float cross = Cross(ab, cd);
        if (Math.Abs(cross) > 0.000001f)
        {
            float t = Cross(c - a, cd) / cross, s = Cross(c - a, ab) / cross;
            if (t >= 0 && t <= 1 && s >= 0 && s <= 1) return 0;
        }
        return Math.Min(Math.Min((a - Closest(a, c, d)).magnitude, (b - Closest(b, c, d)).magnitude),
            Math.Min((c - Closest(c, a, b)).magnitude, (d - Closest(d, a, b)).magnitude));
    }
}
