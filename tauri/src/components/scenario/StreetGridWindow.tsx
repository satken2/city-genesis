/**
 * Street grid creation window (window_type="street_grid"), scenario mode only.
 *
 * Specify a polygon on CityMapCanvas and create streets of the selected pattern inside it as planned
 * construction (STATUS_SCHEDULED_CONSTRUCTION).
 * POST /api/v1/scenario-tools/street-grid handles terrain-following growth, layout within blocks, and
 * connectivity/alignment validation.
 * After creation, inspect in 3D, delete unwanted streets, and complete them with Complete All in scenario
 * editing.
 *
 * Controls: left click adds a vertex; right click, Delete, or Backspace removes the last vertex; left-drag empty
 * space pans; wheel zooms.
 * Distinguish click/drag by screen distance between press and release. CityMapCanvas starts panning when
 * onPointerDown returns false, so always allow panning here and accept a vertex only if released without
 * movement.
 *
 * Center the map on the 3D camera position when opened, only on the first camera:state received.
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
import { ApiError, buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import { PATH_STREET } from "../../constants";
import "./StreetGridWindow.css";

type WorldPoint = { x: number; z: number };
const STREET_PATTERNS = ["relaxed_grid", "contour", "organic"] as const;
const BLOCK_PATTERNS = ["block_grid", "block_strips", "block_perimeter"] as const;
type StreetPattern = (typeof STREET_PATTERNS)[number] | (typeof BLOCK_PATTERNS)[number];

/** Return value of backend create_street_grid */
type StreetGridResult = {
  path_ids: number[];
  created: number;
  candidates: number;
  rejected: Record<string, number>;
  axis_angle_deg: number;
  pattern: StreetPattern | "fixed_grid";
  blocks?: number;
};

/** 3D camera state (camera:state); only position is used. */
type CameraStatePayload = { position?: number[] };

/** A release within this pixel distance from the press is a click adding a vertex; otherwise it is a pan. */
const CLICK_THRESHOLD_PX = 4;

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

/** Street grid creation needs only roads and parcel/district outlines; hide elevation, buildings, etc. */
const MAP_LAYERS: Partial<CityMapLayerVisibility> = {
  ...DEFAULT_CITY_MAP_LAYERS,
  elevation: false,
  buildings: false,
  contours: true,
};

