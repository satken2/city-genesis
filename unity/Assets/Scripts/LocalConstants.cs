using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.VisualBasic;
using UnityEngine;

/// <summary>
/// Defines constants used throughout the game.
/// </summary>
public static class LocalConstants
{
    public static readonly Dictionary<int, Color> SPACE_TYPE_COLOR_MAP = new Dictionary<int, Color>
    {
        { Constants.SPACE_RESIDENCE, new Color(0.25f, 0.8f, 0.25f) },
        { Constants.SPACE_RETAIL, new Color(0.25f, 0.5f, 0.95f) },
        { Constants.SPACE_FACTORY, new Color(0.95f, 0.85f, 0.2f) },
        { Constants.SPACE_OFFICE, new Color(0.6f, 0.4f, 0.9f) },
        { Constants.SPACE_OTHER, new Color(0.7f, 0.7f, 0.7f) },
        { Constants.SPACE_FARM, new Color(0.4f, 0.8f, 0.1f) },
        { Constants.SPACE_LODGING, new Color(0.91f, 0.12f, 0.39f) },
    };
    /// <summary>
    /// Fallback height per floor (m).
    /// Used only when height_per_floor is unavailable in master data.
    /// The world uses 1 Unity unit = 1m, matching the master default of 4.0.
    /// </summary>
    public const float FLOOR_HEIGHT = 4f;

    /// <summary>
    /// Maximum turn deflection at PATH_TRACK/PATH_MONORAIL nodes (degrees; straight = 0, reversal = 180).
    /// Rail connectors inside intersections are rendered only for traversable movements with deflection at or below this value.
    /// Keep this equal to RAIL_MAX_TURN_ANGLE_DEG in the backend's parameters.yaml,
    /// the turn constraint for underlying public transport route searches. A mismatch would allow visible rails
    /// that cannot be traversed, or traversable connections without visible rails.
    /// Divergence when a player edits parameters.yaml is a known limitation.
    /// </summary>
    public const float RAIL_MAX_TURN_ANGLE_DEG = 45.0f;

    // ------------------------------------------------------------------
    // Combined rendering of nearby tracks to make double track visible.
    // There is no internal double-track unit; two one-way single tracks represent double track.
    // Nearby parallel tracks share combined trackbeds, noise barriers, catenary poles, and beams
    // (TrackAdjacency / TrackPath; rendering only, with no backend effect).
    // ------------------------------------------------------------------

    /// <summary>
    /// Maximum XZ gap between trackbed edges for merging (m).
    /// Connect neighboring tracks into one corridor when the gap between their trackbed edges is at or below this value.
    /// This is not centerline distance, so variants of different widths join consistently.
    /// </summary>
    public const float TRACK_MERGE_MAX_EDGE_GAP = 4.0f;

    /// <summary>
    /// Maximum Y difference for merging (m), compared using terrain-following rendered polylines,
    /// so tracks on different levels, such as elevated/ground or ground/underground, do not merge.
    /// </summary>
    public const float TRACK_MERGE_MAX_DY = 1.0f;

    /// <summary>
    /// Maximum tangent-angle difference for merging (degrees). Use the absolute tangent dot product
    /// so tracks laid in opposite directions for inbound/outbound service can still merge.
    /// </summary>
    public const float TRACK_MERGE_MAX_ANGLE_DEG = 20.0f;

    /// <summary>
    /// Maximum angle difference for merging tracks converging at a merge or branch (degrees).
    /// Only when trackbed edge gaps are at most TRACK_MERGE_MAX_EDGE_GAP,
    /// allow merging into one corridor up to this angle, even above TRACK_MERGE_MAX_ANGLE_DEG.
    /// Waiting for tangents to align within 20 degrees would delay merging double-track beds until they overlap,
    /// letting the noise barrier between tracks intrude into the other track.
    /// The value matches node turn constraint RAIL_MAX_TURN_ANGLE_DEG,
    /// the maximum entry angle that can form a merge.
    /// </summary>
    public const float TRACK_MERGE_MAX_CONVERGE_ANGLE_DEG = 45.0f;

    // ------------------------------------------------------------------
    // Track joints: TrackEndTransition S-curves and straight connections without an intersection slab.
    // ------------------------------------------------------------------

