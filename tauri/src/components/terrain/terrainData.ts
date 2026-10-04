import type { LocalizedText } from "../../utils/locale";
import {
  CLIMATE_TEMPERATE,
  CLIMATE_TYPES,
  GROUND_COVER_DESERT,
  GROUND_COVER_GRASSLAND,
  GROUND_COVER_LUSH,
  GROUND_COVER_SEMI_ARID,
  GROUND_COVER_SNOW,
  GROUND_COVER_TYPES,
} from "../../constants";

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
 * バックエンドの /terrain/water とシナリオの地形 API、地形エディタの作業コピー(meta.json)、
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
 * 地形エディタの作業コピー(meta.json)・シナリオの地形 API と、バックエンド
 * (GET /terrain/rivers)・Unity 描画で共通の契約。水面(WaterSurfacePayload)とは別系統で保存する。
 * 湖の契約は単一 water_level の水平面だが、川は流下方向に水面標高が変化するため互換性が無い。
 *
 * 川は「範囲(polygon)」と「水面標高のプロファイル線(profile)」の2つで定義する。
 * 川床を彫り込むときのスプライン(流路・幅・深さ)はエディタ内部の中間データであり、
 * 標高データに焼き込まれた時点で役目を終える。ゲーム側が知る必要があるのは
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
 * 地形エディタの作業コピー(meta.json)とシナリオの地形 API で共通の契約。id は資源の DB ID の文字列で、
 * 保存時はコアが ID で差分適用する(送らなかった資源は削除される)。地形エディタは資源を編集せず、
 * 読み込んだ内容をそのまま送り返す。
 * 経済シミュレーション上の意味(埋蔵量・回復率・分配)は genesis-docs/docs/economics/21_天然資源.md、
 * 仕様の正は genesis-docs/docs/geometries/D_地形形状.md §D.12。
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

export type TerrainPropInstance = {
  id: string; prop_type: number; x: number; y: number; z: number; rotation: number; scale: number;
};
export type TerrainPropScatter = {
  id: string; prop_type: number; polygon: number[][]; density: number; seed: number;
  edge_fade_width: number; allow_water: boolean;
};
/**
 * プロップの配置定義の保存契約(個別配置と範囲散布)。シナリオの地形 API で送受信し、保存では全置換。
 * y は地面からの相対高さ。地形エディタはプロップを編集せず、読み込んだ内容をそのまま送り返す。
 * 仕様の正は genesis-docs/docs/geometries/M_地形プロップ.md。
 */
export type TerrainProps = { instances: TerrainPropInstance[]; scatters: TerrainPropScatter[] };

/**
 * カメラ初期状態の保存契約。
 * 地形エディタの作業コピー(meta.json)・シナリオの地形 API の `camera`、バックエンドの City.camera
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
 * 標高・水面・川のロック。建設物(道路・建物・造成跡・埋立)があるシナリオでは heights_locked になり、
 * 保存時に標高・水面・川を送るとコアが 409 TERRAIN_LOCKED で拒否する。reasons は理由コード
 * (PATHS_EXIST・BUILDINGS_EXIST・TERRAIN_MODIFIED・RECLAIMED_LAND)。
 */
export type TerrainEditorLocks = { heights_locked: boolean; reasons: string[] };

/** Rust の load_terrain_editor_data の戻り値。シナリオの地形 API から作った作業コピーの中身 */
export type TerrainEditorData = {
  scenario_id: string;
  /** 地形の出どころ(取り込んだ標高ファイルの名前、またはチュートリアルの舞台の名前)。表示だけに使う */
  terrain_source: string | null;
  /** シナリオのタイトル(多言語テキスト。見出しに表示言語で出す) */
  title: LocalizedText | null;
  locks: unknown;
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
  /** 地表種別(GROUND_COVER_*)。未設定なら null(草原として扱う)。 */
  ground_cover: number | null;
  /** 気候区分(CLIMATE_*)。未設定なら null(温暖湿潤として扱う)。 */
  climate: number | null;
};

