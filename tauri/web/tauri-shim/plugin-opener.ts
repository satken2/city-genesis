/** ブラウザ版の `@tauri-apps/plugin-opener`。既定のブラウザで開く代わりに新しいタブで開く。 */
export async function openUrl(url: string | URL): Promise<void> {
  window.open(String(url), "_blank", "noopener,noreferrer");
}
