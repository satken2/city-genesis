/**
 * 造成パネル(window_type="grading")
 *
 * 範囲の形の正は Unity(GradingAreaEditManager)。パネルは edit_grading:update_shape で受けた範囲を
 * バックエンドへ送って見積もりと発注可否を表示し、確定で発注する。受けた範囲を Unity へ送り返さない。
 *
 * - マウントで STATE_EDIT_GRADING を送る(再表示は open_seq で再マウントされるので毎回送られる)。
 * - 見積もり(POST /grading/preview)は範囲が閉じた後(finalized)だけ取る。Unity は頂点のドラッグ中に
 *   毎フレーム送ってくるので、入力が落ち着くまで待ち、古い要求は中断する。
 * - 確定は POST /grading → 主体の詳細を開く → edit_grading:finish → 閉じる。パネル自身は STATE_NORMAL を送らない
 *   (finish の中で Unity が通常ステートへ戻して再取得する)。閉じる処理は×と同じ Rust の経路なので、そこで冗長な
 *   STATE_NORMAL が届くが、Unity は既に通常ステートで再取得も取り消されないため無害。
 * - 発注が拒否されたら見積もりを取り直し、新しい応答が来るまで確定を押せなくする。
 * - キャンセルは描いた範囲を捨てて描き直す(STATE_EDIT_GRADING の再送で Unity が作図を初期化する)。
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { ConstructionInfoCard } from "../common/ConstructionInfoCard";
import { ApiError, buildApiUrl, fetchApi } from "../../utils/api";
import { setHintbarText } from "../../utils/hintbar";
import { showNotification } from "../../utils/notification";
import { constructionFundsShortfall } from "../../utils/constructionFunding";
import { GRADING_MAX_AREA } from "../../constants";
import {
  sendChangeState,
  sendEditGradingFinish,
  IPC_EVENT_EDIT_GRADING_UPDATE_SHAPE,
} from "../../utils/ipc";
import {
  gradingInfoRows,
  type GradingCreateResponse,
  type GradingEstimate,
} from "./grading";
import "../common/ConstructionInfoCard.css";
import "./GradingPanel.css";

type GradingPanelProps = {
  apiBaseUrl: string;
  onClose: () => void;
};

type GradingShape = {
  polygon: number[][];
  finalized: boolean;
};

/** 見積もりの結果。key は要求本文で、今の範囲に対する結果かどうかの照合に使う */
type PreviewResult = {
  key: string;
  estimate: GradingEstimate | null;
  error: string | null;
};

const PREVIEW_DEBOUNCE_MS = 300;

const EMPTY_SHAPE: GradingShape = { polygon: [], finalized: false };

export function GradingPanel({ apiBaseUrl, onClose }: GradingPanelProps) {
  const { t } = useTranslation();
  const [shape, setShape] = useState<GradingShape>(EMPTY_SHAPE);
  const [result, setResult] = useState<PreviewResult | null>(null);
  const [previewNonce, setPreviewNonce] = useState(0);
  const [isConfirming, setIsConfirming] = useState(false);

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
    shape.finalized && shape.polygon.length >= 3 ? JSON.stringify({ polygon: shape.polygon }) : null;

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

  // 取得中は直前の結果で行の並びを保ち(ドラッグ中に行が出入りして跳ねないため)、値は読み込み中と表示する。
  const fresh = result !== null && result.key === requestBody ? result : null;
  const isLoading = requestBody !== null && fresh === null;
  const shownEstimate = requestBody !== null ? result?.estimate ?? null : null;
  const estimate = fresh?.estimate ?? null;

  const canConfirm =
    requestBody !== null && estimate !== null && estimate.valid && estimate.affordable && !isConfirming;

  // 資金不足は ConstructionInfoCard が不足額つきで警告するので、そのときは理由の一覧に重ねない。
  const shortfall = estimate ? constructionFundsShortfall(estimate.required_total, estimate.available_funds) : null;
  const reasons = (estimate?.reasons ?? []).filter(
    (code) => !(code === "insufficient_funds" && shortfall != null && shortfall > 0),
  );

  const handleCancel = useCallback(() => {
    setShape(EMPTY_SHAPE);
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
      // 拒否は見積もりの後に道路・区画・資金が変わったときに起きるので、見積もりを取り直して今の理由と資金を出す。
      // 古い結果は捨て、新しい応答が来るまで確定を押せなくする。
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
    // 発注は成立しているので、Unity への後始末が失敗してもウィンドウは閉じる。
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
