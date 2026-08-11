/**
 * 地形エディタの「自動編集」用の海底生成アルゴリズム。
 *
 * 国土地理院DEMのように「海面が一律標高0m」のデータを取り込むと、ゲーム側では
 * 海が存在しない(または極端に浅い海が広がる)地形になってしまう。そこで、
 * 海とみなしたマスを海岸線からの距離に応じて彫り下げ、陸の地形と標高0mで
 * 連続する自然な海底プロファイルを一括生成する。
 *
 * terrainErosion.ts と同じ方針で、React やキャンバス描画に依存しない純粋関数のみを
 * 置く。標高データは行優先の Float32Array(rows × cols、格子間隔 spacing メートル)を
 * 受け取り、配列を直接書き換える(in-place)。呼び出し側はテクスチャ・等高線の
 * 再構築を行うこと。
 *
 * 水深プロファイルは depth(d) = maxDepth × tanh(slope × d / maxDepth)。
 * 海岸(距離0)では水深0・傾き slope で陸側と連続し、沖へ向かうほど傾きが緩んで
 * maxDepth へ漸近する。折れ点のない滑らかな飽和曲線にするために tanh を使う。
 */

import type { ErosionRegion } from "./terrainErosion";

/** 海底生成のパラメータ(UIから調整可能なもののみ) */
export type SeaFloorParams = {
  /** この標高(m)以下のマスを海とみなす。海面が一律0mのデータなら0のままでよい */
  seaLevelThreshold: number;
  /** 海岸線の直近での海底勾配(%)。沖へ向かうほど緩んで最大水深へ漸近する */
  shelfSlopePercent: number;
  /** 最大水深(m、正の値)。どれだけ沖でもこれより深くはならない */
  maxDepth: number;
};

/**
 * 各マスから最寄りの陸マス(標高が閾値より高いマス)までの近似ユークリッド距離(m)を
 * チャンファー距離変換(前進・後退の2パス)で求める。陸マスは距離0。
 * 8近傍を直交=spacing・斜め=spacing×√2 のコストで伝播させる近似で、
 * 厳密なユークリッド距離との誤差は数%に収まるため水深の算出には十分。
 */
function computeDistanceToLand(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  seaLevelThreshold: number
): Float32Array {
  const dist = new Float32Array(rows * cols);
  for (let i = 0; i < dist.length; i++) {
    dist[i] = elev[i] > seaLevelThreshold ? 0 : Infinity;
  }

  const orth = spacing;
  const diag = spacing * Math.SQRT2;

  // 前進パス(左上→右下): 左・上・左上・右上の隣接から伝播する
  for (let r = 0; r < rows; r++) {
    const rowBase = r * cols;
    for (let c = 0; c < cols; c++) {
      const i = rowBase + c;
      let d = dist[i];
      if (d === 0) continue;
      if (c > 0) d = Math.min(d, dist[i - 1] + orth);
      if (r > 0) {
        d = Math.min(d, dist[i - cols] + orth);
        if (c > 0) d = Math.min(d, dist[i - cols - 1] + diag);
        if (c < cols - 1) d = Math.min(d, dist[i - cols + 1] + diag);
      }
      dist[i] = d;
    }
  }

  // 後退パス(右下→左上): 右・下・右下・左下の隣接から伝播する
  for (let r = rows - 1; r >= 0; r--) {
    const rowBase = r * cols;
    for (let c = cols - 1; c >= 0; c--) {
      const i = rowBase + c;
      let d = dist[i];
      if (d === 0) continue;
      if (c < cols - 1) d = Math.min(d, dist[i + 1] + orth);
      if (r < rows - 1) {
        d = Math.min(d, dist[i + cols] + orth);
        if (c < cols - 1) d = Math.min(d, dist[i + cols + 1] + diag);
        if (c > 0) d = Math.min(d, dist[i + cols - 1] + diag);
      }
      dist[i] = d;
    }
  }

  return dist;
}

/**
 * 海底生成を標高配列へ適用する。書き換えた海マスの数を返す(0なら海とみなせる
 * マスが無かった=閾値の調整が必要、という診断に使える)。
 *
 * 海の判定(標高≦閾値)と陸からの距離計算はマップ全体で行い、region(選択範囲)は
 * 「どのマスを書き換えるか」だけを制限する。選択境界を海岸線と誤認して選択の縁に
 * 偽の浅瀬ができないようにするため、距離計算を選択範囲へ閉じ込めることはしない。
 * 湖など海にしたくない0m地帯がある場合に、選択範囲で海側だけを囲んで使う。
 *
 * 対象マスの標高は水深プロファイルの値へ置き換える(加算ではない)ため、パラメータを
 * 変えて何度実行し直しても結果は同じプロファイルに収束する。選択範囲の境界のにじみ
 * (重み0..1)は現在標高とプロファイルの線形補間として効く。
 */
export function applySeaFloor(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  params: SeaFloorParams,
  region?: ErosionRegion
): number {
  const maxDepth = Math.max(1, params.maxDepth);
  const slope = Math.max(1e-4, params.shelfSlopePercent / 100);
  const threshold = params.seaLevelThreshold;

  const dist = computeDistanceToLand(elev, rows, cols, spacing, threshold);

  const rMin = region ? region.rMin : 0;
  const rMax = region ? region.rMax : rows - 1;
  const cMin = region ? region.cMin : 0;
  const cMax = region ? region.cMax : cols - 1;

  let modified = 0;
  for (let r = rMin; r <= rMax; r++) {
    const rowBase = r * cols;
    for (let c = cMin; c <= cMax; c++) {
      const i = rowBase + c;
      if (elev[i] > threshold) continue;
      const weight = region ? region.mask[i] : 1;
      if (weight <= 0) continue;
      // 陸が1マスも無いマップでは dist=Infinity → tanh(Infinity)=1 → 最大水深になる
      const target = -maxDepth * Math.tanh((slope * dist[i]) / maxDepth);
      elev[i] += (target - elev[i]) * weight;
      modified++;
    }
  }
  return modified;
}
