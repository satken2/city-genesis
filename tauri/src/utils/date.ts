/**
 * Virtual calendar date formatting. The game's 30-day months and 360-day years cannot be represented by Date.
 * Month names still correspond to real months 1–12; get their localized names from Intl (January / janvier /
 * enero, etc.) and switch only date order by language family. Pure functions accept locale strings without i18n
 * dependencies.
 */

/** Format definitions for languages ordering year→month→day with trailing unit words. */
const CJK_FORMATS: Record<string, { y: string; m: string; d: string; sep: string }> = {
  ja: { y: "年", m: "月", d: "日", sep: "～" },
  zh: { y: "年", m: "月", d: "日", sep: "～" },
  ko: { y: "년 ", m: "월 ", d: "일", sep: " ~ " },
};

/**
 * Parse days since January 1, 1800 using the virtual calendar (30-day months, 360-day years).
 */
export function parseDaysSinceEpoch(daysSinceEpoch: number) {
  const year = 1800 + Math.floor(daysSinceEpoch / 360);
  const month = Math.floor((daysSinceEpoch % 360) / 30) + 1;
  const day = (daysSinceEpoch % 30) + 1;
  return { year, month, day };
}

/** Extract the locale's language subtag (ja / zh / ko / en / fr / es). */
function primarySubtag(locale: string): string {
  return (locale || "").split("-")[0];
}

/**
 * Return month names (1–12) for the locale. Virtual month numbers correspond to real ones, so Intl can retrieve
 * names from real dates.
 */
function monthName(month: number, locale: string): string {
  return new Intl.DateTimeFormat(locale, { month: "long" }).format(
    new Date(Date.UTC(2000, month - 1, 15))
  );
}

/**
 * Format a virtual date from elapsed days and language settings. Omit the day when hideDay is true and
 * simulationDays is 30.
 */
export function formatEpochDate(
  daysSinceEpoch: number,
  locale: string,
  options?: { hideDay?: boolean; simulationDays?: number }
): string {
  const { year, month, day } = parseDaysSinceEpoch(daysSinceEpoch);
  const shouldHideDay = options?.hideDay && options?.simulationDays === 30;
  const lang = primarySubtag(locale);

  const cjk = CJK_FORMATS[lang];
  if (cjk) {
    const head = `${year}${cjk.y}${month}${cjk.m}`;
    return shouldHideDay ? head.trimEnd() : `${head}${day}${cjk.d}`;
  }

  const name = monthName(month, locale);
  if (shouldHideDay) {
    // Spanish/French insert a preposition between month and year.
    if (lang === "es") return `${name} de ${year}`;
    return `${name} ${year}`;
  }
  if (lang === "es") return `${day} de ${name} de ${year}`;
  if (lang === "fr") return `${day} ${name} ${year}`;
  return `${name} ${day}, ${year}`;
}

/**
 * Format a date range representing a period.
 */
export function formatEpochDateRange(
  startDays: number,
  endDays: number,
  locale: string,
  options?: { hideDay?: boolean; simulationDays?: number }
): string {
  const start = formatEpochDate(startDays, locale, options);
  const end = formatEpochDate(endDays, locale, options);
  const separator = CJK_FORMATS[primarySubtag(locale)]?.sep ?? " – ";
  return `${start}${separator}${end}`;
}
