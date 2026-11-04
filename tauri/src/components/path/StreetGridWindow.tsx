import { describeError } from "../../utils/errors";
/**
 * 街路の一括作成ウィンドウ(window_type="street_grid")。通常の建設とシナリオ制作で共用する。
 *
 * CityMapCanvasで多角形を指定し、選択したパターンの街路を計画中(STATUS_SCHEDULED_CONSTRUCTION)で作る。
 * 地形追従・街区内配置・接続と線形の検証はPOST /api/v1/paths/street-gridが担当する。
 * 作成は現在の入力のプレビュー確認後にだけ許可し、入力を変えたらプレビューを破棄する。
 * 地形追従の幹線と枝道の割り当ては、選んだ道路の幅をもとにコアが決める。
 * 作成した街路は建設計画として通常の着工・竣工を待つ。シナリオ制作では一括完成も利用できる。
 *
 * 左クリックで頂点を置き、右クリックで範囲を閉じる。確定後は頂点のドラッグ・辺への挿入・頂点の削除で編集する。
 * 面積は描画中の輪郭から求める。海岸線で切り詰めた後の対象面積はコアで判定する。
 * 地図の表示領域を確保するため、範囲指定の操作案内はヒントバーに表示する。
 *
 * 最初に受けたcamera:stateだけを使い、3Dカメラ位置に地図を合わせる。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { listen } from "@tauri-apps/api/event";

import {
  CityMapCanvas,
  DEFAULT_CITY_MAP_LAYERS,
  type CityMapCanvasHandle,
  type CityMapLayerVisibility,
  type CityMapPointerEvent,
  type CityMapViewState,
} from "../map/CityMapCanvas";
import { IPC_EVENT_CAMERA_STATE, sendCameraQueryState, sendSessionReload } from "../../utils/ipc";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { clearHintbarTextIf, setHintbarText } from "../../utils/hintbar";
import { useMasterData } from "../../contexts/MasterDataContext";
import { PATH_STREET } from "../../constants";
import { formatArea } from "../../utils/formatArea";
import { resolveIntlLocale } from "../../utils/locale";
import { ringSignedArea } from "../terrain/riverGeometry";
import { hitTestShape, type XZ as WorldPoint } from "./pathShapeOverlay";
import "./StreetGridWindow.css";

const STREET_PATTERNS = ["relaxed_grid", "contour", "organic"] as const;
const BLOCK_PATTERNS = ["block_grid", "block_strips", "block_perimeter"] as const;
type StreetPattern = (typeof STREET_PATTERNS)[number] | (typeof BLOCK_PATTERNS)[number];

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

/** 3D camera state (camera:state); only position is used. */
type CameraStatePayload = { position?: number[] };

/** Radius in meters when centering the map on the camera position */
const CAMERA_HALF_EXTENT_M = 600;

/** Default street spacing in meters, matching backend DEFAULT_STREET_GRID_SPACING */
const DEFAULT_SPACING_M = 80;
const SPACING_MIN_M = 20;
const SPACING_MAX_M = 500;

/** Default street variant (one-lane local road). Use the first street if missing from master data. */
const DEFAULT_VARIANT = 212;

/** Area drawing colors */
const AREA_FILL = "rgba(249, 226, 175, 0.18)";
const AREA_EDGE = "#f9e2af";
const VERTEX_FILL = "#f9e2af";
const VERTEX_EDGE = "#1e1e2e";
const VERTEX_RADIUS_PX = 5;
const PREVIEW_COLOR = "rgba(250, 179, 135, 0.9)";
/** Minimum preview stroke width so narrow streets remain visible when zoomed out */
const PREVIEW_MIN_WIDTH_PX = 2;

/** Show existing roads over terrain colors and shading so slopes can be judged when drawing the area; hide buildings. */
const MAP_LAYERS: Partial<CityMapLayerVisibility> = {
  ...DEFAULT_CITY_MAP_LAYERS,
  elevation: true,
  buildings: false,
};

