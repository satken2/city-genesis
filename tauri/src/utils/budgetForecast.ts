/**
 * 次エポックの予算と支出見込みの導出。予算ウィンドウ(CityBudgetWindow)とコックピットの
 * 予算ミニメーター(BudgetMiniMeter)が共有し、両者の示す収支を一致させる。
 */

import { LEDGER_RESERVE } from "../constants";

const RESERVE_KEY = String(LEDGER_RESERVE);

/**
 * 予算比率スライダーの上限(%)。全カテゴリ共通で必要額の150%まで
 * (100%超は見積もり誤差やエポック中の価格・賃金変動へのヘッドルーム)。
 */
export const RATIO_MAX_PERCENT = 150;

/** バックエンドの比率(category_ratio。0〜1.5、未設定は100%)を比率スライダーの％にする。 */
export function ratioPercent(fraction: number | undefined): number {
  return Math.min(fraction !== undefined ? Math.round(fraction * 100) : 100, RATIO_MAX_PERCENT);
}

/**
 * カテゴリの維持優先度（Tier）を返す。
 * Tier は数字が小さいほど優先的に維持され、大きいほど先に削減される
 * （1=最優先維持〜4=最初に削減。バックエンドと同一の番号体系）。
 * 未設定カテゴリはバックエンド City.budget_tiers の既定値と同じフォールバックを使う。
 */
export function getCategoryTier(key: string, tiers: Record<string, number>): number {
  if (tiers[key] !== undefined) {
    return tiers[key];
  }
  const cId = Number(key);
  if (cId === 10) return 4; // 貯蓄: 最初に削減（希望0のため実質無効）
  if (cId === 8 || cId === 14) return 3; // 建設・用地買収
  if ([3, 4, 5, 7, 11].includes(cId)) return 2; // 教育・治安・消防・その他・交通
  return 1; // 医療・インフラ・修繕: 最優先維持
}

/**
 * プレイヤーが設定した「必要額に対する比率(％)」とカテゴリ別の必要額(基準額)から
 * 実際に配分される予算額(円)を導出する。バックエンドの City.maintain_and_scale_budget を
 * 忠実に反映したプレビュー計算：desired = 基準額 × 比率(0〜150%)、希望合計が配分可能額
 * (現金)を超える場合は維持優先度の低い Tier 4→1 の順に削減して収める
 * （＝資金不足時の自動調整。Tier 1 が最後まで維持される）。
 * 基準額の合計が0の場合(初回エポック等)は全カテゴリ均等配分のブートストラップをミラーする。
 *
 * 返り値の desired は削減前のプレイヤー希望額であり、収支バランスの「圧縮前の支出」バー
 * (現金不足で削られた分を薄く描く3本目の棒)のデータ源になる。
 */
export function deriveBudgets(
  ratiosPct: Record<string, number>,
  base: Record<string, number>,
  tiers: Record<string, number>,
  reserves: number,
): {
  budgets: Record<string, number>;
  desired: Record<string, number>;
  autoAdjusted: boolean;
} {
  const keys = Object.keys(base).filter((k) => k !== RESERVE_KEY);

  const budgets: Record<string, number> = {};
  for (const k of keys) budgets[k] = 0;
  budgets[RESERVE_KEY] = 0;

  // プレイヤー希望額(削減前)。現金の多寡に関係なく 基準額 × 比率 で決まる。
  const desired: Record<string, number> = {};
  for (const k of keys) {
    const b = Math.max(base[k] ?? 0, 0);
    const ratio = Math.min(Math.max((ratiosPct[k] ?? 100) / 100, 0), RATIO_MAX_PERCENT / 100);
    desired[k] = b * ratio;
  }

  if (reserves <= 0) {
    // 債務超過: バックエンドは全予算を0にする(希望額はそのまま圧縮前として表示する)。
    return { budgets, desired, autoAdjusted: false };
  }

  const totalBase = keys.reduce((s, k) => s + Math.max(base[k] ?? 0, 0), 0);
  if (totalBase <= 1e-8) {
    // 実績も必要量も未捕捉(初回エポック等): バックエンドと同じく現金を均等配分する。
    const even = keys.length > 0 ? reserves / keys.length : 0;
    for (const k of keys) budgets[k] = even;
    return { budgets, desired: { ...budgets }, autoAdjusted: false };
  }

  for (const k of keys) budgets[k] = desired[k];

  const available = Math.max(reserves, 0);
  const totalDesired = keys.reduce((s, k) => s + budgets[k], 0);

  let autoAdjusted = false;
  if (totalDesired > available + 1e-6) {
    autoAdjusted = true;
    let deficit = totalDesired - available;
    // 維持優先度の低い Tier 4 から順に削減する（Tier 1 が最後まで維持される）
    for (let tier = 4; tier >= 1 && deficit > 1e-6; tier--) {
      const tierKeys = keys.filter((k) => getCategoryTier(k, tiers) === tier);
      const tierSum = tierKeys.reduce((s, k) => s + budgets[k], 0);
      if (tierSum <= 1e-8) continue;
      if (tierSum <= deficit) {
        for (const k of tierKeys) budgets[k] = 0;
        deficit -= tierSum;
      } else {
        const scale = (tierSum - deficit) / tierSum;
        for (const k of tierKeys) budgets[k] *= scale;
        deficit = 0;
      }
    }
  }

  return { budgets, desired, autoAdjusted };
}

/**
 * カテゴリごとの次エポックの支出見込み = min(予算, 支払見込み)。
 * 予算は支出の上限で、それを超える支出は計画の縮小や残予算での打ち切りで止まり、
 * 予算が余っても支払見込み(在庫で賄う原材料を除いた額)より多くは出ていかない。
 */
export function expectedSpending(
  budgets: Record<string, number>,
  payments: Record<string, number>,
): Record<string, number> {
  const spending: Record<string, number> = {};
  for (const [key, budget] of Object.entries(budgets)) {
    spending[key] = Math.min(budget, payments[key] ?? 0);
  }
  return spending;
}
