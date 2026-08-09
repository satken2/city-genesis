/**
 * 都市2Dマップの共通描画コンポーネント。
 *
 * ツールバー等のUIを一切持たない「純粋な絵」のコンポーネントであり、
 * 都市マップウィンドウ(CityMapWindow)のほか、各種パネルへの埋め込み
 * (道路建設プレビューの背景地図、路線図の下敷きなど)にもそのまま使える。
 *
 * 座標系: ゲームはX-Z水平面・Y標高。マップは真上からのTop-Downビュー。
 * 描画はCanvas 2Dのビュー変換(translate+scale)で行い、各レイヤーは
 * ワールド座標のままPath2D/オフスクリーンCanvasとしてキャッシュする。
 *
 * 画面上の向き: Canvas 2DはY軸が下向きだが、ここではビュー変換でY倍率を負にして
 * ワールド+Zを画面の「上」に描く。これによりUnityの俯瞰カメラ・ミニマップ
 * (いずれも+Zが画面上)と2Dマップの上下が一致する。個々の座標を反転させるのでは
 * なく変換行列(CTM)で吸収するため、Path2D・標高画像・等高線・グリッド・overlayDraw
 * はすべてワールド座標のまま無変更で流用できる。
 * overlayDrawで文字や画像(アイコン等)を描く場合のみ、Y反転のかかったCTM上で
 * 上下反転して見えるため、その描画に限りY方向を打ち消す必要がある。
 *
 * レイヤー(propsのlayersでON/OFF):
 *   - elevation : 標高に応じた色分け(近景標高マップを最大解像度で色付け。地形エディタと同方式)
 *   - contours  : 等高線(近景標高マップからMarching Squaresで構築。ズームに応じて間隔を細分化)
 *   - grid      : 100m間隔のグリッド(セル境界=cell_size間隔は濃く強調)
 *   - coastline : 海岸線ポリゴン(陸地の外縁+内陸の湖・池の岸線=穴リング)
 *   - zoneBorders : 地区の枠線
 *   - parcels   : 土地区画の枠線
 *   - buildings : 建物の底面形状
 *   - allRoads / trunkRoads / railways : パス(道路・幹線道路・線路)。
 *     道路はマスターデータの全幅(width)をstroke幅に使い、実際の道路幅を持つ
 *     面として描く(街路=灰、自動車専用道路=黄、線路=茶の線)。
 *
 * ヒートマップ(heatmap propで種別を1つだけ選ぶ排他表示):
 *   騒音・大気汚染の環境スコアを土地区画の塗り分け(コロプレス)で表現する。
 *   バックエンドが持つのは区画中心でサンプリング済みの区画単位スコアであり、
 *   ビットマップそのものは公開されていないため、区画ポリゴンを色で塗る形になる。
 *   2種を同時に塗ると色が意味を失うため、複数ONにできるレイヤーとは分けている。
 *
 * データ取得:
 *   - パス・土地区画・建物はビューポートにパディングを加えたbboxを指定して取得する。
 *     ビューポートが取得済み範囲の外へ出たらデバウンス付きで再取得する。
 *   - 地区は件数が少ない(上限100件)ため全件取得する。
 *   - 幹線道路の判定はマスターデータ(paths[variant].trunk)で行う
 *     (自動車専用道路はtrunkフラグにかかわらず常に幹線扱い)。
 *
 * 上位コンポーネントはref(CityMapCanvasHandle)経由で全体表示リセットと
 * データ再読み込みを指示でき、overlayDrawコールバックでワールド座標系の
 * 重ね描き(敷設中の道路・路線図など)を行える。
 */

import { forwardRef, useEffect, useImperativeHandle, useRef } from "react";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import type { MasterData } from "../../types";
import { computePixelRGB, decodeFloat16Array } from "../terrain/terrainData";
import {
  PATH_MOTORWAY,
  PATH_STREET,
  PATH_WALKWAY,
  PATH_TRACK,
} from "../../constants";

/* ============================================================================
 * 公開型
 * ========================================================================== */

/** レイヤーの表示状態。すべてpropsから制御する */
export type CityMapLayerVisibility = {
  /** 海岸線 */
  coastline: boolean;
  /** 幹線道路(master.yamlのtrunkフラグを持つバリアント) */
  trunkRoads: boolean;
  /** すべての道路(自動車道・街路・歩道) */
  allRoads: boolean;
  /** 線路 */
  railways: boolean;
  /** 土地区画の枠線 */
  parcels: boolean;
  /** 建物の底面形状 */
  buildings: boolean;
  /** 駅(同じ駅舎に所属するプラットフォーム群の凸包) */
  stations: boolean;
  /** 地区の枠線 */
  zoneBorders: boolean;
  /** 標高に応じた色分け */
  elevation: boolean;
  /** 等高線 */
  contours: boolean;
  /** グリッド */
  grid: boolean;
};

/** レイヤー表示のデフォルト値(主要レイヤーON、等高線・グリッドはOFF) */
export const DEFAULT_CITY_MAP_LAYERS: CityMapLayerVisibility = {
  coastline: true,
  trunkRoads: true,
  allRoads: true,
  railways: true,
  parcels: true,
  buildings: true,
  stations: true,
  zoneBorders: true,
  elevation: true,
  contours: false,
  grid: false,
};

/**
 * ヒートマップの種別。土地区画を環境スコアで塗り分ける。
 * 2種を重ねても色が混ざって読めなくなるだけなので、レイヤーとは別の排他選択とする。
 */
export type CityMapHeatmapKind = "none" | "noise" | "pollution";

/** ヒートマップの実データを持つ種別(noneを除く) */
export type CityMapHeatmapActiveKind = Exclude<CityMapHeatmapKind, "none">;

/** ヒートマップの凡例に必要な情報。上位コンポーネントへonHeatmapStatsで通知する */
export type CityMapHeatmapStats = {
  kind: CityMapHeatmapActiveKind;
  /**
   * 悪さスコアの実値レンジ(0=最良〜1=最悪)。
   * 塗り色はこのレンジを両端いっぱいに引き伸ばした相対値なので、
   * 色の濃さをエポック間で比較するにはこのレンジを併せて見る必要がある。
   */
  min: number;
  max: number;
  /** 値を持つ区画数。0ならヒートマップ未計算(エポック未実行) */
  count: number;
};

/** ワールド座標(X-Z平面)の矩形範囲 */
export type CityMapBounds = { x1: number; z1: number; x2: number; z2: number };

/** ビュー状態。overlayDrawコールバックに渡される */
export type CityMapViewState = { offsetX: number; offsetY: number; zoom: number };

/** 上位コンポーネントがref経由で呼べる操作 */
export type CityMapCanvasHandle = {
  /** 全体表示(initialBounds指定時はその範囲)へビューをリセットする */
  resetView: () => void;
  /** 全データを再取得して再描画する(ビューは維持) */
  reload: () => void;
  /**
   * 指定したワールド座標範囲が収まるようにビューだけを移動・拡縮する。
   * initialBounds propを変えると初期化effectが再実行され地形・ベクターを再取得して
   * しまうのに対し、この操作は再取得を伴わずビュー変換のみを更新する。
   * 敷設中の道路プレビューのように、範囲が頻繁に変わる埋め込み用途で使う。
   */
  fitBounds: (bounds: CityMapBounds) => void;
};

