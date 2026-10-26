/**
 * Shared HUD definitions (persistent gameplay control panels)
 *
 * The HUD uses five Tauri windows. Transparent areas in a window over Unity still intercept clicks, so each
 * window covers only its actual panel.
 *
 * | Window label | Component | Position | Content |
 * | --- | --- | --- | --- |
 * | `icon-panel` | `IconPanel` | Left edge, vertically centered | Menus (roads/construction/budget/statistics…), transparent background |
 * | `cockpit` | `Cockpit` | Bottom center | Tracking list, three common tools, city name/date, governance score/budget meters, alerts, epoch execution |
 * | `system-tray` | `SystemTray` | Bottom right | System statistics, settings, save, reload |
 * | `display-tray` | `DisplayTray` | Bottom left | Quick layer toggles (buildings, paths, props, parcels, terrain, districts) |
 * | `citizen-voice` | `CitizenVoice` | Top right | Sliding cards of citizens' remarks with action buttons |
 *
 * Cockpit/system tray icons sit directly on the panel, so individual padding fields compensate for image size
 * differences.
 * The icon panel fills CSS button frames with images and does not specify padding here.
 */

import { invoke } from "@tauri-apps/api/core";
import citizenListIcon from "../../assets/images/menu/citizen-tracking.svg";

/** Resolve a menu icon image URL (relative to the page location in the browser version). */
export function iconUrl(filename: string): string {
  return `${import.meta.env.BASE_URL}images/menu/${filename}`;
}

/**
 * HUD icon definition
 */
export type HudIconDef = {
  id: string;
  src: string;
  tooltipKey: string;
  /** Image filename in the panel directory without extension; defaults to id. */
  panelIcon?: string;
  /** Inner icon padding. Omit where the image fills its frame (the icon panel). */
  padding?: string;
  /** Window type opened on click. If absent, clicking is disabled or handled by the component. */
  windowType?: string;
  /** Tauri command called directly without the window registry (mutually exclusive with `windowType`) */
  command?: string;
  /** Hint bar text key when opening the window. Clear the hint bar if omitted. */
  hintKey?: string;
  /** Visible only in scenario authoring mode (author controls) */
  scenarioOnly?: boolean;
  /** Usable while locked because City Hall is missing (needed to found the city itself) */
  enabledWhenLocked?: boolean;
  /** Hidden in read-only mode (cities shown to the public on a public server) */
  hiddenWhenReadOnly?: boolean;
};

/**
 * Vertical menu icons on the left-edge icon panel, in array order.
 * Scenario-only controls belong to the scenario editor opened from the system tray.
 *
 * Omit budget (`city_budget`), public facilities (`public_facilities`), and achievements (`achievements`) here.
 * Their cockpit meters continuously show their state and open the corresponding window on click, avoiding
 * duplicate entry points.
 */
export const MENU_ICONS: HudIconDef[] = [
  { id: "grading", src: "menu_grading.png", tooltipKey: "taskbar.menuGrading", windowType: "grading", hintKey: "hint.grading", hiddenWhenReadOnly: true },
  { id: "road", src: "menu_road.png", tooltipKey: "taskbar.menuRoad", windowType: "path_build", hiddenWhenReadOnly: true }, // Place road, railway, and infrastructure paths together.
  { id: "land", src: "menu_land.png", tooltipKey: "taskbar.menuLand", windowType: "parcel_creation", hiddenWhenReadOnly: true },
  { id: "district", src: "menu_district.png", tooltipKey: "taskbar.menuDistrict", windowType: "zone_edit", hiddenWhenReadOnly: true },
  { id: "construction", src: "menu_construction.png", tooltipKey: "taskbar.menuConstruction", windowType: "building_construction", enabledWhenLocked: true, hiddenWhenReadOnly: true },
  { id: "transport", src: "menu_transport.png", tooltipKey: "taskbar.menuTransport", windowType: "transit_management" },
  { id: "prop_placement", src: "menu_prop.png", tooltipKey: "taskbar.menuPropPlacement", windowType: "prop_placement", hintKey: "prop_placement.hint", hiddenWhenReadOnly: true },
  { id: "construction_projects", src: "menu_construction.png", tooltipKey: "taskbar.menuConstructionProjects", windowType: "construction_projects" },
  { id: "regulation", src: "menu_regulation.png", tooltipKey: "taskbar.menuRegulation", windowType: "city_regulation" },
  { id: "stat", src: "menu_stat.png", tooltipKey: "taskbar.menuStat", windowType: "city_statistics" },
];

/**
 * Common tools arranged in row order in the cockpit's left three-column grid. Image references are resolved
 * URLs.
 * Cockpit appends its drive button, leaving the bottom-right cell empty.
 */
export const TOOL_ICONS: HudIconDef[] = [
  { id: "visibility", src: iconUrl("visibility.png"), tooltipKey: "taskbar.menuView", padding: "4px", windowType: "display_options" },
  { id: "map", src: iconUrl("map.png"), tooltipKey: "taskbar.menuMap", padding: "4px", windowType: "city_map" },
  { id: "citizen_list", src: citizenListIcon, tooltipKey: "taskbar.menuCitizenList", padding: "3px", windowType: "citizen_list" },
  { id: "bulldozer", src: iconUrl("bulldozer.png"), tooltipKey: "taskbar.bulldozerOff", padding: "3px" },
];

/**
 * Icons in the system tray (bottom right)
 */
export const TRAY_ICONS: HudIconDef[] = [
  { id: "system_stats", src: "tray_system_stats.png", tooltipKey: "taskbar.traySystemStats", padding: "4px", windowType: "system_stats", hiddenWhenReadOnly: true },
  { id: "settings", src: "tray_settings.png", tooltipKey: "taskbar.traySettings", padding: "4px", windowType: "settings", hiddenWhenReadOnly: true },
  { id: "save", src: "tray_save.png", tooltipKey: "taskbar.traySave", padding: "4px", windowType: "save_load", hiddenWhenReadOnly: true },
  { id: "reload", src: "tray_reload.png", tooltipKey: "taskbar.trayReload", padding: "4px" },
];

/** Open a registered window. Failure only logs to the console, keeping the HUD usable. */
export async function openRegisteredWindow(typeKey: string): Promise<void> {
  try {
    await invoke("open_registered_window", { windowType: typeKey });
  } catch (error) {
    console.error("failed to open window", typeKey, error);
  }
}
