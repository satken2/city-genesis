using System;
using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// Towers use actual width; main cables follow span-dependent parabolas.
/// Towers stand at every anchor between two suspension spans (waypoints, and nodes joined to another suspension bridge),
/// and at the free ends of a chain at SIDE_SPAN_RATIO of the end span. A free end has an anchorage that receives the cable.
/// A joint tower is shared: only the bridge with the smallest path ID at the node draws it, and every bridge there sizes it
/// from the longest adjacent main span so their cables meet the same tower top.
/// Towers, cables and anchorages are drawn once per path along the whole deck polyline (Generate), and Far LOD keeps
/// the towers (GenerateFar); the truss is a continuous part drawn per road spline (GenerateTruss).
/// </summary>
public static class SuspensionBridgeGenerator
{
    private const float SIDE_SPAN_RATIO = 0.2f;        // End-to-tower distance as a share of the end span.
    private const float TOWER_HEIGHT_PER_SPAN = 0.08f; // Tower height above the deck per metre of the adjacent main span (12m two-lane reference).
    private const float MIN_TOWER_HEIGHT = 3.2f;
    private const float ANCHORAGE_SHARE = 0.25f;       // Anchorage length as a share of the side span (deck end to tower).
    private const float DECK_CABLE_RISE = 0.8f;        // Cable height above the deck where it lands at an end without an anchorage model.

    /// <summary>Other suspension bridges joined at the path's start (true) or end node. Provided by PathManager.</summary>
    public static Func<PathSummary, bool, List<PathSummary>> JointProvider;

    /// <summary>Whether the path is drawn as a suspension bridge (its variant has a tower model).</summary>
    public static bool Handles(PathSummary path)
        => path != null && ModelManager.Instance != null
            && ModelManager.Instance.GetModel($"Models/Paths/p{path.path_variant}/tower", true) != null;

    public static void GenerateTruss(GameObject parent, SplineComputer spline, PathSummary path, float width)
    {
        string truss = $"Models/Paths/p{path.path_variant}/truss";
        if (PathDrawContext.ResolvePrefab(truss, null, out _) != null)
            RoadPath.CreateSplineMesh(parent, spline, truss, width, 0, RoadPartHeights.BODY_ABOVE_SPLINE,
                stretchToWidth: false, maxTileLength: 0, normalizeBodySurface: false);
    }

