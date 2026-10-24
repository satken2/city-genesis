/**
 * Display option layer/mode definitions and icon resolution
 */

// ---- Icon resolution ----

const visualModules = import.meta.glob<{ default: string }>(
  "../../assets/images/visual/*.png",
  { eager: true },
);

const trafficModules = import.meta.glob<{ default: string }>(
  "../../assets/images/traffics/*.png",
  { eager: true },
);

const spaceModules = import.meta.glob<{ default: string }>(
  "../../assets/images/spaces/*.png",
  { eager: true },
);

const buildingModules = import.meta.glob<{ default: string }>(
  "../../assets/images/buildings/*.png",
  { eager: true },
);

const alertModules = import.meta.glob<{ default: string }>(
  "../../assets/images/alerts/*.{png,svg}",
  { eager: true },
);

// Vehicle icons used for the public transport tab and line type buttons.
const transportModules = import.meta.glob<{ default: string }>(
  "../../assets/images/transport/*.png",
  { eager: true },
);

const ALL_MODULES: Record<string, { default: string }> = {
  ...visualModules,
  ...trafficModules,
  ...spaceModules,
  ...buildingModules,
  ...alertModules,
  ...transportModules,
};

export function resolveDisplayIcon(iconPath: string): string {
  const key = `../../assets/images/${iconPath}`;
  return ALL_MODULES[key]?.default ?? "";
}

// ---- Types ----

export type DisplaySubMode = {
  id: string;
  tooltipKey: string;
  icon: string;
};

export type DisplayMode = {
  id: string;
  tooltipKey: string;
  icon: string;
  subModes?: DisplaySubMode[];
};

/**
 * Visibility toggles independent of color modes (modes).
 * Send the enabled toggle IDs to Unity as flags (CSV) in view:change_view.
 */
export type DisplayToggle = {
  id: string;
  labelKey: string;
  icon: string;
};

export type DisplayLayer = {
  id: string;
  titleKey: string;
  defaultMode: string;
  /** Initially enabled toggle IDs. All toggles default to off if omitted. */
  defaultFlags?: string[];
  modes: DisplayMode[];
  toggles?: DisplayToggle[];
};

// ---- Section definitions ----

