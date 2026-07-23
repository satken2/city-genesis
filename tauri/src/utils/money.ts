/**
 * 金額フォーマット用ユーティリティ
 *
 * 言語設定に応じて金額をコンパクト化して表示する。
 * 日本語は「52.5億」、英語は「5.25B」のように、桁数に応じて細かい数を切り捨てる。
 * Intl.NumberFormat の compact 表記が日本語（万/億/兆）・英語（K/M/B）の
 * 両方を標準でカバーするため、それを薄くラップしている。
 *
 * date.ts と同様、ロケール文字列を引数に取る純粋関数として実装する。
 */

/** 通貨単位の暫定プレースホルダ。ゲーム内通貨単位が確定したらここを差し替える。 */
export const PLACEHOLDER_CURRENCY_UNIT = "¢";

/**
 * 金額を有効3桁でコンパクト表記する（例: ja-JP → 52.5億 / 2.84万、en-US → 5.25B / 28.4K）。
 * unit が true のとき末尾に暫定通貨単位を付加する。
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
 * 金額を丸めずに3桁区切りで表記する（例: 5,250,437,891）。ツールチップ用。
 * unit が true のとき末尾に暫定通貨単位を付加する。
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
