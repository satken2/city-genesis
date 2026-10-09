/**
 * カテゴリ別税収 折れ線グラフ
 *
 * 税収の時系列推移を「合計＋5カテゴリ(消費税/所得税/輸入関税/輸出関税/法人税)」の
 * 多系列折れ線で表示する。データ源は GET /api/v1/city/budgets の history
 * (サーバ側で蓄積された過去エポックの税収実績。台帳キー31〜35)。
 *
 * 単系列専用の TimeSeriesChart とは別に、recharts を直接使う多系列チャートとして実装する。
 * 配色・ラベルは財政Sankeyと共通の TAX_REVENUE_META を再利用し、両表示で色を統一する。
 * 分析画面(data.py)の税収グラフと同じ「合計＋5カテゴリ」構成。
 */

import { useTranslation } from "react-i18next";
import {
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ResponsiveContainer,
} from "recharts";
import { TAX_REVENUE_META } from "../common/FiscalSankey";
import { formatCompactAmount, formatExactAmount } from "../../utils/money";
import { resolveIntlLocale } from "../../utils/locale";
import { useRemPx } from "../../hooks/useRemPx";

/** history 1エポック分。revenues は台帳キー(文字列)→実績額。 */
export type TaxRevenueHistoryEntry = {
  epoch: number;
  revenues: Record<string, number>;
};

export type TaxRevenueChartProps = {
  data: TaxRevenueHistoryEntry[];
};

/** 合計線の色。カテゴリ色(TAX_REVENUE_META)と重ならない中立色。 */
const COLOR_TOTAL = "#e2e8f0";

const AXIS_TICK = { fontSize: "0.93rem", fill: "#9cb3cb" } as const;
const TOOLTIP_STYLE = {
  background: "rgba(15, 23, 42, 0.95)",
  border: "1px solid rgba(148, 163, 184, 0.2)",
  borderRadius: "6px",
  fontSize: "1rem",
} as const;

export function TaxRevenueChart({ data }: TaxRevenueChartProps) {
  const { t, i18n } = useTranslation();
  const remPx = useRemPx();
  const locale = resolveIntlLocale(i18n.language);

  if (!data || data.length === 0) {
    return <div className="tax-window__chart-empty">{t("common.noData")}</div>;
  }

  // 各エポックを { epoch, total, "31":..., ... } に整形。total は5カテゴリの合計。
  const chartData = data.map((entry) => {
    const revenues = entry.revenues ?? {};
    const row: Record<string, number> = { epoch: entry.epoch };
    let total = 0;
    for (const meta of TAX_REVENUE_META) {
      const v = Number(revenues[meta.key] ?? 0);
      const safe = Number.isFinite(v) ? v : 0;
      row[meta.key] = safe;
      total += safe;
    }
    row.total = total;
    return row;
  });

  return (
    <div className="tax-window__chart-body">
      <ResponsiveContainer width="100%" height="100%">
        <LineChart data={chartData} margin={{ top: 8, right: 12, bottom: 4, left: 4 }}>
          <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
          <XAxis
            dataKey="epoch"
            tick={AXIS_TICK}
            tickLine={false}
            axisLine={{ stroke: "#253246" }}
          />
          <YAxis
            width={remPx(4.44)}
            tick={AXIS_TICK}
            tickLine={false}
            axisLine={{ stroke: "#253246" }}
            domain={["auto", "auto"]}
            tickFormatter={(v: number) => formatCompactAmount(v, locale)}
          />
          <Tooltip
            contentStyle={TOOLTIP_STYLE}
            labelFormatter={(label) => `Epoch ${label}`}
            formatter={(value: unknown) => formatExactAmount(Number(value), locale, true)}
          />
          <Legend wrapperStyle={{ fontSize: "0.93rem" }} iconType="plainline" />
          {/* 合計線を先頭に描画。参考値として太めの実線。 */}
          <Line
            type="monotone"
            dataKey="total"
            name={t("tax.revenueTotal", { defaultValue: "合計" })}
            stroke={COLOR_TOTAL}
            strokeWidth={2.5}
            dot={false}
            activeDot={{ r: 4 }}
            isAnimationActive={false}
          />
          {TAX_REVENUE_META.map((meta) => (
            <Line
              key={meta.key}
              type="monotone"
              dataKey={meta.key}
              name={t(meta.labelKey)}
              stroke={meta.color}
              strokeWidth={2}
              dot={false}
              activeDot={{ r: 4 }}
              isAnimationActive={false}
            />
          ))}
        </LineChart>
      </ResponsiveContainer>
    </div>
  );
}
