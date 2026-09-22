/**
 * 満足度・余暇・住環境タブコンポーネント
 *
 * 統計ウィンドウの「満足度」「余暇」「住環境」の3タブが共有する中身。
 * GET /api/v1/stats/wellbeing から確定済みの都市全体または選択地区の平均(直近HISTORY_COUNTエポック（現在120）)を取得し、
 * `view` に応じて表示する指標の組を切り替える。
 * データはエポックが変わらない限り再取得しない(useEpochCachedStats によるキャッシュ)。
 *
 * レイアウト(他の統計タブと同じカード構成で、ウィンドウの高さに収める):
 * - 上部: 表示中の指標の説明文(1〜2行)。
 * - 中段: 現在値のKPIカード列(表示指標 + 世帯満足度)。
 * - 下段: 推移グラフのカード(縦の残り全部)。「満足度」表示のときだけ右側に
 *   満足度の構成カード(四軸の重み付き貢献点のドーナツ + 内訳)を並べる。
 *
 * 満足度系の値はバックエンドの 0〜1 を100点換算で表示し、
 * 時間(可処分時間・所定労働時間)はそのまま 時間/日 で表示する。
 * 「余暇」表示では時間の系列だけ右軸に載せる。
 */

import { useMemo, useState, type CSSProperties } from "react";
import { useTranslation } from "react-i18next";
import {
  LineChart,
  Line,
  PieChart,
  Pie,
  Cell,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ResponsiveContainer,
} from "recharts";
import { useEpochCachedStats } from "../useEpochCache";
import { resolveIntlLocale } from "../../../utils/locale";
import { formatEpochDate } from "../../../utils/date";
import "./WellbeingTab.css";

type View = "satisfaction" | "leisure" | "comfort";

// --- バックエンド GET /api/v1/stats/wellbeing のレスポンス型 ---

/** 1エポック分の都市平均。世帯が0のエポックは指標が null(欠測)になる */
type HistoryPoint = {
  epoch: number;
  date: number;
  households: number;
  [key: string]: number | null;
};

type WellbeingStats = {
  epoch: number;
  scopes: { zone_id: number | null; name: string | null }[];
  history: HistoryPoint[];
  current: HistoryPoint | null;
  weights: Record<string, number>;
};

const AXES = ["wealth", "leisure", "health", "comfort"] as const;

/** 表示ごとに折れ線で並べる系列。世帯満足度はどの表示でも末尾に重ねる */
const VIEW_KEYS: Record<View, string[]> = {
  satisfaction: AXES.map((axis) => `satis_${axis}`),
  leisure: ["leisure_hours", "work_hours", "satis_leisure"],
  comfort: ["satis_residential", "quiet", "sunlight", "safety", "satis_comfort"],
};

/** 表示ごとのアクセント色(カード見出しの左罫線) */
const VIEW_ACCENT: Record<View, string> = {
  satisfaction: "#38bdf8",
  leisure: "#f59e0b",
  comfort: "#10b981",
};

// 四軸の色は折れ線と構成ドーナツで共有する。
const AXIS_COLORS: Record<(typeof AXES)[number], string> = {
  wealth: "#f59e0b",
  leisure: "#38bdf8",
  health: "#10b981",
  comfort: "#a78bfa",
};

/** 系列色。KPIカードの値と折れ線を同じ色にして対応づける */
const SERIES_COLORS: Record<string, string> = {
  satis_wealth: AXIS_COLORS.wealth,
  satis_leisure: AXIS_COLORS.leisure,
  satis_health: AXIS_COLORS.health,
  satis_comfort: AXIS_COLORS.comfort,
  leisure_hours: "#f59e0b",
  work_hours: "#f87171",
  satis_residential: "#f59e0b",
  quiet: "#38bdf8",
  sunlight: "#10b981",
  safety: "#f87171",
  satisfaction: "#e2e8f0",
};

const CHART_MARGIN = { top: 8, right: 12, left: 0, bottom: 0 };
const Y_AXIS_WIDTH = 40;
const AXIS_TICK = { fontSize: 10, fill: "#8ba3bb" };
const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "11px",
};

/** 時間系列(時間/日)か。それ以外は 0〜1 の満足度系で100点換算する */
function isHoursKey(key: string): boolean {
  return key.endsWith("_hours");
}

function toDisplayValue(key: string, raw: number | null | undefined): number | null {
  if (raw == null) return null;
  return isHoursKey(key) ? raw : raw * 100;
}

