/**
 * Public service window component
 *
 * One tab per public service (products whose public_service is true in products_master), followed by the
 * logistics tab (LogisticsPane: trade hubs, freight stations and freight routes).
 *
 * Each tab has two columns:
 * - Top left: public service price controls, including method (fixed price / cost ratio), value
 *   (fixed price or cost multiplier), manufacturing cost, and charts for actual results,
 *   expected demand, and current facility capacity. Prices never fluctuate with the market;
 *   either fixed price or cost ratio always applies.
 * - Bottom left: table of facilities supplying the service and related high-pressure goods.
 * - Right: the same map as the city map infrastructure display. Supply facilities have workload
 *   pressure pie charts. Goods with service areas show those areas and building colors;
 *   goods whose suppliers receive infrastructure goods (electricity/water) also overlay the network.
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import { StatusBadge } from "../common/StatusBadge";
import { SortableAgentTable, type SortableAgentColumn } from "../common/SortableAgentTable";
import type {
  PublicFacilityAgent,
  PublicServicePrice,
  PublicServicePriceMode,
  PublicServiceSupply,
  PublicServiceSupplyData,
} from "../../types";
import { useEpochCachedStats } from "../statistics/useEpochCache";
import { CityMapCanvas, type CityMapLayerVisibility } from "../map/CityMapCanvas";
import { useInfraProducts, type InfraSelection } from "../map/infraOverlay";
import { InfraMapLegend } from "../map/InfraMapLegend";
import { PublicServiceTrendChart } from "./PublicServiceTrendChart";
import type { PublicServiceHistoryData, PublicServiceProductHistory } from "./publicServiceTrend";
import { getPublicServiceFacilities, getPublicServiceInfrastructure } from "./publicServiceInfrastructure";
import { LogisticsPane } from "./LogisticsPane";
import "./PublicFacilitiesWindow.css";

/** Tab key of the logistics tab, placed after the public service tabs (product ID strings never collide with it). */
const LOGISTICS_TAB = "logistics";

type PublicServicePricesData = {
  prices: PublicServicePrice[];
};

/** Public service tabs. Tab keys are product ID strings. */
type PublicServiceProduct = {
  id: number;
  display_name: string;
  unit: string;
};

type TFn2 = ReturnType<typeof useTranslation>["t"];

/** Display numbers with thousands separators. */
function num(v?: number | null, digits = 0): string {
  return v == null
    ? "—"
    : v.toLocaleString(undefined, {
        minimumFractionDigits: digits,
        maximumFractionDigits: digits,
      });
}

/** Round rates/costs to two decimal places; electricity (1 kWh) and water (1 L) may cost less than ¥1 per unit. */
function roundPrice(v: number): number {
  return Math.round(v * 100) / 100;
}

/** Display rates/costs with thousands separators (up to two decimal places for fractions below ¥1). */
function price(v: number): string {
  return roundPrice(v).toLocaleString(undefined, { maximumFractionDigits: 2 });
}

/** Format a proportion as a percentage. */
function pct(v?: number): string {
  return v == null ? "—" : `${Math.round(v * 100)}%`;
}

/**
 * Facility table column definitions.
 * The left price control card shows prices, production, and sales for the whole city,
 * so list only per-facility spare supply capacity (production capacity) and business indicators here.
 */
function buildColumns(t: TFn2, unit: string): SortableAgentColumn<PublicFacilityAgent>[] {
  return [
    {
      key: "status",
      label: t("agentList.colStatus"),
      align: "center",
      render: (a) => <StatusBadge status={a.status} />,
      sortValue: (a) => a.status,
    },
    {
      key: "name",
      label: t("agentList.colName"),
      render: (a) => a.name,
      sortValue: (a) => a.name,
    },
    {
      key: "production_capacity",
      label: `${t("publicFacilities.productionCapacity")} (${unit})`,
      align: "right",
      render: (a) => num(a.production_capacity, 0), // null = facility without a space constraint (no defined production limit).
      sortValue: (a) => a.production_capacity ?? 0,
    },
    {
      key: "balance",
      label: t("publicFacilities.balance"),
      align: "right",
      render: (a) => num(a.balance, 0),
      sortValue: (a) => a.balance,
    },
    {
      key: "utilization_rate",
      label: t("publicFacilities.utilizationRate"),
      align: "right",
      render: (a) => pct(a.utilization_rate),
      sortValue: (a) => a.utilization_rate,
    },
  ];
}

