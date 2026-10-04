using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ゲーム全体で使用する定数を定義するクラス
/// バックエンドの定数定義に基づいて自動生成される
/// </summary>
public static class Constants
{
    public const string STEAM_TICKET_IDENTITY = "city-genesis";
    public const int GAME_VERIFIED = 0;
    public const int GAME_UNVERIFIED = 1;
    public const int PLAY_MODE_NORMAL = 0;
    public const int PLAY_MODE_SCEARIO = 1;
    public const int PLAY_MODE_TUTORIAL = 2;
    public const string CITY_SCENARIO_DIR = "UrbanGenesis/scenarios";
    public const string CITY_SAVE_DIR = "UrbanGenesis/saves";
    public const int HISTORY_COUNT = 120;
    public const float DAYS_PER_YEAR = 365.0f;
    public const float DAYS_PER_MONTH = 30.0f;
    public const int STATUS_TEMPORARY = 0;
    public const int STATUS_ACTIVE = 1;
    public const int STATUS_PAUSED = 2;
    public const int STATUS_SCHEDULED_CONSTRUCTION = 3;
    public const int STATUS_CONSTRUCTING = 4;
    public const int STATUS_SCHEDULED_DEMOLITION = 5;
    public const int STATUS_DEMOLISHING = 6;
    public const int STATUS_COMPLETED = 7;
    public const int STATUS_CANCELED = 8;
    public const int STATUS_EMPTY = 9;
    public const int STATUS_COLLAPSED = 10;
    public const int STATUS_PENDING = 11;
    public const int REASON_NONE = 0;
    public const int REASON_FIRE = 1;
    public const int REASON_AGING = 2;
    public const int REASON_DEFICIT = 3;
    public const int REASON_LOW_UTILIZATION = 4;
    public const int REASON_USER = 5;
    public const int DEMAND_TIER1 = 1;
    public const int DEMAND_TIER2 = 2;
    public const int DEMAND_TIER3 = 3;
    public const int VALUE_NONE = -1;
    public const int VALUE_DFAULT = 0;
    public const int BUILDING_APARTMENT = 1;
    public const int BUILDING_HOUSE = 2;
    public const int BUILDING_RETAIL = 4;
    public const int BUILDING_FACTORY = 8;
    public const int BUILDING_OFFICE = 16;
    public const int BUILDING_FARM = 32;
    public const int BUILDING_MINE = 64;
    public const int BUILDING_PORT = 128;
    public const int BUILDING_OTHER = 256;
    public const int BUILDING_PUBLIC = 512;
    public const int BD_CATEGORY_RESIDENTIAL = 1;
    public const int BD_CATEGORY_COMMERCIAL = 2;
    public const int BD_CATEGORY_OFFICE = 3;
    public const int BD_CATEGORY_INDUSTRIAL = 4;
    public const int BD_CATEGORY_RESOURCE = 5;
    public const int BD_CATEGORY_TOURISM = 6;
    public const int BD_CATEGORY_POWER = 7;
    public const int BD_CATEGORY_WATER = 8;
    public const int BD_CATEGORY_PUBLIC_SERVICE = 9;
    public const int BD_CATEGORY_EDUCATION = 10;
    public const int BD_CATEGORY_STATION = 11;
    public const int BD_CATEGORY_PORT = 12;
    public const int BD_CATEGORY_PLAZA = 13;
    public const int BD_CATEGORY_OTHER = 14;
    public const int MODEL_MODULAR_FLAT = 1;
    public const int MODEL_MODULAR_HOUSE = 2;
    public const int MODEL_MODULAR_GABLE = 3;
    public const int MODEL_MODULAR_GROUND = 4;
    public const int MODEL_PREBUILT = 5;
    public const int MODEL_PORT = 6;
    public const int MODEL_MASHUP = 7;
    public const int MODEL_MODULAR_SHED = 8;
    public const int MODEL_RAIL_YARD = 9;
    public const int MODEL_PARKING = 10;
    public const int MODEL_ROTARY = 11;
    public const int MODEL_PARK = 12;
    public const int MODEL_OPEN_PIT = 13;
    public const float MASHUP_CELL_SIZE = 8.0f;
    public const float OPEN_PIT_YARD_DEPTH = 40.0f;
    public const int FAMILY_SINGLE = 1;
    public const int FAMILY_COUPLE = 2;
    public const int FAMILY_FAMILY = 3;
    public const int FAMILY_SENIOR = 4;
    public const int MEMBER_SINGLE = 1;
    public const int MEMBER_MARRIED = 2;
    public const int SKILL_BASE = 0;
    public const int SKILL_PHYSICAL = 1;
    public const int SKILL_OFFICE = 2;
    public const int SKILL_RETAIL = 3;
    public const int SKILL_TECHNOLOGY = 4;
    public const int SKILL_MEDICAL = 5;
    public const int PARCEL_LAND = 1;
    public const int PARCEL_SHORE = 2;
    public const int PARCEL_WATER = 3;
    public const int PARCEL_RECLAIMED = 4;
    public const int GOODS_RESOURCE = 0;
    public const int GOODS_MATERIAL = 1;
    public const int GOODS_RETAIL = 2;
    public const int GOODS_SERVICE = 3;
    public const int GOODS_INFRASTRUCTURE = 4;
    public const int GOODS_TRANSIT = 5;
    public const int RESOURCE_OIL = 1;
    public const int RESOURCE_ORE = 2;
    public const int RESOURCE_RAREMETAL = 3;
    public const int RESOURCE_FOREST = 4;
    public const int RESOURCE_WATER = 5;
    public const int TYPE_DEFAULT = 0;
    public const int TYPE_CITY = 1;
    public const int TYPE_EDUCATION = 2;
    public const int TYPE_HOUSEHOLD = 3;
    public const int TYPE_TRADE = 4;
    public const int TYPE_STANDARD = 10;
    public const int TYPE_BUILDING = 20;
    public const int TYPE_OPEN_SPACE = 30;
    public const int VARIANT_ZONE_CONSTRUCTION = 0;
    public const int VARIANT_ZONE_CONSTRUCTION_MAINTENANCE = 1;
    public const int VARIANT_ZONE_RELOCATE = 3;
    public const int VARIANT_TRANSIT_LINE = 2;
    public const int VARIANT_CITY = 100;
    public const int VARIANT_CENTRAL_BANK = 101;
    public const int VARIANT_ELEMENRATY_SCHOOL = 200;
    public const int VARIANT_HOUSEHOLD = 300;
    public const int VARIANT_TOURIST = 302;
    public const int VARIANT_IMPORT = 400;
    public const int VARIANT_FIRE_STATION = 1052;
    public const int VARIANT_POLICE_STATION = 1053;
    public const int VARIANT_SOLAR_PLANT = 1054;
    public const int VARIANT_SUBSTATION = 1062;
    public const int VARIANT_APARTMENT = 2001;
    public const int VARIANT_INTERCITY_GATEWAY_OUTSIDE = 1069;
    public const int VARIANT_GRADING_LAND = 3006;
    public const int VARIANT_GRADING_SHORE = 3007;
    public const int PATH_VARIANT_ROPEWAY = 601;
    public const int PATH_VARIANT_ROPEWAY_STATION = 602;
    public const int PATH_VARIANT_GRID = 1101;
    public const int PRODUCT_CONSTRUCTION_SERVICE = 124;
    public const int PRODUCT_FIRE_SERVICE = 125;
    public const int PRODUCT_SECURITY_SERVICE = 126;
    public const string PUBLIC_PRICE_MODE_FIXED = "fixed";
    public const string PUBLIC_PRICE_MODE_COST_RATIO = "cost_ratio";
    public const float DEFAULT_PUBLIC_SERVICE_COST_RATIO = 1.0f;
    public const int PRODUCT_METRO_VEHICLE = 501;
    public const int PRODUCT_BUS_VEHICLE = 502;
    public const int PRODUCT_FOOD_RETAIL = 201;
    public const int PRODUCT_PROCESSED_FOOD = 202;
    public const int PRODUCT_ALCOHOL_RETAIL = 203;
    public const int PRODUCT_DINING_SERVICE = 204;
    public const int PRODUCT_FINE_DINING_SERVICE = 205;
    public const int PRODUCT_CLOTHING_RETAIL = 206;
    public const int PRODUCT_LUXURY_CLOTHING = 207;
    public const int PRODUCT_JEWELRY = 208;
    public const int PRODUCT_GASOLINE_RETAIL = 209;
    public const int PRODUCT_PHARMACEUTICALS_RETAIL = 210;
    public const int PRODUCT_AUTOMOBILE_RETAIL = 211;
    public const int PRODUCT_HOME_APPLIANCES_RETAIL = 212;
    public const int PRODUCT_FITNESS_SERVICE = 213;
    public const int PRODUCT_BEAUTY_SERVICE = 214;
    public const int PRODUCT_ENTERTAINMENT_SERVICE = 215;
    public const int PRODUCT_NURSING_CARE_SERVICE = 216;
    public const int PRODUCT_MEDICAL_SERVICES = 217;
    public const int PRODUCT_TOURISM_EXPERIENCE = 218;
    public const int PRODUCT_INTERCITY_TRAVEL = 219;
    public const int PRODUCT_SOFTWARE = 301;
    public const int PRODUCT_WATER_SUPPLY = 302;
    public const int PRODUCT_ELECTRICITY = 303;
    public const int PRODUCT_INTERNET = 304;
    public const int PRODUCT_TRANSPORT_SERVICE = 305;
    public const int PRODUCT_SEWAGE_SERVICE = 306;
    public const int PRODUCT_GARBAGE_SERVICE = 307;
    public const int PRODUCT_HIGH_ELECTRICITY = 401;
    public const int PRODUCT_HIGH_WATER = 402;
    public const int TRACK_NORMAL = 1;
    public const int TRACK_STATION = 2;
    public const int TRACK_BRIDGE = 3;
    public const int OPERATION_DEMOLISH = 0;
    public const int OPERATION_PAUSE = 1;
    public const int OPERATION_AQUIRE = 2;
    public const int OPERATION_RESUME = 3;
    public const int OPERATION_MAINTENANCE = 4;
    public const int EVENT_DEFAULT = 0;
    public const int EVENT_CONSTRUCTION = 1;
    public const int EVENT_AGENT_OPEN = 1;
    public const int EVENT_PATH_OPEN = 2;
    public const int EVENT_DISASTER = 3;
    public const int EVENT_OPERATION = 4;
    public const int LEDGER_CITYHALL = 1;
    public const int LEDGER_MEDICAL = 2;
    public const int LEDGER_EDUCATION = 3;
    public const int LEDGER_SECURITY = 4;
    public const int LEDGER_FIRE = 5;
    public const int LEDGER_INFRASTRUCTURE = 6;
    public const int LEDGER_OTHER = 7;
    public const int LEDGER_CONSTRUCTION = 8;
    public const int LEDGER_MAINTENANCE = 9;
    public const int LEDGER_RESERVE = 10;
    public const int LEDGER_TRANSPORT = 11;
    public const int LEDGER_WELFARE = 12;
    public const int LEDGER_LAND_ACQUISITION = 13;
    public const int LEDGER_LAND_PURCHASE = 14;
    public const int LEDGER_CONSUMPTION_TAX = 31;
    public const int LEDGER_INCOME_TAX = 32;
    public const int LEDGER_IMPORT_TAX = 33;
    public const int LEDGER_EXPORT_TAX = 34;
    public const int LEDGER_BUSINESS_TAX = 35;
    public const float MAX_CATEGORY_RATIO = 1.5f;
    public const float MAX_CONSTRUCTION_SPEED_FACTOR = 5.0f;
    public const float RAIL_JUNCTION_POSITION_TOLERANCE = 0.05f;
    public const float RAIL_JUNCTION_ANGLE_TOLERANCE_DEG = 0.1f;
    public const float RAIL_JUNCTION_PLANE_LENGTH = 20.0f;
    public const float RAIL_JUNCTION_PLANE_TOLERANCE = 0.01f;
    public const int VALUE_INIT = 0;
    public const int VALUE_EMPTY = 1;
    public const int VALUE_MAKING = 2;
    public const int VALUE_FIXED = 3;
    public const int VALUE_OLD = 4;
    public const string PARAM_ZONE_DEMAND = "Zone.zone_demand";
    public const string PARAM_ZONE_EXPORT_DEMAND = "Zone.zone_export_demand";
    public const string PARAM_ZONE_SUPPLY = "Zone.zone_supply";
    public const string PARAM_ZONE_PROFIT_RATE = "Zone.profit_rates['rate']";
    public const string PARAM_ZONE_PROFIT_RATES_LIST = "Zone.profit_rates['list]";
    public const string PARAM_ZONE_AVERAGE_PRICES = "Zone.average_prices";
    public const string PARAM_ZONE_SUPPLY_LIST = "Zone.zone_supply_list";
    public const string PARAM_CITY_AVERAGE_PRICES = "City.average_prices";
    public const int ROUTE_PEDESTRIAN = 0;
    public const int ROUTE_AUTOMOBILE = 1;
    public const int ROUTE_DIRECT = 2;
    public const int HYBRID_PATHFIND = 3;
    public const float UNREACHABLE_TRAVEL_TIME = 1000000000.0f;
    public const int TRAFFIC_PEDESTRIAN = 0;
    public const int TRAFFIC_ROAD = 1;
    public const int TRAFFIC_RAIL = 2;
    public const int VEHICLE_PEDESTRIAN = 0;
    public const int VEHICLE_STANDARD = 10;
    public const int VEHICLE_TRUCK = 11;
    public const int VEHICLE_BUS = 12;
    public const int VEHICLE_COMMERCIAL = 13;
    public const int VEHICLE_TRAM = 14;
    public const int VEHICLE_METRO_TRAIN = 20;
    public const int VEHICLE_PASSANGER_TRAIN = 21;
    public const int VEHICLE_FREIGHT_TRAIN = 22;
    public const int VEHICLE_HIGH_SPEED_TRAIN = 23;
    public const int VEHICLE_MONORAIL = 24;
    public const int SHAPE_HOUSE = 1;
    public const int SHAPE_OFFICE = 2;
    public const int SHAPE_COMMERCIAL = 3;
    public const int SHAPE_FULL = 4;
    public const int PATH_DEFAULT = 0;
    public const int PATH_STREET = 1;
    public const int PATH_MOTORWAY = 2;
    public const int PATH_WALKWAY = 3;
    public const int PATH_TRACK = 4;
    public const int PATH_MONORAIL = 5;
    public const int PATH_CONVEYOR = 6;
    public const int PATH_TRANSPORT = 10;
    public const int PATH_ELECTRICITY = 11;
    public const int PATH_WATER = 12;
    public const int PATH_PIPELINE = 13;
    public const int PATH_STATION_HOUSE = 20;
    public const int DIR_BOTH = 0;
    public const int DIR_FORWARD = 1;
    public const int DIR_BACKWARD = 2;
    public const int TRAFFIC_HAND_LEFT = 0;
    public const int TRAFFIC_HAND_RIGHT = 1;
    public const int PLATFORM_SIDE_OUTSIDE = 0;
    public const int PLATFORM_SIDE_INSIDE = 1;
    public const int PLATFORM_SIDE_BOTH = 2;
    public const int PATH_SIDE_LEFT = 0;
    public const int PATH_SIDE_RIGHT = 1;
    public const float STATION_HOUSE_DEFAULT_WIDTH = 24.0f;
    public const float STATION_HOUSE_DEFAULT_DEPTH = 16.0f;
    public const float STATION_HOUSE_TRACK_CLEARANCE_MARGIN = 0.1f;
    public const float STATION_HOUSE_TRACK_SEARCH_RADIUS = 150.0f;
    public const int CONVEYOR_MAX_NODE_DEGREE = 2;
    public const float PATH_TANGENT_ALIGN_MAX_ANGLE_DEG = 20.0f;
    public const float INFRA_TAP_HEIGHT_TOLERANCE = 3.0f;
    public const float RAIL_MIN_HEADWAY_MIN = 2.0f;
    public const float WATER_BRIDGE_MIN_CLEARANCE = 0.5f;
    public const float QUAY_KEEP_WIDTH = 2.8f;
    public const float QUAY_BAND_WIDTH = 5.6f;
    public const float QUAY_PROBE_DISTANCE = 0.5f;
    public const float SEA_GRADING_TOP = 3.0f;
    public const float GRADING_MAX_AREA = 4000000.0f;
    public const float GRADING_HEIGHT_OFFSET_LIMIT = 100.0f;
    public const float GRADING_MIN_LAND_OVERLAP = 1.0f;
    public const int GROUND_COVER_GRASSLAND = 1;
    public const int GROUND_COVER_DESERT = 2;
    public const int GROUND_COVER_LUSH = 3;
    public const int GROUND_COVER_SNOW = 4;
    public const int GROUND_COVER_SEMI_ARID = 5;
    public const int CLIMATE_TEMPERATE = 1;
    public const int CLIMATE_SUBTROPICAL = 2;
    public const int CLIMATE_CONTINENTAL = 3;
    public const int CLIMATE_MEDITERRANEAN = 4;
    public const int CLIMATE_STEPPE = 5;
    public const int CLIMATE_DESERT = 6;
    public const int CLIMATE_SUBARCTIC = 7;
    public const float CLIMATE_TEMPERATURE_VARIATION = 3.0f;
    public const float CLIMATE_SUNLIGHT_VARIATION = 15.0f;
    public const float CLIMATE_PRECIPITATION_VARIATION = 30.0f;
    public const float CLIMATE_TEMPERATURE_FADE = 10.0f;
    public const int ASSET_REGION_STANDARD = 1;
    public const int INTERCHANGE_KIND_IC = 1;
    public const int INTERCHANGE_KIND_JCT = 2;
    public const float INTERCHANGE_HULL_MARGIN_M = 10.0f;
    public const int INTERCHANGE_NAME_MAX_LENGTH = 64;
    public const int ROAD_NAME_MAX_LENGTH = 64;
    public const float ROAD_TRACE_MAX_DEFLECTION_DEG = 30.0f;
    public const int PAYMENT_MARKET = 0;
    public const int PAYMENT_RENT = 1;
    public const int PAYMENT_SALARY = 2;
    public const int PAYMENT_INHERIT = 3;
    public const int PAYMENT_REALESTATE = 4;
    public const int PAYMENT_TRANSIT = 5;
    public const int PAYMENT_ACQUISITION = 6;
    public const int PAYMENT_TRANSFER = 7;
    public const int PAYMENT_INTEREST = 8;
    public const int NODE_ROAD = 1;
    public const int NODE_RAIL = 2;
    public const int NODE_MONORAIL = 4;
    public const int NODE_CONVEYOR = 8;
    public const int NODE_TRANSIT = 16;
    public const int INFRA_BIT_BASE = 5;
    public const int SPACE_OTHER = 1;
    public const int SPACE_RESIDENCE = 2;
    public const int SPACE_RETAIL = 4;
    public const int SPACE_FACTORY = 8;
    public const int SPACE_OFFICE = 16;
    public const int SPACE_FARM = 32;
    public const int SPACE_LODGING = 64;
    public const string DATA_AGENT = "EA";
    public const string DATA_BUILDING = "BD";
    public const string DATA_PARCEL = "PC";
    public const string DATA_PATH = "PT";
    public const string DATA_ZONE = "ZN";
    public const string IMAGE_COMPLAINT = "complaint";
    public const string IMAGE_DAMAGED = "damaged";
    public const int TRANSACTION_INCOME = 0;
    public const int TRANSACTION_EXPENSE = 1;
    public const int EXPENSE_WAGE = 0;
    public const int EXPENSE_RENT = 1;
    public const int EXPENSE_TAX = 2;
    public const int EXPENSE_MATERIAL = 3;
    public const int EXPENSE_LOAN = 4;
    public const int EXPENSE_TRANSPORT = 5;
    public const int EXPENSE_FAMILY = 6;
    public const int EXPENSE_OTHER = 7;
    public const int EXPENSE_CONSTRUCTION = 8;
    public const int SOA_NODE = 1;
    public const int LOG_DEBUG = 0;
    public const int LOG_INFO = 1;
    public const int LOG_WARNING = 2;
    public const int LOG_ERROR = 3;
    public const int LOG_TRACE = 4;
    public const int LOG_FATAL = 5;
    public const int HEIGHTMAP_CELL_SIZE = 65;
    public const int HEIGHTMAP_CELLS_X = 5;
    public const int HEIGHTMAP_CELLS_Y = 5;
    public const int TERRAIN_CELL_SIZE_M = 1000;
    public const int ALERT_AGENT_CONSTRUCTION_SHORTAGE = 1;
    public const int ALERT_AGENT_ON_FIRE = 2;
    public const int ALERT_AGENT_ACCIDENTAL_FIRE = 4;
    public const int ALERT_AGENT_CRIME = 8;
    public const int ALERT_AGENT_CRIME_SIGN = 16;
    public const int ALERT_AGENT_INFRA_DISCONNECTED = 32;
    public const int ALERT_AGENT_LABOR_SHORTAGE = 64;
    public const int ALERT_AGENT_MATERIAL_SHORTAGE = 128;
    public const int ALERT_AGENT_HIGH_SKILL_SHORTAGE = 256;
    public const int ALERT_AGENT_BUDGET_CAPPED = 512;
    public const int ALERT_PATH_CONSTRUCTION_SHORTAGE = 1;
    public const int ALERT_PATH_ACCIDENT = 2;
    public const int ALERT_PATH_NO_STATION_HOUSE = 4;
    public const int ALERT_PATH_MASK_SHIFT = 8;
    public const int ALERT_DISPLAY_RUIN = 65536;
    public const int ALERT_DISPLAY_RESOURCE_WARNING = 131072;
    public const int ALERT_DISPLAY_FACILITY_WARNING = 262144;
    public const int ALERT_DISPLAY_SANITATION_WARNING = 524288;
    public const int ALERT_DISPLAY_INFRA_WARNING = 1048576;
    public const float ALERT_BUDGET_CAPPED_SCALE_THRESHOLD = 0.5f;
    public const int PATH_EQUIPMENT_NONE = 0;
    public const int PATH_EQUIPMENT_STANDARD = 1;
    public const int PATH_UTILITY_NONE = 0;
    public const int PATH_UTILITY_OVERHEAD = 1;
    public const int PATH_UTILITY_UNDERGROUND = 2;
    public const int VARIANT_BORDER_CHECKPOINT = 4;

