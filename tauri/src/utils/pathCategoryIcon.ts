/**
 * パス建設パネルのカテゴリ(=path_type)のアイコン画像を解決するユーティリティ。
 *
 * 画像は path_type の数値をファイル名とする `assets/images/path_categories/{path_type}.png`。
 * Vite の import.meta.glob で事前一括取得し、ランタイムではキー検索する。
 * 該当画像が無いカテゴリは空文字を返す（呼び出し側でアイコン枠を空表示にする）。
 * パスの一般アイコン(utils/pathIcon)へはフォールバックしない。カテゴリの画像は専用に用意する。
 */

const categoryModules = import.meta.glob<{ default: string }>(
  "../assets/images/path_categories/*.png",
  { eager: true }
);

/** カテゴリ(path_type)に対応するアイコン画像URLを返す。無ければ空文字。 */
export function resolvePathCategoryIcon(pathType: number): string {
  const key = `../assets/images/path_categories/${pathType}.png`;
  return categoryModules[key]?.default ?? "";
}
