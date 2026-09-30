use crate::state::SharedState;
use serde_json::{json, Value};
use std::path::PathBuf;
use std::sync::OnceLock;
use std::time::Duration;
use tauri::Manager;

static USER_DATA_DIR: OnceLock<PathBuf> = OnceLock::new();

pub fn definition() -> Value {
    serde_json::from_str(include_str!("../../../version.json")).expect("invalid version.json")
}

/// <ユーザーフォルダ>(コアの `consts._get_appdata_dir()` と同じ `UrbanGenesis/`)を決めて記録する。setup で1回呼ぶ。
/// セーブの置き場・地形エディタの作業コピーはすべてここを基準にする(`user_data_dir`)。
pub fn init_user_data_dir(app: &tauri::AppHandle) {
    match user_data_base(app) {
        Ok(base) => { let _ = USER_DATA_DIR.set(base.join("UrbanGenesis")); }
        Err(e) => log::error!("{e}"),
    }
}

/// OS のローカルデータフォルダ。Windows はコア・Unity と同じく環境変数 `LOCALAPPDATA` → `APPDATA` を先に見る
/// (既知フォルダ API は環境変数を見ないので、`LOCALAPPDATA` の無い環境でコアとフォルダの場所がずれる)。
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

/// フロントのMAJORがコアのMAJOR未満なら起動できない(フロントの方が新しい分には構わない)。
/// コアの版を読めないときも、互換であると推測しない。
pub fn check_frontend_version(core: &Value) -> Result<(), String> {
    let frontend = definition()["frontend_version"].as_str().unwrap_or_default().to_string();
    let core_version = core["core_version"].as_str().unwrap_or_default();
    if frontend_covers_core(&frontend, core_version) { Ok(()) }
    else { Err(format!("FRONTEND_VERSION_OUTDATED: core {core_version} / UI {frontend}")) }
}

fn frontend_covers_core(frontend: &str, core: &str) -> bool {
    matches!((major_of(frontend), major_of(core)), (Some(frontend), Some(core)) if frontend >= core)
}

fn major_of(version: &str) -> Option<u64> {
    version.split('.').next()?.parse().ok()
}

/// コアはアプリ実行中に差し替わらないため、取得成功後の版情報を起動中ずっと共有する。
/// 起動準備中の取得失敗は確定せず、次の初期取得で再試行できる。同時要求もOnceCellで1本にまとめる。
pub async fn core_info(state: &SharedState) -> Result<Value, String> {
    let (base, cache) = { let guard = state.lock(); (guard.api_base_url.clone(), guard.core_info.clone()) };
    cache.get_or_try_init(|| async {
        let response = reqwest::Client::new().get(format!("{}/api/v1/system/version", base.trim_end_matches('/')))
            .timeout(Duration::from_secs(10)).send().await.map_err(|e| e.to_string())?;
        response.error_for_status().map_err(|e| e.to_string())?.json().await.map_err(|e| e.to_string())
    }).await.cloned()
}

pub async fn verify_core(state: &SharedState) -> Result<(), String> {
    if !state.lock().backend_health.connected { return Err("BACKEND_DISCONNECTED".into()); }
    let error = state.lock().graphic_contract_error.clone();
    if let Some(error) = error { return Err(error); }
    core_info(state).await?;
    Ok(())
}

#[tauri::command]
pub async fn get_core_version(state: tauri::State<'_, SharedState>) -> Result<Value, String> {
    core_info(state.inner()).await
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
    let response = reqwest::Client::new().get(url).query(&[("template", template)]).timeout(Duration::from_secs(120))
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

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Read, Write};
    use std::net::TcpListener;

    fn mock_core(statuses: Vec<u16>) -> (SharedState, std::thread::JoinHandle<()>) {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let base = format!("http://{}", listener.local_addr().unwrap());
        let server = std::thread::spawn(move || {
            for status in statuses {
                let (mut stream, _) = listener.accept().unwrap();
                stream.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
                let mut request = Vec::new();
                let mut buffer = [0u8; 1024];
                while !request.ends_with(b"\r\n\r\n") {
                    let count = stream.read(&mut buffer).unwrap();
                    assert!(count > 0);
                    request.extend_from_slice(&buffer[..count]);
                }
                let request = String::from_utf8(request).unwrap();
                assert!(request.starts_with("GET /api/v1/system/version HTTP/1.1"));
                let body = json!({ "core_version": "1.0.0" }).to_string();
                write!(stream, "HTTP/1.1 {status} Test\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}", body.len()).unwrap();
            }
        });
        let state = SharedState::new(
            serde_json::from_str("{}").unwrap(), serde_json::from_str("{}").unwrap(),
            base, String::new(), String::new(),
        );
        state.lock().backend_health.observe(true, true);
        (state, server)
    }

    fn runtime() -> tokio::runtime::Runtime {
        tokio::runtime::Builder::new_current_thread().enable_all().build().unwrap()
    }

    #[test]
    fn startup_version_is_shared_across_concurrent_callers_and_later_operations() {
        let (state, server) = mock_core(vec![200]);
        runtime().block_on(async {
            let mut callers = Vec::new();
            for _ in 0..12 {
                let state = state.clone();
                callers.push(tokio::spawn(async move { core_info(&state).await.unwrap() }));
            }
            for caller in callers { assert_eq!(caller.await.unwrap()["core_version"], "1.0.0"); }
            // サーバ終了後の開始・ロード前確認も起動時の結果だけで通る。
            server.join().unwrap();
            for _ in 0..3 { verify_core(&state).await.unwrap(); }
            assert_eq!(core_info(&state).await.unwrap()["core_version"], "1.0.0");
        });
    }

    #[test]
    fn initial_unavailable_response_can_be_retried_until_first_success() {
        let (state, server) = mock_core(vec![503, 200]);
        runtime().block_on(async {
            assert!(core_info(&state).await.is_err());
            verify_core(&state).await.unwrap();
            server.join().unwrap();
            verify_core(&state).await.unwrap();
        });
    }

    #[test]
    fn frontend_must_not_be_older_than_core_major() {
        assert!(frontend_covers_core("1.0.0", "1.9.9"));
        assert!(frontend_covers_core("2.0.0", "1.9.9"));
        assert!(!frontend_covers_core("1.9.9", "2.0.0"));
        assert!(!frontend_covers_core("1.0.0", ""));
        assert!(!frontend_covers_core("1.0.0", "unknown"));
    }
}
