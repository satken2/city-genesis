/**
 * Drawing and hit testing for PathShapeEditorPanel (pure functions independent of React).
 *
 * Expand edit_path:update_shape into anchors and each anchor's incoming/outgoing controls, then draw through
 * CityMapCanvas's overlayDraw (world-coordinate CTM applied, +Z = north points upward).
 * Convert screen-constant dimensions to world units with px / view.zoom.
 * For screen-pixel drawing such as text, cancel CTM scaling and Y flip (withScreenSpace).
 *
 * Do not complete Bézier controls here. Unity sends all controls in curve mode, so draw received values directly
 * to match the backend/Unity shape. Straight mode connects points with straight lines.
 */

import type { CityMapViewState } from "../map/CityMapCanvas";
import type { PathAlignmentSyncPayload, PathMaxSpanViolation, PathShapeUpdatePayload } from "./pathBuildConstants";

export type XZ = { x: number; z: number };

/** Expanded shape for drawing/hit testing. All arrays follow anchor order. */
export type PathShapeGeometry = {
  anchors: XZ[];
  /** Height relative to ground (m). */
  relHeights: number[];
  /** Embankment offset (m). */
  embankments: number[];
  /** Outgoing control (P1 of the next segment). null at the end and in straight mode. */
  controlOut: (XZ | null)[];
  /** Incoming control (P2 of the previous segment). null at the start and in straight mode. */
  controlIn: (XZ | null)[];
  curved: boolean;
};

/** Hit test result, prioritized as controls > nodes > edges. */
export type ShapeHit =
  | { type: "control"; index: number; which: "in" | "out" }
  | { type: "node"; index: number }
  | { type: "edge"; index: number; world: XZ };

/** Drag preview. hit is the drag target; world is the cursor's world coordinate. */
export type ShapeDrag = { hit: ShapeHit; world: XZ };

/** Hit tolerance radius (screen px), close to Unity RoadOverlayHitDetector (14 px for points, 10 px for edges). */
export const SHAPE_HIT_OPTIONS = { nodeRadiusPx: 12, controlRadiusPx: 10, edgeDistancePx: 8 };

/** Bézier subdivisions per segment. */
const BEZIER_SUBDIVISIONS = 24;

/* ---- Palette (similar to city map feature colors) ---- */
const COLOR_CENTERLINE = "#89b4fa";
const COLOR_CENTERLINE_EDGE = "#11111b";
const COLOR_LATERAL = "rgba(137, 180, 250, 0.45)";
const COLOR_HANDLE_LINE = "#7c3aed";
const COLOR_CONTROL = "#cba6f7";
const COLOR_NODE_START = "#a6e3a1";
const COLOR_NODE_END = "#f38ba8";
const COLOR_NODE_WAYPOINT = "#f5f5f5";
const COLOR_NODE_EDGE = "#1e1e2e";
const COLOR_SELECTED = "#4ea1ff";
const COLOR_HOVER = "#f9e2af";
const COLOR_TEXT = "#ffffff";
const COLOR_TEXT_BG = "rgba(8, 8, 8, 0.85)";
const COLOR_ALIGN_WARN = "#f9e2af";
const COLOR_ALIGN_NEAR = "#fab387";
const COLOR_ALIGN_BAD = "#f38ba8";
const COLOR_LABEL_CURVE = "#ff8c40";
const COLOR_LABEL_GRADE = "#ffcc33";

/* Shape expansion */

function toXZ(v: number[] | null | undefined): XZ | null {
  if (!v || v.length < 2) return null;
  // Accept both [x, z] and [x, y, z], matching Unity's reading convention.
  return { x: v[0], z: v[v.length - 1] };
}

