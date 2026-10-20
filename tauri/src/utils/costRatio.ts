/**
 * Color coding for construction cost relative to standard cost (construction on perfectly flat terrain at zero
 * elevation). Shared by building/road construction panels. Ratio = estimated cost / standard cost; 1.0 equals
 * level ground with no premium. Elevated/underground structures and steep terrain increase it.
 */

/** Below this threshold: green (reasonable, comparable to level ground). */
export const COST_RATIO_GREEN_MAX = 2.0;
/** Below this threshold: yellow (moderate premium); at or above: red (large premium). */
export const COST_RATIO_YELLOW_MAX = 4.0;

/**
 * Return text color for the ratio: green (< 2.0) → yellow (2.0–4.0) → red (>= 4.0).
 */
export function costRatioColor(ratio: number): string {
  if (ratio < COST_RATIO_GREEN_MAX) return "#5bc46a"; // Green: comparable to level ground
  if (ratio < COST_RATIO_YELLOW_MAX) return "#e0b23c"; // Yellow: moderate premium
  return "#e0603c"; // Red: large premium
}
