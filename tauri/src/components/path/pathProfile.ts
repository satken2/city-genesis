/**
 * 2Dパス形状編集パネルの縦断面ストリップ(PathProfileStrip)のモデル構築・描画・ヒット判定(React非依存)。
 *
 * 曲線を伸ばして直線にした横軸(始点からの距離m)に、路面の絶対標高と地盤の標高を描く。
 * 高架道路・高架線路(height_editable)のノードの高さを上下ドラッグで編集するための簡易描画で、
 * 線とノード(番号付き)だけを描く。横軸の位置・地盤高・路面プロファイルはUnityの
 * `update_shape.alignment`(1mローカル推定を5m間引き)から取り、無ければ地図の折れ線長と
 * 地面相対高さだけで描く(地盤=0の相対表示)。
 */

import type { PathAlignmentSyncPayload, PathShapeUpdatePayload } from "./pathBuildConstants";
import { alignmentColor, segmentPoints, type PathShapeGeometry } from "./pathShapeOverlay";

export type ProfileNode = {
  index: number;
  /** 始点からの距離(m) */
  dist: number;
  /** 地面相対の高さ(m)。編集対象の値 */
  relHeight: number;
  /** 地盤の標高(m)。相対表示では 0 */
  groundY: number;
};

export type ProfileSample = { dist: number; deckY: number; groundY: number; grade: number };

export type ProfileModel = {
  nodes: ProfileNode[];
  /** 路面プロファイル(距離順)。相対表示では空 */
  samples: ProfileSample[];
  /** 勾配の区間ラベル(距離順ではない)。相対表示では空 */
  gradeLabels: Array<{ dist: number; deckY: number; value: number; limit: number }>;
  /** 地盤の標高が分かる(絶対標高で描く)か */
  absolute: boolean;
  totalLength: number;
  gradeLimit: number;
};

/** update_shape のペイロードと展開済み形状から縦断面モデルを作る */
export function buildProfileModel(shape: PathShapeUpdatePayload | null, geom: PathShapeGeometry): ProfileModel | null {
  const n = geom.anchors.length;
  if (!shape || n === 0) return null;
  const a = shape.alignment;
  const hasProfile = !!a && a.samples.length >= 2 && a.samples[0].length >= 7 && Array.isArray(a.nodes) && a.nodes.length === n;

  if (hasProfile && a) {
    const samples: ProfileSample[] = a.samples.map((s) => ({ dist: s[4], deckY: s[5], groundY: s[6], grade: s[3] }));
    const nodes: ProfileNode[] = geom.anchors.map((_, i) => ({
      index: i,
      dist: a.nodes![i][0],
      relHeight: geom.relHeights[i] ?? 0,
      groundY: a.nodes![i][1],
    }));
    const gradeLabels = a.labels
      .filter((l) => !l.curve)
      .map((l) => {
        const near = nearestSampleByXZ(a, l.x, l.z);
        return { dist: near?.[4] ?? 0, deckY: near?.[5] ?? 0, value: l.value, limit: l.limit };
      });
    return {
      nodes,
      samples,
      gradeLabels,
      absolute: true,
      totalLength: samples[samples.length - 1].dist,
      gradeLimit: a.grade_limit,
    };
  }

  // 相対表示: 地図の折れ線長を横軸、地面相対高さを縦軸にする
  const nodes: ProfileNode[] = [];
  let dist = 0;
  for (let i = 0; i < n; i++) {
    nodes.push({ index: i, dist, relHeight: geom.relHeights[i] ?? 0, groundY: 0 });
    if (i < n - 1) {
      const seg = segmentPoints(geom, i);
      for (let k = 0; k < seg.length - 1; k++) dist += Math.hypot(seg[k + 1].x - seg[k].x, seg[k + 1].z - seg[k].z);
    }
  }
  return { nodes, samples: [], gradeLabels: [], absolute: false, totalLength: dist, gradeLimit: a?.grade_limit ?? 0 };
}

function nearestSampleByXZ(a: PathAlignmentSyncPayload, x: number, z: number) {
  let best: PathAlignmentSyncPayload["samples"][number] | null = null;
  let bestD = Infinity;
  for (const s of a.samples) {
    const d = (s[0] - x) ** 2 + (s[1] - z) ** 2;
    if (d < bestD) { bestD = d; best = s; }
  }
  return best;
}

/* ============================================================================
 * レイアウト(距離・標高 ⇔ 画面px)
 * ========================================================================== */

export type ProfileLayout = {
  width: number;
  height: number;
  left: number;
  right: number;
  top: number;
  bottom: number;
  yMin: number;
  yMax: number;
  totalLength: number;
};

const MARGIN_LEFT = 48;
const MARGIN_RIGHT = 14;
const MARGIN_TOP = 26;
const MARGIN_BOTTOM = 20;
/** 縦軸の最小レンジ(m)。平坦な路線でも線が潰れないようにする */
const MIN_Y_RANGE = 12;

