/**
 * Public transport creation/route editing panel
 *
 * Left-docked window_type="transit_creation" panel, matching BuildingConstructionPanel's
 * style/position. Creates public transport lines; editLineId switches to editing an existing
 * line's route points (opened through "Edit route" in line details).
 *
 * Lines are closed loops of stops/waypoints in visitation order, returning from the last stop
 * to the first. Round trips are loops listing outbound and return stops. On integrated double
 * track carrying both directions, both trips use the same stops; direction determines the track.
 * For two parallel single tracks, return stops use the opposite-facing platforms.
 * Vehicles may change direction only at their own stop platforms, regardless of traffic direction.
 *
 * New-line flow:
 * 1. Choose a line type in the dropdown; show its icon/description from transit_master.
 * 2. Type selection enters Unity STATE_EDIT_TRANSIT so stops/waypoints can be placed in 3D.
 *    Each point change arrives through edit_transit:update_points.
 * 3. Set fare, target headway, cars per train, and vehicle appearance (visual only; choices from
 *    TransitVehicleSelector). Once there are at least two stops, refetch TransitEstimateCard
 *    profitability estimates on point/settings changes.
 * 4. [Create] calls POST /api/v1/transit/lines with path_variant = transit_path_id of the selected
 *    type (section path variant), and points = visitation points received from Unity.
 * Route editing retains type/fare/etc. (the details settings tab owns those), gives Unity the
 * current points to edit, then sends new points via PUT /api/v1/transit/lines/{id}/route.
 *
 * Stops can be placed only on stop_path_variants paths (stations/streets/etc.); waypoints only on
 * running-network paths. Unity snaps accordingly using the supplied stop/route_path_variants.
 * LRT (VEHICLE_TRAM types) does not lay platforms as paths: after line creation, Unity draws
 * them automatically outside both tracks of the stop's underlying path (safety islands inside
 * street carriageways). Explain that platforms appear on creation because temporary stops have none.
 * On creation/update success, emit transit:lines_changed to refresh transit_management and line
 * details. Keep public transport visible while this panel is open.
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { emit, listen } from "@tauri-apps/api/event";
import type { TransitMasterEntry, TransitRoutePoint } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { setHintbarText } from "../../utils/hintbar";
import { showNotification } from "../../utils/notification";
import { resolveTransitIcon } from "../../utils/pathIcon";
import { useMasterData } from "../../contexts/MasterDataContext";
import { NumberStepper } from "../common/NumberStepper";
import {
  TransitVehicleSelector,
  defaultTransitVehicleModel,
  transitVehicleOptions,
} from "./TransitVehicleSelector";
import {
  TRANSIT_DEFAULT_FARE,
  TRANSIT_DEFAULT_HEADWAY_MIN,
  TRANSIT_DEFAULT_CARS_PER_TRAIN,
} from "../../local-constants";
import { PLAY_MODE_SCENARIO, STATUS_ACTIVE, VEHICLE_TRAM } from "../../constants";
import { usePlayMode } from "../../hooks/usePlayMode";
import { useTransitEstimate } from "../../hooks/useTransitEstimate";
import { TransitEstimateCard } from "./TransitEstimateCard";
import {
  sendChangeState,
  sendViewApplyPreset,
  sendEditTransitFinish,
  VIEW_PRESET_TRANSIT,
  IPC_EVENT_EDIT_TRANSIT_UPDATE_POINTS,
} from "../../utils/ipc";
// Use the same design (text sizes, cards, selects) as the building construction panel,
// sharing its bc-* style definitions.
import "../building/BuildingConstructionPanel.css";

/** edit_transit:update_points payload. Visitation points in traversal order. */
type TransitPointsPayload = {
  points: TransitRoutePoint[];
};

/** Line being route-edited (only required fields from GET /api/v1/transit/lines/{id}). */
type EditedLine = {
  path_variant: number;
  fare_price: number;
  target_headway: number;
  cars_per_train: number;
  route_points: TransitRoutePoint[];
};

/** Line creation/route update response fields used for suspension warnings. */
type RouteSaveResponse = { status?: number; pause_reason?: string | null };