/** Expand the update_shape payload. Return an empty shape if no points exist. */
export function buildShapeGeometry(p: PathShapeUpdatePayload | null): PathShapeGeometry {
  const empty: PathShapeGeometry = {
    anchors: [], relHeights: [], embankments: [], controlOut: [], controlIn: [], curved: p?.is_curved === true,
  };
  const pts = p?.selected_points;
  if (!p || !pts || pts.length === 0) return empty;

  const anchors = pts.map((pt) => ({ x: pt[0], z: pt[2] ?? pt[1] }));
  const relHeights = pts.map((pt) => (pt.length >= 3 ? pt[1] : 0));
  const embankments = anchors.map((_, i) => p.embankment_heights?.[i] ?? 0);
  const n = anchors.length;
  const controlOut: (XZ | null)[] = new Array(n).fill(null);
  const controlIn: (XZ | null)[] = new Array(n).fill(null);
  const curved = p.is_curved === true;
  if (curved && n >= 2) {
    controlOut[0] = toXZ(p.start_control);
    controlIn[n - 1] = toXZ(p.end_control);
    for (let i = 1; i < n - 1; i++) {
      const wc = p.waypoint_controls?.[i - 1];
      controlIn[i] = toXZ(wc?.control_in);
      controlOut[i] = toXZ(wc?.control_out);
    }
  }
  return { anchors, relHeights, embankments, controlOut, controlIn, curved };
}

/**
 * Copy the shape with only the drag target moved for preview.
 * Match Unity: moving a node translates both controls by the same amount; moving a control keeps the opposite
 * control collinear through the vertex while preserving its length.
 */
export function applyDragPreview(geom: PathShapeGeometry, drag: ShapeDrag | null): PathShapeGeometry {
  if (!drag || drag.hit.type === "edge") return geom;
  const g: PathShapeGeometry = {
    ...geom,
    anchors: geom.anchors.slice(),
    controlOut: geom.controlOut.slice(),
    controlIn: geom.controlIn.slice(),
  };
  const i = drag.hit.index;
  if (i < 0 || i >= g.anchors.length) return geom;
  if (drag.hit.type === "node") {
    const dx = drag.world.x - g.anchors[i].x;
    const dz = drag.world.z - g.anchors[i].z;
    g.anchors[i] = { x: drag.world.x, z: drag.world.z };
    const co = g.controlOut[i];
    const ci = g.controlIn[i];
    if (co) g.controlOut[i] = { x: co.x + dx, z: co.z + dz };
    if (ci) g.controlIn[i] = { x: ci.x + dx, z: ci.z + dz };
    return g;
  }
  const anchor = g.anchors[i];
  const moved = { x: drag.world.x, z: drag.world.z };
  const opposite = drag.hit.which === "in" ? g.controlOut[i] : g.controlIn[i];
  if (drag.hit.which === "in") g.controlIn[i] = moved; else g.controlOut[i] = moved;
  if (opposite) {
    const len = Math.hypot(opposite.x - anchor.x, opposite.z - anchor.z);
    const dirLen = Math.hypot(moved.x - anchor.x, moved.z - anchor.z);
    if (dirLen > 1e-6) {
      const ux = (moved.x - anchor.x) / dirLen;
      const uz = (moved.z - anchor.z) / dirLen;
      const mirrored = { x: anchor.x - ux * len, z: anchor.z - uz * len };
      if (drag.hit.which === "in") g.controlOut[i] = mirrored; else g.controlIn[i] = mirrored;
    }
  }
  return g;
}

/**
 * Optimistic update at drag end. Reflect the moved target in the received payload to prevent the display
 * reverting before Unity's update_shape arrives.
 */
