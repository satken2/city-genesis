/**
 * Agent detail panel
 *
 * Dedicated panel in the window_type="agent_detail" popup window.
 * Displays the agent header (icon, status, name) and basic information.
 *
 * Navigation:
 * Agent links navigate to another agent within the same panel. The back button returns to
 * the previous agent. The entityHistory array manages history; its last entry is the current agent.
 *
 * Future extensions:
 * Type-specific details are intended below the basic information, according to agent.type and agent.is_bd.
 */
import { PLAY_MODE_SCENARIO } from "../../constants";


import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import type { AgentData, AgentResponse } from "../../types";
import {
  sendCameraFocus,
  sendChangeState,
  sendEditConnectorShow,
  sendEditConnectorHide,
} from "../../utils/ipc";
import {
  TYPE_BUILDING,
  TYPE_CITY,
  TYPE_HOUSEHOLD,
  TYPE_TRADE,
  TYPE_STANDARD,
  TYPE_EDUCATION,
  STATUS_SCHEDULED_CONSTRUCTION,
  STATUS_CONSTRUCTING,
  STATUS_PENDING,
  STATUS_DEMOLISHING,
} from "../../constants";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { buildingWarningMask, fetchSpatialWarnings, type SpatialWarning } from "../../utils/agentAlerts";
import { usePlayMode } from "../../hooks/usePlayMode";
import { useConstructionBudgetRow } from "../../hooks/useConstructionBudgetRow";
import { resolveAgentIcon } from "../../utils/agentIcon";
import { useMasterData } from "../../contexts/MasterDataContext";
import { InfoHeader } from "../common/InfoHeader";
import { TagRow } from "../common/TagRow";
import { isTaggable } from "../../utils/constructionTags";
import { SupplyChainSection } from "../supply/SupplyChainSection";
import { supportsSupplyGraph } from "../supply/supplyGraph";
import { useSupplyGraph } from "../../hooks/useSupplyGraph";
import { AgentAlertsPanel } from "./AgentAlertsPanel";
import { AgentBasicInfoPanel } from "./AgentBasicInfoPanel";
import { BuildingInfo } from "./BuildingInfo";
import { BuildingInfoPanel } from "./BuildingInfoPanel";
import { ParkingInfoPanel } from "./ParkingInfoPanel";
import { HouseholdInfoPanel } from "./HouseholdInfoPanel";
import { DemandInfoPanel } from "./DemandInfoPanel";
import { SupplyInfoPanel } from "./SupplyInfoPanel";
import { CargoInfoPanel } from "./CargoInfoPanel";
import { InfraConnectionPanel } from "./InfraConnectionPanel";
import { EducationTabPanel } from "./EducationTabPanel";
import { EmploymentInfoPanel } from "./EmploymentInfoPanel";
import { AgentOperationPanel } from "./AgentOperationPanel";
import { PublicFacilityPanel } from "./PublicFacilityPanel";
import { BuildingConstructionInfoPanel } from "./BuildingConstructionInfoPanel";
import { GradingConstructionInfoPanel } from "../grading/GradingConstructionInfoPanel";
import { isGradingVariant } from "../grading/grading";
import "./AgentDetailPanel.css";
// Import explicitly to reuse icon buttons (.operation-panel / .operation-btn).
import "./AgentOperationPanel.css";
import rocketIcon from "../../assets/images/ui/rocket.png";

/** Icon outline during acquisition negotiations. Orange distinguishes it from public facility yellow. */
const ACQUISITION_FRAME_COLOR = "#f97316";

/**
 * Relocation button icon (arrows in four directions). Drawn as our own SVG rather than an external
 * asset to avoid adding asset credits. Its currentColor fill follows the button text color.
 */
function RelocateIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path
        d="M12 2 L15.5 5.5 H13 V11 H18.5 V8.5 L22 12 L18.5 15.5 V13 H13 V18.5 H15.5 L12 22 L8.5 18.5 H11 V13 H5.5 V15.5 L2 12 L5.5 8.5 V11 H11 V5.5 H8.5 Z"
        fill="currentColor"
      />
    </svg>
  );
}

