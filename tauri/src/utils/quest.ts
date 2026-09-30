/**
 * 達成目標(クエスト)の条件節の表示用の整形。達成目標ウィンドウとエポック結果ウィンドウで共用する。
 */

/** 比較演算子の表示記号(>= は ≥ のように数学記号へ置き換える) */
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

/** 条件節の現在値・目標値。整数は桁区切り、小数は2桁まで。値が無ければ「—」 */
export function formatQuestValue(v: number | null | undefined, locale: string): string {
  if (v === null || v === undefined) return "—";
  return Number.isInteger(v)
    ? v.toLocaleString(locale)
    : v.toLocaleString(locale, { maximumFractionDigits: 2 });
}