    public const float TRACK_TRANSITION_DEFAULT_RADIUS = 100f; // S-curve radius for tracks without a master-data minimum curve radius (m).
    public const float TRACK_JOIN_MAX_BEND_DEG = 2.0f; // Allowed centerline bend at nodes joining tracks without an intersection slab (degrees), following ROAD_MERGE_MAX_MAIN_BEND_DEG for motorways.
    public const float TRACK_JOIN_LATERAL_TOLERANCE = 0.01f; // Tolerance for matching lateral track position and width at two ends joined without a slab (m).
    public const float PROVISIONAL_TRACK_WELD_DISTANCE = 1.5f; // Horizontal distance for connecting node-less planned, under-construction, or preview track ends (m), matching the core's RAIL_NODE_SNAP_THRESHOLD.
    public const float PROVISIONAL_TRACK_WELD_HEIGHT = 0.5f; // Maximum absolute-elevation difference for the same connection (m), matching the core's welding on completion.

    public const float ROAD_MERGE_MAX_EDGE_GAP = 2.0f; // Join motorway main-line and ramp surfaces when their edges are within this gap (m).
    public const float ROAD_MERGE_MAX_ANGLE_DEG = 30.0f; // Maximum angle difference for merging or incorporating a main line and ramp (degrees).
    public const float ROAD_MERGE_MAX_DY = 1.0f; // Maximum height difference for merging adjacent sections whose road surfaces do not overlap (m).
    public const float ROAD_MERGE_OVERLAP_MAX_DY = 4.0f; // Maximum height difference for bringing a ramp to main-line height where surfaces overlap (m).
    public const float ROAD_MERGE_SINK = 0.02f; // Lower the merged ramp surface below the main line by this amount to prevent Z-fighting (m).
    public const float ROAD_MERGE_MAX_ADDED_GRADE = 0.04f; // Maximum added grade while transitioning the adjusted ramp height back to its original vertical alignment.
    public const float ROAD_MERGE_MIN_TRANSITION = 20f; // Minimum length of the ramp-height transition (m).
    public const float ROAD_MERGE_MAX_TRANSITION = 120f; // Maximum length of the ramp-height transition (m).
    public const float ROAD_MERGE_MAX_MAIN_BEND_DEG = 2.0f; // Allowed main-line bend at nodes joining main lines without an intersection slab (degrees).
    public const float INTERSECTION_SURFACE_VERTEX_SPACING = 2.0f; // Spacing of interior intersection-surface vertices (m). Fine triangles follow grade curvature without bending across a diagonal.

    // ------------------------------------------------------------------
    // 3D rendering during construction (STATUS_CONSTRUCTING).
    // Buildings: dirt mound covering the site + scaffold ring (scaffold-wall/corner) + crane (crane-base/mid/top).
    // Paths: dirt strip covering the route + elevated pillars only + tunnel portals only.
    // ------------------------------------------------------------------

    /// <summary>Minimum logical floor count for a crane. Lower-rise construction sites do not get tower cranes.</summary>
    public const int CONSTRUCTION_CRANE_MIN_FLOORS = 4;

    /// <summary>Crane segments above the current scaffold height (builtFloors), representing a tower crane rising above the building.</summary>
    public const int CONSTRUCTION_CRANE_EXTRA_LEVELS = 2;

    /// <summary>Base dirt-mound thickness (m). Site and road dirt rises this far above the ground.</summary>
    public const float CONSTRUCTION_DIRT_THICKNESS = 0.4f;

    /// <summary>Noise bump amplitude on the dirt mound's top (m). Uses multi-octave fbm (ConstructionMeshUtil.DirtNoise).</summary>
    public const float CONSTRUCTION_DIRT_BUMP_AMPLITUDE = 0.35f;

    /// <summary>World frequency of the base dirt-noise octave (1/m). Multiply coordinates by this value (wavelength about 6.7m); higher octaves use 2.7x and 6.1x.</summary>
    public const float CONSTRUCTION_DIRT_NOISE_SCALE = 0.15f;

    /// <summary>Maximum edge length when subdividing the dirt mound's top (m). Longer edges split at midpoints to create interior bump vertices.</summary>
    public const float CONSTRUCTION_DIRT_MAX_EDGE = 3f;

