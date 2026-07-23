/**
 * 不動産統計タブコンポーネント
 *
 * GET /api/v1/stats/realestate から
 * フロアタイプごとの現在の供給量・占有量・空室率・需要圧、
 * および家賃と地価の推移履歴を取得して可視化する。
 *
 * レイアウト:
 * - 左右分割 (左 1/2, 右 1/2)
 *   - 左カラム: 不動産需給状況テーブル (用途別、プログレスバー付き空室率、需要圧バッジ)
 *   - 右カラム: 上下分割 (各 1/2 高さ)
 *     - 右上: 平均賃料の推移 (LineChart)
 *     - 右下: 平均地価の推移 (LineChart)
 */

import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
} from "recharts";
import { useEpochCachedStats } from "../useEpochCache";
import "./RealEstateTab.css";

// --- 型定義 ---

type FloorPoint = {
  space_type: number;
  space_type_name: string;
  capacity: number;
  occupied: number;
  vacancy: number;
  vacancy_rate: number;
  demand_pressure: number;
};

type HistoryPoint = {
  epoch: number;
  average_rent: number;
  average_land_price: number;
};

type Scope = {
  zone_id: number | null;
  name: string | null;
  current_floors: FloorPoint[];
  history: HistoryPoint[];
};

type RealEstateStats = {
  epoch: number;
  scopes: Scope[];
};

const CHART_MARGIN = { top: 10, right: 15, left: -5, bottom: 5 };