    public static readonly Dictionary<int, int> VEHICLE_TO_TRAFFIC = new Dictionary<int, int>
    {
        { VEHICLE_PEDESTRIAN, TRAFFIC_PEDESTRIAN },
        { VEHICLE_STANDARD, TRAFFIC_ROAD },
        { VEHICLE_TRUCK, TRAFFIC_ROAD },
        { VEHICLE_BUS, TRAFFIC_ROAD },
        { VEHICLE_COMMERCIAL, TRAFFIC_ROAD },
        { VEHICLE_TRAM, TRAFFIC_ROAD },
        { VEHICLE_METRO_TRAIN, TRAFFIC_RAIL },
        { VEHICLE_PASSANGER_TRAIN, TRAFFIC_RAIL },
        { VEHICLE_FREIGHT_TRAIN, TRAFFIC_RAIL },
        { VEHICLE_HIGH_SPEED_TRAIN, TRAFFIC_RAIL },
        { VEHICLE_MONORAIL, TRAFFIC_RAIL }
    };

    public static readonly Dictionary<int, string> STATUS_NAME_MAP = new Dictionary<int, string>
    {
        { STATUS_TEMPORARY, "計画中" },
        { STATUS_ACTIVE, "稼働中" },
        { STATUS_PAUSED, "停止中" },
        { STATUS_DEMOLISHING, "取り壊し中" },
        { STATUS_SCHEDULED_CONSTRUCTION, "計画中" },
        { STATUS_SCHEDULED_DEMOLITION, "取り壊し予定" },
        { STATUS_CONSTRUCTING, "建設中" },
        { STATUS_COMPLETED, "完了" },
        { STATUS_CANCELED, "中止" },
        { STATUS_EMPTY, "取り壊し済" },
        { STATUS_COLLAPSED, "倒壊" },
        { STATUS_PENDING, "着工保留中" }
    };

