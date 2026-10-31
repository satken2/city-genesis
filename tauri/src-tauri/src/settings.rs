use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::PathBuf;
use tauri::AppHandle;
use tauri::Manager;

const SETTINGS_FILE_NAME: &str = "settings.json";

/// Supported UI languages. Keep in sync with SUPPORTED_LANGUAGES in src/utils/locale.ts and the table in steam::game_language.
const SUPPORTED_LANGUAGES: [&str; 7] = ["ja", "en", "es-419", "zh-Hans", "zh-Hant", "fr", "ko"];
/// Initial language when neither Steam nor the OS gives a supported language.
const FALLBACK_LANGUAGE: &str = "en";
/// Regions that write Chinese in Traditional script, for tags without a script subtag (zh-TW, zh-HK, zh-MO).
const TRADITIONAL_CHINESE_REGIONS: [&str; 3] = ["tw", "hk", "mo"];

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
            language: initial_language(),
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
 * Language before the player chooses one: the game language set in Steam, then the OS display languages in the
 * user's preference order, then English. Once settings are saved, the saved language takes precedence.
 */
fn initial_language() -> String {
    #[cfg(feature = "steam")]
    if let Some(language) = crate::steam::game_language() {
        return language.to_string();
    }
    os_languages()
        .iter()
        .find_map(|tag| language_from_locale_tag(tag))
        .unwrap_or(FALLBACK_LANGUAGE)
        .to_string()
}

/// Map a BCP 47 tag (ja-JP, zh-Hant-HK, ...) to a supported language by its primary subtag. Chinese resources are
/// split by script, so infer the script from the region when the tag has no script subtag.
fn language_from_locale_tag(tag: &str) -> Option<&'static str> {
    let subtags: Vec<String> = tag.split(['-', '_']).map(str::to_ascii_lowercase).collect();
    let has = |subtag: &str| subtags.iter().any(|s| s == subtag);
    let primary = subtags.first()?.as_str();
    if primary == "zh" {
        let traditional = has("hant") || (!has("hans") && TRADITIONAL_CHINESE_REGIONS.iter().any(|r| has(r)));
        return Some(if traditional { "zh-Hant" } else { "zh-Hans" });
    }
    SUPPORTED_LANGUAGES.iter().copied().find(|code| code.split('-').next() == Some(primary))
}

/// Windows display languages in the user's preference order, as MUI names (ja-JP, zh-TW, ...).
#[cfg(target_os = "windows")]
fn os_languages() -> Vec<String> {
    use windows_sys::Win32::Globalization::{GetUserPreferredUILanguages, MUI_LANGUAGE_NAME};
    let mut count = 0u32;
    let mut len = 0u32;
    unsafe {
        if GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, &mut count, std::ptr::null_mut(), &mut len) == 0 {
            return Vec::new();
        }
        let mut buffer = vec![0u16; len as usize];
        if GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, &mut count, buffer.as_mut_ptr(), &mut len) == 0 {
            return Vec::new();
        }
        buffer
            .split(|&c| c == 0)
            .filter(|name| !name.is_empty())
            .map(String::from_utf16_lossy)
            .collect()
    }
}

/// OS language detection is implemented only for Windows.
#[cfg(not(target_os = "windows"))]
fn os_languages() -> Vec<String> {
    Vec::new()
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn locale_tags_map_to_supported_languages() {
        let cases = [
            ("ja-JP", Some("ja")),
            ("en-GB", Some("en")),
            ("es-ES", Some("es-419")),
            ("es-419", Some("es-419")),
            ("zh-CN", Some("zh-Hans")),
            ("zh-SG", Some("zh-Hans")),
            ("zh-TW", Some("zh-Hant")),
            ("zh-HK", Some("zh-Hant")),
            ("zh-Hans-HK", Some("zh-Hans")),
            ("zh-Hant", Some("zh-Hant")),
            ("fr-CA", Some("fr")),
            ("ko-KR", Some("ko")),
            ("de-DE", None),
            ("", None),
        ];
        for (tag, expected) in cases {
            assert_eq!(language_from_locale_tag(tag), expected, "{tag}");
        }
    }
}
