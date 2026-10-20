import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { CartesianGrid, Line, LineChart, ReferenceDot, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { useRemPx } from "../../hooks/useRemPx";
import { resolveIntlLocale } from "../../utils/locale";
import type { PublicServiceSupply } from "../../types";
import { buildPublicServiceTrend, epochsToYears, publicServiceChartUpperBound, type PublicServiceProductHistory } from "./publicServiceTrend";

type PublicServiceTrendChartProps = {
  epochs: number[];
  history?: PublicServiceProductHistory;
  currentEpoch: number;
  supply?: PublicServiceSupply;
  infrastructureCapacity?: { label: string; value: number };
  capacityLabel?: string;
  simulationDays: number;
  unit: string;
};

export function PublicServiceTrendChart({ epochs, history, currentEpoch, supply, infrastructureCapacity, capacityLabel, simulationDays, unit }: PublicServiceTrendChartProps) {
  const { t, i18n } = useTranslation();
  const remPx = useRemPx();
  const locale = resolveIntlLocale(i18n.language);
  const compact = useMemo(() => new Intl.NumberFormat(locale, { notation: "compact", maximumFractionDigits: 1 }), [locale]);
  const trend = useMemo(() => buildPublicServiceTrend(epochs, history, currentEpoch, supply, simulationDays, infrastructureCapacity?.value),
    [epochs, history, currentEpoch, supply, simulationDays, infrastructureCapacity?.value]);
  const upperBound = useMemo(() => publicServiceChartUpperBound(trend), [trend]);
  const chartData = useMemo(() => trend.points.map((point) => ({
    ...point, years: epochsToYears(point.epoch - currentEpoch, simulationDays),
  })), [trend, currentEpoch, simulationDays]);
  const timeFormat = useMemo(() => new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }), [locale]);
  const formatDuration = (years: number) => {
    const value = Math.abs(years) < 1 ? Math.abs(years) * 12 : Math.abs(years);
    const count = Math.max(0.1, Math.round(value * 10) / 10);
    return t(Math.abs(years) < 1 ? "publicServiceTrend.months" : "publicServiceTrend.years", {
      count, amount: timeFormat.format(count),
    });
  };
  const formatRelativeTime = (years: number) => years === 0
    ? t("publicServiceTrend.current")
    : t(years < 0 ? "publicServiceTrend.past" : "publicServiceTrend.future", { time: formatDuration(years) });
  const capacity = supply?.supply_capacity ?? 0;
  const status = trend.capacityReachedIn === 0
    ? t("publicServiceTrend.atCapacity")
    : trend.sampleCount < 2
      ? t("publicServiceTrend.insufficientHistory")
      : trend.capacityReachedIn !== null
        ? t("publicServiceTrend.capacityIn", { time: formatDuration(epochsToYears(trend.capacityReachedIn, simulationDays)) })
        : t("publicServiceTrend.noCapacityCrossing", { years: trend.forecastYears });

  return (
    <div className="psp-trend">
      <div className="psp-trend__header">
        <span className="psp-field__label">{t("publicServicePrices.supplyStatus")}</span>
        <span className={trend.capacityReachedIn !== null ? "psp-trend__status psp-trend__status--warn" : "psp-trend__status"}>
          {status}
        </span>
      </div>
      <div className="psp-trend__legend">
        <span><i className="psp-trend__swatch psp-trend__swatch--demand" />{t("publicServicePrices.demand")}</span>
        <span><i className="psp-trend__swatch psp-trend__swatch--production" />{t("publicServicePrices.production")}</span>
        <span><i className="psp-trend__swatch psp-trend__swatch--forecast" />{t("publicServiceTrend.forecast")}</span>
        <span><i className="psp-trend__swatch psp-trend__swatch--capacity" />
          {capacityLabel ?? t("publicServiceTrend.currentCapacity")}: {Math.round(capacity).toLocaleString(locale)} {unit}
        </span>
        {infrastructureCapacity && <span><i className="psp-trend__swatch psp-trend__swatch--infrastructure" />
          {infrastructureCapacity.label}: {Math.round(infrastructureCapacity.value).toLocaleString(locale)} {unit}
        </span>}
      </div>
      <div className="psp-trend__chart" role="img" aria-label={t("publicServiceTrend.chartLabel", { unit })}>
        <ResponsiveContainer width="100%" height="100%">
          <LineChart data={chartData} margin={{ top: remPx(1), right: remPx(1), bottom: remPx(0.5), left: 0 }}>
            <CartesianGrid stroke="var(--flat-raised-hover)" vertical={false} />
            <XAxis dataKey="years" type="number"
              domain={trend.sampleCount === 0 ? [0, trend.forecastYears] : ["dataMin", "dataMax"]}
              tickFormatter={(years: number) => timeFormat.format(years)}
              tick={{ fill: "var(--flat-text-label)", fontSize: "0.93rem" }} tickLine={false}
              axisLine={{ stroke: "var(--flat-raised-hover)" }}
              label={{ value: t("publicServiceTrend.yearsAxis"), position: "insideBottom", offset: -remPx(0.5), fill: "var(--flat-text-label)" }} />
            <YAxis width={remPx(5)} domain={[0, upperBound]} allowDataOverflow tickFormatter={(value: number) => compact.format(value)}
              tick={{ fill: "var(--flat-text-label)", fontSize: "0.93rem" }} tickLine={false} axisLine={false} />
            <Tooltip contentStyle={{ background: "var(--flat-surface)", border: "none", color: "var(--flat-text-body)", fontSize: "1rem" }}
              labelFormatter={(years) => formatRelativeTime(Number(years))}
              formatter={(value) => `${Math.round(Number(value)).toLocaleString(locale)} ${unit}`} />
            <ReferenceLine y={capacity} stroke="var(--flat-good)" strokeWidth={2} ifOverflow="hidden" />
            {infrastructureCapacity && <ReferenceLine y={infrastructureCapacity.value} stroke="var(--flat-text-label)" strokeWidth={2} strokeDasharray="2 3" ifOverflow="hidden" />}
            {trend.capacityReachedIn !== null && trend.capacityReachedIn > 0 && <ReferenceDot
              x={epochsToYears(trend.capacityReachedIn, simulationDays)} y={trend.capacityLimit} r={remPx(0.3)}
              fill="var(--flat-warn)" stroke="var(--flat-surface)" />}
            {trend.sampleCount > 0 && <ReferenceLine x={0} stroke="var(--flat-text-muted)"
              label={{ value: t("publicServiceTrend.current"), position: "insideTopRight", fill: "var(--flat-text-label)" }} />}
            <Line dataKey="demand" type="linear" name={t("publicServicePrices.demand")} stroke="var(--flat-warn)" strokeWidth={2}
              dot={trend.sampleCount === 1} isAnimationActive={false} />
            <Line dataKey="production" type="linear" name={t("publicServicePrices.production")} stroke="var(--flat-accent)" strokeWidth={2}
              dot={trend.sampleCount === 1} isAnimationActive={false} />
            <Line dataKey="forecast" type="linear" name={t("publicServiceTrend.forecast")} stroke="var(--flat-warn)" strokeWidth={2}
              strokeDasharray="5 4" dot={false} isAnimationActive={false} />
          </LineChart>
        </ResponsiveContainer>
      </div>
    </div>
  );
}
