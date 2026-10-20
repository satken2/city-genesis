/**
 * Shared rendering component for the city's 2D map.
 *
 * This component renders only the map, with no toolbar or other UI. It can be used in CityMapWindow or embedded in other panels
 * (as a background for road construction previews, a base for line maps, etc.).
 *
 * Coordinates: the game uses the X-Z horizontal plane and Y for elevation. The map is a top-down view.
 * Rendering uses Canvas 2D view transforms (translate + scale); each layer is cached as Path2D or an offscreen canvas in world coordinates.
 *
 * Screen orientation: terrain row 0 is the southern edge; columns run west to east and rows south to north.
 * Columns increase along +X and rows along +Z, so geographic north is +Z and east is +X, matching Unity's overhead view,
 * compass, and camera yaw (clockwise from north = +Z = 0). Canvas 2D's Y axis points downward, so the view transform uses
 * a negative Y scale to draw +Z upward. The current transformation matrix (CTM) handles this instead of reversing individual
 * coordinates, allowing Path2D, elevation images, contours, grids, and overlayDraw to use world coordinates throughout.
 * Text and images (icons, etc.) would appear upside down under the Y-flipped CTM, so cancel the Y flip only when drawing them
 * (e.g. drawMapLabels in mapLabels.ts).
 *
 * Layers (toggled through the layers prop):
 *   - elevation: elevation colors from beige lowlands to green highlands, with terrain shaded by light from the northwest.
 *     Color the near-field elevation map at full resolution and derive shading from slopes measured in real distances.
 *     The near-field elevation map represents the initial terrain. Fill reclaimed land (GET /api/v1/terrain/reclaimed-lands)
 *     with the lowest land elevation color so it does not appear as sea. The crest height is a derived, unsaved value,
 *     so do not use the actual elevation color. When the layer is off, land is filled from coastline polygons, so an
 *     expanded coastline automatically gives reclaimed land the land color.
 *   - contours: contour lines built from the near-field elevation map with Marching Squares; intervals decrease with zoom.
 *   - grid: a grid at 100 m intervals, with cell boundaries at cell_size intervals emphasized in a darker color.
 *   - coastline: coastline polygons (outer land boundaries and hole rings for inland lake and pond shores).
 *   - zoneBorders: district outlines.
 *   - parcels: parcel outlines.
 *   - buildings: building footprints.
 *   - buildings3d: simple 3D buildings extruded by floor count × floor height (effective only when buildings is on).
 *     Treat the viewpoint as directly above the screen center. Translate the roof, retaining the footprint's shape, by
 *     (building center − screen center) × height / viewpoint height, then connect it to the footprint with side faces.
 *     These solids overlay the 2D map above roads/tracks and below lines, highlights, and labels.
 *   - allRoads / trunkRoads / railways: paths (roads, arterial roads, and tracks).
 *     Draw roads as filled occupied polygons, with width transitions from centerlines at connections between two different widths.
 *     Streets are light gray, highways dark gray, and tracks solid gray lines.
 *     Arterial roads comprise highways and streets belonging to roads (named roads; sections with road_id).
 *     Road name labels follow the arterial road layer (drawn at label_points from GET /api/v1/roads/, rotated to match
 *     the road direction; placement and collision avoidance are in mapLabels.ts).
 *   - transitLines: public transport lines. Draw the actual running geometry along underlying roads/tracks returned by
 *     GET /api/v1/transit/lines/geometry as bands in each line's color, rather than straight segments between stops.
 *     Mark stops with rings. Drawing rules are in transitLineDraw.ts. transitFocusLineId emphasizes one line
 *     (used by the line detail map), and transitVariant filters which line types are drawn.
 *     Refetch geometry on the "transit:lines_changed" notification.
 *   - interchanges: interchange (IC/JCT) symbols and names, fetched citywide from GET /api/v1/interchanges/ and drawn
 *     at their centers (type abbreviation + ID when unnamed). Like districts, there are few of them, so refetch them
 *     with every vector fetch to track detection changes caused by epoch execution and road editing.
 *   - labels: everything drawn by mapLabels.ts (district names; station, interchange, and facility symbols/names; road names).
 *     Symbols are also drawn as labels, so disabling this layer also hides interchange and facility symbols.
 *
 * Land outside the city:
 *   For maps with background terrain, fill the distant coastline (GET /api/v1/terrain/background/coastline, a coarse
 *   outline created with the map and unchanged during play) with the land color only outside the city rectangle.
 *   Draw elevation colors, contours, grids, and coastline strokes only inside the city. Always outline the city rectangle
 *   with a gray dotted line regardless of layers to show the area available for city use.
 *
 * Heatmap (one mutually exclusive type selected through the heatmap prop):
 *   Overlay environmental scores as a grid image. Zero impact is transparent; opacity increases with the value.
 *   Draw above roads/buildings and below labels so both environmental scores and names remain readable.
 *   Display LODs are generated during epochs; fetch and color only the image appropriate to the zoom level.
 *   heatmapMode="sources" colors buildings and roads to show noise/pollution sources.
 *
 * Infrastructure display (infraSelection selects one network good and one supply-facility good):
 *   The network shows infrastructure goods utilization and flow (drawn on a second canvas for animation). coverage shows
 *   supply facilities, filled active ranges for facilities with ranges, per-facility outlines, and buildings colored by
 *   whether they are inside or outside coverage. Each facility has one pie chart for workload pressure.
 *   Fetching and drawing are in infraOverlay.ts. Building colors follow the same layer and scale visibility conditions as
 *   normal buildings. Like the heatmap, this colors the map, so callers must prevent selecting both at once.
 *
 * District value fills (zoneFill supplies values for each district):
 *   Represent district-level indicators, such as satisfaction, by filling district polygons. Fills lie below district
 *   outlines, so boundaries remain visible when used with the outline layer. The caller handles normalization and
 *   retains the actual value range, since only it has the actual values also shown in the legend.
 *   Value fills are mutually exclusive with heatmaps; do not pass both simultaneously.
 *
 * Data fetching:
 *   - Fetch paths, parcels, and buildings with a bbox covering the viewport plus padding.
 *     Debounce refetching when the viewport leaves the fetched area.
 *   - Fetch all districts because there are few of them (at most 100).
 *   - Determine arterial roads from each path's path_type and road_id (the master has no arterial-road column).
 *   - Fetch the citywide road list for road name labels because it is small. As with interchanges, refetch it on every
 *     vector fetch and on the "roads:changed" notification.
 *
 * Road highlighting (focusPathIds / previewPathIds):
 *   In road detail overviews and location search, overlay a road's sections as bands in its color above roads/tracks
 *   and below labels. preview marks unconfirmed candidates as dashed lines of the same color. focusDimOthers dims other roads/tracks.
 *
 * Compass (showCompass):
 *   The map is a fixed north-up view (world +Z points upward), so overlay a fixed, nonrotating SVG compass at the upper right.
 *   It is a DOM element rather than canvas content, unaffected by map redraws or view transforms.
 *
 * Parent components can use the ref (CityMapCanvasHandle) to reset the overview and reload data, and use overlayDraw
 * for overlays in world coordinates (roads under construction, line maps, etc.).
 */

import { forwardRef, useEffect, useImperativeHandle, useRef } from "react";
import { useTranslation } from "react-i18next";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import type { InterchangeListResponse, InterchangeSummary, MasterData } from "../../types";
import { decodeFloat16Array } from "../terrain/terrainData";
import { ELEVATION_RAMP, buildTerrainHillshade, terrainReliefColor } from "../terrain/terrainRelief";
import {
  PATH_MOTORWAY,
  PATH_STREET,
  PATH_WALKWAY,
  PATH_TRACK,
  MODEL_PARK,
  ROAD_COLOR_PALETTE,
} from "../../constants";
import { drawMapLabels, facilitySymbol, type MapPlace, type MapRoadLabel } from "./mapLabels";
import { RoadCenterline, roadSurface } from "./roadSurface";
import { drawTransitLines, type TransitLineGeometry } from "./transitLineDraw";
import {
  buildCoverageRender, buildNetworkRender, drawCoverageFill, drawCoverageOutlines, drawNetwork,
  COVERAGE_IN_RANGE_COLOR, COVERAGE_OUT_OF_RANGE_COLOR,
  type InfraCoverageRender, type InfraNetworkRender, type InfraSelection,
  type InfrastructureStats, type ServiceCoverageData,
} from "./infraOverlay";
import { interchangeDisplayName } from "../intersection/interchangeName";
import { fetchRoadList, hasRoad, ROADS_CHANGED_EVENT } from "../road/roadApi";
import type { RoadSummary } from "../road/roadTypes";
import {
  HEATMAP_RAMPS, effectiveHeatmapMode, heatmapPixels, pickHeatmapLod,
  type CityMapHeatmapKind, type CityMapHeatmapActiveKind, type CityMapHeatmapMode,
  type HeatmapGrid, type HeatmapSources,
} from "./mapHeatmap";
export { getHeatmapRampStops } from "./mapHeatmap";
export type { CityMapHeatmapKind, CityMapHeatmapActiveKind, CityMapHeatmapMode } from "./mapHeatmap";

/* Public types */

/** Layer visibility, controlled entirely through props. */
export type CityMapLayerVisibility = {
  /** Coastline */
  coastline: boolean;
  /** Arterial roads (highways and streets belonging to a road [road_id]) and road name labels. */
  trunkRoads: boolean;
  /** All roads (motorways, streets, and footpaths). */
  allRoads: boolean;
  /** Tracks */
  railways: boolean;
  /** Public transport lines (actual running geometry along roads/tracks, and stops). */
  transitLines: boolean;
  /** Parcel outlines */
  parcels: boolean;
  /** Building footprints */
  buildings: boolean;
  /** Draw buildings as simple 3D extrusions based on floor count (no effect when buildings is off). */
  buildings3d: boolean;
  /** Stations (convex hulls of platforms belonging to the same station building). */
  stations: boolean;
  /** Interchange (IC/JCT) symbols and names. */
  interchanges: boolean;
  /** District outlines */
  zoneBorders: boolean;
  /** Elevation colors and terrain shading. */
  elevation: boolean;
  /** Contour lines */
  contours: boolean;
  /** Grid */
  grid: boolean;
  /** Labels for district names; station, interchange, and facility symbols/names; and road names. */
  labels: boolean;
};

/**
 * Default layer visibility (only buildings, stations, interchanges, all roads, arterial roads, tracks, and labels are on).
 * Public transport line geometry is fetched citywide through a separate API, so embedded maps must enable it explicitly.
 */
export const DEFAULT_CITY_MAP_LAYERS: CityMapLayerVisibility = {
  coastline: false,
  trunkRoads: true,
  allRoads: true,
  railways: true,
  transitLines: false,
  parcels: false,
  buildings: true,
  buildings3d: false,
  stations: true,
  interchanges: true,
  zoneBorders: false,
  elevation: false,
  contours: false,
  grid: false,
  labels: true,
};

/** Information needed for the heatmap legend. Reported to the parent through onHeatmapStats. */
export type CityMapHeatmapStats = {
  kind: CityMapHeatmapActiveKind;
  mode: CityMapHeatmapMode;
  /** Overlays use adverse-impact scores from 0 to 1; source views use the citywide intensity range. */
  min: number;
  max: number;
  /** Number of pixels with values or number of sources. Zero means no data. */
  count: number;
};

/**
 * District value fill settings. Fills lie below district outlines, so they can be used with the outline layer.
 *
 * The caller handles normalization (which range maps to the endpoints) and retains the actual value range.
 * The appropriate endpoints vary by indicator: citywide absolute values or the minimum/maximum across districts.
 * Only the caller also has the actual values shown in the legend.
 */
export type CityMapZoneFill = {
  /** zone_id → value normalized to 0–1 (1 is the high end of the ramp). Districts without values are not filled. */
  values: Map<number, number>;
  /** Low-to-high color ramp. Defaults to ZONE_FILL_DEFAULT_RAMP (low = red, high = green). */
  ramp?: [number, number, number][];
};

/** Rectangular bounds in world coordinates (X-Z plane). */
export type CityMapBounds = { x1: number; z1: number; x2: number; z2: number };

/** View state passed to the overlayDraw callback. */
export type CityMapViewState = { offsetX: number; offsetY: number; zoom: number };

/**
 * Information passed to pointer callbacks. screen is in pixels relative to the canvas's upper-left corner;
 * world is the world coordinate (x, z) at that point. view is used for the inverse conversion (world → screen).
 */
export type CityMapPointerEvent = {
  world: { x: number; z: number };
  screen: { x: number; y: number };
  view: CityMapViewState;
  button: number;
  event: MouseEvent;
};

/** Operations parent components can call through the ref. */
export type CityMapCanvasHandle = {
  /** Reset the view to the full map (or initialBounds when specified). */
  resetView: () => void;
  /** Refetch all data and redraw while preserving the view. */
  reload: () => void;
  /**
 * Only pan/zoom the view to fit the specified world bounds.
 * Changing the initialBounds prop reruns the initialization effect and refetches terrain/vectors; this operation
 * updates only the view transform, without fetching. Use for embedded views with frequently changing bounds,
 * such as road previews during construction.
 */
  fitBounds: (bounds: CityMapBounds) => void;
  /**
 * Refetch only vectors (paths, parcels, buildings, districts, etc.) within the already fetched bounds.
 * Unlike reload, this does not refetch terrain/coastlines. Use to track data whose membership changes after
 * each operation, such as district editing.
 */
  reloadVectors: () => void;
};

/** Fetched vectors passed to onVectorsLoaded. Limited to the display bbox, except districts, which are fetched in full. */
export type CityMapVectors = {
  paths: CityMapPathItem[];
  zones: CityMapPolygonItem[];
  parcels: CityMapPolygonItem[];
  /** polygon uses coordinates local to center_point. */
  buildings: CityMapPolygonItem[];
};

