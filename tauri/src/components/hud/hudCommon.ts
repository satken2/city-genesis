/**
 * HUD(ゲーム中に画面へ常駐する操作パネル群)の共通定義
 *
 * HUDは4枚のTauriウィンドウに分かれている。
 * Unityの3Dビューに重ねる透明ウィンドウでは、見た目が透明な領域でもクリックが
 * WebViewに吸われてしまうため、パネルの実面積だけを覆うウィンドウに分割している。
 *
 * | ウィンドウラベル | コンポーネント | 位置 | 内容 |
 * | --- | --- | --- | --- |
 * | `icon-panel` | `IconPanel` | 画面左端・上下中央 | 各種メニュー(道路/建設/予算/統計…)。背景は透明 |
 * | `cockpit` | `Cockpit` | 画面下部中央 | 追跡リスト・常用ツール3種・都市名と日付・施政スコア/予算メータ・アラート・エポック実行 |
 * | `system-tray` | `SystemTray` | 画面右下 | システム統計・設定・セーブ・リロード |
 * | `display-tray` | `DisplayTray` | 画面左下 | 表示レイヤーの簡易ON/OFF(建物・パス・プロップ・区画・地形・地区) |
 *
 * コックピット・システムトレイのアイコンは背景板の上に画像を直接置くため、
 * 画像サイズの差を padding フィールドで個別に吸収する
 * (アイコンパネルはCSSで描いたボタン枠の中に画像を敷き詰めるので指定しない)。
 */

import { invoke } from "@tauri-apps/api/core";
import citizenTrackingIcon from "../../assets/images/menu/citizen-tracking.svg";

/** メニューアイコン画像のURLを解決する(ブラウザ版はページの置き場所からの相対になる) */
export function iconUrl(filename: string): string {
  return `${import.meta.env.BASE_URL}images/menu/${filename}`;
}

/**
 * HUDに配置するアイコンの定義
 */
export type HudIconDef = {
  id: string;
  src: string;
  tooltipKey: string;
  /** panelディレクトリ内の画像ファイル名(拡張子除く)。未指定時は id と同名 */
  panelIcon?: string;
  /** アイコン画像の内側余白。画像を枠いっぱいに描く箇所(アイコンパネル)では指定しない */
  padding?: string;
  /** クリックで開くウィンドウタイプ(未設定ならクリック無効、またはコンポーネント側で個別処理) */
  windowType?: string;
  /** ウィンドウレジストリを経由せず直接呼ぶTauriコマンド名(`windowType`と排他) */
  command?: string;
  /** ウィンドウを開くときにヒントバーへ出す文言のキー。未指定ならヒントバーを空にする */
  hintKey?: string;
  /** テンプレート作成モードのときだけ表示する(製作者向けの操作) */
  templateOnly?: boolean;
  /** 市庁舎未設置によるロック中でも押せる(=都市創設そのものに必要な操作) */
  enabledWhenLocked?: boolean;
  /** 読み取り専用モード(公開サーバーで不特定多数に見せる都市)では出さない */
  hiddenWhenReadOnly?: boolean;
};

/**
 * アイコンパネル(画面左端)に縦並びで置くメニューアイコン。この順にそのまま縦に並ぶ。
 * 上から通常プレイの操作群を並べ、末尾に `templateOnly` の製作者向け操作を置く
 * (通常プレイでは表示されず、区切り線で分ける)。
 *
 * 予算(`city_budget`)・公共施設(`public_facilities`)・達成目標(`achievements`)はここに置かない。
 * いずれもコックピットのメータ(予算ミニメータ・公共サービス充足度・スコアミニメータ)が
 * 常時その状況を表示しており、そのままクリックで開くため、入口を二重に持たせない。
 */
