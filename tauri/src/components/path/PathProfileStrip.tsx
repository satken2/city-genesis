/**
 * Longitudinal profile strip (lower section of the 2D path shape editor).
 *
 * During elevated road/track placement (height_editable), plot road/ground elevations against distance from the start,
 * unrolling the curve into a straight horizontal axis. Drag nodes vertically to edit ground-relative heights
 * (the same values as the elevation stepper). Horizontal XZ movement is unavailable. Node numbers are one-based,
 * shared with the map and Unity's 3D view.
 *
 * Send nothing while dragging; on release send edit_path:change_node once (the same event as the elevation stepper).
 * Unity clamps to min_height, applies the change, and responds with edit_path:update_shape and edit_path:select_node.
 * A click without movement selects the node (select_node in edit_path:shape_command).
 * React-independent model building, drawing, and hit testing are in pathProfile.ts.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { sendEditPathChangeNode, sendEditPathShapeCommand } from "../../utils/ipc";
import type { PathShapeUpdatePayload } from "./pathBuildConstants";
import type { PathShapeGeometry } from "./pathShapeOverlay";
import {
  buildProfileLayout,
  buildProfileModel,
  drawProfile,
  hitTestProfileNode,
  metersPerPixel,
  type ProfileModel,
} from "./pathProfile";

/** Movement threshold separating clicks from drags (screen px). */
const DRAG_THRESHOLD_PX = 3;

type Props = {
  shape: PathShapeUpdatePayload | null;
  geom: PathShapeGeometry;
  /** Whether editing is allowed (phase=editing). Display-only during placement. */
  editing: boolean;
  /** Callback applying height changes optimistically to local state (retain display until Unity responds). */
  onLocalHeightChange: (index: number, relHeight: number) => void;
};