export function RealEstateTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const { data, loading, error } = useEpochCachedStats<RealEstateStats>(
    apiBaseUrl,
    "/api/v1/stats/realestate"
  );

  const [selectedScopeKey, setSelectedScopeKey] = useState("city");

  const scopes = data?.scopes ?? [];

  const currentScope = useMemo(() => {
    if (selectedScopeKey === "city") {
      return scopes.find((s) => s.zone_id === null);
    }
    return scopes.find((s) => String(s.zone_id) === selectedScopeKey);
  }, [scopes, selectedScopeKey]);

  if (loading) {
    return <div className="realestate-tab__loading">{t("common.loading")}</div>;
  }

  if (error || !currentScope || !data) {
    return (
      <div className="realestate-tab__error">
        {t("common.fetchError")}
        {error ? `: ${error}` : ""}
      </div>
    );
  }

  const getSpaceTypeName = (name: string) => {
    switch (name) {
      case "residence":
        return t("display.subSpaceResidence");
      case "retail":
        return t("display.subSpaceRetail");
      case "factory":
        return t("display.subSpaceFactory");
      case "office":
        return t("display.subSpaceOffice");
      case "farm":
        return t("display.subSpaceFarm");
      default:
        return t("display.subSpaceOther");
    }
  };

  return (
    <div className="realestate-tab">
      {/* ヘッダー部（スコープ選択） */}
      <header className="realestate-tab__header">
        <div className="scope-selector">
          <label htmlFor="realestate-scope-select" className="scope-selector__label">
            {t("statistics.scopeLabel", "集計範囲:")}
          </label>
          <select
            id="realestate-scope-select"
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

      <div className="realestate-tab__body">
        {/* 左カラム: 現在の需給状況 */}
        <div className="realestate-tab__left">
          <div className="realestate-card">
            <h3 className="realestate-card__title">
              {t("statistics.realestateTitle", "不動産需給状況")}
            </h3>
            <div className="realestate-table-container">
              <table className="realestate-table">
                <thead>
                  <tr>
                    <th>{t("statistics.floorType", "用途")}</th>
                    <th className="text-right">{t("statistics.floorCapacity", "供給容量")}</th>
                    <th className="text-right">{t("statistics.floorOccupied", "占有量")}</th>
                    <th className="text-right">{t("statistics.floorVacancy", "空き")}</th>
                    <th className="text-right">{t("statistics.floorVacancyRate", "空室率")}</th>
                    <th className="text-right">{t("statistics.floorDemandPressure", "需要圧")}</th>
                  </tr>
                </thead>
                <tbody>
                  {currentScope.current_floors.map((floor) => {
                    const occPercent = floor.capacity > 0 ? (floor.occupied / floor.capacity) * 100 : 0;
                    const vacPercent = floor.vacancy_rate * 100;
                    return (
                      <tr key={floor.space_type}>
                        <td className="font-semibold">{getSpaceTypeName(floor.space_type_name)}</td>
                        <td className="text-right">
                          {floor.capacity.toLocaleString(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 1 })} ㎡
                        </td>
                        <td className="text-right">
                          {floor.occupied.toLocaleString(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 1 })} ㎡
                        </td>
                        <td className="text-right">
                          {floor.vacancy.toLocaleString(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 1 })} ㎡
                        </td>
                        <td className="text-right">
                          <div className="vacancy-cell">
                            <span className="vacancy-rate">{vacPercent.toFixed(1)}%</span>
                            <div className="vacancy-bar-bg">
                              <div
                                className="vacancy-bar-fill"
                                style={{ width: `${Math.min(occPercent, 100)}%` }}
                              />
                            </div>
                          </div>
                        </td>
                        <td className="text-right">
                          <span className={`demand-pressure-badge ${floor.demand_pressure > 0 ? "has-pressure" : ""}`}>
                            {floor.demand_pressure.toLocaleString(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 1 })}
                          </span>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </div>
        </div>

        {/* 右カラム: 折れ線グラフ */}
        <div className="realestate-tab__right">
          {/* 右上: 賃料推移 */}
          <div className="realestate-card realestate-card--half">
            <h3 className="realestate-card__title">
              {t("statistics.realestateRentTrend", "平均賃料の推移")}
            </h3>
            <div className="realestate-chart-container">
              {currentScope.history.length === 0 ? (
                <div className="realestate-tab__empty">{t("statistics.noData", "時系列データがまだありません。")}</div>
              ) : (
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart
                    data={currentScope.history}
                    margin={CHART_MARGIN}
                  >
                    <CartesianGrid strokeDasharray="3 3" stroke="#253545" horizontal={true} vertical={false} />
                    <XAxis dataKey="epoch" stroke="#8ba3bb" fontSize={9} />
                    <YAxis stroke="#8ba3bb" fontSize={9} />
                    <Tooltip
                      contentStyle={{ background: "#1a2230", border: "1px solid rgba(255,255,255,0.1)", borderRadius: "4px" }}
                      itemStyle={{ fontSize: 10 }}
                      labelStyle={{ fontSize: 10, fontWeight: "bold", color: "#8ba3bb" }}
                    />
                    <Line
                      type="monotone"
                      dataKey="average_rent"
                      stroke="#fab005"
                      strokeWidth={2}
                      dot={{ r: 2 }}
                      name={t("statistics.averageRent", "平均賃料")}
                    />
                  </LineChart>
                </ResponsiveContainer>
              )}
            </div>
          </div>

          {/* 右下: 地価推移 */}
          <div className="realestate-card realestate-card--half">
            <h3 className="realestate-card__title">
              {t("statistics.realestateLandPriceTrend", "平均地価の推移")}
            </h3>
            <div className="realestate-chart-container">
              {currentScope.history.length === 0 ? (
                <div className="realestate-tab__empty">{t("statistics.noData", "時系列データがまだありません。")}</div>
              ) : (
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart
                    data={currentScope.history}
                    margin={CHART_MARGIN}
                  >
                    <CartesianGrid strokeDasharray="3 3" stroke="#253545" horizontal={true} vertical={false} />
                    <XAxis dataKey="epoch" stroke="#8ba3bb" fontSize={9} />
                    <YAxis stroke="#8ba3bb" fontSize={9} />
                    <Tooltip
                      contentStyle={{ background: "#1a2230", border: "1px solid rgba(255,255,255,0.1)", borderRadius: "4px" }}
                      itemStyle={{ fontSize: 10 }}
                      labelStyle={{ fontSize: 10, fontWeight: "bold", color: "#8ba3bb" }}
                    />
                    <Line
                      type="monotone"
                      dataKey="average_land_price"
                      stroke="#40c057"
                      strokeWidth={2}
                      dot={{ r: 2 }}
                      name={t("parcel.landPrice", "平均地価")}
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
