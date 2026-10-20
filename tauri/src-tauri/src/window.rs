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

/// Window labels for the HUD (persistent in-game control
/// panels). Split into multiple windows because transparent
/// windows over Unity's 3D view intercept clicks even in visually
/// transparent areas; each window must cover only its panel's actual area.
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

/// Spacing between HUD panels, screen edges, and other windows.
pub const HUD_MARGIN: f64 = 8.0;

/// Icon panel width (vertical menu icons at the left edge). Must match button
/// size (46px) plus horizontal padding (6px×2) in `IconPanel.css`. Too narrow
/// clips buttons; too wide creates transparent areas that intercept Unity input.
pub const ICON_PANEL_WIDTH: f64 = 58.0;
/// Initial icon panel height. Actual height varies with icon
/// count; React reports and overrides it using `resize_icon_panel`.
pub const ICON_PANEL_DEFAULT_HEIGHT: f64 = 660.0;

/// Cockpit size (bottom center: tracking list, frequent tools, public service fulfillment, city name/date, budget meter,
/// alerts, and epoch execution). Width is initial only; React's `resize_cockpit` fits the measured width for the language and date.
pub const COCKPIT_WIDTH: f64 = 908.0;
pub const COCKPIT_HEIGHT: f64 = 76.0;

/// Initial system tray size (bottom right: settings/save/reload). Mode buttons change the width and the
/// unverified label adds height; React reports the measured size and overrides it with `resize_system_tray`.
pub const SYSTEM_TRAY_WIDTH: f64 = 176.0;
pub const SYSTEM_TRAY_HEIGHT: f64 = 40.0;

/// Display tray size (bottom left: layer visibility toggle icons). With
/// six fixed buttons, use these values without measured-size
/// notifications. Match `DisplayTray.css`: button 30px, gap 6px, horizontal
/// padding 8px, border 1px (30×6 + 6×5 + 8×2 + 1×2 = 228). Too narrow clips
/// buttons; too wide creates transparent areas that intercept Unity input.
pub const DISPLAY_TRAY_WIDTH: f64 = 228.0;
pub const DISPLAY_TRAY_HEIGHT: f64 = 40.0;

/// Height occupied by the bottom-center cockpit (from the screen bottom to its
/// top). Shift centered popups and bottom-center panels upward by this amount.
pub const HUD_BOTTOM_RESERVED: f64 = COCKPIT_HEIGHT + HUD_MARGIN;

/// Width occupied by the left icon panel (from the screen left to
/// the panel right). Shift left-docked windows right by this amount.
pub const HUD_LEFT_RESERVED: f64 = ICON_PANEL_WIDTH + HUD_MARGIN;

static NEXT_WINDOW_ID: AtomicU64 = AtomicU64::new(1);

/// Window placement: size in logical pixels, position in physical pixels (virtual screen coordinates).
///
/// For logical positions, tao converts using the current monitor's scale when moving, or the first
/// monitor containing the coordinates when creating. Mixed scale factors can place windows
/// incorrectly, even on another monitor. Route all monitor placement through this type using physical coordinates.
#[derive(Debug, Clone, Copy)]
pub struct Placement {
    pub size: LogicalSize<f64>,
    pub position: PhysicalPosition<i32>,
}

impl Placement {
    /// Build placement from logical coordinates computed with `monitor`'s scale (origin:
    /// `monitor.position().to_logical(scale)`). Use scale 1 when no monitor is available.
    pub fn on_monitor(monitor: Option<&Monitor>, size: LogicalSize<f64>, position: LogicalPosition<f64>) -> Self {
        let scale = monitor.map_or(1.0, |m| m.scale_factor());
        Self {
            size,
            position: position.to_physical(scale),
        }
    }

    /// Move and resize a window. Set size after moving so conversion uses the
    /// destination scale. Moving across scale factors may shift the position via
    /// Windows' suggested rectangle (WM_DPICHANGED), so reapply the position last.
    pub fn apply<R: Runtime>(&self, window: &WebviewWindow<R>) {
        let _ = window.set_position(self.position);
        let _ = window.set_size(self.size);
        let _ = window.set_position(self.position);
    }
}

/// Create a hidden window, apply `placement`, then show it if `visible`. Do not specify
/// position during creation: tao interprets it using a guessed monitor's scale (see `Placement`).
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

/// Determine the monitor index for popups.
///
/// Normally use the HUD monitor. In multi-display mode, use a separate monitor;
/// if the specified monitor matches the HUD or is disconnected, use the next
/// monitor after the HUD (keep popups separate when moving the HUD to that monitor
/// or disconnecting a monitor). With only one monitor, always use the HUD monitor.
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

/// Move the popup destination from `current` in `direction` (positive: next, negative: previous), skipping the HUD monitor.
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

/// Popup destination monitor index and whether it contains the HUD.
fn popup_monitor_of(state: &AppState, monitor_count: usize) -> (usize, bool) {
    let index = resolve_popup_monitor(
        state.multi_display,
        state.current_monitor_index,
        state.popup_monitor_index,
        monitor_count,
    );
    (index, index == state.current_monitor_index)
}

