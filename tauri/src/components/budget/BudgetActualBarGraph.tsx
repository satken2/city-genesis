/**
 * 予算の計画・実績を3本組の横棒グラフで比較表示するコンポーネント
 *
 * 各予算カテゴリについて、以下の3本の横棒を縦に積んで表示する:
 * - 前回計画 (prev budget)       ... 薄色
 * - 前回実績 (prev actual)       ... 中間色
 * - 今回計画 (current budget)    ... 濃色
 *
 * 棒の長さは baseValue に対する割合で計算される。
 * カテゴリごとに hsl 色相を変え、3本の棒は透明度で区別する。
 */

import { useTranslation } from "react-i18next";
import "./BudgetActualBarGraph.css";

export type BudgetActualItem = {
  categoryId: number;
  /** 前エポック計画 (prev_budgets) */
  budget: number;
  /** 前エポック実績 (total_expense) */
  actual: number;
  /** 今エポック計画 (pendingBudgets) */
  currentBudget: number;
};

type BudgetActualBarGraphProps = {
  /** グラフに表示するカテゴリデータの配列 */
  items: BudgetActualItem[];
  /** 棒の100%幅となる基準値（例: 今エポックの現預金） */
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
