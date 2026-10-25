mod asset_manager;
mod ipc;
mod messages;
mod settings;
mod state;
mod window;
mod version;
mod backend_health;
mod process;
mod client_stats;
#[cfg(feature = "steam")]
mod steam;

use messages::{EntityRef, MutexGroups, WindowRegistry};
use serde_json::Value;
use state::SharedState;
use std::sync::atomic::Ordering;
use std::time::Duration;
use tauri::{Emitter, LogicalPosition, LogicalSize, Manager, Monitor, WebviewUrl, WebviewWindowBuilder, WindowEvent};

// HTTP play modes. Send the same values as the core's consts.py.
const PLAY_MODE_NORMAL: i32 = 0;
const PLAY_MODE_SCENARIO: i32 = 1;
const PLAY_MODE_TUTORIAL: i32 = 2;
// verification_status in GET /sessions. Same value as the core's consts.py.
const GAME_VERIFIED: i64 = 0;

fn load_registry() -> WindowRegistry {
    serde_json::from_str(include_str!(concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/config/window-registry.json"
    )))
    .expect("failed to parse window-registry.json")
}

fn load_mutex_groups() -> MutexGroups {
    serde_json::from_str(include_str!(concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/config/window-mutex-groups.json"
    )))
    .expect("failed to parse window-mutex-groups.json")
}

/// Top menu size
const TOPMENU_WIDTH: f64 = 800.0;
const TOPMENU_HEIGHT: f64 = 600.0;

/// Hint bar height. Match the actual CSS display size (padding + border +
/// two text lines) exactly to avoid transparent, noninteractive margins.
const HINTBAR_HEIGHT: f64 = 68.0;
const TUTORIAL_HEIGHT: f64 = 200.0;

/// Monitor position and size in logical coordinates. Fall back to 1920x1080 at the origin if unavailable.
fn monitor_frame(monitor: Option<&Monitor>) -> (LogicalPosition<f64>, LogicalSize<f64>) {
    match monitor {
        Some(monitor) => {
            let scale = monitor.scale_factor();
            (monitor.position().to_logical(scale), monitor.size().to_logical(scale))
        }
        None => (LogicalPosition::new(0.0, 0.0), LogicalSize::new(1920.0, 1080.0)),
    }
}

/// Hint bar placement: top center, 55% of screen width (CSS fills the window width).
fn hintbar_placement(monitor: Option<&Monitor>) -> window::Placement {
    let (pos, size) = monitor_frame(monitor);
    let width = size.width * 0.55;
    window::Placement::on_monitor(
        monitor,
        LogicalSize::new(width, HINTBAR_HEIGHT),
        LogicalPosition::new(pos.x + (size.width - width) / 2.0, pos.y + 4.0),
    )
}

/// Tutorial placement: directly below the hint bar (4px top offset + hint bar + 4px gap), with the same width.
fn tutorial_placement(monitor: Option<&Monitor>) -> window::Placement {
    let (pos, size) = monitor_frame(monitor);
    let width = size.width * 0.55;
    window::Placement::on_monitor(
        monitor,
        LogicalSize::new(width, TUTORIAL_HEIGHT),
        LogicalPosition::new(pos.x + (size.width - width) / 2.0, pos.y + 4.0 + HINTBAR_HEIGHT + 4.0),
    )
}

#[tauri::command]
fn get_window_context(window: tauri::Window, state: tauri::State<'_, SharedState>) -> Result<messages::WindowContextDto, String> {
    window::get_window_context(state.inner(), window.label()).ok_or_else(|| "window context not found".to_string())
}

#[tauri::command]
fn close_current_window(window: tauri::Window, app: tauri::AppHandle, state: tauri::State<'_, SharedState>) -> Result<(), String> {
    window::close_from_frontend(&app, state.inner(), window.label())
}

#[tauri::command]
fn send_window_action(
    state: tauri::State<'_, SharedState>,
    window_type: String,
    entity: Option<EntityRef>,
    action: String,
    params: Value,
) -> Result<(), String> {
    require_backend(state.inner())?;
    ipc::send_ui_action(state.inner(), window_type, entity, action, params)
}

fn require_backend(state: &SharedState) -> Result<(), String> {
    if state.lock().backend_health.connected { Ok(()) }
    else { Err("BACKEND_DISCONNECTED".into()) }
}

fn publish_backend_connection(app: &tauri::AppHandle, state: &SharedState, connected: bool) {
    let _ = app.emit("backend-connection", serde_json::json!({ "connected": connected }));
    if state.lock().graphic_connected {
        let _ = ipc::send_ui_action(state, String::new(), None, "system:backend_connection".into(),
            serde_json::json!({ "connected": connected }));
    }
}

/// Tell Unity whether the core is verified so it shows its UNVERIFIED caption (debug runs count as verified).
/// Sent when Unity starts and when session polling observes a change; nothing before the first successful poll.
fn publish_verification(state: &SharedState) {
    let status = {
        let guard = state.lock();
        if !guard.graphic_connected { return; }
        session_verification_status(guard.session_state.as_ref())
    };
    let Some(status) = status else { return };
    let _ = ipc::send_ui_action(state, String::new(), None, "system:verification".into(),
        serde_json::json!({ "verified": status == GAME_VERIFIED }));
}

fn session_verification_status(session: Option<&Value>) -> Option<i64> {
    session?.get("verification_status")?.as_i64()
}

fn observe_backend(app: &tauri::AppHandle, state: &SharedState, success: bool) -> bool {
    let (action, connected, changed) = {
        let mut guard = state.lock();
        let previous = guard.backend_health.connected;
        let action = guard.backend_health.observe(success, cfg!(all(debug_assertions, dev)));
        guard.backend_health.connected = success && guard.graphic_contract_error.is_none()
            && guard.core_info.get().is_some();
        (action, guard.backend_health.connected, previous != guard.backend_health.connected)
    };
    if changed { publish_backend_connection(app, state, connected); }
    match action {
        backend_health::HealthAction::Exit => {
            log::warn!("[session-poll] 通信確認が2回連続で失敗したため終了します");
            process::request_app_exit(app, state);
            false
        }
        backend_health::HealthAction::ReturnToMenu => {
            log::warn!("[session-poll] 通信確認が2回連続で失敗しました。開発版は再接続を待ちます");
            return_to_topmenu(app, state);
            true
        }
        backend_health::HealthAction::Continue => true,
    }
}

#[tauri::command]
fn get_connection_status(state: tauri::State<'_, SharedState>) -> Value {
    window::get_connection_status(state.inner())
}

#[tauri::command]
fn begin_epoch_presentation(app: tauri::AppHandle, state: tauri::State<'_, SharedState>, id: String) -> Result<bool, String> {
    require_backend(state.inner())?;
    let connected = {
        let mut guard = state.lock();
        if guard.is_loading || guard.epoch_presentation_id.is_some() {
            return Err("エポック演出または初期ロードが進行中です".into());
        }
        guard.epoch_presentation_id = Some(id.clone());
        guard.graphic_connected
    };
    if connected {
        if let Err(error) = ipc::send_ui_action(state.inner(), "".into(), None, "epoch:prepare".into(), serde_json::json!({"id": id})) {
            window::finish_epoch_presentation(&app, state.inner(), &id);
            return Err(error);
        }
    }
    let _ = app.emit("epoch:executing", serde_json::json!({"executing": true}));
    Ok(connected)
}

#[tauri::command]
async fn set_epoch_tips_visible(app: tauri::AppHandle, state: tauri::State<'_, SharedState>, id: String, visible: bool) -> Result<(), String> {
    let monitor = {
        let guard = state.lock();
        if guard.epoch_presentation_id.as_deref() != Some(&id) { return Ok(()); }
        guard.current_monitor_index
    };
    if visible { window::open_tips_window_with_mode(&app, monitor, true)?; }
    else { window::close_tips_window(&app); }
    Ok(())
}

#[tauri::command]
fn finish_epoch_presentation(app: tauri::AppHandle, state: tauri::State<'_, SharedState>, id: String) {
    window::finish_epoch_presentation(&app, state.inner(), &id);
}

#[tauri::command]
fn is_epoch_presentation_current(state: tauri::State<'_, SharedState>, id: String) -> bool {
    state.lock().epoch_presentation_id.as_deref() == Some(&id)
}

/// Ask Unity to capture the 3D view immediately before saving save data or a scenario. Return false without sending if
/// Unity is disconnected (do not accumulate requests in the MMF; the UI saves without waiting for a capture). Unity uploads
/// the image directly to the core (`PUT /api/v1/sessions/thumbnail`) and replies with `camera:thumbnail_captured {id, success}`.
#[tauri::command]
fn request_thumbnail_capture(state: tauri::State<'_, SharedState>, id: String) -> Result<bool, String> {
    if !state.lock().graphic_connected {
        return Ok(false);
    }
    ipc::send_ui_action(state.inner(), "".into(), None, "camera:capture_thumbnail".into(), serde_json::json!({"id": id}))?;
    Ok(true)
}

/// Match the icon panel height to React's measured content height. Since
/// the number of icons depends on scenario mode, React (`IconPanel`)
/// reports each content height change to avoid transparent window margins.
#[tauri::command]
fn resize_icon_panel(app: tauri::AppHandle, state: tauri::State<'_, SharedState>, height: f64) {
    window::resize_icon_panel(&app, state.inner(), height);
}

/// Have React report the system tray size, for the same reason as the icon panel height:
/// mode buttons change its width, the unverified label its height, and transparent margins must not intercept clicks.
#[tauri::command]
fn resize_system_tray(app: tauri::AppHandle, state: tauri::State<'_, SharedState>, width: f64, height: f64) {
    window::resize_system_tray(&app, state.inner(), width, height);
}

