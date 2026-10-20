/**
 * Time series chart component
 *
 * Displays numeric arrays as recharts line charts. Supports up to one ReferenceLine
 * for thresholds or upper limits. Used for per-epoch histories such as natural resource
 * consumption and remaining amounts.
 */

import { useTranslation } from "react-i18next";
import {
  LineChart,
  Line,
  XAxis,
  YAxis,
  Tooltip,
  ResponsiveContainer,
  ReferenceLine,
} from "recharts";
import "./TimeSeriesChart.css";
import { useRemPx } from "../../hooks/useRemPx";

type ReferenceLineConfig = {
  /** Reference line Y coordinate. */
  value: number;
  /** Reference line label. */
  label: string;
  /** Reference line color. */
  color?: string;
};

export type TimeSeriesChartProps = {
  /** Numeric array to display. Each element corresponds to one data point. */
  data: number[];
  /** Line color. */
  color?: string;
  /** Chart height (px). */
  height?: number;
  /** Reference line configuration. Omit to draw no reference line. */
  referenceLine?: ReferenceLineConfig;
  /** Value formatter. Display the number unchanged if omitted. */
  formatter?: (value: number) => string;
};

export function TimeSeriesChart({
  data,
  color = "#4ea1ff",
  height = 200,
  referenceLine,
  formatter,
}: TimeSeriesChartProps) {
  const { t } = useTranslation();
  const remPx = useRemPx();

  if (!data || data.length === 0) {
    return (
      <span className="time-series-chart-empty">{t("common.noData")}</span>
    );
  }

  const chartData = data.map((v, i) => ({
    index: i + 1,
    value: v,
  }));

  return (
    <div className="time-series-chart" style={{ width: "100%", height }}>
      <ResponsiveContainer>
        <LineChart data={chartData}>
          <XAxis
            dataKey="index"
            tick={{ fontSize: "0.93rem", fill: "#9cb3cb" }}
            axisLine={{ stroke: "rgba(255,255,255,0.1)" }}
            tickLine={false}
          />
          <YAxis
            tick={{ fontSize: "0.93rem", fill: "#9cb3cb" }}
            axisLine={{ stroke: "rgba(255,255,255,0.1)" }}
            tickLine={false}
            width={remPx(4.17)}
          />
          <Tooltip
            content={({ payload }) => {
              if (!payload || !payload.length) return null;
              const raw = payload[0]?.value;
              const display = formatter
                ? formatter(Number(raw ?? 0))
                : String(raw ?? "");
              return (
                <div className="time-series-chart-tooltip">{display}</div>
              );
            }}
          />
          {referenceLine && (
            <ReferenceLine
              y={referenceLine.value}
              stroke={referenceLine.color ?? "#f59e0b"}
              strokeDasharray="6 3"
              label={{
                value: referenceLine.label,
                position: "insideTopRight",
                fill: referenceLine.color ?? "#f59e0b",
                fontSize: "1rem",
              }}
            />
          )}
          <Line
            type="monotone"
            dataKey="value"
            stroke={color}
            strokeWidth={2}
            dot={false}
            activeDot={{ r: 4 }}
          />
        </LineChart>
      </ResponsiveContainer>
    </div>
  );
}
