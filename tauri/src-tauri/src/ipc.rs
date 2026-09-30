use crate::messages::{EntityRef, Envelope, UiActionPayload};
#[cfg(target_os = "windows")]
use crate::messages::OpenWindowPayload;
use crate::state::SharedState;
#[cfg(target_os = "windows")]
use crate::window;
use serde_json::Value;
#[cfg(target_os = "windows")]
use serde_json::json;
use std::sync::atomic::{AtomicU64, Ordering};
#[cfg(target_os = "windows")]
use std::sync::atomic::AtomicBool;
#[cfg(target_os = "windows")]
use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use tauri::AppHandle;
#[cfg(target_os = "windows")]
use tauri::{Emitter, Manager};
#[cfg(target_os = "windows")]
use windows_sys::Win32::Foundation::*;
#[cfg(target_os = "windows")]
use windows_sys::Win32::System::Memory::*;
#[cfg(target_os = "windows")]
use windows_sys::Win32::System::Threading::*;

static NEXT_ID: AtomicU64 = AtomicU64::new(1);

// 共有メモリのサイズ。Unity側 Assets/Scripts/IPC/IpcTransport.cs の CAPACITY と必ず一致させること
// (CreateOrOpen は先に起動した側のサイズが採用されるため、食い違うと片側だけ範囲外を読む)。
// 土地区画の一括作成プレビューのようにポリゴンを数百件送るメッセージがあるため8KBでは足りない。
#[cfg(target_os = "windows")]
const CAPACITY: usize = 1024 * 1024;
#[cfg(target_os = "windows")]
const HEADER_SIZE: usize = 8;
#[cfg(target_os = "windows")]
const BUFFER_SIZE: usize = CAPACITY - HEADER_SIZE;

// ============================================================================
// MmfChannel — MemoryMappedFile + Mutex + Event のラッパー
// ============================================================================

#[cfg(target_os = "windows")]
struct MmfChannelInner {
    mmf_handle: HANDLE,
    buffer: *mut u8,
    mutex: HANDLE,
    event: HANDLE,
}

#[cfg(target_os = "windows")]
#[derive(Clone)]
pub(crate) struct MmfChannel {
    inner: Arc<MmfChannelInner>,
}

#[cfg(not(target_os = "windows"))]
#[derive(Clone)]
pub(crate) struct MmfChannel;

#[cfg(target_os = "windows")]
impl MmfChannel {
    fn create(mmf_name: &str, mutex_name: &str, event_name: &str, reset: bool) -> Result<Self, String> {
        let mmf_name_wide = to_wide(mmf_name);
        let mutex_name_wide = to_wide(mutex_name);
        let event_name_wide = to_wide(event_name);

        unsafe {
            let mmf = CreateFileMappingW(
                INVALID_HANDLE_VALUE,
                std::ptr::null(),
                PAGE_READWRITE,
                0,
                CAPACITY as u32,
                mmf_name_wide.as_ptr(),
            );
            if mmf.is_null() {
                return Err(format!("CreateFileMappingW failed for {mmf_name}"));
            }

            let view = MapViewOfFile(mmf, FILE_MAP_ALL_ACCESS, 0, 0, CAPACITY);
            let buffer = view.Value as *mut u8;
            if buffer.is_null() {
                CloseHandle(mmf);
                return Err("MapViewOfFile failed".to_string());
            }

            let mutex = CreateMutexW(std::ptr::null(), 0, mutex_name_wide.as_ptr());
            if mutex.is_null() {
                UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS { Value: buffer as *mut _ });
                CloseHandle(mmf);
                return Err("CreateMutexW failed".to_string());
            }

            let event = CreateEventW(std::ptr::null(), 0, 0, event_name_wide.as_ptr());
            if event.is_null() {
                CloseHandle(mutex);
                UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS { Value: buffer as *mut _ });
                CloseHandle(mmf);
                return Err("CreateEventW failed".to_string());
            }

