/**
 * 建築規制の入力フィールド群コンポーネント
 *
 * 土地区画パネル(restriction_override=true のときのみ有効)と
 * 地区パネルの双方で使う、規制関連の入力フィールドをまとめたもの。
 * - 建設可能建物種別マスク(AllowedBdMaskInput)
 * - 建ぺい率上限
 * - 容積率上限
 * - 高さ規制
 * - 最低構造種別(防火地域指定に相当。この構造以上でしか建てられない。0=制限なし)
 *
 * disabled=true の場合はすべてのフィールドが非活性になる。
 *
 * 建ぺい率・容積率は API では比率(1.0 = 100%)で保持するが、
 * 入力欄では 100 倍したパーセント値として表示・編集する。
 * 建ぺい率は 100% が上限だが、容積率は 100% を大きく超える(例: 3000%)ため上限を設けない。
 * 高さはメートル単位のまま入力し、右隣に単位ラベルを付ける。
 */

import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { useMasterData } from "../../contexts/MasterDataContext";
import { AllowedBdMaskInput } from "./AllowedBdMaskInput";
import "./RestrictionFields.css";

export type RestrictionValues = {
  allowed_bd_mask: number;
  limit_coverage_ratio: number | null;
  limit_volume_ratio: number | null;
  limit_height: number | null;
  /** 建設を許可する最低構造種別(structures マスターのID)。0=制限なし */
  min_structure: number;
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
  const masterData = useMasterData();

  /** 構造種別の選択肢(ID昇順)。マスター未取得時は「制限なし」だけになる。 */
  const structureOptions = useMemo(
    () =>
      Object.entries(masterData?.structures ?? {})
        .map(([id, entry]) => ({ id: Number(id), name: entry.display_name }))
        .sort((a, b) => a.id - b.id),
    [masterData]
  );

  function handleBdMaskChange(mask: number) {
    onChange({ ...values, allowed_bd_mask: mask });
  }

  function handleMinStructureChange(raw: string) {
    const n = Number(raw);
    onChange({ ...values, min_structure: Number.isNaN(n) ? 0 : n });
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

          <span className={`agent-detail-label${disabled ? " restriction-label--disabled" : ""}`}>
            {t("parcel.minStructure")}
          </span>
          <div className="restriction-input-with-suffix">
            <select
              className="restriction-input"
              value={values.min_structure ?? 0}
              onChange={(e) => handleMinStructureChange(e.target.value)}
              disabled={disabled}
            >
              <option value={0}>{t("parcel.minStructureNone")}</option>
              {structureOptions.map((opt) => (
                <option key={opt.id} value={opt.id}>
                  {t("parcel.minStructureAtLeast", { name: opt.name })}
                </option>
              ))}
            </select>
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
            disabled={saving}
          >
            {saving ? t("common.saving") : t("common.save")}
          </button>
        </div>
      )}
    </section>
  );
}