/// Destination for popups (windows driven by `window-registry.json`).
struct PopupTarget {
    monitor: Option<Monitor>,
    /// Whether the destination monitor contains the HUD. If so, avoid HUD-occupied areas.
    has_hud: bool,
    right_bottom_reserved: f64,
}

fn popup_target(app: &AppHandle, state: &SharedState) -> PopupTarget {
    let monitors = app.available_monitors().unwrap_or_default();
    let (index, has_hud) = popup_monitor_of(&state.lock(), monitors.len());
    PopupTarget {
        monitor: monitors.into_iter().nth(index),
        has_hud,
        right_bottom_reserved: hud_right_bottom_reserved(app),
    }
}

/// Change HUD/popup destination settings with `update`. If the popup destination
/// changes, use `reset_window_geometries` to move open popups there. Saved
/// positions/sizes belong to the old monitor and would move reopened windows back if retained.
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

/// HUD window title and initial size. Return defaults for
/// content-dependent dimensions; React's measured values override them.
fn hud_window_spec(label: &str) -> (&'static str, f64, f64) {
    match label {
        LABEL_ICON_PANEL => ("Genesis Icon Panel", ICON_PANEL_WIDTH, ICON_PANEL_DEFAULT_HEIGHT),
        LABEL_COCKPIT => ("Genesis Cockpit", COCKPIT_WIDTH, COCKPIT_HEIGHT),
        LABEL_DISPLAY_TRAY => ("Genesis Display Tray", DISPLAY_TRAY_WIDTH, DISPLAY_TRAY_HEIGHT),
        _ => ("Genesis System Tray", SYSTEM_TRAY_WIDTH, SYSTEM_TRAY_HEIGHT),
    }
}

/// Create an initially hidden HUD window. Show it
/// with `set_hud_visible` when the session starts.
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
    layout_hud_windows(app, monitors, monitor_index, None, None, None);
    Ok(())
}

