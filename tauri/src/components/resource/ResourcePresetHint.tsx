/**
 * 資源作成の目安。面積比例のプリセットは形状を描く前にも1 km²あたりの量を示す。
 * 入力後は単位付きの埋蔵量・持続採取量を示し、水源は生活用水に換算した人口を添える。
 * 人口は30日エポックで回復量だけを使う参考値で、産業需要や取水・配水施設の能力は含めない。
 */

import { useTranslation } from "react-i18next";
import { DAYS_PER_YEAR, RESOURCE_WATER } from "../../constants";
import { sustainableDailyYield, waterPopulationEstimate, type ResourcePreset } from "../../data/resourcePresets";
import type { MasterData } from "../../types";
import { resolveIntlLocale } from "../../utils/locale";
import { formatArea } from "../../utils/formatArea";

type ResourcePresetHintProps = {
  preset: ResourcePreset | null;
  productId: number | null;
  masterData: MasterData;
  areaM2: number;
  maxAmount: number;
  recoveryRate: number;
  awaitingShape: boolean;
  /** The caller supplies a class to match the notes to the containing panel. */
  className: string;
};

export function ResourcePresetHint({
  preset,
  productId,
  masterData,
  areaM2,
  maxAmount,
  recoveryRate,
  awaitingShape,
  className,
}: ResourcePresetHintProps) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);
  const amountFormat = new Intl.NumberFormat(locale, { notation: "compact", maximumSignificantDigits: 3 });
  const formatAmount = (value: number) => Number.isFinite(value) ? amountFormat.format(value) : "-";
  const formatQuantity = (value: number) => {
    const useTonnes = preset?.unit === "kg" && value >= 1000;
    const unit = preset ? t(`resourcePreset.units.${useTonnes ? "tonnes" : preset.unit}`) : masterData.products[productId ?? -1]?.unit ?? "";
    return `${formatAmount(useTonnes ? value / 1000 : value)} ${unit}`.trim();
  };
  const waterEstimate = !awaitingShape && productId === RESOURCE_WATER
    ? waterPopulationEstimate(maxAmount, recoveryRate, masterData)
    : null;

  const parts: string[] = [];
  if (areaM2 > 0) parts.push(t("resourcePreset.summary.area", { area: formatArea(areaM2, locale) }));
  parts.push(t("resourcePreset.summary.amount", { amount: formatQuantity(maxAmount) }));
  if (recoveryRate > 0) {
    const dailyYield = sustainableDailyYield(maxAmount, recoveryRate);
    parts.push(t("resourcePreset.summary.yield", { amount: formatQuantity(dailyYield) }));
    // 回復量は埋蔵上限×回復率で一定なので、空から満タンになるまでの日数は回復率の逆数になる。
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
      {preset?.basis === "area" && (
        <div className={className}>{t("resourcePreset.summary.perArea", { amount: formatQuantity(preset.amount * 1e6) })}</div>
      )}
      <div className={className}>{awaitingShape ? t("resourcePreset.summary.needsShape") : parts.join(" / ")}</div>
      {waterEstimate && (
        <>
          <div className={className}>{t("resourcePreset.summary.waterPopulation", { population: formatAmount(waterEstimate.population) })}</div>
          <div className={className}>{t("resourcePreset.summary.waterPopulationBasis")}</div>
        </>
      )}
    </>
  );
}
