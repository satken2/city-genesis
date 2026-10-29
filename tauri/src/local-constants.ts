/**
 * UI-specific constants. constants.ts is generated; do not edit it. Define UI-only constants here.
 */

/**
 * True in the production UI bundled by `tauri build` (equivalent to the distribution exe); false in `tauri dev`
 * via the Vite development server. Allow development checks of top menu/HUD using only backend APIs even without
 * Unity's Named Pipe.
 */
export const IS_PACKAGED_PRODUCTION_UI = import.meta.env.PROD;

/**
 * Default fare and target headway (minutes) for new public transport lines. Line types are PATH_TRANSPORT
 * variants in path_master; do not define separate UI type constants.
 */
export const TRANSIT_DEFAULT_FARE = 200;
export const TRANSIT_DEFAULT_HEADWAY_MIN = 10;
// Default cars_per_train for creation.
export const TRANSIT_DEFAULT_CARS_PER_TRAIN = 1;

/**
 * Default drawing colors by public transport line type (path_variant). Per-line `line_color` is authoritative
 * (assigned from a backend palette on creation and editable by players); use these only as fallbacks for empty
 * line_color. Keyed by variant ID like resolveTransitIcon (utils/pathIcon.ts). Undefined types use
 * TRANSIT_DEFAULT_COLOR.
 */
export const TRANSIT_VARIANT_COLOR: Record<number, string> = {
  1001: "#fab387", // Bus line (orange)
  1002: "#89b4fa", // Metro line (blue)
  1003: "#a6e3a1", // LRT line (green)
  1004: "#cba6f7", // Monorail line (purple)
  1005: "#f38ba8", // Regional railway line (red)
};

/** Fallback color for undefined types. */
export const TRANSIT_DEFAULT_COLOR = "#f9e2af";

/**
 * Line color swatches (line detail settings tab). The same 12-color palette used cyclically for new lines by the
 * backend (crud_transit.TRANSIT_LINE_COLOR_PALETTE). Saturated colors resemble real subway maps and remain
 * distinct on dark UI and 3D ground. Custom colors can be entered by color code.
 */
export const TRANSIT_LINE_COLOR_PRESETS: readonly string[] = [
  "#e5171f", // Red
  "#0079c2", // Blue
  "#00a650", // Green
  "#f39700", // Orange
  "#8f76d6", // Purple
  "#e85298", // Pink
  "#00b2b2", // Teal
  "#ffd400", // Yellow
  "#9c5e31", // Brown
  "#1f2f8c", // Navy
  "#b5b5ac", // Silver
  "#6cbb5a", // Yellow-green
];

/** Look up the line diagram color from line type (path_variant). */
export function resolveTransitColor(pathVariant: number): string {
  return TRANSIT_VARIANT_COLOR[pathVariant] ?? TRANSIT_DEFAULT_COLOR;
}

import {
  STATUS_TEMPORARY,
  STATUS_ACTIVE,
  STATUS_PAUSED,
  STATUS_SCHEDULED_CONSTRUCTION,
  STATUS_CONSTRUCTING,
  STATUS_SCHEDULED_DEMOLITION,
  STATUS_DEMOLISHING,
  STATUS_COMPLETED,
  STATUS_CANCELED,
  STATUS_EMPTY,
  STATUS_COLLAPSED,
  PATH_DEFAULT,
  PATH_STREET,
  PATH_MOTORWAY,
  PATH_WALKWAY,
  PATH_TRACK,
  PATH_MONORAIL,
  PATH_CONVEYOR,
  PATH_FERRY,
  PATH_TRANSPORT,
  PATH_ELECTRICITY,
  PATH_WATER,
  PATH_PIPELINE,
  PATH_STATION_HOUSE,
  PRODUCT_WATER_SUPPLY,
  PRODUCT_ELECTRICITY,
} from "./constants";

/**
 * Products and order for the cockpit public service fulfillment meters (hud/PublicServiceGauges). GET
 * /api/v1/city/public-service-supply returns all regulated products; centralize the UI's choice of
 * always-visible public services here. Include high-voltage goods in service supply limits rather than separate
 * bars.
 */
export const COCKPIT_PUBLIC_SERVICE_PRODUCTS: readonly number[] = [
  PRODUCT_ELECTRICITY,
  PRODUCT_WATER_SUPPLY,
];

/**
 * Infrastructure path types (power transmission, water conveyance, pipelines) and helpers. Matches backend
 * consts.PATH_TYPES_INFRA. constants.ts does not generate tuples, so centralize PATH_TYPES_INFRA here. Node bits
 * physically separate networks by goods; use this only for shared infrastructure behavior independent of goods,
 * such as not deforming terrain.
 */
