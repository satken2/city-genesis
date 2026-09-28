/**
 * 建物建設パネルの分類(BD_CATEGORY_*)のアイコン画像を解決するユーティリティ。
 *
 * 画像は分類の数値IDをファイル名とする `assets/images/bd_categories/{id}.png`。
 * Vite の import.meta.glob で事前一括取得し、ランタイムではキー検索する。
 * 該当画像が無い分類は空文字を返す（呼び出し側でアイコン枠を空表示にする）。
 */

const categoryModules = import.meta.glob<{ default: string }>(
  "../assets/images/bd_categories/*.png",
  { eager: true }
);

/** 分類IDに対応するアイコン画像URLを返す。無ければ空文字。 */
export function resolveBdCategoryIcon(categoryId: number): string {
  const key = `../assets/images/bd_categories/${categoryId}.png`;
  return categoryModules[key]?.default ?? "";
}
