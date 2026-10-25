/**
 * Cargo information component
 *
 * Cargo section in the detail panel for import/export hubs (TYPE_TRADE).
 * After a capacity/utilization summary, displays allocations and actual throughput by item for
 * imports (sales to the city) and exports (purchases from the city) as horizontal bars.
 * Capacity and totals use weight (t); individual item charts use each item's own unit.
 *
 * Fetches from GET /api/v1/agents/{id}/cargo because item data is split between SoA and row-store
 * and absent from the single_agent schema. Double bars use pale for this epoch's allocation and
 * dark for actual throughput (imports: sales to the city; exports: purchases from the city), matching FloorOccupancyBar.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import type { AgentData, MasterData } from "../../types";
import { useMasterData } from "../../contexts/MasterDataContext";
import { useEpochPeriodUnit } from "../../hooks/useEpochPeriodUnit";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { DataList } from "../common/DataList";
import "./CargoInfoPanel.css";

type CargoProduct = {
  product_id: number;
  allocation: number;
  handled: number;
  price: number;
  lost?: number;
};

type CargoInfoResponse = {
  /** Capacity by direction (t/epoch). Hubs with raw materials use the smaller of the area limit and received-material capacity. */
  capacity_per_epoch: number;
  /** Capacity determined solely by floor area (t/epoch). */
  area_capacity_per_epoch: number;
  /** Throughput supported by received raw materials (t/epoch). For international freight stations, this is capacity delivered by international freight lines. Null for ports and others without raw materials. */
  material_capacity_per_epoch: number | null;
  /** Total actual throughput by direction (t). Item units differ, so receive the total in tonnes for comparison with capacity. */
  import_handled_tons: number;
  export_handled_tons: number;
  imports: CargoProduct[];
  exports: CargoProduct[];
};

type CargoInfoPanelProps = {
  agent: AgentData;
  apiBaseUrl: string;
};

/** Throughput formatting (thousands separators and up to one decimal place). */
function formatAmount(value: number): string {
  return value.toLocaleString(undefined, { maximumFractionDigits: 1 });
}

type CargoDirectionSectionProps = {
  direction: "import" | "export";
  products: CargoProduct[];
  masterData: MasterData | null;
};

/**
 * Item chart for one direction, either imports or exports.
 * The wrapper class cargo--import / cargo--export selects blue for imports or green for exports.
 */
