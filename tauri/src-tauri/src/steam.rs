//! Steam store integration (Cargo feature `steam`): initialize Steam, pass through
//! Steam-managed folders (DLC/Workshop), send authentication tickets to the core for verification,
//! and copy the achievements the core records in the player profile to Steam.
//!
//! Continue without integration when launched outside Steam (no Steam client or unknown
//! App ID). The core remains unverified and subject to the unverified epoch limit. Do not
//! call RestartAppIfNecessary, since playing unverified outside Steam is supported.
//! Graphics (Unity) also initializes SteamAPI for the overlay but does not handle verification.

use std::path::{Path, PathBuf};
use std::sync::mpsc::Receiver;
use std::sync::OnceLock;
use std::time::{Duration, Instant};
use steamworks::{AuthTicket, Client, TicketForWebApiResponse};

/// チケット識別子。コアと共有する定数を build.rs が `src/constants.ts` から埋め込む。
const TICKET_IDENTITY: &str = env!("CG_STEAM_TICKET_IDENTITY");

/// Core response retry interval and timeout (actually wait only when the core was started externally).
const CORE_WAIT_INTERVAL: Duration = Duration::from_secs(3);
const CORE_WAIT_TIMEOUT: Duration = Duration::from_secs(60);
/// Maximum wait for a ticket callback.
const TICKET_TIMEOUT: Duration = Duration::from_secs(10);
/// Retry count and initial/maximum intervals for temporary failures (no ticket, or unreachable core/verification server).
const MAX_ATTEMPTS: u32 = 10;
const BACKOFF_INITIAL: Duration = Duration::from_secs(5);
const BACKOFF_MAX: Duration = Duration::from_secs(60);

static CLIENT: OnceLock<Client> = OnceLock::new();

/// Initialize SteamAPI and return store-managed folders for the core. On failure, return empty and continue without Steam integration.
pub fn init() -> Vec<(&'static str, PathBuf)> {
    let client = match Client::init() {
        Ok(client) => client,
        Err(error) => {
            log::warn!("[steam] Steam を初期化できません。Steam 連携なしで続行します: {error}");
            return Vec::new();
        }
    };
    let pump = client.clone();
    std::thread::spawn(move || loop {
        pump.run_callbacks();
        std::thread::sleep(Duration::from_millis(100));
    });
    let dirs = content_dirs(&client);
    let _ = CLIENT.set(client);
    dirs
}

/// Steam-managed folders: DLC in the game's installation directory under `dlc/`; Workshop in the same library at
/// `steamapps/workshop/content/<App ID>` (`steamapps` is two levels above the installation directory, `steamapps/common/<game>`).
fn content_dirs(client: &Client) -> Vec<(&'static str, PathBuf)> {
    let app_id = client.utils().app_id();
    let install_dir = PathBuf::from(client.apps().app_install_dir(app_id));
    let mut dirs = vec![("GENESIS_DLC_DIR", install_dir.join("dlc"))];
    if let Some(steamapps) = install_dir.parent().and_then(Path::parent) {
        dirs.push(("GENESIS_WORKSHOP_DIR", steamapps.join("workshop").join("content").join(app_id.0.to_string())));
    }
    dirs
}

/// Start core ticket verification on a separate thread. Do nothing if Steam initialization failed.
pub fn start_verification(api_base_url: String) {
    let Some(client) = CLIENT.get().cloned() else { return };
    std::thread::spawn(move || verify(&client, api_base_url.trim_end_matches('/')));
}

/// Obtain a ticket and send it to the core's `POST /api/v1/system/verify`. The core queries the verification server and retains the
/// result. Send on every launch even if cached verification succeeds, refreshing owned-pack keys to enable DLC purchased since the last
/// launch. Do not retry 401/403, which definitively reject the ticket. For other failures, obtain a new ticket and retry after a delay.
fn verify(client: &Client, base: &str) {
    let http = reqwest::blocking::Client::builder().timeout(Duration::from_secs(30)).build().unwrap_or_default();
    if !wait_for_core(&http, base) {
        log::warn!("[steam] コアに接続できないため、チケットの検証を中止します");
        return;
    }

    let app_id = client.utils().app_id().0.to_string();
    let (sender, receiver) = std::sync::mpsc::channel();
    let _callback = client.register_callback(move |response: TicketForWebApiResponse| {
        let _ = sender.send(response);
    });
    let user = client.user();
    let mut backoff = BACKOFF_INITIAL;
    for attempt in 1..=MAX_ATTEMPTS {
        // Tickets are single-use; obtain a new one each time.
        let ticket = user.authentication_session_ticket_for_webapi(TICKET_IDENTITY);
        let result = match receive_ticket(&receiver, ticket) {
            Some(bytes) => {
                let body = serde_json::json!({ "app_id": app_id, "ticket": to_hex(&bytes) });
                http.post(format!("{base}/api/v1/system/verify")).json(&body).send().map_err(|e| e.to_string())
            }
            None => Err("チケットを取得できません".to_string()),
        };
        user.cancel_authentication_ticket(ticket);
        match result {
            Ok(response) if response.status().is_success() => {
                log::info!("[steam] チケットを検証しました");
                return;
            }
            Ok(response) if matches!(response.status().as_u16(), 401 | 403) => {
                log::warn!("[steam] チケットが拒否されました。未認証のまま続行します");
                return;
            }
            Ok(response) => log::warn!("[steam] 検証に失敗しました (HTTP {}) ({attempt}/{MAX_ATTEMPTS})", response.status()),
            Err(error) => log::warn!("[steam] 検証に失敗しました: {error} ({attempt}/{MAX_ATTEMPTS})"),
        }
        std::thread::sleep(backoff);
        backoff = (backoff * 2).min(BACKOFF_MAX);
    }
    log::warn!("[steam] {MAX_ATTEMPTS} 回試しても検証できませんでした。未認証のまま続行します");
}

