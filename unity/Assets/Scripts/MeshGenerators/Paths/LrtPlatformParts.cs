using System;
using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// Resolve/render LRT side platforms (outside each track). Base-path generators (TrackPath for 431,
/// LrtRoadParts.Generate for 222/242/303) call this during mesh generation, excluding Far/construction.
/// Stop positions come from line-summary stop_points (StopProvider, configured by DataLoaders/LrtStopPlatforms);
/// fraction→distance matches vehicles: fraction × uncut 3D point-list length (TransitRouteGeometry). LrtRoadParts defines dimensions.
/// </summary>
public static class LrtPlatformParts
{
    /// <summary>One stop on one line. Stop-center (train center) fraction and deck length (train length plus end margins, m).</summary>
    public readonly struct Stop
    {
        public readonly float Fraction, DeckLength;
        public Stop(float fraction, float deckLength) { Fraction = fraction; DeckLength = deckLength; }
    }

    /// <summary>
    /// Deck interval on rendered points. From/To are spline distances (m) from rendered-list start.
    /// Ends clipped by tunnel rendering sections or other tracks (Clip) are not real deck ends; omit ramps there.
    /// </summary>
    public struct Span
    {
        public float From, To;
        public bool RampAtFrom, RampAtTo;
        public float Length => To - From;
    }

    /// <summary>Stops by path ID. Without setup (test scenes, etc.), omit platforms.</summary>
    public static Func<int, IReadOnlyList<Stop>> StopProvider;

    /// <summary>Provider of terrain-following points by path ID/intersection clipping (set by PathManager). If unset, treat rendered points as the full path.</summary>
    public static Func<int, bool, List<Vector3>> PolylineProvider;

    private const float SLAB_STEP = 1f;             // Cross-section interval for fallback slabs.
    private const float SLAB_BOTTOM_BELOW_SPLINE = .3f; // Fallback slab bottom, deep enough to absorb terrain variation.
    private const float END_TOLERANCE = .01f;           // Distance treating rendering-section endpoints as deck endpoints.

    /// <summary>
    /// Return deck intervals on rendered points (intersection-clipped; tunnel rendering sections). Empty for non-LRT bases or no stops.
    /// </summary>
    public static List<Span> Resolve(PathSummary path, IReadOnlyList<Vector3> drawn, float splineLength)
    {
        if (path == null || !LrtRoadParts.IsLrt(path.path_variant) || path.status == Constants.STATUS_CONSTRUCTING
            || drawn == null || drawn.Count < 2) return new List<Span>();
        var stops = StopProvider?.Invoke(path.path_id);
        if (stops == null || stops.Count == 0) return new List<Span>();
        var full = PolylineProvider?.Invoke(path.path_id, false);
        var trimmed = PolylineProvider?.Invoke(path.path_id, true);
        return Layout(stops, full != null && full.Count >= 2 ? full : drawn, trimmed != null && trimmed.Count >= 2 ? trimmed : drawn,
            drawn, splineLength);
    }

