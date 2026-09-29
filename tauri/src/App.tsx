import { configureBackendRequests } from "./utils/api";
import { useEffect, useMemo, useState, lazy, Suspense, type MouseEvent as ReactMouseEvent } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { emit, listen } from "@tauri-apps/api/event";
import "./App.css";
import closeIcon from "./assets/images/ui/close.png";
import type { QueryState, QuestNotification, WindowContext, WindowDefinition } from "./types";
import { buildApiUrl } from "./utils/api";
import { loadAppSettings, type AppSettings } from "./utils/settings";
import { isSupportedLanguage } from "./utils/locale";

/**
 * ポップアップパネル群の遅延インポート定義
 *
 * これらのパネルはいずれも重い依存(recharts, three.js など)を含み、かつ同時に1つしか
 * 描画されないため、初期バンドルから切り離して個別チャンクに分割する。
 * これにより新規ウィンドウ起動時にロードするJS量が大幅に減少し、WebView2の初回起動が
 * 数十ms〜数百ms高速化される。チャンクはディスクキャッシュから即座に読み込まれるため、
 * パネル表示までの体感遅延は無視できる。
 */
const AgentDetailPanel = lazy(() => import("./components/agent/AgentDetailPanel").then(m => ({ default: m.AgentDetailPanel })));
const CitizenDetailPanel = lazy(() => import("./components/citizen/CitizenDetailPanel").then(m => ({ default: m.CitizenDetailPanel })));
const DiagnosisPanel = lazy(() => import("./components/diagnosis/DiagnosisPanel").then(m => ({ default: m.DiagnosisPanel })));
const ParcelDetailPanel = lazy(() => import("./components/parcel/ParcelDetailPanel").then(m => ({ default: m.ParcelDetailPanel })));
const ZoneDetailPanel = lazy(() => import("./components/zone/ZoneDetailPanel").then(m => ({ default: m.ZoneDetailPanel })));
const IntersectionDetailPanel = lazy(() => import("./components/intersection/IntersectionDetailPanel").then(m => ({ default: m.IntersectionDetailPanel })));
const ZoneMergeConfirmWindow = lazy(() => import("./components/zone/ZoneMergeConfirmWindow").then(m => ({ default: m.ZoneMergeConfirmWindow })));
const ConfirmDialogWindow = lazy(() => import("./components/common/ConfirmDialogWindow").then(m => ({ default: m.ConfirmDialogWindow })));
const BuildingDirectAcquisitionConfirmWindow = lazy(() => import("./components/building/BuildingDirectAcquisitionConfirmWindow").then(m => ({ default: m.BuildingDirectAcquisitionConfirmWindow })));
const PathConstructionPanel = lazy(() => import("./components/path/PathConstructionPanel").then(m => ({ default: m.PathConstructionPanel })));
const PathShapeEditorPanel = lazy(() => import("./components/path/PathShapeEditorPanel").then(m => ({ default: m.PathShapeEditorPanel })));
const PathDetailPanel = lazy(() => import("./components/path/PathDetailPanel").then(m => ({ default: m.PathDetailPanel })));
const BuildingConstructionPanel = lazy(() => import("./components/building/BuildingConstructionPanel").then(m => ({ default: m.BuildingConstructionPanel })));
const MashupEditPanel = lazy(() => import("./components/building/MashupEditPanel").then(m => ({ default: m.MashupEditPanel })));
const ParcelCreationPanel = lazy(() => import("./components/parcel/ParcelCreationPanel").then(m => ({ default: m.ParcelCreationPanel })));
const CityBudgetWindow = lazy(() => import("./components/budget/CityBudgetWindow").then(m => ({ default: m.CityBudgetWindow })));
const PublicFacilitiesWindow = lazy(() => import("./components/budget/PublicFacilitiesWindow").then(m => ({ default: m.PublicFacilitiesWindow })));
const TransitManagementWindow = lazy(() => import("./components/transit/TransitManagementWindow").then(m => ({ default: m.TransitManagementWindow })));
const TransitCreationPanel = lazy(() => import("./components/transit/TransitCreationPanel").then(m => ({ default: m.TransitCreationPanel })));
const TransitDetailPanel = lazy(() => import("./components/transit/TransitDetailPanel").then(m => ({ default: m.TransitDetailPanel })));
const RoadDetailPanel = lazy(() => import("./components/road/RoadDetailPanel").then(m => ({ default: m.RoadDetailPanel })));
const SettingsWindow = lazy(() => import("./components/taskbar/SettingsWindow").then(m => ({ default: m.SettingsWindow })));
const CreditsWindow = lazy(() => import("./components/taskbar/CreditsWindow").then(m => ({ default: m.CreditsWindow })));
const SaveLoadWindow = lazy(() => import("./components/taskbar/SaveLoadWindow").then(m => ({ default: m.SaveLoadWindow })));
const ScenarioManagerWindow = lazy(() => import("./components/template/ScenarioManagerWindow").then(m => ({ default: m.ScenarioManagerWindow })));
const ScenarioMetadataWindow = lazy(() => import("./components/template/ScenarioMetadataWindow").then(m => ({ default: m.ScenarioMetadataWindow })));
const SaveListWindow = lazy(() => import("./components/taskbar/SaveListWindow").then(m => ({ default: m.SaveListWindow })));
const NewGameWindow = lazy(() => import("./components/taskbar/NewGameWindow").then(m => ({ default: m.NewGameWindow })));
const CityRenameWindow = lazy(() => import("./components/taskbar/CityRenameWindow").then(m => ({ default: m.CityRenameWindow })));
const ResourceDetailPanel = lazy(() => import("./components/resource/ResourceDetailPanel").then(m => ({ default: m.ResourceDetailPanel })));
const ResourceConstructionPanel = lazy(() => import("./components/resource/ResourceConstructionPanel").then(m => ({ default: m.ResourceConstructionPanel })));
const PropPlacementPanel = lazy(() => import("./components/prop/PropPlacementPanel").then(m => ({ default: m.PropPlacementPanel })));
const GradingPanel = lazy(() => import("./components/grading/GradingPanel").then(m => ({ default: m.GradingPanel })));
const AchievementsWindow = lazy(() => import("./components/achievement/AchievementsWindow").then(m => ({ default: m.AchievementsWindow })));
const QuestEditPanel = lazy(() => import("./components/achievement/QuestEditPanel").then(m => ({ default: m.QuestEditPanel })));
const CityTaxWindow = lazy(() => import("./components/tax/CityTaxWindow").then(m => ({ default: m.CityTaxWindow })));
const CityRegulationWindow = lazy(() => import("./components/regulation/CityRegulationWindow").then(m => ({ default: m.CityRegulationWindow })));
const CityStatisticsWindow = lazy(() => import("./components/statistics/CityStatisticsWindow").then(m => ({ default: m.CityStatisticsWindow })));
const CityFinancialPolicyWindow = lazy(() => import("./components/statistics/CityFinancialPolicyWindow").then(m => ({ default: m.CityFinancialPolicyWindow })));
const DisplayOptionsPanel = lazy(() => import("./components/display/DisplayOptionsPanel").then(m => ({ default: m.DisplayOptionsPanel })));
const EpochConfirmWindow = lazy(() => import("./components/taskbar/EpochConfirmWindow").then(m => ({ default: m.EpochConfirmWindow })));
const CityNotificationsWindow = lazy(() => import("./components/notification/CityNotificationsWindow").then(m => ({ default: m.CityNotificationsWindow })));
const ConstructionProjectsWindow = lazy(() => import("./components/construction/ConstructionProjectsWindow").then(m => ({ default: m.ConstructionProjectsWindow })));
const EpochResultWindow = lazy(() => import("./components/taskbar/EpochResultWindow").then(m => ({ default: m.EpochResultWindow })));
const QuestCompleteWindow = lazy(() => import("./components/taskbar/QuestCompleteWindow").then(m => ({ default: m.QuestCompleteWindow })));
const TutorialIntroWindow = lazy(() => import("./components/tutorial/TutorialIntroWindow").then(m => ({ default: m.TutorialIntroWindow })));
const TerrainEditorCanvas = lazy(() => import("./components/terrain/TerrainEditorCanvas").then(m => ({ default: m.TerrainEditorCanvas })));
const Terrain3DPreviewCanvas = lazy(() => import("./components/terrain/Terrain3DPreviewCanvas").then(m => ({ default: m.Terrain3DPreviewCanvas })));
const CityMapWindow = lazy(() => import("./components/map/CityMapWindow").then(m => ({ default: m.CityMapWindow })));
const GodEditPanel = lazy(() => import("./components/debug/GodEditPanel").then(m => ({ default: m.GodEditPanel })));
const StreetGridWindow = lazy(() => import("./components/template/StreetGridWindow").then(m => ({ default: m.StreetGridWindow })));
const AgentCreationPanel = lazy(() => import("./components/template/AgentCreationPanel").then(m => ({ default: m.AgentCreationPanel })));
// EpochReport は型のみ参照するため、実体をバンドルに含めない type-only import で取得する
import type { EpochReport } from "./components/taskbar/EpochResultWindow";
import type { PendingZoneMerge } from "./components/zone/ZoneMergeConfirmWindow";
import type { PendingDirectAcquisition } from "./components/building/BuildingDirectAcquisitionConfirmWindow";
import { emitConfirmDialogResult, type ConfirmDialogRequest } from "./utils/confirmDialog";

