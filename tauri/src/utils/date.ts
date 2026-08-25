/**
 * 仮想カレンダーの日付整形ユーティリティ
 *
 * ゲーム内の暦は「1ヶ月=30日」「1年=360日」の仮想暦なので Date では表せない。
 * ただし月の名前だけは実暦と同じ 1〜12 月なので、月名は Intl から取得して
 * 言語ごとの綴り（January / janvier / enero …）を賄い、年月日の並びだけを
 * 言語系統ごとに切り替える。
 *
 * ロケール文字列を引数に取る純粋関数として実装し、i18n への依存は持たない。
 */

/** 年月日を「年→月→日」の順で並べ、単位語を後置する言語系統の書式定義。 */
const CJK_FORMATS: Record<string, { y: string; m: string; d: string; sep: string }> = {
  ja: { y: "年", m: "月", d: "日", sep: "～" },
  zh: { y: "年", m: "月", d: "日", sep: "～" },
  ko: { y: "년 ", m: "월 ", d: "일", sep: " ~ " },
};

/**
 * 1800年1月1日からの経過日数を「1ヶ月=30日」「1年=360日」の仮想カレンダーとしてパースする
 */
export function parseDaysSinceEpoch(daysSinceEpoch: number) {
  const year = 1800 + Math.floor(daysSinceEpoch / 360);
  const month = Math.floor((daysSinceEpoch % 360) / 30) + 1;
  const day = (daysSinceEpoch % 30) + 1;
  return { year, month, day };
}

/** ロケールの言語サブタグ（ja / zh / ko / en / fr / es）を取り出す。 */
function primarySubtag(locale: string): string {
  return (locale || "").split("-")[0];
}

/**
 * 指定ロケールにおける月名（1〜12）を返す。
 * 仮想暦の月番号は実暦の月番号と同じ意味なので、実在の日付から Intl で名前を引ける。
 */
function monthName(month: number, locale: string): string {
  return new Intl.DateTimeFormat(locale, { month: "long" }).format(
    new Date(Date.UTC(2000, month - 1, 15))
  );
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
  const shouldHideDay = options?.hideDay && options?.simulationDays === 30;
  const lang = primarySubtag(locale);

  const cjk = CJK_FORMATS[lang];
  if (cjk) {
    const head = `${year}${cjk.y}${month}${cjk.m}`;
    return shouldHideDay ? head.trimEnd() : `${head}${day}${cjk.d}`;
  }

  const name = monthName(month, locale);
  if (shouldHideDay) {
    // スペイン語・フランス語は「月 年」の間に前置詞を挟む
    if (lang === "es") return `${name} de ${year}`;
    return `${name} ${year}`;
  }
  if (lang === "es") return `${day} de ${name} de ${year}`;
  if (lang === "fr") return `${day} ${name} ${year}`;
  return `${name} ${day}, ${year}`;
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
  const separator = CJK_FORMATS[primarySubtag(locale)]?.sep ?? " – ";
  return `${start}${separator}${end}`;
}