    /// <summary>
    /// Far LOD: towers only. The tiled far model cannot hold towers at span-dependent positions and heights,
    /// while cables and anchorages are too thin or low to read at that distance. Does nothing for paths without a tower model.
    /// </summary>
    public static void GenerateFar(GameObject parent, List<Vector3> points, PathSummary path)
    {
        if (!Handles(path)) return;
        RoadPath.GetPathMasterParams(path, out float width, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
        Generate(parent, points, path, width, towersOnly: true);
    }

    /// <summary>points: the drawn deck centerline (trimmed ends and the remaining waypoints).</summary>
    public static void Generate(GameObject parent, List<Vector3> points, PathSummary path, float width, bool towersOnly = false)
    {
        var prefab = PathDrawContext.ResolvePrefab($"Models/Paths/p{path.path_variant}/tower", null, out _);
        if (prefab == null || points == null || points.Count < 2) return;
        var deck = new Deck(points);
        if (deck.Length < 8f) return;
        var filter = prefab.GetComponentInChildren<MeshFilter>();
        var renderer = prefab.GetComponentInChildren<MeshRenderer>();
        if (filter == null || renderer == null) return;

        var startJoints = Joints(path, true);
        var endJoints = Joints(path, false);
        var anchors = PlanAnchors(path);
        float[] stations = TowerStations(anchors, startJoints.Count > 0, endJoints.Count > 0);
        if (stations == null) return;
        int last = stations.Length - 1;
        float[] heights = TowerHeights(stations,
            startJoints.Count > 0 ? NeighborSpan(path, true, startJoints) : 0f,
            endJoints.Count > 0 ? NeighborSpan(path, false, endJoints) : 0f);

        // Use a 12m two-lane bridge as reference; preserve tower proportions/roadside margins on wider bridges.
        float widthScale = Mathf.Max(1f, width / 12f);
        for (int k = 0; k <= last; k++) heights[k] *= widthScale;
        // Stations are measured on the anchor plan; locate them on the (possibly trimmed) deck by plan position,
        // so waypoint towers stand at the corners.
        var distances = new float[stations.Length];
        for (int k = 0; k <= last; k++)
            distances[k] = k == 0 && startJoints.Count > 0 ? 0f
                : k == last && endJoints.Count > 0 ? deck.Length
                : deck.Project(PlanPoint(anchors, stations[k]));

        float lateral = width*.5f + .8f * widthScale;
        var metal = System.Array.Find(renderer.sharedMaterials, m => m != null && m.name.StartsWith("PATH_METAL"));
        var batch = new PathStructureMesh(parent, "SuspensionCables", new[] { metal ?? renderer.sharedMaterial });
        Vector3 DeckPoint(float d, Vector3 right, float x) => deck.Point(d) + right*x + Vector3.up*RoadPartHeights.BODY_ABOVE_SPLINE;
        for (int k = 0; k <= last; k++)
        {
            if (k == 0 && startJoints.Count > 0 && !OwnsJoint(path, startJoints)) continue;
            if (k == last && endJoints.Count > 0 && !OwnsJoint(path, endJoints)) continue;
            Vector3 right = deck.Right(distances[k]);
            var tower = UnityEngine.Object.Instantiate(prefab,parent.transform);
            tower.name = "SuspensionTower";
            tower.transform.position = DeckPoint(distances[k], right, 0);
            tower.transform.rotation = Quaternion.LookRotation(Vector3.Cross(right, Vector3.up));
            tower.transform.localScale = new Vector3(1,heights[k]/12f,1);
            foreach (var r in tower.GetComponentsInChildren<MeshRenderer>())
            {
                var mats = r.sharedMaterials;
                for (int i=0;i<mats.Length;i++) mats[i]=RoadPath.GetOrCreateRoadMaterial(mats[i]);
                r.sharedMaterials=mats;
            }
            for (int side=-1;side<=1;side+=2)
            {
                Vector3 top=DeckPoint(distances[k], right, side*lateral);
                Vector3 ground=new Vector3(top.x,Utils.GetGroundHeight(top),top.z);
                if (top.y > ground.y) batch.Bar(ground,top,.85f * widthScale);
            }
        }
        if (towersOnly)
        {
            batch.Finish();
            return;
        }

        // Cable spans: [from, to] in deck distance, with rise above the deck as a function of the span parameter u.
        var spans = new List<(float from, float to, Func<float, float> rise)>();
        if (startJoints.Count == 0)
        {
            var (inset, entry) = Anchorage(parent, deck, path, true, distances[0]);
            float h = heights[0];
            spans.Add((inset, distances[0], u => Mathf.Lerp(entry, h, u) - h*.12f*4*u*(1-u)));
        }
        for (int k = 0; k < last; k++)
        {
            float a = heights[k], b = heights[k + 1], sag = .75f * Mathf.Min(a, b);
            spans.Add((distances[k], distances[k + 1], u => Mathf.Lerp(a, b, u) - sag*4*u*(1-u)));
        }
        if (endJoints.Count == 0)
        {
            var (inset, entry) = Anchorage(parent, deck, path, false, deck.Length - distances[last]);
            float h = heights[last];
            spans.Add((distances[last], deck.Length - inset, u => Mathf.Lerp(h, entry, u) - h*.12f*4*u*(1-u)));
        }
        for (int side=-1;side<=1;side+=2)
        {
            foreach (var span in spans)
            {
                // A cable is straight in plan between its two saddles, which face the bisector at corners.
                Vector3 rightFrom = deck.Right(span.from), rightTo = deck.Right(span.to);
                int count=Mathf.Clamp(Mathf.CeilToInt((span.to-span.from)/3f),4,256);
                Vector3 previous=default;
                for(int i=0;i<=count;i++)
                {
                    float u=(float)i/count;
                    float rise=span.rise(u);
                    Vector3 deckPoint=DeckPoint(Mathf.Lerp(span.from,span.to,u), Vector3.Lerp(rightFrom,rightTo,u), side*lateral);
                    Vector3 cable=deckPoint+Vector3.up*rise;
                    if(i>0) batch.Bar(previous,cable,.25f, sides: 12);
                    if(i>0 && i<count && rise>1f) batch.Bar(deckPoint,cable,.035f);
                    previous=cable;
                }
            }
        }
        batch.Finish();
    }

    /// <summary>
    /// Anchorage at a free end (model `p{variant}/anchorage`), scaled so its length is ANCHORAGE_SHARE of the side span.
    /// Model: origin on the deck centerline at the road surface of the end, +Z toward the bridge, X at actual size.
    /// Y and Z above the origin take the same scale; vertices below the origin stretch to the ground like piers.
    /// The cable enters at the bridge-side top (max Z, max Y) at the tower's cable offset.
    /// Returns that entry as (distance inward from the end, height above the deck).
    /// </summary>
    private static (float inset, float rise) Anchorage(GameObject parent, Deck deck, PathSummary path, bool start, float sideSpan)
    {
        var prefab = PathDrawContext.ResolvePrefab($"Models/Paths/p{path.path_variant}/anchorage", null, out _);
        var filter = prefab != null ? prefab.GetComponentInChildren<MeshFilter>() : null;
        var renderer = prefab != null ? prefab.GetComponentInChildren<MeshRenderer>() : null;
        Mesh source = filter != null ? filter.sharedMesh : null;
        if (source == null || renderer == null || sideSpan < .01f) return (0f, DECK_CABLE_RISE);
        var bounds = source.bounds;
        if (bounds.max.z < .01f || bounds.min.y >= 0f) return (0f, DECK_CABLE_RISE);
        float scale = ANCHORAGE_SHARE * sideSpan / bounds.max.z;
        var vertices = source.vertices;
        var world = new Vector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 v = vertices[i];
            float d = start ? v.z * scale : deck.Length - v.z * scale;
            Vector3 top = deck.Point(d) + deck.Right(d) * (start ? v.x : -v.x) + Vector3.up * RoadPartHeights.BODY_ABOVE_SPLINE;
            if (v.y >= 0f)
            {
                world[i] = top + Vector3.up * (v.y * scale);
                continue;
            }
            // Bury 15cm so the bottom does not float on terrain error (as platform foundations do).
            float ground = Mathf.Min(top.y, Utils.GetGroundHeight(top) - .15f);
            world[i] = new Vector3(top.x, Mathf.Lerp(top.y, ground, v.y / bounds.min.y), top.z);
        }
        var batch = new PathStructureMesh(parent, "SuspensionAnchorage", renderer.sharedMaterials);
        batch.Append(source, world);
        batch.Finish();
        return (bounds.max.z * scale, bounds.max.y * scale);
    }

