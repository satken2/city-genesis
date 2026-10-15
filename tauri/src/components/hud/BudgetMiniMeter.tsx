/**
 * 予算ミニメーター（コックピット常駐）
 *
 * 直近1期の市の「収入」に対する「予算見込み額（必要額）」の割合を、常時コックピットに表示する
 * 小型メーター。市の収支を一目で示すためのもので、100% が収支トントン、超えれば赤字見込みを表す。
 *
 * - メーター全体の横幅 = 直近1期の収入、色塗り幅 = 予算見込み額（ratio = 見込み / 収入）
 * - ratio < 0.9: 緑 / 0.9〜1.0: 黄 / 1.0超（赤字見込み）: 赤 ＋ 枠線も赤
 * - 10秒に1回バックエンドをREST取得して最新化する
 * - クリックで CityBudgetWindow を開く
 *
 * 収入は直前に完了したエポックの total_revenue の全キー合計（税と公営事業収入。借入は含まない）。
 * 見込み額は CityBudgetWindow の基準額と同じ算出元のカテゴリ別必要額（projected_required）の
 * 合計で、建設・修繕・用地買収も含める（貯蓄は支出ではないので除外）。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { LEDGER_RESERVE } from "../../constants";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { Meter } from "../common/Meter";
import "./hud.css";

/** バックエンドのポーリング間隔（ミリ秒） */
const POLL_INTERVAL_MS = 10_000;

type BudgetsResponse = {
  projected_required: Record<string, number>;
  total_revenue: Record<string, number>;
};

/** 貯蓄カテゴリを除いたカテゴリ別必要額の合計を予算見込み額として求める。 */
function sumProjected(projectedRequired: Record<string, number>): number {
  let total = 0;
  for (const [key, value] of Object.entries(projectedRequired)) {
    if (Number(key) === LEDGER_RESERVE) continue;
    total += value;
  }
  return total;
}

/** ratio に応じた塗り色を返す。1.0超=赤（赤字見込み）、0.9以上=黄、それ未満=緑。 */
function getFillColor(ratio: number): string {
  if (ratio > 1) return "#ef4444";
  if (ratio >= 0.9) return "#eab308";
  return "#22c55e";
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
  const [projected, setProjected] = useState<number | null>(null);

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
        setProjected(sumProjected(res.projected_required));
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

  const hasData = revenue !== null && projected !== null;
  // 収入<=0 のときは 0 除算を避ける。収入なしで支出するのは赤字なので見込みが正なら赤、見込み0なら緑。
  const ratio =
    hasData && revenue! > 0
      ? projected! / revenue!
      : hasData && projected! > 0
        ? Infinity
        : 0;
  const over = ratio > 1;
  const fillColor = getFillColor(ratio);

  return (
    <button
      type="button"
      id="budget"
      className={`hud-mini-meter${over ? " hud-mini-meter--alert" : ""}`}
      onClick={onOpen}
      title={t("taskbar.budgetMeterTooltip")}
    >
      <span className="hud-mini-meter__label">{t("taskbar.budgetMeterLabel")}</span>
      <div className="hud-mini-meter__bar">
        {hasData ? (
          <Meter value={ratio} color={fillColor} />
        ) : (
          <span className="hud-mini-meter__placeholder">—</span>
        )}
      </div>
    </button>
  );
}
