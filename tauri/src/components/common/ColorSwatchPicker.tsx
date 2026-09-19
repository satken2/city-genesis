/**
 * 色見本ピッカー
 *
 * プリセットの丸い色見本の列と、"#rrggbb" のカラーコード入力欄を組み合わせた小型の色選択部品。
 * OS のカラーダイアログ(input type="color")は最前面固定の透明ウィンドウの裏に開いて操作できない
 * ことがあるため使わず、パネル内で完結する UI にしている。
 *
 * 値は常に "#rrggbb"(小文字)で受け渡す。入力欄は "#" 省略・大文字も受け付けて正規化し、
 * 書式が正しいときだけ onChange を呼ぶ(不正な途中入力は欄内に留まり、フォーカスを外すと
 * 現在値へ戻る)。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import "./ColorSwatchPicker.css";

type ColorSwatchPickerProps = {
  /** 現在の色("#rrggbb") */
  value: string;
  onChange: (hex: string) => void;
  /** 色見本として並べるプリセット("#rrggbb" の配列) */
  presets: readonly string[];
  disabled?: boolean;
};

/** 入力文字列を "#rrggbb"(小文字)へ正規化する。書式違反は null */
export function normalizeHexColor(raw: string): string | null {
  const text = raw.trim().replace(/^#/, "");
  if (!/^[0-9a-fA-F]{6}$/.test(text)) return null;
  return `#${text.toLowerCase()}`;
}

export function ColorSwatchPicker({ value, onChange, presets, disabled = false }: ColorSwatchPickerProps) {
  const { t } = useTranslation();
  const [draft, setDraft] = useState(value);
  const [focused, setFocused] = useState(false);

  // 外部から値が変わったら(保存後の再取得・元に戻す)、編集中でない限り入力欄も追従させる。
  useEffect(() => {
    if (!focused) setDraft(value);
  }, [value, focused]);

  const draftValid = normalizeHexColor(draft) !== null;

  const commit = () => {
    setFocused(false);
    const normalized = normalizeHexColor(draft);
    if (normalized === null) {
      setDraft(value);
      return;
    }
    setDraft(normalized);
    if (normalized !== value) onChange(normalized);
  };

  return (
    <div className={`color-swatch-picker${disabled ? " color-swatch-picker--disabled" : ""}`}>
      <div className="color-swatch-picker__swatches" role="listbox" aria-label={t("common.colorPresets")}>
        {presets.map((hex) => {
          const selected = hex.toLowerCase() === value.toLowerCase();
          return (
            <button
              key={hex}
              type="button"
              role="option"
              aria-selected={selected}
              className={`color-swatch-picker__swatch${selected ? " color-swatch-picker__swatch--selected" : ""}`}
              style={{ background: hex }}
              title={hex}
              disabled={disabled}
              onClick={() => onChange(hex.toLowerCase())}
            />
          );
        })}
      </div>
      <label className="color-swatch-picker__hex">
        <span
          className="color-swatch-picker__preview"
          style={{ background: draftValid ? normalizeHexColor(draft) ?? value : value }}
        />
        <input
          type="text"
          className={`color-swatch-picker__input${draftValid ? "" : " color-swatch-picker__input--invalid"}`}
          value={draft}
          spellCheck={false}
          maxLength={7}
          disabled={disabled}
          aria-label={t("common.colorHex")}
          onFocus={() => setFocused(true)}
          onChange={(e) => setDraft(e.target.value)}
          onBlur={commit}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              commit();
              (e.target as HTMLInputElement).blur();
            } else if (e.key === "Escape") {
              setDraft(value);
              (e.target as HTMLInputElement).blur();
            }
          }}
        />
      </label>
    </div>
  );
}
