/**
 * 路線の運休理由コード(APIの pause_reason)から、表示文言の i18n キーを引く。
 * 公共交通ウィンドウの一覧(運休の印のツールチップ)と路線詳細パネル(注意バナー)が共用する。
 */

const PAUSE_REASON_KEYS: Record<string, string> = {
  route_disconnected: "transit.pauseReason.routeDisconnected",
  no_underlay: "transit.pauseReason.noUnderlay",
  train_too_long: "transit.pauseReason.trainTooLong",
};

/** 理由コードが無い運休(車両不足など)や未知のコードは汎用文言にする */
export function pauseReasonKey(reason: string | null | undefined): string {
  return (reason && PAUSE_REASON_KEYS[reason]) || "transit.pauseReason.generic";
}
