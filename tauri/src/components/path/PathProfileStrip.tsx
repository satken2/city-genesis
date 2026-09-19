/**
 * 縦断面ストリップ(2Dパス形状編集パネルの下段)
 *
 * 高架道路・高架線路(height_editable)の敷設中に、曲線を伸ばして直線にした横軸(始点からの距離)に
 * 路面と地盤の標高を描き、ノードを上下ドラッグして地面相対の高さ(標高ステッパーと同じ値)を変える。
 * 横方向(XZ)は動かせない。ノード番号は地図・Unityの3Dビューと共通(1始まり)。
 *
 * ドラッグ中は送らず、ドラッグ終了時に `edit_path:change_node`(既存の標高ステッパーと同じイベント)を
 * 1回送る。Unityが min_height でクランプして適用し、`edit_path:update_shape` と `edit_path:select_node`
 * で返す。クリック(移動なし)はノード選択(`edit_path:shape_command` の select_node)。
 * モデル構築・描画・ヒット判定は React 非依存の `pathProfile.ts`。
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

/** クリックとドラッグを分ける移動量(画面px) */
const DRAG_THRESHOLD_PX = 3;

type Props = {
  shape: PathShapeUpdatePayload | null;
  geom: PathShapeGeometry;
  /** 編集できる状態(phase=editing)か。配置中は表示のみ */
  editing: boolean;
  /** 高さ変更をローカルへ先行反映するためのコールバック(Unityの応答までの表示維持) */
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

  /** キャンバスを現在のサイズ・DPRで描き直す */
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

  /* ---- ポインタ操作(縦方向のみ) ---- */
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
        // 上へ動かすと高くなる。下限はマスターの min_height(Unity側のクランプと同じ)
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
          // Unityの応答までの間、表示が元に戻らないようローカルへ先に反映してから送る
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
    <div className="pse-profile">
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
