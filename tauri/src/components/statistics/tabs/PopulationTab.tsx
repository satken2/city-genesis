/**
 * Population tab component.
 *
 * Content of the statistics window's "Population" tab. Fetches and displays city-wide and
 * per-District population statistics (time series + population pyramid) from
 * GET /api/v1/stats/population. Data is not fetched again until the Epoch changes
 * (cached by useEpochCachedStats).
 *
 * Layout:
 * - Top: scope dropdown (entire city / individual Districts) and current population
 *   (with an Immigrant breakdown).
 * - Bottom left (75%): three vertically stacked time series charts sharing the horizontal scale.
 *     1. Population history (stacked area chart of nationals + Immigrants).
 *     2. Births + Immigrant arrivals (stacked dark green + light green bars).
 *     3. Deaths + departures (stacked dark red + light red bars).
 * - Bottom right (25%): population pyramid (men/women on opposite sides, nationals/Immigrants
 *   distinguished by color), with day/night population by District below it
 *   (from GET /api/v1/stats/commute).
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
import { useRemPx } from "../../../hooks/useRemPx";

// --- Fields used for District population from the backend GET /api/v1/stats/commute response types ---

/** Nighttime (residential) and daytime population by District. The UI calculates the day/night ratio. */
type ZonePopPoint = {
  zone_id: number;
  name: string;
  night_population: number;
  daytime_population: number;
};

type CommuteZoneStats = {
  zone_populations: ZonePopPoint[];
};

// --- Backend GET /api/v1/stats/population response types ---

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

// --- Colors (consistent colors for nationals/Immigrants across all charts) ---
const COLOR_NATIONAL = "#4a9eff"; // Nationals: blue.
const COLOR_IMMIGRANT = "#f59e0b"; // Immigrants: orange.
const COLOR_BIRTH = "#2f9e44"; // Births: dark green.
const COLOR_INFLOW = "#8ce99a"; // Arrivals: light green.
const COLOR_DEATH = "#c92a2a"; // Deaths: dark red.

// Population pyramid colors (hue by gender, shade by national/Immigrant status).
const COLOR_M_NATIONAL = "#228be6";   // Men, nationals: blue.
const COLOR_M_IMMIGRANT = "#74c0fc";  // Men, Immigrants: light blue.
const COLOR_F_NATIONAL = "#e64980";   // Women, nationals: pink.
const COLOR_F_IMMIGRANT = "#faa2c1";  // Women, Immigrants: pale pink.

// Shared settings for consistent horizontal scales across charts.
const CHART_MARGIN = { top: 6, right: 10, left: 0, bottom: 0 };
const Y_AXIS_WIDTH_REM = 4.07; // Y-axis width (rem). Convert to px with useRemPx when rendering.
const AXIS_TICK = { fontSize: "0.83rem", fill: "#8ba3bb" };
const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "1rem",
};

/** Dropdown selection key: "city" for the entire city, zone_id as a string for a District. */
function scopeKey(scope: Scope): string {
  return scope.zone_id == null ? "city" : String(scope.zone_id);
}

/**
 * Day/night population table by District.
 *
 * The ratio of daytime to nighttime (residence-based) population indicates whether a District
 * leans toward employment or housing. Values come from the commute statistics API, so fetching
 * is contained within this table.
 */
