/**
 * シナリオ管理の「地形を取り込む」の補助。
 *
 * 標高ファイル(グレースケールPNG・.npy)を POST /api/v1/scenarios/from-heightmap へ生バイトで送り、
 * 作られた自作シナリオの地形から平面図を描いてシナリオ画像にする。ファイルはPC内のどこにあってもよく、
 * 変換・検証の正はコア(terrain_import)で、ここは入力の前検査と大きさの見込みを出すだけにする。
 * 都市の外の背景地形(.npy)も一緒に送れる。選んだファイルの中身は POST /api/v1/scenarios/heightmap-preview
 * (取り込みと同じ読み方で間引いた標高)で受け取り、取り込み前のプレビューに使う。
 */
import { buildApiUrl, fetchApi } from "../../utils/api";
import { uploadScenarioThumbnail } from "../../utils/saves";
import { base64ToUint8, decodeFloat16Array, normalizeGroundCover } from "../terrain/terrainData";
import { renderTerrainThumbnail } from "../terrain/terrainThumbnail";

export type HeightmapFormat = "png" | "npy";

/** 標高ファイルの形式と画素数(東西 columns × 南北 rows)。読めない項目は null */
export type HeightmapInfo = { format: HeightmapFormat | null; columns: number | null; rows: number | null };

/** 都市の外の背景地形(.npy)。1画素あたりの距離は詳細地形とは別に持ち、海面の高さだけ詳細地形と共通 */
export type BackgroundImportRequest = { file: File; spacing: number };

/** 取り込みの入力。min/max はPNGだけが使う(.npy は値そのものが標高) */
export type HeightmapImportRequest = {
  file: File;
  spacing: number;
  minHeight: number;
  maxHeight: number;
  seaLevel: number;
  title: string;
  background: BackgroundImportRequest | null;
};

/** コアの値域(HeightmapImport・terrain_import)の写し。外れた値はコアが 422 で断る */
export const HEIGHTMAP_LIMITS = {
  maxSpacing: 1000, maxAbsHeight: 10000, maxMapSize: 30000, maxBackgroundSize: 100000, titleMaxLength: 160,
} as const;
const SIZE_QUANTUM = 1000;
/** 背景地形の格子間隔(m)。コアの background_terrain.SPACING と同じで、どんな間隔の入力もこの格子に直される */
export const BACKGROUND_SPACING = 50;

const PNG_SIGNATURE = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
const NPY_MAGIC = [0x93, 0x4e, 0x55, 0x4d, 0x50, 0x59];
/** .npy のヘッダーはふつう数百バイト。壊れた長さで巨大な読み込みをしないための上限 */
const NPY_HEADER_LIMIT = 1 << 20;

const startsWith = (bytes: Uint8Array, prefix: number[]) => prefix.every((value, index) => bytes[index] === value);

/** 拡張子から形式を推す(中身を読む前の表示用)。コアは先頭のバイトで判別する */
export function heightmapFormatOfName(name: string | undefined): HeightmapFormat | null {
  const extension = name?.toLowerCase().match(/\.([a-z0-9]+)$/)?.[1];
  return extension === "png" || extension === "npy" ? extension : null;
}