            let channel = Self {
                inner: Arc::new(MmfChannelInner {
                    mmf_handle: mmf,
                    buffer,
                    mutex,
                    event,
                }),
            };

            if reset {
                WaitForSingleObject(mutex, INFINITE);
                write_i32(buffer, 0, 0);
                write_i32(buffer, 4, 0);
                ReleaseMutex(mutex);
            }

            Ok(channel)
        }
    }

    fn write_message(&self, data: &[u8]) -> Result<(), String> {
        let message_size = 4 + data.len();
        if message_size > BUFFER_SIZE - 4 {
            return Err(format!("Message too large: {message_size} bytes"));
        }

        unsafe {
            WaitForSingleObject(self.inner.mutex, INFINITE);

            let write_pos = read_i32(self.inner.buffer, 0) as usize;
            let read_pos = read_i32(self.inner.buffer, 4) as usize;

            let result = if write_pos >= read_pos {
                let at_end = BUFFER_SIZE - write_pos;
                let at_start = read_pos;

                if message_size <= at_end {
                    self.write_at(write_pos, data);
                    write_i32(self.inner.buffer, 0, (write_pos + message_size) as i32);
                    Ok(())
                } else if message_size < at_start {
                    // ReadPos ちょうどまで埋めると WritePos == ReadPos(=空)と区別できないため、等号は許さない。
                    if write_pos + 4 <= BUFFER_SIZE {
                        write_i32(self.inner.buffer, HEADER_SIZE + write_pos, 0);
                    }
                    self.write_at(0, data);
                    write_i32(self.inner.buffer, 0, message_size as i32);
                    Ok(())
                } else {
                    Err("MMF buffer full".to_string())
                }
            } else {
                let available = read_pos - write_pos - 4;
                if message_size > available {
                    Err("MMF buffer full".to_string())
                } else {
                    self.write_at(write_pos, data);
                    write_i32(self.inner.buffer, 0, (write_pos + message_size) as i32);
                    Ok(())
                }
            };

            ReleaseMutex(self.inner.mutex);

            if result.is_ok() {
                SetEvent(self.inner.event);
            }

            result
        }
    }

    unsafe fn write_at(&self, pos: usize, data: &[u8]) {
        let base = self.inner.buffer.add(HEADER_SIZE + pos);
        write_i32_raw(base, data.len() as i32);
        std::ptr::copy_nonoverlapping(data.as_ptr(), base.add(4), data.len());
    }

    fn read_message(&self) -> Option<Vec<u8>> {
        unsafe {
            WaitForSingleObject(self.inner.mutex, INFINITE);
            let data = self.read_message_locked();
            ReleaseMutex(self.inner.mutex);
            data
        }
    }

    /// 呼び出し元がMutexを保持している前提。
    unsafe fn read_message_locked(&self) -> Option<Vec<u8>> {
        let write_pos = read_i32(self.inner.buffer, 0) as usize;
        let mut read_pos = read_i32(self.inner.buffer, 4) as usize;

        if read_pos == write_pos {
            return None;
        }

        // ラップ後に先頭へ書かれたメッセージは同じ書き込み通知の中で続けて読む。
        // ここで打ち切ると、次の書き込み通知が来るまで取り残される。
        if read_pos + 4 > BUFFER_SIZE || read_i32(self.inner.buffer, HEADER_SIZE + read_pos) == 0 {
            read_pos = 0;
            write_i32(self.inner.buffer, 4, 0);
            if read_pos == write_pos {
                return None;
            }
        }

        let length = read_i32(self.inner.buffer, HEADER_SIZE + read_pos);
        if length <= 0 || length as usize > BUFFER_SIZE - read_pos - 4 {
            // 長さが壊れている。範囲外を読まず、未読分を捨てて以降の通信を生かす。
            write_i32(self.inner.buffer, 4, write_pos as i32);
            return None;
        }
        let length = length as usize;

        let mut data = vec![0u8; length];
        std::ptr::copy_nonoverlapping(
            self.inner.buffer.add(HEADER_SIZE + read_pos + 4),
            data.as_mut_ptr(),
            length,
        );

        write_i32(self.inner.buffer, 4, (read_pos + 4 + length) as i32);
        Some(data)
    }

    fn wait_for_data(&self, timeout_ms: u32) -> bool {
        unsafe { WaitForSingleObject(self.inner.event, timeout_ms) == WAIT_OBJECT_0 as u32 }
    }
}

