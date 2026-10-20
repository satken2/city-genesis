/**
 * Employment tab component.
 *
 * Content of the statistics window's "Employment" tab.
 * Fetches employment statistics from GET /api/v1/stats/employment and displays:
 *
 * - Employment summary (KPI cards): employment rate, job fulfillment rate, and average wage.
 * - Unemployment rate history (line chart): unemployment rate time series by Epoch.
 * - Supply/demand balance by wage tier (overlaid horizontal bars): three tiers, uneducated,
 *   standard, and advanced. Draw demand in light bars with dark supply bars overlaid.
 *   Scale the maximum of all six demand/supply values across the three tiers to the chart width.
 * - Job demand by Skill (horizontal bars): fulfillment rates for six Skills.
 * - Employment by industry table (scrollable): detailed list by Variant.
 */

import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
} from "recharts";
import { useEpochCachedStats } from "../useEpochCache";
import "./EmploymentTab.css";
import { useRemPx } from "../../../hooks/useRemPx";

// --- Backend GET /api/v1/stats/employment response types ---

type Summary = {
  labor_force: number;
  employed: number;
  unemployed: number;
  employment_rate: number;
  total_quota: number;
  total_labor: number;
  fill_rate: number;
  average_wage: number;
};

type TierBalance = {
  tier: "uneducated" | "standard" | "high";
  supply: number;
  demand_quota: number;
  employed: number;
  average_wage: number;
};

// One unemployment rate history data point (snapshot for an Epoch).
type UnemploymentHistoryPoint = {
  epoch: number;
  unemployment_rate: number;
};

type SkillDemand = {
  skill_id: number;
  skill_name: string;
  total_quota: number;
  total_labor: number;
  fill_rate: number;
};

type IndustryRow = {
  variant_id: number;
  variant_name: string;
  agent_count: number;
  total_quota: number;
  total_labor: number;
  fill_rate: number;
  average_wage: number;
};

type Scope = {
  zone_id: number | null;
  name: string | null;
  history: UnemploymentHistoryPoint[];
  summary: Summary;
  tier_balance: TierBalance[];
  skill_demand: SkillDemand[];
  industry: IndustryRow[];
};

type EmploymentStats = {
  epoch: number;
  scopes: Scope[];
};

// --- Color definitions ---
const COLOR_UNEDUCATED = "#f59e0b"; // Uneducated: amber.
const COLOR_STANDARD   = "#3b82f6"; // Standard: blue.
const COLOR_HIGH       = "#8b5cf6"; // Advanced: violet.

const COLOR_UNEMPLOYMENT = "#ef4444"; // Unemployment rate line: red.

const COLOR_FILL_OK    = "#10b981"; // Good fulfillment: emerald.
const COLOR_FILL_LOW   = "#f59e0b"; // Moderate fulfillment: amber.
const COLOR_FILL_WARN  = "#ef4444"; // Insufficient fulfillment: red.

// Shared Recharts style constants (same theme as other tabs).
const CHART_MARGIN = { top: 8, right: 16, left: -10, bottom: 0 };
const Y_AXIS_WIDTH_REM = 4.07; // Y-axis width (rem). Convert to px with useRemPx when rendering.
const AXIS_TICK = { fontSize: "0.93rem", fill: "#8ba3bb" };
const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "1rem",
};

function getFillColor(rate: number): string {
  if (rate >= 0.8) return COLOR_FILL_OK;
  if (rate >= 0.5) return COLOR_FILL_LOW;
  return COLOR_FILL_WARN;
}

function formatRate(rate: number): string {
  return `${(rate * 100).toFixed(1)}%`;
}

function formatWage(wage: number): string {
  if (wage === 0) return "-";
  return `$${wage.toFixed(1)}`;
}

// KPI cards.
function KpiCard({
  label,
  value,
  sub,
  color,
}: {
  label: string;
  value: string;
  sub?: string;
  color?: string;
}) {
  return (
    <div className="emp-kpi-card">
      <div className="emp-kpi-card__label">{label}</div>
      <div className="emp-kpi-card__value" style={{ color: color ?? "#f1f5f9" }}>
        {value}
      </div>
      {sub && <div className="emp-kpi-card__sub">{sub}</div>}
    </div>
  );
}

