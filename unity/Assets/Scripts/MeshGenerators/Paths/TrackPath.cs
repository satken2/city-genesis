using UnityEngine;
using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;

/// <summary>
/// Track (PATH_TRACK)/monorail (PATH_MONORAIL) mesh generation using Dreamteck Splines.
///
/// Like RoadPath, bend prefab parts along splines through SplineMesh,
/// but with different parts (Models/Paths/p{variant}/{partName}, falling back to p{type}):
///
///   body  ... Track bed. Trapezoidal ballast cross-section for surface tracks; elevated/bridge variants
///             use girder+bed prefab cross-sections for elevated decks (no dedicated deck slot;
///             variants are fixed as surface/elevated, so prefab sections suffice).
///             Stretch to full master width.
///   track ... One track (two rails+sleepers). Preserve gauge by avoiding
///             X scaling; place lanes tracks at equal offsets, actual size.
///             Monorail uses the same slot with one guideway-beam model.
///             lanes=2 is combined double track carrying both directions in one path; vehicles use the
///             traffic-hand-side track relative to travel direction (TrackIndexForTravel).
///   wire  ... Contact/catenary wires, actual size at each track center. Prefab geometry supplies wire height
///             (about 5m above rail); code has no height parameter.
///   pole / beam ... Posts and portal beams, instantiated on both sides at wire_spacing.
///   wall / pillar ... Sound barriers/elevated piers, using RoadPath's shared helpers.
///   platform / platform_wall / platform_panel ... Three station-platform parts described below.
///
/// [Station platforms]
/// Station width is track-corridor width, like ordinary tracks. Beds/tracks/intersection hulls/
/// rail connectors render identically, joining neighboring tracks seamlessly.
/// Platforms extend platform_width beyond beds and contain three elements:
///   - platform (track-side edge) ... Actual-size edge geometry at fixed ±PLATFORM_EDGE_OFFSET=1.5m from track center,
///     projecting trackward from corridor edges. Includes yellow lines/tactile paving,
///     platform doors, vertical edge faces, and platform-support posts. Place origin at track centerline/track surface;
///     prefab geometry supplies lateral edge position and rise to platform top.
///   - Deck (platform top) ... Procedural suspended slab of thickness PLATFORM_DECK_THICKNESS (15cm),
///     with top PLATFORM_DECK_SINK below nominal PLATFORM_SURFACE_ABOVE_SPLINE.
///     Extends platform_width from edge to outer platform edge. Material comes from
///     platform_panel (material reference only), falling back to shared concrete.
///   - platform_wall (outer edge) ... Actual-size walls/fences/benches at ±(width/2+platform_width),
///     with origin at track centerline/surface and lateral outer-edge position supplied by geometry.
///     Omitted for island joins/nearby-track encroachment.
/// Master platform_width defines deck width; code height constants define height,
/// structurally matching site/collider/rendering. Author edge/outer-edge prefab lateral positions
/// and rise to match master values/PLATFORM_SURFACE_ABOVE_SPLINE
/// (otherwise they misalign with decks; see CreatePlatformSide comments).
/// Single-track platform sides follow path platform_side (inside/outside/both). Resolve inside/outside to left/right from city
/// traffic side (CityInfoStore.TrafficHand) and track travel_direction
/// (ResolvePlatformExtents; DIR_BACKWARD reverses geometric left/right).
/// Stations with at least two tracks use both exterior side platforms regardless of platform_side;
/// place each side's origin at its outermost track centerline (reuses single-track right-side parts).
/// Stations with a center_width gap between left/right tracks are island stations (IsIslandPlatform),
/// with one central platform and no outer extensions. Place identical edge parts facing inward from both tracks;
/// span one deck between them (CreateIslandPlatformParts). GetTrackCenterOffset positions tracks outside the central gap.
/// This mirrors backend site-polygon resolution (geo_paths.get_platform_extension_widths);
/// changing only one side misaligns site/rendering.
/// On platform sides, omit sound barriers (platform_wall handles outer edges),
/// move poles beyond platform outer edges, and use asymmetric picking colliders including platforms.
///
/// [Joined island platforms]
/// For nearly parallel station paths with facing platforms (e.g. both PLATFORM_SIDE_INSIDE),
/// TrackAdjacency joins platforms if bed-edge gap ≤ combined platform extensions
/// (TrackCorridorInfo Deck* / PlatformWallOmit):
///   - Deck: lower path_id spans fully to neighbor edge; the other draws none
///     (one continuous island, following spacing adjustments).
///   - platform_wall: omit both sides.
///   - Poles/beams: minimum-path_id track spans outside the full island width
///     (same main-track approach as bed-join pole corridors).
/// For encroachment without a valid island (e.g. another bed overlaps platforms),
/// omit platform_wall and clamp decks before neighbor beds.
///
/// Master wire_spacing controls electrification (wire/pole/beam placement);
/// null/nonpositive skips these as nonelectrified.
///
/// LrtPlatformParts draws side platforms for LRT dedicated-track 431 stops separately from station platforms
/// (stops come from line stop_points; move poles outside platform intervals and omit parapets;
/// omit platform portions overlapped by other beds, and main corridor tracks place poles outside neighboring platforms).
///
/// Skip all missing prefab parts; if both bed/tracks are absent, fall back to white strips,
/// allowing gameplay before art assets are complete.
///
/// TrackIntersectionMeshGenerator computes node switch/crossing rails
/// as RailConnectorSpec geometry; this class's CreateRailConnectorObject instantiates them.
///
/// One-way tracks (DIR_FORWARD / DIR_BACKWARD) receive child direction-arrow overlays
/// (TrackDirectionArrows), visible only in road-placement mode
/// (STATE_EDIT_PATH; see TrackDirectionArrows class comments).
///
/// [Nearby-track joined rendering]
/// Double track may be one two-track path or two parallel one-way single tracks.
/// Since the latter otherwise looks like separate single tracks,
/// treat tracks close/parallel in XZ/Y (TrackAdjacency.Compute; thresholds
/// LocalConstants.TRACK_MERGE_*) as a corridor and join rendering below.
/// Converging merges/branches also begin joining before full tangent alignment once bed-edge gaps
/// meet the threshold (TRACK_MERGE_MAX_CONVERGE_ANGLE_DEG):
///   - Bed (body): fill gaps with ballast strips for a continuous bed.
///     Lower path_id owns infill to avoid duplicates.
///   - Walls: clip away sides between joined tracks.
///     Restore walls when branches separate. Independently of parallelism,
///     also omit walls where another bed physically overlaps wall positions (merge throats/shallow crossings)
///     via TrackCorridorInfo.Left/RightWallBlocked.
///   - Poles/beams: only minimum-path_id main track places posts
///     at both full-corridor edges and spans beams. Others omit them in joined sections.
/// Each path still draws its own tracks/wires.
/// Joining affects rendering only, never backend data/pathfinding.
/// Get neighbors from TrackAdjacency.NeighborProvider, set by PathManager;
/// if unset (test scenes, etc.), draw standalone as usual.
/// </summary>
public class TrackPath : IPathMeshGenerator
{
    /// <summary>Connected paths from node reverse lookup. Without a provider, defer buffer-stop placement.</summary>
    public static System.Func<int, List<PathSummary>> ConnectedPathsProvider;
    /// <summary>Facility terminals such as depots. Tracks continue inside facilities, so omit buffer stops.</summary>
    public static System.Func<int, bool> InfrastructureConnectionProvider;
    /// <summary>Track lateral profile (S joins, TrackEndTransition), set by PathManager. If unset, use original positions throughout.</summary>
    public static System.Func<PathSummary, TrackLateralProfile> LateralProfileProvider;

    /// <summary>
    /// Current path laterals (null without S curves). Per-generation value sharing deformation with platforms/end parts.
    /// </summary>
    private static TrackLateralSampler activeLateral;

    /// <summary>
    /// Per-point shifts of current deck track-side/outer edges (geometric frame, null without S curves).
    /// Side platforms shift both edges with their track; islands use left/right track shifts. Set only while
    /// CreatePlatformSide / CreateIslandPlatformParts build decks.
    /// </summary>
    private static float[] deckNearShift;
    private static float[] deckFarShift;
    /// <summary>Pole offset outside corridor (bed) edges (m).</summary>
    public const float POLE_MARGIN = 0.5f;

    /// <summary>
    /// Platform track-side edge distance from its facing track centerline (m).
    /// Fixed regardless of variant width; projects inward beyond bed-corridor edges
    /// to approach the vehicle clearance limit. platform-prefab edge geometry, procedural deck track-side edges,
    /// and neighbor-edge calculations for island joins (TrackAdjacency) share this value.
    /// Centerline-relative distance is PlatformEdgeDistance (equal to this for single track).
    /// </summary>
    public const float PLATFORM_EDGE_OFFSET = 1.5f;

    /// <summary>
    /// Bed-gap-filler overlap into both beds (m).
    /// Exact edge alignment could leave numeric cracks; overlap slightly.
    /// </summary>
    private const float MERGE_FILLER_EDGE_INSET = 0.5f;

    /// <summary>
    /// Bed-gap-filler sink below bed top (BODY_ABOVE_SPLINE), m.
    /// Coplanar overlap causes Z-fighting; lower slightly
    /// beneath ballast shoulders (steps between ballast surfaces are hard to notice).
    /// </summary>
    private const float MERGE_FILLER_SINK = 0.02f;

    /// <summary>Minimum continuous bed-gap-filler length (m), preventing slivers.</summary>
    private const float MERGE_FILLER_MIN_LENGTH = 1.0f;

    /// <summary>Minimum sound-barrier rendering interval (m), preventing tiny walls at join boundaries.</summary>
    private const float MERGE_WALL_MIN_RUN_LENGTH = 1.0f;

    /// <summary>
    /// Deck sink below nominal platform height (PLATFORM_SURFACE_ABOVE_SPLINE), m.
    /// Edge-part top details (yellow lines/tactile paving) sit at nominal height;
    /// lower deck slightly to prevent Z-fighting (tactile blocks appearing 2cm raised match reality).
    /// </summary>
    private const float PLATFORM_DECK_SINK = 0.02f;

    /// <summary>Minimum deck width (m), preventing slivers in clamped sections.</summary>
    private const float PLATFORM_DECK_MIN_WIDTH = 0.05f;

    /// <summary>Minimum continuous deck length (m).</summary>
    private const float PLATFORM_DECK_MIN_RUN_LENGTH = 1.0f;

    /// <summary>
    /// Deck thickness (m). Suspended slabs do not extend to ground;
    /// platform edge geometry supplies support posts/rise.
    /// </summary>
    private const float PLATFORM_DECK_THICKNESS = 0.15f;

