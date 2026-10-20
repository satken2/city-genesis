/**
 * Path merge/split operation section.
 *
 * Shown after pressing Merge/Split in PathDetailPanel operations. Handles the flow:
 * select target in 3D → review lengths → confirm.
 *
 * 1. On mount, switch Unity to target selection (STATE_MERGE_PATH / STATE_SPLIT_PATH with the target path ID).
 * 2. Receive Unity's selection through IPC (merge partner path ID / split point projected onto the centerline).
 * 3. Request a backend dry_run preview and show resulting lengths.
 * 4. Confirm through the same endpoint without dry_run and ask Unity to redraw.
 *
 * The backend rejects incompatible merges (broken curves, conflicting directions, etc.) and invalid split points
 * (too close to endpoints) during preview; display the reason directly.
 * Always return Unity to normal mode on unmount.
 */

import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import type { PathData } from "../../types";
import { ApiError, fetchApi } from "../../utils/api";
import {
  sendEndPathTopologyEdit,
  sendFinishPathTopologyEdit,
  sendStartPathMerge,
  sendStartPathSplit,
  IPC_EVENT_EDIT_PATH_SELECT_MERGE_TARGET,
  IPC_EVENT_EDIT_PATH_SELECT_SPLIT_POINT,
} from "../../utils/ipc";
import { notifyRoadsChanged } from "../road/roadApi";
import "./PathTopologyEditPanel.css";

/** Whether the active operation is merge or split. */
export type PathTopologyEditMode = "merge" | "split";

/** Merge partner selection received from Unity. */
type MergeTargetSelection = { path_id: number; path_name: string };

/** Split point received from Unity ([x, y, z] projected onto the centerline). */
type SplitPointSelection = { point: number[] };

/** POST /paths/merge response with dry_run=true. */
type MergePreview = {
  path_id: number;
  removed_path_id: number;
  removed_node_id: number;
  length_a: number;
  length_b: number;
  merged_length: number;
};

/** POST /paths/{id}/split response with dry_run=true. */
type SplitPreview = {
  path_id: number;
  split_point: number[];
  segment_lengths: number[];
  total_length: number;
};

type PathTopologyEditPanelProps = {
  apiBaseUrl: string;
  path: PathData;
  mode: PathTopologyEditMode;
  /** End the operation (cancel/post-confirm cleanup). The parent closes this section. */
  onClose: () => void;
  /** Confirmation succeeded. The parent refetches path details. */
  onCompleted: () => void;
};

