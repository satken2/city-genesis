/**
 * Shared L1 (critical) notification logic. Fetch and format events requiring immediate mayor attention from GET
 * /api/v1/notifications. Used by cockpit flashing alerts/new-event toasts and CityNotificationsWindow. The
 * backend returns only type and params, not messages; the frontend interpolates i18n notifications.types.<type>.
 * See city-genesis/docs/ja/programs/通知レベル管理.md for L1/L2/L3 concepts.
 */

import { buildApiUrl, fetchApi } from "./api";

/** Reference to the affected entity, used to open its detail window on click. */
export type L1NotificationEntity = {
  type: string;
  id: number;
};

/** One L1 notification. id is a stable diff key used to toast only newly occurring events. */
export type L1Notification = {
  id: string;
  type: string;
  /** In-game date at occurrence (days since January 1, 1800). */
  created_date: number;
  params: Record<string, unknown>;
  entity: L1NotificationEntity | null;
  /**
   * Severity. warning: city function is deteriorating and the player can act within a few epochs (flashing icon
   * + red badge). caution: no available action, resolution takes years, or impact affects only growth/reserve
   * capacity (nonflashing yellow badge).
   */
  severity: "warning" | "caution";
};

/**
 * Whether this is a caution notification. Treat unknown severity (new backend types arriving first) as warning
 * to avoid understating critical problems.
 */
export function isCautionNotification(notification: L1Notification): boolean {
  return notification.severity === "caution";
}

type NotificationsResponse = {
  notifications: L1Notification[];
};

/** Fetch currently active L1 notifications from the backend. */
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

/** Minimal signature for i18n's t function (returned by react-i18next useTranslation). */
type TranslateFn = (key: string, options?: Record<string, unknown>) => string;

/**
 * Format one notification for display. Unknown types fall back to a generic message, so display remains
 * functional if frontend i18n additions lag behind backend types.
 */
export function formatL1NotificationMessage(t: TranslateFn, notification: L1Notification): string {
  // Collapse wording depends on casualties; public facility operating failures depend on cause (params.reason).
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
    // Fall back from unknown reason to generic wording without a cause, then from unknown type to unknown.
    defaultValue: t(`notifications.types.${notification.type}`, {
      ...notification.params,
      defaultValue: t("notifications.types.unknown"),
    }),
  });
}

/**
 * Map notification entity types to detail window window_type. Used to open entity details on notification row
 * clicks.
 */
export const NOTIFICATION_ENTITY_WINDOW_TYPES: Record<string, string> = {
  agent: "agent_detail",
  resource: "resource_detail",
  zone: "zone_detail",
};