#[cfg(target_os = "windows")]
impl Drop for MmfChannelInner {
    fn drop(&mut self) {
        unsafe {
            if !self.buffer.is_null() {
                UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS { Value: self.buffer as *mut _ });
            }
            if !self.mmf_handle.is_null() {
                CloseHandle(self.mmf_handle);
            }
            if !self.mutex.is_null() {
                CloseHandle(self.mutex);
            }
            if !self.event.is_null() {
                CloseHandle(self.event);
            }
        }
    }
}

#[cfg(target_os = "windows")]
unsafe impl Send for MmfChannelInner {}
#[cfg(target_os = "windows")]
unsafe impl Sync for MmfChannelInner {}

/// アライメント安全问题なくMMFバッファからi32を読み出す。
#[cfg(target_os = "windows")]
unsafe fn read_i32(base: *mut u8, offset: usize) -> i32 {
    let mut bytes = [0u8; 4];
    std::ptr::copy_nonoverlapping(base.add(offset), bytes.as_mut_ptr(), 4);
    i32::from_ne_bytes(bytes)
}

/// アライメント安全问题なくMMFバッファにi32を書き込む。
#[cfg(target_os = "windows")]
unsafe fn write_i32(base: *mut u8, offset: usize, value: i32) {
    std::ptr::copy_nonoverlapping(value.to_ne_bytes().as_ptr(), base.add(offset), 4);
}

/// 生ポインタにアライメント安全问题なくi32を書き込む。
#[cfg(target_os = "windows")]
unsafe fn write_i32_raw(ptr: *mut u8, value: i32) {
    std::ptr::copy_nonoverlapping(value.to_ne_bytes().as_ptr(), ptr, 4);
}

#[cfg(target_os = "windows")]
fn to_wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

// ============================================================================
// 公開API
// ============================================================================

/// IPC通信を開始する。
/// UI→Graphic方向のMMFライターとGraphic→UI方向のMMFリーダーを作成し、
/// リーダースレッドを起動してUnityからのメッセージを待機する。
/// IPC通信を開始する。
/// UI→Graphic方向のMMFライターとGraphic→UI方向のMMFリーダーを作成し、
/// リーダースレッドを起動してUnityからのメッセージを待機する。
pub fn start_ipc(app: AppHandle, state: SharedState) {
    #[cfg(target_os = "windows")]
    {
        let writer = match MmfChannel::create(
            "genesis-ipc-ui-to-graphic",
            "genesis-ipc-mutex-utg",
            "genesis-ipc-event-utg",
            true,
        ) {
            Ok(w) => w,
            Err(e) => {
                eprintln!("Failed to create writer MMF: {e}");
                return;
            }
        };

        let reader = match MmfChannel::create(
            "genesis-ipc-graphic-to-ui",
            "genesis-ipc-mutex-gtu",
            "genesis-ipc-event-gtu",
            false,
        ) {
            Ok(r) => r,
            Err(e) => {
                eprintln!("Failed to create reader MMF: {e}");
                return;
            }
        };

        {
            let mut guard = state.lock();
            guard.writer_channel = Some(writer);
        }

        // system.ready を最初に送信
        let ready = make_envelope("tauri", "parent", "system:ready", crate::version::definition());
        let _ = send_to_parent(&state, ready);

        // リーダースレッド起動
        let shutdown = {
            let guard = state.lock();
            guard.shutdown.clone()
        };

        std::thread::spawn(move || {
            run_reader_loop(reader, app, state, shutdown);
        });
    }
    #[cfg(not(target_os = "windows"))]
    {
        let _ = app;
        let _ = state;
        log::warn!("IPC is not supported on this platform. Ignoring start_ipc.");
    }
}

