/**
 * 天然資源エリア作成フォームの注記。選んでいる目安の説明と、入力値の読み替え
 * (面積・埋蔵量・持続採取量・空から満タンまでの時間)を表示する。
 *
 * 埋蔵量上限と回復率(1日あたりの割合)はそのままでは桁の感覚がつかみにくいため、
 * 1日あたりの量と回復にかかる時間へ直して見せる。
 * テンプレート作成モードの資源編集パネルが使う。
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
  /** 注記の見た目は置き場所のパネルに合わせるため、クラスは呼び出し側が渡す */
  className: string;
};

/** 1ha未満はm²、それ以上はkm²(有効3桁)で表す */
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
    // 回復量は上限×回復率で一定なので、空から満タンまでは 1/回復率 日かかる。
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
