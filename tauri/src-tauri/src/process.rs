//! コアとグラフィック(Unity)の子プロセス。起動・監視と、アプリ全体の終了をまとめる。
//!
//! Tauri はストアの起動対象で、全プロセスの親になる。インストール先(この実行ファイルのフォルダ)の
//! 決まった場所に実行ファイルがあれば子として起動し、無ければ外部で起動済みとみなして接続だけする
//! (開発時に手で起動したコアや Unity Editor。将来は別のマシンのサーバーも同じ扱いになる)。
//! コアの終了を決められるのは自分で起動したときだけで、外部のコアは止めない。

use crate::state::SharedState;
use std::collections::VecDeque;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, ExitStatus, Stdio};
use std::time::{Duration, Instant};
use tauri::{AppHandle, Manager};

/// インストール先から見たコア(Nuitka のビルド一式)とグラフィック(Unity のビルド一式)の実行ファイル。
const CORE_EXE: &str = "Core/genesis";
const GRAPHIC_EXE: &str = "Graphics/Realcity";
/// コアを起動しないときの接続先。開発時に手で起動したコア(`uvicorn api:app --port 5000`)に合わせる。
const EXTERNAL_CORE_URL: &str = "http://127.0.0.1:5000";

/// コアが応答するまで待つ最大時間。Nuitka 版はライブラリとマスターの読み込みに時間がかかる。
const CORE_READY_TIMEOUT: Duration = Duration::from_secs(120);
/// コアが緊急オートセーブを書き終えて終了するまで待つ最大時間。過ぎたら強制終了する。
const CORE_EXIT_TIMEOUT: Duration = Duration::from_secs(60);
/// グラフィックが終了通知を受けて自分で終わるまで待つ最大時間。
const GRAPHIC_EXIT_TIMEOUT: Duration = Duration::from_secs(8);
/// この時間内にグラフィックが2回異常終了したら、再起動をあきらめてアプリを終了する。
const GRAPHIC_CRASH_WINDOW: Duration = Duration::from_secs(60);

#[cfg(target_os = "windows")]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

/// 起動時に決める接続先と、子として起動する実行ファイル。
pub struct Launch {
    pub api_base_url: String,
    core: Option<(PathBuf, u16)>,
    graphic: Option<PathBuf>,
    /// コアへ渡す環境変数。ストア連携が渡すストア管理のフォルダ(DLC・Workshop)。
    pub core_env: Vec<(&'static str, PathBuf)>,
}

impl Launch {
    pub fn resolve() -> Self {
        let install_dir = std::env::current_exe()
            .ok()
            .and_then(|exe| exe.parent().map(Path::to_path_buf))
            .unwrap_or_default();
        let existing = |name: &str| {
            Some(install_dir.join(format!("{name}{}", std::env::consts::EXE_SUFFIX))).filter(|path| path.is_file())
        };
        let core = existing(CORE_EXE).map(|exe| (exe, free_port()));
        let api_base_url = match &core {
            Some((_, port)) => format!("http://127.0.0.1:{port}"),
            None => std::env::var("GENESIS_UI_API_BASE_URL").unwrap_or_else(|_| EXTERNAL_CORE_URL.into()),
        };
        Self { api_base_url, core, graphic: existing(GRAPHIC_EXE), core_env: Vec::new() }
    }
}

#[cfg(test)]
impl Launch {
    /// 子を起動せず、指定した接続先へ繋ぐだけの起動設定(テスト用)。
    pub fn connect_only(api_base_url: String) -> Self {
        Self { api_base_url, core: None, graphic: None, core_env: Vec::new() }
    }
}

/// OS に空いているポートを選ばせる。コアが bind するまでの間に取られたら、コアが準備前に終了して起動失敗になる。
fn free_port() -> u16 {
    std::net::TcpListener::bind(("127.0.0.1", 0))
        .and_then(|listener| listener.local_addr())
        .map(|address| address.port())
        .unwrap_or(5000)
}

/// 起動した子プロセスと終了処理の状態。共有状態の中に1つだけ持つ。
pub struct Processes {
    launch: Launch,
    core: Option<Child>,
    graphic: Option<Child>,
    graphic_crashes: VecDeque<Instant>,
    exiting: bool,
    #[cfg(target_os = "windows")]
    job: Option<job::Job>,
}

impl Processes {
    pub fn new(launch: Launch) -> Self {
        Self {
            launch,
            core: None,
            graphic: None,
            graphic_crashes: VecDeque::new(),
            exiting: false,
            #[cfg(target_os = "windows")]
            job: job::Job::create(),
        }
    }

