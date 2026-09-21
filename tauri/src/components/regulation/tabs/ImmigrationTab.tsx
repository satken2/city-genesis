/**
 * 移民規制タブ
 *
 * 都市への移民受け入れ条件（世帯主の年齢・世帯構成・性別・スキル要件・受け入れ上限）を設定する。
 * 応募した移民世帯がこの条件を満たさない場合、入居を許可しない。
 *
 * bool / dict 型のフィールドを含むため、編集中値は建築規制とは別の state で管理される。
 */

import { useCallback, useMemo } from "react";
import { useTranslation } from "react-i18next";
import { useMasterData } from "../../../contexts/MasterDataContext";
import type { SkillEntry } from "../../../types";
import type { ImmigrationRegulations } from "../regulationTypes";

/** 保存ペイロードの構築や差分判定で使う、スカラー項目（辞書以外）のキー一覧 */
export const IMMIGRATION_SCALAR_KEYS = [
  "immigration_enabled",
  "immigration_age_min",
  "immigration_age_max",
  "immigration_allow_single",
  "immigration_allow_married_no_children",
  "immigration_allow_with_children",
  "immigration_gender_policy",
  "immigration_quota",
] as const;

/** 数値入力の項目 */
type ImmNumberKey =
  | "immigration_age_min"
  | "immigration_age_max"
  | "immigration_gender_policy"
  | "immigration_quota";

/** チェックボックス（世帯構成の許可フラグ）の項目 */
type ImmBoolKey =
  | "immigration_enabled"
  | "immigration_allow_single"
  | "immigration_allow_married_no_children"
  | "immigration_allow_with_children";

/** 世帯構成チェックボックスの並び */
const HOUSEHOLD_CHECKS: { key: ImmBoolKey; labelKey: string }[] = [
  { key: "immigration_allow_single", labelKey: "regulation.immigrationAllowSingle" },
  { key: "immigration_allow_married_no_children", labelKey: "regulation.immigrationAllowMarriedNoChildren" },
  { key: "immigration_allow_with_children", labelKey: "regulation.immigrationAllowWithChildren" },
];

/** スキル要件スライダーの上限値。市民のスキル値がとりうる現実的な範囲に合わせる */
const SKILL_SLIDER_MAX = 1.5;

