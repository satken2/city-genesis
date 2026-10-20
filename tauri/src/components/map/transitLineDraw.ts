/**
 * 2D rendering for public transport lines (CityMapCanvas's transitLines layer).
 *
 * GET /api/v1/transit/lines/geometry supplies actual running geometry: [x, z] polylines (journeys) along underlying
 * roads/tracks, rather than straight links between stops. The backend extracts TransitLine.journeys
 * (running profiles between stops) from the underlying paths' Bézier geometry. Outbound and inbound journeys on
 * parallel single tracks therefore appear on physically separate tracks. On combined double-track paths
 * (including LRT), or street running such as buses, both directions overlap on the same path (arrows point both ways).
 *
 * Drawing rules:
 *   - Active lines: bands in line colors with white casings. Place a direction arrow at each inter-stop arc-length midpoint.
 *   - Suspended lines: gray dashed lines without arrows.
 *   - Lines without running geometry (no valid underlying route, empty journeys): gray dashed straight links between
 *     section path endpoints to indicate location only, not running geometry.
 *   - Stops: line-colored rings with white centers. Stops shared by multiple lines use dark gray rings.
 *   - Focused line (focusLineId): emphasize one line as in line detail maps. Dim other lines; draw the focused line last
 *     (in front), marking the first stop of its circuit with a green ring and turnback stops with red rings.
 *
 * Coordinates match CityMapCanvas's overlayDraw: world X-Z plane with north = +Z upward on screen.
 * Divide stroke widths, radii, and arrow dimensions by view.zoom for a consistent screen appearance.
 */

import { STATUS_ACTIVE } from "../../constants";
import { resolveTransitColor } from "../../local-constants";
import type { CityMapViewState } from "./CityMapCanvas";

/** One line from GET /api/v1/transit/lines/geometry. */
export type TransitLineGeometry = {
  line_agent_id: number;
  name: string | null;
  path_variant: number;
  status: number;
  /** Line color ("#rrggbb"). Empty means unset; fall back to the default for its type. */
  line_color: string;
  pause_reason: string | null;
  /** Stops, once each in circuit order (first visit for stops visited twice per circuit). turnback indicates whether vehicles reverse there. */
  stops: { node_id: number; position: [number, number]; name: string | null; turnback: boolean }[];
  /** Running geometry between stops in circuit order. turnback indicates reversal at the section's departure stop. */
  journeys: { turnback: boolean; points: [number, number][] }[];
  /** Straight links between section path endpoints, used only to locate lines lacking running geometry. */
  segments: { from: [number, number]; to: [number, number]; status: number }[];
};

/** Band casings and stop centers use white to remain visible over terrain/buildings. */
const COLOR_OUTLINE = "#ffffff";
/** Color for suspended lines and lines without running geometry. */
const COLOR_INACTIVE = "#7c8389";
/** Ring color for stops shared by multiple lines. */
const COLOR_SHARED_STOP = "#556773";
/** Ring colors for the focused line's first and turnback stops. */
const COLOR_FOCUS_START = "#18845b";
const COLOR_FOCUS_END = "#cf4b50";

/** Opacity of other lines when a focused line is present. */
const DIMMED_ALPHA = 0.3;
/** Minimum screen length (px) for direction arrows. Arrows become unreadable on short sections. */
const ARROW_MIN_SCREEN_LEN_PX = 60;

/** Resolve line color, using type defaults for unset (empty) colors. */
export function effectiveTransitLineColor(line: Pick<TransitLineGeometry, "line_color" | "path_variant">): string {
  return line.line_color && line.line_color !== "" ? line.line_color : resolveTransitColor(line.path_variant);
}

/** Append a polyline to the current path (moveTo followed by lineTo commands). */
function tracePolyline(ctx: CanvasRenderingContext2D, points: [number, number][]): void {
  ctx.beginPath();
  for (let i = 0; i < points.length; i++) {
    const [x, z] = points[i];
    if (i === 0) ctx.moveTo(x, z);
    else ctx.lineTo(x, z);
  }
}

/** Return a polyline's arc-length midpoint and travel-direction unit vector there. Return null for zero length. */
function polylineMidpoint(
  points: [number, number][]
): { x: number; z: number; ux: number; uz: number; length: number } | null {
  let total = 0;
  const seg: number[] = [];
  for (let i = 1; i < points.length; i++) {
    const d = Math.hypot(points[i][0] - points[i - 1][0], points[i][1] - points[i - 1][1]);
    seg.push(d);
    total += d;
  }
  if (total < 1e-6) return null;
  let remain = total / 2;
  for (let i = 0; i < seg.length; i++) {
    if (remain > seg[i]) {
      remain -= seg[i];
      continue;
    }
    const [ax, az] = points[i];
    const [bx, bz] = points[i + 1];
    const ux = (bx - ax) / seg[i];
    const uz = (bz - az) / seg[i];
    return { x: ax + ux * remain, z: az + uz * remain, ux, uz, length: total };
  }
  const last = points[points.length - 1];
  return { x: last[0], z: last[1], ux: 1, uz: 0, length: total };
}

/** Direction arrow (a symmetric triangle centered at (x,z), pointing along (ux,uz)). */
function drawArrow(
  ctx: CanvasRenderingContext2D,
  px: (w: number) => number,
  x: number,
  z: number,
  ux: number,
  uz: number,
  color: string
): void {
  const halfLen = px(5);
  const halfWidth = px(3.5);
  const rx = -uz;
  const rz = ux;
  const baseX = x - ux * halfLen;
  const baseZ = z - uz * halfLen;
  ctx.fillStyle = color;
  ctx.beginPath();
  ctx.moveTo(x + ux * halfLen, z + uz * halfLen);
  ctx.lineTo(baseX + rx * halfWidth, baseZ + rz * halfWidth);
  ctx.lineTo(baseX - rx * halfWidth, baseZ - rz * halfWidth);
  ctx.closePath();
  ctx.fill();
}

