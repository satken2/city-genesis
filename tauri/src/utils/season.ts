/**
 * 仮想暦の季節と季節画像。
 *
 * 季節は北半球の温帯の暦月で分ける(3〜5月=春、6〜8月=夏、9〜11月=秋、12〜2月=冬)。
 * Unity の天気(`WeatherClimate.SeasonOf`)と同じ区切りで、気候区分による違いは持たない。
 * 暦は1か月=30日・1年=360日(`date.ts`)。
 */
import { parseDaysSinceEpoch } from "./date";

export type Season = "spring" | "summer" | "autumn" | "winter";

export const DAYS_PER_YEAR = 360;

/** 1年の中で各季節が始まる日(1月1日=0)。冬は12月1日に始まり、年をまたいで2月末まで続く */
export const SEASON_STARTS: { season: Season; day: number }[] = [
  { season: "spring", day: 60 },
  { season: "summer", day: 150 },
  { season: "autumn", day: 240 },
  { season: "winter", day: 330 },
];

/** 1年の中の日(1月1日=0〜359) */
export function dayOfYear(daysSinceEpoch: number): number {
  return ((daysSinceEpoch % DAYS_PER_YEAR) + DAYS_PER_YEAR) % DAYS_PER_YEAR;
}

export function seasonOf(daysSinceEpoch: number): Season {
  const day = dayOfYear(daysSinceEpoch);
  return [...SEASON_STARTS].reverse().find((start) => start.day <= day)?.season ?? "winter";
}

/** `assets/images/seasons/{月}.png`(1〜12)。無い月は `default.png` */
const SEASON_IMAGES = import.meta.glob<{ default: string }>(
  "../assets/images/seasons/*.{png,jpg,jpeg,webp}",
  { eager: true },
);

function seasonImage(name: string): string | undefined {
  const entry = Object.entries(SEASON_IMAGES).find(([path]) => path.split("/").pop()?.replace(/\.[^.]+$/, "") === name);
  return entry?.[1].default;
}

/** その日付の月の画像。月の画像も既定の画像も無ければ undefined */
export function seasonImageUrl(daysSinceEpoch: number): string | undefined {
  return seasonImage(String(parseDaysSinceEpoch(daysSinceEpoch).month)) ?? seasonImage("default");
}