function CargoDirectionSection({ direction, products, masterData }: CargoDirectionSectionProps) {
  const { t } = useTranslation();
  const isImport = direction === "import";

  // Scale the bars to the largest value across all items (the larger of allocation and actual throughput).
  // Units differ by item, so this is not a strict quantitative comparison;
  // it is a relative display intended to show intuitively which items dominate this hub's throughput.
  const maxValue = products.reduce(
    (max, p) => Math.max(max, p.allocation, p.handled),
    0,
  );

  // Sort actual throughput descending (then allocation descending on ties) to put leading items first.
  const rows = [...products].sort(
    (a, b) => b.handled - a.handled || b.allocation - a.allocation,
  );

  const handledLabel = isImport
    ? t("cargo.handledImport", "販売量")
    : t("cargo.handledExport", "買取量");

  return (
    <div className={`cargo--${direction}`}>
      <h4 className="cargo-products-title">
        {isImport
          ? t("cargo.importSectionTitle", "輸入")
          : t("cargo.exportSectionTitle", "輸出")}
      </h4>

      {rows.length === 0 ? (
        <p className="agent-detail-muted">{t("cargo.noProducts", "取扱品目がありません")}</p>
      ) : (
        <>
          <div className="cargo-legend">
            <span className="cargo-legend__item">
              <span className="cargo-legend__swatch cargo-legend__swatch--handled" />
              {handledLabel}
            </span>
            <span className="cargo-legend__item">
              <span className="cargo-legend__swatch cargo-legend__swatch--allocation" />
              {t("cargo.allocation", "割当量")}
            </span>
          </div>

          <div className="cargo-rows">
            {rows.map((row) => {
              const product = masterData?.products[row.product_id];
              const displayName = product?.display_name ?? String(row.product_id);
              const unit = product?.unit ?? "";
              const allocPct = maxValue > 0 ? (row.allocation / maxValue) * 100 : 0;
              const handledPct =
                row.allocation > 0 ? Math.min((row.handled / row.allocation) * 100, 100) : 0;

              return (
                <div key={row.product_id} className="cargo-row">
                  <div className="cargo-row__head">
                    <span className="cargo-row__name">{displayName}</span>
                    <span className="cargo-row__values">
                      {`${formatAmount(row.handled)} / ${formatAmount(row.allocation)} ${unit}`}
                    </span>
                  </div>
                  <div className="cargo-row__track">
                    <div className="cargo-row__allocation" style={{ width: `${allocPct}%` }}>
                      {row.handled > 0 && (
                        <div className="cargo-row__handled" style={{ width: `${handledPct}%` }} />
                      )}
                    </div>
                  </div>
                  <div className="cargo-row__sub">
                    <span>
                      {isImport
                        ? t("cargo.priceImport", "販売単価")
                        : t("cargo.priceExport", "買取単価")}
                      {": "}
                      {t("common.currency", { value: row.price.toFixed(2) })}
                    </span>
                    {(row.lost ?? 0) > 0 && (
                      <span className="cargo-row__lost">
                        {t("cargo.lost", "売り逃し")}
                        {": "}
                        {`${formatAmount(row.lost ?? 0)} ${unit}`}
                      </span>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        </>
      )}
    </div>
  );
}

export function CargoInfoPanel({ agent, apiBaseUrl }: CargoInfoPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const [cargo, setCargo] = useState<CargoInfoResponse | null>(null);
  const [error, setError] = useState(false);
  const periodUnit = useEpochPeriodUnit(apiBaseUrl);

  // The agent reference changes whenever the detail panel refetches the agent,
  // so refetch cargo information as well to follow values updated after an epoch.
  useEffect(() => {
    const controller = new AbortController();
    setError(false);
    const url = buildApiUrl(apiBaseUrl, `/api/v1/agents/${agent.agent_id}/cargo`);
    fetchApi<CargoInfoResponse>(url, { signal: controller.signal })
      .then((res) => {
        if (!controller.signal.aborted) setCargo(res);
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          console.error("Failed to fetch cargo info:", err);
          setError(true);
        }
      });
    return () => controller.abort();
  }, [apiBaseUrl, agent]);

  if (error) {
    return (
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">{t("cargo.title", "荷役情報")}</h3>
        <p className="agent-detail-muted">{t("common.fetchError")}</p>
      </section>
    );
  }

  if (!cargo) {
    return (
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">{t("cargo.title", "荷役情報")}</h3>
        <p className="agent-detail-muted">{t("common.loading")}</p>
      </section>
    );
  }


  return (
    <section className="agent-detail-section cargo">
      <h3 className="agent-detail-section-title">{t("cargo.title", "荷役情報")}</h3>

      <div className="agent-detail-grid">
        <span className="agent-detail-label">{t("cargo.capacity", "取扱能力")}</span>
        <span className="agent-detail-value">
          {formatAmount(cargo.capacity_per_epoch)} t
          <span className="cargo-unit-suffix">
            {t("cargo.perPeriod", { context: periodUnit })}
          </span>
        </span>

        {cargo.material_capacity_per_epoch != null && (
          <>
            <span className="agent-detail-label">{t("cargo.materialCapacity", "原材料による上限")}</span>
            <span className="agent-detail-value">{formatAmount(cargo.material_capacity_per_epoch)} t</span>

            <span className="agent-detail-label">{t("cargo.areaCapacity", "面積による上限")}</span>
            <span className="agent-detail-value">{formatAmount(cargo.area_capacity_per_epoch)} t</span>
          </>
        )}

        <span className="agent-detail-label">{t("cargo.importHandled", "輸入取扱量")}</span>
        <span className="agent-detail-value">{formatAmount(cargo.import_handled_tons)} t</span>

        <span className="agent-detail-label">{t("cargo.exportHandled", "輸出取扱量")}</span>
        <span className="agent-detail-value">{formatAmount(cargo.export_handled_tons)} t</span>

        {agent.utilization != null && (
          <>
            <span className="agent-detail-label">{t("cargo.utilization", "稼働率")}</span>
            <span className="agent-detail-value">
              <DataList
                values={agent.utilization}
                formatter={(v) => `${(v * 100).toFixed(1)}%`}
                clampRange={[0, 1]}
              />
            </span>
          </>
        )}
      </div>

      <CargoDirectionSection direction="import" products={cargo.imports} masterData={masterData} />
      <CargoDirectionSection direction="export" products={cargo.exports} masterData={masterData} />
    </section>
  );
}