export function StreetGridWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const mapRef = useRef<CityMapCanvasHandle>(null);

  const [vertices, setVertices] = useState<WorldPoint[]>([]);
  const [spacing, setSpacing] = useState(DEFAULT_SPACING_M);
  const [pattern, setPattern] = useState<StreetPattern>("relaxed_grid");
  const [parcelSize, setParcelSize] = useState(20);
  const [variant, setVariant] = useState<number | null>(null);
  const [angleAuto, setAngleAuto] = useState(true);
  const [angleDeg, setAngleDeg] = useState(0);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [overlayVersion, setOverlayVersion] = useState(0);
  const blockMode = BLOCK_PATTERNS.some((key) => key === pattern);

  // CityMapCanvas mirrors pointer handlers in refs, so also keep vertices and hover position
  // in refs to access them without recreating handlers.
  const verticesRef = useRef<WorldPoint[]>(vertices);
  verticesRef.current = vertices;
  const hoverRef = useRef<WorldPoint | null>(null);
  const pressRef = useRef<{ screen: { x: number; y: number }; world: WorldPoint } | null>(null);
  const lastScreenRef = useRef<{ x: number; y: number } | null>(null);
  const fittedRef = useRef(false);

  const redraw = useCallback(() => setOverlayVersion((v) => v + 1), []);

  /** Selectable street variants: surface streets supporting frontage, excluding elevated/underground paths. */
  const streetVariants = useMemo(() => {
    if (!masterData) return [] as { id: number; name: string }[];
    return Object.entries(masterData.paths)
      .map(([id, entry]) => ({ id: Number(id), entry }))
      .filter(({ entry }) => entry.type === PATH_STREET && !entry.height_editable && entry.frontage_allowed)
      .filter(({ entry }) => !blockMode || entry.lanes === 1)
      .sort((a, b) => a.id - b.id)
      .map(({ id, entry }) => ({ id, name: entry.display_name }));
  }, [masterData, blockMode]);

  useEffect(() => {
    if (streetVariants.length === 0 || streetVariants.some((v) => v.id === variant)) return;
    const preferred = streetVariants.find((v) => v.id === DEFAULT_VARIANT) ?? streetVariants[0];
    setVariant(preferred.id);
  }, [streetVariants, variant]);

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
  const removeLastVertex = useCallback(() => {
    setVertices((prev) => prev.slice(0, -1));
    setMessage(null);
  }, []);

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if ((e.key === "Delete" || e.key === "Backspace") && !(e.target instanceof HTMLInputElement)) {
        removeLastVertex();
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [removeLastVertex]);

  useEffect(() => {
    redraw();
  }, [vertices, redraw]);

  /* ---- Pointer controls ---- */
  const handlePointerDown = useCallback((e: CityMapPointerEvent): boolean => {
    pressRef.current = { screen: e.screen, world: e.world };
    lastScreenRef.current = e.screen;
    // Return false to let CityMapCanvas pan; detect clicks on mouseup.
    return false;
  }, []);

  const handlePointerMove = useCallback(
    (e: CityMapPointerEvent) => {
      lastScreenRef.current = e.screen;
      hoverRef.current = e.world;
      if (verticesRef.current.length > 0) redraw();
    },
    [redraw]
  );

  useEffect(() => {
    const onMouseUp = (e: MouseEvent) => {
      const press = pressRef.current;
      pressRef.current = null;
      if (!press || e.button !== 0) return;
      const last = lastScreenRef.current ?? press.screen;
      const moved = Math.hypot(last.x - press.screen.x, last.y - press.screen.y);
      if (moved > CLICK_THRESHOLD_PX) return;
      setVertices((prev) => [...prev, press.world]);
      setMessage(null);
    };
    window.addEventListener("mouseup", onMouseUp);
    return () => window.removeEventListener("mouseup", onMouseUp);
  }, []);

  const handleContextMenu = useCallback(() => {
    removeLastVertex();
  }, [removeLastVertex]);

  /* ---- Area overlay ---- */
  const overlayDraw = useCallback((ctx: CanvasRenderingContext2D, view: CityMapViewState) => {
    const pts = verticesRef.current;
    if (pts.length === 0) return;
    const px = (v: number) => v / view.zoom;

    if (pts.length >= 2) {
      ctx.beginPath();
      ctx.moveTo(pts[0].x, pts[0].z);
      for (let i = 1; i < pts.length; i++) ctx.lineTo(pts[i].x, pts[i].z);
      if (pts.length >= 3) {
        ctx.closePath();
        ctx.fillStyle = AREA_FILL;
        ctx.fill();
      }
      ctx.strokeStyle = AREA_EDGE;
      ctx.lineWidth = px(2);
      ctx.setLineDash([]);
      ctx.stroke();
    }

    // Provisional edge from the last vertex to the cursor
    const hover = hoverRef.current;
    if (hover) {
      const last = pts[pts.length - 1];
      ctx.beginPath();
      ctx.moveTo(last.x, last.z);
      ctx.lineTo(hover.x, hover.z);
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
  }, []);

  /* ---- Creation ---- */
  async function createStreetGrid() {
    if (busy || vertices.length < 3 || variant === null) return;
    setBusy(true);
    setMessage(null);
    try {
      const result = await fetchApi<StreetGridResult>(buildApiUrl(apiBaseUrl, "/api/v1/scenario-tools/street-grid"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          points: vertices.map((p) => [p.x, p.z]),
          spacing: blockMode ? undefined : spacing,
          parcel_size: blockMode ? parcelSize : undefined,
          pattern,
          path_variant: variant,
          angle_deg: blockMode || angleAuto ? null : angleDeg,
        }),
      });
      const rejectedTotal = Object.values(result.rejected).reduce((a, b) => a + b, 0);
      const reasons = Object.entries(result.rejected)
        .filter(([, n]) => n > 0)
        .map(([key, n]) => `${t(`scenarioTools.reject.${key}`, { defaultValue: key })}: ${n}`)
        .join(" / ");
      setMessage(
        (blockMode && result.blocks === 0
          ? t("scenarioTools.noBlocks")
          : t("scenarioTools.gridCreated", { created: result.created, rejected: rejectedTotal })) +
          (reasons ? ` (${reasons})` : "")
      );
      setVertices([]);
      // Reflect planned streets in 3D and refresh the map's road data.
      await sendSessionReload();
      mapRef.current?.reload();
    } catch (err) {
      if (err instanceof ApiError) {
        const i18nKey = `errors.${err.code}`;
        setMessage(i18nKey !== "errors.UNKNOWN_ERROR" ? t(i18nKey, { defaultValue: err.message }) : err.message);
      } else {
        setMessage(t("scenarioTools.gridFailed"));
      }
    } finally {
      setBusy(false);
    }
  }

  const canCreate = vertices.length >= 3 && variant !== null && !busy;

  return (
    <div className="street-grid">
      <div className="street-grid__toolbar">
        <div className="street-grid__hint">
          {vertices.length < 3 ? t("scenarioTools.hintAddVertices") : t("scenarioTools.hintReady")}
          <span className="street-grid__count">{t("scenarioTools.vertexCount", { count: vertices.length })}</span>
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
              min={blockMode ? 15 : SPACING_MIN_M}
              max={blockMode ? 25 : SPACING_MAX_M}
              step={blockMode ? 1 : 10}
              value={blockMode ? parcelSize : spacing}
              disabled={busy}
              onChange={(e) => {
                const v = Number.parseInt(e.target.value, 10);
                if (!Number.isNaN(v)) {
                  if (blockMode) setParcelSize(Math.max(15, Math.min(25, v)));
                  else setSpacing(Math.max(SPACING_MIN_M, Math.min(SPACING_MAX_M, v)));
                }
              }}
            />
            <span className="street-grid__unit">m</span>
          </label>
          <label className="street-grid__field">
            <span>{t("scenarioTools.variant")}</span>
            <select value={variant ?? ""} disabled={busy || streetVariants.length === 0} onChange={(e) => setVariant(Number(e.target.value))}>
              {streetVariants.map((v) => (
                <option key={v.id} value={v.id}>
                  {v.name}
                </option>
              ))}
            </select>
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
        </div>
        <div className="street-grid__actions">
          <button type="button" className="street-grid__btn" disabled={busy || vertices.length === 0} onClick={removeLastVertex}>
            {t("scenarioTools.undoVertex")}
          </button>
          <button type="button" className="street-grid__btn" disabled={busy || vertices.length === 0} onClick={() => setVertices([])}>
            {t("scenarioTools.clearVertices")}
          </button>
          <button type="button" className="street-grid__btn street-grid__btn--primary" disabled={!canCreate} onClick={() => void createStreetGrid()}>
            {busy ? t("scenarioTools.creating") : t("scenarioTools.create")}
          </button>
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
          onPointerDown={handlePointerDown}
          onPointerMove={handlePointerMove}
          onContextMenu={handleContextMenu}
          cursor="crosshair"
        />
        {message && <div className="street-grid__message">{message}</div>}
      </div>
    </div>
  );
}
