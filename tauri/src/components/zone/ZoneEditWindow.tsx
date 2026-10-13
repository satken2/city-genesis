/**
 * 地区編集ウィンドウ (window_type="zone_edit")。
 *
 * 都市マップ(CityMapCanvas)の上でブラシを使い、なぞったところの道路の面を地区へ割り当てる。
 * 道路は左右の面ごとに地区を持ち、土地区画は接道のうち区画がある側の面の地区に従うので、
 * ブラシが拾うのは道路の面(zoneBrushTargets.ts)。
 *
 * ツール(塗った面の行き先):
 *   - 新規地区: 拾った面で新しい地区を作る(POST /api/v1/zones/)。続けて塗り足せるよう、
 *     作成後は拡張ツールに切り替える。
 *   - 既存地区の拡張: マウスを重ねた地区を強調し、そこからなぞり始めて拾った面をその地区へ取り込む
 *     (POST /api/v1/paths/batch-zone)。
 * 塗り方(ブラシが拾う面):
 *   - ブロック一括: 生活道路に囲まれたブロックに触れたら、境界の道路のブロック側の面をすべて拾う。
 *   - 道路単位: 道路の片側の半分の帯か、その面に接道する土地区画の集まりに触れたら、その面を拾う。
 * 操作: Ctrl+左ドラッグ=なぞる(離した時点で送信) / 左ドラッグ=パン / ホイール=ズーム。
 *
 * ブラシは画面上で一定の半径なので、ズームすれば塗れる範囲が変わり、大きさの設定は持たない。
 * 都市の矩形の外の道路(都市外地区)は所属を変えられないので、その面は拾わない。
 * 地区の連結性とサイズはコアが検証し(ZONE_CONTIGUITY_ERROR / ZONE_SIZE_ERROR)、ここでは結果を出すだけ。
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
import { PATH_SIDE_LEFT, PATH_SIDE_RIGHT, PATH_STREET } from "../../constants";
import {
  buildBlocks,
  buildFrontages,
  circleTouches,
  faceKey,
  findZoneAt,
  parseFaceKey,
  signedArea,
  type Block,
  type FaceKey,
  type Frontage,
  type Ring,
  type StreetEdge,
} from "./zoneBrushTargets";
import "./ZoneEditWindow.css";

type Tool = "create" | "expand";
type PaintMode = "block" | "road";
type WorldPoint = { x: number; z: number };

type ZoneShape = { zoneId: number; name: string; color: string; polygon: Ring; area: number };

/** 1回のなぞり。zoneId=null は新規地区。半径と塗り方は押した時点で決めて、なぞり終えるまで変えない */
type Stroke = {
  zoneId: number | null;
  color: string;
  radius: number;
  mode: PaintMode;
  points: WorldPoint[];
  /** 触れたブロック・沿道の区画の集まり(強調に使う) */
  picked: Set<Block | Frontage>;
  faces: Set<FaceKey>;
  /** 送信に成功し、地図の取り直しを待っている */
  committed: boolean;
};

/** ブラシの半径(画面上のpx) */
const BRUSH_RADIUS_PX = 20;

const ZONE_FILL_ALPHA = 0.3;
const TARGET_FILL_ALPHA = 0.35;
const STROKE_TRAIL_ALPHA = 0.25;
const PICKED_FILL_ALPHA = 0.5;
const FALLBACK_ZONE_COLOR = "#888888";
const BRUSH_EDGE = "rgba(30, 30, 46, 0.85)";

const TOOL_LABELS: Record<Tool, string> = { create: "zone.edit.toolCreate", expand: "zone.edit.toolExpand" };
const MODE_LABELS: Record<PaintMode, string> = { block: "zone.edit.modeBlock", road: "zone.edit.modeRoad" };

const MAP_LAYERS: Partial<CityMapLayerVisibility> = {
  ...DEFAULT_CITY_MAP_LAYERS,
  zoneBorders: true,
  parcels: true,
};

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

/**
 * 地区の形は外周だけで穴を持たないので、飛び地を囲む地区の形は飛び地も覆う。地区を塗るときは
 * 作業用のキャンバスに不透明で塗り重ねてから半透明で写し、重なりの色が混ざらないようにする。
 */
