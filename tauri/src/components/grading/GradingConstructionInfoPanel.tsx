/**
 * Construction tab for an earthworks agent (AgentDetailPanel selects this or BuildingConstructionInfoPanel via
 * isGradingVariant).
 *
 * Get estimates from GET /api/v1/grading/{agent_id}, which uses the same function and keys as the earthworks
 * preview.
 * The preview API rejects overlaps with existing parcels, so it cannot serve an already ordered agent occupying
 * its own parcel.
 * Get cost per epoch and remaining construction time from the budget list's construction rows, as for buildings.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useConstructionBudgetRow } from "../../hooks/useConstructionBudgetRow";
import { ConstructionInfoCard } from "../common/ConstructionInfoCard";
import { STATUS_CONSTRUCTING } from "../../constants";
import { gradingInfoRows, type GradingEstimate } from "./grading";

type GradingConstructionInfoPanelProps = {
  agent: AgentData;
  apiBaseUrl: string;
  refreshKey: number;
};

export function GradingConstructionInfoPanel({
  agent,
  apiBaseUrl,
  refreshKey,
}: GradingConstructionInfoPanelProps) {
  const { t } = useTranslation();
  const [estimate, setEstimate] = useState<GradingEstimate | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    fetchApi<GradingEstimate>(buildApiUrl(apiBaseUrl, `/api/v1/grading/${agent.agent_id}`), {
      signal: controller.signal,
    })
      .then((data) => {
        if (!controller.signal.aborted) setEstimate(data);
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("Failed to fetch grading estimate:", err);
          setEstimate(null);
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [agent.agent_id, apiBaseUrl, refreshKey]);

  const isConstructing = agent.status === STATUS_CONSTRUCTING;
  const { costPerEpoch, remainingEpochs } = useConstructionBudgetRow(
    apiBaseUrl,
    "agent",
    agent.agent_id,
    isConstructing,
    refreshKey,
  );

  return (
    <section className="agent-detail-section">
      <ConstructionInfoCard
        title={t("construction.info")}
        leadingRows={gradingInfoRows(t, estimate, loading)}
        estimatedCost={estimate?.estimated_construction_cost ?? null}
        standardCost={null}
        estimatedDurationDays={estimate?.estimated_duration_days ?? null}
        isCostLoading={loading}
        perEpoch={
          isConstructing
            ? { costPerEpoch, remainingEpochs, isDemolishing: false }
            : undefined
        }
      />
    </section>
  );
}
