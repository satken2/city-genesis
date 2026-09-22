mod ipc;
mod messages;
mod settings;
mod state;
mod terrain_export_paths;
mod window;

use messages::{BackendLaunchConfig, EntityRef, GraphicLaunchConfig, MutexGroups, WindowRegistry};
use serde_json::Value;
use state::SharedState;
use std::sync::atomic::Ordering;
use std::time::Duration;
use tauri::{Emitter, Manager, WebviewUrl, WebviewWindowBuilder, WindowEvent};

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

fn api_base_url() -> String {
    // 開発用の明示オーバーライドが最優先
    if let Ok(url) = std::env::var("GENESIS_UI_API_BASE_URL") {
        return url;
    }
    // genesis.py(スーパーバイザ)経由で起動された場合は GENESIS_API_PORT が渡される。
    // find_free_port により 5000 以外のポートになることがあるため必ずこれを読む
    // (以前はこの env を読んでおらず、5000 が使用中の環境で接続先を誤るバグがあった)。
    let port = std::env::var("GENESIS_API_PORT")
        .ok()
        .and_then(|p| p.parse::<u16>().ok())
        .unwrap_or(5000);
    format!("http://localhost:{port}")
}

/// 実行ファイルと同じディレクトリにある graphic-launch.toml を読み込む。
/// ファイルが存在しない場合は環境変数 GENESIS_GRAPHIC_COMMAND をフォールバックとして使う。
/// どちらも設定されていなければ空文字を返す（フロントエンドでエラー表示される）。
fn load_graphic_launch_command() -> String {
    if let Ok(exe_path) = std::env::current_exe() {
        let config_path = exe_path.parent().map(|p| p.join("graphic-launch.toml"));
        if let Some(path) = config_path {
            if path.exists() {
                if let Ok(data) = std::fs::read_to_string(&path) {
                    if let Ok(config) = toml::from_str::<GraphicLaunchConfig>(&data) {
                        return config.launch_command;
                    }
                    eprintln!("graphic-launch.toml のパースに失敗しました: {}", path.display());
                }
            }
        }
    }

    std::env::var("GENESIS_GRAPHIC_COMMAND").unwrap_or_default()
}

/// 実行ファイルと同じディレクトリにある backend-launch.toml を読み込む。
/// ファイルが存在しない場合は環境変数 GENESIS_BACKEND_COMMAND をフォールバックとして使う。
/// どちらも設定されていなければ空文字を返す（バックエンドプロセス管理が無効になる）。
fn load_backend_launch_command() -> String {
    if let Ok(exe_path) = std::env::current_exe() {
        let config_path = exe_path.parent().map(|p| p.join("backend-launch.toml"));
        if let Some(path) = config_path {
            if path.exists() {
                if let Ok(data) = std::fs::read_to_string(&path) {
                    if let Ok(config) = toml::from_str::<BackendLaunchConfig>(&data) {
                        return config.launch_command;
                    }
                    eprintln!("backend-launch.toml のパースに失敗しました: {}", path.display());
                }
            }
        }
    }

    std::env::var("GENESIS_BACKEND_COMMAND").unwrap_or_default()
}

/// トップメニューのサイズ
const TOPMENU_WIDTH: f64 = 800.0;
const TOPMENU_HEIGHT: f64 = 600.0;

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
    ipc::send_ui_action(state.inner(), window_type, entity, action, params)
}

