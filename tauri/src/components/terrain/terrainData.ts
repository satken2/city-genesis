import type { TerrainProps } from "./TerrainPropEditor";

/**
 * 地形データの符号化・色マッピングの共通ロジック。
 *
 * near.bin の標高データは float16 (IEEE 754 half) LE バイト列を base64 エンコードした形で
 * Tauriコマンドとやり取りされる。この符号化・色マッピングは2D地形エディタ(TerrainEditorCanvas)と
 * 3Dプレビュー(Terrain3DPreviewCanvas)の両方で必要になるため、ここに共通化する。
 */

/** GeoJSON ライクなポリゴン(XZワールド座標。coordinates は Polygon なら [外周, 穴...]、
 *  MultiPolygon なら [[外周, 穴...], ...])。 */
export type WaterPolygonGeoJson = {
  type: "Polygon" | "MultiPolygon";
  coordinates: number[][][] | number[][][][];
};

/**
 * 水面1枚分(海面以外の湖など)の定義。
 * バックエンドの /terrain/water、地形エディタの meta.json / サイドカーJSON、
 * Unityの WaterSurfaceManager すべてで共通の契約。
 */
export type WaterSurfacePayload = {
  id: string;
  /** 水面のY標高(m)。heightmap と同じ絶対標高フレーム(海面=0) */
  water_level: number;
  /** 種別ラベル(任意。将来の見た目分岐用)。"lake" など */
  kind?: string;
  polygon: WaterPolygonGeoJson;
};

/**
 * 川1本分の保存契約。
 * 地形エディタの meta.json / サイドカーJSON(map_xxx.rivers.json)と、バックエンド
 * (GET /terrain/rivers)・Unity 描画で共通の契約。水面(WaterSurfacePayload)とは別系統で保存する。
 * 湖の契約は単一 water_level の水平面だが、川は流下方向に水面標高が変化するため互換性が無い。
 *
 * 川は「範囲(polygon)」と「水面標高のプロファイル線(profile)」の2つで定義する。
 * 川床を彫り込むときのスプライン(流路・幅・深さ)はエディタ内部の中間データであり、
 * 標高データ(.npy)に焼き込まれた時点で役目を終える。ゲーム側が知る必要があるのは
 * 「どの範囲が川で」「その範囲の各地点の水面が何mか」だけなので、それだけを保存する。
 * 仕様の正は genesis-docs/docs/geometries/D_地形形状.md §D.11。
 */
export type RiverPayload = {
  id: string;
  /** 種別ラベル。常に "river" */
  kind: string;
  /** 川の範囲。彫り込み後の地形で「水面標高より低い」領域を地形格子から抽出したもので、
   *  中州・島は穴(内周リング)になる。座標系は水面の polygon と同じ(ワールドXZ、マップ中心が(0,0))。
   *  建設不可判定(海岸線の穴あけ)・パスの横断判定・水面メッシュの範囲はすべてこの形状を使う */
  polygon: WaterPolygonGeoJson;
  /** 水面標高のプロファイル線 [[x, waterY, z], ...](上流→下流)。
   *  範囲内の任意地点の水面標高は「この折れ線上の最近点の waterY」で決まる
   *  (riverGeometry.ts の RiverProfileSampler)。y は絶対標高(m、海面=0)で流下方向に単調非増加 */
  profile: number[][];
};

/**
 * 天然資源エリア1件分の保存契約。
 * 地形エディタの meta.json / サイドカーJSON(map_xxx.resources.json)と、バックエンド
 * (terrain_registry.load_map_resources → init_game での資源エンティティ生成)で共通の契約。
 * 経済シミュレーション上の意味(埋蔵量・回復率・分配)は genesis-docs/docs/economics/21_天然資源.md、
 * サイドカーの仕様の正は genesis-docs/docs/geometries/D_地形形状.md §D.12。
 */
export type ResourceAreaPayload = {
  id: string;
  /** 資源種別(resource_master のキー)。提供物資(product_id)は種別からバックエンドが導出する */
  resource_type: number;
  /** 埋蔵量上限。作成直後の埋蔵量はこの値と同値(満タン)で初期化される */
  max_amount: number;
  /** 埋蔵量上限に対する1日あたりの回復割合。0.0 は回復しない非再生資源(鉱脈・油田など) */
  recovery_rate: number;
  polygon: WaterPolygonGeoJson;
};

