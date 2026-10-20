/**
 * Road strip map (vertical diagram in the road detail Strip map tab).
 *
 * Draw the road from origin to endpoint, top to bottom, using chainage rather than geographic bends.
 *   - Left: chainage ticks and small signs where speed limits change.
 *   - Center: section lines with width = lanes and color = congestion (green/yellow/red at 50%/80%).
 *     Planned/under-construction sections are gray dashed; sections without capacity are gray.
 *     Parallel directional sections lie right of the baseline, with direction arrows for one-way sections.
 *     Casings on both sides indicate tunnels/bridges.
 *   - Center-right: crossing road names (road-colored if named, clickable for details), IC/JCT, stops, section names,
 *     disconnected component gaps ("Disconnected, about ... m"), and branches.
 *   - Right: traffic bars and vehicles/day plus congestion % per section (parallel sections have their own rows).
 *
 * Stack branches as label rows at their attachment chainage (backend attach_km, or the origin-side end of the projected
 * baseline range). Each row has a connector, a short horizontal bar proportional to section length, and traffic.
 * Branches nearly perpendicular to the baseline have almost zero projected range; vertical drawing would collapse them
 * to points and make branches at the same chainage overlap indistinguishably.
 *
 * Scale vertically by chainage, stretching piecewise linearly to keep minimum spacing between adjacent markers
 * (events, section boundaries, midpoints), so text remains separate even at dense intersections on short sections.
 * Compress gaps between components to a fixed length regardless of actual distance. Tick spacing is therefore uneven,
 * but tick values still indicate correct chainage. Clicking a section opens path_detail.
 */

import { useEffect, useMemo, useRef, useState, type RefObject } from "react";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";
import { invoke } from "@tauri-apps/api/core";
import {
  PATH_MOTORWAY,
  PATH_TRACK,
  STATUS_ACTIVE,
} from "../../constants";
import { resolveIntlLocale } from "../../utils/locale";
import { interchangeDisplayName } from "../intersection/interchangeName";
import { roadDisplayName } from "./roadApi";
import type { RoadCrossing, RoadDetail, RoadSegment } from "./roadTypes";
import {
  ROAD_COLOR_INACTIVE,
  ROAD_COLOR_NO_CAPACITY,
  ROAD_COLOR_VC_HIGH,
  ROAD_COLOR_VC_LOW,
  ROAD_COLOR_VC_MID,
  formatInt,
  formatKm,
  formatPercentValue,
  segmentColor,
} from "./roadFormat";
import "./RoadStripMap.css";

const DEFAULT_WIDTH = 440;
const TOP = 30;
const BOTTOM = 18;
const AXIS_X = 32;
const SIGN_X = 46;
const LINE_X = 76;
const PARALLEL_DX = 8;
const LABEL_X = 118;
const BRANCH_BAR_X = LINE_X + 10;
const BRANCH_BAR_MAX = LABEL_X - 6 - BRANCH_BAR_X;
const BRANCH_BAR_MIN = 8;
const BRANCH_M_PER_PX = 20;
const TRAFFIC_W = 128;
const LINE_H = 14;
const LABEL_FONT = 11;
const GAP_PX = 64;
const SIGN_R = 8;
const TRAFFIC_ROW_H = 18;
const MIN_KEY_GAP = 6;
const KM_EPS = 0.0005;

const CROSSING_GRAY = "#9aa6b2";
const INTERCHANGE_FILL = "#22c55e";
const STOP_STROKE = "#2e86de";
const CAP_COLOR = "#e2e8f0";
const STRUCTURE_EDGE = "#cbd5e1";

type LabelLine = {
  text: string;
  color: string;
  glyph: "crossing" | "interchange" | "stop" | "tunnel" | "bridge" | "none";
  glyphColor?: string;
  italic?: boolean;
  bold?: boolean;
  onClick?: () => void;
  title?: string;
  /** Branch row with connector, horizontal bar, and traffic bar at its row position. */
  branch?: RoadSegment;
};

type Key = { km: number; lines: LabelLine[]; minH: number };

type Gap = { fromKm: number; toKm: number; meters: number | null };