function KpiCard({
  label,
  value,
  unit,
  color,
  emphasized,
}: {
  label: string;
  value: number | null;
  unit: string;
  color: string;
  emphasized?: boolean;
}) {
  return (
    <div className={`wellbeing-kpi-card${emphasized ? " wellbeing-kpi-card--total" : ""}`}>
      <div className="wellbeing-kpi-card__label">{label}</div>
      <div className="wellbeing-kpi-card__value" style={{ color }}>
        {value == null ? "—" : value.toFixed(1)}
        <span className="wellbeing-kpi-card__unit">{unit}</span>
      </div>
    </div>
  );
}

export function WellbeingTab({ apiBaseUrl, view }: { apiBaseUrl: string; view: View }) {
  const { t, i18n } = useTranslation();
  const [selectedKey, setSelectedKey] = useState("city");
  const { data, loading, error } = useEpochCachedStats<WellbeingStats>(
    apiBaseUrl,
    `/api/v1/stats/wellbeing${selectedKey === "city" ? "" : `?zone_id=${selectedKey}`}`
  );

  const keys = VIEW_KEYS[view];
  const history = data?.history ?? [];

  // 横軸ラベルは日付にし、系列値は表示単位へ換算しておく。
  const chartData = useMemo(() => {
    const locale = resolveIntlLocale(i18n.language);
    return history.map((p) => ({
      label: formatEpochDate(p.date, locale),
      ...Object.fromEntries(
        [...keys, "satisfaction"].map((k) => [k, toDisplayValue(k, p[k])])
      ),
    }));
  }, [history, keys, i18n.language]);

  if (loading) {
    return <div className="wellbeing-tab__loading">{t("common.loading")}</div>;
  }
  if (error || !data) {
    return (
      <div className="wellbeing-tab__error">
        {t("common.fetchError")}
        {error ? `: ${error}` : ""}
      </div>
    );
  }
  const current = data.current;
  const scopeSelector = <div className="wellbeing-scope">
    <label htmlFor="wellbeing-scope-select">{t("statistics.scopeLabel")}</label>
    <select id="wellbeing-scope-select" value={selectedKey} onChange={e => setSelectedKey(e.target.value)}>
      {data.scopes.map(scope => <option key={scope.zone_id ?? "city"} value={scope.zone_id ?? "city"}>
        {scope.zone_id == null ? t("statistics.cityWide") : `${scope.name ?? ""} #${scope.zone_id}`}
      </option>)}
    </select>
  </div>;
  if (!current) {
    return <div className="wellbeing-tab">{scopeSelector}<div className="wellbeing-tab__empty">{t("wellbeing.empty")}</div></div>;
  }

  const unitOf = (key: string) => t(isHoursKey(key) ? "wellbeing.hours" : "wellbeing.points");

  // 満足度の構成: 各軸の平均値 × 重み を100点換算した貢献点。合計が世帯満足度になる。
  const slices = AXES.map((axis) => ({
    key: axis,
    name: t(`wellbeing.satis_${axis}`),
    value: Number(current[`satis_${axis}`] ?? 0) * (data.weights[axis] ?? 0) * 100,
    color: AXIS_COLORS[axis],
  }));
  const satisfactionNow = toDisplayValue("satisfaction", current.satisfaction);
  // 一周は常に100点。未達分を背景色の区画として残し、貢献点だけを着色する。
  const donutSlices = [...slices, {
    key: "unfilled",
    name: t("wellbeing.unfilled"),
    value: Math.max(0, 100 - slices.reduce((sum, slice) => sum + slice.value, 0)),
    color: "rgba(255,255,255,0.06)",
  }];
  const showDots = chartData.length === 1;

  return (
    <div
      className="wellbeing-tab"
      style={{ "--wellbeing-accent": VIEW_ACCENT[view] } as CSSProperties}
    >
      {scopeSelector}
      <p className="wellbeing-tab__description">{t(`wellbeing.${view}Description`)}</p>

      {/* ===== 中段: 現在値のKPIカード列 ===== */}
      <div className="wellbeing-kpi-row">
        {keys.map((key) => (
          <KpiCard
            key={key}
            label={t(`wellbeing.${key}`)}
            value={toDisplayValue(key, current[key])}
            unit={unitOf(key)}
            color={SERIES_COLORS[key]}
          />
        ))}
        <KpiCard
          label={t("wellbeing.satisfaction")}
          value={satisfactionNow}
          unit={t("wellbeing.points")}
          color={SERIES_COLORS.satisfaction}
          emphasized
        />
      </div>

      {/* ===== 下段: 推移グラフ(＋満足度表示のみ構成ドーナツ) ===== */}
      <div className="wellbeing-tab__body">
        <section className="wellbeing-card wellbeing-card--chart">
          <h3 className="wellbeing-card__title">{t("wellbeing.history")}</h3>
          <div className="wellbeing-chart">
            <ResponsiveContainer width="100%" height="100%">
              <LineChart data={chartData} margin={CHART_MARGIN}>
                <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" />
                <XAxis dataKey="label" tick={AXIS_TICK} tickLine={false} minTickGap={40} />
                <YAxis
                  yAxisId="score"
                  width={Y_AXIS_WIDTH}
                  domain={[0, 100]}
                  tick={AXIS_TICK}
                  tickLine={false}
                />
                {view === "leisure" && (
                  <YAxis
                    yAxisId="hours"
                    orientation="right"
                    width={Y_AXIS_WIDTH}
                    domain={[0, 16]}
                    tick={AXIS_TICK}
                    tickLine={false}
                  />
                )}
                <Tooltip
                  contentStyle={TOOLTIP_STYLE}
                  labelStyle={{ color: "#fff" }}
                  formatter={(value: any, name: any, item: any) => [
                    `${Number(value).toFixed(1)} ${unitOf(String(item?.dataKey ?? ""))}`,
                    name,
                  ]}
                />
                <Legend wrapperStyle={{ fontSize: 10 }} iconSize={8} />
                {keys.map((key) => (
                  <Line
                    key={key}
                    yAxisId={isHoursKey(key) ? "hours" : "score"}
                    dataKey={key}
                    name={t(`wellbeing.${key}`)}
                    stroke={SERIES_COLORS[key]}
                    strokeWidth={2}
                    dot={showDots}
                    isAnimationActive={false}
                  />
                ))}
                <Line
                  yAxisId="score"
                  dataKey="satisfaction"
                  name={t("wellbeing.satisfaction")}
                  stroke={SERIES_COLORS.satisfaction}
                  strokeWidth={3}
                  dot={showDots}
                  isAnimationActive={false}
                />
              </LineChart>
            </ResponsiveContainer>
          </div>
        </section>

        {view === "satisfaction" && (
          <section className="wellbeing-card wellbeing-card--breakdown">
            <h3 className="wellbeing-card__title">{t("wellbeing.composition")}</h3>
            <p className="wellbeing-card__note">{t("wellbeing.compositionDescription")}</p>
            <div className="wellbeing-breakdown__chart">
              <ResponsiveContainer width="100%" height="100%">
                <PieChart>
                  <Pie
                    data={donutSlices}
                    dataKey="value"
                    nameKey="name"
                    innerRadius="62%"
                    outerRadius="90%"
                    stroke="none"
                    startAngle={90}
                    endAngle={-270}
                    isAnimationActive={false}
                  >
                    {donutSlices.map((s) => (
                      <Cell key={s.key} fill={s.color} />
                    ))}
                  </Pie>
                  <Tooltip
                    contentStyle={TOOLTIP_STYLE}
                    formatter={(value: any, name: any) => [
                      `${Number(value).toFixed(1)} ${t("wellbeing.points")}`,
                      name,
                    ]}
                  />
                </PieChart>
              </ResponsiveContainer>
              <div className="wellbeing-breakdown__center">
                <span className="wellbeing-breakdown__center-value">
                  {satisfactionNow == null ? "—" : satisfactionNow.toFixed(1)}
                </span>
                <span className="wellbeing-breakdown__center-label">
                  {t("wellbeing.satisfaction")}
                </span>
              </div>
            </div>
            <ul className="wellbeing-breakdown__legend">
              {slices.map((s) => (
                <li key={s.key} className="wellbeing-breakdown__item">
                  <i style={{ background: s.color }} />
                  <span className="wellbeing-breakdown__item-name">{s.name}</span>
                  <span className="wellbeing-breakdown__item-value">
                    {s.value.toFixed(1)} {t("wellbeing.points")}
                  </span>
                </li>
              ))}
            </ul>
          </section>
        )}
      </div>
    </div>
  );
}
