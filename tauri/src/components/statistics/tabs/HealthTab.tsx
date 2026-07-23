/**
 * 保健衛生タブコンポーネント
 *
 * 統計ウィンドウの「保健衛生」タブの中身。GET /api/v1/stats/health から
 * 都市全体および各地区の保健衛生統計（平均寿命推移＋栄養・医療充足度分布）を取得して表示する。
 * データはエポックが変わらない限り再取得しない（useEpochCachedStats によるキャッシュ）。
 *
 * レイアウト:
 * - 上部: 集計範囲（都市全体／各地区）のプルダウン。
 * - 中部 (上半分): 平均寿命グラフ（折れ線グラフ）。死亡した市民の平均年齢推移。
 * - 下部 (下半分): 左右2カラム
 *     - 左側 (50%): 栄養充足度グラフ（世帯の nutrition 分布、横向き棒グラフ、隙間なし）。
 *     - 右側 (50%): 医療充足度グラフ（世帯の healthcare 分布、横向き棒グラフ、隙間なし）。
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
  ResponsiveContainer,
} from "recharts";
import { useEpochCachedStats } from "../useEpochCache";
import "./HealthTab.css";

// --- バックエンド GET /api/v1/stats/health のレスポンス型 ---

type HistoryPoint = {
  epoch: number;
  avg_death_age: number | null;
};

type DistributionPoint = {
  bin: string;
  count: number;
};

type Scope = {
  zone_id: number | null;
  name: string | null;
  history: HistoryPoint[];
  nutrition_distribution: DistributionPoint[];
  healthcare_distribution: DistributionPoint[];
};

type HealthStats = {
  epoch: number;
  scopes: Scope[];
};

// --- 配色 (都市環境や福祉に合ったモダンカラー) ---
const COLOR_LIFESPAN = "#3b82f6"; // 寿命: ブルー
const COLOR_NUTRITION = "#10b981"; // 栄養: エメラルドグリーン
const COLOR_HEALTHCARE = "#06b6d4"; // 医療: シアン

const CHART_MARGIN = { top: 10, right: 15, left: 5, bottom: 5 };
const Y_AXIS_WIDTH = 40;
const AXIS_TICK = { fontSize: 9, fill: "#8ba3bb" };
const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "11px",
};

function scopeKey(scope: Scope): string {
  return scope.zone_id == null ? "city" : String(scope.zone_id);
}

export function HealthTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
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
      {/* ===== 上部: 集計範囲の選択 ===== */}
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

      {/* ===== 中部: 平均寿命推移 (上半分) ===== */}
      <div className="health-tab__top-chart">
        <section className="health-chart">
          <h3 className="health-chart__title">{t("statistics.averageLifespan")}</h3>
          <div className="health-chart__area" style={{ height: "200px" }}>
            {hasHistory ? (
              <ResponsiveContainer width="100%" height={180}>
                <LineChart data={scope.history} margin={CHART_MARGIN}>
                  <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" />
                  <XAxis dataKey="epoch" tick={AXIS_TICK} tickLine={false} />
                  <YAxis
                    width={Y_AXIS_WIDTH}
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
      </div>

      {/* ===== 下部: 充足度分布 (下半分、左右2カラム) ===== */}
      <div className="health-tab__bottom-row">
        {/* 左半分: 栄養充足度分布 */}
        <section className="health-chart health-chart--half">
          <h3 className="health-chart__title">{t("statistics.nutritionSatisfaction")}</h3>
          <div className="health-chart__area" style={{ height: "280px" }}>
            <ResponsiveContainer width="100%" height="100%">
              <BarChart
                layout="vertical"
                data={scope.nutrition_distribution}
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
                  width={60}
                  tick={{ fontSize: 8, fill: "#8ba3bb" }}
                  tickLine={false}
                  interval={0}
                />
                <Tooltip
                  contentStyle={TOOLTIP_STYLE}
                  labelStyle={{ color: "#fff" }}
                  formatter={(value: any) => [`${value} ${t("statistics.householdsUnit")}`, t("statistics.nutritionSatisfaction")]}
                />
                <Bar dataKey="count" fill={COLOR_NUTRITION} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </section>

        {/* 右半分: 医療充足度分布 */}
        <section className="health-chart health-chart--half">
          <h3 className="health-chart__title">{t("statistics.healthcareSatisfaction")}</h3>
          <div className="health-chart__area" style={{ height: "280px" }}>
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
                  width={60}
                  tick={{ fontSize: 8, fill: "#8ba3bb" }}
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
