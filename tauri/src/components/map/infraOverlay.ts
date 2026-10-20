/**
 * Types, goods lists, and rendering for city map infrastructure (CityMapCanvas's infraSelection).
 *
 * One network and one coverage layer can be drawn simultaneously.
 * - Network (infrastructure goods, GOODS_INFRASTRUCTURE): GET /api/v1/stats/infrastructure. Color paths by flow/capacity
 *   utilization (width represents capacity), overlay stripes moving with flow on paths carrying flow, and show production
 *   facilities as circles and consumption facilities as diamonds. Draw disconnected facilities in red.
 * - Supply facilities and coverage: GET /api/v1/buildings/service-coverage/{product_id}. Show facilities supplying the good.
 *   For goods with ranges, lightly fill the union of active facilities' ranges to avoid darkening overlaps, and outline
 *   each facility's range (dashed if inactive). Color buildings by inside/outside coverage (handled in CityMapCanvas as part
 *   of building rendering). Goods without ranges (e.g. firefighting) show facilities only.
 * - Facility workload pressure (sales / production capacity) is shown by one pie chart per facility, below its network
 *   symbol or at its center if absent from the network. This is not utilization (production / production capacity):
 *   facilities producing standby supply at full capacity regardless of demand (fire stations, fixed-output power plants,
 *   etc.) would otherwise always look full, giving colors different meanings for different facility types.
 *
 * Drawing functions expect ctx to have the world-coordinate view transform including Y flip.
 * Scale dimensions that must remain constant on screen by /view.zoom.
 */

import { useEffect, useMemo, useState } from "react";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { GOODS_INFRASTRUCTURE, STATUS_ACTIVE } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import type { EffectRangeRegion } from "../../utils/ipc";
import type { MasterData } from "../../types";

/* Infrastructure networks (GET /api/v1/stats/infrastructure) */

export type InfraProducerItem = {
  agent_id: number;
  name: string;
  center_point: number[] | null;
  /** Terminal node coordinates. Path endpoints are welded here, so prefer these coordinates for drawing. */
  node_point: number[] | null;
  /** Production this epoch (amount produced immediately before delivery). */
  production: number;
  shipped: number;
  /** Utilization this epoch (0–1). */
  utilization: number;
  /** Workload pressure this epoch (shipments / production capacity, 0–1). */
  load: number;
  connected: boolean;
};

export type InfraConsumerItem = {
  agent_id: number;
  name: string;
  center_point: number[] | null;
  /** Terminal node coordinates. Path endpoints are welded here, so prefer these coordinates for drawing. */
  node_point: number[] | null;
  demand: number;
  received: number;
  /** Utilization this epoch (0–1). */
  utilization: number;
  /** Workload pressure this epoch (sales of the facility's own supplied good / production capacity, 0–1). */
  load: number;
  connected: boolean;
};

export type InfraPathItem = {
  path_id: number;
  status: number;
  /** Actual signed flow this epoch. Positive follows the points order (start → end). */
  flow: number;
  capacity: number;
  points: number[][];
};

export type InfraProductStats = {
  product_id: number;
  production_total: number;
  shipped_total: number;
  demand_total: number;
  received_total: number;
  fulfillment_rate: number;
  producers: InfraProducerItem[];
  consumers: InfraConsumerItem[];
  paths: InfraPathItem[];
};

export type InfrastructureStats = {
  epoch: number;
  simulation_days: number;
  products: InfraProductStats[];
};

export const INFRA_COLOR_DISCONNECTED = "#ef4444";
export const INFRA_COLOR_PRODUCER = "#10b981";
export const INFRA_COLOR_CONSUMER = "#38bdf8";
const INFRA_COLOR_PATH_INACTIVE = "#6b7280";
const INFRA_COLOR_FLOW_STRIPE = "rgba(255, 255, 255, 0.85)";

/*
 * Use world units (m) for flow stripes. With screen-pixel units, stripes would retain their screen speed as paths shrink
 * when zooming out and appear disproportionately fast. Only stripe length has a screen-pixel minimum to remain visible.
 */
const STRIPE_DASH_M = 10;
const STRIPE_GAP_M = 14;
const STRIPE_SPEED_MIN_M = 1;
const STRIPE_SPEED_MAX_M = 15;
const STRIPE_MIN_DASH_PX = 4;

/*
 * Path width represents infra_capacity in screen px. Compress with a square root because linear scaling makes
 * high-capacity paths too thick. Represent utilization only by color.
 */
