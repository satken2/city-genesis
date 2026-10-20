/**
 * System tray (persistent bottom-right panel)
 *
 * Collect app operations (system statistics, settings, save/load, reload), rather than gameplay controls.
 * The first slot shows a noninteractive mode icon in tutorial/read-only mode. Scenario mode provides one entry
 * to the scenario editor; normal debug mode uses that entry to enter scenario mode and open it.
 * Hide app icons marked `hiddenWhenReadOnly` in read-only mode.
 * While the core is unverified, a red label extends the tray upward.
 *
 * Width varies with button count and height with the label; report the measured size through `resize_system_tray`
 * to fit the window and prevent transparent margins intercepting Unity clicks, as with icon panel height.
 */
import { PLAY_MODE_SCENARIO, PLAY_MODE_TUTORIAL, PLAY_MODE_READ_ONLY } from "../../constants";


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
  const { visible, apiBaseUrl, playMode, debugExecution, unverified, epochExecuting } = useHudSession();
  const scenarioMode = playMode === PLAY_MODE_SCENARIO;
  const readOnly = playMode === PLAY_MODE_READ_ONLY;
  const modeIndicator = MODE_INDICATORS[playMode];
  const panelRef = useRef<HTMLDivElement | null>(null);
  const [opening, setOpening] = useState(false);

  /**
   * Report measured panel size as window size, without resending identical sizes.
   * This prevents endless round trips due to fractional rounding, matching IconPanel's design.
   */
  useEffect(() => {
    const element = panelRef.current;
    if (!element) return;

    let lastSent = { width: 0, height: 0 };
    const observer = new ResizeObserver(() => {
      const { width, height } = element.getBoundingClientRect();
      if (width <= 0 || height <= 0) return;
      if (Math.abs(width - lastSent.width) < 1 && Math.abs(height - lastSent.height) < 1) return;
      lastSent = { width, height };
      void invoke("resize_system_tray", { width, height }).catch(console.error);
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, [visible]);

  async function openScenarioTools() {
    if (!backendConnected || epochExecuting || opening) return;
    setOpening(true);
    try {
      if (!scenarioMode) {
        await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/sessions"), {
          method: "PATCH", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ play_mode: PLAY_MODE_SCENARIO }),
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
      {unverified && <div className="system-tray-unverified">{t("taskbar.trayUnverified")}</div>}
      <div className="system-tray-icons">
        {modeIndicator ? (
          <span className="system-tray-icon system-tray-mode" title={t(modeIndicator.labelKey)}>
            <img src={iconUrl(modeIndicator.src)} alt={t(modeIndicator.labelKey)} className="hud-icon-img" draggable={false} />
          </span>
        ) : (scenarioMode || debugExecution) && (
          <button id="scenario_tools" className="hud-icon-btn system-tray-icon" type="button"
            title={t(scenarioMode ? "windowType.scenario_tools" : "scenarioTools.editor.enterScenario")}
            disabled={!backendConnected || epochExecuting || opening} onClick={() => void openScenarioTools()}>
            <img src={iconUrl(scenarioMode ? "scenario_on.png" : "scenario_off.png")}
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
    </div>
  );
}