export function applyLocalMove(p: PathShapeUpdatePayload, hit: ShapeHit, world: XZ): PathShapeUpdatePayload {
  if (hit.type === "edge" || !p.selected_points) return p;
  const n = p.selected_points.length;
  const i = hit.index;
  if (i < 0 || i >= n) return p;
  const setXZ = (v: number[] | null | undefined, dx: number, dz: number): number[] | null | undefined =>
    v && v.length >= 2 ? [v[0] + dx, ...(v.length >= 3 ? [v[1]] : []), v[v.length - 1] + dz] : v;
  const next: PathShapeUpdatePayload = {
    ...p,
    alignment: undefined,
    selected_points: p.selected_points.map((pt) => pt.slice()),
    waypoint_controls: p.waypoint_controls?.map((w) => ({ ...w })),
  };
  if (hit.type === "node") {
    const pt = next.selected_points![i];
    const dx = world.x - pt[0];
    const dz = world.z - pt[pt.length - 1];
    pt[0] = world.x;
    pt[pt.length - 1] = world.z;
    if (i === 0) next.start_control = setXZ(next.start_control, dx, dz) ?? undefined;
    if (i === n - 1) next.end_control = setXZ(next.end_control, dx, dz) ?? undefined;
    if (i > 0 && i < n - 1 && next.waypoint_controls?.[i - 1]) {
      const wc = next.waypoint_controls[i - 1];
      wc.control_in = setXZ(wc.control_in, dx, dz);
      wc.control_out = setXZ(wc.control_out, dx, dz);
    }
    return next;
  }
  const replace = (v: number[] | null | undefined): number[] =>
    v && v.length >= 3 ? [world.x, v[1], world.z] : [world.x, 0, world.z];
  if (hit.which === "out" && i === 0) next.start_control = replace(next.start_control);
  else if (hit.which === "in" && i === n - 1) next.end_control = replace(next.end_control);
  else if (i > 0 && i < n - 1) {
    const wcs = next.waypoint_controls ?? [];
    while (wcs.length < n - 2) wcs.push({ control_in: null, control_out: null });
    if (hit.which === "in") wcs[i - 1].control_in = replace(wcs[i - 1].control_in);
    else wcs[i - 1].control_out = replace(wcs[i - 1].control_out);
    next.waypoint_controls = wcs;
  }
  return next;
}

/* Polyline conversion */

/** Points for segment i (anchors[i] → anchors[i+1]). A cubic Bézier in curve mode when both controls exist, otherwise a straight line. */
export function segmentPoints(geom: PathShapeGeometry, i: number): XZ[] {
  const a = geom.anchors[i];
  const b = geom.anchors[i + 1];
  const c1 = geom.curved ? geom.controlOut[i] : null;
  const c2 = geom.curved ? geom.controlIn[i + 1] : null;
  if (!a || !b) return [];
  if (!c1 || !c2) return [a, b];
  const out: XZ[] = [];
  for (let k = 0; k <= BEZIER_SUBDIVISIONS; k++) {
    const t = k / BEZIER_SUBDIVISIONS;
    const u = 1 - t;
    const w0 = u * u * u, w1 = 3 * u * u * t, w2 = 3 * u * t * t, w3 = t * t * t;
    out.push({
      x: w0 * a.x + w1 * c1.x + w2 * c2.x + w3 * b.x,
      z: w0 * a.z + w1 * c1.z + w2 * c2.z + w3 * b.z,
    });
  }
  return out;
}

/** Points for the entire path, excluding duplicates at segment boundaries. */
export function shapePolyline(geom: PathShapeGeometry): XZ[] {
  const out: XZ[] = [];
  for (let i = 0; i < geom.anchors.length - 1; i++) {
    const seg = segmentPoints(geom, i);
    for (let k = i === 0 ? 0 : 1; k < seg.length; k++) out.push(seg[k]);
  }
  if (geom.anchors.length === 1) out.push(geom.anchors[0]);
  return out;
}

/** Points offset by offset (m) to the right of travel (approximation for parallel-track previews). */
function offsetPolyline(points: XZ[], offset: number): XZ[] {
  const n = points.length;
  if (n < 2 || offset === 0) return points;
  const out: XZ[] = [];
  for (let i = 0; i < n; i++) {
    const p0 = points[Math.max(0, i - 1)];
    const p1 = points[Math.min(n - 1, i + 1)];
    const dx = p1.x - p0.x, dz = p1.z - p0.z;
    const len = Math.hypot(dx, dz) || 1;
    // The right normal of travel direction (dx,dz) is (dz,-dx) (x = east, z = north; same convention as Unity path placement).
    out.push({ x: points[i].x + (dz / len) * offset, z: points[i].z - (dx / len) * offset });
  }
  return out;
}

