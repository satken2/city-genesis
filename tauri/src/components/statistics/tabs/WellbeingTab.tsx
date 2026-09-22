/**
 * 満足度・余暇・住環境タブコンポーネント
 *
 * 統計ウィンドウの「満足度」「余暇」「住環境」の3タブが共有する中身。
 * GET /api/v1/stats/wellbeing から確定済みの都市全体または選択地区の平均(直近HISTORY_COUNTエポック（現在120）)を取得し、
 * `view` に応じて表示する指標の組を切り替える。
 * データはエポックが変わらない限り再取得しない(useEpochCachedStats によるキャッシュ)。
 *
 * レイアウト(ウィンドウの高さに収める):
 * - 「満足度」表示: 上部に集計範囲、下段を等幅2カラムにして
 *   左=[推移グラフ(選択中の指標を強調し、他の系列は細く薄く描く)]
 *     [構成グラフ(四軸の貢献点の積み上げ面。積み上げた高さが世帯満足度)]の上下2枚、
 *   右=[指標切替ボタン][選択指標の説明と現在値・地区間レンジ][地区別の色分け地図]。
 * - 「余暇」「住環境」表示: 説明文 → 現在値のKPIカード列 → 推移グラフの縦積み。
 *
 * 満足度系の値はバックエンドの 0〜1 を100点換算で表示し、
 * 時間(可処分時間・所定労働時間)はそのまま 時間/日 で表示する。
 * 「余暇」表示では時間の系列だけ右軸に載せる。
 */

import { useEffect, useMemo, useRef, useState, type CSSProperties } from "react";
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
  type CityMapCanvasHandle,
  type CityMapLayerVisibility,
  type CityMapZoneFill,
} from "../../map/CityMapCanvas";
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

// --- バックエンド GET /api/v1/stats/wellbeing/zones のレスポンス型 ---

/** 最新確定エポックの地区別現在値。母集団世帯が0の地区は値が null(欠測) */
type WellbeingZone = {
  zone_id: number;
  name: string | null;
  households: number;
  values: Record<string, number | null>;
};

type WellbeingZoneStats = {
  epoch: number;
  date: number | null;
  zones: WellbeingZone[];
};

const AXES = ["wealth", "leisure", "health", "comfort"] as const;

/** 表示ごとに折れ線で並べる系列。世帯満足度はどの表示でも末尾に重ねる */
const VIEW_KEYS: Record<View, string[]> = {
  satisfaction: AXES.map((axis) => `satis_${axis}`),
  leisure: ["leisure_hours", "work_hours", "satis_leisure"],
  comfort: ["satis_residential", "quiet", "sunlight", "safety", "satis_comfort"],
};

/**
 * 指標切替ボタン・選択指標の詳細・地区別地図の右カラムを出す表示。
 * 「余暇」は可処分時間(時間/日)と満足度(点)が混在して1つの指標として並べられないため外す。
 */
const VIEW_HAS_INDICATOR: Record<View, boolean> = {
  satisfaction: true,
  leisure: false,
  comfort: true,
};

/** 構成グラフ(積み上げ面)の1band */
type CompositionPart = {
  /** グラフ内のdataKey */
  id: string;
  /** 履歴から読む値のキー */
  source: string;
  /** 表示名(i18n)と色(SERIES_COLORS)を引くキー */
  series: string;
  /** 指定時は weights[axis] を掛ける。履歴に貢献点そのものが入っていない指標用 */
  weightAxis?: string;
};

/**
 * 表示ごとの構成グラフ。積み上げた高さがその表示の総合指標に一致する組だけを置く。
 *
 * 満足度は四軸の重み付き和なので、履歴の軸の値に weights を掛けて貢献点にする。
 * 住環境は「4要素の平均 × 住宅充足度」の積で和に分解できないため、
 * バックエンドが乗数適用後の貢献値(comfort_*)を記録しており、UIは掛け算をしない
 * (UI側で式を再現すると、シミュレーション本体との二重実装になる)。
 */
