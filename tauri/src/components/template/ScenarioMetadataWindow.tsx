import { useEffect, useState } from "react";
import { emit } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { SaveEntry, saveError } from "../../utils/saves";
import { SUPPORTED_LANGUAGES } from "../../utils/locale";
import { ScenarioText, ScenarioTextFields } from "../common/ScenarioTextFields";
import { ScenarioThumbnail } from "../common/ScenarioThumbnail";
import { SaveVersionInfo } from "../common/SaveVersionInfo";
import "./Scenarios.css";

type Metadata = ScenarioText & { author: string; category: string; language: string };
export function ScenarioMetadataWindow({ apiBaseUrl, scenarioId, onClose }: {
  apiBaseUrl: string; scenarioId: string; onClose: () => void;
}) {
  const { t } = useTranslation();
  const [entry, setEntry] = useState<SaveEntry | null>(null);
  const [value, setValue] = useState<Metadata>({ scenario_title: "", scenario_description: "", scenario_objective: "", author: "", category: "sandbox", language: "ja" });
  const [thumbnail, setThumbnail] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  useEffect(() => {
    const controller = new AbortController();
    fetchApi<{ save: SaveEntry }>(buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(scenarioId)}?template=true`), { signal: controller.signal, cache: "reload" })
      .then(({ save }) => { setEntry(save); setValue({
        scenario_title: save.scenario_title ?? "", scenario_description: save.scenario_description ?? "",
        scenario_objective: save.scenario_objective ?? "", author: save.author ?? "", category: save.category ?? "sandbox", language: save.language ?? "ja",
      }); }).catch(e => { if (!controller.signal.aborted) setError(saveError(e, t)); });
    return () => controller.abort();
  }, [apiBaseUrl, scenarioId, t]);

  async function save() {
    setBusy(true); setError("");
    try {
      await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/scenarios/${encodeURIComponent(scenarioId)}`), {
        method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value),
      });
      if (thumbnail) await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(scenarioId)}/thumbnail?template=true`), {
        method: "PUT", headers: { "Content-Type": "image/png" }, body: thumbnail,
      });
      await emit("scenarios:changed");
      onClose();
    } catch (e) { setError(saveError(e, t)); }
    finally { setBusy(false); }
  }

  return <form className="scenario-editor" onSubmit={e => { e.preventDefault(); void save(); }}>
    <p>{t("scenarios.metadataHint")}</p>
    {error && <p role="alert" className="scenario-error">{error}</p>}
    {entry && <>
      <SaveVersionInfo entry={entry} />
      <ScenarioTextFields value={value} disabled={busy} onChange={text => setValue({ ...value, ...text })} />
      <label className="scenario-info__field"><span>{t("scenarios.author")}</span>
        <input className="scenario-info__input" value={value.author} maxLength={120} disabled={busy} onChange={e => setValue({ ...value, author: e.target.value })} /></label>
      <label className="scenario-info__field"><span>{t("scenarios.category")}</span>
        <select value={value.category} disabled={busy} onChange={e => setValue({ ...value, category: e.target.value })}>
          {["sandbox", "challenge", "tutorial"].map(category => <option key={category} value={category}>{t(`scenarios.${category}`)}</option>)}
        </select></label>
      <label className="scenario-info__field"><span>{t("scenarios.language")}</span>
        <select value={value.language} disabled={busy} onChange={e => setValue({ ...value, language: e.target.value })}>
          {SUPPORTED_LANGUAGES.map(language => <option key={language} value={language}>{language}</option>)}
        </select></label>
      <label className="scenario-info__field"><span>{t("achievements.scenarioImageLabel")}</span>
        <ScenarioThumbnail apiBaseUrl={apiBaseUrl} templateId={scenarioId} revision={entry.content_revision} terrainMapId={entry.terrain_map_id} className="scenario-thumbnail" />
        <input type="file" accept="image/png" disabled={busy} onChange={e => setThumbnail(e.target.files?.[0] ?? null)} /></label>
      <div className="scenario-actions"><button type="submit" disabled={busy}>{t("achievements.scenarioSave")}</button>
        <button type="button" disabled={busy} onClick={onClose}>{t("scenarios.close")}</button></div>
    </>}
  </form>;
}