export function EmploymentTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const remPx = useRemPx();
  const { data, loading, error } = useEpochCachedStats<EmploymentStats>(
    apiBaseUrl,
    "/api/v1/stats/employment"
  );

  const [selectedScopeKey, setSelectedScopeKey] = useState("city");

  const scopes = data?.scopes ?? [];

  const currentScope = useMemo(() => {
    if (selectedScopeKey === "city") {
      return scopes.find((s) => s.zone_id === null);
    }
    return scopes.find((s) => String(s.zone_id) === selectedScopeKey);
  }, [scopes, selectedScopeKey]);

  if (loading) {
    return (
      <div className="stats-tab-loading">
        {t("statistics.loading", "読み込み中...")}
      </div>
    );
  }

  if (error || !currentScope) {
    return (
      <div className="stats-tab-error">
        {t("statistics.error", "データの取得に失敗しました。")}
      </div>
    );
  }

  const { summary, tier_balance, skill_demand, industry, history } = currentScope;

  // Scale reference for supply/demand balance by wage tier.
  // Fit the largest of the six values (three tiers × demand/supply) to the chart width,
  // taking the maximum across all supply / demand_quota values.
  const maxTierValue = Math.max(
    ...tier_balance.map((t) => Math.max(t.supply, t.demand_quota))
  );

  const tierLabel = (tier: string) => {
    switch (tier) {
      case "uneducated":
        return t("statistics.employment.tierUneducated", "無教育人材");
      case "standard":
        return t("statistics.employment.tierStandard", "標準人材");
      case "high":
        return t("statistics.employment.tierHigh", "高度人材");
      default:
        return tier;
    }
  };

  const tierColor = (tier: string) => {
    switch (tier) {
      case "uneducated": return COLOR_UNEDUCATED;
      case "standard": return COLOR_STANDARD;
      case "high": return COLOR_HIGH;
      default: return COLOR_STANDARD;
    }
  };

  const skillLabel = (name: string) => {
    switch (name) {
      case "base": return t("statistics.skills.base", "基礎");
      case "physical": return t("statistics.skills.physical", "肉体");
      case "office": return t("statistics.skills.office", "事務");
      case "retail": return t("statistics.skills.retail", "接客");
      case "technology": return t("statistics.skills.technology", "技術");
      case "medical": return t("statistics.skills.medical", "医療");
      default: return name;
    }
  };

  return (
    <div className="emp-tab">
      {/* Scope selector */}
      <header className="emp-tab__header">
        <div className="scope-selector">
          <label htmlFor="emp-scope-select" className="scope-selector__label">
            {t("statistics.scopeLabel", "集計範囲:")}
          </label>
          <select
            id="emp-scope-select"
            className="scope-selector__select"
            value={selectedScopeKey}
            onChange={(e) => setSelectedScopeKey(e.target.value)}
          >
            <option value="city">{t("statistics.cityWide", "都市全体")}</option>
            {scopes
              .filter((s) => s.zone_id !== null)
              .map((s) => (
                <option key={s.zone_id} value={String(s.zone_id)}>
                  {s.name || `${t("statistics.zone", "地区")} ${s.zone_id}`}
                </option>
              ))}
          </select>
        </div>
      </header>

      <div className="emp-tab__body">
        {/* Left column */}
        <div className="emp-tab__left">

          {/* ① KPI summary */}
          <div className="emp-card">
            <h3 className="emp-card__title">
              {t("statistics.employment.summaryTitle", "雇用サマリー")}
            </h3>
            <div className="emp-kpi-row">
              <KpiCard
                label={t("statistics.employment.employmentRate", "就業率")}
                value={formatRate(summary.employment_rate)}
                sub={`${summary.employed.toLocaleString()} / ${summary.labor_force.toLocaleString()} 人`}
                color={getFillColor(summary.employment_rate)}
              />
              <KpiCard
                label={t("statistics.employment.fillRate", "求人充足率")}
                value={formatRate(summary.fill_rate)}
                sub={`${summary.total_labor.toFixed(1)} / ${summary.total_quota.toFixed(1)}`}
                color={getFillColor(summary.fill_rate)}
              />
              <KpiCard
                label={t("statistics.employment.averageWage", "平均賃金")}
                value={formatWage(summary.average_wage)}
                sub={t("statistics.employment.perEpoch", "/エポック")}
              />
            </div>
            <div className="emp-unemployed-row">
              <span className="emp-unemployed-label">
                {t("statistics.employment.unemployed", "失業者数")}:
              </span>
              <span
                className="emp-unemployed-value"
                style={{ color: summary.unemployed > 0 ? COLOR_FILL_WARN : COLOR_FILL_OK }}
              >
                {summary.unemployed.toLocaleString()}
                {t("statistics.people", " 人")}
              </span>
            </div>
          </div>

          {/* ② Unemployment rate history */}
          <div className="emp-card emp-card--chart">
            <h3 className="emp-card__title">
              {t("statistics.employment.unemploymentTrendTitle", "失業率の推移")}
            </h3>
            <div className="emp-chart-container">
              {history.length > 0 ? (
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart data={history} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
                    <XAxis
                      dataKey="epoch"
                      tick={AXIS_TICK}
                      tickLine={false}
                      axisLine={{ stroke: "#253246" }}
                    />
                    <YAxis
                      width={remPx(Y_AXIS_WIDTH_REM)}
                      tick={AXIS_TICK}
                      tickLine={false}
                      axisLine={{ stroke: "#253246" }}
                      domain={[0, 1]}
                      tickFormatter={(v: number) => `${(v * 100).toFixed(0)}%`}
                    />
                    <Tooltip
                      contentStyle={TOOLTIP_STYLE}
                      labelFormatter={(label) => `Epoch ${label}`}
                      formatter={(value: any) =>
                        `${(Number(value) * 100).toFixed(1)}%`
                      }
                    />
                    <Line
                      type="monotone"
                      dataKey="unemployment_rate"
                      name={t("statistics.employment.unemploymentRate", "失業率")}
                      stroke={COLOR_UNEMPLOYMENT}
                      strokeWidth={2}
                      dot={{ r: 2 }}
                      activeDot={{ r: 4 }}
                      isAnimationActive={false}
                    />
                  </LineChart>
                </ResponsiveContainer>
              ) : (
                <div className="emp-chart__no-data">
                  {t("statistics.employment.noTrendData", "推移データがありません")}
                </div>
              )}
            </div>
          </div>

          {/* ③ Supply/demand balance by wage tier */}
          <div className="emp-card">
            <h3 className="emp-card__title">
              {t("statistics.employment.tierBalanceTitle", "賃金ティア別 需給バランス")}
            </h3>
            <div className="emp-tier-legend">
              <span className="emp-tier-legend__item">
                <span
                  className="emp-tier-legend__bar emp-tier-legend__bar--demand"
                />
                {t("statistics.employment.demandLegend", "需要（求人定員）")}
              </span>
              <span className="emp-tier-legend__item">
                <span
                  className="emp-tier-legend__bar emp-tier-legend__bar--supply"
                />
                {t("statistics.employment.supplyLegend", "供給（人材数）")}
              </span>
            </div>
            <div className="emp-tier-list">
              {tier_balance.map((tb) => {
                const supplyWidth = maxTierValue > 0 ? (tb.supply / maxTierValue) * 100 : 0;
                const demandWidth = maxTierValue > 0 ? (tb.demand_quota / maxTierValue) * 100 : 0;
                const color = tierColor(tb.tier);
                return (
                  <div key={tb.tier} className="emp-tier-row">
                    <div
                      className="emp-tier-row__label"
                      style={{ color }}
                    >
                      {tierLabel(tb.tier)}
                    </div>
                    <div className="emp-tier-overlay-track">
                      {/* Demand (job quota): light bar, drawn behind. */}
                      <div
                        className="emp-tier-overlay-bar emp-tier-overlay-bar--demand"
                        style={{ width: `${demandWidth}%`, background: color }}
                        title={`${t("statistics.employment.demandLegend", "需要")}: ${tb.demand_quota.toFixed(0)}`}
                      />
                      {/* Supply (worker count): dark bar, overlaid in front. */}
                      <div
                        className="emp-tier-overlay-bar emp-tier-overlay-bar--supply"
                        style={{ width: `${supplyWidth}%`, background: color }}
                        title={`${t("statistics.employment.supplyLegend", "供給")}: ${tb.supply.toLocaleString()}`}
                      />
                    </div>
                    <div className="emp-tier-row__values">
                      <span className="emp-tier-row__supply" style={{ color }}>
                        {tb.supply.toLocaleString()}
                      </span>
                      <span className="emp-tier-row__sep">/</span>
                      <span className="emp-tier-row__demand">
                        {tb.demand_quota.toFixed(0)}
                      </span>
                    </div>
                    <div className="emp-tier-row__wage">
                      {formatWage(tb.average_wage)}
                    </div>
                  </div>
                );
              })}
            </div>
          </div>

          {/* ③ Job demand by Skill */}
          <div className="emp-card">
            <h3 className="emp-card__title">
              {t("statistics.employment.skillDemandTitle", "スキル別 求人充足率")}
            </h3>
            <div className="emp-skill-list">
              {skill_demand.map((sd) => {
                const fillColor = getFillColor(sd.fill_rate);
                const laborWidth = sd.total_quota > 0 ? (sd.total_labor / sd.total_quota) * 100 : 0;
                return (
                  <div key={sd.skill_id} className="emp-skill-row">
                    <div className="emp-skill-row__label">
                      {skillLabel(sd.skill_name)}
                    </div>
                    <div className="emp-skill-row__bar-area">
                      <div className="emp-skill-bar-bg">
                        <div
                          className="emp-skill-bar-fill"
                          style={{
                            width: `${Math.min(laborWidth, 100)}%`,
                            background: fillColor,
                          }}
                        />
                        {/* Excess indicator */}
                        {laborWidth > 100 && (
                          <div
                            className="emp-skill-bar-overflow"
                            style={{ background: fillColor }}
                          />
                        )}
                      </div>
                    </div>
                    <div
                      className="emp-skill-row__rate"
                      style={{ color: fillColor }}
                    >
                      {sd.total_quota > 0 ? formatRate(sd.fill_rate) : "-"}
                    </div>
                    <div className="emp-skill-row__nums">
                      {sd.total_quota > 0
                        ? `${sd.total_labor.toFixed(0)}/${sd.total_quota.toFixed(0)}`
                        : "需要なし"}
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
        </div>

        {/* Right column: industry table */}
        <div className="emp-tab__right">
          <div className="emp-card emp-card--full">
            <h3 className="emp-card__title">
              {t("statistics.employment.industryTitle", "業種別 雇用状況")}
            </h3>
            <div className="emp-industry-table-wrapper">
              <table className="emp-industry-table">
                <thead>
                  <tr>
                    <th className="emp-industry-table__th emp-industry-table__th--name">
                      {t("statistics.employment.colIndustry", "業種")}
                    </th>
                    <th className="emp-industry-table__th emp-industry-table__th--num">
                      {t("statistics.employment.colCount", "施設数")}
                    </th>
                    <th className="emp-industry-table__th">
                      {t("statistics.employment.colFillRate", "充足率")}
                    </th>
                    <th className="emp-industry-table__th emp-industry-table__th--num">
                      {t("statistics.employment.colWage", "賃金")}
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {industry.map((row) => {
                    const fillColor = getFillColor(row.fill_rate);
                    const barWidth = row.total_quota > 0
                      ? Math.min((row.total_labor / row.total_quota) * 100, 100)
                      : 0;
                    return (
                      <tr key={row.variant_id} className="emp-industry-table__row">
                        <td className="emp-industry-table__td emp-industry-table__td--name">
                          <span className="emp-industry-table__name">
                            {row.variant_name}
                          </span>
                          <span className="emp-industry-table__quota">
                            {row.total_labor.toFixed(0)}/{row.total_quota.toFixed(0)}
                          </span>
                        </td>
                        <td className="emp-industry-table__td emp-industry-table__td--num">
                          {row.agent_count}
                        </td>
                        <td className="emp-industry-table__td emp-industry-table__td--bar">
                          <div className="emp-industry-bar-bg">
                            <div
                              className="emp-industry-bar-fill"
                              style={{
                                width: `${barWidth}%`,
                                background: fillColor,
                              }}
                            />
                          </div>
                          <span
                            className="emp-industry-bar-label"
                            style={{ color: fillColor }}
                          >
                            {row.total_quota > 0 ? formatRate(row.fill_rate) : "-"}
                          </span>
                        </td>
                        <td className="emp-industry-table__td emp-industry-table__td--num">
                          <span style={{ color: "#94a3b8" }}>
                            {formatWage(row.average_wage)}
                          </span>
                        </td>
                      </tr>
                    );
                  })}
                  {industry.length === 0 && (
                    <tr>
                      <td
                        colSpan={4}
                        className="emp-industry-table__empty"
                      >
                        {t("statistics.employment.noIndustryData", "雇用データがありません")}
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
