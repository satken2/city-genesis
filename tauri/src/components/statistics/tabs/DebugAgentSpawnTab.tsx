/**
 * Agent spawning debug tab component (developers only).
 *
 * Content of the statistics window's "Debug (Agents)" tab. Visualizes agent_spawn history from
 * the backend's GET /api/v1/stats/spawn_debug (blocking-factor counters and evaluation metrics
 * recorded each Epoch by SpawnPhase). This tab is visible only when the backend's
 * gd3.debug_execution is true (development runs); CityStatisticsWindow controls its visibility.
 *
 * Two-column layout, with three-quarters on the left and one-quarter on the right.
 * - Top left: blocking-factor breakdown (stacked bar chart). Stacks counts of factors blocking
 *   spawning each Epoch (insufficient demand, price competitiveness, rent, raw materials, labor,
 *   etc.), with overlaid lines for candidate and successful Spawn counts.
 * - Bottom left: Spawn evaluation metric history (line chart). For the selected industry, plots
 *   potential demand (domestic/export), effective demand, and minimum operating quantity on
 *   the left axis, with competitiveness and gross margin on the right. Demand exceeding the
 *   minimum operating quantity and improving competitiveness/margins indicate how close
 *   the industry is to entering the market.
 * - Right: industry scores for the latest Epoch. Clicking a row selects the industry in the left
 *   charts. Directly below the table heading, shows the financing gate (credit concentration
 *   limit and its denominator, total private deposits). The limit is city-wide and shared across
 *   industries, so it is shown outside the columns for comparison with each industry's estimated
 *   initial capital at minimum operating scale ("Minimum initial capital" column). Industries
 *   exceeding the limit appear in red. Those within the limit enter at a scale automatically
 *   reduced to what credit can cover; the reduced scale is the "Evaluation scale" column.
 *
 * Industry selection is shared by both left charts and the right table.
 * With "All industries (total)" selected, show only summed demand metrics, since competitiveness
 * and margins are industry-specific values with no meaningful sum.
 */

import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  Bar,
  ComposedChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ResponsiveContainer,
} from "recharts";
import { useEpochCachedStats } from "../useEpochCache";
import { useMasterData } from "../../../contexts/MasterDataContext";
import {
  SpawnDebugStats,
  AgentSpawnMetrics,
  SPAWN_DEBUG_ENDPOINT,
  CHART_MARGIN,
  AXIS_TICK,
  TOOLTIP_STYLE,
  formatCompact,
  formatCoefficient,
} from "./spawnDebugShared";
import "./DebugSpawnTab.css";
import { useRemPx } from "../../../hooks/useRemPx";

/** Variant selection for Agent spawning. "all" means the sum across all industries. */
type AgentVariantSelection = number | "all";

/**
 * Display definitions for blocking-factor counters.
 * Keys must match counter names used by the backend's SpawnPhase._debug_count.
 * Centrally manages stack order, colors, and i18n labels for the stacked bar chart.
 */
const BLOCKER_SERIES: Array<{ key: string; labelKey: string; color: string }> = [
  { key: "no_demand", labelKey: "statistics.debugSpawnCounterNoDemand", color: "#64748b" },
  { key: "competitiveness", labelKey: "statistics.debugSpawnCounterCompetitiveness", color: "#f97316" },
  { key: "rent_negative", labelKey: "statistics.debugSpawnCounterRentNegative", color: "#ef4444" },
  { key: "rent_below_market", labelKey: "statistics.debugSpawnCounterRentBelowMarket", color: "#f59e0b" },
  { key: "materials", labelKey: "statistics.debugSpawnCounterMaterials", color: "#a16207" },
  { key: "materials_capacity", labelKey: "statistics.debugSpawnCounterMaterialsCapacity", color: "#854d0e" },
  { key: "capital", labelKey: "statistics.debugSpawnCounterCapital", color: "#0f766e" },
  { key: "high_skill_labor", labelKey: "statistics.debugSpawnCounterHighSkillLabor", color: "#8b5cf6" },
  { key: "general_labor", labelKey: "statistics.debugSpawnCounterGeneralLabor", color: "#c084fc" },
  { key: "below_min_qty", labelKey: "statistics.debugSpawnCounterBelowMinQty", color: "#0ea5e9" },
  { key: "dedicated_pending", labelKey: "statistics.debugSpawnCounterDedicatedPending", color: "#334155" },
  { key: "no_buildable_parcel", labelKey: "statistics.debugSpawnCounterNoBuildableParcel", color: "#78716c" },
];