export function PathTopologyEditPanel({
  apiBaseUrl,
  path,
  mode,
  onClose,
  onCompleted,
}: PathTopologyEditPanelProps) {
  const { t } = useTranslation();
  const base = apiBaseUrl.replace(/\/+$/, "");

  const [mergeTarget, setMergeTarget] = useState<MergeTargetSelection | null>(null);
  const [splitPoint, setSplitPoint] = useState<number[] | null>(null);
  const [mergePreview, setMergePreview] = useState<MergePreview | null>(null);
  const [splitPreview, setSplitPreview] = useState<SplitPreview | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const pathId = path.path_id;

  // Whether confirmation succeeded. On success, edit_path:finish_topology_edit also exits selection mode,
  // so suppress the unmount cancellation (STATE_NORMAL).
  // Otherwise that state transition would interrupt the async differential reload started immediately after confirmation.
  const finishedRef = useRef(false);

  // Switch Unity to target selection; restore normal mode on unmount
  // so candidate coloring does not remain when the panel closes or switches operations.
  useEffect(() => {
    const start = mode === "merge" ? sendStartPathMerge : sendStartPathSplit;
    start(pathId).catch((err: unknown) => {
      console.error("[PathTopologyEditPanel] failed to enter selection mode:", err);
    });
    return () => {
      if (!finishedRef.current) void sendEndPathTopologyEdit();
    };
  }, [mode, pathId]);

  // Receive Unity selections. Each replaces the previous selection and refreshes the preview.
  useEffect(() => {
    const appWindow = getCurrentWindow();
    let unlisten: (() => void) | undefined;
    let cancelled = false;

    const eventName =
      mode === "merge"
        ? IPC_EVENT_EDIT_PATH_SELECT_MERGE_TARGET
        : IPC_EVENT_EDIT_PATH_SELECT_SPLIT_POINT;

    appWindow
      .listen<MergeTargetSelection | SplitPointSelection>(eventName, (event) => {
        if (mode === "merge") {
          const payload = event.payload as MergeTargetSelection;
          setMergeTarget({ path_id: payload.path_id, path_name: payload.path_name ?? "" });
        } else {
          const payload = event.payload as SplitPointSelection;
          setSplitPoint(payload.point);
        }
      })
      .then((fn) => {
        if (cancelled) fn();
        else unlisten = fn;
      })
      .catch((err: unknown) => {
        console.error("[PathTopologyEditPanel] listen failed:", err);
      });

    return () => {
      cancelled = true;
      unlisten?.();
    };
  }, [mode]);

  // Fetch the dry_run preview once a selection is available.
  // Display backend messages directly for merge/split rejection reasons.
  useEffect(() => {
    const selectionReady = mode === "merge" ? mergeTarget != null : splitPoint != null;
    if (!selectionReady) return;

    const controller = new AbortController();
    setPreviewLoading(true);
    setError(null);

    const request =
      mode === "merge"
        ? fetchApi<MergePreview>(`${base}/api/v1/paths/merge`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              path_id_a: pathId,
              path_id_b: mergeTarget?.path_id,
              dry_run: true,
            }),
            signal: controller.signal,
          })
        : fetchApi<SplitPreview>(`${base}/api/v1/paths/${pathId}/split`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ point: splitPoint, dry_run: true }),
            signal: controller.signal,
          });

    request
      .then((data) => {
        if (controller.signal.aborted) return;
        if (mode === "merge") setMergePreview(data as MergePreview);
        else setSplitPreview(data as SplitPreview);
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        setMergePreview(null);
        setSplitPreview(null);
        setError(err instanceof ApiError || err instanceof Error ? err.message : String(err));
      })
      .finally(() => {
        if (!controller.signal.aborted) setPreviewLoading(false);
      });

    return () => {
      controller.abort();
    };
  }, [mode, base, pathId, mergeTarget, splitPoint]);

  /** Format length (m) to one decimal place. */
  const formatLength = (meters: number | undefined) =>
    t("path.topology.meters", { value: (meters ?? 0).toFixed(1) });

  /** Merge partner's display name. Generate from its ID if unnamed. */
  const mergeTargetName =
    mergeTarget == null
      ? ""
      : mergeTarget.path_name !== ""
        ? mergeTarget.path_name
        : t("path.titleWithId", { id: mergeTarget.path_id });

  const canSubmit =
    !submitting &&
    !previewLoading &&
    (mode === "merge" ? mergePreview != null : splitPreview != null);

  /**
 * Confirm by sending the preview's request without dry_run.
 * On success, ask Unity to redraw, tell the parent to refetch path details, and exit the mode.
 */
  const handleConfirm = useCallback(() => {
    if (!canSubmit) return;
    setSubmitting(true);
    setError(null);

    const request =
      mode === "merge"
        ? fetchApi(`${base}/api/v1/paths/merge`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ path_id_a: pathId, path_id_b: mergeTarget?.path_id }),
          })
        : fetchApi(`${base}/api/v1/paths/${pathId}/split`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ point: splitPoint }),
          });

    request
      .then(async () => {
        finishedRef.current = true;
        // Merging removes path_id_b. Splitting removes no path, so pass 0.
        await sendFinishPathTopologyEdit(
          mode === "merge" ? (mergeTarget?.path_id ?? 0) : 0,
        );
        // Splitting inherits road_id on the latter half; merging inherits the absorbed path's membership, changing the road's section set.
        await notifyRoadsChanged(undefined, "path_detail");
        onCompleted();
        onClose();
      })
      .catch((err: unknown) => {
        setError(err instanceof ApiError || err instanceof Error ? err.message : String(err));
      })
      .finally(() => setSubmitting(false));
  }, [canSubmit, mode, base, pathId, mergeTarget, splitPoint, onCompleted, onClose]);

  const title =
    mode === "merge" ? t("path.topology.mergeTitle") : t("path.topology.splitTitle");
  const hint = mode === "merge" ? t("path.topology.mergeHint") : t("path.topology.splitHint");
  const hasSelection = mode === "merge" ? mergeTarget != null : splitPoint != null;

  return (
    <section className="path-topology-section">
      <h3 className="path-topology-title">{title}</h3>
      <p className="path-topology-hint">{hint}</p>

      {!hasSelection && (
        <p className="path-topology-waiting">{t("path.topology.waitingSelection")}</p>
      )}

      {hasSelection && previewLoading && (
        <p className="path-topology-waiting">{t("path.topology.checking")}</p>
      )}

      {error && <div className="path-topology-error">{error}</div>}

      {mode === "merge" && mergePreview && (
        <div className="path-topology-grid">
          <span className="path-topology-label">{t("path.topology.thisPath")}</span>
          <span className="path-topology-value">{formatLength(mergePreview.length_a)}</span>
          <span className="path-topology-label">{mergeTargetName}</span>
          <span className="path-topology-value">{formatLength(mergePreview.length_b)}</span>
          <span className="path-topology-label path-topology-label--total">
            {t("path.topology.mergedLength")}
          </span>
          <span className="path-topology-value path-topology-value--total">
            {formatLength(mergePreview.merged_length)}
          </span>
        </div>
      )}

      {mode === "split" && splitPreview && (
        <div className="path-topology-grid">
          <span className="path-topology-label">{t("path.topology.beforeLength")}</span>
          <span className="path-topology-value">{formatLength(splitPreview.total_length)}</span>
          <span className="path-topology-label path-topology-label--total">
            {t("path.topology.firstSegment")}
          </span>
          <span className="path-topology-value path-topology-value--total">
            {formatLength(splitPreview.segment_lengths?.[0])}
          </span>
          <span className="path-topology-label path-topology-label--total">
            {t("path.topology.secondSegment")}
          </span>
          <span className="path-topology-value path-topology-value--total">
            {formatLength(splitPreview.segment_lengths?.[1])}
          </span>
        </div>
      )}

      <div className="path-topology-actions">
        <button
          className="path-topology-btn path-topology-btn--confirm"
          type="button"
          disabled={!canSubmit}
          onClick={handleConfirm}
        >
          {submitting
            ? t("path.topology.submitting")
            : mode === "merge"
              ? t("path.topology.confirmMerge")
              : t("path.topology.confirmSplit")}
        </button>
        <button
          className="path-topology-btn"
          type="button"
          disabled={submitting}
          onClick={onClose}
        >
          {t("path.topology.cancel")}
        </button>
      </div>
    </section>
  );
}
