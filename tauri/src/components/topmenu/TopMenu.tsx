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
import { fetchApi, buildApiUrl } from "../../utils/api";
import { CoreVersion, frontendVersion } from "../../utils/version";

type SessionStatePayload = {
  current_save_id: string | null;
  session?: Record<string, unknown>;
  city?: Record<string, unknown>;
  isLoading: boolean;
};

export function TopMenu({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const appWindow = useMemo(() => getCurrentWindow(), []);
  const [core, setCore] = useState<CoreVersion | null>(null);
  const [versionError, setVersionError] = useState("");
  useEffect(() => {
    if (!apiBaseUrl) return;
    let active = true;
    const refresh = () => {
      void Promise.all([
        fetchApi<CoreVersion>(buildApiUrl(apiBaseUrl, "/api/v1/system/version"), { cache: "no-store" }),
        invoke<{ error: string | null }>("get_frontend_status"),
      ]).then(([value, status]) => {
        if (!active) return;
        setCore(value);
        const error = value.api_contract_version !== frontendVersion.api_contract_version ? "API_CONTRACT_MISMATCH" : status.error;
        setVersionError(error ? t(`saveCompatibility.${error}`) : "");
      }).catch(() => { if (active) { setCore(null); setVersionError(t("saveCompatibility.versionUnavailable")); } });
    };
    refresh();
    const timer = setInterval(refresh, 5000);
    return () => { active = false; clearInterval(timer); };
  }, [apiBaseUrl, t]);
  const [quitting, setQuitting] = useState(false);
  const [debugExecution, setDebugExecution] = useState(false);

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
    appWindow
      .listen<SessionStatePayload>("session-state", (event) => {
        const payload = event.payload;
        setDebugExecution((payload.session?.debug_execution as boolean) ?? false);

        if (versionError) { void appWindow.show(); return; }

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
    };
  }, [appWindow, versionError]);

  /**
   * 新規ゲームボタン押下時の処理。
   *
   * Unity を起動し、シナリオ選択ウィンドウ（new_game）を開く。
   * ユーザーは地形またはテンプレートを選択後、NewGameWindow 内で create_session を呼び出す。
   * そのため、ここでは Tips は表示しない。
   */
  async function handleNewGame() {
    try {
      await invoke<string>("launch_graphic");
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

  async function handleEditor() {
    try {
      await invoke<string>("launch_graphic");
      await invoke("open_registered_window", { windowType: "template_editor" });
    } catch (error) {
      console.error("[TopMenu] handleEditor error:", error);
    }
  }

  async function handleOptions() {
    try {
      await invoke("open_registered_window", { windowType: "settings" });
    } catch (error) {
      console.error("[TopMenu] failed to open settings", error);
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
      {/**
       * メインボタン群。
       * スプラッシュ画面上部から 45% の位置に、左右中央揃え、幅は画面全体の 35% で配置する。
       * デバッグ表示の有無やメッセージの出現に関係なく位置がズレないよう、絶対配置を用いる。
       */}
      <div className="topmenu-main-buttons">
        <button
          className="topmenu-btn topmenu-btn-primary"
          onClick={handleNewGame}
          disabled={!core || !!versionError}
          type="button"
        >
          {t("topmenu.newGame")}
        </button>
        <button className="topmenu-btn" onClick={handleLoad} disabled={!core || !!versionError} type="button">
          {t("topmenu.load")}
        </button>
        <button className="topmenu-btn" onClick={handleEditor} disabled={!core || !!versionError} type="button">
          {t("topmenu.editor")}
        </button>
      </div>

      {/**
       * デバッグ初期化ボタン。
       * デバッグ実行時のみ設定・シナリオ管理ボタンの左に表示する。
       */}
      {debugExecution && (
        <button
          className="topmenu-btn topmenu-btn-debug"
          onClick={handleDebugInit}
          disabled={!core || !!versionError}
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

      <div className="topmenu-versions"><span>core {core ? `v${core.core_version}` : t("saveCompatibility.unknown")}</span><span>ui v{frontendVersion.frontend_version}</span>
        {versionError && <span role="alert">{versionError}</span>}</div>
      <button className="topmenu-btn topmenu-btn-scenarios" title={t("scenarios.manage")} aria-label={t("scenarios.manage")}
        onClick={() => void invoke("open_registered_window", { windowType: "scenario_manager" })}>
        <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" aria-hidden="true">
          <rect x="3" y="3" width="7" height="7" /><rect x="14" y="3" width="7" height="7" /><rect x="3" y="14" width="7" height="7" /><rect x="14" y="14" width="7" height="7" />
        </svg>
      </button>
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