/// Fit the cockpit to its content width for the current language, date, and font size.
#[tauri::command]
fn resize_cockpit(app: tauri::AppHandle, state: tauri::State<'_, SharedState>, width: f64) {
    window::resize_cockpit(&app, state.inner(), width);
}

/// Return the top menu's initial state. Called by the
/// frontend TopMenu component during initialization.
#[tauri::command]
fn get_topmenu_state(state: tauri::State<'_, SharedState>) -> Value {
    let guard = state.lock();
    serde_json::json!({
        "apiBaseUrl": guard.api_base_url,
    })
}

/// Move the HUD (icon panel, cockpit, system tray, display tray), hint
/// bar, tutorial, and top menu to another monitor. direction=1 selects
/// the next monitor; -1 selects the previous one. Also move open
/// popups when their destination changes (in normal mode, or when the HUD
/// moves to the popup monitor). Do nothing if there is only one monitor.
#[tauri::command]
fn switch_monitor(app: tauri::AppHandle, state: tauri::State<'_, SharedState>, direction: i32) -> Result<Value, String> {
    let monitors: Vec<_> = app.available_monitors()
        .map_err(|err| err.to_string())?
        .into_iter()
        .collect();

    if monitors.len() <= 1 {
        return Ok(serde_json::json!({ "monitorIndex": 0 }));
    }

    let count = monitors.len();
    let mut new_index = 0;
    window::update_display_target(&app, state.inner(), |guard| {
        guard.current_monitor_index = if direction > 0 {
            (guard.current_monitor_index + 1) % count
        } else {
            (guard.current_monitor_index + count - 1) % count
        };
        new_index = guard.current_monitor_index;
    })?;

    let monitor = monitors.get(new_index).ok_or("monitor index out of range")?;
    window::layout_hud_windows(&app, &monitors, new_index, None, None, None);
    if let Some(hintbar) = app.get_webview_window("hintbar") {
        hintbar_placement(Some(monitor)).apply(&hintbar);
    }
    if let Some(tutorial) = app.get_webview_window("tutorial") {
        tutorial_placement(Some(monitor)).apply(&tutorial);
    }
    // Synchronize the position here so the top menu appears on the HUD monitor when shown after the game ends.
    if let Some(topmenu) = app.get_webview_window("topmenu") {
        window::centered_on_monitor(Some(monitor), TOPMENU_WIDTH, TOPMENU_HEIGHT).apply(&topmenu);
    }

    let mut settings = settings::load_settings(&app);
    settings.monitor_index = new_index;
    let _ = settings::save_settings(&app, &settings);

    Ok(serde_json::json!({ "monitorIndex": new_index }))
}

/// Toggle multi-display mode (showing popups on a monitor separate
/// from the HUD). Move open popups if their destination monitor changes.
#[tauri::command]
fn set_multi_display(app: tauri::AppHandle, state: tauri::State<'_, SharedState>, enabled: bool) -> Result<(), String> {
    window::update_display_target(&app, state.inner(), |guard| guard.multi_display = enabled)?;
    let mut settings = settings::load_settings(&app);
    settings.multi_display = enabled;
    settings::save_settings(&app, &settings)
}

/// In multi-display mode, move the popup destination to the next (direction>0) or previous monitor, skipping the HUD monitor.
#[tauri::command]
fn switch_popup_monitor(app: tauri::AppHandle, state: tauri::State<'_, SharedState>, direction: i32) -> Result<(), String> {
    let count = app.available_monitors().map_err(|err| err.to_string())?.len();
    let mut new_index = 0;
    window::update_display_target(&app, state.inner(), |guard| {
        let hud = guard.current_monitor_index;
        let current = window::resolve_popup_monitor(true, hud, guard.popup_monitor_index, count);
        guard.popup_monitor_index = window::next_popup_monitor(hud, current, count, direction);
        new_index = guard.popup_monitor_index;
    })?;
    let mut settings = settings::load_settings(&app);
    settings.popup_monitor_index = new_index;
    settings::save_settings(&app, &settings)
}

#[tauri::command]
fn load_app_settings(app: tauri::AppHandle) -> Result<settings::AppSettings, String> {
    Ok(settings::load_settings(&app))
}

#[tauri::command]
fn save_app_settings(app: tauri::AppHandle, settings: settings::AppSettings) -> Result<(), String> {
    settings::save_settings(&app, &settings)
}

/// Show or hide the hint bar window according to user settings.
///
/// Several startup paths show the hint bar; centralize the
/// decision to honor the hidden setting here. The Hintbar component
/// applies setting changes immediately by receiving `settings-updated`.
fn show_hintbar_if_enabled(app: &tauri::AppHandle) {
    if let Some(hintbar) = app.get_webview_window("hintbar") {
        if settings::load_settings(app).hintbar_visible {
            let _ = hintbar.show();
        } else {
            let _ = hintbar.hide();
        }
    }
}

/// Open the popup corresponding to a `window-registry.json` key (for taskbar shortcuts).
#[tauri::command]
async fn open_registered_window(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
    window_type: String,
    params: Option<serde_json::Value>,
    entity_id: Option<String>,
) -> Result<(), String> {
    use crate::messages::{EntityRef, OpenWindowParams, OpenWindowPayload};
    let entity = entity_id.map(|id| EntityRef {
        r#type: None,
        id: Some(id),
    });
    let open_params: Option<OpenWindowParams> =
        params.and_then(|v| serde_json::from_value(v).ok());
    let payload = OpenWindowPayload {
        window_type,
        entity,
        params: open_params,
    };
    window::open_window(app, state.inner().clone(), payload).await
}

/// Close the popup corresponding to a `window-registry.json` key if open. Used for
/// dependent windows (such as closing the 2D path shape editor when placement is confirmed).
#[tauri::command]
fn close_registered_window(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
    window_type: String,
) -> Result<(), String> {
    window::close_windows_by_type(&app, state.inner(), &window_type);
    Ok(())
}

/// Create a new game session (called after selecting a scenario or
/// tutorial). Ask Unity to clear the previous city, hide topmenu and
/// current popups, show Tips, then POST /api/v1/sessions to the backend.
///
/// Pass extra parameters (city name, language, simulation days, climate, ground cover, and asset
/// packs) to the backend as query parameters. When loading a scenario, only settings not locked by the
/// author may override scenario values (the core rejects different values for locked settings with 422).
/// `intro_dialog` is the content of the information window opened once the city appears.
#[tauri::command]
async fn create_session(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
    scenario_id: Option<String>,
    city_name: Option<String>,
    language: Option<String>,
    simulation_days: Option<u32>,
    play_mode: Option<i32>,
    tutorial_id: Option<String>,
    accept_balance_change: Option<bool>,
    climate: Option<i32>,
    ground_cover: Option<i32>,
    asset_pack: Option<String>,
    intro_dialog: Option<serde_json::Value>,
) -> Result<(), String> {
    let play_mode = play_mode.unwrap_or(PLAY_MODE_NORMAL);
    if !matches!(play_mode, PLAY_MODE_NORMAL | PLAY_MODE_SCENARIO | PLAY_MODE_TUTORIAL) {
        return Err("不正なプレイモードです。".to_string());
    }
    log::info!(
        "[create_session] scenario_id={:?} city_name={:?} language={:?} simulation_days={:?} play_mode={} tutorial_id={:?} climate={:?} ground_cover={:?} asset_pack={:?}",
        scenario_id, city_name, language, simulation_days, play_mode, tutorial_id,
        climate, ground_cover, asset_pack
    );

    version::verify_core(state.inner()).await?;
    if let Some(id) = &scenario_id {
        version::validate_load(state.inner(), id, true, accept_balance_change.unwrap_or(false)).await?;
    }

    version::prepare_graphic(state.inner()).await?;
    require_backend(state.inner())?;

    if let Some(topmenu) = app.get_webview_window("topmenu") {
        let _ = topmenu.hide();
    }

    // Close all open popups (such as NewGameWindow).
    window::close_all_popup_windows(&app, state.inner());

    begin_initial_loading(&app, state.inner(), scenario_id.is_none() && play_mode == PLAY_MODE_NORMAL)?;

    {
        let mut guard = state.lock();
        guard.is_loading = true;
        guard.tutorial_id = if play_mode == PLAY_MODE_TUTORIAL {
            tutorial_id.clone().or_else(|| Some("controls".to_string()))
        } else {
            None
        };
        guard.intro_dialog = intro_dialog;
    }

    let api_base_url = {
        let guard = state.lock();
        guard.api_base_url.clone()
    };
    let base = api_base_url.trim_end_matches('/');
    let url = format!("{}/api/v1/sessions", base);

    // Build query parameters. Since reqwest's
    // .query() omits serde_json::Value::Null,
    // explicitly wrap each parameter in Option.
    #[derive(serde::Serialize)]
    struct SessionParams {
        scenario_id: Option<String>,
        city_name: Option<String>,
        language: Option<String>,
        simulation_days: Option<u32>,
        play_mode: i32,
        tutorial_id: Option<String>,
        accept_balance_change: bool,
        climate: Option<i32>,
        ground_cover: Option<i32>,
        asset_pack: Option<String>,
    }
    let query_params = SessionParams {
        scenario_id: scenario_id.clone(),
        city_name: city_name.clone(),
        language: language.clone(),
        simulation_days,
        play_mode,
        tutorial_id: tutorial_id.clone(),
        accept_balance_change: accept_balance_change.unwrap_or(false),
        climate,
        ground_cover,
        asset_pack,
    };

    let client = reqwest::Client::new();
    let res = client
        .post(&url)
        .query(&query_params)
        .timeout(Duration::from_secs(60))
        .send()
        .await
        .map_err(|e| { abort_initial_loading(&app, state.inner()); format!("セッション作成リクエストに失敗しました: {e}") })?;

    if !res.status().is_success() {
        let status = res.status();
        let body = res.text().await.unwrap_or_default();
        log::error!("[create_session] セッション作成失敗: {} {}", status, body);
        abort_initial_loading(&app, state.inner());
        if let Some(topmenu) = app.get_webview_window("topmenu") {
            let _ = topmenu.show();
        }
        {
            let mut guard = state.lock();
            guard.is_loading = false;
        }
        return Err(format!("セッション作成に失敗しました: {status} {body}"));
    }

    // The initial camera view bypasses the UI. On game:start, the graphics module
    // reads camera (stored in save data or the scenario) directly from GET /api/v1/city.
    log::info!("[create_session] セッション作成リクエスト成功。ポーリング待機に委譲");
    state.lock().backend_load_complete = true;
    Ok(())
}