export type CityMapCanvasProps = {
  apiBaseUrl: string;
  /** レイヤー表示状態。省略したキーは DEFAULT_CITY_MAP_LAYERS の値になる */
  layers?: Partial<CityMapLayerVisibility>;
  /** 表示するヒートマップの種別(省略時は"none"=非表示) */
  heatmap?: CityMapHeatmapKind;
  /**
   * ヒートマップの実値レンジ・件数の通知。凡例の描画に使う。
   * 非表示のときはnullが渡る。
   */
  onHeatmapStats?: (stats: CityMapHeatmapStats | null) => void;
  /** 初期表示範囲(ワールド座標)。省略時はマップ全体にフィットする */
  initialBounds?: CityMapBounds;
  /** マウスによるパン・ズーム操作を許可するか(埋め込みプレビューではfalse推奨) */
  interactive?: boolean;
  /**
   * 各レイヤーの描画後に呼ばれる重ね描きコールバック。
   * ctxにはワールド座標系のビュー変換が適用済みなので、ワールド座標のまま
   * 描画できる。画面上で一定の線幅にするには lineWidth = px / view.zoom とする。
   */
  overlayDraw?: (ctx: CanvasRenderingContext2D, view: CityMapViewState) => void;
  /**
   * オーバーレイ再描画のトリガー。overlayDrawはrefミラーで保持されるため、
   * コールバックの中身(描画対象データや選択状態)が変わっただけでは再描画されない。
   * 描画内容を変えたいときはこの値をインクリメントして再描画を要求する。
   */
  overlayVersion?: number;
  /**
   * 連続アニメーション用の重ね描きコールバック(インフラ網の流量アニメなど)。
   * 指定するとアニメーション専用の第2キャンバス(マウス操作は透過)に対して
   * requestAnimationFrameループで毎フレーム呼ばれる。ctxにはoverlayDrawと同じ
   * ワールド座標系のビュー変換が適用済みで、timeMsには経過時刻(performance.now())が
   * 渡される。ベースの地図(地形・ベクター)はフル再描画されないため、毎フレーム
   * 呼んでも描画コストはこのコールバックの分だけで済む。未指定ならループ自体が
   * 走らない(静的な埋め込み用途に追加コストなし)。
   */
  animatedOverlayDraw?: (
    ctx: CanvasRenderingContext2D,
    view: CityMapViewState,
    timeMs: number
  ) => void;
  /** マウスカーソル位置のワールド座標通知(キャンバス外に出たらnull) */
  onCursorWorldPos?: (pos: { x: number; z: number } | null) => void;
  /** ズーム倍率の変更通知 */
  onZoomChange?: (zoom: number) => void;
  className?: string;
};

/* ============================================================================
 * 内部型・定数
 * ========================================================================== */

/** GET /api/v1/paths のレスポンス要素(描画に必要なフィールドのみ) */
type ApiPathItem = {
  path_id: number;
  path_type: number;
  path_variant: number;
  start_point: number[];
  end_point: number[];
  waypoints?: (number[] | { position?: number[]; control_in?: number[] | null; control_out?: number[] | null })[];
  /** 端点のベジエ制御点 [x, y, z]。保存値があれば自動計算より優先して曲線を描く */
  start_control?: number[] | null;
  end_control?: number[] | null;
  /** ベジエ曲線かどうか(1=曲線/0=直線・折れ線)。曲線のみ補間して描く */
  curved?: boolean | number;
  /** パスの占有ポリゴン [[x,z],...](ワールド座標)。駅の凸包描画に使う */
  polygon?: [number, number][] | null;
  /** 所属する駅舎パスのID。プラットフォームのみ有効で、未所属・非プラットフォームは0以下 */
  house_path_id?: number;
};

/** polygonを持つエンティティ(土地区画・地区・建物)の共通形 */
type ApiPolygonItem = {
  polygon?: [number, number][] | null;
  center_point?: number[] | null;
  /** 地区のみ: 1/true=自動管理地区、0/false=確定地区。枠線の実線/破線の描き分けに使う */
  auto_managed?: boolean | number;
  /** 土地区画のみ: 区画ID。ヒートマップ値との突き合わせに使う */
  parcel_id?: number;
};

/**
 * ヒートマップAPI(GET /api/v1/city/noise, /pollution)のレスポンス。
 * valuesはmin〜maxを0〜65535へ線形正規化したuint16で、parcel_idsと同じ並び。
 * ヒートマップ未計算(エポック未実行)のときは両配列が空でmin/maxも返らない。
 */
type ApiHeatmap = {
  parcel_ids?: number[];
  values?: number[];
  min?: number;
  max?: number;
};

/** 道路の描画区分。streetは歩道(PATH_WALKWAY)も含む */
type RoadBandKind = "street" | "motorway";

/**
 * 同じ描画スタイル(区分・幹線フラグ・幅)の道路をまとめた描画単位。
 * strokeのlineWidthは1回の描画で1値しか指定できないため、実幅で描くには
 * 幅の異なるバリアントを別のPath2Dに分けて構築する必要がある。
 */
type RoadBand = { kind: RoadBandKind; trunk: boolean; width: number; path: Path2D };

/** 取得済みベクターデータとPath2Dキャッシュ */
type VectorState = {
  paths: ApiPathItem[];
  parcels: ApiPolygonItem[];
  buildings: ApiPolygonItem[];
  zones: ApiPolygonItem[];
  /** Path2Dキャッシュ(データ取得後・マスターデータ変更後に構築) */
  roadBands: RoadBand[];
  railPath: Path2D | null;
  parcelPath: Path2D | null;
  buildingPath: Path2D | null;
  /** 駅の凸包(駅舎ごとにまとめたプラットフォーム群の凸包)。半透明の面で描く */
  stationHullPath: Path2D | null;
  /** 確定地区(auto_managed=0)の枠線。実線で描く */
  zonePathConfirmed: Path2D | null;
  /** 自動管理地区(auto_managed=1)の枠線。破線で描き「まだ仮の区割り」であることを示す */
  zonePathAuto: Path2D | null;
  /** bbox取得を要求した範囲。カバレッジ判定に使う */
  fetchedBounds: CityMapBounds | null;
  /** 取得済みのデータ種別 */
  fetchedKinds: Set<VectorKind>;
  loading: boolean;
};

type VectorKind = "paths" | "parcels" | "buildings" | "zones";

/** 取得済みヒートマップと色ビンごとのPath2Dキャッシュ */
type HeatmapState = {
  /** 保持しているデータの種別。"none"=未取得 */
  loadedKind: CityMapHeatmapKind;
  /** 取得を要求した種別。応答が返った時点で切り替わっていれば結果を捨てる */
  requestedKind: CityMapHeatmapKind;
  /** parcel_id → 0〜1へ戻した相対値 */
  values: Map<number, number>;
  /** 悪さスコアの実値レンジ(凡例用) */
  min: number;
  max: number;
  /** 色ビンごとの区画ポリゴン(添字=ビン番号)。値を持つ区画が無ければnull */
  binPaths: Path2D[] | null;
  /** ビン番号に対応する塗り色 */
  binColors: string[];
};

/** 地形設定(GET /api/v1/terrain/settings) */
type TerrainSettings = {
  map_width: number;
  map_height: number;
  near_map_spacing: number;
  cell_size?: number;
};

/** 1本の等高線(あるレベルのPath2D) */
type ContourLine = { path: Path2D; major: boolean };

/**
 * ロード済みの標高マップ(近景解像度)。
 * 地形エディタと同様に最大解像度の標高配列を保持し、色付け済みオフスクリーン
 * キャンバスと、ズーム変更時の等高線再構築の元データとして使う。
 */
type TerrainState = {
  canvas: HTMLCanvasElement;
  elev: Float32Array;
  rows: number;
  cols: number;
  spacing: number;
  minLand: number;
  maxLand: number;
};

/** 背景色。computePixelRGBの海面色と同色にして陸地だけが浮かび上がる見た目にする */
const BG_COLOR = "#0d0d14";

