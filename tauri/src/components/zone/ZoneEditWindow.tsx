/**
 * District editor (window_type="zone_edit")
 *
 * Brush road sides on CityMapCanvas to assign them to a district. Roads have separate districts per side;
 * parcels follow the district of the frontage road's parcel-facing side, so targets are road sides
 * (zoneBrushTargets.ts).
 *
 * Tools (destination for brushed sides):
 * - New district: create from picked sides (POST /api/v1/zones/), then switch to Extend so painting can
 *   continue.
 * - Extend existing district: highlight the hovered district and show its name and color on this tool's button,
 *   then start brushing there to add picked sides to it (POST /api/v1/paths/batch-zone).
 * Brush modes:
 * - Whole block: touching a block enclosed by local roads picks every block-facing boundary side.
 * - By road: pick one road section's side, also selectable through frontage parcels. If exactly one road lies
 *   between directly brushed sides at both ends, include its side too.
 * Defaults: Extend Existing District and By Road.
 * Controls: Ctrl + left drag brushes and submits on release; left drag pans; wheel zooms. Put control guidance
 * in the hint bar to preserve map space.
 *
 * Brush radius is fixed in screen pixels, so zoom changes its world coverage; there is no size setting.
 * Exclude road sides outside the city's rectangle, whose outside-city district membership cannot change.
 * The core validates connectivity and size (ZONE_CONTIGUITY_ERROR / ZONE_SIZE_ERROR); this panel only displays
 * results. On success, refresh map vectors and 3D through session:reload.
 *
 * District fills change only with data and belong in overlayDraw. Use animatedOverlayDraw for the moving brush,
 * highlights, and stroke trail without redrawing the whole map.
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
import { describeError, buildApiUrl, fetchApi } from "../../utils/api";
import { clearHintbarTextIf, setHintbarText } from "../../utils/hintbar";
import { sendSessionReload } from "../../utils/ipc";
import { PATH_SIDE_LEFT, PATH_SIDE_RIGHT, PATH_STREET } from "../../constants";
import {
  buildBlocks,
  buildFrontages,
  buildRoadTargets,
  circleTouches,
  faceKey,
  findZoneAt,
  findSingleRoadGaps,
  parseFaceKey,
  signedArea,
  type Block,
  type FaceKey,
  type Frontage,
  type Ring,
  type RoadTarget,
  type StreetEdge,
} from "./zoneBrushTargets";
import "./ZoneEditWindow.css";

type Tool = "create" | "expand";
type PaintMode = "block" | "road";
type WorldPoint = { x: number; z: number };

type ZoneShape = { zoneId: number; name: string; color: string; polygon: Ring; area: number };

/** One brush stroke. zoneId=null means a new district. Fix radius/mode at press time until the stroke ends. */
type Stroke = {
  zoneId: number | null;
  color: string;
  radius: number;
  mode: PaintMode;
  points: WorldPoint[];
  touched: Set<Block | RoadTarget>;
  /** Directly touched sides and sides including filled intervening roads, shared by display and submission. */
  picked: Set<Block | RoadTarget>;
  faces: Set<FaceKey>;
  /** Submission succeeded; waiting for the map refresh. */
  committed: boolean;
};

/** Brush radius in screen pixels */
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

/** Point halfway along a polyline's length */
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

/** New district color. Vary hue only, with saturation 0.65 and value 0.90, matching backend crud_zones._generate_zone_color. */
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
  // For districts without outlines, do not reuse the previous district's drawing path.
  ctx.beginPath();
  if (ring.length < 3) return;
  ctx.moveTo(ring[0][0], ring[0][1]);
  for (let i = 1; i < ring.length; i++) ctx.lineTo(ring[i][0], ring[i][1]);
  ctx.closePath();
}

