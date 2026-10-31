/**
 * Labor and social security tab
 *
 * Working hours: statutory working hours per day.
 * Social security: policy values for city government cash benefits to households (unemployment benefits, pensions,
 * income support). Retirement age and benefit rates are City policy properties used both for required social
 * security budget calculations (category 12) and benefit payments in the backend.
 *
 * The API exchanges benefit rates as 0–1 ratios, while the UI uses 0–100 percentages.
 */

import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import type { WelfareRegulations } from "../regulationTypes";
import { RegulationNumberRow, RegulationSection } from "../RegulationForm";

/** Keys used for save payloads and change detection */
export const WELFARE_KEYS = [
  "retirement_age",
  "unemployment_benefit_ratio",
  "pension_benefit_ratio",
  "livelihood_assistance_ratio",
] as const;

export type WelfareKey = (typeof WELFARE_KEYS)[number];

/** Convert between benefit ratios (API, 0–1) and percentages (UI, 0–100). */
const RATIO_KEYS: ReadonlySet<WelfareKey> = new Set([
  "unemployment_benefit_ratio",
  "pension_benefit_ratio",
  "livelihood_assistance_ratio",
]);

export function welfareApiToUi(key: WelfareKey, apiValue: number): number {
  return RATIO_KEYS.has(key) ? Math.round(apiValue * 100) : apiValue;
}

export function welfareUiToApi(key: WelfareKey, uiValue: number): number {
  return RATIO_KEYS.has(key) ? uiValue / 100 : uiValue;
}

/** Row order. Unit labels and input ranges differ by field. */
const WELFARE_ROWS: { key: WelfareKey; labelKey: string; unitKey: string; min: number; max: number; step: number }[] = [
  { key: "retirement_age", labelKey: "regulation.retirementAge", unitKey: "regulation.retirementAgeUnit", min: 0, max: 120, step: 1 },
  { key: "unemployment_benefit_ratio", labelKey: "regulation.unemploymentBenefitRatio", unitKey: "regulation.welfareWageRatioUnit", min: 0, max: 150, step: 5 },
  { key: "pension_benefit_ratio", labelKey: "regulation.pensionBenefitRatio", unitKey: "regulation.welfareWageRatioUnit", min: 0, max: 150, step: 5 },
  { key: "livelihood_assistance_ratio", labelKey: "regulation.livelihoodAssistanceRatio", unitKey: "regulation.welfareShortfallRatioUnit", min: 0, max: 100, step: 5 },
];

export function LaborTab({
  workHours,
  onWorkHoursChange,
  cityWelfare,
  pendingWelfare,
  onWelfareChange,
}: {
  /** Working hours per day, preferring the draft */
  workHours: number;
  onWorkHoursChange: (hours: number) => void;
  /** Saved social security values retrieved from the API, in API units */
  cityWelfare: WelfareRegulations;
  /** Draft values in API units; absent keys display saved values. */
  pendingWelfare: Partial<WelfareRegulations>;
  onWelfareChange: (key: WelfareKey, apiValue: number) => void;
}) {
  const { t } = useTranslation();

  /** Shared numeric input handler. Ignore negative or nonnumeric input and retain the previous value. */
  const handleWelfareChange = useCallback(
    (key: WelfareKey, rawValue: string) => {
      const parsed = key === "retirement_age" ? parseInt(rawValue, 10) : parseFloat(rawValue);
      if (isNaN(parsed) || parsed < 0) return;
      onWelfareChange(key, welfareUiToApi(key, parsed));
    },
    [onWelfareChange],
  );

  return (
    <>
      <RegulationSection title={t("wellbeing.workTitle")} description={t("wellbeing.workDescription")}>
        <div className="regulation-building-row">
          <label htmlFor="work-hours">{t("wellbeing.work_hours")}</label>
          <input id="work-hours" type="range" min={1} max={16} step={0.5} value={workHours}
            onChange={e => onWorkHoursChange(Number(e.target.value))} />
          <output htmlFor="work-hours">{workHours.toFixed(1)} {t("wellbeing.hours")}</output>
        </div>
      </RegulationSection>

      <RegulationSection title={t("regulation.welfareTitle")} description={t("regulation.welfareDescription")}>
        <div className="regulation-building-list">
          {WELFARE_ROWS.map((row) => {
            const apiValue = pendingWelfare[row.key] ?? cityWelfare[row.key];
            return (
              <RegulationNumberRow
                key={row.key}
                label={t(row.labelKey)}
                unit={t(row.unitKey)}
                value={welfareApiToUi(row.key, apiValue)}
                modified={apiValue !== cityWelfare[row.key]}
                min={row.min}
                max={row.max}
                step={row.step}
                onChange={(raw) => handleWelfareChange(row.key, raw)}
              />
            );
          })}
        </div>
        <p className="regulation-immigration-quota-hint">
          {t("regulation.welfareHint")}
        </p>
      </RegulationSection>
    </>
  );
}
