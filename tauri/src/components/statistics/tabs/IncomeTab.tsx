/**
 * Income statistics tab component.
 *
 * Fetches and displays Household income and disposable income distributions, plus demand
 * tier shares and history, from GET /api/v1/stats/income.
 *
 * Layout:
 * - Equal left/right split.
 *   - Left column: vertical split.
 *     - Top left: income distribution (horizontal bars, Recharts BarChart).
 *     - Bottom left: disposable income distribution (horizontal bars, Recharts BarChart).
 *   - Right column: vertical split.
 *     - Top right: current demand tier shares (pie chart, Recharts PieChart).
 *     - Bottom right: demand tier count history (line chart, Recharts LineChart).
 */

import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
  PieChart,
  Pie,
  Cell,
  LineChart,
  Line,
} from "recharts";
import { useEpochCachedStats } from "../useEpochCache";
import "./IncomeTab.css";
import { useRemPx } from "../../../hooks/useRemPx";

// --- Type definitions ---

type DistPoint = {
  bin: string;
  income: number;
  discretionary: number;
};

type TierCounts = {
  tier_0: number;
  tier_1: number;
  tier_2: number;
  tier_3: number;
};

type HistoryPoint = {
  epoch: number;
  tier_0: number;
  tier_1: number;
  tier_2: number;
  tier_3: number;
};

type Scope = {
  zone_id: number | null;
  name: string | null;
  distribution: DistPoint[];
  tiers: TierCounts;
};

type IncomeStats = {
  epoch: number;
  history: HistoryPoint[];
  scopes: Scope[];
};

// --- Color definitions ---
const COLOR_INCOME = "#fab005";        // Average income: gold.
const COLOR_DISCRETIONARY = "#228be6";  // Disposable income: blue.

const COLOR_TIER0 = "#fa5252";          // Tier 0 (extreme poverty): red.
const COLOR_TIER1 = "#fd7e14";          // Tier 1 (necessities only): orange.
const COLOR_TIER2 = "#eeb902";          // Tier 2 (through ordinary goods): yellow.
const COLOR_TIER3 = "#40c057";          // Tier 3 (through luxury goods): green.

// Shared Recharts settings.
const CHART_MARGIN = { top: 10, right: 15, left: -5, bottom: 5 };

