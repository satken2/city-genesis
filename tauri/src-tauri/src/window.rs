use crate::ipc;
use crate::messages::{EntityRef, NotificationPayload, OpenWindowPayload, WindowContextDto, WindowDefinition, WindowLayout};
use crate::state::{OpenWindowState, SharedState, WindowGeometry, MAX_HIDDEN_WINDOWS};
use serde_json::{json, Value};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;
use tauri::{AppHandle, Emitter, LogicalPosition, LogicalSize, Manager, Monitor, WebviewUrl, WebviewWindowBuilder};

/// HUD(ゲーム中に画面へ常駐する操作パネル群)のウィンドウラベル。
/// 3枚に分かれているのは、Unityの3Dビューの上に重ねる透明ウィンドウでは
/// 「見た目が透明な領域でもクリックがWebViewに吸われる」ため、
/// パネルの実面積だけを覆うウィンドウに分割する必要があるから。
pub const LABEL_ICON_PANEL: &str = "icon-panel";
pub const LABEL_COCKPIT: &str = "cockpit";
pub const LABEL_SYSTEM_TRAY: &str = "system-tray";
pub const HUD_WINDOW_LABELS: [&str; 3] = [LABEL_ICON_PANEL, LABEL_COCKPIT, LABEL_SYSTEM_TRAY];

/// HUD各パネルと画面端・他ウィンドウとの間隔
pub const HUD_MARGIN: f64 = 8.0;

/// アイコンパネル(画面左端・縦並びのメニューアイコン)の幅。
/// `IconPanel.css` のボタン寸法(50px)+ パネルの左右パディング(4px×2)と一致させること。
/// 足りないとボタンが切れ、余ると透明領域がクリックを吸ってUnityを操作できなくなる。
pub const ICON_PANEL_WIDTH: f64 = 58.0;
/// アイコンパネルの初期高さ。実際の高さはアイコン数で変わるため、
/// React側が `resize_icon_panel` コマンドで実測値を通知して上書きする。
pub const ICON_PANEL_DEFAULT_HEIGHT: f64 = 660.0;

/// コックピット(画面下部中央・常用ツール/公共サービス充足度/都市名日付/予算メータ/アラート/エポック実行)のサイズ。
/// ウィンドウはパネルの実寸ぴったりで作るため、中身を増やしたらこの幅も広げる必要がある。
pub const COCKPIT_WIDTH: f64 = 800.0;
pub const COCKPIT_HEIGHT: f64 = 76.0;

/// システムトレイ(画面右下・設定/セーブ/リロード)のサイズ。
/// 幅はテンプレートモードのボタン(一括完成・路地作成)の有無で変わるため既定値であり、
/// React側が `resize_system_tray` コマンドで実測値を通知して上書きする。
pub const SYSTEM_TRAY_WIDTH: f64 = 176.0;
pub const SYSTEM_TRAY_HEIGHT: f64 = 40.0;

/// 画面下部中央でコックピットが占有する高さ(画面下端からコックピット上端まで)。
/// ポップアップの中央寄せや下部中央パネルの配置はこの分だけ上に詰める。
pub const HUD_BOTTOM_RESERVED: f64 = COCKPIT_HEIGHT + HUD_MARGIN;

/// 画面右下でシステムトレイが占有する高さ。右サイドパネルの丈はこれだけ避ける
/// (コックピットは中央にあり右サイドパネルとは重ならないため、こちらの薄い方を使う)。
pub const HUD_RIGHT_BOTTOM_RESERVED: f64 = SYSTEM_TRAY_HEIGHT + HUD_MARGIN;

/// 画面左端でアイコンパネルが占有する幅(画面左端からパネル右端まで)。
/// 左ドックのウィンドウはこの分だけ右へずらす。
pub const HUD_LEFT_RESERVED: f64 = ICON_PANEL_WIDTH + HUD_MARGIN;

static NEXT_WINDOW_ID: AtomicU64 = AtomicU64::new(1);

/// HUDウィンドウのタイトルと初期サイズ。
/// アイコンパネルの高さだけは中身のアイコン数で変わるため、ここでは既定値を返し、
/// 実際の高さは React 側の実測値(`resize_icon_panel`)で上書きされる。
fn hud_window_spec(label: &str) -> (&'static str, f64, f64) {
    match label {
        LABEL_ICON_PANEL => ("Genesis Icon Panel", ICON_PANEL_WIDTH, ICON_PANEL_DEFAULT_HEIGHT),
        LABEL_COCKPIT => ("Genesis Cockpit", COCKPIT_WIDTH, COCKPIT_HEIGHT),
        _ => ("Genesis System Tray", SYSTEM_TRAY_WIDTH, SYSTEM_TRAY_HEIGHT),
    }
}

/// HUDウィンドウ3枚を生成する。初期状態は非表示で、
/// セッション開始時に `set_hud_visible` で表示される。
pub fn create_hud_windows(
    app: &AppHandle,
    monitors: &[Monitor],
    monitor_index: usize,
) -> Result<(), String> {
    for label in HUD_WINDOW_LABELS {
        let (title, width, height) = hud_window_spec(label);
        WebviewWindowBuilder::new(app, label, WebviewUrl::App("index.html".into()))
            .title(title)
            .decorations(false)
            .transparent(true)
            .shadow(false)
            .always_on_top(true)
            .skip_taskbar(true)
            .resizable(false)
            .visible(false)
            .inner_size(width, height)
            .build()
            .map_err(|e| format!("failed to create {label} window: {e}"))?;
    }
    layout_hud_windows(app, monitors, monitor_index, None, None);
    Ok(())
}

/// HUD各ウィンドウを対象モニターの定位置(左端中央・下部中央・右下)へ配置する。
///
/// `icon_panel_height` はアイコンパネルの高さ、`system_tray_width` はシステムトレイの幅の
/// 明示指定。None のときは現在のウィンドウ寸法(=React側が実測して通知済みの値)を
/// 維持したまま位置だけ取り直す。
pub fn layout_hud_windows(
    app: &AppHandle,
    monitors: &[Monitor],
    monitor_index: usize,
    icon_panel_height: Option<f64>,
    system_tray_width: Option<f64>,
) {
    let Some(monitor) = monitors.get(monitor_index) else {
        return;
    };
    let scale = monitor.scale_factor();
    let m_size = monitor.size().to_logical::<f64>(scale);
    let m_pos = monitor.position().to_logical::<f64>(scale);

    // アイコンパネル: 画面左端に縦置きし、上下方向は中央寄せにする
    if let Some(win) = app.get_webview_window(LABEL_ICON_PANEL) {
        let current = win
            .inner_size()
            .ok()
            .map(|s| s.to_logical::<f64>(scale).height)
            .filter(|h| *h > 1.0)
            .unwrap_or(ICON_PANEL_DEFAULT_HEIGHT);
        let height = icon_panel_height
            .unwrap_or(current)
            .clamp(1.0, m_size.height - HUD_MARGIN * 2.0);
        let _ = win.set_size(LogicalSize::new(ICON_PANEL_WIDTH, height));
        let _ = win.set_position(LogicalPosition::new(
            m_pos.x + HUD_MARGIN,
            m_pos.y + (m_size.height - height) / 2.0,
        ));
    }

    // コックピット: 画面下部中央
    if let Some(win) = app.get_webview_window(LABEL_COCKPIT) {
        let _ = win.set_size(LogicalSize::new(COCKPIT_WIDTH, COCKPIT_HEIGHT));
        let _ = win.set_position(LogicalPosition::new(
            m_pos.x + (m_size.width - COCKPIT_WIDTH) / 2.0,
            m_pos.y + m_size.height - COCKPIT_HEIGHT - HUD_MARGIN,
        ));
    }

    // システムトレイ: 画面右下。幅は中身のボタン数に合わせて右端基準で伸縮する
    if let Some(win) = app.get_webview_window(LABEL_SYSTEM_TRAY) {
        let current = win
            .inner_size()
            .ok()
            .map(|s| s.to_logical::<f64>(scale).width)
            .filter(|w| *w > 1.0)
            .unwrap_or(SYSTEM_TRAY_WIDTH);
        let width = system_tray_width
            .unwrap_or(current)
            .clamp(1.0, m_size.width - HUD_MARGIN * 2.0);
        let _ = win.set_size(LogicalSize::new(width, SYSTEM_TRAY_HEIGHT));
        let _ = win.set_position(LogicalPosition::new(
            m_pos.x + m_size.width - width - HUD_MARGIN,
            m_pos.y + m_size.height - SYSTEM_TRAY_HEIGHT - HUD_MARGIN,
        ));
    }
}