type ServicePriceCardProps = {
  product: PublicServiceProduct;
  row?: PublicServicePrice;
  supply?: PublicServiceSupply;
  infrastructureCapacity?: { label: string; value: number };
  capacityLabel?: string;
  epochs: number[];
  history?: PublicServiceProductHistory;
  currentEpoch: number;
  simulationDays: number;
  /** Draft string while editing the price/ratio input. Undefined if unedited. */
  draft?: string;
  onDraftChange: (value: string) => void;
  onModeChange: (mode: PublicServicePriceMode) => void;
  onCommit: () => void;
};

/**
 * Price control card for one product.
 * Shows pricing method, setting, manufacturing cost, and public service supply/demand trends.
 */
function ServicePriceCard({
  product,
  row,
  supply,
  infrastructureCapacity,
  capacityLabel,
  epochs,
  history,
  currentEpoch,
  simulationDays,
  draft,
  onDraftChange,
  onModeChange,
  onCommit,
}: ServicePriceCardProps) {
  const { t } = useTranslation();

  const mode: PublicServicePriceMode = row?.mode ?? "cost_ratio";
  const isRatio = mode === "cost_ratio";
  // Displayed input value: draft while editing; committed values use fixed=price (up to two decimals) / cost_ratio=ratio.
  const valueText =
    draft !== undefined
      ? draft
      : row
        ? isRatio
          ? String(row.value)
          : String(roundPrice(row.value))
        : "";

  return (
    <div className="psp-card">
      {row && (
        <div className="psp-card__fields">
          <div className="psp-field">
            <span className="psp-field__label">
              {t("publicServicePrices.priceMode")}
            </span>
            <select
              className="psp-row__mode"
              value={mode}
              onChange={(e) =>
                onModeChange(e.target.value as PublicServicePriceMode)
              }
            >
              <option value="fixed">{t("publicServicePrices.modeFixed")}</option>
              <option value="cost_ratio">
                {t("publicServicePrices.modeCostRatio")}
              </option>
            </select>
          </div>

          <div className="psp-field__control">
            {isRatio && <span>{t("publicServicePrices.costRatioPrefix")}</span>}
            <input
              type="number"
              className="psp-row__input"
              aria-label={t(isRatio ? "publicServicePrices.modeCostRatio" : "publicServicePrices.modeFixed")}
              value={valueText}
              min={0}
              step={isRatio ? 0.05 : "any"}
              onChange={(e) => onDraftChange(e.target.value)}
              onBlur={onCommit}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  onCommit();
                  (e.target as HTMLInputElement).blur();
                }
              }}
            />
            <span>{t(isRatio ? "publicServicePrices.costRatioSuffix" : "publicServicePrices.currencyUnit")}</span>
          </div>

          <div className="psp-card__cost">
            <span className="psp-field__label">
              {t("publicServicePrices.unitCost")}
            </span>
            <span className="psp-field__num">
              {price(row.unit_cost)}
              <span className="psp-row__unit">{t("publicServicePrices.currencyUnit")}/{product.unit}</span>
            </span>
          </div>
        </div>
      )}

      <PublicServiceTrendChart
        epochs={epochs}
        history={history}
        currentEpoch={currentEpoch}
        supply={supply}
        infrastructureCapacity={infrastructureCapacity}
        capacityLabel={capacityLabel}
        simulationDays={simulationDays}
        unit={product.unit}
      />
    </div>
  );
}

/**
 * Map background. Omit elevation coloring, tracks, stations, and labels;
 * labels overlap facility pie charts and become hard to read.
 */
const MAP_LAYERS: Partial<CityMapLayerVisibility> = {
  elevation: false,
  coastline: true,
  trunkRoads: true,
  allRoads: true,
  railways: false,
  buildings: true,
  stations: false,
  parcels: false,
  contours: false,
  grid: false,
  zoneBorders: false,
  labels: false,
};