export function ImmigrationTab({
  cityImmigration,
  pendingImmigration,
  onNumberChange,
  onBoolChange,
  onSkillThresholdChange,
}: {
  /** API から取得した確定値 */
  cityImmigration: ImmigrationRegulations;
  /** 編集中値（キーが無い項目は確定値を表示する） */
  pendingImmigration: Partial<ImmigrationRegulations>;
  onNumberChange: (key: ImmNumberKey, value: number) => void;
  onBoolChange: (key: ImmBoolKey, value: boolean) => void;
  onSkillThresholdChange: (skillId: number, value: number) => void;
}) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  /** スキル要件スライダーを描画するための全スキル一覧（ID順） */
  const skillEntries = useMemo(() => {
    if (!masterData?.skills) return [];
    return Object.entries(masterData.skills)
      .map(([id, entry]) => ({
        id: Number(id),
        display_name: (entry as SkillEntry).display_name,
      }))
      .sort((a, b) => a.id - b.id);
  }, [masterData?.skills]);

  /** スカラー項目の表示値（編集中値を優先） */
  const getScalar = useCallback(
    <K extends keyof ImmigrationRegulations>(key: K): ImmigrationRegulations[K] =>
      (pendingImmigration[key] ?? cityImmigration[key]) as ImmigrationRegulations[K],
    [pendingImmigration, cityImmigration],
  );

  /** スカラー項目が確定値から変更されているか */
  const isModified = useCallback(
    (key: keyof ImmigrationRegulations): boolean => getScalar(key) !== cityImmigration[key],
    [getScalar, cityImmigration],
  );

  /** 指定スキルの要件閾値（スライダーの現在位置）。未設定は 0 */
  const getSkillThreshold = useCallback(
    (skillId: number): number => {
      const key = String(skillId);
      const pending = pendingImmigration.immigration_skill_thresholds;
      if (pending && key in pending) return pending[key];
      return cityImmigration.immigration_skill_thresholds[key] ?? 0;
    },
    [pendingImmigration, cityImmigration],
  );

  /** 数値入力の共通ハンドラ。負値や非数値は無視して直前の値を保つ */
  const handleNumberChange = useCallback(
    (key: ImmNumberKey, rawValue: string) => {
      const parsed = parseInt(rawValue, 10);
      if (isNaN(parsed) || parsed < 0) return;
      onNumberChange(key, parsed);
    },
    [onNumberChange],
  );

  return (
    <>
      <p className="regulation-pane__description">
        {t("regulation.immigrationDescription")}
      </p>

      <div className="regulation-immigration-list">
        <div className="regulation-immigration-row regulation-immigration-row--check">
          <label className="regulation-immigration-checkbox">
            <input
              type="checkbox"
              checked={getScalar("immigration_enabled")}
              onChange={(e) => onBoolChange("immigration_enabled", e.target.checked)}
            />
            <span className={isModified("immigration_enabled") ? "regulation-immigration-checkbox--modified" : ""}>
              {t("regulation.immigrationEnabled")}
            </span>
          </label>
        </div>
        {/* 世帯主の年齢範囲 */}
        <div className="regulation-immigration-row">
          <span
            className={[
              "regulation-immigration-row__name",
              (isModified("immigration_age_min") || isModified("immigration_age_max")) &&
                "regulation-immigration-row__name--modified",
            ]
              .filter(Boolean)
              .join(" ")}
          >
            {t("regulation.immigrationAgeRange")}
          </span>
          <div className="regulation-immigration-row__input-group">
            <input
              type="number"
              min={0}
              max={120}
              step={1}
              className={[
                "regulation-immigration-input",
                isModified("immigration_age_min") && "regulation-immigration-input--modified",
              ]
                .filter(Boolean)
                .join(" ")}
              value={getScalar("immigration_age_min")}
              onChange={(e) => handleNumberChange("immigration_age_min", e.target.value)}
            />
            <span className="regulation-immigration-separator">〜</span>
            <input
              type="number"
              min={0}
              max={120}
              step={1}
              className={[
                "regulation-immigration-input",
                isModified("immigration_age_max") && "regulation-immigration-input--modified",
              ]
                .filter(Boolean)
                .join(" ")}
              value={getScalar("immigration_age_max")}
              onChange={(e) => handleNumberChange("immigration_age_max", e.target.value)}
            />
          </div>
        </div>

        {/* 世帯構成 */}
        <div className="regulation-immigration-row regulation-immigration-row--check">
          <span className="regulation-immigration-row__name">
            {t("regulation.immigrationHouseholdComposition")}
          </span>
          <div className="regulation-immigration-checkboxes">
            {HOUSEHOLD_CHECKS.map((check) => (
              <label key={check.key} className="regulation-immigration-checkbox">
                <input
                  type="checkbox"
                  checked={getScalar(check.key)}
                  onChange={(e) => onBoolChange(check.key, e.target.checked)}
                />
                <span className={isModified(check.key) ? "regulation-immigration-checkbox--modified" : ""}>
                  {t(check.labelKey)}
                </span>
              </label>
            ))}
          </div>
        </div>

        {/* 性別（世帯主） */}
        <div className="regulation-immigration-row">
          <span
            className={[
              "regulation-immigration-row__name",
              isModified("immigration_gender_policy") && "regulation-immigration-row__name--modified",
            ]
              .filter(Boolean)
              .join(" ")}
          >
            {t("regulation.immigrationGenderPolicy")}
          </span>
          <select
            className="regulation-immigration-select"
            value={getScalar("immigration_gender_policy")}
            onChange={(e) => handleNumberChange("immigration_gender_policy", e.target.value)}
          >
            <option value={0}>{t("regulation.immigrationGenderAny")}</option>
            <option value={1}>{t("regulation.immigrationGenderMale")}</option>
            <option value={2}>{t("regulation.immigrationGenderFemale")}</option>
          </select>
        </div>

        {/* 受け入れ上限人数（クオータ） */}
        <div className="regulation-immigration-row">
          <span
            className={[
              "regulation-immigration-row__name",
              isModified("immigration_quota") && "regulation-immigration-row__name--modified",
            ]
              .filter(Boolean)
              .join(" ")}
          >
            {t("regulation.immigrationQuota")}
          </span>
          <div className="regulation-immigration-row__input-group">
            <input
              type="number"
              min={0}
              step={1}
              className={[
                "regulation-immigration-input",
                isModified("immigration_quota") && "regulation-immigration-input--modified",
              ]
                .filter(Boolean)
                .join(" ")}
              value={getScalar("immigration_quota")}
              onChange={(e) => handleNumberChange("immigration_quota", e.target.value)}
            />
            <span className="regulation-immigration-separator">
              {t("regulation.immigrationQuotaUnit")}
            </span>
          </div>
        </div>
        <p className="regulation-immigration-quota-hint">
          {t("regulation.immigrationQuotaHint")}
        </p>

        {/* スキル要件（OR条件） */}
        <div className="regulation-immigration-skills">
          <span className="regulation-immigration-skills__title">
            {t("regulation.immigrationSkillThresholds")}
          </span>
          <p className="regulation-immigration-skills__hint">
            {t("regulation.immigrationSkillHint")}
          </p>
          <div className="regulation-skill-list">
            {skillEntries.map((skill) => {
              const value = getSkillThreshold(skill.id);
              const modified = value !== (cityImmigration.immigration_skill_thresholds[String(skill.id)] ?? 0);
              return (
                <div key={skill.id} className="regulation-skill-row">
                  <span
                    className={[
                      "regulation-skill-row__name",
                      modified && "regulation-skill-row__name--modified",
                    ]
                      .filter(Boolean)
                      .join(" ")}
                    title={skill.display_name}
                  >
                    {skill.display_name}
                  </span>
                  <div className="regulation-skill-row__control">
                    <input
                      type="range"
                      min={0}
                      max={SKILL_SLIDER_MAX}
                      step={0.05}
                      className="regulation-skill-slider"
                      value={value}
                      onChange={(e) => onSkillThresholdChange(skill.id, parseFloat(e.target.value))}
                    />
                    <span className="regulation-skill-value">{value.toFixed(2)}</span>
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      </div>
    </>
  );
}
