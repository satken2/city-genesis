/**
 * 地形エディタの「自動編集」用の侵食アルゴリズム集。
 *
 * TerrainEditorCanvas から呼ばれる純粋関数のみを置き、React やキャンバス描画には
 * 一切依存しない。標高データはエディタ本体と同じ「行優先の Float32Array
 * (rows × cols、格子間隔 spacing メートル、標高 0 以下は海面)」をそのまま受け取り、
 * 配列を直接書き換える(in-place)。呼び出し側はテクスチャ・等高線の再構築を行うこと。
 *
 * 提供する侵食は2種類:
 *   - 熱侵食(Thermal Erosion): 安息角を超える急斜面の土砂が崩れ落ちて裾に堆積する
 *     現象のシミュレーション。崖をなだらかにする用途。
 *   - 水流侵食(Hydraulic Erosion): 雨粒パーティクルが斜面を流れ下りながら土砂を
 *     削り・運び・堆積させるシミュレーション。谷筋や扇状地のような地形を刻む用途。
 *     海面(SEA_LEVEL)に達した雨粒は消滅し、運んでいた土砂は海中の斜面を滑り落ちて
 *     安息角より緩い海底に沈降する(settleSedimentUnderwater)。
 */

/** 熱侵食のパラメータ(UIから調整可能なもののみ) */
export type ThermalErosionParams = {
  /** 安息角(度)。この角度を超える斜面が崩れる。砂は約30〜35度、岩場なら大きめ */
  talusAngleDeg: number;
  /** 反復回数。多いほど広範囲がなだらかになる */
  iterations: number;
};

/** 水流侵食のパラメータ(UIから調整可能なもののみ) */
export type HydraulicErosionParams = {
  /** シミュレーションする雨粒の数。多いほど侵食が進み、計算時間も比例して増える */
  droplets: number;
  /** 侵食の強さ(0..1)。雨粒が1ステップで削れる土砂量の係数 */
  erodeStrength: number;
};

/**
 * 侵食を選択範囲へ限定するための領域指定。
 *
 * 地形エディタ側の選択マスク(グリッド点ごとの 0..1 重み)と、その外接矩形(グリッド範囲)を
 * そのまま渡す。mask は行優先で長さ rows*cols、範囲外(選択外)のセルは重み 0。
 * このモジュールは React / エディタ本体に依存しない方針のため、エディタ側の DirtyRect 型は
 * import せず、必要なフィールドだけを持つ独自の型として定義する。
 *
 * 侵食を「選択範囲だけ」に厳密に閉じ込めるのではなく、選択範囲を起点として自然に侵食させる
 * (土砂や雨粒が境界を多少越えて流れ、周辺と連続的になじむことを許容する)ために使う。
 */
export type ErosionRegion = {
  /** グリッド点ごとの選択重み(0..1)。行優先・長さ rows*cols。 */
  mask: Float32Array;
  /** 選択範囲の外接矩形(グリッド行・列の閉区間)。 */
  rMin: number;
  rMax: number;
  cMin: number;
  cMax: number;
};

/** 熱侵食で1反復あたりに移動させる「超過分」の割合。1.0にすると振動しやすいため半分に抑える */
const THERMAL_RATE = 0.5;

/**
 * 海面の標高(メートル)。標高がこの値以下の場所は海とみなす。
 * near.bin の仕様(標高0以下=海面)と対応する。水流侵食では、雨粒がこの高さまで
 * 下りてきた瞬間に「大量の海水にぶつかって流速がゼロ(=運搬容量もゼロ)になった」と
 * 解釈し、運んでいた土砂を全て海中へ堆積させて粒を終了する判定の基準に使う。
 */
const SEA_LEVEL = 0;

/**
 * 水流侵食の内部定数(Hans Beyer の droplet 法で一般的に使われる値)。
 * UIに露出すると調整項目が多すぎるため、効果への影響が大きい「雨粒の数」と
 * 「侵食の強さ」だけをパラメータ化し、残りはここで固定する。
 */
