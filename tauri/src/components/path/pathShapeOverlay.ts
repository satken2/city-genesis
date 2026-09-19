/**
 * 2Dパス形状編集パネル(PathShapeEditorPanel)の描画・ヒット判定(React非依存の純関数)。
 *
 * `edit_path:update_shape` のペイロードを「アンカー列 + 各アンカーの入口/出口制御点」に展開し、
 * CityMapCanvas の overlayDraw(ワールド座標系のCTM適用済み。+Zが画面上向き)へ描く。
 * 画面上で一定の大きさにしたい寸法は `px / view.zoom` でワールド単位へ換算する。
 * 文字だけはCTMのY反転を打ち消してから描く(withScreenText)。
 *
 * ベジエの制御点補完はここでは行わない。曲線モードではUnityが全制御点を送ってくるため、
 * 受信値をそのまま描く(バックエンド・Unityと同じ形状になる)。直線モードは直線で結ぶ。
 */

import type { CityMapViewState } from "../map/CityMapCanvas";
import type { PathAlignmentSyncPayload, PathMaxSpanViolation, PathShapeUpdatePayload } from "./pathBuildConstants";

export type XZ = { x: number; z: number };

/** 描画・ヒット判定用に展開した形状。配列はすべてアンカーと同順 */
export type PathShapeGeometry = {
  anchors: XZ[];
  /** 地面相対の高さ(m) */
  relHeights: number[];
  /** 盛土オフセット(m) */
  embankments: number[];
  /** 出口制御点(次のセグメントのP1)。終点と直線モードでは null */
  controlOut: (XZ | null)[];
  /** 入口制御点(前のセグメントのP2)。始点と直線モードでは null */
  controlIn: (XZ | null)[];
  curved: boolean;
};

/** ヒット判定の結果。制御点 > ノード > 辺 の優先順で返す */
export type ShapeHit =
  | { type: "control"; index: number; which: "in" | "out" }
  | { type: "node"; index: number }
  | { type: "edge"; index: number; world: XZ };

/** ドラッグ中のプレビュー。hit はドラッグ対象、world はカーソルのワールド座標 */
export type ShapeDrag = { hit: ShapeHit; world: XZ };

/** ヒット判定の許容半径(画面px)。Unity RoadOverlayHitDetector(点14px/辺10px)に近い値 */
export const SHAPE_HIT_OPTIONS = { nodeRadiusPx: 12, controlRadiusPx: 10, edgeDistancePx: 8 };

/** 1セグメントのベジエ細分数 */
const BEZIER_SUBDIVISIONS = 24;

/* ---- 配色(都市マップの地物色と同系統) ---- */
const COLOR_CENTERLINE = "#89b4fa";
const COLOR_CENTERLINE_EDGE = "#11111b";
const COLOR_LATERAL = "rgba(137, 180, 250, 0.45)";
const COLOR_HANDLE_LINE = "rgba(205, 214, 244, 0.7)";
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

/* ============================================================================
 * 形状の展開
 * ========================================================================== */

function toXZ(v: number[] | null | undefined): XZ | null {
  if (!v || v.length < 2) return null;
  // [x, z] と [x, y, z] の両方を受け付ける(Unity側の読み取りと同じ流儀)
  return { x: v[0], z: v[v.length - 1] };
}

/** update_shape のペイロードを展開する。点が無ければ空の形状を返す */
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
 * ドラッグ中のプレビュー用に、対象だけを動かした形状のコピーを作る。
 * Unity側の挙動に合わせ、ノード移動では両側の制御点を同じ量だけ平行移動し、
 * 制御点移動では反対側の制御点を頂点を挟んで一直線(長さ維持)に保つ。
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
 * ドラッグ終了時の楽観的更新。Unityからの update_shape が届くまでの間、
 * 動かした対象を受信ペイロードにも反映して表示が戻らないようにする。
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

/* ============================================================================
 * 折れ線化
 * ========================================================================== */

/** セグメント i(anchors[i]→anchors[i+1])の点列。曲線モードで両制御点が揃えば3次ベジエ、それ以外は直線 */
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

/** 全線の点列(セグメント境界の重複は除く) */
export function shapePolyline(geom: PathShapeGeometry): XZ[] {
  const out: XZ[] = [];
  for (let i = 0; i < geom.anchors.length - 1; i++) {
    const seg = segmentPoints(geom, i);
    for (let k = i === 0 ? 0 : 1; k < seg.length; k++) out.push(seg[k]);
  }
  if (geom.anchors.length === 1) out.push(geom.anchors[0]);
  return out;
}

