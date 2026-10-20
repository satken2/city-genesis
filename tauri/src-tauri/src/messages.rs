use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::HashMap;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Envelope {
    pub version: String,
    pub request_id: String,
    pub timestamp: String,
    pub source: String,
    pub destination: String,
    pub event: String,
    #[serde(default)]
    pub payload: Value,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct EntityRef {
    #[serde(default)]
    pub r#type: Option<String>,
    #[serde(default)]
    pub id: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct PositionHint {
    #[serde(default)]
    pub mode: Option<String>,
    #[serde(default)]
    pub x: Option<f64>,
    #[serde(default)]
    pub y: Option<f64>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct OpenWindowParams {
    #[serde(default)]
    pub preferred_position: Option<PositionHint>,
    #[serde(default)]
    pub extra: Option<Value>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct OpenWindowPayload {
    pub window_type: String,
    #[serde(default)]
    pub entity: Option<EntityRef>,
    #[serde(default)]
    pub params: Option<OpenWindowParams>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct UiActionPayload {
    pub action: String,
    pub window_type: String,
    #[serde(default)]
    pub entity: Option<EntityRef>,
    #[serde(default)]
    pub params: Value,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WindowActionConfig {
    pub id: String,
    pub label: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FetchConfig {
    pub method: String,
    pub path_template: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum WindowPolicy {
    Single,
    Multiple,
    SinglePerEntity,
}

/// Window placement method
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum WindowLayout {
    /// Center using the registry width/height (legacy behavior).
    #[default]
    Default,
    /// Place a full-height side panel at the left edge. Width
    /// is 40% of the monitor (or width_ratio when specified);
    /// height fills the screen above the taskbar with margins.
    LeftPanel,
    /// Place at roughly half the width of LeftPanel (20% of the monitor, overridable with width_ratio).
    LeftPanelNarrow,
    /// Place at the top left using the registry width/height without stretching vertically.
    LeftTop,
    /// Place at the top right using the registry width/height without stretching
    /// vertically. Always anchor to the top right; restore only size from saved geometry.
    RightTop,
    /// Place a full-height side panel at the right edge. Width is 20% of
    /// the monitor; height fills the screen above the taskbar with margins.
    RightPanel,
    /// Place a panel at the bottom center. Width is 65%
    /// of the monitor; height uses the registry height
    /// unchanged. Position directly above the taskbar.
    BottomCenter,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WindowDefinition {
    pub title: String,
    pub width: u32,
    pub height: u32,
    pub resizable: bool,
    pub policy: WindowPolicy,
    #[serde(default)]
    pub entity_type: Option<String>,
    #[serde(default)]
    pub fetch: Option<FetchConfig>,
    #[serde(default)]
    pub actions: Vec<WindowActionConfig>,
    /// Path to an HTML file to load directly (for example,
    /// "debug/debug_map.html"). When set, display plain HTML in the webview instead
    /// of the React UI. api_base_url is appended automatically as a query parameter.
    #[serde(default)]
    pub html_path: Option<String>,
    /// When true, show only the title bar with an empty body (for placeholders).
    #[serde(default)]
    pub empty_shell: bool,
    /// When true, omit the title bar (drag area and close button) and let
    /// the panel fill the window. The panel handles closing and margins.
    #[serde(default)]
    pub chromeless: bool,
    /// Window placement method. Defaults to Default (centered) when unset.
    #[serde(default)]
    pub layout: WindowLayout,
    /// Initial width as a fraction of the work area (monitor width), e.g.
    /// 0.8 = 80%. In `default` layout, effective only when `height_ratio`
    /// is also set. In `left_panel` / `left_panel_narrow` / `right_top` /
    /// `bottom_center`, independently overrides the layout's default ratio.
    #[serde(default)]
    pub width_ratio: Option<f64>,
    /// Initial height as a fraction of the available height above the
    /// taskbar. In `default` layout, effective only when `width_ratio` is also set.
    #[serde(default)]
    pub height_ratio: Option<f64>,
    /// When true, show the window maximized.
    #[serde(default)]
    pub maximized: bool,
}

pub type WindowRegistry = HashMap<String, WindowDefinition>;
pub type MutexGroups = HashMap<String, Vec<String>>;

#[derive(Debug, Clone, Serialize)]
pub struct WindowContextDto {
    pub label: String,
    pub instance_key: String,
    pub window_type: String,
    pub title: String,
    pub definition: WindowDefinition,
    pub entity: Option<EntityRef>,
    pub params: Value,
    pub api_base_url: String,
    /// Window opening generation, incremented whenever a hidden window is shown again or
    /// repurposed. React includes it in the remount key to reset panel state on reopening.
    pub open_seq: u64,
}

/// Notification popup payload. Used to send
/// notification content from Rust to the frontend.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct NotificationPayload {
    pub message: String,
    pub icon_path: String,
    pub duration_ms: Option<u64>,
}