    // Tracks lack sidewalks; accept sidewalk setbacks for interface compatibility but do not use them.
    public PathMeshResult Generate(PathSummary pathData, List<Vector3> points, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default)
    {
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[TrackPath] Generate開始: path_id={pathData?.path_id ?? -1}, path_variant={pathData?.path_variant ?? -1}, path_type={pathData?.path_type ?? -1}, points={points?.Count ?? 0}");

        if (points == null || points.Count < 2)
        {
            Debug.LogWarning($"[TrackPath] ポイント不足 ({points?.Count ?? 0}件)。生成スキップ。");
            return new PathMeshResult(null, false);
        }

        GetTrackMasterParams(pathData, out float width, out int lanes, out float wallWidth, out float? pillarSpacing, out bool heightEditable, out float? wireSpacing, out float baseHeight, out float platformWidth);

        // Left/right platform extensions (zero for absent sides/nonplatform variants).
        ResolvePlatformExtents(pathData, platformWidth, out float platformLeft, out float platformRight);

        bool isCurved = pathData.curved;

        // With curved=False, CalculatePathPoints returns anchors only, preventing terrain conformity.
        // Interpolate intermediate points at regular intervals and apply terrain height.
        // Preserve waypoint corners as Broken points (no road-style corner cutting/filling;
        // tracks normally use smooth curved=true; polyline corners are simplified rendering).
        // PathManager already applies design elevations to station_grade_profile tracks; do not reconform to terrain.
        if (!isCurved && (pathData.station_grade_profile == null || pathData.station_grade_profile.Count < 2)
            && (points.Count == 2 || (pathData.GetWaypointList()?.Count ?? 0) > 0))
        {
            bool followTerrain = PathManager.GetPathFollowTerrain(pathData)
                && AreAllAnchorsOnGround(pathData);
            points = PathManager.SubdividePointsWithTerrain(points, PathManager.TERRAIN_SAMPLE_INTERVAL, followTerrain);
        }

        // Nearby parallel-track corridor joins and station-platform
        // joining/encroachment information. Null without joins/provider (test scenes, etc.),
        // retaining standalone rendering (platform decks at own width).
        // Compute uses platformLeft/platformRight to suppress platform-side bed joins
        // (ballast infill, etc.) and detect island-platform joins.
        // S curves for straight joins to differing track positions (TrackEndTransition). Null keeps original positions throughout.
        TrackLateralSampler lateral = ResolveLateral(pathData, points);
        var corridor = ResolveCorridor(pathData, points, width, wallWidth, platformLeft, platformRight, lateral);

        GameObject container = new GameObject($"Track_{pathData.path_id}");
        SplineComputer spline = RoadPath.BuildSpline(container, points, isCurved);
        // Derive repeated bed/wire/wall tile length from curvature (track/platform retain fixed authored lengths).
        PathDrawContext.SetAdaptiveTileLength(PathDrawContext.ComputeAdaptiveTileLength(spline, RoadPath.MAX_EXTRUDE_TILE_LENGTH));

        activeLateral = lateral;
        try
        {
            CreateTrackParts(container, spline, points, width, lanes, wallWidth, pillarSpacing, heightEditable, baseHeight, wireSpacing, pathData, corridor, platformLeft, platformRight);
        }
        finally
        {
            activeLateral = null;
        }

        // Add direction-arrow overlays to one-way tracks (nothing for bidirectional).
        // Visible only in road-placement mode (STATE_EDIT_PATH), controlled collectively by TrackDirectionArrows.
        TrackDirectionArrows.Create(container, spline, pathData.travel_direction);

        // Picking collider uses a strip, like roads. Include platforms in full width,
        // shifting center toward platform sides for asymmetry.
        float colliderWidth = width + platformLeft + platformRight;
        float colliderOffset = (platformRight - platformLeft) * 0.5f;
        Mesh colliderMesh = RoadPath.BuildColliderMeshFromSpline(spline, colliderWidth, colliderOffset);
        if (colliderMesh != null)
        {
            colliderMesh.name = $"TrackCollider_{pathData.path_id}";
        }

        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[TrackPath] Generate完了: 子オブジェクト数={container.transform.childCount}, colliderMesh={(colliderMesh != null ? "あり" : "なし")}");
        return new PathMeshResult(colliderMesh, true, container);
    }

