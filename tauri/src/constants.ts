/**
 * ゲーム全体で使用する定数を定義する
 * バックエンドの定数定義に基づいて自動生成される
 */
export const STEAM_TICKET_IDENTITY: string = "city-genesis";
export const GAME_VERIFIED: number = 0;
export const GAME_UNVERIFIED: number = 1;
export const PLAY_MODE_NORMAL: number = 0;
export const PLAY_MODE_SCEARIO: number = 1;
export const PLAY_MODE_TUTORIAL: number = 2;
export const PLAY_MODE_READ_ONLY: number = 3;
export const CITY_SCENARIO_DIR: string = "UrbanGenesis/scenarios";
export const CITY_SAVE_DIR: string = "UrbanGenesis/saves";
export const HISTORY_COUNT: number = 120;
export const ZONE_MAX_DIAMETER: number = 1000.0;
export const ZONE_MAX_TOTAL_STREET_LENGTH: number = 8000.0;
export const DAYS_PER_YEAR: number = 365.0;
export const DAYS_PER_MONTH: number = 30.0;
export const STATUS_TEMPORARY: number = 0;
export const STATUS_ACTIVE: number = 1;
export const STATUS_PAUSED: number = 2;
export const STATUS_SCHEDULED_CONSTRUCTION: number = 3;
export const STATUS_CONSTRUCTING: number = 4;
export const STATUS_SCHEDULED_DEMOLITION: number = 5;
export const STATUS_DEMOLISHING: number = 6;
export const STATUS_COMPLETED: number = 7;
export const STATUS_CANCELED: number = 8;
export const STATUS_EMPTY: number = 9;
export const STATUS_COLLAPSED: number = 10;
export const STATUS_PENDING: number = 11;
export const REASON_NONE: number = 0;
export const REASON_FIRE: number = 1;
export const REASON_AGING: number = 2;
export const REASON_DEFICIT: number = 3;
export const REASON_LOW_UTILIZATION: number = 4;
export const REASON_USER: number = 5;
export const DEMAND_TIER1: number = 1;
export const DEMAND_TIER2: number = 2;
export const DEMAND_TIER3: number = 3;
export const VALUE_NONE: number = -1;
export const VALUE_DFAULT: number = 0;
export const BUILDING_APARTMENT: number = 1;
export const BUILDING_HOUSE: number = 2;
export const BUILDING_RETAIL: number = 4;
export const BUILDING_FACTORY: number = 8;
export const BUILDING_OFFICE: number = 16;
export const BUILDING_FARM: number = 32;
export const BUILDING_MINE: number = 64;
export const BUILDING_PORT: number = 128;
export const BUILDING_OTHER: number = 256;
export const BUILDING_PUBLIC: number = 512;
export const BD_CATEGORY_RESIDENTIAL: number = 1;
export const BD_CATEGORY_COMMERCIAL: number = 2;
export const BD_CATEGORY_OFFICE: number = 3;
export const BD_CATEGORY_INDUSTRIAL: number = 4;
export const BD_CATEGORY_RESOURCE: number = 5;
export const BD_CATEGORY_TOURISM: number = 6;
export const BD_CATEGORY_POWER: number = 7;
export const BD_CATEGORY_WATER: number = 8;
export const BD_CATEGORY_PUBLIC_SERVICE: number = 9;
export const BD_CATEGORY_EDUCATION: number = 10;
export const BD_CATEGORY_STATION: number = 11;
export const BD_CATEGORY_PORT: number = 12;
export const BD_CATEGORY_PLAZA: number = 13;
export const BD_CATEGORY_OTHER: number = 14;
export const MODEL_MODULAR_FLAT: number = 1;
export const MODEL_MODULAR_HOUSE: number = 2;
export const MODEL_MODULAR_GABLE: number = 3;
export const MODEL_MODULAR_GROUND: number = 4;
export const MODEL_PREBUILT: number = 5;
export const MODEL_PORT: number = 6;
export const MODEL_MASHUP: number = 7;
export const MODEL_MODULAR_SHED: number = 8;
export const MODEL_RAIL_YARD: number = 9;
export const MODEL_PARKING: number = 10;
export const MODEL_ROTARY: number = 11;
export const MODEL_PARK: number = 12;
export const MODEL_OPEN_PIT: number = 13;
export const MASHUP_CELL_SIZE: number = 8.0;
export const OPEN_PIT_YARD_DEPTH: number = 40.0;
export const FAMILY_SINGLE: number = 1;
export const FAMILY_COUPLE: number = 2;
export const FAMILY_FAMILY: number = 3;
export const FAMILY_SENIOR: number = 4;
export const MEMBER_SINGLE: number = 1;
export const MEMBER_MARRIED: number = 2;
export const SKILL_BASE: number = 0;
export const SKILL_PHYSICAL: number = 1;
export const SKILL_OFFICE: number = 2;
export const SKILL_RETAIL: number = 3;
export const SKILL_TECHNOLOGY: number = 4;
export const SKILL_MEDICAL: number = 5;
export const PARCEL_LAND: number = 1;
export const PARCEL_SHORE: number = 2;
export const PARCEL_WATER: number = 3;
export const PARCEL_RECLAIMED: number = 4;
export const GOODS_RESOURCE: number = 0;
export const GOODS_MATERIAL: number = 1;
export const GOODS_RETAIL: number = 2;
export const GOODS_SERVICE: number = 3;
export const GOODS_INFRASTRUCTURE: number = 4;
export const GOODS_TRANSIT: number = 5;
export const RESOURCE_OIL: number = 1;
export const RESOURCE_ORE: number = 2;
export const RESOURCE_RAREMETAL: number = 3;
export const RESOURCE_FOREST: number = 4;
export const RESOURCE_WATER: number = 5;
export const TYPE_DEFAULT: number = 0;
export const TYPE_CITY: number = 1;
export const TYPE_EDUCATION: number = 2;
export const TYPE_HOUSEHOLD: number = 3;
export const TYPE_TRADE: number = 4;
export const TYPE_STANDARD: number = 10;
export const TYPE_BUILDING: number = 20;
export const TYPE_OPEN_SPACE: number = 30;
export const VARIANT_ZONE_CONSTRUCTION: number = 0;
export const VARIANT_ZONE_CONSTRUCTION_MAINTENANCE: number = 1;
export const VARIANT_ZONE_RELOCATE: number = 3;
export const VARIANT_TRANSIT_LINE: number = 2;
export const VARIANT_CITY: number = 100;
export const VARIANT_CENTRAL_BANK: number = 101;
export const VARIANT_ELEMENRATY_SCHOOL: number = 200;
export const VARIANT_HOUSEHOLD: number = 300;
export const VARIANT_TOURIST: number = 302;
export const VARIANT_IMPORT: number = 400;
export const VARIANT_FIRE_STATION: number = 1052;
export const VARIANT_POLICE_STATION: number = 1053;
export const VARIANT_SOLAR_PLANT: number = 1054;
export const VARIANT_SUBSTATION: number = 1062;
export const VARIANT_APARTMENT: number = 2001;
export const VARIANT_INTERCITY_GATEWAY_OUTSIDE: number = 1069;
export const VARIANT_GRADING_LAND: number = 3006;
export const VARIANT_GRADING_SHORE: number = 3007;
export const PATH_VARIANT_ROPEWAY: number = 601;
export const PATH_VARIANT_ROPEWAY_STATION: number = 602;
export const PATH_VARIANT_GRID: number = 1101;
export const PRODUCT_CONSTRUCTION_SERVICE: number = 124;
export const PRODUCT_FIRE_SERVICE: number = 125;
export const PRODUCT_SECURITY_SERVICE: number = 126;
export const PUBLIC_PRICE_MODE_FIXED: string = "fixed";
export const PUBLIC_PRICE_MODE_COST_RATIO: string = "cost_ratio";
export const DEFAULT_PUBLIC_SERVICE_COST_RATIO: number = 1.0;
export const PRODUCT_METRO_VEHICLE: number = 501;
export const PRODUCT_BUS_VEHICLE: number = 502;
export const PRODUCT_FOOD_RETAIL: number = 201;
export const PRODUCT_PROCESSED_FOOD: number = 202;
export const PRODUCT_ALCOHOL_RETAIL: number = 203;
export const PRODUCT_DINING_SERVICE: number = 204;
export const PRODUCT_FINE_DINING_SERVICE: number = 205;
export const PRODUCT_CLOTHING_RETAIL: number = 206;
export const PRODUCT_LUXURY_CLOTHING: number = 207;
export const PRODUCT_JEWELRY: number = 208;
export const PRODUCT_GASOLINE_RETAIL: number = 209;
export const PRODUCT_PHARMACEUTICALS_RETAIL: number = 210;
export const PRODUCT_AUTOMOBILE_RETAIL: number = 211;
export const PRODUCT_HOME_APPLIANCES_RETAIL: number = 212;
export const PRODUCT_FITNESS_SERVICE: number = 213;
export const PRODUCT_BEAUTY_SERVICE: number = 214;
export const PRODUCT_ENTERTAINMENT_SERVICE: number = 215;
export const PRODUCT_NURSING_CARE_SERVICE: number = 216;
export const PRODUCT_MEDICAL_SERVICES: number = 217;
export const PRODUCT_TOURISM_EXPERIENCE: number = 218;
export const PRODUCT_INTERCITY_TRAVEL: number = 219;
export const PRODUCT_SOFTWARE: number = 301;
export const PRODUCT_WATER_SUPPLY: number = 302;
export const PRODUCT_ELECTRICITY: number = 303;
export const PRODUCT_INTERNET: number = 304;
export const PRODUCT_TRANSPORT_SERVICE: number = 305;
export const PRODUCT_SEWAGE_SERVICE: number = 306;
export const PRODUCT_GARBAGE_SERVICE: number = 307;
export const PRODUCT_HIGH_ELECTRICITY: number = 401;
export const PRODUCT_HIGH_WATER: number = 402;
export const TRACK_NORMAL: number = 1;
export const TRACK_STATION: number = 2;
export const TRACK_BRIDGE: number = 3;
export const OPERATION_DEMOLISH: number = 0;
export const OPERATION_PAUSE: number = 1;
export const OPERATION_AQUIRE: number = 2;
export const OPERATION_RESUME: number = 3;
export const OPERATION_MAINTENANCE: number = 4;
export const EVENT_DEFAULT: number = 0;
export const EVENT_CONSTRUCTION: number = 1;
export const EVENT_AGENT_OPEN: number = 1;
export const EVENT_PATH_OPEN: number = 2;
export const EVENT_DISASTER: number = 3;
export const EVENT_OPERATION: number = 4;
export const LEDGER_CITYHALL: number = 1;
export const LEDGER_MEDICAL: number = 2;
export const LEDGER_EDUCATION: number = 3;
export const LEDGER_SECURITY: number = 4;
export const LEDGER_FIRE: number = 5;
export const LEDGER_INFRASTRUCTURE: number = 6;
export const LEDGER_OTHER: number = 7;
export const LEDGER_CONSTRUCTION: number = 8;
export const LEDGER_MAINTENANCE: number = 9;
export const LEDGER_RESERVE: number = 10;
export const LEDGER_TRANSPORT: number = 11;
export const LEDGER_WELFARE: number = 12;
export const LEDGER_LAND_ACQUISITION: number = 13;
export const LEDGER_LAND_PURCHASE: number = 14;
export const LEDGER_CONSUMPTION_TAX: number = 31;
export const LEDGER_INCOME_TAX: number = 32;
export const LEDGER_IMPORT_TAX: number = 33;
export const LEDGER_EXPORT_TAX: number = 34;
export const LEDGER_BUSINESS_TAX: number = 35;
export const MAX_CATEGORY_RATIO: number = 1.5;
export const MAX_CONSTRUCTION_SPEED_FACTOR: number = 5.0;
export const RAIL_JUNCTION_POSITION_TOLERANCE: number = 0.05;
export const RAIL_JUNCTION_ANGLE_TOLERANCE_DEG: number = 0.1;
export const RAIL_JUNCTION_PLANE_LENGTH: number = 20.0;
export const RAIL_JUNCTION_PLANE_TOLERANCE: number = 0.01;
export const VALUE_INIT: number = 0;
export const VALUE_EMPTY: number = 1;
export const VALUE_MAKING: number = 2;
export const VALUE_FIXED: number = 3;
export const VALUE_OLD: number = 4;
export const PARAM_ZONE_DEMAND: string = "Zone.zone_demand";
export const PARAM_ZONE_EXPORT_DEMAND: string = "Zone.zone_export_demand";
export const PARAM_ZONE_SUPPLY: string = "Zone.zone_supply";
export const PARAM_ZONE_PROFIT_RATE: string = "Zone.profit_rates['rate']";
export const PARAM_ZONE_PROFIT_RATES_LIST: string = "Zone.profit_rates['list]";
export const PARAM_ZONE_AVERAGE_PRICES: string = "Zone.average_prices";
export const PARAM_ZONE_SUPPLY_LIST: string = "Zone.zone_supply_list";
export const PARAM_CITY_AVERAGE_PRICES: string = "City.average_prices";
export const ROUTE_PEDESTRIAN: number = 0;
export const ROUTE_AUTOMOBILE: number = 1;
export const ROUTE_DIRECT: number = 2;
export const HYBRID_PATHFIND: number = 3;
export const UNREACHABLE_TRAVEL_TIME: number = 1000000000.0;
export const TRAFFIC_PEDESTRIAN: number = 0;
export const TRAFFIC_ROAD: number = 1;
export const TRAFFIC_RAIL: number = 2;
export const VEHICLE_PEDESTRIAN: number = 0;
export const VEHICLE_STANDARD: number = 10;
export const VEHICLE_TRUCK: number = 11;
export const VEHICLE_BUS: number = 12;
export const VEHICLE_COMMERCIAL: number = 13;
export const VEHICLE_TRAM: number = 14;
export const VEHICLE_METRO_TRAIN: number = 20;
export const VEHICLE_PASSANGER_TRAIN: number = 21;
export const VEHICLE_FREIGHT_TRAIN: number = 22;
export const VEHICLE_HIGH_SPEED_TRAIN: number = 23;
export const VEHICLE_MONORAIL: number = 24;
export const SHAPE_HOUSE: number = 1;
export const SHAPE_OFFICE: number = 2;
export const SHAPE_COMMERCIAL: number = 3;
export const SHAPE_FULL: number = 4;
export const SHAPE_SETBACK: number = 5;
export const PATH_DEFAULT: number = 0;
export const PATH_STREET: number = 1;
export const PATH_MOTORWAY: number = 2;
export const PATH_WALKWAY: number = 3;
export const PATH_TRACK: number = 4;
export const PATH_MONORAIL: number = 5;
export const PATH_CONVEYOR: number = 6;
export const PATH_TRANSPORT: number = 10;
export const PATH_ELECTRICITY: number = 11;
export const PATH_WATER: number = 12;
export const PATH_PIPELINE: number = 13;
export const PATH_STATION_HOUSE: number = 20;
export const DIR_BOTH: number = 0;
export const DIR_FORWARD: number = 1;
export const DIR_BACKWARD: number = 2;
export const TRAFFIC_HAND_LEFT: number = 0;
export const TRAFFIC_HAND_RIGHT: number = 1;
export const PLATFORM_SIDE_OUTSIDE: number = 0;
export const PLATFORM_SIDE_INSIDE: number = 1;
export const PLATFORM_SIDE_BOTH: number = 2;
export const PATH_SIDE_LEFT: number = 0;
export const PATH_SIDE_RIGHT: number = 1;
export const STATION_HOUSE_DEFAULT_WIDTH: number = 24.0;
export const STATION_HOUSE_DEFAULT_DEPTH: number = 16.0;
export const STATION_HOUSE_TRACK_CLEARANCE_MARGIN: number = 0.1;
export const STATION_HOUSE_TRACK_SEARCH_RADIUS: number = 150.0;
export const CONVEYOR_MAX_NODE_DEGREE: number = 2;
export const PATH_TANGENT_ALIGN_MAX_ANGLE_DEG: number = 20.0;
export const INFRA_TAP_HEIGHT_TOLERANCE: number = 3.0;
export const RAIL_MIN_HEADWAY_MIN: number = 2.0;
export const WATER_BRIDGE_MIN_CLEARANCE: number = 0.5;
export const QUAY_KEEP_WIDTH: number = 2.8;
export const QUAY_BAND_WIDTH: number = 5.6;
export const QUAY_PROBE_DISTANCE: number = 0.5;
export const SEA_GRADING_TOP: number = 3.0;
export const GRADING_MAX_AREA: number = 4000000.0;
export const GRADING_HEIGHT_OFFSET_LIMIT: number = 100.0;
export const GRADING_MIN_LAND_OVERLAP: number = 1.0;
export const ENV_SOURCE_CORE_RADIUS_M: number = 10.0;
export const ENV_DECAY_CUTOFF_RATIO: number = 4.0;
export const GROUND_COVER_GRASSLAND: number = 1;
export const GROUND_COVER_DESERT: number = 2;
export const GROUND_COVER_LUSH: number = 3;
export const GROUND_COVER_SNOW: number = 4;
export const GROUND_COVER_SEMI_ARID: number = 5;
export const CLIMATE_AF: number = 1;
export const CLIMATE_AM: number = 2;
export const CLIMATE_AW: number = 3;
export const CLIMATE_BWH: number = 4;
export const CLIMATE_BWK: number = 5;
export const CLIMATE_BSH: number = 6;
export const CLIMATE_BSK: number = 7;
export const CLIMATE_CSA: number = 8;
export const CLIMATE_CSB: number = 9;
export const CLIMATE_CFA: number = 10;
export const CLIMATE_CWA: number = 11;
export const CLIMATE_CFB: number = 12;
export const CLIMATE_CFC: number = 13;
export const CLIMATE_DFA: number = 14;
export const CLIMATE_DFB: number = 15;
export const CLIMATE_DFC: number = 16;
export const CLIMATE_DFD: number = 17;
export const CLIMATE_DWA: number = 18;
export const CLIMATE_ET: number = 19;
export const CLIMATE_EF: number = 20;
export const CLIMATE_H: number = 21;
export const CLIMATE_TEMPERATURE_VARIATION: number = 3.0;
export const CLIMATE_SUNLIGHT_VARIATION: number = 15.0;
export const CLIMATE_PRECIPITATION_VARIATION: number = 30.0;
export const WIND_SPEED_AVERAGE: number = 5.0;
export const WIND_SPEED_VARIATION: number = 1.0;
export const CLIMATE_TEMPERATURE_FADE: number = 10.0;
export const ASSET_REGION_STANDARD: number = 1;
export const INTERCHANGE_KIND_IC: number = 1;
export const INTERCHANGE_KIND_JCT: number = 2;
export const INTERCHANGE_HULL_MARGIN_M: number = 10.0;
export const INTERCHANGE_NAME_MAX_LENGTH: number = 64;
export const ROAD_NAME_MAX_LENGTH: number = 64;
export const ROAD_TRACE_MAX_DEFLECTION_DEG: number = 30.0;
export const PAYMENT_MARKET: number = 0;
export const PAYMENT_RENT: number = 1;
export const PAYMENT_SALARY: number = 2;
export const PAYMENT_INHERIT: number = 3;
export const PAYMENT_REALESTATE: number = 4;
export const PAYMENT_TRANSIT: number = 5;
export const PAYMENT_ACQUISITION: number = 6;
export const PAYMENT_TRANSFER: number = 7;
export const NODE_ROAD: number = 1;
export const NODE_RAIL: number = 2;
export const NODE_MONORAIL: number = 4;
export const NODE_CONVEYOR: number = 8;
export const NODE_TRANSIT: number = 16;
export const INFRA_BIT_BASE: number = 5;
export const SPACE_OTHER: number = 1;
export const SPACE_RESIDENCE: number = 2;
export const SPACE_RETAIL: number = 4;
export const SPACE_FACTORY: number = 8;
export const SPACE_OFFICE: number = 16;
export const SPACE_FARM: number = 32;
export const SPACE_LODGING: number = 64;
export const DATA_AGENT: string = "EA";
export const DATA_BUILDING: string = "BD";
export const DATA_PARCEL: string = "PC";
export const DATA_PATH: string = "PT";
export const DATA_ZONE: string = "ZN";
export const IMAGE_COMPLAINT: string = "complaint";
export const IMAGE_DAMAGED: string = "damaged";
export const TRANSACTION_INCOME: number = 0;
export const TRANSACTION_EXPENSE: number = 1;
export const EXPENSE_WAGE: number = 0;
export const EXPENSE_RENT: number = 1;
export const EXPENSE_TAX: number = 2;
export const EXPENSE_MATERIAL: number = 3;
export const EXPENSE_LOAN: number = 4;
export const EXPENSE_TRANSPORT: number = 5;
export const EXPENSE_FAMILY: number = 6;
export const EXPENSE_OTHER: number = 7;
export const EXPENSE_CONSTRUCTION: number = 8;
export const SOA_NODE: number = 1;
export const LOG_DEBUG: number = 0;
export const LOG_INFO: number = 1;
export const LOG_WARNING: number = 2;
export const LOG_ERROR: number = 3;
export const LOG_TRACE: number = 4;
export const LOG_FATAL: number = 5;
export const HEIGHTMAP_CELL_SIZE: number = 65;
export const HEIGHTMAP_CELLS_X: number = 5;
export const HEIGHTMAP_CELLS_Y: number = 5;
export const TERRAIN_CELL_SIZE_M: number = 1000;
export const ALERT_AGENT_CONSTRUCTION_SHORTAGE: number = 1;
export const ALERT_AGENT_ON_FIRE: number = 2;
export const ALERT_AGENT_ACCIDENTAL_FIRE: number = 4;
export const ALERT_AGENT_CRIME: number = 8;
export const ALERT_AGENT_CRIME_SIGN: number = 16;
export const ALERT_AGENT_INFRA_DISCONNECTED: number = 32;
export const ALERT_AGENT_LABOR_SHORTAGE: number = 64;
export const ALERT_AGENT_MATERIAL_SHORTAGE: number = 128;
export const ALERT_AGENT_HIGH_SKILL_SHORTAGE: number = 256;
export const ALERT_AGENT_BUDGET_CAPPED: number = 512;
export const ALERT_PATH_CONSTRUCTION_SHORTAGE: number = 1;
export const ALERT_PATH_ACCIDENT: number = 2;
export const ALERT_PATH_NO_STATION_HOUSE: number = 4;
export const ALERT_PATH_MASK_SHIFT: number = 8;
export const ALERT_DISPLAY_RUIN: number = 65536;
export const ALERT_DISPLAY_RESOURCE_WARNING: number = 131072;
export const ALERT_DISPLAY_FACILITY_WARNING: number = 262144;
export const ALERT_DISPLAY_SANITATION_WARNING: number = 524288;
export const ALERT_DISPLAY_INFRA_WARNING: number = 1048576;
export const ALERT_BUDGET_CAPPED_SCALE_THRESHOLD: number = 0.5;
export const PATH_EQUIPMENT_NONE: number = 0;
export const PATH_EQUIPMENT_STANDARD: number = 1;
export const PATH_UTILITY_NONE: number = 0;
export const PATH_UTILITY_OVERHEAD: number = 1;
export const PATH_UTILITY_UNDERGROUND: number = 2;
export const VARIANT_BORDER_CHECKPOINT: number = 4;

