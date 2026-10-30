use crate::ipc::MmfChannel;
use crate::messages::{MutexGroups, NotificationPayload, WindowContextDto, WindowRegistry};
use serde_json::Value;
use std::collections::{HashMap, VecDeque};
use std::sync::atomic::AtomicBool;
use std::sync::{Arc, Mutex};

/// Window position and size: size in logical pixels, position in physical
/// pixels (virtual screen coordinates). Physical positions avoid ambiguity
/// between monitors with different scale factors. Remember only for the current
/// process; do not persist. Docked windows ignore position (x, y) and use only size.
#[derive(Debug, Clone, Copy, Default)]
pub struct WindowGeometry {
    pub x: i32,
    pub y: i32,
    pub width: f64,
    pub height: f64,
}

/// Maximum retained hidden windows (closed, awaiting reuse). Hide
/// closed popups instead of immediately destroying them to avoid
/// WebView2 startup cost (about 0.4 seconds) on reopening. Limit
/// memory usage by actually destroying the oldest excess windows (LRU).
pub const MAX_HIDDEN_WINDOWS: usize = 10;

#[derive(Clone)]
pub struct SharedState(pub Arc<Mutex<AppState>>);

pub struct AppState {
    pub registry: WindowRegistry,
    pub mutex_groups: MutexGroups,
    pub open_windows: HashMap<String, OpenWindowState>,
    /// Remembered position/size per window type for the current
    /// process. Keyed by `window_type`. Docked windows store size
    /// only; freely placed windows store both position and size.
    pub saved_geometries: HashMap<String, WindowGeometry>,
    /// MMF writer for UI→Graphic.
    pub writer_channel: Option<MmfChannel>,
    pub api_base_url: String,
    /// Core version information and compatibility results. All WebViews, startup paths, and session monitoring share the same startup result.
    pub core_info: Arc<tokio::sync::OnceCell<serde_json::Value>>,
    /// Index of the monitor displaying the HUD (plus hint bar, tutorial, top menu, etc.).
    pub current_monitor_index: usize,
    /// Multi-display mode: show popups on a monitor separate from the HUD.
    pub multi_display: bool,
    /// Popup monitor index in multi-display mode. `window::resolve_popup_monitor`
    /// selects the actual destination, substituting if it matches the HUD or is disconnected.
    pub popup_monitor_index: usize,
    /// In-game time information received from the parent process.
    pub game_time: Option<Value>,
    /// Core/graphics child processes and application-wide shutdown state.
    pub processes: crate::process::Processes,
    /// Reader thread shutdown flag.
    pub shutdown: Arc<AtomicBool>,
    /// Last fetched session information.
    pub session_state: Option<serde_json::Value>,
    pub backend_health: crate::backend_health::BackendHealth,
    /// Last observed city identifier (session_id from GET /api/v1/sessions; None before a game starts).
    pub last_session_id: Option<String>,
    /// Loading flag (game startup in progress).
    pub is_loading: bool,
    pub loading_id: u64,
    pub backend_load_complete: bool,
    pub graphic_load_ready: bool,
    pub needs_parameter_setup: bool,
    /// Suppress the HUD during initial setup; enable normal interaction after setup completes.
    pub awaiting_parameter_setup: bool,
    pub loading_started: bool,
    /// Started tutorial ID (None for normal play).
    pub tutorial_id: Option<String>,
    /// Information window content to open once the new city appears (`params.extra` of info_dialog).
    pub intro_dialog: Option<Value>,
    /// Whether parameter setup opened in in-game editing mode. false
    /// (default): initial setup for a new free-mode game; show HUD on
    /// completion. true: reopened from Taskbar during scenario authoring; only close
    ///   on completion, without sending game:start, because the game is already running.
    pub parameter_setup_edit_mode: bool,
    /// Notification popup queue. Show the next notification in
    /// order after the user closes one or it hides automatically.
    pub notification_queue: VecDeque<NotificationPayload>,
    /// Whether a notification popup is currently visible. While
    /// true, enqueue new notifications without displaying them.
    pub notification_showing: bool,
    /// Generation that prevents a manually closed notification's timer from closing the next notification.
    pub notification_generation: u64,
    /// Labels of hidden popups (closed, awaiting reuse), with the most recently
    /// hidden at the end. open_window searches these for reuse candidates; when
    /// MAX_HIDDEN_WINDOWS is exceeded, actually destroy from the front (oldest).
    pub hidden_lru: VecDeque<String>,
    /// Whether system:graphic_ready has ever been received from Graphic
    /// (Unity). Subsequent receipts indicate automatic restart after a
    /// crash and require clearing popups tied to the previous session.
    /// Distinguish the first receipt (normal startup) to preserve windows
    /// such as load screens opened by users in the UI before Unity started.
    pub graphic_ready_seen: bool,
    /// Whether Graphic (Unity) is currently connected. Set by
    /// system:graphic_ready; cleared by system:graphic_stopped (normal exit or
    /// stopping Editor Play). Crashes do not clear it, but the restarted Unity's
    /// graphic_ready reconciles it. Used to skip rendering waits in development without
    /// Unity (graphic_ready_seen instead detects reconnection and never resets once set).
    pub graphic_connected: bool,
    pub graphic_contract_error: Option<String>,
    pub graphic_version: Option<String>,
    /// Presentation from epoch start until the results screen closes. Distinguishes completion notifications from stale windows.
    pub epoch_presentation_id: Option<String>,
}

