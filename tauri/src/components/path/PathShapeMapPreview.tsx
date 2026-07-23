/**
 * 道路建設パネル用のマッププレビュー
 *
 * 共通の都市2Dマップ部品 CityMapCanvas を埋め込み、その上に敷設中の道路
 * (ノード・直線/ベジエ曲線・制御点ハンドル)をワールド座標で重ね描きする。
 * 周辺の道路・土地区画・建物は CityMapCanvas のレイヤー機能が描画するため、
 * このコンポーネント側でそれらを取得・描画する必要はない。
 *
 * ビュー追従:
 *   ノード配列が変わるたびにその外接矩形へビューを合わせる。initialBounds prop を
 *   変えると CityMapCanvas が地形・ベクターを再取得してしまうため、ここでは
 *   ref経由の fitBounds() でビュー変換のみを更新して再取得を避ける。
 *   描画内容(ノード等)が変わったことは overlayVersion のインクリメントで
 *   CityMapCanvas に伝え、オーバーレイを再描画させる。
 *
 * 座標系:
 *   CityMapCanvas の overlayDraw に渡る ctx はワールド座標(X-Z平面)かつ +Z が
 *   画面上向きになる変換(Y反転)が適用済みなので、ノード座標 {x, z} をそのまま
 *   moveTo/lineTo/bezierCurveTo に渡せる。画面上で一定の見た目にするため、線幅・
 *   半径・破線間隔は view.zoom で割ってワールド単位へ換算する。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  CityMapCanvas,
  type CityMapCanvasHandle,
  type CityMapBounds,
  type CityMapLayerVisibility,
  type CityMapViewState,
} from "../map/CityMapCanvas";
import type { BuildPoint, ControlPoint, CurveMode } from "./pathBuildConstants";
import type { WaypointControls } from "./usePathConstruction";
import { computeAnchorOffsetDirs, offsetControlPoint, offsetPolyline2D } from "./pathOffset";

type PathShapeMapPreviewProps = {
  apiBaseUrl: string;
  nodes: BuildPoint[];
  curveMode: CurveMode;
  startControl: ControlPoint;
  endControl: ControlPoint;
  /**
   * 中間アンカー(nodes の両端を除く各点)の制御点。Unityから受信した値で、
   * 指定があるセグメントは自動計算(Catmull-Rom)ではなくこの値で曲線を描き、
   * 3D側でドラッグ調整した曲線形状とプレビューを一致させる。
   */
  waypointControls?: WaypointControls[];
  /**
   * 複合パス(combined_paths)の各コンポーネントの横方向オフセット(m)。
   * 指定された場合、中心線をガイド(破線)として描き、各オフセット位置に
   * 実際に敷設される並走パスを実線で描画する。
   */
  lateralOffsets?: number[];
};

/**
 * プレビューで表示するレイヤー。周辺の道路(幹線含む)・線路・土地区画・建物のみを
 * 表示し、標高色分け・等高線・グリッド・地区枠線・海岸線は敷設中の道路形状を
 * 見やすくするため非表示にする。
 */
const PREVIEW_LAYERS: Partial<CityMapLayerVisibility> = {
  coastline: false,
  trunkRoads: true,
  allRoads: true,
  railways: true,
  parcels: true,
  buildings: true,
  zoneBorders: false,
  elevation: false,
  contours: false,
  grid: false,
};

/** ノード外接矩形に加える余白(ワールドm)。単一ノードでも一定範囲が見えるようにする */
const FIT_MARGIN = 40;

/* ---- セグメント制御点計算(PathShapePreviewCanvas と同じロジック) ---- */