const CAPACITY_REF = 2000;
const CAPACITY_REF_PX = 5;
const WIDTH_MIN_PX = 3;
const WIDTH_MAX_PX = 12;

/* Facility workload pie charts (screen px). For network facilities, leave a gap below the symbol. */
const PIE_RADIUS_PX = 10;
const PIE_GAP_PX = 3;

/** Facility workload pie chart colors: red above 90%, orange above 70%, otherwise green. */
function facilityLoadColor(rate: number): string {
  return rate > 0.9 ? "#ef4444" : rate > 0.7 ? "#f97316" : "#22c55e";
}

/**
 * Draw a workload pie chart centered on (x, z), growing clockwise from the top (100% is a full circle, 50% a semicircle).
 * Use a translucent white background circle so position and full size remain visible at low pressure, with a border for
 * contrast over bright land. ctx includes a Y flip to keep +Z up, so decrease angles to draw clockwise on screen.
 */
function drawLoadPie(ctx: CanvasRenderingContext2D, x: number, z: number, rate: number, zoom: number) {
  const radius = PIE_RADIUS_PX / zoom;
  ctx.beginPath();
  ctx.arc(x, z, radius, 0, Math.PI * 2);
  ctx.fillStyle = "rgba(255, 255, 255, 0.5)";
  ctx.fill();
  ctx.lineWidth = 1.5 / zoom;
  ctx.strokeStyle = "#0b0f19";
  ctx.stroke();

  const ratio = Math.min(1, rate);
  if (!(ratio > 0)) return;
  ctx.beginPath();
  ctx.moveTo(x, z);
  ctx.arc(x, z, radius, Math.PI / 2, Math.PI / 2 - 2 * Math.PI * ratio, true);
  ctx.closePath();
  ctx.fillStyle = facilityLoadColor(rate);
  ctx.fill();
}

/** Color for path utilization (0–1): cyan at low utilization, red near capacity. */
export function utilizationColor(rate: number): string {
  const clamped = Math.max(0, Math.min(1, rate));
  return `hsl(${190 - 190 * clamped}, 85%, 55%)`;
}

type NetworkPathRender = {
  shape: Path2D;
  isActive: boolean;
  absFlow: number;
  rate: number;
  widthPx: number;
  /** Flow direction. Positive follows the points order (start → end). */
  direction: number;
  speedM: number;
};

export type InfraNetworkRender = { product: InfraProductStats; paths: NetworkPathRender[] };

/**
 * Build network drawing data. Normalize stripe speed by |flow| relative to the maximum flow for the good
 * (more flow means faster motion). API capacity is infra_capacity × simulation_days, so divide by days
 * to recover the capacity used to determine width.
 */
export function buildNetworkRender(product: InfraProductStats, simulationDays: number): InfraNetworkRender {
  const maxAbsFlow = product.paths.reduce((max, p) => Math.max(max, Math.abs(p.flow)), 0);
  const paths = product.paths
    .filter((p) => p.points.length >= 2)
    .map((p) => {
      const shape = new Path2D();
      shape.moveTo(p.points[0][0], p.points[0][1]);
      for (let i = 1; i < p.points.length; i++) shape.lineTo(p.points[i][0], p.points[i][1]);
      const absFlow = Math.abs(p.flow);
      const infraCapacity = p.capacity / Math.max(1, simulationDays);
      return {
        shape,
        isActive: p.status === STATUS_ACTIVE,
        absFlow,
        rate: p.capacity > 0 ? Math.min(absFlow / p.capacity, 1) : 0,
        widthPx: infraCapacity > 0
          ? Math.min(WIDTH_MAX_PX, Math.max(WIDTH_MIN_PX, CAPACITY_REF_PX * Math.sqrt(infraCapacity / CAPACITY_REF)))
          : WIDTH_MIN_PX,
        direction: p.flow >= 0 ? 1 : -1,
        speedM: maxAbsFlow > 0
          ? STRIPE_SPEED_MIN_M + (STRIPE_SPEED_MAX_M - STRIPE_SPEED_MIN_M) * (absFlow / maxAbsFlow)
          : 0,
      };
    });
  return { product, paths };
}

