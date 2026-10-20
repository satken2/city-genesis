/**
 * Building spawning debug tab component (developers only).
 *
 * Content of the statistics window's "Debug (Buildings)" tab. Visualizes construction history
 * from the backend's GET /api/v1/stats/spawn_debug (the highest final score per Variant and its
 * breakdown, recorded each Epoch by ConstructionPhase). Visible only when the backend's
 * gd3.debug_execution is true (development runs); CityStatisticsWindow controls visibility.
 *
 * Two-column layout, with three-quarters on the left and one-quarter on the right.
 * - Left: line chart of the selected Variant's highest final score (NPV − construction costs −
 *   acquisition/demolition − land costs). A negative score approaching 0 indicates construction
 *   is close to becoming viable. Also overlays the best pattern's NPV (revenue side) and total
 *   cost (construction + land + acquisition/demolition). The intersection of NPV and cost shows
 *   how much rent must rise or costs must fall for construction to occur, helping tune master
 *   parameters such as cost_ratio and rent multipliers by land use.
 * - Right: Variant scores for the latest Epoch. Clicking a row selects the Variant in the left chart.
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
  ReferenceLine,
  ResponsiveContainer,
} from "recharts";
import { useEpochCachedStats } from "../useEpochCache";
import { useMasterData } from "../../../contexts/MasterDataContext";
import {
  SpawnDebugStats,
  ConstructionVariantEntry,
  SPAWN_DEBUG_ENDPOINT,
  CHART_MARGIN,
  AXIS_TICK,
  TOOLTIP_STYLE,
  formatCompact,
} from "./spawnDebugShared";
import "./DebugSpawnTab.css";
import { useRemPx } from "../../../hooks/useRemPx";

// Line series colors.
const COLOR_BEST_SCORE = "#4ea1ff"; // Highest final score.
const COLOR_NPV = "#10b981";        // Best pattern's NPV (revenue side).
const COLOR_COST = "#ef4444";       // Best pattern's total cost.

/** Calculates the best pattern's total cost (construction + land + acquisition/demolition). */
function totalCost(entry: ConstructionVariantEntry): number {
  return entry.best_construction_cost + entry.best_land_cost + entry.best_extra_cost;
}

