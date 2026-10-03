/**
 * ニューゲームウィンドウコンポーネント
 *
 * トップメニューとシステムメニューの「ニューゲーム」ボタンから開かれるポップアップウィンドウ。
 * タイトルバーを持たないウィンドウ(レジストリの chromeless)で、閉じるのは左下の「閉じる」と Esc。
 *
 * 左カラムの上端はヒーローで、小見出しの下にタブ「シナリオを選択」と「シナリオを探す」(共有シナリオの閲覧。未提供なので無効)を
 * 見出しの大きさで並べる。その下が選択肢のタイル一覧で、開閉できるチュートリアルミッションと、全保存元(自作・コミュニティ・DLC)の
 * シナリオ(GET /api/v1/scenarios)を節に分けて並べる。地形から直接始める入口は無く、標高ファイルはシナリオ管理で
 * シナリオにしてから遊ぶ。チュートリアルはコアが同梱の教材の都市から始める(チュートリアルIDだけを送る)。
 *
 * 一覧のタイルは画像を全面に敷き、名前と保存元の札だけを重ねる。説明・達成条件は右カラムに集約する。
 * クリア済みのチュートリアル・シナリオにはバッジ画像を付ける(タイルの右上と詳細パネル)。クリアの記録は
 * 都市と独立したプレイヤーのプロフィール(GET /api/v1/profile)から引く。
 * 右カラム(詳細パネル)はウィンドウの右端・上端・下端まで届く固定幅の面で、選んだ対象の画像を上端いっぱいに敷き、
 * 画像の下端にタイトルを重ねる。その下に説明・マップの事実欄(大きさ・地表・気候。シナリオだけ)・達成条件を並べ、
 * 下端に開始ボタンを置く。シナリオの説明文・達成条件は作者のフリーテキスト。事実欄の値はすべて一覧の要素(都市が持つ値)から引く。
 *
 * 開始ボタンで「ゲーム設定の確認」ダイアログが選択画面の上に重なり(ウィンドウ内のオーバーレイで、
 * ネイティブのウィンドウは開かない)、都市名・都市の言語・シミュレーション日数などを設定してからゲームを開始する。
 * プレイ中はこのダイアログに未保存の変更が失われる警告を出す。最終の「開始」まで現在の都市とUnityの表示を保持する。
 *
 * シナリオの紹介文・初期の都市名は作者が多言語で書くテキストで、紹介文は表示言語、初期の都市名は
 * 選んだ都市の言語で引く(無ければ英語)。都市の言語はいつでも選べ、既定はアプリ設定の言語。シナリオと
 * 違う言語を選ぶと、コアが都市内の名前を付け直す。シミュレーション日数・気候・地表・アセットの地域は、
 * 作者が固定した項目は表示だけ、固定していない項目はシナリオの値を初期値に選べる。
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
import { normalizeClimate, normalizeGroundCover } from "../terrain/terrainData";
import { formatMapSize } from "../../utils/mapSize";
import { FlatTabs } from "../common/FlatTabs";
import { TerrainFacts } from "../common/TerrainFacts";
import { ScenarioOriginBadges } from "../common/ScenarioSource";
import { SaveDialogButton } from "./SaveDialog";
import { BackIcon, NewGameIcon, ScenarioIcon } from "./SaveIcons";
import clearedBadgeImage from "../../assets/images/ui/badge.png";
import "./NewGameWindow.css";
import { fetchApi } from "../../utils/api";
import { SaveEntry, ScenarioStartOption, prepareSaveLoad, saveError, scenarioDisplayTitle } from "../../utils/saves";
import { ASSET_REGION_STANDARD, ASSET_REGION_TYPES, CLIMATE_TYPES, GROUND_COVER_TYPES } from "../../constants";
import { SaveVersionInfo } from "../common/SaveVersionInfo";

type NewGameWindowProps = {
  apiBaseUrl: string;
  onClose: () => void;
};

/**
 * シナリオ一覧(GET /api/v1/scenarios)の要素。
 * scenario_* は紹介用の項目で、紹介文は任意。読み込み可否はコアの検証結果に従う。
 */
