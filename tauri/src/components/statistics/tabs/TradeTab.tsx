import { useMemo } from "react";
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
import "./TradeTab.css";

type HistoryPoint = {
  epoch: number;
  exchange_rate: number;
  import_value: number;
  export_value: number;
};

type TradeProductCapacity = {
  product_id: number;
  product_name: string;
  capacity: number;
  amount: number;
};

type TradeStats = {
  epoch: number;
  history: HistoryPoint[];
  import_capacities: TradeProductCapacity[];
  export_capacities: TradeProductCapacity[];
};

const COLOR_EXCHANGE_RATE = "#06b6d4"; // 為替レート: シアン
const COLOR_EXPORT = "#10b981";        // 輸出: エメラルドグリーン
const COLOR_IMPORT = "#ef4444";        // 輸入: ローズレッド

const CHART_MARGIN = { top: 12, right: 16, left: -10, bottom: 0 };
const Y_AXIS_WIDTH = 48;
const AXIS_TICK = { fontSize: 10, fill: "#8ba3bb" };
const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "11px",
};

export function TradeTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const { data, loading, error } = useEpochCachedStats<TradeStats>(
    apiBaseUrl,
    "/api/v1/stats/trade"
  );

  const history = data?.history ?? [];
  const rawImportCapacities = data?.import_capacities ?? [];
  const rawExportCapacities = data?.export_capacities ?? [];

  // 実績(amount)ゼロの財は表示しない。キャパシティがあっても取引実績が無いものは
  // ユーザーにとってノイズになるため、実績が1件でもある財だけをリストアップする。
  const importCapacities = useMemo(
    () => rawImportCapacities.filter((p) => p.amount > 0),
    [rawImportCapacities]
  );
  const exportCapacities = useMemo(
    () => rawExportCapacities.filter((p) => p.amount > 0),
    [rawExportCapacities]
  );

  // 右側の棒グラフのスケーリング基準（最大値）を算出。表示対象に絞った上で算出する。
  const maxImportVal = useMemo(() => {
    return importCapacities.reduce(
      (max, p) => Math.max(max, p.capacity, p.amount),
      0
    );
  }, [importCapacities]);

  const maxExportVal = useMemo(() => {
    return exportCapacities.reduce(
      (max, p) => Math.max(max, p.capacity, p.amount),
      0
    );
  }, [exportCapacities]);

  if (loading) {
    return <div className="trade-tab__loading">{t("common.loading")}</div>;
  }

  if (error || !data) {
    return (
      <div className="trade-tab__error">
        {t("common.fetchError")}{error ? `: ${error}` : ""}
      </div>
    );
  }

  const hasHistory = history.length > 0;

  return (
    <div className="trade-tab">
      <h2 className="trade-tab__title">{t("statistics.tradeTitle")}</h2>

      <div className="trade-tab__grid">
        {/* 左半分: 履歴推移グラフ */}
        <div className="trade-tab__col-left">
          {/* 為替レート推移 */}
          <div className="trade-card">
            <h3 className="trade-card__title">
              {t("statistics.exchangeRateHistory")}
            </h3>
            <div className="trade-card__chart-container">
              {hasHistory ? (
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart data={history} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
                    <XAxis
                      dataKey="epoch"
                      tick={AXIS_TICK}
                      tickLine={false}
                      axisLine={{ stroke: "#253246" }}
                    />
                    <YAxis
                      width={Y_AXIS_WIDTH}
                      tick={AXIS_TICK}
                      tickLine={false}
                      axisLine={{ stroke: "#253246" }}
                      domain={["auto", "auto"]}
                    />
                    <Tooltip
                      contentStyle={TOOLTIP_STYLE}
                      labelFormatter={(label) => `Epoch ${label}`}
                    />
                    <Line
                      type="monotone"
                      dataKey="exchange_rate"
                      name={t("statistics.exchangeRate")}
                      stroke={COLOR_EXCHANGE_RATE}
                      strokeWidth={2}
                      dot={{ r: 2 }}
                      activeDot={{ r: 4 }}
                    />
                  </LineChart>
                </ResponsiveContainer>
              ) : (
                <div className="trade-card__no-data">
                  {t("statistics.noTradeData")}
                </div>
              )}
            </div>
          </div>

          {/* 輸出入金額推移 */}
          <div className="trade-card">
            <h3 className="trade-card__title">
              {t("statistics.tradeValueHistory")}
            </h3>
            <div className="trade-card__chart-container">
              {hasHistory ? (
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart data={history} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
                    <XAxis
                      dataKey="epoch"
                      tick={AXIS_TICK}
                      tickLine={false}
                      axisLine={{ stroke: "#253246" }}
                    />
                    <YAxis
                      width={Y_AXIS_WIDTH}
                      tick={AXIS_TICK}
                      tickLine={false}
                      axisLine={{ stroke: "#253246" }}
                    />
                    <Tooltip
                      contentStyle={TOOLTIP_STYLE}
                      labelFormatter={(label) => `Epoch ${label}`}
                    />
                    <Line
                      type="monotone"
                      dataKey="export_value"
                      name={t("statistics.exportValue")}
                      stroke={COLOR_EXPORT}
                      strokeWidth={2}
                      dot={{ r: 2 }}
                    />
                    <Line
                      type="monotone"
                      dataKey="import_value"
                      name={t("statistics.importValue")}
                      stroke={COLOR_IMPORT}
                      strokeWidth={2}
                      dot={{ r: 2 }}
                    />
                  </LineChart>
                </ResponsiveContainer>
              ) : (
                <div className="trade-card__no-data">
                  {t("statistics.noTradeData")}
                </div>
              )}
            </div>
          </div>
        </div>

        {/* 右半分: キャパシティ・実績量の重ね合わせ棒グラフ */}
        <div className="trade-tab__col-right">
          {/* 輸出拠点キャパシティ合計 */}
          <div className="trade-card">
            <h3 className="trade-card__title">
              {t("statistics.exportCapacityTotal")}
            </h3>
            <div className="trade-card__list-container">
              {exportCapacities.length > 0 ? (
                exportCapacities.map((item) => {
                  const capWidth = maxExportVal > 0 ? (item.capacity / maxExportVal) * 100 : 0;
                  const amtWidth = maxExportVal > 0 ? (item.amount / maxExportVal) * 100 : 0;
                  return (
                    <div key={item.product_id} className="trade-bar-item">
                      <div className="trade-bar-item__header">
                        <span className="trade-bar-item__name">{item.product_name}</span>
                        <span className="trade-bar-item__values">
                          <span className="trade-bar-item__val-actual" style={{ color: COLOR_EXPORT }}>
                            {item.amount.toLocaleString(undefined, { maximumFractionDigits: 1 })}
                          </span>
                          <span className="trade-bar-item__val-divider">/</span>
                          <span className="trade-bar-item__val-capacity">
                            {item.capacity.toLocaleString(undefined, { maximumFractionDigits: 1 })}
                          </span>
                        </span>
                      </div>
                      <div className="trade-bar-track">
                        {/* キャパシティ（薄い棒） */}
                        <div
                          className="trade-bar-fill trade-bar-fill--capacity-export"
                          style={{ width: `${capWidth}%` }}
                          title={`${t("statistics.capacity")}: ${item.capacity}`}
                        />
                        {/* 実際の取扱量（濃い棒） */}
                        <div
                          className="trade-bar-fill trade-bar-fill--actual-export"
                          style={{ width: `${amtWidth}%` }}
                          title={`${t("statistics.actualAmount")}: ${item.amount}`}
                        />
                      </div>
                    </div>
                  );
                })
              ) : (
                <div className="trade-card__no-data">
                  {t("statistics.noTradeData")}
                </div>
              )}
            </div>
          </div>

          {/* 輸入拠点キャパシティ合計 */}
          <div className="trade-card">
            <h3 className="trade-card__title">
              {t("statistics.importCapacityTotal")}
            </h3>
            <div className="trade-card__list-container">
              {importCapacities.length > 0 ? (
                importCapacities.map((item) => {
                  const capWidth = maxImportVal > 0 ? (item.capacity / maxImportVal) * 100 : 0;
                  const amtWidth = maxImportVal > 0 ? (item.amount / maxImportVal) * 100 : 0;
                  return (
                    <div key={item.product_id} className="trade-bar-item">
                      <div className="trade-bar-item__header">
                        <span className="trade-bar-item__name">{item.product_name}</span>
                        <span className="trade-bar-item__values">
                          <span className="trade-bar-item__val-actual" style={{ color: COLOR_IMPORT }}>
                            {item.amount.toLocaleString(undefined, { maximumFractionDigits: 1 })}
                          </span>
                          <span className="trade-bar-item__val-divider">/</span>
                          <span className="trade-bar-item__val-capacity">
                            {item.capacity.toLocaleString(undefined, { maximumFractionDigits: 1 })}
                          </span>
                        </span>
                      </div>
                      <div className="trade-bar-track">
                        {/* キャパシティ（薄い棒） */}
                        <div
                          className="trade-bar-fill trade-bar-fill--capacity-import"
                          style={{ width: `${capWidth}%` }}
                          title={`${t("statistics.capacity")}: ${item.capacity}`}
                        />
                        {/* 実際の取扱量（濃い棒） */}
                        <div
                          className="trade-bar-fill trade-bar-fill--actual-import"
                          style={{ width: `${amtWidth}%` }}
                          title={`${t("statistics.actualAmount")}: ${item.amount}`}
                        />
                      </div>
                    </div>
                  );
                })
              ) : (
                <div className="trade-card__no-data">
                  {t("statistics.noTradeData")}
                </div>
              )}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
