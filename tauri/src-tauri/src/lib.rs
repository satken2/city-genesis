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

// HTTPのプレイモード。コアのconsts.pyと同じ値で送る。
const PLAY_MODE_NORMAL: i32 = 0;
const PLAY_MODE_SCEARIO: i32 = 1;
const PLAY_MODE_TUTORIAL: i32 = 2;

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

/// トップメニューのサイズ
const TOPMENU_WIDTH: f64 = 800.0;
const TOPMENU_HEIGHT: f64 = 600.0;

/// ヒントバーの高さ。CSSでの実際の表示サイズ(padding + border + 2行テキスト)にぴったり合わせ、
/// 透明なクリックできない余白を作らない。
const HINTBAR_HEIGHT: f64 = 68.0;
const TUTORIAL_HEIGHT: f64 = 200.0;

/// モニターの論理座標での位置とサイズ。取得できなければ原点の 1920x1080 とみなす。
fn monitor_frame(monitor: Option<&Monitor>) -> (LogicalPosition<f64>, LogicalSize<f64>) {
    match monitor {
        Some(monitor) => {
            let scale = monitor.scale_factor();
            (monitor.position().to_logical(scale), monitor.size().to_logical(scale))
        }
        None => (LogicalPosition::new(0.0, 0.0), LogicalSize::new(1920.0, 1080.0)),
    }
}

/// ヒントバーの配置: 上部中央、幅は画面の55%(CSS側はウィンドウ幅いっぱいに追従する)。
fn hintbar_placement(monitor: Option<&Monitor>) -> window::Placement {
    let (pos, size) = monitor_frame(monitor);
    let width = size.width * 0.55;
    window::Placement::on_monitor(
        monitor,
        LogicalSize::new(width, HINTBAR_HEIGHT),
        LogicalPosition::new(pos.x + (size.width - width) / 2.0, pos.y + 4.0),
    )
}

/// チュートリアルの配置: ヒントバーの直下(上端4px + ヒントバー + 間隔4px)、幅はヒントバーと同じ。
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

/// セーブ・シナリオ保存の直前に、3Dビューの撮影をUnityへ頼む。
/// Unityが接続していなければ送らずに false を返す(不在の間の要求をMMFに溜めず、UIは撮影を待たずに保存する)。
/// Unityは撮った画像をコア(`PUT /api/v1/sessions/thumbnail`)へ直接送り、`camera:thumbnail_captured {id, success}` で返す。
#[tauri::command]
fn request_thumbnail_capture(state: tauri::State<'_, SharedState>, id: String) -> Result<bool, String> {
    if !state.lock().graphic_connected {
        return Ok(false);
    }
    ipc::send_ui_action(state.inner(), "".into(), None, "camera:capture_thumbnail".into(), serde_json::json!({"id": id}))?;
    Ok(true)
}

/// アイコンパネルの高さをReact側の実測値に合わせる。
/// アイコン数がテンプレートモードの有無で変わるため、中身の高さが変わるたびに
/// React(`IconPanel`)から通知させ、ウィンドウに透明な余白ができないようにする。
#[tauri::command]
fn resize_icon_panel(app: tauri::AppHandle, state: tauri::State<'_, SharedState>, height: f64) {
    window::resize_icon_panel(&app, state.inner(), height);
}

/// システムトレイの幅をReact側から通知させる(アイコンパネルの高さと同じ理由。
/// テンプレートモードのボタンで横幅が変わるため、透明な余白がクリックを吸わないようにする)。
#[tauri::command]
fn resize_system_tray(app: tauri::AppHandle, state: tauri::State<'_, SharedState>, width: f64) {
    window::resize_system_tray(&app, state.inner(), width);
}

/// コックピットを言語・日付・文字サイズに応じた内容幅に合わせる。
#[tauri::command]
fn resize_cockpit(app: tauri::AppHandle, state: tauri::State<'_, SharedState>, width: f64) {
    window::resize_cockpit(&app, state.inner(), width);
}

/// トップメニューの初期状態を返す
/// フロントエンドのTopMenuコンポーネントが初期化時に呼び出す
#[tauri::command]
fn get_topmenu_state(state: tauri::State<'_, SharedState>) -> Value {
    let guard = state.lock();
    serde_json::json!({
        "apiBaseUrl": guard.api_base_url,
    })
}