const HYDRAULIC = {
  /** 慣性(0..1)。大きいほど雨粒が直進し、小さいほど勾配に忠実に曲がる */
  inertia: 0.05,
  /** 運搬容量係数。大きいほど1粒が多くの土砂を運べる */
  capacityFactor: 4,
  /** 容量計算に使う最小勾配。平地で容量が0になって即堆積するのを防ぐ */
  minSlope: 0.01,
  /** 容量超過時に1ステップで堆積させる割合(0..1) */
  depositSpeed: 0.3,
  /** 1ステップごとの水の蒸発率(0..1) */
  evaporate: 0.01,
  /** 高低差から速度を更新する際の重力係数 */
  gravity: 4,
  /** 雨粒1つの最大ステップ数。無限ループ防止と計算量の上限 */
  maxLifetime: 30,
  /** 侵食を分散させるブラシ半径(セル数)。1点掘りの縦穴を防ぐ */
  erosionRadius: 3,
  /** 陸地(標高がSEA_LEVELより高い場所)に落ちるまでの再抽選回数。海ばかり引く粒は捨てる */
  spawnRetries: 8,
} as const;

/**
 * 雨粒が海面(SEA_LEVEL)に達した後の「海中の土砂沈降」の内部定数。
 *
 * 着水点へ土砂を一括で積む方式だと、雨粒は1ステップ=1セルしか進まないため着水点は
 * 必ず海岸線から1セル以内になり、海がどれだけ深くても海岸線のすぐ沖の1列だけが
 * 垂直な柱状に海面まで埋まって「垂直な崖のまま島が1セルずつ広がる」不自然な挙動になる。
 * そこで着水後の土砂は混濁流のように海底斜面を滑り落とし、安息角より緩い場所でだけ
 * 沈降させる(settleSedimentUnderwater)。
 */
const UNDERWATER = {
  /** 海中の安息角(度)。これより急な海中斜面では土砂は沈降せず滑り続ける */
  talusAngleDeg: 30,
  /** 完全に平坦な海底で1ステップに沈降する土砂の割合。斜面が安息角に近づくほど減る */
  depositRate: 0.3,
  /** 沈降フェーズの最大ステップ数(計算量の上限。使い切ったら残りを最終位置へ置く) */
  maxSteps: 100,
  /** 運搬量がこれ未満(m)まで減ったら、残りを現在位置へ置いて終了する */
  minSediment: 1e-3,
} as const;

/**
 * 熱侵食(安息角のシミュレーション)を標高配列へ適用する。
 *
 * 各反復で全セルを走査し、8近傍との高低差が安息角に相当する閾値
 * (tan(安息角) × 水平距離)を超えている場合、超過分に比例した土砂を低い側の
 * 隣接セルへ移動させる。これを反復すると、急峻な崖が安息角に近い斜面へ崩れていく。
 *
 * 差分は一時バッファに蓄積して反復の最後に一括加算する。セルを走査しながら直接
 * 書き換えると、走査方向(左上→右下)に依存して崩落が偏るため。
 *
 * 海面下(標高0以下)のセルも通常セルと同様に扱う。海底で崩落が起きても表示・
 * ゲーム進行への実害はなく、海岸の水際が自然になじむ利点の方が大きい。
 *
 * region を渡すと、崩落の対象を選択範囲の外接矩形内のセルだけに絞り、各セルの流出量を
 * 選択重み(mask)で減衰させる。境界に向かって重みが 0 へ滑らかに落ちるため、選択の縁で
 * 段差(不自然な崖)ができず周辺と連続的になじむ。低い側の隣接セルが選択範囲の外にあっても
 * 土砂はそのまま渡す(範囲外が多少なだらかに変化するのは許容)。region 未指定なら全域。
 */
