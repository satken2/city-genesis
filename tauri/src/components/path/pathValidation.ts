import type { PathInterferenceHighlight } from "../../utils/ipc";

import type { PathAlignmentResponse } from "./usePathConstruction";

export type PathValidationResult = PathInterferenceHighlight & {
  is_valid: boolean;
  alignments?: PathAlignmentResponse[];
  alignment_violations?: Array<{
    index: number;
    curve_violations: number[][];
    grade_violations: number[][];
  }>;
};

type Translate = (key: string, values?: Record<string, string | number>) => string;

/** 全線検証の拒否理由を使う。中心線の推定値から原因を推測しない。 */
export function describePathValidation(result: PathValidationResult, t: Translate): string[] {
  if (result.is_valid) return [];
  const messages: string[] = [];
  for (const violation of result.alignment_violations ?? []) {
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
  if (result.colliding_path_ids.length) messages.push(t("path_construction.validationPathCollision"));
  if (result.colliding_river_ids?.length) messages.push(t("path_construction.validationRiverCollision"));
  return messages.length ? messages : [t("path_construction.validationRejected")];
}
