/**
 * L1通知(重大通知)の共通処理
 *
 * バックエンドの GET /api/v1/notifications が返す「市長が今すぐ対処すべき重大事象」を
 * 取得・文言化するユーティリティ。コックピット(アラートアイコンの点滅・新規発生トースト)と
 * 通知一覧ウィンドウ(CityNotificationsWindow)の両方が使う。
 *
 * 通知メッセージの文言はバックエンドでは組み立てず、type と params だけが返る契約
 * なので、i18n キー notifications.types.<type> への差し込みはフロント側で行う。
 *
 * 通知レベル(L1/L2/L3)の考え方は genesis-docs/docs/programs/通知レベル管理.md を参照。
 */

import { buildApiUrl, fetchApi } from "./api";

/** 通知対象のエンティティ参照。クリックで対応する詳細ウィンドウを開くために使う。 */
export type L1NotificationEntity = {
  type: string;
  id: number;
};

/** L1通知1件分。id は差分検出用の安定キー(新規発生分だけトーストするために使う)。 */
export type L1Notification = {
  id: string;
  type: string;
  /** 発生時点のゲーム内日付(1800年1月1日からの経過日数) */
  created_date: number;
  params: Record<string, unknown>;
  entity: L1NotificationEntity | null;
  /**
   * 重要度。warning=都市の機能が損なわれつつあり、プレイヤーが数エポック内に打てる手が
   * ある問題(アイコン点滅+赤バッジ) / caution=手がない・年単位かかる、または壊れるのが
   * 成長・余力に留まる問題(点滅なしの黄バッジ)。
   */
  severity: "warning" | "caution";
};

/**
 * caution(注意)通知かどうか。未知の severity(バックエンドが先行して新種を返した場合)は
 * 安全側に倒して warning 扱いにする(重大な問題を控えめ表示に落とさないため)。
 */
export function isCautionNotification(notification: L1Notification): boolean {
  return notification.severity === "caution";
}

type NotificationsResponse = {
  notifications: L1Notification[];
};

/** 現在有効なL1通知の一覧をバックエンドから取得する。 */
export async function fetchL1Notifications(
  apiBaseUrl: string,
  signal?: AbortSignal,
): Promise<L1Notification[]> {
  const res = await fetchApi<NotificationsResponse>(
    buildApiUrl(apiBaseUrl, "/api/v1/notifications/"),
    { signal },
  );
  return res.notifications ?? [];
}

/** i18n の t 関数(react-i18next の useTranslation が返すもの)の最小シグネチャ。 */
type TranslateFn = (key: string, options?: Record<string, unknown>) => string;

/**
 * 通知1件を表示用メッセージへ文言化する。
 *
 * 未知の type(バックエンドが先行して新種を返した場合)は汎用文言へフォールバック
 * するため、フロントの i18n 追加が遅れても表示自体は壊れない。
 */
export function formatL1NotificationMessage(t: TranslateFn, notification: L1Notification): string {
  // 倒壊は死者の有無、公共施設の運営不能は原因(params.reason)で文言キーが変わる。
  let typeKey = notification.type;
  if (notification.type === "collapse" && Number(notification.params.deaths ?? 0) > 0) {
    typeKey = "collapse_with_deaths";
  }
  const reason = notification.params.reason;
  if (
    notification.type === "facility_inoperative" &&
    typeof reason === "string" &&
    reason !== "unknown"
  ) {
    typeKey = `facility_inoperative_${reason}`;
  }
  return t(`notifications.types.${typeKey}`, {
    ...notification.params,
    // 未知の reason は原因なしの汎用文言へ、未知の type は unknown へ段階的に落とす。
    defaultValue: t(`notifications.types.${notification.type}`, {
      ...notification.params,
      defaultValue: t("notifications.types.unknown"),
    }),
  });
}

/**
 * 通知対象エンティティ種別 → 詳細ウィンドウの window_type の対応表。
 * 通知一覧ウィンドウの行クリックで対象の詳細を開くために使う。
 */
export const NOTIFICATION_ENTITY_WINDOW_TYPES: Record<string, string> = {
  agent: "agent_detail",
  resource: "resource_detail",
  zone: "zone_detail",
};
