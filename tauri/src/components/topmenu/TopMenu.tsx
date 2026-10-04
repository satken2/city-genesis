import { useEffect, useMemo, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { openUrl } from "@tauri-apps/plugin-opener";
import { useTranslation } from "react-i18next";

import splashImage from "../../assets/images/splash.png";
import exitIcon from "../../assets/images/exit.png";
import githubIcon from "../../assets/images/github.png";
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
  const [verified, setVerified] = useState<boolean | null>(null); // セッションを取得できない間は null(表示しない)

  /**
   * Rust 側のセッションポーリングスレッドから配信される `session-state` イベントを受信し、
   * トップメニューウィンドウの表示/非表示を制御する。
   *
   * current_save_id が未設定の間はトップメニューを表示する。
   * ただし、ゲーム開始/ロード処理中（isLoading == true）の間は Tips が表示されているため、
   * トップメニューは明示的に隠す。
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

        // ローディング中はトップメニューを隠す。
        // Tips が前面に表示されており、トップメニューがその上に出てくるのを防ぐ。
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
   * 新規ゲームボタン押下時の処理。
   *
   * シナリオ選択ウィンドウ（new_game）を開く。
   * ユーザーはチュートリアルまたはシナリオを選択後、NewGameWindow 内で create_session を呼び出す。
   * Unityの起動・リセットとTipsの表示は、開始設定を確定した後に行う。
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

  /** シナリオ管理(一覧・地形の取り込み・紹介情報・地形エディタ)はUIだけで完結する。都市を開くときは create_session が Unity を起動する。 */
  async function handleScenarioManager() {
    try {
      await invoke("open_registered_window", { windowType: "scenario_manager" });
    } catch (error) {
      console.error("[TopMenu] handleScenarioManager error:", error);
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
   * デバッグ初期化ボタン押下時の処理。
   *
   * Rust 側の start_debug_init を呼び出し、Unity 起動 → Tips 表示 →
   * バックエンド POST → ポーリング待機 → HUD 表示 の一連のフローを実行する。
   */
  async function handleDebugInit() {
    try {
      await invoke("start_debug_init");
    } catch (error) {
      console.error("[TopMenu] handleDebugInit error:", error);
    }
  }

  /**
   * ゲーム終了処理。
   *
   * 終了シーケンス(Unityへの通知 → バックエンドの統括シャットダウン → UI終了)は
   * Rust 側の exit_app に一元化されている。バックエンドの後始末(緊急オートセーブ・
   * 子プロセスの終了)は genesis.py スーパーバイザが統括するため、
   * フロントエンドでの停止待ちポーリングは不要。
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
       * メインボタン群。
       * スプラッシュ画面上部から 45% の位置に、左右中央揃え、幅は画面全体の 35% で配置する。
       * デバッグ表示の有無やメッセージの出現に関係なく位置がズレないよう、絶対配置を用いる。
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
       * デバッグ初期化ボタン。
       * デバッグ実行時のみ設定ボタンの左に表示する。
       */}
      {debugExecution && (
        <button
          className="topmenu-btn topmenu-btn-debug"
          onClick={handleDebugInit}
          disabled={!coreAvailable}
          title={t("topmenu.debugInit")}
          aria-label={t("topmenu.debugInit")}
          type="button"
        >
          D
        </button>
      )}

      {/**
       * GitHub ボタン。
       * ウィンドウ下部中央に配置し、アイコンとリポジトリ名を縦に並べて表示する。
       * クリックすると開発者の GitHub リポジトリを既定のブラウザで開く。
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
        {/* バージョン表記と同じく、言語に依らず英語で固定する。 */}
        {verified !== null && <span>{verified ? `Verified by ${debugExecution ? "Debug" : "Steam"}` : "Unverified"}</span>}
      </div>
      {/**
       * オプションボタン。
       * 終了ボタンの左側に正方形で配置し、アイコン画像のみを表示する。
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
       * 終了ボタン。
       * ウィンドウ右下に正方形で配置し、アイコン画像を表示する。
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
