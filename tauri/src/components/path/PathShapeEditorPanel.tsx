/**
 * 2D path shape editor (window_type="path_shape_editor").
 *
 * Edit nodes and Bézier controls of paths under placement (roads/tracks/infrastructure) from above on CityMapCanvas.
 * Unity's RoadPlanManager remains the shape's source of truth. This panel:
 *   - receives and draws edit_path:update_shape broadcasts sent to all WebViews;
 *   - sends only operation results through edit_path:shape_command, once at drag end.
 * Unity applies operations and returns update_shape, keeping the shape consistent with the 3D view.
 *
 * Controls (matching Unity's editing phase, without snapping):
 *   Left-drag node/control = move in XZ only, preserving 3D height; left-click node = select;
 *   left-click edge = insert waypoint; right-click node = delete; Delete = delete selected node;
 *   Esc = cancel drag; left-drag empty space = pan; wheel = zoom.
 * During placing, display only; editing begins after right-click confirmation in 3D (editing).
 * Elevation/embankment editing belongs to the placement panel steppers (node selection stays synchronized with 3D).
 *
 * For elevated/underground (height_editable) variants, show PathProfileStrip beneath the map,
 * where vertical dragging edits node height (Z direction) only.
 * Remounted on each open, so request the current shape with edit_path:query_shape on mount.
 * Close this panel when notified that placement ended (phase=inactive).
 * Closing this panel leaves the placement session running and sends nothing to Unity.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import {
  CityMapCanvas,
  DEFAULT_CITY_MAP_LAYERS,
  type CityMapBounds,
  type CityMapCanvasHandle,
  type CityMapPointerEvent,
  type CityMapViewState,
} from "../map/CityMapCanvas";
import {
  IPC_EVENT_CAMERA_STATE,
  IPC_EVENT_EDIT_PATH_UPDATE_SHAPE,
  sendCameraQueryState,
  sendEditPathQueryShape,
  sendEditPathShapeCommand,
} from "../../utils/ipc";
import type { PathMaxSpanViolation, PathShapeUpdatePayload } from "./pathBuildConstants";
import {
  SHAPE_HIT_OPTIONS,
  applyLocalMove,
  buildShapeGeometry,
  drawPathShape,
  hitTestShape,
  nearestAlignmentSample,
  sameHit,
  shapeBounds,
  type PathShapeGeometry,
  type ShapeDrag,
  type ShapeHit,
} from "./pathShapeOverlay";
import { PathProfileStrip } from "./PathProfileStrip";
import { RouteProposalControls } from "./RouteProposalControls";
import { useRouteProposal } from "./useRouteProposal";
import "./PathShapeEditorPanel.css";

/** Click/drag movement threshold (screen px), matching Unity InputHandler. */
const DRAG_THRESHOLD_PX = 5;

/** Search radius for alignment samples showing radius/grade beside the cursor (screen px). */
const ALIGNMENT_HOVER_PX = 24;

/** Half-span (m) centered on the camera when no points exist. */
const CAMERA_FALLBACK_HALF_EXTENT = 300;

/** Overview padding (fraction of bounds and minimum in m). */
const FIT_PADDING_RATIO = 0.25;
const FIT_PADDING_MIN_M = 40;

/** Map layers. Disable elevation colors to keep map features readable (as in CityMapWindow). Use a constant to avoid refetching on rerender. */
const MAP_LAYERS = { ...DEFAULT_CITY_MAP_LAYERS, elevation: false };

/** Cursor shapes by hit target. */
function cursorForHit(hit: ShapeHit | null): string {
  if (!hit) return "";
  if (hit.type === "edge") return "copy";
  return "grab";
}