export function applyThermalErosion(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  params: ThermalErosionParams,
  region?: ErosionRegion,
): void {
  const count = rows * cols;
  // 走査範囲。region があれば外接矩形に限定する(rows/cols でクランプ)。
  const rLo = region ? Math.max(0, region.rMin) : 0;
  const rHi = region ? Math.min(rows - 1, region.rMax) : rows - 1;
  const cLo = region ? Math.max(0, region.cMin) : 0;
  const cHi = region ? Math.min(cols - 1, region.cMax) : cols - 1;
  // delta を適用し戻す範囲。隣接セルへ配分された土砂も拾うため上下左右1セルだけ広げる。
  const applyRLo = Math.max(0, rLo - 1);
  const applyRHi = Math.min(rows - 1, rHi + 1);
  const applyCLo = Math.max(0, cLo - 1);
  const applyCHi = Math.min(cols - 1, cHi + 1);
  // 8近傍のオフセット(行, 列)と、対応する崩落閾値(高低差がこれを超えたら崩れる)
  const talusTan = Math.tan((params.talusAngleDeg * Math.PI) / 180);
  const thresholdOrtho = talusTan * spacing; // 上下左右
  const thresholdDiag = talusTan * spacing * Math.SQRT2; // 斜め
  const neighborOffsets = [
    { dr: -1, dc: 0, threshold: thresholdOrtho },
    { dr: 1, dc: 0, threshold: thresholdOrtho },
    { dr: 0, dc: -1, threshold: thresholdOrtho },
    { dr: 0, dc: 1, threshold: thresholdOrtho },
    { dr: -1, dc: -1, threshold: thresholdDiag },
    { dr: -1, dc: 1, threshold: thresholdDiag },
    { dr: 1, dc: -1, threshold: thresholdDiag },
    { dr: 1, dc: 1, threshold: thresholdDiag },
  ];

  // 反復ごとの標高差分の蓄積バッファ(走査順に依存しない対称な崩落にするため)
  const delta = new Float32Array(count);
  // 超過分の一時保存(セルごとに8近傍分を使い回す)
  const excess = new Float32Array(neighborOffsets.length);
  const excessIdx = new Int32Array(neighborOffsets.length);

  for (let iter = 0; iter < params.iterations; iter++) {
    delta.fill(0);
    for (let r = rLo; r <= rHi; r++) {
      const rowBase = r * cols;
      for (let c = cLo; c <= cHi; c++) {
        const idx = rowBase + c;
        // region があるセルの選択重み。0 のセルは崩落させない。
        const weight = region ? region.mask[idx] : 1;
        if (weight <= 0) continue;
        const h = elev[idx];

        // 閾値を超えて低い隣接セルを列挙し、超過分の合計と最大値を求める
        let excessCount = 0;
        let totalExcess = 0;
        let maxExcess = 0;
        for (const n of neighborOffsets) {
          const nr = r + n.dr;
          const nc = c + n.dc;
          if (nr < 0 || nr >= rows || nc < 0 || nc >= cols) continue;
          const nIdx = nr * cols + nc;
          const e = h - elev[nIdx] - n.threshold;
          if (e <= 0) continue;
          excess[excessCount] = e;
          excessIdx[excessCount] = nIdx;
          excessCount++;
          totalExcess += e;
          if (e > maxExcess) maxExcess = e;
        }
        if (excessCount === 0) continue;

        // 最大超過分の一部を、各隣接セルへ超過分に比例して配分する。
        // region 指定時は選択重みで流出量を減衰させ、境界をなだらかになじませる。
        const moveTotal = THERMAL_RATE * (maxExcess / 2) * weight;
        delta[idx] -= moveTotal;
        for (let i = 0; i < excessCount; i++) {
          delta[excessIdx[i]] += moveTotal * (excess[i] / totalExcess);
        }
      }
    }
    // region 指定時は delta が非ゼロなのは走査範囲＋隣接1セルのみなので、その範囲だけ適用する。
    if (region) {
      for (let r = applyRLo; r <= applyRHi; r++) {
        const rowBase = r * cols;
        for (let c = applyCLo; c <= applyCHi; c++) {
          const i = rowBase + c;
          elev[i] += delta[i];
        }
      }
    } else {
      for (let i = 0; i < count; i++) elev[i] += delta[i];
    }
  }
}