export type CityMapCanvasProps = {
  apiBaseUrl: string;
  /** Layer visibility. Omitted keys use DEFAULT_CITY_MAP_LAYERS. */
  layers?: Partial<CityMapLayerVisibility>;
  /** Heatmap type to display (defaults to "none", meaning hidden). */
  heatmap?: CityMapHeatmapKind;
  /** Sunlight and public safety always use the overlay, without source display. */
  heatmapMode?: CityMapHeatmapMode;
  /**
 * line_agent_id of the line to emphasize in the public transport layer. Draw it in front with a green start ring
 * and red end ring, and dim other lines (for line detail maps). No effect when the transitLines layer is off.
 */
  transitFocusLineId?: number | null;
  /**
 * Line type to draw in the public transport layer (the line's path_variant = transit_master's transit_path_id).
 * Omit or pass null to draw all lines.
 */
  transitVariant?: number | null;
  /**
 * Path IDs of sections highlighted as bands in the road color (road detail overview or roads in location search).
 * Draw above roads/tracks and below labels. Only loaded sections within the display area are drawn,
 * so the caller must fit the display bounds to the sections.
 */
  focusPathIds?: number[] | null;
  /** Highlight band color ("#rrggbb"). Defaults to the first ROAD_COLOR_PALETTE entry. */
  focusColor?: string | null;
  /** Whether to dim other roads/tracks while highlighting. */
  focusDimOthers?: boolean;
  /** Path IDs of unconfirmed candidate sections drawn as dashed lines in the highlight color (e.g. straight-ahead extension candidates). */
  previewPathIds?: number[] | null;
  /**
 * Notification of the heatmap's actual value range and count, used to draw the legend.
 * Receives null when hidden.
 */
  onHeatmapStats?: (stats: CityMapHeatmapStats | null) => void;
  /**
 * District value fills (omitting this leaves districts unfilled). Like heatmaps, value fills are mutually exclusive;
 * using both mixes the colors and makes them unreadable.
 */
  zoneFill?: CityMapZoneFill;
  /**
 * Goods to display as infrastructure (omitted/null hides it). Overlay the network infrastructure good's network
 * and the coverage good's supply facilities, effect ranges, and building colors. Do not use with a heatmap.
 */
  infraSelection?: InfraSelection | null;
  /** Initial display bounds in world coordinates. Defaults to fitting the entire map. */
  initialBounds?: CityMapBounds;
  /** Whether to allow mouse/touch pan and zoom (false is recommended for embedded previews). */
  interactive?: boolean;
  /**
 * Whether to overlay a compass in the upper right. Defaults to interactive
 * (shown on standalone maps, hidden on static embedded backgrounds such as bases for line maps).
 */
  showCompass?: boolean;
  /**
 * Overlay callback called after drawing each layer.
 * ctx already has the world-coordinate view transform, so drawing can use world coordinates directly.
 * For a constant screen-space stroke width, use lineWidth = px / view.zoom.
 */
  overlayDraw?: (ctx: CanvasRenderingContext2D, view: CityMapViewState) => void;
  /**
 * Trigger for redrawing overlays. overlayDraw is kept in a ref mirror, so changes to the callback's contents
 * (drawing data or selection state) alone do not redraw. Increment this value to request a redraw when content changes.
 */
  overlayVersion?: number;
  /**
 * Overlay callback for continuous animation (e.g. infrastructure network flows).
 * When provided, this is called every frame in a requestAnimationFrame loop on a second canvas dedicated to animation
 * and transparent to mouse input. ctx has the same world-coordinate view transform as overlayDraw, and timeMs
 * receives the elapsed timestamp (performance.now()). The base map (terrain/vectors) is not fully redrawn, so the
 * per-frame drawing cost is limited to this callback. If omitted, no loop runs (no extra cost for static embedded uses).
 */
  animatedOverlayDraw?: (
    ctx: CanvasRenderingContext2D,
    view: CityMapViewState,
    timeMs: number
  ) => void;
  /** World coordinates under the mouse cursor (null when outside the canvas). */
  onCursorWorldPos?: (pos: { x: number; z: number } | null) => void;
  /**
 * Left-button press hook (interactive only). Return true to prevent this drag from panning and delegate subsequent
 * onPointerMove / onPointerUp events to the parent's drag operation (e.g. node movement).
 * Returning false or omitting the hook starts panning as usual.
 */
  onPointerDown?: (e: CityMapPointerEvent) => boolean;
  /** Pointer movement hook (received across the window for cursor tracking during drags; not called during pinches). */
  onPointerMove?: (e: CityMapPointerEvent) => void;
  /** End notification for a drag whose onPointerDown returned true. */
  onPointerUp?: (e: CityMapPointerEvent) => void;
  /** Left click/tap with less than 5 px of pan movement. Reported independently of existing drag operations. */
  onMapClick?: (e: CityMapPointerEvent) => void;
  /** Right-click (contextmenu) hook. Always suppress the default context menu. */
  onContextMenu?: (e: CityMapPointerEvent) => void;
  /** Canvas cursor (CSS cursor). The parent changes it according to the hovered object. */
  cursor?: string;
  /** Zoom level change notification. */
  onZoomChange?: (zoom: number) => void;
  /**
 * Notification that vectors finished loading. The parent can use the same data drawn on the map for hit testing, etc.
 * Districts are fetched only when zoneBorders is on, parcels when parcels is on, and buildings when buildings is on.
 */
  onVectorsLoaded?: (vectors: CityMapVectors) => void;
  className?: string;
};

/* Internal types and constants */

/** GET /api/v1/paths response item (only fields needed for rendering and parent hit testing). */
type ApiPathItem = {
  path_id: number;
  name?: string | null;
  center_point?: number[] | null;
  start_node_id?: number;
  end_node_id?: number;
  path_type: number;
  path_variant: number;
  start_point: number[];
  end_point: number[];
  waypoints?: (number[] | { position?: number[]; control_in?: number[] | null; control_out?: number[] | null })[];
  /** Endpoint Bézier control points [x, y, z]. Saved values take precedence over automatic calculation when drawing curves. */
  start_control?: number[] | null;
  end_control?: number[] | null;
  /** Whether this is a Bézier curve (1 = curve, 0 = straight/polyline). Interpolate only curves. */
  curved?: boolean | number;
  /** Path occupied polygon [[x,z],...] in world coordinates. Used to draw station convex hulls. */
  polygon?: [number, number][] | null;
  /** Width (m) */
  width?: number;
  /** ID of the station building path this belongs to. Valid only for platforms; unassigned or non-platform paths use 0 or less. */
  house_path_id?: number;
  /** ID of the road this belongs to. VALUE_NONE (-1) if unassigned. Used to identify arterial roads. */
  road_id?: number | null;
  /** District ID when both sides belong to the same district. Roads between two districts and unassigned roads use 0 or less. */
  zone_id?: number;
  /** District IDs for the left side (in the start → end direction) and right side. Unassigned sides use 0 or less. */
  left_zone_id?: number;
  right_zone_id?: number;
  status?: number;
};

/** Common shape for entities with polygons (parcels, districts, buildings). */
type ApiPolygonItem = {
  agent_id?: number;
  name?: string | null;
  type?: number;
  public_facility?: boolean | number;
  variant?: number;
  polygon?: [number, number][] | null;
  center_point?: number[] | null;
  /** Buildings only: floor count and tourist attraction type (0 = not a tourist attraction). Used for simple 3D height. */
  floors?: number;
  landmark?: number;
  /** Parcels only: parcel ID, used for selection and matching caller-provided values. */
  parcel_id?: number;
  /** Parcels only: adjoining path ID (0 or less if none) and which side of the road the parcel is on (PATH_SIDE_*; -1 if undetermined). */
  front_path_id?: number;
  front_path_side?: number;
  /** Districts only: district ID, used to match district value fills. */
  zone_id?: number;
  /** Districts only: identifying color ("#rrggbb"). */
  color?: string | null;
};

export type CityMapPathItem = ApiPathItem;
export type CityMapPolygonItem = ApiPolygonItem;

/** Road rendering category. street includes footpaths (PATH_WALKWAY). */
type RoadBandKind = "street" | "motorway";

/** Rendering unit grouping road surfaces with the same style and centerlines used for minimum stroke widths in distant views. */
type RoadBand = { kind: RoadBandKind; trunk: boolean; width: number; path: Path2D; surface: Path2D; color?: string };

/** One building extruded into simple 3D. The footprint is in world coordinates. */
type BuildingSolid = {
  ring: [number, number][];
  cx: number;
  cz: number;
  /** Floor count × floor height (m). */
  height: number;
  /** Footprint winding (counterclockwise = 1, clockwise = -1), used to determine whether a side face points toward the screen center. */
  winding: number;
  minX: number;
  minZ: number;
  maxX: number;
  maxZ: number;
  /** Roof color (same as the 2D footprint) and side-face color. */
  color: string;
  wallColor: string;
};

/** Fetched vector data and Path2D caches. */
type VectorState = {
  paths: ApiPathItem[];
  places: MapPlace[];
  parcels: ApiPolygonItem[];
  buildings: ApiPolygonItem[];
  zones: ApiPolygonItem[];
  /** Path2D caches built after data fetching or master data changes. */
  roadBands: RoadBand[];
  railPath: Path2D | null;
  railSourceBands: { path: Path2D; color: string }[];
  /** Building footprints grouped by fill color (normal light gray, source colors, or inside/outside coverage). */
  buildingBands: { path: Path2D; color: string }[];
  /** Buildings extruded into simple 3D (excluding parks). */
  buildingSolids: BuildingSolid[];
  parcelPath: Path2D | null;
  buildingPath: Path2D | null;
  parkPath: Path2D | null;
  /** Station convex hulls (platforms grouped by station building), drawn as translucent surfaces. */
  stationHullPath: Path2D | null;
  /** Public transport line running geometry (citywide, independent of bbox). */
  transitLines: TransitLineGeometry[];
  /** Interchanges (citywide, independent of bbox). */
  interchanges: InterchangeSummary[];
  /** Road list (citywide, independent of bbox), used for road name labels. */
  roads: RoadSummary[];
  /** Centerlines of highlighted sections and candidate sections. */
  focusPath: Path2D | null;
  previewPath: Path2D | null;
  /** District outlines, drawn as solid lines. */
  zonePath: Path2D | null;
  /** District value fills. With at most 100 districts, keep one fill per district without quantization. */
  zoneFillBands: { path: Path2D; color: string }[] | null;
  /** Bounds requested for the bbox fetch, used for coverage checks. */
  fetchedBounds: CityMapBounds | null;
  /** Types of data already fetched. */
  fetchedKinds: Set<VectorKind>;
  loading: boolean;
};

type VectorKind = "paths" | "parcels" | "buildings" | "zones" | "transit" | "interchanges" | "roads";

/** Selected environmental image or source intensity. Images are cached per LOD. */
type HeatmapState = {
  loadedKind: CityMapHeatmapKind;
  mode: CityMapHeatmapMode;
  requestKey: string;
  generation: number;
  grid: HeatmapGrid | null;
  images: Map<number, HTMLCanvasElement>;
  loadingLevels: Set<number>;
  pathValues: Map<number, number>;
  buildingValues: Map<number, number>;
  min: number;
  max: number;
  count: number;
  binColors: string[];
};

/** Terrain settings (GET /api/v1/terrain/settings). */
type TerrainSettings = {
  map_width: number;
  map_height: number;
  near_map_spacing: number;
  cell_size?: number;
};

/** One contour line (a Path2D for a given level). */
type ContourLine = { path: Path2D; major: boolean };

/**
 * Loaded elevation map at near-field resolution.
 * As in the terrain editor, retain the full-resolution elevation array as source data for the colored offscreen canvas
 * and for rebuilding contours when zoom changes.
 */
type TerrainState = {
  canvas: HTMLCanvasElement;
  baseCanvas: HTMLCanvasElement;
  elev: Float32Array;
  rows: number;
  cols: number;
  spacing: number;
  minLand: number;
  maxLand: number;
  /** Lowest land elevation color (used to fill reclaimed land when the elevation layer is on). */
  lowestLandColor: string;
};

/** Water background color. Rivers and lakes in coastline holes use the same blue. */
const BG_COLOR = "#a8d7ed";
const COLOR_LAND_RGB = [211, 248, 226] as const;
const COLOR_LAND = `rgb(${COLOR_LAND_RGB.join(",")})`;
const COLOR_PARK = "#a8dab5";
const COLOR_BUILDING_BORDER = "#cfd4dc";

/** Drawing colors for each layer. */
const COLOR_COASTLINE = "#85bfd5";
const COLOR_ZONE = "#839bad";
const COLOR_PARCEL = "#d5dccf";
const COLOR_BUILDING = "#e4e7ed";
/**
 * Field of view for the simple 3D viewpoint (degrees). Viewpoint height = screen half-diagonal ÷ tan(FOV / 2),
 * so buildings at screen corners lean outward by screen-space height × tan(FOV / 2).
 */
const BUILDING_3D_FOV_DEG = 60;
/** Brightness of simple 3D side faces (multiplier applied to the roof color). */
const BUILDING_WALL_SHADE = 0.85;
/** Floor height (m) for buildings whose master has none. Matches the core default. */
const DEFAULT_HEIGHT_PER_FLOOR = 3.5;
/*
 * Station convex hulls enclosing platforms belonging to the same station building.
 * A translucent red surface marks the extent of one station.
 */
const COLOR_STATION = "#d76a66";
/*
 * Roads are drawn as surfaces at their actual widths, with fill colors by type.
 * Arterial streets are slightly darker gray than other streets to reveal the road network's structure.
 */
const COLOR_STREET = "#c6ced3";
const COLOR_STREET_TRUNK = "#aab7c1";
const COLOR_MOTORWAY = "#778896";
const COLOR_RAIL = "#7c8389";
/** Color of the dotted city boundary (detailed terrain rectangle), and dot diameter/spacing (screen px, independent of zoom). */
const COLOR_CITY_BOUNDARY = "#5f666c";
const CITY_BOUNDARY_DOT_PX = 3;
const CITY_BOUNDARY_GAP_PX = 5;
/** Opacity of unhighlighted roads/tracks when highlighting roads. */
const FOCUS_DIM_ALPHA = 0.35;
/** Screen-space highlight band width (px). Independent of actual road width so routes remain traceable from a distance. */
const FOCUS_BAND_PX = 6;
const FOCUS_BAND_EDGE_PX = 9;
const PREVIEW_BAND_PX = 4;
/*
 * Contour colors: dark brown over terrain colors (as in the terrain editor),
 * and gray-green over the pale green background when the elevation layer is off to maintain visibility.
 */
const COLOR_CONTOUR_ON_TERRAIN = "rgba(40,28,14,0.5)";
const COLOR_CONTOUR_MAJOR_ON_TERRAIN = "rgba(30,20,10,0.75)";
const COLOR_CONTOUR_ON_BG = "rgba(94,113,80,0.22)";
const COLOR_CONTOUR_MAJOR_ON_BG = "rgba(94,113,80,0.4)";
const COLOR_GRID = "rgba(75,99,110,0.12)";
const COLOR_GRID_CELL = "rgba(75,99,110,0.3)";

/**
 * Number of source-display color bins. Quantize building/road intensities and group shapes with the same color
 * to limit draw calls even as the city grows.
 */
const HEATMAP_BIN_COUNT = 12;

/**
 * Default district value fill ramp (low → high). Red → yellow → green represents indicators where higher is better.
 * Its direction is opposite to HEATMAP_RAMPS for adverse-impact scores; do not confuse them.
 * The stop colors match the red, yellow, and green from common/Meter's getMeterColor.
 */
const ZONE_FILL_DEFAULT_RAMP: [number, number, number][] = [
  [239, 68, 68], [234, 179, 8], [34, 197, 94],
];

/** District fill opacity. Districts cover large areas, so use a lighter fill than heatmaps to keep map features visible. */
const ZONE_FILL_ALPHA = 0.45;

/** Grid spacing (m). Cell boundaries use cell_size from terrain settings. */
const GRID_MINOR_INTERVAL = 100;

/** Allowed zoom range. */
const ZOOM_MIN = 0.005;
const ZOOM_MAX = 1000;

/** Padding added to the viewport for bbox fetches (fraction of width/height). */
const FETCH_PAD = 0.5;
/** Margin for checking fetched-area coverage (must be smaller than FETCH_PAD). */
const COVER_MARGIN = 0.15;
/** Vector refetch debounce (ms). */
const VECTOR_DEBOUNCE_MS = 300;
/** Debounce for rebuilding contours after zoom changes (ms). */
const CONTOUR_DEBOUNCE_MS = 150;
/** Coalescing interval (ms) for road change notifications (sent on every click during section editing). */
const ROADS_REFRESH_DEBOUNCE_MS = 150;
/**
 * Zoom grace factor before refining contour intervals. Keep the base interval from the reference zoom
 * (full-map fit) up to this factor (same value as the terrain editor).
 */
const CONTOUR_REFINE_ZOOM_THRESHOLD = 6;
/** Maximum result count for bbox fetches. */
const FETCH_LIMIT = 10000;

/** Fallback road widths (m) by path_type before master data loads. */
const FALLBACK_ROAD_WIDTH: Record<number, number> = {
  [PATH_STREET]: 8,
  [PATH_MOTORWAY]: 16,
  [PATH_WALKWAY]: 2,
};

/* Pure helper functions */

/**
 * Return a CSS color at position t (0–1) on a color ramp.
 * Simple linear interpolation between stops lets map fills and legend gradients share the same ramp.
 */
function rampColor(stops: [number, number, number][], t: number): string {
  const clamped = Math.max(0, Math.min(1, t));
  const seg = (stops.length - 1) * clamped;
  const i = Math.min(stops.length - 2, Math.floor(seg));
  const f = seg - i;
  const a = stops[i];
  const b = stops[i + 1];
  const ch = (k: number) => Math.round(a[k] + (b[k] - a[k]) * f);
  return `rgb(${ch(0)}, ${ch(1)}, ${ch(2)})`;
}

/** Precompute the fill color for each bin, using the color at the bin's midpoint. */
function buildHeatmapBinColors(kind: CityMapHeatmapActiveKind): string[] {
  const stops = HEATMAP_RAMPS[kind];
  return Array.from({ length: HEATMAP_BIN_COUNT }, (_, i) =>
    rampColor(stops, (i + 0.5) / HEATMAP_BIN_COUNT)
  );
}

/**
 * Return CSS stop colors (low → high) for the district fill ramp.
 * Exported so legend gradients use the same colors as map fills.
 */
export function getZoneFillRampStops(ramp?: [number, number, number][]): string[] {
  return (ramp ?? ZONE_FILL_DEFAULT_RAMP).map(([r, g, b]) => `rgb(${r}, ${g}, ${b})`);
}

