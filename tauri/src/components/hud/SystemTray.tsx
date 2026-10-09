/**
 * システムトレイ(画面右下の常駐パネル)
 *
 * ゲームプレイそのものではなくアプリの操作(システム統計・設定・セーブ/ロード・再読み込み)を集めた小パネル。
 * 先頭はプレイモードの枠。チュートリアル・読み取り専用モードではモードを表すアイコン(押せない)を出す。
 * それ以外のモードでは、デバッグ実行時のみテンプレートモードの切替ボタンを出す。
 * 読み取り専用モードでは、アプリの操作のうち `hiddenWhenReadOnly` のアイコンを出さない。
 * テンプレートモード中はさらに、シナリオ制作者向けの都市開発支援ボタンが3つ増える。
 *   - 一括完成 … 計画中・建設中の全パス/建物を即時竣工させる(POST /api/v1/templates/complete-construction)
 *   - 路地作成 … 2D地図上で範囲と生成パターンを指定して路地を計画中で作る(street_grid ウィンドウ)
 *   - 民間化 … 貸し建物と民間参入可能な一般施設の公共施設フラグを一括解除する
 *
 * ボタン数で横幅が変わるため、実測幅をTauriコマンド `resize_system_tray` で通知して
 * ウィンドウ幅を合わせる(透明な余白がUnityへのクリックを吸わないようにするため。
 * アイコンパネルの高さと同じ仕組み)。
 */
import { PLAY_MODE_SCEARIO, PLAY_MODE_NORMAL, PLAY_MODE_TUTORIAL, PLAY_MODE_READ_ONLY } from "../../constants";


import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { emit } from "@tauri-apps/api/event";

import { sendSessionForceReload, sendSessionReload } from "../../utils/ipc";
import { ApiError, buildApiUrl, fetchApi } from "../../utils/api";
import { showNotification, dismissNotification } from "../../utils/notification";
import { TRAY_ICONS, iconUrl, openRegisteredWindow, type HudIconDef } from "./hudCommon";
import { useHudSession } from "./useHudSession";
import "./hud.css";
import "./SystemTray.css";

/** 一括完成のレスポンス(バックエンド complete_all_construction の戻り値) */
type CompleteConstructionResult = {
  completed_paths: number;
  completed_buildings: number;
  remaining_paths: number;
  remaining_buildings: number;
};

type PrivatizeFacilitiesResult = {
  privatized_agents: number;
};

const MODE_INDICATORS: Record<number, { src: string; labelKey: string }> = {
  [PLAY_MODE_TUTORIAL]: { src: "tray_tutorial.png", labelKey: "taskbar.trayTutorialMode" },
  [PLAY_MODE_READ_ONLY]: { src: "tray_read_only.png", labelKey: "taskbar.trayReadOnlyMode" },
};

