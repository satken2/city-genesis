/**
 * Money display component
 *
 * Displays compact amounts according to language (Japanese: 52.5億 / English: 5.25B).
 * The title attribute shows the exact amount on hover.
 * Follows EpochDate's structure: derive locale from i18n, then format with a pure utils function.
 */

import { useTranslation } from "react-i18next";
import { formatCompactAmount, formatExactAmount } from "../../utils/money";
import { resolveIntlLocale } from "../../utils/locale";
import "./Money.css";

type MoneyProps = {
  /** Amount to display (number). */
  value: number;
  /** Whether to display the currency unit (¢), default false. */
  unit?: boolean;
  /** Optional additional class for integration into existing layouts. */
  className?: string;
};

export function Money({ value, unit = false, className }: MoneyProps) {
  const { i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);
  return (
    <span
      className={className ? `money ${className}` : "money"}
      title={formatExactAmount(value, locale, unit)}
    >
      {formatCompactAmount(value, locale, unit)}
    </span>
  );
}