/** Draw one network frame. timeMs is the elapsed timestamp for moving stripes (0 freezes motion). */
export function drawNetwork(
  ctx: CanvasRenderingContext2D,
  view: { zoom: number },
  render: InfraNetworkRender,
  timeMs: number
) {
  ctx.lineJoin = "round";
  for (const path of render.paths) {
    if (path.isActive) {
      ctx.strokeStyle = utilizationColor(path.rate);
      ctx.lineWidth = path.widthPx / view.zoom;
      ctx.setLineDash([]);
      ctx.lineCap = "round";
      ctx.globalAlpha = path.absFlow > 0 ? 0.95 : 0.55;
      ctx.stroke(path.shape);

      // Increasing lineDashOffset moves stripes toward the start, so decrease it for start → end flow.
      if (path.absFlow > 0) {
        const dashWorld = Math.max(STRIPE_DASH_M, STRIPE_MIN_DASH_PX / view.zoom);
        const gapWorld = dashWorld * (STRIPE_GAP_M / STRIPE_DASH_M);
        // Wrap by the period to prevent unbounded offsets and stripe jitter during prolonged display.
        const phase = ((timeMs / 1000) * path.speedM) % (dashWorld + gapWorld);
        ctx.strokeStyle = INFRA_COLOR_FLOW_STRIPE;
        ctx.setLineDash([dashWorld, gapWorld]);
        ctx.lineDashOffset = -path.direction * phase;
        ctx.lineCap = "butt";
        ctx.globalAlpha = 0.9;
        ctx.stroke(path.shape);
        ctx.lineDashOffset = 0;
      }
    } else {
      // Paths under construction or planned use gray dashed lines without moving stripes.
      const dashWorld = Math.max(6, STRIPE_MIN_DASH_PX / view.zoom);
      ctx.strokeStyle = INFRA_COLOR_PATH_INACTIVE;
      ctx.lineWidth = (path.widthPx * 0.6) / view.zoom;
      ctx.setLineDash([dashWorld, dashWorld * (4 / 6)]);
      ctx.lineCap = "butt";
      ctx.globalAlpha = 0.7;
      ctx.stroke(path.shape);
    }
  }
  ctx.setLineDash([]);
  ctx.globalAlpha = 1;

  const r = 6 / view.zoom;
  const outline = 1.5 / view.zoom;
  const pieOffset = r + (PIE_GAP_PX + PIE_RADIUS_PX) / view.zoom;
  for (const producer of render.product.producers) {
    const cp = producer.node_point ?? producer.center_point;
    if (!cp || cp.length < 2) continue;
    ctx.beginPath();
    ctx.arc(cp[0], cp[1], r, 0, Math.PI * 2);
    ctx.fillStyle = producer.connected ? INFRA_COLOR_PRODUCER : INFRA_COLOR_DISCONNECTED;
    ctx.fill();
    ctx.lineWidth = outline;
    ctx.strokeStyle = "#0b0f19";
    ctx.stroke();
    drawLoadPie(ctx, cp[0], cp[1] - pieOffset, producer.load, view.zoom);
  }
  for (const consumer of render.product.consumers) {
    const cp = consumer.node_point ?? consumer.center_point;
    if (!cp || cp.length < 2) continue;
    ctx.beginPath();
    ctx.moveTo(cp[0], cp[1] + r);
    ctx.lineTo(cp[0] + r, cp[1]);
    ctx.lineTo(cp[0], cp[1] - r);
    ctx.lineTo(cp[0] - r, cp[1]);
    ctx.closePath();
    ctx.fillStyle = consumer.connected ? INFRA_COLOR_CONSUMER : INFRA_COLOR_DISCONNECTED;
    ctx.fill();
    ctx.lineWidth = outline;
    ctx.strokeStyle = "#0b0f19";
    ctx.stroke();
    drawLoadPie(ctx, cp[0], cp[1] - pieOffset, consumer.load, view.zoom);
  }
}

/* Coverage (GET /api/v1/buildings/service-coverage/{product_id}) */

export type ServiceCoverageSupplier = {
  agent_id: number;
  name: string;
  variant: number;
  status: number;
  /** Utilization this epoch (0–1). */
  utilization: number;
  /** Workload pressure this epoch (sales / production capacity, 0–1). */
  load: number;
  center_point: number[] | null;
  /** Effect range. null for facilities without ranges (e.g. fire stations). */
  region: EffectRangeRegion | null;
};

export type ServiceCoverageData = {
  product_id: number;
  suppliers: ServiceCoverageSupplier[];
  /** Active buildings whose site parcels are inside/outside the effect ranges of active facilities. */
  covered_building_ids: number[];
  uncovered_building_ids: number[];
};

