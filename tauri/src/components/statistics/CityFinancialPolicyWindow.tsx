import { useEffect, useState, useMemo } from "react";
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
import { fetchApi, buildApiUrl } from "../../utils/api";
import "./CityFinancialPolicyWindow.css";
import { useRemPx } from "../../hooks/useRemPx";

type FinanceStats = {
  epoch: number;
  epochs: number[];
  current: {
    money_supply: number;
    gdp: number;
    real_gdp: number;
    price_index: number;
    quantitative_easing_per_day: number;
    central_bank_reserves: number;
    central_bank_collections: number;
  };
  history: {
    epochs: number[];
    gdp: number[];
    real_gdp: number[];
    price_index: number[];
    quantitative_easing_per_day: number[];
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

  // 量的緩和のローカル設定値
  const [currentQe, setCurrentQe] = useState<number>(0);
  const [isSaving, setIsSaving] = useState<boolean>(false);

  // APIデータ取得完了時に初期値を設定
  useEffect(() => {
    if (data?.current) {
      setCurrentQe(data.current.quantitative_easing_per_day);
    }
  }, [data]);

  // 設定保存処理
  const saveQeSetting = async (value: number) => {
    if (isSaving) return;
    setIsSaving(true);
    try {
      await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/city"), {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ quantitative_easing_per_day: value }),
      });
    } catch (err) {
      console.error("Failed to save QE setting:", err);
    } finally {
      setIsSaving(false);
    }
  };

  const handleSliderChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const val = Number(e.target.value);
    setCurrentQe(val);
  };

  const handleSliderRelease = () => {
    void saveQeSetting(currentQe);
  };

  const handleNumberInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const val = Number(e.target.value);
    setCurrentQe(Math.max(0, val));
  };

  const handleNumberInputBlur = () => {
    void saveQeSetting(currentQe);
  };

  const handleNumberInputKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "Enter") {
      void saveQeSetting(currentQe);
      e.currentTarget.blur();
    }
  };

  // グラフ用時系列データの整形
  const hasHistory = useMemo(() => {
    return data && data.history && data.history.epochs.length > 0;
  }, [data]);

  const qeChartData = useMemo(() => {
    if (!hasHistory || !data) return [];
    return data.history.epochs.map((epoch, idx) => ({
      epoch,
      qe: data.history.quantitative_easing_per_day[idx] ?? 0,
    }));
  }, [data, hasHistory]);

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

  const { money_supply, gdp, real_gdp, price_index } = data.current;

  // フォーマット用
  const formatCurrency = (v: number) => `¥${v.toLocaleString(undefined, { maximumFractionDigits: 0 })}`;
  const formatPriceIndex = (v: number) => `${v.toFixed(3)} (${(v * 100).toFixed(1)}%)`;

  return (
    <div className="finance-window">
      {/* 左ペイン: サマリとポリシー設定 */}
      <div className="finance-window__left-pane">
        <div className="finance-card">
          <h3 className="finance-card__title">{t("financialPolicy.summaryCard")}</h3>
          <dl className="finance-summary-list">
            <div className="finance-summary-item">
              <dt>{t("financialPolicy.moneySupply")}</dt>
              <dd className="finance-summary-value">{formatCurrency(money_supply)}</dd>
            </div>
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

        <div className="finance-card">
          <h3 className="finance-card__title">{t("financialPolicy.qeSlider")}</h3>
          <div className="finance-control-group">
            <div className="finance-slider-container">
              <input
                type="range"
                className="finance-slider"
                min="0"
                max="50000"
                step="100"
                value={currentQe}
                onChange={handleSliderChange}
                onMouseUp={handleSliderRelease}
                onTouchEnd={handleSliderRelease}
              />
            </div>
            <div className="finance-input-container">
              <input
                type="number"
                className="finance-number-input"
                min="0"
                max="1000000"
                value={currentQe}
                onChange={handleNumberInputChange}
                onBlur={handleNumberInputBlur}
                onKeyDown={handleNumberInputKeyDown}
              />
              <span className="finance-input-unit">¥ / {t("statistics.days")}</span>
            </div>
          </div>
          {isSaving && <div className="finance-status-saving">{t("common.saving")}</div>}
        </div>
        
        {/* 左下は空白 */}
        <div className="finance-window__left-bottom-space" />
      </div>

      {/* 右ペイン: 履歴グラフ */}
      <div className="finance-window__right-pane">
        {hasHistory ? (
          <div className="finance-charts-container">
            {/* 上段: 量的緩和 */}
            <div className="finance-chart-card">
              <h4 className="finance-chart-card__title">{t("financialPolicy.qeChart")}</h4>
              <div className="finance-chart-card__graph">
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart data={qeChartData} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
                    <XAxis dataKey="epoch" tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} />
                    <YAxis tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} width={remPx(5.56)} tickFormatter={(v) => `¥${v.toLocaleString()}`} />
                    <Tooltip contentStyle={TOOLTIP_STYLE} labelFormatter={(label) => `Epoch ${label}`} formatter={(value: any) => `¥${Number(value).toLocaleString()}`} />
                    <Line type="monotone" dataKey="qe" stroke="#fbbf24" strokeWidth={2} dot={false} activeDot={{ r: 4 }} isAnimationActive={false} />
                  </LineChart>
                </ResponsiveContainer>
              </div>
            </div>

            {/* 中段: GDP */}
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

            {/* 下段: 物価 */}
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

            {/* 右下の空セル */}
            <div className="finance-chart-card--empty" />
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
