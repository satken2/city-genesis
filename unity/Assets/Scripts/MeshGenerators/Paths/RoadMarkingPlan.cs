using System;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>路面の二次元座標で保持する標示。画像の解像度や道路メッシュのLODに依存しない。</summary>
public sealed class RoadMarkingPlan
{
    public const float TileSize = 32f;
    public readonly List<Vector3> Vertices = new();
    public readonly List<int> Triangles = new();
    public Rect Bounds { get; private set; }
    public bool Empty => Vertices.Count == 0;
    private bool hasBounds;
    private Mesh mesh;
    private string signature;

    public void Triangle(Vector2 a, Vector2 b, Vector2 c)
    {
        int i = Vertices.Count;
        Vertices.Add(a); Vertices.Add(b); Vertices.Add(c);
        Triangles.Add(i); Triangles.Add(i + 1); Triangles.Add(i + 2);
        foreach (var p in new[] { a, b, c })
        {
            if (!hasBounds) { Bounds = new Rect(p, Vector2.zero); hasBounds = true; }
            else Bounds = Rect.MinMaxRect(Mathf.Min(Bounds.xMin, p.x), Mathf.Min(Bounds.yMin, p.y),
                Mathf.Max(Bounds.xMax, p.x), Mathf.Max(Bounds.yMax, p.y));
        }
        signature = null;
    }

    public void Strip(Vector2 a, Vector2 b, float width)
    {
        Vector2 d = b - a;
        if (d.sqrMagnitude < .000001f || width <= 0f) return;
        Vector2 n = new Vector2(-d.y, d.x).normalized * width * .5f;
        Triangle(a - n, a + n, b + n); Triangle(a - n, b + n, b - n);
    }

    public void Polyline(IReadOnlyList<Vector2> points, float width, bool dashed = false, float phase = 0f)
    {
        float distance = phase;
        for (int i = 1; i < points.Count; i++)
        {
            Vector2 a = points[i - 1], b = points[i];
            float length = Vector2.Distance(a, b), s = 0f;
            if (!dashed) Strip(a, b, width);
            else while (s < length - .0001f)
            {
                float cycle = Mathf.Repeat(distance + s, 4f);
                float step = Mathf.Min(length - s, (cycle < 2f ? 2f : 4f) - cycle);
                if (cycle < 2f) Strip(Vector2.Lerp(a, b, s / length), Vector2.Lerp(a, b, (s + step) / length), width);
                s += Mathf.Max(.0001f, step);
            }
            distance += length;
        }
    }

    /// <summary>進行方向の矢印。形状はコードで生成し、外部の画像素材を必要としない。</summary>
    public void Arrow(Vector2 center, Vector2 forward, RoadMarkingTurns turns, float scale = 1f)
    {
        if (turns == RoadMarkingTurns.None) return;
        Vector2 right = new Vector2(-forward.y, forward.x);
        Vector2 tail = center - forward * 1.6f * scale;
        Vector2 elbow = center - forward * .2f * scale;
        bool straight = (turns & RoadMarkingTurns.Straight) != 0;
        Strip(tail, straight ? center + forward * .9f * scale : elbow, .18f * scale);
        if (straight) Head(center + forward * 1.65f * scale, forward, scale);
        foreach (int side in new[] { -1, 1 })
        {
            var turn = side < 0 ? RoadMarkingTurns.Left : RoadMarkingTurns.Right;
            if ((turns & turn) == 0) continue;
            Vector2 direction = right * side;
            Vector2 tip = elbow + direction * 1.05f * scale + forward * .55f * scale;
            Vector2 headBase = Head(tip, direction, scale * .8f);
            // 枝は先端三角形の底辺中央へ接続し、最後の短い区間を転回方向へ向ける。
            Vector2 bend = headBase - direction * .1f * scale;
            Strip(elbow, bend, .18f * scale);
            Strip(bend, headBase + direction * .09f * scale, .18f * scale);
        }
    }

    private Vector2 Head(Vector2 tip, Vector2 direction, float scale)
    {
        Vector2 side = new Vector2(-direction.y, direction.x) * .42f * scale;
        Vector2 back = tip - direction * .85f * scale;
        Triangle(tip, back - side, back + side);
        return back;
    }

    public Mesh GetMesh()
    {
        if (mesh != null) return mesh;
        mesh = new Mesh { name = "Road marking bake geometry", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(Vertices); mesh.SetTriangles(Triangles, 0); mesh.RecalculateBounds();
        return mesh;
    }

    public void ReleaseMesh() { if (mesh != null) UnityEngine.Object.Destroy(mesh); mesh = null; }

    public string Signature
    {
        get
        {
            if (signature != null) return signature;
            // 内容署名でLOD再生成時も画像を再利用する。都市やオブジェクトIDは画素内容に含めない。
            var bytes = new byte[Vertices.Count * 8];
            for (int i = 0; i < Vertices.Count; i++)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(Vertices[i].x), 0, bytes, i * 8, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(Vertices[i].y), 0, bytes, i * 8 + 4, 4);
            }
            using var sha = System.Security.Cryptography.SHA256.Create();
            signature = Convert.ToBase64String(sha.ComputeHash(bytes));
            return signature;
        }
    }

    public bool IntersectsTile(Rect tile)
    {
        if (Empty || !Bounds.Overlaps(tile, true)) return false;
        for (int i = 0; i < Vertices.Count; i += 3)
        {
            var a = Vertices[i]; var b = Vertices[i + 1]; var c = Vertices[i + 2];
            var box = Rect.MinMaxRect(Mathf.Min(a.x, b.x, c.x), Mathf.Min(a.y, b.y, c.y),
                Mathf.Max(a.x, b.x, c.x), Mathf.Max(a.y, b.y, c.y));
            if (box.Overlaps(tile, true)) return true;
        }
        return false;
    }
}

