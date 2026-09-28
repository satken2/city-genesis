/**
 * データリストコンポーネント
 *
 * 数値の配列データを表示する。デフォルトでは最新値(末尾要素)のみを表示し、
 * クリックするとミニチャート付きポップアップが開き、全要素の推移を確認できる。
 * 収入・支出・稼働率などのリングバッファ履歴データに使用する。
 */

import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { BarChart, Bar, XAxis, Tooltip, ResponsiveContainer } from "recharts";
import "./DataList.css";

type DataListProps = {
  values: number[];
  /** 値のフォーマット関数。未指定の場合は数値をそのまま表示 */
  formatter?: (value: number) => string;
  /**
   * チャート描画値のクランプ範囲 [min, max]。
   * 利益率のように意味のある値域が決まっているデータで、履歴に含まれる
   * 外れ値（例: 過去セーブに残るクランプ前の異常値）がY軸スケールを
   * 支配して他の値が潰れるのを防ぐ。棒の高さだけをクランプし、
   * ツールチップと最新値表示には生値をそのまま使う。
   */
  clampRange?: [number, number];
};

export function DataList({ values, formatter, clampRange }: DataListProps) {
  const { t } = useTranslation();
  const containerRef = useRef<HTMLDivElement>(null);
  const [isOpen, setIsOpen] = useState(false);

  if (!values || values.length === 0) {
    return <span className="data-list-empty">{t("common.noData")}</span>;
  }

  const latestValue = values[0];
  const displayValue = formatter
    ? formatter(latestValue)
    : String(latestValue);

  // For chronological order (oldest on left, newest on right), we copy and reverse values
  const chronologicalValues = [...values].reverse();
  // value はチャートの棒の高さ（clampRange 指定時はクランプ済み）、raw はツールチップ用の生値
  const chartData = chronologicalValues.map((v, i) => ({
    index: i + 1,
    value: clampRange
      ? Math.min(Math.max(v, clampRange[0]), clampRange[1])
      : v,
    raw: v,
  }));

  return (
    <div className="data-list" ref={containerRef}>
      <button
        className="data-list-trigger"
        onClick={() => setIsOpen(!isOpen)}
        type="button"
      >
        <span>{displayValue}</span>
        <span className="data-list-clock-btn">
          <svg
            xmlns="http://www.w3.org/2000/svg"
            width="11"
            height="11"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2.5"
            strokeLinecap="round"
            strokeLinejoin="round"
          >
            <circle cx="12" cy="12" r="10" />
            <polyline points="12 6 12 12 16 14" />
          </svg>
        </span>
      </button>
      {isOpen && (
        <>
          <div
            className="data-list-overlay"
            onClick={() => setIsOpen(false)}
          />
          <div className="data-list-popup">
            <div className="data-list-popup-header">
              <span>{t("common.history")}</span>
              <button
                className="data-list-popup-close"
                onClick={() => setIsOpen(false)}
                type="button"
              >
                ×
              </button>
            </div>
            <ResponsiveContainer width="100%" height={120}>
              <BarChart data={chartData}>
                <XAxis
                  dataKey="index"
                  tick={{ fontSize: "0.93rem", fill: "#9cb3cb" }}
                  axisLine={{ stroke: "rgba(255,255,255,0.1)" }}
                  tickLine={false}
                />
                <Tooltip
                  content={({ payload }) => {
                    if (!payload || !payload.length) return null;
                    // クランプ済みの棒の高さではなく生値を表示する
                    const raw = payload[0]?.payload?.raw;
                    const display = formatter
                      ? formatter(Number(raw ?? 0))
                      : String(raw ?? "");
                    return (
                      <div className="data-list-tooltip">
                        {display}
                      </div>
                    );
                  }}
                />
                <Bar dataKey="value" fill="#4ea1ff" radius={[2, 2, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </>
      )}
    </div>
  );
}
