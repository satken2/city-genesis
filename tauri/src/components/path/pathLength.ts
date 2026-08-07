/**
 * 敷設中パスの水平長(m)を求めるユーティリティ
 *
 * 建設費用セクションでパスの長さを数値表示するために使う。
 * 直線モードはノード折れ線の各セグメント長を合算し、曲線モードは各セグメントを
 * Catmull-Rom由来のベジエ制御点で3次ベジエとしてサンプリングして弧長を近似する
 * (制御点の導出はバックエンド自動補完・地図描画の曲線補間と同じ規約)。
 * 高さ(Y)差は道路長の指標としては無視し、X-Z平面上の水平距離だけを積算する。
 */

import type { BuildPoint, ControlPoint, CurveMode } from "./pathBuildConstants";
import type { WaypointControls } from "./usePathConstruction";

/** ベジエ1セグメントあたりの分割数(弧長近似の精度)。 */
const BEZIER_SAMPLES = 24;

/* ---- セグメント制御点計算(地図の曲線補間・バックエンド自動補完と同一ロジック) ---- */

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

/** 3次ベジエ1セグメントを BEZIER_SAMPLES 分割してサンプリングし、折れ線長で弧長を近似する。 */
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
 * 敷設中パス(中心線)の水平長(m)を返す。ノードが2点未満なら null。
 * 軌道複線でも中心線1本の長さを返す(各線の実長は中心線とほぼ同じ)。
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

  // 曲線: 2点は単一の3次ベジエ、3点以上はセグメントごとの3次ベジエとして弧長を積算する。
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
