/** 金額を言語別の省略表記で示し、ホバー時に桁を省略しない金額を表示する。単位の設定変更にも追従する。 */

import { useCurrencyUnit } from "../../hooks/useCurrencyUnit";
import { useTranslation } from "react-i18next";
import { formatCompactAmount, formatExactAmount } from "../../utils/money";
import { resolveIntlLocale } from "../../utils/locale";
import "./Money.css";

type MoneyProps = {
  /** Amount to display (number). */
  value: number;
  /** 通貨単位を付けるか。単位はユーザー設定と表示言語から決まる。 */
  unit?: boolean;
  /** Optional additional class for integration into existing layouts. */
  className?: string;
};

export function Money({ value, unit = false, className }: MoneyProps) {
  const { i18n } = useTranslation();
  useCurrencyUnit();
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
