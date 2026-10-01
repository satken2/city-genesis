/**
 * コックピット(画面下部中央の常駐パネル)
 *
 * プレイ中に最も頻繁に見る情報と操作を1枚に集約する。
 *   [常用ツール3種(2列のグリッド)] [公共サービス充足度] [都市名・施政スコア / 日付・予算メータ(2行2列)] [アラート] [エポック処理開始]
 *
 * - 常用ツール: 表示切替・都市マップ・ブルドーザ。
 *   ブルドーザはウィンドウを開かずUnityの削除ステートに入る常駐モードで、
 *   アイコンパネルの地区編集と排他(`hudMode`でウィンドウをまたいで共有)。
 *   解除し忘れによる誤削除を防ぐため、ON の間はパネル全体を工事現場風の
 *   黄黒斜線(`cockpit--bulldozer`)にして常駐中であることを目立たせる。
 *   神モード(templateMode)では建物・道路をクリックで即時に削除する(確認も取り消しもない)。
 *   通常は空き地・計画中の建物/道路・地形プロップだけが対象。
 * - 公共サービス充足度: 電力・水道などの需要/供給キャパシティを縦棒で常時表示する。
 *   クリックで公共施設ウィンドウを開く。
 * - アラート: L1通知は重要度2階調で表現する。warning(都市崩壊の差し迫った危機)が
 *   1件以上あると点滅+赤バッジ、caution(成長停止・緩慢な荒廃系)は点滅させず
 *   黄バッジのみ出す。緊急でない問題でアイコンが点滅し続けると、本当の危機の
 *   シグナルが薄まるため。点滅で気づける以上プレイヤーの操作を遮る必要はないため、
 *   トーストは出さない。
 *
 * BGMの再生と、3Dビューでのオブジェクト選択に応じたウィンドウ表示は、
 * HUDの中で常に1枚だけ存在するこのウィンドウが受け持つ(多重再生・多重オープンを避けるため)。
 */

import { useCallback, useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useTranslation } from "react-i18next";

import {
  sendChangeState,
  openDriveMode,
  sendRemoveObjectConfirmResult,
  IPC_EVENT_REMOVE_OBJECT_REQUEST_CONFIRM,
  IPC_EVENT_WORLD_SELECT_OBJECT,
} from "../../utils/ipc";
import { useMasterData } from "../../contexts/MasterDataContext";
import { setHintbarText } from "../../utils/hintbar";
import { formatEpochDate } from "../../utils/date";
import { resolveIntlLocale } from "../../utils/locale";
import { BgmManager } from "../../utils/bgm";
import { requestConfirm } from "../../utils/confirmDialog";
import {
  fetchL1Notifications,
  isCautionNotification,
  type L1Notification,
} from "../../utils/cityNotifications";
import { BudgetMiniMeter } from "./BudgetMiniMeter";
import { PublicServiceGauges } from "./PublicServiceGauges";
import { ScoreMiniMeter } from "./ScoreMiniMeter";
import { TOOL_ICONS, iconUrl, openRegisteredWindow, type HudIconDef } from "./hudCommon";
import { setHudMode, useHudMode } from "./hudMode";
import { useHudSession } from "./useHudSession";
import { useWeather } from "./useWeather";
import { WeatherStatus } from "./WeatherStatus";
import "./hud.css";
import "./Cockpit.css";

/** L1通知(重大通知)のポーリング間隔(ミリ秒)。通知は基本的にエポック実行で変わるため長めでよい。 */
const L1_NOTIFICATION_POLL_INTERVAL_MS = 30_000;