/// Load save data and start a game session. Ask
/// Unity to clear the previous city, hide topmenu,
/// show Tips, then send a load request to the backend.
#[tauri::command]
async fn start_load_game(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
    save_id: String,
    accept_balance_change: Option<bool>,
) -> Result<(), String> {
    log::info!("[start_load_game] save_id={} でロード開始", save_id);

    version::validate_load(state.inner(), &save_id, false, accept_balance_change.unwrap_or(false)).await?;
    version::prepare_graphic(state.inner()).await?;
    require_backend(state.inner())?;

    if let Some(topmenu) = app.get_webview_window("topmenu") {
        let _ = topmenu.hide();
    }

    // Close all open popups when loading is confirmed. Clear the load
    // screen itself and any previous session's detail panels, which become
    // unrelated to the loaded data when loading another save during a game.
    window::close_all_popup_windows(&app, state.inner());

    begin_initial_loading(&app, state.inner(), false)?;


    let api_base_url = {
        let guard = state.lock();
        guard.api_base_url.clone()
    };
    let url = format!("{}/api/v1/sessions", api_base_url.trim_end_matches('/'));

    let client = reqwest::Client::new();
    let res = client
        .post(&url)
        .query(&[("save_id", save_id.as_str()), ("accept_balance_change", if accept_balance_change.unwrap_or(false) { "true" } else { "false" })])
        .timeout(Duration::from_secs(60))
        .send()
        .await
        .map_err(|e| { abort_initial_loading(&app, state.inner()); format!("ロードリクエストに失敗しました: {e}") })?;

    if !res.status().is_success() {
        let status = res.status();
        let body = res.text().await.unwrap_or_default();
        log::error!("[start_load_game] ロード失敗: {} {}", status, body);
        abort_initial_loading(&app, state.inner());
        if let Some(topmenu) = app.get_webview_window("topmenu") {
            let _ = topmenu.show();
        }
        {
            let mut guard = state.lock();
            guard.is_loading = false;
        }
        return Err(format!("ロードに失敗しました: {status} {body}"));
    }

    log::info!("[start_load_game] ロードリクエスト成功。ポーリング待機に委譲");
    state.lock().backend_load_complete = true;
    Ok(())
}

/// Open the save data directory (`<user folder>/saves`, matching the core's CITY_SAVE_DIR) in the OS file explorer.
#[tauri::command]
async fn open_save_directory() -> Result<(), String> {
    let dir = version::user_data_dir().ok_or("ユーザーフォルダを解決できません")?.join("saves");
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;

    #[cfg(target_os = "windows")]
    {
        std::process::Command::new("explorer")
            .arg(&dir)
            .spawn()
            .map_err(|e| e.to_string())?;
    }
    #[cfg(target_os = "macos")]
    {
        std::process::Command::new("open")
            .arg(&dir)
            .spawn()
            .map_err(|e| e.to_string())?;
    }
    #[cfg(not(any(target_os = "windows", target_os = "macos")))]
    {
        std::process::Command::new("xdg-open")
            .arg(&dir)
            .spawn()
            .map_err(|e| e.to_string())?;
    }

    Ok(())
}

/// Return the terrain editor working copy directory (`<user folder>/terrain_editor/`).
///
/// Working copies only pass initial data between windows. Only save_scenario_terrain (the core
/// PUT) applies changes to the scenario (after a successful save, update the working copy to match).
fn terrain_editor_dir() -> Result<std::path::PathBuf, String> {
    let dir = version::user_data_dir().ok_or("ユーザーフォルダを解決できません")?.join("terrain_editor");
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    Ok(dir)
}

/// Check for a working copy ID (directory name). Accept only the lowercase, hyphenated
/// form produced by `Uuid::to_string()` so separators or `..` cannot escape terrain_editor/.
fn is_terrain_editor_id(id: &str) -> bool {
    uuid::Uuid::parse_str(id).is_ok_and(|parsed| parsed.to_string() == id)
}

fn terrain_editor_copy_dir(editor_id: &str) -> Result<std::path::PathBuf, String> {
    if !is_terrain_editor_id(editor_id) {
        return Err(format!("地形エディタの作業コピーのIDが不正です: {editor_id}"));
    }
    Ok(terrain_editor_dir()?.join(editor_id))
}

fn read_terrain_editor_meta(dir: &std::path::Path) -> Result<Value, String> {
    let text = std::fs::read_to_string(dir.join("meta.json"))
        .map_err(|e| format!("meta.json の読み込みに失敗しました: {e}"))?;
    serde_json::from_str(&text).map_err(|e| format!("meta.json の形式が不正です: {e}"))
}

/// Return all working copy IDs held by terrain editor windows, including visible and hidden windows awaiting reuse.
fn terrain_editor_window_ids(state: &SharedState) -> (Vec<String>, std::collections::HashSet<String>) {
    let guard = state.lock();
    let mut visible = Vec::new();
    let mut all = std::collections::HashSet::new();
    for window in guard.open_windows.values().filter(|w| w.window_type == "terrain_editor") {
        let Some(id) = window.entity.as_ref().and_then(|e| e.id.clone()) else { continue };
        if !window.hidden {
            visible.push(id.clone());
        }
        all.insert(id);
    }
    (visible, all)
}

/// Delete working copies older than one hour that are not associated with any
/// window. The age check protects newly created copies whose windows have not opened yet.
fn remove_stale_terrain_editor_copies(root: &std::path::Path, referenced: &std::collections::HashSet<String>) {
    let Ok(entries) = std::fs::read_dir(root) else { return };
    for entry in entries.flatten() {
        let name = entry.file_name().to_string_lossy().into_owned();
        let stale = entry
            .metadata()
            .and_then(|metadata| metadata.modified())
            .ok()
            .and_then(|modified| modified.elapsed().ok())
            .is_some_and(|age| age > Duration::from_secs(3600));
        if stale && is_terrain_editor_id(&name) && !referenced.contains(&name) {
            if let Err(e) = std::fs::remove_dir_all(entry.path()) {
                log::warn!("古い地形エディタの作業コピーを削除できません: {name}: {e}");
            }
        }
    }
}

/// URL for the scenario terrain API (`/api/v1/scenarios/{id}/terrain`). Encode the ID as a single path segment.
fn scenario_terrain_url(state: &SharedState, scenario_id: &str) -> Result<reqwest::Url, String> {
    let base = state.lock().api_base_url.clone();
    let mut url = reqwest::Url::parse(&format!("{}/api/v1/scenarios/", base.trim_end_matches('/')))
        .map_err(|e| e.to_string())?;
    url.path_segments_mut()
        .map_err(|_| "invalid API URL")?
        .pop_if_empty()
        .push(scenario_id)
        .push("terrain");
    Ok(url)
}

/// Convert a core error response to a `CODE: body` string. The UI uses the leading code to find a translation (saveError in `utils/saves.ts`).
async fn scenario_api_error(response: reqwest::Response) -> String {
    let status = response.status();
    let data: Value = response.json().await.unwrap_or(Value::Null);
    let body = data.get("detail").filter(|detail| detail.is_object()).unwrap_or(&data);
    match body.get("code").and_then(Value::as_str) {
        Some(code) => format!("{code}: {}", body.get("message").and_then(Value::as_str).unwrap_or_default()),
        None => format!("HTTP {status}"),
    }
}

/// Response from GET /api/v1/scenarios/{id}/terrain. The core's scenario terrain API defines the contract.
#[derive(serde::Deserialize)]
struct ScenarioTerrainDocument {
    settings: ScenarioTerrainSettings,
    /// Base64-encoded float16 LE near-field elevations (row-major, grid_rows×grid_cols).
    near_base64: String,
    #[serde(default)]
    water_surfaces: Value,
    #[serde(default)]
    rivers: Value,
    #[serde(default)]
    resources: Value,
    #[serde(default)]
    props: Value,
    #[serde(default)]
    camera: Value,
    #[serde(default)]
    ground_cover: Value,
    #[serde(default)]
    climate: Value,
    /// Terrain source (the imported elevation filename or tutorial setting name). Used only for display.
    #[serde(default)]
    terrain_source: Value,
    #[serde(default)]
    title: Value,
    locks: ScenarioTerrainLocks,
}

#[derive(serde::Deserialize)]
struct ScenarioTerrainSettings {
    map_width: f64,
    map_height: f64,
    near_spacing: f64,
    far_spacing: f64,
    cell_size: f64,
    grid_rows: f64,
    grid_cols: f64,
}

/// Scenarios with construction (roads, buildings, grading traces, or reclaimed land) cannot change elevations, water surfaces, or rivers. reasons lists the reason codes.
#[derive(serde::Deserialize, serde::Serialize)]
struct ScenarioTerrainLocks {
    heights_locked: bool,
    #[serde(default)]
    reasons: Vec<Value>,
}

