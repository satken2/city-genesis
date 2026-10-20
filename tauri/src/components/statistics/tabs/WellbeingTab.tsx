/**
 * Satisfaction, leisure, and Living environment tab component.
 *
 * Shared content for the statistics window's "Satisfaction", "Leisure", and "Living environment"
 * tabs. Fetches finalized city-wide or selected-District averages for the latest HISTORY_COUNT
 * Epochs (currently 120) from GET /api/v1/stats/wellbeing, and selects metrics according to `view`.
 * Data is not fetched again until the Epoch changes (cached by useEpochCachedStats).
 *
 * Layout (fits within window height):
 * - "Satisfaction" and "Living environment" (VIEW_HAS_INDICATOR): scope at the top, with two
 *   equal-width columns below. The left stacks a history chart (emphasizing the selected metric
 *   while drawing others thinly and faintly) and a composition chart (stacked contribution areas
 *   whose height equals the view's overall metric). The right stacks metric buttons, selected
 *   metric description/current value/District range, and a District choropleth map.
 * - "Leisure": explanatory text → current-value KPI cards → history chart, stacked vertically.
 *   No metric switching because hours/day and points cannot be listed as a single metric.
 *
 * Satisfaction values from the backend's 0–1 range are displayed on a 100-point scale;
 * time values (disposable time and scheduled working time) remain in hours/day.
 * In "Leisure", only time series use the right axis.
 */

import { useMemo, useState, type CSSProperties } from "react";
import { useTranslation } from "react-i18next";
import {
  LineChart,
  Line,
  AreaChart,
  Area,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ResponsiveContainer,
} from "recharts";
import {
  CityMapCanvas,
  getZoneFillRampStops,
  type CityMapLayerVisibility,
  type CityMapZoneFill,
} from "../../map/CityMapCanvas";
import { useEpochCachedStats } from "../useEpochCache";
import { resolveIntlLocale } from "../../../utils/locale";
import { formatEpochDate } from "../../../utils/date";
import "./WellbeingTab.css";
import { useRemPx } from "../../../hooks/useRemPx";

type View = "satisfaction" | "leisure" | "comfort";

// --- Backend GET /api/v1/stats/wellbeing response types ---

/** City averages for one Epoch. Metrics are null (missing) in Epochs with no Households. */
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

// --- Backend GET /api/v1/stats/wellbeing/zones response types ---

/** Current values by District for the latest finalized Epoch. null (missing) where the Household population is zero. */
type WellbeingZone = {
  zone_id: number;
  name: string | null;
  households: number;
  values: Record<string, number | null>;
};

type WellbeingZoneStats = {
  /** Finalized Epoch represented by the snapshot. null when no Epoch has run and no history exists. */
  epoch: number | null;
  date: number | null;
  zones: WellbeingZone[];
};

const AXES = ["wealth", "leisure", "health", "comfort"] as const;

/** Line series for each view. Excludes the view's overall metric (VIEW_TOTAL), which is overlaid last. */
const VIEW_KEYS: Record<View, string[]> = {
  satisfaction: AXES.map((axis) => `satis_${axis}`),
  leisure: ["leisure_hours", "work_hours", "satis_leisure"],
  comfort: ["satis_residential", "quiet", "sunlight", "safety"],
};

/**
 * Overall metric for each view. Living environment uses living comfort, matching its stacked
 * composition chart. Household satisfaction includes axes beyond the four Living environment
 * elements, so placing it alongside their breakdown would misleadingly suggest it is their total.
 */
const VIEW_TOTAL: Record<View, string> = {
  satisfaction: "satisfaction",
  leisure: "satisfaction",
  comfort: "satis_comfort",
};

/**
 * Views with the right column for metric buttons, selected metric details, and a District map.
 * Excludes "Leisure", where disposable time (hours/day) and satisfaction (points) cannot be
 * listed as a single metric.
 */
const VIEW_HAS_INDICATOR: Record<View, boolean> = {
  satisfaction: true,
  leisure: false,
  comfort: true,
};

/** One band in the composition chart (stacked areas). */
type CompositionPart = {
  /** dataKey within the chart. */
  id: string;
  /** Key for the value read from history. */
  source: string;
  /** Key for the color in SERIES_COLORS, also matched against the metric selection. */
  series: string;
  /** Display-name i18n key. Defaults to series (specified only for bands whose values differ from the original metric). */
  labelKey?: string;
  /** When specified, multiply by weights[axis]. For metrics whose actual contributions are absent from history. */
  weightAxis?: string;
};

