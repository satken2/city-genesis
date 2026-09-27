using System;
using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;
using UnityEngine;

/// <summary>建物の間口を道路の片側へ投影する。道路本体と交通グラフは維持し、側帯だけに開口を作る。</summary>
public sealed class RoadAccessPlan
{
    public const float MaxGap = 6f;
    public sealed class Connection
    {
        public BuildingRoadAccessIndex.Opening Opening;
        public int Side;
        public float From, To;
        public Vector3 A, B;
        public float[] Percents;
        public Vector3[] Gates;
        public Vector2[] Apron;
        public List<int> ApronTriangles;
    }
    readonly List<Connection> connections = new();
    readonly SplineComputer spline;
    readonly float width;
    public IReadOnlyList<Connection> Connections => connections;
    public readonly List<Vector2> Left = new(), Right = new(); // スプライン距離(m)

    RoadAccessPlan(SplineComputer spline, float width)
    { this.spline = spline; this.width = width; }

    public static RoadAccessPlan Create(PathSummary path, SplineComputer spline, IReadOnlyList<Vector3> points, float width)
    {
        var result = new RoadAccessPlan(spline, width);
        // 一般道路のみ対象。高架・地下との高さの整合は間口全体で判定する。
        if (path.path_type != Constants.PATH_STREET || path.status != Constants.STATUS_ACTIVE) return result;
        var openings = BuildingRoadAccessIndex.Query(points, width * .5f + MaxGap);
        return openings.Count == 0 ? result : Resolve(spline, width, openings);
    }

