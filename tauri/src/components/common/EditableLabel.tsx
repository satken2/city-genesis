/**
 * 編集可能なトグル式ラベル
 *
 * 通常は文字列とえんぴつボタンを並べて表示し、ボタンを押すと入力欄+保存/キャンセルへ切り替わる。
 * Enterで保存、Escapeで取り消し。名称のインライン変更が必要な箇所で共用する。
 *
 * 保存処理そのもの(APIの選択・成功後の再取得)は呼び出し側の責務で、onSaveが例外を投げた場合は
 * 入力内容を保持したままエラー文言を出して編集を継続する(入力し直しのやり直しを避けるため)。
 */

import { useState } from "react";
import { useTranslation } from "react-i18next";
import editIcon from "../../assets/images/ui/edit.png";
import "./EditableLabel.css";

type EditableLabelProps = {
  /** 編集対象の生の値。入力欄の初期値になる */
  value: string;
  /** 表示テキスト。名称未設定時のID表記など、生の値と表示が異なる場合に渡す */
  displayText?: string;
  /** 表示テキストに付与する追加クラス(呼び出し元の見出しスタイルを当てる) */
  textClassName?: string;
  /** 空文字での保存を許可するかどうか(既定は不可) */
  allowEmpty?: boolean;
  /** 入力欄の最大文字数 */
  maxLength?: number;
  /** 保存処理。失敗時は例外を投げること(呼び出し元でのエラー表示は不要) */
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
    // 変更が無い場合と、空欄が許可されていない場合は送信せずに閉じる
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
          if (e.nativeEvent.isComposing) return; // IMEの変換確定のEnter・Escapeで保存・取消しない
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
