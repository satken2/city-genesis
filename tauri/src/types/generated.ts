/**
 * endpoints.yaml から自動生成された型定義。
 *
 * このファイルは手動で編集しないでください。
 * 変更する場合はスプレッドシートを編集し、コア側の型定義生成手順で再生成してください。
 */

/** referrer メソッドが返す {id, name} オブジェクト */
export type RefItem = {
  id: number;
  name: string | null;
};

export type CitizenSummary = {
  /** Unique citizen ID. */
  citizen_id: number;
  /** Randomly generated citizen name. */
  name: string;
  /** Role in the household, such as head of household, spouse, or child. See the shared MEMBER_* constants. */
  citizen_type: number;
  /** Age in years. */
  age: number;
  /** Gender (0 = male, 1 = female). */
  gender: number;
};

export type CitizenData = {
  /** Unique citizen ID. */
  citizen_id: number;
  /** Randomly generated citizen name. */
  name: string;
  /** Role in the household, such as head of household, spouse, or child. See the shared MEMBER_* constants. */
  citizen_type: number;
  /** Age in years. */
  age: number;
  /** Gender (0 = male, 1 = female). */
  gender: number;
  /** One-way commute time in minutes. */
  commute_time: number;
  /** Whether the citizen immigrated from outside the city (0 or 1). */
  is_immigrant: number;
  /** ID of the citizen's household agent. */
  household_agent_id: number;
  /** Name of the household identified by household_agent_id. */
  household_agent_name?: string;
  /** ID of the employer agent (VALUE_NONE if unemployed). */
  employer_agent_id: number;
  /** Name of the employer identified by employer_agent_id. */
  employer_agent_name?: string;
  /** ID of the school agent the citizen attends (VALUE_NONE if the citizen does not attend school). */
  school_agent_id: number;
  /** Name of the school identified by school_agent_id. */
  school_agent_name?: string;
  /** Maps skill IDs to proficiency levels. Used to determine effective labor and skill levels. */
  skills: Record<string, number>;
  /** Whether the citizen has completed higher education (0 or 1). */
  educated: boolean;
  /** ID of the skill the citizen is learning in higher education (VALUE_NONE if the citizen is not enrolled). */
  education_skill: number;
  /** Propensity to commit crime (0.0 to 1.0). Used to determine the probability of a crime occurring. */
  potential_criminality: number;
};

export type AgentSummary = {
  /** Unique agent ID. */
  agent_id: number;
  /** Agent name. */
  name: string;
  /** ID of the district this object belongs to. */
  zone_id: number;
  /** ID of the parcel where the agent is located. */
  parcel_id: number;
  /** Visual revision number. Clients use it to detect when an object needs to be redrawn. */
  version: number;
  /** Broad agent category, such as household, general facility, or public facility. See TYPE_*. */
  type: number;
  /** Agent subcategory, given by the variant ID in agent_master. */
  variant: number;
  /** Tourist attraction type, given by its ID in landmark_master (0 if the agent is not a tourist attraction). */
  landmark: number;
  /** Building footprint polygon coordinates in the form [[x, z], ...]. */
  polygon?: any[];
  /** Center point coordinates [x, z]. */
  center_point?: any[];
  /** Slum rendering boundary coordinates [[x, z], ...], relative to center_point as with polygon. The boundary extends beyond the footprint for rendering only. Huts outside the footprint become sparser toward the rendering boundary. Null for other agents. */
  fringe_polygon?: any[];
  /** Quay line along the parcel's natural shoreline, given as [A'x, A'z, B'x, B'z]. The sea is on the left when looking from A' to B'. Used to render the port quay and position ships as they berth. Null for non-coastal parcels and parcels fronting a reclaimed-land quay. (Computed on the server. The type may vary.) */
  berth_line?: any;
  /** Status, such as planned, under construction, or active. See the shared STATUS_* constants. */
  status: number;
  /** Reason for the status transition. */
  status_reason?: number;
  /** List of sub-building placements for a composite building. Each entry contains subbuilding_id, model_prefix, position [x, z], rotation, floors, and height_per_floor. (Computed on the server. The type may vary.) */
  sub_buildings?: any;
  /** Number of floors. */
  floors?: number;
  /** Building category, such as residential or farm. Each category value is a power of two. */
  bd_type?: number;
  /** Bitmask of floor use categories the building provides, formed by combining SPACE_* flags with bitwise OR. (Computed on the server. The type may vary.) */
  space_types?: any;
  /** Durability (0.0 to 1.0, with 1.0 representing new condition). */
  durability?: number;
  /** Whether the agent is a public facility (0 = private, 1 = public). */
  public_facility: boolean;
  /** Utilization rate (most recent epoch's slot). */
  utilization?: number[];
  /** Construction progress (0% to 100%). (Computed on the server. The type may vary.) */
  construction_progress?: any;
  /** ID of the terminal node that connects to the infrastructure network. */
  infra_node_id: number;
  /** ID of a transport connection node, such as a depot connection. */
  depot_node_id: number;
  /** Seed for randomizing the object's appearance (0.0 to 1.0). */
  visual_variant?: number;
  /** Seed for randomizing the object's appearance (0.0 to 1.0). */
  visual_variant2?: number;
  /** Seed for randomizing the object's appearance (0.0 to 1.0). */
  visual_variant3?: number;
};

