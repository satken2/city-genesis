/**
 * React Context for application-wide font size. The setting is body text size in px, exposed as 1rem through the
 * document root CSS variable (--font-size-base). Components use rem so the UI scales proportionally to body text
 * (1rem). Read settings.json; SettingsWindow handles saving. All components can access the current size and
 * setter through useFontSize().
 *
 * Broadcast the Tauri font-size-changed event to all windows on changes. Each window's FontSizeProvider listens
 * and updates immediately. Windows opened later read the settings file on startup.
 */

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useLayoutEffect,
  useState,
  type ReactNode,
} from "react";
import { emit, listen } from "@tauri-apps/api/event";
import { loadAppSettings } from "../utils/settings";

/** Tauri event name for synchronization between windows. */
const EVENT_FONT_SIZE_CHANGED = "font-size-changed";

/**
 * Font size presets (body text size in px). key also serves as an i18n translation key. Medium is the default
 * without a settings file; match Rust's default in settings.rs.
 */
export const FONT_SIZE_PRESETS: Record<string, number> = {
  small: 10,
  medium: 13,
  large: 16,
};

type FontSizeContextValue = {
  /** Current font size in px. */
  fontSize: number;
  /** Change the font size. */
  setFontSize: (size: number) => void;
  /** Preset key matching the current font size (null if none). */
  presetKey: string | null;
};

const FontSizeContext = createContext<FontSizeContextValue>({
  fontSize: FONT_SIZE_PRESETS.medium,
  setFontSize: () => {},
  presetKey: "medium",
});

/**
 * Look up the preset key for a font size.
 */
function findPresetKey(size: number): string | null {
  for (const [key, value] of Object.entries(FONT_SIZE_PRESETS)) {
    if (value === size) {
      return key;
    }
  }
  return null;
}

/**
 * Provider exposing font size through Context. On mount, restore from settings.json and update --font-size-base
 * on the document root. Synchronize the CSS variable and Tauri events on every change.
 *
 * For multi-window synchronization, setFontSize emits changes to all windows; listen receives other windows'
 * events and updates state. SettingsWindow saves the file; this provider only reads and broadcasts.
 */
export function FontSizeProvider({ children }: { children: ReactNode }) {
  const [fontSize, setFontSizeState] = useState(FONT_SIZE_PRESETS.medium);

  /**
   * Read font size from settings on mount. Use the default (medium) if the file is missing or its value is
   * invalid.
   */
  useEffect(() => {
    let cancelled = false;
    loadAppSettings()
      .then((settings) => {
        if (cancelled) return;
        const loaded = settings.font_size;
        if (Number.isFinite(loaded) && loaded >= 8 && loaded <= 32) {
          setFontSizeState(loaded);
        }
      })
      .catch((err) => {
        console.error("failed to load font size from settings", err);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const setFontSize = useCallback((size: number) => {
    const clamped = Math.max(8, Math.min(32, Math.round(size)));
    setFontSizeState(clamped);
    // Notify all windows of the font size change.
    void emit(EVENT_FONT_SIZE_CHANGED, clamped);
  }, []);

  /**
   * Apply font size changes to the CSS variable. Use a layout effect before child useEffects so panels measuring
   * content to resize their windows do not measure with the old font size.
   */
  useLayoutEffect(() => {
    document.documentElement.style.setProperty("--font-size-base", `${fontSize}px`);
  }, [fontSize]);

  /**
   * Listen for font size changes from other windows. Tauri emit delivers to every window, so changes in settings
   * immediately affect popups too.
   */
  useEffect(() => {
    const unlisten = listen<number>(EVENT_FONT_SIZE_CHANGED, (event) => {
      const value = event.payload;
      if (Number.isFinite(value) && value >= 8 && value <= 32) {
        setFontSizeState(value);
      }
    });

    return () => {
      void unlisten.then((fn) => fn());
    };
  }, []);

  const presetKey = findPresetKey(fontSize);

  return (
    <FontSizeContext.Provider value={{ fontSize, setFontSize, presetKey }}>
      {children}
    </FontSizeContext.Provider>
  );
}

/**
 * Hook to read/change font size. Available only within FontSizeProvider.
 */
export function useFontSize() {
  return useContext(FontSizeContext);
}