/// HUD(アイコンパネル・コックピット・システムトレイ・表示トレイ)とヒントバー・チュートリアル・
/// トップメニューを別のモニターに移動する。
/// directionが1なら次のモニター、-1なら前のモニターに移動する。
/// ポップアップの表示先も変わるとき(通常モード、またはHUDをポップアップの表示先へ移したとき)は
/// 開いているポップアップも移す。モニターが1つしかない場合は何もしない。
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
    // ゲーム終了後にトップメニューが表示された際、HUDと同じモニターに出現するよう、ここで位置を同期しておく。
    if let Some(topmenu) = app.get_webview_window("topmenu") {
        window::centered_on_monitor(Some(monitor), TOPMENU_WIDTH, TOPMENU_HEIGHT).apply(&topmenu);
    }

    let mut settings = settings::load_settings(&app);
    settings.monitor_index = new_index;
    let _ = settings::save_settings(&app, &settings);

    Ok(serde_json::json!({ "monitorIndex": new_index }))
}

/// マルチディスプレイモード(ポップアップをHUDとは別のモニターに出す)を切り替える。
/// ポップアップの表示先が変わったら、開いているポップアップも移す。
#[tauri::command]
fn set_multi_display(app: tauri::AppHandle, state: tauri::State<'_, SharedState>, enabled: bool) -> Result<(), String> {
    window::update_display_target(&app, state.inner(), |guard| guard.multi_display = enabled)?;
    let mut settings = settings::load_settings(&app);
    settings.multi_display = enabled;
    settings::save_settings(&app, &settings)
}

/// マルチディスプレイモードでのポップアップの表示先を、HUDのモニターを飛ばして次(direction>0)/前のモニターへ移す。
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

/// ヒントバーウィンドウをユーザー設定に従って表示/非表示にする。
///
/// ゲーム開始時にヒントバーを出す箇所は複数あるため、
/// 「設定で非表示にしているなら出さない」という判断をここに集約する。
/// 設定変更時の即時反映は Hintbar コンポーネント側が `settings-updated` を受けて行う。
fn show_hintbar_if_enabled(app: &tauri::AppHandle) {
    if let Some(hintbar) = app.get_webview_window("hintbar") {
        if settings::load_settings(app).hintbar_visible {
            let _ = hintbar.show();
        } else {
            let _ = hintbar.hide();
        }
    }
}

/// `window-registry.json` のキーに対応するポップアップを開く（タスクバーのショートカット用）
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

/// `window-registry.json` のキーに対応するポップアップを(開いていれば)閉じる。
/// 別ウィンドウの操作に付随して閉じたいとき(敷設パネルの確定で2Dパス形状編集パネルを閉じる等)に使う。
#[tauri::command]
fn close_registered_window(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
    window_type: String,
) -> Result<(), String> {
    window::close_windows_by_type(&app, state.inner(), &window_type);
    Ok(())
}

