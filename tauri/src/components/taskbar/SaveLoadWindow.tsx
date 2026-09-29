/**
 * システムメニュー(セーブ・ロードとセッション切替)。システムトレイのセーブアイコンから開く。
 *
 * タイトルバーを持たないウィンドウ(レジストリの chromeless)で、閉じるのは「ゲームに戻る」と Esc。
 * 保存と取り返しのつかない操作の確認はウィンドウ内のオーバーレイで行い、保存に成功したらウィンドウを閉じる。
 * - 上書きセーブは確認してから保存する。まだ保存していない都市(シナリオから始めた直後など)は上書きする先が無いので、別名セーブになる。
 * - 別名セーブは名前を付けて新しいセーブデータにする。
 * - どちらも保存の直前に3Dビューを撮り、セーブデータの画像にする(`captureSaveThumbnail`)。
 * - シナリオとして保存は紹介情報ウィンドウを作成モードで開く。紹介情報を確定するまで何も保存しない。
 */

import { useCallback, useEffect, useState, type ReactNode } from "react";
import { invoke } from "@tauri-apps/api/core";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";
import { sendSessionReset, sendSessionReload } from "../../utils/ipc";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { captureSaveThumbnail, errorCode, saveDisplayName, type SaveEntry } from "../../utils/saves";
import { SaveDialog, SaveDialogButton } from "./SaveDialog";
import { BackIcon, DebugIcon, HomeIcon, LoadIcon, NewGameIcon, PowerIcon, SaveAsIcon, SaveIcon, ScenarioIcon } from "./SaveIcons";
import "./SaveLoadWindow.css";

type SaveLoadWindowProps = {
  apiBaseUrl: string;
  onClose: () => void;
};

/** GET /api/v1/sessions のうち使う項目。debug_execution のときだけデバッグ初期化を出す */
type SessionInfo = {
  current_save_id: string | null;
  debug_execution: boolean;
};

type ConfirmAction = "newGame" | "returnToTop" | "quitGame";

type Dialog =
  | { kind: "overwrite" }
  | { kind: "saveAs" }
  | { kind: "confirm"; action: ConfirmAction }
  | { kind: "error"; message: string };

const SAVE_NAME_MAX_LENGTH = 120; // コアの POST /api/v1/saves の name の上限

const CONFIRM_TEXT: Record<ConfirmAction, { label: string; message: string; busy: string }> = {
  newGame: { label: "saveLoad.newGame", message: "saveLoad.confirmNewGame", busy: "saveLoad.loading" },
  returnToTop: { label: "saveLoad.returnToTop", message: "saveLoad.confirmReturnToTop", busy: "saveLoad.loading" },
  quitGame: { label: "saveLoad.quitGame", message: "saveLoad.confirmQuitGame", busy: "saveLoad.shuttingDown" },
};

/** 理由コードに翻訳があればその文、無ければ操作ごとの失敗文 */
function failureText(error: unknown, t: TFunction, fallback: string): string {
  const code = errorCode(error);
  return code ? t(`saveCompatibility.${code}`, { defaultValue: fallback }) : fallback;
}

function MenuItem({ icon, label, detail, danger = false, className, disabled, onClick }: {
  icon: ReactNode; label: string; detail?: string; danger?: boolean; className?: string; disabled?: boolean; onClick: () => void;
}) {
  const classes = ["save-menu-item", danger ? "save-menu-item--danger" : "", className ?? ""].filter(Boolean).join(" ");
  return (
    <button className={classes} type="button" disabled={disabled} onClick={onClick}>
      <span className="save-menu-icon">{icon}</span>
      <span className="save-menu-text">
        <span className="save-menu-label">{label}</span>
        {detail && <span className="save-menu-detail">{detail}</span>}
      </span>
    </button>
  );
}

