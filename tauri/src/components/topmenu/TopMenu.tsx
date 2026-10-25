import { useEffect, useMemo, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { openUrl } from "@tauri-apps/plugin-opener";
import { useTranslation } from "react-i18next";

import splashImage from "../../assets/images/splash.png";
import exitIcon from "../../assets/images/exit.png";
import githubIcon from "../../assets/images/github.png";
import assetManagerIcon from "../../assets/images/menu/display-building.svg";
import settingIcon from "../../assets/images/setting.png";
import "./TopMenu.css";
import { GAME_VERIFIED } from "../../constants";
import { CoreVersion, frontendVersion } from "../../utils/version";

type SessionStatePayload = {
  current_save_id: string | null;
  session: Record<string, unknown> | null;
  city?: Record<string, unknown>;
  isLoading: boolean;
  coreVersion: string | null;
  backendConnected: boolean;
};

export function TopMenu({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const appWindow = useMemo(() => getCurrentWindow(), []);
  const [coreVersion, setCoreVersion] = useState<string | null>(null);
  const [backendConnected, setBackendConnected] = useState(false);
  const coreAvailable = backendConnected && !!coreVersion;
  useEffect(() => {
    if (!apiBaseUrl) return;
    let active = true;
    void invoke<CoreVersion>("get_core_version").then(value => {
      if (!active) return;
      setCoreVersion(value.core_version);
    }).catch(() => {
      if (!active) return;
      setCoreVersion(null);
    });
    return () => { active = false; };
  }, [apiBaseUrl]);
  const [quitting, setQuitting] = useState(false);
  const [debugExecution, setDebugExecution] = useState(false);
  const [verified, setVerified] = useState<boolean | null>(null); // null (hidden) while session state is unavailable.

  /**
   * Receive `session-state` from Rust's session polling thread to control top menu visibility.
   *
   * Show while current_save_id is unset. Explicitly hide during game start/load (isLoading == true), when Tips is
   * displayed.
   */
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    const connectionListener = appWindow.listen<{ connected: boolean }>("backend-connection", (event) => {
      setBackendConnected(event.payload.connected);
    });
    appWindow
      .listen<SessionStatePayload>("session-state", (event) => {
        const payload = event.payload;
        setCoreVersion(payload.coreVersion);
        setBackendConnected(payload.backendConnected);
        setDebugExecution((payload.session?.debug_execution as boolean) ?? false);
        const verificationStatus = payload.session?.verification_status;
        setVerified(typeof verificationStatus === "number" ? verificationStatus === GAME_VERIFIED : null);

        // Hide the top menu while loading.
        // Tips is in front; prevent the top menu from appearing over it.
        if (payload.isLoading) {
          appWindow.hide().catch(() => {});
          return;
        }

        const shouldShow = payload.current_save_id == null;

        if (shouldShow) {
          appWindow.show().catch(() => {});
        } else {
          appWindow.hide().catch(() => {});
        }
      })
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);
    return () => {
      unlisten?.();
      void connectionListener.then((stop) => stop());
    };
  }, [appWindow]);

  /**
   * New Game button handler.
   *
   * Open scenario selection (new_game). After selecting a tutorial/scenario, NewGameWindow calls create_session.
   * Launch/reset Unity and show Tips only after starting options are confirmed.
   */
  async function handleNewGame() {
    try {
      await invoke("open_registered_window", { windowType: "new_game" });
    } catch (error) {
      console.error("[TopMenu] handleNewGame error:", error);
    }
  }

  async function handleLoad() {
    try {
      await invoke("open_registered_window", { windowType: "save_list" });
    } catch (error) {
      console.error("[TopMenu] handleLoad error:", error);
    }
  }

  /** Scenario management (list, terrain import, introduction, terrain editor) runs entirely in the UI. Unity is already running, started with the app by process::start_children. */
  async function handleScenarioManager() {
    try {
      await invoke("open_registered_window", { windowType: "scenario_manager" });
    } catch (error) {
      console.error("[TopMenu] handleScenarioManager error:", error);
    }
  }

  async function handleAssetManager() {
    try {
      await invoke("open_registered_window", { windowType: "asset_manager" });
    } catch (error) {
      console.error("[TopMenu] failed to open asset manager", error);
    }
  }

  async function handleOptions() {
    try {
      await invoke("open_registered_window", { windowType: "settings" });
    } catch (error) {
      console.error("[TopMenu] failed to open settings", error);
    }
  }

  async function handleMinimize() {
    try {
      await appWindow.minimize();
    } catch (error) {
      console.error("[TopMenu] failed to minimize window", error);
    }
  }

  /**
   * Exit handling.
   *
   * Rust's exit_app owns shutdown: notify Unity → coordinated backend shutdown → exit UI.
   * The core manages emergency autosave and the application coordinates child-process termination, so frontend shutdown
   * polling is unnecessary.
   */
  async function handleQuit() {
    setQuitting(true);
    try {
      await invoke("exit_app");
    } catch (error) {
      console.error("[TopMenu] handleQuit error:", error);
    }
  }

  async function handleGitHub() {
    await openUrl("https://github.com/satken2/city-genesis");
  }

  return (
    <div className="topmenu" data-tauri-drag-region style={{ backgroundImage: `url(${splashImage})` }}>
      <button
        className="topmenu-minimize"
        onClick={handleMinimize}
        type="button"
        aria-label={t("topmenu.minimize")}
        title={t("topmenu.minimize")}
      >
        <span aria-hidden="true" />
      </button>
      {/**
       * Main buttons.
       * Position absolutely at 45% from the splash screen's top, horizontally centered, with 35% screen width.
       * Keep placement stable regardless of debug displays/messages.
       */}
      <div className="topmenu-main-buttons">
        <button
          className="topmenu-btn topmenu-btn-primary"
          onClick={handleNewGame}
          disabled={!coreAvailable}
          type="button"
        >
          {t("topmenu.newGame")}
        </button>
        <button className="topmenu-btn" onClick={handleLoad} disabled={!coreAvailable} type="button">
          {t("topmenu.load")}
        </button>
        <button className="topmenu-btn" onClick={handleScenarioManager} disabled={!coreAvailable} type="button">
          {t("scenarios.manage")}
        </button>
      </div>

      {/**
       * GitHub button.
       * At bottom center, stack the icon and repository name vertically.
       * Click opens the developer's GitHub repository in the default browser.
       */}
      <button
        className="topmenu-btn topmenu-btn-github"
        onClick={handleGitHub}
        aria-label="satken2/city-genesis"
        title="satken2/city-genesis"
      >
        <img src={githubIcon} alt="GitHub" className="topmenu-github-icon" />
        <span className="topmenu-github-label">satken2/city-genesis</span>
      </button>

      <div className="topmenu-versions">
        <span>Core {coreAvailable ? `v${coreVersion}` : t("topmenu.coreConnectionFailed")}</span>
        <span>Graphic v{frontendVersion.frontend_version}</span>
        {/* Keep English regardless of language, as with the version label. */}
        {verified !== null && <span>{verified ? `Verified by ${debugExecution ? "Debug" : "Steam"}` : "Unverified"}</span>}
      </div>
      {/**
       * Asset manager button.
       * Square to the left of Options, displaying only its icon image.
       */}
      <button
        className="topmenu-btn topmenu-btn-asset-manager"
        onClick={handleAssetManager}
        disabled={!coreAvailable}
        aria-label={t("topmenu.assetManager")}
        title={t("topmenu.assetManager")}
      >
        <img src={assetManagerIcon} alt={t("topmenu.assetManager")} className="topmenu-asset-manager-icon" />
      </button>

      {/**
       * Options button.
       * Square to the left of Exit, displaying only its icon image.
       */}
      <button
        className="topmenu-btn topmenu-btn-options"
        onClick={handleOptions}
        aria-label={t("topmenu.options")}
        title={t("topmenu.options")}
      >
        <img src={settingIcon} alt={t("topmenu.options")} className="topmenu-options-icon" />
      </button>

      {/**
       * Exit button.
       * Square at bottom right, displaying an icon image.
       */}
      <button
        className="topmenu-btn topmenu-btn-quit"
        onClick={handleQuit}
        disabled={quitting}
        aria-label={quitting ? t("topmenu.quitting") : t("topmenu.quit")}
        title={quitting ? t("topmenu.quitting") : t("topmenu.quit")}
      >
        <img src={exitIcon} alt={quitting ? t("topmenu.quitting") : t("topmenu.quit")} className="topmenu-quit-icon" />
      </button>
    </div>
  );
}
