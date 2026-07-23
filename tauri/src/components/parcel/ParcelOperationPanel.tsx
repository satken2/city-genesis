import { useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import type { ParcelData } from "../../types";
import { sendEditParcelStartShapeEdit } from "../../utils/ipc";
import trashIcon from "../../assets/images/ui/trash.png";
import editIcon from "../../assets/images/ui/edit.png";
import submitIcon from "../../assets/images/ui/submit.png";
import cancelIcon from "../../assets/images/ui/cancel.png";
import "./ParcelOperationPanel.css";

type ParcelOperationPanelProps = {
  parcel: ParcelData;
  apiBaseUrl: string;
  onActionPerformed: () => void;
  cityAgentId: number;
  isEditingShape: boolean;
  canConfirmEdit: boolean;
  onStartEditShape: () => void;
  onConfirmEditShape: () => void;
  onCancelEditShape: () => void;
};

export function ParcelOperationPanel({
  parcel,
  apiBaseUrl,
  cityAgentId,
  isEditingShape,
  canConfirmEdit,
  onStartEditShape,
  onConfirmEditShape,
  onCancelEditShape,
}: ParcelOperationPanelProps) {
  const { t } = useTranslation();
  const [loading, setLoading] = useState(false);

  const isEmpty = parcel.usage_id === -1;
  const isCityOwned = parcel.owner_agent_id === cityAgentId;
  const canEditShape = isEmpty && isCityOwned;
  const base = apiBaseUrl.replace(/\/+$/, "");

  async function handleDelete() {
    setLoading(true);
    try {
      const res = await fetch(`${base}/api/v1/parcels/${parcel.parcel_id}`, {
        method: "DELETE",
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      await invoke("close_current_window");
    } catch (err) {
      console.error("Delete failed:", err);
    } finally {
      setLoading(false);
    }
  }

  async function handleEditShape() {
    if (!canEditShape) return;
    onStartEditShape();
    await sendEditParcelStartShapeEdit(parcel.parcel_id, parcel.polygon);
  }

  const deleteDisabledTooltip = isEmpty
    ? undefined
    : t("parcel.action.disabledInUse");

  const editDisabledTooltip = canEditShape
    ? undefined
    : t("parcel.action.disabledNotCityOwnedOrInUse");

  return (
    <div className="operation-panel">
      {isEditingShape ? (
        <>
          <button
            className="operation-btn operation-btn--primary"
            type="button"
            title={t("parcel.action.confirmShape")}
            disabled={!canConfirmEdit}
            onClick={() => void onConfirmEditShape()}
          >
            <img src={submitIcon} alt={t("parcel.action.confirmShape")} />
          </button>
          <button
            className="operation-btn"
            type="button"
            title={t("parcel.action.cancelShape")}
            onClick={() => void onCancelEditShape()}
          >
            <img src={cancelIcon} alt={t("parcel.action.cancelShape")} />
          </button>
        </>
      ) : (
        <>
          <button
            className="operation-btn"
            type="button"
            title={deleteDisabledTooltip ?? t("parcel.action.delete")}
            disabled={loading || !isEmpty}
            onClick={() => void handleDelete()}
          >
            <img src={trashIcon} alt={t("parcel.action.delete")} />
          </button>
          <button
            className="operation-btn"
            type="button"
            title={editDisabledTooltip ?? t("parcel.action.editShape")}
            disabled={!canEditShape}
            onClick={() => void handleEditShape()}
          >
            <img src={editIcon} alt={t("parcel.action.editShape")} />
          </button>
        </>
      )}
    </div>
  );
}