/** 表示中の高さ(ドラッグ中の仮の高さを含む)から縦軸レンジを決める */
export function buildProfileLayout(
  model: ProfileModel,
  width: number,
  height: number,
  previewHeights: Map<number, number>,
): ProfileLayout {
  let yMin = Infinity, yMax = -Infinity;
  const consider = (v: number) => { yMin = Math.min(yMin, v); yMax = Math.max(yMax, v); };
  for (const s of model.samples) { consider(s.deckY); consider(s.groundY); }
  for (const nd of model.nodes) {
    consider(nd.groundY);
    consider(nd.groundY + (previewHeights.get(nd.index) ?? nd.relHeight));
  }
  if (!Number.isFinite(yMin)) { yMin = 0; yMax = MIN_Y_RANGE; }
  const range = Math.max(MIN_Y_RANGE, (yMax - yMin) * 1.25);
  const mid = (yMax + yMin) / 2;
  return {
    width, height, left: MARGIN_LEFT, right: MARGIN_RIGHT, top: MARGIN_TOP, bottom: MARGIN_BOTTOM,
    yMin: mid - range / 2, yMax: mid + range / 2,
    totalLength: Math.max(model.totalLength, 1),
  };
}

export function distToX(l: ProfileLayout, dist: number): number {
  return l.left + (dist / l.totalLength) * (l.width - l.left - l.right);
}

export function elevToY(l: ProfileLayout, y: number): number {
  return l.top + ((l.yMax - y) / (l.yMax - l.yMin)) * (l.height - l.top - l.bottom);
}

/** 画面の縦1pxあたりの標高(m)。ドラッグ量の換算に使う */
export function metersPerPixel(l: ProfileLayout): number {
  return (l.yMax - l.yMin) / Math.max(1, l.height - l.top - l.bottom);
}

/** 目盛りの刻み(1・2・5系列)。おおよそ targetCount 本になる値を選ぶ */
function niceStep(range: number, targetCount: number): number {
  const raw = range / Math.max(1, targetCount);
  const pow = Math.pow(10, Math.floor(Math.log10(Math.max(raw, 1e-6))));
  for (const m of [1, 2, 5, 10]) {
    if (raw <= m * pow) return m * pow;
  }
  return 10 * pow;
}

/* ============================================================================
 * ヒット判定
 * ========================================================================== */

export const PROFILE_NODE_HIT_PX = 12;

export function hitTestProfileNode(
  model: ProfileModel,
  l: ProfileLayout,
  screen: { x: number; y: number },
  previewHeights: Map<number, number>,
): number {
  let best = -1;
  let bestD = PROFILE_NODE_HIT_PX;
  for (const nd of model.nodes) {
    const x = distToX(l, nd.dist);
    const y = elevToY(l, nd.groundY + (previewHeights.get(nd.index) ?? nd.relHeight));
    const d = Math.hypot(x - screen.x, y - screen.y);
    if (d < bestD) { bestD = d; best = nd.index; }
  }
  return best;
}

/* ============================================================================
 * 描画
 * ========================================================================== */

const COLOR_BG = "#0f1420";
const COLOR_GRID = "rgba(147, 169, 192, 0.18)";
const COLOR_AXIS_TEXT = "#93a9c0";
const COLOR_GROUND_FILL = "rgba(80, 90, 70, 0.45)";
const COLOR_GROUND_LINE = "#8a9a78";
const COLOR_DECK = "#89b4fa";
const COLOR_DECK_EDGE = "#11111b";
const COLOR_PREVIEW = "#f9e2af";
const COLOR_NODE_START = "#a6e3a1";
const COLOR_NODE_END = "#f38ba8";
const COLOR_NODE_WAYPOINT = "#f5f5f5";
const COLOR_NODE_EDGE = "#1e1e2e";
const COLOR_SELECTED = "#4ea1ff";
const COLOR_HOVER = "#f9e2af";
const COLOR_TEXT = "#ffffff";
const COLOR_TEXT_BG = "rgba(8, 8, 8, 0.85)";
const COLOR_LABEL_GRADE = "#ffcc33";

export type ProfileDrawOptions = {
  hoverIndex: number;
  dragIndex: number;
  selectedIndex: number;
  /** ドラッグ中の仮の相対高さ(ノード添字→m) */
  previewHeights: Map<number, number>;
  /** 勾配ラベルの文言(行ごと) */
  gradeLabelLines: (value: number, limit: number) => string[];
  /** 相対表示のときの基準線の名前(「地盤」など) */
  groundText: string;
};