    /// <summary>Drawn deck centerline: straight spans between its points, measured by 3D length.</summary>
    private sealed class Deck
    {
        private readonly List<Vector3> points = new List<Vector3>();
        private readonly List<float> cumulative = new List<float>();

        public Deck(List<Vector3> source)
        {
            foreach (var point in source)
            {
                if (points.Count > 0 && Vector3.Distance(points[points.Count - 1], point) < .01f) continue;
                cumulative.Add(points.Count == 0 ? 0f : cumulative[cumulative.Count - 1] + Vector3.Distance(points[points.Count - 1], point));
                points.Add(point);
            }
        }

        public float Length => cumulative[cumulative.Count - 1];

        private int Span(float d)
        {
            for (int i = 0; i < points.Count - 2; i++)
                if (d <= cumulative[i + 1]) return i;
            return Mathf.Max(0, points.Count - 2);
        }

        public Vector3 Point(float d)
        {
            if (points.Count < 2) return points[0];
            int i = Span(d);
            return Vector3.Lerp(points[i], points[i + 1], (d - cumulative[i]) / (cumulative[i + 1] - cumulative[i]));
        }

        private Vector3 SpanRight(int i)
            => Vector3.Cross(Vector3.up, Vector3.ProjectOnPlane(points[i + 1] - points[i], Vector3.up)).normalized;

        /// <summary>Horizontal right of the deck. At a corner, the bisector shared by both spans (towers there stand square to it).</summary>
        public Vector3 Right(float d)
        {
            if (points.Count < 2) return Vector3.right;
            for (int i = 1; i < points.Count - 1; i++)
                if (Mathf.Abs(d - cumulative[i]) < .01f) return (SpanRight(i - 1) + SpanRight(i)).normalized;
            return SpanRight(Span(d));
        }

        /// <summary>Deck distance of the point nearest to a plan (XZ) position.</summary>
        public float Project(Vector2 plan)
        {
            float best = float.MaxValue, result = 0f;
            for (int i = 0; i < points.Count - 1; i++)
            {
                Vector2 a = new Vector2(points[i].x, points[i].z), b = new Vector2(points[i + 1].x, points[i + 1].z);
                float distance = PolylineUtils.DistanceToSegmentSq(plan, a, b);
                if (distance >= best) continue;
                best = distance;
                result = Mathf.Lerp(cumulative[i], cumulative[i + 1], PolylineUtils.ProjectOnSegment(plan, a, b));
            }
            return result;
        }
    }