/* Coordinate conversion and hit testing */

export function worldToScreen(view: CityMapViewState, p: XZ): { x: number; y: number } {
  return { x: (p.x - view.offsetX) * view.zoom, y: (view.offsetY - p.z) * view.zoom };
}

export function screenToWorld(view: CityMapViewState, s: { x: number; y: number }): XZ {
  return { x: s.x / view.zoom + view.offsetX, z: view.offsetY - s.y / view.zoom };
}

/**
 * Screen-coordinate hit testing, prioritized as controls (curve mode only) → nodes → edges.
 * For edge hits, return the nearest point on the edge in world coordinates for waypoint insertion.
 */
export function hitTestShape(
  geom: PathShapeGeometry,
  screen: { x: number; y: number },
  view: CityMapViewState,
  opts: typeof SHAPE_HIT_OPTIONS = SHAPE_HIT_OPTIONS,
): ShapeHit | null {
  const n = geom.anchors.length;
  if (n === 0) return null;
  const dist = (p: XZ) => {
    const s = worldToScreen(view, p);
    return Math.hypot(s.x - screen.x, s.y - screen.y);
  };

  if (geom.curved) {
    let best: ShapeHit | null = null;
    let bestD = opts.controlRadiusPx;
    for (let i = 0; i < n; i++) {
      const co = geom.controlOut[i];
      const ci = geom.controlIn[i];
      if (co) { const d = dist(co); if (d < bestD) { bestD = d; best = { type: "control", index: i, which: "out" }; } }
      if (ci) { const d = dist(ci); if (d < bestD) { bestD = d; best = { type: "control", index: i, which: "in" }; } }
    }
    if (best) return best;
  }

  let bestNode = -1;
  let bestNodeD = opts.nodeRadiusPx;
  for (let i = 0; i < n; i++) {
    const d = dist(geom.anchors[i]);
    if (d < bestNodeD) { bestNodeD = d; bestNode = i; }
  }
  if (bestNode >= 0) return { type: "node", index: bestNode };

  let bestEdge: ShapeHit | null = null;
  let bestEdgeD = opts.edgeDistancePx;
  for (let i = 0; i < n - 1; i++) {
    const seg = segmentPoints(geom, i);
    for (let k = 0; k < seg.length - 1; k++) {
      const a = worldToScreen(view, seg[k]);
      const b = worldToScreen(view, seg[k + 1]);
      const abx = b.x - a.x, aby = b.y - a.y;
      const len2 = abx * abx + aby * aby;
      const t = len2 > 0 ? Math.max(0, Math.min(1, ((screen.x - a.x) * abx + (screen.y - a.y) * aby) / len2)) : 0;
      const cx = a.x + abx * t, cy = a.y + aby * t;
      const d = Math.hypot(screen.x - cx, screen.y - cy);
      if (d < bestEdgeD) {
        bestEdgeD = d;
        bestEdge = { type: "edge", index: i, world: screenToWorld(view, { x: cx, y: cy }) };
      }
    }
  }
  return bestEdge;
}

export function sameHit(a: ShapeHit | null, b: ShapeHit | null): boolean {
  if (a === b) return true;
  if (!a || !b || a.type !== b.type || a.index !== b.index) return false;
  if (a.type === "control" && b.type === "control") return a.which === b.which;
  return true;
}

/** Alignment sample nearest the cursor within maxPx screen pixels. Used for hover radius/grade display. */
export function nearestAlignmentSample(
  alignment: PathAlignmentSyncPayload | undefined,
  screen: { x: number; y: number },
  view: CityMapViewState,
  maxPx: number,
): { radius: number | null; grade: number } | null {
  if (!alignment) return null;
  let best: { radius: number | null; grade: number } | null = null;
  let bestD = maxPx;
  for (const s of alignment.samples) {
    const p = worldToScreen(view, { x: s[0], z: s[1] });
    const d = Math.hypot(p.x - screen.x, p.y - screen.y);
    if (d < bestD) { bestD = d; best = { radius: s[2], grade: s[3] }; }
  }
  return best;
}

