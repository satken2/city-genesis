import { requireApiSuccess } from "../../utils/api";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import type { AgentData } from "../../types";
import {
  STATUS_ACTIVE,
  STATUS_PAUSED,
  STATUS_TEMPORARY,
  STATUS_SCHEDULED_CONSTRUCTION,
  STATUS_SCHEDULED_DEMOLITION,
  STATUS_COLLAPSED,
  STATUS_PENDING,
  TYPE_DEFAULT,
  TYPE_CITY,
  TYPE_HOUSEHOLD,
  TYPE_TRADE,
} from "../../constants";
import { sendBuildingExploded, sendSessionReload } from "../../utils/ipc";
import { fetchApi } from "../../utils/api";
import { requestConfirm } from "../../utils/confirmDialog";
import purchaseIcon from "../../assets/images/purchase.png";
import bulldozeIcon from "../../assets/images/bulldoze.png";
import pauseIcon from "../../assets/images/ui/pause.png";
import playIcon from "../../assets/images/ui/play.png";
import trashIcon from "../../assets/images/ui/trash.png";
import repairIcon from "../../assets/images/ui/repair.png";
import editIcon from "../../assets/images/ui/edit.png";
import "./AgentOperationPanel.css";

async function openBuildingEdit(agentId: number) {
  await invoke("open_registered_window", {
    windowType: "building_construction",
    params: { extra: { editAgentId: agentId } },
  });
}

/**
 * Explosion button icon (a bomb with a fuse).
 * Unlike other operation icons, uses our own SVG rather than an external asset to avoid adding
 * asset credits. Its currentColor fill follows the button text color.
 */
function BombIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <circle cx="10.5" cy="15.5" r="6.5" fill="currentColor" />
      <path
        d="M14.8 10.8 C16.2 9.4 17 8.4 17.6 7.4"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.8"
        strokeLinecap="round"
      />
      <path
        d="M19.5 1.5 L20.4 4.1 L23 5 L20.4 5.9 L19.5 8.5 L18.6 5.9 L16 5 L18.6 4.1 Z"
        fill="currentColor"
      />
    </svg>
  );
}

type AgentOperationPanelProps = {
  agent: AgentData;
  apiBaseUrl: string;
  onActionPerformed: () => void;
};

const PRE_CONSTRUCTION = new Set([STATUS_TEMPORARY, STATUS_SCHEDULED_CONSTRUCTION, STATUS_PENDING]);

const NO_OPERATION_TYPES = new Set([TYPE_DEFAULT, TYPE_CITY]);

