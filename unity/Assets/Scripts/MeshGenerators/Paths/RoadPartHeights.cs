/// <summary>
/// Height-offset constants for road/intersection parts.
///
/// Road rendering has two layers. Inconsistent per-generator offsets would
/// create steps at joins or Z-fighting between parts;
/// always derive height-related values from these constants.
///
/// [Layer 1: spline points]
///   Terrain-following road (relative Y=0) spline points sit at terrain elevation + ROAD_SPLINE_OFFSET
///   (PathManager sampling). Elevated/underground roads with relative Y≠0 use
///   data Y (terrain+relative Y) directly without this offset.
///
/// [Layer 2: part offsets from spline]
///   Each part is raised further from spline points through SplineMesh offset.y.
///   Road top = spline + BODY_ABOVE_SPLINE (terrain+0.2 for surface roads).
///
/// Intersections reproduce these layers:
///   Cut points (assigned to road endpoint spline points) = reference plane (terrain+ROAD_SPLINE_OFFSET,
///   or absolute node Y for elevated nodes).
///   Intersection asphalt = reference plane + BODY_ABOVE_SPLINE (coplanar with road top;
///   regions meet at cut lines without overlap/Z-fighting).
///   Sidewalk connectors = reference plane + SIDEWALK_ABOVE_SPLINE (flush with ordinary sidewalk bottoms).
///
/// Vertical prefab pivot conventions (constants require these):
///   - body: pivot=road top (thickness extends downward).
///   - line / sidewalk / wall / center: pivot=bottom (extends upward; curb rise is prefab geometry).
///   Legacy body/line/sidewalk models including road height are rebased to road origin in generation meshes.
///   Correct sidewalks/lines using body-top Y from the same selected model family.
/// </summary>
public static class RoadPartHeights
{
    /// <summary>
    /// Lift of terrain-following spline points above terrain, preventing burial from terrain mesh resolution limits.
    /// </summary>
    public const float ROAD_SPLINE_OFFSET = 0.1f;

    /// <summary>Road body height above spline. Road top matches this plane (pivot convention).</summary>
    public const float BODY_ABOVE_SPLINE = 0.1f;

    /// <summary>Edge/center/lane-divider line height above spline, 2cm above body top.</summary>
    public const float LINE_ABOVE_SPLINE = 0.12f;

    /// <summary>
    /// Extra lift of center/lane-divider lines above side lines.
    /// Avoids Z-fighting where they overlap near intersections.
    /// </summary>
    public const float CENTER_LINE_EXTRA = 0.005f;

    /// <summary>Sidewalk-bottom height above spline. Same plane as body top.</summary>
    public const float SIDEWALK_ABOVE_SPLINE = 0.1f;

    /// <summary>Sound-barrier/guardrail bottom height above spline.</summary>
    public const float WALL_ABOVE_SPLINE = 0.1f;

    /// <summary>
    /// Height above spline of track parts (two rails+sleepers for one track) and
    /// intersection rail-connector bottoms (rail). Place 2cm above bed body top (BODY_ABOVE_SPLINE)
    /// to avoid Z-fighting.
    /// Prefab pivot convention: track/rail pivot at sleeper underside (contact with bed).
    /// Prefab geometry supplies rise to rail top.
    /// </summary>
    public const float TRACK_ABOVE_SPLINE = 0.12f;

    /// <summary>
    /// Monorail track-part (track/rail, guideway beam) top above its origin (spline + TRACK_ABOVE_SPLINE).
    /// Beam top=spline+1.2m, bottom=spline-0.3m, preventing burial even at zero relative height.
    /// Place vehicle origin (running-tire contact plane) on top; floor at beam-top+0.3m matches platform (PLATFORM_SURFACE_ABOVE_SPLINE).
    /// As with railway rail tops, prefab geometry supplies the rise.
    /// </summary>
    public const float MONORAIL_BEAM_TOP_ABOVE_TRACK = 1.08f;

    /// <summary>
    /// Overhead wire/post/beam reference-plane height above spline.
    /// Use the same reference as track parts.
    /// Prefab pivot convention: all use the track-bottom reference plane;
    /// prefab geometry supplies contact-wire height (about 5m above rail) and pole rise
    /// (code has no wire-height parameter).
    /// </summary>
    public const float OVERHEAD_ABOVE_SPLINE = 0.12f;

    /// <summary>
    /// One-way track direction-arrow (TrackDirectionArrows) surface height above spline.
    /// Raise above rail top (track reference + prefab rise of about 0.2m)
    /// so arrows remain visible over rails in overhead editing views.
    /// </summary>
    public const float DIRECTION_ARROW_ABOVE_SPLINE = 0.45f;

    /// <summary>
    /// Station-platform prefab (platform/platform_wall) placement origin height above spline.
    /// Model convention: origin=track centerline/track surface
    /// (spline center/reference plane). Prefab geometry supplies lateral edge/outer-edge positions
    /// and rise to platform surface (see TrackPath.CreatePlatformSide).
    /// Extend bottoms slightly below origin (about -0.5m) to absorb small terrain variations.
    /// </summary>
    public const float PLATFORM_BASE_ABOVE_SPLINE = 0.0f;

    /// <summary>
    /// Station-house footbridge (body) origin height above spline.
    /// Model convention: origin=ground (spline reference plane);
    /// prefab geometry supplies deck clearance and pier rise
    /// (same principle as poles/platforms; code holds no girder height).
    /// </summary>
    public const float STATION_BRIDGE_BASE_ABOVE_SPLINE = 0.0f;

    /// <summary>
    /// Nominal station-platform surface height above spline.
    /// About 1.2m above rail top (track reference + prefab rise about 0.2m).
    /// The dynamically generated horizontal deck sits PLATFORM_DECK_SINK
    /// (TrackPath) below this plane.
    /// Author platform-edge (platform) top geometry at this constant height (1.5m)
    /// above prefab origin; otherwise deck and edge tops disagree.
    /// </summary>
    public const float PLATFORM_SURFACE_ABOVE_SPLINE = 1.5f;
}