type TemplateEntry = SaveEntry;

/** プレイヤーのプロフィール(GET /api/v1/profile)のうち、この画面が使う項目 */
type ProfileEntry = {
  /** クリアしたチュートリアルの教材ID(POST /api/v1/sessions の tutorial_id と同じ値) */
  tutorials_cleared: string[];
  /** クリアしたシナリオのID(シナリオ一覧の要素の id と同じ値) */
  scenarios_cleared: string[];
};

type NewGameTab = "select" | "browse";

/**
 * 詳細パネルに表示する選択対象。
 * 一覧のタイルをクリックすると設定される（この時点ではまだゲームは始まらない）。
 */
type Selection =
  | { kind: "tutorial"; id: string }
  | { kind: "template"; id: string };

/**
 * 詳細パネルの描画に必要な項目へ正規化した表示データ。
 * チュートリアルとシナリオで供給元が異なるため、パネル側は
 * この形だけを知っていればよいようにする。
 */
type DetailView = {
  /** サムネイル画像URL（チュートリアル用等）。指定されている場合は最優先表示 */
  thumbnailUrl: string | null;
  /** シナリオ一覧の要素。チュートリアルでは null */
  template: TemplateEntry | null;
  /** サムネイルが無いときに使うプレースホルダーのCSS修飾子 */
  placeholderModifier: string;
  title: string;
  description: string;
  /**
   * マップの事実欄。チュートリアルは出さない(null)。
   * mapSize は「7 × 6 km」の形で、不明なら null。地表・気候は未指定・未知の値をゲームと同じ既定に正規化する。
   */
  terrain: { mapSize: string | null; groundCover: number; climate: number } | null;
  cleared: boolean;
  /** 達成条件のフリーテキスト。未設定なら null で、詳細には節ごと出したうえで「達成条件なし」と書く(無いことも情報なので隠さない) */
  objective: string | null;
  /** 読み込めないシナリオは詳細で理由を見せ、開始だけ止める */
  startDisabled: boolean;
  onStart: () => void;
};

/**
 * 設定確認モーダルの選択状態
 *
 * シナリオは templateId、チュートリアルは tutorialId をセットする。
 */
type ConfirmSelection = {
  templateId: string | null;
  /** シナリオから始めるときの一覧の要素(言語・開始設定の値と固定の有無)。チュートリアルでは null */
  template: TemplateEntry | null;
  defaultCityName: string;
  defaultLanguage: string;
  defaultSimulationDays: number;
  playMode: number;
  tutorialId?: string | null;
};

/** シナリオの開始設定のうち、シミュレーション日数以外の選択肢(値の一覧と翻訳キーの接頭辞) */
const EXTRA_START_OPTIONS: { key: Exclude<ScenarioStartOption, "simulation_days">; values: number[]; labelKey: string }[] = [
  { key: "climate", values: CLIMATE_TYPES, labelKey: "climate" },
  { key: "ground_cover", values: GROUND_COVER_TYPES, labelKey: "groundCover" },
  { key: "asset_region", values: ASSET_REGION_TYPES, labelKey: "assetRegion" },
];

/** 初期の都市名。選んだ都市の言語 → 英語の順に引き、無ければ保存済みの都市名 */
function defaultCityNameOf(template: TemplateEntry, language: string): string {
  return resolveLocalizedText(template.scenario_city_name, language) || template.city_name || "";
}

function startOptionValuesOf(template: TemplateEntry | null): Record<Exclude<ScenarioStartOption, "simulation_days">, number> {
  return {
    climate: normalizeClimate(template?.climate),
    ground_cover: normalizeGroundCover(template?.ground_cover),
    asset_region: template?.asset_region ?? ASSET_REGION_STANDARD,
  };
}

