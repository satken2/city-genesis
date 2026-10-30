using System;
using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// Towers use actual width; main cables follow span-dependent parabolas.
/// Towers stand at every anchor between two suspension spans (waypoints, and nodes joined to another suspension bridge),
/// and at the free ends of a chain at SIDE_SPAN_RATIO of the end span, where the cable drops to the deck.
/// A joint tower is shared: only the bridge with the smallest path ID at the node draws it, and every bridge there sizes it
/// from the longest adjacent main span so their cables meet the same tower top.
/// </summary>
public static class SuspensionBridgeGenerator
{
    private const float SIDE_SPAN_RATIO = 0.2f;        // End-to-tower distance as a share of the end span.
    private const float TOWER_HEIGHT_PER_SPAN = 0.2f;  // Tower height per metre of the adjacent main span (12m two-lane reference).
    private const float MIN_TOWER_HEIGHT = 8f;

    /// <summary>Other suspension bridges joined at the path's start (true) or end node. Provided by PathManager.</summary>
    public static Func<PathSummary, bool, List<PathSummary>> JointProvider;

    /// <summary>Whether the path is drawn as a suspension bridge (its variant has a tower model).</summary>
    public static bool Handles(PathSummary path)
        => path != null && ModelManager.Instance != null
            && ModelManager.Instance.GetModel($"Models/Paths/p{path.path_variant}/tower", true) != null;

    public static void Generate(GameObject parent, SplineComputer spline, PathSummary path, float width)
    {
        var prefab = PathDrawContext.ResolvePrefab($"Models/Paths/p{path.path_variant}/tower", null, out _);
        if (prefab == null) return;
        string truss = $"Models/Paths/p{path.path_variant}/truss";
        if (PathDrawContext.ResolvePrefab(truss, null, out _) != null)
            RoadPath.CreateSplineMesh(parent, spline, truss, width, 0, RoadPartHeights.BODY_ABOVE_SPLINE,
                stretchToWidth: false, maxTileLength: 0, normalizeBodySurface: false);
        float length = spline.CalculateLength();
        if (length < 8f) return;
        var filter = prefab.GetComponentInChildren<MeshFilter>();
        var renderer = prefab.GetComponentInChildren<MeshRenderer>();
        if (filter == null || renderer == null) return;

        var startJoints = Joints(path, true);
        var endJoints = Joints(path, false);
        float[] stations = TowerStations(path, startJoints.Count > 0, endJoints.Count > 0, out float planLength);
        if (stations == null) return;
        int last = stations.Length - 1;
        float[] heights = TowerHeights(stations,
            startJoints.Count > 0 ? NeighborSpan(path, true, startJoints) : 0f,
            endJoints.Count > 0 ? NeighborSpan(path, false, endJoints) : 0f);

        // Use a 12m two-lane bridge as reference; preserve tower proportions/roadside margins on wider bridges.
        float widthScale = Mathf.Max(1f, width / 12f);
        for (int k = 0; k <= last; k++) heights[k] *= widthScale;
        // Stations are measured on the anchor plan; spread them over the (possibly trimmed) spline in proportion.
        var percents = new double[stations.Length];
        for (int k = 0; k <= last; k++)
            percents[k] = k == 0 && startJoints.Count > 0 ? 0.0
                : k == last && endJoints.Count > 0 ? 1.0
                : RoadPath.ClampSplinePercent(spline.Travel(0.0, stations[k] * length / planLength, Spline.Direction.Forward));

        float lateral = width*.5f + .8f * widthScale;
        var metal = System.Array.Find(renderer.sharedMaterials, m => m != null && m.name.StartsWith("PATH_METAL"));
        var batch = new PathStructureMesh(parent, "SuspensionCables", new[] { metal ?? renderer.sharedMaterial });
        Vector3 Deck(double t, float x)
        {
            var sample = spline.Evaluate(RoadPath.ClampSplinePercent(t));
            var right = Vector3.Cross(Vector3.up, sample.forward).normalized;
            return sample.position + right*x + Vector3.up*RoadPartHeights.BODY_ABOVE_SPLINE;
        }
        for (int k = 0; k <= last; k++)
        {
            if (k == 0 && startJoints.Count > 0 && !OwnsJoint(path, startJoints)) continue;
            if (k == last && endJoints.Count > 0 && !OwnsJoint(path, endJoints)) continue;
            var sample = spline.Evaluate(percents[k]);
            var tower = UnityEngine.Object.Instantiate(prefab,parent.transform);
            tower.name = "SuspensionTower";
            tower.transform.position = Deck(percents[k],0);
            tower.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(sample.forward,Vector3.up).normalized);
            tower.transform.localScale = new Vector3(1,heights[k]/12f,1);
            foreach (var r in tower.GetComponentsInChildren<MeshRenderer>())
            {
                var mats = r.sharedMaterials;
                for (int i=0;i<mats.Length;i++) mats[i]=RoadPath.GetOrCreateRoadMaterial(mats[i]);
                r.sharedMaterials=mats;
            }
            for (int side=-1;side<=1;side+=2)
            {
                Vector3 top=Deck(percents[k],side*lateral);
                Vector3 ground=new Vector3(top.x,Utils.GetGroundHeight(top),top.z);
                if (top.y > ground.y) batch.Bar(ground,top,.85f * widthScale);
            }
        }

