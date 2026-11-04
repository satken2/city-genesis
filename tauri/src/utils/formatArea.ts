/** 小さな範囲でもゼロに丸めず、広い範囲は一覧しやすい km² で示す。 */
export function formatArea(areaM2: number, locale: string): string {
  if (areaM2 < 10_000) return `${Math.round(areaM2).toLocaleString(locale)} m²`;
  return `${new Intl.NumberFormat(locale, { maximumSignificantDigits: 3 }).format(areaM2 / 1e6)} km²`;
}
