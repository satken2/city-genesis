/**
 * 土地区画の規制入力フィールド群コンポーネント
 *
 * restriction_override=true の場合にのみ有効になる規制関連の入力フィールドをまとめたもの。
 * - 建設可能建物種別マスク(AllowedBdMaskInput)
 * - 建ぺい率上限
 * - 容積率上限
 * - 高さ規制
 *
 * disabled=true の場合はすべてのフィールドが非活性になる。
 *
 * 建ぺい率・容積率は API では 0.0〜1.0 の比率で保持するが、
 * 入力欄では 100 倍したパーセント値(0〜100 相当)として表示・編集する。
 * 高さはメートル単位のまま入力し、右隣に単位ラベルを付ける。
 */

import { useTranslation } from "react-i18next";
import { AllowedBdMaskInput } from "./AllowedBdMaskInput";

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
  /** true の場合はすべてのフィールドを非活性にしてグレーアウト表示する */
  disabled: boolean;
  onSave?: () => void;
  saving?: boolean;
  saveError?: string | null;
  children?: React.ReactNode;
};

/**
 * API の比率(0〜1)を入力欄用のパーセント文字列に変換する
 */
function ratioToPercentInputString(value: number | null): string {
  if (value == null) return "";
  const pct = value * 100;
  const rounded = Math.round(pct * 100) / 100;
  return String(rounded);
}

/**
 * パーセント入力を API の比率(0〜1)に変換する
 */
function parsePercentToRatio(raw: string): number | null {
  if (raw.trim() === "") return null;
  const n = Number(raw);
  if (isNaN(n)) return null;
  return n / 100;
}

/**
 * 高さなどメートル単位の number | null を入力文字列に変換する
 */
function toMeterInputString(value: number | null): string {
  return value == null ? "" : String(value);
}

/**
 * 入力文字列を数値に変換する(空は null)
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
              max={100}
              step={0.1}
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
        <div className="parcel-save-row">
          {saveError && (
            <span className="parcel-save-error">{saveError}</span>
          )}
          <button
            className="parcel-save-btn"
            type="button"
            onClick={() => void onSave()}
            disabled={saving}
          >
            {saving ? t("common.saving") : t("common.save")}
          </button>
        </div>
      )}
    </section>
  );
}
