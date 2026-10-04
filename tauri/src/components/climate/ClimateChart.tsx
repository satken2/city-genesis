import { useState, type PointerEvent } from "react";
import { useTranslation } from "react-i18next";
import type { ClimateMonth } from "../../utils/climate";

export type ClimateMetric = "temperature" | "precipitation" | "sunlight";
export const CLIMATE_COLORS: Record<ClimateMetric, string> = {
  temperature: "#ff8585", precipitation: "#62adf2", sunlight: "#eac45d",
};
const WIDTH = 720, HEIGHT = 340, LEFT = 48, RIGHT = 112, TOP = 28, BOTTOM = 40;
const PLOT_HEIGHT = HEIGHT - TOP - BOTTOM;
type Domain = Record<ClimateMetric, [number, number]>;

function domains(months: ClimateMonth[]): Domain {
  return {
    temperature: [Math.floor((Math.min(...months.map(month => month.temperature)) - 5) / 10) * 10,
      Math.ceil((Math.max(...months.map(month => month.temperature)) + 5) / 10) * 10],
    precipitation: [0, Math.max(100, Math.ceil(Math.max(...months.map(month => month.precipitation)) / 100) * 100)],
    sunlight: [0, 100],
  };
}

/** 編集中は縦軸を固定し、同じマウス位置が同じ値を指すようにする。 */
export function ClimateChart({ months, metric, selectedMonth, disabled, onSelect, onChange }: {
  months: ClimateMonth[]; metric: ClimateMetric; selectedMonth: number; disabled: boolean;
  onSelect: (month: number) => void; onChange: (month: number, metric: ClimateMetric, value: number) => void;
}) {
  const { t } = useTranslation();
  const [drag, setDrag] = useState<{ month: number; domain: Domain } | null>(null);
  const domain = drag?.domain ?? domains(months);
  const step = (WIDTH - LEFT - RIGHT) / 12;
  const x = (month: number) => LEFT + step * (month + 0.5);
  const y = (value: number, series: ClimateMetric) => TOP + PLOT_HEIGHT * (1 - (value - domain[series][0]) / (domain[series][1] - domain[series][0]));
  function change(month: number, value: number) {
    if (!Number.isFinite(value)) return;
    const bounded = metric === "temperature" ? value : Math.max(0, metric === "sunlight" ? Math.min(100, value) : value);
    onChange(month, metric, Math.round(bounded * (metric === "temperature" ? 10 : 1)) / (metric === "temperature" ? 10 : 1));
  }
  function move(event: PointerEvent<SVGSVGElement>) {
    if (!drag || disabled) return;
    const rect = event.currentTarget.getBoundingClientRect();
    const position = Math.max(0, Math.min(1, ((event.clientY - rect.top) * HEIGHT / rect.height - TOP) / PLOT_HEIGHT));
    const [min, max] = drag.domain[metric];
    change(drag.month, max - position * (max - min));
  }
  return <svg className="climate-chart" viewBox={`0 0 ${WIDTH} ${HEIGHT}`} aria-label={t("climateSettings.chart")}
    onPointerMove={move} onPointerUp={() => setDrag(null)} onPointerCancel={() => setDrag(null)}>
    <rect x={x(selectedMonth) - step / 2} y={TOP} width={step} height={PLOT_HEIGHT} className="climate-chart-selection" />
    {Array.from({ length: 6 }, (_, index) => {
      const fraction = index / 5, position = TOP + PLOT_HEIGHT * (1 - fraction);
      return <g key={index} className="climate-chart-axis">
        <line x1={LEFT} x2={WIDTH - RIGHT} y1={position} y2={position} />
        <text x={LEFT - 8} y={position + 4} textAnchor="end" fill={CLIMATE_COLORS.temperature}>
          {Math.round(domain.temperature[0] + fraction * (domain.temperature[1] - domain.temperature[0]))}</text>
        <text x={WIDTH - RIGHT + 8} y={position + 4} fill={CLIMATE_COLORS.precipitation}>{Math.round(domain.precipitation[1] * fraction)}</text>
        <text x={WIDTH - 16} y={position + 4} textAnchor="end" fill={CLIMATE_COLORS.sunlight}>{Math.round(fraction * 100)}</text>
      </g>;
    })}
    <g className="climate-chart-unit">
      <text x={LEFT - 8} y={16} textAnchor="end" fill={CLIMATE_COLORS.temperature}>°C</text>
      <text x={WIDTH - RIGHT + 8} y={16} fill={CLIMATE_COLORS.precipitation}>mm</text>
      <text x={WIDTH - 16} y={16} textAnchor="end" fill={CLIMATE_COLORS.sunlight}>%</text>
    </g>
    {months.map((month, index) => <g key={index}>
      <rect x={x(index) - step * 0.3} y={y(month.precipitation, "precipitation")} width={step * 0.6}
        height={TOP + PLOT_HEIGHT - y(month.precipitation, "precipitation")} fill={CLIMATE_COLORS.precipitation} opacity="0.5" />
      <line x1={x(index)} x2={x(index)} y1={TOP + PLOT_HEIGHT} y2={y(month.sunlight, "sunlight")}
        stroke={CLIMATE_COLORS.sunlight} strokeWidth="3" />
      <circle cx={x(index)} cy={y(month.sunlight, "sunlight")} r="3" fill={CLIMATE_COLORS.sunlight} />
      <text x={x(index)} y={HEIGHT - 12} textAnchor="middle" className="climate-chart-month">{index + 1}</text>
    </g>)}
    <polyline points={months.map((month, index) => `${x(index)},${y(month.temperature, "temperature")}`).join(" ")}
      fill="none" stroke={CLIMATE_COLORS.temperature} strokeWidth="2" />
    {months.map((month, index) => <circle key={index} cx={x(index)} cy={y(month.temperature, "temperature")}
      r="3" fill={CLIMATE_COLORS.temperature} />)}
    {months.map((month, index) => <circle key={index} cx={x(index)} cy={y(month[metric], metric)} r="8"
      className="climate-chart-handle" fill={CLIMATE_COLORS[metric]} tabIndex={disabled ? -1 : 0} role="slider"
      aria-label={`${t("climateSettings.month", { month: index + 1 })} ${t(`weather.${metric}`)}`}
      aria-valuenow={month[metric]} aria-valuemin={domain[metric][0]} aria-valuemax={domain[metric][1]}
      onPointerDown={event => {
        if (disabled || event.button !== 0) return;
        event.preventDefault(); onSelect(index); setDrag({ month: index, domain });
        event.currentTarget.ownerSVGElement?.setPointerCapture(event.pointerId);
      }}
      onKeyDown={event => {
        if (disabled || !["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"].includes(event.key)) return;
        event.preventDefault(); onSelect(index);
        change(index, month[metric] + (["ArrowUp", "ArrowRight"].includes(event.key) ? 1 : -1) * (metric === "precipitation" ? 5 : 1));
      }} />)}
  </svg>;
}