#[derive(Clone)]
pub struct OpenWindowState {
    pub label: String,
    pub instance_key: String,
    pub window_type: String,
    pub title: String,
    pub entity: Option<crate::messages::EntityRef>,
    pub params: Value,
    /// Whether the user closed this window, leaving it hidden and
    /// awaiting reuse. While true, open_window treats it as a reuse candidate.
    pub hidden: bool,
    /// Opening generation for this window (WebView), incremented
    /// whenever a hidden window is shown again or repurposed. React
    /// receives it as WindowContext.open_seq and includes it in the
    /// panel remount key to guarantee reopening resets state (reusing
    /// WebView2 must not retain the panel's previous useState values).
    pub open_seq: u64,
}

impl SharedState {
    pub fn new(registry: WindowRegistry, mutex_groups: MutexGroups, launch: crate::process::Launch) -> Self {
        Self(Arc::new(Mutex::new(AppState {
            registry,
            mutex_groups,
            open_windows: HashMap::new(),
            saved_geometries: HashMap::new(),
            writer_channel: None,
            api_base_url: launch.api_base_url.clone(),
            core_info: Arc::new(tokio::sync::OnceCell::new()),
            current_monitor_index: 0,
            multi_display: false,
            popup_monitor_index: 0,
            game_time: None,
            processes: crate::process::Processes::new(launch),
            shutdown: Arc::new(AtomicBool::new(false)),
            session_state: None,
            backend_health: Default::default(),
            last_session_id: None,
            is_loading: false,
            loading_id: 0,
            backend_load_complete: false,
            graphic_load_ready: false,
            needs_parameter_setup: false,
            awaiting_parameter_setup: false,
            loading_started: false,
            tutorial_id: None,
            intro_dialog: None,
            parameter_setup_edit_mode: false,
            notification_queue: VecDeque::new(),
            notification_showing: false,
            notification_generation: 0,
            hidden_lru: VecDeque::new(),
            graphic_ready_seen: false,
            graphic_connected: false,
            graphic_contract_error: None,
            graphic_version: None,
            epoch_presentation_id: None,
        })))
    }

    pub fn lock(&self) -> std::sync::MutexGuard<'_, AppState> {
        self.0.lock().expect("shared state lock poisoned")
    }
}

impl AppState {
    pub fn to_context(&self, label: &str) -> Option<WindowContextDto> {
        let window = self.open_windows.get(label)?;
        let definition = self.registry.get(&window.window_type)?.clone();
        Some(WindowContextDto {
            label: window.label.clone(),
            instance_key: window.instance_key.clone(),
            window_type: window.window_type.clone(),
            title: window.title.clone(),
            definition,
            entity: window.entity.clone(),
            params: window.params.clone(),
            api_base_url: self.api_base_url.clone(),
            open_seq: window.open_seq,
        })
    }
}