import { IconPanel } from "./components/hud/IconPanel";
import { Cockpit } from "./components/hud/Cockpit";
import { SystemTray } from "./components/hud/SystemTray";
import { DisplayTray } from "./components/hud/DisplayTray";
import { TopMenu } from "./components/topmenu/TopMenu";
import { ParameterSetupWindow } from "./components/parameter-setup/ParameterSetupWindow";
import { NotificationWindow } from "./components/notification/NotificationWindow";
import { TipsWindow } from "./components/tips/TipsWindow";
import { Hintbar } from "./components/hintbar/Hintbar";
import { Tutorial } from "./components/tutorial/Tutorial";
import { TutorialLayer } from "./tutorial/TutorialLayer";
import { MasterDataProvider } from "./contexts/MasterDataContext";

/**
 * 専用パネルが実装されているウィンドウタイプのセット
 * これらのタイプはジェネリックな JSON 表示ではなく、専用コンポーネントでレンダリングする
 */
const SPECIALIZED_PANEL_TYPES = new Set(["agent_detail", "diagnosis", "parcel_detail", "zone_detail", "intersection_detail", "zone_merge", "building_direct_acquisition", "confirm_dialog", "path_build", "path_shape_editor", "path_detail", "city_budget", "public_facilities", "transit_management", "transit_creation", "transit_detail", "road_detail", "settings", "credits", "citizen_detail", "top_page", "building_construction", "mashup_edit", "parcel_creation", "save_load", "save_list", "scenario_manager", "scenario_metadata", "new_game", "city_rename", "resource_detail", "resource_edit", "prop_placement", "grading", "achievements", "city_hall", "city_regulation", "city_statistics", "financial_policy", "display_options", "epoch_confirm", "city_notifications", "construction_projects", "epoch_result", "quest_complete", "tutorial_intro", "terrain_editor", "terrain_3d_preview", "god_edit", "quest_edit", "city_map", "street_grid", "agent_creation"]);