/** Apply soft-light shading over elevation colors and retain the resulting image without recalculating on pan/zoom. */
function buildTerrainCanvas(
  elev: Float32Array,
  rows: number,
  cols: number,
  minLand: number,
  maxLand: number,
  spacing: number,
  elevation: boolean
): HTMLCanvasElement {
  const cv = document.createElement("canvas");
  cv.width = cols;
  cv.height = rows;
  const ctx = cv.getContext("2d")!;
  const img = ctx.createImageData(cols, rows);
  const d = img.data;
  const shade = elevation ? buildTerrainHillshade(elev, rows, cols, spacing) : null;
  for (let i = 0; i < rows * cols; i++) {
    let rgb: readonly number[] = elev[i] <= 0 ? [168, 215, 237] : COLOR_LAND_RGB;
    if (shade && elev[i] > 0) {
      rgb = terrainReliefColor(elev[i], minLand, maxLand, shade[i]);
    }
    d[i * 4 + 0] = rgb[0];
    d[i * 4 + 1] = rgb[1];
    d[i * 4 + 2] = rgb[2];
    d[i * 4 + 3] = 255;
  }
  ctx.putImageData(img, 0, 0);
  return cv;
}

/** Choose a readable contour interval for the elevation range (approximately 25 lines or fewer). */
function pickContourInterval(range: number): number {
  if (range <= 0) return 100;
  const candidates = [10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000];
  for (const c of candidates) {
    if (range / c <= 25) return c;
  }
  return candidates[candidates.length - 1];
}

/** Return the largest "round" value in the 1-2-5 sequence (1, 2, 5, 10, 20, 50, ...) not exceeding value (minimum 1). */
function niceIntervalAtOrBelow(value: number): number {
  if (value <= 1) return 1;
  const magnitude = Math.pow(10, Math.floor(Math.log10(value)));
  const residual = value / magnitude;
  let niceResidual: number;
  if (residual >= 5) niceResidual = 5;
  else if (residual >= 2) niceResidual = 2;
  else niceResidual = 1;
  return niceResidual * magnitude;
}

/**
 * Refine contour intervals according to the current zoom (same logic as the terrain editor).
 * baseInterval is the interval at the reference zoom (full-map fit). Refine it along the 1-2-5 sequence for zoom
 * beyond CONTOUR_REFINE_ZOOM_THRESHOLD times the reference. Zooming out never makes it coarser than the base interval.
 */
function pickContourIntervalForZoom(baseInterval: number, baseZoom: number, currentZoom: number): number {
  if (baseZoom <= 0) return baseInterval;
  const refineStartZoom = baseZoom * CONTOUR_REFINE_ZOOM_THRESHOLD;
  if (currentZoom <= refineStartZoom) return baseInterval;
  const factor = currentZoom / refineStartZoom;
  return Math.max(1, niceIntervalAtOrBelow(baseInterval / factor));
}

/**
 * Build contours with Marching Squares (same algorithm as the terrain editor).
 * For each cell (2 × 2 grid points), add segments only when the four corner elevations straddle the level,
 * using linear interpolation to locate crossings on edges. Accumulate all segments in world-coordinate Path2D objects.
 * Mark every fifth contour major=true to change stroke width/color when drawing.
 * The caller chooses interval (initially pickContourInterval, then pickContourIntervalForZoom to refine with zoom).
 */
function buildContours(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  minLand: number,
  maxLand: number,
  interval: number
): ContourLine[] {
  const halfW = mapWidth / 2;
  const halfH = mapHeight / 2;

  // Contour levels (multiples of interval with 0 < L <= maxLand).
  const levels: number[] = [];
  const start = Math.ceil(minLand / interval) * interval;
  for (let L = start; L <= maxLand; L += interval) {
    if (L > 0) levels.push(L);
  }
  if (levels.length === 0 && maxLand > 0) levels.push(Math.max(start, interval));

  const contours: ContourLine[] = [];
  for (let li = 0; li < levels.length; li++) {
    const L = levels[li];
    const path = new Path2D();
    const major = li % 5 === 0;

    for (let r = 0; r < rows - 1; r++) {
      const baseZ = r * spacing - halfH;
      const rowBase = r * cols;
      const rowNext = rowBase + cols;
      for (let c = 0; c < cols - 1; c++) {
        const a = elev[rowBase + c];
        const b = elev[rowBase + c + 1];
        const cc = elev[rowNext + c + 1];
        const d = elev[rowNext + c];
        const mn = Math.min(a, b, cc, d);
        const mx = Math.max(a, b, cc, d);
        if (L < mn || L > mx) continue;

        const caseIdx =
          (a >= L ? 1 : 0) | (b >= L ? 2 : 0) | (cc >= L ? 4 : 0) | (d >= L ? 8 : 0);
        if (caseIdx === 0 || caseIdx === 15) continue;

        const baseX = c * spacing - halfW;
        const pTop = (L - a) / (b - a);
        const pRight = (L - b) / (cc - b);
        const pBottom = (L - d) / (cc - d);
        const pLeft = (L - a) / (d - a);

        const topX = baseX + pTop * spacing;
        const topZ = baseZ;
        const rightX = baseX + spacing;
        const rightZ = baseZ + pRight * spacing;
        const botX = baseX + pBottom * spacing;
        const botZ = baseZ + spacing;
        const leftX = baseX;
        const leftZ = baseZ + pLeft * spacing;

        const addSeg = (x1: number, z1: number, x2: number, z2: number) => {
          path.moveTo(x1, z1);
          path.lineTo(x2, z2);
        };
        switch (caseIdx) {
          case 1: addSeg(leftX, leftZ, topX, topZ); break;
          case 2: addSeg(topX, topZ, rightX, rightZ); break;
          case 3: addSeg(leftX, leftZ, rightX, rightZ); break;
          case 4: addSeg(rightX, rightZ, botX, botZ); break;
          case 5: addSeg(topX, topZ, rightX, rightZ); addSeg(leftX, leftZ, botX, botZ); break;
          case 6: addSeg(topX, topZ, botX, botZ); break;
          case 7: addSeg(leftX, leftZ, botX, botZ); break;
          case 8: addSeg(leftX, leftZ, botX, botZ); break;
          case 9: addSeg(topX, topZ, botX, botZ); break;
          case 10: addSeg(topX, topZ, leftX, leftZ); addSeg(rightX, rightZ, botX, botZ); break;
          case 11: addSeg(rightX, rightZ, botX, botZ); break;
          case 12: addSeg(leftX, leftZ, rightX, rightZ); break;
          case 13: addSeg(topX, topZ, rightX, rightZ); break;
          case 14: addSeg(leftX, leftZ, topX, topZ); break;
          default: break;
        }
      }
    }
    contours.push({ path, major });
  }
  return contours;
}

/** Build grid lines at the specified interval as a world-coordinate Path2D. */
function buildGridPath(mapWidth: number, mapHeight: number, interval: number): Path2D {
  const path = new Path2D();
  const halfW = mapWidth / 2;
  const halfH = mapHeight / 2;
  for (let x = -halfW; x <= halfW; x += interval) {
    path.moveTo(x, -halfH);
    path.lineTo(x, halfH);
  }
  for (let z = -halfH; z <= halfH; z += interval) {
    path.moveTo(-halfW, z);
    path.lineTo(halfW, z);
  }
  return path;
}

/** Bézier control points ([x,z]) for one anchor. null/undefined means unspecified (filled automatically). */
type AnchorControls = { cIn?: [number, number] | null; cOut?: [number, number] | null };

/** Flatten [x,y,z] or [x,z] coordinates to [x,z]. Return null for invalid coordinates. */
function toXZ(p: number[] | null | undefined): [number, number] | null {
  if (!p || p.length < 2) return null;
  return p.length >= 3 ? [p[0], p[2]] : [p[0], p[1]];
}

/**
 * Collect path anchors (start → waypoints → end) as [x,z].
 * Discard y (elevation) from 3D [x,y,z] coordinates. Waypoints also support the {position:[...]} form.
 * controls holds Bézier control points in anchor order (start_control/end_control for endpoints,
 * and waypoint controls from the {position,control_in,control_out} form for intermediate anchors).
 * Skip controls together with invalid-coordinate anchors to preserve index alignment.
 */
function collectPathPoints(item: ApiPathItem, controls?: AnchorControls[]): [number, number][] {
  const raw: { p: number[] | undefined; ctrl: AnchorControls }[] = [
    { p: item.start_point, ctrl: { cOut: toXZ(item.start_control) } },
  ];
  if (item.waypoints) {
    for (const wp of item.waypoints) {
      if (Array.isArray(wp)) {
        raw.push({ p: wp, ctrl: {} });
      } else {
        raw.push({ p: wp?.position, ctrl: { cIn: toXZ(wp?.control_in), cOut: toXZ(wp?.control_out) } });
      }
    }
  }
  raw.push({ p: item.end_point, ctrl: { cIn: toXZ(item.end_control) } });

  const pts: [number, number][] = [];
  for (const entry of raw) {
    const xz = toXZ(entry.p);
    if (!xz) continue;
    pts.push(xz);
    controls?.push(entry.ctrl);
  }
  return pts;
}

/** Add polylines to Path2D (for straight paths, curved=0). */
function appendPolyline(path: Pick<Path2D, "moveTo" | "lineTo">, pts: [number, number][]): void {
  if (pts.length < 2) return;
  path.moveTo(pts[0][0], pts[0][1]);
  for (let i = 1; i < pts.length; i++) path.lineTo(pts[i][0], pts[i][1]);
}

/**
 * Add anchors to Path2D as a smooth curve (for curved paths, curved=1).
 * Prefer saved Bézier controls in controls (values adjusted by user dragging), filling gaps with controls derived
 * from Catmull-Rom (1/6 of the adjacent anchor difference). This uses the same formula as the backend's
 * _auto_fill_controls automatic completion.
 */
function appendSmoothPolyline(
  path: Pick<Path2D, "moveTo" | "lineTo" | "bezierCurveTo">,
  pts: [number, number][],
  controls?: AnchorControls[]
): void {
  if (pts.length < 2) return;
  path.moveTo(pts[0][0], pts[0][1]);
  if (pts.length === 2) {
    // Only two points: use a curve if controls are saved; otherwise a straight line (automatic controls at 1/3 and 2/3 produce a straight line).
    const cOut = controls?.[0]?.cOut;
    const cIn = controls?.[1]?.cIn;
    if (cOut || cIn) {
      const s = pts[0];
      const e = pts[1];
      const c1 = cOut ?? [s[0] + (e[0] - s[0]) / 3, s[1] + (e[1] - s[1]) / 3];
      const c2 = cIn ?? [s[0] + (e[0] - s[0]) * 2 / 3, s[1] + (e[1] - s[1]) * 2 / 3];
      path.bezierCurveTo(c1[0], c1[1], c2[0], c2[1], e[0], e[1]);
    } else {
      path.lineTo(pts[1][0], pts[1][1]);
    }
    return;
  }
  for (let i = 0; i < pts.length - 1; i++) {
    const prev = pts[Math.max(0, i - 1)];
    const curr = pts[i];
    const next = pts[i + 1];
    const after = pts[Math.min(pts.length - 1, i + 2)];
    const cOut = controls?.[i]?.cOut;
    const cIn = controls?.[i + 1]?.cIn;
    path.bezierCurveTo(
      cOut ? cOut[0] : curr[0] + (next[0] - prev[0]) / 6,
      cOut ? cOut[1] : curr[1] + (next[1] - prev[1]) / 6,
      cIn ? cIn[0] : next[0] - (after[0] - curr[0]) / 6,
      cIn ? cIn[1] : next[1] - (after[1] - curr[1]) / 6,
      next[0],
      next[1]
    );
  }
}

/**
 * Return a path's centerline as a polyline. For curved paths, flatten the same Bézier used to draw the map.
 * Exported for following road shapes outside the map renderer (e.g. brush hit testing in district editing).
 */
export function pathCenterline(item: ApiPathItem): [number, number][] {
  const controls: AnchorControls[] = [];
  const pts = collectPathPoints(item, controls);
  const centerline = new RoadCenterline();
  if (item.curved) appendSmoothPolyline(centerline, pts, controls);
  else appendPolyline(centerline, pts);
  return centerline.points;
}

/**
 * Convert entities with polygons to Path2D.
 * With localPolygon=true, treat polygon as local to center_point and add center_point as the offset
 * (the format used by buildings).
 */
function buildPolygonPath(items: ApiPolygonItem[], localPolygon: boolean): Path2D {
  const path = new Path2D();
  for (const item of items) {
    const poly = item.polygon;
    if (!poly || poly.length < 3) continue;
    const ox = localPolygon && item.center_point ? item.center_point[0] : 0;
    const oz = localPolygon && item.center_point ? item.center_point[1] : 0;
    path.moveTo(poly[0][0] + ox, poly[0][1] + oz);
    for (let i = 1; i < poly.length; i++) {
      path.lineTo(poly[i][0] + ox, poly[i][1] + oz);
    }
    path.closePath();
  }
  return path;
}

/** Simple 3D side-face color. Uniformly darken the roof color ("#rrggbb" or "rgb(r, g, b)"). */
function wallColorOf(color: string): string {
  const hex = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(color);
  const rgb = hex ? hex.slice(1).map((h) => parseInt(h, 16)) : (color.match(/\d+/g) ?? []).slice(0, 3).map(Number);
  return `rgb(${rgb.map((v) => Math.round(v * BUILDING_WALL_SHADE)).join(", ")})`;
}

/** Convert a building footprint (local to center_point) into a world-coordinate solid. Return null for zero-area footprints. */
function buildBuildingSolid(item: ApiPolygonItem, height: number, color: string, wallColor: string): BuildingSolid | null {
  const poly = item.polygon;
  const center = toXZ(item.center_point);
  if (!poly || poly.length < 3 || !center) return null;
  const ring = poly.map(([x, z]): [number, number] => [x + center[0], z + center[1]]);
  let area = 0;
  let minX = Infinity, minZ = Infinity, maxX = -Infinity, maxZ = -Infinity;
  ring.forEach(([x, z], i) => {
    const [nx, nz] = ring[(i + 1) % ring.length];
    area += x * nz - nx * z;
    minX = Math.min(minX, x);
    minZ = Math.min(minZ, z);
    maxX = Math.max(maxX, x);
    maxZ = Math.max(maxZ, z);
  });
  if (area === 0) return null;
  return { ring, cx: center[0], cz: center[1], height, winding: Math.sign(area), minX, minZ, maxX, maxZ, color, wallColor };
}

/**
 * Return a point set's convex hull in counterclockwise order (Andrew's monotone chain algorithm).
 * Return fewer than three points unchanged. Used to compute enclosing polygons for stations (platform groups).
 */
