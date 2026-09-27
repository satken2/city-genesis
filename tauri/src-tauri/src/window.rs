use crate::ipc;
use crate::messages::{EntityRef, NotificationPayload, OpenWindowPayload, WindowContextDto, WindowDefinition, WindowLayout};
use crate::state::{AppState, OpenWindowState, SharedState, WindowGeometry, MAX_HIDDEN_WINDOWS};
use serde_json::{json, Value};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;
use tauri::{
    AppHandle, Emitter, LogicalPosition, LogicalSize, Manager, Monitor, PhysicalPosition, Runtime, WebviewUrl,
    WebviewWindow, WebviewWindowBuilder,
};

/// HUD(ゲーム中に画面へ常駐する操作パネル群)のウィンドウラベル。
/// 複数枚に分かれているのは、Unityの3Dビューの上に重ねる透明ウィンドウでは
/// 「見た目が透明な領域でもクリックがWebViewに吸われる」ため、
/// パネルの実面積だけを覆うウィンドウに分割する必要があるから。
pub const LABEL_ICON_PANEL: &str = "icon-panel";
pub const LABEL_COCKPIT: &str = "cockpit";
pub const LABEL_SYSTEM_TRAY: &str = "system-tray";
pub const LABEL_DISPLAY_TRAY: &str = "display-tray";
pub const HUD_WINDOW_LABELS: [&str; 4] = [
    LABEL_ICON_PANEL,
    LABEL_COCKPIT,
    LABEL_SYSTEM_TRAY,
    LABEL_DISPLAY_TRAY,
];

/// HUD各パネルと画面端・他ウィンドウとの間隔
pub const HUD_MARGIN: f64 = 8.0;

/// アイコンパネル(画面左端・縦並びのメニューアイコン)の幅。
/// `IconPanel.css` のボタン寸法(46px)+ パネルの左右パディング(6px×2)と一致させること。
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

/// 表示トレイ(画面左下・レイヤーの表示ON/OFFを切り替えるアイコン列)のサイズ。
/// ボタン数が固定(6個)なので実測値の通知は行わず、ここの値をそのまま使う。
/// `DisplayTray.css` のボタン寸法(30px)・間隔(6px)・左右パディング(8px)・枠線(1px)と
/// 一致させること(30×6 + 6×5 + 8×2 + 1×2 = 228)。
/// 足りないとボタンが切れ、余ると透明領域がクリックを吸ってUnityを操作できなくなる。
pub const DISPLAY_TRAY_WIDTH: f64 = 228.0;
pub const DISPLAY_TRAY_HEIGHT: f64 = 40.0;

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

/// ウィンドウの配置。サイズは論理ピクセル、位置は物理ピクセル(仮想スクリーン座標)。
///
/// 位置を論理座標のまま渡すと、tao は移動時にはウィンドウが今いるモニターの倍率で、生成時には
/// 座標を含むモニターを先頭から探してその倍率で物理座標へ換算する。倍率の違うモニターが混在すると
/// 別の場所(最悪は別のモニター)に出るため、モニター上への配置はすべてこの型を通して物理座標で渡す。
#[derive(Debug, Clone, Copy)]
pub struct Placement {
    pub size: LogicalSize<f64>,
    pub position: PhysicalPosition<i32>,
}

impl Placement {
    /// `monitor` の倍率で計算した論理座標(原点は `monitor.position().to_logical(倍率)`)から配置を作る。
    /// モニターが取得できないときは倍率1として扱う。
    pub fn on_monitor(monitor: Option<&Monitor>, size: LogicalSize<f64>, position: LogicalPosition<f64>) -> Self {
        let scale = monitor.map_or(1.0, |m| m.scale_factor());
        Self {
            size,
            position: position.to_physical(scale),
        }
    }

    /// ウィンドウを移動・リサイズする。
    /// サイズは移動先の倍率で換算させるため移動の後に合わせる。倍率の違うモニターへ移ると
    /// Windowsが提案する矩形(WM_DPICHANGED)で位置もずれうるので、最後に位置を打ち直す。
    pub fn apply<R: Runtime>(&self, window: &WebviewWindow<R>) {
        let _ = window.set_position(self.position);
        let _ = window.set_size(self.size);
        let _ = window.set_position(self.position);
    }
}

/// ウィンドウを非表示で生成し、`placement` に置いてから `visible` なら表示する。
/// 生成時の位置指定は tao が推測したモニターの倍率で読まれるため使わない(`Placement` 参照)。
pub fn build_placed<R: Runtime, M: Manager<R>>(
    builder: WebviewWindowBuilder<'_, R, M>,
    placement: Placement,
    visible: bool,
) -> Result<WebviewWindow<R>, String> {
    let window = builder
        .visible(false)
        .inner_size(placement.size.width, placement.size.height)
        .build()
        .map_err(|e| e.to_string())?;
    placement.apply(&window);
    if visible {
        window.show().map_err(|e| e.to_string())?;
    }
    Ok(window)
}