/// 新規ゲームセッションを作成する（シナリオまたはチュートリアルの選択後に呼び出される）。
/// Unity に前の都市の表示を片付けさせ、topmenu と現在のポップアップを隠して Tipsを表示した後、
/// バックエンドに POST /api/v1/sessions を送信する。
///
/// 追加パラメータ（都市名・言語・シミュレーション日数・気候・地表・アセットの地域）はクエリパラメータとして
/// バックエンドに渡される。テンプレートロード時は、作者が固定していない項目だけテンプレートの値を上書きできる
/// (固定の項目に違う値を送るとコアが 422 で断る)。
#[tauri::command]
async fn create_session(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
    template_id: Option<String>,
    city_name: Option<String>,
    language: Option<String>,
    simulation_days: Option<u32>,
    play_mode: Option<i32>,
    tutorial_id: Option<String>,
    accept_balance_change: Option<bool>,
    climate: Option<i32>,
    ground_cover: Option<i32>,
    asset_region: Option<i32>,
) -> Result<(), String> {
    let play_mode = play_mode.unwrap_or(PLAY_MODE_NORMAL);
    if !matches!(play_mode, PLAY_MODE_NORMAL | PLAY_MODE_SCEARIO | PLAY_MODE_TUTORIAL) {
        return Err("不正なプレイモードです。".to_string());
    }
    log::info!(
        "[create_session] template_id={:?} city_name={:?} language={:?} simulation_days={:?} play_mode={} tutorial_id={:?} climate={:?} ground_cover={:?} asset_region={:?}",
        template_id, city_name, language, simulation_days, play_mode, tutorial_id,
        climate, ground_cover, asset_region
    );

    version::verify_core(state.inner()).await?;
    if let Some(id) = &template_id {
        version::validate_load(state.inner(), id, true, accept_balance_change.unwrap_or(false)).await?;
    }

    version::prepare_graphic(state.inner()).await?;
    require_backend(state.inner())?;

    if let Some(topmenu) = app.get_webview_window("topmenu") {
        let _ = topmenu.hide();
    }

    // 開いているポップアップを全て閉じる（NewGameWindow など）
    window::close_all_popup_windows(&app, state.inner());

    begin_initial_loading(&app, state.inner(), template_id.is_none() && play_mode == PLAY_MODE_NORMAL)?;

    {
        let mut guard = state.lock();
        guard.is_loading = true;
        guard.tutorial_id = if play_mode == PLAY_MODE_TUTORIAL {
            tutorial_id.clone().or_else(|| Some("basic".to_string()))
        } else {
            None
        };

    }

    let api_base_url = {
        let guard = state.lock();
        guard.api_base_url.clone()
    };
    let base = api_base_url.trim_end_matches('/');
    let url = format!("{}/api/v1/sessions", base);

    // クエリパラメータを組み立てる。
    // serde_json::Value::Null は reqwest の .query() で除外されるため、
    // 各パラメータを明示的に Option で包んで渡す。
    #[derive(serde::Serialize)]
    struct SessionParams {
        template_id: Option<String>,
        city_name: Option<String>,
        language: Option<String>,
        simulation_days: Option<u32>,
        play_mode: i32,
        tutorial_id: Option<String>,
        accept_balance_change: bool,
        climate: Option<i32>,
        ground_cover: Option<i32>,
        asset_region: Option<i32>,
    }
    let query_params = SessionParams {
        template_id: template_id.clone(),
        city_name: city_name.clone(),
        language: language.clone(),
        simulation_days,
        play_mode,
        tutorial_id: tutorial_id.clone(),
        accept_balance_change: accept_balance_change.unwrap_or(false),
        climate,
        ground_cover,
        asset_region,
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

    // カメラの初期視点はUIを経由しない。グラフィックが game:start 受信時に
    // GET /api/v1/city の camera(セーブ・シナリオに保存された値)を自分で読む。
    log::info!("[create_session] セッション作成リクエスト成功。ポーリング待機に委譲");
    state.lock().backend_load_complete = true;
    Ok(())
}

/// セーブデータをロードしてゲームセッションを開始する。
/// Unity に前の都市の表示を片付けさせ、topmenu を隠して Tipsを表示した後、
/// バックエンドにロードリクエストを送信する。
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

    // ロードの確定に伴い、開いているポップアップを全て閉じる。
    // ロード画面自体のほか、ゲーム中に別のセーブデータをロードし直した場合は
    // 前セッションの詳細パネル等がロード後のデータと無関係になるため一掃する。
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

/// セーブデータの保存先ディレクトリ(`<ユーザーフォルダ>/saves`。コアの CITY_SAVE_DIR と同じ)をOSのファイルエクスプローラー等で開く。
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

/// 地形エディタの作業コピーの置き場(`<ユーザーフォルダ>/terrain_editor/`)を返す。
///
/// 作業コピーはウィンドウ間の初期データ受け渡し専用で、シナリオへの反映は save_scenario_terrain(コアの PUT)だけが行う
/// (保存に成功したら、作業コピーも保存した内容に合わせる)。
fn terrain_editor_dir() -> Result<std::path::PathBuf, String> {
    let dir = version::user_data_dir().ok_or("ユーザーフォルダを解決できません")?.join("terrain_editor");
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    Ok(dir)
}

/// 作業コピーのID(= ディレクトリ名)か。`Uuid::to_string()` と同じ小文字・ハイフン区切りの形だけを通し、
/// 区切り文字や `..` で terrain_editor/ の外を指させない。
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

/// 地形エディタのウィンドウが持つ作業コピーのID。表示中のものと、再利用待ち(非表示)を含む全体を返す。
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

/// どのウィンドウにも結び付かず、作ってから1時間を過ぎた作業コピーを消す。
/// 作った直後(ウィンドウを開く前)のものを巻き込まないよう、経過時間でも絞る。
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

/// シナリオの地形 API(`/api/v1/scenarios/{id}/terrain`)の URL。ID はパスの1区間として符号化する。
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

/// コアのエラー応答を「CODE: 本文」の文字列にする。UI は先頭のコードで翻訳文を引く(`utils/saves.ts` の saveError)。
async fn scenario_api_error(response: reqwest::Response) -> String {
    let status = response.status();
    let data: Value = response.json().await.unwrap_or(Value::Null);
    let body = data.get("detail").filter(|detail| detail.is_object()).unwrap_or(&data);
    match body.get("code").and_then(Value::as_str) {
        Some(code) => format!("{code}: {}", body.get("message").and_then(Value::as_str).unwrap_or_default()),
        None => format!("HTTP {status}"),
    }
}

/// GET /api/v1/scenarios/{id}/terrain の応答。契約の正はコアのシナリオ地形 API。
#[derive(serde::Deserialize)]
struct ScenarioTerrainDocument {
    settings: ScenarioTerrainSettings,
    /// float16 LE の近景標高(行優先、grid_rows×grid_cols)を base64 にしたもの。
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
    /// 地形の出どころ(取り込んだ標高ファイルの名前、またはチュートリアルの舞台の名前)。表示だけに使う。
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

/// 建設物(道路・建物・造成跡・埋立)があるシナリオでは標高・水面・川を変えられない。reasons は理由コードの一覧。
#[derive(serde::Deserialize, serde::Serialize)]
struct ScenarioTerrainLocks {
    heights_locked: bool,
    #[serde(default)]
    reasons: Vec<Value>,
}

/// GET 応答から作業コピー(meta.json の内容と near.bin のバイト列)を作る。標高のバイト数が格子と合わなければ拒否する。
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

/// シナリオの地形を地形エディタの作業コピーへ書き出し、作業コピーのID(editor_id)を返す。
///
/// 同じシナリオの地形エディタが表示中ならそのIDを返す(開き直しで未保存の編集を捨てず、
/// 2枚のウィンドウの保存が互いを上書きしないようにする)。ゲームのセッション中はコアが 409 SESSION_ACTIVE で断る。
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

/// load_terrain_editor_data の戻り値。作業コピーの meta.json に、near.bin を base64 にした標高を加えたもの。
/// 水面・川・資源・プロップ・カメラの形式の正は公開docs `D_地形形状.md` §D.7〜D.9(プロップは `M_地形プロップ.md`)。
#[derive(serde::Serialize)]
struct TerrainEditorData {
    scenario_id: String,
    terrain_source: Value,
    title: Value,
    /// `{heights_locked, reasons}`。heights_locked なら標高・水面・川を保存できない。
    locks: Value,
    map_width: f64,
    map_height: f64,
    near_spacing: f64,
    far_spacing: f64,
    cell_size: f64,
    grid_rows: u32,
    grid_cols: u32,
    /// float16 LE バイト列を base64 エンコードした文字列。
    data_base64: String,
    water_surfaces: Value,
    rivers: Value,
    /// id は資源の DB ID の文字列。保存時はコアが ID で差分適用する(無い ID は新規)。
    resources: Value,
    props: Value,
    /// 未設定なら null。
    camera: Value,
    /// 未設定なら null で、フロントエンドが草原として扱う。
    ground_cover: Value,
    /// 未設定なら null で、フロントエンドが温暖湿潤として扱う。
    climate: Value,
}

/// 地形エディタの作業コピーを読み込む。near.bin は大きいので、メインスレッドを塞がないよう非同期コマンドにしている。
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

/// PUT /api/v1/scenarios/{id}/terrain の応答のうち、地形エディタが使う項目。
#[derive(serde::Deserialize, serde::Serialize)]
struct ScenarioTerrainSaveResult {
    content_revision: i64,
    /// 保存前に判定したロック。
    locks: ScenarioTerrainLocks,
    /// 今回新しく作った資源の {エディタ側の仮ID: DB の ID の文字列}。
    resource_ids: std::collections::HashMap<String, String>,
}

/// 保存した内容を反映した作業コピーの meta.json。新しく作った資源は DB の ID に替える
/// (作業コピーを読み直したときに仮IDのまま保存すると、コアが資源を削除して作り直すため)。
/// 水面・川は送ったとき(null でないとき)だけ置き換える。コアも null を「変更しない」と扱う。
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

/// 保存に成功した内容で作業コピー(near.bin・meta.json)を書き換える。標高は送ったときだけ書く。
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

/// 地形エディタの編集内容をシナリオへ保存する(PUT /api/v1/scenarios/{id}/terrain)。
///
/// 保存先のシナリオは作業コピーの meta.json から引き、UI からは受け取らない。標高・水面・川は、
/// ロック中なら None(null)で送る決まりで、ここでも先に弾く。寸法の変更拒否などの最終判定はコアが行う。
/// 成功したら作業コピーを保存した内容に合わせ、応答(版・ロック・新しい資源のID)を返す。
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

/// アプリケーション全体を終了する（トップメニューの「終了」ボタン用）
#[tauri::command]
fn exit_app(app: tauri::AppHandle, state: tauri::State<'_, SharedState>) -> Result<(), String> {
    let state = state.inner().clone();
    // 子プロセスの終了を待つため、UI スレッドをブロックしないよう別スレッドで実行する
    std::thread::spawn(move || {
        process::request_app_exit(&app, &state);
    });
    Ok(())
}

/// 通知ポップアップをキューに追加する。
/// 既に表示中でなければ即座に表示が開始される。
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

/// 通知ポップアップを手動で閉じ、キューの次の通知を表示する。
///
/// フロントエンドの×ボタンから呼び出される。
#[tauri::command]
async fn dismiss_notification(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
) -> Result<(), String> {
    window::dismiss_notification(&app, state.inner())
}

/// フロントエンドが測定した通知コンテンツの高さを受け取り、ウィンドウサイズを調整する。
///
/// 通知の文字量に応じて高さを可変にするため、フロントエンド側で実際のレンダリング高さを取得した後、
/// このコマンドを呼び出してウィンドウを内容にフィットさせる。
#[tauri::command]
async fn resize_notification_window(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
    height: f64,
) -> Result<(), String> {
    window::resize_notification_window(&app, state.inner(), height)
}

/// ロード要求の世代を進め、前セッションの完了通知を無視する。
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
        // 既存セッションが残っていれば、次のポーリングで描画を作り直して復帰する。
        guard.last_session_id = None;
    }
    window::close_tips_window(app);
    if let Some(topmenu) = app.get_webview_window("topmenu") { let _ = topmenu.show(); }
}

