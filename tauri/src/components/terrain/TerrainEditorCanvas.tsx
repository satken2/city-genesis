/**
 * 地形エディタのキャンバス本体。
 *
 * EditorWindow から terrain_editor ウィンドウとして開かれ、指定された地形マップの
 * 作業コピー(near.bin) を読み込んで、その解像度(nearマップ解像度)のまま描画する。
 * 地図は3枚のキャンバスを重ねて描く。最下段の WebGL2 キャンバス(terrainEditorRenderer.ts)が
 * 標高から地形の色・選択範囲のハイライト・100m間隔のグリッド・セル境界(cell_size間隔。
 * 通常のグリッドより濃く強調する)・等高線を画素単位で描き、その上の Canvas2D が確定済みの
 * 水面・川・資源・プロップ・カメラを描いてマウス操作も受け、最上段の Canvas2D がブラシや
 * 作図中の図形などマウスに追従するものを描く(renderFrame 参照)。
 *
 * マウス操作は左クリック/左ドラッグ/右クリックで役割を分けており、モード切替は持たない。
 *   - 左クリック(その場でボタンを離す): 範囲選択を単発適用(ブラシ1回分の合成、
 *     またはポリゴンの頂点追加)。ドラッグしてもブラシを連打で塗り広げることはない。
 *   - 左ドラッグ(押した位置から一定距離動かす): 画面移動(パン)。クリックかドラッグかは
 *     移動量の閾値で判定し、閾値を超えるまではパンへ移行しない。
 *   - 右クリック: 描画中のポリゴン(頂点3つ以上)を確定
 *
 * 地形編集はTauri(本ウィンドウ)内で完結し、バックエンドの状態には影響しない。
 * 編集ツールは左パネルに配置され、以下の2系統を提供する。
 *   - 手動編集: 円/四角ブラシまたは任意ポリゴンで範囲を「選択」し(ブラシは単発クリック、
 *     ポリゴンは右クリックまたは「確定」ボタンで選択にマージされる)、その後
 *     「編集方法」パネルで標高の上下・指定標高への近似・ノイズ付加のいずれかを選んで
 *     「適用」ボタンを押した時点で初めて地形が書き換わる。選択はクリックする度に
 *     最大値合成(union)でマージされるため、ブラシを何度かクリックしたりポリゴンを何度か
 *     確定したりして選択範囲を広げていける。選択範囲は「選択範囲をリセット」ボタンで
 *     明示的にクリアするまで保持される。ブラシ/ポリゴンの内側は常に重み1.0で確定し、
 *     境界のなじませ(線形/滑らか/ガウス減衰)は内側を減衰させるのではなく、境界の外側へ
 *     「境界のぼかし幅」の分だけにじみ出すように効かせる(選択(範囲指定方法)側で選ぶ)。
 *   - 自動編集: 侵食シミュレーションと海底生成を適用する。選択範囲があればその範囲を
 *     起点(海底生成は書き換え対象の制限)に、なければマップ全体へ適用する(手動編集で
 *     選択マスクを作ってから実行する)。水流侵食(雨粒パーティクルが斜面を削りながら
 *     流れて谷筋・堆積を作る)、熱侵食(安息角を超える崖・急斜面が崩れてなだらかになる)、
 *     海底生成(海面が一律0mのデータの海マスを、海岸からの距離に応じた水深へ彫り下げて
 *     陸と連続する海底を作る)の3種類。アルゴリズム本体は terrainErosion.ts /
 *     terrainSeaFloor.ts に分離されており、本ファイルはパラメータUIと実行後の
 *     再描画(地形層への標高の転送・3Dプレビュー送信)のみを担当する。
 *
 * 地形編集・水面編集・川編集に加えて「資源編集」タブがあり、天然資源エリア(鉱脈・油田
 * などのポリゴン領域)を水面と同じ作図フローでオーサリングできる。資源は経済シミュレー
 * ションのエンティティだが、埋蔵資源は地形と密接に結び付く(山に鉱脈、平野に水源など)
 * ため、地形マップ固有のオーサリング済みデータとしてここで定義する。定義した資源は
 * meta.json / サイドカーJSON(map_xxx.resources.json)に永続化され、バックエンドが
 * 新規都市の初期化時(init_game)に読み込んで資源エンティティとして配置する。
 * 資源種別の一覧はマスターデータ(resource_master)に依存するため、バックエンドが
 * 起動していない場合は資源種別を選択できない(既存の資源の表示・削除は可能)。
 */