/// ポップアップを表示するモニターの番号を決める。
///
/// 通常はHUDと同じモニター。マルチディスプレイモードではHUDとは別のモニターにし、
/// 指定のモニターがHUDと同じか接続されていなければHUDの次のモニターを使う
/// (HUDを指定のモニターへ移したときや、モニターを外したときも別のモニターに出し続けるため)。
/// モニターが1台ならモードに関わらずHUDと同じになる。
pub fn resolve_popup_monitor(multi_display: bool, hud: usize, preferred: usize, count: usize) -> usize {
    if !multi_display || count < 2 {
        return hud;
    }
    if preferred < count && preferred != hud {
        preferred
    } else {
        (hud + 1) % count
    }
}

/// ポップアップの表示先を、`current` から `direction` 方向(正で次、負で前)にあるHUDの無いモニターへ送る。
pub fn next_popup_monitor(hud: usize, current: usize, count: usize, direction: i32) -> usize {
    if count < 2 {
        return current;
    }
    let mut next = current % count;
    loop {
        next = if direction > 0 {
            (next + 1) % count
        } else {
            (next + count - 1) % count
        };
        if next != hud {
            return next;
        }
    }
}

/// ポップアップの表示先モニターの番号と、そこにHUDがあるか。
fn popup_monitor_of(state: &AppState, monitor_count: usize) -> (usize, bool) {
    let index = resolve_popup_monitor(
        state.multi_display,
        state.current_monitor_index,
        state.popup_monitor_index,
        monitor_count,
    );
    (index, index == state.current_monitor_index)
}

/// ポップアップ(`window-registry.json` 駆動のウィンドウ)の表示先。
struct PopupTarget {
    monitor: Option<Monitor>,
    /// 表示先モニターにHUDがあるか。あればHUDの占有領域を避けて配置する。
    has_hud: bool,
}

fn popup_target(app: &AppHandle, state: &SharedState) -> PopupTarget {
    let monitors = app.available_monitors().unwrap_or_default();
    let (index, has_hud) = popup_monitor_of(&state.lock(), monitors.len());
    PopupTarget {
        monitor: monitors.into_iter().nth(index),
        has_hud,
    }
}

/// HUD・ポップアップの表示先の設定を `update` で書き換える。
/// ポップアップの表示先が変わったら `reset_window_geometries` で開いているポップアップを新しい表示先へ移す。
/// 記憶した位置・サイズは元のモニターでの値なので、残すと開き直したときに元のモニターへ戻る。
pub fn update_display_target(
    app: &AppHandle,
    state: &SharedState,
    update: impl FnOnce(&mut AppState),
) -> Result<(), String> {
    let count = app.available_monitors().map(|m| m.len()).unwrap_or(0);
    let moved = {
        let mut guard = state.lock();
        let before = popup_monitor_of(&guard, count);
        update(&mut *guard);
        before != popup_monitor_of(&guard, count)
    };
    if moved {
        reset_window_geometries(app, state)?;
    }
    Ok(())
}

/// HUDウィンドウのタイトルと初期サイズ。
/// アイコンパネルの高さだけは中身のアイコン数で変わるため、ここでは既定値を返し、
/// 実際の高さは React 側の実測値(`resize_icon_panel`)で上書きされる。
fn hud_window_spec(label: &str) -> (&'static str, f64, f64) {
    match label {
        LABEL_ICON_PANEL => ("Genesis Icon Panel", ICON_PANEL_WIDTH, ICON_PANEL_DEFAULT_HEIGHT),
        LABEL_COCKPIT => ("Genesis Cockpit", COCKPIT_WIDTH, COCKPIT_HEIGHT),
        LABEL_DISPLAY_TRAY => ("Genesis Display Tray", DISPLAY_TRAY_WIDTH, DISPLAY_TRAY_HEIGHT),
        _ => ("Genesis System Tray", SYSTEM_TRAY_WIDTH, SYSTEM_TRAY_HEIGHT),
    }
}

/// HUDウィンドウを生成する。初期状態は非表示で、
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

