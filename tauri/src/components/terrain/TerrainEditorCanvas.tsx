/**
 * Terrain editor canvas body.
 *
 * Edits terrain in a Scenario. Opens as a terrain_editor window from "Edit terrain" in Scenario
 * management and loads the work copy (near.bin, meta.json) prepared by Rust's
 * prepare_scenario_terrain_editor from the Scenario terrain API, rendering at its original
 * near-map resolution. The map stacks three canvases. The bottom WebGL2 canvas
 * (terrainEditorRenderer.ts) renders terrain color, selection highlights, 100 m grid lines,
 * cell boundaries (at cell_size intervals, darker than the regular grid), and contours per pixel.
 * The middle Canvas2D draws committed water surfaces, rivers, and camera markers and receives
 * mouse input. The top Canvas2D draws mouse-following elements such as the brush and shapes
 * being drawn (see renderFrame).
 *
 * Mouse actions have separate roles without a mode switch:
 *   - Left click (release in place): apply a single selection action (merge one brush stamp
 *     or add a polygon vertex). Dragging does not repeatedly paint brush stamps.
 *   - Left drag (move a certain distance from the press position): pan. A movement threshold
 *     distinguishes a click from a drag; panning starts only after it is exceeded.
 *   - Right click: close the polygon being drawn (at least three vertices).
 *
 * Editing is contained in this window. The Scenario changes only when "Save to Scenario" is
 * pressed (Rust save_scenario_terrain → core PUT /api/v1/scenarios/{id}/terrain). Map dimensions
 * cannot change. Scenarios with construction (roads, buildings, Earthworks traces, reclaimed land)
 * lock elevations, water, and rivers (heights_locked), disabling their tabs and automatic editing
 * and omitting those values on save. Unsaved edits are lost on close, so indicate them above
 * the save button. Pressing "Edit terrain" again for the same Scenario while visible only brings
 * this window forward, preserving edits (Rust returns the visible window's work-copy ID).
 * The left tool panel provides two editing workflows:
 *   - Manual editing: select an area with a circular/square brush or arbitrary polygon (single
 *     brush clicks; polygons merge into the selection on right click or "Confirm"). Then choose
 *     raise/lower, approach target elevation, or noise in "Editing method". Terrain changes only
 *     when "Apply" is pressed. Each selection merges by maximum (union), so repeated brush
 *     clicks or polygon confirmations expand it. Selection persists until explicitly cleared with
 *     "Reset selection". Brush/polygon interiors always have weight 1.0. Boundary blending
 *     (linear/smooth/Gaussian falloff) extends outward by the "Boundary feather width", instead
 *     of attenuating the interior; choose it in the selection method controls.
 *   - Automatic editing: apply erosion simulation or sea-floor generation. A selection provides
 *     the starting region (or limits changed cells for sea-floor generation); otherwise use the
 *     entire map. Create the mask with manual editing first. Three types are available:
 *     hydraulic erosion (raindrop particles carve slopes and create valleys/deposits), thermal
 *     erosion (cliffs/steep slopes exceeding the angle of repose collapse and become gentler),
 *     and sea-floor generation (deepens uniform 0 m sea cells according to coastline distance,
 *     creating a sea floor continuous with the land). Algorithms live in terrainErosion.ts /
 *     terrainSeaFloor.ts; this file only handles parameter UI and post-execution redraws
 *     (uploading elevations to the terrain layer and sending them to the 3D preview).
 *
 * Natural resources and Props are neither displayed nor edited here; use resource editing and
 * Prop placement in Scenario creation mode. The core save API applies resources by ID differences
 * and replaces Props in full, so send loaded contents back unchanged to retain them; omitting
 * them would delete the Scenario's resources and Props on save.
 */

import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";
import { NumberStepper } from "../common/NumberStepper";
import { errorCode, saveError } from "../../utils/saves";
import { GROUND_COVER_GRASSLAND, GROUND_COVER_TYPES } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import { copyClimatePreset, type ClimateSettings } from "../../utils/climate";
import {
  type TerrainEditorData,
  type TerrainEditorLocks,
  type WaterSurfacePayload,
  type WaterPolygonGeoJson,
  type RiverPayload,
  type ResourceAreaPayload,
  type TerrainProps,
  type TerrainCameraPayload,
  type GridRect,
  type Terrain3DRequestStatePayload,
  type Terrain3DDetachPayload,
  type Terrain3DFullStatePayload,
  type Terrain3DSelectionDeltaPayload,
  type Terrain3DSelectionResetPayload,
  type Terrain3DElevationDeltaPayload,
  decodeFloat16Array,
  base64ToUint8,
  encodeFloat16Array,
  uint8ToBase64,
  groundCoverRamp,
  landElevationRange,
  normalizeGroundCover,
  normalizeTerrainEditorLocks,
  scenarioTerrainSaveArgs,
  extractElevRect,
  quantizeMaskRect,
  SEA_RGB,
  EVT_TERRAIN_3D_REQUEST_STATE,
  EVT_TERRAIN_3D_DETACH,
  EVT_TERRAIN_3D_FULL_STATE,
  EVT_TERRAIN_3D_SELECTION_DELTA,
  EVT_TERRAIN_3D_SELECTION_RESET,
  EVT_TERRAIN_3D_ELEVATION_DELTA,
} from "./terrainData";
import { TerrainEditorRenderer } from "./terrainEditorRenderer";
import {
  type ThermalErosionParams,
  type HydraulicErosionParams,
  type ErosionRegion,
  applyThermalErosion,
  applyHydraulicErosion,
} from "./terrainErosion";
import { type SeaFloorParams, type SeaFloorSlopeMode, applySeaFloor } from "./terrainSeaFloor";
import {
  type RiverGenParams,
  type GeneratedRiver,
  type RiverMouthKind,
  type RiverControlPoint,
  extractRiverNetwork,
  evolveRivers,
  carveRivers,
  applyRiverDepth,
  buildRiverPolygon,
  buildRiverFromControlPoints,
  fitRiverRouteToValley,
  simplifyCenterlineToControlPoints,
  dischargeFromWidth,
  nearestPointIndex,
} from "./terrainRiver";
import { polygonArea, polygonParts } from "./riverGeometry";
import { resolveLocalizedText, type LocalizedText } from "../../utils/locale";
import "./TerrainEditorCanvas.css";

type TerrainEditorCanvasProps = {
  /** Work-copy ID (editor_id), passed through window-registry entity_id and also used for 3D preview synchronization. */
  mapId: string;
};

/** Pan/zoom view state, kept in a ref to avoid rerenders during frequent drag updates. */
type ViewState = {
  offsetX: number;
  offsetY: number;
  zoom: number;
};

/** Loaded terrain grid information, retaining only fields unchanged by manual elevation edits. */
type TerrainSettings = {
  mapWidth: number;
  mapHeight: number;
  spacing: number;
  rows: number;
  cols: number;
  /** Land elevation range for color contrast, fixed at initial load without recalculation after edits.
   *  Elevations beyond it simply clamp colors to 1.0 without practical harm; this avoids scanning
   *  the entire map to recalculate on every edit. */
  minLand: number;
  maxLand: number;
};

/** One point in world coordinates (X-Z plane). */
type WorldPoint = { x: number; z: number };

/**
 * Internal terrain editor representation of one non-sea water surface (lakes, etc.).
 * points contains exterior polygon vertices in world XZ; waterLevel is water Y elevation
 * (meters, sea level = 0). Converted to GeoJSON-like WaterSurfacePayload on save or 3D preview send.
 */
type EditorWaterSurface = { id: string; waterLevel: number; points: WorldPoint[] };

/**
 * Internal terrain editor representation of a river, extending GeneratedRiver (terrainRiver.ts):
 *   - params: generation parameters, for reference when regenerating.
 *   - carved: whether the riverbed has been carved into terrain. false immediately after
 *     "Calculate courses" (preview state); "Carve" each river in the list to set true.
 *     Only carved rivers are saved or included in the 3D preview.
 *   - depthCoef: depth coefficient used to carve this river, adjustable per river from the generation default.
 *   - polygon: river area (terrain below water elevation after carving; holes = sandbars).
 *     Extracted on carving for the RiverPayload.polygon contract and 2D/3D water display;
 *     null before carving. Restored rivers lack points width/bedY (filled with 0 and waterY),
 *     so recompute their area in a band around the expanded existing polygon
 *     (the polygon band in buildRiverPolygon).
 *   - controlPoints: upstream-to-downstream control points used for node editing. Clicked
 *     vertices for manually drawn rivers; a simplified centerline after meander evolution for
 *     generated rivers. Rebuild points with buildRiverFromControlPoints whenever a node moves.
 *     null for restored rivers without a spline, which cannot be node-edited.
 *   - manualWidths: upstream/downstream widths (meters) for manually drawn rivers. Infer
 *     discharge from these widths at control points and interpolate by arc length between them.
 *     null for generated rivers, which retain discharge from course identification.
 */
type EditorRiver = GeneratedRiver & {
  params: RiverGenParams;
  carved: boolean;
  depthCoef: number;
  polygon: WaterPolygonGeoJson | null;
  controlPoints: RiverControlPoint[] | null;
  manualWidths: { head: number; tail: number } | null;
};

/**
 * Pointer operations in the river editing tab:
 *   - rain: same area selection as terrain editing, specifying the rainfall region for generation.
 *   - draw: manually draw a course (left click = add vertex; right click = confirm river).
 *   - node: edit nodes of the selected uncarved river (drag = move, click course = add,
 *     right-click node = delete).
 */
type RiverPointerMode = "rain" | "draw" | "node";

/** Mutually exclusive editing targets selected by the dropdown at the top of the left tool panel. */
type EditorTab = "terrain" | "water" | "river" | "camera";

/** Editing targets disabled by heights_locked; each modifies elevations, water surfaces, or rivers. */
const HEIGHT_LOCKED_TABS: ReadonlySet<EditorTab> = new Set<EditorTab>(["terrain", "water", "river"]);

/** Screen-space picking tolerance (px) for nodes/courses in node editing. Divide by zoom for world coordinates. */
const RIVER_NODE_HIT_PX = 9;

/** Automatically chooses a readable contour interval from the elevation range, at the reference zoom fitting the entire view. */
function pickContourInterval(range: number): number {
  if (range <= 0) return 100;
  const candidates = [10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000];
  for (const c of candidates) {
    // Choose an interval yielding about 15–25 contours; use finer spacing for smaller ranges.
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
 * Zoom-factor grace range before contour refinement begins. Keep the base interval until
 * this factor beyond the reference zoom (fitting all terrain). Large terrains have a very low
 * fit zoom, so without this grace a slight zoom-in would reveal fine contours while still far
 * away. Retain the base interval until a meaningful zoom-in to avoid overly detailed contours
 * at distant views.
 */
const CONTOUR_REFINE_ZOOM_THRESHOLD = 6;

/**
 * Movement threshold (screen pixels) distinguishing a left click in place (single selection)
 * from a drag (pan). Switch to panning after moving beyond this distance from the press position.
 */
const CLICK_VS_DRAG_THRESHOLD_PX = 4;

/**
 * Wait until state changes stop for this duration (ms), then batch a full-state send to the
 * 3D preview. Full state encodes all elevations, and the preview rebuilds its mesh. This exceeds
 * the 150 ms carving-preview rebuild debounce so the rebuild following Undo and repeated
 * stepper clicks can be batched together.
 */
const PREVIEW_FULL_STATE_QUIET_MS = 200;
/** Even during continuous changes, send after this duration (ms) from the first change so the preview cannot remain stale. */
const PREVIEW_FULL_STATE_MAX_WAIT_MS = 1000;

/**
 * Defaults for newly placed initial camera state. Height matches the game's CameraController
 * initial altitude (baseInitialHeight=150m), and pitch matches the normal gameplay overview.
 */
const CAMERA_DEFAULT_HEIGHT_M = 150;
const CAMERA_DEFAULT_PITCH_DEG = 50;
const CAMERA_DEFAULT_YAW_DEG = 0;

/**
 * Refines contour intervals according to the current zoom.
 *
 * baseInterval is the interval at the reference zoom fitting all terrain. Refine through the
 * 1-2-5 series only for zoom beyond CONTOUR_REFINE_ZOOM_THRESHOLD times that reference.
 * Do not coarsen below the base interval when zooming out, simplifying visual changes to
 * "zooming in gives finer detail".
 */
function pickContourIntervalForZoom(baseInterval: number, baseZoom: number, currentZoom: number): number {
  if (baseZoom <= 0) return baseInterval;
  const refineStartZoom = baseZoom * CONTOUR_REFINE_ZOOM_THRESHOLD;
  if (currentZoom <= refineStartZoom) return baseInterval;
  const factor = currentZoom / refineStartZoom;
  return Math.max(1, niceIntervalAtOrBelow(baseInterval / factor));
}

/* ============================================================================
 * Manual editing tool types and logic.
 *
 * First build selectionMask (weight 0..1 per grid point) through "Selection method"
 * (circle/square brush or arbitrary polygon). "Editing method" (raise/lower, approach target,
 * noise) changes elevations only when "Apply" is pressed. Separating selection from applying
 * edits ensures clicking alone does not change terrain. Brushes and polygons merge into the
 * same mask by maximum (union), so repeated clicks/confirmations can combine and expand selections.
 * ========================================================================== */

/** Selection method. */
type ShapeKind = "circle" | "rect" | "polygon";
/** Editing method. */
type EditOperation = "raiseLower" | "flatten" | "noise";
/** Blending method between selection boundaries and surrounding terrain. */
type FalloffKind = "linear" | "smooth" | "gaussian";
/** Noise generation algorithm. */
type NoiseAlgorithm = "white" | "fractal";
/** Noise bias (depress / both / raise). */
type NoiseBias = "dig" | "both" | "bulge";

/** Circle/square brush shape parameters. size is the radius (or half-side) along local X. */
type BrushShapeParams = { size: number; aspect: number; angleDeg: number };
/** Raise/lower parameters. rate sign chooses direction (meters per "Apply", attenuated by selection weight). */
type RaiseLowerParams = { rate: number };
/** Approach-target parameters. rate is the residual fraction closed per "Apply" (1 immediately matches points with weight 1). */
type FlattenParams = { target: number; rate: number };
/** Noise parameters. */
type NoiseToolParams = {
  algorithm: NoiseAlgorithm;
  scale: number;
  octaves: number;
  amplitude: number;
  bias: NoiseBias;
};

/** Grid bounds, used for partial terrain-layer uploads and selection bounding rectangles. */
type DirtyRect = { rMin: number; rMax: number; cMin: number; cMax: number };

/**
 * Computes weight from normalized position t in the feather band (0 = shape boundary,
 * weight 1; 1 = outer feather edge, weight 0). falloffWeight is a general falloff curve
 * expressing attenuation within this local band, rather than distance from the center.
 */
function falloffWeight(nd: number, kind: FalloffKind): number {
  const d = Math.max(0, Math.min(1, nd));
  if (kind === "linear") return 1 - d;
  if (kind === "gaussian") return Math.exp(-3 * d * d);
  // smooth: apply smoothstep to linear falloff for gentler transitions near the boundary.
  const w = 1 - d;
  return w * w * (3 - 2 * w);
}

/** Deterministic pseudorandom value [-1,1] from integer grid coordinates, independent of time so it remains stable across frames. */
function hashNoise2D(ix: number, iz: number): number {
  let n = ix * 374761393 + iz * 668265263;
  n = (n ^ (n >> 13)) * 1274126177;
  n = n ^ (n >> 16);
  return ((n & 0x7fffffff) / 0x7fffffff) * 2 - 1;
}

/** Value noise smoothly interpolating hashNoise2D at grid points. */
function valueNoise2D(x: number, z: number): number {
  const x0 = Math.floor(x);
  const z0 = Math.floor(z);
  const sx = x - x0;
  const sz = z - z0;
  const fx = sx * sx * (3 - 2 * sx);
  const fz = sz * sz * (3 - 2 * sz);
  const n00 = hashNoise2D(x0, z0);
  const n10 = hashNoise2D(x0 + 1, z0);
  const n01 = hashNoise2D(x0, z0 + 1);
  const n11 = hashNoise2D(x0 + 1, z0 + 1);
  const ix0 = n00 + (n10 - n00) * fx;
  const ix1 = n01 + (n11 - n01) * fx;
  return ix0 + (ix1 - ix0) * fz;
}

/** Fractal noise combining multiple octaves of valueNoise2D. Returns approximately [-1,1]. */
function fractalNoise2D(wx: number, wz: number, scale: number, octaves: number): number {
  let amp = 1;
  let freq = 1 / scale;
  let sum = 0;
  let norm = 0;
  for (let o = 0; o < octaves; o++) {
    sum += valueNoise2D(wx * freq, wz * freq) * amp;
    norm += amp;
    amp *= 0.5;
    freq *= 2;
  }
  return norm > 0 ? sum / norm : 0;
}

/**
 * Computes world-space bounding box radii of the rotated circle/square brush.
 * With feather (boundary blur width), includes the outward feathered area, expanding the
 * selection-mask update and partial terrain-layer upload bounds accordingly.
 */
function shapeAabbHalfExtents(size: number, aspect: number, angleDeg: number, feather: number = 0): { hx: number; hz: number } {
  const rad = (angleDeg * Math.PI) / 180;
  const c = Math.abs(Math.cos(rad));
  const s = Math.abs(Math.sin(rad));
  const a = size + feather;
  const b = (size + feather) * aspect;
  return { hx: a * c + b * s, hz: a * s + b * c };
}

/**
 * Computes selection weight at world coordinates (wx,wz) for a circle/square brush.
 *
 * The interior (nd<=1) always has weight 1.0. Previously attenuating the interior with distance
 * from the center made the inside look blurred, so feathering now applies only outside (nd>1).
 * Outside, weight falls from 1 to 0 according to falloffKind up to the similarly scaled outer
 * shape (size+feather), and is 0 beyond. size and size+feather shapes have the same angle and
 * aspect ratio, so nd alone expresses relative distance from the boundary in every direction
 * because the inner/outer scale ratio is constant.
 */
function computeShapeWeight(
  wx: number,
  wz: number,
  cx: number,
  cz: number,
  size: number,
  aspect: number,
  angleDeg: number,
  shapeKind: "circle" | "rect",
  falloffKind: FalloffKind,
  feather: number
): number {
  // Convert into brush-local coordinates (center origin, inverse rotation), then normalize.
  const rad = (-angleDeg * Math.PI) / 180;
  const dx = wx - cx;
  const dz = wz - cz;
  const lx = dx * Math.cos(rad) - dz * Math.sin(rad);
  const lz = dx * Math.sin(rad) + dz * Math.cos(rad);
  const safeSize = Math.max(1e-6, size);
  const nx = lx / safeSize;
  const nz = lz / (safeSize * aspect);
  const nd = shapeKind === "circle" ? Math.sqrt(nx * nx + nz * nz) : Math.max(Math.abs(nx), Math.abs(nz));
  if (nd <= 1) return 1;
  if (feather <= 0) return 0;
  const outerNd = (size + feather) / size;
  if (nd >= outerNd) return 0;
  const bandT = (nd - 1) / (outerNd - 1);
  return falloffWeight(bandT, falloffKind);
}

/** Shortest distance from point (px,pz) to segment (ax,az)–(bx,bz). */
function distToSegment(px: number, pz: number, ax: number, az: number, bx: number, bz: number): number {
  const dx = bx - ax;
  const dz = bz - az;
  const lenSq = dx * dx + dz * dz;
  let t = lenSq > 0 ? ((px - ax) * dx + (pz - az) * dz) / lenSq : 0;
  t = Math.max(0, Math.min(1, t));
  const ex = ax + dx * t;
  const ez = az + dz * t;
  return Math.hypot(px - ex, pz - ez);
}

/** Point-in-polygon test using ray casting. */
function pointInPolygon(x: number, z: number, poly: WorldPoint[]): boolean {
  let inside = false;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const xi = poly[i].x, zi = poly[i].z;
    const xj = poly[j].x, zj = poly[j].z;
    const intersects = zi > z !== zj > z && x < ((xj - xi) * (z - zi)) / (zj - zi) + xi;
    if (intersects) inside = !inside;
  }
  return inside;
}

/** Shortest distance from a point to polygon boundary edges. */
function distToPolygonBoundary(x: number, z: number, poly: WorldPoint[]): number {
  let minDist = Infinity;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const d = distToSegment(x, z, poly[j].x, poly[j].z, poly[i].x, poly[i].z);
    if (d < minDist) minDist = d;
  }
  return minDist;
}

/**
 * Merges circle/square brush weights into selectionMask by maximum (union).
 * Returns affected grid bounds, or null if none. With feather, scans a bounding rectangle
 * expanded to include the outward feathered area.
 */
function mergeShapeIntoSelection(
  mask: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  cx: number,
  cz: number,
  shapeKind: "circle" | "rect",
  brush: BrushShapeParams,
  falloffKind: FalloffKind,
  feather: number
): DirtyRect | null {
  const halfW = mapWidth / 2;
  const halfH = mapHeight / 2;
  const { hx, hz } = shapeAabbHalfExtents(brush.size, brush.aspect, brush.angleDeg, feather);
  const cMin = Math.max(0, Math.floor((cx - hx + halfW) / spacing));
  const cMax = Math.min(cols - 1, Math.ceil((cx + hx + halfW) / spacing));
  const rMin = Math.max(0, Math.floor((cz - hz + halfH) / spacing));
  const rMax = Math.min(rows - 1, Math.ceil((cz + hz + halfH) / spacing));
  if (cMin > cMax || rMin > rMax) return null;

  let touched = false;
  for (let r = rMin; r <= rMax; r++) {
    const wz = r * spacing - halfH;
    const rowBase = r * cols;
    for (let c = cMin; c <= cMax; c++) {
      const wx = c * spacing - halfW;
      const weight = computeShapeWeight(wx, wz, cx, cz, brush.size, brush.aspect, brush.angleDeg, shapeKind, falloffKind, feather);
      if (weight <= 0) continue;
      const idx = rowBase + c;
      if (weight > mask[idx]) mask[idx] = weight;
      touched = true;
    }
  }
  return touched ? { rMin, rMax, cMin, cMax } : null;
}

/**
 * Merges polygon weights into selectionMask by maximum (union).
 * Interior weight is always 1; feathering applies only outside, extending outward by feather
 * from the boundary with falloffKind attenuation from 1 to 0.
 * Returns affected grid bounds, or null if none.
 */
function mergePolygonIntoSelection(
  mask: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  poly: WorldPoint[],
  feather: number,
  falloffKind: FalloffKind
): DirtyRect | null {
  if (poly.length < 3) return null;
  const halfW = mapWidth / 2;
  const halfH = mapHeight / 2;

  let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;
  for (const p of poly) {
    if (p.x < minX) minX = p.x;
    if (p.x > maxX) maxX = p.x;
    if (p.z < minZ) minZ = p.z;
    if (p.z > maxZ) maxZ = p.z;
  }
  // Expand the bounding rectangle scan by the outward feather width.
  const margin = Math.max(0, feather);
  const cMin = Math.max(0, Math.floor((minX - margin + halfW) / spacing));
  const cMax = Math.min(cols - 1, Math.ceil((maxX + margin + halfW) / spacing));
  const rMin = Math.max(0, Math.floor((minZ - margin + halfH) / spacing));
  const rMax = Math.min(rows - 1, Math.ceil((maxZ + margin + halfH) / spacing));
  if (cMin > cMax || rMin > rMax) return null;

  let touched = false;
  for (let r = rMin; r <= rMax; r++) {
    const wz = r * spacing - halfH;
    const rowBase = r * cols;
    for (let c = cMin; c <= cMax; c++) {
      const wx = c * spacing - halfW;
      const inside = pointInPolygon(wx, wz, poly);
      let weight: number;
      if (inside) {
        weight = 1;
      } else if (margin > 0) {
        const dist = distToPolygonBoundary(wx, wz, poly);
        if (dist >= margin) continue;
        weight = falloffWeight(dist / margin, falloffKind);
      } else {
        continue;
      }
      if (weight <= 0) continue;
      const idx = rowBase + c;
      if (weight > mask[idx]) mask[idx] = weight;
      touched = true;
    }
  }
  return touched ? { rMin, rMax, cMin, cMax } : null;
}

/** Returns bounds containing both grid regions, expanding the selection bounding rectangle on each merge. */
function unionDirtyRect(a: DirtyRect | null, b: DirtyRect): DirtyRect {
  if (!a) return b;
  return {
    rMin: Math.min(a.rMin, b.rMin),
    rMax: Math.max(a.rMax, b.rMax),
    cMin: Math.min(a.cMin, b.cMin),
    cMax: Math.max(a.cMax, b.cMax),
  };
}

/**
 * Changes one cell's elevation according to the editing method. weight is the selectionMask
 * value (0..1) unchanged. Called during one scan of the selection bounds when "Apply" is pressed,
 * so repeated-application safeguards (excluding visited cells, etc.) are unnecessary.
 */
function applyOperationDelta(
  elev: Float32Array,
  idx: number,
  col: number,
  row: number,
  wx: number,
  wz: number,
  weight: number,
  operation: EditOperation,
  raiseLowerP: RaiseLowerParams,
  flattenP: FlattenParams,
  noiseP: NoiseToolParams
): void {
  if (weight <= 0) return;

  if (operation === "raiseLower") {
    elev[idx] += raiseLowerP.rate * weight;
    return;
  }

  if (operation === "flatten") {
    const cur = elev[idx];
    const t = Math.min(1, Math.max(0, flattenP.rate * weight));
    elev[idx] = cur + (flattenP.target - cur) * t;
    return;
  }

  // operation === "noise"
  const raw =
    noiseP.algorithm === "white"
      ? hashNoise2D(col, row)
      : fractalNoise2D(wx, wz, Math.max(1, noiseP.scale), Math.max(1, Math.round(noiseP.octaves)));
  let delta = raw * noiseP.amplitude;
  if (noiseP.bias === "dig") delta = -Math.abs(delta);
  else if (noiseP.bias === "bulge") delta = Math.abs(delta);
  elev[idx] += delta * weight;
}