/// Place HUD windows at fixed positions on the target monitor (left center, bottom center, bottom right, bottom left).
///
/// `icon_panel_height` explicitly sets icon panel height, `system_tray_size` the system tray size, and
/// `cockpit_width` the cockpit width. None preserves current
/// dimensions (already measured and reported by React) and only recalculates position.
pub fn layout_hud_windows(
    app: &AppHandle,
    monitors: &[Monitor],
    monitor_index: usize,
    icon_panel_height: Option<f64>,
    system_tray_size: Option<LogicalSize<f64>>,
    cockpit_width: Option<f64>,
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
    // Convert current dimensions to logical units using the window's current monitor scale (using the destination scale would be wrong when moving from another monitor).
    let current_size = |win: &WebviewWindow| {
        let win_scale = win.scale_factor().unwrap_or(scale);
        win.inner_size().ok().map(|s| s.to_logical::<f64>(win_scale))
    };

    // Icon panel: vertical at the left edge, centered vertically.
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

    // Cockpit: bottom center.
    if let Some(win) = app.get_webview_window(LABEL_COCKPIT) {
        let current = current_size(&win)
            .map(|s| s.width)
            .filter(|w| *w > 1.0)
            .unwrap_or(COCKPIT_WIDTH);
        let width = cockpit_width
            .unwrap_or(current)
            .clamp(1.0, m_size.width - HUD_MARGIN * 2.0);
        place(
            &win,
            width,
            COCKPIT_HEIGHT,
            m_pos.x + (m_size.width - width) / 2.0,
            m_pos.y + m_size.height - COCKPIT_HEIGHT - HUD_MARGIN,
        );
    }

    // System tray: bottom right. Resize from the bottom-right corner to fit the buttons and the label.
    if let Some(win) = app.get_webview_window(LABEL_SYSTEM_TRAY) {
        let current = current_size(&win)
            .filter(|s| s.width > 1.0 && s.height > 1.0)
            .unwrap_or(LogicalSize::new(SYSTEM_TRAY_WIDTH, SYSTEM_TRAY_HEIGHT));
        let size = system_tray_size.unwrap_or(current);
        let width = size.width.clamp(1.0, m_size.width - HUD_MARGIN * 2.0);
        let height = size.height.clamp(1.0, m_size.height - HUD_MARGIN * 2.0);
        place(
            &win,
            width,
            height,
            m_pos.x + m_size.width - width - HUD_MARGIN,
            m_pos.y + m_size.height - height - HUD_MARGIN,
        );
    }

    // Display tray: bottom left, with the same left-edge margin as the
    // icon panel. Left-docked popups reserve `HUD_BOTTOM_RESERVED` plus a
    // margin at the bottom, so they do not vertically overlap this tray.
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

/// Show or hide all HUD windows together, following
/// whether a session (a city being played) exists.
pub fn set_hud_visible(app: &AppHandle, visible: bool) {
    for label in HUD_WINDOW_LABELS {
        if let Some(win) = app.get_webview_window(label) {
            let _ = if visible { win.show() } else { win.hide() };
        }
    }
    // WebView2 can lose the creation-time always_on_top
    // flag across hide()→show(); reapply it whenever shown.
    if visible {
        reassert_topmost(app);
    }
}

/// Reassert topmost for all visible Tauri windows (three HUD panels, hint bar, popups, etc.).
///
/// Setting topmost once at creation is insufficient: when
/// Unity enters the foreground as fullscreen (borderless
///   covering the monitor), the shell can raise it above
/// topmost UI; WebView2 can also lose topmost across hide()→show().
///
/// Do not use `set_always_on_top(true)`. tao skips `SetWindowPos` if the flag is
/// unchanged, so windows already marked topmost cannot recover from being sent
/// behind Unity. Call `SetWindowPos(HWND_TOPMOST)` directly on each HWND instead.
///
/// Preserve relative window stacking. `HWND_TOPMOST` inserts at the
/// front of the topmost band, so apply in current Z-order from back to
/// front (preserving popup ordering and popups above HUD). This is
/// idempotent and cheap; call on each focus change or Unity recovery notification.
pub fn reassert_topmost(app: &AppHandle) {
    #[cfg(target_os = "windows")]
    {
        use std::collections::HashSet;
        use windows_sys::Win32::UI::WindowsAndMessaging::{
            GetTopWindow, GetWindow, IsWindowVisible, SetWindowPos, GW_HWNDNEXT, HWND_TOPMOST,
            SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE,
        };

        // HWND set for our visible windows (exclude hidden windows).
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

        // Walk desktop top-level windows from front to back, collecting only our windows.
        let mut ordered: Vec<isize> = Vec::with_capacity(targets.len());
        let mut cursor = unsafe { GetTopWindow(std::ptr::null_mut()) };
        while !cursor.is_null() {
            if targets.contains(&(cursor as isize)) {
                ordered.push(cursor as isize);
            }
            cursor = unsafe { GetWindow(cursor, GW_HWNDNEXT) };
        }

        // Collected front to back; applying in reverse order preserves relative stacking.
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

/// Hide/restore all Tauri windows with DWM cloaking without changing show/hide state.
///
/// Use during Unity fly/capture mode to show only the 3D view. Without changing
/// visibility state, normal opening/closing continues while cloaked; restoring reveals
/// only windows currently open (windows created after cloaking are excluded). Do not use
/// show() even for restoration, since activation would steal keyboard input from Unity.
#[cfg(target_os = "windows")]
pub fn set_ui_cloaked(app: &AppHandle, cloaked: bool) {
    use windows_sys::Win32::Graphics::Dwm::{DwmSetWindowAttribute, DWMWA_CLOAK};

    let value: i32 = cloaked.into();
    for win in app.webview_windows().values() {
        if let Ok(hwnd) = win.hwnd() {
            unsafe {
                DwmSetWindowAttribute(
                    hwnd.0 as *mut core::ffi::c_void,
                    DWMWA_CLOAK as u32,
                    &value as *const i32 as *const core::ffi::c_void,
                    std::mem::size_of::<i32>() as u32,
                );
            }
        }
    }
    if !cloaked {
        reassert_topmost(app);
    }
}

/// Match icon panel height to React's measurement. Rust
/// cannot know how scenario mode changes icon count; React
/// reports each content height change to prevent transparent margins.
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
    layout_hud_windows(app, &monitors, monitor_index, Some(height), None, None);
}

/// Height occupied by the bottom-right system tray; right side panels and notifications avoid this area.
/// Use the tray's current height, which grows with the unverified label. The centered cockpit does not overlap the right panel.
pub fn hud_right_bottom_reserved(app: &AppHandle) -> f64 {
    let height = app
        .get_webview_window(LABEL_SYSTEM_TRAY)
        .and_then(|win| {
            let scale = win.scale_factor().ok()?;
            Some(win.inner_size().ok()?.to_logical::<f64>(scale).height)
        })
        .filter(|h| *h > 1.0)
        .unwrap_or(SYSTEM_TRAY_HEIGHT);
    height + HUD_MARGIN
}

/// Update system tray size to React's measurement and realign at the bottom right.
pub fn resize_system_tray(app: &AppHandle, state: &SharedState, width: f64, height: f64) {
    let monitor_index = {
        let guard = state.lock();
        guard.current_monitor_index
    };
    let monitors: Vec<_> = app
        .available_monitors()
        .unwrap_or_default()
        .into_iter()
        .collect();
    layout_hud_windows(app, &monitors, monitor_index, None, Some(LogicalSize::new(width, height)), None);
}

/// Fit cockpit width to measured content and realign at the bottom center.
pub fn resize_cockpit(app: &AppHandle, state: &SharedState, width: f64) {
    let monitor_index = state.lock().current_monitor_index;
    let monitors = app.available_monitors().unwrap_or_default();
    layout_hud_windows(app, &monitors, monitor_index, None, None, Some(width));
}

/// Whether the layout is docked to a screen edge. Docked windows
/// do not remember positions; layout calculation repositions them.
fn is_docked_layout(layout: &WindowLayout) -> bool {
    !matches!(layout, WindowLayout::Default)
}

/// Remember the window's current size (and position when needed) in state.
///
/// Retained only for the current process, never persisted.
/// Update position only when `record_position == true` and
/// not docked. Always update size. Exclude maximized windows
/// because their geometry is not meaningful for restoration.
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

    // Do not remember maximized geometry.
    if win.is_maximized().unwrap_or(false) {
        return;
    }

    let scale = win.scale_factor().unwrap_or(1.0);
    // Remember size using the inner client area. Restoration uses inner_size /
    // set_size, so remembering outer size would add frame thickness on every reopening
    // (even undecorated resizable windows have invisible resize borders). Positions
    // need no conversion because both outer_position and set_position use the outer frame.
    let Some(size) = win.inner_size().map(|s| s.to_logical::<f64>(scale)).ok() else {
        return;
    };

    // For docked layouts, update size only. Remember positions in physical coordinates (`WindowGeometry`).
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

/// Calculate popup placement (size and position) by layout type.
///
/// Shared logic for new window creation, hidden-window reuse, and destination
/// changes. Centralized layout calculation applies identical positions/sizes on every path.
///
/// When the destination monitor contains the HUD, docked layouts avoid persistent HUD areas (left icon
/// panel, bottom-center cockpit, bottom-right system tray). Shift left docks by `HUD_LEFT_RESERVED` and
/// subtract reserved bottom height. In multi-display mode on a monitor without HUD, use the full screen edges.
///
/// Docked layouts (LeftPanel/RightPanel/BottomCenter/LeftTop/RightTop)
///   derive position from monitor data; saved_geometry overrides size only.
/// Free layout (Default) restores saved_geometry position and size as-is.
///   Otherwise, use width_ratio/height_ratio or definition.width/height for size
///   and place at payload.params.preferred_position or the destination monitor center.
///
/// If no monitor is available, fall back to the definition's size at (0,0).
fn compute_window_geometry(
    definition: &WindowDefinition,
    payload: &OpenWindowPayload,
    saved_geometry: Option<WindowGeometry>,
    target: &PopupTarget,
) -> Placement {
    let monitor = target.monitor.as_ref();
    let (left_reserved, bottom_reserved, right_bottom_reserved) = if target.has_hud {
        (HUD_LEFT_RESERVED, HUD_BOTTOM_RESERVED, target.right_bottom_reserved)
    } else {
        (0.0, 0.0, 0.0)
    };

    if definition.layout == WindowLayout::LeftPanel
        || definition.layout == WindowLayout::LeftPanelNarrow
    {
        // Left side panel: fill the height beside the icon panel.
        if let Some(monitor) = monitor {
            let scale = monitor.scale_factor();
            let m_size = monitor.size().to_logical::<f64>(scale);
            let m_pos = monitor.position().to_logical::<f64>(scale);

            let margin = HUD_MARGIN;
            // Use the layout's default width ratio unless the definition supplies
            // width_ratio; content density can require different widths even within left-docked layouts.
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

            // Prefer remembered size when available (position follows the layout).
            let panel_width = saved_geometry.map(|g| g.width).unwrap_or(computed_width);
            let panel_height = saved_geometry.map(|g| g.height).unwrap_or(computed_height);

            return Placement::on_monitor(
                Some(monitor),
                LogicalSize::new(panel_width, panel_height),
                LogicalPosition::new(m_pos.x + left_reserved + margin, m_pos.y + margin),
            );
        }
    } else if definition.layout == WindowLayout::RightPanel {
        // Right side panel: fill the height at the monitor's right edge.
        if let Some(monitor) = monitor {
            let scale = monitor.scale_factor();
            let m_size = monitor.size().to_logical::<f64>(scale);
            let m_pos = monitor.position().to_logical::<f64>(scale);

            let margin = HUD_MARGIN;
            let computed_width = (m_size.width * 0.20).round();
            let computed_height = m_size.height - right_bottom_reserved - margin * 2.0;

            // Prefer remembered size when available. Right-edge placement depends on width.
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
        // Bottom-center panel: use the specified monitor width ratio (55%
        // when unset) and registry height. Place directly above the cockpit.
        if let Some(monitor) = monitor {
            let scale = monitor.scale_factor();
            let m_size = monitor.size().to_logical::<f64>(scale);
            let m_pos = monitor.position().to_logical::<f64>(scale);

            let margin = HUD_MARGIN;
            let width_ratio = definition.width_ratio.unwrap_or(0.55);
            let computed_width = (m_size.width * width_ratio).round();
            let computed_height = definition.height as f64;

            // Prefer remembered size when available (position follows bottom-center layout).
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
        // Top-left placement: beside the icon panel at the screen top, using registry size.
        if let Some(monitor) = monitor {
            let scale = monitor.scale_factor();
            let m_pos = monitor.position().to_logical::<f64>(scale);

            let margin = HUD_MARGIN;
            // Prefer remembered size when available (position follows top-left layout).
            let w = saved_geometry.map(|g| g.width).unwrap_or(definition.width as f64);
            let h = saved_geometry.map(|g| g.height).unwrap_or(definition.height as f64);

            return Placement::on_monitor(
                Some(monitor),
                LogicalSize::new(w, h),
                LogicalPosition::new(m_pos.x + left_reserved + margin, m_pos.y + margin),
            );
        }
    } else if definition.layout == WindowLayout::RightTop {
        // Top-right placement: use registry size with margins from the
        // top/right edges. Intended for windows such as the 2D path shape editor
        // used alongside the left placement panel, keeping the 3D view center clear.
        if let Some(monitor) = monitor {
            let scale = monitor.scale_factor();
            let m_pos = monitor.position().to_logical::<f64>(scale);
            let m_size = monitor.size().to_logical::<f64>(scale);

            let margin = HUD_MARGIN;
            // Default size: use width_ratio of monitor width if specified; without height_ratio, make a
            // square (clamped to work area height excluding the cockpit). Without ratios, use registry dimensions.
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
        // Free placement: restore remembered position and size as-is when available.
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

        // Use an explicit parent-specified position if supplied; otherwise center on the destination monitor.
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

    // Fallback when no monitor is available.
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

    // Priority 1: reuse a visible or hidden window with the same instance_key. Show
    // hidden windows and remove them from LRU. Update context (entity/params) and focus.
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
            // Remove from LRU first so exclusive closing below cannot destroy it for exceeding the limit or repurpose it.
            if was_hidden {
                guard.hidden_lru.retain(|l| l != &label);
            }
            was_hidden
        };
        // Showing a hidden window is a new opening, so apply exclusive groups. This window
        // is still hidden and will not close itself even if its group includes its own type.
        if was_hidden {
            apply_mutex_rules(&app, &state, &payload.window_type)?;
        }
        {
            let mut guard = state.lock();
            // Restore from hidden (awaiting reuse) state. Visible windows
            // already have hidden=false. Reopening a closed window advances
            // open_seq to request a React panel remount (state reset). Do not
            // increment for reopening an already visible window (focus only).
            if was_hidden {
                if let Some(w) = guard.open_windows.get_mut(&label) {
                    w.hidden = false;
                    w.open_seq += 1;
                }
            }
            // Overwrite entity/params with the latest request (the reference point may change despite the same instance_key).
            if let Some(window) = guard.open_windows.get_mut(&label) {
                window.entity = payload.entity.clone();
                window.params =
                    serde_json::to_value(payload.params.clone()).unwrap_or(Value::Null);
            }
        }

        if let Some(window) = app.get_webview_window(&label) {
            // Hidden maximized windows cannot be repositioned on destination changes (`reset_window_geometries`), so move them to the destination here.
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

    // Fetch layout inputs first (shared by creation and reuse).
    let saved_geometry = {
        let guard = state.lock();
        guard.saved_geometries.get(&payload.window_type).copied()
    };
    let target = popup_target(&app, &state);

    // Priority 2: repurpose a hidden popup awaiting reuse. Tauri cannot rename
    // window labels, so retain the label and overwrite
    // window_type/entity/params/instance_key/title, then reset size/position to the new
    // definition. React receives the new context through window-context-updated and rerenders.
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
                // Repurposing also counts as reopening, so advance the
                // generation. Although window_type changes the React key,
                // always increment because reuse for the same type is also possible.
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

    // Priority 3: no reusable window exists; create a new one (legacy behavior).
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

    // Maximization uses the current monitor, so move to the destination before maximizing.
    let window = build_placed(builder, placement, false)?;
    if definition.maximized {
        let _ = window.maximize();
    }
    window.show().map_err(|err| err.to_string())?;
    let _ = window.set_focus();

    Ok(())
}

/// Place a popup. Move maximized windows to their destination before maximizing
/// (maximization uses the current monitor). Restoring a maximized window shows it
/// even if hidden, so do not use this for maximized windows that must remain hidden.
fn place_popup<R: Runtime>(window: &WebviewWindow<R>, placement: Placement, maximized: bool) {
    let _ = window.unmaximize();
    placement.apply(window);
    if maximized {
        let _ = window.maximize();
    }
}

/// Whether the window is on `monitor`. Return true if either is unavailable to avoid repositioning.
fn is_on_monitor<R: Runtime>(window: &WebviewWindow<R>, monitor: Option<&Monitor>) -> bool {
    match (window.current_monitor().ok().flatten(), monitor) {
        (Some(current), Some(target)) => current.position() == target.position(),
        _ => true,
    }
}

/// Windows that restore Unity's normal mode when closed (editing
/// windows). Include both Unity editing states (`STATE_*`) and
/// temporary 3D display replacements (public transport
/// list/creation). Restoring the display requires Unity's
/// `STATE_NORMAL`; omission leaves the replacement visible after closing.
/// Hiding a window does not unmount React, so cleanup cannot be left to the panel.
///
/// Unity has only one editing state, so these types must not coexist. Add mutual exclusions
/// against every listed type in `window-mutex-groups.json` when adding a type here.
/// Otherwise an earlier panel remains open with its state overwritten; the tests below check this.
///
/// Exclude district editing (STATE_EDIT_ZONE) and bulldozer (STATE_REMOVE_OBJECTS): they are HUD toggles without windows,
/// and the HUD sends STATE_NORMAL when toggled off. Also exclude zone_detail, which only displays data without changing state.
const EDIT_MODE_WINDOW_TYPES: &[&str] = &[
    "parcel_creation",
    "path_build",
    "building_construction",
    "resource_edit",
    "prop_placement",
    "transit_management",
    "transit_creation",
    // Building selection overrides the input preset (MODE_PICK_BUILDING). Restore it even when closed while awaiting a selection.
    "agent_creation",
    "grading",
];

/// On session switches, prevent stale HTTP responses from reopening Tips or results.
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
    // Fetch the window type first to make the intent explicit (it remains accessible after marking hidden).
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

    // For idempotency, do nothing if the entry is missing or
    // already hidden awaiting reuse. Subsequent calls return early here.
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

    // Return to STATE_NORMAL when an editing window closes. Exclusive closing
    // caused by opening another editing window sends this before the new
    // window appears, so it does not overwrite the new panel's editing state on
    // mount. Each UI panel switches to planned view when its construction
    // confirmation button is pressed; do not send view:change_view unconditionally here.
    if let Some(ref wt) = window_type {
        if EDIT_MODE_WINDOW_TYPES.contains(&wt.as_str()) {
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "world:change_state".to_string(),
                json!({ "state": "STATE_NORMAL" }),
            );
            if wt == "path_build" {
                // Ensure the grid is hidden when the path construction panel closes.
                // The window only becomes hidden awaiting reuse, so React unmount
                // cleanup does not run; Rust's window close handler must explicitly send off.
                let _ = ipc::send_ui_action(
                    state,
                    wt.clone(),
                    None,
                    "view:set_grid".to_string(),
                    json!({ "mode": "off" }),
                );
                // Clear placement interference highlights (source="path_build") here for the same reason.
                let _ = ipc::send_ui_action(
                    state,
                    wt.clone(),
                    None,
                    "world:clear_highlights".to_string(),
                    json!({ "source": "path_build" }),
                );
                // When the path construction panel owning the placement session closes, also close its
                // dependent 2D path shape editor. Closing the 2D panel alone does not end the placement session.
                close_windows_by_type(app, state, "path_shape_editor");
            }
            if wt == "building_construction" {
                // When the building construction panel closes, clear direct
                // placement interference highlights (source="building_construction").
                // The window only hides awaiting reuse and React unmount cleanup does
                // not run, so the close handler sends explicitly, as for path_build.
                let _ = ipc::send_ui_action(
                    state,
                    wt.clone(),
                    None,
                    "world:clear_highlights".to_string(),
                    json!({ "source": "building_construction" }),
                );
            }
        } else if wt == "agent_detail" {
            // When the agent detail panel closes, clear terminal highlights. Unity
            // returns to normal only in connector editing mode (STATE_EDIT_CONNECTOR).
            // Detail panels can coexist with editing windows; sending STATE_NORMAL would
            // also terminate a later building construction or other state, as with path_detail.
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "edit_connector:hide".to_string(),
                json!({}),
            );
        } else if wt == "zone_merge" {
            // If district merge confirmation closes without confirmation
            // or cancellation, cancel Unity's pending confirmation. The
            // window only hides awaiting reuse and React does not unmount;
            // leaving cleanup to the panel would leave the merge pending.
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "edit_zone:finish_merge".to_string(),
                json!({ "merged": 0 }),
            );
        } else if wt == "parcel_detail" {
            // When the parcel detail panel closes, clear road access display and
            // expansion previews, and release the parcel outline display preset.
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "parcel:hide_front_path".to_string(),
                json!({}),
            );
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "edit_parcel:update_preview".to_string(),
                json!({ "mode": "merge", "preview_polygons": [], "original_polygon": null }),
            );
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "view:restore_preset".to_string(),
                json!({ "preset": "parcel_detail" }),
            );
        } else if wt == "road_detail" {
            // When a road detail panel closes, clear its 3D highlight band and end
            // section editing for this road. The window only hides awaiting reuse and
            // React does not unmount, so send explicitly here, as with path_detail.
            // Multiple road detail windows may coexist; include the closed road's
            // road_id. Unity ignores edit_road:cancel when not editing sections or when
            // editing a different road, avoiding cancellation of edits in another detail panel.
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
            // Also notify the closing panel to clear its section editing display and hint bar
            // guidance (it remains mounted while hidden and has no other way to detect closing).
            let _ = app.emit_to(label, "road:panel_closing", json!({}));
        } else if wt == "path_detail" {
            // When the path detail panel closes, clear interference highlights for
            // paths awaiting construction. The window only hides awaiting reuse and
            // React unmount cleanup does not run, so the close handler sends explicitly.
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "world:clear_highlights".to_string(),
                json!({ "source": "path_detail" }),
            );
            // End path merge/split target selection and return to normal if active. Leaving
            // this to the panel would keep selection mode active and block 3D input because
            // unmount cleanup does not run. Unity does nothing if selection mode is inactive.
            let _ = ipc::send_ui_action(
                state,
                wt.clone(),
                None,
                "edit_path:cancel_topology_edit".to_string(),
                json!({}),
            );
        }
    }

    // Hide the window and mark it for reuse. Users often reopen the
    // same window type immediately after closing, so retain it to
    // avoid WebView2 startup cost (about 0.4 seconds). If hidden_lru
    // exceeds MAX_HIDDEN_WINDOWS, actually destroy the oldest windows first.
    let labels_to_destroy: Vec<String> = {
        let mut guard = state.lock();
        let mut to_destroy = Vec::new();
        // While over the limit, select the oldest window (LRU front) for destruction.
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

    // Actually close windows evicted by the LRU limit.
    // Their side effects (such as sending STATE_NORMAL)
    // already ran when first hidden; simply destroy them here.
    for old_label in labels_to_destroy {
        if let Some(old_window) = app.get_webview_window(&old_label) {
            let _ = old_window.close();
        }
        let mut guard = state.lock();
        guard.open_windows.remove(&old_label);
    }

    // Hide the current window without destroying it.
    if let Some(window) = app.get_webview_window(label) {
        let _ = window.hide();
    }
    // Explicitly invalidate guide display conditions because reused WebViews do not unmount.
    let _ = app.emit_to("tutorial", "tutorial:window_visibility", serde_json::json!({
        "label": label, "window": window_type, "entity": entity_id, "visible": false
    }));

    Ok(())
}

