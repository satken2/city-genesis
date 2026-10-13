/**
 * システムトレイ(画面右下の常駐パネル)
 *
 * ゲームプレイそのものではなくアプリの操作(システム統計・設定・セーブ/ロード・再読み込み)を集めた小パネル。
 * 先頭はプレイモードの枠。チュートリアル・読み取り専用モードではモードを表すアイコン(押せない)を出す。
 * シナリオモードではシナリオ編集ウィンドウへの入口を1つ出す。
 * デバッグ実行の通常モードでは、同じ入口からシナリオモードに入って開く。
 * 読み取り専用モードでは、アプリの操作のうち `hiddenWhenReadOnly` のアイコンを出さない。
 *
 * ボタン数で横幅が変わるため、実測幅をTauriコマンド `resize_system_tray` で通知して
 * ウィンドウ幅を合わせる(透明な余白がUnityへのクリックを吸わないようにするため。
 * アイコンパネルの高さと同じ仕組み)。
 */
import { PLAY_MODE_SCEARIO, PLAY_MODE_TUTORIAL, PLAY_MODE_READ_ONLY } from "../../constants";


import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";

import { sendSessionForceReload } from "../../utils/ipc";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { TRAY_ICONS, iconUrl, openRegisteredWindow, type HudIconDef } from "./hudCommon";
import { useHudSession } from "./useHudSession";
import "./hud.css";
import "./SystemTray.css";

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
  const [opening, setOpening] = useState(false);

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

  async function openScenarioTools() {
    if (!backendConnected || epochExecuting || opening) return;
    setOpening(true);
    try {
      if (!godMode) {
        await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/sessions"), {
          method: "PATCH", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ play_mode: PLAY_MODE_SCEARIO }),
        });
      }
      await openRegisteredWindow("scenario_tools");
    } catch (error) { console.error("failed to open scenario tools", error); }
    finally { setOpening(false); }
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
      ) : (godMode || debugExecution) && (
        <button id="scenario_tools" className="hud-icon-btn system-tray-icon" type="button"
          title={t(godMode ? "windowType.scenario_tools" : "templateTools.editor.enterScenario")}
          disabled={!backendConnected || epochExecuting || opening} onClick={() => void openScenarioTools()}>
          <img src={iconUrl(godMode ? "templat_on.png" : "templat_off.png")}
            alt={t("windowType.scenario_tools")} className="hud-icon-img" draggable={false} />
        </button>
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