function computeSegmentCP1(
  pts: BuildPoint[],
  i: number,
  startControl: ControlPoint,
  waypointControls?: WaypointControls[]
): { x: number; z: number } {
  if (i === 0 && startControl) return startControl;
  // セグメントiの始点が中間アンカー(index i-1)のとき、その出口制御点を優先する
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
  waypointControls?: WaypointControls[]
): { x: number; z: number } {
  if (i === pts.length - 2 && endControl) return endControl;
  // セグメントiの終点が中間アンカー(index i)のとき、その入口制御点を優先する
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

/** ノード配列を包含するワールド座標範囲(余白付き)を求める。空なら null */
function computeNodeBounds(nodes: BuildPoint[]): CityMapBounds | null {
  if (nodes.length === 0) return null;
  let minX = Infinity;
  let minZ = Infinity;
  let maxX = -Infinity;
  let maxZ = -Infinity;
  for (const pt of nodes) {
    if (pt.x < minX) minX = pt.x;
    if (pt.z < minZ) minZ = pt.z;
    if (pt.x > maxX) maxX = pt.x;
    if (pt.z > maxZ) maxZ = pt.z;
  }
  return {
    x1: minX - FIT_MARGIN,
    z1: minZ - FIT_MARGIN,
    x2: maxX + FIT_MARGIN,
    z2: maxZ + FIT_MARGIN,
  };
}

/* ---- 制御点ハンドル描画(ワールド座標) ---- */

function drawControlHandle(
  ctx: CanvasRenderingContext2D,
  px: (w: number) => number,
  cp: ControlPoint,
  anchor: BuildPoint,
  color: string
) {
  if (!cp) return;

  // アンカーと制御点を結ぶ破線
  ctx.strokeStyle = color;
  ctx.lineWidth = px(1);
  ctx.setLineDash([px(3), px(3)]);
  ctx.beginPath();
  ctx.moveTo(anchor.x, anchor.z);
  ctx.lineTo(cp.x, cp.z);
  ctx.stroke();
  ctx.setLineDash([]);

  // 制御点(小さな四角)
  const r = px(3);
  ctx.fillStyle = color;
  ctx.strokeStyle = "#000000";
  ctx.lineWidth = px(1);
  ctx.beginPath();
  ctx.rect(cp.x - r, cp.z - r, r * 2, r * 2);
  ctx.fill();
  ctx.stroke();
}

/* ---- コンポーネント ---- */

export function PathShapeMapPreview({
  apiBaseUrl,
  nodes,
  curveMode,
  startControl,
  endControl,
  waypointControls,
  lateralOffsets,
}: PathShapeMapPreviewProps) {
  const mapRef = useRef<CityMapCanvasHandle>(null);
  const [overlayVersion, setOverlayVersion] = useState(0);

  // 初回マウント時のフィット範囲。以降のノード変更は fitBounds() で追従するため、
  // この値は変えず CityMapCanvas の初期化effectを再走させない(意図的に空依存)。
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const initialBounds = useMemo(() => computeNodeBounds(nodes) ?? undefined, []);

  /* ---- 敷設中の道路をワールド座標で重ね描きする ---- */
  const overlayDraw = useCallback(
    (ctx: CanvasRenderingContext2D, view: CityMapViewState) => {
      if (nodes.length === 0) return;
      const px = (w: number) => w / view.zoom;

      ctx.lineJoin = "round";
      ctx.lineCap = "round";

      // 1本のパス(直線または曲線)を現在のスタイルで描く
      const strokePath = (
        pts: BuildPoint[],
        sc0: ControlPoint,
        ec0: ControlPoint,
        wcs?: WaypointControls[]
      ) => {
        ctx.beginPath();
        ctx.moveTo(pts[0].x, pts[0].z);
        if (curveMode === "straight") {
          for (let i = 1; i < pts.length; i++) {
            ctx.lineTo(pts[i].x, pts[i].z);
          }
        } else if (pts.length === 2) {
          const sc = sc0 ?? pts[0];
          const ec = ec0 ?? pts[1];
          ctx.bezierCurveTo(sc.x, sc.z, ec.x, ec.z, pts[1].x, pts[1].z);
        } else {
          for (let i = 0; i < pts.length - 1; i++) {
            const cp1 = computeSegmentCP1(pts, i, sc0, wcs);
            const cp2 = computeSegmentCP2(pts, i, ec0, wcs);
            ctx.bezierCurveTo(cp1.x, cp1.z, cp2.x, cp2.z, pts[i + 1].x, pts[i + 1].z);
          }
        }
        ctx.stroke();
      };

      const hasOffsets = (lateralOffsets?.length ?? 0) > 0;

      // 中心線(直線または曲線)。複合パスではガイドとして破線で描く
      if (nodes.length >= 2) {
        ctx.strokeStyle = "#89b4fa";
        ctx.lineWidth = px(hasOffsets ? 1.5 : 3);
        if (hasOffsets) ctx.setLineDash([px(4), px(4)]);
        strokePath(nodes, startControl, endControl, waypointControls);
        ctx.setLineDash([]);
      }

      // 複合パスの並走コンポーネント(実際に敷設される位置)を実線で描く
      if (hasOffsets && nodes.length >= 2 && lateralOffsets) {
        const anchorDirs = computeAnchorOffsetDirs(nodes);
        ctx.strokeStyle = "#94e2d5";
        ctx.lineWidth = px(2.5);
        for (const lateral of lateralOffsets) {
          const offsetNodes = offsetPolyline2D(nodes, lateral);
          const sc = offsetControlPoint(startControl, anchorDirs[0], lateral);
          const ec = offsetControlPoint(endControl, anchorDirs[anchorDirs.length - 1], lateral);
          // 中間アンカーの制御点もアンカーと同じ方向でオフセットし、曲線を相似に保つ
          const offsetWcs = waypointControls?.map((wc, i) => {
            const dir = anchorDirs[i + 1];
            const ci = wc.control_in
              ? offsetControlPoint(
                  { x: wc.control_in[0], z: wc.control_in[wc.control_in.length - 1] },
                  dir,
                  lateral
                )
              : null;
            const co = wc.control_out
              ? offsetControlPoint(
                  { x: wc.control_out[0], z: wc.control_out[wc.control_out.length - 1] },
                  dir,
                  lateral
                )
              : null;
            return {
              control_in: ci ? [ci.x, 0, ci.z] : null,
              control_out: co ? [co.x, 0, co.z] : null,
            };
          });
          strokePath(offsetNodes, sc, ec, offsetWcs);
        }
      }

      // ノード(始点=緑・終点=赤・中間=青)
      const r = px(4);
      for (const pt of nodes) {
        ctx.fillStyle =
          pt.role === "start" ? "#a6e3a1" : pt.role === "end" ? "#f38ba8" : "#89b4fa";
        ctx.strokeStyle = "#000000";
        ctx.lineWidth = px(1.5);
        ctx.beginPath();
        ctx.arc(pt.x, pt.z, r, 0, Math.PI * 2);
        ctx.fill();
        ctx.stroke();
      }

      // 制御点ハンドル(曲線モード時のみ)
      if (curveMode !== "straight" && nodes.length >= 2) {
        drawControlHandle(ctx, px, startControl, nodes[0], "#f9e2af");
        drawControlHandle(ctx, px, endControl, nodes[nodes.length - 1], "#f9e2af");
      }
    },
    [nodes, curveMode, startControl, endControl, waypointControls, lateralOffsets]
  );

  /* ---- ノード等が変わったらビューを追従させ、オーバーレイを再描画する ---- */
  useEffect(() => {
    const bounds = computeNodeBounds(nodes);
    if (bounds) {
      mapRef.current?.fitBounds(bounds);
    }
    setOverlayVersion((v) => v + 1);
  }, [nodes, curveMode, startControl, endControl, waypointControls, lateralOffsets]);

  return (
    <CityMapCanvas
      ref={mapRef}
      apiBaseUrl={apiBaseUrl}
      layers={PREVIEW_LAYERS}
      initialBounds={initialBounds}
      interactive={false}
      overlayDraw={overlayDraw}
      overlayVersion={overlayVersion}
    />
  );
}
