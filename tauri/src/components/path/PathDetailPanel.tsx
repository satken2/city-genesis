import { requireApiSuccess } from "../../utils/api";
import { describeError } from "../../utils/errors";
/**
 * Path detail panel.
 *
 * Dedicated panel in a window_type="path_detail" popup, showing path header information (icon/status/name) and attributes.
 *
 * Contents:
 * - InfoHeader: icon, status, name (if unnamed and assigned to a road: "road name (section #id)").
 * - Road membership row for streets/highways (road/RoadMembershipRow).
 * - Basic information tab:
 *   - Basic information section: district, terrain following, color (PathColorRow), and meters for
 *     utilization/durability/noise/air pollution (PathConditionPanel).
 *   - Traffic/speed settings (PathTrafficPanel): direction toggle, speed-limit sign/input, passing speed,
 *     on-street parking donut, traffic by vehicle type.
 * - Road equipment tab: streetlights, fences, street trees, power distribution method.
 * - Station information for station buildings; construction tab for planned paths and paths under construction.
 *
 * Use paths/path_N.png according to path_type. Status display uses InfoHeader, shared with agent and parcel panels.
 *
 * When nearby_zones is included in the API response, district membership can be changed through a dropdown.
 * Only districts of adjacent paths are selectable, maintaining district continuity.
 */
import { PLAY_MODE_SCENARIO } from "../../constants";


import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import type { NearbyZoneEntry, PathData } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { sendHighlightObjects, sendClearHighlights, sendCameraFocus } from "../../utils/ipc";
import type { PathInterferenceHighlight } from "../../utils/ipc";
import { usePlayMode } from "../../hooks/usePlayMode";
import { resolvePathIcon } from "../../utils/pathIcon";
import { useMasterData } from "../../contexts/MasterDataContext";
import {
  PATH_STREET,
  PATH_STATION_HOUSE,
  STATUS_SCHEDULED_CONSTRUCTION,
  STATUS_CONSTRUCTING,
  STATUS_PENDING,
  STATUS_DEMOLISHING,
  ALERT_PATH_CONSTRUCTION_SHORTAGE,
  ALERT_PATH_ACCIDENT,
  ALERT_PATH_NO_STATION_HOUSE,
  VALUE_NONE
} from "../../constants";
import { InfoHeader } from "../common/InfoHeader";
import { TagRow } from "../common/TagRow";
import { isTaggable } from "../../utils/constructionTags";
import { RoadMembershipRow, useRoadList, type PathRoadFields } from "../road/RoadMembershipRow";
import { hasRoad, isNamedRoadPathType, notifyRoadsChanged, roadDisplayName, ROADS_CHANGED_EVENT } from "../road/roadApi";
import { ZoneLink } from "../common/ZoneLink";
import { StationInfoSection } from "./StationInfoSection";
import { hasPathCondition, PathConditionPanel } from "./PathConditionPanel";
import { PathEquipmentPanel, pathEquipmentFields } from "./PathEquipmentPanel";
import { PathColorRow } from "./PathColorRow";
import { PathTrafficPanel } from "./PathTrafficPanel";
import { PathOperationPanel } from "./PathOperationPanel";
import { PathTopologyEditPanel } from "./PathTopologyEditPanel";
import type { PathTopologyEditMode } from "./PathTopologyEditPanel";
import { PathConstructionInfoPanel } from "./PathConstructionInfoPanel";
import { PATH_REBUILD_CHANGED_EVENT } from "./pathRebuildApi";
import shortageIcon from "../../assets/images/ui/agent_panel/bribery.png";
import accidentIcon from "../../assets/images/ui/agent_panel/car-accident.png";
// Alert cards and construction speed styling are shared with the agent panel. Panels are lazy-loaded
// (App.tsx lazy), splitting CSS into chunks as well. Explicitly import the shared source or
// styles will be absent in windows that have not loaded that chunk.
import "../agent/AgentDetailPanel.css";
import "./PathDetailPanel.css";
import rocketIcon from "../../assets/images/ui/rocket.png";