/**
 * 位置(連続セル座標)の高さと勾配をバイリニア補間で求めた結果。
 * 勾配はセル単位(1セル進んだときの標高変化)で返す。
 */
type HeightAndGradient = { height: number; gradX: number; gradZ: number };

/** 連続セル座標 (x, z) の高さと勾配をバイリニア補間で計算する */
function sampleHeightAndGradient(
  elev: Float32Array,
  cols: number,
  x: number,
  z: number,
  out: HeightAndGradient,
): void {
  const cx = Math.floor(x);
  const cz = Math.floor(z);
  const fx = x - cx;
  const fz = z - cz;
  const base = cz * cols + cx;
  const h00 = elev[base];
  const h10 = elev[base + 1];
  const h01 = elev[base + cols];
  const h11 = elev[base + cols + 1];
  // X方向・Z方向それぞれの偏微分をバイリニアの係数から直接求める
  out.gradX = (h10 - h00) * (1 - fz) + (h11 - h01) * fz;
  out.gradZ = (h01 - h00) * (1 - fx) + (h11 - h10) * fx;
  out.height = h00 * (1 - fx) * (1 - fz) + h10 * fx * (1 - fz) + h01 * (1 - fx) * fz + h11 * fx * fz;
}

/** 土砂量 amount(m) を、連続セル座標 (x, z) を囲む格子点4つへバイリニア重みで加算する */
function depositBilinear(elev: Float32Array, cols: number, x: number, z: number, amount: number): void {
  const cx = Math.floor(x);
  const cz = Math.floor(z);
  const fx = x - cx;
  const fz = z - cz;
  const idx = cz * cols + cx;
  elev[idx] += amount * (1 - fx) * (1 - fz);
  elev[idx + 1] += amount * fx * (1 - fz);
  elev[idx + cols] += amount * (1 - fx) * fz;
  elev[idx + cols + 1] += amount * fx * fz;
}

/**
 * 海面に達した雨粒の土砂を、海中の斜面に沿って滑り落としながら沈降させる。
 *
 * 雨粒本体は着水した瞬間に「大量の海水にぶつかって流速がゼロになった」とみなして
 * 消滅するが、土砂まで着水点へ一括で積むと海岸線のすぐ沖だけが垂直な柱状に埋まる
 * (UNDERWATER 定数のコメント参照)。実際の土砂は水中でも斜面を滑り落ち、安息角より
 * 緩い海底に溜まっていくため、ここでは土砂を混濁流のように最急降下方向へ1セルずつ
 * 滑らせ、各位置で「斜面が安息角より緩いほど多く」沈降させる。
 *
 * 深い海に面した断崖から流入した土砂は、崖面(安息角より急)ではほとんど沈降せず、
 * 崖下の海底へ届いてから堆積する。堆積が尖って安息角を超えると、後続の土砂はその
 * 斜面を滑ってさらに沖へ運ばれるため、堆積は自然と安息角以下の扇状(錐状)に整い、
 * 海底から海面まで積み上がったところで初めて新しい陸地になる。
 *
 * 勾配が無い平坦な海底では直前の進行方向を惰性として維持し、沈降しながら直進する。
 * マップ外へ出た土砂は、マップ外の海底へ沈んだとみなして捨てる。
 */
