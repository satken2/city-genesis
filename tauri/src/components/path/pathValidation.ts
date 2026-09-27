import type { PathInterferenceHighlight } from "../../utils/ipc";

import type { PathAlignmentResponse } from "./usePathConstruction";
import type { PathMaxSpanViolation, PathShapeUpdatePayload } from "./pathBuildConstants";

/** 選択・警告の再通知では検証をやり直さず、形状・編集世代・確定可否の変更だけを拾う。 */
export function pathValidationShapeKey(shape: PathShapeUpdatePayload): string {
  return JSON.stringify([
    shape.revision, shape.phase, shape.can_confirm, shape.unsent_paths_count, shape.path_type,
    shape.is_curved, shape.follow_terrain, shape.selected_points, shape.start_control, shape.end_control,
    shape.waypoint_controls, shape.embankment_heights, shape.lateral_offsets, shape.max_span_violations,
  ]);
}

export type PathValidationResult = PathInterferenceHighlight & {
  is_valid: boolean;
  colliding_sea_lake_ids?: string[]; // 海は "sea"、湖は water_surfaces の id
  colliding_quay_band_ids?: number[]; // reclaimed_land_id
  colliding_grading_site_ids?: number[]; // 工事中の造成の区画の parcel_id
  alignments?: PathAlignmentResponse[];
  alignment_violations?: Array<{
    index: number;
    curve_violations: number[][];
    grade_violations: number[][];
  }>;
};

type Translate = (key: string, values?: Record<string, string | number>) => string;

/** 形状編集と建設パネルでUnityの赤線の理由を共通表示する。 */
export function describeMaxSpanViolations(violations: PathMaxSpanViolation[] | undefined, t: Translate): string[] {
  return (violations ?? []).map(({ segment_index, distance, limit }) =>
    t("path_construction.validationMaxSpan", {
      start: segment_index + 1,
      end: segment_index + 2,
      distance: distance.toFixed(2),
      limit: Number(limit.toFixed(2)),
    }));
}

/**
 * 全線検証の拒否理由を使う。中心線の推定値から原因を推測しない。
 * shapeSummary を渡すと、曲線半径・勾配の区間ごとの理由をその1行に畳む(区間を形状編集パネルの吹き出しが示す画面用)。
 */
export function describePathValidation(result: PathValidationResult, t: Translate, shapeSummary?: string): string[] {
  if (result.is_valid) return [];
  const messages: string[] = [];
  const violations = result.alignment_violations ?? [];
  if (shapeSummary !== undefined) {
    if (violations.length) messages.push(shapeSummary);
  } else {
    for (const violation of violations) {
      for (const [kind, ranges] of [
        ["validationCurve", violation.curve_violations],
        ["validationGrade", violation.grade_violations],
      ] as const) {
        for (const [start, end] of ranges) {
          messages.push(t(`path_construction.${kind}`, {
            track: violation.index + 1, start: start.toFixed(1), end: end.toFixed(1),
          }));
        }
      }
    }
  }
  if (result.colliding_path_ids.length) messages.push(t("path_construction.validationPathCollision"));
  if (result.colliding_river_ids?.length) messages.push(t("path_construction.validationRiverCollision"));
  if (result.colliding_sea_lake_ids?.length) messages.push(t("errors.CREATEPATH_SEA_LAKE_CROSSING"));
  if (result.colliding_quay_band_ids?.length) messages.push(t("errors.CREATEPATH_QUAY_BAND_CROSSING"));
  if (result.colliding_grading_site_ids?.length) messages.push(t("path_construction.validationGradingSite"));
  return messages.length ? messages : [t("path_construction.validationRejected")];
}