type RoadStripMapProps = {
  road: RoadDetail;
};

/** Section stroke width (px), proportional to lanes but bounded to keep single lanes visible and multiple lanes clear of adjacent columns. */
function laneWidth(lanes: number): number {
  return Math.max(4, Math.min(16, 3 + lanes * 1.6));
}

/** Tick interval (km). Choose the smallest interval leaving roughly 50 px or more at the proportional scale. */
function tickStep(pxPerKm: number): number {
  const candidates = [0.1, 0.2, 0.25, 0.5, 1, 2, 5, 10, 20, 50, 100];
  return candidates.find((c) => c * pxPerKm >= 50) ?? 100;
}

/** Estimate text width (full-width = 1 em, half-width = 0.6 em) and truncate overflow, since SVG text cannot truncate automatically. */
function fitText(text: string, maxPx: number, fontPx: number): string {
  let width = 0;
  const chars = Array.from(text);
  for (let i = 0; i < chars.length; i++) {
    width += (chars[i].charCodeAt(0) < 0x2e80 ? 0.6 : 1) * fontPx;
    if (width > maxPx) return chars.slice(0, Math.max(1, i - 1)).join("") + "…";
  }
  return text;
}

function openWindow(windowType: string, id: number) {
  void invoke("open_registered_window", { windowType, entityId: String(id) });
}

/** Section vehicles/day and congestion %. Use the status name when inactive. */
function trafficText(s: RoadSegment, t: TFunction, locale: string): string {
  if (s.status !== STATUS_ACTIVE) return t(`status.${s.status}`, { defaultValue: "" });
  return s.vc != null
    ? t("road.strip.trafficValue", { traffic: formatInt(s.road_traffic, locale), vc: formatPercentValue(s.vc, locale) })
    : t("road.strip.trafficValueNoVc", { traffic: formatInt(s.road_traffic, locale) });
}

function crossingLabel(c: RoadCrossing, t: TFunction): LabelLine {
  if (c.road_id != null && c.road_id > 0) {
    const roadId = c.road_id;
    return {
      text: roadDisplayName(c.road_name, roadId, t),
      color: c.road_color ?? CROSSING_GRAY,
      glyph: "crossing",
      glyphColor: c.road_color ?? CROSSING_GRAY,
      bold: true,
      onClick: () => openWindow("road_detail", roadId),
      title: t("road.strip.openRoad"),
    };
  }
  let text: string;
  if (c.path_type === PATH_TRACK) {
    text = c.path_name !== "" ? t("road.strip.railCrossingNamed", { name: c.path_name }) : t("road.strip.railCrossing");
  } else if (c.path_name !== "") {
    text = c.path_name;
  } else {
    text = c.path_type === PATH_MOTORWAY
      ? t("road.strip.unnamedMotorway", { id: c.path_id })
      : t("road.strip.unnamedStreet", { id: c.path_id });
  }
  return {
    text,
    color: "#b8c4d0",
    glyph: "crossing",
    glyphColor: CROSSING_GRAY,
    onClick: () => openWindow("path_detail", c.path_id),
    title: t("road.strip.openPath"),
  };
}

/** Observe width: resizable panels require actual width to lay out the SVG in pixels. */
function useElementWidth(): [RefObject<HTMLDivElement>, number] {
  const ref = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(DEFAULT_WIDTH);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const update = () => setWidth(Math.max(320, Math.floor(el.clientWidth)));
    update();
    const observer = new ResizeObserver(update);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  return [ref, width];
}

