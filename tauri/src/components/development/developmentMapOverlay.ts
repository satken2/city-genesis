/**
 * 都市の自動開発・自動拡張・エポック拡張提案で共有する 2D 地図オーバーレイ描画とブラシ入力フック。
 */
import { useCallback, useRef, useState } from "react";
import type { MasterData } from "../../types";
import type { CityMapPointerEvent, CityMapViewState } from "../map/CityMapCanvas";
import { paintThroughScratch } from "../map/paintThroughScratch";

export type WorldPoint = { x: number; z: number };
export type Stroke = { radius: number; points: WorldPoint[] };

export type PreviewRoad = {
  path_variant: number;
  points: [number, number][];
  curved?: boolean;
  start_control?: [number, number] | null;
  end_control?: [number, number] | null;
  is_arterial?: boolean;
};

export type PreviewPlanOverlay = {
  roads: PreviewRoad[];
  parcels: [number, number][][];
};

/** ブラシの半径(画面のピクセル) */
export const BRUSH_RADIUS_PX = 30;
/** 筆跡に点を足す最小の間隔(ブラシの半径に対する比)。ポインタの移動ごとに足すと点列が無駄に長くなる */
const STROKE_STEP_RATIO = 0.25;

const PAINT_COLOR = "#89b4fa";
const PAINT_ALPHA = 0.3;
const BRUSH_EDGE = "rgba(30, 30, 46, 0.85)";
const PARCEL_FILL = "rgba(249, 226, 175, 0.35)";
const PARCEL_EDGE = "rgba(249, 226, 175, 0.9)";
const ROAD_COLOR = "rgba(250, 179, 135, 0.95)";
const ARTERIAL_ROAD_COLOR = "rgba(235, 111, 86, 0.96)";
/** 縮小しても細い道路が見えるように保つプレビューの道路の最小の太さ(px) */
const ROAD_MIN_WIDTH_PX = 2;

export function strokePath(ctx: CanvasRenderingContext2D, stroke: Stroke): void {
  ctx.lineWidth = stroke.radius * 2;
  ctx.beginPath();
  ctx.moveTo(stroke.points[0].x, stroke.points[0].z);
  // 1点だけの筆跡も丸い線端で円になるよう、同じ点へ線を引く。
  for (const p of stroke.points.length > 1 ? stroke.points.slice(1) : stroke.points) ctx.lineTo(p.x, p.z);
  ctx.stroke();
}

/** 塗ったブラシ範囲と計画済みの道路・土地区画を地図オーバーレイへ描く。 */
export function drawDevelopmentOverlay(
  ctx: CanvasRenderingContext2D,
  view: CityMapViewState,
  options: {
    strokes?: Stroke[];
    scratchCanvas?: HTMLCanvasElement | null;
    plan?: PreviewPlanOverlay | null;
    masterData?: MasterData | null;
  },
): void {
  const px = (v: number) => v / view.zoom;
  const { strokes, scratchCanvas, plan, masterData } = options;
  if (strokes && strokes.length > 0 && scratchCanvas) {
    paintThroughScratch(ctx, scratchCanvas, PAINT_ALPHA, (s) => {
      s.strokeStyle = PAINT_COLOR;
      s.lineCap = "round";
      s.lineJoin = "round";
      for (const stroke of strokes) strokePath(s, stroke);
    });
  }
  if (!plan) return;
  ctx.save();
  ctx.fillStyle = PARCEL_FILL;
  ctx.strokeStyle = PARCEL_EDGE;
  ctx.lineWidth = px(1);
  for (const ring of plan.parcels) {
    if (ring.length < 3) continue;
    ctx.beginPath();
    ctx.moveTo(ring[0][0], ring[0][1]);
    for (const [x, z] of ring.slice(1)) ctx.lineTo(x, z);
    ctx.closePath();
    ctx.fill();
    ctx.stroke();
  }
  ctx.lineCap = "round";
  ctx.lineJoin = "round";
  // 路地を先に描き、幹線を上に重ねて見やすくする。
  const sortedRoads = [...plan.roads].sort((a, b) => Number(Boolean(a.is_arterial)) - Number(Boolean(b.is_arterial)));
  for (const road of sortedRoads) {
    if (road.points.length < 2) continue;
    ctx.strokeStyle = road.is_arterial ? ARTERIAL_ROAD_COLOR : ROAD_COLOR;
    ctx.beginPath();
    ctx.moveTo(road.points[0][0], road.points[0][1]);
    for (const [x, z] of road.points.slice(1)) ctx.lineTo(x, z);
    ctx.lineWidth = Math.max(masterData?.paths[road.path_variant]?.width ?? 0, px(ROAD_MIN_WIDTH_PX));
    ctx.stroke();
  }
  ctx.restore();
}

