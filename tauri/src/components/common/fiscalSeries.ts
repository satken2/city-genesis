/**
 * 財政グラフ(予算ウィンドウの税収推移・収支バランス)の系列の色とラベル。
 */

import { LEDGER_RESERVE } from "../../constants";

/**
 * 税目の系列。キーは total_revenue の台帳(ledger)キー: 31=消費税,32=所得税,33=輸入関税,34=輸出関税,35=法人税。
 * ラベルは税率タブと共通の i18n キー(tax.*)。税以外の歳入(公営事業収入)は buildBusinessRevenueItems が組み立てる。
 */
export const TAX_REVENUE_META: Array<{ key: string; labelKey: string; color: string }> = [
  { key: "32", labelKey: "tax.incomeTax", color: "#4ea1ff" },      // 所得税: Blue
  { key: "35", labelKey: "tax.businessTax", color: "#f59e0b" },    // 法人税: Amber
  { key: "31", labelKey: "tax.consumptionTax", color: "#c084fc" }, // 消費税: Purple
  { key: "33", labelKey: "tax.importTax", color: "#ef4444" },      // 輸入関税: Red
  { key: "34", labelKey: "tax.exportTax", color: "#10b981" },      // 輸出関税: Emerald
];

/** 支出カテゴリの色。既存の予算バー(BudgetActualBarGraph)と同じ生成式で色を合わせる。 */
export function spendCategoryColor(catId: number): string {
  return `hsl(${(catId * 50) % 360}, 50%, 55%)`;
}

/** buildBusinessRevenueItems が受け取る翻訳関数の型(useTranslation の t で満たせる)。 */
type TranslateFn = (key: string, options?: Record<string, unknown>) => string;

/** 財政グラフの1系列 */
export type FiscalSeriesItem = {
  label: string;
  /** 金額(円) */
  value: number;
  color: string;
};

/**
 * 税外収入(公営事業収入)の系列を構築する。
 *
 * バックエンドの total_revenue には税(台帳キー31〜35)のほかに、公営施設の事業収益
 * (電気・水道料金などの公共サービス売上)と公営住宅・公共ビルの家賃収入が、その施設の
 * コストカテゴリキー(1〜7)で計上される(market_match / pay_rent が計上元)。
 * これを支出側と同じ予算カテゴリ粒度・同じ色(spendCategoryColor)で描くことで、
 * 「どのカテゴリの公営事業がどれだけ稼いでいるか」を支出と対比できる。
 * 値が0の項目も含めて全カテゴリ分を返す(描く側で落とす)。貯蓄はカテゴリではないので除く。
 */
export function buildBusinessRevenueItems(
  totalRevenue: Record<string, number>,
  budgetCategories: Record<number, { display_name: string }> | undefined,
  t: TranslateFn,
): FiscalSeriesItem[] {
  if (!budgetCategories) return [];
  return Object.entries(budgetCategories)
    .map(([id, entry]) => ({ id: Number(id), entry }))
    .filter(({ id }) => id !== LEDGER_RESERVE)
    .sort((a, b) => a.id - b.id)
    .map(({ id, entry }) => ({
      label: t("budget.categoryRevenue", {
        defaultValue: "{{category}}事業収入",
        category: t(`budget.category.${id}`, { defaultValue: entry.display_name }),
      }),
      value: totalRevenue[String(id)] ?? 0,
      color: spendCategoryColor(id),
    }));
}
