import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";

/**
 * Rust側のファイルログ(実行ファイルと同じ場所の city-genesis/tauri.log)へ
 * INFOレベルの1行を書き込む。
 *
 * 本番ビルドではWebViewのconsoleを確認する手段が乏しいため、失敗すると
 * 画面に何も出ないまま止まりうる重要フロー(エポック実行など)の進行記録に使う。
 * どのウィンドウからの出力か分かるよう、ウィンドウラベルを先頭に付ける。
 * 転送自体の失敗(ウィンドウ破棄中など)は握り潰す。
 */
export function logInfoToFile(message: string): void {
  const label = getCurrentWindow().label;
  void invoke("log_frontend_info", { message: `[${label}] ${message}` }).catch(() => {});
}
