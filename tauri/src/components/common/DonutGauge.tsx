/**
 * Donut gauge
 *
 * Shows a 0.0-1.0 proportion as a ring, percentage in the center, and label/details below.
 * Used to compare individual ratios such as utilization side by side, rather than breakdown pie charts.
 *
 * Match Meter color/overflow rules. polarity reverses whether higher values are good (color may
 * override explicitly). Above 1.0, cap the ring at one revolution and surround it with a magenta glow.
 * The central percentage remains uncapped so the degree of overflow stays visible.
 */

import { getMeterColor, type MeterPolarity } from "./Meter";
import "./DonutGauge.css";

// User units in viewBox 0 0 100 100.
const RING_RADIUS = 40;
const RING_WIDTH = 12;
const RING_CIRCUMFERENCE = 2 * Math.PI * RING_RADIUS;

type DonutGaugeProps = {
  /** Proportion. Clamp negatives to 0; above 1, cap the ring and indicate overflow. Non-finite values, such as overflow with zero capacity, display "—". */
  value: number;
  /** Label beneath the ring. */
  label: string;
  /** Value polarity (default "positive"), with the same meaning as Meter. */
  polarity?: MeterPolarity;
  /** Explicit ring color. If omitted, derive from value and polarity (getMeterColor). */
  color?: string;
  /** Small supplementary text below the label (actual count, etc.). */
  detail?: string;
};

export function DonutGauge({ value, label, polarity = "positive", color, detail }: DonutGaugeProps) {
  const safeValue = Number.isNaN(value) ? 0 : value;
  const clampedValue = Math.max(0, Math.min(1, safeValue));
  const isOverflow = safeValue > 1;
  const ringColor = color ?? getMeterColor(clampedValue, polarity);
  const centerText = Number.isFinite(safeValue) ? `${Math.round(Math.max(0, safeValue) * 100)}%` : "—";

  return (
    <div className={isOverflow ? "donut-gauge donut-gauge--overflow" : "donut-gauge"}>
      <svg className="donut-gauge__chart" viewBox="0 0 100 100" role="img" aria-label={`${label} ${centerText}`}>
        {/* Rotate only the ring 90 degrees counterclockwise so it extends clockwise from 12 o'clock. */}
        <g className="donut-gauge__ring" transform="rotate(-90 50 50)" fill="none" strokeWidth={RING_WIDTH}>
          <circle className="donut-gauge__track" cx="50" cy="50" r={RING_RADIUS} />
          <circle
            className="donut-gauge__arc"
            cx="50"
            cy="50"
            r={RING_RADIUS}
            stroke={ringColor}
            strokeDasharray={`${RING_CIRCUMFERENCE * clampedValue} ${RING_CIRCUMFERENCE}`}
          />
        </g>
        <text className="donut-gauge__value" x="50" y="50">
          {centerText}
        </text>
      </svg>
      <span className="donut-gauge__label">{label}</span>
      {detail && <span className="donut-gauge__detail">{detail}</span>}
    </div>
  );
}
