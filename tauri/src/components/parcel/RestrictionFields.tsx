/**
 * Building regulation input fields
 *
 * Shared regulation fields for the parcel panel (enabled only when restriction_override=true)
 * and district panel:
 * - Buildable building type mask (AllowedBdMaskInput).
 * - Building coverage limit.
 * - Volume ratio limit.
 * - Height regulation.
 *
 * disabled=true disables all fields.
 * API coverage/volume ratios are proportions (1.0 = 100%), but inputs display/edit percentages
 * multiplied by 100. Coverage is capped at 100%; volume ratio may greatly exceed 100% (e.g. 3000%)
 * and has no upper bound. Heights remain meters with a unit label to the right.
 */

import { useTranslation } from "react-i18next";
import { AllowedBdMaskInput } from "./AllowedBdMaskInput";
import "./RestrictionFields.css";

export type RestrictionValues = {
  allowed_bd_mask: number;
  limit_coverage_ratio: number | null;
  limit_volume_ratio: number | null;
  limit_height: number | null;
};

type RestrictionFieldsProps = {
  title?: string;
  values: RestrictionValues;
  onChange: (values: RestrictionValues) => void;
  /** True disables and grays out all fields. */
  disabled: boolean;
  onSave?: () => void;
  saving?: boolean;
  /** Disable only saving (e.g. when unchanged) while keeping inputs editable. */
  saveDisabled?: boolean;
  saveError?: string | null;
  children?: React.ReactNode;
};

/**
 * Convert API ratios (0-1) into percentage input strings.
 */
function ratioToPercentInputString(value: number | null): string {
  if (value == null) return "";
  const pct = value * 100;
  const rounded = Math.round(pct * 100) / 100;
  return String(rounded);
}

/**
 * Convert percentage inputs into API ratios (0-1).
 */
function parsePercentToRatio(raw: string): number | null {
  if (raw.trim() === "") return null;
  const n = Number(raw);
  if (isNaN(n)) return null;
  return n / 100;
}

/**
 * Convert meter-based number | null values, such as height, into input strings.
 */
function toMeterInputString(value: number | null): string {
  return value == null ? "" : String(value);
}

/**
 * Convert input strings to numbers (empty becomes null).
 */
function parseMeterInput(raw: string): number | null {
  if (raw.trim() === "") return null;
  const n = Number(raw);
  return isNaN(n) ? null : n;
}

export function RestrictionFields({
  title,
  values,
  onChange,
  disabled,
  onSave,
  saving,
  saveDisabled = false,
  saveError,
  children,
}: RestrictionFieldsProps) {
  const { t } = useTranslation();

  function handleBdMaskChange(mask: number) {
    onChange({ ...values, allowed_bd_mask: mask });
  }

  function handleCoveragePercentChange(raw: string) {
    onChange({
      ...values,
      limit_coverage_ratio: parsePercentToRatio(raw),
    });
  }

  function handleVolumePercentChange(raw: string) {
    onChange({
      ...values,
      limit_volume_ratio: parsePercentToRatio(raw),
    });
  }

  function handleHeightChange(raw: string) {
    onChange({ ...values, limit_height: parseMeterInput(raw) });
  }

  return (
    <section className="agent-detail-section">
      {title && <h3 className="agent-detail-section-title">{title}</h3>}
      {children}
      <div className="restriction-fields">
        <div className="agent-detail-grid">
          <span className={`agent-detail-label${disabled ? " restriction-label--disabled" : ""}`}>
            {t("parcel.allowedBdMask")}
          </span>
          <div className="agent-detail-value">
            <AllowedBdMaskInput
              value={values.allowed_bd_mask}
              onChange={handleBdMaskChange}
              disabled={disabled}
              getLabel={(key) => t(key)}
            />
          </div>

          <span className={`agent-detail-label${disabled ? " restriction-label--disabled" : ""}`}>
            {t("parcel.limitCoverageRatio")}
          </span>
          <div className="restriction-input-with-suffix">
            <input
              className="restriction-input restriction-input--narrow"
              type="number"
              min={0}
              max={100}
              step={0.1}
              value={ratioToPercentInputString(values.limit_coverage_ratio)}
              onChange={(e) => handleCoveragePercentChange(e.target.value)}
              disabled={disabled}
              placeholder="—"
            />
            <span className="restriction-input-unit">{t("parcel.unitPercent")}</span>
          </div>

          <span className={`agent-detail-label${disabled ? " restriction-label--disabled" : ""}`}>
            {t("parcel.limitVolumeRatio")}
          </span>
          <div className="restriction-input-with-suffix">
            <input
              className="restriction-input restriction-input--narrow"
              type="number"
              min={0}
              step={10}
              value={ratioToPercentInputString(values.limit_volume_ratio)}
              onChange={(e) => handleVolumePercentChange(e.target.value)}
              disabled={disabled}
              placeholder="—"
            />
            <span className="restriction-input-unit">{t("parcel.unitPercent")}</span>
          </div>

          <span className={`agent-detail-label${disabled ? " restriction-label--disabled" : ""}`}>
            {t("parcel.limitHeight")}
          </span>
          <div className="restriction-input-with-suffix">
            <input
              className="restriction-input restriction-input--narrow"
              type="number"
              min={0}
              step={0.5}
              value={toMeterInputString(values.limit_height)}
              onChange={(e) => handleHeightChange(e.target.value)}
              disabled={disabled}
              placeholder="—"
            />
            <span className="restriction-input-unit">{t("parcel.unitMeter")}</span>
          </div>
        </div>
      </div>
      {onSave && (
        <div className="restriction-save-row">
          {saveError && (
            <span className="restriction-save-error">{saveError}</span>
          )}
          <button
            className="restriction-save-btn"
            type="button"
            onClick={() => void onSave()}
            disabled={saving || saveDisabled}
          >
            {saving ? t("common.saving") : t("common.save")}
          </button>
        </div>
      )}
    </section>
  );
}
