/**
 * Social security tab
 *
 * Set policy values for city government cash benefits to households (unemployment benefits, pensions, income
 * support).
 * Retirement age and benefit rates are City policy properties used both for required social security budget
 * calculations (category 12) and benefit payments in the backend.
 *
 * The API exchanges benefit rates as 0–1 ratios, while the UI uses 0–100 percentages.
 */

import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import type { WelfareRegulations } from "../regulationTypes";

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
const ROWS: { key: WelfareKey; labelKey: string; unitKey: string; min: number; max: number; step: number }[] = [
  { key: "retirement_age", labelKey: "regulation.retirementAge", unitKey: "regulation.retirementAgeUnit", min: 0, max: 120, step: 1 },
  { key: "unemployment_benefit_ratio", labelKey: "regulation.unemploymentBenefitRatio", unitKey: "regulation.welfareWageRatioUnit", min: 0, max: 150, step: 5 },
  { key: "pension_benefit_ratio", labelKey: "regulation.pensionBenefitRatio", unitKey: "regulation.welfareWageRatioUnit", min: 0, max: 150, step: 5 },
  { key: "livelihood_assistance_ratio", labelKey: "regulation.livelihoodAssistanceRatio", unitKey: "regulation.welfareShortfallRatioUnit", min: 0, max: 100, step: 5 },
];

export function WelfareTab({
  cityWelfare,
  pendingWelfare,
  onChange,
}: {
  /** Saved values retrieved from the API, in API units */
  cityWelfare: WelfareRegulations;
  /** Draft values in API units; absent keys display saved values. */
  pendingWelfare: Partial<WelfareRegulations>;
  onChange: (key: WelfareKey, apiValue: number) => void;
}) {
  const { t } = useTranslation();

  /** Shared numeric input handler. Ignore negative or nonnumeric input and retain the previous value. */
  const handleChange = useCallback(
    (key: WelfareKey, rawValue: string) => {
      const parsed = key === "retirement_age" ? parseInt(rawValue, 10) : parseFloat(rawValue);
      if (isNaN(parsed) || parsed < 0) return;
      onChange(key, welfareUiToApi(key, parsed));
    },
    [onChange],
  );

  return (
    <>
      <p className="regulation-pane__description">
        {t("regulation.welfareDescription")}
      </p>

      <div className="regulation-building-list">
        {ROWS.map((row) => {
          const apiValue = pendingWelfare[row.key] ?? cityWelfare[row.key];
          const modified = apiValue !== cityWelfare[row.key];
          return (
            <div key={row.key} className="regulation-building-row">
              <span
                className={[
                  "regulation-building-row__name",
                  modified && "regulation-building-row__name--modified",
                ]
                  .filter(Boolean)
                  .join(" ")}
              >
                {t(row.labelKey)}
              </span>
              <div className="regulation-building-row__input-group">
                <input
                  type="number"
                  min={row.min}
                  max={row.max}
                  step={row.step}
                  className={[
                    "regulation-building-input",
                    modified && "regulation-building-input--modified",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  value={welfareApiToUi(row.key, apiValue)}
                  onChange={(e) => handleChange(row.key, e.target.value)}
                />
                <span className="regulation-building-unit">{t(row.unitKey)}</span>
              </div>
            </div>
          );
        })}
      </div>
      <p className="regulation-immigration-quota-hint">
        {t("regulation.welfareHint")}
      </p>
    </>
  );
}
