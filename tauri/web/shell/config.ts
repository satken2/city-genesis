/** ブラウザ版が接続するコアの URL。値は `web/.env` の VITE_CORE_API_URL(手元だけの変更は `web/.env.local`)。 */
export const CORE_API_URL: string = (import.meta.env.VITE_CORE_API_URL ?? "").replace(/\/+$/, "");

/** Rust の開発ビルド(`cfg!(all(debug_assertions, dev))`)に当たる。開発サーバーで動かしているときだけ真。 */
export const DEVELOPMENT: boolean = import.meta.env.DEV;

/** 各ウィンドウの iframe が読むページ。exe の各 WebView の `index.html` に当たる。 */
export const APP_PAGE = "app.html";

/** Unity の Web ビルドの置き場(ページからの相対)。中の `build.json` に読み込むファイルの名前がある。 */
export const GRAPHIC_BUILD_URL = "unity/";
