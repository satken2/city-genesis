/**
 * エポック日付コンポーネント
 *
 * 1800年1月1日からの経過日数を日付形式で表示する。
 * 多言語対応のため、i18next の現在の言語設定に応じたロケールでフォーマットする。
 * tooltip には経過日数を表示し、将来「何日前」表示などにも拡張できるようにコンポーネント化している。
 */

import { useTranslation } from "react-i18next";
import { formatEpochDate } from "../../utils/date";

type EpochDateProps = {
  /** 1800年1月1日からの経過日数 */
  daysSinceEpoch: number;
  /** 日付を省略するかどうか */
  hideDay?: boolean;
  /** シミュレーション日数 */
  simulationDays?: number;
};

export function EpochDate({ daysSinceEpoch, hideDay, simulationDays }: EpochDateProps) {
  const { i18n, t } = useTranslation();

  const locale = i18n.language === "ja" ? "ja-JP" : "en-US";
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
