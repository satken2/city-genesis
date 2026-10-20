import { resolveIntlLocale } from "./locale";

/**
 * Format map dimensions from m to km (e.g. 7 × 6 km). Return null for missing/invalid values so callers can
 * display Unknown, etc.
 */
export function formatMapSize(width: unknown, height: unknown, language: string): string | null {
  if (typeof width !== "number" || typeof height !== "number" || !Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0) return null;
  const format = new Intl.NumberFormat(resolveIntlLocale(language), { maximumFractionDigits: 3 });
  return `${format.format(width / 1000)} × ${format.format(height / 1000)} km`;
}