    public static readonly Dictionary<string, int> ALERT_WARNING_FILTER_MAP = new Dictionary<string, int>
    {
        { "resource_runway", ALERT_DISPLAY_RESOURCE_WARNING },
        { "resource_depleted", ALERT_DISPLAY_RESOURCE_WARNING },
        { "facility_inoperative", ALERT_DISPLAY_FACILITY_WARNING },
        { "sanitation", ALERT_DISPLAY_SANITATION_WARNING },
        { "infra_facility_down", ALERT_DISPLAY_INFRA_WARNING }
    };

    public static readonly int[] OFF_MARKET_GOODS_TYPES = new int[] { GOODS_INFRASTRUCTURE, GOODS_TRANSIT };

    public static readonly int[] GRADING_VARIANTS = new int[] { VARIANT_GRADING_LAND, VARIANT_GRADING_SHORE };

    public static readonly int[] ALL_ROUTES = new int[] { ROUTE_PEDESTRIAN, ROUTE_AUTOMOBILE };

    public static readonly int[] ALL_TRAFFICS = new int[] { TRAFFIC_PEDESTRIAN, TRAFFIC_ROAD, TRAFFIC_RAIL };

    public static readonly int[] PEDESTRIAN_VEHICLE_KEYS = new int[] { VEHICLE_PEDESTRIAN };

