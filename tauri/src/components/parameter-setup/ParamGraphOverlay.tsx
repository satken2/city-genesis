import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { LineChart, Line, XAxis, YAxis, Tooltip, ResponsiveContainer } from "recharts";
import { compileFormula, type FormulaVars } from "../../utils/formula";
import { useRemPx } from "../../hooks/useRemPx";
import "./ParameterSetupWindow.css";
import "./ParamGraphOverlay.css";

// シート general_params の graph_* / slider_* 列から届くグラフ編集の定義。
export type ParamGraph = {
  formula: string;
  x: { label: string; min: number; max: number };
  y: { label: string; min: number; max: number; scale: "linear" | "log" };
  slider: { min: number; max: number };
};

type ParamGraphOverlayProps = {
  paramKey: string;
  label: string;
  graph: ParamGraph;
  // 式が参照する全パラメータの現在値。paramKey 以外はこの値で固定して描く。
  vars: FormulaVars;
  initialValue: number;
  step: number;
  onApply: (value: number) => void;
  onClose: () => void;
};

const SAMPLE_COUNT = 200;

function formatTick(v: number): string {
  if (v !== 0 && Math.abs(v) < 1e-3) return v.toExponential(0);
  return String(Number(v.toPrecision(4)));
}

// 対数軸は10のべき乗だけに目盛りを打つ(recharts の自動目盛りは対数軸で桁が揃わない)。
function logTicks(min: number, max: number): number[] {
  const ticks: number[] = [];
  for (let k = Math.ceil(Math.log10(min)); k <= Math.floor(Math.log10(max)); k++) ticks.push(10 ** k);
  return ticks;
}

/**
 * 数値パラメータ1つをグラフを見ながら編集するオーバーレイ。
 * 動かせるのはこのパラメータだけで、式に出てくるほかのパラメータは現在値で固定する。
 * Y軸の範囲はシートで固定し、値を動かしたときに曲線の形の変化がそのまま見えるようにする。
 */
export function ParamGraphOverlay({
  paramKey,
  label,
  graph,
  vars,
  initialValue,
  step,
  onApply,
  onClose,
}: ParamGraphOverlayProps) {
  const { t } = useTranslation();
  const remPx = useRemPx();
  const [value, setValue] = useState(initialValue);
  const formula = useMemo(() => compileFormula(graph.formula), [graph.formula]);
  const isLog = graph.y.scale === "log";

  const data = useMemo(() => {
    const scope: FormulaVars = { ...vars, [paramKey]: value };
    return Array.from({ length: SAMPLE_COUNT }, (_, i) => {
      const x = graph.x.min + ((graph.x.max - graph.x.min) * i) / (SAMPLE_COUNT - 1);
      const y = formula({ ...scope, x });
      return { x, y: Number.isFinite(y) && (!isLog || y > 0) ? y : null };
    });
  }, [formula, vars, paramKey, value, graph.x.min, graph.x.max, isLog]);

  const xLabel = t(`paramSetup.param.${paramKey}.graphX`, { defaultValue: graph.x.label });
  const yLabel = t(`paramSetup.param.${paramKey}.graphY`, { defaultValue: graph.y.label });
  const axisTick = { fontSize: "0.93rem", fill: "#9cb3cb" };

  return (
    <div className="param-graph-overlay" onMouseDown={onClose}>
      <div
        className="param-graph-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="param-graph-title"
        onMouseDown={(e) => e.stopPropagation()}
      >
        <h2 id="param-graph-title" className="param-graph-title">
          {label}
        </h2>
        <div className="param-graph-axis-label">{yLabel}</div>
        <div className="param-graph-chart">
          <ResponsiveContainer>
            <LineChart data={data} margin={{ top: 8, right: 16, bottom: 0, left: 0 }}>
              <XAxis
                dataKey="x"
                type="number"
                domain={[graph.x.min, graph.x.max]}
                tickFormatter={formatTick}
                tick={axisTick}
                axisLine={{ stroke: "rgba(255,255,255,0.1)" }}
                tickLine={false}
              />
              <YAxis
                type="number"
                scale={isLog ? "log" : "linear"}
                domain={[graph.y.min, graph.y.max]}
                ticks={isLog ? logTicks(graph.y.min, graph.y.max) : undefined}
                allowDataOverflow
                tickFormatter={formatTick}
                tick={axisTick}
                axisLine={{ stroke: "rgba(255,255,255,0.1)" }}
                tickLine={false}
                width={remPx(4.17)}
              />
              <Tooltip
                isAnimationActive={false}
                content={({ payload }) => {
                  const point = payload?.[0]?.payload as { x: number; y: number | null } | undefined;
                  if (!point || point.y === null) return null;
                  return (
                    <div className="param-graph-tooltip">
                      {xLabel} {formatTick(point.x)} : {yLabel} {formatTick(point.y)}
                    </div>
                  );
                }}
              />
              <Line
                dataKey="y"
                stroke="#4ea1ff"
                strokeWidth={2}
                dot={false}
                activeDot={{ r: 4 }}
                isAnimationActive={false}
              />
            </LineChart>
          </ResponsiveContainer>
        </div>
        <div className="param-graph-axis-label param-graph-axis-label--x">{xLabel}</div>
        <div className="param-graph-control">
          <input
            className="param-graph-slider"
            type="range"
            min={graph.slider.min}
            max={graph.slider.max}
            step={step}
            value={value}
            onChange={(e) => setValue(parseFloat(e.target.value))}
          />
          <input
            className="param-setup-input"
            type="number"
            step={step}
            value={String(value)}
            onChange={(e) => {
              const n = parseFloat(e.target.value);
              if (!isNaN(n)) setValue(n);
            }}
          />
        </div>
        <div className="param-graph-buttons">
          <button className="param-setup-btn param-setup-btn--skip" onClick={onClose} type="button">
            {t("common.cancel")}
          </button>
          <button className="param-setup-btn param-setup-btn--start" onClick={() => onApply(value)} type="button">
            {t("common.ok")}
          </button>
        </div>
      </div>
    </div>
  );
}