/**
 * カメラ初期状態の保存契約。
 * 地形エディタの meta.json / terrain_maps.yaml の `camera`、バックエンドの City.camera
 * (GET/PATCH /api/v1/city)、Unity の CameraController.CameraStatePayload で共通の契約。
 * 仕様の正は genesis-docs/docs/geometries/D_地形形状.md §D.13。
 */
export type TerrainCameraPayload = {
  /** カメラ位置のワールド座標(m)。x/z はマップ中心が(0,0)、y は絶対標高フレームの高さ */
  x: number;
  y: number;
  z: number;
  /** 視線の方位(度)。北(ワールド+Z)を0とする時計回り */
  yaw: number;
  /** 見下ろし角(度)。正で下向き(90=真下)、0で水平 */
  pitch: number;
};

/**
 * 初期開放セル1つ分の保存契約 [cx, cy]。
 * 地形エディタの meta.json / terrain_maps.yaml の `initial_cells` と、バックエンドの
 * 新規都市初期化(terrain_registry → initialize_data での acquire_cell)で共通の契約。
 * セル番号はマップ隅(-map_width/2, -map_height/2)を (0, 0) とする cell_size 間隔の格子。
 * 仕様の正は genesis-docs/docs/geometries/D_地形形状.md §D.14。
 */
export type InitialCellPayload = [number, number];

/** バックエンドの地形エディタ用データ取得コマンドの戻り値型 */
export type TerrainEditorData = {
  map_id: string;
  map_width: number;
  map_height: number;
  near_spacing: number;
  far_spacing: number;
  cell_size: number;
  grid_rows: number;
  grid_cols: number;
  /** float16 LE バイト列を base64 エンコードした文字列 */
  data_base64: string;
  /** 水面ポリゴン(海面以外の湖など)の一覧。未設定なら空配列。 */
  water_surfaces: WaterSurfacePayload[];
  /** 川の一覧。未設定なら空配列。 */
  rivers: RiverPayload[];
  /** 天然資源エリアの一覧。未設定なら空配列。 */
  resources: ResourceAreaPayload[];
  props: TerrainProps;
  /** カメラ初期状態。未設定なら null。 */
  camera: TerrainCameraPayload | null;
  /** 初期開放セルの一覧。未設定なら空配列(ゲームはマップ中央の2×2セルで始まる)。 */
  initial_cells: InitialCellPayload[];
};

/** float16(IEEE 754 half)のビット表現を float32 相当に変換する */
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

