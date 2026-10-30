/**
 * Finance statistics tab.
 *
 * Left column: GDP (nominal/real) and GDP deflator histories, stacked vertically.
 * Right column: breakdown of the latest Epoch's nominal GDP (GdpBreakdownPanel).
 */
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
import { useEpochCachedStats } from "../useEpochCache";
import { GdpBreakdownPanel, type GdpBreakdown } from "./GdpBreakdown";
import { resolveIntlLocale } from "../../../utils/locale";
import { formatCompactAmount, formatExactAmount } from "../../../utils/money";
import "./FinanceTab.css";
import { useRemPx } from "../../../hooks/useRemPx";

type FinanceStats = {
  epoch: number;
  epochs: number[];
  history: {
    epochs: number[];
    gdp: number[];
    real_gdp: number[];
    price_index: number[]; // GDP deflator (1.0 = baseline).
  };
  breakdown: GdpBreakdown | null;
};

const CHART_MARGIN = { top: 12, right: 16, left: 10, bottom: 0 };
const AXIS_TICK = { fontSize: "0.93rem", fill: "#8ba3bb" };
const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "1rem",
};

export function FinanceTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);
  const remPx = useRemPx();
  const { data, loading, error } = useEpochCachedStats<FinanceStats>(
    apiBaseUrl,
    "/api/v1/stats/finance"
  );

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

  return (
    <div className="finance-tab">
      <div className="finance-tab__history">
        {hasHistory ? (
          <>
            <div className="finance-chart-card">
              <h4 className="finance-chart-card__title">{t("financialPolicy.gdpChart")}</h4>
              <div className="finance-chart-card__graph">
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart data={gdpChartData} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
                    <XAxis dataKey="epoch" tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} />
                    <YAxis tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} width={remPx(5.56)} tickFormatter={(v) => formatCompactAmount(Number(v), locale, true)} />
                    <Tooltip contentStyle={TOOLTIP_STYLE} labelFormatter={(label) => `Epoch ${label}`} formatter={(value: any) => formatExactAmount(Number(value), locale, true)} />
                    <Legend wrapperStyle={{ fontSize: "0.93rem" }} iconType="plainline" />
                    <Line type="monotone" dataKey="nominal" name={t("financialPolicy.nominalGdp")} stroke="#4ea1ff" strokeWidth={2} dot={false} activeDot={{ r: 4 }} isAnimationActive={false} />
                    <Line type="monotone" dataKey="real" name={t("financialPolicy.realGdp")} stroke="#c084fc" strokeWidth={2} strokeDasharray="5 4" dot={false} activeDot={{ r: 4 }} isAnimationActive={false} />
                  </LineChart>
                </ResponsiveContainer>
              </div>
            </div>

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
          </>
        ) : (
          <div className="finance-charts-empty">
            {t("financialPolicy.noData")}
          </div>
        )}
      </div>

      <div className="finance-tab__breakdown">
        {data.breakdown ? (
          <GdpBreakdownPanel breakdown={data.breakdown} />
        ) : (
          <div className="finance-charts-empty">
            {t("financialPolicy.breakdownEmpty")}
          </div>
        )}
      </div>
    </div>
  );
}
