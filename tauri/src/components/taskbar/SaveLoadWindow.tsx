/**
 * System menu (save/load and session switching), opened from the system tray save icon.
 *
 * No title bar (registry chromeless); close through Return to Game or Esc. Use in-window overlays for saving and
 * irreversible action confirmation; close after successful saving.
 * Save actions depend on the type of open file (core current_save_id).
 * - Save data: Overwrite, Save As, Save as Scenario.
 *   - Confirm before overwriting. Unsaved cities, such as those just started from a scenario, have no overwrite
 *     target and use Save As.
 *   - Save As creates new save data with a name.
 *   - Both capture the 3D view immediately before saving (`captureSaveThumbnail`).
 *   - Save as Scenario opens introduction metadata in creation mode; nothing is saved until confirmed.
 * - Scenario (custom scenario opened through Edit City in scenario management): Overwrite or Save as Another
 *   Scenario.
 *   - Confirm before writing to the open scenario. Do not recapture the image, which the author chose in
 *     introduction metadata.
 *   - Save as Another Scenario uses the same creation mode as Save as Scenario; future overwrites target the new
 *     scenario.
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

/** Fields used from GET /api/v1/sessions */
type SessionInfo = {
  current_save_id: string | null;
  current_save_is_scenario: boolean;
};

type ConfirmAction = "returnToTop" | "quitGame";

type Dialog =
  | { kind: "overwrite" }
  | { kind: "saveAs" }
  | { kind: "confirm"; action: ConfirmAction };

const SAVE_NAME_MAX_LENGTH = 120; // Core limit for name in POST /api/v1/saves

const CONFIRM_TEXT: Record<ConfirmAction, { label: string; message: string; busy: string }> = {
  returnToTop: { label: "saveLoad.returnToTop", message: "saveLoad.confirmReturnToTop", busy: "saveLoad.loading" },
  quitGame: { label: "saveLoad.quitGame", message: "saveLoad.confirmQuitGame", busy: "saveLoad.shuttingDown" },
};

/** Use the reason-code translation if available, otherwise the action-specific failure message. */
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

  /** Fetch the overwrite target (current saved file) and city name used as the initial Save As name. */
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
      // Unsaved city IDs return 404 and are treated as having no overwrite target.
      const saved = await fetchApi<{ save: SaveEntry | ScenarioEntry }>(
        buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(session.current_save_id)}${scenario ? "?scenario=true" : ""}`),
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

  /** Open the next window before closing the menu; closing first may let its hidden WebView be reused. */
  const openWindow = useCallback(async (windowType: string, params?: Record<string, unknown>) => {
    try {
      await invoke("open_registered_window", { windowType, params });
      onClose();
    } catch (error) {
      console.error(`failed to open ${windowType} window`, error);
    }
  }, [onClose]);

  /** Open-file heading: title for scenarios, name for save data. */
  const currentName = !currentSave ? "" : currentSave.is_scenario
    ? scenarioDisplayTitle(currentSave, i18n.language) : saveDisplayName(currentSave);

  const openSaveAs = useCallback(() => {
    setSaveName(currentName || cityName);
    openDialog({ kind: "saveAs" });
  }, [cityName, currentName, openDialog]);

  const openScenarioCreate = useCallback((titleKey: string) => {
    void openWindow("scenario_metadata", { extra: { create: true, titleKey } });
  }, [openWindow]);

  /** Without an overwrite target, use Save As for the same file type. */
  const openOverwrite = useCallback(() => {
    if (currentSave) openDialog({ kind: "overwrite" });
    else if (editingScenario) openScenarioCreate("saveLoad.saveAsNewScenario");
    else openSaveAs();
  }, [currentSave, editingScenario, openDialog, openSaveAs, openScenarioCreate]);

  /** Save and close the menu on success. */
  const save = useCallback(async (overwrite: boolean) => {
    setBusy(true);
    setFailure("");
    try {
      // Scenario images are author-selected in introduction metadata; do not recapture the 3D view on overwrite.
      if (!editingScenario) await captureSaveThumbnail();
      if (overwrite && currentSave) {
        const query = editingScenario ? "?scenario=true" : "";
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
        // Rust's exit_app owns shutdown: notify Unity → coordinated core shutdown/emergency autosave → exit UI.
        await invoke("exit_app");
        return;
      }
      await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/sessions"), { method: "DELETE" });
      // The top menu appears when session polling detects current_save_id has disappeared.
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
