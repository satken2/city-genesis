use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::PathBuf;
use tauri::AppHandle;
use tauri::Manager;

const SETTINGS_FILE_NAME: &str = "settings.json";

/**
 * アプリケーション設定を表す構造体
 *
 * 言語、フォントサイズ、音量、タスクバー表示モニター、ヒントバー表示可否などの
 * ユーザー設定を一元管理する。
 * serde の default 属性により、設定ファイルに項目が存在しない場合でも
 * 安全にデシリアライズできる。
 */
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct AppSettings {
    pub language: String,
    pub font_size: f64,
    pub volumes: HashMap<String, u8>,
    pub monitor_index: usize,
    pub hintbar_visible: bool,
}

impl Default for AppSettings {
    fn default() -> Self {
        let mut volumes = HashMap::new();
        volumes.insert("master".to_string(), 80);
        volumes.insert("bgm".to_string(), 60);
        volumes.insert("sfx".to_string(), 70);
        Self {
            language: "ja".to_string(),
            font_size: 15.0,
            volumes,
            monitor_index: 0,
            hintbar_visible: true,
        }
    }
}

/**
 * 設定ファイルのフルパスを取得する
 *
 * Tauri の app_config_dir を使用し、OS 標準の設定ディレクトリ配下に
 * settings.json を配置する。ディレクトリが存在しない場合は自動的に作成する。
 */
fn settings_path(app: &AppHandle) -> Result<PathBuf, String> {
    let dir = app.path().app_config_dir().map_err(|e| e.to_string())?;
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    Ok(dir.join(SETTINGS_FILE_NAME))
}

/**
 * 設定ファイルを読み込む
 *
 * ファイルが存在しない、またはパースに失敗した場合はデフォルト値を返す。
 * これにより、初回起動時や設定ファイル破損時でもアプリケーションが
 * 正常に動作し続ける。
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
 * 設定ファイルに書き込む
 *
 * 設定変更時にフロントエンドから呼び出され、
 * 永続化された設定を次回起動時に復元できるようにする。
 */
pub fn save_settings(app: &AppHandle, settings: &AppSettings) -> Result<(), String> {
    let path = settings_path(app)?;
    let data = serde_json::to_string_pretty(settings).map_err(|e| e.to_string())?;
    std::fs::write(&path, data).map_err(|e| e.to_string())?;
    Ok(())
}
