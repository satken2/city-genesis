/**
 * Budget mini meter (persistent in the cockpit)
 *
 * Shows the balance ratio of the city's latest period revenue against projected spending for the next epoch:
 * (revenue − projected spending) / revenue.
 * BalanceMeter's center means break-even; moving left indicates a projected deficit.
 *
 * - Refresh through backend REST polling every 10 seconds.
 * - Click to open CityBudgetWindow.
 *
 * Revenue sums every total_revenue key from the last completed epoch (taxes and public enterprise revenue,
 * excluding borrowing).
 * Projected spending uses the same derivation as the budget window's spending balance (utils/budgetForecast),
 * based on budgets derived from saved ratios and expected payments. The resulting balance matches the budget
 * window.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { deriveBudgets, expectedSpending, ratioPercent } from "../../utils/budgetForecast";
import { BalanceMeter } from "../common/BalanceMeter";
import "./hud.css";

/** Backend polling interval in milliseconds */
const POLL_INTERVAL_MS = 10_000;

type BudgetsResponse = {
  projected_required: Record<string, number>;
  projected_payment: Record<string, number>;
  category_ratio?: Record<string, number>;
  budget_tiers?: Record<string, number>;
  current_reserves?: number;
  total_revenue: Record<string, number>;
};

/** Total projected spending for the next epoch. */
function sumExpectedSpending(res: BudgetsResponse): number {
  const ratiosPct: Record<string, number> = {};
  for (const key of Object.keys(res.projected_required)) {
    ratiosPct[key] = ratioPercent(res.category_ratio?.[key]);
  }
  const { budgets } = deriveBudgets(
    ratiosPct,
    res.projected_required,
    res.budget_tiers ?? {},
    res.current_reserves ?? 0,
  );
  return Object.values(expectedSpending(budgets, res.projected_payment)).reduce((sum, v) => sum + v, 0);
}

export function BudgetMiniMeter({
  apiBaseUrl,
  onOpen,
}: {
  apiBaseUrl: string;
  onOpen: () => void;
}) {
  const { t } = useTranslation();
  const [revenue, setRevenue] = useState<number | null>(null);
  const [spending, setSpending] = useState<number | null>(null);

  useEffect(() => {
    if (!apiBaseUrl) return;

    const controller = new AbortController();
    let cancelled = false;

    async function refresh() {
      try {
        const res = await fetchApi<BudgetsResponse>(
          buildApiUrl(apiBaseUrl, "/api/v1/city/budgets"),
          { signal: controller.signal },
        );
        if (cancelled) return;
        setRevenue(Object.values(res.total_revenue).reduce((sum, v) => sum + v, 0));
        setSpending(sumExpectedSpending(res));
      } catch {
        // Keep the previous value on retrieval failure (including aborts) to avoid flicker.
      }
    }

    void refresh();
    const timer = setInterval(() => void refresh(), POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      controller.abort();
      clearInterval(timer);
    };
  }, [apiBaseUrl]);

  const hasData = revenue !== null && spending !== null;
  // Avoid division by zero when revenue <= 0. Spending without revenue is a deficit: use the left edge for positive projected spending, or center for zero.
  const balance =
    hasData && revenue! > 0
      ? (revenue! - spending!) / revenue!
      : hasData && spending! > 0
        ? -Infinity
        : 0;

  return (
    <button
      type="button"
      id="budget"
      className="hud-mini-meter"
      onClick={onOpen}
      title={t("taskbar.budgetMeterTooltip")}
    >
      <span className="hud-mini-meter__label">{t("taskbar.budgetMeterLabel")}</span>
      <div className="hud-mini-meter__bar">
        {hasData ? (
          <BalanceMeter value={balance} />
        ) : (
          <span className="hud-mini-meter__placeholder">—</span>
        )}
      </div>
    </button>
  );
}
