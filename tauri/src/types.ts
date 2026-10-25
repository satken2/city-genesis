import type { LocalizedText } from "./utils/locale";

export type EntityRef = {
  type?: string;
  id?: string;
};

export type WindowActionConfig = {
  id: string;
  label: string;
};

export type FetchConfig = {
  method: string;
  path_template: string;
};

export type WindowDefinition = {
  title: string;
  width: number;
  height: number;
  resizable: boolean;
  policy: "single" | "multiple" | "single_per_entity";
  entity_type?: string;
  fetch?: FetchConfig;
  actions: WindowActionConfig[];
  html_path?: string;
  /** Render an empty area without body content (for mocks). */
  empty_shell?: boolean;
  /** Omit the title bar (drag area and close button); the panel fills the window and handles closing. */
  chromeless?: boolean;
};

export type WindowContext = {
  label: string;
  instance_key: string;
  window_type: string;
  title: string;
  definition: WindowDefinition;
  entity?: EntityRef;
  params: Record<string, unknown> | null;
  api_base_url: string;
  /**
   * Opening generation, incremented by Rust whenever a hidden window is shown again or repurposed. WebView2 is
   * reused rather than destroyed on reopening; including this value in App.tsx's remount key guarantees
   * reopening resets panel state.
   */
  open_seq: number;
};

export type QueryState =
  | { status: "idle" }
  | { status: "loading" }
  | { status: "success"; url: string; data: unknown }
  | { status: "error"; url: string; message: string };

/**
 * Agent data in the /api/v1/agents/{id} response. Defines fields shared by all types and optional type-specific
 * fields.
 */
export type AgentData = {
  agent_id: number;
  name: string;
  type: number;
  variant: number;
  /** Landmark type (landmarks master ID). 0 means not a landmark. */
  landmark?: number;
  status: number;
  /** Reason for the current status (REASON_*). 0 (REASON_NONE) means a normal transition. */
  status_reason?: number;
  alert?: number;
  status_from: number;
  reserves: number;
  revenues: number[];
  expenses: number[];
  public_facility: boolean;
  zone_id: number;
  zone_name: string;
  building_agent_id: number;
  building_agent_name: string;
  is_bd: boolean;
  bd_type: number;
  construction_progress: number;
  created_date?: number;
  parcel_id?: number;
  /** Whether the containing parcel is under land acquisition negotiation (backend-resolved parcel under_acquisition). */
  under_acquisition?: boolean;
  attraction?: number;
  durability?: number;
  floors?: number;
  height?: number;
  floor_area?: number;
  base_area?: number;
  volume_ratio?: number;
  under_maintenance?: boolean;
  /** Building parking capacity in vehicles. Zero for non-buildings. */
  parking_capacity?: number;
  /**
   * Daytime/nighttime parking usage this epoch, in vehicles. Tenant and household demand is assigned to their
   * host building. May exceed capacity, with overflow to parking facilities or streets. Derived and not saved;
   * zero after loading until the next epoch.
   */
  parking_use_day?: number;
  parking_use_night?: number;
  wage?: number;
  quota?: number;
  total_labor?: number;
  required_skill?: number;
  family_type?: number;
  employees?: AgentSummary[];
  members?: CitizenMember[];
  tenants?: AgentSummary[];
  students?: AgentSummary[];
  rent?: number;
  capacity?: Record<string, number>;
  occupied?: Record<string, number>;
  demand_amount?: Record<string, number>;
  supply_amount?: number;
  supply_product_id?: number;
  supply_price?: number;
  supply_production?: number;
  supply_sold_amount?: number[];
  profit_rates?: number[];
  utilization?: number[];
  satis_nutrition?: number;
  satis_healthcare?: number;
  satis_leisure?: number;
  satis_residential?: number;
  /** Tourism experience fulfillment, a component of tourist household (VARIANT_TOURIST) satisfaction. */
  satis_tourism?: number;
  discretionary_income?: number;
  env_score?: number;
  qol_score?: number;
  has_automobile?: boolean;
  education_type?: Record<string, number>;
  /**
   * Enrolled students by skill (skill ID → count), for education facilities (TYPE_EDUCATION) only. Manually
   * injected by the backend because not yet included in generated endpoints.yaml.
   */
  enrolled_by_skill?: Record<string, number>;
  /** Total capacity from total floor area. Manually injected only for education facilities (TYPE_EDUCATION). */
  max_students?: number;
  is_evicted?: boolean;
  realestate_amount?: number;
  user_operations?: unknown[];
  /**
   * Inventory remaining after consumption (product ID → amount). Procurement increases it; production material
   * consumption and similar uses decrease it. This is not acquired quantity (industrial materials carry over
   * across epochs).
   */
  remain_amount?: Record<string, number>;
  /**
   * Acquired quantity this epoch (product ID → amount): gross receipts from market procurement (including
   * self-supply), infrastructure delivery, vehicle delivery, and natural resource distribution, unaffected by
   * consumption. Reset for all goods at epoch start, so it represents acquisitions in the previous epoch. Use
   * for acquired quantity and infrastructure connection receipt displays.
   */
  received_amount?: Record<string, number>;
  utilization_target?: number;
  /**
   * Abandonment flag. true means the player has stopped maintaining this public building. Abandoned buildings
   * receive no automatic repairs and deteriorate naturally. Only public facilities that are buildings
   * (is_bd=true) may be abandoned. If operating when abandoned, they stop operating.
   */
  abandoned?: boolean;
  /**
   * Infrastructure network terminal node ID (connection point). 0 means no terminal. If equal to depot_node_id,
   * it denotes a shared connection node.
   */
  infra_node_id?: number;
  /**
   * Running network (public transport) terminal node ID (line connection point). 0 means no terminal. Used by
   * depots and similar facilities; electrified railway depots share the node with infra_node_id.
   */
  depot_node_id?: number;
};