/**
 * District geometry has only an exterior ring, without holes, so a district surrounding an enclave also covers
 * it.
 * Paint opaque fills on a working canvas, then composite translucently to prevent overlapping colors from
 * mixing.
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

  const [tool, setTool] = useState<Tool>("expand");
  const [mode, setMode] = useState<PaintMode>("road");
  const [previewColor, setPreviewColor] = useState(randomZoneColor);
  const [hoverZoneId, setHoverZoneId] = useState<number | null>(null);
  const [zones, setZones] = useState<ZoneShape[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [overlayVersion, setOverlayVersion] = useState(0);

  useEffect(() => {
    const hint = t(`zone.edit.hint.${tool}.${mode}`);
    void setHintbarText(hint).catch(console.error);
    return () => {
      void clearHintbarTextIf(hint).catch(console.error);
    };
  }, [t, tool, mode]);

  // CityMapCanvas mirrors pointer/drawing callbacks through refs; read latest values from refs.
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
  const roadTargetsRef = useRef<RoadTarget[]>([]);
  /** District membership per road side (<= 0 means unassigned). Avoid sending sides already belonging to the extension target. */
  const faceZonesRef = useRef(new Map<FaceKey, number>());
  /** Local road centerline midpoint, used to detect roads outside the city's rectangle. */
  const streetMidsRef = useRef(new Map<number, WorldPoint>());
  const strokeRef = useRef<Stroke | null>(null);
  const cursorRef = useRef<WorldPoint | null>(null);
  const zoneScratchRef = useRef<HTMLCanvasElement | null>(null);
  const targetScratchRef = useRef<HTMLCanvasElement | null>(null);
  /** City rectangle half-width/half-height in meters. null until terrain settings arrive; do not filter by extent meanwhile. */
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
    roadTargetsRef.current = buildRoadTargets(streets, frontagesRef.current);
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
        // Largest first, so enclaves (smaller inner districts) are painted above surrounding districts.
        .sort((a, b) => b.area - a.area)
    );
    if (strokeRef.current?.committed) strokeRef.current = null;
    setOverlayVersion((v) => v + 1);
  }, []);

  /** Prefer road sides; when selecting by outline, return the smaller inner district. */
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
    // Generate a new color whenever New District is selected again.
    if (next === "create") setPreviewColor(randomZoneColor());
  }, []);

  const selectMode = useCallback((next: PaintMode) => {
    setMode(next);
    setError(null);
  }, []);

  /* ---- Brushing ---- */

  /** Whether a road side lies inside the city's rectangle */
  const isInsideCity = useCallback((face: FaceKey): boolean => {
    const half = cityHalfRef.current;
    const mid = streetMidsRef.current.get(parseFaceKey(face).pathId);
    return !half || !mid || (Math.abs(mid.x) <= half.w && Math.abs(mid.z) <= half.h);
  }, []);

  /** Pick sides from blocks/roadside parcel groups touched by the circle centered at p. */
  const stampAt = useCallback(
    (stroke: Stroke, p: WorldPoint) => {
      const targets = stroke.mode === "block" ? blocksRef.current : roadTargetsRef.current;
      const previousCount = stroke.touched.size;
      for (const target of targets) {
        if (stroke.touched.has(target)) continue;
        const rings = "ring" in target ? [target.ring] : target.rings;
        if (!circleTouches(p.x, p.z, stroke.radius, rings, target.bounds)) continue;
        stroke.touched.add(target);
      }
      if (stroke.touched.size === previousCount) return;
      stroke.picked = new Set(stroke.touched);
      if (stroke.mode === "road") {
        for (const target of findSingleRoadGaps(roadTargetsRef.current, stroke.touched)) stroke.picked.add(target);
      }
      stroke.faces.clear();
      for (const target of stroke.picked) {
        for (const face of target.faces) {
          if (isInsideCity(face)) stroke.faces.add(face);
        }
      }
    },
    [isInsideCity]
  );

  /** Sample from the previous point to p at half-radius intervals, avoiding gaps during fast movement. */
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
        touched: new Set(),
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
    (err: unknown): string => describeError(err, t),
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
      // Retain the stroke result until map refresh, when handleVectorsLoaded clears it.
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

  /* ---- Drawing ---- */

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
        // Do not highlight enclaves inside the outer boundary.
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

  const hoverZone = hoverZoneId !== null ? zonesById.get(hoverZoneId) : undefined;

  return (
    <div className="zone-edit">
      <div className="zone-edit__toolbar">
        <div className="zone-edit__group">
          <span className="zone-edit__heading">{t("zone.edit.modeHeading")}</span>
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
        </div>
        <div className="zone-edit__group">
          <span className="zone-edit__heading">{t("zone.edit.toolHeading")}</span>
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
                {key === "expand" && hoverZone ? (
                  <>
                    <span className="zone-edit__chip" style={{ background: hoverZone.color }} />
                    {t("zone.edit.toolExpandZone", { name: hoverZone.name })}
                  </>
                ) : (
                  t(TOOL_LABELS[key])
                )}
              </button>
            ))}
          </div>
        </div>
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
