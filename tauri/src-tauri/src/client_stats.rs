//! System statistics measured on the client's PC: memory and video memory for the 3D (graphics) process,
//! total PC memory, and CPU/GPU names. Tauri measures these because the core may run on another machine.
//! The core's `GET /api/v1/stats/system` returns its process memory and computation time; the window
//! combines both sources. On platforms other than Windows, skip measurement and return null for each value.

use crate::state::SharedState;
use serde_json::Value;

#[tauri::command]
pub async fn get_client_system_stats(state: tauri::State<'_, SharedState>) -> Result<Value, String> {
    let graphic_pid = state.lock().processes.graphic_pid();
    tauri::async_runtime::spawn_blocking(move || platform::collect(graphic_pid))
        .await
        .map_err(|e| e.to_string())
}

#[cfg(not(target_os = "windows"))]
mod platform {
    pub fn collect(_graphic_pid: Option<u32>) -> serde_json::Value {
        serde_json::json!({
            "memory": { "system_total_bytes": null, "used_bytes": null, "graphic": null },
            "vram": null,
            "environment": { "cpu_name": null, "gpu_name": null, "gpu_memory_bytes": null },
        })
    }
}

#[cfg(target_os = "windows")]
mod platform {
    use serde_json::{json, Value};
    use std::collections::HashMap;
    use std::os::windows::process::CommandExt;
    use std::process::{Command, Stdio};
    use std::ptr::null_mut;
    use std::sync::OnceLock;
    use std::time::{Duration, Instant};
    use windows_sys::Win32::Foundation::CloseHandle;
    use windows_sys::Win32::System::Performance::{
        PdhAddEnglishCounterW, PdhCloseQuery, PdhCollectQueryData, PdhGetFormattedCounterArrayW, PdhOpenQueryW,
        PDH_CSTATUS_NEW_DATA, PDH_CSTATUS_VALID_DATA, PDH_FMT_COUNTERVALUE_ITEM_W, PDH_FMT_LARGE, PDH_HCOUNTER,
        PDH_HQUERY, PDH_MORE_DATA,
    };
    use windows_sys::Win32::System::ProcessStatus::{K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS};
    use windows_sys::Win32::System::Registry::{RegGetValueW, HKEY_LOCAL_MACHINE, RRF_RT_REG_SZ};
    use windows_sys::Win32::System::SystemInformation::{GlobalMemoryStatusEx, MEMORYSTATUSEX};
    use windows_sys::Win32::System::Threading::{OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION};

    const CREATE_NO_WINDOW: u32 = 0x0800_0000;

    pub fn collect(graphic_pid: Option<u32>) -> Value {
        let (total, used) = system_memory().map_or((None, None), |(total, used)| (Some(total), Some(used)));
        let graphic = graphic_pid
            .and_then(process_memory)
            .map(|(current, peak)| json!({ "current_bytes": current, "peak_bytes": peak }));
        let (gpu_name, gpu_memory) = gpu_info();
        json!({
            "memory": { "system_total_bytes": total, "used_bytes": used, "graphic": graphic },
            "vram": vram(graphic_pid),
            "environment": { "cpu_name": cpu_name(), "gpu_name": gpu_name, "gpu_memory_bytes": gpu_memory },
        })
    }

    /// Process physical memory usage in bytes: (current, peak since startup). The peak is the high-water mark recorded by the OS.
    fn process_memory(pid: u32) -> Option<(u64, u64)> {
        unsafe {
            let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
            if handle.is_null() {
                return None;
            }
            let mut counters: PROCESS_MEMORY_COUNTERS = std::mem::zeroed();
            counters.cb = std::mem::size_of::<PROCESS_MEMORY_COUNTERS>() as u32;
            let read = K32GetProcessMemoryInfo(handle, &mut counters, counters.cb) != 0;
            CloseHandle(handle);
            read.then(|| (counters.WorkingSetSize as u64, counters.PeakWorkingSetSize as u64))
        }
    }

