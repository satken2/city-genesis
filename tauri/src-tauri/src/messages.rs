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

/// ウィンドウの配置方法
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum WindowLayout {
    /// レジストリの width/height で中央に配置（従来の挙動）
    #[default]
    Default,
    /// 画面左端に上下いっぱいのサイドパネルとして配置。
    /// 幅はモニターの40%、高さはタスクバーを除いた画面全体にマージンを空けて広がる。
    LeftPanel,
    /// LeftPanel の約半分の幅（モニターの20%）で配置。
    LeftPanelNarrow,
    /// 画面左端・上端に配置。上下には広げず、レジストリの width/height をそのまま使用する。
    LeftTop,
    /// 画面右端・上端に配置。上下には広げず、レジストリの width/height をそのまま使用する。
    /// 位置は常に右上に固定し、記憶済みジオメトリからはサイズだけを復元する。
    RightTop,
    /// 画面右端に上下いっぱいのサイドパネルとして配置。
    /// 幅はモニターの20%、高さはタスクバーを除いた画面全体にマージンを空けて広がる。
    RightPanel,
    /// 画面下部中央に配置するパネル。
    /// 幅はモニターの65%、高さはレジストリのheightをそのまま使用する。
    /// タスクバーの直上に配置される。
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
    /// 直接ロードするHTMLファイルのパス（例: "debug/debug_map.html"）。
    /// 設定されている場合、React UI ではなくバニラHTMLをwebviewに直接表示する。
    /// クエリパラメータとして api_base_url が自動付与される。
    #[serde(default)]
    pub html_path: Option<String>,
    /// true のときタイトルバーのみで本文は空（プレースホルダー用）
    #[serde(default)]
    pub empty_shell: bool,
    /// ウィンドウの配置方法。未設定なら Default（中央配置）。
    #[serde(default)]
    pub layout: WindowLayout,
    /// 作業領域（モニター幅）に対する初期幅の比率。`height_ratio` と両方指定時のみ有効（例: 0.8 = 80%）。
    #[serde(default)]
    pub width_ratio: Option<f64>,
    /// タスクバー上の利用可能高さに対する初期高さの比率。`width_ratio` と両方指定時のみ有効。
    #[serde(default)]
    pub height_ratio: Option<f64>,
    /// true の場合、ウィンドウを最大化して表示する。
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
    /// ウィンドウが「開かれた」世代番号。非表示ウィンドウの再表示・転用のたびに増える。
    /// React 側はこれを再マウントキーに含め、開き直し時にパネルの状態を確実にリセットする。
    pub open_seq: u64,
}

/// 通知ポップアップのペイロード。
/// Rust → フロントエンドへ通知内容を配信する際に使用する。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct NotificationPayload {
    pub message: String,
    pub icon_path: String,
    pub duration_ms: Option<u64>,
}

/// graphic-launch.toml のデシリアライズ用構造体
/// 実行ファイルと同じディレクトリに配置され、Graphic(Unity) の起動コマンドを定義する
#[derive(Debug, Clone, Deserialize)]
pub struct GraphicLaunchConfig {
    pub launch_command: String,
}

/// 実行ファイルと同じディレクトリに配置され、バックエンド（genesis-core）の起動コマンドを定義する
#[derive(Debug, Clone, Deserialize)]
pub struct BackendLaunchConfig {
    pub launch_command: String,
}