    /// 起動中のグラフィックのプロセスID。外部の Unity(Editor)や終了済みなら None。
    pub fn graphic_pid(&mut self) -> Option<u32> {
        let child = self.graphic.as_mut()?;
        matches!(child.try_wait(), Ok(None)).then(|| child.id())
    }

    fn spawn_graphic(&mut self, api_base_url: &str) -> Result<(), String> {
        let Some(exe) = self.launch.graphic.clone() else { return Ok(()) };
        log::info!("[process] グラフィックを起動します: {}", exe.display());
        let child = Command::new(&exe)
            .args(["--api-base-url", &format!("{}/api/v1", api_base_url.trim_end_matches('/'))])
            .current_dir(exe.parent().unwrap_or(Path::new(".")))
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .map_err(|e| format!("グラフィックを起動できません: {e}"))?;
        // グラフィックは状態を持たないので、Tauri が強制終了されたときは道連れにしてよい
        #[cfg(target_os = "windows")]
        if let Some(job) = &self.job {
            job.assign(&child);
        }
        self.graphic = Some(child);
        Ok(())
    }
}

/// コアを起動する。標準入力のパイプは Tauri が握り続け、閉じることでコアに終了を伝える
/// (Tauri が異常終了しても OS がパイプを閉じるので、コアは緊急オートセーブを残して終われる)。
/// そのためコアは Job Object に入れず、Tauri の終了で即座に道連れにしない。
fn spawn_core(exe: &Path, port: u16, env: &[(&'static str, PathBuf)]) -> std::io::Result<Child> {
    log::info!("[process] コアを起動します: {} (port {port})", exe.display());
    let mut command = Command::new(exe);
    command
        .arg("--attached")
        .current_dir(exe.parent().unwrap_or(Path::new(".")))
        .env("GENESIS_API_PORT", port.to_string())
        .envs(env.iter().map(|(key, dir)| (*key, dir.as_os_str())))
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(CREATE_NO_WINDOW);
    }
    command.spawn()
}

/// 子プロセスを起動する。コアを起動したときは応答するまで待ってからグラフィックを起動する
/// (グラフィックは起動直後からコアに問い合わせるため)。続けられないときはアプリを終了して false を返す。
pub fn start_children(app: &AppHandle, state: &SharedState) -> bool {
    let (core, api_base_url, env) = {
        let guard = state.lock();
        let launch = &guard.processes.launch;
        (launch.core.clone(), guard.api_base_url.clone(), launch.core_env.clone())
    };
    if let Some((exe, port)) = core {
        match spawn_core(&exe, port, &env) {
            Ok(child) => state.lock().processes.core = Some(child),
            Err(error) => {
                log::error!("[process] コアを起動できません: {error}");
                request_app_exit(app, state);
                return false;
            }
        }
        if !wait_core_ready(state) {
            request_app_exit(app, state);
            return false;
        }
    }
    let spawned = state.lock().processes.spawn_graphic(&api_base_url);
    if let Err(error) = spawned {
        log::error!("[process] {error}");
        request_app_exit(app, state);
        return false;
    }
    true
}

/// 起動したコアが版情報に応答するまで待つ。先にコアが終了した・時間切れなら false。
fn wait_core_ready(state: &SharedState) -> bool {
    let deadline = Instant::now() + CORE_READY_TIMEOUT;
    loop {
        if tauri::async_runtime::block_on(crate::version::core_info(state)).is_ok() {
            log::info!("[process] コアの準備ができました");
            return true;
        }
        let exited = {
            let mut guard = state.lock();
            guard.processes.core.as_mut().map_or(true, |child| !matches!(child.try_wait(), Ok(None)))
        };
        if exited {
            log::error!("[process] コアが準備の前に終了しました");
            return false;
        }
        if Instant::now() >= deadline {
            log::error!("[process] コアが {} 秒以内に応答しませんでした", CORE_READY_TIMEOUT.as_secs());
            return false;
        }
        std::thread::sleep(Duration::from_millis(200));
    }
}

enum ChildEvent {
    CoreExited(ExitStatus),
    GraphicExited(ExitStatus),
}

/// アプリが終わるまで子プロセスを見張る(呼び出したスレッドを使い続ける)。
/// - コアの終了: 都市の状態を失ったので、アプリを終了する。
/// - グラフィックの正常終了(exit 0): ユーザーが3Dのウィンドウを閉じたので、アプリを終了する。
/// - グラフィックの異常終了: 状態はコアにあり、再起動した Unity が自分で都市に復帰できるので再起動する。
///   ただし短時間に繰り返すなら直らないとみなしてアプリを終了する。
pub fn watch_children(app: &AppHandle, state: &SharedState) {
    loop {
        std::thread::sleep(Duration::from_millis(500));
        let event = {
            let mut guard = state.lock();
            if guard.processes.exiting {
                return;
            }
            poll_children(&mut guard.processes)
        };
        match event {
            None => {}
            Some(ChildEvent::CoreExited(status)) => {
                log::error!("[process] コアが終了しました ({status})。アプリを終了します");
                request_app_exit(app, state);
            }
            Some(ChildEvent::GraphicExited(status)) if status.success() => {
                log::info!("[process] グラフィックが終了しました。アプリを終了します");
                request_app_exit(app, state);
            }
            Some(ChildEvent::GraphicExited(status)) => {
                let respawned = {
                    let mut guard = state.lock();
                    let api_base_url = guard.api_base_url.clone();
                    let processes = &mut guard.processes;
                    if is_crash_loop(&mut processes.graphic_crashes, Instant::now()) {
                        Err(format!("グラフィックが {} 秒以内に2回異常終了しました ({status})", GRAPHIC_CRASH_WINDOW.as_secs()))
                    } else {
                        log::warn!("[process] グラフィックが異常終了しました ({status})。再起動します");
                        processes.spawn_graphic(&api_base_url)
                    }
                };
                if let Err(error) = respawned {
                    log::error!("[process] {error}。アプリを終了します");
                    request_app_exit(app, state);
                }
            }
        }
    }
}

fn poll_children(processes: &mut Processes) -> Option<ChildEvent> {
    if let Some(Ok(Some(status))) = processes.core.as_mut().map(Child::try_wait) {
        processes.core = None;
        return Some(ChildEvent::CoreExited(status));
    }
    if let Some(Ok(Some(status))) = processes.graphic.as_mut().map(Child::try_wait) {
        processes.graphic = None;
        return Some(ChildEvent::GraphicExited(status));
    }
    None
}

/// 異常終了の時刻を記録し、直近2回が GRAPHIC_CRASH_WINDOW に収まるか(繰り返し落ちているか)を返す。
fn is_crash_loop(crashes: &mut VecDeque<Instant>, now: Instant) -> bool {
    crashes.push_back(now);
    while crashes.len() > 2 {
        crashes.pop_front();
    }
    crashes.len() == 2 && now.duration_since(crashes[0]) < GRAPHIC_CRASH_WINDOW
}

/// アプリ全体の終了。発生源(終了ボタン・ウィンドウの×・グラフィックやコアの終了・通信断)によらず、
/// 必ずここに合流させる。2回目以降の呼び出しは何もしない。
///
/// 1. 画面を消す(保存を待つ間に操作させない)
/// 2. グラフィックへ system:shutdown を送る(未接続・送信失敗は無視)
/// 3. 自分で起動したコアの標準入力を閉じて終了を伝え、緊急オートセーブを書き終えて終了するまで待つ
/// 4. 残った子を強制終了して、UI 自身を終了する
pub fn request_app_exit(app: &AppHandle, state: &SharedState) {
    let (core, graphic) = {
        let mut guard = state.lock();
        if guard.processes.exiting {
            return;
        }
        guard.processes.exiting = true;
        (guard.processes.core.take(), guard.processes.graphic.take())
    };
    for window in app.webview_windows().values() {
        let _ = window.hide();
    }
    let _ = crate::ipc::send_ui_action(
        state,
        String::new(),
        None,
        "system:shutdown".to_string(),
        serde_json::json!({}),
    );
    if let Some(mut core) = core {
        drop(core.stdin.take());
        wait_or_kill(&mut core, CORE_EXIT_TIMEOUT);
    }
    if let Some(mut graphic) = graphic {
        wait_or_kill(&mut graphic, GRAPHIC_EXIT_TIMEOUT);
    }
    app.exit(0);
}

fn wait_or_kill(child: &mut Child, timeout: Duration) {
    let deadline = Instant::now() + timeout;
    while Instant::now() < deadline {
        match child.try_wait() {
            Ok(None) => std::thread::sleep(Duration::from_millis(100)),
            _ => return,
        }
    }
    let _ = child.kill();
    let _ = child.wait();
}

#[cfg(target_os = "windows")]
mod job {
    use windows_sys::Win32::Foundation::{CloseHandle, HANDLE};
    use windows_sys::Win32::System::JobObjects::{
        AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation, SetInformationJobObject,
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
    };

    /// 中のプロセスを、ハンドルが閉じたとき(Tauri の終了時。強制終了を含む)に道連れで終了させるジョブ。
    /// ハンドルは閉じずに持ち続け、プロセスの終了で OS に閉じさせる。
    pub struct Job(HANDLE);

    // ハンドルはどのスレッドから使ってもよい
    unsafe impl Send for Job {}

    impl Job {
        pub fn create() -> Option<Self> {
            unsafe {
                let handle = CreateJobObjectW(std::ptr::null(), std::ptr::null());
                if handle.is_null() {
                    return None;
                }
                let mut info: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = std::mem::zeroed();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                let applied = SetInformationJobObject(
                    handle,
                    JobObjectExtendedLimitInformation,
                    &info as *const JOBOBJECT_EXTENDED_LIMIT_INFORMATION as *const core::ffi::c_void,
                    std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
                );
                if applied == 0 {
                    CloseHandle(handle);
                    return None;
                }
                Some(Self(handle))
            }
        }

        pub fn assign(&self, child: &std::process::Child) {
            use std::os::windows::io::AsRawHandle;
            if unsafe { AssignProcessToJobObject(self.0, child.as_raw_handle() as HANDLE) } == 0 {
                log::warn!("[process] グラフィックを Job Object に入れられませんでした");
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn crash_loop_needs_two_crashes_within_the_window() {
        let start = Instant::now();
        let mut crashes = VecDeque::new();
        assert!(!is_crash_loop(&mut crashes, start));
        assert!(is_crash_loop(&mut crashes, start + GRAPHIC_CRASH_WINDOW - Duration::from_secs(1)));
    }

    #[test]
    fn crashes_far_apart_are_restarted_each_time() {
        let start = Instant::now();
        let mut crashes = VecDeque::new();
        for i in 0..4 {
            assert!(!is_crash_loop(&mut crashes, start + (GRAPHIC_CRASH_WINDOW + Duration::from_secs(1)) * i));
        }
        assert_eq!(crashes.len(), 2);
    }
}
