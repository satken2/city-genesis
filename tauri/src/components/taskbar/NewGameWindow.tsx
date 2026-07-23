/**
 * ニューゲームウィンドウコンポーネント
 *
 * トップメニューの「ニューゲーム」ボタンから開かれるポップアップウィンドウ。
 * 以下の2つのセクションを縦に並べて表示する:
 *
 * 1. 地形から始める … バックエンドの assets/terrain_maps.yaml に登録された地形マップ一覧を
 *    タイル表示し、クリックで設定確認モーダルを表示する。
 *    チュートリアル未クリアでも選択可能。
 * 2. シナリオから始める … チュートリアルミッション（常に表示）と
 *    バックエンドのテンプレート一覧を並べて表示する。
 *    チュートリアルミッションは最初の地形マップ（与論島）をチュートリアルONでロードする。
 *    テンプレートシナリオはチュートリアルクリア後に選択可能になる。
 *
 * タイルクリック後は「ゲーム設定の確認」モーダルが開き、
 * 都市名・都市の言語・シミュレーション日数を設定してからゲームを開始する。
 */

import { useState, useEffect, useCallback } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { loadAppSettings } from "../../utils/settings";
import { TUTORIAL_SCENARIOS } from "../tutorial/tutorialSteps";
import { TerrainMapThumbnail } from "../common/TerrainMapThumbnail";
import "./NewGameWindow.css";

type NewGameWindowProps = {
  apiBaseUrl: string;
  onClose: () => void;
  isScenarioEditor?: boolean;
};

/**
 * 地形マップエントリ
 * GET /api/v1/terrain/maps の各要素の型
 */
type TerrainMapEntry = {
  id: string;
  name: string;
  settings: Record<string, unknown>;
};

/**
 * テンプレート（シナリオ）セーブデータのメタデータエントリ
 * GET /api/v1/saves?template=true の各要素の型
 */
type TemplateEntry = {
  id: string;
  city_name: string;
  population: number;
  current_date: string;
  epoch: number;
  simulation_days: number;
  saved_at: string;
  is_scenario: boolean;
  language: string;
};

/**
 * ステータスメッセージの型
 */
type StatusMessage = {
  text: string;
  type: "success" | "error" | "info";
};

/**
 * 設定確認モーダルの選択状態
 *
 * terrainId / templateId のどちらか一方がセットされ、
 * もう一方は null になる。
 * isTutorial が true の場合、チュートリアルモードでゲームを開始する。
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
const AVAILABLE_LANGUAGES = ["ja", "en"] as const;

/**
 * シミュレーション日数の選択肢
 */
const SIMULATION_DAYS_OPTIONS = [7, 30] as const;