/** 各レイヤーの描画色 */
const COLOR_COASTLINE = "#89dceb";
const COLOR_ZONE = "#89b4fa";
const COLOR_PARCEL = "#585b70";
const COLOR_BUILDING = "#cba6f7";
/* 駅の凸包(同じ駅舎に所属するプラットフォーム群の外接凸包)。薄いピンクの面で
 * 「1つの駅」の範囲を示す */
const COLOR_STATION = "#f38ba8";
/* 道路は実幅の面として描くため、種別ごとの塗り色を持つ。
 * 幹線街路は非幹線よりやや明るい灰にして道路網の骨格を読み取れるようにする */
const COLOR_STREET = "#82858f";
const COLOR_STREET_TRUNK = "#a9adb8";
const COLOR_MOTORWAY = "#f9e2af";
const COLOR_RAIL = "#a5744c";
/* 等高線色。地形色の上では焦げ茶(地形エディタと同じ)、
 * 標高レイヤーOFFの暗い背景の上では白系を使って視認性を保つ */
const COLOR_CONTOUR_ON_TERRAIN = "rgba(40,28,14,0.5)";
const COLOR_CONTOUR_MAJOR_ON_TERRAIN = "rgba(30,20,10,0.75)";
const COLOR_CONTOUR_ON_BG = "rgba(255,255,255,0.22)";
const COLOR_CONTOUR_MAJOR_ON_BG = "rgba(255,255,255,0.38)";
const COLOR_GRID = "rgba(255,255,255,0.10)";
const COLOR_GRID_CELL = "rgba(255,255,255,0.35)";

/**
 * ヒートマップの色ビン数。区画は数千件になるため1件ずつfillすると重い。
 * 値をこの段数へ量子化し、同じ色の区画を1つのPath2Dへまとめて描画回数を畳む。
 */
const HEATMAP_BIN_COUNT = 12;

/** ヒートマップ塗りの不透明度。下の地形・上の道路の双方を潰さない濃さにする */
const HEATMAP_ALPHA = 0.5;

/**
 * 種別ごとのカラーランプ(低い=悪さが小さい → 高い=悪さが大きい)。
 * 騒音は暖色、大気汚染は紫系にして、どちらを表示中か色だけで判別できるようにする。
 */
const HEATMAP_RAMPS: Record<CityMapHeatmapActiveKind, [number, number, number][]> = {
  noise: [[253, 230, 138], [249, 115, 22], [190, 18, 60]],
  pollution: [[209, 231, 221], [168, 85, 247], [76, 29, 149]],
};

/** ヒートマップ種別ごとの取得先エンドポイント */
const HEATMAP_ENDPOINT: Record<CityMapHeatmapActiveKind, string> = {
  noise: "/api/v1/city/noise",
  pollution: "/api/v1/city/pollution",
};

/** グリッドの間隔(m)。セル境界はterrain設定のcell_sizeを使う */
const GRID_MINOR_INTERVAL = 100;

/** ズーム倍率の許容範囲 */
const ZOOM_MIN = 0.005;
const ZOOM_MAX = 1000;

/** bbox取得時にビューポートへ加えるパディング(幅・高さに対する比率) */
const FETCH_PAD = 0.5;
/** 取得済み範囲のカバレッジ判定マージン(FETCH_PADより小さいこと) */
const COVER_MARGIN = 0.15;
/** ベクターデータ再取得のデバウンス(ms) */
const VECTOR_DEBOUNCE_MS = 300;
/** ズーム変更に伴う等高線再構築のデバウンス(ms) */
const CONTOUR_DEBOUNCE_MS = 150;
/**
 * 等高線を細分化し始めるズーム倍率の猶予。基準ズーム(全体フィット表示)から
 * この倍率までは間隔を細かくしない(地形エディタと同じ値)。
 */
const CONTOUR_REFINE_ZOOM_THRESHOLD = 6;
/** bbox取得時の件数上限 */
const FETCH_LIMIT = 10000;

/** マスターデータ未取得時の道路幅フォールバック(m)。path_type別 */
const FALLBACK_ROAD_WIDTH: Record<number, number> = {
  [PATH_MOTORWAY]: 16,
  [PATH_STREET]: 8,
  [PATH_WALKWAY]: 2,
};

/* ============================================================================
 * 純粋関数ヘルパー
 * ========================================================================== */

/**
 * カラーランプ上の位置t(0〜1)の色をCSS文字列で返す。
 * ストップ間を線形補間するだけの単純な実装で、地図の塗りと凡例の
 * グラデーションで同一のランプを共有するために使う。
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

/** ビン番号に対応する塗り色を先に作っておく(ビンの中央値の色を代表色とする) */
function buildHeatmapBinColors(kind: CityMapHeatmapActiveKind): string[] {
  const stops = HEATMAP_RAMPS[kind];
  return Array.from({ length: HEATMAP_BIN_COUNT }, (_, i) =>
    rampColor(stops, (i + 0.5) / HEATMAP_BIN_COUNT)
  );
}

/**
 * ヒートマップのカラーランプのストップ色(低→高)をCSS文字列で返す。
 * 凡例のグラデーションを地図の塗りと同じ色で描くために公開している。
 */
export function getHeatmapRampStops(kind: CityMapHeatmapActiveKind): string[] {
  return HEATMAP_RAMPS[kind].map(([r, g, b]) => `rgb(${r}, ${g}, ${b})`);
}

/**
 * 標高配列を色付けしたオフスクリーンCanvasを生成する。
 * 色は地形エディタと同じ computePixelRGB(低地=緑、高地=茶色、海面=背景色)を使う。
 */
function buildTerrainCanvas(
  elev: Float32Array,
  rows: number,
  cols: number,
  minLand: number,
  maxLand: number
): HTMLCanvasElement {
  const cv = document.createElement("canvas");
  cv.width = cols;
  cv.height = rows;
  const ctx = cv.getContext("2d")!;
  const img = ctx.createImageData(cols, rows);
  const d = img.data;
  for (let i = 0; i < rows * cols; i++) {
    const [r, g, b] = computePixelRGB(elev[i], minLand, maxLand);
    d[i * 4 + 0] = r;
    d[i * 4 + 1] = g;
    d[i * 4 + 2] = b;
    d[i * 4 + 3] = 255;
  }
  ctx.putImageData(img, 0, 0);
  return cv;
}

/** 標高レンジに応じて見やすい等高線間隔を選ぶ(約25本以下になる間隔) */
function pickContourInterval(range: number): number {
  if (range <= 0) return 100;
  const candidates = [10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000];
  for (const c of candidates) {
    if (range / c <= 25) return c;
  }
  return candidates[candidates.length - 1];
}

/** 1-2-5系列(1,2,5,10,20,50,...)の中から value 以下で最大の「きりの良い」値を返す(最小1) */
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
 * 現在のズーム倍率に応じて等高線間隔を細かくする(地形エディタと同じロジック)。
 * 基準ズーム(全体フィット表示)での間隔を baseInterval とし、そこから
 * CONTOUR_REFINE_ZOOM_THRESHOLD 倍以上ズームインした分だけ間隔を1-2-5系列で
 * 細分化する。ズームアウト方向には基準間隔より粗くしない。
 */
function pickContourIntervalForZoom(baseInterval: number, baseZoom: number, currentZoom: number): number {
  if (baseZoom <= 0) return baseInterval;
  const refineStartZoom = baseZoom * CONTOUR_REFINE_ZOOM_THRESHOLD;
  if (currentZoom <= refineStartZoom) return baseInterval;
  const factor = currentZoom / refineStartZoom;
  return Math.max(1, niceIntervalAtOrBelow(baseInterval / factor));
}

