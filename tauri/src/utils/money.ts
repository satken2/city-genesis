/** 金額の整形と通貨単位を集約する。数量や人数には使わず、表示する桁数だけ呼び出し側で指定する。 */

import { resolveSupportedLanguage, type SupportedLanguage } from "./locale";

const DEFAULT_CURRENCY_UNITS: Record<SupportedLanguage, string> = {
  ja: "円",
  en: "$",
  "es-419": "$",
  "zh-Hans": "元",
  "zh-Hant": "元",
  fr: "€",
  ko: "₩",
};

/** null は言語に追従する。通貨設定は各 WebView で復元し、React 外の確認文も同じ値を参照する。 */
let currencyUnitOverride: string | null = null;
const currencyUnitListeners = new Set<() => void>();

export function defaultCurrencyUnit(language: string): string {
  return DEFAULT_CURRENCY_UNITS[resolveSupportedLanguage(language)];
}

export function getCurrencyUnitOverride(): string | null {
  return currencyUnitOverride;
}

export function getCurrencyUnit(language: string): string {
  return currencyUnitOverride ?? defaultCurrencyUnit(language);
}

export function setCurrencyUnit(unit: string | null | undefined): void {
  const next = typeof unit === "string" ? unit.trim() || null : null;
  if (next === currencyUnitOverride) return;
  currencyUnitOverride = next;
  currencyUnitListeners.forEach((listener) => listener());
}

export function subscribeCurrencyUnit(listener: () => void): () => void {
  currencyUnitListeners.add(listener);
  return () => { currencyUnitListeners.delete(listener); };
}

/** 日本語の万・億、英語の K・M・B など、ロケールに合わせて有効数字3桁で省略表示する。 */
export function formatCompactAmount(
  value: number,
  locale: string,
  unit = false
): string {
  return formatExactAmount(value, locale, unit, {
    notation: "compact",
    maximumSignificantDigits: 3,
  });
}

/** 桁区切り付きの金額を表示する。単価などで必要な小数桁も、この入口で整形する。 */
export function formatExactAmount(
  value: number,
  locale: string,
  unit = false,
  options?: Intl.NumberFormatOptions,
): string {
  if (!Number.isFinite(value)) {
    return "-";
  }
  const formatted = value.toLocaleString(locale, options);
  return unit ? `${formatted}${getCurrencyUnit(locale)}` : formatted;
}
