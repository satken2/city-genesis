import { useEffect, useMemo, useRef, useState } from "react";
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
import { GAME_VERIFIED, PLAY_MODE_TUTORIAL } from "../../constants";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { requestConfirm, showNotice } from "../../utils/confirmDialog";
import { resolveSupportedLanguage } from "../../utils/locale";
import { saveError } from "../../utils/saves";
import { loadAppSettings, saveAppSettings } from "../../utils/settings";
import { CoreVersion, frontendVersion } from "../../utils/version";

/** 初回起動確認で「はい」を選んだときに開始するチュートリアル(基本操作編)のID。 */
const FIRST_LAUNCH_TUTORIAL_ID = "controls";

type SessionStatePayload = {
  current_save_id: string | null;
  session: Record<string, unknown> | null;
  city?: Record<string, unknown>;
  isLoading: boolean;
  coreVersion: string | null;
  backendConnected: boolean;
};

type SavesSummaryResponse = {
  saves?: Record<string, unknown>;
};

type ProfileSummaryResponse = {
  tutorials_cleared?: string[];
  scenarios_cleared?: string[];
  achievements?: string[];
};

export function TopMenu({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const appWindow = useMemo(() => getCurrentWindow(), []);
  const [coreVersion, setCoreVersion] = useState<string | null>(null);
  const [backendConnected, setBackendConnected] = useState(false);
  const [topMenuVisible, setTopMenuVisible] = useState(false);
  const [promptingTutorial, setPromptingTutorial] = useState(false);
  const firstLaunchCheckedRef = useRef(false);
  const coreAvailable = backendConnected && !!coreVersion;
  const menuDisabled = !coreAvailable || promptingTutorial;
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
          setTopMenuVisible(false);
          appWindow.hide().catch(() => {});
          return;
        }

        const shouldShow = payload.current_save_id == null;
        setTopMenuVisible(shouldShow);

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
   * 完全な初回起動時だけ、基本操作チュートリアルを開始するか確認する。
   * 案内済みフラグ・既存セーブ・プレイヤープロフィールのいずれも空のときに一度だけ確認ポップアップを出し、
   * 承諾されたら基本操作編("controls")を直接ロードする。
   */
  useEffect(() => {
    if (!coreAvailable || !topMenuVisible || !apiBaseUrl || firstLaunchCheckedRef.current) return;
    let cancelled = false;

    const markPrompted = async () => {
      const latest = await loadAppSettings();
      if (!latest.first_launch_tutorial_prompted) {
        await saveAppSettings({ ...latest, first_launch_tutorial_prompted: true });
      }
    };

    const run = async () => {
      try {
        const settings = await loadAppSettings();
        if (cancelled || firstLaunchCheckedRef.current) return;
        if (settings.first_launch_tutorial_prompted) {
          firstLaunchCheckedRef.current = true;
          return;
        }

        const [savesRes, profileRes] = await Promise.all([
          fetchApi<SavesSummaryResponse>(buildApiUrl(apiBaseUrl, "/api/v1/saves"), { cache: "reload" }),
          fetchApi<ProfileSummaryResponse>(buildApiUrl(apiBaseUrl, "/api/v1/profile"), { cache: "reload" }),
        ]);
        if (cancelled || firstLaunchCheckedRef.current) return;
        firstLaunchCheckedRef.current = true;

        const hasExistingHistory =
          Object.keys(savesRes.saves ?? {}).length > 0 ||
          (profileRes.tutorials_cleared?.length ?? 0) > 0 ||
          (profileRes.scenarios_cleared?.length ?? 0) > 0 ||
          (profileRes.achievements?.length ?? 0) > 0;
        if (hasExistingHistory) {
          await markPrompted().catch(console.error);
          return;
        }

        // ポップアップ表示前に案内済みとして保存し、応答前にアプリを閉じても2回目以降は再表示しない。
        await markPrompted().catch(console.error);

        const lang = resolveSupportedLanguage(settings.language || i18n.language);
        const tFixed = i18n.getFixedT(lang);
        setPromptingTutorial(true);
        try {
          const confirmed = await requestConfirm({
            message: tFixed("topmenu.firstLaunchTutorialConfirm"),
            confirmLabel: tFixed("topmenu.firstLaunchTutorialYes"),
            cancelLabel: tFixed("topmenu.firstLaunchTutorialNo"),
          });
          if (!confirmed) return;

          await invoke("create_session", {
            acceptBalanceChange: true,
            scenarioId: null,
            language: lang,
            groundCover: null,
            assetPack: null,
            playMode: PLAY_MODE_TUTORIAL,
            tutorialId: FIRST_LAUNCH_TUTORIAL_ID,
            introDialog: null,
          });
        } catch (error) {
          console.error("[TopMenu] failed to start first-launch tutorial:", error);
          await showNotice({ message: saveError(error, tFixed) || tFixed("newGame.startFailed") });
        } finally {
          setPromptingTutorial(false);
        }
      } catch (error) {
        console.error("[TopMenu] first-launch tutorial check failed:", error);
      }
    };

    void run();
    return () => { cancelled = true; };
  }, [apiBaseUrl, coreAvailable, i18n, topMenuVisible]);

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
   * The application coordinates child-process termination, so frontend shutdown polling is unnecessary.
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
          disabled={menuDisabled}
          type="button"
        >
          {t("topmenu.newGame")}
        </button>
        <button className="topmenu-btn" onClick={handleLoad} disabled={menuDisabled} type="button">
          {t("topmenu.load")}
        </button>
        <button className="topmenu-btn" onClick={handleScenarioManager} disabled={menuDisabled} type="button">
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
        disabled={menuDisabled}
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