/**
 * Marching Squaresで等高線群を構築する(地形エディタと同じアルゴリズム)。
 * 各セル(2x2グリッド点)で4隅の標高がレベルを跨ぐ場合のみ、エッジ上の交点を
 * 線形補間で求めて線分を追加する。全線分はワールド座標のPath2Dに蓄積する。
 * 5本ごとの主要等高線はmajor=trueとし、描画時に線幅・色を切り替える。
 * interval は呼び出し側で決める(初回は pickContourInterval、その後はズーム倍率に
 * 応じて pickContourIntervalForZoom で細分化した値を渡す)。
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

  // 等高線レベル群(0 < L <= maxLand の interval倍数)
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

/** 指定間隔のグリッド線をワールド座標のPath2Dとして構築する */
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

/** アンカー1点分のベジエ制御点([x,z])。null/undefined は未指定(自動計算で補う) */
type AnchorControls = { cIn?: [number, number] | null; cOut?: [number, number] | null };

/** [x,y,z]または[x,z]の座標を[x,z]へ潰す。無効な座標はnull */
function toXZ(p: number[] | null | undefined): [number, number] | null {
  if (!p || p.length < 2) return null;
  return p.length >= 3 ? [p[0], p[2]] : [p[0], p[1]];
}

/**
 * パスのアンカー点列(start → waypoints → end)を[x,z]で収集する。
 * 3D座標[x,y,z]はy(標高)を捨てる。waypointは{position:[...]}形式にも対応する。
 * controls には各アンカーと同順のベジエ制御点(端点=start_control/end_control、
 * 中間={position,control_in,control_out}形式のwaypointの制御点)を格納する。
 * 無効な座標のアンカーはスキップされるが、controls も同時にスキップして
 * 点列とのインデックス対応を保つ。
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

/** ポリライン群をPath2Dへ追加する(直線パス curved=0 用) */
function appendPolyline(path: Path2D, pts: [number, number][]): void {
  if (pts.length < 2) return;
  path.moveTo(pts[0][0], pts[0][1]);
  for (let i = 1; i < pts.length; i++) path.lineTo(pts[i][0], pts[i][1]);
}

/**
 * アンカー点列を滑らかな曲線としてPath2Dへ追加する(曲線パス curved=1 用)。
 * controls に保存済みのベジエ制御点(ユーザーがドラッグで調整した値)があれば
 * それを優先し、無い箇所はCatmull-Rom由来の制御点(隣接アンカー差分の1/6)で補う。
 * 補完式はバックエンドの自動補完(_auto_fill_controls)と同じ計算。
 */
