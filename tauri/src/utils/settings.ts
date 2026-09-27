import { invoke } from "@tauri-apps/api/core";
import { emit } from "@tauri-apps/api/event";

/**
 * アプリケーション設定の型定義
 *
 * Rust 側の settings::AppSettings と対応する。
 * 言語、フォントサイズ、音量、HUDとポップアップの表示モニター、ヒントバー表示可否などを含む。
 * モニター関連の3項目は Rust のコマンド(`switch_monitor` / `set_multi_display` / `switch_popup_monitor`)が
 * 保存するので、UI からは書き換えない。
 */
export type AppSettings = {
  language: string;
  font_size: number;
  volumes: Record<string, number>;
  monitor_index: number;
  /** 画面上部のヒントバーを表示するか */
  hintbar_visible: boolean;
  /** マルチディスプレイモード(ポップアップをHUDとは別のモニターに出す) */
  multi_display: boolean;
  /** マルチディスプレイモードでポップアップを出すモニター */
  popup_monitor_index: number;
};

/**
 * 設定ファイルからアプリケーション設定を読み込む
 *
 * Rust コマンド load_app_settings を呼び出し、
 * JSON 形式で永続化された設定を復元する。
 */
export async function loadAppSettings(): Promise<AppSettings> {
  return invoke<AppSettings>("load_app_settings");
}

/**
 * アプリケーション設定をファイルに保存する
 *
 * Rust コマンド save_app_settings を呼び出し、
 * 次回起動時に同じ設定が復元されるように永続化する。
 * また、音量などの変更をリアルタイムに反映するため settings-updated イベントを発行する。
 */
export async function saveAppSettings(settings: AppSettings): Promise<void> {
  await invoke("save_app_settings", { settings });
  await emit("settings-updated", settings);
}

