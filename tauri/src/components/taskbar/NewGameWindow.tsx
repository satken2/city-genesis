/**
 * New Game window component
 *
 * Popup opened from New Game in the top/system menu. No title bar (registry chromeless); close through the
 * lower-left button or Esc.
 *
 * The left hero shows a subheading and heading-sized Select Scenario / Find Scenarios / Select Terrain tabs.
 * Find Scenarios is disabled because shared scenario browsing is unavailable. Select Terrain lists the scenarios
 * the bundled catalog tags terrain (bare terrains). Select Scenario shows tiled sections of collapsible tutorial
 * missions and all other scenarios, in the order of GET /api/v1/scenarios (bundled scenarios first).
 * Development debug cities (source debug), included only when the core enables development debugging, appear
 * above tutorials in a separate Debug section and start like scenarios. There is no direct terrain start: import
 * elevation files as scenarios in scenario management first. Tutorials start from core-bundled lesson cities by
 * sending only the tutorial ID. Every tutorial and scenario can start only when its list item from the core
 * reports it loadable; never assume compatibility when that is unknown.
 *
 * Tiles use full-surface images with names/source badges; descriptions and completion conditions belong in the
 * right column. Add completion badge images to cleared tutorial/scenario tiles and details, using the
 * city-independent player profile (GET /api/v1/profile).
 * The fixed-width detail panel reaches the window's top/right/bottom edges. Its top image carries the title at
 * the lower edge, followed by description, map facts (size, ground cover, climate; scenarios only), completion
 * conditions, credits (only when written; a one-line toggle, closed by default), and Start at the bottom.
 * Descriptions/conditions are author-written free text; all map facts come from the scenario list item (city values).
 * The save format version is not shown; only missing packs and the reason a scenario cannot load appear.
 *
 * Start opens a settings confirmation overlay within the window, not a native popup. Configure city name, naming
 * language, simulation days, etc. before starting. During play, warn about unsaved changes here. Keep the
 * current city and Unity view until final Start.
 *
 * Scenario introductions and initial city names are author-written multilingual text. Resolve introductions in
 * the display language and city names in the selected city language, falling back to English. City language is
 * always selectable and defaults to the app language. If it differs from the scenario, the core regenerates
 * names within the city.
 * For simulation days, climate, ground cover, and asset region, author-fixed settings are display-only; others
 * are selectable with scenario values as defaults.
 */
import { PLAY_MODE_NORMAL, PLAY_MODE_TUTORIAL } from "../../constants";


