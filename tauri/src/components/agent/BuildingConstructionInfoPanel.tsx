/**
 * Building construction information component
 *
 * Displayed in the detail panel for planned, under-construction, or demolishing agents, etc.
 * Shows building footprint area, height, total floor area, estimated construction cost and time.
 * During construction/demolition, also fetches cost per epoch and remaining epochs.
 * Uses ConstructionInfoCard, shared with path details, for the actual display.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useConstructionBudgetRow } from "../../hooks/useConstructionBudgetRow";
import { ConstructionInfoCard } from "../common/ConstructionInfoCard";
import {
  STATUS_CONSTRUCTING,
  STATUS_DEMOLISHING,
} from "../../constants";

type BuildingConstructionInfoPanelProps = {
  agent: AgentData;
  apiBaseUrl: string;
  refreshKey: number;
};

export function BuildingConstructionInfoPanel({
  agent,
  apiBaseUrl,
  refreshKey,
}: BuildingConstructionInfoPanelProps) {
  const { t } = useTranslation();
  const [costData, setCostData] = useState<{
    estimated_construction_cost: number | null;
    standard_construction_cost: number | null;
    estimated_duration_days: number | null;
  } | null>(null);
  const [loading, setLoading] = useState(false);

  // Fetch the estimated construction cost (POST /api/v1/buildings/construction-cost).
  useEffect(() => {
    if (agent.variant == null || agent.parcel_id == null || agent.floors == null) {
      return;
    }
    const controller = new AbortController();
    setLoading(true);

    const url = buildApiUrl(apiBaseUrl, "/api/v1/buildings/construction-cost");
    fetchApi<{
      estimated_construction_cost: number | null;
      standard_construction_cost: number | null;
      estimated_duration_days: number | null;
    }>(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        variant: agent.variant,
        parcel_id: agent.parcel_id,
        floors: agent.floors,
        construction_ratio: 0.6, // Default building coverage ratio.
      }),
      signal: controller.signal,
    })
      .then((data) => {
        if (!controller.signal.aborted) {
          setCostData({
            estimated_construction_cost: data.estimated_construction_cost ?? null,
            standard_construction_cost: data.standard_construction_cost ?? null,
            estimated_duration_days: data.estimated_duration_days ?? null,
          });
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("Failed to fetch building construction cost:", err);
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      });

    return () => controller.abort();
  }, [agent.variant, agent.parcel_id, agent.floors, apiBaseUrl, refreshKey]);

  // Fetch cost per epoch and remaining construction time only during construction/demolition.
  const isCsOrDm = agent.status === STATUS_CONSTRUCTING || agent.status === STATUS_DEMOLISHING;
  const { costPerEpoch, remainingEpochs } = useConstructionBudgetRow(
    apiBaseUrl,
    "agent",
    agent.agent_id,
    isCsOrDm,
    refreshKey,
  );

  return (
    <section className="agent-detail-section">
      <ConstructionInfoCard
        title={t("construction.info")}
        buildingArea={agent.base_area ?? null}
        totalHeight={agent.height ?? null}
        totalFloorArea={agent.floor_area ?? null}
        estimatedCost={costData?.estimated_construction_cost ?? null}
        standardCost={costData?.standard_construction_cost ?? null}
        costRatioHelp={t("building_construction.costRatioHelp")}
        estimatedDurationDays={costData?.estimated_duration_days ?? null}
        isCostLoading={loading}
        perEpoch={
          isCsOrDm
            ? {
                costPerEpoch,
                remainingEpochs,
                isDemolishing: agent.status === STATUS_DEMOLISHING,
              }
            : undefined
        }
      />
    </section>
  );
}
