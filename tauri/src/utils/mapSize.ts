import { resolveIntlLocale } from "./locale";

/**
 * マップの大きさ(m)をキロメートル単位で整形する(例: 7 × 6 km)。
 * 欠けている・不正な値なら null(呼び出し側で「不明」などに置き換える)。
 */
export function formatMapSize(width: unknown, height: unknown, language: string): string | null {
  if (typeof width !== "number" || typeof height !== "number" || !Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0) return null;
  const format = new Intl.NumberFormat(resolveIntlLocale(language), { maximumFractionDigits: 3 });
  return `${format.format(width / 1000)} × ${format.format(height / 1000)} km`;
}
