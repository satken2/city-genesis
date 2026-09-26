/**
 * 道路詳細パネル(RoadDetailPanel・RoadStripMap・RoadTrafficProfile)が共有する表示用の小道具。
 *
 * 混雑度の色分けはパス詳細(path/PathTrafficPanel の congestionColor)・統計ウィンドウの道路の混雑率と
 * 同じ閾値(50%まで余裕・80%超で逼迫)に揃える。recharts と SVG 属性は CSS 変数を解決できないので色は直値で持つ。
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

/** 混雑度の色。容量が無い区間(vc=null)は灰色 */
export function vcColor(vc: number | null): string {
  if (vc == null) return ROAD_COLOR_NO_CAPACITY;
  if (vc >= ROAD_VC_CONGESTED) return ROAD_COLOR_VC_HIGH;
  if (vc >= ROAD_VC_WARN) return ROAD_COLOR_VC_MID;
  return ROAD_COLOR_VC_LOW;
}

/** 区間の線の色。稼働中でない(計画中・工事中など)区間は交通が無いので灰色にする */
export function segmentColor(seg: RoadSegment): string {
  if (seg.status !== STATUS_ACTIVE) return ROAD_COLOR_INACTIVE;
  return vcColor(seg.vc);
}

/** 延長(m)を km 表記の数値文字列にする。1km 未満も km で揃え、小数は桁を増やして潰れないようにする */
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
 * ヘッダのアイコンに使うパス種別。延長の長い方の種別(街路と高速道路が混ざる道路もある)。
 * 区間が無い・どちらでもない場合は街路。
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

/** 車線構成を延長の長い順に並べたもの */
export function sortedLanes(stats: RoadStats): { lanes: number; length_m: number }[] {
  return [...stats.lanes_composition].sort((a, b) => b.length_m - a.length_m || b.lanes - a.lanes);
}

/** ヘッダの車線構成の要約(「4車線道路」、構成が複数なら「4車線道路 ほか」) */
export function lanesSummary(stats: RoadStats, t: TFunction): string {
  const lanes = sortedLanes(stats);
  if (lanes.length === 0) return "";
  const head = t("road.lanesRoad", { n: lanes[0].lanes });
  return lanes.length > 1 ? t("road.lanesRoadAndOthers", { road: head }) : head;
}

/** 1区間の混雑込み・自由走行の旅行速度(km/h)。所要時間が無い区間は null */
export function segmentSpeedKmh(seg: RoadSegment, free: boolean): number | null {
  const minutes = free ? seg.travel_time_free_min : seg.travel_time_min;
  if (minutes == null || minutes <= 0) return null;
  return (seg.length_m / 1000) / (minutes / 60);
}