/** 塗った範囲(strokes)をタブ間で共有するブラシ入力フック。 */
export function useBrushStrokes(busy: boolean, onStrokeStart?: () => void) {
  const [strokes, setStrokes] = useState<Stroke[]>([]);
  const busyRef = useRef(busy);
  busyRef.current = busy;
  const onStartRef = useRef(onStrokeStart);
  onStartRef.current = onStrokeStart;
  const activeStrokeRef = useRef<Stroke | null>(null);
  const cursorRef = useRef<WorldPoint | null>(null);

  const handlePointerDown = useCallback((e: CityMapPointerEvent): boolean => {
    if (!e.event.ctrlKey || e.button !== 0 || busyRef.current) return false;
    activeStrokeRef.current = { radius: BRUSH_RADIUS_PX / e.view.zoom, points: [e.world] };
    onStartRef.current?.();
    return true;
  }, []);

  const handlePointerMove = useCallback((e: CityMapPointerEvent) => {
    const stroke = activeStrokeRef.current;
    if (!stroke) return;
    const last = stroke.points[stroke.points.length - 1];
    if (Math.hypot(e.world.x - last.x, e.world.z - last.z) >= stroke.radius * STROKE_STEP_RATIO) {
      stroke.points.push(e.world);
    }
  }, []);

  const handlePointerUp = useCallback((e: CityMapPointerEvent) => {
    const stroke = activeStrokeRef.current;
    activeStrokeRef.current = null;
    if (!stroke) return;
    if (e.event.type !== "pointercancel") stroke.points.push(e.world);
    setStrokes((prev) => [...prev, stroke]);
  }, []);

  const handleCursor = useCallback((pos: WorldPoint | null) => {
    cursorRef.current = pos;
  }, []);

  const clearArea = useCallback(() => {
    activeStrokeRef.current = null;
    setStrokes([]);
  }, []);

  const drawLive = useCallback((ctx: CanvasRenderingContext2D, view: CityMapViewState) => {
    ctx.save();
    const stroke = activeStrokeRef.current;
    if (stroke) {
      ctx.globalAlpha = PAINT_ALPHA;
      ctx.strokeStyle = PAINT_COLOR;
      ctx.lineCap = "round";
      ctx.lineJoin = "round";
      strokePath(ctx, stroke);
    }
    const cursor = cursorRef.current;
    if (cursor) {
      ctx.globalAlpha = 1;
      ctx.beginPath();
      ctx.arc(cursor.x, cursor.z, BRUSH_RADIUS_PX / view.zoom, 0, Math.PI * 2);
      ctx.strokeStyle = BRUSH_EDGE;
      ctx.lineWidth = 1.5 / view.zoom;
      ctx.stroke();
    }
    ctx.restore();
  }, []);

  return {
    strokes,
    clearArea,
    pointerProps: {
      animatedOverlayDraw: drawLive,
      onCursorWorldPos: handleCursor,
      onPointerDown: handlePointerDown,
      onPointerMove: handlePointerMove,
      onPointerUp: handlePointerUp,
    },
  };
}
