/**
 * 路線図(実地図オーバーレイ)
 *
 * 路線詳細パネル(TransitDetailPanel)の右ペインに埋め込む路線図。
 * 共通の都市2Dマップ部品 CityMapCanvas を `interactive={false}` で埋め込み、
 * その上に停留所(駅)マーカーと区間エッジ(路線)を `overlayDraw` でワールド座標の
 * まま重ね描きする。周辺の道路・線路・区画・建物は CityMapCanvas のレイヤーが描画する。
 *
 * 進行方向の表現:
 *   - 複線(メトロ/鉄道) ... 区間パスの travel_direction が DIR_FORWARD / DIR_BACKWARD の
 *     いずれか。進行方向の左側に微小オフセットした線を引き、中点に進行方向矢印を置く。
 *     これにより往路と復路が並行に描かれ、進行方向が視覚的に分かる。
 *   - バス/LRT 等の街路走行 ... travel_direction が DIR_BOTH。1本の道路を往復するため、
 *     直線で結び中点に両方向の矢印を置く。
 *
 * 運休中の路線(status≠STATUS_ACTIVE)は区間エッジを点線・暗色で描く。
 *
 * 座標系:
 *   CityMapCanvas の overlayDraw に渡る ctx はワールド座標(X-Z平面)かつ +Z が
 *   画面上向きになる変換(Y反転)が適用済みなので、停留所座標 (x, z) をそのまま
 *   moveTo/lineTo/arc に渡せる。線幅・半径・矢印サイズは view.zoom で割って
 *   ワールド単位へ換算し、画面上で一定の見た目にする。
 *   ※文字は描かないため、Y反転の打ち消しは不要。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  CityMapCanvas,
  type CityMapCanvasHandle,
  type CityMapBounds,
  type CityMapLayerVisibility,
  type CityMapViewState,
} from "../map/CityMapCanvas";
import { STATUS_ACTIVE } from "../../constants";
import type { TransitStop, TransitEdge } from "./TransitDetailPanel";
import "./TransitLineMap.css";

/** バックエンド consts.py の DIR_* と同じ値。区間パスの通行方向。
 * 複線は FORWARD/BACKWARD のいずれか(コード上は FORWARD 以外を BACKWARD 扱い)。
 * バス等の街路走行は BOTH。 */
const DIR_BOTH = 0;
const DIR_FORWARD = 1;

/** 複線の左右オフセット量(画面ピクセル相当)。進行方向の左側に寄せて描く */
const DUAL_TRACK_OFFSET_PX = 6;
/** 停留所外接矩形に加える余白(ワールドm)。単一停留所でも一定範囲が見えるようにする */
const FIT_MARGIN = 60;

type TransitLineMapProps = {
  apiBaseUrl: string;
  stops: TransitStop[];
  edges: TransitEdge[];
  /** 路線カラー("#rrggbb")。区間エッジと中間停留所のリングに使う */
  color: string;
  /** true のとき稼働中として描く。false は運休で点線・暗色になる */
  running: boolean;
};

/**
 * 路線図で表示するレイヤー。周辺の道路(幹線含む)・線路・土地区画・建物は表示して
 * 路線の地理的文脈が分かるようにする一方、標高色分け・等高線・グリッド・地区枠線は
 * 路線を見やすくするため非表示にする。海岸線は位置把握の参考として残す。
 */
const MAP_LAYERS: Partial<CityMapLayerVisibility> = {
  coastline: true,
  trunkRoads: true,
  allRoads: true,
  railways: true,
  parcels: true,
  buildings: true,
  stations: true,
  zoneBorders: false,
  elevation: false,
  contours: false,
  grid: false,
};

/** 停留所配列を包含するワールド座標範囲(余白付き)を求める。空なら null */
function computeStopsBounds(stops: TransitStop[]): CityMapBounds | null {
  if (stops.length === 0) return null;
  let minX = Infinity;
  let minZ = Infinity;
  let maxX = -Infinity;
  let maxZ = -Infinity;
  for (const s of stops) {
    const x = s.position[0];
    const z = s.position[2];
    if (x < minX) minX = x;
    if (z < minZ) minZ = z;
    if (x > maxX) maxX = x;
    if (z > maxZ) maxZ = z;
  }
  return {
    x1: minX - FIT_MARGIN,
    z1: minZ - FIT_MARGIN,
    x2: maxX + FIT_MARGIN,
    z2: maxZ + FIT_MARGIN,
  };
}