export const PATH_TYPES_INFRA: readonly number[] = [PATH_ELECTRICITY, PATH_WATER, PATH_PIPELINE];

/** Return whether path_type is infrastructure. */
export function isInfraPathType(pathType: number | null | undefined): boolean {
  return pathType != null && PATH_TYPES_INFRA.includes(pathType);
}

/**
 * Path types hidden as construction panel categories. Unset defaults (without an actual entity) and line section
 * paths created by the public transport window are not player-laid paths.
 */
export const PATH_TYPES_HIDDEN_IN_BUILD_PANEL: readonly number[] = [PATH_DEFAULT, PATH_TRANSPORT];

/**
 * Path types whose category tiles appear under Tracks in the construction panel. PATH_TYPES_INFRA appear under
 * Infrastructure; all others appear under Roads.
 */
export const PATH_TYPES_TRACK_GROUP: readonly number[] = [PATH_TRACK, PATH_MONORAIL, PATH_CONVEYOR, PATH_FERRY, PATH_STATION_HOUSE];

/**
 * Path types with minimum curve radius/maximum gradient calculated from geometry. Matches backend
 * geo_path_alignment.SPEED_PATH_TYPES. Other types retain uncalculated defaults (0/straight), so omit them from
 * path details.
 */
export const PATH_TYPES_WITH_ALIGNMENT: readonly number[] = [PATH_STREET, PATH_MOTORWAY, PATH_TRACK, PATH_MONORAIL];

/**
 * Path types showing traversal speed (base_speed) in path details: roads carrying cars only. Track/monorail
 * limits are assumed unchanged by regulations (default 300km/h), leaving traversal speed at that ceiling. Actual
 * train speed depends on curve radius and the line type's maximum speed, making this display meaningless.
 */
export const PATH_TYPES_WITH_BASE_SPEED: readonly number[] = [PATH_STREET, PATH_MOTORWAY];

/**
 * Path types supporting speed limits (signs and individual settings) in path details. Include sidewalks in
 * addition to traversal-speed types: limits do not affect pedestrians, but LRT on pedestrian roads with LRT uses
 * them. Exclude tracks/monorails because player-set limits serve no purpose (same in regulations). People movers
 * use equipment cruising speed; public transport section paths, infrastructure, and station buildings have no
 * traversal time, so exclude them.
 */
export const PATH_TYPES_WITH_SPEED_LIMIT: readonly number[] = [...PATH_TYPES_WITH_BASE_SPEED, PATH_WALKWAY];

/**
 * Path types with calculated utilization, matching backend PathUtilizationPhase. Other types always have zero
 * utilization, so omit it from path details.
 */
export const PATH_TYPES_WITH_UTILIZATION: readonly number[] = [
  PATH_STREET,
  PATH_MOTORWAY,
  PATH_TRACK,
  PATH_MONORAIL,
  PATH_CONVEYOR,
  PATH_TRANSPORT,
];

/**
 * Display styles by status. bg is background color; text is text color. Statuses with showProgress=true show a
 * progress bar according to completion percentage.
 */
export const STATUS_STYLE_MAP: Record<
  number,
  { bg: string; text: string; showProgress?: boolean }
> = {
  [STATUS_TEMPORARY]: { bg: "#ffffff", text: "#1a1a1a" },
  [STATUS_ACTIVE]: { bg: "#22c55e", text: "#ffffff" },
  [STATUS_PAUSED]: { bg: "#9ca3af", text: "#ffffff" },
  [STATUS_SCHEDULED_CONSTRUCTION]: { bg: "#ffffff", text: "#1a1a1a" },
  [STATUS_CONSTRUCTING]: {
    bg: "#eab308",
    text: "#1a1a1a",
    showProgress: true,
  },
  [STATUS_SCHEDULED_DEMOLITION]: { bg: "#a16207", text: "#ffffff" },
  [STATUS_DEMOLISHING]: { bg: "#a16207", text: "#ffffff" },
  [STATUS_COMPLETED]: { bg: "#3b82f6", text: "#ffffff" },
  [STATUS_CANCELED]: { bg: "#6b7280", text: "#ffffff" },
  [STATUS_EMPTY]: { bg: "#6b7280", text: "#ffffff" },
  [STATUS_COLLAPSED]: { bg: "#ef4444", text: "#ffffff" },
};

/**
 * Space category color palette for building cross-sections. Cycle by index in category appearance order.
 */
export const SPACE_CATEGORY_COLORS: readonly string[] = [
  "#4A90D9",
  "#E8A838",
  "#50C878",
  "#E85D75",
  "#9B59B6",
  "#1ABC9C",
  "#F39C12",
  "#34495E",
];
