/**
 * Virtual calendar seasons and images. Use northern temperate calendar months: March–May spring, June–August
 * summer, September–November autumn, December–February winter. Matches Unity WeatherClimate.SeasonOf, without
 * climate-specific differences. Calendar: 30-day months, 360-day years (date.ts).
 */
import { parseDaysSinceEpoch } from "./date";

export type Season = "spring" | "summer" | "autumn" | "winter";

export const DAYS_PER_YEAR = 360;

/** Season start days within a year (January 1=0). Winter begins December 1 and continues across the year through February. */
export const SEASON_STARTS: { season: Season; day: number }[] = [
  { season: "spring", day: 60 },
  { season: "summer", day: 150 },
  { season: "autumn", day: 240 },
  { season: "winter", day: 330 },
];

/** Day within the year (January 1=0 through 359). */
export function dayOfYear(daysSinceEpoch: number): number {
  return ((daysSinceEpoch % DAYS_PER_YEAR) + DAYS_PER_YEAR) % DAYS_PER_YEAR;
}

export function seasonOf(daysSinceEpoch: number): Season {
  const day = dayOfYear(daysSinceEpoch);
  return [...SEASON_STARTS].reverse().find((start) => start.day <= day)?.season ?? "winter";
}

/** assets/images/seasons/{month}.png (1–12). Missing months use default.png. */
const SEASON_IMAGES = import.meta.glob<{ default: string }>(
  "../assets/images/seasons/*.{png,jpg,jpeg,webp}",
  { eager: true },
);

function seasonImage(name: string): string | undefined {
  const entry = Object.entries(SEASON_IMAGES).find(([path]) => path.split("/").pop()?.replace(/\.[^.]+$/, "") === name);
  return entry?.[1].default;
}

/** Image for the date's month. undefined if both monthly and default images are absent. */
export function seasonImageUrl(daysSinceEpoch: number): string | undefined {
  return seasonImage(String(parseDaysSinceEpoch(daysSinceEpoch).month)) ?? seasonImage("default");
}