export type AgentData = {
  /** Unique agent ID. */
  agent_id: number;
  /** Agent name. */
  name: string;
  /** Visual revision number. Clients use it to detect when an object needs to be redrawn. */
  version: number;
  /** Broad agent category, such as household, general facility, or public facility. See TYPE_*. */
  type: number;
  /** Agent subcategory, given by the variant ID in agent_master. */
  variant: number;
  /** Tourist attraction type, given by its ID in landmark_master (0 if the agent is not a tourist attraction). */
  landmark: number;
  /** Status, such as planned, under construction, or active. See STATUS_*. */
  status: number;
  /** Reason for the status transition. */
  status_reason?: number;
  /** Bitmask of alert flags (int16). See the shared ALERT_AGENT_* constants. */
  alert: number;
  /** Creation date. */
  created_date: number;
  /** Date when the current status began. */
  status_from: number;
  /** Cash on hand in game currency. */
  reserves: number;
  /** Revenue history by epoch. */
  revenues: number[];
  /** Expense history by epoch. */
  expenses: number[];
  /** Attractiveness and ability to draw visitors. */
  attraction: number;
  /** Abandonment flag for public buildings (0 or 1). Abandoned buildings are excluded from automatic repairs. */
  abandoned?: boolean;
  /** Whether land acquisition negotiations are in progress for the parcel the building occupies. (Computed on the server. The type may vary.) */
  under_acquisition?: any;
  /** Whether the agent is a public facility (0 = private, 1 = public). */
  public_facility: boolean;
  /** ID of the terminal node that connects to the infrastructure network. */
  infra_node_id: number;
  /** ID of a transport connection node, such as a depot connection. */
  depot_node_id: number;
  /** List of actions scheduled by the player, such as demolition. */
  user_operations?: any[];
  /** Maps product IDs to quantities demanded in the current epoch. */
  demand_amount: Record<string, number>;
  /** Maps product IDs to quantities received in the current epoch. */
  received_amount?: Record<string, number>;
  /** Maps product IDs to quantities remaining in inventory. */
  remain_amount: Record<string, number>;
  /** ID of the parcel where the agent is located. */
  parcel_id: number;
  /** ID of the district this object belongs to. */
  zone_id: number;
  /** Name of the district identified by zone_id. */
  zone_name?: string;
  /** For a tenant, ID of the building agent it occupies. */
  building_agent_id?: number;
  /** Name of the building identified by building_agent_id. */
  building_agent_name?: string;
  /** Occupied floor area (m²). */
  realestate_amount?: number;
  /** Whether the agent has been evicted (0 or 1). */
  is_evicted?: boolean;
  /** Whether the agent is a building (0 or 1). */
  is_bd: boolean;
  /** Building category, such as residential or farm. Each category value is a power of two. */
  bd_type?: number;
  /** Durability (0.0 to 1.0, with 1.0 representing new condition). */
  durability?: number;
  /** Number of floors. */
  floors?: number;
  /** Building height (m). */
  height?: number;
  /** Total floor area (m²). */
  floor_area?: number;
  /** Building footprint area, equivalent to one floor (m²). */
  base_area?: number;
  /** Volume ratio (total floor area divided by site area). */
  volume_ratio?: number;
  /** Whether repairs are in progress (0 or 1). */
  under_maintenance?: boolean;
  /** Construction progress (0% to 100%). (Computed on the server. The type may vary.) */
  construction_progress?: any;
  /** Rent per unit. */
  rent?: number;
  /** Maps floor use categories to their total capacity. */
  capacity?: Record<string, number>;
  /** Maps floor use categories to their occupied capacity. */
  occupied?: Record<string, number>;
  /** List of agents occupying this building as tenants. */
  tenants?: RefItem[];
  /** Degree to which the household's nutritional needs are met (0.0 to 1.0). */
  satis_nutrition?: number;
  /** Degree to which the household's healthcare needs are met (0.0 to 1.0). */
  satis_healthcare?: number;
  /** Degree to which the household's entertainment and leisure needs are met (0.0 to 1.0). */
  satis_leisure?: number;
  /** Household satisfaction with its living environment (0.0 to 1.0). */
  satis_residential?: number;
  /** Satisfaction with the tourism experience (0.0 to 1.0). Contributes to overall tourism satisfaction for tourist households. */
  satis_tourism?: number;
  /** Household type, such as single person, couple, or household with children. See the shared FAMILY_* constants. */
  family_type?: number;
  /** List of citizens in this household. */
  members?: RefItem[];
  /** Household disposable income. */
  discretionary_income?: number;
  /** Environmental score for the location. */
  env_score?: number;
  /** Household quality-of-life (QOL) score. */
  qol_score?: number;
  /** Whether the household owns a private car (0 or 1). */
  has_automobile?: boolean;
  /** Household demand tier (0 to 3). It is the highest demand_tier among the products the household ordered. Tier 0 means the household could not place any orders. */
  demand_tier?: number;
  /** Household income tier (0 to 3). Counts how many cost-of-living thresholds its income meets. The thresholds are minimum living costs, the cost of meeting tier 1 demand up to its limit, and the cost of meeting tier 1 and tier 2 demand. */
  income_tier?: number;
  /** Utilization rate (most recent epoch's slot). */
  utilization?: number[];
  /** Player-defined target utilization rate (0.0 to 1.5). */
  utilization_target?: number;
  /** Wage rate in yen per unit of effective labor per day. A worker's daily pay is this rate multiplied by their effective labor. */
  wage?: number;
  /** Target quantity of goods to produce or services to provide per epoch. */
  quota?: number;
  /** Total labor required for operation. */
  total_labor?: number;
  /** ID of the skill required for employment. */
  required_skill?: number;
  /** List of citizens employed at this facility. */
  employees?: RefItem[];
  /** Quantity available to ship in the current epoch. */
  supply_amount?: number;
  /** ID of the product this facility supplies. */
  supply_product_id?: number;
  /** Product selling price including tax. */
  supply_price?: number;
  /** Quantity produced in the current epoch. */
  supply_production?: number;
  /** History of quantities sold by epoch. */
  supply_sold_amount?: number[];
  /** Profit margin history by epoch. */
  profit_rates?: number[];
  /** Number of tourists this facility adds to the city's total visitor capacity. Applies only to tourist attractions and gateway facilities. Returns 0 when the facility is inactive. (Computed on the server. The type may vary.) */
  tourist_capacity?: any;
  /** Maps skill IDs to the number of students the school can admit for each skill. */
  education_type?: Record<string, any>;
  /** List of citizens enrolled at this school. */
  students?: RefItem[];
  /** Total enrollment capacity based on the site, measured in students. Schools only. */
  max_students?: number;
  /** Maps skill IDs to the number of students currently enrolled for each skill. Schools only. (Computed on the server. The type may vary.) */
  enrolled_by_skill?: any;
  /** Building parking capacity, measured in vehicles. This value is set when the building is created or updated and is saved. Returns 0 for agents other than buildings. */
  parking_capacity: number;
  /** Daytime parking use in the current epoch, measured in vehicles. Includes work and school commuters, visitors, and vehicles accommodated by parking facilities. */
  parking_use_day: number;
  /** Nighttime parking use in the current epoch, measured in vehicles. Includes vehicles owned by resident households and vehicles accommodated by parking facilities. */
  parking_use_night: number;
  /** Number of vehicles that overflow onto the street after parking facilities are accounted for. Uses the larger of the daytime and nighttime counts. Returns 0 for agents other than buildings. */
  parking_overflow: number;
};

