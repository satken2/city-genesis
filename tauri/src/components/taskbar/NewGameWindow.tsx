/**
 * ニューゲームウィンドウコンポーネント
 *
 * トップメニューの「ニューゲーム」ボタンから開かれるポップアップウィンドウ。
 * 上部のタブは「シナリオを選択」と「シナリオを探す」(共有シナリオの閲覧。未提供なので無効)。
 *
 * 「シナリオを選択」は左右2カラム構成で、左に選択肢の一覧、右に固定幅の詳細パネルを置く。
 * 一覧はチュートリアルミッション(常に表示)と、全保存元(自作・コミュニティ・DLC)のシナリオ
 * (GET /api/v1/scenarios)を並べる。地形から直接始める入口は無く、地形はシナリオ管理で
 * シナリオにしてから遊ぶ。チュートリアルだけは教材が前提とする地形から直接始める。
 *
 * 一覧のタイルはサムネイルと名前と保存元の札だけを出す。説明・達成条件は右カラムに集約する。
 * 右カラム(詳細パネル)はサムネイルを大きく出し、その下にタイトル・説明・舞台となるマップ
 * (大きさ・地表・気候)・達成条件を並べる。シナリオの説明文・達成条件は作者のフリーテキスト。
 * シナリオの地表・気候は地形エディタで変えられるので、一覧の要素(都市が持つ値)を優先する。
 * マップの寸法は変えられないので、大きさはオリジナル地形の登録表から引く。
 *
 * 詳細パネルの開始ボタンで「ゲーム設定の確認」画面に切り替わり、
 * 都市名・都市の言語・シミュレーション日数を設定してからゲームを開始する。
 */

import { useState, useEffect, useCallback, useMemo } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { loadAppSettings } from "../../utils/settings";
import { SUPPORTED_LANGUAGES, resolveSupportedLanguage } from "../../utils/locale";
import { TUTORIAL_SCENARIOS } from "../tutorial/tutorialSteps";
import { ScenarioThumbnail } from "../common/ScenarioThumbnail";
import { normalizeClimate, normalizeGroundCover } from "../terrain/terrainData";
import { formatMapSize, terrainMapTitle, type TerrainMapEntry } from "../../utils/terrainMaps";
import { FlatTabs } from "../common/FlatTabs";
import { TerrainFacts } from "../common/TerrainFacts";
import { ScenarioOriginBadges } from "../common/ScenarioSource";
import "./NewGameWindow.css";
import { fetchApi } from "../../utils/api";
import { SaveEntry, prepareSaveLoad, saveError } from "../../utils/saves";
import { SaveVersionInfo } from "../common/SaveVersionInfo";

type NewGameWindowProps = {
  apiBaseUrl: string;
  onClose: () => void;
};

/**
 * シナリオ一覧(GET /api/v1/scenarios)の要素。
 * scenario_* と terrain_map_* は紹介用の項目で、紹介文は任意。読み込み可否はコアの検証結果に従う。
 */
type TemplateEntry = SaveEntry;

type NewGameTab = "select" | "browse";

/**
 * ステータスメッセージの型
 */
type StatusMessage = {
  text: string;
  type: "success" | "error" | "info";
};

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
  /** シナリオ固有の画像が無いときにサムネイルとして使う地形マップID */
  terrainMapId: string | null;
  /** サムネイルが無いときに使うプレースホルダーのCSS修飾子 */
  placeholderModifier: string;
  title: string;
  description: string;
  /** 舞台となるマップ名 */
  mapName: string | null;
  /** マップサイズの表示文字列（例: 7 × 6 km）。不明なら null */
  mapSize: string | null;
  /** 地表種別。値の供給元(シナリオの一覧要素・登録表)がどちらも無いときは null */
  groundCover: number | null;
  /** 気候区分。未指定・未知の値はゲームと同じ温暖湿潤に正規化する */
  climate: number;
  /** 達成条件のフリーテキスト。未設定なら null */
  objective: string | null;
  /** 開始ボタンのラベル */
  startLabel: string;
  /** 読み込めないシナリオは詳細で理由を見せ、開始だけ止める */
  startDisabled: boolean;
  onStart: () => void;
};

