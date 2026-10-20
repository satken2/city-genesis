/**
 * Format achievement quest condition clauses for display. Shared by achievements and epoch results windows.
 */

/** Display symbols for comparison operators (e.g. replace >= with ≥). */
export function questCompareLabel(op: string): string {
  switch (op) {
    case ">=":
      return "≥";
    case "<=":
      return "≤";
    case "==":
      return "=";
    case "!=":
      return "≠";
    default:
      return op;
  }
}

/** Current/target clause values: grouped integers, up to two decimal places, or — when absent. */
export function formatQuestValue(v: number | null | undefined, locale: string): string {
  if (v === null || v === undefined) return "—";
  return Number.isInteger(v)
    ? v.toLocaleString(locale)
    : v.toLocaleString(locale, { maximumFractionDigits: 2 });
}
