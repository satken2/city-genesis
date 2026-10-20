using System.Collections.Generic;
using UnityEngine;
using Dreamteck.Splines;
using GeneratedModels;

/// <summary>
/// Single source of truth for LRT track positions, rail-top heights, stop-platform dimensions, and shared-street track-part generation.
/// Tracks, vehicles, intersection rail connectors, and platforms all share these values.
/// One LRT path carries symmetric double track for both directions (fixed DIR_BOTH).
/// </summary>
public static class LrtRoadParts
{
    public static bool IsEmbedded(int variant) => variant == 222 || variant == 242 || variant == 303;
    public static bool IsLrt(int variant) => IsEmbedded(variant) || variant == 431;

    public const float EMBEDDED_RAIL_TOP = .025f;       // lrt_track rail top (from road-surface origin).
    public const float DEDICATED_RAIL_TOP = .355f;      // 431 track rail top (shared with 411, relative to TRACK_ABOVE_SPLINE).
    public const float MALL_TRACK_OFFSET = 1.75f;       // 303 track centers. Same 3.5m spacing as 431 (7m width, two tracks).
    public const float MALL_POLE_OFFSET = 5.4f;         // 303 roadside pole bases. Arms extend toward the nearer track.
    public const float VEHICLE_HALF_WIDTH = 1.27f;      // Half LRT body width (2.54m).
    public const float MALL_VEHICLE_ENVELOPE = MALL_TRACK_OFFSET + VEHICLE_HALF_WIDTH;  // Half-width of vehicle passage on 303.

    public const float PLATFORM_EDGE_GAP = 1.35f;       // Track center to platform inner edge (body half-width+0.08m).
    public const float PLATFORM_ABOVE_RAIL = .30f;      // Platform top above rail top.
    public const float PLATFORM_END_MARGIN = 1f;        // Margin from each train end to deck end.
    public const float PLATFORM_MERGE_GAP = 2f;         // Merge platforms on the same path if their gap is below this.
    public const float PLATFORM_MIN_LENGTH = 4f;        // Omit decks shorter than this after fitting the clipped section.
    public const float SHELTER_MIN_DECK_LENGTH = 8f;    // Minimum deck length for shelters.
    public const float PLATFORM_POLE_CLEARANCE = .5f;   // 431 pole distance from platform outer edge.
    public const float PLATFORM_TRACK_ZONE_MARGIN = 2f;     // Margin beyond ramp ends for 431 pole relocation/parapet omission.
    public const float PLATFORM_EQUIPMENT_ZONE_MARGIN = 1f; // Margin beyond ramp ends for 303 roadside-equipment exclusion.

    /// <summary>
    /// Lateral track-center distance (positive, one of two symmetric tracks).
    /// 222/242 use innermost bidirectional lane centers; 303 is constant; 431 matches TrackPath.GetTrackCenterOffset derivation.
    /// </summary>
    public static float Offset(PathSummary path)
    {
        if (path.path_variant == 303) return MALL_TRACK_OFFSET;
        if (!IsEmbedded(path.path_variant))
        {
            TrackPath.GetTrackMasterParams(path, out float trackWidth, out int trackLanes, out _, out _, out _, out _, out _, out _);
            return Mathf.Abs(TrackPath.GetTrackCenterOffset(trackWidth, trackLanes, 0, TrackPath.GetTrackCenterWidth(path)));
        }
        RoadPath.GetPathMasterParams(path, out float width, out float sidewalk, out float side, out _,
            out int lanes, out _, out float center, out _, out _, out _, out _);
        // Shared-street track positions remain in bidirectional inner lanes even when changed to one-way traffic.
        var layout = new RoadLaneLayout(width, sidewalk, side, center, lanes, Constants.DIR_BOTH);
        return Mathf.Abs(layout.Center(layout.Count - 1, Constants.TRAFFIC_HAND_LEFT));
    }

    /// <summary>Return all lateral track centers ascending (positive rightward along start→end).</summary>
    public static List<float> TrackLaterals(PathSummary path)
    {
        if (IsEmbedded(path.path_variant))
        {
            float offset = Offset(path);
            return new List<float> { -offset, offset };
        }
        TrackPath.GetTrackMasterParams(path, out float width, out int lanes, out _, out _, out _, out _, out _, out _);
        int count = Mathf.Max(1, lanes);
        float centerWidth = TrackPath.GetTrackCenterWidth(path);
        var laterals = new List<float>(count);
        for (int i = 0; i < count; i++) laterals.Add(TrackPath.GetTrackCenterOffset(width, count, i, centerWidth));
        return laterals;
    }