/// HUDウィンドウ3枚をまとめて表示/非表示する。
/// セッション(=プレイ中の都市)の有無に連動させる。
pub fn set_hud_visible(app: &AppHandle, visible: bool) {
    for label in HUD_WINDOW_LABELS {
        if let Some(win) = app.get_webview_window(label) {
            let _ = if visible { win.show() } else { win.hide() };
        }
    }
    // WebView2では hide()→show() をまたぐと生成時の always_on_top 指定が
    // 効かなくなることがあるため、表示のたびに再適用する。
    if visible {
        reassert_topmost(app);
    }
}

/// 表示中の全Tauriウィンドウ(HUD3枚・ヒントバー・ポップアップ等)のtopmost(最前面)を打ち直す。
///
/// topmostは生成時に一度指定しただけでは維持されない:
/// - Unityがフルスクリーン扱い(モニタ全面を覆うボーダーレス)でフォアグラウンドになると、
///   シェルがゲームをtopmostより手前へ持ち上げ、UIが裏に回る
/// - WebView2では hide()→show() をまたぐとtopmostが外れることがある
///
/// `set_always_on_top(true)` は使わない。tao は新旧フラグの差分が無いと `SetWindowPos` を
/// 発行しないため、既にtopmost指定済みのウィンドウには何も起きず、裏に回った状態から
/// 復帰できない。ここでは HWND に対して直接 `SetWindowPos(HWND_TOPMOST)` を発行する。
///
/// ウィンドウ同士の重なり順は変えない。`HWND_TOPMOST` はtopmost帯の先頭へ挿入するため、
/// 現在のZオーダーで下にあるものから順に発行して相対順を保つ(ポップアップ同士の
/// 前後関係や、ポップアップがHUDより手前にある状態を崩さないため)。
/// 冪等かつ安価なので、フォーカス移動や Unity からの復帰通知のたびに呼び直してよい。
pub fn reassert_topmost(app: &AppHandle) {
    #[cfg(target_os = "windows")]
    {
        use std::collections::HashSet;
        use windows_sys::Win32::UI::WindowsAndMessaging::{
            GetTopWindow, GetWindow, IsWindowVisible, SetWindowPos, GW_HWNDNEXT, HWND_TOPMOST,
            SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE,
        };

        // 表示中の自ウィンドウの HWND 集合(非表示のものは対象外)。
        let mut targets: HashSet<isize> = HashSet::new();
        for win in app.webview_windows().values() {
            if let Ok(hwnd) = win.hwnd() {
                let hwnd = hwnd.0 as *mut core::ffi::c_void;
                if unsafe { IsWindowVisible(hwnd) } != 0 {
                    targets.insert(hwnd as isize);
                }
            }
        }
        if targets.is_empty() {
            return;
        }

        // デスクトップ直下のトップレベルウィンドウを手前から順に歩き、自ウィンドウだけ拾う。
        let mut ordered: Vec<isize> = Vec::with_capacity(targets.len());
        let mut cursor = unsafe { GetTopWindow(std::ptr::null_mut()) };
        while !cursor.is_null() {
            if targets.contains(&(cursor as isize)) {
                ordered.push(cursor as isize);
            }
            cursor = unsafe { GetWindow(cursor, GW_HWNDNEXT) };
        }

        // 手前から順に集めたので、逆順(最背面から)に打つと相対順が保たれる。
        for hwnd in ordered.into_iter().rev() {
            unsafe {
                SetWindowPos(
                    hwnd as *mut core::ffi::c_void,
                    HWND_TOPMOST,
                    0,
                    0,
                    0,
                    0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
                );
            }
        }
    }
    #[cfg(not(target_os = "windows"))]
    {
        for win in app.webview_windows().values() {
            let _ = win.set_always_on_top(true);
        }
    }
}

/// アイコンパネルの高さをReact側の実測値に合わせる。
/// アイコン数はテンプレートモードの有無で変わり、Rust側からは分からないため、
/// 中身の高さが変わるたびにReactから通知させて透明な余白が出ないようにする。
pub fn resize_icon_panel(app: &AppHandle, state: &SharedState, height: f64) {
    let monitor_index = {
        let guard = state.lock();
        guard.current_monitor_index
    };
    let monitors: Vec<_> = app
        .available_monitors()
        .unwrap_or_default()
        .into_iter()
        .collect();
    layout_hud_windows(app, &monitors, monitor_index, Some(height), None);
}

/// システムトレイの幅をReact側の実測値に合わせて更新し、右下へ寄せ直す。
pub fn resize_system_tray(app: &AppHandle, state: &SharedState, width: f64) {
    let monitor_index = {
        let guard = state.lock();
        guard.current_monitor_index
    };
    let monitors: Vec<_> = app
        .available_monitors()
        .unwrap_or_default()
        .into_iter()
        .collect();
    layout_hud_windows(app, &monitors, monitor_index, None, Some(width));
}

/// 指定レイアウトが画面端に固定されるドック配置かどうか。
/// ドック配置ウィンドウは位置を記憶せず、レイアウト計算で再配置される。
fn is_docked_layout(layout: &WindowLayout) -> bool {
    !matches!(layout, WindowLayout::Default)
}

/// ウィンドウの現在のサイズ（および必要なら位置）を state に記憶する。
///
/// プロセス起動中のみ保持され、永続化されない。
/// - `record_position == true` かつ非ドック配置の場合のみ位置も更新する。
/// - サイズは常に更新する。
/// - 最大化中のウィンドウは記憶対象外（最大化状態の値は無意味なため）。
pub fn record_window_geometry(app: &AppHandle, state: &SharedState, label: &str, record_position: bool) {
    let (window_type, is_docked) = {
        let guard = state.lock();
        let Some(window) = guard.open_windows.get(label) else {
            return;
        };
        let wt = window.window_type.clone();
        let docked = guard
            .registry
            .get(&wt)
            .map(|d| is_docked_layout(&d.layout))
            .unwrap_or(false);
        (wt, docked)
    };

    let Some(win) = app.get_webview_window(label) else {
        return;
    };

    // 最大化中は記憶しない
    if win.is_maximized().unwrap_or(false) {
        return;
    }

    let scale = win.scale_factor().unwrap_or(1.0);
    // サイズは内側(クライアント領域)基準で記憶する。復元側は inner_size / set_size という
    // 内側基準のAPIを使うため、外枠基準で記憶すると開き直すたびに枠の厚み分だけ膨張する
    // (装飾なしでもリサイズ可能ウィンドウは不可視のリサイズ枠を持つ)。
    // 位置は outer_position / set_position のどちらも外枠基準なのでそのままでよい。
    let Some(size) = win.inner_size().map(|s| s.to_logical::<f64>(scale)).ok() else {
        return;
    };

    // ドック配置では位置を記憶せず、サイズのみ更新する
    let pos = if record_position && !is_docked {
        win.outer_position()
            .map(|p| p.to_logical::<f64>(scale))
            .ok()
    } else {
        None
    };

    let mut guard = state.lock();
    let entry = guard
        .saved_geometries
        .entry(window_type)
        .or_default();
    entry.width = size.width;
    entry.height = size.height;
    if let Some(p) = pos {
        entry.x = p.x;
        entry.y = p.y;
    }
}

