/**
 * Scenario manager window, opened from the top menu without Unity.
 *
 * - Scenarios tab: one list for custom, community (Workshop), and DLC scenarios.
 *   Custom scenarios support city, terrain, and introduction editing, upload, and deletion. Actions are icon
 *   buttons with hover explanations.
 *   Deletion mainly removes unreadable scenarios (corrupt or too new), so it is available regardless of
 *   readability.
 *   Community/DLC scenarios are read-only and can only be played through New Game; show no action buttons.
 *   Encrypted custom scenarios derived from DLC cannot edit terrain: plaintext working copies would remain on
 *   disk, so the core rejects with SCENARIO_ENCRYPTED. Disable Edit Terrain and explain in a tooltip.
 * - Import Terrain tab: choose a grayscale PNG or .npy elevation file anywhere on the PC to create a custom
 *   scenario without starting a game. The game bundles no terrain, so this is the sole entry point for new
 *   terrain.
 *   Enter one title string; the core sets it in both English and the app's configured language. Register a
 *   terrain plan view as the scenario image immediately afterward; failure does not invalidate creation. Then
 *   switch to Scenarios and select the new card.
 *
 * Editing a city through create_session closes all popups, so the confirmation mentions unsaved terrain editor
 * changes.
 */
import { PLAY_MODE_SCENARIO } from "../../constants";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { prepareSaveLoad, ScenarioEntry, saveError, scenarioDisplayTitle } from "../../utils/saves";
import { requestConfirm } from "../../utils/confirmDialog";
import { loadAppSettings } from "../../utils/settings";
import { resolveLocalizedText, resolveSupportedLanguage } from "../../utils/locale";
import { formatMapSize } from "../../utils/mapSize";
import { FlatTabs } from "../common/FlatTabs";
import { ScenarioThumbnail } from "../common/ScenarioThumbnail";
import { SaveVersionInfo } from "../common/SaveVersionInfo";
import { ScenarioOriginBadges } from "../common/ScenarioSource";
import {
  HEIGHTMAP_LIMITS, heightmapFormatOfName, importHeightmap, importedBackgroundExtent, importedMapExtent, readHeightmapInfo,
  registerTerrainThumbnail, type HeightmapImportRequest, type HeightmapInfo,
} from "./heightmapImport";
import { backgroundPreviewLayer, detailPreviewLayer } from "./heightmapPreview";
import { TerrainImportPreview, useHeightmapPreview } from "./TerrainImportPreview";
import infoIcon from "../../assets/images/ui/info.png";
import terrainIcon from "../../assets/images/ui/terrain.png";
import repairIcon from "../../assets/images/ui/repair.png";
import rocketIcon from "../../assets/images/ui/rocket.png";
import trashIcon from "../../assets/images/ui/trash.png";
import approvedIcon from "../../assets/images/ui/approved.png";
import type { ScenarioCatalog } from "./ScenarioSignWindow";
import "./Scenarios.css";

type Catalog = ScenarioCatalog;
type Tab = "scenarios" | "terrains";

/** Icon-only action button. Show its explanation in title on hover, with any disabled reason on the next line. */
function ScenarioActionButton({ icon, label, reason, disabled, className, onClick }: {
  icon: string; label: string; reason?: string; disabled: boolean; className?: string; onClick: () => void;
}) {
  return <button type="button" className={`scenario-icon-button${className ? ` ${className}` : ""}`} disabled={disabled}
    aria-label={label} title={reason ? `${label}\n${reason}` : label} onClick={onClick}>
    <img src={icon} alt="" />
  </button>;
}