export function PublicFacilitiesWindow({
  apiBaseUrl,
}: {
  apiBaseUrl: string;
}) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const marketHistory = useEpochCachedStats<PublicServiceHistoryData>(apiBaseUrl, "/api/v1/stats/market_price");
  const infraProducts = useInfraProducts(apiBaseUrl);
  const [simulationDays, setSimulationDays] = useState(30);

  const [activeTab, setActiveTab] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Public service prices. Store row state keyed by product_id.
  const [servicePrices, setServicePrices] = useState<
    Record<number, PublicServicePrice>
  >({});
  // Latest public service supply/demand and current facility capacity for horizontal chart lines.
  const [serviceSupplies, setServiceSupplies] = useState<
    Record<number, PublicServiceSupply>
  >({});
  // Draft strings in price/ratio text boxes (product_id -> input value).
  // In fixed mode, keep the fixed-price draft; in cost_ratio mode, keep the cost-multiplier draft.
  const [valueDrafts, setValueDrafts] = useState<Record<number, string>>({});

  useEffect(() => {
    const controller = new AbortController();

    async function loadData() {
      try {
        const [pricesRes, supplyRes, cityRes] = await Promise.all([
          fetchApi<PublicServicePricesData>(
            buildApiUrl(apiBaseUrl, "/api/v1/city/public-service-prices"),
            { signal: controller.signal },
          ),
          fetchApi<PublicServiceSupplyData>(
            buildApiUrl(apiBaseUrl, "/api/v1/city/public-service-supply?include_facilities=true"),
            { signal: controller.signal },
          ),
          fetchApi<{ city: { simulation_days: number } }>(
            buildApiUrl(apiBaseUrl, "/api/v1/city/"),
            { signal: controller.signal },
          ),
        ]);
        if (controller.signal.aborted) return;
        setSimulationDays(cityRes.city.simulation_days);
        const priceMap: Record<number, PublicServicePrice> = {};
        for (const row of pricesRes.prices) {
          priceMap[row.product_id] = row;
        }
        setServicePrices(priceMap);
        const supplyMap: Record<number, PublicServiceSupply> = {};
        for (const row of supplyRes.services) {
          supplyMap[row.product_id] = row;
        }
        setServiceSupplies(supplyMap);
      } catch (err) {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : "Unknown error");
        }
      } finally {
        setLoading(false);
      }
    }

    loadData();
    return () => controller.abort();
  }, [apiBaseUrl]);

  // Send updated public service pricing policy (mode + value) to the backend and update all rows from its response.
  const patchServicePrice = useCallback(
    async (productId: number, mode: PublicServicePriceMode, value: number) => {
      try {
        const res = await fetchApi<PublicServicePricesData>(
          buildApiUrl(apiBaseUrl, "/api/v1/city/public-service-prices"),
          {
            method: "PATCH",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              prices: { [String(productId)]: { mode, value } },
            }),
          },
        );
        const priceMap: Record<number, PublicServicePrice> = {};
        for (const row of res.prices) {
          priceMap[row.product_id] = row;
        }
        setServicePrices(priceMap);
        setValueDrafts((prev) => {
          const next = { ...prev };
          delete next[productId];
          return next;
        });
      } catch (err) {
        setError(err instanceof Error ? err.message : "Unknown error");
      }
    },
    [apiBaseUrl],
  );

  // Switch pricing method (fixed price / cost ratio).
  // Convert the current effective price into the new mode's setting to avoid a price jump on switching.
  //   - Switch to fixed price: new value = current effective price.
  //   - Switch to cost ratio: new value = current effective price / manufacturing cost (default ratio 1.0 if cost is 0).
  const handleModeChange = useCallback(
    (productId: number, nextMode: PublicServicePriceMode) => {
      const row = servicePrices[productId];
      if (!row) return;
      if (row.mode === nextMode) return;
      const effective =
        row.mode === "cost_ratio" ? row.unit_cost * row.value : row.value;
      let nextValue: number;
      if (nextMode === "fixed") {
        nextValue = Math.max(0, roundPrice(effective));
      } else {
        nextValue =
          row.unit_cost > 0
            ? Math.max(0, Math.round((effective / row.unit_cost) * 100) / 100)
            : 1.0;
      }
      patchServicePrice(productId, nextMode, nextValue);
    },
    [servicePrices, patchServicePrice],
  );

  // Commit the price/ratio text box (onBlur / Enter). Update only the value, retaining the current mode.
  const handleValueCommit = useCallback(
    (productId: number) => {
      const draft = valueDrafts[productId];
      if (draft === undefined) return;
      const parsed = Number(draft);
      const row = servicePrices[productId];
      if (!row || Number.isNaN(parsed)) {
        setValueDrafts((prev) => {
          const next = { ...prev };
          delete next[productId];
          return next;
        });
        return;
      }
      patchServicePrice(productId, row.mode, Math.max(0, parsed));
    },
    [valueDrafts, servicePrices, patchServicePrice],
  );

  // Public service product list. Extract master entries with public_service and use them directly as tabs.
  const publicServiceProducts = useMemo<PublicServiceProduct[]>(() => {
    if (!masterData?.products) return [];
    return Object.entries(masterData.products)
      .filter(([, entry]) => entry.public_service)
      .map(([id, entry]) => ({
        id: Number(id),
        display_name: entry.display_name,
        unit: entry.unit,
      }))
      .sort((a, b) => a.id - b.id);
  }, [masterData?.products]);

  // After data loads, automatically select the first tab if none is selected.
  useEffect(() => {
    if (activeTab === null && publicServiceProducts.length > 0) {
      setActiveTab(String(publicServiceProducts[0].id));
    }
  }, [activeTab, publicServiceProducts]);

  const activeProduct = useMemo(
    () => publicServiceProducts.find((p) => String(p.id) === activeTab) ?? null,
    [publicServiceProducts, activeTab],
  );

  const activeFacilities = useMemo(() => activeProduct ? getPublicServiceFacilities(activeProduct.id, serviceSupplies) : [],
    [activeProduct, serviceSupplies]);
  const infrastructure = activeProduct ? getPublicServiceInfrastructure(activeProduct.id) : undefined;
  const infrastructureSupply = infrastructure ? serviceSupplies[infrastructure.productId] : undefined;
  const infrastructureCapacity = infrastructure && infrastructureSupply ? {
    label: t(`publicServiceTrend.${infrastructure.infrastructureCapacityLabel}`),
    value: infrastructureSupply.supply_capacity * infrastructure.quantityScale,
  } : undefined;
  const columns = useMemo(() => buildColumns(t, activeProduct?.unit ?? ""), [t, activeProduct?.unit]);

  const coverageOption = activeProduct
    ? infraProducts.find((p) => p.kind === "coverage" && p.productId === activeProduct.id)
    : undefined;
  const feedProductId = coverageOption?.feedProductId ?? null;
  const mapSelection = useMemo<InfraSelection | null>(
    () => (activeProduct ? { network: feedProductId, coverage: activeProduct.id } : null),
    [activeProduct, feedProductId],
  );
  const hasCoverage = coverageOption !== undefined;

  if (loading || marketHistory.loading) {
    return <div className="pf-window__loading">{t("common.loading")}</div>;
  }

  const loadError = error ?? marketHistory.error;
  if (loadError) {
    return <div className="pf-window__error">{loadError}</div>;
  }

  return (
    <div className="pf-window">
      <nav className="pf-tabs">
        {publicServiceProducts.map((product) => (
          <button
            key={product.id}
            type="button"
            className={[
              "pf-tabs__tab",
              activeTab === String(product.id) && "pf-tabs__tab--active",
            ]
              .filter(Boolean)
              .join(" ")}
            onClick={() => setActiveTab(String(product.id))}
          >
            {product.display_name}
          </button>
        ))}
        <button
          type="button"
          className={["pf-tabs__tab", activeTab === LOGISTICS_TAB && "pf-tabs__tab--active"].filter(Boolean).join(" ")}
          onClick={() => setActiveTab(LOGISTICS_TAB)}
        >
          {t("publicFacilities.logisticsTab")}
        </button>
      </nav>

      {activeTab === LOGISTICS_TAB ? (
        <LogisticsPane apiBaseUrl={apiBaseUrl} />
      ) : (
        <div className="pf-window__content">
          <div className="pf-window__left">
            {activeProduct && (
              <div className="pf-window__service">
                <ServicePriceCard
                  product={activeProduct}
                  row={servicePrices[activeProduct.id]}
                  supply={serviceSupplies[activeProduct.id]}
                  infrastructureCapacity={infrastructureCapacity}
                  capacityLabel={infrastructure ? t(`publicServiceTrend.${infrastructure.capacityLabel}`) : undefined}
                  epochs={marketHistory.data?.epochs ?? []}
                  history={marketHistory.data?.products.find((row) => row.product_id === activeProduct.id)}
                  currentEpoch={marketHistory.data?.epoch ?? 0}
                  simulationDays={simulationDays}
                  draft={valueDrafts[activeProduct.id]}
                  onDraftChange={(value) =>
                    setValueDrafts((prev) => ({ ...prev, [activeProduct.id]: value }))
                  }
                  onModeChange={(mode) => handleModeChange(activeProduct.id, mode)}
                  onCommit={() => handleValueCommit(activeProduct.id)}
                />
              </div>
            )}

            <div className="pf-window__facilities">
              <SortableAgentTable
                columns={columns}
                rows={activeFacilities}
                emptyText={t("budget.noFacilities")}
              />
            </div>
          </div>

          <div className="pf-window__map-column">
            <div className="pf-window__map">
              <CityMapCanvas
                apiBaseUrl={apiBaseUrl}
                interactive
                layers={MAP_LAYERS}
                infraSelection={mapSelection}
              />
            </div>
            {hasCoverage && mapSelection && (
              <div className="pf-window__legend">
                <InfraMapLegend selection={mapSelection} />
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