import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";
import { save as saveFileDialog } from "@tauri-apps/plugin-dialog";
import { showNotification } from "../../utils/notification";
import { NumberStepper } from "../common/NumberStepper";
import { NEW_TERRAIN_INITIAL_ELEVATION_M } from "../../local-constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import { CLIMATE_TEMPERATE, CLIMATE_TYPES, GROUND_COVER_GRASSLAND, GROUND_COVER_TYPES } from "../../constants";
import {
  type TerrainEditorData,
  type WaterSurfacePayload,
  type WaterPolygonGeoJson,
  type RiverPayload,
  type ResourceAreaPayload,
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
  normalizeClimate,
  normalizeGroundCover,
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
import { type SeaFloorParams, applySeaFloor } from "./terrainSeaFloor";
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
import { polygonArea, polygonParts, ringSignedArea, translatePolygon } from "./riverGeometry";
import { captureTerrainThumbnail } from "./terrainThumbnail";
import { ResourcePresetHint } from "../resource/ResourcePresetHint";
import { useResourcePresetInputs } from "../../hooks/useResourcePresetInputs";
import { RESOURCE_MAX_AMOUNT_LIMIT } from "../../data/resourcePresets";
import { formatCompactAmount } from "../../utils/money";
import { resolveIntlLocale } from "../../utils/locale";
import "./TerrainEditorCanvas.css";
import { TerrainPropEditor, useTerrainPropEditor, emptyTerrainProps, type TerrainProps } from "./TerrainPropEditor";

type TerrainEditorCanvasProps = {
  /** 編集対象の地形マップID。window-registry の entity_id 経由で渡される。 */
  mapId: string;
};

/** パン・ズームのビュー状態。ドラッグ中の高頻度更新で再描画を抑えるため ref で保持する */
type ViewState = {
  offsetX: number;
  offsetY: number;
  zoom: number;
};

/** ロード済み地形の格子情報。標高配列の書き換え(手動編集)後も不変な部分のみを保持する */
type TerrainSettings = {
  mapWidth: number;
  mapHeight: number;
  spacing: number;
  rows: number;
  cols: number;
  /** 色コントラスト計算用の陸地標高レンジ。初回ロード時に固定し、編集で標高がこの範囲を
   *  超えても再計算はしない(色は1.0にクランプされるだけなので実害はなく、編集の度に
   *  マップ全体を走査して再計算するコストを避けるための単純化)。 */
  minLand: number;
  maxLand: number;
};

/** ワールド座標系(X-Z平面)の1点 */
type WorldPoint = { x: number; z: number };

/**
 * 地形エディタ内での水面(海面以外の湖など)1枚分の内部表現。
 * points は外周ポリゴンの頂点(ワールド座標XZ)。waterLevel は水面のY標高(m、海面=0)。
 * エクスポート・3Dプレビュー送信時に GeoJSON ライクな WaterSurfacePayload へ変換する。
 */
type EditorWaterSurface = { id: string; waterLevel: number; points: WorldPoint[] };

/**
 * 地形エディタ内での川1本分の内部表現。
 * GeneratedRiver(terrainRiver.ts)に以下を加えたもの。
 *   - params: 生成時のパラメータ(再生成の参考用)
 *   - carved: 川床を地形へ彫り込み済みか。「流路を計算」直後は false(プレビュー状態)で、
 *     一覧から1本ずつ「彫る」ことで true になる。エクスポート・3Dプレビューの
 *     対象になるのは彫り込み済みの川のみ。
 *   - depthCoef: この川を彫るときの深さ係数。生成時の既定値から川ごとに調整できる。
 *   - polygon: 川の範囲(彫り込み後の地形で水面より低い領域。穴=中州)。彫り込み時に抽出し、
 *     保存契約(RiverPayload.polygon)・2D/3Dプレビューの水面表示に使う。未彫り込みは null。
 *     保存データから復元した川は points の width/bedY を持たない(0 と waterY で埋める)ため、
 *     範囲の再計算は既存 polygon を膨らませた帯で行う(buildRiverPolygon の polygon 帯)。
 *   - controlPoints: ノード編集の単位となる制御点列(上流→下流)。手動で描いた川はクリックした
 *     頂点、自動生成した川は蛇行進化後の中心線を間引いたもの。ノードを動かすたびに
 *     buildRiverFromControlPoints で points を作り直す。保存データから復元した川(スプライン
 *     無し)は null で、ノード編集の対象外。
 *   - manualWidths: 手動で描いた川の上流端/下流端の川幅(m)。制御点の流量はこの幅から逆算し、
 *     区間は弧長で線形補間する。自動生成した川は null(流量は流路特定の結果をそのまま持つ)。
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
 * 川編集タブでのポインタ操作の種類。
 *   - rain: 地形編集と同じ範囲選択(自動生成の降雨範囲の指定)
 *   - draw: 流路の手動作図(左クリック=頂点追加、右クリック=川を確定)
 *   - node: 選択中の未彫り込みの川のノード編集(ドラッグ=移動、流路上のクリック=追加、ノードの右クリック=削除)
 */
type RiverPointerMode = "rain" | "draw" | "node";

/** 左ツールパネル上部のプルダウンで排他的に切り替える編集対象 */
type EditorTab = "terrain" | "water" | "river" | "resource" | "camera" | "prop";

/** ノード編集でノード・流路を拾う画面上の許容距離(px)。ワールド座標へはズームで割って換算する */
const RIVER_NODE_HIT_PX = 9;

/**
 * 地形エディタ内での天然資源エリア1件分の内部表現。
 * points は外周ポリゴンの頂点(ワールド座標XZ)。エクスポート時に GeoJSON ライクな
 * ResourceAreaPayload へ変換する。種別の表示名はマスターデータから都度解決するため持たない。
 */
type EditorResourceArea = {
  id: string;
  resourceType: number;
  maxAmount: number;
  recoveryRate: number;
  points: WorldPoint[];
};

/** 等高線間隔を標高レンジに応じて見やすい値に自動選択する(ビュー全体を映した基準ズームでの間隔) */
function pickContourInterval(range: number): number {
  if (range <= 0) return 100;
  const candidates = [10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000];
  for (const c of candidates) {
    // 約15〜25本になる間隔を採用する。レンジが小さければ細かい間隔を選ぶ。
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
 * 等高線を細分化し始めるズーム倍率の猶予。基準ズーム(地形全体が収まるフィット表示)から
 * この倍率までは間隔を細かくしない。大きな地形ではフィット表示のズームがかなり小さいため、
 * 猶予なしだと少しズームインしただけ(まだ画面全体には地形の一部しか映っていない程度)で
 * 細かい等高線が出てしまう。「かなり離れている(=まだあまり寄っていない)のに細かい等高線が
 * 出る」ことがないよう、ある程度ズームインするまでは基準間隔のまま据え置く。
 */
const CONTOUR_REFINE_ZOOM_THRESHOLD = 6;

/**
 * 左ボタンの「その場クリック(単発の選択操作)」と「ドラッグ(パン)」を区別する移動量の
 * 閾値(スクリーンピクセル)。押した位置からこの距離を超えて動かすとパンへ移行する。
 */
const CLICK_VS_DRAG_THRESHOLD_PX = 4;

/**
 * 3Dプレビューへの全状態の送信は、変更がこの時間(ms)止むまで待って1回にまとめる。全状態は標高全体を
 * 符号化し、受け取ったプレビューもメッシュを作り直すため。Undo直後に続く彫り込みプレビューの再構築
 * (150msのデバウンス)やステッパーの連打がまとまるよう、そのデバウンスより長くしてある。
 */
const PREVIEW_FULL_STATE_QUIET_MS = 200;
/** 変更が続いていても、最初の変更からこの時間(ms)が経ったら送る(プレビューが古いまま止まらないように) */
const PREVIEW_FULL_STATE_MAX_WAIT_MS = 1000;

/**
 * カメラ初期状態を新しく置くときの既定値。高さはゲーム側 CameraController の初期高度
 * (baseInitialHeight=150m)に、見下ろし角は通常プレイの俯瞰に合わせる。
 */
const CAMERA_DEFAULT_HEIGHT_M = 150;
const CAMERA_DEFAULT_PITCH_DEG = 50;
const CAMERA_DEFAULT_YAW_DEG = 0;

/**
 * 現在のズーム倍率に応じて等高線間隔を細かくする。
 *
 * 基準ズーム(地形全体が収まるフィット表示)での間隔を baseInterval とし、そこから
 * CONTOUR_REFINE_ZOOM_THRESHOLD 倍以上ズームインした分だけ間隔を1-2-5系列で細分化する。
 * ズームアウト方向には基準間隔より粗くしない(見た目の変化を「拡大したら細かくなる」方向に
 * 限定するための単純化)。
 */
function pickContourIntervalForZoom(baseInterval: number, baseZoom: number, currentZoom: number): number {
  if (baseZoom <= 0) return baseInterval;
  const refineStartZoom = baseZoom * CONTOUR_REFINE_ZOOM_THRESHOLD;
  if (currentZoom <= refineStartZoom) return baseInterval;
  const factor = currentZoom / refineStartZoom;
  return Math.max(1, niceIntervalAtOrBelow(baseInterval / factor));
}

/* ============================================================================
 * 手動編集ツールの型とロジック
 *
 * 「範囲指定方法」(円/四角ブラシ or 任意ポリゴン)でまず選択マスク(selectionMask、
 * グリッド点ごとに0..1の重みを持つ)を組み立て、「編集方法」(標高上下/指定標高に
 * 近づける/ノイズ)はその選択マスクに対して「適用」ボタンを押した時に初めて標高を
 * 書き換える。範囲指定と編集方法(適用)を分離しているため、クリックした瞬間に
 * 地形が変化することはない。ブラシもポリゴンも同じマスクへ最大値合成(union)で
 * マージされるので、何度かクリック/確定を繰り返すことで選択範囲を合成して広げられる。
 * ========================================================================== */

/** 範囲指定方法 */
type ShapeKind = "circle" | "rect" | "polygon";
/** 編集方法 */
type EditOperation = "raiseLower" | "flatten" | "noise";
/** 選択範囲の境界での周辺地形のなじませ方 */
type FalloffKind = "linear" | "smooth" | "gaussian";
/** ノイズ生成アルゴリズム */
type NoiseAlgorithm = "white" | "fractal";
/** ノイズの偏り(凹ませる/両方/膨らませる) */
type NoiseBias = "dig" | "both" | "bulge";

/** 円/四角ブラシの形状パラメータ。size はローカルX軸方向の半径(または半辺)。 */
type BrushShapeParams = { size: number; aspect: number; angleDeg: number };
/** 標高の上下パラメータ。rate の符号で上昇/下降を切り替える(「適用」1回あたりのm、選択の重みで減衰) */
type RaiseLowerParams = { rate: number };
/** 指定標高に近づけるパラメータ。rate は「適用」1回あたりに残差を詰める割合(1で選択重み1の点は即座に一致) */
type FlattenParams = { target: number; rate: number };
/** ノイズ付加パラメータ */
type NoiseToolParams = {
  algorithm: NoiseAlgorithm;
  scale: number;
  octaves: number;
  amplitude: number;
  bias: NoiseBias;
};

/** グリッド範囲(地形層への部分転送や選択範囲の外接矩形に使う) */
type DirtyRect = { rMin: number; rMax: number; cMin: number; cMax: number };

/**
 * 0..1 に正規化した「ぼかし帯の中での位置」t (0=形状の境界=重み1, 1=にじみの外端=重み0)
 * から重みを求める。falloffWeight自体は中心からの距離ではなく、ぼかし帯という
 * 局所的な区間の中でどれだけ減衰したかを表す汎用の減衰カーブとして使う。
 */
function falloffWeight(nd: number, kind: FalloffKind): number {
  const d = Math.max(0, Math.min(1, nd));
  if (kind === "linear") return 1 - d;
  if (kind === "gaussian") return Math.exp(-3 * d * d);
  // smooth: 線形減衰にスムーステップを適用し、境界付近をなだらかにする
  const w = 1 - d;
  return w * w * (3 - 2 * w);
}

/** 整数格子座標から決定論的な擬似乱数[-1,1]を得る(フレーム間で値が変わらないよう時刻に依存しない) */
function hashNoise2D(ix: number, iz: number): number {
  let n = ix * 374761393 + iz * 668265263;
  n = (n ^ (n >> 13)) * 1274126177;
  n = n ^ (n >> 16);
  return ((n & 0x7fffffff) / 0x7fffffff) * 2 - 1;
}

/** hashNoise2D を格子点として滑らかに補間するバリューノイズ */
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

/** valueNoise2D を複数オクターブ重ね合わせたフラクタルノイズ。戻り値はおおむね[-1,1]。 */
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
 * 円/四角ブラシの(回転後の)ワールド空間バウンディングボックス半径を求める。
 * feather (境界のぼかし幅)を指定すると、にじみ出た範囲まで含めた外接矩形になる
 * (選択マスクへの反映や地形層への部分転送の範囲をにじみ分だけ広げるために使う)。
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
 * 円/四角ブラシ内でのワールド座標(wx,wz)における選択重みを求める。
 *
 * 形状の内側(nd<=1)は常に重み1.0で確定する。以前は中心からの距離に応じて内側自体を
 * 減衰させていたが、それだと「円の内側がぼやける」見た目になってしまうため、ぼかしは
 * 境界の外側(nd>1)にだけ効かせるように変更した。外側は size を基準にした外接形状
 * (size+feather の相似拡大)までの間で falloffKind に従って1→0に減衰し、そこより
 * 外は0になる。size と size+feather は角度・縦横比が同じ相似形なので、どの方向でも
 * 「境界からの相対距離」は nd の値だけで表現できる(内側と外側の縮尺比が一定なため)。
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
  // ブラシのローカル座標系(中心原点、角度分だけ逆回転)に変換してから正規化する
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

/** 点(px,pz)から線分(ax,az)-(bx,bz)までの最短距離 */
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

/** レイキャスト法によるポリゴン内外判定 */
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

/** 点からポリゴン境界(各辺)までの最短距離 */
function distToPolygonBoundary(x: number, z: number, poly: WorldPoint[]): number {
  let minDist = Infinity;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const d = distToSegment(x, z, poly[j].x, poly[j].z, poly[i].x, poly[i].z);
    if (d < minDist) minDist = d;
  }
  return minDist;
}

/**
 * 円/四角ブラシの選択重みを selectionMask へ最大値合成(union)でマージする。影響したグリッド範囲(なければnull)を返す。
 * feather を渡すと、ブラシの外接矩形も外側ににじみ出た範囲まで広げて走査する。
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
 * ポリゴンの選択重みを selectionMask へ最大値合成(union)でマージする。
 * ポリゴン内部は常に重み1で確定し、ぼかしはポリゴンの外側にだけ効かせる。
 * 境界から feather 幅の分だけ外側へにじみ出し、falloffKind に従って1→0に減衰する。
 * 影響したグリッド範囲(なければnull)を返す。
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
  // 外側へのにじみ分(feather)だけ外接矩形の走査範囲を広げる
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

/** 2つのグリッド範囲を包含する範囲を返す(選択範囲の外接矩形をマージのたびに広げていくため) */
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
 * 1セル分の標高を編集方法に応じて書き換える。weight は selectionMask の値(0..1)をそのまま渡す。
 * 「適用」ボタン押下時に選択範囲の外接矩形を1回だけ走査して呼ばれるため、連続適用時のような
 * 二重加算対策(訪問済みセルの除外等)は不要。
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

/** selectionMask の外接矩形(bounds)全体に編集方法を1回適用する。「適用」ボタン押下時に呼ぶ。 */
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
 * Canvas2D の層(ベクター層・カーソル層)を消去し、世界座標のまま描けるビュー変換を掛ける。
 * 描き終えたら呼び出し側で ctx.restore() する。
 * 世界→CSS 変換は screenX = zoom*(worldX - offsetX), screenY = zoom*(offsetY - worldZ)。
 * 標高配列は行0が南端(世界-Z)なので、Y倍率を負にして北(+Z)を画面上に描く(Unityの俯瞰・
 * 都市マップ・地形層と同じ向き)。文字だけはこの反転で上下逆になるため fillWorldText で打ち消す。
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
 * ワールド座標 (x, z) に文字を描く。draw() のビュー変換は北(+Z)を画面上にするY反転を含むため、
 * そのまま fillText すると上下逆になる。描く位置へ移動してからY反転だけを打ち消す
 * (フォントサイズ・textAlign・textBaseline は呼び出し側の設定がそのまま効く)。
 */
function fillWorldText(ctx: CanvasRenderingContext2D, text: string, x: number, z: number): void {
  ctx.save();
  ctx.translate(x, z);
  ctx.scale(1, -1);
  ctx.fillText(text, 0, 0);
  ctx.restore();
}

/**
 * ブラシ(円/四角)のアウトラインをワールド座標系で描画する。draw() 内でビュー変換適用後に呼ぶ。
 * 内側の重み1の境界(実線)に加え、feather>0 のときは外側ににじみ出る範囲の外端(破線)も
 * 併せて描き、「境界の外側がぼける」という新しい挙動をプレビューでも分かるようにする。
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

/** 編集中ポリゴンのプレビュー(確定済み頂点を結ぶ線 + マウス位置への追従線)を描画する */
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
 * 水面(海面以外の湖など)を2Dキャンバスへ描画する。draw() 内でビュー変換適用後に呼ぶ。
 * 確定済みの水面は半透明の青で塗りつぶし＋外周線を描く。作図中(水面モード)の場合は、
 * 未確定ポリゴンをマウス追従線付きで青く描く。
 */
function strokeWaterSurfaces(
  ctx: CanvasRenderingContext2D,
  surfaces: EditorWaterSurface[],
  inProgressPoints: WorldPoint[],
  mouse: WorldPoint | null,
  lineWidth: number
): void {
  // 確定済み水面
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

  // 作図中の水面ポリゴン(未確定)
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
 * 資源種別ごとの描画色([塗り, 外周線])。種別IDを本数で割った余りで循環させる
 * (種別はマスターデータ由来で増減しうるため、固定の対応表ではなくパレット方式にする)。
 */
const RESOURCE_AREA_COLORS: [string, string][] = [
  ["rgba(255,170,60,0.28)", "rgba(255,195,110,0.95)"],
  ["rgba(185,120,255,0.28)", "rgba(205,155,255,0.95)"],
  ["rgba(255,95,95,0.28)", "rgba(255,135,135,0.95)"],
  ["rgba(95,220,140,0.28)", "rgba(135,240,170,0.95)"],
  ["rgba(240,215,80,0.28)", "rgba(250,230,130,0.95)"],
  ["rgba(80,225,225,0.28)", "rgba(130,240,240,0.95)"],
];

/** 資源種別IDに対応する描画色([塗り, 外周線])を返す */
function resourceAreaColors(resourceType: number): [string, string] {
  const idx = Math.abs(Math.trunc(resourceType)) % RESOURCE_AREA_COLORS.length;
  return RESOURCE_AREA_COLORS[idx];
}

/**
 * 天然資源エリアを2Dキャンバスへ描画する。draw() 内でビュー変換適用後に呼ぶ。
 * 確定済みのエリアは種別ごとの色で塗りつぶし+外周線+中心の種別ラベルを描き、
 * 作図中(資源編集タブ)の未確定ポリゴンはマウス追従線付きの破線で描く。
 * ラベルの表示名はマスターデータ未取得時のフォールバックを含めて呼び出し側が解決する。
 */
function strokeResourceAreas(
  ctx: CanvasRenderingContext2D,
  areas: EditorResourceArea[],
  labelFor: (resourceType: number) => string,
  inProgressPoints: WorldPoint[],
  mouse: WorldPoint | null,
  lineWidth: number
): void {
  for (const area of areas) {
    if (area.points.length < 3) continue;
    const [fill, stroke] = resourceAreaColors(area.resourceType);
    ctx.beginPath();
    ctx.moveTo(area.points[0].x, area.points[0].z);
    for (let i = 1; i < area.points.length; i++) ctx.lineTo(area.points[i].x, area.points[i].z);
    ctx.closePath();
    ctx.fillStyle = fill;
    ctx.fill();
    ctx.strokeStyle = stroke;
    ctx.lineWidth = lineWidth;
    ctx.stroke();

    // 種別ラベルを重心に描く(フォントサイズはズームに追従して画面上でほぼ一定になる)
    let cx = 0;
    let cz = 0;
    for (const p of area.points) { cx += p.x; cz += p.z; }
    cx /= area.points.length;
    cz /= area.points.length;
    ctx.fillStyle = stroke;
    ctx.font = `${lineWidth * 9}px sans-serif`;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    fillWorldText(ctx, labelFor(area.resourceType), cx, cz);
  }

  // 作図中の資源ポリゴン(未確定)。水面の作図中表示と同じ構成でオレンジ系にする。
  if (inProgressPoints.length > 0) {
    ctx.strokeStyle = "rgba(255,195,110,0.95)";
    ctx.fillStyle = "rgba(255,195,110,0.95)";
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
 * カメラ初期状態のマーカーを2Dキャンバスへ描画する。draw() 内でビュー変換適用後に呼ぶ。
 * 位置の丸と、yaw の向きへ伸びる視線(矢印)と視野を示す扇形を描く。yaw は北(+Z)を0とする
 * 時計回りなので、向きベクトルは (sin(yaw), cos(yaw))(2D都市マップのカメラマーカーと同じ)。
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
  // 見下ろし角が浅いほど視線は遠くへ届くので、矢印の長さで俯角の目安も伝える
  const reach = lineWidth * 40 + camera.y / Math.max(Math.tan((camera.pitch * Math.PI) / 180), 0.2);
  const halfFov = Math.PI / 6;

  ctx.save();
  ctx.lineWidth = lineWidth;
  ctx.strokeStyle = "rgba(255,230,90,0.9)";
  ctx.fillStyle = "rgba(255,230,90,0.18)";

  // 視野の扇形
  ctx.beginPath();
  ctx.moveTo(camera.x, camera.z);
  ctx.lineTo(camera.x + Math.sin(yawRad - halfFov) * reach, camera.z + Math.cos(yawRad - halfFov) * reach);
  ctx.lineTo(camera.x + dx * reach * 1.1, camera.z + dz * reach * 1.1);
  ctx.lineTo(camera.x + Math.sin(yawRad + halfFov) * reach, camera.z + Math.cos(yawRad + halfFov) * reach);
  ctx.closePath();
  ctx.fill();
  ctx.stroke();

  // 視線の矢印
  ctx.beginPath();
  ctx.moveTo(camera.x, camera.z);
  ctx.lineTo(camera.x + dx * reach, camera.z + dz * reach);
  ctx.stroke();

  // 位置
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

/** カメラ編集タブで注視点を指定する操作の目安として、カメラ位置からマウスまで破線を引く */
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

/** strokeRivers に渡す表示オプション(彫り込みプレビュー・水面表示・選択中の川)。 */
type RiverDrawOptions = {
  /** 未彫り込みの川の、プレビュー地形上で抽出した範囲ポリゴン(川ID→範囲)。プレビュー無効なら空 */
  previewPolygons: ReadonlyMap<string, WaterPolygonGeoJson | null>;
  /** false なら範囲ポリゴンの水面塗りを描かない(川底の地形を見るため) */
  waterVisible: boolean;
  /** 一覧で選択中の川ID(強調表示)。null なら無し */
  selectedId: string | null;
};

/**
 * 川の中心線・範囲ポリゴンの Path2D を形状ごとに1回だけ作るキャッシュ。川(EditorRiver)と範囲は
 * 変更のたびに新しい配列・オブジェクトへ差し替える運用(Undoスタックが同じオブジェクトを参照するため)
 * なので、配列・オブジェクト自体をキーにでき、形状が変われば作り直される。マウスを動かすたびの
 * 再描画で数千点の川を組み直さないために使う。
 */
const riverLinePathCache = new WeakMap<EditorRiver["points"], { path: Path2D; avgWidth: number }>();
const riverFillPathCache = new WeakMap<WaterPolygonGeoJson, Path2D>();

/**
 * 川を2Dキャンバスへ描画する。draw() 内でビュー変換適用後に呼ぶ。
 * - 彫り込み済みの川: 範囲ポリゴンの水面塗り(穴=中州は抜く) + 細い水面プロファイル線。
 *   ゲーム本体でどの範囲に水面が描かれるかをそのまま示す。範囲が未抽出(異常系)は中心線だけ。
 * - 未彫り込みの川: 明るい水色の破線。彫り込みプレビュー中はプレビュー地形で抽出した範囲を
 *   やや薄い水面色で塗る(彫り込み済みと区別する)。
 * - 各川の源流点に一覧と同じ番号を描き、どの線がどの川かを対応づける。番号のフォントは
 *   ズームに追従して画面上でほぼ一定の大きさになる。選択中の川は線を太く明るくする。
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
      // 彫り込み済み: 範囲ポリゴン(穴込み)を水面色で塗る。even-odd で穴を抜く
      if (river.polygon && options.waterVisible) {
        const fill = buildFill(river.polygon);
        ctx.fillStyle = "rgba(60,140,220,0.45)";
        ctx.fill(fill, "evenodd");
        ctx.strokeStyle = "rgba(120,200,255,0.9)";
        ctx.lineWidth = lineWidth * 0.75;
        ctx.setLineDash([]);
        ctx.stroke(fill);
      }
      // 水面プロファイル線(範囲の中心を通る流路)
      ctx.strokeStyle = river.polygon ? `rgba(40,110,220,${selected ? 0.95 : 0.6})` : "rgba(255,140,40,0.9)";
      ctx.lineWidth = lineWidth * 0.75 * emphasis;
      ctx.setLineDash([]);
      ctx.stroke(built.path);
    } else {
      // 未彫り込み: プレビュー範囲(あれば)を薄い水面色で塗り、明るい水色の破線で流路を示す
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

    // 番号ラベル(源流点)。一覧の「川N」と同じ番号。円で背景を敷いて地形の上でも読めるようにする
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

/** 折れ線上で (wx, wz) に最も近い点までの距離と、その点が属する線分の始点インデックス・線分内位置 t */
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

/** 制御点列の中で (wx, wz) から tol 以内にある最も近いノードのインデックス。無ければ -1 */
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
 * 手動作図中の流路を描く: 置いた頂点を結ぶ線と、マウスへの追従線。頂点には順番を振り、
 * 最後の線分の先端に矢印を付けて「上流→下流」の向きを示す。
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

  // 進行方向の矢印(最後の確定線分、追従中は追従線の先端)
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
 * ノード編集中の川の制御点ハンドルを描く。制御点を結ぶ細い破線(曲線との違いが分かるように)と、
 * 各ノードの円。ホバー中のノードは大きく黄色にする。終点(河口)はひし形で区別する。
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
  const { t, i18n } = useTranslation();
  // 資源編集タブの種別プルダウン・種別ラベル表示に使う(resource_master 由来)。
  // 地形エディタ自体はバックエンド非依存で動くため、未取得(null)でも他タブは全機能が使える。
  const masterData = useMasterData();
  // 地図は3枚のキャンバスを重ねて描く(renderFrame 参照)。下から地形層(WebGL2)、ベクター層
  // (Canvas2D。マウス操作もここで受ける)、カーソル層(Canvas2D)。
  const terrainGlCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const cursorCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const coordOverlayRef = useRef<HTMLDivElement | null>(null);
  const zoomOverlayRef = useRef<HTMLDivElement | null>(null);

  // ロード状態。分岐表示に使うため React state にする。
  const [status, setStatus] = useState<"loading" | "ready" | "error">("loading");
  const [errorMsg, setErrorMsg] = useState<string>("");

  // 地形層(地形色・選択範囲・グリッド・等高線)の描画器。初めて地形を置くときに作り、
  // アンマウントで GL 資源を解放する。WebGL2 が使えない環境では作れず、ロードがエラーになる。
  const rendererRef = useRef<TerrainEditorRenderer | null>(null);
  // 描画の予約。draw()/drawCursorLayer() は requestAnimationFrame を1つ予約するだけで、1フレームに
  // 何度呼ばれても描画は1回にまとまる。fullFramePendingRef はそのフレームで全層を描き直すか。
  const frameRequestRef = useRef<number | null>(null);
  const fullFramePendingRef = useRef(false);
  // セル境界(cell_size間隔)のグリッド間隔。cell_size はメタデータ欄で編集可能で、描画は
  // イベントハンドラのクロージャ(status 変更時にしか張り直されない)からも呼ばれるため、
  // cellSize state ではなくこの ref から読む。
  const cellGridIntervalRef = useRef<number>(500);
  const settingsRef = useRef<TerrainSettings | null>(null);
  const viewRef = useRef<ViewState>({ offsetX: 0, offsetY: 0, zoom: 1 });

  // 等高線間隔のズーム追従。baseIntervalRef/baseZoomRefは地形全体が収まる基準ズームでの
  // 間隔とその時のズーム倍率(resetView直後に確定)。描画のたびに、そこからズームインした分だけ
  // pickContourIntervalForZoomで間隔を細分化する。
  const baseIntervalRef = useRef<number>(100);
  const baseZoomRef = useRef<number>(1);

  const dragRef = useRef<{ active: boolean; startX: number; startY: number; offX: number; offY: number }>({
    active: false,
    startX: 0,
    startY: 0,
    offX: 0,
    offY: 0,
  });
  // 左ボタンを押した瞬間のスクリーン座標。ボタンを離すまで保持し、mousemoveでの移動量から
  // 「その場でのクリック(単発の選択操作)」か「ドラッグ(パン)」かを判定するために使う。
  // ドラッグへ移行した場合は dragRef.active が true になり、以後はパンとして扱われる。
  const leftDownRef = useRef<{ x: number; y: number } | null>(null);

  // 標高データ本体。「適用」ボタン押下時にのみ書き換わる(このコンポーネント内で完結し、
  // バックエンドの状態には影響しない)。書き換えたら変化した範囲を描画器へ転送する。
  const elevRef = useRef<Float32Array | null>(null);

  // 選択範囲(selectionMask)本体。グリッド点ごとに0..1の重みを持ち、ブラシ/ポリゴンで
  // クリックする度に最大値合成(union)でマージされる。「適用」ボタンで編集方法を適用する
  // 対象になり、「選択範囲をリセット」で明示的にクリアするまで保持される。
  const selectionMaskRef = useRef<Float32Array | null>(null);
  // ポリゴンが閉じた状態でのにじみプレビューの重み(0..255、格子と同じ並び)。描画器へ転送して表示する。
  const polygonPreviewWeightsRef = useRef<Uint8Array | null>(null);
  // 選択範囲の外接矩形(グリッド範囲)。「適用」時の走査範囲になるほか、選択の有無判定にも使う。
  const selectionBoundsRef = useRef<DirtyRect | null>(null);

  // 3Dプレビューウィンドウ(terrain_3d_preview)への選択範囲差分送信をrAF単位に間引くための
  // 保留矩形とスケジュール状態。マウスドラッグの度に毎回emitすると過剰になるため、
  // 1フレームに溜まった変化をまとめて1回だけ送る。
  const previewSyncRef = useRef<{ pendingSelectionRect: DirtyRect | null; rafId: number | null }>({
    pendingSelectionRect: null,
    rafId: null,
  });

  // --- 3Dプレビューとの接続(契約は terrainData.ts のイベント定義を参照) ---
  // 接続中(request_state を受けて detach されていない)プレビューの sessionId。空なら何も送らない。
  const previewSessionsRef = useRef<Set<string>>(new Set());
  // 3Dプレビューへ送る状態(標高・選択範囲・水面・川・地表種別)の世代。変更のたびに進める。
  const previewStateVersionRef = useRef(0);
  // 接続中のプレビューへ最後に送り終えた世代と、切断したプレビューが切断時点で受け取っていた世代。
  // 非表示から戻ったプレビューは、その間に状態が変わっていなければ全状態を受け取り直さない
  // (大きなマップでは全状態の受信とメッシュの作り直しに数秒かかるため)。
  const previewSentVersionRef = useRef(0);
  const detachedPreviewVersionsRef = useRef<Map<string, number>>(new Map());
  // 全状態の送信予約と、予約中の最初の変更の時刻(performance.now)。連続する変更を1回の送信にまとめる。
  const fullStateTimerRef = useRef<number | null>(null);
  const fullStatePendingSinceRef = useRef<number | null>(null);

  // --- 編集ツールのUI状態 ---
  // 「移動」と「編集」でモードを分けず、常に左クリックが範囲選択(単発)・左ドラッグが
  // 画面移動・右クリックがポリゴン確定というように、操作を専有させている。
  const [shape, setShape] = useState<ShapeKind>("circle");
  const [operation, setOperation] = useState<EditOperation>("raiseLower");
  const [brushParams, setBrushParams] = useState<BrushShapeParams>({ size: 50, aspect: 1, angleDeg: 0 });
  // 選択範囲の境界を周辺地形へどうなじませるか。ブラシ/ポリゴンいずれの選択にも共通で使う
  // (選択が確定した時点で重みとしてマスクへ焼き込まれるため、編集方法側では持たない)。
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
  // 境界のぼかし幅(ワールド単位)。ブラシ(円/四角)・ポリゴンいずれの選択にも共通で使う。
  // 形状の内側は常に重み1で、この幅の分だけ境界の外側へにじみ出すように減衰させる。
  const [featherWidth, setFeatherWidth] = useState<number>(20);
  const [polygonPointCount, setPolygonPointCount] = useState(0);
  const [polygonClosed, setPolygonClosed] = useState(false);
  // 選択範囲が存在するか(ボタンの有効/無効切り替え用。実体はselectionBoundsRef)。
  const [hasSelection, setHasSelection] = useState(false);

  // --- アクティブな編集タブのステート ---
  // 地形編集・水面編集・川編集・資源編集を排他的に切り替える。
  const metadataDialogRef = useRef<HTMLDialogElement>(null);
  const propEditor = useTerrainPropEditor(pushUndoState, draw);
  const [activeTab, setActiveTab] = useState<EditorTab>("terrain");
  const [terrainEditTab, setTerrainEditTab] = useState<"auto" | "manual">("manual");

  // --- 水面(海面以外の湖など)のUI状態 ---
  // 水面モードON中は、左クリックが水面ポリゴンの頂点追加・右クリックが形状の確定(閉じる)になる
  // (標高編集の選択操作とは独立)。水面はこのエディタ内で作成し、保存/エクスポートで
  // meta.json / サイドカーJSON に永続化する。実体は waterSurfacesRef / waterPointsRef。
  const [waterMode, setWaterMode] = useState(false);
  // 次に作成する水面のY標高(m)。heightmap と同じ絶対標高フレーム(海面=0)。
  const [waterLevelInput, setWaterLevelInput] = useState(0);
  // 作図中の水面ポリゴンが「閉じた(形状確定済み)」状態か。ポリゴン選択と同じ2段階
  // (形状を閉じる→水位を設定→水面を確定)にするためのフラグ。閉じるまでは頂点追加が続き、
  // 閉じた後は水位を設定して「水面を確定」ボタンで初めて1枚の水面として登録される。
  const [waterPolygonClosed, setWaterPolygonClosed] = useState(false);
  // 作成済み水面の枚数と、作図中ポリゴンの頂点数(ボタン表示用。実体は各ref)。
  const [waterSurfaceCount, setWaterSurfaceCount] = useState(0);
  const [waterPointCount, setWaterPointCount] = useState(0);

  // --- 天然資源エリアのUI状態 ---
  // 資源編集タブがアクティブな間は、左クリック=資源ポリゴンの頂点追加・右クリック=形状の
  // 確定(閉じる)になる(水面編集と同じ2段階: 形状を閉じる→種別・埋蔵量等を設定→確定)。
  // 実体は resourceAreasRef / resourcePointsRef。
  const [resourceMode, setResourceMode] = useState(false);
  // 次に作成する資源の種別。マスターデータ(resource_master)から選ぶ。埋蔵量上限・回復率は
  // 種別の目安から導出する(resourceInputs。作図中の頂点列 resourcePointsRef の宣言の後で求める)。
  const [resourceTypeInput, setResourceTypeInput] = useState<number | null>(null);
  // 作図中の資源ポリゴンが「閉じた(形状確定済み)」状態か(水面の waterPolygonClosed と同じ)。
  const [resourcePolygonClosed, setResourcePolygonClosed] = useState(false);
  // 作図中ポリゴンの頂点数と、作成済み資源の一覧表示用ミラー(実体は resourceAreasRef)。
  const [resourcePointCount, setResourcePointCount] = useState(0);
  const [resourceList, setResourceList] = useState<
    { id: string; resourceType: number; maxAmount: number; recoveryRate: number }[]
  >([]);

  // --- カメラ初期状態のUI状態 ---
  // カメラ編集タブがアクティブな間は、左クリック=カメラ位置の指定、右クリック=注視点の指定
  // (位置から注視点への方位を yaw にする)になる。高さ・見下ろし角・方位はカードの数値入力でも
  // 変えられる。実体は cameraRef、表示用ミラーが cameraState。マップに1つだけ持ち、
  // 未設定(null)ならゲーム側が原点の既定視点を使う。
  const [cameraMode, setCameraMode] = useState(false);
  const [cameraState, setCameraState] = useState<TerrainCameraPayload | null>(null);

  // --- 川編集のUI状態 ---
  // 川は「流路を計算」→一覧に未彫り込みとして追加→一覧で個別に削除・深さ調整・
  // 「彫る」で1本ずつ地形へ彫り込む、というフロー。計算は標高を一切変更せず、
  // 何度でもパラメータを変えて再計算できる(再計算は未彫り込みの川だけを差し替える)。
  // 降雨範囲は地形編集と同じ選択マスク(選択があればその範囲にのみ雨が降る)を流用する。
  const [riverParams, setRiverParams] = useState<RiverGenParams>({
    rainfallMmPerYear: 1000,
    minDischargeM3s: 0.5,
    meanderStrength: 0.5,
    meanderIterations: 30,
    widthCoef: 5,
    // 深さ係数の既定値。d = 係数×Q^0.4 のため、流量が小さいマップでは係数を
    // 大きくしないと彫り込みが視認できない(グリッド間隔20mに対して1m未満の溝になる)
    depthCoef: 1.5,
    enableBraiding: true,
  });
  // プレビュー結果のサマリ(川の本数・総延長km・陸上の最大流量)。null はプレビューなし。
  // maxDischargeM3s は流路が0本のとき「閾値がマップの最大流量に届いていない」ことを
  // ユーザーへ示し、最小流量・雨量の調整を促すための診断値。
  const [riverPreviewStats, setRiverPreviewStats] = useState<{
    count: number;
    totalKm: number;
    maxDischargeM3s: number;
    /** 短すぎて破棄された川の本数。0本の原因が「閾値不足」か「区間が短すぎ」かの切り分けに使う */
    discardedShortCount: number;
  } | null>(null);
  // 作成済みの川の一覧表示用(実体は riversRef)。削除・彫り込みボタンの再描画のために state にミラーする。
  // 未彫り込みは幅の最小〜最大(上流と下流で流量に応じて幅が変わることを確認できるように)、
  // 彫り込み済みは範囲ポリゴンの面積(ha)を表示する。
  const [riverList, setRiverList] = useState<
    {
      id: string;
      lengthKm: number;
      minWidth: number;
      maxWidth: number;
      carved: boolean;
      depthCoef: number;
      /** 範囲ポリゴンの面積(ha)。未彫り込み・範囲未抽出は null */
      areaHa: number | null;
      /** ノード編集できる川か(未彫り込みかつ制御点あり) */
      editable: boolean;
      /** 制御点(ノード)の数。制御点を持たない川は 0 */
      nodeCount: number;
      /** 手動作図の川の上流端/下流端の川幅(m)。自動生成した川は null */
      manualWidths: { head: number; tail: number } | null;
      /** 河口の種類(未彫り込みの川で「海・湖・合流・接続なし」の表示に使う) */
      mouth: RiverMouthKind;
    }[]
  >([]);
  // --- 彫り込みプレビュー ---
  // 未彫り込みの川を「今の深さ係数で彫ったらこうなる」を、実際の標高データ(elevRef)を変えずに
  // 表示するための仕組み。previewElevRef は elevRef のコピーに未彫り込みの川を彫った表示用配列で、
  // 地形層・3Dプレビューはこれがあればこちらを表示する(displayElev)。
  // previewPolygonsRef はプレビュー地形上で抽出した未彫り込みの川の範囲(水面表示用)。
  // 実際の彫り込み(確定)は従来どおり carveRiverObjects が elevRef へ行う。
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
  // --- 流路の手動作図・ノード編集 ---
  // 川編集タブのポインタ操作(降雨範囲の選択/川を描く/ノード編集)。タブを離れると rain に戻す。
  // 作図中の頂点列は riverDraftPointsRef、ノードのドラッグ中の状態は riverNodeDragRef、
  // マウス直下のノード(ハンドルの強調表示用)は hoveredRiverNodeRef が持つ。
  const [riverPointerMode, setRiverPointerMode] = useState<RiverPointerMode>("rain");
  const [riverDraftPointCount, setRiverDraftPointCount] = useState(0);
  // 手動作図の川の上流端/下流端の川幅(m)。作図を確定するときの初期値で、作成後は一覧の行で川ごとに変えられる。
  const [riverManualWidthHead, setRiverManualWidthHead] = useState(6);
  const [riverManualWidthTail, setRiverManualWidthTail] = useState(20);
  const [riverRouteRadius, setRiverRouteRadius] = useState(100);
  const riverDraftPointsRef = useRef<WorldPoint[]>([]);
  const riverNodeDragRef = useRef<{ riverId: string; index: number; moved: boolean } | null>(null);
  const hoveredRiverNodeRef = useRef<number>(-1);
  // 川幅の下限は設けない(小川・用水路も作れるようにする)。格子間隔より細い河道は
  // 彫り込み側(carveRivers のタルウェグ線の直接刻み)が1格子幅の連続した溝として表現する。

  // --- 地図メタデータ(保存欄) ---
  // map_width/map_height/near_spacing/grid_rows/grid_cols は標高データのグリッド構造そのものと
  // 結び付いており、リサンプリングなしに変更すると near.bin との整合性が壊れるため編集不可にし、
  // settingsRef の値をそのまま読み取り専用表示する。map_id(エクスポート先の登録ID)/
  // far_spacing/cell_size は近景標高データに影響しない独立したメタデータなので編集可能にする。
  const [mapIdInput, setMapIdInput] = useState<string>(mapId);
  const [farSpacing, setFarSpacing] = useState<number>(0);
  const [cellSize, setCellSize] = useState<number>(0);
  const [exportStatus, setExportStatus] = useState<"idle" | "exporting" | "exported" | "error">("idle");
  const [exportErrorMsg, setExportErrorMsg] = useState<string>("");

  // --- マップサイズ変更(セル単位での追加/削除) ---
  type ResizeEdge = "top" | "bottom" | "left" | "right";
  type ResizeAction = "add" | "remove";
  const [resizeEdge, setResizeEdge] = useState<ResizeEdge>("top");
  const [resizeAction, setResizeAction] = useState<ResizeAction>("add");
  const [resizeStatus, setResizeStatus] = useState<"idle" | "ok" | "error">("idle");
  const [resizeErrorMsg, setResizeErrorMsg] = useState<string>("");

  // --- 自動編集(侵食シミュレーション) ---
  // ボタンのonClickから直接読むだけなので、ポインタハンドラ用のref(toolStateRef)へは
  // ミラーしない。細かい係数は terrainErosion.ts 側の内部定数を参照。
  const [thermalParams, setThermalParams] = useState<ThermalErosionParams>({ talusAngleDeg: 33, iterations: 50 });
  const [hydraulicParams, setHydraulicParams] = useState<HydraulicErosionParams>({ droplets: 50000, erodeStrength: 0.3 });
  const [seaFloorParams, setSeaFloorParams] = useState<SeaFloorParams>({ seaLevelThreshold: 0, shelfSlopePercent: 2, maxDepth: 50 });
  // 海底生成の直近実行で書き換えたマス数。0は「海とみなせるマスが無かった」の診断表示に使う。nullは未実行。
  const [seaFloorModifiedCount, setSeaFloorModifiedCount] = useState<number | null>(null);

  // --- Undo / Redo ---
  // rivers も標高と一緒にスナップショットする。川床の彫り込みを Undo したときに、
  // 地形だけ戻って川リストが残る(またはその逆の)不整合を防ぐため。
  // 水面・資源エリアもスナップショットする。マップリサイズが標高と一緒にこれらの
  // ワールド座標を平行移動するため、リサイズを Undo したときに地形だけ戻って
  // 水面・資源がズレたまま残る不整合を防ぐ(各リストは常にイミュータブルに差し替える
  // 運用なので、配列の浅いコピーでスナップショットとして成立する)。
  type UndoState = {
    elev: Float32Array;
    mapWidth: number;
    mapHeight: number;
    spacing: number;
    rows: number;
    cols: number;
    rivers: EditorRiver[];
    waterSurfaces: EditorWaterSurface[];
    resourceAreas: EditorResourceArea[];
    props: TerrainProps;
    camera: TerrainCameraPayload | null;
  };
  const MAX_UNDO = 5;
  const undoStackRef = useRef<UndoState[]>([]);
  const redoStackRef = useRef<UndoState[]>([]);

  /** 現在の標高データ・マップ寸法・川リストを Undo スタックに記録し、Redo スタックをクリアする。 */
  function pushUndoState() {
    const elev = elevRef.current;
    const s = settingsRef.current;
    if (!elev || !s) return;
    undoStackRef.current.push({
      elev: new Float32Array(elev),
      mapWidth: s.mapWidth,
      mapHeight: s.mapHeight,
      spacing: s.spacing,
      rows: s.rows,
      cols: s.cols,
      rivers: riversRef.current.slice(),
      waterSurfaces: waterSurfacesRef.current.slice(),
      resourceAreas: resourceAreasRef.current.slice(),
      props: propEditor.current.data.current,
      camera: cameraRef.current,
    });
    if (undoStackRef.current.length > MAX_UNDO) undoStackRef.current.shift();
    redoStackRef.current = [];
  }

  /** Undo スタックから状態を復元する。現在の状態は Redo スタックに退避する。 */
  function performUndo() {
    const s = settingsRef.current;
    const target = undoStackRef.current.pop();
    if (!target || !s) return;
    // 現在の状態を redo スタックへ退避
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
        resourceAreas: resourceAreasRef.current.slice(),
        props: propEditor.current.data.current,
        camera: cameraRef.current,
      });
      if (redoStackRef.current.length > MAX_UNDO) redoStackRef.current.shift();
    }
    // rebuildTerrainVisuals が末尾で3Dプレビューへ全状態(川・水面を含む)を送るため、
    // 川・水面・資源のリストは先に復元しておく。
    restoreRivers(target.rivers);
    restoreWaterSurfaces(target.waterSurfaces);
    restoreResourceAreas(target.resourceAreas);
    propEditor.current.restore(target.props);
    restoreCamera(target.camera);
    rebuildTerrainVisuals(target.elev, target.mapWidth, target.mapHeight, target.spacing, target.rows, target.cols);
    // 復元後の川リスト・地形で彫り込みプレビューを作り直す(川編集タブ以外では解除される)
    scheduleRiverPreviewRebuild();
  }

  /** Redo スタックから状態を復元する。現在の状態は Undo スタックに退避する。 */
  function performRedo() {
    const s = settingsRef.current;
    const target = redoStackRef.current.pop();
    if (!target || !s) return;
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
        resourceAreas: resourceAreasRef.current.slice(),
        props: propEditor.current.data.current,
        camera: cameraRef.current,
      });
      if (undoStackRef.current.length > MAX_UNDO) undoStackRef.current.shift();
    }
    restoreRivers(target.rivers);
    restoreWaterSurfaces(target.waterSurfaces);
    restoreResourceAreas(target.resourceAreas);
    propEditor.current.restore(target.props);
    restoreCamera(target.camera);
    rebuildTerrainVisuals(target.elev, target.mapWidth, target.mapHeight, target.spacing, target.rows, target.cols);
    // 復元後の川リスト・地形で彫り込みプレビューを作り直す(川編集タブ以外では解除される)
    scheduleRiverPreviewRebuild();
  }

  // ポインタイベントハンドラは status が変わった時にしか張り直さないため、常に最新の
  // ツール設定を読めるよう ref にもミラーしておく。
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
    resourceMode,
    resourcePolygonClosed,
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
      resourceMode,
      resourcePolygonClosed,
      cameraMode,
      riverPointerMode,
      riverParams,
      riverManualWidthHead,
      riverManualWidthTail,
    };
  }, [shape, operation, brushParams, selectionFalloff, raiseLowerParams, flattenParams, noiseParams, featherWidth, polygonClosed, waterMode, waterLevelInput, waterPolygonClosed, resourceMode, resourcePolygonClosed, cameraMode, riverPointerMode, riverParams, riverManualWidthHead, riverManualWidthTail]);

  // 現在のブラシ中心(ワールド座標)。マウス移動の度に更新されるため ref で保持する。
  const brushWorldPosRef = useRef<WorldPoint | null>(null);
  // ポリゴンツールで確定前の頂点列。
  const polygonPointsRef = useRef<WorldPoint[]>([]);

  // === 水面(海面以外の湖など)の実体 ===
  // 作成済み水面の一覧。内部表現は {id, waterLevel, points(頂点のワールド座標列)}。
  // エクスポート・3Dプレビュー送信時に GeoJSON ライク(WaterSurfacePayload)へ変換する。
  const waterSurfacesRef = useRef<EditorWaterSurface[]>([]);
  // 水面モードで作図中(未確定)の頂点列。
  const waterPointsRef = useRef<WorldPoint[]>([]);

  // === 川の実体 ===
  // 未彫り込み(計算直後)と彫り込み済みの両方を carved フラグつきで持つ。
  // エクスポート・3Dプレビュー送信の対象になるのは彫り込み済みの川のみ。
  const riversRef = useRef<EditorRiver[]>([]);

  // === 天然資源エリアの実体 ===
  // 作成済み資源エリアの一覧と、資源編集タブで作図中(未確定)の頂点列。
  // 3Dプレビューへは送らない(資源は標高・水面と違い3D形状を持たないため)。
  const resourceAreasRef = useRef<EditorResourceArea[]>([]);
  const resourcePointsRef = useRef<WorldPoint[]>([]);

  // 次に作成する資源の埋蔵量上限・回復率。初期値は種別ごとの目安で、面積比例の目安は作図中の
  // 形状の面積に追従する(頂点の増減・形状を閉じる操作は resourcePointCount 等の state 更新を伴い、
  // そのたびに再描画されるため ref から面積を計算してよい)。
  const resourceDraftAreaM2 =
    resourcePointCount >= 3 ? Math.abs(ringSignedArea(resourcePointsRef.current.map((p) => [p.x, p.z]))) : 0;
  const resourceProductId =
    resourceTypeInput !== null ? masterData?.resource_types[resourceTypeInput]?.product_id ?? null : null;
  const resourceInputs = useResourcePresetInputs(resourceProductId, resourceDraftAreaM2);

  // === カメラ初期状態の実体 ===
  // エクスポート(meta.json / terrain_maps.yaml の camera)の対象。3Dプレビューへは送らない。
  const cameraRef = useRef<TerrainCameraPayload | null>(null);
  const backgroundRef = useRef<TerrainEditorData["background"]>(null);

  // === 地表種別 ===
  // エクスポート(meta.json / terrain_maps.yaml の ground_cover)の対象で、2D・3Dプレビュー・サムネイルの
  // 地形色のランプも決める。描画関数はイベントのクロージャから呼ばれ state を読めないため実体は ref に持つ。
  const groundCoverRef = useRef<number>(GROUND_COVER_GRASSLAND);
  const [groundCover, setGroundCover] = useState<number>(GROUND_COVER_GRASSLAND);

  // === 気候区分 ===
  // エクスポート(meta.json / terrain_maps.yaml の climate)の対象。ゲーム内の天気の出方だけを決め、エディタの描画には使わない。
  const [climate, setClimate] = useState<number>(CLIMATE_TEMPERATE);

  // 資源種別の表示名の対応表。draw()(イベントハンドラのクロージャから呼ばれ、React state を
  // 直接読めない)がラベル描画に使うため、マスターデータの変化を ref へミラーする。
  // マスターデータ未取得の間は空のままで、ラベルは「#種別ID」へフォールバックする。
  const resourceTypeNamesRef = useRef<Record<number, string>>({});
  useEffect(() => {
    const names: Record<number, string> = {};
    if (masterData) {
      for (const [id, entry] of Object.entries(masterData.resource_types)) {
        names[Number(id)] = entry.display_name;
      }
    }
    resourceTypeNamesRef.current = names;
    if (status === "ready") draw();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [masterData, status]);

  /** 資源種別IDの表示名を返す(マスターデータ未取得・未知の種別は「#ID」表記) */
  function resourceTypeLabel(resourceType: number): string {
    return resourceTypeNamesRef.current[resourceType] ?? `#${resourceType}`;
  }

  /** 範囲指定方法をポリゴン以外に切り替えたら、未確定の頂点をクリアする */
  useEffect(() => {
    if (shape !== "polygon" && polygonPointsRef.current.length > 0) {
      polygonPointsRef.current = [];
      setPolygonPointCount(0);
    }
  }, [shape]);

  /** アンマウント時、描画と全状態送信の予約を止め、地形層の GL 資源を解放する */
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

  /** アンマウント時、3Dプレビューへの選択差分送信予約(rAF)が残っていれば止める */
  useEffect(() => {
    return () => {
      if (previewSyncRef.current.rafId != null) cancelAnimationFrame(previewSyncRef.current.rafId);
    };
  }, []);

  /**
   * 3Dプレビューウィンドウ(terrain_3d_preview)との接続管理。プレビューはマウント時・再表示時に
   * request_state、非表示・アンマウント時に detach を送ってくる。request_state には編集中の
   * メモリ上の全状態を返す(エディタより後から開いた・開き直したプレビューも、ディスクの保存済み
   * データではなく編集中の状態と同期させるため)。ただし切断していた間に状態が変わっていなければ
   * 送り直さない。状態を送るのは接続中のプレビューがあるときだけ。
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
   * ビュー座標→世界座標。
   * screenX = (worldX - offsetX) * zoom、screenY = (offsetY - worldZ) * zoom の逆変換。
   * offsetY は画面上端の世界Z(北=+Zを上に描くため、画面の下へ行くほどZが減る)。
   */
  function screenToWorld(sx: number, sy: number): [number, number] {
    const v = viewRef.current;
    return [sx / v.zoom + v.offsetX, v.offsetY - sy / v.zoom];
  }

  /** 地形全体が収まるようにビューをリセットする */
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

  /** 次のフレームで地図の全層を描き直す。1フレームに何度呼んでも描画は1回にまとまる */
  function draw() {
    requestFrame(true);
  }

  /** 次のフレームでカーソル層だけを描き直す。マウスが動いただけでビューも編集データも変わらないときに使う */
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
   * 1フレーム分の描画。地図は3枚のキャンバスを重ねて描く。
   *   - 地形層(WebGL2): 地形色・選択範囲・100mグリッド・セル境界・等高線。描画器が描く
   *   - ベクター層(Canvas2D): 確定済みの水面・川・資源・プロップ・カメラ
   *   - カーソル層(Canvas2D): ブラシ・作図中の図形・川のノードなど、マウスに追従して変わるもの
   * full=false(マウスが動いただけ)のフレームはカーソル層だけを描き直し、川や資源が多くても
   * ホバーの負荷が増えないようにする。
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

    // 3枚のバッキングストアを CSS サイズ × dpr に揃える。大きさが変わると中身が消えるので全層を描き直す
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
        showGrid: 100 * v.zoom >= 6, // 線が詰まりすぎる縮尺では描かない
        cellInterval: cellGridIntervalRef.current,
        showCellGrid: cellGridIntervalRef.current * v.zoom >= 4,
        showPolygonPreview: ts.polygonClosed,
      });

      beginWorldLayer(ctx, v, dpr);
      strokeWaterSurfaces(ctx, waterSurfacesRef.current, [], null, lineW);
      // 川。彫り込み済みは濃青の帯+中心線、未彫り込み(計算直後)は水色の破線。
      strokeRivers(ctx, riversRef.current, lineW, {
        previewPolygons: previewPolygonsRef.current,
        waterVisible: riverWaterVisibleRef.current,
        selectedId: selectedRiverIdRef.current,
      });
      strokeResourceAreas(ctx, resourceAreasRef.current, resourceTypeLabel, [], null, lineW);
      propEditor.current.drawPlaced(ctx, v.zoom);
      strokeCameraMarker(ctx, cameraRef.current, lineW);
      ctx.restore();

      // オーバーレイ表示更新(直接 DOM 替換で React 再描画を回避)
      if (zoomOverlayRef.current) {
        zoomOverlayRef.current.textContent = Math.round(v.zoom * 100) + "%";
      }
    }

    beginWorldLayer(cursorCtx, v, dpr);
    const cursor = brushWorldPosRef.current;
    // ブラシ/ポリゴンのプレビュー(常時表示。モード切替を廃止したため編集ツールは常に有効)。
    // 水面モード・資源モード・川の作図/ノード編集中は標高編集のブラシ/ポリゴンプレビューは
    // 表示しない(操作が水面/資源/流路の作図に切り替わるため)。
    if (activeTabRef.current === "prop" || ts.waterMode || ts.resourceMode || ts.cameraMode || ts.riverPointerMode !== "rain") {
      // 水面/資源/カメラ/流路編集モードでは何も描かない(それぞれ下の stroke* で描く)。
    } else if (ts.shape === "polygon") {
      strokePolygonPreview(cursorCtx, polygonPointsRef.current, ts.polygonClosed ? null : cursor, lineW);
    } else if (cursor) {
      strokeBrushOutline(cursorCtx, cursor.x, cursor.z, ts.shape, ts.brushParams, ts.featherWidth, lineW);
    }
    // 作図中の水面・資源の頂点。形状を閉じたらマウス追従線は消す(閉じたポリゴンをそのまま見せる)。
    if (ts.waterMode) strokeWaterSurfaces(cursorCtx, [], waterPointsRef.current, ts.waterPolygonClosed ? null : cursor, lineW);
    if (ts.resourceMode) {
      strokeResourceAreas(cursorCtx, [], resourceTypeLabel, resourcePointsRef.current, ts.resourcePolygonClosed ? null : cursor, lineW);
    }
    // 流路の手動作図(作図中の頂点列)と、ノード編集中の川の制御点ハンドル
    if (ts.riverPointerMode === "draw") {
      strokeRiverDraft(cursorCtx, riverDraftPointsRef.current, cursor, lineW);
    } else if (ts.riverPointerMode === "node") {
      const target = nodeEditTargetRiver();
      if (target?.controlPoints) strokeRiverControlPoints(cursorCtx, target.controlPoints, hoveredRiverNodeRef.current, lineW);
    }
    if (activeTabRef.current === "prop") propEditor.current.drawDraft(cursorCtx, v.zoom, cursor);
    if (ts.cameraMode) strokeCameraAimLine(cursorCtx, cameraRef.current, cursor, lineW);
    cursorCtx.restore();
  }

  /**
   * 表示に使う標高配列。彫り込みプレビュー中はプレビュー配列、それ以外は実データ。
   * 地形層の表示(描画器への転送)と3Dプレビュー送信はこれを読む(実データを書き換える
   * 編集処理は必ず elevRef を直接使うこと)。
   */
  function displayElev(): Float32Array | null {
    return previewElevRef.current ?? elevRef.current;
  }

  /** 表示用の標高(displayElev)の rect 部分を描画器へ転送し直して再描画する。rect を省略すると全域 */
  function refreshTerrainLayer(rect?: DirtyRect) {
    const elev = displayElev();
    if (elev) rendererRef.current?.updateElevation(elev, rect);
    draw();
  }

  /**
   * 現在の標高・選択マスク・メタデータの全体を、接続中の3Dプレビューへすぐに送る。
   * 予約済みの送信はこれで済むので取り消す。refだけを読むので、どのクロージャから呼んでもよい。
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
   * 3Dプレビューへ送る状態が変わったことを記録し、接続中のプレビューがあれば全状態の送信を
   * 予約する(ロード・リサイズ・Undo/Redo・水面・川・地表種別など、差分イベントの無い変更)。
   * 呼ばれるたびに予約を後ろへずらし(最初の変更から PREVIEW_FULL_STATE_MAX_WAIT_MS まで)、
   * 送る内容は送信時点の状態で作るので、続けて起きた変更は1回の送信にまとまる。
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
   * 3Dプレビューへ差分イベントを送る。状態の変更時には先に previewStateVersionRef を進めておくこと。
   * 接続中のプレビューが無いとき、全状態の送信を予約済みのとき(その全状態に今回の変更も含まれる)は
   * 送らない。payload は送るときだけ作る(符号化が重いため)。
   */
  function emitPreviewDelta<T>(event: string, buildPayload: () => T) {
    if (previewSessionsRef.current.size === 0 || fullStateTimerRef.current != null) return;
    void emit(event, buildPayload());
    previewSentVersionRef.current = previewStateVersionRef.current;
  }

  /**
   * 選択マスクの変化(rect範囲)を3Dプレビューへ送る予約を入れる。ブラシドラッグ中は
   * mousemoveの度に呼ばれるが、1フレームに複数回来た分は合成して1回のemitにまとめる
   * (rAFで間引く)。
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

  /** 現在のブラシ位置(brushWorldPosRef)の形状を選択マスクへマージする(円/四角ツールでのクリック単発時) */
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

  /** ポリゴンツールで頂点を追加する */
  function addPolygonPoint(wx: number, wz: number) {
    polygonPointsRef.current = [...polygonPointsRef.current, { x: wx, z: wz }];
    setPolygonPointCount(polygonPointsRef.current.length);
    draw();
  }

  /** ポリゴンの頂点をすべてクリアする(未確定の下描きを破棄するだけで、選択マスクには影響しない) */
  function clearPolygonPoints() {
    polygonPointsRef.current = [];
    setPolygonPointCount(0);
    setPolygonClosed(false);
    draw();
  }

  /** 描画中のポリゴンを確定し、囲まれた範囲を選択マスクへマージしてから頂点をクリアする(標高はまだ変更しない) */
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
   * 水面(海面以外の湖など)の作成
   * ========================================================================== */

  /**
   * 編集タブを切り替える。
   * 水面編集/資源編集タブに切り替えた場合は対応する作図モード(waterMode/resourceMode)を
   * ONにし、他のタブに切り替えた場合はOFFにして作図中(未確定)の頂点を破棄する。
   * 川編集タブのポインタ操作は riverPointerMode で決まり(既定は地形編集と同じ範囲選択=降雨範囲の
   * 指定)、タブを離れるときに rain へ戻して作図中の頂点を破棄する。
   * 未彫り込みの川はタブを離れても破棄しない(一覧から個別に彫る途中の状態を保つため)。
   */
  function handleTabChange(tab: EditorTab) {
    const leavingRiver = activeTabRef.current === "river" && tab !== "river";
    activeTabRef.current = tab;
    setActiveTab(tab);
    if (tab !== "prop") propEditor.current.reset();
    // 彫り込みプレビューは川編集タブでだけ表示する。他のタブでは実データ(elevRef)への編集が
    // 走るため、プレビュー配列が表示に残ると編集結果が見えなくなる。
    if (leavingRiver) {
      clearRiverPreview();
      setRiverPointerModeSafe("rain");
    }
    if (tab === "river") scheduleRiverPreviewRebuild();
    setWaterMode(tab === "water");
    setResourceMode(tab === "resource");
    setCameraMode(tab === "camera");
    if (tab !== "water") {
      // 水面編集モードを解除する際は、作図中の未確定の頂点データを破棄する
      waterPointsRef.current = [];
      setWaterPointCount(0);
      setWaterPolygonClosed(false);
    }
    if (tab !== "resource") {
      // 資源編集モードを解除する際も同様に、作図中の未確定の頂点データを破棄する
      resourcePointsRef.current = [];
      setResourcePointCount(0);
      setResourcePolygonClosed(false);
    }
    draw();
  }

  /** 水面モードで、作図中の水面ポリゴンに頂点を追加する(形状を閉じる前のみ)。 */
  function addWaterPoint(wx: number, wz: number) {
    waterPointsRef.current = [...waterPointsRef.current, { x: wx, z: wz }];
    setWaterPointCount(waterPointsRef.current.length);
    draw();
  }

  /** 作図中(未確定)の水面ポリゴンの頂点をクリアする。 */
  function clearWaterPoints() {
    waterPointsRef.current = [];
    setWaterPointCount(0);
    setWaterPolygonClosed(false);
    draw();
  }

  /**
   * 閉じた(形状確定済みの)水面ポリゴン(頂点3つ以上)を、現在の水位(waterLevelInput)で
   * 1枚の水面として登録する。「水面を確定」ボタンから呼ぶ(右クリックは形状を閉じるだけで、
   * ここまでは実行しない=形状確定→水位設定→水面確定の2段階)。3Dプレビューにも即座に反映する。
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
    setWaterSurfaceCount(waterSurfacesRef.current.length);
    waterPointsRef.current = [];
    setWaterPointCount(0);
    setWaterPolygonClosed(false);
    draw();
    scheduleTerrain3dFullState();
  }

  /** 最後に作成した水面を1枚削除する。 */
  function removeLastWaterSurface() {
    if (waterSurfacesRef.current.length === 0) return;
    waterSurfacesRef.current = waterSurfacesRef.current.slice(0, -1);
    setWaterSurfaceCount(waterSurfacesRef.current.length);
    draw();
    scheduleTerrain3dFullState();
  }

  /** 作成済みの水面をすべて削除する。作図中の頂点も破棄する。 */
  function clearAllWaterSurfaces() {
    waterSurfacesRef.current = [];
    waterPointsRef.current = [];
    setWaterSurfaceCount(0);
    setWaterPointCount(0);
    setWaterPolygonClosed(false);
    draw();
    scheduleTerrain3dFullState();
  }

  /** 内部表現の水面一覧を、保存・送信用の GeoJSON ライク(WaterSurfacePayload)に変換する(確定済みのみ)。 */
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
   * 3Dプレビュー送信用の水面一覧。確定済みの水面に加えて、「形状を閉じたが未確定の水面」を
   * 現在の水位(waterLevelInput)で仮の1枚として含める。これにより、確定前でも水位を変えると
   * 3Dプレビューの水面が上下する。エクスポートには使わない(未確定分を含めないため)。
   * 送信予約のタイマーやイベントのクロージャからも呼ばれるので、state ではなく toolStateRef を読む。
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

  /** meta.json / サイドカーから読み込んだ WaterSurfacePayload 配列を内部表現へ変換する。 */
  function deserializeWaterSurfaces(payload: WaterSurfacePayload[] | undefined): EditorWaterSurface[] {
    if (!Array.isArray(payload)) return [];
    const result: EditorWaterSurface[] = [];
    for (const ws of payload) {
      const polygon = ws?.polygon;
      if (!polygon || polygon.type !== "Polygon") continue;
      // Polygon の coordinates は [外周, 穴...]。エディタでは外周のみを扱う。
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

  /* ============================================================================
   * 天然資源エリアの作成(資源編集タブ)
   *
   * 作図フローは水面と同じ2段階(左クリックで頂点追加→右クリックで形状を閉じる→
   * 種別・埋蔵量上限・回復率を設定して「資源を確定」で1件として登録)。
   * 登録した資源はエクスポート(サイドカー map_xxx.resources.json)の
   * 対象になり、バックエンドが新規都市の初期化時に資源エンティティとして配置する。
   * ========================================================================== */

  /** 資源編集タブで、作図中の資源ポリゴンに頂点を追加する(形状を閉じる前のみ)。 */
  function addResourcePoint(wx: number, wz: number) {
    resourcePointsRef.current = [...resourcePointsRef.current, { x: wx, z: wz }];
    setResourcePointCount(resourcePointsRef.current.length);
    draw();
  }

  /** 作図中(未確定)の資源ポリゴンの頂点をクリアする。 */
  function clearResourcePoints() {
    resourcePointsRef.current = [];
    setResourcePointCount(0);
    setResourcePolygonClosed(false);
    draw();
  }

  /** resourceAreasRef の内容を一覧表示用 state(resourceList)へミラーする。資源リストを変更したら必ず呼ぶ。 */
  function syncResourceListState() {
    setResourceList(
      resourceAreasRef.current.map((area) => ({
        id: area.id,
        resourceType: area.resourceType,
        maxAmount: area.maxAmount,
        recoveryRate: area.recoveryRate,
      })),
    );
  }

  /** 資源リストを丸ごと差し替える(ロード・Undo/Redo の復元用) */
  function restoreResourceAreas(areas: EditorResourceArea[]) {
    resourceAreasRef.current = areas;
    syncResourceListState();
  }

  /** 水面リストを丸ごと差し替える(Undo/Redo の復元用) */
  function restoreWaterSurfaces(surfaces: EditorWaterSurface[]) {
    waterSurfacesRef.current = surfaces;
    setWaterSurfaceCount(surfaces.length);
  }

  /**
   * 閉じた(形状確定済みの)資源ポリゴン(頂点3つ以上)を、現在の種別・埋蔵量上限・回復率で
   * 1件の資源エリアとして登録する。「資源を確定」ボタンから呼ぶ。種別未選択なら何もしない
   * (ボタン側も無効化されている)。手入力した値は1件ごとに捨て、次の形状は再び目安から始める。
   */
  function confirmResourceArea() {
    const poly = resourcePointsRef.current;
    if (poly.length < 3 || resourceTypeInput === null) return;
    const area: EditorResourceArea = {
      id: `resource_${Date.now()}_${Math.floor(Math.random() * 1000)}`,
      resourceType: resourceTypeInput,
      maxAmount: resourceInputs.maxAmount,
      recoveryRate: resourceInputs.recoveryRate,
      points: poly.map((p) => ({ x: p.x, z: p.z })),
    };
    resourceAreasRef.current = [...resourceAreasRef.current, area];
    syncResourceListState();
    resourcePointsRef.current = [];
    setResourcePointCount(0);
    setResourcePolygonClosed(false);
    resourceInputs.reset();
    draw();
  }

  /** 資源エリアを1件削除する。 */
  function removeResourceArea(id: string) {
    resourceAreasRef.current = resourceAreasRef.current.filter((area) => area.id !== id);
    syncResourceListState();
    draw();
  }

  /** 作成済みの資源エリアをすべて削除する。作図中の頂点も破棄する。 */
  function clearAllResourceAreas() {
    resourceAreasRef.current = [];
    syncResourceListState();
    clearResourcePoints();
  }

  /** 内部表現の資源エリア一覧を、保存用の GeoJSON ライク(ResourceAreaPayload)に変換する。 */
  function serializeResourceAreas(): ResourceAreaPayload[] {
    return resourceAreasRef.current.map((area) => ({
      id: area.id,
      resource_type: area.resourceType,
      max_amount: area.maxAmount,
      recovery_rate: area.recoveryRate,
      polygon: {
        type: "Polygon" as const,
        coordinates: [area.points.map((p) => [p.x, p.z])],
      },
    }));
  }

  /* ============================================================================
   * カメラ初期状態(カメラ編集タブ)
   *
   * 左クリックでカメラ位置(x, z)を置き、右クリックで注視点を指定して方位(yaw)を決める。
   * 高さ(y)・見下ろし角(pitch)・方位はカードの数値入力でも変えられる。マップに1つだけ持ち、
   * エクスポート(terrain_maps.yaml の camera)の対象になる。
   * ゲームは新規都市の開始時にこの視点で始まり、以後はセーブデータ側の視点が正になる。
   * ========================================================================== */

  /** cameraRef を差し替えて表示用 state へミラーする。カメラを変更したら必ずここを通す。 */
  function restoreCamera(camera: TerrainCameraPayload | null) {
    cameraRef.current = camera;
    setCameraState(camera);
  }

  /** カメラ位置をワールド座標 (wx, wz) に置く。未設定なら高さ・向きは既定値で新規作成する。 */
  function placeCamera(wx: number, wz: number) {
    const current = cameraRef.current;
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
   * 注視点 (wx, wz) の方向へカメラを向ける(位置から注視点への方位を yaw にする)。
   * yaw は北(+Z)を0とする時計回りなので atan2(dx, dz)。位置と同じ点なら向きは変えない。
   */
  function aimCameraAt(wx: number, wz: number) {
    const current = cameraRef.current;
    if (!current) return;
    const dx = wx - current.x;
    const dz = wz - current.z;
    if (Math.hypot(dx, dz) < 1e-6) return;
    const yaw = ((Math.atan2(dx, dz) * 180) / Math.PI + 360) % 360;
    restoreCamera({ ...current, yaw });
    draw();
  }

  /** カメラの高さ・見下ろし角・方位のいずれかを数値入力から更新する。 */
  function updateCameraField(field: "y" | "yaw" | "pitch", value: number) {
    const current = cameraRef.current;
    if (!current) return;
    restoreCamera({ ...current, [field]: value });
    draw();
  }

  /** カメラ初期状態を未設定に戻す(ゲームは原点の既定視点で始まる)。 */
  function clearCamera() {
    restoreCamera(null);
    draw();
  }

  /** 作業コピー(meta.json)の camera を検証して復元する。数値が揃っていなければ未設定扱い。 */
  function deserializeCamera(payload: TerrainCameraPayload | null | undefined): TerrainCameraPayload | null {
    if (!payload || typeof payload !== "object") return null;
    const fields = ["x", "y", "z", "yaw", "pitch"] as const;
    for (const key of fields) {
      if (typeof payload[key] !== "number" || !Number.isFinite(payload[key])) return null;
    }
    return { x: payload.x, y: payload.y, z: payload.z, yaw: payload.yaw, pitch: payload.pitch };
  }

  /** meta.json / サイドカーから読み込んだ ResourceAreaPayload 配列を内部表現へ変換する。 */
  function deserializeResourceAreas(payload: ResourceAreaPayload[] | undefined): EditorResourceArea[] {
    if (!Array.isArray(payload)) return [];
    const result: EditorResourceArea[] = [];
    for (const ra of payload) {
      const polygon = ra?.polygon;
      if (!polygon || polygon.type !== "Polygon") continue;
      // Polygon の coordinates は [外周, 穴...]。エディタでは外周のみを扱う。
      const exterior = (polygon.coordinates as number[][][])[0];
      if (!Array.isArray(exterior) || exterior.length < 3) continue;
      if (typeof ra.resource_type !== "number") continue;
      result.push({
        id: ra.id ?? `resource_${Date.now()}_${Math.floor(Math.random() * 1000)}`,
        resourceType: ra.resource_type,
        maxAmount: typeof ra.max_amount === "number" ? ra.max_amount : 1000,
        recoveryRate: typeof ra.recovery_rate === "number" ? ra.recovery_rate : 0,
        points: exterior.map((c) => ({ x: c[0], z: c[1] })),
      });
    }
    return result;
  }

  /* ============================================================================
   * 川の生成(川編集タブ)
   *
   * 「流路を計算」(プレビュー、標高不変)→「川床を彫って確定」(標高書き換え+Undo対応)の
   * 2段階フロー。アルゴリズム本体は terrainRiver.ts に分離されており、本ファイルは
   * パラメータUIと実行後の再描画のみを担当する。降雨範囲は地形編集の選択マスクを流用する
   * (選択があればその範囲にのみ雨が降る。侵食の buildErosionRegion と同じ)。
   * ========================================================================== */

  /** 川1本の総延長(km)を求める */
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

  /** riversRef の内容を一覧表示用 state(riverList)へミラーする。川リストを変更したら必ず呼ぶ。 */
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
   * 彫り込み済みの全ての川について、現在の地形から範囲ポリゴンを抽出し直す。
   *
   * 合流部で本流と支流の範囲が重ならないよう、流量(河口の q)の大きい川から順に処理し、
   * 先に範囲を得た川の格子点を後続の川から除外する(claimed)。保存データから復元した川は
   * q=0 なので、生成した川の後にリスト順で処理される。
   *
   * 探索帯は、流路スプラインを持つ川(このセッションで生成・彫り込んだ川。width>0)は
   * 「中心線±(幅/2+格子の半対角幅+なじみ帯+1格子)」、復元した川は既存範囲を上限とする。
   * 周辺斜面の低地へは広げず、範囲内で水没する部分を取り直す。
   * 彫り込み(carve)のたびに呼ぶほか、「川の範囲を再計算」ボタンからも呼ぶ。
   */
  function recomputeRiverPolygons(s: TerrainSettings, elev: Float32Array) {
    const claimed = new Uint8Array(s.rows * s.cols);
    const lakes = serializeWaterSurfaces();
    const order = riversRef.current
      .map((river, index) => ({ river, index }))
      .filter((e) => e.river.carved)
      .sort((a, b) => (b.river.points[b.river.points.length - 1]?.q ?? 0) - (a.river.points[a.river.points.length - 1]?.q ?? 0));
    // Undo スタックのスナップショットが同じ EditorRiver オブジェクトを参照しているため、
    // 範囲は in-place で書き換えず、新しいオブジェクトへ差し替える。
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

  /** 「川の範囲を再計算」ボタン: 現在の地形で範囲ポリゴンを取り直し、2D/3Dプレビューを更新する */
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
   * 彫り込みプレビュー
   *
   * 未彫り込みの川を、実データを変えずに「今の深さ係数で彫った地形」として表示する。
   * 深さ係数のステッパーを動かすたびに作り直し、気に入ったら「確定して彫る」で実データへ
   * 反映する。水面表示を切ると範囲の塗りと3Dの水面が消え、川底の地形だけが見える。
   * ========================================================================== */

  /** 表示中の標高(実データまたはプレビュー)で地形層と3Dプレビューを更新する */
  function refreshDisplayedTerrain() {
    refreshTerrainLayer();
    scheduleTerrain3dFullState();
  }

  /**
   * 地表種別を差し替える。repaint なら2D表示を描き直して3Dプレビューにも送り直す(選択範囲・表示位置は保つ)。
   * 2D表示の地形色は描画時にランプを渡して塗るので、標高の転送はやり直さない。
   */
  function applyGroundCover(value: unknown, repaint: boolean) {
    const next = normalizeGroundCover(value);
    groundCoverRef.current = next;
    setGroundCover(next);
    if (repaint) {
      draw();
      scheduleTerrain3dFullState();
    }
  }

  /**
   * 彫り込みプレビューを作り直す。未彫り込みの川が無い/プレビュー無効/川編集タブ以外なら解除する。
   * 実データのコピーに未彫り込みの川を各自の深さ係数で彫り、その地形で範囲ポリゴンを抽出する
   * (彫り込み済みの川は既存の範囲を種にして先に領域を取り、合流部の重なりを除く)。
   */
  function rebuildRiverPreview() {
    const s = settingsRef.current;
    const elev = elevRef.current;
    if (!s || !elev) return;
    const uncarved = riversRef.current.filter((river) => !river.carved);
    if (!riverPreviewEnabledRef.current || activeTabRef.current !== "river" || uncarved.length === 0) {
      previewElevRef.current = null;
      previewPolygonsRef.current = new Map();
      // 実データ表示へ戻す。彫り込み確定直後(実データが変わった)にもここを通るため、
      // プレビューの有無にかかわらず標高全域を描画器へ転送し直す
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

  /** プレビューの再構築をデバウンスして予約する(ステッパー連打で毎回全域を彫り直さないため) */
  function scheduleRiverPreviewRebuild() {
    if (previewRebuildTimerRef.current != null) window.clearTimeout(previewRebuildTimerRef.current);
    previewRebuildTimerRef.current = window.setTimeout(() => {
      previewRebuildTimerRef.current = null;
      rebuildRiverPreview();
    }, 150);
  }

  /** 彫り込みプレビューを解除し、実データの表示へ戻す */
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
   * 2D/3Dプレビューの表示に使う川の一覧。保存用(serializeRivers)と違い、水面表示が OFF なら空、
   * ON なら彫り込み済みの川に加えて未彫り込みの川のプレビュー範囲も含める。
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

  /** 一覧の行クリック: 川を選択して2Dキャンバス上で強調表示する(同じ行の再クリックで解除) */
  function selectRiver(id: string) {
    const next = selectedRiverIdRef.current === id ? null : id;
    selectedRiverIdRef.current = next;
    setSelectedRiverId(next);
    draw();
  }

  /** 川リストを丸ごと差し替える(Undo/Redo の復元用) */
  function restoreRivers(rivers: EditorRiver[]) {
    riversRef.current = rivers;
    syncRiverListState();
  }

  /**
   * 「流路を計算」ボタン: 現在の地形とパラメータから川ネットワークを抽出し、蛇行進化まで
   * 適用した流路を「未彫り込み」の川として一覧へ追加する。標高は一切変更しないため、
   * 雨量・閾値・降雨範囲を変えて何度でも押し直せる(再計算は未彫り込みの川だけを差し替え、
   * 彫り込み済みの川は残す)。彫り込みは一覧の「彫る」ボタンで1本ずつ行う。
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
        // ノード編集用に中心線を間引いた制御点を持たせる(初めてノードを動かすまで points は蛇行進化の結果のまま)
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
    // 新しい未彫り込みの川を、今の深さ係数で彫ったプレビューとして表示する
    rebuildRiverPreview();
  }

  /**
   * 彫り込み後の共通処理: 川の範囲ポリゴンの抽出(彫り込み済み全川)・地形層への標高全域の
   * 転送・3Dプレビューへの全状態送信。複数の川がマップ全域に及ぶため、侵食の finishErosion と
   * 同じく全域を転送し直す。3Dプレビューへは川リストも変わるため、標高差分ではなく full_state を送る。
   */
  function finishRiverCarve(s: TerrainSettings, elev: Float32Array) {
    recomputeRiverPolygons(s, elev);
    // 残った未彫り込みの川のプレビューを新しい実データの上で作り直す(無ければ実データ表示へ戻る)。
    // どちらの経路でも地形層(標高全域)と3Dプレビューが更新される。
    rebuildRiverPreview();
  }

  /**
   * 未彫り込みの川群を標高データへ彫り込み、彫り込み済み(carved=true)の新しいオブジェクトへ
   * 差し替える。Undo スタックのスナップショットが同じオブジェクトを参照しているため、
   * carved フラグは in-place で書き換えない(書き換えると Undo で地形だけ戻って
   * 川が彫り込み済みのまま残る)。
   */
  function carveRiverObjects(s: TerrainSettings, elev: Float32Array, targets: EditorRiver[]) {
    for (const river of targets) {
      // 川ごとの深さ係数を川床標高(bedY)へ反映してから彫る
      applyRiverDepth(river, river.depthCoef);
    }
    carveRivers(elev, s.rows, s.cols, s.spacing, s.mapWidth, s.mapHeight, targets);
    const ids = new Set(targets.map((river) => river.id));
    riversRef.current = riversRef.current.map((river) =>
      ids.has(river.id) ? { ...river, carved: true } : river,
    );
  }

  /**
   * 一覧の「彫る」ボタン: 未彫り込みの川1本の川床を、その川の深さ係数で標高データへ
   * 彫り込む。彫り込みは Undo(Ctrl+Z)で川リストごと元に戻せる。
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

  /** 「未彫り込みをすべて彫る」ボタン: 未彫り込みの川を一括で彫り込む(Undoは1回分) */
  function carveAllUncarvedRivers() {
    const s = settingsRef.current;
    const elev = elevRef.current;
    const targets = riversRef.current.filter((river) => !river.carved);
    if (!s || !elev || targets.length === 0) return;
    pushUndoState();
    carveRiverObjects(s, elev, targets);
    finishRiverCarve(s, elev);
  }

  /** 一覧の深さ係数ステッパー: 未彫り込みの川1本の深さ係数を変更する(彫るときに反映される) */
  function setRiverDepthCoef(id: string, depthCoef: number) {
    const river = riversRef.current.find((r) => r.id === id);
    if (!river || river.carved) return;
    river.depthCoef = depthCoef;
    syncRiverListState();
    // 深さを変えたら彫り込みプレビューを作り直す(連打はデバウンス)
    scheduleRiverPreviewRebuild();
  }

  /**
   * 川を1本削除する。未彫り込みの川は計算結果を破棄するだけ。彫り込み済みの川は
   * データ(スプライン・水面リボン・保存対象)のみ削除し、彫り込み済みの地形は戻さない
   * (地形も戻したい場合は Ctrl+Z を使う)。
   */
  function removeRiver(id: string) {
    riversRef.current = riversRef.current.filter((river) => river.id !== id);
    syncRiverListState();
    // 削除した川のプレビュー彫り込みも消す(残りの川で作り直す)
    rebuildRiverPreview();
  }

  /** 川をすべて削除する(地形は戻さない) */
  function clearAllRivers() {
    riversRef.current = [];
    syncRiverListState();
    // 削除した川のプレビュー彫り込みも消す(残りの川で作り直す)
    rebuildRiverPreview();
  }

  /**
   * 内部表現の川一覧を、保存・送信用の RiverPayload に変換する(彫り込み済みかつ範囲が
   * 抽出できた川のみ)。範囲が空の川(彫り込んでも水面より低い格子点が無かった等)は
   * ゲーム側に渡しても意味が無いため除く。
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
   * meta.json / サイドカーから読み込んだ RiverPayload 配列を内部表現へ変換する。
   * 流量・曲率・幅・川床など生成時の中間量は保存していないため 0(川床は水面標高)で埋める。
   * 復元した川は再彫り込みには使わず、表示・削除・範囲の再計算・再保存のみに使う
   * (範囲の再計算は幅ではなく既存ポリゴンの膨張帯で行う)。
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
        // 保存されている川は彫り込み済みのもののみ(serializeRivers が彫り込み済みだけを書く)
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
   * 流路の手動作図・ノード編集
   *
   * 川編集タブのポインタ操作は riverPointerMode で切り替える(RiverPointerMode 参照)。
   * 流路の実体は制御点列(EditorRiver.controlPoints)で、ノードを動かすたびに
   * terrainRiver.ts の buildRiverFromControlPoints で中心線・河口・水面プロファイルを
   * 作り直す(標高は変えない)。彫り込みは従来どおり一覧の「確定して彫る」で行う。
   * 本流を動かしたときは、そこへ合流している支流の終点を本流の新しい中心線へ吸着し直して
   * 連鎖的に作り直す(合流が切れないように)。
   * ========================================================================== */

  /**
   * ポインタ操作を切り替える。draw 以外へ移るときは作図中の頂点を破棄し、ドラッグ・ホバー状態も
   * 消す。ポインタハンドラは toolStateRef を読むため、state の反映(次の描画)を待たずに
   * ref も直接更新してから再描画する。
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

  /** ノード編集の対象(一覧で選択中の、制御点を持つ未彫り込みの川)。無ければ null */
  function nodeEditTargetRiver(): EditorRiver | null {
    const id = selectedRiverIdRef.current;
    if (!id) return null;
    const river = riversRef.current.find((r) => r.id === id);
    if (!river || river.carved || !river.controlPoints) return null;
    return river;
  }

  /** ノード・流路を拾う許容距離(ワールド座標m)。画面上で一定のpx数になるようズームで換算する */
  function riverHitToleranceWorld(): number {
    return RIVER_NODE_HIT_PX / viewRef.current.zoom;
  }

  /** 流路(中心線)を拾う許容距離。細い川でも画面上の最小px数、太い川は川幅の半分まで許す */
  function riverLineToleranceWorld(river: EditorRiver, wx: number, wz: number): number {
    const near = nearestOnPolyline(river.points, wx, wz);
    const width = near ? river.points[near.index].width : 0;
    return Math.max(riverHitToleranceWorld(), width / 2);
  }

  /**
   * 手動作図の川の各制御点に流量を割り当てる。川幅を上流端→下流端で弧長に対して線形補間し、
   * hydraulic geometry の逆算(dischargeFromWidth)で流量へ変換する。
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
   * 制御点列から川を作り直して riversRef を差し替える。作り直した川へ合流している未彫り込みの
   * 支流は、終点を新しい中心線上の最近点へ吸着させて連鎖的に作り直す(河口の種類は保つ)。
   * 川リストの一覧表示も更新する。描画・プレビューの更新は呼び出し側で行う。
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

  /** 両端と接続先を保ち、指定した横断帯の低い地形へ未彫り込みの流路を寄せる。 */
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

  /** 作図モードで、作図中の流路に頂点を追加する(上流→下流の順) */
  function addRiverDraftPoint(wx: number, wz: number) {
    riverDraftPointsRef.current = [...riverDraftPointsRef.current, { x: wx, z: wz }];
    setRiverDraftPointCount(riverDraftPointsRef.current.length);
    draw();
  }

  /** 作図中の流路の頂点をすべて破棄する */
  function clearRiverDraft() {
    riverDraftPointsRef.current = [];
    setRiverDraftPointCount(0);
    draw();
  }

  /**
   * 作図中の頂点列(2点以上)を1本の未彫り込みの川として登録する。頂点はそのまま制御点になり、
   * 流量は上流端/下流端の川幅から逆算する。終点の位置から河口(海・湖・合流・接続なし)を判定し、
   * 登録後はその川を選択して(ノード編集へすぐ移れるように)作図をクリアする。
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

  /** ノードをドラッグで移動する(ドラッグ中に毎回呼ぶ。終点を動かした場合は河口を判定し直す) */
  function moveRiverNode(river: EditorRiver, index: number, wx: number, wz: number) {
    if (!river.controlPoints) return;
    const cps = river.controlPoints.slice();
    cps[index] = { ...cps[index], x: wx, z: wz };
    applyRiverControlPoints(river.id, cps, index !== cps.length - 1);
  }

  /** 流路上をクリックした位置に制御点を挿入する(流量は前後の制御点から線分内位置で補間) */
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

  /** 制御点を1つ削除する(2点は残す。終点を消した場合は新しい終点で河口を判定し直す) */
  function deleteRiverNode(river: EditorRiver, index: number) {
    const cps = river.controlPoints;
    if (!cps || cps.length <= 2 || index < 0 || index >= cps.length) return;
    pushUndoState();
    hoveredRiverNodeRef.current = -1;
    applyRiverControlPoints(river.id, cps.filter((_, i) => i !== index), index !== cps.length - 1);
    draw();
    rebuildRiverPreview();
  }

  /** 一覧の川幅ステッパー: 手動作図の川の上流端/下流端の川幅を変え、流路を作り直す */
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

  /** ノード編集モードで、選択中の川のノードがカーソル直下にあればそのインデックスを返す */
  function hitSelectedRiverNode(wx: number, wz: number): { river: EditorRiver; index: number } | null {
    const target = nodeEditTargetRiver();
    if (!target?.controlPoints) return null;
    const index = findRiverNodeAt(target.controlPoints, wx, wz, riverHitToleranceWorld());
    return index >= 0 ? { river: target, index } : null;
  }

  /**
   * ノード編集モードでの左クリック(ノード以外): 選択中の川の流路上ならその位置に制御点を挿入し、
   * 他の未彫り込みの川の流路上ならその川を選択する。
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

  /** ノード編集モードのマウス移動: ハンドルの強調とカーソル形状を更新する */
  function updateRiverNodeHover(wx: number, wz: number) {
    const hit = hitSelectedRiverNode(wx, wz);
    const index = hit ? hit.index : -1;
    if (canvasRef.current) canvasRef.current.style.cursor = index >= 0 ? "grab" : "";
    hoveredRiverNodeRef.current = index;
  }

  /** ポリゴンが閉じた状態のとき、ぼかし幅を含む仮の選択範囲の重みを作って描画器へ転送する */
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

  // polygonClosed, featherWidth, selectionFalloff の変更を監視し、ポリゴンプレビューを更新する。
  // 閉じていない間は描画側が表示しない(showPolygonPreview)ので、重みは次に閉じたときに作り直す。
  useEffect(() => {
    if (polygonClosed) refreshPolygonPreviewTexture();
    else draw();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [polygonClosed, featherWidth, selectionFalloff]);

  /** 選択範囲(ブラシ/ポリゴンでマージ済みのマスク)を完全にクリアする */
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

  /** 現在の選択範囲に、現在選ばれている編集方法を1回適用する(「適用」ボタン押下時) */
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
   * 現在の選択範囲(あれば)を侵食の対象領域指定へ変換する。選択が無ければ undefined を返し、
   * 呼び出し側はマップ全域への侵食にフォールバックする。selectionMask と外接矩形をそのまま渡す。
   */
  function buildErosionRegion(): ErosionRegion | undefined {
    const bounds = selectionBoundsRef.current;
    const mask = selectionMaskRef.current;
    if (!bounds || !mask) return undefined;
    return { mask, rMin: bounds.rMin, rMax: bounds.rMax, cMin: bounds.cMin, cMax: bounds.cMax };
  }

  /**
   * 自動編集(侵食)の適用後共通処理。侵食は選択範囲を起点にしても土砂や雨粒が境界を越えて
   * 流れ、変化範囲が選択矩形ちょうどにはならないため、選択の有無に関わらず全域を描画器へ
   * 転送し直し、3Dプレビューへ全域の標高差分を送る。
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
   * 「熱侵食」実行ボタン: 安息角を超える崖・急斜面を崩すシミュレーションを同期実行する。
   * 選択範囲があればその範囲を起点に、なければマップ全体へ適用する。
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
   * 「水流侵食」実行ボタン: 雨粒パーティクルによる侵食シミュレーションを同期実行する。
   * 選択範囲があればその範囲に雨粒を降らせ(雨粒数は選択面積で自動按分)、なければマップ全体へ適用する。
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
   * 「海底生成」実行ボタン: 標高が判定値以下のマスを海とみなし、海岸線からの距離に
   * 応じた水深プロファイルへ彫り下げる。国土地理院DEMのように海面が一律0mのデータを
   * 取り込んだとき、陸の地形と連続する自然な海底を一括生成するための操作。
   * 選択範囲があればその範囲内のマスのみ書き換える(湖など海にしたくない0m地帯の保護用)。
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

  /** 地形層の描画器を返す。未作成なら作る。WebGL2 が使えない環境では Error を投げる */
  function ensureRenderer(): TerrainEditorRenderer {
    if (rendererRef.current) return rendererRef.current;
    const canvas = terrainGlCanvasRef.current;
    const renderer = canvas ? TerrainEditorRenderer.create(canvas, draw) : null;
    if (!renderer) throw new Error(t("terrainEditor.webglUnavailable"));
    rendererRef.current = renderer;
    return renderer;
  }

  /**
   * 標高配列と新しいマップ寸法を与えて、地形層(描画器の標高・選択範囲)・選択マスク・
   * settingsRef を一括で作り直す。ロード・マップリサイズ・Undo/Redo の共通処理。
   * 標高レンジ(minLand, maxLand)も再計算し、resetView() と draw() も行う。彫り込みプレビューは
   * 古い標高から作ったもので格子の寸法も違いうるので破棄する(作り直しの予約は呼び出し側)。
   * 描画器に地形を置けない(WebGL2 が無い・GPUのテクスチャ上限を超える)ときは、何も変えずに Error を投げる。
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
    // グリッド形状自体が変わりうるうえ選択マスクも全クリアしたので、3Dプレビューへ全状態を送り直す。
    scheduleTerrain3dFullState();
  }

  /**
   * ワールド座標を持つベクター系の編集データ(水面・川・資源エリア・各作図中の頂点列)を
   * (dx, dz) だけ平行移動する。マップリサイズ専用の補正処理。
   *
   * ワールド座標の原点はマップ中心なので、端へのセル追加/削除でマップ寸法が変わると、
   * 同じ地形コンテンツのワールド座標が step/2 だけずれる。標高配列は handleResizeMap が
   * 行・列の詰め替えで追従させるが、ベクター系は座標そのものを持つため、ここで同じ量だけ
   * 動かして地形との位置関係を保つ。各リストはイミュータブルに差し替える(Undoスタックが
   * 浅いコピーのスナップショットを保持しており、要素をin-placeで書き換えるとスナップ
   * ショットまで壊れるため)。
   */
  function shiftVectorFeatures(dx: number, dz: number) {
    if (dx === 0 && dz === 0) return;
    waterSurfacesRef.current = waterSurfacesRef.current.map((ws) => ({
      ...ws,
      points: ws.points.map((p) => ({ x: p.x + dx, z: p.z + dz })),
    }));
    riversRef.current = riversRef.current.map((river) => ({
      ...river,
      points: river.points.map((p) => ({ ...p, x: p.x + dx, z: p.z + dz })),
      polygon: river.polygon ? translatePolygon(river.polygon, dx, dz) : null,
      controlPoints: river.controlPoints ? river.controlPoints.map((p) => ({ ...p, x: p.x + dx, z: p.z + dz })) : null,
    }));
    riverDraftPointsRef.current = riverDraftPointsRef.current.map((p) => ({ x: p.x + dx, z: p.z + dz }));
    resourceAreasRef.current = resourceAreasRef.current.map((area) => ({
      ...area,
      points: area.points.map((p) => ({ x: p.x + dx, z: p.z + dz })),
    }));
    waterPointsRef.current = waterPointsRef.current.map((p) => ({ x: p.x + dx, z: p.z + dz }));
    resourcePointsRef.current = resourcePointsRef.current.map((p) => ({ x: p.x + dx, z: p.z + dz }));
    propEditor.current.shift(dx, dz);
    polygonPointsRef.current = polygonPointsRef.current.map((p) => ({ x: p.x + dx, z: p.z + dz }));
    if (cameraRef.current) {
      restoreCamera({ ...cameraRef.current, x: cameraRef.current.x + dx, z: cameraRef.current.z + dz });
    }
  }

  /**
   * マップサイズをセル単位で変更する(追加/削除)。
   * map_width/map_height は 2*cell_size 刻みで増減する。
   * 追加時は新規エリアを新規地形作成時と同じ初期標高(`NEW_TERRAIN_INITIAL_ELEVATION_M`、海面より低い)で埋め、
   * 削除時は指定端から切り落とす。水面・川・資源エリアなどのベクター系データは
   * shiftVectorFeatures で標高配列と同じだけ平行移動し、地形との位置関係を保つ。
   */
  function handleResizeMap() {
    const s = settingsRef.current;
    const elev = elevRef.current;
    if (!s || !elev || cellSize <= 0) return;

    pushUndoState();

    const stepMeters = 2 * cellSize;
    const stepGrids = stepMeters / s.spacing;
    if (stepGrids !== Math.floor(stepGrids) || stepGrids <= 0) {
      setResizeStatus("error");
      setResizeErrorMsg(t("terrainEditor.resizeGridError", { defaultValue: "セルサイズ×2 が 近景間隔で割り切れません" }));
      return;
    }

    let newMapWidth = s.mapWidth;
    let newMapHeight = s.mapHeight;
    const deltaGrids = stepGrids;

    if (resizeEdge === "left" || resizeEdge === "right") {
      newMapWidth = resizeAction === "add" ? newMapWidth + stepMeters : newMapWidth - stepMeters;
    } else {
      newMapHeight = resizeAction === "add" ? newMapHeight + stepMeters : newMapHeight - stepMeters;
    }

    if (newMapWidth < stepMeters || newMapHeight < stepMeters) {
      setResizeStatus("error");
      setResizeErrorMsg(t("terrainEditor.resizeMinReached", { defaultValue: "マップサイズが最小に達しました" }));
      return;
    }

    const newCols = newMapWidth / s.spacing + 1;
    const newRows = newMapHeight / s.spacing + 1;
    const maxTextureSize = rendererRef.current?.maxTextureSize ?? Infinity;
    if (newCols > maxTextureSize || newRows > maxTextureSize) {
      setResizeStatus("error");
      setResizeErrorMsg(t("terrainEditor.terrainTooLargeForGpu", { cols: newCols, rows: newRows, max: maxTextureSize }));
      return;
    }
    const newCount = newRows * newCols;
    const newElev = new Float32Array(newCount);
    const oldCols = s.cols;
    const oldRows = s.rows;

    if (resizeAction === "add") {
      // 追加される新規エリアを、新規地形作成時と同じ初期標高(海面より低い)で埋める。
      // 既存エリアはこのあとの分岐で上書きされる。
      newElev.fill(NEW_TERRAIN_INITIAL_ELEVATION_M);
      if (resizeEdge === "top") {
        for (let r = 0; r < oldRows; r++) {
          for (let c = 0; c < oldCols; c++) newElev[r * newCols + c] = elev[r * oldCols + c];
        }
      } else if (resizeEdge === "bottom") {
        const rowOff = deltaGrids;
        for (let r = 0; r < oldRows; r++) {
          for (let c = 0; c < oldCols; c++) newElev[(r + rowOff) * newCols + c] = elev[r * oldCols + c];
        }
      } else if (resizeEdge === "left") {
        const colOff = deltaGrids;
        for (let r = 0; r < oldRows; r++) {
          for (let c = 0; c < oldCols; c++) newElev[r * newCols + c + colOff] = elev[r * oldCols + c];
        }
      } else {
        for (let r = 0; r < oldRows; r++) {
          for (let c = 0; c < oldCols; c++) newElev[r * newCols + c] = elev[r * oldCols + c];
        }
      }
    } else {
      if (resizeEdge === "top") {
        for (let r = 0; r < newRows; r++) {
          for (let c = 0; c < newCols; c++) newElev[r * newCols + c] = elev[r * oldCols + c];
        }
      } else if (resizeEdge === "bottom") {
        const rowOff = oldRows - newRows;
        for (let r = 0; r < newRows; r++) {
          for (let c = 0; c < newCols; c++) newElev[r * newCols + c] = elev[(r + rowOff) * oldCols + c];
        }
      } else if (resizeEdge === "left") {
        const colOff = oldCols - newCols;
        for (let r = 0; r < newRows; r++) {
          for (let c = 0; c < newCols; c++) newElev[r * newCols + c] = elev[r * oldCols + c + colOff];
        }
      } else {
        for (let r = 0; r < newRows; r++) {
          for (let c = 0; c < newCols; c++) newElev[r * newCols + c] = elev[r * oldCols + c];
        }
      }
    }

    // 既存の地形コンテンツはワールド原点(マップ中心)から見て step/2 だけ平行移動する
    // (例: 上端に追加→中心が新規エリア側へ寄るため、既存コンテンツは反対側へ半分ずれる)。
    // 標高の詰め替え(上のループ)と同じずれ量をベクター系データにも適用する。
    const half = stepMeters / 2;
    let shiftX = 0;
    let shiftZ = 0;
    if (resizeEdge === "left") shiftX = resizeAction === "add" ? half : -half;
    else if (resizeEdge === "right") shiftX = resizeAction === "add" ? -half : half;
    else if (resizeEdge === "bottom") shiftZ = resizeAction === "add" ? half : -half;
    else shiftZ = resizeAction === "add" ? -half : half;
    shiftVectorFeatures(shiftX, shiftZ);

    rebuildTerrainVisuals(newElev, newMapWidth, newMapHeight, s.spacing, newRows, newCols);
    // 彫り込みプレビューは rebuildTerrainVisuals が破棄するので、新しい地形で作り直す(川編集タブ以外では解除のまま)
    scheduleRiverPreviewRebuild();

    setResizeStatus("ok");
  }

  /**
   * 「3Dプレビューを開く」ボタン押下時、terrain_3d_preview ウィンドウを開く。
   * single_per_entity ポリシー(entity_type: terrain_map)のため、同じマップIDに対しては
   * 既に開いているウィンドウが前面に出るだけで多重には開かない。
   */
  async function handleOpen3dPreview() {
    try {
      await invoke("open_registered_window", { windowType: "terrain_3d_preview", entityId: mapId });
    } catch (err) {
      console.error("[TerrainEditor] failed to open 3D preview window", err);
    }
  }

  /**
   * 編集中の標高データを、バックエンドが読み込める .npy ファイルへエクスポートする。
   *
   * 保存先パスは @tauri-apps/plugin-dialog の保存ダイアログでユーザーに選ばせ、
   * その後 Tauriコマンド export_terrain_as_npy に現在の標高データ(メモリ上の elev 配列)を
   * そのまま渡す。.npy への変換はRust側が自前で行う(外部プロセスに依存しない)。
   * サムネイル画像(長辺512px・海が透過の平面図)も保存する標高データから生成し、.npyと同じファイル名
   * (拡張子だけ.png)で同じ場所に書き出す。
   */
  async function handleExportTerrainAsNpy() {
    const s = settingsRef.current;
    const elev = elevRef.current;
    if (!s || !elev) return;

    const suggestedName = `map_${mapIdInput.trim() || mapId}.npy`;
    let outPath: string | null;
    try {
      outPath = await saveFileDialog({
        defaultPath: suggestedName,
        filters: [{ name: "NumPy", extensions: ["npy"] }],
      });
    } catch (error) {
      setExportStatus("error");
      setExportErrorMsg(error instanceof Error ? error.message : String(error));
      return;
    }
    if (!outPath) return;

    setExportStatus("exporting");
    setExportErrorMsg("");
    try {
      const bytes = encodeFloat16Array(elev);
      const dataBase64 = uint8ToBase64(bytes);
      const thumbnailBase64 = captureTerrainThumbnail({
        elev: decodeFloat16Array(bytes, elev.length),
        rows: s.rows,
        cols: s.cols,
        mapWidth: s.mapWidth,
        mapHeight: s.mapHeight,
        spacing: s.spacing,
        groundCover: groundCoverRef.current,
      });
      const result = await invoke<{ yaml_updated: boolean }>("export_terrain_as_npy", {
        mapId: mapIdInput.trim() || mapId,
        mapWidth: s.mapWidth,
        mapHeight: s.mapHeight,
        nearSpacing: s.spacing,
        farSpacing,
        cellSize,
        gridRows: s.rows,
        gridCols: s.cols,
        dataBase64,
        outPath,
        thumbnailBase64,
        waterSurfaces: serializeWaterSurfaces(),
        rivers: serializeRivers(),
        resources: serializeResourceAreas(),
        props: propEditor.current.data.current,
        camera: cameraRef.current,
        background: backgroundRef.current,
        groundCover: groundCoverRef.current,
        climate,
      });
      setExportStatus("exported");
      // エクスポート先にterrain_maps.yamlが見つからなかった場合は、通知ポップアップで知らせる。
      // 見つかった場合はyaml側への追記/上書きまでTauri側で完了しているので、ここでは何もしない。
      if (!result.yaml_updated) {
        void showNotification({
          message: t("terrainEditor.exportYamlNotFound", {
            defaultValue: "エクスポート先に terrain_maps.yaml が見つからなかったため、追記していません",
          }),
        });
      }
    } catch (error) {
      setExportStatus("error");
      setExportErrorMsg(error instanceof Error ? error.message : String(error));
    }
  }

  /**
   * 地形データをロードする。
   * Tauri コマンド load_terrain_editor_data でローカル作業コピーを読み込み、
   * float16 を float32 に変換した上でオフスクリーン地形画像・等高線・グリッドを構築する。
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
        const data = await invoke<TerrainEditorData>("load_terrain_editor_data", { mapId });
        backgroundRef.current = data.background;
        if (cancelled) return;
        applyGroundCover(data.ground_cover, false);
        setClimate(normalizeClimate(data.climate));

        const bytes = base64ToUint8(data.data_base64);
        const count = data.grid_rows * data.grid_cols;
        if (count <= 0) throw new Error("empty terrain data");
        if (bytes.byteLength < count * 2) throw new Error("terrain data too short");

        const elev = decodeFloat16Array(bytes, count);
        const spacing = data.near_spacing > 0 ? data.near_spacing : 10;
        const mapWidth = data.map_width;
        const mapHeight = data.map_height;

        // 地図メタデータ欄の初期値。map_id はロード元のIDから開始し、変更すれば別のIDでエクスポートする。
        // cellSize state は rebuildTerrainVisuals より先にセットする（すでに引数で渡しているので
        // 実害はないが、後続のセル境界グリッドuseEffectが古い値で走るのを避けるため）。
        setMapIdInput(data.map_id);
        setFarSpacing(data.far_spacing > 0 ? data.far_spacing : 200);
        setCellSize(data.cell_size > 0 ? data.cell_size : 500);

        // 標高レンジ計算・地形層への転送・選択マスクの初期化・settingsRef 設定・リセットビュー・描画
        // は rebuildTerrainVisuals で一括実行する。
        rebuildTerrainVisuals(
          elev,
          mapWidth,
          mapHeight,
          spacing,
          data.grid_rows,
          data.grid_cols,
          data.cell_size > 0 ? data.cell_size : 500,
        );

        // 作業コピー(meta.json)に保存されている水面(海面以外の湖など)を復元する。
        waterSurfacesRef.current = deserializeWaterSurfaces(data.water_surfaces);
        setWaterSurfaceCount(waterSurfacesRef.current.length);
        waterPointsRef.current = [];
        setWaterPointCount(0);

        // 作業コピー(meta.json)に保存されている川を復元する(川床は標高データに彫り込み済み)。
        restoreRivers(deserializeRivers(data.rivers));
        setRiverPreviewStats(null);

        // 作業コピー(meta.json)に保存されている天然資源エリアを復元する。
        restoreResourceAreas(deserializeResourceAreas(data.resources));
        propEditor.current.restore(data.props ?? emptyTerrainProps());
        resourcePointsRef.current = [];
        setResourcePointCount(0);
        setResourcePolygonClosed(false);

        // 作業コピー(meta.json)に保存されているカメラ初期状態を復元する。
        restoreCamera(deserializeCamera(data.camera));

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

  /** ウィンドウリサイズで再描画 */
  useEffect(() => {
    function onResize() {
      draw();
    }
    window.addEventListener("resize", onResize);
    return () => window.removeEventListener("resize", onResize);
  }, []);

  /** ツール設定(形状・ブラシ形状)を変えたら即座にプレビューへ反映する */
  useEffect(() => {
    if (status === "ready") draw();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status, shape, brushParams]);

  /**
   * 水位(waterLevelInput)や「形状を閉じた」状態が変わったら、3Dプレビューへ全状態を再送する。
   * 形状を閉じた未確定の水面は現在の水位で仮表示されるため、水位を変えると3Dの水面が上下する。
   */
  useEffect(() => {
    if (status === "ready") scheduleTerrain3dFullState();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status, waterLevelInput, waterPolygonClosed]);

  /** セルサイズ(メタデータ欄で編集可能)を変えたら、セル境界グリッドの間隔を差し替える */
  useEffect(() => {
    if (status === "ready" && cellSize > 0) {
      cellGridIntervalRef.current = cellSize;
      draw();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status, cellSize]);

  /**
   * マウス操作: 左クリック(その場で離す)でブラシ/ポリゴンの範囲選択を単発適用、
   * 左ドラッグで画面移動、右クリックでポリゴン確定。
   *
   * 左ボタンはクリックとドラッグの両方を担うため、mousedown時点ではどちらか確定しない。
   * mousemoveで移動量が CLICK_VS_DRAG_THRESHOLD_PX を超えた瞬間にパンへ移行し
   * (dragRef.active = true)、以後はパンとして扱う。閾値を超えないままmouseupを
   * 迎えた場合のみ、そのボタンを離した位置でブラシ/頂点追加を1回だけ実行する。
   */
  useEffect(() => {
    const cv = canvasRef.current;
    if (!cv) return;
    // cv は非null確定だが、ネスト関数内の TS ナローイングが効かないため別変数で束縛する
    const canvasEl: HTMLCanvasElement = cv;

    function onDown(e: MouseEvent) {
      const ts = toolStateRef.current;

      // 右ボタン: 水面/資源モード中は作図中のポリゴン(頂点3つ以上)の「形状」を確定(閉じる)する。
      // ここでは登録まで行わず、パラメータ(水位/資源種別等)を設定してから確定ボタンで登録する2段階。
      // 通常時はポリゴン描画中(頂点3つ以上)ならポリゴン作成を完了(閉じる)する。それ以外は何もしない。
      if (e.button === 2) {
        if (activeTabRef.current === "prop") { propEditor.current.closePolygon(); return; }
        if (ts.riverPointerMode === "draw") {
          // 流路の作図: 右クリックで川を確定する(2点未満なら何もしない)
          finishRiverDraft();
          return;
        }
        if (ts.riverPointerMode === "node") {
          // ノード編集: ノードの右クリックで削除する
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
        if (ts.resourceMode) {
          if (resourcePointsRef.current.length >= 3 && !ts.resourcePolygonClosed) setResourcePolygonClosed(true);
          return;
        }
        if (ts.cameraMode) {
          // カメラモード: 右クリック位置を注視点として、カメラの向き(yaw)を決める。
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
      // 左ボタン: この時点ではクリック/ドラッグどちらになるか未確定。
      // パンの起点(現在のオフセット)だけ記録しておき、実際にパンへ移行するかは onMove で判定する。
      const v = viewRef.current;
      leftDownRef.current = { x: e.clientX, y: e.clientY };
      dragRef.current = { active: false, startX: e.clientX, startY: e.clientY, offX: v.offsetX, offY: v.offsetY };
      // ノード編集: ノードの上で押した場合はパンではなくノードのドラッグにする(移動の確定は onMove の閾値判定)
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
        // カーソル座標表示
        if (coordOverlayRef.current) {
          coordOverlayRef.current.textContent = `${wx.toFixed(1)}, ${wz.toFixed(1)}`;
        }
        // ブラシ/ポリゴンのプレビュー追従のため、常に最新のワールド座標を保持する
        brushWorldPosRef.current = { x: wx, z: wz };
      }

      // ノードのドラッグ中: 閾値を超えたら(最初の1回だけ Undo を積んで)ノードを追従させ、パンはしない。
      // 流路の作り直しは軽いので毎回行い、重い彫り込みプレビューはデバウンスして予約する。
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

      // 左ボタン押下中、移動量が閾値を超えたらパンへ移行する(クリック単発とドラッグパンの判定)。
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
        // Yは反転描画(+Zが画面上)なので、offsetYはドラッグ量を加算する向きになる。
        v.offsetY = dragRef.current.offY + dy / v.zoom;
        draw();
        return;
      }

      if (inside) {
        if (toolStateRef.current.riverPointerMode === "node") {
          const [wx, wz] = screenToWorld(mx, my);
          updateRiverNodeHover(wx, wz);
        }
        // ブラシ・作図中の追従線・ノードの強調はすべてカーソル層にあるので、そこだけ描き直す
        drawCursorLayer();
      }
    }

    function onUp(e: MouseEvent) {
      // ノードのドラッグ終了: 動かしていれば最終状態で彫り込みプレビューを作り直す。
      // 動かさずに離した(ノードをクリックしただけ)場合は何もしない。
      const nodeDrag = riverNodeDragRef.current;
      if (e.button === 0 && nodeDrag) {
        riverNodeDragRef.current = null;
        leftDownRef.current = null;
        dragRef.current.active = false;
        if (canvasEl.style) canvasEl.style.cursor = "grab";
        if (nodeDrag.moved) rebuildRiverPreview();
        return;
      }
      // 左ボタンがドラッグへ移行せずに離された場合のみ、その位置でクリック単発の選択操作を行う。
      if (e.button === 0 && leftDownRef.current && !dragRef.current.active) {
        const ts = toolStateRef.current;
        const rect = canvasEl.getBoundingClientRect();
        const [wx, wz] = screenToWorld(e.clientX - rect.left, e.clientY - rect.top);
        if (activeTabRef.current === "prop") {
          propEditor.current.click(wx, wz);
        } else if (ts.riverPointerMode === "draw") {
          // 流路の作図: 左クリックで頂点を追加する(上流→下流の順)
          addRiverDraftPoint(wx, wz);
        } else if (ts.riverPointerMode === "node") {
          // ノード編集: 流路上のクリックでノード追加、他の川のクリックで選択の切り替え
          handleRiverNodeClick(wx, wz);
        } else if (ts.waterMode) {
          // 水面モード: 形状を閉じる前のみ、左クリックで水面ポリゴンの頂点を追加する。
          if (!ts.waterPolygonClosed) addWaterPoint(wx, wz);
        } else if (ts.resourceMode) {
          // 資源モード: 形状を閉じる前のみ、左クリックで資源ポリゴンの頂点を追加する。
          if (!ts.resourcePolygonClosed) addResourcePoint(wx, wz);
        } else if (ts.cameraMode) {
          // カメラモード: 左クリック位置にカメラを置く(向き・高さは維持)。
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
      // カーソル位置の世界座標をズーム前後で保持
      const [wxBefore, wyBefore] = screenToWorld(mx, my);
      const factor = e.deltaY < 0 ? 1.15 : 1 / 1.15;
      v.zoom = Math.max(0.0001, Math.min(100000, v.zoom * factor));
      const [wxAfter, wyAfter] = screenToWorld(mx, my);
      v.offsetX += wxBefore - wxAfter;
      v.offsetY += wyBefore - wyAfter;
      // 等高線の間隔も描画時にズーム倍率から決まるので、描き直すだけで細かくなる
      draw();
    }

    function onContextMenu(e: MouseEvent) {
      // 右クリックをポリゴン確定に割り当てているため、ブラウザの右クリックメニューは抑止する
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
   * Undo / Redo のキーボードショートカット。Ctrl+Z で戻る、Ctrl+Y (Ctrl+Shift+Z) で進む。
   * Escape は流路の作図中の頂点を破棄する。
   */
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (metadataDialogRef.current?.open) return;
      if (e.key === "Escape" && toolStateRef.current.riverPointerMode === "draw" && riverDraftPointsRef.current.length > 0) {
        clearRiverDraft();
        return;
      }
      if (e.key === "Escape" && activeTabRef.current === "prop") { propEditor.current.reset(); return; }
      if (!e.ctrlKey || e.metaKey) return;
      // テキスト入力中はインターセプトしない
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

  return (
    <div className="terrain-editor-root">
      <dialog ref={metadataDialogRef} className="terrain-editor-metadata-dialog" aria-label={t("terrainEditor.metadataTitle")}>
        <button type="button" className="terrain-editor-btn terrain-editor-dialog-close" onClick={() => metadataDialogRef.current?.close()}>{t("common.close")}</button>
            {/* 地図メタデータカード */}
            <div className="terrain-editor-card">
              <div className="terrain-editor-card-title">
                {t("terrainEditor.metadataTitle", { defaultValue: "地図メタデータ" })}
              </div>
              <div className="terrain-editor-param-group">
                <div className="terrain-editor-param-row">
                  <span className="terrain-editor-param-label">{t("terrainEditor.metaMapId", { defaultValue: "マップID" })}</span>
                  <input
                    type="text"
                    className="terrain-editor-text-input"
                    value={mapIdInput}
                    onChange={(e) => setMapIdInput(e.target.value)}
                    disabled={status !== "ready"}
                  />
                </div>
                <div className="terrain-editor-param-row">
                  <span className="terrain-editor-param-label">{t("terrainEditor.metaFarSpacing", { defaultValue: "遠景グリッド間隔" })}</span>
                  <NumberStepper value={farSpacing} onChange={setFarSpacing} min={1} max={2000} step={10} disabled={status !== "ready"} />
                </div>
                <div className="terrain-editor-param-row">
                  <span className="terrain-editor-param-label">{t("terrainEditor.metaCellSize", { defaultValue: "セルサイズ" })}</span>
                  <NumberStepper value={cellSize} onChange={setCellSize} min={1} max={5000} step={10} disabled={status !== "ready"} />
                </div>
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
                    value={climate}
                    onChange={(e) => setClimate(normalizeClimate(Number(e.target.value)))}
                    disabled={status !== "ready"}
                  >
                    {CLIMATE_TYPES.map((type) => (
                      <option key={type} value={type}>{t(`climate.${type}`)}</option>
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

              {/* マップサイズ変更(セル単位での追加/削除) */}
              <div className="terrain-editor-section-title" style={{ marginTop: 10 }}>
                {t("terrainEditor.resizeMapTitle", { defaultValue: "マップサイズ変更" })}
              </div>
              <div className="terrain-editor-hint">
                {settingsRef.current
                  ? t("terrainEditor.resizeHint", {
                      defaultValue: `${2 * cellSize}m (${(2 * cellSize) / settingsRef.current.spacing}グリッド) 単位で追加/削除します`,
                      stepMeters: 2 * cellSize,
                      stepGrids: (2 * cellSize) / settingsRef.current.spacing,
                    })
                  : ""}
              </div>
              <div className="terrain-editor-resize-row">
                <select
                  className="terrain-editor-select"
                  value={resizeEdge}
                  onChange={(e) => setResizeEdge(e.target.value as ResizeEdge)}
                  disabled={status !== "ready"}
                >
                  <option value="top">{t("terrainEditor.resizeTop", { defaultValue: "上端" })}</option>
                  <option value="bottom">{t("terrainEditor.resizeBottom", { defaultValue: "下端" })}</option>
                  <option value="left">{t("terrainEditor.resizeLeft", { defaultValue: "左端" })}</option>
                  <option value="right">{t("terrainEditor.resizeRight", { defaultValue: "右端" })}</option>
                </select>
                <select
                  className="terrain-editor-select"
                  value={resizeAction}
                  onChange={(e) => setResizeAction(e.target.value as ResizeAction)}
                  disabled={status !== "ready"}
                >
                  <option value="add">{t("terrainEditor.resizeAdd", { defaultValue: "追加" })}</option>
                  <option value="remove">{t("terrainEditor.resizeRemove", { defaultValue: "削除" })}</option>
                </select>
                <button
                  type="button"
                  className="terrain-editor-btn"
                  disabled={status !== "ready"}
                  onClick={handleResizeMap}
                >
                  {t("terrainEditor.resizeExecute", { defaultValue: "実行" })}
                </button>
              </div>
              {resizeStatus === "ok" && (
                <div className="terrain-editor-hint terrain-editor-hint--success">
                  {t("terrainEditor.resizeOk", { defaultValue: "マップサイズを変更しました" })}
                </div>
              )}
              {resizeStatus === "error" && (
                <div className="terrain-editor-hint terrain-editor-hint--error">
                  {resizeErrorMsg}
                </div>
              )}

            </div>
      </dialog>
      {/* 左ツールパネル。カード形式の編集UIを配置する。 */}
      <aside className="terrain-editor-tools">
        <div className="terrain-editor-tools-title-row">
          <div className="terrain-editor-tools-title">
            {t("terrainEditor.toolsTitle", { defaultValue: "編集ツール" })}
          </div>
          <div className="terrain-editor-header-actions">
          <button type="button" className="terrain-editor-btn" onClick={() => void handleOpen3dPreview()}>
            {t("terrainEditor.open3dPreview", { defaultValue: "3Dプレビューを開く" })}
          </button>
          <button type="button" className="terrain-editor-btn" onClick={() => metadataDialogRef.current?.showModal()}>{t("terrainEditor.editMetadata")}</button>
          </div>
        </div>

        {/* 編集対象はプルダウンで選ぶ。 */}
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
            <option value="terrain">{t("terrainEditor.tabTerrain", { defaultValue: "地形編集" })}</option>
            <option value="water">{t("terrainEditor.tabWater", { defaultValue: "水面編集" })}</option>
            <option value="river">{t("terrainEditor.tabRiver", { defaultValue: "川編集" })}</option>
            <option value="resource">{t("terrainEditor.tabResource", { defaultValue: "資源編集" })}</option>
            <option value="prop">{t("terrainEditor.tabProp")}</option>
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

        {/* 選択中の編集カードを1列で表示する。設定値と範囲選択はタブをまたいで保持する。 */}
        <div className="terrain-editor-cards-area">

          {/* ============================================================ */}
          {/* 左カラム: 地形の自動編集 */}
          {/* ============================================================ */}
          <div className="terrain-editor-cards-left" id="terrain-edit-panel-auto" role="tabpanel" aria-labelledby="terrain-edit-tab-auto" hidden={activeTab !== "terrain" || terrainEditTab !== "auto"}>

            {/* 自動編集カード(侵食シミュレーションをマップ全体へ一括適用する) */}
            {activeTab === "terrain" && (
              <div className="terrain-editor-card">
              <div className="terrain-editor-card-title">{t("terrainEditor.autoTitle", { defaultValue: "自動編集" })}</div>

              {/* 水流侵食: 雨粒パーティクルが斜面を削りながら流れ、谷筋と河口の堆積を作る */}
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

              {/* 熱侵食: 安息角を超える崖・急斜面が崩れてなだらかになる */}
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

              {/* 海底生成: 海面一律0mのデータ(国土地理院DEM等)の海マスを、海岸からの距離に応じた水深へ彫り下げる */}
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
                        "標高が判定値以下のマスを海とみなし、海岸から沖へ向かって指定勾配で深くします(最大水深へ滑らかに漸近)。海面が一律0mのデータ(国土地理院DEM等)から自然な海底を作る用途です。湖など海にしたくない0m地帯がある場合は、先に海側だけを範囲選択してから実行してください",
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
          {/* 右カラム: 各タブの編集カード */}
          {/* ============================================================ */}
          <div className="terrain-editor-cards-right" id="terrain-edit-panel-manual" role={activeTab === "terrain" ? "tabpanel" : undefined} aria-labelledby={activeTab === "terrain" ? "terrain-edit-tab-manual" : undefined} hidden={activeTab === "terrain" && terrainEditTab !== "manual"}>
            {activeTab === "prop" && <TerrainPropEditor editor={propEditor.current} />}

            {/* 水面カード(海面以外の湖などを任意ポリゴンで追加する) */}
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

            {/* 資源編集カード群(作図 → パラメータ設定 → 作成済み一覧) */}
            {activeTab === "resource" && (
              <>
                <div className="terrain-editor-card">
                  <div className="terrain-editor-card-title">{t("terrainEditor.resourceTitle", { defaultValue: "天然資源エリア" })}</div>
                  <div className="terrain-editor-section">
                    <div className="terrain-editor-hint">
                      {t("terrainEditor.resourceHint", {
                        defaultValue:
                          "資源編集タブがアクティブのときは資源追加モードになり、左クリックで資源ポリゴンの頂点を追加、右クリック(または「形状を確定」)で形状を閉じます(3点以上)。左ドラッグで画面移動。形状を閉じたあと、種別・埋蔵量上限・回復率を設定して「資源を確定」で1件の資源エリアとして登録します。登録した資源はこのマップで新規都市を始めたときに配置されます",
                      })}
                    </div>
                    {!masterData && (
                      <div className="terrain-editor-hint terrain-editor-hint--error">
                        {t("terrainEditor.resourceMasterUnavailable", {
                          defaultValue:
                            "マスターデータを取得できないため資源種別を選択できません(バックエンド未起動?)。既存の資源の確認・削除は可能です",
                        })}
                      </div>
                    )}
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.resourceType", { defaultValue: "資源種別" })}</span>
                      <select
                        className="terrain-editor-select"
                        value={resourceTypeInput ?? ""}
                        onChange={(e) => setResourceTypeInput(e.target.value ? parseInt(e.target.value) : null)}
                        disabled={status !== "ready" || !masterData}
                      >
                        <option value="">{t("terrainEditor.resourceTypeUnselected", { defaultValue: "(選択してください)" })}</option>
                        {masterData &&
                          Object.entries(masterData.resource_types).map(([id, entry]) => (
                            <option key={id} value={id}>
                              {entry.display_name}
                            </option>
                          ))}
                      </select>
                    </div>
                    {resourceInputs.presets.length > 1 && (
                      <div className="terrain-editor-param-row">
                        <span className="terrain-editor-param-label">{t("resourcePreset.label")}</span>
                        <select
                          className="terrain-editor-select"
                          value={resourceInputs.preset?.key ?? ""}
                          onChange={(e) => resourceInputs.selectPreset(e.target.value)}
                          disabled={status !== "ready"}
                        >
                          {resourceInputs.presets.map((preset) => (
                            <option key={preset.key} value={preset.key}>
                              {t(`resourcePreset.presets.${preset.key}.name`)}
                            </option>
                          ))}
                        </select>
                      </div>
                    )}
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.resourceMaxAmount", { defaultValue: "埋蔵量上限" })}</span>
                      <NumberStepper
                        className="number-stepper--wide"
                        value={resourceInputs.maxAmount}
                        onChange={resourceInputs.setMaxAmount}
                        min={1}
                        max={RESOURCE_MAX_AMOUNT_LIMIT}
                        step={100}
                        integerOnly={true}
                        disabled={status !== "ready"}
                      />
                    </div>
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.resourceRecoveryRate", { defaultValue: "回復率(1日あたり)" })}</span>
                      <NumberStepper
                        value={resourceInputs.recoveryRate}
                        onChange={resourceInputs.setRecoveryRate}
                        min={0}
                        max={1}
                        step={0.0001}
                        integerOnly={false}
                        disabled={status !== "ready"}
                      />
                    </div>
                    <ResourcePresetHint
                      preset={resourceInputs.preset}
                      areaM2={resourceDraftAreaM2}
                      maxAmount={resourceInputs.maxAmount}
                      recoveryRate={resourceInputs.recoveryRate}
                      awaitingShape={resourceInputs.awaitingShape}
                      className="terrain-editor-hint"
                    />
                    <div className="terrain-editor-polygon-count">
                      {resourcePolygonClosed
                        ? t("terrainEditor.resourceShapeClosed", { defaultValue: "形状を確定しました。種別・埋蔵量を設定して「資源を確定」を押してください" })
                        : t("terrainEditor.resourceVertexCount", { defaultValue: `作図中の頂点数: ${resourcePointCount}`, count: resourcePointCount })}
                    </div>
                    <div className="terrain-editor-action-buttons">
                      {!resourcePolygonClosed ? (
                        <>
                          <button
                            type="button"
                            className="terrain-editor-btn terrain-editor-btn-confirm"
                            disabled={!resourceMode || resourcePointCount < 3}
                            onClick={() => setResourcePolygonClosed(true)}
                          >
                            {t("terrainEditor.resourceCloseShape", { defaultValue: "形状を確定" })}
                          </button>
                          <button
                            type="button"
                            className="terrain-editor-btn"
                            disabled={!resourceMode || resourcePointCount === 0}
                            onClick={clearResourcePoints}
                          >
                            {t("terrainEditor.resourceClearPoints", { defaultValue: "作図クリア" })}
                          </button>
                        </>
                      ) : (
                        <>
                          <button
                            type="button"
                            className="terrain-editor-btn terrain-editor-btn-confirm"
                            disabled={!resourceMode || resourceTypeInput === null}
                            onClick={confirmResourceArea}
                          >
                            {t("terrainEditor.resourceConfirm", { defaultValue: "資源を確定" })}
                          </button>
                          <button
                            type="button"
                            className="terrain-editor-btn"
                            disabled={!resourceMode}
                            onClick={clearResourcePoints}
                          >
                            {t("terrainEditor.resourceClearPoints", { defaultValue: "作図クリア" })}
                          </button>
                        </>
                      )}
                    </div>
                    {resourcePolygonClosed && resourceTypeInput === null && (
                      <div className="terrain-editor-hint">
                        {t("terrainEditor.resourceTypeRequired", { defaultValue: "資源種別を選択すると確定できます" })}
                      </div>
                    )}
                  </div>
                </div>

                {/* 作成済みの資源エリア一覧カード */}
                <div className="terrain-editor-card">
                  <div className="terrain-editor-card-title">{t("terrainEditor.resourceListTitle", { defaultValue: "作成済みの資源" })}</div>
                  <div className="terrain-editor-section">
                    {resourceList.length === 0 ? (
                      <div className="terrain-editor-hint">
                        {t("terrainEditor.resourceListEmpty", { defaultValue: "作成済みの資源はありません" })}
                      </div>
                    ) : (
                      <>
                        {resourceList.map((area, idx) => (
                          <div key={area.id} className="terrain-editor-param-group">
                            <div className="terrain-editor-param-row">
                              <span className="terrain-editor-param-label">
                                {t("terrainEditor.resourceListItem", {
                                  defaultValue: `資源${idx + 1}: ${resourceTypeLabel(area.resourceType)}`,
                                  index: idx + 1,
                                  name: resourceTypeLabel(area.resourceType),
                                })}
                              </span>
                              <span className="terrain-editor-selection-status">
                                {t("terrainEditor.resourceListItemDetail", {
                                  defaultValue: `上限${area.maxAmount} / 回復${area.recoveryRate}`,
                                  maxAmount: formatCompactAmount(area.maxAmount, resolveIntlLocale(i18n.language)),
                                  recoveryRate: area.recoveryRate,
                                })}
                              </span>
                            </div>
                            <div className="terrain-editor-action-buttons">
                              <button
                                type="button"
                                className="terrain-editor-btn"
                                onClick={() => removeResourceArea(area.id)}
                              >
                                {t("terrainEditor.resourceDeleteOne", { defaultValue: "削除" })}
                              </button>
                            </div>
                          </div>
                        ))}
                        <div className="terrain-editor-action-buttons">
                          <button type="button" className="terrain-editor-btn" onClick={clearAllResourceAreas}>
                            {t("terrainEditor.resourceDeleteAll", { defaultValue: "すべての資源を削除" })}
                          </button>
                        </div>
                      </>
                    )}
                  </div>
                </div>
              </>
            )}

            {/* カメラ初期状態カード */}
            {activeTab === "camera" && (
              <div className="terrain-editor-card">
                <div className="terrain-editor-card-title">{t("terrainEditor.cameraTitle", { defaultValue: "カメラ初期状態" })}</div>
                <div className="terrain-editor-section">
                  <div className="terrain-editor-hint">
                    {t("terrainEditor.cameraHint", {
                      defaultValue:
                        "このマップで新規都市を始めたときのカメラの位置と向きです。左クリックでカメラ位置を置き、右クリックで注視点を指定して向きを決めます。左ドラッグで画面移動。未設定のままならゲームはマップ中心(原点)の既定視点で始まります",
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

            {/* 川編集カード群(降雨範囲 → 自動生成 → 作成済み一覧) */}
            {activeTab === "river" && (
              <>
                {/* 操作モードカード: 川編集タブでのポインタ操作(降雨範囲の選択/川を描く/ノード編集)を切り替える */}
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

                {/* 降雨範囲カード: 地形編集と同じ選択マスクを降雨範囲として流用する */}
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

                {/* 川の自動生成カード: プレビュー(標高不変)→確定(彫り込み)の2段階フロー */}
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
                      {/* 上限は設けない。小さな島マップでは現実的な雨量では最大流量が閾値に
                          届かないことがあり(riverZeroHint参照)、非現実的に大きな値で
                          試したいニーズがあるため */}
                      <NumberStepper
                        value={riverParams.rainfallMmPerYear}
                        onChange={(v) => setRiverParams((p) => ({ ...p, rainfallMmPerYear: v }))}
                        min={100}
                        step={100}
                      />
                    </div>
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.riverMinDischarge", { defaultValue: "最小流量(m³/s)" })}</span>
                      {/* 小さな島マップでは最大流量が1m³/s未満になるため、下限はかなり小さくとる */}
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
                    {/* 幅・深さは 係数×流量^べき乗 で決まるため、流量が小さいマップでは
                        係数を大きく振れないと変化が視認できない。上限は広めにとる */}
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
                    {/* 流路が0本のときは原因に応じたヒントを出す。
                        - 最大流量が閾値未満: 閾値を下げるか雨量を増やす必要がある
                        - 閾値は超えているが全て短すぎて破棄: 閾値をさらに下げると流路の
                          起点(channel head)が上流へ移動して川が長くなり、破棄されなくなる。
                        小さな島マップではどちらも普通に起きる。 */}
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

                {/* 作成済みの川カード(未彫り込み+彫り込み済みの一覧) */}
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
                        {/* 彫り込みプレビューと水面表示の切り替え(表示だけで実データは変えない) */}
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
                            {/* 見出し行のクリックで選択(ステッパー・ボタンの操作で選択が切り替わらないよう見出し行だけに付ける) */}
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
                            {/* 未彫り込みの川は、彫る前に深さ係数を川ごとに調整できる */}
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
                            {/* 手動で描いた川は上流端/下流端の川幅を川ごとに変えられる(流路を作り直す) */}
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
                            {/* 未彫り込みの川の河口の種類とノード数(ノード編集の対象かどうかの目安) */}
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

            {/* 手動編集カード */}
            {activeTab === "terrain" && (
              <div className="terrain-editor-card">
              <div className="terrain-editor-card-title">{t("terrainEditor.editTitle", { defaultValue: "手動編集" })}</div>

              {/* 操作方法の案内。マウスボタンごとに操作を専有させている。 */}
              <div className="terrain-editor-section">
                <div className="terrain-editor-hint">
                  {t("terrainEditor.editHint", {
                    defaultValue: "左クリックで範囲に単発適用、左ドラッグで画面移動、右クリックでポリゴンを確定",
                  })}
                </div>
              </div>

              {/* 範囲指定方法 */}
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

                {/* 境界のなじませ方・ぼかし幅はブラシ/ポリゴンどちらの選択にも共通で使う。
                    内側は常に重み1で確定し、ここで指定した幅の分だけ境界の外側ににじみ出す。 */}
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

                {/* 選択範囲の状態表示とリセット */}
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

              {/* 編集方法 */}
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
              {/* エクスポート */}
              <div className="terrain-editor-action-buttons" style={{ marginTop: 10 }}>
                <button
                  type="button"
                  className="terrain-editor-btn"
                  disabled={status !== "ready" || exportStatus === "exporting"}
                  onClick={() => void handleExportTerrainAsNpy()}
                >
                  {exportStatus === "exporting"
                    ? t("terrainEditor.exporting", { defaultValue: "エクスポート中..." })
                    : t("terrainEditor.export", { defaultValue: ".npyへエクスポート" })}
                </button>
              </div>
              {exportStatus === "exported" && (
                <div className="terrain-editor-hint terrain-editor-hint--success">
                  {t("terrainEditor.exportSuccess", { defaultValue: ".npyへエクスポートしました" })}
                </div>
              )}
              {exportStatus === "error" && (
                <div className="terrain-editor-hint terrain-editor-hint--error">
                  {exportErrorMsg || t("terrainEditor.exportFailed", { defaultValue: "エクスポートに失敗しました" })}
                </div>
              )}
        </div>
      </aside>

      {/* キャンバス領域。ディスプレイいっぱいに広げるために flex:1 で埋める。 */}
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