export const MENU_ICONS: HudIconDef[] = [
  { id: "grading", src: "menu_grading.png", tooltipKey: "taskbar.menuGrading", windowType: "grading", hintKey: "hint.grading", hiddenWhenReadOnly: true },
  { id: "road", src: "menu_road.png", tooltipKey: "taskbar.menuRoad", windowType: "path_build", hiddenWhenReadOnly: true }, // 道路・鉄道・インフラのパスをまとめて敷く
  { id: "land", src: "menu_land.png", tooltipKey: "taskbar.menuLand", windowType: "parcel_creation", hiddenWhenReadOnly: true },
  { id: "district", src: "menu_district.png", tooltipKey: "taskbar.menuDistrict", windowType: "zone_edit", hiddenWhenReadOnly: true },
  { id: "construction", src: "menu_construction.png", tooltipKey: "taskbar.menuConstruction", windowType: "building_construction", enabledWhenLocked: true, hiddenWhenReadOnly: true },
  { id: "transport", src: "menu_transport.png", tooltipKey: "taskbar.menuTransport", windowType: "transit_management" },
  { id: "prop_placement", src: "menu_prop.png", tooltipKey: "taskbar.menuPropPlacement", windowType: "prop_placement", hintKey: "prop_placement.hint", hiddenWhenReadOnly: true },
  { id: "construction_projects", src: "menu_construction.png", tooltipKey: "taskbar.menuConstructionProjects", windowType: "construction_projects" },
  { id: "regulation", src: "menu_regulation.png", tooltipKey: "taskbar.menuRegulation", windowType: "city_regulation" },
  { id: "city", src: "menu_city.png", tooltipKey: "taskbar.menuCity", windowType: "city_hall" },
  { id: "finance", src: "menu_finance.png", tooltipKey: "taskbar.menuFinance", windowType: "financial_policy" },
  { id: "stat", src: "menu_stat.png", tooltipKey: "taskbar.menuStat", windowType: "city_statistics" },
  { id: "resource_edit", src: "menu_resource.png", panelIcon: "resources", tooltipKey: "taskbar.menuResourceEdit", windowType: "resource_edit", hintKey: "hint.resourceEdit", templateOnly: true },
  { id: "agent_creation", src: "menu_construction.png", panelIcon: "agents", tooltipKey: "taskbar.menuAgentCreation", windowType: "agent_creation", templateOnly: true },
  { id: "parameter_edit", src: "tray_settings.png", panelIcon: "adjust", tooltipKey: "taskbar.menuParameterEdit", command: "open_parameter_edit_window", templateOnly: true },
];

/**
 * コックピット左側の3列グリッドに行順で置く常用ツール。画像の参照先は解決済みURL。
 * 末尾にCockpit側のドライブボタンを続け、右下は空ける。
 */
export const TOOL_ICONS: HudIconDef[] = [
  { id: "visibility", src: iconUrl("visibility.png"), tooltipKey: "taskbar.menuView", padding: "4px", windowType: "display_options" },
  { id: "map", src: iconUrl("map.png"), tooltipKey: "taskbar.menuMap", padding: "4px", windowType: "city_map" },
  { id: "citizen_tracking", src: citizenTrackingIcon, tooltipKey: "taskbar.menuCitizenTracking", padding: "3px", windowType: "citizen_tracking" },
  { id: "bulldozer", src: iconUrl("bulldozer.png"), tooltipKey: "taskbar.bulldozerOff", padding: "3px" },
];

/**
 * システムトレイ(画面右下)に置くアイコン。
 */
export const TRAY_ICONS: HudIconDef[] = [
  { id: "system_stats", src: "tray_system_stats.png", tooltipKey: "taskbar.traySystemStats", padding: "4px", windowType: "system_stats", hiddenWhenReadOnly: true },
  { id: "settings", src: "tray_settings.png", tooltipKey: "taskbar.traySettings", padding: "4px", windowType: "settings", hiddenWhenReadOnly: true },
  { id: "save", src: "tray_save.png", tooltipKey: "taskbar.traySave", padding: "4px", windowType: "save_load", hiddenWhenReadOnly: true },
  { id: "reload", src: "tray_reload.png", tooltipKey: "taskbar.trayReload", padding: "4px" },
];

/** レジストリ定義のウィンドウを開く。失敗してもHUDは壊さず、コンソールに残すだけにする。 */
export async function openRegisteredWindow(typeKey: string): Promise<void> {
  try {
    await invoke("open_registered_window", { windowType: typeKey });
  } catch (error) {
    console.error("failed to open window", typeKey, error);
  }
}
