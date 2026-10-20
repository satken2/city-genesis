/**
 * Major (L1) notification list window
 *
 * Opened from the cockpit alert icon, showing currently active L1 notifications.
 * Display the list from backend GET /api/v1/notifications, rebuilt every epoch by NotificationPhase at epoch
 * end.
 *
 * Group into two severity sections:
 * - Warning: city functions are deteriorating and action is available now; red rows.
 * - Caution: not immediately solvable, or affecting only growth/spare capacity; yellow rows.
 *
 * - Each row contains an icon, localized message, and occurrence date.
 * - Clicking a row with a target entity opens its detail window (agent or natural resource).
 * - If empty, show a placeholder stating there are no issues requiring action.
 *
 * Unlike transient bottom-right toast notifications (NotificationWindow), this list remains populated while
 * unresolved crises persist.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import {
  fetchL1Notifications,
  formatL1NotificationMessage,
  isCautionNotification,
  NOTIFICATION_ENTITY_WINDOW_TYPES,
  type L1Notification,
} from "../../utils/cityNotifications";
import { formatEpochDate } from "../../utils/date";
import { resolveIntlLocale } from "../../utils/locale";
import crisisIcon from "../../assets/images/ui/crisis.png";
import infoIcon from "../../assets/images/ui/info.png";
import "./CityNotificationsWindow.css";

type CityNotificationsWindowProps = {
  apiBaseUrl: string;
};

export function CityNotificationsWindow({ apiBaseUrl }: CityNotificationsWindowProps) {
  const { t, i18n } = useTranslation();
  const [notifications, setNotifications] = useState<L1Notification[] | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);

  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    fetchL1Notifications(apiBaseUrl, controller.signal)
      .then((items) => {
        setNotifications(items);
        setLoadFailed(false);
      })
      .catch(() => {
        // Show non-abort retrieval failures in a placeholder to distinguish them from an empty list.
        if (!controller.signal.aborted) setLoadFailed(true);
      });
    return () => controller.abort();
  }, [apiBaseUrl]);

  const locale = resolveIntlLocale(i18n.language);

  /** Open the notification target's detail window; do nothing for unmapped types. */
  async function openEntityDetail(notification: L1Notification) {
    const entity = notification.entity;
    if (!entity) return;
    const windowType = NOTIFICATION_ENTITY_WINDOW_TYPES[entity.type];
    if (!windowType) return;
    try {
      await invoke("open_registered_window", {
        windowType,
        entityId: String(entity.id),
      });
    } catch (error) {
      console.error("failed to open entity detail from notification", error);
    }
  }

  /** Render a notification group (warning or caution) with a section heading. */
  function renderSection(titleKey: string, items: L1Notification[], caution: boolean) {
    if (items.length === 0) return null;
    return (
      <section className="city-notifications-section">
        <h3 className="city-notifications-section-title">{t(titleKey)}</h3>
        <ul className="city-notifications-list">
          {items.map((notification) => {
            const clickable =
              notification.entity != null &&
              NOTIFICATION_ENTITY_WINDOW_TYPES[notification.entity.type] != null;
            return (
              <li key={notification.id}>
                <button
                  type="button"
                  className={`city-notifications-row${caution ? " city-notifications-row--caution" : ""}${clickable ? " city-notifications-row--clickable" : ""}`}
                  disabled={!clickable}
                  onClick={() => void openEntityDetail(notification)}
                >
                  <img
                    src={caution ? infoIcon : crisisIcon}
                    alt=""
                    className="city-notifications-row-icon"
                    draggable={false}
                  />
                  <span className="city-notifications-row-message">
                    {formatL1NotificationMessage(t, notification)}
                  </span>
                  <span className="city-notifications-row-date">
                    {formatEpochDate(notification.created_date, locale)}
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      </section>
    );
  }

  const warnings = (notifications ?? []).filter((n) => !isCautionNotification(n));
  const cautions = (notifications ?? []).filter(isCautionNotification);

  return (
    <div className="city-notifications-root">
      {notifications === null ? (
        <p className="city-notifications-empty">
          {loadFailed ? t("notifications.loadFailed") : "…"}
        </p>
      ) : notifications.length === 0 ? (
        <p className="city-notifications-empty">{t("notifications.empty")}</p>
      ) : (
        <>
          {renderSection("notifications.sections.warning", warnings, false)}
          {renderSection("notifications.sections.caution", cautions, true)}
        </>
      )}
    </div>
  );
}