/** 点列を進行方向右へ offset(m) だけ平行移動した点列(複線の平行線プレビュー用の近似) */
function offsetPolyline(points: XZ[], offset: number): XZ[] {
  const n = points.length;
  if (n < 2 || offset === 0) return points;
  const out: XZ[] = [];
  for (let i = 0; i < n; i++) {
    const p0 = points[Math.max(0, i - 1)];
    const p1 = points[Math.min(n - 1, i + 1)];
    const dx = p1.x - p0.x, dz = p1.z - p0.z;
    const len = Math.hypot(dx, dz) || 1;
    // 進行方向 (dx,dz) の右手法線は (dz, -dx)(x東・z北の右手系)
    out.push({ x: points[i].x + (dz / len) * offset, z: points[i].z - (dx / len) * offset });
  }
  return out;
}

/* ============================================================================
 * 座標変換・ヒット判定
 * ========================================================================== */

export function worldToScreen(view: CityMapViewState, p: XZ): { x: number; y: number } {
  return { x: (p.x - view.offsetX) * view.zoom, y: (view.offsetY - p.z) * view.zoom };
}

export function screenToWorld(view: CityMapViewState, s: { x: number; y: number }): XZ {
  return { x: s.x / view.zoom + view.offsetX, z: view.offsetY - s.y / view.zoom };
}

/**
 * 画面座標でのヒット判定。制御点(曲線モードのみ) → ノード → 辺 の順に優先する。
 * 辺のヒットでは、辺上の最近傍点のワールド座標を返す(そこへウェイポイントを挿入する)。
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

/**
 * カーソルに最も近い線形サンプル(画面px距離が maxPx 以内)。ホバー時の半径・勾配表示に使う。
 */
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

/** 点列の外接矩形(余白付き)。fitBounds に渡す */
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

/* ============================================================================
 * 描画
 * ========================================================================== */

/** 線形サンプルの深刻度(0=規格に余裕、1=制限ちょうど、>1=違反)。半径と勾配の悪い方 */
function alignmentSeverity(a: PathAlignmentSyncPayload, radius: number | null, grade: number): number {
  let s = 0;
  if (a.radius_limit > 0 && radius != null && radius > 0) s = Math.max(s, a.radius_limit / radius);
  if (a.grade_limit > 0) s = Math.max(s, grade / a.grade_limit);
  return s;
}

/** 深刻度に応じた着色。規格内で余裕があれば null(中心線の色のまま) */
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
  /** 区間ラベルの文言(行ごと)。i18n済みの文字列を上位が渡す */
  labelLines: (label: PathAlignmentSyncPayload["labels"][number]) => string[];
  /** ノード脇の高さ表示。表示不要なら null */
  heightText: (relHeight: number, embankment: number) => string | null;
};

/** ワールド座標 (wx, wz) を原点に、画面px単位・Y下向きの座標系で文字などを描く */
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

/** 敷設中パスの形状(中心線・複線・制御点・ノード・線形の色分けとラベル)を描く */
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

  // 複線の平行線(中心線から右へ正のオフセット)
  for (const off of opts.lateralOffsets) {
    if (off === 0) continue;
    ctx.strokeStyle = COLOR_LATERAL;
    ctx.lineWidth = px(2);
    strokePolyline(ctx, offsetPolyline(line, off));
  }

  // 中心線(縁取り付き)
  ctx.strokeStyle = COLOR_CENTERLINE_EDGE;
  ctx.lineWidth = px(5);
  strokePolyline(ctx, line);
  ctx.strokeStyle = COLOR_CENTERLINE;
  ctx.lineWidth = px(3);
  strokePolyline(ctx, line);

  // 線形の色分け(ドラッグ中はUnityの再評価前なので描かない。ドラッグ終了後の受信で更新される)
  const alignment = opts.alignment;
  if (alignment && !opts.drag && alignment.samples.length >= 2) {
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

  // 制御点とハンドル線(曲線モードのみ)
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

  // ノード(始点=緑、終点=赤、中間=白。選択中は青の輪、ホバー中は黄)
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

    // ノード番号(1始まり。縦断面ストリップ・Unity 3Dのラベルと共通)を右上に、高さを右に出す
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

  // 辺ホバー: 挿入位置のプレビュー
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

  // 距離超過も線形違反と同じ引出線ラベルで、対象区間に結び付けて表示する。
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
        // 同じ位置に重なるラベルは下へずらして重ならないようにする
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