/// メッセージをUnityに送信する。
/// MMFに直接書き込む（Mutex取得→書き込み→Event通知）。
pub fn send_to_parent(state: &SharedState, envelope: Envelope) -> Result<(), String> {
    if !envelope.event.starts_with("system:") {
        if let Some(error) = &state.lock().graphic_contract_error {
            return Err(error.clone());
        }
    }
    #[cfg(target_os = "windows")]
    {
        let json = serde_json::to_string(&envelope).map_err(|e| e.to_string())?;
        let data = json.as_bytes();

        let writer = {
            let guard = state.lock();
            guard.writer_channel.as_ref().cloned().ok_or("IPC not initialized")?
        };

        writer.write_message(data)
    }
    #[cfg(not(target_os = "windows"))]
    {
        let _ = state;
        log::debug!("send_to_parent ignored on non-Windows: {:?}", envelope.event);
        Ok(())
    }
}

/// UIアクション（モード変更やセッション操作など）をUnityに送信する。
pub fn send_ui_action(
    state: &SharedState,
    window_type: String,
    entity: Option<EntityRef>,
    action: String,
    params: Value,
) -> Result<(), String> {
    let payload = UiActionPayload {
        action,
        window_type,
        entity,
        params,
    };
    send_to_parent(
        state,
        make_envelope(
            "tauri",
            "parent",
            "ui:action",
            serde_json::to_value(payload).map_err(|err| err.to_string())?,
        ),
    )
}

/// エンベロープを生成するヘルパー。
pub fn make_envelope(source: &str, destination: &str, event: &str, payload: Value) -> Envelope {
    Envelope {
        version: "1.0".to_string(),
        request_id: format!("msg-{}", NEXT_ID.fetch_add(1, Ordering::Relaxed)),
        timestamp: iso_timestamp(),
        source: source.to_string(),
        destination: destination.to_string(),
        event: event.to_string(),
        payload,
    }
}

// ============================================================================
// リーダースレッド
// ============================================================================

#[cfg(target_os = "windows")]
fn run_reader_loop(
    reader: MmfChannel,
    app: AppHandle,
    state: SharedState,
    shutdown: Arc<AtomicBool>,
) {
    let mut was_connected = false;

    while !shutdown.load(Ordering::Relaxed) {
        if !reader.wait_for_data(500) {
            continue;
        }

        if shutdown.load(Ordering::Relaxed) {
            break;
        }

        loop {
            match reader.read_message() {
                Some(data) => {
                    was_connected = true;

                    let json_str = match String::from_utf8(data) {
                        Ok(s) => s,
                        Err(_) => continue,
                    };

                    if let Ok(envelope) = serde_json::from_str::<Envelope>(&json_str) {
                        let app = app.clone();
                        let state = state.clone();
                        tauri::async_runtime::spawn(async move {
                            let _ = handle_incoming_message(app, state, envelope).await;
                        });
                    }
                }
                None => break,
            }
        }
    }

    if was_connected {
        on_graphic_disconnected(&app, &state);
    }
}

// ============================================================================
// 接続状態管理
// ============================================================================

#[cfg(target_os = "windows")]
fn on_graphic_disconnected(app: &AppHandle, state: &SharedState) {
    let labels: Vec<String> = {
        let guard = state.lock();
        guard.open_windows.keys().cloned().collect()
    };
    for label in labels {
        let _ = window::close_window_by_label(app, state, &label);
    }
}

// ============================================================================
// メッセージハンドリング
// ============================================================================

