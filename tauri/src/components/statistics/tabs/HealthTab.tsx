/**
 * Health and sanitation tab component.
 *
 * Content of the statistics window's "Health and sanitation" tab. Fetches and displays city-wide
 * and per-District health statistics (life expectancy history + sanitation/healthcare fulfillment
 * distributions) from GET /api/v1/stats/health. Data is not fetched again until the Epoch changes
 * (cached by useEpochCachedStats).
 *
 * Layout:
 * - Top: scope dropdown (entire city / individual Districts).
 * - Middle (upper half): two columns.
 *     - Left (50%): life expectancy line chart, showing the average age of deceased Citizens.
 *     - Right (50%): death-age distribution (stacked horizontal bars by five-year age band and
 *       cause of death). Aggregated only city-wide, so show guidance when a District is selected.
 * - Bottom (lower half): two columns.
 *     - Left (50%): sanitation fulfillment (Household sanitation distribution, horizontal bars with no gaps).
 *     - Right (50%): healthcare fulfillment (Household healthcare distribution, horizontal bars with no gaps).
 */

import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  LineChart,
  Line,
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ResponsiveContainer,
} from "recharts";
import { useEpochCachedStats } from "../useEpochCache";
import "./HealthTab.css";
import { useRemPx } from "../../../hooks/useRemPx";

// --- Backend GET /api/v1/stats/health response types ---

type HistoryPoint = {
  epoch: number;
  avg_death_age: number | null;
};

type DistributionPoint = {
  bin: string;
  count: number;
};

/** One age band in the death-age distribution. Keys other than band contain death counts by cause. */
type DeathAgeBand = {
  band: string;
  natural: number;
  sanitation: number;
  orphan: number;
  traffic: number;
  collapse: number;
};

type DeathAgeDistribution = {
  /** Number of historical Epochs included in the aggregation. */
  epochs: number;
  /** Ascending age bands (0–4 → 85+). */
  bands: DeathAgeBand[];
};

type Scope = {
  zone_id: number | null;
  name: string | null;
  history: HistoryPoint[];
  sanitation_distribution: DistributionPoint[];
  healthcare_distribution: DistributionPoint[];
  /** Death counts by cause and age band. Available only city-wide; null for District scopes. */
  death_age_distribution: DeathAgeDistribution | null;
};

type HealthStats = {
  epoch: number;
  scopes: Scope[];
};

// --- Colors (modern colors suited to urban environments and welfare) ---
const COLOR_LIFESPAN = "#3b82f6"; // Life expectancy: blue.
const COLOR_NUTRITION = "#10b981"; // Sanitation: emerald green.
const COLOR_HEALTHCARE = "#06b6d4"; // Healthcare: cyan.

/**
 * Colors by cause in the death-age distribution.
 * Categorical palette verified for lightness range, saturation, CVD (color vision diversity)
 * separation, and contrast against a dark surface (#1e293b).
 * Stacking follows this array's order (adjacent pairs have been verified).
 */
const DEATH_CAUSES = [
  { key: "natural", labelKey: "statistics.deathCauseNatural", color: "#3b82f6" },
  { key: "sanitation", labelKey: "statistics.deathCauseSanitation", color: "#d97706" },
  { key: "orphan", labelKey: "statistics.deathCauseOrphan", color: "#8b5cf6" },
  { key: "traffic", labelKey: "statistics.deathCauseTraffic", color: "#0891b2" },
  { key: "collapse", labelKey: "statistics.deathCauseCollapse", color: "#dc2626" },
] as const;

/** Dividers between stacked segments. Match the chart surface's background color. */
const STACK_STROKE = "#1e293b";

const CHART_MARGIN = { top: 10, right: 15, left: 5, bottom: 5 };
const Y_AXIS_WIDTH_REM = 3.7; // Y-axis width (rem). Convert to px with useRemPx when rendering.
const AXIS_TICK = { fontSize: "0.83rem", fill: "#8ba3bb" };
const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "1rem",
};

function scopeKey(scope: Scope): string {
  return scope.zone_id == null ? "city" : String(scope.zone_id);
}

