import type { LocalizedText } from "../../utils/locale";
import type { ClimateSettings } from "../../utils/climate";
import {
  GROUND_COVER_DESERT,
  GROUND_COVER_GRASSLAND,
  GROUND_COVER_LUSH,
  GROUND_COVER_SEMI_ARID,
  GROUND_COVER_SNOW,
  GROUND_COVER_TYPES,
} from "../../constants";

/**
 * Shared terrain data encoding and color mapping logic.
 *
 * near.bin elevation data is exchanged with Tauri commands as base64-encoded little-endian
 * float16 (IEEE 754 half) bytes. Both the 2D terrain editor (TerrainEditorCanvas) and 3D preview
 * (Terrain3DPreviewCanvas) need this encoding and color mapping, so it is shared here.
 */

/** GeoJSON-like polygon in world XZ coordinates. coordinates is [exterior, holes...] for Polygon,
 *  or [[exterior, holes...], ...] for MultiPolygon. */
export type WaterPolygonGeoJson = {
  type: "Polygon" | "MultiPolygon";
  coordinates: number[][][] | number[][][][];
};

/**
 * Definition of one non-sea water surface (lakes, etc.).
 * Shared contract across the backend's /terrain/water, Scenario terrain API, terrain editor
 * work copy (meta.json), and Unity WaterSurfaceManager.
 */
export type WaterSurfacePayload = {
  id: string;
  /** Water surface Y elevation (meters), using the same absolute frame as heightmap (sea level = 0). */
  water_level: number;
  /** Optional type label for future appearance variations, e.g. "lake". */
  kind?: string;
  polygon: WaterPolygonGeoJson;
};

/**
 * Storage contract for one river.
 *
 * Shared by terrain editor work copies (meta.json), the Scenario terrain API, backend
 * (GET /terrain/rivers), and Unity rendering. Stored separately from WaterSurfacePayload:
 * lakes use a horizontal plane with one water_level, while river elevations change downstream,
 * making the contracts incompatible.
 *
 * Rivers are defined by an area (polygon) and a water elevation profile line (profile).
 * The spline used to carve the riverbed (course, width, depth) is intermediate editor data
 * whose purpose ends once baked into elevations. The game only needs to know which area
 * is a river and the water elevation at each point, so only those are saved.
 * The authoritative specification is D.8 in city-genesis/docs/ja/geometries/D_地形形状.md.
 */
export type RiverPayload = {
  id: string;
  /** Type label, always "river". */
  kind: string;
  /** River area extracted from terrain grid regions below water elevation after carving.
   *  Sandbars/islands become holes (interior rings). Uses the same coordinate system as water
   *  surface polygon (world XZ, map center at (0,0)). This shape is shared by construction
   *  exclusion (coastline holes), Path crossing tests, and water surface mesh bounds. */
  polygon: WaterPolygonGeoJson;
  /** Water elevation profile line [[x, waterY, z], ...], upstream to downstream.
   *  Water elevation anywhere in the area is waterY at the nearest point on this polyline
   *  (RiverProfileSampler in riverGeometry.ts). y is absolute elevation (meters, sea level = 0),
   *  monotonically non-increasing downstream. */
  profile: number[][];
};

/**
 * Storage contract for one natural resource area.
 *
 * Shared by terrain editor work copies (meta.json) and the Scenario terrain API. id is the
 * resource DB ID as a string. On save, the core applies differences by ID (omitted resources
 * are deleted). The terrain editor does not edit resources; it sends loaded contents back unchanged.
 * For resource concepts, see city-genesis/docs/ja/economics/17_財の供給と輸送.md.
 * The authoritative specification is D.9 in city-genesis/docs/ja/geometries/D_地形形状.md.
 */
export type ResourceAreaPayload = {
  id: string;
  /** Resource type (resource_master key). The backend derives the supplied product_id from this type. */
  resource_type: number;
  /** Maximum reserves. Initial reserves equal this value (full) immediately after creation. */
  max_amount: number;
  /** Daily recovery fraction of maximum reserves. 0.0 means nonrenewable resources (mineral deposits, oil fields, etc.). */
  recovery_rate: number;
  polygon: WaterPolygonGeoJson;
};