function convexHull(pts: [number, number][]): [number, number][] {
  if (pts.length < 3) return pts.slice();
  const p = [...pts].sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  const cross = (o: [number, number], a: [number, number], b: [number, number]) =>
    (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
  const lower: [number, number][] = [];
  for (const pt of p) {
    while (lower.length >= 2 && cross(lower[lower.length - 2], lower[lower.length - 1], pt) <= 0) lower.pop();
    lower.push(pt);
  }
  const upper: [number, number][] = [];
  for (let i = p.length - 1; i >= 0; i--) {
    const pt = p[i];
    while (upper.length >= 2 && cross(upper[upper.length - 2], upper[upper.length - 1], pt) <= 0) upper.pop();
    upper.push(pt);
  }
  // Drop each chain's last point because it duplicates the starting point.
  lower.pop();
  upper.pop();
  return lower.concat(upper);
}

/**
 * Build Path2D geometry representing stations (the logic in [[19_駅と駅舎]]).
 * Group platforms (station paths with positive platform_width in the master) by station building (house_path_id).
 * Draw the convex hull of all occupied-polygon vertices belonging to the same station building as one closed polygon.
 * Do not draw platforms unassigned to a station building. A station building with one platform uses that platform's
 * rectangle directly as its convex hull.
 */
function buildStationHullPath(paths: ApiPathItem[], md: MasterData | null): Path2D | null {
  // house_path_id → polygon vertices of all platforms belonging to that station building.
  const groups = new Map<number, [number, number][]>();
  for (const p of paths) {
    const houseId = p.house_path_id ?? 0;
    if (houseId <= 0) continue;
    const pw = md?.paths?.[p.path_variant]?.platform_width;
    if (!(typeof pw === "number" && pw > 0)) continue; // Platforms (station paths) only.
    const poly = p.polygon;
    if (!poly || poly.length < 2) continue;
    let acc = groups.get(houseId);
    if (!acc) {
      acc = [];
      groups.set(houseId, acc);
    }
    for (const v of poly) acc.push([v[0], v[1]]);
  }
  if (groups.size === 0) return null;

  const path = new Path2D();
  for (const verts of groups.values()) {
    const hull = convexHull(verts);
    if (hull.length < 3) continue;
    path.moveTo(hull[0][0], hull[0][1]);
    for (let i = 1; i < hull.length; i++) path.lineTo(hull[i][0], hull[i][1]);
    path.closePath();
  }
  return path;
}

/**
 * Convert Polygon/MultiPolygon data from the GeoJSON-like coastline API to Path2D.
 * Draw inner rings (holes) as well as outer land boundaries. These holes result from subtracting inland lake/pond
 * water polygons from the land, so their shorelines use the same strokes as the coastline (the backend's
 * compute_coastline_polygon subtracts water footprints with difference).
 */
function buildCoastlinePath(geojson: { type?: string; coordinates?: unknown }): Path2D {
  const path = new Path2D();
  const addRing = (ring: [number, number][]) => {
    if (!ring || ring.length < 3) return;
    path.moveTo(ring[0][0], ring[0][1]);
    for (let i = 1; i < ring.length; i++) path.lineTo(ring[i][0], ring[i][1]);
    path.closePath();
  };
  // Draw all rings in one Polygon ([outer ring, hole ring, ...]).
  const addPolygon = (rings: [number, number][][]) => {
    if (!rings) return;
    for (const ring of rings) addRing(ring);
  };
  const coords = geojson?.coordinates as [number, number][][] | [number, number][][][] | undefined;
  if (!coords || !coords.length) return path;
  if (geojson.type === "Polygon") {
    addPolygon(coords as [number, number][][]);
  } else if (geojson.type === "MultiPolygon") {
    for (const polyCoords of coords as [number, number][][][]) {
      addPolygon(polyCoords);
    }
  }
  return path;
}

/** Whether a fully contains b with a margin. Used for viewport coverage checks. */
function coversWithMargin(outer: CityMapBounds, inner: CityMapBounds, marginRatio: number): boolean {
  const mw = (outer.x2 - outer.x1) * marginRatio;
  const mh = (outer.z2 - outer.z1) * marginRatio;
  return (
    inner.x1 >= outer.x1 + mw &&
    inner.z1 >= outer.z1 + mh &&
    inner.x2 <= outer.x2 - mw &&
    inner.z2 <= outer.z2 - mh
  );
}

/* Compass (fixed overlay) */

/** Compass SVG side length (px), overlaid at the map's upper right. */
const COMPASS_SIZE = 76;
/** Compass SVG viewBox side length. All shape coordinates are defined within this square. */
const COMPASS_VIEW = 100;
/** Compass rose center. */
const COMPASS_C = COMPASS_VIEW / 2;
/** Petal waist radius. Adjacent directions share points on this radius. */
const COMPASS_WAIST = 11;

/** One triangular compass rose petal. A light/dark pair forms one direction to create three-dimensional shading. */
type CompassPetal = { points: string; fill: string };

/*
 * Petal colors. Only north uses red hues for immediate recognition; other directions are achromatic.
 * Split each direction into a light counterclockwise half and dark clockwise half to give a flat SVG
 * origami-like shading (the traditional compass rose technique).
 */
const COMPASS_FILL_NORTH_LIGHT = "#e27777";
const COMPASS_FILL_NORTH_DARK = "#9d4055";
const COMPASS_FILL_LIGHT = "#cdd6f4";
const COMPASS_FILL_DARK = "#70808d";

/** Convert polar coordinates about the center (clockwise degrees from north = 0) to SVG coordinates. SVG Y points downward. */
function compassPolar(angleDeg: number, radius: number): string {
  const rad = (angleDeg * Math.PI) / 180;
  return `${COMPASS_C + Math.sin(rad) * radius},${COMPASS_C - Math.cos(rad) * radius}`;
}

/**
 * Generate compass rose petals (8 directions × 2 light/dark halves = 16 triangles).
 * Each direction comprises two center-tip-waist triangles. Adjacent directions share waist points
 * (at ±22.5° from the direction, radius COMPASS_WAIST), closing the star without gaps or overlaps.
 * Cardinal directions (N/E/S/W) are longer; intermediate directions (NE, etc.) are shorter.
 */
function buildCompassPetals(): CompassPetal[] {
  const petals: CompassPetal[] = [];
  for (let i = 0; i < 8; i++) {
    const angle = i * 45;
    const tip = compassPolar(angle, i % 2 === 0 ? 35 : 20);
    const center = `${COMPASS_C},${COMPASS_C}`;
    const north = i === 0;
    petals.push({
      points: `${center} ${tip} ${compassPolar(angle - 22.5, COMPASS_WAIST)}`,
      fill: north ? COMPASS_FILL_NORTH_LIGHT : COMPASS_FILL_LIGHT,
    });
    petals.push({
      points: `${center} ${tip} ${compassPolar(angle + 22.5, COMPASS_WAIST)}`,
      fill: north ? COMPASS_FILL_NORTH_DARK : COMPASS_FILL_DARK,
    });
  }
  return petals;
}

/** Petals are fixed shapes, so assemble them once at module load. */
const COMPASS_PETALS = buildCompassPetals();

/** Direction label positions (radius 44 from the center), in clockwise degrees from north = 0. */
const COMPASS_LABELS: { key: string; angle: number; north?: boolean }[] = [
  { key: "cityMap.compass.north", angle: 0, north: true },
  { key: "cityMap.compass.east", angle: 90 },
  { key: "cityMap.compass.south", angle: 180 },
  { key: "cityMap.compass.west", angle: 270 },
];

/**
 * Compass overlaid at the map's upper right.
 * The map is a fixed north-up view (world +Z points upward), so a completely static SVG suffices.
 * Pass mouse input through to the canvas beneath.
 */
function MapCompass() {
  const { t } = useTranslation();
  return (
    <svg
      viewBox={`0 0 ${COMPASS_VIEW} ${COMPASS_VIEW}`}
      width={COMPASS_SIZE}
      height={COMPASS_SIZE}
      style={{ position: "absolute", top: 10, right: 10, pointerEvents: "none" }}
      aria-hidden="true"
    >
      <circle cx={COMPASS_C} cy={COMPASS_C} r={47} fill="rgba(255,255,255,0.9)" stroke="#c7d0d4" strokeWidth={1.5} />
      <circle cx={COMPASS_C} cy={COMPASS_C} r={39} fill="none" stroke="#dce2e4" strokeWidth={1} />
      {COMPASS_PETALS.map((petal, i) => (
        <polygon key={i} points={petal.points} fill={petal.fill} />
      ))}
      <circle cx={COMPASS_C} cy={COMPASS_C} r={3} fill="#1e1e2e" stroke="#cdd6f4" strokeWidth={1} />
      {COMPASS_LABELS.map((label) => {
        const rad = (label.angle * Math.PI) / 180;
        return (
          <text
            key={label.key}
            x={COMPASS_C + Math.sin(rad) * 43}
            y={COMPASS_C - Math.cos(rad) * 43}
            textAnchor="middle"
            dominantBaseline="central"
            fontSize={12}
            fontWeight={label.north ? 700 : 500}
            fill={label.north ? COMPASS_FILL_NORTH_DARK : "#53636d"}
          >
            {t(label.key)}
          </text>
        );
      })}
    </svg>
  );
}

/* Component implementation */

export const CityMapCanvas = forwardRef<CityMapCanvasHandle, CityMapCanvasProps>(
  function CityMapCanvas(
    {
      apiBaseUrl,
      layers,
      heatmap = "none",
      heatmapMode = "overlay",
      transitFocusLineId = null,
      transitVariant = null,
      focusPathIds = null,
      focusColor = null,
      focusDimOthers = false,
      previewPathIds = null,
      initialBounds,
      interactive = true,
      showCompass,
      overlayDraw,
      overlayVersion,
      animatedOverlayDraw,
      onCursorWorldPos,
      onPointerDown,
      onPointerMove,
      onPointerUp,
      onMapClick,
      onContextMenu,
      cursor,
      onZoomChange,
      onHeatmapStats,
      onVectorsLoaded,
      zoneFill,
      infraSelection = null,
      className,
    },
    ref
  ) {
    const masterData = useMasterData();
    const wrapRef = useRef<HTMLDivElement | null>(null);
    const canvasRef = useRef<HTMLCanvasElement | null>(null);
    // Second canvas dedicated to animated overlays, placed above the base canvas.
    // The requestAnimationFrame loop redraws only this canvas each frame
    // (separate layers avoid fully redrawing the base map).
    const animCanvasRef = useRef<HTMLCanvasElement | null>(null);

    // Mirror layers, callbacks, and master data in refs so event handlers and async tasks
    // always see their latest values without reinstalling handlers.
    const layersRef = useRef<CityMapLayerVisibility>({ ...DEFAULT_CITY_MAP_LAYERS, ...layers });
    const heatmapRef = useRef<CityMapHeatmapKind>(heatmap);
    const heatmapModeRef = useRef<CityMapHeatmapMode>(heatmapMode);
    const zoneFillRef = useRef<CityMapZoneFill | undefined>(zoneFill);
    const transitFocusRef = useRef<number | null>(transitFocusLineId);
    const transitVariantRef = useRef<number | null>(transitVariant);
    const roadFocusRef = useRef({ focusPathIds, previewPathIds, focusColor, focusDimOthers });
    roadFocusRef.current = { focusPathIds, previewPathIds, focusColor, focusDimOthers };
    const overlayDrawRef = useRef<CityMapCanvasProps["overlayDraw"]>(overlayDraw);
    const animatedOverlayDrawRef = useRef<CityMapCanvasProps["animatedOverlayDraw"]>(animatedOverlayDraw);
    const cursorCbRef = useRef<CityMapCanvasProps["onCursorWorldPos"]>(onCursorWorldPos);
    const pointerCbRef = useRef({ onPointerDown, onPointerMove, onPointerUp, onContextMenu, onMapClick });
    const zoomCbRef = useRef<CityMapCanvasProps["onZoomChange"]>(onZoomChange);
    const heatmapStatsCbRef = useRef<CityMapCanvasProps["onHeatmapStats"]>(onHeatmapStats);
    const vectorsCbRef = useRef<CityMapCanvasProps["onVectorsLoaded"]>(onVectorsLoaded);
    const infraSelectionRef = useRef<InfraSelection | null>(infraSelection);
    const masterDataRef = useRef<MasterData | null>(masterData);
    // Unnamed interchange labels are translated at draw time, so also update them when the language changes.
    const { t } = useTranslation();
    const tRef = useRef(t);
    tRef.current = t;
    overlayDrawRef.current = overlayDraw;
    animatedOverlayDrawRef.current = animatedOverlayDraw;
    cursorCbRef.current = onCursorWorldPos;
    pointerCbRef.current = { onPointerDown, onPointerMove, onPointerUp, onContextMenu, onMapClick };
    zoomCbRef.current = onZoomChange;
    heatmapStatsCbRef.current = onHeatmapStats;
    vectorsCbRef.current = onVectorsLoaded;

    // Rendering engine operations. Constructed in the initialization effect and called from other effects
    // (such as layer changes) and useImperativeHandle.
    const engineRef = useRef<{
      draw: () => void;
      drawAnimatedFrame: (timeMs: number) => void;
      resetView: () => void;
      reload: () => void;
      fitBounds: (bounds: CityMapBounds) => void;
      reloadVectors: () => void;
      ensureVectors: () => void;
      ensureHeatmap: () => void;
      ensureInfra: () => void;
      rebuildPathClassification: () => void;
      rebuildZoneFill: () => void;
      rebuildRoadFocus: () => void;
    } | null>(null);

    // Key initialBounds by value because it is an object, avoiding reinitialization when its values are unchanged.
    const boundsKey = initialBounds
      ? `${initialBounds.x1},${initialBounds.z1},${initialBounds.x2},${initialBounds.z2}`
      : "";
    const initialBoundsRef = useRef<CityMapBounds | undefined>(initialBounds);
    initialBoundsRef.current = initialBounds;

    /* ---- Layer changes: update refs, fetch missing data, and redraw. ---- */
    useEffect(() => {
      layersRef.current = { ...DEFAULT_CITY_MAP_LAYERS, ...layers };
      engineRef.current?.ensureVectors();
      engineRef.current?.draw();
    }, [layers]);

    /* ---- Language changes: redraw translated labels (default interchange names). ---- */
    useEffect(() => {
      engineRef.current?.draw();
    }, [t]);

    /* ---- Focused line changes: shared data only needs redrawing. ---- */
    useEffect(() => {
      transitFocusRef.current = transitFocusLineId;
      engineRef.current?.draw();
    }, [transitFocusLineId]);

    /* ---- Public transport type changes: all line data is available, so only redraw. ---- */
    useEffect(() => {
      transitVariantRef.current = transitVariant;
      engineRef.current?.draw();
    }, [transitVariant]);

    /* ---- Road highlight changes: rebuild section centerlines and redraw (fetch sections if not loaded). ---- */
    const focusKey = (focusPathIds ?? []).join(",");
    const previewKey = (previewPathIds ?? []).join(",");
    useEffect(() => {
      engineRef.current?.ensureVectors();
      engineRef.current?.rebuildRoadFocus();
      engineRef.current?.draw();
    }, [focusKey, previewKey, focusColor, focusDimOthers]);

    /* ---- Environmental type/display mode changes. ---- */
    useEffect(() => {
      heatmapRef.current = heatmap;
      heatmapModeRef.current = heatmapMode;
      engineRef.current?.ensureVectors();
      engineRef.current?.ensureHeatmap();
      engineRef.current?.draw();
    }, [heatmap, heatmapMode]);

    /* ---- Infrastructure good changes: refetch data and redraw. ---- */
    const infraNetworkId = infraSelection?.network ?? null;
    const infraCoverageId = infraSelection?.coverage ?? null;
    useEffect(() => {
      infraSelectionRef.current = { network: infraNetworkId, coverage: infraCoverageId };
      engineRef.current?.ensureInfra();
    }, [infraNetworkId, infraCoverageId]);

    /* ---- District value fill changes: load district data, rebuild Path2D, and redraw. ---- */
    useEffect(() => {
      zoneFillRef.current = zoneFill;
      engineRef.current?.ensureVectors();
      engineRef.current?.rebuildZoneFill();
      engineRef.current?.draw();
    }, [zoneFill]);

    /* ---- Master data changes: rebuild Path2D because road widths and park/station classifications may change. ---- */
    useEffect(() => {
      masterDataRef.current = masterData;
      engineRef.current?.rebuildPathClassification();
      engineRef.current?.draw();
    }, [masterData]);

    /* ---- Overlay update request: redraw when overlayDraw content changes. ---- */
    useEffect(() => {
      engineRef.current?.draw();
    }, [overlayVersion]);

    /* ---- Cursor shape: follow the parent's hover detection. ---- */
    useEffect(() => {
      if (canvasRef.current) canvasRef.current.style.cursor = cursor ?? "";
    }, [cursor]);

    /* ---- Initialization (load data and register events). Rebuild when apiBaseUrl, etc. change. ---- */
    useEffect(() => {
      const canvas = canvasRef.current;
      const wrap = wrapRef.current;
      if (!canvas || !wrap || !apiBaseUrl) return;

      const ctx = canvas.getContext("2d");
      if (!ctx) return;

      // Animation overlay canvas (if unavailable, disable only animation and continue).
      const animCanvas = animCanvasRef.current;
      const animCtx = animCanvas?.getContext("2d") ?? null;

      const abort = new AbortController();
      let disposed = false;

      /* ---- Internal state ---- */
      const view: CityMapViewState = { offsetX: 0, offsetY: 0, zoom: 1 };
      let canvasW = 0;
      let canvasH = 0;
      /**
 * Whether the initial view is established. view retains its initial values until resetView after terrain fetching.
 * A bbox fetch before then would load an irrelevant area near the world origin and record it as fetchedBounds,
 * which would also interfere with refetch decisions.
 */
      let viewReady = false;
      /** Whether another fetch request (additional data types or view movement) arrived during fetching. Refetch after completion. */
      let vectorLoadPending = false;

      let settings: TerrainSettings | null = null;
      let terrain: TerrainState | null = null;
      let contours: ContourLine[] = [];
      let gridMinor: Path2D | null = null;
      let gridCell: Path2D | null = null;
      let coastPath: Path2D | null = null;
      let backgroundLandPath: Path2D | null = null;
      let reclaimedPath: Path2D | null = null;

      // Adapt contour intervals to zoom, as in the terrain editor.
      // contourBaseInterval/contourBaseZoom are the interval and zoom at full-map fit;
      // refine the interval along the 1-2-5 sequence as zoom increases from there.
      // Keep contours built at contourInterval at all times.
      let contourBaseInterval = 100;
      let contourBaseZoom = 1;
      let contourInterval = 100;

      const vec: VectorState = {
        paths: [],
        places: [],
        parcels: [],
        buildings: [],
        zones: [],
        roadBands: [],
        railPath: null,
        railSourceBands: [],
        buildingBands: [],
        buildingSolids: [],
        parcelPath: null,
        buildingPath: null,
        parkPath: null,
        stationHullPath: null,
        transitLines: [],
        interchanges: [],
        roads: [],
        focusPath: null,
        previewPath: null,
        zonePath: null,
        zoneFillBands: null,
        fetchedBounds: null,
        fetchedKinds: new Set(),
        loading: false,
      };

      const heat: HeatmapState = {
        loadedKind: "none", mode: "overlay", requestKey: "", generation: 0,
        grid: null, images: new Map(), loadingLevels: new Set(),
        pathValues: new Map(), buildingValues: new Map(),
        min: 0, max: 0, count: 0, binColors: [],
      };

      /** Infrastructure display. requestKey identifies the network and coverage goods to avoid duplicate fetches for the same goods. */
      const infra: {
        requestKey: string;
        generation: number;
        network: InfraNetworkRender | null;
        coverage: InfraCoverageRender | null;
      } = { requestKey: "", generation: 0, network: null, coverage: null };
      const reducedMotion = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ?? false;

      let vectorTimer: number | null = null;
      let contourTimer: number | null = null;

      /*
 * ---- Coordinate conversion ----
 * offsetX is world x at the screen's left edge (sx=0); offsetY is world z at its top edge (sy=0).
 * Y is flipped to keep north (+Z) up, so screen_y = (offsetY - wz) * zoom.
 */
      const screenToWorld = (sx: number, sy: number): [number, number] => [
        sx / view.zoom + view.offsetX,
        view.offsetY - sy / view.zoom,
      ];

      const getViewBounds = (): CityMapBounds => {
        // The top is north (maximum z) and bottom is south (minimum z), so reorder to ensure z1 < z2.
        const [x1, zTop] = screenToWorld(0, 0);
        const [x2, zBottom] = screenToWorld(canvasW, canvasH);
        return { x1, z1: Math.min(zTop, zBottom), x2, z2: Math.max(zTop, zBottom) };
      };

      /** Set the view to fit the specified bounds. */
      const setViewToBounds = (b: CityMapBounds) => {
        const pad = 20;
        const dataW = Math.max(1, b.x2 - b.x1);
        const dataH = Math.max(1, b.z2 - b.z1);
        const zoom = Math.min(
          (Math.max(50, canvasW) - pad * 2) / dataW,
          (Math.max(50, canvasH) - pad * 2) / dataH
        );
        view.zoom = Math.max(ZOOM_MIN, Math.min(ZOOM_MAX, zoom));
        view.offsetX = b.x1 - (canvasW / view.zoom - dataW) / 2;
        // offsetY is world z at the screen's top edge (the northern side of the view). Add padding northward to center the bounds.
        view.offsetY = b.z2 + (canvasH / view.zoom - dataH) / 2;
        zoomCbRef.current?.(view.zoom);
      };

      const resetView = () => {
        viewReady = true;
        const ib = initialBoundsRef.current;
        if (ib) {
          setViewToBounds(ib);
        } else if (settings) {
          setViewToBounds({
            x1: -settings.map_width / 2,
            z1: -settings.map_height / 2,
            x2: settings.map_width / 2,
            z2: settings.map_height / 2,
          });
        } else {
          // Fallback before terrain settings load: place the origin (0,0) at the screen center
          // (offsetY is world z at the top edge, hence +canvasH/2).
          view.offsetX = -canvasW / 2;
          view.offsetY = canvasH / 2;
          view.zoom = 1;
          zoomCbRef.current?.(view.zoom);
        }
      };

      /**
 * Draw buildings as simple 3D (ctx already has the world-coordinate view transform).
 * The viewpoint is directly above the screen center, at the distance derived from the screen half-diagonal and FOV.
 * Zooming in lowers it, making buildings lean farther outward. Translate roofs by
 * (building center − screen center) × height / viewpoint height.
 * Draw only sides facing the screen center (the roof hides other sides and the footprint). Buildings nearer the center
 * lean over those farther out, so draw from farthest to nearest relative to the screen center.
 */
      const drawBuildingSolids = () => {
        const [cx, cz] = screenToWorld(canvasW / 2, canvasH / 2);
        const eyeHeight = Math.hypot(canvasW, canvasH) / 2 / view.zoom / Math.tan((BUILDING_3D_FOV_DEG / 2) * Math.PI / 180);
        const b = getViewBounds();
        const visible: { solid: BuildingSolid; dx: number; dz: number; distance: number }[] = [];
        for (const solid of vec.buildingSolids) {
          const lean = solid.height / eyeHeight;
          const dx = (solid.cx - cx) * lean;
          const dz = (solid.cz - cz) * lean;
          if (solid.maxX + Math.max(0, dx) < b.x1 || solid.minX + Math.min(0, dx) > b.x2
            || solid.maxZ + Math.max(0, dz) < b.z1 || solid.minZ + Math.min(0, dz) > b.z2) continue;
          visible.push({ solid, dx, dz, distance: Math.hypot(solid.cx - cx, solid.cz - cz) });
        }
        visible.sort((p, q) => q.distance - p.distance);

        ctx.strokeStyle = COLOR_BUILDING_BORDER;
        ctx.lineWidth = 1 / view.zoom;
        ctx.lineJoin = "round";
        ctx.setLineDash([]);
        for (const { solid, dx, dz } of visible) {
          const { ring, winding } = solid;
          const walls = new Path2D();
          const roof = new Path2D();
          for (let i = 0; i < ring.length; i++) {
            const [ax, az] = ring[i];
            const [bx, bz] = ring[(i + 1) % ring.length];
            if (i === 0) roof.moveTo(ax + dx, az + dz);
            else roof.lineTo(ax + dx, az + dz);
            // Only edges whose outward normal (to the right for counterclockwise winding) opposes the lean direction are visible.
            if (winding * ((bz - az) * dx - (bx - ax) * dz) >= 0) continue;
            walls.moveTo(ax, az);
            walls.lineTo(bx, bz);
            walls.lineTo(bx + dx, bz + dz);
            walls.lineTo(ax + dx, az + dz);
            walls.closePath();
          }
          roof.closePath();
          ctx.fillStyle = solid.wallColor;
          ctx.fill(walls);
          ctx.fillStyle = solid.color;
          ctx.fill(roof);
          walls.addPath(roof);
          ctx.stroke(walls);
        }
      };

      /* ---- Drawing ---- */
      const draw = () => {
        if (disposed || canvasW === 0 || canvasH === 0) return;
        const L = layersRef.current;
        const dpr = window.devicePixelRatio || 1;

        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.fillStyle = BG_COLOR;
        ctx.fillRect(0, 0, canvas.width, canvas.height);

        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.save();
        // Use a negative Y scale to draw world +Z (north) upward, matching Unity's overhead view and compass.
        // world(wx,wz) → screen: sx=(wx-offsetX)*zoom, sy=(offsetY-wz)*zoom
        ctx.translate(-view.offsetX * view.zoom, view.offsetY * view.zoom);
        ctx.scale(view.zoom, -view.zoom);

        const z = view.zoom;
        const px = (w: number) => w / z; // Convert stroke widths from screen pixels to world units.
        // Draw detailed shapes only at readable scales. Facility symbols/names use a separate zoom threshold.
        const showMinorRoads = L.allRoads && (z > 0.3 || heat.mode === "sources");
        const showBuildingShapes = L.buildings && (z > 0.7 || heat.mode === "sources");
        const showParcels = L.parcels && z > 1;

        // Land outside the city. Fill outside a city rectangle shrunk by 1 px so boundary pixels extend beneath city land,
        // preventing a blue seam between them.
        if (backgroundLandPath && settings) {
          const halfW = settings.map_width / 2 - px(1);
          const halfH = settings.map_height / 2 - px(1);
          const bounds = getViewBounds();
          const outside = new Path2D();
          outside.rect(bounds.x1, bounds.z1, bounds.x2 - bounds.x1, bounds.z2 - bounds.z1);
          outside.rect(-halfW, -halfH, halfW * 2, halfH * 2);
          ctx.save();
          ctx.clip(outside, "evenodd");
          ctx.fillStyle = COLOR_LAND;
          ctx.fill(backgroundLandPath, "evenodd");
          ctx.restore();
        }

        // Elevation colors (map the full-resolution colored near-field image onto the world rectangle).
        // When elevation is off and a coastline is available, skip this because the coastline fill below covers all land.
        if (terrain && settings && (L.elevation || !coastPath)) {
          ctx.save();
          if (coastPath) ctx.clip(coastPath, "evenodd");
          ctx.imageSmoothingEnabled = true;
          ctx.drawImage(
            L.elevation ? terrain.canvas : terrain.baseCanvas,
            -settings.map_width / 2,
            -settings.map_height / 2,
            settings.map_width,
            settings.map_height
          );
          ctx.restore();
          if (L.elevation && reclaimedPath) {
            ctx.fillStyle = terrain.lowestLandColor;
            ctx.fill(reclaimedPath);
          }
        }

        // Fill the land outline together with river/lake holes to preserve water regardless of elevation display.
        if (coastPath && !L.elevation) {
          ctx.fillStyle = COLOR_LAND;
          ctx.fill(coastPath, "evenodd");
        }
        if (showBuildingShapes && vec.parkPath) {
          ctx.fillStyle = COLOR_PARK;
          ctx.fill(vec.parkPath);
        }

        // Contours. Switch colors between the terrain image and pale green background when the elevation layer is off.
        if (L.contours && contours.length > 0) {
          ctx.lineJoin = "round";
          ctx.lineCap = "round";
          const onTerrain = L.elevation && terrain !== null;
          for (const c of contours) {
            ctx.strokeStyle = c.major
              ? (onTerrain ? COLOR_CONTOUR_MAJOR_ON_TERRAIN : COLOR_CONTOUR_MAJOR_ON_BG)
              : (onTerrain ? COLOR_CONTOUR_ON_TERRAIN : COLOR_CONTOUR_ON_BG);
            ctx.lineWidth = px(c.major ? 1.5 : 1);
            ctx.stroke(c.path);
          }
        }

        // Grid (skip when too dense after zooming out).
        if (L.grid) {
          if (gridMinor && GRID_MINOR_INTERVAL * z >= 6) {
            ctx.strokeStyle = COLOR_GRID;
            ctx.lineWidth = px(1);
            ctx.stroke(gridMinor);
          }
          if (gridCell) {
            ctx.strokeStyle = COLOR_GRID_CELL;
            ctx.lineWidth = px(1.5);
            ctx.stroke(gridCell);
          }
        }

        // District value fills. Draw before district outlines to show good/bad districts as areas
        // without obscuring outlines, roads, or buildings.
        if (vec.zoneFillBands) {
          ctx.globalAlpha = ZONE_FILL_ALPHA;
          for (const band of vec.zoneFillBands) {
            ctx.fillStyle = band.color;
            ctx.fill(band.path);
          }
          ctx.globalAlpha = 1.0;
        }

        // Coverage fills. Draw before roads, buildings, and outlines to keep them visible.
        if (infra.coverage) drawCoverageFill(ctx, infra.coverage);

        // Coastline
        if (L.coastline && coastPath) {
          ctx.strokeStyle = COLOR_COASTLINE;
          ctx.lineWidth = px(0.8);
          ctx.setLineDash([]);
          ctx.stroke(coastPath);
        }

        // Draw all district outlines as solid lines.
        if (L.zoneBorders) {
          ctx.strokeStyle = COLOR_ZONE;
          ctx.lineWidth = px(2);
          if (vec.zonePath) {
            ctx.setLineDash([]);
            ctx.stroke(vec.zonePath);
          }

        }

        // Parcels (outlines only).
        if (showParcels && vec.parcelPath) {
          ctx.strokeStyle = COLOR_PARCEL;
          ctx.lineWidth = px(1);
          ctx.stroke(vec.parcelPath);
        }

        // Stations (convex hulls of platforms belonging to the same station building). Translucent red fill and outlines
        // show the extent of one station. Platforms (tracks) are drawn above this.
        if (L.stations && vec.stationHullPath) {
          ctx.globalAlpha = 0.22;
          ctx.fillStyle = COLOR_STATION;
          ctx.fill(vec.stationHullPath);
          ctx.globalAlpha = 1.0;
          ctx.strokeStyle = COLOR_STATION;
          ctx.lineWidth = px(1.5);
          ctx.setLineDash([]);
          ctx.stroke(vec.stationHullPath);
        }

        // Building footprints (surfaces grouped by fill color, plus thin outlines).
        if (showBuildingShapes && vec.buildingPath) {
          for (const band of vec.buildingBands) {
            ctx.fillStyle = band.color;
            ctx.fill(band.path);
          }
          ctx.strokeStyle = COLOR_BUILDING_BORDER;
          ctx.lineWidth = px(1);
          ctx.stroke(vec.buildingPath);
        }

        ctx.lineJoin = "round";
        ctx.lineCap = "round";

        // Add centerlines only in distant views where surfaces are less than 1 px wide. Do not extend beyond endpoints.
        ctx.lineCap = "butt";
        const focus = roadFocusRef.current;
        const dimmed = focus.focusDimOthers && (vec.focusPath !== null || vec.previewPath !== null);
        if (dimmed) ctx.globalAlpha = FOCUS_DIM_ALPHA;
        for (const band of vec.roadBands) {
          if (!(band.trunk ? L.trunkRoads : showMinorRoads)) continue;
          const color = band.color ?? (band.kind === "motorway" ? COLOR_MOTORWAY
            : band.trunk ? COLOR_STREET_TRUNK : COLOR_STREET);
          ctx.fillStyle = color;
          ctx.fill(band.surface);
          if (band.width * z < 1) {
            ctx.strokeStyle = color;
            ctx.lineWidth = px(1);
            ctx.stroke(band.path);
          }
        }
        if (L.railways && vec.railPath) {
          ctx.strokeStyle = COLOR_RAIL;
          ctx.lineWidth = px(1.6);
          ctx.setLineDash([]);
          ctx.stroke(vec.railPath);
          for (const band of vec.railSourceBands) {
            ctx.strokeStyle = band.color;
            ctx.stroke(band.path);
          }
        }
        ctx.globalAlpha = 1.0;

        // Simple 3D buildings overlap roads/tracks, so draw after them and before reading overlays (lines, highlights, labels).
        if (showBuildingShapes && L.buildings3d) drawBuildingSolids();

        // Public transport lines (bands along actual roads/tracks and stops), above tracks/roads and below overlays.
        const variant = transitVariantRef.current;
        const transitLines = variant === null ? vec.transitLines : vec.transitLines.filter((line) => line.path_variant === variant);
        if (L.transitLines && transitLines.length > 0) {
          drawTransitLines(ctx, { ...view }, transitLines, transitFocusRef.current);
        }

        // Per-facility coverage outlines, overlaid above roads/buildings to keep boundaries readable.
        if (infra.coverage) drawCoverageOutlines(ctx, view, infra.coverage);

        // Road highlights. Overlay road-colored bands on white casings; show candidates as dashed lines of the same color.
        const focusColorValue = focus.focusColor || ROAD_COLOR_PALETTE[0];
        ctx.lineJoin = "round";
        if (vec.focusPath) {
          ctx.lineCap = "round";
          ctx.setLineDash([]);
          ctx.strokeStyle = "rgba(255,255,255,0.9)";
          ctx.lineWidth = px(FOCUS_BAND_EDGE_PX);
          ctx.stroke(vec.focusPath);
          ctx.strokeStyle = focusColorValue;
          ctx.lineWidth = px(FOCUS_BAND_PX);
          ctx.stroke(vec.focusPath);
        }
        if (vec.previewPath) {
          ctx.lineCap = "butt";
          ctx.strokeStyle = focusColorValue;
          ctx.lineWidth = px(PREVIEW_BAND_PX);
          ctx.setLineDash([px(10), px(7)]);
          ctx.stroke(vec.previewPath);
          ctx.setLineDash([]);
        }

        // City bounds. Always use a dotted outline to show the usable city area even where it joins background land.
        // Draw zero-length segments with round caps to create dots whose diameter equals the stroke width.
        if (settings) {
          ctx.save();
          ctx.strokeStyle = COLOR_CITY_BOUNDARY;
          ctx.lineWidth = px(CITY_BOUNDARY_DOT_PX);
          ctx.lineCap = "round";
          ctx.setLineDash([0, px(CITY_BOUNDARY_DOT_PX + CITY_BOUNDARY_GAP_PX)]);
          ctx.strokeRect(-settings.map_width / 2, -settings.map_height / 2, settings.map_width, settings.map_height);
          ctx.restore();
        }

        // Row 0 is the southern edge, so place the image using the same world transform as the terrain.
        if (heat.grid && heat.mode === "overlay" && heat.grid.levels.length > 0) {
          const lod = pickHeatmapLod(heat.grid.levels, z);
          void loadHeatmapLevel(lod);
          const cached = heat.images.has(lod) ? lod : [...heat.images.keys()].sort((a, b) => Math.abs(a - lod) - Math.abs(b - lod))[0];
          const image = heat.images.get(cached);
          if (image) {
            ctx.save();
            if (settings) {
              ctx.beginPath();
              ctx.rect(-settings.map_width / 2, -settings.map_height / 2, settings.map_width, settings.map_height);
              ctx.clip();
            }
            ctx.imageSmoothingEnabled = true;
            ctx.drawImage(image, heat.grid.origin_x, heat.grid.origin_z, heat.grid.width_m, heat.grid.height_m);
            ctx.restore();
          }
        }

        if (L.labels) {
          const places = vec.places.filter((place) => place.station ? L.stations : L.buildings);
          if (L.interchanges) {
            for (const ic of vec.interchanges) {
              places.push({ id: ic.interchange_id, x: ic.center[0], z: ic.center[1],
                name: interchangeDisplayName(ic, tRef.current), symbol: "interchange", interchange: true });
            }
          }
          // Pass unnamed roads as empty strings without adding "Road #id", letting drawMapLabels omit them (number-only labels
          // would merely take space from district/facility names; Unity's 3D labels and location search also omit unnamed roads).
          const roadLabels: MapRoadLabel[] = [];
          if (L.trunkRoads) {
            for (const road of vec.roads) {
              for (const point of road.label_points) {
                roadLabels.push({ roadId: road.road_id, name: road.name, color: road.color, x: point.x, z: point.z, angleDeg: point.angle_deg });
              }
            }
          }
          drawMapLabels(ctx, view, canvasW, canvasH, vec.zones, places, roadLabels);
        }

        // Parent component overlays (roads under construction, line maps, etc.).
        overlayDrawRef.current?.(ctx, { ...view });

        ctx.restore();

        // After view movement, schedule contour rebuilding or vector refetching if needed.
        scheduleContourZoomRefresh();
        scheduleVectorLoad();
      };

      /**
 * Draw one frame of the animation overlay.
 * Clear only the second canvas and call animatedOverlayDraw without redrawing the base map. Use the same view transform
 * as draw() and read view each frame to stay synchronized with the base map during pan/zoom. Without a callback,
 * only clear the canvas (also removes residual images when animation stops).
 */
      const drawAnimatedFrame = (timeMs: number) => {
        if (disposed || !animCanvas || !animCtx || canvasW === 0 || canvasH === 0) return;
        animCtx.setTransform(1, 0, 0, 1, 0, 0);
        animCtx.clearRect(0, 0, animCanvas.width, animCanvas.height);

        const cb = animatedOverlayDrawRef.current;
        if (!cb && !infra.network) return;

        const dpr = window.devicePixelRatio || 1;
        animCtx.setTransform(dpr, 0, 0, dpr, 0, 0);
        animCtx.save();
        animCtx.translate(-view.offsetX * view.zoom, view.offsetY * view.zoom);
        animCtx.scale(view.zoom, -view.zoom);
        // When reduced motion is requested, freeze network stripes at time 0.
        if (infra.network) drawNetwork(animCtx, view, infra.network, reducedMotion ? 0 : timeMs);
        cb?.(animCtx, { ...view }, timeMs);
        animCtx.restore();
      };

      /* ---- Terrain (elevation colors, contours, grid) ---- */

      /**
 * Fetch the entire near-field elevation map (float16 LE raw) at once and build colored imagery and contours at
 * full resolution, as in the terrain editor. No LOD switches between near and distant views; all later drawing
 * scales this single offscreen canvas.
 */
      const loadTerrain = async () => {
        try {
          settings = await fetchApi<TerrainSettings>(
            buildApiUrl(apiBaseUrl, "/api/v1/terrain/settings"),
            { signal: abort.signal }
          );

          // Build grid Path2D (100 m intervals plus cell boundaries).
          gridMinor = buildGridPath(settings.map_width, settings.map_height, GRID_MINOR_INTERVAL);
          const cellSize = settings.cell_size && settings.cell_size > 0 ? settings.cell_size : 500;
          gridCell = buildGridPath(settings.map_width, settings.map_height, cellSize);

          const spacing = settings.near_map_spacing;
          if (!spacing || spacing <= 0) {
            console.warn("[CityMapCanvas] near_map_spacing is not available");
            return;
          }

          // Use plain fetch rather than fetchApi for the binary response.
          const res = await fetch(buildApiUrl(apiBaseUrl, "/api/v1/terrain/near"), {
            signal: abort.signal,
          });
          if (!res.ok) throw new Error(`near map: HTTP ${res.status}`);
          const buf = new Uint8Array(await res.arrayBuffer());

          const rows = Math.floor(settings.map_height / spacing) + 1;
          const cols = Math.floor(settings.map_width / spacing) + 1;
          if (buf.byteLength < rows * cols * 2) {
            console.warn("[CityMapCanvas] near map data too short");
            return;
          }
          const elev = decodeFloat16Array(buf, rows * cols);

          // Use min/max for land only (elevation > 0) as the reference range for colors/contours, as in the terrain editor.
          let minLand = Infinity;
          let maxLand = -Infinity;
          for (let i = 0; i < elev.length; i++) {
            const e = elev[i];
            if (e > 0) {
              if (e < minLand) minLand = e;
              if (e > maxLand) maxLand = e;
            }
          }
          if (!isFinite(minLand) || !isFinite(maxLand)) {
            minLand = 0;
            maxLand = 1;
          }
          if (maxLand - minLand < 1) maxLand = minLand + 1;

          terrain = {
            canvas: buildTerrainCanvas(elev, rows, cols, minLand, maxLand, spacing, true),
            baseCanvas: buildTerrainCanvas(elev, rows, cols, minLand, maxLand, spacing, false),
            elev,
            rows,
            cols,
            spacing,
            minLand,
            maxLand,
            lowestLandColor: rampColor(ELEVATION_RAMP, 0),
          };

          // Build contours at the base interval. Set the reference zoom after the initial view is established;
          // scheduleContourZoomRefresh subsequently refines the interval according to zoom.
          contourBaseInterval = pickContourInterval(maxLand - minLand);
          contourInterval = contourBaseInterval;
          contours = buildContours(
            elev, rows, cols, spacing,
            settings.map_width, settings.map_height, minLand, maxLand, contourBaseInterval
          );
        } catch (err) {
          if (!abort.signal.aborted) console.warn("[CityMapCanvas] failed to load terrain:", err);
        }
      };

      /**
 * Rebuild contours at the interval appropriate to the current zoom.
 * Marching Squares is costly in proportion to cell count, so call only through scheduleContourZoomRefresh
 * after zooming settles.
 */
      const rebuildContoursAtCurrentZoom = () => {
        if (!settings || !terrain) return;
        contourInterval = pickContourIntervalForZoom(contourBaseInterval, contourBaseZoom, view.zoom);
        contours = buildContours(
          terrain.elev, terrain.rows, terrain.cols, terrain.spacing,
          settings.map_width, settings.map_height, terrain.minLand, terrain.maxLand, contourInterval
        );
        draw();
      };

      /**
 * Check whether zoom requires finer contours and debounce rebuilding when needed.
 * Schedule only if the candidate interval actually changes, so panning or small zoom changes that retain
 * the interval do not even create a timer.
 */
      const scheduleContourZoomRefresh = () => {
        if (!layersRef.current.contours || !terrain) return;
        const desired = pickContourIntervalForZoom(contourBaseInterval, contourBaseZoom, view.zoom);
        if (desired === contourInterval) return;
        if (contourTimer !== null) window.clearTimeout(contourTimer);
        contourTimer = window.setTimeout(() => {
          contourTimer = null;
          if (!disposed) rebuildContoursAtCurrentZoom();
        }, CONTOUR_DEBOUNCE_MS);
      };

      /* ---- Coastline ---- */

      /** Fetch land Polygon/MultiPolygon data from the GeoJSON-like API and convert to Path2D. */
      const fetchLandPath = async (path: string): Promise<Path2D | null> => {
        const geo = await fetchApi<{ type?: string; coordinates?: unknown }>(
          buildApiUrl(apiBaseUrl, path),
          { signal: abort.signal }
        );
        return geo.coordinates && (geo.type === "Polygon" || geo.type === "MultiPolygon")
          ? buildCoastlinePath(geo) : null;
      };

      const loadCoastline = async () => {
        try {
          coastPath = await fetchLandPath("/api/v1/terrain/coastline");
        } catch (err) {
          if (!abort.signal.aborted) console.warn("[CityMapCanvas] failed to load coastline:", err);
        }
      };

      /** Land outside the city. Refetch on each reload because changing sessions can change the map. */
      const loadBackgroundLand = async () => {
        try {
          backgroundLandPath = await fetchLandPath("/api/v1/terrain/background/coastline");
        } catch (err) {
          if (!abort.signal.aborted) console.warn("[CityMapCanvas] failed to load background coastline:", err);
        }
      };

      /* ---- Reclaimed land (only for fills with the elevation layer on) ---- */
      const loadReclaimedLands = async () => {
        try {
          const res = await fetchApi<{ reclaimed_lands?: { polygon?: [number, number][] }[] }>(
            buildApiUrl(apiBaseUrl, "/api/v1/terrain/reclaimed-lands"),
            { signal: abort.signal }
          );
          const lands = res.reclaimed_lands ?? [];
          reclaimedPath = lands.length > 0
            ? buildCoastlinePath({
                type: "MultiPolygon",
                coordinates: lands.map((land) => [land.polygon ?? []]),
              })
            : null;
        } catch (err) {
          if (!abort.signal.aborted) console.warn("[CityMapCanvas] failed to load reclaimed lands:", err);
        }
      };

      /* ---- Environmental overlays and sources ---- */

      const notifyHeatmapStats = () => {
        heatmapStatsCbRef.current?.(heat.loadedKind === "none" ? null : {
          kind: heat.loadedKind, mode: heat.mode, min: heat.min, max: heat.max, count: heat.count,
        });
      };

      /** Color only the selected LOD and scale the image during pan/zoom. */
      const loadHeatmapLevel = async (lod: number) => {
        if (!heat.grid || heat.loadedKind === "none" || heat.images.has(lod) || heat.loadingLevels.has(lod)) return;
        const generation = heat.generation;
        const kind = heat.loadedKind;
        const level = heat.grid.levels[lod];
        heat.loadingLevels.add(lod);
        try {
          const response = await fetch(buildApiUrl(apiBaseUrl, `/api/v1/city/heatmaps/${kind}/${lod}`), { signal: abort.signal });
          if (!response.ok) throw new Error(`heatmap: HTTP ${response.status}`);
          const buffer = await response.arrayBuffer();
          if (disposed || heat.generation !== generation) return;
          if (buffer.byteLength !== level.rows * level.cols * 2) throw new Error("heatmap grid size mismatch");
          const image = document.createElement("canvas");
          image.width = level.cols;
          image.height = level.rows;
          const imageContext = image.getContext("2d")!;
          const pixels = imageContext.createImageData(level.cols, level.rows);
          pixels.data.set(heatmapPixels(new Uint16Array(buffer), kind));
          imageContext.putImageData(pixels, 0, 0);
          heat.images.set(lod, image);
        } catch (err) {
          if (!abort.signal.aborted) console.warn("[CityMapCanvas] failed to load heatmap LOD:", err);
        } finally {
          if (heat.generation === generation) heat.loadingLevels.delete(lod);
        }
        if (!disposed && heat.generation === generation && heat.images.has(lod)) draw();
      };

      /**
 * Build a fill Path2D per district from fetched district polygons and zoneFill values.
 * The backend normalizes district polygons to a single outer ring, so holes need not be considered.
 */
      const rebuildZoneFill = () => {
        const fill = zoneFillRef.current;
        if (!fill || fill.values.size === 0) {
          vec.zoneFillBands = null;
          return;
        }
        const ramp = fill.ramp ?? ZONE_FILL_DEFAULT_RAMP;
        const bands: { path: Path2D; color: string }[] = [];
        for (const zone of vec.zones) {
          const poly = zone.polygon;
          if (!poly || poly.length < 3 || zone.zone_id === undefined) continue;
          const value = fill.values.get(zone.zone_id);
          if (value === undefined) continue;
          const path = new Path2D();
          path.moveTo(poly[0][0], poly[0][1]);
          for (let i = 1; i < poly.length; i++) path.lineTo(poly[i][0], poly[i][1]);
          path.closePath();
          bands.push({ path, color: rampColor(ramp, value) });
        }
        vec.zoneFillBands = bands.length ? bands : null;
      };

      /** Do not apply stale responses after the type, mode, or reload generation changes. */
      const loadHeatmap = async (kind: CityMapHeatmapKind) => {
        const mode = effectiveHeatmapMode(kind, heatmapModeRef.current);
        const key = `${kind}:${mode}`;
        if (heat.requestKey === key) return;
        heat.requestKey = key;
        const generation = ++heat.generation;
        heat.loadedKind = "none";
        heat.mode = mode;
        heat.grid = null;
        heat.images.clear();
        heat.loadingLevels.clear();
        heat.pathValues.clear();
        heat.buildingValues.clear();
        rebuildPathClassification();
        notifyHeatmapStats();
        draw();
        if (kind === "none") return;
        try {
          if (mode === "sources") {
            const data = await fetchApi<HeatmapSources>(buildApiUrl(apiBaseUrl, `/api/v1/city/environment-sources/${kind}`), { signal: abort.signal });
            if (disposed || generation !== heat.generation) return;
            data.path_ids.forEach((id, i) => heat.pathValues.set(id, data.path_values[i] / 65535));
            data.building_ids.forEach((id, i) => heat.buildingValues.set(id, data.building_values[i] / 65535));
            heat.min = data.min;
            heat.max = data.max;
            heat.count = data.path_ids.length + data.building_ids.length;
          } else {
            const data = await fetchApi<HeatmapGrid>(buildApiUrl(apiBaseUrl, `/api/v1/city/heatmaps/${kind}`), { signal: abort.signal });
            if (disposed || generation !== heat.generation) return;
            heat.grid = data.levels.length > 0 ? data : null;
            heat.min = data.min ?? 0;
            heat.max = data.max ?? 1;
            heat.count = data.levels.length > 0 ? data.levels[0].rows * data.levels[0].cols : 0;
          }
          heat.loadedKind = kind;
          heat.binColors = buildHeatmapBinColors(kind);
          rebuildPathClassification();
          notifyHeatmapStats();
          draw();
        } catch (err) {
          if (heat.generation === generation) heat.requestKey = "";
          if (!abort.signal.aborted) console.warn("[CityMapCanvas] failed to load heatmap:", err);
        }
      };

      const ensureHeatmap = () => { void loadHeatmap(heatmapRef.current); };

      /**
 * Fetch infrastructure display data. Fetch networks and coverage in parallel and discard stale responses
 * after the goods or reload generation changes.
 */
      const loadInfra = async () => {
        const networkId = infraSelectionRef.current?.network ?? null;
        const coverageId = infraSelectionRef.current?.coverage ?? null;
        const key = networkId === null && coverageId === null ? "" : `${networkId}:${coverageId}`;
        if (infra.requestKey === key) return;
        infra.requestKey = key;
        const generation = ++infra.generation;
        infra.network = null;
        infra.coverage = null;
        rebuildPathClassification();
        draw();
        // Removing the network stops the rAF loop, whose last frame still contains the network, so clear it here.
        drawAnimatedFrame(0);
        if (!key) return;
        try {
          const [stats, coverage] = await Promise.all([
            networkId === null ? null : fetchApi<InfrastructureStats>(
              buildApiUrl(apiBaseUrl, "/api/v1/stats/infrastructure"), { signal: abort.signal }),
            coverageId === null ? null : fetchApi<ServiceCoverageData>(
              buildApiUrl(apiBaseUrl, `/api/v1/buildings/service-coverage/${coverageId}`), { signal: abort.signal }),
          ]);
          if (disposed || generation !== infra.generation) return;
          const product = stats?.products.find((p) => p.product_id === networkId) ?? null;
          const simulationDays = stats?.simulation_days || 1;
          infra.network = product ? buildNetworkRender(product, simulationDays) : null;
          infra.coverage = coverage ? buildCoverageRender(coverage, product) : null;
          rebuildPathClassification();
          draw();
        } catch (err) {
          if (infra.generation === generation) infra.requestKey = "";
          if (!abort.signal.aborted) console.warn("[CityMapCanvas] failed to load infra overlay:", err);
        }
      };

      /* ---- Vector data (paths, parcels, buildings, districts) ---- */

      /** Return data types required by current layer settings. */
      const neededKinds = (): VectorKind[] => {
        const L = layersRef.current;
        const kinds: VectorKind[] = [];
        const focus = roadFocusRef.current;
        if (L.trunkRoads || L.allRoads || L.railways || L.stations
          || (focus.focusPathIds?.length ?? 0) > 0 || (focus.previewPathIds?.length ?? 0) > 0) kinds.push("paths");
        if (L.parcels) kinds.push("parcels");
        if (L.buildings) kinds.push("buildings");
        // Show district name labels independently of boundary visibility.
        kinds.push("zones");
        if (L.transitLines) kinds.push("transit");
        if (L.interchanges) kinds.push("interchanges");
        if (L.trunkRoads) kinds.push("roads");
        return kinds;
      };

      /** Build centerlines for highlighted sections and candidates from fetched paths. */
      const rebuildRoadFocus = () => {
        const { focusPathIds: focusIds, previewPathIds: previewIds } = roadFocusRef.current;
        const build = (ids: number[] | null | undefined): Path2D | null => {
          if (!ids || ids.length === 0) return null;
          const wanted = new Set(ids);
          const path = new Path2D();
          let any = false;
          for (const p of vec.paths) {
            if (!wanted.has(p.path_id)) continue;
            const controls: AnchorControls[] = [];
            const pts = collectPathPoints(p, controls);
            if (pts.length < 2) continue;
            if (p.curved) appendSmoothPolyline(path, pts, controls);
            else appendPolyline(path, pts);
            any = true;
          }
          return any ? path : null;
        };
        vec.focusPath = build(focusIds);
        vec.previewPath = build(previewIds);
      };

      /**
 * Classify paths by rendering category (street/highway/track) and build Path2D objects.
 * Prefer occupied surfaces and interpolate two-path width transitions along saved controls.
 * Also update building park classifications when master data arrives.
 */
      const rebuildPathClassification = () => {
        const md = masterDataRef.current;
        vec.places = [];
        for (const building of vec.buildings) {
          const master = building.variant === undefined ? undefined : md?.agents?.[building.variant];
          const symbol = facilitySymbol(building.variant, building.type ?? master?.type, !!building.public_facility);
          const center = toXZ(building.center_point);
          if (symbol && center) vec.places.push({ id: building.agent_id ?? 0, x: center[0], z: center[1],
            name: building.name || master?.display_name || "", symbol });
        }
        for (const path of vec.paths) {
          const master = md?.paths?.[path.path_variant];
          if (master?.model_type !== "station_house") continue;
          const points = collectPathPoints(path);
          const center = toXZ(path.center_point) ?? (points.length > 0
            ? [points.reduce((n, p) => n + p[0], 0) / points.length, points.reduce((n, p) => n + p[1], 0) / points.length]
            : null);
          if (center) vec.places.push({ id: path.path_id, x: center[0], z: center[1], name: path.name || master.display_name,
            symbol: "station", station: true });
        }
        const isPark = (building: ApiPolygonItem) => building.variant !== undefined
          && md?.agents?.[building.variant]?.model_type === MODEL_PARK;
        vec.buildingPath = buildPolygonPath(vec.buildings.filter((b) => !isPark(b)), true);
        vec.parkPath = buildPolygonPath(vec.buildings.filter(isPark), true);
        const sourceBin = (value: number | undefined) => heat.mode === "sources" && value && value > 0
          ? Math.min(HEATMAP_BIN_COUNT - 1, Math.floor(value * HEATMAP_BIN_COUNT)) : -1;
        const coverage = infra.coverage;
        // Building fill color, shared by 2D footprints and simple 3D roofs. Parks remain green unless coverage/source colors apply.
        const buildingColor = (building: ApiPolygonItem): string | null => {
          const id = building.agent_id ?? 0;
          if (coverage?.covered.has(id)) return COVERAGE_IN_RANGE_COLOR;
          if (coverage?.uncovered.has(id)) return COVERAGE_OUT_OF_RANGE_COLOR;
          const bin = sourceBin(heat.buildingValues.get(id));
          if (bin >= 0) return heat.binColors[bin];
          return isPark(building) ? null : COLOR_BUILDING;
        };
        // Tourist attractions have their own floor heights, which take precedence (as in the construction panel).
        const heightOf = (building: ApiPolygonItem) => (building.floors ?? 0)
          * (md?.landmarks?.[building.landmark ?? 0]?.height_per_floor
            ?? (building.variant === undefined ? undefined : md?.agents?.[building.variant]?.height_per_floor)
            ?? DEFAULT_HEIGHT_PER_FLOOR);
        const colorGroups = new Map<string, ApiPolygonItem[]>();
        const wallColors = new Map<string, string>();
        vec.buildingSolids = [];
        for (const building of vec.buildings) {
          const color = buildingColor(building);
          if (color === null) continue;
          const items = colorGroups.get(color) ?? [];
          items.push(building);
          colorGroups.set(color, items);
          if (isPark(building)) continue;
          let wallColor = wallColors.get(color);
          if (!wallColor) {
            wallColor = wallColorOf(color);
            wallColors.set(color, wallColor);
          }
          const solid = buildBuildingSolid(building, heightOf(building), color, wallColor);
          if (solid) vec.buildingSolids.push(solid);
        }
        vec.buildingBands = [...colorGroups].map(([color, items]) => ({ path: buildPolygonPath(items, true), color }));
        const railBins = new Map<number, Path2D>();
        const bands = new Map<string, RoadBand>();
        const roadTypes = [PATH_STREET, PATH_MOTORWAY, PATH_WALKWAY];
        const widthOf = (p: ApiPathItem) => md?.paths?.[p.path_variant]?.width ?? FALLBACK_ROAD_WIDTH[p.path_type] ?? 8;
        const nodeRoads = new Map<number, ApiPathItem[]>();
        for (const p of vec.paths) {
          if (!roadTypes.includes(p.path_type)) continue;
          for (const id of [p.start_node_id, p.end_node_id]) {
            if (id == null || id <= 0) continue;
            const connected = nodeRoads.get(id) ?? [];
            connected.push(p);
            nodeRoads.set(id, connected);
          }
        }
        const away = (p: ApiPathItem, id: number): [number, number] => {
          const controls: AnchorControls[] = [];
          const pts = collectPathPoints(p, controls);
          const start = p.start_node_id === id;
          const point = start ? pts[0] : pts[pts.length - 1];
          const adjacent = start ? pts[1] : pts[pts.length - 2];
          const control = start ? controls[0]?.cOut : controls[controls.length - 1]?.cIn;
          const target = p.curved && control ? control : adjacent;
          if (!point || !target) return [0, 0];
          const dx = target[0] - point[0], dz = target[1] - point[1];
          const length = Math.hypot(dx, dz);
          return length > 1e-6 ? [dx / length, dz / length] : [0, 0];
        };
        const joint = (p: ApiPathItem, id: number | undefined): { width: number; normal?: [number, number] } => {
          const connected = id == null ? undefined : nodeRoads.get(id);
          if (id == null || !connected || connected.length !== 2 || connected[0] === connected[1]) return { width: widthOf(p) };
          const other = connected.find((road) => road !== p)!;
          const a = away(p, id), b = away(other, id);
          const dx = a[0] - b[0], dz = a[1] - b[1], length = Math.hypot(dx, dz);
          // Handle same-direction branches and degenerate tangents with original occupied surfaces instead of width transitions.
          if (a[0] * b[0] + a[1] * b[1] > -0.5 || length < 1e-6) return { width: widthOf(p) };
          const sign = p.start_node_id === id ? 1 : -1;
          return { width: Math.min(widthOf(p), widthOf(other)), normal: [-dz / length * sign, dx / length * sign] };
        };
        const rail = new Path2D();
        let hasRail = false;

        for (const p of vec.paths) {
          const controls: AnchorControls[] = [];
          const pts = collectPathPoints(p, controls);
          if (pts.length < 2) continue;
          // Apply Bézier interpolation only to curved paths. Interpolating straight/polyline paths (curved=0)
          // would draw bends that do not exist, so always distinguish them by the flag.
          // Use saved controls for curved paths when available (curves adjusted by dragging).
          const append = p.curved
            ? (target: Pick<Path2D, "moveTo" | "lineTo" | "bezierCurveTo">, ap: [number, number][]) => appendSmoothPolyline(target, ap, controls)
            : appendPolyline;
          const bin = sourceBin(heat.pathValues.get(p.path_id));
          if (p.path_type === PATH_TRACK) {
            append(rail, pts);
            if (bin >= 0) {
              const sourcePath = railBins.get(bin) ?? new Path2D();
              append(sourcePath, pts);
              railBins.set(bin, sourcePath);
            }
            hasRail = true;
          } else if (
            p.path_type === PATH_STREET ||
            p.path_type === PATH_MOTORWAY ||
            p.path_type === PATH_WALKWAY
          ) {
            const master = md?.paths?.[p.path_variant];
            // Arterial roads comprise highways forming the city's structure and sections the player named and grouped into roads.
            const trunk = p.path_type === PATH_MOTORWAY || hasRoad(p.road_id);
            const kind: RoadBandKind = p.path_type === PATH_MOTORWAY ? "motorway" : "street";
            const width = master?.width ?? FALLBACK_ROAD_WIDTH[p.path_type] ?? 8;
            const key = `${kind}:${trunk ? 1 : 0}:${width}:${bin}`;
            let band = bands.get(key);
            if (!band) {
              band = { kind, trunk, width, path: new Path2D(), surface: new Path2D(), color: bin >= 0 ? heat.binColors[bin] : undefined };
              bands.set(key, band);
            }
            append(band.path, pts);
            const start = joint(p, p.start_node_id);
            const end = joint(p, p.end_node_id);
            if (p.polygon && p.polygon.length >= 3 && !start.normal && !end.normal) {
              band.surface.addPath(buildPolygonPath([p], false));
            } else {
              const centerline = new RoadCenterline();
              append(centerline, pts);
              const polygon = roadSurface(centerline.points, width, start.width, end.width, start.normal, end.normal);
              band.surface.addPath(buildPolygonPath([{ polygon }], false));
            }
          }
        }
        // Draw order: nonarterial streets → arterial streets → highways (later items appear on top).
        const rank = (b: RoadBand) => (b.kind === "motorway" ? 2 : b.trunk ? 1 : 0);
        vec.roadBands = [...bands.values()].sort((a, b) => rank(a) - rank(b));
        vec.railPath = hasRail ? rail : null;
        vec.railSourceBands = [...railBins].map(([bin, path]) => ({ path, color: heat.binColors[bin] }));
        // Stations (convex hulls of platforms grouped by station building). The platform_width check requires
        // master data, so rebuild at the same time as road classification.
        vec.stationHullPath = buildStationHullPath(vec.paths, md);
        rebuildRoadFocus();
      };

      const loadVectors = async (kinds: VectorKind[], bounds: CityMapBounds) => {
        if (kinds.length === 0) return;
        // Retain requests received during fetching and refetch using the currently required types after completion.
        // Dropping them would lose requests immediately after enabling layers/zoneFill, leaving them permanently undrawn.
        if (vec.loading) {
          vectorLoadPending = true;
          return;
        }
        vec.loading = true;
        const bbox = `${bounds.x1},${bounds.z1},${bounds.x2},${bounds.z2}`;

        try {
          await Promise.all(
            kinds.map(async (kind) => {
              if (kind === "transit") {
                // Fetch line geometry citywide once; do not refetch on viewport movement.
                // (reload and "transit:lines_changed" remove it from fetchedKinds to force refetching).
                if (vec.fetchedKinds.has("transit")) return;
                const data = await fetchApi<{ lines: TransitLineGeometry[] }>(
                  buildApiUrl(apiBaseUrl, "/api/v1/transit/lines/geometry"),
                  { signal: abort.signal }
                );
                vec.transitLines = data.lines ?? [];
                return;
              }
              if (kind === "interchanges") {
                // Like districts, there are few entries, so refetch all each time. Detection changes with epochs
                // and road editing. Catch failures here and use empty data so other data types can still render.
                try {
                  const data = await fetchApi<InterchangeListResponse>(
                    buildApiUrl(apiBaseUrl, "/api/v1/interchanges/"),
                    { signal: abort.signal }
                  );
                  vec.interchanges = (data.interchanges ?? []).filter(
                    (ic) => Array.isArray(ic.center) && ic.center.length >= 2
                  );
                } catch (err) {
                  if (abort.signal.aborted) throw err;
                  console.warn("[CityMapCanvas] failed to load interchanges:", err);
                  vec.interchanges = [];
                }
                return;
              }
              if (kind === "roads") {
                // As with interchanges, refetch all each time and isolate failures from other data types.
                try {
                  const data = await fetchRoadList(apiBaseUrl, abort.signal);
                  vec.roads = (data.roads ?? []).filter((road) => Array.isArray(road.label_points));
                } catch (err) {
                  if (abort.signal.aborted) throw err;
                  console.warn("[CityMapCanvas] failed to load roads:", err);
                  vec.roads = [];
                }
                return;
              }
              if (kind === "zones") {
                // Fetch all districts because there are few (also avoids losing large polygons whose center_point
                // falls outside the bbox used for filtering).
                const data = await fetchApi<{ zones: ApiPolygonItem[] }>(
                  buildApiUrl(apiBaseUrl, "/api/v1/zones"),
                  { signal: abort.signal }
                );
                vec.zones = data.zones ?? [];
                vec.zonePath = buildPolygonPath(vec.zones, false);
                // Value fills use district polygons directly, so update them whenever districts are replaced.
                rebuildZoneFill();
                return;
              }
              const url = buildApiUrl(
                apiBaseUrl,
                `/api/v1/${kind}?limit=${FETCH_LIMIT}&bbox=${encodeURIComponent(bbox)}`
              );
              if (kind === "paths") {
                const data = await fetchApi<{ paths: ApiPathItem[] }>(url, { signal: abort.signal });
                vec.paths = data.paths ?? [];
                rebuildPathClassification();
              } else if (kind === "parcels") {
                const data = await fetchApi<{ parcels: ApiPolygonItem[] }>(url, { signal: abort.signal });
                vec.parcels = data.parcels ?? [];
                vec.parcelPath = buildPolygonPath(vec.parcels, false);

              } else {
                const data = await fetchApi<{ buildings: ApiPolygonItem[] }>(url, { signal: abort.signal });
                vec.buildings = data.buildings ?? [];
                // Building polygons are local to center_point.
                rebuildPathClassification();
              }
            })
          );
          for (const k of kinds) vec.fetchedKinds.add(k);
          vec.fetchedBounds = bounds;
          if (!disposed) {
            draw();
            vectorsCbRef.current?.({ paths: vec.paths, zones: vec.zones, parcels: vec.parcels, buildings: vec.buildings });
          }
        } catch (err) {
          if (!abort.signal.aborted) console.warn("[CityMapCanvas] failed to load vector data:", err);
        } finally {
          vec.loading = false;
          // Process pending requests through coverage checks (do nothing if data is already sufficient).
          if (vectorLoadPending && !disposed) {
            vectorLoadPending = false;
            scheduleVectorLoad();
          }
          if (pendingRefreshKinds.size > 0 && !disposed) {
            const pending = [...pendingRefreshKinds];
            pendingRefreshKinds.clear();
            refreshKinds(pending);
          }
        }
      };

      /**
 * Refetch only specified types within the fetched bounds when just some types become stale (e.g. road change notifications).
 * Keep the fetched markers and overwrite data. Clearing them would make scheduleVectorLoad detect missing types and
 * refetch everything. During an active fetch, record only the types and refetch the same bounds after it finishes.
 */
      const pendingRefreshKinds = new Set<VectorKind>();
      const refreshKinds = (requested: VectorKind[]) => {
        // Do nothing before the initial fetch completes (it will supply all types).
        if (!vec.fetchedBounds) return;
        const needed = neededKinds();
        const kinds = requested.filter((k) => needed.includes(k));
        if (kinds.length === 0) return;
        if (vec.loading) {
          for (const k of kinds) pendingRefreshKinds.add(k);
          return;
        }
        void loadVectors(kinds, vec.fetchedBounds);
      };

      /** Fetch all currently required data types with viewport plus padding as the bbox. */
      const fetchVectorsForViewport = () => {
        // Do not fetch before the initial view is established (the startup sequence fetches after resetView).
        if (!viewReady) return;
        const b = getViewBounds();
        const pw = (b.x2 - b.x1) * FETCH_PAD;
        const ph = (b.z2 - b.z1) * FETCH_PAD;
        const bounds: CityMapBounds = {
          x1: b.x1 - pw,
          z1: b.z1 - ph,
          x2: b.x2 + pw,
          z2: b.z2 + ph,
        };
        void loadVectors(neededKinds(), bounds);
      };

      /**
 * Schedule refetching when the viewport leaves the fetched bounds.
 * Debounce to avoid firing every frame during pan/zoom.
 * Effectively does nothing for noninteractive embedded views because their view does not move.
 */
      const scheduleVectorLoad = () => {
        // Defer coverage checks while fetching. Losing the request could leave the edges of a view moved during
        // an in-flight fetch as empty strips without roads or buildings until the next operation.
        if (vec.loading) {
          vectorLoadPending = true;
          return;
        }
        const kinds = neededKinds();
        if (kinds.length === 0) return;
        const allFetched = kinds.every((k) => vec.fetchedKinds.has(k));
        if (
          allFetched &&
          vec.fetchedBounds &&
          coversWithMargin(vec.fetchedBounds, getViewBounds(), COVER_MARGIN)
        ) {
          return;
        }
        if (vectorTimer !== null) window.clearTimeout(vectorTimer);
        vectorTimer = window.setTimeout(() => {
          vectorTimer = null;
          if (!disposed) fetchVectorsForViewport();
        }, VECTOR_DEBOUNCE_MS);
      };

      /** Fetch missing data types for enabled layers (called by the layer-change effect). */
      const ensureVectors = () => {
        const missing = neededKinds().filter((k) => !vec.fetchedKinds.has(k));
        if (missing.length === 0) return;
        fetchVectorsForViewport();
      };

      /* ---- Resize ---- */
      const resizeCanvas = () => {
        const dpr = window.devicePixelRatio || 1;
        canvasW = wrap.clientWidth;
        canvasH = wrap.clientHeight;
        canvas.width = Math.max(1, Math.round(canvasW * dpr));
        canvas.height = Math.max(1, Math.round(canvasH * dpr));
        canvas.style.width = `${canvasW}px`;
        canvas.style.height = `${canvasH}px`;
        // Keep the animation canvas at the base canvas's size and DPR as well
        // (let the next rAF frame handle redrawing).
        if (animCanvas) {
          animCanvas.width = canvas.width;
          animCanvas.height = canvas.height;
          animCanvas.style.width = canvas.style.width;
          animCanvas.style.height = canvas.style.height;
        }
        draw();
      };
      const resizeObserver = new ResizeObserver(() => resizeCanvas());
      resizeObserver.observe(wrap);

      /*
 * ---- Pointer operations (interactive only) ----
 * Use Pointer Events uniformly for mouse and touch. One pointer pans (or performs the parent's drag operation);
 * two fingers pinch, zooming and moving while keeping the point beneath their midpoint fixed.
 * After a pinch begins, do not return to panning or clicking until all fingers are lifted.
 */
      const pointers = new Map<number, { x: number; y: number }>();
      let dragging = false;
      let dragStartX = 0;
      let dragStartY = 0;
      let dragOffsetX = 0;
      let dragOffsetY = 0;
      // Drag whose parent onPointerDown returned true (e.g. node movement). Do not pan.
      let customDrag = false;
      let moved = false;
      let pinch: { distance: number; zoom: number; world: [number, number] } | null = null;

      /** Convert a pointer event to pointer information for the parent's callbacks. */
      const toPointerEvent = (e: MouseEvent): CityMapPointerEvent => {
        const rect = canvas.getBoundingClientRect();
        const mx = e.clientX - rect.left;
        const my = e.clientY - rect.top;
        const [wx, wz] = screenToWorld(mx, my);
        return {
          world: { x: wx, z: wz },
          screen: { x: mx, y: my },
          view: { ...view },
          button: e.button,
          event: e,
        };
      };

      /** Two-finger midpoint (canvas coordinates) and distance. */
      const pinchState = () => {
        const [a, b] = [...pointers.values()];
        const rect = canvas.getBoundingClientRect();
        return { x: (a.x + b.x) / 2 - rect.left, y: (a.y + b.y) / 2 - rect.top, distance: Math.max(1, Math.hypot(a.x - b.x, a.y - b.y)) };
      };

      const onPointerDownEvent = (e: PointerEvent) => {
        if (e.button !== 0 || customDrag) return;
        pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
        if (pointers.size === 2) {
          const p = pinchState();
          pinch = { distance: p.distance, zoom: view.zoom, world: screenToWorld(p.x, p.y) };
          dragging = false;
          return;
        }
        if (pointers.size > 1) return;
        const down = pointerCbRef.current.onPointerDown;
        if (down && down(toPointerEvent(e))) {
          customDrag = true;
          return;
        }
        dragging = true;
        moved = false;
        dragStartX = e.clientX;
        dragStartY = e.clientY;
        dragOffsetX = view.offsetX;
        dragOffsetY = view.offsetY;
      };

      const onPointerMoveEvent = (e: PointerEvent) => {
        if (pointers.has(e.pointerId)) pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
        if (pinch) {
          if (pointers.size < 2) return;
          const p = pinchState();
          view.zoom = Math.max(ZOOM_MIN, Math.min(ZOOM_MAX, (pinch.zoom * p.distance) / pinch.distance));
          const [wx, wz] = screenToWorld(p.x, p.y);
          view.offsetX += pinch.world[0] - wx;
          view.offsetY += pinch.world[1] - wz;
          zoomCbRef.current?.(view.zoom);
          draw();
          return;
        }
        const rect = canvas.getBoundingClientRect();
        const mx = e.clientX - rect.left;
        const my = e.clientY - rect.top;
        if (mx >= 0 && mx <= canvasW && my >= 0 && my <= canvasH) {
          const [wx, wz] = screenToWorld(mx, my);
          cursorCbRef.current?.({ x: wx, z: wz });
        } else {
          cursorCbRef.current?.(null);
        }
        pointerCbRef.current.onPointerMove?.(toPointerEvent(e));

        if (!dragging) return;
        if (Math.hypot(e.clientX - dragStartX, e.clientY - dragStartY) >= 5) moved = true;
        // Pan to keep the world position under the cursor fixed.
        // Because Y is flipped, add drag movement to offsetY.
        view.offsetX = dragOffsetX - (e.clientX - dragStartX) / view.zoom;
        view.offsetY = dragOffsetY + (e.clientY - dragStartY) / view.zoom;
        draw();
      };

      const onPointerUpEvent = (e: PointerEvent) => {
        if (!pointers.delete(e.pointerId)) return;
        if (pinch) {
          if (pointers.size === 0) pinch = null;
          return;
        }
        const rect = canvas.getBoundingClientRect();
        if (e.type === "pointerup" && dragging && !moved && Math.hypot(e.clientX - dragStartX, e.clientY - dragStartY) < 5
          && e.clientX >= rect.left && e.clientX <= rect.right && e.clientY >= rect.top && e.clientY <= rect.bottom) {
          pointerCbRef.current.onMapClick?.(toPointerEvent(e));
        }
        dragging = false;
        if (customDrag) {
          customDrag = false;
          pointerCbRef.current.onPointerUp?.(toPointerEvent(e));
        }
      };

      const onContextMenu = (e: MouseEvent) => {
        // Right clicks are used for parent operations (e.g. deleting nodes), so always suppress the browser's default menu.
        e.preventDefault();
        pointerCbRef.current.onContextMenu?.(toPointerEvent(e));
      };

      const onPointerLeave = () => {
        cursorCbRef.current?.(null);
      };

      const onWheel = (e: WheelEvent) => {
        e.preventDefault();
        const rect = canvas.getBoundingClientRect();
        const mx = e.clientX - rect.left;
        const my = e.clientY - rect.top;

        const [wxBefore, wzBefore] = screenToWorld(mx, my);
        const factor = e.deltaY < 0 ? 1.15 : 1 / 1.15;
        view.zoom = Math.max(ZOOM_MIN, Math.min(ZOOM_MAX, view.zoom * factor));
        const [wxAfter, wzAfter] = screenToWorld(mx, my);
        view.offsetX += wxBefore - wxAfter;
        view.offsetY += wzBefore - wzAfter;

        zoomCbRef.current?.(view.zoom);
        draw();
      };

      if (interactive) {
        canvas.addEventListener("pointerdown", onPointerDownEvent);
        window.addEventListener("pointermove", onPointerMoveEvent);
        window.addEventListener("pointerup", onPointerUpEvent);
        window.addEventListener("pointercancel", onPointerUpEvent);
        canvas.addEventListener("pointerleave", onPointerLeave);
        canvas.addEventListener("wheel", onWheel, { passive: false });
        canvas.addEventListener("contextmenu", onContextMenu);
      }

      /* ---- Startup sequence ---- */
      const reload = () => {
        vec.fetchedKinds.clear();
        vec.fetchedBounds = null;
        // Clear fetched types to refetch heatmaps and infrastructure as well (their values change during epochs).
        heat.requestKey = "";
        heat.generation++;
        infra.requestKey = "";
        infra.generation++;
        void (async () => {
          await Promise.all([loadTerrain(), loadCoastline(), loadBackgroundLand(), loadReclaimedLands()]);
          if (disposed) return;
          fetchVectorsForViewport();
          void loadHeatmap(heatmapRef.current);
          void loadInfra();
          draw();
        })();
      };

      // Refetch running geometry when lines are created, reconfigured, or abolished (emitted by the public transport window/line detail).
      // Do nothing while the layer is off (ensureVectors fetches next time it is enabled).
      let unlistenTransit: (() => void) | undefined;
      void listen("transit:lines_changed", () => {
        if (disposed || !layersRef.current.transitLines) return;
        vec.fetchedKinds.delete("transit");
        ensureVectors();
      })
        .then((fn) => {
          if (disposed) fn();
          else unlistenTransit = fn;
        })
        .catch(console.error);

      // Creating, renaming, reassigning, or deleting roads changes road name labels. Reassignment also changes path road_id
      // (arterial classification), so refetch only roads and paths (parcels, buildings, and districts are unaffected).
      // Section editing sends this on every click, so coalesce consecutive notifications.
      // Do not refetch closed maps (hidden windows). Reopening remounts and refetches everything, but record missed
      // notifications and apply them on focus in case a hidden window is shown again without remounting.
      let unlistenRoads: (() => void) | undefined;
      let unlistenFocus: (() => void) | undefined;
      let roadsTimer: number | null = null;
      let roadsStale = false;
      const refreshRoads = () => {
        if (roadsTimer !== null) window.clearTimeout(roadsTimer);
        roadsTimer = window.setTimeout(() => {
          roadsTimer = null;
          if (disposed) return;
          void getCurrentWindow()
            .isVisible()
            .catch(() => true)
            .then((visible) => {
              if (disposed) return;
              if (!visible) {
                roadsStale = true;
                return;
              }
              roadsStale = false;
              refreshKinds(["roads", "paths"]);
            });
        }, ROADS_REFRESH_DEBOUNCE_MS);
      };
      void listen(ROADS_CHANGED_EVENT, () => {
        if (!disposed) refreshRoads();
      })
        .then((fn) => {
          if (disposed) fn();
          else unlistenRoads = fn;
        })
        .catch(console.error);
      void getCurrentWindow()
        .onFocusChanged(({ payload: focused }) => {
          if (focused && roadsStale && !disposed) refreshRoads();
        })
        .then((fn) => {
          if (disposed) fn();
          else unlistenFocus = fn;
        })
        .catch(console.error);

      resizeCanvas();
      void (async () => {
        await Promise.all([loadTerrain(), loadCoastline(), loadBackgroundLand(), loadReclaimedLands()]);
        if (disposed) return;
        resetView();
        // The reference zoom for contour refinement is the zoom when the initial view is established
        // (full-map fit or fit to the specified initialBounds).
        contourBaseZoom = view.zoom;
        fetchVectorsForViewport();
        void loadHeatmap(heatmapRef.current);
        void loadInfra();
        draw();
      })();

      engineRef.current = {
        draw,
        drawAnimatedFrame,
        resetView: () => {
          resetView();
          draw();
        },
        reload,
        fitBounds: (bounds: CityMapBounds) => {
          setViewToBounds(bounds);
          draw();
        },
        reloadVectors: () => refreshKinds(neededKinds()),
        ensureVectors,
        ensureHeatmap,
        ensureInfra: () => { void loadInfra(); },
        rebuildPathClassification,
        rebuildZoneFill,
        rebuildRoadFocus,
      };

      return () => {
        disposed = true;
        abort.abort();
        engineRef.current = null;
        resizeObserver.disconnect();
        unlistenTransit?.();
        unlistenRoads?.();
        unlistenFocus?.();
        if (roadsTimer !== null) window.clearTimeout(roadsTimer);
        if (vectorTimer !== null) window.clearTimeout(vectorTimer);
        if (contourTimer !== null) window.clearTimeout(contourTimer);
        if (interactive) {
          canvas.removeEventListener("pointerdown", onPointerDownEvent);
          window.removeEventListener("pointermove", onPointerMoveEvent);
          window.removeEventListener("pointerup", onPointerUpEvent);
          window.removeEventListener("pointercancel", onPointerUpEvent);
          canvas.removeEventListener("pointerleave", onPointerLeave);
          canvas.removeEventListener("wheel", onWheel);
          canvas.removeEventListener("contextmenu", onContextMenu);
        }
      };
      // Compare initialBounds using a value-based key (boundsKey).
      // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [apiBaseUrl, interactive, boundsKey]);

    /*
 * ---- Animation overlay rAF loop ----
 * Run only while animatedOverlayDraw is provided or an infrastructure goods network is displayed.
 * Changes to callback contents are handled by the ref mirror, so depend only on its presence boolean to avoid
 * restarting the loop. If the engine is not initialized, do nothing and wait for the next frame.
 */
    const hasAnimatedOverlay = animatedOverlayDraw != null || infraNetworkId !== null;
    useEffect(() => {
      if (!hasAnimatedOverlay) return;
      let rafId = 0;
      const loop = (now: number) => {
        engineRef.current?.drawAnimatedFrame(now);
        rafId = window.requestAnimationFrame(loop);
      };
      rafId = window.requestAnimationFrame(loop);
      return () => {
        window.cancelAnimationFrame(rafId);
        // Clear the last drawn frame when stopping (if the callback is already removed, this only clears).
        engineRef.current?.drawAnimatedFrame(0);
      };
    }, [hasAnimatedOverlay]);

    useImperativeHandle(ref, () => ({
      resetView: () => engineRef.current?.resetView(),
      reload: () => engineRef.current?.reload(),
      fitBounds: (bounds: CityMapBounds) => engineRef.current?.fitBounds(bounds),
      reloadVectors: () => engineRef.current?.reloadVectors(),
    }));

    return (
      <div
        ref={wrapRef}
        className={className}
        style={{ position: "relative", width: "100%", height: "100%", overflow: "hidden" }}
      >
        {/* Disable browser scrolling/zooming on interactive maps so the map handles touch pan/pinch. */}
        <canvas ref={canvasRef} style={{ position: "absolute", inset: 0, display: "block", touchAction: interactive ? "none" : undefined }} />
        {/*
 * Second canvas dedicated to animation overlays (animatedOverlayDraw).
 * Pass mouse input through to the base canvas beneath.
 */}
        <canvas
          ref={animCanvasRef}
          style={{ position: "absolute", inset: 0, display: "block", pointerEvents: "none" }}
        />
        {(showCompass ?? interactive) && <MapCompass />}
      </div>
    );
  }
);
