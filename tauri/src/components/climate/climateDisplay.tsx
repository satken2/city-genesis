/**
 * 確定気候の4要素(気温・降水量・日照強度・風速)の表示名・アイコン・数値表記。
 * Cockpit の確定気候と気候ウィンドウで共有する。要素のキーは `GET /api/v1/city/climate-effects` と同じ。
 */
import type { ReactNode } from "react";

export type ClimateElement = "temperature" | "precipitation" | "sunlight" | "wind_speed";

export type ClimateValues = Record<ClimateElement, number | null>;

export const CLIMATE_ELEMENTS: { key: ClimateElement; label: string }[] = [
  { key: "temperature", label: "weather.temperature" },
  { key: "precipitation", label: "weather.precipitation" },
  { key: "sunlight", label: "weather.sunlight" },
  { key: "wind_speed", label: "weather.windSpeed" },
];

const ICON_PATHS: Record<ClimateElement, ReactNode> = {
  temperature: <>
    <path d="M9 14.5V5a3 3 0 0 1 6 0v9.5a5 5 0 1 1-6 0Z" />
    <path d="M12 8v9" /><circle cx="12" cy="18" r="1.5" />
  </>,
  precipitation: <>
    <path d="M12 3s-7 8-7 12a7 7 0 0 0 14 0c0-4-7-12-7-12Z" />
    <path d="M8 15a4 4 0 0 0 4 4" />
  </>,
  sunlight: <>
    <circle cx="12" cy="12" r="4" /><path d="M12 1v3m0 16v3M1 12h3m16 0h3M4 4l2 2m12 12 2 2M4 20l2-2M18 6l2-2" />
  </>,
  wind_speed: <>
    <path d="M3 8h10a3 3 0 1 0-3-3" /><path d="M3 12h15a3 3 0 1 1-3 3" /><path d="M3 16h6" />
  </>,
};

export function ClimateIcon({ element }: { element: ClimateElement }) {
  return <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true">
    {ICON_PATHS[element]}
  </svg>;
}

/** 単位付きの表記。未実行・未取得の値は0と区別して「—」にする。 */
export function formatClimateValue(element: ClimateElement, value: number | null, locale: string): string {
  if (value == null) return "—";
  switch (element) {
    case "temperature": return `${value.toLocaleString(locale, { maximumFractionDigits: 1 })}°C`;
    case "precipitation": return `${value.toLocaleString(locale, { maximumFractionDigits: 0 })}mm`;
    case "sunlight": return `${value.toLocaleString(locale, { maximumFractionDigits: 0 })}%`;
    case "wind_speed": return `${value.toLocaleString(locale, { minimumFractionDigits: 1, maximumFractionDigits: 1 })}m/s`;
  }
}
