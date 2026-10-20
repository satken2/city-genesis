/**
 * Compare budget plans and actuals with grouped horizontal bars
 *
 * For each budget category, stack three bars vertically:
 * - Previous plan (prev budget): light.
 * - Previous actual (prev actual): medium.
 * - Current plan (current budget): dark.
 *
 * Bar length is proportional to baseValue.
 * Use a different HSL hue per category and opacity to distinguish the three bars.
 */

import { useTranslation } from "react-i18next";
import "./BudgetActualBarGraph.css";

export type BudgetActualItem = {
  categoryId: number;
  /** Previous epoch plan (prev_budgets). */
  budget: number;
  /** Previous epoch actual (total_expense). */
  actual: number;
  /** Current epoch plan (pendingBudgets). */
  currentBudget: number;
};

type BudgetActualBarGraphProps = {
  /** Array of category data to display in the chart. */
  items: BudgetActualItem[];
  /** Reference value representing 100% bar width (e.g. cash for the current epoch). */
  baseValue: number;
};

export function BudgetActualBarGraph({ items, baseValue }: BudgetActualBarGraphProps) {
  const { t } = useTranslation();

  if (baseValue <= 0) return null;

  const sortedItems = [...items].sort((a, b) => a.categoryId - b.categoryId);

  return (
    <div className="budget-actual-graph">
      <div className="budget-actual-graph__legend">
        <span className="budget-actual-graph__legend-item">
          <span className="budget-actual-graph__legend-mark budget-actual-graph__legend-mark--budget" />
          {t("budget.prevBudget")}
        </span>
        <span className="budget-actual-graph__legend-item">
          <span className="budget-actual-graph__legend-mark budget-actual-graph__legend-mark--actual" />
          {t("budget.prevActual")}
        </span>
        <span className="budget-actual-graph__legend-item">
          <span className="budget-actual-graph__legend-mark budget-actual-graph__legend-mark--current" />
          {t("budget.currentBudget")}
        </span>
      </div>
      {sortedItems.map((item) => {
        const hue = (item.categoryId * 50) % 360;
        const budgetPct = Math.min((item.budget / baseValue) * 100, 100);
        const actualPct = Math.min((item.actual / baseValue) * 100, 100);
        const currentPct = Math.min((item.currentBudget / baseValue) * 100, 100);

        return (
          <div key={item.categoryId} className="budget-actual-graph__row">
            <span className="budget-actual-graph__cat-label">
              {t(`budget.category.${item.categoryId}`)}
            </span>
            <div className="budget-actual-graph__bars">
              <div
                className="budget-actual-graph__bar"
                style={{
                  width: `${budgetPct}%`,
                  backgroundColor: `hsla(${hue}, 50%, 55%, 0.3)`,
                }}
                title={`${t("budget.prevBudget")}: ${Math.round(item.budget).toLocaleString()}`}
              />
              <div
                className="budget-actual-graph__bar"
                style={{
                  width: `${actualPct}%`,
                  backgroundColor: `hsla(${hue}, 50%, 55%, 0.6)`,
                }}
                title={`${t("budget.prevActual")}: ${Math.round(item.actual).toLocaleString()}`}
              />
              <div
                className="budget-actual-graph__bar"
                style={{
                  width: `${currentPct}%`,
                  backgroundColor: `hsl(${hue}, 50%, 55%)`,
                }}
                title={`${t("budget.currentBudget")}: ${Math.round(item.currentBudget).toLocaleString()}`}
              />
            </div>
          </div>
        );
      })}
    </div>
  );
}
