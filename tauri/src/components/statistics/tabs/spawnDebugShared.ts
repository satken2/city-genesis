/**
 * Shared definitions for developer-only Spawn debug tabs.
 *
 * Centralizes GET /api/v1/stats/spawn_debug response types, shared chart constants, and number
 * formatters used by DebugAgentSpawnTab and DebugBuildingSpawnTab. Both tabs use the same
 * endpoint (useEpochCachedStats memoizes per Epoch, so only one request is made), sharing
 * one type definition to prevent divergence.
 */

/** Per-Variant construction statistics from GET /api/v1/stats/spawn_debug.
 *  The four best_* breakdown fields are raw values for the (Parcel × floor count) pattern with the highest final score. */
export type ConstructionVariantEntry = {
  best_score: number;
  spawned: number;
  /** Best pattern's NPV (present value of rental income). */
  best_npv: number;
  /** Best pattern's construction cost. */
  best_construction_cost: number;
  /** Best pattern's land acquisition cost (after deducting the developer margin). */
  best_land_cost: number;
  /** Best pattern's existing-building acquisition + demolition cost. */
  best_extra_cost: number;
};

/** One building Spawn history record from GET /api/v1/stats/spawn_debug. */
export type ConstructionRecord = {
  date: number;
  candidates: number;
  positive_patterns: number;
  spawned: number;
  variants: Record<string, ConstructionVariantEntry>;
};

/** Industry-level evaluation metrics for Agent spawning (recorded each Epoch by SpawnPhase).
 *  Demand metrics (demand_* / effective_demand) are summed across Districts; best_* are the best
 *  values across Districts; capital_need is the minimum across Districts.
 *  Keys may be absent for industries whose evaluation ended early. */
export type AgentSpawnMetrics = {
  /** Sum of domestic potential demand (remaining demand after subtracting existing supply). */
  demand_domestic?: number;
  /** Sum of export potential demand. */
  demand_export?: number;
  /** Sum of effective demand after competitiveness discounting (the upper bound on Spawn scale). */
  effective_demand?: number;
  /** Best domestic price competitiveness coefficient (0–1). */
  best_domestic_competitiveness?: number;
  /** Best export competitiveness coefficient (0–1). */
  best_export_competitiveness?: number;
  /** Best estimated margin (equivalent to the sort key for Spawn execution order). */
  best_profit_rate?: number;
  /** Minimum operating quantity (from the master, independent of District). */
  min_qty?: number;
  /** Minimum estimated initial capital at minimum operating scale (working capital + dedicated building construction costs).
   *  Industries exceeding the record's credit_limit cannot enter because financing is unavailable.
   *  (Larger scales are automatically reduced to fit the credit limit, so this value determines eligibility.) */
  capital_need?: number;
  /** Evaluation scale after all capital, labor, and physical limits are applied (Epoch production, best value across Districts).
   *  Basis for profitability evaluation and the actual Spawn scale. */
  assumed_qty?: number;
};

/** One Agent Spawn history record from GET /api/v1/stats/spawn_debug.
 *  variants contains blocking-factor counters (counts); metrics contains internal evaluation values. */
export type AgentSpawnRecord = {
  date: number;
  variants: Record<string, Record<string, number>>;
  metrics: Record<string, AgentSpawnMetrics>;
  /** Credit concentration limit per project (yen). A city-wide scalar shared by industries:
   *  max(annual nominal GDP × SPAWN_CREDIT_CONCENTRATION_LIMIT, SPAWN_CREDIT_FLOOR). */
  credit_limit: number;
  /** Annual nominal GDP (yen), the denominator for the credit concentration limit.
   *  Annualized average nominal GDP of completed Epochs in the latest year. */
  annual_gdp: number;
};

/** Complete GET /api/v1/stats/spawn_debug response. */
export type SpawnDebugStats = {
  enabled: boolean;
  construction: ConstructionRecord[];
  agent_spawn: AgentSpawnRecord[];
};

/** Endpoint shared by both tabs (useEpochCachedStats memoizes per Epoch). */
export const SPAWN_DEBUG_ENDPOINT = "/api/v1/stats/spawn_debug";

// Shared Recharts style constants (same dark theme as other statistics tabs).
export const CHART_MARGIN = { top: 12, right: 16, left: 0, bottom: 0 };
export const AXIS_TICK = { fontSize: "0.93rem", fill: "#8ba3bb" };
export const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "1rem",
};

/** Abbreviates large numbers (scores and demand quantities can become very large). */
export function formatCompact(v: number): string {
  const abs = Math.abs(v);
  if (abs >= 1_000_000_000) return `${(v / 1_000_000_000).toFixed(1)}G`;
  if (abs >= 1_000_000) return `${(v / 1_000_000).toFixed(1)}M`;
  if (abs >= 1_000) return `${(v / 1_000).toFixed(1)}k`;
  return v.toFixed(0);
}

/** Display formatter for coefficients (competitiveness 0–1, margins, etc.). */
export function formatCoefficient(v: number): string {
  return v.toFixed(3);
}
