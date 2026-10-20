import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import {
  LineChart,
  Line,
  BarChart,
  Bar,
  Cell,
  ReferenceLine,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
} from "recharts";
import { useEpochCachedStats } from "../useEpochCache";
import "./TradeTab.css";
import { useRemPx } from "../../../hooks/useRemPx";

type HistoryPoint = {
  epoch: number;
  import_value: number;
  export_value: number;
  trade_balance: number; // Export value − import value. Positive = money enters the city; negative = money leaves.
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

const COLOR_EXPORT = "#10b981";        // Exports: emerald green.
const COLOR_IMPORT = "#ef4444";        // Imports: rose red.

const CHART_MARGIN = { top: 12, right: 16, left: -10, bottom: 0 };
const Y_AXIS_WIDTH_REM = 4.44; // Y-axis width (rem). Convert to px with useRemPx when rendering.
const AXIS_TICK = { fontSize: "0.93rem", fill: "#8ba3bb" };
const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "1rem",
};

export function TradeTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const remPx = useRemPx();
  const { data, loading, error } = useEpochCachedStats<TradeStats>(
    apiBaseUrl,
    "/api/v1/stats/trade"
  );

  const history = data?.history ?? [];
  const rawImportCapacities = data?.import_capacities ?? [];
  const rawExportCapacities = data?.export_capacities ?? [];

  // Hide goods with zero recorded amount. Capacity without any actual trades is noise to users,
  // so list only goods with at least one recorded trade.
  const importCapacities = useMemo(
    () => rawImportCapacities.filter((p) => p.amount > 0),
    [rawImportCapacities]
  );
  const exportCapacities = useMemo(
    () => rawExportCapacities.filter((p) => p.amount > 0),
    [rawExportCapacities]
  );

  // Calculate the right bar chart's scaling reference (maximum), using only displayed goods.
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
        {/* Left half: history charts */}
        <div className="trade-tab__col-left">
          {/* Trade balance history. Color surpluses like exports and deficits like imports. */}
          <div className="trade-card">
            <h3 className="trade-card__title">
              {t("statistics.tradeBalanceHistory")}
            </h3>
            <div className="trade-card__chart-container">
              {hasHistory ? (
                <ResponsiveContainer width="100%" height="100%">
                  <BarChart data={history} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
                    <XAxis
                      dataKey="epoch"
                      tick={AXIS_TICK}
                      tickLine={false}
                      axisLine={{ stroke: "#253246" }}
                    />
                    <YAxis
                      width={remPx(Y_AXIS_WIDTH_REM)}
                      tick={AXIS_TICK}
                      tickLine={false}
                      axisLine={{ stroke: "#253246" }}
                    />
                    <Tooltip
                      contentStyle={TOOLTIP_STYLE}
                      labelFormatter={(label) => `Epoch ${label}`}
                      cursor={{ fill: "rgba(255,255,255,0.05)" }}
                    />
                    <ReferenceLine y={0} stroke="#8ba3bb" />
                    <Bar
                      dataKey="trade_balance"
                      name={t("statistics.tradeBalance")}
                    >
                      {history.map((point) => (
                        <Cell
                          key={point.epoch}
                          fill={point.trade_balance >= 0 ? COLOR_EXPORT : COLOR_IMPORT}
                        />
                      ))}
                    </Bar>
                  </BarChart>
                </ResponsiveContainer>
              ) : (
                <div className="trade-card__no-data">
                  {t("statistics.noTradeData")}
                </div>
              )}
            </div>
          </div>

          {/* Export/import value history */}
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
                      width={remPx(Y_AXIS_WIDTH_REM)}
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

        {/* Right half: overlaid capacity and actual volume bars */}
        <div className="trade-tab__col-right">
          {/* Total export hub capacity */}
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
                        {/* Capacity (light bar) */}
                        <div
                          className="trade-bar-fill trade-bar-fill--capacity-export"
                          style={{ width: `${capWidth}%` }}
                          title={`${t("statistics.capacity")}: ${item.capacity}`}
                        />
                        {/* Actual throughput (dark bar) */}
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

          {/* Total import hub capacity */}
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
                        {/* Capacity (light bar) */}
                        <div
                          className="trade-bar-fill trade-bar-fill--capacity-import"
                          style={{ width: `${capWidth}%` }}
                          title={`${t("statistics.capacity")}: ${item.capacity}`}
                        />
                        {/* Actual throughput (dark bar) */}
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