    /// <summary>
    /// Convert stops to distance intervals on uncut points; merge/fit into clipped sections, discarding decks below PLATFORM_MIN_LENGTH afterward.
    /// Prioritize covering stopped trains, allowing decks to reach cut endpoints and ramps to extend beyond onto intersection hulls
    /// or past dead ends. Clip retained decks to rendered points; return distances proportionally converted to spline length.
    /// </summary>
    public static List<Span> Layout(IReadOnlyList<Stop> stops, IReadOnlyList<Vector3> full, IReadOnlyList<Vector3> trimmed,
        IReadOnlyList<Vector3> drawn, float splineLength)
    {
        var result = new List<Span>();
        var line = new VehiclePolyline(full);
        float trimStart = line.Project(trimmed[0]), trimEnd = line.Project(trimmed[trimmed.Count - 1]);
        float drawnStart = line.Project(drawn[0]), drawnEnd = line.Project(drawn[drawn.Count - 1]);
        if (drawnEnd - drawnStart < END_TOLERANCE || splineLength <= 0f) return result;
        float scale = splineLength / (drawnEnd - drawnStart);
        foreach (var deck in MergeDecks(stops, line.Length))
        {
            float from = Mathf.Max(deck.x, trimStart), to = Mathf.Min(deck.y, trimEnd);
            if (to - from < LrtRoadParts.PLATFORM_MIN_LENGTH) continue;
            float drawnFrom = Mathf.Max(from, drawnStart), drawnTo = Mathf.Min(to, drawnEnd);
            if (drawnTo - drawnFrom < END_TOLERANCE) continue;
            result.Add(new Span
            {
                From = (drawnFrom - drawnStart) * scale,
                To = (drawnTo - drawnStart) * scale,
                RampAtFrom = from >= drawnStart - END_TOLERANCE,
                RampAtTo = to <= drawnEnd + END_TOLERANCE,
            });
        }
        return result;
    }

    /// <summary>
    /// Convert each stop deck to uncut-point distance interval (x=start, y=end); merge overlaps and gaps under PLATFORM_MERGE_GAP
    /// and return ascending. Multiple lines sharing/nearby stops therefore form one platform.
    /// </summary>
    public static List<Vector2> MergeDecks(IReadOnlyList<Stop> stops, float fullLength)
    {
        var decks = new List<Vector2>(stops.Count);
        foreach (var stop in stops)
        {
            if (stop.DeckLength <= 0f) continue;
            float center = stop.Fraction * fullLength;
            decks.Add(new Vector2(center - stop.DeckLength * .5f, center + stop.DeckLength * .5f));
        }
        decks.Sort((a, b) => a.x.CompareTo(b.x));
        var merged = new List<Vector2>(decks.Count);
        foreach (var deck in decks)
        {
            int last = merged.Count - 1;
            if (last >= 0 && deck.x - merged[last].y < LrtRoadParts.PLATFORM_MERGE_GAP)
                merged[last] = new Vector2(merged[last].x, Mathf.Max(merged[last].y, deck.y));
            else merged.Add(deck);
        }
        return merged;
    }

    /// <summary>
    /// Return sorted, merged platform intervals (deck + end ramps + margin) as spline distances (x=start, y=end).
    /// Used for 431 pole relocation/parapet omission and 303 roadside-equipment suppression. Decks at cut endpoints extend intervals beyond the spline.
    /// </summary>
    public static List<Vector2> ZoneRanges(List<Span> spans, float rampLength, float margin)
    {
        var zones = new List<Vector2>();
        if (spans == null) return zones;
        foreach (var span in spans)
            zones.Add(new Vector2(span.From - rampLength - margin, span.To + rampLength + margin));
        zones.Sort((a, b) => a.x.CompareTo(b.x));
        var merged = new List<Vector2>(zones.Count);
        foreach (var zone in zones)
        {
            int last = merged.Count - 1;
            if (last >= 0 && zone.x <= merged[last].y) merged[last] = new Vector2(merged[last].x, Mathf.Max(merged[last].y, zone.y));
            else merged.Add(zone);
        }
        return merged;
    }

    public static bool Contains(IReadOnlyList<Vector2> ranges, float distance)
    {
        if (ranges == null) return false;
        foreach (var range in ranges)
            if (distance >= range.x && distance <= range.y) return true;
        return false;
    }

    /// <summary>Return the complement as ascending spline-percent intervals. No intervals gives one full-length interval.</summary>
    public static List<TrackCorridorInfo.PercentRange> OutsidePercentRanges(SplineComputer spline, IReadOnlyList<Vector2> ranges)
    {
        var result = new List<TrackCorridorInfo.PercentRange>();
        double cursor = 0.0;
        if (ranges != null)
            foreach (var range in ranges)
            {
                double from = Percent(spline, range.x), to = Percent(spline, range.y);
                if (from > cursor) result.Add(new TrackCorridorInfo.PercentRange { From = cursor, To = from });
                cursor = Math.Max(cursor, to);
            }
        if (cursor < 1.0) result.Add(new TrackCorridorInfo.PercentRange { From = cursor, To = 1.0 });
        return result;
    }