export function TransitCreationPanel({
  apiBaseUrl,
  onClose,
  editLineId,
}: {
  apiBaseUrl: string;
  onClose: () => void;
  /** line_agent_id of the existing line to route-edit. Omit for new line creation. */
  editLineId?: number | null;
}) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const scenarioMode = usePlayMode(apiBaseUrl) === PLAY_MODE_SCENARIO;
  const isEdit = editLineId != null;

  // Selected line type (transit_master key). Unused in route editing, where the line determines its type.
  const [selectedTransitId, setSelectedTransitId] = useState<number | null>(null);
  const [editedLine, setEditedLine] = useState<EditedLine | null>(null);
  const [fare, setFare] = useState<number>(TRANSIT_DEFAULT_FARE);
  const [headway, setHeadway] = useState<number>(TRANSIT_DEFAULT_HEADWAY_MIN);
  // cars_per_train. If train length = cars_per_train × individual car length (type master) exceeds
  // the stop's underlying path length (station platform or road/track section between intersections), the line is suspended.
  const [carsPerTrain, setCarsPerTrain] = useState<number>(TRANSIT_DEFAULT_CARS_PER_TRAIN);
  // Vehicle appearance (vehicle_model). Options differ by type, so changing type resets to its first option.
  const [vehicleModel, setVehicleModel] = useState<number>(0);
  // Visitation points being placed in Unity (graphics).
  const [points, setPoints] = useState<TransitRoutePoint[]>([]);
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const stopCount = points.filter((p) => p.kind === "stop").length;
  const waypointCount = points.length - stopCount;

  /** Line types from transit_master. Types whose line paths are template_only appear only in scenario authoring mode. */
  const transitEntries = useMemo(() => {
    if (!masterData) return [];
    return (Object.entries(masterData.transit) as unknown as [string, TransitMasterEntry][])
      .filter(([, entry]) => scenarioMode || !masterData.paths[entry.transit_path_id]?.template_only);
  }, [masterData, scenarioMode]);

  /** Master data for the selected line type (or the edited line's type during route editing). */
  const selectedEntry: TransitMasterEntry | null = useMemo(() => {
    if (!masterData) return null;
    if (isEdit) {
      if (!editedLine) return null;
      return transitEntries.find(([, e]) => e.transit_path_id === editedLine.path_variant)?.[1] ?? null;
    }
    if (selectedTransitId === null) return null;
    return masterData.transit[selectedTransitId] ?? null;
  }, [isEdit, editedLine, selectedTransitId, masterData, transitEntries]);

  useEffect(() => {
    void setHintbarText(t("hint.transitCreation"));
    return () => {
      void setHintbarText("");
    };
  }, [t]);

  // While open, use a public transport 3D view (all lines plus planned roads/buildings).
  // STATE_EDIT_TRANSIT applies the same preset on type selection, but existing lines must
  // already be visible before selection, so also send on mount.
  // STATE_NORMAL on window close restores the previous view.
  useEffect(() => {
    void sendViewApplyPreset(VIEW_PRESET_TRANSIT, "transit_creation").catch(console.error);
  }, []);

  // For route editing, fetch the target line's type, settings, and current visitation points.
  useEffect(() => {
    if (!isEdit) return;
    const controller = new AbortController();
    fetchApi<EditedLine>(buildApiUrl(apiBaseUrl, `/api/v1/transit/lines/${editLineId}`), {
      signal: controller.signal,
    })
      .then((line) => {
        if (!controller.signal.aborted) setEditedLine(line);
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) setError(err instanceof Error ? err.message : String(err));
      });
    return () => controller.abort();
  }, [apiBaseUrl, isEdit, editLineId]);

  // Once type is determined, put Unity in line placement mode (STATE_EDIT_TRANSIT).
  // Pass stop constraints (stop_path_variants) and running network (route_path_variants) so Unity can
  // filter snapping targets. New-line type changes reset visitation points and vehicle appearance. Route edits
  // pass current points for editing and hide the edited line's existing color band (line_agent_id).
  useEffect(() => {
    if (!selectedEntry) return;
    const initialPoints = isEdit && editedLine ? editedLine.route_points : [];
    setPoints(initialPoints);
    if (!isEdit) {
      setVehicleModel(defaultTransitVehicleModel(selectedEntry.transit_path_id));
      setCarsPerTrain((cars) => Math.max(selectedEntry.freight ? 2 : 1, cars));
    }
    void sendChangeState(
      "STATE_EDIT_TRANSIT",
      {
        path_variant: selectedEntry.transit_path_id,
        stop_path_variants: selectedEntry.stop_path_variants,
        route_path_variants: selectedEntry.route_path_variants,
        points: initialPoints.map((p) => ({ kind: p.kind, position: p.position })),
        line_agent_id: isEdit ? editLineId : null,
      },
      "transit_creation",
    );
  }, [selectedEntry, isEdit, editedLine, editLineId]);

  // Return to normal mode and clear line placement gizmos on close (unmount).
  useEffect(() => {
    return () => {
      void sendChangeState("STATE_NORMAL", {}, "transit_creation");
    };
  }, []);

  // Receive visitation points being placed in Unity and update state.
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    listen<TransitPointsPayload>(IPC_EVENT_EDIT_TRANSIT_UPDATE_POINTS, (event) => {
      setPoints(event.payload?.points ?? []);
    })
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);
    return () => {
      unlisten?.();
    };
  }, []);

  // Route edit estimates use the line's current fare, headway, and cars-per-train settings.
  const estimateFare = isEdit ? editedLine?.fare_price ?? 0 : fare;
  const estimateHeadway = isEdit ? editedLine?.target_headway ?? TRANSIT_DEFAULT_HEADWAY_MIN : headway;
  const estimateCars = isEdit ? editedLine?.cars_per_train ?? 1 : carsPerTrain;

  // Two stops determine the underlying route (loop distance/time), enabling vehicle requirement and profitability estimates.
  const { estimate, loading: estimateLoading, error: estimateError } = useTransitEstimate(
    apiBaseUrl,
    selectedEntry !== null && stopCount >= 2
      ? {
          kind: "planned",
          pathVariant: selectedEntry.transit_path_id,
          points,
          farePrice: estimateFare,
          targetHeadway: estimateHeadway,
          carsPerTrain: estimateCars,
          ...(isEdit ? { editedLineId: editLineId as number } : {}),
        }
      : null,
  );

  const handleConfirm = useCallback(async () => {
    if (selectedEntry === null) return;
    if (stopCount < 2) {
      setError(t("transit.needTwoStops"));
      return;
    }
    setIsSaving(true);
    setError(null);
    try {
      const saved = isEdit
        ? await fetchApi<RouteSaveResponse>(buildApiUrl(apiBaseUrl, `/api/v1/transit/lines/${editLineId}/route`), {
            method: "PUT",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ points }),
          })
        : await fetchApi<RouteSaveResponse>(buildApiUrl(apiBaseUrl, "/api/v1/transit/lines"), {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              // Specify line type using the section path variant (transit_path_id in transit_master).
              path_variant: selectedEntry.transit_path_id,
              fare_price: fare,
              target_headway: headway,
              cars_per_train: carsPerTrain,
              vehicle_model: vehicleModel,
              points,
            }),
          });
      await emit("transit:lines_changed", {});
      // End Unity line placement mode and reload data to apply the new route.
      await sendEditTransitFinish();
      // Successful creation/update may still suspend a line if stops lack underlying connectivity. Report a warning instead of success.
      const paused = saved != null && saved.status != null && saved.status !== STATUS_ACTIVE;
      const message = paused
        ? t(isEdit ? "transit.routeUpdatedButSuspended" : "transit.createdButSuspended")
        : t(isEdit ? "transit.routeUpdateSuccess" : "transit.createSuccess");
      await showNotification({ message, durationMs: paused ? 5000 : 3000 }).catch(() => {});
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save line");
    } finally {
      setIsSaving(false);
    }
  }, [apiBaseUrl, isEdit, editLineId, selectedEntry, fare, headway, carsPerTrain, vehicleModel, points, stopCount, onClose, t]);

  if (!masterData) {
    return <p className="bc-muted">{t("common.loading")}</p>;
  }

  // Use fallback text if master description is unset ("nan" or empty).
  const description =
    selectedEntry && selectedEntry.description && selectedEntry.description !== "nan"
      ? selectedEntry.description
      : "—";

  return (
    <div className="bc-layout">
      {/* Line type dropdown. Route editing cannot change the type. */}
      {!isEdit && (
        <div className="bc-section">
          {transitEntries.length === 0 ? (
            <div className="bc-empty-variants">{t("transit.noVariants")}</div>
          ) : (
            <select
              className="bc-select"
              data-guide="transit.type"
              value={selectedTransitId ?? ""}
              onChange={(e) => setSelectedTransitId(e.target.value ? parseInt(e.target.value) : null)}
            >
              <option value="">{t("transit.selectMode")}</option>
              {transitEntries.map(([idStr, entry]) => (
                <option key={idStr} value={idStr}>
                  {entry.display_name}
                </option>
              ))}
            </select>
          )}
        </div>
      )}

      {/* Type icon/description card (icon resolved from the section path variant). */}
      {selectedEntry && (
        <div className="bc-section">
          <div className="bc-description-frame">
            <img
              src={resolveTransitIcon(selectedEntry.transit_path_id)}
              alt={selectedEntry.display_name}
              className="bc-description-icon"
              draggable={false}
            />
            <div className="bc-description-body">
              <div className="bc-description-name">{selectedEntry.display_name}</div>
              <div className="bc-description-text">{description}</div>
            </div>
          </div>
        </div>
      )}

      {/* Vehicle appearance (omit for types without rendered vehicles; route edits change this in line detail settings). */}
      {!isEdit && selectedEntry && transitVehicleOptions(selectedEntry.transit_path_id).length > 0 && (
        <div className="bc-section">
          <div className="bc-section-title">{t("transit.vehicleModel")}</div>
          <TransitVehicleSelector
            pathVariant={selectedEntry.transit_path_id}
            value={vehicleModel}
            onChange={setVehicleModel}
            disabled={isSaving}
          />
          <div className="bc-description-text" style={{ marginTop: 6, opacity: 0.8 }}>
            {t("transit.vehicleModelNote")}
          </div>
        </div>
      )}

      {/* Fare/headway (new lines only) and counts of stops/waypoints being placed. */}
      {selectedEntry && (
        <div className="bc-section">
          {!isEdit && (
            <>
              <div className="bc-split-row">
                <div className="bc-split-col">
                  <div className="bc-section-title">{t("transit.fare")}</div>
                  <div className="bc-stepper-row" data-guide="transit.fare">
                    <NumberStepper value={fare} onChange={setFare} min={0} max={100000} step={10} integerOnly={true} />
                    <span className="bc-stepper-unit">{t("transit.currencyUnit")}</span>
                  </div>
                </div>
                <div className="bc-split-col">
                  <div className="bc-section-title">{t("transit.headway")}</div>
                  <div className="bc-stepper-row" data-guide="transit.headway">
                    <NumberStepper value={headway} onChange={setHeadway} min={1} max={120} step={1} integerOnly={true} />
                    <span className="bc-stepper-unit">{t("transit.minutesUnit")}</span>
                  </div>
                </div>
              </div>
              <div className="bc-split-row" style={{ marginTop: 8 }}>
                <div className="bc-split-col">
                  <div className="bc-section-title">{t("transit.carsPerTrain")}</div>
                  <div className="bc-stepper-row">
                    <NumberStepper value={carsPerTrain} onChange={setCarsPerTrain} min={selectedEntry.freight ? 2 : 1} max={20} step={1} integerOnly={true} />
                    <span className="bc-stepper-unit">{t("transit.carsUnit")}</span>
                  </div>
                  {selectedEntry.freight && <p className="bc-description-text">{t("transit.freightConsistNote")}</p>}
                </div>
              </div>
            </>
          )}
          <div className="bc-split-row" style={{ marginTop: isEdit ? 0 : 8 }}>
            <div className="bc-split-col">
              <div className="bc-section-title">{t("transit.stopCountLabel")}</div>
              <div className="bc-stepper-row">
                <span className="bc-description-name">{stopCount}</span>
                <span className="bc-stepper-unit">{t("transit.stopsUnit")}</span>
              </div>
            </div>
            <div className="bc-split-col">
              <div className="bc-section-title">{t("transit.waypointCountLabel")}</div>
              <div className="bc-stepper-row">
                <span className="bc-description-name">{waypointCount}</span>
                <span className="bc-stepper-unit">{t("transit.stopsUnit")}</span>
              </div>
            </div>
          </div>
          {!isEdit && selectedEntry.vehicle_type === VEHICLE_TRAM && (
            <div className="bc-description-text" style={{ marginTop: 8, opacity: 0.8 }}>
              {t("transit.autoPlatformNote")}
            </div>
          )}
        </div>
      )}

      {/* Profitability estimate (instructions only until two stops exist). */}
      {selectedEntry && (
        <div className="bc-section">
          <div className="bc-section-title">{t("transit.estimate.title")}</div>
          <TransitEstimateCard
            estimate={estimate}
            loading={estimateLoading}
            error={estimateError}
            placeholder={t("transit.estimate.placeholder")}
          />
        </div>
      )}

      {error && (
        <div className="bc-section">
          <div className="bc-description-text" style={{ color: "#fca5a5" }}>{error}</div>
        </div>
      )}

      {/* Action buttons (fixed at panel bottom). */}
      {selectedEntry && (
        <div className="bc-section bc-action-bar">
          <div className="bc-actions">
            <button className="bc-btn" type="button" data-guide="transit.cancel" onClick={onClose}>
              {t("transit.cancel")}
            </button>
            <button
              className="bc-btn bc-btn-confirm"
              type="button"
              data-guide="transit.confirm"
              disabled={isSaving || stopCount < 2}
              onClick={() => void handleConfirm()}
            >
              {isSaving ? t("common.processing") : t(isEdit ? "transit.confirmRouteUpdate" : "transit.confirmCreate")}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