/// ウィンドウの配置(サイズと位置)をレイアウト種別に応じて計算する。
///
/// 新規ウィンドウの builder 構築と、非表示ウィンドウの再利用時の set_size/set_position
/// の両方から呼ばれる共通ロジック。レイアウト計算を一元化することで、新規・再利用の
/// どちらでも同じ位置・サイズが適用される。
///
/// ドック配置は常駐HUD(左のアイコンパネル・下部中央のコックピット・右下のシステムトレイ)を
/// 避けた領域に収める。左ドックは `HUD_LEFT_RESERVED` だけ右へ寄せ、丈は下部の占有分を引く。
///
/// - ドック配置(LeftPanel/RightPanel/BottomCenter/LeftTop): モニター情報を基準に位置を決定する。
///   saved_geometry があればサイズを上書きする(位置はレイアウトに従う)。
/// - 自由配置(Default): saved_geometry があれば位置とサイズをそのまま復元する。
///   なければ width_ratio/height_ratio または definition.width/height でサイズを決め、
///   payload.params.preferred_position またはモニター中央に配置する。
///
/// モニターが取得できない場合は definition のサイズと位置 (0,0) にフォールバックする。
fn compute_window_geometry(
    definition: &WindowDefinition,
    payload: &OpenWindowPayload,
    saved_geometry: Option<WindowGeometry>,
    monitors: &[Monitor],
    monitor_index: usize,
) -> (LogicalSize<f64>, LogicalPosition<f64>) {
    let monitor = monitors.get(monitor_index);

    if definition.layout == WindowLayout::LeftPanel
        || definition.layout == WindowLayout::LeftPanelNarrow
    {
        // 左サイドパネル: アイコンパネルの右隣に上下いっぱいに配置する
        if let Some(monitor) = monitor {
            let scale = monitor.scale_factor();
            let m_size = monitor.size().to_logical::<f64>(scale);
            let m_pos = monitor.position().to_logical::<f64>(scale);

            let margin = HUD_MARGIN;
            let width_ratio = if definition.layout == WindowLayout::LeftPanelNarrow {
                0.2
            } else {
                0.4
            };
            let computed_width = (m_size.width * width_ratio).round();
            let computed_height = m_size.height - HUD_BOTTOM_RESERVED - margin * 2.0;

            // 記憶済みサイズがあればそれを優先する（位置はレイアウトに従う）
            let panel_width = saved_geometry.map(|g| g.width).unwrap_or(computed_width);
            let panel_height = saved_geometry.map(|g| g.height).unwrap_or(computed_height);

            return (
                LogicalSize::new(panel_width, panel_height),
                LogicalPosition::new(m_pos.x + HUD_LEFT_RESERVED + margin, m_pos.y + margin),
            );
        }
    } else if definition.layout == WindowLayout::RightPanel {
        // 右サイドパネル: モニターの右端に上下いっぱいに配置する
        if let Some(monitor) = monitor {
            let scale = monitor.scale_factor();
            let m_size = monitor.size().to_logical::<f64>(scale);
            let m_pos = monitor.position().to_logical::<f64>(scale);

            let margin = HUD_MARGIN;
            let computed_width = (m_size.width * 0.20).round();
            let computed_height = m_size.height - HUD_RIGHT_BOTTOM_RESERVED - margin * 2.0;

            // 記憶済みサイズがあればそれを優先する。右端配置なので位置は幅に依存する。
            let panel_width = saved_geometry.map(|g| g.width).unwrap_or(computed_width);
            let panel_height = saved_geometry.map(|g| g.height).unwrap_or(computed_height);

            return (
                LogicalSize::new(panel_width, panel_height),
                LogicalPosition::new(
                    m_pos.x + m_size.width - panel_width - margin,
                    m_pos.y + margin,
                ),
            );
        }
    } else if definition.layout == WindowLayout::BottomCenter {
        // 画面下部中央パネル: モニター幅の指定比率(未指定時55%)、高さはレジストリ値を使用
        // コックピットの直上に配置する
        if let Some(monitor) = monitor {
            let scale = monitor.scale_factor();
            let m_size = monitor.size().to_logical::<f64>(scale);
            let m_pos = monitor.position().to_logical::<f64>(scale);

            let margin = HUD_MARGIN;
            let width_ratio = definition.width_ratio.unwrap_or(0.55);
            let computed_width = (m_size.width * width_ratio).round();
            let computed_height = definition.height as f64;

            // 記憶済みサイズがあればそれを優先する（位置は下部中央レイアウトに従う）
            let panel_width = saved_geometry.map(|g| g.width).unwrap_or(computed_width);
            let panel_height = saved_geometry.map(|g| g.height).unwrap_or(computed_height);

            let x = m_pos.x + (m_size.width - panel_width) / 2.0;
            let y = m_pos.y + m_size.height - HUD_BOTTOM_RESERVED - panel_height - margin;

            return (
                LogicalSize::new(panel_width, panel_height),
                LogicalPosition::new(x, y),
            );
        }
    } else if definition.layout == WindowLayout::LeftTop {
        // 左上配置: アイコンパネルの右隣・画面上端に配置し、サイズはレジストリ値を使用
        if let Some(monitor) = monitor {
            let scale = monitor.scale_factor();
            let m_pos = monitor.position().to_logical::<f64>(scale);

            let margin = HUD_MARGIN;
            // 記憶済みサイズがあればそれを優先する（位置は左上レイアウトに従う）
            let w = saved_geometry.map(|g| g.width).unwrap_or(definition.width as f64);
            let h = saved_geometry.map(|g| g.height).unwrap_or(definition.height as f64);

            return (
                LogicalSize::new(w, h),
                LogicalPosition::new(m_pos.x + HUD_LEFT_RESERVED + margin, m_pos.y + margin),
            );
        }
    } else if definition.layout == WindowLayout::RightTop {
        // 右上配置: 画面右端・上端にマージンを空けて配置し、サイズはレジストリ値を使用する。
        // 敷設パネル(左端)と同時に使う2Dパス形状編集パネルのように、3Dビューの中央を
        // 塞がずに左のパネルと共存させたいウィンドウ向け。
        if let Some(monitor) = monitor {
            let scale = monitor.scale_factor();
            let m_pos = monitor.position().to_logical::<f64>(scale);
            let m_size = monitor.size().to_logical::<f64>(scale);

            let margin = HUD_MARGIN;
            // 既定サイズ: width_ratio があればモニター幅に対する比率で幅を決め、height_ratio が
            // 無ければ正方形にする(コックピットを避けた作業領域の高さでクランプ)。比率が無ければレジストリの実寸。
            let work_h = (m_size.height - HUD_BOTTOM_RESERVED - margin * 2.0).max(200.0);
            let (default_w, default_h) = match definition.width_ratio {
                Some(rw) if rw > 0.0 => {
                    let w = (m_size.width * rw).round();
                    let h = match definition.height_ratio {
                        Some(rh) if rh > 0.0 => (work_h * rh).round(),
                        _ => w.min(work_h),
                    };
                    (w, h)
                }
                _ => (definition.width as f64, definition.height as f64),
            };
            let w = saved_geometry.map(|g| g.width).unwrap_or(default_w);
            let h = saved_geometry.map(|g| g.height).unwrap_or(default_h);

            return (
                LogicalSize::new(w, h),
                LogicalPosition::new(m_pos.x + m_size.width - w - margin, m_pos.y + margin),
            );
        }
    } else {
        // 自由配置: 記憶済みの位置・サイズがあればそれをそのまま復元する。
        if let Some(geo) = saved_geometry {
            return (
                LogicalSize::new(geo.width, geo.height),
                LogicalPosition::new(geo.x, geo.y),
            );
        }

        let (win_w, win_h) = match (
            definition.width_ratio,
            definition.height_ratio,
            monitor,
        ) {
            (Some(rw), Some(rh), Some(monitor)) if rw > 0.0 && rh > 0.0 => {
                let scale = monitor.scale_factor();
                let m_size = monitor.size().to_logical::<f64>(scale);
                let work_h = (m_size.height - HUD_BOTTOM_RESERVED).max(100.0);
                ((m_size.width * rw).round(), (work_h * rh).round())
            }
            _ => (definition.width as f64, definition.height as f64),
        };

        // 親から明示的な位置指定があればそれを使い、なければHUDと同じモニターの中央に配置する
        let pos = if let Some(position) = payload.params.as_ref().and_then(|p| p.preferred_position.as_ref()) {
            if let (Some(x), Some(y)) = (position.x, position.y) {
                LogicalPosition::new(x, y)
            } else if let Some(monitor) = monitor {
                let scale = monitor.scale_factor();
                let m_size = monitor.size().to_logical::<f64>(scale);
                let m_pos = monitor.position().to_logical::<f64>(scale);
                let x = m_pos.x + (m_size.width - win_w) / 2.0;
                let y = m_pos.y + (m_size.height - HUD_BOTTOM_RESERVED - win_h) / 2.0;
                LogicalPosition::new(x, y)
            } else {
                LogicalPosition::new(0.0, 0.0)
            }
        } else if let Some(monitor) = monitor {
            let scale = monitor.scale_factor();
            let m_size = monitor.size().to_logical::<f64>(scale);
            let m_pos = monitor.position().to_logical::<f64>(scale);
            let x = m_pos.x + (m_size.width - win_w) / 2.0;
            let y = m_pos.y + (m_size.height - HUD_BOTTOM_RESERVED - win_h) / 2.0;
            LogicalPosition::new(x, y)
        } else {
            LogicalPosition::new(0.0, 0.0)
        };

        return (LogicalSize::new(win_w, win_h), pos);
    }

    // モニターが取得できない場合のフォールバック
    (
        LogicalSize::new(definition.width as f64, definition.height as f64),
        LogicalPosition::new(0.0, 0.0),
    )
}