    /// PC-wide memory in bytes: (installed RAM, used RAM). Used RAM includes other applications and is refreshed on every call.
    fn system_memory() -> Option<(u64, u64)> {
        unsafe {
            let mut status: MEMORYSTATUSEX = std::mem::zeroed();
            status.dwLength = std::mem::size_of::<MEMORYSTATUSEX>() as u32;
            (GlobalMemoryStatusEx(&mut status) != 0)
                .then(|| (status.ullTotalPhys, status.ullTotalPhys - status.ullAvailPhys))
        }
    }

    /// Current dedicated video memory from GPU performance counters (PDH). used_bytes covers
    /// the entire GPU; graphic_bytes covers the 3D process. On PCs with multiple GPUs, select
    /// the one most used by 3D (or the one with the highest usage if 3D is not using any yet).
    fn vram(graphic_pid: Option<u32>) -> Option<Value> {
        let (process_usage, adapter_usage) = unsafe { read_gpu_counters() }?;
        let adapters: HashMap<String, i64> =
            adapter_usage.into_iter().map(|(name, value)| (name.to_lowercase(), value)).collect();
        let mut graphic_by_adapter: HashMap<String, i64> = HashMap::new();
        for (name, value) in &process_usage {
            if let Some((pid, adapter)) = split_process_instance(name) {
                let adapter = adapter.to_lowercase();
                if Some(pid) == graphic_pid && adapters.contains_key(&adapter) {
                    *graphic_by_adapter.entry(adapter).or_default() += value;
                }
            }
        }
        let chosen = graphic_by_adapter
            .iter()
            .max_by_key(|(_, value)| **value)
            .or_else(|| adapters.iter().max_by_key(|(_, value)| **value))
            .map(|(name, _)| name.clone())?;
        Some(json!({ "used_bytes": adapters[&chosen], "graphic_bytes": graphic_by_adapter.get(&chosen) }))
    }

    /// Split a GPU Process Memory instance name, `pid_<PID>_luid_0x…_0x…_phys_<n>`, into a PID and GPU instance name.
    fn split_process_instance(name: &str) -> Option<(u32, &str)> {
        let rest = &name[name.find("pid_")? + "pid_".len()..];
        let (pid, adapter) = rest.split_once('_')?;
        adapter.starts_with("luid_").then_some(())?;
        Some((pid.parse().ok()?, adapter))
    }

    /// Read dedicated video memory usage per process and per GPU as instance-name-to-byte-count maps.
    unsafe fn read_gpu_counters() -> Option<(Vec<(String, i64)>, Vec<(String, i64)>)> {
        let mut query: PDH_HQUERY = null_mut();
        if PdhOpenQueryW(std::ptr::null(), 0, &mut query) != 0 {
            return None;
        }
        let mut process_counter: PDH_HCOUNTER = null_mut();
        let mut adapter_counter: PDH_HCOUNTER = null_mut();
        let collected = PdhAddEnglishCounterW(query, wide(r"\GPU Process Memory(*)\Dedicated Usage").as_ptr(), 0, &mut process_counter) == 0
            && PdhAddEnglishCounterW(query, wide(r"\GPU Adapter Memory(*)\Dedicated Usage").as_ptr(), 0, &mut adapter_counter) == 0
            && PdhCollectQueryData(query) == 0;
        let result = collected.then(|| (read_counter_array(process_counter), read_counter_array(adapter_counter)));
        PdhCloseQuery(query);
        result
    }

