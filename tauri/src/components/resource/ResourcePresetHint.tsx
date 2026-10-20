/**
 * Notes for the natural resource area creation form: explain the selected preset and interpret inputs as area,
 * reserves, sustainable extraction, and time from empty to full.
 *
 * Maximum reserves and daily recovery fractions are hard to assess directly, so convert them to daily quantities
 * and recovery time.
 * Used by the resource editor in scenario authoring mode.
 */

import { useTranslation } from "react-i18next";
import { DAYS_PER_YEAR } from "../../constants";
import { sustainableDailyYield, type ResourcePreset } from "../../data/resourcePresets";
import { formatCompactAmount } from "../../utils/money";
import { resolveIntlLocale } from "../../utils/locale";

type ResourcePresetHintProps = {
  preset: ResourcePreset | null;
  areaM2: number;
  maxAmount: number;
  recoveryRate: number;
  awaitingShape: boolean;
  /** The caller supplies a class to match the notes to the containing panel. */
  className: string;
};

/** Use m² below 1ha, otherwise km² with three significant figures. */
function formatArea(areaM2: number, locale: string): string {
  if (areaM2 < 10_000) return `${Math.round(areaM2).toLocaleString(locale)} m²`;
  return `${new Intl.NumberFormat(locale, { maximumSignificantDigits: 3 }).format(areaM2 / 1e6)} km²`;
}

export function ResourcePresetHint({
  preset,
  areaM2,
  maxAmount,
  recoveryRate,
  awaitingShape,
  className,
}: ResourcePresetHintProps) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);

  const parts: string[] = [];
  if (areaM2 > 0) parts.push(t("resourcePreset.summary.area", { area: formatArea(areaM2, locale) }));
  parts.push(t("resourcePreset.summary.amount", { amount: formatCompactAmount(maxAmount, locale) }));
  if (recoveryRate > 0) {
    const dailyYield = sustainableDailyYield(maxAmount, recoveryRate);
    parts.push(t("resourcePreset.summary.yield", { amount: formatCompactAmount(dailyYield, locale) }));
    // Recovery is constant at maximum × rate, so filling from empty takes 1 / recovery rate days.
    const refillDays = 1 / recoveryRate;
    parts.push(
      refillDays < DAYS_PER_YEAR
        ? t("resourcePreset.summary.refillDays", { days: Math.max(1, Math.round(refillDays)) })
        : t("resourcePreset.summary.refillYears", {
            years: new Intl.NumberFormat(locale, { maximumSignificantDigits: 2 }).format(refillDays / DAYS_PER_YEAR),
          }),
    );
  } else {
    parts.push(t("resourcePreset.summary.nonRenewable"));
  }

  return (
    <>
      {preset && <div className={className}>{t(`resourcePreset.presets.${preset.key}.note`)}</div>}
      <div className={className}>{awaitingShape ? t("resourcePreset.summary.needsShape") : parts.join(" / ")}</div>
    </>
  );
}