/**
 * Compact agent reference for employee/tenant lists, etc. Matches the backend referrer method's {id, name}
 * format (same shape as RefItem in types/generated.ts).
 */
export type AgentSummary = {
  id: number;
  name: string | null;
};

/**
 * Household member information for the household detail member list. Supplied by the backend as id/name pairs.
 */
export type CitizenMember = {
  id: number;
  name: string;
};

/**
 * Citizen data in the /api/v1/citizens/{id} response.
 */
export type CitizenData = {
  citizen_id: number;
  name: string;
  citizen_type: number;
  age: number;
  gender: number;
  household_agent_id: number;
  household_agent_name: string;
  employer_agent_id: number;
  employer_agent_name: string;
  school_agent_id: number;
  school_agent_name: string;
  skills: Record<string, number>;
  education_skill: number;
  potential_criminality: number;
  commute_time: number;
};

/**
 * Complete /api/v1/citizens/{id} response.
 */
export type CitizenResponse = {
  citizen: CitizenData;
};

/**
 * Complete /api/v1/agents/{id} response.
 */
export type AgentResponse = {
  economic_agent: AgentData;
};

export type ZoneData = {
  zone_id: number;
  name: string;
  color: string;
  polygon: number[][];
  area: number;
  center_point: number[];
  /** true = automatically managed district (may be merged by the system); false = finalized district confirmed by the user. */
  auto_managed: boolean;
  allowed_bd_mask: number;
  limit_coverage_ratio: number | null;
  limit_volume_ratio: number | null;
  limit_height: number | null;
  zone_average_prices: Record<string, number>;
  average_wage: number[];
  average_rent: number[];
  average_land_price: number[];
  zone_population: number[];
  daytime_population: number[];
  zone_unemployment_rate: number[];
  space_demands: Record<string, number>;
  space_supplies: Record<string, number>;
  zone_supply: Record<string, number>;
  zone_demand: Record<string, number>;
};

/**
 * One pending tenant demand that cannot be built because parcels are too small (location failure report for slot
 * starvation). Volatile data rebuilt by ConstructionPhase each epoch; empty immediately after loading.
 */
export type ZoneSlotStarvation = {
  space_type: number;
  variant: number;
  /** Pending tenant industry name (master display_name). */
  tenant_name: string;
  /** Required floor area per building (m2). */
  required: number;
  /** Maximum capacity per building achievable with the district's building pattern (m2). */
  max_capacity: number;
  /** Number of tenants awaiting move-in. */
  count: number;
};

/**
 * Complete /api/v1/zones/{id} response.
 */
export type ZoneResponse = {
  zone: ZoneData;
  /** Additional information returned only by GET /zones/{id}, not list responses. */
  slot_starvation?: ZoneSlotStarvation[];
};

/**
 * One interchange (IC/JCT), returned by GET /api/v1/interchanges/ and /{id}. Automatically detected from ramp
 * groups by the backend; handwritten because absent from generated.ts. All lists are ascending; coordinates are
 * horizontal [x, z].
 */
export type InterchangeSummary = {
  interchange_id: number;
  /** Player-assigned name. Empty means unnamed; the display creates a fallback from the ID. */
  name: string;
  /** INTERCHANGE_KIND_IC (has ordinary road arms) / INTERCHANGE_KIND_JCT (all arms are motorways). */
  kind: number;
  center: [number, number];
  /** Convex hull of the ramp group expanded by a margin (without repeating the first point). */
  polygon: [number, number][];
  /** Internal paths (road paths with both ends inside the interchange). */
  path_ids: number[];
  ramp_path_ids: number[];
  node_ids: number[];
  /** Connections to external roads (portal nodes and arm paths with only one end inside). */
  arms: { node_id: number; path_id: number }[];
  /** Reachable arm pairs [entry arm path ID, exit arm path ID] without acute turns. Excludes U-turns returning to the same arm. */
  connections: [number, number][];
};

