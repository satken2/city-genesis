/**
 * コックピット(画面下部中央の常駐パネル)
 *
 * プレイ中に最も頻繁に見る情報と操作を1枚に集約する。
 *   [常用ツール4種(2行2列)] [都市名 / 日付・予算メータ] [アラート] [エポック処理開始]
 *
 * - 常用ツール: 表示切替・都市マップ・地形セル開放・ブルドーザ。
 *   ブルドーザはウィンドウを開かずUnityの削除ステートに入る常駐モードで、
 *   アイコンパネルの地区編集と排他(`hudMode`でウィンドウをまたいで共有)。
 * - アラート: L1通知(今すぐ対処が必要な重大事象)が1件以上あると点滅+件数バッジを出し、
 *   新規発生分は右下トーストでも知らせる。
 *
 * BGMの再生と、3Dビューでのオブジェクト選択に応じたウィンドウ表示は、
 * HUDの中で常に1枚だけ存在するこのウィンドウが受け持つ(多重再生・多重オープンを避けるため)。
 */

import { useCallback, useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useTranslation } from "react-i18next";

import { sendChangeState, IPC_EVENT_WORLD_SELECT_OBJECT } from "../../utils/ipc";
import { setHintbarText } from "../../utils/hintbar";
import { formatEpochDate } from "../../utils/date";
import { BgmManager } from "../../utils/bgm";
import { showNotification } from "../../utils/notification";
import {
  fetchL1Notifications,
  formatL1NotificationMessage,
  type L1Notification,
} from "../../utils/cityNotifications";
import { BudgetMiniMeter } from "./BudgetMiniMeter";
import { TOOL_ICONS, iconUrl, openRegisteredWindow, type HudIconDef } from "./hudCommon";
import { setHudMode, useHudMode } from "./hudMode";
import { useHudSession, useTutorialHighlight } from "./useHudSession";
import crisisIcon from "../../assets/images/ui/crisis.png";
import "./hud.css";
import "./Cockpit.css";

/** L1通知(重大通知)のポーリング間隔(ミリ秒)。通知は基本的にエポック実行で変わるため長めでよい。 */
const L1_NOTIFICATION_POLL_INTERVAL_MS = 30_000;