export function ScenarioManagerWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const [tab, setTab] = useState<Tab>("scenarios");
  const [terrainOpened, setTerrainOpened] = useState(false);
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [status, setStatus] = useState("");
  const grid = useRef<HTMLDivElement>(null);
  const reload = useCallback(async () => {
    setCatalog(await fetchApi<Catalog>(buildApiUrl(apiBaseUrl, "/api/v1/scenarios"), { cache: "reload" }));
  }, [apiBaseUrl]);
  useEffect(() => {
    void reload().catch(e => setError(saveError(e, t)));
    const listener = listen("scenarios:changed", () => { void reload().catch(e => setError(saveError(e, t))); });
    return () => { void listener.then(unlisten => unlisten()); };
  }, [reload, t]);
  // A newly created card may appear anywhere in the list; scroll the selected card into view.
  useEffect(() => {
    if (tab !== "scenarios" || !selectedId) return;
    const card = [...grid.current?.querySelectorAll<HTMLElement>("[data-scenario-id]") ?? []]
      .find(element => element.dataset.scenarioId === selectedId);
    card?.scrollIntoView?.({ block: "nearest" });
  }, [tab, selectedId, catalog]);

  /** Run one action at a time, translating failures by reason code. Return whether it succeeded. */
  async function run(operation: () => Promise<unknown>): Promise<boolean> {
    setBusy(true); setError(""); setStatus("");
    try { await operation(); return true; } catch (e) { setError(saveError(e, t)); return false; } finally { setBusy(false); }
  }
  /**
   * Open a custom scenario's city in scenario authoring mode (play_mode=PLAY_MODE_SCENARIO).
   * The core preserves IDs when opening it, so overwrite-save in the system menu writes to this scenario.
   */
  async function editCity(entry: ScenarioEntry) {
    if (!await requestConfirm({ message: t("scenarios.editConfirm") })) return;
    if (!await prepareSaveLoad(apiBaseUrl, entry.id, true, t)) return;
    await invoke("create_session", { scenarioId: entry.id, playMode: PLAY_MODE_SCENARIO, acceptBalanceChange: true });
  }
  /**
   * Export scenario terrain to a working folder, then open the terrain editor without a session.
   * 取得失敗の診断はログに残し、表示は共通の理由コードの翻訳へ任せる。
   */
  async function editTerrain(entry: ScenarioEntry) {
    const editorId = await invoke<string>("prepare_scenario_terrain_editor", { scenarioId: entry.id }).catch((e: unknown) => {
      console.error("[ScenarioManager] failed to prepare the terrain editor", e);
      throw e;
    });
    await invoke("open_registered_window", { windowType: "terrain_editor", entityId: editorId });
  }
  /** Irreversibly delete custom scenario files. Refetch even on failure because the list may be stale. */
  async function deleteScenario(entry: ScenarioEntry) {
    const confirmed = await requestConfirm({
      message: t("scenarios.deleteConfirm", { title: scenarioDisplayTitle(entry, i18n.language) }), confirmLabel: t("common.delete"), tone: "danger",
    });
    if (!confirmed) return;
    try {
      await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(entry.id)}?scenario=true`), { method: "DELETE" });
    } finally {
      await reload().catch(() => undefined);
    }
    setSelectedId(current => current === entry.id ? null : current);
    setStatus(t("scenarios.deleted"));
    void emit("scenarios:changed");
  }
  async function createFromHeightmap(request: HeightmapImportRequest, format: HeightmapInfo["format"]) {
    const settings = await loadAppSettings().catch(() => null);
    const { id } = await importHeightmap(apiBaseUrl, request, resolveSupportedLanguage(settings?.language || i18n.language), format);
    // Images can be replaced later through introduction editing; failure to generate a plan view does not invalidate scenario creation.
    await registerTerrainThumbnail(apiBaseUrl, id).catch((e: unknown) => console.error("[ScenarioManager] failed to register the terrain thumbnail", e));
    await reload();
    setSelectedId(id);
    setTab("scenarios");
    setStatus(t("scenarios.created"));
    void emit("scenarios:changed");
  }

  const entries = catalog ? Object.values(catalog.scenarios) : [];
  return <div className="scenario-manager">
    <FlatTabs<Tab>
      label={t("scenarios.manage")}
      active={tab}
      onChange={next => { setTab(next); setError(""); setStatus(""); if (next === "terrains") setTerrainOpened(true); }}
      tabs={[{ id: "scenarios", label: t("scenarios.tabScenarios") }, { id: "terrains", label: t("scenarios.tabTerrains") }]}
    />
    {tab === "scenarios" && <div className="scenario-manager-body">
      <p className="scenario-hint">{t("scenarios.createHint")}</p>
      {error && <p className="scenario-error" role="alert">{error}</p>}
      {status && <p className="scenario-status" role="status">{status}</p>}
      <div ref={grid} className="scenario-grid" aria-busy={busy || !catalog}>
        {catalog && entries.map(entry => {
          const own = entry.origin === "local" && !entry.read_only;
          const loadable = entry.compatibility.loadable;
          const description = resolveLocalizedText(entry.scenario_description, i18n.language);
          return <article key={entry.id} data-scenario-id={entry.id} aria-current={entry.id === selectedId ? "true" : undefined}
            className={`scenario-card${entry.id === selectedId ? " scenario-card--selected" : ""}`} onClick={() => setSelectedId(entry.id)}>
            <ScenarioThumbnail apiBaseUrl={apiBaseUrl} scenarioId={entry.id} revision={entry.content_revision} className="scenario-thumbnail" />
            <div className="scenario-card-content">
              <ScenarioOriginBadges entry={entry} />
              <h3>{scenarioDisplayTitle(entry, i18n.language)}</h3>
              {description && <p className="scenario-description">{description}</p>}
              {entry.author && <p>{t("scenarios.byAuthor", { author: entry.author })}</p>}
              <SaveVersionInfo entry={entry} />
              {own && <div className="scenario-actions">
                <ScenarioActionButton icon={infoIcon} label={t("scenarios.editMetadata")} disabled={busy || !loadable}
                  onClick={() => void run(() => invoke("open_registered_window", {
                    windowType: "scenario_metadata", params: { extra: { scenarioId: entry.id } },
                  }))} />
                <ScenarioActionButton icon={terrainIcon} label={t("scenarios.editTerrain")} disabled={busy || !loadable || !!entry.encrypted}
                  reason={entry.encrypted ? t("scenarios.terrainEncryptedHint") : undefined} onClick={() => void run(() => editTerrain(entry))} />
                <ScenarioActionButton icon={repairIcon} label={t("scenarios.editCity")} disabled={busy || !loadable}
                  onClick={() => void run(() => editCity(entry))} />
                <ScenarioActionButton icon={rocketIcon} label={t("scenarios.upload")} disabled={busy || !catalog.upload_available || !loadable}
                  reason={!catalog.upload_available ? t("scenarios.uploadUnconfigured") : undefined}
                  onClick={() => void run(async () => {
                    if (!await requestConfirm({ message: t("scenarios.uploadConfirm", { host: catalog.upload_host }) })) return;
                    await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/scenarios/${encodeURIComponent(entry.id)}/upload`), { method: "POST" });
                    setStatus(t("scenarios.uploaded"));
                  })} />
                {catalog.signing_available && <ScenarioActionButton icon={approvedIcon} label={t("scenarios.signing.button")} disabled={busy || !loadable}
                  onClick={() => void run(() => invoke("open_registered_window", {
                    windowType: "scenario_sign", params: { extra: { scenarioId: entry.id, openedAt: Date.now() } },
                  }))} />}
                <ScenarioActionButton icon={trashIcon} label={t("common.delete")} className="scenario-icon-button--delete" disabled={busy}
                  onClick={() => void run(() => deleteScenario(entry))} />
              </div>}
            </div>
          </article>;
        })}
        {catalog && entries.length === 0 && <p className="scenario-empty">{t("scenarios.empty")}</p>}
      </div>
    </div>}
    {terrainOpened && <TerrainImportPanel apiBaseUrl={apiBaseUrl} hidden={tab !== "terrains"} busy={busy} error={tab === "terrains" ? error : ""}
      onCreate={(request, format) => run(() => createFromHeightmap(request, format))} />}
  </div>;
}