    /// <summary>
    /// Whether start/end/all waypoint relative Ys are zero (ground-following).
    /// Determines terrain-conformity subdivision.
    /// </summary>
    private static bool AreAllAnchorsOnGround(PathSummary pathData)
    {
        var startList = pathData.GetStartPointList();
        var endList = pathData.GetEndPointList();
        float startRelY = (startList != null && startList.Count >= 3) ? startList[1] : 0f;
        float endRelY = (endList != null && endList.Count >= 3) ? endList[1] : 0f;
        if (startRelY != 0f || endRelY != 0f) return false;

        var waypointList = pathData.GetWaypointList();
        if (waypointList != null)
        {
            foreach (var wp in waypointList)
            {
                float relY = (wp.position != null && wp.position.Length >= 3) ? wp.position[1] : 0f;
                if (relY != 0f) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Resolve left/right platform extensions (m) from platform_side (inside/outside/both), city traffic side
    /// via CityInfoStore.TrafficHand, and track travel_direction.
    /// Sides follow spline start→end (right=Cross(up, forward)).
    ///
    /// Paths with at least two tracks (combined double-track stations) have both exterior platforms regardless of platform_side.
    /// Island stations (IsIslandPlatform) instead occupy the central gap, extending neither side (both zero).
    ///
    /// For single tracks, inside means toward the opposing track of a two-single-track pair;
    /// with left-hand traffic, inside=travel-right; with right-hand traffic, inside=travel-left.
    /// Selecting inside for both directions creates facing platforms forming an island.
    ///
    /// Inside/outside depend on travel direction; map travel-frame sides to geometric
    /// spline frame. DIR_BACKWARD runs end→start, reversing sides
    /// (up/down tracks may share geometric direction but oppose travel; without reversal,
    /// both platforms would appear on the same side). For bidirectional DIR_BOTH single track,
    /// placement direction has no operational meaning; use geometric direction as travel direction.
    ///
    /// Mirrors backend site-polygon calculation
    /// geo_paths.get_platform_extension_widths. Changing only one side
    /// misaligns site (land occupancy/picking) and appearance. Resolve track count from path_variant master.
    /// </summary>
    public static void ResolvePlatformExtents(PathSummary pathData, float platformWidth, out float leftWidth, out float rightWidth)
    {
        leftWidth = 0f;
        rightWidth = 0f;
        if (platformWidth <= 0f || pathData == null) return;

        int side = pathData.GetPlatformSide();
        var master = MasterDataStore.Instance?.GetPathMaster(pathData.path_variant);
        if (IsIslandPlatform(master)) return;
        int lanes = master?.lanes ?? 1;
        if (side == Constants.PLATFORM_SIDE_BOTH || lanes >= 2)
        {
            leftWidth = platformWidth;
            rightWidth = platformWidth;
            return;
        }

        bool insideIsRight = CityInfoStore.TrafficHand == Constants.TRAFFIC_HAND_LEFT;
        bool platformIsRight = (side == Constants.PLATFORM_SIDE_INSIDE) == insideIsRight;
        if (pathData.travel_direction == Constants.DIR_BACKWARD) platformIsRight = !platformIsRight;
        if (platformIsRight) rightWidth = platformWidth;
        else leftWidth = platformWidth;
    }

    /// <summary>
    /// Return track-index center offset (lateral distance from spline centerline).
    /// Like road lanes, subtract central gap (centerWidth, master center_width) from full width,
    /// divide remaining width by track count, and place tracks at band centers. Central gap lies between equal track halves,
    /// applying only for even counts (island stations place platforms here).
    /// Examples: single (width5, lanes1)=0; double (width10, lanes2)=±2.5; island double (width16, lanes2, center6)=±5.5.
    /// </summary>
    public static float GetTrackCenterOffset(float width, int lanes, int trackIndex, float centerWidth = 0f)
    {
        int laneCount = Mathf.Max(1, lanes);
        if (centerWidth <= 0f || centerWidth >= width || laneCount % 2 != 0)
        {
            return -width * 0.5f + width / laneCount * (trackIndex + 0.5f);
        }
        int half = laneCount / 2;
        float laneWidth = (width - centerWidth) / laneCount;
        return trackIndex < half
            ? -width * 0.5f + laneWidth * (trackIndex + 0.5f)
            : centerWidth * 0.5f + laneWidth * (trackIndex - half + 0.5f);
    }

    /// <summary>Variant central gap (master center_width), determining track laterals/island width.</summary>
    public static float GetTrackCenterWidth(PathSummary pathData)
    {
        if (pathData == null || MasterDataStore.Instance == null) return 0f;
        return MasterDataStore.Instance.GetPathMaster(pathData.path_variant)?.center_width ?? 0f;
    }

    /// <summary>
    /// Whether this is an island station: station variants (platform_width) with at least two tracks and central
    /// center_width gap, holding one platform without outer extensions.
    /// Same definition as backend MasterData.is_island_platform_variant.
    /// </summary>
    public static bool IsIslandPlatform(PathMasterItem master)
    {
        return master != null && (master.platform_width ?? 0f) > 0f
            && (master.lanes ?? 1) >= 2 && (master.center_width ?? 0f) > 0f;
    }

    /// <summary>Whether this variant is an island-platform station.</summary>
    public static bool IsIslandPlatform(PathSummary pathData)
    {
        return pathData != null && MasterDataStore.Instance != null
            && IsIslandPlatform(MasterDataStore.Instance.GetPathMaster(pathData.path_variant));
    }

    /// <summary>Island-edge centerline distance (deck spans ±this), PLATFORM_EDGE_OFFSET inward from inner track centers.</summary>
    public static float IslandEdgeDistance(float width, int lanes, float centerWidth)
    {
        return Mathf.Abs(GetTrackCenterOffset(width, lanes, Mathf.Max(1, lanes) / 2, centerWidth)) - PLATFORM_EDGE_OFFSET;
    }

    /// <summary>
    /// Track index used for travel (GetTrackCenterOffset index, 0=geometric leftmost).
    /// forward means geometric start→end. Single track uses its only track; multiple tracks use the outermost
    /// on the traffic-hand side relative to travel (left for left-hand traffic). Path/direction uniquely determine track;
    /// intersection rail connectors/buffer stops use this rule to select ends.
    /// Vehicle rendering (Vehicles/TransitRouteGeometry.SampleTrack) uses the same rule.
    /// </summary>
    public static int TrackIndexForTravel(int trackCount, bool forward)
    {
        if (trackCount <= 1) return 0;
        bool keepLeft = CityInfoStore.TrafficHand == Constants.TRAFFIC_HAND_LEFT;
        return keepLeft == forward ? 0 : trackCount - 1;
    }

    /// <summary>
    /// Geometric signed center lateral of the track facing this platform side (outermost on that side).
    /// Place platform edges/outer walls/foundations/end walls at this track center,
    /// allowing single-track right-side parts to work unchanged at combined double-track stations. Zero for single track.
    /// </summary>
    public static float PlatformTrackOffset(float width, int lanes, bool leftSide, float centerWidth = 0f)
    {
        return GetTrackCenterOffset(width, lanes, leftSide ? 0 : Mathf.Max(1, lanes) - 1, centerWidth);
    }

    /// <summary>
    /// Centerline distance of exterior platform track-side edge.
    /// PLATFORM_EDGE_OFFSET outside the facing track center; equal on both sides. Single track uses PLATFORM_EDGE_OFFSET.
    /// Island edges use IslandEdgeDistance.
    /// </summary>
    public static float PlatformEdgeDistance(float width, int lanes, float centerWidth = 0f)
    {
        return Mathf.Abs(PlatformTrackOffset(width, lanes, false, centerWidth)) + PLATFORM_EDGE_OFFSET;
    }

    private static TrackCorridorInfo ResolveCorridor(PathSummary pathData, List<Vector3> points,
        float width, float wallWidth, float platformLeft, float platformRight, TrackLateralSampler lateral)
    {
        TrackCorridorInfo corridor = null;
        if (TrackAdjacency.NeighborProvider != null)
        {
            List<TrackNeighborInfo> trackNeighbors = TrackAdjacency.NeighborProvider(pathData);
            if (trackNeighbors != null && trackNeighbors.Count > 0)
            {
                corridor = TrackAdjacency.ComputeWithShifts(pathData.path_id, points, width, wallWidth, platformLeft, platformRight, trackNeighbors,
                    LrtRoadParts.IsLrt(pathData.path_variant) ? LrtRoadParts.PlatformOuterOffset(pathData) : 0f, lateral?.OuterShiftsAtPoints(),
                    TrackAdjacency.IsProvisional(pathData));
            }
        }
        return corridor;
    }

    /// <summary>Prepare reading track laterals on rendering points. Null without S curves/provider.</summary>
    private static TrackLateralSampler ResolveLateral(PathSummary pathData, List<Vector3> points)
    {
        if (LateralProfileProvider == null || points == null || points.Count < 2) return null;
        TrackLateralProfile profile = LateralProfileProvider(pathData);
        return profile == null || profile.IsUniform ? null : new TrackLateralSampler(profile, pathData, points);
    }

    /// <summary>Shift of current path's track at point index from original position (zero without S curves).</summary>
    private static float ActiveShift(int track, int index)
    {
        if (activeLateral == null || track < 0 || track >= activeLateral.Profile.TrackCount) return 0f;
        return activeLateral.Shift(track, activeLateral.AlongAt(index));
    }

    /// <summary>Track index at current path lateral trackOffset (-1 without S curves).</summary>
    private static int ActiveTrackIndex(float trackOffset)
    {
        if (activeLateral == null) return -1;
        float[] laterals = activeLateral.Profile.Base;
        int best = 0;
        for (int i = 1; i < laterals.Length; i++)
            if (Mathf.Abs(laterals[i] - trackOffset) < Mathf.Abs(laterals[best] - trackOffset)) best = i;
        return best;
    }

    /// <summary>Set next-part deformation (PathDrawContext.Warp), call build, and always restore.</summary>
    private static void WithWarp(PartWarp warp, System.Action build)
    {
        PathDrawContext.Warp = warp;
        try
        {
            build();
        }
        finally
        {
            PathDrawContext.Warp = null;
        }
    }

    /// <summary>
    /// Build a spline through track-center points (path centerline shifted per point along horizontal right by track lateral).
    /// Keeps sleepers perpendicular to tracks in S curves. Match path-centerline spline type.
    /// </summary>
    private static SplineComputer BuildTrackSpline(GameObject container, SplineComputer centerSpline, List<Vector3> points, TrackLateralSampler lateral, int track, out List<Vector3> trackPoints)
    {
        ComputeStripFrames(points, out Vector3[] rights, out _);
        trackPoints = new List<Vector3>(points.Count);
        for (int k = 0; k < points.Count; k++) trackPoints.Add(points[k] + rights[k] * lateral.Lateral(track, lateral.AlongAt(k)));
        var holder = new GameObject($"TrackSpline_{track}");
        holder.transform.SetParent(container.transform, false);
        return RoadPath.BuildSpline(holder, trackPoints, centerSpline.type == Spline.Type.Bezier);
    }

    /// <summary>
    /// Map path-centerline spline intervals to track-spline fractions. Both derive from identical point ordering;
    /// use corresponding cumulative-distance ratios.
    /// </summary>
    private static TrackCorridorInfo.PercentRange MapRangeToTrack(TrackCorridorInfo.PercentRange range, List<Vector3> centerPoints, List<Vector3> trackPoints)
    {
        return new TrackCorridorInfo.PercentRange { From = MapPercent(range.From, centerPoints, trackPoints), To = MapPercent(range.To, centerPoints, trackPoints) };
    }

    private static double MapPercent(double percent, List<Vector3> from, List<Vector3> to)
    {
        if (percent <= 0.0 || percent >= 1.0 || from.Count != to.Count || from.Count < 2) return percent;
        float totalFrom = 0f, totalTo = 0f;
        for (int k = 1; k < from.Count; k++)
        {
            totalFrom += Vector3.Distance(from[k - 1], from[k]);
            totalTo += Vector3.Distance(to[k - 1], to[k]);
        }
        if (totalFrom <= 0f || totalTo <= 0f) return percent;
        float target = (float)percent * totalFrom, along = 0f, alongTo = 0f;
        for (int k = 1; k < from.Count; k++)
        {
            float segment = Vector3.Distance(from[k - 1], from[k]);
            float segmentTo = Vector3.Distance(to[k - 1], to[k]);
            if (along + segment >= target)
            {
                float t = segment > 1e-6f ? (target - along) / segment : 0f;
                return (alongTo + segmentTo * t) / totalTo;
            }
            along += segment;
            alongTo += segmentTo;
        }
        return percent;
    }

    /// <summary>Completed-section beds/tracks/parapets. Elevated tests/neighbor-side wall omission match completed paths.</summary>
    public static void CreateConstructionParts(GameObject container, SplineComputer spline,
        List<Vector3> points, PathSummary pathData)
    {
        GetTrackMasterParams(pathData, out float width, out int lanes, out float wallWidth,
            out float? pillarSpacing, out bool heightEditable, out float? wireSpacing,
            out float baseHeight, out float platformWidth);
        ResolvePlatformExtents(pathData, platformWidth, out float platformLeft, out float platformRight);
        TrackLateralSampler lateral = ResolveLateral(pathData, points);
        var corridor = ResolveCorridor(pathData, points, width, wallWidth, platformLeft, platformRight, lateral);
        activeLateral = lateral;
        try
        {
            CreateTrackParts(container, spline, points, width, lanes, wallWidth, pillarSpacing,
                heightEditable, baseHeight, wireSpacing, pathData, corridor, platformLeft, platformRight,
                constructionOnly: true);
        }
        finally
        {
            activeLateral = null;
        }
    }

    /// <summary>
    /// Place track parts (bed/tracks/wires/poles/walls/piers/platforms) along splines.
    /// With optional corridor, joined sections fill bed gaps,
    /// omit walls, span poles/beams across full width,
    /// and join island platforms (deck infill/platform_wall omission).
    /// points matches spline control points (used for dynamic deck generation).
    /// platformLeft/platformRight are station-platform extensions
    /// resolved by ResolvePlatformExtents (zero=absent).
    /// </summary>
    private static void CreateTrackParts(GameObject container, SplineComputer spline, List<Vector3> points, float width, int lanes, float wallWidth, float? pillarSpacing, bool heightEditable, float baseHeight, float? wireSpacing, PathSummary pathData, TrackCorridorInfo corridor = null, float platformLeft = 0f, float platformRight = 0f, bool constructionOnly = false)
    {
        string variantBase = $"Models/Paths/p{pathData.path_variant}";
        string typeBase = $"Models/Paths/p{pathData.path_type}";
        int laneCount = Mathf.Max(1, lanes);
        float centerWidth = GetTrackCenterWidth(pathData);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[TrackPath] CreateTrackParts開始: variant={variantBase}, type={typeBase}, width={width}, lanes={laneCount}, center={centerWidth}, wallWidth={wallWidth}, wireSpacing={wireSpacing}");

        // Surface/elevated/underground status follows anchor heights, not variants; select girder/slab-track/parapet
        // rendering by above-terrain percent intervals. Surface sections (including cuttings) use standard parts;
        // elevated sections use `{part}_elevated`, falling back to standard.
        List<TrackCorridorInfo.PercentRange> elevatedRanges = GetElevatedPercentRanges(points, baseHeight);
        List<TrackCorridorInfo.PercentRange> groundRanges = ComplementRanges(elevatedRanges);

        // S joins (TrackEndTransition). Exterior parts shift by outer-track displacement; beds/girders/arches resize accordingly.
        // Symmetric shifts keep bed centers fixed.
        TrackLateralSampler lateral = activeLateral;
        PartWarp bodyWarp = lateral != null ? new PartWarp { Scale = percent => 1f + 2f * lateral.OuterShiftAtPercent(percent) / width } : null;

        // Bed (body): stretch to full width. Height uses the road-body system,
        // coplanar with intersection bed hulls (reference + BODY_ABOVE_SPLINE).
        // body_elevated skips body top rebasing/top-width fitting; author top at origin height and full-section width at path width.
        GameObject ballast = null;
        WithWarp(bodyWarp, () =>
        {
            foreach (var range in groundRanges)
            {
                GameObject part = RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/body", width, 0f, RoadPartHeights.BODY_ABOVE_SPLINE, false, $"{typeBase}/body", clipFrom: range.From, clipTo: range.To);
                if (ballast == null) ballast = part;
            }
            foreach (var range in elevatedRanges)
            {
                GameObject part = RoadPath.CreateSplineMesh(container, spline, ElevatedPartName(variantBase, "body"), width, 0f, RoadPartHeights.BODY_ABOVE_SPLINE, false, $"{typeBase}/body", clipFrom: range.From, clipTo: range.To);
                if (ballast == null) ballast = part;
            }
            if (!constructionOnly) BrickArcadeGenerator.Generate(container, spline, pathData, width);
        });
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[TrackPath] body(道床): {(ballast != null ? "生成成功" : "プレハブなし")} 高架区間={elevatedRanges.Count}");

        // Fill neighboring bed gaps with ballast strips in joined sections for continuous appearance.
        // Variants without bed prefabs (monorail, etc.) have no bed or infill.
        if (corridor != null && ballast != null)
        {
            MeshRenderer ballastRenderer = ballast.GetComponent<MeshRenderer>();
            Material bodyMaterial = ballastRenderer != null ? ballastRenderer.sharedMaterial : null;
            CreateBodyGapFillers(container, corridor, bodyMaterial);
        }

        // Track: actual-size placement at each track center (stretchToWidth=false preserves gauge).
        // Preserve sleeper thickness/period with authored-length tiles, not adaptive road tiling (maxTileLength: 0).
        // With S curves, place each track on its shifted spline for perpendicular sleepers; wires share it.
        var trackSplines = new SplineComputer[laneCount];
        int placedTracks = 0;
        for (int i = 0; i < laneCount; i++)
        {
            float offset = GetTrackCenterOffset(width, laneCount, i, centerWidth);
            SplineComputer trackSpline = spline;
            List<Vector3> trackPoints = null;
            if (lateral != null)
            {
                trackSpline = BuildTrackSpline(container, spline, points, lateral, i, out trackPoints);
                offset = 0f;
            }
            trackSplines[i] = trackSpline;
            foreach (var range in groundRanges)
            {
                var clip = trackPoints != null ? MapRangeToTrack(range, points, trackPoints) : range;
                GameObject track = RoadPath.CreateSplineMesh(container, trackSpline, $"{variantBase}/track", width / laneCount, offset, RoadPartHeights.TRACK_ABOVE_SPLINE, false, $"{typeBase}/track", isDashed: false, stretchToWidth: false, clipFrom: clip.From, clipTo: clip.To, maxTileLength: 0f);
                if (track != null) placedTracks++;
            }
            foreach (var range in elevatedRanges)
            {
                var clip = trackPoints != null ? MapRangeToTrack(range, points, trackPoints) : range;
                GameObject track = RoadPath.CreateSplineMesh(container, trackSpline, ElevatedPartName(variantBase, "track"), width / laneCount, offset, RoadPartHeights.TRACK_ABOVE_SPLINE, false, $"{typeBase}/track", isDashed: false, stretchToWidth: false, clipFrom: clip.From, clipTo: clip.To, maxTileLength: 0f);
                if (track != null) placedTracks++;
            }
        }
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[TrackPath] track(軌道): {placedTracks}/{laneCount}本配置");
        if (!constructionOnly) CreateTerminations(container, points, corridor, pathData, width, laneCount, platformLeft, platformRight, variantBase, typeBase);

        // LRT-stop platforms (431). In platform intervals (ramps+margin), move poles outside and omit elevated parapets.
        // Omit intervals overlapped by other beds on each side (pole/parapet intervals use decks before subtraction).
        List<LrtPlatformParts.Span> lrtPlatforms = constructionOnly ? null : LrtPlatformParts.Resolve(pathData, points, spline.CalculateLength());
        LrtPlatformParts.Generate(container, spline, pathData, lrtPlatforms,
            corridor?.GetLrtPlatformBlockedRanges(leftSide: true), corridor?.GetLrtPlatformBlockedRanges(leftSide: false));
        List<Vector2> lrtPlatformZones = LrtPlatformParts.ZoneRanges(lrtPlatforms,
            LrtRoadParts.PlatformRampLength(pathData.path_variant), LrtRoadParts.PLATFORM_TRACK_ZONE_MARGIN);

        // Variants lacking both bed and track fall back to white strips to preserve gameplay.
        // Track-only models (monorail, etc.) legitimately omit beds.
        if (ballast == null && placedTracks == 0)
        {
            RoadPath.CreateFallbackRoadBody(container, spline, width);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[TrackPath] 道床・軌道ともにモデルなし。白いストリップにフォールバック");
        }

        // Platforms (station variants with platform_width only).
        // Three elements: platform edge, procedural deck, and platform_wall outer edge
        // (see class comments). platform_width defines extension; deck height follows
        // RoadPartHeights.PLATFORM_SURFACE_ABOVE_SPLINE. Edge/outer prefabs use
        // origin=facing track centerline/track surface, with rise supplied by geometry.
        // Island stations have no exterior extension (platformLeft/Right=0); place two edges/one deck between tracks.
        if (!constructionOnly && (platformLeft > 0f || platformRight > 0f))
        {
            CreatePlatformParts(container, spline, points, corridor, width, laneCount, centerWidth, platformLeft, platformRight, variantBase, typeBase, pathData);
        }
        else if (!constructionOnly && IsIslandPlatform(pathData))
        {
            CreateIslandPlatformParts(container, spline, points, corridor, width, laneCount, centerWidth, variantBase, typeBase, pathData);
        }

        // Overhead system (electrified variants with wire_spacing only).
        if (!constructionOnly && wireSpacing.HasValue && wireSpacing.Value > 0f)
        {
            for (int i = 0; i < laneCount; i++)
            {
                float offset = lateral != null ? 0f : GetTrackCenterOffset(width, laneCount, i, centerWidth);
                RoadPath.CreateSplineMesh(container, trackSplines[i], $"{variantBase}/wire", width / laneCount, offset, RoadPartHeights.OVERHEAD_ABOVE_SPLINE, false, $"{typeBase}/wire", isDashed: false, stretchToWidth: false);
            }
            float lrtPoleOffset = lrtPlatformZones.Count > 0
                ? LrtRoadParts.PlatformOuterOffset(pathData) + LrtRoadParts.PLATFORM_POLE_CLEARANCE : 0f;
            GeneratePolesAndBeams(container, spline, wireSpacing.Value, width, variantBase, typeBase, corridor, platformLeft, platformRight, lrtPlatformZones, lrtPoleOffset, TunnelSectionUtil.GetClearanceHeight(pathData.path_variant));
        }
        else
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[TrackPath] wire_spacing未設定: 非電化（架線・架線柱なし）");
        }

        // Parapets (wall): elevated sections only, outside beds using road right-side convention (X-mirror left).
        // Clip joined sides to avoid walls between neighboring tracks;
        // restore walls where branching tracks separate.
        // Omit walls on platform sides to avoid blocking boarding space;
        // platform_wall handles outer walls/fences. Nonplatform sides retain normal walls.
        // LRT platforms exist on both sides; omit both walls in platform intervals (platform fences handle them).
        if (wallWidth > 0f && elevatedRanges.Count > 0)
        {
            float wallCenterOffset = (width * 0.5f) + (wallWidth * 0.5f);
            List<TrackCorridorInfo.PercentRange> wallRanges = IntersectRanges(elevatedRanges, LrtPlatformParts.OutsidePercentRanges(spline, lrtPlatformZones));
            if (platformLeft <= 0f)
            {
                WithWarp(lateral != null ? new PartWarp { Shift = percent => -lateral.OuterShiftAtPercent(percent) } : null, () =>
                {
                    foreach (var range in IntersectRanges(GetWallPercentRanges(corridor, leftSide: true), wallRanges))
                    {
                        RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/wall", wallWidth, -wallCenterOffset, RoadPartHeights.WALL_ABOVE_SPLINE, true, $"{typeBase}/wall", clipFrom: range.From, clipTo: range.To);
                    }
                });
            }
            if (platformRight <= 0f)
            {
                WithWarp(lateral != null ? new PartWarp { Shift = percent => lateral.OuterShiftAtPercent(percent) } : null, () =>
                {
                    foreach (var range in IntersectRanges(GetWallPercentRanges(corridor, leftSide: false), wallRanges))
                    {
                        RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/wall", wallWidth, wallCenterOffset, RoadPartHeights.WALL_ABOVE_SPLINE, false, $"{typeBase}/wall", clipFrom: range.From, clipTo: range.To);
                    }
                });
            }
        }

        // Underground walls lie outside passage space including platforms, independent of parapet width/platform-side presence.
        var tunnels = PathTunnelParts.Ranges(spline, pathData);
        float lrtOuter = lrtPlatforms != null && lrtPlatforms.Count > 0 ? LrtRoadParts.PlatformOuterOffset(pathData) : 0f;
        WithWarp(lateral != null ? new PartWarp { Shift = percent => -lateral.OuterShiftAtPercent(percent) } : null,
            () => PathTunnelParts.GenerateWall(container, spline, pathData, Mathf.Max(width * .5f + platformLeft, lrtOuter),
                wallWidth, true, tunnels, GetWallPercentRanges(corridor, true)));
        WithWarp(lateral != null ? new PartWarp { Shift = percent => lateral.OuterShiftAtPercent(percent) } : null,
            () => PathTunnelParts.GenerateWall(container, spline, pathData, Mathf.Max(width * .5f + platformRight, lrtOuter),
                wallWidth, false, tunnels, GetWallPercentRanges(corridor, false)));
        float ceilingLeft = Mathf.Max(width * .5f + platformLeft, lrtOuter);
        float ceilingRight = Mathf.Max(width * .5f + platformRight, lrtOuter);
        WithWarp(lateral != null ? new PartWarp { Scale = percent => 1f + 2f * lateral.OuterShiftAtPercent(percent) / (ceilingLeft + ceilingRight) } : null,
            () => PathTunnelParts.GenerateCeiling(container, spline, pathData, ceilingLeft, ceilingRight, tunnels));

        // Elevated piers share road logic. Omit underground (see RoadPath rationale).
        if (!constructionOnly && heightEditable && pillarSpacing.HasValue && pillarSpacing.Value > 0f
            && !TunnelSectionUtil.IsUndergroundPath(pathData))
        {
            // Only one of directly joined paths places endpoint piers at plate-free straight joins.
            RoadEndJoin startJoin = RoadAdjacency.EndJoin(pathData, true), endJoin = RoadAdjacency.EndJoin(pathData, false);
            WithWarp(bodyWarp, () => RoadPath.GeneratePillars(container, spline, pillarSpacing.Value, baseHeight, pathData, variantBase, typeBase,
                startJoin.Continuous && !startJoin.OwnsSupport, endJoin.Continuous && !endJoin.OwnsSupport));
        }
    }

    /// <summary>
    /// Resolve ends per connection node. Mere parallel proximity does not affect buffer stops.
    /// Place stops only on track ends unused by any node rail. Find used ends through
    /// TrackIntersectionMeshGenerator.ResolveConnectedTrackEnds, the same direction-dependent rules as intersection rail connectors.
    /// Close platform ends if no same-side platform continues (island-deck owner uses actual width).
    /// Island stations continue only into island stations; otherwise close edge-to-edge.
    /// </summary>
    private static void CreateTerminations(GameObject container, List<Vector3> points, TrackCorridorInfo corridor,
        PathSummary path, float width, int lanes, float leftWidth, float rightWidth, string variantBase, string typeBase)
    {
        if (path.path_type != Constants.PATH_TRACK && path.path_type != Constants.PATH_MONORAIL) return;
        ComputeStripFrames(points, out var rights, out var lengths);
        float centerWidth = GetTrackCenterWidth(path);
        float platformEdge = PlatformEdgeDistance(width, lanes, centerWidth);
        bool island = IsIslandPlatform(path);
        for (int end = 0; end < 2; end++)
        {
            int idx = end == 0 ? 0 : points.Count - 1;
            int nodeId = end == 0 ? path.start_node_id : path.end_node_id;
            var connections = ConnectedPathsProvider?.Invoke(nodeId);
            bool facilityContinues = InfrastructureConnectionProvider?.Invoke(nodeId) == true;
            bool leftContinues = false, rightContinues = false, islandContinues = false;
            // Tracks with incompatible rails cannot determine whether own track ends are used.
            var nodePaths = new List<PathSummary> { path };
            if (connections != null)
                foreach (var other in connections)
                {
                    if (other.path_id == path.path_id
                        || !TrackIntersectionMeshGenerator.RailsConnect(path.path_type, path.path_variant, other.path_type, other.path_variant)) continue;
                    nodePaths.Add(other);
                    GetTrackMasterParams(other, out _, out _, out _, out _, out _, out _, out _, out float pfWidth);
                    ResolvePlatformExtents(other, pfWidth, out float otherLeft, out float otherRight);
                    // Same-end connections reverse geometry direction; swap sides.
                    bool reverse = (end == 0) == (other.start_node_id == nodeId);
                    leftContinues |= (reverse ? otherRight : otherLeft) > 0f;
                    rightContinues |= (reverse ? otherLeft : otherRight) > 0f;
                    islandContinues |= IsIslandPlatform(other);
                }
            Vector3 forward = Vector3.Cross(rights[idx], Vector3.up);
            Vector3 inward = end == 0 ? forward : -forward;
            var anchor = end == 0 ? path.GetStartPointList() : path.GetEndPointList();
            bool atAnchor = anchor == null || anchor.Count < 2 ||
                Vector2.Distance(new Vector2(points[idx].x, points[idx].z),
                    new Vector2(anchor[0], anchor.Count >= 3 ? anchor[2] : anchor[1])) < .5f;
            // Do not assume unloaded/temporary placement nodes are termini.
            // Tunnel-portal rendering-section ends differ from actual track ends.
            // Shared infrastructure/depot terminals continue tracks inside facilities; omit stops.
            if (path.path_type == Constants.PATH_TRACK && atAnchor && connections != null && connections.Count > 0 && nodeId > 0 && !facilityContinues && lengths[points.Count - 1] >= 3f)
            {
                var usedEnds = TrackIntersectionMeshGenerator.ResolveConnectedTrackEnds(nodeId, nodePaths);
                // Author buffer_stop origin at track center/sleeper underside, +Z toward approaching trains (track interior).
                for (int lane = 0; lane < lanes; lane++)
                {
                    if (usedEnds.Contains((path.path_id, lane))) continue;
                    PlaceEndPart(container, LoadPrefab($"{variantBase}/buffer_stop", $"{typeBase}/buffer_stop"),
                        "TrackBufferStop", points[idx] + inward * 1.0f + rights[idx] * (GetTrackCenterOffset(width, lanes, lane, centerWidth) + ActiveShift(lane, idx))
                            + Vector3.up * RoadPartHeights.TRACK_ABOVE_SPLINE,
                        Quaternion.LookRotation(inward), Vector3.one);
                }
            }
            if (leftWidth <= 0f && rightWidth <= 0f && (!island || islandContinues)) continue;
            var prefab = LoadPrefab($"{variantBase}/platform_end", $"{typeBase}/platform_end");
            // Monorails share platform height; use railway end walls if dedicated parts are absent.
            if (prefab == null && path.path_type == Constants.PATH_MONORAIL)
                prefab = LoadPrefab($"Models/Paths/p{Constants.PATH_TRACK}/platform_end", null);
            if (island && !islandContinues)
            {
                float edge = IslandEdgeDistance(width, lanes, centerWidth);
                float from = -edge + ActiveShift(lanes / 2 - 1, idx), to = edge + ActiveShift(lanes / 2, idx);
                if (to - from >= PLATFORM_DECK_MIN_WIDTH) PlacePlatformEndPanels(container, prefab, points[idx], rights[idx], inward, from, to);
            }
            for (int side = 0; side < 2; side++)
            {
                bool left = side == 0;
                float pf = left ? leftWidth : rightWidth;
                if (pf <= 0f || (left ? leftContinues : rightContinues)) continue;
                // S curves move edges/default outer boundaries outward. Corridor DeckFar already includes shifts.
                float outward = left ? -ActiveShift(0, idx) : ActiveShift(lanes - 1, idx);
                float[] farValues = corridor == null ? null : (left ? corridor.LeftDeckFar : corridor.RightDeckFar);
                float far = farValues == null ? width * .5f + pf + outward : farValues[idx];
                float near = platformEdge + outward;
                if (float.IsNaN(far) || far <= near + PLATFORM_DECK_MIN_WIDTH) continue;
                if (left) PlacePlatformEndPanels(container, prefab, points[idx], rights[idx], inward, -far, -near);
                else PlacePlatformEndPanels(container, prefab, points[idx], rights[idx], inward, near, far);
            }
        }
    }

    /// <summary>
    /// Divide platform end walls over lateral from–to (signed centerline distances) into panels ≤1.2m.
    /// Place 12cm inside the end at deck-top height; scale only laterally to panel width.
    /// Lateral scale directly equals width, so author 1m-wide parts with origin at lateral center/deck top.
    /// </summary>
    private static void PlacePlatformEndPanels(GameObject container, GameObject prefab, Vector3 point, Vector3 right, Vector3 inward, float from, float to)
    {
        float span = to - from;
        int panels = Mathf.Max(1, Mathf.CeilToInt(span / 1.2f));
        for (int panel = 0; panel < panels; panel++)
        {
            float lateral = from + span * (panel + .5f) / panels;
            PlaceEndPart(container, prefab, "TrackPlatformEnd", point + right * lateral
                + inward * .12f + Vector3.up * (RoadPartHeights.PLATFORM_SURFACE_ABOVE_SPLINE - PLATFORM_DECK_SINK),
                Quaternion.LookRotation(inward), new Vector3(span / panels, 1f, 1f));
        }
    }

    private static void PlaceEndPart(GameObject parent, GameObject prefab, string name, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        if (prefab == null) return;
        var part = UnityEngine.Object.Instantiate(prefab, position, rotation, parent.transform);
        part.name = name;
        part.transform.localScale = scale;
        foreach (var renderer in part.GetComponentsInChildren<MeshRenderer>())
        {
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++) materials[i] = RoadPath.GetOrCreateRoadMaterial(materials[i]);
            renderer.sharedMaterials = materials;
        }
    }

    /// <summary>
    /// Generate full station platforms (edges/decks/outer edges) on each side.
    /// Each part uses its facing track centerline (PlatformTrackOffset) as origin.
    /// Read deck material from platform_panel (material reference only),
    /// falling back to shared concrete if absent. Platforms define station visibility,
    /// so unlike optional parts, missing assets do not mean omission.
    /// </summary>
    private static void CreatePlatformParts(GameObject container, SplineComputer spline, List<Vector3> points, TrackCorridorInfo corridor, float width, int lanes, float centerWidth, float platformLeft, float platformRight, string variantBase, string typeBase, PathSummary path)
    {
        Material deckMaterial = ResolveDeckMaterial(variantBase, typeBase);
        ResolveDeckFrames(corridor, points, out Vector3[] rightDir, out float[] cumLength);

        float halfWidth = width * 0.5f;
        if (platformLeft > 0f)
        {
            CreatePlatformSide(container, spline, corridor, points, rightDir, cumLength, leftSide: true, PlatformTrackOffset(width, lanes, true, centerWidth), halfWidth, platformLeft, deckMaterial, variantBase, typeBase, path);
        }
        if (platformRight > 0f)
        {
            CreatePlatformSide(container, spline, corridor, points, rightDir, cumLength, leftSide: false, PlatformTrackOffset(width, lanes, false, centerWidth), halfWidth, platformRight, deckMaterial, variantBase, typeBase, path);
        }
    }

    /// <summary>
    /// Generate island station platforms (two edges/one deck) between tracks.
    /// Use the single-track right-side platform unmirrored on left track and mirrored on right,
    /// both facing center. One deck spans ±IslandEdgeDistance; edge parts cover both seams,
    /// so omit side end faces. Islands have no platform_wall or platform_foundation.
    /// </summary>
    private static void CreateIslandPlatformParts(GameObject container, SplineComputer spline, List<Vector3> points, TrackCorridorInfo corridor, float width, int lanes, float centerWidth, string variantBase, string typeBase, PathSummary path)
    {
        int laneCount = Mathf.Max(1, lanes);
        int leftIndex = laneCount / 2 - 1, rightIndex = laneCount / 2;
        float leftTrack = GetTrackCenterOffset(width, laneCount, leftIndex, centerWidth);
        float rightTrack = GetTrackCenterOffset(width, laneCount, rightIndex, centerWidth);
        // At S joins, each edge and deck boundary moves with its track (converging tracks narrow the platform).
        TrackLateralSampler lateral = activeLateral;
        WithWarp(lateral != null ? new PartWarp { Shift = percent => lateral.ShiftAtPercent(leftIndex, percent) } : null, () =>
            PathTunnelParts.GeneratePlatform(container, spline, path, variantBase, typeBase, leftTrack, false));
        WithWarp(lateral != null ? new PartWarp { Shift = percent => lateral.ShiftAtPercent(rightIndex, percent) } : null, () =>
            PathTunnelParts.GeneratePlatform(container, spline, path, variantBase, typeBase, rightTrack, true));

        float edge = IslandEdgeDistance(width, laneCount, centerWidth);
        if (edge * 2f < PLATFORM_DECK_MIN_WIDTH || points == null || points.Count < 2) return;
        ResolveDeckFrames(corridor, points, out Vector3[] rightDir, out float[] cumLength);
        if (cumLength[points.Count - 1] - cumLength[0] < PLATFORM_DECK_MIN_RUN_LENGTH) return;
        var joined = new bool[points.Count];
        for (int i = 0; i < joined.Length; i++) joined[i] = true;
        Material material = ResolveDeckMaterial(variantBase, typeBase);
        GameObject endPrefab = LoadPrefab($"{variantBase}/platform_end", $"{typeBase}/platform_end");
        deckNearShift = lateral != null ? lateral.ShiftsAtPoints(leftIndex) : null;
        deckFarShift = lateral != null ? lateral.ShiftsAtPoints(rightIndex) : null;
        try
        {
            // Omit intervals whose converging tracks narrow the deck below the minimum; close platform before them.
            int n = points.Count, run = 0, i = 0;
            while (i < n)
            {
                if (!DeckWideEnough(i, false, -edge, null, edge)) { i++; continue; }
                int s = i;
                while (i < n && DeckWideEnough(i, false, -edge, null, edge)) i++;
                int e = i - 1;
                if (e - s < 1 || cumLength[e] - cumLength[s] < PLATFORM_DECK_MIN_RUN_LENGTH) continue;
                BuildPlatformDeckMesh(container, points, rightDir, cumLength, s, e, false, -edge, null, joined, edge, material, run++);
                if (s > 0) CloseIslandDeck(container, endPrefab, points, rightDir, s, edge, isEnd: false);
                if (e < n - 1) CloseIslandDeck(container, endPrefab, points, rightDir, e, edge, isEnd: true);
            }
        }
        finally
        {
            deckNearShift = null;
            deckFarShift = null;
        }
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[TrackPath] 島式ホーム: 軌道 {leftTrack}/{rightTrack}, デッキ ±{edge}");
    }

    /// <summary>Close island decks ending mid-path due to S narrowing with the same fences as platform ends.</summary>
    private static void CloseIslandDeck(GameObject container, GameObject prefab, List<Vector3> points, Vector3[] rightDir, int idx, float edge, bool isEnd)
    {
        int n = points.Count;
        Vector3 forward = points[Mathf.Min(idx + 1, n - 1)] - points[Mathf.Max(idx - 1, 0)];
        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-6f) return;
        forward.Normalize();
        DeckEdges(idx, false, -edge, null, edge, out float near, out float far);
        PlacePlatformEndPanels(container, prefab, points[idx], rightDir[idx], isEnd ? -forward : forward, near, far);
    }