/** Road information per arm returned by GET /api/v1/interchanges/{id}. */
export type InterchangeArmDetail = {
  node_id: number;
  path_id: number;
  /** Arm road type (PATH_STREET / PATH_MOTORWAY). null if the road is not found. */
  path_type: number | null;
  /** Section name of the arm path. Empty if unnamed. */
  path_name: string;
  /** Road the arm path belongs to. VALUE_NONE if none. */
  road_id: number;
  road_name: string;
  zone_id: number;
  /** Containing district name. null if not in a district. */
  zone_name: string | null;
  /** Bearing from the interchange center to the portal node (clockwise degrees from north = +Z). null if the node is not found. */
  bearing_deg: number | null;
};

/** GET /api/v1/interchanges/ response. version increments whenever list contents change. */
export type InterchangeListResponse = {
  version: number;
  interchanges: InterchangeSummary[];
};

/** GET /api/v1/interchanges/{id} response. */
export type InterchangeResponse = {
  interchange: InterchangeSummary;
  arms: InterchangeArmDetail[];
};

/**
 * Parcel data in the /api/v1/parcels/{id} response. Fields follow the single_parcel schema.
 */
export type ParcelData = {
  parcel_id: number;
  parcel_type: number;
  status: number;
  polygon: number[][];
  center_point: number[];
  usage_id: number;
  zone_id: number;
  zone_name: string | null;
  resource_id: Record<string, number>;
  name_resource?: Record<string, string | null>;
  front_path_id: number;
  front_path_name: string | null;
  facade_edge_index: number;
  owner_agent_id: number;
  owner_agent_name: string | null;
  land_price: number;
  restriction_override: boolean;
  /** Whether under acquisition negotiation with the city government for road construction, etc. Remains privately owned until agreement. */
  under_acquisition: boolean;
  allowed_bd_mask: number;
  limit_coverage_ratio: number | null;
  limit_volume_ratio: number | null;
  limit_height: number | null;
  area: number;
  altitude: number;
  /**
   * Whether a path crosses directly below (shallow underground) or above the parcel (0/1). Private buildings do
   * not spawn on parcels with 1 (public facilities can be placed manually). Optional because responses before
   * endpoints.yaml regeneration omit it.
   */
  has_crossing_path?: number;
  user_operations: unknown[];
};

/**
 * Base master entry containing only a display name. Many master endpoints return this structure.
 */
export type MasterNameEntry = {
  display_name: string;
};

/**
 * Building type master (bd_types).
 */
export type BdTypeEntry = MasterNameEntry;

/**
 * Skill master (skills).
 */
export type SkillEntry = MasterNameEntry;

/**
 * Vehicle type master (vehicle_types). category identifies the transport category (0: walking, 1: road, 2:
 * rail).
 */
export type VehicleTypeEntry = {
  display_name: string;
  category: number;
};

/**
 * Space category master (space_categories).
 */
export type SpaceCategoryEntry = MasterNameEntry;

/**
 * Product type master (product_types). Entry 4 is null (externality goods have no type display name).
 */
export type ProductTypeEntry = MasterNameEntry | null;

/**
 * Parcel type master (parcel_types).
 */
export type ParcelTypeEntry = MasterNameEntry;

/**
 * Path type master (path_types).
 */
export type PathTypeEntry = MasterNameEntry;

/**
 * Natural resource type master (resource_types). product_id identifies the extracted product (used for resource
 * detail icons, etc.).
 */
export type ResourceTypeEntry = {
  display_name: string;
  product_id: number;
};

/**
 * Terrain prop type master (prop_types). Defines decorative types such as forests, rocks, and bushes. prefab is
 * the 3D model path loaded by graphics (Unity). If unset, Unity resolves from prop_type
 * (Resources/Models/Props/{id}).
 */
export type PropMasterEntry = {
  display_name: string;
  prefab?: string;
};

/**
 * Agent type master (agent_types).
 */
export type AgentTypeEntry = {
  display_name: string;
  description: string;
};

/**
 * Agent master (agents). Detailed definitions by variant; available fields depend on type and the building flag.
 *
 * - type=0 (district): common fields only.
 * - type=1,2 (city government, school): operating parameters such as required_skill.
 * - type=3 (household): common fields only.
 * - type=4,5 (import/export): investment and demand parameters.
 * - type=10 (private facility): supply, materials, aggregation parameters, etc.; with building=true, also
 * height_per_floor, space_limits, etc.
 * - type=20 (building definition): max_floors, min_floors, and building geometry parameters.
 * - type=30 (open space): only building=true building fields; no supply / labor / space_limits.
 */
