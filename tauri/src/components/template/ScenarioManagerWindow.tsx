import { save as chooseSavePath } from "@tauri-apps/plugin-dialog";
import { useCallback, useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { prepareSaveLoad, SaveEntry, saveError } from "../../utils/saves";
import { requestConfirm } from "../../utils/confirmDialog";
import { ScenarioThumbnail } from "../common/ScenarioThumbnail";
import { SaveVersionInfo } from "../common/SaveVersionInfo";
import "./Scenarios.css";

type Catalog = { scenarios: Record<string, SaveEntry>; upload_available: boolean; upload_host: string | null };
export function ScenarioManagerWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [filter, setFilter] = useState("all");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [status, setStatus] = useState("");
  const input = useRef<HTMLInputElement>(null);
  const reload = useCallback(async () => {
    setCatalog(await fetchApi<Catalog>(buildApiUrl(apiBaseUrl, "/api/v1/scenarios"), { cache: "reload" }));
  }, [apiBaseUrl]);
  useEffect(() => {
    void reload().catch(e => setError(saveError(e, t)));
    const listener = listen("scenarios:changed", () => { void reload().catch(e => setError(saveError(e, t))); });
    return () => { void listener.then(unlisten => unlisten()); };
  }, [reload, t]);
  async function run(operation: () => Promise<unknown>) {
    setBusy(true); setError(""); setStatus("");
    try { await operation(); } catch (e) { setError(saveError(e, t)); } finally { setBusy(false); }
  }
  async function start(entry: SaveEntry, editing: boolean) {
    if (!await requestConfirm({ message: t("scenarios.startConfirm") })) return;
    if (!await prepareSaveLoad(apiBaseUrl, entry.id, true, t)) return;
    await invoke("create_session", { templateId: entry.id, templateMode: editing, acceptBalanceChange: true });
  }
  async function exportScenario(entry: SaveEntry) {
    // OSの保存ダイアログと書き込みはRustに集約し、ブラウザのダウンロード設定に依存しない。
    const outputPath = await chooseSavePath({ defaultPath: "scenario.cgscenario", filters: [{ name: t("scenarios.manage"), extensions: ["cgscenario"] }] });
    if (outputPath) await invoke("export_scenario", { scenarioId: entry.id, outputPath });
  }
  return <div className="scenario-manager">
    <div className="scenario-toolbar">
      <div className="scenario-filters">{["all", "builtin", "downloaded", "local"].map(origin =>
        <button key={origin} type="button" aria-pressed={filter === origin} onClick={() => setFilter(origin)}>{t(`scenarios.${origin}`)}</button>)}</div>
      <button disabled={busy} onClick={() => input.current?.click()}>{t("scenarios.import")}</button>
      <button disabled={busy} onClick={() => void run(reload)}>{t("scenarios.refresh")}</button>
      <input ref={input} type="file" accept=".cg,.cgscenario" hidden onChange={e => {
        const file = e.target.files?.[0]; e.target.value = "";
        if (file) void run(async () => {
          await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/scenarios/import"), { method: "POST", body: file });
          await reload(); setStatus(t("scenarios.imported"));
        });
      }} />
    </div>
    {error && <p className="scenario-error" role="alert">{error}</p>}
    {status && <p role="status">{status}</p>}
    {catalog && !catalog.upload_available && <p className="scenario-hint">{t("scenarios.uploadUnconfigured")}</p>}
    <div className="scenario-grid" aria-busy={busy || !catalog}>
      {catalog && Object.values(catalog.scenarios).filter(entry => filter === "all" || entry.origin === filter).map(entry =>
        <article key={entry.id} className="scenario-card">
          <ScenarioThumbnail apiBaseUrl={apiBaseUrl} templateId={entry.id} revision={entry.content_revision} terrainMapId={entry.terrain_map_id} className="scenario-thumbnail" />
          <div className="scenario-card-content">
            <span className="scenario-origin">{t(`scenarios.${entry.origin}`)}</span>
            <h3>{entry.scenario_title || entry.city_name}</h3>
            <p className="scenario-description">{entry.scenario_description}</p>
            {entry.author && <p>{t("scenarios.byAuthor", { author: entry.author })}</p>}
            <SaveVersionInfo entry={entry} />
            <div className="scenario-actions">
              <button disabled={busy || !entry.compatibility.loadable} onClick={() => void run(() => start(entry, false))}>{t("scenarios.play")}</button>
              <button disabled={busy || !entry.compatibility.loadable} onClick={() => void run(() => start(entry, true))}>{t(entry.origin === "local" ? "scenarios.editCity" : "scenarios.editCopy")}</button>
              <button disabled={busy || !entry.compatibility.loadable} onClick={() => void run(() => exportScenario(entry))}>{t("scenarios.export")}</button>
              {entry.origin === "local" && <>
                <button disabled={busy || !entry.compatibility.loadable} onClick={() => void run(() => invoke("open_registered_window", {
                  windowType: "scenario_metadata", params: { extra: { scenarioId: entry.id } },
                }))}>{t("scenarios.metadata")}</button>
                <button disabled={busy || !catalog.upload_available || !entry.compatibility.loadable} title={!catalog.upload_available ? t("scenarios.uploadUnconfigured") : undefined}
                  onClick={() => void run(async () => {
                    if (!await requestConfirm({ message: t("scenarios.uploadConfirm", { host: catalog.upload_host }) })) return;
                    await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/scenarios/${entry.id}/upload`), { method: "POST" });
                    setStatus(t("scenarios.uploaded"));
                  })}>{t("scenarios.upload")}</button>
              </>}
            </div>
          </div>
        </article>)}
      {catalog && !Object.values(catalog.scenarios).some(entry => filter === "all" || entry.origin === filter) && <p>{t("scenarios.empty")}</p>}
    </div>
  </div>;
}