pub async fn open_window(app: AppHandle, state: SharedState, payload: OpenWindowPayload) -> Result<(), String> {
    let definition = {
        let guard = state.lock();
        guard
            .registry
            .get(&payload.window_type)
            .cloned()
            .ok_or_else(|| format!("unknown window_type: {}", payload.window_type))?
    };

    let instance_key = make_instance_key(&payload.window_type, payload.entity.as_ref());

    // 優先度1: 同一 instance_key のウィンドウ(表示中または非表示)があれば再利用する。
    // 非表示のものは再表示し、LRUから外す。コンテキスト(entity/params)を最新化してフォーカスする。
    let existing_label = {
        let guard = state.lock();
        guard
            .open_windows
            .values()
            .find(|window| window.instance_key == instance_key)
            .map(|window| window.label.clone())
    };

    if let Some(label) = existing_label {
        {
            let mut guard = state.lock();
            let was_hidden = guard
                .open_windows
                .get(&label)
                .map(|w| w.hidden)
                .unwrap_or(false);
            // 非表示(再利用待ち)状態から復帰させる。表示中の場合は hidden フラグは既に false。
            // 再表示は「閉じたウィンドウを開き直した」操作なので open_seq を進め、
            // React 側にパネルの再マウント(=状態リセット)を要求する。
            // 表示中ウィンドウへの再オープン要求(フォーカス移動)ではインクリメントしない。
            if was_hidden {
                if let Some(w) = guard.open_windows.get_mut(&label) {
                    w.hidden = false;
                    w.open_seq += 1;
                }
                guard.hidden_lru.retain(|l| l != &label);
            }
            // entity/params を最新要求で上書きする(同一 instance_key だが参照ポイントが変わる可能性がある)。
            if let Some(window) = guard.open_windows.get_mut(&label) {
                window.entity = payload.entity.clone();
                window.params =
                    serde_json::to_value(payload.params.clone()).unwrap_or(Value::Null);
            }
        }

        if let Some(window) = app.get_webview_window(&label) {
            window.show().map_err(|err| err.to_string())?;
            let _ = window.set_focus();
            if let Some(context) = state.lock().to_context(&label) {
                window
                    .emit_to(label.as_str(), "window-context-updated", context)
                    .map_err(|err| err.to_string())?;
            }
        }
        return Ok(());
    }

    apply_mutex_rules(&app, &state, &payload.window_type)?;

    // レイアウト計算に必要な情報を先に取得しておく(新規・再利用で共有)。
    let saved_geometry = {
        let guard = state.lock();
        guard.saved_geometries.get(&payload.window_type).copied()
    };
    let monitor_index = state.lock().current_monitor_index;
    let monitors: Vec<_> = app
        .available_monitors()
        .unwrap_or_default()
        .into_iter()
        .collect();

    // 優先度2: 非表示(再利用待ち)のポップアップウィンドウがあれば、それを別用途に転用する。
    // Tauri はウィンドウラベルを変更できないため、ラベルはそのままに window_type/entity/
    // params/instance_key/title を上書きし、サイズ・位置も新しい定義に合わせて再設定する。
    // React 側は window-context-updated イベントで新しい context を受け取り再レンダリングする。
    let reuse_label = {
        let guard = state.lock();
        guard.hidden_lru.back().cloned()
    };

    if let Some(label) = reuse_label {
        let title = definition.title.clone();
        let params_value = serde_json::to_value(payload.params.clone()).unwrap_or(Value::Null);

        let (size, pos) = compute_window_geometry(
            &definition,
            &payload,
            saved_geometry,
            &monitors,
            monitor_index,
        );

        {
            let mut guard = state.lock();
            guard.hidden_lru.retain(|l| l != &label);
            if let Some(w) = guard.open_windows.get_mut(&label) {
                w.window_type = payload.window_type.clone();
                w.instance_key = instance_key.clone();
                w.title = title.clone();
                w.entity = payload.entity.clone();
                w.params = params_value;
                w.hidden = false;
                // 別用途への転用も「開き直し」なので世代を進める。
                // (window_type が変わるため React 側のキーは変わるが、同種転用の可能性も
                // 排除できないため一貫してインクリメントする)
                w.open_seq += 1;
            }
        }

        if let Some(window) = app.get_webview_window(&label) {
            let _ = window.set_title(&title);
            let _ = window.set_resizable(definition.resizable);
            if definition.maximized {
                let _ = window.maximize();
            } else {
                let _ = window.unmaximize();
                let _ = window.set_size(size);
                let _ = window.set_position(pos);
            }
            window.show().map_err(|err| err.to_string())?;
            let _ = window.set_focus();
            if let Some(context) = state.lock().to_context(&label) {
                window
                    .emit_to(label.as_str(), "window-context-updated", context)
                    .map_err(|err| err.to_string())?;
            }
        }
        return Ok(());
    }

    // 優先度3: 再利用可能なウィンドウがないため新規に生成する(従来の挙動)。
    let label = format!("popup-{}", NEXT_WINDOW_ID.fetch_add(1, Ordering::Relaxed));
    let title = definition.title.clone();
    let params_value = serde_json::to_value(payload.params.clone()).unwrap_or(Value::Null);

    {
        let mut guard = state.lock();
        guard.open_windows.insert(
            label.clone(),
            OpenWindowState {
                label: label.clone(),
                instance_key: instance_key.clone(),
                window_type: payload.window_type.clone(),
                title: title.clone(),
                entity: payload.entity.clone(),
                params: params_value,
                hidden: false,
                open_seq: 0,
            },
        );
    }

    let (size, pos) = compute_window_geometry(
        &definition,
        &payload,
        saved_geometry,
        &monitors,
        monitor_index,
    );

    let builder = WebviewWindowBuilder::new(&app, label.clone(), WebviewUrl::App("index.html".into()))
        .title(&title)
        .decorations(false)
        .visible(true)
        .resizable(definition.resizable)
        .maximized(definition.maximized)
        .inner_size(size.width, size.height)
        .position(pos.x, pos.y)
        .always_on_top(true);

    let window = builder.build().map_err(|err| err.to_string())?;
    let _ = window.set_focus();

    Ok(())
}

