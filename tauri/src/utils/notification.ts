import { invoke } from "@tauri-apps/api/core";

/**
 * 通知ポップアップを表示する際のオプション。
 */
export interface ShowNotificationOptions {
  /** 通知本文。 */
  message: string;
  /**
   * アイコン画像のパス。
   * 未指定の場合はデフォルトの info アイコンが使用される。
   */
  iconPath?: string;
  /**
   * 自動非表示までの時間（ミリ秒）。
   * 未指定の場合はユーザーが×ボタンを押すまで表示され続ける。
   */
  durationMs?: number;
}

/**
 * 通知ポップアップを表示する。
 *
 * 既に通知が表示中の場合はキューに積まれ、現在の通知が終了した後に順番に表示される。
 * 内容が上書きされるのではなく、キューとして順次処理される。
 *
 * @param options 通知の表示オプション
 */
export function showNotification(options: ShowNotificationOptions): Promise<void> {
  return invoke("show_notification", {
    message: options.message,
    iconPath: options.iconPath,
    durationMs: options.durationMs,
  });
}

/**
 * 現在表示中の通知ポップアップを手動で閉じる。
 *
 * キューに次の通知が残っていれば、自動的に次が表示される。
 */
export function dismissNotification(): Promise<void> {
  return invoke("dismiss_notification");
}
