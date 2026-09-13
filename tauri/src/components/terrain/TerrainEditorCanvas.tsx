/**
 * 地形エディタのキャンバス本体。
 *
 * EditorWindow から terrain_editor ウィンドウとして開かれ、指定された地形マップの
 * 作業コピー(near.bin) を読み込んで最大解像度(nearマップ解像度)で描画する。
 * 通常のゲーム表示が far近景をLOD切り替えするのに対し、本エディタは常に最大解像度で
 * 描画し、さらに等高線と100m間隔のグリッドを重ね描きする。グリッドのうちセル境界
 * (cell_size間隔)にあたる線は、通常のグリッドより濃く強調して描く。
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
 *     再描画(テクスチャ・等高線・3Dプレビュー送信)のみを担当する。
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
import {
  type TerrainEditorData,
  type WaterSurfacePayload,
  type WaterPolygonGeoJson,
  type RiverPayload,
  type ResourceAreaPayload,
  type TerrainCameraPayload,
  type GridRect,
  type Terrain3DRequestStatePayload,
  type Terrain3DFullStatePayload,
  type Terrain3DSelectionDeltaPayload,
  type Terrain3DSelectionResetPayload,
  type Terrain3DElevationDeltaPayload,
  decodeFloat16Array,
  base64ToUint8,
  encodeFloat16Array,
  uint8ToBase64,
  computePixelRGB,
  extractElevRect,
  quantizeMaskRect,
  EVT_TERRAIN_3D_REQUEST_STATE,
  EVT_TERRAIN_3D_FULL_STATE,
  EVT_TERRAIN_3D_SELECTION_DELTA,
  EVT_TERRAIN_3D_SELECTION_RESET,
  EVT_TERRAIN_3D_ELEVATION_DELTA,
} from "./terrainData";
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
  extractRiverNetwork,
  evolveRivers,
  carveRivers,
  applyRiverDepth,
  buildRiverPolygon,
} from "./terrainRiver";
import { polygonArea, polygonParts, translatePolygon } from "./riverGeometry";
import { captureTerrainThumbnail } from "./terrainThumbnail";
import "./TerrainEditorCanvas.css";

type TerrainEditorCanvasProps = {
  /** 編集対象の地形マップID。window-registry の entity_id 経由で渡される。 */
  mapId: string;
};