/** Applies the editing method once over the selectionMask bounds, when "Apply" is pressed. */
function applySelectionOperation(
  elev: Float32Array,
  mask: Float32Array,
  bounds: DirtyRect,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  operation: EditOperation,
  raiseLowerP: RaiseLowerParams,
  flattenP: FlattenParams,
  noiseP: NoiseToolParams
): void {
  const halfW = mapWidth / 2;
  const halfH = mapHeight / 2;
  for (let r = bounds.rMin; r <= bounds.rMax; r++) {
    const wz = r * spacing - halfH;
    const rowBase = r * cols;
    for (let c = bounds.cMin; c <= bounds.cMax; c++) {
      const idx = rowBase + c;
      const weight = mask[idx];
      if (weight <= 0) continue;
      const wx = c * spacing - halfW;
      applyOperationDelta(elev, idx, c, r, wx, wz, weight, operation, raiseLowerP, flattenP, noiseP);
    }
  }
}

/**
 * Clears a Canvas2D layer (vector/cursor) and applies a view transform for drawing directly
 * in world coordinates. The caller must ctx.restore() afterward.
 * World → CSS: screenX = zoom*(worldX - offsetX), screenY = zoom*(offsetY - worldZ).
 * Elevation row 0 is the south edge (world -Z), so negative Y scale places north (+Z) upward,
 * matching Unity's overview, the city map, and the terrain layer. fillWorldText cancels this
 * flip for text, which would otherwise be upside down.
 */
function beginWorldLayer(ctx: CanvasRenderingContext2D, view: ViewState, dpr: number): void {
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.clearRect(0, 0, ctx.canvas.width, ctx.canvas.height);
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.save();
  ctx.translate(-view.offsetX * view.zoom, view.offsetY * view.zoom);
  ctx.scale(view.zoom, -view.zoom);
}

/**
 * Draws text at world coordinates (x, z). draw() flips Y to place north (+Z) upward, so direct
 * fillText would invert text. Move to the drawing position, then cancel only the Y flip;
 * the caller's font size, textAlign, and textBaseline remain effective.
 */
function fillWorldText(ctx: CanvasRenderingContext2D, text: string, x: number, z: number): void {
  ctx.save();
  ctx.translate(x, z);
  ctx.scale(1, -1);
  ctx.fillText(text, 0, 0);
  ctx.restore();
}

/**
 * Draws a circle/square brush outline in world coordinates after draw() applies the view transform.
 * Alongside the solid boundary of the weight-1 interior, draws a dashed outer feather edge
 * when feather>0, making the new outward-blur behavior visible in the preview too.
 */
function strokeBrushOutline(
  ctx: CanvasRenderingContext2D,
  cx: number,
  cz: number,
  shapeKind: "circle" | "rect",
  brush: BrushShapeParams,
  feather: number,
  lineWidth: number
): void {
  ctx.save();
  ctx.translate(cx, cz);
  ctx.rotate((brush.angleDeg * Math.PI) / 180);

  const drawShape = (size: number) => {
    ctx.beginPath();
    if (shapeKind === "circle") {
      ctx.ellipse(0, 0, size, size * brush.aspect, 0, 0, Math.PI * 2);
    } else {
      ctx.rect(-size, -size * brush.aspect, size * 2, size * 2 * brush.aspect);
    }
    ctx.stroke();
  };

  ctx.strokeStyle = "rgba(255,255,255,0.85)";
  ctx.lineWidth = lineWidth;
  ctx.setLineDash([]);
  drawShape(brush.size);

  if (feather > 0) {
    ctx.strokeStyle = "rgba(255,255,255,0.4)";
    ctx.setLineDash([lineWidth * 3, lineWidth * 3]);
    drawShape(brush.size + feather);
    ctx.setLineDash([]);
  }

  ctx.restore();
}

/** Draws the polygon-in-progress preview (lines between committed vertices + a line following the mouse). */
function strokePolygonPreview(
  ctx: CanvasRenderingContext2D,
  points: WorldPoint[],
  mouse: WorldPoint | null,
  lineWidth: number
): void {
  if (points.length === 0) return;
  ctx.strokeStyle = "rgba(255,255,255,0.85)";
  ctx.fillStyle = "rgba(255,255,255,0.85)";
  ctx.lineWidth = lineWidth;
  ctx.beginPath();
  ctx.moveTo(points[0].x, points[0].z);
  for (let i = 1; i < points.length; i++) ctx.lineTo(points[i].x, points[i].z);
  if (mouse) ctx.lineTo(mouse.x, mouse.z);
  if (points.length >= 3) ctx.closePath();
  ctx.stroke();
  const r = lineWidth * 3;
  for (const p of points) {
    ctx.beginPath();
    ctx.arc(p.x, p.z, r, 0, Math.PI * 2);
    ctx.fill();
  }
}

/**
 * Draws non-sea water surfaces (lakes, etc.) on the 2D canvas after draw() applies the view
 * transform. Committed surfaces use translucent blue fill and outlines. In water drawing mode,
 * draws the unfinished polygon in blue with a mouse-following line.
 */
function strokeWaterSurfaces(
  ctx: CanvasRenderingContext2D,
  surfaces: EditorWaterSurface[],
  inProgressPoints: WorldPoint[],
  mouse: WorldPoint | null,
  lineWidth: number
): void {
  // Committed water surfaces.
  for (const ws of surfaces) {
    if (ws.points.length < 3) continue;
    ctx.beginPath();
    ctx.moveTo(ws.points[0].x, ws.points[0].z);
    for (let i = 1; i < ws.points.length; i++) ctx.lineTo(ws.points[i].x, ws.points[i].z);
    ctx.closePath();
    ctx.fillStyle = "rgba(60,140,220,0.32)";
    ctx.fill();
    ctx.strokeStyle = "rgba(120,200,255,0.95)";
    ctx.lineWidth = lineWidth;
    ctx.stroke();
  }

  // Water polygon being drawn (uncommitted).
  if (inProgressPoints.length > 0) {
    ctx.strokeStyle = "rgba(120,200,255,0.95)";
    ctx.fillStyle = "rgba(120,200,255,0.95)";
    ctx.lineWidth = lineWidth;
    ctx.beginPath();
    ctx.moveTo(inProgressPoints[0].x, inProgressPoints[0].z);
    for (let i = 1; i < inProgressPoints.length; i++) ctx.lineTo(inProgressPoints[i].x, inProgressPoints[i].z);
    if (mouse) ctx.lineTo(mouse.x, mouse.z);
    if (inProgressPoints.length >= 3) ctx.closePath();
    ctx.stroke();
    const r = lineWidth * 3;
    for (const p of inProgressPoints) {
      ctx.beginPath();
      ctx.arc(p.x, p.z, r, 0, Math.PI * 2);
      ctx.fill();
    }
  }
}

/**
 * Draws the initial camera state marker on the 2D canvas after draw() applies the view transform.
 * Draws a position circle, a view arrow along yaw, and a field-of-view sector. yaw is clockwise
 * from north (+Z) = 0, giving direction (sin(yaw), cos(yaw)), as in the 2D city map camera marker.
 */
function strokeCameraMarker(
  ctx: CanvasRenderingContext2D,
  camera: TerrainCameraPayload | null,
  lineWidth: number
): void {
  if (!camera) return;
  const yawRad = (camera.yaw * Math.PI) / 180;
  const dx = Math.sin(yawRad);
  const dz = Math.cos(yawRad);
  // Shallower downward pitch reaches farther, so arrow length also indicates the pitch.
  const reach = lineWidth * 40 + camera.y / Math.max(Math.tan((camera.pitch * Math.PI) / 180), 0.2);
  const halfFov = Math.PI / 6;

  ctx.save();
  ctx.lineWidth = lineWidth;
  ctx.strokeStyle = "rgba(255,230,90,0.9)";
  ctx.fillStyle = "rgba(255,230,90,0.18)";

  // Field-of-view sector.
  ctx.beginPath();
  ctx.moveTo(camera.x, camera.z);
  ctx.lineTo(camera.x + Math.sin(yawRad - halfFov) * reach, camera.z + Math.cos(yawRad - halfFov) * reach);
  ctx.lineTo(camera.x + dx * reach * 1.1, camera.z + dz * reach * 1.1);
  ctx.lineTo(camera.x + Math.sin(yawRad + halfFov) * reach, camera.z + Math.cos(yawRad + halfFov) * reach);
  ctx.closePath();
  ctx.fill();
  ctx.stroke();

  // View direction arrow.
  ctx.beginPath();
  ctx.moveTo(camera.x, camera.z);
  ctx.lineTo(camera.x + dx * reach, camera.z + dz * reach);
  ctx.stroke();

  // Position.
  ctx.fillStyle = "rgba(255,230,90,0.95)";
  ctx.beginPath();
  ctx.arc(camera.x, camera.z, lineWidth * 4, 0, Math.PI * 2);
  ctx.fill();

  ctx.font = `${lineWidth * 9}px sans-serif`;
  ctx.textAlign = "center";
  ctx.textBaseline = "bottom";
  fillWorldText(ctx, "Camera", camera.x, camera.z + lineWidth * 6);
  ctx.restore();
}

/** Draws a dashed line from camera to mouse as guidance for setting the camera tab's look-at point. */
function strokeCameraAimLine(
  ctx: CanvasRenderingContext2D,
  camera: TerrainCameraPayload | null,
  mouse: WorldPoint | null,
  lineWidth: number
): void {
  if (!camera || !mouse) return;
  ctx.save();
  ctx.lineWidth = lineWidth;
  ctx.setLineDash([lineWidth * 4, lineWidth * 4]);
  ctx.strokeStyle = "rgba(255,230,90,0.6)";
  ctx.beginPath();
  ctx.moveTo(camera.x, camera.z);
  ctx.lineTo(mouse.x, mouse.z);
  ctx.stroke();
  ctx.restore();
}

/** Display options passed to strokeRivers (carving preview, water visibility, selected river). */
type RiverDrawOptions = {
  /** Area polygons extracted on preview terrain for uncarved rivers (river ID → area). Empty when preview is off. */
  previewPolygons: ReadonlyMap<string, WaterPolygonGeoJson | null>;
  /** false omits area-polygon water fills to show riverbed terrain. */
  waterVisible: boolean;
  /** River ID selected in the list (highlighted). null means none. */
  selectedId: string | null;
};

/**
 * Caches river centerline and area-polygon Path2D objects, building each shape only once.
 * Rivers (EditorRiver) and areas are replaced by new arrays/objects on every change because
 * the Undo stack shares their references. The arrays/objects themselves can therefore be keys,
 * rebuilding only when shapes change, avoiding reconstruction of thousands of river points
 * on every mouse-movement redraw.
 */
const riverLinePathCache = new WeakMap<EditorRiver["points"], { path: Path2D; avgWidth: number }>();
const riverFillPathCache = new WeakMap<WaterPolygonGeoJson, Path2D>();

/**
 * Draws rivers on the 2D canvas after draw() applies the view transform.
 * - Carved rivers: area-polygon water fill (holes for sandbars) + thin water profile line,
 *   showing exactly where the game draws water. If area extraction is missing (error case),
 *   draw only the centerline.
 * - Uncarved rivers: bright light-blue dashed lines. During carving preview, fill the area
 *   extracted from preview terrain with a slightly fainter water color to distinguish it.
 * - Label each source with the same number as the list, connecting each line to its river.
 *   Number font scales with zoom to stay roughly constant on screen. Make selected rivers
 *   thicker and brighter.
 */
function strokeRivers(
  ctx: CanvasRenderingContext2D,
  rivers: EditorRiver[],
  lineWidth: number,
  options: RiverDrawOptions,
): void {
  const buildPath = (river: EditorRiver): { path: Path2D; avgWidth: number } | null => {
    if (river.points.length < 2) return null;
    const cached = riverLinePathCache.get(river.points);
    if (cached) return cached;
    const path = new Path2D();
    path.moveTo(river.points[0].x, river.points[0].z);
    let widthSum = 0;
    for (let i = 1; i < river.points.length; i++) {
      path.lineTo(river.points[i].x, river.points[i].z);
      widthSum += river.points[i].width;
    }
    const built = { path, avgWidth: widthSum / (river.points.length - 1) };
    riverLinePathCache.set(river.points, built);
    return built;
  };
  const buildFill = (polygon: WaterPolygonGeoJson): Path2D => {
    const cached = riverFillPathCache.get(polygon);
    if (cached) return cached;
    const fill = new Path2D();
    for (const part of polygonParts(polygon)) {
      for (const ring of part) {
        if (ring.length < 3) continue;
        fill.moveTo(ring[0][0], ring[0][1]);
        for (let i = 1; i < ring.length; i++) fill.lineTo(ring[i][0], ring[i][1]);
        fill.closePath();
      }
    }
    riverFillPathCache.set(polygon, fill);
    return fill;
  };

  ctx.lineJoin = "round";
  ctx.lineCap = "round";

  rivers.forEach((river, index) => {
    const built = buildPath(river);
    if (!built) return;
    const selected = river.id === options.selectedId;
    const emphasis = selected ? 2 : 1;
    if (river.carved) {
      // Carved: fill area polygons (including holes) with water color; cut holes using even-odd fill.
      if (river.polygon && options.waterVisible) {
        const fill = buildFill(river.polygon);
        ctx.fillStyle = "rgba(60,140,220,0.45)";
        ctx.fill(fill, "evenodd");
        ctx.strokeStyle = "rgba(120,200,255,0.9)";
        ctx.lineWidth = lineWidth * 0.75;
        ctx.setLineDash([]);
        ctx.stroke(fill);
      }
      // Water profile line (course through the area's center).
      ctx.strokeStyle = river.polygon ? `rgba(40,110,220,${selected ? 0.95 : 0.6})` : "rgba(255,140,40,0.9)";
      ctx.lineWidth = lineWidth * 0.75 * emphasis;
      ctx.setLineDash([]);
      ctx.stroke(built.path);
    } else {
      // Uncarved: fill the preview area, if present, with faint water color and show the course as a bright light-blue dashed line.
      const previewPolygon = options.previewPolygons.get(river.id);
      if (previewPolygon && options.waterVisible) {
        const fill = buildFill(previewPolygon);
        ctx.fillStyle = "rgba(90,190,240,0.35)";
        ctx.fill(fill, "evenodd");
        ctx.strokeStyle = "rgba(150,225,255,0.8)";
        ctx.lineWidth = lineWidth * 0.75;
        ctx.setLineDash([]);
        ctx.stroke(fill);
      } else {
        ctx.strokeStyle = "rgba(120,200,255,0.25)";
        ctx.lineWidth = Math.max(built.avgWidth, lineWidth);
        ctx.setLineDash([]);
        ctx.stroke(built.path);
      }
      ctx.strokeStyle = selected ? "rgba(255,255,255,0.95)" : "rgba(120,220,255,0.95)";
      ctx.lineWidth = lineWidth * emphasis;
      ctx.setLineDash([lineWidth * 4, lineWidth * 3]);
      ctx.stroke(built.path);
      ctx.setLineDash([]);
    }

    // Number label at the source, matching "River N" in the list. Use a circular background for readability over terrain.
    const head = river.points[0];
    const radius = lineWidth * 6 * (selected ? 1.25 : 1);
    ctx.beginPath();
    ctx.arc(head.x, head.z, radius, 0, Math.PI * 2);
    ctx.fillStyle = selected ? "rgba(255,255,255,0.95)" : river.carved ? "rgba(40,110,220,0.9)" : "rgba(20,120,200,0.9)";
    ctx.fill();
    ctx.strokeStyle = "rgba(255,255,255,0.9)";
    ctx.lineWidth = lineWidth * 0.75;
    ctx.stroke();
    ctx.fillStyle = selected ? "#10305a" : "#ffffff";
    ctx.font = `bold ${lineWidth * 8}px sans-serif`;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    fillWorldText(ctx, String(index + 1), head.x, head.z);
  });
}

/** Distance to the nearest point on a polyline from (wx, wz), the containing segment's start index, and parameter t along it. */
function nearestOnPolyline(
  points: ReadonlyArray<{ x: number; z: number }>,
  wx: number,
  wz: number,
): { dist: number; index: number; t: number } | null {
  if (points.length === 0) return null;
  if (points.length === 1) return { dist: Math.hypot(points[0].x - wx, points[0].z - wz), index: 0, t: 0 };
  let best = { dist: Infinity, index: 0, t: 0 };
  for (let i = 0; i < points.length - 1; i++) {
    const a = points[i];
    const b = points[i + 1];
    const dx = b.x - a.x;
    const dz = b.z - a.z;
    const len2 = dx * dx + dz * dz;
    const t = len2 < 1e-12 ? 0 : Math.max(0, Math.min(1, ((wx - a.x) * dx + (wz - a.z) * dz) / len2));
    const dist = Math.hypot(a.x + dx * t - wx, a.z + dz * t - wz);
    if (dist < best.dist) best = { dist, index: i, t };
  }
  return best;
}

/** Index of the nearest control point within tol of (wx, wz), or -1 if none. */
function findRiverNodeAt(cps: ReadonlyArray<RiverControlPoint>, wx: number, wz: number, tol: number): number {
  let best = -1;
  let bestDist = tol;
  for (let i = 0; i < cps.length; i++) {
    const d = Math.hypot(cps[i].x - wx, cps[i].z - wz);
    if (d <= bestDist) {
      bestDist = d;
      best = i;
    }
  }
  return best;
}

/**
 * Draws a manually drafted course: lines joining placed vertices and a mouse-following line.
 * Numbers the vertices and puts an arrow at the last segment's tip to show upstream → downstream.
 */
function strokeRiverDraft(ctx: CanvasRenderingContext2D, points: WorldPoint[], mouse: WorldPoint | null, lineWidth: number): void {
  if (points.length === 0) return;
  ctx.lineJoin = "round";
  ctx.lineCap = "round";
  ctx.strokeStyle = "rgba(120,220,255,0.95)";
  ctx.lineWidth = lineWidth * 1.5;
  ctx.setLineDash([lineWidth * 4, lineWidth * 3]);
  ctx.beginPath();
  ctx.moveTo(points[0].x, points[0].z);
  for (let i = 1; i < points.length; i++) ctx.lineTo(points[i].x, points[i].z);
  if (mouse) ctx.lineTo(mouse.x, mouse.z);
  ctx.stroke();
  ctx.setLineDash([]);

  // Direction arrow (last committed segment, or the following line's tip while tracking the mouse).
  const tip = mouse ?? points[points.length - 1];
  const from = mouse ? points[points.length - 1] : points[points.length - 2];
  if (from && (tip.x !== from.x || tip.z !== from.z)) {
    const ang = Math.atan2(tip.z - from.z, tip.x - from.x);
    const size = lineWidth * 8;
    ctx.fillStyle = "rgba(120,220,255,0.95)";
    ctx.beginPath();
    ctx.moveTo(tip.x, tip.z);
    ctx.lineTo(tip.x - size * Math.cos(ang - 0.5), tip.z - size * Math.sin(ang - 0.5));
    ctx.lineTo(tip.x - size * Math.cos(ang + 0.5), tip.z - size * Math.sin(ang + 0.5));
    ctx.closePath();
    ctx.fill();
  }

  const r = lineWidth * 4;
  ctx.font = `bold ${lineWidth * 7}px sans-serif`;
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  points.forEach((p, i) => {
    ctx.beginPath();
    ctx.arc(p.x, p.z, r, 0, Math.PI * 2);
    ctx.fillStyle = "rgba(255,255,255,0.95)";
    ctx.fill();
    ctx.strokeStyle = "rgba(20,120,200,0.95)";
    ctx.lineWidth = lineWidth;
    ctx.stroke();
    ctx.fillStyle = "#10305a";
    fillWorldText(ctx, String(i + 1), p.x, p.z);
  });
}

/**
 * Draws control-point handles for the river being node-edited: thin dashed lines between
 * control points to distinguish them from the curve, plus a circle for each node.
 * Enlarge hovered nodes and color them yellow; distinguish the endpoint (mouth) with a diamond.
 */
function strokeRiverControlPoints(ctx: CanvasRenderingContext2D, cps: ReadonlyArray<RiverControlPoint>, hoveredIndex: number, lineWidth: number): void {
  if (cps.length === 0) return;
  ctx.strokeStyle = "rgba(255,255,255,0.45)";
  ctx.lineWidth = lineWidth * 0.75;
  ctx.setLineDash([lineWidth * 2, lineWidth * 2]);
  ctx.beginPath();
  ctx.moveTo(cps[0].x, cps[0].z);
  for (let i = 1; i < cps.length; i++) ctx.lineTo(cps[i].x, cps[i].z);
  ctx.stroke();
  ctx.setLineDash([]);
  cps.forEach((p, i) => {
    const hovered = i === hoveredIndex;
    const r = lineWidth * (hovered ? 5 : 3.5);
    ctx.beginPath();
    if (i === cps.length - 1) {
      ctx.moveTo(p.x, p.z - r * 1.3);
      ctx.lineTo(p.x + r * 1.3, p.z);
      ctx.lineTo(p.x, p.z + r * 1.3);
      ctx.lineTo(p.x - r * 1.3, p.z);
      ctx.closePath();
    } else {
      ctx.arc(p.x, p.z, r, 0, Math.PI * 2);
    }
    ctx.fillStyle = hovered ? "rgba(255,230,90,0.95)" : "rgba(255,255,255,0.95)";
    ctx.fill();
    ctx.strokeStyle = "rgba(20,90,170,0.95)";
    ctx.lineWidth = lineWidth;
    ctx.stroke();
  });
}

