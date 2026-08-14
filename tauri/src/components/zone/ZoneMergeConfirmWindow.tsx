/**
 * 地区の統合の確定ウィンドウ
 *
 * window_type="zone_merge" の小さなポップアップ。地区編集モードの「地区の統合」ツールで
 * 3Dビューの統合元・統合先が決まると、Unity が `edit_zone:request_merge` を送り、
 * 常駐しているアイコンパネルがこのウィンドウを開く(統合内容は `params.extra.pendingMerge`)。
 *
 * 地区がひとつ消える不可逆操作なので、3Dジェスチャだけで完結させず確定ボタンを挟む。
 * 確定時の送信(`POST /api/v1/zones/{id}/merge`)は他の編集と同じくUI側が行い、
 * 結果を `edit_zone:finish_merge` で Unity へ返して地区・道路を再取得させる。
 *
 * ウィンドウを×で閉じた場合は Rust 側(`window.rs` のクローズ処理)が取消を送るため、
 * Unity の確定待ちが取り残されることはない。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { buildApiUrl, fetchApi } from "../../utils/api";
import { sendEditZoneFinishMerge } from "../../utils/ipc";
import "./ZoneMergeConfirmWindow.css";

/** 3Dビューの地区編集モードで決まった「統合元 → 統合先」の組。 */
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

  // 確定待ちのまま別の統合が要求されると、ウィンドウは再利用されて props だけが差し替わる。
  // 前の失敗表示を残さないよう、対象の組が変わったら状態をリセットする。
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
    } catch {
      setError(t("zone.mergeFailed", "地区の統合に失敗しました"));
      setBusy(false);
    }
  };

  /** 取消。Unity側の確定待ちも解除する(地区編集モードは継続する)。 */
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