/// Close all visible windows of the specified type (hide for reuse). Do nothing if none are
/// open. Used to close dependent windows from another window's action, such as a confirmation button.
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
    // Editing windows overwrite Unity's state, so also exclude persistent HUD modes (bulldozer/district editing).
    // Send here to clear them regardless of the opening entry point (HUD icons, detail panel Edit buttons,
    // etc.). The source of truth is `hud:mode_changed`, subscribed to by all HUD windows (`hud/hudMode.ts` in the UI).
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
    // Skip unmanaged windows: main, HUD panels, topmenu, notification, hintbar, tutorial,
    // parameter-setup, and tips. notification/hintbar/tutorial are dedicated windows
    // outside open_windows; removing them would emit an incorrect popup-count-changed event.
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
        // Also remove from LRU if present (for example, during destruction due to the LRU limit).
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

/// Close all currently open popup windows.
///
/// Clear remaining modals/panels when switching the
/// taskbar and top menu at game end. Call
/// close_window_by_label for each open_windows entry so editing
/// cleanup, such as returning to STATE_NORMAL, runs correctly.
pub fn close_all_popup_windows(app: &AppHandle, state: &SharedState) {
    let labels: Vec<String> = {
        let guard = state.lock();
        guard.open_windows.keys().cloned().collect()
    };
    for label in labels {
        let _ = close_window_by_label(app, state, &label);
    }
}

