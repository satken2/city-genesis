import { invoke } from "@tauri-apps/api/core";
import { emit } from "@tauri-apps/api/event";

/**
 * Application settings types, matching Rust settings::AppSettings. Includes language, font size, volume,
 * HUD/popup monitors, hint bar/citizen voice visibility, etc. Rust commands (switch_monitor / set_multi_display /
 * switch_popup_monitor) save the three monitor settings; do not overwrite them from the UI.
 */
export type AppSettings = {
  language: string;
  font_size: number;
  volumes: Record<string, number>;
  monitor_index: number;
  /** Whether to show the top hint bar. */
  hintbar_visible: boolean;
  /** Whether to show the top-right citizen voice card. */
  citizen_voice_visible: boolean;
  /** Multi-display mode (popups on a monitor separate from the HUD). */
  multi_display: boolean;
  /** Popup monitor in multi-display mode. */
  popup_monitor_index: number;
};

/**
 * Load application settings from file. Call Rust load_app_settings to restore persisted JSON settings.
 */
export async function loadAppSettings(): Promise<AppSettings> {
  return invoke<AppSettings>("load_app_settings");
}

/**
 * Save application settings to file through Rust save_app_settings for restoration on the next launch. Also emit
 * settings-updated to apply changes such as volume immediately.
 */
export async function saveAppSettings(settings: AppSettings): Promise<void> {
  await invoke("save_app_settings", { settings });
  await emit("settings-updated", settings);
}

