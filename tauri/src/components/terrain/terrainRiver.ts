/**
 * 地形エディタの「川編集」用の川生成アルゴリズム集。
 *
 * terrainErosion.ts と同じ方針で、TerrainEditorCanvas から呼ばれる純粋関数のみを置き、
 * React やキャンバス描画には一切依存しない。標高データはエディタ本体と同じ
 * 「行優先の Float32Array(rows × cols、格子間隔 spacing メートル、標高 0 以下は海面)」を
 * そのまま受け取る。ワールド座標は worldX = col*spacing - mapWidth/2(原点=マップ中央)。
 *
 * 処理は3段階に分かれ、UIの「流路を計算(プレビュー)」→「川床を彫って確定」の
 * 2段階フローに対応する:
 *   1. extractRiverNetwork: 地形から流路を自動特定する(標高は変更しない)。
 *      ダウンサンプル → priority-flood 窪地埋め → D8流向 → 流量集積(flow accumulation)
 *      → 流量が閾値を超えるセル列を川ネットワークとして抽出し、本流/支流に分解する。
 *      雨量(mm/年)と降雨範囲(選択マスク)をパラメータとして受け取る。
 *   2. evolveRivers: 蛇行進化シミュレーション(曲率駆動の流路横移動、Howard-Knutson系の
 *      簡易版)で平地区間の蛇行を発達させ、水面標高プロファイル(流下方向に単調非増加)を
 *      確定する(標高は変更しない)。
 *   3. carveRivers: 確定した流路に沿って川床を標高配列へ彫り込む(in-place 書き換え)。
 *      カーブ外岸が深く急峻になる非対称断面と、低勾配・大流量区間の中州(網状化)を再現する。
 *
 * 保存契約(RiverPayload、terrainData.ts)への変換はエディタ本体側で行う。
 * 仕様の正は genesis-docs/docs/geometries/D_地形形状.md §D.11。
 */

import type { WaterSurfacePayload, WaterPolygonGeoJson, GridRect } from "./terrainData";
import type { ErosionRegion } from "./terrainErosion";
import {
  RiverProfileSampler,
  extractRegionPolygon,
  pointInPolygon,
  polygonBounds,
  rasterizePolygonMask,
} from "./riverGeometry";

/** 川生成のパラメータ(UIから調整可能なもののみ) */
export type RiverGenParams = {
  /** 年間雨量(mm)。多いほど同じ地形でも支流が増え、川が太くなる */
  rainfallMmPerYear: number;
  /** 川になる最小流量(m³/s)。流量集積がこの値を超えたセルが川になる */
  minDischargeM3s: number;
  /** 蛇行の強さ(0..1)。0で蛇行進化を無効化 */
  meanderStrength: number;
  /** 蛇行進化の反復回数。多いほど蛇行波長が成長する */
  meanderIterations: number;
  /** 川幅係数。幅 = widthCoef × 流量^0.5 (hydraulic geometry) */
  widthCoef: number;
  /** 深さ係数。深さ = depthCoef × 流量^0.4 (hydraulic geometry) */
  depthCoef: number;
  /** 低勾配・大流量区間で中州(網状流路)を作るか */
  enableBraiding: boolean;
};

/** 流路中心線上の1点。座標はワールドXZ、標高は海面=0の絶対フレーム(m) */
export type RiverPoint = {
  x: number;
  z: number;
  /** この地点の流量(m³/s)。合流のたびに増える */
  q: number;
  /** 川幅(m)。網状化区間では拡幅済みの値 */
  width: number;
  /** 水面のY標高(m)。流下方向に単調非増加 */
  waterY: number;
  /** 川床(最深部=タルウェグ)のY標高(m)。waterY - 深さ */
  bedY: number;
  /** 無次元化した平滑曲率(±0.8にクランプ済み)。正=左カーブ。断面の非対称化に使う */
  curvature: number;
  /** 網状化(中州あり)区間か */
  braided: boolean;
};

/** 川の終端(河口)の種類 */
export type RiverMouthKind = "sea" | "lake" | "edge" | "confluence";

/** extractRiverNetwork の結果。診断情報つき */
export type RiverExtractionResult = {
  rivers: GeneratedRiver[];
  /** 陸上セルで到達した最大流量(m³/s)。流路が0本のとき「閾値が最大流量に届いていない」
   *  ことをUIで示し、閾値・雨量の調整を促すための診断値 */
  maxDischargeM3s: number;
  /** 短すぎて破棄された川の本数。流路が0本でも最大流量が閾値を超えている場合、
   *  原因はこちら(閾値を超える区間が短すぎる)なので、UIは閾値不足とは別の
   *  ヒントを出す必要がある */
  discardedShortCount: number;
};

/** 生成された川1本分。points は上流→下流の順 */
export type GeneratedRiver = {
  id: string;
  points: RiverPoint[];
  mouth: RiverMouthKind;
  /** mouth === "lake" のとき、流入先の湖の水位(m)。水面プロファイルの河口クランプに使う */
  mouthWaterLevel?: number;
  /** mouth === "confluence" のとき、合流先の川とその最近点インデックス */
  confluence?: { riverId: string; pointIndex: number };
};

/* ============================================================================
 * 内部定数
 *
 * UIに露出すると調整項目が多すぎるため、効果への影響が大きいものだけを
 * RiverGenParams としてパラメータ化し、残りはここで固定する。
 * ========================================================================== */

const RIVER = {
  /** 流路特定に使うダウンサンプル格子の最大辺セル数。5000²のマップでもJS同期実行を数秒に収める */
  maxDsDim: 2048,
  /** priority-flood の埋め立て勾配(m)。平坦部でも一意な流下方向を保証する */
  fillEpsilon: 1e-4,
  /** 流出係数。降った雨のうち河川流量になる割合 */
  runoffCoef: 0.4,
  /** 豊水期(bankfull)換算係数。年平均流量に対する河道形成流量の倍率。
   *  川の見た目・河道の規模(幅・深さ)を決めるのは年平均流量ではなく豊水期・洪水時の
   *  流量であり、小流域では年平均の数十倍になる。年平均のままだと数km規模の島マップでは
   *  最大流量が0.1m³/s未満となり、現実的な閾値では川が1本もできない。
   *  hydraulic geometry(幅∝Q^0.5)も本来bankfull流量に対する関係式なので、
   *  この換算後の流量を discharge として一貫して使う */
  bankfullFactor: 30,
  /** 中心線ポリラインのリサンプル間隔(spacing に対する倍率) */
  resampleFactor: 2,
  /** これより点数が少ない川は破棄する(短すぎる沢は描画・彫り込みに値しないため)。
   *  数km規模の島マップの短い川も残せるよう、極端に大きくしない */
  minRiverPoints: 10,
  /** 川幅の下限(spacing に対する倍率)。1格子より細い川は彫り込みが表現できない。
   *  小さなマップでは物理幅が数mになり全区間がこの下限に張り付く(=上流と下流で
   *  太さが変わらなく見える)ため、必要最小限に留める */
  minWidthFactor: 1.5,
  /** 深さの下限(m) */
  minDepth: 0.5,
  /** 蛇行: 横移動速度の基準係数(meanderStrength=1 のときの値) */
  migrationCoef: 1.0,
  /** 蛇行: ベンドの成長飽和。曲率半径がこの倍率×川幅まで小さくなったベンドは
   *  それ以上成長しない(実河川の最小曲率半径 ~2-3川幅 に相当)。無限成長の暴走を防ぐ */
  saturationRadiusWidths: 3,
  /** 蛇行: 平滑曲率に混ぜる局所曲率の割合(0=上流EMAのみ、1=局所のみ)。
   *  局所項が蛇行振幅の成長を、上流項が頂点の下流方向への進行を担う */
  curvatureLocalMix: 0.6,
  /** 蛇行: 初期擾乱の振幅(川幅に対する倍率)と波長(川幅に対する倍率)。
   *  曲率駆動の蛇行進化は完全な直線(曲率0)からは成長できないため、実河川の河床の
   *  不整に相当する小さな横方向の揺らぎを最初に与えて蛇行の種にする */
  seedAmpWidths: 0.3,
  seedWavelengthWidths: 12,
  /** 蛇行: 上流曲率の減衰距離(川幅に対する倍率)。HKの畳み込みを指数移動平均で近似する */
  curvatureLagWidths: 10,
  /** 蛇行: 1イテレーションの横移動量の上限(リサンプル間隔に対する倍率)。数値的な暴走を防ぐ */
  maxStepFactor: 0.4,
  /** 蛇行: 水面縦断勾配がこの値以上の区間は蛇行しない(山地の急流) */
  maxMeanderSlope: 0.01,
  /** 蛇行: ネックカットオフの判定。パス距離 > neckPathWidths×幅 の点対が
   *  ユークリッド距離 < neckCutWidths×幅 まで接近したらループを切除する */
  neckPathWidths: 5,
  neckCutWidths: 1.5,
  /** 水面プロファイル: 最小縦断勾配(m/m)。水面が完全に平坦になるのを防ぐ */
  minSurfaceSlope: 5e-5,
  /** 水面プロファイル: 河口から上流へ水位が戻るときの最大勾配(m/m)。河口クランプのなじませに使う */
  maxMouthSlope: 0.005,
  /** 断面: 無次元曲率のクランプ値。タルウェグの外岸寄せと淵の深化に効く */
  maxNormCurvature: 0.8,
  /** 断面: タルウェグ(最深線)を外岸側へ寄せる割合(無次元曲率に対する係数) */
  thalwegShift: 0.6,
  /** 断面: カーブの淵(bend pool)の深化係数。深さ×(1 + poolDeepen×|κ|) */
  poolDeepen: 0.5,
  /** 彫り込み: 岸肩の水面からの高さ(m)。この高さから外側のなじみ帯で既存地形へ遷移する */
  freeboard: 0.7,
  /** 彫り込み: 河道壁(岸)の勾配(tan)。約35°。断面のV字プロファイルとの min を採ることで、
   *  岸際が実河川の「水を抜いたときの急な壁」のように立ち上がる */
  bankSlope: 0.7,
  /** 彫り込み: 断面プロファイルのべき指数(深さ = d × (1 - |x|^p))。
   *  p=1 は直線のV字だが、谷底付近の勾配が緩く「ふわっとした」窪みに見える。
   *  p<1 にするとタルウェグ直上で勾配が急峻になり、川底の最深部が鋭角なノッチとして
   *  はっきり刻まれる(粗いグリッドでも彫られたことが視認できる) */
  crossSectionExponent: 0.5,
  /** 彫り込み: なじみ帯の幅(川幅に対する倍率と、spacing に対する下限倍率)。
   *  広くすると川の周囲がお椀型に広くへこんでしまうため、岸肩から既存地形へ
   *  素早く遷移する最小限の幅に留める */
  blendWidthFactor: 0.3,
  blendMinSpacingFactor: 1.5,
  /** 網状化: 判定条件(水面勾配の上限、川幅の下限[m]、連続区間の最小点数)。
   *  幅の下限は widthCoef=5 のとき流量36m³/s相当。砂利床の中規模河川でも網状化する
   *  実態(日本の扇状地河川など)に合わせ、大河川専用にはしない */
  braidMaxSlope: 0.002,
  braidMinWidth: 30,
  braidMinRun: 4,
  /** 網状化: 拡幅倍率 */
  braidWidenFactor: 1.6,
  /** 網状化: バー(中州)の高さ(深さに対する倍率)と、バー頂の水面からの深さ(m、冠水中州) */
  braidBarHeightFactor: 0.9,
  braidBarClipBelowWater: 0.3,
  /** 網状化: バーを置く横断範囲(|tn| < この値)。外側の2本の分流路は常に低く保つ */
  braidBarExtent: 0.55,
  /** 網状化: バー場ノイズの流下方向波長(川幅に対する倍率) */
  braidNoiseWavelengthWidths: 3,
  /** 範囲抽出: 「水面より低い」と判定する余裕(m)。水面ちょうどの格子点(岸肩のなじみ帯)を
   *  範囲に入れず、岸線を水面のわずか下で切る */
  polygonWaterEps: 0.05,
  /** 範囲抽出: 範囲に入れる格子点の、水面プロファイル線からの最大距離(m)。湛水区間の
   *  岸越えの氾濫を表現しつつ、平坦な氾濫原で際限なく広がるのを防ぐ。大河川の川幅
   *  (網状化区間の拡幅込み)より十分大きく取る */
  polygonMaxDistFromProfileM: 120,
  /** 範囲抽出: リング間引きの許容誤差(spacing に対する倍率)。格子由来の細かなギザギザを消しつつ
   *  岸線の形状は保つ */
  polygonSimplifyFactor: 0.25,
  /** 制御点: 自動生成した中心線を制御点へ間引くときの許容誤差(spacing に対する倍率)。
   *  蛇行の形を保ちつつノード数を減らす */
  controlPointToleranceFactor: 0.75,
  /** 制御点: 間引きで必ず残す「流量の段差」の相対値。合流点(支流の流入)を制御点として固定する */
  controlPointQJump: 0.1,
  /** 制御点: 河口の判定で他の川へ「合流」とみなす距離の下限(リサンプル間隔に対する倍率)。
   *  相手の川幅とこの値の大きい方を吸着半径にする */
  confluenceSnapDsFactor: 2,
} as const;

