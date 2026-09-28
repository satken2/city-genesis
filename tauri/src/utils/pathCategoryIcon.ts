/**
 * パス建設パネルのカテゴリ(=path_type)のアイコン画像を解決するユーティリティ。
 *
 * 画像は path_type の数値をファイル名とする `assets/images/paths/path_{path_type}.png`。
 * Vite の import.meta.glob で事前一括取得し、ランタイムではキー検索する。
 * 該当画像が無いカテゴリは枠だけを空表示とする仕様のため、一般アイコン(utils/pathIcon)には
 * フォールバックせず空文字を返す。
 */

const pathModules = import.meta.glob<{ default: string }>(
  "../assets/images/paths/*.png",
  { eager: true }
);

/** カテゴリ(path_type)に対応するアイコン画像URLを返す。無ければ空文字。 */
export function resolvePathCategoryIcon(pathType: number): string {
  const keyWithPrefix = `../assets/images/paths/path_${pathType}.png`;
  const keyWithoutPrefix = `../assets/images/paths/${pathType}.png`;
  return pathModules[keyWithPrefix]?.default ?? pathModules[keyWithoutPrefix]?.default ?? "";
}
