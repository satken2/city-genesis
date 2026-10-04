/**
 * 地区編集ウィンドウ (window_type="zone_edit")。
 *
 * 都市マップ(CityMapCanvas)の上でブラシを使い、なぞった街路を地区へ割り当てる。
 * 地区は街路の集合で、区画は接道する街路の地区に従うので、ブラシが拾うのは街路だけ。
 *
 * ツール:
 *   - 新規地区: なぞった街路で新しい地区を作る(POST /api/v1/zones/)。続けて塗り足せるよう、
 *     作成後は拡張ツールに切り替える。
 *   - 既存地区の拡張: マウスを重ねた地区を強調し、そこからなぞり始めた街路をその地区へ取り込む
 *     (POST /api/v1/paths/batch-zone)。
 * 操作: Ctrl+左ドラッグ=なぞる(離した時点で送信) / 左ドラッグ=パン / ホイール=ズーム。
 *
 * ブラシは画面上で一定の半径で、街路の中心線の中点(道のりの半分の位置)を円が覆ったら拾う。
 * 円が街路に触れただけで拾うと、交差点を通るたびにそこから伸びる横道まで拾い、塗った範囲の縁から
 * 1本ずつはみ出すため。ズームすれば塗れる範囲が変わるので、大きさの設定は持たない。
 * 都市の矩形の外の街路(都市外地区)は所属を変えられないので拾わない。
 * 割り当て後の地区の街路が繋がっているかはコアが検証し(ZONE_CONTIGUITY_ERROR)、ここでは結果を出すだけ。
 * 成功したら地図のベクターと3D(session:reload)を取り直す。
 *
 * 地区の色塗りはデータが変わったときだけ描けばよいので overlayDraw に、カーソルに追従する
 * ブラシ・強調・なぞった軌跡は地図全体を描き直さずに済む animatedOverlayDraw に描く。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import {
  CityMapCanvas,
  DEFAULT_CITY_MAP_LAYERS,
  pathCenterline,
  type CityMapCanvasHandle,
  type CityMapLayerVisibility,
  type CityMapPointerEvent,
  type CityMapVectors,
  type CityMapViewState,
} from "../map/CityMapCanvas";
import { ApiError, buildApiUrl, fetchApi } from "../../utils/api";
import { sendSessionReload } from "../../utils/ipc";
import { PATH_STREET } from "../../constants";
import "./ZoneEditWindow.css";

type Tool = "create" | "expand";
type WorldPoint = { x: number; z: number };
type Ring = [number, number][];

type Street = { pathId: number; zoneId: number; polygon: Ring; mid: WorldPoint };
type ZoneShape = { zoneId: number; name: string; color: string; polygon: Ring };

/** 1回のなぞり。zoneId=null は新規地区。半径は押した時点のズームで決めて、なぞり終えるまで変えない */
type Stroke = {
  zoneId: number | null;
  color: string;
  radius: number;
  points: WorldPoint[];
  pathIds: Set<number>;
  /** 送信に成功し、地図の取り直しを待っている */
  committed: boolean;
};

/** ブラシの半径(画面上のpx) */
const BRUSH_RADIUS_PX = 20;

const ZONE_FILL_ALPHA = 0.3;
const TARGET_FILL_ALPHA = 0.35;
const STROKE_TRAIL_ALPHA = 0.25;
const PICKED_STREET_ALPHA = 0.9;
const FALLBACK_ZONE_COLOR = "#888888";
const BRUSH_EDGE = "rgba(30, 30, 46, 0.85)";

const MAP_LAYERS: Partial<CityMapLayerVisibility> = {
  ...DEFAULT_CITY_MAP_LAYERS,
  zoneBorders: true,
  parcels: true,
};

function pointInRing(x: number, z: number, ring: Ring): boolean {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const [xi, zi] = ring[i];
    const [xj, zj] = ring[j];
    if (zi > z !== zj > z && x < ((xj - xi) * (z - zi)) / (zj - zi) + xi) inside = !inside;
  }
  return inside;
}

/** 折れ線の道のりの半分の位置 */
function polylineMidpoint(points: [number, number][]): WorldPoint | null {
  if (points.length === 0) return null;
  let total = 0;
  for (let i = 1; i < points.length; i++) total += Math.hypot(points[i][0] - points[i - 1][0], points[i][1] - points[i - 1][1]);
  let remain = total / 2;
  for (let i = 1; i < points.length; i++) {
    const [ax, az] = points[i - 1];
    const [bx, bz] = points[i];
    const len = Math.hypot(bx - ax, bz - az);
    if (len >= remain && len > 0) return { x: ax + ((bx - ax) * remain) / len, z: az + ((bz - az) * remain) / len };
    remain -= len;
  }
  return { x: points[0][0], z: points[0][1] };
}

