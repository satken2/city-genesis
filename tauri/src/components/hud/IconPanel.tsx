/**
 * Icon panel (persistent vertical menu at the left edge)
 *
 * A column of icons opens menu windows (roads, construction, regulations, statistics, etc.).
 * `MENU_ICONS` in `hudCommon.ts` owns order and target windows. Prefer images under `assets/images/panel`,
 * falling back to MENU_ICONS images when undefined.
 *
 * Hide city-building operations (`hiddenWhenReadOnly`) in read-only mode.
 *
 * Rust positions the dedicated `icon-panel` window at the screen's left edge, vertically centered.
 * Icon count varies with play mode, so report measured height through `resize_icon_panel` to fit the window.
 * Otherwise transparent margins intercept clicks and block Unity controls.
 */
import { PLAY_MODE_SCENARIO, PLAY_MODE_READ_ONLY } from "../../constants";


import { Fragment, useCallback, useEffect, useRef } from "react";
import { invoke } from "@tauri-apps/api/core";
import { useTranslation } from "react-i18next";

import { setHintbarText } from "../../utils/hintbar";
import {
  MENU_ICONS,
  iconUrl,
  openRegisteredWindow,
  type HudIconDef,
} from "./hudCommon";
import { useHudSession } from "./useHudSession";
import "./hud.css";
import "./IconPanel.css";

const panelIconModules = import.meta.glob<{ default: string }>(
  "../../assets/images/panel/*.png",
  { eager: true }
);

function resolveIconSrc(icon: HudIconDef): string {
  const fileKey = icon.panelIcon ?? icon.id;
  return (
    panelIconModules[`../../assets/images/panel/${fileKey}.png`]?.default ??
    iconUrl(icon.src)
  );
}

export function IconPanel() {
  const { t } = useTranslation();
  const { visible, playMode, isLocked, epochExecuting } = useHudSession();
  const scenarioMode = playMode === PLAY_MODE_SCENARIO;
  const readOnly = playMode === PLAY_MODE_READ_ONLY;
  const panelRef = useRef<HTMLElement | null>(null);

  /**
   * Report the panel's actual height, including borders, to the window.
   * Observe with ResizeObserver to cover icon count changes when switching play modes.
   * Do not resend identical heights. Although window resizing should not affect panel height, this also prevents
   * endless exchanges caused by fractional rounding.
   */
  useEffect(() => {
    const element = panelRef.current;
    if (!element) return;

    let lastSent = 0;
    const observer = new ResizeObserver(() => {
      const height = element.getBoundingClientRect().height;
      if (height <= 0 || Math.abs(height - lastSent) < 1) return;
      lastSent = height;
      void invoke("resize_icon_panel", { height }).catch(console.error);
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, [visible]);

  const handleIconClick = useCallback(
    async (icon: HudIconDef) => {
      // Icons calling Tauri commands directly do not alter Unity's state, so
      // preserve the persistent mode and hint bar.
      if (icon.command) {
        await invoke(icon.command).catch(console.error);
        return;
      }
      if (!icon.windowType) return;
      // Opening an editing window clears the persistent mode through Rust's apply_mutex_rules.
      await setHintbarText(icon.hintKey ? t(icon.hintKey) : "");
      await openRegisteredWindow(icon.windowType);
    },
    [t],
  );

  if (!visible) return null;

  const icons = MENU_ICONS.filter((icon) => (scenarioMode || !icon.scenarioOnly) && !(readOnly && icon.hiddenWhenReadOnly));

  return (
    <nav ref={panelRef} className={`icon-panel ${epochExecuting ? "hud-disabled" : ""}`}>
      {icons.map((icon, index) => {
        // Add a separator before author controls to distinguish them visually from normal gameplay menus.
        const startsScenarioGroup = icon.scenarioOnly && !icons[index - 1]?.scenarioOnly;
        return (
          <Fragment key={icon.id}>
            {startsScenarioGroup && <div className="icon-panel-divider" />}
            <button
              id={icon.id}
              className="hud-icon-btn icon-panel-slot"
              type="button"
              title={t(icon.tooltipKey)}
              disabled={isLocked && !icon.enabledWhenLocked}
              onClick={() => void handleIconClick(icon)}
            >
              <img src={resolveIconSrc(icon)} alt={t(icon.tooltipKey)} className="hud-icon-img" draggable={false} />
            </button>
          </Fragment>
        );
      })}
    </nav>
  );
}