/// 編集モードを持つウィンドウタイプ。
/// これらのウィンドウが閉じられたとき、グラフィックプログラムに STATE_NORMAL を送信して
/// 編集モードから抜ける必要がある。
///
/// zone_detail は表示専用パネルでステートを変更しないため含めない。
/// 地区編集(STATE_EDIT_ZONE)はウィンドウを開かないタスクバーのトグルモードのため、ここには含めない
/// (トグルOFFで Taskbar が直接 STATE_NORMAL を送る。ブルドーザ=STATE_REMOVE_OBJECTS と同じ扱い)。
const EDIT_MODE_WINDOW_TYPES: &[&str] = &[
    "parcel_creation",
    "path_build",
    "infra_build",
    "track_build",
    "building_construction",
    "terrain_unlock",
];

pub fn close_window_by_label(app: &AppHandle, state: &SharedState, label: &str) -> Result<(), String> {
    // ウィンドウタイプを先に取得しておく(非表示マーク後も参照できるが、取得時の意図を明確にするため)。
    let window_type = {
        let guard = state.lock();
        guard.open_windows.get(label).map(|w| w.window_type.clone())
    };

    // 冪等性のため、エントリが存在しない場合または既に非表示(再利用待ち)の場合は何もしない。
    // 二回目以降の呼び出しはここで早期リターンする。
    let should_close = {
        let guard = state.lock();
        guard
            .open_windows
            .get(label)
            .map(|w| !w.hidden)
            .unwrap_or(false)
    };
    if !should_close {
        return Ok(());
    }

    // 編集モードのウィンドウが閉じられたら STATE_NORMAL に戻す。
    // ただし、他に編集モードのウィンドウがまだ表示中の場合は戻さない。
    // 例えば道路敷設中に誤って地区パネルを開いて閉じた場合、
    // 道路敷設を継続できるよう、STATE_NORMAL への強制遷移を防ぐ。
    // 建設確定後に計画表示に切り替える処理は、UI 側の各パネルで確定ボタン押下時に個別に行う。
    // ここでは一律で view:change_view を送らない。
    if let Some(ref wt) = window_type {
        if EDIT_MODE_WINDOW_TYPES.contains(&wt.as_str()) {
            let has_other_edit_window = {
                let guard = state.lock();
                guard
                    .open_windows
                    .values()
                    .filter(|w| !w.hidden && w.label != label)
                    .any(|w| EDIT_MODE_WINDOW_TYPES.contains(&w.window_type.as_str()))
            };
            if !has_other_edit_window {
                let _ = ipc::send_ui_action(
                    state,
                    wt.clone(),
                    None,
                    "world:change_state".to_string(),
                    json!({ "state": "STATE_NORMAL" }),
                );
            }
            if wt == "path_build" || wt == "infra_build" {
                // 道路建設・インフラ建設パネル(同じ PathConstructionPanel)が閉じられた場合、
                // 描画されているグリッドを確実に非表示にする。
                // ウィンドウは非表示(再利用待ち)になるだけでReact側のアンマウント処理による
                // クリーンアップ送信が走らないため、Rustのウィンドウクローズ処理側で明示的にオフを送る。
                let _ = ipc::send_ui_action(
                    state,
                    wt.clone(),
                    None,
                    "view:set_grid".to_string(),
                    json!({ "mode": "off" }),
                );
                // 敷設由来の干渉ハイライト(source="path_build")も同じ理由でここで消す。
                let _ = ipc::send_ui_action(
                    state,
                    wt.clone(),
                    None,
                    "world:clear_highlights".to_string(),
                    json!({ "source": "path_build" }),
                );
            }
            if wt == "path_build" || wt == "infra_build" || wt == "track_build" {
                // 敷設セッションの持ち主である敷設パネルが閉じられたら、付随する2Dパス形状編集
                // パネルも閉じる。逆(2Dパネルを閉じる)は敷設セッションを終わらせない。
                close_windows_by_type(app, state, "path_shape_editor");
            }
            if wt == "building_construction" {
                // 建物建設パネルが閉じられたとき、直接配置の干渉ハイライト
                // (source="building_construction")を消す。ウィンドウは非表示(再利用待ち)に
                // なるだけでReact側のアンマウント処理によるクリーンアップ送信が走らないため、
                // クローズ処理側で明示的に送る(path_build と同じ理由)。
                let _ = ipc::send_ui_action(
                    state,
                    wt.clone(),
                    None,
                    "world:clear_highlights".to_string(),
                    json!({ "source": "building_construction" }),
                );
            }
        } else if wt == "agent_detail" {
            // 経済主体詳細パネルが閉じられたとき、接続端子のハイライトを消去し、
            // コネクタ編集モード中だった場合に通常モードへ戻す。
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "edit_connector:hide".to_string(),
                json!({}),
            );
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "world:change_state".to_string(),
                json!({ "state": "STATE_NORMAL" }),
            );
        } else if wt == "zone_merge" {
            // 地区統合の確定ウィンドウが(確定も取消もされずに)閉じられたとき、
            // Unity 側の確定待ちを取消として解除する。ウィンドウは非表示(再利用待ち)に
            // なるだけでReact側のアンマウント処理が走らないため、パネル任せにすると
            // 統合待ちのまま取り残される。
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "edit_zone:finish_merge".to_string(),
                json!({ "merged": 0 }),
            );
        } else if wt == "parcel_detail" {
            // 土地区画詳細パネルが閉じられたとき、接道表示をクリアする。
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "parcel:hide_front_path".to_string(),
                json!({}),
            );
        } else if wt == "path_detail" {
            // パス詳細パネルが閉じられたとき、着工前パスの干渉ハイライトをクリアする。
            // ウィンドウは非表示(再利用待ち)になるだけでReact側のアンマウント処理による
            // クリーンアップ送信が走らないため、クローズ処理側で明示的に送る。
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "world:clear_highlights".to_string(),
                json!({ "source": "path_detail" }),
            );
            // パスの結合/分割の対象選択モード中だった場合に打ち切って通常モードへ戻す。
            // 同じ理由(アンマウント処理が走らない)で、パネル任せにすると選択モードのまま
            // 3Dビューの入力が固まる。Unity側は選択モード中でなければ何もしない。
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "edit_path:cancel_topology_edit".to_string(),
                json!({}),
            );
        }
    }

    // ウィンドウを非表示にし、再利用待ち状態にする。
    // ユーザが閉じた直後に同じウィンドウタイプを開き直すケースは多いため、WebView2 の起動
    // コスト(約0.4秒)を回避するためにウィンドウを破棄せず保持する。
    // hidden_lru が MAX_HIDDEN_WINDOWS を超える場合は最古のものから本当に破棄する。
    let labels_to_destroy: Vec<String> = {
        let mut guard = state.lock();
        let mut to_destroy = Vec::new();
        // 上限を超える間、最古のもの(LRU先頭)を破棄対象に回す
        while guard.hidden_lru.len() >= MAX_HIDDEN_WINDOWS {
            if let Some(old) = guard.hidden_lru.pop_front() {
                to_destroy.push(old);
            }
        }
        guard.hidden_lru.push_back(label.to_string());
        if let Some(w) = guard.open_windows.get_mut(label) {
            w.hidden = true;
        }
        to_destroy
    };

    // LRU上限により破棄対象になったウィンドウを本当に閉じる。
    // これらの副作用(STATE_NORMAL送信など)は、それらが最初に非表示にされた時点で
    // 既に実行済みなので、ここでは単に destroy する。
    for old_label in labels_to_destroy {
        if let Some(old_window) = app.get_webview_window(&old_label) {
            let _ = old_window.close();
        }
        let mut guard = state.lock();
        guard.open_windows.remove(&old_label);
    }

    // 現在のウィンドウを非表示にする(破棄はしない)。
    if let Some(window) = app.get_webview_window(label) {
        let _ = window.hide();
    }

    Ok(())
}