/** 1本の等高線(ある標高レベルに対する worldly Path2D) */
type Contour = {
  level: number;
  path: Path2D;
  /** 主要等高線(毎5本)かどうか。描画 lineWidth を切り替えるために使う */
  major: boolean;
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
 * 保存・エクスポート・3Dプレビュー送信時に GeoJSON ライクな WaterSurfacePayload へ変換する。
 */
type EditorWaterSurface = { id: string; waterLevel: number; points: WorldPoint[] };

/**
 * 地形エディタ内での川1本分の内部表現。
 * GeneratedRiver(terrainRiver.ts)に以下を加えたもの。
 *   - params: 生成時のパラメータ(再生成の参考用)
 *   - carved: 川床を地形へ彫り込み済みか。「流路を計算」直後は false(プレビュー状態)で、
 *     一覧から1本ずつ「彫る」ことで true になる。保存・エクスポート・3Dプレビューの
 *     対象になるのは彫り込み済みの川のみ。
 *   - depthCoef: この川を彫るときの深さ係数。生成時の既定値から川ごとに調整できる。
 *   - polygon: 川の範囲(彫り込み後の地形で水面より低い領域。穴=中州)。彫り込み時に抽出し、
 *     保存契約(RiverPayload.polygon)・2D/3Dプレビューの水面表示に使う。未彫り込みは null。
 *     保存データから復元した川は points の width/bedY を持たない(0 と waterY で埋める)ため、
 *     範囲の再計算は既存 polygon を膨らませた帯で行う(buildRiverPolygon の polygon 帯)。
 */
type EditorRiver = GeneratedRiver & {
  params: RiverGenParams;
  carved: boolean;
  depthCoef: number;
  polygon: WaterPolygonGeoJson | null;
};

/**
 * 地形エディタ内での天然資源エリア1件分の内部表現。
 * points は外周ポリゴンの頂点(ワールド座標XZ)。保存・エクスポート時に GeoJSON ライクな
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

/** 100m グリッドの Path2D を世界座標系で構築する */
function buildGridPath(mapWidth: number, mapHeight: number, interval: number): Path2D {
  const path = new Path2D();
  const halfW = mapWidth / 2;
  const halfH = mapHeight / 2;
  // 縦線(世界X方向)
  for (let x = -halfW; x <= halfW; x += interval) {
    path.moveTo(x, -halfH);
    path.lineTo(x, halfH);
  }
  // 横線(世界Z方向)。半端な端の線は [0, halfW) のループで漏れるのを防ぐため終端も個別に引く
  for (let z = -halfH; z <= halfH; z += interval) {
    path.moveTo(-halfW, z);
    path.lineTo(halfW, z);
  }
  // 浮動小数点の累積誤差で端が1px足りないのを補うため、最端線を明示的に引く
  path.moveTo(halfW, -halfH);
  path.lineTo(halfW, halfH);
  path.moveTo(-halfW, halfH);
  path.lineTo(halfW, halfH);
  return path;
}

/**
 * Marching Squares で全等高線を構築する。
 *
 * 各セル(2x2グリッド点)で4隅の標高を調べ、指定レベルを跨ぐセルについて
 * エッジ上の交点を線形補間で求めて線分を追加する。saddle ケース(5,10)は
 * 2本の線分になる。全ての線分は世界座標で Path2D に蓄積し、描画時に
 * ビュー変換を適用してストロークする。
 *
 * interval は呼び出し側で決める(初回ロード時は pickContourInterval、その後は
 * ズーム倍率に応じて pickContourIntervalForZoom で細分化した値を渡す)。
 *
 * セル数が大きくても全レベル一度に構築可能なよう、各セルの4隅min/maxで
 * レベルを跨がないセルをスキップする最適化を入れている。
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
): Contour[] {
  const halfW = mapWidth / 2;
  const halfH = mapHeight / 2;
  // グリッド点(col,row) → 世界X,Z
  // 世界X = col*spacing - halfW, 世界Z = row*spacing - halfH

  // 等高線レベル群(0 < L <= maxLand の interval倍数)
  const levels: number[] = [];
  const start = Math.ceil(minLand / interval) * interval;
  for (let L = start; L <= maxLand; L += interval) {
    if (L > 0) levels.push(L);
  }
  if (levels.length === 0 && maxLand > 0) levels.push(Math.max(start, interval));

  const contours: Contour[] = [];
  for (let li = 0; li < levels.length; li++) {
    const L = levels[li];
    const path = new Path2D();
    const major = li % 5 === 0;

    for (let r = 0; r < rows - 1; r++) {
      const baseZ = r * spacing - halfH;
      const rowBase = r * cols;
      const rowNext = rowBase + cols;
      for (let c = 0; c < cols - 1; c++) {
        const a = elev[rowBase + c]; // 左上 TL
        const b = elev[rowBase + c + 1]; // 右上 TR
        const cc = elev[rowNext + c + 1]; // 右下 BR
        const d = elev[rowNext + c]; // 左下 BL
        const mn = Math.min(a, b, cc, d);
        const mx = Math.max(a, b, cc, d);
        if (L < mn || L > mx) continue;

        // 4隅の inside(>=L) ビット
        const caseIdx =
          (a >= L ? 1 : 0) | (b >= L ? 2 : 0) | (cc >= L ? 4 : 0) | (d >= L ? 8 : 0);
        if (caseIdx === 0 || caseIdx === 15) continue;

        const baseX = c * spacing - halfW;
        // 各エッジの交点を線形補間で求める
        // top = A(左上)→B(右上), right = B→C, bottom = D(左下)→C, left = A→D
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

        // case毎に線分を追加。saddle(5,10)は2本。
        // 便宜上 moveTo→lineTo で各線分を個別に引く(ストロークなので向きは不問)。
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
    contours.push({ level: L, path, major });
  }
  return contours;
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

/** グリッド範囲(テクスチャの部分更新や選択範囲の外接矩形に使う) */
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
 * (選択マスクへの反映やテクスチャ差分更新の走査範囲をにじみ分だけ広げるために使う)。
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

/** 標高が変化したグリッド範囲だけ地形テクスチャ(ImageData)を塗り直し、Canvasへ反映する */
function refreshTerrainTexture(
  rect: DirtyRect,
  elev: Float32Array,
  cols: number,
  minLand: number,
  maxLand: number,
  imgData: ImageData,
  ctx: CanvasRenderingContext2D
): void {
  const data = imgData.data;
  for (let r = rect.rMin; r <= rect.rMax; r++) {
    const rowBase = r * cols;
    for (let c = rect.cMin; c <= rect.cMax; c++) {
      const i = rowBase + c;
      const [red, g, b] = computePixelRGB(elev[i], minLand, maxLand);
      data[i * 4 + 0] = red;
      data[i * 4 + 1] = g;
      data[i * 4 + 2] = b;
      data[i * 4 + 3] = 255;
    }
  }
  const w = rect.cMax - rect.cMin + 1;
  const h = rect.rMax - rect.rMin + 1;
  ctx.putImageData(imgData, 0, 0, rect.cMin, rect.rMin, w, h);
}

/** selectionMask の値(0..1)を反映した半透明ハイライトを選択オーバーレイテクスチャへ書き込む */
function refreshSelectionTexture(
  rect: DirtyRect,
  mask: Float32Array,
  cols: number,
  imgData: ImageData,
  ctx: CanvasRenderingContext2D
): void {
  const data = imgData.data;
  for (let r = rect.rMin; r <= rect.rMax; r++) {
    const rowBase = r * cols;
    for (let c = rect.cMin; c <= rect.cMax; c++) {
      const i = rowBase + c;
      const w = Math.max(0, Math.min(1, mask[i]));
      data[i * 4 + 0] = 80;
      data[i * 4 + 1] = 190;
      data[i * 4 + 2] = 255;
      data[i * 4 + 3] = Math.round(w * 160);
    }
  }
  const w = rect.cMax - rect.cMin + 1;
  const h = rect.rMax - rect.rMin + 1;
  ctx.putImageData(imgData, 0, 0, rect.cMin, rect.rMin, w, h);
}

/** 選択オーバーレイテクスチャ全体を透明にクリアする(「選択範囲をリセット」時に呼ぶ) */
function clearSelectionTexture(imgData: ImageData, ctx: CanvasRenderingContext2D): void {
  imgData.data.fill(0);
  ctx.putImageData(imgData, 0, 0);
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
    ctx.fillText(labelFor(area.resourceType), cx, cz);
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
 * カメラ編集タブで注視点を指定する操作の目安として、mouse が渡されたら位置からマウスまで破線を引く。
 */
function strokeCameraMarker(
  ctx: CanvasRenderingContext2D,
  camera: TerrainCameraPayload | null,
  mouse: WorldPoint | null,
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
  ctx.fillText("Camera", camera.x, camera.z - lineWidth * 6);

  if (mouse) {
    ctx.setLineDash([lineWidth * 4, lineWidth * 4]);
    ctx.strokeStyle = "rgba(255,230,90,0.6)";
    ctx.beginPath();
    ctx.moveTo(camera.x, camera.z);
    ctx.lineTo(mouse.x, mouse.z);
    ctx.stroke();
    ctx.setLineDash([]);
  }
  ctx.restore();
}

/**
 * 川を2Dキャンバスへ描画する。draw() 内でビュー変換適用後に呼ぶ。
 * 彫り込み済みの川は「範囲ポリゴンの水面塗り(穴=中州は抜く) + 細い水面プロファイル線」で、
 * ゲーム本体でどの範囲に水面が描かれるかをそのまま示す。範囲が未抽出の彫り込み済みの川
 * (異常系)は中心線だけを描く。未彫り込み(計算直後)の川は明るい水色の破線で、川幅の帯を
 * 平均幅で近似して描く(セグメントごとに lineWidth を変えるとストローク回数が点数分に増えるため)。
 */
function strokeRivers(
  ctx: CanvasRenderingContext2D,
  rivers: EditorRiver[],
  lineWidth: number,
): void {
  const buildPath = (river: EditorRiver): { path: Path2D; avgWidth: number } | null => {
    if (river.points.length < 2) return null;
    const path = new Path2D();
    path.moveTo(river.points[0].x, river.points[0].z);
    let widthSum = 0;
    for (let i = 1; i < river.points.length; i++) {
      path.lineTo(river.points[i].x, river.points[i].z);
      widthSum += river.points[i].width;
    }
    return { path, avgWidth: widthSum / (river.points.length - 1) };
  };

  ctx.lineJoin = "round";
  ctx.lineCap = "round";

  for (const river of rivers) {
    const built = buildPath(river);
    if (!built) continue;
    if (river.carved) {
      // 彫り込み済み: 範囲ポリゴン(穴込み)を水面色で塗る。even-odd で穴を抜く
      if (river.polygon) {
        const fill = new Path2D();
        for (const part of polygonParts(river.polygon)) {
          for (const ring of part) {
            if (ring.length < 3) continue;
            fill.moveTo(ring[0][0], ring[0][1]);
            for (let i = 1; i < ring.length; i++) fill.lineTo(ring[i][0], ring[i][1]);
            fill.closePath();
          }
        }
        ctx.fillStyle = "rgba(60,140,220,0.45)";
        ctx.fill(fill, "evenodd");
        ctx.strokeStyle = "rgba(120,200,255,0.9)";
        ctx.lineWidth = lineWidth * 0.75;
        ctx.setLineDash([]);
        ctx.stroke(fill);
      }
      // 水面プロファイル線(範囲の中心を通る流路)
      ctx.strokeStyle = river.polygon ? "rgba(40,110,220,0.6)" : "rgba(255,140,40,0.9)";
      ctx.lineWidth = lineWidth * 0.75;
      ctx.setLineDash([]);
      ctx.stroke(built.path);
    } else {
      // 未彫り込み: 明るい水色の破線(まだ地形を変更していないことを示す)
      ctx.strokeStyle = "rgba(120,200,255,0.25)";
      ctx.lineWidth = Math.max(built.avgWidth, lineWidth);
      ctx.setLineDash([]);
      ctx.stroke(built.path);
      ctx.strokeStyle = "rgba(120,220,255,0.95)";
      ctx.lineWidth = lineWidth;
      ctx.setLineDash([lineWidth * 4, lineWidth * 3]);
      ctx.stroke(built.path);
      ctx.setLineDash([]);
    }
  }
}

export function TerrainEditorCanvas({ mapId }: TerrainEditorCanvasProps) {
  const { t } = useTranslation();
  // 資源編集タブの種別プルダウン・種別ラベル表示に使う(resource_master 由来)。
  // 地形エディタ自体はバックエンド非依存で動くため、未取得(null)でも他タブは全機能が使える。
  const masterData = useMasterData();
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const coordOverlayRef = useRef<HTMLDivElement | null>(null);
  const zoomOverlayRef = useRef<HTMLDivElement | null>(null);

  // ロード状態。分岐表示に使うため React state にする。
  const [status, setStatus] = useState<"loading" | "ready" | "error">("loading");
  const [errorMsg, setErrorMsg] = useState<string>("");

  // 地形データの実体は再描画の度に参照するため ref に保持する。
  // 一度ロードすれば不変なので state ではなく ref。
  const terrainCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const gridPathRef = useRef<Path2D | null>(null);
  // セル境界(cell_size間隔)のグリッド。通常のグリッドより濃く重ね描きして区別する。
  // cell_size はメタデータ欄で編集可能なため、変更の度に再構築する(cellSize用useEffect参照)。
  // draw() 内で使う間隔もこの ref に持たせ、cellSize state を直接参照しない
  // (draw() はズーム/パン等のイベントハンドラからも呼ばれ、それらのクロージャは
  // status 変更時にしか張り直されないため、state を直接読むと値が古くなるのを避けるため)。
  const cellGridPathRef = useRef<Path2D | null>(null);
  const cellGridIntervalRef = useRef<number>(500);
  const contoursRef = useRef<Contour[]>([]);
  const settingsRef = useRef<TerrainSettings | null>(null);
  const viewRef = useRef<ViewState>({ offsetX: 0, offsetY: 0, zoom: 1 });

  // 等高線間隔のズーム追従。baseIntervalRef/baseZoomRefは地形全体が収まる基準ズームでの
  // 間隔とその時のズーム倍率(resetView直後に確定)。そこからズームインした分だけ
  // pickContourIntervalForZoomで間隔を細分化する。contoursRefは常にcontourIntervalRefの
  // 間隔で構築済みの状態を保つ。
  const baseIntervalRef = useRef<number>(100);
  const baseZoomRef = useRef<number>(1);
  const contourIntervalRef = useRef<number>(100);
  // ズーム操作中に毎フレーム等高線を再構築すると重いため、ズームが落ち着いてから
  // 一度だけ再構築するデバウンス用タイマー。
  const contourZoomTimerRef = useRef<number | null>(null);

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
  // バックエンドの状態には影響しない)。
  const elevRef = useRef<Float32Array | null>(null);
  // 地形テクスチャのImageDataと、それを描いているオフスクリーンCanvasの2Dコンテキスト。
  // 適用時は変化したセルの範囲だけ塗り直す(refreshTerrainTexture)ことで、マップ全体を
  // 毎回再構築するコストを避けている。
  const terrainImageDataRef = useRef<ImageData | null>(null);
  const terrainCtxRef = useRef<CanvasRenderingContext2D | null>(null);

  // 選択範囲(selectionMask)本体。グリッド点ごとに0..1の重みを持ち、ブラシ/ポリゴンで
  // クリックする度に最大値合成(union)でマージされる。「適用」ボタンで編集方法を適用する
  // 対象になり、「選択範囲をリセット」で明示的にクリアするまで保持される。
  const selectionMaskRef = useRef<Float32Array | null>(null);
  // 選択範囲を半透明ハイライトとして描画するためのオフスクリーンCanvas/ImageData/コンテキスト。
  // 地形テクスチャと同様、変化したセル範囲だけ塗り直す。
  const selectionCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const selectionImageDataRef = useRef<ImageData | null>(null);
  const selectionCtxRef = useRef<CanvasRenderingContext2D | null>(null);
  // ポリゴンが閉じた状態でのにじみプレビューを描画するための一時的Canvas/ImageData/コンテキスト
  const polygonPreviewCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const polygonPreviewImageDataRef = useRef<ImageData | null>(null);
  const polygonPreviewCtxRef = useRef<CanvasRenderingContext2D | null>(null);
  // 選択範囲の外接矩形(グリッド範囲)。「適用」時の走査範囲になるほか、選択の有無判定にも使う。
  const selectionBoundsRef = useRef<DirtyRect | null>(null);

  // 3Dプレビューウィンドウ(terrain_3d_preview)への選択範囲差分送信をrAF単位に間引くための
  // 保留矩形とスケジュール状態。マウスドラッグの度に毎回emitすると過剰になるため、
  // 1フレームに溜まった変化をまとめて1回だけ送る。
  const previewSyncRef = useRef<{ pendingSelectionRect: DirtyRect | null; rafId: number | null }>({
    pendingSelectionRect: null,
    rafId: null,
  });

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
  const [activeTab, setActiveTab] = useState<"terrain" | "water" | "river" | "resource" | "camera">("terrain");

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
  // 次に作成する資源のパラメータ。種別はマスターデータ(resource_master)から選ぶ。
  const [resourceTypeInput, setResourceTypeInput] = useState<number | null>(null);
  const [resourceMaxAmountInput, setResourceMaxAmountInput] = useState(1000);
  const [resourceRecoveryRateInput, setResourceRecoveryRateInput] = useState(0);
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
    }[]
  >([]);

  // --- 地図メタデータ(保存欄) ---
  // map_width/map_height/near_spacing/grid_rows/grid_cols は標高データのグリッド構造そのものと
  // 結び付いており、リサンプリングなしに変更すると near.bin との整合性が壊れるため編集不可にし、
  // settingsRef の値をそのまま読み取り専用表示する。map_id(保存先ディレクトリ名。別名保存に使う)/
  // far_spacing/cell_size は近景標高データに影響しない独立したメタデータなので編集可能にする。
  const [mapIdInput, setMapIdInput] = useState<string>(mapId);
  const [farSpacing, setFarSpacing] = useState<number>(0);
  const [cellSize, setCellSize] = useState<number>(0);
  const [saveStatus, setSaveStatus] = useState<"idle" | "saving" | "saved" | "error">("idle");
  const [saveErrorMsg, setSaveErrorMsg] = useState<string>("");
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
        camera: cameraRef.current,
      });
      if (redoStackRef.current.length > MAX_UNDO) redoStackRef.current.shift();
    }
    // rebuildTerrainVisuals が末尾で3Dプレビューへ全状態(川・水面を含む)を送るため、
    // 川・水面・資源のリストは先に復元しておく。
    restoreRivers(target.rivers);
    restoreWaterSurfaces(target.waterSurfaces);
    restoreResourceAreas(target.resourceAreas);
    restoreCamera(target.camera);
    rebuildTerrainVisuals(target.elev, target.mapWidth, target.mapHeight, target.spacing, target.rows, target.cols);
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
        camera: cameraRef.current,
      });
      if (undoStackRef.current.length > MAX_UNDO) undoStackRef.current.shift();
    }
    restoreRivers(target.rivers);
    restoreWaterSurfaces(target.waterSurfaces);
    restoreResourceAreas(target.resourceAreas);
    restoreCamera(target.camera);
    rebuildTerrainVisuals(target.elev, target.mapWidth, target.mapHeight, target.spacing, target.rows, target.cols);
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
    };
  }, [shape, operation, brushParams, selectionFalloff, raiseLowerParams, flattenParams, noiseParams, featherWidth, polygonClosed, waterMode, waterLevelInput, waterPolygonClosed, resourceMode, resourcePolygonClosed, cameraMode]);

  // 現在のブラシ中心(ワールド座標)。マウス移動の度に更新されるため ref で保持する。
  const brushWorldPosRef = useRef<WorldPoint | null>(null);
  // ポリゴンツールで確定前の頂点列。
  const polygonPointsRef = useRef<WorldPoint[]>([]);

  // === 水面(海面以外の湖など)の実体 ===
  // 作成済み水面の一覧。内部表現は {id, waterLevel, points(頂点のワールド座標列)}。
  // 保存・エクスポート・3Dプレビュー送信時に GeoJSON ライク(WaterSurfacePayload)へ変換する。
  const waterSurfacesRef = useRef<EditorWaterSurface[]>([]);
  // 水面モードで作図中(未確定)の頂点列。
  const waterPointsRef = useRef<WorldPoint[]>([]);

  // === 川の実体 ===
  // 未彫り込み(計算直後)と彫り込み済みの両方を carved フラグつきで持つ。
  // 保存・エクスポート・3Dプレビュー送信の対象になるのは彫り込み済みの川のみ。
  const riversRef = useRef<EditorRiver[]>([]);

  // === 天然資源エリアの実体 ===
  // 作成済み資源エリアの一覧と、資源編集タブで作図中(未確定)の頂点列。
  // 3Dプレビューへは送らない(資源は標高・水面と違い3D形状を持たないため)。
  const resourceAreasRef = useRef<EditorResourceArea[]>([]);
  const resourcePointsRef = useRef<WorldPoint[]>([]);

  // === カメラ初期状態の実体 ===
  // 保存・エクスポート(meta.json / terrain_maps.yaml の camera)の対象。3Dプレビューへは送らない。
  const cameraRef = useRef<TerrainCameraPayload | null>(null);

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

  /** アンマウント時、等高線再構築の予約タイマーが残っていれば止める */
  useEffect(() => {
    return () => {
      if (contourZoomTimerRef.current != null) window.clearTimeout(contourZoomTimerRef.current);
    };
  }, []);

  /** アンマウント時、3Dプレビューへの選択差分送信予約(rAF)が残っていれば止める */
  useEffect(() => {
    return () => {
      if (previewSyncRef.current.rafId != null) cancelAnimationFrame(previewSyncRef.current.rafId);
    };
  }, []);

  /**
   * 3Dプレビューウィンドウ(terrain_3d_preview)が自身のマウント時に送ってくる
   * request_state イベントを受け、現在の全状態を送り返す。プレビューを一度閉じて
   * 開き直した場合や、エディタより後からプレビューを開いた場合でも、ディスクの
   * 保存済みデータではなく編集中のメモリ上の状態と同期させるためのハンドシェイク。
   */
  useEffect(() => {
    let unlisten: (() => void) | null = null;
    let cancelled = false;
    listen<Terrain3DRequestStatePayload>(EVT_TERRAIN_3D_REQUEST_STATE, (event) => {
      if (event.payload.mapId !== mapId) return;
      emitTerrain3dFullState();
    }).then((fn) => {
      if (cancelled) fn();
      else unlisten = fn;
    });
    return () => {
      cancelled = true;
      if (unlisten) unlisten();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mapId]);

  /**
   * ビュー座標→世界座標。
   * screenX = (worldX - offsetX) * zoom の逆変換。
   */
  function screenToWorld(sx: number, sy: number): [number, number] {
    const v = viewRef.current;
    return [sx / v.zoom + v.offsetX, sy / v.zoom + v.offsetY];
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
      offsetY: -s.mapHeight / 2 - (cssH / zoom - s.mapHeight) / 2,
    };
  }

  /**
   * 1フレーム分の描画。
   *
   * 世界座標→描画ピクセルの変換は ctx の transform で行い、地形・グリッド・等高線は
   * 全て世界座標系のまま Path2D / drawImage で渡す。これにより大量の等高線線分が
   * あってもストローク1回で描画できる。
   */
  function draw() {
    const cv = canvasRef.current;
    const s = settingsRef.current;
    if (!cv || !s) return;
    const ctx = cv.getContext("2d");
    if (!ctx) return;
    const ts = toolStateRef.current;

    const dpr = window.devicePixelRatio || 1;
    const cssW = cv.clientWidth;
    const cssH = cv.clientHeight;
    if (cssW === 0 || cssH === 0) return;

    // バッキングストアを CSS サイズ × dpr に揃える
    const bw = Math.round(cssW * dpr);
    const bh = Math.round(cssH * dpr);
    if (cv.width !== bw || cv.height !== bh) {
      cv.width = bw;
      cv.height = bh;
    }

    // ベースはデバイスピクセル。背景を塗る。
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.fillStyle = "#0d0d14";
    ctx.fillRect(0, 0, cv.width, cv.height);

    // 以降 CSS ピピクセル座標系で描く
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

    const v = viewRef.current;
    // 世界→CSS 変換: screen = zoom*(world - offset)
    // 構築順: base(dpr) → translate(-offset*zoom) → scale(zoom)
    ctx.save();
    ctx.translate(-v.offsetX * v.zoom, -v.offsetY * v.zoom);
    ctx.scale(v.zoom, v.zoom);

    // 地形描画(最大解像度のoffscreenを世界矩形に張る)
    if (terrainCanvasRef.current) {
      ctx.imageSmoothingEnabled = true;
      ctx.drawImage(
        terrainCanvasRef.current,
        -s.mapWidth / 2,
        -s.mapHeight / 2,
        s.mapWidth,
        s.mapHeight
      );
    }

    // 選択範囲のハイライト(半透明オーバーレイ。地形の上・グリッド/等高線の下に重ねる)
    if (selectionCanvasRef.current) {
      ctx.drawImage(
        selectionCanvasRef.current,
        -s.mapWidth / 2,
        -s.mapHeight / 2,
        s.mapWidth,
        s.mapHeight
      );
    }

    // ポリゴン仮確定プレビューのハイライト
    if (polygonPreviewCanvasRef.current && ts.polygonClosed) {
      ctx.drawImage(
        polygonPreviewCanvasRef.current,
        -s.mapWidth / 2,
        -s.mapHeight / 2,
        s.mapWidth,
        s.mapHeight
      );
    }

    // 線幅は世界単位で指定→画面では zoom 倍になる。1px にするには 1/zoom。
    const thinW = 1 / v.zoom;
    const majorW = 1.5 / v.zoom;

    // 100m グリッド。ズームで細かすぎるときは描かない。
    if (gridPathRef.current && 100 * v.zoom >= 6) {
      ctx.strokeStyle = "rgba(255,255,255,0.12)";
      ctx.lineWidth = thinW;
      ctx.stroke(gridPathRef.current);
    }

    // セル境界グリッド(cell_size間隔)。通常グリッドの上から濃く重ね描きして強調する。
    if (cellGridPathRef.current && cellGridIntervalRef.current * v.zoom >= 4) {
      ctx.strokeStyle = "rgba(255,255,255,0.45)";
      ctx.lineWidth = majorW;
      ctx.stroke(cellGridPathRef.current);
    }

    // 等高線
    if (contoursRef.current.length > 0) {
      ctx.lineJoin = "round";
      ctx.lineCap = "round";
      for (const cnt of contoursRef.current) {
        ctx.strokeStyle = cnt.major ? "rgba(30,20,10,0.75)" : "rgba(40,28,14,0.5)";
        ctx.lineWidth = cnt.major ? majorW : thinW;
        ctx.stroke(cnt.path);
      }
    }

    // ブラシ/ポリゴンのプレビュー(常時表示。モード切替を廃止したため編集ツールは常に有効)。
    // 水面モード・資源モード中は標高編集のブラシ/ポリゴンプレビューは表示しない
    // (操作が水面/資源の作図に切り替わるため)。
    const previewLineW = 1.5 / v.zoom;
    if (ts.waterMode || ts.resourceMode || ts.cameraMode) {
      // 水面/資源/カメラモードでは何も描かない(それぞれ下の stroke* で描く)。
    } else if (ts.shape === "polygon") {
      strokePolygonPreview(ctx, polygonPointsRef.current, ts.polygonClosed ? null : brushWorldPosRef.current, previewLineW);
    } else if (brushWorldPosRef.current) {
      strokeBrushOutline(ctx, brushWorldPosRef.current.x, brushWorldPosRef.current.z, ts.shape, ts.brushParams, ts.featherWidth, previewLineW);
    }

    // 水面(海面以外の湖など)。確定済みは常に表示し、作図中の頂点は水面モードのときだけ追従表示する。
    strokeWaterSurfaces(
      ctx,
      waterSurfacesRef.current,
      ts.waterMode ? waterPointsRef.current : [],
      // 形状を閉じたらマウス追従線は消す(閉じたポリゴンをそのまま見せる)。
      ts.waterMode && !ts.waterPolygonClosed ? brushWorldPosRef.current : null,
      previewLineW
    );

    // 川。彫り込み済みは濃青の帯+中心線、未彫り込み(計算直後)は水色の破線。
    strokeRivers(ctx, riversRef.current, previewLineW);

    // 天然資源エリア。確定済みは常に表示し、作図中の頂点は資源編集タブのときだけ追従表示する。
    strokeResourceAreas(
      ctx,
      resourceAreasRef.current,
      resourceTypeLabel,
      ts.resourceMode ? resourcePointsRef.current : [],
      ts.resourceMode && !ts.resourcePolygonClosed ? brushWorldPosRef.current : null,
      previewLineW
    );

    // カメラ初期状態のマーカー。常に表示し、カメラ編集タブのときだけ注視点指定の目安線を追従表示する。
    strokeCameraMarker(ctx, cameraRef.current, ts.cameraMode ? brushWorldPosRef.current : null, previewLineW);

    ctx.restore();

    // オーバーレイ表示更新(直接 DOM 替換で React 再描画を回避)
    if (zoomOverlayRef.current) {
      zoomOverlayRef.current.textContent = Math.round(v.zoom * 100) + "%";
    }
  }

  /**
   * 現在のズーム倍率に応じた間隔で、全セルから等高線を再構築する。
   * marching squares はセル数に比例するコストなので、ドラッグ中は行わず「適用」実行時
   * など標高が確定したタイミング、またはズーム操作が落ち着いたタイミングでのみ呼ぶ。
   */
  function rebuildContoursAtCurrentZoom() {
    const s = settingsRef.current;
    const elev = elevRef.current;
    if (!s || !elev) return;
    const interval = pickContourIntervalForZoom(baseIntervalRef.current, baseZoomRef.current, viewRef.current.zoom);
    contourIntervalRef.current = interval;
    contoursRef.current = buildContours(elev, s.rows, s.cols, s.spacing, s.mapWidth, s.mapHeight, s.minLand, s.maxLand, interval);
    draw();
  }

  /**
   * ズームで等高線間隔を細かくすべきか判定し、必要ならデバウンスして再構築する。
   * ホイール操作の度に毎回 marching squares を回すと重いため、間隔の候補値が実際に
   * 変わる場合のみ、ズームが少し落ち着いてから(150ms)まとめて再構築する。
   */
  function scheduleContourZoomRefresh() {
    const desired = pickContourIntervalForZoom(baseIntervalRef.current, baseZoomRef.current, viewRef.current.zoom);
    if (desired === contourIntervalRef.current) return;
    if (contourZoomTimerRef.current != null) window.clearTimeout(contourZoomTimerRef.current);
    contourZoomTimerRef.current = window.setTimeout(() => {
      contourZoomTimerRef.current = null;
      rebuildContoursAtCurrentZoom();
    }, 150);
  }

  /**
   * 3Dプレビューウィンドウ(開いていれば)へ、現在の標高・選択マスク・メタデータの全体を送る。
   * ロード完了時・マップリサイズ確定時、およびプレビュー側からの request_state 受信時に呼ぶ。
   * プレビューウィンドウが開いていなくても emit 自体は無害(誰も listen していないだけ)なので、
   * 開いているかどうかの判定は行わない。
   */
  function emitTerrain3dFullState() {
    const s = settingsRef.current;
    const elev = elevRef.current;
    const mask = selectionMaskRef.current;
    if (!s || !elev || !mask) return;
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
      rivers: serializeRivers(),
    };
    void emit(EVT_TERRAIN_3D_FULL_STATE, payload);
  }

  /**
   * 選択マスクの変化(rect範囲)を3Dプレビューへ送る予約を入れる。ブラシドラッグ中は
   * mousemoveの度に呼ばれるが、1フレームに複数回来た分は合成して1回のemitにまとめる
   * (rAFで間引く)。
   */
  function schedulePreviewSelectionSync(rect: DirtyRect) {
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
      const payload: Terrain3DSelectionDeltaPayload = {
        mapId,
        ...pending,
        maskRectBase64: uint8ToBase64(quantizeMaskRect(mask, s.cols, pending)),
      };
      void emit(EVT_TERRAIN_3D_SELECTION_DELTA, payload);
    });
  }

  /** 現在のブラシ位置(brushWorldPosRef)の形状を選択マスクへマージする(円/四角ツールでのクリック単発時) */
  function mergeBrushAtCurrentPos() {
    const s = settingsRef.current;
    const mask = selectionMaskRef.current;
    const pos = brushWorldPosRef.current;
    const imgData = selectionImageDataRef.current;
    const selCtx = selectionCtxRef.current;
    if (!s || !mask || !pos || !imgData || !selCtx) return;
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
    refreshSelectionTexture(rect, mask, s.cols, imgData, selCtx);
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
    const imgData = selectionImageDataRef.current;
    const selCtx = selectionCtxRef.current;
    if (!s || !mask || !imgData || !selCtx || poly.length < 3) return;
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
      refreshSelectionTexture(rect, mask, s.cols, imgData, selCtx);
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
   * 川編集タブではポインタ操作を地形編集と同じ範囲選択(降雨範囲の指定に使う)のままにする。
   * 未彫り込みの川はタブを離れても破棄しない(一覧から個別に彫る途中の状態を保つため)。
   */
  function handleTabChange(tab: "terrain" | "water" | "river" | "resource" | "camera") {
    setActiveTab(tab);
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
    emitTerrain3dFullState();
  }

  /** 最後に作成した水面を1枚削除する。 */
  function removeLastWaterSurface() {
    if (waterSurfacesRef.current.length === 0) return;
    waterSurfacesRef.current = waterSurfacesRef.current.slice(0, -1);
    setWaterSurfaceCount(waterSurfacesRef.current.length);
    draw();
    emitTerrain3dFullState();
  }

  /** 作成済みの水面をすべて削除する。作図中の頂点も破棄する。 */
  function clearAllWaterSurfaces() {
    waterSurfacesRef.current = [];
    waterPointsRef.current = [];
    setWaterSurfaceCount(0);
    setWaterPointCount(0);
    setWaterPolygonClosed(false);
    draw();
    emitTerrain3dFullState();
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
   * 3Dプレビューの水面が上下する。保存・エクスポートには使わない(未確定分を含めないため)。
   */
  function serializeWaterSurfacesForPreview(): WaterSurfacePayload[] {
    const list = serializeWaterSurfaces();
    if (waterPolygonClosed && waterPointsRef.current.length >= 3) {
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
   * 登録した資源は保存(meta.json)・エクスポート(サイドカー map_xxx.resources.json)の
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
   * (ボタン側も無効化されている)。
   */
  function confirmResourceArea() {
    const poly = resourcePointsRef.current;
    if (poly.length < 3 || resourceTypeInput === null) return;
    const area: EditorResourceArea = {
      id: `resource_${Date.now()}_${Math.floor(Math.random() * 1000)}`,
      resourceType: resourceTypeInput,
      maxAmount: resourceMaxAmountInput,
      recoveryRate: resourceRecoveryRateInput,
      points: poly.map((p) => ({ x: p.x, z: p.z })),
    };
    resourceAreasRef.current = [...resourceAreasRef.current, area];
    syncResourceListState();
    resourcePointsRef.current = [];
    setResourcePointCount(0);
    setResourcePolygonClosed(false);
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

  /** meta.json / サイドカーから読み込んだ ResourceAreaPayload 配列を内部表現へ変換する。 */
  /* ============================================================================
   * カメラ初期状態(カメラ編集タブ)
   *
   * 左クリックでカメラ位置(x, z)を置き、右クリックで注視点を指定して方位(yaw)を決める。
   * 高さ(y)・見下ろし角(pitch)・方位はカードの数値入力でも変えられる。マップに1つだけ持ち、
   * 保存(meta.json)・エクスポート(terrain_maps.yaml の camera)の対象になる。
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
   * 「中心線±(幅/2+なじみ帯)」、復元した川(幅情報なし)は「既存の範囲を数セル膨らませた帯」。
   * 後者により、彫り込み後に手作業で岸を削った/盛った変更にも範囲が追従する。
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
      const hasSpline = river.points.length > 0 && river.points[0].width > 0;
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
    emitTerrain3dFullState();
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
  }

  /**
   * 彫り込み後の共通処理: 川の範囲ポリゴンの抽出(彫り込み済み全川)・地形テクスチャの
   * 全域塗り直し・等高線再構築・3Dプレビューへの全状態送信。複数の川がマップ全域に及ぶため、
   * 侵食の finishErosion と同じく全域rectで塗り直す。3Dプレビューへは川リストも変わるため、
   * 標高差分ではなく full_state を送る。
   */
  function finishRiverCarve(s: TerrainSettings, elev: Float32Array) {
    recomputeRiverPolygons(s, elev);
    const fullRect: DirtyRect = { rMin: 0, rMax: s.rows - 1, cMin: 0, cMax: s.cols - 1 };
    if (terrainImageDataRef.current && terrainCtxRef.current) {
      refreshTerrainTexture(fullRect, elev, s.cols, s.minLand, s.maxLand, terrainImageDataRef.current, terrainCtxRef.current);
    }
    rebuildContoursAtCurrentZoom();
    emitTerrain3dFullState();
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
  }

  /**
   * 川を1本削除する。未彫り込みの川は計算結果を破棄するだけ。彫り込み済みの川は
   * データ(スプライン・水面リボン・保存対象)のみ削除し、彫り込み済みの地形は戻さない
   * (地形も戻したい場合は Ctrl+Z を使う)。
   */
  function removeRiver(id: string) {
    riversRef.current = riversRef.current.filter((river) => river.id !== id);
    syncRiverListState();
    draw();
    emitTerrain3dFullState();
  }

  /** 川をすべて削除する(地形は戻さない) */
  function clearAllRivers() {
    riversRef.current = [];
    syncRiverListState();
    draw();
    emitTerrain3dFullState();
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
      });
    }
    return result;
  }

  /** ポリゴンが閉じた状態のとき、ぼかし幅を含む仮の選択範囲をプレビューCanvasへ書き込む */
  function refreshPolygonPreviewTexture() {
    const s = settingsRef.current;
    const poly = polygonPointsRef.current;
    const imgData = polygonPreviewImageDataRef.current;
    const ctx = polygonPreviewCtxRef.current;
    if (!s || !imgData || !ctx) return;

    imgData.data.fill(0);

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

      const data = imgData.data;
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
            const i = rowBase + c;
            data[i * 4 + 0] = 80;
            data[i * 4 + 1] = 190;
            data[i * 4 + 2] = 255;
            data[i * 4 + 3] = Math.round(weight * 160);
          }
        }
      }
    }
    ctx.putImageData(imgData, 0, 0);
    draw();
  }

  // polygonClosed, featherWidth, selectionFalloff の変更を監視し、ポリゴンプレビューを更新する
  useEffect(() => {
    if (polygonClosed) {
      refreshPolygonPreviewTexture();
    } else {
      const imgData = polygonPreviewImageDataRef.current;
      const ctx = polygonPreviewCtxRef.current;
      if (imgData && ctx) {
        imgData.data.fill(0);
        ctx.putImageData(imgData, 0, 0);
        draw();
      }
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [polygonClosed, featherWidth, selectionFalloff]);

  /** 選択範囲(ブラシ/ポリゴンでマージ済みのマスク)を完全にクリアする */
  function resetSelection() {
    const mask = selectionMaskRef.current;
    if (mask) mask.fill(0);
    selectionBoundsRef.current = null;
    const imgData = selectionImageDataRef.current;
    const selCtx = selectionCtxRef.current;
    if (imgData && selCtx) clearSelectionTexture(imgData, selCtx);
    clearPolygonPoints();
    setHasSelection(false);
    draw();
    void emit(EVT_TERRAIN_3D_SELECTION_RESET, { mapId } satisfies Terrain3DSelectionResetPayload);
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
    if (terrainImageDataRef.current && terrainCtxRef.current) {
      refreshTerrainTexture(bounds, elev, s.cols, s.minLand, s.maxLand, terrainImageDataRef.current, terrainCtxRef.current);
    }
    rebuildContoursAtCurrentZoom();
    const elevDeltaPayload: Terrain3DElevationDeltaPayload = {
      mapId,
      ...bounds,
      elevRectBase64: uint8ToBase64(encodeFloat16Array(extractElevRect(elev, s.cols, bounds))),
    };
    void emit(EVT_TERRAIN_3D_ELEVATION_DELTA, elevDeltaPayload);
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
   * 流れ、変化範囲が選択矩形ちょうどにはならないため、選択の有無に関わらず全域rectで
   * テクスチャを塗り直し、等高線を再構築(内部でdraw()される)し、3Dプレビューへ全域の
   * 標高差分を送る。
   */
  function finishErosion(s: TerrainSettings, elev: Float32Array) {
    const fullRect: DirtyRect = { rMin: 0, rMax: s.rows - 1, cMin: 0, cMax: s.cols - 1 };
    if (terrainImageDataRef.current && terrainCtxRef.current) {
      refreshTerrainTexture(fullRect, elev, s.cols, s.minLand, s.maxLand, terrainImageDataRef.current, terrainCtxRef.current);
    }
    rebuildContoursAtCurrentZoom();
    const payload: Terrain3DElevationDeltaPayload = {
      mapId,
      ...fullRect,
      elevRectBase64: uint8ToBase64(encodeFloat16Array(extractElevRect(elev, s.cols, fullRect))),
    };
    void emit(EVT_TERRAIN_3D_ELEVATION_DELTA, payload);
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

  /**
   * 標高配列と新しいマップ寸法を与えて、地形テクスチャ・選択キャンバス・等高線・グリッド・
   * settingsRef を一括で再構築する。マップリサイズ時とロード時の共通処理。
   * 標高レンジ(minLand, maxLand)も再計算し、resetView() と draw() も行う。
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
    const cs = cellSizeParam ?? (cellSize > 0 ? cellSize : 500);

    let minLand = Infinity;
    let maxLand = -Infinity;
    for (let i = 0; i < count; i++) {
      const e = elev[i];
      if (e > 0) { if (e < minLand) minLand = e; if (e > maxLand) maxLand = e; }
    }
    if (!isFinite(minLand) || !isFinite(maxLand)) { minLand = 0; maxLand = 1; }
    if (maxLand - minLand < 1) maxLand = minLand + 1;

    const off = document.createElement("canvas");
    off.width = cols;
    off.height = rows;
    const octx = off.getContext("2d")!;
    const img = octx.createImageData(cols, rows);
    const px = img.data;
    for (let i = 0; i < count; i++) {
      const [r, g, b] = computePixelRGB(elev[i], minLand, maxLand);
      px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = 255;
    }
    octx.putImageData(img, 0, 0);

    const selOff = document.createElement("canvas");
    selOff.width = cols;
    selOff.height = rows;
    const selOctx = selOff.getContext("2d")!;
    const selImg = selOctx.createImageData(cols, rows);
    selOctx.putImageData(selImg, 0, 0);

    const baseInterval = pickContourInterval(maxLand - minLand);

    elevRef.current = elev;
    terrainCanvasRef.current = off;
    terrainImageDataRef.current = img;
    terrainCtxRef.current = octx;
    const polyPreviewOff = document.createElement("canvas");
    polyPreviewOff.width = cols;
    polyPreviewOff.height = rows;
    const polyPreviewOctx = polyPreviewOff.getContext("2d")!;
    const polyPreviewImg = polyPreviewOctx.createImageData(cols, rows);
    polyPreviewOctx.putImageData(polyPreviewImg, 0, 0);

    selectionCanvasRef.current = selOff;
    selectionImageDataRef.current = selImg;
    selectionCtxRef.current = selOctx;
    polygonPreviewCanvasRef.current = polyPreviewOff;
    polygonPreviewImageDataRef.current = polyPreviewImg;
    polygonPreviewCtxRef.current = polyPreviewOctx;
    selectionMaskRef.current = new Float32Array(count);
    selectionBoundsRef.current = null;
    setHasSelection(false);

    gridPathRef.current = buildGridPath(mapWidth, mapHeight, 100);
    cellGridPathRef.current = buildGridPath(mapWidth, mapHeight, cs);
    cellGridIntervalRef.current = cs;

    contoursRef.current = buildContours(elev, rows, cols, spacing, mapWidth, mapHeight, minLand, maxLand, baseInterval);

    settingsRef.current = { mapWidth, mapHeight, spacing, rows, cols, minLand, maxLand };

    resetView();
    baseIntervalRef.current = baseInterval;
    baseZoomRef.current = viewRef.current.zoom;
    contourIntervalRef.current = baseInterval;

    draw();
    // ロード完了時・マップリサイズ確定時は、グリッド形状自体が変わりうるため
    // (選択マスクもここで全クリアされているため)、3Dプレビューへ全状態を送り直す。
    emitTerrain3dFullState();
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
    }));
    resourceAreasRef.current = resourceAreasRef.current.map((area) => ({
      ...area,
      points: area.points.map((p) => ({ x: p.x + dx, z: p.z + dz })),
    }));
    waterPointsRef.current = waterPointsRef.current.map((p) => ({ x: p.x + dx, z: p.z + dz }));
    resourcePointsRef.current = resourcePointsRef.current.map((p) => ({ x: p.x + dx, z: p.z + dz }));
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

    setResizeStatus("ok");
    setSaveStatus("idle");
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
   * 編集中の標高データとメタデータを、Tauriコマンド save_terrain_editor_data で
   * ローカル作業コピー(meta.json / near.bin)へ書き戻す。
   *
   * マップIDをロード時から変更していれば別ディレクトリへの新規保存(別名保存)になる。
   * ここでの保存はあくまで作業コピーの更新であり、バックエンドの元データやゲームの
   * セーブデータには影響しない。ゲームで使うには、地形データ作成マニュアルの手順で
   * 別途 .npy への変換と assets/ への配置が必要。
   */
  async function handleSaveTerrainEditorData() {
    const s = settingsRef.current;
    const elev = elevRef.current;
    const targetMapId = mapIdInput.trim();
    if (!s || !elev) return;
    if (!targetMapId) {
      setSaveStatus("error");
      setSaveErrorMsg(t("terrainEditor.saveMapIdRequired", { defaultValue: "マップIDを入力してください" }));
      return;
    }
    setSaveStatus("saving");
    setSaveErrorMsg("");
    try {
      const bytes = encodeFloat16Array(elev);
      const dataBase64 = uint8ToBase64(bytes);
      const thumbnailBase64 = captureTerrainThumbnail(
        elev,
        s.rows,
        s.cols,
        s.mapWidth,
        s.mapHeight,
        s.spacing,
        s.minLand,
        s.maxLand
      );
      await invoke("save_terrain_editor_data", {
        mapId: targetMapId,
        mapWidth: s.mapWidth,
        mapHeight: s.mapHeight,
        nearSpacing: s.spacing,
        farSpacing,
        cellSize,
        gridRows: s.rows,
        gridCols: s.cols,
        dataBase64,
        thumbnailBase64,
        waterSurfaces: serializeWaterSurfaces(),
        rivers: serializeRivers(),
        resources: serializeResourceAreas(),
        camera: cameraRef.current,
      });
      setSaveStatus("saved");
    } catch (error) {
      setSaveStatus("error");
      setSaveErrorMsg(error instanceof Error ? error.message : String(error));
    }
  }

  /**
   * 編集中の標高データを、バックエンドが読み込める .npy ファイルへエクスポートする。
   *
   * 保存先パスは @tauri-apps/plugin-dialog の保存ダイアログでユーザーに選ばせ、
   * その後 Tauriコマンド export_terrain_as_npy に現在の標高データ(メモリ上の elev 配列)を
   * そのまま渡す。「保存」ボタン(作業コピーへの書き戻し)とは独立しており、事前の保存操作は
   * 不要。.npy への変換はRust側が自前で行う(外部プロセスに依存しない)。
   * サムネイル画像(160x120のPNG)もこの時点の標高データから生成し、.npyと同じファイル名
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
      const thumbnailBase64 = captureTerrainThumbnail(
        elev,
        s.rows,
        s.cols,
        s.mapWidth,
        s.mapHeight,
        s.spacing,
        s.minLand,
        s.maxLand
      );
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
        camera: cameraRef.current,
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
        if (cancelled) return;

        const bytes = base64ToUint8(data.data_base64);
        const count = data.grid_rows * data.grid_cols;
        if (count <= 0) throw new Error("empty terrain data");
        if (bytes.byteLength < count * 2) throw new Error("terrain data too short");

        const elev = decodeFloat16Array(bytes, count);
        const spacing = data.near_spacing > 0 ? data.near_spacing : 10;
        const mapWidth = data.map_width;
        const mapHeight = data.map_height;

        // 地図メタデータ欄の初期値。map_id はロード元のIDから開始し、変更すれば別名保存になる。
        // cellSize state は rebuildTerrainVisuals より先にセットする（すでに引数で渡しているので
        // 実害はないが、後続のセル境界グリッドuseEffectが古い値で走るのを避けるため）。
        setMapIdInput(mapId);
        setFarSpacing(data.far_spacing > 0 ? data.far_spacing : 200);
        setCellSize(data.cell_size > 0 ? data.cell_size : 500);
        setSaveStatus("idle");
        setSaveErrorMsg("");

        // 標高レンジ計算・テクスチャ構築・等高線・グリッド・settingsRef 設定・リセットビュー・描画
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
    if (status === "ready") emitTerrain3dFullState();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status, waterLevelInput, waterPolygonClosed]);

  /** セルサイズ(メタデータ欄で編集可能)を変えたら、セル境界グリッドを再構築する */
  useEffect(() => {
    const s = settingsRef.current;
    if (status === "ready" && s && cellSize > 0) {
      cellGridPathRef.current = buildGridPath(s.mapWidth, s.mapHeight, cellSize);
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
        v.offsetY = dragRef.current.offY - dy / v.zoom;
        draw();
        return;
      }

      if (inside) draw();
    }

    function onUp(e: MouseEvent) {
      // 左ボタンがドラッグへ移行せずに離された場合のみ、その位置でクリック単発の選択操作を行う。
      if (e.button === 0 && leftDownRef.current && !dragRef.current.active) {
        const ts = toolStateRef.current;
        const rect = canvasEl.getBoundingClientRect();
        const [wx, wz] = screenToWorld(e.clientX - rect.left, e.clientY - rect.top);
        if (ts.waterMode) {
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
      draw();
      // 拡大したら等高線を細かくする(ズームが落ち着いてからデバウンスして再構築)
      scheduleContourZoomRefresh();
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

  /** Undo / Redo のキーボードショートカット。Ctrl+Z で戻る、Ctrl+Y (Ctrl+Shift+Z) で進む。 */
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
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
      {/* 左ツールパネル。カード形式の編集UIを配置する。 */}
      <aside className="terrain-editor-tools">
        <div className="terrain-editor-tools-title-row">
          <div className="terrain-editor-tools-title">
            {t("terrainEditor.toolsTitle", { defaultValue: "編集ツール" })}
          </div>
          <button type="button" className="terrain-editor-btn" onClick={() => void handleOpen3dPreview()}>
            {t("terrainEditor.open3dPreview", { defaultValue: "3Dプレビューを開く" })}
          </button>
        </div>

        {/* 編集機能のタブ切り替えバー */}
        <div className="terrain-editor-tabs">
          <button
            type="button"
            className={`terrain-editor-tab ${activeTab === "terrain" ? "active" : ""}`}
            onClick={() => handleTabChange("terrain")}
            disabled={status !== "ready"}
          >
            {t("terrainEditor.tabTerrain", { defaultValue: "地形編集" })}
          </button>
          <button
            type="button"
            className={`terrain-editor-tab ${activeTab === "water" ? "active" : ""}`}
            onClick={() => handleTabChange("water")}
            disabled={status !== "ready"}
          >
            {t("terrainEditor.tabWater", { defaultValue: "水面編集" })}
          </button>
          <button
            type="button"
            className={`terrain-editor-tab ${activeTab === "river" ? "active" : ""}`}
            onClick={() => handleTabChange("river")}
            disabled={status !== "ready"}
          >
            {t("terrainEditor.tabRiver", { defaultValue: "川編集" })}
          </button>
          <button
            type="button"
            className={`terrain-editor-tab ${activeTab === "resource" ? "active" : ""}`}
            onClick={() => handleTabChange("resource")}
            disabled={status !== "ready"}
          >
            {t("terrainEditor.tabResource", { defaultValue: "資源編集" })}
          </button>
          <button
            type="button"
            className={`terrain-editor-tab ${activeTab === "camera" ? "active" : ""}`}
            onClick={() => handleTabChange("camera")}
            disabled={status !== "ready"}
          >
            {t("terrainEditor.tabCamera", { defaultValue: "カメラ" })}
          </button>
        </div>

        {/* カード配置領域(上半分)。左カラム(メタデータ)と右カラム(各タブの編集カード)の2列。 */}
        <div className="terrain-editor-cards-area">

          {/* ============================================================ */}
          {/* 左カラム: 地図メタデータ */}
          {/* ============================================================ */}
          <div className="terrain-editor-cards-left">

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

              {/* 保存・エクスポート */}
              <div className="terrain-editor-action-buttons" style={{ marginTop: 10 }}>
                <button
                  type="button"
                  className="terrain-editor-btn terrain-editor-btn-confirm"
                  disabled={status !== "ready" || saveStatus === "saving"}
                  onClick={() => void handleSaveTerrainEditorData()}
                >
                  {saveStatus === "saving"
                    ? t("terrainEditor.saving", { defaultValue: "保存中..." })
                    : t("terrainEditor.save", { defaultValue: "保存" })}
                </button>
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
              {saveStatus === "saved" && (
                <div className="terrain-editor-hint terrain-editor-hint--success">
                  {t("terrainEditor.saveSuccess", { defaultValue: "作業コピーに保存しました" })}
                </div>
              )}
              {saveStatus === "error" && (
                <div className="terrain-editor-hint terrain-editor-hint--error">
                  {saveErrorMsg || t("terrainEditor.saveFailed", { defaultValue: "保存に失敗しました" })}
                </div>
              )}
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
          <div className="terrain-editor-cards-right">

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
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.resourceMaxAmount", { defaultValue: "埋蔵量上限" })}</span>
                      <NumberStepper
                        value={resourceMaxAmountInput}
                        onChange={setResourceMaxAmountInput}
                        min={1}
                        max={10000000}
                        step={100}
                        integerOnly={true}
                        disabled={status !== "ready"}
                      />
                    </div>
                    <div className="terrain-editor-param-row">
                      <span className="terrain-editor-param-label">{t("terrainEditor.resourceRecoveryRate", { defaultValue: "回復率(1日あたり)" })}</span>
                      <NumberStepper
                        value={resourceRecoveryRateInput}
                        onChange={setResourceRecoveryRateInput}
                        min={0}
                        max={1}
                        step={0.001}
                        integerOnly={false}
                        disabled={status !== "ready"}
                      />
                    </div>
                    <div className="terrain-editor-hint">
                      {t("terrainEditor.resourceRecoveryHint", {
                        defaultValue: "回復率0は採取した分だけ枯渇する非再生資源(鉱脈・油田など)になります",
                      })}
                    </div>
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
                                  maxAmount: area.maxAmount,
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
                        {riverList.map((river, idx) => (
                          <div key={river.id} className="terrain-editor-param-group">
                            <div className="terrain-editor-param-row">
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
                            <div className="terrain-editor-action-buttons">
                              {!river.carved && (
                                <button
                                  type="button"
                                  className="terrain-editor-btn terrain-editor-btn-confirm"
                                  disabled={status !== "ready"}
                                  onClick={() => carveOneRiver(river.id)}
                                >
                                  {t("terrainEditor.riverCarveOne", { defaultValue: "彫る" })}
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
        <canvas ref={canvasRef} className="terrain-editor-canvas" />
        <div className="terrain-editor-coord-overlay" ref={coordOverlayRef}>---, ---</div>
        <div className="terrain-editor-zoom-overlay" ref={zoomOverlayRef}>100%</div>
      </div>
    </div>
  );
}