/// HUD各ウィンドウを対象モニターの定位置(左端中央・下部中央・右下・左下)へ配置する。
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
    let place = |win: &WebviewWindow, width: f64, height: f64, x: f64, y: f64| {
        Placement::on_monitor(Some(monitor), LogicalSize::new(width, height), LogicalPosition::new(x, y)).apply(win);
    };
    // 現在の寸法はウィンドウが今いるモニターの倍率で論理値に直す(移動先の倍率で割ると別のモニターから移すときに狂う)
    let current_size = |win: &WebviewWindow| {
        let win_scale = win.scale_factor().unwrap_or(scale);
        win.inner_size().ok().map(|s| s.to_logical::<f64>(win_scale))
    };

    // アイコンパネル: 画面左端に縦置きし、上下方向は中央寄せにする
    if let Some(win) = app.get_webview_window(LABEL_ICON_PANEL) {
        let current = current_size(&win)
            .map(|s| s.height)
            .filter(|h| *h > 1.0)
            .unwrap_or(ICON_PANEL_DEFAULT_HEIGHT);
        let height = icon_panel_height
            .unwrap_or(current)
            .clamp(1.0, m_size.height - HUD_MARGIN * 2.0);
        place(
            &win,
            ICON_PANEL_WIDTH,
            height,
            m_pos.x + HUD_MARGIN,
            m_pos.y + (m_size.height - height) / 2.0,
        );
    }

    // コックピット: 画面下部中央
    if let Some(win) = app.get_webview_window(LABEL_COCKPIT) {
        place(
            &win,
            COCKPIT_WIDTH,
            COCKPIT_HEIGHT,
            m_pos.x + (m_size.width - COCKPIT_WIDTH) / 2.0,
            m_pos.y + m_size.height - COCKPIT_HEIGHT - HUD_MARGIN,
        );
    }

    // システムトレイ: 画面右下。幅は中身のボタン数に合わせて右端基準で伸縮する
    if let Some(win) = app.get_webview_window(LABEL_SYSTEM_TRAY) {
        let current = current_size(&win)
            .map(|s| s.width)
            .filter(|w| *w > 1.0)
            .unwrap_or(SYSTEM_TRAY_WIDTH);
        let width = system_tray_width
            .unwrap_or(current)
            .clamp(1.0, m_size.width - HUD_MARGIN * 2.0);
        place(
            &win,
            width,
            SYSTEM_TRAY_HEIGHT,
            m_pos.x + m_size.width - width - HUD_MARGIN,
            m_pos.y + m_size.height - SYSTEM_TRAY_HEIGHT - HUD_MARGIN,
        );
    }

    // 表示トレイ: 画面左下。アイコンパネルと同じく画面左端からマージンだけ空けて置く。
    // 左ドックのポップアップは下端から `HUD_BOTTOM_RESERVED` + マージン分を空けるので、
    // この高さのトレイとは縦に重ならない。
    if let Some(win) = app.get_webview_window(LABEL_DISPLAY_TRAY) {
        place(
            &win,
            DISPLAY_TRAY_WIDTH,
            DISPLAY_TRAY_HEIGHT,
            m_pos.x + HUD_MARGIN,
            m_pos.y + m_size.height - DISPLAY_TRAY_HEIGHT - HUD_MARGIN,
        );
    }
}