/// Create a working copy (meta.json contents and near.bin bytes) from the GET response. Reject elevation byte counts that do not match the grid.
fn scenario_terrain_working_copy(scenario_id: &str, document: ScenarioTerrainDocument) -> Result<(Value, Vec<u8>), String> {
    use base64::Engine;
    let ScenarioTerrainDocument {
        settings, near_base64, water_surfaces, rivers, resources, props, camera,
        ground_cover, climate, terrain_source, title, locks,
    } = document;
    let near = base64::engine::general_purpose::STANDARD
        .decode(&near_base64)
        .map_err(|e| format!("標高データのデコードに失敗しました: {e}"))?;
    let grid = |count: f64| (count.fract() == 0.0 && (2.0..=u32::MAX as f64).contains(&count)).then_some(count as u64);
    let (rows, cols) = (grid(settings.grid_rows), grid(settings.grid_cols));
    let expected = rows.zip(cols).and_then(|(rows, cols)| rows.checked_mul(cols)?.checked_mul(2));
    let (Some(rows), Some(cols)) = (rows, cols) else {
        return Err(format!("標高データの格子数が不正です({}×{})", settings.grid_cols, settings.grid_rows));
    };
    if expected != Some(near.len() as u64) {
        return Err(format!("標高データのサイズが格子({cols}×{rows})と一致しません({} バイト)", near.len()));
    }
    let array = |value: Value| if value.is_array() { value } else { Value::Array(Vec::new()) };
    let props = if props.is_object() { props } else { serde_json::json!({"instances": [], "scatters": []}) };
    let meta = serde_json::json!({
        "scenario_id": scenario_id,
        "terrain_source": terrain_source,
        "title": title,
        "locks": locks,
        "map_width": settings.map_width,
        "map_height": settings.map_height,
        "near_spacing": settings.near_spacing,
        "far_spacing": settings.far_spacing,
        "cell_size": settings.cell_size,
        "grid_rows": rows,
        "grid_cols": cols,
        "water_surfaces": array(water_surfaces),
        "rivers": array(rivers),
        "resources": array(resources),
        "props": props,
        "camera": camera,
        "ground_cover": ground_cover,
        "climate": climate,
    });
    Ok((meta, near))
}

/// Export scenario terrain to a terrain editor working copy and return its ID (editor_id).
///
/// If a terrain editor for the same scenario is visible, return its ID to preserve unsaved edits on reopening and prevent
/// two windows from overwriting each other's saves. During a game session, the core rejects this with 409 SESSION_ACTIVE.
#[tauri::command]
async fn prepare_scenario_terrain_editor(
    state: tauri::State<'_, SharedState>,
    scenario_id: String,
) -> Result<String, String> {
    let root = terrain_editor_dir()?;
    let (visible, referenced) = terrain_editor_window_ids(state.inner());
    let opened = visible.into_iter().find(|id| {
        is_terrain_editor_id(id)
            && read_terrain_editor_meta(&root.join(id)).is_ok_and(|meta| meta["scenario_id"] == scenario_id.as_str())
    });
    if let Some(editor_id) = opened {
        return Ok(editor_id);
    }
    remove_stale_terrain_editor_copies(&root, &referenced);

    let url = scenario_terrain_url(state.inner(), &scenario_id)?;
    let response = reqwest::Client::new()
        .get(url)
        .timeout(Duration::from_secs(120))
        .send()
        .await
        .map_err(|e| format!("シナリオの地形を取得できません: {e}"))?;
    if !response.status().is_success() {
        return Err(scenario_api_error(response).await);
    }
    let document: ScenarioTerrainDocument = response
        .json()
        .await
        .map_err(|e| format!("シナリオの地形の形式が不正です: {e}"))?;
    let (meta, near) = scenario_terrain_working_copy(&scenario_id, document)?;

    let editor_id = uuid::Uuid::new_v4().to_string();
    let dir = root.join(&editor_id);
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    std::fs::write(dir.join("near.bin"), &near)
        .map_err(|e| format!("near.bin の書き込みに失敗しました: {e}"))?;
    std::fs::write(dir.join("meta.json"), meta.to_string())
        .map_err(|e| format!("meta.json の書き込みに失敗しました: {e}"))?;
    log::info!("[prepare_scenario_terrain_editor] scenario_id={} editor_id={}", scenario_id, editor_id);
    Ok(editor_id)
}

/// Return value of load_terrain_editor_data: the working copy's meta.json plus base64-encoded elevations from near.bin. The
/// public docs `D_地形形状.md` §D.7–D.9 define water surface, river, resource, prop, and camera formats (`M_地形プロップ.md` for props).
#[derive(serde::Serialize)]
struct TerrainEditorData {
    scenario_id: String,
    terrain_source: Value,
    title: Value,
    /// `{heights_locked, reasons}`. When heights_locked is true, elevations, water surfaces, and rivers cannot be saved.
    locks: Value,
    map_width: f64,
    map_height: f64,
    near_spacing: f64,
    far_spacing: f64,
    cell_size: f64,
    grid_rows: u32,
    grid_cols: u32,
    /// Base64-encoded float16 LE byte sequence.
    data_base64: String,
    water_surfaces: Value,
    rivers: Value,
    /// id is the resource DB ID as a string. On save, the core applies a diff by ID (missing IDs are new resources).
    resources: Value,
    props: Value,
    /// null if unset.
    camera: Value,
    /// null if unset; the frontend treats it as grassland.
    ground_cover: Value,
    /// null if unset; the frontend treats it as humid subtropical.
    climate: Value,
}

/// Read a terrain editor working copy. near.bin is large, so use an async command to avoid blocking the main thread.
#[tauri::command]
async fn load_terrain_editor_data(editor_id: String) -> Result<TerrainEditorData, String> {
    use base64::Engine;
    let dir = terrain_editor_copy_dir(&editor_id)?;
    let meta = read_terrain_editor_meta(&dir)?;
    let bytes = std::fs::read(dir.join("near.bin"))
        .map_err(|e| format!("near.bin の読み込みに失敗しました: {e}"))?;
    let number = |key: &str| meta.get(key).and_then(Value::as_f64).unwrap_or(0.0);
    let value = |key: &str| meta.get(key).cloned().unwrap_or(Value::Null);
    let array = |key: &str| meta.get(key).filter(|v| v.is_array()).cloned().unwrap_or_else(|| Value::Array(Vec::new()));
    Ok(TerrainEditorData {
        scenario_id: meta.get("scenario_id").and_then(Value::as_str).unwrap_or_default().to_string(),
        terrain_source: value("terrain_source"),
        title: value("title"),
        locks: value("locks"),
        map_width: number("map_width"),
        map_height: number("map_height"),
        near_spacing: number("near_spacing"),
        far_spacing: number("far_spacing"),
        cell_size: number("cell_size"),
        grid_rows: number("grid_rows") as u32,
        grid_cols: number("grid_cols") as u32,
        data_base64: base64::engine::general_purpose::STANDARD.encode(&bytes),
        water_surfaces: array("water_surfaces"),
        rivers: array("rivers"),
        resources: array("resources"),
        props: meta
            .get("props")
            .filter(|v| v.is_object())
            .cloned()
            .unwrap_or_else(|| serde_json::json!({"instances": [], "scatters": []})),
        camera: value("camera"),
        ground_cover: value("ground_cover"),
        climate: value("climate"),
    })
}

/// Fields of the PUT /api/v1/scenarios/{id}/terrain response used by the terrain editor.
#[derive(serde::Deserialize, serde::Serialize)]
struct ScenarioTerrainSaveResult {
    content_revision: i64,
    /// Lock state evaluated before saving.
    locks: ScenarioTerrainLocks,
    /// Newly created resources as {temporary editor ID: DB ID string}.
    resource_ids: std::collections::HashMap<String, String>,
}

/// Working copy meta.json reflecting saved content. Replace temporary resource IDs with DB IDs
/// (otherwise, saving after rereading the working copy would make the core delete and recreate
/// resources). Replace water surfaces and rivers only when sent (non-null); the core also treats null as unchanged.
fn saved_terrain_editor_meta(mut meta: Value, body: &Value, result: &ScenarioTerrainSaveResult) -> Value {
    let mut resources = body["resources"].clone();
    if let Some(entries) = resources.as_array_mut() {
        for entry in entries.iter_mut().filter_map(Value::as_object_mut) {
            let saved_id = entry.get("id").and_then(Value::as_str).and_then(|id| result.resource_ids.get(id)).cloned();
            if let Some(saved_id) = saved_id {
                entry.insert("id".into(), Value::String(saved_id));
            }
        }
    }
    if let Some(fields) = meta.as_object_mut() {
        for key in ["props", "camera", "ground_cover", "climate"] {
            fields.insert(key.into(), body[key].clone());
        }
        for key in ["water_surfaces", "rivers"] {
            if !body[key].is_null() {
                fields.insert(key.into(), body[key].clone());
            }
        }
        fields.insert("resources".into(), resources);
        fields.insert("locks".into(), serde_json::to_value(&result.locks).unwrap_or(Value::Null));
    }
    meta
}

/// Update the working copy (near.bin and meta.json) with successfully saved content. Write elevations only when supplied.
fn write_saved_terrain_editor_copy(
    dir: &std::path::Path,
    meta: Value,
    body: &Value,
    result: &ScenarioTerrainSaveResult,
) -> Result<(), String> {
    use base64::Engine;
    if let Some(near) = body["near_base64"].as_str() {
        let bytes = base64::engine::general_purpose::STANDARD
            .decode(near)
            .map_err(|e| format!("標高データのデコードに失敗しました: {e}"))?;
        std::fs::write(dir.join("near.bin"), bytes).map_err(|e| format!("near.bin の書き込みに失敗しました: {e}"))?;
    }
    std::fs::write(dir.join("meta.json"), saved_terrain_editor_meta(meta, body, result).to_string())
        .map_err(|e| format!("meta.json の書き込みに失敗しました: {e}"))
}