/** 1年の秒数(流量[m³/s]への換算に使う) */
const SECONDS_PER_YEAR = 365 * 24 * 3600;

/** D8 の8近傍オフセット(行, 列)と水平距離係数 */
const D8_OFFSETS: ReadonlyArray<{ dr: number; dc: number; dist: number }> = [
  { dr: -1, dc: 0, dist: 1 },
  { dr: 1, dc: 0, dist: 1 },
  { dr: 0, dc: -1, dist: 1 },
  { dr: 0, dc: 1, dist: 1 },
  { dr: -1, dc: -1, dist: Math.SQRT2 },
  { dr: -1, dc: 1, dist: Math.SQRT2 },
  { dr: 1, dc: -1, dist: Math.SQRT2 },
  { dr: 1, dc: 1, dist: Math.SQRT2 },
];

/* ============================================================================
 * 汎用ヘルパー
 * ========================================================================== */

/** 整数格子座標から決定論的な擬似乱数[-1,1]を得る(中州のバー場ノイズ用) */
function hashNoise2D(ix: number, iz: number): number {
  let n = ix * 374761393 + iz * 668265263;
  n = (n ^ (n >> 13)) * 1274126177;
  n = n ^ (n >> 16);
  return ((n & 0x7fffffff) / 0x7fffffff) * 2 - 1;
}

/** hashNoise2D を格子点として滑らかに補間するバリューノイズ([-1,1]) */
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

/** ワールド座標 (wx, wz) の標高をフル解像度の標高配列からバイリニア補間で得る(範囲外はクランプ) */
function sampleElevAtWorld(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  wx: number,
  wz: number,
): number {
  const gx = Math.min(Math.max((wx + mapWidth / 2) / spacing, 0), cols - 1.001);
  const gz = Math.min(Math.max((wz + mapHeight / 2) / spacing, 0), rows - 1.001);
  const cx = Math.floor(gx);
  const cz = Math.floor(gz);
  const fx = gx - cx;
  const fz = gz - cz;
  const base = cz * cols + cx;
  const h00 = elev[base];
  const h10 = elev[base + 1];
  const h01 = elev[base + cols];
  const h11 = elev[base + cols + 1];
  return h00 * (1 - fx) * (1 - fz) + h10 * fx * (1 - fz) + h01 * (1 - fx) * fz + h11 * fx * fz;
}

/** レイキャスト法によるポリゴン内外判定(頂点は [x, z] の配列) */
function pointInRing(x: number, z: number, ring: number[][]): boolean {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const xi = ring[i][0];
    const zi = ring[i][1];
    const xj = ring[j][0];
    const zj = ring[j][1];
    const intersects = zi > z !== zj > z && x < ((xj - xi) * (z - zi)) / (zj - zi) + xi;
    if (intersects) inside = !inside;
  }
  return inside;
}

/* ============================================================================
 * 最小バイナリヒープ(priority-flood 用)
 *
 * キー(埋め立て後標高)は Float64 で持つ。float32 だと標高数千mに対して
 * fillEpsilon(1e-4m)の加算が丸め消失し、平坦部の流下方向が壊れるため。
 * ========================================================================== */

class MinHeap {
  private idx: Int32Array;
  private key: Float64Array;
  private n = 0;

  constructor(capacity: number) {
    this.idx = new Int32Array(capacity);
    this.key = new Float64Array(capacity);
  }

  get size(): number {
    return this.n;
  }

  push(index: number, key: number): void {
    let i = this.n++;
    this.idx[i] = index;
    this.key[i] = key;
    while (i > 0) {
      const parent = (i - 1) >> 1;
      if (this.key[parent] <= this.key[i]) break;
      this.swap(i, parent);
      i = parent;
    }
  }

  /** 最小キーの要素の index を取り出す */
  pop(): number {
    const top = this.idx[0];
    this.n--;
    if (this.n > 0) {
      this.idx[0] = this.idx[this.n];
      this.key[0] = this.key[this.n];
      let i = 0;
      for (;;) {
        const l = i * 2 + 1;
        const r = l + 1;
        let smallest = i;
        if (l < this.n && this.key[l] < this.key[smallest]) smallest = l;
        if (r < this.n && this.key[r] < this.key[smallest]) smallest = r;
        if (smallest === i) break;
        this.swap(i, smallest);
        i = smallest;
      }
    }
    return top;
  }

  private swap(a: number, b: number): void {
    const ti = this.idx[a];
    this.idx[a] = this.idx[b];
    this.idx[b] = ti;
    const tk = this.key[a];
    this.key[a] = this.key[b];
    this.key[b] = tk;
  }
}

/* ============================================================================
 * [1] 流路特定(extractRiverNetwork)
 * ========================================================================== */

/** ダウンサンプル格子と、その上で計算した水文量一式(extractRiverNetwork の内部状態) */
type HydroGrid = {
  dsRows: number;
  dsCols: number;
  stride: number;
  /** ブロックmin でダウンサンプルした標高(湖は水位に置換済み) */
  dsElev: Float32Array;
  /** 各セルが属する湖のインデックス(-1=湖でない) */
  lakeIdx: Int32Array;
  /** priority-flood 後の標高(Float64、ε勾配込み) */
  filled: Float64Array;
  /** D8流向(D8_OFFSETS のインデックス、-1=流出先なし) */
  flowDir: Int8Array;
  /** 流量(m³/s) */
  discharge: Float64Array;
  /** ダウンサンプルセル → ワールド座標 */
  cellToWorldX: (c: number) => number;
  cellToWorldZ: (r: number) => number;
};

/**
 * ダウンサンプル格子を作り、湖の水位置換 → priority-flood → D8流向 → 流量集積までを行う。
 *
 * ダウンサンプルは stride×stride ブロックの最小値を採る。平均や間引きだと1セル幅の谷底が
 * 消えて流路が尾根に乗り上げるため、谷底(最小値)を保存する。
 *
 * 湖(water_surfaces)はポリゴン内のセル標高を水位に置換してから埋める。湖面は平坦+εで
 * 満たされ、流入した水が自然に「溢出点」から流出する経路になる(物理的にも正しい)。
 *
 * 雨は rainRegion(選択マスク)があればその範囲にのみ降らせる(セルの積算初期値=ブロック内の
 * マスク重み平均)。流下は範囲外へも継続するため、範囲内に源を持つ川は海まで自然に伸びる。
 */
function buildHydroGrid(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  params: RiverGenParams,
  lakes: WaterSurfacePayload[],
  rainRegion?: ErosionRegion,
): HydroGrid {
  const stride = Math.max(1, Math.ceil(Math.max(rows, cols) / RIVER.maxDsDim));
  const dsRows = Math.ceil(rows / stride);
  const dsCols = Math.ceil(cols / stride);
  const dsCount = dsRows * dsCols;
  const halfW = mapWidth / 2;
  const halfH = mapHeight / 2;

  // ブロック中心のワールド座標(stride=1 のときは元の格子点と一致する)
  const cellToWorldX = (c: number) => (c * stride + (stride - 1) / 2) * spacing - halfW;
  const cellToWorldZ = (r: number) => (r * stride + (stride - 1) / 2) * spacing - halfH;

  // --- ダウンサンプル(ブロックmin) + 降雨重み(ブロック平均) ---
  const dsElev = new Float32Array(dsCount);
  const rainWeight = rainRegion ? new Float32Array(dsCount) : null;
  for (let dr = 0; dr < dsRows; dr++) {
    const r0 = dr * stride;
    const r1 = Math.min(r0 + stride, rows);
    for (let dc = 0; dc < dsCols; dc++) {
      const c0 = dc * stride;
      const c1 = Math.min(c0 + stride, cols);
      let mn = Infinity;
      let wSum = 0;
      let wCount = 0;
      for (let r = r0; r < r1; r++) {
        const rowBase = r * cols;
        for (let c = c0; c < c1; c++) {
          const e = elev[rowBase + c];
          if (e < mn) mn = e;
          if (rainRegion) {
            wSum += rainRegion.mask[rowBase + c];
            wCount++;
          }
        }
      }
      const di = dr * dsCols + dc;
      dsElev[di] = mn;
      if (rainWeight) rainWeight[di] = wCount > 0 ? wSum / wCount : 0;
    }
  }

  // --- 湖の水位置換 ---
  const lakeIdx = new Int32Array(dsCount).fill(-1);
  for (let li = 0; li < lakes.length; li++) {
    const lake = lakes[li];
    if (!lake?.polygon || lake.polygon.type !== "Polygon") continue;
    const ring = (lake.polygon.coordinates as number[][][])[0];
    if (!Array.isArray(ring) || ring.length < 3) continue;
    let minX = Infinity;
    let maxX = -Infinity;
    let minZ = Infinity;
    let maxZ = -Infinity;
    for (const p of ring) {
      if (p[0] < minX) minX = p[0];
      if (p[0] > maxX) maxX = p[0];
      if (p[1] < minZ) minZ = p[1];
      if (p[1] > maxZ) maxZ = p[1];
    }
    const dsSpacing = stride * spacing;
    const cMin = Math.max(0, Math.floor((minX + halfW) / dsSpacing));
    const cMax = Math.min(dsCols - 1, Math.ceil((maxX + halfW) / dsSpacing));
    const rMin = Math.max(0, Math.floor((minZ + halfH) / dsSpacing));
    const rMax = Math.min(dsRows - 1, Math.ceil((maxZ + halfH) / dsSpacing));
    for (let dr = rMin; dr <= rMax; dr++) {
      for (let dc = cMin; dc <= cMax; dc++) {
        if (!pointInRing(cellToWorldX(dc), cellToWorldZ(dr), ring)) continue;
        const di = dr * dsCols + dc;
        lakeIdx[di] = li;
        dsElev[di] = lake.water_level;
      }
    }
  }

  // --- priority-flood 窪地埋め(Barnes 2014 の ε 付き) ---
  // シード: 海(標高0以下)の全セル + マップ外周セル(マップ端からの流出を許す)。
  // pop した最低セルから近傍へ filled = max(元標高, filled[cur] + ε) を伝播する。
  const filled = new Float64Array(dsCount);
  const visited = new Uint8Array(dsCount);
  const popOrder = new Int32Array(dsCount);
  let popCount = 0;
  const heap = new MinHeap(dsCount);

  const pushSeed = (di: number) => {
    if (visited[di]) return;
    visited[di] = 1;
    filled[di] = dsElev[di];
    heap.push(di, filled[di]);
  };
  for (let di = 0; di < dsCount; di++) {
    if (dsElev[di] <= 0) pushSeed(di);
  }
  for (let dc = 0; dc < dsCols; dc++) {
    pushSeed(dc);
    pushSeed((dsRows - 1) * dsCols + dc);
  }
  for (let dr = 0; dr < dsRows; dr++) {
    pushSeed(dr * dsCols);
    pushSeed(dr * dsCols + dsCols - 1);
  }

  while (heap.size > 0) {
    const cur = heap.pop();
    popOrder[popCount++] = cur;
    const r = Math.floor(cur / dsCols);
    const c = cur - r * dsCols;
    for (const o of D8_OFFSETS) {
      const nr = r + o.dr;
      const nc = c + o.dc;
      if (nr < 0 || nr >= dsRows || nc < 0 || nc >= dsCols) continue;
      const ni = nr * dsCols + nc;
      if (visited[ni]) continue;
      visited[ni] = 1;
      filled[ni] = Math.max(dsElev[ni], filled[cur] + RIVER.fillEpsilon);
      heap.push(ni, filled[ni]);
    }
  }

  // --- D8 流向(埋め立て後標高の最急降下) ---
  // 埋め立て後の標高には局所最小が存在しない(シード以外は必ずより低い近傍を持つ)ため、
  // どのセルからでも海・湖・マップ端まで下れる。
  const flowDir = new Int8Array(dsCount).fill(-1);
  for (let r = 0; r < dsRows; r++) {
    for (let c = 0; c < dsCols; c++) {
      const di = r * dsCols + c;
      let best = -1;
      let bestSlope = 0;
      for (let oi = 0; oi < D8_OFFSETS.length; oi++) {
        const o = D8_OFFSETS[oi];
        const nr = r + o.dr;
        const nc = c + o.dc;
        if (nr < 0 || nr >= dsRows || nc < 0 || nc >= dsCols) continue;
        const ni = nr * dsCols + nc;
        const slope = (filled[di] - filled[ni]) / o.dist;
        if (slope > bestSlope) {
          bestSlope = slope;
          best = oi;
        }
      }
      flowDir[di] = best;
    }
  }

  // --- 流量集積(flow accumulation) ---
  // popOrder は埋め立て後標高の昇順なので、逆順(高い方から)に走査すれば、
  // 各セルを処理する時点で上流の寄与が全て積算済みになる(ソート不要)。
  const acc = new Float64Array(dsCount);
  if (rainWeight) {
    acc.set(rainWeight);
  } else {
    acc.fill(1);
  }
  for (let k = popCount - 1; k >= 0; k--) {
    const di = popOrder[k];
    const d = flowDir[di];
    if (d < 0) continue;
    const o = D8_OFFSETS[d];
    const r = Math.floor(di / dsCols);
    const c = di - r * dsCols;
    acc[(r + o.dr) * dsCols + (c + o.dc)] += acc[di];
  }

  // 積算セル数 → 流量(m³/s)。雨量 × 集水面積 × 流出係数 を年間秒数で割った年平均流量に、
  // 豊水期換算係数(bankfullFactor)を掛けた「河道形成流量」を使う。
  const cellArea = (stride * spacing) * (stride * spacing);
  const qFactor =
    (cellArea * params.rainfallMmPerYear * 1e-3 * RIVER.runoffCoef * RIVER.bankfullFactor) / SECONDS_PER_YEAR;
  const discharge = new Float64Array(dsCount);
  for (let di = 0; di < dsCount; di++) discharge[di] = acc[di] * qFactor;

  return { dsRows, dsCols, stride, dsElev, lakeIdx, filled, flowDir, discharge, cellToWorldX, cellToWorldZ };
}

