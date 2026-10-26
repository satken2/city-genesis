/**
 * Open the generic confirmation window (window_type="confirm_dialog") and await the user's answer. Do not use
 * `window.confirm`: tauri-plugin-dialog replaces it with a Promise-returning implementation, so `if
 * (!window.confirm(...))` always passes through without confirmation. Native dialogs also do not match the
 * game's appearance.
 *
 * The same window also asks for an amount (`requestAmount`) and reports a finished action with only an OK button
 * (`showNotice`).
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
  /** Entered amount, for confirmed requests with `amountMax`. */
  amount?: number;
};

export type ConfirmDialogRequest = {
  requestId: string;
  /** Translated body text. Preserve line breaks in display. */
  message: string;
  /** Translated confirmation button text. Defaults to OK. */
  confirmLabel?: string;
  /** "danger" makes the confirmation button red (irreversible or consequential actions). */
  tone?: "default" | "danger";
  /** Show only the confirmation button. */
  noticeOnly?: boolean;
  /** Ask for a whole amount from 1 to this value; confirmation is disabled until it is valid. */
  amountMax?: number;
};

export type ConfirmDialogOptions = Omit<ConfirmDialogRequest, "requestId"> & {
  /** i18n key for the title bar. Defaults to Confirmation. */
  titleKey?: string;
};

/** Return the confirmation window's answer (App.tsx handles closing via the close button). */
export async function emitConfirmDialogResult(requestId: string, confirmed: boolean, amount?: number) {
  await emit(CONFIRM_DIALOG_RESULT_EVENT, { requestId, confirmed, amount } satisfies ConfirmDialogResult);
}

async function openConfirmDialog({ titleKey, ...rest }: ConfirmDialogOptions, requestId: string) {
  const request: ConfirmDialogRequest = { ...rest, requestId };
  await invoke("open_registered_window", {
    windowType: "confirm_dialog",
    params: { extra: { titleKey, confirmRequest: request } },
  });
}

/**
 * Open confirmation and resolve with the answer: confirmed on confirmation, not confirmed on cancellation, close
 * button, or replacement by another confirmation. Rust-side closure, such as game-end bulk closing, leaves it
 * unresolved; the original action is no longer relevant then, so this causes no practical issue.
 */
async function awaitConfirmDialog(options: ConfirmDialogOptions): Promise<ConfirmDialogResult> {
  const requestId = crypto.randomUUID();

  let resolveResult!: (result: ConfirmDialogResult) => void;
  const result = new Promise<ConfirmDialogResult>((resolve) => {
    resolveResult = resolve;
  });
  const unlisten = await listen<ConfirmDialogResult>(CONFIRM_DIALOG_RESULT_EVENT, (event) => {
    if (event.payload.requestId !== requestId) return;
    unlisten();
    resolveResult(event.payload);
  });

  try {
    await openConfirmDialog(options, requestId);
  } catch (err) {
    console.error("Failed to open confirm dialog:", err);
    unlisten();
    return { requestId, confirmed: false };
  }
  return result;
}

/** Resolve true on confirmation and false otherwise. */
export async function requestConfirm(options: ConfirmDialogOptions): Promise<boolean> {
  return (await awaitConfirmDialog(options)).confirmed;
}

/** Resolve with the entered amount, or null when not confirmed. */
export async function requestAmount(
  options: ConfirmDialogOptions & { amountMax: number },
): Promise<number | null> {
  const result = await awaitConfirmDialog(options);
  return result.confirmed && result.amount !== undefined ? result.amount : null;
}

/** Show a message with only an OK button. Nothing waits for the acknowledgement. */
export async function showNotice(options: Omit<ConfirmDialogOptions, "noticeOnly" | "amountMax">) {
  try {
    await openConfirmDialog({ ...options, noticeOnly: true }, crypto.randomUUID());
  } catch (err) {
    console.error("Failed to open notice:", err);
  }
}
