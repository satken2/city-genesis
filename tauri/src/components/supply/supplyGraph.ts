import type { AgentData, MasterData } from "../../types";
import { GOODS_INFRASTRUCTURE, GOODS_RESOURCE, GOODS_TRANSIT, VARIANT_TRANSIT_LINE } from "../../constants";

export type SupplyState = "ok" | "unknown" | "missing" | "inactive" | "partial" | "limited"
  | "disconnected" | "supply_shortage" | "capacity_limited";

export type SupplyNode = {
  id: string;
  kind: "facility" | "resource" | "consumer";
  role: "resource" | "producer" | "consumer" | "vehicle_consumer";
  selected: boolean;
  name?: string;
  variant?: number;
  path_variant?: number;
  product_id: number;
  state: SupplyState;
  count: number;
  active_count: number;
  production?: number | null;
  resource_amount?: number;
};

export type SupplyEdge = {
  source: string;
  target: string;
  product_id: number;
  transport: "resource" | "infrastructure" | "vehicle";
  state: SupplyState;
  recipient_count: number;
  reachable_count: number;
  received: number | null;
  demand: number;
  path_ids: number[];
};

/** nodes と edges は上流・選択対象・直接の消費先の順に並ぶ一系列。 */
export type SupplyGraph = {
  nodes: SupplyNode[];
  edges: SupplyEdge[];
  selected_id: string;
  has_results: boolean;
};

export type SupplyResponse = {
  agent_id: number;
  epoch: number;
  pending: boolean;
  alert: number;
  graph: SupplyGraph | null;
};

export function supportsSupplyGraph(agent: AgentData | null, master: MasterData | null): boolean {
  if (!agent) return false;
  if (agent.variant === VARIANT_TRANSIT_LINE) return true;
  const entry = master?.agents[agent.variant];
  if (!entry || !master) return false;
  const supply = master.products[entry.supply ?? -1]?.type;
  return supply === GOODS_INFRASTRUCTURE || supply === GOODS_TRANSIT
    || Object.keys(entry.materials ?? {}).some((pid) => {
      const type = master.products[Number(pid)]?.type;
      return type === GOODS_INFRASTRUCTURE || type === GOODS_RESOURCE || type === GOODS_TRANSIT;
    });
}
