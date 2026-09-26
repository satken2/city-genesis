/**
 * パスの結合・分割の操作セクション
 *
 * パス詳細パネル(PathDetailPanel)の操作パネルで結合/分割ボタンを押すと表示され、
 * 「3Dビューで対象を選ぶ → 長さを確認する → 確定する」という流れを担う。
 *
 * 1. マウント時に Unity を対象選択モードへ切り替える
 *    (結合=STATE_MERGE_PATH / 分割=STATE_SPLIT_PATH。対象パスIDを渡す)
 * 2. Unity での選択結果を IPC で受け取る
 *    (結合=結合相手のパスID / 分割=中心線上へ射影済みの分割点)
 * 3. 選択結果でバックエンドのプレビュー(dry_run)を叩き、結合後・分割後の長さを表示する
 * 4. 確定ボタンで同じエンドポイントを dry_run なしで叩き、Unityへ再描画を指示する
 *
 * 結合できない組み合わせ(曲線の折れ・通行方向の矛盾など)や分割できない地点
 * (端点に近すぎる)はプレビュー時点でバックエンドが弾き、その理由をそのまま表示する。
 * アンマウント時は必ず Unity を通常モードへ戻す。
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

/** 結合と分割のどちらの操作中かを表す。 */
export type PathTopologyEditMode = "merge" | "split";

/** Unity から届く結合相手の選択結果。 */
type MergeTargetSelection = { path_id: number; path_name: string };

/** Unity から届く分割点の選択結果(中心線へ射影済みの [x, y, z])。 */
type SplitPointSelection = { point: number[] };

/** POST /paths/merge (dry_run=true) のレスポンス。 */
type MergePreview = {
  path_id: number;
  removed_path_id: number;
  removed_node_id: number;
  length_a: number;
  length_b: number;
  merged_length: number;
};

/** POST /paths/{id}/split (dry_run=true) のレスポンス。 */
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
  /** 操作を終了する(キャンセル・確定後の後始末)。親がこのセクションを閉じる。 */
  onClose: () => void;
  /** 確定が成功した。親はパス詳細を再取得する。 */
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

  // 確定送信が成功したかどうか。成功時は edit_path:finish_topology_edit が選択モードの
  // 終了まで担うため、アンマウント時のキャンセル送信(STATE_NORMAL)を抑止する。
  // 抑止しないと、確定直後に始まった非同期の差分リロードへ状態遷移が割り込む。
  const finishedRef = useRef(false);

  // Unity を対象選択モードへ切り替える。アンマウント時は通常モードへ戻す
  // (パネルを閉じた・別の操作へ移った場合でも候補パスの着色が残らないようにする)。
  useEffect(() => {
    const start = mode === "merge" ? sendStartPathMerge : sendStartPathSplit;
    start(pathId).catch((err: unknown) => {
      console.error("[PathTopologyEditPanel] failed to enter selection mode:", err);
    });
    return () => {
      if (!finishedRef.current) void sendEndPathTopologyEdit();
    };
  }, [mode, pathId]);

  // Unity での選択結果を受け取る。選択のたびに上書きされ、プレビューが引き直される。
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

  // 選択結果が決まったらプレビュー(dry_run)を取得する。
  // 結合不可・分割不可の理由はここでバックエンドから返るメッセージをそのまま表示する。
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

  /** 長さ(m)を小数1桁で表示用に整形する。 */
  const formatLength = (meters: number | undefined) =>
    t("path.topology.meters", { value: (meters ?? 0).toFixed(1) });

  /** 結合相手のパスの表示名。名前が未設定ならIDから生成する。 */
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
   * プレビューと同じ内容を dry_run なしで送信して確定する。
   * 成功したらUnityへ再描画を指示し、親にパス詳細の再取得を促してモードを終了する。
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
        // 結合で消えるのは path_id_b。分割では消えるパスが無いので 0 を渡す。
        await sendFinishPathTopologyEdit(
          mode === "merge" ? (mergeTarget?.path_id ?? 0) : 0,
        );
        // 分割は後半へ road_id を引き継ぎ、結合は吸収した側の所属を受け継ぐので、道路の区間の集合が変わる
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