export type AgentMasterEntry = {
  display_name: string;
  type: number;
  building: boolean;
  description: string;
  space_type: number;
  /** Higher education flag for education facilities (type=2). true = university (admission capacity is editable). */
  is_higher_education?: boolean;
  required_skill?: number;
  max_skill?: number;
  skilled_people?: number;
  skill_multiplier?: number;
  investment_factor?: number;
  space_need?: number;
  space_min?: number;
  space_max?: number;
  labor_need?: number;
  labor_min?: number;
  labor_max?: number;
  max_traffic_sales_bonus?: Record<string, number>;
  max_cluster_bonus?: number;
  max_group_cluster_bonus?: number;
  agglomeration_limit?: number;
  cluster_radius?: number;
  supply?: number;
  materials?: Record<string, number>;
  investments?: Record<string, number>;
  cluster_group?: number;
  height_per_floor?: number;
  shared_building?: boolean;
  space_limits?: Record<string, number>;
  bd_type?: number;
  // Building construction panel categories (BD_CATEGORY_* in constants.ts), present only on building variants.
  // Uncategorized buildings appear under Other (BD_CATEGORY_OTHER). Separate from building regulation bd_type.
  bd_category?: number;
  building_shape?: number;
  cost_ratio?: number;
  min_space?: number;
  max_space?: number;
  max_floors?: number;
  min_floors?: number;
  // Reserve capacity multiplier. Positive values use the public reserve method: production target = smoothed faced demand ×
  // reserve multiplier × target utilization, enabling the target utilization slider. Unset/0 uses normal demand-following planning.
  production_headroom?: number;
  // Whether the footprint is resizable. false fixes it to a default_width × default_height
  // rectangle, without coverage-ratio or parcel-shape generation. Unset means resizable (legacy behavior).
  resizable?: boolean;
  // Initial footprint width for direct placement and fixed footprint width in m when resizable=false.
  default_width?: number;
  // Initial footprint depth for direct placement and fixed footprint depth in m when resizable=false. Square if unset.
  default_height?: number;
  // Building mesh generation method (MODEL_* in constants.ts). MODEL_MASHUP (7) buildings
  // consist of multiple subbuildings from the master and can open a placement editor from details.
  model_type?: number;
  // Variant placeable only in scenario authoring mode. Hide from normal-play construction panels.
  template_only?: boolean;
  // Slum building created only through urban sprawl. Hide from normal-play construction panels.
  sprawl_only?: boolean;
  // Compatible parcel type (PARCEL_* in constants.ts). Build only on parcels with exactly matching
  // parcel_type. Unset means PARCEL_LAND, matching backend is_building_compatible_with_parcel_type.
  parcel_type?: number;
};

/**
 * Landmark master (landmarks). An independent axis for unique attractions such as Tokyo Tower without adding
 * variants. All use one agent_variant (the landmark variant); this ID selects the attraction. Capacity derives
 * from total floor area and is not stored here.
 */
export type LandmarkMasterEntry = {
  display_name: string;
  description?: string;
  /** Agent variant owning this landmark (agents master ID). */
  agent_variant: number;
  /** Identifier used for Unity's part folder name (landmark-{model_prefix}). */
  model_prefix: string;
  /** Fixed site dimensions in m, defined per landmark and not player-editable. */
  width: number;
  depth: number;
  /** Logical floor count and floor height in m. Total floor area = footprint area × floor count determines capacity. */
  floors: number;
  height_per_floor: number;
  /** Construction cost multiplier relative to a normal building with the same total floor area. */
  cost_multiplier: number;
};

/**
 * Subbuilding master (subbuildings). Defines wings such as classrooms/gymnasiums composing a mashup building
 * (MODEL_MASHUP). agent_variant refers to the host facility variant. width / height are footprint dimensions in
 * m; unset wings occupy one square cell (MASHUP_CELL_SIZE).
 */
export type SubBuildingMasterEntry = {
  display_name: string;
  agent_variant: number;
  /** Whether this is the facility's main wing. Random layouts always place one at the front (road-facing edge). */
  primary?: boolean;
  /** Whether this is a unique wing placed exactly once in random layouts (priority after primary; omitted if no space). */
  sole?: boolean;
  model_prefix?: string;
  /** Footprint width in cells (one unit = MASHUP_CELL_SIZE m; defaults to one cell). */
  width?: number;
  /** Footprint depth (same units). */
  height?: number;
  min_floors?: number;
  max_floors?: number;
  height_per_floor?: number;
};

/**
 * Product master (products).
 */
export type ProductMasterEntry = {
  display_name: string;
  name: string;
  unit: string;
  type: number;
  regulated: boolean;
  public_service: boolean;
  trade: boolean;
  self_sustain: number;
  transport_amount: number;
  price_elasticity: number;
  demand_tier: number;
  demand_min: number;
  demand_max: number;
  demand_weight: number;
  demand_atom: boolean;
};

/**
 * Path master (paths). sidewalk_width, side_width, and tree_spacing are null when unset.
 */
