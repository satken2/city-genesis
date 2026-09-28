/**
 * 汎用の確認ウィンドウ
 *
 * window_type="confirm_dialog" の小さなポップアップ。呼び出し側は直接開かず、
 * `utils/confirmDialog.ts` の `requestConfirm()` を使う(要求内容は `params.extra.confirmRequest`)。
 * 回答は `emitConfirmDialogResult` で呼び出し元のウィンドウへ返す。
 *
 * タイトルバーの×は App.tsx の closeWindow が取消として返す(このコンポーネントの外にあるため)。
 * ウィンドウは閉じても非表示で再利用され、表示中に次の確認要求が来ると props だけが差し替わる。
 * 差し替えられた古い要求には取消を返し、呼び出し元を待たせたままにしない。
 * 取消を effect の後始末(アンマウント時)で返してはいけない。開発時の StrictMode は表示直後に
 * 後始末を空打ちするため、ユーザーが答える前に取消が届き、呼び出し元が false で終わってしまう。
 */

import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";

import { emitConfirmDialogResult, type ConfirmDialogRequest } from "../../utils/confirmDialog";
import "./ConfirmDialogWindow.css";

type ConfirmDialogWindowProps = {
  request?: ConfirmDialogRequest;
  onClose: () => void;
};

export function ConfirmDialogWindow({ request, onClose }: ConfirmDialogWindowProps) {
  const { t } = useTranslation();
  const answeredRef = useRef(false);
  const currentRequestIdRef = useRef<string | undefined>(undefined);
  const cancelButtonRef = useRef<HTMLButtonElement>(null);
  const requestId = request?.requestId;

  useEffect(() => {
    if (!requestId || requestId === currentRequestIdRef.current) return;
    const replaced = currentRequestIdRef.current;
    if (replaced && !answeredRef.current) void emitConfirmDialogResult(replaced, false);
    currentRequestIdRef.current = requestId;
    answeredRef.current = false;
    // 誤ってEnterで確定しないよう、初期フォーカスは取消側に置く
    cancelButtonRef.current?.focus();
  }, [requestId]);

  const answer = async (confirmed: boolean) => {
    if (!requestId || answeredRef.current) return;
    answeredRef.current = true;
    await emitConfirmDialogResult(requestId, confirmed);
    onClose();
  };

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") void answer(false);
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  });

  if (!request) {
    return <p className="confirm-dialog-muted">{t("common.noData")}</p>;
  }

  return (
    <div className="confirm-dialog">
      <p className="confirm-dialog-message">{request.message}</p>
      <div className="confirm-dialog-actions">
        <button
          ref={cancelButtonRef}
          className="confirm-dialog-btn confirm-dialog-btn--cancel"
          type="button"
          onClick={() => void answer(false)}
        >
          {t("common.cancel")}
        </button>
        <button
          className={`confirm-dialog-btn confirm-dialog-btn--${request.tone ?? "default"}`}
          type="button"
          onClick={() => void answer(true)}
        >
          {request.confirmLabel ?? t("common.ok")}
        </button>
      </div>
    </div>
  );
}