/** Padded bounding rectangle of the point sequence, passed to fitBounds. */
export function shapeBounds(geom: PathShapeGeometry, paddingRatio: number, minPadding: number) {
  const pts: XZ[] = [...geom.anchors];
  for (const c of geom.controlIn) if (c) pts.push(c);
  for (const c of geom.controlOut) if (c) pts.push(c);
  if (pts.length === 0) return null;
  let x1 = Infinity, z1 = Infinity, x2 = -Infinity, z2 = -Infinity;
  for (const p of pts) { x1 = Math.min(x1, p.x); z1 = Math.min(z1, p.z); x2 = Math.max(x2, p.x); z2 = Math.max(z2, p.z); }
  const pad = Math.max(minPadding, Math.max(x2 - x1, z2 - z1) * paddingRatio);
  return { x1: x1 - pad, z1: z1 - pad, x2: x2 + pad, z2: z2 + pad };
}

/* Drawing */

/** Alignment severity (0 = ample margin, 1 = exactly at limit, >1 = violation), taking the worse of radius and grade. */
function alignmentSeverity(a: PathAlignmentSyncPayload, radius: number | null, grade: number): number {
  let s = 0;
  if (a.radius_limit > 0 && radius != null && radius > 0) s = Math.max(s, a.radius_limit / radius);
  if (a.grade_limit > 0) s = Math.max(s, grade / a.grade_limit);
  return s;
}

/** Color by severity. null if comfortably within the standard, retaining the centerline color. */
export function alignmentColor(severity: number): string | null {
  if (severity >= 1) return COLOR_ALIGN_BAD;
  if (severity >= 0.8) return COLOR_ALIGN_NEAR;
  if (severity >= 0.6) return COLOR_ALIGN_WARN;
  return null;
}

export type ShapeDrawOptions = {
  hover: ShapeHit | null;
  drag: ShapeDrag | null;
  selectedIndex: number;
  lateralOffsets: number[];
  alignment?: PathAlignmentSyncPayload;
  maxSpanViolations?: PathMaxSpanViolation[];
  spanLabelLines?: (violation: PathMaxSpanViolation) => string[];
  /** Section label text by line. The parent supplies localized strings. */
  labelLines: (label: PathAlignmentSyncPayload["labels"][number]) => string[];
  /** Height text beside a node. null to hide. */
  heightText: (relHeight: number, embankment: number) => string | null;
};

/** Draw text, etc. in screen px with Y down, using world (wx,wz) as the origin (cancel CTM scaling and Y flip). */
function withScreenSpace(
  ctx: CanvasRenderingContext2D,
  view: CityMapViewState,
  wx: number,
  wz: number,
  fn: (ctx: CanvasRenderingContext2D) => void,
) {
  ctx.save();
  ctx.translate(wx, wz);
  ctx.scale(1 / view.zoom, -1 / view.zoom);
  fn(ctx);
  ctx.restore();
}

function strokePolyline(ctx: CanvasRenderingContext2D, pts: XZ[]) {
  if (pts.length < 2) return;
  ctx.beginPath();
  ctx.moveTo(pts[0].x, pts[0].z);
  for (let i = 1; i < pts.length; i++) ctx.lineTo(pts[i].x, pts[i].z);
  ctx.stroke();
}

