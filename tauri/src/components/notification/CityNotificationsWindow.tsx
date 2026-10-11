/**
 * 重大通知(L1通知)一覧ウィンドウ
 *
 * コックピットのアラートアイコンをクリックすると開く、現在有効なL1通知の一覧。
 * データ源はバックエンドの GET /api/v1/notifications で、エポック末尾の
 * NotificationPhase が毎エポック再構築したリストをそのまま表示する。
 *
 * 通知は重要度2階調でセクション分けして表示する:
 *   - 警告(warning): 都市の機能が損なわれつつあり、今打てる手がある問題。赤系の行
 *   - 注意(caution): すぐには解決できない、または壊れるのが成長・余力に留まる問題。黄系の行
 *
 * - 各行は「アイコン + 文言化したメッセージ + 発生日」で構成する
 * - 対象エンティティを持つ通知は行クリックで対応する詳細ウィンドウ
 *   (経済主体詳細・天然資源詳細)を開く
 * - 通知が1件も無いときは「対処が必要な問題はない」旨のプレースホルダーを出す
 *
 * 右下に出る一過性のトースト通知(NotificationWindow)とは別物で、こちらは
 * 「未解決の危機が続いている限り表示され続ける一覧」を担う。
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
        // 中断以外の取得失敗はプレースホルダーで知らせる(空一覧と区別する)。
        if (!controller.signal.aborted) setLoadFailed(true);
      });
    return () => controller.abort();
  }, [apiBaseUrl]);

  const locale = resolveIntlLocale(i18n.language);

  /** 通知対象のエンティティ詳細ウィンドウを開く(対応表に無い種別は何もしない)。 */
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

  /** 通知1グループ(警告 or 注意)をセクション見出し付きで描画する。 */
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
