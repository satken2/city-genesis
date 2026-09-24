/**
 * 公共交通路線の2D地図描画(CityMapCanvas の transitLines レイヤー本体)。
 *
 * データは GET /api/v1/transit/lines/geometry が返す走行形状で、停留所間を直線で結ぶのではなく
 * 実際の下地道路・線路に沿った [x, z] 折れ線(journeys)を持つ。バックエンドが
 * `TransitLine.journeys`(停留所間ごとの走行プロファイル)を下地パスのベジエ形状から
 * 切り出したものなので、複線の往路・復路は物理的に別の線路上に描かれ、バス等の街路走行は
 * 往路と復路が同じ道路上に重なる(進行方向の矢印が両向きに見える)。
 *
 * 描画規則:
 *   - 稼働中の路線 ... 路線カラーの帯(白い縁取り付き)。各停留所間の弧長中点に進行方向矢印。
 *   - 運休中の路線 ... 灰色の破線。矢印なし。
 *   - 走行形状が無い路線(下地経路が成立せず journeys が空) ... 区間パスの端点を結ぶ直線を
 *     灰色の破線で描き、位置だけを示す(走行形状ではない)。
 *   - 停留所 ... 路線カラーのリング+白い芯。複数路線が共有する停留所は濃灰リング。
 *   - 注目路線(focusLineId) ... 路線詳細の路線地図のように1路線を主役にする。他の路線は薄く
 *     描き、注目路線は最後に(最前面に)描いて始点=緑・終点=赤のリングで端を強調する。
 *
 * 座標系は CityMapCanvas の overlayDraw と同じワールド座標(X-Z 平面、+Z=北が画面上)で、
 * 線幅・半径・矢印の寸法は view.zoom で割って画面上で一定の見た目にする。
 */

import { STATUS_ACTIVE } from "../../constants";
import { resolveTransitColor } from "../../local-constants";
import type { CityMapViewState } from "./CityMapCanvas";

/** GET /api/v1/transit/lines/geometry の1路線分 */
export type TransitLineGeometry = {
  line_agent_id: number;
  name: string | null;
  path_variant: number;
  status: number;
  /** 路線カラー("#rrggbb")。空文字は未設定で種別ごとの既定色にフォールバックする */
  line_color: string;
  pause_reason: string | null;
  /** 停留所。往路の出現順(始点→終点)。走行形状が無い路線は順不同 */
  stops: { node_id: number; position: [number, number]; name: string | null }[];
  /** 停留所間ごとの走行形状。往路(returning=false)が停留所順に並び、その後に復路が続く */
  journeys: { returning: boolean; points: [number, number][] }[];
  /** 区間パスの端点を結ぶ直線。走行形状が無い路線の位置表示にだけ使う */
  segments: { from: [number, number]; to: [number, number]; status: number }[];
};

/** 帯の縁取り・停留所の芯の色。地形や建物の上でも帯が埋もれないよう白色にする */
const COLOR_OUTLINE = "#ffffff";
/** 運休中・走行形状なしの路線の色 */
const COLOR_INACTIVE = "#7c8389";
/** 複数路線が共有する停留所のリング色 */
const COLOR_SHARED_STOP = "#556773";
/** 注目路線の始点・終点のリング色 */
const COLOR_FOCUS_START = "#18845b";
const COLOR_FOCUS_END = "#cf4b50";

/** 注目路線があるときの、それ以外の路線の不透明度 */
const DIMMED_ALPHA = 0.3;
/** 進行方向矢印を置く最小の画面上長さ(px)。短い区間に矢印を置くと潰れて読めない */
const ARROW_MIN_SCREEN_LEN_PX = 60;

/** 路線カラーを解決する。未設定(空)の路線は種別ごとの既定色を使う */
export function effectiveTransitLineColor(line: Pick<TransitLineGeometry, "line_color" | "path_variant">): string {
  return line.line_color && line.line_color !== "" ? line.line_color : resolveTransitColor(line.path_variant);
}

/** 折れ線を現在のパスへ追加する(moveTo→lineTo 列) */
function tracePolyline(ctx: CanvasRenderingContext2D, points: [number, number][]): void {
  ctx.beginPath();
  for (let i = 0; i < points.length; i++) {
    const [x, z] = points[i];
    if (i === 0) ctx.moveTo(x, z);
    else ctx.lineTo(x, z);
  }
}

/** 折れ線の弧長中点の位置と、その地点の進行方向単位ベクトルを返す。長さゼロなら null */
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

/** 進行方向矢印(中心 (x,z)、向き (ux,uz) の軸対称な三角形) */
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

/** 停留所のリング(外側=fill 色、内側=白い芯) */
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
 * 全路線の走行形状と停留所を描く。
 * @param focusLineId 注目路線の line_agent_id。指定時はその路線を最前面・他路線を薄く描く
 */
export function drawTransitLines(
  ctx: CanvasRenderingContext2D,
  view: CityMapViewState,
  lines: TransitLineGeometry[],
  focusLineId?: number | null
): void {
  const px = (w: number) => w / view.zoom;
  const hasFocus = focusLineId != null;
  // 注目路線を最後に描いて最前面にする
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
      // 走行形状なし: 区間パスの直線を破線で描き、位置だけを示す
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
    // 進行方向矢印。薄く描く路線は情報量を落として主役を邪魔しない
    if (active && !dimmed) {
      for (const j of journeys) {
        const mid = polylineMidpoint(j.points);
        if (!mid || mid.length * view.zoom < ARROW_MIN_SCREEN_LEN_PX) continue;
        drawArrow(ctx, px, mid.x, mid.z, mid.ux, mid.uz, color);
      }
    }
  }

  // 停留所は全路線の帯の上に描く。共有停留所は1回だけ描き、色で共有を示す。
  type StopMark = { x: number; z: number; colors: Set<string>; dimmed: boolean; focusFill: string | null };
  const marks = new Map<number, StopMark>();
  for (const line of ordered) {
    const dimmed = hasFocus && line.line_agent_id !== focusLineId;
    const color = effectiveTransitLineColor(line);
    const last = line.stops.length - 1;
    line.stops.forEach((s, i) => {
      let mark = marks.get(s.node_id);
      if (!mark) {
        mark = { x: s.position[0], z: s.position[1], colors: new Set(), dimmed: true, focusFill: null };
        marks.set(s.node_id, mark);
      }
      mark.colors.add(color);
      if (!dimmed) mark.dimmed = false;
      if (hasFocus && !dimmed) {
        // 注目路線の始点=緑・終点=赤(1停留所だけの路線は端を強調しない)
        mark.focusFill = last > 0 && i === 0 ? COLOR_FOCUS_START : last > 0 && i === last ? COLOR_FOCUS_END : color;
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