export type TerrainPropInstance = {
  id: string; prop_type: number; x: number; y: number; z: number; rotation: number; scale: number;
};
export type TerrainPropScatter = {
  id: string; prop_type: number; polygon: number[][]; density: number; seed: number;
  edge_fade_width: number; allow_water: boolean;
};
/**
 * Storage contract for Prop placement definitions (individual placements and area scattering).
 * Exchanged through the Scenario terrain API and replaced in full on save. y is height relative
 * to the ground. The terrain editor does not edit Props; it sends loaded contents back unchanged.
 * The authoritative specification is city-genesis/docs/ja/geometries/M_地形プロップ.md.
 */
export type TerrainProps = { instances: TerrainPropInstance[]; scatters: TerrainPropScatter[] };

/**
 * Storage contract for initial camera state.
 * Shared by terrain editor work copies (meta.json), `camera` in the Scenario terrain API,
 * City.camera in the backend (GET/PATCH /api/v1/city), and Unity's
 * CameraController.CameraStatePayload. The authoritative specification is D.9 in
 * city-genesis/docs/ja/geometries/D_地形形状.md.
 */
export type TerrainCameraPayload = {
  /** Camera position in world coordinates (meters). Map center is (0,0) in x/z; y uses the absolute elevation frame. */
  x: number;
  y: number;
  z: number;
  /** View azimuth (degrees), clockwise from north (world +Z) = 0. */
  yaw: number;
  /** Downward pitch (degrees). Positive looks down (90 = straight down); 0 is horizontal. */
  pitch: number;
};

/**
 * Elevation, water surface, and river locks. Scenarios with construction (roads, buildings,
 * Earthworks traces, reclaimed land) have heights_locked; sending elevations/water/rivers on
 * save is rejected by the core with 409 TERRAIN_LOCKED. reasons contains codes
 * (PATHS_EXIST, BUILDINGS_EXIST, TERRAIN_MODIFIED, RECLAIMED_LAND).
 */
export type TerrainEditorLocks = { heights_locked: boolean; reasons: string[] };

/** Rust load_terrain_editor_data result: contents of the work copy built from the Scenario terrain API. */
export type TerrainEditorData = {
  scenario_id: string;
  /** Terrain origin (imported elevation filename or Tutorial setting name), used only for display. */
  terrain_source: string | null;
  /** Scenario title (multilingual text, displayed in the heading in the active language). */
  title: LocalizedText | null;
  locks: unknown;
  map_width: number;
  map_height: number;
  near_spacing: number;
  far_spacing: number;
  cell_size: number;
  grid_rows: number;
  grid_cols: number;
  /** Base64-encoded little-endian float16 bytes. */
  data_base64: string;
  /** List of non-sea water polygons (lakes, etc.). Empty if unset. */
  water_surfaces: WaterSurfacePayload[];
  /** River list. Empty if unset. */
  rivers: RiverPayload[];
  /** Natural resource area list. Empty if unset. */
  resources: ResourceAreaPayload[];
  props: TerrainProps;
  /** Initial camera state. null if unset. */
  camera: TerrainCameraPayload | null;
  /** Ground cover type (GROUND_COVER_*). null if unset (treated as grassland). */
  ground_cover: number | null;
  /** Climate category (CLIMATE_*). null if unset (treated as humid subtropical). */
  climate: ClimateSettings;
};

/**
 * Normalizes work-copy lock information. Treats malformed data as locked, preferring disabled
 * editing over a save rejected by the core. Reasons accept string codes or `{code}` objects.
 */
export function normalizeTerrainEditorLocks(value: unknown): TerrainEditorLocks {
  const locks = value as { heights_locked?: unknown; reasons?: unknown } | null;
  if (!locks || typeof locks !== "object" || typeof locks.heights_locked !== "boolean") {
    return { heights_locked: true, reasons: [] };
  }
  const reasons = Array.isArray(locks.reasons)
    ? locks.reasons
        .map((reason) => (typeof reason === "string" ? reason : (reason as { code?: unknown } | null)?.code))
        .filter((code): code is string => typeof code === "string" && code.length > 0)
    : [];
  return { heights_locked: locks.heights_locked, reasons };
}

