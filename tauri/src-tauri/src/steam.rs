//! Steam 版のストア連携(Cargo の feature `steam`)。Steam の初期化、Steam が管理するフォルダ(DLC・Workshop)の
//! 受け渡し、認証チケットをコアへ渡して検証してもらう処理を持つ。
//!
//! Steam の外で起動したとき(Steam クライアントが無い・App ID が分からない)は何もせずに続行する。
//! コアは未認証のままで、未認証のエポック上限が掛かる。RestartAppIfNecessary は呼ばない
//! (Steam の外からの起動も、未認証として遊べるようにしているため)。
//! グラフィック(Unity)もオーバーレイのために SteamAPI を初期化するが、認証には関わらない。

use std::path::{Path, PathBuf};
use std::sync::mpsc::Receiver;
use std::sync::OnceLock;
use std::time::{Duration, Instant};
use steamworks::{AuthTicket, Client, TicketForWebApiResponse};

/// チケットの identity と認証済みの状態値。コアの consts.py が正で、`src/constants.ts` から build.rs が埋め込む。
const TICKET_IDENTITY: &str = env!("CG_STEAM_TICKET_IDENTITY");
const GAME_VERIFIED: &str = env!("CG_GAME_VERIFIED");

/// コアの応答を待つ間隔と最大時間(コアを外部で起動したときだけ実際に待つ)。
const CORE_WAIT_INTERVAL: Duration = Duration::from_secs(3);
const CORE_WAIT_TIMEOUT: Duration = Duration::from_secs(60);
/// チケットのコールバックを待つ最大時間。
const TICKET_TIMEOUT: Duration = Duration::from_secs(10);
/// 一時的な失敗(チケットが取れない・コアや検証サーバーに届かない)のときに試す回数と、間隔の初期値・上限。
const MAX_ATTEMPTS: u32 = 10;
const BACKOFF_INITIAL: Duration = Duration::from_secs(5);
const BACKOFF_MAX: Duration = Duration::from_secs(60);

static CLIENT: OnceLock<Client> = OnceLock::new();

/// SteamAPI を初期化し、コアへ渡すストア管理のフォルダを返す。初期化できなければ空で、Steam 連携なしで続行する。
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

/// Steam が管理するフォルダ。DLC はゲームのインストール先の `dlc/`、Workshop は同じライブラリの
/// `steamapps/workshop/content/<App ID>`(インストール先 `steamapps/common/<ゲーム>` の2つ上が `steamapps`)。
fn content_dirs(client: &Client) -> Vec<(&'static str, PathBuf)> {
    let app_id = client.utils().app_id();
    let install_dir = PathBuf::from(client.apps().app_install_dir(app_id));
    let mut dirs = vec![("GENESIS_DLC_DIR", install_dir.join("dlc"))];
    if let Some(steamapps) = install_dir.parent().and_then(Path::parent) {
        dirs.push(("GENESIS_WORKSHOP_DIR", steamapps.join("workshop").join("content").join(app_id.0.to_string())));
    }
    dirs
}

/// コアにチケットを検証してもらう処理を別スレッドで始める。Steam を初期化できていなければ何もしない。
pub fn start_verification(api_base_url: String) {
    let Some(client) = CLIENT.get().cloned() else { return };
    std::thread::spawn(move || verify(&client, api_base_url.trim_end_matches('/')));
}

/// チケットを取ってコアの `POST /api/v1/system/verify` へ送る。検証サーバーへの問い合わせと結果の保持はコアが行う。
/// 401/403 はチケットの拒否が確定したので再送しない。それ以外の失敗は、チケットを取り直して間隔を空けて再送する。
fn verify(client: &Client, base: &str) {
    let http = reqwest::blocking::Client::builder().timeout(Duration::from_secs(30)).build().unwrap_or_default();
    match wait_for_core(&http, base) {
        None => {
            log::warn!("[steam] コアに接続できないため、チケットの検証を中止します");
            return;
        }
        Some(true) => {
            log::info!("[steam] 認証済みのため、チケットを送りません");
            return;
        }
        Some(false) => {}
    }

    let app_id = client.utils().app_id().0.to_string();
    let (sender, receiver) = std::sync::mpsc::channel();
    let _callback = client.register_callback(move |response: TicketForWebApiResponse| {
        let _ = sender.send(response);
    });
    let user = client.user();
    let mut backoff = BACKOFF_INITIAL;
    for attempt in 1..=MAX_ATTEMPTS {
        // チケットは使い捨てなので毎回取り直す
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

/// コアが応答するまで待ち、認証済みかどうかを返す。待ちきれなければ None。
fn wait_for_core(http: &reqwest::blocking::Client, base: &str) -> Option<bool> {
    let deadline = Instant::now() + CORE_WAIT_TIMEOUT;
    loop {
        let session = http
            .get(format!("{base}/api/v1/sessions"))
            .send()
            .ok()
            .filter(|response| response.status().is_success())
            .and_then(|response| response.json::<serde_json::Value>().ok());
        if let Some(session) = session {
            return Some(session["verification_status"].to_string() == GAME_VERIFIED);
        }
        if Instant::now() >= deadline {
            return None;
        }
        std::thread::sleep(CORE_WAIT_INTERVAL);
    }
}

/// 指定したチケットのコールバックを待ち、チケットの中身を返す。前の要求への応答は読み捨てる。
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

fn to_hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}
