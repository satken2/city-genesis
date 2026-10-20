/**
 * Income/expenditure balance meter
 *
 * Shows the balance ratio ((income-expenditure)/income, 0 means break-even) as a bar extending
 * left/right from a central dotted line. Fixed endpoints are ±20%; clamp values beyond them.
 * Status is indicated by the border color; the bar is always gray and corners are square
 * to distinguish it from Meter, which fills from the left and indicates status by fill color.
 */

import "./BalanceMeter.css";

/** Balance ratio corresponding to either endpoint. */
const RANGE = 0.2;

/** Border colors by balance ratio: above +5% blue / within ±5% green / at least -15% yellow / below -15% red. */
function getBorderColor(value: number): string {
  if (value > 0.05) return "#3b82f6";
  if (value >= -0.05) return "#22c55e";
  if (value >= -0.15) return "#eab308";
  return "#ef4444";
}

export function BalanceMeter({ value }: { value: number }) {
  const clamped = Math.max(-RANGE, Math.min(RANGE, value));
  const halfPercent = (Math.abs(clamped) / RANGE) * 50;

  return (
    <div className="balance-meter" style={{ borderColor: getBorderColor(value) }}>
      <div
        className="balance-meter__fill"
        style={{
          left: `${clamped < 0 ? 50 - halfPercent : 50}%`,
          width: `${halfPercent}%`,
        }}
      />
      <div className="balance-meter__center" />
    </div>
  );
}
