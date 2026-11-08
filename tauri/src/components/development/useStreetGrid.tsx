/**
 * 道路のみの作成では、クリックで頂点を置き、右クリックで閉じた範囲をコアへ渡す。
 * 表示面積は描画中の輪郭から求め、海岸線で切り詰めた対象面積はコアが判定する。
 */
import { describeError } from "../../utils/errors";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import {
  type CityMapPointerEvent,
  type CityMapViewState,
} from "../map/CityMapCanvas";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import { formatArea } from "../../utils/formatArea";
import { resolveIntlLocale } from "../../utils/locale";
import { ringSignedArea } from "../terrain/riverGeometry";
import { hitTestShape, type XZ as WorldPoint } from "../path/pathShapeOverlay";
import type { DevelopmentWorkflowOptions } from "./CityDevelopmentWindow";

export const STREET_PATTERNS = ["relaxed_grid", "contour", "organic"] as const;
export const BLOCK_PATTERNS = ["block_grid", "block_strips", "block_perimeter"] as const;
export type StreetPattern = (typeof STREET_PATTERNS)[number] | (typeof BLOCK_PATTERNS)[number];

/** Fields shared by the backend's street grid preview and creation responses */
type StreetGridSummary = {
  candidates: number;
  rejected: Record<string, number>;
  axis_angle_deg: number;
  pattern: StreetPattern | "fixed_grid";
  blocks?: number;
};

/** Return value of backend create_street_grid */
type StreetGridResult = StreetGridSummary & {
  path_ids: number[];
  created: number;
};

/** Return value of backend plan_street_grid (segments in creation order) */
type StreetGridPlan = StreetGridSummary & {
  segments: { path_variant: number; points: [number, number][] }[];
};

/** Default street spacing in meters, matching backend DEFAULT_STREET_GRID_SPACING */
const DEFAULT_SPACING_M = 80;
const SPACING_MIN_M = 20;
const SPACING_MAX_M = 500;

/** Area drawing colors */
const AREA_FILL = "rgba(249, 226, 175, 0.18)";
const AREA_EDGE = "#f9e2af";
const VERTEX_FILL = "#f9e2af";
const VERTEX_EDGE = "#1e1e2e";
const VERTEX_RADIUS_PX = 5;
const PREVIEW_COLOR = "rgba(250, 179, 135, 0.9)";
/** Minimum preview stroke width so narrow streets remain visible when zoomed out */
const PREVIEW_MIN_WIDTH_PX = 2;

