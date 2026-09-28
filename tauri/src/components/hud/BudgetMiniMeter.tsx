/**
 * 予算ミニメーター（コックピット常駐）
 *
 * 市政府の「使用可能な現預金」に対する「予算見込み額（フル稼働必要額）」の割合を、
 * 常時コックピットに表示する小型メーター。プレイヤーが CityBudgetWindow を開かなくても
 * 資金繰りが健全か（見込み費用が現預金に収まっているか）を一目で把握できる。
 *
 * - メーター全体の横幅 = 現預金、色塗り幅 = 予算見込み額（ratio = 見込み / 現預金）
 * - ratio < 0.9: 緑 / 0.9〜1.0: 黄 / 1.0超（予算オーバー）: 赤 ＋ 枠線も赤
 * - 10秒に1回バックエンドをREST取得して最新化する
 * - クリックで CityBudgetWindow を開く
 *
 * 見込み額は CityBudgetWindow の computeBaseAmounts と同一ロジック（施設別 projected_expense の
 * 単純合計。category_id=10「貯蓄」は除外）で算出する。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { Meter } from "../common/Meter";
import "./hud.css";

/** 貯蓄カテゴリ。見込み額の集計から除外する（CityBudgetWindow の LEDGER_RESERVE_KEY と同一）。 */
const LEDGER_RESERVE_CATEGORY = 10;

/** バックエンドのポーリング間隔（ミリ秒） */
const POLL_INTERVAL_MS = 10_000;

type CityResponse = {
  city: {
    city_agent_id: number;
  };
};

type CityAgentResponse = {
  economic_agent: {
    reserves: number;
  };
};

type BudgetAgentRow = {
  category_id: number;
  projected_expense: number;
};

type BudgetAgentsResponse = {
  agents: BudgetAgentRow[];
};

/**
 * 施設別 projected_expense の単純合計を予算見込み額として求める。
 * 貯蓄カテゴリ（10）は除外する（CityBudgetWindow.computeBaseAmounts と同一）。
 */
function sumProjected(agents: BudgetAgentRow[]): number {
  let total = 0;
  for (const agent of agents) {
    if (agent.category_id === LEDGER_RESERVE_CATEGORY) continue;
    total += agent.projected_expense;
  }
  return total;
}

/** ratio に応じた塗り色を返す。1.0超=赤（オーバー）、0.9以上=黄、それ未満=緑。 */
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
  const [reserves, setReserves] = useState<number | null>(null);
  const [projected, setProjected] = useState<number | null>(null);

  useEffect(() => {
    if (!apiBaseUrl) return;

    const controller = new AbortController();
    let cancelled = false;

    async function refresh() {
      try {
        const cityRes = await fetchApi<CityResponse>(
          buildApiUrl(apiBaseUrl, "/api/v1/city"),
          { signal: controller.signal },
        );
        const cityAgentId = cityRes.city.city_agent_id;
        const [agentRes, budgetAgentsRes] = await Promise.all([
          fetchApi<CityAgentResponse>(
            buildApiUrl(apiBaseUrl, `/api/v1/agents/${cityAgentId}`),
            { signal: controller.signal },
          ),
          fetchApi<BudgetAgentsResponse>(
            buildApiUrl(apiBaseUrl, "/api/v1/city/budgets/agents"),
            { signal: controller.signal },
          ),
        ]);

        if (cancelled) return;
        setReserves(agentRes.economic_agent.reserves);
        setProjected(sumProjected(budgetAgentsRes.agents));
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

  const hasData = reserves !== null && projected !== null;
  // reserves<=0 のときは 0 除算を避ける。見込みが正なら実質オーバー、見込み0なら健全（緑）扱い。
  const ratio =
    hasData && reserves! > 0
      ? projected! / reserves!
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