export function IncomeTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const remPx = useRemPx();
  const { data, loading, error } = useEpochCachedStats<IncomeStats>(
    apiBaseUrl,
    "/api/v1/stats/income"
  );

  const [selectedScopeKey, setSelectedScopeKey] = useState("city");

  const scopes = data?.scopes ?? [];

  const currentScope = useMemo(() => {
    if (selectedScopeKey === "city") {
      return scopes.find((s) => s.zone_id === null);
    }
    return scopes.find((s) => String(s.zone_id) === selectedScopeKey);
  }, [scopes, selectedScopeKey]);

  // Prepare pie chart data.
  const pieData = useMemo(() => {
    if (!currentScope) return [];
    const tData = currentScope.tiers;
    return [
      { name: t("statistics.tier0Legend", "Tier 0 (極貧)"), value: tData.tier_0, color: COLOR_TIER0 },
      { name: t("statistics.tier1Legend", "Tier 1 (必需品のみ)"), value: tData.tier_1, color: COLOR_TIER1 },
      { name: t("statistics.tier2Legend", "Tier 2 (一般財まで)"), value: tData.tier_2, color: COLOR_TIER2 },
      { name: t("statistics.tier3Legend", "Tier 3 (高級品まで)"), value: tData.tier_3, color: COLOR_TIER3 },
    ];
  }, [currentScope, t]);

  const totalHouseholds = useMemo(() => {
    if (!currentScope) return 0;
    const tData = currentScope.tiers;
    return tData.tier_0 + tData.tier_1 + tData.tier_2 + tData.tier_3;
  }, [currentScope]);

  if (loading) {
    return <div className="income-tab__loading">{t("common.loading")}</div>;
  }

  if (error || !currentScope || !data) {
    return (
      <div className="income-tab__error">
        {t("common.fetchError")}
        {error ? `: ${error}` : ""}
      </div>
    );
  }

  // --- Custom income/disposable income distribution tooltip (one series per chart) ---
  const makeDistBarTooltip = (color: string, legendLabel: string) =>
    function DistBarTooltip({ active, payload, label }: any) {
      if (active && payload && payload.length) {
        return (
          <div className="income-tooltip">
            <p className="income-tooltip__bin">{`${t("statistics.incomeRangeLabel", "所得範囲")}: ${label}`}</p>
            <p className="income-tooltip__item" style={{ color }}>
              {`${legendLabel}: ${payload[0]?.value || 0} ${t("statistics.householdsUnit", "世帯")}`}
            </p>
          </div>
        );
      }
      return null;
    };

  // --- Custom pie chart tooltip ---
  const CustomPieTooltip = ({ active, payload }: any) => {
    if (active && payload && payload.length) {
      const p = payload[0];
      const percent = totalHouseholds > 0 ? ((p.value / totalHouseholds) * 100).toFixed(1) : "0.0";
      return (
        <div className="income-tooltip">
          <p className="income-tooltip__tier" style={{ color: p.payload.color }}>
            {p.name}
          </p>
          <p className="income-tooltip__val">
            {`世帯数: ${p.value.toLocaleString()} 世帯 (${percent}%)`}
          </p>
        </div>
      );
    }
    return null;
  };

  return (
    <div className="income-tab">
      {/* Header (scope selection) */}
      <header className="income-tab__header">
        <div className="scope-selector">
          <label htmlFor="income-scope-select" className="scope-selector__label">
            {t("statistics.scopeLabel", "集計範囲:")}
          </label>
          <select
            id="income-scope-select"
            className="scope-selector__select"
            value={selectedScopeKey}
            onChange={(e) => setSelectedScopeKey(e.target.value)}
          >
            <option value="city">{t("statistics.cityWide", "都市全体")}</option>
            {scopes
              .filter((s) => s.zone_id !== null)
              .map((s) => (
                <option key={s.zone_id} value={String(s.zone_id)}>
                  {s.name || `地区 #${s.zone_id}`}
                </option>
              ))}
          </select>
        </div>
      </header>

      <div className="income-tab__body">
        {/* Left column: income / disposable income distributions (vertically split without overlap) */}
        <div className="income-tab__left">
          <div className="income-card income-card--half">
            <h3 className="income-card__title">
              {t("statistics.incomeDistributionOnly", "所得の分布")}
            </h3>
            <div className="income-chart-container">
              <ResponsiveContainer width="100%" height="100%">
                <BarChart
                  data={currentScope.distribution}
                  layout="vertical"
                  margin={CHART_MARGIN}
                >
                  <CartesianGrid strokeDasharray="3 3" stroke="#253545" horizontal={true} vertical={false} />
                  <XAxis type="number" stroke="#8ba3bb" fontSize="0.83rem" />
                  <YAxis dataKey="bin" type="category" stroke="#8ba3bb" fontSize="0.83rem" width={remPx(7.41)} />
                  <Tooltip
                    content={makeDistBarTooltip(COLOR_INCOME, t("statistics.incomeLegend", "平均所得"))}
                    cursor={{ fill: "rgba(255,255,255,0.03)" }}
                  />
                  <Bar
                    dataKey="income"
                    fill={COLOR_INCOME}
                    name={t("statistics.incomeLegend", "平均所得")}
                    radius={[0, 2, 2, 0]}
                  />
                </BarChart>
              </ResponsiveContainer>
            </div>
          </div>

          <div className="income-card income-card--half">
            <h3 className="income-card__title">
              {t("statistics.discretionaryDistributionOnly", "可処分所得の分布")}
            </h3>
            <div className="income-chart-container">
              <ResponsiveContainer width="100%" height="100%">
                <BarChart
                  data={currentScope.distribution}
                  layout="vertical"
                  margin={CHART_MARGIN}
                >
                  <CartesianGrid strokeDasharray="3 3" stroke="#253545" horizontal={true} vertical={false} />
                  <XAxis type="number" stroke="#8ba3bb" fontSize="0.83rem" />
                  <YAxis dataKey="bin" type="category" stroke="#8ba3bb" fontSize="0.83rem" width={remPx(7.41)} />
                  <Tooltip
                    content={makeDistBarTooltip(COLOR_DISCRETIONARY, t("statistics.discretionaryLegend", "可処分所得"))}
                    cursor={{ fill: "rgba(255,255,255,0.03)" }}
                  />
                  <Bar
                    dataKey="discretionary"
                    fill={COLOR_DISCRETIONARY}
                    name={t("statistics.discretionaryLegend", "可処分所得")}
                    radius={[0, 2, 2, 0]}
                  />
                </BarChart>
              </ResponsiveContainer>
            </div>
          </div>
        </div>

        {/* Right column: pie chart and history chart */}
        <div className="income-tab__right">
          {/* Top right: current demand tier shares */}
          <div className="income-card income-card--half">
            <h3 className="income-card__title">
              {t("statistics.demandTierDistribution", "現在の需要ティア割合")}
            </h3>
            <div className="income-pie-chart-wrapper">
              <div className="income-pie-chart">
                <ResponsiveContainer width="100%" height="100%">
                  <PieChart>
                    <Pie
                      data={pieData}
                      dataKey="value"
                      nameKey="name"
                      cx="50%"
                      cy="50%"
                      innerRadius={45}
                      outerRadius={65}
                      paddingAngle={3}
                    >
                      {pieData.map((entry, index) => (
                        <Cell key={`cell-${index}`} fill={entry.color} />
                      ))}
                    </Pie>
                    <Tooltip content={<CustomPieTooltip />} />
                  </PieChart>
                </ResponsiveContainer>
                {/* Total Household count in the pie chart center */}
                <div className="pie-center-label">
                  <span className="pie-center-label__num">
                    {totalHouseholds.toLocaleString()}
                  </span>
                  <span className="pie-center-label__unit">
                    {t("statistics.householdsUnit", "世帯")}
                  </span>
                </div>
              </div>
              <div className="income-pie-legend">
                {pieData.map((item, idx) => {
                  const percent = totalHouseholds > 0 ? ((item.value / totalHouseholds) * 100).toFixed(1) : "0.0";
                  return (
                    <div key={idx} className="pie-legend-item">
                      <span className="pie-legend-item__dot" style={{ backgroundColor: item.color }} />
                      <span className="pie-legend-item__label">{item.name}</span>
                      <span className="pie-legend-item__val">{`${item.value.toLocaleString()} (${percent}%)`}</span>
                    </div>
                  );
                })}
              </div>
            </div>
          </div>

          {/* Bottom right: Household count history by demand tier */}
          <div className="income-card income-card--half">
            <h3 className="income-card__title">
              {t("statistics.demandTierTrend", "需要ティア世帯数の推移")}
              {selectedScopeKey !== "city" && (
                <span className="income-card__subtitle-hint"> (※都市全体)</span>
              )}
            </h3>
            <div className="income-chart-container">
              {data.history.length === 0 ? (
                <div className="income-tab__empty">{t("statistics.economyTrendNoData", "時系列データがまだありません。")}</div>
              ) : (
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart
                    data={data.history}
                    margin={CHART_MARGIN}
                  >
                    <CartesianGrid strokeDasharray="3 3" stroke="#253545" horizontal={true} vertical={false} />
                    <XAxis dataKey="epoch" stroke="#8ba3bb" fontSize="0.83rem" />
                    <YAxis width={remPx(5.56)} stroke="#8ba3bb" fontSize="0.83rem" />
                    <Tooltip
                      contentStyle={{ background: "#1a2230", border: "1px solid rgba(255,255,255,0.1)", borderRadius: "4px" }}
                      itemStyle={{ fontSize: "0.93rem" }}
                      labelStyle={{ fontSize: "0.93rem", fontWeight: "bold", color: "#8ba3bb" }}
                    />
                    <Line
                      type="monotone"
                      dataKey="tier_3"
                      stroke={COLOR_TIER3}
                      strokeWidth={2}
                      dot={{ r: 2 }}
                      name={t("statistics.tier3Legend", "Tier 3")}
                    />
                    <Line
                      type="monotone"
                      dataKey="tier_2"
                      stroke={COLOR_TIER2}
                      strokeWidth={2}
                      dot={{ r: 2 }}
                      name={t("statistics.tier2Legend", "Tier 2")}
                    />
                    <Line
                      type="monotone"
                      dataKey="tier_1"
                      stroke={COLOR_TIER1}
                      strokeWidth={2}
                      dot={{ r: 2 }}
                      name={t("statistics.tier1Legend", "Tier 1")}
                    />
                    <Line
                      type="monotone"
                      dataKey="tier_0"
                      stroke={COLOR_TIER0}
                      strokeWidth={2}
                      dot={{ r: 2 }}
                      name={t("statistics.tier0Legend", "Tier 0")}
                    />
                  </LineChart>
                </ResponsiveContainer>
              )}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
