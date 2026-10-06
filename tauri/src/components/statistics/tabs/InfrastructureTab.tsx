/**
 * 統計ウィンドウ「インフラ網」タブ
 *
 * インフラ財(GOODS_INFRASTRUCTURE: 高圧電流・高圧水道など)の配送ネットワークを
 * 2Dマップ上に可視化する。データは GET /api/v1/stats/infrastructure から取得し、
 * エポック単位でキャッシュする(useEpochCachedStats)。
 *
 * 画面構成:
 * - 上部: インフラ財の製品切り替えボタン
 * - 左列: 選択中製品のサマリー(供給在庫・配送量・需要量・充足率)と
 *         インフラ施設のリスト(接続済み・未接続を問わず全施設を列挙。
 *         各行に接続状態のドットを付け、クリックで経済主体詳細ウィンドウを開く)
 * - 右列: CityMapCanvas への埋め込み地図。地図の下敷きは海岸線と道路のみとし
 *         (標高色分けは行わない)、インフラパスを流量/容量の利用率で色分け・太さ表現し、
 *         生産施設(丸)・消費施設(菱形)をマーカー表示する。未接続施設は赤で描画する。
 *
 * 流れのアニメーション:
 * 描画はすべて CityMapCanvas の animatedOverlayDraw(アニメ専用の第2キャンバス、
 * rAF駆動)で行う。流量のあるパスには明るい縞模様(破線)を重ね、lineDashOffset を
 * 時間で動かして流れの向きを表現する。API の flow は符号付きで、
 * 正 = points の並び順(start→end)の向きに流れたことを表す。縞の速度は選択中製品の
 * 最大流量で正規化し、流量が多いパスほど速く流す。prefers-reduced-motion 環境では
 * 時刻を0に固定して縞を静止させる。
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { STATUS_ACTIVE } from "../../../constants";
import { useMasterData } from "../../../contexts/MasterDataContext";
import { useEpochCachedStats } from "../useEpochCache";
import {
  CityMapCanvas,
  type CityMapViewState,
} from "../../map/CityMapCanvas";
import "./InfrastructureTab.css";

/* ---- GET /api/v1/stats/infrastructure のレスポンス型 ---- */

