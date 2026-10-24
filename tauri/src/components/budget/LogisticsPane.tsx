/**
 * Logistics tab of the public facility window.
 *
 * - Left: trade hubs (handling capacity per direction and this epoch's handled tonnage) and the stations/terminals
 *   that send or receive freight infrastructure goods (production capacity, sales, workload pressure).
 * - Right: the city map with the selected freight good's network (international freight / ore / crude oil routes),
 *   drawn by the same infrastructure overlay as the city map (paths colored by flow / capacity).
 * The core decides which goods are freight (GET /api/v1/stats/logistics freight_product_ids).
 */
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMasterData } from "../../contexts/MasterDataContext";
import { StatusBadge } from "../common/StatusBadge";
import { SortableAgentTable, type SortableAgentColumn } from "../common/SortableAgentTable";
import { useEpochCachedStats } from "../statistics/useEpochCache";
import { CityMapCanvas, type CityMapLayerVisibility } from "../map/CityMapCanvas";
import { InfraMapLegend } from "../map/InfraMapLegend";
import type { InfraSelection } from "../map/infraOverlay";

type LogisticsHub = {
  agent_id: number;
  name: string;
  variant: number;
  status: number;
  capacity: number;
  import_handled: number;
  export_handled: number;
  utilization: number;
};

type LogisticsStation = {
  agent_id: number;
  name: string;
  variant: number;
  status: number;
  supply_product_id: number;
  /** null = no space constraint (no defined production limit). */
  production_capacity: number | null;
  sold: number;
  load: number;
};

type LogisticsData = {
  epoch: number;
  simulation_days: number;
  freight_product_ids: number[];
  hubs: LogisticsHub[];
  stations: LogisticsStation[];
};

type TFn = ReturnType<typeof useTranslation>["t"];
type MasterData = ReturnType<typeof useMasterData>;

/** Freight routes run on rails, so show railways and stations under the network overlay. */
const MAP_LAYERS: Partial<CityMapLayerVisibility> = {
  elevation: false,
  coastline: true,
  trunkRoads: true,
  allRoads: true,
  railways: true,
  buildings: true,
  stations: true,
  parcels: false,
  contours: false,
  grid: false,
  zoneBorders: false,
  labels: false,
};

function num(v: number | null | undefined): string {
  return v == null ? "—" : Math.round(v).toLocaleString();
}

function pct(v: number): string {
  return `${Math.round(v * 100)}%`;
}

function variantName(masterData: MasterData, variant: number): string {
  return masterData?.agents?.[variant]?.display_name ?? String(variant);
}

function hubColumns(t: TFn, masterData: MasterData): SortableAgentColumn<LogisticsHub>[] {
  return [
    { key: "status", label: t("agentList.colStatus"), align: "center", render: (h) => <StatusBadge status={h.status} />, sortValue: (h) => h.status },
    { key: "name", label: t("agentList.colName"), render: (h) => h.name, sortValue: (h) => h.name },
    { key: "variant", label: t("agentList.colVariant"), render: (h) => variantName(masterData, h.variant), sortValue: (h) => h.variant },
    { key: "capacity", label: `${t("cargo.capacity")} (t)`, align: "right", render: (h) => num(h.capacity), sortValue: (h) => h.capacity },
    { key: "import", label: `${t("cargo.importHandled")} (t)`, align: "right", render: (h) => num(h.import_handled), sortValue: (h) => h.import_handled },
    { key: "export", label: `${t("cargo.exportHandled")} (t)`, align: "right", render: (h) => num(h.export_handled), sortValue: (h) => h.export_handled },
    { key: "utilization", label: t("cargo.utilization"), align: "right", render: (h) => pct(h.utilization), sortValue: (h) => h.utilization },
  ];
}

function stationColumns(t: TFn, masterData: MasterData): SortableAgentColumn<LogisticsStation>[] {
  const unit = (s: LogisticsStation) => masterData?.products?.[s.supply_product_id]?.unit ?? "";
  return [
    { key: "status", label: t("agentList.colStatus"), align: "center", render: (s) => <StatusBadge status={s.status} />, sortValue: (s) => s.status },
    { key: "name", label: t("agentList.colName"), render: (s) => s.name, sortValue: (s) => s.name },
    { key: "variant", label: t("agentList.colVariant"), render: (s) => variantName(masterData, s.variant), sortValue: (s) => s.variant },
    {
      key: "capacity",
      label: t("publicFacilities.productionCapacity"),
      align: "right",
      render: (s) => `${num(s.production_capacity)} ${unit(s)}`,
      sortValue: (s) => s.production_capacity ?? 0,
    },
    { key: "sold", label: t("cargo.handledImport"), align: "right", render: (s) => `${num(s.sold)} ${unit(s)}`, sortValue: (s) => s.sold },
    { key: "load", label: t("statistics.columnUsage"), align: "right", render: (s) => pct(s.load), sortValue: (s) => s.load },
  ];
}

export function LogisticsPane({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const logistics = useEpochCachedStats<LogisticsData>(apiBaseUrl, "/api/v1/stats/logistics");
  const [routeProductId, setRouteProductId] = useState<number | null>(null);

  const freightProductIds = logistics.data?.freight_product_ids ?? [];
  const selectedRoute = routeProductId ?? freightProductIds[0] ?? null;
  const mapSelection = useMemo<InfraSelection | null>(
    () => (selectedRoute === null ? null : { network: selectedRoute, coverage: null }),
    [selectedRoute],
  );
  const hubCols = useMemo(() => hubColumns(t, masterData), [t, masterData]);
  const stationCols = useMemo(() => stationColumns(t, masterData), [t, masterData]);

  if (logistics.loading && !logistics.data) {
    return <div className="pf-window__loading">{t("common.loading")}</div>;
  }
  if (logistics.error) {
    return <div className="pf-window__error">{logistics.error}</div>;
  }

  return (
    <div className="pf-window__content">
      <div className="pf-window__left">
        <h3 className="lg-pane__title">{t("publicFacilities.hubs")}</h3>
        <div className="pf-window__facilities">
          <SortableAgentTable columns={hubCols} rows={logistics.data?.hubs ?? []} emptyText={t("budget.noFacilities")} />
        </div>
        <h3 className="lg-pane__title">{t("publicFacilities.stations")}</h3>
        <div className="pf-window__facilities">
          <SortableAgentTable columns={stationCols} rows={logistics.data?.stations ?? []} emptyText={t("budget.noFacilities")} />
        </div>
      </div>

      <div className="pf-window__map-column">
        <div className="lg-pane__routes">
          <span>{t("publicFacilities.routes")}</span>
          {freightProductIds.map((pid) => (
            <button
              key={pid}
              type="button"
              className={["pf-tabs__tab", pid === selectedRoute && "pf-tabs__tab--active"].filter(Boolean).join(" ")}
              onClick={() => setRouteProductId(pid)}
            >
              {masterData?.products?.[pid]?.display_name ?? pid}
            </button>
          ))}
        </div>
        <div className="pf-window__map">
          <CityMapCanvas apiBaseUrl={apiBaseUrl} interactive layers={MAP_LAYERS} infraSelection={mapSelection} />
        </div>
        {mapSelection && (
          <div className="pf-window__legend">
            <InfraMapLegend selection={mapSelection} />
          </div>
        )}
      </div>
    </div>
  );
}
