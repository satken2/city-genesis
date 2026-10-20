/**
 * ブラウザ版の `@tauri-apps/plugin-dialog`。
 * 選んだパスはコアが読み書きするが、ブラウザにはコアの PC のファイルを選ぶ手段が無い。
 * OS のダイアログが開けなかったときと同じく失敗を返す。
 */
export async function open(_options?: unknown): Promise<never> {
  throw new Error("ファイルダイアログはブラウザ版では使えません");
}

export async function save(_options?: unknown): Promise<never> {
  throw new Error("ファイルダイアログはブラウザ版では使えません");
}
