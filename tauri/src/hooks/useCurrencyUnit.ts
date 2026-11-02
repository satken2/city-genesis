import { useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import { defaultCurrencyUnit, getCurrencyUnitOverride, subscribeCurrencyUnit } from "../utils/money";

/** 金額・通貨ラベルを表示する部品を購読させ、設定変更時に開いたままの画面も更新する。 */
export function useCurrencyUnit(): string {
  const { i18n } = useTranslation();
  const override = useSyncExternalStore(subscribeCurrencyUnit, getCurrencyUnitOverride, getCurrencyUnitOverride);
  return override ?? defaultCurrencyUnit(i18n.language);
}