export type PathSummary = {
  /** Unique path ID. */
  path_id: number;
  /** Path name. */
  name: string;
  /** Visual revision number. Clients use it to detect when an object needs to be redrawn. */
  version: number;
  /** Whether this is a trunk road. True for motorways and streets that belong to a named road (road_id). */
  trunk: boolean;
  /** Broad path category, such as road, street, track, public transport, or infrastructure. See PATH_*. */
  path_type: number;
  /** Path subcategory, given by the variant ID that determines its width and specifications. */
  path_variant: number;
  /** District ID when both sides belong to the same district. Returns 0 for roads on a district boundary or with no district. */
  zone_id: number;
  /** District ID on the left side, looking from start to end. A value of 0 or less means no district. */
  left_zone_id: number;
  /** District ID on the right side. A value of 0 or less means no district. */
  right_zone_id: number;
  /** Coordinates of the polygon occupied by the path. */
  polygon: any[];
  /** Status, such as planned, under construction, or active. See STATUS_*. */
  status: number;
  /** Whether the path is a Bézier curve (1 = curve, 0 = straight line or polyline). */
  curved: boolean;
  /** Traffic volume by traffic type, such as road or rail traffic. */
  path_traffic: Record<string, number>;
  /** Travel direction (both ways, start to end, or end to start). */
  travel_direction: number;
  house_path_id?: number;
  /** Side on which the station platform is placed (outer, inner, or both sides). */
  platform_side: number;
  /** Terrain-following setting for this path (-1 = use the variant default, 0 = disabled, 1 = enabled). */
  follow_terrain: number;
  /** Start node ID. */
  start_node_id: number;
  /** End node ID. */
  end_node_id: number;
  /** Start anchor coordinates [x, y, z]. */
  start_point: any[];
  /** End anchor coordinates [x, y, z]. */
  end_point: any[];
  /** Outgoing control point at the start (P1 of a cubic Bézier curve), given as [x, y, z]. Filled in automatically if unspecified. */
  start_control: any[];
  /** Incoming control point at the end (P2 of a cubic Bézier curve), given as [x, y, z]. Filled in automatically if unspecified. */
  end_control: any[];
  /** List of intermediate anchors and control points. */
  waypoints: any[];
  /** Embankment offset at each anchor (-5 to +5 m). Used to reshape the terrain when construction starts. */
  embankment_heights: any[];
  /** Center point coordinates [x, z]. */
  center_point: any[];
  /** Width (m). */
  width: number;
  /** Noise level. */
  noise: number;
  /** Utilization rate (traffic volume divided by capacity). */
  utilization: number;
  /** Durability (0.0 to 1.0, with 1.0 representing new condition). */
  durability: number;
  /** Streetlight type (0 = none, 1 = standard). Saved for each road. */
  streetlight_type: number;
  /** Fence between the sidewalk and the roadway (0 = none, 1 = standard). Saved for each road. */
  fence_type: number;
  /** Street tree type (0 = none, 1 = standard). Saved for each road. */
  street_tree_type: number;
  /** Utility wiring mode (0 = none, 1 = overhead, 2 = underground). Saved for each road. */
  utility_mode: number;
  /** Shared station grade reference line in the form [x, absolute elevation, z]. If empty, normal terrain following applies. */
  station_grade_profile: any[];
  /** Path ends with station buildings (both, start, or end). */
  house_ends: string;
  /** Station building passage mode (elevated, ground, or underground). */
  passage_mode: string;
  /** Bench type (0 = none, 1 = standard). Saved for each road. */
  bench_type: number;
  /** Trash bin type (0 = none, 1 = standard). Saved for each road. */
  bin_type: number;
  /** Planter type (0 = none, 1 = standard). Saved for each road. */
  planter_type: number;
  /** Advertising type (0 = none, 1 = standard). Saved for each road. */
  advert_type: number;
  /** Appearance (0 = standard, 1 or greater = appearance number within the variant). Saved for each path and used only for rendering. */
  appearance: number;
  /** Number of vehicles parked on the street, including vehicles in dedicated parking spaces. Uses the larger of the daytime and nighttime counts. */
  parked_cars: number;
  /** Reference capacity, measured in vehicles, for calculating the on-street parking ratio and allocating parked vehicles. This differs from dedicated parking spaces, which do not obstruct traffic. Returns 0 except on active surface streets. */
  street_parking_capacity: number;
  /** Speed limit (km/h). (Computed on the server. The type may vary.) */
  max_speed?: any;
  /** ID of the named road this path belongs to. A value of -1 (VALUE_NONE) identifies an unnamed path with no road assignment. */
  road_id?: number;
  /** Travel speed (km/h) based on the speed limit, curve radius, and longitudinal grade. Excludes slowing and stopping at intersections. */
  base_speed: number;
  /** Color (#RRGGBB). Uses the path's own color if set, otherwise the variant default. Null for variants that do not use a color. This color is applied to the model's painted parts (PATH_PAINT). */
  color?: string;
};