function ZonePopulationTable({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const { data, error } = useEpochCachedStats<CommuteZoneStats>(
    apiBaseUrl,
    "/api/v1/stats/commute"
  );
  const zones = data?.zone_populations ?? [];

  return (
    <section className="pop-zones">
      <h3 className="pop-chart__title">{t("statistics.zonePopulationTitle")}</h3>
      <div className="pop-zones__table-container">
        <table className="pop-zones__table">
          <thead>
            <tr>
              <th>{t("statistics.zoneName")}</th>
              <th className="pop-zones__num">{t("statistics.nightPopulation")}</th>
              <th className="pop-zones__num">{t("statistics.daytimePopulation")}</th>
              <th className="pop-zones__num">{t("statistics.dayNightRatio")}</th>
            </tr>
          </thead>
          <tbody>
            {zones.length === 0 ? (
              <tr>
                <td colSpan={4} className="pop-zones__empty">
                  {/* Do not label fetch failures as "No data", since that would obscure diagnosis. */}
                  {error ? t("common.fetchError") : t("statistics.noZoneData")}
                </td>
              </tr>
            ) : (
              zones.map((z) => {
                const ratio =
                  z.night_population > 0
                    ? Math.round((z.daytime_population / z.night_population) * 100)
                    : 0;
                let ratioClass = "pop-ratio--balanced";
                if (ratio > 120) ratioClass = "pop-ratio--day";
                else if (ratio < 80) ratioClass = "pop-ratio--night";

                return (
                  <tr key={z.zone_id}>
                    <td className="pop-zones__name">{z.name}</td>
                    <td className="pop-zones__num">{z.night_population.toLocaleString()}</td>
                    <td className="pop-zones__num">{z.daytime_population.toLocaleString()}</td>
                    <td className="pop-zones__num">
                      <span className={`pop-ratio ${ratioClass}`}>
                        {z.night_population > 0 ? `${ratio}%` : "0%"}
                      </span>
                    </td>
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      </div>
    </section>
  );
}

export function PopulationTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const remPx = useRemPx();
  const { data, loading, error } = useEpochCachedStats<PopulationStats>(
    apiBaseUrl,
    "/api/v1/stats/population"
  );

  // Selected aggregation scope (defaults to the entire city).
  const [selectedKey, setSelectedKey] = useState<string>("city");

  const scope = useMemo<Scope | null>(() => {
    if (!data || data.scopes.length === 0) return null;
    return (
      data.scopes.find((s) => scopeKey(s) === selectedKey) ?? data.scopes[0]
    );
  }, [data, selectedKey]);

  // Pyramid data. Reverse age bands so older ones appear above; use negative values for men and positive values for women.
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
      {/* ===== Top: scope selection and current population ===== */}
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

      {/* ===== Bottom: three time series charts on the left, pyramid on the right ===== */}
      <div className="pop-tab__body">
        {/* Left 75%: time series charts (shared horizontal scale) */}
        <div className="pop-tab__charts">
          {/* 1. Population history (stacked areas for nationals + Immigrants) */}
          <section className="pop-chart">
            <h3 className="pop-chart__title">{t("statistics.populationTrend")}</h3>
            <div className="pop-chart__area">
              {hasHistory ? (
                <ResponsiveContainer width="100%" height="100%">
                  <AreaChart data={scope.history} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" />
                    <XAxis dataKey="epoch" tick={AXIS_TICK} tickLine={false} />
                    <YAxis width={remPx(Y_AXIS_WIDTH_REM)} tick={AXIS_TICK} tickLine={false} />
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

          {/* 2. Births + Immigrant arrivals (stacked dark green + light green bars) */}
          <section className="pop-chart">
            <h3 className="pop-chart__title">{t("statistics.birthsInflow")}</h3>
            <div className="pop-chart__area">
              {hasHistory ? (
                <ResponsiveContainer width="100%" height="100%">
                  <BarChart data={scope.history} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" />
                    <XAxis dataKey="epoch" tick={AXIS_TICK} tickLine={false} />
                    <YAxis width={remPx(Y_AXIS_WIDTH_REM)} tick={AXIS_TICK} tickLine={false} />
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

          {/* 3. Deaths */}
          <section className="pop-chart">
            <h3 className="pop-chart__title">{t("statistics.deaths")}</h3>
            <div className="pop-chart__area">
              {hasHistory ? (
                <ResponsiveContainer width="100%" height="100%">
                  <BarChart data={scope.history} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" />
                    <XAxis dataKey="epoch" tick={AXIS_TICK} tickLine={false} />
                    <YAxis width={remPx(Y_AXIS_WIDTH_REM)} tick={AXIS_TICK} tickLine={false} />
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

        {/* Right 25%: population pyramid */}
        <div className="pop-tab__pyramid">
          <h3 className="pop-chart__title">{t("statistics.populationPyramid")}</h3>
          {/* Legend: hue by gender, shade by national/Immigrant status */}
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
                    width={remPx(3.33)}
                    tick={{ fontSize: "0.74rem", fill: "#8ba3bb" }}
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

          <ZonePopulationTable apiBaseUrl={apiBaseUrl} />
        </div>
      </div>
    </div>
  );
}
