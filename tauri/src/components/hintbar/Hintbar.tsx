/**
 * Persistent hint bar at the top of the screen
 *
 * Show lightweight hints and guidance in a rounded popup-style frame at the top center.
 *
 * - Fixed at the top center, occupying 55% of the screen width.
 * - Height allows about two lines of text.
 * - No close button; users cannot dismiss it.
 * - Click-through: Rust sets `set_ignore_cursor_events` when creating the window.
 * - Visibility requires both:
 *   - `current_save_id` in `session-state`: remain visible while save data is loaded, even without text.
 *   - `hintbar_visible` in settings: changes apply immediately via `settings-updated`.
 *
 * Update text through `setHintbarText()`, which emits the global Tauri event `hintbar:set-text`.
 */

import { useEffect, useMemo, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { loadAppSettings, type AppSettings } from "../../utils/settings";
import "./Hintbar.css";

export type HintbarPayload = {
  text: string;
  /** If specified, replace only when the current display matches this string (`clearHintbarTextIf`). */
  only_if?: string;
};

type SessionStatePayload = {
  current_save_id: string | null;
  isLoading?: boolean;
};

export function Hintbar() {
  const appWindow = useMemo(() => getCurrentWindow(), []);
  const [sessionActive, setSessionActive] = useState(false);
  const [enabled, setEnabled] = useState(true);
  const [text, setText] = useState("");

  useEffect(() => {
    let unlistenText: (() => void) | undefined;
    let unlistenSession: (() => void) | undefined;
    let unlistenSettings: (() => void) | undefined;
    let unlistenLoading: (() => void) | undefined;

    listen("initial-loading:finished", () => setSessionActive(true))
      .then((fn) => { unlistenLoading = fn; }).catch(console.error);

    listen<HintbarPayload>("hintbar:set-text", (event) => {
      const { text: next, only_if: onlyIf } = event.payload;
      setText((prev) => (onlyIf == null || prev === onlyIf ? next : prev));
    })
      .then((fn) => {
        unlistenText = fn;
      })
      .catch(console.error);

    appWindow
      .listen<SessionStatePayload>("session-state", (event) => {
        setSessionActive(event.payload.current_save_id != null && !event.payload.isLoading);
      })
      .then((fn) => {
        unlistenSession = fn;
      })
      .catch(console.error);

    loadAppSettings()
      .then((settings) => {
        setEnabled(settings.hintbar_visible !== false);
      })
      .catch(console.error);

    listen<AppSettings>("settings-updated", (event) => {
      setEnabled(event.payload.hintbar_visible !== false);
    })
      .then((fn) => {
        unlistenSettings = fn;
      })
      .catch(console.error);

    return () => {
      unlistenText?.();
      unlistenSession?.();
      unlistenSettings?.();
      unlistenLoading?.();
    };
  }, [appWindow]);

  const visible = sessionActive && enabled;

  /**
   * Stopping React rendering leaves the transparent always-on-top window in place.
   * Show or hide the native window itself to match visibility conditions.
   */
  useEffect(() => {
    if (visible) {
      appWindow.show().catch(() => {});
    } else {
      appWindow.hide().catch(() => {});
    }
  }, [appWindow, visible]);

  if (!visible) {
    return null;
  }

  return (
    <div className="hintbar-root">
      <div className="hintbar-frame">
        <span className="hintbar-text">{text}</span>
      </div>
    </div>
  );
}
