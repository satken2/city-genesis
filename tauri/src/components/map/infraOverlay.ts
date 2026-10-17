/**
 * 都市地図のインフラ表示(CityMapCanvas の infraSelection)で使う型・財の一覧・描画。
 *
 * 網と効果範囲を1つずつ同時に描ける。
 * - 網(インフラ財 GOODS_INFRASTRUCTURE): GET /api/v1/stats/infrastructure の網。パスを流量/容量の利用率で
 *   色分けし(太さは容量)、流量のあるパスには流れの向きへ動く縞を重ね、生産施設(丸)・消費施設(菱形)を置く。
 *   未接続の施設は赤で描く。
 * - 効果範囲(効果範囲を持つサービス財): GET /api/v1/buildings/service-coverage/{product_id}。稼働中の施設の範囲を
 *   重なりで濃くならないよう和集合で薄く塗り、施設ごとの輪郭を描く(稼働していない施設は破線)。
 *   建物は範囲内・範囲外で塗り分ける(建物の描画に組み込むため CityMapCanvas が行う)。
 *
 * 描画関数の ctx にはワールド座標系のビュー変換(Y反転込み)が適用済みとする。
 * 画面上で一定の太さ・大きさにしたいものは /view.zoom でスケールする。
 */

import { useEffect, useMemo, useState } from "react";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { GOODS_INFRASTRUCTURE, STATUS_ACTIVE } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import type { EffectRangeRegion } from "../../utils/ipc";
import type { MasterData } from "../../types";

/* ============================================================================
 * インフラ網(GET /api/v1/stats/infrastructure)
 * ========================================================================== */

export type InfraProducerItem = {
  agent_id: number;
  name: string;
  center_point: number[] | null;
  /** 接続端子ノードの座標。パスの端点はここに溶接されるので描画はこちらを優先する */
  node_point: number[] | null;
  /** 当エポックの生産量(配送の直前に作った量) */
  production: number;
  shipped: number;
  connected: boolean;
};

export type InfraConsumerItem = {
  agent_id: number;
  name: string;
  center_point: number[] | null;
  /** 接続端子ノードの座標。パスの端点はここに溶接されるので描画はこちらを優先する */
  node_point: number[] | null;
  demand: number;
  received: number;
  connected: boolean;
};

export type InfraPathItem = {
  path_id: number;
  status: number;
  /** 当エポックの実流量。符号付きで、正 = points の並び順(start→end)の向き */
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

/* 流れの縞模様はワールド座標基準(m)にする。画面px基準だとズームアウトでパスが画面上で
 * 短くなっても縞が同じ速さで流れ、相対的に猛烈に速く見えるため。縞長だけは画面px下限で潰れを防ぐ。 */
const STRIPE_DASH_M = 10;
const STRIPE_GAP_M = 14;
const STRIPE_SPEED_MIN_M = 1;
const STRIPE_SPEED_MAX_M = 15;
const STRIPE_MIN_DASH_PX = 4;

/* パスの太さは infra_capacity 基準(画面px)。線形だと大容量のパスが太すぎるので平方根で圧縮する。
 * 利用率は色だけで表す。 */
const CAPACITY_REF = 2000;
const CAPACITY_REF_PX = 5;
const WIDTH_MIN_PX = 3;
const WIDTH_MAX_PX = 12;

/** パスの利用率(0〜1)の色。低利用率はシアン、容量逼迫は赤。 */
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
  /** 流れの向き。正 = points の並び順(start→end) */
  direction: number;
  speedM: number;
};

export type InfraNetworkRender = { product: InfraProductStats; paths: NetworkPathRender[] };

/**
 * 網の描画データを作る。縞の速度は|flow|を製品内の最大流量で正規化する(流量が多いほど速い)。
 * API の capacity は infra_capacity × simulation_days なので、太さは日数で割って戻した容量から決める。
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

/** 網を1フレーム描く。timeMs は縞を動かす経過時刻(動きを止めるときは0)。 */
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

      // lineDashOffset は増やすと縞が始点側へ戻るので、start→end へ流すには負の向きに進める。
      if (path.absFlow > 0) {
        const dashWorld = Math.max(STRIPE_DASH_M, STRIPE_MIN_DASH_PX / view.zoom);
        const gapWorld = dashWorld * (STRIPE_GAP_M / STRIPE_DASH_M);
        // 長時間の表示でオフセットが発散して縞がジッタるのを周期で畳んで防ぐ
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
      // 建設中・計画中のパスはグレーの破線(縞なし)
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
  }
}

/* ============================================================================
 * 効果範囲(GET /api/v1/buildings/service-coverage/{product_id})
 * ========================================================================== */

export type ServiceCoverageSupplier = {
  agent_id: number;
  name: string;
  variant: number;
  status: number;
  center_point: number[] | null;
  region: EffectRangeRegion;
};

export type ServiceCoverageData = {
  product_id: number;
  suppliers: ServiceCoverageSupplier[];
  /** 稼働中の建物のうち、敷地の土地区画が稼働中の施設の効果範囲に入るもの / 入らないもの */
  covered_building_ids: number[];
  uncovered_building_ids: number[];
};