/** 流量から川幅(m)を求める(hydraulic geometry: w ∝ Q^0.5) */
function widthFromDischarge(q: number, spacing: number, params: RiverGenParams): number {
  return Math.max(params.widthCoef * Math.sqrt(Math.max(0, q)), minRiverWidth(spacing));
}

/** 川幅の下限(m)。1格子より細い川は彫り込みで表現できないため、指定した幅はこの値へ切り上げられる */
export function minRiverWidth(spacing: number): number {
  return RIVER.minWidthFactor * spacing;
}

/** 流量から水深(m)を求める(hydraulic geometry: d ∝ Q^0.4) */
function depthFromDischarge(q: number, depthCoef: number): number {
  return Math.max(depthCoef * Math.pow(Math.max(0, q), 0.4), RIVER.minDepth);
}

/**
 * 川床標高(bedY)を、確定済みの水面(waterY)・流量(q)・曲率から指定の深さ係数で計算し直す。
 * evolveRivers 内の計算と同じ式。彫り込み直前に「川ごとの深さ係数」を反映するために使う
 * (一覧UIで川1本ずつ深さを調整してから個別に彫り込むフロー)。
 */
export function applyRiverDepth(river: GeneratedRiver, depthCoef: number): void {
  for (const p of river.points) {
    const d = depthFromDischarge(p.q, depthCoef);
    p.bedY = p.waterY - d * (1 + RIVER.poolDeepen * Math.abs(p.curvature));
  }
}

/**
 * 中心線ポリラインを等間隔 ds でリサンプルする(x, z, q, width を線形補間)。
 * waterY / bedY / curvature / braided はこの後の工程で計算し直すため引き継がない。
 */
function resampleCenterline(points: RiverPoint[], ds: number): RiverPoint[] {
  if (points.length < 2) return points;
  const out: RiverPoint[] = [];
  let acc = 0;
  out.push({ ...points[0] });
  for (let i = 1; i < points.length; i++) {
    const prev = points[i - 1];
    const cur = points[i];
    const segLen = Math.hypot(cur.x - prev.x, cur.z - prev.z);
    if (segLen <= 1e-9) continue;
    let remain = ds - acc;
    while (remain <= segLen) {
      const t = remain / segLen;
      out.push({
        x: prev.x + (cur.x - prev.x) * t,
        z: prev.z + (cur.z - prev.z) * t,
        q: prev.q + (cur.q - prev.q) * t,
        width: prev.width + (cur.width - prev.width) * t,
        waterY: 0,
        bedY: 0,
        curvature: 0,
        braided: false,
      });
      remain += ds;
    }
    acc = segLen - (remain - ds);
  }
  // 終端は必ず含める(河口の位置がリサンプルでずれないように)
  const last = points[points.length - 1];
  const tail = out[out.length - 1];
  if (Math.hypot(last.x - tail.x, last.z - tail.z) > ds * 0.25) {
    out.push({ ...last });
  } else {
    out[out.length - 1] = { ...last };
  }
  return out;
}

/**
 * 中心線の位置を Laplacian 平滑化でなじませる(端点は固定)。
 * weight は隣接点の重み(カーネルは [w, 1-2w, w])。蛇行進化の反復中は、平滑化の減衰が
 * 蛇行の成長を打ち消してしまわないよう軽いカーネル(小さいweight)を使う。
 */
function smoothCenterline(points: RiverPoint[], passes: number, weight: number = 0.25): void {
  for (let pass = 0; pass < passes; pass++) {
    let prevX = points[0].x;
    let prevZ = points[0].z;
    for (let i = 1; i < points.length - 1; i++) {
      const cur = points[i];
      const next = points[i + 1];
      const nx = weight * prevX + (1 - 2 * weight) * cur.x + weight * next.x;
      const nz = weight * prevZ + (1 - 2 * weight) * cur.z + weight * next.z;
      prevX = cur.x;
      prevZ = cur.z;
      cur.x = nx;
      cur.z = nz;
    }
  }
}

/**
 * 地形から川ネットワークを自動抽出する(標高は変更しない)。
 *
 * 雨量(params.rainfallMmPerYear)と降雨範囲(rainRegion、選択マスク)から流量集積を計算し、
 * 流量が params.minDischargeM3s を超えるセル列を川とする。合流点では流量が最大の枝が
 * 本流として通過し、他の枝は支流(mouth="confluence")として終端する。
 *
 * 戻り値の rivers[].points は上流→下流の順で、x/z/q/width のみ確定している。
 * waterY/bedY/curvature は evolveRivers で計算する。id は `${idPrefix}_${連番}`。
 */
export function extractRiverNetwork(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  params: RiverGenParams,
  lakes: WaterSurfacePayload[],
  rainRegion?: ErosionRegion,
  idPrefix: string = "river",
): RiverExtractionResult {
  const g = buildHydroGrid(elev, rows, cols, spacing, mapWidth, mapHeight, params, lakes, rainRegion);
  const { dsRows, dsCols, dsElev, lakeIdx, flowDir, discharge } = g;
  const dsCount = dsRows * dsCols;

  // --- 川セルの判定(流量が閾値以上の陸セル。湖の中は川にしない) ---
  // あわせて陸上の最大流量を集計する(流路0本のときの診断値)。
  const isRiver = new Uint8Array(dsCount);
  let maxDischargeM3s = 0;
  for (let di = 0; di < dsCount; di++) {
    if (dsElev[di] <= 0 || lakeIdx[di] >= 0) continue;
    if (discharge[di] > maxDischargeM3s) maxDischargeM3s = discharge[di];
    if (discharge[di] >= params.minDischargeM3s) isRiver[di] = 1;
  }

  // --- 各川セルの「支配上流」(流入する川セルのうち流量最大のもの)と流入数 ---
  // 合流点で本流として通過するのは支配上流の枝。支流はそこで終端する。
  const domUp = new Int32Array(dsCount).fill(-1);
  const inflow = new Uint8Array(dsCount);
  for (let r = 0; r < dsRows; r++) {
    for (let c = 0; c < dsCols; c++) {
      const di = r * dsCols + c;
      if (!isRiver[di]) continue;
      const d = flowDir[di];
      if (d < 0) continue;
      const o = D8_OFFSETS[d];
      const ni = (r + o.dr) * dsCols + (c + o.dc);
      if (!isRiver[ni]) continue;
      inflow[ni] = Math.min(255, inflow[ni] + 1);
      if (domUp[ni] < 0 || discharge[di] > discharge[domUp[ni]]) domUp[ni] = di;
    }
  }

  // --- 源頭(川セルのうち川の流入が無いセル)から下流へ walk して1本ずつ抽出する ---
  // 各川セルは「支配上流チェーン」でちょうど1本の川に属する。
  const cellRiver = new Int32Array(dsCount).fill(-1);
  type RawRiver = {
    cells: number[];
    mouth: RiverMouthKind;
    mouthWaterLevel?: number;
    confluenceCell?: number;
  };
  const rawRivers: RawRiver[] = [];

  for (let src = 0; src < dsCount; src++) {
    if (!isRiver[src] || inflow[src] > 0) continue;
    const cells: number[] = [src];
    let mouth: RiverMouthKind = "edge";
    let mouthWaterLevel: number | undefined;
    let confluenceCell: number | undefined;
    let cur = src;
    for (;;) {
      const d = flowDir[cur];
      if (d < 0) {
        mouth = "edge";
        break;
      }
      const o = D8_OFFSETS[d];
      const r = Math.floor(cur / dsCols);
      const next = (r + o.dr) * dsCols + (cur - r * dsCols + o.dc);
      if (lakeIdx[next] >= 0) {
        // 湖へ流入して終端。湖内の区間は作らない(溢出点から先は別の川として抽出される)。
        mouth = "lake";
        mouthWaterLevel = lakes[lakeIdx[next]]?.water_level ?? 0;
        cells.push(next);
        break;
      }
      if (dsElev[next] <= 0) {
        // 海へ到達。河口を明確にするため海側へ1セルだけ延長して終端する。
        mouth = "sea";
        cells.push(next);
        break;
      }
      if (!isRiver[next]) {
        // 閾値ちょうどの境界などで川セルが途切れた場合(通常は起きない)。そこで終端する。
        mouth = "edge";
        break;
      }
      if (domUp[next] !== cur) {
        // 合流点: next の支配上流は別の枝(=本流)。この川は支流としてここで終端する。
        mouth = "confluence";
        confluenceCell = next;
        break;
      }
      cells.push(next);
      cellRiver[next] = rawRivers.length;
      cur = next;
    }
    cellRiver[src] = rawRivers.length;
    rawRivers.push({ cells, mouth, mouthWaterLevel, confluenceCell });
  }

  // --- セル列 → ワールド座標ポリライン → リサンプル+平滑化 ---
  const ds = RIVER.resampleFactor * spacing;
  const rivers: GeneratedRiver[] = [];
  const rawIndexToRiverIndex = new Int32Array(rawRivers.length).fill(-1);
  let discardedShortCount = 0;

  for (let ri = 0; ri < rawRivers.length; ri++) {
    const raw = rawRivers[ri];
    const pts: RiverPoint[] = raw.cells.map((di) => {
      const r = Math.floor(di / dsCols);
      const c = di - r * dsCols;
      const q = discharge[di];
      return {
        x: g.cellToWorldX(c),
        z: g.cellToWorldZ(r),
        q,
        width: widthFromDischarge(q, spacing, params),
        waterY: 0,
        bedY: 0,
        curvature: 0,
        braided: false,
      };
    });
    const resampled = resampleCenterline(pts, ds);
    if (resampled.length < RIVER.minRiverPoints) {
      discardedShortCount++;
      continue;
    }
    smoothCenterline(resampled, 2);
    rawIndexToRiverIndex[ri] = rivers.length;
    rivers.push({
      id: `${idPrefix}_${rivers.length}`,
      points: resampled,
      mouth: raw.mouth,
      mouthWaterLevel: raw.mouthWaterLevel,
    });
  }

  // --- 合流先の解決(短すぎて破棄された川に合流していた場合は edge 扱いに落とす) ---
  for (let ri = 0; ri < rawRivers.length; ri++) {
    const riverIdx = rawIndexToRiverIndex[ri];
    if (riverIdx < 0) continue;
    const raw = rawRivers[ri];
    if (raw.mouth !== "confluence" || raw.confluenceCell === undefined) continue;
    const targetRaw = cellRiver[raw.confluenceCell];
    const targetIdx = targetRaw >= 0 ? rawIndexToRiverIndex[targetRaw] : -1;
    if (targetIdx < 0) {
      rivers[riverIdx].mouth = "edge";
      continue;
    }
    const target = rivers[targetIdx];
    rivers[riverIdx].confluence = {
      riverId: target.id,
      pointIndex: nearestPointIndex(target.points, rivers[riverIdx].points[rivers[riverIdx].points.length - 1]),
    };
  }

  const merged = mergeParallelChannels(rivers);
  return { rivers: merged, maxDischargeM3s, discardedShortCount };
}