/// Connection information for requests sent directly from WebViews to the backend.
pub fn get_connection_status(state: &SharedState) -> Value {
    let guard = state.lock();
    json!({
        "api_base_url": guard.api_base_url,
        "backend_connected": guard.backend_health.connected,
        // Exclude hidden windows awaiting reuse from the visible popup count.
        "open_window_count": guard.open_windows.values().filter(|w| !w.hidden).count(),
    })
}

/// Open parameter setup. edit_mode=false: initial setup
/// for a new free-mode game. edit_mode=true: re-edit during
/// scenario authoring (do not send game:start on
/// completion). Store the mode in state for React's startup
/// query. If already open, only update the mode and bring it forward.
///
/// Never call from the main thread (`builder.build()` deadlocks waiting for the
/// event loop). Use `async fn` for Tauri commands, or call from another thread.
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

/// Placement centered on the monitor. If no monitor is available, use the virtual screen origin.
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

/// Close parameter setup. Do nothing if absent.
pub fn close_parameter_setup_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("parameter-setup") {
        let _ = window.close();
    }
}

/// Show the Tips of the Day window. Do nothing if already
/// visible. Display a small 580x380 window at screen center.
pub fn open_tips_window(app: &AppHandle, state: &SharedState) -> Result<(), String> {
    let monitor_index = {
        let guard = state.lock();
        guard.current_monitor_index
    };
    open_tips_window_on_monitor(app, monitor_index)
}

