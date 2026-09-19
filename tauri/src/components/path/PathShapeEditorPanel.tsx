/**
 * 2Dパス形状編集パネル (window_type="path_shape_editor")
 *
 * 敷設中のパス(道路/軌道/インフラ)のノードとベジエ制御点を、都市マップ(CityMapCanvas)の上で
 * 真上から編集する。形状の真実源はUnity側(RoadPlanManager)のままで、このパネルは
 *   - `edit_path:update_shape`(全WebViewへブロードキャスト)で現在形状を受け取って描き、
 *   - 操作の結果だけを `edit_path:shape_command`(ドラッグ終了時に1回)で送る。
 * 送った結果はUnityが適用して update_shape で返すので、3Dビューと常に同じ形状になる。
 *
 * 操作(Unityの編集フェーズと同じ操作感。吸着はしない):
 *   ノード/制御点を左ドラッグ=移動(XZのみ。高さは3D側の値を維持) / ノードを左クリック=選択 /
 *   辺を左クリック=ウェイポイント挿入 / ノードを右クリック=削除 / Delete=選択中ノードを削除 /
 *   Esc=ドラッグ取消 / 空白を左ドラッグ=パン / ホイール=ズーム。
 * ノード配置中(placing)は表示のみで、3Dビューの右クリック確定後(editing)に編集できる。
 * 標高・盛土の編集は敷設パネルのステッパーに任せる(ノード選択は3Dと同期する)。
 *
 * 高架・地下(height_editable)のバリアントでは地図の下に縦断面ストリップ(PathProfileStrip)を出し、
 * そこではノードの高さ(Z方向)だけを上下ドラッグで編集する。
 * 開くたびに再マウントされるため、マウント時に `edit_path:query_shape` で現在形状を要求する。
 * 敷設セッションが終わった(phase=inactive)通知を受けたら自分を閉じる。
 * このパネルを閉じても敷設セッションは続く(Unityへは何も送らない)。
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
import type { PathShapeUpdatePayload } from "./pathBuildConstants";
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

/** クリックとドラッグを分ける移動量(画面px)。Unity InputHandler の閾値に合わせる */
const DRAG_THRESHOLD_PX = 5;

/** カーソル脇に半径・勾配を出す線形サンプルの探索半径(画面px) */
const ALIGNMENT_HOVER_PX = 24;

/** 点列が無いときにカメラ位置を中心に表示する範囲(m。片側) */
const CAMERA_FALLBACK_HALF_EXTENT = 300;

/** 全体表示の余白(範囲に対する比率と最小値m) */
const FIT_PADDING_RATIO = 0.25;
const FIT_PADDING_MIN_M = 40;

/** 地図レイヤー。標高色分けは地物の読み取りを妨げるのでOFF(都市マップウィンドウと同じ)。再レンダーで再取得が走らないよう定数にする */
const MAP_LAYERS = { ...DEFAULT_CITY_MAP_LAYERS, elevation: false };