type PathDetailPanelProps = {
  apiBaseUrl: string;
  pathTemplate: string;
  initialEntityId: string;
};

export function PathDetailPanel({
  apiBaseUrl,
  pathTemplate,
  initialEntityId,
}: PathDetailPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const scenarioMode = usePlayMode(apiBaseUrl) === PLAY_MODE_SCENARIO;
  const [pathData, setPathData] = useState<(PathData & PathRoadFields) | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selectedTab, setSelectedTab] = useState<string | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  // Construction speed (utilization_target, integer percent) and construction cost/time for roads under construction.
  const [constructionSpeed, setConstructionSpeed] = useState<number>(100);
  const [statusSaving, setStatusSaving] = useState(false);
  // Active path merge/split operation. While non-null, the 3D view is in target selection mode.
  const [topologyEditMode, setTopologyEditMode] = useState<PathTopologyEditMode | null>(null);

  const url = buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId);

  useEffect(() => {
    const unlisten = listen<{ path_id: number }>(PATH_REBUILD_CHANGED_EVENT, event => {
      if (String(event.payload.path_id) === initialEntityId) setRefreshKey(key => key + 1);
    });
    return () => { void unlisten.then(stop => stop()); };
  }, [initialEntityId]);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetch(url, { signal: controller.signal })
      .then(async (res) => {
        await requireApiSuccess(res);
        return res.json() as Promise<PathData & PathRoadFields>;
      })
      .then((data) => {
        if (!controller.signal.aborted) {
          setPathData(data);
          setLoading(false);
        }
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          setError(describeError(err));
          setLoading(false);
        }
      });

    return () => {
      controller.abort();
    };
  }, [url, refreshKey]);

  const isNamedRoad = isNamedRoadPathType(pathData?.path_type);
  const { roads } = useRoadList(apiBaseUrl, isNamedRoad);

  // Road membership can change from road details or other path details. Refetching the whole panel would erase
  // unsaved equipment inputs, so update only road_id on change notifications.
  const refreshRoadMembership = useCallback(async () => {
    try {
      const data = await fetchApi<PathData & PathRoadFields>(url);
      setPathData((prev) => (prev && prev.path_id === data.path_id ? { ...prev, road_id: data.road_id } : prev));
    } catch (err) {
      console.error("[PathDetailPanel] road membership refresh failed:", err);
    }
  }, [url]);

  useEffect(() => {
    if (!isNamedRoad) return;
    const unlistenPromise = listen(ROADS_CHANGED_EVENT, () => void refreshRoadMembership());
    return () => {
      void unlistenPromise.then((unlisten) => unlisten());
    };
  }, [isNamedRoad, refreshRoadMembership]);

  const isPreConstruction = pathData?.status === STATUS_SCHEDULED_CONSTRUCTION || pathData?.status === STATUS_PENDING;
  const isConstructing = pathData?.status === STATUS_CONSTRUCTING;
  const showConstructionInfo = pathData && [
    STATUS_PENDING,
    STATUS_SCHEDULED_CONSTRUCTION,
    STATUS_CONSTRUCTING,
    STATUS_DEMOLISHING
  ].includes(pathData.status);

  // Initialize local construction speed state (utilization_target) for the road under construction.
  useEffect(() => {
    if (pathData && pathData.utilization_target != null) {
      setConstructionSpeed(Math.round(pathData.utilization_target * 100));
    } else {
      setConstructionSpeed(100);
    }
  }, [pathData]);

  // While a path awaiting construction is open, automatically highlight interfering parcels/buildings/existing paths in 3D.
  // Fetch authoritative data already held by the backend (GET /paths/{id}/interference) and send it to Unity.
  // No action for built/under-construction paths, where interference is irrelevant. Clear on close/status transitions.
  useEffect(() => {
    if (!apiBaseUrl || !pathData || !isPreConstruction) return;
    const pathId = pathData.path_id;
    let cancelled = false;
    const controller = new AbortController();
    fetch(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/${pathId}/interference`, {
      signal: controller.signal,
    })
      .then(async (res) => {
        await requireApiSuccess(res);
        return res.json() as Promise<PathInterferenceHighlight>;
      })
      .then((data) => {
        if (!cancelled) void sendHighlightObjects(data, "path_detail");
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          console.error("[PathDetailPanel] interference fetch failed:", err);
        }
      });
    return () => {
      cancelled = true;
      controller.abort();
      void sendClearHighlights("path_detail");
    };
  }, [apiBaseUrl, pathData, isPreConstruction]);

  /** PATCH construction speed (utilization_target, integer percent). */
  const handleConstructionSpeedChange = useCallback(() => {
    if (!pathData) return;
    const base = apiBaseUrl.replace(/\/+$/, "");
    fetch(`${base}/api/v1/paths/${pathData.path_id}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ utilization_target: constructionSpeed / 100 }),
    })
      .then(() => setRefreshKey((k) => k + 1))
      .catch((err) => console.error("Failed to update construction speed:", err));
  }, [pathData, apiBaseUrl, constructionSpeed]);

  /**
 * Change the name (road/station name). Always allowed because all paths are public assets laid by the city.
 * After success, refetch path details to update station information and other name-dependent sections as well.
 */
  const handleNameSave = useCallback(async (next: string) => {
    if (!pathData) return;
    await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/paths/{id}", String(pathData.path_id)), {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name: next }),
    });
    setRefreshKey((k) => k + 1);
  }, [pathData, apiBaseUrl]);

  /** Handler that toggles between starting construction and holding it. */
  const handleStatusToggle = useCallback((checked: boolean) => {
    if (!pathData || statusSaving) return;
    setStatusSaving(true);
    const newStatus = checked ? STATUS_SCHEDULED_CONSTRUCTION : STATUS_PENDING;
    const base = apiBaseUrl.replace(/\/+$/, "");
    fetch(`${base}/api/v1/paths/${pathData.path_id}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ status: newStatus }),
    })
      .then(async (res) => {
        await requireApiSuccess(res);
        setRefreshKey((k) => k + 1);
        // This changes planned/under-construction lengths in road details and dashed line-map rendering.
        await notifyRoadsChanged(undefined, "path_detail");
      })
      .catch((err) => {
        console.error("Failed to update path status:", err);
      })
      .finally(() => setStatusSaving(false));
  }, [pathData, apiBaseUrl, statusSaving]);

  /**
 * Determine whether district reassignment is available from nearby_zones.
 * Available for PATH_STREET when nearby_zones has at least one entry.
 */
  const canChangeZone =
    pathData?.path_type === PATH_STREET &&
    pathData.nearby_zones != null &&
    pathData.nearby_zones.length > 0;

  const [zoneSaving, setZoneSaving] = useState(false);
  const [zoneError, setZoneError] = useState<string | null>(null);

  /**
 * Build district dropdown choices.
 * Prepend the current district if absent from nearby_zones.
 */
  const zoneOptions: NearbyZoneEntry[] = (() => {
    if (!pathData || !pathData.nearby_zones) return [];
    const options = [...pathData.nearby_zones];
    const hasCurrentZone =
      pathData.zone_id != null &&
      options.some((z) => z.zone_id === pathData.zone_id);
    if (!hasCurrentZone && pathData.zone_name != null && pathData.zone_id > 0) {
      options.unshift({ zone_id: pathData.zone_id, zone_name: pathData.zone_name });
    }
    return options;
  })();

  /**
 * Send district reassignment through PATCH.
 * Refetch data on success.
 */
  const handleZoneChange = useCallback(
    (newZoneId: number) => {
      if (!pathData || zoneSaving) return;
      setZoneSaving(true);
      setZoneError(null);

      const base = apiBaseUrl.replace(/\/+$/, "");
      fetch(`${base}/api/v1/paths/${pathData.path_id}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ zone_id: newZoneId }),
      })
        .then(async (res) => {
          await requireApiSuccess(res);
          setRefreshKey((k) => k + 1);
        })
        .catch((err) => {
          setZoneError(describeError(err));
        })
        .finally(() => setZoneSaving(false));
    },
    [pathData, apiBaseUrl, zoneSaving],
  );

  if (loading) {
    return <p className="agent-detail-muted">{t("common.loading")}</p>;
  }

  if (error || !pathData) {
    return (
      <div className="agent-detail-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  const iconSrc = resolvePathIcon(pathData.path_type, pathData.path_variant);

  /** Get the path variant's display name from master data. */
  const variantName = masterData?.paths[pathData.path_variant]?.display_name;

  // For station building paths (= stations), show station information (rename, member platforms, serving lines).
  const isStationHouse =
    masterData?.paths[pathData.path_variant]?.type === PATH_STATION_HOUSE;

  const hasEquipment = pathEquipmentFields.some(field =>
    masterData?.paths[pathData.path_variant]?.[`${field}_allowed`] || pathData[field] !== 0
  );
  const tabs: { id: string; label: string }[] = [];
  if (!isPreConstruction) {
    tabs.push({ id: "basic", label: t("path.tabs.basic") });
    if (hasEquipment) tabs.push({ id: "equipment", label: t("pathEquipment.title") });
  }
  if (isStationHouse) tabs.push({ id: "station", label: t("station.sectionTitle") });
  if (showConstructionInfo) tabs.push({ id: "construction", label: t("path.tabs.construction") });
  const activeTab = tabs.some(tab => tab.id === selectedTab)
    ? selectedTab
    : (showConstructionInfo && (isPreConstruction || isConstructing) ? "construction" : tabs[0]?.id);

  /**
 * Path display name. Use name (section name) if present; otherwise road name and section ID,
 * or just the ID when unassigned to a road.
 */
  const roadId = hasRoad(pathData.road_id) ? pathData.road_id : null;
  const displayName =
    pathData.name !== ""
      ? pathData.name
      : roadId !== null
        ? t("path.titleInRoad", {
          road: roadDisplayName(roads.find((road) => road.road_id === roadId)?.name, roadId, t),
          id: pathData.path_id,
        })
        : t("path.titleWithId", { id: pathData.path_id });

  const showZoneRow = pathData.zone_name != null || canChangeZone;
  const showFollowTerrainRow = pathData.follow_terrain === 0 || pathData.follow_terrain === 1;
  const showColorRow = pathData.color != null;
  const hasBasicGrid = showZoneRow || showFollowTerrainRow || showColorRow;
  const hasCondition = hasPathCondition(pathData);

  return (
    <div className="agent-detail">
      <InfoHeader
        iconSrc={iconSrc}
        status={pathData.status}
        variantName={variantName}
        name={displayName}
        constructionProgress={pathData.construction_progress ?? undefined}
        onNameSave={handleNameSave}
        nameValue={pathData.name}
        onFocusCamera={() => void sendCameraFocus("path_detail", pathData.polygon).catch(console.error)}
      />

      {/*
 * Construction project tag and toggle to hide only this road in 3D.
 * Tags can be reassigned only before/during construction, so show the row only then (tags remain after completion,
 * and completed paths are still counted in the construction project window).
 */}
      {isTaggable("path", pathData.status) && (
        <TagRow apiBaseUrl={apiBaseUrl} kind="path" entityId={pathData.path_id} />
      )}

      {/* Which named road this section belongs to. Only streets and highways can belong to roads. */}
      {isNamedRoad && (
        <RoadMembershipRow
          apiBaseUrl={apiBaseUrl}
          pathId={pathData.path_id}
          roadId={roadId}
          roads={roads}
          onChanged={(next) =>
            setPathData((prev) => (prev ? { ...prev, road_id: next ?? VALUE_NONE } : prev))
          }
        />
      )}

      <PathOperationPanel
        path={pathData}
        apiBaseUrl={apiBaseUrl}
        onActionPerformed={() => setRefreshKey((k) => k + 1)}
        topologyEditMode={topologyEditMode}
        onStartTopologyEdit={(mode) =>
          // Pressing the same button again cancels the operation (also restores normal 3D mode).
          setTopologyEditMode((current) => (current === mode ? null : mode))
        }
      />

      {/*
 * During merge/split: section for 3D target selection, length review, and confirmation.
 * Remount with key on mode changes to avoid retaining selection state.
 */}
      {topologyEditMode && (
        <PathTopologyEditPanel
          key={topologyEditMode}
          apiBaseUrl={apiBaseUrl}
          path={pathData}
          mode={topologyEditMode}
          onClose={() => setTopologyEditMode(null)}
          onCompleted={() => setRefreshKey((k) => k + 1)}
        />
      )}

      {(() => {
        const hasShortage = pathData.alert ? (pathData.alert & ALERT_PATH_CONSTRUCTION_SHORTAGE) !== 0 : false;
        const hasAccident = pathData.alert ? (pathData.alert & ALERT_PATH_ACCIDENT) !== 0 : false;
        // Station path (platform) without a station building. PathData has no station_id-equivalent field,
        // so check only the alert bit (prompt placement from the track construction panel's station building tab).
        const hasNoStationHouse = pathData.alert ? (pathData.alert & ALERT_PATH_NO_STATION_HOUSE) !== 0 : false;
        const hasAlerts = hasShortage || hasAccident || hasNoStationHouse;
        if (!hasAlerts) return null;

        return (
          <div className="agent-detail-alerts">
            {hasShortage && (
              <div className="agent-detail-alert-card">
                <div className="agent-detail-alert-title">
                  <img src={shortageIcon} alt="shortage" className="agent-detail-alert-icon" />
                  {t("path.alerts.constructionShortage.title")}
                </div>
                <div className="agent-detail-alert-desc">
                  {t("path.alerts.constructionShortage.description")}
                </div>
              </div>
            )}
            {hasAccident && (
              <div className="agent-detail-alert-card">
                <div className="agent-detail-alert-title">
                  <img src={accidentIcon} alt="accident" className="agent-detail-alert-icon" />
                  {t("path.alerts.accident.title")}
                </div>
                <div className="agent-detail-alert-desc">
                  {t("path.alerts.accident.description")}
                </div>
              </div>
            )}
            {hasNoStationHouse && (
              <div className="agent-detail-alert-card">
                <div className="agent-detail-alert-title">
                  <img src={shortageIcon} alt="no station house" className="agent-detail-alert-icon" />
                  {t("path.alerts.noStationHouse.title")}
                </div>
                <div className="agent-detail-alert-desc">
                  {t("path.alerts.noStationHouse.description")}
                </div>
              </div>
            )}
          </div>
        );
      })()}

      {/* Warning banner for construction not started (on hold). */}
      {pathData.status === STATUS_PENDING && (
        <div className="pending-banner pending-banner--active">
          <span className="pending-banner__text">
            {t("path.pendingWarning", "この道路は計画中です。着工を指示してください。")}
          </span>
          <button
            className="pending-banner__btn"
            type="button"
            disabled={statusSaving}
            onClick={() => handleStatusToggle(true)}
          >
            <img src={rocketIcon} alt="rocket" />
            {t("path.action.startConstruction", "着工")}
          </button>
        </div>
      )}

      {/* Road under construction: construction speed (utilization_target) adjustment. */}
      {isConstructing && (
        <div className="agent-detail-utilization-target">
          <div className="agent-detail-utilization-target-header">
            <span className="agent-detail-utilization-target-title">
              {t("path_construction.constructionSpeed", "建設スピード")}
            </span>
            <span className="agent-detail-utilization-target-value">
              {constructionSpeed}%
            </span>
          </div>
          <div className="agent-detail-utilization-target-slider-container">
            <input
              type="range"
              min="0"
              max="500"
              value={constructionSpeed}
              onChange={(e) => setConstructionSpeed(Number(e.target.value))}
              onMouseUp={handleConstructionSpeedChange}
              onTouchEnd={handleConstructionSpeedChange}
              className="agent-detail-utilization-target-slider"
            />
          </div>
        </div>
      )}

      {tabs.length > 0 && (
        <div className="agent-detail-tabs path-detail-tabs">
          {tabs.map(tab => (
            <button key={tab.id} type="button"
              className={`agent-detail-tab${activeTab === tab.id ? " agent-detail-tab--active" : ""}`}
              aria-pressed={activeTab === tab.id}
              aria-controls={`path-detail-${tab.id}`}
              onClick={() => setSelectedTab(tab.id)}>
              {tab.label}
            </button>
          ))}
        </div>
      )}

      {/* Use hidden to switch visibility while retaining unsaved equipment inputs and speed-limit inputs awaiting application. */}
      {!isPreConstruction && (
        <>
          <div id="path-detail-basic" hidden={activeTab !== "basic"}>
            <div className="agent-detail-tab-content">
              {(hasBasicGrid || hasCondition) && (
                <section className="agent-detail-section">
                  <h3 className="agent-detail-section-title">{t("path.tabs.basic")}</h3>
                  {hasBasicGrid && (
                    <div className="agent-detail-grid">
                      {showZoneRow && (
                        <>
                          <span className="agent-detail-label">{t("path.zoneName")}</span>
                          <span className="agent-detail-value">
                            {canChangeZone ? (
                              <select
                                className="zone-change-select"
                                value={pathData.zone_id ?? ""}
                                disabled={zoneSaving}
                                onChange={(e) => handleZoneChange(Number(e.target.value))}
                              >
                                {zoneOptions.map((z) => (
                                  <option key={z.zone_id} value={z.zone_id}>
                                    {z.zone_name}
                                  </option>
                                ))}
                              </select>
                            ) : (
                              <ZoneLink
                                zoneId={pathData.zone_id}
                                zoneName={pathData.zone_name ?? ""}
                              />
                            )}
                          </span>
                        </>
                      )}
                      {/* Per-path terrain-following override. Show only when overriding the variant default (0/1). */}
                      {showFollowTerrainRow && (
                        <>
                          <span className="agent-detail-label">{t("path.followTerrain", "地形追従")}</span>
                          <span className="agent-detail-value">
                            {pathData.follow_terrain === 1
                              ? t("path.followTerrainFollow", "する(個別設定)")
                              : t("path.followTerrainNone", "しない(個別設定)")}
                          </span>
                        </>
                      )}
                      {showColorRow && <PathColorRow path={pathData} apiBaseUrl={apiBaseUrl} onSaved={setPathData} />}
                    </div>
                  )}
                  {zoneError && (
                    <div className="zone-change-error">{zoneError}</div>
                  )}
                  <PathConditionPanel path={pathData} apiBaseUrl={apiBaseUrl} />
                </section>
              )}

              <PathTrafficPanel
                key={pathData.path_id}
                path={pathData}
                apiBaseUrl={apiBaseUrl}
                onSaved={setPathData}
              />
            </div>
          </div>
          {hasEquipment && (
            <div id="path-detail-equipment" hidden={activeTab !== "equipment"}>
              <PathEquipmentPanel path={pathData} apiBaseUrl={apiBaseUrl} onSaved={setPathData} />
            </div>
          )}
        </>
      )}
      {isStationHouse && (
        <div id="path-detail-station" hidden={activeTab !== "station"}>
          <StationInfoSection apiBaseUrl={apiBaseUrl} pathId={pathData.path_id}
            passageMode={pathData.passage_mode}
            pathName={displayName} onRenamed={() => setRefreshKey(k => k + 1)} />
        </div>
      )}
      {showConstructionInfo && (
        <div id="path-detail-construction" hidden={activeTab !== "construction"}>
          <PathConstructionInfoPanel path={pathData} apiBaseUrl={apiBaseUrl} refreshKey={refreshKey} />
        </div>
      )}
      {scenarioMode && (
        <button
          className="property-edit-open-btn"
          type="button"
          onClick={() => {
            void invoke("open_registered_window", {
              windowType: "property_edit",
              params: { extra: { entityType: "path", entityId: String(pathData.path_id) } },
            });
          }}
        >
          {t("property_edit.openEditor", "Edit properties")}
        </button>
      )}
    </div>
  );
}
