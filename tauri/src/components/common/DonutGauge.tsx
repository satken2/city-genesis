/**
 * ドーナツ型ゲージ
 *
 * 0.0〜1.0 の割合値を円環で示し、中央に百分率、下にラベルと補足を出す。
 * 利用率のように1つの割合を横並びで見比べる箇所に使う(内訳の比率を描く円グラフではない)。
 *
 * 色と超過表示の規則は Meter と揃える。polarity で色の良し悪しの向きを反転し(color で明示指定も可)、
 * 1.0 を超える値は円環を1周で頭打ちにしたうえで周囲を赤紫に光らせる。
 * 中央の百分率は頭打ちにせず実際の値を出す(超過の度合いを読めるようにするため)。
 */

import { getMeterColor, type MeterPolarity } from "./Meter";
import "./DonutGauge.css";

// viewBox 0 0 100 100 のユーザー単位。
const RING_RADIUS = 40;
const RING_WIDTH = 12;
const RING_CIRCUMFERENCE = 2 * Math.PI * RING_RADIUS;

type DonutGaugeProps = {
  /** 割合値。0未満は0に丸め、1超は1周で頭打ちにして超過表示にする。容量0での超過など非有限値は「—」表示 */
  value: number;
  /** 円環の下に出すラベル */
  label: string;
  /** 値の向き(既定 "positive")。Meter と同じ意味 */
  polarity?: MeterPolarity;
  /** 円環の色の明示指定。省略時は value と polarity から決める(getMeterColor) */
  color?: string;
  /** ラベルの下に小さく添える補足(実数など) */
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
        {/* 12時の位置から時計回りに伸ばすため、円環だけ反時計回りに90度戻す */}
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
