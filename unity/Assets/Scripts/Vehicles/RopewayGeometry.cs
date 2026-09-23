using System;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>ケーブル、搬器の吊り点、終端滑車が共有する実寸の循環経路。</summary>
public sealed class RopewayGeometry
{
    public const float CableOffset = 2.1f;
    public const float CableRadius = .04f;
    public const float CableHeight = RoadPartHeights.BODY_ABOVE_SPLINE - CableRadius;
    public const float TerminalInset = 4f;
    public const float CabinSpacing = 40f;
    public const float Speed = 4f;

    public sealed class Leg
    {
        public int PathId;
        public VehiclePolyline Line;
        public float Start;
    }
    public sealed class Terminal
    {
        public int PathId;
        public Vector3 Center;
        public Quaternion Rotation;
    }

    public readonly List<Leg> Legs = new();
    public readonly List<Terminal> Terminals = new();
    public float Length { get; private set; }
    public float ForwardLength { get; private set; }
    public bool Closed { get; private set; }
    public int CabinCount => Closed ? 2 * Mathf.Max(1, Mathf.FloorToInt(Mathf.Min(ForwardLength, Length - ForwardLength) / CabinSpacing))
        : Mathf.Max(2, Mathf.FloorToInt(Length / CabinSpacing));
    public readonly Dictionary<int, List<Vector3>> SourcePoints = new();

    public static RopewayGeometry Build(RopewayNetwork.Route route, Func<PathSummary, List<Vector3>> resolve)
    {
        var points = new List<Vector3>();
        var owners = new List<int>();
        var result = new RopewayGeometry { Closed = route.Closed };
        foreach (var link in route.Links)
        {
            var source = resolve(link.Path);
            if (source == null || source.Count < 2) return null;
            result.SourcePoints[link.Path.path_id] = source;
            var part = new List<Vector3>(source);
            if (!link.Forward) part.Reverse();
            if (points.Count > 0 && Vector3.Distance(points[points.Count - 1], part[0]) > .2f) return null;
            if (points.Count == 0) points.Add(part[0]);
            for (int i = 1; i < part.Count; i++)
            {
                if (Vector3.Distance(points[points.Count - 1], part[i]) < .001f) continue;
                points.Add(part[i]); owners.Add(link.Path.path_id);
            }
        }
        if (points.Count < 2) return null;
        if (!route.Closed)
        {
            float head = StationLength(route, result.SourcePoints, false);
            float tail = StationLength(route, result.SourcePoints, true);
            // 短い駅でも滑車径を縮めない。中心を駅内へ置き、折り返し部分の屋根端からの張り出しを許す。
            Trim(points, owners, Mathf.Min(TerminalInset, head * .5f));
            points.Reverse(); owners.Reverse(); Trim(points, owners, Mathf.Min(TerminalInset, tail * .5f));
            points.Reverse(); owners.Reverse();
        }
        else
        {
            if (Vector3.Distance(points[0], points[points.Count - 1]) > .2f) return null;
            points[points.Count - 1] = points[0];
        }
        if (points.Count < 2) return null;

        var right = Offset(points, CableOffset, route.Closed);
        var left = Offset(points, -CableOffset, route.Closed);
        result.AddLegs(right, owners);
        result.ForwardLength = result.Length;
        if (!route.Closed)
            result.AddTurn(points[points.Count - 1], points[points.Count - 1] - points[points.Count - 2], owners[owners.Count - 1]);
        left.Reverse(); owners.Reverse(); result.AddLegs(left, owners);
        if (!route.Closed) result.AddTurn(points[0], points[0] - points[1], owners[owners.Count - 1]);
        return result;
    }

    static float StationLength(RopewayNetwork.Route route, Dictionary<int, List<Vector3>> points, bool reverse)
    {
        float length = 0;
        for (int j = 0; j < route.Links.Count; j++)
        {
            var link = route.Links[reverse ? route.Links.Count - j - 1 : j];
            if (!RopewayNetwork.Station(link.Path)) break;
            length += new VehiclePolyline(points[link.Path.path_id]).Length;
        }
        return length;
    }

