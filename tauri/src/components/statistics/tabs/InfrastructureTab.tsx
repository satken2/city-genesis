/**
 * 統計ウィンドウ「インフラ網」タブ
 *
 * インフラ財(GOODS_INFRASTRUCTURE: 高圧電流・高圧水道など)の配送ネットワークと、効果範囲を持つ
 * サービス財(電力・水道など)の供給範囲を2Dマップで見せる。地図の描画は CityMapCanvas の
 * インフラ表示(infraSelection)に任せ、このタブは財の切り替え・サマリー・施設一覧を持つ。
 *
 * 画面構成:
 * - 上部: 財の切り替えボタン(インフラ財、効果範囲を持つサービス財の順)
 * - 左列: インフラ財は GET /api/v1/stats/infrastructure(エポック単位でキャッシュ)のサマリー
 *         (生産量・配送量・需要量・充足率)とインフラ施設のリスト(接続状態のドット付き)。
 *         サービス財は地図が取得した効果範囲から、稼働中の施設数・範囲内外の建物数と供給施設のリスト。
 *         リストの行をクリックすると経済主体詳細ウィンドウを開く。
 * - 右列: 地図と凡例。下敷きは海岸線と道路のみ(標高色分けはしない)。サービス財では建物を
 *         範囲内・範囲外で塗り分けるため建物も描く。
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { STATUS_ACTIVE } from "../../../constants";
import { useMasterData } from "../../../contexts/MasterDataContext";
import { useEpochCachedStats } from "../useEpochCache";
import { CityMapCanvas, type CityMapLayerVisibility } from "../../map/CityMapCanvas";
import {
  useInfraProducts,
  type CityMapInfraData,
  type InfrastructureStats,
  type ServiceCoverageData,
} from "../../map/infraOverlay";
import { InfraMapLegend } from "../../map/InfraMapLegend";
import "./InfrastructureTab.css";

/** 経済主体詳細ウィンドウを開く */
function openAgentDetail(agentId: number) {
  void invoke("open_registered_window", {
    windowType: "agent_detail",
    entityId: String(agentId),
  });
}

/** 充足率・範囲内の割合のバーの色 */
function rateGradient(percent: number): string {
  return percent >= 100
    ? "linear-gradient(90deg, #059669, #10b981)"
    : percent >= 50
      ? "linear-gradient(90deg, #ca8a04, #eab308)"
      : "linear-gradient(90deg, #dc2626, #ef4444)";
}

