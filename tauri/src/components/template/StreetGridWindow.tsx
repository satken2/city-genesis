/**
 * 路地作成ウィンドウ (window_type="street_grid")。テンプレートモード専用。
 *
 * 都市マップ(CityMapCanvas)の上で範囲の多角形を頂点指定し、その内側へ選択した形状の路地を
 * 計画中(STATUS_SCHEDULED_CONSTRUCTION)で作成する。作成そのものはバックエンドの
 * POST /api/v1/templates/street-grid が行う(地形に沿う成長・街区内の割付・接続と線形の検証)。
 * 作成後は3Dで確認して不要な路地を消し、システムトレイの「一括完成」で竣工させる運用。
 *
 * 操作:
 *   左クリック=頂点追加 / 右クリック=最後の頂点を削除 / Delete・Backspace=最後の頂点を削除 /
 *   空白を左ドラッグ=パン / ホイール=ズーム。
 * クリックとドラッグの区別は、押した位置から離した位置までの画面上の移動量で判定する
 * (CityMapCanvas は onPointerDown が false を返すとパンを始めるため、ここでは常にパンを
 * 許可し、動かずに離した場合だけ頂点として採用する)。
 *
 * 開いた時点の3Dカメラ位置を中心に地図を寄せる(camera:state の初回受信時のみ)。
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

/** バックエンド create_street_grid の戻り値 */
type StreetGridResult = {
  path_ids: number[];
  created: number;
  candidates: number;
  rejected: Record<string, number>;
  axis_angle_deg: number;
  pattern: StreetPattern | "fixed_grid";
  blocks?: number;
};

/** 3Dカメラの状態(camera:state)。位置だけ使う */
type CameraStatePayload = { position?: number[] };

/** 押下位置からこの距離(px)以内で離したらクリック(頂点追加)、それ以上はパンとみなす */
const CLICK_THRESHOLD_PX = 4;

/** カメラ位置を中心に地図を寄せるときの半径(m) */
const CAMERA_HALF_EXTENT_M = 600;

/** 既定の街路間隔(m)。バックエンドの DEFAULT_STREET_GRID_SPACING と同じ値 */
const DEFAULT_SPACING_M = 80;
const SPACING_MIN_M = 20;
const SPACING_MAX_M = 500;

/** 既定の路地バリアント(1車線生活道路)。マスターに無ければ最初の街路を使う */
const DEFAULT_VARIANT = 212;

/** 範囲の描画色 */
const AREA_FILL = "rgba(249, 226, 175, 0.18)";
const AREA_EDGE = "#f9e2af";
const VERTEX_FILL = "#f9e2af";
const VERTEX_EDGE = "#1e1e2e";
const VERTEX_RADIUS_PX = 5;

