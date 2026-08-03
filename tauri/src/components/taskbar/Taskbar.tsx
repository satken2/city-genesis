/**
 * 常駐タスクバーコンポーネント
 *
 * Windows 11のタスクバー風の、画面下部に常駐する半透明バー。
 * 左から順に、検索欄、メニューアイコン領域、システムトレイ（設定・セーブ・リロード）、
 * モニター切替ボタン、ゲーム内時間を表示する。
 *
 * - 検索欄は見た目のみのプレースホルダー（機能は後で追加）
 * - メニューアイコンは中央に配置する。各アイコンは window-registry のウィンドウタイプを開く（未割当は無操作）
 * - システムトレイアイコンは右側（ゲーム内時刻の左）に小さく配置する
 * - "<" / ">" ボタンでタスクバーを別のディスプレイに移動する
 * - ゲーム内時間は親プロセスからのIPCイベントで更新される
 *
 * 各アイコンのパディングは画像サイズの差を吸収するため、
 * iconPaddingフィールドで個別に調整可能にしている
 */

import { useEffect, useMemo, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { listen } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";

import { sendSessionForceReload, sendSessionReload, sendChangeState, sendChangeDisplay, IPC_EVENT_WORLD_SELECT_OBJECT } from "../../utils/ipc";
import { setHintbarText } from "../../utils/hintbar";
import { formatEpochDate } from "../../utils/date";
import { BgmManager } from "../../utils/bgm";
import "./Taskbar.css";

function formatDate(daysSinceEpoch: number, locale: string): string {
  return formatEpochDate(daysSinceEpoch, locale);
}

type SessionStatePayload = {
  current_save_id: string | null;
  session?: Record<string, unknown>;
  city?: Record<string, unknown>;
  apiBaseUrl: string;
  isLoading?: boolean;
};


type GameTimePayload = {
  epoch?: number;
  days?: number;
  date_str?: string;
};

/**
 * タスクバーに配置するメニューアイコンの定義
 * paddingは画像ごとのサイズ差を手動調整するためのもの
 */
type MenuIconDef = {
  id: string;
  src: string;
  tooltipKey: string;
  padding: string;
  /** タスクバーから開くウィンドウタイプ（未設定ならクリック無効） */
  windowType?: string;
};

/**
 * システムトレイに配置する小アイコンの定義
 */
type TrayIconDef = {
  id: string;
  src: string;
  tooltipKey: string;
  padding: string;
};

const MENU_ICONS: MenuIconDef[] = [
  { id: "road", src: "menu_road.png", tooltipKey: "taskbar.menuRoad", padding: "4px", windowType: "path_build" },
  { id: "land", src: "menu_land.png", tooltipKey: "taskbar.menuLand", padding: "4px", windowType: "parcel_creation" },
  { id: "district", src: "menu_district.png", tooltipKey: "taskbar.menuDistrict", padding: "4px" },
  { id: "construction", src: "menu_construction.png", tooltipKey: "taskbar.menuConstruction", padding: "4px", windowType: "building_construction" },
  { id: "transport", src: "menu_transport.png", tooltipKey: "taskbar.menuTransport", padding: "4px", windowType: "transit_management" },
  { id: "budget", src: "menu_budget.png", tooltipKey: "taskbar.menuBudget", padding: "4px", windowType: "city_budget" },
  { id: "public", src: "menu_public.png", tooltipKey: "taskbar.menuPublic", padding: "4px", windowType: "public_facilities" },
  { id: "regulation", src: "menu_regulation.png", tooltipKey: "taskbar.menuRegulation", padding: "4px", windowType: "city_regulation" },
  { id: "city", src: "menu_city.png", tooltipKey: "taskbar.menuCity", padding: "4px", windowType: "city_hall" },
  { id: "finance", src: "menu_finance.png", tooltipKey: "taskbar.menuFinance", padding: "4px", windowType: "financial_policy" },
  { id: "stat", src: "menu_stat.png", tooltipKey: "taskbar.menuStat", padding: "4px", windowType: "city_statistics" },
  { id: "achieve", src: "menu_achieve.png", tooltipKey: "taskbar.menuAchieve", padding: "4px", windowType: "achievements" },
];

const TRAY_ICONS: TrayIconDef[] = [
  { id: "settings", src: "tray_settings.png", tooltipKey: "taskbar.traySettings", padding: "4px" },
  { id: "save", src: "tray_save.png", tooltipKey: "taskbar.traySave", padding: "4px" },
  { id: "reload", src: "tray_reload.png", tooltipKey: "taskbar.trayReload", padding: "4px" },
];

/**
 * クリックすると Unity の描画ステートを変更する編集モード系ウィンドウの種別。
 * これらを開くと Unity は STATE_REMOVE_OBJECTS を上書きするため、
 * ブルドーザトグルを連動して OFF にする必要がある。
 */
const EDIT_MODE_WINDOW_TYPES = new Set<string>([
  "path_build",
  "parcel_creation",
  "building_construction",
  "transit_management",
]);

function iconUrl(filename: string): string {
  return `/images/menu/${filename}`;
}

export function Taskbar() {
  const { t, i18n } = useTranslation();
  const appWindow = useMemo(() => getCurrentWindow(), []);
  const [visible, setVisible] = useState(false);
  const [apiBaseUrl, setApiBaseUrl] = useState("");
  const [templateMode, setTemplateMode] = useState(false);
  const [cityHallPlaced, setCityHallPlaced] = useState(true);
  const [debugExecution, setDebugExecution] = useState(false);
  const [cityName, setCityName] = useState("");
  const [currentDate, setCurrentDate] = useState<number | null>(null);
  const [, setGameTime] = useState<GameTimePayload | null>(null);
  // エポック実行中(epoch_confirm ウィンドウから通知される)はタスクバーの全ボタンを無効化する
  const [epochExecuting, setEpochExecuting] = useState(false);
  // ブルドーザ(オブジェクト削除)モードのトグル状態。
  // ON にすると Unity を STATE_REMOVE_OBJECTS にし、OFF で STATE_NORMAL に戻す。
  const [bulldozerActive, setBulldozerActive] = useState(false);
  const [districtActive, setDistrictActive] = useState(false);

  // チュートリアル用の無効化・ハイライト状態
  const [tutorialActive, setTutorialActive] = useState(false);
  const [tutorialTarget, setTutorialTarget] = useState<string | undefined>(undefined);

  // BGM管理クラスのライフサイクル管理
  useEffect(() => {
    const bgm = new BgmManager();
    return () => {
      bgm.destroy();
    };
  }, []);

  // チュートリアル状態の監視・初期化
  useEffect(() => {
    // 初期状態を localStorage から復元
    const active = localStorage.getItem("tutorial_active") === "true";
    const target = localStorage.getItem("tutorial_target") || undefined;
    setTutorialActive(active);
    setTutorialTarget(target);

    let unlistenState: (() => void) | undefined;

    // グローバルイベントの購読
    listen<{ active: boolean; targetElementId?: string }>("tutorial:state_changed", (event) => {
      setTutorialActive(event.payload.active);
      setTutorialTarget(event.payload.targetElementId);
    })
      .then((fn) => {
        unlistenState = fn;
      })
      .catch(console.error);

    return () => {
      unlistenState?.();
    };
  }, []);

  const getBtnClassName = (id: string, baseClass: string) => {
    if (tutorialActive && tutorialTarget === id) {
      return `${baseClass} taskbar-highlighted-btn`;
    }
    return baseClass;
  };

  /**
   * Rust 側のセッションポーリングスレッドから配信される `session-state` イベントを受信し、
   * タスクバーウィンドウの表示/非表示と内容を更新する。
   *
   * current_save_id が非 null のときだけタスクバーを表示する。
   *
   * cityName と currentDate は同イベントの `city` フィールドから取得する。
   */
  useEffect(() => {
    let unlistenSession: (() => void) | undefined;
    let unlistenTime: (() => void) | undefined;
    let unlistenSelect: (() => void) | undefined;
    let unlistenEpoch: (() => void) | undefined;

    appWindow
      .listen<SessionStatePayload>("session-state", (event) => {
        const payload = event.payload;
        const shouldShow = payload.current_save_id != null;

        if (shouldShow) {
          appWindow.show().catch(() => {});
        } else {
          appWindow.hide().catch(() => {});
          // セッションが切れた/再接続時は Unity が STATE_NORMAL で起動するため、
          // ブルドーザ・地区編集トグルもリセットして表示をそろえる。
          setBulldozerActive(false);
          setDistrictActive(false);
        }
        setVisible(shouldShow);
        setApiBaseUrl(payload.apiBaseUrl);
        setTemplateMode((payload.session?.template_mode as boolean) ?? false);
        setDebugExecution((payload.session?.debug_execution as boolean) ?? false);

        const cityData = payload.city?.city as Record<string, unknown> | undefined;
        setCityName((cityData?.name as string) ?? "");
        setCurrentDate((cityData?.current_date as number) ?? null);
        setCityHallPlaced((cityData?.city_hall_placed as boolean) ?? false);
      })
      .then((fn) => {
        unlistenSession = fn;
      })
      .catch(console.error);

    appWindow
      .listen<GameTimePayload>("game:time_update", (event) => {
        setGameTime(event.payload);
      })
      .then((fn) => {
        unlistenTime = fn;
      })
      .catch(console.error);

    // グラフィックプログラムからのオブジェクト選択イベントを受信し、対応する詳細ウィンドウを開く
    appWindow
      .listen<{ window_type: string; entity: { type: string; id: string } }>(IPC_EVENT_WORLD_SELECT_OBJECT, (event) => {
        const payload = event.payload;
        if (payload.window_type === "zone_detail" && payload.entity?.type === "zone") {
          void invoke("open_registered_window", {
            windowType: "zone_detail",
            entityId: payload.entity.id,
          });
        }
      })
      .then((fn) => {
        unlistenSelect = fn;
      })
      .catch(console.error);

    // エポック実行の開始/終了を受信し、実行中はタスクバーを無効化する。
    // 送信元は epoch_confirm ウィンドウ(EpochConfirmWindow.tsx)。
    appWindow
      .listen<{ executing: boolean }>("epoch:executing", (event) => {
        setEpochExecuting(event.payload.executing);
      })
      .then((fn) => {
        unlistenEpoch = fn;
      })
      .catch(console.error);

    return () => {
      unlistenSession?.();
      unlistenTime?.();
      unlistenSelect?.();
      unlistenEpoch?.();
    };
  }, [appWindow]);

  async function openRegisteredWindow(typeKey: string) {
    try {
      await invoke("open_registered_window", { windowType: typeKey });
    } catch (error) {
      console.error("failed to open window", typeKey, error);
    }
  }

  function handleExecuteEpoch() {
    void openRegisteredWindow("epoch_confirm");
  }

  /**
   * ブルドーザ(オブジェクト削除)モードのトグル。
   * ON で Unity を STATE_REMOVE_OBJECTS にし、OFF で STATE_NORMAL に戻す。
   * windowType "bulldozer" は送信元ラベルであり、実ウィンドウは開かない。
   */
  async function toggleBulldozer() {
    const next = !bulldozerActive;
    try {
      if (next) setDistrictActive(false);
      await sendChangeState(next ? "STATE_REMOVE_OBJECTS" : "STATE_NORMAL", undefined, "bulldozer");
      await setHintbarText(next ? t("hint.bulldozer") : "");
      setBulldozerActive(next);
    } catch (error) {
      console.error("failed to toggle bulldozer mode", error);
    }
  }

  /**
   * 地区編集モードのトグル。ブルドーザと同じくウィンドウを開かない常駐モード
   * (windowType "district" は送信元ラベルで実ウィンドウは無い)。
   * ON で Unity を STATE_EDIT_ZONE にして道路を地区色分け表示にし、ヒントバーに操作説明を出す。
   * OFF で STATE_NORMAL に戻し道路表示を既定へ戻してからセッションをリロードする
   * (地区の割当・拡張は3Dジェスチャで即時反映されるため、抜ける際に最新状態を確実に反映する)。
   * 3Dビュー内のツール切替(作成/拡張)・操作は Unity 側の Ctrl ラジアルメニューが担う。
   */
  async function toggleDistrict() {
    const next = !districtActive;
    try {
      if (next) {
        setBulldozerActive(false);
        await setHintbarText(t("hint.districtEdit"));
        await sendChangeState("STATE_EDIT_ZONE", undefined, "district");
        await sendChangeDisplay("road", "zone_color");
      } else {
        await sendChangeState("STATE_NORMAL", undefined, "district");
        await sendChangeDisplay("road", "default");
        await setHintbarText("");
        await sendSessionReload();
      }
      setDistrictActive(next);
    } catch (error) {
      console.error("failed to toggle district edit mode", error);
    }
  }

  async function toggleTemplateMode() {
    const next = !templateMode;
    try {
      await fetch(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/sessions`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ template_mode: next }),
      });
      setTemplateMode(next);
    } catch (error) {
      console.error("failed to toggle template mode", error);
    }
  }

  const isLocked = !cityHallPlaced && !templateMode;
  const locale = i18n.language === "ja" ? "ja-JP" : "en-US";
  const formattedDate = currentDate != null
    ? formatDate(currentDate, locale)
    : "—";

  /**
   * Graphic 未接続、またはセッション未作成(current_save_id が null)のときは描画しない。
   * ウィンドウ自体は session-state イベント側で非表示にしている。
   */
  if (!visible) return null;

  return (
    <div className={`taskbar-root ${epochExecuting ? "taskbar-disabled" : ""}`}>
      <div className="taskbar-search-wrapper">
        <button
          id="visibility"
          className={getBtnClassName("visibility", "taskbar-tray-icon")}
          type="button"
          title={t("taskbar.menuView")}
          onClick={() => void openRegisteredWindow("display_options")}
        >
          <img
            src={iconUrl("visibility.png")}
            alt={t("taskbar.menuView")}
            className="taskbar-tray-icon-img"
            draggable={false}
          />
        </button>

        <button
          id="map"
          className={getBtnClassName("map", "taskbar-tray-icon")}
          type="button"
          title={t("taskbar.menuMap")}
          onClick={() => void openRegisteredWindow("city_map")}
        >
          <img
            src={iconUrl("map.png")}
            alt={t("taskbar.menuMap")}
            className="taskbar-tray-icon-img"
            draggable={false}
          />
        </button>
        <button
          id="unlock"
          className={getBtnClassName("unlock", "taskbar-tray-icon")}
          type="button"
          title={t("taskbar.menuUnlock")}
          onClick={async () => {
            setBulldozerActive(false);
            setDistrictActive(false);
            await setHintbarText(t("hint.terrainUnlock"));
            await openRegisteredWindow("terrain_unlock");
            await sendChangeState("STATE_UNLOCK_CELL", undefined, "terrain_unlock");
          }}
        >
          <img
            src={iconUrl("pixels.png")}
            alt={t("taskbar.menuUnlock")}
            className="taskbar-tray-icon-img"
            draggable={false}
          />
        </button>
        <button
          id="bulldozer"
          className={`${getBtnClassName("bulldozer", "taskbar-tray-icon")}${bulldozerActive ? " taskbar-tray-icon--active" : ""}`}
          type="button"
          aria-pressed={bulldozerActive}
          title={t(bulldozerActive ? "taskbar.bulldozerOn" : "taskbar.bulldozerOff")}
          onClick={() => void toggleBulldozer()}
        >
          <img
            src={iconUrl("menu_bulldoze.png")}
            alt={t(bulldozerActive ? "taskbar.bulldozerOn" : "taskbar.bulldozerOff")}
            className="taskbar-tray-icon-img"
            draggable={false}
          />
        </button>
      </div>

      <div className="taskbar-icons">
        {MENU_ICONS.map((icon) => (
          <button
            key={icon.id}
            id={icon.id}
            className={`${getBtnClassName(icon.id, "taskbar-icon-slot")}${icon.id === "district" && districtActive ? " taskbar-icon-slot--active" : ""}`}
            type="button"
            title={t(icon.tooltipKey)}
            aria-pressed={icon.id === "district" ? districtActive : undefined}
            style={{ padding: icon.padding }}
            disabled={isLocked && icon.id !== "construction"}
            onClick={async () => {
              if (icon.id === "district") {
                // 地区編集はウィンドウを開かず、ブルドーザと同型のトグルモードにする。
                await toggleDistrict();
              } else if (icon.windowType) {
                // 編集モード系ウィンドウを開くと Unity が STATE を上書きするため、
                // ブルドーザ・地区編集トグルを連動して OFF にする。
                if (EDIT_MODE_WINDOW_TYPES.has(icon.windowType)) {
                  setBulldozerActive(false);
                  setDistrictActive(false);
                }
                await setHintbarText("");
                await openRegisteredWindow(icon.windowType);
              }
            }}
          >
            <img
              src={iconUrl(icon.src)}
              alt={t(icon.tooltipKey)}
              className="taskbar-icon-img"
              draggable={false}
            />
          </button>
        ))}
        {templateMode && (
          <button
            id="resource_edit"
            className={getBtnClassName("resource_edit", "taskbar-icon-slot")}
            type="button"
            title={t("taskbar.menuResourceEdit")}
            style={{ padding: "4px" }}
            disabled={isLocked}
            onClick={async () => {
              setBulldozerActive(false);
              setDistrictActive(false);
              await setHintbarText(t("hint.resourceEdit"));
              await openRegisteredWindow("resource_edit");
            }}
          >
            <img
              src={iconUrl("pixels.png")}
              alt={t("taskbar.menuResourceEdit")}
              className="taskbar-icon-img"
              draggable={false}
            />
          </button>
        )}
        {templateMode && (
          <button
            id="parameter_edit"
            className={getBtnClassName("parameter_edit", "taskbar-icon-slot")}
            type="button"
            title={t("taskbar.menuParameterEdit")}
            style={{ padding: "4px" }}
            disabled={isLocked}
            onClick={() => void invoke("open_parameter_edit_window")}
          >
            <img
              src={iconUrl("tray_settings.png")}
              alt={t("taskbar.menuParameterEdit")}
              className="taskbar-icon-img"
              draggable={false}
            />
          </button>
        )}
        <button
          id="prop_placement"
          className={getBtnClassName("prop_placement", "taskbar-icon-slot")}
          type="button"
          title={t("taskbar.menuPropPlacement")}
          style={{ padding: "4px" }}
          disabled={isLocked}
          onClick={async () => {
            setBulldozerActive(false);
            setDistrictActive(false);
            await setHintbarText(t("prop_placement.hint"));
            await openRegisteredWindow("prop_placement");
          }}
        >
          <img
            src={iconUrl("menu_land.png")}
            alt={t("taskbar.menuPropPlacement")}
            className="taskbar-icon-img"
            draggable={false}
          />
        </button>
        <div className="taskbar-divider" />
        <button
          id="execute"
          className={getBtnClassName("execute", "taskbar-execute-btn")}
          type="button"
          title={t("taskbar.menuExecute")}
          disabled={isLocked}
          onClick={() => void handleExecuteEpoch()}
        >
          {t("taskbar.menuExecute")}
          <img
            src={iconUrl("menu_execute.png")}
            alt={t("taskbar.menuExecute")}
            className="taskbar-execute-img"
            draggable={false}
          />
        </button>
      </div>

      <div className="taskbar-tray">
        {debugExecution && (
          <button
            id="debug_template"
            className={getBtnClassName("debug_template", "taskbar-tray-icon")}
            type="button"
            title={t(templateMode ? "taskbar.trayTemplateOn" : "taskbar.trayTemplateOff")}
            style={{ padding: "4px" }}
            onClick={() => void toggleTemplateMode()}
          >
            <img
              src={iconUrl(templateMode ? "templat_on.png" : "templat_off.png")}
              alt={t(templateMode ? "taskbar.trayTemplateOn" : "taskbar.trayTemplateOff")}
              className="taskbar-tray-icon-img"
              draggable={false}
            />
          </button>
        )}
        {TRAY_ICONS.map((icon) => (
          <button
            key={icon.id}
            id={icon.id}
            className={getBtnClassName(icon.id, "taskbar-tray-icon")}
            type="button"
            title={t(icon.tooltipKey)}
            style={{ padding: icon.padding }}
            onClick={() => {
              if (icon.id === "settings") {
                void openRegisteredWindow("settings");
              } else if (icon.id === "save") {
                void openRegisteredWindow("save_load");
              } else if (icon.id === "reload") {
                void sendSessionForceReload();
              }
            }}
          >
            <img
              src={iconUrl(icon.src)}
              alt={t(icon.tooltipKey)}
              className="taskbar-tray-icon-img"
              draggable={false}
            />
          </button>
        ))}
      </div>

      <div className="taskbar-time">
        <span className="taskbar-time-city">{cityName || "—"}</span>
        <span className="taskbar-time-date">{formattedDate}</span>
      </div>
    </div>
  );
}