/** ヒット対象ごとのカーソル形状 */
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
  // 地図の初期表示範囲。CityMapCanvas は地形ロード後に initialBounds(無ければ都市全体)へ
  // ビューを合わせるため、先に fitBounds を呼んでも上書きされる。最初の形状(または点列が
  // 無いときのカメラ位置)から範囲を決めてから地図をマウントし、敷設中パス全体が収まるようにする。
  const [initialBounds, setInitialBounds] = useState<CityMapBounds | null>(null);
  const routeProposal = useRouteProposal({ apiBaseUrl, payload: shape });

  // ポインタハンドラは CityMapCanvas 側で ref ミラーされるが、ハンドラ自身も最新の形状・
  // ホバー・ドラッグを再生成なしで参照したいので ref に持つ。
  const geom = useMemo(() => buildShapeGeometry(shape), [shape]);
  const geomRef = useRef<PathShapeGeometry>(geom);
  geomRef.current = geom;
  const shapeRef = useRef<PathShapeUpdatePayload | null>(shape);
  shapeRef.current = shape;
  const hoverRef = useRef<ShapeHit | null>(null);
  const dragRef = useRef<(ShapeDrag & { start: { x: number; y: number }; moved: boolean }) | null>(null);
  // 最初に点列を受け取ったときだけ全体表示する(以後はユーザーのパン・ズームを壊さない)
  const fittedRef = useRef(false);

  const phase = shape?.phase ?? "inactive";
  const editing = phase === "editing";
  const editingRef = useRef(editing);
  editingRef.current = editing;

  const redraw = useCallback(() => setOverlayVersion((v) => v + 1), []);

  /** 形状の外接矩形へ全体表示する。点が無ければ何もしない */
  const fitToShape = useCallback((g: PathShapeGeometry) => {
    const bounds = shapeBounds(g, FIT_PADDING_RATIO, FIT_PADDING_MIN_M);
    if (bounds) mapRef.current?.fitBounds(bounds);
  }, []);

  /* ---- Unityからの形状受信・初期問い合わせ ---- */
  useEffect(() => {
    let unlistenShape: (() => void) | undefined;
    let unlistenCamera: (() => void) | undefined;

    listen<PathShapeUpdatePayload>(IPC_EVENT_EDIT_PATH_UPDATE_SHAPE, (event) => {
      const payload = event.payload;
      if (payload.phase === "inactive") {
        // 敷設セッションが終わった(確定・キャンセル・パネルクローズ)。このパネルだけを閉じる。
        void invoke("close_current_window").catch(console.error);
        return;
      }
      setShape(payload);
      const g = buildShapeGeometry(payload);
      if (!fittedRef.current && g.anchors.length > 0) {
        fittedRef.current = true;
        // 地図が未マウントなら初期範囲として渡す。カメラ位置で先にマウント済みなら
        // initialBounds の差し替えで地図が作り直され、地形ロード後にこの範囲へ合う。
        const bounds = shapeBounds(g, FIT_PADDING_RATIO, FIT_PADDING_MIN_M);
        if (bounds) setInitialBounds(bounds);
      }
    })
      .then((fn) => { unlistenShape = fn; })
      .catch(console.error);

    // 点列が空のまま開いたときの初期表示位置。3Dカメラの現在位置を中心にする。
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

  /* ---- 形状が変わったら重ね描きを更新する ---- */
  useEffect(() => {
    redraw();
  }, [geom, redraw]);

  /* ---- キー操作: Escでドラッグ取消、Deleteで選択中ノードを削除 ---- */
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

  /* ---- ポインタ操作 ---- */
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
      // 辺上は挿入位置プレビューがカーソルに追従する
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
      // クリック(移動なし)はノード選択。制御点のクリックは何もしない
      if (hit.type === "node") {
        setShape((prev) => (prev ? { ...prev, selected_index: hit.index } : prev));
        void sendEditPathShapeCommand({ op: "select_node", index: hit.index }).catch(console.error);
      }
      redraw();
      return;
    }
    // ドラッグ終了: Unityの応答が届くまで表示が戻らないよう先にローカルへ反映してから送る
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

  /* ---- 重ね描き ---- */
  const labelLines = useCallback((label: { curve: boolean; value: number; limit: number }) => {
    const value = label.value.toFixed(2);
    const limit = Number(label.limit.toFixed(2)).toString();
    const text = label.curve
      ? t("path_shape_editor.curveLabel", { value, limit })
      : t("path_shape_editor.gradeLabel", { value, limit });
    return text.split("\n");
  }, [t]);

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
      labelLines,
      heightText,
    });
  }, [labelLines, heightText]);

  /** 縦断面ストリップでの高さ変更をローカルへ先行反映する(Unityの update_shape が届くまでの表示維持) */
  const handleLocalHeightChange = useCallback((index: number, relHeight: number) => {
    setShape((prev) => {
      if (!prev?.selected_points?.[index]) return prev;
      const pts = prev.selected_points.map((pt) => pt.slice());
      const pt = pts[index];
      if (pt.length >= 3) pt[1] = relHeight; else pts[index] = [pt[0], relHeight, pt[1]];
      return { ...prev, selected_points: pts, selected_index: index };
    });
  }, []);

  /* ---- 表示文言 ---- */
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
      <div className="pse-toolbar">
        <span className={`pse-hint ${editing ? "" : "pse-hint--muted"}`}>{hint}</span>
        <div className="pse-actions">
          <button type="button" className="pse-btn" onClick={() => fitToShape(geomRef.current)}>
            {t("path_shape_editor.fitView")}
          </button>
        </div>
      </div>
      <RouteProposalControls state={routeProposal} />
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
        {/* 線形の要約(左下)。Unityのローカル推定で、権威値は敷設パネルの費用カードが表示する */}
        {alignment && (
          <div className="pse-summary">
            <span className="pse-summary-item">
              <span className="pse-summary-label">{t("path_shape_editor.minRadius")}</span>
              <span className={`pse-summary-value ${alignment.radius_limit > 0 && alignment.min_curve_radius != null && alignment.min_curve_radius < alignment.radius_limit ? "pse-summary-value--bad" : ""}`}>
                {alignment.min_curve_radius == null ? t("path_shape_editor.straight") : `${alignment.min_curve_radius.toFixed(1)}m`}
              </span>
              <span className="pse-summary-limit">
                {t("path_shape_editor.limit")} {formatLimit(alignment.radius_limit, "m")}
              </span>
            </span>
            <span className="pse-summary-item">
              <span className="pse-summary-label">{t("path_shape_editor.maxGrade")}</span>
              <span className={`pse-summary-value ${alignment.grade_limit > 0 && alignment.max_grade > alignment.grade_limit ? "pse-summary-value--bad" : ""}`}>
                {alignment.max_grade.toFixed(2)}%
              </span>
              <span className="pse-summary-limit">
                {t("path_shape_editor.limit")} {formatLimit(alignment.grade_limit, "%")}
              </span>
            </span>
          </div>
        )}
        {/* カーソル位置と、その近くの線形サンプルの半径・勾配(右下) */}
        <div className="pse-status">
          <span ref={statusRef}>---, ---</span>
        </div>
      </div>
      {/* 高架・地下のバリアントだけ、地図の下に縦断面(高さ編集)ストリップを出す */}
      {shape?.height_editable === true && (
        <PathProfileStrip
          shape={shape}
          geom={geom}
          editing={editing}
          onLocalHeightChange={handleLocalHeightChange}
        />
      )}
    </div>
  );
}