/** Terrain editor contents saved to the Scenario. Resources and Props are not edited and retain their loaded contents. */
export type ScenarioTerrainContent = {
  elev: Float32Array;
  waterSurfaces: WaterSurfacePayload[];
  rivers: RiverPayload[];
  resources: ResourceAreaPayload[];
  props: TerrainProps;
  camera: TerrainCameraPayload | null;
  groundCover: number;
  climate: ClimateSettings;
};

/**
 * Arguments for Rust save_scenario_terrain. Set elevations, water surfaces, and rivers to null
 * when heights are locked (core contract: sending values causes 409 TERRAIN_LOCKED).
 * Encode elevations only when sending them.
 */
export function scenarioTerrainSaveArgs(editorId: string, heightsLocked: boolean, content: ScenarioTerrainContent) {
  return {
    editorId,
    nearBase64: heightsLocked ? null : uint8ToBase64(encodeFloat16Array(content.elev)),
    waterSurfaces: heightsLocked ? null : content.waterSurfaces,
    rivers: heightsLocked ? null : content.rivers,
    resources: content.resources,
    props: content.props,
    camera: content.camera,
    groundCover: content.groundCover,
    climate: content.climate,
  };
}

/** Converts float16 (IEEE 754 half) bits to a float32-equivalent value. */
export function decodeFloat16(bits: number): number {
  const sign = (bits & 0x8000) >> 15;
  const exp = (bits & 0x7c00) >> 10;
  const frac = bits & 0x03ff;
  if (exp === 0) {
    if (frac === 0) return sign ? -0 : 0;
    return (sign ? -1 : 1) * Math.pow(2, -14) * (frac / 1024);
  }
  if (exp === 0x1f) {
    if (frac !== 0) return NaN;
    return sign ? -Infinity : Infinity;
  }
  return (sign ? -1 : 1) * Math.pow(2, exp - 15) * (1 + frac / 1024);
}

/**
 * Lookup table mapping float16 bit patterns (0–65535) to values. Calling Math.pow for each of
 * millions of elevations takes hundreds of milliseconds, so build all patterns on first use
 * and use table lookup.
 */
let float16Table: Float32Array | null = null;

/** Converts little-endian float16 bytes to Float32Array. */
export function decodeFloat16Array(bytes: Uint8Array, count: number): Float32Array {
  if (!float16Table) {
    float16Table = new Float32Array(0x10000);
    for (let bits = 0; bits < 0x10000; bits++) float16Table[bits] = decodeFloat16(bits);
  }
  const table = float16Table;
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const out = new Float32Array(count);
  for (let i = 0; i < count; i++) {
    out[i] = table[view.getUint16(i * 2, true)];
  }
  return out;
}

/**
 * Converts float32 bits to float16 (IEEE 754 half) bits.
 * Terrain elevations normally fit normalized float16 numbers (absolute values about 6e-5–65504),
 * so omit rounding to subnormal numbers and treat extremes as 0 or ±Infinity. NaN uses unsigned 0x7e00.
 */
function float32BitsToFloat16(bits: number): number {
  const sign = (bits >>> 16) & 0x8000;
  const exp32 = (bits >>> 23) & 0xff;
  const mantissa32 = bits & 0x7fffff;
  if (exp32 === 0xff) return mantissa32 !== 0 ? 0x7e00 : sign | 0x7c00;

  let exp16 = exp32 - 127 + 15;
  if (exp16 >= 0x1f) return sign | 0x7c00; // Overflow → Infinity.
  if (exp16 <= 0) return sign; // Underflow (including ±0) → round to 0.

  // Round the 23-bit mantissa to 10 bits (add the midpoint bit before shifting right).
  let mantissa16 = (mantissa32 + 0x00001000) >>> 13;
  if (mantissa16 === 0x400) {
    // If rounding overflows the mantissa, increment the exponent.
    exp16 += 1;
    mantissa16 = 0;
    if (exp16 >= 0x1f) return sign | 0x7c00;
  }
  return sign | (exp16 << 10) | mantissa16;
}

/** Scratch space for encodeFloat16 to reinterpret a value as float32 bits. */
const scratchFloat32 = new Float32Array(1);
const scratchFloat32Bits = new Uint32Array(scratchFloat32.buffer);

