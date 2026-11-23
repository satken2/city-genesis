import { GRADING_VARIANTS, PARCEL_WATER } from "../constants";
import type { AgentData } from "../types";
import { buildApiUrl, fetchApi } from "./api";

/**
 * 道路からの離れすぎ警告を出す接道距離(m)のしきい値。
 * コアの接道アクセスペナルティ(既定 1m=1分、上限30分)では10m離れると
 * 通勤・買い物・物資調達に片道10分以上が上乗せされる。路面辺の余裕幅(3m)を
 * 十分に超え、明確に離れすぎている区画だけを対象にする。
 */
export const ROAD_ACCESS_WARNING_DISTANCE_M = 10;

export type RoadAccessWarning =
  | { kind: "noRoad" }
  | { kind: "farFromRoad"; distanceM: number };

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

/**
 * 建物の立地区画が道路から離れすぎているか(または接道道路がないか)を判定する。
 * コアが計算済みの接道距離(front_path_distance)と接道ID(front_path_id)のみを使い、
 * 接道を持たない海上区画(PARCEL_WATER)と造成工事は対象外とする。
 */
export function evaluateRoadAccessWarning(
  agent: Pick<
    AgentData,
    "is_bd" | "variant" | "parcel_id" | "parcel_type" | "front_path_id" | "front_path_distance"
  >,
): RoadAccessWarning | null {
  if (!agent.is_bd || (agent.parcel_id ?? 0) <= 0) return null;
  if (GRADING_VARIANTS.includes(agent.variant)) return null;
  if (agent.parcel_type === PARCEL_WATER) return null;
  if (agent.front_path_id == null && agent.front_path_distance == null) return null;
  if (agent.front_path_id != null && agent.front_path_id <= 0) {
    return { kind: "noRoad" };
  }
  const dist = agent.front_path_distance;
  if (dist != null && Number.isFinite(dist) && dist >= ROAD_ACCESS_WARNING_DISTANCE_M) {
    return { kind: "farFromRoad", distanceM: Math.round(dist) };
  }
  return null;
}