/**
 * Display definitions for evaluation metric lines.
 * Keys must match the recording keys in the backend's SpawnPhase._debug_metric_add /
 * _debug_metric_max. axis="amount" uses the left axis (quantity), "coefficient" the right axis.
 * summable is true only for series where summing Variants (when "all" is selected) is meaningful.
 */
const METRIC_SERIES: Array<{
  key: keyof AgentSpawnMetrics;
  labelKey: string;
  color: string;
  axis: "amount" | "coefficient";
  summable: boolean;
  dash?: string;
}> = [
  { key: "demand_domestic", labelKey: "statistics.debugSpawnMetricDemandDomestic", color: "#4ea1ff", axis: "amount", summable: true },
  { key: "demand_export", labelKey: "statistics.debugSpawnMetricDemandExport", color: "#a78bfa", axis: "amount", summable: true },
  { key: "effective_demand", labelKey: "statistics.debugSpawnMetricEffectiveDemand", color: "#10b981", axis: "amount", summable: true },
  { key: "assumed_qty", labelKey: "statistics.debugSpawnMetricAssumedQty", color: "#22d3ee", axis: "amount", summable: false },
  { key: "min_qty", labelKey: "statistics.debugSpawnMetricMinQty", color: "#94a3b8", axis: "amount", summable: false, dash: "5 4" },
  { key: "best_domestic_competitiveness", labelKey: "statistics.debugSpawnMetricDomesticCompetitiveness", color: "#f59e0b", axis: "coefficient", summable: false },
  { key: "best_export_competitiveness", labelKey: "statistics.debugSpawnMetricExportCompetitiveness", color: "#f97316", axis: "coefficient", summable: false },
  { key: "best_profit_rate", labelKey: "statistics.debugSpawnMetricProfitRate", color: "#ef4444", axis: "coefficient", summable: false },
];

// Line series colors.
const COLOR_CANDIDATES = "#94a3b8"; // Candidate count (dotted).
const COLOR_REALIZED = "#10b981";   // Successful Spawn count (solid).

/** One row in the latest Epoch table. */
type LatestAgentRow = {
  variantId: number;
  demand: number | null;
  competitiveness: number | null;
  profitRate: number | null;
  /** Estimated initial capital at minimum operating scale (working capital + dedicated building construction costs).
   * Determines eligibility for entry when compared with the credit concentration limit. null for industries that did not reach the capital check. */
  capitalNeed: number | null;
  /** Evaluation scale (Epoch production) after capital, labor, and physical limits are applied. null for unevaluated industries. */
  assumedQty: number | null;
  spawned: number;
};

