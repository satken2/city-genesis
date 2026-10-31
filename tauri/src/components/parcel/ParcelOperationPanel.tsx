import { requireApiSuccess } from "../../utils/api";
import { describeError } from "../../utils/errors";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import type { ParcelData } from "../../types";
import { ApiError, buildApiUrl, fetchApi } from "../../utils/api";
import { showNotification } from "../../utils/notification";
import {
  sendEditParcelStartShapeEdit,
  sendEditParcelUpdatePreview,
  sendSessionReload,
} from "../../utils/ipc";
import trashIcon from "../../assets/images/ui/trash.png";
import editIcon from "../../assets/images/ui/edit.png";
import submitIcon from "../../assets/images/ui/submit.png";
import cancelIcon from "../../assets/images/ui/cancel.png";
import expandIcon from "../../assets/images/ui/path_merge.svg";
import nextIcon from "../../assets/images/ui/reload.png";
import "./ParcelOperationPanel.css";

/**
 * Adjacent parcels available for expansion, corresponding to GET /api/v1/parcels/{id}/merge-candidates.
 * Sorted by descending shared-edge length. polygon is the shape after merging.
 */
type MergeCandidate = {
  parcel_id: number;
  shared_length: number;
  polygon: number[][];
};

/** Creation method name for parcel previews (edit_parcel:update_preview). Unity only draws the merged shape with a blue outline. */
const MERGE_PREVIEW_MODE = "merge";

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

  /** Parcel expansion candidates. Null means not expanding. */
  const [mergeCandidates, setMergeCandidates] = useState<MergeCandidate[] | null>(null);
  /** Index of the candidate being previewed. */
  const [mergeIndex, setMergeIndex] = useState(0);
  /** Alive flag preventing preview drawing from responses received after unmount. */
  const mountedRef = useRef(true);
  /** Keep whether a preview is displayed for the unmount check. */
  const isExpandingRef = useRef(false);

  const isEmpty = parcel.usage_id === -1;
  const isCityOwned = parcel.owner_agent_id === cityAgentId;
  const canEditShape = isEmpty && isCityOwned;
  const isExpanding = mergeCandidates !== null;
  const base = apiBaseUrl.replace(/\/+$/, "");

  useEffect(() => {
    isExpandingRef.current = isExpanding;
  }, [isExpanding]);

  // Do not leave a 3D preview when refetching or window replacement unmounts the component.
  // Closing the window leaves it ready for reuse without unmounting,
  // so Rust close handling sends the same clear instruction.
  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      if (isExpandingRef.current) {
        void sendEditParcelUpdatePreview(MERGE_PREVIEW_MODE, []);
      }
    };
  }, []);

  function errorMessage(err: unknown, fallback: string): string {
    if (err instanceof ApiError) return describeError(err, t);
    return err instanceof Error ? describeError(err) : fallback;
  }

  function showMergePreview(candidates: MergeCandidate[], index: number) {
    setMergeCandidates(candidates);
    setMergeIndex(index);
    void sendEditParcelUpdatePreview(MERGE_PREVIEW_MODE, [candidates[index].polygon]);
  }

  function endExpand() {
    setMergeCandidates(null);
    setMergeIndex(0);
    void sendEditParcelUpdatePreview(MERGE_PREVIEW_MODE, []);
  }

  async function handleDelete() {
    setLoading(true);
    try {
      const res = await fetch(`${base}/api/v1/parcels/${parcel.parcel_id}`, {
        method: "DELETE",
      });
      await requireApiSuccess(res);
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

  /** Preview merging with the neighbor sharing the longest edge. */
  async function handleStartExpand() {
    setLoading(true);
    try {
      const res = await fetchApi<{ candidates: MergeCandidate[] }>(
        buildApiUrl(apiBaseUrl, `/api/v1/parcels/${parcel.parcel_id}/merge-candidates`)
      );
      if (!mountedRef.current) return;
      if (res.candidates.length === 0) {
        void showNotification({ message: t("parcel.expandNoCandidates"), durationMs: 5000 });
        return;
      }
      showMergePreview(res.candidates, 0);
    } catch (err) {
      console.error("[ParcelOperation] merge candidates failed:", err);
      void showNotification({ message: errorMessage(err, t("parcel.expandFailed")), durationMs: 5000 });
    } finally {
      setLoading(false);
    }
  }

  /** Preview the next-longest shared-edge candidate. Wrap to the first after the last. */
  function handleNextExpand() {
    if (!mergeCandidates) return;
    showMergePreview(mergeCandidates, (mergeIndex + 1) % mergeCandidates.length);
  }

  /**
 * Merge with the previewed parcel. The resulting parcel has a new ID,
 * so refresh the 3D view and switch this panel to the new parcel.
 */
  async function handleConfirmExpand() {
    if (!mergeCandidates) return;
    const target = mergeCandidates[mergeIndex];
    setLoading(true);
    try {
      const res = await fetchApi<{ parcel: number }>(buildApiUrl(apiBaseUrl, "/api/v1/parcels"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ mode: "merge", parcel_ids: [parcel.parcel_id, target.parcel_id] }),
      });
      endExpand();
      await sendSessionReload();
      await invoke("open_registered_window", {
        windowType: "parcel_detail",
        entityId: String(res.parcel),
      });
    } catch (err) {
      console.error("[ParcelOperation] merge failed:", err);
      void showNotification({ message: errorMessage(err, t("parcel.expandFailed")), durationMs: 5000 });
    } finally {
      if (mountedRef.current) setLoading(false);
    }
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
      ) : isExpanding ? (
        <>
          <button
            className="operation-btn"
            type="button"
            title={t("parcel.action.nextExpand")}
            disabled={loading}
            onClick={handleNextExpand}
          >
            <img src={nextIcon} alt={t("parcel.action.nextExpand")} />
          </button>
          <button
            className="operation-btn operation-btn--primary"
            type="button"
            title={t("parcel.action.confirmExpand")}
            disabled={loading}
            onClick={() => void handleConfirmExpand()}
          >
            <img src={submitIcon} alt={t("parcel.action.confirmExpand")} />
          </button>
          <button
            className="operation-btn"
            type="button"
            title={t("parcel.action.cancelExpand")}
            disabled={loading}
            onClick={endExpand}
          >
            <img src={cancelIcon} alt={t("parcel.action.cancelExpand")} />
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
          <button
            className="operation-btn"
            type="button"
            title={deleteDisabledTooltip ?? t("parcel.action.expand")}
            disabled={loading || !isEmpty}
            onClick={() => void handleStartExpand()}
          >
            <img src={expandIcon} alt={t("parcel.action.expand")} />
          </button>
        </>
      )}
    </div>
  );
}