export function RoadStripMap({ road }: RoadStripMapProps) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);
  const [containerRef, width] = useElementWidth();

  const layout = useMemo(() => {
    const sections = [...road.sections].sort((a, b) => a.index - b.index);
    const totalKm = Math.max(
      KM_EPS,
      ...sections.map((s) => s.end_km),
      ...road.segments.map((s) => Math.max(s.from_km, s.to_km)),
    );
    const pxPerKm = Math.max(60, Math.min(400, 700 / totalKm));

    const segs = road.segments.map((s) => ({
      ...s,
      lo: Math.min(s.from_km, s.to_km),
      hi: Math.max(s.from_km, s.to_km),
    }));
    const mains = segs.filter((s) => s.role === "main").sort((a, b) => a.lo - b.lo);
    const parallels = segs.filter((s) => s.role === "parallel");
    const branches = segs.filter((s) => s.role === "branch");

    const gaps: Gap[] = [];
    for (let i = 1; i < sections.length; i++) {
      gaps.push({ fromKm: sections[i - 1].end_km, toKm: sections[i].start_km, meters: sections[i].gap_before_m });
    }

    // Collect marker keys. Merge equal chainages and stack their labels vertically.
    const keyMap = new Map<number, Key>();
    const keyAt = (km: number): Key => {
      const q = Math.round(km / KM_EPS);
      let k = keyMap.get(q);
      if (!k) {
        k = { km, lines: [], minH: 0 };
        keyMap.set(q, k);
      }
      return k;
    };
    keyAt(0);
    keyAt(totalKm);
    for (const s of sections) {
      keyAt(s.start_km);
      keyAt(s.end_km);
    }
    for (const g of gaps) {
      keyAt((g.fromKm + g.toKm) / 2).lines.push({
        text: g.meters != null
          ? t("road.strip.gap", { m: formatInt(g.meters, locale) })
          : t("road.strip.gapUnknown"),
        color: "#8a9bb0",
        glyph: "none",
        italic: true,
      });
    }

    // Traffic rows at the far right: one per section midpoint key, stacked if baseline/parallel sections share a key.
    const trafficRowsAt = new Map<Key, number[]>();
    const addTrafficRow = (s: { lo: number; hi: number; path_id: number }) => {
      const k = keyAt((s.lo + s.hi) / 2);
      const rows = trafficRowsAt.get(k) ?? [];
      rows.push(s.path_id);
      trafficRowsAt.set(k, rows);
      k.minH = Math.max(k.minH, rows.length * TRAFFIC_ROW_H);
      return k;
    };

    // Place signs where speed limits change from the previous section, including each component's first section.
    const signs: { km: number; speed: number }[] = [];
    let prevSection = -1;
    let prevSpeed: number | null = null;
    for (const s of mains) {
      keyAt(s.lo);
      keyAt(s.hi);
      const mid = addTrafficRow(s);
      if (s.name !== "" || s.structure != null) {
        mid.lines.push({
          text: s.name !== "" ? s.name : t(s.structure === "tunnel" ? "road.strip.tunnel" : "road.strip.bridge"),
          color: "#c9d6e3",
          glyph: s.structure ?? "none",
          italic: true,
          onClick: () => openWindow("path_detail", s.path_id),
          title: t("road.strip.openPath"),
        });
      }
      if (s.speed_limit != null && (s.section_index !== prevSection || s.speed_limit !== prevSpeed)) {
        signs.push({ km: s.lo, speed: s.speed_limit });
        const k = keyAt(s.lo);
        k.minH = Math.max(k.minH, SIGN_R * 2 + 4);
      }
      prevSection = s.section_index;
      prevSpeed = s.speed_limit;
    }
    for (const s of parallels) {
      keyAt(s.lo);
      keyAt(s.hi);
      addTrafficRow(s);
    }
    const trafficRowOf = new Map<number, { km: number; dy: number }>();
    for (const [k, rows] of trafficRowsAt) {
      rows.forEach((id, i) => trafficRowOf.set(id, { km: k.km, dy: (i - (rows.length - 1) / 2) * TRAFFIC_ROW_H }));
    }

    const attachKmOf = (s: (typeof branches)[number]): number => {
      return s.attach_km != null && Number.isFinite(s.attach_km) ? Math.min(totalKm, Math.max(0, s.attach_km)) : s.lo;
    };
    for (const s of [...branches].sort((a, b) => attachKmOf(a) - attachKmOf(b) || a.path_id - b.path_id)) {
      const name = s.name !== "" ? s.name : t("road.pathWithId", { id: s.path_id });
      keyAt(attachKmOf(s)).lines.push({
        text: [t("road.strip.branch", { name }), trafficText(s, t, locale)].filter((p) => p !== "").join(" · "),
        color: "#c9d6e3",
        glyph: "none",
        onClick: () => openWindow("path_detail", s.path_id),
        title: t("road.strip.openPath"),
        branch: s,
      });
    }

    const markers: { km: number; kind: "crossing" | "interchange" | "stop" | "cap"; sides?: ("left" | "right")[]; color?: string }[] = [];
    const lastSection = sections.length > 0 ? sections[sections.length - 1].index : 0;
    for (const ev of road.events) {
      const k = keyAt(ev.km);
      if (ev.kind === "terminal") {
        markers.push({ km: ev.km, kind: "cap" });
        const isOrigin = ev.position === "start" && ev.section_index === (sections[0]?.index ?? 0);
        const isTerminus = ev.position === "end" && ev.section_index === lastSection;
        if (isOrigin || isTerminus) {
          k.lines.unshift({
            text: t(isOrigin ? "road.strip.origin" : "road.strip.terminus"),
            color: "#e2e8f0",
            glyph: "none",
            bold: true,
          });
        }
      } else if (ev.kind === "crossing") {
        const sides = new Set<"left" | "right">();
        for (const c of ev.crossings) {
          if (c.side === "both" || c.side === "left") sides.add("left");
          if (c.side === "both" || c.side === "right") sides.add("right");
          k.lines.push(crossingLabel(c, t));
        }
        const named = ev.crossings.find((c) => c.road_color);
        markers.push({ km: ev.km, kind: "crossing", sides: [...sides], color: named?.road_color ?? CROSSING_GRAY });
      } else if (ev.kind === "interchange") {
        // Match map, 3D, and intersection-detail display names (actual names, or abbreviation + ID if unnamed).
        k.lines.push({
          text: interchangeDisplayName({ interchange_id: ev.interchange_id, name: ev.name, kind: ev.interchange_kind }, t),
          color: "#bbf7d0",
          glyph: "interchange",
          bold: true,
        });
        markers.push({ km: ev.km, kind: "interchange" });
      } else if (ev.kind === "stop") {
        const lineId = ev.line_agent_id;
        k.lines.push({
          text: ev.name && ev.name !== "" ? ev.name : t("road.strip.stopUnnamed"),
          color: "#bfdbfe",
          glyph: "stop",
          onClick: lineId > 0 ? () => openWindow("transit_detail", lineId) : undefined,
          title: lineId > 0 ? t("road.strip.openLine") : undefined,
        });
        markers.push({ km: ev.km, kind: "stop" });
      }
    }

    // Piecewise-linear vertical positions: accumulate the greater of proportional distance and label-height minimum spacing.
    const keys = [...keyMap.values()].sort((a, b) => a.km - b.km);
    const heightOf = (k: Key) => Math.max(k.minH, k.lines.length * LINE_H);
    const inGap = (a: number, b: number) =>
      gaps.find((g) => a >= g.fromKm - KM_EPS && b <= g.toKm + KM_EPS && g.toKm - g.fromKm > KM_EPS);
    const ys: number[] = [0];
    for (let i = 1; i < keys.length; i++) {
      const a = keys[i - 1];
      const b = keys[i];
      const gap = inGap(a.km, b.km);
      const proportional = gap
        ? (GAP_PX * (b.km - a.km)) / (gap.toKm - gap.fromKm)
        : (b.km - a.km) * pxPerKm;
      const minGap = Math.max(MIN_KEY_GAP, (heightOf(a) + heightOf(b)) / 2 + 4);
      ys.push(ys[i - 1] + Math.max(proportional, minGap));
    }
    const firstPad = Math.max(0, heightOf(keys[0]) / 2);
    const yOf = (km: number): number => {
      if (km <= keys[0].km) return TOP + firstPad;
      for (let i = 1; i < keys.length; i++) {
        if (km <= keys[i].km) {
          const a = keys[i - 1].km;
          const b = keys[i].km;
          const f = b - a > 0 ? (km - a) / (b - a) : 0;
          return TOP + firstPad + ys[i - 1] + (ys[i] - ys[i - 1]) * f;
        }
      }
      return TOP + firstPad + ys[ys.length - 1];
    };
    const lastPad = heightOf(keys[keys.length - 1]) / 2;
    const height = TOP + firstPad + ys[ys.length - 1] + lastPad + BOTTOM;

    // Ticks: omit those inside gaps or compressed within 14 px of the previous tick.
    const step = tickStep(pxPerKm);
    const ticks: { km: number; y: number }[] = [];
    for (let i = 0; i * step <= totalKm + KM_EPS; i++) {
      const km = i * step;
      if (gaps.some((g) => km > g.fromKm + KM_EPS && km < g.toKm - KM_EPS)) continue;
      const y = yOf(km);
      if (ticks.length > 0 && y - ticks[ticks.length - 1].y < 14) continue;
      ticks.push({ km, y });
    }

    // Shift one-way baseline sections left only when parallel sections overlap, placing directions side by side.
    const overlapsParallel = (s: { lo: number; hi: number }) =>
      parallels.some((p) => p.lo < s.hi - KM_EPS && p.hi > s.lo + KM_EPS);

    const maxTraffic = Math.max(0, ...[...mains, ...parallels, ...branches].map((s) => s.road_traffic));

    return {
      keys, yOf, height, ticks, step, signs, markers, gaps, mains, parallels,
      overlapsParallel, maxTraffic, heightOf, trafficRowOf,
    };
  }, [road, t, locale]);

  const labelMaxPx = width - TRAFFIC_W - LABEL_X - 22;
  const trafficX = width - TRAFFIC_W;
  const barMax = TRAFFIC_W - 10;

  const tickDigits = layout.step === 0.25 ? 2 : layout.step < 1 ? 1 : 0;

  const segmentTitle = (s: RoadSegment) => {
    const parts = [
      s.name !== "" ? `${s.name} (#${s.path_id})` : t("road.pathWithId", { id: s.path_id }),
      t("road.strip.lanes", { n: s.lanes }),
      `${formatKm(s.from_km * 1000, locale)}–${formatKm(s.to_km * 1000, locale)} km`,
    ];
    if (s.status !== STATUS_ACTIVE) parts.push(t(`status.${s.status}`, { defaultValue: "" }));
    else {
      parts.push(t("road.strip.trafficValueNoVc", { traffic: formatInt(s.road_traffic, locale) }));
      if (s.vc != null) parts.push(t("road.strip.vcValue", { vc: formatPercentValue(s.vc, locale) }));
    }
    if (s.speed_limit != null) parts.push(t("road.strip.speedLimit", { speed: Math.round(s.speed_limit) }));
    if (s.accident) parts.push(t("road.strip.accident"));
    return parts.filter((p) => p !== "").join("\n");
  };

  const renderSegment = (s: (typeof layout.mains)[number], x: number, key: string) => {
    const y1 = layout.yOf(s.lo);
    const y2 = Math.max(y1 + 3, layout.yOf(s.hi));
    const w = laneWidth(s.lanes);
    const color = segmentColor(s);
    const inactive = s.status !== STATUS_ACTIVE;
    const midY = (y1 + y2) / 2;
    const arrow = s.direction === "both" || y2 - y1 < 14 ? null : s.direction;
    return (
      <g key={key} className="road-strip__segment" onClick={() => openWindow("path_detail", s.path_id)}>
        <title>{segmentTitle(s)}</title>
        {/* Use transparent hit areas wider than strokes for easier clicking. */}
        <rect x={x - 10} y={y1} width={20} height={y2 - y1} fill="transparent" />
        {s.structure === "bridge" && (
          <>
            <line x1={x - w / 2 - 3} x2={x - w / 2 - 3} y1={y1} y2={y2} stroke={STRUCTURE_EDGE} strokeWidth={1.2} />
            <line x1={x + w / 2 + 3} x2={x + w / 2 + 3} y1={y1} y2={y2} stroke={STRUCTURE_EDGE} strokeWidth={1.2} />
          </>
        )}
        {s.structure === "tunnel" && (
          <>
            <line x1={x - w / 2 - 3} x2={x - w / 2 - 3} y1={y1} y2={y2} stroke={STRUCTURE_EDGE} strokeWidth={1.2} strokeDasharray="3 3" />
            <line x1={x + w / 2 + 3} x2={x + w / 2 + 3} y1={y1} y2={y2} stroke={STRUCTURE_EDGE} strokeWidth={1.2} strokeDasharray="3 3" />
          </>
        )}
        <line
          className="road-strip__segment-line"
          x1={x}
          x2={x}
          y1={y1}
          y2={y2}
          stroke={color}
          strokeWidth={w}
          strokeOpacity={s.structure === "tunnel" ? 0.6 : 1}
          strokeDasharray={inactive ? "6 4" : undefined}
        />
        {arrow && (
          <path
            d={arrow === "down"
              ? `M ${x - 3} ${midY - 2} L ${x} ${midY + 2} L ${x + 3} ${midY - 2}`
              : `M ${x - 3} ${midY + 2} L ${x} ${midY - 2} L ${x + 3} ${midY + 2}`}
            fill="none"
            stroke="#0f172a"
            strokeWidth={1.6}
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        )}
        {s.accident && <circle cx={x + w / 2 + 6} cy={midY} r={2.5} fill={ROAD_COLOR_VC_HIGH} />}
      </g>
    );
  };

  const trafficBarLength = (s: RoadSegment) =>
    s.status === STATUS_ACTIVE && layout.maxTraffic > 0 ? Math.max(1, (s.road_traffic / layout.maxTraffic) * barMax) : 0;

  /** Baseline/parallel traffic rows (layout already reserves height at midpoint keys). */
  const renderTrafficBar = (s: (typeof layout.mains)[number]) => {
    const row = layout.trafficRowOf.get(s.path_id);
    if (!row) return null;
    const midY = layout.yOf(row.km) + row.dy;
    const len = trafficBarLength(s);
    return (
      <g key={`tr-${s.path_id}`} className="road-strip__segment" onClick={() => openWindow("path_detail", s.path_id)}>
        <title>{segmentTitle(s)}</title>
        <text x={width - 6} y={midY - 3} textAnchor="end" className="road-strip__traffic-text">
          {s.direction !== "both" ? (s.direction === "down" ? "▼ " : "▲ ") : ""}
          {trafficText(s, t, locale)}
        </text>
        <rect x={trafficX} y={midY + 1} width={barMax} height={5} rx={2} fill="rgba(255,255,255,0.06)" />
        {len > 0 && <rect x={trafficX} y={midY + 1} width={len} height={5} rx={2} fill={segmentColor(s)} />}
      </g>
    );
  };

  /** Branch row: connector from attachment point, horizontal bar proportional to section length, far-right traffic bar (numbers in the label). */
  const renderBranchRow = (s: RoadSegment, keyY: number, y: number) => {
    const color = segmentColor(s);
    const barLen = Math.max(BRANCH_BAR_MIN, Math.min(BRANCH_BAR_MAX, s.length_m / BRANCH_M_PER_PX));
    const len = trafficBarLength(s);
    return (
      <>
        <rect x={LINE_X} y={y - LINE_H / 2} width={width - LINE_X} height={LINE_H} fill="transparent" />
        <path d={`M ${LINE_X} ${keyY} L ${BRANCH_BAR_X} ${y}`} fill="none" stroke={color} strokeWidth={1.5} strokeOpacity={0.7} />
        <line
          x1={BRANCH_BAR_X}
          x2={BRANCH_BAR_X + barLen}
          y1={y}
          y2={y}
          stroke={color}
          strokeWidth={Math.min(6, laneWidth(s.lanes))}
          strokeDasharray={s.status !== STATUS_ACTIVE ? "4 3" : undefined}
        />
        <rect x={trafficX} y={y - 2} width={barMax} height={4} rx={2} fill="rgba(255,255,255,0.06)" />
        {len > 0 && <rect x={trafficX} y={y - 2} width={len} height={4} rx={2} fill={color} />}
      </>
    );
  };

  const renderGlyph = (line: LabelLine, x: number, y: number) => {
    switch (line.glyph) {
      case "crossing":
        return <line x1={x} x2={x + 9} y1={y} y2={y} stroke={line.glyphColor ?? CROSSING_GRAY} strokeWidth={3} strokeLinecap="round" />;
      case "interchange":
        return <path d={`M ${x + 4.5} ${y - 4.5} L ${x + 9} ${y} L ${x + 4.5} ${y + 4.5} L ${x} ${y} Z`} fill={INTERCHANGE_FILL} />;
      case "stop":
        return <circle cx={x + 4.5} cy={y} r={3.5} fill="#ffffff" stroke={STOP_STROKE} strokeWidth={2} />;
      case "tunnel":
        return <path d={`M ${x} ${y + 4} L ${x} ${y - 1} A 4.5 4.5 0 0 1 ${x + 9} ${y - 1} L ${x + 9} ${y + 4}`} fill="none" stroke={STRUCTURE_EDGE} strokeWidth={1.5} />;
      case "bridge":
        return <path d={`M ${x} ${y + 3} Q ${x + 4.5} ${y - 5} ${x + 9} ${y + 3} M ${x} ${y - 1} L ${x + 9} ${y - 1}`} fill="none" stroke={STRUCTURE_EDGE} strokeWidth={1.5} />;
      default:
        return null;
    }
  };

  const lastY = layout.yOf(Number.MAX_VALUE);

  return (
    <div className="road-strip" ref={containerRef}>
      <div className="road-strip__legend">
        <span className="road-strip__legend-item"><i style={{ background: ROAD_COLOR_VC_LOW }} />{t("road.strip.legendLow")}</span>
        <span className="road-strip__legend-item"><i style={{ background: ROAD_COLOR_VC_MID }} />{t("road.strip.legendMid")}</span>
        <span className="road-strip__legend-item"><i style={{ background: ROAD_COLOR_VC_HIGH }} />{t("road.strip.legendHigh")}</span>
        <span className="road-strip__legend-item"><i style={{ background: ROAD_COLOR_NO_CAPACITY }} />{t("road.strip.legendNoCapacity")}</span>
        <span className="road-strip__legend-item"><i className="road-strip__legend-dashed" style={{ borderColor: ROAD_COLOR_INACTIVE }} />{t("road.strip.legendInactive")}</span>
        <span className="road-strip__legend-note">{t("road.strip.legendWidth")}</span>
      </div>
      <svg className="road-strip__svg" width={width} height={layout.height} role="img" aria-label={t("road.tabs.diagram")}>
        <text x={AXIS_X} y={TOP - 14} textAnchor="end" className="road-strip__axis-title">km</text>
        <text x={width - 6} y={TOP - 14} textAnchor="end" className="road-strip__axis-title">{t("road.strip.trafficHeader")}</text>

        {/* Chainage ticks. */}
        <line x1={AXIS_X + 4} x2={AXIS_X + 4} y1={layout.yOf(0)} y2={lastY} className="road-strip__axis" />
        {layout.ticks.map((tick) => (
          <g key={`tick-${tick.km}`}>
            <line x1={AXIS_X + 1} x2={AXIS_X + 7} y1={tick.y} y2={tick.y} className="road-strip__axis" />
            <text x={AXIS_X - 2} y={tick.y + 3.5} textAnchor="end" className="road-strip__tick">
              {tick.km.toLocaleString(locale, { minimumFractionDigits: tickDigits, maximumFractionDigits: tickDigits })}
            </text>
          </g>
        ))}

        {/* Gaps between components. */}
        {layout.gaps.map((g, i) => (
          <line
            key={`gap-${i}`}
            x1={LINE_X}
            x2={LINE_X}
            y1={layout.yOf(g.fromKm)}
            y2={layout.yOf(g.toKm)}
            stroke="#64748b"
            strokeWidth={2}
            strokeDasharray="2 5"
            strokeLinecap="round"
          />
        ))}

        {/* Crossing roads (behind the baseline, extending briefly toward their continuation side). */}
        {layout.markers.filter((m) => m.kind === "crossing").map((m, i) => {
          const y = layout.yOf(m.km);
          const left = m.sides?.includes("left");
          const right = m.sides?.includes("right");
          return (
            <line
              key={`cross-${i}`}
              x1={left ? LINE_X - 20 : LINE_X}
              x2={right ? LINE_X + 20 : LINE_X}
              y1={y}
              y2={y}
              stroke={m.color ?? CROSSING_GRAY}
              strokeWidth={3}
              strokeLinecap="round"
              opacity={0.85}
            />
          );
        })}

        {/* Parallel sections (opposite direction) and baseline. */}
        {layout.parallels.map((s) => renderSegment(s, LINE_X + PARALLEL_DX, `par-${s.path_id}`))}
        {layout.mains.map((s) =>
          renderSegment(
            s,
            s.direction !== "both" && layout.overlapsParallel(s) ? LINE_X - PARALLEL_DX : LINE_X,
            `main-${s.path_id}`,
          ),
        )}

        {/* Markers on the line: ends, IC/JCT, stops. */}
        {layout.markers.map((m, i) => {
          const y = layout.yOf(m.km);
          if (m.kind === "cap") {
            return <line key={`cap-${i}`} x1={LINE_X - 11} x2={LINE_X + 11} y1={y} y2={y} stroke={CAP_COLOR} strokeWidth={3} strokeLinecap="round" />;
          }
          if (m.kind === "interchange") {
            return <path key={`ic-${i}`} d={`M ${LINE_X} ${y - 7} L ${LINE_X + 7} ${y} L ${LINE_X} ${y + 7} L ${LINE_X - 7} ${y} Z`} fill={INTERCHANGE_FILL} stroke="#0f172a" strokeWidth={1.5} />;
          }
          if (m.kind === "stop") {
            return <circle key={`stop-${i}`} cx={LINE_X} cy={y} r={4.5} fill="#ffffff" stroke={STOP_STROKE} strokeWidth={2.2} />;
          }
          return null;
        })}

        {/* Signs where speed limits change. */}
        {layout.signs.map((sg, i) => {
          const y = layout.yOf(sg.km);
          return (
            <g key={`sign-${i}`}>
              <title>{t("road.strip.speedLimit", { speed: Math.round(sg.speed) })}</title>
              <circle cx={SIGN_X} cy={y} r={SIGN_R} fill="#ffffff" stroke="#d62828" strokeWidth={2.2} />
              <text x={SIGN_X} y={y + 3} textAnchor="middle" className="road-strip__sign-text">
                {Math.round(sg.speed)}
              </text>
            </g>
          );
        })}

        {/* Label column. */}
        {layout.keys.map((k) => {
          if (k.lines.length === 0) return null;
          const cy = layout.yOf(k.km);
          const top = cy - ((k.lines.length - 1) * LINE_H) / 2;
          return (
            <g key={`lbl-${k.km}`}>
              {k.lines.some((line) => !line.branch) && (
                <line x1={LINE_X + 14} x2={LABEL_X - 4} y1={cy} y2={cy} className="road-strip__leader" />
              )}
              {k.lines.map((line, i) => {
                const y = top + i * LINE_H;
                const glyph = renderGlyph(line, LABEL_X, y);
                const tx = glyph ? LABEL_X + 13 : LABEL_X;
                return (
                  <g
                    key={i}
                    className={line.onClick ? "road-strip__label road-strip__label--link" : "road-strip__label"}
                    onClick={line.onClick}
                  >
                    {line.title && <title>{line.title}</title>}
                    {line.branch && renderBranchRow(line.branch, cy, y)}
                    {glyph}
                    <text
                      x={tx}
                      y={y + 4}
                      fill={line.color}
                      fontSize={LABEL_FONT}
                      fontWeight={line.bold ? 700 : 400}
                      fontStyle={line.italic ? "italic" : undefined}
                    >
                      {fitText(line.text, labelMaxPx - (tx - LABEL_X), LABEL_FONT)}
                    </text>
                  </g>
                );
              })}
            </g>
          );
        })}

        {/* Far right: traffic per section. */}
        {layout.mains.map((s) => renderTrafficBar(s))}
        {layout.parallels.map((s) => renderTrafficBar(s))}
      </svg>
    </div>
  );
}
