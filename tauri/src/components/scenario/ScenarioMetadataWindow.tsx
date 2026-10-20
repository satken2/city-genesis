/**
 * Scenario introduction window. Opened by Save as Scenario / Save as Another Scenario in the system menu
 * (creation mode), or Edit Introduction in scenario management.
 *
 * - Enter multilingual title, description, completion conditions, and initial city name. English is always
 *   required for the title, and required for other fields if any language has text. Fall back to English when
 *   the display language is missing.
 * - Set starting options (simulation days, climate, ground cover) and whether players may change them at game
 *   start. UI checkboxes mean Editable; invert them into fixed fields (scenario_fixed_options) for the core.
 *   Also select a default asset pack; like language, players may always change it, so it cannot be fixed.
 * - Show the city's naming language first in starting options. It cannot be edited here: changing language
 *   without renaming would break name-regeneration decisions at game start. Players may always change it, so
 *   Editable stays checked and cannot be cleared.
 * - Creation mode (create) starts from the current city's introduction/options. Save creates a new scenario
 *   without overwriting existing ones. Nothing is written before saving, so cancellation leaves nothing behind.
 *   Before creation, stage the chosen image with the core for the next save; if none was chosen, capture the 3D
 *   view immediately before saving.
 */
import { useEffect, useState } from "react";
import { emit } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { AssetPackEntry, ScenarioEntry, ScenarioStartOption, captureSaveThumbnail, fetchAssetPacks, saveError, uploadScenarioThumbnail } from "../../utils/saves";
import { compactLocalizedText, missingEnglish, resolveLocalizedText, type LocalizedText } from "../../utils/locale";
import { ASSET_PACK_STANDARD, GROUND_COVER_TYPES, SIMULATION_DAYS_CHOICES } from "../../constants";
import { normalizeGroundCover } from "../terrain/terrainData";
import type { ClimateSettings } from "../../utils/climate";
import { LocalizedTextFields } from "../common/LocalizedTextFields";
import { ScenarioThumbnail } from "../common/ScenarioThumbnail";
import { SaveVersionInfo } from "../common/SaveVersionInfo";
import "./Scenarios.css";

type Texts = Record<string, LocalizedText | undefined>;
type Options = Record<Exclude<ScenarioStartOption, "climate">, number> & { climate: ClimateSettings };

const TEXT_KEYS = ["scenario_title", "scenario_description", "scenario_objective", "scenario_city_name"] as const;
const START_OPTIONS: ScenarioStartOption[] = ["simulation_days", "climate", "ground_cover"];

/** Initial form data: saved scenario when editing; current city (GET /api/v1/city/) and city language when creating. */
type ScenarioSource = Pick<ScenarioEntry, (typeof TEXT_KEYS)[number] | "scenario_fixed_options" | "author" | "language"
  | "simulation_days" | "climate" | "ground_cover" | "asset_pack">;

function startOptionsOf(source: ScenarioSource): Options {
  return {
    simulation_days: source.simulation_days || 30,
    climate: source.climate!,
    ground_cover: normalizeGroundCover(source.ground_cover),
  };
}

