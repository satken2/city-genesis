/**
 * 建設中・解体中エンティティの「1エポックあたりの建設コスト」と「残りエポック数」を
 * 予算一覧エンドポイント(GET /api/v1/city/budgets/agents?category_id=建設)から取得するフック。
 *
 * 建設コスト・工期の算出ロジックはバックエンドの get_budget_agents に一元化されており、
 * 詳細パネル側は建物(kind="agent")・パス(kind="path")ともこのフックで同じ行データを参照する。
 * enabled=false の間(計画中など建設実績が無いステータス)は取得せず null を返す。
 */

import { useEffect, useState } from "react";
import { buildApiUrl, fetchApi } from "../utils/api";
import { LEDGER_CONSTRUCTION } from "../constants";

type BudgetAgentRow = {
  kind?: string;
  agent_id?: number;
  path_id?: number;
  construction_cost_per_epoch?: number;
  remaining_epochs?: number | null;
};

export function useConstructionBudgetRow(
  apiBaseUrl: string,
  kind: "agent" | "path",
  entityId: number | string,
  enabled: boolean,
  refreshKey: number,
): { costPerEpoch: number | null; remainingEpochs: number | null } {
  const [costPerEpoch, setCostPerEpoch] = useState<number | null>(null);
  const [remainingEpochs, setRemainingEpochs] = useState<number | null>(null);

  useEffect(() => {
    if (!enabled) {
      setCostPerEpoch(null);
      setRemainingEpochs(null);
      return;
    }
    const controller = new AbortController();
    const url = buildApiUrl(apiBaseUrl, `/api/v1/city/budgets/agents?category_id=${LEDGER_CONSTRUCTION}`);
    fetchApi<{ agents: BudgetAgentRow[] }>(url, { signal: controller.signal })
      .then((res) => {
        if (controller.signal.aborted) return;
        const row = res.agents.find(
          (a) =>
            a.kind === kind &&
            String(kind === "agent" ? a.agent_id : a.path_id) === String(entityId),
        );
        setCostPerEpoch(row?.construction_cost_per_epoch ?? null);
        setRemainingEpochs(row?.remaining_epochs ?? null);
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("Failed to fetch construction budget row:", err);
          setCostPerEpoch(null);
          setRemainingEpochs(null);
        }
      });
    return () => controller.abort();
  }, [apiBaseUrl, kind, entityId, enabled, refreshKey]);

  return { costPerEpoch, remainingEpochs };
}
