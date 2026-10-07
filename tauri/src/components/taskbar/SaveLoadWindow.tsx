/**
 * システムメニュー(セーブ・ロードとセッション切替)。システムトレイのセーブアイコンから開く。
 *
 * タイトルバーを持たないウィンドウ(レジストリの chromeless)で、閉じるのは「ゲームに戻る」と Esc。
 * 保存と取り返しのつかない操作の確認はウィンドウ内のオーバーレイで行い、保存に成功したらウィンドウを閉じる。
 * 保存の項目は、開いているファイル(コアの current_save_id)の種類で決まる。
 * - セーブデータ: 上書きセーブ・別名セーブ・シナリオとして保存。
 *   - 上書きセーブは確認してから保存する。まだ保存していない都市(シナリオから始めた直後など)は上書きする先が無いので、別名セーブになる。
 *   - 別名セーブは名前を付けて新しいセーブデータにする。
 *   - どちらも保存の直前に3Dビューを撮り、セーブデータの画像にする(`captureSaveThumbnail`)。
 *   - シナリオとして保存は紹介情報ウィンドウを作成モードで開く。紹介情報を確定するまで何も保存しない。
 * - シナリオ(シナリオ管理の「都市を編集」で開いた自作シナリオ): 上書き保存・別のシナリオとして保存。
 *   - 上書き保存は確認してから開いているシナリオへ書く。シナリオの画像は作者が紹介情報で選んだものなので撮り直さない。
 *   - 別のシナリオとして保存は「シナリオとして保存」と同じ作成モードで、以後の上書き保存の先が新しいシナリオになる。
 */

import { useCallback, useEffect, useState, type ReactNode } from "react";
import { invoke } from "@tauri-apps/api/core";
import { emit } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";
import { sendSessionReset } from "../../utils/ipc";
import { buildApiUrl, fetchApi } from "../../utils/api";
import {
  captureSaveThumbnail, errorCode, saveDisplayName, scenarioDisplayTitle, type SaveEntry, type ScenarioEntry,
} from "../../utils/saves";
import { SaveDialog, SaveDialogButton } from "./SaveDialog";
import { BackIcon, HomeIcon, LoadIcon, NewGameIcon, PowerIcon, SaveAsIcon, SaveIcon, ScenarioIcon } from "./SaveIcons";
import "./SaveLoadWindow.css";

type SaveLoadWindowProps = {
  apiBaseUrl: string;
  onClose: () => void;
};

/** GET /api/v1/sessions のうち使う項目 */
type SessionInfo = {
  current_save_id: string | null;
  current_save_is_scenario: boolean;
};

type ConfirmAction = "returnToTop" | "quitGame";

type Dialog =
  | { kind: "overwrite" }
  | { kind: "saveAs" }
  | { kind: "confirm"; action: ConfirmAction };

const SAVE_NAME_MAX_LENGTH = 120; // コアの POST /api/v1/saves の name の上限