export type PathData = {
  /** Unique path ID. */
  path_id: number;
  /** Path name. */
  name: string;
  /** Visual revision number. Clients use it to detect when an object needs to be redrawn. */
  version: number;
  /** Broad path category, such as road, street, track, public transport, or infrastructure. See PATH_*. */
  path_type: number;
  /** Path subcategory, given by the variant ID that determines its width and specifications. */
  path_variant: number;
  /** Status, such as planned, under construction, or active. See STATUS_*. */
  status: number;
  /** Bitmask of alert flags (int16). See the shared ALERT_PATH_* constants. */
  alert: number;
  /** ID of the public transport line agent that this section of path belongs to. */
  line_agent_id: number;
  /** Coordinates of the polygon occupied by the path. */
  polygon: any[];
  /** Whether the path is a Bézier curve (1 = curve, 0 = straight line or polyline). */
  curved: boolean;
  /** Travel direction (both ways, start to end, or end to start). */
  travel_direction: number;
  /** Whether repairs are in progress (0 or 1). */
  under_maintenance: number;
  /** Side on which the station platform is placed (outer, inner, or both sides). */
  platform_side: number;
  /** Terrain-following setting for this path (-1 = use the variant default, 0 = disabled, 1 = enabled). */
  follow_terrain: number;
  /** District ID when both sides belong to the same district. Returns 0 for roads on a district boundary or with no district. */
  zone_id: number;
  /** Name of the district identified by zone_id. Null for roads on a district boundary or with no district. */
  zone_name?: string;
  /** District ID on the left side, looking from start to end. A value of 0 or less means no district. */
  left_zone_id: number;
  /** District ID on the right side. A value of 0 or less means no district. */
  right_zone_id: number;
  /** Signed flow of infrastructure goods in the current epoch. Positive values indicate flow from start to end. */
  infra_flow: number;
  /** Noise level. */
  noise: number;
  /** Air pollution level. */
  air_pollution: number;
  /** Speed limit (km/h). (Computed on the server. The type may vary.) */
  max_speed?: any;
  /** Whether the player has overridden the speed limit (0 or 1). */
  override_max_speed: boolean;
  /** Travel speed (km/h) based on the speed limit, curve radius, and longitudinal grade. Excludes slowing and stopping at intersections. */
  base_speed: number;
  /** Minimum curve radius along the path (m). Straight paths use the upper limit (1e6). */
  min_curve_radius: number;
  /** Maximum longitudinal grade along the path (%). */
  max_grade: number;
  /** Actual traversal time by travel mode, including congestion. */
  travel_time: Record<string, number>;
  /** Traffic volume by traffic type, such as road or rail traffic. */
  path_traffic: Record<string, number>;
  /** Utilization rate (traffic volume divided by capacity). (Computed on the server. The type may vary.) */
  utilization?: any;
  /** Durability. Defaults to 1.0. */
  durability: number;
  /** Construction progress (0% to 100%). (Computed on the server. The type may vary.) */
  construction_progress?: any;
  /** List of districts to which adjacent streets sharing a node belong. (Computed on the server. The type may vary.) */
  nearby_zones?: any;
  /** Start node ID. */
  start_node_id: number;
  /** End node ID. */
  end_node_id: number;
  /** Start anchor coordinates [x, y, z]. */
  start_point: any[];
  /** End anchor coordinates [x, y, z]. */
  end_point: any[];
  /** Outgoing control point at the start (P1 of a cubic Bézier curve), given as [x, y, z]. Filled in automatically if unspecified. */
  start_control: any[];
  /** Incoming control point at the end (P2 of a cubic Bézier curve), given as [x, y, z]. Filled in automatically if unspecified. */
  end_control: any[];
  /** List of intermediate anchors and control points. */
  waypoints: any[];
  /** Embankment offset at each anchor (-5 to +5 m). Used to reshape the terrain when construction starts. */
  embankment_heights: any[];
  /** Center point coordinates [x, z]. */
  center_point: any[];
  /** Width (m). */
  width: number;
  /** Physical path length (m). */
  length: number;
  /** Streetlight type (0 = none, 1 = standard). Saved for each road. */
  streetlight_type: number;
  /** Fence between the sidewalk and the roadway (0 = none, 1 = standard). Saved for each road. */
  fence_type: number;
  /** Street tree type (0 = none, 1 = standard). Saved for each road. */
  street_tree_type: number;
  /** Utility wiring mode (0 = none, 1 = overhead, 2 = underground). Saved for each road. */
  utility_mode: number;
  /** Shared station grade reference line in the form [x, absolute elevation, z]. If empty, normal terrain following applies. */
  station_grade_profile: any[];
  /** Path ends with station buildings (both, start, or end). */
  house_ends: string;
  /** Station building passage mode (elevated, ground, or underground). */
  passage_mode: string;
  /** Bench type (0 = none, 1 = standard). Saved for each road. */
  bench_type: number;
  /** Trash bin type (0 = none, 1 = standard). Saved for each road. */
  bin_type: number;
  /** Planter type (0 = none, 1 = standard). Saved for each road. */
  planter_type: number;
  /** Advertising type (0 = none, 1 = standard). Saved for each road. */
  advert_type: number;
  /** Appearance (0 = standard, 1 or greater = appearance number within the variant). Saved for each path and used only for rendering. */
  appearance: number;
  /** Number of vehicles parked on the street, including vehicles in dedicated parking spaces. Uses the larger of the daytime and nighttime counts. */
  parked_cars: number;
  /** Reference capacity, measured in vehicles, for calculating the on-street parking ratio and allocating parked vehicles. This differs from dedicated parking spaces, which do not obstruct traffic. Returns 0 except on active surface streets. */
  street_parking_capacity: number;
  /** On-street parking ratio (total vehicles parked on the street divided by street_parking_capacity). To assess traffic obstruction, subtract the proportion represented by dedicated parking capacity. Returns 0 if the reference capacity is 0. */
  parking_ratio: number;
  /** ID of the named road this path belongs to. A value of -1 (VALUE_NONE) identifies an unnamed path with no road assignment. */
  road_id?: number;
  /** Color (#RRGGBB). Uses the path's own color if set, otherwise the variant default. Null for variants that do not use a color. This color is applied to the model's painted parts (PATH_PAINT). */
  color?: string;
  /** ID of the child path attached to this structure, such as the lower deck of a suspension bridge. A value of -1 (VALUE_NONE) means there is no attached path. */
  attached_path_id?: number;
};