/// Unityから受信したメッセージを処理する。
#[cfg(target_os = "windows")]
pub async fn handle_incoming_message(
    app: AppHandle,
    state: SharedState,
    envelope: Envelope,
) -> Result<(), String> {
    if !envelope.event.starts_with("system:") && !state.lock().graphic_connected {
        return Ok(());
    }
    match envelope.event.as_str() {
        "world:select_object" => {
            let payload: OpenWindowPayload =
                serde_json::from_value(envelope.payload).map_err(|err| err.to_string())?;
            window::open_window(app, state, payload).await?;
        }
        "game:time_update" => {
            let mut guard = state.lock();
            guard.game_time = Some(envelope.payload.clone());
            drop(guard);
            let _ = app.emit("game:time_update", envelope.payload);
        }
        "edit_path:select_node" => {
            let _ = app.emit("edit_path:select_node", envelope.payload);
        }
        "tutorial:applied" => {
            let _ = app.emit_to("tutorial", "tutorial:applied", envelope.payload);
        }
        "edit_path:update_shape" => {
            let _ = app.emit("edit_path:update_shape", envelope.payload);
        }
        "edit_path:select_merge_target" => {
            // パス結合の相手選択モードで選ばれたパスをパス詳細パネルへ中継する。
            // パネルは結合プレビュー(dry_run)を取得して長さを表示し、確定を待つ。
            let _ = app.emit("edit_path:select_merge_target", envelope.payload);
        }
        "edit_road:toggle_path" => {
            // 道路の区間編集モードでクリックされた区間を道路詳細パネルへ中継する。
            // 所属の付け外しはパネルが道路APIで行い、結果の全集合をUnityへ送り返す。
            let _ = app.emit("edit_road:toggle_path", envelope.payload);
        }
        "edit_road:ended" => {
            // Unity の区間編集モードが終わった(完了・Esc・別のモードへの切り替え・閉じたパネルの
            // edit_road:cancel など理由を問わない)ことを道路詳細パネルへ中継する。
            // パネルは road_id が自分の道路なら、何も送り返さずに編集中の表示を落とす。
            let _ = app.emit("edit_road:ended", envelope.payload);
        }
        "world:roads_changed" => {
            // Unity の操作(ブルドーザーでの区間の撤去など)で道路の所属・存在が変わった可能性を、
            // UI の道路の変更通知 roads:changed (roadApi.ts の ROADS_CHANGED_EVENT)として全ウィンドウへ流す。
            // 道路詳細・パス詳細・都市マップはこれを受けて道路を取り直す。Unity は自分で道路一覧を取り直すので
            // world:refresh_roads は送り返さない。
            let _ = app.emit("roads:changed", envelope.payload);
        }
        "edit_path:select_split_point" => {
            // パス分割の分割点選択モードで選ばれた地点をパス詳細パネルへ中継する。
            // パネルは分割プレビュー(dry_run)を取得して前半・後半の長さを表示し、確定を待つ。
            let _ = app.emit("edit_path:select_split_point", envelope.payload);
        }
        "edit_building:select_parcel" => {
            let _ = app.emit("edit_building:select_parcel", envelope.payload);
        }
        "edit_building:placement_state" => {
            let _ = app.emit("edit_building:placement_state", envelope.payload);
        }
        "edit_building:select_point" => {
            // 直接配置モード: 建物建設モード中に土地区画外(地形)がクリックされたとき、
            // そのワールド座標をUIへ中継する。UIはこの地点を中心とする矩形底面のプレビューを
            // バックエンドに問い合わせる。
            let _ = app.emit("edit_building:select_point", envelope.payload);
        }
        "edit_building:invalid_parcel" => {
            let _ = app.emit("edit_building:invalid_parcel", envelope.payload);
        }
        "building_construction:copy_source" => {
            // 既存建物コピー建設: コピー元選択モードで選ばれた建物の variant / floors /
            // visual_variant を建物建設パネルへ中継する。パネルはこれをシードして
            // STATE_EDIT_BUILDING の区画選択フローへ遷移する。
            let _ = app.emit("building_construction:copy_source", envelope.payload);
        }
        "edit_building:update_direct_angle" => {
            // 直接配置モード: 中ボタンドラッグによる回転が終了(リリース)し、
            // Unity側が確定した回転角(度)をUIへ中継する。UIは回転ステッパーを更新する。
            let _ = app.emit("edit_building:update_direct_angle", envelope.payload);
        }
        "edit_parcel:select_point" => {
            let _ = app.emit("edit_parcel:select_point", envelope.payload);
        }
        "edit_parcel:select_path" => {
            let _ = app.emit("edit_parcel:select_path", envelope.payload);
        }
        "edit_parcel:select_parcel" => {
            let _ = app.emit("edit_parcel:select_parcel", envelope.payload);
        }
        "edit_parcel:set_mode" => {
            // 3Dビューのラジアルメニュー(Ctrl)で土地区画の作成モードが切り替わったことを
            // 作成パネルへ中継する。作成モードの真実源はUnity側にあり、パネルは入力欄の構成と
            // 選択済み表示をこれに合わせる(モード据え置きで届いた場合は「選択のリセット」の意味)。
            let _ = app.emit("edit_parcel:set_mode", envelope.payload);
        }
        "edit_parcel:update_shape" => {
            let _ = app.emit("edit_parcel:update_shape", envelope.payload);
        }
        "edit_resource:update_shape" => {
            let _ = app.emit("edit_resource:update_shape", envelope.payload);
        }
        "edit_grading:update_shape" => {
            let _ = app.emit("edit_grading:update_shape", envelope.payload);
        }
        "edit_transit:update_points" => {
            let _ = app.emit("edit_transit:update_points", envelope.payload);
        }
        "edit_zone:selected" => {
            let _ = app.emit("edit_zone:selected", envelope.payload);
        }
        "edit_zone:request_merge" => {
            // 地区編集モードの統合ツールで統合元・統合先が決まったことをUIへ中継する。
            // 地区がひとつ消える不可逆操作なので、Unityは送信せず地区詳細パネルの確定ボタンを待つ
            // (アイコンパネルがこれを受けて統合先の地区詳細ウィンドウを開く)。
            let _ = app.emit("edit_zone:request_merge", envelope.payload);
        }
        "edit_prop:update_instances" => {
            let _ = app.emit("edit_prop:update_instances", envelope.payload);
        }
        "edit_prop:update_shape" => {
            let _ = app.emit("edit_prop:update_shape", envelope.payload);
        }
        "edit_prop:placement_settings" => {
            // 3D側の操作(中ボタン回転・PageUp/PageDown)で変わった回転角・高さをプロップ配置パネルへ中継する。
            let _ = app.emit("edit_prop:placement_settings", envelope.payload);
        }
        "remove_object:request_confirm" => {
            // ブルドーザで範囲配置プロップ(森など)をクリックしたときの削除確認要求。
            // 常駐するコックピットだけが確認ダイアログを出し、結果を remove_object:confirm_result で返す。
            let _ = app.emit_to(window::LABEL_COCKPIT, "remove_object:request_confirm", envelope.payload);
        }
        "view:current_state" => {
            let _ = app.emit("view:current_state", envelope.payload);
        }
        "view:object_filter_state" => {
            // オブジェクト単位の表示フィルタ(建設プロジェクトのタグ単位/個別の表示切替)の
            // 現在値。表示状態の正はUnity側にあり、複数のパネルが同じ状態を見るため全ウィンドウへ流す。
            let _ = app.emit("view:object_filter_state", envelope.payload);
        }
        "weather:state" => {
            let _ = app.emit_to(window::LABEL_COCKPIT, "weather:state", envelope.payload);
        }
        "epoch:darkened" => {
            let current = state.lock().epoch_presentation_id.clone();
            if current.is_some() && envelope.payload.get("id").and_then(|id| id.as_str()) == current.as_deref() {
                let _ = app.emit("epoch:darkened", envelope.payload);
            }
        }
        "view:grid_angle_changed" => {
            let _ = app.emit("view:grid_angle_changed", envelope.payload);
        }
        "view:grid_toggled" => {
            // 3Dビューのラジアルメニューでグリッド表示がトグルされたことをUIへ中継する。
            // 道路建設パネルがグリッド設定UIの表示状態を同期するために受信する。
            let _ = app.emit("view:grid_toggled", envelope.payload);
        }
        "camera:state" => {
            // 3Dカメラの現在位置と視線方位を2D都市マップへ中継する。
            // camera:query_state への応答で、問い合わせ1回につき1回だけ流れる。
            let _ = app.emit("camera:state", envelope.payload);
        }
        "camera:thumbnail_captured" => {
            // camera:capture_thumbnail(保存直前の撮影)への応答。頼んだウィンドウが id で自分の要求を見分ける。
            let _ = app.emit("camera:thumbnail_captured", envelope.payload);
        }
        "system:ping" => {
            send_to_parent(
                &state,
                make_envelope("tauri", "parent", "system:pong", json!({})),
            )?;
        }
        "system:shutdown" => {
            app.exit(0);
        }
        "system:focus_gained" => {
            // Unity がフォアグラウンドになった直後の通知。フルスクリーン扱いの Unity が
            // 活性化するとシェルが topmost の UI を裏へ回すため、その後で UI 全体の
            // 最前面を打ち直す。「Unity をクリックすれば UI が必ず戻る」ための復帰経路。
            window::reassert_topmost(&app);
        }
        "game:loaded" => {
            let loading_id = envelope.payload.get("loading_id").and_then(|v| v.as_u64());
            let mut guard = state.lock();
            if guard.is_loading && guard.loading_started && !guard.awaiting_parameter_setup && loading_id == Some(guard.loading_id) {
                guard.graphic_load_ready = true;
            }
        }
        "system:graphic_ready" => {
            let expected = crate::version::definition();
            let compatible = envelope.payload["ipc_contract_version"] == expected["ipc_contract_version"]
                && envelope.payload["core_compatible"] == true;
            {
                let mut guard = state.lock();
                guard.graphic_version = envelope.payload["frontend_version"].as_str().map(str::to_owned);
                guard.graphic_contract_error = if compatible { None } else { Some("IPC_CONTRACT_MISMATCH".into()) };
                guard.graphic_connected = compatible;
            }
            let mut reply = expected;
            reply["accepted"] = json!(compatible);
            send_to_parent(&state, make_envelope("tauri", "parent", "system:ready", reply))?;
            if !compatible {
                let _ = app.emit("compatibility-error", "IPC_CONTRACT_MISMATCH");
                return Ok(());
            }
            let connected = state.lock().backend_health.connected;
            crate::publish_backend_connection(&app, &state, connected);

            // Unity の起動完了通知。初回起動と、クラッシュ後にスーパーバイザ
            // (genesis.py)が Unity を自動再起動した場合の両方で送られてくる。
            // ポップアップの一掃は2回目以降(=クラッシュ後の再起動)に限る。
            // 再起動時は前セッションの編集モードや選択対象に依存するポップアップが
            // 無効になっているため一掃が必要だが、初回起動時に一掃すると、
            // Unity より先に起動が完了した UI でユーザーが操作中のウィンドウ
            // (トップメニューから開いたロード画面など)まで閉じてしまう。
            // データ再同期は Unity 自身がバックエンドから行うため、ここでは
            // 表示リセットとフロントエンドへの通知のみを行う。
            let is_reconnect = {
                let mut guard = state.lock();
                let seen = guard.graphic_ready_seen;
                guard.graphic_ready_seen = true;
                guard.graphic_connected = true;
                seen
            };
            if is_reconnect {
                window::close_all_popup_windows(&app, &state);
            }
            let (loading, started, has_session) = {
                let guard = state.lock();
                (guard.is_loading, guard.loading_started, guard.last_save_id.is_some())
            };
            if loading && started {
                crate::send_game_start_ipc(&state, &app);
            } else if !loading && has_session {
                let app = app.clone();
                let state = state.clone();
                std::thread::spawn(move || {
                    if crate::begin_initial_loading(&app, &state, false).is_ok() {
                        crate::send_game_start_ipc(&state, &app);
                    }
                });
            }
            // 起動直後の Unity は自分をフォアグラウンドにするため、UI が裏に回りうる。
            window::reassert_topmost(&app);
            let _ = app.emit("graphic-reconnected", envelope.payload);
        }
        "system:graphic_stopped" => {
            // Unity の正常終了(Editor の Play 停止を含む)。クラッシュでは届かない。
            // 開発時の Unity なし起動で描画完了を待っている最中に Unity が止まると
            // game:loaded は二度と来ないので、その場で Tips を開始ボタンへ切り替える。
            let mut guard = state.lock();
            guard.graphic_connected = false;
            #[cfg(all(debug_assertions, dev))]
            {
                if guard.is_loading && guard.loading_started && !guard.awaiting_parameter_setup {
                    guard.graphic_load_ready = true;
                }
            }
        }
        "notification:show" => {
            if let Some(win) = app.get_webview_window(crate::window::LABEL_COCKPIT) {
                let _ = win.emit("show-toast", &envelope.payload);
            }
        }
        _ => {
            send_to_parent(
                &state,
                make_envelope(
                    "tauri",
                    "parent",
                    "ui:error",
                    json!({ "message": format!("unsupported event: {}", envelope.event) }),
                ),
            )?;
        }
    }

    Ok(())
}