function appendSmoothPolyline(
  path: Path2D,
  pts: [number, number][],
  controls?: AnchorControls[]
): void {
  if (pts.length < 2) return;
  path.moveTo(pts[0][0], pts[0][1]);
  if (pts.length === 2) {
    // 2点のみ: 保存済み制御点があれば曲線、無ければ直線(自動補完は1/3・2/3内分=直線のため)
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
 * polygonを持つエンティティ群をPath2Dへ変換する。
 * localPolygon=trueの場合、polygonはcenter_pointからのローカル座標とみなして
 * center_pointをオフセットとして加算する(建物がこの形式)。
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

/**
 * 点群の凸包を反時計回り(Andrewのmonotone chain法)で返す。
 * 3点未満はそのまま返す。駅(プラットフォーム群)の外接ポリゴン算出に使う。
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
  // 各chainの末尾は始点と重複するため落とす
  lower.pop();
  upper.pop();
  return lower.concat(upper);
}

/**
 * 「駅」を表すPath2Dを構築する([[29_駅と駅舎]]のロジック)。
 * プラットフォーム(=マスターの platform_width が正の駅パス)を所属駅舎(house_path_id)
 * ごとにまとめ、同じ駅舎に属する全プラットフォームの占有ポリゴン頂点の凸包を
 * 1つの閉ポリゴンとして描く。どの駅舎にも所属しないプラットフォームは描かない。
 * プラットフォームが1本だけの駅舎はそのホームの矩形がそのまま凸包になる。
 */
function buildStationHullPath(paths: ApiPathItem[], md: MasterData | null): Path2D | null {
  // house_path_id → その駅舎に所属する全プラットフォームのポリゴン頂点
  const groups = new Map<number, [number, number][]>();
  for (const p of paths) {
    const houseId = p.house_path_id ?? 0;
    if (houseId <= 0) continue;
    const pw = md?.paths?.[p.path_variant]?.platform_width;
    if (!(typeof pw === "number" && pw > 0)) continue; // プラットフォーム(駅パス)のみ
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
 * 海岸線API(GeoJSONライク)のPolygon/MultiPolygonをPath2Dへ変換する。
 * 外周リング(陸地の外縁)に加え、内周リング(穴)も描く。穴は内陸の湖・池など
 * 水面ポリゴンが陸地から差し引かれてできたものであり、これを描くことで
 * 池の岸線も海岸線と同じ線で表現される(バックエンドの compute_coastline_polygon が
 * 水面 footprint を difference して穴を開けている)。
 */
function buildCoastlinePath(geojson: { type?: string; coordinates?: unknown }): Path2D {
  const path = new Path2D();
  const addRing = (ring: [number, number][]) => {
    if (!ring || ring.length < 3) return;
    path.moveTo(ring[0][0], ring[0][1]);
    for (let i = 1; i < ring.length; i++) path.lineTo(ring[i][0], ring[i][1]);
    path.closePath();
  };
  // 1つのPolygon(=[外周リング, 穴リング, ...])の全リングを描く
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

/** aがbを完全に包含しているか(マージン付き)。ビューポートカバレッジ判定に使う */
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

/* ============================================================================
 * コンポーネント本体
 * ========================================================================== */

export const CityMapCanvas = forwardRef<CityMapCanvasHandle, CityMapCanvasProps>(
  function CityMapCanvas(
    {
      apiBaseUrl,
      layers,
      heatmap = "none",
      initialBounds,
      interactive = true,
      overlayDraw,
      overlayVersion,
      animatedOverlayDraw,
      onCursorWorldPos,
      onZoomChange,
      onHeatmapStats,
      className,
    },
    ref
  ) {
    const masterData = useMasterData();
    const wrapRef = useRef<HTMLDivElement | null>(null);
    const canvasRef = useRef<HTMLCanvasElement | null>(null);
    // アニメーションオーバーレイ専用の第2キャンバス。ベースキャンバスの上に重なり、
    // requestAnimationFrameループでこのキャンバスだけを毎フレーム描き直す
    // (ベースの地図をフル再描画せずに済ませるためのレイヤー分離)。
    const animCanvasRef = useRef<HTMLCanvasElement | null>(null);

    // レイヤー・コールバック・マスターデータはイベントハンドラや非同期処理から
    // 常に最新値を参照できるようrefにミラーする(ハンドラの張り直しを避けるため)。
    const layersRef = useRef<CityMapLayerVisibility>({ ...DEFAULT_CITY_MAP_LAYERS, ...layers });
    const heatmapRef = useRef<CityMapHeatmapKind>(heatmap);
    const overlayDrawRef = useRef<CityMapCanvasProps["overlayDraw"]>(overlayDraw);
    const animatedOverlayDrawRef = useRef<CityMapCanvasProps["animatedOverlayDraw"]>(animatedOverlayDraw);
    const cursorCbRef = useRef<CityMapCanvasProps["onCursorWorldPos"]>(onCursorWorldPos);
    const zoomCbRef = useRef<CityMapCanvasProps["onZoomChange"]>(onZoomChange);
    const heatmapStatsCbRef = useRef<CityMapCanvasProps["onHeatmapStats"]>(onHeatmapStats);
    const masterDataRef = useRef<MasterData | null>(masterData);
    overlayDrawRef.current = overlayDraw;
    animatedOverlayDrawRef.current = animatedOverlayDraw;
    cursorCbRef.current = onCursorWorldPos;
    zoomCbRef.current = onZoomChange;
    heatmapStatsCbRef.current = onHeatmapStats;

    // 描画エンジンの操作関数。初期化effect内で構築し、他のeffect(レイヤー変更等)や
    // useImperativeHandleから呼び出す。
    const engineRef = useRef<{
      draw: () => void;
      drawAnimatedFrame: (timeMs: number) => void;
      resetView: () => void;
      reload: () => void;
      fitBounds: (bounds: CityMapBounds) => void;
      ensureVectors: () => void;
      ensureHeatmap: () => void;
      rebuildPathClassification: () => void;
    } | null>(null);

    // initialBoundsはオブジェクトのため、値が同じなら再初期化しないようキー化する
    const boundsKey = initialBounds
      ? `${initialBounds.x1},${initialBounds.z1},${initialBounds.x2},${initialBounds.z2}`
      : "";
    const initialBoundsRef = useRef<CityMapBounds | undefined>(initialBounds);
    initialBoundsRef.current = initialBounds;

    /* ---- レイヤー変更: refを更新し、不足データの取得と再描画を行う ---- */
    useEffect(() => {
      layersRef.current = { ...DEFAULT_CITY_MAP_LAYERS, ...layers };
      engineRef.current?.ensureVectors();
      engineRef.current?.draw();
    }, [layers]);

    /* ---- ヒートマップ種別の変更: 未取得なら取得し、区画データも揃えて再描画する ---- */
    useEffect(() => {
      heatmapRef.current = heatmap;
      engineRef.current?.ensureVectors();
      engineRef.current?.ensureHeatmap();
      engineRef.current?.draw();
    }, [heatmap]);

    /* ---- マスターデータ変更: 幹線道路の分類が変わるためPath2Dを再構築する ---- */
    useEffect(() => {
      masterDataRef.current = masterData;
      engineRef.current?.rebuildPathClassification();
      engineRef.current?.draw();
    }, [masterData]);

    /* ---- オーバーレイ更新要求: overlayDrawの描画内容が変わったときの再描画 ---- */
    useEffect(() => {
      engineRef.current?.draw();
    }, [overlayVersion]);

    /* ---- 初期化(データロード・イベント登録)。apiBaseUrl等が変わったら作り直す ---- */
    useEffect(() => {
      const canvas = canvasRef.current;
      const wrap = wrapRef.current;
      if (!canvas || !wrap || !apiBaseUrl) return;

      const ctx = canvas.getContext("2d");
      if (!ctx) return;

      // アニメーションオーバーレイ用キャンバス(取得失敗時はアニメだけ無効化して続行)
      const animCanvas = animCanvasRef.current;
      const animCtx = animCanvas?.getContext("2d") ?? null;

      const abort = new AbortController();
      let disposed = false;

      /* ---- 内部状態 ---- */
      const view: CityMapViewState = { offsetX: 0, offsetY: 0, zoom: 1 };
      let canvasW = 0;
      let canvasH = 0;

      let settings: TerrainSettings | null = null;
      let terrain: TerrainState | null = null;
      let contours: ContourLine[] = [];
      let gridMinor: Path2D | null = null;
      let gridCell: Path2D | null = null;
      let coastPath: Path2D | null = null;

      // 等高線間隔のズーム追従(地形エディタと同じ方式)。
      // contourBaseInterval/contourBaseZoomは全体フィット表示時の間隔とズーム倍率で、
      // そこからズームインした分だけ間隔を1-2-5系列で細分化する。
      // contoursは常にcontourIntervalの間隔で構築済みの状態を保つ。
      let contourBaseInterval = 100;
      let contourBaseZoom = 1;
      let contourInterval = 100;

      const vec: VectorState = {
        paths: [],
        parcels: [],
        buildings: [],
        zones: [],
        roadBands: [],
        railPath: null,
        parcelPath: null,
        buildingPath: null,
        stationHullPath: null,
        zonePathConfirmed: null,
        zonePathAuto: null,
        fetchedBounds: null,
        fetchedKinds: new Set(),
        loading: false,
      };

      const heat: HeatmapState = {
        loadedKind: "none",
        requestedKind: "none",
        values: new Map(),
        min: 0,
        max: 0,
        binPaths: null,
        binColors: [],
      };

      let vectorTimer: number | null = null;
      let contourTimer: number | null = null;

      /* ---- 座標変換 ----
       * offsetX = 画面左端(sx=0)のworld x。offsetY = 画面上端(sy=0)のworld z。
       * Y反転描画のため画面上端がzの最大側になる(screen_y = (offsetY - wz) * zoom)。 */
      const screenToWorld = (sx: number, sy: number): [number, number] => [
        sx / view.zoom + view.offsetX,
        view.offsetY - sy / view.zoom,
      ];

      const getViewBounds = (): CityMapBounds => {
        // 上端(sy=0)がz最大・下端(sy=canvasH)がz最小になるので、z1<z2となるよう並べ替える
        const [x1, zTop] = screenToWorld(0, 0);
        const [x2, zBottom] = screenToWorld(canvasW, canvasH);
        return { x1, z1: Math.min(zTop, zBottom), x2, z2: Math.max(zTop, zBottom) };
      };

      /** 指定範囲が収まるようにビューを設定する */
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
        // offsetYは画面上端のworld z(=表示範囲の最大側)。データを中央に収める余白を足す
        view.offsetY = b.z2 + (canvasH / view.zoom - dataH) / 2;
        zoomCbRef.current?.(view.zoom);
      };

      const resetView = () => {
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
          // 地形設定未取得時のフォールバック。原点(0,0)を画面中央に置く
          // (offsetYは上端のworld zなので +canvasH/2)
          view.offsetX = -canvasW / 2;
          view.offsetY = canvasH / 2;
          view.zoom = 1;
          zoomCbRef.current?.(view.zoom);
        }
      };

      /* ---- 描画 ---- */
      const draw = () => {
        if (disposed || canvasW === 0 || canvasH === 0) return;
        const L = layersRef.current;
        const dpr = window.devicePixelRatio || 1;

        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.fillStyle = BG_COLOR;
        ctx.fillRect(0, 0, canvas.width, canvas.height);

        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.save();
        // Y倍率を負にしてworld+Zを画面上向きに描く(Unity俯瞰/ミニマップと一致)。
        // world(wx,wz) → 画面: sx=(wx-offsetX)*zoom, sy=(offsetY-wz)*zoom
        ctx.translate(-view.offsetX * view.zoom, view.offsetY * view.zoom);
        ctx.scale(view.zoom, -view.zoom);

        const z = view.zoom;
        const px = (w: number) => w / z; // 画面ピクセル単位の線幅をワールド単位へ

        // 標高色分け(最大解像度で色付けした近景マップの画像を世界矩形に張る)
        if (L.elevation && terrain && settings) {
          ctx.imageSmoothingEnabled = true;
          ctx.drawImage(
            terrain.canvas,
            -settings.map_width / 2,
            -settings.map_height / 2,
            settings.map_width,
            settings.map_height
          );
        }

        // 等高線。地形色の上とレイヤーOFF時の暗い背景の上とで色を切り替える
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

        // グリッド(ズームアウトで細かすぎる場合は描かない)
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

        // ヒートマップ(騒音・大気汚染)。土地区画を環境スコアで塗り分ける。
        // 地形の上・道路や建物の下に敷くことで、都市インフラの読み取りを妨げずに
        // 「どのあたりが悪いか」を面で見せる。
        if (heat.binPaths) {
          ctx.globalAlpha = HEATMAP_ALPHA;
          for (let i = 0; i < heat.binPaths.length; i++) {
            ctx.fillStyle = heat.binColors[i];
            ctx.fill(heat.binPaths[i]);
          }
          ctx.globalAlpha = 1.0;
        }

        // 海岸線
        if (L.coastline && coastPath) {
          ctx.strokeStyle = COLOR_COASTLINE;
          ctx.lineWidth = px(2);
          ctx.setLineDash([]);
          ctx.stroke(coastPath);
        }

        // 地区の枠線。確定地区=実線、自動管理地区=破線で描き分ける
        // (自動管理地区はシステムが統合しうる「仮の区割り」であることを視覚的に伝える)
        if (L.zoneBorders) {
          ctx.strokeStyle = COLOR_ZONE;
          ctx.lineWidth = px(2);
          if (vec.zonePathConfirmed) {
            ctx.setLineDash([]);
            ctx.stroke(vec.zonePathConfirmed);
          }
          if (vec.zonePathAuto) {
            ctx.setLineDash([px(8), px(6)]);
            ctx.stroke(vec.zonePathAuto);
            ctx.setLineDash([]);
          }
        }

        // 土地区画(枠線のみ)
        if (L.parcels && vec.parcelPath) {
          ctx.strokeStyle = COLOR_PARCEL;
          ctx.lineWidth = px(1);
          ctx.stroke(vec.parcelPath);
        }

        // 駅(同じ駅舎に所属するプラットフォーム群の凸包)。薄いピンクの面+枠線で
        // 「1つの駅」の範囲を示す。プラットフォーム(線路)はこの上に描かれる。
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

        // 建物の底面形状(半透明塗り + 枠線)
        if (L.buildings && vec.buildingPath) {
          ctx.globalAlpha = 0.35;
          ctx.fillStyle = COLOR_BUILDING;
          ctx.fill(vec.buildingPath);
          ctx.globalAlpha = 1.0;
          ctx.strokeStyle = COLOR_BUILDING;
          ctx.lineWidth = px(1);
          ctx.stroke(vec.buildingPath);
        }

        ctx.lineJoin = "round";
        ctx.lineCap = "round";

        // 道路(実幅の面描画)。lineWidthにマスターの全幅(m)を使い、丸いcap/joinで
        // つなぐことで道路ポリゴンの近似として描く。ズームアウトで実幅が細くなり
        // すぎて消えないよう、画面ピクセル単位の最小幅でクランプする
        for (const band of vec.roadBands) {
          if (!(band.trunk ? L.trunkRoads : L.allRoads)) continue;
          if (band.kind === "motorway") {
            ctx.strokeStyle = COLOR_MOTORWAY;
            ctx.lineWidth = Math.max(band.width, px(2.5));
          } else {
            ctx.strokeStyle = band.trunk ? COLOR_STREET_TRUNK : COLOR_STREET;
            ctx.lineWidth = Math.max(band.width, px(1.2));
          }
          ctx.stroke(band.path);
        }

        // 線路(茶色の実線の上に背景色の破線を重ねて枕木風の見た目にする)
        if (L.railways && vec.railPath) {
          ctx.strokeStyle = COLOR_RAIL;
          ctx.lineWidth = px(2);
          ctx.stroke(vec.railPath);
          ctx.strokeStyle = BG_COLOR;
          ctx.lineWidth = px(2);
          ctx.setLineDash([px(6), px(6)]);
          ctx.stroke(vec.railPath);
          ctx.setLineDash([]);
        }

        // 上位コンポーネントの重ね描き(敷設中道路・路線図など)
        overlayDrawRef.current?.(ctx, { ...view });

        ctx.restore();

        // ビューが動いた結果、等高線の再構築・ベクターの再取得が必要ならスケジュールする
        scheduleContourZoomRefresh();
        scheduleVectorLoad();
      };

      /**
       * アニメーションオーバーレイの1フレーム描画。
       * 第2キャンバスだけをクリアして animatedOverlayDraw を呼ぶ(ベースの地図は
       * 再描画しない)。ビュー変換は draw() と同一で、view を毎フレーム読むため
       * パン・ズーム中もベースの地図と自動で同期する。コールバック未設定時は
       * クリアのみ行う(アニメ停止時の残像消去を兼ねる)。
       */
      const drawAnimatedFrame = (timeMs: number) => {
        if (disposed || !animCanvas || !animCtx || canvasW === 0 || canvasH === 0) return;
        animCtx.setTransform(1, 0, 0, 1, 0, 0);
        animCtx.clearRect(0, 0, animCanvas.width, animCanvas.height);

        const cb = animatedOverlayDrawRef.current;
        if (!cb) return;

        const dpr = window.devicePixelRatio || 1;
        animCtx.setTransform(dpr, 0, 0, dpr, 0, 0);
        animCtx.save();
        animCtx.translate(-view.offsetX * view.zoom, view.offsetY * view.zoom);
        animCtx.scale(view.zoom, -view.zoom);
        cb(animCtx, { ...view }, timeMs);
        animCtx.restore();
      };

      /* ---- 地形(標高色分け・等高線・グリッド) ---- */

      /**
       * 近景標高マップ全体(float16 LE raw)を一括取得し、地形エディタと同様に
       * 最大解像度のまま色付け画像と等高線を構築する。LOD(遠景/近景の切り替え)は
       * 持たず、以後の描画はすべてこの1枚のオフスクリーンキャンバスを拡縮して行う。
       */
      const loadTerrain = async () => {
        try {
          settings = await fetchApi<TerrainSettings>(
            buildApiUrl(apiBaseUrl, "/api/v1/terrain/settings"),
            { signal: abort.signal }
          );

          // グリッドPath2Dを構築(100m + セル境界)
          gridMinor = buildGridPath(settings.map_width, settings.map_height, GRID_MINOR_INTERVAL);
          const cellSize = settings.cell_size && settings.cell_size > 0 ? settings.cell_size : 500;
          gridCell = buildGridPath(settings.map_width, settings.map_height, cellSize);

          const spacing = settings.near_map_spacing;
          if (!spacing || spacing <= 0) {
            console.warn("[CityMapCanvas] near_map_spacing is not available");
            return;
          }

          // バイナリレスポンスのためfetchApiではなく素のfetchを使う
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

          // 陸地(標高>0)のみのmin/maxを色・等高線の基準レンジとする(地形エディタと同じ)
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
            canvas: buildTerrainCanvas(elev, rows, cols, minLand, maxLand),
            elev,
            rows,
            cols,
            spacing,
            minLand,
            maxLand,
          };

          // 等高線を基準間隔で構築する。基準ズームは初期ビュー確定後に設定され、
          // 以後はズームに応じて scheduleContourZoomRefresh が間隔を細分化する。
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
       * 現在のズーム倍率に応じた間隔で等高線を再構築する。
       * Marching Squaresはセル数に比例して重いため、ズーム操作が落ち着いてから
       * scheduleContourZoomRefresh 経由でのみ呼ばれる。
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
       * ズームで等高線間隔を細かくすべきか判定し、必要ならデバウンスして再構築する。
       * 間隔の候補値が実際に変わる場合のみスケジュールするため、パン操作や
       * 間隔が変わらない程度のズームではタイマーすら張られない。
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

      /* ---- 海岸線 ---- */
      const loadCoastline = async () => {
        try {
          const geo = await fetchApi<{ type?: string; coordinates?: unknown }>(
            buildApiUrl(apiBaseUrl, "/api/v1/terrain/coastline"),
            { signal: abort.signal }
          );
          coastPath = buildCoastlinePath(geo);
        } catch (err) {
          if (!abort.signal.aborted) console.warn("[CityMapCanvas] failed to load coastline:", err);
        }
      };

      /* ---- ヒートマップ(騒音・大気汚染) ---- */

      /** 凡例に必要な実値レンジ・件数を上位へ通知する */
      const notifyHeatmapStats = () => {
        const cb = heatmapStatsCbRef.current;
        if (!cb) return;
        cb(
          heat.loadedKind === "none"
            ? null
            : {
                kind: heat.loadedKind,
                min: heat.min,
                max: heat.max,
                count: heat.values.size,
              }
        );
      };

      /**
       * 取得済みの区画ポリゴンとヒートマップ値から、色ビンごとのPath2Dを構築する。
       * 区画データはビューポートのbboxで入れ替わるため、区画取得のたびに呼び直す。
       */
      const rebuildHeatmapPaths = () => {
        if (heat.loadedKind === "none" || heat.values.size === 0) {
          heat.binPaths = null;
          return;
        }
        const bins: Path2D[] = Array.from({ length: HEATMAP_BIN_COUNT }, () => new Path2D());
        let hasAny = false;
        for (const parcel of vec.parcels) {
          const poly = parcel.polygon;
          if (!poly || poly.length < 3 || parcel.parcel_id === undefined) continue;
          const value = heat.values.get(parcel.parcel_id);
          if (value === undefined) continue;
          const bin = Math.min(
            HEATMAP_BIN_COUNT - 1,
            Math.max(0, Math.floor(value * HEATMAP_BIN_COUNT))
          );
          const path = bins[bin];
          path.moveTo(poly[0][0], poly[0][1]);
          for (let i = 1; i < poly.length; i++) path.lineTo(poly[i][0], poly[i][1]);
          path.closePath();
          hasAny = true;
        }
        heat.binPaths = hasAny ? bins : null;
      };

      /**
       * 指定種別のヒートマップを取得する。
       * 値は都市全体の区画分がまとめて返るため、ビューポートに関係なく種別ごとに
       * 1回だけ取得すればよい(ベクターデータのようなbbox再取得は不要)。
       * 応答が返った時点で別の種別へ切り替わっていた場合は結果を捨てる。
       */
      const loadHeatmap = async (kind: CityMapHeatmapKind) => {
        heat.requestedKind = kind;
        if (kind === "none") {
          heat.loadedKind = "none";
          heat.values.clear();
          heat.binPaths = null;
          notifyHeatmapStats();
          return;
        }
        if (heat.loadedKind === kind) return;
        try {
          const data = await fetchApi<ApiHeatmap>(
            buildApiUrl(apiBaseUrl, HEATMAP_ENDPOINT[kind]),
            { signal: abort.signal }
          );
          if (disposed || heat.requestedKind !== kind) return;

          const ids = data.parcel_ids ?? [];
          const values = data.values ?? [];
          heat.values.clear();
          const count = Math.min(ids.length, values.length);
          for (let i = 0; i < count; i++) {
            heat.values.set(ids[i], values[i] / 65535);
          }
          heat.min = typeof data.min === "number" ? data.min : 0;
          heat.max = typeof data.max === "number" ? data.max : 0;
          heat.loadedKind = kind;
          heat.binColors = buildHeatmapBinColors(kind);
          rebuildHeatmapPaths();
          notifyHeatmapStats();
          draw();
        } catch (err) {
          if (!abort.signal.aborted) console.warn("[CityMapCanvas] failed to load heatmap:", err);
        }
      };

      /** ヒートマップ種別の変更effectから呼ばれる。未取得なら取得する */
      const ensureHeatmap = () => {
        void loadHeatmap(heatmapRef.current);
      };

      /* ---- ベクターデータ(パス・土地区画・建物・地区) ---- */

      /** 現在のレイヤー設定で必要なデータ種別を返す */
      const neededKinds = (): VectorKind[] => {
        const L = layersRef.current;
        const kinds: VectorKind[] = [];
        if (L.trunkRoads || L.allRoads || L.railways || L.stations) kinds.push("paths");
        // ヒートマップは区画ポリゴンを塗り分けるため、区画レイヤーがOFFでも区画が要る
        if (L.parcels || heatmapRef.current !== "none") kinds.push("parcels");
        if (L.buildings) kinds.push("buildings");
        if (L.zoneBorders) kinds.push("zones");
        return kinds;
      };

      /**
       * パスを描画区分(街路系/自動車専用道路/線路)ごとに分類してPath2Dを構築する。
       * 道路はマスターデータのwidth(全幅m)をstroke幅に使って実幅の面として描くため、
       * 幅の異なるバリアントは別のRoadBandに分ける。
       */
      const rebuildPathClassification = () => {
        const md = masterDataRef.current;
        const bands = new Map<string, RoadBand>();
        const rail = new Path2D();
        let hasRail = false;

        for (const p of vec.paths) {
          const controls: AnchorControls[] = [];
          const pts = collectPathPoints(p, controls);
          if (pts.length < 2) continue;
          // 曲線パスのみベジエ補間する。直線・折れ線パス(curved=0)を補間すると
          // 実際には存在しない曲がりが描かれてしまうため、必ずフラグで描き分ける。
          // 曲線パスは保存済み制御点(ドラッグ調整された曲線)があればそれを使う
          const append = p.curved
            ? (target: Path2D, ap: [number, number][]) => appendSmoothPolyline(target, ap, controls)
            : appendPolyline;
          if (p.path_type === PATH_TRACK) {
            append(rail, pts);
            hasRail = true;
          } else if (
            p.path_type === PATH_MOTORWAY ||
            p.path_type === PATH_STREET ||
            p.path_type === PATH_WALKWAY
          ) {
            const master = md?.paths?.[p.path_variant];
            // 幹線道路レイヤーの対象判定はマスターのtrunkフラグ。ただし自動車専用
            // 道路は都市の骨格として常に見せたいため、フラグにかかわらず幹線扱い
            const trunk = p.path_type === PATH_MOTORWAY || Boolean(master?.trunk);
            const kind: RoadBandKind = p.path_type === PATH_MOTORWAY ? "motorway" : "street";
            const width = master?.width ?? FALLBACK_ROAD_WIDTH[p.path_type] ?? 8;
            const key = `${kind}:${trunk ? 1 : 0}:${width}`;
            let band = bands.get(key);
            if (!band) {
              band = { kind, trunk, width, path: new Path2D() };
              bands.set(key, band);
            }
            append(band.path, pts);
          }
        }
        // 描画順: 非幹線街路 → 幹線街路 → 自動車専用道路(後に描くほど上に重なる)
        const rank = (b: RoadBand) => (b.kind === "motorway" ? 2 : b.trunk ? 1 : 0);
        vec.roadBands = [...bands.values()].sort((a, b) => rank(a) - rank(b));
        vec.railPath = hasRail ? rail : null;
        // 駅(駅舎ごとにまとめたプラットフォーム群の凸包)。platform_width判定に
        // マスターデータが要るため、道路分類と同じタイミングで再構築する。
        vec.stationHullPath = buildStationHullPath(vec.paths, md);
      };

      const loadVectors = async (kinds: VectorKind[], bounds: CityMapBounds) => {
        if (vec.loading || kinds.length === 0) return;
        vec.loading = true;
        const bbox = `${bounds.x1},${bounds.z1},${bounds.x2},${bounds.z2}`;

        try {
          await Promise.all(
            kinds.map(async (kind) => {
              if (kind === "zones") {
                // 地区は件数が少ないため全件取得する(bboxはcenter_point基準のため
                // 巨大ポリゴンが範囲外判定で欠落するのを避ける意図もある)
                const data = await fetchApi<{ zones: ApiPolygonItem[] }>(
                  buildApiUrl(apiBaseUrl, "/api/v1/zones"),
                  { signal: abort.signal }
                );
                vec.zones = data.zones ?? [];
                vec.zonePathConfirmed = buildPolygonPath(
                  vec.zones.filter((zn) => !zn.auto_managed),
                  false
                );
                vec.zonePathAuto = buildPolygonPath(
                  vec.zones.filter((zn) => !!zn.auto_managed),
                  false
                );
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
                // ヒートマップの塗りは区画ポリゴンそのものなので、区画の入れ替えに追従させる
                rebuildHeatmapPaths();
              } else {
                const data = await fetchApi<{ buildings: ApiPolygonItem[] }>(url, { signal: abort.signal });
                vec.buildings = data.buildings ?? [];
                // 建物のpolygonはcenter_pointからのローカル座標
                vec.buildingPath = buildPolygonPath(vec.buildings, true);
              }
            })
          );
          for (const k of kinds) vec.fetchedKinds.add(k);
          vec.fetchedBounds = bounds;
          if (!disposed) draw();
        } catch (err) {
          if (!abort.signal.aborted) console.warn("[CityMapCanvas] failed to load vector data:", err);
        } finally {
          vec.loading = false;
        }
      };

      /** ビューポート+パディングをbboxとして、現在必要なデータ種別をすべて取得する */
      const fetchVectorsForViewport = () => {
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
       * ビューポートが取得済み範囲から出た場合の再取得スケジュール。
       * パン・ズーム中に毎フレーム発火しないようデバウンスする。
       * 非インタラクティブ(埋め込み)ではビューが動かないため実質何もしない。
       */
      const scheduleVectorLoad = () => {
        if (vec.loading) return;
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

      /** レイヤーONで未取得の種別があれば取得する(レイヤー変更effectから呼ばれる) */
      const ensureVectors = () => {
        const missing = neededKinds().filter((k) => !vec.fetchedKinds.has(k));
        if (missing.length === 0) return;
        fetchVectorsForViewport();
      };

      /* ---- リサイズ ---- */
      const resizeCanvas = () => {
        const dpr = window.devicePixelRatio || 1;
        canvasW = wrap.clientWidth;
        canvasH = wrap.clientHeight;
        canvas.width = Math.max(1, Math.round(canvasW * dpr));
        canvas.height = Math.max(1, Math.round(canvasH * dpr));
        canvas.style.width = `${canvasW}px`;
        canvas.style.height = `${canvasH}px`;
        // アニメーションキャンバスもベースと同サイズ・同DPRに追従させる
        // (再描画は次のrAFフレームに任せる)
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

      /* ---- マウス操作(interactive時のみ) ---- */
      let dragging = false;
      let dragStartX = 0;
      let dragStartY = 0;
      let dragOffsetX = 0;
      let dragOffsetY = 0;

      const onMouseDown = (e: MouseEvent) => {
        if (e.button !== 0) return;
        dragging = true;
        dragStartX = e.clientX;
        dragStartY = e.clientY;
        dragOffsetX = view.offsetX;
        dragOffsetY = view.offsetY;
      };

      const onMouseMove = (e: MouseEvent) => {
        const rect = canvas.getBoundingClientRect();
        const mx = e.clientX - rect.left;
        const my = e.clientY - rect.top;
        if (mx >= 0 && mx <= canvasW && my >= 0 && my <= canvasH) {
          const [wx, wz] = screenToWorld(mx, my);
          cursorCbRef.current?.({ x: wx, z: wz });
        } else {
          cursorCbRef.current?.(null);
        }

        if (!dragging) return;
        // カーソル下のワールド座標が固定されるようにパンする。
        // Yは反転描画のため、offsetYはドラッグ量を加算する向きになる。
        view.offsetX = dragOffsetX - (e.clientX - dragStartX) / view.zoom;
        view.offsetY = dragOffsetY + (e.clientY - dragStartY) / view.zoom;
        draw();
      };

      const onMouseUp = () => {
        dragging = false;
      };

      const onMouseLeave = () => {
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
        canvas.addEventListener("mousedown", onMouseDown);
        window.addEventListener("mousemove", onMouseMove);
        window.addEventListener("mouseup", onMouseUp);
        canvas.addEventListener("mouseleave", onMouseLeave);
        canvas.addEventListener("wheel", onWheel, { passive: false });
      }

      /* ---- 起動シーケンス ---- */
      const reload = () => {
        vec.fetchedKinds.clear();
        vec.fetchedBounds = null;
        // 取得済み種別をクリアしてヒートマップも取り直す(エポック実行で値が変わるため)
        heat.loadedKind = "none";
        heat.binPaths = null;
        void (async () => {
          await Promise.all([loadTerrain(), loadCoastline()]);
          if (disposed) return;
          fetchVectorsForViewport();
          void loadHeatmap(heatmapRef.current);
          draw();
        })();
      };

      resizeCanvas();
      void (async () => {
        await Promise.all([loadTerrain(), loadCoastline()]);
        if (disposed) return;
        resetView();
        // 等高線細分化の基準ズームは初期ビュー(全体フィット、または
        // initialBounds指定範囲へのフィット)確定時のズーム倍率とする。
        contourBaseZoom = view.zoom;
        fetchVectorsForViewport();
        void loadHeatmap(heatmapRef.current);
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
        ensureVectors,
        ensureHeatmap,
        rebuildPathClassification,
      };

      return () => {
        disposed = true;
        abort.abort();
        engineRef.current = null;
        resizeObserver.disconnect();
        if (vectorTimer !== null) window.clearTimeout(vectorTimer);
        if (contourTimer !== null) window.clearTimeout(contourTimer);
        if (interactive) {
          canvas.removeEventListener("mousedown", onMouseDown);
          window.removeEventListener("mousemove", onMouseMove);
          window.removeEventListener("mouseup", onMouseUp);
          canvas.removeEventListener("mouseleave", onMouseLeave);
          canvas.removeEventListener("wheel", onWheel);
        }
      };
      // initialBoundsは値ベースのキー(boundsKey)で比較する
      // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [apiBaseUrl, interactive, boundsKey]);

    /* ---- アニメーションオーバーレイのrAFループ ----
     * animatedOverlayDrawが指定されている間だけ回す(コールバックの中身の差し替えは
     * refミラーが吸収するため、有無のbooleanだけを依存にしてループを張り直さない)。
     * エンジン未初期化のフレームは何もせず次フレームを待つ。 */
    const hasAnimatedOverlay = animatedOverlayDraw != null;
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
        // 停止時に最終フレームの描画を消す(コールバック解除済みならクリアのみ走る)
        engineRef.current?.drawAnimatedFrame(0);
      };
    }, [hasAnimatedOverlay]);

    useImperativeHandle(ref, () => ({
      resetView: () => engineRef.current?.resetView(),
      reload: () => engineRef.current?.reload(),
      fitBounds: (bounds: CityMapBounds) => engineRef.current?.fitBounds(bounds),
    }));

    return (
      <div
        ref={wrapRef}
        className={className}
        style={{ position: "relative", width: "100%", height: "100%", overflow: "hidden" }}
      >
        <canvas ref={canvasRef} style={{ position: "absolute", inset: 0, display: "block" }} />
        {/* アニメーションオーバーレイ(animatedOverlayDraw)専用の第2キャンバス。
            マウス操作は下のベースキャンバスに透過させる */}
        <canvas
          ref={animCanvasRef}
          style={{ position: "absolute", inset: 0, display: "block", pointerEvents: "none" }}
        />
      </div>
    );
  }
);
