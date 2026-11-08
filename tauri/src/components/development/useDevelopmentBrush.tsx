/**
 * ブラシの筆跡は塗り始めの半径(m)と点列のままコアに渡す。範囲の面はコアが作る。
 * 範囲はアルゴリズム間で共有する。確定は現在の入力でプレビューが済んでから行う。
 */
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { emit } from "@tauri-apps/api/event";

import {
  type CityMapPointerEvent,
  type CityMapViewState,
} from "../map/CityMapCanvas";
import { paintThroughScratch } from "../map/paintThroughScratch";
import { TagPicker } from "../common/TagPicker";
import { TAGS_CHANGED_EVENT } from "../../utils/constructionTags";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { describeError } from "../../utils/errors";
import { useMasterData } from "../../contexts/MasterDataContext";
import { formatArea } from "../../utils/formatArea";
import { resolveIntlLocale } from "../../utils/locale";

export const DEVELOPMENT_PATTERNS = [
  "grid", "terrain_grid", "arterial", "neighborhood", "branching", "downtown_grid", "subdivision",
] as const;
export const STREET_PATTERNS = ["relaxed_grid", "contour", "organic"] as const;
export const BLOCK_PATTERNS = ["block_grid", "block_strips", "block_perimeter"] as const;
export type DevelopmentPattern = (typeof DEVELOPMENT_PATTERNS)[number];
export type StreetPattern = (typeof STREET_PATTERNS)[number];
export type BlockPattern = (typeof BLOCK_PATTERNS)[number];

/** 道路の間隔(m)と区画の面積(m²)の既定値と範囲。範囲はコアの検証と同じ */
const DEFAULT_SPACING_M = 70;
const SPACING_MIN_M = 20;
const SPACING_MAX_M = 500;
const DEFAULT_PARCEL_AREA_M2 = 600;
const PARCEL_AREA_MIN_M2 = 50;
const PARCEL_AREA_MAX_M2 = 20000;
const DEFAULT_PARCEL_SIZE_M = 20;
const PARCEL_SIZE_MIN_M = 15;
const PARCEL_SIZE_MAX_M = 25;

type WorldPoint = { x: number; z: number };
type Stroke = { radius: number; points: WorldPoint[] };

/** コアの plan_city_development の戻り値 */
type DevelopmentPlan = {
  roads: { path_variant: number; points: [number, number][] }[];
  parcels: [number, number][][];
  rejected: Record<string, number>;
  area_m2: number;
  blocks?: number;
};

/** コアの create_city_development の戻り値 */
type DevelopmentResult = {
  path_ids: number[];
  parcel_ids: number[];
  rejected: Record<string, number>;
};

/** ブラシの半径(画面のピクセル) */
const BRUSH_RADIUS_PX = 30;
/** 筆跡に点を足す最小の間隔(ブラシの半径に対する比)。ポインタの移動ごとに足すと点列が無駄に長くなる */
const STROKE_STEP_RATIO = 0.25;

const PAINT_COLOR = "#89b4fa";
const PAINT_ALPHA = 0.3;
const BRUSH_EDGE = "rgba(30, 30, 46, 0.85)";
const PARCEL_FILL = "rgba(249, 226, 175, 0.35)";
const PARCEL_EDGE = "rgba(249, 226, 175, 0.9)";
const ROAD_COLOR = "rgba(250, 179, 135, 0.95)";
/** 縮小しても細い道路が見えるように保つプレビューの道路の最小の太さ(px) */
const ROAD_MIN_WIDTH_PX = 2;

function strokePath(ctx: CanvasRenderingContext2D, stroke: Stroke): void {
  ctx.lineWidth = stroke.radius * 2;
  ctx.beginPath();
  ctx.moveTo(stroke.points[0].x, stroke.points[0].z);
  // 1点だけの筆跡も丸い線端で円になるよう、同じ点へ線を引く。
  for (const p of stroke.points.length > 1 ? stroke.points.slice(1) : stroke.points) ctx.lineTo(p.x, p.z);
  ctx.stroke();
}