const CONFIRM_TEXT: Record<ConfirmAction, { label: string; message: string; busy: string }> = {
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
  const { t, i18n } = useTranslation();
  const [editingScenario, setEditingScenario] = useState(false);
  const [currentSave, setCurrentSave] = useState<SaveEntry | ScenarioEntry | null>(null);
  const [cityName, setCityName] = useState("");
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
      const scenario = session.current_save_is_scenario ?? false;
      setEditingScenario(scenario);
      setCityName(city?.city.name ?? "");
      if (!session.current_save_id) return;
      // まだ保存していない都市のIDは 404 になり、上書き先なしとして扱う
      const saved = await fetchApi<{ save: SaveEntry | ScenarioEntry }>(
        buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(session.current_save_id)}${scenario ? "?template=true" : ""}`),
        { cache: "reload", signal },
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

  /** 別のウィンドウを開いてからメニューを閉じる(先に閉じると、非表示になったこのWebViewが転用されうる) */
  const openWindow = useCallback(async (windowType: string, params?: Record<string, unknown>) => {
    try {
      await invoke("open_registered_window", { windowType, params });
      onClose();
    } catch (error) {
      console.error(`failed to open ${windowType} window`, error);
    }
  }, [onClose]);

  /** 開いているファイルの見出し。シナリオはタイトル、セーブデータは名前 */
  const currentName = !currentSave ? "" : currentSave.is_scenario
    ? scenarioDisplayTitle(currentSave, i18n.language) : saveDisplayName(currentSave);

  const openSaveAs = useCallback(() => {
    setSaveName(currentName || cityName);
    openDialog({ kind: "saveAs" });
  }, [cityName, currentName, openDialog]);

  const openScenarioCreate = useCallback((titleKey: string) => {
    void openWindow("scenario_metadata", { extra: { create: true, titleKey } });
  }, [openWindow]);

  /** 上書きする先が無ければ、開いているファイルと同じ種類の別名保存にする */
  const openOverwrite = useCallback(() => {
    if (currentSave) openDialog({ kind: "overwrite" });
    else if (editingScenario) openScenarioCreate("saveLoad.saveAsNewScenario");
    else openSaveAs();
  }, [currentSave, editingScenario, openDialog, openSaveAs, openScenarioCreate]);

  /** 保存し、成功したらメニューを閉じる */
  const save = useCallback(async (overwrite: boolean) => {
    setBusy(true);
    setFailure("");
    try {
      // シナリオの画像は作者が紹介情報で選んだものなので、上書きでは3Dビューを撮り直さない
      if (!editingScenario) await captureSaveThumbnail();
      if (overwrite && currentSave) {
        const query = editingScenario ? "?template=true" : "";
        await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(currentSave.id)}${query}`), { method: "PUT" });
      } else {
        await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/saves"), {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ name: saveName.trim() }),
        });
      }
      if (editingScenario) void emit("scenarios:changed");
      setDialog(null);
      onClose();
    } catch (error) {
      setFailure(failureText(error, t, t("saveLoad.saveFailed")));
    } finally {
      setBusy(false);
    }
  }, [apiBaseUrl, currentSave, editingScenario, onClose, saveName, t]);

  const runConfirmed = useCallback(async (action: ConfirmAction) => {
    setBusy(true);
    setFailure("");
    try {
      if (action === "quitGame") {
        // 終了シーケンス(Unityへの通知 → コアの統括シャットダウンと緊急オートセーブ → UI終了)は Rust の exit_app が持つ
        await invoke("exit_app");
        return;
      }
      await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/sessions"), { method: "DELETE" });
      // トップメニューはセッションのポーリングで current_save_id が消えたのを検知して出る
      await sendSessionReset().catch(() => {});
      setDialog(null);
      onClose();
    } catch (error) {
      console.error(`[SaveLoadWindow] ${action} failed`, error);
      setFailure(failureText(error, t, t("saveLoad.loadFailed")));
    } finally {
      setBusy(false);
    }
  }, [apiBaseUrl, onClose, t]);

  const overwriteLabel = t(editingScenario ? "saveLoad.overwriteScenario" : "saveLoad.overwriteSave");

  function renderDialog(current: Dialog) {
    const failureLine = failure && <p className="save-dialog-error" role="alert">{failure}</p>;
    const cancel = <SaveDialogButton disabled={busy} onClick={dismissDialog}>{t("common.cancel")}</SaveDialogButton>;
    switch (current.kind) {
      case "overwrite":
        return (
          <SaveDialog icon={<SaveIcon />} title={overwriteLabel} busy={busy} onDismiss={dismissDialog}
            onSubmit={() => void save(true)}
            actions={<>{cancel}<SaveDialogButton kind="primary" type="submit" busy={busy}>
              {busy ? t("common.saving") : overwriteLabel}
            </SaveDialogButton></>}>
            <p className="save-dialog-text">
              {t(editingScenario ? "saveLoad.confirmOverwriteScenario" : "saveLoad.confirmOverwrite", { name: currentName })}
            </p>
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
        const icon = current.action === "returnToTop" ? <HomeIcon /> : <PowerIcon />;
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
    }
  }

  return (
    <div className="save-menu">
      <nav className="save-menu-list" aria-label={t("windowType.save_load")}>
        <div className="save-menu-group">
          <MenuItem icon={<SaveIcon />} label={overwriteLabel} detail={currentName || undefined} disabled={busy} onClick={openOverwrite} />
          {editingScenario ? (
            <MenuItem icon={<ScenarioIcon />} label={t("saveLoad.saveAsNewScenario")} disabled={busy}
              onClick={() => openScenarioCreate("saveLoad.saveAsNewScenario")} />
          ) : <>
            <MenuItem icon={<SaveAsIcon />} label={t("saveLoad.saveAs")} disabled={busy} onClick={openSaveAs} />
            <MenuItem icon={<ScenarioIcon />} label={t("saveLoad.saveAsScenario")} disabled={busy}
              onClick={() => openScenarioCreate("saveLoad.saveAsScenario")} />
          </>}
        </div>
        <div className="save-menu-group">
          <MenuItem icon={<LoadIcon />} label={t("saveLoad.load")} disabled={busy} onClick={() => void openWindow("save_list")} />
          <MenuItem icon={<NewGameIcon />} label={t("saveLoad.newGame")} disabled={busy}
            onClick={() => void openWindow("new_game")} />
        </div>
        <div className="save-menu-group">
          <MenuItem icon={<HomeIcon />} label={t("saveLoad.returnToTop")} disabled={busy}
            onClick={() => openDialog({ kind: "confirm", action: "returnToTop" })} />
          <MenuItem icon={<PowerIcon />} label={t("saveLoad.quitGame")} danger disabled={busy}
            onClick={() => openDialog({ kind: "confirm", action: "quitGame" })} />
        </div>
      </nav>
      <MenuItem className="save-menu-back" icon={<BackIcon />} label={t("saveLoad.backToGame")} disabled={busy} onClick={onClose} />
      {dialog && renderDialog(dialog)}
    </div>
  );
}
