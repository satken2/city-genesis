/**
 * 建物直接配置の用地買収の確認ウィンドウ
 *
 * window_type="building_direct_acquisition" の小さなポップアップ。建物建設パネルの
 * 直接配置モードで、設置位置が民間の土地区画と重なっている(preview-direct の
 * requires_acquisition=True)状態で確定ボタンが押されると、パネルがこのウィンドウを
 * 開く(買収内容は `params.extra.pendingAcquisition`)。
 *
 * 民間の土地・建物を市政府が自動買収して取り壊す不可逆操作なので、即確定せず
 * 確認ボタンを挟む(地区統合 ZoneMergeConfirmWindow と同じ方針)。OKを押すと
 * クロスウィンドウイベント "building_direct:acquisition_confirmed" を発火し、
 * 建設パネル側が承諾つき確定(POST /buildings/direct + acknowledge_acquisition)を行う。
 *
 * キャンセル・×閉じは何も送らない。パネル側に確定待ちの状態はなく、プレビューは
 * そのまま残るため、Rust 側のクローズ特別処理も不要。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { emit } from "@tauri-apps/api/event";

import "./BuildingDirectAcquisitionConfirmWindow.css";

/** 建設パネルの preview-direct 結果から渡される買収内容。 */
export type PendingDirectAcquisition = {
  /** 重なっている民間区画の数 */
  blockingCount: number;
  /** 買収費見積の合計(建物残存価値+解体費見積+土地代) */
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

  // 確認待ちのまま別の位置で再度確定が押されると、ウィンドウは再利用されて props だけが
  // 差し替わる。前回の busy 状態を残さないようリセットする。
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

  /** 承諾。パネル側が承諾つき確定を実行するので、イベントを発火して閉じるだけ。 */
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

      <p className="bda-note">
        {t(
          "building_construction.acquisitionConfirmNote",
          "買収は数エポックかけて進行し、用地買収予算から支払われます。完了までのあいだ建物は計画中のまま待機します。",
        )}
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