/// Wait for the core to respond. Return false on timeout.
fn wait_for_core(http: &reqwest::blocking::Client, base: &str) -> bool {
    let deadline = Instant::now() + CORE_WAIT_TIMEOUT;
    loop {
        let responded = http
            .get(format!("{base}/api/v1/sessions"))
            .send()
            .ok()
            .is_some_and(|response| response.status().is_success() && response.bytes().is_ok());
        if responded {
            return true;
        }
        if Instant::now() >= deadline {
            return false;
        }
        std::thread::sleep(CORE_WAIT_INTERVAL);
    }
}

/// Wait for the specified ticket's callback and return its contents. Discard responses to earlier requests.
fn receive_ticket(receiver: &Receiver<TicketForWebApiResponse>, ticket: AuthTicket) -> Option<Vec<u8>> {
    let deadline = Instant::now() + TICKET_TIMEOUT;
    while let Some(remaining) = deadline.checked_duration_since(Instant::now()) {
        let response = receiver.recv_timeout(remaining).ok()?;
        if response.ticket_handle != ticket {
            continue;
        }
        return match response.result {
            Ok(()) => {
                let length = usize::try_from(response.ticket_len).unwrap_or(0).min(response.ticket.len());
                Some(response.ticket[..length].to_vec())
            }
            Err(error) => {
                log::warn!("[steam] チケットを取得できません: {error}");
                None
            }
        };
    }
    None
}

/// Bring Steam achievements in line with the player profile (`achievements` of GET /api/v1/profile) on a separate thread.
/// The core judges and records unlocks; Steam holds a copy. Sending the whole profile each time also delivers
/// achievements earned while Steam was not running, and leaves already unlocked ones untouched.
pub fn sync_achievements(api_base_url: String) {
    let Some(client) = CLIENT.get().cloned() else { return };
    std::thread::spawn(move || {
        let http = reqwest::blocking::Client::builder().timeout(Duration::from_secs(30)).build().unwrap_or_default();
        let base = api_base_url.trim_end_matches('/');
        if wait_for_core(&http, base) {
            send_profile_achievements(&client, &http, base);
        }
    });
}

fn send_profile_achievements(client: &Client, http: &reqwest::blocking::Client, base: &str) {
    let profile = http
        .get(format!("{base}/api/v1/profile"))
        .send()
        .and_then(|response| response.error_for_status())
        .and_then(|response| response.json::<serde_json::Value>());
    let names: Vec<String> = match profile {
        Ok(profile) => profile["achievements"]
            .as_array()
            .map(|items| items.iter().filter_map(|item| item.as_str().map(str::to_string)).collect())
            .unwrap_or_default(),
        Err(error) => {
            log::warn!("[steam] プロフィールを取得できないため、実績を送れません: {error}");
            return;
        }
    };
    let stats = client.user_stats();
    let mut unlocked = 0;
    for name in &names {
        let achievement = stats.achievement(name);
        if achievement.get() == Ok(true) {
            continue;
        }
        // Fails for API names not registered in Steamworks, or before Steam has delivered the user's stats.
        match achievement.set() {
            Ok(()) => unlocked += 1,
            Err(()) => log::warn!("[steam] 実績 {name} を解除できません"),
        }
    }
    if unlocked > 0 {
        match stats.store_stats() {
            Ok(()) => log::info!("[steam] 実績を {unlocked} 件解除しました"),
            Err(()) => log::warn!("[steam] 実績を Steam に保存できません"),
        }
    }
}

fn to_hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}
