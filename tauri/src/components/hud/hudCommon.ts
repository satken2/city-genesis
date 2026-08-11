/**
 * HUD(ゲーム中に画面へ常駐する操作パネル群)の共通定義
 *
 * HUDは3枚のTauriウィンドウに分かれている。
 * Unityの3Dビューに重ねる透明ウィンドウでは、見た目が透明な領域でもクリックが
 * WebViewに吸われてしまうため、パネルの実面積だけを覆うウィンドウに分割している。
 *
 * | ウィンドウラベル | コンポーネント | 位置 | 内容 |
 * | --- | --- | --- | --- |
 * | `icon-panel` | `IconPanel` | 画面左端・上下中央 | 各種メニュー(道路/建設/予算/統計…)。背景は透明 |
 * | `cockpit` | `Cockpit` | 画面下部中央 | 常用ツール4種・都市名と日付・施政スコア/予算メータ・アラート・エポック実行 |
 * | `system-tray` | `SystemTray` | 画面右下 | 設定・セーブ・リロード |
 *
 * 各アイコンのパディングは画像サイズの差を吸収するため、
 * padding フィールドで個別に調整可能にしている。
 */

import { invoke } from "@tauri-apps/api/core";

/** メニューアイコン画像のURLを解決する */
export function iconUrl(filename: string): string {
  return `/images/menu/${filename}`;
}

/**
 * HUDに配置するアイコンの定義
 */
export type HudIconDef = {
  id: string;
  src: string;
  tooltipKey: string;
  padding: string;
  /** クリックで開くウィンドウタイプ(未設定ならクリック無効、またはコンポーネント側で個別処理) */
  windowType?: string;
};

/**
 * アイコンパネル(画面左端)に縦並びで置くメニューアイコン。
 * 上から機能グループ順(建設系 → 交通系 → 市政系 → 情報系)に並べる。
 */
export const MENU_ICONS: HudIconDef[] = [
  { id: "road", src: "menu_road.png", tooltipKey: "taskbar.menuRoad", padding: "4px", windowType: "path_build" },
  { id: "track", src: "menu_road.png", tooltipKey: "taskbar.menuTrack", padding: "4px", windowType: "track_build" },
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

/**
 * コックピット左側に2行×2列で置く常用ツール。
 * 表示切替・マップ・地形セル開放・ブルドーザという、
 * 他のメニューを開いている最中でも押したくなる操作をまとめている。
 */
export const TOOL_ICONS: HudIconDef[] = [
  { id: "visibility", src: "visibility.png", tooltipKey: "taskbar.menuView", padding: "4px", windowType: "display_options" },
  { id: "map", src: "map.png", tooltipKey: "taskbar.menuMap", padding: "4px", windowType: "city_map" },
  { id: "unlock", src: "pixels.png", tooltipKey: "taskbar.menuUnlock", padding: "4px", windowType: "terrain_unlock" },
  { id: "bulldozer", src: "menu_bulldoze.png", tooltipKey: "taskbar.bulldozerOff", padding: "3px" },
];

/**
 * システムトレイ(画面右下)に置くアイコン。
 */
export const TRAY_ICONS: HudIconDef[] = [
  { id: "settings", src: "tray_settings.png", tooltipKey: "taskbar.traySettings", padding: "4px", windowType: "settings" },
  { id: "save", src: "tray_save.png", tooltipKey: "taskbar.traySave", padding: "4px", windowType: "save_load" },
  { id: "reload", src: "tray_reload.png", tooltipKey: "taskbar.trayReload", padding: "4px" },
];

/**
 * クリックすると Unity の描画ステートを変更する編集モード系ウィンドウの種別。
 * これらを開くと Unity は STATE_REMOVE_OBJECTS を上書きするため、
 * ブルドーザ・地区編集トグルを連動して OFF にする必要がある。
 */
export const EDIT_MODE_WINDOW_TYPES = new Set<string>([
  "path_build",
  "track_build",
  "parcel_creation",
  "building_construction",
  "transit_management",
]);

/** レジストリ定義のウィンドウを開く。失敗してもHUDは壊さず、コンソールに残すだけにする。 */
export async function openRegisteredWindow(typeKey: string): Promise<void> {
  try {
    await invoke("open_registered_window", { windowType: typeKey });
  } catch (error) {
    console.error("failed to open window", typeKey, error);
  }
}