const VIEW_COMPOSITION: Partial<Record<View, CompositionPart[]>> = {
  satisfaction: AXES.map((axis) => ({
    id: axis,
    source: `satis_${axis}`,
    series: `satis_${axis}`,
    weightAxis: axis,
  })),
  comfort: [
    { id: "housing", source: "comfort_housing", series: "satis_residential" },
    { id: "quiet", source: "comfort_quiet", series: "quiet" },
    { id: "sunlight", source: "comfort_sunlight", series: "sunlight" },
    { id: "safety", source: "comfort_safety", series: "safety" },
  ],
};

/** 表示ごとのアクセント色(カード見出しの左罫線) */
const VIEW_ACCENT: Record<View, string> = {
  satisfaction: "#38bdf8",
  leisure: "#f59e0b",
  comfort: "#10b981",
};

// 四軸の色は折れ線と指標切替ボタンで共有する。
const AXIS_COLORS: Record<(typeof AXES)[number], string> = {
  wealth: "#f59e0b",
  leisure: "#38bdf8",
  health: "#10b981",
  comfort: "#a78bfa",
};

/** 系列色。KPIカードの値・折れ線・指標切替ボタンを同じ色にして対応づける */
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

/** 非選択の系列の不透明度。選択中の指標との主従を色の濃さで示す */
const DIMMED_OPACITY = 0.3;

/** 積み上げ面の塗りの濃さ(選択中の軸 / 非選択 / 総合選択時の一律) */
const AREA_OPACITY_ACTIVE = 0.85;
const AREA_OPACITY_INACTIVE = 0.4;
const AREA_OPACITY_NEUTRAL = 0.6;

/**
 * 地区別地図のレイヤー。地区の塗り分けを読ませる画なので、区画・建物の枠線と
 * 標高色分けは落とし、位置の見当がつく海岸線と道路だけ残す。
 * 再レンダリングでレイヤー変更effectを走らせないようモジュール定数として持つ。
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

/**
 * 満足度表示の右カラム(指標切替 + 選択指標の詳細 + 地区別の色分け地図)。
 *
 * 地区別の現在値はこの画だけが使うため、取得もここに閉じる
 * (余暇・住環境表示ではマウントされないので通信も発生しない)。
 * 選択中の指標は推移グラフの強調とも共有するため、stateは親が持つ。
 */
function WellbeingSidePanel({
  apiBaseUrl,
  keys,
  indicator,
  onSelectIndicator,
  current,
  epoch,
}: {
  apiBaseUrl: string;
  keys: string[];
  indicator: string;
  onSelectIndicator: (key: string) => void;
  current: HistoryPoint;
  epoch: number | null;
}) {
  const { t } = useTranslation();
  const { data } = useEpochCachedStats<WellbeingZoneStats>(
    apiBaseUrl,
    "/api/v1/stats/wellbeing/zones"
  );

  const mapRef = useRef<CityMapCanvasHandle | null>(null);
  const mapEpochRef = useRef<number | null>(null);
  useEffect(() => {
    // 統計ウィンドウは閉じても非表示になるだけでアンマウントされないため、
    // エポックが進んだら地区形状を取り直す(初回ロードとの二重取得は避ける)。
    if (epoch == null) return;
    const previous = mapEpochRef.current;
    mapEpochRef.current = epoch;
    if (previous != null && previous !== epoch) mapRef.current?.reload();
  }, [epoch]);

  // 選択指標の地区別現在値。欠測(母集団世帯0)の地区は塗らない。
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

  // 塗りは地区間のmin〜maxを両端まで引き伸ばした相対値。地区差が小さくても違いが読める
  // 代わりにエポック間・指標間の絶対比較ができないため、凡例に実値レンジを併記する。
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
          <CityMapCanvas
            ref={mapRef}
            apiBaseUrl={apiBaseUrl}
            layers={MAP_LAYERS}
            zoneFill={zoneFill}
          />
          {zoneRange ? (
            <div className="wellbeing-map__legend">
              <span>{format(toDisplayValue(indicator, zoneRange.min))}</span>
              <span className="wellbeing-map__legend-bar" style={{ background: rampGradient }} />
              <span>{format(toDisplayValue(indicator, zoneRange.max))}</span>
            </div>
          ) : (
            <div className="wellbeing-map__empty">{t("wellbeing.zoneMapEmpty")}</div>
          )}
        </div>
      </section>
    </div>
  );
}

