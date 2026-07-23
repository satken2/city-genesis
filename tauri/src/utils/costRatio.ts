/**
 * 建設コストの「標準コスト（完全に平坦・標高ゼロで建てた場合のコスト）に対する倍率」の
 * 色分けロジック。建物建設パネル・道路建設パネルで共用する。
 *
 * 倍率 = 推定建設コスト ÷ 標準コスト。1.0 が平地相当（割高分なし）。
 * 高架・地下構造や急峻な地形ほど倍率が上がる。
 */

/** これ未満は緑（平地並みで妥当）。 */
export const COST_RATIO_GREEN_MAX = 2.0;
/** これ未満は黄（やや割高）。以上は赤（大幅に割高）。 */
export const COST_RATIO_YELLOW_MAX = 4.0;

/**
 * 倍率に対応する文字色を返す。
 * 緑 (< 2.0) → 黄 (2.0〜4.0) → 赤 (>= 4.0)。
 */
export function costRatioColor(ratio: number): string {
  if (ratio < COST_RATIO_GREEN_MAX) return "#5bc46a"; // 緑: 平地並み
  if (ratio < COST_RATIO_YELLOW_MAX) return "#e0b23c"; // 黄: やや割高
  return "#e0603c"; // 赤: 大幅に割高
}
