/**
 * Color swatch picker
 *
 * Compact color selector combining circular presets with a "#rrggbb" input.
 * Avoid the OS color dialog (input type="color"), which may open behind the always-on-top
 * transparent window and become inaccessible; keep controls within the panel.
 *
 * Always pass values as lowercase "#rrggbb". Input accepts uppercase and missing "#", normalizes
 * it, and calls onChange only for valid formats. Invalid partial input stays in the field
 * and returns to the current value on blur.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import "./ColorSwatchPicker.css";

type ColorSwatchPickerProps = {
  /** Current color ("#rrggbb"). */
  value: string;
  onChange: (hex: string) => void;
  /** Presets displayed as swatches (array of "#rrggbb"). */
  presets: readonly string[];
  disabled?: boolean;
};

/** Normalize input to lowercase "#rrggbb". Null for invalid format. */
export function normalizeHexColor(raw: string): string | null {
  const text = raw.trim().replace(/^#/, "");
  if (!/^[0-9a-fA-F]{6}$/.test(text)) return null;
  return `#${text.toLowerCase()}`;
}

export function ColorSwatchPicker({ value, onChange, presets, disabled = false }: ColorSwatchPickerProps) {
  const { t } = useTranslation();
  const [draft, setDraft] = useState(value);
  const [focused, setFocused] = useState(false);

  // Follow external value changes (refetch after saving or revert) unless the field is being edited.
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