/// 指定ウィンドウタイプの表示中ウィンドウをすべて閉じる(非表示・再利用待ちにする)。
/// 開いていなければ何もしない。他ウィンドウの操作(確定ボタン等)から付随ウィンドウを閉じる用途。
pub fn close_windows_by_type(app: &AppHandle, state: &SharedState, window_type: &str) {
    let labels: Vec<String> = {
        let guard = state.lock();
        guard
            .open_windows
            .values()
            .filter(|w| !w.hidden && w.window_type == window_type)
            .map(|w| w.label.clone())
            .collect()
    };
    for label in labels {
        let _ = close_window_by_label(app, state, &label);
    }
}

fn apply_mutex_rules(app: &AppHandle, state: &SharedState, window_type: &str) -> Result<(), String> {
    let blocked_types = {
        let guard = state.lock();
        guard.mutex_groups.get(window_type).cloned().unwrap_or_default()
    };

    if blocked_types.is_empty() {
        return Ok(());
    }

    let labels_to_close: Vec<String> = {
        let guard = state.lock();
        guard
            .open_windows
            .values()
            .filter(|window| blocked_types.iter().any(|blocked| blocked == &window.window_type))
            .map(|window| window.label.clone())
            .collect()
    };

    for label in labels_to_close {
        close_window_by_label(app, state, &label)?;
    }

    Ok(())
}

fn make_instance_key(window_type: &str, entity: Option<&EntityRef>) -> String {
    if let Some(entity) = entity {
        if let Some(id) = entity.id.as_ref() {
            return format!("{window_type}:{id}");
        }
    }
    window_type.to_string()
}

pub fn on_window_destroyed(app: &AppHandle, state: &SharedState, label: &str) {
    // main・HUD3枚・topmenu・notification・hintbar・tutorial・parameter-setup・tips は管理対象外なのでスキップする。
    // notification・hintbar・tutorial は open_windows に登録されない専用ウィンドウのため、
    // 削除処理を行うと誤った popup-count-changed イベントが発行されてしまう。
    if label == "main"
        || HUD_WINDOW_LABELS.contains(&label)
        || label == "topmenu"
        || label == "notification"
        || label == "hintbar"
        || label == "tutorial"
        || label == "parameter-setup"
        || label == "tips"
    {
        return;
    }

    {
        let mut guard = state.lock();
        guard.open_windows.remove(label);
        // LRU にも残っている可能性がある(LRU上限による破棄時など)ので除外する。
        guard.hidden_lru.retain(|l| l != label);
    }

    let _ = app.emit("popup-count-changed", json!({}));
}

pub fn get_window_context(state: &SharedState, label: &str) -> Option<WindowContextDto> {
    state.lock().to_context(label)
}

pub fn close_from_frontend(app: &AppHandle, state: &SharedState, label: &str) -> Result<(), String> {
    close_window_by_label(app, state, label)
}

/// 現在開いているすべてのポップアップウィンドウを閉じる。
///
/// ゲーム終了時に、タスクバーやトップメニューの切り替えと同時に
/// 残っているモーダル・パネルを一掃するために使用する。
/// open_windows に登録されている各ウィンドウに対して close_window_by_label を呼び出し、
/// 編集モードの後始末（STATE_NORMAL への復帰など）も適切に行われる。
pub fn close_all_popup_windows(app: &AppHandle, state: &SharedState) {
    let labels: Vec<String> = {
        let guard = state.lock();
        guard.open_windows.keys().cloned().collect()
    };
    for label in labels {
        let _ = close_window_by_label(app, state, &label);
    }
}

pub fn get_connection_status(state: &SharedState) -> Value {
    let guard = state.lock();
    json!({
        "api_base_url": guard.api_base_url,
        // 非表示(再利用待ち)ウィンドウは表示中のポップアップ数に含めない。
        "open_window_count": guard.open_windows.values().filter(|w| !w.hidden).count(),
    })
}

/// Loading ウィンドウを表示する。既に表示されている場合は何もしない。
/// 画面中央に 400x200 の小ウィンドウを表示し、ゲーム開始/ロード中の待機を示す。
pub fn show_loading_window(app: &AppHandle, state: &SharedState) -> Result<(), String> {
    if app.get_webview_window("loading").is_some() {
        return Ok(());
    }

    let monitor_index = {
        let guard = state.lock();
        guard.current_monitor_index
    };

    let monitors: Vec<_> = app
        .available_monitors()
        .unwrap_or_default()
        .into_iter()
        .collect();

    let mut builder = WebviewWindowBuilder::new(
        app,
        "loading",
        WebviewUrl::App("index.html".into()),
    )
    .title("Loading")
    .decorations(false)
    .transparent(true)
    .always_on_top(true)
    .skip_taskbar(true)
    .resizable(false)
    .inner_size(400.0, 200.0);

    if let Some(monitor) = monitors.get(monitor_index) {
        let scale = monitor.scale_factor();
        let m_size = monitor.size().to_logical::<f64>(scale);
        let m_pos = monitor.position().to_logical::<f64>(scale);
        let x = m_pos.x + (m_size.width - 400.0) / 2.0;
        let y = m_pos.y + (m_size.height - 200.0) / 2.0;
        builder = builder.position(x, y);
    }

    builder.build().map_err(|e| e.to_string())?;
    Ok(())
}

