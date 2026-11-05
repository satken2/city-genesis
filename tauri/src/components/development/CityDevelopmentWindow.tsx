/**
 * 都市の自動開発ウィンドウ(window_type="city_development")。
 *
 * 道路の並べ方(パターン)・道路の間隔・区画の面積を選び、CityMapCanvas をブラシで塗った範囲に道路と土地区画を
 * 計画する。範囲の地形をなだらかにする整地・道路の並べ方・区画の形・既存の道路と区画を潰さない規則はコアの
 * POST /api/v1/city-development/preview が決め(なだらかにした後の想定で計画を返す)、確定は同じ入力で
 * POST /api/v1/city-development を呼ぶ。確定は現在の入力のプレビュー後にだけ許し、入力を変えたらプレビューを捨てる。
 *
 * Ctrl+左ドラッグで塗り、左ドラッグで地図を動かす(地区編集と同じ操作)。ブラシの半径は画面のピクセルで固定なので、
 * 縮小すると広く塗れる。筆跡は塗り始めの半径(m)と点列のままコアへ送り、範囲の面はコアが作る。
 * 塗った範囲とプレビューはデータが変わったときだけ overlayDraw に描き、塗っている途中の筆跡とブラシの円は
 * animatedOverlayDraw に描く。地図の表示領域を確保するため、操作案内はヒントバーに出す。
 *
 * 確定では、選んだ建設プロジェクトのタグ(空ならタグなし)を作った道路に付ける。
 *
 * 最初に受けた camera:state だけを使い、3Dカメラ位置に地図を合わせる。
 */

import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { emit, listen } from "@tauri-apps/api/event";

import {
  CityMapCanvas,
  DEFAULT_CITY_MAP_LAYERS,
  type CityMapCanvasHandle,
  type CityMapLayerVisibility,
  type CityMapPointerEvent,
  type CityMapViewState,
} from "../map/CityMapCanvas";
import { paintThroughScratch } from "../map/paintThroughScratch";
import { TagPicker } from "../common/TagPicker";
import { TAGS_CHANGED_EVENT } from "../../utils/constructionTags";
import { IPC_EVENT_CAMERA_STATE, sendCameraQueryState, sendSessionReload } from "../../utils/ipc";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { describeError } from "../../utils/errors";
import { clearHintbarTextIf, setHintbarText } from "../../utils/hintbar";
import { useMasterData } from "../../contexts/MasterDataContext";
import { formatArea } from "../../utils/formatArea";
import { resolveIntlLocale } from "../../utils/locale";
import "../path/StreetGridWindow.css";

/** 道路の並べ方。コアの PATTERNS と同じ */
const PATTERNS = ["grid", "terrain_grid", "arterial"] as const;
type Pattern = (typeof PATTERNS)[number];

/** 道路の間隔(m)と区画の面積(m²)の既定値と範囲。範囲はコアの検証と同じ */
const DEFAULT_SPACING_M = 70;
const SPACING_MIN_M = 20;
const SPACING_MAX_M = 500;
const DEFAULT_PARCEL_AREA_M2 = 600;
const PARCEL_AREA_MIN_M2 = 50;
const PARCEL_AREA_MAX_M2 = 20000;

type WorldPoint = { x: number; z: number };
type Stroke = { radius: number; points: WorldPoint[] };

/** コアの plan_city_development の戻り値 */
type DevelopmentPlan = {
  roads: { path_variant: number; points: [number, number][] }[];
  parcels: [number, number][][];
  rejected: Record<string, number>;
  area_m2: number;
};

/** コアの create_city_development の戻り値 */
type DevelopmentResult = {
  path_ids: number[];
  parcel_ids: number[];
  rejected: Record<string, number>;
};

/** 3Dカメラの状態(camera:state)。位置だけを使う */
type CameraStatePayload = { position?: number[] };

/** カメラ位置に地図を合わせるときの半径(m) */
const CAMERA_HALF_EXTENT_M = 600;

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

