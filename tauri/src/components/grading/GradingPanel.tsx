/**
 * Earthworks panel (window_type="grading")
 *
 * Unity (GradingAreaEditManager) owns the area geometry. Send areas received via edit_grading:update_shape to
 * the backend for estimates and order eligibility; Confirm places the order. Do not echo received areas to
 * Unity.
 *
 * - Send STATE_EDIT_GRADING on mount. Redisplay remounts through open_seq, so it is sent every time.
 * - Request estimates (POST /grading/preview) only after the area is closed (finalized). Unity sends every frame
 *   while dragging vertices; debounce input and abort stale requests. Include the height offset in the same
 *   request.
 * - Preserve heightOffset during vertex editing; reset it to 0 on restart (Cancel). Confirmation remounts the
 *   whole window. Reclamation does not accept offsets (kind is shore or reference is sea_top); disable the field
 *   and show the applied value.
 * - Confirm: POST /grading → open agent details → edit_grading:finish → close. The panel does not send
 *   STATE_NORMAL; finish returns Unity to normal and refreshes data. Closing follows Rust's close-button path,
 *   which redundantly sends STATE_NORMAL. This is harmless because Unity is already in normal state and the
 *   refresh is not canceled.
 * - If the order is rejected, refetch the estimate and disable Confirm until a new response arrives.
 * - Cancel discards the drawn area and restarts drawing (resending STATE_EDIT_GRADING resets Unity's drawing
 *   state).
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { ConstructionInfoCard } from "../common/ConstructionInfoCard";
import { NumberStepper } from "../common/NumberStepper";
import { ApiError, buildApiUrl, fetchApi } from "../../utils/api";
import { setHintbarText } from "../../utils/hintbar";
import { showNotification } from "../../utils/notification";
import { constructionFundsShortfall } from "../../utils/constructionFunding";
import { GRADING_HEIGHT_OFFSET_LIMIT, GRADING_MAX_AREA } from "../../constants";
import {
  sendChangeState,
  sendEditGradingFinish,
  IPC_EVENT_EDIT_GRADING_UPDATE_SHAPE,
} from "../../utils/ipc";
import {
  gradingBaseNote,
  gradingInfoRows,
  type GradingCreateResponse,
  type GradingEstimate,
} from "./grading";
import "../common/ConstructionInfoCard.css";
import "../path/PathConstructionPanel.css";
import "./GradingPanel.css";

type GradingPanelProps = {
  apiBaseUrl: string;
  onClose: () => void;
};

type GradingShape = {
  polygon: number[][];
  finalized: boolean;
};

/** Estimate result. key is the request body, used to match the result to the current area. */
type PreviewResult = {
  key: string;
  estimate: GradingEstimate | null;
  error: string | null;
};

const PREVIEW_DEBOUNCE_MS = 300;

const EMPTY_SHAPE: GradingShape = { polygon: [], finalized: false };

const HEIGHT_OFFSET_STEP = 0.1;

