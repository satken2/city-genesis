/**
 * Public/publicly operated facility control panel
 *
 * Operation card at the top of detail panels for public facilities (public_facility=true).
 * Extends the area formerly dedicated to target utilization into a combined control panel:
 *
 * - Operation buttons: repair/reconstruct, stop/start toggle, abandonment toggle, bulldoze.
 *   Planned facilities instead get delete/edit. Stop/start is one button whose icon toggles.
 * - Target utilization ("Construction speed" during construction, "Target utilization" for steady
 *   production). An icon/current-value button at the right of the same row expands the slider only
 *   when clicked. Show "actual / target utilization" in the button and actual utilization below
 *   the slider. Construction speed has no corresponding actual value, so shows only its setting.
 *
 * Abandonment means giving up maintenance; while abandoned, disable everything except ending
 * abandonment, since repair, stop/start, and bulldoze operate on maintained facilities.
 *
 * Non-building public facilities (e.g. public service operators) cannot be abandoned, repaired,
 * or stopped/started, so show only bulldoze (delete while planned).
 * Earthworks (isGradingVariant) have no bulldoze/edit actions; cancel by holding and then deleting.
 * The city government (TYPE_CITY) is permanent and has no operations, so omit the card itself.
 */

import { useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import type { AgentData, AgentResponse } from "../../types";
import {
  STATUS_ACTIVE,
  STATUS_PAUSED,
  STATUS_TEMPORARY,
  STATUS_SCHEDULED_CONSTRUCTION,
  STATUS_SCHEDULED_DEMOLITION,
  STATUS_COLLAPSED,
  STATUS_CONSTRUCTING,
  STATUS_PENDING,
  TYPE_CITY,
} from "../../constants";
import { fetchApi } from "../../utils/api";
import { sendSessionReload } from "../../utils/ipc";
import { useMasterData } from "../../contexts/MasterDataContext";
import { isGradingVariant } from "../grading/grading";
import bulldozeIcon from "../../assets/images/bulldoze.png";
import pauseIcon from "../../assets/images/ui/pause.png";
import playIcon from "../../assets/images/ui/play.png";
import trashIcon from "../../assets/images/ui/trash.png";
import repairIcon from "../../assets/images/ui/repair.png";
import editIcon from "../../assets/images/ui/edit.png";
import rocketIcon from "../../assets/images/ui/rocket.png";
import pendingIcon from "../../assets/images/ui/pending.png";
import abandonIcon from "../../assets/images/alerts/rubble.png";
import "./AgentOperationPanel.css";
import "./PublicFacilityPanel.css";

type PublicFacilityPanelProps = {
  agent: AgentData;
  apiBaseUrl: string;
  /** Callback to refetch the panel after an operation. */
  onActionPerformed: () => void;
  /** Target utilization (0-500%). The parent holds the value being edited. */
  utilizationTarget: number;
  /** Update the value while dragging the slider. */
  setUtilizationTarget: (value: number) => void;
  /** Apply to the server when the slider is committed (mouseUp/touchEnd). */
  onUtilizationCommit: () => void;
  /** Construction cost per epoch for a building under construction (null otherwise). */
  constructionCostPerEpoch: number | null;
  /** Remaining construction time in epochs for a building under construction (null otherwise). */
  remainingEpochs: number | null;
};

const PRE_CONSTRUCTION = new Set([STATUS_TEMPORARY, STATUS_SCHEDULED_CONSTRUCTION, STATUS_PENDING]);

/** Meter icon for target utilization/construction speed. Use inline SVG until a dedicated image is available. */
function UtilizationIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path
        d="M4 17a8 8 0 1 1 16 0"
        fill="none"
        stroke="currentColor"
        strokeWidth="2.2"
        strokeLinecap="round"
      />
      <path
        d="M12 17 16.5 11.5"
        fill="none"
        stroke="currentColor"
        strokeWidth="2.2"
        strokeLinecap="round"
      />
      <circle cx="12" cy="17" r="1.7" fill="currentColor" />
    </svg>
  );
}

