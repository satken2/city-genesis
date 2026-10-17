/**
 * 統計ウィンドウ「インフラ網」タブ
 *
 * 都市マップのインフラオーバーレイと同じ表示に、サマリーと施設のリストを並べる。財の選択肢と選択状態
 * (useInfraSelection)、地図の描画(CityMapCanvas の infraSelection)、凡例(InfraMapLegend)は都市マップと共通で、
 * 供給施設がインフラ財を受け取るサービス財(電力・水道など)は、そのインフラ財の網と効果範囲を重ねて描く。
 *
 * 画面構成:
 * - 上部: 財の切り替えボタン
 * - 左列: 地図が取得したデータ(onInfraLoaded)のサマリーと施設のリスト。網はインフラ財の生産量・配送量・需要量・
 *         充足率とインフラ施設(接続状態のドット付き)、効果範囲は稼働中の施設数・範囲内外の建物数と供給施設。
 *         網と効果範囲を重ねる財では両方を網から順に並べる。リストの行をクリックすると経済主体詳細ウィンドウを開く。
 * - 右列: 地図と凡例。下敷きは海岸線と道路のみ(標高色分けはしない)。効果範囲では建物を
 *         範囲内・範囲外で塗り分けるため建物も描く。
 */

import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { STATUS_ACTIVE } from "../../../constants";
import { useMasterData } from "../../../contexts/MasterDataContext";
import { CityMapCanvas, type CityMapLayerVisibility } from "../../map/CityMapCanvas";
import {
  useInfraSelection,
  type CityMapInfraData,
  type InfraProductStats,
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
  const infra = useInfraSelection(apiBaseUrl);
  const infraSelection = infra.option?.selection ?? null;
  const networkPid = infraSelection?.network ?? null;
  const coveragePid = infraSelection?.coverage ?? null;

  // サマリーは地図が取得したデータをそのまま使う(財を切り替えると取得し直すまで null)
  const [loaded, setLoaded] = useState<CityMapInfraData | null>(null);

  /** 製品の表示名(マスターデータから解決) */
  const productLabel = useCallback(
    (pid: number) => masterData?.products[pid]?.display_name ?? String(pid),
    [masterData]
  );

  // 下敷きは海岸線と道路のみ。効果範囲は建物の塗り分けを見せるため建物も描く。
  const layers = useMemo<Partial<CityMapLayerVisibility>>(() => ({
    elevation: false,
    coastline: true,
    trunkRoads: true,
    allRoads: true,
    railways: false,
    buildings: coveragePid !== null,
    stations: false,
    parcels: false,
    contours: false,
    grid: false,
    zoneBorders: false,
  }), [coveragePid]);

  if (infra.options.length === 0) {
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
        {infra.options.map((p) => (
          <button
            key={p.productId}
            type="button"
            className={[
              "infra-tab__product-btn",
              p.productId === infra.option?.productId && "infra-tab__product-btn--active",
            ]
              .filter(Boolean)
              .join(" ")}
            onClick={() => infra.select(p.productId)}
          >
            {productLabel(p.productId)}
          </button>
        ))}
      </div>

      <div className="infra-tab__grid">
        <div className="infra-tab__col-left">
          {networkPid !== null && (
            <NetworkSummary
              title={productLabel(networkPid)}
              unit={masterData?.products[networkPid]?.unit ?? ""}
              network={loaded?.network?.product ?? null}
            />
          )}
          {coveragePid !== null && (
            <CoverageSummary title={productLabel(coveragePid)} coverage={loaded?.coverage ?? null} />
          )}
        </div>

        <div className="infra-tab__col-right">
          <div className="infra-tab__map">
            <CityMapCanvas
              apiBaseUrl={apiBaseUrl}
              interactive
              layers={layers}
              infraSelection={infraSelection}
              onInfraLoaded={setLoaded}
            />
          </div>
          {infraSelection && (
            <div className="infra-tab__legend">
              <InfraMapLegend selection={infraSelection} />
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
  network: InfraProductStats | null;
}) {
  const { t } = useTranslation();
  if (!network) {
    return <div className="infra-card infra-card__empty">{t("common.loading")}</div>;
  }
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
