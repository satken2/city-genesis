/**
 * Immigration regulations tab
 *
 * Set admission conditions: household head age, household composition, gender, skill requirements, and admission
 * quota.
 * Reject applicant immigrant households that do not meet these conditions.
 *
 * Drafts use separate state from building regulations because fields include bool/dict types.
 */

import { useCallback, useMemo } from "react";
import { useTranslation } from "react-i18next";
import { useMasterData } from "../../../contexts/MasterDataContext";
import type { SkillEntry } from "../../../types";
import type { ImmigrationRegulations } from "../regulationTypes";

/** Scalar (nondictionary) keys used for save payloads and change detection */
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

/** Numeric input fields */
type ImmNumberKey =
  | "immigration_age_min"
  | "immigration_age_max"
  | "immigration_gender_policy"
  | "immigration_quota";

/** Checkbox fields (allowed household compositions) */
type ImmBoolKey =
  | "immigration_enabled"
  | "immigration_allow_single"
  | "immigration_allow_married_no_children"
  | "immigration_allow_with_children";

/** Household composition checkbox order */
const HOUSEHOLD_CHECKS: { key: ImmBoolKey; labelKey: string }[] = [
  { key: "immigration_allow_single", labelKey: "regulation.immigrationAllowSingle" },
  { key: "immigration_allow_married_no_children", labelKey: "regulation.immigrationAllowMarriedNoChildren" },
  { key: "immigration_allow_with_children", labelKey: "regulation.immigrationAllowWithChildren" },
];

/** Skill requirement slider maximum, matching the realistic range of citizen skill values */
const SKILL_SLIDER_MAX = 1.5;

export function ImmigrationTab({
  cityImmigration,
  pendingImmigration,
  onNumberChange,
  onBoolChange,
  onSkillThresholdChange,
}: {
  /** Saved values retrieved from the API */
  cityImmigration: ImmigrationRegulations;
  /** Draft values; absent keys display saved values. */
  pendingImmigration: Partial<ImmigrationRegulations>;
  onNumberChange: (key: ImmNumberKey, value: number) => void;
  onBoolChange: (key: ImmBoolKey, value: boolean) => void;
  onSkillThresholdChange: (skillId: number, value: number) => void;
}) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  /** All skills in ID order for rendering skill requirement sliders */
  const skillEntries = useMemo(() => {
    if (!masterData?.skills) return [];
    return Object.entries(masterData.skills)
      .map(([id, entry]) => ({
        id: Number(id),
        display_name: (entry as SkillEntry).display_name,
      }))
      .sort((a, b) => a.id - b.id);
  }, [masterData?.skills]);

  /** Displayed scalar value, preferring the draft */
  const getScalar = useCallback(
    <K extends keyof ImmigrationRegulations>(key: K): ImmigrationRegulations[K] =>
      (pendingImmigration[key] ?? cityImmigration[key]) as ImmigrationRegulations[K],
    [pendingImmigration, cityImmigration],
  );

  /** Whether a scalar field differs from its saved value */
  const isModified = useCallback(
    (key: keyof ImmigrationRegulations): boolean => getScalar(key) !== cityImmigration[key],
    [getScalar, cityImmigration],
  );

  /** Required threshold for the specified skill (current slider position); defaults to 0. */
  const getSkillThreshold = useCallback(
    (skillId: number): number => {
      const key = String(skillId);
      const pending = pendingImmigration.immigration_skill_thresholds;
      if (pending && key in pending) return pending[key];
      return cityImmigration.immigration_skill_thresholds[key] ?? 0;
    },
    [pendingImmigration, cityImmigration],
  );

  /** Shared numeric input handler. Ignore negative or nonnumeric input and retain the previous value. */
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
        {/* Household head age range */}
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

        {/* Household composition */}
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

        {/* Gender (household head) */}
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

        {/* Admission limit (quota) */}
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

        {/* Skill requirements (OR condition) */}
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