    /// <summary>
    /// Subtract blocked intervals on this platform side (ascending, nonoverlapping spline distances) from deck intervals.
    /// New cut ends touch other tracks, so omit ramps; also omit at true ends if ramp treads (rampLength from end)
    /// overlap blocked. Discard remaining fragments below PLATFORM_MIN_LENGTH.
    /// Intervals with nothing removed remain unchanged, including short fragments clipped by tunnel rendering sections.
    /// </summary>
    public static List<Span> Clip(Span span, IReadOnlyList<Vector2> blocked, float rampLength)
    {
        var pieces = new List<Span>();
        var current = span;
        bool cut = false;
        if (blocked != null)
            foreach (var range in blocked)
            {
                if (range.y <= current.From || range.x >= current.To) continue;
                cut = true;
                if (range.x > current.From)
                    pieces.Add(new Span { From = current.From, To = range.x, RampAtFrom = current.RampAtFrom });
                current = new Span { From = range.y, To = current.To, RampAtTo = current.RampAtTo };
                if (current.To <= current.From) break;
            }
        if (current.To > current.From) pieces.Add(current);
        if (cut) pieces.RemoveAll(piece => piece.Length < LrtRoadParts.PLATFORM_MIN_LENGTH);
        for (int i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            piece.RampAtFrom &= !Overlaps(blocked, piece.From - rampLength, piece.From);
            piece.RampAtTo &= !Overlaps(blocked, piece.To, piece.To + rampLength);
            pieces[i] = piece;
        }
        return pieces;
    }

    private static bool Overlaps(IReadOnlyList<Vector2> ranges, float from, float to)
    {
        if (ranges == null) return false;
        foreach (var range in ranges)
            if (range.x < to && range.y > from) return true;
        return false;
    }

    /// <summary>
    /// Draw platforms on both sides. Resolve parts variant→type, skipping absent parts.
    /// Only lrt_platform falls back to a procedural slab (keeps stop locations minimally visible).
    /// leftBlocked/rightBlocked are intervals where other track beds overlap the platform strip (spline distances from 431 joining corridors);
    /// omit those portions on each side. Place one shelter centered on each side's longest retained deck.
    /// </summary>
    public static void Generate(GameObject parent, SplineComputer spline, PathSummary path, List<Span> spans,
        IReadOnlyList<Vector2> leftBlocked = null, IReadOnlyList<Vector2> rightBlocked = null)
    {
        if (spans == null || spans.Count == 0 || spline == null) return;
        int variant = path.path_variant;
        string variantBase = $"Models/Paths/p{variant}", typeBase = $"Models/Paths/p{path.path_type}";
        float track = LrtRoadParts.Offset(path);
        float inner = LrtRoadParts.PlatformInnerOffset(path), width = LrtRoadParts.PlatformWidth(variant), outer = inner + width;
        float railTop = LrtRoadParts.RailTopAboveSpline(variant), surface = LrtRoadParts.PlatformSurfaceAboveSpline(variant);
        bool modeled = LoadOptional(variantBase, typeBase, "lrt_platform") != null;
        GameObject end = LoadOptional(variantBase, typeBase, "lrt_platform_end");
        GameObject shelter = LoadOptional(variantBase, typeBase, "lrt_shelter");
        // Align shelter back (+X) to the outer edge. Read depth from actual model dimensions.
        float shelterBack = 0f;
        MeshFilter shelterFilter = shelter != null ? shelter.GetComponent<MeshFilter>() : null;
        if (shelterFilter != null && shelterFilter.sharedMesh != null) shelterBack = shelterFilter.sharedMesh.bounds.max.x;
        float rampLength = LrtRoadParts.PlatformRampLength(variant);
        foreach (var span in spans)
            for (int side = -1; side <= 1; side += 2)
            {
                Span? longest = null;
                foreach (var piece in Clip(span, side < 0 ? leftBlocked : rightBlocked, rampLength))
                {
                    double from = Percent(spline, piece.From), to = Percent(spline, piece.To);
                    if (to - from < 1e-6) continue;
                    // Place right-side models with origin at track center; X-mirror the left side outward. Preserve the authored 4m design period.
                    if (modeled)
                        RoadPath.CreateSplineMesh(parent, spline, variantBase + "/lrt_platform", width, side * track, railTop, side < 0,
                            typeBase + "/lrt_platform", stretchToWidth: false, clipFrom: from, clipTo: to, compensatePivot: false,
                            maxTileLength: 0f, normalizeBodySurface: false);
                    else BuildSlab(parent, spline, from, to, side, inner, outer, surface);
                    float rampLateral = side * (inner + width * .5f);
                    if (piece.RampAtFrom) PlaceRamp(parent, end, spline, from, false, rampLateral, railTop);
                    if (piece.RampAtTo) PlaceRamp(parent, end, spline, to, true, rampLateral, railTop);
                    if (longest == null || piece.Length > longest.Value.Length) longest = piece;
                }
                if (shelter != null && longest != null && longest.Value.Length >= LrtRoadParts.SHELTER_MIN_DECK_LENGTH)
                {
                    double center = Percent(spline, (longest.Value.From + longest.Value.To) * .5f);
                    if (!PathTunnelParts.IsTunnel(path, spline.EvaluatePosition(center)))
                        PlaceShelter(parent, shelter, spline, center, side, outer - shelterBack, surface);
                }
            }
    }