export function DebugBuildingSpawnTab({ apiBaseUrl }: { apiBaseUrl: string }) {
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

  // ---- All Variant IDs appearing in history (descending latest Epoch score) ----
  const variantIds = useMemo(() => {
    if (!data || data.construction.length === 0) return [];
    const ids = new Set<number>();
    for (const rec of data.construction) {
      for (const key of Object.keys(rec.variants ?? {})) {
        ids.add(Number(key));
      }
    }
    const latest = data.construction[data.construction.length - 1]?.variants ?? {};
    return Array.from(ids).sort(
      (a, b) => (latest[b]?.best_score ?? -Infinity) - (latest[a]?.best_score ?? -Infinity)
    );
  }, [data]);

  const [selectedVariantId, setSelectedVariantId] = useState<number | null>(null);
  // If none is selected, fall back to the Variant with the highest score in the latest Epoch.
  const effectiveVariantId = selectedVariantId ?? variantIds[0] ?? null;

  // ---- Data for the selected Variant's score/breakdown history chart ----
  const chartData = useMemo(() => {
    if (!data || effectiveVariantId === null) return [];
    return data.construction.map((rec) => {
      const entry = rec.variants?.[effectiveVariantId] ?? null;
      return {
        date: rec.date,
        best_score: entry ? entry.best_score : null,
        best_npv: entry ? entry.best_npv : null,
        best_cost: entry ? totalCost(entry) : null,
        spawned: entry ? entry.spawned : 0,
      };
    });
  }, [data, effectiveVariantId]);

  // Whether the selected Variant has actual data (a score recorded in at least one Epoch).
  // If the Variant has never been evaluated, for example because there are no candidate Parcels,
  // best_score is null in every Epoch. Use this check to display "No data" instead of the chart,
  // preventing the previously selected Variant's chart from remaining visible.
  const hasChartData = useMemo(
    () => chartData.some((d) => d.best_score !== null),
    [chartData]
  );

  // ---- Variant list for the latest Epoch ----
  const latestConstruction = data?.construction[data.construction.length - 1] ?? null;
  const latestRows = useMemo(() => {
    if (!latestConstruction) return [];
    return Object.entries(latestConstruction.variants ?? {})
      .map(([id, entry]) => ({
        variantId: Number(id),
        bestScore: entry.best_score,
        npv: entry.best_npv,
        cost: totalCost(entry),
        spawned: entry.spawned,
      }))
      .sort((a, b) => b.bestScore - a.bestScore);
  }, [latestConstruction]);

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
    <div className="debug-spawn-tab debug-spawn-tab--fixed">
      <h2 className="debug-spawn-tab__title">{t("statistics.debugBuildingSpawnTitle")}</h2>

      <div className="debug-spawn-layout">
        {/* Left: score/breakdown history chart */}
        <div className="debug-spawn-layout__main">
          <section className="debug-spawn-card debug-spawn-card--fill">
            <div className="debug-spawn-card__header">
              <h3 className="debug-spawn-card__title">
                {t("statistics.debugSpawnConstructionSection")}
              </h3>
              <div className="debug-spawn-control">
                <label className="debug-spawn-control__label">
                  {t("statistics.debugSpawnVariantSelect")}
                </label>
                <select
                  className="debug-spawn-control__select"
                  value={effectiveVariantId ?? ""}
                  onChange={(e) => setSelectedVariantId(Number(e.target.value))}
                >
                  {variantIds.map((id) => (
                    <option key={id} value={id}>
                      {variantName(id)}
                    </option>
                  ))}
                </select>
              </div>
            </div>

            {hasChartData && effectiveVariantId !== null ? (
              <div className="debug-spawn-card__chart debug-spawn-card__chart--tall">
                <ResponsiveContainer width="100%" height="100%">
                  <ComposedChart data={chartData} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
                    <XAxis dataKey="date" tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} />
                    <YAxis
                      yAxisId="score"
                      tick={AXIS_TICK}
                      tickLine={false}
                      axisLine={{ stroke: "#253246" }}
                      width={remPx(4.81)}
                      tickFormatter={formatCompact}
                      domain={["auto", "auto"]}
                    />
                    <YAxis
                      yAxisId="count"
                      orientation="right"
                      tick={AXIS_TICK}
                      tickLine={false}
                      axisLine={{ stroke: "#253246" }}
                      width={remPx(2.78)}
                      allowDecimals={false}
                    />
                    <Tooltip
                      contentStyle={TOOLTIP_STYLE}
                      formatter={(value: any, _name: any, item: any) =>
                        item?.dataKey === "spawned"
                          ? value
                          : formatCompact(Number(value))
                      }
                    />
                    <Legend wrapperStyle={{ fontSize: "1rem" }} />
                    {/* Score 0 = threshold at which construction becomes viable */}
                    <ReferenceLine
                      yAxisId="score"
                      y={0}
                      stroke="#f59e0b"
                      strokeDasharray="6 3"
                      label={{
                        value: t("statistics.debugSpawnZeroLine"),
                        position: "insideTopRight",
                        fill: "#f59e0b",
                        fontSize: "1rem",
                      }}
                    />
                    <Bar
                      yAxisId="count"
                      dataKey="spawned"
                      name={t("statistics.debugSpawnSpawnedCount")}
                      fill="#10b981"
                      fillOpacity={0.5}
                      isAnimationActive={false}
                    />
                    <Line
                      yAxisId="score"
                      type="monotone"
                      dataKey="best_score"
                      name={t("statistics.debugSpawnBestScore")}
                      stroke={COLOR_BEST_SCORE}
                      strokeWidth={2}
                      dot={false}
                      connectNulls
                      isAnimationActive={false}
                    />
                    <Line
                      yAxisId="score"
                      type="monotone"
                      dataKey="best_npv"
                      name={t("statistics.debugSpawnBestNpv")}
                      stroke={COLOR_NPV}
                      strokeWidth={1.5}
                      strokeDasharray="5 4"
                      dot={false}
                      connectNulls
                      isAnimationActive={false}
                    />
                    <Line
                      yAxisId="score"
                      type="monotone"
                      dataKey="best_cost"
                      name={t("statistics.debugSpawnBestCost")}
                      stroke={COLOR_COST}
                      strokeWidth={1.5}
                      strokeDasharray="5 4"
                      dot={false}
                      connectNulls
                      isAnimationActive={false}
                    />
                  </ComposedChart>
                </ResponsiveContainer>
              </div>
            ) : (
              <div className="debug-spawn-card__no-data">{t("statistics.debugSpawnNoData")}</div>
            )}
          </section>
        </div>

        {/* Right: Variant scores for the latest Epoch (click a row to select a Variant) */}
        <div className="debug-spawn-layout__side">
          <section className="debug-spawn-card debug-spawn-card--fill">
            <h3 className="debug-spawn-card__title">
              {t("statistics.debugSpawnLatestTable")}
              {latestConstruction && (
                <span className="debug-spawn-card__meta">
                  {t("statistics.debugSpawnPositivePatterns")}: {latestConstruction.positive_patterns}
                </span>
              )}
            </h3>
            {latestRows.length > 0 ? (
              <div className="debug-spawn-table-scroll debug-spawn-table-scroll--fill">
                <table className="debug-spawn-table debug-spawn-table--clickable">
                  <thead>
                    <tr>
                      <th>{t("statistics.debugSpawnColVariant")}</th>
                      <th>{t("statistics.debugSpawnColBestScore")}</th>
                      <th>{t("statistics.debugSpawnColNpv")}</th>
                      <th>{t("statistics.debugSpawnColCost")}</th>
                      <th>{t("statistics.debugSpawnColSpawned")}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {latestRows.map((row) => (
                      <tr
                        key={row.variantId}
                        className={[
                          row.bestScore > 0 && "debug-spawn-table__row--positive",
                          row.variantId === effectiveVariantId &&
                            "debug-spawn-table__row--selected",
                        ]
                          .filter(Boolean)
                          .join(" ")}
                        onClick={() => setSelectedVariantId(row.variantId)}
                      >
                        <td>{variantName(row.variantId)}</td>
                        <td className="debug-spawn-table__num">{formatCompact(row.bestScore)}</td>
                        <td className="debug-spawn-table__num">
                          {row.npv !== null ? formatCompact(row.npv) : "-"}
                        </td>
                        <td className="debug-spawn-table__num">
                          {row.cost !== null ? formatCompact(row.cost) : "-"}
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
