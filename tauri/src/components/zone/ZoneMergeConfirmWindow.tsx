/**
 * District merge confirmation window
 *
 * Small window_type="zone_merge" popup. When source/target are chosen in the 3D district merge tool, Unity sends
 * `edit_zone:request_merge`. The persistent icon panel opens this window with details in
 * `params.extra.pendingMerge`.
 *
 * Merging irreversibly removes one district, so require confirmation beyond the 3D gesture.
 * As with other edits, the UI submits POST /api/v1/zones/{id}/merge, then returns `edit_zone:finish_merge` so
 * Unity refetches districts/roads.
 *
 * Closing with × causes Rust's close handler in `window.rs` to send cancellation, ensuring Unity is not left
 * awaiting confirmation.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { ApiError, buildApiUrl, fetchApi } from "../../utils/api";
import { sendEditZoneFinishMerge } from "../../utils/ipc";
import "./ZoneMergeConfirmWindow.css";

/** Source → target pair selected in the 3D district editing mode. */
export type PendingZoneMerge = {
  targetZoneId: number;
  targetZoneName: string;
  sourceZoneId: number;
  sourceZoneName: string;
};

type ZoneMergeConfirmWindowProps = {
  apiBaseUrl: string;
  pendingMerge?: PendingZoneMerge;
  onClose: () => void;
};

export function ZoneMergeConfirmWindow({
  apiBaseUrl,
  pendingMerge,
  onClose,
}: ZoneMergeConfirmWindowProps) {
  const { t } = useTranslation();

  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // A new merge request while awaiting confirmation reuses the window and replaces only its props.
  // Reset state when the pair changes so previous failures do not remain visible.
  useEffect(() => {
    setBusy(false);
    setError(null);
  }, [pendingMerge?.targetZoneId, pendingMerge?.sourceZoneId]);

  if (!pendingMerge) {
    return <p className="zone-merge-muted">{t("zone.mergeNoRequest", "統合する地区が選択されていません")}</p>;
  }

  const sourceName = pendingMerge.sourceZoneName || `#${pendingMerge.sourceZoneId}`;
  const targetName = pendingMerge.targetZoneName || `#${pendingMerge.targetZoneId}`;

  const confirmMerge = async () => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await fetchApi(
        buildApiUrl(apiBaseUrl, "/api/v1/zones/{id}/merge", String(pendingMerge.targetZoneId)),
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ source_zone_id: pendingMerge.sourceZoneId }),
        }
      );
      await sendEditZoneFinishMerge(true);
      onClose();
    } catch (err) {
      setError(err instanceof ApiError
        ? t(`errors.${err.code}`, { defaultValue: t("zone.mergeFailed") })
        : t("zone.mergeFailed"));
      setBusy(false);
    }
  };

  /** Cancel and clear Unity's pending confirmation, while keeping district editing mode active. */
  const cancelMerge = async () => {
    if (busy) return;
    await sendEditZoneFinishMerge(false);
    onClose();
  };

  return (
    <div className="zone-merge">
      <div className="zone-merge-pair">
        <span className="zone-merge-source">{sourceName}</span>
        <span className="zone-merge-arrow" aria-hidden="true">→</span>
        <span className="zone-merge-target">{targetName}</span>
      </div>

      <p className="zone-merge-desc">
        {t("zone.mergeConfirm", {
          source: sourceName,
          target: targetName,
          defaultValue: "「{{source}}」を「{{target}}」に統合します。統合元の地区は消滅し、その規制設定も失われます。",
        })}
      </p>

      {error && <p className="zone-merge-error">{error}</p>}

      <div className="zone-merge-actions">
        <button className="zone-merge-cancel-btn" type="button" disabled={busy} onClick={() => void cancelMerge()}>
          {t("common.cancel")}
        </button>
        <button className="zone-merge-confirm-btn" type="button" disabled={busy} onClick={() => void confirmMerge()}>
          {t("zone.mergeAction", "統合する")}
        </button>
      </div>
    </div>
  );
}