/**
 * 作業コピーのロック情報を正規化する。形が崩れていればロック扱いにする(保存がコアに拒否されるより、
 * 編集できない方を選ぶ)。理由は文字列コードか `{code}` を受け付ける。
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

/** シナリオへ保存する地形エディタの内容。資源とプロップは編集せず、読み込んだ内容がそのまま入る */
export type ScenarioTerrainContent = {
  elev: Float32Array;
  waterSurfaces: WaterSurfacePayload[];
  rivers: RiverPayload[];
  resources: ResourceAreaPayload[];
  props: TerrainProps;
  camera: TerrainCameraPayload | null;
  groundCover: number;
  climate: number;
};

/**
 * Rust の save_scenario_terrain の引数。標高がロックされているときは標高・水面・川を null にする
 * (コアの契約。値を送ると 409 TERRAIN_LOCKED)。標高は送るときだけ符号化する。
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

/**
 * float16 のビット表現(0〜65535)→ 値の対応表。数百万点の標高を変換するとき、1点ごとに
 * Math.pow を呼ぶと100ms単位の時間がかかるため、初回利用時に全ビット表現ぶんを作って引く。
 */
let float16Table: Float32Array | null = null;

/** float16 LE バイト列を Float32Array に変換する */
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
 * float32 のビット表現を float16(IEEE 754 half)のビット表現へ変換する。
 * 地形の標高データは通常 float16 の正規化数の範囲(絶対値 約6e-5〜65504)に収まるため、
 * 非正規化数への丸めは省略し、極端な値は 0 または ±Infinity として扱う。NaN は符号なしの 0x7e00。
 */