export function AgentOperationPanel({
  agent,
  apiBaseUrl,
  onActionPerformed,
}: AgentOperationPanelProps) {
  const { t } = useTranslation();
  const [loading, setLoading] = useState(false);
  const mountedRef = useRef(true);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  if (NO_OPERATION_TYPES.has(agent.type)) {
    return null;
  }

  const isPreConstruction = PRE_CONSTRUCTION.has(agent.status);
  const base = apiBaseUrl.replace(/\/+$/, "");

  /** Return true on success so the caller can decide whether to run subsequent processing. */
  async function handleAction(
    method: string,
    body?: Record<string, unknown>,
    notify: boolean = true,
  ): Promise<boolean> {
    setLoading(true);
    try {
      const res = await fetch(`${base}/api/v1/agents/${agent.agent_id}`, {
        method,
        headers: body ? { "Content-Type": "application/json" } : undefined,
        body: body ? JSON.stringify(body) : undefined,
      });
      await requireApiSuccess(res);
      if (notify) onActionPerformed();
      return true;
    } catch (err) {
      console.error("Operation failed:", err);
      return false;
    } finally {
      setLoading(false);
    }
  }

  // Delete an agent in planning. Its 3D object remains in Unity after backend deletion until data
  // is refreshed, so send session:reload after successful deletion to synchronize changes.
  // (Unity destroys buildings absent from the fetched result.)
  // Refetching the deleted agent would only produce a 404, so do not call onActionPerformed
  // (notify=false). If the backend rejects deletion, leave the window open as well.
  async function handleDelete() {
    const ok = await handleAction("DELETE", undefined, false);
    if (!ok) return;
    await sendSessionReload();
    try {
      await invoke("close_current_window");
    } catch {
      // Ignore if the window is already closed.
    }
  }

  async function handlePurchase() {
    await handleAction("PATCH", { under_aquisition: true });
  }

  async function handleBulldoze() {
    if (agent.status === STATUS_SCHEDULED_DEMOLITION) {
      await handleAction("PATCH", { status: STATUS_ACTIVE });
    } else {
      await handleAction("PATCH", { status: STATUS_SCHEDULED_DEMOLITION });
    }
  }

  async function handlePause() {
    await handleAction("PATCH", { status: STATUS_PAUSED });
  }

  async function handleResume() {
    await handleAction("PATCH", { status: STATUS_ACTIVE });
  }

  async function handleRepair() {
    await handleAction("PATCH", { under_maintenance: true });
  }

  // Explode a private building while disguising it as an accident. The dedicated endpoint (/explode)
  // immediately changes it to STATUS_COLLAPSED without waiting for an epoch, leaving a ruin.
  // Residents and employees inside are confirmed as accident deaths next epoch, reducing the city score.
  // This is irreversible and accidental use has a high cost, so always request confirmation first.
  async function handleExplode() {
    const confirmed = await requestConfirm({
      titleKey: "agent.action.explodeTitle",
      message: t("agent.action.explodeConfirm"),
      confirmLabel: t("agent.action.explodeConfirmAction"),
      tone: "danger",
    });
    // If the detail panel switches to another building during confirmation, "this building" in the
    // confirmation no longer matches the building on screen, so do not execute it.
    if (!confirmed || !mountedRef.current) return;
    setLoading(true);
    try {
      await fetchApi(`${base}/api/v1/agents/${agent.agent_id}/explode`, {
        method: "POST",
      });
      // Notify only after API success. Reflect the committed state in the panel even if the 3D notification fails.
      try {
        await sendBuildingExploded(agent.agent_id);
      } catch (err) {
        console.error("Explosion graphics notification failed:", err);
      }
      onActionPerformed();
    } catch (err) {
      console.error("Explode failed:", err);
    } finally {
      setLoading(false);
    }
  }

  // Reconstruct a collapsed building in place. The dedicated endpoint (/reconstruct) changes it to
  // STATUS_CONSTRUCTING. Work then consumes construction service goods just like normal construction,
  // and completion restores STATUS_ACTIVE and full durability.
  async function handleReconstruct() {
    setLoading(true);
    try {
      const res = await fetch(
        `${base}/api/v1/agents/${agent.agent_id}/reconstruct`,
        { method: "POST" },
      );
      await requireApiSuccess(res);
      onActionPerformed();
    } catch (err) {
      console.error("Reconstruct failed:", err);
    } finally {
      setLoading(false);
    }
  }

  if (isPreConstruction) {
    return (
      <div className="operation-panel">
        <button
          className="operation-btn"
          type="button"
          title={t("agent.action.delete")}
          disabled={loading || !agent.public_facility}
          onClick={() => void handleDelete()}
        >
          <img src={trashIcon} alt={t("agent.action.delete")} />
        </button>
        {agent.is_bd && (
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
      </div>
    );
  }

  const canPurchase =
    !agent.public_facility &&
    agent.type !== TYPE_HOUSEHOLD &&
    agent.type !== TYPE_TRADE;

  const canBulldoze =
    agent.public_facility ||
    agent.type === TYPE_TRADE;

  const canPause =
    agent.public_facility && agent.is_bd && agent.status === STATUS_ACTIVE;

  const canResume =
    agent.public_facility && agent.is_bd && agent.status === STATUS_PAUSED;

  const canRepair =
    agent.public_facility && !agent.under_maintenance;

  // Explosion targets only active private buildings. Public facilities and import/export hubs can be
  // bulldozed directly by the city government, making the disguise pointless; planned, under-construction, and ruined buildings lack a structure to collapse.
  const canExplode =
    !agent.public_facility &&
    agent.is_bd &&
    agent.type !== TYPE_TRADE &&
    (agent.status === STATUS_ACTIVE || agent.status === STATUS_PAUSED);

  // Ruins (collapsed or burned down) require reconstruction rather than repair. Limited to public facilities owned by the city government.
  const isCollapsed = agent.status === STATUS_COLLAPSED;
  const canReconstruct = agent.public_facility && agent.is_bd && isCollapsed;

  return (
    <div className="operation-panel">
      <button
        className="operation-btn"
        type="button"
        title={t("agent.action.purchase")}
        disabled={loading || !canPurchase}
        onClick={() => void handlePurchase()}
      >
        <img src={purchaseIcon} alt={t("agent.action.purchase")} />
      </button>
      <button
        className="operation-btn"
        type="button"
        title={agent.status === STATUS_SCHEDULED_DEMOLITION ? t("agent.action.cancelBulldoze") : t("agent.action.bulldoze")}
        data-guide="agent.demolish"
        disabled={loading || !canBulldoze}
        onClick={() => void handleBulldoze()}
      >
        <img src={bulldozeIcon} alt={t("agent.action.bulldoze")} />
      </button>
      <button
        className="operation-btn"
        type="button"
        title={t("agent.action.pause")}
        disabled={loading || !canPause}
        onClick={() => void handlePause()}
      >
        <img src={pauseIcon} alt={t("agent.action.pause")} />
      </button>
      <button
        className="operation-btn"
        type="button"
        title={t("agent.action.resume")}
        disabled={loading || !canResume}
        onClick={() => void handleResume()}
      >
        <img src={playIcon} alt={t("agent.action.resume")} />
      </button>
      {/* When collapsed, show a "Reconstruct" button in place of the repair button (mutually exclusive). */}
      {isCollapsed ? (
        <button
          className="operation-btn"
          type="button"
          title={t("agent.action.rebuild")}
          disabled={loading || !canReconstruct}
          onClick={() => void handleReconstruct()}
        >
          <img src={repairIcon} alt={t("agent.action.rebuild")} />
        </button>
      ) : (
        <button
          className="operation-btn"
          type="button"
          title={t("agent.action.repair")}
          disabled={loading || !canRepair}
          onClick={() => void handleRepair()}
        >
          <img src={repairIcon} alt={t("agent.action.repair")} />
        </button>
      )}
      {/*
 * Illegal demolition. Turns a building into a ruin without acquisition or demolition procedures,
 * but citizens inside die in an accident, greatly worsening public sentiment.
 */}
      {canExplode && (
        <button
          className="operation-btn operation-btn--danger"
          type="button"
          title={t("agent.action.explode")}
          disabled={loading}
          onClick={() => void handleExplode()}
        >
          <BombIcon />
        </button>
      )}
    </div>
  );
}
