/**
 * シナリオの紹介情報ウィンドウ(テンプレートとして保存した直後と、シナリオ管理の「紹介情報を編集」から開く)。
 *
 * - 紹介文(タイトル・説明・達成条件)と初期の都市名を多言語で書く。英語は必須(タイトルは常に、
 *   ほかの項目は1言語でも書けば)で、表示する言語の文が無いときは英語が出る。
 * - 開始設定(シミュレーション日数・気候・地表・アセットの地域)の値と、それを固定するかを決める。
 *   固定しない項目はゲーム開始時にプレイヤーが選び直せる。言語は常にプレイヤーが選べる。
 * - 都市内の名前の言語は変えられない(名前を付け直さずに言語だけを変えると、ゲーム開始時の付け直しの判定が狂う)。
 */
import { useEffect, useState } from "react";
import { emit } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { SaveEntry, ScenarioStartOption, saveError, uploadScenarioThumbnail } from "../../utils/saves";
import { compactLocalizedText, missingEnglish, type LocalizedText } from "../../utils/locale";
import { ASSET_REGION_STANDARD, ASSET_REGION_TYPES, CLIMATE_TYPES, GROUND_COVER_TYPES, SIMULATION_DAYS_CHOICES } from "../../constants";
import { normalizeClimate, normalizeGroundCover } from "../terrain/terrainData";
import { LocalizedTextFields } from "../common/LocalizedTextFields";
import { ScenarioThumbnail } from "../common/ScenarioThumbnail";
import { SaveVersionInfo } from "../common/SaveVersionInfo";
import "./Scenarios.css";

type Texts = Record<string, LocalizedText | undefined>;
type Options = Record<ScenarioStartOption, number>;

const TEXT_KEYS = ["scenario_title", "scenario_description", "scenario_objective", "scenario_city_name"] as const;
const START_OPTIONS: ScenarioStartOption[] = ["simulation_days", "climate", "ground_cover", "asset_region"];

function startOptionsOf(save: SaveEntry): Options {
  return {
    simulation_days: save.simulation_days || 30,
    climate: normalizeClimate(save.climate),
    ground_cover: normalizeGroundCover(save.ground_cover),
    asset_region: save.asset_region ?? ASSET_REGION_STANDARD,
  };
}

