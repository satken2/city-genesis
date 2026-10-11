/**
 * 観光統計タブコンポーネント
 *
 * GET /api/v1/stats/tourism から観光客の滞在状況・受入能力・宿泊需給・満足度・消費を取得して可視化する。
 *
 * レイアウト:
 * - 最上段: サマリータイル(滞在客数 / 受入可能人数と充足率 / 到着・帰国 / 応募 / 観光収入 / 平均満足度)
 * - 左右分割
 *   - 左カラム: 推移グラフ3枚(滞在客数 vs 受入可能人数、到着・帰国・応募、観光収入)
 *   - 右カラム: ボトルネック表(観光地 vs 玄関口)、宿泊需給カード、満足度カード(平均+分布)、観光消費リスト
 *
 * 人流の内訳(訪問先)と地区別集計は持たない(バックエンドが集計していない)。
 */

import { useMemo } from "react";
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
import "./TourismTab.css";
import { useRemPx } from "../../../hooks/useRemPx";

// --- バックエンド GET /api/v1/stats/tourism のレスポンス型 ---

type HistoryPoint = {
  epoch: number;
  tourist_count: number;
  tourist_capacity: number;
  arrivals: number;
  departures: number;
  applicants: number;
  tourism_revenue: number;
};

type FacilityCount = {
  variant: number;
  name: string;
  total: number;
  active: number;
};

type BottleneckRow = {
  kind: "attraction" | "gateway";
  product_id: number;
  product_name: string;
  facilities: FacilityCount[];
  capacity_per_day: number;
  actual_per_day: number;
  utilization: number;
  capacity_persons: number | null;
  is_constraint: boolean;
};

type HotelCount = {
  variant: number;
  name: string;
  active: number;
  paused: number;
  constructing: number;
};

type DistributionPoint = {
  bin: string;
  entertainment: number;
  tourism: number;
  qol: number;
};

type TourismStats = {
  epoch: number;
  party_size: number;
  summary: {
    tourist_count: number;
    tourist_household_count: number;
    tourist_capacity: number;
    occupancy_ratio: number;
    arrivals: number;
    departures: number;
    applicants: number;
    tourism_revenue: number;
  };
  history: HistoryPoint[];
  bottleneck: BottleneckRow[];
  lodging: {
    capacity: number;
    occupied: number;
    vacancy: number;
    vacancy_rate: number;
    waiting_parties: number;
    waiting_days_mean: number;
    waiting_days_max: number;
    avg_rent_per_m2: number;
    lodging_cost_per_party_day: number;
    tourist_wtp_per_party_day: number;
    hotels: HotelCount[];
  };
  satisfaction: {
    staying_parties: number;
    avg_entertainment: number;
    avg_tourism: number;
    avg_env: number;
    avg_qol: number;
    departure_candidate_ratio: number;
    departure_threshold: number;
    distribution: DistributionPoint[];
  };
  consumption: {
    product_id: number;
    product_name: string;
    unit: string;
    amount: number;
    value: number;
  }[];
};

// 系列色。観光客=桃(宿泊フロア色と揃える)、受入能力=シアン、到着=緑、帰国=赤、応募=琥珀、収入=紫
const COLOR_COUNT = "#ec4899";
const COLOR_CAPACITY = "#06b6d4";
const COLOR_ARRIVALS = "#10b981";
const COLOR_DEPARTURES = "#ef4444";
const COLOR_APPLICANTS = "#f59e0b";
const COLOR_REVENUE = "#a78bfa";
const COLOR_ENTERTAINMENT = "#f59e0b";
const COLOR_TOURISM = "#ec4899";
const COLOR_QOL = "#60a5fa";

const CHART_MARGIN = { top: 12, right: 16, left: -10, bottom: 0 };
const Y_AXIS_WIDTH_REM = 4.44; // Y軸の幅(rem)。描画時に useRemPx で px に直す
const AXIS_TICK = { fontSize: "0.93rem", fill: "#8ba3bb" };
const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "1rem",
};

function fmt(value: number, digits = 0): string {
  return value.toLocaleString(undefined, { maximumFractionDigits: digits });
}

function pct(ratio: number): string {
  return `${(ratio * 100).toFixed(1)}%`;
}