/// Save terrain editor changes to the scenario (PUT /api/v1/scenarios/{id}/terrain).
///
/// Resolve the target scenario from the working copy's meta.json, never from the UI. Elevations, water surfaces, and rivers
/// must be sent as None (null) while locked; reject violations here too. The core makes final checks such as rejecting
/// dimension changes. On success, update the working copy to match and return the response (version, locks, and new resource IDs).
#[tauri::command]
async fn save_scenario_terrain(
    state: tauri::State<'_, SharedState>,
    editor_id: String,
    near_base64: Option<String>,
    water_surfaces: Option<Value>,
    rivers: Option<Value>,
    resources: Value,
    props: Value,
    camera: Option<Value>,
    ground_cover: i64,
    climate: Value,
) -> Result<ScenarioTerrainSaveResult, String> {
    require_backend(state.inner())?;
    let dir = terrain_editor_copy_dir(&editor_id)?;
    let meta = read_terrain_editor_meta(&dir)?;
    let scenario_id = meta["scenario_id"]
        .as_str()
        .filter(|id| !id.is_empty())
        .ok_or("作業コピーにシナリオIDがありません")?
        .to_string();
    let heights_locked = meta["locks"]["heights_locked"].as_bool().unwrap_or(true);
    if heights_locked && (near_base64.is_some() || water_surfaces.is_some() || rivers.is_some()) {
        return Err("TERRAIN_LOCKED: 建設物があるシナリオでは標高・水面・川を保存できません".into());
    }
    let body = serde_json::json!({
        "near_base64": near_base64,
        "water_surfaces": water_surfaces,
        "rivers": rivers,
        "resources": resources,
        "props": props,
        "camera": camera,
        "ground_cover": ground_cover,
        "climate": climate,
    });
    let url = scenario_terrain_url(state.inner(), &scenario_id)?;
    let response = reqwest::Client::new()
        .put(url)
        .json(&body)
        .timeout(Duration::from_secs(600))
        .send()
        .await
        .map_err(|e| format!("シナリオへ保存できません: {e}"))?;
    if !response.status().is_success() {
        return Err(scenario_api_error(response).await);
    }
    let result: ScenarioTerrainSaveResult = response
        .json()
        .await
        .map_err(|e| format!("シナリオへの保存の応答の形式が不正です: {e}"))?;
    log::info!(
        "[save_scenario_terrain] scenario_id={} editor_id={} content_revision={} new_resources={}",
        scenario_id, editor_id, result.content_revision, result.resource_ids.len()
    );
    if let Err(e) = write_saved_terrain_editor_copy(&dir, meta, &body, &result) {
        log::warn!("[save_scenario_terrain] 作業コピーを保存した内容に合わせられません: {e}");
    }
    Ok(result)
}

#[cfg(test)]
mod terrain_editor_tests {
    use super::{
        is_terrain_editor_id, saved_terrain_editor_meta, scenario_terrain_working_copy, ScenarioTerrainDocument,
        ScenarioTerrainSaveResult,
    };
    use serde_json::json;

    const ID: &str = "0f8fad5b-d9cb-469f-a165-70867728950e";

    #[test]
    fn editor_id_accepts_only_canonical_uuid() {
        assert!(is_terrain_editor_id(ID));
        for bad in [
            String::new(),
            "..".to_string(),
            format!("../{ID}"),
            format!("{ID}/.."),
            ID.to_uppercase(),
            ID.replace('-', ""),
            format!("{{{ID}}}"),
            format!("urn:uuid:{ID}"),
        ] {
            assert!(!is_terrain_editor_id(&bad), "{bad}");
        }
    }

    fn document(near: &[u8], rows: u64, cols: u64) -> ScenarioTerrainDocument {
        use base64::Engine;
        serde_json::from_value(json!({
            "settings": {
                "map_width": 10.0, "map_height": 10.0, "near_spacing": 10.0, "far_spacing": 200.0,
                "cell_size": 500.0, "grid_rows": rows, "grid_cols": cols,
            },
            "near_base64": base64::engine::general_purpose::STANDARD.encode(near),
            "water_surfaces": null,
            "rivers": [{"id": "r"}],
            "resources": [],
            "props": null,
            "camera": null,
            "ground_cover": 2,
            "climate": 6,
            "terrain_source": "flat",
            "title": "テスト",
            "locks": {"heights_locked": true, "reasons": ["PATHS_EXIST"]},
        }))
        .unwrap()
    }

    #[test]
    fn working_copy_keeps_scenario_and_locks() {
        let (meta, near) = scenario_terrain_working_copy("local~abc", document(&[0; 8], 2, 2)).unwrap();
        assert_eq!(near.len(), 8);
        assert_eq!(meta["scenario_id"], "local~abc");
        assert_eq!(meta["title"], "テスト");
        assert_eq!(meta["terrain_source"], "flat");
        assert_eq!(meta["locks"], json!({"heights_locked": true, "reasons": ["PATHS_EXIST"]}));
        assert_eq!(meta["water_surfaces"], json!([]));
        assert_eq!(meta["rivers"], json!([{"id": "r"}]));
        assert_eq!(meta["props"], json!({"instances": [], "scatters": []}));
        assert_eq!(meta["grid_cols"], 2);
    }

    #[test]
    fn working_copy_rejects_elevation_size_mismatch() {
        assert!(scenario_terrain_working_copy("local~abc", document(&[0; 6], 2, 2)).is_err());
        assert!(scenario_terrain_working_copy("local~abc", document(&[0; 2], 1, 1)).is_err());
    }

    #[test]
    fn saved_meta_uses_database_ids_for_new_resources() {
        let meta = json!({
            "scenario_id": "local~abc",
            "locks": {"heights_locked": false, "reasons": []},
            "water_surfaces": [{"id": "lake"}],
            "rivers": [],
            "resources": [{"id": "7"}],
            "camera": {"x": 0.0},
        });
        let body = json!({
            "near_base64": null,
            "water_surfaces": null,
            "rivers": [{"id": "river"}],
            "resources": [{"id": "7", "resource_type": 1}, {"id": "resource_1", "resource_type": 2}],
            "props": {"instances": [], "scatters": []},
            "camera": null,
            "ground_cover": 3,
            "climate": 2,
        });
        let result: ScenarioTerrainSaveResult = serde_json::from_value(json!({
            "success": true, "id": "local~abc", "content_revision": 4,
            "locks": {"heights_locked": false, "reasons": []},
            "resource_ids": {"resource_1": "12"},
        }))
        .unwrap();
        let saved = saved_terrain_editor_meta(meta, &body, &result);
        assert_eq!(saved["scenario_id"], "local~abc");
        assert_eq!(saved["resources"], json!([{"id": "7", "resource_type": 1}, {"id": "12", "resource_type": 2}]));
        assert_eq!(saved["water_surfaces"], json!([{"id": "lake"}]), "送らなかった水面はそのまま");
        assert_eq!(saved["rivers"], json!([{"id": "river"}]));
        assert_eq!(saved["camera"], json!(null), "カメラの null は未設定に戻したことを表す");
        assert_eq!(saved["ground_cover"], 3);
        assert_eq!(saved["locks"], json!({"heights_locked": false, "reasons": []}));
    }
}

/// Exit the entire application (for the top menu's Exit button).
#[tauri::command]
fn exit_app(app: tauri::AppHandle, state: tauri::State<'_, SharedState>) -> Result<(), String> {
    let state = state.inner().clone();
    // Run on a separate thread to avoid blocking the UI thread while waiting for child processes to exit.
    std::thread::spawn(move || {
        process::request_app_exit(&app, &state);
    });
    Ok(())
}

/// Queue a notification popup. Show it
/// immediately if no notification is currently visible.
#[tauri::command]
fn show_notification(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
    message: String,
    icon_path: Option<String>,
    duration_ms: Option<u64>,
) -> Result<(), String> {
    let payload = crate::messages::NotificationPayload {
        message,
        icon_path: icon_path.unwrap_or_else(|| String::new()),
        duration_ms,
    };
    window::enqueue_notification(&app, state.inner(), payload);
    Ok(())
}

/// Manually close the notification popup and show the next queued notification.
///
/// Called by the frontend close button.
#[tauri::command]
async fn dismiss_notification(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
) -> Result<(), String> {
    window::dismiss_notification(&app, state.inner())
}

/// Receive the notification content height measured by the frontend and resize the window.
///
/// After measuring the actual rendered height, the frontend calls
/// this command to fit the window to the notification's text length.
#[tauri::command]
async fn resize_notification_window(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
    height: f64,
) -> Result<(), String> {
    window::resize_notification_window(&app, state.inner(), height)
}

/// Advance the load request generation and ignore completion notifications from the previous session.
fn begin_initial_loading(app: &tauri::AppHandle, state: &SharedState, needs_setup: bool) -> Result<(), String> {
    window::cancel_epoch_presentation(app, state);
    {
        let mut guard = state.lock();
        guard.loading_id += 1;
        guard.is_loading = true;
        guard.backend_load_complete = false;
        guard.loading_started = false;
        guard.graphic_load_ready = false;
        guard.needs_parameter_setup = needs_setup;
        guard.awaiting_parameter_setup = false;
        guard.tutorial_id = None;
        guard.intro_dialog = None;
    }
    window::set_hud_visible(app, false);
    if let Some(hintbar) = app.get_webview_window("hintbar") { let _ = hintbar.hide(); }
    let result = ipc::send_ui_action(state, "".into(), None, "game:loading".into(), serde_json::json!({}))
        .and_then(|_| window::open_tips_window(app, state));
    if result.is_err() { abort_initial_loading(app, state); }
    result
}