/**
 * ほぼ平行に並走する重複チャネルを統合する。
 *
 * D8流向は8方向にしか流せないため、谷底が平坦に近い場所では「下流方向の勾配が
 * 隣の列へ渡る微小な横勾配に常に勝つ」状態になり、1〜2セル間隔の平行な川を
 * 複数本抽出してしまうことがある(左右対称の谷や平坦な氾濫原で顕著)。物理的には
 * 川幅以内まで接近した流路は1本に合流するため、中心線同士が川幅程度以内で
 * 並走している川(サンプル点の6割以上が近接)は、河口流量の大きい方だけを残す。
 * 削除した川を合流先として参照していた支流は残した方へ付け替える。
 * 注: 削除した側の集水は残した側の流量へ加算しない(幅が若干過小になるが、
 * 同じ谷が二重に彫られる問題の解消を優先する)。
 */
function mergeParallelChannels(rivers: GeneratedRiver[]): GeneratedRiver[] {
  const n = rivers.length;
  if (n < 2) return rivers;

  // 外接矩形(高速な事前フィルタ用)。中心線は幅0の線なので、直線の川同士だと
  // 矩形が重ならない。近接判定の閾値(川幅程度)ぶんパディングしておく。
  const bboxes = rivers.map((river) => {
    let minX = Infinity;
    let maxX = -Infinity;
    let minZ = Infinity;
    let maxZ = -Infinity;
    let maxW = 0;
    for (const p of river.points) {
      if (p.x < minX) minX = p.x;
      if (p.x > maxX) maxX = p.x;
      if (p.z < minZ) minZ = p.z;
      if (p.z > maxZ) maxZ = p.z;
      if (p.width > maxW) maxW = p.width;
    }
    return { minX: minX - maxW, maxX: maxX + maxW, minZ: minZ - maxW, maxZ: maxZ + maxW };
  });

  const removedTo = new Map<string, string>(); // 削除した川ID → 統合先の川ID
  const removed = new Set<number>();

  for (let i = 0; i < n; i++) {
    if (removed.has(i)) continue;
    for (let j = i + 1; j < n; j++) {
      if (removed.has(j)) continue;
      // 平行重複は長さも範囲もほぼ同じ川として現れる。明らかに異なる形状のペアは飛ばす
      // (支流網の総当たりで距離計算が爆発しないようにするための事前フィルタ)。
      const li = rivers[i].points.length;
      const lj = rivers[j].points.length;
      if (Math.min(li, lj) / Math.max(li, lj) < 0.7) continue;
      const a = bboxes[i];
      const b = bboxes[j];
      if (a.minX > b.maxX || b.minX > a.maxX || a.minZ > b.maxZ || b.minZ > a.maxZ) continue;

      // 小さい方の川のサンプル点が、相手の中心線から川幅程度以内にある割合を数える
      const [smallIdx, bigIdx] = li <= lj ? [i, j] : [j, i];
      const small = rivers[smallIdx];
      const big = rivers[bigIdx];
      let near = 0;
      let checked = 0;
      for (let k = 0; k < small.points.length; k += 2) {
        const p = small.points[k];
        const q = big.points[nearestPointIndex(big.points, p)];
        checked++;
        if (Math.hypot(p.x - q.x, p.z - q.z) < (p.width + q.width) * 0.75) near++;
      }
      if (checked === 0 || near / checked <= 0.6) continue;

      // 河口流量の大きい方を残す
      const qi = rivers[i].points[rivers[i].points.length - 1].q;
      const qj = rivers[j].points[rivers[j].points.length - 1].q;
      const dropIdx = qi >= qj ? j : i;
      const keepIdx = qi >= qj ? i : j;
      removed.add(dropIdx);
      removedTo.set(rivers[dropIdx].id, rivers[keepIdx].id);
      if (dropIdx === i) break; // i を消したら i のループを抜ける
    }
  }

  if (removed.size === 0) return rivers;

  const kept = rivers.filter((_, idx) => !removed.has(idx));
  const keptById = new Map(kept.map((river) => [river.id, river]));
  // 削除した川へ合流していた支流を統合先へ付け替える(統合先も削除されている場合を考慮して1段たどる)
  for (const river of kept) {
    if (river.mouth !== "confluence" || !river.confluence) continue;
    let targetId = river.confluence.riverId;
    while (removedTo.has(targetId)) targetId = removedTo.get(targetId)!;
    const target = keptById.get(targetId);
    if (!target) {
      river.mouth = "edge";
      river.confluence = undefined;
      continue;
    }
    river.confluence = {
      riverId: targetId,
      pointIndex: nearestPointIndex(target.points, river.points[river.points.length - 1]),
    };
  }
  return kept;
}

/** polyline 上で pt に最も近い点のインデックスを返す */
export function nearestPointIndex(polyline: RiverPoint[], pt: { x: number; z: number }): number {
  let best = 0;
  let bestDist = Infinity;
  for (let i = 0; i < polyline.length; i++) {
    const d = (polyline[i].x - pt.x) ** 2 + (polyline[i].z - pt.z) ** 2;
    if (d < bestDist) {
      bestDist = d;
      best = i;
    }
  }
  return best;
}

/* ============================================================================
 * [2] 蛇行進化 + 水面標高プロファイル(evolveRivers)
 * ========================================================================== */

/**
 * 符号付きの平滑曲率 K[i](1/m)を計算する。
 *
 * 局所曲率は Menger 曲率(3点の外接円の曲率)で求める。Howard-Knutson モデルの
 * 「局所曲率 + 上流曲率の重み付き積分」を、局所項と上流→下流への指数移動平均(IIR)の
 * 混合で近似する。上流項(EMA)が蛇行の頂点を下流側へ位相遅れさせて進行させ、
 * 局所項が蛇行振幅の成長を駆動する(EMA単独だと小さな揺らぎが強く減衰されて
 * 蛇行が成長できない)。正の値は左カーブ(進行方向に対して反時計回り)。
 */
function computeSmoothedCurvature(points: RiverPoint[], ds: number): Float64Array {
  const n = points.length;
  const K = new Float64Array(n);
  let ema = 0;
  for (let i = 1; i < n - 1; i++) {
    const ax = points[i].x - points[i - 1].x;
    const az = points[i].z - points[i - 1].z;
    const bx = points[i + 1].x - points[i].x;
    const bz = points[i + 1].z - points[i].z;
    const cross = ax * bz - az * bx;
    const la = Math.hypot(ax, az);
    const lb = Math.hypot(bx, bz);
    const lc = Math.hypot(ax + bx, az + bz);
    const k = la > 1e-9 && lb > 1e-9 && lc > 1e-9 ? (2 * cross) / (la * lb * lc) : 0;
    // 減衰距離 λ = curvatureLagWidths × 川幅。幅が広いほど遠くの上流曲率まで効く。
    const lambda = RIVER.curvatureLagWidths * Math.max(points[i].width, 1);
    const alpha = Math.min(1, ds / lambda);
    ema = alpha * k + (1 - alpha) * ema;
    K[i] = RIVER.curvatureLocalMix * k + (1 - RIVER.curvatureLocalMix) * ema;
  }
  return K;
}

/**
 * ネックカットオフ: 蛇行が発達しすぎて首(ネック)が細くなったループを切除する。
 *
 * 空間ハッシュで「パス距離は遠いのにユークリッド距離が近い」点対を探し、見つかったら
 * その間の点を取り除いて短絡させる(実河川の蛇行切断に相当)。切除されたループを
 * 三日月湖として残す処理はスコープ外(D_地形形状.md §D.11 参照)。
 * 1回の呼び出しで最初に見つかった1箇所だけを切る(イテレーションが十分多いため)。
 */
function neckCutoff(points: RiverPoint[], ds: number): RiverPoint[] {
  const n = points.length;
  if (n < 8) return points;
  let maxWidth = 0;
  for (const p of points) if (p.width > maxWidth) maxWidth = p.width;
  const cellSize = Math.max(RIVER.neckCutWidths * maxWidth, ds);
  const buckets = new Map<number, number[]>();
  const keyOf = (x: number, z: number) => {
    const gx = Math.floor(x / cellSize);
    const gz = Math.floor(z / cellSize);
    return gx * 73856093 + gz * 19349663;
  };
  for (let i = 0; i < n; i++) {
    const key = keyOf(points[i].x, points[i].z);
    const list = buckets.get(key);
    if (list) list.push(i);
    else buckets.set(key, [i]);
  }
  for (let i = 0; i < n; i++) {
    const p = points[i];
    const gx = Math.floor(p.x / cellSize);
    const gz = Math.floor(p.z / cellSize);
    for (let ox = -1; ox <= 1; ox++) {
      for (let oz = -1; oz <= 1; oz++) {
        const list = buckets.get((gx + ox) * 73856093 + (gz + oz) * 19349663);
        if (!list) continue;
        for (const j of list) {
          if (j <= i) continue;
          const w = Math.max(p.width, points[j].width);
          // パス距離が近い点対は単なる隣接点なので除外する
          if ((j - i) * ds <= RIVER.neckPathWidths * w) continue;
          const dist = Math.hypot(points[j].x - p.x, points[j].z - p.z);
          if (dist < RIVER.neckCutWidths * w) {
            // i と j の間のループを切除して短絡する
            return points.slice(0, i + 1).concat(points.slice(j));
          }
        }
      }
    }
  }
  return points;
}

/**
 * 蛇行進化シミュレーション(Howard-Knutson 系の簡易版)と水面標高プロファイルの確定。
 * rivers の points を in-place で更新する。elev(地形)は変更しない。
 *
 * 各イテレーションで、中心線の各点を法線方向へ「平滑曲率 × 川幅」に比例した量だけ
 * 横移動させる(蛇行はカーブの外側へ成長する)。移動量は以下で減衰させる:
 *   - 平地判定 D_slope: 水面縦断勾配が急な区間(山地の急流)は蛇行しない
 *   - 谷閉じ込め D_confine: 両岸の地形が高い峡谷では横移動できない
 * イテレーション後に等間隔リサンプル+平滑化+ネックカットオフを行い、支流の河口は
 * 本流中心線上の最近点へ再スナップする(本流が動いても合流が切れないように)。
 *
 * 最後に水面標高プロファイルを計算する(詳細は computeSurfaceProfile)。
 */