/**
 * 進行方向矢印を描く。(fromX,fromZ)→(toX,toZ) の向きの矢印を、線分の中点に置く。
 * 引数の座標は既にオフセット済みであることを想定(オフセットは呼び出し側で処理する)。
 */
function drawArrow(
  ctx: CanvasRenderingContext2D,
  px: (w: number) => number,
  fromX: number,
  fromZ: number,
  toX: number,
  toZ: number,
  color: string,
) {
  const dx = toX - fromX;
  const dz = toZ - fromZ;
  const len = Math.hypot(dx, dz);
  if (len < 1e-6) return;
  const ux = dx / len;
  const uz = dz / len;
  const midX = (fromX + toX) / 2;
  const midZ = (fromZ + toZ) / 2;
  // 矢印は中点を中心に、進行方向に沿って軸対称な三角形
  const halfLen = px(5);
  const halfWidth = px(3.5);
  const tipX = midX + ux * halfLen;
  const tipZ = midZ + uz * halfLen;
  // 進行方向に垂直な単位ベクトル(右側)
  const rx = -uz;
  const rz = ux;
  const baseX = midX - ux * halfLen;
  const baseZ = midZ - uz * halfLen;
  ctx.fillStyle = color;
  ctx.beginPath();
  ctx.moveTo(tipX, tipZ);
  ctx.lineTo(baseX + rx * halfWidth, baseZ + rz * halfWidth);
  ctx.lineTo(baseX - rx * halfWidth, baseZ - rz * halfWidth);
  ctx.closePath();
  ctx.fill();
}