/** Building colors inside/outside coverage. Match Unity's ServiceCoverageOverlay. */
export const COVERAGE_IN_RANGE_COLOR = "#3b82f6";
export const COVERAGE_OUT_OF_RANGE_COLOR = "#ef4444";
export const COVERAGE_OUTLINE_COLOR = "#2563eb";
const COVERAGE_FILL_COLOR = "rgba(59, 130, 246, 0.16)";
const COVERAGE_OUTLINE_PX = 1.5;
const COVERAGE_DASH_PX = 7;
const COVERAGE_GAP_PX = 5;

export type InfraCoverageRender = {
  /** Union of active facility ranges (one nonzero-rule shape, with holes wound in reverse). */
  fill: Path2D;
  activeOutline: Path2D;
  inactiveOutline: Path2D;
  covered: Set<number>;
  uncovered: Set<number>;
  /** Facilities receiving workload pie charts. Excludes facilities also shown in the network, which are drawn there. */
  pies: { x: number; z: number; load: number }[];
};

/** Signed ring area in world XZ. Positive has the same direction as Canvas arc's default. */
function signedArea(ring: number[][]): number {
  let area = 0;
  for (let i = 0; i < ring.length; i++) {
    const [x1, z1] = ring[i];
    const [x2, z2] = ring[(i + 1) % ring.length];
    area += x1 * z2 - x2 * z1;
  }
  return area / 2;
}

/** Add a closed ring in the specified winding (outer = positive, hole = negative). */
function appendRing(path: Path2D, ring: number[][], positive: boolean) {
  if (ring.length < 3) return;
  const points = (signedArea(ring) > 0) === positive ? ring : [...ring].reverse();
  path.moveTo(points[0][0], points[0][1]);
  for (let i = 1; i < points.length; i++) path.lineTo(points[i][0], points[i][1]);
  path.closePath();
}

function appendRegion(path: Path2D, region: EffectRangeRegion | null) {
  if (!region) return;
  if (region.type === "circle") {
    if (region.radius <= 0) return;
    path.moveTo(region.center[0] + region.radius, region.center[1]);
    path.arc(region.center[0], region.center[1], region.radius, 0, Math.PI * 2);
    path.closePath();
    return;
  }
  for (const polygon of region.polygons) {
    appendRing(path, polygon.exterior, true);
    for (const hole of polygon.holes) appendRing(path, hole, false);
  }
}

/**
 * Build coverage drawing data. Fill active facilities only; use solid or dashed outlines according to activity.
 * network is the accompanying network (null if absent); do not place pie charts on facilities shown there.
 */
export function buildCoverageRender(data: ServiceCoverageData, network: InfraProductStats | null = null): InfraCoverageRender {
  const onNetwork = new Set([...(network?.producers ?? []), ...(network?.consumers ?? [])].map((f) => f.agent_id));
  const fill = new Path2D();
  const activeOutline = new Path2D();
  const inactiveOutline = new Path2D();
  for (const supplier of data.suppliers) {
    const active = supplier.status === STATUS_ACTIVE;
    if (active) appendRegion(fill, supplier.region);
    appendRegion(active ? activeOutline : inactiveOutline, supplier.region);
  }
  return {
    fill,
    activeOutline,
    inactiveOutline,
    covered: new Set(data.covered_building_ids),
    uncovered: new Set(data.uncovered_building_ids),
    pies: data.suppliers
      .filter((s) => s.center_point && !onNetwork.has(s.agent_id))
      .map((s) => ({ x: s.center_point![0], z: s.center_point![1], load: s.load })),
  };
}

/** Fill effect ranges before drawing roads/buildings. */
export function drawCoverageFill(ctx: CanvasRenderingContext2D, render: InfraCoverageRender) {
  ctx.fillStyle = COVERAGE_FILL_COLOR;
  ctx.fill(render.fill, "nonzero");
}

/** Draw per-facility range outlines and workload pie charts for facilities absent from the network, above roads/buildings. */
export function drawCoverageOutlines(
  ctx: CanvasRenderingContext2D,
  view: { zoom: number },
  render: InfraCoverageRender
) {
  ctx.strokeStyle = COVERAGE_OUTLINE_COLOR;
  ctx.lineWidth = COVERAGE_OUTLINE_PX / view.zoom;
  ctx.lineJoin = "round";
  ctx.setLineDash([]);
  ctx.stroke(render.activeOutline);
  ctx.setLineDash([COVERAGE_DASH_PX / view.zoom, COVERAGE_GAP_PX / view.zoom]);
  ctx.stroke(render.inactiveOutline);
  ctx.setLineDash([]);
  for (const pie of render.pies) drawLoadPie(ctx, pie.x, pie.z, pie.load, view.zoom);
}

/* Available goods */