    /// <summary>
    /// Read deck material from platform_panel (material-reference-only prefab); fall back to shared concrete
    /// because platforms define station visibility, unlike optional parts that may simply be skipped.
    /// </summary>
    private static Material ResolveDeckMaterial(string variantBase, string typeBase)
    {
        GameObject panelPrefab = LoadPrefab($"{variantBase}/platform_panel", $"{typeBase}/platform_panel");
        MeshRenderer panelRenderer = panelPrefab != null ? panelPrefab.GetComponent<MeshRenderer>() : null;
        if (panelRenderer != null && panelRenderer.sharedMaterial != null)
        {
            return RoadPath.GetOrCreateRoadMaterial(panelRenderer.sharedMaterial);
        }
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[TrackPath] platform_panel プレハブなし: デッキは共通コンクリートで描画");
        return BuildingMeshBuilder.GetCommonConcreteMaterial();
    }

    /// <summary>
    /// Right-vector/cumulative-arc-length frame for procedural decks. Share corridor arrays if available;
    /// otherwise compute from points (no neighbor).
    /// </summary>
    private static void ResolveDeckFrames(TrackCorridorInfo corridor, List<Vector3> points, out Vector3[] rightDir, out float[] cumLength)
    {
        if (corridor != null)
        {
            rightDir = corridor.RightDir;
            cumLength = corridor.CumulativeLength;
        }
        else
        {
            ComputeStripFrames(points, out rightDir, out cumLength);
        }
    }