export function evolveRivers(
  rivers: GeneratedRiver[],
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  params: RiverGenParams,
): void {
  const ds = RIVER.resampleFactor * spacing;
  const sample = (wx: number, wz: number) => sampleElevAtWorld(elev, rows, cols, spacing, mapWidth, mapHeight, wx, wz);
  const riverById = new Map<string, GeneratedRiver>();
  for (const r of rivers) riverById.set(r.id, r);

  const iterations = params.meanderStrength > 0 ? Math.max(0, Math.round(params.meanderIterations)) : 0;
  const migration = params.meanderStrength * RIVER.migrationCoef;

  // --- 初期擾乱の付与 ---
  // 曲率駆動の蛇行進化は完全な直線(曲率0)からは成長できない(変位が常に0のまま)ため、
  // 実河川の河床の不整に相当する小さな横方向の揺らぎを種として与える。揺らぎは決定論的な
  // バリューノイズ(川ごとに位相を変える)で、勾配・峡谷の減衰は本編の横移動と同じく適用する。
  if (iterations > 0) {
    for (let ri = 0; ri < rivers.length; ri++) {
      const pts = rivers[ri].points;
      const n = pts.length;
      if (n < 4) continue;
      let arc = 0;
      for (let i = 2; i < n - 2; i++) {
        const p = pts[i];
        arc += Math.hypot(p.x - pts[i - 1].x, p.z - pts[i - 1].z);
        let tx = pts[i + 1].x - pts[i - 1].x;
        let tz = pts[i + 1].z - pts[i - 1].z;
        const tl = Math.hypot(tx, tz);
        if (tl < 1e-9) continue;
        tx /= tl;
        tz /= tl;
        const hUp = sample(pts[i - 1].x, pts[i - 1].z);
        const hDown = sample(pts[i + 1].x, pts[i + 1].z);
        const slope = Math.max(0, (hUp - hDown) / (2 * ds));
        const dSlope = Math.max(0, 1 - slope / RIVER.maxMeanderSlope);
        if (dSlope <= 0) continue;
        const wavelength = RIVER.seedWavelengthWidths * p.width;
        const amp = RIVER.seedAmpWidths * p.width * params.meanderStrength * dSlope;
        const offset = amp * valueNoise2D(arc / wavelength, ri * 7.13 + 0.5);
        p.x += -tz * offset;
        p.z += tx * offset;
      }
      smoothCenterline(pts, 1);
    }
  }

  for (let iter = 0; iter < iterations; iter++) {
    for (const river of rivers) {
      const pts = river.points;
      const n = pts.length;
      if (n < 4) continue;

      const K = computeSmoothedCurvature(pts, ds);

      // 各点の変位を先に全て計算してから適用する(計算中の配列を自分で汚さないため)
      const dxArr = new Float64Array(n);
      const dzArr = new Float64Array(n);
      for (let i = 2; i < n - 2; i++) {
        const p = pts[i];
        const w = p.width;
        const d = depthFromDischarge(p.q, params.depthCoef);

        // 接線と左法線(左法線 = 接線を+90°回転: (tx,tz) → (-tz,tx))
        let tx = pts[i + 1].x - pts[i - 1].x;
        let tz = pts[i + 1].z - pts[i - 1].z;
        const tl = Math.hypot(tx, tz);
        if (tl < 1e-9) continue;
        tx /= tl;
        tz /= tl;
        const nx = -tz;
        const nz = tx;

        // 平地判定: 地形の縦断勾配(まだ水面が無いので地形サンプルで代用)
        const hUp = sample(pts[i - 1].x, pts[i - 1].z);
        const hDown = sample(pts[i + 1].x, pts[i + 1].z);
        const slope = Math.max(0, (hUp - hDown) / (2 * ds));
        const dSlope = Math.max(0, 1 - slope / RIVER.maxMeanderSlope);
        if (dSlope <= 0) continue;

        // 谷閉じ込め: 中心線の左右 2w の地点が水面想定(現地形)より 3d 以上高ければ移動を減衰
        const hC = sample(p.x, p.z);
        const hL = sample(p.x + nx * 2 * w, p.z + nz * 2 * w);
        const hR = sample(p.x - nx * 2 * w, p.z - nz * 2 * w);
        const excess = Math.max(hL, hR) - (hC + 3 * d);
        const dConfine = excess <= 0 ? 1 : Math.max(0, 1 - excess / (3 * d));
        if (dConfine <= 0) continue;

        // ベンドの成長飽和: 曲率半径が saturationRadiusWidths×川幅 まで小さくなった
        // (=十分発達した)ベンドは成長を止める。これが無いと蛇行が無限に成長して暴走する。
        const saturation = Math.max(0, 1 - Math.abs(K[i]) * w * RIVER.saturationRadiusWidths);
        if (saturation <= 0) continue;

        // 横移動: 蛇行はカーブの外側(曲率中心の反対側)へ成長する。
        // K>0(左カーブ)のとき曲率中心は左法線側にあるため、変位は -N 方向。
        let delta = migration * (K[i] * w) * w * dSlope * dConfine * saturation;
        const maxStep = RIVER.maxStepFactor * ds;
        if (delta > maxStep) delta = maxStep;
        else if (delta < -maxStep) delta = -maxStep;
        dxArr[i] = -delta * nx;
        dzArr[i] = -delta * nz;
      }
      for (let i = 2; i < n - 2; i++) {
        pts[i].x += dxArr[i];
        pts[i].z += dzArr[i];
      }

      // 形を整える: ネックカットオフ(切れなくなるまで) → 等間隔リサンプル → 平滑化。
      // 平滑化はごく軽いカーネルにする(重いと蛇行の成長より減衰が勝って直線に戻ってしまう)。
      let newPts = pts;
      for (let cut = 0; cut < 20; cut++) {
        const after = neckCutoff(newPts, ds);
        if (after.length === newPts.length) break;
        newPts = after;
      }
      newPts = resampleCenterline(newPts, ds);
      smoothCenterline(newPts, 1, 0.08);
      if (newPts.length >= 4) river.points = newPts;
    }

    // 支流の河口を本流中心線上の最近点へ再スナップする
    for (const river of rivers) {
      if (river.mouth !== "confluence" || !river.confluence) continue;
      const target = riverById.get(river.confluence.riverId);
      if (!target) continue;
      const tail = river.points[river.points.length - 1];
      const idx = nearestPointIndex(target.points, tail);
      river.confluence.pointIndex = idx;
      tail.x = target.points[idx].x;
      tail.z = target.points[idx].z;
    }
  }

  computeSurfaceProfiles(rivers, elev, rows, cols, spacing, mapWidth, mapHeight, params, ds);
}

/**
 * 全ての川の水面標高プロファイル(waterY)・川床標高(bedY)・断面用曲率・網状化フラグを確定する。
 *
 * 水面は「地形の平滑化サンプル」を上限とし、下流方向の min スキャンで単調非増加を保証する。
 * さらに最小勾配 ε_s を課して完全な平坦を避ける(Unity の水面リボンが確実に「流れ下る」見た目に
 * なるように)。河口は種類に応じてクランプする:
 *   - sea: 0m(海面)
 *   - lake: 湖の水位
 *   - confluence: 合流点の本流の水面(支流の水面が本流より低くならないよう本流を先に処理する)
 * クランプで水位が下がった分は、河口から上流へ最大勾配 maxMouthSlope でなじませる(逆向きスキャン)。
 */
function computeSurfaceProfiles(
  rivers: GeneratedRiver[],
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  params: RiverGenParams,
  ds: number,
): void {
  const sample = (wx: number, wz: number) => sampleElevAtWorld(elev, rows, cols, spacing, mapWidth, mapHeight, wx, wz);
  const riverById = new Map<string, GeneratedRiver>();
  for (const r of rivers) riverById.set(r.id, r);

  // 支流は合流先の水面が確定してから処理する(トポロジカル順)。
  // 循環参照は構造上起きないが、防御的に反復上限を設ける。
  const processed = new Set<string>();
  let pending = rivers.slice();
  for (let round = 0; round < 100 && pending.length > 0; round++) {
    const next: GeneratedRiver[] = [];
    for (const river of pending) {
      if (river.mouth === "confluence" && river.confluence && !processed.has(river.confluence.riverId)) {
        // 合流先が未処理なら後回しにする(合流先が消えている場合は即処理)
        if (riverById.has(river.confluence.riverId)) {
          next.push(river);
          continue;
        }
      }
      computeOneSurfaceProfile(river, riverById, sample, params, ds);
      processed.add(river.id);
    }
    if (next.length === pending.length) {
      // 依存が解決できない(合流先が循環している等の異常系)。残りを強制処理する。
      for (const river of next) {
        computeOneSurfaceProfile(river, riverById, sample, params, ds);
        processed.add(river.id);
      }
      pending = [];
    } else {
      pending = next;
    }
  }
}

/** 1本の川の waterY / bedY / curvature / braided を確定する(computeSurfaceProfiles の本体) */
function computeOneSurfaceProfile(
  river: GeneratedRiver,
  riverById: Map<string, GeneratedRiver>,
  sample: (wx: number, wz: number) => number,
  params: RiverGenParams,
  ds: number,
): void {
  const pts = river.points;
  const n = pts.length;
  if (n === 0) return;

  // --- 地形サンプル: 河道横断方向(±川幅/2)の最低点を各点の「谷底標高」とする ---
  // 中心線上の1点だけをサンプルすると、蛇行や平滑化で流路が谷底から少し外れた区間で
  // 水面が谷壁の高さに「載って」しまい、running-max がそれを上流全体へ伝播して
  // 水面が地形から浮く=彫り込みが一切発生しない区間ができる。横断方向の最低点に
  // 固定することで、流路のわずかなブレに関わらず水面が谷底に沿う。
  const terrain = new Float64Array(n);
  for (let i = 0; i < n; i++) {
    const p = pts[i];
    // 接線(端点は片側差分)から左法線を求める
    const prev = pts[Math.max(0, i - 1)];
    const next = pts[Math.min(n - 1, i + 1)];
    let tx = next.x - prev.x;
    let tz = next.z - prev.z;
    const tl = Math.hypot(tx, tz);
    if (tl < 1e-9) {
      tx = 1;
      tz = 0;
    } else {
      tx /= tl;
      tz /= tl;
    }
    const nx = -tz;
    const nz = tx;
    const halfW = p.width / 2;
    let mn = Infinity;
    for (let k = -2; k <= 2; k++) {
      const off = (k / 2) * halfW;
      const h = sample(p.x + nx * off, p.z + nz * off);
      if (h < mn) mn = h;
    }
    terrain[i] = mn;
  }
  const smoothed = new Float64Array(n);
  const HALF_WINDOW = 1;
  for (let i = 0; i < n; i++) {
    let sum = 0;
    let count = 0;
    for (let k = -HALF_WINDOW; k <= HALF_WINDOW; k++) {
      const j = i + k;
      if (j < 0 || j >= n) continue;
      sum += terrain[j];
      count++;
    }
    smoothed[i] = sum / count;
  }

  // --- 上流方向 running-max スキャン(1次元の窪地充填)+ 最小勾配 ---
  // 流路の途中に地形の盛り上がり(尾根・ノイズの瘤)がある場合、水は「上流側の低い水位の
  // まま瘤を貫通する」のではなく「瘤(溢出点)の高さまで溜まってから乗り越える」。
  // そのため水面は、各点から見た下流側の最大標高(=そこを流れ切るのに必要な水位)を
  // 上流方向の running-max で求める。こうすると窪地区間は溢出点の高さの平坦な水面
  // (湖のような区間。地形より水面が上に来るため彫り込みは発生しない)になり、
  // 瘤を横切る箇所の彫り込みは川の深さ分だけで済む。
  // ※以前は下流方向の min スキャンだったが、それだと瘤の高さぶんの巨大な峡谷を
  //   彫ってしまう(深さ係数と無関係に発生する)ため、この方式に修正した。
  const ws = new Float64Array(n);
  ws[n - 1] = smoothed[n - 1];
  for (let i = n - 2; i >= 0; i--) {
    ws[i] = Math.max(smoothed[i], ws[i + 1]);
  }
  // 単調非増加は running-max で保証済み。最小勾配だけを下流方向のパスで課す
  // (完全に平坦な水面を避け、Unityの水面リボンが確実に「流れ下る」見た目になるように)。
  for (let i = 1; i < n; i++) {
    ws[i] = Math.min(ws[i], ws[i - 1] - RIVER.minSurfaceSlope * ds);
  }

  // --- 河口クランプ + 逆向きのなじませスキャン ---
  let mouthLevel: number | null = null;
  if (river.mouth === "sea") mouthLevel = 0;
  else if (river.mouth === "lake") mouthLevel = river.mouthWaterLevel ?? 0;
  else if (river.mouth === "confluence" && river.confluence) {
    const target = riverById.get(river.confluence.riverId);
    if (target && target.points.length > 0) {
      const idx = nearestPointIndex(target.points, pts[n - 1]);
      river.confluence.pointIndex = idx;
      mouthLevel = target.points[idx].waterY;
    }
  }
  if (mouthLevel !== null && ws[n - 1] > mouthLevel) {
    // 河口の水位を mouthLevel まで下げ、その落差(drop)を河口付近の限定区間だけに
    // smoothstep で配分してなじませる。区間長は「追加される勾配が maxMouthSlope を
    // 超えない長さ」(川全体を上限)。川全体に勾配上限を課す方式だと、源流の水面が
    // 地形から大きく乖離して(例: 標高100mの源流の水面が河口+わずかになる)、
    // 彫り込みが不自然な大峡谷になってしまうため、なじませは河口側に限定する。
    const drop = ws[n - 1] - mouthLevel;
    const rampLen = Math.min(n - 1, Math.max(1, Math.ceil(drop / (RIVER.maxMouthSlope * ds))));
    for (let k = 0; k <= rampLen; k++) {
      const i = n - 1 - k;
      const t = 1 - k / rampLen; // 河口で1 → 上流側の区間端で0
      const sm = t * t * (3 - 2 * t);
      ws[i] -= drop * sm;
    }
  }

  // --- 断面用の無次元曲率(±maxNormCurvature にクランプ) ---
  const K = computeSmoothedCurvature(pts, ds);
  for (let i = 0; i < n; i++) {
    const kn = K[i] * pts[i].width * 2;
    pts[i].curvature = Math.max(-RIVER.maxNormCurvature, Math.min(RIVER.maxNormCurvature, kn));
  }

  // --- 網状化(braiding)区間の判定と拡幅 ---
  // 条件: 水面勾配が緩く(braidMaxSlope 未満)かつ川幅が大きい(braidMinWidth 以上)。
  // 短い飛び地はノイズなので、braidMinRun 点以上連続した区間だけを網状化する。
  if (params.enableBraiding) {
    const rawFlag = new Uint8Array(n);
    for (let i = 1; i < n - 1; i++) {
      const slope = Math.max(0, (ws[i - 1] - ws[i + 1]) / (2 * ds));
      if (slope < RIVER.braidMaxSlope && pts[i].width >= RIVER.braidMinWidth) rawFlag[i] = 1;
    }
    let runStart = -1;
    for (let i = 0; i <= n; i++) {
      const flag = i < n ? rawFlag[i] : 0;
      if (flag && runStart < 0) runStart = i;
      if (!flag && runStart >= 0) {
        if (i - runStart >= RIVER.braidMinRun) {
          for (let j = runStart; j < i; j++) {
            pts[j].braided = true;
            pts[j].width *= RIVER.braidWidenFactor;
          }
        }
        runStart = -1;
      }
    }
  }

  // --- 水面・川床の確定 ---
  // bedY はタルウェグ(最深部)の標高。カーブの淵は poolDeepen×|κ| の分だけ深くなる。
  for (let i = 0; i < n; i++) {
    const d = depthFromDischarge(pts[i].q, params.depthCoef);
    pts[i].waterY = ws[i];
    pts[i].bedY = ws[i] - d * (1 + RIVER.poolDeepen * Math.abs(pts[i].curvature));
  }
}