/** Numeric input value. Treat blank input as NaN rather than zero, since Number("") returns 0. */
const numberOf = (value: string) => value.trim() === "" ? NaN : Number(value);
const withinHeightRange = (value: number) => Number.isFinite(value) && Math.abs(value) <= HEIGHTMAP_LIMITS.maxAbsHeight;

/** Information icon beside a field label. Show its explanation only in a hover tooltip (title). */
function InfoTip({ text }: { text: string }) {
  return <span className="scenario-import-info" role="img" aria-label={text} title={text}><img src={infoIcon} alt="" /></span>;
}

/** Read elevation file dimensions/format from initial bytes. Reread after file changes; null until complete. */
function useHeightmapInfo(file: File | null): HeightmapInfo | null {
  const [info, setInfo] = useState<HeightmapInfo | null>(null);
  useEffect(() => {
    setInfo(null);
    if (!file) return;
    let active = true;
    readHeightmapInfo(file).then(value => { if (active) setInfo(value); }).catch(() => { if (active) setInfo(null); });
    return () => { active = false; };
  }, [file]);
  return info;
}

/**
 * Elevation import form and right-side preview. Hide rather than unmount after first opening to retain inputs
 * across tab changes.
 * Once pixel dimensions are known, estimate map/background sizes and block creation if a side exceeds its limit;
 * the core remains authoritative.
 * Optional background terrain (.npy) has separate pixel spacing but shares sea level.
 * Render previews from elevations interpreted by the core exactly as during import; show read failures below
 * inputs.
 * After successful creation, clear only files and title, retaining conversion settings for repeated use.
 */
