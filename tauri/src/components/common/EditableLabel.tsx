/**
 * Toggle-editable label
 *
 * Normally displays text and a pencil button. Clicking switches to input plus save/cancel.
 * Enter saves; Escape cancels. Shared wherever inline renaming is needed.
 *
 * The caller owns saving (API selection and refetch after success). If onSave throws, retain the
 * input, show the error, and remain in editing mode so the player need not reenter the text.
 */

import { useState } from "react";
import { useTranslation } from "react-i18next";
import editIcon from "../../assets/images/ui/edit.png";
import "./EditableLabel.css";

type EditableLabelProps = {
  /** Raw editable value, used as the input's initial value. */
  value: string;
  /** Display text, supplied when it differs from the raw value (e.g. ID fallback for an unset name). */
  displayText?: string;
  /** Additional display-text class, for applying the caller's heading styles. */
  textClassName?: string;
  /** Whether saving an empty string is allowed (default false). */
  allowEmpty?: boolean;
  /** Maximum input length. */
  maxLength?: number;
  /** Save handler. Throw on failure; the caller need not display errors itself. */
  onSave: (next: string) => Promise<void> | void;
};

export function EditableLabel({
  value,
  displayText,
  textClassName,
  allowEmpty = false,
  maxLength,
  onSave,
}: EditableLabelProps) {
  const { t } = useTranslation();
  const [editing, setEditing] = useState(false);
  const [input, setInput] = useState("");
  const [saving, setSaving] = useState(false);
  const [failed, setFailed] = useState(false);

  const startEditing = () => {
    setInput(value);
    setFailed(false);
    setEditing(true);
  };

  const commit = async () => {
    if (saving) return;
    const next = input.trim();
    // Close without sending when unchanged or blank while empty values are disallowed.
    if (next === value || (!allowEmpty && next === "")) {
      setEditing(false);
      return;
    }
    setSaving(true);
    setFailed(false);
    try {
      await onSave(next);
      setEditing(false);
    } catch (err) {
      console.error("failed to save editable label:", err);
      setFailed(true);
    } finally {
      setSaving(false);
    }
  };

  if (!editing) {
    return (
      <span className="editable-label">
        <span className={["editable-label__text", textClassName].filter(Boolean).join(" ")}>
          {displayText ?? value}
        </span>
        <button
          className="editable-label__icon-btn"
          type="button"
          title={t("common.rename", "名称を変更")}
          onClick={startEditing}
        >
          <img className="editable-label__icon" src={editIcon} alt={t("common.rename", "名称を変更")} />
        </button>
      </span>
    );
  }

  return (
    <span className="editable-label">
      <input
        className="editable-label__input"
        type="text"
        value={input}
        maxLength={maxLength}
        autoFocus
        disabled={saving}
        onChange={(e) => setInput(e.target.value)}
        onKeyDown={(e) => {
          if (e.nativeEvent.isComposing) return; // Do not save/cancel for Enter/Escape used in IME composition.
          if (e.key === "Enter") void commit();
          else if (e.key === "Escape") setEditing(false);
        }}
      />
      <button
        className="editable-label__btn"
        type="button"
        disabled={saving}
        onClick={() => void commit()}
      >
        {saving ? t("common.saving") : t("common.save")}
      </button>
      <button
        className="editable-label__btn"
        type="button"
        disabled={saving}
        onClick={() => setEditing(false)}
      >
        {t("common.cancel")}
      </button>
      {failed && (
        <span className="editable-label__error">
          {t("common.renameFailed", "名称の変更に失敗しました")}
        </span>
      )}
    </span>
  );
}
