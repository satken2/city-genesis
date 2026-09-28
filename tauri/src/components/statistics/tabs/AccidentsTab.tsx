/**
 * 事件・事故タブコンポーネント
 *
 * 統計ウィンドウの「事件・事故」タブの中身。GET /api/v1/stats/accidents から
 * 過去の事故・災害履歴と、直近エポックのイベント一覧、関係施設一覧を取得して表示する。
 *
 * レイアウト:
 * - 上3分の1:
 *     - 左半分: サマリ。直近エポックの火災・犯罪・交通事故・倒壊の件数。
 *     - 右半分: 事故死者数の推移（既存の積み上げ棒グラフ）。
 * - 下3分の2: タブ分割（「火災」「交通事故」「犯罪」「倒壊事故」）
 *     - 火災/犯罪: 左半分は縦2分割（推移グラフ・発生一覧）、右半分は消防署/警察署一覧。
 *     - 交通事故/倒壊事故: 左半分は縦2分割（推移グラフ・発生一覧）、右半分は空白。
 */

import { useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import {
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
import { useMasterData } from "../../../contexts/MasterDataContext";
import { PathLink } from "../../common/PathLink";
import "./AccidentsTab.css";
import { useRemPx } from "../../../hooks/useRemPx";

// --- バックエンド GET /api/v1/stats/accidents のレスポンス型 ---

type AccidentHistoryPoint = {
  epoch: number;
  traffic_deaths: number;
  collapse_deaths: number;
  fire_count: number;
  misfire_count: number;
  traffic_accident_count: number;
  crime_count: number;
  crime_sign_count: number;
  collapse_count: number;
};

type TrafficAccidentItem = {
  id: number;
  name: string;
  center_point: number[] | null;
  path_variant: number;
};

type FireItem = {
  id: number;
  name: string;
  center_point: number[] | null;
  variant: number;
};

type CrimeItem = {
  id: number;
  name: string;
  center_point: number[] | null;
  variant: number;
};

type CollapseItem = {
  id: number;
  name: string;
  center_point: number[] | null;
  variant: number;
  zone_id: number;
  death_count: number;
  is_path: boolean;
  // 倒壊原因("fire"=焼失 / "aging"=老朽倒壊)
  cause?: string;
};

type StationItem = {
  id: number;
  name: string;
  zone_id: number;
  zone_name: string;
  supply_production: number;
  supply_sold_amount: number;
  usage_ratio: number;
};

type AccidentsStats = {
  epoch: number;
  history: AccidentHistoryPoint[];
  latest: {
    traffic_accidents: TrafficAccidentItem[];
    fires: FireItem[];
    collapses: CollapseItem[];
    crimes: CrimeItem[];
  };
  fire_stations: StationItem[];
  police_stations: StationItem[];
};

// --- 配色 (警告・災害を連想させる暖色系＋警察の寒色系) ---
const COLOR_TRAFFIC = "#f59e0b"; // 交通事故: オレンジ
const COLOR_COLLAPSE = "#ef4444"; // 倒壊: レッド
const COLOR_FIRE = "#fb7185"; // 火災: ローズ
const COLOR_CRIME = "#06b6d4"; // 犯罪: シアン
const COLOR_CRIME_SIGN = "rgba(6, 182, 212, 0.35)"; // 犯罪の兆候

const CHART_MARGIN = { top: 10, right: 15, left: 5, bottom: 5 };
const Y_AXIS_WIDTH_REM = 2.78; // Y軸の幅(rem)。描画時に useRemPx で px に直す
const AXIS_TICK = { fontSize: "0.83rem", fill: "#8ba3bb" };
const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "1.02rem",
};

/**
 * 業務逼迫度を表示するメーターコンポーネント
 */
// goodHigh: 値が高いほど「良い」指標(例: 車両調達率)のとき true。既定(false)は逼迫度
// (混雑率など)で、高いほど赤。goodHigh=true では配色を反転し、100%=緑・0%=赤 にする。
export function UsageMeter({ usage_ratio, goodHigh = false }: { usage_ratio: number; goodHigh?: boolean }) {
  const pct = Math.min(Math.max(usage_ratio * 100, 0), 100);

  // 逼迫度の指標では pct そのものを、良い指標では不足度(100-pct)を「悪さ」として配色する。
  const badness = goodHigh ? 100 - pct : pct;
  let fillColor = "#10b981"; // 低逼迫 / 十分: 緑
  if (badness > 80) {
    fillColor = "#ef4444"; // 高逼迫 / 大幅不足: 赤
  } else if (badness > 50) {
    fillColor = "#f59e0b"; // 中逼迫 / やや不足: オレンジ
  }

  return (
    <div className="usage-meter">
      <div className="usage-meter__track">
        <div
          className="usage-meter__fill"
          style={{ width: `${pct}%`, backgroundColor: fillColor }}
        />
      </div>
      <span className="usage-meter__label" style={{ color: fillColor }}>
        {pct.toFixed(0)}%
      </span>
    </div>
  );
}

export function AccidentsTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const remPx = useRemPx();
  const masterData = useMasterData();
  const { data, loading, error } = useEpochCachedStats<AccidentsStats>(
    apiBaseUrl,
    "/api/v1/stats/accidents"
  );

  const [activeSubTab, setActiveSubTab] = useState<"fire" | "traffic" | "crime" | "collapse">("fire");

  // 建物(agent)詳細ウィンドウを開く
  const openAgentDetail = (agentId: number) => {
    void invoke("open_registered_window", {
      windowType: "agent_detail",
      entityId: String(agentId),
    });
  };

  if (loading) {
    return <div className="accidents-tab__loading">{t("common.loading")}</div>;
  }
  if (error || !data) {
    return (
      <div className="accidents-tab__error">
        {t("common.fetchError")}
        {error ? `: ${error}` : ""}
      </div>
    );
  }

  const history = data.history;
  const hasHistory = history.length > 0;
  
  const processedHistory = history.map((item) => ({
    ...item,
    misfire_only: Math.max(0, item.misfire_count - item.fire_count),
  }));

  const latest = data.latest;

  const pathVariantName = (variant: number): string =>
    masterData?.paths[variant]?.display_name ?? "";
  const agentVariantName = (variant: number): string =>
    masterData?.agents[variant]?.display_name ?? "";

  const renderAxesAndGrid = () => (
    <>
      <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" />
      <XAxis dataKey="epoch" tick={AXIS_TICK} tickLine={false} />
      <YAxis
        width={remPx(Y_AXIS_WIDTH_REM)}
        tick={AXIS_TICK}
        tickLine={false}
        allowDecimals={false}
      />
      <Tooltip
        contentStyle={TOOLTIP_STYLE}
        labelStyle={{ color: "#fff" }}
        labelFormatter={(label) => `${t("statistics.epochLabel")} ${label}`}
      />
    </>
  );

  const renderStationTable = (stations: StationItem[], title: string) => (
    <section className="accidents-list" style={{ flex: 1 }}>
      <h3 className="accidents-list__title">
        {title}
        <span className="accidents-list__count">({stations.length})</span>
      </h3>
      <div className="accidents-list__body">
        {stations.length > 0 ? (
          <table className="accidents-table">
            <thead>
              <tr>
                <th>{t("statistics.columnAgent")}</th>
                <th>{t("statistics.columnDistrict")}</th>
                <th>{t("statistics.columnUsage")}</th>
              </tr>
            </thead>
            <tbody>
              {stations.map((item) => (
                <tr key={`station-${item.id}`}>
                  <td className="accidents-table__cell accidents-table__cell--name">
                    <button
                      type="button"
                      className="entity-link"
                      onClick={() => openAgentDetail(item.id)}
                    >
                      {item.name}
                    </button>
                  </td>
                  <td className="accidents-table__cell accidents-table__cell--muted">
                    {item.zone_name || `地区#${item.zone_id}`}
                  </td>
                  <td className="accidents-table__cell" style={{ width: "120px" }}>
                    <UsageMeter usage_ratio={item.usage_ratio} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : (
          <div className="accidents-list__empty">
            {t("statistics.noData")}
          </div>
        )}
      </div>
    </section>
  );

  return (
    <div className="accidents-tab-container">
      {/* ===== 上3分の1: サマリと事故死者数の推移 ===== */}
      <div className="accidents-top-section">
        {/* 左半分: サマリカード */}
        <div className="accidents-summary-card">
          <h3 className="accidents-summary-title">{t("statistics.summaryTitle")}</h3>
          <div className="accidents-summary-grid">
            <div className="accidents-summary-item" onClick={() => setActiveSubTab("fire")} style={{ cursor: "pointer" }}>
              <span className="accidents-summary-item__label">{t("statistics.summaryFire")}</span>
              <span className="accidents-summary-item__value" style={{ color: COLOR_FIRE }}>
                {latest.fires.length}
              </span>
            </div>
            <div className="accidents-summary-item" onClick={() => setActiveSubTab("crime")} style={{ cursor: "pointer" }}>
              <span className="accidents-summary-item__label">{t("statistics.summaryCrime")}</span>
              <span className="accidents-summary-item__value" style={{ color: COLOR_CRIME }}>
                {latest.crimes.length}
              </span>
            </div>
            <div className="accidents-summary-item" onClick={() => setActiveSubTab("traffic")} style={{ cursor: "pointer" }}>
              <span className="accidents-summary-item__label">{t("statistics.summaryTraffic")}</span>
              <span className="accidents-summary-item__value" style={{ color: COLOR_TRAFFIC }}>
                {latest.traffic_accidents.length}
              </span>
            </div>
            <div className="accidents-summary-item" onClick={() => setActiveSubTab("collapse")} style={{ cursor: "pointer" }}>
              <span className="accidents-summary-item__label">{t("statistics.summaryCollapse")}</span>
              <span className="accidents-summary-item__value" style={{ color: COLOR_COLLAPSE }}>
                {latest.collapses.length}
              </span>
            </div>
          </div>
        </div>

        {/* 右半分: 事故死者数の推移 */}
        <div className="accidents-deaths-chart">
          <section className="accidents-chart" style={{ height: "100%" }}>
            <h3 className="accidents-chart__title accidents-chart__title--death">
              {t("statistics.accidentDeathsHistory")}
            </h3>
            <div className="accidents-chart__area">
              {hasHistory ? (
                <ResponsiveContainer width="100%" height="100%">
                  <BarChart data={history} margin={CHART_MARGIN}>
                    {renderAxesAndGrid()}
                    <Legend wrapperStyle={{ fontSize: "0.93rem" }} />
                    <Bar
                      dataKey="traffic_deaths"
                      name={t("statistics.trafficDeaths")}
                      stackId="d"
                      fill={COLOR_TRAFFIC}
                    />
                    <Bar
                      dataKey="collapse_deaths"
                      name={t("statistics.collapseDeaths")}
                      stackId="d"
                      fill={COLOR_COLLAPSE}
                      radius={[2, 2, 0, 0]}
                    />
                  </BarChart>
                </ResponsiveContainer>
              ) : (
                <div className="accidents-chart__empty">
                  {t("statistics.noData")}
                </div>
              )}
            </div>
          </section>
        </div>
      </div>

      {/* ===== 下3分の2: サブタブ切替と詳細情報 ===== */}
      <div className="accidents-bottom-section">
        <nav className="accidents-sub-tabs">
          <button
            type="button"
            className={`accidents-sub-tab-btn ${activeSubTab === "fire" ? "accidents-sub-tab-btn--active" : ""}`}
            style={{ borderBottomColor: activeSubTab === "fire" ? COLOR_FIRE : "transparent" }}
            onClick={() => setActiveSubTab("fire")}
          >
            {t("statistics.subTabFire")}
          </button>
          <button
            type="button"
            className={`accidents-sub-tab-btn ${activeSubTab === "traffic" ? "accidents-sub-tab-btn--active" : ""}`}
            style={{ borderBottomColor: activeSubTab === "traffic" ? COLOR_TRAFFIC : "transparent" }}
            onClick={() => setActiveSubTab("traffic")}
          >
            {t("statistics.subTabTraffic")}
          </button>
          <button
            type="button"
            className={`accidents-sub-tab-btn ${activeSubTab === "crime" ? "accidents-sub-tab-btn--active" : ""}`}
            style={{ borderBottomColor: activeSubTab === "crime" ? COLOR_CRIME : "transparent" }}
            onClick={() => setActiveSubTab("crime")}
          >
            {t("statistics.subTabCrime")}
          </button>
          <button
            type="button"
            className={`accidents-sub-tab-btn ${activeSubTab === "collapse" ? "accidents-sub-tab-btn--active" : ""}`}
            style={{ borderBottomColor: activeSubTab === "collapse" ? COLOR_COLLAPSE : "transparent" }}
            onClick={() => setActiveSubTab("collapse")}
          >
            {t("statistics.subTabCollapse")}
          </button>
        </nav>

        <div className="accidents-sub-tab-content">
          {activeSubTab === "fire" && (
            <div className="accidents-tab-split">
              {/* 左半分 */}
              <div className="accidents-tab-split__left">
                {/* 火災推移グラフ */}
                <section className="accidents-chart" style={{ flex: 1 }}>
                  <h3 className="accidents-chart__title accidents-chart__title--fire">
                    {t("statistics.fireCountHistory")}
                  </h3>
                  <div className="accidents-chart__area">
                    {hasHistory ? (
                      <ResponsiveContainer width="100%" height="100%">
                        <BarChart data={processedHistory} margin={CHART_MARGIN}>
                          {renderAxesAndGrid()}
                          <Legend wrapperStyle={{ fontSize: "0.93rem" }} />
                          <Bar
                            dataKey="fire_count"
                            name={t("statistics.fireCount")}
                            stackId="f"
                            fill={COLOR_COLLAPSE}
                          />
                          <Bar
                            dataKey="misfire_only"
                            name={t("statistics.misfireCount")}
                            stackId="f"
                            fill="rgba(239, 68, 68, 0.35)"
                            radius={[2, 2, 0, 0]}
                          />
                        </BarChart>
                      </ResponsiveContainer>
                    ) : (
                      <div className="accidents-chart__empty">
                        {t("statistics.noData")}
                      </div>
                    )}
                  </div>
                </section>
                {/* 火災一覧 */}
                <section className="accidents-list" style={{ flex: 1 }}>
                  <h3 className="accidents-list__title">
                    {t("statistics.fireList")}
                    <span className="accidents-list__count">({latest.fires.length})</span>
                  </h3>
                  <div className="accidents-list__body">
                    {latest.fires.length > 0 ? (
                      <table className="accidents-table">
                        <thead>
                          <tr>
                            <th>{t("statistics.columnBuilding")}</th>
                            <th>{t("statistics.columnType")}</th>
                          </tr>
                        </thead>
                        <tbody>
                          {latest.fires.map((item) => (
                            <tr key={`fire-${item.id}`}>
                              <td className="accidents-table__cell accidents-table__cell--name">
                                <button
                                  type="button"
                                  className="entity-link"
                                  onClick={() => openAgentDetail(item.id)}
                                >
                                  {item.name || `#${item.id}`}
                                </button>
                              </td>
                              <td className="accidents-table__cell accidents-table__cell--muted">
                                {agentVariantName(item.variant)}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    ) : (
                      <div className="accidents-list__empty">
                        {t("statistics.noFiresThisEpoch")}
                      </div>
                    )}
                  </div>
                </section>
              </div>
              {/* 右半分: 消防署一覧 */}
              <div className="accidents-tab-split__right">
                {renderStationTable(data.fire_stations, t("statistics.fireStationsTitle"))}
              </div>
            </div>
          )}

          {activeSubTab === "traffic" && (
            <div className="accidents-tab-split">
              {/* 左半分 */}
              <div className="accidents-tab-split__left">
                {/* 交通事故件数の推移 */}
                <section className="accidents-chart" style={{ flex: 1 }}>
                  <h3 className="accidents-chart__title accidents-chart__title--traffic">
                    {t("statistics.trafficAccidentCountHistory")}
                  </h3>
                  <div className="accidents-chart__area">
                    {hasHistory ? (
                      <ResponsiveContainer width="100%" height="100%">
                        <BarChart data={history} margin={CHART_MARGIN}>
                          {renderAxesAndGrid()}
                          <Bar
                            dataKey="traffic_accident_count"
                            name={t("statistics.summaryTraffic")}
                            fill={COLOR_TRAFFIC}
                            radius={[2, 2, 0, 0]}
                          />
                        </BarChart>
                      </ResponsiveContainer>
                    ) : (
                      <div className="accidents-chart__empty">
                        {t("statistics.noData")}
                      </div>
                    )}
                  </div>
                </section>
                {/* 交通事故一覧 */}
                <section className="accidents-list" style={{ flex: 1 }}>
                  <h3 className="accidents-list__title">
                    {t("statistics.trafficAccidentList")}
                    <span className="accidents-list__count">
                      ({latest.traffic_accidents.length})
                    </span>
                  </h3>
                  <div className="accidents-list__body">
                    {latest.traffic_accidents.length > 0 ? (
                      <table className="accidents-table">
                        <thead>
                          <tr>
                            <th>{t("statistics.columnRoad")}</th>
                            <th>{t("statistics.columnType")}</th>
                          </tr>
                        </thead>
                        <tbody>
                          {latest.traffic_accidents.map((item) => (
                            <tr key={item.id}>
                              <td className="accidents-table__cell accidents-table__cell--name">
                                <PathLink
                                  pathId={item.id}
                                  pathName={item.name || `#${item.id}`}
                                />
                              </td>
                              <td className="accidents-table__cell accidents-table__cell--muted">
                                {pathVariantName(item.path_variant)}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    ) : (
                      <div className="accidents-list__empty">
                        {t("statistics.noAccidentsThisEpoch")}
                      </div>
                    )}
                  </div>
                </section>
              </div>
              {/* 右半分: 空白 */}
              <div className="accidents-tab-split__right" />
            </div>
          )}

          {activeSubTab === "crime" && (
            <div className="accidents-tab-split">
              {/* 左半分 */}
              <div className="accidents-tab-split__left">
                {/* 犯罪推移グラフ */}
                <section className="accidents-chart" style={{ flex: 1 }}>
                  <h3 className="accidents-chart__title" style={{ borderLeftColor: COLOR_CRIME }}>
                    {t("statistics.crimeCountHistory")}
                  </h3>
                  <div className="accidents-chart__area">
                    {hasHistory ? (
                      <ResponsiveContainer width="100%" height="100%">
                        <BarChart data={history} margin={CHART_MARGIN}>
                          {renderAxesAndGrid()}
                          <Legend wrapperStyle={{ fontSize: "0.93rem" }} />
                          <Bar
                            dataKey="crime_count"
                            name={t("statistics.crimeCount")}
                            stackId="c"
                            fill={COLOR_CRIME}
                          />
                          <Bar
                            dataKey="crime_sign_count"
                            name={t("statistics.crimeSignCount")}
                            stackId="c"
                            fill={COLOR_CRIME_SIGN}
                            radius={[2, 2, 0, 0]}
                          />
                        </BarChart>
                      </ResponsiveContainer>
                    ) : (
                      <div className="accidents-chart__empty">
                        {t("statistics.noData")}
                      </div>
                    )}
                  </div>
                </section>
                {/* 犯罪一覧 */}
                <section className="accidents-list" style={{ flex: 1 }}>
                  <h3 className="accidents-list__title" style={{ borderLeftColor: COLOR_CRIME }}>
                    {t("statistics.crimeList")}
                    <span className="accidents-list__count">({latest.crimes.length})</span>
                  </h3>
                  <div className="accidents-list__body">
                    {latest.crimes.length > 0 ? (
                      <table className="accidents-table">
                        <thead>
                          <tr>
                            <th>{t("statistics.columnBuilding")}</th>
                            <th>{t("statistics.columnType")}</th>
                          </tr>
                        </thead>
                        <tbody>
                          {latest.crimes.map((item) => (
                            <tr key={`crime-${item.id}`}>
                              <td className="accidents-table__cell accidents-table__cell--name">
                                <button
                                  type="button"
                                  className="entity-link"
                                  onClick={() => openAgentDetail(item.id)}
                                >
                                  {item.name || `#${item.id}`}
                                </button>
                              </td>
                              <td className="accidents-table__cell accidents-table__cell--muted">
                                {agentVariantName(item.variant)}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    ) : (
                      <div className="accidents-list__empty">
                        {t("statistics.noCrimesThisEpoch")}
                      </div>
                    )}
                  </div>
                </section>
              </div>
              {/* 右半分: 警察署一覧 */}
              <div className="accidents-tab-split__right">
                {renderStationTable(data.police_stations, t("statistics.policeStationsTitle"))}
              </div>
            </div>
          )}

          {activeSubTab === "collapse" && (
            <div className="accidents-tab-split">
              {/* 左半分 */}
              <div className="accidents-tab-split__left">
                {/* 倒壊件数の推移 */}
                <section className="accidents-chart" style={{ flex: 1 }}>
                  <h3 className="accidents-chart__title accidents-chart__title--death">
                    {t("statistics.collapseCountHistory")}
                  </h3>
                  <div className="accidents-chart__area">
                    {hasHistory ? (
                      <ResponsiveContainer width="100%" height="100%">
                        <BarChart data={history} margin={CHART_MARGIN}>
                          {renderAxesAndGrid()}
                          <Bar
                            dataKey="collapse_count"
                            name={t("statistics.summaryCollapse")}
                            fill={COLOR_COLLAPSE}
                            radius={[2, 2, 0, 0]}
                          />
                        </BarChart>
                      </ResponsiveContainer>
                    ) : (
                      <div className="accidents-chart__empty">
                        {t("statistics.noData")}
                      </div>
                    )}
                  </div>
                </section>
                {/* 倒壊一覧 */}
                <section className="accidents-list" style={{ flex: 1 }}>
                  <h3 className="accidents-list__title" style={{ borderLeftColor: COLOR_COLLAPSE }}>
                    {t("statistics.collapseList")}
                    <span className="accidents-list__count">
                      ({latest.collapses.length})
                    </span>
                  </h3>
                  <div className="accidents-list__body">
                    {latest.collapses.length > 0 ? (
                      <table className="accidents-table">
                        <thead>
                          <tr>
                            <th>{t("statistics.columnTarget")}</th>
                            <th>{t("statistics.columnCause")}</th>
                            <th className="accidents-table__th--num">
                              {t("statistics.columnDeaths")}
                            </th>
                          </tr>
                        </thead>
                        <tbody>
                          {latest.collapses.map((item) => (
                            <tr key={`col-${item.is_path ? "p" : "a"}-${item.id}`}>
                              <td className="accidents-table__cell accidents-table__cell--name">
                                {item.is_path ? (
                                  <PathLink
                                    pathId={item.id}
                                    pathName={item.name || `#${item.id}`}
                                  />
                                ) : (
                                  <button
                                    type="button"
                                    className="entity-link"
                                    onClick={() => openAgentDetail(item.id)}
                                  >
                                    {item.name || `#${item.id}`}
                                  </button>
                                )}
                              </td>
                              <td className="accidents-table__cell">
                                {item.cause === "fire"
                                  ? t("statistics.collapseCauseFire")
                                  : t("statistics.collapseCauseAging")}
                              </td>
                              <td className="accidents-table__cell accidents-table__cell--num">
                                {item.death_count}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    ) : (
                      <div className="accidents-list__empty">
                        {t("statistics.noCollapsesThisEpoch")}
                      </div>
                    )}
                  </div>
                </section>
              </div>
              {/* 右半分: 空白 */}
              <div className="accidents-tab-split__right" />
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