function App() {
  const { t, i18n } = useTranslation();
  const appWindow = useMemo(() => getCurrentWindow(), []);
  const [context, setContext] = useState<WindowContext | null>(null);
  const [apiBaseUrl, setApiBaseUrl] = useState<string>("");
  const [openWindowCount, setOpenWindowCount] = useState<number>(0);
  const [queryState, setQueryState] = useState<QueryState>({ status: "idle" });
  const [actionState, setActionState] = useState<string>("");
  const [windowKind, setWindowKind] = useState<"host" | "icon-panel" | "cockpit" | "system-tray" | "display-tray" | "topmenu" | "popup" | "notification" | "hintbar" | "parameter-setup" | "tutorial" | "tips">("host");

  useEffect(() => {
    let disposed = false;
    const initialize = async () => {
      const label = appWindow.label;
      if (label === "icon-panel") {
        setWindowKind("icon-panel");
      } else if (label === "cockpit") {
        setWindowKind("cockpit");
      } else if (label === "system-tray") {
        setWindowKind("system-tray");
      } else if (label === "display-tray") {
        setWindowKind("display-tray");
      } else if (label === "topmenu") {
        setWindowKind("topmenu");

      } else if (label === "notification") {
        setWindowKind("notification");
      } else if (label === "hintbar") {
        setWindowKind("hintbar");
      } else if (label === "tutorial") {
        setWindowKind("tutorial");
      } else if (label === "parameter-setup") {
        setWindowKind("parameter-setup");
      } else if (label === "tips") {
        console.log("[App] tips window detected, setting windowKind");
        setWindowKind("tips");
      } else if (label !== "main") {
        setWindowKind("popup");
      }
      const status = await invoke<{ api_base_url: string; open_window_count: number }>("get_connection_status");
      if (!disposed) {
        setApiBaseUrl(status.api_base_url);
        setOpenWindowCount(status.open_window_count);
      }

      // window-registry のポップアップ以外(HUD各枚・トップメニュー・通知・Tips・ヒントバー・
      // チュートリアル・初期設定)は Rust 側が直接生成するウィンドウで、context を持たない
      const contextlessLabels = [
        "main", "icon-panel", "cockpit", "system-tray", "display-tray", "topmenu",
        "notification", "tips", "hintbar", "tutorial", "parameter-setup",
      ];
      if (!contextlessLabels.includes(label)) {
        const initialContext = await invoke<WindowContext>("get_window_context");
        if (!disposed) {
          setContext(initialContext);
        }
      }

      // 設定ファイルから言語を復元する
      try {
        const settings = await loadAppSettings();
        if (isSupportedLanguage(settings.language)) {
          await i18n.changeLanguage(settings.language);
        }
      } catch {
        // 設定ファイル読み込み失敗時はデフォルト言語を使用する
      }
    };

    initialize().catch((error) => {
      console.error(error);
    });

    let unlistenWindow: (() => void) | undefined;

    appWindow
      .listen<WindowContext>("window-context-updated", (event) => {
        setContext(event.payload);
      })
      .then((fn) => {
        unlistenWindow = fn;
      })
      .catch(console.error);

    return () => {
      disposed = true;
      unlistenWindow?.();
    };
  }, [appWindow, i18n]);

  /**
   * 設定ウィンドウでの言語変更を、開いている全ウィンドウへ即時反映する。
   *
   * i18n インスタンスはウィンドウ(WebView)ごとに独立しているため、設定ウィンドウで
   * changeLanguage しても他ウィンドウには伝播しない。設定保存時に全ウィンドウへ
   * ブロードキャストされる `settings-updated` を各ウィンドウで受け直すことで、
   * 常駐したまま再マウントされないHUD3枚・トップメニューにも反映される
   * (これがないと次回起動まで旧言語のままになる)。
   */
  useEffect(() => {
    const unlisten = listen<AppSettings>("settings-updated", (event) => {
      const language = event.payload?.language;
      if (isSupportedLanguage(language) && language !== i18n.language) {
        void i18n.changeLanguage(language);
      }
    });

    return () => {
      void unlisten.then((fn) => fn());
    };
  }, [i18n]);

  useEffect(() => {
    if (!context) {
      return;
    }

    if (context.definition.empty_shell) {
      setQueryState({ status: "idle" });
      return;
    }

    const fetchConfig = context.definition.fetch;
    if (!fetchConfig) {
      setQueryState({ status: "idle" });
      return;
    }

    // 専用パネルがデータ取得を独自に行う場合は、ジェネリックな取得処理をスキップする
    if (SPECIALIZED_PANEL_TYPES.has(context.window_type)) {
      setQueryState({ status: "idle" });
      return;
    }

    const url = buildApiUrl(context.api_base_url, fetchConfig.path_template, context.entity?.id);
    const controller = new AbortController();
    setQueryState({ status: "loading" });

    fetch(url, { method: fetchConfig.method, signal: controller.signal })
      .then(async (response) => {
        if (!response.ok) {
          throw new Error(`HTTP ${response.status}`);
        }
        const data = await response.json();
        setQueryState({ status: "success", url, data });
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) {
          return;
        }
        const message = error instanceof Error ? error.message : "Unknown error";
        setQueryState({ status: "error", url, message });
      });

    return () => {
      controller.abort();
    };
  }, [context]);

  async function closeWindow() {
    try {
      if (context) {
        await emit("bgm:release", { id: context.window_type });
        // 確認ウィンドウを×で閉じたら取消として返す(確定・取消ボタン経由なら回答済みで、呼び出し側が重複を無視する)
        const confirmRequest = windowExtra(context)?.confirmRequest as ConfirmDialogRequest | undefined;
        if (context.window_type === "confirm_dialog" && confirmRequest) {
          await emitConfirmDialogResult(confirmRequest.requestId, false);
        }
      }
      await invoke("close_current_window");
    } catch {
      // ウィンドウが既に閉じている場合やクローズに失敗した場合は無視する
    }
  }

  async function startWindowDrag(event: ReactMouseEvent<HTMLElement>) {
    if (event.button !== 0) {
      return;
    }

    try {
      await appWindow.startDragging();
    } catch (error) {
      console.error("failed to start window drag", error);
    }
  }

  async function sendAction(action: string) {
    if (!context) {
      return;
    }

    setActionState("sending");
    try {
      await invoke("send_window_action", {
        windowType: context.window_type,
        entity: context.entity ?? null,
        action,
        params: {
          source_window: context.label,
          timestamp: new Date().toISOString(),
        },
      });
      setActionState(`sent:${action}`);
    } catch (error) {
      setActionState(`error:${String(error)}`);
    }
  }

  configureBackendRequests(apiBaseUrl);

  return (
    <MasterDataProvider apiBaseUrl={apiBaseUrl}>
      <TutorialLayer windowType={context?.window_type ?? windowKind} entity={context?.entity?.id} />
      {windowKind === "topmenu" ? (
        <TopMenu apiBaseUrl={apiBaseUrl} />
      ) : windowKind === "icon-panel" ? (
        <IconPanel />
      ) : windowKind === "cockpit" ? (
        <Cockpit />
      ) : windowKind === "system-tray" ? (
        <SystemTray />
      ) : windowKind === "display-tray" ? (
        <DisplayTray />
      ) : windowKind === "hintbar" ? (
        <Hintbar />
      ) : windowKind === "tutorial" ? (
        <Tutorial />

      ) : windowKind === "parameter-setup" ? (
        <ParameterSetupWindow apiBaseUrl={apiBaseUrl} />
      ) : windowKind === "tips" ? (
        <TipsWindow mode={new URLSearchParams(window.location.search).get("tipsMode") === "epoch" ? "epoch" : "initial"} />
      ) : windowKind === "notification" ? (
        <NotificationWindow />
      ) : windowKind === "popup" && context ? (
        <main className={context.definition.chromeless ? "popup-shell popup-shell--chromeless" : "popup-shell"}>
          {!context.definition.chromeless && <header className="titlebar">
            <div
              className="titlebar-drag-region"
              onMouseDown={(event) => {
                void startWindowDrag(event);
              }}
              role="presentation"
            >
              <div className="titlebar-text">
                <span className="titlebar-title">
                  {t((windowExtra(context)?.titleKey as string | undefined) ?? `windowType.${context.window_type}`, {
                    defaultValue: context.title,
                  })}
                </span>
              </div>
            </div>
            <div className="titlebar-actions">
              <button className="titlebar-close-button" onClick={closeWindow} type="button" aria-label={t("common.close")}>
                <img src={closeIcon} alt="Close" />
              </button>
            </div>
          </header>}

          <section
            className={
              context.definition.html_path
                ? "content-iframe"
                : context.definition.empty_shell
                  ? "content content-empty-shell"
                  : "content"
            }
          >
            {/* open_seq を含めることで、非表示→再表示で再利用されたウィンドウでも
                パネルが必ず再マウントされ、前回の useState(選択状態やマウント時フェッチの
                結果)を引きずらない。ウィンドウ再利用の詳細は src-tauri/AGENTS.md 参照 */}
            <Suspense
              key={`${context.window_type}:${context.entity?.id ?? ""}:${context.label}:${context.open_seq}`}
              fallback={null}
            >
            {context.definition.html_path ? (
              <HtmlPanel context={context} />
            ) : context.window_type === "agent_detail" ? (
              <AgentDetailPanel
                apiBaseUrl={context.api_base_url}
                pathTemplate={context.definition.fetch!.path_template}
                initialEntityId={context.entity?.id ?? ""}
              />
            ) : context.window_type === "diagnosis" ? (
              <DiagnosisPanel
                apiBaseUrl={context.api_base_url}
                pathTemplate={context.definition.fetch!.path_template}
                initialEntityId={context.entity?.id ?? ""}
              />
            ) : context.window_type === "citizen_detail" ? (
              <CitizenDetailPanel
                apiBaseUrl={context.api_base_url}
                pathTemplate={context.definition.fetch!.path_template}
                initialEntityId={context.entity?.id ?? ""}
              />
            ) : context.window_type === "parcel_detail" ? (
              <ParcelDetailPanel
                apiBaseUrl={context.api_base_url}
                pathTemplate={context.definition.fetch!.path_template}
                initialEntityId={context.entity?.id ?? ""}
              />
            ) : context.window_type === "zone_detail" ? (
              <ZoneDetailPanel
                apiBaseUrl={context.api_base_url}
                pathTemplate={context.definition.fetch!.path_template}
                initialEntityId={context.entity?.id ?? ""}
              />
            ) : context.window_type === "intersection_detail" ? (
              <IntersectionDetailPanel
                apiBaseUrl={context.api_base_url}
                pathTemplate={context.definition.fetch!.path_template}
                initialEntityId={context.entity?.id ?? ""}
              />
            ) : context.window_type === "zone_merge" ? (
              <ZoneMergeConfirmWindow
                apiBaseUrl={context.api_base_url}
                pendingMerge={
                  (context.params as Record<string, unknown> | null)?.extra
                    ? ((context.params as Record<string, unknown>).extra as Record<string, unknown>).pendingMerge as PendingZoneMerge | undefined
                    : undefined
                }
                onClose={closeWindow}
              />
            ) : context.window_type === "confirm_dialog" ? (
              <ConfirmDialogWindow
                request={windowExtra(context)?.confirmRequest as ConfirmDialogRequest | undefined}
                onClose={closeWindow}
              />
            ) : context.window_type === "building_direct_acquisition" ? (
              <BuildingDirectAcquisitionConfirmWindow
                pendingAcquisition={
                  (context.params as Record<string, unknown> | null)?.extra
                    ? ((context.params as Record<string, unknown>).extra as Record<string, unknown>).pendingAcquisition as PendingDirectAcquisition | undefined
                    : undefined
                }
                onClose={closeWindow}
              />
            ) : context.window_type === "path_detail" ? (
              <PathDetailPanel
                apiBaseUrl={context.api_base_url}
                pathTemplate={context.definition.fetch!.path_template}
                initialEntityId={context.entity?.id ?? ""}
              />
            ) : context.window_type === "path_build" ? (
              <PathConstructionPanel
                apiBaseUrl={context.api_base_url}
                onClose={closeWindow}
                editPathId={
                  (context.params as Record<string, unknown> | null)?.extra
                    ? ((context.params as Record<string, unknown>).extra as Record<string, unknown>).editPathId as number | undefined
                    : undefined
                }
              />
            ) : context.window_type === "path_shape_editor" ? (
              // 敷設中パスの2D形状編集。パス建設パネル(path_build)と並行して開く
              <PathShapeEditorPanel apiBaseUrl={context.api_base_url} />
            ) : context.window_type === "resource_edit" ? (
              <ResourceConstructionPanel
                apiBaseUrl={context.api_base_url}
                onClose={closeWindow}
              />
            ) : context.window_type === "prop_placement" ? (
              <PropPlacementPanel
                apiBaseUrl={context.api_base_url}
                onClose={closeWindow}
              />
            ) : context.window_type === "grading" ? (
              <GradingPanel
                apiBaseUrl={context.api_base_url}
                onClose={closeWindow}
              />
            ) : context.window_type === "building_construction" ? (
              <BuildingConstructionPanel
                apiBaseUrl={context.api_base_url}
                onClose={closeWindow}
                editAgentId={windowExtra(context)?.editAgentId as number | undefined}
                relocateCityHall={windowExtra(context)?.relocateCityHall === true}
              />
            ) : context.window_type === "mashup_edit" ? (
              <MashupEditPanel
                apiBaseUrl={context.api_base_url}
                initialEntityId={context.entity?.id ?? ""}
                onClose={closeWindow}
              />
            ) : context.window_type === "parcel_creation" ? (
              <ParcelCreationPanel apiBaseUrl={context.api_base_url} />
            ) : context.window_type === "city_budget" ? (
              <CityBudgetWindow apiBaseUrl={context.api_base_url} />
            ) : context.window_type === "public_facilities" ? (
              <PublicFacilitiesWindow apiBaseUrl={context.api_base_url} />
            ) : context.window_type === "transit_management" ? (
              <TransitManagementWindow apiBaseUrl={context.api_base_url} />
            ) : context.window_type === "transit_creation" ? (
              // editLineId があれば既存路線の経路編集。路線を切り替えて開き直したとき状態が残らないよう key で作り直す
              <TransitCreationPanel
                key={String(windowExtra(context)?.editLineId ?? "new")}
                apiBaseUrl={context.api_base_url}
                onClose={closeWindow}
                editLineId={windowExtra(context)?.editLineId as number | undefined}
              />
            ) : context.window_type === "transit_detail" ? (
              <TransitDetailPanel
                apiBaseUrl={context.api_base_url}
                pathTemplate={context.definition.fetch!.path_template}
                initialEntityId={context.entity?.id ?? ""}
              />
            ) : context.window_type === "road_detail" ? (
              <RoadDetailPanel
                apiBaseUrl={context.api_base_url}
                initialEntityId={context.entity?.id ?? ""}
                offerTrace={windowExtra(context)?.offerTrace === true}
                seedPathId={windowExtra(context)?.seedPathId as number | undefined}
              />
            ) : context.window_type === "settings" ? (
              <SettingsWindow apiBaseUrl={context.api_base_url} />
            ) : context.window_type === "credits" ? (
              <CreditsWindow />
            ) : context.window_type === "top_page" ? (
              <TopMenu apiBaseUrl={apiBaseUrl} />
            ) : context.window_type === "save_load" ? (
              <SaveLoadWindow apiBaseUrl={context.api_base_url} onClose={closeWindow} />
            ) : context.window_type === "scenario_manager" ? (
              <ScenarioManagerWindow apiBaseUrl={context.api_base_url} />
            ) : context.window_type === "scenario_metadata" ? (
              <ScenarioMetadataWindow key={windowExtra(context)?.create === true ? "create" : String(windowExtra(context)?.scenarioId ?? "")}
                apiBaseUrl={context.api_base_url} scenarioId={String(windowExtra(context)?.scenarioId ?? "")}
                create={windowExtra(context)?.create === true} onClose={closeWindow} />
            ) : context.window_type === "save_list" ? (
              <SaveListWindow apiBaseUrl={context.api_base_url} onClose={closeWindow} />
            ) : context.window_type === "new_game" ? (
              <NewGameWindow apiBaseUrl={context.api_base_url} onClose={closeWindow} />
            ) : context.window_type === "city_rename" ? (
              <CityRenameWindow apiBaseUrl={context.api_base_url} onClose={closeWindow} />
            ) : context.window_type === "terrain_editor" ? (
              <TerrainEditorCanvas mapId={context.entity?.id ?? ""} />

            ) : context.window_type === "terrain_3d_preview" ? (
              <Terrain3DPreviewCanvas mapId={context.entity?.id ?? ""} />

            ) : context.window_type === "resource_detail" ? (
              <ResourceDetailPanel
                apiBaseUrl={context.api_base_url}
                pathTemplate={context.definition.fetch!.path_template}
                initialEntityId={context.entity?.id ?? ""}
              />
            ) : context.window_type === "achievements" ? (
              <AchievementsWindow apiBaseUrl={context.api_base_url} />
            ) : context.window_type === "city_hall" ? (
              <CityTaxWindow apiBaseUrl={context.api_base_url} onClose={closeWindow} />
            ) : context.window_type === "city_regulation" ? (
              <CityRegulationWindow apiBaseUrl={context.api_base_url} onClose={closeWindow} />
            ) : context.window_type === "city_statistics" ? (
              <CityStatisticsWindow apiBaseUrl={context.api_base_url} />
            ) : context.window_type === "financial_policy" ? (
              <CityFinancialPolicyWindow apiBaseUrl={context.api_base_url} />
            ) : context.window_type === "display_options" ? (
              <DisplayOptionsPanel apiBaseUrl={context.api_base_url} />
            ) : context.window_type === "epoch_confirm" ? (
              <EpochConfirmWindow apiBaseUrl={context.api_base_url} onClose={closeWindow} />
            ) : context.window_type === "city_notifications" ? (
              <CityNotificationsWindow apiBaseUrl={context.api_base_url} />
            ) : context.window_type === "construction_projects" ? (
              <ConstructionProjectsWindow apiBaseUrl={context.api_base_url} />
            ) : context.window_type === "epoch_result" ? (
              (() => {
                // エポック実行結果は epoch_confirm ウィンドウから params.extra 経由で渡される。
                const extra = ((context.params as Record<string, unknown> | null)?.extra ?? {}) as {
                  report?: EpochReport;
                  epochCount?: number;
                  currentDate?: number;
                  completedQuests?: QuestNotification[];
                  failedQuests?: QuestNotification[];
                };
                return (
                  <EpochResultWindow
                    report={extra.report as EpochReport}
                    epochCount={extra.epochCount ?? 1}
                    currentDate={extra.currentDate ?? 0}
                    apiBaseUrl={context.api_base_url}
                    completedQuests={extra.completedQuests ?? []}
                    failedQuests={extra.failedQuests ?? []}
                    onClose={async () => {
                      // 結果を閉じる際、notify="modal" の達成クエストがあればお祝いウィンドウを開く。
                      // (結果で因果を確認してからお祝い、の連続演出)
                      const quests = extra.completedQuests ?? [];
                      if (quests.some((q) => q.notify === "modal")) {
                        await invoke("open_registered_window", {
                          windowType: "quest_complete",
                          params: { extra: { quests, openedAt: Date.now() } },
                        });
                      }
                      await closeWindow();
                    }}
                  />
                );
              })()
            ) : context.window_type === "quest_complete" ? (
              <QuestCompleteWindow
                quests={
                  (((context.params as Record<string, unknown> | null)?.extra as
                    | { quests?: QuestNotification[] }
                    | undefined)?.quests) ?? []
                }
                openedAt={Number(windowExtra(context)?.openedAt) || undefined}
                onClose={closeWindow}
              />
            ) : context.window_type === "tutorial_intro" ? (
              <TutorialIntroWindow
                tutorialId={String(windowExtra(context)?.tutorialId ?? "")}
                onStart={closeWindow}
              />
            ) : context.window_type === "city_map" ? (
              <CityMapWindow apiBaseUrl={context.api_base_url} />
            ) : context.window_type === "street_grid" ? (
              // テンプレートモード専用。2D地図で範囲を指定して格子状の路地を計画中で作る
              <StreetGridWindow apiBaseUrl={context.api_base_url} />
            ) : context.window_type === "agent_creation" ? (
              // テンプレートモード専用。3Dビューで選んだ共用建物へ建物ではない経済主体を入居済みで作る
              <AgentCreationPanel apiBaseUrl={context.api_base_url} />
            ) : context.window_type === "god_edit" ? (
              <GodEditPanel
                apiBaseUrl={context.api_base_url}
                initialEntityType={
                  (context.params as Record<string, unknown> | null)?.extra
                    ? ((context.params as Record<string, unknown>).extra as Record<string, unknown>).entityType as string | undefined
                    : undefined
                }
                initialEntityId={
                  (context.params as Record<string, unknown> | null)?.extra
                    ? ((context.params as Record<string, unknown>).extra as Record<string, unknown>).entityId as string | undefined
                    : undefined
                }
              />
            ) : context.window_type === "quest_edit" ? (
              <QuestEditPanel
                apiBaseUrl={context.api_base_url}
                onClose={closeWindow}
                editQuestId={
                  (context.params as Record<string, unknown> | null)?.extra
                    ? ((context.params as Record<string, unknown>).extra as Record<string, unknown>).editQuestId as number | undefined
                    : undefined
                }
              />
            ) : context.definition.empty_shell ? null : (
              <>
                <WindowMeta context={context} />
                <DataPanel queryState={queryState} />
                <ActionPanel
                  definition={context.definition}
                  actionState={actionState}
                  onAction={sendAction}
                />
              </>
            )}
            </Suspense>
          </section>
        </main>
      ) : (
        <main className="host-shell">
          <section className="host-panel">
          <h1>Genesis UI Host</h1>
          <p>Empty window</p>
          <dl className="status-grid">
            <div>
              <dt>Core API</dt>
              <dd>{apiBaseUrl || "loading..."}</dd>
            </div>
            <div>
              <dt>Open windows</dt>
              <dd>{openWindowCount}</dd>
            </div>
          </dl>
        </section>
        </main>
      )}
    </MasterDataProvider>
  );
}