/// Show Tips of the Day on the specified monitor index.
/// Create windows from an async command or worker thread.
pub fn open_tips_window_on_monitor(app: &AppHandle, monitor_index: usize) -> Result<(), String> {
    open_tips_window_with_mode(app, monitor_index, false)
}

/// Initial loading waits for the Start button; for epoch waits, the caller closes it on completion.
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

/// Close Tips of the Day. Do nothing if absent.
pub fn close_tips_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("tips") {
        let _ = window.destroy();
    }
}

/// Notification popup width in logical pixels. Leave a
/// fixed right-edge margin to avoid overlapping the system tray.
const NOTIFICATION_WIDTH: f64 = 320.0;

/// Minimum notification popup height in logical
/// pixels. Ensure enough height for the icon and body.
const NOTIFICATION_MIN_HEIGHT: f64 = 56.0;

/// Maximum notification popup height in logical pixels. The
/// frontend scrolls or wraps long text to stay within this height.
pub const NOTIFICATION_MAX_HEIGHT: f64 = 200.0;

/// Notification popup placement: bottom right of the HUD monitor, above the system tray.
pub fn notification_placement(monitor: Option<&Monitor>, height: f64, right_bottom_reserved: f64) -> Placement {
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
            m_pos.y + m_size.height - right_bottom_reserved - height - margin,
        ),
    )
}