    /// <summary>
    /// Generate one platform side (edge/deck/outer edge). trackOffset is the facing track's center lateral
    /// via PlatformTrackOffset (zero for single track); edge/outer edge/foundation origins use that centerline.
    ///
    /// Prefab modeling conventions (also see RoadPartHeights):
    /// Both platform and platform_wall use origin=track centerline/track surface;
    /// prefab geometry supplies lateral positions beyond that (code offsets only to track position).
    /// Author right-side parts; X-mirror left (origin on track center makes reflection symmetric about it):
    ///   platform (edge) ... Author edge at X=+PLATFORM_EDGE_OFFSET (fixed 1.5m,
    ///                      independent of variant width). Prefab supplies rise to platform top
    ///                      including vertical edge faces/support posts, and yellow lines/
    ///                      tactile paving above.
    ///                      Top details must match PLATFORM_SURFACE_ABOVE_SPLINE;
    ///                      the deck is a suspended 15cm slab without generated supports.
    ///   platform_wall ... Author walls/fences/benches at X=+(width/lanes/2+platform_width)
    ///                     (single-track outer edge), rising from platform top. Combined double-track stations
    ///                     have the same distance from track to outer edge (width/2+platform_width).
    ///                     Omit vertical faces coplanar with deck outer sides (avoid Z-fighting).
    /// Mismatch between prefab X and PLATFORM_EDGE_OFFSET/master width/lanes/platform_width
    /// misaligns edges/decks; author models to these values.
    /// Both use actual-size placement (no X scaling/pivot correction); skip if absent.
    /// </summary>
    private static void CreatePlatformSide(GameObject container, SplineComputer spline, TrackCorridorInfo corridor, List<Vector3> points, Vector3[] rightDir, float[] cumLength, bool leftSide, float trackOffset, float halfWidth, float platformWidth, Material deckMaterial, string variantBase, string typeBase, PathSummary path)
    {
        string sideLabel = leftSide ? "左" : "右";

        // At S joins, platform parts move sideways with their facing tracks.
        TrackLateralSampler lateral = activeLateral;
        int sideTrack = ActiveTrackIndex(trackOffset);
        PartWarp sideWarp = lateral != null ? new PartWarp { Shift = percent => lateral.ShiftAtPercent(sideTrack, percent) } : null;

        // Tile at authored length to preserve post/fence periods. Models contain subdivisions for curve conformity.
        // platform edge: origin at track centerline, placed at track-surface reference.
        GameObject edge = null;
        WithWarp(sideWarp, () => edge = PathTunnelParts.GeneratePlatform(container, spline, path, variantBase, typeBase, trackOffset, leftSide));
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[TrackPath] platform 淵({sideLabel}): {(edge != null ? "生成成功" : "プレハブなし")}");

        // Procedural deck: horizontal board from edge (PLATFORM_EDGE_OFFSET from facing track center)
        // to outer edge (width/2 + platform_width).
        float[] sideShifts = lateral != null ? lateral.ShiftsAtPoints(sideTrack) : null;
        deckNearShift = sideShifts;
        deckFarShift = sideShifts;
        int deckRuns;
        try
        {
            deckRuns = BuildPlatformDeckRuns(container, corridor, points, rightDir, cumLength, leftSide, Mathf.Abs(trackOffset) + PLATFORM_EDGE_OFFSET, halfWidth + platformWidth, deckMaterial);
        }
        finally
        {
            deckNearShift = null;
            deckFarShift = null;
        }
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[TrackPath] platform デッキ({sideLabel}): {deckRuns}区間");

        // platform_wall: origin at track centerline, placed at track-surface reference;
        // prefab geometry supplies lateral outer-edge position.
        // Omit island joins/neighbor encroachment via percent clipping.
        // Put always-visible details in platform, since platform_wall intervals can be omitted.
        List<TrackCorridorInfo.PercentRange> ranges = corridor != null
            ? corridor.GetPlatformWallPercentIntervals(leftSide, MERGE_WALL_MIN_RUN_LENGTH)
            : new List<TrackCorridorInfo.PercentRange> { new TrackCorridorInfo.PercentRange { From = 0.0, To = 1.0 } };
        WithWarp(sideWarp, () =>
        {
            foreach (var range in ranges)
            {
                RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/platform_wall", 0f, trackOffset, RoadPartHeights.PLATFORM_BASE_ABOVE_SPLINE, leftSide, $"{typeBase}/platform_wall", stretchToWidth: false, compensatePivot: false, clipFrom: range.From, clipTo: range.To, maxTileLength: 0f);
                PlatformFoundationGenerator.Generate(container, spline, variantBase, typeBase, leftSide, trackOffset, range.From, range.To);
            }
        });
    }