function settleSedimentUnderwater(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  startX: number,
  startZ: number,
  startDirX: number,
  startDirZ: number,
  sediment: number,
  hg: HeightAndGradient,
): void {
  // 補間・移動で cx+1 / cz+1 を参照するため、可動範囲は雨粒本体と同じ [0, cols-1) × [0, rows-1)
  const maxX = cols - 1;
  const maxZ = rows - 1;
  const talusTan = Math.tan((UNDERWATER.talusAngleDeg * Math.PI) / 180);
  let x = startX;
  let z = startZ;
  let dirX = startDirX;
  let dirZ = startDirZ;
  for (let step = 0; step < UNDERWATER.maxSteps; step++) {
    sampleHeightAndGradient(elev, cols, x, z, hg);
    // 現在位置の斜度(tan)。gradX/gradZ は「1セルあたりの標高変化[m]」なので spacing で割る。
    const slopeTan = Math.hypot(hg.gradX, hg.gradZ) / spacing;
    // 安息角より急な斜面では沈降させない。緩いほど depositRate に近い割合で沈降させる。
    const settleRatio = Math.max(0, 1 - slopeTan / talusTan) * UNDERWATER.depositRate;
    if (settleRatio > 0) {
      const deposit = sediment * settleRatio;
      depositBilinear(elev, cols, x, z, deposit);
      sediment -= deposit;
      if (sediment < UNDERWATER.minSediment) return;
    }
    // 最急降下方向へ1セル進む。勾配が無い(平坦)なら直前の進行方向を惰性で維持する。
    const len = Math.hypot(hg.gradX, hg.gradZ);
    if (len > 1e-8) {
      dirX = -hg.gradX / len;
      dirZ = -hg.gradZ / len;
    }
    x += dirX;
    z += dirZ;
    if (x < 0 || x >= maxX || z < 0 || z >= maxZ) return; // マップ外へ流出
  }
  // ステップ数を使い切った場合は、残りを現在位置へ置く(消えると質量が失われるため)
  depositBilinear(elev, cols, x, z, sediment);
}

/** 侵食ブラシの1点(中心からのオフセットと正規化済み重み) */
type BrushPoint = { dr: number; dc: number; weight: number };

/**
 * 侵食を周囲に分散させる円形ブラシ(中心ほど重い線形減衰、重みの合計=1)を事前計算する。
 * 雨粒ごとに計算すると重いため、1回の水流侵食の間は同じテーブルを使い回す。
 */
function buildErosionBrush(radius: number): BrushPoint[] {
  const points: BrushPoint[] = [];
  let totalWeight = 0;
  for (let dr = -radius; dr <= radius; dr++) {
    for (let dc = -radius; dc <= radius; dc++) {
      const dist = Math.sqrt(dr * dr + dc * dc);
      if (dist > radius) continue;
      const weight = 1 - dist / radius;
      if (weight <= 0) continue;
      points.push({ dr, dc, weight });
      totalWeight += weight;
    }
  }
  for (const p of points) p.weight /= totalWeight;
  return points;
}

/**
 * パーティクルベースの水流侵食(droplet法)を標高配列へ適用する。
 *
 * 雨粒を1粒ずつランダムな陸地に落とし、以下を寿命が尽きるまで繰り返す:
 *   1. 現在位置の勾配を求め、慣性を混ぜた方向へ1セル分移動する
 *   2. 移動による高低差・速度・水量から「運搬容量」を計算する
 *   3. 運んでいる土砂が容量を超えた(または上り坂に当たった)なら移動前の位置に堆積、
 *      余裕があるなら移動前の位置周辺をブラシ状に削って土砂を積み込む
 *   4. 速度と水量を更新する(下りで加速し、水は徐々に蒸発する)
 * 移動先が海(標高 SEA_LEVEL 以下)に達したステップでは、高低差を「海面に触れるまでの
 * 落差」にクランプしたうえで通常どおり侵食・堆積を行い、その後「大量の海水にぶつかって
 * 流速がゼロ=運搬容量がゼロになった」と解釈して粒を終了する。クランプにより、海面より
 * 下に続く海中の崖の落差は侵食量に寄与しない(崖の海面から上の部分だけが普通の崖として
 * 侵食されるため、断崖絶壁の島は上面の縁から徐々に削れていく)。運んでいた土砂は
 * settleSedimentUnderwater へ引き渡し、海底斜面を滑り落としながら安息角より緩い海底へ
 * 沈降させる(深い海に面した断崖なら崖下の海底に扇状に堆積する)。着水点へ一括堆積させると
 * 着水点は必ず海岸線から1セル以内なので、海の深さに関係なく海岸線のすぐ沖が垂直な柱状に
 * 海面まで埋まり、島が1セルずつ際限なく広がってしまう。沈降方式では海中の堆積が何万粒も
 * 積み重なって標高が SEA_LEVEL を超えたときにのみ新しい陸地が生まれ、河口の平野(デルタ)は
 * 海が実際に埋まった分だけ前進する。
 *
 * 位置はセル単位で扱うが、運搬容量と速度の計算には spacing で無次元化した勾配
 * (高低差[m] ÷ 格子間隔[m])を使う。標高はメートル値のため、高低差をそのまま使うと
 * 容量が実質無限大になり、崖際が1ステップで数十mも削れてしまう。物理的な厳密さより
 * 「見た目に谷筋が刻まれる」ことを優先した、地形エディタ用途の標準的な簡略化。
 *
 * region を渡すと、雨粒の投下位置を選択範囲の外接矩形内に限定し、さらに選択重み(mask)を
 * 投下確率として使う(縁ほど雨粒が減り、侵食強度が滑らかに弱まる)。落ちた後の流下・侵食・
 * 堆積の物理は全域時と同じなので、谷が連続的に刻まれ選択境界に段差はできない。雨粒が境界を
 * 越えて流れ、範囲外を多少削るのは許容する。また、選択面積(mask 重みの総和)に応じて
 * 実際にシミュレートする雨粒数を按分し、全域と同じ droplets 値でも単位面積あたりの侵食密度が
 * 全域実行時と揃うようにする。region 未指定なら全域へ従来どおり適用する。
 */