export function DebugAgentSpawnTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const remPx = useRemPx();
  const masterData = useMasterData();
  const { data, loading, error } = useEpochCachedStats<SpawnDebugStats>(
    apiBaseUrl,
    SPAWN_DEBUG_ENDPOINT
  );

  /** Resolves a display name from a Variant ID (shows the ID itself when absent from the master). */
  const variantName = (variantId: number): string =>
    masterData?.agents?.[variantId]?.display_name ?? `#${variantId}`;

  // ---- All industry IDs appearing in history (descending total counter events) ----
  // Higher counter totals, whether blocked or successful, indicate more active industries, so rank them first.
  // Include industries recorded only in metrics so they remain available in the selector.
  const agentVariantIds = useMemo(() => {
    if (!data) return [];
    const totals = new Map<number, number>();
    for (const rec of data.agent_spawn) {
      for (const [id, counters] of Object.entries(rec.variants ?? {})) {
        const sum = Object.values(counters).reduce((a, b) => a + b, 0);
        totals.set(Number(id), (totals.get(Number(id)) ?? 0) + sum);
      }
      for (const id of Object.keys(rec.metrics ?? {})) {
        if (!totals.has(Number(id))) totals.set(Number(id), 0);
      }
    }
    return Array.from(totals.keys()).sort(
      (a, b) => (totals.get(b) ?? 0) - (totals.get(a) ?? 0)
    );
  }, [data]);

  // Selected industry. Defaults to the sum across all industries ("all"). Shared by both left charts and the right table.
  const [selectedVariant, setSelectedVariant] =
    useState<AgentVariantSelection>("all");

  // ---- Top left: data for the blocking-factor stacked chart ----
  // For "all", sum all industries; otherwise aggregate only the selected industry's counters.
  const blockerChartData = useMemo(() => {
    if (!data) return [];
    return data.agent_spawn.map((rec) => {
      const variants = rec.variants ?? {};
      const entries =
        selectedVariant === "all"
          ? Object.values(variants)
          : [variants[String(selectedVariant)] ?? {}];

      const row: Record<string, number> = { date: rec.date, candidates: 0, realized: 0 };
      for (const s of BLOCKER_SERIES) row[s.key] = 0;

      for (const c of entries) {
        row.candidates += c["candidates"] ?? 0;
        row.realized +=
          (c["spawned"] ?? 0) + (c["reused_paused"] ?? 0) + (c["dedicated_registered"] ?? 0);
        for (const s of BLOCKER_SERIES) {
          row[s.key] += c[s.key] ?? 0;
        }
      }
      return row;
    });
  }, [data, selectedVariant]);

  // Whether the selected target has any counters. If none, display "No data".
  const hasBlockerData = useMemo(
    () =>
      blockerChartData.some((row) =>
        Object.entries(row).some(([k, v]) => k !== "date" && v !== 0)
      ),
    [blockerChartData]
  );

  // ---- Bottom left: data for the evaluation metric line chart ----
  // For a selected industry, show all its metrics; for "all", sum only meaningful
  // demand metrics (summable=true) across all industries.
  // Use null for Epochs with no recorded value, leaving a gap in the line (interpolated by connectNulls).
  const visibleMetricSeries = useMemo(
    () =>
      selectedVariant === "all"
        ? METRIC_SERIES.filter((s) => s.summable)
        : METRIC_SERIES,
    [selectedVariant]
  );

  const metricChartData = useMemo(() => {
    if (!data) return [];
    return data.agent_spawn.map((rec) => {
      const metrics = rec.metrics ?? {};
      const row: Record<string, number | null> = { date: rec.date };
      if (selectedVariant === "all") {
        for (const s of METRIC_SERIES) {
          if (!s.summable) continue;
          let sum: number | null = null;
          for (const m of Object.values(metrics)) {
            const v = m[s.key];
            if (v !== undefined) sum = (sum ?? 0) + v;
          }
          row[s.key] = sum;
        }
      } else {
        const m = metrics[String(selectedVariant)] ?? {};
        for (const s of METRIC_SERIES) {
          row[s.key] = m[s.key] ?? null;
        }
      }
      return row;
    });
  }, [data, selectedVariant]);

  const hasMetricData = useMemo(
    () =>
      metricChartData.some((row) =>
        Object.entries(row).some(([k, v]) => k !== "date" && v !== null)
      ),
    [metricChartData]
  );

  // ---- Right: industry scores for the latest Epoch ----
  // Combine metrics (demand, competitiveness, margin) and counters (successful Spawns) into one row,
  // sorted by descending margin (unrecorded entries last).
  const latestRecord = data?.agent_spawn[data.agent_spawn.length - 1] ?? null;
  // Financing threshold (shared by industries). Also used to flag estimated initial capital exceeding the limit.
  const creditLimit = latestRecord?.credit_limit ?? null;
  const latestRows = useMemo((): LatestAgentRow[] => {
    if (!latestRecord) return [];
    const metrics = latestRecord.metrics ?? {};
    const counters = latestRecord.variants ?? {};
    const ids = new Set<number>([
      ...Object.keys(metrics).map(Number),
      ...Object.keys(counters).map(Number),
    ]);
    const rows = Array.from(ids).map((variantId): LatestAgentRow => {
      const m = metrics[String(variantId)] ?? {};
      const c = counters[String(variantId)] ?? {};
      const domestic = m.demand_domestic;
      const exportDemand = m.demand_export;
      const demand =
        domestic === undefined && exportDemand === undefined
          ? null
          : (domestic ?? 0) + (exportDemand ?? 0);
      const compCandidates = [
        m.best_domestic_competitiveness,
        m.best_export_competitiveness,
      ].filter((v): v is number => v !== undefined);
      return {
        variantId,
        demand,
        competitiveness: compCandidates.length > 0 ? Math.max(...compCandidates) : null,
        profitRate: m.best_profit_rate ?? null,
        capitalNeed: m.capital_need ?? null,
        assumedQty: m.assumed_qty ?? null,
        spawned:
          (c["spawned"] ?? 0) + (c["reused_paused"] ?? 0) + (c["dedicated_registered"] ?? 0),
      };
    });
    rows.sort((a, b) => (b.profitRate ?? -Infinity) - (a.profitRate ?? -Infinity));
    return rows;
  }, [latestRecord]);

  if (loading) {
    return <div className="debug-spawn-tab__loading">{t("common.loading")}</div>;
  }

  if (error || !data) {
    return (
      <div className="debug-spawn-tab__error">
        {t("common.fetchError")}{error ? `: ${error}` : ""}
      </div>
    );
  }

  return (
    <div className="debug-spawn-tab">
      <h2 className="debug-spawn-tab__title">{t("statistics.debugAgentSpawnTitle")}</h2>

      <div className="debug-spawn-layout">
        <div className="debug-spawn-layout__main">
          {/* Top left: blocking-factor stacked bar chart */}
          <section className="debug-spawn-card">
            <div className="debug-spawn-card__header">
              <h3 className="debug-spawn-card__title">
                {t("statistics.debugSpawnAgentSection")}
              </h3>
              <div className="debug-spawn-control">
                <label className="debug-spawn-control__label">
                  {t("statistics.debugSpawnAgentVariantSelect")}
                </label>
                <select
                  className="debug-spawn-control__select"
                  value={selectedVariant === "all" ? "all" : String(selectedVariant)}
                  onChange={(e) =>
                    setSelectedVariant(
                      e.target.value === "all" ? "all" : Number(e.target.value)
                    )
                  }
                >
                  <option value="all">{t("statistics.debugSpawnAllVariants")}</option>
                  {agentVariantIds.map((id) => (
                    <option key={id} value={id}>
                      {variantName(id)}
                    </option>
                  ))}
                </select>
              </div>
            </div>
            {blockerChartData.length > 0 && hasBlockerData ? (
              <div className="debug-spawn-card__chart">
                <ResponsiveContainer width="100%" height="100%">
                  <ComposedChart data={blockerChartData} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
                    <XAxis dataKey="date" tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} />
                    <YAxis tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} width={remPx(3.7)} allowDecimals={false} />
                    <Tooltip contentStyle={TOOLTIP_STYLE} />
                    <Legend wrapperStyle={{ fontSize: "1rem" }} />
                    {BLOCKER_SERIES.map((s) => (
                      <Bar
                        key={s.key}
                        dataKey={s.key}
                        name={t(s.labelKey)}
                        stackId="blockers"
                        fill={s.color}
                        isAnimationActive={false}
                      />
                    ))}
                    <Line
                      type="monotone"
                      dataKey="candidates"
                      name={t("statistics.debugSpawnCandidates")}
                      stroke={COLOR_CANDIDATES}
                      strokeWidth={2}
                      strokeDasharray="5 4"
                      dot={false}
                      isAnimationActive={false}
                    />
                    <Line
                      type="monotone"
                      dataKey="realized"
                      name={t("statistics.debugSpawnRealized")}
                      stroke={COLOR_REALIZED}
                      strokeWidth={2}
                      dot={false}
                      isAnimationActive={false}
                    />
                  </ComposedChart>
                </ResponsiveContainer>
              </div>
            ) : (
              <div className="debug-spawn-card__no-data">{t("statistics.debugSpawnNoData")}</div>
            )}
          </section>

          {/* Bottom left: evaluation metric line chart */}
          <section className="debug-spawn-card">
            <h3 className="debug-spawn-card__title">
              {t("statistics.debugSpawnMetricsSection")}
            </h3>
            {hasMetricData ? (
              <div className="debug-spawn-card__chart">
                <ResponsiveContainer width="100%" height="100%">
                  <ComposedChart data={metricChartData} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
                    <XAxis dataKey="date" tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} />
                    {/* Left axis: demand quantities (potential/effective demand, minimum operating quantity) */}
                    <YAxis
                      yAxisId="amount"
                      tick={AXIS_TICK}
                      tickLine={false}
                      axisLine={{ stroke: "#253246" }}
                      width={remPx(4.81)}
                      tickFormatter={formatCompact}
                      domain={["auto", "auto"]}
                    />
                    {/* Right axis: coefficients (competitiveness 0–1, gross margin) */}
                    {selectedVariant !== "all" && (
                      <YAxis
                        yAxisId="coefficient"
                        orientation="right"
                        tick={AXIS_TICK}
                        tickLine={false}
                        axisLine={{ stroke: "#253246" }}
                        width={remPx(3.7)}
                      />
                    )}
                    <Tooltip
                      contentStyle={TOOLTIP_STYLE}
                      formatter={(value: any, _name: any, item: any) => {
                        const series = METRIC_SERIES.find((s) => s.key === item?.dataKey);
                        return series?.axis === "coefficient"
                          ? formatCoefficient(Number(value))
                          : formatCompact(Number(value));
                      }}
                    />
                    <Legend wrapperStyle={{ fontSize: "1rem" }} />
                    {visibleMetricSeries.map((s) => (
                      <Line
                        key={s.key}
                        yAxisId={s.axis}
                        type="monotone"
                        dataKey={s.key}
                        name={t(s.labelKey)}
                        stroke={s.color}
                        strokeWidth={2}
                        strokeDasharray={s.dash}
                        dot={false}
                        connectNulls
                        isAnimationActive={false}
                      />
                    ))}
                  </ComposedChart>
                </ResponsiveContainer>
              </div>
            ) : (
              <div className="debug-spawn-card__no-data">{t("statistics.debugSpawnNoData")}</div>
            )}
          </section>
        </div>

        {/* Right: industry scores for the latest Epoch (click a row to select an industry) */}
        <div className="debug-spawn-layout__side">
          <section className="debug-spawn-card debug-spawn-card--fill">
            <h3 className="debug-spawn-card__title">
              {t("statistics.debugSpawnAgentLatestTable")}
            </h3>
            {/* Financing gate. A single line outside the table because the threshold is shared by industries.
                Credit concentration limit = max(annual nominal GDP × concentration limit rate, floor).
                In the early city stage, the floor applies and the limit cannot be inferred from the GDP ratio, so show both. */}
            {latestRecord && (
              <div className="debug-spawn-note">
                <span className="debug-spawn-note__item">
                  {t("statistics.debugSpawnCreditLimit")}:{" "}
                  <span className="debug-spawn-note__value">
                    {formatCompact(latestRecord.credit_limit)}
                  </span>
                </span>
                <span className="debug-spawn-note__item">
                  {t("statistics.debugSpawnAnnualGdp")}:{" "}
                  <span className="debug-spawn-note__value">
                    {formatCompact(latestRecord.annual_gdp)}
                  </span>
                </span>
              </div>
            )}
            {latestRows.length > 0 ? (
              <div className="debug-spawn-table-scroll debug-spawn-table-scroll--fill">
                <table className="debug-spawn-table debug-spawn-table--clickable">
                  <thead>
                    <tr>
                      <th>{t("statistics.debugSpawnColVariant")}</th>
                      <th>{t("statistics.debugSpawnColDemand")}</th>
                      <th>{t("statistics.debugSpawnColCompetitiveness")}</th>
                      <th>{t("statistics.debugSpawnColProfitRate")}</th>
                      <th>{t("statistics.debugSpawnColCapitalNeed")}</th>
                      <th>{t("statistics.debugSpawnColAssumedQty")}</th>
                      <th>{t("statistics.debugSpawnColSpawned")}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {latestRows.map((row) => (
                      <tr
                        key={row.variantId}
                        className={[
                          row.spawned > 0 && "debug-spawn-table__row--positive",
                          row.variantId === selectedVariant &&
                            "debug-spawn-table__row--selected",
                        ]
                          .filter(Boolean)
                          .join(" ")}
                        onClick={() => setSelectedVariant(row.variantId)}
                      >
                        <td>{variantName(row.variantId)}</td>
                        <td className="debug-spawn-table__num">
                          {row.demand !== null ? formatCompact(row.demand) : "-"}
                        </td>
                        <td className="debug-spawn-table__num">
                          {row.competitiveness !== null
                            ? formatCoefficient(row.competitiveness)
                            : "-"}
                        </td>
                        <td className="debug-spawn-table__num">
                          {row.profitRate !== null
                            ? formatCoefficient(row.profitRate)
                            : "-"}
                        </td>
                        {/* Estimated initial capital at minimum operating scale. If it exceeds the credit concentration
                            limit, entry is impossible because financing is unavailable; show only that row in red.
                            (Within the limit, scale is automatically reduced to what credit can cover.) */}
                        <td
                          className={[
                            "debug-spawn-table__num",
                            creditLimit !== null &&
                              row.capitalNeed !== null &&
                              row.capitalNeed > creditLimit &&
                              "debug-spawn-table__num--over",
                          ]
                            .filter(Boolean)
                            .join(" ")}
                        >
                          {row.capitalNeed !== null
                            ? formatCompact(row.capitalNeed)
                            : "-"}
                        </td>
                        <td className="debug-spawn-table__num">
                          {row.assumedQty !== null
                            ? formatCompact(row.assumedQty)
                            : "-"}
                        </td>
                        <td className="debug-spawn-table__num">{row.spawned}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : (
              <div className="debug-spawn-card__no-data">{t("statistics.debugSpawnNoData")}</div>
            )}
          </section>
        </div>
      </div>
    </div>
  );
}