export function HealthTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const remPx = useRemPx();
  const { data, loading, error } = useEpochCachedStats<HealthStats>(
    apiBaseUrl,
    "/api/v1/stats/health"
  );

  const [selectedKey, setSelectedKey] = useState<string>("city");

  const scope = useMemo<Scope | null>(() => {
    if (!data || data.scopes.length === 0) return null;
    return (
      data.scopes.find((s) => scopeKey(s) === selectedKey) ?? data.scopes[0]
    );
  }, [data, selectedKey]);

  // Sort descending (85+ → 0–4) to display older age bands at the top.
  const deathAgeData = useMemo<DeathAgeBand[]>(() => {
    const dist = scope?.death_age_distribution;
    if (!dist) return [];
    return [...dist.bands].reverse();
  }, [scope]);

  if (loading) {
    return <div className="health-tab__loading">{t("common.loading")}</div>;
  }
  if (error || !data || !scope) {
    return (
      <div className="health-tab__error">
        {t("common.fetchError")}
        {error ? `: ${error}` : ""}
      </div>
    );
  }

  const scopeLabel = (s: Scope): string =>
    s.zone_id == null
      ? t("statistics.cityWide")
      : s.name || `${t("statistics.tabPopulation")} #${s.zone_id}`;

  const hasHistory = scope.history.length > 0;

  return (
    <div className="health-tab">
      {/* ===== Top: aggregation scope selection ===== */}
      <header className="health-tab__header">
        <div className="health-tab__scope">
          <label className="health-tab__scope-label" htmlFor="health-scope-select">
            {t("statistics.scopeLabel")}
          </label>
          <select
            id="health-scope-select"
            className="health-tab__scope-select"
            value={selectedKey}
            onChange={(e) => setSelectedKey(e.target.value)}
          >
            {data.scopes.map((s) => (
              <option key={scopeKey(s)} value={scopeKey(s)}>
                {scopeLabel(s)}
              </option>
            ))}
          </select>
        </div>
      </header>

      {/* ===== Middle: life expectancy history (left) + death-age distribution (right) ===== */}
      <div className="health-tab__top-row">
        <section className="health-chart health-chart--lifespan">
          <h3 className="health-chart__title">{t("statistics.averageLifespan")}</h3>
          <div className="health-chart__area">
            {hasHistory ? (
              <ResponsiveContainer width="100%" height="100%">
                <LineChart data={scope.history} margin={CHART_MARGIN}>
                  <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" />
                  <XAxis dataKey="epoch" tick={AXIS_TICK} tickLine={false} />
                  <YAxis
                    width={remPx(Y_AXIS_WIDTH_REM)}
                    tick={AXIS_TICK}
                    tickLine={false}
                    tickFormatter={(v: number) => `${v}${t("statistics.yearsOldUnit")}`}
                    domain={[0, "auto"]}
                  />
                  <Tooltip
                    contentStyle={TOOLTIP_STYLE}
                    labelStyle={{ color: "#fff" }}
                    formatter={(value: any) => {
                      if (value === null || value === undefined) return ["-", t("statistics.averageLifespan")];
                      return [`${value} ${t("statistics.yearsOldUnit")}`, t("statistics.averageLifespan")];
                    }}
                  />
                  <Line
                    type="monotone"
                    dataKey="avg_death_age"
                    stroke={COLOR_LIFESPAN}
                    strokeWidth={2}
                    dot={{ r: 3, fill: COLOR_LIFESPAN, strokeWidth: 0 }}
                    activeDot={{ r: 5 }}
                    connectNulls={true}
                  />
                </LineChart>
              </ResponsiveContainer>
            ) : (
              <div className="health-chart__empty">{t("statistics.noData")}</div>
            )}
          </div>
        </section>

        {/* Death-age distribution: stacked horizontal bars by five-year age band (85+ at top) and cause of death */}
        <section className="health-chart health-chart--deaths">
          <h3 className="health-chart__title">
            {t("statistics.deathAgeDistribution")}
            {scope.death_age_distribution && scope.death_age_distribution.epochs > 0 && (
              <span className="health-chart__subtitle">
                {t("statistics.deathAgeDistributionEpochs", {
                  count: scope.death_age_distribution.epochs,
                })}
              </span>
            )}
          </h3>
          <div className="health-chart__area">
            {scope.death_age_distribution == null ? (
              // Cause-of-death data by age band is not recorded for District scopes.
              <div className="health-chart__empty">
                {t("statistics.deathAgeDistributionCityOnly")}
              </div>
            ) : deathAgeData.every((b) =>
                DEATH_CAUSES.every((c) => b[c.key] === 0)
              ) ? (
              <div className="health-chart__empty">{t("statistics.noData")}</div>
            ) : (
              <ResponsiveContainer width="100%" height="100%">
                <BarChart
                  layout="vertical"
                  data={deathAgeData}
                  barCategoryGap={1}
                  margin={{ top: 0, right: 15, left: 0, bottom: 0 }}
                >
                  <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" horizontal={false} />
                  <XAxis
                    type="number"
                    tick={AXIS_TICK}
                    tickLine={false}
                    allowDecimals={false}
                    tickFormatter={(v: number) => v.toLocaleString()}
                  />
                  <YAxis
                    type="category"
                    dataKey="band"
                    width={remPx(3.7)}
                    tick={{ fontSize: "0.74rem", fill: "#8ba3bb" }}
                    tickLine={false}
                    interval={0}
                  />
                  <Tooltip
                    contentStyle={TOOLTIP_STYLE}
                    labelStyle={{ color: "#fff" }}
                    formatter={(value: any, name: any) => {
                      const cause = DEATH_CAUSES.find((c) => c.key === name);
                      return [
                        `${Number(value).toLocaleString()} ${t("statistics.people")}`,
                        cause ? t(cause.labelKey) : String(name),
                      ];
                    }}
                  />
                  <Legend
                    wrapperStyle={{ fontSize: "0.93rem" }}
                    iconSize={8}
                    formatter={(value: string) => {
                      const cause = DEATH_CAUSES.find((c) => c.key === value);
                      return cause ? t(cause.labelKey) : value;
                    }}
                  />
                  {DEATH_CAUSES.map((cause) => (
                    <Bar
                      key={cause.key}
                      dataKey={cause.key}
                      stackId="deaths"
                      fill={cause.color}
                      stroke={STACK_STROKE}
                      strokeWidth={1}
                    />
                  ))}
                </BarChart>
              </ResponsiveContainer>
            )}
          </div>
        </section>
      </div>

      {/* ===== Bottom: fulfillment distributions (lower half, two columns) ===== */}
      <div className="health-tab__bottom-row">
        {/* Left half: sanitation fulfillment distribution */}
        <section className="health-chart health-chart--half">
          <h3 className="health-chart__title">{t("statistics.sanitationSatisfaction")}</h3>
          <div className="health-chart__area">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart
                layout="vertical"
                data={scope.sanitation_distribution}
                barCategoryGap={0}
                margin={{ top: 5, right: 15, left: 15, bottom: 5 }}
              >
                <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" />
                <XAxis
                  type="number"
                  tick={AXIS_TICK}
                  tickLine={false}
                  tickFormatter={(v: number) => `${v.toLocaleString()}`}
                />
                <YAxis
                  type="category"
                  dataKey="bin"
                  width={remPx(5.56)}
                  tick={{ fontSize: "0.74rem", fill: "#8ba3bb" }}
                  tickLine={false}
                  interval={0}
                />
                <Tooltip
                  contentStyle={TOOLTIP_STYLE}
                  labelStyle={{ color: "#fff" }}
                  formatter={(value: any) => [`${value} ${t("statistics.householdsUnit")}`, t("statistics.sanitationSatisfaction")]}
                />
                <Bar dataKey="count" fill={COLOR_NUTRITION} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </section>

        {/* Right half: healthcare fulfillment distribution */}
        <section className="health-chart health-chart--half">
          <h3 className="health-chart__title">{t("statistics.healthcareSatisfaction")}</h3>
          <div className="health-chart__area">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart
                layout="vertical"
                data={scope.healthcare_distribution}
                barCategoryGap={0}
                margin={{ top: 5, right: 15, left: 15, bottom: 5 }}
              >
                <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" />
                <XAxis
                  type="number"
                  tick={AXIS_TICK}
                  tickLine={false}
                  tickFormatter={(v: number) => `${v.toLocaleString()}`}
                />
                <YAxis
                  type="category"
                  dataKey="bin"
                  width={remPx(5.56)}
                  tick={{ fontSize: "0.74rem", fill: "#8ba3bb" }}
                  tickLine={false}
                  interval={0}
                />
                <Tooltip
                  contentStyle={TOOLTIP_STYLE}
                  labelStyle={{ color: "#fff" }}
                  formatter={(value: any) => [`${value} ${t("statistics.householdsUnit")}`, t("statistics.healthcareSatisfaction")]}
                />
                <Bar dataKey="count" fill={COLOR_HEALTHCARE} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </section>
      </div>
    </div>
  );
}
