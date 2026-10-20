import { invoke } from "@tauri-apps/api/core";

/**
 * Notification popup options.
 */
export interface ShowNotificationOptions {
  /** Notification body. */
  message: string;
  /**
   * Icon image path. Defaults to the info icon when omitted.
   */
  iconPath?: string;
  /**
   * Delay before automatic hiding in milliseconds. Defaults to five seconds.
   */
  durationMs?: number;
}

/**
 * Show a notification popup. If one is visible, enqueue and show in order after the current notification ends.
 * Process sequentially rather than overwriting content.
 *
 * @param options Notification display options.
 */
export function showNotification(options: ShowNotificationOptions): Promise<void> {
  return invoke("show_notification", {
    message: options.message,
    iconPath: options.iconPath,
    durationMs: options.durationMs,
  });
}

/**
 * Manually close the current notification popup. Automatically show the next queued notification if any.
 */
export function dismissNotification(): Promise<void> {
  return invoke("dismiss_notification");
}