fn iso_timestamp() -> String {
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_else(|_| Duration::from_secs(0))
        .as_secs();
    format!("{now}")
}

#[cfg(all(test, target_os = "windows"))]
mod tests {
    use super::*;

    /// 実運用の名前と衝突しないよう、テストごと・プロセスごとに別名の共有メモリを作る。
    fn channel(tag: &str) -> MmfChannel {
        let pid = std::process::id();
        MmfChannel::create(
            &format!("genesis-ipc-test-{tag}-{pid}"),
            &format!("genesis-ipc-test-mutex-{tag}-{pid}"),
            &format!("genesis-ipc-test-event-{tag}-{pid}"),
            true,
        )
        .unwrap()
    }

    #[test]
    fn message_after_wrap_is_read_without_another_write() {
        let ch = channel("wrap");
        let payload = |b: u8| vec![b; 400_000];
        ch.write_message(&payload(b'a')).unwrap();
        ch.write_message(&payload(b'b')).unwrap();
        assert_eq!(ch.read_message().unwrap()[0], b'a');
        assert_eq!(ch.read_message().unwrap()[0], b'b');

        // 末尾に収まらず先頭へラップする。読み手はラップマーカーを越えて同じ呼び出しで読めること。
        ch.write_message(&payload(b'c')).unwrap();
        assert_eq!(ch.read_message().unwrap()[0], b'c');
        assert!(ch.read_message().is_none());
    }

    #[test]
    fn wrap_never_fills_up_to_read_pos() {
        let ch = channel("full");
        let first = vec![0u8; 600_000];
        ch.write_message(&first).unwrap();
        ch.read_message().unwrap();

        // 先頭の空きは ReadPos(600_004)ちょうど。埋め切ると空と区別できないため拒否する。
        let exact = vec![0u8; 600_000];
        assert_eq!(ch.write_message(&exact), Err("MMF buffer full".to_string()));
    }

    #[test]
    fn corrupted_length_discards_backlog_instead_of_reading_out_of_bounds() {
        let ch = channel("corrupt");
        ch.write_message(b"hello").unwrap();
        unsafe { write_i32(ch.inner.buffer, HEADER_SIZE, -1) };
        assert!(ch.read_message().is_none());

        ch.write_message(b"next").unwrap();
        assert_eq!(ch.read_message().unwrap(), b"next");
    }
}