/// Loading ウィンドウを閉じる。存在しない場合は何もしない。
pub fn hide_loading_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("loading") {
        let _ = window.close();
    }
}

/// パラメータ設定ウィンドウを開く。
/// edit_mode=false … フリーモード新規ゲーム開始時の初期セットアップ。
/// edit_mode=true … テンプレート作成モード中の再編集（完了しても game:start を送らない）。
/// どちらのモードで開いたかを state に記録し、React 側が起動時に問い合わせる。
/// 既に開いている場合はモードだけ更新して前面へ出す。
///
/// メインスレッドから呼んではならない(`builder.build()` がイベントループ待ちでデッドロックする)。
/// Tauriコマンド経由なら `async fn`、それ以外なら別スレッドから呼ぶこと。
pub fn open_parameter_setup_window(
    app: &AppHandle,
    state: &SharedState,
    edit_mode: bool,
) -> Result<(), String> {
    {
        let mut guard = state.lock();
        guard.parameter_setup_edit_mode = edit_mode;
    }

    if let Some(window) = app.get_webview_window("parameter-setup") {
        let _ = window.set_focus();
        return Ok(());
    }

    let monitor_index = {
        let guard = state.lock();
        guard.current_monitor_index
    };

    let monitors: Vec<_> = app
        .available_monitors()
        .unwrap_or_default()
        .into_iter()
        .collect();

    let width = 620.0_f64;
    let height = 560.0_f64;

    let mut builder = WebviewWindowBuilder::new(
        app,
        "parameter-setup",
        WebviewUrl::App("index.html".into()),
    )
    .title("ゲームパラメータ設定")
    .decorations(false)
    .transparent(true)
    .always_on_top(true)
    .skip_taskbar(true)
    .resizable(false)
    .inner_size(width, height);

    if let Some(monitor) = monitors.get(monitor_index) {
        let scale = monitor.scale_factor();
        let m_size = monitor.size().to_logical::<f64>(scale);
        let m_pos = monitor.position().to_logical::<f64>(scale);
        let x = m_pos.x + (m_size.width - width) / 2.0;
        let y = m_pos.y + (m_size.height - height) / 2.0;
        builder = builder.position(x, y);
    }

    builder.build().map_err(|e| e.to_string())?;
    Ok(())
}

/// パラメータ設定ウィンドウを閉じる。存在しない場合は何もしない。
pub fn close_parameter_setup_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("parameter-setup") {
        let _ = window.close();
    }
}

/// Tips of the Day ウィンドウを表示する。既に表示されている場合は何もしない。
/// 画面中央に 580x380 の小ウィンドウを表示する。
pub fn open_tips_window(app: &AppHandle, state: &SharedState) -> Result<(), String> {
    let monitor_index = {
        let guard = state.lock();
        guard.current_monitor_index
    };
    open_tips_window_on_monitor(app, monitor_index)
}

/// Tips of the Day ウィンドウをモニターインデックス指定で表示する。
/// close_parameter_setup_window の直後に呼ぶと WebView2 破棄処理とデッドロックするため、
/// finish_parameter_setup からは必ず遅延スレッド経由で呼ぶこと。
pub fn open_tips_window_on_monitor(app: &AppHandle, monitor_index: usize) -> Result<(), String> {
    if app.get_webview_window("tips").is_some() {
        return Ok(());
    }

    let monitors: Vec<_> = app
        .available_monitors()
        .unwrap_or_default()
        .into_iter()
        .collect();

    let width = 680.0_f64;
    let height = 380.0_f64;

    let mut builder = WebviewWindowBuilder::new(
        app,
        "tips",
        WebviewUrl::App("index.html".into()),
    )
    .title("Today's Tips")
    .decorations(false)
    .transparent(true)
    .always_on_top(true)
    .skip_taskbar(true)
    .resizable(false)
    .inner_size(width, height);

    if let Some(monitor) = monitors.get(monitor_index) {
        let scale = monitor.scale_factor();
        let m_size = monitor.size().to_logical::<f64>(scale);
        let m_pos = monitor.position().to_logical::<f64>(scale);
        let x = m_pos.x + (m_size.width - width) / 2.0;
        let y = m_pos.y + (m_size.height - height) / 2.0;
        builder = builder.position(x, y);
    }

    let window = builder.build().map_err(|e| e.to_string())?;
    log::info!("[tips] ウィンドウ作成成功: label={}", window.label());
    Ok(())
}

/// Tips of the Day ウィンドウを閉じる。存在しない場合は何もしない。
pub fn close_tips_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("tips") {
        let _ = window.close();
    }
}

/// 通知ポップアップの横幅（論理ピクセル）。
/// システムトレイに重ならないよう、右端から一定のマージンを空けて配置する。
const NOTIFICATION_WIDTH: f64 = 320.0;

/// 通知ポップアップの最小高さ（論理ピクセル）。
/// アイコンと本文が収まる最低限の高さを確保する。
const NOTIFICATION_MIN_HEIGHT: f64 = 72.0;

/// 通知ポップアップの最大高さ（論理ピクセル）。
/// 本文が長い場合でもこの高さを超えないよう、フロントエンド側でスクロールまたは折り返しを行う。
const NOTIFICATION_MAX_HEIGHT: f64 = 200.0;

/// 通知ポップアップをキューに追加し、表示可能であれば即座に表示を開始する。
///
/// 既に通知が表示中の場合はキューに積むだけで、現在の通知が終了した後に自動的に次が表示される。
pub fn enqueue_notification(app: &AppHandle, state: &SharedState, payload: NotificationPayload) {
    let should_start = {
        let mut guard = state.lock();
        guard.notification_queue.push_back(payload);
        !guard.notification_showing
    };

    if should_start {
        let app = app.clone();
        let state = state.clone();
        std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(50));
            let _ = process_notification_queue(&app, &state);
        });
    }
}

/// キューの先頭の通知を表示する。
///
/// ウィンドウが未作成の場合は新規に生成し、既存の場合は `notification-show` イベントを送信して内容を上書きする。
/// 一定時間で消えるように設定されている場合は、別スレッドでタイマーを開始する。
fn process_notification_queue(app: &AppHandle, state: &SharedState) -> Result<(), String> {
    let payload = {
        let mut guard = state.lock();
        if guard.notification_showing {
            return Ok(());
        }
        let payload = guard.notification_queue.pop_front();
        if payload.is_some() {
            guard.notification_showing = true;
        }
        payload
    };

    let Some(payload) = payload else {
        return Ok(());
    };

    let win = match app.get_webview_window("notification") {
        Some(win) => win,
        None => create_notification_window(app, state)?,
    };

    // フロントエンドに通知内容を送信してからウィンドウを表示する。
    // これにより、フロントエンドが高さを測定・リサイズしてからスライドインできる。
    win.emit("notification-show", &payload)
        .map_err(|e| e.to_string())?;
    win.show().map_err(|e| e.to_string())?;

    if let Some(duration_ms) = payload.duration_ms {
        let app = app.clone();
        let state = state.clone();
        std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(duration_ms));
            let _ = dismiss_notification(&app, &state);
        });
    }

    Ok(())
}

