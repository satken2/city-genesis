/**
 * 汎用の確認ウィンドウ(window_type="confirm_dialog")を開き、ユーザーの回答を待つ。
 *
 * `window.confirm` は使わないこと。tauri-plugin-dialog を入れていると `window.confirm` が
 * Promise を返す実装に差し替わり、`if (!window.confirm(...))` が常に素通りして確認なしで
 * 実行されてしまう。また、ネイティブダイアログはゲームの見た目とも揃わない。
 *
 * 確認ウィンドウは別の WebView なので、回答はクロスウィンドウイベント
 * `CONFIRM_DIALOG_RESULT_EVENT` で呼び出し元へ返る。要求ごとに requestId を振り、
 * 自分の要求への最初の回答だけを採用する(同じ要求に取消が重ねて届くことがあるため)。
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
  /** 本文(翻訳済み)。改行はそのまま改行として表示する */
  message: string;
  /** 確定ボタンの文言(翻訳済み)。省略時は「OK」 */
  confirmLabel?: string;
  /** "danger" は確定ボタンを赤くする(取り消せない・犠牲を伴う操作) */
  tone?: "default" | "danger";
};

export type ConfirmDialogOptions = Omit<ConfirmDialogRequest, "requestId"> & {
  /** タイトルバーに出す i18n キー。省略時は「確認」 */
  titleKey?: string;
};

/** 確認ウィンドウ側(×閉じは App.tsx)から回答を返す。 */
export async function emitConfirmDialogResult(requestId: string, confirmed: boolean) {
  await emit(CONFIRM_DIALOG_RESULT_EVENT, { requestId, confirmed } satisfies ConfirmDialogResult);
}

/**
 * 確認ウィンドウを開き、確定なら true、キャンセル・×閉じ・別の確認に差し替えられた場合は
 * false で解決する。ゲーム終了時の一括クローズなど Rust 側から閉じられた場合は解決しないが、
 * その時点で呼び出し元の操作も意味を失っているので実害はない。
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