export function ScenarioMetadataWindow({ apiBaseUrl, scenarioId, create = false, onClose }: {
  apiBaseUrl: string; scenarioId: string; create?: boolean; onClose: () => void;
}) {
  const { t, i18n } = useTranslation();
  const [entry, setEntry] = useState<ScenarioEntry | null>(null);
  const [language, setLanguage] = useState<string | null>(null);
  const [texts, setTexts] = useState<Texts>({});
  const [author, setAuthor] = useState("");
  const [options, setOptions] = useState<Options | null>(null);
  const [fixed, setFixed] = useState<ScenarioStartOption[]>([]);
  const [assetPack, setAssetPack] = useState(ASSET_PACK_STANDARD);
  const [assetPacks, setAssetPacks] = useState<AssetPackEntry[]>([]);
  const [thumbnail, setThumbnail] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  useEffect(() => {
    const controller = new AbortController();
    const signal = controller.signal;
    const apply = (source: ScenarioSource) => {
      setTexts(Object.fromEntries(TEXT_KEYS.map(key => [key, source[key] ?? {}])));
      setAuthor(source.author ?? "");
      setOptions(startOptionsOf(source));
      setFixed(source.scenario_fixed_options ?? []);
      setAssetPack(source.asset_pack ?? ASSET_PACK_STANDARD);
      setLanguage(source.language);
    };
    fetchAssetPacks(apiBaseUrl).then(packs => { if (!signal.aborted) setAssetPacks(packs); }).catch(() => {});
    const load = create
      ? Promise.all([
        fetchApi<{ city: Omit<ScenarioSource, "author" | "language"> }>(buildApiUrl(apiBaseUrl, "/api/v1/city/"), { signal }),
        fetchApi<{ language: string }>(buildApiUrl(apiBaseUrl, "/api/v1/sessions"), { signal, cache: "reload" }),
      ]).then(([{ city }, session]) => apply({ ...city, language: session.language }))
      : fetchApi<{ save: ScenarioEntry }>(buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(scenarioId)}?scenario=true`), { signal, cache: "reload" })
        .then(({ save }) => { setEntry(save); apply(save); });
    load.catch(e => { if (!signal.aborted) setError(saveError(e, t)); });
    return () => controller.abort();
  }, [apiBaseUrl, create, scenarioId, t]);

  const choices: Record<Exclude<ScenarioStartOption, "climate">, { values: number[]; label: (value: number) => string }> = {
    simulation_days: { values: SIMULATION_DAYS_CHOICES, label: days => t("newGame.daysOption", { days }) },
    ground_cover: { values: GROUND_COVER_TYPES, label: value => t(`groundCover.${value}`) },
  };
  const optionLabels: Record<ScenarioStartOption, string> = {
    simulation_days: t("newGame.simulationDaysLabel"), climate: t("climate.label"),
    ground_cover: t("groundCover.label"),
  };
  // If the default pack is not owned by the author, retain its ID as an option so resaving does not change the default.
  const packChoices = assetPacks.some(pack => pack.pack_id === assetPack) || assetPack === ASSET_PACK_STANDARD
    ? assetPacks : [...assetPacks, { pack_id: assetPack, display_name: { en: assetPack } }];

  async function save() {
    if (!options) return;
    if (TEXT_KEYS.some(key => missingEnglish(texts[key], key === "scenario_title"))) {
      setError(t("scenarios.englishRequired"));
      return;
    }
    setBusy(true); setError("");
    const body = JSON.stringify({
      ...Object.fromEntries(TEXT_KEYS.map(key => [key, compactLocalizedText(texts[key])])),
      author, ...options, asset_pack: assetPack,
      scenario_fixed_options: START_OPTIONS.filter(option => fixed.includes(option)),
    });
    try {
      if (create) {
        if (thumbnail) {
          await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/sessions/thumbnail"), {
            method: "PUT", headers: { "Content-Type": "image/png" }, body: thumbnail,
          });
        } else {
          await captureSaveThumbnail();
        }
        await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/scenario-tools"), { method: "POST", headers: { "Content-Type": "application/json" }, body });
      } else {
        await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/scenarios/${encodeURIComponent(scenarioId)}`), {
          method: "PATCH", headers: { "Content-Type": "application/json" }, body,
        });
        if (thumbnail) await uploadScenarioThumbnail(apiBaseUrl, scenarioId, thumbnail);
      }
      await emit("scenarios:changed");
      onClose();
    } catch (e) { setError(saveError(e, t)); }
    finally { setBusy(false); }
  }

  return <form className="scenario-editor" onSubmit={e => { e.preventDefault(); void save(); }}>
    {error && <p role="alert" className="scenario-error">{error}</p>}
    {language !== null && options && <>
      {entry && <SaveVersionInfo entry={entry} />}
      <LocalizedTextFields disabled={busy} value={texts} onChange={setTexts} fields={[
        { key: "scenario_title", label: t("achievements.scenarioTitleLabel"), maxLength: 160 },
        { key: "scenario_description", label: t("achievements.scenarioDescriptionLabel"), maxLength: 8000, multiline: true },
        { key: "scenario_objective", label: t("achievements.scenarioObjectiveLabel"), maxLength: 4000, multiline: true },
        { key: "scenario_city_name", label: t("scenarios.initialCityName"), maxLength: 120 },
      ]} />
      <label className="scenario-info__field"><span>{t("scenarios.author")}</span>
        <input type="text" className="scenario-info__input" value={author} maxLength={120} disabled={busy} onChange={e => setAuthor(e.target.value)} /></label>
      <section className="scenario-group">
        <h3 className="scenario-group-title">{t("scenarios.startSettings")}</h3>
        <p className="scenario-hint">{t("scenarios.startSettingsHint")}</p>
        <div className="scenario-start-row" data-option="language">
          <span>{t("scenarios.cityLanguage")}</span>
          <span className="scenario-start-value">{t(`settings.language.${language}`, { defaultValue: language })}</span>
          <label className="scenario-start-changeable">
            <input type="checkbox" checked disabled readOnly />
            {t("scenarios.changeableOption")}
          </label>
        </div>
        {START_OPTIONS.map(option => <div className="scenario-start-row" key={option} data-option={option}>
          <span>{optionLabels[option]}</span>
          {option === "climate" ? <span className="scenario-start-value">{options.climate.name}</span> : <select value={options[option]} disabled={busy} aria-label={optionLabels[option]}
            onChange={e => setOptions({ ...options, [option]: Number(e.target.value) })}>
            {choices[option].values.map(value => <option key={value} value={value}>{choices[option].label(value)}</option>)}
          </select>}
          <label className="scenario-start-changeable">
            <input type="checkbox" checked={!fixed.includes(option)} disabled={busy}
              onChange={e => setFixed(e.target.checked ? fixed.filter(value => value !== option) : [...fixed, option])} />
            {t("scenarios.changeableOption")}
          </label>
        </div>)}
        <div className="scenario-start-row" data-option="asset_pack">
          <span>{t("assetPack.label")}</span>
          <select value={assetPack} disabled={busy} aria-label={t("assetPack.label")} onChange={e => setAssetPack(e.target.value)}>
            <option value={ASSET_PACK_STANDARD}>{t("assetPack.standard")}</option>
            {packChoices.map(pack => <option key={pack.pack_id} value={pack.pack_id}>{resolveLocalizedText(pack.display_name, i18n.language)}</option>)}
          </select>
          <label className="scenario-start-changeable">
            <input type="checkbox" checked disabled readOnly />
            {t("scenarios.changeableOption")}
          </label>
        </div>
      </section>
      <label className="scenario-info__field"><span>{t("achievements.scenarioImageLabel")}</span>
        {entry
          ? <ScenarioThumbnail apiBaseUrl={apiBaseUrl} scenarioId={scenarioId} revision={entry.content_revision} className="scenario-thumbnail" />
          : <div className="scenario-thumbnail" />}
        <input type="file" accept="image/png" disabled={busy} onChange={e => setThumbnail(e.target.files?.[0] ?? null)} /></label>
      <div className="scenario-actions"><button type="submit" disabled={busy}>{t("achievements.scenarioSave")}</button>
        <button type="button" disabled={busy} onClick={onClose}>{create ? t("common.cancel") : t("scenarios.close")}</button></div>
    </>}
  </form>;
}
