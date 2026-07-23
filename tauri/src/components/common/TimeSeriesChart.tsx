/**
 * 時系列チャートコンポーネント
 *
 * 数値配列を recharts の折れ線グラフで表示する。
 * 参考線(ReferenceLine)を1本まで描画でき、閾値や上限値の可視化に使用する。
 * 天然資源の消費量推移・残量推移など、エポック単位の履歴データに使用する。
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

type ReferenceLineConfig = {
  /** 基準線のY座標 */
  value: number;
  /** 基準線のラベル */
  label: string;
  /** 基準線の色 */
  color?: string;
};

export type TimeSeriesChartProps = {
  /** 表示する数値配列。各要素が1データポイントに対応 */
  data: number[];
  /** 折れ線の色 */
  color?: string;
  /** グラフの高さ(px) */
  height?: number;
  /** 基準線の設定。未指定の場合は基準線を描画しない */
  referenceLine?: ReferenceLineConfig;
  /** 値のフォーマット関数。未指定の場合は数値をそのまま表示 */
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
            tick={{ fontSize: 10, fill: "#9cb3cb" }}
            axisLine={{ stroke: "rgba(255,255,255,0.1)" }}
            tickLine={false}
          />
          <YAxis
            tick={{ fontSize: 10, fill: "#9cb3cb" }}
            axisLine={{ stroke: "rgba(255,255,255,0.1)" }}
            tickLine={false}
            width={45}
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
                fontSize: 11,
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