export function GradingPanel({ apiBaseUrl, onClose }: GradingPanelProps) {
  const { t } = useTranslation();
  const [shape, setShape] = useState<GradingShape>(EMPTY_SHAPE);
  const [result, setResult] = useState<PreviewResult | null>(null);
  const [previewNonce, setPreviewNonce] = useState(0);
  const [isConfirming, setIsConfirming] = useState(false);
  const [heightOffset, setHeightOffset] = useState(0);

  useEffect(() => {
    sendChangeState("STATE_EDIT_GRADING", undefined, "grading").catch((err) => {
      console.error("[GradingPanel] Failed to enter edit mode:", err);
    });
  }, []);

  useEffect(() => {
    setHintbarText(t("hint.grading")).catch((err) => {
      console.error("[GradingPanel] setHintbarText failed:", err);
    });
    return () => {
      void setHintbarText("");
    };
  }, [t]);

  useEffect(() => {
    let unlisten: (() => void) | undefined;
    getCurrentWindow()
      .listen<{ polygon?: number[][]; finalized?: boolean }>(IPC_EVENT_EDIT_GRADING_UPDATE_SHAPE, (event) => {
        setShape({
          polygon: event.payload.polygon ?? [],
          finalized: event.payload.finalized === true,
        });
      })
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);
    return () => {
      unlisten?.();
    };
  }, []);

  const requestBody =
    shape.finalized && shape.polygon.length >= 3
      ? JSON.stringify({ polygon: shape.polygon, height_offset: heightOffset })
      : null;

  useEffect(() => {
    if (requestBody === null) {
      setResult(null);
      return;
    }
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      fetchApi<GradingEstimate>(buildApiUrl(apiBaseUrl, "/api/v1/grading/preview"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: requestBody,
        signal: controller.signal,
      })
        .then((estimate) => {
          if (controller.signal.aborted) return;
          setResult({ key: requestBody, estimate, error: null });
        })
        .catch((err: unknown) => {
          if (controller.signal.aborted) return;
          console.error("[GradingPanel] preview failed:", err);
          const error =
            err instanceof ApiError
              ? t(`errors.${err.code}`, { defaultValue: err.message })
              : t("errors.UNKNOWN_ERROR");
          setResult({ key: requestBody, estimate: null, error });
        });
    }, PREVIEW_DEBOUNCE_MS);
    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [apiBaseUrl, requestBody, previewNonce, t]);

  // Keep row ordering from the previous result while loading to avoid jumps during dragging, but show values as loading.
  const fresh = result !== null && result.key === requestBody ? result : null;
  const isLoading = requestBody !== null && fresh === null;
  const shownEstimate = requestBody !== null ? result?.estimate ?? null : null;
  const estimate = fresh?.estimate ?? null;

  const canConfirm =
    requestBody !== null && estimate !== null && estimate.valid && estimate.affordable && !isConfirming;

  // ConstructionInfoCard warns about insufficient funds with the shortfall amount; avoid duplicating that in the reasons list.
  const shortfall = estimate ? constructionFundsShortfall(estimate.required_total, estimate.available_funds) : null;
  const reasons = (estimate?.reasons ?? []).filter(
    (code) => !(code === "insufficient_funds" && shortfall != null && shortfall > 0),
  );

  // Reclamation does not accept a height offset. If the last response was reclamation (kind is shore even with road frontage), disable the input and show the applied value (0).
  const isShore = shownEstimate?.kind === "shore" || shownEstimate?.base_kind === "sea_top";
  const baseNote = isShore
    ? t("grading.shoreHeightLocked")
    : requestBody === null
      ? null
      : gradingBaseNote(t, shownEstimate) ?? (isLoading ? t("common.loading") : null);

  const handleCancel = useCallback(() => {
    setShape(EMPTY_SHAPE);
    setHeightOffset(0);
    sendChangeState("STATE_EDIT_GRADING", undefined, "grading").catch((err) => {
      console.error("[GradingPanel] Failed to restart drawing:", err);
    });
  }, []);

  const handleConfirm = useCallback(async () => {
    if (!canConfirm || requestBody === null) return;
    setIsConfirming(true);
    let created: GradingCreateResponse;
    try {
      created = await fetchApi<GradingCreateResponse>(buildApiUrl(apiBaseUrl, "/api/v1/grading"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: requestBody,
      });
    } catch (err) {
      setIsConfirming(false);
      // Rejection can follow road, parcel, or funding changes after the estimate. Refetch to show current reasons and funding.
      // Discard stale results and disable Confirm until the new response arrives.
      setResult(null);
      setPreviewNonce((n) => n + 1);
      const message =
        err instanceof ApiError && err.code !== "UNKNOWN_ERROR"
          ? t(`errors.${err.code}`, { defaultValue: err.message })
          : err instanceof ApiError
            ? err.message
            : t("grading.confirmFailed");
      void showNotification({ message, durationMs: 8000 });
      return;
    }
    // The order has succeeded, so close the window even if Unity cleanup fails.
    void invoke("open_registered_window", {
      windowType: "agent_detail",
      entityId: String(created.agent_id),
    });
    await sendEditGradingFinish().catch((err) => {
      console.error("[GradingPanel] sendEditGradingFinish failed:", err);
    });
    onClose();
  }, [apiBaseUrl, canConfirm, onClose, requestBody, t]);

  return (
    <div className="grading-layout">
      <div className="grading-section">
        <div className="grading-height">
          <div className="pc-settings-row">
            <span className="pc-settings-label">{t("grading.heightOffset")}</span>
            <div className="pc-stepper-row">
              {/* NumberStepper does not call onChange for empty input, a sign alone, or NaN. Only committed finite values arrive. */}
              <NumberStepper
                value={isShore ? shownEstimate?.height_offset ?? 0 : heightOffset}
                onChange={setHeightOffset}
                min={-GRADING_HEIGHT_OFFSET_LIMIT}
                max={GRADING_HEIGHT_OFFSET_LIMIT}
                step={HEIGHT_OFFSET_STEP}
                integerOnly={false}
                disabled={isShore || isConfirming}
              />
              <span className="pc-stepper-unit">m</span>
            </div>
          </div>
          {baseNote && <div className="grading-height-note">{baseNote}</div>}
        </div>
        <ConstructionInfoCard
          title={t("construction.info")}
          leadingRows={gradingInfoRows(t, shownEstimate, isLoading)}
          estimatedCost={shownEstimate?.estimated_construction_cost ?? null}
          standardCost={null}
          estimatedDurationDays={shownEstimate?.estimated_duration_days ?? null}
          isCostLoading={isLoading}
          nextEpochConstructionBudget={shownEstimate?.next_epoch_construction_budget ?? null}
          supplyRatio={shownEstimate?.construction_supply_ratio ?? null}
          expectedDurationDays={shownEstimate?.expected_duration_days ?? null}
          availableFunds={shownEstimate?.available_funds ?? null}
          requiredTotal={shownEstimate?.required_total ?? null}
        />
        {(reasons.length > 0 || fresh?.error) && (
          <ul className="grading-reasons">
            {fresh?.error && (
              <li className="construction-supply-note construction-supply-note-severe">{fresh.error}</li>
            )}
            {reasons.map((code) => (
              <li key={code} className="construction-supply-note construction-supply-note-severe">
                {t(`grading.reasons.${code}`, {
                  defaultValue: code,
                  limit: GRADING_MAX_AREA / 1e6,
                  offsetLimit: GRADING_HEIGHT_OFFSET_LIMIT,
                })}
              </li>
            ))}
          </ul>
        )}
      </div>

      <div className="grading-section grading-action-bar">
        <div className="grading-actions">
          <button className="grading-btn" type="button" onClick={handleCancel} disabled={isConfirming}>
            {t("grading.cancel")}
          </button>
          <button
            className="grading-btn grading-btn-confirm"
            type="button"
            disabled={!canConfirm}
            onClick={() => void handleConfirm()}
          >
            {isConfirming ? t("grading.confirming") : t("grading.confirm")}
          </button>
        </div>
      </div>
    </div>
  );
}