export const VEHICLE_TO_TRAFFIC: Record<number, number> = {
  0: 0,
  10: 1,
  11: 1,
  12: 1,
  13: 1,
  14: 1,
  20: 2,
  21: 2,
  22: 2,
  23: 2,
  24: 2,
};

export const STATUS_NAME_MAP: Record<number, string> = {
  0: "計画中",
  1: "稼働中",
  2: "停止中",
  6: "取り壊し中",
  3: "計画中",
  5: "取り壊し予定",
  4: "建設中",
  7: "完了",
  8: "中止",
  9: "取り壊し済",
  10: "倒壊",
  11: "着工保留中",
};

export const ALERT_WARNING_FILTER_MAP: Record<string, number> = {
  "resource_runway": 131072,
  "facility_inoperative": 262144,
  "sanitation": 524288,
  "infra_facility_down": 1048576,
};

export const OFF_MARKET_GOODS_TYPES: number[] = [4, 5];

export const GRADING_VARIANTS: number[] = [3006, 3007];

export const ALL_ROUTES: number[] = [0, 1];

export const ALL_TRAFFICS: number[] = [0, 1, 2];

export const PEDESTRIAN_VEHICLE_KEYS: number[] = [0];

export const ROAD_VEHICLE_KEYS: number[] = [10, 11, 12, 13, 14];