    private static double Percent(SplineComputer spline, float distance) =>
        RoadPath.ClampSplinePercent(spline.Travel(0.0, Mathf.Max(0f, distance), Spline.Direction.Forward));

    private static bool InDetailRange(double percent) =>
        !PathDrawContext.HasDetailRange || (percent >= PathDrawContext.DetailFrom && percent <= PathDrawContext.DetailTo);

    private static GameObject LoadOptional(string variantBase, string typeBase, string part) =>
        ModelManager.Instance?.GetModel($"{variantBase}/{part}", true) ?? ModelManager.Instance?.GetModel($"{typeBase}/{part}", true);

    /// <summary>
    /// Ramp +Z points outward from deck; tilt along deck-end grade (align tip to neighboring road/bed surface).
    /// Decks at cut endpoints extend beyond the clipped spline (onto intersection hulls/past dead ends).
    /// </summary>
    private static void PlaceRamp(GameObject parent, GameObject prefab, SplineComputer spline, double percent, bool outwardForward, float lateral, float railTop)
    {
        if (prefab == null || !InDetailRange(percent)) return;
        SplineSample sample = spline.Evaluate(percent);
        Vector3 flat = Vector3.ProjectOnPlane(sample.forward, Vector3.up);
        if (flat.sqrMagnitude < 1e-6f) return;
        Vector3 right = Vector3.Cross(Vector3.up, flat).normalized;
        Vector3 outward = (outwardForward ? sample.forward : -sample.forward).normalized;
        Place(parent, prefab, "LrtPlatformEnd", sample.position + right * lateral + Vector3.up * railTop, Quaternion.LookRotation(outward, Vector3.up));
    }

