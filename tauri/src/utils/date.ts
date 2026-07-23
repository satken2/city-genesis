const ENGLISH_MONTHS = [
  "January", "February", "March", "April", "May", "June",
  "July", "August", "September", "October", "November", "December"
];

/**
 * 1800年1月1日からの経過日数を「1ヶ月=30日」「1年=360日」の仮想カレンダーとしてパースする
 */
export function parseDaysSinceEpoch(daysSinceEpoch: number) {
  const year = 1800 + Math.floor(daysSinceEpoch / 360);
  const month = Math.floor((daysSinceEpoch % 360) / 30) + 1;
  const day = (daysSinceEpoch % 30) + 1;
  return { year, month, day };
}

/**
 * 経過日数と言語設定に基づき、仮想日付をフォーマットする。
 * hideDay が true でかつ simulationDays が 30 の場合、日付部分を省略する。
 */
export function formatEpochDate(
  daysSinceEpoch: number,
  locale: string,
  options?: { hideDay?: boolean; simulationDays?: number }
): string {
  const { year, month, day } = parseDaysSinceEpoch(daysSinceEpoch);
  const isJa = locale.startsWith("ja");
  const shouldHideDay = options?.hideDay && options?.simulationDays === 30;

  if (isJa) {
    if (shouldHideDay) {
      return `${year}年${month}月`;
    }
    return `${year}年${month}月${day}日`;
  } else {
    const monthName = ENGLISH_MONTHS[month - 1];
    if (shouldHideDay) {
      return `${monthName} ${year}`;
    }
    return `${monthName} ${day}, ${year}`;
  }
}

/**
 * 期間を表す日付範囲をフォーマットする。
 */
export function formatEpochDateRange(
  startDays: number,
  endDays: number,
  locale: string,
  options?: { hideDay?: boolean; simulationDays?: number }
): string {
  const start = formatEpochDate(startDays, locale, options);
  const end = formatEpochDate(endDays, locale, options);
  const separator = locale.startsWith("ja") ? "～" : " – ";
  return `${start}${separator}${end}`;
}
