//! User data for the asset manager (building, path, vehicle, and prop model lists, and building and vehicle weight editing).
//!
//! Player weight overrides are stored in `<user folder>/asset_weights.json` as {source: {unit: {model
//! number: weight}}}, where source is standard or a pack ID. Unity reads them whenever a city opens. Unity
//! writes thumbnails to `<user folder>/cache/asset_thumbnails/`; read them using the filenames received over IPC.

use base64::Engine;
use serde_json::{Map, Value};
use std::path::PathBuf;

use crate::version;

const WEIGHTS_FILE: &str = "asset_weights.json";
const THUMBNAIL_DIR: [&str; 2] = ["cache", "asset_thumbnails"];

fn user_dir() -> Result<PathBuf, String> {
    version::user_data_dir().ok_or_else(|| "ユーザーフォルダを解決できません".to_string())
}

/// Player weight overrides. Empty if the file does not exist.
#[tauri::command]
pub fn load_asset_weights() -> Result<Value, String> {
    let path = user_dir()?.join(WEIGHTS_FILE);
    match std::fs::read_to_string(&path) {
        Ok(text) => serde_json::from_str(&text).map_err(|e| format!("{WEIGHTS_FILE} の形式が不正です: {e}")),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(Value::Object(Map::new())),
        Err(e) => Err(format!("{WEIGHTS_FILE} を読めません: {e}")),
    }
}

/// Replace all player weight overrides. Write to a separate file before replacing the original so Unity cannot read a partially written file.
#[tauri::command]
pub fn save_asset_weights(weights: Value) -> Result<(), String> {
    if !is_weight_table(&weights) {
        return Err("重みの形式が不正です".into());
    }
    let dir = user_dir()?;
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    let text = serde_json::to_string_pretty(&weights).map_err(|e| e.to_string())?;
    let temporary = dir.join(format!("{WEIGHTS_FILE}.tmp"));
    std::fs::write(&temporary, text).map_err(|e| e.to_string())?;
    std::fs::rename(&temporary, dir.join(WEIGHTS_FILE)).map_err(|e| e.to_string())
}

/// Format: {source: {unit: {model number: weight}}}. Model numbers contain digits only; weights are nonnegative integers that fit in a Unity int.
fn is_weight_table(value: &Value) -> bool {
    value.as_object().is_some_and(|sources| {
        sources.values().all(|units| {
            units.as_object().is_some_and(|units| {
                units.values().all(|models| {
                    models.as_object().is_some_and(|models| {
                        models.iter().all(|(id, weight)| {
                            !id.is_empty()
                                && id.chars().all(|c| c.is_ascii_digit())
                                && weight.as_u64().is_some_and(|w| w <= i32::MAX as u64)
                        })
                    })
                })
            })
        })
    })
}

/// Return a Unity-generated thumbnail (PNG) as base64. Accept only filenames within the thumbnail folder.
#[tauri::command]
pub fn read_asset_thumbnail(file: String) -> Result<String, String> {
    if !is_thumbnail_name(&file) {
        return Err(format!("サムネイルの名前が不正です: {file}"));
    }
    let mut path = user_dir()?;
    for part in THUMBNAIL_DIR {
        path.push(part);
    }
    let bytes = std::fs::read(path.join(&file)).map_err(|e| format!("サムネイルを読めません: {e}"))?;
    Ok(base64::engine::general_purpose::STANDARD.encode(bytes))
}

fn is_thumbnail_name(file: &str) -> bool {
    file.strip_suffix(".png").is_some_and(|stem| {
        !stem.is_empty() && stem.chars().all(|c| c.is_ascii_alphanumeric() || c == '_' || c == '-')
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn weight_table_accepts_only_non_negative_integers_by_numeric_id() {
        assert!(is_weight_table(&json!({})));
        assert!(is_weight_table(&json!({"standard": {"Models/Buildings/a2000": {"0": 200, "400": 0}}})));
        assert!(!is_weight_table(&json!({"standard": {"Models/Buildings/a2000": {"0": -1}}})));
        assert!(!is_weight_table(&json!({"standard": {"Models/Buildings/a2000": {"0": 1.5}}})));
        assert!(!is_weight_table(&json!({"standard": {"Models/Buildings/a2000": {"x": 1}}})));
        assert!(!is_weight_table(&json!({"standard": []})));
    }

    #[test]
    fn thumbnail_name_stays_inside_the_folder() {
        assert!(is_thumbnail_name("buildings_2000_200.png"));
        assert!(!is_thumbnail_name("../asset_weights.json"));
        assert!(!is_thumbnail_name("..\\x.png"));
        assert!(!is_thumbnail_name(".png"));
    }
}
