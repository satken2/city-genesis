/**
 * Generic confirmation window
 *
 * Small window_type="confirm_dialog" popup. Callers use `requestConfirm()` in
 * `utils/confirmDialog.ts`, rather than opening it directly (request in `params.extra.confirmRequest`).
 * Return the answer to the caller window through `emitConfirmDialogResult`.
 *
 * The title-bar close button is handled as cancellation by closeWindow in App.tsx, outside this component.
 * The closed window remains hidden for reuse; a new request while open only replaces props.
 * Cancel the replaced request so its caller is not left waiting.
 * Do not return cancellation in effect cleanup (unmount): development StrictMode runs cleanup
 * immediately after display, which would send cancellation before the player answers and finish
 * with false in the caller.
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
    // Initially focus Cancel to prevent accidental confirmation with Enter.
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