export function StreetGridWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const masterData = useMasterData();
  const mapRef = useRef<CityMapCanvasHandle>(null);

  const [vertices, setVertices] = useState<WorldPoint[]>([]);
  const [closed, setClosed] = useState(false);
  const [hover, setHover] = useState<WorldPoint | null>(null);
  const dragRef = useRef<{ index: number; pointerId: number; start: WorldPoint; vertex: WorldPoint } | null>(null);
  const [spacingInput, setSpacingInput] = useState(String(DEFAULT_SPACING_M));
  const [pattern, setPattern] = useState<StreetPattern>("relaxed_grid");
  const [parcelSizeInput, setParcelSizeInput] = useState("20");
  /** Selected street types. null until master data provides the default. */
  const [variants, setVariants] = useState<number[] | null>(null);
  const [angleAuto, setAngleAuto] = useState(true);
  const [angleDeg, setAngleDeg] = useState(0);
  const [running, setRunning] = useState<"preview" | "create" | null>(null);
  const busy = running !== null;
  const [message, setMessage] = useState<string | null>(null);
  const [preview, setPreview] = useState<{ plan: StreetGridPlan; summary: string } | null>(null);
  const [overlayVersion, setOverlayVersion] = useState(0);
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

  useEffect(() => {
    void setHintbarText(hint).catch(console.error);
    return () => {
      void clearHintbarTextIf(hint).catch(console.error);
    };
  }, [hint]);

  const fittedRef = useRef(false);
  /** Incremented on every input change so a preview response for older inputs is ignored. */
  const inputVersionRef = useRef(0);

  /** Selectable street variants: surface streets supporting frontage, excluding elevated/underground paths. */
  const streetVariants = useMemo(() => {
    return Object.entries(masterData?.paths ?? {})
      .map(([id, entry]) => ({ id: Number(id), entry }))
      .filter(({ entry }) => entry.type === PATH_STREET && !entry.height_editable && entry.frontage_allowed)
      .filter(({ entry }) => !blockMode || entry.lanes === 1)
      .sort((a, b) => a.id - b.id)
      .map(({ id, entry }) => ({ id, name: entry.display_name }));
  }, [masterData, blockMode]);

  // Drop types that the pattern cannot use and fall back to the default only when nothing remains.
  // Unchecking every type by hand leaves the selection empty.
  useEffect(() => {
    if (streetVariants.length === 0) return;
    const fallback = [(streetVariants.find((v) => v.id === DEFAULT_VARIANT) ?? streetVariants[0]).id];
    if (variants === null) {
      setVariants(fallback);
      return;
    }
    const available = variants.filter((id) => streetVariants.some((v) => v.id === id));
    if (available.length === variants.length) return;
    setVariants(available.length > 0 ? available : fallback);
  }, [streetVariants, variants]);

  const toggleVariant = (id: number, checked: boolean) => {
    setVariants((prev) => {
      const rest = (prev ?? []).filter((v) => v !== id);
      return checked ? [...rest, id] : rest;
    });
  };

  useEffect(() => {
    inputVersionRef.current += 1;
    setPreview(null);
  }, [vertices, pattern, sizeInput, variants, angleAuto, angleDeg]);

  /* ---- Center on the 3D camera only once ---- */
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    listen<CameraStatePayload>(IPC_EVENT_CAMERA_STATE, (event) => {
      const pos = event.payload?.position;
      if (fittedRef.current || !pos || pos.length < 3) return;
      fittedRef.current = true;
      mapRef.current?.fitBounds({
        x1: pos[0] - CAMERA_HALF_EXTENT_M,
        z1: pos[2] - CAMERA_HALF_EXTENT_M,
        x2: pos[0] + CAMERA_HALF_EXTENT_M,
        z2: pos[2] + CAMERA_HALF_EXTENT_M,
      });
    })
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);
    void sendCameraQueryState().catch(console.error);
    return () => {
      unlisten?.();
    };
  }, []);

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
  }

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (!busy && vertices.length && (e.key === "Delete" || e.key === "Backspace") && !(e.target instanceof HTMLInputElement)) {
        e.preventDefault();
        removeVertex(vertices.length - 1);
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [busy, vertices.length, removeVertex]);

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

  useEffect(() => setOverlayVersion(v => v + 1), [overlayDraw]);

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
      setMessage(summarize(result, "scenarioTools.gridCreated", result.created));
      clearArea();
      // Reflect planned streets in 3D and refresh the map's road data.
      await sendSessionReload();
      mapRef.current?.reload();
    } catch (err) {
      setMessage(describeError(err, t));
    } finally {
      setRunning(null);
    }
  }

  const canPreview = closed && areaM2 > 0 && (variants?.length ?? 0) > 0 && sizeValid && !busy;
  const canCreate = canPreview && preview !== null;
  const shownMessage = preview?.summary ?? message;

  return (
    <div className="street-grid">
      <div className="street-grid__toolbar">
        <div className="street-grid__field street-grid__variants">
          <span>{t("scenarioTools.variant")}</span>
          {streetVariants.map((v) => (
            <label key={v.id} className="street-grid__variant">
              <input
                type="checkbox"
                checked={variants?.includes(v.id) ?? false}
                disabled={busy}
                onChange={(e) => toggleVariant(v.id, e.target.checked)}
              />
              {v.name}
            </label>
          ))}
          <output className="street-grid__area" aria-live="off">
            {t("scenarioTools.selectedArea", { area: formatArea(areaM2, resolveIntlLocale(i18n.language)) })}
          </output>
        </div>
        <div className="street-grid__controls">
          <label className="street-grid__field">
            <span>{t("scenarioTools.pattern")}</span>
            <select value={pattern} disabled={busy} onChange={(e) => setPattern(e.target.value as StreetPattern)}>
              <optgroup label={t("scenarioTools.terrainPatterns")}>
                {STREET_PATTERNS.map((key) => (
                  <option key={key} value={key}>{t(`scenarioTools.patterns.${key}`)}</option>
                ))}
              </optgroup>
              <optgroup label={t("scenarioTools.blockPatterns")}>
                {BLOCK_PATTERNS.map((key) => (
                  <option key={key} value={key}>{t(`scenarioTools.patterns.${key}`)}</option>
                ))}
              </optgroup>
            </select>
          </label>
          <label className="street-grid__field">
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
            <span className="street-grid__unit">m</span>
          </label>
          {!blockMode && <label className="street-grid__field">
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
          <div className="street-grid__actions">
            <button type="button" className="street-grid__btn" disabled={busy || vertices.length === 0} onClick={() => { clearArea(); setMessage(null); }}>
              {t("scenarioTools.clearVertices")}
            </button>
            <button type="button" className="street-grid__btn" disabled={!canPreview} onClick={() => void previewStreetGrid()}>
              {running === "preview" ? t("scenarioTools.previewing") : t("scenarioTools.preview")}
            </button>
            <button type="button" className="street-grid__btn street-grid__btn--primary" disabled={!canCreate} onClick={() => void createStreetGrid()}>
              {running === "create" ? t("scenarioTools.creating") : t("scenarioTools.create")}
            </button>
          </div>
        </div>
        {blockMode && <div className="street-grid__block-hint">{t("scenarioTools.blockHint")}</div>}
      </div>
      <div className="street-grid__map">
        <CityMapCanvas
          ref={mapRef}
          apiBaseUrl={apiBaseUrl}
          layers={MAP_LAYERS}
          interactive
          showCompass
          overlayDraw={overlayDraw}
          overlayVersion={overlayVersion}
          onMapClick={handleMapClick}
          onCursorWorldPos={!closed && vertices.length > 0 ? setHover : undefined}
          onPointerDown={handlePointerDown}
          onPointerMove={moveVertex}
          onPointerUp={e => {
            if (e.event.type !== "pointercancel") moveVertex(e);
            dragRef.current = null;
          }}
          onContextMenu={handleContextMenu}
          cursor="crosshair"
        />
        {shownMessage && <div className="street-grid__message">{shownMessage}</div>}
      </div>
    </div>
  );
}