/**
 * ゲームで使用可能な言語の一覧
 * Tauri側の設定と同じ言語セットを提供する
 */
const AVAILABLE_LANGUAGES = SUPPORTED_LANGUAGES;

/**
 * シミュレーション日数の選択肢
 */
const SIMULATION_DAYS_OPTIONS = [7, 30] as const;

/** クリア済みのバッジ */
function ClearedBadge({ className }: { className: string }) {
  const { t } = useTranslation();
  return <img className={className} src={clearedBadgeImage} alt={t("newGame.cleared")} title={t("newGame.cleared")} draggable={false} />;
}

/**
 * 一覧のタイル。画像(children)を全面に敷き、下端に名前を重ねる。
 * index は一覧を開いたときにタイルを順に浮かび上がらせる時差に使う。
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
  const [templates, setTemplates] = useState<TemplateEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [profile, setProfile] = useState<ProfileEntry | null>(null);
  const [tutorialsExpanded, setTutorialsExpanded] = useState<boolean | null>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState("");
  const [hasCurrentGame, setHasCurrentGame] = useState<boolean | null>(null);

  // 詳細パネルに表示中の選択対象
  const [selection, setSelection] = useState<Selection | null>(null);

  // 設定確認モーダルの表示状態
  const [confirmSelection, setConfirmSelection] = useState<ConfirmSelection | null>(null);

  // 設定確認モーダル内の入力値
  const [cityName, setCityName] = useState("");
  // プレイヤーが都市名を書き換えたか。書き換えていなければ、言語を変えたとき初期の都市名を引き直す
  const [cityNameEdited, setCityNameEdited] = useState(false);
  const [language, setLanguage] = useState("ja");
  const [simulationDays, setSimulationDays] = useState(30);
  const [startOptions, setStartOptions] = useState(() => startOptionValuesOf(null));

  // 未保存の都市にもIDがあるため、保存ファイルの有無ではなくセッションで警告の必要性を判定する。
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
   * ウィンドウ初期化時にシナリオ一覧を取得する。失敗してもチュートリアルは始められるので一覧を空で出す。
   * シナリオ管理でシナリオが増減したら(scenarios:changed)取り直す。
   */
  useEffect(() => {
    if (!apiBaseUrl) return;
    const base = apiBaseUrl.replace(/\/+$/, "");
    let active = true;
    const fetchTemplates = () => fetchApi<{ scenarios: Record<string, TemplateEntry> }>(`${base}/api/v1/scenarios`, { cache: "reload" })
      .then(data => { if (active) setTemplates(Object.values(data.scenarios)); });

    setLoading(true);
    void fetchTemplates().catch(() => {}).finally(() => { if (active) setLoading(false); });
    const listener = listen("scenarios:changed", () => { void fetchTemplates().catch(() => {}); });
    return () => { active = false; void listener.then(unlisten => unlisten()); };
  }, [apiBaseUrl]);

  /** ウィンドウ初期化時にクリアの記録を取得する。失敗しても教材は開いて選択できる。 */
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
  // 全クリアのプレイヤーに一瞬一覧が開いて見えないよう、記録の取得中は閉じる。手動の開閉を優先する。
  const tutorialsOpen = tutorialsExpanded ?? (profile !== null && !NEW_GAME_TUTORIAL_SCENARIOS.every(scenario => isTutorialCleared(scenario.id)));

  /**
   * テンプレート（シナリオ）での開始。
   * 設定確認モーダルを開き、テンプレートの既存値をデフォルトとしてセットする。
   */
  const handleStartTemplate = useCallback(
    async (template: TemplateEntry) => {
      const settings = await loadAppSettings();
      // 都市の言語の既定はアプリ設定の言語(他言語のプレイヤーが何もしなくても自分の言語で始まる)
      const defaultLang = resolveSupportedLanguage(settings.language || i18n.language);
      const defaultDays = template.simulation_days || 30;
      const defaultCityName = defaultCityNameOf(template, defaultLang);
      setCityName(defaultCityName);
      setCityNameEdited(false);
      setLanguage(defaultLang);
      setSimulationDays(defaultDays);
      setStartOptions(startOptionValuesOf(template));
      setConfirmSelection({
        templateId: template.id,
        template,
        defaultCityName,
        defaultLanguage: defaultLang,
        defaultSimulationDays: defaultDays,
        playMode: PLAY_MODE_NORMAL,
        tutorialId: null,
      });
    },
    [i18n]
  );

  /**
   * チュートリアルミッションでの開始。
   * 教材の都市(地形と初期配置)はコアが同梱しているので、チュートリアルIDだけを送る。
   * 都市の言語が教材と違えば、コアがシナリオと同じく名前を付け直す。
   */
  const handleStartTutorial = useCallback(async (tutorialId: string) => {
    const settings = await loadAppSettings();
    const defaultLang = resolveSupportedLanguage(settings.language || i18n.language);
    setCityName("");
    setCityNameEdited(false);
    setLanguage(defaultLang);
    setSimulationDays(30);
    setConfirmSelection({
      templateId: null,
      template: null,
      defaultCityName: "",
      defaultLanguage: defaultLang,
      defaultSimulationDays: 30,
      playMode: PLAY_MODE_TUTORIAL,
      tutorialId,
    });
  }, [i18n]);

  /**
   * 選択中の対象を詳細パネル用の表示データへ正規化する。
   * 選択が無い、または選択対象が一覧から消えた場合は null を返す。
   */
  const detail = useMemo<DetailView | null>(() => {
    if (!selection) return null;

    if (selection.kind === "tutorial") {
      const scenario = NEW_GAME_TUTORIAL_SCENARIOS.find((s) => s.id === selection.id);
      if (!scenario) return null;
      return {
        thumbnailUrl: scenario.image || null,
        template: null,
        placeholderModifier: `new-game-detail-image--tutorial new-game-detail-image--${scenario.id}`,
        title: t(scenario.titleKey),
        description: t(scenario.descriptionKey),
        terrain: null,
        cleared: isTutorialCleared(scenario.id),
        objective: t("newGame.tutorialObjective"),
        startDisabled: false,
        onStart: () => void handleStartTutorial(scenario.id),
      };
    }

    const template = templates.find((tmpl) => tmpl.id === selection.id);
    if (!template) return null;
    return {
      thumbnailUrl: null,
      template,
      placeholderModifier: "new-game-detail-image--template",
      title: scenarioDisplayTitle(template, i18n.language),
      description: resolveLocalizedText(template.scenario_description, i18n.language) || t("newGame.detailNoDescription"),
      terrain: {
        mapSize: formatMapSize(template.map_width, template.map_height, i18n.language),
        groundCover: normalizeGroundCover(template.ground_cover),
        climate: normalizeClimate(template.climate),
      },
      cleared: isScenarioCleared(template.id),
      objective: resolveLocalizedText(template.scenario_objective, i18n.language) || null,
      startDisabled: template.compatibility?.loadable === false,
      onStart: () => void handleStartTemplate(template),
    };
  }, [
    selection,
    templates,
    t,
    i18n.language,
    isTutorialCleared,
    isScenarioCleared,
    handleStartTemplate,
    handleStartTutorial,
  ]);

  /**
   * 設定確認モーダルで「開始」を押した時のハンドラ。
   * Rust 側の create_session を呼び出し、Unity 起動 → LoadingWindow 表示 →
   * バックエンド POST → ポーリング待機 → HUD 表示 のフローを実行する。
   * create_session は開いているポップアップをすべて閉じる。
   */
  const handleConfirmStart = useCallback(async () => {
    if (!confirmSelection || hasCurrentGame == null) return;

    setBusy(true);
    setFailure("");
    // 作者が固定した項目は送らない(コアがシナリオの値で始める)
    const fixed = new Set(confirmSelection.template?.scenario_fixed_options ?? []);
    const chosen = (option: ScenarioStartOption, value: number) =>
      confirmSelection.template && !fixed.has(option) ? value : null;
    try {
      if (confirmSelection.templateId && !await prepareSaveLoad(apiBaseUrl, confirmSelection.templateId, true, t)) {
        setBusy(false);
        return;
      }
      await invoke("create_session", {
        acceptBalanceChange: true,
        templateId: confirmSelection.templateId,
        cityName: cityName || null,
        language,
        simulationDays: fixed.has("simulation_days") ? null : simulationDays,
        climate: chosen("climate", startOptions.climate),
        groundCover: chosen("ground_cover", startOptions.ground_cover),
        assetRegion: chosen("asset_region", startOptions.asset_region),
        playMode: confirmSelection.playMode,
        tutorialId: confirmSelection.tutorialId || null,
      });
      onClose();
    } catch (error) {
      setFailure(saveError(error, t) || t("newGame.startFailed"));
      setBusy(false);
    }
  }, [apiBaseUrl, t, onClose, confirmSelection, hasCurrentGame, cityName, language, simulationDays, startOptions]);

  /** 都市の言語を変える。都市名を書き換えていなければ、その言語の初期の都市名に引き直す */
  const handleLanguageChange = useCallback((next: string) => {
    setLanguage(next);
    if (confirmSelection?.template && !cityNameEdited) setCityName(defaultCityNameOf(confirmSelection.template, next));
  }, [confirmSelection, cityNameEdited]);

  /**
   * 設定確認モーダルを閉じてタイル選択画面に戻る
   */
  const handleCancelConfirm = useCallback(() => {
    setConfirmSelection(null);
    setFailure("");
  }, []);

  // Esc は設定確認ダイアログを開いていればダイアログを、いなければウィンドウを閉じる。開始処理中は何もしない
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

  // 「シナリオを探す」は共有シナリオの閲覧画面の予定地。未提供なので常に「シナリオを選択」を表示する。
  const tabs = (
    <FlatTabs<NewGameTab>
      label={t("topmenu.newGame")}
      active="select"
      onChange={() => {}}
      tabs={[
        { id: "select", label: t("newGame.tabSelect") },
        { id: "browse", label: t("newGame.tabBrowse"), disabled: true, badge: t("newGame.comingSoon") },
      ]}
    />
  );

  const confirmTemplate = confirmSelection?.template ?? null;
  const fixedOptions = new Set<ScenarioStartOption>(confirmTemplate?.scenario_fixed_options ?? []);
  const isSelected = (kind: Selection["kind"], id: string) => selection?.kind === kind && selection.id === id;

  // 選択画面。設定確認ダイアログを開いている間も、幕の下に描いたままにする
  return (
    <div className="new-game">
      <div className="new-game-main">
        <header className="new-game-hero">
          <span className="new-game-overline">{t("windowType.new_game")}</span>
          {tabs}
        </header>

        <div className="new-game-list">
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
                  index={index}
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
            {loading ? (
              <div className="new-game-empty">{t("newGame.loading")}</div>
            ) : templates.length === 0 ? (
              <div className="new-game-empty">{t("newGame.noTemplates")}</div>
            ) : (
              <div className="new-game-tiles">
                {/* 読み込めない作品も選択し、詳細で理由を確認できる。 */}
                {templates.map((tmpl, index) => (
                  <ScenarioTile
                    key={tmpl.id}
                    title={scenarioDisplayTitle(tmpl, i18n.language)}
                    selected={isSelected("template", tmpl.id)}
                    cleared={isScenarioCleared(tmpl.id)}
                    disabled={busy}
                    index={NEW_GAME_TUTORIAL_SCENARIOS.length + index}
                    onSelect={() => setSelection({ kind: "template", id: tmpl.id })}
                  >
                    <ScenarioThumbnail apiBaseUrl={apiBaseUrl} templateId={tmpl.id} revision={tmpl.content_revision} />
                    <ScenarioOriginBadges entry={tmpl} />
                  </ScenarioTile>
                ))}
              </div>
            )}
          </section>
        </div>

        <footer className="new-game-footer">
          <SaveDialogButton disabled={busy} onClick={onClose}>{t("common.close")}</SaveDialogButton>
        </footer>
      </div>

      {/* 詳細パネル: 固定幅で、ウィンドウの右端・上端・下端まで届く */}
      <aside className="new-game-detail">
        {detail ? (
          // 選択を変えるたびに中身を作り直し、切り替わりの演出を毎回流す
          <div className="new-game-detail-scroll" key={`${selection?.kind}:${selection?.id}`}>
            {detail.thumbnailUrl ? (
              <img className="new-game-detail-image" src={detail.thumbnailUrl} alt="" draggable={false} />
            ) : detail.template ? (
              <ScenarioThumbnail
                apiBaseUrl={apiBaseUrl}
                templateId={detail.template.id}
                revision={detail.template.content_revision}
                className="new-game-detail-image"
                placeholderModifier={detail.placeholderModifier}
              />
            ) : (
              <div className={`new-game-detail-image ${detail.placeholderModifier}`} />
            )}

            {/* タイトルは画像の下端に重ねる */}
            <div className="new-game-detail-head">
              <div className="new-game-detail-badges">
                {detail.template
                  ? <ScenarioOriginBadges entry={detail.template} />
                  : <span className="new-game-detail-kind">{t("newGame.tutorialBadge")}</span>}
                {detail.cleared && <ClearedBadge className="new-game-detail-cleared" />}
              </div>
              <h2 className="new-game-detail-title">{detail.title}</h2>
            </div>

            <div className="new-game-detail-content">
              {detail.template && <SaveVersionInfo entry={detail.template} />}
              <p className="new-game-detail-desc">{detail.description}</p>

              {detail.terrain && <TerrainFacts {...detail.terrain} />}

              <section className="new-game-detail-block">
                <h3 className="new-game-detail-block-title">{t("newGame.detailObjectiveLabel")}</h3>
                <p className="new-game-detail-block-text">{detail.objective ?? t("newGame.detailNoObjective")}</p>
              </section>
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
            disabled={busy || !detail || detail.startDisabled}
            type="button"
          >
            <span>{t("newGame.startScenarioButton")}</span>
            <BackIcon className="new-game-start-arrow" />
          </button>
        </div>
      </aside>

      {/* 設定確認ダイアログ: 選択画面の上に重ねる。開始処理中は幕を押しても閉じない */}
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
              {/* 都市名 */}
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

              {/* 都市の言語 */}
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
                {confirmTemplate && language !== confirmTemplate.language && (
                  <span className="new-game-confirm-note new-game-confirm-relocalize">
                    {t("newGame.relocalizeNote", {
                      from: t(`settings.language.${confirmTemplate.language}`, { defaultValue: confirmTemplate.language }),
                      to: t(`settings.language.${language}`),
                    })}
                  </span>
                )}
              </div>

              {/* シミュレーション日数 */}
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

              {/* 気候・地表・アセットの地域(シナリオから始めるときだけ) */}
              {confirmTemplate && EXTRA_START_OPTIONS.map(({ key, values, labelKey }) => (
                <div className="new-game-confirm-field" key={key} data-option={key}>
                  <label className="new-game-confirm-label">{t(`${labelKey}.label`)}</label>
                  {fixedOptions.has(key) ? (
                    <span className="new-game-confirm-fixed">
                      {t(`${labelKey}.${startOptions[key]}`)}
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
                          <option key={value} value={value}>{t(`${labelKey}.${value}`)}</option>
                        ))}
                      </select>
                    </div>
                  )}
                </div>
              ))}
            </div>

            {/* 開始に失敗した理由。開始中はボタンのスピナーで示す */}
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