export function Cockpit() {
  const { t, i18n } = useTranslation();
  const { visible, apiBaseUrl, isLocked, cityName, currentDate, epochExecuting, templateMode } = useHudSession();
  const { weather, failed: weatherFailed, select: selectWeather } = useWeather(visible, templateMode);
  const mode = useHudMode();
  // ブルドーザの削除確認ダイアログでプロップ種別名を出すために参照する。
  const masterData = useMasterData();

  // L1通知(今すぐ対処が必要な重大事象)の現在有効な一覧。件数バッジと点滅の判定に使う。
  const [l1Notifications, setL1Notifications] = useState<L1Notification[]>([]);

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

  /**
   * ブルドーザの確認付き削除。Unity は範囲配置プロップ(森など)を即削除せず、常駐する本ウィンドウへ
   * 確認を求めてくる。散布範囲ごと消える不可逆操作なので確認ダイアログを挟み、回答は取消でも必ず返す
   * (返さないと Unity 側が確認待ちのまま次の範囲配置クリックを受け付けない)。
   */
  useEffect(() => {
    const appWindow = getCurrentWindow();
    let unlisten: (() => void) | undefined;

    appWindow
      .listen<{ object_type: string; id: number; prop_type?: number }>(
        IPC_EVENT_REMOVE_OBJECT_REQUEST_CONFIRM,
        async (event) => {
          const payload = event.payload;
          if (!payload?.object_type || payload.id == null) return;
          const name =
            payload.prop_type != null
              ? masterData?.prop_types?.[payload.prop_type]?.display_name ?? String(payload.prop_type)
              : "";
          // 取消・×閉じでも false が返るので、Unity 側の確認待ちは必ず解除される
          const accepted = await requestConfirm({
            message: t("taskbar.bulldozerConfirmScatter", { name }),
            confirmLabel: t("common.delete"),
            tone: "danger",
          });
          void sendRemoveObjectConfirmResult(payload.object_type, payload.id, accepted).catch(console.error);
        },
      )
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);

    return () => {
      unlisten?.();
    };
  }, [masterData, t]);

  // セッションが切れたら通知一覧を空にし、別の都市をロードし直したときに
  // 前の都市の件数バッジが残らないようにする。
  useEffect(() => {
    if (visible) return;
    setL1Notifications([]);
  }, [visible]);

  /**
   * L1通知(重大通知)のポーリング取得。
   * - 通知が1件以上あるとき、アラートアイコンを点滅させ件数バッジを出す
   *   (通知の内容はアイコンをクリックして開く一覧ウィンドウで確認する)
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
  }, [apiBaseUrl, epochExecuting]);

  /**
   * ブルドーザ(オブジェクト削除)モードのトグル。
   * ON で Unity を STATE_REMOVE_OBJECTS にし、OFF で STATE_NORMAL に戻す。
   * 神モード(templateMode)では instant_remove を付け、建物・道路をステータスを問わず確認なしで
   * 即時に削除させる(Unity は神モードかを知らないので UI が伝える)。
   * windowType "bulldozer" は送信元ラベルであり、実ウィンドウは開かない。
   */
  const toggleBulldozer = useCallback(async () => {
    const next = mode !== "bulldozer";
    try {
      await sendChangeState(
        next ? "STATE_REMOVE_OBJECTS" : "STATE_NORMAL",
        next && templateMode ? { instant_remove: true } : undefined,
        "bulldozer",
      );
      await setHintbarText(next ? t(templateMode ? "hint.bulldozerInstant" : "hint.bulldozer") : "");
      await setHudMode(next ? "bulldozer" : null);
    } catch (error) {
      console.error("failed to toggle bulldozer mode", error);
    }
  }, [mode, t, templateMode]);

  const handleToolClick = useCallback(
    async (icon: HudIconDef) => {
      if (icon.id === "bulldozer") {
        await toggleBulldozer();
        return;
      }
      if (icon.windowType) await openRegisteredWindow(icon.windowType);
    },
    [toggleBulldozer],
  );

  if (!visible) return null;

  const locale = resolveIntlLocale(i18n.language);
  const formattedDate = currentDate != null ? formatEpochDate(currentDate, locale) : "—";
  // warning(都市崩壊の危機)だけが点滅+赤バッジを駆動する。caution(成長停止系)は
  // 点滅しない黄バッジのみで、警告の緊急度シグナルを薄めないようにする。
  const cautionCount = l1Notifications.filter(isCautionNotification).length;
  const warningCount = l1Notifications.length - cautionCount;

  return (
    <div
      className={`hud-panel cockpit ${mode === "bulldozer" ? "cockpit--bulldozer" : ""} ${epochExecuting ? "hud-disabled" : ""}`}
    >
      <div className="cockpit-tools">
        {TOOL_ICONS.map((icon) => {
          const active = icon.id === "bulldozer" && mode === "bulldozer";
          const bulldozerTooltipKey = templateMode
            ? (active ? "taskbar.bulldozerInstantOn" : "taskbar.bulldozerInstantOff")
            : (active ? "taskbar.bulldozerOn" : "taskbar.bulldozerOff");
          const tooltip = icon.id === "bulldozer" ? t(bulldozerTooltipKey) : t(icon.tooltipKey);
          return (
            <button
              key={icon.id}
              id={icon.id}
              className={`hud-icon-btn cockpit-tool${active ? " hud-icon-btn--active" : ""}`}
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
        <button
          id="drive"
          className="hud-icon-btn cockpit-tool cockpit-drive"
          type="button"
          title={t("drive.title")}
          aria-label={t("drive.title")}
          disabled={isLocked}
          onClick={() => void (async () => {
            if (mode !== null) {
              await sendChangeState("STATE_NORMAL", undefined, "cockpit");
              await setHudMode(null);
              await setHintbarText("");
            }
            await openDriveMode(t("drive", { returnObjects: true }) as Record<string, string>);
          })().catch(console.error)}
        >
          <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" strokeWidth="1.7" aria-hidden="true">
            <circle cx="12" cy="12" r="9" /><circle cx="12" cy="12" r="2" />
            <path d="M3 10h7m4 0h7M10 14l-2 6m6-6 2 6" />
          </svg>
        </button>
      </div>

      <PublicServiceGauges
        apiBaseUrl={apiBaseUrl}
        epochExecuting={epochExecuting}
        onOpen={() => void openRegisteredWindow("public_facilities")}
      />

      <div className="cockpit-status">
        <button
          className="cockpit-city"
          type="button"
          title={t("cityRename.tooltip")}
          onClick={() => void openRegisteredWindow("city_rename")}
        >
          {cityName || "—"}
        </button>
        <ScoreMiniMeter apiBaseUrl={apiBaseUrl} onOpen={() => void openRegisteredWindow("achievements")} />
        <div className="cockpit-date-weather">
          <span className="cockpit-date">{formattedDate}</span>
          <WeatherStatus weather={weather} editable={templateMode} failed={weatherFailed} onSelect={selectWeather} />
        </div>
        <BudgetMiniMeter
          apiBaseUrl={apiBaseUrl}
          onOpen={() => void openRegisteredWindow("city_budget")}
        />
      </div>

      <button
        id="notifications"
        className={`hud-icon-btn cockpit-alert${warningCount > 0 ? " cockpit-alert--active" : ""}`}
        type="button"
        title={t("notifications.trayTooltip")}
        onClick={() => void openRegisteredWindow("city_notifications")}
      >
        <img src={iconUrl("tray_notifications.png")} alt={t("notifications.trayTooltip")} className="hud-icon-img" draggable={false} />
        {warningCount > 0 && <span className="cockpit-alert-badge">{warningCount}</span>}
        {cautionCount > 0 && (
          <span className="cockpit-alert-badge cockpit-alert-badge--caution">{cautionCount}</span>
        )}
      </button>

      <button
        id="execute"
        className="cockpit-execute"
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