export function Cockpit() {
  const { t, i18n } = useTranslation();
  const { visible, apiBaseUrl, isLocked, cityName, currentDate, epochExecuting } = useHudSession();
  const mode = useHudMode();
  const getBtnClassName = useTutorialHighlight();

  // L1通知(今すぐ対処が必要な重大事象)の現在有効な一覧。
  const [l1Notifications, setL1Notifications] = useState<L1Notification[]>([]);
  // 前回取得時の通知ID集合。新規発生分だけをトーストするための差分キー。
  // null は「未取得(初回)」を表し、初回取得分はトーストしない
  // (セーブロード直後に復元された既存通知で鳴らさないため)。
  const l1PrevIdsRef = useRef<Set<string> | null>(null);

  // BGM管理クラスのライフサイクル管理
  useEffect(() => {
    const bgm = new BgmManager();
    return () => {
      bgm.destroy();
    };
  }, []);

  // グラフィックプログラムからのオブジェクト選択イベントを受信し、対応する詳細ウィンドウを開く
  useEffect(() => {
    const appWindow = getCurrentWindow();
    let unlistenSelect: (() => void) | undefined;

    appWindow
      .listen<{ window_type: string; entity: { type: string; id: string } }>(
        IPC_EVENT_WORLD_SELECT_OBJECT,
        (event) => {
          const payload = event.payload;
          if (payload.window_type === "zone_detail" && payload.entity?.type === "zone") {
            void invoke("open_registered_window", {
              windowType: "zone_detail",
              entityId: payload.entity.id,
            });
          }
        },
      )
      .then((fn) => {
        unlistenSelect = fn;
      })
      .catch(console.error);

    return () => {
      unlistenSelect?.();
    };
  }, []);

  // 別セーブをロードし直したとき、旧セーブの通知が新セーブの「新規発生」として
  // トーストされないよう、セッションが切れたら差分状態もリセットする。
  useEffect(() => {
    if (visible) return;
    setL1Notifications([]);
    l1PrevIdsRef.current = null;
  }, [visible]);

  /**
   * L1通知(重大通知)のポーリング取得。
   * - 通知が1件以上あるとき、アラートアイコンを点滅させ件数バッジを出す
   * - 前回取得時に無かった id の通知は「新規発生分」として右下トーストでも知らせる
   *   (初回取得分はロード済み状態の復元なのでトーストしない)
   * - エポック実行終了(epochExecuting: true→false)で本エフェクトが再実行され、
   *   実行直後の最新状態を即時反映する(実行中はポーリング自体を止める)
   */
  useEffect(() => {
    if (!apiBaseUrl || epochExecuting) return;
    const controller = new AbortController();
    let cancelled = false;

    async function refresh() {
      try {
        const items = await fetchL1Notifications(apiBaseUrl, controller.signal);
        if (cancelled) return;
        setL1Notifications(items);
        const prevIds = l1PrevIdsRef.current;
        if (prevIds !== null) {
          for (const item of items) {
            if (!prevIds.has(item.id)) {
              void showNotification({
                message: formatL1NotificationMessage(t, item),
                iconPath: crisisIcon,
                durationMs: 8000,
              });
            }
          }
        }
        l1PrevIdsRef.current = new Set(items.map((item) => item.id));
      } catch {
        // 取得失敗(中断含む)は前回値を保持し、画面をちらつかせない(BudgetMiniMeterと同方針)。
      }
    }

    void refresh();
    const timer = setInterval(() => void refresh(), L1_NOTIFICATION_POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      controller.abort();
      clearInterval(timer);
    };
  }, [apiBaseUrl, epochExecuting, t]);

  /**
   * ブルドーザ(オブジェクト削除)モードのトグル。
   * ON で Unity を STATE_REMOVE_OBJECTS にし、OFF で STATE_NORMAL に戻す。
   * windowType "bulldozer" は送信元ラベルであり、実ウィンドウは開かない。
   */
  const toggleBulldozer = useCallback(async () => {
    const next = mode !== "bulldozer";
    try {
      await sendChangeState(next ? "STATE_REMOVE_OBJECTS" : "STATE_NORMAL", undefined, "bulldozer");
      await setHintbarText(next ? t("hint.bulldozer") : "");
      await setHudMode(next ? "bulldozer" : null);
    } catch (error) {
      console.error("failed to toggle bulldozer mode", error);
    }
  }, [mode, t]);

  const handleToolClick = useCallback(
    async (icon: HudIconDef) => {
      if (icon.id === "bulldozer") {
        await toggleBulldozer();
        return;
      }
      if (icon.id === "unlock") {
        // 地形セル開放はウィンドウを開いた上でUnityをセル選択ステートにする
        await setHudMode(null);
        await setHintbarText(t("hint.terrainUnlock"));
        await openRegisteredWindow("terrain_unlock");
        await sendChangeState("STATE_UNLOCK_CELL", undefined, "terrain_unlock");
        return;
      }
      if (icon.windowType) await openRegisteredWindow(icon.windowType);
    },
    [toggleBulldozer, t],
  );

  if (!visible) return null;

  const locale = i18n.language === "ja" ? "ja-JP" : "en-US";
  const formattedDate = currentDate != null ? formatEpochDate(currentDate, locale) : "—";
  const alertCount = l1Notifications.length;

  return (
    <div className={`hud-panel cockpit ${epochExecuting ? "hud-disabled" : ""}`}>
      <div className="cockpit-tools">
        {TOOL_ICONS.map((icon) => {
          const active = icon.id === "bulldozer" && mode === "bulldozer";
          const tooltip = icon.id === "bulldozer"
            ? t(active ? "taskbar.bulldozerOn" : "taskbar.bulldozerOff")
            : t(icon.tooltipKey);
          return (
            <button
              key={icon.id}
              id={icon.id}
              className={`${getBtnClassName(icon.id, "hud-icon-btn cockpit-tool")}${active ? " hud-icon-btn--active" : ""}`}
              type="button"
              title={tooltip}
              aria-pressed={icon.id === "bulldozer" ? active : undefined}
              style={{ padding: icon.padding }}
              onClick={() => void handleToolClick(icon)}
            >
              <img src={iconUrl(icon.src)} alt={tooltip} className="hud-icon-img" draggable={false} />
            </button>
          );
        })}
      </div>

      <div className="cockpit-status">
        <span className="cockpit-city">{cityName || "—"}</span>
        <div className="cockpit-status-row">
          <span className="cockpit-date">{formattedDate}</span>
          <BudgetMiniMeter apiBaseUrl={apiBaseUrl} onOpen={() => void openRegisteredWindow("city_budget")} />
        </div>
      </div>

      <button
        id="notifications"
        className={`${getBtnClassName("notifications", "hud-icon-btn cockpit-alert")}${alertCount > 0 ? " cockpit-alert--active" : ""}`}
        type="button"
        title={t("notifications.trayTooltip")}
        onClick={() => void openRegisteredWindow("city_notifications")}
      >
        <img src={iconUrl("tray_notifications.png")} alt={t("notifications.trayTooltip")} className="hud-icon-img" draggable={false} />
        {alertCount > 0 && <span className="cockpit-alert-badge">{alertCount}</span>}
      </button>

      <button
        id="execute"
        className={getBtnClassName("execute", "cockpit-execute")}
        type="button"
        title={t("taskbar.menuExecute")}
        disabled={isLocked}
        onClick={() => void openRegisteredWindow("epoch_confirm")}
      >
        {t("taskbar.menuExecute")}
        <img src={iconUrl("menu_execute.png")} alt="" className="cockpit-execute-img" draggable={false} />
      </button>
    </div>
  );
}