export function useDevelopmentBrush(
  pattern: DevelopmentPattern | StreetPattern | BlockPattern,
  { apiBaseUrl, variants, reload }: { apiBaseUrl: string; variants: number[]; reload: () => Promise<void> },
) {
  const { t, i18n } = useTranslation();
  const masterData = useMasterData();
  const blockMode = BLOCK_PATTERNS.some(key => key === pattern);
  const createsParcels = DEVELOPMENT_PATTERNS.some(key => key === pattern);
  const hasAngle = !blockMode && !createsParcels;
  // 入力途中の空欄もそのまま表示し、送信できるかを別に判定する。
  const [spacingInput, setSpacingInput] = useState(String(DEFAULT_SPACING_M));
  const [parcelAreaInput, setParcelAreaInput] = useState(String(DEFAULT_PARCEL_AREA_M2));
  const [parcelSizeInput, setParcelSizeInput] = useState(String(DEFAULT_PARCEL_SIZE_M));
  const [angleAuto, setAngleAuto] = useState(true);
  const [angleInput, setAngleInput] = useState("0");
  const parcelArea = Number(parcelAreaInput);
  const widestRoad = createsParcels ? Math.max(0, ...variants.map(id => masterData?.paths[id]?.width ?? 0)) : 0;
  const sizeInput = blockMode ? parcelSizeInput : spacingInput;
  const size = Number(sizeInput);
  const minSize = blockMode ? PARCEL_SIZE_MIN_M : Math.max(SPACING_MIN_M, widestRoad);
  const maxSize = blockMode ? PARCEL_SIZE_MAX_M : SPACING_MAX_M;
  const sizeValid = size >= minSize && size <= maxSize && size > widestRoad;
  const parcelAreaValid = parcelArea >= PARCEL_AREA_MIN_M2 && parcelArea <= PARCEL_AREA_MAX_M2;
  const angle = hasAngle && !angleAuto ? Number(angleInput || NaN) : null;
  const angleValid = angle === null || (angle >= -180 && angle <= 180);

  const [strokes, setStrokes] = useState<Stroke[]>([]);
  const [running, setRunning] = useState<"preview" | "create" | null>(null);
  const busy = running !== null;
  const [message, setMessage] = useState<string | null>(null);
  const [projectTag, setProjectTag] = useState("");
  const parcelAreaParam = createsParcels ? parcelArea : undefined;
  const request = useMemo(() => ({
    pattern,
    path_variants: variants,
    spacing: blockMode ? undefined : size,
    parcel_size: blockMode ? size : undefined,
    parcel_area: parcelAreaParam,
    angle_deg: angle,
    strokes: strokes.map(s => ({ radius: s.radius, points: s.points.map(p => [p.x, p.z]) })),
  }), [pattern, variants, blockMode, size, parcelAreaParam, angle, strokes]);
  const [preview, setPreview] = useState<{ request: typeof request; plan: DevelopmentPlan } | null>(null);
  const plan = preview?.request === request ? preview.plan : null;
  useEffect(() => setMessage(null), [pattern]);

  const busyRef = useRef(busy);
  busyRef.current = busy;
  const activeStrokeRef = useRef<Stroke | null>(null);
  const cursorRef = useRef<WorldPoint | null>(null);
  const paintScratchRef = useRef<HTMLCanvasElement | null>(null);

  /* ---- ブラシ ---- */
  const handlePointerDown = useCallback((e: CityMapPointerEvent): boolean => {
    if (!e.event.ctrlKey || e.button !== 0 || busyRef.current) return false;
    activeStrokeRef.current = { radius: BRUSH_RADIUS_PX / e.view.zoom, points: [e.world] };
    setMessage(null);
    return true;
  }, []);

  const handlePointerMove = useCallback((e: CityMapPointerEvent) => {
    const stroke = activeStrokeRef.current;
    if (!stroke) return;
    const last = stroke.points[stroke.points.length - 1];
    if (Math.hypot(e.world.x - last.x, e.world.z - last.z) >= stroke.radius * STROKE_STEP_RATIO) stroke.points.push(e.world);
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

  function clearArea() {
    activeStrokeRef.current = null;
    setStrokes([]);
    setMessage(null);
  }

  /* ---- 塗った範囲とプレビューの描画 ---- */
  const overlayDraw = useCallback((ctx: CanvasRenderingContext2D, view: CityMapViewState) => {
    const px = (v: number) => v / view.zoom;
    if (strokes.length > 0) {
      paintScratchRef.current ??= document.createElement("canvas");
      paintThroughScratch(ctx, paintScratchRef.current, PAINT_ALPHA, (s) => {
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
      ctx.beginPath();
      ctx.moveTo(ring[0][0], ring[0][1]);
      for (const [x, z] of ring.slice(1)) ctx.lineTo(x, z);
      ctx.closePath();
      ctx.fill();
      ctx.stroke();
    }
    ctx.strokeStyle = ROAD_COLOR;
    ctx.lineCap = "butt";
    ctx.lineJoin = "round";
    for (const road of plan.roads) {
      ctx.beginPath();
      ctx.moveTo(road.points[0][0], road.points[0][1]);
      for (const [x, z] of road.points.slice(1)) ctx.lineTo(x, z);
      ctx.lineWidth = Math.max(masterData?.paths[road.path_variant]?.width ?? 0, px(ROAD_MIN_WIDTH_PX));
      ctx.stroke();
    }
    ctx.restore();
  }, [strokes, plan, masterData]);

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

  /* ---- プレビューと確定 ---- */
  const postDevelopment = <T,>(path: string, extra: Record<string, unknown> = {}) =>
    fetchApi<T>(buildApiUrl(apiBaseUrl, path), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...request, ...extra }),
    });

  const withReasons = (text: string, rejected: Record<string, number>) => {
    const reasons = Object.entries(rejected)
      .filter(([, n]) => n > 0)
      .map(([key, n]) => `${t(`scenarioTools.reject.${key}`, { defaultValue: key })}: ${n}`)
      .join(" / ");
    return text + (reasons ? ` (${reasons})` : "");
  };
  const rejectedTotal = (rejected: Record<string, number>) => Object.values(rejected).reduce((a, b) => a + b, 0);

  async function previewDevelopment() {
    if (!canPreview) return;
    setRunning("preview");
    setMessage(null);
    setPreview(null);
    try {
      const result = await postDevelopment<DevelopmentPlan>("/api/v1/city-development/preview");
      setPreview({ request, plan: result });
    } catch (err) {
      setMessage(describeError(err, t));
    } finally {
      setRunning(null);
    }
  }

  async function createDevelopment() {
    if (!canCreate) return;
    setRunning("create");
    setMessage(null);
    try {
      const result = await postDevelopment<DevelopmentResult>("/api/v1/city-development", projectTag ? { tag: projectTag } : {});
      // 開いている建設プロジェクトの一覧とタグの選択肢を取り直させる。
      if (projectTag && result.path_ids.length > 0) void emit(TAGS_CHANGED_EVENT, { tag: projectTag }).catch(console.error);
      const rejected = rejectedTotal(result.rejected);
      const summary = createsParcels
        ? t("cityDevelopment.created", { roads: result.path_ids.length, parcels: result.parcel_ids.length, rejected })
        : t("scenarioTools.gridCreated", { count: result.path_ids.length, rejected });
      setMessage(withReasons(summary, result.rejected));
      activeStrokeRef.current = null;
      setStrokes([]);
      await reload();
    } catch (err) {
      setMessage(describeError(err, t));
    } finally {
      setRunning(null);
    }
  }

  const canPreview = strokes.length > 0 && variants.length > 0 && sizeValid
    && (!createsParcels || parcelAreaValid) && angleValid && !busy;
  const canCreate = canPreview && plan !== null && (plan.roads.length > 0 || plan.parcels.length > 0);
  const settings = (
    <>
      <label className="city-development__field">
        <span>{t(blockMode ? "scenarioTools.parcelSize" : "cityDevelopment.spacing")}</span>
        <input
          type="number"
          min={minSize}
          max={maxSize}
          step={blockMode ? 1 : 10}
          value={sizeInput}
          aria-invalid={!sizeValid}
          disabled={busy}
          data-guide={blockMode ? "city_development.parcel_size" : "city_development.spacing"}
          onChange={e => (blockMode ? setParcelSizeInput : setSpacingInput)(e.currentTarget.value)}
        />
        <span className="city-development__unit">m</span>
      </label>
      {createsParcels && (
        <label className="city-development__field">
          <span>{t("cityDevelopment.parcelArea")}</span>
          <input
            type="number"
            min={PARCEL_AREA_MIN_M2}
            max={PARCEL_AREA_MAX_M2}
            step={50}
            value={parcelAreaInput}
            aria-invalid={!parcelAreaValid}
            disabled={busy}
            data-guide="city_development.parcel_area"
            onChange={e => setParcelAreaInput(e.currentTarget.value)}
          />
          <span className="city-development__unit">m²</span>
        </label>
      )}
      {hasAngle && (
        <label className="city-development__field">
          <span>{t("scenarioTools.angle")}</span>
          <select value={angleAuto ? "auto" : "manual"} disabled={busy} onChange={e => setAngleAuto(e.currentTarget.value === "auto")}>
            <option value="auto">{t("scenarioTools.angleAuto")}</option>
            <option value="manual">{t("scenarioTools.angleManual")}</option>
          </select>
          {!angleAuto && (
            <input
              type="number"
              min={-180}
              max={180}
              step={5}
              value={angleInput}
              aria-invalid={!angleValid}
              disabled={busy}
              onChange={e => setAngleInput(e.currentTarget.value)}
            />
          )}
        </label>
      )}
      {blockMode && <p className="city-development__note">{t("scenarioTools.blockHint")}</p>}
      <div className="city-development__field">
        <span>{t("tag.label")}</span>
        <TagPicker apiBaseUrl={apiBaseUrl} value={projectTag} onChange={setProjectTag} disabled={busy} />
      </div>
    </>
  );

  let previewSummary: string | null = null;
  if (plan) {
    const rejected = rejectedTotal(plan.rejected);
    if (createsParcels) {
      previewSummary = t("cityDevelopment.previewed", {
        area: formatArea(plan.area_m2, resolveIntlLocale(i18n.language)),
        roads: plan.roads.length,
        parcels: plan.parcels.length,
        rejected,
      });
    } else {
      previewSummary = plan.blocks === 0
        ? t("scenarioTools.noBlocks")
        : t("scenarioTools.gridPreviewed", { count: plan.roads.length, rejected });
    }
    previewSummary = withReasons(previewSummary, plan.rejected);
  }

  return {
    settings,
    running,
    hint: t("cityDevelopment.hint"),
    hasArea: strokes.length > 0,
    clearArea,
    canPreview,
    canCreate,
    preview: previewDevelopment,
    create: createDevelopment,
    message: message ?? previewSummary,
    mapProps: {
      overlayDraw,
      animatedOverlayDraw: drawLive,
      onCursorWorldPos: handleCursor,
      onPointerDown: handlePointerDown,
      onPointerMove: handlePointerMove,
      onPointerUp: handlePointerUp,
    },
  };
}