/// game:startは描画準備のみ開始する。表示解禁はgame:revealで行う。
fn send_game_start_ipc(state: &SharedState, _app: &tauri::AppHandle) {
    let loading_id = {
        let mut guard = state.lock();
        guard.loading_started = true;
        guard.graphic_load_ready = false;
        // Unityを起動しないUI開発ではバックエンドの準備完了だけで開始できる。
        // 接続中なら描画完了を待ち、配布ビルドにはこの省略処理を含めない。
        // 判定は「現在接続中か」(graphic_connected)であり「一度でも接続したか」ではない。
        // 後者だとEditorのPlayを止めた後の再ロードで永遠に待つ。
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
}

/// フリーモード新規ゲームのパラメータ設定完了後に呼ばれるコマンド。
/// パラメータ設定ウィンドウを閉じ、HUD/Hintbarを表示する。都市の初期ロードは完了済み。
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

/// テンプレート作成モード中にアイコンパネルからパラメータ設定ウィンドウを「編集モード」で開く。
/// 初期セットアップとは異なり、完了しても game:start は送らずウィンドウを閉じるだけになる。
///
/// 同期コマンドにしてはならない。Tauri は同期コマンドをメインスレッドで実行するが、
/// その先の `builder.build()` はウィンドウ生成をメインスレッドのイベントループへ投げて
/// 完了を待つため、自分でイベントループを塞いだまま待ち続けてアプリ全体がフリーズする。
#[tauri::command]
async fn open_parameter_edit_window(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
) -> Result<(), String> {
    window::open_parameter_setup_window(&app, &state, true)
}

/// パラメータ設定ウィンドウが「ゲーム内編集モード」で開かれているかを返す。
/// React 側（ParameterSetupWindow）が起動時に問い合わせ、フッターの挙動を切り替える。
#[tauri::command]
fn get_parameter_setup_mode(state: tauri::State<'_, SharedState>) -> bool {
    state.lock().parameter_setup_edit_mode
}

/// パラメータ設定ウィンドウ（編集モード）を閉じる。game:start は送らない。
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
    // 生成に失敗してもTipsから再試行できるよう、先に次のウィンドウを用意する。
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

/// セッション消失・開発版の持続的な通信断で表示と待機状態を片付ける。
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

/// コアに接続できない間の session-state。トップメニューはこれを受けて表示され、接続失敗を示す。
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

/// セッション状態を定期的にポーリングし、全ウィンドウに配信するスレッドを起動する。
///
/// 5秒ごとにバックエンドの `/api/v1/sessions` と、都市があれば `/api/v1/city/` を取得し、
/// `session-state` イベントとして全 WebView ウィンドウにブロードキャストする。
/// これにより TopMenu・HUD・Tips などが個別にポーリングする必要がなくなる。
///
/// バックエンドの準備完了後にUnityの初期描画を開始する。
/// Tipsを閉じる条件はUnityのgame:loadedとユーザーの開始操作である。
fn start_session_polling(state: SharedState, app: tauri::AppHandle) {
    std::thread::spawn(move || {
        // クライアントをループ外で生成してコネクションプールを維持する。
        // localhost 内の通信でも、都度クライアントを作り直すと TCP コネクションの
        // 確立に時間がかかり、Python 側の GIL や接続キューの影響でタイムアウトしやすくなる。
        let client = reqwest::blocking::Client::builder()
            .timeout(Duration::from_secs(10))
            // Python 側 (uvicorn) の keep-alive タイムアウトより短く設定することで、
            // サーバ側ですでに切断された idle コネクションを再利用してしまい、
            // 一瞬だけリクエストが失敗する問題を軽減する。
            .pool_idle_timeout(Duration::from_secs(3))
            .pool_max_idle_per_host(10)
            .build()
            .unwrap_or_default();

        // 起動準備が済むまでだけ取得を試み、取得後は同じ版情報を使う。
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
        // フロントのMAJORがコアのMAJOR未満なら、起動できない。待っても版は変わらないので再試行せず終了する。
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

            // このGETより後にPOSTが終わった場合、古い応答を新セッションの状態として採用しない。
            let (polled_loading_id, load_complete_before_poll, started_before_poll) = {
                let guard = state.lock();
                (guard.loading_id, guard.backend_load_complete, guard.loading_started)
            };

            // 通信確認は互換性の照合結果から独立させる。
            let session_data: Option<Value> = match client
                .get(format!("{}/api/v1/sessions", base))
                .send()
            {
                Ok(res) => {
                    let status = res.status();
                    if status.is_success() {
                        match res.text() {
                            Ok(body) => {
                                // 成功時の生データは毎ポーリングごとに出すとノイズになるため出力しない。
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
                    // reqwest のエラーを詳細に分類してログに出力する。
                    // localhost 内の通信でもタイムアウト・接続拒否・DNS 解決失敗などが発生しうる。
                    // {:?} を使うとソースエラーチェーンも含めて出力されるため、
                    // コネクションリセットやプールの不整合などの根本原因が特定しやすくなる。
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
                // 瞬断では既存の都市情報を配信し直さず、各画面の表示内容を保持する。
                if guard.last_session_id.is_none() && !guard.is_loading {
                    let _ = app.emit("session-state", disconnected_session_state(&api_base_url, &core["core_version"]));
                }
                drop(guard);
                std::thread::sleep(Duration::from_secs(5));
                continue;
            }
            // IPC契約の不一致で画面の起動を止めても、正常なHTTP応答を通信断として数えない。
            if state.lock().graphic_contract_error.is_some() {
                if !observe_backend(&app, &state, true) { break; }
                std::thread::sleep(Duration::from_secs(5));
                continue;
            }

            let current_save_id = session_data
                .as_ref()
                .and_then(|d| d.get("current_save_id"))
                .and_then(|v| v.as_str());
            // ゲームの開始・終了は current_save_id の有無、別の都市への切り替えは session_id で判定する。
            // 別名保存では current_save_id だけが新しいファイルのIDに変わるので、切り替えと取り違えて読み直さない。
            let current_session_id = current_save_id.and(
                session_data.as_ref().and_then(|d| d.get("session_id")).and_then(|v| v.as_str()));

            // 都市情報を取得（Taskbar の cityName / currentDate 用）。都市が無い間はコアが 503 を返すだけなので取らない。
            // 失敗してもセッション情報には影響しない。
            // 応答は成否によらず本文を読み切る。未読のまま捨てると切断が RST になり、Windows のコアでは
            // asyncio がそのソケットを閉じ損ねて残す(コアの終了も残った接続を待って止まる)。
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

            // 都市の変化はゲーム開始・終了の検知ログで通知するため、変化がない場合は何も出力しない。
            let session_changed = prev_session_id.as_deref() != current_session_id;

            let load_completed = {
                let mut guard = state.lock();
                let ready = load_complete_before_poll && guard.backend_load_complete
                    && guard.loading_id == polled_loading_id && current_save_id.is_some();
                if ready { guard.backend_load_complete = false; }
                ready
            };
            // ロード中の消失は通常の都市の変化判定から外れるため別に拾う。拾わないとUnityは描画完了を
            // 通知できず、閉じられないTipsや初期設定のまま操作不能になる。POST応答前は新セッションが
            // まだ無いのが正常なので、準備完了の確認後に送ったGETだけで判定する。
            let session_lost_while_loading = is_loading && current_save_id.is_none()
                && (load_complete_before_poll || started_before_poll)
                && state.lock().loading_id == polled_loading_id;
            if (session_changed && !is_loading) || load_completed || session_lost_while_loading {
                let new_session_id = current_session_id.map(|s| s.to_string());
                {
                    let mut guard = state.lock();
                    guard.last_session_id = new_session_id.clone();
                }

                // non-null: ゲーム開始またはセッション切り替え
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
                    // POSTの応答前にセッションを検知しても、準備完了まではロードしない。
                    if load_completed || !is_loading { send_game_start_ipc(&state, &app); }

                } else {
                    // null: ゲーム終了。ロード中に消失した場合は前回値もnullのことがある。
                    log::info!("[session-poll] current_save_id がクリアされました。ゲーム終了を検知");
                    return_to_topmenu(&app, &state);
                }
            }

            // セッション状態を保存
            {
                let mut guard = state.lock();
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
            }
            // 復旧時はセッションの消失・切り替えを反映してから操作を解禁する。
            // 再接続先の都市へ古い画面から更新要求を送らないため、この順序を守る。
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

/// ログ出力を初期化する。
/// 実行ファイルと同じディレクトリに city-genesis/tauri.log を作成し、
/// 標準出力とファイルの両方に DEBUG レベル以上のログを出力する。
/// Windows の本番ビルドではコンソールが表示されないため、ファイルログが重要となる。
fn setup_logging() -> Result<(), fern::InitError> {
    let log_path = if let Ok(exe_path) = std::env::current_exe() {
        exe_path
            .parent()
            .map(|p| p.join("city-genesis/tauri.log"))
            .unwrap_or_else(|| "city-genesis/tauri.log".into())
    } else {
        "city-genesis/tauri.log".into()
    };

    // fern はディレクトリを作らないため、親ディレクトリを先に用意する。
    // これがないと開発ビルド(target/debug 直下)ではログ初期化が必ず失敗する。
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
        // reqwest 内部の接続ログはポーリングのたびに大量に出るため、
        // reqwest クレート単体では Warn 以上のみ出力する。
        .level_for("reqwest", log::LevelFilter::Warn)
        // tao はイベントループの冗長な DEBUG (NewEvents/RedrawEventsCleared) を
        // 常時吐き続けてログを埋めるため、Warn 以上のみ出力する。
        .level_for("tao", log::LevelFilter::Warn)
        .chain(std::io::stdout())
        .chain(fern::log_file(log_path)?)
        .apply()?;
    Ok(())
}

/// フロントエンドからのエラーログを受け取るコマンド。
/// JavaScript の window.onerror や unhandledrejection から送信されたエラーを
/// Rust のログシステムを通じてファイルに記録する。
#[tauri::command]
fn log_frontend_error(message: String) {
    log::error!("[frontend] {}", message);
}

/// フロントエンドからの情報ログを受け取るコマンド。
/// エポック実行のように「失敗しても画面に何も出ず無言で止まりうる」フローの
/// 進行記録に使う(本番ビルドではWebViewのconsoleを確認できないため)。
#[tauri::command]
fn log_frontend_info(message: String) {
    log::info!("[frontend] {}", message);
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    setup_logging().unwrap_or_else(|e| eprintln!("ログ初期化に失敗しました: {}", e));

    // ストア連携は子プロセスより先に初期化し、ストアが管理するフォルダをコアへ渡す。
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
            // セーブの置き場・地形エディタの作業コピーの基準になるユーザーフォルダを決める。
            version::init_user_data_dir(app.handle());

            if let Some(main_window) = app.get_webview_window("main") {
                let _ = main_window.hide();
            }

            // コックピットは読み込み直後に音量をUnityへ送り、以後は設定変更とUnity起動時にしか送らない。
            // ウィンドウより後にIPCを開くと、その1通が「IPC not initialized」で失われるため先に開く。
            ipc::start_ipc(app.handle().clone(), setup_state.clone());

            let loaded_settings = settings::load_settings(app.handle());
            let monitors: Vec<_> = app.available_monitors()
                .unwrap_or_default()
                .into_iter()
                .collect();

            // 記録したモニターが外れていたらプライマリモニターを使う(設定ファイルの値は残すので、再接続すれば戻る)
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

            // トップメニューウィンドウを target_monitor（設定ファイルの monitor_index に対応するモニター、
            // またはプライマリモニター）の中央に生成する。最初の session-state で表示されるので、
            // コアの起動を待つ間は何も出さない。
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

            // HUDウィンドウ4枚(左端のアイコンパネル・下部中央のコックピット・右下のシステムトレイ・左下の表示トレイ)を
            // target_monitor 上に生成する。初期状態では非表示で、
            // GET /api/v1/sessions の current_save_id が非nullになったポーリングで表示される。
            window::create_hud_windows(app.handle(), &monitors, monitor_index)
                .expect("failed to create HUD windows");

            // ヒントバーウィンドウを target_monitor の上部中央に生成する。
            // 初期状態では非表示で、ゲームの開始時に設定に従って表示される。
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

            // チュートリアルウィンドウをヒントバーの直下に生成する。
            // 初期状態は非表示で、テンプレートから新規ゲームを開始した際に表示される。
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

            // 通知ポップアップウィンドウを事前に作成しておく。
            // 表示時にウィンドウを都度生成する方式だと、アニメーション開始までの遅延が大きくなるため、
            // 非表示のまま準備しておき、内容が決まってから show() する方式を採用する。
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
                window::notification_placement(target_monitor, window::NOTIFICATION_MAX_HEIGHT),
                false,
            )
            .expect("failed to create notification window");

            // コアとグラフィックを起動し、コアの準備ができてから認証とセッション監視を始める。
            // このスレッドはその後、アプリが終わるまで子プロセスを見張る。
            let app_handle = app.handle().clone();
            let state = setup_state.clone();
            std::thread::spawn(move || {
                if !process::start_children(&app_handle, &state) {
                    return;
                }
                #[cfg(feature = "steam")]
                steam::start_verification(state.lock().api_base_url.clone());
                start_session_polling(state.clone(), app_handle.clone());
                process::watch_children(&app_handle, &state);
            });
            Ok(())
        })
        .on_window_event({
            move |window, event| {
                match event {
                    // ウィンドウの×ボタン(Alt+F4含む)。
                    // - main/topmenu/HUD各枚 はアプリの主要サーフェスなので、
                    //   閉じる操作を「アプリ全体の終了要求」として扱い、
                    //   終了シーケンス(Unity通知→コアの保存と終了を待つ→自終了)に合流させる。
                    // - open_windows 管理下のポップアップは既存の hide+LRU 再利用
                    //   セマンティクスを維持するため close_from_frontend に委譲する。
                    // - その他の専用ウィンドウ(notification/hintbar/tutorial等)は素通しする。
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
                    // リサイズ中はサイズを、移動中は位置（自由配置のみ）を記憶する
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
                    // どれかのTauriウィンドウがフォーカスを失った=多くはUnityの3Dビューを
                    // クリックした瞬間。Unityがフルスクリーン扱いでフォアグラウンドになると
                    // topmostのUIが裏に回ることがあるため、このタイミングで打ち直す。
                    // シェルによる持ち上げはUnityの活性化中に起きるため、これだけでは先に
                    // 走って取りこぼすことがある。Unity側がフォーカスを得た後に送る
                    // system:focus_gained(ipc.rs)での打ち直しが本命の復帰経路。
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
            client_stats::get_client_system_stats,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

