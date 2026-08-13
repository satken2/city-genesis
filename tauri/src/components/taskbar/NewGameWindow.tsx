/**
 * ニューゲームウィンドウコンポーネント
 *
 * トップメニューの「ニューゲーム」ボタンから開かれるポップアップウィンドウ。
 * 左右2カラム構成で、左に選択肢の一覧、右に固定幅の詳細パネルを置く。
 *
 * 左カラム（一覧）は2つのセクションを縦に並べる:
 *
 * 1. 地形から始める … バックエンドの assets/terrain_maps.yaml に登録された地形マップ一覧を
 *    タイル表示する。チュートリアル未クリアでも選択可能。
 * 2. シナリオから始める … チュートリアルミッション（常に表示）と
 *    バックエンドのテンプレート一覧を並べて表示する。
 *    チュートリアルミッションは平坦な地形マップ（flat）をチュートリアルONでロードする。
 *
 * 一覧のタイルはサムネイルと名前だけを出す。説明・達成条件は右カラムに集約する。
 *
 * 右カラム（詳細パネル）はタイルをクリックして選択した対象の内容を表示する。
 * サムネイルを大きく出し、その下にタイトル・説明・舞台となるマップ・達成条件を並べる。
 * シナリオの説明文・達成条件はテンプレート作成者が記入するフリーテキストで、
 * セーブデータのメタデータ（GET /api/v1/saves?template=true）に含まれる。
 *
 * 詳細パネルの開始ボタンで「ゲーム設定の確認」画面に切り替わり、
 * 都市名・都市の言語・シミュレーション日数を設定してからゲームを開始する。
 */

import { useState, useEffect, useCallback, useMemo } from "react";
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
 *
 * scenario_* と terrain_map_* はシナリオ紹介用の項目。
 * これらの項目を持たない時期に保存されたテンプレートも読めるよう、すべて省略可能とする。
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
  scenario_title?: string;
  scenario_description?: string;
  scenario_objective?: string;
  terrain_map_id?: string;
  terrain_map_name?: string;
};

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
  | { kind: "terrain"; id: string }
  | { kind: "tutorial"; id: string }
  | { kind: "template"; id: string };

/**
 * 詳細パネルの描画に必要な項目へ正規化した表示データ。
 * 地形・チュートリアル・テンプレートで供給元が異なるため、パネル側は
 * この形だけを知っていればよいようにする。
 */