/** 路地作成中は道路と区画・地区の枠線だけ見えれば十分なので、標高・建物などは消す */
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

  // ポインタハンドラは CityMapCanvas 側で ref ミラーされるため、頂点列とホバー位置は
  // ハンドラを作り直さずに参照できるよう ref にも持つ。
  const verticesRef = useRef<WorldPoint[]>(vertices);
  verticesRef.current = vertices;
  const hoverRef = useRef<WorldPoint | null>(null);
  const pressRef = useRef<{ screen: { x: number; y: number }; world: WorldPoint } | null>(null);
  const lastScreenRef = useRef<{ x: number; y: number } | null>(null);
  const fittedRef = useRef(false);

  const redraw = useCallback(() => setOverlayVersion((v) => v + 1), []);

  /** 選択できる路地バリアント: 街路のうち高架・地下でなく接道できるもの */
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

  /* ---- 3Dカメラ位置へ初回だけ寄せる ---- */
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

  /* ---- 頂点の編集 ---- */
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

  /* ---- ポインタ操作 ---- */
  const handlePointerDown = useCallback((e: CityMapPointerEvent): boolean => {
    pressRef.current = { screen: e.screen, world: e.world };
    lastScreenRef.current = e.screen;
    // false を返してパンは CityMapCanvas に任せる。クリック判定は mouseup で行う
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

  /* ---- 範囲の重ね描き ---- */
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

    // 最後の頂点からカーソルまでの仮の辺
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

  /* ---- 作成 ---- */
  async function createStreetGrid() {
    if (busy || vertices.length < 3 || variant === null) return;
    setBusy(true);
    setMessage(null);
    try {
      const result = await fetchApi<StreetGridResult>(buildApiUrl(apiBaseUrl, "/api/v1/templates/street-grid"), {
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
        .map(([key, n]) => `${t(`templateTools.reject.${key}`, { defaultValue: key })}: ${n}`)
        .join(" / ");
      setMessage(
        (blockMode && result.blocks === 0
          ? t("templateTools.noBlocks")
          : t("templateTools.gridCreated", { created: result.created, rejected: rejectedTotal })) +
          (reasons ? ` (${reasons})` : "")
      );
      setVertices([]);
      // 計画中の路地を3Dへ反映し、地図も新しい道路を拾い直す
      await sendSessionReload();
      mapRef.current?.reload();
    } catch (err) {
      if (err instanceof ApiError) {
        const i18nKey = `errors.${err.code}`;
        setMessage(i18nKey !== "errors.UNKNOWN_ERROR" ? t(i18nKey, { defaultValue: err.message }) : err.message);
      } else {
        setMessage(t("templateTools.gridFailed"));
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
          {vertices.length < 3 ? t("templateTools.hintAddVertices") : t("templateTools.hintReady")}
          <span className="street-grid__count">{t("templateTools.vertexCount", { count: vertices.length })}</span>
        </div>
        <div className="street-grid__controls">
          <label className="street-grid__field">
            <span>{t("templateTools.pattern")}</span>
            <select value={pattern} disabled={busy} onChange={(e) => setPattern(e.target.value as StreetPattern)}>
              <optgroup label={t("templateTools.terrainPatterns")}>
                {STREET_PATTERNS.map((key) => (
                  <option key={key} value={key}>{t(`templateTools.patterns.${key}`)}</option>
                ))}
              </optgroup>
              <optgroup label={t("templateTools.blockPatterns")}>
                {BLOCK_PATTERNS.map((key) => (
                  <option key={key} value={key}>{t(`templateTools.patterns.${key}`)}</option>
                ))}
              </optgroup>
            </select>
          </label>
          <label className="street-grid__field">
            <span>{t(blockMode ? "templateTools.parcelSize" : "templateTools.spacing")}</span>
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
            <span>{t("templateTools.variant")}</span>
            <select value={variant ?? ""} disabled={busy || streetVariants.length === 0} onChange={(e) => setVariant(Number(e.target.value))}>
              {streetVariants.map((v) => (
                <option key={v.id} value={v.id}>
                  {v.name}
                </option>
              ))}
            </select>
          </label>
          {!blockMode && <label className="street-grid__field">
            <span>{t("templateTools.angle")}</span>
            <select value={angleAuto ? "auto" : "manual"} disabled={busy} onChange={(e) => setAngleAuto(e.target.value === "auto")}>
              <option value="auto">{t("templateTools.angleAuto")}</option>
              <option value="manual">{t("templateTools.angleManual")}</option>
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
            {t("templateTools.undoVertex")}
          </button>
          <button type="button" className="street-grid__btn" disabled={busy || vertices.length === 0} onClick={() => setVertices([])}>
            {t("templateTools.clearVertices")}
          </button>
          <button type="button" className="street-grid__btn street-grid__btn--primary" disabled={!canCreate} onClick={() => void createStreetGrid()}>
            {busy ? t("templateTools.creating") : t("templateTools.create")}
          </button>
        </div>
        {blockMode && <div className="street-grid__block-hint">{t("templateTools.blockHint")}</div>}
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