#[tauri::command]
fn get_connection_status(state: tauri::State<'_, SharedState>) -> Value {
    window::get_connection_status(state.inner())
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

/// トップメニューの初期状態を返す
/// フロントエンドのTopMenuコンポーネントが初期化時に呼び出す
#[tauri::command]
fn get_topmenu_state(state: tauri::State<'_, SharedState>) -> Value {
    let guard = state.lock();
    serde_json::json!({
        "apiBaseUrl": guard.api_base_url,
    })
}

/// HUD(アイコンパネル・コックピット・システムトレイ)とトップメニューを別のモニターに移動する
/// directionが1なら次のモニター、-1なら前のモニターに移動する
/// モニターが1つしかない場合は何もしない
#[tauri::command]
fn switch_monitor(app: tauri::AppHandle, state: tauri::State<'_, SharedState>, direction: i32) -> Result<Value, String> {
    let monitors: Vec<_> = app.available_monitors()
        .map_err(|err| err.to_string())?
        .into_iter()
        .collect();

    if monitors.len() <= 1 {
        return Ok(serde_json::json!({ "monitorIndex": 0 }));
    }

    let new_index = {
        let mut guard = state.lock();
        let count = monitors.len();
        if direction > 0 {
            guard.current_monitor_index = (guard.current_monitor_index + 1) % count;
        } else {
            guard.current_monitor_index = (guard.current_monitor_index + count - 1) % count;
        }
        guard.current_monitor_index
    };

    let monitor = monitors.get(new_index).ok_or("monitor index out of range")?;
    let scale = monitor.scale_factor();
    let monitor_size = monitor.size().to_logical::<f64>(scale);
    let monitor_pos = monitor.position().to_logical::<f64>(scale);

    // HUD3枚(左端のアイコンパネル・下部中央のコックピット・右下のシステムトレイ)を対象モニターへ移動する
    window::layout_hud_windows(&app, &monitors, new_index, None, None);

    // ヒントバーも同じモニターの上部中央に移動する。
    if let Some(hintbar) = app.get_webview_window("hintbar") {
        let hintbar_width = monitor_size.width * 0.55;
        let hintbar_height = 68.0;
        let hintbar_x = monitor_pos.x + (monitor_size.width - hintbar_width) / 2.0;
        let hintbar_y = monitor_pos.y + 4.0;
        let _ = hintbar.set_size(tauri::LogicalSize::new(hintbar_width, hintbar_height));
        let _ = hintbar.set_position(tauri::LogicalPosition::new(hintbar_x, hintbar_y));
    }

    // チュートリアルウィンドウもヒントバーの直下に移動する。
    if let Some(tutorial) = app.get_webview_window("tutorial") {
        let tutorial_width = monitor_size.width * 0.55;
        let tutorial_height = 200.0;
        let tutorial_x = monitor_pos.x + (monitor_size.width - tutorial_width) / 2.0;
        let tutorial_y = monitor_pos.y + 76.0;
        let _ = tutorial.set_size(tauri::LogicalSize::new(tutorial_width, tutorial_height));
        let _ = tutorial.set_position(tauri::LogicalPosition::new(tutorial_x, tutorial_y));
    }

    // トップメニューも同じモニターの中央に移動する。
    // ゲーム終了後にトップメニューが表示された際、HUDと同じモニターに
    // 出現するよう、ここで位置を同期しておく。
    if let Some(topmenu) = app.get_webview_window("topmenu") {
        let topmenu_x = monitor_pos.x + (monitor_size.width - TOPMENU_WIDTH) / 2.0;
        let topmenu_y = monitor_pos.y + (monitor_size.height - TOPMENU_HEIGHT) / 2.0;
        let _ = topmenu.set_position(tauri::LogicalPosition::new(topmenu_x, topmenu_y));
    }

    let mut settings = settings::load_settings(&app);
    settings.monitor_index = new_index;
    let _ = settings::save_settings(&app, &settings);

    Ok(serde_json::json!({ "monitorIndex": new_index }))
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

/// Graphic(Unity) を起動する、または起動済みの場合はパイプ経由でリセット指示を送る
#[tauri::command]
fn launch_graphic(state: tauri::State<'_, SharedState>) -> Result<String, String> {
    window::launch_graphic(state.inner())
}

/// 新規ゲームセッションを開始する。
/// Unity を起動し、topmenu を隠して Tipsを表示した後、
/// バックエンドに POST /api/v1/sessions を送信する。
/// バックエンドの準備完了はポーリングスレッドが検知し、自動的にHUDを表示する。
#[tauri::command]
async fn start_new_game(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
) -> Result<(), String> {
    log::info!("[start_new_game] Unity 起動を開始");

    // Unity を起動
    window::launch_graphic(state.inner())?;

    // topmenu を隠す
    if let Some(topmenu) = app.get_webview_window("topmenu") {
        let _ = topmenu.hide();
    }

    // ゲーム開始の確定に伴い、開いているポップアップを全て閉じる
    window::close_all_popup_windows(&app, state.inner());

    // Tipsをロード画面として表示
    begin_initial_loading(&app, state.inner(), true)?;


    // バックエンドにセッション作成リクエストを送信
    let api_base_url = {
        let guard = state.lock();
        guard.api_base_url.clone()
    };
    let url = format!("{}/api/v1/sessions", api_base_url.trim_end_matches('/'));

    let client = reqwest::Client::new();
    let res = client
        .post(&url)
        .timeout(Duration::from_secs(60))
        .send()
        .await
        .map_err(|e| { abort_initial_loading(&app, state.inner()); format!("セッション作成リクエストに失敗しました: {e}") })?;

    if !res.status().is_success() {
        let status = res.status();
        let body = res.text().await.unwrap_or_default();
        log::error!("[start_new_game] セッション作成失敗: {} {}", status, body);
        // 失敗時は loading を閉じて topmenu を戻す
        abort_initial_loading(&app, state.inner());
        if let Some(topmenu) = app.get_webview_window("topmenu") {
            let _ = topmenu.show();
        }
        {
            let mut guard = state.lock();
            guard.is_loading = false;
        }
        return Err(format!("セッション作成に失敗しました: {status}"));
    }

    log::info!("[start_new_game] セッション作成リクエスト成功。ポーリング待機に委譲");
    state.lock().backend_load_complete = true;
    Ok(())
}

/// 新規ゲームセッションを作成する（地形またはテンプレート選択後に呼び出される）。
/// Unity を起動し、topmenu と現在のポップアップを隠して Tipsを表示した後、
/// バックエンドに POST /api/v1/sessions を送信する。
///
/// 追加パラメータ（都市名・言語・シミュレーション日数）はクエリパラメータとして
/// バックエンドに渡される。テンプレートロード時はテンプレートの値を上書きする。
#[tauri::command]
async fn create_session(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
    terrain_map_id: Option<String>,
    template_id: Option<String>,
    city_name: Option<String>,
    language: Option<String>,
    simulation_days: Option<u32>,
    is_tutorial: Option<bool>,
    template_mode: Option<bool>,
    tutorial_id: Option<String>,
) -> Result<(), String> {
    log::info!(
        "[create_session] terrain_map_id={:?} template_id={:?} city_name={:?} language={:?} simulation_days={:?} is_tutorial={:?} template_mode={:?} tutorial_id={:?}",
        terrain_map_id, template_id, city_name, language, simulation_days, is_tutorial, template_mode, tutorial_id
    );

    // Unity が起動していなければ起動する
    window::launch_graphic(state.inner())?;

    if let Some(topmenu) = app.get_webview_window("topmenu") {
        let _ = topmenu.hide();
    }

    // 開いているポップアップを全て閉じる（NewGameWindow など）
    window::close_all_popup_windows(&app, state.inner());

    begin_initial_loading(&app, state.inner(), template_id.is_none() && !template_mode.unwrap_or(false) && !is_tutorial.unwrap_or(false))?;

    {
        let mut guard = state.lock();
        guard.is_loading = true;
        guard.tutorial_id = if is_tutorial.unwrap_or(false) {
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
        terrain_map_id: Option<String>,
        template_id: Option<String>,
        city_name: Option<String>,
        language: Option<String>,
        simulation_days: Option<u32>,
        is_tutorial: Option<bool>,
        template_mode: Option<bool>,
    }
    let query_params = SessionParams {
        terrain_map_id: terrain_map_id.clone(),
        template_id: template_id.clone(),
        city_name: city_name.clone(),
        language: language.clone(),
        simulation_days,
        is_tutorial,
        template_mode,
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
        return Err(format!("セッション作成に失敗しました: {status}"));
    }

    // カメラの初期視点はUIを経由しない。グラフィックが game:start 受信時に
    // GET /api/v1/city の camera(セーブ値、または地形マップ定義の初期値)を自分で読む。
    log::info!("[create_session] セッション作成リクエスト成功。ポーリング待機に委譲");
    state.lock().backend_load_complete = true;
    Ok(())
}

/// セーブデータをロードしてゲームセッションを開始する。
/// Unity を起動し、topmenu を隠して Tipsを表示した後、
/// バックエンドにロードリクエストを送信する。
#[tauri::command]
async fn start_load_game(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
    save_id: String,
) -> Result<(), String> {
    log::info!("[start_load_game] save_id={} でロード開始", save_id);

    window::launch_graphic(state.inner())?;

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
        .query(&[("save_id", &save_id)])
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
        return Err(format!("ロードに失敗しました: {status}"));
    }

    log::info!("[start_load_game] ロードリクエスト成功。ポーリング待機に委譲");
    state.lock().backend_load_complete = true;
    Ok(())
}

/// セーブデータの保存先ディレクトリをOSのファイルエクスプローラー等で開く。
#[tauri::command]
async fn open_save_directory(app: tauri::AppHandle) -> Result<(), String> {
    let local_data = app.path().app_local_data_dir().map_err(|e| e.to_string())?;
    let base = local_data
        .parent()
        .ok_or_else(|| "ローカルデータディレクトリの親を解決できません".to_string())?;
    let dir = base.join("UrbanGenesis").join("saves");
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

/// 地形エディタの作業コピー保存先ディレクトリを解決する。
///
/// ファイル配置.md で定義された AppData 共通ディレクトリ(UrbanGenesis/) 配下の
/// terrain_editor/ を返す。バックエンド(Python)が管理する saves/ や templates/ と
/// 同一の UrbanGenesis/ ルートを共有するが、Tauri が独立して管理する領域である。
///
/// Tauri の app_local_data_dir() は OS ローカルデータディレクトリ配下の
/// バンドル識別子(com.citygenesis.ui)を返すため、親ディレクトリに遡ってから
/// UrbanGenesis/terrain_editor を結合することで、バックエンドとパスを統一する。
fn terrain_editor_dir(app: &tauri::AppHandle) -> Result<std::path::PathBuf, String> {
    let local_data = app.path().app_local_data_dir().map_err(|e| e.to_string())?;
    let base = local_data
        .parent()
        .ok_or_else(|| "ローカルデータディレクトリの親を解決できません".to_string())?;
    let dir = base.join("UrbanGenesis").join("terrain_editor");
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    Ok(dir)
}

/// レスポンスヘッダの値を f64 として取得する。失敗時は 0.0。
fn header_f64(res: &reqwest::Response, name: &str) -> f64 {
    res.headers()
        .get(name)
        .and_then(|v| v.to_str().ok())
        .and_then(|s| s.parse::<f64>().ok())
        .unwrap_or(0.0)
}

/// 地形エディタの編集対象として、指定マップの近景標高マップをバックエンドから取得し、
/// ローカル(AppData)に作業コピーを保存する。
///
/// 地形編集はこのローカルコピーに対して行われ、バックエンドには書き戻さない。
/// 原本は標高だけを新しいIDへ複製し、ゲームへの反映はエクスポートで行う。
#[tauri::command]
async fn copy_terrain_for_editor(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
    map_id: String,
    original: Option<bool>,
) -> Result<String, String> {
    let api_base_url = {
        let guard = state.lock();
        guard.api_base_url.clone()
    };
    let base = api_base_url.trim_end_matches('/');
    let original = original.unwrap_or(false);
    let url = format!("{}/api/v1/terrain/maps/{}/near?original={}", base, map_id, original);

    let client = reqwest::Client::new();
    let res = client
        .get(&url)
        .timeout(Duration::from_secs(60))
        .send()
        .await
        .map_err(|e| format!("地形データの取得に失敗しました: {e}"))?;
    if !res.status().is_success() {
        return Err(format!(
            "地形データの取得に失敗しました: HTTP {}",
            res.status()
        ));
    }

    let map_width = header_f64(&res, "X-Map-Width");
    let map_height = header_f64(&res, "X-Map-Height");
    let near_spacing = header_f64(&res, "X-Near-Spacing");
    let far_spacing = header_f64(&res, "X-Far-Spacing");
    let cell_size = header_f64(&res, "X-Cell-Size");
    let grid_rows = header_f64(&res, "X-Grid-Rows");
    let grid_cols = header_f64(&res, "X-Grid-Cols");

    let bytes = res
        .bytes()
        .await
        .map_err(|e| format!("地形データの読み込みに失敗しました: {e}"))?;

    // 登録マップの配置定義を取得し、編集中の都市とは独立した作業コピーへ持ち込む。
    let props: serde_json::Value = if original {
        serde_json::json!({"instances": [], "scatters": []})
    } else {
        client
            .get(format!("{}/api/v1/terrain/maps/{}/props", base, map_id))
            .timeout(Duration::from_secs(60))
            .send()
            .await
            .map_err(|e| format!("プロップ定義の取得に失敗しました: {e}"))?
            .error_for_status()
            .map_err(|e| format!("プロップ定義の取得に失敗しました: {e}"))?
            .json()
            .await
            .map_err(|e| format!("プロップ定義の読み込みに失敗しました: {e}"))?
    };

    let resource_document: serde_json::Value = if original {
        serde_json::json!({"resources": []})
    } else {
        client
            .get(format!("{}/api/v1/terrain/maps/{}/resources", base, map_id))
            .timeout(Duration::from_secs(60))
            .send()
            .await
            .map_err(|e| format!("資源定義の取得に失敗しました: {e}"))?
            .error_for_status()
            .map_err(|e| format!("資源定義の取得に失敗しました: {e}"))?
            .json()
            .await
            .map_err(|e| format!("資源定義の読み込みに失敗しました: {e}"))?
    };
    let resources = terrain_editor_resources(&resource_document)?;

    let features: serde_json::Value = {
        client
            .get(format!("{}/api/v1/terrain/maps/{}/features?original={}", base, map_id, original))
            .timeout(Duration::from_secs(60))
            .send()
            .await
            .map_err(|e| format!("川・水面定義の取得に失敗しました: {e}"))?
            .error_for_status()
            .map_err(|e| format!("川・水面定義の取得に失敗しました: {e}"))?
            .json()
            .await
            .map_err(|e| format!("川・水面定義の読み込みに失敗しました: {e}"))?
    };

    let map_id = if original {
        format!("{}_{}", map_id, uuid::Uuid::new_v4())
    } else {
        map_id
    };
    // ウィンドウ再利用で前回の編集状態が残らないよう、取り込みごとに別のIDで開く。
    let editor_id = uuid::Uuid::new_v4().to_string();
    let map_dir = terrain_editor_dir(&app)?.join(&editor_id);
    std::fs::create_dir_all(&map_dir).map_err(|e| e.to_string())?;

    let meta = serde_json::json!({
        "map_id": map_id,
        "map_width": map_width,
        "map_height": map_height,
        "near_spacing": near_spacing,
        "far_spacing": far_spacing,
        "cell_size": cell_size,
        "grid_rows": grid_rows,
        "grid_cols": grid_cols,
        "props": props,
        "resources": resources,
        "water_surfaces": features["water_surfaces"],
        "rivers": features["rivers"],
        "camera": features["camera"],
        "background": features["background"],
    });
    std::fs::write(map_dir.join("meta.json"), meta.to_string())
        .map_err(|e| format!("meta.json の書き込みに失敗しました: {e}"))?;
    std::fs::write(map_dir.join("near.bin"), &bytes)
        .map_err(|e| format!("near.bin の書き込みに失敗しました: {e}"))?;

    log::info!("[copy_terrain_for_editor] map_id={} を保存しました", map_id);
    Ok(editor_id)
}

/// 都市生成ローダーと同じ2種類の外形を受け入れ、資源の各フィールドは加工しない。
fn terrain_editor_resources(document: &serde_json::Value) -> Result<&serde_json::Value, String> {
    let resources = if document.is_array() {
        document
    } else {
        document.get("resources").unwrap_or(&serde_json::Value::Null)
    };
    if !resources.is_array() {
        return Err("資源定義の形式が不正です(resources配列が必要です)".to_string());
    }
    Ok(resources)
}

#[cfg(test)]
mod terrain_resource_import_tests {
    use super::terrain_editor_resources;
    use serde_json::json;

    #[test]
    fn preserves_resource_fields_in_both_supported_formats() {
        let entries = json!([{
            "id": "ore", "resource_type": 3, "max_amount": 123456.5,
            "recovery_rate": 0.025,
            "polygon": {"type": "Polygon", "coordinates": [[[0, 0], [10, 0], [10, 10], [0, 0]]]}
        }]);
        let document = json!({"version": 1, "resources": entries});
        assert_eq!(terrain_editor_resources(&document).unwrap(), &entries);
        assert_eq!(terrain_editor_resources(&entries).unwrap(), &entries);
        assert_eq!(terrain_editor_resources(&json!({"resources": []})).unwrap(), &json!([]));
    }

    #[test]
    fn rejects_missing_or_invalid_resource_array() {
        for document in [json!(null), json!({}), json!({"resources": null}), json!({"resources": {}})] {
            assert!(terrain_editor_resources(&document).is_err());
        }
    }
}

/// 地形エディタが表示・編集するためのローカル作業コピーを読み込む。
///
/// copy_terrain_for_editor で保存した meta.json と near.bin を読み込み、
/// 標高データは base64 文字列として返す(float16 LE バイト列)。
#[derive(serde::Serialize)]
struct TerrainEditorData {
    map_id: String,
    map_width: f64,
    map_height: f64,
    near_spacing: f64,
    far_spacing: f64,
    cell_size: f64,
    grid_rows: u32,
    grid_cols: u32,
    /// float16 LE バイト列を base64 エンコードした文字列。
    data_base64: String,
    /// 水面ポリゴン(海面以外の湖など)の一覧。meta.json の water_surfaces をそのまま返す。
    /// 各要素は {id, water_level, kind, polygon(GeoJSONライク)}。未設定なら空配列。
    water_surfaces: serde_json::Value,
    /// 川の一覧。meta.json の rivers をそのまま返す。
    /// 各要素は {id, kind, centerline, widths, surface_offsets, footprint}(RiverPayload)。
    /// 未設定なら空配列。仕様の正は genesis-docs/docs/geometries/D_地形形状.md §D.11。
    rivers: serde_json::Value,
    /// 天然資源エリアの一覧。meta.json の resources をそのまま返す。
    /// 各要素は {id, resource_type, max_amount, recovery_rate, polygon(GeoJSONライク)}
    /// (ResourceAreaPayload)。未設定なら空配列。仕様の正は同 §D.12。
    resources: serde_json::Value,
    props: serde_json::Value,
    /// カメラ初期状態 {x, y, z, yaw, pitch}(TerrainCameraPayload)。未設定なら null。
    /// 仕様の正は同 §D.13。
    camera: serde_json::Value,
    background: serde_json::Value,
}

#[tauri::command]
fn load_terrain_editor_data(
    app: tauri::AppHandle,
    map_id: String,
) -> Result<TerrainEditorData, String> {
    use base64::Engine;
    let map_dir = terrain_editor_dir(&app)?.join(&map_id);
    let meta_str = std::fs::read_to_string(map_dir.join("meta.json"))
        .map_err(|e| format!("meta.json の読み込みに失敗しました: {e}"))?;
    let meta: serde_json::Value =
        serde_json::from_str(&meta_str).map_err(|e| e.to_string())?;
    let bytes = std::fs::read(map_dir.join("near.bin"))
        .map_err(|e| format!("near.bin の読み込みに失敗しました: {e}"))?;

    let data_base64 = base64::engine::general_purpose::STANDARD.encode(&bytes);
    Ok(TerrainEditorData {
        map_id: meta
            .get("map_id")
            .and_then(|v| v.as_str())
            .unwrap_or(&map_id)
            .to_string(),
        map_width: meta
            .get("map_width")
            .and_then(|v| v.as_f64())
            .unwrap_or(0.0),
        map_height: meta
            .get("map_height")
            .and_then(|v| v.as_f64())
            .unwrap_or(0.0),
        near_spacing: meta
            .get("near_spacing")
            .and_then(|v| v.as_f64())
            .unwrap_or(0.0),
        far_spacing: meta
            .get("far_spacing")
            .and_then(|v| v.as_f64())
            .unwrap_or(0.0),
        cell_size: meta
            .get("cell_size")
            .and_then(|v| v.as_f64())
            .unwrap_or(0.0),
        grid_rows: meta
            .get("grid_rows")
            .and_then(|v| v.as_f64())
            .unwrap_or(0.0) as u32,
        grid_cols: meta
            .get("grid_cols")
            .and_then(|v| v.as_f64())
            .unwrap_or(0.0) as u32,
        data_base64,
        water_surfaces: meta
            .get("water_surfaces")
            .cloned()
            .unwrap_or_else(|| serde_json::Value::Array(Vec::new())),
        rivers: meta
            .get("rivers")
            .cloned()
            .unwrap_or_else(|| serde_json::Value::Array(Vec::new())),
        resources: meta
            .get("resources")
            .cloned()
            .unwrap_or_else(|| serde_json::Value::Array(Vec::new())),
        props: meta
            .get("props")
            .cloned()
            .unwrap_or_else(|| serde_json::json!({"instances": [], "scatters": []})),
        camera: meta.get("camera").cloned().unwrap_or(serde_json::Value::Null),
        background: meta.get("background").cloned().unwrap_or(serde_json::Value::Null),
    })
}

/// 新しい地形を作成し、地形エディタ用の作業コピーを初期化する。
///
/// 最小サイズ(2×2セル、1000m×1000m)の標高マップを標高-10mで生成し、
/// ローカル(AppData)に作業コピーとして保存する。生成したマップIDを返す。
/// マップリサイズは 2*cell_size 刻みのため、初期マップは 2*cell_size 以上である必要がある。
#[tauri::command]
fn create_new_terrain_for_editor(app: tauri::AppHandle) -> Result<String, String> {
    let map_id = uuid::Uuid::new_v4().to_string();

    let near_spacing = 10.0f64;
    let far_spacing = 200.0f64;
    let cell_size = 500.0f64;
    let cells_x = 2u32;
    let cells_y = 2u32;
    let map_width = cell_size * cells_x as f64;   // 1000m
    let map_height = cell_size * cells_y as f64;   // 1000m
    let grid_cols = (map_width / near_spacing) as u32 + 1;   // 101
    let grid_rows = (map_height / near_spacing) as u32 + 1;  // 101

    // 標高-10.0 を float16 LE バイト列にする(-10.0 = 0xC900 LE → [0x00, 0xC9])
    let elev_f16: u16 = 0xC900;
    let lo = (elev_f16 & 0xFF) as u8;
    let hi = ((elev_f16 >> 8) & 0xFF) as u8;
    let sample = [lo, hi];
    let count = (grid_rows * grid_cols) as usize;
    let mut bytes = Vec::with_capacity(count * 2);
    for _ in 0..count {
        bytes.extend_from_slice(&sample);
    }

    let map_dir = terrain_editor_dir(&app)?.join(&map_id);
    std::fs::create_dir_all(&map_dir).map_err(|e| e.to_string())?;

    let meta = serde_json::json!({
        "map_id": map_id,
        "map_width": map_width,
        "map_height": map_height,
        "near_spacing": near_spacing,
        "far_spacing": far_spacing,
        "cell_size": cell_size,
        "grid_rows": grid_rows,
        "grid_cols": grid_cols,
    });
    std::fs::write(map_dir.join("meta.json"), meta.to_string())
        .map_err(|e| format!("meta.json の書き込みに失敗しました: {e}"))?;
    std::fs::write(map_dir.join("near.bin"), &bytes)
        .map_err(|e| format!("near.bin の書き込みに失敗しました: {e}"))?;

    log::info!(
        "[create_new_terrain_for_editor] map_id={} を作成しました ({}x{} cells, {}x{} grid, {}m x {}m)",
        map_id, cells_x, cells_y, grid_cols, grid_rows, map_width, map_height
    );
    Ok(map_id)
}

/// export_terrain_as_npy の戻り値。terrain_maps.yaml への追記可否をフロントに伝えるために使う。
#[derive(serde::Serialize)]
struct ExportTerrainResult {
    /// エクスポート先ディレクトリに terrain_maps.yaml が見つかり、エントリを追記/上書きできたか。
    /// false の場合、フロントエンドは「terrain_maps.yaml が見つからない」旨の通知を出す。
    yaml_updated: bool,
}

/// 地形エディタで編集中の標高データを、バックエンドが読み込める .npy ファイルへ変換して書き出す。
///
/// フロントエンドから受け取る標高データは既に .npy の data 部と同じ float16 リトルエンディアンの
/// 生バイト列なので、変換は build_npy_float16 でヘッダを被せるだけで完結する(外部プロセスや
/// 一時ファイルは介さない)。
///
/// エクスポート先と同じディレクトリに terrain_maps.yaml が存在する場合は、そのマップのエントリを
/// 追記(同じ map_id が既にあれば上書き)する。存在しなければ何もせず、戻り値の yaml_updated を
/// false にしてフロントエンドに伝える(フロントエンド側で「見つからない」旨の通知を出す)。
#[tauri::command]
fn export_terrain_as_npy(
    map_id: String,
    map_width: f64,
    map_height: f64,
    near_spacing: f64,
    far_spacing: f64,
    cell_size: f64,
    grid_rows: u32,
    grid_cols: u32,
    data_base64: String,
    out_path: String,
    thumbnail_base64: Option<String>,
    water_surfaces: Option<serde_json::Value>,
    rivers: Option<serde_json::Value>,
    resources: Option<serde_json::Value>,
    props: Option<serde_json::Value>,
    camera: Option<serde_json::Value>,
    background: Option<serde_json::Value>,
) -> Result<ExportTerrainResult, String> {
    use base64::Engine;
    // 付随JSONを含め、最初のファイルを書く前に原本への出力を拒否する。
    let checked_path = terrain_export_paths::checked_export_path(std::path::Path::new(&out_path))?;
    let out_path = checked_path.to_string_lossy().into_owned();
    let bytes = base64::engine::general_purpose::STANDARD
        .decode(&data_base64)
        .map_err(|e| format!("標高データのデコードに失敗しました: {e}"))?;

    let expected_len = (grid_rows as usize) * (grid_cols as usize) * 2;
    if bytes.len() != expected_len {
        return Err(format!(
            "標高データのサイズが不正です(期待値: {expected_len} バイト、実際: {} バイト)",
            bytes.len()
        ));
    }

    let props = props.unwrap_or_else(|| serde_json::json!({"instances": [], "scatters": []}));
    let props_doc = serde_json::json!({"version": 1, "props": props});
    std::fs::write(std::path::Path::new(&out_path).with_extension("props.json"), props_doc.to_string())
        .map_err(|e| format!("プロップ定義の書き込みに失敗しました: {e}"))?;

    let npy = build_npy_float16(&bytes, grid_rows as usize, grid_cols as usize);
    std::fs::write(&out_path, &npy)
        .map_err(|e| format!(".npyファイルの書き込みに失敗しました: {e}"))?;

    // サムネイル画像は .npy と同じファイル名(拡張子だけ.png)で同じディレクトリに書き出す。
    // これは genesis-core側の terrain_registry が「.npyの拡張子をpngに置き換えた名前」を
    // サムネイルパスの命名規則として前提にしているため(terrain_maps.yamlへの新規フィールド追加を避ける)。
    if let Some(thumb_b64) = thumbnail_base64 {
        let thumb_bytes = base64::engine::general_purpose::STANDARD
            .decode(&thumb_b64)
            .map_err(|e| format!("サムネイル画像のデコードに失敗しました: {e}"))?;
        let thumb_path = std::path::Path::new(&out_path).with_extension("png");
        std::fs::write(&thumb_path, &thumb_bytes)
            .map_err(|e| format!("サムネイル画像の書き込みに失敗しました: {e}"))?;
    }

    // 水面ポリゴン(海面以外の湖など)は .npy と同じファイル名(拡張子だけ .water.json)で
    // 同じディレクトリに書き出す。genesis-core側の terrain_registry.get_water_surfaces_path が
    // 「.npyの拡張子を .water.json に置き換えた名前」を命名規則として前提にしているため
    // (terrain_maps.yaml への新規フィールド追加を避ける)。水面が空でもファイルを書き出し、
    // 以前のエクスポートで残った古い水面が読まれ続けるのを防ぐ。
    {
        let water_surfaces = water_surfaces.unwrap_or_else(|| serde_json::Value::Array(Vec::new()));
        let water_doc = serde_json::json!({
            "version": 1,
            "water_surfaces": water_surfaces,
        });
        // out_path が "map_foo.npy" なら "map_foo.water.json" にする。
        let water_path = std::path::Path::new(&out_path).with_extension("water.json");
        std::fs::write(&water_path, water_doc.to_string())
            .map_err(|e| format!("水面定義ファイルの書き込みに失敗しました: {e}"))?;
    }

    // 川は .npy と同じファイル名(拡張子だけ .rivers.json)で同じディレクトリに書き出す
    // (.water.json と同じ命名規則・方針)。川床の彫り込み自体は標高データ(.npy)に含まれて
    // おり、このファイルは川の範囲ポリゴン(polygon)と水面標高のプロファイル線(profile)を持つ。
    // 川が無くてもファイルを書き出し、以前のエクスポートで残った古い川が読まれ続けるのを防ぐ。
    // version 2 = 範囲ポリゴン方式(1 はスプライン帯方式で、バックエンドは読まない)。
    // 仕様の正は D_地形形状.md §D.11。
    {
        let rivers = rivers.unwrap_or_else(|| serde_json::Value::Array(Vec::new()));
        let rivers_doc = serde_json::json!({
            "version": 2,
            "rivers": rivers,
        });
        // out_path が "map_foo.npy" なら "map_foo.rivers.json" にする。
        let rivers_path = std::path::Path::new(&out_path).with_extension("rivers.json");
        std::fs::write(&rivers_path, rivers_doc.to_string())
            .map_err(|e| format!("川定義ファイルの書き込みに失敗しました: {e}"))?;
    }

    // 天然資源エリアは .npy と同じファイル名(拡張子だけ .resources.json)で同じディレクトリに
    // 書き出す(.water.json と同じ命名規則・方針)。genesis-core側の terrain_registry が
    // この命名規則を前提に読み込み、新規都市の初期化時に資源エンティティとして配置する。
    // 資源が無くてもファイルを書き出し、以前のエクスポートで残った古い資源が読まれ続けるのを防ぐ。
    // 仕様の正は D_地形形状.md §D.12。
    {
        let resources = resources.unwrap_or_else(|| serde_json::Value::Array(Vec::new()));
        let resources_doc = serde_json::json!({
            "version": 1,
            "resources": resources,
        });
        // out_path が "map_foo.npy" なら "map_foo.resources.json" にする。
        let resources_path = std::path::Path::new(&out_path).with_extension("resources.json");
        std::fs::write(&resources_path, resources_doc.to_string())
            .map_err(|e| format!("資源定義ファイルの書き込みに失敗しました: {e}"))?;
    }

    log::info!("[export_terrain_as_npy] out_path={} へエクスポートしました", out_path);

    // カメラ初期状態はサイドカーではなく terrain_maps.yaml のエントリ(camera:)に書く。
    // 5つのスカラーだけなので専用ファイルを増やさず、バックエンドの terrain_registry が
    // マップ定義の一部として読む。数値が揃っていない値は未設定として扱う。
    let camera_spec: Option<TerrainCameraSpec> = camera
        .and_then(|v| serde_json::from_value::<TerrainCameraSpec>(v).ok());

    // エクスポート先と同じディレクトリの terrain_maps.yaml にエントリを追記する。
    // 存在しなければ何もせず yaml_updated=false を返し、フロントエンドが通知を出す。
    let out_path_buf = std::path::Path::new(&out_path);
    let yaml_updated = match out_path_buf.parent() {
        Some(dir) => {
            let yaml_path = dir.join("terrain_maps.yaml");
            if yaml_path.exists() {
                let file_name = terrain_export_paths::registry_file_name(out_path_buf);
                upsert_terrain_yaml_entry(
                    &yaml_path,
                    &map_id,
                    file_name,
                    near_spacing,
                    map_width,
                    map_height,
                    near_spacing,
                    far_spacing,
                    cell_size,
                    camera_spec.as_ref(),
                    background.as_ref(),
                )?;
                true
            } else {
                false
            }
        }
        None => false,
    };

    Ok(ExportTerrainResult { yaml_updated })
}

/// terrain_maps.yaml のエントリに書くカメラ初期状態(TerrainCameraPayload と同じ契約)。
/// 位置はワールド座標(m)、yaw は北(+Z)を0とする時計回りの度、pitch は見下ろし角の度。
#[derive(serde::Deserialize)]
struct TerrainCameraSpec {
    x: f64,
    y: f64,
    z: f64,
    yaw: f64,
    pitch: f64,
}

/// terrain_maps.yaml に1マップ分のエントリをテキストベースで追記/上書きする。
///
/// serde_yamlでパース→再シリアライズすると、既存の他エントリのコメント(`# 与論島`等)や
/// 空行区切りが失われてしまう。terrain_maps.yaml はトップレベルキー(`{map_id}:`、行頭に
/// インデントなし)ごとにブロックが並ぶ単純な構造であることを前提に、対象ブロックの行範囲
/// だけをテキストとして置換/追記することで、無関係な箇所の見た目を変えない。
///
/// ブロックの開始位置は、キー行(`{id}:`)の直前に空行を挟まず連続するコメント行(`# 名前`等)
/// があればそこまで遡ったものとする。これにより、次のエントリの見出しコメントを誤って
/// 対象ブロックに含めてしまうことを避ける。
#[allow(clippy::too_many_arguments)]
fn upsert_terrain_yaml_entry(
    yaml_path: &std::path::Path,
    map_id: &str,
    file_name: String,
    src_spacing: f64,
    map_width: f64,
    map_height: f64,
    near_map_spacing: f64,
    far_map_spacing: f64,
    cell_size: f64,
    camera: Option<&TerrainCameraSpec>,
    background: Option<&serde_json::Value>,
) -> Result<(), String> {
    let original = std::fs::read_to_string(yaml_path)
        .map_err(|e| format!("terrain_maps.yaml の読み込みに失敗しました: {e}"))?;
    let lines: Vec<&str> = original.lines().collect();

    // トップレベルキー行(行頭にインデントがなく、コメント/区切り線でもない `xxx:` 形式の行)を探す。
    let is_top_level_key = |line: &str| -> Option<String> {
        if line.is_empty() || line.starts_with(char::is_whitespace) || line.starts_with('#') || line.starts_with("---") {
            return None;
        }
        let colon_pos = line.find(':')?;
        let key = &line[..colon_pos];
        if !key.is_empty() && key.chars().all(|c| c.is_alphanumeric() || c == '_' || c == '-') {
            Some(key.to_string())
        } else {
            None
        }
    };

    let key_line_idxs: Vec<usize> = lines
        .iter()
        .enumerate()
        .filter(|(_, line)| is_top_level_key(line).is_some())
        .map(|(i, _)| i)
        .collect();

    // 各キー行の「ブロック開始位置」= 直前に空行を挟まず連続するコメント行があればその先頭。
    let block_start_of = |key_idx: usize| -> usize {
        let mut start = key_idx;
        while start > 0 && lines[start - 1].starts_with('#') {
            start -= 1;
        }
        start
    };

    // カメラ初期状態が未設定ならキー自体を書かない(バックエンドは未設定=原点の既定視点)。
    let camera_text = match camera {
        Some(c) => format!(
            "\n  camera:\n    x: {x:.1}\n    y: {y:.1}\n    z: {z:.1}\n    yaw: {yaw:.1}\n    pitch: {pitch:.1}",
            x = c.x,
            y = c.y,
            z = c.z,
            yaw = c.yaw,
            pitch = c.pitch,
        ),
        None => String::new(),
    };

    // 背景は不変の原本を参照する。JSONのオブジェクト表記はYAMLでも有効。
    let background_text = background.filter(|v| !v.is_null())
        .map(|v| format!("\n  background: {v}")).unwrap_or_default();
    let entry_text = format!(
        "# {map_id}\n\
         {map_id}:\n\
        \x20 id: {map_id}\n\
        \x20 name: {map_id}\n\
        \x20 file_name: {file_name}\n\
        \x20 src_spacing: {src_spacing:.1}\n\
        \x20 settings:\n\
        \x20   map_width: {map_width}\n\
        \x20   map_height: {map_height}\n\
        \x20   near_map_spacing: {near_map_spacing}\n\
        \x20   far_map_spacing: {far_map_spacing}\n\
        \x20   dynamic_map_spacing: 1\n\
        \x20   cell_size: {cell_size}{camera_text}{background_text}",
        map_width = map_width as i64,
        map_height = map_height as i64,
        near_map_spacing = near_map_spacing as i64,
        far_map_spacing = far_map_spacing as i64,
        cell_size = cell_size as i64,
    );

    // 既存の同一IDブロックを探す。見つかればその範囲を置換、なければ末尾に追記する。
    let existing_range = key_line_idxs.iter().enumerate().find_map(|(idx, &key_idx)| {
        if is_top_level_key(lines[key_idx]).as_deref() == Some(map_id) {
            let block_start = block_start_of(key_idx);
            let mut block_end = key_line_idxs.get(idx + 1).map(|&next_key_idx| block_start_of(next_key_idx)).unwrap_or(lines.len());
            // ブロック末尾の空行は置換対象から除外し、次のブロックとの間隔を保つ。
            while block_end > block_start && lines[block_end - 1].trim().is_empty() {
                block_end -= 1;
            }
            Some((block_start, block_end))
        } else {
            None
        }
    });

    let mut new_lines: Vec<String> = lines.iter().map(|s| s.to_string()).collect();
    let entry_lines: Vec<String> = entry_text.lines().map(|s| s.to_string()).collect();

    if let Some((start, end)) = existing_range {
        new_lines.splice(start..end, entry_lines);
    } else {
        // 末尾の空行を正規化してから、区切りの空行1つを挟んで新規ブロックを追記する。
        while new_lines.last().map(|l| l.trim().is_empty()).unwrap_or(false) {
            new_lines.pop();
        }
        new_lines.push(String::new());
        new_lines.extend(entry_lines);
    }

    let mut output = new_lines.join("\n");
    output.push('\n');
    std::fs::write(yaml_path, output).map_err(|e| format!("terrain_maps.yaml の書き込みに失敗しました: {e}"))
}

#[cfg(test)]
mod terrain_yaml_entry_tests {
    use super::upsert_terrain_yaml_entry;

    const EXISTING: &str = "# island\nisland:\n  id: island\n  name: island\n  file_name: terrains/map_island.npy\n  src_spacing: 10.0\n  settings:\n    map_width: 1000\n    map_height: 1000\n    near_map_spacing: 10\n    far_map_spacing: 200\n    dynamic_map_spacing: 1\n    cell_size: 500\n";

    /// 同じIDのブロックを置換する形で書き出し、書かれた内容を返す。
    fn upsert() -> String {
        let dir = std::env::temp_dir().join(format!("cg_yaml_test_{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("terrain_maps.yaml");
        std::fs::write(&path, EXISTING).unwrap();
        upsert_terrain_yaml_entry(
            &path, "island", "terrains/map_island.npy".to_string(),
            10.0, 1000.0, 1000.0, 10.0, 200.0, 500.0, None, Some(&serde_json::json!({"file_name":"terrains/original/bg.npy"})),
        ).unwrap();
        let text = std::fs::read_to_string(&path).unwrap();
        std::fs::remove_dir_all(&dir).ok();
        text
    }

    /// 既存の同一IDブロックは重複追記されず、見出しコメントと settings を保ったまま置き換わる。
    #[test]
    fn replaces_existing_block_in_place() {
        let text = upsert();
        assert_eq!(text.matches("\nisland:\n").count(), 1, "{text}");
        assert!(text.starts_with("# island\n"), "{text}");
        assert!(text.contains("    cell_size: 500\n"), "{text}");
        assert!(text.contains("terrains/original/bg.npy"), "{text}");
    }
}

/// float16(リトルエンディアン)の生バイト列に NumPy の .npy(バージョン1.0)ヘッダを被せる。
///
/// 構造は「マジック(\x93NUMPY) + バージョン(1,0) + ヘッダ長(u16 LE) + ヘッダ辞書 + データ部」。
/// ヘッダ辞書は改行で終わるASCII文字列で、データ部が64バイト境界から始まるよう空白で詰める
/// (numpy自身の書き出し規約。np.load は境界に依存しないが、既存の assets/*.npy と
/// バイナリ互換な体裁にしておく)。
///
/// dtype は '<f2' 固定。バックエンド(terrain_registry.build_near_map)は np.load した配列を
/// float32へキャストしてから使うため、海底の負の標高もそのまま保持される。
fn build_npy_float16(data: &[u8], rows: usize, cols: usize) -> Vec<u8> {
    let dict = format!("{{'descr': '<f2', 'fortran_order': False, 'shape': ({rows}, {cols}), }}");
    // マジック6 + バージョン2 + ヘッダ長2 = 10バイトの後に辞書が続く。辞書末尾の改行1バイトを
    // 含めた長さを64の倍数へ切り上げた差分が、詰める空白の数になる。
    let unpadded = 10 + dict.len() + 1;
    let header_len = dict.len() + 1 + (64 - unpadded % 64) % 64;

    let mut out = Vec::with_capacity(10 + header_len + data.len());
    out.extend_from_slice(b"\x93NUMPY\x01\x00");
    out.extend_from_slice(&(header_len as u16).to_le_bytes());
    out.extend_from_slice(dict.as_bytes());
    out.resize(10 + header_len - 1, b' ');
    out.push(b'\n');
    out.extend_from_slice(data);
    out
}

/// デバッグ初期化を実行してゲームセッションを開始する。
/// Unity を起動し、topmenu を隠して Tipsを表示した後、
/// バックエンドに POST /api/v1/system/debug-init を送信する。
#[tauri::command]
async fn start_debug_init(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
) -> Result<(), String> {
    log::info!("[start_debug_init] デバッグ初期化を開始");

    window::launch_graphic(state.inner())?;

    if let Some(topmenu) = app.get_webview_window("topmenu") {
        let _ = topmenu.hide();
    }

    // ゲーム開始の確定に伴い、開いているポップアップを全て閉じる
    window::close_all_popup_windows(&app, state.inner());

    begin_initial_loading(&app, state.inner(), false)?;


    let api_base_url = {
        let guard = state.lock();
        guard.api_base_url.clone()
    };
    let url = format!("{}/api/v1/system/debug-init", api_base_url.trim_end_matches('/'));

    let client = reqwest::Client::new();
    let res = client
        .post(&url)
        .timeout(Duration::from_secs(60))
        .send()
        .await
        .map_err(|e| { abort_initial_loading(&app, state.inner()); format!("セッション作成リクエストに失敗しました: {e}") })?;

    if !res.status().is_success() {
        let status = res.status();
        let body = res.text().await.unwrap_or_default();
        log::error!("[start_new_game] セッション作成失敗: {} {}", status, body);
        abort_initial_loading(&app, state.inner());
        if let Some(topmenu) = app.get_webview_window("topmenu") {
            let _ = topmenu.show();
        }
        {
            let mut guard = state.lock();
            guard.is_loading = false;
        }
        return Err(format!("セッション作成に失敗しました: {status}"));
    }

    log::info!("[start_new_game] セッション作成リクエスト成功。ポーリング待機に委譲");
    state.lock().backend_load_complete = true;
    Ok(())
}

/// バックエンドプロセスを起動する。
/// 既に起動している場合は何もしない。
/// api_base_url からポート番号を抽出し、環境変数 GENESIS_API_PORT に設定して起動する。
#[tauri::command]
fn start_backend(state: tauri::State<'_, SharedState>) -> Result<Value, String> {
    let (launch_cmd, api_base_url) = {
        let guard = state.lock();
        (guard.backend_launch_command.clone(), guard.api_base_url.clone())
    };

    if launch_cmd.is_empty() {
        return Err("バックエンド起動コマンドが設定されていません".to_string());
    }

    {
        let guard = state.lock();
        if guard.backend_process.is_some() {
            return Ok(serde_json::json!({"status": "already_running"}));
        }
    }

    let port = api_base_url
        .rsplit(':')
        .next()
        .and_then(|s| s.trim_end_matches('/').parse::<u16>().ok())
        .unwrap_or(5000);

    let command = launch_cmd.replace("{port}", &port.to_string());

    log::info!("[start_backend] バックエンドを起動します: {}", command);

    let child = std::process::Command::new("cmd")
        .args(["/C", &command])
        .env("GENESIS_API_PORT", port.to_string())
        .spawn()
        .map_err(|e| format!("バックエンドの起動に失敗しました: {e}"))?;

    {
        let mut guard = state.lock();
        guard.backend_process = Some(child);
    }

    Ok(serde_json::json!({"status": "started", "port": port}))
}

/// バックエンドプロセスを強制終了する。
#[tauri::command]
fn stop_backend(state: tauri::State<'_, SharedState>) -> Result<Value, String> {
    let mut guard = state.lock();
    if let Some(ref mut child) = guard.backend_process {
        let _ = child.kill();
        let _ = child.wait();
        guard.backend_process = None;
        log::info!("[stop_backend] バックエンドプロセスを終了しました");
        Ok(serde_json::json!({"status": "stopped"}))
    } else {
        Ok(serde_json::json!({"status": "not_running"}))
    }
}

/// バックエンドを再起動する。
/// GPU設定変更後に呼び出される。既存プロセスを終了してから新しいプロセスを起動する。
#[tauri::command]
fn restart_backend(state: tauri::State<'_, SharedState>) -> Result<Value, String> {
    stop_backend(state.clone())?;
    std::thread::sleep(Duration::from_millis(500));
    start_backend(state)
}

/// アプリ全体の終了シーケンス。終了要求の発生源(終了ボタン・ウィンドウの×・
/// バックエンド発の shutting_down 検知)によらず、必ずこの関数に合流させる。
///
/// 1. Unity へ system:shutdown IPC を送る(未接続・送信失敗は無視)
/// 2. バックエンドへ POST /system/shutdown?mode=app を送る。
///    genesis.py スーパーバイザ監視下では、これが緊急オートセーブと
///    全プロセスの後始末を統括する(レスポンスは即座に返る)
/// 3. UI 自身を終了する
///
/// バックエンドが既に死んでいる場合もタイムアウト(2秒)で必ず 3 に到達する。
fn request_app_exit(app: &tauri::AppHandle, state: &SharedState) {
    // 1. Unity へ終了通知(Unity 側は ui:action の system:shutdown で Application.Quit する)
    let _ = ipc::send_ui_action(
        state,
        String::new(),
        None,
        "system:shutdown".to_string(),
        serde_json::json!({}),
    );

    // 2. バックエンドへ統括シャットダウンを要求
    let base = {
        let guard = state.lock();
        guard.api_base_url.trim_end_matches('/').to_string()
    };
    if !base.is_empty() {
        let client = reqwest::blocking::Client::builder()
            .timeout(Duration::from_secs(2))
            .build()
            .unwrap_or_default();
        let _ = client
            .post(format!("{}/api/v1/system/shutdown?mode=app", base))
            .send();
    }

    // 3. UI 自身を終了
    app.exit(0);
}

/// アプリケーション全体を終了する（トップメニューの「終了」ボタン用）
#[tauri::command]
fn exit_app(app: tauri::AppHandle, state: tauri::State<'_, SharedState>) -> Result<(), String> {
    let state = state.inner().clone();
    // HTTP 送信を含むため UI スレッドをブロックしないよう別スレッドで実行する
    std::thread::spawn(move || {
        request_app_exit(&app, &state);
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
fn dismiss_notification(
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
fn resize_notification_window(
    app: tauri::AppHandle,
    state: tauri::State<'_, SharedState>,
    height: f64,
) -> Result<(), String> {
    window::resize_notification_window(&app, state.inner(), height)
}

/// ロード要求の世代を進め、前セッションの完了通知を無視する。
fn begin_initial_loading(app: &tauri::AppHandle, state: &SharedState, needs_setup: bool) -> Result<(), String> {
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
        guard.last_save_id = None;
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
        // 接続済みなら描画完了を待ち、配布ビルドにはこの省略処理を含めない。
        #[cfg(all(debug_assertions, dev))]
        {
            guard.graphic_load_ready = !guard.graphic_ready_seen;
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

/// セッション状態を定期的にポーリングし、全ウィンドウに配信するスレッドを起動する。
///
/// 5秒ごとにバックエンドの `/api/v1/sessions` と `/api/v1/city` を取得し、
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

        // 連続失敗回数をカウントする。本番ビルド（exe）でのみ終了判定に使用する。
        let mut consecutive_failures: u32 = 0;

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
            let (polled_loading_id, load_complete_before_poll) = {
                let guard = state.lock();
                (guard.loading_id, guard.backend_load_complete)
            };

            // セッション情報を取得
            // send() 失敗や status != 2xx の場合はリクエスト失敗として扱い、
            // current_save_id の変化判定をスキップする。これにより一時的な通信エラーで
            // ゲーム終了を誤判定するのを防ぐ。
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
                                    Ok(json) => Some(json),
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

            // バックエンド発の統括シャットダウン検知。
            // genesis.py スーパーバイザがシャットダウンを開始すると /sessions に
            // shutting_down: true が立つ(Unity ウィンドウの×・クラッシュループ等)。
            // これがコア→UI 方向の唯一の終了通知経路(ポーリング便乗)なので、
            // 検知したら Unity へ終了を転送して自発終了する。
            let shutting_down = session_data
                .as_ref()
                .and_then(|d| d.get("shutting_down"))
                .and_then(|v| v.as_bool())
                .unwrap_or(false);
            if shutting_down {
                log::info!("[session-poll] バックエンドのシャットダウンを検知しました。UIを終了します");
                let _ = ipc::send_ui_action(
                    &state,
                    String::new(),
                    None,
                    "system:shutdown".to_string(),
                    serde_json::json!({}),
                );
                app.exit(0);
                break;
            }

            let current_save_id = session_data
                .as_ref()
                .and_then(|d| d.get("current_save_id"))
                .and_then(|v| v.as_str());

            let request_ok = session_data.is_some();

            // リクエスト失敗時は前回の current_save_id を維持してイベントを配信する。
            // これによりフロントエンドが current_save_id == null を受信して
            // 誤った表示切り替え（TopMenu が前面に出るなど）をするのを防ぐ。
            let current_save_id_for_emit: Option<String> = if request_ok {
                current_save_id.map(|s| s.to_string())
            } else {
                let guard = state.lock();
                guard.last_save_id.clone()
            };

            // 都市情報を取得（Taskbar の cityName / currentDate 用）
            // 失敗してもセッション情報には影響しない
            let city_data: Option<Value> = client
                .get(format!("{}/api/v1/city", base))
                .send()
                .ok()
                .and_then(|r| {
                    if r.status().is_success() {
                        r.json().ok()
                    } else {
                        None
                    }
                });

            let is_loading = {
                let guard = state.lock();
                guard.is_loading
            };

            let tutorial_id = {
                let guard = state.lock();
                guard.tutorial_id.clone()
            };

            // 全ウィンドウにセッション状態を配信
            let _ = app.emit(
                "session-state",
                serde_json::json!({
                    "current_save_id": current_save_id_for_emit,
                    "session": session_data,
                    "city": city_data,
                    "apiBaseUrl": api_base_url,
                    "isLoading": is_loading,
                    "tutorialId": tutorial_id,
                }),
            );

            // current_save_id の変化を検知
            // リクエストが失敗した場合は変化判定をスキップし、前回の last_save_id を維持する。
            // これにより一時的な通信エラーでゲーム終了を誤判定するのを防ぐ。
            if !request_ok {
                consecutive_failures += 1;
                log::warn!(
                    "[session-poll] リクエスト失敗のため current_save_id の変化判定をスキップ（連続失敗: {}回）",
                    consecutive_failures
                );

                // 本番ビルド（exe）でのみ、バックエンドへの通信が2回連続で失敗したらUIを終了する。
                // 開発中（tauri dev）はゲームを維持し、一時的な通信切れでも終了しない。
                if cfg!(not(dev)) && consecutive_failures >= 2 {
                    log::error!("[session-poll] バックエンドへの通信が2回連続で失敗しました。アプリケーションを終了します。");
                    app.exit(1);
                    break;
                }

                std::thread::sleep(Duration::from_secs(5));
                continue;
            } else {
                consecutive_failures = 0;
            }

            let (prev_save_id, is_loading) = {
                let guard = state.lock();
                (guard.last_save_id.clone(), guard.is_loading)
            };

            let save_id_changed = match (&prev_save_id, current_save_id) {
                (None, Some(_)) => true,
                (Some(a), Some(b)) => a != b,
                (Some(_), None) => true,
                (None, None) => false,
            };

            // current_save_id の変化はゲーム開始・終了の検知ログで通知するため、
            // 変化がない場合は何も出力しない。

            let load_completed = {
                let mut guard = state.lock();
                let ready = load_complete_before_poll && guard.backend_load_complete
                    && guard.loading_id == polled_loading_id && current_save_id.is_some();
                if ready { guard.backend_load_complete = false; }
                ready
            };
            if (save_id_changed && !is_loading) || load_completed {
                let new_save_id = current_save_id.map(|s| s.to_string());
                {
                    let mut guard = state.lock();
                    guard.last_save_id = new_save_id.clone();
                }

                // non-null: ゲーム開始またはセッション切り替え
                if new_save_id.is_some() {
                    log::info!(
                        "[session-poll] current_save_id が設定されました。ゲーム開始を検知"
                    );

                    if !is_loading {
                        if let Err(error) = begin_initial_loading(&app, &state, false) {
                            log::error!("初期ロード画面を開けません: {error}");
                        }
                    }
                    // POSTの応答前にセッションを検知しても、準備完了まではロードしない。
                    if load_completed || !is_loading { send_game_start_ipc(&state, &app); }

                }

                // non-null -> null: ゲーム終了
                if prev_save_id.is_some() && new_save_id.is_none() {
                    log::info!("[session-poll] current_save_id がクリアされました。ゲーム終了を検知");

                    // 開いているすべてのポップアップウィンドウ（モーダル・パネル）を閉じる。
                    // トップメニューに戻る前に残っている UI を一掃し、編集モードの後始末も行う。
                    window::close_all_popup_windows(&app, &state);

                    // 通知ウィンドウも非表示にして、次のゲーム開始時に前回の通知が残らないようにする。
                    let _ = window::dismiss_notification(&app, &state);

                    // Tips ウィンドウを非表示にする/閉じる
                    window::close_tips_window(&app);

                    // HUD・Hintbar を隠して topmenu を表示
                    window::set_hud_visible(&app, false);
                    if let Some(hintbar) = app.get_webview_window("hintbar") {
                        let _ = hintbar.hide();
                    }
                    // チュートリアルウィンドウも非表示にする
                    if let Some(tutorial) = app.get_webview_window("tutorial") {
                        let _ = tutorial.hide();
                    }
                    let _ = app.emit("tutorial:hide", ());
                    if let Some(topmenu) = app.get_webview_window("topmenu") {
                        let _ = topmenu.show();
                    }
                    window::close_tips_window(&app);

                    {
                        let mut guard = state.lock();
                        guard.is_loading = false;
                        guard.graphic_load_ready = false;
                        guard.awaiting_parameter_setup = false;
                        guard.tutorial_id = None;
                    }

                    let _ = ipc::send_ui_action(
                        &state,
                        "".to_string(),
                        None,
                        "game:end".to_string(),
                        serde_json::json!({}),
                    );
                }
            }

            // セッション状態を保存
            {
                let mut guard = state.lock();
                guard.session_state = session_data;
            }

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

    let shared_state = SharedState::new(
        load_registry(),
        load_mutex_groups(),
        api_base_url(),
        load_graphic_launch_command(),
        load_backend_launch_command(),
    );
    let setup_state = shared_state.clone();
    let event_state = shared_state.clone();

    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_dialog::init())
        .manage(shared_state.clone())
        .setup(move |app| {
            if let Some(main_window) = app.get_webview_window("main") {
                let _ = main_window.hide();
            }

            let loaded_settings = settings::load_settings(app.handle());
            {
                let mut guard = setup_state.lock();
                guard.current_monitor_index = loaded_settings.monitor_index;
            }

            let monitors: Vec<_> = app.available_monitors()
                .unwrap_or_default()
                .into_iter()
                .collect();

            let monitor_index = setup_state.lock().current_monitor_index;
            let target_monitor = monitors.get(monitor_index)
                .cloned()
                .or_else(|| app.primary_monitor().unwrap_or_default());

            let mut size = tauri::LogicalSize::new(1920.0, 1080.0);
            let mut pos = tauri::LogicalPosition::new(0.0, 0.0);

            if let Some(primary) = target_monitor {
                let scale = primary.scale_factor();
                size = primary.size().to_logical::<f64>(scale);
                pos = primary.position().to_logical::<f64>(scale);
            } else {
                log::warn!("No monitor detected. Using default 1920x1080 positioning.");
            }

            // トップメニューウィンドウを target_monitor（設定ファイルの monitor_index に対応するモニター、
            // またはプライマリモニター）の中央に生成する
            let topmenu_x = pos.x + (size.width - TOPMENU_WIDTH) / 2.0;
            let topmenu_y = pos.y + (size.height - TOPMENU_HEIGHT) / 2.0;

            let _topmenu = WebviewWindowBuilder::new(
                app,
                "topmenu",
                WebviewUrl::App("index.html".into()),
            )
            .title("City Genesis")
            .decorations(false)
            .transparent(true)
            .always_on_top(true)
            .resizable(false)
            .inner_size(TOPMENU_WIDTH, TOPMENU_HEIGHT)
            .position(topmenu_x, topmenu_y)
            .build()
            .expect("failed to create topmenu window");

            // HUDウィンドウ3枚(左端のアイコンパネル・下部中央のコックピット・右下のシステムトレイ)を
            // target_monitor 上に生成する。初期状態では非表示で、
            // GET /api/v1/sessions の current_save_id が非nullになったポーリングで表示される。
            window::create_hud_windows(app.handle(), &monitors, monitor_index)
                .expect("failed to create HUD windows");

            // ヒントバーウィンドウを target_monitor の上部中央に生成する。
            // 初期状態では表示。セッション未ロード時に session-state イベントで非表示となる。
            // 幅は画面の 55%(CSS 側はウィンドウ幅いっぱいに追従する)。
            // 高さは CSS での実際の表示サイズ（padding + border + 2行テキスト）にぴったり合わせ、
            // 透明なクリックできない余白ができないようにする。
            let hintbar_width = size.width * 0.55;
            let hintbar_height = 68.0;
            let hintbar_x = pos.x + (size.width - hintbar_width) / 2.0;
            let hintbar_y = pos.y + 4.0;

            let hintbar = WebviewWindowBuilder::new(
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
            .resizable(false)
            .visible(true)
            .inner_size(hintbar_width, hintbar_height)
            .position(hintbar_x, hintbar_y)
            .build()
            .expect("failed to create hintbar window");
            let _ = hintbar.set_ignore_cursor_events(true);

            // チュートリアルウィンドウを target_monitor の上部中央に生成する。
            // ヒントバーの直下（4px + 68px + 4px = 76px）に配置する。
            // 初期状態は非表示で、テンプレートから新規ゲームを開始した際に表示される。
            // 幅は画面の 55% でヒントバーと同じ、高さは 200px とする。
            let tutorial_width = size.width * 0.55;
            let tutorial_height = 200.0;
            let tutorial_x = pos.x + (size.width - tutorial_width) / 2.0;
            let tutorial_y = pos.y + 76.0; // hintbar(68px) + gap(4px) + top(4px)

            let _tutorial = WebviewWindowBuilder::new(
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
            .resizable(false)
            .visible(false)
            .inner_size(tutorial_width, tutorial_height)
            .position(tutorial_x, tutorial_y)
            .build()
            .expect("failed to create tutorial window");

            // 通知ポップアップウィンドウを事前に作成しておく。
            // 表示時にウィンドウを都度生成する方式だと、アニメーション開始までの遅延が大きくなるため、
            // 非表示のまま準備しておき、内容が決まってから show() する方式を採用する。
            let notification_margin = 16.0;
            let notification_width = 320.0;
            let notification_max_height = 200.0;
            let notification_x = pos.x + size.width - notification_width - notification_margin;
            let notification_y = pos.y + size.height - window::HUD_RIGHT_BOTTOM_RESERVED - notification_max_height - notification_margin;

            let _notification = WebviewWindowBuilder::new(
                app,
                "notification",
                WebviewUrl::App("index.html".into()),
            )
            .title("Notification")
            .decorations(false)
            .transparent(true)
            .always_on_top(true)
            .skip_taskbar(true)
            .resizable(false)
            .visible(false)
            .inner_size(notification_width, notification_max_height)
            .position(notification_x, notification_y)
            .build()
            .expect("failed to create notification window");

            ipc::start_ipc(app.handle().clone(), setup_state.clone());
            start_session_polling(setup_state.clone(), app.handle().clone());
            Ok(())
        })
        .on_window_event({
            move |window, event| {
                match event {
                    // ウィンドウの×ボタン(Alt+F4含む)。
                    // - main/topmenu/HUD各枚 はアプリの主要サーフェスなので、
                    //   閉じる操作を「アプリ全体の終了要求」として扱い、
                    //   終了シーケンス(Unity通知→バックエンド統括shutdown→自終了)に合流させる。
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
                                    request_app_exit(&app, &state);
                                });
                            }
                            "parameter-setup" if event_state.lock().awaiting_parameter_setup => { api.prevent_close(); }
                            "notification" | "hintbar" | "tutorial" | "parameter-setup" => {}
                            "tips" if event_state.lock().is_loading => { api.prevent_close(); }
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
            get_connection_status,
            resize_icon_panel,
            resize_system_tray,
            get_topmenu_state,
            switch_monitor,
            open_registered_window,
            close_registered_window,
            launch_graphic,
            start_new_game,
            start_load_game,
            start_debug_init,
            create_session,
            start_backend,
            stop_backend,
            restart_backend,
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
            copy_terrain_for_editor,
            create_new_terrain_for_editor,
            load_terrain_editor_data,
            export_terrain_as_npy,
            open_save_directory,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