fn abort_initial_loading(app: &tauri::AppHandle, state: &SharedState) {
    {
        let mut guard = state.lock();
        guard.is_loading = false;
        guard.backend_load_complete = false;
        guard.loading_started = false;
        guard.graphic_load_ready = false;
        guard.needs_parameter_setup = false;
        // If a session still exists, rebuild the display on the next poll to recover.
        guard.last_session_id = None;
    }
    window::close_tips_window(app);
    if let Some(topmenu) = app.get_webview_window("topmenu") { let _ = topmenu.show(); }
}

/// game:start only starts rendering preparation. game:reveal allows the display to become visible.
fn send_game_start_ipc(state: &SharedState, _app: &tauri::AppHandle) {
    let loading_id = {
        let mut guard = state.lock();
        guard.loading_started = true;
        guard.graphic_load_ready = false;
        // UI development without Unity can start as soon as the backend is ready. If
        // connected, wait for rendering; omit this shortcut from distribution builds. Check
        // whether Unity is currently connected (graphic_connected), not whether it has ever
        // connected. The latter would wait forever on reload after stopping Play in the Editor.
        #[cfg(all(debug_assertions, dev))]
        {
            guard.graphic_load_ready = !guard.graphic_connected;
        }
        guard.loading_id
    };
    let _ = ipc::send_ui_action(state, "".into(), None, "session:reload".into(), serde_json::json!({}));
    let _ = ipc::send_ui_action(state, "".into(), None, "game:start".into(), serde_json::json!({"loading_id": loading_id}));
}

fn show_game_controls(app: &tauri::AppHandle, state: &SharedState) {
    let _ = app.emit("initial-loading:finished", ());
    window::set_hud_visible(app, true);
    show_hintbar_if_enabled(app);
    let tutorial_id = state.lock().tutorial_id.clone();
    if let Some(tid) = tutorial_id {
        if let Some(tutorial) = app.get_webview_window("tutorial") { let _ = tutorial.show(); }
        let _ = app.emit("tutorial:show", tid);
    }
    let intro_dialog = state.lock().intro_dialog.take();
    if let Some(extra) = intro_dialog {
        let payload = messages::OpenWindowPayload {
            window_type: "info_dialog".into(),
            entity: None,
            params: Some(messages::OpenWindowParams { preferred_position: None, extra: Some(extra) }),
        };
        // finish_parameter_setup runs on the main thread, so do not wait for window creation here.
        let (app, state) = (app.clone(), state.clone());
        tauri::async_runtime::spawn(async move {
            if let Err(err) = window::open_window(app, state, payload).await {
                log::error!("[show_game_controls] 案内ウィンドウを開けません: {err}");
            }
        });
    }
}

/// Command called after parameter setup for a new free-mode game. Close
/// parameter setup and show the HUD/Hintbar. Initial city loading is already complete.
#[tauri::command]
fn finish_parameter_setup(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
) -> Result<(), String> {
    {
        let mut guard = state.lock();
        if !guard.awaiting_parameter_setup { return Err("初期設定中ではありません".into()); }
        guard.awaiting_parameter_setup = false;
        guard.is_loading = false;
    }
    window::close_parameter_setup_window(&app);
    show_game_controls(&app, state.inner());
    Ok(())
}

/// Open parameter setup in editing mode from the icon panel during scenario authoring.
/// Unlike initial setup, completion only closes the window without sending game:start.
///
/// Must not be a synchronous command. Tauri runs synchronous commands on the main
/// thread, but `builder.build()` posts window creation to that thread's event
/// loop and waits. Blocking the event loop while waiting would freeze the entire app.
#[tauri::command]
async fn open_parameter_edit_window(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
) -> Result<(), String> {
    window::open_parameter_setup_window(&app, &state, true)
}

/// Return whether parameter setup opened in in-game editing mode. React
/// (ParameterSetupWindow) queries this on startup to choose its footer behavior.
#[tauri::command]
fn get_parameter_setup_mode(state: tauri::State<'_, SharedState>) -> bool {
    state.lock().parameter_setup_edit_mode
}

/// Close parameter setup in editing mode without sending game:start.
#[tauri::command]
fn close_parameter_setup_window(app: tauri::AppHandle) -> Result<(), String> {
    window::close_parameter_setup_window(&app);
    Ok(())
}

#[tauri::command]
fn get_initial_loading_ready(state: tauri::State<'_, SharedState>) -> bool {
    state.lock().graphic_load_ready
}

#[tauri::command]
async fn close_tips_window(app: tauri::AppHandle, state: tauri::State<'_, SharedState>) -> Result<(), String> {
    let (needs_setup, loading_id) = {
        let guard = state.lock();
        if !guard.is_loading || !guard.graphic_load_ready { return Err("初期ロードは完了していません".into()); }
        (guard.needs_parameter_setup, guard.loading_id)
    };
    // Prepare the next window first so Tips can retry even if creation fails.
    if needs_setup { window::open_parameter_setup_window(&app, state.inner(), false)?; }
    ipc::send_ui_action(state.inner(), "".into(), None, "game:reveal".into(), serde_json::json!({"loading_id": loading_id}))?;
    {
        let mut guard = state.lock();
        guard.graphic_load_ready = false;
        guard.needs_parameter_setup = false;
        guard.awaiting_parameter_setup = needs_setup;
        guard.is_loading = needs_setup;
    }
    window::close_tips_window(&app);
    if !needs_setup { show_game_controls(&app, state.inner()); }
    Ok(())
}

#[tauri::command]
fn reset_window_geometries(app: tauri::AppHandle, state: tauri::State<'_, SharedState>) -> Result<(), String> {
    window::reset_window_geometries(&app, state.inner())
}

/// Clear the display and wait state on session loss or a persistent connection failure in development builds.
fn return_to_topmenu(app: &tauri::AppHandle, state: &SharedState) {
    {
        let mut guard = state.lock();
        guard.last_session_id = None;
        guard.session_state = None;
        guard.loading_id += 1;
        guard.is_loading = false;
        guard.backend_load_complete = false;
        guard.loading_started = false;
        guard.graphic_load_ready = false;
        guard.needs_parameter_setup = false;
        guard.awaiting_parameter_setup = false;
        guard.tutorial_id = None;
        guard.notification_queue.clear();
    }
    window::cancel_epoch_presentation(app, state);
    window::close_all_popup_windows(app, state);
    let _ = window::dismiss_notification(app, state);
    window::close_tips_window(app);
    window::close_parameter_setup_window(app);
    window::set_hud_visible(app, false);
    if let Some(hintbar) = app.get_webview_window("hintbar") { let _ = hintbar.hide(); }
    if let Some(tutorial) = app.get_webview_window("tutorial") { let _ = tutorial.hide(); }
    let _ = app.emit("tutorial:hide", ());
    if let Some(topmenu) = app.get_webview_window("topmenu") { let _ = topmenu.show(); }
    let _ = ipc::send_ui_action(state, "".into(), None, "game:end".into(), serde_json::json!({}));
}

/// session-state while the core is unreachable. The top menu shows itself and indicates connection failure on receipt.
fn disconnected_session_state(api_base_url: &str, core_version: &Value) -> Value {
    serde_json::json!({
        "current_save_id": null,
        "session": null,
        "city": null,
        "apiBaseUrl": api_base_url,
        "isLoading": false,
        "tutorialId": null,
        "coreVersion": core_version,
        "backendConnected": false,
    })
}

