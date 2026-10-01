import type { AgentData, MasterData } from "../../types";
import { GOODS_INFRASTRUCTURE, GOODS_RESOURCE, GOODS_TRANSIT, VARIANT_TRANSIT_LINE } from "../../constants";

export type SupplyState = "ok" | "unknown" | "missing" | "inactive" | "partial" | "limited"
  | "disconnected" | "unavailable" | "supply_shortage" | "capacity_limited" | "unmatched";

export type SupplyNode = {
  id: string;
  kind: "facility" | "resource";
  role: "resource" | "producer" | "consumer" | "vehicle_consumer" | "market";
  selected: boolean;
  name?: string;
  agent_id?: number;
  variant?: number;
  path_variant?: number;
  product_id: number;
  state: SupplyState;
  count: number;
  active_count: number;
  production?: number | null;
  resource_amount?: number;
  importer_count?: number;
  agent_ids: number[];
  facilities?: { id: number; name: string }[];
  resource_ids?: number[];
  variants: { variant: number; count: number; active_count: number; production: number | null }[];
};

export type SupplyEdge = {
  source: string;
  target: string;
  product_id: number;
  transport: "resource" | "infrastructure" | "vehicle" | "market";
  state: SupplyState;
  cycle: boolean;
  recipient_count: number;
  reachable_count: number;
  received: number | null;
  demand: number;
  path_ids: number[];
};

export type SupplyGraph = {
  nodes: SupplyNode[];
  edges: SupplyEdge[];
  selected_id: string;
  has_results: boolean;
  truncated: boolean;
};

export type SupplyResponse = {
  agent_id: number;
  epoch: number;
  pending: boolean;
  alert: number;
  graph: SupplyGraph | null;
  roots: { type: string; params: Record<string, unknown> }[];
};

export function supportsSupplyGraph(agent: AgentData, master: MasterData | null): boolean {
  if (agent.variant === VARIANT_TRANSIT_LINE) return true;
  const entry = master?.agents[agent.variant];
  if (!entry || !master) return false;
  const supply = master.products[entry.supply ?? -1]?.type;
  return supply === GOODS_INFRASTRUCTURE || supply === GOODS_TRANSIT
    || Object.keys(entry.materials ?? {}).some((pid) => {
      const type = master.products[Number(pid)]?.type;
      return type === GOODS_INFRASTRUCTURE || type === GOODS_RESOURCE;
    });
}

/** 循環の辺を外して供給先までの距離を求め、同じ箱を一度だけ配置する。 */
export function supplyColumns(graph: SupplyGraph): SupplyNode[][] {
  const distances = new Map<string, number>();
  const distance = (id: string, visiting = new Set<string>()): number => {
    if (distances.has(id)) return distances.get(id)!;
    if (visiting.has(id) || id === graph.selected_id) return 0;
    const next = graph.edges.filter((edge) => edge.source === id && !edge.cycle);
    const result = next.length ? Math.max(...next.map((edge) => 1 + distance(edge.target, new Set([...visiting, id])))) : 0;
    distances.set(id, result);
    return result;
  };
  const farthest = Math.max(0, ...graph.nodes.map((node) => distance(node.id)));
  const columns = Array.from({ length: farthest + 1 }, () => [] as SupplyNode[]);
  for (const node of graph.nodes) columns[farthest - distance(node.id)].push(node);
  return columns;
}
