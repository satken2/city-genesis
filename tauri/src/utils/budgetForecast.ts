/**
 * Derive next-epoch budgets and spending forecasts. Shared by CityBudgetWindow and the cockpit BudgetMiniMeter
 * so both show the same balance.
 */

import { LEDGER_RESERVE } from "../constants";

const RESERVE_KEY = String(LEDGER_RESERVE);

/**
 * Budget ratio slider maximum (%): 150% of required funds for every category. Above 100% provides headroom for
 * estimation error and price/wage changes during the epoch.
 */
export const RATIO_MAX_PERCENT = 150;

/** Convert backend category_ratio (0–1.5, default 100%) to slider percent. */
export function ratioPercent(fraction: number | undefined): number {
  return Math.min(fraction !== undefined ? Math.round(fraction * 100) : 100, RATIO_MAX_PERCENT);
}

/**
 * Return a category's preservation priority (Tier). Lower tiers are preserved first; higher tiers are cut first
 * (1=highest preservation priority through 4=first cut, matching backend numbering). Unset categories use the
 * same fallback as backend City.budget_tiers.
 */
export function getCategoryTier(key: string, tiers: Record<string, number>): number {
  if (tiers[key] !== undefined) {
    return tiers[key];
  }
  const cId = Number(key);
  if (cId === 10) return 4; // Savings: cut first (effectively disabled because desired is zero).
  if (cId === 8 || cId === 14) return 3; // Construction and land acquisition
  if ([3, 4, 5, 7, 11].includes(cId)) return 2; // Education, public safety, fire services, other, transport
  return 1; // Healthcare, infrastructure, repairs: highest preservation priority
}

/**
 * Derive actual allocated budgets in yen from player ratios (%) and category required amounts (base amounts).
 * Preview faithfully mirrors backend City.maintain_and_scale_budget: desired = base × ratio (0–150%). If desired
 * total exceeds available cash, cut lowest-priority tiers in order 4→1, preserving Tier 1 longest. If total base
 * amount is zero (such as the first epoch), mirror equal-allocation bootstrapping.
 *
 * Returned desired values are player requests before cuts, feeding the balance chart's pre-compression spending
 * bar (the third bar, where cash-related cuts appear faintly).
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

  // Player-requested amount before cuts. Determined by base × ratio regardless of available cash.
  const desired: Record<string, number> = {};
  for (const k of keys) {
    const b = Math.max(base[k] ?? 0, 0);
    const ratio = Math.min(Math.max((ratiosPct[k] ?? 100) / 100, 0), RATIO_MAX_PERCENT / 100);
    desired[k] = b * ratio;
  }

  if (reserves <= 0) {
    // Insolvency: the backend sets all budgets to zero (still display desired amounts before compression).
    return { budgets, desired, autoAdjusted: false };
  }

  const totalBase = keys.reduce((s, k) => s + Math.max(base[k] ?? 0, 0), 0);
  if (totalBase <= 1e-8) {
    // No actuals or requirements yet (such as the first epoch): divide cash equally, matching the backend.
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
    // Cut lowest preservation priority first, from Tier 4 (preserve Tier 1 longest).
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
 * Next-epoch spending forecast per category = min(budget, expected payments). Budget caps spending through
 * scaled-down plans or stopping at remaining funds. Unused budget cannot increase spending above expected
 * payments (excluding materials covered by inventory).
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