/// Start a thread to poll session state periodically and broadcast it to all windows.
///
/// Every five seconds, fetch `/api/v1/sessions` and, if a city exists,
/// `/api/v1/city/` from the backend; broadcast them to all WebView windows as
/// `session-state`. This avoids separate polling in TopMenu, HUD, Tips, and other components.
///
/// Start Unity's initial rendering after the backend is ready. Close
/// Tips after both Unity's game:loaded and the user's Start action.
fn start_session_polling(state: SharedState, app: tauri::AppHandle) {
    std::thread::spawn(move || {
        // Create the client outside the loop to retain the connection pool.
        // Even on localhost, recreating clients requires TCP connection setup
        // and makes timeouts more likely due to Python's GIL and connection queue.
        let client = reqwest::blocking::Client::builder()
            .timeout(Duration::from_secs(10))
            // Use a timeout shorter than Python's (uvicorn's)
            // keep-alive timeout to reduce brief request failures caused
            // by reusing idle connections already closed by the server.
            .pool_idle_timeout(Duration::from_secs(3))
            .pool_max_idle_per_host(10)
            .build()
            .unwrap_or_default();

        // Retry only until startup preparation finishes; reuse the same version information after retrieval.
        let core = loop {
            if state.lock().shutdown.load(Ordering::Relaxed) { return; }
            match tauri::async_runtime::block_on(version::core_info(&state)) {
                Ok(info) => break info,
                Err(error) => {
                    log::warn!("[startup] コアの版情報を取得できません: {}", error);
                    if !observe_backend(&app, &state, false) { return; }
                    let api_base_url = state.lock().api_base_url.clone();
                    let _ = app.emit("session-state", disconnected_session_state(&api_base_url, &Value::Null));
                    std::thread::sleep(Duration::from_secs(5));
                }
            }
        };
        // Startup is impossible if the frontend MAJOR is lower than the core MAJOR. Waiting cannot change the version, so exit without retrying.
        if let Err(error) = version::check_frontend_version(&core) {
            log::error!("[startup] {error}。終了します");
            process::request_app_exit(&app, &state);
            return;
        }

        loop {
            let (api_base_url, should_stop) = {
                let guard = state.lock();
                (guard.api_base_url.clone(), guard.shutdown.clone())
            };

            if should_stop.load(Ordering::Relaxed) {
                break;
            }

            if api_base_url.is_empty() {
                std::thread::sleep(Duration::from_secs(5));
                continue;
            }

            let base = api_base_url.trim_end_matches('/');

            // If the POST completes after this GET, do not adopt the stale response as the new session state.
            let (polled_loading_id, load_complete_before_poll, started_before_poll) = {
                let guard = state.lock();
                (guard.loading_id, guard.backend_load_complete, guard.loading_started)
            };

            // Keep connectivity checks independent of compatibility matching results.
            let session_data: Option<Value> = match client
                .get(format!("{}/api/v1/sessions", base))
                .send()
            {
                Ok(res) => {
                    let status = res.status();
                    if status.is_success() {
                        match res.text() {
                            Ok(body) => {
                                // Do not log raw successful responses on every poll; they would create noise.
                                match serde_json::from_str::<Value>(&body) {
                                    Ok(json) if json.is_object() && json.get("current_save_id").is_some() => Some(json),
                                    Ok(_) => { log::error!("[session-poll] セッション応答の形式が不正です"); None },
                                    Err(e) => {
                                        log::error!("[session-poll] JSONパース失敗: {} body={}", e, body);
                                        None
                                    }
                                }
                            }
                            Err(e) => {
                                log::error!("[session-poll] レスポンス読み取り失敗: {}", e);
                                None
                            }
                        }
                    } else {
                        let body = res.text().unwrap_or_default();
                        log::error!("[session-poll] GET /api/v1/sessions 失敗: status={} body={}", status, body);
                        None
                    }
                }
                Err(e) => {
                    // Classify and log reqwest errors in detail. Even localhost
                    // communication can time out, refuse connections, or fail DNS
                    // resolution. {:?} includes the source error chain, helping
                    // identify causes such as connection resets or pool inconsistencies.
                    let err_kind = if e.is_timeout() {
                        "TIMEOUT"
                    } else if e.is_connect() {
                        "CONNECT"
                    } else if e.is_request() {
                        "REQUEST"
                    } else if e.is_body() {
                        "BODY"
                    } else if e.is_decode() {
                        "DECODE"
                    } else {
                        "OTHER"
                    };
                    log::error!(
                        "[session-poll] GET /api/v1/sessions リクエスト失敗 [{}]: {:?}",
                        err_kind, e
                    );
                    None
                }
            };

            if session_data.is_none() {
                if !observe_backend(&app, &state, false) { break; }
                let guard = state.lock();
                // On a brief interruption, retain each screen's contents without rebroadcasting existing city data.
                if guard.last_session_id.is_none() && !guard.is_loading {
                    let _ = app.emit("session-state", disconnected_session_state(&api_base_url, &core["core_version"]));
                }
                drop(guard);
                std::thread::sleep(Duration::from_secs(5));
                continue;
            }
            // Even if an IPC contract mismatch blocks screen startup, do not count a successful HTTP response as a connection failure.
            if state.lock().graphic_contract_error.is_some() {
                if !observe_backend(&app, &state, true) { break; }
                std::thread::sleep(Duration::from_secs(5));
                continue;
            }

            let current_save_id = session_data
                .as_ref()
                .and_then(|d| d.get("current_save_id"))
                .and_then(|v| v.as_str());
            // Use current_save_id presence to detect game start/end, and session_id to detect switching cities. Save
            // As changes only current_save_id to the new file ID; do not mistake it for a session switch and reload.
            let current_session_id = current_save_id.and(
                session_data.as_ref().and_then(|d| d.get("session_id")).and_then(|v| v.as_str()));

            // Fetch city data for Taskbar's cityName / currentDate. Skip when there is no city, since
            // the core would only return 503. Failures do not affect session data. Always consume the
            // response body, even on failure. Dropping it unread causes an RST disconnect; the Windows
            // core's asyncio can leave the socket open (and core shutdown stalls waiting for those connections).
            let city_data: Option<Value> = current_save_id.and_then(|_| {
                let res = client.get(format!("{}/api/v1/city/", base)).send().ok()?;
                let ok = res.status().is_success();
                let body = res.bytes().ok()?;
                if ok { serde_json::from_slice(&body).ok() } else { None }
            });

            let (prev_session_id, is_loading) = {
                let guard = state.lock();
                (guard.last_session_id.clone(), guard.is_loading)
            };

            // City changes are logged by game start/end detection; log nothing if unchanged.
            let session_changed = prev_session_id.as_deref() != current_session_id;

            let load_completed = {
                let mut guard = state.lock();
                let ready = load_complete_before_poll && guard.backend_load_complete
                    && guard.loading_id == polled_loading_id && current_save_id.is_some();
                if ready { guard.backend_load_complete = false; }
                ready
            };
            // Detect disappearance during loading separately from normal city changes. Otherwise Unity cannot
            // report rendering completion, leaving Tips or initial setup unclosable and blocking interaction. No
            // new session is expected before the POST response; evaluate only GETs sent after readiness was confirmed.
            let session_lost_while_loading = is_loading && current_save_id.is_none()
                && (load_complete_before_poll || started_before_poll)
                && state.lock().loading_id == polled_loading_id;
            if (session_changed && !is_loading) || load_completed || session_lost_while_loading {
                let new_session_id = current_session_id.map(|s| s.to_string());
                {
                    let mut guard = state.lock();
                    guard.last_session_id = new_session_id.clone();
                }

                // non-null: game start or session switch
                if new_session_id.is_some() {
                    log::info!(
                        "[session-poll] current_save_id が設定されました。ゲーム開始を検知"
                    );

                    if !is_loading {
                        if session_changed { window::close_all_popup_windows(&app, &state); }
                        if let Err(error) = begin_initial_loading(&app, &state, false) {
                            log::error!("初期ロード画面を開けません: {error}");
                        }
                    }
                    // Even if the session is detected before the POST response, do not load until it is ready.
                    if load_completed || !is_loading { send_game_start_ipc(&state, &app); }

                } else {
                    // null: game end. The previous value may also be null if the session vanished during loading.
                    log::info!("[session-poll] current_save_id がクリアされました。ゲーム終了を検知");
                    return_to_topmenu(&app, &state);
                }
            }

            // Save session state.
            let verification_changed = {
                let mut guard = state.lock();
                let changed = session_verification_status(guard.session_state.as_ref())
                    != session_verification_status(session_data.as_ref());
                guard.session_state = session_data.clone();
                if !guard.is_loading
                    && session_data
                        .as_ref()
                        .and_then(|session| session.get("play_mode"))
                        .and_then(Value::as_i64)
                        != Some(PLAY_MODE_TUTORIAL as i64)
                {
                    guard.tutorial_id = None;
                }
                changed
            };
            if verification_changed { publish_verification(&state); }
            // On recovery, apply session disappearance or switches before allowing
            // interaction. This order prevents stale screens from updating the newly connected city.
            if !observe_backend(&app, &state, true) { break; }
            let (is_loading, tutorial_id) = {
                let guard = state.lock();
                (guard.is_loading, guard.tutorial_id.clone())
            };
            let _ = app.emit("session-state", serde_json::json!({
                "current_save_id": current_save_id,
                "session": session_data,
                "city": city_data,
                "apiBaseUrl": api_base_url,
                "isLoading": is_loading,
                "tutorialId": tutorial_id,
                "coreVersion": core["core_version"],
                "backendConnected": true,
            }));

            std::thread::sleep(Duration::from_secs(5));
        }
    });
}

/// Initialize logging. Create city-genesis/tauri.log in
/// the executable's directory and log DEBUG and higher
/// to both stdout and the file. File logging is
/// essential because Windows production builds have no console.
fn setup_logging() -> Result<(), fern::InitError> {
    let log_path = if let Ok(exe_path) = std::env::current_exe() {
        exe_path
            .parent()
            .map(|p| p.join("city-genesis/tauri.log"))
            .unwrap_or_else(|| "city-genesis/tauri.log".into())
    } else {
        "city-genesis/tauri.log".into()
    };

    // fern does not create directories, so prepare the parent first. Otherwise log
    // initialization always fails in development builds directly under target/debug.
    if let Some(parent) = log_path.parent() {
        std::fs::create_dir_all(parent)?;
    }

    fern::Dispatch::new()
        .format(|out, message, record| {
            out.finish(format_args!(
                "{}[{}][{}] {}",
                chrono::Local::now().format("[%Y-%m-%d %H:%M:%S]"),
                record.target(),
                record.level(),
                message
            ))
        })
        .level(log::LevelFilter::Debug)
        // reqwest's internal connection logs generate too much
        // output on each poll; restrict that crate to Warn and higher.
        .level_for("reqwest", log::LevelFilter::Warn)
        // tao continuously emits verbose event-loop DEBUG logs
        // (NewEvents/RedrawEventsCleared), filling the log; restrict it to Warn and higher.
        .level_for("tao", log::LevelFilter::Warn)
        .chain(std::io::stdout())
        .chain(fern::log_file(log_path)?)
        .apply()?;
    Ok(())
}

/// Receive frontend error logs. Record errors sent
/// from JavaScript window.onerror or
/// unhandledrejection to a file through Rust's logging system.
#[tauri::command]
fn log_frontend_error(message: String) {
    log::error!("[frontend] {}", message);
}

/// Receive frontend informational logs. Track progress in flows
/// such as epoch execution that can stop silently without displaying
/// an error (the WebView console is unavailable in production builds).
#[tauri::command]
fn log_frontend_info(message: String) {
    log::info!("[frontend] {}", message);
}