/** base64 文字列を Uint8Array にデコードする */
export function base64ToUint8(b64: string): Uint8Array {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

/** float16 LE バイト列を Float32Array に変換する */
export function decodeFloat16Array(bytes: Uint8Array, count: number): Float32Array {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const out = new Float32Array(count);
  for (let i = 0; i < count; i++) {
    out[i] = decodeFloat16(view.getUint16(i * 2, true));
  }
  return out;
}

/**
 * float32 の値を float16(IEEE 754 half)のビット表現へ変換する(decodeFloat16の逆)。
 * 地形の標高データは通常 float16 の正規化数の範囲(絶対値 約6e-5〜65504)に収まるため、
 * 非正規化数への丸めは省略し、極端な値は 0 または ±Infinity として扱う。
 */
export function encodeFloat16(value: number): number {
  if (Number.isNaN(value)) return 0x7e00;
  const sign = value < 0 || Object.is(value, -0) ? 0x8000 : 0;
  const abs = Math.abs(value);
  if (abs === 0) return sign;
  if (!isFinite(abs)) return sign | 0x7c00;

  const buf = new ArrayBuffer(4);
  const f32 = new Float32Array(buf);
  const u32 = new Uint32Array(buf);
  f32[0] = abs;
  const bits = u32[0];
  const exp32 = (bits >>> 23) & 0xff;
  const mantissa32 = bits & 0x7fffff;

  let exp16 = exp32 - 127 + 15;
  if (exp16 >= 0x1f) return sign | 0x7c00; // オーバーフロー→Infinity
  if (exp16 <= 0) return sign; // アンダーフロー→0に丸める

  // 23bit仮数部を10bitに丸める(中間ビットを足してから右シフト=四捨五入)
  let mantissa16 = (mantissa32 + 0x00001000) >>> 13;
  if (mantissa16 === 0x400) {
    // 丸めで仮数がオーバーフローしたら指数を1つ上げる
    exp16 += 1;
    mantissa16 = 0;
    if (exp16 >= 0x1f) return sign | 0x7c00;
  }
  return sign | (exp16 << 10) | mantissa16;
}

/** Float32Array を float16 LE バイト列(Uint8Array)へ変換する */
export function encodeFloat16Array(values: Float32Array): Uint8Array {
  const out = new Uint8Array(values.length * 2);
  const view = new DataView(out.buffer);
  for (let i = 0; i < values.length; i++) {
    view.setUint16(i * 2, encodeFloat16(values[i]), true);
  }
  return out;
}

/** Uint8Array を base64 文字列にエンコードする(atobの逆)。巨大配列でもスタックを溢れさせないようチャンク処理する。 */
export function uint8ToBase64(bytes: Uint8Array): string {
  const chunkSize = 0x8000;
  let bin = "";
  for (let i = 0; i < bytes.length; i += chunkSize) {
    bin += String.fromCharCode(...bytes.subarray(i, i + chunkSize));
  }
  return btoa(bin);
}

/**
 * 標高を0..1に正規化した値から地形色を得る。
 * 低=緑、高=茶色の色マップで、海面(標高0以下)は背景色にする。
 * 都市マップ(components/map/CityMapCanvas)の標高色分けも同系統のランプを使っている。
 */
export function terrainColor(t: number): [number, number, number] {
  const clamped = Math.max(0, Math.min(1, t));
  let r: number, g: number, b: number;
  if (clamped < 0.5) {
    const s2 = clamped * 2;
    r = Math.round(45 + s2 * (140 - 45));
    g = Math.round(106 + s2 * (160 - 106));
    b = Math.round(79 + s2 * (60 - 79));
  } else {
    const s2 = (clamped - 0.5) * 2;
    r = Math.round(140 + s2 * (160 - 140));
    g = Math.round(160 + s2 * (100 - 160));
    b = Math.round(60 + s2 * (30 - 60));
  }
  return [r, g, b];
}

/** 標高1点から地形テクスチャのRGBを求める。海面(標高0以下)は背景色にする。 */
export function computePixelRGB(e: number, minLand: number, maxLand: number): [number, number, number] {
  if (e <= 0) return [0x0d, 0x0d, 0x14];
  const t = (e - minLand) / (maxLand - minLand);
  return terrainColor(t);
}

/**
 * 選択範囲(水色)ハイライトを重ねた頂点色を求める。
 * 2Dエディタの選択オーバーレイ(rgba(80,190,255, weight*160/255))を地形色の上に
 * 通常アルファ合成した結果と同じ見た目になるよう、3Dプレビューの頂点カラーもここで揃える。
 */
export function computeVertexColor(
  elev: number,
  minLand: number,
  maxLand: number,
  maskWeight: number
): [number, number, number] {
  const [tr, tg, tb] = computePixelRGB(elev, minLand, maxLand);
  const a = (Math.max(0, Math.min(1, maskWeight)) * 160) / 255;
  const hr = 80, hg = 190, hb = 255;
  return [
    Math.round(tr * (1 - a) + hr * a),
    Math.round(tg * (1 - a) + hg * a),
    Math.round(tb * (1 - a) + hb * a),
  ];
}

/** グリッド上の矩形範囲(行・列とも両端含む) */
export type GridRect = { rMin: number; rMax: number; cMin: number; cMax: number };

/** 標高/選択マスク配列から rect 部分だけを行優先で抜き出す(差分送信用) */
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

/** 標高配列の rect 部分を抜き出す(elevation_delta イベント用) */
export function extractElevRect(elev: Float32Array, cols: number, rect: GridRect): Float32Array {
  return extractRect(elev, cols, rect);
}

/** 選択マスク(0..1)の rect 部分を 0-255 に量子化して抜き出す(selection_delta / full_state イベント用) */
export function quantizeMaskRect(mask: Float32Array, cols: number, rect: GridRect): Uint8Array {
  const rectValues = extractRect(mask, cols, rect);
  const out = new Uint8Array(rectValues.length);
  for (let i = 0; i < rectValues.length; i++) {
    out[i] = Math.round(Math.max(0, Math.min(1, rectValues[i])) * 255);
  }
  return out;
}

/** quantizeMaskRect の逆変換。dst(フルサイズのマスク配列)へ rect 範囲を書き戻す */
export function dequantizeMaskRectInto(dst: Float32Array, cols: number, rect: GridRect, quantized: Uint8Array): void {
  let oi = 0;
  for (let r = rect.rMin; r <= rect.rMax; r++) {
    const rowBase = r * cols;
    for (let c = rect.cMin; c <= rect.cMax; c++) dst[rowBase + c] = quantized[oi++] / 255;
  }
}

/** extractElevRect の逆変換。dst(フルサイズの標高配列)へ rect 範囲を書き戻す */
export function applyElevRectInto(dst: Float32Array, cols: number, rect: GridRect, values: Float32Array): void {
  let vi = 0;
  for (let r = rect.rMin; r <= rect.rMax; r++) {
    const rowBase = r * cols;
    for (let c = rect.cMin; c <= rect.cMax; c++) dst[rowBase + c] = values[vi++];
  }
}

/* ============================================================================
 * 地形エディタ(terrain_editor)⇔ 3Dプレビュー(terrain_3d_preview) 間のイベント契約
 *
 * 別ウィンドウ同士のリアルタイム同期は、このアプリの既存パターン(FontSizeContext等)に
 * 倣い、Tauriの emit/listen による全ウィンドウブロードキャスト + mapId によるフィルタで
 * 行う(emitTo による特定ウィンドウ宛て送信は使わない)。
 * ========================================================================== */

/** プレビュー側がマウント時に現在の全状態を要求するイベント */
export const EVT_TERRAIN_3D_REQUEST_STATE = "terrain_3d_preview:request_state";
/** エディタ側が保持する標高・選択マスク・メタデータの全体を送るイベント(要求時・ロード時・リサイズ確定時) */
export const EVT_TERRAIN_3D_FULL_STATE = "terrain_3d_preview:full_state";
/** ブラシ/ポリゴンの選択範囲(水色ハイライト)差分を送るイベント(ドラッグ中も含めリアルタイム) */
export const EVT_TERRAIN_3D_SELECTION_DELTA = "terrain_3d_preview:selection_delta";
/** 選択範囲を全クリアしたことを通知するイベント */
export const EVT_TERRAIN_3D_SELECTION_RESET = "terrain_3d_preview:selection_reset";
/** 「適用」ボタン押下で確定した標高差分を送るイベント */
export const EVT_TERRAIN_3D_ELEVATION_DELTA = "terrain_3d_preview:elevation_delta";

export type Terrain3DRequestStatePayload = { mapId: string };

export type Terrain3DFullStatePayload = {
  mapId: string;
  mapWidth: number;
  mapHeight: number;
  spacing: number;
  rows: number;
  cols: number;
  minLand: number;
  maxLand: number;
  /** float16 LE を base64 エンコードした標高配列(行優先、rows*cols要素) */
  elevBase64: string;
  /** 0-255 に量子化した選択マスクを base64 エンコードしたもの(行優先、rows*cols要素) */
  maskBase64: string;
  /** 水面ポリゴン(海面以外の湖など)の一覧。3Dプレビューで水面板を描くために送る。 */
  waterSurfaces: WaterSurfacePayload[];
  /** 確定済みの川の一覧。3Dプレビューで水面リボンを描くために送る。
   *  川の変化(彫り込み確定・削除)は標高も全域で変化するため、専用の差分イベントは
   *  設けず full_state に相乗りさせる。 */
  rivers: RiverPayload[];
};

export type Terrain3DSelectionDeltaPayload = GridRect & {
  mapId: string;
  /** quantizeMaskRect の結果を base64 エンコードしたもの */
  maskRectBase64: string;
};

export type Terrain3DSelectionResetPayload = { mapId: string };

export type Terrain3DElevationDeltaPayload = GridRect & {
  mapId: string;
  /** extractElevRect の結果を float16 LE → base64 エンコードしたもの */
  elevRectBase64: string;
};
