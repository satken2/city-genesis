/**
 * Building placement land acquisition confirmation window
 *
 * Small window_type="building_direct_acquisition" popup. The construction panel opens it when
 * confirmation is clicked while direct placement overlaps private parcels (preview-direct
 * requires_acquisition=True) or the selected parcel has a private building (preview
 * requires_acquisition=True). Acquisition details are in `params.extra.pendingAcquisition`.
 *
 * Automatic city government acquisition/demolition of private land/buildings is irreversible,
 * so require confirmation before committing, as for district merging in ZoneMergeConfirmWindow.
 * OK emits the cross-window event "building_direct:acquisition_confirmed"; the construction
 * panel commits with consent (POST /buildings/direct or POST /buildings + acknowledge_acquisition).
 *
 * Cancel/close emits nothing. The panel has no pending-confirmation state and retains its preview,
 * so no special Rust close handling is needed.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { emit } from "@tauri-apps/api/event";

import "./BuildingDirectAcquisitionConfirmWindow.css";

/** Acquisition details passed from the construction panel's preview result. */
export type PendingDirectAcquisition = {
  /** Number of private parcels to acquire. */
  blockingCount: number;
  /** Total estimated acquisition cost (residual building value + estimated demolition cost + land price). */
  estimatedCost: number;
};

type BuildingDirectAcquisitionConfirmWindowProps = {
  pendingAcquisition?: PendingDirectAcquisition;
  onClose: () => void;
};

export function BuildingDirectAcquisitionConfirmWindow({
  pendingAcquisition,
  onClose,
}: BuildingDirectAcquisitionConfirmWindowProps) {
  const { t } = useTranslation();

  const [busy, setBusy] = useState(false);

  // If confirmation is clicked again at another position while this window is pending, the window
  // is reused and only props change. Reset to avoid retaining the previous busy state.
  useEffect(() => {
    setBusy(false);
  }, [pendingAcquisition?.blockingCount, pendingAcquisition?.estimatedCost]);

  if (!pendingAcquisition) {
    return (
      <p className="bda-muted">
        {t("building_construction.acquisitionNoRequest", "確認する買収内容がありません")}
      </p>
    );
  }

  /** Consent. The panel performs the actual commit with consent, so just emit the event and close. */
  const confirmAcquisition = async () => {
    if (busy) return;
    setBusy(true);
    await emit("building_direct:acquisition_confirmed", {});
    onClose();
  };

  return (
    <div className="bda">
      <p className="bda-desc">
        {t("building_construction.acquisitionConfirmDesc", {
          count: pendingAcquisition.blockingCount,
          cost: Math.round(pendingAcquisition.estimatedCost).toLocaleString(),
          defaultValue:
            "設置位置が民間の土地区画 {{count}} 件と重なっています。市政府が自動で用地を買収し、既存の建物を取り壊してから建設を開始します(買収費見積: {{cost}})。",
        })}
      </p>

      <div className="bda-actions">
        <button className="bda-cancel-btn" type="button" disabled={busy} onClick={onClose}>
          {t("common.cancel")}
        </button>
        <button
          className="bda-confirm-btn"
          type="button"
          disabled={busy}
          onClick={() => void confirmAcquisition()}
        >
          {t("building_construction.acquisitionConfirmAction", "買収して建設")}
        </button>
      </div>
    </div>
  );
}