/// Copy the achievements recorded in the player profile to Steam. Does nothing without Steam integration.
#[tauri::command]
fn sync_steam_achievements(state: tauri::State<'_, SharedState>) {
    #[cfg(feature = "steam")]
    steam::sync_achievements(state.lock().api_base_url.clone());
    #[cfg(not(feature = "steam"))]
    let _ = state;
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    setup_logging().unwrap_or_else(|e| eprintln!("ログ初期化に失敗しました: {}", e));

    // Initialize store integration before child processes and pass store-managed folders to the core.
    #[allow(unused_mut)]
    let mut launch = process::Launch::resolve();
    #[cfg(feature = "steam")]
    launch.core_env.extend(steam::init());
    let shared_state = SharedState::new(load_registry(), load_mutex_groups(), launch);
    let setup_state = shared_state.clone();
    let event_state = shared_state.clone();

    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_dialog::init())
        .manage(shared_state.clone())
        .setup(move |app| {
            // Resolve the user folder that anchors save data and terrain editor working copies.
            version::init_user_data_dir(app.handle());

            if let Some(main_window) = app.get_webview_window("main") {
                let _ = main_window.hide();
            }

            // The cockpit sends volume to Unity immediately after loading, then only on settings changes or Unity
            // startup. Initialize IPC before creating windows, or that initial message is lost with "IPC not initialized".
            ipc::start_ipc(app.handle().clone(), setup_state.clone());

            let loaded_settings = settings::load_settings(app.handle());
            let monitors: Vec<_> = app.available_monitors()
                .unwrap_or_default()
                .into_iter()
                .collect();

            // Use the primary monitor if the saved monitor is disconnected (retain the setting so reconnecting restores it).
            let monitor_index = if loaded_settings.monitor_index < monitors.len() {
                loaded_settings.monitor_index
            } else {
                let primary = app.primary_monitor().ok().flatten();
                monitors
                    .iter()
                    .position(|m| primary.as_ref().is_some_and(|p| p.position() == m.position()))
                    .unwrap_or(0)
            };
            {
                let mut guard = setup_state.lock();
                guard.current_monitor_index = monitor_index;
                guard.multi_display = loaded_settings.multi_display;
                guard.popup_monitor_index = loaded_settings.popup_monitor_index;
            }

            let target_monitor = monitors.get(monitor_index)
                .cloned()
                .or_else(|| app.primary_monitor().unwrap_or_default());
            if target_monitor.is_none() {
                log::warn!("No monitor detected. Using default 1920x1080 positioning.");
            }
            let target_monitor = target_monitor.as_ref();

            // Create the top menu centered on target_monitor (the monitor matching
            // monitor_index in settings, or the primary monitor). The first
            // session-state shows it; display nothing while waiting for the core to start.
            let topmenu_builder = WebviewWindowBuilder::new(
                app,
                "topmenu",
                WebviewUrl::App("index.html".into()),
            )
            .title("City Genesis")
            .decorations(false)
            .transparent(true)
            .always_on_top(true)
            .resizable(false);
            window::build_placed(
                topmenu_builder,
                window::centered_on_monitor(target_monitor, TOPMENU_WIDTH, TOPMENU_HEIGHT),
                false,
            )
                .expect("failed to create topmenu window");

            // Create four HUD windows on target_monitor: left icon panel, bottom-center
            // cockpit, bottom-right system tray, and bottom-left display tray. Initially
            // hidden; show when polling GET /api/v1/sessions returns a non-null current_save_id.
            window::create_hud_windows(app.handle(), &monitors, monitor_index)
                .expect("failed to create HUD windows");

            // Create the hint bar at the top center of target_monitor.
            // Initially hidden; show according to settings when the game starts.
            let hintbar_builder = WebviewWindowBuilder::new(
                app,
                "hintbar",
                WebviewUrl::App("index.html".into()),
            )
            .title("Hintbar")
            .decorations(false)
            .transparent(true)
            .shadow(false)
            .always_on_top(true)
            .skip_taskbar(true)
            .resizable(false);
            let hintbar = window::build_placed(hintbar_builder, hintbar_placement(target_monitor), false)
                .expect("failed to create hintbar window");
            let _ = hintbar.set_ignore_cursor_events(true);

            // Create the tutorial window directly below the hint bar.
            // Initially hidden; show when starting a new game from a scenario.
            let tutorial_builder = WebviewWindowBuilder::new(
                app,
                "tutorial",
                WebviewUrl::App("index.html".into()),
            )
            .title("Tutorial")
            .decorations(false)
            .transparent(true)
            .shadow(false)
            .always_on_top(true)
            .skip_taskbar(true)
            .resizable(false);
            window::build_placed(tutorial_builder, tutorial_placement(target_monitor), false)
                .expect("failed to create tutorial window");

            // Pre-create the notification popup. Creating a window on
            // every notification would delay animation startup, so
            // prepare it hidden and call show() after its content is ready.
            let notification_builder = WebviewWindowBuilder::new(
                app,
                "notification",
                WebviewUrl::App("index.html".into()),
            )
            .title("Notification")
            .decorations(false)
            .transparent(true)
            .shadow(false)
            .always_on_top(true)
            .skip_taskbar(true)
            .resizable(false);
            window::build_placed(
                notification_builder,
                window::notification_placement(
                    target_monitor,
                    window::NOTIFICATION_MAX_HEIGHT,
                    window::hud_right_bottom_reserved(app.handle()),
                ),
                false,
            )
            .expect("failed to create notification window");

            // Start the core and graphics, then begin verification and session monitoring once the
            // core is ready. This thread subsequently monitors child processes until the app exits.
            let app_handle = app.handle().clone();
            let state = setup_state.clone();
            std::thread::spawn(move || {
                if !process::start_children(&app_handle, &state) {
                    return;
                }
                #[cfg(feature = "steam")]
                {
                    steam::start_verification(state.lock().api_base_url.clone());
                    steam::sync_achievements(state.lock().api_base_url.clone());
                }
                start_session_polling(state.clone(), app_handle.clone());
                process::watch_children(&app_handle, &state);
            });
            Ok(())
        })
        .on_window_event({
            move |window, event| {
                match event {
                    // Window close button (including Alt+F4). Closing
                    // main/topmenu/HUD windows requests application-wide
                    //   shutdown because they are primary surfaces; use the
                    //   common sequence (notify Unity → wait for core save and exit
                    // → exit self). Delegate managed popups to
                    //   close_from_frontend to retain hide+LRU reuse semantics. Let other
                    // dedicated windows (notification/hintbar/tutorial, etc.) pass through.
                    WindowEvent::CloseRequested { api, .. } => {
                        let label = window.label().to_string();
                        match label.as_str() {
                            "main" | "topmenu"
                            | window::LABEL_ICON_PANEL
                            | window::LABEL_COCKPIT
                            | window::LABEL_SYSTEM_TRAY
                            | window::LABEL_DISPLAY_TRAY => {
                                api.prevent_close();
                                let app = window.app_handle().clone();
                                let state = event_state.clone();
                                std::thread::spawn(move || {
                                    process::request_app_exit(&app, &state);
                                });
                            }
                            "parameter-setup" if event_state.lock().awaiting_parameter_setup => { api.prevent_close(); }
                            "notification" | "hintbar" | "tutorial" | "parameter-setup" => {}
                            "tips" if { let guard = event_state.lock(); guard.is_loading || guard.epoch_presentation_id.is_some() } => { api.prevent_close(); }
                            "tips" => {}
                            _ => {
                                let is_managed_popup = {
                                    let guard = event_state.lock();
                                    guard.open_windows.contains_key(&label)
                                };
                                if is_managed_popup {
                                    api.prevent_close();
                                    let _ = window::close_from_frontend(
                                        window.app_handle(),
                                        &event_state,
                                        &label,
                                    );
                                }
                            }
                        }
                    }
                    WindowEvent::Destroyed => {
                        window::on_window_destroyed(window.app_handle(), &event_state, window.label());
                    }
                    // Remember size during resizing, and position during movement (only for freely placed windows).
                    WindowEvent::Resized(_) => {
                        window::record_window_geometry(
                            window.app_handle(),
                            &event_state,
                            window.label(),
                            false,
                        );
                    }
                    WindowEvent::Moved(_) => {
                        window::record_window_geometry(
                            window.app_handle(),
                            &event_state,
                            window.label(),
                            true,
                        );
                    }
                    // A Tauri window losing focus usually means the user just
                    // clicked Unity's 3D view. If Unity enters the foreground as
                    // fullscreen, topmost UI windows may move behind it; reassert
                    // them now. Shell promotion occurs during Unity activation, so
                    // this can run too early. Reasserting on system:focus_gained
                    // (ipc.rs), sent after Unity gains focus, is the primary recovery path.
                    WindowEvent::Focused(false) => {
                        window::reassert_topmost(window.app_handle());
                    }
                    _ => {}
                }
            }
        })
        .invoke_handler(tauri::generate_handler![
            get_window_context,
            close_current_window,
            send_window_action,
            begin_epoch_presentation,
            set_epoch_tips_visible,
            finish_epoch_presentation,
            is_epoch_presentation_current,
            request_thumbnail_capture,
            get_connection_status,
            resize_icon_panel,
            resize_system_tray,
            resize_cockpit,
            get_topmenu_state,
            switch_monitor,
            set_multi_display,
            switch_popup_monitor,
            open_registered_window,
            close_registered_window,
            start_load_game,
            version::get_frontend_status,
            version::get_core_version,
            create_session,
            exit_app,
            load_app_settings,
            save_app_settings,
            log_frontend_error,
            log_frontend_info,
            sync_steam_achievements,
            show_notification,
            dismiss_notification,
            resize_notification_window,
            finish_parameter_setup,
            open_parameter_edit_window,
            get_parameter_setup_mode,
            close_parameter_setup_window,
            close_tips_window,
            get_initial_loading_ready,
            reset_window_geometries,
            prepare_scenario_terrain_editor,
            load_terrain_editor_data,
            save_scenario_terrain,
            open_save_directory,
            asset_manager::load_asset_weights,
            asset_manager::save_asset_weights,
            asset_manager::read_asset_thumbnail,
            client_stats::get_client_system_stats,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