        // Cable spans: [from, to] in spline percent, with rise above the deck as a function of the span parameter u.
        var spans = new List<(double from, double to, Func<float, float> rise)>();
        if (startJoints.Count == 0)
        {
            float h = heights[0];
            spans.Add((0.0, percents[0], u => Mathf.Lerp(.8f, h, u) - h*.12f*4*u*(1-u)));
        }
        for (int k = 0; k < last; k++)
        {
            float a = heights[k], b = heights[k + 1], sag = .75f * Mathf.Min(a, b);
            spans.Add((percents[k], percents[k + 1], u => Mathf.Lerp(a, b, u) - sag*4*u*(1-u)));
        }
        if (endJoints.Count == 0)
        {
            float h = heights[last];
            spans.Add((percents[last], 1.0, u => Mathf.Lerp(h, .8f, u) - h*.12f*4*u*(1-u)));
        }
        for (int side=-1;side<=1;side+=2)
        {
            foreach (var span in spans)
            {
                int count=Mathf.Clamp(Mathf.CeilToInt(length*(float)(span.to-span.from)/3f),4,256);
                Vector3 previous=default;
                for(int i=0;i<=count;i++)
                {
                    float u=(float)i/count;
                    double t=span.from+(span.to-span.from)*u;
                    float rise=span.rise(u);
                    Vector3 deck=Deck(t,side*lateral), cable=deck+Vector3.up*rise;
                    if(i>0) batch.Bar(previous,cable,.25f, sides: 12);
                    if(i>0 && i<count && rise>1f) batch.Bar(deck,cable,.035f);
                    previous=cable;
                }
            }
        }
        batch.Finish();
    }

    private static List<PathSummary> Joints(PathSummary path, bool start)
        => JointProvider?.Invoke(path, start) ?? new List<PathSummary>();

    private static bool OwnsJoint(PathSummary path, List<PathSummary> joints)
    {
        foreach (var other in joints)
            if (other.path_id < path.path_id) return false;
        return true;
    }

    /// <summary>
    /// Tower positions as plan (XZ) distances from the path start along its anchors. A joined end has its tower at the end;
    /// a free end at SIDE_SPAN_RATIO of the end span. Null if the anchors are degenerate.
    /// </summary>
    private static float[] TowerStations(PathSummary path, bool startJoint, bool endJoint, out float planLength)
    {
        planLength = 0f;
        var anchors = new List<Vector2>();
        void Add(float[] p)
        {
            if (p != null && p.Length >= 2) anchors.Add(new Vector2(p[0], p[p.Length - 1]));
        }
        Add(path.GetStartPointList()?.ToArray());
        var waypoints = path.GetWaypointList();
        if (waypoints != null) foreach (var wp in waypoints) Add(wp?.position);
        Add(path.GetEndPointList()?.ToArray());
        if (anchors.Count < 2) return null;

        var cumulative = new float[anchors.Count];
        for (int i = 1; i < anchors.Count; i++) cumulative[i] = cumulative[i - 1] + Vector2.Distance(anchors[i - 1], anchors[i]);
        planLength = cumulative[anchors.Count - 1];
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
            float[] s = TowerStations(other, otherStart || Joints(other, true).Count > 0, !otherStart || Joints(other, false).Count > 0, out _);
            if (s == null) continue;
            longest = Mathf.Max(longest, otherStart ? s[1] - s[0] : s[s.Length - 1] - s[s.Length - 2]);
        }
        return longest;
    }
}
