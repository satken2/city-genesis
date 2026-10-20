import { useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import type { PathData } from "../../types";
import {
  STATUS_TEMPORARY,
  STATUS_SCHEDULED_CONSTRUCTION,
  STATUS_ACTIVE,
  STATUS_COLLAPSED,
  STATUS_PENDING,
  STATUS_CONSTRUCTING,
  STATUS_DEMOLISHING,
  PATH_TRANSPORT,
} from "../../constants";
import { sendSessionReload } from "../../utils/ipc";
import { notifyRoadsChanged } from "../road/roadApi";
import type { PathTopologyEditMode } from "./PathTopologyEditPanel";
import trashIcon from "../../assets/images/ui/trash.png";
import bulldozeIcon from "../../assets/images/bulldoze.png";
import editIcon from "../../assets/images/ui/edit.png";
import repairIcon from "../../assets/images/ui/repair.png";
import rocketIcon from "../../assets/images/ui/rocket.png";
import pendingIcon from "../../assets/images/ui/pending.png";
import mergeIcon from "../../assets/images/ui/path_merge.svg";
import splitIcon from "../../assets/images/ui/path_split.svg";
import "./PathOperationPanel.css";

async function openPathEdit(pathId: number) {
  await invoke("open_registered_window", {
    windowType: "path_build",
    params: { extra: { editPathId: pathId } },
  });
}

type PathOperationPanelProps = {
  path: PathData;
  apiBaseUrl: string;
  onActionPerformed: () => void;
  /**
 * Start path merge/split. Actual target selection occurs in Unity's 3D view, so this only reports the mode
 * to the parent (PathDetailPanel). PathTopologyEditPanel handles the selection-waiting section and confirmation.
 */
  onStartTopologyEdit: (mode: PathTopologyEditMode) => void;
  /** Current merge/split operation (null if none), used for button on states. */
  topologyEditMode: PathTopologyEditMode | null;
};

const PRE_CONSTRUCTION = new Set([STATUS_TEMPORARY, STATUS_SCHEDULED_CONSTRUCTION, STATUS_PENDING]);

export function PathOperationPanel({
  path,
  apiBaseUrl,
  onActionPerformed,
  onStartTopologyEdit,
  topologyEditMode,
}: PathOperationPanelProps) {
  const { t } = useTranslation();
  const [loading, setLoading] = useState(false);

  const base = apiBaseUrl.replace(/\/+$/, "");
  const isPreConstruction = PRE_CONSTRUCTION.has(path.status);
  // Collapsed paths are reconstructed rather than repaired.
  const isCollapsed = path.status === STATUS_COLLAPSED;

  async function handleToggleStatus() {
    setLoading(true);
    const isPlannedOrConstructing = path.status === STATUS_SCHEDULED_CONSTRUCTION || path.status === STATUS_CONSTRUCTING;
    const newStatus = isPlannedOrConstructing ? STATUS_PENDING : STATUS_SCHEDULED_CONSTRUCTION;
    try {
      const res = await fetch(`${base}/api/v1/paths/${path.path_id}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ status: newStatus }),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      // This changes planned/under-construction lengths in road details and dashed line-map rendering.
      await notifyRoadsChanged(undefined, "path_detail");
      onActionPerformed();
    } catch (err) {
      console.error("Toggle status failed:", err);
    } finally {
      setLoading(false);
    }
  }

  async function handleDelete() {
    setLoading(true);
    try {
      const res = await fetch(`${base}/api/v1/paths/${path.path_id}`, {
        method: "DELETE",
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      // Request a Unity data reload.
      await sendSessionReload();
      // Deletion removes road membership and deletes roads left with no sections. Notify before closing because hidden windows are reused.
      await notifyRoadsChanged(undefined, "path_detail");
      await invoke("close_current_window");
    } catch (err) {
      console.error("Delete failed:", err);
    } finally {
      setLoading(false);
    }
  }

  async function handleRepair() {
    setLoading(true);
    try {
      const res = await fetch(`${base}/api/v1/paths/${path.path_id}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ under_maintenance: true }),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
    } catch (err) {
      console.error("Repair failed:", err);
    } finally {
      setLoading(false);
    }
  }

  // Demolish an active path (city government ownership allows the player to demolish freely). The dedicated
  // POST /demolish endpoint switches to STATUS_DEMOLISHING, after which construction
  // service goods advance demolition until the path is deleted. Press again during demolition to
  // cancel with DELETE /demolish and return to STATUS_ACTIVE (toggle between the two).
  // Status changes affect 3D rendering, so request a Unity data reload.
  async function handleDemolish() {
    setLoading(true);
    const isDemolishing = path.status === STATUS_DEMOLISHING;
    try {
      const res = await fetch(`${base}/api/v1/paths/${path.path_id}/demolish`, {
        method: isDemolishing ? "DELETE" : "POST",
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      await sendSessionReload();
      await notifyRoadsChanged(undefined, "path_detail");
      onActionPerformed();
    } catch (err) {
      console.error("Demolish failed:", err);
    } finally {
      setLoading(false);
    }
  }

  // Reconstruct a collapsed path in place. The dedicated /reconstruct endpoint switches to
  // STATUS_CONSTRUCTING, after which construction service goods advance work as with normal placement.
  // Completion restores STATUS_ACTIVE and new durability. Because status changes affect 3D rendering,
  // request a Unity data reload and refetch the detail panel.
  async function handleReconstruct() {
    setLoading(true);
    try {
      const res = await fetch(
        `${base}/api/v1/paths/${path.path_id}/reconstruct`,
        { method: "POST" },
      );
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      await sendSessionReload();
      await notifyRoadsChanged(undefined, "path_detail");
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
          title={t("path.action.delete")}
          disabled={loading}
          onClick={() => void handleDelete()}
        >
          <img src={trashIcon} alt={t("path.action.delete")} />
        </button>
        {path.status === STATUS_PENDING ? (
          <button
            className="operation-btn"
            type="button"
            title={t("path.action.startConstruction")}
            disabled={loading}
            onClick={() => void handleToggleStatus()}
          >
            <img src={rocketIcon} alt={t("path.action.startConstruction")} />
          </button>
        ) : (
          <button
            className="operation-btn"
            type="button"
            title={t("path.action.stopConstruction")}
            disabled={loading}
            onClick={() => void handleToggleStatus()}
          >
            <img src={pendingIcon} alt={t("path.action.stopConstruction")} />
          </button>
        )}
        <button
          className="operation-btn"
          type="button"
          title={t("path.action.editShape")}
          disabled={loading}
          onClick={() => {
            void openPathEdit(path.path_id);
          }}
        >
          <img src={editIcon} alt={t("path.action.editShape")} />
        </button>
      </div>
    );
  }

  return (
    <div className="operation-panel">
      {/* When collapsed, show a "Reconstruct" button in place of the repair button (mutually exclusive). */}
      {isCollapsed ? (
        <button
          className="operation-btn"
          type="button"
          title={t("path.action.rebuild")}
          disabled={loading}
          onClick={() => void handleReconstruct()}
        >
          <img src={repairIcon} alt={t("path.action.rebuild")} />
        </button>
      ) : (
        <>
          {path.status === STATUS_CONSTRUCTING && (
            <button
              className="operation-btn"
              type="button"
              title={t("path.action.stopConstruction")}
              disabled={loading}
              onClick={() => void handleToggleStatus()}
            >
              <img src={pendingIcon} alt={t("path.action.stopConstruction")} />
            </button>
          )}
          {/*
 * Bulldoze: the player demolishes city government paths. Toggle active/demolishing;
 * while demolishing, this acts as Cancel demolition.
 */}
          {(path.status === STATUS_ACTIVE || path.status === STATUS_DEMOLISHING) && (
            <button
              className="operation-btn"
              type="button"
              title={path.status === STATUS_DEMOLISHING ? t("path.action.cancelBulldoze") : t("path.action.bulldoze")}
              disabled={loading}
              onClick={() => void handleDemolish()}
            >
              <img src={bulldozeIcon} alt={t("path.action.bulldoze")} />
            </button>
          )}
          {path.status !== STATUS_DEMOLISHING && (
            <button
              className="operation-btn"
              type="button"
              title={t("path.action.repair")}
              disabled={loading || !!path.under_maintenance}
              onClick={() => void handleRepair()}
            >
              <img src={repairIcon} alt={t("path.action.repair")} />
            </button>
          )}
          {/* Rebuild, merge, and split also require active paths on the backend. */}
          {path.status === STATUS_ACTIVE && (
            <>
              {path.path_type !== PATH_TRANSPORT && (
                <button className="operation-btn" type="button"
                  title={t("path.rebuild.title")} aria-label={t("path.rebuild.title")}
                  data-guide="path.rebuild.open" disabled={loading}
                  onClick={() => {
                    void invoke("open_registered_window", {
                      windowType: "path_rebuild", entityId: String(path.path_id),
                    }).catch(console.error);
                  }}>
                  <img src={editIcon} alt="" />
                </button>
              )}
              <button
                className={`operation-btn${topologyEditMode === "merge" ? " operation-btn--on" : ""}`}
                type="button"
                title={t("path.action.merge")}
                disabled={loading}
                onClick={() => onStartTopologyEdit("merge")}
              >
                <img src={mergeIcon} alt={t("path.action.merge")} />
              </button>
              <button
                className={`operation-btn${topologyEditMode === "split" ? " operation-btn--on" : ""}`}
                type="button"
                title={t("path.action.split")}
                disabled={loading}
                onClick={() => onStartTopologyEdit("split")}
              >
                <img src={splitIcon} alt={t("path.action.split")} />
              </button>
            </>
          )}
        </>
      )}
    </div>
  );
}