/** 範囲内・範囲外の建物の色。Unity の ServiceCoverageOverlay と一致させる。 */
export const COVERAGE_IN_RANGE_COLOR = "#3b82f6";
export const COVERAGE_OUT_OF_RANGE_COLOR = "#ef4444";
export const COVERAGE_OUTLINE_COLOR = "#2563eb";
const COVERAGE_FILL_COLOR = "rgba(59, 130, 246, 0.16)";
const COVERAGE_OUTLINE_PX = 1.5;
const COVERAGE_DASH_PX = 7;
const COVERAGE_GAP_PX = 5;

export type InfraCoverageRender = {
  /** 稼働中の施設の範囲の和集合(穴を逆向きに巻いた非ゼロ規則の1枚) */
  fill: Path2D;
  activeOutline: Path2D;
  inactiveOutline: Path2D;
  covered: Set<number>;
  uncovered: Set<number>;
};

/** リングの符号付き面積(ワールドXZ)。正なら Canvas の arc の既定と同じ向き。 */
function signedArea(ring: number[][]): number {
  let area = 0;
  for (let i = 0; i < ring.length; i++) {
    const [x1, z1] = ring[i];
    const [x2, z2] = ring[(i + 1) % ring.length];
    area += x1 * z2 - x2 * z1;
  }
  return area / 2;
}

/** リングを指定の向き(外周=正・穴=負)で閉路として加える。 */
function appendRing(path: Path2D, ring: number[][], positive: boolean) {
  if (ring.length < 3) return;
  const points = (signedArea(ring) > 0) === positive ? ring : [...ring].reverse();
  path.moveTo(points[0][0], points[0][1]);
  for (let i = 1; i < points.length; i++) path.lineTo(points[i][0], points[i][1]);
  path.closePath();
}

function appendRegion(path: Path2D, region: EffectRangeRegion) {
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

/** 効果範囲の描画データを作る。塗りは稼働中の施設だけで、輪郭は稼働しているかで実線・破線に分ける。 */
export function buildCoverageRender(data: ServiceCoverageData): InfraCoverageRender {
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
  };
}

/** 効果範囲の面を塗る。道路・建物より先に描く。 */
export function drawCoverageFill(ctx: CanvasRenderingContext2D, render: InfraCoverageRender) {
  ctx.fillStyle = COVERAGE_FILL_COLOR;
  ctx.fill(render.fill, "nonzero");
}

/** 施設ごとの効果範囲の輪郭を描く。道路・建物の上に描く。 */
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
}

/* ============================================================================
 * 表示できる財の一覧
 * ========================================================================== */

/** network = インフラ財の網、coverage = サービス財の効果範囲 */
export type InfraProductKind = "network" | "coverage";

/** 地図に重ねる財。網(インフラ財)と効果範囲(サービス財)をそれぞれ1つまで同時に描ける */
export type InfraSelection = { network: number | null; coverage: number | null };

/** CityMapCanvas が取得した表示中の財のデータ(onInfraLoaded で通知する) */
export type CityMapInfraData = {
  network: { product: InfraProductStats; simulationDays: number } | null;
  coverage: ServiceCoverageData | null;
};

export type InfraProductOption = {
  productId: number;
  kind: InfraProductKind;
  /** 効果範囲を持つ供給施設の variant(サービス財のみ。アイコンに使う) */
  supplierVariants: number[];
  /** 効果範囲を持つ供給施設が材料として受け取るインフラ財(変電所の高圧電力など。サービス財のみ・無ければ null) */
  feedProductId: number | null;
};

/** 供給施設のマスターの材料からインフラ財を探す */
function findFeedProduct(supplierVariants: number[], masterData: MasterData | null): number | null {
  for (const variant of supplierVariants) {
    for (const id of Object.keys(masterData?.agents?.[variant]?.materials ?? {})) {
      if (masterData?.products?.[Number(id)]?.type === GOODS_INFRASTRUCTURE) return Number(id);
    }
  }
  return null;
}

/**
 * インフラ表示で選べる財。インフラ財はマスターの商品種別、効果範囲を持つサービス財はコアの一覧
 * (GET /api/v1/buildings/service-coverage)で決める。効果範囲の有無の判定はコアだけが持つ。
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

/** インフラ表示の選択肢1つ。productId は選択肢の名前に使う財 */
export type InfraOverlayOption = { productId: number; selection: InfraSelection };

/**
 * インフラ表示の選択肢。効果範囲の供給施設がインフラ財を受け取るサービス財(変電所の電力・水道ポンプの水道)は、
 * 発電所などから建物に届くまでを1枚で見せるため、そのインフラ財の網と組にして1つの選択肢にする。
 * 組になったインフラ財は単独では並べない。
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
  /** 財の番号順に並べた選択肢 */
  options: InfraOverlayOption[];
  /** 選択中の選択肢。未選択や選んだ財が選択肢から消えたときは先頭 */
  option: InfraOverlayOption | null;
  select: (productId: number) => void;
};

/** インフラ表示の選択肢と選択状態。都市マップのインフラオーバーレイと統計のインフラ網タブが共有する */
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