export function SystemTray({ backendConnected = true }: { backendConnected?: boolean }) {
  const { t } = useTranslation();
  const { visible, apiBaseUrl, playMode, debugExecution, epochExecuting } = useHudSession();
  const godMode = playMode === PLAY_MODE_SCEARIO;
  const readOnly = playMode === PLAY_MODE_READ_ONLY;
  const modeIndicator = MODE_INDICATORS[playMode];
  const panelRef = useRef<HTMLDivElement | null>(null);
  const [completing, setCompleting] = useState(false);
  const [privatizing, setPrivatizing] = useState(false);

  /**
   * パネルの実測幅をウィンドウ幅として通知する。同じ幅の再送はしない
   * (端数の揺れで送信が往復し続けるのを防ぐため。IconPanel と同じ設計)。
   */
  useEffect(() => {
    const element = panelRef.current;
    if (!element) return;

    let lastSent = 0;
    const observer = new ResizeObserver(() => {
      const width = element.getBoundingClientRect().width;
      if (width <= 0 || Math.abs(width - lastSent) < 1) return;
      lastSent = width;
      void invoke("resize_system_tray", { width }).catch(console.error);
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, [visible]);

  /** テンプレートモード(製作者向けの編集モード)の切替。バックエンドのセッション設定を書き換える。 */
  async function toggleGodMode() {
    if (!backendConnected) return;
    try {
      await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/sessions"), {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ play_mode: godMode ? PLAY_MODE_NORMAL : PLAY_MODE_SCEARIO }),
      });
    } catch (error) {
      console.error("failed to toggle template mode", error);
    }
  }

  /**
   * 一括完成。エポック実行と同じく `epoch:executing` でHUDをロックし、完了後にUnityへ
   * セッション再読み込みを送る(地形の平坦化・交差点の分割が起きるため差分ではなく再読み込み)。
   */
  async function completeConstruction() {
    if (completing || privatizing || epochExecuting || !backendConnected) return;
    setCompleting(true);
    await emit("epoch:executing", { executing: true });
    await showNotification({ message: t("templateTools.completing") });
    try {
      const result = await fetchApi<CompleteConstructionResult>(
        buildApiUrl(apiBaseUrl, "/api/v1/templates/complete-construction"),
        { method: "POST" }
      );
      await sendSessionReload();
      await dismissNotification();
      const remaining = result.remaining_paths + result.remaining_buildings;
      void showNotification({
        message: t(remaining > 0 ? "templateTools.completedWithRemaining" : "templateTools.completed", {
          paths: result.completed_paths,
          buildings: result.completed_buildings,
          remaining,
        }),
        durationMs: 5000,
      });
    } catch (err) {
      await dismissNotification();
      let message: string;
      if (err instanceof ApiError) {
        const i18nKey = `errors.${err.code}`;
        message = i18nKey !== "errors.UNKNOWN_ERROR" ? t(i18nKey, { defaultValue: err.message }) : err.message;
      } else {
        message = t("templateTools.completeFailed");
      }
      void showNotification({ message, durationMs: 5000 });
    } finally {
      await emit("epoch:executing", { executing: false });
      setCompleting(false);
    }
  }

  async function privatizeFacilities() {
    if (privatizing || completing || epochExecuting || !backendConnected || !godMode) return;
    setPrivatizing(true);
    try {
      await emit("epoch:executing", { executing: true });
      await showNotification({ message: t("templateTools.privatizing") });
      const result = await fetchApi<PrivatizeFacilitiesResult>(
        buildApiUrl(apiBaseUrl, "/api/v1/templates/privatize-facilities"),
        { method: "POST" }
      );
      await sendSessionReload();
      await dismissNotification();
      void showNotification({
        message: t("templateTools.privatized", { count: result.privatized_agents }),
        durationMs: 5000,
      });
    } catch (err) {
      await dismissNotification();
      const message = err instanceof ApiError
        ? t(`errors.${err.code}`, { defaultValue: err.message })
        : t("templateTools.privatizeFailed");
      void showNotification({ message, durationMs: 5000 });
    } finally {
      setPrivatizing(false);
      await emit("epoch:executing", { executing: false });
    }
  }

  function handleTrayClick(icon: HudIconDef) {
    if (!backendConnected && icon.id !== "settings") return;
    if (icon.id === "reload") {
      void sendSessionForceReload();
      return;
    }
    if (icon.windowType) void openRegisteredWindow(icon.windowType);
  }

  if (!visible) return null;

  return (
    <div ref={panelRef} className={`hud-panel system-tray ${epochExecuting ? "hud-disabled" : ""}`}>
      {modeIndicator ? (
        <span className="system-tray-icon system-tray-mode" title={t(modeIndicator.labelKey)}>
          <img src={iconUrl(modeIndicator.src)} alt={t(modeIndicator.labelKey)} className="hud-icon-img" draggable={false} />
        </span>
      ) : debugExecution && (
        <button
          id="debug_template"
          className="hud-icon-btn system-tray-icon"
          type="button"
          title={t(godMode ? "taskbar.trayTemplateOn" : "taskbar.trayTemplateOff")}
          disabled={!backendConnected}
          onClick={() => void toggleGodMode()}
        >
          <img
            src={iconUrl(godMode ? "templat_on.png" : "templat_off.png")}
            alt={t(godMode ? "taskbar.trayTemplateOn" : "taskbar.trayTemplateOff")}
            className="hud-icon-img"
            draggable={false}
          />
        </button>
      )}
      {godMode && (
        <>
          <button
            id="template_complete"
            className="hud-icon-btn system-tray-icon"
            type="button"
            title={t("templateTools.trayComplete")}
            disabled={completing || privatizing || !backendConnected}
            onClick={() => void completeConstruction()}
          >
            <img src={iconUrl("tray_complete.png")} alt={t("templateTools.trayComplete")} className="hud-icon-img" draggable={false} />
          </button>
          <button
            id="template_privatize"
            className="hud-icon-btn system-tray-icon"
            type="button"
            title={t("templateTools.trayPrivatize")}
            disabled={privatizing || completing || epochExecuting || !backendConnected}
            onClick={() => void privatizeFacilities()}
          >
            <img src={iconUrl("menu_construction.png")} alt={t("templateTools.trayPrivatize")} className="hud-icon-img" draggable={false} />
          </button>
          <button
            id="template_street_grid"
            className="hud-icon-btn system-tray-icon"
            type="button"
            title={t("templateTools.trayStreetGrid")}
            disabled={!backendConnected}
            onClick={() => void openRegisteredWindow("street_grid")}
          >
            <img src={iconUrl("tray_street_grid.png")} alt={t("templateTools.trayStreetGrid")} className="hud-icon-img" draggable={false} />
          </button>
        </>
      )}
      {TRAY_ICONS.filter((icon) => !(readOnly && icon.hiddenWhenReadOnly)).map((icon) => (
        <button
          key={icon.id}
          id={icon.id}
          className="hud-icon-btn system-tray-icon"
          type="button"
          title={t(icon.tooltipKey)}
          disabled={!backendConnected && icon.id !== "settings"}
          style={{ padding: icon.padding }}
          onClick={() => handleTrayClick(icon)}
        >
          <img src={iconUrl(icon.src)} alt={t(icon.tooltipKey)} className="hud-icon-img" draggable={false} />
        </button>
      ))}
    </div>
  );
}
