/**
 * ゲーム全体で使用する定数を定義する
 * バックエンドの定数定義に基づいて自動生成される
 */
export const VERIFICATION_SERVER_URL: string = "http://127.0.0.1:8080";
export const TEMPLATE_PUBLIC_URL: string = "https://storage.googleapis.com/cg2025/initial_saves_metadata.json";
export const STEAM_TICKET_IDENTITY: string = "city-genesis";
export const GAME_VERIFIED: number = 0;
export const GAME_UNVERIFIED: number = 1;
export const CITY_TEMPLATE_DIR: string = "UrbanGenesis/templates";
export const CITY_SAVE_DIR: string = "UrbanGenesis/saves";
export const HISTORY_COUNT: number = 120;
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
export const MODEL_MODULAR_FLAT: number = 1;
export const MODEL_MODULAR_HOUSE: number = 2;
export const MODEL_MODULAR_GABLE: number = 3;
export const MODEL_MODULAR_GROUND: number = 4;
export const MODEL_PREBUILT: number = 5;
export const MODEL_PORT: number = 6;
export const MODEL_MASHUP: number = 7;
export const MASHUP_CELL_SIZE: number = 8.0;
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
export const VARIANT_ZONE_CONSTRUCTION: number = 0;
export const VARIANT_ZONE_CONSTRUCTION_MAINTENANCE: number = 1;
export const VARIANT_ZONE_RELOCATE: number = 3;
export const VARIANT_TRANSIT_LINE: number = 2;
export const VARIANT_CITY: number = 100;
export const VARIANT_CENTRAL_BANK: number = 101;
export const VARIANT_ELEMENRATY_SCHOOL: number = 200;
export const VARIANT_HOUSEHOLD: number = 300;
export const VARIANT_IMPORT: number = 400;
export const VARIANT_SOLAR_PLANT: number = 1054;
export const VARIANT_SUBSTATION: number = 1062;
export const VARIANT_APARTMENT: number = 2001;
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
export const LEDGER_LAND_SALE: number = 12;
export const LEDGER_LAND_ACQUISITION: number = 13;
export const LEDGER_CONSUMPTION_TAX: number = 31;
export const LEDGER_INCOME_TAX: number = 32;
export const LEDGER_IMPORT_TAX: number = 33;
export const LEDGER_EXPORT_TAX: number = 34;
export const LEDGER_BUSINESS_TAX: number = 35;
export const MAX_CATEGORY_RATIO: number = 1.5;
export const MAX_CONSTRUCTION_SPEED_FACTOR: number = 5.0;
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
export const VEHICLE_METRO_TRAIN: number = 20;
export const VEHICLE_PASSANGER_TRAIN: number = 21;
export const VEHICLE_FREIGHT_TRAIN: number = 22;
export const VEHICLE_HIGH_SPEED_TRAIN: number = 23;
export const SHAPE_HOUSE: number = 1;
export const SHAPE_OFFICE: number = 2;
export const SHAPE_COMMERCIAL: number = 3;
export const SHAPE_FULL: number = 4;
export const PATH_DEFAULT: number = 0;
export const PATH_MOTORWAY: number = 1;
export const PATH_STREET: number = 2;
export const PATH_WALKWAY: number = 3;
export const PATH_TRACK: number = 4;
export const PATH_MONORAIL: number = 5;
export const PATH_ROPEWAY: number = 6;
export const PATH_TRANSPORT: number = 10;
export const PATH_ELECTRICITY: number = 11;
export const PATH_WATER: number = 12;
export const DIR_BOTH: number = 0;
export const DIR_FORWARD: number = 1;
export const DIR_BACKWARD: number = 2;
export const TRAFFIC_HAND_LEFT: number = 0;
export const TRAFFIC_HAND_RIGHT: number = 1;
export const PLATFORM_SIDE_OUTSIDE: number = 0;
export const PLATFORM_SIDE_INSIDE: number = 1;
export const PLATFORM_SIDE_BOTH: number = 2;
export const PATH_TANGENT_ALIGN_MAX_ANGLE_DEG: number = 20.0;
export const PAYMENT_MARKET: number = 0;
export const PAYMENT_RENT: number = 1;
export const PAYMENT_SALARY: number = 2;
export const PAYMENT_INHERIT: number = 3;
export const PAYMENT_REALESTATE: number = 4;
export const PAYMENT_TRANSIT: number = 5;
export const PAYMENT_ACQUISITION: number = 6;
export const NODE_ROAD: number = 1;
export const NODE_RAIL: number = 2;
export const NODE_MONORAIL: number = 4;
export const NODE_ROPEWAY: number = 8;
export const NODE_TRANSIT: number = 16;
export const INFRA_BIT_BASE: number = 5;
export const SPACE_OTHER: number = 1;
export const SPACE_RESIDENCE: number = 2;
export const SPACE_RETAIL: number = 4;
export const SPACE_FACTORY: number = 8;
export const SPACE_OFFICE: number = 16;
export const SPACE_FARM: number = 32;
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
export const ALERT_PATH_CONSTRUCTION_SHORTAGE: number = 1;
export const ALERT_PATH_ACCIDENT: number = 2;

export const VEHICLE_TO_TRAFFIC: Record<number, number> = {
  0: 0,
  10: 1,
  11: 1,
  12: 1,
  13: 1,
  20: 2,
  21: 2,
  22: 2,
  23: 2,
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

export const ALL_ROUTES: number[] = [0, 1];

export const ALL_TRAFFICS: number[] = [0, 1, 2];

export const PEDESTRIAN_VEHICLE_KEYS: number[] = [0];

export const ROAD_VEHICLE_KEYS: number[] = [10, 11, 12, 13];

export const RAIL_VEHICLE_KEYS: number[] = [20, 21, 22, 23];

export const TENANT_TYPES: number[] = [2, 4, 8, 16];