    static void Trim(List<Vector3> points, List<int> owners, float distance)
    {
        while (points.Count > 1)
        {
            float length = Vector3.Distance(points[0], points[1]);
            if (distance < length) { points[0] = Vector3.Lerp(points[0], points[1], distance / length); return; }
            distance -= length; points.RemoveAt(0); owners.RemoveAt(0);
        }
    }

    static List<Vector3> Offset(List<Vector3> points, float offset, bool closed)
    {
        var result = new List<Vector3>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            int previous = i > 0 ? i - 1 : closed ? points.Count - 2 : 0;
            int next = i < points.Count - 1 ? i + 1 : closed ? 1 : i;
            Vector3 a = points[i] - points[previous], b = points[next] - points[i];
            a.y = b.y = 0;
            if (a.sqrMagnitude < .0001f) a = b;
            if (b.sqrMagnitude < .0001f) b = a;
            a.Normalize(); b.Normalize();
            Vector3 n = Vector3.Cross(Vector3.up, a + b).normalized;
            if (n.sqrMagnitude < .001f) n = Vector3.Cross(Vector3.up, b);
            float scale = Mathf.Min(3, 1 / Mathf.Max(.333f, Vector3.Dot(n, Vector3.Cross(Vector3.up, b))));
            result.Add(points[i] + n * (offset * scale) + Vector3.up * CableHeight);
        }
        return result;
    }

    void AddLegs(List<Vector3> points, List<int> owners)
    {
        int start = 0;
        for (int i = 0; i < owners.Count; i++)
        {
            if (i + 1 < owners.Count && owners[i] == owners[i + 1]) continue;
            AddLeg(owners[i], points.GetRange(start, i - start + 2)); start = i + 1;
        }
    }

    void AddLeg(int owner, List<Vector3> points)
    {
        var line = new VehiclePolyline(points);
        Legs.Add(new Leg { PathId = owner, Line = line, Start = Length });
        Length += line.Length;
    }

    void AddTurn(Vector3 end, Vector3 direction, int owner)
    {
        direction.y = 0; direction.Normalize();
        var side = Vector3.Cross(Vector3.up, direction);
        Vector3 center = end + Vector3.up * CableHeight;
        var arc = new List<Vector3>();
        for (int i = 0; i <= 48; i++)
        {
            float angle = Mathf.PI * i / 48;
            arc.Add(center + CableOffset * (side * Mathf.Cos(angle) + direction * Mathf.Sin(angle)));
        }
        AddLeg(owner, arc);
        Terminals.Add(new Terminal { PathId = owner, Center = center, Rotation = Quaternion.LookRotation(direction, Vector3.up) });
    }

    public float Distance(double seconds, int cabin, double phase = 0)
    {
        if (!Closed) return (float)Repeat(seconds * Speed + phase + (double)Length * cabin / CabinCount, Length);
        // 閉路は左右それぞれ独立した循環。一周後に反対側へ横移動させない。
        float start = cabin % 2 == 0 ? 0 : ForwardLength;
        float length = cabin % 2 == 0 ? ForwardLength : Length - ForwardLength;
        return start + (float)Repeat(seconds * Speed + phase + (double)length * (cabin / 2) / (CabinCount / 2), length);
    }

    static double Repeat(double value, double length) => (value % length + length) % length;

    public int Sample(float distance, out Vector3 position, out Vector3 tangent)
    {
        distance = (float)Repeat(distance, Length);
        int lo = 0, hi = Legs.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Legs[mid].Start <= distance) lo = mid; else hi = mid - 1;
        }
        var leg = Legs[lo];
        leg.Line.Sample(distance - leg.Start, true, out position, out tangent);
        return leg.PathId;
    }

    public float Project(Vector3 position)
    {
        float best = float.PositiveInfinity, distance = 0;
        foreach (var leg in Legs)
        {
            float local = leg.Line.Project(position);
            leg.Line.Sample(local, true, out var point, out _);
            float error = (point - position).sqrMagnitude;
            if (error < best) { best = error; distance = leg.Start + local; }
        }
        return distance;
    }
}
