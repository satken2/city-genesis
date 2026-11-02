import { describeError } from "../../utils/errors";
/**
 * 街路の一括作成ウィンドウ(window_type="street_grid")。通常の建設とシナリオ制作で共用する。
 *
 * CityMapCanvasで多角形を指定し、選択したパターンの街路を計画中(STATUS_SCHEDULED_CONSTRUCTION)で作る。
 * 地形追従・街区内配置・接続と線形の検証はPOST /api/v1/paths/street-gridが担当する。
 * プレビューは同じ入力で作成される街路を返すため、入力を変えたら破棄する。
 * 複数種類はバックエンドが最大勾配の小さい順に配置する。
 * 作成した街路は建設計画として通常の着工・竣工を待つ。シナリオ制作では一括完成も利用できる。
 *
 * 左クリックで頂点追加、右クリック・Delete・Backspaceで末尾削除、左ドラッグで移動、ホイールで拡大縮小する。
 * CityMapCanvasのパンを許可し、押下から解放までの移動距離で頂点追加とドラッグを判別する。
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
import "./StreetGridWindow.css";

type WorldPoint = { x: number; z: number };
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
  const { t } = useTranslation();
  const masterData = useMasterData();
  const mapRef = useRef<CityMapCanvasHandle>(null);

  const [vertices, setVertices] = useState<WorldPoint[]>([]);
  const [spacing, setSpacing] = useState(DEFAULT_SPACING_M);
  const [pattern, setPattern] = useState<StreetPattern>("relaxed_grid");
  const [parcelSize, setParcelSize] = useState(20);
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
  const hint = t(vertices.length < 3 ? "scenarioTools.hintAddVertices" : "scenarioTools.hintReady");

  useEffect(() => {
    void setHintbarText(hint).catch(console.error);
    return () => {
      void clearHintbarTextIf(hint).catch(console.error);
    };
  }, [hint]);

  // CityMapCanvas mirrors pointer handlers in refs, so also keep vertices and hover position
  // in refs to access them without recreating handlers.
  const verticesRef = useRef<WorldPoint[]>(vertices);
  verticesRef.current = vertices;
  const hoverRef = useRef<WorldPoint | null>(null);
  const pressRef = useRef<{ screen: { x: number; y: number }; world: WorldPoint } | null>(null);
  const lastScreenRef = useRef<{ x: number; y: number } | null>(null);
  const fittedRef = useRef(false);
  const previewRef = useRef<StreetGridPlan | null>(null);
  previewRef.current = preview?.plan ?? null;
  const pathMasterRef = useRef(masterData?.paths);
  pathMasterRef.current = masterData?.paths;
  /** Incremented on every input change so a preview response for older inputs is ignored. */
  const inputVersionRef = useRef(0);

  const redraw = useCallback(() => setOverlayVersion((v) => v + 1), []);

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
  }, [vertices, pattern, spacing, parcelSize, variants, angleAuto, angleDeg]);

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
  }, [vertices, preview, redraw]);

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

  /* ---- Area and preview overlay ---- */
  const overlayDraw = useCallback((ctx: CanvasRenderingContext2D, view: CityMapViewState) => {
    const px = (v: number) => v / view.zoom;
    const plan = previewRef.current;
    if (plan) {
      ctx.strokeStyle = PREVIEW_COLOR;
      ctx.lineCap = "round";
      ctx.lineJoin = "round";
      ctx.setLineDash([]);
      for (const segment of plan.segments) {
        ctx.beginPath();
        ctx.moveTo(segment.points[0][0], segment.points[0][1]);
        for (const [x, z] of segment.points.slice(1)) ctx.lineTo(x, z);
        ctx.lineWidth = Math.max(pathMasterRef.current?.[segment.path_variant]?.width ?? 0, px(PREVIEW_MIN_WIDTH_PX));
        ctx.stroke();
      }
    }

    const pts = verticesRef.current;
    if (pts.length === 0) return;

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

  /* ---- Preview and creation ---- */
  const postStreetGrid = <T,>(path: string) =>
    fetchApi<T>(buildApiUrl(apiBaseUrl, path), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        points: vertices.map((p) => [p.x, p.z]),
        spacing: blockMode ? undefined : spacing,
        parcel_size: blockMode ? parcelSize : undefined,
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
    if (!canCreate) return;
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
      setVertices([]);
      // Reflect planned streets in 3D and refresh the map's road data.
      await sendSessionReload();
      mapRef.current?.reload();
    } catch (err) {
      setMessage(describeError(err, t));
    } finally {
      setRunning(null);
    }
  }

  const canCreate = vertices.length >= 3 && (variants?.length ?? 0) > 0 && !busy;
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
            <button type="button" className="street-grid__btn" disabled={busy || vertices.length === 0} onClick={removeLastVertex}>
              {t("scenarioTools.undoVertex")}
            </button>
            <button type="button" className="street-grid__btn" disabled={busy || vertices.length === 0} onClick={() => setVertices([])}>
              {t("scenarioTools.clearVertices")}
            </button>
            <button type="button" className="street-grid__btn" disabled={!canCreate} onClick={() => void previewStreetGrid()}>
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
          onPointerDown={handlePointerDown}
          onPointerMove={handlePointerMove}
          onContextMenu={handleContextMenu}
          cursor="crosshair"
        />
        {shownMessage && <div className="street-grid__message">{shownMessage}</div>}
      </div>
    </div>
  );
}
