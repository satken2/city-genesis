/**
 * Line detail panel
 *
 * Dedicated popup on the screen's right (window_type="transit_detail"). Handles one public
 * transport line with the same right-panel styling/tab layout as AgentDetailPanel.
 *
 * Tabs:
 * - Basic information: type, vehicle appearance, status, vehicles, trains, section count,
 *   and operating results (boardings, balance, crowding, procurement fulfillment).
 * - Settings: fare, headway, cars per train, line color, vehicle appearance (saved by PATCH),
 *   route editing, and closure. Below fare/headway/car count, show TransitEstimateCard using
 *   edited values. Route point order is edited in transit_creation opened in route-edit mode.
 * - Line map: TransitLineMap on a real map, focusing the shared map's public transport layer on this line.
 * - Line diagram: vertical strip diagram (TransitLineDiagram).
 *
 * Place the supply diagram first in basic information and suspension/vehicle shortage warnings
 * below the header. Fetch via GET /api/v1/transit/lines/{id}, save with PATCH, close with DELETE.
 * After save/delete, emit transit:lines_changed to refresh the public transport window; also refetch
 * on this notification after route edits. Unity's shared line data refreshes periodically,
 * while preserving player display settings.
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";
import { buildApiUrl } from "../../utils/api";
import { InfoHeader } from "../common/InfoHeader";
import { NumberStepper } from "../common/NumberStepper";
import { ColorSwatchPicker } from "../common/ColorSwatchPicker";
import { Meter } from "../common/Meter";
import { resolveTransitIcon } from "../../utils/pathIcon";
import { STATUS_ACTIVE } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import { TRANSIT_LINE_COLOR_PRESETS, resolveTransitColor } from "../../local-constants";
import { TransitLineMap } from "./TransitLineMap";
import { TransitLineDiagram } from "./TransitLineDiagram";
import { TransitFleetSection } from "./TransitFleetSection";
import { TransitVehicleLabel, TransitVehicleSelector, transitVehicleOptions } from "./TransitVehicleSelector";
import { pauseReasonKey } from "./transitPauseReason";
import { TransitEstimateCard } from "./TransitEstimateCard";
import { useTransitEstimate } from "../../hooks/useTransitEstimate";
import { SupplyChainSection } from "../supply/SupplyChainSection";
import { useSupplyGraph } from "../../hooks/useSupplyGraph";
// Share right-panel styling (sections, label/value grids, tabs) with the agent detail panel.
import "../agent/AgentDetailPanel.css";
// Operation status badges (.transit-badge) are shared with the public transport window list.
import "./TransitManagementWindow.css";
import "./TransitDetailPanel.css";

type TransitSegment = {
  path_id: number;
  name: string | null;
  status: number;
};

/**
 * Stop node: the unit drawn as a station in the line diagram. List each once in traversal order
 * (first visit for stops visited twice per loop); order is this sequence.
 * name resolves from the underlying path (station name for a station building, road name for streets).
 * Unnamed/default stops use null; UI supplies sequential labels. house_path_id identifies
 * the station building path supplying the station name.
 * ridership is daily boardings from that stop in the previous epoch, null before any record
 * (before first simulation or for stops recreated by route editing).
 */
export type TransitStop = {
  node_id: number;
  order: number;
  /** World coordinates [x, y, z]. UI treats them as the X-Z plane. */
  position: [number, number, number];
  name: string | null;
  station_path_id: number | null;
  house_path_id: number | null;
  ridership: number | null;
};

/**
 * Line visitation points (stops/waypoints in order, wrapping from last to first).
 * turnback means vehicles reverse at that stop (round-trip ends; false while suspended).
 * Waypoint node_id is null.
 */
export type TransitRoutePointSummary = {
  kind: "stop" | "waypoint";
  position: [number, number, number];
  node_id: number | null;
  turnback: boolean;
};

/**
 * One line section (PATH_TRANSPORT between consecutive stops), drawn as a line in the diagram.
 * Section paths are one-way along traversal order: from_node_id → to_node_id with
 * travel_direction DIR_FORWARD, independent of underlying track traffic direction.
 * The diagram uses status (dashed for suspended sections) and utilization (section crowding,
 * 0-1 from the previous epoch).
 */