type InfraProducerItem = {
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

type InfraConsumerItem = {
  agent_id: number;
  name: string;
  center_point: number[] | null;
  /** 接続端子ノードの座標。パスの端点はここに溶接されるので描画はこちらを優先する */
  node_point: number[] | null;
  demand: number;
  received: number;
  connected: boolean;
};

type InfraPathItem = {
  path_id: number;
  status: number;
  /** 当エポックの実流量。符号付きで、正 = points の並び順(start→end)の向き */
  flow: number;
  capacity: number;
  points: number[][];
};

type InfraProductStats = {
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

type InfrastructureStats = {
  epoch: number;
  simulation_days: number;
  products: InfraProductStats[];
};

/* ---- 描画色 ---- */

const COLOR_DISCONNECTED = "#ef4444"; // 未接続: 赤
const COLOR_PRODUCER = "#10b981";     // 生産施設: エメラルド
const COLOR_CONSUMER = "#38bdf8";     // 消費施設: スカイブルー
const COLOR_PATH_INACTIVE = "#6b7280"; // 建設中パス: グレー
const COLOR_FLOW_STRIPE = "rgba(255, 255, 255, 0.85)"; // 流れの縞模様: 明るい白

/* ---- 流れの縞模様アニメーションの定数(いずれもワールド座標基準) ----
 * 画面px基準にすると、ズームアウトしてパスが画面上で短くなっても縞は同じ速さで
 * 流れるため相対的に猛烈に速く見える。ワールド基準にすることで「2倍ズームインすれば
 * 画面上の流速も2倍」という素直な挙動になる。 */

/** 縞1つの長さ(ワールド単位=m) */
const STRIPE_DASH_M = 10;
/** 縞と縞の間隔(m) */
const STRIPE_GAP_M = 14;
/** 縞の移動速度の下限(m/秒)。流量がわずかでも流れが視認できる控えめな速さ */
const STRIPE_SPEED_MIN_M = 1;
/** 縞の移動速度の上限(m/秒)。選択中製品の最大流量のパスがこの速さになる */
const STRIPE_SPEED_MAX_M = 15;
/** 広域表示で縞が1px未満に潰れないための、縞長の画面px下限 */
const STRIPE_MIN_DASH_PX = 4;

/* ---- パスの太さ(infra_capacity 基準、画面ピクセル固定) ----
 * 利用率は色(utilizationColor)だけで表し、太さはバリアントの輸送容量を表す。
 * 線形だと容量5000のパスが太くなりすぎるので平方根で圧縮する。 */

/** 太さの基準となる infra_capacity(小型送電線1102の値) */
const CAPACITY_REF = 2000;
/** 基準容量のときの画面上の太さ(px) */
const CAPACITY_REF_PX = 5;
const WIDTH_MIN_PX = 3;
const WIDTH_MAX_PX = 12;

/**
 * パスの利用率(0〜1)を色に変換する。
 * 低利用率はシアン系、高利用率(容量逼迫)は赤系のHSL補間。
 */
function utilizationColor(rate: number): string {
  const clamped = Math.max(0, Math.min(1, rate));
  const hue = 190 - 190 * clamped; // 190(シアン) → 0(赤)
  return `hsl(${hue}, 85%, 55%)`;
}

/** 経済主体詳細ウィンドウを開く */
function openAgentDetail(agentId: number) {
  void invoke("open_registered_window", {
    windowType: "agent_detail",
    entityId: String(agentId),
  });
}

export function InfrastructureTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const { data, loading, error } = useEpochCachedStats<InfrastructureStats>(
    apiBaseUrl,
    "/api/v1/stats/infrastructure"
  );

  const products = useMemo(() => data?.products ?? [], [data]);

  // 選択中のインフラ財。初期値はデータが入った最初の製品(なければ先頭)。
  const [selectedPid, setSelectedPid] = useState<number | null>(null);
  useEffect(() => {
    if (products.length === 0) return;
    if (selectedPid !== null && products.some((p) => p.product_id === selectedPid)) {
      return;
    }
    const withData = products.find(
      (p) => p.producers.length > 0 || p.consumers.length > 0 || p.paths.length > 0
    );
    setSelectedPid((withData ?? products[0]).product_id);
  }, [products, selectedPid]);

  const selected = useMemo(
    () => products.find((p) => p.product_id === selectedPid) ?? null,
    [products, selectedPid]
  );

  /** 製品の表示名と単位(マスターデータから解決) */
  const productLabel = useCallback(
    (pid: number) => masterData?.products[pid]?.display_name ?? String(pid),
    [masterData]
  );
  const unit = selected
    ? masterData?.products[selected.product_id]?.unit ?? ""
    : "";

  // OSレベルで動きの抑制が要求されている場合は縞を静止させる(時刻を0に固定)
  const reducedMotion = useMemo(
    () => window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ?? false,
    []
  );

  /**
   * 選択中製品のパス描画データ。折れ線のPath2Dは毎フレーム構築すると無駄なので、
   * 選択製品が変わったときだけ作り直す。縞の速度は|flow|を製品内の最大流量で
   * 正規化してMIN〜MAXの範囲へ写像する(流量が多いパスほど速く流れる)。
   * 太さはバリアントの infra_capacity 基準。APIのcapacityは
   * infra_capacity × simulation_days なので、simulation_days で割って戻す。
   */
  const renderPaths = useMemo(() => {
    if (!selected) return [];
    const simDays = data?.simulation_days || 1;
    const maxAbsFlow = selected.paths.reduce(
      (max, p) => Math.max(max, Math.abs(p.flow)),
      0
    );
    return selected.paths
      .filter((p) => p.points.length >= 2)
      .map((p) => {
        const shape = new Path2D();
        shape.moveTo(p.points[0][0], p.points[0][1]);
        for (let i = 1; i < p.points.length; i++) {
          shape.lineTo(p.points[i][0], p.points[i][1]);
        }
        const absFlow = Math.abs(p.flow);
        const infraCapacity = p.capacity / simDays;
        return {
          shape,
          isActive: p.status === STATUS_ACTIVE,
          absFlow,
          rate: p.capacity > 0 ? Math.min(absFlow / p.capacity, 1) : 0,
          widthPx:
            infraCapacity > 0
              ? Math.min(
                  WIDTH_MAX_PX,
                  Math.max(
                    WIDTH_MIN_PX,
                    CAPACITY_REF_PX * Math.sqrt(infraCapacity / CAPACITY_REF)
                  )
                )
              : WIDTH_MIN_PX,
          // 流れの向き。flowは符号付きで、正 = points の並び順(start→end)の向き
          direction: p.flow >= 0 ? 1 : -1,
          speedM:
            maxAbsFlow > 0
              ? STRIPE_SPEED_MIN_M +
                (STRIPE_SPEED_MAX_M - STRIPE_SPEED_MIN_M) * (absFlow / maxAbsFlow)
              : 0,
        };
      });
  }, [selected, data?.simulation_days]);

  /**
   * インフラ網のオーバーレイ描画(CityMapCanvasのanimatedOverlayDrawとして
   * rAFループから毎フレーム呼ばれる)。
   * ctx にはワールド座標系のビュー変換(Y反転込み)が適用済みなので、
   * 図形はワールド座標のまま描ける。画面上で一定のサイズにしたいものは
   * /view.zoom でスケールする。文字は描かないためY反転の打ち消しは不要。
   */
  const drawInfraOverlay = useCallback(
    (ctx: CanvasRenderingContext2D, view: CityMapViewState, timeMs: number) => {
      if (!selected) return;
      const time = reducedMotion ? 0 : timeMs;
      ctx.lineJoin = "round";

      // --- インフラパス(折れ線) ---
      for (const path of renderPaths) {
        if (path.isActive) {
          // ベース線: 太さは容量(バリアント固有)、色は利用率。
          // lineWidthはワールド座標系のctxに対する指定なので、/view.zoomすると
          // 画面上の太さがズームによらず一定になる。
          ctx.strokeStyle = utilizationColor(path.rate);
          ctx.lineWidth = path.widthPx / view.zoom;
          ctx.setLineDash([]);
          ctx.lineCap = "round";
          ctx.globalAlpha = path.absFlow > 0 ? 0.95 : 0.55;
          ctx.stroke(path.shape);

          // 縞模様: 流量のあるパスにだけ明るい破線を重ね、流れの向きへ動かす。
          // lineDashOffsetは増やすと縞が始点側へ戻るため、start→end方向(flow正)へ
          // 流すには負のオフセットを時間に比例して増やす。破線長・オフセットは
          // ワールド単位のまま指定する(=画面上の流速がズーム倍率に比例する)。
          // ただし縞長だけは広域表示で潰れないよう画面px下限でクランプする。
          if (path.absFlow > 0) {
            const dashWorld = Math.max(STRIPE_DASH_M, STRIPE_MIN_DASH_PX / view.zoom);
            const gapWorld = dashWorld * (STRIPE_GAP_M / STRIPE_DASH_M);
            // 長時間セッションでオフセットが発散して縞がジッタるのを周期で畳んで防ぐ
            const phase = ((time / 1000) * path.speedM) % (dashWorld + gapWorld);
            ctx.strokeStyle = COLOR_FLOW_STRIPE;
            ctx.lineWidth = path.widthPx / view.zoom;
            ctx.setLineDash([dashWorld, gapWorld]);
            ctx.lineDashOffset = -path.direction * phase;
            ctx.lineCap = "butt";
            ctx.globalAlpha = 0.9;
            ctx.stroke(path.shape);
            ctx.lineDashOffset = 0;
          }
        } else {
          // 建設中・計画中のパスはグレーの破線で示す(縞アニメなし)
          const dashWorld = Math.max(6, STRIPE_MIN_DASH_PX / view.zoom);
          ctx.strokeStyle = COLOR_PATH_INACTIVE;
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

      // --- 生産施設(丸) ---
      for (const producer of selected.producers) {
        // パスの端点は接続端子ノードに溶接されるので、そちらを優先して線端に重ねる
        const cp = producer.node_point ?? producer.center_point;
        if (!cp || cp.length < 2) continue;
        ctx.beginPath();
        ctx.arc(cp[0], cp[1], r, 0, Math.PI * 2);
        ctx.fillStyle = producer.connected ? COLOR_PRODUCER : COLOR_DISCONNECTED;
        ctx.fill();
        ctx.lineWidth = outline;
        ctx.strokeStyle = "#0b0f19";
        ctx.stroke();
      }

      // --- 消費施設(菱形) ---
      for (const consumer of selected.consumers) {
        const cp = consumer.node_point ?? consumer.center_point;
        if (!cp || cp.length < 2) continue;
        ctx.beginPath();
        ctx.moveTo(cp[0], cp[1] + r);
        ctx.lineTo(cp[0] + r, cp[1]);
        ctx.lineTo(cp[0], cp[1] - r);
        ctx.lineTo(cp[0] - r, cp[1]);
        ctx.closePath();
        ctx.fillStyle = consumer.connected ? COLOR_CONSUMER : COLOR_DISCONNECTED;
        ctx.fill();
        ctx.lineWidth = outline;
        ctx.strokeStyle = "#0b0f19";
        ctx.stroke();
      }
    },
    [selected, renderPaths, reducedMotion]
  );

  if (loading) {
    return <div className="infra-tab__loading">{t("common.loading")}</div>;
  }

  if (error || !data) {
    return (
      <div className="infra-tab__error">
        {t("common.fetchError")}
        {error ? `: ${error}` : ""}
      </div>
    );
  }

  if (products.length === 0) {
    return (
      <div className="infra-tab__loading">
        {t("statistics.infrastructure.noData")}
      </div>
    );
  }

  // 左列に並べるインフラ施設(接続済み・未接続を問わず全件)。
  // 生産施設・消費施設をまとめ、各行で種別と接続状態を示す。
  const facilities = selected
    ? [
        ...selected.producers.map((p) => ({
          agent_id: p.agent_id,
          name: p.name,
          connected: p.connected,
          kind: "producer" as const,
        })),
        ...selected.consumers.map((c) => ({
          agent_id: c.agent_id,
          name: c.name,
          connected: c.connected,
          kind: "consumer" as const,
        })),
      ]
    : [];
  // タイトル脇のバッジには注意喚起として未接続施設の件数を出す。
  const disconnectedCount = facilities.filter((f) => !f.connected).length;

  const fulfillmentPercent = selected
    ? Math.round(selected.fulfillment_rate * 100)
    : 0;

  return (
    <div className="infra-tab">
      <h2 className="infra-tab__title">{t("statistics.infrastructure.title")}</h2>

      {/* 製品切り替え */}
      <div className="infra-tab__products">
        {products.map((p) => (
          <button
            key={p.product_id}
            type="button"
            className={[
              "infra-tab__product-btn",
              p.product_id === selectedPid && "infra-tab__product-btn--active",
            ]
              .filter(Boolean)
              .join(" ")}
            onClick={() => setSelectedPid(p.product_id)}
          >
            {productLabel(p.product_id)}
          </button>
        ))}
      </div>

      <div className="infra-tab__grid">
        {/* 左列: サマリーと未接続リスト */}
        <div className="infra-tab__col-left">
          {selected && (
            <div className="infra-card">
              <h3 className="infra-card__title">
                {productLabel(selected.product_id)}
              </h3>
              <dl className="infra-summary">
                <div className="infra-summary__row">
                  <dt>{t("statistics.infrastructure.production")}</dt>
                  <dd>
                    {selected.production_total.toFixed(1)} {unit}
                  </dd>
                </div>
                <div className="infra-summary__row">
                  <dt>{t("statistics.infrastructure.delivered")}</dt>
                  <dd>
                    {selected.received_total.toFixed(1)} {unit}
                  </dd>
                </div>
                <div className="infra-summary__row">
                  <dt>{t("statistics.infrastructure.demand")}</dt>
                  <dd>
                    {selected.demand_total.toFixed(1)} {unit}
                  </dd>
                </div>
                <div className="infra-summary__row">
                  <dt>{t("statistics.infrastructure.fulfillment")}</dt>
                  <dd>{fulfillmentPercent}%</dd>
                </div>
              </dl>
              <div className="infra-fulfillment-track">
                <div
                  className="infra-fulfillment-fill"
                  style={{
                    width: `${Math.min(fulfillmentPercent, 100)}%`,
                    background:
                      fulfillmentPercent >= 100
                        ? "linear-gradient(90deg, #059669, #10b981)"
                        : fulfillmentPercent >= 50
                          ? "linear-gradient(90deg, #ca8a04, #eab308)"
                          : "linear-gradient(90deg, #dc2626, #ef4444)",
                  }}
                />
              </div>
              <div className="infra-summary__counts">
                <span>
                  {t("statistics.infrastructure.producers")}: {selected.producers.length}
                </span>
                <span>
                  {t("statistics.infrastructure.consumers")}: {selected.consumers.length}
                </span>
              </div>
            </div>
          )}

          <div className="infra-card infra-card--list">
            <h3 className="infra-card__title">
              {t("statistics.infrastructure.facilities")}
              {disconnectedCount > 0 && (
                <span className="infra-card__badge">{disconnectedCount}</span>
              )}
            </h3>
            {facilities.length === 0 ? (
              <div className="infra-card__empty">
                {t("statistics.infrastructure.noFacilities")}
              </div>
            ) : (
              <ul className="infra-facility-list">
                {facilities.map((facility) => (
                  <li key={facility.agent_id}>
                    <button
                      type="button"
                      className={[
                        "infra-facility-list__item",
                        !facility.connected &&
                          "infra-facility-list__item--disconnected",
                      ]
                        .filter(Boolean)
                        .join(" ")}
                      onClick={() => openAgentDetail(facility.agent_id)}
                    >
                      <span
                        className={[
                          "infra-facility-list__dot",
                          facility.connected
                            ? "infra-facility-list__dot--connected"
                            : "infra-facility-list__dot--disconnected",
                        ].join(" ")}
                      />
                      <span className="infra-facility-list__name">
                        {facility.name}
                      </span>
                      <span className="infra-facility-list__kind">
                        {facility.kind === "producer"
                          ? t("statistics.infrastructure.producers")
                          : t("statistics.infrastructure.consumers")}
                      </span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </div>

        {/* 右列: 地図オーバーレイ */}
        <div className="infra-tab__col-right">
          <div className="infra-tab__map">
            <CityMapCanvas
              apiBaseUrl={apiBaseUrl}
              interactive
              layers={{
                // 下敷きは海岸線と道路のみ。標高色分けや線路・区画・建物・地区枠・
                // 等高線・グリッドはインフラ網の可視化には不要なので描かない。
                elevation: false,
                coastline: true,
                trunkRoads: true,
                allRoads: true,
                railways: false,
                buildings: false,
                stations: false,
                parcels: false,
                contours: false,
                grid: false,
                zoneBorders: false,
              }}
              animatedOverlayDraw={drawInfraOverlay}
            />
          </div>
          {/* 凡例 */}
          <div className="infra-legend">
            <span className="infra-legend__item">
              <span className="infra-legend__gradient" />
              {t("statistics.infrastructure.legendUtilization")}
            </span>
            <span className="infra-legend__item">
              <span className="infra-legend__stripes" />
              {t("statistics.infrastructure.legendFlow")}
            </span>
            <span className="infra-legend__item">
              <span
                className="infra-legend__marker infra-legend__marker--circle"
                style={{ background: COLOR_PRODUCER }}
              />
              {t("statistics.infrastructure.producers")}
            </span>
            <span className="infra-legend__item">
              <span
                className="infra-legend__marker infra-legend__marker--diamond"
                style={{ background: COLOR_CONSUMER }}
              />
              {t("statistics.infrastructure.consumers")}
            </span>
            <span className="infra-legend__item">
              <span
                className="infra-legend__marker infra-legend__marker--circle"
                style={{ background: COLOR_DISCONNECTED }}
              />
              {t("statistics.infrastructure.legendDisconnected")}
            </span>
          </div>
        </div>
      </div>
    </div>
  );
}