export type ParcelSummary = {
  /** Unique parcel ID. */
  parcel_id: number;
  /** Visual revision number. Clients use it to detect when an object needs to be redrawn. */
  version: number;
  /** Parcel type, such as land or shoreline. See PARCEL_*. */
  parcel_type: number;
  /** Status, such as planned or active. See STATUS_*. */
  status: number;
  /** Reference elevation (m). */
  altitude: number;
  /** Parcel polygon coordinates in the form [[x, z], ...]. */
  polygon: any[];
  /** Center point coordinates [x, z]. */
  center_point: any[];
  /** ID of the street the parcel fronts. */
  front_path_id: number;
  /** Side of the frontage street where the parcel lies (PATH_SIDE_LEFT or PATH_SIDE_RIGHT, or -1 for no frontage). The parcel belongs to the district on that side. */
  front_path_side: number;
  /** Land price. */
  land_price: number;
  /** Whether the parcel overrides district regulations (0 or 1). */
  restriction_override: boolean;
  /** Bitmask of building categories allowed for construction, formed by combining bd_type values with bitwise OR. */
  allowed_bd_mask: number;
  /** Maximum building coverage ratio. */
  limit_coverage_ratio: number;
  /** Maximum volume ratio. */
  limit_volume_ratio: number;
  /** Maximum height (m). */
  limit_height: number;
  /** ID of the building agent using the parcel (0 if vacant). */
  usage_id: number;
  /** Water depth just beyond the quay (m). A port can be built only if the parcel meets or exceeds the port's required depth (berth_depth in agent_master). Returns 0 for non-coastal parcels. */
  water_depth: number;
};