function paintThroughScratch(
  ctx: CanvasRenderingContext2D,
  scratch: HTMLCanvasElement,
  alpha: number,
  paint: (s: CanvasRenderingContext2D) => void
): void {
  if (scratch.width !== ctx.canvas.width || scratch.height !== ctx.canvas.height) {
    scratch.width = ctx.canvas.width;
    scratch.height = ctx.canvas.height;
  }
  const s = scratch.getContext("2d")!;
  s.setTransform(1, 0, 0, 1, 0, 0);
  s.globalCompositeOperation = "source-over";
  s.clearRect(0, 0, scratch.width, scratch.height);
  s.setTransform(ctx.getTransform());
  paint(s);
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.globalAlpha = alpha;
  ctx.drawImage(scratch, 0, 0);
  ctx.restore();
}

export function ZoneEditWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const mapRef = useRef<CityMapCanvasHandle>(null);

  const [tool, setTool] = useState<Tool>("create");
  const [mode, setMode] = useState<PaintMode>("block");
  const [previewColor, setPreviewColor] = useState(randomZoneColor);
  const [hoverZoneId, setHoverZoneId] = useState<number | null>(null);
  const [zones, setZones] = useState<ZoneShape[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [overlayVersion, setOverlayVersion] = useState(0);

  // ポインタ・描画コールバックは CityMapCanvas 側で ref ミラーされるため、最新値は ref で読む。
  const toolRef = useRef(tool);
  toolRef.current = tool;
  const modeRef = useRef(mode);
  modeRef.current = mode;
  const previewColorRef = useRef(previewColor);
  previewColorRef.current = previewColor;
  const busyRef = useRef(busy);
  busyRef.current = busy;
  const hoverZoneIdRef = useRef(hoverZoneId);
  hoverZoneIdRef.current = hoverZoneId;
  const zonesRef = useRef(zones);
  zonesRef.current = zones;
  const blocksRef = useRef<Block[]>([]);
  const frontagesRef = useRef<Frontage[]>([]);
  /** 面ごとの所属地区(未所属は0以下)。拡張で取り込み先に属している面を送らないために使う */
  const faceZonesRef = useRef(new Map<FaceKey, number>());
  /** 生活道路の中心線の中点。都市の矩形の外の道路を見分けるのに使う */
  const streetMidsRef = useRef(new Map<number, WorldPoint>());
  const strokeRef = useRef<Stroke | null>(null);
  const cursorRef = useRef<WorldPoint | null>(null);
  const zoneScratchRef = useRef<HTMLCanvasElement | null>(null);
  const targetScratchRef = useRef<HTMLCanvasElement | null>(null);
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
    const streets: StreetEdge[] = [];
    const faceZones = new Map<FaceKey, number>();
    const mids = new Map<number, WorldPoint>();
    for (const path of vectors.paths) {
      if (path.path_type !== PATH_STREET) continue;
      const centerline = pathCenterline(path);
      const mid = polylineMidpoint(centerline);
      if (mid) mids.set(path.path_id, mid);
      faceZones.set(faceKey(path.path_id, PATH_SIDE_LEFT), path.left_zone_id ?? 0);
      faceZones.set(faceKey(path.path_id, PATH_SIDE_RIGHT), path.right_zone_id ?? 0);
      streets.push({
        pathId: path.path_id,
        startNodeId: path.start_node_id ?? 0,
        endNodeId: path.end_node_id ?? 0,
        centerline,
        width: path.width ?? 0,
      });
    }
    blocksRef.current = buildBlocks(streets);
    frontagesRef.current = buildFrontages(streets, vectors.parcels);
    faceZonesRef.current = faceZones;
    streetMidsRef.current = mids;
    setZones(
      vectors.zones
        .filter((zone) => (zone.zone_id ?? 0) > 0)
        .map((zone) => ({
          zoneId: zone.zone_id!,
          name: zone.name ?? "",
          color: zone.color || FALLBACK_ZONE_COLOR,
          polygon: zone.polygon ?? [],
          area: Math.abs(signedArea(zone.polygon ?? [])),
        }))
        // 大きい順。塗り重ねたときに飛び地(内側の小さい地区)が上になる
        .sort((a, b) => b.area - a.area)
    );
    if (strokeRef.current?.committed) strokeRef.current = null;
    setOverlayVersion((v) => v + 1);
  }, []);

  /** 道路の面を優先し、輪郭で選ぶ場合は内側の小さい地区を返す */
  const zoneAt = useCallback((p: WorldPoint): number | null => {
    return findZoneAt(p.x, p.z, zonesByIdRef.current, frontagesRef.current, faceZonesRef.current);
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

  const selectMode = useCallback((next: PaintMode) => {
    setMode(next);
    setError(null);
  }, []);

  /* ---- なぞる ---- */

  /** 都市の矩形の内側の道路の面か */
  const isInsideCity = useCallback((face: FaceKey): boolean => {
    const half = cityHalfRef.current;
    const mid = streetMidsRef.current.get(parseFaceKey(face).pathId);
    return !half || !mid || (Math.abs(mid.x) <= half.w && Math.abs(mid.z) <= half.h);
  }, []);

  /** 円の中心 p で触れたブロック・沿道の区画の集まりの面を拾う */
  const stampAt = useCallback(
    (stroke: Stroke, p: WorldPoint) => {
      const targets: (Block | Frontage)[] = stroke.mode === "block" ? blocksRef.current : frontagesRef.current;
      for (const target of targets) {
        if (stroke.picked.has(target)) continue;
        const rings = "ring" in target ? [target.ring] : target.rings;
        if (!circleTouches(p.x, p.z, stroke.radius, rings, target.bounds)) continue;
        stroke.picked.add(target);
        for (const face of "faces" in target ? target.faces : [target.face]) {
          if (isInsideCity(face)) stroke.faces.add(face);
        }
      }
    },
    [isInsideCity]
  );

  /** 直前の点から p までを半径の半分の間隔で拾い、速く動かしても途中を取りこぼさない */
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
        mode: modeRef.current,
        points: [e.world],
        picked: new Set(),
        faces: new Set(),
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
    const faces = [...stroke.faces]
      .filter((face) => stroke.zoneId === null || faceZonesRef.current.get(face) !== stroke.zoneId)
      .map(parseFaceKey);
    if (faces.length === 0) {
      strokeRef.current = null;
      return;
    }
    const assignment = { path_ids: faces.map((face) => face.pathId), sides: faces.map((face) => face.side) };
    setBusy(true);
    try {
      if (stroke.zoneId === null) {
        await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/zones/"), {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ ...assignment, color: stroke.color }),
        });
        setTool("expand");
      } else {
        await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/paths/batch-zone"), {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ ...assignment, zone_id: stroke.zoneId }),
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
    zoneScratchRef.current ??= document.createElement("canvas");
    paintThroughScratch(ctx, zoneScratchRef.current, ZONE_FILL_ALPHA, (s) => {
      for (const zone of zonesRef.current) {
        fillRing(s, zone.polygon);
        s.fillStyle = zone.color;
        s.fill();
      }
    });
  }, []);

  const drawLive = useCallback((ctx: CanvasRenderingContext2D, view: CityMapViewState) => {
    const px = (v: number) => v / view.zoom;
    const stroke = strokeRef.current;
    const targetZoneId = stroke ? stroke.zoneId : toolRef.current === "expand" ? hoverZoneIdRef.current : null;
    const target = targetZoneId !== null ? zonesByIdRef.current.get(targetZoneId) : undefined;
    if (target) {
      targetScratchRef.current ??= document.createElement("canvas");
      paintThroughScratch(ctx, targetScratchRef.current, TARGET_FILL_ALPHA, (s) => {
        fillRing(s, target.polygon);
        s.fillStyle = target.color;
        s.fill();
        // 外周の内側にある飛び地は強調しない
        s.globalCompositeOperation = "destination-out";
        for (const zone of zonesRef.current) {
          if (zone.area >= target.area) continue;
          fillRing(s, zone.polygon);
          s.fill();
        }
      });
    }
    ctx.save();
    if (target) {
      fillRing(ctx, target.polygon);
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
      ctx.globalAlpha = PICKED_FILL_ALPHA;
      for (const picked of stroke.picked) {
        for (const ring of "ring" in picked ? [picked.ring] : picked.rings) {
          fillRing(ctx, ring);
          ctx.fill();
        }
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
              {t(TOOL_LABELS[key])}
            </button>
          ))}
        </div>
        <div className="zone-edit__tools" role="group">
          {(["block", "road"] as const).map((key) => (
            <button
              key={key}
              type="button"
              className={`zone-edit__tool${mode === key ? " zone-edit__tool--active" : ""}`}
              aria-pressed={mode === key}
              data-guide="zone_edit.mode"
              data-guide-value={key}
              onClick={() => selectMode(key)}
            >
              {t(MODE_LABELS[key])}
            </button>
          ))}
        </div>
        <div className="zone-edit__target">
          <span className="zone-edit__chip" style={{ background: target?.color ?? "transparent" }} />
          <span>{target?.name ?? t("zone.edit.noTarget")}</span>
        </div>
        <div className="zone-edit__hint">{t(`zone.edit.hint.${tool}.${mode}`)}</div>
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
