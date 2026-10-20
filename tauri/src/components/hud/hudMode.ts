/**
 * Persistent HUD modes (bulldozer and district editing), shared across windows
 *
 * Bulldozer toggles from the cockpit. `district` represents Unity's 3D district editing mode; currently no UI
 * enables it, since districts are edited in `zone_edit`.
 * Persistent modes occupy Unity's rendering state and cannot be active together. Synchronize a single state
 * across all HUD windows using Tauri's global `hud:mode_changed` event as the source of truth.
 *
 * The component initiating a mode change handles its side effects (`STATE_*` to Unity, hint text, session
 * reload). Other windows only synchronize their display state.
 * Whenever an editing window opens, Rust (`apply_mutex_rules` in `window.rs`) sends a reset (`mode: null`)
 * regardless of entry point. The opened window overwrites Unity's state.
 */

import { useEffect, useState } from "react";
import { emit, listen } from "@tauri-apps/api/event";

/** Mutually exclusive persistent mode. null is normal state with both modes off. */
export type HudMode = "bulldozer" | "district" | null;

export const HUD_EVENT_MODE_CHANGED = "hud:mode_changed";

/**
 * Subscribers in this window, updated immediately without waiting for a Tauri event round trip.
 * An echoed identical value is harmless.
 */
const localListeners = new Set<(mode: HudMode) => void>();

/** Switch the persistent mode and notify all HUD windows. */
export async function setHudMode(mode: HudMode): Promise<void> {
  localListeners.forEach((listener) => listener(mode));
  await emit(HUD_EVENT_MODE_CHANGED, { mode });
}

/**
 * Subscribe to the current persistent mode.
 * Reset when the session disconnects, as Unity restarts in STATE_NORMAL.
 */
export function useHudMode(): HudMode {
  const [mode, setMode] = useState<HudMode>(null);

  useEffect(() => {
    localListeners.add(setMode);
    let unlistenMode: (() => void) | undefined;
    let unlistenSession: (() => void) | undefined;

    listen<{ mode: HudMode }>(HUD_EVENT_MODE_CHANGED, (event) => {
      setMode(event.payload.mode);
    })
      .then((fn) => {
        unlistenMode = fn;
      })
      .catch(console.error);

    listen<{ current_save_id: string | null }>("session-state", (event) => {
      if (event.payload.current_save_id == null) setMode(null);
    })
      .then((fn) => {
        unlistenSession = fn;
      })
      .catch(console.error);

    return () => {
      localListeners.delete(setMode);
      unlistenMode?.();
      unlistenSession?.();
    };
  }, []);

  return mode;
}
