use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::PathBuf;
use tauri::AppHandle;
use tauri::Manager;

const SETTINGS_FILE_NAME: &str = "settings.json";

/**
 * Application settings structure. Centralizes user settings such as language, font size, volume, HUD/popup
 * monitors, and hint bar/citizen voice visibility. serde's default attributes allow safe deserialization when settings are
 * missing from the file.
 */
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct AppSettings {
    pub language: String,
    pub font_size: f64,
    pub volumes: HashMap<String, u8>,
    pub monitor_index: usize,
    pub hintbar_visible: bool,
    /// Whether to show the top-right citizen voice card.
    pub citizen_voice_visible: bool,
    /// Multi-display mode (show popups on a monitor separate from the HUD).
    pub multi_display: bool,
    /// Popup monitor in multi-display mode.
    pub popup_monitor_index: usize,
}

impl Default for AppSettings {
    fn default() -> Self {
        let mut volumes = HashMap::new();
        volumes.insert("master".to_string(), 80);
        volumes.insert("bgm".to_string(), 60);
        volumes.insert("sfx".to_string(), 70);
        Self {
            language: "ja".to_string(),
            font_size: 13.0, // Body text size in px. Match the UI's FONT_SIZE_PRESETS.medium.
            volumes,
            monitor_index: 0,
            hintbar_visible: true,
            citizen_voice_visible: true,
            multi_display: false,
            popup_monitor_index: 0,
        }
    }
}

/**
 * Get the full settings file path. Use Tauri's app_config_dir to place settings.json under the OS standard
 * configuration directory. Create the directory automatically if missing.
 */
fn settings_path(app: &AppHandle) -> Result<PathBuf, String> {
    let dir = app.path().app_config_dir().map_err(|e| e.to_string())?;
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    Ok(dir.join(SETTINGS_FILE_NAME))
}

/**
 * Load the settings file. Return defaults if the file is missing or cannot be parsed so the application keeps
 * working on first launch or with corrupted settings.
 */
pub fn load_settings(app: &AppHandle) -> AppSettings {
    match settings_path(app) {
        Ok(path) => {
            if path.exists() {
                if let Ok(data) = std::fs::read_to_string(&path) {
                    match serde_json::from_str::<AppSettings>(&data) {
                        Ok(settings) => return settings,
                        Err(err) => {
                            eprintln!("設定ファイルのパースに失敗しました: {}", err);
                        }
                    }
                }
            }
        }
        Err(err) => {
            eprintln!("設定ファイルパスの取得に失敗しました: {}", err);
        }
    }
    AppSettings::default()
}

/**
 * Write the settings file. Called by the frontend when settings change so persisted settings can be restored on
 * the next launch.
 */
pub fn save_settings(app: &AppHandle, settings: &AppSettings) -> Result<(), String> {
    let path = settings_path(app)?;
    let data = serde_json::to_string_pretty(settings).map_err(|e| e.to_string())?;
    std::fs::write(&path, data).map_err(|e| e.to_string())?;
    Ok(())
}
