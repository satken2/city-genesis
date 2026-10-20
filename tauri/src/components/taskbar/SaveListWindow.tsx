/**
 * Save list (load screen), opened separately from Load in the system or top menu.
 *
 * No title bar (registry chromeless); close through the footer or Esc.
 * Each card shows the saved 3D image, name, source scenario title, city-language flag, population, and game
 * date. Place the real save timestamp separately at bottom right to distinguish it from game date.
 * Newest saves first. Mark the loaded save with a solid left block and badge.
 * Confirm loading/deletion through in-window overlays. Successful deletion simply removes the entry without a
 * success message.
 */

import { useState, useCallback, useEffect, useMemo, type CSSProperties } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { EpochDate } from "../common/EpochDate";
import { SaveVersionInfo } from "../common/SaveVersionInfo";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { isSupportedLanguage, resolveIntlLocale, resolveLocalizedText, type SupportedLanguage } from "../../utils/locale";
import { SaveEntry, prepareSaveLoad, saveDisplayName, saveError } from "../../utils/saves";
import { useApiImageUrl } from "../../hooks/useApiImageUrl";
import { SaveDialog, SaveDialogButton } from "./SaveDialog";
import { CalendarIcon, CityIcon, LoadIcon, PopulationIcon, ScenarioIcon, TrashIcon } from "./SaveIcons";
import "./SaveListWindow.css";

type SaveListWindowProps = {
  apiBaseUrl: string;
  onClose: () => void;
};

type SavesResponse = {
  success: boolean;
  saves: Record<string, SaveEntry>;
};

type SessionInfo = {
  current_save_id: string | null;
};

type Pending = { kind: "load" | "delete"; entry: SaveEntry };

/** City-language flag emoji. Country follows the Intl locale region in utils/locale.ts; use Mexico for es-419, which has no country region. */
const LANGUAGE_FLAGS: Record<SupportedLanguage, string> = {
  ja: "🇯🇵", en: "🇺🇸", "es-419": "🇲🇽", "zh-Hans": "🇨🇳", "zh-Hant": "🇹🇼", fr: "🇫🇷", ko: "🇰🇷",
};

/** Format real saved_at timestamps ("20240115_123045") in the display locale. Preserve the original string if its format differs. */
function formatSavedAt(raw: string, locale: string): string {
  const match = /^(\d{4})(\d{2})(\d{2})_(\d{2})(\d{2})(\d{2})$/.exec(raw ?? "");
  if (!match) return raw;
  const [year, month, day, hour, minute, second] = match.slice(1).map(Number);
  return new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" })
    .format(new Date(year, month - 1, day, hour, minute, second));
}

/**
 * Image captured at save time. Use the save timestamp as a revision to invalidate the two-hour HTTP cache after
 * overwrite.
 * For saves without images, skip retrieval and show a cityscape icon. Overlay the city-language flag at upper
 * left.
 */