export type PathMasterEntry = {
  streetlight_type_allowed: boolean;
  default_streetlight_type: number;
  fence_type_allowed: boolean;
  default_fence_type: number;
  street_tree_type_allowed: boolean;
  default_street_tree_type: number;
  bench_type_allowed: boolean;
  default_bench_type: number;
  bin_type_allowed: boolean;
  default_bin_type: number;
  planter_type_allowed: boolean;
  default_planter_type: number;
  advert_type_allowed: boolean;
  default_advert_type: number;
  utility_mode_allowed: boolean;
  default_utility_mode: number;

  display_name: string;
  description: string;
  sidewalk_width: number | null;
  side_width: number | null;
  tree_spacing: number | null;
  lanes: number;
  type: number;
  width: number;
  capacity_base: number;
  /** Variant default speed limit (km/h), defined in master.yaml. */
  default_speed_limit: number;
  /** Whether elevation is editable. Matches height_editable in master.yaml. */
  height_editable: boolean;
  /** Allow roadside parcel generation and road access designation. false for through-only streets such as bridges. */
  frontage_allowed: boolean;
  /**
   * Variant default for terrain following, matching master.yaml follow_terrain (unset = true). Used when the
   * path-specific follow_terrain is unset.
   */
  follow_terrain?: boolean;
  /**
   * Minimum allowed height in m, matching master.yaml min_height. Only variants with negative values
   * (underground roads/subway tracks, etc.) can be built underground (height<0). Unset means 0 (underground
   * prohibited).
   */
  min_height?: number | null;
  /**
   * Whether this is an interchange ramp variant, matching master.yaml ramp. The backend detects interchanges
   * from connected ramp components (unset = false).
   */
  ramp?: boolean;
  /** Variant available only in scenario authoring (unset = false). Hide from normal-play construction, rebuilding, and line options. */
  template_only?: boolean;
  /**
   * Mesh generator type. PathMeshFactory selects RoadPath / TrackPath / none from this value: "default" →
   * RoadPath, "track" → TrackPath, "none" → no mesh generation. Unset means "default".
   */
  model_type?: string;
  /**
   * Whether curved placement is allowed. false hides the UI straight/curve toggle and allows straight paths
   * only. Unset means true.
   */
  curve_allowed?: boolean;
  /** Alignment constraint: minimum allowed curve radius in m. 0/unset means unrestricted. */
  min_curve_radius?: number;
  /** Alignment constraint: maximum allowed longitudinal gradient in %. 0/unset means unrestricted. */
  max_grade?: number;
  /**
   * Structural constraint: maximum length in m of one non-terrain-following span supported by pillars/girders.
   * Matches master.yaml max_span. Does not apply to ground spans attached to terrain. 0/unset means
   * unrestricted.
   */
  max_span?: number | null;
  /**
   * Station path platform width in m, matching master.yaml platform_width. Only single-track station variants
   * with positive values allow platform_side selection during construction. Integrated double-track stations
   * with lanes≥2 define platform positions per variant: outside side platforms or an island with center_width.
   * Unset/null means no platform (ordinary roads/tracks).
   */
  platform_width?: number | null;
  /**
   * Track structure type (tracks, monorails, people movers), matching master.yaml track_type. Numeric values
   * match the shared TRACK_* constants (TRACK_NORMAL=normal / TRACK_BRIDGE=bridge / TRACK_STATION=station). Sort
   * construction panel track variants by normal/bridge/station structure. Unset means TRACK_NORMAL; unused for
   * non-track variants.
   */
  track_type?: number;
  /**
   * Whether pedestrians can board/alight at this people mover path (PATH_CONVEYOR) section's endpoints.
   * master.yaml boardable; true for ropeway station sections, etc. Unset for other paths.
   */
  boardable?: boolean;
  /** Fixed boarding wait at people mover boarding nodes in minutes. master.yaml board_wait_min. */
  board_wait_min?: number;
  /**
   * Product IDs of infrastructure goods carried by this variant, matching master.yaml infra_products.
   * Electrified tracks include electricity (PRODUCT_HIGH_ELECTRICITY=401); construction panels use the array to
   * detect electrification and sort variants. Unset/empty excludes the variant from infrastructure delivery
   * networks (non-electrified tracks, ordinary roads, etc.).
   */
  infra_products?: number[];
  /**
   * Dimensions and lateral offset in m of exclusive building areas at both ends of station building paths
   * (type=PATH_STATION_HOUSE). house_width is perpendicular to the line; house_depth runs along it. Rectangles
   * extend outward from the endpoint by house_depth, with their inner edge on the endpoint. house_offset shifts
   * laterally, positive to the right when facing outward. Matches master.yaml house_width / house_depth /
   * house_offset (unset for other variants).
   */
  house_width?: number | null;
  house_depth?: number | null;
  house_placement?: "outside" | "inside";
  house_offset?: number | null;
  /** Passage mode for new station buildings. Defaults to elevated. Does not apply to saved paths. */
  default_passage_mode?: "ground" | "elevated" | "underground";
};

/**
 * Combined path component (one parallel path).
 * - path_variant: path_master variant ID.
 * - lateral_offset: offset from the centerline in m, positive to the right facing placement direction
 * (start→end).
 * - height_offset: vertical offset from the centerline in m.
 * - travel_direction: 0=bidirectional / 1=forward (start→end) / 2=reverse (end→start).
 */
export type CombinedPathComponent = {
  path_variant: number;
  lateral_offset: number;
  height_offset: number;
  travel_direction: number;
  /**
   * Station path platform side (PLATFORM_SIDE_OUTSIDE/INSIDE/BOTH). Used by double-track station presets (side
   * platforms = outside×2; island = inside×2). Omit if unset to use the backend default (outside).
   */
  platform_side?: number;
};