/**
 * ウィンドウを開くときに params.extra で渡された追加情報(編集対象のIDなど)を返す。
 * titleKey があれば、タイトルバーはウィンドウ種別の既定名の代わりにその i18n キーの文言を出す
 * (同じウィンドウを新設と編集で使い分けるとき用)。
 */
function windowExtra(context: WindowContext): Record<string, unknown> | undefined {
  const params = context.params as Record<string, unknown> | null;
  return (params?.extra as Record<string, unknown> | undefined) ?? undefined;
}

function WindowMeta({ context }: { context: WindowContext }) {
  return (
    <section className="panel">
      <div className="panel-header">
        <h2>Window Context</h2>
      </div>
      <div className="meta-grid">
        <MetaRow label="Label" value={context.label} />
        <MetaRow label="Instance Key" value={context.instance_key} />
        <MetaRow label="Type" value={context.window_type} />
        <MetaRow label="Entity Type" value={context.entity?.type ?? "-"} />
        <MetaRow label="Entity ID" value={context.entity?.id ?? "-"} />
        <MetaRow label="Policy" value={context.definition.policy} />
      </div>
    </section>
  );
}

function MetaRow({ label, value }: { label: string; value: string }) {
  return (
    <>
      <span className="meta-label">{label}</span>
      <span className="meta-value">{value}</span>
    </>
  );
}

