import type { AgentData } from "../types";
import { buildApiUrl, fetchApi } from "./api";

/** Spatial warning returned by Unity's alert API. warning uses a separate bit field from agent alert. */
export type SpatialWarning = {
  entity_type: string;
  id: number;
  warning: number;
};

type AlertsResponse = {
  warnings?: SpatialWarning[];
};

export async function fetchSpatialWarnings(
  apiBaseUrl: string,
  signal?: AbortSignal,
): Promise<SpatialWarning[]> {
  const response = await fetchApi<AlertsResponse>(
    buildApiUrl(apiBaseUrl, "/api/v1/alerts/"),
    { signal },
  );
  return response.warnings ?? [];
}

/** Select the same warnings as Unity by building/containing parcel ID, without reevaluating thresholds such as sanitation. */
export function buildingWarningMask(
  agent: Pick<AgentData, "agent_id" | "is_bd" | "parcel_id">,
  warnings: SpatialWarning[],
): number {
  if (!agent.is_bd) return 0;
  const targets: Partial<Record<string, number>> = {
    agent: agent.agent_id,
    parcel: agent.parcel_id ?? 0,
  };
  return warnings.reduce((mask, item) => {
    const targetId = targets[item.entity_type];
    return targetId != null && targetId > 0 && targetId === item.id
      ? mask | item.warning
      : mask;
  }, 0);
}