/** Stop ring (outer fill color, inner white center). */
function drawStopMarker(
  ctx: CanvasRenderingContext2D,
  px: (w: number) => number,
  x: number,
  z: number,
  fill: string,
  ringR: number,
  innerR: number
): void {
  ctx.fillStyle = fill;
  ctx.strokeStyle = COLOR_OUTLINE;
  ctx.lineWidth = px(1.5);
  ctx.setLineDash([]);
  ctx.beginPath();
  ctx.arc(x, z, ringR, 0, Math.PI * 2);
  ctx.fill();
  ctx.stroke();
  ctx.fillStyle = COLOR_OUTLINE;
  ctx.beginPath();
  ctx.arc(x, z, innerR, 0, Math.PI * 2);
  ctx.fill();
}

/**
 * Draw running geometry and stops for all lines.
 * @param focusLineId line_agent_id of the focused line. When specified, draw it in front and dim other lines.
 */
export function drawTransitLines(
  ctx: CanvasRenderingContext2D,
  view: CityMapViewState,
  lines: TransitLineGeometry[],
  focusLineId?: number | null
): void {
  const px = (w: number) => w / view.zoom;
  const hasFocus = focusLineId != null;
  // Draw the focused line last to put it in front.
  const ordered = hasFocus
    ? [...lines.filter((l) => l.line_agent_id !== focusLineId), ...lines.filter((l) => l.line_agent_id === focusLineId)]
    : lines;

  ctx.save();
  ctx.lineJoin = "round";
  ctx.lineCap = "round";

  for (const line of ordered) {
    const dimmed = hasFocus && line.line_agent_id !== focusLineId;
    const active = line.status === STATUS_ACTIVE;
    const color = effectiveTransitLineColor(line);
    ctx.globalAlpha = dimmed ? DIMMED_ALPHA : 1;

    const journeys = line.journeys.filter((j) => j.points.length >= 2);
    if (journeys.length === 0) {
      // No running geometry: draw section path straight links dashed to indicate location only.
      ctx.strokeStyle = COLOR_INACTIVE;
      ctx.lineWidth = px(2);
      ctx.setLineDash([px(5), px(4)]);
      for (const s of line.segments) {
        ctx.beginPath();
        ctx.moveTo(s.from[0], s.from[1]);
        ctx.lineTo(s.to[0], s.to[1]);
        ctx.stroke();
      }
      ctx.setLineDash([]);
      continue;
    }

    for (const j of journeys) {
      if (active) {
        tracePolyline(ctx, j.points);
        ctx.setLineDash([]);
        ctx.strokeStyle = COLOR_OUTLINE;
        ctx.lineWidth = px(7);
        ctx.stroke();
        ctx.strokeStyle = color;
        ctx.lineWidth = px(4);
        ctx.stroke();
      } else {
        tracePolyline(ctx, j.points);
        ctx.strokeStyle = COLOR_INACTIVE;
        ctx.lineWidth = px(4);
        ctx.setLineDash([px(5), px(4)]);
        ctx.stroke();
        ctx.setLineDash([]);
      }
    }
    // Direction arrows. Reduce detail on dimmed lines so they do not distract from the focused line.
    if (active && !dimmed) {
      for (const j of journeys) {
        const mid = polylineMidpoint(j.points);
        if (!mid || mid.length * view.zoom < ARROW_MIN_SCREEN_LEN_PX) continue;
        drawArrow(ctx, px, mid.x, mid.z, mid.ux, mid.uz, color);
      }
    }
  }

  // Draw stops above all line bands. Draw shared stops once, indicating sharing by color.
  type StopMark = { x: number; z: number; colors: Set<string>; dimmed: boolean; focusFill: string | null };
  const marks = new Map<number, StopMark>();
  for (const line of ordered) {
    const dimmed = hasFocus && line.line_agent_id !== focusLineId;
    const color = effectiveTransitLineColor(line);
    line.stops.forEach((s, i) => {
      let mark = marks.get(s.node_id);
      if (!mark) {
        mark = { x: s.position[0], z: s.position[1], colors: new Set(), dimmed: true, focusFill: null };
        marks.set(s.node_id, mark);
      }
      mark.colors.add(color);
      if (!dimmed) mark.dimmed = false;
      if (hasFocus && !dimmed) {
        // Focused circuit: first stop green, turnback stops red (do not highlight lines with only one stop).
        const multi = line.stops.length > 1;
        mark.focusFill = multi && i === 0 ? COLOR_FOCUS_START : multi && s.turnback ? COLOR_FOCUS_END : color;
      }
    });
  }
  for (const mark of marks.values()) {
    ctx.globalAlpha = mark.dimmed ? DIMMED_ALPHA : 1;
    const fill = mark.focusFill ?? (mark.colors.size === 1 ? [...mark.colors][0] : COLOR_SHARED_STOP);
    const ringR = mark.dimmed ? px(4) : px(6);
    const innerR = mark.dimmed ? px(2) : px(3.5);
    drawStopMarker(ctx, px, mark.x, mark.z, fill, ringR, innerR);
  }

  ctx.restore();
}
