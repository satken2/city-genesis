/**
 * 予算カテゴリ（LEDGER_*）のアイコン画像を解決するユーティリティ。
 *
 * 画像はカテゴリの数値IDをファイル名とする `assets/images/ledger/{id}.png`。
 * Vite の import.meta.glob で事前一括取得し、ランタイムではキー検索する。
 * 該当画像が無いカテゴリは空文字を返す（呼び出し側でアイコン枠を空表示にする）。
 */

const ledgerModules = import.meta.glob<{ default: string }>(
  "../assets/images/ledger/*.png",
  { eager: true }
);

/** 予算カテゴリIDに対応するアイコン画像URLを返す。無ければ空文字。 */
export function resolveLedgerIcon(categoryId: number): string {
  const key = `../assets/images/ledger/${categoryId}.png`;
  return ledgerModules[key]?.default ?? "";
}
