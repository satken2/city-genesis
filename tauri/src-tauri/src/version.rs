use crate::state::SharedState;
use serde_json::{json, Value};
use std::time::Duration;

pub fn definition() -> Value {
    serde_json::from_str(include_str!("../../../version.json")).expect("invalid version.json")
}

pub fn headers() -> reqwest::header::HeaderMap {
    let mut headers = reqwest::header::HeaderMap::new();
    headers.insert("X-City-Genesis-API", definition()["api_contract_version"].to_string().parse().unwrap());
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

#[tauri::command]
pub async fn export_scenario(state: tauri::State<'_, SharedState>, scenario_id: String, output_path: String) -> Result<(), String> {
    verify_core(state.inner()).await?;
    let target = std::path::PathBuf::from(output_path);
    if target.extension().and_then(|value| value.to_str()) != Some("cgscenario") {
        return Err("SCENARIO_PACKAGE_INVALID".into());
    }
    let base = state.lock().api_base_url.clone();
    let mut url = reqwest::Url::parse(&format!("{}/api/v1/scenarios/", base.trim_end_matches('/'))).map_err(|e| e.to_string())?;
    url.path_segments_mut().map_err(|_| "invalid API URL")?.pop_if_empty().push(&scenario_id).push("export");
    let response = client().get(url).timeout(Duration::from_secs(120)).send().await.map_err(|e| e.to_string())?;
    let bytes = response.error_for_status().map_err(|e| e.to_string())?.bytes().await.map_err(|e| e.to_string())?;
    std::fs::write(target, bytes).map_err(|e| e.to_string())
}