async function openBuildingEdit(agentId: number) {
  await invoke("open_registered_window", {
    windowType: "building_construction",
    params: { extra: { editAgentId: agentId } },
  });
}

export function PublicFacilityPanel({
  agent,
  apiBaseUrl,
  onActionPerformed,
  utilizationTarget,
  setUtilizationTarget,
  onUtilizationCommit,
  constructionCostPerEpoch,
  remainingEpochs,
}: PublicFacilityPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const [loading, setLoading] = useState(false);
  // Expansion state of the target utilization slider. Closed by default; opens only when its value is clicked.
  const [sliderOpen, setSliderOpen] = useState(false);

  const base = apiBaseUrl.replace(/\/+$/, "");

  // The city government (TYPE_CITY) is permanent and has no operations, so omit the card.
  if (agent.type === TYPE_CITY) {
    return null;
  }

  const agentUrl = `${base}/api/v1/agents/${agent.agent_id}`;

  async function patchAgent(body: Record<string, unknown>) {
    setLoading(true);
    try {
      await fetchApi<AgentResponse>(agentUrl, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      onActionPerformed();
    } catch (err) {
      console.error("Operation failed:", err);
    } finally {
      setLoading(false);
    }
  }

  // Delete a planned facility (on hold or scheduled to start). Its 3D object remains in Unity after
  // backend deletion until data is refreshed, so send session:reload after successful deletion
  // to synchronize changes (Unity destroys buildings absent from the fetched result).
  // Refetching a deleted agent would only produce a 404, so do not call onActionPerformed;
  // close the window instead.
  async function handleDelete() {
    setLoading(true);
    try {
      await fetchApi(agentUrl, { method: "DELETE" });
      await sendSessionReload();
      try {
        await invoke("close_current_window");
      } catch {
        // Ignore if the window is already closed.
      }
    } catch (err) {
      console.error("Delete failed:", err);
    } finally {
      setLoading(false);
    }
  }

  async function handleBulldoze() {
    // Toggle off scheduled demolition if already set; otherwise schedule demolition.
    if (agent.status === STATUS_SCHEDULED_DEMOLITION) {
      await patchAgent({ status: STATUS_ACTIVE });
    } else {
      await patchAgent({ status: STATUS_SCHEDULED_DEMOLITION });
    }
  }

  // Stop/start toggle. Stop if active; resume if stopped.
  async function handleToggleActive() {
    if (agent.status === STATUS_PAUSED) {
      await patchAgent({ status: STATUS_ACTIVE });
    } else {
      await patchAgent({ status: STATUS_PAUSED });
    }
  }

  // Start/hold toggle. Switch a start instruction to hold, or a hold to start.
  async function handleToggleStatus() {
    const isPlannedOrConstructing = status === STATUS_SCHEDULED_CONSTRUCTION || status === STATUS_CONSTRUCTING;
    const newStatus = isPlannedOrConstructing ? STATUS_PENDING : STATUS_SCHEDULED_CONSTRUCTION;
    await patchAgent({ status: newStatus });
  }

  async function handleRepair() {
    await patchAgent({ under_maintenance: true });
  }

  // Reconstruct a collapsed building in place (dedicated endpoint).
  async function handleReconstruct() {
    setLoading(true);
    try {
      await fetchApi(`${agentUrl}/reconstruct`, { method: "POST" });
      onActionPerformed();
    } catch (err) {
      console.error("Reconstruct failed:", err);
    } finally {
      setLoading(false);
    }
  }

  // Toggle abandonment. Abandoning an active facility stops it (STATUS_PAUSED);
  // the backend performs the status transition.
  async function handleAbandonToggle(next: boolean) {
    await patchAgent({ abandoned: next });
  }

  const status = agent.status;
  const isPreConstruction = PRE_CONSTRUCTION.has(status);
  const isCollapsed = status === STATUS_COLLAPSED;
  const isBuilding = agent.is_bd;
  const isAbandoned = !!agent.abandoned;
  // Earthworks agents disappear on completion and cannot be bulldozed. Their area cannot be edited in building construction; delete and reorder in the earthworks window to change it.
  const isGrading = isGradingVariant(agent.variant);
  // Active/stopped facilities allow abandonment, repair, and stop/start toggles.
  const isRunningState = status === STATUS_ACTIVE || status === STATUS_PAUSED;
  const isPaused = status === STATUS_PAUSED;
  // Abandoned facilities reject actions for maintained facilities (repair, stop/start, bulldoze, start construction).
  const lockedByAbandon = loading || isAbandoned;

  // Display conditions for the target utilization slider (same as the previous card).
  // During construction: "Construction speed" (up to 500%); spare-capacity (>0) or fixed-quantity (<0) facilities: "Target utilization" (up to 100%).
  const agentMaster = masterData?.agents[agent.variant];
  const isHeadroomFacility = (agentMaster?.production_headroom ?? 0) !== 0;
  const isConstructing = status === STATUS_CONSTRUCTING;
  const showSlider = isConstructing || isHeadroomFacility;
  const sliderMax = isConstructing ? 500 : 100;
  const actualUtilization = agent.utilization?.[0];
  const actualUtilizationText = actualUtilization != null ? `${Math.round(actualUtilization * 100)}%` : "—";

  return (
    <div className="pf-panel">
      <div className="pf-panel__title">
        {t("agent.publicFacilityPanel.title", "公共/公営施設")}
      </div>

      <div className="operation-panel">
        {isPreConstruction ? (
          <>
            {isBuilding && (status === STATUS_PENDING || status === STATUS_SCHEDULED_CONSTRUCTION) && (
              <button
                className="operation-btn"
                type="button"
                title={status === STATUS_PENDING ? t("agent.action.startConstruction") : t("agent.action.stopConstruction")}
                disabled={loading}
                onClick={() => void handleToggleStatus()}
              >
                <img
                  src={status === STATUS_PENDING ? rocketIcon : pendingIcon}
                  alt="toggle construction"
                />
              </button>
            )}
            {isBuilding && !isGrading && (
              <button
                className="operation-btn"
                type="button"
                title={t("agent.action.edit")}
                disabled={loading}
                onClick={() => void openBuildingEdit(agent.agent_id)}
              >
                <img src={editIcon} alt={t("agent.action.edit")} />
              </button>
            )}
            <button
              className="operation-btn operation-btn--delete"
              type="button"
              title={t("agent.action.delete")}
              disabled={loading}
              onClick={() => void handleDelete()}
            >
              <img src={trashIcon} alt={t("agent.action.delete")} />
            </button>
          </>
        ) : (
          <>
            {/* Show "Reconstruct" rather than repair when collapsed (mutually exclusive). */}
            {isCollapsed ? (
              <button
                className="operation-btn"
                type="button"
                title={t("agent.action.rebuild")}
                disabled={lockedByAbandon || !isBuilding}
                onClick={() => void handleReconstruct()}
              >
                <img src={repairIcon} alt={t("agent.action.rebuild")} />
              </button>
            ) : (
              <button
                className="operation-btn"
                type="button"
                title={t("agent.action.repair")}
                disabled={
                  lockedByAbandon ||
                  !isBuilding ||
                  !isRunningState ||
                  !!agent.under_maintenance
                }
                onClick={() => void handleRepair()}
              >
                <img src={repairIcon} alt={t("agent.action.repair")} />
              </button>
            )}

            {/* Construction start toggle (start instructed / under construction). */}
            {isBuilding && status === STATUS_CONSTRUCTING && (
              <button
                className="operation-btn"
                type="button"
                title={t("agent.action.stopConstruction")}
                disabled={lockedByAbandon}
                onClick={() => void handleToggleStatus()}
              >
                <img src={pendingIcon} alt={t("agent.action.stopConstruction")} />
              </button>
            )}

            {/* Stop/start toggle, buildings only. Disabled while abandoned; end abandonment first. */}
            {isBuilding && (
              <button
                className="operation-btn"
                type="button"
                title={
                  isPaused ? t("agent.action.resume") : t("agent.action.pause")
                }
                disabled={lockedByAbandon || !isRunningState}
                onClick={() => void handleToggleActive()}
              >
                <img
                  src={isPaused ? playIcon : pauseIcon}
                  alt={
                    isPaused
                      ? t("agent.action.resume")
                      : t("agent.action.pause")
                  }
                />
              </button>
            )}

            {/*
 * Abandonment toggle, buildings only. Other operation buttons are disabled when pressed (abandoned),
 * so this action alone must remain available while abandoned.
 */}
            {isBuilding && (
              <button
                className={[
                  "operation-btn",
                  isAbandoned && "operation-btn--active",
                ]
                  .filter(Boolean)
                  .join(" ")}
                type="button"
                aria-pressed={isAbandoned}
                title={
                  isAbandoned
                    ? t("agent.action.cancelAbandon")
                    : `${t("agent.abandon")}(${t("agent.abandonHint")})`
                }
                disabled={loading || !isRunningState}
                onClick={() => void handleAbandonToggle(!isAbandoned)}
              >
                <img src={abandonIcon} alt={t("agent.abandon")} />
              </button>
            )}

            {!isGrading && (
              <button
                className="operation-btn"
                type="button"
                title={
                  status === STATUS_SCHEDULED_DEMOLITION
                    ? t("agent.action.cancelBulldoze")
                    : t("agent.action.bulldoze")
                }
                disabled={lockedByAbandon}
                onClick={() => void handleBulldoze()}
              >
                <img src={bulldozeIcon} alt={t("agent.action.bulldoze")} />
              </button>
            )}
          </>
        )}

        {/*
 * Target utilization. Align to the right of the button row, showing only icon and current value.
 * No caption; expand the slider on click.
 */}
        {showSlider && (
          <button
            type="button"
            className="operation-btn operation-btn--util"
            aria-expanded={sliderOpen}
            title={
              isConstructing
                ? t("agent.constructionSpeed", "建設スピード")
                : t("agent.utilizationTarget", "稼働率目標")
            }
            onClick={() => setSliderOpen((open) => !open)}
          >
            <UtilizationIcon />
            <span className="operation-btn__util-value">
              {isConstructing
                ? `${utilizationTarget}%`
                : `${actualUtilizationText} / ${utilizationTarget}%`}
            </span>
          </button>
        )}
      </div>

      {/*
 * Show the caption only while the slider is expanded (the button tooltip serves this role while
 * collapsed). Place it left of the slider to identify the control.
 */}
      {showSlider && sliderOpen && (
        <div className="pf-panel__util-slider">
          <span className="agent-detail-utilization-target-title">
            {isConstructing
              ? t("agent.constructionSpeed", "建設スピード")
              : t("agent.utilizationTarget", "稼働率目標")}
          </span>
          <input
            type="range"
            min="0"
            max={sliderMax}
            value={utilizationTarget}
            onChange={(e) => setUtilizationTarget(Number(e.target.value))}
            onMouseUp={onUtilizationCommit}
            onTouchEnd={onUtilizationCommit}
            className="agent-detail-utilization-target-slider"
          />
          {!isConstructing && (
            <>
              <span className="agent-detail-utilization-target-title">
                {t("agent.currentUtilization")}
              </span>
              <span className="pf-panel__util-current">{actualUtilizationText}</span>
            </>
          )}
        </div>
      )}

      {isConstructing && (
        <div className="agent-detail-grid">
          <span className="agent-detail-label">
            {t("agent.constructionCostPerEpoch", "1エポックの建設コスト")}
          </span>
          <span className="agent-detail-value">
            {constructionCostPerEpoch != null
              ? t("common.currency", {
                  value: Math.round(constructionCostPerEpoch),
                })
              : "—"}
          </span>
          <span className="agent-detail-label">
            {t("agent.remainingConstruction", "残り建設期間")}
          </span>
          <span className="agent-detail-value">
            {remainingEpochs != null
              ? t("agent.remainingEpochs", {
                  defaultValue: "約{{n}}エポック",
                  n: Math.ceil(remainingEpochs),
                })
              : "—"}
          </span>
        </div>
      )}
    </div>
  );
}