// Hide planned buildings and roads normally; let Unity's display presets handle temporary visibility during editing.
export const DISPLAY_LAYERS: DisplayLayer[] = [
  {
    id: "terrain",
    titleKey: "display.layerTerrain",
    defaultMode: "default",
    modes: [
      { id: "default", tooltipKey: "display.modeTerrainDefault", icon: "visual/normal.png" },
      { id: "transparent", tooltipKey: "display.modeTerrainTransparent", icon: "visual/invisible.png" },
      { id: "elevation", tooltipKey: "display.modeTerrainElevation", icon: "visual/heatmap.png" },
    ],
  },
  {
    id: "alert",
    titleKey: "display.layerAlert",
    defaultMode: "default",
    modes: [
      { id: "default", tooltipKey: "display.layerAlert", icon: "alerts/siren.png" },
    ],
  },
  {
    id: "road",
    titleKey: "display.layerRoad",
    defaultMode: "default",
    modes: [
      { id: "default", tooltipKey: "display.modeRoadDefault", icon: "visual/normal.png" },
      {
        id: "traffic_color",
        tooltipKey: "display.modeRoadTraffic",
        icon: "visual/traffic.png",
        subModes: [
          { id: "TRAFFIC_PEDESTRIAN", tooltipKey: "display.subPedestrian", icon: "traffics/traffic_pedestrian.png" },
          { id: "TRAFFIC_ROAD", tooltipKey: "display.subRoad", icon: "traffics/traffic_road.png" },
          { id: "TRAFFIC_RAIL", tooltipKey: "display.subRail", icon: "traffics/traffic_rail.png" },
        ],
      },
      { id: "speed_color", tooltipKey: "display.modeRoadSpeed", icon: "visual/speed.png" },
      { id: "durability_color", tooltipKey: "display.modeRoadDurability", icon: "visual/durability.png" },
      { id: "zone_color", tooltipKey: "display.modeRoadZone", icon: "visual/region.png" },
      { id: "hidden", tooltipKey: "display.modeHidden", icon: "visual/invisible.png" },
    ],
    toggles: [
      { id: "plan", labelKey: "display.toggleShowPlanRoad", icon: "visual/plan.png" },
      { id: "speed_limit", labelKey: "display.toggleSpeedLimitSigns", icon: "visual/speed.png" },
    ],
  },
  {
    id: "building",
    titleKey: "display.layerBuilding",
    defaultMode: "default",
    modes: [
      { id: "default", tooltipKey: "display.modeBuildingDefault", icon: "visual/normal.png" },
      {
        id: "floor_color",
        tooltipKey: "display.modeBuildingFloor",
        icon: "visual/spaces.png",
        subModes: [
          { id: "SPACE_OTHER", tooltipKey: "display.subSpaceOther", icon: "spaces/space_0.png" },
          { id: "SPACE_RESIDENCE", tooltipKey: "display.subSpaceResidence", icon: "spaces/space_1.png" },
          { id: "SPACE_RETAIL", tooltipKey: "display.subSpaceRetail", icon: "spaces/space_2.png" },
          { id: "SPACE_FACTORY", tooltipKey: "display.subSpaceFactory", icon: "spaces/space_3.png" },
          { id: "SPACE_OFFICE", tooltipKey: "display.subSpaceOffice", icon: "spaces/space_4.png" },
          { id: "SPACE_FARM", tooltipKey: "display.subSpaceFarm", icon: "spaces/space_5.png" },
        ],
      },
      { id: "zone_color", tooltipKey: "display.modeBuildingZone", icon: "visual/region.png" },
      { id: "durability_color", tooltipKey: "display.modeBuildingDurability", icon: "visual/durability.png" },
      { id: "hidden", tooltipKey: "display.modeHidden", icon: "visual/invisible.png" },
    ],
    toggles: [
      { id: "plan", labelKey: "display.toggleShowPlanBuilding", icon: "visual/plan.png" },
      { id: "public", labelKey: "display.toggleHighlightPublic", icon: "buildings/bd_512.png" },
    ],
  },
  {
    id: "parcel",
    titleKey: "display.layerParcel",
    defaultMode: "hidden",
    modes: [
      { id: "hidden", tooltipKey: "display.modeHidden", icon: "visual/invisible.png" },
      { id: "border", tooltipKey: "display.modeParcelBorder", icon: "visual/frame.png" },
      { id: "land_price_color", tooltipKey: "display.modeParcelLandPrice", icon: "visual/price.png" },
      { id: "zone_color", tooltipKey: "display.modeParcelZone", icon: "visual/region.png" },
      { id: "noise_color", tooltipKey: "display.modeParcelNoise", icon: "visual/heatmap.png" },
      { id: "air_pollution_color", tooltipKey: "display.modeParcelAirPollution", icon: "visual/heatmap.png" },
      { id: "crime_color", tooltipKey: "display.modeParcelCrime", icon: "visual/heatmap.png" },
      { id: "sunshine_color", tooltipKey: "display.modeParcelSunshine", icon: "visual/heatmap.png" },
      {
        id: "buildable_color",
        tooltipKey: "display.modeParcelBuildable",
        icon: "visual/zoning.png",
        subModes: [
          { id: "BUILDING_APARTMENT", tooltipKey: "display.subBdApartment", icon: "buildings/bd_1.png" },
          { id: "BUILDING_HOUSE", tooltipKey: "display.subBdHouse", icon: "buildings/bd_2.png" },
          { id: "BUILDING_RETAIL", tooltipKey: "display.subBdRetail", icon: "buildings/bd_4.png" },
          { id: "BUILDING_FACTORY", tooltipKey: "display.subBdFactory", icon: "buildings/bd_8.png" },
          { id: "BUILDING_OFFICE", tooltipKey: "display.subBdOffice", icon: "buildings/bd_16.png" },
          { id: "BUILDING_FARM", tooltipKey: "display.subBdFarm", icon: "buildings/bd_32.png" },
          { id: "BUILDING_MINE", tooltipKey: "display.subBdMine", icon: "buildings/bd_64.png" },
          { id: "BUILDING_PORT", tooltipKey: "display.subBdPort", icon: "buildings/bd_128.png" },
          { id: "BUILDING_OTHER", tooltipKey: "display.subBdOther", icon: "buildings/bd_256.png" },
          { id: "BUILDING_PUBLIC", tooltipKey: "display.subBdPublic", icon: "buildings/bd_512.png" },
        ],
      },
    ],
  },
  {
    id: "district",
    titleKey: "display.layerDistrict",
    defaultMode: "hidden",
    modes: [
      { id: "hidden", tooltipKey: "display.modeHidden", icon: "visual/invisible.png" },
      { id: "border_icon", tooltipKey: "display.modeDistrictBorderIcon", icon: "visual/normal.png" },
    ],
  },
  {
    id: "resource",
    titleKey: "display.layerResource",
    defaultMode: "hidden",
    modes: [
      { id: "hidden", tooltipKey: "display.modeHidden", icon: "visual/invisible.png" },
      { id: "visible", tooltipKey: "display.modeResourceVisible", icon: "visual/normal.png" },
    ],
  },
  {
    // Public transport layer. It has a single mode (default); visibility by line type is
    // managed by DisplayOptionsPanel's TransitSection through subMode (CSV of enabled transit_path_id values).
    id: "transit",
    titleKey: "display.layerTransit",
    defaultMode: "hidden",
    modes: [
      { id: "default", tooltipKey: "display.modeTransitDefault", icon: "transport/t_bus.png" },
    ],
  },
  {
    // Coverage layer. DisplayOptionsPanel's CoverageSection manages the selected service good through
    // subMode (product ID). Only one good can be selected, since buildings are colored by whether they are in range.
    id: "coverage",
    titleKey: "display.layerCoverage",
    defaultMode: "hidden",
    modes: [
      { id: "hidden", tooltipKey: "display.modeHidden", icon: "visual/invisible.png" },
      { id: "default", tooltipKey: "display.modeCoverageDefault", icon: "alerts/electricity.png" },
    ],
  },
  {
    id: "prop",
    titleKey: "display.layerProp",
    defaultMode: "visible",
    modes: [
      { id: "visible", tooltipKey: "display.modePropVisible", icon: "visual/normal.png" },
      { id: "hidden", tooltipKey: "display.modeHidden", icon: "visual/invisible.png" },
    ],
  },
];
