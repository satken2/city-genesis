import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import {
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ResponsiveContainer,
} from "recharts";
import { useEpochCachedStats } from "./useEpochCache";
import "./CityFinancialPolicyWindow.css";
import { useRemPx } from "../../hooks/useRemPx";

type FinanceStats = {
  epoch: number;
  epochs: number[];
  current: {
    gdp: number;
    real_gdp: number;
    price_index: number; // GDP デフレーター(1.0 = 基準)
    central_bank_reserves: number;
    central_bank_collections: number;
  };
  history: {
    epochs: number[];
    gdp: number[];
    real_gdp: number[];
    price_index: number[];
    central_bank_reserves: number[];
    central_bank_collections: number[];
  };
};

const CHART_MARGIN = { top: 12, right: 16, left: 10, bottom: 0 };
const AXIS_TICK = { fontSize: "0.93rem", fill: "#8ba3bb" };
const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "1rem",
};

export function CityFinancialPolicyWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const remPx = useRemPx();
  const { data, loading, error } = useEpochCachedStats<FinanceStats>(
    apiBaseUrl,
    "/api/v1/stats/finance"
  );

  // グラフ用時系列データの整形
  const hasHistory = useMemo(() => {
    return data && data.history && data.history.epochs.length > 0;
  }, [data]);

  const gdpChartData = useMemo(() => {
    if (!hasHistory || !data) return [];
    return data.history.epochs.map((epoch, idx) => ({
      epoch,
      nominal: data.history.gdp[idx] ?? 0,
      real: data.history.real_gdp[idx] ?? 0,
    }));
  }, [data, hasHistory]);

  const priceChartData = useMemo(() => {
    if (!hasHistory || !data) return [];
    return data.history.epochs.map((epoch, idx) => ({
      epoch,
      price: data.history.price_index[idx] ?? 1.0,
    }));
  }, [data, hasHistory]);

  const cbDebtChartData = useMemo(() => {
    if (!hasHistory || !data) return [];
    return data.history.epochs.map((epoch, idx) => {
      const reserves = data.history.central_bank_reserves?.[idx] ?? 0;
      return {
        epoch,
        debt: -reserves,
      };
    });
  }, [data, hasHistory]);

  const cbCollectionsChartData = useMemo(() => {
    if (!hasHistory || !data) return [];
    return data.history.epochs.map((epoch, idx) => ({
      epoch,
      collections: data.history.central_bank_collections?.[idx] ?? 0,
    }));
  }, [data, hasHistory]);

  if (loading) {
    return <div className="finance-window-loading">{t("common.loading")}</div>;
  }

  if (error || !data) {
    return (
      <div className="finance-window-error">
        {t("common.fetchError")}{error ? `: ${error}` : ""}
      </div>
    );
  }

  const { gdp, real_gdp, price_index } = data.current;

  // フォーマット用
  const formatCurrency = (v: number) => `¥${v.toLocaleString(undefined, { maximumFractionDigits: 0 })}`;
  const formatPriceIndex = (v: number) => `${v.toFixed(3)} (${(v * 100).toFixed(1)}%)`;

  return (
    <div className="finance-window">
      {/* 左ペイン: サマリ */}
      <div className="finance-window__left-pane">
        <div className="finance-card">
          <h3 className="finance-card__title">{t("financialPolicy.summaryCard")}</h3>
          <dl className="finance-summary-list">
            <div className="finance-summary-item">
              <dt>{t("financialPolicy.nominalGdp")}</dt>
              <dd className="finance-summary-value">{formatCurrency(gdp)}</dd>
            </div>
            <div className="finance-summary-item">
              <dt>{t("financialPolicy.realGdp")}</dt>
              <dd className="finance-summary-value">{formatCurrency(real_gdp)}</dd>
            </div>
            <div className="finance-summary-item">
              <dt>{t("financialPolicy.priceIndex")}</dt>
              <dd className="finance-summary-value">{formatPriceIndex(price_index)}</dd>
            </div>
          </dl>
        </div>

        {/* 左下は空白 */}
        <div className="finance-window__left-bottom-space" />
      </div>

      {/* 右ペイン: 履歴グラフ */}
      <div className="finance-window__right-pane">
        {hasHistory ? (
          <div className="finance-charts-container">
            {/* GDP */}
            <div className="finance-chart-card">
              <h4 className="finance-chart-card__title">{t("financialPolicy.gdpChart")}</h4>
              <div className="finance-chart-card__graph">
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart data={gdpChartData} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
                    <XAxis dataKey="epoch" tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} />
                    <YAxis tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} width={remPx(5.56)} tickFormatter={(v) => `¥${v.toLocaleString()}`} />
                    <Tooltip contentStyle={TOOLTIP_STYLE} labelFormatter={(label) => `Epoch ${label}`} formatter={(value: any) => `¥${Number(value).toLocaleString()}`} />
                    <Legend wrapperStyle={{ fontSize: "0.93rem" }} iconType="plainline" />
                    <Line type="monotone" dataKey="nominal" name={t("financialPolicy.nominalGdp")} stroke="#4ea1ff" strokeWidth={2} dot={false} activeDot={{ r: 4 }} isAnimationActive={false} />
                    <Line type="monotone" dataKey="real" name={t("financialPolicy.realGdp")} stroke="#c084fc" strokeWidth={2} strokeDasharray="5 4" dot={false} activeDot={{ r: 4 }} isAnimationActive={false} />
                  </LineChart>
                </ResponsiveContainer>
              </div>
            </div>

            {/* 物価(GDPデフレーター) */}
            <div className="finance-chart-card">
              <h4 className="finance-chart-card__title">{t("financialPolicy.priceChart")}</h4>
              <div className="finance-chart-card__graph">
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart data={priceChartData} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
                    <XAxis dataKey="epoch" tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} />
                    <YAxis tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} width={remPx(5.56)} tickFormatter={(v) => v.toFixed(2)} />
                    <Tooltip contentStyle={TOOLTIP_STYLE} labelFormatter={(label) => `Epoch ${label}`} formatter={(value: any) => `${Number(value).toFixed(3)} (${(Number(value) * 100).toFixed(1)}%)`} />
                    <Line type="monotone" dataKey="price" stroke="#10b981" strokeWidth={2} dot={false} activeDot={{ r: 4 }} isAnimationActive={false} />
                  </LineChart>
                </ResponsiveContainer>
              </div>
            </div>

            {/* 中央銀行の負債履歴 */}
            <div className="finance-chart-card">
              <h4 className="finance-chart-card__title">{t("financialPolicy.centralBankDebtChart")}</h4>
              <div className="finance-chart-card__graph">
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart data={cbDebtChartData} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
                    <XAxis dataKey="epoch" tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} />
                    <YAxis tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} width={remPx(5.56)} tickFormatter={(v) => `¥${v.toLocaleString()}`} />
                    <Tooltip contentStyle={TOOLTIP_STYLE} labelFormatter={(label) => `Epoch ${label}`} formatter={(value: any) => `¥${Number(value).toLocaleString()}`} />
                    <Line type="monotone" dataKey="debt" stroke="#f87171" strokeWidth={2} dot={false} activeDot={{ r: 4 }} isAnimationActive={false} />
                  </LineChart>
                </ResponsiveContainer>
              </div>
            </div>

            {/* 各経済主体からの徴収額履歴 */}
            <div className="finance-chart-card">
              <h4 className="finance-chart-card__title">{t("financialPolicy.collectionsChart")}</h4>
              <div className="finance-chart-card__graph">
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart data={cbCollectionsChartData} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
                    <XAxis dataKey="epoch" tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} />
                    <YAxis tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} width={remPx(5.56)} tickFormatter={(v) => `¥${v.toLocaleString()}`} />
                    <Tooltip contentStyle={TOOLTIP_STYLE} labelFormatter={(label) => `Epoch ${label}`} formatter={(value: any) => `¥${Number(value).toLocaleString()}`} />
                    <Line type="monotone" dataKey="collections" stroke="#6366f1" strokeWidth={2} dot={false} activeDot={{ r: 4 }} isAnimationActive={false} />
                  </LineChart>
                </ResponsiveContainer>
              </div>
            </div>
          </div>
        ) : (
          <div className="finance-charts-empty">
            {t("financialPolicy.noData")}
          </div>
        )}
      </div>
    </div>
  );
}