export type TransitEdge = {
  path_id: number;
  from_node_id: number;
  to_node_id: number;
  travel_direction: number;
  status: number;
  utilization: number;
};

type TransitLineDetail = {
  line_agent_id: number;
  name: string | null;
  path_variant: number;
  status: number;
  fare_price: number;
  target_headway: number;
  // Cars per train (player setting) and train count (=ceil(loop time/headway), derived by backend;
  // 0 before the first simulation).
  cars_per_train: number;
  train_count: number;
  // Line color ("#rrggbb"). Empty means unset, falling back to the type's default color.
  line_color: string;
  vehicle_model: number; // Vehicle appearance number (visual only; choices from TransitVehicleSelector's catalog).
  vehicle_count: number;
  required_vehicle_count: number;
  revenues: number;
  expenses: number;
  ridership: number; // Daily boardings.
  max_utilization: number; // Crowding (utilization of the busiest section).
  path_count: number;
  // Suspension reason code (null / "route_disconnected" / "no_underlay" / "train_too_long").
  pause_reason: string | null;
  segments: TransitSegment[];
  // Stops (each once in traversal order), visitation points, and section edges for the diagram, from GET /api/v1/transit/lines/{id}.
  stops: TransitStop[];
  route_points: TransitRoutePointSummary[];
  edges: TransitEdge[];
};

type TabId = "basic" | "settings" | "map" | "diagram";

type TransitDetailPanelProps = {
  apiBaseUrl: string;
  pathTemplate: string;
  initialEntityId: string;
};

/** Resolve line color, using type defaults for unset (empty) colors. */
function effectiveLineColor(line: TransitLineDetail): string {
  return line.line_color && line.line_color !== "" ? line.line_color : resolveTransitColor(line.path_variant);
}

