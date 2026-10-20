/**
 * Helpers for Import Terrain in scenario management.
 *
 * Send elevation files (grayscale PNG / .npy) as raw bytes to POST /api/v1/scenarios/from-heightmap, then render
 * a plan view of the created custom scenario's terrain as its image. Files may be anywhere on the PC.
 * The core (terrain_import) owns conversion and validation; this code only prechecks input and estimates
 * dimensions.
 * Optionally include background terrain outside the city (.npy). Read selected files through POST
 * /api/v1/scenarios/heightmap-preview, which downsamples elevations using the same interpretation as import, for
 * a pre-import preview.
 */
import { buildApiUrl, fetchApi } from "../../utils/api";
import { uploadScenarioThumbnail } from "../../utils/saves";
import { base64ToUint8, decodeFloat16Array } from "../terrain/terrainData";
import { renderTerrainThumbnail } from "../terrain/terrainThumbnail";

export type HeightmapFormat = "png" | "npy";

/** Elevation file format and pixel dimensions (east–west columns × north–south rows). Unreadable fields are null. */
export type HeightmapInfo = { format: HeightmapFormat | null; columns: number | null; rows: number | null };

/** Background terrain outside the city (.npy). Pixel spacing is separate from detailed terrain; only sea level is shared. */
export type BackgroundImportRequest = { file: File; spacing: number };

/** Import input. Only PNG uses min/max; .npy values are elevations directly. */
export type HeightmapImportRequest = {
  file: File;
  spacing: number;
  minHeight: number;
  maxHeight: number;
  seaLevel: number;
  title: string;
  background: BackgroundImportRequest | null;
};

/** Copy of the core's value ranges (HeightmapImport / terrain_import). The core rejects out-of-range values with 422. */
export const HEIGHTMAP_LIMITS = {
  maxSpacing: 1000, maxAbsHeight: 10000, maxMapSize: 30000, maxBackgroundSize: 100000, titleMaxLength: 160,
} as const;
const SIZE_QUANTUM = 1000;
/** Background terrain grid spacing in meters, matching background_terrain.SPACING in the core. Inputs at any spacing are resampled to this grid. */
export const BACKGROUND_SPACING = 50;

const PNG_SIGNATURE = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
const NPY_MAGIC = [0x93, 0x4e, 0x55, 0x4d, 0x50, 0x59];
/** .npy headers are normally a few hundred bytes. Cap length to prevent huge reads from corrupt headers. */
const NPY_HEADER_LIMIT = 1 << 20;

const startsWith = (bytes: Uint8Array, prefix: number[]) => prefix.every((value, index) => bytes[index] === value);

/** Infer format from the extension before reading content. The core detects it from the initial bytes. */
export function heightmapFormatOfName(name: string | undefined): HeightmapFormat | null {
  const extension = name?.toLowerCase().match(/\.([a-z0-9]+)$/)?.[1];
  return extension === "png" || extension === "npy" ? extension : null;
}

/**
 * Read pixel dimensions from initial bytes: PNG IHDR width/height (always the first chunk), or .npy header
 * shape.
 * Avoid decoding the entire image, keeping large 16-bit images inexpensive.
 */
