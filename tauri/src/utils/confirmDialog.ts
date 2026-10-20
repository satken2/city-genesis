/**
 * Open the generic confirmation window (window_type="confirm_dialog") and await the user's answer. Do not use
 * `window.confirm`: tauri-plugin-dialog replaces it with a Promise-returning implementation, so `if
 * (!window.confirm(...))` always passes through without confirmation. Native dialogs also do not match the
 * game's appearance.
 *
 * The confirmation lives in another WebView; return answers through cross-window `CONFIRM_DIALOG_RESULT_EVENT`.
 * Assign requestId per request and accept only its first answer, since duplicate cancellations can arrive.
 */

import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";

export const CONFIRM_DIALOG_RESULT_EVENT = "confirm_dialog:result";

export type ConfirmDialogResult = {
  requestId: string;
  confirmed: boolean;
};

export type ConfirmDialogRequest = {
  requestId: string;
  /** Translated body text. Preserve line breaks in display. */
  message: string;
  /** Translated confirmation button text. Defaults to OK. */
  confirmLabel?: string;
  /** "danger" makes the confirmation button red (irreversible or consequential actions). */
  tone?: "default" | "danger";
};

export type ConfirmDialogOptions = Omit<ConfirmDialogRequest, "requestId"> & {
  /** i18n key for the title bar. Defaults to Confirmation. */
  titleKey?: string;
};

/** Return the confirmation window's answer (App.tsx handles closing via the close button). */
export async function emitConfirmDialogResult(requestId: string, confirmed: boolean) {
  await emit(CONFIRM_DIALOG_RESULT_EVENT, { requestId, confirmed } satisfies ConfirmDialogResult);
}

/**
 * Open confirmation; resolve true on confirmation, false on cancellation, close button, or replacement by
 * another confirmation. Rust-side closure, such as game-end bulk closing, leaves it unresolved; the original
 * action is no longer relevant then, so this causes no practical issue.
 */
export async function requestConfirm(options: ConfirmDialogOptions): Promise<boolean> {
  const { titleKey, ...rest } = options;
  const request: ConfirmDialogRequest = { ...rest, requestId: crypto.randomUUID() };

  let resolveResult!: (confirmed: boolean) => void;
  const result = new Promise<boolean>((resolve) => {
    resolveResult = resolve;
  });
  const unlisten = await listen<ConfirmDialogResult>(CONFIRM_DIALOG_RESULT_EVENT, (event) => {
    if (event.payload.requestId !== request.requestId) return;
    unlisten();
    resolveResult(event.payload.confirmed);
  });

  try {
    await invoke("open_registered_window", {
      windowType: "confirm_dialog",
      params: { extra: { titleKey, confirmRequest: request } },
    });
  } catch (err) {
    console.error("Failed to open confirm dialog:", err);
    unlisten();
    return false;
  }
  return result;
}
