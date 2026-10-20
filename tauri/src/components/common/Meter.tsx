/**
 * Generic meter component
 *
 * Visualizes proportions from 0.0 to 1.0 as horizontal bars.
 * Three color levels communicate status intuitively. Used for ratios such as durability/utilization.
 *
 * Above 1.0 (capacity overflow, etc.), cap the bar at 100% without rescaling and surround it
 * with a magenta glow. Keeping the scale fixed preserves comparison with neighboring meters.
 * Callers can add numeric values when the degree of overflow should be visible.
 */

import "./Meter.css";

/**
 * Value polarity: "positive" means higher is better (durability, fulfillment, etc.);
 * "negative" means higher is worse (noise, air pollution, crime, etc.), reversing color mapping.
 */
export type MeterPolarity = "positive" | "negative";

type MeterProps = {
  /** Proportion from 0.0 to 1.0. Clamp negatives to 0; cap above 1 at 100% and indicate overflow. */
  value: number;
  /** Label to the left of the meter. */
  label?: string;
  /** Value polarity (default "positive"). Bar length represents the value itself for either polarity. */
  polarity?: MeterPolarity;
  /** Explicit fill color. If omitted, derive automatically from value (getMeterColor). */
  color?: string;
};

/**
 * Return the display color for a ratio clamped to 0-1. DonutGauge shares the rule.
 * Goodness (1-value for negative polarity): at least 0.7 = green (good),
 * at least 0.4 = yellow (caution), below 0.4 = red (danger).
 */
export function getMeterColor(value: number, polarity: MeterPolarity = "positive"): string {
  const goodness = polarity === "negative" ? 1 - value : value;
  if (goodness >= 0.7) return "#22c55e";
  if (goodness >= 0.4) return "#eab308";
  return "#ef4444";
}

export function Meter({ value, label, polarity = "positive", color }: MeterProps) {
  const clampedValue = Math.max(0, Math.min(1, value));
  const percentage = Math.round(clampedValue * 100);
  const fillColor = color ?? getMeterColor(clampedValue, polarity);
  const isOverflow = value > 1;

  return (
    <div className="meter">
      {label && <span className="meter-label">{label}</span>}
      <div className={isOverflow ? "meter-bar meter-bar--overflow" : "meter-bar"}>
        <div
          className="meter-fill"
          style={{
            width: `${percentage}%`,
            backgroundColor: fillColor,
          }}
        />
      </div>

    </div>
  );
}