/**
 * Composition charts by view. Include only combinations whose stacked height equals the view's overall metric.
 *
 * Satisfaction is a weighted sum of four axes, so multiply historical axis values by weights
 * to obtain contributions. Living environment is "average of four elements × housing fulfillment",
 * a product that cannot be decomposed into a sum. The backend therefore records contributions
 * after applying the multiplier (comfort_*), and the UI does no multiplication, avoiding
 * a duplicate implementation of the simulation formula.
 */
const VIEW_COMPOSITION: Partial<Record<View, CompositionPart[]>> = {
  satisfaction: AXES.map((axis) => ({
    id: axis,
    source: `satis_${axis}`,
    series: `satis_${axis}`,
    weightAxis: axis,
  })),
  comfort: [
    // The housing band is a diminishing-returns housing evaluation rather than raw fulfillment, so use a separate display name.
    { id: "housing", source: "comfort_housing", series: "satis_residential", labelKey: "comfort_housing" },
    { id: "quiet", source: "comfort_quiet", series: "quiet" },
    { id: "sunlight", source: "comfort_sunlight", series: "sunlight" },
    { id: "safety", source: "comfort_safety", series: "safety" },
  ],
};

/** Accent color by view (left border of card headings). */
const VIEW_ACCENT: Record<View, string> = {
  satisfaction: "#38bdf8",
  leisure: "#f59e0b",
  comfort: "#10b981",
};

// Share the four axis colors between lines and metric buttons.
const AXIS_COLORS: Record<(typeof AXES)[number], string> = {
  wealth: "#f59e0b",
  leisure: "#38bdf8",
  health: "#10b981",
  comfort: "#a78bfa",
};

/** Series colors. Match KPI values, lines, and metric buttons to make their correspondence clear. */
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
const Y_AXIS_WIDTH_REM = 3.7; // Y-axis width (rem). Convert to px with useRemPx when rendering.
const AXIS_TICK = { fontSize: "0.93rem", fill: "#8ba3bb" };
const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "1rem",
};

/** Opacity of unselected series. Color intensity establishes their hierarchy relative to the selected metric. */
const DIMMED_OPACITY = 0.3;

/** Stacked-area fill intensity (selected axis / unselected / uniform when the overall metric is selected). */
const AREA_OPACITY_ACTIVE = 0.85;
const AREA_OPACITY_INACTIVE = 0.4;
const AREA_OPACITY_NEUTRAL = 0.6;

/**
 * District map layers. This map emphasizes District colors, so omit Parcel/building outlines
 * and elevation coloring, leaving only coastlines and roads for orientation.
 * Keep this as a module constant to avoid triggering the layer-change effect on every render.
 */
const MAP_LAYERS: Partial<CityMapLayerVisibility> = {
  coastline: true,
  trunkRoads: true,
  allRoads: true,
  railways: false,
  parcels: false,
  buildings: false,
  stations: false,
  zoneBorders: true,
  elevation: false,
  contours: false,
  grid: false,
};

/** Whether this is a time series (hours/day). Other metrics use 0–1 satisfaction values converted to 100 points. */
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

/**
 * Right column containing metric switching, selected metric details, and a District choropleth
 * map (satisfaction / Living environment views).
 *
 * Only this view uses current District values, so fetching is contained here (not mounted in
 * leisure view, so no request occurs). The parent holds the selected metric state because it
 * is also shared with history chart emphasis.
 */