export const RAIL_VEHICLE_KEYS: number[] = [20, 21, 22, 23, 24];

export const GROUND_COVER_TYPES: number[] = [1, 2, 3, 4, 5];

export const CLIMATE_TYPES: number[] = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21];

export const ASSET_REGION_TYPES: number[] = [1];

export const SIMULATION_DAYS_CHOICES: number[] = [7, 30];

export const SCENARIO_START_OPTIONS: string[] = ["simulation_days", "climate", "ground_cover", "asset_region"];

export const PATH_TYPES_NAMED_ROAD: number[] = [1, 2];

export const ROAD_COLOR_PALETTE: string[] = ["#7F77DD", "#E4572E", "#1D9E75", "#D4537E", "#2E86DE", "#BA7517", "#0F6E56", "#993C1D"];

export const TENANT_TYPES: number[] = [2, 4, 8, 16, 64];

export const ALERT_WARNING_DISPLAY_BITS: number[] = [131072, 262144, 524288, 1048576];

export const ALERT_AGENT_ICON_BITS: number[] = [1, 8, 32];

export const ALERT_AGENT_VFX_BITS: number[] = [2];

export const ALERT_PATH_ICON_BITS: number[] = [1, 2, 4];

export const PATH_EQUIPMENT_FIELDS: string[] = ["streetlight_type", "fence_type", "street_tree_type", "utility_mode", "bench_type", "bin_type", "planter_type", "advert_type"];