function DataPanel({ queryState }: { queryState: QueryState }) {
  return (
    <section className="panel">
      <div className="panel-header">
        <h2>REST Data</h2>
      </div>
      {queryState.status === "idle" && <p className="muted">このウィンドウには REST 定義がありません。</p>}
      {queryState.status === "loading" && <p className="muted">データを取得しています...</p>}
      {queryState.status === "error" && (
        <div className="error-box">
          <strong>取得失敗</strong>
          <p>{queryState.url}</p>
          <p>{queryState.message}</p>
        </div>
      )}
      {queryState.status === "success" && (
        <>
          <p className="muted">{queryState.url}</p>
          <pre className="json-view">{JSON.stringify(queryState.data, null, 2)}</pre>
        </>
      )}
    </section>
  );
}

function ActionPanel({
  definition,
  actionState,
  onAction,
}: {
  definition: WindowDefinition;
  actionState: string;
  onAction: (action: string) => Promise<void>;
}) {
  return (
    <section className="panel">
      <div className="panel-header">
        <h2>Actions</h2>
      </div>
      <div className="action-row">
        {definition.actions.map((action) => (
          <button key={action.id} onClick={() => onAction(action.id)} type="button">
            {action.label}
          </button>
        ))}
      </div>
      {actionState && <p className="muted">{actionState}</p>}
    </section>
  );
}

function HtmlPanel({ context }: { context: WindowContext }) {
  const htmlPath = context.definition.html_path;
  if (!htmlPath) return null;

  const separator = htmlPath.includes("?") ? "&" : "?";
  const src = `${htmlPath}${separator}api_base=${encodeURIComponent(context.api_base_url)}`;

  return (
    <iframe
      className="html-panel-iframe"
      src={src}
      title={context.title}
      sandbox="allow-scripts allow-same-origin"
    />
  );
}

export default App;