export function useStreetGrid(
  pattern: StreetPattern | undefined,
  { apiBaseUrl, variants, running, setRunning, reload }: DevelopmentWorkflowOptions,
) {
  const { t, i18n } = useTranslation();
  const masterData = useMasterData();

  const [vertices, setVertices] = useState<WorldPoint[]>([]);
  const [closed, setClosed] = useState(false);
  const [hover, setHover] = useState<WorldPoint | null>(null);
  const dragRef = useRef<{ index: number; pointerId: number; start: WorldPoint; vertex: WorldPoint } | null>(null);
  const [spacingInput, setSpacingInput] = useState(String(DEFAULT_SPACING_M));
  const [parcelSizeInput, setParcelSizeInput] = useState("20");
  const [angleAuto, setAngleAuto] = useState(true);
  const [angleDeg, setAngleDeg] = useState(0);
  const busy = running !== null;
  const [message, setMessage] = useState<string | null>(null);
  const [preview, setPreview] = useState<{ plan: StreetGridPlan; summary: string } | null>(null);
  const blockMode = BLOCK_PATTERNS.some((key) => key === pattern);
  // 入力途中の空欄や「120」の最初の「1」もそのまま表示し、送信できるかを別に判定する。
  const sizeInput = blockMode ? parcelSizeInput : spacingInput;
  const sizeM = Number(sizeInput);
  const minSizeM = blockMode ? 15 : SPACING_MIN_M;
  const maxSizeM = blockMode ? 25 : SPACING_MAX_M;
  const sizeValid = Number.isInteger(sizeM) && sizeM >= minSizeM && sizeM <= maxSizeM;
  const hint = t(closed ? "scenarioTools.hintReady" : "scenarioTools.hintAddVertices");
  const provisional = !closed && vertices.length > 0 ? hover : null;
  const ring = useMemo(() => provisional ? [...vertices, provisional] : vertices, [vertices, provisional]);
  const areaM2 = useMemo(() => Math.abs(ringSignedArea(ring.map(p => [p.x, p.z]))), [ring]);

  /** Incremented on every input change so a preview response for older inputs is ignored. */
  const inputVersionRef = useRef(0);

  useEffect(() => {
    inputVersionRef.current += 1;
    setPreview(null);
  }, [vertices, pattern, sizeInput, variants, angleAuto, angleDeg]);

  /* ---- Vertex editing ---- */
  const removeVertex = useCallback((index: number) => {
    dragRef.current = null;
    setVertices(prev => prev.filter((_, i) => i !== index));
    if (vertices.length <= 3) setClosed(false);
    setMessage(null);
  }, [vertices.length]);

  function clearArea() {
    dragRef.current = null;
    setVertices([]);
    setClosed(false);
    setHover(null);
    setMessage(null);
  }

  useEffect(() => {
    if (!pattern) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (!busy && vertices.length && (e.key === "Delete" || e.key === "Backspace") && !(e.target instanceof HTMLInputElement)) {
        e.preventDefault();
        removeVertex(vertices.length - 1);
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [pattern, busy, vertices.length, removeVertex]);

  /* ---- Pointer controls ---- */
  function hitOutline(e: CityMapPointerEvent) {
    // 閉じ辺も通常の辺と同じように判定する。頂点が辺に重なる場合は頂点が優先される。
    return hitTestShape({ anchors: [...vertices, vertices[0]], curved: false }, e.screen, e.view);
  }

  function handleMapClick(e: CityMapPointerEvent) {
    if (busy) return;
    if (closed) {
      const hit = hitOutline(e);
      if (hit?.type !== "edge") return;
      const i = hit.index + 1;
      setVertices(prev => [...prev.slice(0, i), hit.world, ...prev.slice(i)]);
    } else {
      setVertices(prev => [...prev, e.world]);
      setHover(null);
    }
    setMessage(null);
  }

  function handlePointerDown(e: CityMapPointerEvent): boolean {
    if (busy || !closed) return false;
    const hit = hitOutline(e);
    if (hit?.type !== "node") return false;
    dragRef.current = { index: hit.index, pointerId: (e.event as PointerEvent).pointerId, start: e.world, vertex: vertices[hit.index] };
    return true;
  }

  function moveVertex(e: CityMapPointerEvent) {
    const drag = dragRef.current;
    if (!drag || busy || drag.pointerId !== (e.event as PointerEvent).pointerId) return;
    const point = { x: drag.vertex.x + (e.world.x - drag.start.x), z: drag.vertex.z + (e.world.z - drag.start.z) };
    setVertices(prev => prev[drag.index]?.x === point.x && prev[drag.index]?.z === point.z
      ? prev : prev.map((p, i) => i === drag.index ? point : p));
    setMessage(null);
  }

  function handleContextMenu(e: CityMapPointerEvent) {
    if (busy || dragRef.current) return;
    if (!closed) {
      if (vertices.length >= 3) {
        setClosed(true);
        setHover(null);
      }
      return;
    }
    const hit = hitOutline(e);
    if (hit?.type === "node") removeVertex(hit.index);
  }

  /* ---- Area and preview overlay ---- */
  const overlayDraw = useCallback((ctx: CanvasRenderingContext2D, view: CityMapViewState) => {
    const px = (v: number) => v / view.zoom;
    const plan = preview?.plan;
    if (plan) {
      ctx.strokeStyle = PREVIEW_COLOR;
      ctx.lineCap = "round";
      ctx.lineJoin = "round";
      ctx.setLineDash([]);
      for (const segment of plan.segments) {
        ctx.beginPath();
        ctx.moveTo(segment.points[0][0], segment.points[0][1]);
        for (const [x, z] of segment.points.slice(1)) ctx.lineTo(x, z);
        ctx.lineWidth = Math.max(masterData?.paths[segment.path_variant]?.width ?? 0, px(PREVIEW_MIN_WIDTH_PX));
        ctx.stroke();
      }
    }

    const pts = vertices;
    if (pts.length === 0) return;

    if (ring.length >= 3) {
      ctx.beginPath();
      ctx.moveTo(ring[0].x, ring[0].z);
      for (let i = 1; i < ring.length; i++) ctx.lineTo(ring[i].x, ring[i].z);
      ctx.closePath();
      ctx.fillStyle = AREA_FILL;
      ctx.fill();
    }

    if (pts.length >= 2) {
      ctx.beginPath();
      ctx.moveTo(pts[0].x, pts[0].z);
      for (let i = 1; i < pts.length; i++) ctx.lineTo(pts[i].x, pts[i].z);
      if (closed) ctx.closePath();
      ctx.strokeStyle = AREA_EDGE;
      ctx.lineWidth = px(2);
      ctx.setLineDash([]);
      ctx.stroke();
    }

    if (provisional) {
      const last = pts[pts.length - 1];
      ctx.beginPath();
      ctx.moveTo(last.x, last.z);
      ctx.lineTo(provisional.x, provisional.z);
      if (pts.length >= 2) ctx.lineTo(pts[0].x, pts[0].z);
      ctx.strokeStyle = AREA_EDGE;
      ctx.lineWidth = px(1);
      ctx.setLineDash([px(6), px(4)]);
      ctx.stroke();
      ctx.setLineDash([]);
    }

    for (const p of pts) {
      ctx.beginPath();
      ctx.arc(p.x, p.z, px(VERTEX_RADIUS_PX), 0, Math.PI * 2);
      ctx.fillStyle = VERTEX_FILL;
      ctx.fill();
      ctx.strokeStyle = VERTEX_EDGE;
      ctx.lineWidth = px(1);
      ctx.stroke();
    }
  }, [closed, vertices, ring, provisional, preview, masterData]);

  /* ---- Preview and creation ---- */
  const postStreetGrid = <T,>(path: string) =>
    fetchApi<T>(buildApiUrl(apiBaseUrl, path), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        points: vertices.map((p) => [p.x, p.z]),
        spacing: blockMode ? undefined : sizeM,
        parcel_size: blockMode ? sizeM : undefined,
        pattern,
        path_variants: variants,
        angle_deg: blockMode || angleAuto ? null : angleDeg,
      }),
    });

  /** Result message: the street count text followed by the per-reason rejection counts. */
  const summarize = (result: StreetGridSummary, countKey: string, count: number) => {
    const rejectedTotal = Object.values(result.rejected).reduce((a, b) => a + b, 0);
    const reasons = Object.entries(result.rejected)
      .filter(([, n]) => n > 0)
      .map(([key, n]) => `${t(`scenarioTools.reject.${key}`, { defaultValue: key })}: ${n}`)
      .join(" / ");
    return (
      (blockMode && result.blocks === 0
        ? t("scenarioTools.noBlocks")
        : t(countKey, { count, rejected: rejectedTotal })) + (reasons ? ` (${reasons})` : "")
    );
  };

  async function previewStreetGrid() {
    if (!canPreview) return;
    const version = inputVersionRef.current;
    setRunning("preview");
    setMessage(null);
    try {
      const plan = await postStreetGrid<StreetGridPlan>("/api/v1/paths/street-grid/preview");
      if (version !== inputVersionRef.current) return;
      setPreview({ plan, summary: summarize(plan, "scenarioTools.gridPreviewed", plan.segments.length) });
    } catch (err) {
      setMessage(describeError(err, t));
    } finally {
      setRunning(null);
    }
  }

  async function createStreetGrid() {
    if (!canCreate) return;
    setRunning("create");
    setMessage(null);
    try {
      const result = await postStreetGrid<StreetGridResult>("/api/v1/paths/street-grid");
      clearArea();
      setMessage(summarize(result, "scenarioTools.gridCreated", result.created));
      await reload();
    } catch (err) {
      setMessage(describeError(err, t));
    } finally {
      setRunning(null);
    }
  }

  const canPreview = !!pattern && closed && areaM2 > 0 && variants.length > 0 && sizeValid && !busy;
  const canCreate = canPreview && preview !== null;
  const settings = (
    <>
      <label className="city-development__field">
        <span>{t(blockMode ? "scenarioTools.parcelSize" : "scenarioTools.spacing")}</span>
        <input
          type="number"
          min={minSizeM}
          max={maxSizeM}
          step={blockMode ? 1 : 10}
          value={sizeInput}
          aria-invalid={!sizeValid}
          disabled={busy}
          onChange={(e) => (blockMode ? setParcelSizeInput : setSpacingInput)(e.currentTarget.value)}
        />
        <span className="city-development__unit">m</span>
      </label>
      {!blockMode && <label className="city-development__field">
        <span>{t("scenarioTools.angle")}</span>
        <select value={angleAuto ? "auto" : "manual"} disabled={busy} onChange={(e) => setAngleAuto(e.target.value === "auto")}>
          <option value="auto">{t("scenarioTools.angleAuto")}</option>
          <option value="manual">{t("scenarioTools.angleManual")}</option>
        </select>
        {!angleAuto && (
          <input
            type="number"
            min={-180}
            max={180}
            step={5}
            value={angleDeg}
            disabled={busy}
            onChange={(e) => {
              const v = Number.parseFloat(e.target.value);
              if (!Number.isNaN(v)) setAngleDeg(v);
            }}
          />
        )}
      </label>}
      {blockMode && <p className="city-development__note">{t("scenarioTools.blockHint")}</p>}
      <output className="city-development__area" aria-live="off">
        {t("scenarioTools.selectedArea", { area: formatArea(areaM2, resolveIntlLocale(i18n.language)) })}
      </output>
    </>
  );

  return {
    settings,
    hint,
    hasArea: vertices.length > 0,
    clearArea,
    canPreview,
    canCreate,
    preview: previewStreetGrid,
    create: createStreetGrid,
    message: preview?.summary ?? message,
    mapProps: {
      overlayDraw,
      onMapClick: handleMapClick,
      onCursorWorldPos: !closed && vertices.length > 0 ? setHover : undefined,
      onPointerDown: handlePointerDown,
      onPointerMove: moveVertex,
      onPointerUp: (e: CityMapPointerEvent) => {
        if (e.event.type !== "pointercancel") moveVertex(e);
        dragRef.current = null;
      },
      onContextMenu: handleContextMenu,
    },
  };
}