/**
 * 設定確認モーダルの選択状態
 *
 * チュートリアルは terrainId(教材が前提とする地形)、シナリオは templateId をセットし、
 * もう一方は null になる。
 */
type ConfirmSelection = {
  terrainId: string | null;
  templateId: string | null;
  defaultCityName: string;
  defaultLanguage: string;
  defaultSimulationDays: number;
  isTutorial: boolean;
  tutorialId?: string | null;
};

/**
 * ゲームで使用可能な言語の一覧
 * Tauri側の設定と同じ言語セットを提供する
 */
const AVAILABLE_LANGUAGES = SUPPORTED_LANGUAGES;

/**
 * シミュレーション日数の選択肢
 */
const SIMULATION_DAYS_OPTIONS = [7, 30] as const;

export function NewGameWindow({
  apiBaseUrl,
  onClose,
}: NewGameWindowProps) {
  const { t, i18n } = useTranslation();
  const [terrainMaps, setTerrainMaps] = useState<TerrainMapEntry[]>([]);
  const [templates, setTemplates] = useState<TemplateEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<StatusMessage | null>(null);

  // 詳細パネルに表示中の選択対象
  const [selection, setSelection] = useState<Selection | null>(null);

  // 設定確認モーダルの表示状態
  const [confirmSelection, setConfirmSelection] = useState<ConfirmSelection | null>(null);

  // 設定確認モーダル内の入力値
  const [cityName, setCityName] = useState("");
  const [language, setLanguage] = useState("ja");
  const [simulationDays, setSimulationDays] = useState(30);

  /**
   * ウィンドウ初期化時に地形マップ一覧とシナリオ一覧を並列で取得する。
   * 地形マップはチュートリアルの開始と、詳細パネルの舞台の名前・大きさ(チュートリアルは地表・気候も)に使う。
   * いずれかの取得に失敗しても、成功した方は表示する。
   * シナリオ管理でシナリオが増減したら(scenarios:changed)シナリオ一覧だけ取り直す。
   */
  useEffect(() => {
    if (!apiBaseUrl) return;
    const base = apiBaseUrl.replace(/\/+$/, "");
    let active = true;
    const fetchTemplates = () => fetchApi<{ scenarios: Record<string, TemplateEntry> }>(`${base}/api/v1/scenarios`, { cache: "reload" })
      .then(data => { if (active) setTemplates(Object.values(data.scenarios)); });

    async function fetchData() {
      setLoading(true);
      const [terrainRes] = await Promise.allSettled([
        fetchApi<{ maps: TerrainMapEntry[] }>(`${base}/api/v1/terrain/maps`),
        fetchTemplates(),
      ]);
      if (!active) return;
      if (terrainRes.status === "fulfilled") setTerrainMaps(terrainRes.value.maps);
      setLoading(false);
    }

    void fetchData();
    const listener = listen("scenarios:changed", () => { void fetchTemplates().catch(() => {}); });
    return () => { active = false; void listener.then(unlisten => unlisten()); };
  }, [apiBaseUrl]);

  /**
   * テンプレート（シナリオ）での開始。
   * 設定確認モーダルを開き、テンプレートの既存値をデフォルトとしてセットする。
   */
  const handleStartTemplate = useCallback(
    async (template: TemplateEntry) => {
      const settings = await loadAppSettings();
      const defaultLang = resolveSupportedLanguage(
        template.language || settings.language || i18n.language
      );
      const defaultDays = template.simulation_days || 30;
      setCityName(template.city_name || "");
      setLanguage(defaultLang);
      setSimulationDays(defaultDays);
      setConfirmSelection({
        terrainId: null,
        templateId: template.id,
        defaultCityName: template.city_name || "",
        defaultLanguage: defaultLang,
        defaultSimulationDays: defaultDays,
        isTutorial: false,
        tutorialId: null,
      });
    },
    [i18n]
  );

  /**
   * チュートリアルミッションでの開始。
   * 教材の初期配置は特定の地形を前提にするため、シナリオが指定する地形マップ
   * (TutorialScenario.terrainMapId)をチュートリアルモードでロードする。
   * 地形マップが取得できていない場合はエラーメッセージを表示する。
   */
  const handleStartTutorial = useCallback(async (tutorialId: string) => {
    const scenario = TUTORIAL_SCENARIOS.find((s) => s.id === tutorialId);
    const terrainMap = terrainMaps.find((m) => m.id === scenario?.terrainMapId);
    if (!terrainMap) {
      setStatus({
        text: t("newGame.tutorialNoTerrain"),
        type: "error",
      });
      return;
    }
    const settings = await loadAppSettings();
    const defaultLang = resolveSupportedLanguage(settings.language || i18n.language);
    setCityName("");
    setLanguage(defaultLang);
    setSimulationDays(30);
    setConfirmSelection({
      terrainId: terrainMap.id,
      templateId: null,
      defaultCityName: "",
      defaultLanguage: defaultLang,
      defaultSimulationDays: 30,
      isTutorial: true,
      tutorialId,
    });
  }, [terrainMaps, i18n, t]);

  /**
   * 選択中の対象を詳細パネル用の表示データへ正規化する。
   * 選択が無い、または選択対象が一覧から消えた場合は null を返す。
   */
  const detail = useMemo<DetailView | null>(() => {
    if (!selection) return null;

    if (selection.kind === "tutorial") {
      const scenario = TUTORIAL_SCENARIOS.find((s) => s.id === selection.id);
      if (!scenario) return null;
      const map = terrainMaps.find((m) => m.id === scenario.terrainMapId);
      return {
        thumbnailUrl: scenario.image || null,
        template: null,
        terrainMapId: null,
        placeholderModifier: `new-game-detail-image--tutorial new-game-detail-image--${scenario.id}`,
        title: t(scenario.titleKey),
        description: t(scenario.descriptionKey),
        mapName: terrainMapTitle({ id: scenario.terrainMapId, name: map?.name ?? scenario.terrainMapId }, t),
        mapSize: formatMapSize(map?.settings, i18n.language),
        groundCover: map ? normalizeGroundCover(map.ground_cover) : null,
        climate: normalizeClimate(map?.climate),
        objective: t("newGame.tutorialObjective"),
        startLabel: t("newGame.startScenarioButton"),
        startDisabled: false,
        onStart: () => void handleStartTutorial(scenario.id),
      };
    }

    const template = templates.find((tmpl) => tmpl.id === selection.id);
    if (!template) return null;
    const terrainMap = template.terrain_map_id
      ? terrainMaps.find((m) => m.id === template.terrain_map_id)
      : undefined;
    return {
      thumbnailUrl: null,
      template,
      terrainMapId: template.terrain_map_id || null,
      placeholderModifier: "new-game-detail-image--template",
      title:
        template.scenario_title ||
        template.city_name ||
        `Template ${template.id.slice(0, 8)}`,
      description: template.scenario_description || t("newGame.detailNoDescription"),
      mapName: template.terrain_map_id
        ? terrainMapTitle({ id: template.terrain_map_id, name: terrainMap?.name || template.terrain_map_name || template.terrain_map_id }, t)
        : null,
      mapSize: formatMapSize(terrainMap?.settings, i18n.language),
      groundCover: template.ground_cover !== undefined || terrainMap
        ? normalizeGroundCover(template.ground_cover ?? terrainMap?.ground_cover)
        : null,
      climate: normalizeClimate(template.climate ?? terrainMap?.climate),
      objective: template.scenario_objective || null,
      startLabel: t("newGame.startScenarioButton"),
      startDisabled: template.compatibility?.loadable === false,
      onStart: () => void handleStartTemplate(template),
    };
  }, [
    selection,
    terrainMaps,
    templates,
    t,
    i18n.language,
    handleStartTemplate,
    handleStartTutorial,
  ]);

  /**
   * 設定確認モーダルで「開始」を押した時のハンドラ。
   * Rust 側の create_session を呼び出し、Unity 起動 → LoadingWindow 表示 →
   * バックエンド POST → ポーリング待機 → HUD 表示 のフローを実行する。
   * create_session は開いているポップアップをすべて閉じる(確認画面の注記で予告する)。
   */
  const handleConfirmStart = useCallback(async () => {
    if (!confirmSelection) return;

    setBusy(true);
    setStatus({ text: t("newGame.starting"), type: "info" });
    try {
      if (confirmSelection.templateId && !await prepareSaveLoad(apiBaseUrl, confirmSelection.templateId, true, t)) {
        setBusy(false); setStatus(null); return;
      }
      await invoke("create_session", {
        acceptBalanceChange: true,
        terrainMapId: confirmSelection.terrainId,
        templateId: confirmSelection.templateId,
        cityName: cityName || null,
        language,
        simulationDays,
        isTutorial: confirmSelection.isTutorial,
        tutorialId: confirmSelection.tutorialId || null,
      });
      onClose();
    } catch (error) {
      setStatus({
        text: saveError(error, t),
        type: "error",
      });
      setBusy(false);
    }
  }, [apiBaseUrl, t, onClose, confirmSelection, cityName, language, simulationDays]);

  /**
   * 設定確認モーダルを閉じてタイル選択画面に戻る
   */
  const handleCancelConfirm = useCallback(() => {
    setConfirmSelection(null);
    setStatus(null);
  }, []);

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

  // 設定確認モーダル表示中
  if (confirmSelection) {
    return (
      <div className="new-game-window">
        {tabs}
        <div className="new-game-body new-game-body--confirm">
          <div className="new-game-confirm">
            <h2 className="new-game-confirm-title">{t("newGame.confirmTitle")}</h2>

            {/* 都市名 */}
            <div className="new-game-confirm-field">
              <label className="new-game-confirm-label">
                {t("newGame.cityNameLabel")}
              </label>
              <input
                className="new-game-confirm-input"
                type="text"
                value={cityName}
                onChange={(e) => setCityName(e.target.value)}
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
                  onChange={(e) => setLanguage(e.target.value)}
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
            </div>

            {/* シミュレーション日数 */}
            <div className="new-game-confirm-field">
              <label className="new-game-confirm-label">
                {t("newGame.simulationDaysLabel")}
              </label>
              <div className="new-game-confirm-days">
                {SIMULATION_DAYS_OPTIONS.map((days) => (
                  <button
                    key={days}
                    className={`new-game-confirm-days-btn ${simulationDays === days ? "active" : ""}`}
                    onClick={() => setSimulationDays(days)}
                    disabled={busy}
                    type="button"
                  >
                    {t("newGame.daysOption", { days })}
                  </button>
                ))}
              </div>
              <span className="new-game-confirm-note">{t("newGame.simulationDaysNote")}</span>
            </div>

            {/* ボタン */}
            <p className="new-game-confirm-note new-game-confirm-warning">{t("newGame.closeWindowsNote")}</p>
            <div className="new-game-confirm-actions">
              <button
                className="new-game-confirm-btn new-game-confirm-btn--back"
                onClick={handleCancelConfirm}
                disabled={busy}
                type="button"
              >
                {t("newGame.backButton")}
              </button>
              <button
                className="new-game-confirm-btn new-game-confirm-btn--start"
                onClick={() => void handleConfirmStart()}
                disabled={busy}
                type="button"
              >
                {t("newGame.startButton")}
              </button>
            </div>
          </div>

          {status && (
            <div className={`new-game-status new-game-status--${status.type}`}>
              {status.text}
            </div>
          )}
        </div>
      </div>
    );
  }

  // タイル選択画面
  return (
    <div className="new-game-window">
      {tabs}
      <div className="new-game-body new-game-body--select">
        <div className="new-game-list">
          {loading ? (
            <div className="new-game-loading">{t("newGame.loading")}</div>
          ) : (
            <section className="new-game-section">
              <div className="new-game-tiles">
                {/* チュートリアルミッション: 常に表示、常に選択可能 */}
                {TUTORIAL_SCENARIOS.map((scenario) => (
                  <button
                    key={scenario.id}
                    className={`new-game-tile new-game-tile--tutorial ${
                      selection?.kind === "tutorial" && selection.id === scenario.id
                        ? "selected"
                        : ""
                    }`}
                    onClick={() => setSelection({ kind: "tutorial", id: scenario.id })}
                    disabled={busy}
                    type="button"
                    title={t(scenario.titleKey)}
                  >
                    <span className="new-game-tile-badge">{t("newGame.tutorialBadge")}</span>
                    {scenario.image ? (
                      <img className="new-game-tile-image" src={scenario.image} alt="" />
                    ) : (
                      <div className={`new-game-tile-image new-game-tile-image--tutorial new-game-tile-image--${scenario.id}`} />
                    )}
                    <span className="new-game-tile-name">{t(scenario.titleKey)}</span>
                  </button>
                ))}

                {/* 読み込めない作品も選択し、詳細で理由を確認できる。 */}
                {templates.map((tmpl) => (
                  <button
                    key={tmpl.id}
                    className={`new-game-tile ${
                      selection?.kind === "template" && selection.id === tmpl.id ? "selected" : ""
                    }`}
                    onClick={() => setSelection({ kind: "template", id: tmpl.id })}
                    disabled={busy}
                    type="button"
                    title={tmpl.scenario_title || tmpl.city_name || tmpl.id}
                  >
                    <ScenarioThumbnail
                      apiBaseUrl={apiBaseUrl}
                      templateId={tmpl.id} revision={tmpl.content_revision}
                      terrainMapId={tmpl.terrain_map_id}
                    />
                    <span className="new-game-tile-name">
                      {tmpl.scenario_title || tmpl.city_name || `Template ${tmpl.id.slice(0, 8)}`}
                    </span>
                    <ScenarioOriginBadges entry={tmpl} />
                  </button>
                ))}
              </div>

              {/* チュートリアル以外のシナリオがない場合の空状態メッセージ */}
              {templates.length === 0 && (
                <div className="new-game-empty">{t("newGame.noTemplates")}</div>
              )}
            </section>
          )}
        </div>

        {/* 詳細パネル（固定幅） */}
        <aside className="new-game-detail">
          {detail ? (
            <>
              {detail.thumbnailUrl ? (
                <img
                  className="new-game-detail-image"
                  src={detail.thumbnailUrl}
                  alt=""
                />
              ) : detail.template ? (
                <ScenarioThumbnail
                  apiBaseUrl={apiBaseUrl}
                  templateId={detail.template.id}
                  revision={detail.template.content_revision}
                  terrainMapId={detail.terrainMapId}
                  className="new-game-detail-image"
                  placeholderModifier={detail.placeholderModifier}
                />
              ) : (
                <div className={`new-game-detail-image ${detail.placeholderModifier}`} />
              )}

              <div className="new-game-detail-content">
                <h2 className="new-game-detail-title">{detail.title}</h2>
                {detail.template && <>
                  <ScenarioOriginBadges entry={detail.template} />
                  <SaveVersionInfo entry={detail.template} />
                </>}
                <p className="new-game-detail-desc">{detail.description}</p>

                {detail.mapName && (
                  <div className="new-game-detail-block">
                    <h3 className="new-game-detail-block-title">{t("newGame.detailMapLabel")}</h3>
                    <p className="new-game-detail-block-text">{detail.mapName}</p>
                  </div>
                )}

                <TerrainFacts mapSize={detail.mapSize} groundCover={detail.groundCover} climate={detail.climate} />

                {detail.objective && (
                  <div className="new-game-detail-block">
                    <h3 className="new-game-detail-block-title">
                      {t("newGame.detailObjectiveLabel")}
                    </h3>
                    <p className="new-game-detail-block-text">{detail.objective}</p>
                  </div>
                )}
              </div>

              <button
                className="new-game-detail-start"
                onClick={detail.onStart}
                disabled={busy || detail.startDisabled}
                type="button"
              >
                {detail.startLabel}
              </button>
            </>
          ) : (
            <div className="new-game-detail-placeholder">{t("newGame.detailPlaceholder")}</div>
          )}
        </aside>

        {status && (
          <div className={`new-game-status new-game-status--${status.type}`}>
            {status.text}
          </div>
        )}
      </div>
    </div>
  );
}
