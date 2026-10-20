/**
 * Parcel detail panel
 *
 * Dedicated panel in the window_type="parcel_detail" popup.
 * Displays parcel header (icon, status, name) and attributes.
 *
 * Editable fields:
 * - restriction_override: whether to override district building regulations individually.
 * - allowed_bd_mask: bitmask of buildable building types.
 * - limit_coverage_ratio: coverage limit (0-1 in API, % in inputs).
 * - limit_volume_ratio: volume ratio limit (0-1 in API, % in inputs).
 * - limit_height: height regulation.
 *
 * Regulation fields are enabled only when restriction_override=true.
 * The Save button sends changes in a PATCH request.
 */
import { PLAY_MODE_SCENARIO } from "../../constants";


import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { listen } from "@tauri-apps/api/event";
import { invoke } from "@tauri-apps/api/core";
import type { ParcelData } from "../../types";
import { ApiError, buildApiUrl, fetchApi } from "../../utils/api";
import { showNotification } from "../../utils/notification";
import { resolveParcelIcon } from "../../utils/parcelIcon";
import { usePlayMode } from "../../hooks/usePlayMode";
import { useMasterData } from "../../contexts/MasterDataContext";
import { InfoHeader } from "../common/InfoHeader";
import { ZoneLink } from "../common/ZoneLink";
import { PathLink } from "../common/PathLink";
import { AgentLink } from "../common/AgentLink";
import { ResourceCard } from "../common/ResourceCard";
import { RestrictionFields, type RestrictionValues } from "./RestrictionFields";
import { ParcelOperationPanel } from "./ParcelOperationPanel";
import { ParcelEnvironmentSection } from "./ParcelEnvironmentSection";
import {
  sendCameraFocus,
  sendEditParcelCancelShapeEdit,
  sendEditParcelFinishShapeEdit,
  sendShowParcelFrontPath,
  sendHideParcelFrontPath,
  sendViewApplyPreset,
  sendViewRestorePreset,
  VIEW_PRESET_PARCEL_DETAIL,
} from "../../utils/ipc";
import { IPC_EVENT_EDIT_PARCEL_UPDATE_SHAPE } from "../../utils/ipc";
import { STATUS_ACTIVE } from "../../constants";
import "./ParcelDetailPanel.css";

type ResourceListItem = {
  resource_id: number;
  product_id: number;
  max_amount: number;
  resource_amount: number;
  recovery_rate: number;
};

/**
 * Candidate street for changing front_path_id.
 * Corresponds to each entry returned by GET /api/v1/parcels/{id}/facing-paths.
 */
type FrontPathCandidate = {
  path_id: number;
  distance: number;
  name: string | null;
  zone_id: number | null;
  zone_name: string | null;
  is_current: boolean;
};

type ParcelDetailPanelProps = {
  apiBaseUrl: string;
  pathTemplate: string;
  initialEntityId: string;
};

