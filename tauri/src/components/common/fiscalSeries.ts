/**
 * Series colors and labels for fiscal charts (budget window tax revenue trends and income/expenditure balance).
 */

import { LEDGER_RESERVE } from "../../constants";

/**
 * Tax series. Keys are total_revenue ledger keys: 31=consumption tax, 32=income tax,
 * 33=import tariff, 34=export tariff, 35=corporate tax.
 * Labels share tax.* i18n keys with the tax tab. buildBusinessRevenueItems builds non-tax
 * (public enterprise) income.
 */
export const TAX_REVENUE_META: Array<{ key: string; labelKey: string; color: string }> = [
  { key: "32", labelKey: "tax.incomeTax", color: "#4ea1ff" },      // Income tax: Blue.
  { key: "35", labelKey: "tax.businessTax", color: "#f59e0b" },    // Corporate tax: Amber.
  { key: "31", labelKey: "tax.consumptionTax", color: "#c084fc" }, // Consumption tax: Purple.
  { key: "33", labelKey: "tax.importTax", color: "#ef4444" },      // Import tariff: Red.
  { key: "34", labelKey: "tax.exportTax", color: "#10b981" },      // Export tariff: Emerald.
];

/** Expense category colors, using the same generation formula as BudgetActualBarGraph to match existing budget bars. */
export function spendCategoryColor(catId: number): string {
  return `hsl(${(catId * 50) % 360}, 50%, 55%)`;
}

/** Translation function type accepted by buildBusinessRevenueItems (satisfied by useTranslation's t). */
type TranslateFn = (key: string, options?: Record<string, unknown>) => string;

/** One fiscal chart series. */
export type FiscalSeriesItem = {
  label: string;
  /** Amount (yen). */
  value: number;
  color: string;
};

/**
 * Build non-tax income (public enterprise income) series.
 *
 * Besides taxes (ledger keys 31-35), backend total_revenue records public facility earnings
 * (public service sales such as electricity/water charges) and public housing/building rent
 * under their facility cost category keys (1-7), recorded by market_match / pay_rent.
 * Use the same budget category granularity/colors (spendCategoryColor) as expenses so players
 * can compare each public enterprise category's earnings with spending.
 * Return all categories including zeros; rendering filters them. Exclude savings, which is not a category.
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