export function TransitLineMap({
  apiBaseUrl,
  stops,
  edges,
  color,
  running,
}: TransitLineMapProps) {
  const mapRef = useRef<CityMapCanvasHandle>(null);
  const [overlayVersion, setOverlayVersion] = useState(0);

  // 初回マウント時のフィット範囲。以降のデータ変更は fitBounds() で追従するため、
  // この値は変えず CityMapCanvas の初期化effectを再走させない(意図的に空依存)。
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const initialBounds = useMemo(() => computeStopsBounds(stops) ?? undefined, []);

  // エッジ描画でノードIDから停留所座標を引けるようにする
  const stopByNodeId = useMemo(() => {
    const m = new Map<number, TransitStop>();
    for (const s of stops) m.set(s.node_id, s);
    return m;
  }, [stops]);

  /* ---- 路線(エッジ)と停留所(マーカー)をワールド座標で重ね描きする ---- */
  const overlayDraw = useCallback(
    (ctx: CanvasRenderingContext2D, view: CityMapViewState) => {
      if (stops.length === 0) return;
      const px = (w: number) => w / view.zoom;

      ctx.lineJoin = "round";
      ctx.lineCap = "round";

      // --- 路線エッジ ---
      // 複線(メトロ/鉄道)は進行方向の左側に微小オフセットして描き、進行方向矢印を中点に置く。
      // バス等の街路走行は1本の道路を往復するため、直線で結び両方向の矢印を置く。
      const offsetWorld = DUAL_TRACK_OFFSET_PX / view.zoom;
      for (const e of edges) {
        const from = stopByNodeId.get(e.from_node_id);
        const to = stopByNodeId.get(e.to_node_id);
        if (!from || !to) continue;
        const fromX = from.position[0];
        const fromZ = from.position[2];
        const toX = to.position[0];
        const toZ = to.position[2];
        const dx = toX - fromX;
        const dz = toZ - fromZ;
        const len = Math.hypot(dx, dz);
        if (len < 1e-6) continue;
        const ux = dx / len;
        const uz = dz / len;
        // 進行方向に垂直な左側単位ベクトル(進行方向を (ux,uz) として、左=+Z画面上)
        const lx = -uz;
        const lz = ux;

        const active = e.status === STATUS_ACTIVE;
        const strokeColor = active ? color : "#6c7086";
        ctx.strokeStyle = strokeColor;
        ctx.lineWidth = px(3);
        if (!active) {
          ctx.setLineDash([px(5), px(4)]);
        } else {
          ctx.setLineDash([]);
        }

        if (e.travel_direction === DIR_BOTH) {
          // バス等: 直線 + 中点に両方向矢印(往復運行)
          ctx.beginPath();
          ctx.moveTo(fromX, fromZ);
          ctx.lineTo(toX, toZ);
          ctx.stroke();
          ctx.setLineDash([]);
          if (active) {
            const midX = (fromX + toX) / 2;
            const midZ = (fromZ + toZ) / 2;
            // 始点→中点方向の矢印と、終点→中点方向の矢印
            drawArrow(ctx, px, fromX, fromZ, midX, midZ, color);
            drawArrow(ctx, px, toX, toZ, midX, midZ, color);
          }
        } else {
          // 複線: travel_direction に従って進行方向を決め、左側にオフセットした線分を描く
          const fwd = e.travel_direction === DIR_FORWARD;
          const fx = fwd ? fromX : toX;
          const fz = fwd ? fromZ : toZ;
          const tx = fwd ? toX : fromX;
          const tz = fwd ? toZ : fromZ;
          const ox0 = fx + lx * offsetWorld;
          const oz0 = fz + lz * offsetWorld;
          const ox1 = tx + lx * offsetWorld;
          const oz1 = tz + lz * offsetWorld;
          ctx.beginPath();
          ctx.moveTo(ox0, oz0);
          ctx.lineTo(ox1, oz1);
          ctx.stroke();
          ctx.setLineDash([]);
          if (active) {
            // オフセット済みの中点で進行方向矢印
            drawArrow(ctx, px, ox0, oz0, ox1, oz1, color);
          }
        }
      }
      ctx.setLineDash([]);

      // --- 停留所マーカー(始点=緑・終点=赤・中間=路線カラーのリング+黒芯) ---
      const ringR = px(7);
      const innerR = px(4);
      const lastOrder = stops.length - 1;
      for (const s of stops) {
        const x = s.position[0];
        const z = s.position[2];
        // 外側のリング(路線カラー)。始点・終点は塗りを変えて路線の端を強調する。
        const isStart = s.order === 0 && stops.length > 1;
        const isEnd = s.order === lastOrder && stops.length > 1;
        ctx.fillStyle = activeRingColor(color, isStart, isEnd);
        ctx.strokeStyle = "#11111b";
        ctx.lineWidth = px(1.5);
        ctx.beginPath();
        ctx.arc(x, z, ringR, 0, Math.PI * 2);
        ctx.fill();
        ctx.stroke();
        // 内側の芯
        ctx.fillStyle = "#11111b";
        ctx.beginPath();
        ctx.arc(x, z, innerR, 0, Math.PI * 2);
        ctx.fill();
      }
    },
    [stops, edges, stopByNodeId, color],
  );

  /* ---- 停留所・エッジが変わったらビューを追従させ、オーバーレイを再描画する ---- */
  useEffect(() => {
    const bounds = computeStopsBounds(stops);
    if (bounds) {
      mapRef.current?.fitBounds(bounds);
    }
    setOverlayVersion((v) => v + 1);
  }, [stops, edges]);

  return (
    <div className="transit-line-map">
      <CityMapCanvas
        ref={mapRef}
        apiBaseUrl={apiBaseUrl}
        layers={MAP_LAYERS}
        initialBounds={initialBounds}
        interactive={false}
        overlayDraw={overlayDraw}
        overlayVersion={overlayVersion}
        className="transit-line-map__canvas"
      />
      {/* 凡例: 路線カラー・始終点・運休の見分け方 */}
      <div className="transit-line-map__legend">
        <span className="transit-line-map__legend-item">
          <span
            className="transit-line-map__legend-line"
            style={{ background: color, opacity: running ? 1 : 0.5 }}
          />
          {running ? "" : ""}
        </span>
      </div>
    </div>
  );
}

/** 始点・終点・中間で停留所リングの色を変える。中間は路線カラー、始点=緑、終点=赤 */
function activeRingColor(base: string, isStart: boolean, isEnd: boolean): string {
  if (isStart) return "#a6e3a1";
  if (isEnd) return "#f38ba8";
  return base;
}