export function PathProfileStrip({ shape, geom, editing, onLocalHeightChange }: Props) {
  const { t } = useTranslation();
  const wrapRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const [cursor, setCursor] = useState("");

  const model = useMemo(() => buildProfileModel(shape, geom), [shape, geom]);
  const modelRef = useRef<ProfileModel | null>(model);
  modelRef.current = model;
  const selectedIndex = shape?.selected_index ?? -1;
  const selectedRef = useRef(selectedIndex);
  selectedRef.current = selectedIndex;
  const editingRef = useRef(editing);
  editingRef.current = editing;
  const minHeightRef = useRef(shape?.min_height ?? 0);
  minHeightRef.current = shape?.min_height ?? 0;

  const hoverRef = useRef(-1);
  const dragRef = useRef<{ index: number; startY: number; startRel: number; rel: number; moved: boolean } | null>(null);
  const previewRef = useRef<Map<number, number>>(new Map());

  const gradeLabelLines = useCallback((value: number, limit: number) => {
    const text = t("path_shape_editor.gradeLabel", {
      value: value.toFixed(2),
      limit: Number(limit.toFixed(2)).toString(),
    });
    return text.split("\n");
  }, [t]);

  /** Redraw the canvas at its current size and DPR. */
  const draw = useCallback(() => {
    const canvas = canvasRef.current;
    const wrap = wrapRef.current;
    if (!canvas || !wrap) return;
    const dpr = window.devicePixelRatio || 1;
    const w = wrap.clientWidth;
    const h = wrap.clientHeight;
    if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
      canvas.width = Math.max(1, Math.round(w * dpr));
      canvas.height = Math.max(1, Math.round(h * dpr));
      canvas.style.width = `${w}px`;
      canvas.style.height = `${h}px`;
    }
    const ctx = canvas.getContext("2d");
    if (!ctx) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    const m = modelRef.current;
    if (!m) {
      ctx.clearRect(0, 0, w, h);
      return;
    }
    const layout = buildProfileLayout(m, w, h, previewRef.current);
    drawProfile(ctx, m, layout, {
      hoverIndex: hoverRef.current,
      dragIndex: dragRef.current?.index ?? -1,
      selectedIndex: selectedRef.current,
      previewHeights: previewRef.current,
      gradeLabelLines,
      groundText: t("path_shape_editor.ground"),
    });
  }, [gradeLabelLines, t]);

  useEffect(() => {
    draw();
  }, [draw, model, selectedIndex]);

  useEffect(() => {
    const wrap = wrapRef.current;
    if (!wrap) return;
    const observer = new ResizeObserver(() => draw());
    observer.observe(wrap);
    return () => observer.disconnect();
  }, [draw]);

  /* ---- Pointer operations (vertical only) ---- */
  const toScreen = (e: MouseEvent | React.MouseEvent) => {
    const rect = canvasRef.current!.getBoundingClientRect();
    return { x: e.clientX - rect.left, y: e.clientY - rect.top };
  };

  const currentLayout = () => {
    const wrap = wrapRef.current;
    const m = modelRef.current;
    if (!wrap || !m) return null;
    return buildProfileLayout(m, wrap.clientWidth, wrap.clientHeight, previewRef.current);
  };

  const handleMouseDown = useCallback((e: React.MouseEvent<HTMLCanvasElement>) => {
    if (e.button !== 0 || !editingRef.current) return;
    const m = modelRef.current;
    const layout = currentLayout();
    if (!m || !layout) return;
    const s = toScreen(e);
    const idx = hitTestProfileNode(m, layout, s, previewRef.current);
    if (idx < 0) return;
    const node = m.nodes[idx];
    dragRef.current = { index: idx, startY: s.y, startRel: node.relHeight, rel: node.relHeight, moved: false };
    setCursor("ns-resize");
    e.preventDefault();
    draw();
  }, [draw]);

  useEffect(() => {
    const onMove = (e: MouseEvent) => {
      const m = modelRef.current;
      if (!m || !canvasRef.current) return;
      const s = toScreen(e);
      const drag = dragRef.current;
      if (drag) {
        const layout = currentLayout();
        if (!layout) return;
        if (!drag.moved && Math.abs(s.y - drag.startY) >= DRAG_THRESHOLD_PX) drag.moved = true;
        if (!drag.moved) return;
        // Moving upward increases height. Minimum is master min_height, matching Unity's clamp.
        const rel = Math.max(minHeightRef.current, drag.startRel + (drag.startY - s.y) * metersPerPixel(layout));
        drag.rel = Math.round(rel * 10) / 10;
        previewRef.current = new Map([[drag.index, drag.rel]]);
        draw();
        return;
      }
      const layout = currentLayout();
      if (!layout) return;
      const idx = editingRef.current ? hitTestProfileNode(m, layout, s, previewRef.current) : -1;
      if (idx !== hoverRef.current) {
        hoverRef.current = idx;
        setCursor(idx >= 0 ? "ns-resize" : "");
        draw();
      }
    };
    const onUp = () => {
      const drag = dragRef.current;
      if (!drag) return;
      dragRef.current = null;
      previewRef.current = new Map();
      setCursor(hoverRef.current >= 0 ? "ns-resize" : "");
      if (drag.moved) {
        if (drag.rel !== drag.startRel) {
          // Apply locally before sending to prevent the display reverting while awaiting Unity's response.
          onLocalHeightChange(drag.index, drag.rel);
          void sendEditPathChangeNode(drag.index, drag.rel).catch(console.error);
        }
      } else {
        void sendEditPathShapeCommand({ op: "select_node", index: drag.index }).catch(console.error);
      }
      draw();
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" && dragRef.current) {
        dragRef.current = null;
        previewRef.current = new Map();
        draw();
      }
    };
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
    window.addEventListener("keydown", onKey);
    return () => {
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
      window.removeEventListener("keydown", onKey);
    };
  }, [draw, onLocalHeightChange]);

  const handleMouseLeave = useCallback(() => {
    if (hoverRef.current !== -1 && !dragRef.current) {
      hoverRef.current = -1;
      setCursor("");
      draw();
    }
  }, [draw]);

  return (
    <div className="pse-profile" data-guide="path.profile" tabIndex={0} onPointerDownCapture={e => e.currentTarget.focus()}>
      <div className="pse-section-header">
        <span className="pse-section-title">{t("path_shape_editor.profileTitle")}</span>
        <span className={`pse-hint ${editing ? "" : "pse-hint--muted"}`}>
          {editing ? t("path_shape_editor.profileHint") : t("path_shape_editor.profileReadOnly")}
        </span>
      </div>
      <div ref={wrapRef} className="pse-profile-canvas-wrap">
        <canvas
          ref={canvasRef}
          style={{ display: "block", cursor }}
          onMouseDown={handleMouseDown}
          onMouseLeave={handleMouseLeave}
          onContextMenu={(e) => e.preventDefault()}
        />
      </div>
    </div>
  );
}
