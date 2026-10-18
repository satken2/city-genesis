/**
 * 都市地図のインフラ表示(CityMapCanvas の infraSelection)で使う型・財の一覧・描画。
 *
 * 網と効果範囲を1つずつ同時に描ける。
 * - 網(インフラ財 GOODS_INFRASTRUCTURE): GET /api/v1/stats/infrastructure の網。パスを流量/容量の利用率で
 *   色分けし(太さは容量)、流量のあるパスには流れの向きへ動く縞を重ね、生産施設(丸)・消費施設(菱形)を置く。
 *   未接続の施設は赤で描く。
 * - 供給施設と効果範囲: GET /api/v1/buildings/service-coverage/{product_id}。財を供給する施設を置き、範囲を持つ施設は
 *   稼働中の施設の範囲を重なりで濃くならないよう和集合で薄く塗り、施設ごとの輪郭を描く(稼働していない施設は破線)。
 *   建物は範囲内・範囲外で塗り分ける(建物の描画に組み込むため CityMapCanvas が行う)。範囲を持たない財(消防など)は施設だけ。
 * - 施設の業務逼迫度(販売量 ÷ 生産能力)は円グラフで示し、施設ごとに1つだけ置く。網の施設は記号の下、網に出ない施設は施設の中心。
 *   稼働率(生産量 ÷ 生産能力)ではないのは、需要と無関係に能力いっぱい待機生産する施設(消防署・定量方式の発電所など)が
 *   常に満杯に見え、施設の種類によって色の意味が変わってしまうため。
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
  /** 当エポックの稼働率(0〜1) */
  utilization: number;
  /** 当エポックの業務逼迫度(出荷量 ÷ 生産能力。0〜1) */
  load: number;
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
  /** 当エポックの稼働率(0〜1) */
  utilization: number;
  /** 当エポックの業務逼迫度(自身が供給する財の販売量 ÷ 生産能力。0〜1) */
  load: number;
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

/* 施設の業務逼迫度の円グラフ(画面px)。網の施設では記号の下に間を空けて置く。 */
const PIE_RADIUS_PX = 10;
const PIE_GAP_PX = 3;

/** 施設の業務逼迫度の円グラフの色。90%を超えると赤、70%を超えるとオレンジ、それ以下は緑。 */
function facilityLoadColor(rate: number): string {
  return rate > 0.9 ? "#ef4444" : rate > 0.7 ? "#f97316" : "#22c55e";
}

/**
 * (x, z) を中心に業務逼迫度の円グラフを描く。100%で円、50%で半円になるよう、画面の真上から時計回りに伸ばす。
 * 逼迫度が低くても位置と全体の大きさが分かるよう、半透明の白い円を下地にし、明るい陸地でも埋もれないよう縁取る。
 * ctx は+Zを上にするY反転を含むので、画面で時計回りにするには角度を減らす向きに描く。
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

/* ============================================================================
 * 効果範囲(GET /api/v1/buildings/service-coverage/{product_id})
 * ========================================================================== */

export type ServiceCoverageSupplier = {
  agent_id: number;
  name: string;
  variant: number;
  status: number;
  /** 当エポックの稼働率(0〜1) */
  utilization: number;
  /** 当エポックの業務逼迫度(販売量 ÷ 生産能力。0〜1) */
  load: number;
  center_point: number[] | null;
  /** 効果範囲。範囲を持たない施設(消防署など)は null */
  region: EffectRangeRegion | null;
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
  /** 業務逼迫度の円グラフを置く施設。網にも出る施設は網の側で描くので含めない */
  pies: { x: number; z: number; load: number }[];
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
 * 効果範囲の描画データを作る。塗りは稼働中の施設だけで、輪郭は稼働しているかで実線・破線に分ける。
 * network は一緒に描く網(無ければ null)で、そこに出る施設には円グラフを置かない。
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

/** 効果範囲の面を塗る。道路・建物より先に描く。 */
export function drawCoverageFill(ctx: CanvasRenderingContext2D, render: InfraCoverageRender) {
  ctx.fillStyle = COVERAGE_FILL_COLOR;
  ctx.fill(render.fill, "nonzero");
}

/** 施設ごとの効果範囲の輪郭と、網に出ない施設の業務逼迫度の円グラフを描く。道路・建物の上に描く。 */
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

/* ============================================================================
 * 表示できる財の一覧
 * ========================================================================== */

/** network = インフラ財の網、coverage = サービス財の効果範囲 */
export type InfraProductKind = "network" | "coverage";

/** 地図に重ねる財。網(インフラ財)と供給施設・効果範囲(coverage)をそれぞれ1つまで同時に描ける */
export type InfraSelection = { network: number | null; coverage: number | null };

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

/** インフラ表示の選択肢と選択状態。都市マップのインフラオーバーレイが使う */
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