    /// <summary>
    /// Compute horizontal-right/cumulative-arc-length frame along points for decks without corridors,
    /// using the same derivation as TrackAdjacency.Compute.
    /// </summary>
    private static void ComputeStripFrames(List<Vector3> points, out Vector3[] rightDir, out float[] cumLength)
    {
        int n = points.Count;
        rightDir = new Vector3[n];
        cumLength = new float[n];
        float cum = 0f;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) cum += Vector3.Distance(points[i - 1], points[i]);
            cumLength[i] = cum;
            Vector3 fwd = points[Mathf.Min(i + 1, n - 1)] - points[Mathf.Max(i - 1, 0)];
            fwd.y = 0f;
            rightDir[i] = fwd.sqrMagnitude < 1e-6f
                ? (i > 0 ? rightDir[i - 1] : Vector3.right)
                : Vector3.Cross(Vector3.up, fwd.normalized);
        }
    }

    /// <summary>
    /// Signed geometric laterals of deck track-side/outer edges at idx, adding S shifts (deckNearShift/deckFarShift).
    /// When outer edge comes from corridor deckFar (actual shifted distance), do not add its shift again.
    /// </summary>
    private static void DeckEdges(int idx, bool leftSide, float nearLat, float[] deckFar, float defaultFar, out float nearPos, out float farPos)
    {
        float sideSign = leftSide ? -1f : 1f;
        float far = deckFar != null ? deckFar[idx] : defaultFar;
        nearPos = sideSign * nearLat + (deckNearShift != null ? deckNearShift[idx] : 0f);
        farPos = sideSign * far + (deckFar == null && deckFarShift != null ? deckFarShift[idx] : 0f);
    }

    /// <summary>Whether a deck fits at idx (known outer edge and width above minimum).</summary>
    private static bool DeckWideEnough(int idx, bool leftSide, float nearLat, float[] deckFar, float defaultFar)
    {
        if (deckFar != null && float.IsNaN(deckFar[idx])) return false;
        DeckEdges(idx, leftSide, nearLat, deckFar, defaultFar, out float nearPos, out float farPos);
        return Mathf.Abs(farPos - nearPos) >= PLATFORM_DECK_MIN_WIDTH;
    }

    /// <summary>
    /// Generate deck sections per continuous interval on the specified side; return section count.
    /// nearLat is platform edge distance from path centerline (PlatformEdgeDistance); defaultFar is
    /// outer edge (width/2 + platform_width). Follow corridor DeckFar
    /// (NaN=omit; includes island ownership/clamping); without a corridor,
    /// use defaultFar throughout.
    /// </summary>
    private static int BuildPlatformDeckRuns(GameObject container, TrackCorridorInfo corridor, List<Vector3> points, Vector3[] rightDir, float[] cumLength, bool leftSide, float nearLat, float defaultFar, Material deckMaterial)
    {
        if (deckMaterial == null || points == null || points.Count < 2) return 0;

        int n = points.Count;
        float[] deckFar = corridor != null ? (leftSide ? corridor.LeftDeckFar : corridor.RightDeckFar) : null;
        bool[] joined = corridor != null ? (leftSide ? corridor.LeftDeckJoined : corridor.RightDeckJoined) : null;

        int runIndex = 0;
        int i = 0;
        while (i < n)
        {
            if (!DeckWideEnough(i, leftSide, nearLat, deckFar, defaultFar))
            {
                i++;
                continue;
            }
            int s = i;
            while (i < n && DeckWideEnough(i, leftSide, nearLat, deckFar, defaultFar)) i++;
            int e = i - 1;

            if (e - s < 1) continue;
            if (cumLength[e] - cumLength[s] < PLATFORM_DECK_MIN_RUN_LENGTH) continue;

            BuildPlatformDeckMesh(container, points, rightDir, cumLength, s, e, leftSide, nearLat, deckFar, joined, defaultFar, deckMaterial, runIndex++);
        }
        return runIndex;
    }

    /// <summary>
    /// Generate deck mesh along point interval [s,e]. Deck is a suspended slab
    /// of PLATFORM_DECK_THICKNESS, consisting of
    /// top, bottom, outer vertical edge, and both end caps.
    /// Top lies PLATFORM_DECK_SINK below PLATFORM_SURFACE_ABOVE_SPLINE.
    /// platform edge geometry supplies supports/rise; do not generate them here.
    /// Place track-side top edge exactly at nearLat (PLATFORM_EDGE_OFFSET from facing track center),
    /// letting edge-part details cover the seam from 2cm above.
    /// Omit the track-side vertical face, also covered by edge parts.
    /// Omit outer faces at joined islands; neighbor edge parts provide them.
    /// </summary>
    private static void BuildPlatformDeckMesh(GameObject container, List<Vector3> points, Vector3[] rightDir, float[] cumLength, int s, int e, bool leftSide, float nearLat, float[] deckFar, bool[] joined, float defaultFar, Material material, int runIndex)
    {
        int count = e - s + 1;
        float deckTop = RoadPartHeights.PLATFORM_SURFACE_ABOVE_SPLINE - PLATFORM_DECK_SINK;
        float deckBottom = deckTop - PLATFORM_DECK_THICKNESS;
        float sideSign = leftSide ? -1f : 1f;

        var vertices = new List<Vector3>(count * 4 + 16);
        var normals = new List<Vector3>(count * 4 + 16);
        var uvs = new List<Vector2>(count * 4 + 16);
        var triangles = new List<int>((count - 1) * 18 + 24);

        // --- Top/bottom strips ---
        // Order vertices from smaller to larger lateral offset, preserving upward winding
        // with the bed-gap-filler convention (reverse bottom winding downward).
        // Meter-based UVs: lateral=offset, longitudinal=arc length.
        for (int k = 0; k < count; k++)
        {
            int idx = s + k;
            DeckEdges(idx, leftSide, nearLat, deckFar, defaultFar, out float nearPos, out float farPos);
            Vector3 basePos = points[idx] + Vector3.up * deckTop;
            Vector3 right = rightDir[idx];
            float lat0 = leftSide ? farPos : nearPos;
            float lat1 = leftSide ? nearPos : farPos;
            vertices.Add(basePos + right * lat0);
            vertices.Add(basePos + right * lat1);
            normals.Add(Vector3.up);
            normals.Add(Vector3.up);
            float v = cumLength[idx] * 0.25f;
            uvs.Add(new Vector2(lat0 * 0.25f, v));
            uvs.Add(new Vector2(lat1 * 0.25f, v));
        }
        int bottomBase = count * 2;
        for (int k = 0; k < count; k++)
        {
            int idx = s + k;
            DeckEdges(idx, leftSide, nearLat, deckFar, defaultFar, out float nearPos, out float farPos);
            Vector3 basePos = points[idx] + Vector3.up * deckBottom;
            Vector3 right = rightDir[idx];
            float lat0 = leftSide ? farPos : nearPos;
            float lat1 = leftSide ? nearPos : farPos;
            vertices.Add(basePos + right * lat0);
            vertices.Add(basePos + right * lat1);
            normals.Add(Vector3.down);
            normals.Add(Vector3.down);
            float v = cumLength[idx] * 0.25f;
            uvs.Add(new Vector2(lat0 * 0.25f, v));
            uvs.Add(new Vector2(lat1 * 0.25f, v));
        }
        for (int k = 0; k < count - 1; k++)
        {
            int v0 = k * 2;       // Near, smaller offset.
            int v1 = k * 2 + 1;   // Near, larger offset.
            int v2 = k * 2 + 2;   // Far, smaller offset.
            int v3 = k * 2 + 3;   // Far, larger offset.
            triangles.Add(v0); triangles.Add(v2); triangles.Add(v1);
            triangles.Add(v1); triangles.Add(v2); triangles.Add(v3);
            // Bottom uses the same order with reversed winding.
            int b0 = bottomBase + v0, b1 = bottomBase + v1, b2 = bottomBase + v2, b3 = bottomBase + v3;
            triangles.Add(b0); triangles.Add(b1); triangles.Add(b2);
            triangles.Add(b1); triangles.Add(b3); triangles.Add(b2);
        }

        // --- Outer vertical edge ---
        // Add only where neither endpoint joins an island (neighbor edge provides joined faces).
        for (int k = 0; k < count - 1; k++)
        {
            int a = s + k;
            int b = s + k + 1;
            if (joined != null && (joined[a] || joined[b])) continue;

            DeckEdges(a, leftSide, nearLat, deckFar, defaultFar, out _, out float farA);
            DeckEdges(b, leftSide, nearLat, deckFar, defaultFar, out _, out float farB);
            Vector3 latA = rightDir[a] * farA;
            Vector3 latB = rightDir[b] * farB;
            Vector3 topA = points[a] + Vector3.up * deckTop + latA;
            Vector3 topB = points[b] + Vector3.up * deckTop + latB;
            Vector3 botA = points[a] + Vector3.up * deckBottom + latA;
            Vector3 botB = points[b] + Vector3.up * deckBottom + latB;

            float u0 = cumLength[a] * 0.25f;
            float u1 = cumLength[b] * 0.25f;
            float vTop = deckTop * 0.25f;
            float vBot = deckBottom * 0.25f;
            AddDeckQuad(vertices, normals, uvs, triangles,
                topA, topB, botB, botA,
                rightDir[a] * sideSign,
                new Vector2(u0, vTop), new Vector2(u1, vTop), new Vector2(u1, vBot), new Vector2(u0, vBot),
                flip: leftSide);
        }

        // --- End caps ---
        AddDeckEndCap(vertices, normals, uvs, triangles, points, rightDir, s, leftSide, nearLat, deckFar, defaultFar, deckTop, deckBottom, isEnd: false);
        AddDeckEndCap(vertices, normals, uvs, triangles, points, rightDir, e, leftSide, nearLat, deckFar, defaultFar, deckTop, deckBottom, isEnd: true);

        Mesh mesh = new Mesh { name = $"TrackPlatformDeck_{(leftSide ? "L" : "R")}_{runIndex}" };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();

        GameObject go = new GameObject(mesh.name);
        go.transform.SetParent(container.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        go.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;
    }

    /// <summary>
    /// Add one deck end cap: vertical rectangle from track-side edge to outer edge,
    /// top to bottom across slab thickness. Normal faces outward along travel (start=-forward, end=+forward).
    /// </summary>
    private static void AddDeckEndCap(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int> triangles, List<Vector3> points, Vector3[] rightDir, int idx, bool leftSide, float nearLat, float[] deckFar, float defaultFar, float deckTop, float deckBottom, bool isEnd)
    {
        int n = points.Count;
        Vector3 fwd = points[Mathf.Min(idx + 1, n - 1)] - points[Mathf.Max(idx - 1, 0)];
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) return;
        fwd.Normalize();

        float sideSign = leftSide ? -1f : 1f;
        DeckEdges(idx, leftSide, nearLat, deckFar, defaultFar, out float nearPos, out float farPos);
        float near = sideSign * nearPos, far = sideSign * farPos;
        Vector3 nearOff = rightDir[idx] * nearPos;
        Vector3 farOff = rightDir[idx] * farPos;
        Vector3 nearTop = points[idx] + Vector3.up * deckTop + nearOff;
        Vector3 farTop = points[idx] + Vector3.up * deckTop + farOff;
        Vector3 nearBot = points[idx] + Vector3.up * deckBottom + nearOff;
        Vector3 farBot = points[idx] + Vector3.up * deckBottom + farOff;

        float vTop = deckTop * 0.25f;
        float vBot = deckBottom * 0.25f;
        AddDeckQuad(vertices, normals, uvs, triangles,
            nearTop, farTop, farBot, nearBot,
            isEnd ? fwd : -fwd,
            new Vector2(near * 0.25f, vTop), new Vector2(far * 0.25f, vTop), new Vector2(far * 0.25f, vBot), new Vector2(near * 0.25f, vBot),
            flip: leftSide != isEnd);
    }

    /// <summary>
    /// Add one quad. normal is the outward front normal;
    /// flip reverses winding only (true when a,b,c,d are counterclockwise viewed from normal,
    /// e.g. left-side mirroring, to align face orientation).
    /// </summary>
    private static void AddDeckQuad(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int> triangles, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD, bool flip)
    {
        int baseIdx = vertices.Count;
        vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
        for (int i = 0; i < 4; i++) normals.Add(normal);
        uvs.Add(uvA); uvs.Add(uvB); uvs.Add(uvC); uvs.Add(uvD);
        if (!flip)
        {
            triangles.Add(baseIdx); triangles.Add(baseIdx + 1); triangles.Add(baseIdx + 2);
            triangles.Add(baseIdx); triangles.Add(baseIdx + 2); triangles.Add(baseIdx + 3);
        }
        else
        {
            triangles.Add(baseIdx); triangles.Add(baseIdx + 2); triangles.Add(baseIdx + 1);
            triangles.Add(baseIdx); triangles.Add(baseIdx + 3); triangles.Add(baseIdx + 2);
        }
    }

    /// <summary>
    /// Place poles/beams along splines at wire_spacing.
    /// Inset ends by half spacing to avoid posts directly at intersection cuts
    /// (same endpoint-clearance principle as street trees).
    ///
    /// In joined corridors, only minimum-path_id main track
    /// places posts at both corridor edges and spans beams; other tracks omit them here.
    /// Main/other track post phases (spacing origins) need not align; after joins end,
    /// each resumes its own posts. Joined island platforms use the same main-track approach;
    /// Left/RightExtent already include full island width (including neighbor's outer platform),
    /// so posts stand outside both sides and beams span full station width.
    ///
    /// On platform sides (platformLeft/platformRight > 0), move posts
    /// beyond outer edges (+POLE_MARGIN) to avoid piercing platforms.
    /// This matches real station poles along outer edges. Stretch beams across post spacing.
    /// In LRT platform intervals (lrtPlatformZones, spline distances), both posts remain at least
    /// lrtPoleOffset (platform outer edge+PLATFORM_POLE_CLEARANCE) from centerline. For neighboring LRT platforms,
    /// corridor extents already include their outer edges (TrackAdjacency Member.Reach).
    /// </summary>
    private static void GeneratePolesAndBeams(GameObject container, SplineComputer spline, float spacing, float width, string variantBase, string typeBase, TrackCorridorInfo corridor = null, float platformLeft = 0f, float platformRight = 0f, List<Vector2> lrtPlatformZones = null, float lrtPoleOffset = 0f, float tunnelClearance = float.PositiveInfinity)
    {
        GameObject polePrefab = LoadPrefab($"{variantBase}/pole", $"{typeBase}/pole");
        GameObject beamPrefab = LoadPrefab($"{variantBase}/beam", $"{typeBase}/beam");
        if (polePrefab == null && beamPrefab == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[TrackPath] pole/beam プレハブなし: 架線柱スキップ");
            return;
        }

        float length = spline.CalculateLength();
        if (length <= 0f || spline.sampleCount == 0) return;

        float ownHalfWidth = width * 0.5f;
        float endClearance = spacing * 0.5f;
        TrackLateralSampler lateral = activeLateral;
        if (length <= endClearance * 2f)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[TrackPath] 架線柱: 線路が短すぎるため配置なし");
            return;
        }

        double currentPercent = spline.Travel(0.0, endClearance, Spline.Direction.Forward);
        double endPercent = spline.Travel(1.0, endClearance, Spline.Direction.Backward);
        if (endPercent <= currentPercent) return;

        // Prevent infinite loops if Travel stops advancing (same guard as tree placement).
        int maxCount = Mathf.FloorToInt(length / spacing) + 2;
        int count = 0;
        int placedCount = 0;

        // Track distance from spline start for corridor lookups;
        // SampleMode.Uniform closely matches point-list cumulative arc length.
        float currentDistance = endClearance;

        while (currentPercent <= endPercent && count < maxCount)
        {
            bool place = !TunnelSectionUtil.IsTunnelPoint(spline.EvaluatePosition(currentPercent), tunnelClearance);
            float leftHalf = ownHalfWidth;
            float rightHalf = ownHalfWidth;
            if (corridor != null)
            {
                int idx = corridor.SampleIndexAtLength(currentDistance);
                if (corridor.SuppressPoleSet[idx])
                {
                    // Main track (minimum path_id) places full-corridor poles/beams here.
                    place = false;
                }
                else
                {
                    leftHalf = corridor.LeftExtent[idx];
                    rightHalf = corridor.RightExtent[idx];
                }
            }

            // S joins move outer edges outward (corridor extents already include shifts).
            float shift = lateral != null ? lateral.OuterShift(currentDistance) : 0f;
            if (corridor == null)
            {
                leftHalf += shift;
                rightHalf += shift;
            }

            // Move platform-side posts beyond platform outer edges,
            // taking the larger of that and corridor extents.
            leftHalf = Mathf.Max(leftHalf, ownHalfWidth + shift + platformLeft);
            rightHalf = Mathf.Max(rightHalf, ownHalfWidth + shift + platformRight);

            if (place)
            {
                float leftPole = leftHalf + POLE_MARGIN, rightPole = rightHalf + POLE_MARGIN;
                if (LrtPlatformParts.Contains(lrtPlatformZones, currentDistance))
                {
                    leftPole = Mathf.Max(leftPole, lrtPoleOffset);
                    rightPole = Mathf.Max(rightPole, lrtPoleOffset);
                }
                SplineSample sample = spline.Evaluate(currentPercent);
                PlacePoleSet(container, polePrefab, beamPrefab, sample, leftPole, rightPole);
                placedCount++;
            }

            double next = spline.Travel(currentPercent, spacing, Spline.Direction.Forward);
            if (next <= currentPercent) break;
            currentPercent = next;
            currentDistance += spacing;
            count++;
        }

        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[TrackPath] 架線柱配置完了: {placedCount}組");
    }

    /// <summary>
    /// Place one pole set (left/right posts + portal beam).
    ///
    /// Prefab pivot conventions:
    ///   pole ... Origin=placement plane (same as track reference), extends +Y. Right-side part,
    ///            arms project -X toward track center. Left rotates 180° about Y for symmetry.
    ///   beam ... Origin=spline-center placement plane; geometry supplies beam height.
    ///            Stretch X to sum of post offsets; for asymmetric layouts,
    ///            shift to their midpoint (joined corridors do not center on own track).
    ///
    /// Posts sit on road/spline reference, hence atop elevated girders.
    /// Unlike piers (pillar), they do not extend to ground.
    /// </summary>
    private static void PlacePoleSet(GameObject container, GameObject polePrefab, GameObject beamPrefab, SplineSample sample, float leftPoleOffset, float rightPoleOffset)
    {
        // Overhead posts/beams stay world-upright, never tilting with terrain grade.
        // Derive yaw-only rotation from horizontal direction; use horizontal-right lateral offsets.
        Vector3 forward = sample.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        forward.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
        Quaternion yawRotation = Quaternion.LookRotation(forward, Vector3.up);

        Vector3 basePos = sample.position + Vector3.up * RoadPartHeights.OVERHEAD_ABOVE_SPLINE;

        if (polePrefab != null)
        {
            GameObject rightPole = Object.Instantiate(polePrefab, container.transform);
            rightPole.transform.position = basePos + right * rightPoleOffset;
            rightPole.transform.rotation = yawRotation;

            GameObject leftPole = Object.Instantiate(polePrefab, container.transform);
            leftPole.transform.position = basePos - right * leftPoleOffset;
            leftPole.transform.rotation = yawRotation * Quaternion.Euler(0f, 180f, 0f);
        }

        if (beamPrefab != null)
        {
            GameObject beam = Object.Instantiate(beamPrefab, container.transform);
            // For asymmetric joined corridors, place at midpoint between posts.
            beam.transform.position = basePos + right * ((rightPoleOffset - leftPoleOffset) * 0.5f);
            beam.transform.rotation = yawRotation;

            MeshFilter mf = beamPrefab.GetComponent<MeshFilter>();
            float meshWidth = (mf != null && mf.sharedMesh != null) ? mf.sharedMesh.bounds.size.x : 0f;
            if (meshWidth > 0.001f)
            {
                Vector3 scale = beam.transform.localScale;
                scale.x = (leftPoleOffset + rightPoleOffset) / meshWidth;
                beam.transform.localScale = scale;
            }
        }
    }

    /// <summary>
    /// Return wall-rendering spline-percent intervals.
    /// Without corridors, full length; otherwise only where the specified side lacks neighbors
    /// and other beds do not overlap wall positions.
    /// </summary>
    /// <summary>
    /// Return elevated part name: `{part}_elevated` if available, otherwise standard.
    /// Caller performs variant→type fallback using standard type names.
    /// </summary>
    private static string ElevatedPartName(string variantBase, string part)
    {
        string elevated = $"{variantBase}/{part}_elevated";
        bool exists = ModelManager.Instance != null && ModelManager.Instance.GetModel(elevated, true) != null;
        return exists ? elevated : $"{variantBase}/{part}";
    }

    /// <summary>
    /// Return continuous point intervals above terrain by more than GROUND_SNAP_EPSILON
    /// as arc-length-fraction spline-percent ranges, boundaries midway between samples.
    /// Exclude surface/cuttings (underground tunnel sections never reach this point list).
    /// </summary>
    private static List<TrackCorridorInfo.PercentRange> GetElevatedPercentRanges(List<Vector3> points, float baseHeight)
    {
        var result = new List<TrackCorridorInfo.PercentRange>();
        int n = points.Count;
        var cumulative = new float[n];
        for (int i = 1; i < n; i++)
        {
            cumulative[i] = cumulative[i - 1] + Vector2.Distance(new Vector2(points[i - 1].x, points[i - 1].z), new Vector2(points[i].x, points[i].z));
        }
        float total = cumulative[n - 1];
        if (total <= 0f) return result;

        bool Elevated(int i) => points[i].y - baseHeight - Utils.GetGroundHeight(points[i]) > TunnelSectionUtil.GROUND_SNAP_EPSILON;

        int idx = 0;
        while (idx < n)
        {
            if (!Elevated(idx)) { idx++; continue; }
            int s = idx;
            while (idx < n && Elevated(idx)) idx++;
            int e = idx - 1;
            float fromLen = (s == 0) ? 0f : (cumulative[s - 1] + cumulative[s]) * 0.5f;
            float toLen = (e == n - 1) ? total : (cumulative[e] + cumulative[e + 1]) * 0.5f;
            result.Add(new TrackCorridorInfo.PercentRange { From = fromLen / total, To = toLen / total });
        }
        return result;
    }

    /// <summary>Complement of sorted, nonoverlapping percent intervals within [0,1].</summary>
    private static List<TrackCorridorInfo.PercentRange> ComplementRanges(List<TrackCorridorInfo.PercentRange> ranges)
    {
        var result = new List<TrackCorridorInfo.PercentRange>();
        double cursor = 0.0;
        foreach (var range in ranges)
        {
            if (range.From > cursor) result.Add(new TrackCorridorInfo.PercentRange { From = cursor, To = range.From });
            cursor = range.To;
        }
        if (cursor < 1.0) result.Add(new TrackCorridorInfo.PercentRange { From = cursor, To = 1.0 });
        return result;
    }

    /// <summary>Intersection of two percent-interval sequences, excluding empty intervals.</summary>
    private static List<TrackCorridorInfo.PercentRange> IntersectRanges(List<TrackCorridorInfo.PercentRange> a, List<TrackCorridorInfo.PercentRange> b)
    {
        var result = new List<TrackCorridorInfo.PercentRange>();
        foreach (var x in a)
        {
            foreach (var y in b)
            {
                double from = System.Math.Max(x.From, y.From);
                double to = System.Math.Min(x.To, y.To);
                if (to > from) result.Add(new TrackCorridorInfo.PercentRange { From = from, To = to });
            }
        }
        return result;
    }

    private static List<TrackCorridorInfo.PercentRange> GetWallPercentRanges(TrackCorridorInfo corridor, bool leftSide)
    {
        if (corridor == null)
        {
            return new List<TrackCorridorInfo.PercentRange>
            {
                new TrackCorridorInfo.PercentRange { From = 0.0, To = 1.0 }
            };
        }
        return corridor.GetWallPercentIntervals(leftSide, MERGE_WALL_MIN_RUN_LENGTH);
    }

    /// <summary>
    /// Generate ballast gap fillers on each corridor side where self owns infill (lower path_id than neighbor),
    /// sharing own bed material.
    /// </summary>
    private static void CreateBodyGapFillers(GameObject container, TrackCorridorInfo corridor, Material bodyMaterial)
    {
        if (bodyMaterial == null) return;
        CreateBodyGapFillersForSide(container, corridor, bodyMaterial, leftSide: true);
        CreateBodyGapFillersForSide(container, corridor, bodyMaterial, leftSide: false);
    }

    private static void CreateBodyGapFillersForSide(GameObject container, TrackCorridorInfo corridor, Material bodyMaterial, bool leftSide)
    {
        float[] fillTo = leftSide ? corridor.LeftFillTo : corridor.RightFillTo;
        int n = corridor.Points.Count;

        int runIndex = 0;
        int i = 0;
        while (i < n)
        {
            if (!IsFillerSampleValid(fillTo[i], FillerNearLat(corridor, i)))
            {
                i++;
                continue;
            }
            int s = i;
            while (i < n && IsFillerSampleValid(fillTo[i], FillerNearLat(corridor, i))) i++;
            int e = i - 1;

            if (e - s < 1) continue;
            if (corridor.CumulativeLength[e] - corridor.CumulativeLength[s] < MERGE_FILLER_MIN_LENGTH) continue;

            BuildFillerStrip(container, corridor, s, e, leftSide, fillTo, bodyMaterial, runIndex++);
        }
    }

    /// <summary>Own filler edge (slightly inside bed; follows outer-bed S shifts).</summary>
    private static float FillerNearLat(TrackCorridorInfo corridor, int index)
    {
        return corridor.OwnHalfAt(index) - MERGE_FILLER_EDGE_INSET;
    }

    /// <summary>Whether filler fits here (owns infill and positive width).</summary>
    private static bool IsFillerSampleValid(float fillTo, float nearLat)
    {
        return !float.IsNaN(fillTo) && (fillTo + MERGE_FILLER_EDGE_INSET) > nearLat + 0.05f;
    }

    /// <summary>
    /// Generate bed-gap-filler strips along point interval [s,e].
    /// Horizontal height is BODY_ABOVE_SPLINE - MERGE_FILLER_SINK,
    /// sunk under both ballast shoulders (overlap edges by EDGE_INSET to avoid cracks).
    /// </summary>
    private static void BuildFillerStrip(GameObject container, TrackCorridorInfo corridor, int s, int e, bool leftSide, float[] fillTo, Material bodyMaterial, int runIndex)
    {
        int count = e - s + 1;
        var vertices = new Vector3[count * 2];
        var normals = new Vector3[count * 2];
        var uvs = new Vector2[count * 2];
        var triangles = new int[(count - 1) * 6];

        for (int k = 0; k < count; k++)
        {
            int idx = s + k;
            Vector3 basePos = corridor.Points[idx] + Vector3.up * (RoadPartHeights.BODY_ABOVE_SPLINE - MERGE_FILLER_SINK);
            Vector3 right = corridor.RightDir[idx];
            float farLat = fillTo[idx] + MERGE_FILLER_EDGE_INSET;
            float nearLat = FillerNearLat(corridor, idx);

            // Order vertices from smaller to larger lateral offset to preserve upward winding.
            float lat0 = leftSide ? -farLat : nearLat;
            float lat1 = leftSide ? -nearLat : farLat;
            vertices[k * 2] = basePos + right * lat0;
            vertices[k * 2 + 1] = basePos + right * lat1;
            normals[k * 2] = Vector3.up;
            normals[k * 2 + 1] = Vector3.up;

            // Approximate ballast tiling: meter-based offset across and arc length along.
            float v = corridor.CumulativeLength[idx] * 0.25f;
            uvs[k * 2] = new Vector2(lat0 * 0.25f, v);
            uvs[k * 2 + 1] = new Vector2(lat1 * 0.25f, v);
        }

        int t = 0;
        for (int k = 0; k < count - 1; k++)
        {
            int v0 = k * 2;       // Near, smaller offset.
            int v1 = k * 2 + 1;   // Near, larger offset.
            int v2 = k * 2 + 2;   // Far, smaller offset.
            int v3 = k * 2 + 3;   // Far, larger offset.
            triangles[t++] = v0; triangles[t++] = v2; triangles[t++] = v1;
            triangles[t++] = v1; triangles[t++] = v2; triangles[t++] = v3;
        }

        Mesh mesh = new Mesh { name = $"TrackMergeFiller_{(leftSide ? "L" : "R")}_{runIndex}" };
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();

        GameObject go = new GameObject(mesh.name);
        go.transform.SetParent(container.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = bodyMaterial;
        go.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;
    }

    /// <summary>
    /// Shared instantiation of intersection switch/crossing rail connectors (RailConnectorSpec)
    /// by bending rail prefabs with SplineMesh.
    /// Used by PathManager node updates, following the same division as
    /// RoadPath.CreateSidewalkConnectorObject (generator computes geometry; this instantiates).
    /// </summary>
    public static void CreateRailConnectorObject(Transform parent, RailConnectorSpec connector, int index)
    {
        if (connector == null || connector.Points == null || connector.Points.Count < 2)
        {
            return;
        }

        // Prefer rail-only parts (without sleepers); fall back to track parts.
        // Skip if neither exists (optional; only bed hull renders).
        string railPart = "rail";
        GameObject prefab = connector.EmbeddedLrt
            ? LoadPrefab($"Models/Paths/p{connector.PrefabVariant}/lrt_track", null)
            : LoadPrefab($"Models/Paths/p{connector.PrefabVariant}/rail", $"Models/Paths/p{connector.FallbackType}/rail");
        if (prefab == null)
        {
            railPart = "track";
            prefab = LoadPrefab($"Models/Paths/p{connector.PrefabVariant}/track", $"Models/Paths/p{connector.FallbackType}/track");
        }

        MeshFilter prefabMf = prefab != null ? prefab.GetComponent<MeshFilter>() : null;
        if (prefabMf == null || prefabMf.sharedMesh == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[TrackPath] レールコネクタ: rail/track プレハブなし (variant={connector.PrefabVariant}, type={connector.FallbackType})");
            return;
        }

        GameObject go = new GameObject($"RailConnector_{index}");
        go.transform.SetParent(parent, false);

        var points = new List<Vector3>(connector.Points);
        // Align tiny end sections to cross-section tangents, preventing reinterpolation from rotating rail ends.
        float head = Vector3.Distance(points[0], points[1]);
        float tail = Vector3.Distance(points[points.Count - 1], points[points.Count - 2]);
        if (connector.StartDirection.sqrMagnitude > 0f)
            points[1] = new Vector3(points[0].x + connector.StartDirection.x * head, points[0].y + connector.StartSlope * head, points[0].z + connector.StartDirection.y * head);
        if (connector.EndDirection.sqrMagnitude > 0f)
            points[points.Count - 2] = new Vector3(points[points.Count - 1].x - connector.EndDirection.x * tail, points[points.Count - 1].y - connector.EndSlope * tail, points[points.Count - 1].z - connector.EndDirection.y * tail);
        // BuildSpline accepts world points; convert node-local connector points here.
        for (int i = 0; i < points.Count; i++) points[i] = parent.TransformPoint(points[i]);
        SplineComputer spline = RoadPath.BuildSpline(go, points, true);
        if (connector.EmbeddedLrt)
        {
            RoadPath.CreateSplineMesh(go, spline, $"Models/Paths/p{connector.PrefabVariant}/lrt_track", 3, 0, 0,
                stretchToWidth: false);
            // Dedicated tracks have different rail-top/contact-wire relative heights; align wire endpoints separately.
            var wireRoot = new GameObject("LrtWireConnector");
            wireRoot.transform.SetParent(go.transform, false);
            var wirePoints = new List<Vector3>(points);
            for (int i = 0; i < wirePoints.Count; i++)
            {
                float t = (float)i / (wirePoints.Count - 1);
                wirePoints[i] += Vector3.up * Mathf.Lerp(connector.StartWireOffset, connector.EndWireOffset, t * t * (3 - 2 * t));
            }
            var wireSpline = RoadPath.BuildSpline(wireRoot, wirePoints, true);
            RoadPath.CreateSplineMesh(wireRoot, wireSpline, $"Models/Paths/p{connector.PrefabVariant}/lrt_wire", 1, 0, 0,
                stretchToWidth: false);
            return;
        }
        // Preserve authored sleeper thickness/period like main tracks; split at vertex limits without reducing tile count.
        RoadPath.CreateSplineMesh(go, spline, $"Models/Paths/p{connector.PrefabVariant}/{railPart}", 1f, 0f, 0f,
            fallbackPrefabName: $"Models/Paths/p{connector.FallbackType}/{railPart}", stretchToWidth: false, maxTileLength: 0f);
        if (connector.HasWire)
            RoadPath.CreateSplineMesh(go, spline, $"Models/Paths/p{connector.PrefabVariant}/wire", 1f, 0f, 0f,
                fallbackPrefabName: $"Models/Paths/p{connector.FallbackType}/wire", stretchToWidth: false);
    }

    /// <summary>
    /// Get track rendering parameters from master data.
    /// For tracks, lanes means track count (single=1, double=2, quadruple=4).
    /// platformWidth is station-platform width (zero for nonplatform variants).
    /// </summary>
    public static void GetTrackMasterParams(PathSummary pathData, out float width, out int lanes, out float wallWidth, out float? pillarSpacing, out bool heightEditable, out float? wireSpacing, out float baseHeight, out float platformWidth)
    {
        width = 5.0f;
        lanes = 1;
        wallWidth = 0f;
        pillarSpacing = null;
        heightEditable = false;
        wireSpacing = null;
        baseHeight = 0f;
        platformWidth = 0f;

        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(pathData.path_variant) : null;
        if (master == null)
        {
            Debug.LogWarning($"[TrackPath] マスターデータなし (variant={pathData.path_variant})。デフォルト値を使用。");
            return;
        }

        if (master.width > 0f) width = master.width;
        if (master.lanes.HasValue && master.lanes.Value > 0) lanes = master.lanes.Value;
        if (master.wall_width.HasValue) wallWidth = master.wall_width.Value;
        if (master.pillar_spacing.HasValue) pillarSpacing = master.pillar_spacing.Value;
        if (master.height_editable.HasValue) heightEditable = master.height_editable.Value;
        if (master.wire_spacing.HasValue) wireSpacing = master.wire_spacing.Value;
        if (master.base_height.HasValue) baseHeight = master.base_height.Value;
        if (master.platform_width.HasValue && master.platform_width.Value > 0f) platformWidth = master.platform_width.Value;
    }

    private static GameObject LoadPrefab(string primaryPath, string fallbackPath)
    {
        if (ModelManager.Instance == null) return null;
        GameObject prefab = ModelManager.Instance.GetModel(primaryPath);
        if (prefab == null && !string.IsNullOrEmpty(fallbackPath))
        {
            prefab = ModelManager.Instance.GetModel(fallbackPath);
        }
        return prefab;
    }
}