export function ScenarioMetadataWindow({ apiBaseUrl, scenarioId, onClose }: {
  apiBaseUrl: string; scenarioId: string; onClose: () => void;
}) {
  const { t } = useTranslation();
  const [entry, setEntry] = useState<SaveEntry | null>(null);
  const [texts, setTexts] = useState<Texts>({});
  const [author, setAuthor] = useState("");
  const [category, setCategory] = useState("sandbox");
  const [options, setOptions] = useState<Options | null>(null);
  const [fixed, setFixed] = useState<ScenarioStartOption[]>([]);
  const [thumbnail, setThumbnail] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  useEffect(() => {
    const controller = new AbortController();
    fetchApi<{ save: SaveEntry }>(buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(scenarioId)}?template=true`), { signal: controller.signal, cache: "reload" })
      .then(({ save }) => {
        setEntry(save);
        setTexts(Object.fromEntries(TEXT_KEYS.map(key => [key, save[key] ?? {}])));
        setAuthor(save.author ?? "");
        setCategory(save.category ?? "sandbox");
        setOptions(startOptionsOf(save));
        setFixed(save.scenario_fixed_options ?? []);
      }).catch(e => { if (!controller.signal.aborted) setError(saveError(e, t)); });
    return () => controller.abort();
  }, [apiBaseUrl, scenarioId, t]);

  const choices: Record<ScenarioStartOption, { values: number[]; label: (value: number) => string }> = {
    simulation_days: { values: SIMULATION_DAYS_CHOICES, label: days => t("newGame.daysOption", { days }) },
    climate: { values: CLIMATE_TYPES, label: value => t(`climate.${value}`) },
    ground_cover: { values: GROUND_COVER_TYPES, label: value => t(`groundCover.${value}`) },
    asset_region: { values: ASSET_REGION_TYPES, label: value => t(`assetRegion.${value}`) },
  };
  const optionLabels: Record<ScenarioStartOption, string> = {
    simulation_days: t("newGame.simulationDaysLabel"), climate: t("climate.label"),
    ground_cover: t("groundCover.label"), asset_region: t("assetRegion.label"),
  };

  async function save() {
    if (!options) return;
    if (TEXT_KEYS.some(key => missingEnglish(texts[key], key === "scenario_title"))) {
      setError(t("scenarios.englishRequired"));
      return;
    }
    setBusy(true); setError("");
    try {
      await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/scenarios/${encodeURIComponent(scenarioId)}`), {
        method: "PATCH", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          ...Object.fromEntries(TEXT_KEYS.map(key => [key, compactLocalizedText(texts[key])])),
          author, category, ...options,
          scenario_fixed_options: START_OPTIONS.filter(option => fixed.includes(option)),
        }),
      });
      if (thumbnail) await uploadScenarioThumbnail(apiBaseUrl, scenarioId, thumbnail);
      await emit("scenarios:changed");
      onClose();
    } catch (e) { setError(saveError(e, t)); }
    finally { setBusy(false); }
  }

  return <form className="scenario-editor" onSubmit={e => { e.preventDefault(); void save(); }}>
    <p>{t("scenarios.metadataHint")}</p>
    {error && <p role="alert" className="scenario-error">{error}</p>}
    {entry && options && <>
      <SaveVersionInfo entry={entry} />
      <LocalizedTextFields disabled={busy} value={texts} onChange={setTexts} fields={[
        { key: "scenario_title", label: t("achievements.scenarioTitleLabel"), maxLength: 160 },
        { key: "scenario_description", label: t("achievements.scenarioDescriptionLabel"), maxLength: 8000, multiline: true },
        { key: "scenario_objective", label: t("achievements.scenarioObjectiveLabel"), maxLength: 4000, multiline: true },
        { key: "scenario_city_name", label: t("scenarios.initialCityName"), maxLength: 120 },
      ]} />
      <label className="scenario-info__field"><span>{t("scenarios.author")}</span>
        <input type="text" className="scenario-info__input" value={author} maxLength={120} disabled={busy} onChange={e => setAuthor(e.target.value)} /></label>
      <label className="scenario-info__field"><span>{t("scenarios.category")}</span>
        <select value={category} disabled={busy} onChange={e => setCategory(e.target.value)}>
          {["sandbox", "challenge", "tutorial"].map(value => <option key={value} value={value}>{t(`scenarios.${value}`)}</option>)}
        </select></label>
      <div className="scenario-info__field"><span>{t("scenarios.cityLanguage")}</span>
        <span className="scenario-start-value">{t(`settings.language.${entry.language}`, { defaultValue: entry.language })}</span>
        <span className="scenario-hint">{t("scenarios.cityLanguageHint")}</span></div>
      <fieldset className="scenario-start-settings">
        <legend>{t("scenarios.startSettings")}</legend>
        <p className="scenario-hint">{t("scenarios.startSettingsHint")}</p>
        {START_OPTIONS.map(option => <div className="scenario-start-row" key={option} data-option={option}>
          <span>{optionLabels[option]}</span>
          <select value={options[option]} disabled={busy} aria-label={optionLabels[option]}
            onChange={e => setOptions({ ...options, [option]: Number(e.target.value) })}>
            {choices[option].values.map(value => <option key={value} value={value}>{choices[option].label(value)}</option>)}
          </select>
          <label className="scenario-start-fixed">
            <input type="checkbox" checked={fixed.includes(option)} disabled={busy}
              onChange={e => setFixed(e.target.checked ? [...fixed, option] : fixed.filter(value => value !== option))} />
            {t("scenarios.fixedOption")}
          </label>
        </div>)}
      </fieldset>
      <label className="scenario-info__field"><span>{t("achievements.scenarioImageLabel")}</span>
        <ScenarioThumbnail apiBaseUrl={apiBaseUrl} templateId={scenarioId} revision={entry.content_revision} className="scenario-thumbnail" />
        <input type="file" accept="image/png" disabled={busy} onChange={e => setThumbnail(e.target.files?.[0] ?? null)} /></label>
      <div className="scenario-actions"><button type="submit" disabled={busy}>{t("achievements.scenarioSave")}</button>
        <button type="button" disabled={busy} onClick={onClose}>{t("scenarios.close")}</button></div>
    </>}
  </form>;
}
