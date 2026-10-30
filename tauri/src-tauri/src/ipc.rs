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

// Shared memory size. Must match CAPACITY in Unity's Assets/Scripts/IPC/IpcTransport.cs. CreateOrOpen
// uses the size of the first process to start; a mismatch can make the other side read out of bounds. 8
// KB is insufficient for messages containing hundreds of polygons, such as bulk parcel creation previews.
#[cfg(target_os = "windows")]
const CAPACITY: usize = 1024 * 1024;
#[cfg(target_os = "windows")]
const HEADER_SIZE: usize = 8;
#[cfg(target_os = "windows")]
const BUFFER_SIZE: usize = CAPACITY - HEADER_SIZE;

// ============================================================================
// MmfChannel — wrapper for MemoryMappedFile + Mutex + Event
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
                    // Do not allow equality: filling exactly up to ReadPos would make WritePos == ReadPos indistinguishable from an empty buffer.
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

    /// The caller must hold the Mutex.
    unsafe fn read_message_locked(&self) -> Option<Vec<u8>> {
        let write_pos = read_i32(self.inner.buffer, 0) as usize;
        let mut read_pos = read_i32(self.inner.buffer, 4) as usize;

        if read_pos == write_pos {
            return None;
        }

        // Continue reading messages written at the start after wrapping within the same
        // write notification. Stopping here would leave them unread until the next notification.
        if read_pos + 4 > BUFFER_SIZE || read_i32(self.inner.buffer, HEADER_SIZE + read_pos) == 0 {
            read_pos = 0;
            write_i32(self.inner.buffer, 4, 0);
            if read_pos == write_pos {
                return None;
            }
        }

        let length = read_i32(self.inner.buffer, HEADER_SIZE + read_pos);
        if length <= 0 || length as usize > BUFFER_SIZE - read_pos - 4 {
            // The length is corrupt. Discard unread data without reading out of bounds so subsequent communication can continue.
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

/// Read an i32 from the MMF buffer without alignment issues.
#[cfg(target_os = "windows")]
unsafe fn read_i32(base: *mut u8, offset: usize) -> i32 {
    let mut bytes = [0u8; 4];
    std::ptr::copy_nonoverlapping(base.add(offset), bytes.as_mut_ptr(), 4);
    i32::from_ne_bytes(bytes)
}

/// Write an i32 to the MMF buffer without alignment issues.
#[cfg(target_os = "windows")]
unsafe fn write_i32(base: *mut u8, offset: usize, value: i32) {
    std::ptr::copy_nonoverlapping(value.to_ne_bytes().as_ptr(), base.add(offset), 4);
}

/// Write an i32 through a raw pointer without alignment issues.
#[cfg(target_os = "windows")]
unsafe fn write_i32_raw(ptr: *mut u8, value: i32) {
    std::ptr::copy_nonoverlapping(value.to_ne_bytes().as_ptr(), ptr, 4);
}

#[cfg(target_os = "windows")]
fn to_wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

// ============================================================================
// Public API
// ============================================================================

/// Start IPC communication. Create an MMF writer for
/// UI→Graphic and an MMF reader for Graphic→UI, then
/// start the reader thread to wait for Unity
/// messages. Start IPC communication. Create an MMF
/// writer for UI→Graphic and an MMF reader for Graphic→UI,
/// then start the reader thread to wait for Unity messages.
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

        // Send system.ready first.
        let ready = make_envelope("tauri", "parent", "system:ready", crate::version::definition());
        let _ = send_to_parent(&state, ready);

        // Start the reader thread.
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

/// Send a message to Unity. Write directly to
/// the MMF (acquire Mutex → write → signal Event).
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

/// Send a UI action (such as a mode change or session operation) to Unity.
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

/// Helper for creating an envelope.
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
// Reader thread
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
// Connection state management
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
// Message handling
// ============================================================================

/// Handle a message received from Unity.
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
            // Forward the path selected in merge-target selection mode to the path detail panel. The
            // panel fetches a merge preview (dry_run), displays the length, and waits for confirmation.
            let _ = app.emit("edit_path:select_merge_target", envelope.payload);
        }
        "edit_road:toggle_path" => {
            // Forward the section clicked in road section editing mode to the road detail panel. The panel
            // adds or removes membership through the road API and sends the complete resulting set back to Unity.
            let _ = app.emit("edit_road:toggle_path", envelope.payload);
        }
        "edit_road:ended" => {
            // Notify the road detail panel when Unity leaves section editing mode, regardless of
            // the cause (Done, Esc, another mode, or edit_road:cancel from a closed panel). If
            // road_id matches its road, the panel clears its editing display without sending anything back.
            let _ = app.emit("edit_road:ended", envelope.payload);
        }
        "world:roads_changed" => {
            // Broadcast possible changes to road membership or existence caused by Unity actions
            // (such as bulldozing a section) to all windows as the UI's roads:changed notification
            // (ROADS_CHANGED_EVENT in roadApi.ts). Road detail, path detail, and city map panels
            // then refetch roads. Unity refetches its own road list, so do not echo world:refresh_roads.
            let _ = app.emit("roads:changed", envelope.payload);
        }
        "edit_path:select_split_point" => {
            // Forward the point selected in path split-point selection mode to the path detail panel. The
            // panel fetches a split preview (dry_run), displays both resulting lengths, and waits for confirmation.
            let _ = app.emit("edit_path:select_split_point", envelope.payload);
        }
        "edit_building:select_parcel" => {
            let _ = app.emit("edit_building:select_parcel", envelope.payload);
        }
        "edit_building:placement_state" => {
            let _ = app.emit("edit_building:placement_state", envelope.payload);
        }
        "edit_building:select_point" => {
            // Direct placement mode: when terrain outside a parcel is clicked in
            // building construction mode, forward its world coordinates to the UI. The UI
            // requests a rectangular footprint preview centered on this point from the backend.
            let _ = app.emit("edit_building:select_point", envelope.payload);
        }
        "edit_building:invalid_parcel" => {
            let _ = app.emit("edit_building:invalid_parcel", envelope.payload);
        }
        "building_construction:copy_source" => {
            // Copy building construction: forward the selected source building's variant /
            // floors / visual_variant to the building construction panel. The panel uses
            // these as initial values and enters the STATE_EDIT_BUILDING parcel selection flow.
            let _ = app.emit("building_construction:copy_source", envelope.payload);
        }
        "edit_building:update_direct_angle" => {
            // Direct placement mode: when middle-button drag rotation ends (release), forward
            // Unity's finalized angle in degrees to the UI, which updates the rotation stepper.
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
            // Forward parcel creation mode changes from the 3D view's radial menu (Ctrl) to
            // the creation panel. Unity owns the mode; the panel adjusts its inputs and
            // selection display accordingly (receiving the same mode means reset the selection).
            let _ = app.emit("edit_parcel:set_mode", envelope.payload);
        }
        "edit_parcel:update_shape" => {
            let _ = app.emit("edit_parcel:update_shape", envelope.payload);
        }
        "edit_parcel:update_rotation" => {
            let _ = app.emit("edit_parcel:update_rotation", envelope.payload);
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
            // Forward the source and destination chosen by the district editing mode's merge tool to the UI. Because
            // merging irreversibly removes a district, Unity waits for the district detail panel's confirmation
            // button rather than submitting (the icon panel opens the destination district's detail window on receipt).
            let _ = app.emit("edit_zone:request_merge", envelope.payload);
        }
        "edit_prop:update_instances" => {
            let _ = app.emit("edit_prop:update_instances", envelope.payload);
        }
        "edit_prop:update_shape" => {
            let _ = app.emit("edit_prop:update_shape", envelope.payload);
        }
        "edit_prop:placement_settings" => {
            // Forward rotation and height changes from 3D controls (middle-button rotation / PageUp / PageDown) to the prop placement panel.
            let _ = app.emit("edit_prop:placement_settings", envelope.payload);
        }
        "remove_object:request_confirm" => {
            // Deletion confirmation request when the bulldozer clicks an area-placed prop (such as a forest). Only the
            // persistent cockpit shows a confirmation dialog and returns the result via remove_object:confirm_result.
            let _ = app.emit_to(window::LABEL_COCKPIT, "remove_object:request_confirm", envelope.payload);
        }
        "view:current_state" => {
            let _ = app.emit("view:current_state", envelope.payload);
        }
        "view:object_filter_state" => {
            // Current object visibility filters (construction project tags or individual visibility
            // toggles). Unity owns visibility; broadcast to all windows so multiple panels share the same state.
            let _ = app.emit("view:object_filter_state", envelope.payload);
        }
        "weather:state" => {
            let _ = app.emit_to(window::LABEL_COCKPIT, "weather:state", envelope.payload);
        }
        "epoch:ready" => {
            let current = state.lock().epoch_presentation_id.clone();
            if current.is_some() && envelope.payload.get("id").and_then(|id| id.as_str()) == current.as_deref() {
                let _ = app.emit("epoch:ready", envelope.payload);
            }
        }
        "view:grid_angle_changed" => {
            let _ = app.emit("view:grid_angle_changed", envelope.payload);
        }
        "view:grid_toggled" => {
            // Forward grid visibility toggles from the 3D view's radial menu to the UI.
            // The road construction panel uses this to synchronize its grid settings display.
            let _ = app.emit("view:grid_toggled", envelope.payload);
        }
        "camera:state" => {
            // Forward the current 3D camera position and heading to the 2D city
            // map. Each camera:query_state request produces exactly one response.
            let _ = app.emit("camera:state", envelope.payload);
        }
        "view:ui_visibility" => {
            // Start/end of fly or capture mode. Hide all UI to show only the 3D view while active.
            let visible = envelope.payload.get("visible").and_then(|v| v.as_bool()).unwrap_or(true);
            window::set_ui_cloaked(&app, !visible);
        }
        "citizen:follow_state" => {
            // Forward citizen following start/end (including Esc or entry into editing mode in Unity) to camera buttons in the citizen panel and tracking list.
            let _ = app.emit("citizen:follow_state", envelope.payload);
        }
        "citizen:pick_failed" => {
            // Reason the citizen associated with a clicked vehicle or pedestrian could not be inferred. The persistent cockpit displays a notification.
            let _ = app.emit_to(window::LABEL_COCKPIT, "citizen:pick_failed", envelope.payload);
        }
        "drive:request" => {
            // 3D view drive button. The cockpit holds the selection screen text in the current language and responds with drive:open to start.
            let _ = app.emit_to(window::LABEL_COCKPIT, "drive:request", envelope.payload);
        }
        "camera:thumbnail_captured" => {
            // Response to camera:capture_thumbnail (capture immediately before saving). The requesting window identifies its request by id.
            let _ = app.emit("camera:thumbnail_captured", envelope.payload);
        }
        "asset_manager:models" => {
            // Response to asset_manager:render (asset manager rendering request). The requesting window identifies its request by request_id.
            let _ = app.emit("asset_manager:models", envelope.payload);
        }
        "asset_manager:thumbnail" => {
            // Notification that one thumbnail was written. Read the image by filename with read_asset_thumbnail (do not send images through the MMF).
            let _ = app.emit("asset_manager:thumbnail", envelope.payload);
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
            // Notification immediately after Unity enters the foreground. Activating Unity as a
            // fullscreen window can make the shell send topmost UI windows behind it, so
            // reassert topmost for all UI afterward. This restores the UI whenever Unity is clicked.
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
            // A newly started Unity instance is not in fly or capture mode; restore UI hidden by the previous instance.
            window::set_ui_cloaked(&app, false);
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
            crate::publish_verification(&state);

            // Unity startup completion notification, sent both on first
            // launch and after Tauri automatically restarts Unity following
            // a crash. Clear popups only on subsequent launches (restarts
            // after crashes). Those popups depend on the previous session's
            // editing modes or selection and are no longer valid. Clearing
            // on the first launch would also close windows already in use in
            // the UI that started before Unity (such as a load screen opened
            // from the top menu). Unity resynchronizes its own data from the
            // backend; only reset the display and notify the frontend here.
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
                (guard.is_loading, guard.loading_started, guard.last_session_id.is_some())
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
            // Unity brings itself to the foreground on startup, which can send the UI behind it.
            window::reassert_topmost(&app);
            let _ = app.emit("graphic-reconnected", envelope.payload);
        }
        "system:graphic_stopped" => {
            // Unity shutdown notification (including stopping Play in the Editor); not sent on
            // crashes. If Unity stops while waiting for rendering during development without a Unity
            // launch, game:loaded will never arrive; immediately switch Tips to the Start button. Restore
            // UI here as well, since stopping in fly or capture mode prevents the usual restore notification.
            window::set_ui_cloaked(&app, false);
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

    /// Use distinct shared memory names for each test and process to avoid collisions with production names.
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

        // Wrap to the start when the message does not fit at the end. The reader must cross the wrap marker within the same call.
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

        // Free space at the start is exactly ReadPos (600_004). Reject filling it completely because that would be indistinguishable from an empty buffer.
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