/** Draw the placed path's shape (centerline, parallel tracks, controls, nodes, alignment colors and labels). */
export function drawPathShape(
  ctx: CanvasRenderingContext2D,
  view: CityMapViewState,
  baseGeom: PathShapeGeometry,
  opts: ShapeDrawOptions,
) {
  const geom = applyDragPreview(baseGeom, opts.drag);
  const n = geom.anchors.length;
  if (n === 0) return;
  const px = (v: number) => v / view.zoom;
  ctx.save();
  ctx.setLineDash([]);
  ctx.lineCap = "round";
  ctx.lineJoin = "round";

  const line = shapePolyline(geom);

  // Parallel tracks (positive offsets are rightward from the centerline).
  for (const off of opts.lateralOffsets) {
    if (off === 0) continue;
    ctx.strokeStyle = COLOR_LATERAL;
    ctx.lineWidth = px(2);
    strokePolyline(ctx, offsetPolyline(line, off));
  }

  // Centerline with casing.
  ctx.strokeStyle = COLOR_CENTERLINE_EDGE;
  ctx.lineWidth = px(5);
  strokePolyline(ctx, line);
  ctx.strokeStyle = COLOR_CENTERLINE;
  ctx.lineWidth = px(3);
  strokePolyline(ctx, line);

  // Alignment colors (omit during dragging before Unity reevaluates; updated by the response after drag end).
  const alignment = opts.alignment;
  if (alignment?.authoritative && !opts.drag) {
    ctx.lineWidth = px(4);
    ctx.strokeStyle = "#ff3333";
    for (const run of alignment.warnings ?? []) {
      strokePolyline(ctx, run.map((p) => ({ x: p[0], z: p[2] })));
    }
  } else if (alignment && !opts.drag && alignment.samples.length >= 2) {
    ctx.lineWidth = px(4);
    const samples = alignment.samples;
    for (let i = 0; i < samples.length - 1; i++) {
      const a = samples[i], b = samples[i + 1];
      const sev = Math.max(alignmentSeverity(alignment, a[2], a[3]), alignmentSeverity(alignment, b[2], b[3]));
      const color = alignmentColor(sev);
      if (!color) continue;
      ctx.strokeStyle = color;
      ctx.beginPath();
      ctx.moveTo(a[0], a[1]);
      ctx.lineTo(b[0], b[1]);
      ctx.stroke();
    }
  }

  // Controls and handle lines (curve mode only).
  if (geom.curved) {
    ctx.strokeStyle = COLOR_HANDLE_LINE;
    ctx.lineWidth = px(1);
    ctx.setLineDash([px(4), px(3)]);
    for (let i = 0; i < n; i++) {
      const a = geom.anchors[i];
      for (const c of [geom.controlIn[i], geom.controlOut[i]]) {
        if (!c) continue;
        ctx.beginPath();
        ctx.moveTo(a.x, a.z);
        ctx.lineTo(c.x, c.z);
        ctx.stroke();
      }
    }
    ctx.setLineDash([]);
    for (let i = 0; i < n; i++) {
      (["in", "out"] as const).forEach((which) => {
        const c = which === "in" ? geom.controlIn[i] : geom.controlOut[i];
        if (!c) return;
        const hot =
          (opts.drag?.hit.type === "control" && opts.drag.hit.index === i && opts.drag.hit.which === which) ||
          (opts.hover?.type === "control" && opts.hover.index === i && opts.hover.which === which);
        const half = px(hot ? 6 : 4.5);
        ctx.fillStyle = hot ? COLOR_HOVER : COLOR_CONTROL;
        ctx.strokeStyle = COLOR_NODE_EDGE;
        ctx.lineWidth = px(1);
        ctx.beginPath();
        ctx.rect(c.x - half, c.z - half, half * 2, half * 2);
        ctx.fill();
        ctx.stroke();
      });
    }
  }

  // Nodes (start green, end red, intermediate white; selected blue ring, hovered yellow).
  for (let i = 0; i < n; i++) {
    const a = geom.anchors[i];
    const hot = (opts.drag?.hit.type === "node" && opts.drag.hit.index === i)
      || (opts.hover?.type === "node" && opts.hover.index === i);
    const selected = opts.selectedIndex === i;
    const r = px(hot ? 8 : 6);
    if (selected) {
      ctx.beginPath();
      ctx.arc(a.x, a.z, r + px(4), 0, Math.PI * 2);
      ctx.strokeStyle = COLOR_SELECTED;
      ctx.lineWidth = px(2.5);
      ctx.stroke();
    }
    ctx.beginPath();
    ctx.arc(a.x, a.z, r, 0, Math.PI * 2);
    ctx.fillStyle = hot ? COLOR_HOVER : i === 0 ? COLOR_NODE_START : i === n - 1 ? COLOR_NODE_END : COLOR_NODE_WAYPOINT;
    ctx.fill();
    ctx.strokeStyle = COLOR_NODE_EDGE;
    ctx.lineWidth = px(1.5);
    ctx.stroke();

    // Place one-based node numbers at upper right and heights to the right (numbers shared with the profile strip and Unity 3D labels).
    const text = opts.heightText(geom.relHeights[i] ?? 0, geom.embankments[i] ?? 0);
    withScreenSpace(ctx, view, a.x, a.z, (c) => {
      c.font = "11px Consolas, Menlo, monospace";
      c.textBaseline = "middle";
      const num = `${i + 1}`;
      const nw = Math.max(18, c.measureText(num).width + 8);
      c.fillStyle = selected ? COLOR_SELECTED : COLOR_TEXT_BG;
      c.fillRect(10, -26, nw, 16);
      c.fillStyle = COLOR_TEXT;
      c.textAlign = "center";
      c.fillText(num, 10 + nw / 2, -18);
      c.textAlign = "left";
      if (text) {
        const w = c.measureText(text).width + 8;
        c.fillStyle = COLOR_TEXT_BG;
        c.fillRect(10, -7, w, 16);
        c.fillStyle = COLOR_TEXT;
        c.fillText(text, 14, 1);
      }
    });
  }

  // Edge hover: insertion position preview.
  if (opts.hover?.type === "edge" && !opts.drag) {
    const w = opts.hover.world;
    ctx.beginPath();
    ctx.arc(w.x, w.z, px(5), 0, Math.PI * 2);
    ctx.fillStyle = COLOR_HOVER;
    ctx.fill();
    ctx.strokeStyle = COLOR_NODE_EDGE;
    ctx.lineWidth = px(1);
    ctx.stroke();
  }

  // Show excessive distance with the same leader-line labels as alignment violations, linked to the affected section.
  if (!opts.drag) {
    const labels = (alignment?.labels ?? []).map((label) => ({
      x: label.x, z: label.z, lines: opts.labelLines(label), accent: label.curve ? COLOR_LABEL_CURVE : COLOR_LABEL_GRADE,
    }));
    for (const violation of opts.maxSpanViolations ?? []) {
      const segment = segmentPoints(geom, violation.segment_index);
      if (segment.length < 2 || !opts.spanLabelLines) continue;
      const middle = (segment.length - 1) / 2;
      const a = segment[Math.floor(middle)], b = segment[Math.ceil(middle)];
      labels.push({
        x: (a.x + b.x) / 2, z: (a.z + b.z) / 2,
        lines: opts.spanLabelLines(violation), accent: COLOR_ALIGN_BAD,
      });
    }
    labels.forEach((label, idx) => {
      const { lines, accent } = label;
      ctx.beginPath();
      ctx.arc(label.x, label.z, px(3.5), 0, Math.PI * 2);
      ctx.fillStyle = accent;
      ctx.fill();
      withScreenSpace(ctx, view, label.x, label.z, (c) => {
        c.font = "11px Consolas, Menlo, monospace";
        c.textBaseline = "top";
        const lineH = 14;
        const w = Math.max(...lines.map((l) => c.measureText(l).width)) + 14;
        const h = lines.length * lineH + 8;
        // Shift labels downward when they share a position to avoid overlap.
        const ox = 16;
        const oy = -h - 10 + (idx % 2) * (h + 20);
        c.strokeStyle = accent;
        c.lineWidth = 1.5;
        c.beginPath();
        c.moveTo(0, 0);
        c.lineTo(ox, oy + h / 2);
        c.stroke();
        c.fillStyle = COLOR_TEXT_BG;
        c.fillRect(ox, oy, w, h);
        c.fillStyle = accent;
        c.fillRect(ox, oy, 3, h);
        c.fillStyle = COLOR_TEXT;
        lines.forEach((l, li) => c.fillText(l, ox + 9, oy + 4 + li * lineH));
      });
    });
  }

  ctx.restore();
}