/**
 * 画素数を先頭のバイトから読む。PNG は IHDR(必ず先頭のチャンク)の幅・高さ、.npy はヘッダーの shape。
 * 画像全体をデコードしない(16bitの大きな画像でも重くならない)。
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
    // 版1はヘッダー長が2バイト、版2以降は4バイト(どちらもリトルエンディアン)
    const [offset, length] = head[6] === 1 ? [10, view.getUint16(8, true)] : [12, view.getUint32(8, true)];
    if (length > NPY_HEADER_LIMIT) return { format: "npy", columns: null, rows: null };
    const header = new TextDecoder().decode(await file.slice(offset, offset + length).arrayBuffer());
    const shape = header.match(/['"]shape['"]\s*:\s*\(([^)]*)\)/)?.[1]
      .split(",").map(value => value.trim()).filter(Boolean).map(Number);
    // 行0が南・値がmの2次元配列なので、shape は(南北の画素数, 東西の画素数)
    return shape?.length === 2 && shape.every(Number.isInteger)
      ? { format: "npy", columns: shape[1], rows: shape[0] }
      : { format: "npy", columns: null, rows: null };
  }
  return { format: heightmapFormatOfName(file.name), columns: null, rows: null };
}

/** 作られるマップの一辺(m)。コアの terrain_import._map_extent と同じく、元の範囲を1km単位に切り上げる(最小1km) */
export function importedMapExtent(pixels: number, spacing: number): number {
  return Math.max(SIZE_QUANTUM, Math.ceil((pixels - 1) * spacing / SIZE_QUANTUM - 1e-9) * SIZE_QUANTUM);
}

/**
 * 背景地形の一辺(m)。コアの terrain_import.background_from_heightmap と同じく、元の範囲を50m格子の画素数に丸める。
 * 詳細地形と違って1km単位には切り上げない。
 */
export function importedBackgroundExtent(pixels: number, spacing: number): number {
  return Math.round((pixels - 1) * spacing / BACKGROUND_SPACING) * BACKGROUND_SPACING;
}

/**
 * 標高ファイルから自作シナリオを作る。応答の id は保存元つきのID(local~...)。
 * 背景地形は本文の末尾へ続けて送り、その大きさを background_size で渡す(コアは multipart を使わない)。
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
 * プレビュー用に間引いた標高。行0が南。sourceRows/sourceCols は間引く前の画素数(範囲は sourceCols-1 個分の間隔)。
 * minHeight/maxHeight は間引く前のファイルの標高の最小・最大(海面の高さを引く前)で、.npy の標高範囲として見せる。
 */
export type HeightmapPreviewGrid = {
  elev: Float32Array; rows: number; cols: number; sourceRows: number; sourceCols: number; minHeight: number; maxHeight: number;
};

/** 標高ファイルの読み方。min/max はPNGだけが使い、seaLevel は取り込みと同じく標高から引かれる */
export type HeightmapReading = { minHeight: number; maxHeight: number; seaLevel: number };

/**
 * POST /api/v1/scenarios/heightmap-preview。取り込みと同じ読み方で標高を作り、間引いて返す(何も作らない)。
 * 背景は .npy だけなので min/max は送らない。
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

/** GET /api/v1/scenarios/{id}/terrain のうち平面図に使う項目。標高は float16 LE(行0が南)の base64 */
type ScenarioTerrainResponse = {
  settings: { map_width: number; map_height: number; near_spacing: number; grid_rows: number; grid_cols: number };
  near_base64: string;
  ground_cover?: number | null;
};

/** シナリオの地形から平面図(北が上・海は透過)を描き、シナリオ画像として登録する */
export async function registerTerrainThumbnail(apiBaseUrl: string, scenarioId: string): Promise<void> {
  const terrain = await fetchApi<ScenarioTerrainResponse>(
    buildApiUrl(apiBaseUrl, `/api/v1/scenarios/${encodeURIComponent(scenarioId)}/terrain`), { cache: "no-store" },
  );
  const { map_width, map_height, near_spacing, grid_rows, grid_cols } = terrain.settings;
  const bytes = base64ToUint8(terrain.near_base64);
  if (bytes.length !== grid_rows * grid_cols * 2) throw new Error(`Elevation size does not match the grid (${grid_cols}×${grid_rows})`);
  const image = renderTerrainThumbnail({
    elev: decodeFloat16Array(bytes, grid_rows * grid_cols), rows: grid_rows, cols: grid_cols,
    mapWidth: map_width, mapHeight: map_height, spacing: near_spacing, groundCover: normalizeGroundCover(terrain.ground_cover),
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
