/**
 * 汎用メーターコンポーネント
 *
 * 0.0〜1.0 の割合値を水平バーで可視化する。
 * 値の大きさに応じてバーの色が3段階に変化し、直感的な状態把握を可能にする。
 * 耐久度や利用率など、割合で表される指標の表示に使用する。
 */

import "./Meter.css";

type MeterProps = {
  /** 0.0〜1.0 の割合値。範囲外の値はクランプされる */
  value: number;
  /** メーターの左側に表示するラベル */
  label?: string;
  /**
   * 値の向き。"positive"（既定）は値が大きいほど良い指標（耐久度・充足度など）、
   * "negative"は値が大きいほど悪い指標（騒音・大気汚染・犯罪など）で、色の対応が反転する。
   * バーの長さはどちらも値そのものを表す。
   */
  polarity?: "positive" | "negative";
  /** 塗り色の明示指定。省略時は value から自動決定（getMeterColor） */
  color?: string;
};

/**
 * 値の大きさに応じたバーの表示色を返す
 * 0.7以上: 緑（良好）、0.4以上: 黄色（注意）、0.4未満: 赤（危険）
 */
function getMeterColor(value: number): string {
  if (value >= 0.7) return "#22c55e";
  if (value >= 0.4) return "#eab308";
  return "#ef4444";
}

export function Meter({ value, label, polarity = "positive", color }: MeterProps) {
  const clampedValue = Math.max(0, Math.min(1, value));
  const percentage = Math.round(clampedValue * 100);
  const fillColor =
    color ??
    getMeterColor(polarity === "negative" ? 1 - clampedValue : clampedValue);

  return (
    <div className="meter">
      {label && <span className="meter-label">{label}</span>}
      <div className="meter-bar">
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