function SaveThumbnail({ apiBaseUrl, entry }: { apiBaseUrl: string; entry: SaveEntry }) {
  const { t } = useTranslation();
  const url = entry.has_thumbnail
    ? `${buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(entry.id)}/thumbnail`)}?revision=${encodeURIComponent(entry.saved_at)}`
    : null;
  const { src, onError } = useApiImageUrl(url);
  return (
    <div className="save-card-thumb">
      {src ? <img src={src} alt="" onError={onError} draggable={false} /> : <CityIcon className="save-card-thumb-placeholder" />}
      {isSupportedLanguage(entry.language) && (
        <span className="save-card-flag" title={t(`settings.language.${entry.language}`)}>{LANGUAGE_FLAGS[entry.language]}</span>
      )}
    </div>
  );
}

export function SaveListWindow({ apiBaseUrl, onClose }: SaveListWindowProps) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);
  const [saveEntries, setSaveEntries] = useState<SaveEntry[]>([]);
  const [currentSaveId, setCurrentSaveId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [pending, setPending] = useState<Pending | null>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState("");

  /** Refetch the list/current save on every opening without HTTP caching to reflect overwrites and deletion. */
  useEffect(() => {
    if (!apiBaseUrl) return;
    let disposed = false;
    void Promise.allSettled([
      fetchApi<SavesResponse>(buildApiUrl(apiBaseUrl, "/api/v1/saves"), { cache: "reload" }),
      fetchApi<SessionInfo>(buildApiUrl(apiBaseUrl, "/api/v1/sessions"), { cache: "reload" }),
    ]).then(([saves, session]) => {
      if (disposed) return;
      if (saves.status === "fulfilled" && saves.value.success && saves.value.saves) setSaveEntries(Object.values(saves.value.saves));
      if (session.status === "fulfilled") setCurrentSaveId(session.value.current_save_id);
      setLoading(false);
    });
    return () => { disposed = true; };
  }, [apiBaseUrl]);

  const sortedEntries = useMemo(
    () => [...saveEntries].sort((a, b) => (b.saved_at ?? "").localeCompare(a.saved_at ?? "")),
    [saveEntries],
  );

  const dismiss = useCallback(() => {
    if (busy) return;
    setFailure("");
    setPending(null);
  }, [busy]);

  // Esc closes confirmation if open, otherwise the window. Do nothing while busy.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== "Escape" || busy) return;
      event.preventDefault();
      if (pending) dismiss();
      else onClose();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [busy, dismiss, onClose, pending]);

  /**
   * Delegate loading to Rust's start_load_game: launch Unity → loading screen → core load → wait → HUD.
   * Check compatibility first; cancel without action if the balance-change confirmation is declined.
   */
  const handleLoad = useCallback(async (entry: SaveEntry) => {
    setBusy(true);
    setFailure("");
    try {
      if (!await prepareSaveLoad(apiBaseUrl, entry.id, false, t)) return;
      await invoke("start_load_game", { saveId: entry.id, acceptBalanceChange: true });
      onClose();
    } catch (error) {
      setFailure(saveError(error, t) || t("saveList.loadFailed"));
    } finally {
      setBusy(false);
    }
  }, [apiBaseUrl, onClose, t]);

  const handleDelete = useCallback(async (entry: SaveEntry) => {
    setBusy(true);
    setFailure("");
    try {
      await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(entry.id)}`), { method: "DELETE" });
      setSaveEntries(previous => previous.filter(value => value.id !== entry.id));
      setPending(null);
    } catch {
      setFailure(t("saveList.deleteFailed"));
    } finally {
      setBusy(false);
    }
  }, [apiBaseUrl, t]);

  const handleOpenFolder = useCallback(async () => {
    try {
      await invoke("open_save_directory");
    } catch (error) {
      console.error("Failed to open save directory", error);
    }
  }, []);

  function renderPending(current: Pending) {
    const name = saveDisplayName(current.entry);
    const failureLine = failure && <p className="save-dialog-error" role="alert">{failure}</p>;
    const cancel = <SaveDialogButton disabled={busy} onClick={dismiss}>{t("common.cancel")}</SaveDialogButton>;
    if (current.kind === "load") {
      return (
        <SaveDialog icon={<LoadIcon />} title={name} busy={busy} onDismiss={dismiss} onSubmit={() => void handleLoad(current.entry)}
          actions={<>{cancel}<SaveDialogButton kind="primary" type="submit" busy={busy}>
            {busy ? t("saveList.loading") : t("saveList.loadButton")}
          </SaveDialogButton></>}>
          <p className="save-dialog-text">{t("saveList.confirmLoad")}</p>
          {failureLine}
        </SaveDialog>
      );
    }
    return (
      <SaveDialog icon={<TrashIcon />} title={name} tone="danger" busy={busy} onDismiss={dismiss}
        actions={<>{cancel}<SaveDialogButton kind="danger" busy={busy} onClick={() => void handleDelete(current.entry)}>
          {busy ? t("common.processing") : t("saveList.deleteButton")}
        </SaveDialogButton></>}>
        <p className="save-dialog-text">{t("saveList.confirmDelete")}</p>
        {failureLine}
      </SaveDialog>
    );
  }

  return (
    <div className="save-list">
      <header className="save-list-hero">
        <span className="save-list-overline">{t("saveLoad.load")}</span>
        <h1 className="save-list-title">{t("windowType.save_list")}</h1>
      </header>

      <div className="save-list-scroll">
        {loading ? (
          <div className="save-list-empty">{t("saveList.loading")}</div>
        ) : sortedEntries.length === 0 ? (
          <div className="save-list-empty">
            <CityIcon className="save-list-empty-icon" />
            {t("saveList.noSaves")}
          </div>
        ) : (
          <ul className="save-list-items">
            {sortedEntries.map((entry, index) => {
              const isCurrent = entry.id === currentSaveId;
              const scenarioTitle = resolveLocalizedText(entry.scenario_title, i18n.language);
              return (
                <li key={entry.id} className={`save-card${isCurrent ? " save-card--current" : ""}`}
                  style={{ "--save-card-index": index } as CSSProperties}>
                  <SaveThumbnail apiBaseUrl={apiBaseUrl} entry={entry} />
                  <div className="save-card-body">
                    <div className="save-card-heading">
                      <span className="save-card-name" title={saveDisplayName(entry)}>{saveDisplayName(entry)}</span>
                      {isCurrent && <span className="save-card-current">{t("saveList.currentSave")}</span>}
                    </div>
                    {scenarioTitle && (
                      <div className="save-card-scenario" title={t("saveList.scenario")}>
                        <ScenarioIcon className="save-card-meta-icon" />
                        <span>{scenarioTitle}</span>
                      </div>
                    )}
                    <div className="save-card-meta">
                      <span title={t("saveList.population")}>
                        <PopulationIcon className="save-card-meta-icon" />
                        {(entry.population ?? 0).toLocaleString(locale)}
                      </span>
                      <span title={t("saveList.inGameDate")}>
                        <CalendarIcon className="save-card-meta-icon" />
                        <EpochDate daysSinceEpoch={entry.current_date ?? 0} />
                      </span>
                    </div>
                    <SaveVersionInfo entry={entry} />
                  </div>
                  <div className="save-card-side">
                    <div className="save-card-actions">
                      <button className="save-card-load" type="button" disabled={busy || entry.compatibility?.loadable === false}
                        onClick={() => { setFailure(""); setPending({ kind: "load", entry }); }}>
                        {t("saveList.loadButton")}
                      </button>
                      <button className="save-card-delete" type="button" disabled={busy} title={t("saveList.deleteButton")}
                        aria-label={t("saveList.deleteButton")} onClick={() => { setFailure(""); setPending({ kind: "delete", entry }); }}>
                        <TrashIcon />
                      </button>
                    </div>
                    <span className="save-card-saved-at" title={t("saveList.savedAt")}>{formatSavedAt(entry.saved_at, locale)}</span>
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </div>

      <footer className="save-list-footer">
        <button className="save-list-folder-btn" onClick={() => void handleOpenFolder()} type="button">
          <LoadIcon className="save-list-folder-icon" />
          {t("saveList.openFolderButton")}
        </button>
        <SaveDialogButton disabled={busy} onClick={onClose}>{t("common.close")}</SaveDialogButton>
      </footer>

      {pending && renderPending(pending)}
    </div>
  );
}