/**
 * Combined path master (combined_paths). Presets placing multiple path_master variants from one centerline (such
 * as double tracks). The frontend expands the centerline by each component's offsets for bulk creation. Combined
 * paths are not stored as backend entities; only the expanded individual paths exist.
 */
export type CombinedPathEntry = {
  display_name: string;
  description?: string;
  components: CombinedPathComponent[];
};

/**
 * Public transport master (transit). Definitions by line type (bus, metro, etc.).
 * - transit_path_id: path_master variant for this type's section paths (line type key); pass as path_variant to
 * line creation APIs.
 * - stop_path_variants: underlying path variants allowing stop nodes.
 * - route_path_variants: underlying path variants the line's vehicles can traverse.
 * - vehicle_type: VEHICLE_* vehicle type. Unity automatically draws platforms at VEHICLE_TRAM (LRT) stops.
 */
export type TransitMasterEntry = {
  display_name: string;
  description: string;
  transit_path_id: number;
  stop_path_variants: number[];
  route_path_variants: number[];
  vehicle_type?: number;
  /** Freight line total car count includes one locomotive that carries no cargo. */
  freight?: boolean;
};

/**
 * Line tour point (element of points in POST /api/v1/transit/lines or PUT .../lines/{id}/route). Ordered
 * cyclically, returning from the last point to the first. stop = stop; waypoint = mandatory pass-through without
 * stopping.
 */
export type TransitRoutePoint = {
  kind: "stop" | "waypoint";
  /** World coordinates [x, y, z]. */
  position: [number, number, number];
};

/**
 * Line financial estimate (response to POST /api/v1/transit/lines/estimate or /lines/{id}/estimate; handwritten
 * JSON). Assumes all vehicles can be procured; all amounts are per epoch. Numeric values are null when ok=false.
 */
export type TransitLineEstimate = {
  ok: boolean;
  /**
   * Reason estimation is unavailable: suspension codes (route_disconnected / no_underlay / train_too_long),
   * insufficient_stops (fewer than two stops), invalid_stop (invalid stop/waypoint location), or no_distance
   * (zero travel distance).
   */
  reason: string | null;
  fare_price: number;
  target_headway: number;
  cars_per_train: number;
  simulation_days: number;
  /** Occupancy assumed for revenue estimates (0–1). */
  load_factor: number;
  round_trip_length_m: number | null;
  round_trip_minutes: number | null;
  train_count: number | null;
  vehicles_required: number | null;
  /** Effective headway in minutes. A short line can turn around sooner than the target even with one train. */
  effective_headway: number | null;
  /** Daily complete circuit count (each line section carries this many trips). */
  trips_per_day: number | null;
  /** Daily transport capacity in people: trips per day × capacity per train. */
  capacity_per_day: number | null;
  /** Daily boardings at assumed occupancy (assume passengers turn over twice per circuit, once each way for a round-trip service). */
  riders_per_day: number | null;
  revenue: number | null;
  /** Lease fee per vehicle per epoch. */
  lease_price: number | null;
  /** Lease fee source: depot = depot supply price / fixed = fixed public service price / estimated = estimated depot cost. */
  lease_price_basis: "depot" | "fixed" | "estimated" | null;
  operating_cost: number | null;
  balance: number | null;
  /** Occupancy required for fares to cover operating costs. Above 1 means a loss even at full capacity. */
  break_even_load_factor: number | null;
  /** Whether an operating depot connects to the line's running network (null when estimation is unavailable). */
  depot_connected: boolean | null;
  /**
   * Tracks exceeding capacity if these settings are confirmed (same prevalidation as creation/update). For track
   * overload, load/capacity are trips per track per day. For single_track overload, they are daily
   * occupied/operating minutes; min_headway is this line's minimum required headway in minutes (null if other
   * lines already fill capacity).
   */
  track_overloads: {
    path_id: number;
    load: number;
    capacity: number;
    single_track: boolean;
    min_headway?: number | null;
  }[];
};

/**
 * Budget category master (budget_categories).
 */
export type BudgetCategoryEntry = MasterNameEntry;

/**
 * Each nearby_zones entry contains zone_id and zone_name for a district containing an adjacent path.
 */
/**
 * Alignment metrics for the path being placed (usePathConstruction aggregates construction-cost alignment). Used
 * by ConstructionInfoCard for traversal speed/alignment constraints and PathBuildSections to block confirmation.
 */
export type PathAlignmentSummary = {
  /** Geometry-derived traversal speed in km/h. */
  baseSpeed: number;
  /** Speed limit in km/h. */
  maxSpeed: number;
  /** Minimum curve radius in m. null means straight. */
  minCurveRadius: number | null;
  /** Maximum longitudinal gradient in %. */
  maxGrade: number;
  /** Standard minimum curve radius in m. 0 means unrestricted. */
  curveRadiusLimit: number;
  /** Standard maximum gradient in %. 0 means unrestricted. */
  gradeLimit: number;
  /** Some sections fall below the standard minimum curve radius. */
  curveViolated: boolean;
  /** Some sections exceed the standard maximum gradient. */
  gradeViolated: boolean;
};

