/**
 * シナリオの紹介情報ウィンドウ。システムメニューの「シナリオとして保存」「別のシナリオとして保存」(作成モード)と、シナリオ管理の「紹介情報を編集」から開く。
 *
 * - 紹介文(タイトル・説明・達成条件)と初期の都市名を多言語で書く。英語は必須(タイトルは常に、
 *   ほかの項目は1言語でも書けば)で、表示する言語の文が無いときは英語が出る。
 * - 開始設定(シミュレーション日数・気候・地表・アセットの地域)の値と、ゲーム開始時にプレイヤーが変更できるかを決める。
 *   画面は「変更可能」のチェックで見せ、コアへは反転して固定する項目(scenario_fixed_options)として送る。
 * - 開始設定の先頭に都市内の名前の言語を並べる。値はここでは変えられず(名前を付け直さずに言語だけを変えると、
 *   ゲーム開始時の付け直しの判定が狂う)、プレイヤーは常に変更できるので「変更可能」はオンのまま外せない。
 * - 作成モード(create)はプレイ中の都市の紹介情報・開始設定を初期値にし、「保存」で新しいシナリオを作る
 *   (既存のシナリオは上書きしない)。保存するまで何も書かないので、キャンセルすれば何も残らない。
 *   画像は選んだファイル、選ばなければ保存の直前に撮った3Dビューを、次の保存に入る画像としてコアへ預けてから作る。
 */
import { useEffect, useState } from "react";
import { emit } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { ScenarioEntry, ScenarioStartOption, captureSaveThumbnail, saveError, uploadScenarioThumbnail } from "../../utils/saves";
import { compactLocalizedText, missingEnglish, type LocalizedText } from "../../utils/locale";
import { ASSET_REGION_STANDARD, ASSET_REGION_TYPES, GROUND_COVER_TYPES, SIMULATION_DAYS_CHOICES } from "../../constants";
import { normalizeGroundCover } from "../terrain/terrainData";
import type { ClimateSettings } from "../../utils/climate";
import { LocalizedTextFields } from "../common/LocalizedTextFields";
import { ScenarioThumbnail } from "../common/ScenarioThumbnail";
import { SaveVersionInfo } from "../common/SaveVersionInfo";
import "./Scenarios.css";

type Texts = Record<string, LocalizedText | undefined>;
type Options = Record<Exclude<ScenarioStartOption, "climate">, number> & { climate: ClimateSettings };

const TEXT_KEYS = ["scenario_title", "scenario_description", "scenario_objective", "scenario_city_name"] as const;
const START_OPTIONS: ScenarioStartOption[] = ["simulation_days", "climate", "ground_cover", "asset_region"];

/** フォームの初期値の元。編集では保存済みのシナリオ、作成ではプレイ中の都市(GET /api/v1/city/)と都市の言語 */
type ScenarioSource = Pick<ScenarioEntry, (typeof TEXT_KEYS)[number] | "scenario_fixed_options" | "author" | "language"
  | "simulation_days" | "climate" | "ground_cover" | "asset_region">;

function startOptionsOf(source: ScenarioSource): Options {
  return {
    simulation_days: source.simulation_days || 30,
    climate: source.climate!,
    ground_cover: normalizeGroundCover(source.ground_cover),
    asset_region: source.asset_region ?? ASSET_REGION_STANDARD,
  };
}

export function ScenarioMetadataWindow({ apiBaseUrl, scenarioId, create = false, onClose }: {
  apiBaseUrl: string; scenarioId: string; create?: boolean; onClose: () => void;
}) {
  const { t } = useTranslation();
  const [entry, setEntry] = useState<ScenarioEntry | null>(null);
  const [language, setLanguage] = useState<string | null>(null);
  const [texts, setTexts] = useState<Texts>({});
  const [author, setAuthor] = useState("");
  const [options, setOptions] = useState<Options | null>(null);
  const [fixed, setFixed] = useState<ScenarioStartOption[]>([]);
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
      setLanguage(source.language);
    };
    const load = create
      ? Promise.all([
        fetchApi<{ city: Omit<ScenarioSource, "author" | "language"> }>(buildApiUrl(apiBaseUrl, "/api/v1/city/"), { signal }),
        fetchApi<{ language: string }>(buildApiUrl(apiBaseUrl, "/api/v1/sessions"), { signal, cache: "reload" }),
      ]).then(([{ city }, session]) => apply({ ...city, language: session.language }))
      : fetchApi<{ save: ScenarioEntry }>(buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(scenarioId)}?template=true`), { signal, cache: "reload" })
        .then(({ save }) => { setEntry(save); apply(save); });
    load.catch(e => { if (!signal.aborted) setError(saveError(e, t)); });
    return () => controller.abort();
  }, [apiBaseUrl, create, scenarioId, t]);

  const choices: Record<Exclude<ScenarioStartOption, "climate">, { values: number[]; label: (value: number) => string }> = {
    simulation_days: { values: SIMULATION_DAYS_CHOICES, label: days => t("newGame.daysOption", { days }) },
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
    const body = JSON.stringify({
      ...Object.fromEntries(TEXT_KEYS.map(key => [key, compactLocalizedText(texts[key])])),
      author, ...options,
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
        await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/templates"), { method: "POST", headers: { "Content-Type": "application/json" }, body });
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
      </section>
      <label className="scenario-info__field"><span>{t("achievements.scenarioImageLabel")}</span>
        {entry
          ? <ScenarioThumbnail apiBaseUrl={apiBaseUrl} templateId={scenarioId} revision={entry.content_revision} className="scenario-thumbnail" />
          : <div className="scenario-thumbnail" />}
        <input type="file" accept="image/png" disabled={busy} onChange={e => setThumbnail(e.target.files?.[0] ?? null)} /></label>
      <div className="scenario-actions"><button type="submit" disabled={busy}>{t("achievements.scenarioSave")}</button>
        <button type="button" disabled={busy} onClick={onClose}>{create ? t("common.cancel") : t("scenarios.close")}</button></div>
    </>}
  </form>;
}