export function TerrainEditorCanvas({ mapId }: TerrainEditorCanvasProps) {
  const master = useMasterData();
  const { t, i18n } = useTranslation();
  // The map stacks three canvases (see renderFrame): terrain (WebGL2), vector
  // (Canvas2D, also receives mouse input), and cursor (Canvas2D), from bottom to top.
  const terrainGlCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const cursorCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const coordOverlayRef = useRef<HTMLDivElement | null>(null);
  const zoomOverlayRef = useRef<HTMLDivElement | null>(null);

  // Loading status, kept in React state for conditional rendering.
  const [status, setStatus] = useState<"loading" | "ready" | "error">("loading");
  const [errorMsg, setErrorMsg] = useState<string>("");

  // Terrain-layer renderer for terrain colors, selection, grid, and contours. Created when terrain
  // is first set, with GL resources released on unmount. Without WebGL2, creation fails and loading errors.
  const rendererRef = useRef<TerrainEditorRenderer | null>(null);
  // Render scheduling. draw()/drawCursorLayer() only request one requestAnimationFrame, batching
  // repeated calls into one render per frame. fullFramePendingRef indicates whether to redraw all layers.
  const frameRequestRef = useRef<number | null>(null);
  const fullFramePendingRef = useRef(false);
  // Cell-boundary grid spacing (cell_size intervals). Rendering also runs from event-handler
  // closures recreated only on status changes, so read this ref rather than state.
  const cellGridIntervalRef = useRef<number>(500);
  const settingsRef = useRef<TerrainSettings | null>(null);
  const viewRef = useRef<ViewState>({ offsetX: 0, offsetY: 0, zoom: 1 });

  // Zoom-dependent contour spacing. baseIntervalRef/baseZoomRef store the interval and zoom
  // at the reference fit view (set immediately after resetView). Each render refines the interval
  // with pickContourIntervalForZoom according to zoom-in beyond that reference.
  const baseIntervalRef = useRef<number>(100);
  const baseZoomRef = useRef<number>(1);

  const dragRef = useRef<{ active: boolean; startX: number; startY: number; offX: number; offY: number }>({
    active: false,
    startX: 0,
    startY: 0,
    offX: 0,
    offY: 0,
  });
  // Screen position where the left button was pressed. Retained until release, using mousemove
  // displacement to distinguish an in-place click (single selection) from a drag (pan).
  // Once dragging begins, dragRef.active becomes true and subsequent movement is panning.
  const leftDownRef = useRef<{ x: number; y: number } | null>(null);

  // Actual elevation data, modified only when "Apply" is pressed, contained within this component
  // without affecting backend state. Upload changed regions to the renderer after edits.
  const elevRef = useRef<Float32Array | null>(null);

  // Selection mask: 0..1 weights per grid point, merged by maximum (union) for every brush/polygon
  // click. The "Apply" button applies the editing method to this mask,
  // which persists until explicitly cleared with "Reset selection".
  const selectionMaskRef = useRef<Float32Array | null>(null);
  // Feather-preview weights for a closed polygon (0..255, same order as the grid), uploaded to the renderer.
  const polygonPreviewWeightsRef = useRef<Uint8Array | null>(null);
  // Selection bounding rectangle (grid bounds), used both as the "Apply" scan region and to test selection existence.
  const selectionBoundsRef = useRef<DirtyRect | null>(null);

  // Pending rectangle and scheduling state for throttling selection-delta sends to the 3D preview
  // window (terrain_3d_preview) to one per rAF. Emitting on every drag event is excessive,
  // so batch changes accumulated within a frame into one send.
  const previewSyncRef = useRef<{ pendingSelectionRect: DirtyRect | null; rafId: number | null }>({
    pendingSelectionRect: null,
    rafId: null,
  });

  // --- 3D preview connection (see event definitions in terrainData.ts for the contract) ---
  // sessionId of the attached preview (request_state received, not detached). Send nothing if empty.
  const previewSessionsRef = useRef<Set<string>>(new Set());
  // Generation of the state sent to the 3D preview (elevations, selection, water, rivers, ground cover), advanced on every change.
  const previewStateVersionRef = useRef(0);
  // Last fully sent generation for the attached preview, and the generation received by a detached preview when it detached.
  // A preview returning from hidden does not receive full state again if nothing changed while hidden,
  // since receiving full state and rebuilding the mesh can take seconds on large maps.
  const previewSentVersionRef = useRef(0);
  const detachedPreviewVersionsRef = useRef<Map<string, number>>(new Map());
  // Scheduled full-state send and time of the first pending change (performance.now), batching consecutive changes into one send.
  const fullStateTimerRef = useRef<number | null>(null);
  const fullStatePendingSinceRef = useRef<number | null>(null);

  // --- Editing tool UI state ---
  // Actions have exclusive roles without separate move/edit modes: left click selects once,
  // left drag pans, and right click confirms polygons.
  const [shape, setShape] = useState<ShapeKind>("circle");
  const [operation, setOperation] = useState<EditOperation>("raiseLower");
  const [brushParams, setBrushParams] = useState<BrushShapeParams>({ size: 50, aspect: 1, angleDeg: 0 });
  // How selection boundaries blend into surrounding terrain, shared by brush and polygon selections.
  // Baked into mask weights when selection is committed, so it does not belong to the editing method.
  const [selectionFalloff, setSelectionFalloff] = useState<FalloffKind>("smooth");
  const [raiseLowerParams, setRaiseLowerParams] = useState<RaiseLowerParams>({ rate: 20 });
  const [flattenParams, setFlattenParams] = useState<FlattenParams>({ target: 100, rate: 0.05 });
  const [noiseParams, setNoiseParams] = useState<NoiseToolParams>({
    algorithm: "fractal",
    scale: 60,
    octaves: 3,
    amplitude: 8,
    bias: "both",
  });
  // Boundary feather width in world units, shared by circle/square brushes and polygons.
  // Interior weight is always 1; attenuation extends outward from the boundary by this width.
  const [featherWidth, setFeatherWidth] = useState<number>(20);
  const [polygonPointCount, setPolygonPointCount] = useState(0);
  const [polygonClosed, setPolygonClosed] = useState(false);
  // Whether a selection exists, used to enable/disable buttons. The actual bounds are in selectionBoundsRef.
  const [hasSelection, setHasSelection] = useState(false);

  // --- Active editing tab state ---
  // Mutually exclusive terrain, water surface, river, and camera editing.
  const metadataDialogRef = useRef<HTMLDialogElement>(null);
  const [activeTab, setActiveTab] = useState<EditorTab>("terrain");
  const [terrainEditTab, setTerrainEditTab] = useState<"auto" | "manual">("manual");

  // --- UI state for non-sea water surfaces (lakes, etc.) ---
  // In water mode, left click adds water polygon vertices and right click closes the shape,
  // independently of elevation selection. Water surfaces are created here and written back to
  // the Scenario with "Save to Scenario". Actual data is in waterSurfacesRef / waterPointsRef.
  const [waterMode, setWaterMode] = useState(false);
  // Y elevation of the next water surface (meters), in the same absolute frame as heightmap (sea level = 0).
  const [waterLevelInput, setWaterLevelInput] = useState(0);
  // Whether the drafted water polygon is closed (shape confirmed). This supports the same two-stage
  // approach as polygon selection: close shape → set water level → confirm water surface.
  // Vertices can be added until closure; after setting the level, "Confirm water surface" registers the surface.
  const [waterPolygonClosed, setWaterPolygonClosed] = useState(false);
  // Counts of created water surfaces and drafted polygon vertices for button display; actual data is in refs.
  const [waterSurfaceCount, setWaterSurfaceCount] = useState(0);
  const [waterPointCount, setWaterPointCount] = useState(0);

  // --- Initial camera UI state ---
  // While camera editing is active, left click sets position and right click sets a look-at point,
  // deriving yaw from their bearing. Height, downward pitch, and azimuth can also be changed
  // through card inputs. cameraRef holds the data and cameraState mirrors it for display.
  // One per map; if unset (null), the game uses the default view at the origin.
  const [cameraMode, setCameraMode] = useState(false);
  const [cameraState, setCameraState] = useState<TerrainCameraPayload | null>(null);

  // --- River editing UI state ---
  // River workflow: "Calculate courses" → add as uncarved to the list → delete/adjust depth individually
  // → "Carve" into terrain one by one. Calculations never modify elevations,
  // allowing repeated recalculation with changed parameters (replaces only uncarved rivers).
  // Reuse the terrain-editing selection mask for rainfall; if selected, rain falls only within it.
  const [riverParams, setRiverParams] = useState<RiverGenParams>({
    rainfallMmPerYear: 1000,
    minDischargeM3s: 0.5,
    meanderStrength: 0.5,
    meanderIterations: 30,
    widthCoef: 5,
    // Default depth coefficient. Since d = coefficient × Q^0.4, maps with small discharge need a larger
    // coefficient for visible carving (otherwise grooves are below 1 m against a 20 m grid spacing).
    depthCoef: 1.5,
    enableBraiding: true,
  });
  // Preview summary (river count, total length in km, maximum discharge on land). null means no preview.
  // maxDischargeM3s diagnoses zero-course results by showing that the threshold is beyond
  // the map's maximum discharge, prompting adjustment of minimum discharge/rainfall.
  const [riverPreviewStats, setRiverPreviewStats] = useState<{
    count: number;
    totalKm: number;
    maxDischargeM3s: number;
    /** Rivers discarded for being too short, distinguishing insufficient discharge thresholds from sections that are too short. */
    discardedShortCount: number;
  } | null>(null);
  // Mirrors riversRef in state for the created-river list and redraws of delete/carve buttons.
  // Uncarved rivers show min–max width to reveal discharge-based width variation upstream/downstream;
  // carved rivers show area-polygon size in hectares.
  const [riverList, setRiverList] = useState<
    {
      id: string;
      lengthKm: number;
      minWidth: number;
      maxWidth: number;
      carved: boolean;
      depthCoef: number;
      /** Area polygon size (ha). null if uncarved or not yet extracted. */
      areaHa: number | null;
      /** Whether the river supports node editing (uncarved with control points). */
      editable: boolean;
      /** Number of control points (nodes); 0 for rivers without them. */
      nodeCount: number;
      /** Upstream/downstream widths of manually drawn rivers (meters); null for generated rivers. */
      manualWidths: { head: number; tail: number } | null;
      /** Mouth type, used to label uncarved rivers as sea/lake/confluence/unconnected. */
      mouth: RiverMouthKind;
    }[]
  >([]);
  // --- Carving preview ---
  // Shows what uncarved rivers would look like with the current depth coefficient without changing
  // actual elevations (elevRef). previewElevRef is a display copy of elevRef with uncarved rivers carved;
  // the terrain layer and 3D preview use it as displayElev when present.
  // previewPolygonsRef contains uncarved river areas extracted from preview terrain for water display.
  // Actual committed carving still uses carveRiverObjects on elevRef.
  const previewElevRef = useRef<Float32Array | null>(null);
  const previewPolygonsRef = useRef<Map<string, WaterPolygonGeoJson | null>>(new Map());
  const previewRebuildTimerRef = useRef<number | null>(null);
  const [riverPreviewEnabled, setRiverPreviewEnabled] = useState(true);
  const riverPreviewEnabledRef = useRef(true);
  const [riverWaterVisible, setRiverWaterVisible] = useState(true);
  const riverWaterVisibleRef = useRef(true);
  const [selectedRiverId, setSelectedRiverId] = useState<string | null>(null);
  const selectedRiverIdRef = useRef<string | null>(null);
  const activeTabRef = useRef<EditorTab>("terrain");
  // --- Manual course drawing and node editing ---
  // River tab pointer operation (rainfall selection / draw river / edit nodes). Reset to rain when leaving the tab.
  // riverDraftPointsRef holds drafted vertices; riverNodeDragRef holds node dragging state;
  // hoveredRiverNodeRef holds the node under the mouse for handle highlighting.
  const [riverPointerMode, setRiverPointerMode] = useState<RiverPointerMode>("rain");
  const [riverDraftPointCount, setRiverDraftPointCount] = useState(0);
  // Initial upstream/downstream widths (meters) when confirming a manual river, adjustable per river in list rows afterward.
  const [riverManualWidthHead, setRiverManualWidthHead] = useState(6);
  const [riverManualWidthTail, setRiverManualWidthTail] = useState(20);
  const [riverRouteRadius, setRiverRouteRadius] = useState(100);
  const riverDraftPointsRef = useRef<WorldPoint[]>([]);
  const riverNodeDragRef = useRef<{ riverId: string; index: number; moved: boolean } | null>(null);
  const hoveredRiverNodeRef = useRef<number>(-1);
  // No minimum width, allowing streams and canals. Courses narrower than the grid spacing
  // are carved as continuous one-grid-wide grooves by carveRivers' direct thalweg cutting.

  // --- Scenario being edited ---
  // Dimensions, grid spacing, and cell size are tied to the Scenario city and cannot change (the core save API rejects dimension changes).
  // Display these read-only in metadata; only ground cover and climate are editable.
  const [scenarioInfo, setScenarioInfo] = useState<{ title: LocalizedText; terrainSource: string; farSpacing: number; cellSize: number } | null>(null);
  const [locks, setLocks] = useState<TerrainEditorLocks>({ heights_locked: false, reasons: [] });
  const heightsLockedRef = useRef(false);
  const [saveStatus, setSaveStatus] = useState<"idle" | "saving" | "saved" | "error">("idle");
  const [saveErrorMsg, setSaveErrorMsg] = useState<string>("");
  // Generation advanced on each change to saved content (elevations, water, carved rivers, camera, ground cover, climate),
  // and generation of the last completed save. Edits during saving remain unsaved relative to the generation when saving began.
  const [editVersion, setEditVersion] = useState(0);
  const [savedVersion, setSavedVersion] = useState(0);
  const hasUnsavedChanges = editVersion !== savedVersion;
  const editVersionRef = useRef(0);

  /** Records a change to saved content. Every operation modifying it must call this, including pushUndoState and Undo/Redo. */
  function markDirty() {
    editVersionRef.current++;
    setEditVersion(editVersionRef.current);
  }

  // --- Automatic editing (erosion simulation) ---
  // Read only directly from button onClick, so do not mirror into the pointer-handler ref
  // (toolStateRef). See internal constants in terrainErosion.ts for detailed coefficients.
  const [thermalParams, setThermalParams] = useState<ThermalErosionParams>({ talusAngleDeg: 33, iterations: 50 });
  const [hydraulicParams, setHydraulicParams] = useState<HydraulicErosionParams>({ droplets: 50000, erodeStrength: 0.3 });
  const [seaFloorParams, setSeaFloorParams] = useState<SeaFloorParams>({ seaLevelThreshold: 0, slopeMode: "land", shelfSlopePercent: 2, maxDepth: 50 });
  // Cells changed by the latest sea-floor generation. 0 diagnoses no cells qualifying as sea; null means not run yet.
  const [seaFloorModifiedCount, setSeaFloorModifiedCount] = useState<number | null>(null);

  // --- Undo / Redo ---
  // Snapshot rivers together with elevations, preventing Undo of riverbed carving from restoring
  // terrain while leaving the river list unchanged, or vice versa.
  // Include water surfaces and camera in the same snapshot. Lists are always replaced immutably,
  // so shallow array copies are sufficient snapshots.
  type UndoState = {
    elev: Float32Array;
    mapWidth: number;
    mapHeight: number;
    spacing: number;
    rows: number;
    cols: number;
    rivers: EditorRiver[];
    waterSurfaces: EditorWaterSurface[];
    camera: TerrainCameraPayload | null;
  };
  const MAX_UNDO = 5;
  const undoStackRef = useRef<UndoState[]>([]);
  const redoStackRef = useRef<UndoState[]>([]);

  /** Records current elevations, map dimensions, and river list in the Undo stack, clearing Redo. */
  function pushUndoState() {
    const elev = elevRef.current;
    const s = settingsRef.current;
    if (!elev || !s) return;
    markDirty();
    undoStackRef.current.push({
      elev: new Float32Array(elev),
      mapWidth: s.mapWidth,
      mapHeight: s.mapHeight,
      spacing: s.spacing,
      rows: s.rows,
      cols: s.cols,
      rivers: riversRef.current.slice(),
      waterSurfaces: waterSurfacesRef.current.slice(),
      camera: cameraRef.current,
    });
    if (undoStackRef.current.length > MAX_UNDO) undoStackRef.current.shift();
    redoStackRef.current = [];
  }

  /** Restores state from Undo, moving the current state to Redo. */
  function performUndo() {
    const s = settingsRef.current;
    const target = undoStackRef.current.pop();
    if (!target || !s) return;
    markDirty();
    // Move the current state to the Redo stack.
    const elev = elevRef.current;
    if (elev) {
      redoStackRef.current.push({
        elev: new Float32Array(elev),
        mapWidth: s.mapWidth,
        mapHeight: s.mapHeight,
        spacing: s.spacing,
        rows: s.rows,
        cols: s.cols,
        rivers: riversRef.current.slice(),
        waterSurfaces: waterSurfacesRef.current.slice(),
        camera: cameraRef.current,
      });
      if (redoStackRef.current.length > MAX_UNDO) redoStackRef.current.shift();
    }
    // Restore rivers and water lists first because rebuildTerrainVisuals sends full state
    // (including rivers/water) to the 3D preview at the end.
    restoreRivers(target.rivers);
    restoreWaterSurfaces(target.waterSurfaces);
    restoreCamera(target.camera);
    rebuildTerrainVisuals(target.elev, target.mapWidth, target.mapHeight, target.spacing, target.rows, target.cols);
    // Rebuild carving preview from restored rivers/terrain (disabled outside the river editing tab).
    scheduleRiverPreviewRebuild();
  }

  /** Restores state from Redo, moving the current state to Undo. */
  function performRedo() {
    const s = settingsRef.current;
    const target = redoStackRef.current.pop();
    if (!target || !s) return;
    markDirty();
    const elev = elevRef.current;
    if (elev) {
      undoStackRef.current.push({
        elev: new Float32Array(elev),
        mapWidth: s.mapWidth,
        mapHeight: s.mapHeight,
        spacing: s.spacing,
        rows: s.rows,
        cols: s.cols,
        rivers: riversRef.current.slice(),
        waterSurfaces: waterSurfacesRef.current.slice(),
        camera: cameraRef.current,
      });
      if (undoStackRef.current.length > MAX_UNDO) undoStackRef.current.shift();
    }
    restoreRivers(target.rivers);
    restoreWaterSurfaces(target.waterSurfaces);
    restoreCamera(target.camera);
    rebuildTerrainVisuals(target.elev, target.mapWidth, target.mapHeight, target.spacing, target.rows, target.cols);
    // Rebuild carving preview from restored rivers/terrain (disabled outside the river editing tab).
    scheduleRiverPreviewRebuild();
  }

  // Pointer handlers are recreated only on status changes, so mirror tool settings in a ref
  // to ensure they always read current settings.
  const toolStateRef = useRef({
    shape,
    operation,
    brushParams,
    selectionFalloff,
    raiseLowerParams,
    flattenParams,
    noiseParams,
    featherWidth,
    polygonClosed,
    waterMode,
    waterLevelInput,
    waterPolygonClosed,
    cameraMode,
    riverPointerMode,
    riverParams,
    riverManualWidthHead,
    riverManualWidthTail,
  });
  useEffect(() => {
    toolStateRef.current = {
      shape,
      operation,
      brushParams,
      selectionFalloff,
      raiseLowerParams,
      flattenParams,
      noiseParams,
      featherWidth,
      polygonClosed,
      waterMode,
      waterLevelInput,
      waterPolygonClosed,
      cameraMode,
      riverPointerMode,
      riverParams,
      riverManualWidthHead,
      riverManualWidthTail,
    };
  }, [shape, operation, brushParams, selectionFalloff, raiseLowerParams, flattenParams, noiseParams, featherWidth, polygonClosed, waterMode, waterLevelInput, waterPolygonClosed, cameraMode, riverPointerMode, riverParams, riverManualWidthHead, riverManualWidthTail]);

  // Current brush center (world coordinates), kept in a ref because it changes on every mouse move.
  const brushWorldPosRef = useRef<WorldPoint | null>(null);
  // Unconfirmed polygon-tool vertices.
  const polygonPointsRef = useRef<WorldPoint[]>([]);

  // === Non-sea water surface data (lakes, etc.) ===
  // Created water surfaces, internally {id, waterLevel, points (world-coordinate vertices)}.
  // Convert to GeoJSON-like WaterSurfacePayload on save / 3D preview send.
  const waterSurfacesRef = useRef<EditorWaterSurface[]>([]);
  // Unconfirmed vertices being drawn in water mode.
  const waterPointsRef = useRef<WorldPoint[]>([]);

  // === River data ===
  // Contains both uncarved (just calculated) and carved rivers with a carved flag.
  // Only carved rivers are saved or sent to the 3D preview.
  const riversRef = useRef<EditorRiver[]>([]);

  // === Resources and Props (not edited) ===
  // Retain loaded contents to send back unchanged on save. Not sent to the 3D preview either.
  const untouchedContentRef = useRef<{ resources: ResourceAreaPayload[]; props: TerrainProps } | null>(null);

  // === Initial camera data ===
  // Saved to the Scenario as its starting viewpoint; not sent to the 3D preview.
  const cameraRef = useRef<TerrainCameraPayload | null>(null);

  // === Ground cover type ===
  // Saved to the Scenario, also determining terrain color ramps in 2D/3D previews.
  // Kept in a ref because drawing functions called from event closures cannot read state.
  const groundCoverRef = useRef<number>(GROUND_COVER_GRASSLAND);
  const [groundCover, setGroundCover] = useState<number>(GROUND_COVER_GRASSLAND);

  // === Climate category ===
  // Saved to the Scenario. Determines in-game weather only, without affecting editor rendering.
  const [climate, setClimate] = useState<ClimateSettings | null>(null);

  /** Clears unconfirmed vertices when switching away from polygon selection. */
  useEffect(() => {
    if (shape !== "polygon" && polygonPointsRef.current.length > 0) {
      polygonPointsRef.current = [];
      setPolygonPointCount(0);
    }
  }, [shape]);

  /** Cancels scheduled renders/full-state sends and releases terrain-layer GL resources on unmount. */
  useEffect(() => {
    return () => {
      if (frameRequestRef.current != null) cancelAnimationFrame(frameRequestRef.current);
      frameRequestRef.current = null;
      if (fullStateTimerRef.current != null) window.clearTimeout(fullStateTimerRef.current);
      fullStateTimerRef.current = null;
      rendererRef.current?.dispose();
      rendererRef.current = null;
    };
  }, []);

  /** Cancels any pending rAF selection-delta send to the 3D preview on unmount. */
  useEffect(() => {
    return () => {
      if (previewSyncRef.current.rafId != null) cancelAnimationFrame(previewSyncRef.current.rafId);
    };
  }, []);

  /**
   * Manages the 3D preview (terrain_3d_preview) connection. Previews send request_state on mount
   * or reappearance, and detach when hidden/unmounted. Respond with full in-memory editing
   * state, so previews opened later or reopened synchronize with edits rather than saved disk
   * data. Do not resend if state has not changed while detached. Send state only while a preview
   * is attached.
   */
  useEffect(() => {
    let cancelled = false;
    const unlisteners: (() => void)[] = [];
    const keep = (fn: () => void) => {
      if (cancelled) fn();
      else unlisteners.push(fn);
    };
    listen<Terrain3DRequestStatePayload>(EVT_TERRAIN_3D_REQUEST_STATE, (event) => {
      const { mapId: target, sessionId } = event.payload;
      if (target !== mapId) return;
      const detachedAt = detachedPreviewVersionsRef.current.get(sessionId);
      detachedPreviewVersionsRef.current.delete(sessionId);
      previewSessionsRef.current.add(sessionId);
      if (detachedAt !== previewStateVersionRef.current) emitTerrain3dFullState();
    }).then(keep);
    listen<Terrain3DDetachPayload>(EVT_TERRAIN_3D_DETACH, (event) => {
      const { mapId: target, sessionId } = event.payload;
      if (target !== mapId || !previewSessionsRef.current.delete(sessionId)) return;
      detachedPreviewVersionsRef.current.set(sessionId, previewSentVersionRef.current);
    }).then(keep);
    return () => {
      cancelled = true;
      for (const fn of unlisteners) fn();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mapId]);

  /**
   * View → world coordinates: inverse of screenX = (worldX - offsetX) * zoom,
   * screenY = (offsetY - worldZ) * zoom. offsetY is world Z at the top edge; north (+Z)
   * is upward, so Z decreases downward on screen.
   */
  function screenToWorld(sx: number, sy: number): [number, number] {
    const v = viewRef.current;
    return [sx / v.zoom + v.offsetX, v.offsetY - sy / v.zoom];
  }

  /** Resets the view to fit the entire terrain. */
  function resetView() {
    const cv = canvasRef.current;
    const s = settingsRef.current;
    if (!cv || !s) return;
    const cssW = cv.clientWidth;
    const cssH = cv.clientHeight;
    const pad = 40;
    const scaleX = (cssW - pad * 2) / s.mapWidth;
    const scaleY = (cssH - pad * 2) / s.mapHeight;
    const zoom = Math.max(0.0001, Math.min(scaleX, scaleY));
    viewRef.current = {
      zoom,
      offsetX: -s.mapWidth / 2 - (cssW / zoom - s.mapWidth) / 2,
      offsetY: s.mapHeight / 2 + (cssH / zoom - s.mapHeight) / 2,
    };
  }

  /** Redraws all map layers next frame, batching repeated calls into one render per frame. */
  function draw() {
    requestFrame(true);
  }

  /** Redraws only the cursor layer next frame, for mouse movement without view or edited-data changes. */
  function drawCursorLayer() {
    requestFrame(false);
  }

  function requestFrame(full: boolean) {
    if (full) fullFramePendingRef.current = true;
    if (frameRequestRef.current != null) return;
    frameRequestRef.current = requestAnimationFrame(() => {
      frameRequestRef.current = null;
      const fullFrame = fullFramePendingRef.current;
      fullFramePendingRef.current = false;
      renderFrame(fullFrame);
    });
  }

  /**
   * Renders one frame across three stacked canvases:
   *   - Terrain (WebGL2): terrain colors, selection, 100 m grid, cell boundaries, contours, drawn by the renderer.
   *   - Vector (Canvas2D): committed water surfaces, rivers, camera.
   *   - Cursor (Canvas2D): mouse-dependent brush, drafted shapes, river nodes, etc.
   * With full=false (mouse movement only), redraw only the cursor layer so more rivers
   * do not increase hover cost.
   */
  function renderFrame(full: boolean) {
    const cv = canvasRef.current;
    const cursorCv = cursorCanvasRef.current;
    const s = settingsRef.current;
    if (!cv || !cursorCv || !s) return;
    const ctx = cv.getContext("2d");
    const cursorCtx = cursorCv.getContext("2d");
    if (!ctx || !cursorCtx) return;
    const ts = toolStateRef.current;

    const dpr = window.devicePixelRatio || 1;
    const cssW = cv.clientWidth;
    const cssH = cv.clientHeight;
    if (cssW === 0 || cssH === 0) return;

    // Match all three backing stores to CSS size × dpr. Resizing clears them, so redraw every layer.
    const bw = Math.round(cssW * dpr);
    const bh = Math.round(cssH * dpr);
    for (const canvas of [terrainGlCanvasRef.current, cv, cursorCv]) {
      if (canvas && (canvas.width !== bw || canvas.height !== bh)) {
        canvas.width = bw;
        canvas.height = bh;
        full = true;
      }
    }

    const v = viewRef.current;
    const lineW = 1.5 / v.zoom;
    if (full) {
      const contourInterval = pickContourIntervalForZoom(baseIntervalRef.current, baseZoomRef.current, v.zoom);
      rendererRef.current?.render({
        offsetX: v.offsetX,
        offsetY: v.offsetY,
        zoom: v.zoom,
        dpr,
        minLand: s.minLand,
        maxLand: s.maxLand,
        ramp: groundCoverRamp(groundCoverRef.current),
        sea: SEA_RGB,
        contourInterval,
        contourMajorStart: Math.ceil(s.minLand / contourInterval),
        gridInterval: 100,
        showGrid: 100 * v.zoom >= 6, // Omit lines at scales where they would be too dense.
        cellInterval: cellGridIntervalRef.current,
        showCellGrid: cellGridIntervalRef.current * v.zoom >= 4,
        showPolygonPreview: ts.polygonClosed,
      });

      beginWorldLayer(ctx, v, dpr);
      strokeWaterSurfaces(ctx, waterSurfacesRef.current, [], null, lineW);
      // Rivers: carved = dark blue area + centerline; uncarved (just calculated) = light-blue dashed line.
      strokeRivers(ctx, riversRef.current, lineW, {
        previewPolygons: previewPolygonsRef.current,
        waterVisible: riverWaterVisibleRef.current,
        selectedId: selectedRiverIdRef.current,
      });
      strokeCameraMarker(ctx, cameraRef.current, lineW);
      ctx.restore();

      // Update overlays through direct DOM replacement to avoid React rerenders.
      if (zoomOverlayRef.current) {
        zoomOverlayRef.current.textContent = Math.round(v.zoom * 100) + "%";
      }
    }

    beginWorldLayer(cursorCtx, v, dpr);
    const cursor = brushWorldPosRef.current;
    // Brush/polygon preview, always shown because removing mode switching leaves editing tools continuously active.
    // Hide elevation brush/polygon previews in water mode or river drawing/node editing,
    // since input is then assigned to drawing water surfaces/courses.
    if (ts.waterMode || ts.cameraMode || ts.riverPointerMode !== "rain") {
      // Draw nothing here in water/camera/course modes; corresponding stroke* functions below handle them.
    } else if (ts.shape === "polygon") {
      strokePolygonPreview(cursorCtx, polygonPointsRef.current, ts.polygonClosed ? null : cursor, lineW);
    } else if (cursor) {
      strokeBrushOutline(cursorCtx, cursor.x, cursor.z, ts.shape, ts.brushParams, ts.featherWidth, lineW);
    }
    // Drafted water vertices. Once closed, omit the mouse-following line and show the closed polygon.
    if (ts.waterMode) strokeWaterSurfaces(cursorCtx, [], waterPointsRef.current, ts.waterPolygonClosed ? null : cursor, lineW);
    // Manual course draft vertices and node-editing control-point handles.
    if (ts.riverPointerMode === "draw") {
      strokeRiverDraft(cursorCtx, riverDraftPointsRef.current, cursor, lineW);
    } else if (ts.riverPointerMode === "node") {
      const target = nodeEditTargetRiver();
      if (target?.controlPoints) strokeRiverControlPoints(cursorCtx, target.controlPoints, hoveredRiverNodeRef.current, lineW);
    }
    if (ts.cameraMode) strokeCameraAimLine(cursorCtx, cameraRef.current, cursor, lineW);
    cursorCtx.restore();
  }

  /**
   * Elevation array for display: preview array during carving preview, otherwise actual data.
   * Terrain-layer uploads and 3D preview sends read this; editing operations modifying real
   * data must always use elevRef directly.
   */
  function displayElev(): Float32Array | null {
    return previewElevRef.current ?? elevRef.current;
  }

  /** Re-uploads rect of displayElev to the renderer and redraws; omitting rect updates the whole area. */
  function refreshTerrainLayer(rect?: DirtyRect) {
    const elev = displayElev();
    if (elev) rendererRef.current?.updateElevation(elev, rect);
    draw();
  }

  /**
   * Immediately sends all current elevations, selection masks, and metadata to the attached
   * 3D preview. Cancels pending sends because this satisfies them. Reads only refs, so any
   * closure may call it.
   */
  function emitTerrain3dFullState() {
    if (fullStateTimerRef.current != null) {
      window.clearTimeout(fullStateTimerRef.current);
      fullStateTimerRef.current = null;
    }
    fullStatePendingSinceRef.current = null;
    const s = settingsRef.current;
    const elev = displayElev();
    const mask = selectionMaskRef.current;
    if (previewSessionsRef.current.size === 0 || !s || !elev || !mask) return;
    const fullRect: GridRect = { rMin: 0, rMax: s.rows - 1, cMin: 0, cMax: s.cols - 1 };
    const payload: Terrain3DFullStatePayload = {
      mapId,
      mapWidth: s.mapWidth,
      mapHeight: s.mapHeight,
      spacing: s.spacing,
      rows: s.rows,
      cols: s.cols,
      minLand: s.minLand,
      maxLand: s.maxLand,
      elevBase64: uint8ToBase64(encodeFloat16Array(elev)),
      maskBase64: uint8ToBase64(quantizeMaskRect(mask, s.cols, fullRect)),
      waterSurfaces: serializeWaterSurfacesForPreview(),
      rivers: serializeRiversForDisplay(),
      groundCover: groundCoverRef.current,
    };
    void emit(EVT_TERRAIN_3D_FULL_STATE, payload);
    previewSentVersionRef.current = previewStateVersionRef.current;
  }

  /**
   * Records a change to 3D preview state and schedules a full-state send if a preview is attached
   * (changes without delta events: load, resize, Undo/Redo, water, rivers, ground cover, etc.).
   * Each call postpones the send up to PREVIEW_FULL_STATE_MAX_WAIT_MS from the first change.
   * Build the payload at send time, batching consecutive changes into one send.
   */
  function scheduleTerrain3dFullState() {
    previewStateVersionRef.current++;
    if (previewSessionsRef.current.size === 0) return;
    const now = performance.now();
    const since = fullStatePendingSinceRef.current ?? now;
    fullStatePendingSinceRef.current = since;
    if (fullStateTimerRef.current != null) window.clearTimeout(fullStateTimerRef.current);
    const delay = Math.max(0, Math.min(PREVIEW_FULL_STATE_QUIET_MS, since + PREVIEW_FULL_STATE_MAX_WAIT_MS - now));
    fullStateTimerRef.current = window.setTimeout(() => {
      fullStateTimerRef.current = null;
      emitTerrain3dFullState();
    }, delay);
  }

  /**
   * Sends a delta event to the 3D preview. Advance previewStateVersionRef before calling after
   * a state change. Skip when no preview is attached or a full-state send is pending (it will
   * include this change). Build payload only when sending because encoding is expensive.
   */
  function emitPreviewDelta<T>(event: string, buildPayload: () => T) {
    if (previewSessionsRef.current.size === 0 || fullStateTimerRef.current != null) return;
    void emit(event, buildPayload());
    previewSentVersionRef.current = previewStateVersionRef.current;
  }

  /**
   * Schedules sending selection-mask changes in rect to the 3D preview. Called on every
   * mousemove during brush dragging, but merges calls within a frame into one emit using rAF.
   */
  function schedulePreviewSelectionSync(rect: DirtyRect) {
    previewStateVersionRef.current++;
    const sync = previewSyncRef.current;
    sync.pendingSelectionRect = unionDirtyRect(sync.pendingSelectionRect, rect);
    if (sync.rafId != null) return;
    sync.rafId = requestAnimationFrame(() => {
      sync.rafId = null;
      const pending = sync.pendingSelectionRect;
      sync.pendingSelectionRect = null;
      const s = settingsRef.current;
      const mask = selectionMaskRef.current;
      if (!pending || !s || !mask) return;
      emitPreviewDelta(EVT_TERRAIN_3D_SELECTION_DELTA, (): Terrain3DSelectionDeltaPayload => ({
        mapId,
        ...pending,
        maskRectBase64: uint8ToBase64(quantizeMaskRect(mask, s.cols, pending)),
      }));
    });
  }

  /** Merges the shape at brushWorldPosRef into the selection mask for a single circle/square-tool click. */
  function mergeBrushAtCurrentPos() {
    const s = settingsRef.current;
    const mask = selectionMaskRef.current;
    const pos = brushWorldPosRef.current;
    if (!s || !mask || !pos) return;
    const ts = toolStateRef.current;
    if (ts.shape !== "circle" && ts.shape !== "rect") return;
    const rect = mergeShapeIntoSelection(
      mask,
      s.rows,
      s.cols,
      s.spacing,
      s.mapWidth,
      s.mapHeight,
      pos.x,
      pos.z,
      ts.shape,
      ts.brushParams,
      ts.selectionFalloff,
      ts.featherWidth
    );
    if (!rect) return;
    selectionBoundsRef.current = unionDirtyRect(selectionBoundsRef.current, rect);
    rendererRef.current?.updateSelection(mask, rect);
    setHasSelection(true);
    draw();
    schedulePreviewSelectionSync(rect);
  }

  /** Adds a polygon-tool vertex. */
  function addPolygonPoint(wx: number, wz: number) {
    polygonPointsRef.current = [...polygonPointsRef.current, { x: wx, z: wz }];
    setPolygonPointCount(polygonPointsRef.current.length);
    draw();
  }

  /** Clears all polygon vertices, discarding only the unconfirmed draft without affecting the selection mask. */
  function clearPolygonPoints() {
    polygonPointsRef.current = [];
    setPolygonPointCount(0);
    setPolygonClosed(false);
    draw();
  }

  /** Confirms the drafted polygon, merges its enclosed area into the selection mask, and clears vertices without changing elevations yet. */
  function confirmPolygonSelection() {
    const s = settingsRef.current;
    const mask = selectionMaskRef.current;
    const poly = polygonPointsRef.current;
    if (!s || !mask || poly.length < 3) return;
    const ts = toolStateRef.current;
    const rect = mergePolygonIntoSelection(
      mask,
      s.rows,
      s.cols,
      s.spacing,
      s.mapWidth,
      s.mapHeight,
      poly,
      ts.featherWidth,
      ts.selectionFalloff
    );
    if (rect) {
      selectionBoundsRef.current = unionDirtyRect(selectionBoundsRef.current, rect);
      rendererRef.current?.updateSelection(mask, rect);
      setHasSelection(true);
      schedulePreviewSelectionSync(rect);
    }
    clearPolygonPoints();
    setPolygonClosed(false);
  }

  /* ============================================================================
   * Creating non-sea water surfaces (lakes, etc.)
   * ========================================================================== */

  /**
   * Switches editing tabs. Enables waterMode/cameraMode on their corresponding tabs, and
   * disables them elsewhere, discarding unconfirmed water vertices. River pointer operations
   * follow riverPointerMode (defaults to the same area selection as terrain editing, for rainfall).
   * Leaving the tab resets to rain and discards draft vertices. Uncarved rivers persist across
   * tab changes, preserving progress while carving individual rivers from the list.
   */
  function handleTabChange(tab: EditorTab) {
    if (heightsLockedRef.current && HEIGHT_LOCKED_TABS.has(tab)) return;
    const leavingRiver = activeTabRef.current === "river" && tab !== "river";
    activeTabRef.current = tab;
    setActiveTab(tab);
    // Show carving preview only in the river tab. Other tabs edit actual data (elevRef),
    // so leaving the preview array visible would hide their results.
    if (leavingRiver) {
      clearRiverPreview();
      setRiverPointerModeSafe("rain");
    }
    if (tab === "river") scheduleRiverPreviewRebuild();
    setWaterMode(tab === "water");
    setCameraMode(tab === "camera");
    if (tab !== "water") {
      // Discard unconfirmed draft vertices when disabling water editing mode.
      waterPointsRef.current = [];
      setWaterPointCount(0);
      setWaterPolygonClosed(false);
    }
    draw();
  }

  /** Adds a vertex to the drafted water polygon in water mode, only before the shape closes. */
  function addWaterPoint(wx: number, wz: number) {
    waterPointsRef.current = [...waterPointsRef.current, { x: wx, z: wz }];
    setWaterPointCount(waterPointsRef.current.length);
    draw();
  }

  /** Clears unconfirmed water polygon draft vertices. */
  function clearWaterPoints() {
    waterPointsRef.current = [];
    setWaterPointCount(0);
    setWaterPolygonClosed(false);
    draw();
  }

  /**
   * Registers a closed water polygon (at least three vertices) as a surface at waterLevelInput.
   * Called by "Confirm water surface"; right click only closes the shape and does not execute
   * this step (two stages: close shape → set level → confirm surface). Also updates the 3D preview.
   */
  function confirmWaterSurface() {
    const poly = waterPointsRef.current;
    if (poly.length < 3) return;
    const surface: EditorWaterSurface = {
      id: `water_${Date.now()}_${Math.floor(Math.random() * 1000)}`,
      waterLevel: toolStateRef.current.waterLevelInput,
      points: poly.map((p) => ({ x: p.x, z: p.z })),
    };
    waterSurfacesRef.current = [...waterSurfacesRef.current, surface];
    markDirty();
    setWaterSurfaceCount(waterSurfacesRef.current.length);
    waterPointsRef.current = [];
    setWaterPointCount(0);
    setWaterPolygonClosed(false);
    draw();
    scheduleTerrain3dFullState();
  }

  /** Deletes the most recently created water surface. */
  function removeLastWaterSurface() {
    if (waterSurfacesRef.current.length === 0) return;
    waterSurfacesRef.current = waterSurfacesRef.current.slice(0, -1);
    markDirty();
    setWaterSurfaceCount(waterSurfacesRef.current.length);
    draw();
    scheduleTerrain3dFullState();
  }

  /** Deletes all created water surfaces and discards draft vertices. */
  function clearAllWaterSurfaces() {
    if (waterSurfacesRef.current.length > 0) markDirty();
    waterSurfacesRef.current = [];
    waterPointsRef.current = [];
    setWaterSurfaceCount(0);
    setWaterPointCount(0);
    setWaterPolygonClosed(false);
    draw();
    scheduleTerrain3dFullState();
  }

  /** Converts committed internal water surfaces to GeoJSON-like WaterSurfacePayload for saving/sending. */
  function serializeWaterSurfaces(): WaterSurfacePayload[] {
    return waterSurfacesRef.current.map((ws) => ({
      id: ws.id,
      water_level: ws.waterLevel,
      kind: "lake",
      polygon: {
        type: "Polygon" as const,
        coordinates: [ws.points.map((p) => [p.x, p.z])],
      },
    }));
  }

  /**
   * Water surfaces sent to the 3D preview. Includes committed surfaces plus one provisional
   * surface for the closed but unconfirmed polygon, using waterLevelInput. Changing the level
   * therefore moves the 3D surface even before confirmation. Not used for saving, which must
   * exclude unconfirmed surfaces. Reads toolStateRef rather than state because timers and
   * event closures also call it.
   */
  function serializeWaterSurfacesForPreview(): WaterSurfacePayload[] {
    const list = serializeWaterSurfaces();
    if (toolStateRef.current.waterPolygonClosed && waterPointsRef.current.length >= 3) {
      list.push({
        id: "__water_preview__",
        water_level: toolStateRef.current.waterLevelInput,
        kind: "lake",
        polygon: {
          type: "Polygon" as const,
          coordinates: [waterPointsRef.current.map((p) => [p.x, p.z])],
        },
      });
    }
    return list;
  }

  /** Converts WaterSurfacePayload arrays loaded from the work copy (meta.json) to internal representations. */
  function deserializeWaterSurfaces(payload: WaterSurfacePayload[] | undefined): EditorWaterSurface[] {
    if (!Array.isArray(payload)) return [];
    const result: EditorWaterSurface[] = [];
    for (const ws of payload) {
      const polygon = ws?.polygon;
      if (!polygon || polygon.type !== "Polygon") continue;
      // Polygon coordinates are [exterior, holes...]. The editor handles only the exterior.
      const exterior = (polygon.coordinates as number[][][])[0];
      if (!Array.isArray(exterior) || exterior.length < 3) continue;
      result.push({
        id: ws.id ?? `water_${Date.now()}_${Math.floor(Math.random() * 1000)}`,
        waterLevel: typeof ws.water_level === "number" ? ws.water_level : 0,
        points: exterior.map((c) => ({ x: c[0], z: c[1] })),
      });
    }
    return result;
  }

  /** Replaces the entire water list for Undo/Redo restoration. */
  function restoreWaterSurfaces(surfaces: EditorWaterSurface[]) {
    waterSurfacesRef.current = surfaces;
    setWaterSurfaceCount(surfaces.length);
  }

  /* ============================================================================
   * Initial camera state (camera editing tab).
   *
   * Left click places the camera at (x, z); right click sets a look-at point to determine yaw.
   * Card inputs also change height (y), pitch, and azimuth. One camera state per map, saved
   * to the Scenario and used at its start (thereafter the saved game's viewpoint is authoritative).
   * ========================================================================== */

  /** Replaces cameraRef and mirrors it into display state. All camera changes must go through this. */
  function restoreCamera(camera: TerrainCameraPayload | null) {
    cameraRef.current = camera;
    setCameraState(camera);
  }

  /** Places the camera at world (wx, wz), creating it with default height/orientation if unset. */
  function placeCamera(wx: number, wz: number) {
    const current = cameraRef.current;
    markDirty();
    restoreCamera({
      x: wx,
      y: current?.y ?? CAMERA_DEFAULT_HEIGHT_M,
      z: wz,
      yaw: current?.yaw ?? CAMERA_DEFAULT_YAW_DEG,
      pitch: current?.pitch ?? CAMERA_DEFAULT_PITCH_DEG,
    });
    draw();
  }

  /**
   * Points the camera toward (wx, wz), setting yaw to the bearing from position to look-at point.
   * yaw is clockwise from north (+Z) = 0, so use atan2(dx, dz). Leave orientation unchanged
   * if the point equals the camera position.
   */
  function aimCameraAt(wx: number, wz: number) {
    const current = cameraRef.current;
    if (!current) return;
    const dx = wx - current.x;
    const dz = wz - current.z;
    if (Math.hypot(dx, dz) < 1e-6) return;
    const yaw = ((Math.atan2(dx, dz) * 180) / Math.PI + 360) % 360;
    markDirty();
    restoreCamera({ ...current, yaw });
    draw();
  }

  /** Updates camera height, pitch, or azimuth from numeric input. */
  function updateCameraField(field: "y" | "yaw" | "pitch", value: number) {
    const current = cameraRef.current;
    if (!current) return;
    markDirty();
    restoreCamera({ ...current, [field]: value });
    draw();
  }

  /** Unsets initial camera state, so the game starts from the default viewpoint at the origin. */
  function clearCamera() {
    if (cameraRef.current) markDirty();
    restoreCamera(null);
    draw();
  }

  /** Validates and restores camera from work-copy meta.json; treats incomplete numeric fields as unset. */
  function deserializeCamera(payload: TerrainCameraPayload | null | undefined): TerrainCameraPayload | null {
    if (!payload || typeof payload !== "object") return null;
    const fields = ["x", "y", "z", "yaw", "pitch"] as const;
    for (const key of fields) {
      if (typeof payload[key] !== "number" || !Number.isFinite(payload[key])) return null;
    }
    return { x: payload.x, y: payload.y, z: payload.z, yaw: payload.yaw, pitch: payload.pitch };
  }

  /* ============================================================================
   * River generation (river editing tab).
   *
   * Two-stage workflow: "Calculate courses" (preview, unchanged elevations) → "Carve riverbeds
   * and confirm" (elevation changes + Undo). Algorithms live in terrainRiver.ts; this file only
   * handles parameter UI and subsequent redraws. Reuses the terrain selection mask for rainfall
   * (rain only within a selection, as in erosion's buildErosionRegion).
   * ========================================================================== */

  /** Calculates a river's total length (km). */
  function riverLengthKm(river: GeneratedRiver): number {
    let len = 0;
    for (let i = 1; i < river.points.length; i++) {
      len += Math.hypot(
        river.points[i].x - river.points[i - 1].x,
        river.points[i].z - river.points[i - 1].z,
      );
    }
    return len / 1000;
  }

  /** Mirrors riversRef into list display state (riverList). Must be called after every river-list change. */
  function syncRiverListState() {
    setRiverList(
      riversRef.current.map((river) => {
        let minWidth = Infinity;
        let maxWidth = 0;
        for (const p of river.points) {
          if (p.width < minWidth) minWidth = p.width;
          if (p.width > maxWidth) maxWidth = p.width;
        }
        return {
          id: river.id,
          lengthKm: riverLengthKm(river),
          minWidth: isFinite(minWidth) ? minWidth : 0,
          maxWidth,
          carved: river.carved,
          depthCoef: river.depthCoef,
          areaHa: river.carved && river.polygon ? polygonArea(river.polygon) / 10000 : null,
          editable: !river.carved && river.controlPoints !== null,
          nodeCount: river.controlPoints?.length ?? 0,
          manualWidths: river.manualWidths,
          mouth: river.mouth,
        };
      }),
    );
  }

  /**
   * Re-extracts all carved rivers' area polygons from current terrain.
   *
   * Process rivers in descending discharge (mouth q), excluding grid points claimed by earlier
   * rivers to avoid overlapping main-stem/tributary areas at confluences. Restored rivers have
   * q=0, so follow generated rivers in list order.
   *
   * For rivers with course splines (generated/carved this session, width>0), the search band is
   * centerline ± (half-width + half grid diagonal + blending band + one grid cell). Restored
   * rivers are limited to their existing area. Re-extract submerged portions within these
   * bounds without expanding into nearby low slopes. Called on each carve and by
   * "Recalculate river areas".
   */
  function recomputeRiverPolygons(s: TerrainSettings, elev: Float32Array) {
    const claimed = new Uint8Array(s.rows * s.cols);
    const lakes = serializeWaterSurfaces();
    const order = riversRef.current
      .map((river, index) => ({ river, index }))
      .filter((e) => e.river.carved)
      .sort((a, b) => (b.river.points[b.river.points.length - 1]?.q ?? 0) - (a.river.points[a.river.points.length - 1]?.q ?? 0));
    // Undo snapshots reference the same EditorRiver objects, so replace area data with new objects
    // instead of modifying it in place.
    const next = riversRef.current.slice();
    for (const { river, index } of order) {
      const hasSpline = river.points.length > 1 && river.controlPoints !== null;
      const band = hasSpline
        ? ({ kind: "spline" } as const)
        : river.polygon
          ? ({ kind: "polygon", polygon: river.polygon } as const)
          : null;
      if (!band) continue;
      const polygon = buildRiverPolygon(
        elev, s.rows, s.cols, s.spacing, s.mapWidth, s.mapHeight, river, band, claimed, lakes,
      );
      next[index] = { ...river, polygon };
    }
    riversRef.current = next;
    syncRiverListState();
  }

  /** "Recalculate river areas": re-extracts polygons on current terrain and updates 2D/3D previews. */
  function handleRecomputeRiverPolygons() {
    const s = settingsRef.current;
    const elev = elevRef.current;
    if (!s || !elev) return;
    pushUndoState();
    recomputeRiverPolygons(s, elev);
    draw();
    scheduleTerrain3dFullState();
  }

  /* ============================================================================
   * Carving preview.
   *
   * Shows uncarved rivers as terrain carved with the current depth coefficient without changing
   * actual data. Rebuilds on every depth-stepper change; "Confirm and carve" applies it to real
   * data when satisfactory. Disabling water display hides polygon fills and 3D water, exposing
   * only riverbed terrain.
   * ========================================================================== */

  /** Updates terrain and 3D previews using the displayed elevations (actual or preview). */
  function refreshDisplayedTerrain() {
    refreshTerrainLayer();
    scheduleTerrain3dFullState();
  }

  /**
   * Replaces ground cover type. repaint indicates a user change: redraw 2D, resend to 3D
   * preview while preserving selection/view, and record as unsaved. Terrain colors use a ramp
   * passed at draw time, so elevations do not need uploading again.
   */
  function applyGroundCover(value: unknown, repaint: boolean) {
    const next = normalizeGroundCover(value);
    groundCoverRef.current = next;
    setGroundCover(next);
    if (repaint) {
      markDirty();
      draw();
      scheduleTerrain3dFullState();
    }
  }

  /**
   * Rebuilds carving preview; clears it when there are no uncarved rivers, preview is off, or
   * outside the river tab. Carves uncarved rivers into a copy of real data at their own depth
   * coefficients, then extracts areas. Carved rivers claim areas first using their existing
   * polygons as seeds, excluding overlap at confluences.
   */
  function rebuildRiverPreview() {
    const s = settingsRef.current;
    const elev = elevRef.current;
    if (!s || !elev) return;
    const uncarved = riversRef.current.filter((river) => !river.carved);
    if (!riverPreviewEnabledRef.current || activeTabRef.current !== "river" || uncarved.length === 0) {
      previewElevRef.current = null;
      previewPolygonsRef.current = new Map();
      // Restore actual-data display. This also runs just after committed carving changes real data,
      // so re-upload all elevations to the renderer whether a preview exists or not.
      refreshDisplayedTerrain();
      return;
    }
    const preview = new Float32Array(elev);
    for (const river of uncarved) applyRiverDepth(river, river.depthCoef);
    carveRivers(preview, s.rows, s.cols, s.spacing, s.mapWidth, s.mapHeight, uncarved);

    const claimed = new Uint8Array(s.rows * s.cols);
    const lakes = serializeWaterSurfaces();
    const ordered = riversRef.current
      .slice()
      .sort((a, b) => (b.points[b.points.length - 1]?.q ?? 0) - (a.points[a.points.length - 1]?.q ?? 0));
    const polygons = new Map<string, WaterPolygonGeoJson | null>();
    for (const river of ordered) {
      if (river.carved) {
        if (river.polygon) {
          buildRiverPolygon(preview, s.rows, s.cols, s.spacing, s.mapWidth, s.mapHeight, river,
            { kind: "polygon", polygon: river.polygon }, claimed, lakes);
        }
        continue;
      }
      polygons.set(river.id, buildRiverPolygon(
        preview, s.rows, s.cols, s.spacing, s.mapWidth, s.mapHeight, river, { kind: "spline" }, claimed, lakes,
      ));
    }
    previewElevRef.current = preview;
    previewPolygonsRef.current = polygons;
    refreshDisplayedTerrain();
  }

  /** Debounces preview rebuild scheduling, avoiding full-area recarving on every repeated stepper click. */
  function scheduleRiverPreviewRebuild() {
    if (previewRebuildTimerRef.current != null) window.clearTimeout(previewRebuildTimerRef.current);
    previewRebuildTimerRef.current = window.setTimeout(() => {
      previewRebuildTimerRef.current = null;
      rebuildRiverPreview();
    }, 150);
  }

  /** Clears carving preview and restores actual-data display. */
  function clearRiverPreview() {
    if (previewRebuildTimerRef.current != null) {
      window.clearTimeout(previewRebuildTimerRef.current);
      previewRebuildTimerRef.current = null;
    }
    if (previewElevRef.current === null) return;
    previewElevRef.current = null;
    previewPolygonsRef.current = new Map();
    refreshDisplayedTerrain();
  }

  /**
   * Rivers used in 2D/3D previews. Unlike serializeRivers for saving, returns empty when water
   * display is OFF; when ON, includes preview areas for uncarved rivers alongside carved rivers.
   */
  function serializeRiversForDisplay(): RiverPayload[] {
    if (!riverWaterVisibleRef.current) return [];
    const result = serializeRivers();
    for (const river of riversRef.current) {
      if (river.carved) continue;
      const polygon = previewPolygonsRef.current.get(river.id);
      if (!polygon) continue;
      result.push({
        id: river.id,
        kind: "river",
        polygon,
        profile: river.points.map((p) => [p.x, p.waterY, p.z]),
      });
    }
    return result;
  }

  function handleRiverPreviewToggle(enabled: boolean) {
    riverPreviewEnabledRef.current = enabled;
    setRiverPreviewEnabled(enabled);
    rebuildRiverPreview();
  }

  function handleRiverWaterVisibleToggle(visible: boolean) {
    riverWaterVisibleRef.current = visible;
    setRiverWaterVisible(visible);
    draw();
    scheduleTerrain3dFullState();
  }

  /** River list row click: selects/highlights a river on the 2D canvas; clicking the same row again clears selection. */
  function selectRiver(id: string) {
    const next = selectedRiverIdRef.current === id ? null : id;
    selectedRiverIdRef.current = next;
    setSelectedRiverId(next);
    draw();
  }

  /** Replaces the entire river list for Undo/Redo restoration. */
  function restoreRivers(rivers: EditorRiver[]) {
    riversRef.current = rivers;
    syncRiverListState();
  }

  /**
   * "Calculate courses": extracts a river network from current terrain/parameters, applies
   * meander evolution, and adds courses as uncarved rivers. Never changes elevations, so
   * rainfall, thresholds, and rainfall regions can be adjusted and recalculated repeatedly.
   * Recalculation replaces only uncarved rivers, retaining carved ones. Carve rivers individually
   * with their list's "Carve" button.
   */
  function runRiverCompute() {
    const s = settingsRef.current;
    const elev = elevRef.current;
    if (!s || !elev) return;
    const { rivers, maxDischargeM3s, discardedShortCount } = extractRiverNetwork(
      elev,
      s.rows,
      s.cols,
      s.spacing,
      s.mapWidth,
      s.mapHeight,
      riverParams,
      serializeWaterSurfaces(),
      buildErosionRegion(),
      `river_${Date.now()}`,
    );
    evolveRivers(rivers, elev, s.rows, s.cols, s.spacing, s.mapWidth, s.mapHeight, riverParams);
    riversRef.current = [
      ...riversRef.current.filter((river) => river.carved),
      ...rivers.map((river) => ({
        ...river,
        params: riverParams,
        carved: false,
        depthCoef: riverParams.depthCoef,
        polygon: null,
        // Add simplified centerline control points for node editing; points remain the meander evolution result until the first node move.
        controlPoints: simplifyCenterlineToControlPoints(river.points, s.spacing),
        manualWidths: null,
      })),
    ];
    setRiverPreviewStats({
      count: rivers.length,
      totalKm: rivers.reduce((sum, r) => sum + riverLengthKm(r), 0),
      maxDischargeM3s,
      discardedShortCount,
    });
    syncRiverListState();
    draw();
    // Show new uncarved rivers as a carving preview using the current depth coefficient.
    rebuildRiverPreview();
  }

  /**
   * Shared post-carving work: extract areas for all carved rivers, upload all elevations to
   * the terrain layer, and send full state to the 3D preview. Multiple rivers can span the map,
   * so upload the entire area as in erosion's finishErosion. Send full_state instead of an
   * elevation delta because the river list also changes.
   */
  function finishRiverCarve(s: TerrainSettings, elev: Float32Array) {
    recomputeRiverPolygons(s, elev);
    // Rebuild remaining uncarved river previews on new real data, or return to real-data display if none.
    // Both paths update the terrain layer (all elevations) and 3D preview.
    rebuildRiverPreview();
  }

  /**
   * Carves uncarved rivers into elevation data and replaces them with new carved=true objects.
   * Undo snapshots reference the same objects, so do not modify carved in place; otherwise
   * Undo restores terrain while leaving rivers marked as carved.
   */
  function carveRiverObjects(s: TerrainSettings, elev: Float32Array, targets: EditorRiver[]) {
    for (const river of targets) {
      // Apply each river's depth coefficient to bedY before carving.
      applyRiverDepth(river, river.depthCoef);
    }
    carveRivers(elev, s.rows, s.cols, s.spacing, s.mapWidth, s.mapHeight, targets);
    const ids = new Set(targets.map((river) => river.id));
    riversRef.current = riversRef.current.map((river) =>
      ids.has(river.id) ? { ...river, carved: true } : river,
    );
  }

  /**
   * List "Carve" button: carves one uncarved riverbed into elevations using its depth coefficient.
   * Undo (Ctrl+Z) restores both terrain and the river list.
   */
  function carveOneRiver(id: string) {
    const s = settingsRef.current;
    const elev = elevRef.current;
    const river = riversRef.current.find((r) => r.id === id);
    if (!s || !elev || !river || river.carved) return;
    pushUndoState();
    carveRiverObjects(s, elev, [river]);
    finishRiverCarve(s, elev);
  }

  /** "Carve all uncarved rivers": carves them together as one Undo step. */
  function carveAllUncarvedRivers() {
    const s = settingsRef.current;
    const elev = elevRef.current;
    const targets = riversRef.current.filter((river) => !river.carved);
    if (!s || !elev || targets.length === 0) return;
    pushUndoState();
    carveRiverObjects(s, elev, targets);
    finishRiverCarve(s, elev);
  }

  /** List depth stepper: changes one uncarved river's depth coefficient, applied when carving. */
  function setRiverDepthCoef(id: string, depthCoef: number) {
    const river = riversRef.current.find((r) => r.id === id);
    if (!river || river.carved) return;
    river.depthCoef = depthCoef;
    syncRiverListState();
    // Rebuild carving preview after depth changes, debouncing repeated clicks.
    scheduleRiverPreviewRebuild();
  }

  /**
   * Deletes one river. For uncarved rivers, simply discards the calculation result. For carved
   * rivers, deletes only data (spline, water ribbon, saved definition), without restoring terrain.
   * Use Ctrl+Z to restore terrain too.
   */
  function removeRiver(id: string) {
    if (riversRef.current.some((river) => river.id === id && river.carved)) markDirty();
    riversRef.current = riversRef.current.filter((river) => river.id !== id);
    syncRiverListState();
    // Remove the deleted river's preview carving by rebuilding from remaining rivers.
    rebuildRiverPreview();
  }

  /** Deletes all rivers without restoring terrain. */
  function clearAllRivers() {
    if (riversRef.current.some((river) => river.carved)) markDirty();
    riversRef.current = [];
    syncRiverListState();
    // Remove the deleted river's preview carving by rebuilding from remaining rivers.
    rebuildRiverPreview();
  }

  /**
   * Converts internal rivers to RiverPayload for saving/sending, including only carved rivers
   * with extracted areas. Excludes empty areas, for example when carving left no grid points
   * below water level, because they are meaningless to the game.
   */
  function serializeRivers(): RiverPayload[] {
    const result: RiverPayload[] = [];
    for (const river of riversRef.current) {
      if (!river.carved || !river.polygon) continue;
      result.push({
        id: river.id,
        kind: "river",
        polygon: river.polygon,
        profile: river.points.map((p) => [p.x, p.waterY, p.z]),
      });
    }
    return result;
  }

  /**
   * Converts RiverPayload arrays loaded from work-copy meta.json into internal representations.
   * Generation intermediates (discharge, curvature, width, riverbed) were not saved, so fill
   * with 0 (riverbed uses water elevation). Restored rivers are not recarved; they support only
   * display, deletion, area recalculation, and resaving. Area recalculation uses an expanded
   * existing-polygon band rather than width.
   */
  function deserializeRivers(payload: RiverPayload[] | undefined): EditorRiver[] {
    if (!Array.isArray(payload)) return [];
    const result: EditorRiver[] = [];
    for (const rv of payload) {
      if (!Array.isArray(rv?.profile) || rv.profile.length < 2 || !rv.polygon) continue;
      const points = rv.profile.map((c) => ({
        x: c[0],
        z: c[2],
        q: 0,
        width: 0,
        waterY: c[1],
        bedY: c[1],
        curvature: 0,
        braided: false,
      }));
      result.push({
        id: rv.id ?? `river_${Date.now()}_${Math.floor(Math.random() * 1000)}`,
        points,
        mouth: "edge",
        params: riverParams,
        // Only carved rivers are saved, because serializeRivers writes only those.
        carved: true,
        depthCoef: riverParams.depthCoef,
        polygon: rv.polygon,
        controlPoints: null,
        manualWidths: null,
      });
    }
    return result;
  }

  /* ============================================================================
   * Manual course drawing and node editing.
   *
   * River tab pointer actions switch with riverPointerMode (see RiverPointerMode). The course
   * is defined by EditorRiver.controlPoints. On each node move, buildRiverFromControlPoints
   * in terrainRiver.ts rebuilds the centerline, mouth, and water profile without changing elevations.
   * Carving still uses "Confirm and carve" in the list. Moving a main stem snaps tributary
   * endpoints onto its new centerline and recursively rebuilds them to preserve confluences.
   * ========================================================================== */

  /**
   * Switches pointer operation. Leaving draw discards draft vertices and clears drag/hover
   * state. Pointer handlers read toolStateRef, so update the ref directly before redrawing,
   * without waiting for state to propagate in the next render.
   */
  function setRiverPointerModeSafe(mode: RiverPointerMode) {
    if (mode !== "draw") {
      riverDraftPointsRef.current = [];
      setRiverDraftPointCount(0);
    }
    hoveredRiverNodeRef.current = -1;
    riverNodeDragRef.current = null;
    if (canvasRef.current) canvasRef.current.style.cursor = "";
    toolStateRef.current = { ...toolStateRef.current, riverPointerMode: mode };
    setRiverPointerMode(mode);
    draw();
  }

  /** Node-editing target: the selected uncarved river with control points, or null. */
  function nodeEditTargetRiver(): EditorRiver | null {
    const id = selectedRiverIdRef.current;
    if (!id) return null;
    const river = riversRef.current.find((r) => r.id === id);
    if (!river || river.carved || !river.controlPoints) return null;
    return river;
  }

  /** Node/course picking tolerance in world meters, converted by zoom to remain constant in screen pixels. */
  function riverHitToleranceWorld(): number {
    return RIVER_NODE_HIT_PX / viewRef.current.zoom;
  }

  /** Course (centerline) picking tolerance: at least a minimum screen-pixel count for thin rivers, up to half-width for wide ones. */
  function riverLineToleranceWorld(river: EditorRiver, wx: number, wz: number): number {
    const near = nearestOnPolyline(river.points, wx, wz);
    const width = near ? river.points[near.index].width : 0;
    return Math.max(riverHitToleranceWorld(), width / 2);
  }

  /**
   * Assigns discharge to each control point of a manually drawn river. Linearly interpolates
   * upstream → downstream width by arc length, then inverts hydraulic geometry with
   * dischargeFromWidth to obtain discharge.
   */
  function assignManualDischarge(
    cps: RiverControlPoint[],
    widths: { head: number; tail: number },
    params: RiverGenParams,
  ): RiverControlPoint[] {
    const n = cps.length;
    if (n === 0) return cps;
    const arc = new Float64Array(n);
    for (let i = 1; i < n; i++) arc[i] = arc[i - 1] + Math.hypot(cps[i].x - cps[i - 1].x, cps[i].z - cps[i - 1].z);
    const total = arc[n - 1];
    return cps.map((p, i) => {
      const f = total > 1e-9 ? arc[i] / total : i / Math.max(1, n - 1);
      return { ...p, q: dischargeFromWidth(widths.head + (widths.tail - widths.head) * f, params) };
    });
  }

  /**
   * Rebuilds a river from control points and replaces riversRef. Recursively rebuilds uncarved
   * tributaries flowing into it, snapping endpoints to the nearest points on the new centerline
   * while preserving mouth types. Updates the river list display; the caller handles redraws
   * and preview updates.
   */
  function applyRiverControlPoints(id: string, cps: RiverControlPoint[], keepMouth: boolean) {
    const s = settingsRef.current;
    const elev = elevRef.current;
    if (!s || !elev) return;
    const lakes = serializeWaterSurfaces();
    const rebuiltIds = new Set<string>();
    const queue: { id: string; cps: RiverControlPoint[]; keepMouth: boolean }[] = [{ id, cps, keepMouth }];
    while (queue.length > 0) {
      const job = queue.shift()!;
      if (rebuiltIds.has(job.id)) continue;
      const idx = riversRef.current.findIndex((r) => r.id === job.id);
      if (idx < 0) continue;
      const river = riversRef.current[idx];
      const cpsIn = river.manualWidths ? assignManualDischarge(job.cps, river.manualWidths, river.params) : job.cps;
      const others = riversRef.current.filter((r) => r.id !== job.id);
      const result = buildRiverFromControlPoints(
        river, cpsIn, elev, s.rows, s.cols, s.spacing, s.mapWidth, s.mapHeight, river.params, others, lakes, job.keepMouth,
      );
      const next = riversRef.current.slice();
      next[idx] = { ...river, ...result.river, controlPoints: result.controlPoints };
      riversRef.current = next;
      rebuiltIds.add(job.id);
      const rebuilt = next[idx];
      for (const trib of riversRef.current) {
        if (trib.carved || !trib.controlPoints || trib.id === job.id || rebuiltIds.has(trib.id)) continue;
        if (trib.mouth !== "confluence" || trib.confluence?.riverId !== job.id) continue;
        const tribCps = trib.controlPoints.slice();
        const tail = tribCps[tribCps.length - 1];
        const np = rebuilt.points[nearestPointIndex(rebuilt.points, tail)];
        tribCps[tribCps.length - 1] = { ...tail, x: np.x, z: np.z };
        queue.push({ id: trib.id, cps: tribCps, keepMouth: true });
      }
    }
    syncRiverListState();
  }

  /** Moves an uncarved course toward low terrain within the specified cross-sectional band, preserving endpoints and connections. */
  function fitRiverRoute(id: string) {
    const s = settingsRef.current, elev = elevRef.current;
    const river = riversRef.current.find((r) => r.id === id);
    if (!s || !elev || !river || river.carved || !river.controlPoints) return;
    const cps = fitRiverRouteToValley(river, elev, s.rows, s.cols, s.spacing, s.mapWidth, s.mapHeight, riverRouteRadius);
    pushUndoState();
    applyRiverControlPoints(id, cps, true);
    draw();
    rebuildRiverPreview();
  }

  /** Adds a vertex to the drafted course in drawing mode, in upstream-to-downstream order. */
  function addRiverDraftPoint(wx: number, wz: number) {
    riverDraftPointsRef.current = [...riverDraftPointsRef.current, { x: wx, z: wz }];
    setRiverDraftPointCount(riverDraftPointsRef.current.length);
    draw();
  }

  /** Discards all drafted course vertices. */
  function clearRiverDraft() {
    riverDraftPointsRef.current = [];
    setRiverDraftPointCount(0);
    draw();
  }

  /**
   * Registers drafted vertices (at least two) as one uncarved river. Vertices become control
   * points; infer discharge from upstream/downstream widths. Determine mouth type (sea,
   * lake, confluence, unconnected) from endpoint position, then select the river for immediate
   * node editing and clear the draft.
   */
  function finishRiverDraft() {
    const pts = riverDraftPointsRef.current;
    const s = settingsRef.current;
    const elev = elevRef.current;
    if (pts.length < 2 || !s || !elev) return;
    const ts = toolStateRef.current;
    pushUndoState();
    const id = `river_manual_${Date.now()}`;
    const river: EditorRiver = {
      id,
      points: [],
      mouth: "edge",
      params: ts.riverParams,
      carved: false,
      depthCoef: ts.riverParams.depthCoef,
      polygon: null,
      controlPoints: pts.map((p) => ({ x: p.x, z: p.z, q: 0 })),
      manualWidths: { head: ts.riverManualWidthHead, tail: ts.riverManualWidthTail },
    };
    riversRef.current = [...riversRef.current, river];
    applyRiverControlPoints(id, river.controlPoints!, false);
    riverDraftPointsRef.current = [];
    setRiverDraftPointCount(0);
    selectedRiverIdRef.current = id;
    setSelectedRiverId(id);
    draw();
    rebuildRiverPreview();
  }

  /** Moves a node during dragging. Re-evaluates the mouth when the endpoint moves. */
  function moveRiverNode(river: EditorRiver, index: number, wx: number, wz: number) {
    if (!river.controlPoints) return;
    const cps = river.controlPoints.slice();
    cps[index] = { ...cps[index], x: wx, z: wz };
    applyRiverControlPoints(river.id, cps, index !== cps.length - 1);
  }

  /** Inserts a control point at a clicked course position, interpolating discharge between adjacent control points by segment parameter. */
  function insertRiverNode(river: EditorRiver, segIndex: number, t: number, wx: number, wz: number) {
    const cps = river.controlPoints;
    if (!cps || segIndex < 0 || segIndex >= cps.length - 1) return;
    const a = cps[segIndex];
    const b = cps[segIndex + 1];
    const inserted: RiverControlPoint = { x: wx, z: wz, q: a.q + (b.q - a.q) * t };
    pushUndoState();
    applyRiverControlPoints(river.id, [...cps.slice(0, segIndex + 1), inserted, ...cps.slice(segIndex + 1)], true);
    draw();
    rebuildRiverPreview();
  }

  /** Deletes a control point while retaining at least two. If the endpoint is removed, re-evaluates the mouth at the new endpoint. */
  function deleteRiverNode(river: EditorRiver, index: number) {
    const cps = river.controlPoints;
    if (!cps || cps.length <= 2 || index < 0 || index >= cps.length) return;
    pushUndoState();
    hoveredRiverNodeRef.current = -1;
    applyRiverControlPoints(river.id, cps.filter((_, i) => i !== index), index !== cps.length - 1);
    draw();
    rebuildRiverPreview();
  }

  /** List width stepper: changes a manually drawn river's upstream/downstream width and rebuilds its course. */
  function setManualRiverWidth(id: string, which: "head" | "tail", value: number) {
    const river = riversRef.current.find((r) => r.id === id);
    if (!river || river.carved || !river.controlPoints || !river.manualWidths) return;
    riversRef.current = riversRef.current.map((r) =>
      r.id === id && r.manualWidths ? { ...r, manualWidths: { ...r.manualWidths, [which]: value } } : r,
    );
    applyRiverControlPoints(id, river.controlPoints, true);
    draw();
    scheduleRiverPreviewRebuild();
  }

  /** In node editing, returns the selected river's node index under the cursor, if any. */
  function hitSelectedRiverNode(wx: number, wz: number): { river: EditorRiver; index: number } | null {
    const target = nodeEditTargetRiver();
    if (!target?.controlPoints) return null;
    const index = findRiverNodeAt(target.controlPoints, wx, wz, riverHitToleranceWorld());
    return index >= 0 ? { river: target, index } : null;
  }

  /**
   * Left click away from a node in node editing: insert a control point if on the selected
   * river's course, or select another uncarved river if on its course.
   */
  function handleRiverNodeClick(wx: number, wz: number) {
    const target = nodeEditTargetRiver();
    if (target?.controlPoints) {
      const near = nearestOnPolyline(target.points, wx, wz);
      if (near && near.dist <= riverLineToleranceWorld(target, wx, wz)) {
        const seg = nearestOnPolyline(target.controlPoints, wx, wz);
        if (seg) insertRiverNode(target, seg.index, seg.t, wx, wz);
        return;
      }
    }
    let best: { river: EditorRiver; dist: number } | null = null;
    for (const river of riversRef.current) {
      if (river.carved || !river.controlPoints || river.id === target?.id) continue;
      const near = nearestOnPolyline(river.points, wx, wz);
      if (!near || near.dist > riverLineToleranceWorld(river, wx, wz)) continue;
      if (!best || near.dist < best.dist) best = { river, dist: near.dist };
    }
    if (best) {
      selectedRiverIdRef.current = best.river.id;
      setSelectedRiverId(best.river.id);
      hoveredRiverNodeRef.current = -1;
      draw();
    }
  }

  /** Node-editing mouse movement: updates handle highlights and cursor shape. */
  function updateRiverNodeHover(wx: number, wz: number) {
    const hit = hitSelectedRiverNode(wx, wz);
    const index = hit ? hit.index : -1;
    if (canvasRef.current) canvasRef.current.style.cursor = index >= 0 ? "grab" : "";
    hoveredRiverNodeRef.current = index;
  }

  /** Builds provisional selection weights including feather width for a closed polygon and uploads them to the renderer. */
  function refreshPolygonPreviewTexture() {
    const s = settingsRef.current;
    const poly = polygonPointsRef.current;
    const weights = polygonPreviewWeightsRef.current;
    if (!s || !weights) return;

    weights.fill(0);

    if (polygonClosed && poly.length >= 3) {
      const halfW = s.mapWidth / 2;
      const halfH = s.mapHeight / 2;
      const spacing = s.spacing;
      const cols = s.cols;
      const rows = s.rows;
      const feather = toolStateRef.current.featherWidth;
      const falloffKind = toolStateRef.current.selectionFalloff;

      let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;
      for (const p of poly) {
        if (p.x < minX) minX = p.x;
        if (p.x > maxX) maxX = p.x;
        if (p.z < minZ) minZ = p.z;
        if (p.z > maxZ) maxZ = p.z;
      }
      const margin = Math.max(0, feather);
      const cMin = Math.max(0, Math.floor((minX - margin + halfW) / spacing));
      const cMax = Math.min(cols - 1, Math.ceil((maxX + margin + halfW) / spacing));
      const rMin = Math.max(0, Math.floor((minZ - margin + halfH) / spacing));
      const rMax = Math.min(rows - 1, Math.ceil((maxZ + margin + halfH) / spacing));

      if (cMin <= cMax && rMin <= rMax) {
        for (let r = rMin; r <= rMax; r++) {
          const wz = r * spacing - halfH;
          const rowBase = r * cols;
          for (let c = cMin; c <= cMax; c++) {
            const wx = c * spacing - halfW;
            const inside = pointInPolygon(wx, wz, poly);
            let weight: number;
            if (inside) {
              weight = 1;
            } else if (margin > 0) {
              const dist = distToPolygonBoundary(wx, wz, poly);
              if (dist >= margin) continue;
              weight = falloffWeight(dist / margin, falloffKind);
            } else {
              continue;
            }
            if (weight <= 0) continue;
            weights[rowBase + c] = Math.round(weight * 255);
          }
        }
      }
    }
    rendererRef.current?.updatePolygonPreview(weights);
    draw();
  }

  // Watch polygonClosed, featherWidth, and selectionFalloff to update the polygon preview.
  // While open, rendering hides it (showPolygonPreview), so rebuild weights on the next closure.
  useEffect(() => {
    if (polygonClosed) refreshPolygonPreviewTexture();
    else draw();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [polygonClosed, featherWidth, selectionFalloff]);

  /** Completely clears the selection mask merged from brushes/polygons. */
  function resetSelection() {
    const mask = selectionMaskRef.current;
    if (mask) mask.fill(0);
    selectionBoundsRef.current = null;
    rendererRef.current?.clearSelection();
    clearPolygonPoints();
    setHasSelection(false);
    draw();
    previewStateVersionRef.current++;
    emitPreviewDelta(EVT_TERRAIN_3D_SELECTION_RESET, (): Terrain3DSelectionResetPayload => ({ mapId }));
  }

  /** Applies the currently selected editing method once to the current selection when "Apply" is pressed. */
  function applyToSelection() {
    const s = settingsRef.current;
    const elev = elevRef.current;
    const mask = selectionMaskRef.current;
    const bounds = selectionBoundsRef.current;
    if (!s || !elev || !mask || !bounds) return;
    pushUndoState();
    const ts = toolStateRef.current;
    applySelectionOperation(
      elev,
      mask,
      bounds,
      s.cols,
      s.spacing,
      s.mapWidth,
      s.mapHeight,
      ts.operation,
      ts.raiseLowerParams,
      ts.flattenParams,
      ts.noiseParams
    );
    refreshTerrainLayer(bounds);
    previewStateVersionRef.current++;
    emitPreviewDelta(EVT_TERRAIN_3D_ELEVATION_DELTA, (): Terrain3DElevationDeltaPayload => ({
      mapId,
      ...bounds,
      elevRectBase64: uint8ToBase64(encodeFloat16Array(extractElevRect(elev, s.cols, bounds))),
    }));
  }

  /**
   * Converts the current selection, if any, to an erosion region. Returns undefined without
   * selection, allowing the caller to use the entire map. Passes selectionMask and bounds unchanged.
   */
  function buildErosionRegion(): ErosionRegion | undefined {
    const bounds = selectionBoundsRef.current;
    const mask = selectionMaskRef.current;
    if (!bounds || !mask) return undefined;
    return { mask, rMin: bounds.rMin, rMax: bounds.rMax, cMin: bounds.cMin, cMax: bounds.cMax };
  }

  /**
   * Shared post-erosion work. Sediment/raindrops can cross selection boundaries, so even when
   * starting within a selection, changes do not stop at its rectangle. Re-upload all elevations
   * to the renderer and send a full-area elevation delta to the 3D preview regardless of selection.
   */
  function finishErosion(s: TerrainSettings, elev: Float32Array) {
    const fullRect: DirtyRect = { rMin: 0, rMax: s.rows - 1, cMin: 0, cMax: s.cols - 1 };
    refreshTerrainLayer();
    previewStateVersionRef.current++;
    emitPreviewDelta(EVT_TERRAIN_3D_ELEVATION_DELTA, (): Terrain3DElevationDeltaPayload => ({
      mapId,
      ...fullRect,
      elevRectBase64: uint8ToBase64(encodeFloat16Array(extractElevRect(elev, s.cols, fullRect))),
    }));
  }

  /**
   * "Thermal erosion": synchronously simulates collapse of cliffs/slopes exceeding the angle
   * of repose, starting from the selection when present, otherwise across the map.
   */
  function runThermalErosion() {
    const s = settingsRef.current;
    const elev = elevRef.current;
    if (!s || !elev) return;
    pushUndoState();
    applyThermalErosion(elev, s.rows, s.cols, s.spacing, thermalParams, buildErosionRegion());
    finishErosion(s, elev);
  }

  /**
   * "Hydraulic erosion": synchronously simulates raindrop-particle erosion. Rain falls in the
   * selection when present (particle count scaled automatically by selected area), otherwise
   * across the entire map.
   */
  function runHydraulicErosion() {
    const s = settingsRef.current;
    const elev = elevRef.current;
    if (!s || !elev) return;
    pushUndoState();
    applyHydraulicErosion(elev, s.rows, s.cols, s.spacing, hydraulicParams, buildErosionRegion());
    finishErosion(s, elev);
  }

  /**
   * "Generate sea floor": treats cells at/below the threshold as sea and deepens them according
   * to a coastline-distance depth profile. Generates a natural sea floor continuous with land
   * after importing uniformly 0 m sea data such as GSI DEM. With a selection, modifies only
   * selected cells, protecting 0 m areas such as lakes that should not become sea.
   */
  function runSeaFloorGeneration() {
    const s = settingsRef.current;
    const elev = elevRef.current;
    if (!s || !elev) return;
    pushUndoState();
    const modified = applySeaFloor(elev, s.rows, s.cols, s.spacing, seaFloorParams, buildErosionRegion());
    setSeaFloorModifiedCount(modified);
    finishErosion(s, elev);
  }

  /** Returns the terrain renderer, creating it if absent. Throws Error if WebGL2 is unavailable. */
  function ensureRenderer(): TerrainEditorRenderer {
    if (rendererRef.current) return rendererRef.current;
    const canvas = terrainGlCanvasRef.current;
    const renderer = canvas ? TerrainEditorRenderer.create(canvas, draw) : null;
    if (!renderer) throw new Error(t("terrainEditor.webglUnavailable"));
    rendererRef.current = renderer;
    return renderer;
  }

  /**
   * Rebuilds terrain-layer elevations/selection, the selection mask, and settingsRef together
   * from an elevation array and new map dimensions. Shared by load, resize, and Undo/Redo.
   * Recalculates minLand/maxLand, resets the view, and redraws. Discards carving preview because
   * it uses old elevations and may have different grid dimensions; the caller schedules rebuilding.
   * If terrain cannot be set (no WebGL2 or GPU texture limits exceeded), throws Error without changes.
   */
  function rebuildTerrainVisuals(
    elev: Float32Array,
    mapWidth: number,
    mapHeight: number,
    spacing: number,
    rows: number,
    cols: number,
    cellSizeParam?: number,
  ) {
    const count = rows * cols;
    const cs = cellSizeParam ?? cellGridIntervalRef.current;

    const { minLand, maxLand } = landElevationRange(elev);

    const uploaded = ensureRenderer().setTerrain({ rows, cols, spacing, mapWidth, mapHeight }, elev);
    if (!uploaded.ok) {
      throw new Error(t("terrainEditor.terrainTooLargeForGpu", { cols, rows, max: uploaded.maxTextureSize }));
    }

    elevRef.current = elev;
    previewElevRef.current = null;
    previewPolygonsRef.current = new Map();
    selectionMaskRef.current = new Float32Array(count);
    polygonPreviewWeightsRef.current = new Uint8Array(count);
    selectionBoundsRef.current = null;
    setHasSelection(false);
    cellGridIntervalRef.current = cs;
    settingsRef.current = { mapWidth, mapHeight, spacing, rows, cols, minLand, maxLand };

    resetView();
    baseIntervalRef.current = pickContourInterval(maxLand - minLand);
    baseZoomRef.current = viewRef.current.zoom;

    draw();
    // Grid shape may change and selection was cleared, so resend full state to the 3D preview.
    scheduleTerrain3dFullState();
  }

  /**
   * Opens terrain_3d_preview when "Open 3D preview" is pressed. single_per_entity allows one
   * window per work-copy mapId, so an existing window is only brought forward, never duplicated.
   */
  async function handleOpen3dPreview() {
    try {
      await invoke("open_registered_window", { windowType: "terrain_3d_preview", entityId: mapId });
    } catch (err) {
      console.error("[TerrainEditor] failed to open 3D preview window", err);
    }
  }

  /**
   * Saves edits to the Scenario (Rust save_scenario_terrain → core PUT /api/v1/scenarios/{id}/terrain).
   * Omits elevations/water/rivers while heights are locked. Sends loaded resources and Props
   * unchanged because they are not edited. Refreshes the Scenario list (content_revision) on success.
   */
  async function handleSaveToScenario() {
    const elev = elevRef.current;
    const untouched = untouchedContentRef.current;
    if (!elev || !untouched || !climate) return;
    const version = editVersionRef.current;
    setSaveStatus("saving");
    setSaveErrorMsg("");
    try {
      await invoke("save_scenario_terrain", scenarioTerrainSaveArgs(mapId, heightsLockedRef.current, {
        elev,
        waterSurfaces: serializeWaterSurfaces(),
        rivers: serializeRivers(),
        resources: untouched.resources,
        props: untouched.props,
        camera: cameraRef.current,
        groundCover: groundCoverRef.current,
        climate,
      }));
      setSavedVersion(version);
      setSaveStatus("saved");
      void emit("scenarios:changed");
    } catch (error) {
      setSaveStatus("error");
      setSaveErrorMsg(describeSaveError(error));
    }
  }

  /**
   * Converts save failures into display text. Terrain-specific codes use terrainEditor.saveErrors;
   * others use shared Scenario messages. Failures without codes (Rust communication/file errors)
   * have fixed Japanese bodies, so show generic text in the UI and keep the body only in logs.
   */
  function describeSaveError(error: unknown): string {
    const code = errorCode(error);
    if (!code) {
      console.error("[TerrainEditor] failed to save the scenario terrain", error);
      return t("terrainEditor.saveFailed");
    }
    return t(`terrainEditor.saveErrors.${code}`, { defaultValue: saveError(error, t) });
  }

  /**
   * Loads terrain data from the work copy through Tauri load_terrain_editor_data, converts
   * float16 to float32, and builds the offscreen terrain image, contours, and grid.
   * For height-locked Scenarios, switches to an available tab (camera) before displaying.
   */
  useEffect(() => {
    if (!mapId) {
      setStatus("error");
      return;
    }
    let cancelled = false;
    setStatus("loading");

    async function load() {
      try {
        const data = await invoke<TerrainEditorData>("load_terrain_editor_data", { editorId: mapId });
        if (cancelled) return;
        applyGroundCover(data.ground_cover, false);
        setClimate(data.climate);

        const bytes = base64ToUint8(data.data_base64);
        const count = data.grid_rows * data.grid_cols;
        if (count <= 0) throw new Error("empty terrain data");
        if (bytes.byteLength < count * 2) throw new Error("terrain data too short");

        const elev = decodeFloat16Array(bytes, count);
        const spacing = data.near_spacing > 0 ? data.near_spacing : 10;
        const mapWidth = data.map_width;
        const mapHeight = data.map_height;

        const cellSize = data.cell_size > 0 ? data.cell_size : 500;
        setScenarioInfo({
          title: data.title ?? {},
          terrainSource: data.terrain_source ?? "",
          farSpacing: data.far_spacing,
          cellSize,
        });
        const nextLocks = normalizeTerrainEditorLocks(data.locks);
        heightsLockedRef.current = nextLocks.heights_locked;
        setLocks(nextLocks);

        // rebuildTerrainVisuals handles elevation range calculation, terrain uploads, selection-mask
        // initialization, settingsRef setup, view reset, and rendering together.
        rebuildTerrainVisuals(
          elev,
          mapWidth,
          mapHeight,
          spacing,
          data.grid_rows,
          data.grid_cols,
          cellSize,
        );

        // Restore non-sea water surfaces (lakes, etc.) saved in work-copy meta.json.
        waterSurfacesRef.current = deserializeWaterSurfaces(data.water_surfaces);
        setWaterSurfaceCount(waterSurfacesRef.current.length);
        waterPointsRef.current = [];
        setWaterPointCount(0);

        // Restore rivers saved in work-copy meta.json; their beds are already carved into elevations.
        restoreRivers(deserializeRivers(data.rivers));
        setRiverPreviewStats(null);

        // Retain loaded resources/Props unchanged to send back on save, since they are not edited.
        untouchedContentRef.current = { resources: data.resources, props: data.props };

        // Restore initial camera state saved in work-copy meta.json.
        restoreCamera(deserializeCamera(data.camera));

        if (nextLocks.heights_locked && HEIGHT_LOCKED_TABS.has(activeTabRef.current)) handleTabChange("camera");
        setStatus("ready");
      } catch (error) {
        if (cancelled) return;
        setErrorMsg(error instanceof Error ? error.message : String(error));
        setStatus("error");
      }
    }

    void load();
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mapId]);

  /** Redraws on window resize. */
  useEffect(() => {
    function onResize() {
      draw();
    }
    window.addEventListener("resize", onResize);
    return () => window.removeEventListener("resize", onResize);
  }, []);

  /** Immediately reflects tool setting changes (shape/brush shape) in the preview. */
  useEffect(() => {
    if (status === "ready") draw();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status, shape, brushParams]);

  /**
   * Resends full state to the 3D preview when waterLevelInput or closed-shape state changes.
   * Closed but unconfirmed water is shown provisionally at the current level, so changing it
   * moves the 3D water surface up or down.
   */
  useEffect(() => {
    if (status === "ready") scheduleTerrain3dFullState();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status, waterLevelInput, waterPolygonClosed]);

  /**
   * Mouse controls: left click released in place applies one brush/polygon selection action;
   * left drag pans; right click confirms polygons.
   *
   * Since the left button handles clicks and drags, mousedown does not decide which. When
   * mousemove displacement exceeds CLICK_VS_DRAG_THRESHOLD_PX, start panning
   * (dragRef.active = true) and retain that action. Only a mouseup without crossing the
   * threshold applies one brush stamp / vertex addition at the release position.
   */
  useEffect(() => {
    const cv = canvasRef.current;
    if (!cv) return;
    // cv is known non-null, but bind it separately because TS narrowing does not hold in nested functions.
    const canvasEl: HTMLCanvasElement = cv;

    function onDown(e: MouseEvent) {
      const ts = toolStateRef.current;

      // Right button in water mode closes the drafted polygon's shape (at least three vertices).
      // Do not register it here; set water level, then use the confirm button in the second stage.
      // Normally, close the drafted polygon if it has at least three vertices; otherwise do nothing.
      if (e.button === 2) {
        if (ts.riverPointerMode === "draw") {
          // Course drawing: right click confirms the river; do nothing with fewer than two points.
          finishRiverDraft();
          return;
        }
        if (ts.riverPointerMode === "node") {
          // Node editing: right-click a node to delete it.
          const rect = canvasEl.getBoundingClientRect();
          const [wx, wz] = screenToWorld(e.clientX - rect.left, e.clientY - rect.top);
          const hit = hitSelectedRiverNode(wx, wz);
          if (hit) deleteRiverNode(hit.river, hit.index);
          return;
        }
        if (ts.waterMode) {
          if (waterPointsRef.current.length >= 3 && !ts.waterPolygonClosed) setWaterPolygonClosed(true);
          return;
        }
        if (ts.cameraMode) {
          // Camera mode: set yaw toward the right-clicked look-at point.
          const rect = canvasEl.getBoundingClientRect();
          const [wx, wz] = screenToWorld(e.clientX - rect.left, e.clientY - rect.top);
          aimCameraAt(wx, wz);
          return;
        }
        if (ts.shape === "polygon" && polygonPointsRef.current.length >= 3 && !ts.polygonClosed) {
          setPolygonClosed(true);
        }
        return;
      }
      if (e.button !== 0) return;
      // Left button: click versus drag is still undecided.
      // Record only the pan origin (current offset); onMove decides whether panning actually starts.
      const v = viewRef.current;
      leftDownRef.current = { x: e.clientX, y: e.clientY };
      dragRef.current = { active: false, startX: e.clientX, startY: e.clientY, offX: v.offsetX, offY: v.offsetY };
      // Node editing: pressing on a node prepares node dragging instead of panning; onMove's threshold decides movement.
      if (ts.riverPointerMode === "node") {
        const rect = canvasEl.getBoundingClientRect();
        const [wx, wz] = screenToWorld(e.clientX - rect.left, e.clientY - rect.top);
        const hit = hitSelectedRiverNode(wx, wz);
        riverNodeDragRef.current = hit ? { riverId: hit.river.id, index: hit.index, moved: false } : null;
      }
    }

    function onMove(e: MouseEvent) {
      const rect = canvasEl.getBoundingClientRect();
      const mx = e.clientX - rect.left;
      const my = e.clientY - rect.top;
      const inside = mx >= 0 && mx <= rect.width && my >= 0 && my <= rect.height;
      if (inside) {
        const [wx, wz] = screenToWorld(mx, my);
        // Cursor coordinate display.
        if (coordOverlayRef.current) {
          coordOverlayRef.current.textContent = `${wx.toFixed(1)}, ${wz.toFixed(1)}`;
        }
        // Always retain current world coordinates so brush/polygon previews follow the mouse.
        brushWorldPosRef.current = { x: wx, z: wz };
      }

      // Node dragging: after crossing the threshold, push Undo once and move the node with the mouse, without panning.
      // Rebuilding courses is cheap, so do it every time; debounce the expensive carving preview.
      const nodeDrag = riverNodeDragRef.current;
      if (nodeDrag && leftDownRef.current) {
        if (!nodeDrag.moved) {
          const dx = e.clientX - leftDownRef.current.x;
          const dy = e.clientY - leftDownRef.current.y;
          if (Math.hypot(dx, dy) <= CLICK_VS_DRAG_THRESHOLD_PX) return;
          nodeDrag.moved = true;
          pushUndoState();
          if (canvasEl.style) canvasEl.style.cursor = "grabbing";
        }
        const river = riversRef.current.find((r) => r.id === nodeDrag.riverId);
        if (river && !river.carved && river.controlPoints) {
          const [wx, wz] = screenToWorld(mx, my);
          moveRiverNode(river, nodeDrag.index, wx, wz);
          hoveredRiverNodeRef.current = nodeDrag.index;
          draw();
          scheduleRiverPreviewRebuild();
        }
        return;
      }

      // While the left button is held, cross the movement threshold to switch from single click to drag panning.
      if (leftDownRef.current && !dragRef.current.active) {
        const dx = e.clientX - leftDownRef.current.x;
        const dy = e.clientY - leftDownRef.current.y;
        if (Math.hypot(dx, dy) > CLICK_VS_DRAG_THRESHOLD_PX) {
          dragRef.current.active = true;
        }
      }

      if (dragRef.current.active) {
        const dx = e.clientX - dragRef.current.startX;
        const dy = e.clientY - dragRef.current.startY;
        const v = viewRef.current;
        v.offsetX = dragRef.current.offX - dx / v.zoom;
        // Y is rendered inverted (+Z upward), so add drag displacement to offsetY.
        v.offsetY = dragRef.current.offY + dy / v.zoom;
        draw();
        return;
      }

      if (inside) {
        if (toolStateRef.current.riverPointerMode === "node") {
          const [wx, wz] = screenToWorld(mx, my);
          updateRiverNodeHover(wx, wz);
        }
        // Brushes, draft tracking lines, and node highlights all live on the cursor layer, so redraw only it.
        drawCursorLayer();
      }
    }

    function onUp(e: MouseEvent) {
      // End node dragging: if moved, rebuild carving preview at the final state.
      // If released without movement (a node click only), do nothing.
      const nodeDrag = riverNodeDragRef.current;
      if (e.button === 0 && nodeDrag) {
        riverNodeDragRef.current = null;
        leftDownRef.current = null;
        dragRef.current.active = false;
        if (canvasEl.style) canvasEl.style.cursor = "grab";
        if (nodeDrag.moved) rebuildRiverPreview();
        return;
      }
      // Apply a single selection action at release only if the left button never transitioned into dragging.
      if (e.button === 0 && leftDownRef.current && !dragRef.current.active) {
        const ts = toolStateRef.current;
        const rect = canvasEl.getBoundingClientRect();
        const [wx, wz] = screenToWorld(e.clientX - rect.left, e.clientY - rect.top);
        if (ts.riverPointerMode === "draw") {
          // Course drawing: left click adds vertices in upstream-to-downstream order.
          addRiverDraftPoint(wx, wz);
        } else if (ts.riverPointerMode === "node") {
          // Node editing: click a course to add a node, or another river to switch selection.
          handleRiverNodeClick(wx, wz);
        } else if (ts.waterMode) {
          // Water mode: left click adds water polygon vertices only before closure.
          if (!ts.waterPolygonClosed) addWaterPoint(wx, wz);
        } else if (ts.cameraMode) {
          // Camera mode: place the camera at the left-clicked position, preserving orientation and height.
          placeCamera(wx, wz);
        } else if (ts.shape === "polygon") {
          if (!ts.polygonClosed) {
            addPolygonPoint(wx, wz);
          }
        } else {
          brushWorldPosRef.current = { x: wx, z: wz };
          mergeBrushAtCurrentPos();
        }
      }
      leftDownRef.current = null;
      dragRef.current.active = false;
    }

    function onWheel(e: WheelEvent) {
      e.preventDefault();
      const rect = canvasEl.getBoundingClientRect();
      const mx = e.clientX - rect.left;
      const my = e.clientY - rect.top;
      const v = viewRef.current;
      // Preserve the cursor's world position before and after zooming.
      const [wxBefore, wyBefore] = screenToWorld(mx, my);
      const factor = e.deltaY < 0 ? 1.15 : 1 / 1.15;
      v.zoom = Math.max(0.0001, Math.min(100000, v.zoom * factor));
      const [wxAfter, wyAfter] = screenToWorld(mx, my);
      v.offsetX += wxBefore - wxAfter;
      v.offsetY += wyBefore - wyAfter;
      // Contour intervals follow zoom at draw time, so a redraw alone adds finer detail.
      draw();
    }

    function onContextMenu(e: MouseEvent) {
      // Suppress the browser context menu because right click confirms polygons.
      e.preventDefault();
    }

    canvasEl.addEventListener("mousedown", onDown);
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
    canvasEl.addEventListener("wheel", onWheel, { passive: false });
    canvasEl.addEventListener("contextmenu", onContextMenu);
    return () => {
      canvasEl.removeEventListener("mousedown", onDown);
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
      canvasEl.removeEventListener("wheel", onWheel);
      canvasEl.removeEventListener("contextmenu", onContextMenu);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status]);

  /**
   * Undo/Redo shortcuts: Ctrl+Z goes back; Ctrl+Y (Ctrl+Shift+Z) goes forward.
   * Escape discards the course draft vertices.
   */
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (metadataDialogRef.current?.open) return;
      if (e.key === "Escape" && toolStateRef.current.riverPointerMode === "draw" && riverDraftPointsRef.current.length > 0) {
        clearRiverDraft();
        return;
      }
      if (!e.ctrlKey || e.metaKey) return;
      // Do not intercept while entering text.
      const tag = (e.target as HTMLElement)?.tagName?.toLowerCase();
      if (tag === "input" || tag === "textarea" || tag === "select") return;

      if (e.key === "z" || e.key === "Z") {
        e.preventDefault();
        if (undoStackRef.current.length > 0) performUndo();
      } else if (e.key === "y" || e.key === "Y") {
        e.preventDefault();
        if (redoStackRef.current.length > 0) performRedo();
      }
    }
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const scenarioTitle = resolveLocalizedText(scenarioInfo?.title, i18n.language);
  return (
    <div className="terrain-editor-root">
      <dialog ref={metadataDialogRef} className="terrain-editor-metadata-dialog" aria-label={t("terrainEditor.metadataTitle")}>
        <button type="button" className="terrain-editor-btn terrain-editor-dialog-close" onClick={() => metadataDialogRef.current?.close()}>{t("common.close")}</button>
            {/* Map metadata card */}
            <div className="terrain-editor-card">
              <div className="terrain-editor-card-title">
                {t("terrainEditor.metadataTitle", { defaultValue: "地図メタデータ" })}
              </div>
              <div className="terrain-editor-param-group">
                {scenarioInfo && (
                  <>
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.metaTerrainSource")}</span>
                      <span className="terrain-editor-param-value">
                        {scenarioInfo.terrainSource || "-"}
                      </span>
                    </div>
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.metaFarSpacing")}</span>
                      <span className="terrain-editor-param-value">{scenarioInfo.farSpacing}m</span>
                    </div>
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.metaCellSize")}</span>
                      <span className="terrain-editor-param-value">{scenarioInfo.cellSize}m</span>
                    </div>
                  </>
                )}
                <div className="terrain-editor-param-row">
                  <span className="terrain-editor-param-label">{t("groundCover.label")}</span>
                  <select
                    className="terrain-editor-select"
                    value={groundCover}
                    onChange={(e) => applyGroundCover(Number(e.target.value), true)}
                    disabled={status !== "ready"}
                  >
                    {GROUND_COVER_TYPES.map((type) => (
                      <option key={type} value={type}>{t(`groundCover.${type}`)}</option>
                    ))}
                  </select>
                </div>
                <div className="terrain-editor-hint">{t("groundCover.hint")}</div>
                <div className="terrain-editor-param-row">
                  <span className="terrain-editor-param-label">{t("climate.label")}</span>
                  <select
                    className="terrain-editor-select"
                    value=""
                    onChange={(e) => {
                      const preset = master?.climates[Number(e.target.value)];
                      if (!preset) return;
                      setClimate(copyClimatePreset(preset));
                      markDirty();
                    }}
                    disabled={status !== "ready"}
                  >
                    <option value="">{climate?.name}</option>
                    {Object.entries(master?.climates ?? {}).map(([id, preset]) => (
                      <option key={id} value={id}>{preset.display_name}</option>
                    ))}
                  </select>
                </div>
                <div className="terrain-editor-hint">{t("climate.hint")}</div>
                {settingsRef.current && (
                  <div className="terrain-editor-meta-readonly">
                    {t("terrainEditor.metaReadonlySummary", {
                      defaultValue: `${settingsRef.current.mapWidth}×${settingsRef.current.mapHeight}m / 近景間隔${settingsRef.current.spacing}m / ${settingsRef.current.cols}×${settingsRef.current.rows}グリッド(変更不可)`,
                      width: settingsRef.current.mapWidth,
                      height: settingsRef.current.mapHeight,
                      spacing: settingsRef.current.spacing,
                      cols: settingsRef.current.cols,
                      rows: settingsRef.current.rows,
                    })}
                  </div>
                )}
              </div>

            </div>
      </dialog>
      {/* Left tool panel for card-based editing UI. */}
      <aside className="terrain-editor-tools">
        <div className="terrain-editor-tools-title-row">
          <h1 className="terrain-editor-tools-title">
            {scenarioTitle
              ? t("terrainEditor.scenarioHeading", { title: scenarioTitle })
              : t("terrainEditor.toolsTitle")}
          </h1>
          <div className="terrain-editor-header-actions">
          <button type="button" className="terrain-editor-btn" onClick={() => void handleOpen3dPreview()}>
            {t("terrainEditor.open3dPreview", { defaultValue: "3Dプレビューを開く" })}
          </button>
          <button type="button" className="terrain-editor-btn" onClick={() => metadataDialogRef.current?.showModal()}>{t("terrainEditor.editMetadata")}</button>
          </div>
        </div>

        {locks.heights_locked && (
          <div className="terrain-editor-lock-notice" role="note">
            <p>{t("terrainEditor.heightsLocked")}</p>
            {locks.reasons.length > 0 && (
              <ul>
                {locks.reasons.map((code) => (
                  <li key={code}>{t(`terrainEditor.lockReason.${code}`, { defaultValue: code })}</li>
                ))}
              </ul>
            )}
          </div>
        )}

        {/* Select the editing target from a dropdown. Elevation locks disable tabs modifying elevations, water surfaces, or rivers. */}
        <div className="terrain-editor-tabs">
          <label className="terrain-editor-tabs-label" htmlFor="terrain-editor-tab-select">
            {t("terrainEditor.tabSelectLabel", { defaultValue: "編集対象" })}
          </label>
          <select
            id="terrain-editor-tab-select"
            className="terrain-editor-select terrain-editor-tab-select"
            value={activeTab}
            onChange={(e) => handleTabChange(e.target.value as EditorTab)}
            disabled={status !== "ready"}
          >
            <option value="terrain" disabled={locks.heights_locked}>{t("terrainEditor.tabTerrain", { defaultValue: "地形編集" })}</option>
            <option value="water" disabled={locks.heights_locked}>{t("terrainEditor.tabWater", { defaultValue: "水面編集" })}</option>
            <option value="river" disabled={locks.heights_locked}>{t("terrainEditor.tabRiver", { defaultValue: "川編集" })}</option>
            <option value="camera">{t("terrainEditor.tabCamera", { defaultValue: "カメラ" })}</option>
          </select>
        </div>

        {activeTab === "terrain" && (
          <div className="terrain-editor-subtabs" role="tablist" aria-label={t("terrainEditor.tabTerrain")}>
            {(["auto", "manual"] as const).map((tab) => (
              <button
                key={tab}
                id={`terrain-edit-tab-${tab}`}
                type="button"
                role="tab"
                aria-selected={terrainEditTab === tab}
                aria-controls={`terrain-edit-panel-${tab}`}
                tabIndex={terrainEditTab === tab ? 0 : -1}
                className={`terrain-editor-toggle-btn ${terrainEditTab === tab ? "selected" : ""}`}
                onClick={() => setTerrainEditTab(tab)}
                onKeyDown={(event) => {
                  if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
                  event.preventDefault();
                  const next = event.key === "Home" ? "auto" : event.key === "End" ? "manual" : tab === "auto" ? "manual" : "auto";
                  setTerrainEditTab(next);
                  document.getElementById(`terrain-edit-tab-${next}`)?.focus();
                }}
              >
                {t(tab === "auto" ? "terrainEditor.autoTitle" : "terrainEditor.editTitle")}
              </button>
            ))}
          </div>
        )}

        {/* Show selected editing cards in one column. Settings and selections persist across tabs. */}
        <div className="terrain-editor-cards-area">

          {/* ============================================================ */}
          {/* Left column: automatic terrain editing */}
          {/* ============================================================ */}
          <div className="terrain-editor-cards-left" id="terrain-edit-panel-auto" role="tabpanel" aria-labelledby="terrain-edit-tab-auto" hidden={activeTab !== "terrain" || terrainEditTab !== "auto"}>

            {/* Automatic editing card (apply erosion simulation across the map in one operation) */}
            {activeTab === "terrain" && (
              <div className="terrain-editor-card">
              <div className="terrain-editor-card-title">{t("terrainEditor.autoTitle", { defaultValue: "自動編集" })}</div>

              {/* Hydraulic erosion: raindrop particles carve slopes while flowing, creating valleys and mouth deposits. */}
              <div className="terrain-editor-section">
                <div className="terrain-editor-section-title">{t("terrainEditor.autoHydraulic", { defaultValue: "水流侵食(雨)" })}</div>
                <div className="terrain-editor-param-group">
                  <div className="terrain-editor-param-row">
                    <span className="terrain-editor-param-label">{t("terrainEditor.hydraulicDroplets", { defaultValue: "雨粒の数" })}</span>
                    <NumberStepper
                      value={hydraulicParams.droplets}
                      onChange={(v) => setHydraulicParams((p) => ({ ...p, droplets: v }))}
                      min={1000}
                      max={500000}
                      step={1000}
                    />
                  </div>
                  <div className="terrain-editor-param-row">
                    <span className="terrain-editor-param-label">{t("terrainEditor.hydraulicStrength", { defaultValue: "侵食の強さ" })}</span>
                    <NumberStepper
                      value={hydraulicParams.erodeStrength}
                      onChange={(v) => setHydraulicParams((p) => ({ ...p, erodeStrength: v }))}
                      min={0.05}
                      max={1}
                      step={0.05}
                      integerOnly={false}
                    />
                  </div>
                  <div className="terrain-editor-action-buttons">
                    <button
                      type="button"
                      className="terrain-editor-btn"
                      disabled={status !== "ready"}
                      onClick={runHydraulicErosion}
                    >
                      {t("terrainEditor.autoRun", { defaultValue: "実行" })}
                    </button>
                  </div>
                </div>
              </div>

              {/* Thermal erosion: cliffs/steep slopes exceeding the angle of repose collapse and become gentler. */}
              <div className="terrain-editor-section">
                <div className="terrain-editor-section-title">{t("terrainEditor.autoThermal", { defaultValue: "熱侵食(崖崩し)" })}</div>
                <div className="terrain-editor-param-group">
                  <div className="terrain-editor-param-row">
                    <span className="terrain-editor-param-label">{t("terrainEditor.thermalTalusAngle", { defaultValue: "安息角(度)" })}</span>
                    <NumberStepper
                      value={thermalParams.talusAngleDeg}
                      onChange={(v) => setThermalParams((p) => ({ ...p, talusAngleDeg: v }))}
                      min={10}
                      max={60}
                      step={1}
                    />
                  </div>
                  <div className="terrain-editor-param-row">
                    <span className="terrain-editor-param-label">{t("terrainEditor.thermalIterations", { defaultValue: "反復回数" })}</span>
                    <NumberStepper
                      value={thermalParams.iterations}
                      onChange={(v) => setThermalParams((p) => ({ ...p, iterations: v }))}
                      min={1}
                      max={300}
                      step={10}
                    />
                  </div>
                  <div className="terrain-editor-action-buttons">
                    <button
                      type="button"
                      className="terrain-editor-btn"
                      disabled={status !== "ready"}
                      onClick={runThermalErosion}
                    >
                      {t("terrainEditor.autoRun", { defaultValue: "実行" })}
                    </button>
                  </div>
                </div>
              </div>

              {/* Sea-floor generation: deepen uniformly 0 m sea cells (GSI DEM, etc.) according to coastline distance. */}
              <div className="terrain-editor-section">
                <div className="terrain-editor-section-title">{t("terrainEditor.autoSeaFloor", { defaultValue: "海底生成(海0mを深くする)" })}</div>
                <div className="terrain-editor-param-group">
                  <div className="terrain-editor-param-row">
                    <span className="terrain-editor-param-label">{t("terrainEditor.seaFloorThreshold", { defaultValue: "海とみなす標高(m)" })}</span>
                    <NumberStepper
                      value={seaFloorParams.seaLevelThreshold}
                      onChange={(v) => setSeaFloorParams((p) => ({ ...p, seaLevelThreshold: v }))}
                      min={-50}
                      max={50}
                      step={0.5}
                      integerOnly={false}
                    />
                  </div>
                  <div className="terrain-editor-param-row">
                    <span className="terrain-editor-param-label">{t("terrainEditor.seaFloorSlopeMode")}</span>
                    <select
                      className="terrain-editor-select"
                      value={seaFloorParams.slopeMode}
                      onChange={(e) => setSeaFloorParams((p) => ({ ...p, slopeMode: e.target.value as SeaFloorSlopeMode }))}
                      disabled={status !== "ready"}
                    >
                      <option value="land">{t("terrainEditor.seaFloorSlopeModeLand")}</option>
                      <option value="fixed">{t("terrainEditor.seaFloorSlopeModeFixed")}</option>
                    </select>
                  </div>
                  {seaFloorParams.slopeMode === "fixed" && (
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.seaFloorSlope", { defaultValue: "海岸の海底勾配(%)" })}</span>
                      <NumberStepper
                        value={seaFloorParams.shelfSlopePercent}
                        onChange={(v) => setSeaFloorParams((p) => ({ ...p, shelfSlopePercent: v }))}
                        min={0.1}
                        max={20}
                        step={0.1}
                        integerOnly={false}
                      />
                    </div>
                  )}
                  <div className="terrain-editor-param-row">
                    <span className="terrain-editor-param-label">{t("terrainEditor.seaFloorMaxDepth", { defaultValue: "最大水深(m)" })}</span>
                    <NumberStepper
                      value={seaFloorParams.maxDepth}
                      onChange={(v) => setSeaFloorParams((p) => ({ ...p, maxDepth: v }))}
                      min={5}
                      max={500}
                      step={5}
                    />
                  </div>
                  <div className="terrain-editor-action-buttons">
                    <button
                      type="button"
                      className="terrain-editor-btn"
                      disabled={status !== "ready"}
                      onClick={runSeaFloorGeneration}
                    >
                      {t("terrainEditor.autoRun", { defaultValue: "実行" })}
                    </button>
                  </div>
                  {seaFloorModifiedCount === 0 && (
                    <div className="terrain-editor-hint terrain-editor-hint--error">
                      {t("terrainEditor.seaFloorNoTarget", { defaultValue: "海とみなせるマスがありませんでした。「海とみなす標高」を調整してください" })}
                    </div>
                  )}
                  <div className="terrain-editor-hint">
                    {t("terrainEditor.seaFloorHint", {
                      defaultValue:
                        "標高が判定値以下のマスを海とみなし、海岸から沖へ深くします(最大水深へ滑らかに漸近)。「陸の勾配に合わせる」は海岸近くの陸の勾配を海へ延ばし、急な海岸ほど深い海が近く、平らな海岸ほど遠浅になります。港を建てられる水深はこの海底で決まります。実際と違う所(平地の横の深い川など)は、範囲を選んで「一定の勾配」で直してください。湖など海にしたくない0m地帯がある場合は、先に海側だけを範囲選択してから実行してください",
                    })}
                  </div>
                </div>
              </div>

                <div className="terrain-editor-hint">
                  {t("terrainEditor.autoHint", { defaultValue: "選択範囲があればそこを起点に侵食し(雨粒数は選択面積に応じて自動調整)、なければマップ全体へ適用します。実行中は数秒間操作できないことがあります" })}
                </div>
              </div>
            )}
          </div>

          {/* ============================================================ */}
          {/* Right column: editing cards for each tab */}
          {/* ============================================================ */}
          <div className="terrain-editor-cards-right" id="terrain-edit-panel-manual" role={activeTab === "terrain" ? "tabpanel" : undefined} aria-labelledby={activeTab === "terrain" ? "terrain-edit-tab-manual" : undefined} hidden={activeTab === "terrain" && terrainEditTab !== "manual"}>
            {/* Water surface card (add non-sea lakes, etc. as arbitrary polygons) */}
            {activeTab === "water" && (
              <div className="terrain-editor-card">
                <div className="terrain-editor-card-title">{t("terrainEditor.waterTitle", { defaultValue: "水面(湖など)" })}</div>

                <div className="terrain-editor-section">
                  <div className="terrain-editor-hint">
                    {t("terrainEditor.waterHint", {
                      defaultValue:
                        "水面編集タブがアクティブのときは水面追加モードになり、左クリックで水面ポリゴンの頂点を追加、右クリック(または「形状を確定」)で形状を閉じます(3点以上)。左ドラッグで画面移動。形状を閉じたあと、下の「水面の標高」を設定して「水面を確定」で1枚の水面として登録します。海面(標高0m)は自動で表現されるため、ここでは湖など海面以外の水面のみを追加します",
                    })}
                  </div>
                  <div className="terrain-editor-param-row">
                    <span className="terrain-editor-param-label">{t("terrainEditor.waterLevel", { defaultValue: "水面の標高(m)" })}</span>
                    <NumberStepper value={waterLevelInput} onChange={setWaterLevelInput} min={-500} max={5000} step={1} integerOnly={false} disabled={status !== "ready"} />
                  </div>
                  <div className="terrain-editor-polygon-count">
                    {waterPolygonClosed
                      ? t("terrainEditor.waterShapeClosed", { defaultValue: "形状を確定しました。水位を設定して「水面を確定」を押してください" })
                      : t("terrainEditor.waterVertexCount", { defaultValue: `作図中の頂点数: ${waterPointCount}`, count: waterPointCount })}
                  </div>
                  <div className="terrain-editor-action-buttons">
                    {!waterPolygonClosed ? (
                      <>
                        <button
                          type="button"
                          className="terrain-editor-btn terrain-editor-btn-confirm"
                          disabled={!waterMode || waterPointCount < 3}
                          onClick={() => setWaterPolygonClosed(true)}
                        >
                          {t("terrainEditor.waterCloseShape", { defaultValue: "形状を確定" })}
                        </button>
                        <button
                          type="button"
                          className="terrain-editor-btn"
                          disabled={!waterMode || waterPointCount === 0}
                          onClick={clearWaterPoints}
                        >
                          {t("terrainEditor.waterClearPoints", { defaultValue: "作図クリア" })}
                        </button>
                      </>
                    ) : (
                      <>
                        <button
                          type="button"
                          className="terrain-editor-btn terrain-editor-btn-confirm"
                          disabled={!waterMode}
                          onClick={confirmWaterSurface}
                        >
                          {t("terrainEditor.waterConfirm", { defaultValue: "水面を確定" })}
                        </button>
                        <button
                          type="button"
                          className="terrain-editor-btn"
                          disabled={!waterMode}
                          onClick={clearWaterPoints}
                        >
                          {t("terrainEditor.waterClearPoints", { defaultValue: "作図クリア" })}
                        </button>
                      </>
                    )}
                  </div>
                  <div className="terrain-editor-polygon-count">
                    {t("terrainEditor.waterSurfaceCount", { defaultValue: `作成済みの水面: ${waterSurfaceCount}枚`, count: waterSurfaceCount })}
                  </div>
                  <div className="terrain-editor-action-buttons">
                    <button
                      type="button"
                      className="terrain-editor-btn"
                      disabled={waterSurfaceCount === 0}
                      onClick={removeLastWaterSurface}
                    >
                      {t("terrainEditor.waterRemoveLast", { defaultValue: "最後の水面を削除" })}
                    </button>
                    <button
                      type="button"
                      className="terrain-editor-btn"
                      disabled={waterSurfaceCount === 0}
                      onClick={clearAllWaterSurfaces}
                    >
                      {t("terrainEditor.waterClearAll", { defaultValue: "すべての水面を削除" })}
                    </button>
                  </div>
                </div>
              </div>
            )}

            {/* Initial camera state card */}
            {activeTab === "camera" && (
              <div className="terrain-editor-card">
                <div className="terrain-editor-card-title">{t("terrainEditor.cameraTitle", { defaultValue: "カメラ初期状態" })}</div>
                <div className="terrain-editor-section">
                  <div className="terrain-editor-hint">
                    {t("terrainEditor.cameraHint", {
                      defaultValue:
                        "このシナリオを始めたときのカメラの位置と向きです。左クリックでカメラ位置を置き、右クリックで注視点を指定して向きを決めます。左ドラッグで画面移動。未設定のままならゲームはマップ中心(原点)の既定視点で始まります",
                    })}
                  </div>
                  {cameraState ? (
                    <>
                      <div className="terrain-editor-param-row">
                        <span className="terrain-editor-param-label">{t("terrainEditor.cameraPosition", { defaultValue: "位置(x, z)" })}</span>
                        <span className="terrain-editor-selection-status">
                          {`${cameraState.x.toFixed(1)}, ${cameraState.z.toFixed(1)}`}
                        </span>
                      </div>
                      <div className="terrain-editor-param-row">
                        <span className="terrain-editor-param-label">{t("terrainEditor.cameraHeight", { defaultValue: "高さ(m)" })}</span>
                        <NumberStepper
                          value={cameraState.y}
                          onChange={(v) => updateCameraField("y", v)}
                          min={10}
                          max={20000}
                          step={10}
                          integerOnly={false}
                          disabled={status !== "ready"}
                        />
                      </div>
                      <div className="terrain-editor-param-row">
                        <span className="terrain-editor-param-label">{t("terrainEditor.cameraYaw", { defaultValue: "方位(度、北=0)" })}</span>
                        <NumberStepper
                          value={cameraState.yaw}
                          onChange={(v) => updateCameraField("yaw", ((v % 360) + 360) % 360)}
                          min={-1}
                          max={360}
                          step={5}
                          integerOnly={false}
                          disabled={status !== "ready"}
                        />
                      </div>
                      <div className="terrain-editor-param-row">
                        <span className="terrain-editor-param-label">{t("terrainEditor.cameraPitch", { defaultValue: "見下ろし角(度)" })}</span>
                        <NumberStepper
                          value={cameraState.pitch}
                          onChange={(v) => updateCameraField("pitch", v)}
                          min={5}
                          max={89}
                          step={5}
                          integerOnly={false}
                          disabled={status !== "ready"}
                        />
                      </div>
                      <div className="terrain-editor-action-buttons">
                        <button type="button" className="terrain-editor-btn" disabled={!cameraMode} onClick={clearCamera}>
                          {t("terrainEditor.cameraClear", { defaultValue: "カメラをクリア(未設定に戻す)" })}
                        </button>
                      </div>
                    </>
                  ) : (
                    <div className="terrain-editor-hint">
                      {t("terrainEditor.cameraUnset", { defaultValue: "未設定です。キャンバスを左クリックしてカメラ位置を置いてください" })}
                    </div>
                  )}
                </div>
              </div>
            )}

            {/* River editing cards (rainfall region → generation → created-river list) */}
            {activeTab === "river" && (
              <>
                {/* Operation mode card: switch river tab pointer actions (rainfall selection / draw river / edit nodes). */}
                <div className="terrain-editor-card">
                  <div className="terrain-editor-card-title">{t("terrainEditor.riverModeTitle", { defaultValue: "流路の操作" })}</div>
                  <div className="terrain-editor-section">
                    <div className="terrain-editor-toggle-group">
                      <button
                        type="button"
                        className={`terrain-editor-toggle-btn ${riverPointerMode === "rain" ? "selected" : ""}`}
                        onClick={() => setRiverPointerModeSafe("rain")}
                      >
                        {t("terrainEditor.riverModeRain", { defaultValue: "降雨範囲の選択" })}
                      </button>
                      <button
                        type="button"
                        className={`terrain-editor-toggle-btn ${riverPointerMode === "draw" ? "selected" : ""}`}
                        disabled={status !== "ready"}
                        onClick={() => setRiverPointerModeSafe("draw")}
                      >
                        {t("terrainEditor.riverModeDraw", { defaultValue: "川を描く" })}
                      </button>
                      <button
                        type="button"
                        className={`terrain-editor-toggle-btn ${riverPointerMode === "node" ? "selected" : ""}`}
                        disabled={status !== "ready"}
                        onClick={() => setRiverPointerModeSafe("node")}
                      >
                        {t("terrainEditor.riverModeNode", { defaultValue: "ノード編集" })}
                      </button>
                    </div>
                    {riverPointerMode === "rain" && (
                      <div className="terrain-editor-hint">
                        {t("terrainEditor.riverModeRainHint", {
                          defaultValue: "地形編集と同じ操作で範囲を選択します(自動生成の降雨範囲)。川を任意の場所に作るには「川を描く」、作成済みの流路を動かすには「ノード編集」を選んでください",
                        })}
                      </div>
                    )}
                    {riverPointerMode === "draw" && (
                      <>
                        <div className="terrain-editor-hint">
                          {t("terrainEditor.riverDrawHint", {
                            defaultValue:
                              "左クリックで上流から下流の順に頂点を置き、右クリックで川を確定します(2点以上。Escで作図をやり直し)。頂点を通る滑らかな曲線が流路になり、終点が海・湖・他の川の上にあればそこへ接続されます。確定後は「ノード編集」で形を整えられます",
                          })}
                        </div>
                        <div className="terrain-editor-param-row">
                          <span className="terrain-editor-param-label">{t("terrainEditor.riverManualWidthHead", { defaultValue: "上流端の川幅(m)" })}</span>
                          <NumberStepper value={riverManualWidthHead} onChange={setRiverManualWidthHead} min={0} max={500} step={1} integerOnly={false} />
                        </div>
                        <div className="terrain-editor-param-row">
                          <span className="terrain-editor-param-label">{t("terrainEditor.riverManualWidthTail", { defaultValue: "下流端の川幅(m)" })}</span>
                          <NumberStepper value={riverManualWidthTail} onChange={setRiverManualWidthTail} min={0} max={500} step={1} integerOnly={false} />
                        </div>
                        <div className="terrain-editor-polygon-count">
                          {t("terrainEditor.riverDraftVertexCount", { defaultValue: `作図中の頂点数: ${riverDraftPointCount}`, count: riverDraftPointCount })}
                        </div>
                        <div className="terrain-editor-action-buttons">
                          <button
                            type="button"
                            className="terrain-editor-btn terrain-editor-btn-confirm"
                            disabled={riverDraftPointCount < 2}
                            onClick={finishRiverDraft}
                          >
                            {t("terrainEditor.riverDraftConfirm", { defaultValue: "川を確定" })}
                          </button>
                          <button type="button" className="terrain-editor-btn" disabled={riverDraftPointCount === 0} onClick={clearRiverDraft}>
                            {t("terrainEditor.riverDraftClear", { defaultValue: "作図クリア" })}
                          </button>
                        </div>
                      </>
                    )}
                    {riverPointerMode === "node" && (
                      <div className="terrain-editor-hint">
                        {riverList.some((river) => river.id === selectedRiverId && river.editable)
                          ? t("terrainEditor.riverNodeHint", {
                              defaultValue:
                                "選択中の川のノードをドラッグで移動、流路上の左クリックでノードを追加、ノードの右クリックで削除します(2点は残ります)。終点を海・湖・他の川の上へ動かすとそこへ接続されます。別の未彫り込みの川をクリックすると選択が切り替わります",
                            })
                          : t("terrainEditor.riverNodeNoTarget", {
                              defaultValue:
                                "編集する川を一覧で選択するか、地図上の未彫り込みの川をクリックしてください(彫り込み済みの川や読み込んだ川はノード編集できません。彫り直すには Ctrl+Z で彫り込み前に戻してください)",
                            })}
                      </div>
                    )}
                  </div>
                </div>

                {/* Rainfall region card: reuse the terrain-editing selection mask as the rainfall region. */}
                <div className="terrain-editor-card">
                  <div className="terrain-editor-card-title">{t("terrainEditor.rainAreaTitle", { defaultValue: "降雨範囲" })}</div>
                  <div className="terrain-editor-section">
                    <div className="terrain-editor-hint">
                      {t("terrainEditor.rainAreaHint", {
                        defaultValue:
                          "選択範囲があればその範囲にのみ雨を降らせます(範囲内に源を持つ川が海まで伸びます)。選択が無ければマップ全体に降らせます。範囲の作図は地形編集タブと同じ操作です(左クリックでブラシ/ポリゴン選択、右クリックでポリゴン確定)",
                      })}
                    </div>
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.rainAreaStatus", { defaultValue: "降雨範囲" })}</span>
                      <span className="terrain-editor-selection-status">
                        {hasSelection
                          ? t("terrainEditor.rainAreaSelection", { defaultValue: "選択範囲のみ" })
                          : t("terrainEditor.rainAreaWholeMap", { defaultValue: "マップ全体" })}
                      </span>
                    </div>
                    <div className="terrain-editor-action-buttons">
                      <button
                        type="button"
                        className="terrain-editor-btn"
                        disabled={!hasSelection && polygonPointCount === 0}
                        onClick={resetSelection}
                      >
                        {t("terrainEditor.resetSelection", { defaultValue: "選択範囲をリセット" })}
                      </button>
                    </div>
                  </div>
                </div>

                {/* River generation card: two-stage preview (unchanged elevations) → confirm (carve) workflow. */}
                <div className="terrain-editor-card">
                  <div className="terrain-editor-card-title">{t("terrainEditor.riverTitle", { defaultValue: "川の自動生成" })}</div>
                  <div className="terrain-editor-section">
                    <div className="terrain-editor-hint">
                      {t("terrainEditor.riverHint", {
                        defaultValue:
                          "地形から流路を自動で特定します。「流路を計算」の時点では地形は変化しません。川が多すぎ/少なすぎる場合は雨量や最小流量を調整して再計算してください",
                      })}
                    </div>
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.riverRainfall", { defaultValue: "年間雨量(mm)" })}</span>
                      {/* No upper limit. On small island maps, realistic rainfall may leave maximum discharge below
                          the threshold (see riverZeroHint), so users may need to experiment with unrealistically high values. */}
                      <NumberStepper
                        value={riverParams.rainfallMmPerYear}
                        onChange={(v) => setRiverParams((p) => ({ ...p, rainfallMmPerYear: v }))}
                        min={100}
                        step={100}
                      />
                    </div>
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.riverMinDischarge", { defaultValue: "最小流量(m³/s)" })}</span>
                      {/* Maximum discharge on small island maps can be below 1 m³/s, so allow a very small minimum. */}
                      <NumberStepper
                        value={riverParams.minDischargeM3s}
                        onChange={(v) => setRiverParams((p) => ({ ...p, minDischargeM3s: v }))}
                        min={0.01}
                        max={500}
                        step={0.1}
                        integerOnly={false}
                      />
                    </div>
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.riverMeanderStrength", { defaultValue: "蛇行の強さ" })}</span>
                      <NumberStepper
                        value={riverParams.meanderStrength}
                        onChange={(v) => setRiverParams((p) => ({ ...p, meanderStrength: v }))}
                        min={0}
                        max={1}
                        step={0.05}
                        integerOnly={false}
                      />
                    </div>
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.riverMeanderIterations", { defaultValue: "蛇行の反復回数" })}</span>
                      <NumberStepper
                        value={riverParams.meanderIterations}
                        onChange={(v) => setRiverParams((p) => ({ ...p, meanderIterations: v }))}
                        min={0}
                        max={100}
                        step={5}
                      />
                    </div>
                    {/* Width/depth = coefficient × discharge^exponent. Low-discharge maps need a broad coefficient
                        range for visible changes, so allow a generous upper limit. */}
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.riverWidthCoef", { defaultValue: "幅係数" })}</span>
                      <NumberStepper
                        value={riverParams.widthCoef}
                        onChange={(v) => setRiverParams((p) => ({ ...p, widthCoef: v }))}
                        min={1}
                        max={200}
                        step={1}
                        integerOnly={false}
                      />
                    </div>
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.riverDepthCoef", { defaultValue: "深さ係数" })}</span>
                      <NumberStepper
                        value={riverParams.depthCoef}
                        onChange={(v) => setRiverParams((p) => ({ ...p, depthCoef: v }))}
                        min={0.1}
                        max={30}
                        step={0.1}
                        integerOnly={false}
                      />
                    </div>
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.riverBraiding", { defaultValue: "中州を作る" })}</span>
                      <input
                        type="checkbox"
                        checked={riverParams.enableBraiding}
                        onChange={(e) => setRiverParams((p) => ({ ...p, enableBraiding: e.target.checked }))}
                        disabled={status !== "ready"}
                      />
                    </div>
                    <div className="terrain-editor-polygon-count">
                      {riverPreviewStats
                        ? t("terrainEditor.riverPreviewStats", {
                            defaultValue: `計算結果: ${riverPreviewStats.count}本 / 総延長 ${riverPreviewStats.totalKm.toFixed(1)}km`,
                            count: riverPreviewStats.count,
                            totalKm: riverPreviewStats.totalKm.toFixed(1),
                          })
                        : t("terrainEditor.riverNoPreview", { defaultValue: "計算結果なし(「流路を計算」を押してください)" })}
                    </div>
                    {/* With zero courses, show hints matching the cause:
                        - Maximum discharge below threshold: lower the threshold or increase rainfall.
                        - Above threshold, but all discarded as too short: lower the threshold further
                          to move channel heads upstream, lengthening rivers enough to retain them.
                        Both are common on small island maps. */}
                    {riverPreviewStats && riverPreviewStats.count === 0 && (
                      <div className="terrain-editor-hint terrain-editor-hint--error">
                        {riverPreviewStats.maxDischargeM3s < riverParams.minDischargeM3s
                          ? t("terrainEditor.riverZeroHint", {
                              defaultValue: `このマップの最大流量は ${riverPreviewStats.maxDischargeM3s.toFixed(2)}m³/s で、最小流量 ${riverParams.minDischargeM3s}m³/s に届いていません。最小流量を下げるか、雨量を増やしてください`,
                              maxQ: riverPreviewStats.maxDischargeM3s.toFixed(2),
                              minQ: riverParams.minDischargeM3s,
                            })
                          : t("terrainEditor.riverZeroShortHint", {
                              defaultValue: `流量が最小流量を超える区間はありますが、短すぎるため川になりませんでした(${riverPreviewStats.discardedShortCount}本を破棄)。最小流量をさらに下げると流路が上流へ伸びて川として成立しやすくなります`,
                              discarded: riverPreviewStats.discardedShortCount,
                            })}
                      </div>
                    )}
                    <div className="terrain-editor-action-buttons">
                      <button
                        type="button"
                        className="terrain-editor-btn terrain-editor-btn-confirm"
                        disabled={status !== "ready"}
                        onClick={runRiverCompute}
                      >
                        {t("terrainEditor.riverComputePreview", { defaultValue: "流路を計算" })}
                      </button>
                    </div>
                    <div className="terrain-editor-hint">
                      {t("terrainEditor.riverComputeHint", {
                        defaultValue:
                          "計算した流路は下の一覧に「未彫り込み」として追加されます(地形はまだ変化しません)。一覧で不要な川を削除したり深さ係数を調整してから、1本ずつ「彫る」で地形へ彫り込んでください。再計算すると未彫り込みの川だけが差し替わります",
                      })}
                    </div>
                  </div>
                </div>

                {/* Created-river card (uncarved + carved list) */}
                <div className="terrain-editor-card">
                  <div className="terrain-editor-card-title">{t("terrainEditor.riverListTitle", { defaultValue: "作成済みの川" })}</div>
                  <div className="terrain-editor-section">
                    {riverList.length === 0 ? (
                      <div className="terrain-editor-hint">
                        {t("terrainEditor.riverListEmpty", { defaultValue: "作成済みの川はありません" })}
                      </div>
                    ) : (
                      <>
                        <div className="terrain-editor-hint">
                          {t("terrainEditor.riverDeleteHint", {
                            defaultValue:
                              "削除は川のデータ(水面・保存対象)のみで、彫り込み済みの地形は戻しません。地形も戻す場合は Ctrl+Z を使ってください",
                          })}
                        </div>
                        <div className="terrain-editor-param-row">
                          <span className="terrain-editor-param-label">{t("terrainEditor.riverRouteRadius")}</span>
                          <NumberStepper value={riverRouteRadius} onChange={setRiverRouteRadius} min={10} max={500} step={10} />
                        </div>
                        <div className="terrain-editor-hint">{t("terrainEditor.riverRouteHint")}</div>
                        {/* Carving preview and water visibility toggles, affecting display only without changing real data. */}
                        <div className="terrain-editor-param-row">
                          <span className="terrain-editor-param-label">
                            {t("terrainEditor.riverPreviewToggle", { defaultValue: "彫り込みプレビュー(未彫り込みの川を今の深さで表示)" })}
                          </span>
                          <input
                            type="checkbox"
                            checked={riverPreviewEnabled}
                            onChange={(e) => handleRiverPreviewToggle(e.target.checked)}
                            disabled={status !== "ready"}
                          />
                        </div>
                        <div className="terrain-editor-param-row">
                          <span className="terrain-editor-param-label">
                            {t("terrainEditor.riverWaterToggle", { defaultValue: "水面を表示(OFFで川底の地形を見る)" })}
                          </span>
                          <input
                            type="checkbox"
                            checked={riverWaterVisible}
                            onChange={(e) => handleRiverWaterVisibleToggle(e.target.checked)}
                            disabled={status !== "ready"}
                          />
                        </div>
                        <div className="terrain-editor-hint">
                          {t("terrainEditor.riverSelectHint", {
                            defaultValue: "行をクリックすると地図上の同じ番号の川が強調されます。深さ係数を変えるとプレビューが更新され、「確定して彫る」で実際に地形へ彫り込みます",
                          })}
                        </div>
                        {riverList.map((river, idx) => (
                          <div
                            key={river.id}
                            className={`terrain-editor-param-group terrain-editor-river-row${river.id === selectedRiverId ? " is-selected" : ""}`}
                          >
                            {/* Select by clicking the heading row only, so stepper/button actions do not change selection. */}
                            <div className="terrain-editor-param-row terrain-editor-river-row-head" onClick={() => selectRiver(river.id)}>
                              <span className="terrain-editor-param-label">
                                {river.carved
                                  ? t("terrainEditor.riverListItemCarved", {
                                      defaultValue: `川${idx + 1}: ${river.lengthKm.toFixed(1)}km / 範囲${(river.areaHa ?? 0).toFixed(1)}ha`,
                                      index: idx + 1,
                                      lengthKm: river.lengthKm.toFixed(1),
                                      areaHa: (river.areaHa ?? 0).toFixed(1),
                                    })
                                  : t("terrainEditor.riverListItem", {
                                      defaultValue: `川${idx + 1}: ${river.lengthKm.toFixed(1)}km / 幅${Math.round(river.minWidth)}〜${Math.round(river.maxWidth)}m`,
                                      index: idx + 1,
                                      lengthKm: river.lengthKm.toFixed(1),
                                      minWidth: Math.round(river.minWidth),
                                      maxWidth: Math.round(river.maxWidth),
                                    })}
                              </span>
                              <span className="terrain-editor-selection-status">
                                {river.carved
                                  ? river.areaHa === null
                                    ? t("terrainEditor.riverStatusNoPolygon", { defaultValue: "範囲なし" })
                                    : t("terrainEditor.riverStatusCarved", { defaultValue: "彫り込み済み" })
                                  : t("terrainEditor.riverStatusUncarved", { defaultValue: "未彫り込み" })}
                              </span>
                            </div>
                            {/* Adjust each uncarved river's depth coefficient before carving. */}
                            {!river.carved && (
                              <div className="terrain-editor-param-row">
                                <span className="terrain-editor-param-label">
                                  {t("terrainEditor.riverDepthCoefOne", { defaultValue: "深さ係数" })}
                                </span>
                                <NumberStepper
                                  value={river.depthCoef}
                                  onChange={(v) => setRiverDepthCoef(river.id, v)}
                                  min={0.1}
                                  max={30}
                                  step={0.1}
                                  integerOnly={false}
                                />
                              </div>
                            )}
                            {/* Manually drawn rivers allow per-river upstream/downstream width changes, rebuilding their courses. */}
                            {!river.carved && river.manualWidths && (
                              <>
                                <div className="terrain-editor-param-row">
                                  <span className="terrain-editor-param-label">
                                    {t("terrainEditor.riverManualWidthHead", { defaultValue: "上流端の川幅(m)" })}
                                  </span>
                                  <NumberStepper
                                    value={river.manualWidths.head}
                                    onChange={(v) => setManualRiverWidth(river.id, "head", v)}
                                    min={0}
                                    max={500}
                                    step={1}
                                    integerOnly={false}
                                  />
                                </div>
                                <div className="terrain-editor-param-row">
                                  <span className="terrain-editor-param-label">
                                    {t("terrainEditor.riverManualWidthTail", { defaultValue: "下流端の川幅(m)" })}
                                  </span>
                                  <NumberStepper
                                    value={river.manualWidths.tail}
                                    onChange={(v) => setManualRiverWidth(river.id, "tail", v)}
                                    min={0}
                                    max={500}
                                    step={1}
                                    integerOnly={false}
                                  />
                                </div>
                              </>
                            )}
                            {/* Mouth type and node count of uncarved rivers, indicating node-editing availability. */}
                            {!river.carved && (
                              <div className="terrain-editor-param-row">
                                <span className="terrain-editor-param-label">
                                  {t("terrainEditor.riverMouthLabel", { defaultValue: "河口" })}
                                </span>
                                <span className="terrain-editor-selection-status">
                                  {river.mouth === "sea"
                                    ? t("terrainEditor.riverMouthSea", { defaultValue: "海" })
                                    : river.mouth === "lake"
                                      ? t("terrainEditor.riverMouthLake", { defaultValue: "湖" })
                                      : river.mouth === "confluence"
                                        ? t("terrainEditor.riverMouthConfluence", { defaultValue: "他の川へ合流" })
                                        : t("terrainEditor.riverMouthEdge", { defaultValue: "接続なし" })}
                                  {river.editable
                                    ? ` / ${t("terrainEditor.riverNodeCount", { defaultValue: `ノード${river.nodeCount}`, count: river.nodeCount })}`
                                    : ""}
                                </span>
                              </div>
                            )}
                            <div className="terrain-editor-action-buttons">
                              {river.editable && (
                                <button type="button" className="terrain-editor-btn" disabled={status !== "ready"}
                                  onClick={() => fitRiverRoute(river.id)}>
                                  {t("terrainEditor.riverFitRoute")}
                                </button>
                              )}
                              {!river.carved && (
                                <button
                                  type="button"
                                  className="terrain-editor-btn terrain-editor-btn-confirm"
                                  disabled={status !== "ready"}
                                  onClick={() => carveOneRiver(river.id)}
                                >
                                  {t("terrainEditor.riverCarveOne", { defaultValue: "確定して彫る" })}
                                </button>
                              )}
                              <button
                                type="button"
                                className="terrain-editor-btn"
                                onClick={() => removeRiver(river.id)}
                              >
                                {t("terrainEditor.riverDeleteOne", { defaultValue: "削除" })}
                              </button>
                            </div>
                          </div>
                        ))}
                        <div className="terrain-editor-action-buttons">
                          {riverList.some((river) => !river.carved) && (
                            <button
                              type="button"
                              className="terrain-editor-btn"
                              disabled={status !== "ready"}
                              onClick={carveAllUncarvedRivers}
                            >
                              {t("terrainEditor.riverCarveAll", { defaultValue: "未彫り込みをすべて彫る" })}
                            </button>
                          )}
                          {riverList.some((river) => river.carved) && (
                            <button
                              type="button"
                              className="terrain-editor-btn"
                              disabled={status !== "ready"}
                              onClick={handleRecomputeRiverPolygons}
                              title={t("terrainEditor.riverRecomputePolygonHint", {
                                defaultValue:
                                  "彫り込み済みの川の範囲(水面より低い領域)を現在の地形から取り直します。彫った後に岸を手作業で編集したときに使います",
                              })}
                            >
                              {t("terrainEditor.riverRecomputePolygon", { defaultValue: "川の範囲を再計算" })}
                            </button>
                          )}
                          <button type="button" className="terrain-editor-btn" onClick={clearAllRivers}>
                            {t("terrainEditor.riverDeleteAll", { defaultValue: "すべての川を削除" })}
                          </button>
                        </div>
                      </>
                    )}
                  </div>
                </div>
              </>
            )}

            {/* Manual editing card */}
            {activeTab === "terrain" && (
              <div className="terrain-editor-card">
              <div className="terrain-editor-card-title">{t("terrainEditor.editTitle", { defaultValue: "手動編集" })}</div>

              {/* Control guidance. Mouse buttons have exclusive actions. */}
              <div className="terrain-editor-section">
                <div className="terrain-editor-hint">
                  {t("terrainEditor.editHint", {
                    defaultValue: "左クリックで範囲に単発適用、左ドラッグで画面移動、右クリックでポリゴンを確定",
                  })}
                </div>
              </div>

              {/* Selection method */}
              <div className="terrain-editor-section">
                <div className="terrain-editor-section-title">{t("terrainEditor.shapeTitle", { defaultValue: "範囲指定方法" })}</div>
                <div className="terrain-editor-toggle-group">
                  <button
                    type="button"
                    className={`terrain-editor-toggle-btn ${shape === "circle" ? "selected" : ""}`}
                    onClick={() => setShape("circle")}
                  >
                    {t("terrainEditor.shapeCircle", { defaultValue: "円ブラシ" })}
                  </button>
                  <button
                    type="button"
                    className={`terrain-editor-toggle-btn ${shape === "rect" ? "selected" : ""}`}
                    onClick={() => setShape("rect")}
                  >
                    {t("terrainEditor.shapeRect", { defaultValue: "四角ブラシ" })}
                  </button>
                  <button
                    type="button"
                    className={`terrain-editor-toggle-btn ${shape === "polygon" ? "selected" : ""}`}
                    onClick={() => setShape("polygon")}
                  >
                    {t("terrainEditor.shapePolygon", { defaultValue: "任意ポリゴン" })}
                  </button>
                </div>

                {/* Boundary blending and feather width are shared by brush/polygon selections.
                    Interior weight is always 1; feathering extends outward by the specified width. */}
                <div className="terrain-editor-param-row">
                  <span className="terrain-editor-param-label">{t("terrainEditor.falloffTitle", { defaultValue: "周辺のなじませ方" })}</span>
                  <select
                    className="terrain-editor-select"
                    value={selectionFalloff}
                    onChange={(e) => setSelectionFalloff(e.target.value as FalloffKind)}
                  >
                    <option value="linear">{t("terrainEditor.falloffLinear", { defaultValue: "線形補間" })}</option>
                    <option value="smooth">{t("terrainEditor.falloffSmooth", { defaultValue: "滑らかな補間" })}</option>
                    <option value="gaussian">{t("terrainEditor.falloffGaussian", { defaultValue: "ガウス減衰" })}</option>
                  </select>
                </div>
                <div className="terrain-editor-param-row">
                  <span className="terrain-editor-param-label">{t("terrainEditor.featherWidth", { defaultValue: "境界のぼかし幅" })}</span>
                  <NumberStepper value={featherWidth} onChange={setFeatherWidth} min={0} max={300} step={5} />
                </div>

                {shape !== "polygon" ? (
                  <div className="terrain-editor-param-group">
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.brushSize", { defaultValue: "サイズ" })}</span>
                      <NumberStepper
                        value={brushParams.size}
                        onChange={(v) => setBrushParams((p) => ({ ...p, size: v }))}
                        min={1}
                        max={500}
                        step={5}
                      />
                    </div>
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.brushAspect", { defaultValue: "縦横比" })}</span>
                      <NumberStepper
                        value={brushParams.aspect}
                        onChange={(v) => setBrushParams((p) => ({ ...p, aspect: v }))}
                        min={0.1}
                        max={1}
                        step={0.05}
                        integerOnly={false}
                      />
                    </div>
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.brushAngle", { defaultValue: "角度" })}</span>
                      <NumberStepper
                        value={brushParams.angleDeg}
                        onChange={(v) => setBrushParams((p) => ({ ...p, angleDeg: v }))}
                        min={0}
                        max={359}
                        step={5}
                      />
                    </div>
                  </div>
                ) : (
                  <div className="terrain-editor-param-group">
                    <div className="terrain-editor-hint">
                      {t("terrainEditor.polygonHint", {
                        defaultValue: "キャンバス上でクリックして頂点を追加し、右クリックまたは「作成完了」ボタンでポリゴンを閉じてください。その後、ぼかし幅を設定して「確定」ボタンで選択に追加します(3点以上)",
                      })}
                    </div>
                    <div className="terrain-editor-polygon-count">
                      {t("terrainEditor.polygonVertexCount", { defaultValue: `頂点数: ${polygonPointCount}`, count: polygonPointCount })}
                    </div>
                    <div className="terrain-editor-action-buttons">
                      {!polygonClosed ? (
                        <>
                          <button
                            type="button"
                            className="terrain-editor-btn terrain-editor-btn-confirm"
                            disabled={polygonPointCount < 3}
                            onClick={() => setPolygonClosed(true)}
                          >
                            {t("terrainEditor.polygonComplete", { defaultValue: "作成完了" })}
                          </button>
                          <button
                            type="button"
                            className="terrain-editor-btn"
                            disabled={polygonPointCount === 0}
                            onClick={clearPolygonPoints}
                          >
                            {t("terrainEditor.polygonClear", { defaultValue: "クリア" })}
                          </button>
                        </>
                      ) : (
                        <>
                          <button
                            type="button"
                            className="terrain-editor-btn terrain-editor-btn-confirm"
                            onClick={confirmPolygonSelection}
                          >
                            {t("terrainEditor.polygonConfirm", { defaultValue: "確定" })}
                          </button>
                          <button
                            type="button"
                            className="terrain-editor-btn"
                            onClick={() => setPolygonClosed(false)}
                          >
                            {t("terrainEditor.polygonBack", { defaultValue: "作り直す" })}
                          </button>
                          <button
                            type="button"
                            className="terrain-editor-btn"
                            onClick={clearPolygonPoints}
                          >
                            {t("terrainEditor.polygonClear", { defaultValue: "クリア" })}
                          </button>
                        </>
                      )}
                    </div>
                  </div>
                )}

                {/* Selection status and reset */}
                <div className="terrain-editor-param-group">
                  <div className="terrain-editor-param-row">
                    <span className="terrain-editor-param-label">{t("terrainEditor.selectionStatusTitle", { defaultValue: "選択範囲" })}</span>
                    <span className="terrain-editor-selection-status">
                      {hasSelection
                        ? t("terrainEditor.selectionActive", { defaultValue: "選択あり" })
                        : t("terrainEditor.selectionEmpty", { defaultValue: "未選択" })}
                    </span>
                  </div>
                  <div className="terrain-editor-action-buttons">
                    <button
                      type="button"
                      className="terrain-editor-btn"
                      disabled={!hasSelection && polygonPointCount === 0}
                      onClick={resetSelection}
                    >
                      {t("terrainEditor.resetSelection", { defaultValue: "選択範囲をリセット" })}
                    </button>
                  </div>
                </div>
              </div>

              {/* Editing method */}
              <div className="terrain-editor-section">
                <div className="terrain-editor-section-title">{t("terrainEditor.operationTitle", { defaultValue: "編集方法" })}</div>
                <div className="terrain-editor-toggle-group terrain-editor-toggle-group-vertical">
                  <button
                    type="button"
                    className={`terrain-editor-toggle-btn ${operation === "raiseLower" ? "selected" : ""}`}
                    onClick={() => setOperation("raiseLower")}
                  >
                    {t("terrainEditor.opRaiseLower", { defaultValue: "標高の上下" })}
                  </button>
                  <button
                    type="button"
                    className={`terrain-editor-toggle-btn ${operation === "flatten" ? "selected" : ""}`}
                    onClick={() => setOperation("flatten")}
                  >
                    {t("terrainEditor.opFlatten", { defaultValue: "指定標高に近づける" })}
                  </button>
                  <button
                    type="button"
                    className={`terrain-editor-toggle-btn ${operation === "noise" ? "selected" : ""}`}
                    onClick={() => setOperation("noise")}
                  >
                    {t("terrainEditor.opNoise", { defaultValue: "ノイズを加える" })}
                  </button>
                </div>

                {operation === "raiseLower" && (
                  <div className="terrain-editor-param-group">
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">
                        {t("terrainEditor.raiseLowerStrength", { defaultValue: "強さ(+で上昇/-で下降)" })}
                      </span>
                      <NumberStepper
                        value={raiseLowerParams.rate}
                        onChange={(v) => setRaiseLowerParams((p) => ({ ...p, rate: v }))}
                        min={-200}
                        max={200}
                        step={1}
                      />
                    </div>
                  </div>
                )}

                {operation === "flatten" && (
                  <div className="terrain-editor-param-group">
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.flattenTarget", { defaultValue: "目標標高" })}</span>
                      <NumberStepper
                        value={flattenParams.target}
                        onChange={(v) => setFlattenParams((p) => ({ ...p, target: v }))}
                        min={-100}
                        max={3000}
                        step={1}
                      />
                    </div>
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.flattenStrength", { defaultValue: "近づける強さ" })}</span>
                      <NumberStepper
                        value={flattenParams.rate}
                        onChange={(v) => setFlattenParams((p) => ({ ...p, rate: v }))}
                        min={0.05}
                        max={2}
                        step={0.05}
                        integerOnly={false}
                      />
                    </div>
                  </div>
                )}

                {operation === "noise" && (
                  <div className="terrain-editor-param-group">
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.noiseAlgorithm", { defaultValue: "アルゴリズム" })}</span>
                      <select
                        className="terrain-editor-select"
                        value={noiseParams.algorithm}
                        onChange={(e) => setNoiseParams((p) => ({ ...p, algorithm: e.target.value as NoiseAlgorithm }))}
                      >
                        <option value="white">{t("terrainEditor.noiseAlgoWhite", { defaultValue: "ホワイトノイズ" })}</option>
                        <option value="fractal">{t("terrainEditor.noiseAlgoFractal", { defaultValue: "フラクタルノイズ" })}</option>
                      </select>
                    </div>
                    {noiseParams.algorithm === "fractal" && (
                      <>
                        <div className="terrain-editor-param-row">
                          <span className="terrain-editor-param-label">{t("terrainEditor.noiseScale", { defaultValue: "ノイズの大きさ" })}</span>
                          <NumberStepper
                            value={noiseParams.scale}
                            onChange={(v) => setNoiseParams((p) => ({ ...p, scale: v }))}
                            min={1}
                            max={500}
                            step={5}
                          />
                        </div>
                        <div className="terrain-editor-param-row">
                          <span className="terrain-editor-param-label">{t("terrainEditor.noiseOctaves", { defaultValue: "重ね合わせ回数" })}</span>
                          <NumberStepper
                            value={noiseParams.octaves}
                            onChange={(v) => setNoiseParams((p) => ({ ...p, octaves: v }))}
                            min={1}
                            max={8}
                            step={1}
                          />
                        </div>
                      </>
                    )}
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.noiseAmplitude", { defaultValue: "強さ" })}</span>
                      <NumberStepper
                        value={noiseParams.amplitude}
                        onChange={(v) => setNoiseParams((p) => ({ ...p, amplitude: v }))}
                        min={0}
                        max={200}
                        step={1}
                      />
                    </div>
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.noiseBias", { defaultValue: "偏り" })}</span>
                      <select
                        className="terrain-editor-select"
                        value={noiseParams.bias}
                        onChange={(e) => setNoiseParams((p) => ({ ...p, bias: e.target.value as NoiseBias }))}
                      >
                        <option value="dig">{t("terrainEditor.noiseBiasDig", { defaultValue: "凹ませる" })}</option>
                        <option value="both">{t("terrainEditor.noiseBiasBoth", { defaultValue: "両方" })}</option>
                        <option value="bulge">{t("terrainEditor.noiseBiasBulge", { defaultValue: "膨らませる" })}</option>
                      </select>
                    </div>
                  </div>
                )}

                <div className="terrain-editor-param-group">
                  {!hasSelection && (
                    <div className="terrain-editor-hint">{t("terrainEditor.noSelectionHint", { defaultValue: "先に範囲を選択してください" })}</div>
                  )}
                  <div className="terrain-editor-action-buttons">
                    <button
                      type="button"
                      className="terrain-editor-btn terrain-editor-btn-confirm"
                      disabled={!hasSelection}
                      onClick={applyToSelection}
                    >
                      {t("terrainEditor.applySelection", { defaultValue: "適用" })}
                    </button>
                  </div>
                </div>
              </div>
            </div>
          )}
          </div>
        </div>
        <div className="terrain-editor-save-actions">
          {hasUnsavedChanges && (
            <div className="terrain-editor-hint terrain-editor-unsaved" role="status">
              {t("terrainEditor.unsavedChanges")}
            </div>
          )}
          <div className="terrain-editor-action-buttons">
            <button
              type="button"
              className="terrain-editor-btn terrain-editor-btn-confirm"
              disabled={status !== "ready" || saveStatus === "saving"}
              onClick={() => void handleSaveToScenario()}
            >
              {saveStatus === "saving" ? t("terrainEditor.saving") : t("terrainEditor.saveToScenario")}
            </button>
          </div>
          {saveStatus === "saved" && !hasUnsavedChanges && (
            <div className="terrain-editor-hint terrain-editor-hint--success">{t("terrainEditor.saveSuccess")}</div>
          )}
          {saveStatus === "error" && (
            <div className="terrain-editor-hint terrain-editor-hint--error" role="alert">
              {saveErrorMsg || t("terrainEditor.saveFailed")}
            </div>
          )}
        </div>
      </aside>

      {/* Canvas area, filled with flex:1 to cover the display. */}
      <div className="terrain-editor-canvas-wrap">
        {status === "loading" && (
          <div className="terrain-editor-msg">
            {t("terrainEditor.loadingTerrain", { defaultValue: "地形データを読み込んでいます..." })}
          </div>
        )}
        {status === "error" && (
          <div className="terrain-editor-msg terrain-editor-msg--error">
            {errorMsg || t("terrainEditor.loadFailed", { defaultValue: "地形データの読み込みに失敗しました" })}
          </div>
        )}
        <canvas ref={terrainGlCanvasRef} className="terrain-editor-canvas terrain-editor-canvas--terrain" />
        <canvas ref={canvasRef} className="terrain-editor-canvas terrain-editor-canvas--vectors" />
        <canvas ref={cursorCanvasRef} className="terrain-editor-canvas terrain-editor-canvas--cursor" />
        <div className="terrain-editor-coord-overlay" ref={coordOverlayRef}>---, ---</div>
        <div className="terrain-editor-zoom-overlay" ref={zoomOverlayRef}>100%</div>
      </div>
    </div>
  );
}