export type NearbyZoneEntry = {
  zone_id: number;
  zone_name: string;
};

/**
 * Path data in the /api/v1/paths/{id} response, following the single_path schema. max_speed, utilization, and
 * construction_progress use method: other; backend implementations may omit values (allow null).
 */
export type PathData = {
  travel_direction: number;
  house_ends: string;
  passage_mode: string;
  streetlight_type: number;
  fence_type: number;
  street_tree_type: number;
  bench_type: number;
  bin_type: number;
  planter_type: number;
  advert_type: number;
  utility_mode: number;

  path_id: number;
  name: string;
  path_type: number;
  path_variant: number;
  status: number;
  alert?: number;
  polygon: number[][];
  under_maintenance: boolean | null;
  zone_id: number;
  zone_name: string | null;
  nearby_zones?: NearbyZoneEntry[] | null;
  noise: number | null;
  air_pollution: number | null;
  max_speed: number | null;
  override_max_speed: boolean;
  /** Geometry-derived traversal speed in km/h, calculated from speed limit, curve radius, and gradient. Excludes intersection slowing/stopping. */
  base_speed?: number | null;
  /** Minimum curve radius within the path in m. Straight paths use 1e6. */
  min_curve_radius?: number | null;
  /** Maximum longitudinal gradient within the path in %. */
  max_grade?: number | null;
  /** Per-path terrain following (-1=unset [variant default] / 0=off / 1=on). */
  follow_terrain?: number;
  path_traffic: Record<string, number> | null;
  utilization: number | null;
  /**
   * Street parking vehicle count (larger of day/night) and legal capacity. Only operating ground-level local
   * roads have capacity; counts may exceed it. Both are derived and not saved, so zero after loading until the
   * next epoch.
   */
  parked_cars?: number;
  street_parking_capacity?: number;
  utilization_target?: number | null;
  durability: number | null;
  construction_progress: number | null;
  parcel_id?: number;
  start_node_id?: number;
  end_node_id?: number;
};

/**
 * All master data fetched in bulk from the backend. Property keys are numeric backend IDs. JSON serializes keys
 * as strings; convert to numbers on fetch.
 */
export type MasterData = {
  climates: Record<number, import("./utils/climate").ClimatePreset>;
  weather: Record<string, import("./utils/climate").WeatherMasterEntry>;
  bd_types: Record<number, BdTypeEntry>;
  skills: Record<number, SkillEntry>;
  vehicle_types: Record<number, VehicleTypeEntry>;
  space_categories: Record<number, SpaceCategoryEntry>;
  product_types: Record<number, ProductTypeEntry>;
  parcel_types: Record<number, ParcelTypeEntry>;
  path_types: Record<number, PathTypeEntry>;
  agent_types: Record<number, AgentTypeEntry>;
  agents: Record<number, AgentMasterEntry>;
  subbuildings: Record<number, SubBuildingMasterEntry>;
  landmarks: Record<number, LandmarkMasterEntry>;
  products: Record<number, ProductMasterEntry>;
  paths: Record<number, PathMasterEntry>;
  transit: Record<number, TransitMasterEntry>;
  combined_paths: Record<number, CombinedPathEntry>;
  budget_categories: Record<number, BudgetCategoryEntry>;
  resource_types: Record<number, ResourceTypeEntry>;
  prop_types: Record<number, PropMasterEntry>;
};

/**
 * Summary of public service supplying facilities. Each facility entry from GET
 * /api/v1/city/public-service-supply?include_facilities=true.
 */
export type PublicFacilityAgent = {
  agent_id: number;
  name: string;
  variant: number;
  status: number;
  reserves: number;
  production_capacity: number | null; // null = facility without a space constraint (space_need<=0).
  balance: number;
  utilization_rate: number;
};

/**
 * Public agents listed in the city government window. Entries from GET /api/v1/city/government-agents. Metrics
 * depend on agent type; absent metrics are undefined.
 * - Real estate: supply_floor / occupancy_rate / rent / balance / utilization_rate.
 * - Import/export hubs: capacity / import_handling / export_handling (all in t) / utilization_rate.
 * - Businesses: production / sold_amount / price / balance / utilization_rate.
 * - Others: none.
 */
export type GovernmentAgent = {
  agent_id: number;
  name: string;
  status: number;
  supply_floor?: number;
  occupancy_rate?: number;
  rent?: number;
  capacity?: number;
  import_handling?: number;
  export_handling?: number;
  production?: number;
  sold_amount?: number;
  price?: number;
  balance?: number;
  utilization_rate?: number;
};

/** Public service pricing mode: fixed price or cost ratio regulation (no market fluctuations). */
export type PublicServicePriceMode = "fixed" | "cost_ratio";

