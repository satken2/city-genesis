/**
 * Shared display helpers for RoadDetailPanel, RoadStripMap, and RoadTrafficProfile.
 *
 * Congestion colors use the same thresholds as path/PathTrafficPanel congestionColor and road congestion statistics:
 * spare capacity up to 50%, pressure above 80%. Use literal colors because recharts and SVG attributes cannot resolve CSS variables.
 */

import type { TFunction } from "i18next";
import { PATH_MOTORWAY, PATH_STREET, STATUS_ACTIVE } from "../../constants";
import type { RoadSegment, RoadStats } from "./roadTypes";

export const ROAD_VC_WARN = 0.5;
export const ROAD_VC_CONGESTED = 0.8;

export const ROAD_COLOR_VC_LOW = "#10b981";
export const ROAD_COLOR_VC_MID = "#f59e0b";
export const ROAD_COLOR_VC_HIGH = "#ef4444";
export const ROAD_COLOR_NO_CAPACITY = "#6b7280";
export const ROAD_COLOR_INACTIVE = "#7c8794";

/** Congestion color. Gray for sections without capacity (vc=null). */
export function vcColor(vc: number | null): string {
  if (vc == null) return ROAD_COLOR_NO_CAPACITY;
  if (vc >= ROAD_VC_CONGESTED) return ROAD_COLOR_VC_HIGH;
  if (vc >= ROAD_VC_WARN) return ROAD_COLOR_VC_MID;
  return ROAD_COLOR_VC_LOW;
}

/** Section line color. Gray for inactive sections (planned/under construction, etc.), which have no traffic. */
export function segmentColor(seg: RoadSegment): string {
  if (seg.status !== STATUS_ACTIVE) return ROAD_COLOR_INACTIVE;
  return vcColor(seg.vc);
}

/** Convert length (m) to a km numeric string. Keep km even below 1 km, increasing decimal precision to preserve small values. */
export function formatKm(meters: number, locale: string): string {
  const km = meters / 1000;
  const digits = km < 10 ? 2 : 1;
  return km.toLocaleString(locale, { minimumFractionDigits: digits, maximumFractionDigits: digits });
}

export function formatInt(value: number, locale: string): string {
  return Math.round(value).toLocaleString(locale);
}

export function formatPercentValue(ratio: number, locale: string): string {
  return `${Math.round(ratio * 100).toLocaleString(locale)}%`;
}

/**
 * Path type for the header icon: whichever type has greater total length (roads can mix streets/highways).
 * Default to street if no sections or neither type applies.
 */
export function dominantPathType(segments: RoadSegment[]): number {
  let street = 0;
  let motorway = 0;
  for (const s of segments) {
    if (s.path_type === PATH_MOTORWAY) motorway += s.length_m;
    else if (s.path_type === PATH_STREET) street += s.length_m;
  }
  return motorway > street ? PATH_MOTORWAY : PATH_STREET;
}

/** Lane configurations sorted by decreasing length. */
export function sortedLanes(stats: RoadStats): { lanes: number; length_m: number }[] {
  return [...stats.lanes_composition].sort((a, b) => b.length_m - a.length_m || b.lanes - a.lanes);
}

/** Header lane summary ("4-lane road", or "4-lane road and others" for multiple configurations). */
export function lanesSummary(stats: RoadStats, t: TFunction): string {
  const lanes = sortedLanes(stats);
  if (lanes.length === 0) return "";
  const head = t("road.lanesRoad", { n: lanes[0].lanes });
  return lanes.length > 1 ? t("road.lanesRoadAndOthers", { road: head }) : head;
}

/** One section's congested/free-flow travel speed (km/h). null without travel time. */
export function segmentSpeedKmh(seg: RoadSegment, free: boolean): number | null {
  const minutes = free ? seg.travel_time_free_min : seg.travel_time_min;
  if (minutes == null || minutes <= 0) return null;
  return (seg.length_m / 1000) / (minutes / 60);
}