/// Enqueue a notification popup and show it immediately if possible.
///
/// If a notification is already visible, only enqueue; show the next automatically after the current one ends.
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

/// Show the first queued notification.
///
/// Create the window if missing; otherwise send `notification-show` to
/// replace its content. Close after five seconds unless a duration is specified.
fn process_notification_queue(app: &AppHandle, state: &SharedState) -> Result<(), String> {
    let (payload, generation) = {
        let mut guard = state.lock();
        if guard.notification_showing {
            return Ok(());
        }
        let payload = guard.notification_queue.pop_front();
        if payload.is_some() {
            guard.notification_showing = true;
            guard.notification_generation += 1;
        }
        (payload, guard.notification_generation)
    };

    let Some(payload) = payload else {
        return Ok(());
    };

    let win = match app.get_webview_window("notification") {
        Some(win) => win,
        None => create_notification_window(app, state)?,
    };

    // Show only after resizing to measured body height to avoid displaying a transparent maximum-height region.
    win.emit("notification-show", &payload)
        .map_err(|e| e.to_string())?;

    let duration_ms = payload.duration_ms.unwrap_or(5000);
    let app = app.clone();
    let state = state.clone();
    std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(duration_ms));
        let _ = dismiss_notification_generation(&app, &state, Some(generation));
    });

    Ok(())
}

