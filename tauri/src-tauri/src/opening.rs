use crate::{ipc, monitor_frame, settings, state::SharedState, window};
use tauri::{AppHandle, Emitter, LogicalPosition, LogicalSize, Manager, Monitor};

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Phase {
    Setup,
    Splash { first_launch: bool },
    Scene(u8),
    Ending,
    Menu,
}

impl Phase {
    fn stage(self) -> Option<i64> {
        match self {
            Self::Scene(stage) => Some(i64::from(stage)),
            Self::Ending => Some(-1),
            _ => None,
        }
    }

    fn advance(self, from: u8, skip: bool) -> Option<Self> {
        if self != Self::Scene(from) { return None; }
        Some(if skip || from == 2 { Self::Ending } else { Self::Scene(from + 1) })
    }
}

pub fn placement(monitor: Option<&Monitor>) -> window::Placement {
    let (pos, size) = monitor_frame(monitor);
    let width = (size.width * 0.88).min(1080.0);
    let height = (size.height * 0.32).clamp(155.0, 230.0).min(size.height);
    window::Placement::on_monitor(monitor, LogicalSize::new(width, height),
        LogicalPosition::new(pos.x + (size.width - width) / 2.0, pos.y + size.height - height - 24.0))
}

pub fn show_menu(app: &AppHandle, state: &SharedState) {
    let guard = state.lock();
    if guard.opening != Phase::Menu || guard.is_loading || guard.last_session_id.is_some() { return; }
    drop(guard);
    if let Some(menu) = app.get_webview_window("topmenu") { let _ = menu.show(); }
}

fn send_stage(state: &SharedState, phase: Phase) -> Result<(), String> {
    if let Some(stage) = phase.stage() {
        ipc::send_ui_action(state, "opening".into(), None, "opening:stage".into(), serde_json::json!({ "stage": stage }))?;
    }
    Ok(())
}

/// 外部の Unity Editor はウィンドウ生成より先に接続できる。その場合も初回の通知を回収する。
pub fn windows_ready(app: &AppHandle, state: &SharedState, first_launch: bool, ui_only: bool) -> Result<(), String> {
    let connected = {
        let mut guard = state.lock();
        guard.opening = if ui_only { Phase::Menu } else { Phase::Splash { first_launch } };
        guard.graphic_connected
    };
    if ui_only { show_menu(app, state); }
    else if connected { graphic_ready(app, state)?; }
    Ok(())
}

/// スプラッシュ終了後の接続で開始する。Unity が再起動したら、進行中の場面を復元する。
pub fn graphic_ready(app: &AppHandle, state: &SharedState) -> Result<(), String> {
    let phase = {
        let mut guard = state.lock();
        if let Phase::Splash { first_launch } = guard.opening {
            guard.opening = if guard.is_loading || guard.last_session_id.is_some() || !first_launch {
                Phase::Menu
            } else {
                Phase::Scene(0)
            };
        }
        guard.opening
    };
    if phase == Phase::Menu { show_menu(app, state); }
    send_stage(state, phase)
}

/// Unity が絵を切り替えてから字幕やメニューを出す。遅れて届いた前の場面の応答は捨てる。
pub fn shown(app: &AppHandle, state: &SharedState, stage: i64) {
    {
        let mut guard = state.lock();
        if guard.opening.stage() != Some(stage) { return; }
        if guard.opening == Phase::Ending { guard.opening = Phase::Menu; }
    }
    let Some(caption) = app.get_webview_window("opening-caption") else { return };
    if stage == -1 {
        let _ = caption.hide();
        show_menu(app, state);
    } else {
        if stage == 0 {
            let mut saved = settings::load_settings(app);
            if !saved.first_launch_presented {
                saved.first_launch_presented = true;
                if let Err(error) = settings::save_settings(app, &saved) { log::error!("初回案内を保存できません: {error}"); }
            }
        }
        let _ = caption.emit("opening-stage", stage);
        let _ = caption.show();
    }
}

#[tauri::command]
pub fn get_opening_stage(state: tauri::State<'_, SharedState>) -> Option<i64> {
    state.lock().opening.stage()
}

#[tauri::command]
pub fn advance_opening(state: tauri::State<'_, SharedState>, stage: u8, skip: bool) -> Result<(), String> {
    let next = {
        let mut guard = state.lock();
        let Some(next) = guard.opening.advance(stage, skip) else { return Ok(()) };
        guard.opening = next;
        next
    };
    if let Err(error) = send_stage(state.inner(), next) {
        let mut guard = state.lock();
        if guard.opening == next { guard.opening = Phase::Scene(stage); }
        return Err(error);
    }
    Ok(())
}

pub fn hide_for_city(app: &AppHandle, state: &SharedState) {
    let mut guard = state.lock();
    if guard.opening.stage().is_some() { guard.opening = Phase::Menu; }
    drop(guard);
    for label in ["topmenu", "opening-caption"] {
        if let Some(window) = app.get_webview_window(label) { let _ = window.hide(); }
    }
}


#[cfg(test)]
mod tests {
    use super::Phase;

    #[test]
    fn pages_advance_once_and_wait_for_the_last_graphic_acknowledgement() {
        assert_eq!(Phase::Splash { first_launch: true }.advance(0, false), None);
        assert_eq!(Phase::Scene(0).advance(0, false), Some(Phase::Scene(1)));
        assert_eq!(Phase::Scene(1).advance(0, false), None);
        assert_eq!(Phase::Scene(1).advance(1, false), Some(Phase::Scene(2)));
        assert_eq!(Phase::Scene(2).advance(2, false), Some(Phase::Ending));
        assert_eq!(Phase::Scene(0).advance(0, true), Some(Phase::Ending));
        assert_eq!(Phase::Ending.stage(), Some(-1));
        assert_eq!(Phase::Menu.stage(), None);
    }
}
