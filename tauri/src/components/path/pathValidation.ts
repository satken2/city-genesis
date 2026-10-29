import { RAIL_JUNCTION_PLANE_LENGTH } from "../../constants";
import type { PathInterferenceHighlight, PathJunctionLabel } from "../../utils/ipc";

import type { PathAlignmentResponse } from "./usePathConstruction";
import type { PathMaxSpanViolation, PathShapeUpdatePayload } from "./pathBuildConstants";

/** Do not revalidate repeated selection/warning notifications; react only to shape, edit generation, or confirmability changes. */
export function pathValidationShapeKey(shape: PathShapeUpdatePayload): string {
  return JSON.stringify([
    shape.revision, shape.phase, shape.can_confirm, shape.unsent_paths_count, shape.path_type,
    shape.is_curved, shape.follow_terrain, shape.selected_points, shape.start_control, shape.end_control,
    shape.waypoint_controls, shape.embankment_heights, shape.lateral_offsets, shape.max_span_violations,
  ]);
}

export type PathValidationResult = PathInterferenceHighlight & {
  is_valid: boolean;
  colliding_sea_lake_ids?: string[]; // The sea uses "sea"; lakes use water_surfaces IDs.
  ferry_water_violation?: boolean; // A ferry lane leaves the water, or a ferry pier does not reach from land to water.
  colliding_quay_band_ids?: number[]; // reclaimed_land_id
  colliding_grading_site_ids?: number[]; // parcel_id of the parcel undergoing earthworks.
  junction_violations?: Array<{ code: string; position: number[] }>; // Track branching/merging violation. position is the connection [x, absolute road elevation, z].
  alignments?: PathAlignmentResponse[];
  alignment_violations?: Array<{
    index: number;
    curve_violations: number[][];
    grade_violations: number[][];
  }>;
};

type Translate = (key: string, values?: Record<string, string | number>) => string;

function describeJunction(code: string, t: Translate): string {
  return t(`path_construction.railJunction.${code}`, { length: RAIL_JUNCTION_PLANE_LENGTH });
}

/** Convert track branching/merging violations into Unity callouts at connections, using the same text as construction-panel reasons. */
export function describeJunctionLabels(result: PathValidationResult, t: Translate): PathJunctionLabel[] {
  return (result.junction_violations ?? []).map(({ code, position }) => ({ position, text: describeJunction(code, t) }));
}

/** Share reasons for Unity's red lines between the shape editor and construction panel. */
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
 * Use rejection reasons from full-path validation; do not infer causes from centerline estimates.
 * With shapeSummary, collapse per-section curve-radius/grade reasons into that one line
 * (for screens where shape editor callouts identify the sections).
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
  for (const code of new Set((result.junction_violations ?? []).map((violation) => violation.code))) {
    messages.push(describeJunction(code, t));
  }
  if (result.colliding_path_ids.length) messages.push(t("path_construction.validationPathCollision"));
  if (result.colliding_river_ids?.length) messages.push(t("path_construction.validationRiverCollision"));
  if (result.colliding_sea_lake_ids?.length) messages.push(t("errors.CREATEPATH_SEA_LAKE_CROSSING"));
  if (result.ferry_water_violation) messages.push(t("errors.CREATEPATH_FERRY_WATER"));
  if (result.colliding_quay_band_ids?.length) messages.push(t("errors.CREATEPATH_QUAY_BAND_CROSSING"));
  if (result.colliding_grading_site_ids?.length) messages.push(t("path_construction.validationGradingSite"));
  return messages.length ? messages : [t("path_construction.validationRejected")];
}
