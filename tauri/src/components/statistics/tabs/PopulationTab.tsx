/**
 * 人口タブコンポーネント
 *
 * 統計ウィンドウの「人口」タブの中身。GET /api/v1/stats/population から
 * 都市全体および各地区の人口統計（時系列＋人口ピラミッド）を取得して表示する。
 * データはエポックが変わらない限り再取得しない（useEpochCachedStats によるキャッシュ）。
 *
 * レイアウト:
 * - 上部: 集計範囲（都市全体／各地区）のプルダウンと現在人口（移民内訳付き）。
 * - 下部左 (75%): 横スケールを揃えた3つの時系列グラフを縦に並べる。
 *     1. 人口推移（国民＋移民の積み上げ面グラフ）
 *     2. 出生数＋移民流入数（濃緑＋薄緑の積み上げ棒グラフ）
 *     3. 死亡数＋流出数（濃赤＋薄赤の積み上げ棒グラフ）
 * - 下部右 (25%): 人口ピラミッド（男女で左右、国民／移民で色分け）。
 */

import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  AreaChart,
  Area,
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
} from "recharts";
import { useEpochCachedStats } from "../useEpochCache";
import "./PopulationTab.css";

// --- バックエンド GET /api/v1/stats/population のレスポンス型 ---

type HistoryPoint = {
  epoch: number;
  national: number;
  immigrant: number;
  births: number;
  immigrations: number;
  deaths: number;
  emigrations: number;
};

type PyramidBand = {
  band: string;
  national_male: number;
  national_female: number;
  immigrant_male: number;
  immigrant_female: number;
};

type Scope = {
  zone_id: number | null;
  name: string | null;
  current_population: number;
  current_immigrant: number;
  history: HistoryPoint[];
  pyramid: PyramidBand[];
};

type PopulationStats = {
  epoch: number;
  scopes: Scope[];
};

// --- 配色（全グラフで国民／移民の色を統一する） ---
const COLOR_NATIONAL = "#4a9eff"; // 国民: 青
const COLOR_IMMIGRANT = "#f59e0b"; // 移民: 橙
const COLOR_BIRTH = "#2f9e44"; // 出生: 濃い緑
const COLOR_INFLOW = "#8ce99a"; // 流入: 薄い緑
const COLOR_DEATH = "#c92a2a"; // 死亡: 濃い赤

// 人口ピラミッド用配色（男女で系統分け、国民／移民で濃淡）
const COLOR_M_NATIONAL = "#228be6";   // 男性・国民: 青
const COLOR_M_IMMIGRANT = "#74c0fc";  // 男性・移民: 水色
const COLOR_F_NATIONAL = "#e64980";   // 女性・国民: ピンク
const COLOR_F_IMMIGRANT = "#faa2c1";  // 女性・移民: 淡いピンク

// グラフ間で横スケールを揃えるための共通設定。
const CHART_MARGIN = { top: 6, right: 10, left: 0, bottom: 0 };
const Y_AXIS_WIDTH = 44;
const AXIS_TICK = { fontSize: 9, fill: "#8ba3bb" };
const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "11px",
};

/** プルダウンの選択キー。都市全体は "city"、地区は zone_id の文字列。 */
function scopeKey(scope: Scope): string {
  return scope.zone_id == null ? "city" : String(scope.zone_id);
}