/* ============================================================================
 * [2b] 制御点による流路の手動作図・ノード編集
 *
 * 流路を「制御点(疎な頂点列)」でも表し、制御点を通る滑らかな曲線から等間隔の中心線
 * (points)を作り直せるようにする。手動で描いた川はクリックした頂点がそのまま制御点、
 * 自動生成した川は蛇行進化後の中心線を Douglas-Peucker で間引いたものを制御点とする。
 * ノードの移動・追加・削除のたびに buildRiverFromControlPoints で中心線・河口・水面
 * プロファイルを作り直す(標高は変更しない。彫り込みは従来どおり carveRivers)。
 * ========================================================================== */

/** 流路の制御点。q はこの地点の流量(m³/s)で、中心線へは制御点間の線形補間で引き継ぐ */
export type RiverControlPoint = { x: number; z: number; q: number };

/** buildRiverFromControlPoints の結果。河口を他の川へ吸着した場合は末尾の制御点も吸着後の位置になる */
export type RiverRebuildResult = { river: GeneratedRiver; controlPoints: RiverControlPoint[] };

/** 川幅(m)から流量(m³/s)を逆算する(widthFromDischarge の逆関数。幅の下限クランプは考慮しない) */
export function dischargeFromWidth(width: number, params: RiverGenParams): number {
  const coef = Math.max(1e-6, params.widthCoef);
  return (Math.max(0, width) / coef) ** 2;
}

/**
 * 中心線を Douglas-Peucker で間引いて制御点にする。許容誤差は spacing×controlPointToleranceFactor。
 * 合流点で流量が段階的に増える点は形状に関係なく残す(間引くと支流の流入位置の流量変化が
 * 制御点間の線形補間でなまり、合流点より上流の川幅まで太くなるため)。
 */
export function simplifyCenterlineToControlPoints(points: RiverPoint[], spacing: number): RiverControlPoint[] {
  const n = points.length;
  if (n === 0) return [];
  const toCp = (p: RiverPoint): RiverControlPoint => ({ x: p.x, z: p.z, q: p.q });
  if (n <= 2) return points.map(toCp);

  const keep = new Uint8Array(n);
  keep[0] = 1;
  keep[n - 1] = 1;
  for (let i = 1; i < n; i++) {
    if (points[i].q > points[i - 1].q * (1 + RIVER.controlPointQJump) + 1e-9) {
      keep[i - 1] = 1;
      keep[i] = 1;
    }
  }

  // 固定点で区切った区間ごとに反復版DP(再帰深さが点数に比例しないようにスタックで処理)
  const stack: [number, number][] = [];
  let segStart = 0;
  for (let i = 1; i < n; i++) {
    if (keep[i]) {
      stack.push([segStart, i]);
      segStart = i;
    }
  }
  const tol2 = (RIVER.controlPointToleranceFactor * spacing) ** 2;
  while (stack.length > 0) {
    const [a, b] = stack.pop()!;
    if (b - a < 2) continue;
    const ax = points[a].x;
    const az = points[a].z;
    const dx = points[b].x - ax;
    const dz = points[b].z - az;
    const len2 = dx * dx + dz * dz;
    let bestD = -1;
    let bestI = -1;
    for (let i = a + 1; i < b; i++) {
      const px = points[i].x - ax;
      const pz = points[i].z - az;
      let d2: number;
      if (len2 < 1e-12) {
        d2 = px * px + pz * pz;
      } else {
        const t = Math.max(0, Math.min(1, (px * dx + pz * dz) / len2));
        const ex = px - t * dx;
        const ez = pz - t * dz;
        d2 = ex * ex + ez * ez;
      }
      if (d2 > bestD) {
        bestD = d2;
        bestI = i;
      }
    }
    if (bestD > tol2) {
      keep[bestI] = 1;
      stack.push([a, bestI], [bestI, b]);
    }
  }
  const out: RiverControlPoint[] = [];
  for (let i = 0; i < n; i++) if (keep[i]) out.push(toCp(points[i]));
  return out;
}

/**
 * 制御点を通る centripetal Catmull-Rom 曲線を、おおむね step 間隔の折れ線へ展開する。
 * centripetal(α=0.5)は制御点の間隔が不均一でも尖点や自己交差の小ループができない性質があり、
 * 手描きの頂点列に向く。両端は隣の制御点を鏡映した仮想点で接線を決める。
 * q は制御点間で線形補間する。width 以降のフィールドは呼び出し側が計算し直す。
 */
function expandCatmullRom(cps: RiverControlPoint[], step: number): RiverPoint[] {
  const n = cps.length;
  const mk = (x: number, z: number, q: number): RiverPoint => ({ x, z, q, width: 0, waterY: 0, bedY: 0, curvature: 0, braided: false });
  if (n === 0) return [];
  if (n === 1) return [mk(cps[0].x, cps[0].z, cps[0].q)];
  const out: RiverPoint[] = [];
  const mirror = (a: RiverControlPoint, b: RiverControlPoint): RiverControlPoint => ({ x: 2 * a.x - b.x, z: 2 * a.z - b.z, q: a.q });
  const knot = (a: RiverControlPoint, b: RiverControlPoint) => Math.max(1e-6, Math.sqrt(Math.hypot(b.x - a.x, b.z - a.z)));
  for (let i = 0; i < n - 1; i++) {
    const p0 = i === 0 ? mirror(cps[0], cps[1]) : cps[i - 1];
    const p1 = cps[i];
    const p2 = cps[i + 1];
    const p3 = i + 2 < n ? cps[i + 2] : mirror(cps[n - 1], cps[n - 2]);
    const pieces = Math.max(1, Math.ceil(Math.hypot(p2.x - p1.x, p2.z - p1.z) / step));
    const t0 = 0;
    const t1 = t0 + knot(p0, p1);
    const t2 = t1 + knot(p1, p2);
    const t3 = t2 + knot(p2, p3);
    for (let k = 0; k < pieces; k++) {
      const u = k / pieces;
      const t = t1 + (t2 - t1) * u;
      // Barry-Goldman のピラミッド評価
      const w01a = (t1 - t) / (t1 - t0);
      const w01b = (t - t0) / (t1 - t0);
      const w12a = (t2 - t) / (t2 - t1);
      const w12b = (t - t1) / (t2 - t1);
      const w23a = (t3 - t) / (t3 - t2);
      const w23b = (t - t2) / (t3 - t2);
      const a1x = w01a * p0.x + w01b * p1.x;
      const a1z = w01a * p0.z + w01b * p1.z;
      const a2x = w12a * p1.x + w12b * p2.x;
      const a2z = w12a * p1.z + w12b * p2.z;
      const a3x = w23a * p2.x + w23b * p3.x;
      const a3z = w23a * p2.z + w23b * p3.z;
      const w02a = (t2 - t) / (t2 - t0);
      const w02b = (t - t0) / (t2 - t0);
      const w13a = (t3 - t) / (t3 - t1);
      const w13b = (t - t1) / (t3 - t1);
      const b1x = w02a * a1x + w02b * a2x;
      const b1z = w02a * a1z + w02b * a2z;
      const b2x = w13a * a2x + w13b * a3x;
      const b2z = w13a * a2z + w13b * a3z;
      out.push(mk(w12a * b1x + w12b * b2x, w12a * b1z + w12b * b2z, p1.q + (p2.q - p1.q) * u));
    }
  }
  const last = cps[n - 1];
  out.push(mk(last.x, last.z, last.q));
  return out;
}

/** river が(合流の連鎖をたどって)targetId へ流れ込む川なら true。循環参照の防御つき */
function flowsInto(river: GeneratedRiver, targetId: string, byId: Map<string, GeneratedRiver>): boolean {
  let cur: GeneratedRiver | undefined = river;
  for (let hop = 0; hop < 64 && cur; hop++) {
    if (cur.mouth !== "confluence" || !cur.confluence) return false;
    if (cur.confluence.riverId === targetId) return true;
    cur = byId.get(cur.confluence.riverId);
  }
  return false;
}

/**
 * 中心線の終点の位置から河口の種類を決め、river.mouth / mouthWaterLevel / confluence を更新する。
 * 優先順位は 海(終点周辺の地形が0m以下) → 湖(既存の水面ポリゴンの内側) → 合流(他の川の
 * 中心線に吸着半径以内で近い。相手の川幅と resampleFactor×spacing×confluenceSnapDsFactor の
 * 大きい方) → 接続なし(edge。水面は地形の窪地充填プロファイルのまま)。
 * 合流の場合は終点を相手の中心線上の最近点へ吸着させる(吸着後の座標を返す)。
 * 自分へ流れ込む川(支流)へは合流させない(循環になるため)。
 */