import { useState, useEffect, useCallback, useMemo, type CSSProperties, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { loadAppSettings } from "../../utils/settings";
import { SUPPORTED_LANGUAGES, resolveLocalizedText, resolveSupportedLanguage } from "../../utils/locale";
import { NEW_GAME_TUTORIAL_SCENARIOS } from "../tutorial/tutorialSteps";
import { ScenarioThumbnail } from "../common/ScenarioThumbnail";
import { normalizeGroundCover } from "../terrain/terrainData";
import { useMasterData } from "../../contexts/MasterDataContext";
import { formatMapSize } from "../../utils/mapSize";
import { FlatTabs } from "../common/FlatTabs";
import { TerrainFacts } from "../common/TerrainFacts";
import { RequiredPacks, ScenarioOriginBadges } from "../common/ScenarioSource";
import { SaveDialogButton } from "./SaveDialog";
import { BackIcon, NewGameIcon, ScenarioIcon } from "./SaveIcons";
import clearedBadgeImage from "../../assets/images/ui/badge.png";
import developmentNoticeImage from "../../assets/images/development_notice.jpg";
import "./NewGameWindow.css";
import { fetchApi } from "../../utils/api";
import { AssetPackEntry, SaveCompatibility, ScenarioEntry, ScenarioStartOption, fetchAssetPacks, prepareSaveLoad, saveError, scenarioDisplayTitle } from "../../utils/saves";
import { ASSET_PACK_STANDARD, CLIMATE_TYPES, GROUND_COVER_TYPES } from "../../constants";
import { SaveCompatibilityReason } from "../common/SaveVersionInfo";
import type { InfoDialogContent } from "../common/InfoDialogWindow";

type NewGameWindowProps = {
  apiBaseUrl: string;
  onClose: () => void;
};

/** Fields used from the player profile (GET /api/v1/profile) */
type ProfileEntry = {
  /** Completed tutorial lesson IDs, matching tutorial_id in POST /api/v1/sessions */
  tutorials_cleared: string[];
  /** Completed scenario IDs, matching id in scenario list items */
  scenarios_cleared: string[];
};

type NewGameTab = "select" | "browse" | "terrain";

/**
 * Selection displayed in the detail panel.
 * Set by clicking a list tile; this does not start the game yet.
 */
type Selection =
  | { kind: "tutorial"; id: string }
  | { kind: "scenario"; id: string };

/**
 * Display data normalized to the fields needed by the detail panel.
 * Tutorials and scenarios have different sources; the panel only needs this shared shape.
 */
type DetailView = {
  /** Thumbnail URL (e.g. tutorials), given highest display priority when provided. */
  thumbnailUrl: string | null;
  /** Scenario list item; null for tutorials. */
  scenario: ScenarioEntry | null;
  /** CSS modifier for the placeholder when no thumbnail exists */
  placeholderModifier: string;
  title: string;
  description: string;
  /**
   * Map facts, omitted for tutorials (null).
   * mapSize uses the form "7 × 6 km", or null if unknown. Normalize missing/unknown ground cover and climate to
   * the game's defaults.
   */
  terrain: { mapSize: string | null; groundCover: number; climate: string } | null;
  cleared: boolean;
  /** Free-text completion conditions. null if unset; still show the section with No Completion Conditions, since absence is informative. */
  objective: string | null;
  /** Attribution written in the city, shown verbatim. Empty when none; the section is then omitted. */
  credits: string;
  /** Loadability from the core's list item; null while unknown. Unreadable items show the reason in details and block only starting. */
  compatibility: SaveCompatibility | null;
  onStart: () => void;
};

/**
 * Selection state in settings confirmation.
 * Set scenarioId for scenarios, tutorialId for tutorials.
 */
type ConfirmSelection = {
  scenarioId: string | null;
  /** List item when starting a scenario (language, starting option values, fixed flags); null for tutorials. */
  scenario: ScenarioEntry | null;
  defaultCityName: string;
  defaultLanguage: string;
  defaultSimulationDays: number;
  playMode: number;
  tutorialId?: string | null;
};

/** Scenario starting options other than simulation days: value lists and translation key prefixes. */
const EXTRA_START_OPTIONS: { key: Exclude<ScenarioStartOption, "simulation_days">; values: number[]; labelKey: string }[] = [
  { key: "climate", values: [0, ...CLIMATE_TYPES], labelKey: "climate" },
  { key: "ground_cover", values: GROUND_COVER_TYPES, labelKey: "groundCover" },
];

/** Initial city name, resolved in selected city language, then English. */
function defaultCityNameOf(scenario: ScenarioEntry, language: string): string {
  return resolveLocalizedText(scenario.scenario_city_name, language);
}

function startOptionValuesOf(scenario: ScenarioEntry | null): Record<Exclude<ScenarioStartOption, "simulation_days">, number> {
  return {
    climate: 0,
    ground_cover: normalizeGroundCover(scenario?.ground_cover),
  };
}

/** Shown once the city appears after starting in normal play, because the economic balance is still being tuned. */
const DEVELOPMENT_NOTICE: InfoDialogContent = {
  image: developmentNoticeImage,
  headingKey: "newGame.developmentNotice.heading",
  subheadingKey: "newGame.developmentNotice.subheading",
  textKey: "newGame.developmentNotice.text",
  url: "https://store.steampowered.com/app/5021240/CityGenesis/",
  buttonKey: "newGame.developmentNotice.button",
};

/**
 * Languages supported by the game, matching Tauri's settings.
 */
const AVAILABLE_LANGUAGES = SUPPORTED_LANGUAGES;

/**
 * Simulation day options
 */
const SIMULATION_DAYS_OPTIONS = [7, 30] as const;

/** Completion badge */
function ClearedBadge({ className }: { className: string }) {
  const { t } = useTranslation();
  return <img className={className} src={clearedBadgeImage} alt={t("newGame.cleared")} title={t("newGame.cleared")} draggable={false} />;
}

/**
 * List tile with a full-surface image (children) and name overlaid at the bottom.
 * index staggers tile fade-ins when the list opens.
 */
function ScenarioTile({ title, selected, cleared, disabled, className, index, onSelect, children }: {
  title: string;
  selected: boolean;
  cleared: boolean;
  disabled: boolean;
  className?: string;
  index: number;
  onSelect: () => void;
  children: ReactNode;
}) {
  return (
    <button
      className={["new-game-tile", className, selected ? "selected" : ""].filter(Boolean).join(" ")}
      style={{ "--new-game-tile-index": index } as CSSProperties}
      onClick={onSelect}
      disabled={disabled}
      type="button"
      title={title}
      aria-pressed={selected}
    >
      {children}
      {cleared && <ClearedBadge className="new-game-tile-cleared" />}
      <span className="new-game-tile-name">{title}</span>
    </button>
  );
}

export function NewGameWindow({
  apiBaseUrl,
  onClose,
}: NewGameWindowProps) {
  const { t, i18n } = useTranslation();
  const master = useMasterData();
  const [allScenarios, setAllScenarios] = useState<ScenarioEntry[]>([]);
  // Keyed by tutorial ID, matching tutorial_id in POST /api/v1/sessions
  const [tutorialEntries, setTutorialEntries] = useState<Record<string, ScenarioEntry>>({});
  const [loading, setLoading] = useState(true);
  const [profile, setProfile] = useState<ProfileEntry | null>(null);
  const [tutorialsExpanded, setTutorialsExpanded] = useState<boolean | null>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState("");
  const [hasCurrentGame, setHasCurrentGame] = useState<boolean | null>(null);
  const [tab, setTab] = useState<NewGameTab>("select");

  // Selection currently displayed in details
  const [selection, setSelection] = useState<Selection | null>(null);

  // Settings confirmation visibility
  const [confirmSelection, setConfirmSelection] = useState<ConfirmSelection | null>(null);

  // Input values in settings confirmation
  const [cityName, setCityName] = useState("");
  // Whether the player edited the city name. If not, changing language resolves the initial name again.
  const [cityNameEdited, setCityNameEdited] = useState(false);
  const [language, setLanguage] = useState("ja");
  const [simulationDays, setSimulationDays] = useState(30);
  const [startOptions, setStartOptions] = useState(() => startOptionValuesOf(null));
  // Available additional asset packs and selected pack. Default to the scenario pack, or standard if unavailable.
  const [assetPacks, setAssetPacks] = useState<AssetPackEntry[]>([]);
  const [assetPack, setAssetPack] = useState(ASSET_PACK_STANDARD);

  // Unsaved cities also have IDs; use session state rather than save-file existence to decide whether a warning is needed.
  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    let active = true;
    let receivedSessionState = false;
    const listener = listen<{ current_save_id: string | null; backendConnected: boolean }>("session-state", ({ payload }) => {
      if (!active || !payload.backendConnected) return;
      receivedSessionState = true;
      setHasCurrentGame(payload.current_save_id != null);
    });
    void fetchApi<{ current_save_id: string | null }>(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/sessions`, {
      cache: "reload", signal: controller.signal,
    }).then(session => {
      if (active && !receivedSessionState) setHasCurrentGame(session.current_save_id != null);
    }).catch(() => {});
    return () => {
      active = false;
      controller.abort();
      void listener.then(unlisten => unlisten());
    };
  }, [apiBaseUrl]);

  /**
   * Fetch scenarios and tutorial loadability on window initialization. Failure leaves both empty, so nothing can start.
   * Refetch on scenarios:changed when scenario management adds/removes scenarios.
   */
  useEffect(() => {
    if (!apiBaseUrl) return;
    const base = apiBaseUrl.replace(/\/+$/, "");
    let active = true;
    const fetchScenarios = () => fetchApi<{ scenarios: Record<string, ScenarioEntry>; tutorials: Record<string, ScenarioEntry> }>(
      `${base}/api/v1/scenarios`, { cache: "reload" },
    ).then(data => {
      if (!active) return;
      setAllScenarios(Object.values(data.scenarios));
      setTutorialEntries(data.tutorials ?? {});
    });

    setLoading(true);
    void fetchScenarios().catch(() => {}).finally(() => { if (active) setLoading(false); });
    const listener = listen("scenarios:changed", () => { void fetchScenarios().catch(() => {}); });
    return () => { active = false; void listener.then(unlisten => unlisten()); };
  }, [apiBaseUrl]);

  /** Fetch completion records on initialization. Failure still permits opening/selecting lessons. */
  useEffect(() => {
    if (!apiBaseUrl) return;
    let active = true;
    fetchApi<ProfileEntry>(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/profile`, { cache: "reload" })
      .then(data => { if (active) setProfile(data); })
      .catch(() => { if (active) setProfile({ tutorials_cleared: [], scenarios_cleared: [] }); });
    return () => { active = false; };
  }, [apiBaseUrl]);

  const isTutorialCleared = useCallback((id: string) => profile?.tutorials_cleared.includes(id) ?? false, [profile]);
  const isScenarioCleared = useCallback((id: string) => profile?.scenarios_cleared.includes(id) ?? false, [profile]);
  // Keep the list collapsed while fetching records to avoid briefly expanding it for players who cleared everything. Respect manual expansion choices.
  const tutorialsOpen = tutorialsExpanded ?? (profile !== null && !NEW_GAME_TUTORIAL_SCENARIOS.every(scenario => isTutorialCleared(scenario.id)));

  /**
   * Start from a scenario.
   * Open settings confirmation with the scenario's existing values as defaults.
   */
  const handleStartScenario = useCallback(
    async (scenario: ScenarioEntry) => {
      const settings = await loadAppSettings();
      // Default the city language to the app language so players start in their own language without extra action.
      const defaultLang = resolveSupportedLanguage(settings.language || i18n.language);
      const defaultDays = scenario.simulation_days || 30;
      const defaultCityName = defaultCityNameOf(scenario, defaultLang);
      setCityName(defaultCityName);
      setCityNameEdited(false);
      setLanguage(defaultLang);
      setSimulationDays(defaultDays);
      setStartOptions(startOptionValuesOf(scenario));
      const packs = await fetchAssetPacks(apiBaseUrl).catch((): AssetPackEntry[] => []);
      setAssetPacks(packs);
      setAssetPack(packs.some(pack => pack.pack_id === scenario.asset_pack) ? scenario.asset_pack! : ASSET_PACK_STANDARD);
      setConfirmSelection({
        scenarioId: scenario.id,
        scenario,
        defaultCityName,
        defaultLanguage: defaultLang,
        defaultSimulationDays: defaultDays,
        playMode: PLAY_MODE_NORMAL,
        tutorialId: null,
      });
    },
    [apiBaseUrl, i18n]
  );

  /**
   * Start a tutorial mission.
   * The core bundles the lesson city (terrain/initial layout), so send only its ID.
   * If the city language differs from the lesson, the core regenerates names as for scenarios.
   */
  const handleStartTutorial = useCallback(async (tutorialId: string) => {
    const settings = await loadAppSettings();
    const defaultLang = resolveSupportedLanguage(settings.language || i18n.language);
    setCityName("");
    setCityNameEdited(false);
    setLanguage(defaultLang);
    setSimulationDays(30);
    setConfirmSelection({
      scenarioId: null,
      scenario: null,
      defaultCityName: "",
      defaultLanguage: defaultLang,
      defaultSimulationDays: 30,
      playMode: PLAY_MODE_TUTORIAL,
      tutorialId,
    });
  }, [i18n]);

  /**
   * Normalize the current selection for the detail panel.
   * Return null if nothing is selected or the selection disappeared from the list.
   */
  const detail = useMemo<DetailView | null>(() => {
    if (!selection) return null;

    if (selection.kind === "tutorial") {
      const scenario = NEW_GAME_TUTORIAL_SCENARIOS.find((s) => s.id === selection.id);
      if (!scenario) return null;
      return {
        thumbnailUrl: scenario.image || null,
        scenario: null,
        placeholderModifier: `new-game-detail-image--tutorial new-game-detail-image--${scenario.id}`,
        title: t(scenario.titleKey),
        description: t(scenario.descriptionKey),
        terrain: null,
        cleared: isTutorialCleared(scenario.id),
        objective: t("newGame.tutorialObjective"),
        credits: tutorialEntries[scenario.id]?.scenario_credits ?? "",
        compatibility: tutorialEntries[scenario.id]?.compatibility ?? null,
        onStart: () => void handleStartTutorial(scenario.id),
      };
    }

    const entry = allScenarios.find((s) => s.id === selection.id);
    if (!entry) return null;
    return {
      thumbnailUrl: null,
      scenario: entry,
      placeholderModifier: "new-game-detail-image--scenario",
      title: scenarioDisplayTitle(entry, i18n.language),
      description: resolveLocalizedText(entry.scenario_description, i18n.language) || t("newGame.detailNoDescription"),
      terrain: {
        mapSize: formatMapSize(entry.map_width, entry.map_height, i18n.language),
        groundCover: normalizeGroundCover(entry.ground_cover),
        climate: entry.climate?.name ?? "",
      },
      cleared: isScenarioCleared(entry.id),
      objective: resolveLocalizedText(entry.scenario_objective, i18n.language) || null,
      credits: entry.scenario_credits ?? "",
      compatibility: entry.compatibility ?? null,
      onStart: () => void handleStartScenario(entry),
    };
  }, [
    selection,
    allScenarios,
    tutorialEntries,
    t,
    i18n.language,
    isTutorialCleared,
    isScenarioCleared,
    handleStartScenario,
    handleStartTutorial,
  ]);

  /**
   * Start handler in settings confirmation.
   * Call Rust's create_session: launch Unity → show LoadingWindow → backend POST → poll/wait → show HUD.
   * create_session closes all open popups.
   */
  const handleConfirmStart = useCallback(async () => {
    if (!confirmSelection || hasCurrentGame == null) return;

    setBusy(true);
    setFailure("");
    // Omit author-fixed options; the core starts with scenario values.
    const fixed = new Set(confirmSelection.scenario?.scenario_fixed_options ?? []);
    const chosen = (option: ScenarioStartOption, value: number) =>
      confirmSelection.scenario && !fixed.has(option) ? value : null;
    try {
      if (confirmSelection.scenarioId && !await prepareSaveLoad(apiBaseUrl, confirmSelection.scenarioId, true, t)) {
        setBusy(false);
        return;
      }
      await invoke("create_session", {
        acceptBalanceChange: true,
        scenarioId: confirmSelection.scenarioId,
        cityName: cityName || null,
        language,
        simulationDays: fixed.has("simulation_days") ? null : simulationDays,
        climate: startOptions.climate === 0 ? null : chosen("climate", startOptions.climate),
        groundCover: chosen("ground_cover", startOptions.ground_cover),
        assetPack: confirmSelection.scenario ? assetPack : null,
        playMode: confirmSelection.playMode,
        tutorialId: confirmSelection.tutorialId || null,
        introDialog: confirmSelection.playMode === PLAY_MODE_NORMAL ? DEVELOPMENT_NOTICE : null,
      });
      onClose();
    } catch (error) {
      setFailure(saveError(error, t) || t("newGame.startFailed"));
      setBusy(false);
    }
  }, [apiBaseUrl, t, onClose, confirmSelection, hasCurrentGame, cityName, language, simulationDays, startOptions, assetPack]);

  /** Change city language. Unless the name was edited, resolve its initial value in the new language. */
  const handleLanguageChange = useCallback((next: string) => {
    setLanguage(next);
    if (confirmSelection?.scenario && !cityNameEdited) setCityName(defaultCityNameOf(confirmSelection.scenario, next));
  }, [confirmSelection, cityNameEdited]);

  /**
   * Close settings confirmation and return to tile selection.
   */
  const handleCancelConfirm = useCallback(() => {
    setConfirmSelection(null);
    setFailure("");
  }, []);

  // Esc closes settings confirmation if open, otherwise the window. Do nothing while starting.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== "Escape" || busy) return;
      event.preventDefault();
      if (confirmSelection) handleCancelConfirm();
      else onClose();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [busy, confirmSelection, handleCancelConfirm, onClose]);

  // Find Scenarios is reserved for shared scenario browsing and stays disabled.
  // Switching tabs clears the selection so details never show an item outside the visible list.
  const tabs = (
    <FlatTabs<NewGameTab>
      label={t("topmenu.newGame")}
      active={tab}
      onChange={(next) => { setTab(next); setSelection(null); }}
      tabs={[
        { id: "select", label: t("newGame.tabSelect") },
        { id: "browse", label: t("newGame.tabBrowse"), disabled: true },
        { id: "terrain", label: t("newGame.tabTerrain") },
      ]}
    />
  );

  const debugCities = allScenarios.filter((entry) => entry.origin === "debug");
  const terrains = allScenarios.filter((entry) => entry.tag === "terrain");
  const scenarios = allScenarios.filter((entry) => entry.origin !== "debug" && entry.tag !== "terrain");
  const scenarioTile = (entry: ScenarioEntry, index: number) => (
    <ScenarioTile
      key={entry.id}
      title={scenarioDisplayTitle(entry, i18n.language)}
      selected={isSelected("scenario", entry.id)}
      cleared={isScenarioCleared(entry.id)}
      disabled={busy}
      index={index}
      onSelect={() => setSelection({ kind: "scenario", id: entry.id })}
    >
      <ScenarioThumbnail apiBaseUrl={apiBaseUrl} scenarioId={entry.id} revision={entry.content_revision} />
      <ScenarioOriginBadges entry={entry} />
    </ScenarioTile>
  );
  // Unreadable items can still be selected to inspect the reason in details.
  const scenarioTiles = (entries: ScenarioEntry[], firstIndex: number) =>
    loading ? (
      <div className="new-game-empty">{t("newGame.loading")}</div>
    ) : entries.length === 0 ? (
      <div className="new-game-empty">{t("newGame.noScenarios")}</div>
    ) : (
      <div className="new-game-tiles">{entries.map((entry, index) => scenarioTile(entry, firstIndex + index))}</div>
    );

  const confirmScenario = confirmSelection?.scenario ?? null;
  const fixedOptions = new Set<ScenarioStartOption>(confirmScenario?.scenario_fixed_options ?? []);
  const isSelected = (kind: Selection["kind"], id: string) => selection?.kind === kind && selection.id === id;

  // Keep the selection screen rendered beneath the settings confirmation overlay.
  return (
    <div className="new-game">
      <div className="new-game-main">
        <header className="new-game-hero">
          <span className="new-game-overline">{t("windowType.new_game")}</span>
          {tabs}
        </header>

        {tab === "terrain" ? (
          <div className="new-game-list">{scenarioTiles(terrains, 0)}</div>
        ) : (
          <div className="new-game-list">
            {debugCities.length > 0 && (
              <section className="new-game-section">
                <h3 className="new-game-section-title">{t("newGame.sectionDebug")}</h3>
                <div className="new-game-tiles">{debugCities.map(scenarioTile)}</div>
              </section>
            )}

            <section className="new-game-section">
              <h3 className="new-game-section-title">
                <button
                  className="new-game-section-toggle"
                  type="button"
                  aria-expanded={tutorialsOpen}
                  aria-controls="new-game-tutorials"
                  onClick={() => setTutorialsExpanded(!tutorialsOpen)}
                >
                  <span>{t("newGame.sectionTutorials")}</span>
                  <span className="new-game-section-chevron" aria-hidden="true" />
                </button>
              </h3>
              <div id="new-game-tutorials" className="new-game-tiles" hidden={!tutorialsOpen}>
                {NEW_GAME_TUTORIAL_SCENARIOS.map((scenario, index) => (
                  <ScenarioTile
                    key={scenario.id}
                    className="new-game-tile--tutorial"
                    title={t(scenario.titleKey)}
                    selected={isSelected("tutorial", scenario.id)}
                    cleared={isTutorialCleared(scenario.id)}
                    disabled={busy}
                    index={debugCities.length + index}
                    onSelect={() => setSelection({ kind: "tutorial", id: scenario.id })}
                  >
                    {scenario.image ? (
                      <img className="new-game-tile-image" src={scenario.image} alt="" draggable={false} />
                    ) : (
                      <div className={`new-game-tile-image new-game-tile-image--tutorial new-game-tile-image--${scenario.id}`} />
                    )}
                  </ScenarioTile>
                ))}
              </div>
            </section>

            <section className="new-game-section">
              <h3 className="new-game-section-title">{t("newGame.sectionScenarios")}</h3>
              {scenarioTiles(scenarios, debugCities.length + NEW_GAME_TUTORIAL_SCENARIOS.length)}
            </section>
          </div>
        )}

        <footer className="new-game-footer">
          <SaveDialogButton disabled={busy} onClick={onClose}>{t("common.close")}</SaveDialogButton>
        </footer>
      </div>

      {/* Detail panel: fixed width, flush with the window's right/top/bottom edges. */}
      <aside className="new-game-detail">
        {detail ? (
          // Recreate contents on selection changes to replay the transition each time.
          <div className="new-game-detail-scroll" key={`${selection?.kind}:${selection?.id}`}>
            {detail.thumbnailUrl ? (
              <img className="new-game-detail-image" src={detail.thumbnailUrl} alt="" draggable={false} />
            ) : detail.scenario ? (
              <ScenarioThumbnail
                apiBaseUrl={apiBaseUrl}
                scenarioId={detail.scenario.id}
                revision={detail.scenario.content_revision}
                className="new-game-detail-image"
                placeholderModifier={detail.placeholderModifier}
              />
            ) : (
              <div className={`new-game-detail-image ${detail.placeholderModifier}`} />
            )}

            {/* Overlay the title at the image's bottom edge. */}
            <div className="new-game-detail-head">
              <div className="new-game-detail-badges">
                {detail.scenario
                  ? <ScenarioOriginBadges entry={detail.scenario} />
                  : <span className="new-game-detail-kind">{t("newGame.tutorialBadge")}</span>}
                {detail.cleared && <ClearedBadge className="new-game-detail-cleared" />}
              </div>
              <h2 className="new-game-detail-title">{detail.title}</h2>
            </div>

            <div className="new-game-detail-content">
              {detail.scenario && <RequiredPacks entry={detail.scenario} />}
              <SaveCompatibilityReason compatibility={detail.compatibility} />
              <p className="new-game-detail-desc">{detail.description}</p>

              {detail.terrain && <TerrainFacts {...detail.terrain} />}

              <section className="new-game-detail-block">
                <h3 className="new-game-detail-block-title">{t("newGame.detailObjectiveLabel")}</h3>
                <p className="new-game-detail-block-text">{detail.objective ?? t("newGame.detailNoObjective")}</p>
              </section>

              {detail.credits && (
                <details className="new-game-detail-credits">
                  <summary>
                    <span className="new-game-section-chevron" aria-hidden="true" />
                    <span>{t("newGame.detailCreditsLabel")}</span>
                  </summary>
                  <p className="new-game-detail-block-text">{detail.credits}</p>
                </details>
              )}
            </div>
          </div>
        ) : (
          <div className="new-game-detail-placeholder">
            <ScenarioIcon className="new-game-detail-placeholder-icon" />
            <p>{t("newGame.detailPlaceholder")}</p>
          </div>
        )}

        <div className="new-game-detail-actions">
          <button
            className="new-game-start"
            onClick={detail?.onStart}
            disabled={busy || detail?.compatibility?.loadable !== true}
            type="button"
          >
            <span>{t("newGame.startScenarioButton")}</span>
            <BackIcon className="new-game-start-arrow" />
          </button>
        </div>
      </aside>

      {/* Settings confirmation overlays selection. Clicking the backdrop does not close it while starting. */}
      {confirmSelection && (
        <div className="new-game-confirm-overlay" onMouseDown={busy ? undefined : handleCancelConfirm}>
          <div
            className="new-game-confirm"
            role="dialog"
            aria-modal="true"
            aria-labelledby="new-game-confirm-title"
            aria-describedby={hasCurrentGame ? "new-game-confirm-warning" : undefined}
            onMouseDown={(e) => e.stopPropagation()}
          >
            <h2 id="new-game-confirm-title" className="new-game-confirm-title">
              <NewGameIcon />
              <span>{t("newGame.confirmTitle")}</span>
            </h2>

            {hasCurrentGame && (
              <p id="new-game-confirm-warning" className="new-game-confirm-warning">
                {t("newGame.unsavedChangesWarning")}
              </p>
            )}

            <div className="new-game-confirm-fields">
              {/* City name */}
              <div className="new-game-confirm-field">
                <label className="new-game-confirm-label">
                  {t("newGame.cityNameLabel")}
                </label>
                <input
                  className="new-game-confirm-input"
                  type="text"
                  value={cityName}
                  onChange={(e) => { setCityName(e.target.value); setCityNameEdited(true); }}
                  placeholder={t("newGame.cityNamePlaceholder")}
                  disabled={busy}
                />
              </div>

              {/* City language */}
              <div className="new-game-confirm-field">
                <label className="new-game-confirm-label">
                  {t("newGame.languageLabel")}
                </label>
                <div className="new-game-confirm-select-wrap">
                  <select
                    className="new-game-confirm-select"
                    value={language}
                    onChange={(e) => handleLanguageChange(e.target.value)}
                    disabled={busy}
                  >
                    {AVAILABLE_LANGUAGES.map((lang) => (
                      <option key={lang} value={lang}>
                        {t(`settings.language.${lang}`)}
                      </option>
                    ))}
                  </select>
                </div>
                <span className="new-game-confirm-note">{t("newGame.languageNote")}</span>
                {confirmScenario && language !== confirmScenario.language && (
                  <span className="new-game-confirm-note new-game-confirm-relocalize">
                    {t("newGame.relocalizeNote", {
                      from: t(`settings.language.${confirmScenario.language}`, { defaultValue: confirmScenario.language }),
                      to: t(`settings.language.${language}`),
                    })}
                  </span>
                )}
              </div>

              {/* Simulation days */}
              <div className="new-game-confirm-field">
                <label className="new-game-confirm-label">
                  {t("newGame.simulationDaysLabel")}
                </label>
                {fixedOptions.has("simulation_days") ? (
                  <span className="new-game-confirm-fixed">
                    {t("newGame.daysOption", { days: simulationDays })}
                    <span className="new-game-confirm-fixed-badge">{t("newGame.fixedByScenario")}</span>
                  </span>
                ) : (
                  <div className="new-game-confirm-days">
                    {SIMULATION_DAYS_OPTIONS.map((days) => (
                      <button
                        key={days}
                        className={`new-game-confirm-days-btn ${simulationDays === days ? "active" : ""}`}
                        onClick={() => setSimulationDays(days)}
                        disabled={busy}
                        type="button"
                        aria-pressed={simulationDays === days}
                      >
                        {t("newGame.daysOption", { days })}
                      </button>
                    ))}
                  </div>
                )}
                <span className="new-game-confirm-note">{t("newGame.simulationDaysNote")}</span>
              </div>

              {/* Climate, ground cover, and asset pack (scenario starts only) */}
              {confirmScenario && EXTRA_START_OPTIONS.map(({ key, values, labelKey }) => (
                <div className="new-game-confirm-field" key={key} data-option={key}>
                  <label className="new-game-confirm-label">{t(`${labelKey}.label`)}</label>
                  {fixedOptions.has(key) ? (
                    <span className="new-game-confirm-fixed">
                      {key === "climate" ? confirmScenario.climate!.name : t(`${labelKey}.${startOptions[key]}`)}
                      <span className="new-game-confirm-fixed-badge">{t("newGame.fixedByScenario")}</span>
                    </span>
                  ) : (
                    <div className="new-game-confirm-select-wrap">
                      <select
                        className="new-game-confirm-select"
                        value={startOptions[key]}
                        aria-label={t(`${labelKey}.label`)}
                        onChange={(e) => setStartOptions({ ...startOptions, [key]: Number(e.target.value) })}
                        disabled={busy}
                      >
                        {values.map((value) => (
                          <option key={value} value={value}>{key === "climate"
                            ? value === 0 ? confirmScenario.climate!.name : master?.climates[value]?.display_name
                            : t(`${labelKey}.${value}`)}</option>
                        ))}
                      </select>
                    </div>
                  )}
                </div>
              ))}
              {confirmScenario && (
                <div className="new-game-confirm-field" data-option="asset_pack">
                  <label className="new-game-confirm-label">{t("assetPack.label")}</label>
                  <div className="new-game-confirm-select-wrap">
                    <select
                      className="new-game-confirm-select"
                      value={assetPack}
                      aria-label={t("assetPack.label")}
                      onChange={(e) => setAssetPack(e.target.value)}
                      disabled={busy}
                    >
                      <option value={ASSET_PACK_STANDARD}>{t("assetPack.standard")}</option>
                      {assetPacks.map((pack) => (
                        <option key={pack.pack_id} value={pack.pack_id}>{resolveLocalizedText(pack.display_name, i18n.language)}</option>
                      ))}
                    </select>
                  </div>
                </div>
              )}
            </div>

            {/* Reason starting failed; use the button spinner while starting. */}
            {failure && <p className="save-dialog-error" role="alert">{failure}</p>}

            <div className="new-game-confirm-actions">
              <SaveDialogButton disabled={busy} onClick={handleCancelConfirm}>
                {t("newGame.backButton")}
              </SaveDialogButton>
              <SaveDialogButton kind={hasCurrentGame ? "danger" : "primary"} busy={busy} disabled={hasCurrentGame == null} onClick={() => void handleConfirmStart()}>
                {busy ? t("newGame.starting") : t("newGame.startButton")}
              </SaveDialogButton>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