/// HUDウィンドウをまとめて表示/非表示する。
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

    // ドック配置では位置を記憶せず、サイズのみ更新する。位置は物理座標のまま記憶する(`WindowGeometry`)。
    let pos = if record_position && !is_docked {
        win.outer_position().ok()
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

/// ポップアップの配置(サイズと位置)をレイアウト種別に応じて計算する。
///
/// 新規ウィンドウの生成と、非表示ウィンドウの再利用・表示先の変更時の再配置のすべてから
/// 呼ばれる共通ロジック。レイアウト計算を一元化することで、どの経路でも同じ位置・サイズが適用される。
///
/// 表示先モニターにHUDがあれば、ドック配置は常駐HUD(左のアイコンパネル・下部中央のコックピット・
/// 右下のシステムトレイ)を避けた領域に収める。左ドックは `HUD_LEFT_RESERVED` だけ右へ寄せ、丈は
/// 下部の占有分を引く。マルチディスプレイモードでHUDの無いモニターに置くときは画面端まで使う。
///
/// - ドック配置(LeftPanel/RightPanel/BottomCenter/LeftTop/RightTop): モニター情報を基準に位置を決定する。
///   saved_geometry があればサイズを上書きする(位置はレイアウトに従う)。
/// - 自由配置(Default): saved_geometry があれば位置とサイズをそのまま復元する。
///   なければ width_ratio/height_ratio または definition.width/height でサイズを決め、
///   payload.params.preferred_position または表示先モニターの中央に配置する。
///
/// モニターが取得できない場合は definition のサイズと位置 (0,0) にフォールバックする。
fn compute_window_geometry(
    definition: &WindowDefinition,
    payload: &OpenWindowPayload,
    saved_geometry: Option<WindowGeometry>,
    target: &PopupTarget,
) -> Placement {
    let monitor = target.monitor.as_ref();
    let (left_reserved, bottom_reserved, right_bottom_reserved) = if target.has_hud {
        (HUD_LEFT_RESERVED, HUD_BOTTOM_RESERVED, HUD_RIGHT_BOTTOM_RESERVED)
    } else {
        (0.0, 0.0, 0.0)
    };

    if definition.layout == WindowLayout::LeftPanel
        || definition.layout == WindowLayout::LeftPanelNarrow
    {
        // 左サイドパネル: アイコンパネルの右隣に上下いっぱいに配置する
        if let Some(monitor) = monitor {
            let scale = monitor.scale_factor();
            let m_size = monitor.size().to_logical::<f64>(scale);
            let m_pos = monitor.position().to_logical::<f64>(scale);

            let margin = HUD_MARGIN;
            // 幅はレイアウト既定の比率。同じ左ドックでも中身の密度で必要な幅が違うため、
            // 定義が width_ratio を持つならそちらを優先する。
            let default_ratio = if definition.layout == WindowLayout::LeftPanelNarrow {
                0.2
            } else {
                0.4
            };
            let width_ratio = definition
                .width_ratio
                .filter(|r| *r > 0.0)
                .unwrap_or(default_ratio);
            let computed_width = (m_size.width * width_ratio).round();
            let computed_height = m_size.height - bottom_reserved - margin * 2.0;

            // 記憶済みサイズがあればそれを優先する（位置はレイアウトに従う）
            let panel_width = saved_geometry.map(|g| g.width).unwrap_or(computed_width);
            let panel_height = saved_geometry.map(|g| g.height).unwrap_or(computed_height);

            return Placement::on_monitor(
                Some(monitor),
                LogicalSize::new(panel_width, panel_height),
                LogicalPosition::new(m_pos.x + left_reserved + margin, m_pos.y + margin),
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
            let computed_height = m_size.height - right_bottom_reserved - margin * 2.0;

            // 記憶済みサイズがあればそれを優先する。右端配置なので位置は幅に依存する。
            let panel_width = saved_geometry.map(|g| g.width).unwrap_or(computed_width);
            let panel_height = saved_geometry.map(|g| g.height).unwrap_or(computed_height);

            return Placement::on_monitor(
                Some(monitor),
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
            let y = m_pos.y + m_size.height - bottom_reserved - panel_height - margin;

            return Placement::on_monitor(
                Some(monitor),
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

            return Placement::on_monitor(
                Some(monitor),
                LogicalSize::new(w, h),
                LogicalPosition::new(m_pos.x + left_reserved + margin, m_pos.y + margin),
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
            let work_h = (m_size.height - bottom_reserved - margin * 2.0).max(200.0);
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

            return Placement::on_monitor(
                Some(monitor),
                LogicalSize::new(w, h),
                LogicalPosition::new(m_pos.x + m_size.width - w - margin, m_pos.y + margin),
            );
        }
    } else {
        // 自由配置: 記憶済みの位置・サイズがあればそれをそのまま復元する。
        if let Some(geo) = saved_geometry {
            return Placement {
                size: LogicalSize::new(geo.width, geo.height),
                position: PhysicalPosition::new(geo.x, geo.y),
            };
        }

        let (win_w, win_h) = match (
            definition.width_ratio,
            definition.height_ratio,
            monitor,
        ) {
            (Some(rw), Some(rh), Some(monitor)) if rw > 0.0 && rh > 0.0 => {
                let scale = monitor.scale_factor();
                let m_size = monitor.size().to_logical::<f64>(scale);
                let work_h = (m_size.height - bottom_reserved).max(100.0);
                ((m_size.width * rw).round(), (work_h * rh).round())
            }
            _ => (definition.width as f64, definition.height as f64),
        };

        // 親から明示的な位置指定があればそれを使い、なければ表示先モニターの中央に配置する
        let centered = || match monitor {
            Some(monitor) => {
                let scale = monitor.scale_factor();
                let m_size = monitor.size().to_logical::<f64>(scale);
                let m_pos = monitor.position().to_logical::<f64>(scale);
                LogicalPosition::new(
                    m_pos.x + (m_size.width - win_w) / 2.0,
                    m_pos.y + (m_size.height - bottom_reserved - win_h) / 2.0,
                )
            }
            None => LogicalPosition::new(0.0, 0.0),
        };
        let pos = match payload.params.as_ref().and_then(|p| p.preferred_position.as_ref()) {
            Some(position) => match (position.x, position.y) {
                (Some(x), Some(y)) => LogicalPosition::new(x, y),
                _ => centered(),
            },
            None => centered(),
        };

        return Placement::on_monitor(monitor, LogicalSize::new(win_w, win_h), pos);
    }

    // モニターが取得できない場合のフォールバック
    Placement::on_monitor(
        None,
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
        let was_hidden = {
            let mut guard = state.lock();
            let was_hidden = guard
                .open_windows
                .get(&label)
                .map(|w| w.hidden)
                .unwrap_or(false);
            // 下の排他クローズで LRU 上限の本破棄や転用に巻き込まれないよう、先に LRU から外す。
            if was_hidden {
                guard.hidden_lru.retain(|l| l != &label);
            }
            was_hidden
        };
        // 非表示からの再表示は新規に開くのと同じなので排他グループを適用する。
        // 自身はまだ hidden なので、自分の種別を含むグループでも自身は閉じられない。
        if was_hidden {
            apply_mutex_rules(&app, &state, &payload.window_type)?;
        }
        {
            let mut guard = state.lock();
            // 非表示(再利用待ち)状態から復帰させる。表示中の場合は hidden フラグは既に false。
            // 再表示は「閉じたウィンドウを開き直した」操作なので open_seq を進め、
            // React 側にパネルの再マウント(=状態リセット)を要求する。
            // 表示中ウィンドウへの再オープン要求(フォーカス移動)ではインクリメントしない。
            if was_hidden {
                if let Some(w) = guard.open_windows.get_mut(&label) {
                    w.hidden = false;
                    w.open_seq += 1;
                }
            }
            // entity/params を最新要求で上書きする(同一 instance_key だが参照ポイントが変わる可能性がある)。
            if let Some(window) = guard.open_windows.get_mut(&label) {
                window.entity = payload.entity.clone();
                window.params =
                    serde_json::to_value(payload.params.clone()).unwrap_or(Value::Null);
            }
        }

        if let Some(window) = app.get_webview_window(&label) {
            // 再利用待ちの最大化ウィンドウは表示先の変更時に置き直せない(`reset_window_geometries`)ので、ここで表示先へ移す
            if definition.maximized {
                let target = popup_target(&app, &state);
                if !is_on_monitor(&window, target.monitor.as_ref()) {
                    place_popup(&window, compute_window_geometry(&definition, &payload, None, &target), true);
                }
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

    apply_mutex_rules(&app, &state, &payload.window_type)?;

    // レイアウト計算に必要な情報を先に取得しておく(新規・再利用で共有)。
    let saved_geometry = {
        let guard = state.lock();
        guard.saved_geometries.get(&payload.window_type).copied()
    };
    let target = popup_target(&app, &state);

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

        let placement = compute_window_geometry(&definition, &payload, saved_geometry, &target);

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
            place_popup(&window, placement, definition.maximized);
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

    let placement = compute_window_geometry(&definition, &payload, saved_geometry, &target);

    let builder = WebviewWindowBuilder::new(&app, label.clone(), WebviewUrl::App("index.html".into()))
        .title(&title)
        .decorations(false)
        .resizable(definition.resizable)
        .always_on_top(true);

    // 最大化は今いるモニターに対して効くので、表示先へ置いてから最大化する
    let window = build_placed(builder, placement, false)?;
    if definition.maximized {
        let _ = window.maximize();
    }
    window.show().map_err(|err| err.to_string())?;
    let _ = window.set_focus();

    Ok(())
}

/// ポップアップを配置する。最大化するウィンドウは表示先へ移してから最大化する
/// (最大化は今いるモニターに対して効くため)。
/// 最大化中のウィンドウは戻した時点で非表示でも表示されるので、非表示のまま置いておく最大化ウィンドウには使わない。
fn place_popup<R: Runtime>(window: &WebviewWindow<R>, placement: Placement, maximized: bool) {
    let _ = window.unmaximize();
    placement.apply(window);
    if maximized {
        let _ = window.maximize();
    }
}

/// ウィンドウが `monitor` 上にあるか。どちらかが取得できなければ置き直さないよう true とする。
fn is_on_monitor<R: Runtime>(window: &WebviewWindow<R>, monitor: Option<&Monitor>) -> bool {
    match (window.current_monitor().ok().flatten(), monitor) {
        (Some(current), Some(target)) => current.position() == target.position(),
        _ => true,
    }
}

/// 閉じたときにUnityを通常モードへ戻すウィンドウ(編集モードのウィンドウ)。
/// 「Unityの編集ステート(`STATE_*`)に入るもの」に加えて、
/// 「開いている間だけ3Dビューの表示を差し替えるもの(公共交通の一覧・新設)」も含める。
/// 表示を元へ戻すのはUnityの `STATE_NORMAL` 復帰なので、ここに載せないと
/// ×で閉じたときに差し替えた表示が残り続ける(ウィンドウは非表示になるだけで
/// React側のアンマウント処理が走らないため、パネル任せにはできない)。
///
/// Unityの編集ステートは1つしかないので、一覧の種別は互いに同時に開かない。ここに足す種別は
/// `window-mutex-groups.json` で一覧の全種別と双方向に排他にする(併存させると、先に開いた側の
/// パネルが別のステートに上書きされたまま残る。下のテストが検査する)。
///
/// 地区編集(STATE_EDIT_ZONE)とブルドーザ(STATE_REMOVE_OBJECTS)はウィンドウを開かないHUDのトグルなので
/// 含めない(トグルOFFでHUDが直接 STATE_NORMAL を送る)。zone_detail は表示専用でステートを変えないので含めない。
const EDIT_MODE_WINDOW_TYPES: &[&str] = &[
    "parcel_creation",
    "path_build",
    "infra_build",
    "track_build",
    "building_construction",
    "resource_edit",
    "prop_placement",
    "transit_management",
    "transit_creation",
    // 建物の選択で入力プリセットを上書きする(MODE_PICK_BUILDING)。選択待ちのまま閉じても戻すため。
    "agent_creation",
    "grading",
];

/// セッション切り替えでは、古いHTTP応答でTipsや結果を再表示させない。
pub fn cancel_epoch_presentation(app: &AppHandle, state: &SharedState) {
    let id = state.lock().epoch_presentation_id.clone();
    if let Some(id) = id {
        finish_epoch_presentation(app, state, &id);
        let _ = app.emit("epoch:darkened", json!({"id": id}));
    }
}

pub fn finish_epoch_presentation(app: &AppHandle, state: &SharedState, id: &str) {
    let connected = {
        let mut guard = state.lock();
        if guard.epoch_presentation_id.as_deref() != Some(id) { return; }
        guard.epoch_presentation_id = None;
        guard.graphic_connected
    };
    close_tips_window(app);
    if connected {
        if let Err(error) = ipc::send_ui_action(state, "".into(), None, "epoch:executing".into(), json!({"executing": false})) {
            log::error!("epoch presentation finish: {error}");
        }
    }
    let _ = app.emit("epoch:executing", json!({"executing": false}));
}

pub fn close_window_by_label(app: &AppHandle, state: &SharedState, label: &str) -> Result<(), String> {
    // ウィンドウタイプを先に取得しておく(非表示マーク後も参照できるが、取得時の意図を明確にするため)。
    let window_type = {
        let guard = state.lock();
        guard.open_windows.get(label).map(|w| w.window_type.clone())
    };
    let entity_id: Option<i64> = {
        let guard = state.lock();
        guard
            .open_windows
            .get(label)
            .and_then(|w| w.entity.as_ref())
            .and_then(|e| e.id.as_ref())
            .and_then(|id| id.parse::<i64>().ok())
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

    if window_type.as_deref() == Some("epoch_result") {
        let id = state.lock().open_windows.get(label)
            .and_then(|w| w.params.get("extra")?.get("epochPresentationId")?.as_str().map(str::to_owned));
        if let Some(id) = id { finish_epoch_presentation(app, state, &id); }
    }

    // 編集モードのウィンドウが閉じられたら STATE_NORMAL に戻す。
    // 別の編集モードのウィンドウを開いたことによる排他クローズでは、新しいウィンドウの表示より先に
    // 送られるので、新しいパネルがマウント時に送る編集ステートを上書きしない。
    // 建設確定後に計画表示に切り替える処理は、UI 側の各パネルで確定ボタン押下時に個別に行う。
    // ここでは一律で view:change_view を送らない。
    if let Some(ref wt) = window_type {
        if EDIT_MODE_WINDOW_TYPES.contains(&wt.as_str()) {
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "world:change_state".to_string(),
                json!({ "state": "STATE_NORMAL" }),
            );
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
            // 経済主体詳細パネルが閉じられたとき、接続端子のハイライトを消す。
            // Unity は接続点編集モード(STATE_EDIT_CONNECTOR)中のときだけ通常モードへ戻す。
            // 詳細パネルは編集モードのウィンドウと同時に開けるので、STATE_NORMAL を送ると
            // 後から開いた建物建設などのステートまで打ち切ってしまう(path_detail と同じ理由)。
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "edit_connector:hide".to_string(),
                json!({}),
            );
        } else if wt == "diagnosis" {
            // 診断ウィンドウが閉じられたとき、診断木が指すパス・建物のハイライト
            // (source="diagnosis")を消す。ウィンドウは非表示(再利用待ち)になるだけで
            // React側のアンマウント処理によるクリーンアップ送信が走らないため、
            // クローズ処理側で明示的に送る(path_detail と同じ理由)。
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "world:clear_highlights".to_string(),
                json!({ "source": "diagnosis" }),
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
        } else if wt == "road_detail" {
            // 道路詳細パネルが閉じられたとき、3Dの道路の強調帯を消し、この道路の区間編集モード中なら打ち切る。
            // ウィンドウは非表示(再利用待ち)になるだけでReact側のアンマウント処理が走らないため、
            // クローズ処理側で明示的に送る(path_detail と同じ理由)。
            // 道路詳細は道路ごとに1枚ずつ同時に開けるので、閉じた道路の road_id を載せる。Unity は
            // 区間編集モード中でないか、別の道路の編集中なら edit_road:cancel を無視する
            // (載せないと、別の道路詳細パネルが進めている編集まで打ち切ってしまう)。
            let params = match entity_id {
                Some(id) => json!({ "road_id": id }),
                None => json!({}),
            };
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "world:clear_road_focus".to_string(),
                params.clone(),
            );
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "edit_road:cancel".to_string(),
                params,
            );
            // 閉じるパネル自身にも知らせ、区間編集中の表示とヒントバーの案内を片付けさせる
            // (パネルはマウントされたまま非表示になり、閉じられたことを他に知る手段がない)。
            let _ = app.emit_to(label, "road:panel_closing", json!({}));
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
    // 再利用するWebViewはアンマウントされないため、案内の表示条件を明示的に失効させる。
    let _ = app.emit_to("tutorial", "tutorial:window_visibility", serde_json::json!({
        "label": label, "window": window_type, "entity": entity_id, "visible": false
    }));

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
    // 編集モードのウィンドウはUnityのステートを上書きするので、HUDの常駐モード(ブルドーザ・地区編集)とも
    // 排他にする。開く入口(HUDのアイコン・詳細パネルの「編集」ボタン等)を問わず解除するためここで送る。
    // 状態の正はHUDの全ウィンドウが購読する `hud:mode_changed`(UI側 `hud/hudMode.ts`)。
    if EDIT_MODE_WINDOW_TYPES.contains(&window_type) {
        let _ = app.emit("hud:mode_changed", json!({ "mode": null }));
    }

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
    // main・HUD各枚・topmenu・notification・hintbar・tutorial・parameter-setup・tips は管理対象外なのでスキップする。
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

    let builder = WebviewWindowBuilder::new(
        app,
        "parameter-setup",
        WebviewUrl::App("index.html".into()),
    )
    .title("ゲームパラメータ設定")
    .decorations(false)
    .transparent(true)
    .always_on_top(true)
    .skip_taskbar(true)
    .resizable(false);

    build_placed(builder, centered_on_monitor(monitors.get(monitor_index), width, height), true)?;
    Ok(())
}

/// モニター中央に置く配置。モニターが取得できなければ仮想スクリーンの原点に置く。
pub fn centered_on_monitor(monitor: Option<&Monitor>, width: f64, height: f64) -> Placement {
    let position = match monitor {
        Some(monitor) => {
            let scale = monitor.scale_factor();
            let m_size = monitor.size().to_logical::<f64>(scale);
            let m_pos = monitor.position().to_logical::<f64>(scale);
            LogicalPosition::new(
                m_pos.x + (m_size.width - width) / 2.0,
                m_pos.y + (m_size.height - height) / 2.0,
            )
        }
        None => LogicalPosition::new(0.0, 0.0),
    };
    Placement::on_monitor(monitor, LogicalSize::new(width, height), position)
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
/// ウィンドウ生成は非同期コマンドまたはワーカースレッドから行う。
pub fn open_tips_window_on_monitor(app: &AppHandle, monitor_index: usize) -> Result<(), String> {
    open_tips_window_with_mode(app, monitor_index, false)
}

/// 初期ロードは開始ボタン待ち、エポック待機は呼び出し元が完了時に閉じる。
pub fn open_tips_window_with_mode(app: &AppHandle, monitor_index: usize, epoch: bool) -> Result<(), String> {
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

    let builder = WebviewWindowBuilder::new(
        app,
        "tips",
        WebviewUrl::App(if epoch { "index.html?tipsMode=epoch" } else { "index.html" }.into()),
    )
    .title("Today's Tips")
    .decorations(false)
    .transparent(true)
    .always_on_top(true)
    .skip_taskbar(true)
    .resizable(false);

    let window = build_placed(builder, centered_on_monitor(monitors.get(monitor_index), width, height), true)?;
    log::info!("[tips] ウィンドウ作成成功: label={}", window.label());
    Ok(())
}

/// Tips of the Day ウィンドウを閉じる。存在しない場合は何もしない。
pub fn close_tips_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("tips") {
        let _ = window.destroy();
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
pub const NOTIFICATION_MAX_HEIGHT: f64 = 200.0;

/// 通知ポップアップの配置。HUDのモニターの右下、システムトレイの上に置く。
pub fn notification_placement(monitor: Option<&Monitor>, height: f64) -> Placement {
    let size = LogicalSize::new(NOTIFICATION_WIDTH, height);
    let Some(monitor) = monitor else {
        return Placement::on_monitor(None, size, LogicalPosition::new(0.0, 0.0));
    };
    let scale = monitor.scale_factor();
    let m_size = monitor.size().to_logical::<f64>(scale);
    let m_pos = monitor.position().to_logical::<f64>(scale);
    let margin = 16.0;
    Placement::on_monitor(
        Some(monitor),
        size,
        LogicalPosition::new(
            m_pos.x + m_size.width - NOTIFICATION_WIDTH - margin,
            m_pos.y + m_size.height - HUD_RIGHT_BOTTOM_RESERVED - height - margin,
        ),
    )
}

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

    let builder = WebviewWindowBuilder::new(
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

    build_placed(
        builder,
        notification_placement(monitors.get(monitor_index), NOTIFICATION_MAX_HEIGHT),
        false,
    )
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
        notification_placement(Some(monitor), clamped_height).apply(&win);
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

/// 記憶したポップアップの位置・サイズを捨て、開いている(再利用待ちを含む)ポップアップを
/// 表示先モニターの初期配置へ戻す。
pub fn reset_window_geometries(app: &AppHandle, state: &SharedState) -> Result<(), String> {
    let (open_windows, registry) = {
        let mut guard = state.lock();
        guard.saved_geometries.clear();
        let open_windows: Vec<(String, String, bool)> = guard
            .open_windows
            .iter()
            .map(|(label, w_state)| (label.clone(), w_state.window_type.clone(), w_state.hidden))
            .collect();
        let registry = guard.registry.clone();
        (open_windows, registry)
    };
    let target = popup_target(app, state);

    for (label, window_type, hidden) in open_windows {
        let Some(window) = app.get_webview_window(&label) else {
            continue;
        };
        let Some(definition) = registry.get(&window_type) else {
            continue;
        };
        // 再利用待ちの最大化ウィンドウは動かすと表示されてしまうので、開き直すときに置き直す(`open_window`)
        if hidden && definition.maximized {
            continue;
        }

        // 記憶を消した直後の状態＝「初めて開いたときの配置」なので、
        // 新規オープンと同じ計算(saved_geometry なし)をそのまま使う。
        let payload = OpenWindowPayload {
            window_type: window_type.clone(),
            entity: None,
            params: None,
        };
        let placement = compute_window_geometry(definition, &payload, None, &target);
        place_popup(&window, placement, definition.maximized);
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::{next_popup_monitor, resolve_popup_monitor, EDIT_MODE_WINDOW_TYPES};
    use crate::messages::MutexGroups;

    #[test]
    fn popup_monitor_follows_hud_unless_multi_display() {
        assert_eq!(resolve_popup_monitor(false, 1, 0, 3), 1);
        assert_eq!(resolve_popup_monitor(true, 0, 0, 1), 0, "モニター1台ならHUDと同じ");
    }

    #[test]
    fn multi_display_never_shares_the_hud_monitor() {
        assert_eq!(resolve_popup_monitor(true, 0, 2, 3), 2);
        assert_eq!(resolve_popup_monitor(true, 1, 1, 2), 0, "指定がHUDと同じなら次のモニター");
        assert_eq!(resolve_popup_monitor(true, 1, 5, 3), 2, "未接続の指定は次のモニター");
    }

    #[test]
    fn popup_monitor_switch_skips_the_hud_monitor() {
        assert_eq!(next_popup_monitor(0, 1, 3, 1), 2);
        assert_eq!(next_popup_monitor(0, 2, 3, 1), 1);
        assert_eq!(next_popup_monitor(0, 1, 3, -1), 2);
        assert_eq!(next_popup_monitor(0, 1, 2, 1), 1, "2台なら切り替え先が無い");
        assert_eq!(next_popup_monitor(0, 0, 1, 1), 0);
    }

    #[test]
    fn edit_mode_windows_are_mutually_exclusive() {
        let groups: MutexGroups = serde_json::from_str(include_str!(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/config/window-mutex-groups.json"
        )))
        .unwrap();
        for opening in EDIT_MODE_WINDOW_TYPES {
            let closes = groups.get(*opening).map(Vec::as_slice).unwrap_or_default();
            for other in EDIT_MODE_WINDOW_TYPES {
                assert!(
                    closes.iter().any(|t| t == other),
                    "window-mutex-groups.json: {opening} を開いても {other} が閉じない"
                );
            }
        }
    }
}