    public static readonly int[] ROAD_VEHICLE_KEYS = new int[] { VEHICLE_STANDARD, VEHICLE_TRUCK, VEHICLE_BUS, VEHICLE_COMMERCIAL, VEHICLE_TRAM };

    public static readonly int[] RAIL_VEHICLE_KEYS = new int[] { VEHICLE_METRO_TRAIN, VEHICLE_PASSANGER_TRAIN, VEHICLE_FREIGHT_TRAIN, VEHICLE_HIGH_SPEED_TRAIN, VEHICLE_MONORAIL };

    public static readonly int[] GROUND_COVER_TYPES = new int[] { GROUND_COVER_GRASSLAND, GROUND_COVER_DESERT, GROUND_COVER_LUSH, GROUND_COVER_SNOW, GROUND_COVER_SEMI_ARID };

    public static readonly int[] CLIMATE_TYPES = new int[] { CLIMATE_TEMPERATE, CLIMATE_SUBTROPICAL, CLIMATE_CONTINENTAL, CLIMATE_MEDITERRANEAN, CLIMATE_STEPPE, CLIMATE_DESERT, CLIMATE_SUBARCTIC };

    public static readonly int[] ASSET_REGION_TYPES = new int[] { ASSET_REGION_STANDARD };

    public static readonly int[] SIMULATION_DAYS_CHOICES = new int[] { 7, 30 };