    /// <summary>Keep shelters upright, open face (-X) toward track. Left side rotates 180° about Y.</summary>
    private static void PlaceShelter(GameObject parent, GameObject prefab, SplineComputer spline, double percent, int side, float lateral, float surface)
    {
        if (!InDetailRange(percent)) return;
        SplineSample sample = spline.Evaluate(percent);
        Vector3 flat = Vector3.ProjectOnPlane(sample.forward, Vector3.up);
        if (flat.sqrMagnitude < 1e-6f) return;
        flat.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, flat);
        Quaternion rotation = Quaternion.LookRotation(flat, Vector3.up) * (side < 0 ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity);
        Place(parent, prefab, "LrtShelter", sample.position + right * (side * lateral) + Vector3.up * surface, rotation);
    }

    private static void Place(GameObject parent, GameObject prefab, string name, Vector3 position, Quaternion rotation)
    {
        var placed = UnityEngine.Object.Instantiate(prefab, position, rotation, parent.transform);
        placed.name = name;
        foreach (var renderer in placed.GetComponentsInChildren<MeshRenderer>())
        {
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++) materials[i] = RoadPath.GetOrCreateRoadMaterial(materials[i]);
            renderer.sharedMaterials = materials;
        }
    }

    /// <summary>
    /// Fallback for missing lrt_platform. Extrude a box section from inner to outer edge and top to SLAB_BOTTOM_BELOW_SPLINE below spline
    /// along the spline (top, both sides, bottom, end faces). Shared concrete material.
    /// </summary>
    private static void BuildSlab(GameObject parent, SplineComputer spline, double from, double to, int side, float inner, float outer, float surface)
    {
        if (!PathDrawContext.IntersectDetailRange(from, to, out from, out to)) return;
        int steps = Mathf.Max(1, Mathf.CeilToInt(spline.CalculateLength() * (float)(to - from) / SLAB_STEP));
        var rings = new Vector3[steps + 1][];
        var normals = new Vector3[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            SplineSample sample = spline.Evaluate(RoadPath.ClampSplinePercent(from + (to - from) * i / steps));
            Vector3 flat = Vector3.ProjectOnPlane(sample.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, flat).normalized * side;
            Vector3 p = sample.position;
            rings[i] = new[]
            {
                parent.transform.InverseTransformPoint(p + right * inner + Vector3.up * surface),
                parent.transform.InverseTransformPoint(p + right * outer + Vector3.up * surface),
                parent.transform.InverseTransformPoint(p + right * outer - Vector3.up * SLAB_BOTTOM_BELOW_SPLINE),
                parent.transform.InverseTransformPoint(p + right * inner - Vector3.up * SLAB_BOTTOM_BELOW_SPLINE),
            };
            normals[i] = parent.transform.InverseTransformDirection(flat.normalized);
        }
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int i = 0; i < steps; i++)
            for (int k = 0; k < 4; k++)
            {
                Vector3 a = rings[i][k], b = rings[i][(k + 1) % 4], c = rings[i + 1][(k + 1) % 4], d = rings[i + 1][k];
                Vector3 center = (rings[i][0] + rings[i][2]) * .5f;
                AddQuad(vertices, triangles, a, b, c, d, (a + b) * .5f - center);
            }
        AddQuad(vertices, triangles, rings[0][0], rings[0][1], rings[0][2], rings[0][3], -normals[0]);
        AddQuad(vertices, triangles, rings[steps][0], rings[steps][1], rings[steps][2], rings[steps][3], normals[steps]);
        var mesh = new Mesh { name = "LrtPlatformSlab" };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        var go = new GameObject("LrtPlatformSlab");
        go.transform.SetParent(parent.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = BuildingMeshBuilder.GetCommonConcreteMaterial();
        go.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;
    }

    /// <summary>Separate vertices per face to preserve face normals. Align winding to the approximate outward direction.</summary>
    private static void AddQuad(List<Vector3> vertices, List<int> triangles, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward)
    {
        int n = vertices.Count;
        vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
        bool forward = Vector3.Dot(Vector3.Cross(b - a, c - a), outward) >= 0f;
        if (forward) triangles.AddRange(new[] { n, n + 1, n + 2, n, n + 2, n + 3 });
        else triangles.AddRange(new[] { n, n + 2, n + 1, n, n + 3, n + 2 });
    }
}
