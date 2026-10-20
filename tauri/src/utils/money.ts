/**
 * Money formatting utilities. Compact amounts by language, dropping finer digits according to magnitude
 * (Japanese: 52.5億; English: 5.25B). Thin wrapper over Intl.NumberFormat compact notation, which supports
 * Japanese 万/億/兆 and English K/M/B. Like date.ts, use pure functions accepting locale strings.
 */

/** Temporary currency unit placeholder. Replace here when the in-game currency unit is finalized. */
export const PLACEHOLDER_CURRENCY_UNIT = "¢";

/**
 * Format money compactly with three significant digits (ja-JP: 52.5億 / 2.84万; en-US: 5.25B / 28.4K). Append the
 * temporary currency unit when unit is true.
 */
export function formatCompactAmount(
  value: number,
  locale: string,
  unit = false
): string {
  if (!Number.isFinite(value)) {
    return "-";
  }
  const formatted = new Intl.NumberFormat(locale, {
    notation: "compact",
    maximumSignificantDigits: 3,
  }).format(value);
  return unit ? `${formatted}${PLACEHOLDER_CURRENCY_UNIT}` : formatted;
}

/**
 * Format unrounded money with three-digit grouping (e.g. 5,250,437,891) for tooltips. Append the temporary
 * currency unit when unit is true.
 */
export function formatExactAmount(
  value: number,
  locale: string,
  unit = false
): string {
  if (!Number.isFinite(value)) {
    return "-";
  }
  const formatted = value.toLocaleString(locale);
  return unit ? `${formatted}${PLACEHOLDER_CURRENCY_UNIT}` : formatted;
}