    /// <summary>入力を明示した同じ解決器を独立Unity検証でも使う。</summary>
    public static RoadAccessPlan Resolve(SplineComputer spline, float width, IEnumerable<BuildingRoadAccessIndex.Opening> openings)
    {
        var plan = new RoadAccessPlan(spline, width);
        int count = Mathf.Clamp(Mathf.CeilToInt(spline.CalculateLength()), 2, 32768);
        var left = new Vector3[count + 1]; var right = new Vector3[count + 1];
        for (int i = 0; i <= count; i++)
        {
            left[i] = plan.Point((double)i / count, -width * .5f);
            right[i] = plan.Point((double)i / count, width * .5f);
        }
        foreach (var opening in openings)
        {
            var apron = opening.Local.Apron == null ? null : (Vector2[])opening.Local.Apron.Clone();
            if (apron == null || apron.Length < 3) continue;
            if (PolygonUtils.SignedArea(apron) < 0) Array.Reverse(apron);
            var apronTriangles = BuildingMeshBuilder.TriangulateEarClip(apron);
            if (apronTriangles.Count == 0) continue;
            Vector3 direction = opening.Local.Outward;
            for (int side = -1; side <= 1; side += 2)
            {
                var edge = side < 0 ? left : right;
                if (!Hit(opening.A, direction, edge, out float a) || !Hit(opening.B, direction, edge, out float b)) continue;
                float from = Mathf.Min(a, b), to = Mathf.Max(a, b);
                if (from < .0001f || to > .9999f || to - from < .0001f) continue;
                Vector3 pa = a < b ? opening.A : opening.B, pb = a < b ? opening.B : opening.A;
                int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(pa, pb)));
                var percents = new float[steps + 1]; var gates = new Vector3[steps + 1];
                bool valid = true;
                // 幅の広い間口も1m刻みで検査し、両端だけ接する急曲線や敷地へ食い込む道路を除く。
                for (int i = 0; i <= steps; i++)
                {
                    Vector3 gate = Vector3.Lerp(pa, pb, (float)i / steps);
                    if (!Hit(gate, direction, edge, out float percent) || (i > 0 && percent <= percents[i - 1])) { valid = false; break; }
                    var sample = spline.Evaluate(percent);
                    Vector3 outward = Vector3.Cross(Vector3.up, sample.forward).normalized * side;
                    gate.y = plan.Point(percent, side * width * .5f).y;
                    float delta = gate.y - opening.Height(gate);
                    // 既存の施設舗装の上へすり付けるため、路面が低い大きな段差では開口しない。
                    if (Vector3.Dot(direction, outward) > -.7f || delta < -.03f || delta > .8f) { valid = false; break; }
                    percents[i] = percent; gates[i] = gate;
                }
                if (!valid) continue;
                plan.connections.Add(new Connection { Opening = opening, Side = side, From = from, To = to, A = gates[0], B = gates[steps], Percents = percents, Gates = gates,
                    Apron = apron, ApronTriangles = apronTriangles });
                (side < 0 ? plan.Left : plan.Right).Add(new Vector2(spline.CalculateLength(0, from), spline.CalculateLength(0, to)));
                break;
            }
        }
        Merge(plan.Left); Merge(plan.Right);
        return plan;
    }

    static bool Hit(Vector3 origin, Vector3 direction, Vector3[] edge, out float percent)
    {
        float nearest = float.PositiveInfinity; percent = 0;
        // 境界ぴったりの点も半直線の正方向として扱う。
        Vector2 ray = BuildingRoadAccess.XZ(direction), start = BuildingRoadAccess.XZ(origin) - ray * .05f;
        for (int i = 1; i < edge.Length; i++)
            if (PolylineUtils.IntersectRaySegment(start, ray, BuildingRoadAccess.XZ(edge[i - 1]), BuildingRoadAccess.XZ(edge[i]), out float d, out float t)
                && d < nearest && d <= MaxGap + .05f)
            { nearest = d; percent = (i - 1 + t) / (edge.Length - 1); }
        return !float.IsPositiveInfinity(nearest);
    }

    Vector3 Point(double percent, float offset)
    {
        var sample = spline.Evaluate(percent);
        Vector3 right = Vector3.Cross(Vector3.up, sample.forward).normalized;
        return sample.position + right * offset + Vector3.up * RoadPartHeights.BODY_ABOVE_SPLINE;
    }

    public static void Merge(List<Vector2> ranges)
    {
        ranges.Sort((a, b) => a.x.CompareTo(b.x));
        for (int i = 1; i < ranges.Count;)
            if (ranges[i].x <= ranges[i - 1].y + .001f)
            { ranges[i - 1] = new Vector2(ranges[i - 1].x, Mathf.Max(ranges[i - 1].y, ranges[i].y)); ranges.RemoveAt(i); }
            else i++;
    }

    public static bool Contains(IReadOnlyList<Vector2> ranges, float distance, float radius = 0)
    {
        if (ranges != null) foreach (var range in ranges) if (distance + radius >= range.x && distance - radius <= range.y) return true;
        return false;
    }

    public bool Blocks(int side, float distance, float radius) => side != 0 && Contains(side < 0 ? Left : Right, distance, radius);

    /// <summary>既存の交差点・合流の切断範囲と開口の差集合。距離からpercentへは弧長を辿って変換する。</summary>
    public List<Vector2> Remaining(int side, double from = 0, double to = 1)
    {
        var result = new List<Vector2>();
        double cursor = from;
        foreach (var range in side < 0 ? Left : Right)
        {
            double a = spline.Travel(0, range.x, Spline.Direction.Forward), b = spline.Travel(0, range.y, Spline.Direction.Forward);
            if (b <= cursor || a >= to) continue;
            if (a > cursor) result.Add(new Vector2((float)cursor, (float)Math.Min(a, to)));
            cursor = Math.Max(cursor, b);
        }
        if (cursor < to - 1e-6) result.Add(new Vector2((float)cursor, (float)to));
        return result;
    }

    public void GenerateSurface(GameObject parent, Material material)
    {
        if (connections.Count == 0 || material == null) return;
        var vertices = new List<Vector3>(); var triangles = new List<int>(); var uv = new List<Vector2>();
        void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            var normal = Vector3.Cross(b - a, c - a);
            if (normal.sqrMagnitude < 1e-10f) return;
            if (normal.y < 0) (b, c) = (c, b);
            int n = vertices.Count;
            foreach (var p in new[] { a, b, c }) { vertices.Add(parent.transform.InverseTransformPoint(p)); uv.Add(BuildingRoadAccess.XZ(p)); }
            triangles.Add(n); triangles.Add(n + 1); triangles.Add(n + 2);
        }
        foreach (var connection in connections)
        {
            if (!PathDrawContext.IntersectDetailRange(connection.From, connection.To, out _, out _)) continue;
            int count = connection.Gates.Length - 1;
            Vector3 lastEdge = default, lastGate = default;
            for (int i = 0; i <= count; i++)
            {
                float percent = connection.Percents[i];
                Vector3 edge = Point(percent, connection.Side * width * .5f);
                Vector3 gate = connection.Gates[i];
                if (i > 0)
                {
                    // 歩道の下には道路本体がある。追加舗装は道路外縁から敷地までに限る。
                    Triangle(lastEdge, edge, gate); Triangle(lastEdge, gate, lastGate);
                }
                lastEdge = edge; lastGate = gate;
            }
            var opening = connection.Opening;
            var polygon = connection.Apron;
            var indices = connection.ApronTriangles;
            Vector3 Lift(Vector2 point)
            {
                Vector3 world = opening.Center + new Vector3(point.x, 0, point.y);
                float depth = -Vector3.Dot(world - opening.A, opening.Local.Outward);
                Vector3 along = connection.B - connection.A; along.y = 0;
                float t = Mathf.Clamp01(Vector3.Dot(world - connection.A, along) / Mathf.Max(.001f, along.sqrMagnitude));
                float index = t * count;
                int segment = Mathf.Min(Mathf.FloorToInt(index), count - 1);
                float gateHeight = Mathf.Lerp(connection.Gates[segment].y, connection.Gates[segment + 1].y, index - segment);
                world.y = Mathf.Lerp(gateHeight, opening.Height(world), Mathf.Clamp01(depth / BuildingRoadAccess.ApronDepth));
                return world;
            }
            // 接続面も地形格子より細かく分割し、敷地内のすり付けを地形へ戻す。
            void Subdivide(Vector2 a, Vector2 b, Vector2 c, int depth)
            {
                float ab = (a - b).sqrMagnitude, bc = (b - c).sqrMagnitude, ca = (c - a).sqrMagnitude;
                if (depth > 16 || Mathf.Max(ab, Mathf.Max(bc, ca)) <= 1f) { Triangle(Lift(a), Lift(b), Lift(c)); return; }
                if (ab >= bc && ab >= ca) { var m = (a + b) * .5f; Subdivide(a, m, c, depth + 1); Subdivide(m, b, c, depth + 1); }
                else if (bc >= ca) { var m = (b + c) * .5f; Subdivide(a, b, m, depth + 1); Subdivide(a, m, c, depth + 1); }
                else { var m = (c + a) * .5f; Subdivide(a, b, m, depth + 1); Subdivide(m, b, c, depth + 1); }
            }
            for (int i = 0; i < indices.Count; i += 3) Subdivide(polygon[indices[i]], polygon[indices[i + 1]], polygon[indices[i + 2]], 0);
        }
        if (vertices.Count == 0) return;
        var go = new GameObject("BuildingAccessSurface"); go.transform.SetParent(parent.transform, false);
        var mesh = new Mesh { name = "BuildingAccessSurface", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.SetUVs(0, uv); mesh.RecalculateNormals(); mesh.RecalculateBounds();
        var colors = new Color32[vertices.Count];
        Array.Fill(colors, new Color32(255, 255, 255, 255));
        mesh.colors32 = colors; // 未舗装路の縁の侵食マスクでも接続面を切り抜かない。
        go.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
    }
}
