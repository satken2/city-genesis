/**
 * 汎用メーターコンポーネント
 *
 * 0.0〜1.0 の割合値を水平バーで可視化する。
 * 値の大きさに応じてバーの色が3段階に変化し、直感的な状態把握を可能にする。
 * 耐久度や利用率など、割合で表される指標の表示に使用する。
 *
 * 1.0 を超える値(容量超過など)は、目盛りを変えずにバーを100%で頭打ちにしたうえで、
 * バーの周囲を赤紫に光らせて超過を示す。目盛りを伸縮させないのは、並べた他のメーターとの
 * 見比べを崩さないため。超過の度合いまで見せたい画面は、呼び出し側で数値を併記する。
 */

import "./Meter.css";

/**
 * 値の向き。"positive" は値が大きいほど良い指標（耐久度・充足度など）、
 * "negative" は値が大きいほど悪い指標（騒音・大気汚染・犯罪など）で、色の対応が反転する。
 */
export type MeterPolarity = "positive" | "negative";

type MeterProps = {
  /** 0.0〜1.0 の割合値。0未満は0に丸め、1超は100%で頭打ちにして超過表示にする */
  value: number;
  /** メーターの左側に表示するラベル */
  label?: string;
  /** 値の向き(既定 "positive")。バーの長さはどちらの向きでも値そのものを表す */
  polarity?: MeterPolarity;
  /** 塗り色の明示指定。省略時は value から自動決定（getMeterColor） */
  color?: string;
};

/**
 * 0〜1 に丸めた割合値の表示色を返す。DonutGauge も同じ配色規則を使う。
 * 良さ(negative なら 1-値)が 0.7以上: 緑（良好）、0.4以上: 黄色（注意）、0.4未満: 赤（危険）
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
