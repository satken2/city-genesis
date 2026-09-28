use crate::state::SharedState;
use reqwest::header::{HeaderMap, HeaderValue};
use serde_json::{json, Value};
use std::path::PathBuf;
use std::sync::OnceLock;
use std::time::Duration;
use tauri::Manager;

/// ローカルAPIの認証ヘッダー。コアは欠落・不一致を 401 `API_TOKEN_REQUIRED` で拒否する。
pub const TOKEN_HEADER: &str = "X-City-Genesis-Token";

static USER_DATA_DIR: OnceLock<PathBuf> = OnceLock::new();

pub fn definition() -> Value {
    serde_json::from_str(include_str!("../../../version.json")).expect("invalid version.json")
}

/// <ユーザーフォルダ>(コアの `consts._get_appdata_dir()` と同じ `UrbanGenesis/`)を決めて記録する。setup で1回呼ぶ。
/// トークンファイル・セーブの置き場・地形エディタの作業コピーはすべてここを基準にする(`user_data_dir`)。
pub fn init_user_data_dir(app: &tauri::AppHandle) {
    match user_data_base(app) {
        Ok(base) => { let _ = USER_DATA_DIR.set(base.join("UrbanGenesis")); }
        Err(e) => log::error!("{e}"),
    }
}

/// OS のローカルデータフォルダ。Windows はコア・Unity と同じく環境変数 `LOCALAPPDATA` → `APPDATA` を先に見る
/// (既知フォルダ API は環境変数を見ないので、`LOCALAPPDATA` の無い環境でトークンファイルの場所がずれる)。
/// それ以外は app_local_data_dir(バンドル識別子のフォルダ)の親で、Linux ではコアと同じく `XDG_DATA_HOME` を反映する。
fn user_data_base(app: &tauri::AppHandle) -> Result<PathBuf, String> {
    #[cfg(target_os = "windows")]
    {
        for key in ["LOCALAPPDATA", "APPDATA"] {
            if let Some(dir) = std::env::var_os(key).filter(|value| !value.is_empty()) {
                return Ok(PathBuf::from(dir));
            }
        }
    }
    let dir = app
        .path()
        .app_local_data_dir()
        .map_err(|e| format!("ローカルデータディレクトリを解決できません: {e}"))?;
    dir.parent()
        .map(|base| base.to_path_buf())
        .ok_or_else(|| "ローカルデータディレクトリの親を解決できません".to_string())
}

/// <ユーザーフォルダ>。`init_user_data_dir` が解決できなかったときは None。
pub fn user_data_dir() -> Option<PathBuf> {
    USER_DATA_DIR.get().cloned()
}

/// ローカルAPIの認証トークン。環境変数 `GENESIS_API_TOKEN` → `<ユーザーフォルダ>/api_token` の順で解決する。
/// トークンはバックエンドの起動ごとに変わるので、キャッシュせず呼ぶたびに読み直す。
pub fn api_token() -> Option<String> {
    let trimmed = |value: String| Some(value.trim().to_string()).filter(|token| !token.is_empty());
    if let Some(token) = std::env::var("GENESIS_API_TOKEN").ok().and_then(trimmed) {
        return Some(token);
    }
    let path = USER_DATA_DIR.get()?.join("api_token");
    std::fs::read_to_string(path).ok().and_then(trimmed)
}

/// 版の契約ヘッダーと認証トークン。長く使い回すクライアントは既定ヘッダーにせず、リクエストごとに付け直す。
pub fn headers() -> HeaderMap {
    let mut headers = HeaderMap::new();
    headers.insert("X-City-Genesis-API", definition()["api_contract_version"].to_string().parse().unwrap());
    if let Some(mut token) = api_token().and_then(|token| HeaderValue::from_str(&token).ok()) {
        token.set_sensitive(true);
        headers.insert(TOKEN_HEADER, token);
    }
    headers
}

pub fn client() -> reqwest::Client {
    reqwest::Client::builder().default_headers(headers()).build().expect("HTTP client")
}

pub async fn verify_core(state: &SharedState) -> Result<(), String> {
    let (base, error) = { let guard = state.lock(); (guard.api_base_url.clone(), guard.graphic_contract_error.clone()) };
    if let Some(error) = error { return Err(error); }
    let response = client().get(format!("{}/api/v1/system/version", base.trim_end_matches('/')))
        .timeout(Duration::from_secs(10)).send().await.map_err(|e| e.to_string())?;
    let remote: Value = response.error_for_status().map_err(|e| e.to_string())?.json().await.map_err(|e| e.to_string())?;
    if remote["api_contract_version"] != definition()["api_contract_version"] {
        return Err(format!("API_CONTRACT_MISMATCH: core {} / UI {}", remote["api_contract_version"], definition()["api_contract_version"]));
    }
    Ok(())
}

pub async fn prepare_graphic(state: &SharedState) -> Result<(), String> {
    crate::window::launch_graphic(state)?;
    for _ in 0..150 {
        {
            let guard = state.lock();
            if let Some(error) = &guard.graphic_contract_error { return Err(error.clone()); }
            if guard.graphic_connected { return Ok(()); }
            if cfg!(all(debug_assertions, dev)) { return Ok(()); }
        }
        tokio::time::sleep(Duration::from_millis(100)).await;
    }
    Err("IPC_CONTRACT_MISMATCH".into())
}

pub async fn validate_load(state: &SharedState, id: &str, template: bool, accepted: bool) -> Result<(), String> {
    verify_core(state).await?;
    let base = state.lock().api_base_url.clone();
    let mut url = reqwest::Url::parse(&format!("{}/api/v1/saves/", base.trim_end_matches('/'))).map_err(|e| e.to_string())?;
    url.path_segments_mut().map_err(|_| "invalid API URL")?.pop_if_empty().push(id).push("validation");
    let response = client().get(url).query(&[("template", template)]).timeout(Duration::from_secs(120))
        .send().await.map_err(|e| e.to_string())?;
    let status = response.status();
    let data: Value = response.json().await.map_err(|e| e.to_string())?;
    if !status.is_success() { return Err(format!("{}: {}", data["code"], data["message"])); }
    if data["compatibility"]["balance_changed"] == true && !accepted {
        return Err("SAVE_BALANCE_CONFIRMATION_REQUIRED".into());
    }
    Ok(())
}

#[tauri::command]
pub fn get_frontend_status(state: tauri::State<'_, SharedState>) -> Value {
    let guard = state.lock();
    json!({ "error": guard.graphic_contract_error, "graphic_version": guard.graphic_version })
}
