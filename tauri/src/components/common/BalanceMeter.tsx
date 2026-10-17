/**
 * 収支メーター
 *
 * 収支率((収入−支出)/収入。0で収支トントン)を、中央の点線から左右へ伸びるバーで示す。
 * 目盛りは左右の端が ±20% の固定で、範囲外の値は端で頭打ちにする。
 * 状態は枠の色で示し、バーは一律のグレーにする。左から伸びて塗り色で状態を示す Meter と
 * 見間違えないよう、角も丸めない。
 */

import "./BalanceMeter.css";

/** 左右の端に当たる収支率 */
const RANGE = 0.2;

/** 収支率に応じた枠の色。+5%超: 青 / ±5%以内: 緑 / −15%以上: 黄 / −15%未満: 赤 */
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