export function PopulationTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const { data, loading, error } = useEpochCachedStats<PopulationStats>(
    apiBaseUrl,
    "/api/v1/stats/population"
  );

  // 選択中の集計範囲（既定は都市全体）。
  const [selectedKey, setSelectedKey] = useState<string>("city");

  const scope = useMemo<Scope | null>(() => {
    if (!data || data.scopes.length === 0) return null;
    return (
      data.scopes.find((s) => scopeKey(s) === selectedKey) ?? data.scopes[0]
    );
  }, [data, selectedKey]);

  // ピラミッド用データ。高齢の帯が上に来るよう逆順にし、男性を負値・女性を正値にする。
  const pyramidData = useMemo(() => {
    if (!scope) return [];
    return [...scope.pyramid].reverse().map((b) => ({
      band: b.band,
      m_national: -b.national_male,
      m_immigrant: -b.immigrant_male,
      f_national: b.national_female,
      f_immigrant: b.immigrant_female,
    }));
  }, [scope]);

  if (loading) {
    return <div className="pop-tab__loading">{t("common.loading")}</div>;
  }
  if (error || !data || !scope) {
    return (
      <div className="pop-tab__error">
        {t("common.fetchError")}
        {error ? `: ${error}` : ""}
      </div>
    );
  }

  const immigrantPct =
    scope.current_population > 0
      ? Math.round((scope.current_immigrant / scope.current_population) * 100)
      : 0;

  const scopeLabel = (s: Scope): string =>
    s.zone_id == null
      ? t("statistics.cityWide")
      : s.name || `${t("statistics.tabPopulation")} #${s.zone_id}`;

  const hasHistory = scope.history.length > 0;

  return (
    <div className="pop-tab">
      {/* ===== 上部: 集計範囲の選択と現在人口 ===== */}
      <header className="pop-tab__header">
        <div className="pop-tab__scope">
          <label className="pop-tab__scope-label" htmlFor="pop-scope-select">
            {t("statistics.scopeLabel")}
          </label>
          <select
            id="pop-scope-select"
            className="pop-tab__scope-select"
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

        <div className="pop-tab__current">
          <span className="pop-tab__current-label">
            {t("statistics.totalPopulation")}
          </span>
          <span className="pop-tab__current-value">
            {scope.current_population.toLocaleString()}
          </span>
          <span className="pop-tab__current-unit">{t("statistics.people")}</span>
          <span className="pop-tab__current-immig">
            {t("statistics.immigrants")}: {scope.current_immigrant.toLocaleString()} (
            {immigrantPct}%)
          </span>
        </div>
      </header>

      {/* ===== 下部: 左に時系列3グラフ、右にピラミッド ===== */}
      <div className="pop-tab__body">
        {/* 左 75%: 時系列グラフ（横スケール共通） */}
        <div className="pop-tab__charts">
          {/* 1. 人口推移（国民＋移民の積み上げ面） */}
          <section className="pop-chart">
            <h3 className="pop-chart__title">{t("statistics.populationTrend")}</h3>
            <div className="pop-chart__area">
              {hasHistory ? (
                <ResponsiveContainer width="100%" height="100%">
                  <AreaChart data={scope.history} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" />
                    <XAxis dataKey="epoch" tick={AXIS_TICK} tickLine={false} />
                    <YAxis width={Y_AXIS_WIDTH} tick={AXIS_TICK} tickLine={false} />
                    <Tooltip contentStyle={TOOLTIP_STYLE} labelStyle={{ color: "#fff" }} />
                    <Area
                      type="monotone"
                      dataKey="national"
                      name={t("statistics.nationals")}
                      stackId="1"
                      stroke={COLOR_NATIONAL}
                      fill={COLOR_NATIONAL}
                      fillOpacity={0.5}
                    />
                    <Area
                      type="monotone"
                      dataKey="immigrant"
                      name={t("statistics.immigrants")}
                      stackId="1"
                      stroke={COLOR_IMMIGRANT}
                      fill={COLOR_IMMIGRANT}
                      fillOpacity={0.5}
                    />
                  </AreaChart>
                </ResponsiveContainer>
              ) : (
                <div className="pop-chart__empty">{t("statistics.noData")}</div>
              )}
            </div>
          </section>

          {/* 2. 出生数＋移民流入数（濃緑＋薄緑の積み上げ棒） */}
          <section className="pop-chart">
            <h3 className="pop-chart__title">{t("statistics.birthsInflow")}</h3>
            <div className="pop-chart__area">
              {hasHistory ? (
                <ResponsiveContainer width="100%" height="100%">
                  <BarChart data={scope.history} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" />
                    <XAxis dataKey="epoch" tick={AXIS_TICK} tickLine={false} />
                    <YAxis width={Y_AXIS_WIDTH} tick={AXIS_TICK} tickLine={false} />
                    <Tooltip contentStyle={TOOLTIP_STYLE} labelStyle={{ color: "#fff" }} />
                    <Bar
                      dataKey="births"
                      name={t("statistics.births")}
                      stackId="g"
                      fill={COLOR_BIRTH}
                    />
                    <Bar
                      dataKey="immigrations"
                      name={t("statistics.immigrations")}
                      stackId="g"
                      fill={COLOR_INFLOW}
                      radius={[2, 2, 0, 0]}
                    />
                  </BarChart>
                </ResponsiveContainer>
              ) : (
                <div className="pop-chart__empty">{t("statistics.noData")}</div>
              )}
            </div>
          </section>

          {/* 3. 死亡数 */}
          <section className="pop-chart">
            <h3 className="pop-chart__title">{t("statistics.deaths")}</h3>
            <div className="pop-chart__area">
              {hasHistory ? (
                <ResponsiveContainer width="100%" height="100%">
                  <BarChart data={scope.history} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" />
                    <XAxis dataKey="epoch" tick={AXIS_TICK} tickLine={false} />
                    <YAxis width={Y_AXIS_WIDTH} tick={AXIS_TICK} tickLine={false} />
                    <Tooltip contentStyle={TOOLTIP_STYLE} labelStyle={{ color: "#fff" }} />
                    <Bar
                      dataKey="deaths"
                      name={t("statistics.deaths")}
                      stackId="r"
                      fill={COLOR_DEATH}
                    />

                  </BarChart>
                </ResponsiveContainer>
              ) : (
                <div className="pop-chart__empty">{t("statistics.noData")}</div>
              )}
            </div>
          </section>
        </div>

        {/* 右 25%: 人口ピラミッド */}
        <div className="pop-tab__pyramid">
          <h3 className="pop-chart__title">{t("statistics.populationPyramid")}</h3>
          {/* 凡例: 男女で色分け、国民／移民で濃淡 */}
          <div className="pop-pyramid__legend">
            <span className="pop-pyramid__legend-item">
              <i style={{ background: COLOR_M_NATIONAL }} />
              {t("statistics.male")}({t("statistics.nationals")})
            </span>
            <span className="pop-pyramid__legend-item">
              <i style={{ background: COLOR_M_IMMIGRANT }} />
              {t("statistics.male")}({t("statistics.immigrants")})
            </span>
            <span className="pop-pyramid__legend-item">
              <i style={{ background: COLOR_F_NATIONAL }} />
              {t("statistics.female")}({t("statistics.nationals")})
            </span>
            <span className="pop-pyramid__legend-item">
              <i style={{ background: COLOR_F_IMMIGRANT }} />
              {t("statistics.female")}({t("statistics.immigrants")})
            </span>
          </div>
          <div className="pop-pyramid__sides">
            <span>◀ {t("statistics.male")}</span>
            <span>{t("statistics.female")} ▶</span>
          </div>
          <div className="pop-pyramid__area">
            {scope.current_population > 0 ? (
              <ResponsiveContainer width="100%" height="100%">
                <BarChart
                  layout="vertical"
                  data={pyramidData}
                  stackOffset="sign"
                  margin={{ top: 4, right: 6, left: 6, bottom: 4 }}
                >
                  <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" />
                  <XAxis
                    type="number"
                    tick={AXIS_TICK}
                    tickLine={false}
                    tickFormatter={(v: number) => String(Math.abs(v))}
                  />
                  <YAxis
                    type="category"
                    dataKey="band"
                    width={36}
                    tick={{ fontSize: 8, fill: "#8ba3bb" }}
                    tickLine={false}
                    interval={0}
                  />
                  <Tooltip
                    contentStyle={TOOLTIP_STYLE}
                    labelStyle={{ color: "#fff" }}
                    formatter={(value: any, name: any) => [Math.abs(Number(value)), name]}
                  />
                  <Bar dataKey="m_national" name={`${t("statistics.male")}(${t("statistics.nationals")})`} stackId="stack" fill={COLOR_M_NATIONAL} />
                  <Bar dataKey="m_immigrant" name={`${t("statistics.male")}(${t("statistics.immigrants")})`} stackId="stack" fill={COLOR_M_IMMIGRANT} />
                  <Bar dataKey="f_national" name={`${t("statistics.female")}(${t("statistics.nationals")})`} stackId="stack" fill={COLOR_F_NATIONAL} />
                  <Bar dataKey="f_immigrant" name={`${t("statistics.female")}(${t("statistics.immigrants")})`} stackId="stack" fill={COLOR_F_IMMIGRANT} />
                </BarChart>
              </ResponsiveContainer>
            ) : (
              <div className="pop-chart__empty">{t("statistics.noData")}</div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