/** 塗る範囲の傾斜が分かるよう地形の色と陰影を出し、既存の区画の輪郭を重ねる。建物は隠す */
const MAP_LAYERS: Partial<CityMapLayerVisibility> = {
  ...DEFAULT_CITY_MAP_LAYERS,
  elevation: true,
  parcels: true,
  buildings: false,
};

function strokePath(ctx: CanvasRenderingContext2D, stroke: Stroke): void {
  ctx.lineWidth = stroke.radius * 2;
  ctx.beginPath();
  ctx.moveTo(stroke.points[0].x, stroke.points[0].z);
  // 1点だけの筆跡も丸い線端で円になるよう、同じ点へ線を引く。
  for (const p of stroke.points.length > 1 ? stroke.points.slice(1) : stroke.points) ctx.lineTo(p.x, p.z);
  ctx.stroke();
}

export function CityDevelopmentWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const masterData = useMasterData();
  const mapRef = useRef<CityMapCanvasHandle>(null);

  const [pattern, setPattern] = useState<Pattern>("arterial");
  // 入力途中の空欄もそのまま表示し、送信できるかを別に判定する。
  const [spacingInput, setSpacingInput] = useState(String(DEFAULT_SPACING_M));
  const [parcelAreaInput, setParcelAreaInput] = useState(String(DEFAULT_PARCEL_AREA_M2));
  const spacing = Number(spacingInput);
  const parcelArea = Number(parcelAreaInput);
  const inputsValid = Number.isFinite(spacing) && spacing >= SPACING_MIN_M && spacing <= SPACING_MAX_M
    && Number.isFinite(parcelArea) && parcelArea >= PARCEL_AREA_MIN_M2 && parcelArea <= PARCEL_AREA_MAX_M2;
  const [strokes, setStrokes] = useState<Stroke[]>([]);
  const [running, setRunning] = useState<"preview" | "create" | null>(null);
  const busy = running !== null;
  const [message, setMessage] = useState<string | null>(null);
  const [preview, setPreview] = useState<{ plan: DevelopmentPlan; summary: string } | null>(null);
  const [projectTag, setProjectTag] = useState("");
  const [overlayVersion, setOverlayVersion] = useState(0);

  // CityMapCanvas はポインタと描画のコールバックを ref で持ち回すので、最新の値は ref から読む。
  const busyRef = useRef(busy);
  busyRef.current = busy;
  const activeStrokeRef = useRef<Stroke | null>(null);
  const cursorRef = useRef<WorldPoint | null>(null);
  const paintScratchRef = useRef<HTMLCanvasElement | null>(null);
  const fittedRef = useRef(false);
  /** 入力を変えるたびに増やし、古い入力に対するプレビューの応答を捨てる */
  const inputVersionRef = useRef(0);

  const hint = t("cityDevelopment.hint");
  useEffect(() => {
    void setHintbarText(hint).catch(console.error);
    return () => {
      void clearHintbarTextIf(hint).catch(console.error);
    };
  }, [hint]);

  useEffect(() => {
    inputVersionRef.current += 1;
    setPreview(null);
  }, [strokes, pattern, spacingInput, parcelAreaInput]);

  /* ---- 3Dカメラの位置に一度だけ合わせる ---- */
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
    const plan = preview?.plan;
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
  }, [strokes, preview, masterData]);

  useEffect(() => setOverlayVersion((v) => v + 1), [overlayDraw]);

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
      body: JSON.stringify({
        pattern,
        spacing,
        parcel_area: parcelArea,
        strokes: strokes.map((s) => ({ radius: s.radius, points: s.points.map((p) => [p.x, p.z]) })),
        ...extra,
      }),
    });

  /** 結果の文に、作らなかった道路の理由別の本数を続ける。理由の名前は路地の一括作成と共通 */
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
    const version = inputVersionRef.current;
    setRunning("preview");
    setMessage(null);
    try {
      const plan = await postDevelopment<DevelopmentPlan>("/api/v1/city-development/preview");
      if (version !== inputVersionRef.current) return;
      const summary = withReasons(t("cityDevelopment.previewed", {
        area: formatArea(plan.area_m2, resolveIntlLocale(i18n.language)),
        roads: plan.roads.length,
        parcels: plan.parcels.length,
        rejected: rejectedTotal(plan.rejected),
      }), plan.rejected);
      setPreview({ plan, summary });
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
      setMessage(withReasons(t("cityDevelopment.created", {
        roads: result.path_ids.length,
        parcels: result.parcel_ids.length,
        rejected: rejectedTotal(result.rejected),
      }), result.rejected));
      activeStrokeRef.current = null;
      setStrokes([]);
      // 計画した道路と区画を3Dに反映し、地図の道路・区画を取り直す。
      await sendSessionReload();
      mapRef.current?.reload();
    } catch (err) {
      setMessage(describeError(err, t));
    } finally {
      setRunning(null);
    }
  }

  const canPreview = strokes.length > 0 && inputsValid && !busy;
  const canCreate = canPreview && preview !== null && (preview.plan.roads.length > 0 || preview.plan.parcels.length > 0);
  const shownMessage = preview?.summary ?? message;

  return (
    <div className="street-grid">
      <div className="street-grid__toolbar">
        <div className="street-grid__controls">
          <label className="street-grid__field">
            <span>{t("cityDevelopment.pattern")}</span>
            <select
              value={pattern}
              disabled={busy}
              data-guide="city_development.pattern"
              onChange={(e) => setPattern(e.target.value as Pattern)}
            >
              {PATTERNS.map((key) => (
                <option key={key} value={key}>{t(`cityDevelopment.patterns.${key}`)}</option>
              ))}
            </select>
          </label>
          <label className="street-grid__field">
            <span>{t("cityDevelopment.spacing")}</span>
            <input
              type="number"
              min={SPACING_MIN_M}
              max={SPACING_MAX_M}
              step={10}
              value={spacingInput}
              aria-invalid={!(spacing >= SPACING_MIN_M && spacing <= SPACING_MAX_M)}
              disabled={busy}
              data-guide="city_development.spacing"
              onChange={(e) => setSpacingInput(e.currentTarget.value)}
            />
            <span className="street-grid__unit">m</span>
          </label>
          <label className="street-grid__field">
            <span>{t("cityDevelopment.parcelArea")}</span>
            <input
              type="number"
              min={PARCEL_AREA_MIN_M2}
              max={PARCEL_AREA_MAX_M2}
              step={50}
              value={parcelAreaInput}
              aria-invalid={!(parcelArea >= PARCEL_AREA_MIN_M2 && parcelArea <= PARCEL_AREA_MAX_M2)}
              disabled={busy}
              data-guide="city_development.parcel_area"
              onChange={(e) => setParcelAreaInput(e.currentTarget.value)}
            />
            <span className="street-grid__unit">m²</span>
          </label>
          <div className="street-grid__field">
            <span>{t("tag.label")}</span>
            <TagPicker apiBaseUrl={apiBaseUrl} value={projectTag} onChange={setProjectTag} disabled={busy} />
          </div>
          <div className="street-grid__actions">
            <button type="button" className="street-grid__btn" disabled={busy || strokes.length === 0} onClick={clearArea}>
              {t("cityDevelopment.clear")}
            </button>
            <button
              type="button"
              className="street-grid__btn"
              disabled={!canPreview}
              data-guide="city_development.preview"
              onClick={() => void previewDevelopment()}
            >
              {running === "preview" ? t("cityDevelopment.previewing") : t("cityDevelopment.preview")}
            </button>
            <button
              type="button"
              className="street-grid__btn street-grid__btn--primary"
              disabled={!canCreate}
              data-guide="city_development.create"
              onClick={() => void createDevelopment()}
            >
              {running === "create" ? t("cityDevelopment.creating") : t("cityDevelopment.create")}
            </button>
          </div>
        </div>
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
          animatedOverlayDraw={drawLive}
          onCursorWorldPos={handleCursor}
          onPointerDown={handlePointerDown}
          onPointerMove={handlePointerMove}
          onPointerUp={handlePointerUp}
          cursor="crosshair"
        />
        {shownMessage && <div className="street-grid__message">{shownMessage}</div>}
      </div>
    </div>
  );
}