type DetailView = {
  /** サムネイルとして使う地形マップID。解決できない場合は null（プレースホルダー表示） */
  terrainMapId: string | null;
  /** サムネイルが無いときに使うプレースホルダーのCSS修飾子 */
  placeholderModifier: string;
  title: string;
  description: string;
  /** 舞台となるマップ名。地形選択時は自明なので null */
  mapName: string | null;
  /** マップサイズの表示文字列（例: 7000m × 6000m）。不明なら null */
  mapSize: string | null;
  /** 達成条件のフリーテキスト。未設定なら null */
  objective: string | null;
  /** 開始ボタンのラベル */
  startLabel: string;
  onStart: () => void;
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

/**
 * 地形マップ定義の settings から「幅m × 高さm」の表示文字列を組み立てる。
 * map_width / map_height はメートル単位の整数。欠けている場合は null を返す。
 */
function formatMapSize(settings: Record<string, unknown> | undefined): string | null {
  if (!settings) return null;
  const width = settings.map_width;
  const height = settings.map_height;
  if (typeof width !== "number" || typeof height !== "number") return null;
  return `${width.toLocaleString()}m × ${height.toLocaleString()}m`;
}

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

  // 詳細パネルに表示中の選択対象
  const [selection, setSelection] = useState<Selection | null>(null);

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
   * 地形での開始。設定確認モーダルを開き、デフォルト値をセットする。
   * チュートリアル未クリアでも選択可能。
   */
  const handleStartTerrain = useCallback(
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
   * テンプレート（シナリオ）での開始。
   * 設定確認モーダルを開き、テンプレートの既存値をデフォルトとしてセットする。
   */
  const handleStartTemplate = useCallback(
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
   * チュートリアルミッションでの開始。
   * チュートリアルは常に平坦な地形マップ(`flat`)をチュートリアルモードでロードする。
   * チュートリアルは道路・地区・建物の操作手順を学ぶためのものであり、
   * 地形の起伏に左右されず常に同じ初期状態から始められるよう、
   * assets/terrain_maps.yaml で定義された `flat` マップを明示的に選択する。
   * `flat` マップが取得できていない場合はエラーメッセージを表示する。
   */
  const handleStartTutorial = useCallback(async (tutorialId: string) => {
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
   * 選択中の対象を詳細パネル用の表示データへ正規化する。
   * 選択が無い、または選択対象が一覧から消えた場合は null を返す。
   */
  const detail = useMemo<DetailView | null>(() => {
    if (!selection) return null;

    if (selection.kind === "terrain") {
      const map = terrainMaps.find((m) => m.id === selection.id);
      if (!map) return null;
      return {
        terrainMapId: map.id,
        placeholderModifier: "new-game-detail-image--terrain",
        title: map.name,
        description: t("newGame.terrainDetailDesc"),
        mapName: null,
        mapSize: formatMapSize(map.settings),
        objective: null,
        startLabel: t("newGame.startTerrainButton"),
        onStart: () => void handleStartTerrain(map.id),
      };
    }

    if (selection.kind === "tutorial") {
      const scenario = TUTORIAL_SCENARIOS.find((s) => s.id === selection.id);
      if (!scenario) return null;
      const flatMap = terrainMaps.find((m) => m.id === "flat");
      return {
        // チュートリアルは専用の絵柄を持つため、地形サムネイルは使わない。
        terrainMapId: null,
        placeholderModifier: `new-game-detail-image--tutorial new-game-detail-image--${scenario.id}`,
        title: t(scenario.titleKey),
        description: t(scenario.descriptionKey),
        mapName: flatMap ? flatMap.name : null,
        mapSize: formatMapSize(flatMap?.settings),
        objective: t("newGame.tutorialObjective"),
        startLabel: t("newGame.startScenarioButton"),
        onStart: () => void handleStartTutorial(scenario.id),
      };
    }

    const template = templates.find((tmpl) => tmpl.id === selection.id);
    if (!template) return null;
    const terrainMap = template.terrain_map_id
      ? terrainMaps.find((m) => m.id === template.terrain_map_id)
      : undefined;
    return {
      terrainMapId: template.terrain_map_id || null,
      placeholderModifier: "new-game-detail-image--template",
      title:
        template.scenario_title ||
        template.city_name ||
        `Template ${template.id.slice(0, 8)}`,
      description: template.scenario_description || t("newGame.detailNoDescription"),
      mapName: terrainMap?.name || template.terrain_map_name || null,
      mapSize: formatMapSize(terrainMap?.settings),
      objective: template.scenario_objective || null,
      startLabel: t("newGame.startScenarioButton"),
      onStart: () => void handleStartTemplate(template),
    };
  }, [
    selection,
    terrainMaps,
    templates,
    t,
    handleStartTerrain,
    handleStartTemplate,
    handleStartTutorial,
  ]);

  /**
   * 設定確認モーダルで「開始」を押した時のハンドラ。
   * Rust 側の create_session を呼び出し、Unity 起動 → LoadingWindow 表示 →
   * バックエンド POST → ポーリング待機 → HUD 表示 のフローを実行する。
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
  }, [t, onClose, confirmSelection, cityName, language, simulationDays, isScenarioEditor]);

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
    <div className="new-game-body new-game-body--select">
      <div className="new-game-list">
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
                      className={`new-game-tile ${
                        selection?.kind === "terrain" && selection.id === map.id ? "selected" : ""
                      }`}
                      onClick={() => setSelection({ kind: "terrain", id: map.id })}
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
                      <div className={`new-game-tile-image new-game-tile-image--tutorial new-game-tile-image--${scenario.id}`} />
                      <span className="new-game-tile-name">{t(scenario.titleKey)}</span>
                    </button>
                  ))
                }

                {/* テンプレートシナリオ: 常に選択可能 */}
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
                    {tmpl.terrain_map_id ? (
                      <TerrainMapThumbnail apiBaseUrl={apiBaseUrl} mapId={tmpl.terrain_map_id} />
                    ) : (
                      <div className="new-game-tile-image new-game-tile-image--template" />
                    )}
                    <span className="new-game-tile-name">
                      {tmpl.scenario_title || tmpl.city_name || `Template ${tmpl.id.slice(0, 8)}`}
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
      </div>

      {/* 詳細パネル（固定幅） */}
      <aside className="new-game-detail">
        {detail ? (
          <>
            {detail.terrainMapId ? (
              <TerrainMapThumbnail
                apiBaseUrl={apiBaseUrl}
                mapId={detail.terrainMapId}
                className="new-game-detail-image"
                placeholderModifier={detail.placeholderModifier}
              />
            ) : (
              <div className={`new-game-detail-image ${detail.placeholderModifier}`} />
            )}

            <div className="new-game-detail-content">
              <h2 className="new-game-detail-title">{detail.title}</h2>
              <p className="new-game-detail-desc">{detail.description}</p>

              {detail.mapName && (
                <div className="new-game-detail-block">
                  <h3 className="new-game-detail-block-title">{t("newGame.detailMapLabel")}</h3>
                  <p className="new-game-detail-block-text">{detail.mapName}</p>
                </div>
              )}

              {detail.mapSize && (
                <div className="new-game-detail-block">
                  <h3 className="new-game-detail-block-title">{t("newGame.detailMapSizeLabel")}</h3>
                  <p className="new-game-detail-block-text">{detail.mapSize}</p>
                </div>
              )}

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
              disabled={busy}
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
  );
}