/** Converts a number to float16 (IEEE 754 half) bits (inverse of decodeFloat16), rounding to float32 first. */
export function encodeFloat16(value: number): number {
  scratchFloat32[0] = value;
  return float32BitsToFloat16(scratchFloat32Bits[0]);
}

/** Converts Float32Array to little-endian float16 bytes (Uint8Array), reading the same memory as bits without copying values. */
export function encodeFloat16Array(values: Float32Array): Uint8Array {
  const bits = new Uint32Array(values.buffer, values.byteOffset, values.length);
  const out = new Uint8Array(values.length * 2);
  const view = new DataView(out.buffer);
  for (let i = 0; i < bits.length; i++) {
    view.setUint16(i * 2, float32BitsToFloat16(bits[i]), true);
  }
  return out;
}

/**
 * Native base64 implementations (Uint8Array.prototype.toBase64 / Uint8Array.fromBase64).
 * Their types are not yet in the standard library, so declare minimal shapes here and check
 * for availability before use. JS implementations take hundreds of milliseconds for several MB
 * of elevation data, whereas native implementations finish in a few milliseconds.
 */
type NativeBase64Bytes = Uint8Array & { toBase64?: () => string };
type NativeBase64Ctor = typeof Uint8Array & { fromBase64?: (base64: string) => Uint8Array };

