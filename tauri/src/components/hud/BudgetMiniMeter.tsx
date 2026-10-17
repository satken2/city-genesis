/**
 * 予算ミニメーター（コックピット常駐）
 *
 * 直近1期の市の「収入」に対する「次エポックの支出見込み」の収支率（(収入 − 支出見込み) / 収入）を、
 * 常時コックピットに表示する小型メーター。市の収支を一目で示すためのもので、BalanceMeter の中央が
 * 収支トントン、左へ振れるほど赤字見込みを表す。
 *
 * - 10秒に1回バックエンドをREST取得して最新化する
 * - クリックで CityBudgetWindow を開く
 *
 * 収入は直前に完了したエポックの total_revenue の全キー合計（税と公営事業収入。借入は含まない）。
 * 支出見込みは CityBudgetWindow の収支バランスの支出と同じ導出（utils/budgetForecast）で、
 * 保存済みの比率で導いた予算と支払見込みから求める。収支差は予算ウィンドウと一致する。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { deriveBudgets, expectedSpending, ratioPercent } from "../../utils/budgetForecast";
import { BalanceMeter } from "../common/BalanceMeter";
import "./hud.css";

/** バックエンドのポーリング間隔（ミリ秒） */
const POLL_INTERVAL_MS = 10_000;

type BudgetsResponse = {
  projected_required: Record<string, number>;
  projected_payment: Record<string, number>;
  category_ratio?: Record<string, number>;
  budget_tiers?: Record<string, number>;
  current_reserves?: number;
  total_revenue: Record<string, number>;
};

/** 次エポックの支出見込みの合計。 */
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
        // 取得失敗（中断含む）は前回値を保持し、画面をちらつかせない。
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
  // 収入<=0 のときは 0 除算を避ける。収入なしで支出するのは赤字なので見込みが正なら左端、見込み0なら中央。
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
