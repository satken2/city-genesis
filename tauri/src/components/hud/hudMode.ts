/**
 * Persistent HUD mode (bulldozer), shared across windows
 *
 * Bulldozer toggles from the cockpit.
 * Persistent modes occupy Unity's rendering state. Synchronize a single state
 * across all HUD windows using Tauri's global `hud:mode_changed` event as the source of truth.
 *
 * The component initiating a mode change handles its side effects (`STATE_*` to Unity, hint text, session
 * reload). Other windows only synchronize their display state.
 * Whenever an editing window opens, Rust (`apply_mutex_rules` in `window.rs`) sends a reset (`mode: null`)
 * regardless of entry point. The opened window overwrites Unity's state.
 */

import { useEffect, useState } from "react";
import { emit, listen } from "@tauri-apps/api/event";

/** Persistent HUD mode. null is normal state with the mode off. */
export type HudMode = "bulldozer" | null;

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
 * Reset when the city changes or the session disconnects, as Unity starts in STATE_NORMAL.
 */
export function useHudMode(): HudMode {
  const [mode, setMode] = useState<HudMode>(null);

  useEffect(() => {
    localListeners.add(setMode);
    let disposed = false;
    let sessionId: string | undefined;
    const cleanup: (() => void)[] = [];
    const keep = (stop: () => void) => {
      if (disposed) stop();
      else cleanup.push(stop);
    };

    listen<{ mode: HudMode }>(HUD_EVENT_MODE_CHANGED, (event) => {
      if (!disposed) setMode(event.payload.mode);
    })
      .then(keep)
      .catch(console.error);

    listen<{ current_save_id: string | null; session?: { session_id?: string } | null }>("session-state", (event) => {
      if (disposed) return;
      const nextSessionId = event.payload.session?.session_id;
      if (event.payload.current_save_id == null || (sessionId !== undefined && nextSessionId !== sessionId)) setMode(null);
      sessionId = nextSessionId;
    })
      .then(keep)
      .catch(console.error);

    return () => {
      disposed = true;
      localListeners.delete(setMode);
      cleanup.forEach(stop => stop());
    };
  }, []);

  return mode;
}