/** 新規地区の色。バックエンド(crud_zones._generate_zone_color)と同じく彩度0.65・明度0.90で色相だけを振る */
function randomZoneColor(): string {
  const h = Math.random() * 6;
  const s = 0.65;
  const v = 0.9;
  const f = h - Math.floor(h);
  const p = v * (1 - s);
  const q = v * (1 - s * f);
  const u = v * (1 - s * (1 - f));
  const [r, g, b] = [[v, u, p], [q, v, p], [p, v, u], [p, q, v], [u, p, v], [v, p, q]][Math.floor(h) % 6];
  return `#${[r, g, b].map((c) => Math.round(c * 255).toString(16).padStart(2, "0")).join("")}`;
}

function fillRing(ctx: CanvasRenderingContext2D, ring: Ring): void {
  ctx.beginPath();
  ctx.moveTo(ring[0][0], ring[0][1]);
  for (let i = 1; i < ring.length; i++) ctx.lineTo(ring[i][0], ring[i][1]);
  ctx.closePath();
}

export function ZoneEditWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const mapRef = useRef<CityMapCanvasHandle>(null);

  const [tool, setTool] = useState<Tool>("create");
  const [previewColor, setPreviewColor] = useState(randomZoneColor);
  const [hoverZoneId, setHoverZoneId] = useState<number | null>(null);
  const [zones, setZones] = useState<ZoneShape[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [overlayVersion, setOverlayVersion] = useState(0);

  // ポインタ・描画コールバックは CityMapCanvas 側で ref ミラーされるため、最新値は ref で読む。
  const toolRef = useRef(tool);
  toolRef.current = tool;
  const previewColorRef = useRef(previewColor);
  previewColorRef.current = previewColor;
  const busyRef = useRef(busy);
  busyRef.current = busy;
  const hoverZoneIdRef = useRef(hoverZoneId);
  hoverZoneIdRef.current = hoverZoneId;
  const zonesRef = useRef(zones);
  zonesRef.current = zones;
  const streetsRef = useRef<Street[]>([]);
  const strokeRef = useRef<Stroke | null>(null);
  const cursorRef = useRef<WorldPoint | null>(null);
  /** 都市の矩形の半幅・半高(m)。地形設定の取得前は null で、その間は範囲で絞らない */
  const cityHalfRef = useRef<{ w: number; h: number } | null>(null);

  useEffect(() => {
    void fetchApi<{ map_width: number; map_height: number }>(buildApiUrl(apiBaseUrl, "/api/v1/terrain/settings"))
      .then((settings) => {
        cityHalfRef.current = { w: settings.map_width / 2, h: settings.map_height / 2 };
      })
      .catch(console.error);
  }, [apiBaseUrl]);

  const zonesById = useMemo(() => new Map(zones.map((zone) => [zone.zoneId, zone])), [zones]);
  const zonesByIdRef = useRef(zonesById);
  zonesByIdRef.current = zonesById;

  const handleVectorsLoaded = useCallback((vectors: CityMapVectors) => {
    const streets: Street[] = [];
    for (const path of vectors.paths) {
      const ring = path.polygon;
      if (path.path_type !== PATH_STREET || !ring || ring.length < 3) continue;
      const mid = polylineMidpoint(pathCenterline(path));
      if (mid) streets.push({ pathId: path.path_id, zoneId: path.zone_id ?? 0, polygon: ring, mid });
    }
    streetsRef.current = streets;
    setZones(
      vectors.zones
        .filter((zone) => (zone.zone_id ?? 0) > 0 && (zone.polygon?.length ?? 0) >= 3)
        .map((zone) => ({
          zoneId: zone.zone_id!,
          name: zone.name ?? "",
          color: zone.color || FALLBACK_ZONE_COLOR,
          polygon: zone.polygon!,
        }))
    );
    if (strokeRef.current?.committed) strokeRef.current = null;
    setOverlayVersion((v) => v + 1);
  }, []);

  const zoneAt = useCallback((p: WorldPoint): number | null => {
    for (const zone of zonesRef.current) {
      if (pointInRing(p.x, p.z, zone.polygon)) return zone.zoneId;
    }
    return null;
  }, []);

  const handleCursor = useCallback(
    (pos: WorldPoint | null) => {
      cursorRef.current = pos;
      if (strokeRef.current || toolRef.current !== "expand") return;
      const zoneId = pos ? zoneAt(pos) : null;
      if (zoneId !== hoverZoneIdRef.current) setHoverZoneId(zoneId);
    },
    [zoneAt]
  );

  const selectTool = useCallback((next: Tool) => {
    setTool(next);
    setHoverZoneId(null);
    setError(null);
    // 新規地区を選び直すたびに色を引き直す
    if (next === "create") setPreviewColor(randomZoneColor());
  }, []);

  /* ---- なぞる ---- */

  /** 円の中心 p で中点を覆った街路を拾う */
  const stampAt = useCallback((stroke: Stroke, p: WorldPoint) => {
    const half = cityHalfRef.current;
    for (const { pathId, mid } of streetsRef.current) {
      if (stroke.pathIds.has(pathId) || Math.hypot(mid.x - p.x, mid.z - p.z) > stroke.radius) continue;
      if (half && (Math.abs(mid.x) > half.w || Math.abs(mid.z) > half.h)) continue;
      stroke.pathIds.add(pathId);
    }
  }, []);

  /** 直前の点から p までを半径の半分の間隔で拾い、速く動かしても途中の街路を取りこぼさない */
  const extendStroke = useCallback(
    (stroke: Stroke, p: WorldPoint) => {
      const last = stroke.points[stroke.points.length - 1];
      const step = stroke.radius / 2;
      const steps = Math.max(1, Math.ceil(Math.hypot(p.x - last.x, p.z - last.z) / step));
      for (let i = 1; i <= steps; i++) {
        stampAt(stroke, { x: last.x + ((p.x - last.x) * i) / steps, z: last.z + ((p.z - last.z) * i) / steps });
      }
      stroke.points.push(p);
    },
    [stampAt]
  );

  const handlePointerDown = useCallback(
    (e: CityMapPointerEvent): boolean => {
      if (!e.event.ctrlKey || busyRef.current || (strokeRef.current && !strokeRef.current.committed)) return false;
      let zoneId: number | null = null;
      let color = previewColorRef.current;
      if (toolRef.current === "expand") {
        zoneId = zoneAt(e.world);
        if (zoneId === null) return false;
        color = zonesByIdRef.current.get(zoneId)?.color ?? FALLBACK_ZONE_COLOR;
      }
      const stroke: Stroke = {
        zoneId,
        color,
        radius: BRUSH_RADIUS_PX / e.view.zoom,
        points: [e.world],
        pathIds: new Set(),
        committed: false,
      };
      stampAt(stroke, e.world);
      strokeRef.current = stroke;
      setError(null);
      return true;
    },
    [stampAt, zoneAt]
  );

  const handlePointerMove = useCallback(
    (e: CityMapPointerEvent) => {
      const stroke = strokeRef.current;
      if (stroke && !stroke.committed) extendStroke(stroke, e.world);
    },
    [extendStroke]
  );

  const errorMessage = useCallback(
    (err: unknown): string =>
      err instanceof ApiError
        ? t(`errors.${err.code}`, { defaultValue: t("zone.edit.failed") })
        : t("zone.edit.failed"),
    [t]
  );

  const handlePointerUp = useCallback(async () => {
    const stroke = strokeRef.current;
    if (!stroke || stroke.committed) return;
    const zoneOf = new Map(streetsRef.current.map((street) => [street.pathId, street.zoneId]));
    const pathIds = [...stroke.pathIds].filter((id) => stroke.zoneId === null || zoneOf.get(id) !== stroke.zoneId);
    if (pathIds.length === 0) {
      strokeRef.current = null;
      return;
    }
    setBusy(true);
    try {
      if (stroke.zoneId === null) {
        await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/zones/"), {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ path_ids: pathIds, color: stroke.color }),
        });
        setTool("expand");
      } else {
        await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/paths/batch-zone"), {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ path_ids: pathIds, zone_id: stroke.zoneId }),
        });
      }
      // 地図を取り直すまで、なぞった結果を描いたまま残す(handleVectorsLoaded で消える)
      stroke.committed = true;
      mapRef.current?.reloadVectors();
      void sendSessionReload().catch(console.error);
    } catch (err) {
      strokeRef.current = null;
      setError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  }, [apiBaseUrl, errorMessage]);

  /* ---- 描画 ---- */

  const drawZones = useCallback((ctx: CanvasRenderingContext2D) => {
    ctx.save();
    ctx.globalAlpha = ZONE_FILL_ALPHA;
    for (const zone of zonesRef.current) {
      fillRing(ctx, zone.polygon);
      ctx.fillStyle = zone.color;
      ctx.fill();
    }
    ctx.restore();
  }, []);

  const drawLive = useCallback((ctx: CanvasRenderingContext2D, view: CityMapViewState) => {
    const px = (v: number) => v / view.zoom;
    const stroke = strokeRef.current;
    const targetZoneId = stroke ? stroke.zoneId : toolRef.current === "expand" ? hoverZoneIdRef.current : null;
    const target = targetZoneId !== null ? zonesByIdRef.current.get(targetZoneId) : undefined;
    ctx.save();
    if (target) {
      fillRing(ctx, target.polygon);
      ctx.globalAlpha = TARGET_FILL_ALPHA;
      ctx.fillStyle = target.color;
      ctx.fill();
      ctx.globalAlpha = 1;
      ctx.strokeStyle = target.color;
      ctx.lineWidth = px(3);
      ctx.stroke();
    }
    if (stroke) {
      ctx.globalAlpha = STROKE_TRAIL_ALPHA;
      ctx.strokeStyle = stroke.color;
      ctx.fillStyle = stroke.color;
      ctx.lineCap = "round";
      ctx.lineJoin = "round";
      ctx.lineWidth = stroke.radius * 2;
      ctx.beginPath();
      ctx.moveTo(stroke.points[0].x, stroke.points[0].z);
      for (const p of stroke.points) ctx.lineTo(p.x, p.z);
      ctx.stroke();
      ctx.globalAlpha = PICKED_STREET_ALPHA;
      for (const street of streetsRef.current) {
        if (!stroke.pathIds.has(street.pathId)) continue;
        fillRing(ctx, street.polygon);
        ctx.fill();
      }
    }
    const cursor = cursorRef.current;
    if (cursor) {
      ctx.globalAlpha = 1;
      ctx.beginPath();
      ctx.arc(cursor.x, cursor.z, px(BRUSH_RADIUS_PX), 0, Math.PI * 2);
      ctx.strokeStyle = BRUSH_EDGE;
      ctx.lineWidth = px(1.5);
      ctx.stroke();
    }
    ctx.restore();
  }, []);

  const target = tool === "create" ? { name: t("zone.edit.newZone"), color: previewColor } : hoverZoneId !== null ? zonesById.get(hoverZoneId) : undefined;

  return (
    <div className="zone-edit">
      <div className="zone-edit__toolbar">
        <div className="zone-edit__tools" role="group">
          {(["create", "expand"] as const).map((key) => (
            <button
              key={key}
              type="button"
              className={`zone-edit__tool${tool === key ? " zone-edit__tool--active" : ""}`}
              aria-pressed={tool === key}
              data-guide="zone_edit.tool"
              data-guide-value={key}
              onClick={() => selectTool(key)}
            >
              {t(key === "create" ? "zone.edit.toolCreate" : "zone.edit.toolExpand")}
            </button>
          ))}
        </div>
        <div className="zone-edit__target">
          <span className="zone-edit__chip" style={{ background: target?.color ?? "transparent" }} />
          <span>{target?.name ?? t("zone.edit.noTarget")}</span>
        </div>
        <div className="zone-edit__hint">{t(tool === "create" ? "zone.edit.hintCreate" : "zone.edit.hintExpand")}</div>
      </div>
      <div className="zone-edit__map">
        <CityMapCanvas
          ref={mapRef}
          apiBaseUrl={apiBaseUrl}
          layers={MAP_LAYERS}
          interactive
          showCompass
          overlayDraw={drawZones}
          overlayVersion={overlayVersion}
          animatedOverlayDraw={drawLive}
          onVectorsLoaded={handleVectorsLoaded}
          onCursorWorldPos={handleCursor}
          onPointerDown={handlePointerDown}
          onPointerMove={handlePointerMove}
          onPointerUp={() => void handlePointerUp()}
          cursor="crosshair"
        />
        {error && <div className="zone-edit__message">{error}</div>}
      </div>
    </div>
  );
}