export function InfrastructureTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const { data, loading, error } = useEpochCachedStats<InfrastructureStats>(
    apiBaseUrl,
    "/api/v1/stats/infrastructure"
  );
  const productOptions = useInfraProducts(apiBaseUrl);

  // 選択中の財。初期値はインフラ網のデータが入った最初の財(なければ先頭)。
  const [selectedPid, setSelectedPid] = useState<number | null>(null);
  useEffect(() => {
    if (productOptions.length === 0) return;
    if (selectedPid !== null && productOptions.some((p) => p.productId === selectedPid)) return;
    const withData = data?.products.find(
      (p) => p.producers.length > 0 || p.consumers.length > 0 || p.paths.length > 0
    );
    setSelectedPid(withData?.product_id ?? productOptions[0].productId);
  }, [productOptions, data, selectedPid]);

  const selectedKind = productOptions.find((p) => p.productId === selectedPid)?.kind ?? null;
  const network = selectedKind === "network"
    ? data?.products.find((p) => p.product_id === selectedPid) ?? null
    : null;
  const infraSelection = useMemo(() => selectedKind === null ? null : {
    network: selectedKind === "network" ? selectedPid : null,
    coverage: selectedKind === "coverage" ? selectedPid : null,
  }, [selectedKind, selectedPid]);

  // サービス財のサマリーは、地図が取得した効果範囲をそのまま使う
  const [coverage, setCoverage] = useState<ServiceCoverageData | null>(null);
  const handleInfraLoaded = useCallback((loaded: CityMapInfraData | null) => {
    setCoverage(loaded?.coverage ?? null);
  }, []);

  /** 製品の表示名と単位(マスターデータから解決) */
  const productLabel = useCallback(
    (pid: number) => masterData?.products[pid]?.display_name ?? String(pid),
    [masterData]
  );
  const unit = selectedPid !== null ? masterData?.products[selectedPid]?.unit ?? "" : "";

  // 下敷きは海岸線と道路のみ。サービス財は建物の塗り分けを見せるため建物も描く。
  const layers = useMemo<Partial<CityMapLayerVisibility>>(() => ({
    elevation: false,
    coastline: true,
    trunkRoads: true,
    allRoads: true,
    railways: false,
    buildings: selectedKind === "coverage",
    stations: false,
    parcels: false,
    contours: false,
    grid: false,
    zoneBorders: false,
  }), [selectedKind]);

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

  if (productOptions.length === 0) {
    return (
      <div className="infra-tab__loading">
        {t("statistics.infrastructure.noData")}
      </div>
    );
  }

  return (
    <div className="infra-tab">
      <h2 className="infra-tab__title">{t("statistics.infrastructure.title")}</h2>

      {/* 財の切り替え */}
      <div className="infra-tab__products">
        {productOptions.map((p) => (
          <button
            key={p.productId}
            type="button"
            className={[
              "infra-tab__product-btn",
              p.productId === selectedPid && "infra-tab__product-btn--active",
            ]
              .filter(Boolean)
              .join(" ")}
            onClick={() => setSelectedPid(p.productId)}
          >
            {productLabel(p.productId)}
          </button>
        ))}
      </div>

      <div className="infra-tab__grid">
        <div className="infra-tab__col-left">
          {selectedKind === "coverage" ? (
            <CoverageSummary
              title={selectedPid !== null ? productLabel(selectedPid) : ""}
              coverage={coverage}
            />
          ) : (
            network && <NetworkSummary title={productLabel(network.product_id)} unit={unit} network={network} />
          )}
        </div>

        <div className="infra-tab__col-right">
          <div className="infra-tab__map">
            <CityMapCanvas
              apiBaseUrl={apiBaseUrl}
              interactive
              layers={layers}
              infraSelection={infraSelection}
              onInfraLoaded={handleInfraLoaded}
            />
          </div>
          {selectedKind && (
            <div className="infra-tab__legend">
              <InfraMapLegend kind={selectedKind} />
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

/** インフラ財のサマリーとインフラ施設のリスト(接続済み・未接続を問わず全件) */
function NetworkSummary({
  title,
  unit,
  network,
}: {
  title: string;
  unit: string;
  network: InfrastructureStats["products"][number];
}) {
  const { t } = useTranslation();
  const facilities = [
    ...network.producers.map((p) => ({ agent_id: p.agent_id, name: p.name, connected: p.connected, kind: "producer" as const })),
    ...network.consumers.map((c) => ({ agent_id: c.agent_id, name: c.name, connected: c.connected, kind: "consumer" as const })),
  ];
  // タイトル脇のバッジには注意喚起として未接続施設の件数を出す。
  const disconnectedCount = facilities.filter((f) => !f.connected).length;
  const fulfillmentPercent = Math.round(network.fulfillment_rate * 100);

  return (
    <>
      <div className="infra-card">
        <h3 className="infra-card__title">{title}</h3>
        <dl className="infra-summary">
          <div className="infra-summary__row">
            <dt>{t("statistics.infrastructure.production")}</dt>
            <dd>{network.production_total.toFixed(1)} {unit}</dd>
          </div>
          <div className="infra-summary__row">
            <dt>{t("statistics.infrastructure.delivered")}</dt>
            <dd>{network.received_total.toFixed(1)} {unit}</dd>
          </div>
          <div className="infra-summary__row">
            <dt>{t("statistics.infrastructure.demand")}</dt>
            <dd>{network.demand_total.toFixed(1)} {unit}</dd>
          </div>
          <div className="infra-summary__row">
            <dt>{t("statistics.infrastructure.fulfillment")}</dt>
            <dd>{fulfillmentPercent}%</dd>
          </div>
        </dl>
        <div className="infra-fulfillment-track">
          <div
            className="infra-fulfillment-fill"
            style={{ width: `${Math.min(fulfillmentPercent, 100)}%`, background: rateGradient(fulfillmentPercent) }}
          />
        </div>
        <div className="infra-summary__counts">
          <span>{t("statistics.infrastructure.producers")}: {network.producers.length}</span>
          <span>{t("statistics.infrastructure.consumers")}: {network.consumers.length}</span>
        </div>
      </div>

      <div className="infra-card infra-card--list">
        <h3 className="infra-card__title">
          {t("statistics.infrastructure.facilities")}
          {disconnectedCount > 0 && <span className="infra-card__badge">{disconnectedCount}</span>}
        </h3>
        {facilities.length === 0 ? (
          <div className="infra-card__empty">{t("statistics.infrastructure.noFacilities")}</div>
        ) : (
          <ul className="infra-facility-list">
            {facilities.map((facility) => (
              <li key={facility.agent_id}>
                <button
                  type="button"
                  className={[
                    "infra-facility-list__item",
                    !facility.connected && "infra-facility-list__item--disconnected",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  onClick={() => openAgentDetail(facility.agent_id)}
                >
                  <span
                    className={`infra-facility-list__dot ${
                      facility.connected ? "infra-facility-list__dot--connected" : "infra-facility-list__dot--disconnected"
                    }`}
                  />
                  <span className="infra-facility-list__name">{facility.name}</span>
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
    </>
  );
}

/**
 * サービス財の効果範囲のサマリーと供給施設のリスト。範囲内・範囲外は稼働中の建物の数で、
 * 稼働していない施設(建設中など)の範囲は含まない(地図の塗り分けと同じ基準)。
 */
function CoverageSummary({ title, coverage }: { title: string; coverage: ServiceCoverageData | null }) {
  const { t } = useTranslation();
  if (!coverage) {
    return <div className="infra-card infra-card__empty">{t("common.loading")}</div>;
  }
  const activeCount = coverage.suppliers.filter((s) => s.status === STATUS_ACTIVE).length;
  const covered = coverage.covered_building_ids.length;
  const total = covered + coverage.uncovered_building_ids.length;
  const coveredPercent = total > 0 ? Math.round((covered / total) * 100) : 0;

  return (
    <>
      <div className="infra-card">
        <h3 className="infra-card__title">{title}</h3>
        <dl className="infra-summary">
          <div className="infra-summary__row">
            <dt>{t("statistics.infrastructure.activeSuppliers")}</dt>
            <dd>{activeCount} / {coverage.suppliers.length}</dd>
          </div>
          <div className="infra-summary__row">
            <dt>{t("statistics.infrastructure.coveredBuildings")}</dt>
            <dd>{covered}</dd>
          </div>
          <div className="infra-summary__row">
            <dt>{t("statistics.infrastructure.uncoveredBuildings")}</dt>
            <dd>{coverage.uncovered_building_ids.length}</dd>
          </div>
          <div className="infra-summary__row">
            <dt>{t("statistics.infrastructure.coveredRate")}</dt>
            <dd>{coveredPercent}%</dd>
          </div>
        </dl>
        <div className="infra-fulfillment-track">
          <div
            className="infra-fulfillment-fill"
            style={{ width: `${coveredPercent}%`, background: rateGradient(coveredPercent) }}
          />
        </div>
      </div>

      <div className="infra-card infra-card--list">
        <h3 className="infra-card__title">{t("statistics.infrastructure.suppliers")}</h3>
        {coverage.suppliers.length === 0 ? (
          <div className="infra-card__empty">{t("statistics.infrastructure.noFacilities")}</div>
        ) : (
          <ul className="infra-facility-list">
            {coverage.suppliers.map((supplier) => {
              const active = supplier.status === STATUS_ACTIVE;
              return (
                <li key={supplier.agent_id}>
                  <button
                    type="button"
                    className="infra-facility-list__item"
                    onClick={() => openAgentDetail(supplier.agent_id)}
                  >
                    <span
                      className={`infra-facility-list__dot ${
                        active ? "infra-facility-list__dot--connected" : "infra-facility-list__dot--inactive"
                      }`}
                    />
                    <span className="infra-facility-list__name">{supplier.name}</span>
                    <span className="infra-facility-list__kind">
                      {active
                        ? t("statistics.infrastructure.supplierActive")
                        : t("statistics.infrastructure.supplierInactive")}
                    </span>
                  </button>
                </li>
              );
            })}
          </ul>
        )}
      </div>
    </>
  );
}