/**
 * One public service price row from GET/PATCH /api/v1/city/public-service-prices. Each regulated product holds
 * its pricing mode (fixed price/cost ratio), configured value, average public facility production cost, and city
 * average price.
 * - mode="fixed": value = supply price itself.
 * - mode="cost_ratio": value = production cost multiplier (supply price = production cost × value).
 */
export type PublicServicePrice = {
  product_id: number;
  mode: PublicServicePriceMode;
  value: number;
  unit_cost: number;
  average_price: number;
  supply_capacity: number;
  max_demand: number;
  demand: number;
  production: number;
};

/**
 * One public service supply/demand row from GET /api/v1/city/public-service-supply. Supply capacity, demand, and
 * production per regulated product. Unlike PublicServicePrice, also includes infrastructure goods without prices
 * (high-voltage electricity/high-pressure water). All supply/demand displays use this type.
 */
export type PublicServiceSupply = {
  product_id: number;
  supply_capacity: number;
  demand: number;
  production: number;
  /** Supplying facility list, included only when fetched with include_facilities=true. */
  facilities?: PublicFacilityAgent[];
};

export type PublicServiceSupplyData = {
  services: PublicServiceSupply[];
};

// ---------------------------------------------------------------------------
// Achievement quests (quest). Types corresponding to the core API.
// Conditions have three layers:
// metric resolution × temporal derivation × logical composition.
// See city-genesis/docs/ja/economics/16_スコアと達成目標.md for concepts.
// ---------------------------------------------------------------------------

/** Quest condition metric reference: how to select a numeric value on the city object. */
export type QuestMetric = {
  /** Dot-separated attribute/dictionary path (e.g. "population", "budgets.3"). */
  path: string;
  /** Key when path resolves to a dictionary (such as a product ID). Alternative syntax equivalent to dot notation. */
  index?: number | string | null;
  /** Aggregation method when path resolves to a dictionary. */
  agg?: "sum" | "mean" | "max" | "min" | "count" | null;
};

/** Temporal derivation mode: which time reference is used to evaluate the metric. */
export type QuestClauseMode = "value" | "delta" | "delta_pct" | "moving_avg" | "streak";

/** Quest comparison operator. */
export type QuestCompareOp = ">" | "<" | ">=" | "<=" | "==" | "!=";

/** Single quest condition clause: one metric and one comparison. */
export type QuestClause = {
  metric: QuestMetric;
  mode?: QuestClauseMode;
  /** moving_avg window width / required consecutive epochs for streak. */
  window?: number;
  compare: QuestCompareOp;
  /** Constant on the right side of comparison. Ignored when target_metric is set. */
  target?: number | null;
  /** Use another metric on the right side for relative comparisons (such as education budget >= fire service budget). */
  target_metric?: QuestMetric | null;
};

/** Composite quest condition node combining clauses with all / any / not. */
export type QuestCondition = {
  op: "all" | "any" | "not";
  clauses: (QuestClause | QuestCondition)[];
};

/** Quest completion notification method. */
export type QuestNotify = "modal" | "toast" | "off";

/** Quest lifecycle state. */
export type QuestStatus = "locked" | "active" | "completed" | "failed";

/** Quest completion reward. */
export type QuestReward = {
  /** grant_money: inject a grant into city government cash. */
  type: "grant_money";
  amount: number;
};

/**
 * Evaluation state of each condition clause for one quest from GET /api/v1/quests. Used for progress in
 * list/detail views.
 */
export type QuestClauseState = {
  clause_key: string;
  metric: QuestMetric | null;
  mode: QuestClauseMode;
  window: number;
  compare: QuestCompareOp;
  target: number | null;
  current: number | null;
  satisfied: boolean | null;
  /** Current consecutive completed epochs in streak mode. */
  streak: number | null;
  progress: number;
};

/** One quest from GET /api/v1/quests. Names/descriptions are author-written multilingual text (display using resolveLocalizedText). */
export type Quest = {
  id: number;
  name: LocalizedText;
  description: LocalizedText;
  condition: QuestCondition;
  latch: boolean;
  notify: QuestNotify;
  deadline_epoch: number | null;
  deadline_within: number | null;
  prerequisites: number[];
  reward: QuestReward | null;
  category: string;
  hidden: boolean;
  is_force_completable: boolean;
  status: QuestStatus;
  activated_epoch: number;
  completed_epoch: number | null;
  /** Measured clause values at completion (clause_key → {current, target, compare}). */
  achieved_values: Record<string, { current: number | null; target: number | null; compare: string }>;
  /** Overall quest progress 0..1 (calculated by server). */
  progress: number;
  clause_states: QuestClauseState[];
};

/**
 * One completed_quests / failed_quests entry in the epoch execution response. notify selects celebration window
 * (modal), notification popup (toast), or no notification (off).
 */
export type QuestNotification = {
  id: number;
  name: LocalizedText;
  description: LocalizedText;
  latch: boolean;
  notify: QuestNotify;
  category: string;
  hidden: boolean;
  reward: QuestReward | null;
  achieved_values: Record<string, { current: number | null; target: number | null; compare: string }>;
  completed_epoch: number | null;
  status: string;
};