export function ParcelDetailPanel({
  apiBaseUrl,
  pathTemplate,
  initialEntityId,
}: ParcelDetailPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const scenarioMode = usePlayMode(apiBaseUrl) === PLAY_MODE_SCENARIO;
  const [parcelData, setParcelData] = useState<ParcelData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [refreshKey, setRefreshKey] = useState(0);
  const [cityAgentId, setCityAgentId] = useState<number | null>(null);

  /** Whether shape editing mode is active. */
  const [isEditingShape, setIsEditingShape] = useState(false);
  /** Keep latest editing state for the unmount check. */
  const isEditingShapeRef = useRef(isEditingShape);
  /** Edited polygon reported by Unity. */
  const [editingPolygon, setEditingPolygon] = useState<number[][] | null>(null);

  /** restriction_override flag being edited. */
  const [restrictionOverride, setRestrictionOverride] = useState(false);

  /** Regulation field values being edited. */
  const [restrictionValues, setRestrictionValues] = useState<RestrictionValues>({
    allowed_bd_mask: 0,
    limit_coverage_ratio: null,
    limit_volume_ratio: null,
    limit_height: null,
  });

  // Regulation values are not sent while individual settings are disabled, so compare only values that would be saved.
  const restrictionsChanged = parcelData !== null && (
    restrictionOverride !== parcelData.restriction_override ||
    (restrictionOverride && (
      restrictionValues.allowed_bd_mask !== (parcelData.allowed_bd_mask ?? 0) ||
      restrictionValues.limit_coverage_ratio !== (parcelData.limit_coverage_ratio ?? null) ||
      restrictionValues.limit_volume_ratio !== (parcelData.limit_volume_ratio ?? null) ||
      restrictionValues.limit_height !== (parcelData.limit_height ?? null)
    ))
  );

  // Automatically enable parcel outlines in the 3D view while the panel is displayed.
  // Apply the view preset on mount and restore the previous display on unmount.
  // window.rs close handling also reliably restores it when the close button is used.
  useEffect(() => {
    void sendViewApplyPreset(VIEW_PRESET_PARCEL_DETAIL, "parcel_detail").catch(console.error);
    return () => {
      void sendViewRestorePreset(VIEW_PRESET_PARCEL_DETAIL, "parcel_detail").catch(console.error);
    };
  }, []);

  // While open, visualize the selected parcel's front_path as a blue arrow in Unity's 3D view.
  // Request display on mount and clearing on unmount. Unity derives the road-facing edge
  // from parcel_id and draws it (same lifecycle pattern as tutorial flags and shape-edit cancellation).
  useEffect(() => {
    const parcelId = Number.parseInt(initialEntityId, 10);
    if (!Number.isFinite(parcelId) || parcelId <= 0) return;
    void sendShowParcelFrontPath(parcelId);
    return () => {
      void sendHideParcelFrontPath();
    };
  }, [initialEntityId]);

  const url = buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId);

  const [allResources, setAllResources] = useState<ResourceListItem[] | null>(null);

  /** Candidate streets for changing the facing path. */
  const [frontPathCandidates, setFrontPathCandidates] = useState<FrontPathCandidate[]>([]);
  /** Facing path_id selected in the dropdown (null means unchanged from the current path). */
  const [selectedFrontPathId, setSelectedFrontPathId] = useState<number | null>(null);
  /** Whether a facing-path change is being saved. */
  const [frontPathSaving, setFrontPathSaving] = useState(false);
  /** Facing-path save error. */
  const [frontPathSaveError, setFrontPathSaveError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    fetchApi<{ resources: ResourceListItem[] }>(
      buildApiUrl(apiBaseUrl, "/api/v1/resources"),
      { signal: controller.signal }
    )
      .then((res) => {
        if (controller.signal.aborted) return;
        setAllResources(res.resources);
      })
      .catch((err: unknown) => {
        console.error("Failed to fetch resources in ParcelDetailPanel:", err);
      });
    return () => controller.abort();
  }, [apiBaseUrl, refreshKey]);

  // Fetch facing-path candidates (streets within the frontage threshold).
  // Refetch when parcel ID or refreshKey changes after saving; initialize selection to the current facing path.
  useEffect(() => {
    const controller = new AbortController();
    fetchApi<{ candidates: FrontPathCandidate[] }>(
      buildApiUrl(apiBaseUrl, `/api/v1/parcels/${initialEntityId}/facing-paths`),
      { signal: controller.signal }
    )
      .then((res) => {
        if (controller.signal.aborted) return;
        setFrontPathCandidates(res.candidates);
        setSelectedFrontPathId(null);
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        console.error("Failed to fetch front path candidates:", err);
        setFrontPathCandidates([]);
      });
    return () => controller.abort();
  }, [apiBaseUrl, initialEntityId, refreshKey]);

  function handleResourceClick(resourceId: number) {
    void invoke("open_registered_window", {
      windowType: "resource_detail",
      entityId: String(resourceId),
    });
  }

  /** Reflect the latest editing state in a Ref. */
  useEffect(() => {
    isEditingShapeRef.current = isEditingShape;
  }, [isEditingShape]);

  /**
 * On unmount, notify Unity to cancel editing if shape editing is active.
 * This prevents editing visuals such as vertices from remaining after the panel closes.
 */
  useEffect(() => {
    return () => {
      if (isEditingShapeRef.current) {
        void sendEditParcelCancelShapeEdit();
      }
    };
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetch(url, { signal: controller.signal })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<ParcelData>;
      })
      .then((data) => {
        if (!controller.signal.aborted) {
          setParcelData(data);
          setRestrictionOverride(data.restriction_override);
          setRestrictionValues({
            allowed_bd_mask: data.allowed_bd_mask ?? 0,
            limit_coverage_ratio: data.limit_coverage_ratio ?? null,
            limit_volume_ratio: data.limit_volume_ratio ?? null,
            limit_height: data.limit_height ?? null,
          });
          setLoading(false);
        }
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : "Unknown error");
          setLoading(false);
        }
      });

    return () => {
      controller.abort();
    };
  }, [url, refreshKey]);

  useEffect(() => {
    const controller = new AbortController();
    const base = apiBaseUrl.replace(/\/+$/, "");
    fetch(`${base}/api/v1/city/`, { signal: controller.signal })
      .then(async (res) => {
        if (!res.ok) return;
        const data = await res.json() as { city?: { city_agent_id?: number } };
        if (data.city?.city_agent_id !== undefined) {
          setCityAgentId(data.city.city_agent_id);
        }
      })
      .catch(() => {
        // Continue displaying even if fetching the city government agent ID fails.
      });

    return () => {
      controller.abort();
    };
  }, [apiBaseUrl]);

  useEffect(() => {
    const unlistenPromise = listen(IPC_EVENT_EDIT_PARCEL_UPDATE_SHAPE, (event) => {
      const payload = event.payload as {
        parcel_id?: number;
        polygon?: number[][];
      };
      if (payload.parcel_id === parcelData?.parcel_id && payload.polygon) {
        setEditingPolygon(payload.polygon);
      }
    });

    return () => {
      void unlistenPromise.then((unlisten) => unlisten());
    };
  }, [parcelData?.parcel_id]);

  /**
 * Save edits with a PATCH request.
 */
  async function handleSave() {
    if (!parcelData || saving || !restrictionsChanged) return;
    setSaving(true);
    setSaveError(null);

    const patchUrl = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/parcels/${parcelData.parcel_id}`;
    const body = {
      restriction_override: restrictionOverride,
      ...(restrictionOverride
        ? {
            allowed_bd_mask: restrictionValues.allowed_bd_mask,
            limit_coverage_ratio: restrictionValues.limit_coverage_ratio,
            limit_volume_ratio: restrictionValues.limit_volume_ratio,
            limit_height: restrictionValues.limit_height,
          }
        : {}),
    };

    try {
      const res = await fetch(patchUrl, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const updated = await res.json() as ParcelData;
      setParcelData(updated);
      setRestrictionOverride(updated.restriction_override);
      setRestrictionValues({
        allowed_bd_mask: updated.allowed_bd_mask ?? 0,
        limit_coverage_ratio: updated.limit_coverage_ratio ?? null,
        limit_volume_ratio: updated.limit_volume_ratio ?? null,
        limit_height: updated.limit_height ?? null,
      });
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : "Unknown error");
    } finally {
      setSaving(false);
    }
  }

  /**
 * Save a facing-path change with PATCH. Send only front_path_id, independently of regulation
 * saving (handleSave). Since the district may change too, advance refreshKey after success
 * to refetch district display and candidates.
 */
  async function handleSaveFrontPath() {
    if (!parcelData || selectedFrontPathId === null) return;
    setFrontPathSaving(true);
    setFrontPathSaveError(null);

    const patchUrl = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/parcels/${parcelData.parcel_id}`;
    try {
      const res = await fetch(patchUrl, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ front_path_id: selectedFrontPathId }),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const updated = await res.json() as ParcelData;
      setParcelData(updated);
      setSelectedFrontPathId(null);
      setRefreshKey((k) => k + 1);
    } catch (err) {
      setFrontPathSaveError(err instanceof Error ? err.message : "Unknown error");
    } finally {
      setFrontPathSaving(false);
    }
  }

  if (loading) {
    return <p className="agent-detail-muted">{t("common.loading")}</p>;
  }

  if (error || !parcelData) {
    return (
      <div className="agent-detail-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  const iconSrc = resolveParcelIcon(parcelData.parcel_type);

  /** Get the parcel type display name from master data. */
  const variantName = masterData?.parcel_types[parcelData.parcel_type]?.display_name;

  /** Generate parcel display names from IDs. */
  const displayName = t("parcel.titleWithId", { id: parcelData.parcel_id });

  // Determine facing-path presence by ID; default path names are empty, which would falsely appear absent.
  const hasFrontPath = parcelData.front_path_id != null && parcelData.front_path_id > 0;
  /** Facing-path display name. Unnamed paths use "Path #id". */
  const frontPathLabel = (id: number, name?: string | null) => name || t("path.titleWithId", { id });

  return (
    <div className="agent-detail">
      <InfoHeader
        iconSrc={iconSrc}
        status={parcelData.status ?? STATUS_ACTIVE}
        showStatus={false}
        variantName={variantName}
        name={displayName}
        onFocusCamera={() => void sendCameraFocus("parcel_detail", parcelData.polygon).catch(console.error)}
      />

      <ParcelOperationPanel
        parcel={parcelData}
        apiBaseUrl={apiBaseUrl}
        onActionPerformed={() => setRefreshKey((k) => k + 1)}
        cityAgentId={cityAgentId ?? -1}
        isEditingShape={isEditingShape}
        canConfirmEdit={editingPolygon != null && editingPolygon.length >= 3}
        onStartEditShape={() => setIsEditingShape(true)}
        onConfirmEditShape={async () => {
          if (!editingPolygon || editingPolygon.length < 3) return;
          // UI directly PATCHes shape changes to the backend, matching responsibilities in other construction panels.
          // On success, send finish_shape_edit so Unity resets editing state and redraws.
          try {
            await fetchApi(
              buildApiUrl(apiBaseUrl, `/api/v1/parcels/${parcelData.parcel_id}`),
              {
                method: "PATCH",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ polygon: editingPolygon }),
              }
            );
          } catch (err) {
            console.error("[ParcelDetail] shape edit confirm failed:", err);
            let message = t("parcel.shapeEditFailed", "形状の保存に失敗しました");
            let code: string | undefined;
            if (err instanceof ApiError) {
              message = err.message;
              code = err.code;
            } else if (err instanceof Error) {
              message = err.message;
            }
            const i18nKey = code ? `errors.${code}` : null;
            const displayMessage =
              i18nKey && i18nKey !== "errors.UNKNOWN_ERROR"
                ? t(i18nKey, { defaultValue: message })
                : message;
            void showNotification({ message: displayMessage, durationMs: 5000 });
            return;
          }
          await sendEditParcelFinishShapeEdit();
          setIsEditingShape(false);
          setEditingPolygon(null);
          setRefreshKey((k) => k + 1);
        }}
        onCancelEditShape={async () => {
          await sendEditParcelCancelShapeEdit();
          setIsEditingShape(false);
          setEditingPolygon(null);
        }}
      />

      {/*
 * Crossing path (underground/elevated) note.
 * Private buildings do not spawn on has_crossing_path=1 parcels, so explain why apparently
 * empty land remains undeveloped. Players may still build public facilities manually.
 */}
      {parcelData.has_crossing_path === 1 && (
        <div className="parcel-crossing-note">
          <span className="parcel-crossing-note-title">
            {t("parcel.crossingPathTitle", "地下/上空にパスあり")}
          </span>
          <span className="parcel-crossing-note-body">
            {t(
              "parcel.crossingPathBody",
              "この区画の地下または上空をパス（埋設インフラ・トンネル・高架など）が横断しています。民間の建物は建ちません（公共施設は建設できます）。"
            )}
          </span>
        </div>
      )}

      {/* Basic information section. */}
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">{t("parcel.basicInfo", "基本情報")}</h3>
        <div className="agent-detail-grid">
          {parcelData.zone_name && (
            <>
              <span className="agent-detail-label">{t("parcel.zoneName")}</span>
              <span className="agent-detail-value">
                <ZoneLink
                  zoneId={parcelData.zone_id}
                  zoneName={parcelData.zone_name}
                />
              </span>
            </>
          )}

          <span className="agent-detail-label">{t("parcel.landPrice")}</span>
          <span className="agent-detail-value">
            {t("common.currency", { value: parcelData.land_price })}
          </span>

          <span className="agent-detail-label">{t("parcel.area")}</span>
          <span className="agent-detail-value">
            {t("common.sqm", { value: Math.round(parcelData.area) })}
          </span>

          <span className="agent-detail-label">{t("parcel.altitude")}</span>
          <span className="agent-detail-value">
            {t("parcel.altitudeValue", { value: Math.round(parcelData.altitude * 10) / 10 })}
          </span>

          {(hasFrontPath || frontPathCandidates.length > 0) && (
            <>
              <span className="agent-detail-label">{t("parcel.frontPath")}</span>
              <span className="agent-detail-value">
                {(() => {
                  // If there are no candidates, fall back to the existing read-only link display.
                  if (frontPathCandidates.length === 0) {
                    return hasFrontPath ? (
                      <PathLink
                        pathId={parcelData.front_path_id}
                        pathName={frontPathLabel(parcelData.front_path_id, parcelData.front_path_name)}
                      />
                    ) : (
                      <span className="agent-detail-muted">
                        {t("parcel.frontPathNoCandidates")}
                      </span>
                    );
                  }

                  // If the current facing path is absent from the candidates, prepend its option
                  // so its selected state can still be displayed.
                  const currentId = parcelData.front_path_id;
                  const hasCurrent = frontPathCandidates.some((c) => c.path_id === currentId);
                  const options: FrontPathCandidate[] = [...frontPathCandidates];
                  if (hasFrontPath && !hasCurrent) {
                    options.unshift({
                      path_id: currentId,
                      distance: 0,
                      name: parcelData.front_path_name,
                      zone_id: parcelData.zone_id,
                      zone_name: parcelData.zone_name,
                      is_current: true,
                    });
                  }

                  const effectiveSelected = selectedFrontPathId ?? currentId ?? -1;

                  return (
                    <div className="parcel-front-path-edit">
                      <select
                        className="parcel-front-path-select"
                        value={effectiveSelected}
                        disabled={frontPathSaving}
                        onChange={(e) => {
                          const next = Number(e.target.value);
                          // Selecting the current facing path again restores unchanged state (null).
                          setSelectedFrontPathId(next === currentId ? null : next);
                          setFrontPathSaveError(null);
                        }}
                      >
                        {options.map((c) => {
                          const label = t("parcel.frontPathOption", {
                            name: frontPathLabel(c.path_id, c.name),
                            distance: Math.round(c.distance),
                          });
                          return (
                            <option key={c.path_id} value={c.path_id}>
                              {c.zone_name ? `${label} / ${c.zone_name}` : label}
                            </option>
                          );
                        })}
                      </select>
                      <button
                        type="button"
                        className="parcel-save-btn"
                        disabled={selectedFrontPathId === null || frontPathSaving}
                        onClick={() => void handleSaveFrontPath()}
                      >
                        {frontPathSaving
                          ? t("parcel.frontPathSaving")
                          : t("parcel.frontPathSave")}
                      </button>
                      {frontPathSaveError && (
                        <span className="parcel-save-error">
                          {t("parcel.frontPathSaveError")}
                        </span>
                      )}
                    </div>
                  );
                })()}
              </span>
            </>
          )}

          {parcelData.owner_agent_name && (
            <>
              <span className="agent-detail-label">{t("parcel.owner")}</span>
              <span className="agent-detail-value">
                <AgentLink
                  agentId={parcelData.owner_agent_id}
                  agentName={parcelData.owner_agent_name}
                  onNavigate={() => {
                    // Agent navigation within the parcel panel is not currently supported.
                  }}
                />
              </span>
            </>
          )}
        </div>
      </section>

      {/* Environment section (parcel-specific sunlight, air pollution, noise, crime). */}
      <ParcelEnvironmentSection
        apiBaseUrl={apiBaseUrl}
        parcelId={parcelData.parcel_id}
      />

      {/* Allocated natural resources section. */}
      {(() => {
        const assignedResourceIds = parcelData?.resource_id
          ? Object.values(parcelData.resource_id).filter((id) => id > 0)
          : [];
        const assignedResources = allResources
          ? allResources.filter((r) => assignedResourceIds.includes(r.resource_id))
          : [];

        if (assignedResources.length === 0) return null;

        return (
          <section className="agent-detail-section">
            <h3 className="agent-detail-section-title">
              {t("parcel.assignedResources", "割り当てられている天然資源")}
            </h3>
            <div className="parcel-resources-list">
              {assignedResources.map((r) => (
                <ResourceCard
                  key={r.resource_id}
                  resourceId={r.resource_id}
                  productId={r.product_id}
                  maxAmount={r.max_amount}
                  resourceAmount={r.resource_amount}
                  onClick={handleResourceClick}
                />
              ))}
            </div>
          </section>
        );
      })()}

      {/* Regulations section. */}
      <RestrictionFields
        title={t("parcel.restrictions", "建築規制")}
        values={restrictionValues}
        onChange={setRestrictionValues}
        disabled={!restrictionOverride || saving}
        onSave={() => void handleSave()}
        saving={saving}
        saveDisabled={!restrictionsChanged}
        saveError={saveError}
      >
        <div className="parcel-restriction-header">
          <label className="parcel-restriction-override-label">
            <input
              type="checkbox"
              className="parcel-restriction-override-check"
              checked={restrictionOverride}
              disabled={saving}
              onChange={(e) => setRestrictionOverride(e.target.checked)}
            />
            <span className="agent-detail-label">{t("parcel.restrictionOverride")}</span>
          </label>
        </div>
      </RestrictionFields>

      {scenarioMode && (
        <button
          className="property-edit-open-btn"
          type="button"
          onClick={() => {
            void invoke("open_registered_window", {
              windowType: "property_edit",
              params: { extra: { entityType: "parcel", entityId: String(parcelData.parcel_id) } },
            });
          }}
        >
          {t("property_edit.openEditor", "Edit properties")}
        </button>
      )}
    </div>
  );
}