    private static List<PathSummary> Joints(PathSummary path, bool start)
        => JointProvider?.Invoke(path, start) ?? new List<PathSummary>();

    private static bool OwnsJoint(PathSummary path, List<PathSummary> joints)
    {
        foreach (var other in joints)
            if (other.path_id < path.path_id) return false;
        return true;
    }

    /// <summary>Plan (XZ) positions of the path's start, waypoints and end.</summary>
    private static List<Vector2> PlanAnchors(PathSummary path)
    {
        var anchors = new List<Vector2>();
        void Add(float[] p)
        {
            if (p != null && p.Length >= 2) anchors.Add(new Vector2(p[0], p[p.Length - 1]));
        }
        Add(path.GetStartPointList()?.ToArray());
        var waypoints = path.GetWaypointList();
        if (waypoints != null) foreach (var wp in waypoints) Add(wp?.position);
        Add(path.GetEndPointList()?.ToArray());
        return anchors;
    }

    /// <summary>Plan position at a plan distance from the start along the anchors.</summary>
    private static Vector2 PlanPoint(List<Vector2> anchors, float station)
    {
        for (int i = 1; i < anchors.Count; i++)
        {
            float span = Vector2.Distance(anchors[i - 1], anchors[i]);
            if (station <= span || i == anchors.Count - 1) return Vector2.Lerp(anchors[i - 1], anchors[i], span < 1e-4f ? 0f : station / span);
            station -= span;
        }
        return anchors[0];
    }

    /// <summary>
    /// Tower positions as plan (XZ) distances from the path start along its anchors. A joined end has its tower at the end;
    /// a free end at SIDE_SPAN_RATIO of the end span. Null if the anchors are degenerate.
    /// </summary>
    private static float[] TowerStations(List<Vector2> anchors, bool startJoint, bool endJoint)
    {
        if (anchors.Count < 2) return null;
        var cumulative = new float[anchors.Count];
        for (int i = 1; i < anchors.Count; i++) cumulative[i] = cumulative[i - 1] + Vector2.Distance(anchors[i - 1], anchors[i]);
        float planLength = cumulative[anchors.Count - 1];
        if (planLength < 1e-3f) return null;

        var stations = new float[anchors.Count];
        stations[0] = startJoint ? 0f : cumulative[1] * SIDE_SPAN_RATIO;
        for (int i = 1; i < anchors.Count - 1; i++) stations[i] = cumulative[i];
        int last = anchors.Count - 1;
        stations[last] = endJoint ? planLength : planLength - (planLength - cumulative[last - 1]) * SIDE_SPAN_RATIO;
        return stations;
    }

    /// <summary>Tower heights (before width scaling) from the longest adjacent main span, including neighbor spans at joints.</summary>
    private static float[] TowerHeights(float[] stations, float startNeighborSpan, float endNeighborSpan)
    {
        int last = stations.Length - 1;
        var heights = new float[stations.Length];
        for (int k = 0; k <= last; k++)
        {
            float span = Mathf.Max(k > 0 ? stations[k] - stations[k - 1] : 0f, k < last ? stations[k + 1] - stations[k] : 0f);
            if (k == 0) span = Mathf.Max(span, startNeighborSpan);
            if (k == last) span = Mathf.Max(span, endNeighborSpan);
            heights[k] = Mathf.Max(MIN_TOWER_HEIGHT, span * TOWER_HEIGHT_PER_SPAN);
        }
        return heights;
    }

    /// <summary>Longest main span of the joined bridges next to the shared tower at this path's start/end node.</summary>
    private static float NeighborSpan(PathSummary path, bool start, List<PathSummary> joints)
    {
        int node = start ? path.start_node_id : path.end_node_id;
        float longest = 0f;
        foreach (var other in joints)
        {
            bool otherStart = other.start_node_id == node;
            float[] s = TowerStations(PlanAnchors(other), otherStart || Joints(other, true).Count > 0, !otherStart || Joints(other, false).Count > 0);
            if (s == null) continue;
            longest = Mathf.Max(longest, otherStart ? s[1] - s[0] : s[s.Length - 1] - s[s.Length - 2]);
        }
        return longest;
    }
}