/// Create a new notification window.
///
/// Place at the monitor's bottom right, above the taskbar. Create
/// initially hidden at maximum height, then adjust to frontend resize requests.
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
        notification_placement(monitors.get(monitor_index), NOTIFICATION_MAX_HEIGHT, hud_right_bottom_reserved(app)),
        false,
    )
}

/// Hide the notification window and show the next queued notification if any.
///
/// Called when the user presses the close button or by Rust after a timeout.
pub fn dismiss_notification(app: &AppHandle, state: &SharedState) -> Result<(), String> {
    dismiss_notification_generation(app, state, None)
}

fn dismiss_notification_generation(app: &AppHandle, state: &SharedState, generation: Option<u64>) -> Result<(), String> {
    {
        let mut guard = state.lock();
        if generation.is_some_and(|generation| generation != guard.notification_generation)
            || !guard.notification_showing
        {
            return Ok(());
        }
        guard.notification_showing = false;
        guard.notification_generation += 1;
    }

    if let Some(win) = app.get_webview_window("notification") {
        let _ = win.hide();
    }

    let has_next = state.lock().notification_queue.len() > 0;
    if has_next {
        let app = app.clone();
        let state = state.clone();
        std::thread::spawn(move || {
            // Allow a short delay for the slide-out animation.
            std::thread::sleep(Duration::from_millis(300));
            let _ = process_notification_queue(&app, &state);
        });
    }

    Ok(())
}

/// Receive notification content height from the frontend and adjust window size and position.
///
/// After measuring actual rendered height, the frontend calls this
/// command to fit the window to the notification's variable text length.
pub fn resize_notification_window(app: &AppHandle, state: &SharedState, height: f64) -> Result<(), String> {
    let monitor_index = {
        let guard = state.lock();
        if !guard.notification_showing {
            return Ok(());
        }
        guard.current_monitor_index
    };
    let win = app
        .get_webview_window("notification")
        .ok_or("notification window not found")?;

    let monitors: Vec<_> = app
        .available_monitors()
        .unwrap_or_default()
        .into_iter()
        .collect();

    let clamped_height = height.clamp(NOTIFICATION_MIN_HEIGHT, NOTIFICATION_MAX_HEIGHT);

    if let Some(monitor) = monitors.get(monitor_index) {
        notification_placement(Some(monitor), clamped_height, hud_right_bottom_reserved(app)).apply(&win);
    } else {
        win.set_size(LogicalSize::new(NOTIFICATION_WIDTH, clamped_height))
            .map_err(|e| e.to_string())?;
    }

    win.show().map_err(|e| e.to_string())
}

/// Discard remembered popup positions/sizes and restore open popups (including
/// hidden windows awaiting reuse) to initial placement on their destination monitor.
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
        // Moving a hidden maximized window would show it; reposition when reopening instead (`open_window`).
        if hidden && definition.maximized {
            continue;
        }

        // After clearing memory, placement is equivalent to first
        // opening; reuse the same new-window calculation without saved_geometry.
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