export async function readHeightmapInfo(file: Blob & { name?: string }): Promise<HeightmapInfo> {
  const head = new Uint8Array(await file.slice(0, 24).arrayBuffer());
  if (startsWith(head, PNG_SIGNATURE)) {
    const view = new DataView(head.buffer, head.byteOffset, head.byteLength);
    const ihdr = head.length >= 24 && String.fromCharCode(...head.subarray(12, 16)) === "IHDR";
    return { format: "png", columns: ihdr ? view.getUint32(16) : null, rows: ihdr ? view.getUint32(20) : null };
  }
  if (startsWith(head, NPY_MAGIC) && head.length >= 12) {
    const view = new DataView(head.buffer, head.byteOffset, head.byteLength);
    // Version 1 uses a 2-byte header length; version 2 onward uses 4 bytes, both little-endian.
    const [offset, length] = head[6] === 1 ? [10, view.getUint16(8, true)] : [12, view.getUint32(8, true)];
    if (length > NPY_HEADER_LIMIT) return { format: "npy", columns: null, rows: null };
    const header = new TextDecoder().decode(await file.slice(offset, offset + length).arrayBuffer());
    const shape = header.match(/['"]shape['"]\s*:\s*\(([^)]*)\)/)?.[1]
      .split(",").map(value => value.trim()).filter(Boolean).map(Number);
    // A 2D array in meters with row 0 to the south, so shape is (north–south pixels, east–west pixels).
    return shape?.length === 2 && shape.every(Number.isInteger)
      ? { format: "npy", columns: shape[1], rows: shape[0] }
      : { format: "npy", columns: null, rows: null };
  }
  return { format: heightmapFormatOfName(file.name), columns: null, rows: null };
}

/** Generated map side length in meters. As in terrain_import._map_extent, round the source extent up to kilometers, at least 1km. */
export function importedMapExtent(pixels: number, spacing: number): number {
  return Math.max(SIZE_QUANTUM, Math.ceil((pixels - 1) * spacing / SIZE_QUANTUM - 1e-9) * SIZE_QUANTUM);
}

/**
 * Background terrain side length in meters. As in terrain_import.background_from_heightmap, round the source
 * extent to pixel dimensions on a 50m grid.
 * Unlike detailed terrain, do not round up to kilometers.
 */
export function importedBackgroundExtent(pixels: number, spacing: number): number {
  return Math.round((pixels - 1) * spacing / BACKGROUND_SPACING) * BACKGROUND_SPACING;
}

/**
 * Create a custom scenario from an elevation file. Response id includes the storage source (local~...).
 * Append background terrain to the request body and pass its length as background_size; the core does not use
 * multipart.
 */
export async function importHeightmap(apiBaseUrl: string, request: HeightmapImportRequest, language: string, format: HeightmapFormat | null) {
  const query = new URLSearchParams({ spacing: String(request.spacing), sea_level: String(request.seaLevel) });
  if (format !== "npy") {
    query.set("min_height", String(request.minHeight));
    query.set("max_height", String(request.maxHeight));
  }
  if (request.background) {
    query.set("background_spacing", String(request.background.spacing));
    query.set("background_size", String(request.background.file.size));
  }
  query.set("title", request.title);
  query.set("language", language);
  query.set("source_name", request.file.name);
  return fetchApi<{ id: string; map_width: number; map_height: number }>(
    buildApiUrl(apiBaseUrl, `/api/v1/scenarios/from-heightmap?${query}`),
    {
      method: "POST", headers: { "Content-Type": "application/octet-stream" },
      body: request.background ? new Blob([request.file, request.background.file]) : request.file,
    },
  );
}

/**
 * Downsampled elevations for preview, row 0 south. sourceRows/sourceCols are original dimensions; extent spans
 * sourceCols-1 intervals.
 * minHeight/maxHeight are the original file's elevation extrema before subtracting sea level, displayed as the
 * .npy elevation range.
 */
export type HeightmapPreviewGrid = {
  elev: Float32Array; rows: number; cols: number; sourceRows: number; sourceCols: number; minHeight: number; maxHeight: number;
};

/** Elevation file interpretation. Only PNG uses min/max; seaLevel is subtracted from elevations as during import. */
export type HeightmapReading = { minHeight: number; maxHeight: number; seaLevel: number };

/**
 * POST /api/v1/scenarios/heightmap-preview. Interpret elevations as for import, then return downsampled values
 * without creating anything.
 * Background accepts only .npy, so omit min/max.
 */
export async function fetchHeightmapPreview(
  apiBaseUrl: string, file: Blob, reading: HeightmapReading,
  options: { format: HeightmapFormat | null; background?: boolean; signal?: AbortSignal },
): Promise<HeightmapPreviewGrid> {
  const query = new URLSearchParams({ sea_level: String(reading.seaLevel) });
  if (options.background) query.set("background", "true");
  else if (options.format !== "npy") {
    query.set("min_height", String(reading.minHeight));
    query.set("max_height", String(reading.maxHeight));
  }
  const response = await fetchApi<{
    source_rows: number; source_cols: number; rows: number; cols: number; min_height: number; max_height: number; heights_base64: string;
  }>(
    buildApiUrl(apiBaseUrl, `/api/v1/scenarios/heightmap-preview?${query}`),
    { method: "POST", headers: { "Content-Type": "application/octet-stream" }, body: file, signal: options.signal },
  );
  const bytes = base64ToUint8(response.heights_base64);
  if (bytes.length !== response.rows * response.cols * 2) throw new Error(`Preview size does not match the grid (${response.cols}×${response.rows})`);
  return {
    elev: decodeFloat16Array(bytes, response.rows * response.cols), rows: response.rows, cols: response.cols,
    sourceRows: response.source_rows, sourceCols: response.source_cols, minHeight: response.min_height, maxHeight: response.max_height,
  };
}

/** Fields from GET /api/v1/scenarios/{id}/terrain used for the plan view. Elevations are base64 float16 LE, with row 0 south. */
type ScenarioTerrainResponse = {
  settings: { map_width: number; map_height: number; near_spacing: number; grid_rows: number; grid_cols: number };
  near_base64: string;
};

/** Render a plan view from scenario terrain (north up, sea transparent) and register it as the scenario image. */
export async function registerTerrainThumbnail(apiBaseUrl: string, scenarioId: string): Promise<void> {
  const terrain = await fetchApi<ScenarioTerrainResponse>(
    buildApiUrl(apiBaseUrl, `/api/v1/scenarios/${encodeURIComponent(scenarioId)}/terrain`), { cache: "no-store" },
  );
  const { map_width, map_height, near_spacing, grid_rows, grid_cols } = terrain.settings;
  const bytes = base64ToUint8(terrain.near_base64);
  if (bytes.length !== grid_rows * grid_cols * 2) throw new Error(`Elevation size does not match the grid (${grid_cols}×${grid_rows})`);
  const image = renderTerrainThumbnail({
    elev: decodeFloat16Array(bytes, grid_rows * grid_cols), rows: grid_rows, cols: grid_cols,
    mapWidth: map_width, mapHeight: map_height, spacing: near_spacing,
  });
  await uploadScenarioThumbnail(apiBaseUrl, scenarioId, await encodePng(image));
}

async function encodePng({ width, height, pixels }: { width: number; height: number; pixels: Uint8ClampedArray }): Promise<Blob> {
  const canvas = document.createElement("canvas");
  canvas.width = width;
  canvas.height = height;
  const context = canvas.getContext("2d");
  if (!context) throw new Error("Canvas 2D is unavailable");
  context.putImageData(new ImageData(pixels, width, height), 0, 0);
  return new Promise((resolve, reject) => canvas.toBlob(blob => blob ? resolve(blob) : reject(new Error("PNG encoding failed")), "image/png"));
}
