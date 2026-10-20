/**
 * Utility for horizontal length (m) of a path being placed.
 *
 * Used to display path length in the construction cost section.
 * Straight mode sums node-polyline segment lengths. Curve mode approximates arc length by sampling each segment
 * as a cubic Bézier with Catmull-Rom-derived controls (same convention as backend automatic completion and map curves).
 * Ignore height (Y) differences for road length; accumulate only horizontal distances in the X-Z plane.
 */

import type { BuildPoint, ControlPoint, CurveMode } from "./pathBuildConstants";
import type { WaypointControls } from "./usePathConstruction";

/** Subdivisions per Bézier segment (arc-length approximation accuracy). */
const BEZIER_SAMPLES = 24;

/* ---- Segment control calculation (same logic as map curves and backend automatic completion) ---- */

function computeSegmentCP1(
  pts: BuildPoint[],
  i: number,
  startControl: ControlPoint,
  waypointControls?: WaypointControls[],
): { x: number; z: number } {
  if (i === 0 && startControl) return startControl;
  const wc = i > 0 ? waypointControls?.[i - 1]?.control_out : null;
  if (wc && wc.length >= 2) return { x: wc[0], z: wc[wc.length - 1] };
  const prev = pts[Math.max(0, i - 1)];
  const curr = pts[i];
  const next = pts[i + 1];
  return {
    x: curr.x + (next.x - prev.x) / 6,
    z: curr.z + (next.z - prev.z) / 6,
  };
}

function computeSegmentCP2(
  pts: BuildPoint[],
  i: number,
  endControl: ControlPoint,
  waypointControls?: WaypointControls[],
): { x: number; z: number } {
  if (i === pts.length - 2 && endControl) return endControl;
  const wc = i < pts.length - 2 ? waypointControls?.[i]?.control_in : null;
  if (wc && wc.length >= 2) return { x: wc[0], z: wc[wc.length - 1] };
  const curr = pts[i];
  const next = pts[i + 1];
  const after = pts[Math.min(pts.length - 1, i + 2)];
  return {
    x: next.x - (after.x - curr.x) / 6,
    z: next.z - (after.z - curr.z) / 6,
  };
}

/** Sample one cubic Bézier segment in BEZIER_SAMPLES subdivisions and approximate arc length by polyline length. */
function cubicBezierLength(
  p0: { x: number; z: number },
  c1: { x: number; z: number },
  c2: { x: number; z: number },
  p1: { x: number; z: number },
): number {
  let length = 0;
  let prevX = p0.x;
  let prevZ = p0.z;
  for (let s = 1; s <= BEZIER_SAMPLES; s++) {
    const t = s / BEZIER_SAMPLES;
    const mt = 1 - t;
    const a = mt * mt * mt;
    const b = 3 * mt * mt * t;
    const c = 3 * mt * t * t;
    const d = t * t * t;
    const x = a * p0.x + b * c1.x + c * c2.x + d * p1.x;
    const z = a * p0.z + b * c1.z + c * c2.z + d * p1.z;
    length += Math.hypot(x - prevX, z - prevZ);
    prevX = x;
    prevZ = z;
  }
  return length;
}

/**
 * Return horizontal length (m) of the centerline being placed. null with fewer than two nodes.
 * For parallel tracks, return one centerline's length (individual tracks have nearly the same actual length).
 */
export function computePathLength(
  nodes: BuildPoint[],
  curveMode: CurveMode,
  startControl: ControlPoint,
  endControl: ControlPoint,
  waypointControls?: WaypointControls[],
): number | null {
  if (nodes.length < 2) return null;

  if (curveMode === "straight") {
    let length = 0;
    for (let i = 0; i < nodes.length - 1; i++) {
      length += Math.hypot(nodes[i + 1].x - nodes[i].x, nodes[i + 1].z - nodes[i].z);
    }
    return length;
  }

  // Curves: accumulate arc lengths as one cubic Bézier for two points, or per-segment cubic Béziers for three or more.
  if (nodes.length === 2) {
    const sc = startControl ?? nodes[0];
    const ec = endControl ?? nodes[1];
    return cubicBezierLength(nodes[0], sc, ec, nodes[1]);
  }

  let length = 0;
  for (let i = 0; i < nodes.length - 1; i++) {
    const cp1 = computeSegmentCP1(nodes, i, startControl, waypointControls);
    const cp2 = computeSegmentCP2(nodes, i, endControl, waypointControls);
    length += cubicBezierLength(nodes[i], cp1, cp2, nodes[i + 1]);
  }
  return length;
}
