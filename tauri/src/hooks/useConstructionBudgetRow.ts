/**
 * Fetch construction cost per epoch and remaining epochs for entities under construction or demolition from the
 * budget list endpoint (GET /api/v1/city/budgets/agents?category_id=construction). The backend get_budget_agents
 * centralizes cost/construction time calculation. Building (kind="agent") and path (kind="path") detail panels
 * share these row data through this hook. When enabled=false (such as planned status without construction
 * actuals), skip fetching and return null.
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