function float32BitsToFloat16(bits: number): number {
  const sign = (bits >>> 16) & 0x8000;
  const exp32 = (bits >>> 23) & 0xff;
  const mantissa32 = bits & 0x7fffff;
  if (exp32 === 0xff) return mantissa32 !== 0 ? 0x7e00 : sign | 0x7c00;

  let exp16 = exp32 - 127 + 15;
  if (exp16 >= 0x1f) return sign | 0x7c00; // オーバーフロー→Infinity
  if (exp16 <= 0) return sign; // アンダーフロー(±0を含む)→0に丸める

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

/** encodeFloat16 が値を float32 のビット表現へ読み替えるための作業領域 */
const scratchFloat32 = new Float32Array(1);
const scratchFloat32Bits = new Uint32Array(scratchFloat32.buffer);

/** 数値を float16(IEEE 754 half)のビット表現へ変換する(decodeFloat16の逆)。値は一度 float32 へ丸める。 */
export function encodeFloat16(value: number): number {
  scratchFloat32[0] = value;
  return float32BitsToFloat16(scratchFloat32Bits[0]);
}

/** Float32Array を float16 LE バイト列(Uint8Array)へ変換する。値をコピーせず、同じメモリをビット列として読む。 */
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
 * base64 のネイティブ実装(Uint8Array.prototype.toBase64 / Uint8Array.fromBase64)。
 * 型定義が標準ライブラリにまだ無いため、ここで最小限の形を宣言して存在を確かめてから使う。
 * JS実装は数MBの標高データで数百msかかるのに対し、ネイティブ実装は数msで終わる。
 */
type NativeBase64Bytes = Uint8Array & { toBase64?: () => string };
type NativeBase64Ctor = typeof Uint8Array & { fromBase64?: (base64: string) => Uint8Array };

/** base64 文字列を Uint8Array にデコードする */
export function base64ToUint8(b64: string): Uint8Array {
  const ctor = Uint8Array as NativeBase64Ctor;
  if (typeof ctor.fromBase64 === "function") return ctor.fromBase64(b64);
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

/** Uint8Array を base64 文字列にエンコードする(atobの逆)。JS実装は巨大配列でもスタックを溢れさせないようチャンク処理する。 */
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

/** 陸地の標高範囲。平地でも色の正規化でゼロ除算しないよう最低1m幅を持たせる。 */
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
 * 地表種別(GROUND_COVER_*)ごとの標高カラーランプ。低地・中腹・高地の3色を線形補間する。
 * Unity の地表の配色(GroundCoverPalette)を2D表示向けに要約したもので、地形エディタ・3Dプレビューで
 * 同じマップを同じ系統の色で見せる。草原は低地=緑→高地=茶のランプ。
 */
const GROUND_COVER_RAMPS: Record<number, [Rgb, Rgb, Rgb]> = {
  [GROUND_COVER_GRASSLAND]: [[45, 106, 79], [140, 160, 60], [160, 100, 30]],
  [GROUND_COVER_DESERT]: [[222, 196, 146], [204, 166, 112], [158, 112, 72]],
  [GROUND_COVER_LUSH]: [[26, 84, 50], [72, 124, 52], [122, 98, 60]],
  [GROUND_COVER_SNOW]: [[208, 218, 230], [236, 240, 246], [150, 156, 166]],
  [GROUND_COVER_SEMI_ARID]: [[180, 160, 96], [168, 150, 92], [150, 112, 72]],
};

/** 海面(標高0以下)の色。地形エディタの2D表示では背景色も兼ねる。 */
export const SEA_RGB: Rgb = [0x0d, 0x0d, 0x14];

/** 未設定・未知の値を草原に丸めた地表種別。 */
export function normalizeGroundCover(value: unknown): number {
  return typeof value === "number" && GROUND_COVER_TYPES.includes(value) ? value : GROUND_COVER_GRASSLAND;
}

/** 未設定・未知の値を温暖湿潤に丸めた気候区分。 */
export function normalizeClimate(value: unknown): number {
  return typeof value === "number" && CLIMATE_TYPES.includes(value) ? value : CLIMATE_TEMPERATE;
}

/** 地表種別の標高カラーランプ([低地, 中腹, 高地])。地形エディタのシェーダーも同じ3色で塗る。 */
export function groundCoverRamp(groundCover: number = GROUND_COVER_GRASSLAND): [Rgb, Rgb, Rgb] {
  return GROUND_COVER_RAMPS[normalizeGroundCover(groundCover)];
}

/**
 * 標高を0..1に正規化した値から地形色を得る。海面(標高0以下)の扱いは computePixelRGB が持つ。
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

/** 標高1点から地形テクスチャのRGBを求める。海面(標高0以下)は SEA_RGB にする。 */
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
 * 選択範囲(水色)ハイライトを重ねた頂点色を求める。
 * 2Dエディタの選択オーバーレイ(rgba(80,190,255, weight*160/255))を地形色の上に
 * 通常アルファ合成した結果と同じ見た目になるよう、3Dプレビューの頂点カラーもここで揃える。
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
 * 倣い、Tauriの emit/listen による全ウィンドウブロードキャスト + mapId(作業コピーのID。両ウィンドウの
 * entity_id)によるフィルタで行う(emitTo による特定ウィンドウ宛て送信は使わない)。
 *
 * エディタは request_state を送ってきて detach していないプレビューがあるときだけ状態を送る。
 * 全状態は標高を丸ごと符号化するため大きなマップでは数十MBになり、プレビューを開いていない
 * 間まで編集のたびに送ると操作が重くなるため。プレビューはマウントごとに sessionId を作り、
 * 表示されたら request_state、非表示・アンマウントで detach を送る(Tauriのイベントは到着順が
 * 保証されないので、どのマウントの接続/切断かを sessionId で区別する)。
 * ========================================================================== */

/** プレビュー側がマウント時・再表示時に現在の全状態を要求するイベント */
export const EVT_TERRAIN_3D_REQUEST_STATE = "terrain_3d_preview:request_state";
/** プレビュー側が非表示・アンマウントになり、状態の送信が不要になったことを知らせるイベント */
export const EVT_TERRAIN_3D_DETACH = "terrain_3d_preview:detach";
/** エディタ側が保持する標高・選択マスク・メタデータの全体を送るイベント(要求時・ロード時・リサイズ確定時) */
export const EVT_TERRAIN_3D_FULL_STATE = "terrain_3d_preview:full_state";
/** ブラシ/ポリゴンの選択範囲(水色ハイライト)差分を送るイベント(ドラッグ中も含めリアルタイム) */
export const EVT_TERRAIN_3D_SELECTION_DELTA = "terrain_3d_preview:selection_delta";
/** 選択範囲を全クリアしたことを通知するイベント */
export const EVT_TERRAIN_3D_SELECTION_RESET = "terrain_3d_preview:selection_reset";
/** 「適用」ボタン押下で確定した標高差分を送るイベント */
export const EVT_TERRAIN_3D_ELEVATION_DELTA = "terrain_3d_preview:elevation_delta";

/** sessionId はプレビューのマウントごとに作る一意な文字列 */
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
  /** 地表種別(GROUND_COVER_*)。3Dプレビューの地形の頂点色を2Dエディタと同じランプにする。 */
  groundCover: number;
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