[Flags]
public enum RoadMarkingTurns { None = 0, Left = 1, Straight = 2, Right = 4 }

/// <summary>道路種別と接続方向から求める標示規則。左右通行と保存方向を区別する。</summary>
public static class RoadMarkingRules
{
    public static Func<int, List<PathSummary>> ConnectedPaths;
    public static Func<PathSummary, bool, Vector3, bool> IsApproachEndpoint;
    public static bool IsRoad(PathSummary p) => p != null && (p.path_type == Constants.PATH_STREET || p.path_type == Constants.PATH_MOTORWAY);
    public static bool HasPaint(PathSummary p) => IsRoad(p) && (MasterDataStore.Instance?.GetPathMaster(p.path_variant)?.side_width ?? 0f) > 0;

    public static Vector2 Outward(PathSummary path, int node)
    {
        var points = IntersectionPolygonUtil.SamplePlan(path);
        if (points == null || points.Count < 2) return Vector2.up;
        return node == path.start_node_id ? (IntersectionPolygonUtil.XZ(points[1] - points[0])).normalized
            : (IntersectionPolygonUtil.XZ(points[points.Count - 2] - points[points.Count - 1])).normalized;
    }

    public static RoadMarkingTurns Turn(Vector2 incoming, Vector2 outgoing)
    {
        float angle = Vector2.SignedAngle(incoming, outgoing);
        if (Mathf.Abs(angle) > 150f) return RoadMarkingTurns.None;
        if (Mathf.Abs(angle) < 35f) return RoadMarkingTurns.Straight;
        return angle > 0 ? RoadMarkingTurns.Left : RoadMarkingTurns.Right;
    }

    public static RoadMarkingTurns Available(PathSummary path, int node)
    {
        if (ConnectedPaths == null || !IsRoad(path)) return RoadMarkingTurns.None;
        var roads = ConnectedPaths(node);
        int count = 0;
        foreach (var p in roads) if (IsRoad(p) && p.status == Constants.STATUS_ACTIVE) count++;
        if (count < 3 || path.path_type != Constants.PATH_STREET) return RoadMarkingTurns.None;
        Vector2 incoming = -Outward(path, node);
        RoadMarkingTurns result = RoadMarkingTurns.None;
        foreach (var p in roads)
            if (p.path_id != path.path_id && IsRoad(p) && p.status == Constants.STATUS_ACTIVE
                && VehicleManager.DirectionAllowsEntry(p.travel_direction, p.start_node_id == node))
                result |= Turn(incoming, Outward(p, node));
        return result;
    }

    public static RoadMarkingTurns ForLane(RoadMarkingTurns available, int lane, int count, int trafficHand)
    {
        if (count < 2) return available;
        var outer = trafficHand == Constants.TRAFFIC_HAND_LEFT ? RoadMarkingTurns.Left : RoadMarkingTurns.Right;
        var inner = trafficHand == Constants.TRAFFIC_HAND_LEFT ? RoadMarkingTurns.Right : RoadMarkingTurns.Left;
        RoadMarkingTurns result = RoadMarkingTurns.None;
        if (lane == 0) result |= available & outer;
        if (lane == count - 1) result |= available & inner;
        // 2車線では外側を直進兼用にする。3車線以上は中間の車線を直進に使う。
        if ((available & RoadMarkingTurns.Straight) != 0 && (count == 2 ? lane == 0 || (available & inner) == 0 : lane > 0 && lane < count - 1))
            result |= RoadMarkingTurns.Straight;
        return result == RoadMarkingTurns.None ? available : result;
    }

    public static int LaneForTurn(RoadMarkingTurns available, RoadMarkingTurns turn, int count, int trafficHand)
    {
        for (int lane = 0; lane < count; lane++)
            if ((ForLane(available, lane, count, trafficHand) & turn) != 0) return lane;
        return 0;
    }

    public static string JunctionSignature(int node, List<PathSummary> paths)
    {
        var ordered = new List<PathSummary>(paths);
        ordered.Sort((a, b) => a.path_id.CompareTo(b.path_id));
        var signature = new System.Text.StringBuilder().Append(CityInfoStore.TrafficHand);
        foreach (var path in ordered)
        {
            var direction = Outward(path, node);
            signature.Append('|').Append(path.path_id).Append(':').Append(path.path_variant).Append(':')
                .Append(path.path_type).Append(':').Append(path.status).Append(':').Append(path.travel_direction)
                .Append(':').Append(Mathf.RoundToInt(direction.x * 10000)).Append(':').Append(Mathf.RoundToInt(direction.y * 10000));
        }
        return signature.ToString();
    }

    public static RoadMarkingPlan Intersection(List<SidewalkConnectorSpec> connectors)
    {
        var plan = new RoadMarkingPlan();
        if (connectors != null) foreach (var spec in connectors)
        {
            if (spec.Part != "line") continue;
            var points = new List<Vector2>();
            foreach (var p in spec.Points) points.Add(new Vector2(p.x, p.z));
            plan.Polyline(points, spec.Widths.Count > 0 ? spec.Widths[0] : .15f, spec.Dashed);
        }
        return plan;
    }
}
