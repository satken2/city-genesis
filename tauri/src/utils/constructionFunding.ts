/**
 * 公共工事の発注時の資金審査(UI側の先回り判定)。
 *
 * バックエンドはコストプレビューAPIで「利用可能な資金」(available_funds =
 * 市の現預金 − 貯蓄目標 − 既存案件の拘束額)を返し、発注API側でも同じ基準で拒否する。
 * UIは確定ボタンを押す前に同じ比較を行い、資金不足なら確定を無効化して不足額を示す。
 * どちらかの値が未取得(null)の間はUIでは止めない(最終判定はバックエンドが行う)。
 */
export function isConstructionAffordable(
  requiredTotal: number | null | undefined,
  availableFunds: number | null | undefined,
): boolean {
  if (requiredTotal == null || availableFunds == null) return true;
  if (requiredTotal <= 0) return true;
  return requiredTotal <= availableFunds;
}

/** 不足額(0以上)。判定材料が揃っていなければ null。 */
export function constructionFundsShortfall(
  requiredTotal: number | null | undefined,
  availableFunds: number | null | undefined,
): number | null {
  if (requiredTotal == null || availableFunds == null) return null;
  return Math.max(requiredTotal - availableFunds, 0);
}