export type ParcelData = {
  /** Unique parcel ID. */
  parcel_id: number;
  /** Visual revision number. Clients use it to detect when an object needs to be redrawn. */
  version: number;
  /** Parcel type, such as land or shoreline. See PARCEL_*. */
  parcel_type: number;
  /** Status, such as planned or active. See STATUS_*. */
  status: number;
  /** Parcel polygon coordinates in the form [[x, z], ...]. */
  polygon: any[];
  /** Center point coordinates [x, z]. */
  center_point: any[];
  /** ID of the building agent using the parcel (0 if vacant). */
  usage_id: number;
  /** ID of the district this object belongs to. */
  zone_id: number;
  /** Name of the district identified by zone_id. */
  zone_name?: string;
  /** Maps resource types to the IDs of natural resources overlapping the parcel. */
  resource_id: Record<string, number>;
  /** Average terrain slope (dimensionless). Used to determine construction difficulty. */
  terrain_slope: number;
  /** Water depth just beyond the quay (m). A port can be built only if the parcel meets or exceeds the port's required depth (berth_depth in agent_master). Returns 0 for non-coastal parcels. */
  water_depth: number;
  /** Maps resource types to the names of natural resources overlapping the parcel. (Computed on the server. The type may vary.) */
  name_resource?: any;
  /** ID of the street the parcel fronts. */
  front_path_id: number;
  /** Name of the frontage street identified by front_path_id. */
  front_path_name?: string;
  /** Side of the frontage street where the parcel lies (PATH_SIDE_LEFT or PATH_SIDE_RIGHT, or -1 for no frontage). The parcel belongs to the district on that side. */
  front_path_side: number;
  /** List of indices of the parcel edges that face a road. */
  facade_edge_index: any[];
  /** ID of the owner agent, either a private agent or the city government. */
  owner_agent_id: number;
  /** Name of the owner identified by owner_agent_id. */
  owner_agent_name?: string;
  /** Land price. */
  land_price: number;
  /** Maps agent variant IDs to location multipliers. */
  agent_multiplier: Record<string, number>;
  /** Whether the parcel overrides district regulations (0 or 1). */
  restriction_override: boolean;
  /** Whether land acquisition for this parcel is in progress. */
  under_acquisition: boolean;
  /** Whether private agents may build on the parcel. Set to false when the parcel becomes the site of a public facility. The player can change this setting. */
  allow_private_construction: boolean;
  /** Bitmask of building categories allowed for construction, formed by combining bd_type values with bitwise OR. */
  allowed_bd_mask: number;
  /** Maximum building coverage ratio. */
  limit_coverage_ratio: number;
  /** Maximum volume ratio. */
  limit_volume_ratio: number;
  /** Maximum height (m). */
  limit_height: number;
  /** Parcel area (m²). */
  area: number;
  /** Reference elevation (m). */
  altitude: number;
  /** List of actions scheduled by the player, such as land acquisition. */
  user_operations: any[];
};

