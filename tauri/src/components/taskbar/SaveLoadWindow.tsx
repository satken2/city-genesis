/**
 * セーブ・ロードとセッション切替のメニュー。
 * テンプレート保存は都市の編集モードで利用でき、保存後に紹介情報を開く。
 */

import { useState, useCallback, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { useTranslation } from "react-i18next";
import { sendSessionReset, sendSessionReload } from "../../utils/ipc";
import "./SaveLoadWindow.css";
import { fetchApi } from "../../utils/api";
import { saveError } from "../../utils/saves";
import { emit } from "@tauri-apps/api/event";

type SaveLoadWindowProps = {
  apiBaseUrl: string;
  onClose: () => void;
};

/**
 * ステータスメッセージの型
 * typeで表示色を切り替える(success=緑, error=赤, info=青)
 */
type StatusMessage = {
  text: string;
  type: "success" | "error" | "info";
};

/**
 * GET /api/v1/sessions レスポンスの型
 * current_save_id: 現在ロード中のセーブID（未ロード時はnull）
 * debug_execution: デバッグ実行フラグ。trueのときのみデバッグ用ボタンを表示する
 */
type SessionInfo = {
  current_save_id: string | null;
  debug_execution: boolean;
  template_mode: boolean;
};

export function SaveLoadWindow({ apiBaseUrl, onClose: _onClose }: SaveLoadWindowProps) {
  const { t } = useTranslation();
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<StatusMessage | null>(null);
  const [currentSaveId, setCurrentSaveId] = useState<string | null>(null);
  const [confirmAction, setConfirmAction] = useState<"newGame" | "quitGame" | "returnToTop" | null>(null);
  const [debugExecution, setDebugExecution] = useState(false);
  const [templateMode, setTemplateMode] = useState(false);

  /**
   * ウィンドウ初期化時にセッション情報を取得し、
   * 現在のセーブIDを保持する。上書きセーブ時のURL構築に使用する。
   */
  useEffect(() => {
    if (!apiBaseUrl) return;
    const base = apiBaseUrl.replace(/\/+$/, "");
    fetchApi<SessionInfo>(`${base}/api/v1/sessions`)
      .then((data: SessionInfo) => {
        setCurrentSaveId(data.current_save_id);
        setDebugExecution(data.debug_execution ?? false);
        setTemplateMode(data.template_mode);
      })
      .catch(() => {
        /* セッション取得失敗時は上書きセーブを無効化するのみ */
      });
  }, [apiBaseUrl]);

  /**
   * ニューゲームの実行
   *
   * Unity を起動し、シナリオ選択ウィンドウ（new_game）を開く。
   * ユーザーは地形またはテンプレートを選択後、NewGameWindow 内で create_session を呼び出す。
   * そのため、ここでは LoadingWindow は表示しない。
   * 確認ダイアログ経由で呼び出される。
   */
  const handleNewGame = useCallback(async () => {
    setConfirmAction(null);
    try {
      await invoke<string>("launch_graphic");
      await invoke("open_registered_window", { windowType: "new_game" });
      // SaveLoadWindow を閉じる。new_game ウィンドウで操作を続ける。
      _onClose();
    } catch (error) {
      console.error("[SaveLoadWindow] handleNewGame error:", error);
      setStatus({ text: t("saveLoad.loadFailed"), type: "error" });
    }
  }, [t, _onClose]);

  /**
   * 上書きセーブの実行
   * PUT /api/v1/saves/{save_id} で現在のセーブIDに上書き保存する。
   * セーブIDが未取得(セッション未ロード)の場合は実行しない。
   */
  const handleOverwriteSave = useCallback(async () => {
    if (!currentSaveId) return;
    setBusy(true);
    setStatus({ text: t("saveLoad.saving"), type: "info" });
    try {
      const base = apiBaseUrl.replace(/\/+$/, "");
      const data = await fetchApi<{ success?: boolean; save_id?: string; id?: string; error?: string }>(`${base}/api/v1/saves/${currentSaveId}`, {
        method: "PUT",
      });
      if (data.success) {
        setStatus({ text: t("saveLoad.saveSuccess"), type: "success" });
      } else {
        setStatus({ text: data.error ?? t("saveLoad.saveFailed"), type: "error" });
      }
    } catch {
      setStatus({ text: t("saveLoad.saveFailed"), type: "error" });
    } finally {
      setBusy(false);
    }
  }, [apiBaseUrl, currentSaveId, t]);

  /**
   * 別名セーブの実行
   * POST /api/v1/saves で新しいセーブIDを採番して新規保存する。
   * 成功時に currentSaveId を更新する。
   */
  const handleSaveAs = useCallback(async () => {
    setBusy(true);
    setStatus({ text: t("saveLoad.saving"), type: "info" });
    try {
      const base = apiBaseUrl.replace(/\/+$/, "");
      const data = await fetchApi<{ success?: boolean; save_id?: string; id?: string; error?: string }>(`${base}/api/v1/saves`, { method: "POST" });
      if (data.success) {
        setCurrentSaveId(data.save_id ?? data.id ?? null);
        setStatus({ text: t("saveLoad.saveSuccess"), type: "success" });
      } else {
        setStatus({ text: data.error ?? t("saveLoad.saveFailed"), type: "error" });
      }
    } catch {
      setStatus({ text: t("saveLoad.saveFailed"), type: "error" });
    } finally {
      setBusy(false);
    }
  }, [apiBaseUrl, t]);

  const handleSaveTemplate = useCallback(async () => {
    setBusy(true); setStatus(null);
    try {
      const data = await fetchApi<{ id: string }>(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/templates`, { method: "POST" });
      await emit("scenarios:changed");
      await invoke("open_registered_window", { windowType: "scenario_metadata", params: { extra: { scenarioId: data.id } } });
      setStatus({ text: t("saveLoad.saveSuccess"), type: "success" });
    } catch (error) { setStatus({ text: saveError(error, t), type: "error" }); }
    finally { setBusy(false); }
  }, [apiBaseUrl, t]);

  /**
   * ロードボタン押下時の処理
   * セーブデータ一覧ウィンドウ(save_list)を別ウィンドウとして開く。
   * Tauriコマンド経由でウィンドウレジストリに登録されたウィンドウを起動する。
   */
  const handleLoad = useCallback(async () => {
    try {
      await invoke("open_registered_window", { windowType: "save_list" });
    } catch (error) {
      console.error("failed to open save_list window", error);
    }
  }, []);

  /**
   * デバッグ初期化の実行
   * POST /api/v1/system/debug-init でデバッグ用の初期データを投入する。
   * デバッグ実行フラグが有効な場合のみ表示されるボタンから呼び出される。
   */
  const handleDebugInit = useCallback(async () => {
    setBusy(true);
    setStatus({ text: t("saveLoad.loading"), type: "info" });
    try {
      const base = apiBaseUrl.replace(/\/+$/, "");
      const data = await fetchApi<{ success?: boolean; save_id?: string; id?: string; error?: string }>(`${base}/api/v1/system/debug-init`, {
        method: "POST",
      });
      if (!data.error) {
        await sendSessionReload().catch(() => {});
        setStatus({ text: t("saveLoad.newGameSuccess"), type: "success" });
      } else {
        setStatus({ text: data.error ?? t("saveLoad.loadFailed"), type: "error" });
      }
    } catch {
      setStatus({ text: t("saveLoad.loadFailed"), type: "error" });
    } finally {
      setBusy(false);
    }
  }, [apiBaseUrl, t]);

  /**
   * トップメニューに戻る実行
   * DELETE /api/v1/sessions で現在のゲームセッションを終了し、
   * グラフィックプログラムの3Dオブジェクトをクリアする。
   * 成功後はSaveLoadWindowを閉じ、TopMenuのポーリングが検知してトップメニューが表示される。
   */
  const handleReturnToTop = useCallback(async () => {
    setBusy(true);
    setStatus({ text: t("saveLoad.loading"), type: "info" });
    setConfirmAction(null);
    try {
      const base = apiBaseUrl.replace(/\/+$/, "");
      const data = await fetchApi<{ success?: boolean; save_id?: string; id?: string; error?: string }>(`${base}/api/v1/sessions`, { method: "DELETE" });
      if (data.success) {
        // グラフィックプログラムの3Dオブジェクトをクリア
        await sendSessionReset().catch(() => {});
        // ウィンドウを閉じる。TopMenuのポーリングがcurrent_save_idの変化を検知しトップメニューを表示する
        _onClose();
      } else {
        setStatus({ text: data.error ?? t("saveLoad.loadFailed"), type: "error" });
        setBusy(false);
      }
    } catch {
      setStatus({ text: t("saveLoad.loadFailed"), type: "error" });
      setBusy(false);
    }
  }, [apiBaseUrl, t, _onClose]);

  /**
   * ゲーム終了の実行
   * 終了シーケンス(Unityへの通知 → バックエンドの統括シャットダウン → UI終了)は
   * Rust 側の exit_app に一元化されている。バックエンド側で緊急オートセーブと
   * 子プロセスの後始末が行われる。
   */
  const handleQuitGame = useCallback(async () => {
    setBusy(true);
    setStatus({ text: t("saveLoad.shuttingDown"), type: "info" });
    setConfirmAction(null);
    await invoke("exit_app");
  }, [t]);

  /**
   * 確認ダイアログの表示
   * ニューゲームやゲーム終了、トップメニューへの遷移などの取り返しのつかない操作前にユーザーに確認する
   */
  const requestConfirm = useCallback((action: "newGame" | "quitGame" | "returnToTop") => {
    setConfirmAction(action);
  }, []);

  const cancelConfirm = useCallback(() => {
    setConfirmAction(null);
  }, []);

  return (
    <div className="save-load-body" style={{ position: "relative" }}>
      <div className="save-load-actions">
        <button
          className="save-load-action-btn save-load-action-btn--danger"
          onClick={() => requestConfirm("newGame")}
          disabled={busy}
          type="button"
        >
          {t("saveLoad.newGame")}
        </button>
        <button
          className="save-load-action-btn"
          onClick={handleOverwriteSave}
          disabled={busy || !currentSaveId}
          type="button"
        >
          {t("saveLoad.overwriteSave")}
        </button>
        <button
          className="save-load-action-btn"
          onClick={handleSaveAs}
          disabled={busy}
          type="button"
        >
          {t("saveLoad.saveAs")}
        </button>
        <button className="save-load-action-btn" onClick={handleSaveTemplate} disabled={busy || !templateMode}
          title={!templateMode ? t("scenarios.templateModeRequired") : undefined} type="button">
          {t("scenarios.saveTemplate")}
        </button>
        <button
          className="save-load-action-btn save-load-action-btn--accent"
          onClick={handleLoad}
          disabled={busy}
          type="button"
        >
          {t("saveLoad.load")}
        </button>
        <button
          className="save-load-action-btn"
          onClick={() => requestConfirm("returnToTop")}
          disabled={busy}
          type="button"
        >
          {t("saveLoad.returnToTop")}
        </button>
        <button
          className="save-load-action-btn save-load-action-btn--danger"
          onClick={() => requestConfirm("quitGame")}
          disabled={busy}
          type="button"
        >
          {t("saveLoad.quitGame")}
        </button>
        {debugExecution && (
          <button
            className="save-load-action-btn save-load-action-btn--danger"
            onClick={handleDebugInit}
            disabled={busy}
            type="button"
          >
            {t("saveLoad.debugInit")}
          </button>
        )}
      </div>

      {status && (
        <div className={`save-load-status save-load-status--${status.type}`}>
          {status.text}
        </div>
      )}

      {confirmAction && (
        <div className="save-load-confirm-overlay" onMouseDown={cancelConfirm}>
          <div className="save-load-confirm-box" onMouseDown={(e) => e.stopPropagation()}>
            <div className="save-load-confirm-text">
              {confirmAction === "newGame" && t("saveLoad.confirmNewGame")}
              {confirmAction === "quitGame" && t("saveLoad.confirmQuitGame")}
              {confirmAction === "returnToTop" && t("saveLoad.confirmReturnToTop")}
            </div>
            <div className="save-load-confirm-actions">
              <button
                className="save-load-confirm-yes"
                onClick={() => {
                  if (confirmAction === "newGame") void handleNewGame();
                  else if (confirmAction === "quitGame") void handleQuitGame();
                  else if (confirmAction === "returnToTop") void handleReturnToTop();
                }}
                type="button"
              >
                {t("saveLoad.confirm")}
              </button>
              <button onClick={cancelConfirm} type="button">
                {t("saveLoad.cancel")}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