export function NewGameWindow({
  apiBaseUrl,
  onClose,
  isScenarioEditor = false,
}: NewGameWindowProps) {
  const { t, i18n } = useTranslation();
  const [terrainMaps, setTerrainMaps] = useState<TerrainMapEntry[]>([]);
  const [templates, setTemplates] = useState<TemplateEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<StatusMessage | null>(null);

  // 設定確認モーダルの表示状態
  const [confirmSelection, setConfirmSelection] = useState<ConfirmSelection | null>(null);

  // 設定確認モーダル内の入力値
  const [cityName, setCityName] = useState("");
  const [language, setLanguage] = useState("ja");
  const [simulationDays, setSimulationDays] = useState(30);

  /**
   * ウィンドウ初期化時に地形マップ一覧とテンプレート一覧を並列で取得する。
   * いずれかの取得に失敗しても、成功した方は表示する。
   */
  useEffect(() => {
    if (!apiBaseUrl) return;
    const base = apiBaseUrl.replace(/\/+$/, "");

    async function fetchData() {
      setLoading(true);
      try {
        const [terrainRes, templateRes] = await Promise.all([
          fetch(`${base}/api/v1/terrain/maps`).catch(() => null),
          fetch(`${base}/api/v1/saves?template=true`).catch(() => null),
        ]);

        if (terrainRes?.ok) {
          const terrainData: { maps: TerrainMapEntry[] } = await terrainRes.json();
          if (terrainData.maps) {
            setTerrainMaps(terrainData.maps);
          }
        }

        if (templateRes?.ok) {
          const templateData: { success: boolean; saves: Record<string, TemplateEntry> } =
            await templateRes.json();
          if (templateData.success && templateData.saves) {
            setTemplates(Object.values(templateData.saves));
          }
        }
      } catch {
        /* 取得失敗時は空配列のまま */
      } finally {
        setLoading(false);
      }
    }

    void fetchData();
  }, [apiBaseUrl]);

  /**
   * 地形タイルクリック時のハンドラ。
   * 設定確認モーダルを開き、デフォルト値をセットする。
   * チュートリアル未クリアでも選択可能。
   */
  const handleSelectTerrain = useCallback(
    async (mapId: string) => {
      const settings = await loadAppSettings();
      setCityName("");
      setLanguage(settings.language || i18n.language);
      setSimulationDays(30);
      setConfirmSelection({
        terrainId: mapId,
        templateId: null,
        defaultCityName: "",
        defaultLanguage: settings.language || i18n.language,
        defaultSimulationDays: 30,
        isTutorial: false,
        tutorialId: null,
      });
    },
    [i18n]
  );

  /**
   * シナリオタイルクリック時のハンドラ（チュートリアル以外）。
   * 設定確認モーダルを開き、テンプレートの既存値をデフォルトとしてセットする。
   * チュートリアル未クリアの場合は呼ばれない（ボタンが disabled）。
   */
  const handleSelectTemplate = useCallback(
    async (template: TemplateEntry) => {
      const settings = await loadAppSettings();
      const defaultLang = template.language || settings.language || i18n.language;
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
   * チュートリアルミッションタイルクリック時のハンドラ。
   * チュートリアルは常に平坦な地形マップ(`flat`)をチュートリアルモードでロードする。
   * チュートリアルは道路・地区・建物の操作手順を学ぶためのものであり、
   * 地形の起伏に左右されず常に同じ初期状態から始められるよう、
   * assets/terrain_maps.yaml で定義された `flat` マップを明示的に選択する。
   * `flat` マップが取得できていない場合はエラーメッセージを表示する。
   */
  const handleSelectTutorial = useCallback(async (tutorialId: string) => {
    const terrainMap = terrainMaps.find((m) => m.id === "flat");
    if (!terrainMap) {
      setStatus({
        text: t("newGame.tutorialNoTerrain"),
        type: "error",
      });
      return;
    }
    const settings = await loadAppSettings();
    const defaultLang = settings.language || i18n.language;
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
   * 設定確認モーダルで「開始」を押した時のハンドラ。
   * Rust 側の create_session を呼び出し、Unity 起動 → LoadingWindow 表示 →
   * バックエンド POST → ポーリング待機 → Taskbar 表示 のフローを実行する。
   */
  const handleConfirmStart = useCallback(async () => {
    if (!confirmSelection) return;

    setBusy(true);
    setStatus({ text: t("newGame.starting"), type: "info" });
    try {
      await invoke("create_session", {
        terrainMapId: confirmSelection.terrainId,
        templateId: confirmSelection.templateId,
        cityName: cityName || null,
        language,
        simulationDays,
        isTutorial: confirmSelection.isTutorial,
        templateMode: isScenarioEditor,
        tutorialId: confirmSelection.tutorialId || null,
      });
      onClose();
    } catch (error) {
      setStatus({
        text: error instanceof Error ? error.message : t("newGame.startFailed"),
        type: "error",
      });
      setBusy(false);
    }
  }, [t, onClose, confirmSelection, cityName, language, simulationDays]);

  /**
   * 設定確認モーダルを閉じてタイル選択画面に戻る
   */
  const handleCancelConfirm = useCallback(() => {
    setConfirmSelection(null);
    setStatus(null);
  }, []);

  // 設定確認モーダル表示中
  if (confirmSelection) {
    return (
      <div className="new-game-body">
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
            <div className="new-game-confirm-language">
              {AVAILABLE_LANGUAGES.map((lang) => (
                <button
                  key={lang}
                  className={`new-game-confirm-lang-btn ${language === lang ? "active" : ""}`}
                  onClick={() => setLanguage(lang)}
                  disabled={busy}
                  type="button"
                >
                  {t(`settings.language.${lang}`)}
                </button>
              ))}
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
                  {t(`newGame.days${days}`)}
                </button>
              ))}
            </div>
            <span className="new-game-confirm-note">{t("newGame.simulationDaysNote")}</span>
          </div>

          {/* ボタン */}
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
    );
  }

  // タイル選択画面
  return (
    <div className="new-game-body">
      {loading ? (
        <div className="new-game-loading">{t("newGame.loading")}</div>
      ) : (
        <>
          {/* 地形から始めるセクション（チュートリアル未クリアでも選択可能） */}
          <section className="new-game-section">
            <h2 className="new-game-section-title">{t("newGame.fromTerrain")}</h2>
            {terrainMaps.length === 0 ? (
              <div className="new-game-empty">{t("newGame.noTerrainMaps")}</div>
            ) : (
              <div className="new-game-tiles">
                {terrainMaps.map((map) => (
                  <button
                    key={map.id}
                    className="new-game-tile"
                    onClick={() => void handleSelectTerrain(map.id)}
                    disabled={busy}
                    type="button"
                    title={map.name}
                  >
                    <TerrainMapThumbnail apiBaseUrl={apiBaseUrl} mapId={map.id} />
                    <span className="new-game-tile-name">{map.name}</span>
                  </button>
                ))}
              </div>
            )}
          </section>

          {/* シナリオセクション（チュートリアル + テンプレートを統合） */}
          <section className="new-game-section">
            <h2 className="new-game-section-title">{t("newGame.fromScenario")}</h2>
            <div className="new-game-tiles">
              {/* チュートリアルミッション: 常に表示、常に選択可能 */}
              {!isScenarioEditor &&
                TUTORIAL_SCENARIOS.map((scenario) => (
                  <button
                    key={scenario.id}
                    className="new-game-tile new-game-tile--tutorial"
                    onClick={() => void handleSelectTutorial(scenario.id)}
                    disabled={busy}
                    type="button"
                    title={t(scenario.titleKey)}
                  >
                    <span className="new-game-tile-badge">{t("newGame.tutorialBadge")}</span>
                    <div className={`new-game-tile-image new-game-tile-image--tutorial new-game-tile-image--${scenario.id}`} />
                    <span className="new-game-tile-name">{t(scenario.titleKey)}</span>
                    <span className="new-game-tile-desc">{t(scenario.descriptionKey)}</span>
                  </button>
                ))
              }

              {/* テンプレートシナリオ: 常に選択可能 */}
              {templates.map((tmpl) => (
                <button
                  key={tmpl.id}
                  className="new-game-tile"
                  onClick={() => void handleSelectTemplate(tmpl)}
                  disabled={busy}
                  type="button"
                  title={tmpl.city_name || tmpl.id}
                >
                  <div className="new-game-tile-image new-game-tile-image--template" />
                  <span className="new-game-tile-name">
                    {tmpl.city_name || `Template ${tmpl.id.slice(0, 8)}`}
                  </span>
                  <span className="new-game-tile-desc">
                    {t("saveList.population")}: {tmpl.population.toLocaleString()}
                  </span>
                </button>
              ))}
            </div>

            {/* テンプレートがない場合の空状態メッセージ */}
            {templates.length === 0 && (
              <div className="new-game-empty">{t("newGame.noTemplates")}</div>
            )}
          </section>
        </>
      )}

      {status && (
        <div className={`new-game-status new-game-status--${status.type}`}>
          {status.text}
        </div>
      )}
    </div>
  );
}