/** Decodes a base64 string to Uint8Array. */
export function base64ToUint8(b64: string): Uint8Array {
  const ctor = Uint8Array as NativeBase64Ctor;
  if (typeof ctor.fromBase64 === "function") return ctor.fromBase64(b64);
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

/** Encodes Uint8Array as a base64 string (inverse of atob). The JS fallback uses chunks to avoid stack overflow on huge arrays. */
export function uint8ToBase64(bytes: Uint8Array): string {
  const native = bytes as NativeBase64Bytes;
  if (typeof native.toBase64 === "function") return native.toBase64();
  const chunkSize = 0x8000;
  let bin = "";
  for (let i = 0; i < bytes.length; i += chunkSize) {
    bin += String.fromCharCode(...bytes.subarray(i, i + chunkSize));
  }
  return btoa(bin);
}

export type Rgb = [number, number, number];

/** Land elevation range. Enforce at least 1 m to avoid division by zero during color normalization on flat terrain. */
export function landElevationRange(elev: Float32Array): { minLand: number; maxLand: number } {
  let minLand = Infinity;
  let maxLand = -Infinity;
  for (const e of elev) {
    if (e > 0 && Number.isFinite(e)) {
      minLand = Math.min(minLand, e);
      maxLand = Math.max(maxLand, e);
    }
  }
  if (!Number.isFinite(minLand)) return { minLand: 0, maxLand: 1 };
  return { minLand, maxLand: Math.max(maxLand, minLand + 1) };
}

/**
 * Elevation color ramps by ground cover type (GROUND_COVER_*), linearly interpolating lowland,
 * midslope, and highland colors. Summarizes Unity's GroundCoverPalette for 2D display, so the
 * terrain editor and 3D preview use the same color family for the same map.
 * Grassland uses a green lowland → brown highland ramp.
 */
const GROUND_COVER_RAMPS: Record<number, [Rgb, Rgb, Rgb]> = {
  [GROUND_COVER_GRASSLAND]: [[45, 106, 79], [140, 160, 60], [160, 100, 30]],
  [GROUND_COVER_DESERT]: [[222, 196, 146], [204, 166, 112], [158, 112, 72]],
  [GROUND_COVER_LUSH]: [[26, 84, 50], [72, 124, 52], [122, 98, 60]],
  [GROUND_COVER_SNOW]: [[208, 218, 230], [236, 240, 246], [150, 156, 166]],
  [GROUND_COVER_SEMI_ARID]: [[180, 160, 96], [168, 150, 92], [150, 112, 72]],
};

/** Sea color (elevation at or below 0), also used as the 2D terrain editor background. */
export const SEA_RGB: Rgb = [0x0d, 0x0d, 0x14];

/** Ground cover type, defaulting unset/unknown values to grassland. */
export function normalizeGroundCover(value: unknown): number {
  return typeof value === "number" && GROUND_COVER_TYPES.includes(value) ? value : GROUND_COVER_GRASSLAND;
}

/** Ground cover elevation ramp ([lowland, midslope, highland]). The terrain editor shader uses the same three colors. */
export function groundCoverRamp(groundCover: number = GROUND_COVER_GRASSLAND): [Rgb, Rgb, Rgb] {
  return GROUND_COVER_RAMPS[normalizeGroundCover(groundCover)];
}

/**
 * Gets terrain color from elevation normalized to 0..1. computePixelRGB handles sea level (elevation at or below 0).
 */
export function terrainColor(t: number, groundCover: number = GROUND_COVER_GRASSLAND): Rgb {
  const clamped = Math.max(0, Math.min(1, t));
  const [low, mid, high] = groundCoverRamp(groundCover);
  const [from, to, s2]: [Rgb, Rgb, number] = clamped < 0.5 ? [low, mid, clamped * 2] : [mid, high, (clamped - 0.5) * 2];
  return [
    Math.round(from[0] + s2 * (to[0] - from[0])),
    Math.round(from[1] + s2 * (to[1] - from[1])),
    Math.round(from[2] + s2 * (to[2] - from[2])),
  ];
}

/** Computes terrain texture RGB for one elevation. Uses SEA_RGB at or below sea level (0). */
export function computePixelRGB(
  e: number,
  minLand: number,
  maxLand: number,
  groundCover: number = GROUND_COVER_GRASSLAND
): Rgb {
  if (e <= 0) return [SEA_RGB[0], SEA_RGB[1], SEA_RGB[2]];
  const t = (e - minLand) / (maxLand - minLand);
  return terrainColor(t, groundCover);
}

/**
 * Computes vertex color with the light-blue selection highlight overlaid.
 * Matches the appearance of the 2D editor's selection overlay
 * (rgba(80,190,255, weight*160/255)) normally alpha-composited over terrain colors,
 * keeping 3D preview vertex colors consistent.
 */
export function computeVertexColor(
  elev: number,
  minLand: number,
  maxLand: number,
  maskWeight: number,
  groundCover: number = GROUND_COVER_GRASSLAND
): [number, number, number] {
  const [tr, tg, tb] = computePixelRGB(elev, minLand, maxLand, groundCover);
  const a = (Math.max(0, Math.min(1, maskWeight)) * 160) / 255;
  const hr = 80, hg = 190, hb = 255;
  return [
    Math.round(tr * (1 - a) + hr * a),
    Math.round(tg * (1 - a) + hg * a),
    Math.round(tb * (1 - a) + hb * a),
  ];
}

/** Rectangular grid region (inclusive row and column bounds). */
export type GridRect = { rMin: number; rMax: number; cMin: number; cMax: number };

/** Extracts only rect from an elevation/selection mask array in row-major order for delta transmission. */
function extractRect(src: Float32Array, cols: number, rect: GridRect): Float32Array {
  const w = rect.cMax - rect.cMin + 1;
  const h = rect.rMax - rect.rMin + 1;
  const out = new Float32Array(w * h);
  let oi = 0;
  for (let r = rect.rMin; r <= rect.rMax; r++) {
    const rowBase = r * cols;
    for (let c = rect.cMin; c <= rect.cMax; c++) out[oi++] = src[rowBase + c];
  }
  return out;
}

/** Extracts rect from the elevation array for elevation_delta events. */
export function extractElevRect(elev: Float32Array, cols: number, rect: GridRect): Float32Array {
  return extractRect(elev, cols, rect);
}

/** Extracts rect from the selection mask (0..1), quantized to 0–255, for selection_delta / full_state events. */
export function quantizeMaskRect(mask: Float32Array, cols: number, rect: GridRect): Uint8Array {
  const rectValues = extractRect(mask, cols, rect);
  const out = new Uint8Array(rectValues.length);
  for (let i = 0; i < rectValues.length; i++) {
    out[i] = Math.round(Math.max(0, Math.min(1, rectValues[i])) * 255);
  }
  return out;
}

/** Inverse of quantizeMaskRect. Writes rect back into dst (the full-size mask array). */
export function dequantizeMaskRectInto(dst: Float32Array, cols: number, rect: GridRect, quantized: Uint8Array): void {
  let oi = 0;
  for (let r = rect.rMin; r <= rect.rMax; r++) {
    const rowBase = r * cols;
    for (let c = rect.cMin; c <= rect.cMax; c++) dst[rowBase + c] = quantized[oi++] / 255;
  }
}

/** Inverse of extractElevRect. Writes rect back into dst (the full-size elevation array). */
export function applyElevRectInto(dst: Float32Array, cols: number, rect: GridRect, values: Float32Array): void {
  let vi = 0;
  for (let r = rect.rMin; r <= rect.rMax; r++) {
    const rowBase = r * cols;
    for (let c = rect.cMin; c <= rect.cMax; c++) dst[rowBase + c] = values[vi++];
  }
}

/* ============================================================================
 * Event contract between terrain editor (terrain_editor) and 3D preview (terrain_3d_preview).
 *
 * Following existing app patterns (FontSizeContext, etc.), real-time synchronization across
 * windows uses Tauri emit/listen broadcasts to all windows, filtered by mapId (the work-copy ID,
 * used as entity_id in both windows), rather than emitTo targeting a specific window.
 *
 * The editor sends state only while a preview has requested state and not detached.
 * Full state encodes all elevations and can reach tens of MB on large maps, so sending on every
 * edit when no preview is open would slow interaction. Each preview mount creates a sessionId,
 * sends request_state when shown, and detach when hidden/unmounted. Tauri event arrival order
 * is not guaranteed, so sessionId identifies which mount is attaching/detaching.
 * ========================================================================== */

/** Event for the preview to request full current state on mount or becoming visible again. */
export const EVT_TERRAIN_3D_REQUEST_STATE = "terrain_3d_preview:request_state";
/** Event indicating the preview is hidden/unmounted and no longer needs state updates. */
export const EVT_TERRAIN_3D_DETACH = "terrain_3d_preview:detach";
/** Event sending all editor elevations, selection masks, and metadata on request, load, or confirmed resize. */
export const EVT_TERRAIN_3D_FULL_STATE = "terrain_3d_preview:full_state";
/** Event sending brush/polygon selection (light-blue highlight) deltas in real time, including during dragging. */
export const EVT_TERRAIN_3D_SELECTION_DELTA = "terrain_3d_preview:selection_delta";
/** Event notifying that the entire selection was cleared. */
export const EVT_TERRAIN_3D_SELECTION_RESET = "terrain_3d_preview:selection_reset";
/** Event sending elevation deltas committed by pressing "Apply". */
export const EVT_TERRAIN_3D_ELEVATION_DELTA = "terrain_3d_preview:elevation_delta";

/** sessionId is a unique string created for each preview mount. */
export type Terrain3DRequestStatePayload = { mapId: string; sessionId: string };
export type Terrain3DDetachPayload = { mapId: string; sessionId: string };

export type Terrain3DFullStatePayload = {
  mapId: string;
  mapWidth: number;
  mapHeight: number;
  spacing: number;
  rows: number;
  cols: number;
  minLand: number;
  maxLand: number;
  /** Elevation array encoded as little-endian float16, then base64 (row-major, rows*cols elements). */
  elevBase64: string;
  /** Selection mask quantized to 0–255, then base64-encoded (row-major, rows*cols elements). */
  maskBase64: string;
  /** Non-sea water polygons (lakes, etc.), sent for drawing water planes in the 3D preview. */
  waterSurfaces: WaterSurfacePayload[];
  /** Committed rivers, sent for drawing water ribbons in the 3D preview.
   *  River changes (committed carving/deletion) also change elevations across the map, so include
   *  them in full_state rather than adding a dedicated delta event. */
  rivers: RiverPayload[];
  /** Ground cover type (GROUND_COVER_*), keeping the 3D preview's terrain vertex colors on the same ramp as the 2D editor. */
  groundCover: number;
};

export type Terrain3DSelectionDeltaPayload = GridRect & {
  mapId: string;
  /** Base64-encoded quantizeMaskRect result. */
  maskRectBase64: string;
};

export type Terrain3DSelectionResetPayload = { mapId: string };

export type Terrain3DElevationDeltaPayload = GridRect & {
  mapId: string;
  /** extractElevRect result encoded as little-endian float16, then base64. */
  elevRectBase64: string;
};
