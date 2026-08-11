/**
 * システムトレイ(画面右下の常駐パネル)
 *
 * ゲームプレイそのものではなくアプリの操作(設定・セーブ/ロード・再読み込み)を集めた小パネル。
 * デバッグ実行時のみ、テンプレートモードの切替ボタンが先頭に増える。
 */

import { useTranslation } from "react-i18next";

import { sendSessionForceReload } from "../../utils/ipc";
import { TRAY_ICONS, iconUrl, openRegisteredWindow, type HudIconDef } from "./hudCommon";
import { useHudSession, useTutorialHighlight } from "./useHudSession";
import "./hud.css";
import "./SystemTray.css";

export function SystemTray() {
  const { t } = useTranslation();
  const { visible, apiBaseUrl, templateMode, debugExecution, epochExecuting } = useHudSession();
  const getBtnClassName = useTutorialHighlight();

  /** テンプレートモード(製作者向けの編集モード)の切替。バックエンドのセッション設定を書き換える。 */
  async function toggleTemplateMode() {
    try {
      await fetch(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/sessions`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ template_mode: !templateMode }),
      });
    } catch (error) {
      console.error("failed to toggle template mode", error);
    }
  }

  function handleTrayClick(icon: HudIconDef) {
    if (icon.id === "reload") {
      void sendSessionForceReload();
      return;
    }
    if (icon.windowType) void openRegisteredWindow(icon.windowType);
  }

  if (!visible) return null;

  return (
    <div className={`hud-panel system-tray ${epochExecuting ? "hud-disabled" : ""}`}>
      {debugExecution && (
        <button
          id="debug_template"
          className={getBtnClassName("debug_template", "hud-icon-btn system-tray-icon")}
          type="button"
          title={t(templateMode ? "taskbar.trayTemplateOn" : "taskbar.trayTemplateOff")}
          onClick={() => void toggleTemplateMode()}
        >
          <img
            src={iconUrl(templateMode ? "templat_on.png" : "templat_off.png")}
            alt={t(templateMode ? "taskbar.trayTemplateOn" : "taskbar.trayTemplateOff")}
            className="hud-icon-img"
            draggable={false}
          />
        </button>
      )}
      {TRAY_ICONS.map((icon) => (
        <button
          key={icon.id}
          id={icon.id}
          className={getBtnClassName(icon.id, "hud-icon-btn system-tray-icon")}
          type="button"
          title={t(icon.tooltipKey)}
          style={{ padding: icon.padding }}
          onClick={() => handleTrayClick(icon)}
        >
          <img src={iconUrl(icon.src)} alt={t(icon.tooltipKey)} className="hud-icon-img" draggable={false} />
        </button>
      ))}
    </div>
  );
}