    /// <summary>Maximum edge length of provisional floor meshes before terrain tiles load (m). Unused after loading, when clipping follows the terrain grid.</summary>
    public const float SITE_FLOOR_MAX_EDGE = 6f;

    /// <summary>Allowed screen-space error for Terrain rendering LOD, limiting shape differences from covering floors and roads.</summary>
    public const float TERRAIN_HEIGHTMAP_PIXEL_ERROR = 1f;

    /// <summary>Dirt-mound edge fade distance (m). Attenuate bumps within this distance, reaching zero at boundary vertices to avoid gaps with adjacent parcels.</summary>
    public const float CONSTRUCTION_DIRT_EDGE_FADE = 1.5f;

    /// <summary>Extra width added to road width for under-construction path dirt strips (m).</summary>
    public const float CONSTRUCTION_PATH_DIRT_EXTRA_WIDTH = 1.0f;

    /// <summary>Base rubble-mound thickness for STATUS_COLLAPSED (m), thicker than construction dirt (CONSTRUCTION_DIRT_THICKNESS).</summary>
    public const float CONSTRUCTION_RUBBLE_THICKNESS = 0.8f;

    /// <summary>Top-surface noise bump amplitude for collapsed rubble (m), rougher than dirt (CONSTRUCTION_DIRT_BUMP_AMPLITUDE).</summary>
    public const float CONSTRUCTION_RUBBLE_BUMP_AMPLITUDE = 0.7f;

    /// <summary>XZ fill ratio when fitting collapse parts to the footprint AABB. Below 1 leaves margins inside the parcel.</summary>
    public const float COLLAPSE_FRAGMENT_FILL = 0.9f;

    /// <summary>Maximum collapse-part height (m). Taller models are compressed along Y to keep rubble low.</summary>
    public const float COLLAPSE_FRAGMENT_MAX_HEIGHT = 3.0f;

    /// <summary>
    /// Prefix folder for standard parts shared by all buildings.
    /// scaffold/crane parts fall back from variant (a{variant}) to building type (b{bd_type}) to this folder
    /// (Models/Buildings/common/). Since common has no appearance models,
    /// parts go directly in it without a suffix folder.
    /// </summary>
    public const string CONSTRUCTION_COMMON_PREFIX = "common";

    public const float VEHICLE_TIME_EXAGGERATION = 60f; // Time-compression factor for car rendering. 1 means physically accurate simultaneous vehicle counts, almost always 0.
    public const float VEHICLE_ACTIVE_HOURS_PER_DAY = 15f; // Daily traffic activity hours (h). Keep equal to PATH_ACTIVE_HOURS in general_params.yaml.
    public const float PEDESTRIAN_WALK_SPEED = 1.1f; // Average pedestrian speed (m/s). Keep equal to PEDESTRIAN_SPEED (4km/h) in general_params.yaml.
    public const float PEDESTRIAN_SIDEWALK_SURFACE = 0.2f; // Modeled sidewalk height from part bottom to top (m), using the standard sidewalk prefab's value.
    public const float PATH_CURVATURE_WINDOW_M = 10f; // Curve-radius/grade evaluation window (meters before/after). Keep equal to PATH_CURVATURE_WINDOW_M in general_params.yaml.
    public const float PATH_GRADE_SMOOTH_MIN_M = 10f; // Elevation moving-average distance before grade evaluation (m). Keep equal to backend geo_path_alignment's _MIN_GRADE_SMOOTH_M.

    public const bool UNITY_LOG_BUILDING = false; // BuildingMeshBuilder and others.
    public const bool UNITY_LOG_ROAD = false;     // RoadPath and others.
    public const bool UNITY_LOG_LOADER = false;   // Loading progress logs from CellDataLoader and each Manager.
    public const bool UNITY_LOG_TIMING = true;  // [TIMING] measurement logs.
}

/// <summary>
/// Debug.Log wrapper used with LocalConstants.UNITY_LOG_*.
/// A batch emits dozens of timing lines, so use NoStacktrace to avoid collecting a stack trace for each line.
/// </summary>
public static class UnityLog
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void If(bool enabled, object message)
    {
        if (enabled) Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", message);
    }

    /// <summary>The callback is not invoked when disabled, avoiding expensive interpolation when needed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void If(bool enabled, Func<object> message)
    {
        if (enabled) Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", message());
    }
}