    public static readonly string[] SCENARIO_START_OPTIONS = new string[] { "simulation_days", "climate", "ground_cover", "asset_region" };

    public static readonly int[] PATH_TYPES_NAMED_ROAD = new int[] { PATH_STREET, PATH_MOTORWAY };

    public static readonly string[] ROAD_COLOR_PALETTE = new string[] { "#7F77DD", "#E4572E", "#1D9E75", "#D4537E", "#2E86DE", "#BA7517", "#0F6E56", "#993C1D" };

    public static readonly int[] TENANT_TYPES = new int[] { SPACE_RESIDENCE, SPACE_RETAIL, SPACE_FACTORY, SPACE_OFFICE, SPACE_LODGING };

    public static readonly int[] ALERT_WARNING_DISPLAY_BITS = new int[] { ALERT_DISPLAY_RESOURCE_WARNING, ALERT_DISPLAY_FACILITY_WARNING, ALERT_DISPLAY_SANITATION_WARNING, ALERT_DISPLAY_INFRA_WARNING };

    public static readonly int[] ALERT_AGENT_ICON_BITS = new int[] { ALERT_AGENT_CONSTRUCTION_SHORTAGE, ALERT_AGENT_CRIME, ALERT_AGENT_CRIME_SIGN, ALERT_AGENT_INFRA_DISCONNECTED };

    public static readonly int[] ALERT_AGENT_VFX_BITS = new int[] { ALERT_AGENT_ON_FIRE, ALERT_AGENT_ACCIDENTAL_FIRE };

    public static readonly int[] ALERT_PATH_ICON_BITS = new int[] { ALERT_PATH_CONSTRUCTION_SHORTAGE, ALERT_PATH_ACCIDENT, ALERT_PATH_NO_STATION_HOUSE };

    public static readonly string[] PATH_EQUIPMENT_FIELDS = new string[] { "streetlight_type", "fence_type", "street_tree_type", "utility_mode", "bench_type", "bin_type", "planter_type", "advert_type" };
}