/** network = infrastructure goods network; coverage = service goods effect range. */
export type InfraProductKind = "network" | "coverage";

/** Goods overlaid on the map. Draw at most one network (infrastructure good) and one supply-facility/coverage layer simultaneously. */
export type InfraSelection = { network: number | null; coverage: number | null };

export type InfraProductOption = {
  productId: number;
  kind: InfraProductKind;
  /** variant of the supply facility with an effect range (service goods only; used for the icon). */
  supplierVariants: number[];
  /** Infrastructure good received as input by range-based supply facilities (e.g. high-voltage power at substations; service goods only, null if none). */
  feedProductId: number | null;
};

/** Find an infrastructure good among the supply facility master's inputs. */
function findFeedProduct(supplierVariants: number[], masterData: MasterData | null): number | null {
  for (const variant of supplierVariants) {
    for (const id of Object.keys(masterData?.agents?.[variant]?.materials ?? {})) {
      if (masterData?.products?.[Number(id)]?.type === GOODS_INFRASTRUCTURE) return Number(id);
    }
  }
  return null;
}

/**
 * Goods selectable for infrastructure display. Use the master product type for infrastructure goods and the core list
 * (GET /api/v1/buildings/service-coverage) for range-based service goods. Only the core determines whether ranges exist.
 */
export function useInfraProducts(apiBaseUrl: string): InfraProductOption[] {
  const masterData = useMasterData();
  const [coverageProducts, setCoverageProducts] = useState<{ product_id: number; supplier_variants: number[] }[]>([]);

  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    fetchApi<{ products: { product_id: number; supplier_variants: number[] }[] }>(
      buildApiUrl(apiBaseUrl, "/api/v1/buildings/service-coverage"),
      { signal: controller.signal }
    )
      .then((data) => setCoverageProducts(data.products ?? []))
      .catch((err) => {
        if (!controller.signal.aborted) console.warn("[infraOverlay] failed to load coverage products:", err);
      });
    return () => controller.abort();
  }, [apiBaseUrl]);

  const network = useMemo(() => Object.entries(masterData?.products ?? {})
    .filter(([, product]) => product.type === GOODS_INFRASTRUCTURE)
    .map(([id]) => ({ productId: Number(id), kind: "network" as const, supplierVariants: [] as number[], feedProductId: null }))
    .sort((a, b) => a.productId - b.productId), [masterData]);

  const coverage = useMemo(() => coverageProducts.map((p) => {
    const supplierVariants = p.supplier_variants ?? [];
    return {
      productId: p.product_id,
      kind: "coverage" as const,
      supplierVariants,
      feedProductId: findFeedProduct(supplierVariants, masterData),
    };
  }), [coverageProducts, masterData]);

  return useMemo(() => [...network, ...coverage], [network, coverage]);
}

/** One infrastructure display choice. productId identifies the good used for the choice's name. */
export type InfraOverlayOption = { productId: number; selection: InfraSelection };

/**
 * Infrastructure display choices. For service goods whose range-based supply facilities receive infrastructure goods
 * (electricity at substations, water at pumps), pair them with the corresponding infrastructure network in one choice
 * to show the entire path from power plants, etc. to buildings on one map. Do not list paired infrastructure goods separately.
 */
export function pairWithFeedNetwork(options: InfraProductOption[]): InfraOverlayOption[] {
  const feeds = new Set(options.map((p) => p.feedProductId).filter((id): id is number => id !== null));
  return options
    .filter((p) => !(p.kind === "network" && feeds.has(p.productId)))
    .map((p) => ({
      productId: p.productId,
      selection: p.kind === "network"
        ? { network: p.productId, coverage: null }
        : { network: p.feedProductId, coverage: p.productId },
    }));
}

export type InfraSelectionState = {
  /** Choices sorted by good ID. */
  options: InfraOverlayOption[];
  /** Selected choice. Use the first if nothing is selected or the selected good disappears from the choices. */
  option: InfraOverlayOption | null;
  select: (productId: number) => void;
};

/** Infrastructure choices and selection state, used by the city map infrastructure overlay. */
export function useInfraSelection(apiBaseUrl: string): InfraSelectionState {
  const products = useInfraProducts(apiBaseUrl);
  const options = useMemo(
    () => pairWithFeedNetwork(products).sort((a, b) => a.productId - b.productId),
    [products]
  );
  const [productId, setProductId] = useState<number | null>(null);
  const option = options.find((p) => p.productId === productId) ?? options[0] ?? null;
  return { options, option, select: setProductId };
}