function WellbeingSidePanel({
  apiBaseUrl,
  keys,
  indicator,
  onSelectIndicator,
  current,
}: {
  apiBaseUrl: string;
  keys: string[];
  indicator: string;
  onSelectIndicator: (key: string) => void;
  current: HistoryPoint;
}) {
  const { t } = useTranslation();
  const { data, error } = useEpochCachedStats<WellbeingZoneStats>(
    apiBaseUrl,
    "/api/v1/stats/wellbeing/zones"
  );

  // Current District values for the selected metric. Leave missing Districts (zero Household population) uncolored.
  const zoneValues = useMemo(() => {
    const entries: { zoneId: number; value: number }[] = [];
    for (const zone of data?.zones ?? []) {
      const value = zone.values?.[indicator];
      if (typeof value === "number") entries.push({ zoneId: zone.zone_id, value });
    }
    return entries;
  }, [data, indicator]);

  const zoneRange = useMemo(() => {
    if (!zoneValues.length) return null;
    let min = Infinity;
    let max = -Infinity;
    for (const { value } of zoneValues) {
      if (value < min) min = value;
      if (value > max) max = value;
    }
    return { min, max };
  }, [zoneValues]);

  // Colors stretch the District min–max range across the full scale so even small differences remain visible.
  // Absolute comparisons across Epochs/metrics are therefore impossible; include the actual value range in the legend.
  const zoneFill = useMemo<CityMapZoneFill | undefined>(() => {
    if (!zoneRange) return undefined;
    const span = zoneRange.max - zoneRange.min;
    const values = new Map<number, number>();
    for (const { zoneId, value } of zoneValues) {
      values.set(zoneId, span > 0 ? (value - zoneRange.min) / span : 0.5);
    }
    return { values };
  }, [zoneValues, zoneRange]);

  const rampGradient = useMemo(
    () => `linear-gradient(90deg, ${getZoneFillRampStops().join(", ")})`,
    []
  );

  const unit = t(isHoursKey(indicator) ? "wellbeing.hours" : "wellbeing.points");
  const format = (value: number | null) => (value == null ? "—" : value.toFixed(1));

  return (
    <div className="wellbeing-side">
      <div className="wellbeing-seg" role="group" aria-label={t("wellbeing.indicatorSwitch")}>
        {keys.map((key) => {
          const active = key === indicator;
          return (
            <button
              key={key}
              type="button"
              className={["wellbeing-seg__btn", active && "wellbeing-seg__btn--active"]
                .filter(Boolean)
                .join(" ")}
              style={
                active
                  ? { background: SERIES_COLORS[key], borderColor: SERIES_COLORS[key] }
                  : undefined
              }
              onClick={() => onSelectIndicator(key)}
            >
              {t(`wellbeing.${key}`)}
            </button>
          );
        })}
      </div>

      <section className="wellbeing-card wellbeing-detail">
        <h3 className="wellbeing-card__title">{t(`wellbeing.${indicator}`)}</h3>
        <p className="wellbeing-card__note">{t(`wellbeing.desc_${indicator}`)}</p>
        <div className="wellbeing-detail__value" style={{ color: SERIES_COLORS[indicator] }}>
          {format(toDisplayValue(indicator, current[indicator]))}
          <span className="wellbeing-detail__unit">{unit}</span>
        </div>
        <div className="wellbeing-detail__range">
          <span>
            {t("wellbeing.zoneMax")}
            <b>{format(zoneRange && toDisplayValue(indicator, zoneRange.max))}</b>
          </span>
          <span>
            {t("wellbeing.zoneMin")}
            <b>{format(zoneRange && toDisplayValue(indicator, zoneRange.min))}</b>
          </span>
        </div>
      </section>

      <section className="wellbeing-card wellbeing-card--map">
        <div className="wellbeing-map">
          <CityMapCanvas apiBaseUrl={apiBaseUrl} layers={MAP_LAYERS} zoneFill={zoneFill} />
          {zoneRange ? (
            <div className="wellbeing-map__legend">
              <span>{format(toDisplayValue(indicator, zoneRange.min))}</span>
              <span className="wellbeing-map__legend-bar" style={{ background: rampGradient }} />
              <span>{format(toDisplayValue(indicator, zoneRange.max))}</span>
            </div>
          ) : (
            // Do not label fetch failures as "No data", since that would obscure diagnosis.
            <div className="wellbeing-map__empty">
              {error ? t("common.fetchError") : t("wellbeing.zoneMapEmpty")}
            </div>
          )}
        </div>
      </section>
    </div>
  );
}

