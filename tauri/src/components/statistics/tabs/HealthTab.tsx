/**
 * 保健衛生タブコンポーネント
 *
 * 統計ウィンドウの「保健衛生」タブの中身。GET /api/v1/stats/health から
 * 都市全体および各地区の保健衛生統計（平均寿命推移＋衛生・医療充足度分布）を取得して表示する。
 * データはエポックが変わらない限り再取得しない（useEpochCachedStats によるキャッシュ）。
 *
 * レイアウト:
 * - 上部: 集計範囲（都市全体／各地区）のプルダウン。
 * - 中部 (上半分): 左右2カラム
 *     - 左側 (50%): 平均寿命グラフ（折れ線グラフ）。死亡した市民の平均年齢推移。
 *     - 右側 (50%): 死亡年齢分布グラフ（5歳刻み年齢帯×死因別の積み上げ横棒グラフ）。
 *       都市全体でのみ集計されるため、地区スコープ選択時は案内文を表示する。
 * - 下部 (下半分): 左右2カラム
 *     - 左側 (50%): 衛生充足度グラフ（世帯の sanitation 分布、横向き棒グラフ、隙間なし）。
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
  Legend,
  ResponsiveContainer,
} from "recharts";
import { useEpochCachedStats } from "../useEpochCache";
import "./HealthTab.css";
import { useRemPx } from "../../../hooks/useRemPx";

// --- バックエンド GET /api/v1/stats/health のレスポンス型 ---

type HistoryPoint = {
  epoch: number;
  avg_death_age: number | null;
};

type DistributionPoint = {
  bin: string;
  count: number;
};

/** 死亡年齢分布の1年齢帯分。band 以外のキーは死因別の死亡者数 */
type DeathAgeBand = {
  band: string;
  natural: number;
  sanitation: number;
  orphan: number;
  traffic: number;
  collapse: number;
};

type DeathAgeDistribution = {
  /** 合算対象になった履歴エポック数 */
  epochs: number;
  /** 年齢帯昇順 (0-4 → 85+) */
  bands: DeathAgeBand[];
};

type Scope = {
  zone_id: number | null;
  name: string | null;
  history: HistoryPoint[];
  sanitation_distribution: DistributionPoint[];
  healthcare_distribution: DistributionPoint[];
  /** 死因別×年齢帯別の死亡者数。都市全体スコープのみ。地区スコープは null */
  death_age_distribution: DeathAgeDistribution | null;
};

type HealthStats = {
  epoch: number;
  scopes: Scope[];
};

// --- 配色 (都市環境や福祉に合ったモダンカラー) ---
const COLOR_LIFESPAN = "#3b82f6"; // 寿命: ブルー
const COLOR_NUTRITION = "#10b981"; // 衛生: エメラルドグリーン
const COLOR_HEALTHCARE = "#06b6d4"; // 医療: シアン

/**
 * 死亡年齢分布の死因別カラー。
 * ダーク面 (#1e293b) に対して明度帯・彩度・CVD(色覚多様性)分離・コントラストを
 * 検証済みのカテゴリカルパレット。積み上げ順＝この配列順（隣接ペアで検証済み）。
 */
const DEATH_CAUSES = [
  { key: "natural", labelKey: "statistics.deathCauseNatural", color: "#3b82f6" },
  { key: "sanitation", labelKey: "statistics.deathCauseSanitation", color: "#d97706" },
  { key: "orphan", labelKey: "statistics.deathCauseOrphan", color: "#8b5cf6" },
  { key: "traffic", labelKey: "statistics.deathCauseTraffic", color: "#0891b2" },
  { key: "collapse", labelKey: "statistics.deathCauseCollapse", color: "#dc2626" },
] as const;

/** 積み上げセグメント間の区切り線。チャート面の背景色に合わせる */
const STACK_STROKE = "#1e293b";

const CHART_MARGIN = { top: 10, right: 15, left: 5, bottom: 5 };
const Y_AXIS_WIDTH_REM = 3.7; // Y軸の幅(rem)。描画時に useRemPx で px に直す
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

  // 死亡年齢分布は高齢帯を上にして表示するため降順 (85+ → 0-4) に並べ替える。
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

      {/* ===== 中部: 平均寿命推移 (左) ＋ 死亡年齢分布 (右) ===== */}
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

        {/* 死亡年齢分布: 5歳刻み年齢帯 (85+ が最上段) × 死因別の積み上げ横棒グラフ */}
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
              // 地区スコープでは死因別×年齢帯のデータを記録していない
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

      {/* ===== 下部: 充足度分布 (下半分、左右2カラム) ===== */}
      <div className="health-tab__bottom-row">
        {/* 左半分: 衛生充足度分布 */}
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

        {/* 右半分: 医療充足度分布 */}
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
