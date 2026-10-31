import { describeError } from "../../utils/errors";
/**
 * Building appearance editing
 *
 * Dedicated panel in the window_type="building_appearance" popup. Edits appearance variants
 * (visual_variant1/2/3) and, for mashup buildings (MODEL_MASHUP), sub-building placement
 * (e.g. school buildings and gyms). The economic simulation does not read either value,
 * so edits are allowed at any building status. Floor counts cannot be edited here: the backend
 * derives them on delivery by clamping parent floors to the sub-building definition's floor range.
 *
 * Layout:
 * - Top: appearance variant sliders.
 * - Mashup buildings only: available sub-building cards (footprint rectangles at the site canvas
 *   scale), and a canvas of the site (parcel polygon) and placed sub-buildings.
 *
 * Sub-building controls:
 * - Drag a card onto the site to place a new sub-building.
 * - Drag a placed sub-building to move it.
 * - Shift-drag to rotate around its center toward the cursor.
 * - Click to select; Delete or the delete button removes it.
 * - Overlaps with existing sub-buildings are allowed (the backend does not validate them either).
 *
 * Apply:
 * [Apply] sends appearance variants via PATCH /agents/{id} and, for mashup buildings,
 * placements via PUT /buildings/{id}/sub-buildings. session:reload asks Unity to redraw.
 * Keep the window open so players can experiment while viewing the 3D result.
 *
 * Coordinates:
 * position is relative XZ from the building center (parcel center_point); rotation is degrees
 * counterclockwise from +X in the XZ plane. The canvas draws +Z upward on screen.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { SubBuildingMasterEntry } from "../../types";
import { fetchApi } from "../../utils/api";
import { sendSessionReload } from "../../utils/ipc";
import { showNotification } from "../../utils/notification";
import { useMasterData } from "../../contexts/MasterDataContext";
import { MASHUP_CELL_SIZE, MODEL_MASHUP } from "../../constants";
import "./BuildingAppearancePanel.css";

/**
 * Placement entry for one sub-building (sub_buildings in GET /buildings/{id} and the format of
 * PUT /buildings/{id}/sub-buildings). Floors are not editable; the backend derives them from
 * parent floors on delivery.
 */
type SubBuildingPlacement = {
  subbuilding_id: number;
  /** Relative XZ coordinates [x, z] from the building center. */
  position: number[];
  /** Rotation in the XZ plane (degrees, counterclockwise from +X). */
  rotation: number;
};

/** Canvas drag state. */
type DragState =
  | { kind: "move"; index: number; startWorld: [number, number]; origPosition: [number, number] }
  | { kind: "rotate"; index: number; startAngle: number; origRotation: number }
  /** New placement drag from a card. world is non-null only while the cursor is over the site canvas. */
  | { kind: "new"; subbuildingId: number; world: [number, number] | null };

/** Conversion between world coordinates (XZ) and canvas coordinates. */
type ViewTransform = {
  /** Pixels per meter. */
  scale: number;
  toScreen: (x: number, z: number) => [number, number];
  toWorld: (px: number, py: number) => [number, number];
};

/**
 * Appearance variant slider. Its 0-1000 scale matches Unity's appearance selection number
 * (floor of value × 1000). visual_variant selects threshold folders in steps of 100,
 * so allow only those stops.
 */
const APPEARANCE_SLIDERS = [
  { key: "visual_variant", step: 100 },
  { key: "visual_variant2", step: 1 },
  { key: "visual_variant3", step: 1 },
] as const;

type AppearanceKey = (typeof APPEARANCE_SLIDERS)[number]["key"];

/** Fields from GET /buildings/{id} used by this panel. */
type BuildingResponse = {
  name: string;
  variant: number;
  parcel_id: number;
  sub_buildings: SubBuildingPlacement[] | null;
} & Record<AppearanceKey, number>;

type BuildingAppearancePanelProps = {
  apiBaseUrl: string;
  initialEntityId: string;
  onClose: () => void;
};

