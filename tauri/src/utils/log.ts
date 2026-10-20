import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";

/**
 * Write one INFO line to Rust's file log (city-genesis/tauri.log beside the executable). Production WebView
 * console access is limited; track critical flows such as epoch execution that can stop without visible errors.
 * Prefix the window label to identify the source. Ignore forwarding failures, such as during window destruction.
 */
export function logInfoToFile(message: string): void {
  const label = getCurrentWindow().label;
  void invoke("log_frontend_info", { message: `[${label}] ${message}` }).catch(() => {});
}
