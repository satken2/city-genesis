//! Child processes for the core and graphics (Unity). Centralizes startup, monitoring, and application-wide shutdown.
//!
//! Tauri is the store's launch target and parent of all processes. Launch executables found at fixed
//! locations under the installation directory (this executable's folder) as children. Otherwise, only connect,
//! assuming they were started externally (a manually started development core or Unity Editor; future remote
//! servers use the same approach). Stop the core only if this process launched it; never stop an external core.

use crate::state::SharedState;
use std::collections::VecDeque;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, ExitStatus, Stdio};
use std::time::{Duration, Instant};
use tauri::{AppHandle, Manager};

/// Core (Nuitka build) and graphics (Unity build) executables relative to the installation directory.
const CORE_EXE: &str = "Core/genesis";
const GRAPHIC_EXE: &str = "Graphics/Realcity";
/// Connection endpoint when not launching the core. Matches a manually started development core (`uvicorn api:app --port 5000`).
const EXTERNAL_CORE_URL: &str = "http://127.0.0.1:5000";

/// Maximum wait for a core response. The Nuitka build takes time to load libraries and master data.
const CORE_READY_TIMEOUT: Duration = Duration::from_secs(120);
/// Maximum wait for the core to finish emergency autosaving and exit. Force termination after this timeout.
const CORE_EXIT_TIMEOUT: Duration = Duration::from_secs(60);
/// Maximum wait for graphics to exit after receiving the shutdown notification.
const GRAPHIC_EXIT_TIMEOUT: Duration = Duration::from_secs(8);
/// If graphics crashes twice within this interval, stop restarting and exit the app.
const GRAPHIC_CRASH_WINDOW: Duration = Duration::from_secs(60);

#[cfg(target_os = "windows")]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

/// Connection endpoint and child executables determined at startup.
pub struct Launch {
    pub api_base_url: String,
    core: Option<(PathBuf, u16)>,
    graphic: Option<PathBuf>,
    /// Environment variables for the core: store-managed folders (DLC and Workshop) supplied by store integration.
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
    /// Launch configuration that only connects to the specified endpoint without starting children (for tests).
    pub fn connect_only(api_base_url: String) -> Self {
        Self { api_base_url, core: None, graphic: None, core_env: Vec::new() }
    }
}

/// Let the OS choose an available port. If another process takes it before the core binds, the core exits before readiness and startup fails.
fn free_port() -> u16 {
    std::net::TcpListener::bind(("127.0.0.1", 0))
        .and_then(|listener| listener.local_addr())
        .map(|address| address.port())
        .unwrap_or(5000)
}

/// Launched child processes and shutdown state. Keep a single instance in shared state.
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

    /// Running graphics process ID. None for external Unity (Editor) or an exited process.
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
        // Graphics holds no state, so it can be terminated along with Tauri if Tauri is forcibly stopped.
        #[cfg(target_os = "windows")]
        if let Some(job) = &self.job {
            job.assign(&child);
        }
        self.graphic = Some(child);
        Ok(())
    }
}

/// Launch the core. Tauri keeps its stdin pipe open and closes it to signal shutdown (even if
/// Tauri crashes, the OS closes the pipe, allowing the core to write an emergency autosave and
/// exit). Therefore, do not put the core in a Job Object that would terminate it immediately with Tauri.
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

/// Start child processes. If launching the core, wait for a response before starting graphics, which
/// queries the core immediately on startup. If startup cannot continue, exit the app and return false.
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

/// Wait for the launched core to respond with version information. Return false if it exits first or times out.
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

/// Monitor children until the app exits, occupying the calling thread. Core exit:
/// city state is lost, so exit the app. Normal graphics exit (exit 0): the user
/// closed the 3D window, so exit the app. Graphics crash: restart, since state
/// remains in the core and the new Unity instance can restore the city itself. If
///   crashes recur within a short interval, consider recovery impossible and exit the app.
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

/// Record the crash time and return whether the latest two crashes fall within GRAPHIC_CRASH_WINDOW.
fn is_crash_loop(crashes: &mut VecDeque<Instant>, now: Instant) -> bool {
    crashes.push_back(now);
    while crashes.len() > 2 {
        crashes.pop_front();
    }
    crashes.len() == 2 && now.duration_since(crashes[0]) < GRAPHIC_CRASH_WINDOW
}

/// Application-wide shutdown. Route every source here (Exit button, window
/// close, graphics/core exit, or connection failure). Subsequent calls do nothing.
///
/// 1. Hide windows to block interaction while saving. 2. Send
/// system:shutdown to graphics (ignore disconnected or failed sends). 3.
/// Close stdin of the core we launched, then wait for it to finish emergency
/// autosaving and exit. 4. Terminate remaining children and exit the UI itself.
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

    /// Job that terminates contained processes when its handle closes (on Tauri exit,
    /// including forced termination). Retain the handle and let the OS close it when the process exits.
    pub struct Job(HANDLE);

    // The handle may be used from any thread.
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