/** Canvas inner padding (px). */
const CANVAS_PADDING = 24;

/** Sub-building type color palette (selected deterministically from subbuilding_id). */
const SUBBUILDING_COLORS = [
  "#60a5fa", "#f472b6", "#4ade80", "#fbbf24",
  "#a78bfa", "#f87171", "#22d3ee", "#fb923c",
];

function colorForSubbuilding(subbuildingId: number): string {
  const index = Math.abs(Math.trunc(subbuildingId)) % SUBBUILDING_COLORS.length;
  return SUBBUILDING_COLORS[index];
}

/**
 * Sub-building footprint dimensions (m). Master width / height use cells (1 unit = MASHUP_CELL_SIZE m).
 * Treat undefined dimensions as one cell, following the backend convention.
 */
function subbuildingSize(entry: SubBuildingMasterEntry | undefined): [number, number] {
  return [(entry?.width ?? 1) * MASHUP_CELL_SIZE, (entry?.height ?? 1) * MASHUP_CELL_SIZE];
}

/**
 * Return the four world-space corners of a placement entry's footprint rectangle.
 * The rotation axes u (width) = (cosθ, sinθ), v (depth) = (-sinθ, cosθ)
 * follow the backend cell grid convention (generate_mashup_cells).
 */
function placementCorners(
  placement: SubBuildingPlacement,
  centerPoint: number[],
  entry: SubBuildingMasterEntry | undefined,
): [number, number][] {
  const [width, height] = subbuildingSize(entry);
  const rad = (placement.rotation * Math.PI) / 180;
  const ux = Math.cos(rad);
  const uz = Math.sin(rad);
  const vx = -uz;
  const vz = ux;
  const cx = centerPoint[0] + placement.position[0];
  const cz = centerPoint[1] + placement.position[1];
  const hw = width / 2;
  const hh = height / 2;
  return [
    [cx - ux * hw - vx * hh, cz - uz * hw - vz * hh],
    [cx + ux * hw - vx * hh, cz + uz * hw - vz * hh],
    [cx + ux * hw + vx * hh, cz + uz * hw + vz * hh],
    [cx - ux * hw + vx * hh, cz - uz * hw + vz * hh],
  ];
}

/** Whether a world point lies inside a placement footprint rectangle (test local coordinates after accounting for rotation). */
function hitTestPlacement(
  worldX: number,
  worldZ: number,
  placement: SubBuildingPlacement,
  centerPoint: number[],
  entry: SubBuildingMasterEntry | undefined,
): boolean {
  const [width, height] = subbuildingSize(entry);
  const rad = (placement.rotation * Math.PI) / 180;
  const ux = Math.cos(rad);
  const uz = Math.sin(rad);
  const dx = worldX - (centerPoint[0] + placement.position[0]);
  const dz = worldZ - (centerPoint[1] + placement.position[1]);
  const localU = dx * ux + dz * uz;
  const localV = dx * -uz + dz * ux;
  return Math.abs(localU) <= width / 2 && Math.abs(localV) <= height / 2;
}