export function TourismTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const remPx = useRemPx();
  const { data, loading, error } = useEpochCachedStats<TourismStats>(
    apiBaseUrl,
    "/api/v1/stats/tourism"
  );

  const history = data?.history ?? [];
  const consumption = data?.consumption ?? [];
  const maxConsumptionValue = useMemo(
    () => consumption.reduce((max, c) => Math.max(max, c.value), 0),
    [consumption]
  );

  if (loading) {
    return <div className="tourism-tab__loading">{t("common.loading")}</div>;
  }
  if (error || !data) {
    return (
      <div className="tourism-tab__error">
        {t("common.fetchError")}{error ? `: ${error}` : ""}
      </div>
    );
  }

  const { summary, lodging, satisfaction } = data;
  const hasHistory = history.length > 0;
  const netFlow = summary.arrivals - summary.departures;
  const hasStaying = satisfaction.staying_parties > 0;

  const tiles: { key: string; label: string; value: string; sub?: string }[] = [
    {
      key: "tourists",
      label: t("statistics.tourism.tileTourists"),
      value: `${fmt(summary.tourist_count)} ${t("statistics.people")}`,
      sub: `${fmt(summary.tourist_household_count)} ${t("statistics.tourism.parties")}`,
    },
    {
      key: "capacity",
      label: t("statistics.tourism.tileCapacity"),
      value: `${fmt(summary.tourist_capacity)} ${t("statistics.people")}`,
      sub: `${t("statistics.tourism.tileOccupancy")} ${pct(summary.occupancy_ratio)}`,
    },
    {
      key: "flow",
      label: t("statistics.tourism.tileFlow"),
      value: `${netFlow >= 0 ? "+" : ""}${fmt(netFlow)}`,
      sub: `${t("statistics.tourism.seriesArrivals")} ${fmt(summary.arrivals)} / ${t("statistics.tourism.seriesDepartures")} ${fmt(summary.departures)}`,
    },
    {
      key: "applicants",
      label: t("statistics.tourism.tileApplicants"),
      value: `${fmt(summary.applicants)} ${t("statistics.tourism.parties")}`,
    },
    {
      key: "revenue",
      label: t("statistics.tourism.tileRevenue"),
      value: t("common.currency", { value: Math.round(summary.tourism_revenue) }),
    },
    {
      key: "qol",
      label: t("statistics.tourism.tileAvgQol"),
      value: hasStaying ? pct(satisfaction.avg_qol) : "-",
      sub: hasStaying
        ? `${t("statistics.tourism.departureCandidates")} ${pct(satisfaction.departure_candidate_ratio)}`
        : undefined,
    },
  ];

  const renderHistoryChart = (children: React.ReactNode) => (
    <div className="tourism-card__chart-container">
      {hasHistory ? (
        <ResponsiveContainer width="100%" height="100%">
          <LineChart data={history} margin={CHART_MARGIN}>
            <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
            <XAxis dataKey="epoch" tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} />
            <YAxis
              width={remPx(Y_AXIS_WIDTH_REM)}
              tick={AXIS_TICK}
              tickLine={false}
              axisLine={{ stroke: "#253246" }}
              allowDecimals={false}
            />
            <Tooltip contentStyle={TOOLTIP_STYLE} labelFormatter={(label) => `Epoch ${label}`} />
            <Legend wrapperStyle={{ fontSize: "0.93rem" }} iconSize={8} />
            {children}
          </LineChart>
        </ResponsiveContainer>
      ) : (
        <div className="tourism-card__no-data">{t("statistics.tourism.noData")}</div>
      )}
    </div>
  );

  return (
    <div className="tourism-tab">
      <h2 className="tourism-tab__title">{t("statistics.tourism.title")}</h2>

      {/* サマリータイル */}
      <div className="tourism-tiles">
        {tiles.map((tile) => (
          <div key={tile.key} className="tourism-tile">
            <span className="tourism-tile__label">{tile.label}</span>
            <span className="tourism-tile__value">{tile.value}</span>
            {tile.sub && <span className="tourism-tile__sub">{tile.sub}</span>}
          </div>
        ))}
      </div>

      <div className="tourism-tab__grid">
        {/* 左カラム: 推移グラフ */}
        <div className="tourism-tab__col">
          <div className="tourism-card">
            <h3 className="tourism-card__title">{t("statistics.tourism.chartCountHistory")}</h3>
            {renderHistoryChart(
              <>
                <Line type="monotone" dataKey="tourist_count" name={t("statistics.tourism.seriesCount")} stroke={COLOR_COUNT} strokeWidth={2} dot={{ r: 2 }} />
                <Line type="monotone" dataKey="tourist_capacity" name={t("statistics.tourism.seriesCapacity")} stroke={COLOR_CAPACITY} strokeWidth={2} strokeDasharray="5 3" dot={false} />
              </>
            )}
          </div>

          <div className="tourism-card">
            <h3 className="tourism-card__title">{t("statistics.tourism.chartFlowHistory")}</h3>
            {renderHistoryChart(
              <>
                <Line type="monotone" dataKey="arrivals" name={t("statistics.tourism.seriesArrivals")} stroke={COLOR_ARRIVALS} strokeWidth={2} dot={{ r: 2 }} />
                <Line type="monotone" dataKey="departures" name={t("statistics.tourism.seriesDepartures")} stroke={COLOR_DEPARTURES} strokeWidth={2} dot={{ r: 2 }} />
                <Line type="monotone" dataKey="applicants" name={t("statistics.tourism.seriesApplicants")} stroke={COLOR_APPLICANTS} strokeWidth={1.5} strokeDasharray="4 2" dot={false} />
              </>
            )}
          </div>

          <div className="tourism-card">
            <h3 className="tourism-card__title">{t("statistics.tourism.chartRevenueHistory")}</h3>
            {renderHistoryChart(
              <Line type="monotone" dataKey="tourism_revenue" name={t("statistics.tourism.seriesRevenue")} stroke={COLOR_REVENUE} strokeWidth={2} dot={{ r: 2 }} />
            )}
          </div>
        </div>

        {/* 右カラム: ボトルネック・宿泊・満足度・消費 */}
        <div className="tourism-tab__col">
          {/* ボトルネック表 */}
          <div className="tourism-card">
            <h3 className="tourism-card__title">{t("statistics.tourism.bottleneckTitle")}</h3>
            <table className="tourism-table">
              <thead>
                <tr>
                  <th>{t("statistics.tourism.colKind")}</th>
                  <th>{t("statistics.tourism.colFacilities")}</th>
                  <th className="text-right">{t("statistics.tourism.colCapacityPerDay")}</th>
                  <th className="text-right">{t("statistics.tourism.colActualPerDay")}</th>
                  <th className="text-right">{t("statistics.tourism.colUtilization")}</th>
                  <th className="text-right">{t("statistics.tourism.colCapacityPersons")}</th>
                </tr>
              </thead>
              <tbody>
                {data.bottleneck.map((row) => (
                  <tr key={row.kind}>
                    <td className="font-semibold">
                      {t(row.kind === "attraction" ? "statistics.tourism.kindAttraction" : "statistics.tourism.kindGateway")}
                      <span className="tourism-table__product">{row.product_name}</span>
                      {row.is_constraint && (
                        <span className="tourism-badge tourism-badge--constraint">
                          {t("statistics.tourism.constraintBadge")}
                        </span>
                      )}
                    </td>
                    <td>
                      {row.facilities.length === 0 ? (
                        <span className="tourism-table__muted">-</span>
                      ) : (
                        row.facilities.map((f) => (
                          <div key={f.variant} className="tourism-table__facility">
                            {f.name}: {fmt(f.active)}/{fmt(f.total)}
                          </div>
                        ))
                      )}
                    </td>
                    <td className="text-right">{fmt(row.capacity_per_day, 1)}</td>
                    <td className="text-right">{fmt(row.actual_per_day, 1)}</td>
                    <td className="text-right">{pct(row.utilization)}</td>
                    <td className="text-right">
                      {row.capacity_persons == null ? "-" : `${fmt(row.capacity_persons)} ${t("statistics.people")}`}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {/* 宿泊需給 */}
          <div className="tourism-card">
            <h3 className="tourism-card__title">{t("statistics.tourism.lodgingTitle")}</h3>
            <div className="tourism-kv-grid">
              <span className="tourism-kv__label">{t("statistics.tourism.lodgingCapacity")}</span>
              <span className="tourism-kv__value">
                {fmt(lodging.occupied, 1)} / {fmt(lodging.capacity, 1)} ㎡
                <span className="tourism-kv__note">
                  {t("statistics.tourism.lodgingVacancyRate")} {pct(lodging.vacancy_rate)}
                </span>
              </span>
              <span className="tourism-kv__label">{t("statistics.tourism.waitingParties")}</span>
              <span className="tourism-kv__value">
                {fmt(lodging.waiting_parties)} {t("statistics.tourism.parties")}
                {lodging.waiting_parties > 0 && (
                  <span className="tourism-kv__note">
                    {t("statistics.tourism.waitingDays", {
                      mean: fmt(lodging.waiting_days_mean),
                      max: fmt(lodging.waiting_days_max),
                    })}
                  </span>
                )}
              </span>
              <span className="tourism-kv__label">{t("statistics.tourism.lodgingCost")}</span>
              <span className="tourism-kv__value">
                {t("common.currency", { value: lodging.lodging_cost_per_party_day })}
                <span className="tourism-kv__note">
                  {t("statistics.tourism.touristWtp")}{" "}
                  {t("common.currency", { value: lodging.tourist_wtp_per_party_day })}
                </span>
              </span>
            </div>
            <div className="tourism-occupancy-bar" title={pct(1 - lodging.vacancy_rate)}>
              <div
                className="tourism-occupancy-bar__fill"
                style={{ width: `${Math.min(100, lodging.capacity > 0 ? (lodging.occupied / lodging.capacity) * 100 : 0)}%` }}
              />
            </div>
            <table className="tourism-table tourism-table--compact">
              <thead>
                <tr>
                  <th>{t("statistics.tourism.hotelsTitle")}</th>
                  <th className="text-right">{t("statistics.tourism.hotelActive")}</th>
                  <th className="text-right">{t("statistics.tourism.hotelPaused")}</th>
                  <th className="text-right">{t("statistics.tourism.hotelConstructing")}</th>
                </tr>
              </thead>
              <tbody>
                {lodging.hotels.map((h) => (
                  <tr key={h.variant}>
                    <td className="font-semibold">{h.name}</td>
                    <td className="text-right">{fmt(h.active)}</td>
                    <td className="text-right">{fmt(h.paused)}</td>
                    <td className="text-right">{fmt(h.constructing)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {/* 満足度 */}
          <div className="tourism-card">
            <h3 className="tourism-card__title">{t("statistics.tourism.satisfactionTitle")}</h3>
            {hasStaying ? (
              <>
                <div className="tourism-meters">
                  {[
                    { key: "ent", label: t("statistics.tourism.avgEntertainment"), value: satisfaction.avg_entertainment, color: COLOR_ENTERTAINMENT },
                    { key: "tour", label: t("statistics.tourism.avgTourism"), value: satisfaction.avg_tourism, color: COLOR_TOURISM },
                    { key: "env", label: t("statistics.tourism.avgEnv"), value: Math.min(satisfaction.avg_env, 1), color: COLOR_CAPACITY, raw: satisfaction.avg_env },
                    { key: "qol", label: t("statistics.tourism.avgQol"), value: satisfaction.avg_qol, color: COLOR_QOL },
                  ].map((m) => (
                    <div key={m.key} className="tourism-meter">
                      <span className="tourism-meter__label">{m.label}</span>
                      <div className="tourism-meter__track">
                        <div className="tourism-meter__fill" style={{ width: `${m.value * 100}%`, background: m.color }} />
                      </div>
                      <span className="tourism-meter__value">{(m.raw ?? m.value).toFixed(2)}</span>
                    </div>
                  ))}
                </div>
                <div className="tourism-card__chart-container tourism-card__chart-container--short">
                  <ResponsiveContainer width="100%" height="100%">
                    <BarChart data={satisfaction.distribution} margin={{ top: 4, right: 8, left: -20, bottom: 0 }} barCategoryGap={2}>
                      <CartesianGrid strokeDasharray="3 3" stroke="#253246" vertical={false} />
                      <XAxis dataKey="bin" tick={{ fontSize: "0.74rem", fill: "#8ba3bb" }} tickLine={false} interval={0} />
                      <YAxis width={remPx(5.56)} tick={AXIS_TICK} tickLine={false} allowDecimals={false} />
                      <Tooltip contentStyle={TOOLTIP_STYLE} />
                      <Legend wrapperStyle={{ fontSize: "0.93rem" }} iconSize={8} />
                      <Bar dataKey="entertainment" name={t("statistics.tourism.avgEntertainment")} fill={COLOR_ENTERTAINMENT} />
                      <Bar dataKey="tourism" name={t("statistics.tourism.avgTourism")} fill={COLOR_TOURISM} />
                      <Bar dataKey="qol" name={t("statistics.tourism.avgQol")} fill={COLOR_QOL} />
                    </BarChart>
                  </ResponsiveContainer>
                </div>
              </>
            ) : (
              <div className="tourism-card__empty">{t("statistics.tourism.noTourists")}</div>
            )}
          </div>

          {/* 観光消費 */}
          <div className="tourism-card">
            <h3 className="tourism-card__title">{t("statistics.tourism.consumptionTitle")}</h3>
            <div className="tourism-card__list-container">
              {consumption.length > 0 ? (
                consumption.map((item) => {
                  const width = maxConsumptionValue > 0 ? (item.value / maxConsumptionValue) * 100 : 0;
                  return (
                    <div key={item.product_id} className="tourism-bar-item">
                      <div className="tourism-bar-item__header">
                        <span className="tourism-bar-item__name">{item.product_name}</span>
                        <span className="tourism-bar-item__values">
                          <span className="tourism-bar-item__value">
                            {t("common.currency", { value: item.value })}
                          </span>
                          <span className="tourism-bar-item__amount">
                            {fmt(item.amount, 1)} {item.unit}
                          </span>
                        </span>
                      </div>
                      <div className="tourism-bar-track">
                        <div className="tourism-bar-fill" style={{ width: `${width}%` }} />
                      </div>
                    </div>
                  );
                })
              ) : (
                <div className="tourism-card__empty">{t("statistics.tourism.noTourists")}</div>
              )}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