export type ZoneData = {
  /** Unique district ID. */
  zone_id: number;
  /** Visual revision number. Clients use it to detect when an object needs to be redrawn. */
  version: number;
  /** District polygon derived by combining the shapes of its parcels. */
  polygon: any[];
  /** District name. */
  name: string;
  /** District identifier color (#RRGGBB). */
  color: string;
  /** Whether the district's shape has yet to be finalized by the player. */
  auto_managed: boolean;
  /** District area (m²). */
  area: number;
  /** Center point coordinates [x, z]. */
  center_point: any[];
  /** Bitmask of building categories allowed for construction, formed by combining bd_type values with bitwise OR. */
  allowed_bd_mask: number;
  /** Maximum building coverage ratio. */
  limit_coverage_ratio: number;
  /** Maximum volume ratio. */
  limit_volume_ratio: number;
  /** Maximum height (m). */
  limit_height: number;
  /** Maps product IDs to average prices in the district, including tax. */
  zone_average_prices: Record<string, number>;
  /** History of the district's average wage rate. */
  average_wage: number[];
  /** Average rent history. */
  average_rent: number[];
  /** History of land prices per square meter (yen/m²). Calculated as the total land price of parcels open to private construction divided by their total area. */
  average_land_price: number[];
  /** District population history. */
  zone_population: number[];
  /** Daytime population history. */
  daytime_population: number[];
  /** Unemployment rate history. */
  zone_unemployment_rate: number[];
  /** Maps floor use categories to quantities demanded. */
  space_demands: Record<string, number>;
  /** Maps floor use categories to quantities supplied. */
  space_supplies: Record<string, number>;
  /** Maps product IDs to quantities supplied in the district. */
  zone_supply: Record<string, number>;
  /** Maps product IDs to quantities demanded in the district. */
  zone_demand: Record<string, number>;
};