export function PathShapeEditorPanel({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const mapRef = useRef<CityMapCanvasHandle>(null);
  const statusRef = useRef<HTMLSpanElement | null>(null);

  const [shape, setShape] = useState<PathShapeUpdatePayload | null>(null);
  const [overlayVersion, setOverlayVersion] = useState(0);
  const [cursor, setCursor] = useState("");
  // Initial map bounds. CityMapCanvas fits initialBounds (or the entire city if absent) after terrain loads,
  // overwriting any earlier fitBounds call. Determine bounds from the first shape (or camera position
  // when no points exist) before mounting the map to fit the entire path being placed.
  const [initialBounds, setInitialBounds] = useState<CityMapBounds | null>(null);
  const routeProposal = useRouteProposal({ apiBaseUrl, payload: shape });

  // CityMapCanvas mirrors pointer handlers in refs, but the handlers also need current shape, hover,
  // and drag state without being recreated, so keep those in refs.
  const geom = useMemo(() => buildShapeGeometry(shape), [shape]);
  const geomRef = useRef<PathShapeGeometry>(geom);
  geomRef.current = geom;
  const shapeRef = useRef<PathShapeUpdatePayload | null>(shape);
  shapeRef.current = shape;
  const hoverRef = useRef<ShapeHit | null>(null);
  const dragRef = useRef<(ShapeDrag & { start: { x: number; y: number }; moved: boolean }) | null>(null);
  // Fit the full shape only when the first point sequence arrives, preserving subsequent user pan/zoom.
  const fittedRef = useRef(false);

  const phase = shape?.phase ?? "inactive";
  const editing = phase === "editing";
  const editingRef = useRef(editing);
  editingRef.current = editing;

  const redraw = useCallback(() => setOverlayVersion((v) => v + 1), []);

  /** Fit the shape's bounding rectangle. Do nothing without points. */
  const fitToShape = useCallback((g: PathShapeGeometry) => {
    const bounds = shapeBounds(g, FIT_PADDING_RATIO, FIT_PADDING_MIN_M);
    if (bounds) mapRef.current?.fitBounds(bounds);
  }, []);

  /* ---- Receive shapes from Unity and query initially ---- */
  useEffect(() => {
    let unlistenShape: (() => void) | undefined;
    let unlistenCamera: (() => void) | undefined;

    listen<PathShapeUpdatePayload>(IPC_EVENT_EDIT_PATH_UPDATE_SHAPE, (event) => {
      const payload = event.payload;
      if (payload.phase === "inactive") {
        // Placement session ended (confirmation/cancellation/panel close). Close only this panel.
        void invoke("close_current_window").catch(console.error);
        return;
      }
      setShape(payload);
      const g = buildShapeGeometry(payload);
      if (!fittedRef.current && g.anchors.length > 0) {
        fittedRef.current = true;
        // Pass as initial bounds if the map is not mounted. If already mounted around the camera,
        // replacing initialBounds rebuilds the map and fits these bounds after terrain loads.
        const bounds = shapeBounds(g, FIT_PADDING_RATIO, FIT_PADDING_MIN_M);
        if (bounds) setInitialBounds(bounds);
      }
    })
      .then((fn) => { unlistenShape = fn; })
      .catch(console.error);

    // Initial view when opened without points: center on the current 3D camera position.
    listen<{ position?: number[] }>(IPC_EVENT_CAMERA_STATE, (event) => {
      const pos = event.payload?.position;
      if (fittedRef.current || !pos || pos.length < 3) return;
      setInitialBounds((prev) => prev ?? {
        x1: pos[0] - CAMERA_FALLBACK_HALF_EXTENT,
        z1: pos[2] - CAMERA_FALLBACK_HALF_EXTENT,
        x2: pos[0] + CAMERA_FALLBACK_HALF_EXTENT,
        z2: pos[2] + CAMERA_FALLBACK_HALF_EXTENT,
      });
    })
      .then((fn) => { unlistenCamera = fn; })
      .catch(console.error);

    void sendEditPathQueryShape().catch(console.error);
    void sendCameraQueryState().catch(console.error);

    return () => {
      unlistenShape?.();
      unlistenCamera?.();
    };
  }, []);

  /* ---- Update overlays when shape changes ---- */
  useEffect(() => {
    redraw();
  }, [geom, redraw]);

  /* ---- Keyboard: Esc cancels dragging; Delete removes the selected node ---- */
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape" && dragRef.current) {
        dragRef.current = null;
        redraw();
        return;
      }
      if (e.key === "Delete" && editingRef.current && !dragRef.current) {
        const idx = shapeRef.current?.selected_index ?? -1;
        if (idx >= 0) void sendEditPathShapeCommand({ op: "remove_node", index: idx }).catch(console.error);
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [redraw]);

  /* ---- Pointer controls ---- */
  const handlePointerDown = useCallback((e: CityMapPointerEvent): boolean => {
    if (!editingRef.current) return false;
    const hit = hitTestShape(geomRef.current, e.screen, e.view, SHAPE_HIT_OPTIONS);
    if (!hit) return false;
    if (hit.type === "edge") {
      void sendEditPathShapeCommand({ op: "insert_node", edge_index: hit.index, x: hit.world.x, z: hit.world.z })
        .catch(console.error);
      return true;
    }
    dragRef.current = { hit, world: e.world, start: e.screen, moved: false };
    setCursor("grabbing");
    redraw();
    return true;
  }, [redraw]);

  const updateStatus = useCallback((e: CityMapPointerEvent) => {
    if (!statusRef.current) return;
    const near = nearestAlignmentSample(shapeRef.current?.alignment, e.screen, e.view, ALIGNMENT_HOVER_PX);
    const coord = `${e.world.x.toFixed(1)}, ${e.world.z.toFixed(1)}`;
    if (!near) {
      statusRef.current.textContent = coord;
      return;
    }
    const radius = near.radius == null ? t("path_shape_editor.straight") : `${near.radius.toFixed(1)}m`;
    statusRef.current.textContent = `${coord}   R ${radius}   G ${near.grade.toFixed(2)}%`;
  }, [t]);

  const handlePointerMove = useCallback((e: CityMapPointerEvent) => {
    updateStatus(e);
    const drag = dragRef.current;
    if (drag) {
      drag.world = e.world;
      if (!drag.moved && Math.hypot(e.screen.x - drag.start.x, e.screen.y - drag.start.y) >= DRAG_THRESHOLD_PX) {
        drag.moved = true;
      }
      if (drag.moved) redraw();
      return;
    }
    const hit = editingRef.current ? hitTestShape(geomRef.current, e.screen, e.view, SHAPE_HIT_OPTIONS) : null;
    if (!sameHit(hit, hoverRef.current)) {
      hoverRef.current = hit;
      setCursor(cursorForHit(hit));
      redraw();
    } else if (hit?.type === "edge" && hoverRef.current?.type === "edge") {
      // Over an edge, the insertion preview follows the cursor.
      hoverRef.current = hit;
      redraw();
    }
  }, [redraw, updateStatus]);

  const handlePointerUp = useCallback((e: CityMapPointerEvent) => {
    const drag = dragRef.current;
    dragRef.current = null;
    setCursor(cursorForHit(hoverRef.current));
    if (!drag || drag.hit.type === "edge") {
      redraw();
      return;
    }
    const hit = drag.hit;
    if (!drag.moved) {
      // A click without movement selects a node. Clicking a control point does nothing.
      if (hit.type === "node") {
        setShape((prev) => (prev ? { ...prev, selected_index: hit.index } : prev));
        void sendEditPathShapeCommand({ op: "select_node", index: hit.index }).catch(console.error);
      }
      redraw();
      return;
    }
    // Drag end: apply locally before sending so the display does not revert while awaiting Unity.
    const world = e.world;
    setShape((prev) => (prev ? applyLocalMove(prev, hit, world) : prev));
    if (hit.type === "node") {
      void sendEditPathShapeCommand({ op: "move_node", index: hit.index, x: world.x, z: world.z }).catch(console.error);
    } else {
      void sendEditPathShapeCommand({ op: "move_control", index: hit.index, which: hit.which, x: world.x, z: world.z })
        .catch(console.error);
    }
  }, [redraw]);

  const handleContextMenu = useCallback((e: CityMapPointerEvent) => {
    if (!editingRef.current || dragRef.current) return;
    const hit = hitTestShape(geomRef.current, e.screen, e.view, SHAPE_HIT_OPTIONS);
    if (hit?.type === "node") {
      void sendEditPathShapeCommand({ op: "remove_node", index: hit.index }).catch(console.error);
    }
  }, []);

  /* ---- Overlays ---- */
  const labelLines = useCallback((label: { curve: boolean; value: number; limit: number }) => {
    const value = label.value.toFixed(2);
    const limit = Number(label.limit.toFixed(2)).toString();
    const text = label.curve
      ? t("path_shape_editor.curveLabel", { value, limit })
      : t("path_shape_editor.gradeLabel", { value, limit });
    return text.split("\n");
  }, [t]);

  const spanLabelLines = useCallback((violation: PathMaxSpanViolation) =>
    t("path_shape_editor.spanLabel", {
      value: violation.distance.toFixed(2), limit: Number(violation.limit.toFixed(2)),
    }).split("\n"), [t]);

  const heightText = useCallback((relHeight: number, embankment: number) => {
    const parts: string[] = [];
    if (Math.abs(relHeight) >= 0.05) parts.push(`H ${relHeight > 0 ? "+" : ""}${relHeight.toFixed(1)}`);
    if (Math.abs(embankment) >= 0.05) parts.push(`E ${embankment > 0 ? "+" : ""}${embankment.toFixed(1)}`);
    return parts.length > 0 ? parts.join(" ") : null;
  }, []);

  const overlayDraw = useCallback((ctx: CanvasRenderingContext2D, view: CityMapViewState) => {
    const s = shapeRef.current;
    drawPathShape(ctx, view, geomRef.current, {
      hover: hoverRef.current,
      drag: dragRef.current,
      selectedIndex: s?.selected_index ?? -1,
      lateralOffsets: s?.lateral_offsets ?? [],
      alignment: s?.alignment,
      maxSpanViolations: s?.max_span_violations,
      spanLabelLines,
      labelLines,
      heightText,
    });
  }, [labelLines, spanLabelLines, heightText]);

  /** Apply profile-strip height changes optimistically to local state (preserve display until Unity's update_shape arrives). */
  const handleLocalHeightChange = useCallback((index: number, relHeight: number) => {
    setShape((prev) => {
      if (!prev?.selected_points?.[index]) return prev;
      const pts = prev.selected_points.map((pt) => pt.slice());
      const pt = pts[index];
      if (pt.length >= 3) pt[1] = relHeight; else pts[index] = [pt[0], relHeight, pt[1]];
      return { ...prev, selected_points: pts, selected_index: index, alignment: undefined };
    });
  }, []);

  /* ---- Display text ---- */
  const hint = !shape || phase === "inactive"
    ? t("path_shape_editor.hintInactive")
    : phase === "placing"
      ? t("path_shape_editor.hintPlacing")
      : geom.curved
        ? t("path_shape_editor.hintEditing")
        : `${t("path_shape_editor.hintEditing")} ${t("path_shape_editor.hintStraight")}`;

  const alignment = shape?.alignment;
  const formatLimit = (limit: number, unit: string) =>
    limit > 0 ? `${Number(limit.toFixed(2))}${unit}` : t("path_shape_editor.noLimit");

  return (
    <div className="pse">
      <section className="pse-plan" data-guide="path.plan" tabIndex={0} onPointerDownCapture={e => e.currentTarget.focus()}>
        <div className="pse-section-header">
          <span className="pse-section-title">{t("path_shape_editor.planTitle")}</span>
          <span className={`pse-hint ${editing ? "" : "pse-hint--muted"}`}>{hint}</span>
          <div className="pse-actions">
            <button type="button" className="pse-btn" onClick={() => fitToShape(geomRef.current)}>
              {t("path_shape_editor.fitView")}
            </button>
          </div>
        </div>
        <div className="pse-map">
        {initialBounds && (
        <CityMapCanvas
          ref={mapRef}
          apiBaseUrl={apiBaseUrl}
          layers={MAP_LAYERS}
          initialBounds={initialBounds}
          interactive
          showCompass
          overlayDraw={overlayDraw}
          overlayVersion={overlayVersion}
          onPointerDown={handlePointerDown}
          onPointerMove={handlePointerMove}
          onPointerUp={handlePointerUp}
          onContextMenu={handleContextMenu}
          cursor={cursor}
        />
        )}
        {/* Alignment summary. After full-path validation, show the same values as the construction panel and 3D view. */}
        {alignment && (
          <div className="pse-summary">
            <span className="pse-summary-item">
              <span className="pse-summary-label">{t("path_shape_editor.minRadius")}</span>
              <span className={`pse-summary-value ${alignment.curve_violated ? "pse-summary-value--bad" : ""}`}>
                {alignment.min_curve_radius == null ? t("path_shape_editor.straight") : `${alignment.min_curve_radius.toFixed(1)}m`}
              </span>
              <span className="pse-summary-limit">
                {t("path_shape_editor.limit")} {formatLimit(alignment.radius_limit, "m")}
              </span>
            </span>
            <span className="pse-summary-item">
              <span className="pse-summary-label">{t("path_shape_editor.maxGrade")}</span>
              <span className={`pse-summary-value ${alignment.grade_violated ? "pse-summary-value--bad" : ""}`}>
                {alignment.max_grade.toFixed(2)}%
              </span>
              <span className="pse-summary-limit">
                {t("path_shape_editor.limit")} {formatLimit(alignment.grade_limit, "%")}
              </span>
            </span>
          </div>
        )}
        {/* Cursor position and nearby alignment sample radius/grade (lower right). */}
        <div className="pse-status">
          <span ref={statusRef}>---, ---</span>
        </div>
        </div>
      </section>
      {/* Show the longitudinal profile (height editor) strip below the map only for elevated/underground variants. */}
      {shape?.height_editable === true && (
        <PathProfileStrip
          shape={shape}
          geom={geom}
          editing={editing}
          onLocalHeightChange={handleLocalHeightChange}
        />
      )}
      <RouteProposalControls state={routeProposal} />
    </div>
  );
}