/// 通知ウィンドウを新規作成する。
///
/// モニターの右下、タスクバーの上に配置する。
/// 初期状態では非表示かつ最大高さで作成し、フロントエンドからのリサイズ要求に応じて調整する。
fn create_notification_window(app: &AppHandle, state: &SharedState) -> Result<tauri::WebviewWindow, String> {
    let monitor_index = state.lock().current_monitor_index;
    let monitors: Vec<_> = app
        .available_monitors()
        .unwrap_or_default()
        .into_iter()
        .collect();

    let mut builder = WebviewWindowBuilder::new(
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
    .resizable(false)
    .visible(false)
    .inner_size(NOTIFICATION_WIDTH, NOTIFICATION_MAX_HEIGHT);

    if let Some(monitor) = monitors.get(monitor_index) {
        let scale = monitor.scale_factor();
        let m_size = monitor.size().to_logical::<f64>(scale);
        let m_pos = monitor.position().to_logical::<f64>(scale);

        let margin = 16.0;
        let x = m_pos.x + m_size.width - NOTIFICATION_WIDTH - margin;
        let y = m_pos.y + m_size.height - HUD_RIGHT_BOTTOM_RESERVED - NOTIFICATION_MAX_HEIGHT - margin;
        builder = builder.position(x, y);
    }

    builder.build().map_err(|e| e.to_string())
}

/// 通知ウィンドウを非表示にし、キューに残りがあれば次の通知を表示する。
///
/// ユーザーが×ボタンを押した場合や、一定時間経過後に Rust 側から呼び出される。
pub fn dismiss_notification(app: &AppHandle, state: &SharedState) -> Result<(), String> {
    {
        let mut guard = state.lock();
        guard.notification_showing = false;
    }

    if let Some(win) = app.get_webview_window("notification") {
        let _ = win.hide();
    }

    let has_next = state.lock().notification_queue.len() > 0;
    if has_next {
        let app = app.clone();
        let state = state.clone();
        std::thread::spawn(move || {
            // スライドアウトアニメーションの猶予を少し空ける
            std::thread::sleep(Duration::from_millis(300));
            let _ = process_notification_queue(&app, &state);
        });
    }

    Ok(())
}

/// フロントエンドから通知コンテンツの高さを受け取り、ウィンドウサイズと位置を調整する。
///
/// 通知の文字量に応じて高さが可変になるため、フロントエンドが実際のレンダリング高さを測定した後、
/// このコマンドを呼び出してウィンドウをフィットさせる。
pub fn resize_notification_window(app: &AppHandle, state: &SharedState, height: f64) -> Result<(), String> {
    let win = app
        .get_webview_window("notification")
        .ok_or("notification window not found")?;

    let monitor_index = state.lock().current_monitor_index;
    let monitors: Vec<_> = app
        .available_monitors()
        .unwrap_or_default()
        .into_iter()
        .collect();

    let clamped_height = height.clamp(NOTIFICATION_MIN_HEIGHT, NOTIFICATION_MAX_HEIGHT);

    if let Some(monitor) = monitors.get(monitor_index) {
        let scale = monitor.scale_factor();
        let m_size = monitor.size().to_logical::<f64>(scale);
        let m_pos = monitor.position().to_logical::<f64>(scale);

        let margin = 16.0;
        let x = m_pos.x + m_size.width - NOTIFICATION_WIDTH - margin;
        let y = m_pos.y + m_size.height - HUD_RIGHT_BOTTOM_RESERVED - clamped_height - margin;

        win.set_size(LogicalSize::new(NOTIFICATION_WIDTH, clamped_height))
            .map_err(|e| e.to_string())?;
        win.set_position(LogicalPosition::new(x, y))
            .map_err(|e| e.to_string())?;
    } else {
        win.set_size(LogicalSize::new(NOTIFICATION_WIDTH, clamped_height))
            .map_err(|e| e.to_string())?;
    }

    Ok(())
}

/// Graphic(Unity) を起動する、または起動済みの場合は MMF 経由でリセット指示を送る
/// 戻り値: "launched" | "reset_sent" | エラー
pub fn launch_graphic(state: &SharedState) -> Result<String, String> {
    let (has_writer, launch_command, api_base_url) = {
        let guard = state.lock();
        (
            guard.writer_channel.is_some(),
            guard.graphic_launch_command.clone(),
            guard.api_base_url.clone(),
        )
    };

    let is_external = std::env::var("GENESIS_GRAPHIC_EXTERNAL")
        .map(|v| v == "true")
        .unwrap_or(false);

    if has_writer {
        ipc::send_ui_action(
            state,
            "".to_string(),
            None,
            "session:reset".to_string(),
            json!({}),
        )?;
        return Ok("reset_sent".to_string());
    }

    if is_external {
        // バックエンドから既に Unity が起動されている場合は、新たな起動をスキップする
        return Ok("external_already_launched".to_string());
    }

    if launch_command.is_empty() {
        return Err("graphic-launch.toml に launch_command が設定されていません".to_string());
    }

    // 実行ファイルと同じディレクトリを基準に相対パスを解決する。
    // shell(cmd /C)は経由せず直接起動する(本番経路の genesis.py と挙動を揃える。
    // 環境変数の継承とプロセスツリーが素直になる)。
    // launch_command には Unity 実行ファイルのパスをそのまま指定すること
    // (旧形式の `cmd /c "..."` は使えない)。
    let exe_dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.to_path_buf()))
        .unwrap_or_default();

    let graphic_path = {
        let p = std::path::Path::new(&launch_command);
        if p.is_absolute() {
            p.to_path_buf()
        } else {
            exe_dir.join(p)
        }
    };

    std::process::Command::new(&graphic_path)
        .current_dir(&exe_dir)
        .args([
            "--api-base-url",
            &format!("{}/api/v1", api_base_url.trim_end_matches('/')),
        ])
        .spawn()
        .map_err(|err| format!("Graphic の起動に失敗しました: {err}"))?;

    Ok("launched".to_string())
}

pub fn reset_window_geometries(app: &AppHandle, state: &SharedState) -> Result<(), String> {
    let (open_windows, monitor_index, registry) = {
        let mut guard = state.lock();
        guard.saved_geometries.clear();
        let open_windows: Vec<(String, String)> = guard
            .open_windows
            .iter()
            .map(|(label, w_state)| (label.clone(), w_state.window_type.clone()))
            .collect();
        let monitor_index = guard.current_monitor_index;
        let registry = guard.registry.clone();
        (open_windows, monitor_index, registry)
    };

    let monitors: Vec<_> = app.available_monitors()
        .unwrap_or_default()
        .into_iter()
        .collect();

    for (label, window_type) in open_windows {
        let Some(window) = app.get_webview_window(&label) else {
            continue;
        };
        let Some(definition) = registry.get(&window_type) else {
            continue;
        };

        // 記憶を消した直後の状態＝「初めて開いたときの配置」なので、
        // 新規オープンと同じ計算(saved_geometry なし)をそのまま使う。
        let payload = OpenWindowPayload {
            window_type: window_type.clone(),
            entity: None,
            params: None,
        };
        let (size, pos) =
            compute_window_geometry(definition, &payload, None, &monitors, monitor_index);

        let _ = window.set_size(size);
        let _ = window.set_position(pos);
    }

    Ok(())
}
