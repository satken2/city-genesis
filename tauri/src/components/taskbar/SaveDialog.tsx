/**
 * In-window overlays for save menus/lists (confirmation, name input, failure display).
 *
 * Keep the original screen beneath a backdrop and centered box. Clicking the backdrop calls onDismiss (Cancel).
 * While busy, neither backdrop nor Esc closes it. The caller handles Esc alongside whole-window keys.
 * SaveDialogButton is also used for epoch confirmation.
 */
import type { FormEvent, ReactNode } from "react";
import "./SaveDialog.css";

type SaveDialogProps = {
  icon?: ReactNode;
  title: string;
  /** danger indicates irreversible actions (delete/exit), coloring the heading icon as a warning. */
  tone?: "normal" | "danger";
  busy?: boolean;
  onDismiss: () => void;
  /** When supplied, render the box as a form and submit with Enter. */
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

/** Dialog button: primary = main action; danger = irreversible action. */
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