export function TransitDetailPanel({
  apiBaseUrl,
  pathTemplate,
  initialEntityId,
}: TransitDetailPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const [line, setLine] = useState<TransitLineDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const [activeTab, setActiveTab] = useState<TabId>("basic");
  const supplyGraph = useSupplyGraph(apiBaseUrl, initialEntityId, true);

  // Draft fare, headway, cars per train, line color, and vehicle appearance.
  const [fare, setFare] = useState<number>(0);
  const [headway, setHeadway] = useState<number>(1);
  const [carsPerTrain, setCarsPerTrain] = useState<number>(1);
  const [lineColor, setLineColor] = useState<string>("");
  const [vehicleModel, setVehicleModel] = useState<number>(0);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [deleting, setDeleting] = useState(false);
  // Two-step closure confirmation: first click shows confirmation row, second executes.
  const [confirmDelete, setConfirmDelete] = useState(false);

  const url = buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    fetch(url, { signal: controller.signal })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<TransitLineDetail>;
      })
      .then((data) => {
        if (controller.signal.aborted) return;
        setLine(data);
        setFare(Math.round(data.fare_price));
        setHeadway(Math.round(data.target_headway));
        setCarsPerTrain(Math.max(1, Math.round(data.cars_per_train ?? 1)));
        setLineColor(effectiveLineColor(data));
        setVehicleModel(data.vehicle_model ?? 0);
        setLoading(false);
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : "Unknown error");
          setLoading(false);
        }
      });
    return () => controller.abort();
  }, [url, refreshKey]);

  // Refetch when other windows (route editing panel, etc.) change the line.
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    listen("transit:lines_changed", () => setRefreshKey((k) => k + 1))
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);
    return () => {
      unlisten?.();
    };
  }, []);

  /** Open the creation panel in route-edit mode to edit this line's stops/waypoints on the 3D map. */
  const openRouteEditor = useCallback(() => {
    if (!line) return;
    void invoke("open_registered_window", {
      windowType: "transit_creation",
      params: { extra: { editLineId: line.line_agent_id, titleKey: "windowType.transit_route_edit" } },
    });
  }, [line]);

  const isDirty =
    line != null &&
    (fare !== Math.round(line.fare_price) ||
      headway !== Math.round(line.target_headway) ||
      carsPerTrain !== Math.max(1, Math.round(line.cars_per_train ?? 1)) ||
      lineColor !== effectiveLineColor(line) ||
      vehicleModel !== (line.vehicle_model ?? 0));

  const resetEdits = useCallback(() => {
    if (!line) return;
    setFare(Math.round(line.fare_price));
    setHeadway(Math.round(line.target_headway));
    setCarsPerTrain(Math.max(1, Math.round(line.cars_per_train ?? 1)));
    setLineColor(effectiveLineColor(line));
    setVehicleModel(line.vehicle_model ?? 0);
    setSaveError(null);
  }, [line]);

  const handleSave = useCallback(async () => {
    if (!line) return;
    setSaving(true);
    setSaveError(null);
    try {
      const res = await fetch(
        buildApiUrl(apiBaseUrl, `/api/v1/transit/lines/${line.line_agent_id}`),
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            fare_price: fare,
            target_headway: headway,
            cars_per_train: carsPerTrain,
            line_color: lineColor,
            vehicle_model: vehicleModel,
          }),
        },
      );
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      await emit("transit:lines_changed", {});
      setRefreshKey((k) => k + 1);
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : "Failed to save");
    } finally {
      setSaving(false);
    }
  }, [apiBaseUrl, line, fare, headway, carsPerTrain, lineColor, vehicleModel]);

  const handleDelete = useCallback(async () => {
    if (!line) return;
    setDeleting(true);
    try {
      const res = await fetch(
        buildApiUrl(apiBaseUrl, `/api/v1/transit/lines/${line.line_agent_id}`),
        { method: "DELETE" },
      );
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      await emit("transit:lines_changed", {});
      await invoke("close_current_window");
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : "Failed to delete");
      setDeleting(false);
      setConfirmDelete(false);
    }
  }, [apiBaseUrl, line]);

  // Only while the settings tab is open, refetch profitability with draft values before saving to avoid discovering poor results afterward.
  const { estimate, loading: estimateLoading, error: estimateError } = useTransitEstimate(
    apiBaseUrl,
    line != null && activeTab === "settings"
      ? {
          kind: "line",
          lineAgentId: line.line_agent_id,
          farePrice: fare,
          targetHeadway: headway,
          carsPerTrain,
        }
      : null,
    refreshKey,
  );

  if (loading) {
    return <p className="agent-detail-muted">{t("common.loading")}</p>;
  }
  if (error || !line) {
    return (
      <div className="agent-detail-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  // Use the path_master variant's display name as the type name.
  const variantName = masterData?.paths[line.path_variant]?.display_name ?? "";
  const displayName = line.name && line.name !== "" ? line.name : `#${line.line_agent_id}`;
  // Determine operating state from the line agent's status, matching the header status display.
  // Show vehicle shortages through basic information's procured/required counts and the operating-result procurement meter.
  const running = line.status === STATUS_ACTIVE;
  // Display suspension reasons. Missing/unknown reason codes (e.g. vehicle shortage) use generic text.
  const pauseReasonText = running ? null : t(pauseReasonKey(line.pause_reason));
  const procurementRatio =
    line.required_vehicle_count > 0
      ? line.vehicle_count / line.required_vehicle_count
      : null;
  const vehicleShortage = running && procurementRatio !== null && procurementRatio < 1;
  const savedColor = effectiveLineColor(line);
  const balance = line.revenues - line.expenses;
  // Indicate in the heading when estimates use edited values rather than saved settings (line color/appearance do not affect profitability).
  const estimateEdited =
    fare !== Math.round(line.fare_price) ||
    headway !== Math.round(line.target_headway) ||
    carsPerTrain !== Math.max(1, Math.round(line.cars_per_train ?? 1));
  // For types without appearance options (not cataloged), omit the entire appearance row.
  const hasVehicleOptions = transitVehicleOptions(line.path_variant).length > 0;
  const freight = Object.values(masterData?.transit ?? {}).some(
    (entry) => entry.transit_path_id === line.path_variant && entry.freight,
  );
  // Types that carry cars (ferries) charge no fare, so omit the fare field.
  const freeFare = Object.values(masterData?.transit ?? {}).some(
    (entry) => entry.transit_path_id === line.path_variant && (entry.car_capacity ?? 0) > 0,
  );

  const tabs: { id: TabId; label: string }[] = [
    { id: "basic", label: t("transit.tabBasicInfo") },
    { id: "settings", label: t("transit.tabSettings") },
    { id: "map", label: t("transit.tabLineMap") },
    { id: "diagram", label: t("transit.tabLineDiagram") },
  ];

  return (
    <div className="agent-detail">
      <InfoHeader
        iconSrc={resolveTransitIcon(line.path_variant)}
        status={line.status}
        variantName={variantName}
        name={displayName}
      />
      {/*
 * Line color band directly below the header, visible on every tab.
 * Fade it while suspended to reinforce status.
 */}
      <div
        className={`transit-detail__stripe${running ? "" : " transit-detail__stripe--suspended"}`}
        style={{ background: savedColor }}
        aria-hidden="true"
      />

      {(pauseReasonText || vehicleShortage) && <div className="transit-detail__notice" role="status">
        <span className="transit-detail__notice-text">{pauseReasonText ?? t("transit.vehicleShortage")}</span>
      </div>}

      <div className="agent-detail-tabs">
        {tabs.map((tab) => (
          <button
            key={tab.id}
            type="button"
            className={[
              "agent-detail-tab",
              tab.id === activeTab && "agent-detail-tab--active",
            ]
              .filter(Boolean)
              .join(" ")}
            onClick={() => setActiveTab(tab.id)}
          >
            {tab.label}
            {/* Use a dot to indicate unsaved settings even when another tab is open. */}
            {tab.id === "settings" && isDirty && <span className="transit-detail__tab-dot" />}
          </button>
        ))}
      </div>

      <div className="agent-detail-tab-content">
        {activeTab === "basic" && (
          <>
            <SupplyChainSection {...supplyGraph} />
            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">{t("transit.basicInfo")}</h3>
              <div className="agent-detail-grid">
                <span className="agent-detail-label">{t("transit.colName")}</span>
                <span className="agent-detail-value">{variantName}</span>
                {hasVehicleOptions && (
                  <>
                    <span className="agent-detail-label">{t("transit.vehicleModel")}</span>
                    <span className="agent-detail-value">
                      <TransitVehicleLabel pathVariant={line.path_variant} value={line.vehicle_model ?? 0} />
                    </span>
                  </>
                )}
                <span className="agent-detail-label">{t("transit.status")}</span>
                <span className="agent-detail-value">
                  <span className={`transit-badge ${running ? "transit-badge--running" : "transit-badge--suspended"}`}>
                    {running ? t("transit.running") : t("transit.suspended")}
                  </span>
                </span>
                <span className="agent-detail-label">{t("transit.colVehicles")}</span>
                <span className="agent-detail-value">
                  {procurementRatio === null
                    ? Math.round(line.vehicle_count)
                    : `${Math.round(line.vehicle_count)} / ${Math.round(line.required_vehicle_count)}`}
                  <span className="transit-detail__unit">{t("transit.carsUnit")}</span>
                </span>
                {/* Train count is read-only, derived by the backend from headway and line length. */}
                <span className="agent-detail-label">{t("transit.trainCount")}</span>
                <span className="agent-detail-value">
                  {Math.round(line.train_count ?? 0).toLocaleString()}
                  <span className="transit-detail__unit">{t("transit.trainsUnit")}</span>
                </span>
                <span className="agent-detail-label">{t("transit.stopsCount")}</span>
                <span className="agent-detail-value">{line.stops.length}</span>
                <span className="agent-detail-label">{t("transit.stopCount")}</span>
                <span className="agent-detail-value">{line.path_count}</span>
              </div>
            </section>

            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">{t("transit.performance")}</h3>
              {/* Previous epoch results as tiles. Only balance changes color by sign. */}
              <div className="transit-detail__tiles">
                <StatTile label={t("transit.colRidership")} value={Math.round(line.ridership).toLocaleString()} />
                <StatTile
                  label={t("transit.colRevenues")}
                  value={Math.round(line.revenues).toLocaleString()}
                  unit={t("transit.currencyUnit")}
                />
                <StatTile
                  label={t("transit.vehicleCost")}
                  value={Math.round(line.expenses).toLocaleString()}
                  unit={t("transit.currencyUnit")}
                />
                <StatTile
                  label={t("transit.balance")}
                  value={`${balance > 0 ? "+" : ""}${Math.round(balance).toLocaleString()}`}
                  unit={t("transit.currencyUnit")}
                  tone={balance < 0 ? "negative" : balance > 0 ? "positive" : undefined}
                />
              </div>
              <div className="agent-detail-grid" style={{ alignItems: "center" }}>
                {/*
 * Higher crowding is worse; higher vehicle procurement fulfillment is better.
 * Use the shared meter's polarity to reverse color mapping.
 */}
                <span className="agent-detail-label">{t("transit.colUtilization")}</span>
                <span className="agent-detail-value transit-detail__meter">
                  <Meter value={line.max_utilization} polarity="negative" />
                  <span className="transit-detail__meter-value">{formatPercent(line.max_utilization)}</span>
                </span>
                <span className="agent-detail-label">{t("transit.vehicleProcurement")}</span>
                <span className="agent-detail-value transit-detail__meter">
                  {procurementRatio === null ? (
                    "—"
                  ) : (
                    <>
                      <Meter value={procurementRatio} />
                      <span className="transit-detail__meter-value">{formatPercent(procurementRatio)}</span>
                    </>
                  )}
                </span>
              </div>
            </section>

            <TransitFleetSection
              carsPerTrain={line.cars_per_train}
              vehicleCount={line.vehicle_count}
              requiredTrains={line.train_count}
              color={savedColor}
            />
          </>
        )}

        {activeTab === "settings" && (
          <>
            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">{t("transit.tabSettings")}</h3>
              <div className="agent-detail-grid transit-detail__settings-grid">
                {!freeFare && (
                  <>
                    <span className="agent-detail-label">{t("transit.fare")}</span>
                    <span className="agent-detail-value">
                      <NumberStepper value={fare} onChange={setFare} min={0} max={100000} step={10} integerOnly={true} />
                      <span className="transit-detail__unit">{t("transit.currencyUnit")}</span>
                    </span>
                  </>
                )}
                <span className="agent-detail-label">{t("transit.headway")}</span>
                <span className="agent-detail-value">
                  <NumberStepper value={headway} onChange={setHeadway} min={1} max={120} step={1} integerOnly={true} />
                  <span className="transit-detail__unit">{t("transit.minutesUnit")}</span>
                </span>
                <span className="agent-detail-label">{t("transit.carsPerTrain")}</span>
                <span className="agent-detail-value">
                  <NumberStepper value={carsPerTrain} onChange={setCarsPerTrain} min={freight ? 2 : 1} max={20} step={1} integerOnly={true} />
                  <span className="transit-detail__unit">{t("transit.carsUnit")}</span>
                </span>
                {/* Only the three fields above affect profitability; re-estimate directly below them on every change. */}
                <div className="transit-detail__estimate">
                  <div className="transit-detail__estimate-title">
                    {t("transit.estimate.title")}
                    {estimateEdited && (
                      <span className="transit-detail__estimate-tag">{t("transit.estimate.editedTag")}</span>
                    )}
                  </div>
                  <TransitEstimateCard estimate={estimate} loading={estimateLoading} error={estimateError} />
                </div>
                <span className="agent-detail-label">{t("transit.lineColor")}</span>
                <span className="agent-detail-value transit-detail__color-value">
                  <ColorSwatchPicker value={lineColor} onChange={setLineColor} presets={TRANSIT_LINE_COLOR_PRESETS} />
                </span>
                {hasVehicleOptions && (
                  <>
                    <span className="agent-detail-label">{t("transit.vehicleModel")}</span>
                    <span className="agent-detail-value">
                      <TransitVehicleLabel pathVariant={line.path_variant} value={vehicleModel} />
                    </span>
                    <div className="transit-detail__vehicle-models">
                      <TransitVehicleSelector
                        pathVariant={line.path_variant}
                        value={vehicleModel}
                        onChange={setVehicleModel}
                        disabled={saving}
                      />
                    </div>
                  </>
                )}
              </div>

              {/* Save toolbar: unsaved note on the left, Revert/Save on the right. */}
              <div className="transit-detail__toolbar">
                <span className={`transit-detail__dirty${isDirty ? " transit-detail__dirty--on" : ""}`}>
                  {isDirty ? t("transit.unsavedChanges") : t("transit.allSaved")}
                </span>
                <div className="transit-detail__actions">
                  <button
                    type="button"
                    className="transit-detail__btn transit-detail__btn--ghost"
                    disabled={!isDirty || saving}
                    onClick={resetEdits}
                  >
                    {t("transit.discardChanges")}
                  </button>
                  <button
                    type="button"
                    className="transit-detail__btn transit-detail__btn--primary"
                    disabled={!isDirty || saving}
                    onClick={() => void handleSave()}
                  >
                    {saving ? t("common.processing") : t("transit.save")}
                  </button>
                </div>
              </div>
              {saveError && <p className="transit-detail__error">{saveError}</p>}
            </section>

            {/* Route editing (stop/waypoint traversal order) happens on the 3D map, so provide only a button opening the edit panel. */}
            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">{t("transit.routeSection")}</h3>
              <div className="transit-detail__route-row">
                <button type="button" className="transit-detail__btn transit-detail__btn--ghost" onClick={openRouteEditor}>
                  {t("transit.editRoute")}
                </button>
              </div>
            </section>

            {/* Line closure. Place subtly at the end of settings and confirm in two steps to avoid accidental actions. */}
            <section className="agent-detail-section transit-detail__danger">
              <div className="transit-detail__danger-row">
                <div className="transit-detail__danger-text">
                  <span className="transit-detail__danger-title">{t("transit.deleteTitle")}</span>
                </div>
                {confirmDelete ? (
                  <div className="transit-detail__actions">
                    <button
                      type="button"
                      className="transit-detail__btn transit-detail__btn--ghost"
                      disabled={deleting}
                      onClick={() => setConfirmDelete(false)}
                    >
                      {t("transit.cancel")}
                    </button>
                    <button
                      type="button"
                      className="transit-detail__btn transit-detail__btn--danger-solid"
                      disabled={deleting}
                      onClick={() => void handleDelete()}
                    >
                      {deleting ? t("common.processing") : t("transit.deleteConfirm")}
                    </button>
                  </div>
                ) : (
                  <button
                    type="button"
                    className="transit-detail__btn transit-detail__btn--danger"
                    onClick={() => setConfirmDelete(true)}
                  >
                    {t("transit.delete")}
                  </button>
                )}
              </div>
            </section>
          </>
        )}

        {activeTab === "map" && (
          <section className="agent-detail-section">
            {line.stops.length >= 2 ? (
              <TransitLineMap
                apiBaseUrl={apiBaseUrl}
                lineAgentId={line.line_agent_id}
                stops={line.stops}
              />
            ) : (
              <p className="agent-detail-muted">{t("transit.noStops")}</p>
            )}
          </section>
        )}

        {activeTab === "diagram" && (
          <section className="agent-detail-section">
            {line.stops.length >= 2 ? (
              <TransitLineDiagram
                stops={line.stops}
                routePoints={line.route_points}
                edges={line.edges}
                color={savedColor}
                running={running}
              />
            ) : (
              <p className="agent-detail-muted">{t("transit.noStops")}</p>
            )}
          </section>
        )}
      </div>
    </div>
  );
}

/** Percentage text beside meters. The shared Meter component has no numeric display itself. */
function formatPercent(ratio: number): string {
  return `${Math.round(Math.max(0, Math.min(1, ratio)) * 100)}%`;
}

type StatTileProps = {
  label: string;
  value: string;
  unit?: string;
  /** Color values such as balance whose sign determines whether they are good or bad. */
  tone?: "positive" | "negative";
};

/** Operating result tile (label above, value below), matching the top-menu flat style. */
function StatTile({ label, value, unit, tone }: StatTileProps) {
  return (
    <div className={`transit-detail__tile${tone ? ` transit-detail__tile--${tone}` : ""}`}>
      <span className="transit-detail__tile-label">{label}</span>
      <span className="transit-detail__tile-value">
        {value}
        {unit && unit !== "" && <span className="transit-detail__unit">{unit}</span>}
      </span>
    </div>
  );
}