    /// Return wildcard counters as an instance-name-to-value map. Values are PDH_FMT_LARGE integers.
    unsafe fn read_counter_array(counter: PDH_HCOUNTER) -> Vec<(String, i64)> {
        let (mut size, mut count) = (0u32, 0u32);
        if PdhGetFormattedCounterArrayW(counter, PDH_FMT_LARGE, &mut size, &mut count, null_mut()) != PDH_MORE_DATA {
            return Vec::new();
        }
        // The buffer contains an array of entries followed by name strings. Allocate in u64 units to align entry boundaries while receiving the size in bytes.
        let mut buffer = vec![0u64; (size as usize).div_ceil(8)];
        let items = buffer.as_mut_ptr() as *mut PDH_FMT_COUNTERVALUE_ITEM_W;
        if PdhGetFormattedCounterArrayW(counter, PDH_FMT_LARGE, &mut size, &mut count, items) != 0 {
            return Vec::new();
        }
        std::slice::from_raw_parts(items, count as usize)
            .iter()
            .filter(|item| matches!(item.FmtValue.CStatus, PDH_CSTATUS_VALID_DATA | PDH_CSTATUS_NEW_DATA))
            .map(|item| (from_wide(item.szName), item.FmtValue.Anonymous.largeValue))
            .collect()
    }

    /// CPU product name. Read ProcessorNameString from the registry only once.
    fn cpu_name() -> Option<&'static str> {
        static NAME: OnceLock<Option<String>> = OnceLock::new();
        NAME.get_or_init(|| {
            let mut buffer = [0u16; 256];
            let mut size = std::mem::size_of_val(&buffer) as u32;
            let status = unsafe {
                RegGetValueW(
                    HKEY_LOCAL_MACHINE,
                    wide(r"HARDWARE\DESCRIPTION\System\CentralProcessor\0").as_ptr(),
                    wide("ProcessorNameString").as_ptr(),
                    RRF_RT_REG_SZ,
                    null_mut(),
                    buffer.as_mut_ptr().cast(),
                    &mut size,
                )
            };
            let length = buffer.iter().position(|&unit| unit == 0).unwrap_or(buffer.len());
            (status == 0).then(|| String::from_utf16_lossy(&buffer[..length]).trim().to_string())
        })
        .as_deref()
    }

    /// NVIDIA driver product name and dedicated memory capacity. Detect only once, without opening a console.
    fn gpu_info() -> (Option<&'static str>, Option<u64>) {
        static INFO: OnceLock<Option<(String, u64)>> = OnceLock::new();
        INFO.get_or_init(read_gpu_info)
            .as_ref()
            .map_or((None, None), |(name, memory)| (Some(name.as_str()).filter(|name| !name.is_empty()), Some(*memory)))
    }

    fn read_gpu_info() -> Option<(String, u64)> {
        let mut child = Command::new("nvidia-smi")
            .args(["--query-gpu=name,memory.total", "--format=csv,noheader,nounits"])
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .creation_flags(CREATE_NO_WINDOW)
            .spawn()
            .ok()?;
        let deadline = Instant::now() + Duration::from_secs(2);
        while child.try_wait().ok()?.is_none() {
            if Instant::now() >= deadline {
                let _ = child.kill();
                let _ = child.wait();
                return None;
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        let output = child.wait_with_output().ok()?;
        let text = String::from_utf8_lossy(&output.stdout);
        let (name, memory) = text.lines().next()?.rsplit_once(',')?;
        Some((name.trim().to_string(), memory.trim().parse::<u64>().ok()? * 1024 * 1024))
    }

    fn wide(text: &str) -> Vec<u16> {
        text.encode_utf16().chain(Some(0)).collect()
    }

    unsafe fn from_wide(text: *const u16) -> String {
        if text.is_null() {
            return String::new();
        }
        let length = (0..).take_while(|&i| *text.add(i) != 0).count();
        String::from_utf16_lossy(std::slice::from_raw_parts(text, length))
    }

    #[cfg(test)]
    mod tests {
        use super::split_process_instance;

        #[test]
        fn process_instance_is_split_into_pid_and_adapter() {
            assert_eq!(
                split_process_instance("pid_1234_luid_0x00000000_0x0000D1A4_phys_0"),
                Some((1234, "luid_0x00000000_0x0000D1A4_phys_0"))
            );
            assert_eq!(split_process_instance("luid_0x00000000_0x0000D1A4_phys_0"), None);
        }
    }
}