export function SaveLoadWindow({ apiBaseUrl, onClose }: SaveLoadWindowProps) {
  const { t } = useTranslation();
  const [currentSave, setCurrentSave] = useState<SaveEntry | null>(null);
  const [cityName, setCityName] = useState("");
  const [debugExecution, setDebugExecution] = useState(false);
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [saveName, setSaveName] = useState("");
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState("");

  /** 上書き先(保存済みの現在のセーブ)と、別名セーブの名前の初期値に使う都市名を取る */
  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    const signal = controller.signal;
    void (async () => {
      const [session, city] = await Promise.all([
        fetchApi<SessionInfo>(buildApiUrl(apiBaseUrl, "/api/v1/sessions"), { cache: "reload", signal }),
        fetchApi<{ city: { name?: string } }>(buildApiUrl(apiBaseUrl, "/api/v1/city/"), { signal }).catch(() => null),
      ]);
      setDebugExecution(session.debug_execution ?? false);
      setCityName(city?.city.name ?? "");
      if (!session.current_save_id) return;
      // まだ保存していない都市のIDは 404 になり、上書き先なしとして扱う
      const saved = await fetchApi<{ save: SaveEntry }>(
        buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(session.current_save_id)}`), { cache: "reload", signal },
      ).catch(() => null);
      setCurrentSave(saved?.save ?? null);
    })().catch(() => {});
    return () => controller.abort();
  }, [apiBaseUrl]);

  const openDialog = useCallback((next: Dialog) => {
    setFailure("");
    setDialog(next);
  }, []);

  const dismissDialog = useCallback(() => {
    if (busy) return;
    setFailure("");
    setDialog(null);
  }, [busy]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== "Escape" || busy) return;
      event.preventDefault();
      if (dialog) dismissDialog();
      else onClose();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [busy, dialog, dismissDialog, onClose]);

  const openSaveAs = useCallback(() => {
    setSaveName(currentSave ? saveDisplayName(currentSave) : cityName);
    openDialog({ kind: "saveAs" });
  }, [cityName, currentSave, openDialog]);

  const openOverwrite = useCallback(() => {
    if (currentSave) openDialog({ kind: "overwrite" });
    else openSaveAs();
  }, [currentSave, openDialog, openSaveAs]);

  /** 3Dビューを撮ってから保存し、成功したらメニューを閉じる */
  const save = useCallback(async (overwrite: boolean) => {
    setBusy(true);
    setFailure("");
    try {
      await captureSaveThumbnail();
      if (overwrite && currentSave) {
        await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(currentSave.id)}`), { method: "PUT" });
      } else {
        await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/saves"), {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ name: saveName.trim() }),
        });
      }
      setDialog(null);
      onClose();
    } catch (error) {
      setFailure(failureText(error, t, t("saveLoad.saveFailed")));
    } finally {
      setBusy(false);
    }
  }, [apiBaseUrl, currentSave, onClose, saveName, t]);

  /** 別のウィンドウを開いてからメニューを閉じる(先に閉じると、非表示になったこのWebViewが転用されうる) */
  const openWindow = useCallback(async (windowType: string, params?: Record<string, unknown>) => {
    try {
      await invoke("open_registered_window", { windowType, params });
      onClose();
    } catch (error) {
      console.error(`failed to open ${windowType} window`, error);
    }
  }, [onClose]);

  const runConfirmed = useCallback(async (action: ConfirmAction) => {
    setBusy(true);
    setFailure("");
    try {
      if (action === "quitGame") {
        // 終了シーケンス(Unityへの通知 → コアの統括シャットダウンと緊急オートセーブ → UI終了)は Rust の exit_app が持つ
        await invoke("exit_app");
        return;
      }
      if (action === "newGame") {
        // シナリオの選択と開始はニューゲームウィンドウが行う
        await invoke<string>("launch_graphic");
        await invoke("open_registered_window", { windowType: "new_game" });
      } else {
        await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/sessions"), { method: "DELETE" });
        // トップメニューはセッションのポーリングで current_save_id が消えたのを検知して出る
        await sendSessionReset().catch(() => {});
      }
      setDialog(null);
      onClose();
    } catch (error) {
      console.error(`[SaveLoadWindow] ${action} failed`, error);
      setFailure(failureText(error, t, t("saveLoad.loadFailed")));
    } finally {
      setBusy(false);
    }
  }, [apiBaseUrl, onClose, t]);

  const debugInit = useCallback(async () => {
    setBusy(true);
    try {
      await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/system/debug-init"), { method: "POST" });
      await sendSessionReload().catch(() => {});
      onClose();
    } catch (error) {
      openDialog({ kind: "error", message: failureText(error, t, t("saveLoad.loadFailed")) });
    } finally {
      setBusy(false);
    }
  }, [apiBaseUrl, onClose, openDialog, t]);

  function renderDialog(current: Dialog) {
    const failureLine = failure && <p className="save-dialog-error" role="alert">{failure}</p>;
    const cancel = <SaveDialogButton disabled={busy} onClick={dismissDialog}>{t("common.cancel")}</SaveDialogButton>;
    switch (current.kind) {
      case "overwrite":
        return (
          <SaveDialog icon={<SaveIcon />} title={t("saveLoad.overwriteSave")} busy={busy} onDismiss={dismissDialog}
            onSubmit={() => void save(true)}
            actions={<>{cancel}<SaveDialogButton kind="primary" type="submit" busy={busy}>
              {busy ? t("common.saving") : t("saveLoad.overwriteSave")}
            </SaveDialogButton></>}>
            <p className="save-dialog-text">{t("saveLoad.confirmOverwrite", { name: currentSave ? saveDisplayName(currentSave) : "" })}</p>
            {failureLine}
          </SaveDialog>
        );
      case "saveAs":
        return (
          <SaveDialog icon={<SaveAsIcon />} title={t("saveLoad.saveAs")} busy={busy} onDismiss={dismissDialog}
            onSubmit={() => void save(false)}
            actions={<>{cancel}<SaveDialogButton kind="primary" type="submit" busy={busy} disabled={!saveName.trim()}>
              {busy ? t("common.saving") : t("common.save")}
            </SaveDialogButton></>}>
            <label className="save-dialog-field">
              <span className="save-dialog-label">{t("saveLoad.saveName")}</span>
              <input className="save-dialog-input" type="text" value={saveName} maxLength={SAVE_NAME_MAX_LENGTH} disabled={busy}
                autoFocus onFocus={event => event.currentTarget.select()} onChange={event => setSaveName(event.target.value)} />
            </label>
            {failureLine}
          </SaveDialog>
        );
      case "confirm": {
        const text = CONFIRM_TEXT[current.action];
        const icon = current.action === "newGame" ? <NewGameIcon /> : current.action === "returnToTop" ? <HomeIcon /> : <PowerIcon />;
        return (
          <SaveDialog icon={icon} title={t(text.label)} tone="danger" busy={busy} onDismiss={dismissDialog}
            actions={<>
              <SaveDialogButton disabled={busy} onClick={dismissDialog}>{t("saveLoad.cancel")}</SaveDialogButton>
              <SaveDialogButton kind="danger" busy={busy} onClick={() => void runConfirmed(current.action)}>
                {busy ? t(text.busy) : t("saveLoad.confirm")}
              </SaveDialogButton>
            </>}>
            <p className="save-dialog-text">{t(text.message)}</p>
            {failureLine}
          </SaveDialog>
        );
      }
      case "error":
        return (
          <SaveDialog icon={<DebugIcon />} title={t("saveLoad.debugInit")} tone="danger" onDismiss={dismissDialog}
            actions={<SaveDialogButton kind="primary" onClick={dismissDialog}>{t("common.ok")}</SaveDialogButton>}>
            <p className="save-dialog-error" role="alert">{current.message}</p>
          </SaveDialog>
        );
    }
  }

  return (
    <div className="save-menu">
      <nav className="save-menu-list" aria-label={t("windowType.save_load")}>
        <div className="save-menu-group">
          <MenuItem icon={<SaveIcon />} label={t("saveLoad.overwriteSave")} detail={currentSave ? saveDisplayName(currentSave) : undefined}
            disabled={busy} onClick={openOverwrite} />
          <MenuItem icon={<SaveAsIcon />} label={t("saveLoad.saveAs")} disabled={busy} onClick={openSaveAs} />
          <MenuItem icon={<ScenarioIcon />} label={t("saveLoad.saveAsScenario")} disabled={busy}
            onClick={() => void openWindow("scenario_metadata", { extra: { create: true, titleKey: "saveLoad.saveAsScenario" } })} />
        </div>
        <div className="save-menu-group">
          <MenuItem icon={<LoadIcon />} label={t("saveLoad.load")} disabled={busy} onClick={() => void openWindow("save_list")} />
          <MenuItem icon={<NewGameIcon />} label={t("saveLoad.newGame")} disabled={busy}
            onClick={() => openDialog({ kind: "confirm", action: "newGame" })} />
        </div>
        <div className="save-menu-group">
          <MenuItem icon={<HomeIcon />} label={t("saveLoad.returnToTop")} disabled={busy}
            onClick={() => openDialog({ kind: "confirm", action: "returnToTop" })} />
          <MenuItem icon={<PowerIcon />} label={t("saveLoad.quitGame")} danger disabled={busy}
            onClick={() => openDialog({ kind: "confirm", action: "quitGame" })} />
          {debugExecution && (
            <MenuItem icon={<DebugIcon />} label={t("saveLoad.debugInit")} danger disabled={busy} onClick={() => void debugInit()} />
          )}
        </div>
      </nav>
      <MenuItem className="save-menu-back" icon={<BackIcon />} label={t("saveLoad.backToGame")} disabled={busy} onClick={onClose} />
      {dialog && renderDialog(dialog)}
    </div>
  );
}
