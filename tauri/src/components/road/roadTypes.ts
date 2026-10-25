/**
 * API response types for named roads.
 *
 * A road groups multiple paths (sections) divided by intersections into one road, such as "Ome Kaido".
 * Section road_id is authoritative (also included in path list/detail APIs); VALUE_NONE (-1) means an unnamed path.
 * The backend automatically deletes road records when no sections remain.
 *
 * Backend: GET/POST /api/v1/roads/, GET/PATCH/DELETE /api/v1/roads/{id}, etc.
 */

/** Road name placement in map/3D. angle_deg is world-XZ road orientation (atan2(dz, dx) in degrees). */
export type RoadLabelPoint = {
  x: number;
  z: number;
  angle_deg: number;
};

/** One GET /api/v1/roads/ list entry, used for Unity/map labels and membership choices. */
export type RoadSummary = {
  road_id: number;
  /** Empty means unnamed; display code supplies "Road #id". */
  name: string;
  /** "#rrggbb" */
  color: string;
  /** Member section path IDs in ascending order, including planned/under-construction sections. */
  path_ids: number[];
  segment_count: number;
  /** Total centerline length of all member sections (m). */
  length_m: number;
  /** [minx, minz, maxx, maxz], or null if geometry is unavailable. */
  bbox: [number, number, number, number] | null;
  label_points: RoadLabelPoint[];
};

export type RoadListResponse = {
  /** Integer changing with road set/name/color/membership and section geometry/status. Unchanged values allow skipping redraws. */
  version: number;
  roads: RoadSummary[];
};

/** Connected component of sections. Components are separated by disconnected gaps; chainage continues across gaps. */
export type RoadSection = {
  index: number;
  start_km: number;
  end_km: number;
  /** Straight distance (m) from the previous component's end to this component's start. null for the first component. */
  gap_before_m: number | null;
};

/**
 * Section role:
 * - main: part of the baseline (chainage axis).
 * - parallel: one-way section parallel to the baseline (opposite carriageway of a divided road).
 * - branch: section branching off the baseline.
 */
export type RoadSegmentRole = "main" | "parallel" | "branch";

/** down = increasing chainage, away from the origin; up = toward the origin; both = bidirectional. */
export type RoadSegmentDirection = "both" | "down" | "up";

export type RoadSegment = {
  path_id: number;
  /** Section name (e.g. bridge/tunnel name). Empty means unnamed. */
  name: string;
  path_type: number;
  path_variant: number;
  lanes: number;
  status: number;
  section_index: number;
  role: RoadSegmentRole;
  from_km: number;
  to_km: number;
  /**
 * Chainage where a branch attaches to the baseline (starting position in the strip map). Use the endpoint connected
 * to a baseline/parallel section, or project the nearer endpoint onto the baseline if neither connects.
 * null for main/parallel sections.
 */
  attach_km: number | null;
  length_m: number;
  direction: RoadSegmentDirection;
  zone_id: number;
  zone_name: string | null;
  /** String VEHICLE_* key → daily passing trips. Includes only pedestrians/road vehicles with positive values. */
  traffic: Record<string, number>;
  /** Daily road vehicle traffic. */
  road_traffic: number;
  /** Daily road traffic capacity. null when nonpositive. */
  capacity: number | null;
  /** Congestion = road_traffic / capacity, not capped at 1. null without capacity. */
  vc: number | null;
  /** Speed limit (km/h). */
  speed_limit: number | null;
  /** Shape-derived passing speed (km/h). */
  base_speed: number | null;
  /** Free-flow car travel time (minutes). null for inactive/impassable sections. */
  travel_time_free_min: number | null;
  /** Congested car travel time (minutes). */
  travel_time_min: number | null;
  durability: number;
  noise: number;
  air_pollution: number;
  parked_cars: number;
  street_parking_capacity: number;
  /** Whether a traffic accident occurred this epoch. */
  accident: boolean;
  under_maintenance: boolean;
  rebuilding: boolean;
  structure: "tunnel" | "bridge" | null;
};

/** Crossing counterpart at an intersection. If the same road passes through, combine into one entry with side="both". */
export type RoadCrossing = {
  path_id: number;
  path_type: number;
  road_id: number | null;
  road_name: string | null;
  road_color: string | null;
  /** Counterpart section name (empty if unnamed). */
  path_name: string;
  /** Which side the counterpart extends toward relative to increasing chainage. */
  side: "left" | "right" | "both";
};

export type RoadEvent =
  | { kind: "terminal"; km: number; section_index: number; node_id: number | null; position: "start" | "end" }
  | { kind: "crossing"; km: number; node_id: number; crossings: RoadCrossing[] }
  | { kind: "interchange"; km: number; node_id: number; interchange_id: number; name: string; interchange_kind: number }
  | { kind: "stop"; km: number; node_id: number; name: string | null; line_agent_id: number };

export type RoadStats = {
  segment_count: number;
  length_m: number;
  /** Active section length (m). */
  active_length_m: number;
  /** Length of planned, ordered, and under-construction sections (m). */
  planned_length_m: number;
  lanes_composition: { lanes: number; length_m: number }[];
  /** Average cross-sectional traffic (vehicles/day): active baseline road_traffic weighted by section length. */
  avg_daily_traffic: number | null;
  /** Vehicle-kilometers traveled (vehicle-km/day) = Σ road_traffic × section length (km), over active baseline/parallel sections. */
  vehicle_km: number;
  max_vc: { value: number; path_id: number; from_km: number; to_km: number } | null;
  avg_vc: number | null;
  /** Length (m) of active sections with congestion >= 0.8. */
  congested_length_m: number;
  /** Baseline travel time from origin to endpoint (minutes), free-flow/congested. null if any section is inactive. */
  travel_time_free_min: number | null;
  travel_time_min: number | null;
  travel_speed_free_kmh: number | null;
  travel_speed_kmh: number | null;
  /** Congestion delay (vehicle-hours/day) = Σ road_traffic × (congested − free-flow travel time) / 60. */
  delay_vehicle_hours: number;
  avg_durability: number | null;
  low_durability_count: number;
  accident_segment_count: number;
  speed_limit_min: number | null;
  speed_limit_max: number | null;
};

/** Details from GET /api/v1/roads/{id}. */
export type RoadDetail = RoadSummary & {
  version: number;
  /** Origin reference position [x, z], updated on endpoint reversal. null when unset (determined automatically). */
  origin: [number, number] | null;
  stats: RoadStats;
  sections: RoadSection[];
  segments: RoadSegment[];
  events: RoadEvent[];
  /** Districts traversed, in first-occurrence chainage order. */
  zones: { zone_id: number; name: string }[];
  /** Public transport lines using member sections. */
  transit_lines: { line_agent_id: number; name: string; color: string }[];
};

export type RoadMutationResponse = { road: RoadSummary };
export type RoadMembersResponse = { road: RoadSummary | null; deleted: boolean };
export type RoadDeleteResponse = { deleted: true; cleared_path_ids: number[] };
/** bbox covers candidate sections [minx, minz, maxx, maxz], null if no candidates. Older backends omit it. */
export type RoadTraceResponse = { path_ids: number[]; bbox?: [number, number, number, number] | null };
export type RoadExtensionCandidate = { road_id: number; name: string; color: string; point_index: number };
export type RoadExtensionCandidatesResponse = { candidates: RoadExtensionCandidate[] };