export function WellbeingTab({ apiBaseUrl, view }: { apiBaseUrl: string; view: View }) {
  const { t, i18n } = useTranslation();
  const [selectedKey, setSelectedKey] = useState("city");
  const { data, loading, error, epoch } = useEpochCachedStats<WellbeingStats>(
    apiBaseUrl,
    `/api/v1/stats/wellbeing${selectedKey === "city" ? "" : `?zone_id=${selectedKey}`}`
  );

  const keys = VIEW_KEYS[view];
  const history = data?.history ?? [];
  // 折れ線は表示指標に世帯満足度を重ねたもの。満足度表示では指標切替の選択肢も兼ねる。
  const chartKeys = useMemo(() => [...keys, "satisfaction"], [keys]);
  const [selectedIndicator, setIndicator] = useState<string>("satis_wealth");
  // 表示を切り替えると系列の組が変わるため、選択が今の表示に無ければ先頭へ倒す
  // (世帯満足度はどの表示にもあるので、総合を選んだままの移動は維持される)。
  const indicator = chartKeys.includes(selectedIndicator) ? selectedIndicator : chartKeys[0];

  // 横軸ラベルは日付にし、系列値は表示単位へ換算しておく。
  const chartData = useMemo(() => {
    const locale = resolveIntlLocale(i18n.language);
    return history.map((p) => ({
      label: formatEpochDate(p.date, locale),
      ...Object.fromEntries(chartKeys.map((k) => [k, toDisplayValue(k, p[k])])),
    }));
  }, [history, chartKeys, i18n.language]);

  // 構成グラフ用の貢献点(100点換算)。満足度の四軸だけAPIのweightsを掛ける
  // (等分の25%をUIに焼き込まない)。住環境は記録済みの貢献値をそのまま使う。
  const composition = VIEW_COMPOSITION[view];
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
  const showDots = chartData.length === 1;
  const withIndicator = VIEW_HAS_INDICATOR[view];

  /** 折れ線の太さ・濃さ。満足度表示では選択中の指標だけを主役にする */
  const lineStyle = (key: string) => {
    if (!withIndicator) {
      return { strokeWidth: key === "satisfaction" ? 3 : 2, strokeOpacity: 1 };
    }
    return key === indicator
      ? { strokeWidth: 3, strokeOpacity: 1 }
      : { strokeWidth: 1.5, strokeOpacity: DIMMED_OPACITY };
  };

  /**
   * 積み上げ面の濃さ。選択中の要素を濃くするが、積み上げが透けて読めなくならないよう
   * 差は控えめにする(選択中の指標が積み上げの構成要素でないときは全要素を同じ濃さにする)。
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

      {/* 満足度表示の説明文は指標ごとに右カラムのカードへ出すため、ここには置かない */}
      {!withIndicator && (
        <p className="wellbeing-tab__description">{t(`wellbeing.${view}Description`)}</p>
      )}

      {/* ===== 現在値のKPIカード列(満足度表示は右カラムの詳細カードが担う) ===== */}
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
            label={t("wellbeing.satisfaction")}
            value={toDisplayValue("satisfaction", current.satisfaction)}
            unit={t("wellbeing.points")}
            color={SERIES_COLORS.satisfaction}
            emphasized
          />
        </div>
      )}

      {/* ===== 推移グラフ(＋満足度表示のみ構成グラフ・指標切替・詳細・地区別地図) ===== */}
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

          {/* 下段: 構成要素の貢献点の積み上げ。積み上げた高さがその表示の総合指標になる */}
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
                      width={Y_AXIS_WIDTH}
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
                        name={t(`wellbeing.${part.series}`)}
                        stackId="contribution"
                        type="monotone"
                        stroke={SERIES_COLORS[part.series]}
                        fill={SERIES_COLORS[part.series]}
                        fillOpacity={areaOpacity(part.series)}
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
            epoch={epoch}
          />
        )}
      </div>
    </div>
  );
}