export function BuildingAppearancePanel({ apiBaseUrl, initialEntityId, onClose }: BuildingAppearancePanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const agentId = useMemo(() => parseInt(initialEntityId, 10), [initialEntityId]);
  const baseUrl = apiBaseUrl.replace(/\/+$/, "");

  const [variant, setVariant] = useState<number | null>(null);
  const [agentName, setAgentName] = useState<string>("");
  const [appearance, setAppearance] = useState<Record<AppearanceKey, number> | null>(null);
  const [parcelPolygon, setParcelPolygon] = useState<number[][] | null>(null);
  const [centerPoint, setCenterPoint] = useState<number[] | null>(null);
  const [facadeEdgeIndex, setFacadeEdgeIndex] = useState<number[]>([]);
  const [placements, setPlacements] = useState<SubBuildingPlacement[]>([]);
  const [selectedIndex, setSelectedIndex] = useState<number | null>(null);
  const [drag, setDrag] = useState<DragState | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [isSaving, setIsSaving] = useState(false);

  const resizeObserverRef = useRef<ResizeObserver | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const [canvasSize, setCanvasSize] = useState<{ width: number; height: number }>({ width: 0, height: 0 });

  /* ---- Data fetching ---- */

  /**
 * Fetch the building (appearance variants, placements, parcel_id), then the parcel (polygon / center_point).
 * Copy fetched values into local editing state; send nothing to the backend until Apply.
 */
  useEffect(() => {
    if (!baseUrl || !Number.isFinite(agentId)) return;
    const controller = new AbortController();

    (async () => {
      try {
        const building = await fetchApi<BuildingResponse>(
          `${baseUrl}/api/v1/buildings/${agentId}`,
          { signal: controller.signal },
        );
        const parcelRes = await fetchApi<{ polygon: number[][]; center_point: number[]; facade_edge_index: number[] | number }>(
          `${baseUrl}/api/v1/parcels/${building.parcel_id}`,
          { signal: controller.signal },
        );
        if (controller.signal.aborted) return;

        setVariant(building.variant);
        setAgentName(building.name);
        setAppearance(
          Object.fromEntries(APPEARANCE_SLIDERS.map(({ key }) => [key, building[key]])) as Record<AppearanceKey, number>,
        );
        setParcelPolygon(parcelRes.polygon);
        setCenterPoint(parcelRes.center_point);
        const facade = parcelRes.facade_edge_index;
        setFacadeEdgeIndex(Array.isArray(facade) ? facade : facade != null ? [facade] : []);
        // Delivered entries include resolved fields such as model_prefix; retain only the three editable fields.
        setPlacements(
          (building.sub_buildings ?? []).map((p) => ({
            subbuilding_id: p.subbuilding_id,
            position: [p.position[0], p.position[1]],
            rotation: p.rotation ?? 0,
          })),
        );
      } catch (err) {
        if (!controller.signal.aborted) {
          console.error("[BuildingAppearance] Failed to load building data:", err);
          setLoadError(describeError(err));
        }
      }
    })();

    return () => controller.abort();
  }, [baseUrl, agentId]);

  /* ---- Derived from master data ---- */

  const isMashup = variant !== null && masterData?.agents[variant]?.model_type === MODEL_MASHUP;

  /** Sub-building master entries belonging to this variant (for cards). */
  const subbuildingEntries = useMemo(() => {
    if (!masterData || variant === null) return [];
    return Object.entries(masterData.subbuildings)
      .filter(([, entry]) => entry.agent_variant === variant)
      .map(([idStr, entry]) => [parseInt(idStr, 10), entry] as const);
  }, [masterData, variant]);

  /** Lookup from subbuilding_id to master entry (for rendering and hit testing). */
  const subbuildingById = useMemo(() => {
    const map = new Map<number, SubBuildingMasterEntry>();
    for (const [id, entry] of subbuildingEntries) map.set(id, entry);
    return map;
  }, [subbuildingEntries]);

  /* ---- Tracking canvas size ---- */

  /**
 * Observe the site canvas container size. It does not exist until master/building data are ready
 * because a loading display replaces it. Start observation with a callback ref rather than a
 * mount-time useEffect: attach when the element is created, detach when it is removed.
 */
  const setCanvasContainerRef = useCallback((container: HTMLDivElement | null) => {
    resizeObserverRef.current?.disconnect();
    if (!container) {
      resizeObserverRef.current = null;
      return;
    }
    const observer = new ResizeObserver((entries) => {
      const rect = entries[0]?.contentRect;
      if (rect) setCanvasSize({ width: Math.floor(rect.width), height: Math.floor(rect.height) });
    });
    observer.observe(container);
    resizeObserverRef.current = observer;
  }, []);

  /* ---- View transform (fit the whole site into the canvas) ---- */

  const view = useMemo((): ViewTransform | null => {
    if (!parcelPolygon || parcelPolygon.length === 0) return null;
    if (canvasSize.width <= CANVAS_PADDING * 2 || canvasSize.height <= CANVAS_PADDING * 2) return null;

    let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;
    for (const [x, z] of parcelPolygon) {
      minX = Math.min(minX, x); maxX = Math.max(maxX, x);
      minZ = Math.min(minZ, z); maxZ = Math.max(maxZ, z);
    }
    const worldW = Math.max(maxX - minX, 1e-6);
    const worldH = Math.max(maxZ - minZ, 1e-6);
    const scale = Math.min(
      (canvasSize.width - CANVAS_PADDING * 2) / worldW,
      (canvasSize.height - CANVAS_PADDING * 2) / worldH,
    );
    // Center the site on the canvas. Draw +Z upward (opposite screen Y).
    const offsetX = (canvasSize.width - worldW * scale) / 2;
    const offsetY = (canvasSize.height - worldH * scale) / 2;
    return {
      scale,
      toScreen: (x, z) => [offsetX + (x - minX) * scale, offsetY + (maxZ - z) * scale],
      toWorld: (px, py) => [minX + (px - offsetX) / scale, maxZ - (py - offsetY) / scale],
    };
  }, [parcelPolygon, canvasSize]);

  /* ---- Drawing ---- */

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || !view || !parcelPolygon || !centerPoint) return;
    const ctx = canvas.getContext("2d");
    if (!ctx) return;

    const dpr = window.devicePixelRatio || 1;
    canvas.width = canvasSize.width * dpr;
    canvas.height = canvasSize.height * dpr;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, canvasSize.width, canvasSize.height);

    // Site polygon.
    ctx.beginPath();
    parcelPolygon.forEach(([x, z], i) => {
      const [px, py] = view.toScreen(x, z);
      if (i === 0) ctx.moveTo(px, py);
      else ctx.lineTo(px, py);
    });
    ctx.closePath();
    ctx.fillStyle = "rgba(148, 163, 184, 0.12)";
    ctx.fill();
    ctx.strokeStyle = "rgba(148, 163, 184, 0.7)";
    ctx.lineWidth = 1.5;
    ctx.stroke();

    // Highlight the road-facing edge (front), indicating which side to place primary sub-buildings toward.
    const vertexCount = parcelPolygon.length;
    for (const edgeIndex of facadeEdgeIndex) {
      if (edgeIndex < 0 || edgeIndex >= vertexCount) continue;
      const [x1, z1] = parcelPolygon[edgeIndex];
      const [x2, z2] = parcelPolygon[(edgeIndex + 1) % vertexCount];
      const [px1, py1] = view.toScreen(x1, z1);
      const [px2, py2] = view.toScreen(x2, z2);
      ctx.beginPath();
      ctx.moveTo(px1, py1);
      ctx.lineTo(px2, py2);
      ctx.strokeStyle = "rgba(255, 159, 26, 0.9)";
      ctx.lineWidth = 3;
      ctx.stroke();
    }

    // Placed sub-buildings.
    const drawPlacement = (placement: SubBuildingPlacement, options: { selected?: boolean; ghost?: boolean }) => {
      const entry = subbuildingById.get(placement.subbuilding_id);
      const corners = placementCorners(placement, centerPoint, entry);
      const color = colorForSubbuilding(placement.subbuilding_id);

      ctx.save();
      if (options.ghost) ctx.globalAlpha = 0.55;
      ctx.beginPath();
      corners.forEach(([x, z], i) => {
        const [px, py] = view.toScreen(x, z);
        if (i === 0) ctx.moveTo(px, py);
        else ctx.lineTo(px, py);
      });
      ctx.closePath();
      ctx.fillStyle = `${color}55`;
      ctx.fill();
      ctx.strokeStyle = color;
      ctx.lineWidth = options.selected ? 3 : 1.5;
      ctx.stroke();

      // Sub-building name labels (horizontal at the center; omit for small rectangles).
      const [labelX, labelY] = view.toScreen(
        centerPoint[0] + placement.position[0],
        centerPoint[1] + placement.position[1],
      );
      const [width, height] = subbuildingSize(entry);
      const minSidePx = Math.min(width, height) * view.scale;
      if (minSidePx >= 24) {
        ctx.globalAlpha = options.ghost ? 0.7 : 1;
        ctx.fillStyle = "#e5e7eb";
        ctx.font = "11px sans-serif";
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        ctx.fillText(entry?.display_name ?? String(placement.subbuilding_id), labelX, labelY);
      }
      ctx.restore();
    };

    placements.forEach((placement, index) => {
      drawPlacement(placement, { selected: index === selectedIndex });
    });

    // Ghost while dragging a new placement.
    if (drag?.kind === "new" && drag.world) {
      drawPlacement(
        {
          subbuilding_id: drag.subbuildingId,
          position: [drag.world[0] - centerPoint[0], drag.world[1] - centerPoint[1]],
          rotation: 0,
        },
        { ghost: true },
      );
    }
  }, [view, parcelPolygon, centerPoint, facadeEdgeIndex, placements, selectedIndex, drag, subbuildingById, canvasSize]);

  /* ---- Canvas controls (selection, movement, rotation) ---- */

  /** Extract canvas-local coordinates from a pointer event and convert to world coordinates. */
  const eventToWorld = useCallback(
    (clientX: number, clientY: number): [number, number] | null => {
      const canvas = canvasRef.current;
      if (!canvas || !view) return null;
      const rect = canvas.getBoundingClientRect();
      const px = clientX - rect.left;
      const py = clientY - rect.top;
      if (px < 0 || py < 0 || px > rect.width || py > rect.height) return null;
      return view.toWorld(px, py);
    },
    [view],
  );

  const handleCanvasPointerDown = useCallback(
    (e: React.PointerEvent<HTMLCanvasElement>) => {
      if (e.button !== 0 || !centerPoint) return;
      const world = eventToWorld(e.clientX, e.clientY);
      if (!world) return;
      const [wx, wz] = world;

      // Prioritize hit testing items drawn on top (later in the array).
      let hitIndex: number | null = null;
      for (let i = placements.length - 1; i >= 0; i--) {
        if (hitTestPlacement(wx, wz, placements[i], centerPoint, subbuildingById.get(placements[i].subbuilding_id))) {
          hitIndex = i;
          break;
        }
      }

      if (hitIndex === null) {
        setSelectedIndex(null);
        return;
      }

      setSelectedIndex(hitIndex);
      e.currentTarget.setPointerCapture(e.pointerId);
      const placement = placements[hitIndex];
      if (e.shiftKey) {
        // Rotation: add the change in center-to-cursor angle to the initial rotation.
        const cx = centerPoint[0] + placement.position[0];
        const cz = centerPoint[1] + placement.position[1];
        setDrag({
          kind: "rotate",
          index: hitIndex,
          startAngle: Math.atan2(wz - cz, wx - cx),
          origRotation: placement.rotation,
        });
      } else {
        setDrag({
          kind: "move",
          index: hitIndex,
          startWorld: [wx, wz],
          origPosition: [placement.position[0], placement.position[1]],
        });
      }
    },
    [centerPoint, eventToWorld, placements, subbuildingById],
  );

  const handleCanvasPointerMove = useCallback(
    (e: React.PointerEvent<HTMLCanvasElement>) => {
      if (!drag || drag.kind === "new" || !centerPoint) return;
      const canvas = canvasRef.current;
      if (!canvas || !view) return;
      // Convert without clamping so dragging continues to track outside the canvas.
      const rect = canvas.getBoundingClientRect();
      const [wx, wz] = view.toWorld(e.clientX - rect.left, e.clientY - rect.top);

      if (drag.kind === "move") {
        const dx = wx - drag.startWorld[0];
        const dz = wz - drag.startWorld[1];
        setPlacements((prev) =>
          prev.map((p, i) =>
            i === drag.index
              ? { ...p, position: [drag.origPosition[0] + dx, drag.origPosition[1] + dz] }
              : p,
          ),
        );
      } else {
        const placement = placements[drag.index];
        if (!placement) return;
        const cx = centerPoint[0] + placement.position[0];
        const cz = centerPoint[1] + placement.position[1];
        const angle = Math.atan2(wz - cz, wx - cx);
        const deltaDeg = ((angle - drag.startAngle) * 180) / Math.PI;
        const rotation = Math.round(drag.origRotation + deltaDeg);
        setPlacements((prev) =>
          prev.map((p, i) => (i === drag.index ? { ...p, rotation } : p)),
        );
      }
    },
    [drag, centerPoint, view, placements],
  );

  const handleCanvasPointerUp = useCallback(
    (e: React.PointerEvent<HTMLCanvasElement>) => {
      if (drag && drag.kind !== "new") {
        e.currentTarget.releasePointerCapture(e.pointerId);
        setDrag(null);
      }
    },
    [drag],
  );

  /* ---- Dragging new placements from cards ---- */

  /**
 * Start dragging on card pointerdown; track pointermove/up at window level.
 * Draw a ghost while over the site canvas; add the placement on release over the canvas.
 */
  const startNewDrag = useCallback((subbuildingId: number) => {
    setDrag({ kind: "new", subbuildingId, world: null });
  }, []);

  useEffect(() => {
    if (drag?.kind !== "new") return;

    const onPointerMove = (e: PointerEvent) => {
      const world = eventToWorld(e.clientX, e.clientY);
      setDrag((prev) => (prev?.kind === "new" ? { ...prev, world } : prev));
    };
    const onPointerUp = (e: PointerEvent) => {
      const world = eventToWorld(e.clientX, e.clientY);
      setDrag(null);
      if (world && centerPoint) {
        setPlacements((prev) => [
          ...prev,
          {
            subbuilding_id: drag.subbuildingId,
            position: [world[0] - centerPoint[0], world[1] - centerPoint[1]],
            rotation: 0,
          },
        ]);
        setSelectedIndex(placements.length);
      }
    };

    window.addEventListener("pointermove", onPointerMove);
    window.addEventListener("pointerup", onPointerUp);
    return () => {
      window.removeEventListener("pointermove", onPointerMove);
      window.removeEventListener("pointerup", onPointerUp);
    };
  }, [drag, eventToWorld, centerPoint, placements.length]);

  /* ---- Delete selected sub-building (Delete key / button) ---- */

  const deleteSelected = useCallback(() => {
    if (selectedIndex === null) return;
    setPlacements((prev) => prev.filter((_, i) => i !== selectedIndex));
    setSelectedIndex(null);
  }, [selectedIndex]);

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key !== "Delete" && e.key !== "Backspace") return;
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === "INPUT" || target.tagName === "TEXTAREA")) return;
      deleteSelected();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [deleteSelected]);

  /* ---- Apply ---- */

  /**
 * Save appearance variants and, for mashup buildings, placements; request Unity redraw via session:reload.
 * The backend validates all placement entries, so a failed request leaves existing placements unchanged.
 */
  const handleApply = useCallback(async () => {
    if (isSaving || !appearance) return;
    setIsSaving(true);
    try {
      await fetchApi(`${baseUrl}/api/v1/agents/${agentId}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(appearance),
      });
      if (isMashup) {
        await fetchApi(`${baseUrl}/api/v1/buildings/${agentId}/sub-buildings`, {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            sub_buildings: placements.map((p) => ({
              subbuilding_id: p.subbuilding_id,
              position: p.position,
              rotation: p.rotation,
            })),
          }),
        });
      }
      await sendSessionReload();
    } catch (err) {
      console.error("[BuildingAppearance] Failed to apply appearance:", err);
      const message = describeError(err, t);
      void showNotification({ message, durationMs: 5000 });
    } finally {
      setIsSaving(false);
    }
  }, [isSaving, appearance, baseUrl, agentId, isMashup, placements, t]);

  /* ---- Rendering ---- */

  if (loadError) {
    return (
      <div className="ba-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{loadError}</p>
      </div>
    );
  }

  if (!masterData || !appearance) {
    return <p className="ba-muted">{t("common.loading")}</p>;
  }

  // Draw card footprints at the site canvas scale (temporary scale until the view is established).
  const cardScale = view?.scale ?? 3;

  return (
    <div className="ba-layout">
      <div className="ba-header">
        <span className="ba-header-name">{agentName}</span>
      </div>

      {/* Appearance variants. */}
      <div className="ba-sliders">
        {APPEARANCE_SLIDERS.map(({ key, step }, index) => (
          <label key={key} className="ba-slider-row">
            <span className="ba-slider-label">
              {t("building_appearance.variantLabel", { index: index + 1 })}
            </span>
            <input
              type="range"
              className="ba-slider"
              min={0}
              max={1000}
              step={step}
              value={Math.floor(Math.floor(appearance[key] * 1000) / step) * step}
              onChange={(e) => {
                const value = Number(e.target.value) / 1000;
                setAppearance((prev) => (prev ? { ...prev, [key]: value } : prev));
              }}
            />
          </label>
        ))}
      </div>

      {isMashup && (
        <>
          <span className="ba-hint">{t("building_appearance.layoutHint")}</span>

          {/* Sub-building card list. */}
          {subbuildingEntries.length === 0 ? (
            <div className="ba-muted">{t("building_appearance.noSubbuildings")}</div>
          ) : (
            <div className="ba-cards">
              {subbuildingEntries.map(([id, entry]) => {
                const [width, height] = subbuildingSize(entry);
                return (
                  <div
                    key={id}
                    className="ba-card"
                    onPointerDown={(e) => {
                      e.preventDefault();
                      startNewDrag(id);
                    }}
                  >
                    <div className="ba-card-rect-frame">
                      <div
                        className="ba-card-rect"
                        style={{
                          width: Math.max(width * cardScale, 6),
                          height: Math.max(height * cardScale, 6),
                          borderColor: colorForSubbuilding(id),
                          background: `${colorForSubbuilding(id)}33`,
                        }}
                      />
                    </div>
                    <div className="ba-card-name">{entry.display_name}</div>
                    <div className="ba-card-size">
                      {t("building_appearance.sizeLabel", { width, height })}
                    </div>
                  </div>
                );
              })}
            </div>
          )}

          {/* Site canvas. */}
          <div className="ba-canvas-container" ref={setCanvasContainerRef}>
            <canvas
              ref={canvasRef}
              className="ba-canvas"
              style={{ width: canvasSize.width, height: canvasSize.height }}
              onPointerDown={handleCanvasPointerDown}
              onPointerMove={handleCanvasPointerMove}
              onPointerUp={handleCanvasPointerUp}
            />
          </div>
        </>
      )}

      {/* Action buttons */}
      <div className="ba-actions">
        {isMashup && (
          <button
            className="ba-btn"
            type="button"
            disabled={selectedIndex === null}
            onClick={deleteSelected}
          >
            {t("building_appearance.deleteSelected")}
          </button>
        )}
        <div className="ba-actions-spacer" />
        <button className="ba-btn" type="button" onClick={onClose}>
          {t("common.close")}
        </button>
        <button
          className="ba-btn ba-btn-confirm"
          type="button"
          disabled={isSaving}
          onClick={() => void handleApply()}
        >
          {t("building_appearance.apply")}
        </button>
      </div>
    </div>
  );
}