function resolveRiverMouth(
  river: GeneratedRiver,
  sample: (wx: number, wz: number) => number,
  ds: number,
  lakes: WaterSurfacePayload[],
  others: GeneratedRiver[],
): { x: number; z: number } | null {
  const pts = river.points;
  if (pts.length === 0) return null;
  const tail = pts[pts.length - 1];
  river.mouthWaterLevel = undefined;
  river.confluence = undefined;

  let minAround = Infinity;
  for (const [ox, oz] of [[0, 0], [ds, 0], [-ds, 0], [0, ds], [0, -ds]]) {
    const h = sample(tail.x + ox, tail.z + oz);
    if (h < minAround) minAround = h;
  }
  if (minAround <= 0) {
    river.mouth = "sea";
    return null;
  }

  for (const lake of lakes) {
    if (!lake?.polygon || lake.polygon.type !== "Polygon") continue;
    const ring = (lake.polygon.coordinates as number[][][])[0];
    if (!Array.isArray(ring) || ring.length < 3) continue;
    if (pointInRing(tail.x, tail.z, ring)) {
      river.mouth = "lake";
      river.mouthWaterLevel = lake.water_level;
      return null;
    }
  }

  const byId = new Map<string, GeneratedRiver>();
  for (const r of others) byId.set(r.id, r);
  byId.set(river.id, river);
  let best: { river: GeneratedRiver; index: number; dist: number } | null = null;
  for (const other of others) {
    if (other.id === river.id || other.points.length < 2) continue;
    if (flowsInto(other, river.id, byId)) continue;
    const idx = nearestPointIndex(other.points, tail);
    const op = other.points[idx];
    const dist = Math.hypot(op.x - tail.x, op.z - tail.z);
    const snap = Math.max(op.width, RIVER.confluenceSnapDsFactor * ds);
    if (dist <= snap && (!best || dist < best.dist)) best = { river: other, index: idx, dist };
  }
  if (best) {
    river.mouth = "confluence";
    river.confluence = { riverId: best.river.id, pointIndex: best.index };
    const op = best.river.points[best.index];
    tail.x = op.x;
    tail.z = op.z;
    return { x: op.x, z: op.z };
  }
  river.mouth = "edge";
  return null;
}

/**
 * 制御点列から川1本の中心線・河口・水面プロファイルを作り直す(標高は変更しない)。
 *
 * 制御点を通る曲線を resampleFactor×spacing 間隔の中心線に展開し、各点の川幅を流量から
 * (hydraulic geometry で)求め、河口を終点の位置から判定してから水面プロファイルを確定する。
 * keepMouth が true なら河口の種類は base のものを保つ(終点を動かしていない編集や、
 * 本流の変更に追従して終点を吸着し直した支流の再構築で、河口判定のやり直しを避ける)。
 * 支流の合流先の水面を参照するため others には(彫り込み済みも含む)他の全ての川を渡す。
 * base 自身は変更せず、新しい GeneratedRiver を返す(エディタの Undo スナップショットが
 * 同じオブジェクトを参照しているため)。
 */
export function buildRiverFromControlPoints(
  base: GeneratedRiver,
  controlPoints: RiverControlPoint[],
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  params: RiverGenParams,
  others: GeneratedRiver[],
  lakes: WaterSurfacePayload[],
  keepMouth: boolean,
): RiverRebuildResult {
  const ds = RIVER.resampleFactor * spacing;
  const sample = (wx: number, wz: number) => sampleElevAtWorld(elev, rows, cols, spacing, mapWidth, mapHeight, wx, wz);
  let points = expandCatmullRom(controlPoints, ds / 2);
  points = resampleCenterline(points, ds);
  for (const p of points) p.width = widthFromDischarge(p.q, spacing, params);
  const river: GeneratedRiver = {
    ...base,
    points,
    confluence: base.confluence ? { ...base.confluence } : undefined,
  };
  let cps = controlPoints;
  if (!keepMouth || (river.mouth === "confluence" && !river.confluence)) {
    const snapped = resolveRiverMouth(river, sample, ds, lakes, others);
    if (snapped && cps.length > 0) {
      cps = cps.slice();
      cps[cps.length - 1] = { ...cps[cps.length - 1], x: snapped.x, z: snapped.z };
    }
  }
  const byId = new Map<string, GeneratedRiver>();
  for (const r of others) byId.set(r.id, r);
  byId.set(river.id, river);
  computeOneSurfaceProfile(river, byId, sample, params, ds);
  return { river, controlPoints: cps };
}

/* ============================================================================
 * [3] 川床の彫り込み(carveRivers)
 * ========================================================================== */

/**
 * 確定した川ネットワークの川床を、フル解像度の標高配列へ in-place で彫り込む。
 * 変更されたグリッド範囲(DirtyRect 相当)を返す。何も変更しなかった場合は null。
 *
 * 断面は横断位置 tn ∈ [-1, 1](符号は左法線方向)のV字プロファイルで、タルウェグ(最深線)を
 * カーブの外岸側へ寄せた非対称形状にする。外岸側は頂点が近い分だけ斜面が急(攻撃斜面)、
 * 内岸側は緩い(ポイントバー)。さらに「岸からの距離×岸勾配(bankSlope)」を深さの上限に
 * することで、岸際は水を抜いた実河川のような急な河道壁になる。川幅の外側には最小限の
 * 「なじみ帯」を設け、岸肩(水面+freeboard)から既存地形へ smoothstep で遷移させる。
 *
 * 網状化区間では川床にバー場(バリューノイズ)を加算して中州を作る。バー頂は水面より
 * braidBarClipBelowWater だけ下(冠水中州)にクリップし、横断方向 ±0.7 付近の2本の
 * 分流路には加算しない(連続した可航水路の保証)。
 *
 * 適用は「下げる方向のみ」(elev = min(elev, 目標標高))。既存の窪地を横切っても盛り土
 * (不自然な堤防)ができないようにするため。海(標高0以下)のセルは彫らない。陸のセルが
 * 海面下まで彫られるのは河口付近の自然な入り江(エスチュアリ)として許容する。
 *
 * ラスタライズはセグメントごとのカプセル走査だが、目標標高は一旦「スタンプバッファ」へ
 * min 合成で蓄積し、最後に一括で標高配列へ適用する。セグメントの重なり部分(点の継ぎ目や
 * 蛇行の内側)で同じセルが二重に彫られて溝が深くなりすぎるのを防ぐため。
 */
export function carveRivers(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  rivers: GeneratedRiver[],
): GridRect | null {
  if (rivers.length === 0) return null;
  const halfW = mapWidth / 2;
  const halfH = mapHeight / 2;

  // --- 川ネットワーク全体の外接グリッド範囲(なじみ帯込み)を求め、スタンプバッファを確保する ---
  let minX = Infinity;
  let maxX = -Infinity;
  let minZ = Infinity;
  let maxZ = -Infinity;
  let maxReach = 0;
  for (const river of rivers) {
    for (const p of river.points) {
      if (p.x < minX) minX = p.x;
      if (p.x > maxX) maxX = p.x;
      if (p.z < minZ) minZ = p.z;
      if (p.z > maxZ) maxZ = p.z;
      const reach = p.width / 2 + blendWidth(p.width, spacing);
      if (reach > maxReach) maxReach = reach;
    }
  }
  if (!isFinite(minX)) return null;

  const bboxCMin = Math.max(0, Math.floor((minX - maxReach + halfW) / spacing));
  const bboxCMax = Math.min(cols - 1, Math.ceil((maxX + maxReach + halfW) / spacing));
  const bboxRMin = Math.max(0, Math.floor((minZ - maxReach + halfH) / spacing));
  const bboxRMax = Math.min(rows - 1, Math.ceil((maxZ + maxReach + halfH) / spacing));
  if (bboxCMin > bboxCMax || bboxRMin > bboxRMax) return null;
  const bboxW = bboxCMax - bboxCMin + 1;
  const bboxH = bboxRMax - bboxRMin + 1;
  const stamp = new Float32Array(bboxW * bboxH).fill(Infinity);

  // --- 各セグメントの目標標高をスタンプバッファへ min 蓄積する ---
  for (const river of rivers) {
    const pts = river.points;
    // バー場ノイズ用の累積アーク長(川ごとの流路座標系 s)
    let arc = 0;
    for (let i = 0; i < pts.length - 1; i++) {
      const a = pts[i];
      const b = pts[i + 1];
      const segX = b.x - a.x;
      const segZ = b.z - a.z;
      const segLen = Math.hypot(segX, segZ);
      if (segLen < 1e-9) continue;
      const invLen = 1 / segLen;
      const tx = segX * invLen;
      const tz = segZ * invLen;
      // 左法線(接線を+90°回転)。断面の横断位置 t の符号はこの向きが正。
      const nx = -tz;
      const nz = tx;

      const reach = Math.max(a.width, b.width) / 2 + blendWidth(Math.max(a.width, b.width), spacing);
      const cMin = Math.max(bboxCMin, Math.floor((Math.min(a.x, b.x) - reach + halfW) / spacing));
      const cMax = Math.min(bboxCMax, Math.ceil((Math.max(a.x, b.x) + reach + halfW) / spacing));
      const rMin = Math.max(bboxRMin, Math.floor((Math.min(a.z, b.z) - reach + halfH) / spacing));
      const rMax = Math.min(bboxRMax, Math.ceil((Math.max(a.z, b.z) + reach + halfH) / spacing));

      for (let r = rMin; r <= rMax; r++) {
        const wz = r * spacing - halfH;
        for (let c = cMin; c <= cMax; c++) {
          const wx = c * spacing - halfW;
          // セグメントへの射影(s: 0..1 にクランプ)と、セグメントまでの実距離。
          // 横断距離は法線成分だけでなく最近点までのユークリッド距離(端点ではみ出した
          // 軸方向成分も含む)で測る。法線成分だけにすると、セグメント端を超えたセルにも
          // 端の断面がそのまま軸方向へ延長されて印字され、急斜面では下流の低い川床が
          // 斜面の上方まで彫り込まれてしまう(端点は放射状のキャップとして扱うのが正しい)。
          const relX = wx - a.x;
          const relZ = wz - a.z;
          let s = (relX * tx + relZ * tz) * invLen;
          if (s < 0) s = 0;
          else if (s > 1) s = 1;
          const projX = a.x + segX * s;
          const projZ = a.z + segZ * s;
          const offX = wx - projX;
          const offZ = wz - projZ;
          const absT = Math.hypot(offX, offZ);
          // 断面の左右(非対称性)の判定にだけ法線成分の符号を使う
          const t = offX * nx + offZ * nz >= 0 ? absT : -absT;

          // セグメント上のパラメータ補間
          const w = a.width + (b.width - a.width) * s;
          const halfWidth = w / 2;
          const blend = blendWidth(w, spacing);
          if (absT > halfWidth + blend) continue;

          const waterY = a.waterY + (b.waterY - a.waterY) * s;
          const bedY = a.bedY + (b.bedY - a.bedY) * s;
          const kappa = a.curvature + (b.curvature - a.curvature) * s;
          const depth = waterY - bedY; // タルウェグ深(淵の深化込み)

          let target: number;
          if (absT <= halfWidth) {
            // --- 川内部: 鋭角ノッチの非対称断面(岸勾配クリップ付き) ---
            // tn ∈ [-1,1]。タルウェグは外岸側(K>0=左カーブなら右岸=負のt側)へ寄る。
            // 深さプロファイルは 1 - |x|^p(p<1)。タルウェグ直上で勾配が急峻になり、
            // 川底の最深部が鋭角なノッチとしてはっきり刻まれる(直線のV字だと谷底の
            // 勾配が緩く「ふわっとした」窪みに見える)。さらに「岸からの距離×岸勾配」を
            // 深さの上限とすることで、岸際は実河川の水を抜いたときのような急な壁
            // (bankSlope、約35°)で立ち上がる。
            const tn = t / halfWidth;
            const t0 = -RIVER.thalwegShift * kappa;
            const denom = tn < t0 ? 1 + t0 : 1 - t0;
            const xNorm = Math.abs((tn - t0) / Math.max(1e-6, denom));
            const fRaw = 1 - Math.pow(Math.min(1, xNorm), RIVER.crossSectionExponent);
            const f = fRaw > 0 ? fRaw : 0;
            // 岸勾配は「タルウェグで要求深さに到達できる勾配」を下限に引き上げる。
            // 固定勾配のままだと 深さ > 川幅/2×bankSlope の彫り込みが幾何学的に不可能になり、
            // 底が平らにクリップされて深さ係数が効かなくなる(深い川は峡谷のように壁が立つ)。
            const bankToThalweg = Math.max(1e-3, halfWidth * (1 - Math.abs(t0)));
            const effSlope = Math.max(RIVER.bankSlope, depth / bankToThalweg);
            const bankLimit = (halfWidth - absT) * effSlope;
            target = waterY - Math.min(depth * f, bankLimit);

            // --- 中州(網状化区間のバー場) ---
            const braided = (s < 0.5 ? a.braided : b.braided) && (s < 0.5 ? b.braided : a.braided);
            if (braided && absT / halfWidth < RIVER.braidBarExtent) {
              const sGlobal = arc + segLen * s;
              const wavelength = RIVER.braidNoiseWavelengthWidths * w;
              const noise = valueNoise2D(sGlobal / wavelength, tn * 1.5);
              const bar = RIVER.braidBarHeightFactor * depth * Math.max(0, noise);
              // バー頂は水面より braidBarClipBelowWater 下まで(冠水中州)。
              target = Math.min(target + bar, waterY - RIVER.braidBarClipBelowWater);
            }
          } else {
            // --- なじみ帯: 岸肩(水面+freeboard)から既存地形へ smoothstep で遷移 ---
            const u = (absT - halfWidth) / blend;
            const sm = u * u * (3 - 2 * u);
            const shoulder = waterY + RIVER.freeboard;
            const existing = elev[r * cols + c];
            target = shoulder * (1 - sm) + existing * sm;
          }

          const si = (r - bboxRMin) * bboxW + (c - bboxCMin);
          if (target < stamp[si]) stamp[si] = target;
        }
      }
      arc += segLen;
    }
  }

  // --- スタンプバッファを一括適用(下げる方向のみ、海は彫らない) ---
  let dirty: GridRect | null = null;
  for (let r = bboxRMin; r <= bboxRMax; r++) {
    const rowBase = r * cols;
    const stampBase = (r - bboxRMin) * bboxW;
    for (let c = bboxCMin; c <= bboxCMax; c++) {
      const target = stamp[stampBase + (c - bboxCMin)];
      if (!isFinite(target)) continue;
      const idx = rowBase + c;
      const cur = elev[idx];
      if (cur <= 0 || target >= cur) continue;
      elev[idx] = target;
      if (!dirty) {
        dirty = { rMin: r, rMax: r, cMin: c, cMax: c };
      } else {
        if (r < dirty.rMin) dirty.rMin = r;
        if (r > dirty.rMax) dirty.rMax = r;
        if (c < dirty.cMin) dirty.cMin = c;
        if (c > dirty.cMax) dirty.cMax = c;
      }
    }
  }
  return dirty;
}

