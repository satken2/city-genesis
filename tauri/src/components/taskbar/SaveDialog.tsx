/**
 * セーブメニュー・セーブ一覧のウィンドウ内オーバーレイ(確認・名前の入力・失敗の表示)。
 *
 * 幕の下に元の画面を残したまま中央にボックスを重ねる。幕を押すと onDismiss(取消し)。
 * 処理中(busy)は幕でもEscでも閉じない。Escは呼び出し側がウィンドウ全体のキー操作とまとめて扱う。
 */
import type { FormEvent, ReactNode } from "react";
import "./SaveDialog.css";

type SaveDialogProps = {
  icon?: ReactNode;
  title: string;
  /** danger は取り返しのつかない操作(削除・終了)。見出しのアイコンを警告色にする */
  tone?: "normal" | "danger";
  busy?: boolean;
  onDismiss: () => void;
  /** 渡すとボックスを form にし、Enter で送信する */
  onSubmit?: () => void;
  children?: ReactNode;
  actions: ReactNode;
};

export function SaveDialog({ icon, title, tone = "normal", busy = false, onDismiss, onSubmit, children, actions }: SaveDialogProps) {
  const content = <>
    <h2 className="save-dialog-title">{icon}<span>{title}</span></h2>
    {children}
    <div className="save-dialog-actions">{actions}</div>
  </>;
  const className = `save-dialog save-dialog--${tone}`;
  const stop = (event: { stopPropagation: () => void }) => event.stopPropagation();
  return (
    <div className="save-dialog-veil" onMouseDown={busy ? undefined : onDismiss}>
      {onSubmit
        ? <form className={className} role="dialog" aria-modal="true" aria-label={title} onMouseDown={stop}
            onSubmit={(event: FormEvent) => { event.preventDefault(); if (!busy) onSubmit(); }}>{content}</form>
        : <div className={className} role="dialog" aria-modal="true" aria-label={title} onMouseDown={stop}>{content}</div>}
    </div>
  );
}

/** ダイアログのボタン。primary=主操作、danger=取り返しのつかない操作 */
export function SaveDialogButton({ kind = "secondary", busy = false, disabled, type = "button", onClick, children }: {
  kind?: "primary" | "danger" | "secondary"; busy?: boolean; disabled?: boolean; type?: "button" | "submit";
  onClick?: () => void; children: ReactNode;
}) {
  return (
    <button className={`save-dialog-button save-dialog-button--${kind}`} type={type} disabled={disabled || busy} onClick={onClick}>
      {busy && <span className="save-dialog-spinner" aria-hidden="true" />}
      {children}
    </button>
  );
}