function TerrainImportPanel({ apiBaseUrl, hidden, busy, error, onCreate }: {
  apiBaseUrl: string; hidden: boolean; busy: boolean; error: string;
  onCreate: (request: HeightmapImportRequest, format: HeightmapInfo["format"]) => Promise<boolean>;
}) {
  const { t, i18n } = useTranslation();
  const fileInput = useRef<HTMLInputElement>(null);
  const backgroundInput = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [backgroundFile, setBackgroundFile] = useState<File | null>(null);
  const [title, setTitle] = useState("");
  const [titleEdited, setTitleEdited] = useState(false);
  const [values, setValues] = useState({ spacing: "10", minHeight: "0", maxHeight: "1000", seaLevel: "0", backgroundSpacing: "50" });
  const info = useHeightmapInfo(file);
  const backgroundInfo = useHeightmapInfo(backgroundFile);

  const format = info?.format ?? heightmapFormatOfName(file?.name);
  const usesHeightRange = format !== "npy";
  const spacing = numberOf(values.spacing);
  const minHeight = numberOf(values.minHeight);
  const maxHeight = numberOf(values.maxHeight);
  const seaLevel = numberOf(values.seaLevel);
  const backgroundSpacing = numberOf(values.backgroundSpacing);
  const spacingValid = (value: number) => Number.isFinite(value) && value > 0 && value <= HEIGHTMAP_LIMITS.maxSpacing;
  const heightRangeValid = !usesHeightRange || (withinHeightRange(minHeight) && withinHeightRange(maxHeight) && maxHeight > minHeight);
  const valid = spacingValid(spacing) && withinHeightRange(seaLevel) && heightRangeValid;
  const extent = valid && info?.columns && info.rows
    ? { width: importedMapExtent(info.columns, spacing), height: importedMapExtent(info.rows, spacing) } : null;
  const tooLarge = !!extent && Math.max(extent.width, extent.height) > HEIGHTMAP_LIMITS.maxMapSize;
  const backgroundValid = !backgroundFile || spacingValid(backgroundSpacing);
  const backgroundExtent = backgroundFile && spacingValid(backgroundSpacing) && backgroundInfo?.columns && backgroundInfo.rows
    ? { width: importedBackgroundExtent(backgroundInfo.columns, backgroundSpacing), height: importedBackgroundExtent(backgroundInfo.rows, backgroundSpacing) } : null;
  const backgroundTooLarge = !!backgroundExtent && Math.max(backgroundExtent.width, backgroundExtent.height) > HEIGHTMAP_LIMITS.maxBackgroundSize;
  const ready = !!file && valid && !tooLarge && title.trim() !== "" && backgroundValid && !backgroundTooLarge;

  // Do not refetch while values are invalid; retain the previous preview. Only PNG uses min/max, so fix both at 0 for .npy to avoid refetches.
  const seaValid = withinHeightRange(seaLevel);
  const detailPreview = useHeightmapPreview(apiBaseUrl, file,
    seaValid && heightRangeValid ? { minHeight: usesHeightRange ? minHeight : 0, maxHeight: usesHeightRange ? maxHeight : 0, seaLevel } : null,
    { format });
  const backgroundPreview = useHeightmapPreview(apiBaseUrl, backgroundFile,
    seaValid ? { minHeight: 0, maxHeight: 0, seaLevel } : null, { format: "npy", background: true });
  const detailGrid = detailPreview.grid;
  const backgroundGrid = backgroundPreview.grid;
  const detailLayer = useMemo(() => detailGrid && spacingValid(spacing) ? detailPreviewLayer(detailGrid, spacing) : null, [detailGrid, spacing]);
  const backgroundLayer = useMemo(() => backgroundGrid && spacingValid(backgroundSpacing)
    ? backgroundPreviewLayer(backgroundGrid, backgroundSpacing) : null, [backgroundGrid, backgroundSpacing]);

  // .npy stores elevations directly and does not use minimum/maximum elevation inputs; show the file's actual extrema there, to one decimal place.
  const fileHeight = (value: number | undefined) => value === undefined ? "" : String(Math.round(value * 10) / 10);
  const numberInput = (key: keyof typeof values, disabled = false, label?: string, shown?: string) =>
    <input type="number" step="any" aria-label={label} value={shown ?? values[key]} disabled={busy || disabled}
      onChange={e => setValues({ ...values, [key]: e.target.value })} />;
  const field = (key: keyof typeof values, label: string, disabled = false) => <label className="scenario-import-row">
    <span>{label}</span>
    {numberInput(key, disabled)}
  </label>;

  function chooseFile(next: File | null) {
    setFile(next);
    if (next && !titleEdited) setTitle(next.name.replace(/\.[^.]*$/, "").slice(0, HEIGHTMAP_LIMITS.titleMaxLength));
  }
  function clearBackground() {
    setBackgroundFile(null);
    if (backgroundInput.current) backgroundInput.current.value = "";
  }
  async function submit() {
    if (!file || !ready) return;
    const background = backgroundFile ? { file: backgroundFile, spacing: backgroundSpacing } : null;
    if (await onCreate({ file, spacing, minHeight, maxHeight, seaLevel, title: title.trim(), background }, format)) {
      setFile(null); setTitle(""); setTitleEdited(false);
      if (fileInput.current) fileInput.current.value = "";
      clearBackground();
    }
  }

  return <div className="scenario-manager-body" hidden={hidden}>
    {error && <p className="scenario-error" role="alert">{error}</p>}
    <div className="scenario-import-layout">
      <form className="scenario-import" onSubmit={e => { e.preventDefault(); void submit(); }}>
        <label className="scenario-import-row">
          <span>{t("scenarios.importTitle")}</span>
          <input type="text" value={title} maxLength={HEIGHTMAP_LIMITS.titleMaxLength} disabled={busy}
            onChange={e => { setTitle(e.target.value); setTitleEdited(true); }} />
        </label>
        <section className="scenario-group">
          <h3 className="scenario-group-title">{t("scenarios.importDetail")}</h3>
          <p className="scenario-hint">{t("scenarios.importDetailHint")}</p>
          <div className="scenario-import-row">
            <span>{t("scenarios.importFile")}<InfoTip text={t("scenarios.importFormats")} /></span>
            <div className="scenario-import-file">
              <button type="button" disabled={busy} onClick={() => fileInput.current?.click()}>{t("scenarios.chooseFile")}</button>
              <span className="scenario-import-file-name" title={file?.name}>{file?.name ?? t("scenarios.noFileChosen")}</span>
              <input ref={fileInput} type="file" accept=".png,.npy" hidden onChange={e => chooseFile(e.target.files?.[0] ?? null)} />
            </div>
          </div>
          {detailPreview.error && <p className="scenario-error" role="alert">{detailPreview.error}</p>}
          {field("spacing", t("scenarios.importSpacing"))}
          <div className="scenario-import-row">
            <span>{t("scenarios.importHeightRange")}</span>
            <div className="scenario-import-range">
              {numberInput("minHeight", !usesHeightRange, t("scenarios.importMinHeight"), usesHeightRange ? undefined : fileHeight(detailGrid?.minHeight))}
              <span>〜</span>
              {numberInput("maxHeight", !usesHeightRange, t("scenarios.importMaxHeight"), usesHeightRange ? undefined : fileHeight(detailGrid?.maxHeight))}
            </div>
          </div>
          {field("seaLevel", t("scenarios.importSeaLevel"))}
          {extent && <div className="scenario-import-row">
            <span>{t("scenarios.importMapSize")}</span>
            <span className="scenario-import-size">{formatMapSize(extent.width, extent.height, i18n.language)}</span>
          </div>}
          {!valid && <p className="scenario-error">{t("scenarios.importInvalid")}</p>}
          {tooLarge && <p className="scenario-error">{t("scenarios.importTooLarge", { max: HEIGHTMAP_LIMITS.maxMapSize / 1000 })}</p>}
        </section>
        <section className="scenario-group">
          <h3 className="scenario-group-title">{t("scenarios.importBackground")}<small>{t("scenarios.importOptional")}</small></h3>
          <p className="scenario-hint">{t("scenarios.importBackgroundHint")}</p>
          <div className="scenario-import-row">
            <span>{t("scenarios.importFile")}<InfoTip text={t("scenarios.importBackgroundFormats")} /></span>
            <div className="scenario-import-file">
              <button type="button" disabled={busy} onClick={() => backgroundInput.current?.click()}>{t("scenarios.chooseFile")}</button>
              <span className="scenario-import-file-name" title={backgroundFile?.name}>{backgroundFile?.name ?? t("scenarios.noFileChosen")}</span>
              {backgroundFile && <button type="button" disabled={busy} onClick={clearBackground}>{t("scenarios.clearBackground")}</button>}
              <input ref={backgroundInput} type="file" accept=".npy" hidden onChange={e => setBackgroundFile(e.target.files?.[0] ?? null)} />
            </div>
          </div>
          {backgroundPreview.error && <p className="scenario-error" role="alert">{backgroundPreview.error}</p>}
          {field("backgroundSpacing", t("scenarios.importBackgroundSpacing"), !backgroundFile)}
          {backgroundExtent && <div className="scenario-import-row">
            <span>{t("scenarios.importBackgroundSize")}</span>
            <span className="scenario-import-size">{formatMapSize(backgroundExtent.width, backgroundExtent.height, i18n.language)}</span>
          </div>}
          {!backgroundValid && <p className="scenario-error">{t("scenarios.importBackgroundInvalid")}</p>}
          {backgroundTooLarge && <p className="scenario-error">{t("scenarios.importBackgroundTooLarge", { max: HEIGHTMAP_LIMITS.maxBackgroundSize / 1000 })}</p>}
        </section>
        <div className="scenario-import-actions">
          <button type="submit" className="scenario-primary" disabled={busy || !ready}>
            {busy ? t("scenarios.creating") : t("scenarios.createFromTerrain")}
          </button>
        </div>
      </form>
      <TerrainImportPreview detail={detailLayer} background={backgroundLayer} loading={detailPreview.loading || backgroundPreview.loading} />
    </div>
  </div>;
}
