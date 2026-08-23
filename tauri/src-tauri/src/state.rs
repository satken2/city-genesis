use crate::ipc::MmfChannel;
use crate::messages::{MutexGroups, NotificationPayload, WindowContextDto, WindowRegistry};
use serde_json::Value;
use std::collections::{HashMap, VecDeque};
use std::process::Child;
use std::sync::atomic::AtomicBool;
use std::sync::{Arc, Mutex};

/// ウィンドウの位置とサイズ（論理ピクセル）。
/// プロセス起動中のみ記憶され、永続化されない。
/// ドック配置ウィンドウでは位置（x, y）は無視され、サイズのみ使用される。
#[derive(Debug, Clone, Copy, Default)]
pub struct WindowGeometry {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

/// 非表示(クローズ済み・再利用待ち)ウィンドウの保持上限。
/// ユーザが閉じたポップアップは即座に破棄せず非表示にし、再オープン時に再利用する
/// ことでWebView2の再起動コスト(約0.4秒)を回避する。メモリ使用量とのトレードオフで
/// 上限を設け、超過分は最も古いものから本当に破棄する(LRU)。
pub const MAX_HIDDEN_WINDOWS: usize = 10;

#[derive(Clone)]
pub struct SharedState(pub Arc<Mutex<AppState>>);

pub struct AppState {
    pub registry: WindowRegistry,
    pub mutex_groups: MutexGroups,
    pub open_windows: HashMap<String, OpenWindowState>,
    /// ウィンドウ種別ごとの位置・サイズ記憶（プロセス起動中のみ）。
    /// キーは `window_type`。ドック配置ウィンドウはサイズのみ、
    /// 自由配置ウィンドウは位置とサイズの両方が保存される。
    pub saved_geometries: HashMap<String, WindowGeometry>,
    /// UI→Graphic方向のMMFライター。
    pub writer_channel: Option<MmfChannel>,
    pub api_base_url: String,
    /// タスクバーが現在表示されているモニターのインデックス
    pub current_monitor_index: usize,
    /// 親プロセスから通知されたゲーム内時間情報
    pub game_time: Option<Value>,
    /// Graphic(Unity) プロセスの起動コマンド
    pub graphic_launch_command: String,
    /// リーダースレッドの終了フラグ
    pub shutdown: Arc<AtomicBool>,
    /// 最後に取得したセッション情報
    pub session_state: Option<serde_json::Value>,
    /// 最後に確認した current_save_id
    pub last_save_id: Option<String>,
    /// ローディング中フラグ（ゲーム開始処理中）
    pub is_loading: bool,
    /// ゲーム開始時にバックエンドから返された初期セル中心座標。
    /// game:start イベントに乗せてグラフィックに伝える。
    pub initial_cell_center: Option<(f64, f64)>,
    /// フリーモード新規ゲーム開始時に game:start IPC を保留する際に
    /// カメラ座標を一時保持する。finish_parameter_setup コマンドで消費される。
    pub pending_game_start: Option<(f64, f64)>,
    /// 開始したチュートリアルのID（None の場合はチュートリアル以外）
    pub tutorial_id: Option<String>,
    /// シナリオ（テンプレート）を「プレイ」として開始したかどうか。
    /// template_id 指定かつ template_mode=false のセッションで true になる。
    /// シナリオのゲームバランスパラメータは作者が決めるものであり、プレイ開始時に
    /// パラメータ設定ウィンドウを表示しないための判定に使う（フリーモード新規ゲームとの区別）。
    pub is_scenario_play: bool,
    /// パラメータ設定ウィンドウが「ゲーム内編集モード」で開かれているかどうか。
    /// false（既定）… フリーモード新規ゲームの初期セットアップ。完了で game:start を送る。
    /// true … テンプレート作成モード中に Taskbar から開いた再編集。完了しても game:start は
    ///   送らず、ウィンドウを閉じるだけ（既にゲームは進行中のため）。
    pub parameter_setup_edit_mode: bool,
    /// 通知ポップアップの待ち行列。
    /// ユーザーが×を押すか自動非表示になった後、次の通知を順番に表示するために使用する。
    pub notification_queue: VecDeque<NotificationPayload>,
    /// 通知ポップアップが現在表示中かどうか。
    /// true の間は新規通知をキューに積むだけで、表示は行わない。
    pub notification_showing: bool,
    /// 非表示(クローズ済み・再利用待ち)ポップアップウィンドウのラベル一覧。
    /// 末尾が最も最近に非表示にされたもの。open_window はこの中から再利用候補を探し、
    /// MAX_HIDDEN_WINDOWS を超えると先頭(最古)から本当に破棄する。
    pub hidden_lru: VecDeque<String>,
    /// バックエンドプロセス（genesis-core）。city-genesis/tauri から起動した場合に保持する。
    /// genesis.py 経由で起動された場合は None。
    pub backend_process: Option<Child>,
    /// バックエンドの起動コマンド。backend-launch.toml から読み込む。
    /// {port} プレースホルダーを含む場合、実際のポート番号で置換して起動する。
    pub backend_launch_command: String,
    /// Graphic(Unity) から system:graphic_ready を一度でも受信したかどうか。
    /// 2回目以降の受信は「クラッシュ後の自動再起動」を意味し、前セッションに
    /// 依存するポップアップの一掃が必要になる。初回受信(通常起動)では、
    /// Unity より先に起動した UI 側でユーザーが開いているウィンドウ
    /// (ロード画面など)を巻き込んで閉じないために区別する。
    pub graphic_ready_seen: bool,
}

#[derive(Clone)]
pub struct OpenWindowState {
    pub label: String,
    pub instance_key: String,
    pub window_type: String,
    pub title: String,
    pub entity: Option<crate::messages::EntityRef>,
    pub params: Value,
    /// ユーザが閉じたことによって非表示になり、再利用待ち状態かどうか。
    /// true の間は open_window の再利用候補として扱われる。
    pub hidden: bool,
    /// このウィンドウ(WebView)が「開かれた」世代番号。
    /// 非表示ウィンドウを再表示・転用するたびにインクリメントされる。
    /// React 側は WindowContext.open_seq として受け取り、パネルの再マウントキーに
    /// 含めることで「開き直し=状態リセット」のセマンティクスを保証する
    /// (WebView2 自体は再利用してもパネルの useState が前回の値を引きずらないようにする)。
    pub open_seq: u64,
}

impl SharedState {
    pub fn new(
        registry: WindowRegistry,
        mutex_groups: MutexGroups,
        api_base_url: String,
        graphic_launch_command: String,
        backend_launch_command: String,
    ) -> Self {
        Self(Arc::new(Mutex::new(AppState {
            registry,
            mutex_groups,
            open_windows: HashMap::new(),
            saved_geometries: HashMap::new(),
            writer_channel: None,
            api_base_url,
            current_monitor_index: 0,
            game_time: None,
            graphic_launch_command,
            shutdown: Arc::new(AtomicBool::new(false)),
            session_state: None,
            last_save_id: None,
            is_loading: false,
            initial_cell_center: None,
            pending_game_start: None,
            tutorial_id: None,
            is_scenario_play: false,
            parameter_setup_edit_mode: false,
            notification_queue: VecDeque::new(),
            notification_showing: false,
            hidden_lru: VecDeque::new(),
            backend_process: None,
            backend_launch_command,
            graphic_ready_seen: false,
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