type AgentDetailPanelProps = {
  apiBaseUrl: string;
  pathTemplate: string;
  initialEntityId: string;
};

export function AgentDetailPanel({
  apiBaseUrl,
  pathTemplate,
  initialEntityId,
}: AgentDetailPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const scenarioMode = usePlayMode(apiBaseUrl) === PLAY_MODE_SCENARIO;
  const [entityHistory, setEntityHistory] = useState<string[]>([
    initialEntityId,
  ]);
  const [agentData, setAgentData] = useState<AgentData | null>(null);
  const [spatialWarnings, setSpatialWarnings] = useState<SpatialWarning[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const [activeTab, setActiveTab] = useState<string>("basic");
  const [utilizationTarget, setUtilizationTarget] = useState<number>(100);
  const [statusSaving, setStatusSaving] = useState(false);

  const currentEntityId = entityHistory[entityHistory.length - 1];
  const canGoBack = entityHistory.length > 1;
  const hasSupplyGraph = agentData?.agent_id === Number(currentEntityId) && supportsSupplyGraph(agentData, masterData);
  const supplyGraph = useSupplyGraph(apiBaseUrl, currentEntityId, hasSupplyGraph);

  // "Construction cost per epoch" and "remaining construction time (epochs)" for buildings under construction.
  // (Cost and construction time calculations are centralized in get_budget_agents and fetched by a shared hook.)
  const { costPerEpoch: constructionCostPerEpoch, remainingEpochs } = useConstructionBudgetRow(
    apiBaseUrl,
    "agent",
    currentEntityId,
    agentData?.status === STATUS_CONSTRUCTING,
    refreshKey,
  );

  useEffect(() => {
    if (agentData && agentData.utilization_target !== undefined) {
      setUtilizationTarget(Math.round(agentData.utilization_target * 100));
    } else {
      setUtilizationTarget(100);
    }
  }, [agentData]);

  // While the displayed facility has connector nodes (infra_node_id / depot_node_id),
  // highlight them in Unity and allow dragging them as soon as the panel opens
  // (connector editing mode). Replace the highlight when agent_id or the node ID changes,
  // such as when navigating to another facility. Cleanup only sends edit_connector:hide (Unity returns
  // to normal mode if editing connectors; STATE_NORMAL would also end other editing states such as building construction).
  // Closing with the close button does not unmount the panel, so Rust (window.rs) sends the same notification.
  const highlightInfraNodeId = agentData?.infra_node_id ?? 0;
  const highlightDepotNodeId = agentData?.depot_node_id ?? 0;
  const highlightAgentId = agentData?.agent_id ?? 0;
  useEffect(() => {
    if (highlightInfraNodeId <= 0 && highlightDepotNodeId <= 0) return;
    void sendEditConnectorShow(highlightAgentId, highlightInfraNodeId, highlightDepotNodeId);
    void sendChangeState(
      "STATE_EDIT_CONNECTOR",
      {
        agent_id: highlightAgentId,
        infra_node_id: highlightInfraNodeId,
        depot_node_id: highlightDepotNodeId,
      },
      "agent_detail",
    );
    return () => {
      void sendEditConnectorHide();
    };
  }, [highlightAgentId, highlightInfraNodeId, highlightDepotNodeId]);

  const handleUtilizationTargetChange = async () => {
    if (!agentData) return;
    const url = buildApiUrl(apiBaseUrl, pathTemplate, currentEntityId);
    try {
      const updated = await fetchApi<AgentResponse>(url, {
        method: "PATCH",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          utilization_target: utilizationTarget / 100,
        }),
      });
      setAgentData(updated.economic_agent);
    } catch (err) {
      console.error("Failed to update utilization target:", err);
    }
  };

  /**
 * Rename the agent. Only city-managed public facilities can be renamed by the player;
 * private agents (businesses, households, etc.) cannot be renamed.
 */
  const handleNameSave = async (next: string) => {
    const url = buildApiUrl(apiBaseUrl, pathTemplate, currentEntityId);
    const updated = await fetchApi<AgentResponse>(url, {
      method: "PATCH",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ name: next }),
    });
    setAgentData(updated.economic_agent);
  };

  /** Agent details have no geometry, so focus on their parcel (the containing building's parcel for tenants). */
  const focusCamera = async (parcelId: number) => {
    const parcel = await fetchApi<{ polygon: number[][] }>(buildApiUrl(apiBaseUrl, `/api/v1/parcels/${parcelId}`));
    await sendCameraFocus("agent_detail", parcel.polygon);
  };

  /** Handler that toggles between starting construction and holding it. */
  const handleStatusToggle = async (checked: boolean) => {
    if (!agentData || statusSaving) return;
    setStatusSaving(true);
    const newStatus = checked ? STATUS_SCHEDULED_CONSTRUCTION : STATUS_PENDING;
    const url = buildApiUrl(apiBaseUrl, pathTemplate, currentEntityId);
    try {
      const updated = await fetchApi<AgentResponse>(url, {
        method: "PATCH",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          status: newStatus,
        }),
      });
      setAgentData(updated.economic_agent);
    } catch (err) {
      console.error("Failed to update agent status:", err);
    } finally {
      setStatusSaving(false);
    }
  };

  // Refetch data when the current agent ID changes.
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    setSpatialWarnings([]);

    const url = buildApiUrl(apiBaseUrl, pathTemplate, currentEntityId);

    fetchApi<AgentResponse>(url, { signal: controller.signal })
      .then(async (response) => {
        const warnings = response.economic_agent.is_bd
          ? await fetchSpatialWarnings(apiBaseUrl, controller.signal)
          : [];
        if (!controller.signal.aborted) {
          setAgentData(response.economic_agent);
          setSpatialWarnings(warnings);
          setLoading(false);
        }
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          const message =
            err instanceof Error ? err.message : "Unknown error";
          setError(message);
          setLoading(false);
        }
      });

    return () => {
      controller.abort();
    };
  }, [apiBaseUrl, pathTemplate, currentEntityId, refreshKey]);

  // Correct and initialize activeTab according to the current agent type.
  useEffect(() => {
    if (!agentData) return;

    const isImportOrExport = agentData.type === TYPE_TRADE;
    const showBasicInfo = !isImportOrExport;
    const showHouseholdInfo = agentData.type === TYPE_HOUSEHOLD;
    const hasBasicInfoTab = showBasicInfo;
    const hasEducationInfoTab = agentData.type === TYPE_EDUCATION;
    const hasHouseholdInfoTab = showHouseholdInfo;
    const hasCargoInfoTab = isImportOrExport;

    const showBuildingPanel = agentData.is_bd;
    const showBuildingDetails = agentData.type === TYPE_BUILDING;
    const hasBuildingInfoTab = showBuildingPanel || showBuildingDetails;

    const demandAmount: Record<string, number> = agentData.demand_amount ?? {};
    const receivedAmount: Record<string, number> = agentData.received_amount ?? {};
    const acquiredAmount: Record<string, number> = agentData.remain_amount ?? {};
    const productIds = new Set([
      ...Object.keys(demandAmount),
      ...Object.keys(receivedAmount),
      ...Object.keys(acquiredAmount),
    ]);
    const hasDemand = Array.from(productIds).some(
      (id) =>
        (demandAmount[id] ?? 0) !== 0 ||
        (receivedAmount[id] ?? 0) !== 0 ||
        (acquiredAmount[id] ?? 0) !== 0
    );

    const showDemandInfo = !isImportOrExport && hasDemand;
    const showSupplyInfo = agentData.type === TYPE_STANDARD;
    const hasTradeInfoTab = showDemandInfo || showSupplyInfo;

    const showEmploymentInfo = agentData.type === TYPE_STANDARD;
    const hasEmploymentInfoTab = showEmploymentInfo;

    const showConstructionTab = [
      STATUS_PENDING,
      STATUS_SCHEDULED_CONSTRUCTION,
      STATUS_CONSTRUCTING,
      STATUS_DEMOLISHING
    ].includes(agentData.status);

    const isPreConstruction =
      agentData.status === STATUS_SCHEDULED_CONSTRUCTION ||
      agentData.status === STATUS_PENDING;

    const availableIds: string[] = [];
    if (isPreConstruction) {
      if (showConstructionTab) availableIds.push("construction");
    } else {
      if (hasBasicInfoTab) availableIds.push("basic");
      if (hasEducationInfoTab) availableIds.push("education");
      if (hasCargoInfoTab) availableIds.push("cargo");
      if (hasBuildingInfoTab) availableIds.push("building");
      if (hasHouseholdInfoTab) availableIds.push("household");
      if (hasTradeInfoTab) availableIds.push("trade");
      if (hasEmploymentInfoTab) availableIds.push("employment");
      if (showConstructionTab) availableIds.push("construction");
    }

    if (availableIds.length > 0 && !availableIds.includes(activeTab)) {
      if (availableIds.includes("construction") && (agentData.status === STATUS_CONSTRUCTING || isPreConstruction)) {
        setActiveTab("construction");
      } else {
        setActiveTab(availableIds[0]);
      }
    }
  }, [agentData, activeTab]);

  /**
 * Navigate to another agent.
 * Push the new entity ID onto the history stack.
 */
  function navigateToAgent(entityId: string) {
    setEntityHistory((prev) => [...prev, entityId]);
  }

  /**
 * Return to the previous agent.
 */
  function navigateBack() {
    if (canGoBack) {
      setEntityHistory((prev) => prev.slice(0, -1));
    }
  }

  if (loading) {
    return <p className="agent-detail-muted">{t("common.loading")}</p>;
  }

  if (error || !agentData) {
    return (
      <div className="agent-detail-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  const iconSrc = resolveAgentIcon(
    agentData.variant,
    agentData.type,
    agentData.is_bd,
    agentData.bd_type
  );

  const isImportOrExport = agentData.type === TYPE_TRADE;

  const isPreConstruction = agentData.status === STATUS_SCHEDULED_CONSTRUCTION || agentData.status === STATUS_PENDING;

  /** Whether the city government is negotiating to acquire the agent's parcel for road construction or similar work. */
  const underAcquisition = agentData.under_acquisition === true;

  const parcelId = agentData.parcel_id ?? 0; // Values of 0 or less have no parcel.

  /**
 * Get the variant display name from master data.
 * Landmarks (landmark>0) share the generic variant name "Landmark", so display the landmark's
 * own name (such as the Grand Observation Tower).
 */
  const variantName =
    (agentData.landmark != null && agentData.landmark > 0
      ? masterData?.landmarks?.[agentData.landmark]?.display_name
      : undefined) ?? masterData?.agents[agentData.variant]?.display_name;

  /**
 * Whether the agent has building geometry (a site). The city government has none until City Hall
 * is placed when founding the city. Any building with geometry gets a button opening the appearance
 * window (building_appearance), regardless of status.
 */
  const hasBuildingBody = agentData.is_bd && (agentData.parcel_id ?? 0) > 0;

  /** Whether City Hall can be relocated. Show the relocation button only when it has a site. */
  const isRelocatableCityHall = agentData.type === TYPE_CITY && hasBuildingBody;

  // Display conditions for each detail section.
  const showBasicInfo = !isImportOrExport;
  const showHouseholdInfo = agentData.type === TYPE_HOUSEHOLD;
  const hasBasicInfoTab = showBasicInfo;
  const hasEducationInfoTab = agentData.type === TYPE_EDUCATION;
  const hasHouseholdInfoTab = showHouseholdInfo;

  const hasCargoInfoTab = isImportOrExport;

  const showBuildingPanel = agentData.is_bd;
  const showBuildingDetails = agentData.type === TYPE_BUILDING;
  const hasBuildingInfoTab = showBuildingPanel || showBuildingDetails;

  const demandAmount: Record<string, number> = agentData.demand_amount ?? {};
  const receivedAmount: Record<string, number> = agentData.received_amount ?? {};
  const acquiredAmount: Record<string, number> = agentData.remain_amount ?? {};
  const productIds = new Set([
    ...Object.keys(demandAmount),
    ...Object.keys(receivedAmount),
    ...Object.keys(acquiredAmount),
  ]);
  const hasDemand = Array.from(productIds).some(
    (id) =>
      (demandAmount[id] ?? 0) !== 0 ||
      (receivedAmount[id] ?? 0) !== 0 ||
      (acquiredAmount[id] ?? 0) !== 0
  );

  const showDemandInfo = !isImportOrExport && hasDemand;
  const showSupplyInfo = agentData.type === TYPE_STANDARD;
  const hasTradeInfoTab = showDemandInfo || showSupplyInfo;

  const showEmploymentInfo = agentData.type === TYPE_STANDARD;
  const hasEmploymentInfoTab = showEmploymentInfo;

  const showConstructionTab = [
    STATUS_PENDING,
    STATUS_SCHEDULED_CONSTRUCTION,
    STATUS_CONSTRUCTING,
    STATUS_DEMOLISHING
  ].includes(agentData.status);

  const tabs: { id: string; label: string }[] = [];
  if (isPreConstruction) {
    if (showConstructionTab) {
      tabs.push({ id: "construction", label: t("agent.tabConstructionInfo", "建設工事") });
    }
  } else {
    if (hasBasicInfoTab) {
      tabs.push({ id: "basic", label: t("agent.tabBasicInfo", "基本情報") });
    }
    if (hasEducationInfoTab) {
      tabs.push({ id: "education", label: t("agent.tabEducationInfo", "教育") });
    }
    if (hasCargoInfoTab) {
      tabs.push({ id: "cargo", label: t("agent.tabCargoInfo", "荷役情報") });
    }
    if (hasBuildingInfoTab) {
      tabs.push({ id: "building", label: t("agent.tabBuildingInfo", "建物情報") });
    }
    if (hasHouseholdInfoTab) {
      tabs.push({ id: "household", label: t("agent.tabHouseholdInfo", "世帯情報") });
    }
    if (hasTradeInfoTab) {
      tabs.push({ id: "trade", label: t("agent.tabTradeInfo", "需給・取引") });
    }
    if (hasEmploymentInfoTab) {
      tabs.push({ id: "employment", label: t("agent.tabEmploymentInfo", "雇用情報") });
    }
    if (showConstructionTab) {
      tabs.push({ id: "construction", label: t("agent.tabConstructionInfo", "建設工事") });
    }
  }

  return (
    <div className="agent-detail">
      {canGoBack && (
        <button
          className="agent-detail-back"
          onClick={navigateBack}
          type="button"
        >
          ← {t("agent.back")}
        </button>
      )}

      <InfoHeader
        iconSrc={iconSrc}
        status={agentData.status}
        statusReason={agentData.status_reason}
        variantName={variantName}
        name={agentData.name}
        constructionProgress={agentData.construction_progress}
        iconLabel={
          underAcquisition
            ? t("agent.underAcquisition")
            : agentData.public_facility
              ? t("agent.publicFacility")
              : undefined
        }
        iconFrameColor={underAcquisition ? ACQUISITION_FRAME_COLOR : undefined}
        onNameSave={agentData.public_facility ? handleNameSave : undefined}
        onFocusCamera={parcelId > 0 ? () => void focusCamera(parcelId).catch(console.error) : undefined}
      />

      <AgentAlertsPanel
        alert={agentData.alert}
        warning={buildingWarningMask(agentData, spatialWarnings)}
      />

      {/*
 * Construction project tags and a toggle to hide only this building in the 3D view.
 * Tags can be changed only for public facilities awaiting or undergoing construction, so show this
 * row only then. (Tags persist after completion and count as completed in the construction projects window.)
 */}
      {agentData.is_bd && isTaggable("agent", agentData.status, agentData.public_facility) && (
        <TagRow apiBaseUrl={apiBaseUrl} kind="agent" entityId={agentData.agent_id} />
      )}

      {/* Warning banner for construction not started (on hold). */}
      {agentData.is_bd && agentData.status === STATUS_PENDING && (
        <div className="pending-banner pending-banner--active">
          <span className="pending-banner__text">
            {t("agent.pendingWarning", "この建物は計画中です。着工を指示してください。")}
          </span>
          <button
            className="pending-banner__btn"
            type="button"
            disabled={statusSaving}
            onClick={() => void handleStatusToggle(true)}
          >
            <img src={rocketIcon} alt="rocket" />
            {t("agent.action.startConstruction", "着工")}
          </button>
        </div>
      )}

      {/*
 * Public facilities show a control panel card combining operation buttons, target utilization,
 * and abandonment. Other agents keep the operation button row and target utilization card separate.
 */}
      {agentData.public_facility ? (
        <PublicFacilityPanel
          agent={agentData}
          apiBaseUrl={apiBaseUrl}
          onActionPerformed={() => setRefreshKey((k) => k + 1)}
          utilizationTarget={utilizationTarget}
          setUtilizationTarget={setUtilizationTarget}
          onUtilizationCommit={handleUtilizationTargetChange}
          constructionCostPerEpoch={constructionCostPerEpoch}
          remainingEpochs={remainingEpochs}
        />
      ) : (
      <>
      <AgentOperationPanel
        agent={agentData}
        apiBaseUrl={apiBaseUrl}
        onActionPerformed={() => setRefreshKey((k) => k + 1)}
      />

      {(() => {
        const agentMaster = masterData?.agents[agentData.variant];
        // Target utilization is a player control for both the spare-capacity (>0) and fixed-quantity (<0) methods.
        const isHeadroomFacility = (agentMaster?.production_headroom ?? 0) !== 0;
        // For buildings under construction, utilization_target adjusts "construction speed" (up to 500%).
        const isConstructing = agentData.status === STATUS_CONSTRUCTING;
        if (!isConstructing && !isHeadroomFacility) return null;

        const sliderMax = isConstructing ? 500 : 100;

        return (
          <div className="agent-detail-utilization-target">
            <div className="agent-detail-utilization-target-header">
              <span className="agent-detail-utilization-target-title">
                {isConstructing
                  ? t("agent.constructionSpeed", "建設スピード")
                  : t("agent.utilizationTarget", "稼働率目標")}
              </span>
              <span className="agent-detail-utilization-target-value">
                {utilizationTarget}%
              </span>
            </div>
            <div className="agent-detail-utilization-target-slider-container">
              <input
                type="range"
                min="0"
                max={sliderMax}
                value={utilizationTarget}
                onChange={(e) => setUtilizationTarget(Number(e.target.value))}
                onMouseUp={handleUtilizationTargetChange}
                onTouchEnd={handleUtilizationTargetChange}
                className="agent-detail-utilization-target-slider"
              />
            </div>
          </div>
        );
      })()}
      </>
      )}

      {/*
 * City Hall relocation button. Opens the building construction panel in relocation mode and uses
 * the same placement controls as new construction (with no cost or construction work).
 */}
      {isRelocatableCityHall && (
        <div className="operation-panel">
          <button
            className="operation-btn"
            type="button"
            title={t("agent.relocateCityHall")}
            onClick={() => {
              void invoke("open_registered_window", {
                windowType: "building_construction",
                params: {
                  extra: {
                    relocateCityHall: true,
                    titleKey: "building_construction.relocateTitle",
                  },
                },
              });
            }}
          >
            <RelocateIcon />
          </button>
        </div>
      )}

      {/*
 * Agents in planning have no meaningful existing data to display, so show only delete/edit buttons
 * and omit detail sections (except when there is a construction work tab).
 */}
      {tabs.length > 0 && (
        <div className="agent-detail-tabs">
          {tabs.map((tab) => (
            <button
              key={tab.id}
              className={[
                "agent-detail-tab",
                tab.id === activeTab && "agent-detail-tab--active",
              ]
                .filter(Boolean)
                .join(" ")}
              type="button"
              onClick={() => setActiveTab(tab.id)}
            >
              {tab.label}
            </button>
          ))}
        </div>
      )}

      {tabs.length > 0 && (
        <div className="agent-detail-tab-content">
          {activeTab === "basic" && hasBasicInfoTab && !isPreConstruction && (
            <AgentBasicInfoPanel
              agent={agentData}
              onNavigate={navigateToAgent}
            />
          )}

          {activeTab === "education" && hasEducationInfoTab && !isPreConstruction && (
            <EducationTabPanel
              agent={agentData}
              apiBaseUrl={apiBaseUrl}
              pathTemplate={pathTemplate}
              onSaved={() => setRefreshKey((k) => k + 1)}
            />
          )}

          {activeTab === "cargo" && hasCargoInfoTab && !isPreConstruction && (
            <CargoInfoPanel agent={agentData} apiBaseUrl={apiBaseUrl} />
          )}

          {activeTab === "building" && hasBuildingInfoTab && !isPreConstruction && (
            <>
              {showBuildingPanel && <BuildingInfoPanel agent={agentData} />}
              <BuildingInfo agent={agentData} onNavigate={navigateToAgent} />
              {showBuildingPanel && <ParkingInfoPanel agent={agentData} />}
            </>
          )}

          {activeTab === "household" && hasHouseholdInfoTab && !isPreConstruction && (
            <>
              {showHouseholdInfo && (
                <HouseholdInfoPanel agent={agentData} />
              )}
            </>
          )}

          {activeTab === "trade" && hasTradeInfoTab && !isPreConstruction && (
            <>
              {/* Shown only for facilities that send or receive infrastructure goods (checked inside the component). */}
              <InfraConnectionPanel agent={agentData}>
                {hasSupplyGraph && <SupplyChainSection {...supplyGraph} />}
              </InfraConnectionPanel>
              {showDemandInfo && <DemandInfoPanel agent={agentData} />}
              {showSupplyInfo && <SupplyInfoPanel agent={agentData} />}
            </>
          )}

          {activeTab === "employment" && hasEmploymentInfoTab && !isPreConstruction && (
            <>
              {showEmploymentInfo && <EmploymentInfoPanel agent={agentData} />}
            </>
          )}

          {activeTab === "construction" && showConstructionTab && (
            isGradingVariant(agentData.variant) ? (
              <GradingConstructionInfoPanel
                agent={agentData}
                apiBaseUrl={apiBaseUrl}
                refreshKey={refreshKey}
              />
            ) : (
              <BuildingConstructionInfoPanel
                agent={agentData}
                apiBaseUrl={apiBaseUrl}
                refreshKey={refreshKey}
              />
            )
          )}
        </div>
      )}

      {hasBuildingBody && (
        <button
          className="property-edit-open-btn"
          type="button"
          onClick={() => {
            void invoke("open_registered_window", {
              windowType: "building_appearance",
              entityId: currentEntityId,
            });
          }}
        >
          {t("agent.editAppearance")}
        </button>
      )}

      {scenarioMode && (
        <button
          className="property-edit-open-btn"
          type="button"
          onClick={() => {
            void invoke("open_registered_window", {
              windowType: "property_edit",
              params: { extra: { entityType: "agent", entityId: currentEntityId } },
            });
          }}
        >
          {t("property_edit.openEditor", "Edit properties")}
        </button>
      )}
    </div>
  );
}
