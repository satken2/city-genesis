/**
 * Epoch date component
 *
 * Formats elapsed days since January 1, 1800 as a date, using a locale derived from
 * i18next's current language. The tooltip shows elapsed days. Encapsulated for future
 * extensions such as "days ago" display.
 */

import { useTranslation } from "react-i18next";
import { formatEpochDate } from "../../utils/date";
import { resolveIntlLocale } from "../../utils/locale";

type EpochDateProps = {
  /** Elapsed days since January 1, 1800. */
  daysSinceEpoch: number;
  /** Whether to omit the date. */
  hideDay?: boolean;
  /** Simulation days. */
  simulationDays?: number;
};

export function EpochDate({ daysSinceEpoch, hideDay, simulationDays }: EpochDateProps) {
  const { i18n, t } = useTranslation();

  const locale = resolveIntlLocale(i18n.language);
  const formatted = formatEpochDate(daysSinceEpoch, locale, { hideDay, simulationDays });

  return (
    <span
      className="epoch-date"
      title={t("common.daysSinceEpoch", { days: daysSinceEpoch })}
    >
      {formatted}
    </span>
  );
}