export function applyHydraulicErosion(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  params: HydraulicErosionParams,
  region?: ErosionRegion,
): void {
  const brush = buildErosionBrush(HYDRAULIC.erosionRadius);
  const hg: HeightAndGradient = { height: 0, gradX: 0, gradZ: 0 };
  // 補間・移動で cx+1 / cz+1 を参照するため、粒の可動範囲は [0, cols-1) × [0, rows-1)
  const maxX = cols - 1;
  const maxZ = rows - 1;

  // 雨粒の投下範囲(セル座標)。region があれば外接矩形に限定する。
  const spawnCLo = region ? Math.max(0, region.cMin) : 0;
  const spawnCHi = region ? Math.min(maxX, region.cMax) : maxX;
  const spawnRLo = region ? Math.max(0, region.rMin) : 0;
  const spawnRHi = region ? Math.min(maxZ, region.rMax) : maxZ;

  // 実際にシミュレートする雨粒数。region 指定時は選択面積比(mask 重みの総和 / 全格子点数)で
  // 按分し、狭い選択に同数を落として過剰侵食になるのを防ぐ。
  let dropletCount = params.droplets;
  if (region) {
    let weightSum = 0;
    for (let r = spawnRLo; r <= spawnRHi; r++) {
      const rowBase = r * cols;
      for (let c = spawnCLo; c <= spawnCHi; c++) weightSum += region.mask[rowBase + c];
    }
    const fraction = weightSum / (rows * cols);
    dropletCount = Math.max(1, Math.round(params.droplets * fraction));
  }

  for (let d = 0; d < dropletCount; d++) {
    // 陸地(標高>SEA_LEVEL)に落ちるまで数回だけ再抽選する。海ばかり引く粒は捨てる。
    // region 指定時はさらに選択重みを投下確率として使い、選択範囲に閉じ込める。
    let x = 0;
    let z = 0;
    let spawned = false;
    for (let retry = 0; retry < HYDRAULIC.spawnRetries; retry++) {
      x = spawnCLo + Math.random() * (spawnCHi - spawnCLo);
      z = spawnRLo + Math.random() * (spawnRHi - spawnRLo);
      sampleHeightAndGradient(elev, cols, x, z, hg);
      if (hg.height <= SEA_LEVEL) continue;
      if (region && Math.random() >= region.mask[Math.round(z) * cols + Math.round(x)]) continue;
      spawned = true;
      break;
    }
    if (!spawned) continue;

    let dirX = 0;
    let dirZ = 0;
    let speed = 1;
    let water = 1;
    let sediment = 0;

    for (let life = 0; life < HYDRAULIC.maxLifetime; life++) {
      sampleHeightAndGradient(elev, cols, x, z, hg);
      const oldHeight = hg.height;
      const oldX = x;
      const oldZ = z;

      // 勾配の逆方向(下り)に慣性を混ぜて進行方向を決める
      dirX = dirX * HYDRAULIC.inertia - hg.gradX * (1 - HYDRAULIC.inertia);
      dirZ = dirZ * HYDRAULIC.inertia - hg.gradZ * (1 - HYDRAULIC.inertia);
      const len = Math.sqrt(dirX * dirX + dirZ * dirZ);
      if (len < 1e-8) break; // 平坦地で行き場がない
      dirX /= len;
      dirZ /= len;
      x += dirX;
      z += dirZ;
      if (x < 0 || x >= maxX || z < 0 || z >= maxZ) break; // マップ外へ流出

      sampleHeightAndGradient(elev, cols, x, z, hg);

      // 海面到達の判定。着水した雨粒は、このステップの侵食・堆積を終えた後に消滅する
      // (末尾の reachedSea 分岐)。高低差(deltaH)は「海面に触れるまでの落差」でクランプ
      // する。雨粒は海面に触れた時点で流速を失うため、海面より下に続く海中の崖の落差まで
      // 侵食量・運搬容量に寄与させない(クランプしないと、深い海に面した断崖で1粒ごとに
      // 数十mも削れてしまう)。
      const reachedSea = hg.height <= SEA_LEVEL;
      const deltaH = (reachedSea ? SEA_LEVEL : hg.height) - oldHeight;
      // 無次元化した勾配(下りが正)。容量・速度の計算をマップの標高スケールに依存させない
      const slope = -deltaH / spacing;

      // 移動前位置の格子セル(侵食ブラシの中心に使う)
      const cx = Math.floor(oldX);
      const cz = Math.floor(oldZ);

      // 下り勾配・速度・水量に応じた運搬容量(単位はメートル=削れる標高量)
      const capacity = Math.max(slope, HYDRAULIC.minSlope) * speed * water * HYDRAULIC.capacityFactor;

      if (sediment > capacity || deltaH > 0) {
        // 容量超過または上り坂: 移動前の位置に堆積する。
        // 上り坂の場合は穴を埋めるように高低差分まで、それ以外は超過分の一部を置く
        const deposit =
          deltaH > 0 ? Math.min(deltaH, sediment) : (sediment - capacity) * HYDRAULIC.depositSpeed;
        sediment -= deposit;
        depositBilinear(elev, cols, oldX, oldZ, deposit);
      } else {
        // 容量に余裕がある: 移動前の位置周辺をブラシ状に削る。
        // 高低差以上は削らない(削りすぎると進行方向に穴が空いて不自然になるため)
        const erode = Math.min((capacity - sediment) * params.erodeStrength, -deltaH);
        for (const p of brush) {
          const br = cz + p.dr;
          const bc = cx + p.dc;
          if (br < 0 || br >= rows || bc < 0 || bc >= cols) continue;
          const bIdx = br * cols + bc;
          const amount = erode * p.weight;
          // 削れるのはそのセルの海面までの高さまで(侵食で海底を掘り下げない)
          const above = elev[bIdx] - SEA_LEVEL;
          const actual = above < amount ? Math.max(0, above) : amount;
          elev[bIdx] -= actual;
          sediment += actual;
        }
      }

      // 海にぶつかった雨粒は流速ゼロ=運搬容量ゼロとみなして終了し、運んでいた土砂は
      // 海中の沈降処理へ引き渡す(着水点に一括堆積させず、海底斜面を滑り落として沈める)。
      if (reachedSea) {
        settleSedimentUnderwater(elev, rows, cols, spacing, x, z, dirX, dirZ, sediment, hg);
        break;
      }

      // 下りで加速・上りで減速し、水は蒸発で徐々に減る
      speed = Math.sqrt(Math.max(0, speed * speed + slope * HYDRAULIC.gravity));
      water *= 1 - HYDRAULIC.evaporate;
    }
  }
}