export function WellbeingTab({ apiBaseUrl, view }: { apiBaseUrl: string; view: View }) {
  const { t, i18n } = useTranslation();
  const remPx = useRemPx();
  const [selectedKey, setSelectedKey] = useState("city");
  const { data, loading, error } = useEpochCachedStats<WellbeingStats>(
    apiBaseUrl,
    `/api/v1/stats/wellbeing${selectedKey === "city" ? "" : `?zone_id=${selectedKey}`}`
  );

  const keys = VIEW_KEYS[view];
  const composition = VIEW_COMPOSITION[view];
  // Stacked areas treat missing values as 0 (Recharts stack coerces null to 0), creating valleys
  // that suggest comfort fell to 0. All series in composition-chart views become missing together
  // based on households, so omit Epochs without evaluated Households from both charts to align their axes.
  // Retain them in leisure view because scheduled working time is recorded even with zero Households.
  const history = useMemo(() => {
    const points = data?.history ?? [];
    return composition ? points.filter((p) => Number(p.households) > 0) : points;
  }, [data, composition]);
  // Lines overlay the overall metric on the view's metrics; in views with metric switching, they are also the choices.
  const total = VIEW_TOTAL[view];
  const chartKeys = useMemo(() => [...keys, total], [keys, total]);
  const [selectedIndicator, setIndicator] = useState<string>("satis_wealth");
  // Switching views changes the series set; fall back to the first if the selection is absent from the current view.
  const indicator = chartKeys.includes(selectedIndicator) ? selectedIndicator : chartKeys[0];

  // Use dates for horizontal-axis labels and convert series values into display units.
  const chartData = useMemo(() => {
    const locale = resolveIntlLocale(i18n.language);
    return history.map((p) => ({
      label: formatEpochDate(p.date, locale),
      ...Object.fromEntries(chartKeys.map((k) => [k, toDisplayValue(k, p[k])])),
    }));
  }, [history, chartKeys, i18n.language]);

  // Contributions for the composition chart (100-point scale). Apply the API weights only to satisfaction's four axes
  // (do not hardcode equal 25% weights in the UI). For Living environment, use recorded contributions directly.
  const weights = data?.weights;
  const compositionData = useMemo(() => {
    if (!composition) return [];
    const locale = resolveIntlLocale(i18n.language);
    return history.map((p) => {
      const row: Record<string, string | number | null> = {
        label: formatEpochDate(p.date, locale),
      };
      for (const part of composition) {
        const value = p[part.source];
        const weight = part.weightAxis ? weights?.[part.weightAxis] ?? 0 : 1;
        row[part.id] = value == null ? null : value * weight * 100;
      }
      return row;
    });
  }, [history, composition, weights, i18n.language]);

  // Replace the view with a loading screen only on first load. Keep previous data when switching scope
  // (replacing the entire screen unmounts the map, refetches terrain/vectors, and resets the view every time;
  //  the map content does not depend on aggregation scope).
  if (loading && !data) {
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
    {/* Keep displaying the previous scope's values while switching, indicating that the display has not yet caught up. */}
    {loading && <span className="wellbeing-scope__pending">{t("common.loading")}</span>}
  </div>;
  if (!current) {
    return <div className="wellbeing-tab">{scopeSelector}<div className="wellbeing-tab__empty">{t("wellbeing.empty")}</div></div>;
  }

  const unitOf = (key: string) => t(isHoursKey(key) ? "wellbeing.hours" : "wellbeing.points");
  const showDots = chartData.length === 1;
  const withIndicator = VIEW_HAS_INDICATOR[view];

  /** Line width and intensity. In satisfaction view, emphasize only the selected metric. */
  const lineStyle = (key: string) => {
    if (!withIndicator) {
      return { strokeWidth: key === total ? 3 : 2, strokeOpacity: 1 };
    }
    return key === indicator
      ? { strokeWidth: 3, strokeOpacity: 1 }
      : { strokeWidth: 1.5, strokeOpacity: DIMMED_OPACITY };
  };

  /**
   * Stacked-area intensity. Make the selected element darker, but keep the difference modest
   * so transparency does not obscure the stack. Use equal intensity for all elements when
   * the selected metric is not a component of the stack.
   */
  const areaOpacity = (series: string) => {
    if (!composition?.some((part) => part.series === indicator)) return AREA_OPACITY_NEUTRAL;
    return series === indicator ? AREA_OPACITY_ACTIVE : AREA_OPACITY_INACTIVE;
  };

  return (
    <div
      className="wellbeing-tab"
      style={{ "--wellbeing-accent": VIEW_ACCENT[view] } as CSSProperties}
    >
      {scopeSelector}

      {/* Satisfaction descriptions are shown per metric in the right-column card, so omit them here. */}
      {!withIndicator && (
        <p className="wellbeing-tab__description">{t(`wellbeing.${view}Description`)}</p>
      )}

      {/* ===== Current-value KPI cards (handled by the right-column details card in satisfaction view) ===== */}
      {!withIndicator && (
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
            label={t(`wellbeing.${total}`)}
            value={toDisplayValue(total, current[total])}
            unit={unitOf(total)}
            color={SERIES_COLORS[total]}
            emphasized
          />
        </div>
      )}

      {/* ===== History chart (+ composition chart, metric switching, details, and District map in satisfaction view) ===== */}
      <div
        className={`wellbeing-tab__body${withIndicator ? " wellbeing-tab__body--split" : ""}`}
      >
        <div className="wellbeing-charts">
          <section className="wellbeing-card wellbeing-card--chart">
            <h3 className="wellbeing-card__title">{t("wellbeing.history")}</h3>
            <div className="wellbeing-chart">
              <ResponsiveContainer width="100%" height="100%">
                <LineChart data={chartData} margin={CHART_MARGIN}>
                  <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" />
                  <XAxis dataKey="label" tick={AXIS_TICK} tickLine={false} minTickGap={40} />
                  <YAxis
                    yAxisId="score"
                    width={remPx(Y_AXIS_WIDTH_REM)}
                    domain={[0, 100]}
                    tick={AXIS_TICK}
                    tickLine={false}
                  />
                  {view === "leisure" && (
                    <YAxis
                      yAxisId="hours"
                      orientation="right"
                      width={remPx(Y_AXIS_WIDTH_REM)}
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
                  <Legend wrapperStyle={{ fontSize: "0.93rem" }} iconSize={8} />
                  {chartKeys.map((key) => (
                    <Line
                      key={key}
                      yAxisId={isHoursKey(key) ? "hours" : "score"}
                      dataKey={key}
                      name={t(`wellbeing.${key}`)}
                      stroke={SERIES_COLORS[key]}
                      dot={showDots}
                      isAnimationActive={false}
                      {...lineStyle(key)}
                    />
                  ))}
                </LineChart>
              </ResponsiveContainer>
            </div>
          </section>

          {/* Bottom: stacked component contributions. Stack height equals the view's overall metric. */}
          {composition && (
            <section className="wellbeing-card wellbeing-card--chart">
              <h3 className="wellbeing-card__title">{t(`wellbeing.${view}Composition`)}</h3>
              <p className="wellbeing-card__note">{t(`wellbeing.${view}CompositionDescription`)}</p>
              <div className="wellbeing-chart">
                <ResponsiveContainer width="100%" height="100%">
                  <AreaChart data={compositionData} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" />
                    <XAxis dataKey="label" tick={AXIS_TICK} tickLine={false} minTickGap={40} />
                    <YAxis
                      width={remPx(Y_AXIS_WIDTH_REM)}
                      domain={[0, 100]}
                      tick={AXIS_TICK}
                      tickLine={false}
                    />
                    <Tooltip
                      contentStyle={TOOLTIP_STYLE}
                      labelStyle={{ color: "#fff" }}
                      formatter={(value: any, name: any) => [
                        `${Number(value).toFixed(1)} ${t("wellbeing.points")}`,
                        name,
                      ]}
                    />
                    {composition.map((part) => (
                      <Area
                        key={part.id}
                        dataKey={part.id}
                        name={t(`wellbeing.${part.labelKey ?? part.series}`)}
                        stackId="contribution"
                        type="monotone"
                        stroke={SERIES_COLORS[part.series]}
                        fill={SERIES_COLORS[part.series]}
                        fillOpacity={areaOpacity(part.series)}
                        dot={showDots}
                        isAnimationActive={false}
                      />
                    ))}
                  </AreaChart>
                </ResponsiveContainer>
              </div>
            </section>
          )}
        </div>

        {withIndicator && (
          <WellbeingSidePanel
            apiBaseUrl={apiBaseUrl}
            keys={chartKeys}
            indicator={indicator}
            onSelectIndicator={setIndicator}
            current={current}
          />
        )}
      </div>
    </div>
  );
}
