/**
 * プロップのサムネイル画像(src/assets/images/props/{種別ID}.png)を解決するユーティリティ
 *
 * 画像は tools/render_prop_thumbnails.py が通常版FBXから生成してGit管理する。
 * import.meta.glob でビルド時に全画像をバンドルし、実行時は種別IDからURLを引くだけにする。
 * 自由配置パネルの種別タイルと、公共交通の車両選択(車両の自由配置別名の画像を外観見本に使う)が共有する。
 */

const thumbnails = import.meta.glob<string>("../assets/images/props/*.png", {
  eager: true,
  query: "?url",
  import: "default",
});

/**
 * プロップ種別IDのサムネイル画像URLを返す。画像が未生成の種別は undefined
 * (呼び出し側が名前付きの代替表示にする)。
 */
export function resolvePropThumbnail(id: number): string | undefined {
  return thumbnails[`../assets/images/props/${id}.png`];
}