    /// <summary>Rail top above spline. Embedded track=0.125m, 431=0.475m.</summary>
    public static float RailTopAboveSpline(int variant) => IsEmbedded(variant)
        ? RoadPartHeights.BODY_ABOVE_SPLINE + EMBEDDED_RAIL_TOP
        : RoadPartHeights.TRACK_ABOVE_SPLINE + DEDICATED_RAIL_TOP;

    /// <summary>Platform width. For 222/242, safe-island width avoiding outer-lane vehicles.</summary>
    public static float PlatformWidth(int variant) => variant == 222 || variant == 242 ? 1.2f : 2f;

    /// <summary>Ramp length outside each deck end.</summary>
    public static float PlatformRampLength(int variant) => variant == 431 ? 6f : 3f;

    /// <summary>Platform inner-edge lateral distance (outside track, from centerline).</summary>
    public static float PlatformInnerOffset(PathSummary path) => Offset(path) + PLATFORM_EDGE_GAP;

    /// <summary>Platform outer-edge lateral distance from centerline.</summary>
    public static float PlatformOuterOffset(PathSummary path) => PlatformInnerOffset(path) + PlatformWidth(path.path_variant);

    /// <summary>Platform top above spline.</summary>
    public static float PlatformSurfaceAboveSpline(int variant) => RailTopAboveSpline(variant) + PLATFORM_ABOVE_RAIL;

    /// <summary>Deck length = train length (car count×car length) + end margins.</summary>
    public static float PlatformDeckLength(float consistLength) => consistLength + 2f * PLATFORM_END_MARGIN;

    /// <summary>
    /// Pole distances from clipped-spline start. Leave min(spacing/2, 5m) at ends, then place at regular intervals.
    /// Pole rendering and 303 equipment reservations avoiding pole bases use the same sequence.
    /// </summary>
    public static IEnumerable<float> PoleDistances(float length, float spacing)
    {
        if (spacing <= 0f) yield break;
        float margin = Mathf.Min(spacing * .5f, 5f);
        for (float distance = margin; distance <= length - margin; distance += spacing) yield return distance;
    }

    /// <summary>
    /// Shared-street (222/242/303) tracks/wires/poles and stop platforms (platforms from LrtPlatformParts.Resolve).
    /// 222/242 center poles and 303 side poles do not intersect platforms, so remain fixed within platform sections.
    /// </summary>
    public static void Generate(GameObject parent, SplineComputer spline, PathSummary path, List<LrtPlatformParts.Span> platforms = null)
    {
        if (!IsEmbedded(path.path_variant)) return;
        string prefix = $"Models/Paths/p{path.path_variant}";
        float spacing = MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.wire_spacing ?? 0;
        foreach (float x in TrackLaterals(path))
        {
            RoadPath.CreateSplineMesh(parent, spline, prefix + "/lrt_track", 3, x,
                RoadPartHeights.BODY_ABOVE_SPLINE, stretchToWidth: false);
            if (spacing > 0)
                RoadPath.CreateSplineMesh(parent, spline, prefix + "/lrt_wire", 1, x,
                    RoadPartHeights.BODY_ABOVE_SPLINE, stretchToWidth: false);
        }
        LrtPlatformParts.Generate(parent, spline, path, platforms);
        if (spacing <= 0) return;
        var pole = ModelManager.Instance?.GetModel(prefix + "/lrt_pole", true);
        if (pole == null) return;
        foreach (float distance in PoleDistances(spline.CalculateLength(), spacing))
        {
            double percent = spline.Travel(0, distance, Spline.Direction.Forward);
            if (PathDrawContext.HasDetailRange && (percent < PathDrawContext.DetailFrom || percent > PathDrawContext.DetailTo)) continue;
            var sample = spline.Evaluate(percent);
            if (PathTunnelParts.IsTunnel(path, sample.position)) continue;
            Vector3 forward = Vector3.ProjectOnPlane(sample.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f) continue;
            var placed = Object.Instantiate(pole, parent.transform);
            placed.name = "LrtPole";
            placed.transform.position = sample.position + Vector3.up * RoadPartHeights.BODY_ABOVE_SPLINE;
            placed.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            foreach (var renderer in placed.GetComponentsInChildren<MeshRenderer>())
            {
                var materials = renderer.sharedMaterials;
                for (int m = 0; m < materials.Length; m++) materials[m] = RoadPath.GetOrCreateRoadMaterial(materials[m]);
                renderer.sharedMaterials = materials;
            }
        }
    }
}