/** 川幅からなじみ帯の幅(m)を求める */
function blendWidth(width: number, spacing: number): number {
  return Math.max(RIVER.blendWidthFactor * width, RIVER.blendMinSpacingFactor * spacing);
}

/* ============================================================================
 * 保存契約向けヘルパー
 * ========================================================================== */

/**
 * 川の範囲ポリゴンを抽出するときの「種セル」の指定。
 *   - spline: 彫り込み直後用。中心線±(川幅/2 + なじみ帯)の帯(=彫り込みが及んだ範囲)を種にする。
 *     川幅は points の width を使う。
 *   - polygon: 保存済みの川(幅情報を持たない)の再計算用。既存の範囲ポリゴンの内側を種にする。
 *     彫り込み後に手作業で岸を削った/盛った場合、種から連結する範囲だけが追従して伸縮する。
 */
export type RiverPolygonBand =
  | { kind: "spline" }
  | { kind: "polygon"; polygon: WaterPolygonGeoJson };

/**
 * 彫り込み後の地形から、川1本の範囲ポリゴン(穴=中州・島)を抽出する。
 *
 * 範囲の定義は「種セルから連結して到達できる、地形標高が水面標高(プロファイル線上の
 * 最近点の waterY)を下回る格子点の集合」。湖の穴あけ(footprint 内で水位以下の領域)と同じ
 * 考え方を流下方向に変化する水面に適用し、さらに河道から連結していること(=実際に水が
 * 流れ込める)を条件にする。連結条件が無いと、河道の岸(なじみ帯)の外側にある無関係な
 * 低地(水面より低いが土手で隔てられた窪地)まで範囲に入ってしまう。
 * 河道の水面が周囲の地形より高い区間(1次元窪地充填で溢出点の高さまで湛水した区間)では、
 * 水は岸を越えて周囲の低地へ広がる。この広がりは「プロファイル線からの距離が
 * polygonMaxDistFromProfileM 以内」に制限する(平坦な氾濫原で際限なく広がるのを防ぐ)。
 * 制限を種からの歩数ではなく線からの距離にしているのは、再計算(前回の範囲を種にする)を
 * 何度繰り返しても範囲が同じになる(冪等)ようにするため。
 *
 * 以下は範囲から除外する:
 *   - 標高 0m 以下(海)。海面板が既に覆っており、河口で川の水面と重ねると Z-fighting になる
 *   - 既存の湖(water_surfaces)の水位以下の領域。湖の水面が担当する
 *   - claimed に立っている格子点。流量の大きい川から順に処理し、合流部で本流に取られた
 *     領域を支流が二重に持たないようにする(重なった水面同士の Z-fighting 回避)
 * 抽出した範囲の格子点は claimed へ書き戻す。
 *
 * 場は到達範囲の外接矩形+1セルのパディング(負値)の上に作り、領域が必ず閉じるようにする。
 * 戻り値は範囲が空なら null。
 */
export function buildRiverPolygon(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  river: GeneratedRiver,
  band: RiverPolygonBand,
  claimed: Uint8Array,
  waterSurfaces: WaterSurfacePayload[],
): WaterPolygonGeoJson | null {
  const pts = river.points;
  if (pts.length < 2) return null;
  const halfW = mapWidth / 2;
  const halfH = mapHeight / 2;
  // --- 種セルの外接グリッド範囲(氾濫の到達分だけ広げる) ---
  let cMin = Infinity;
  let cMax = -Infinity;
  let rMin = Infinity;
  let rMax = -Infinity;
  const extend = (wx: number, wz: number, reach: number) => {
    cMin = Math.min(cMin, Math.floor((wx - reach + halfW) / spacing));
    cMax = Math.max(cMax, Math.ceil((wx + reach + halfW) / spacing));
    rMin = Math.min(rMin, Math.floor((wz - reach + halfH) / spacing));
    rMax = Math.max(rMax, Math.ceil((wz + reach + halfH) / spacing));
  };
  const floodReach = RIVER.polygonMaxDistFromProfileM;
  if (band.kind === "spline") {
    for (const p of pts) extend(p.x, p.z, p.width / 2 + blendWidth(p.width, spacing) + floodReach);
  } else {
    const b = polygonBounds(band.polygon);
    if (!b) return null;
    extend(b.minX, b.minZ, floodReach);
    extend(b.maxX, b.maxZ, floodReach);
  }
  cMin = Math.max(0, cMin);
  rMin = Math.max(0, rMin);
  cMax = Math.min(cols - 1, cMax);
  rMax = Math.min(rows - 1, rMax);
  if (!isFinite(cMin) || cMin > cMax || rMin > rMax) return null;

  // 場はパディング1セルを含む (bw × bh)。場の格子点 (i, j) ↔ 地形格子 (c, r) = (cMin + i - 1, rMin + j - 1)
  const bw = cMax - cMin + 3;
  const bh = rMax - rMin + 3;
  const originX = (cMin - 1) * spacing - halfW;
  const originZ = (rMin - 1) * spacing - halfH;
  const field = new Float32Array(bw * bh).fill(-1);
  const seed = new Uint8Array(bw * bh);

  if (band.kind === "spline") {
    // 彫り込み(carveRivers)と同じく、各線分の到達範囲のセルを種にする
    for (let s = 0; s < pts.length - 1; s++) {
      const a = pts[s];
      const b = pts[s + 1];
      const segX = b.x - a.x;
      const segZ = b.z - a.z;
      const len2 = segX * segX + segZ * segZ;
      if (len2 < 1e-12) continue;
      const wMax = Math.max(a.width, b.width);
      const reach = wMax / 2 + blendWidth(wMax, spacing);
      const sc0 = Math.max(cMin, Math.floor((Math.min(a.x, b.x) - reach + halfW) / spacing));
      const sc1 = Math.min(cMax, Math.ceil((Math.max(a.x, b.x) + reach + halfW) / spacing));
      const sr0 = Math.max(rMin, Math.floor((Math.min(a.z, b.z) - reach + halfH) / spacing));
      const sr1 = Math.min(rMax, Math.ceil((Math.max(a.z, b.z) + reach + halfH) / spacing));
      for (let r = sr0; r <= sr1; r++) {
        const wz = r * spacing - halfH;
        for (let c = sc0; c <= sc1; c++) {
          const wx = c * spacing - halfW;
          let t = ((wx - a.x) * segX + (wz - a.z) * segZ) / len2;
          if (t < 0) t = 0;
          else if (t > 1) t = 1;
          const d = Math.hypot(wx - (a.x + segX * t), wz - (a.z + segZ * t));
          const w = a.width + (b.width - a.width) * t;
          if (d > w / 2 + blendWidth(w, spacing)) continue;
          seed[(r - rMin + 1) * bw + (c - cMin + 1)] = 1;
        }
      }
    }
  } else {
    // 既存ポリゴンの内側の格子点を種にする
    const base = rasterizePolygonMask(band.polygon, originX, originZ, spacing, bw, bh);
    for (let j = 1; j < bh - 1; j++) {
      for (let i = 1; i < bw - 1; i++) {
        if (base[j * bw + i]) seed[j * bw + i] = 1;
      }
    }
  }

  // --- 各格子点が「水面より低く、川の範囲になり得るか」の判定(遅延評価) ---
  const sampler = new RiverProfileSampler(pts.map((p) => [p.x, p.waterY, p.z]));
  const lakes = waterSurfaces
    .filter((ws) => typeof ws?.water_level === "number")
    .map((ws) => ({ level: ws.water_level, polygon: ws.polygon, bounds: polygonBounds(ws.polygon) }));
  // 0=未評価, 1=範囲候補, 2=範囲外
  const state = new Uint8Array(bw * bh);
  const evaluate = (i: number, j: number): boolean => {
    const fi = j * bw + i;
    if (state[fi] !== 0) return state[fi] === 1;
    let ok = false;
    if (i >= 1 && i < bw - 1 && j >= 1 && j < bh - 1) {
      const c = cMin + i - 1;
      const r = rMin + j - 1;
      const h = elev[r * cols + c];
      if (h > 0 && !claimed[r * cols + c]) {
        const wx = c * spacing - halfW;
        const wz = r * spacing - halfH;
        const near = sampler.nearest(wx, wz);
        const depth = near.dist <= RIVER.polygonMaxDistFromProfileM
          ? sampler.heightOn(near.seg, near.t) - h - RIVER.polygonWaterEps
          : -1;
        if (depth > 0) {
          let inLake = false;
          for (const lake of lakes) {
            const b = lake.bounds;
            if (!b || h > lake.level) continue;
            if (wx < b.minX || wx > b.maxX || wz < b.minZ || wz > b.maxZ) continue;
            if (pointInPolygon(wx, wz, lake.polygon)) {
              inLake = true;
              break;
            }
          }
          if (!inLake) {
            ok = true;
            field[fi] = depth;
          }
        }
      }
    }
    state[fi] = ok ? 1 : 2;
    return ok;
  };

  // --- 種から4近傍へ幅優先で広げる(連結成分の抽出) ---
  const queueI: number[] = [];
  const queueJ: number[] = [];
  const reached = new Uint8Array(bw * bh);
  for (let j = 1; j < bh - 1; j++) {
    for (let i = 1; i < bw - 1; i++) {
      const fi = j * bw + i;
      if (seed[fi] && evaluate(i, j)) {
        reached[fi] = 1;
        queueI.push(i);
        queueJ.push(j);
      }
    }
  }
  let head = 0;
  while (head < queueI.length) {
    const i = queueI[head];
    const j = queueJ[head];
    head++;
    const neighbors: [number, number][] = [[i - 1, j], [i + 1, j], [i, j - 1], [i, j + 1]];
    for (const [ni, nj] of neighbors) {
      const nfi = nj * bw + ni;
      if (reached[nfi]) continue;
      if (!evaluate(ni, nj)) continue;
      reached[nfi] = 1;
      queueI.push(ni);
      queueJ.push(nj);
    }
  }
  // 到達しなかった候補(種と連結していない低地)は範囲外に戻す
  for (let fi = 0; fi < field.length; fi++) {
    if (!reached[fi]) field[fi] = -1;
  }

  const polygon = extractRegionPolygon(field, bw, bh, originX, originZ, spacing, RIVER.polygonSimplifyFactor * spacing);
  if (!polygon) return null;

  // 範囲に入った格子点を claimed へ書き戻す(後続の川の除外用)
  for (let j = 1; j < bh - 1; j++) {
    const r = rMin + j - 1;
    for (let i = 1; i < bw - 1; i++) {
      if (field[j * bw + i] > 0) claimed[r * cols + (cMin + i - 1)] = 1;
    }
  }
  return polygon;
}
