/**
 * Data list component
 *
 * Displays a numeric array. By default, shows only its latest value (last element);
 * clicking opens a popup with a mini chart of all values over time.
 * Used for ring-buffer histories such as income, expenditure, and utilization.
 */

import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { BarChart, Bar, XAxis, Tooltip, ResponsiveContainer } from "recharts";
import "./DataList.css";

type DataListProps = {
  values: number[];
  /** Value formatter. Display the number unchanged if omitted. */
  formatter?: (value: number) => string;
  /**
 * Clamp range [min, max] for plotted values.
 * For data with a meaningful domain, such as profit margins, prevents historical outliers
 * (e.g. unclamped anomalies in old saves) from dominating the Y axis and flattening other values.
 * Clamp only bar heights; tooltip and latest-value displays retain raw values.
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
  // value is chart bar height (clamped if clampRange is supplied); raw is the unmodified tooltip value.
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
                    // Display the raw value rather than the clamped bar height.
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