export type ResourceData = {
  /** Unique natural resource ID. */
  resource_id: number;
  /** Resource name. */
  name: string;
  /** ID of the product extracted from the resource. */
  product_id: number;
  /** Coordinates of the resource area polygon. */
  polygon: any[];
  /** Center point coordinates [x, z]. */
  center_point: any[];
  /** Resource type (key in resource_master in master.yaml). */
  resource_type: number;
  /** Extraction quantity history, newest first. */
  consumption_amount: number[];
  /** Maximum resource reserves. */
  max_amount: number;
  /** Resource reserve history, newest first. */
  current_amount: number[];
  /** Rate at which resource reserves recover per epoch. */
  recovery_rate: number;
};

export type CitizenListResponse = {
  citizens: CitizenSummary[];
};

export type CitizenResponse = {
  citizen: CitizenData;
};

export type AgentListResponse = {
  agents: AgentSummary[];
};

export type AgentResponse = {
  economic_agent: AgentData;
};

export type PathListResponse = {
  paths: PathSummary[];
};

export type PathResponse = {
  path: PathData;
};

export type ParcelListResponse = {
  parcels: ParcelSummary[];
};

export type ParcelResponse = {
  parcel: ParcelData;
};

export type ZoneResponse = {
  zone: ZoneData;
};

export type ResourceResponse = {
  resource: ResourceData;
};