export function drawProfile(
  ctx: CanvasRenderingContext2D,
  model: ProfileModel,
  l: ProfileLayout,
  opts: ProfileDrawOptions,
) {
  ctx.save();
  ctx.fillStyle = COLOR_BG;
  ctx.fillRect(0, 0, l.width, l.height);
  ctx.font = "10px Consolas, Menlo, monospace";
  ctx.textBaseline = "middle";

  // 目盛り(横=距離、縦=標高)
  const plotBottom = l.height - l.bottom;
  const xStep = niceStep(l.totalLength, 8);
  ctx.strokeStyle = COLOR_GRID;
  ctx.lineWidth = 1;
  ctx.fillStyle = COLOR_AXIS_TEXT;
  ctx.textAlign = "center";
  for (let d = 0; d <= l.totalLength + 1e-6; d += xStep) {
    const x = distToX(l, d);
    ctx.beginPath();
    ctx.moveTo(x, l.top);
    ctx.lineTo(x, plotBottom);
    ctx.stroke();
    ctx.fillText(`${Math.round(d)}m`, x, plotBottom + 10);
  }
  const yStep = niceStep(l.yMax - l.yMin, 5);
  ctx.textAlign = "right";
  for (let y = Math.ceil(l.yMin / yStep) * yStep; y <= l.yMax + 1e-6; y += yStep) {
    const sy = elevToY(l, y);
    ctx.beginPath();
    ctx.moveTo(l.left, sy);
    ctx.lineTo(l.width - l.right, sy);
    ctx.stroke();
    ctx.fillText(`${Number(y.toFixed(1))}`, l.left - 6, sy);
  }

  // 地盤(絶対表示ではサンプルの地盤線、相対表示では0の基準線)
  if (model.absolute && model.samples.length >= 2) {
    ctx.beginPath();
    ctx.moveTo(distToX(l, model.samples[0].dist), plotBottom);
    for (const s of model.samples) ctx.lineTo(distToX(l, s.dist), elevToY(l, s.groundY));
    ctx.lineTo(distToX(l, model.samples[model.samples.length - 1].dist), plotBottom);
    ctx.closePath();
    ctx.fillStyle = COLOR_GROUND_FILL;
    ctx.fill();
    ctx.beginPath();
    for (let i = 0; i < model.samples.length; i++) {
      const s = model.samples[i];
      if (i === 0) ctx.moveTo(distToX(l, s.dist), elevToY(l, s.groundY));
      else ctx.lineTo(distToX(l, s.dist), elevToY(l, s.groundY));
    }
    ctx.strokeStyle = COLOR_GROUND_LINE;
    ctx.lineWidth = 1.5;
    ctx.stroke();
  } else {
    const y0 = elevToY(l, 0);
    ctx.beginPath();
    ctx.moveTo(l.left, y0);
    ctx.lineTo(l.width - l.right, y0);
    ctx.strokeStyle = COLOR_GROUND_LINE;
    ctx.lineWidth = 1.5;
    ctx.setLineDash([4, 3]);
    ctx.stroke();
    ctx.setLineDash([]);
    ctx.fillStyle = COLOR_AXIS_TEXT;
    ctx.textAlign = "left";
    ctx.fillText(opts.groundText, l.left + 4, y0 - 8);
  }

  // 路面(絶対表示ではサンプルの路面線を勾配で色分け、相対表示ではノードを直線で結ぶ)
  const dragging = opts.dragIndex >= 0;
  if (model.absolute && model.samples.length >= 2) {
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.strokeStyle = COLOR_DECK_EDGE;
    ctx.lineWidth = 5;
    strokeSamples(ctx, l, model.samples);
    ctx.strokeStyle = COLOR_DECK;
    ctx.lineWidth = 3;
    strokeSamples(ctx, l, model.samples);
    if (!dragging && model.gradeLimit > 0) {
      ctx.lineWidth = 4;
      for (let i = 0; i < model.samples.length - 1; i++) {
        const a = model.samples[i], b = model.samples[i + 1];
        const color = alignmentColor(Math.max(a.grade, b.grade) / model.gradeLimit);
        if (!color) continue;
        ctx.strokeStyle = color;
        ctx.beginPath();
        ctx.moveTo(distToX(l, a.dist), elevToY(l, a.deckY));
        ctx.lineTo(distToX(l, b.dist), elevToY(l, b.deckY));
        ctx.stroke();
      }
    }
  } else {
    ctx.strokeStyle = COLOR_DECK;
    ctx.lineWidth = 3;
    ctx.beginPath();
    model.nodes.forEach((nd, i) => {
      const x = distToX(l, nd.dist);
      const y = elevToY(l, nd.groundY + (opts.previewHeights.get(nd.index) ?? nd.relHeight));
      if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    });
    ctx.stroke();
  }

  // ドラッグ中: 隣のノードから仮位置へ破線を引き、Unityの再評価前でも変化の向きが分かるようにする
  if (dragging && model.absolute) {
    const i = opts.dragIndex;
    const nd = model.nodes[i];
    if (nd) {
      const px = distToX(l, nd.dist);
      const py = elevToY(l, nd.groundY + (opts.previewHeights.get(i) ?? nd.relHeight));
      ctx.strokeStyle = COLOR_PREVIEW;
      ctx.lineWidth = 2;
      ctx.setLineDash([5, 4]);
      for (const j of [i - 1, i + 1]) {
        const nb = model.nodes[j];
        if (!nb) continue;
        ctx.beginPath();
        ctx.moveTo(distToX(l, nb.dist), elevToY(l, nb.groundY + nb.relHeight));
        ctx.lineTo(px, py);
        ctx.stroke();
      }
      ctx.setLineDash([]);
    }
  }

  // ノード(番号と高さ)
  const count = model.nodes.length;
  for (const nd of model.nodes) {
    const rel = opts.previewHeights.get(nd.index) ?? nd.relHeight;
    const x = distToX(l, nd.dist);
    const y = elevToY(l, nd.groundY + rel);
    const hot = nd.index === opts.hoverIndex || nd.index === opts.dragIndex;
    const r = hot ? 7 : 5.5;
    if (nd.index === opts.selectedIndex) {
      ctx.beginPath();
      ctx.arc(x, y, r + 4, 0, Math.PI * 2);
      ctx.strokeStyle = COLOR_SELECTED;
      ctx.lineWidth = 2.5;
      ctx.stroke();
    }
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fillStyle = hot ? COLOR_HOVER : nd.index === 0 ? COLOR_NODE_START : nd.index === count - 1 ? COLOR_NODE_END : COLOR_NODE_WAYPOINT;
    ctx.fill();
    ctx.strokeStyle = COLOR_NODE_EDGE;
    ctx.lineWidth = 1.5;
    ctx.stroke();

    // 番号(上)と相対高さ(下)
    ctx.font = "11px Consolas, Menlo, monospace";
    ctx.textAlign = "center";
    const num = `${nd.index + 1}`;
    const nw = ctx.measureText(num).width + 8;
    ctx.fillStyle = nd.index === opts.selectedIndex ? COLOR_SELECTED : COLOR_TEXT_BG;
    ctx.fillRect(x - nw / 2, y - 26, nw, 16);
    ctx.fillStyle = COLOR_TEXT;
    ctx.fillText(num, x, y - 18);
    const ht = `${rel > 0 ? "+" : ""}${rel.toFixed(1)}m`;
    const hw = ctx.measureText(ht).width + 8;
    ctx.fillStyle = COLOR_TEXT_BG;
    ctx.fillRect(x - hw / 2, y + 10, hw, 16);
    ctx.fillStyle = hot ? COLOR_HOVER : COLOR_TEXT;
    ctx.fillText(ht, x, y + 18);
  }

  // 勾配の区間ラベル(3D・地図と同じ内容)
  if (!dragging) {
    for (const label of model.gradeLabels) {
      const x = distToX(l, label.dist);
      const y = elevToY(l, label.deckY);
      const lines = opts.gradeLabelLines(label.value, label.limit);
      ctx.font = "11px Consolas, Menlo, monospace";
      ctx.textAlign = "left";
      ctx.textBaseline = "top";
      const lineH = 14;
      const w = Math.max(...lines.map((s) => ctx.measureText(s).width)) + 14;
      const h = lines.length * lineH + 8;
      // 上に出すと番号と重なるので路面の下側へ、右端では左へ折り返す
      const bx = Math.min(x + 14, l.width - l.right - w);
      const by = Math.min(y + 30, l.height - l.bottom - h);
      ctx.strokeStyle = COLOR_LABEL_GRADE;
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.moveTo(x, y);
      ctx.lineTo(bx, by + h / 2);
      ctx.stroke();
      ctx.beginPath();
      ctx.arc(x, y, 3.5, 0, Math.PI * 2);
      ctx.fillStyle = COLOR_LABEL_GRADE;
      ctx.fill();
      ctx.fillStyle = COLOR_TEXT_BG;
      ctx.fillRect(bx, by, w, h);
      ctx.fillStyle = COLOR_LABEL_GRADE;
      ctx.fillRect(bx, by, 3, h);
      ctx.fillStyle = COLOR_TEXT;
      lines.forEach((s, li) => ctx.fillText(s, bx + 9, by + 4 + li * lineH));
      ctx.textBaseline = "middle";
    }
  }

  ctx.restore();
}

function strokeSamples(ctx: CanvasRenderingContext2D, l: ProfileLayout, samples: ProfileSample[]) {
  ctx.beginPath();
  samples.forEach((s, i) => {
    const x = distToX(l, s.dist);
    const y = elevToY(l, s.deckY);
    if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
  });
  ctx.stroke();
}
