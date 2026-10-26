/**
 * Session state shared by HUD windows (icon panel, cockpit, system tray, display tray, citizen voice)
 *
 * Receive `session-state`, broadcast to all windows by Rust's session polling thread, and return visibility and
 * basic city information.
 * Rust (`set_hud_visible`) controls native window visibility; React also stops rendering through `visible` to
 * avoid briefly showing the previous session's content on display.
 */
import { GAME_VERIFIED, MAX_UNVERIFIED_POPULATION, PLAY_MODE_NORMAL } from "../../constants";


import { useEffect, useMemo, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { listen } from "@tauri-apps/api/event";

type SessionStatePayload = {
  current_save_id: string | null;
  session?: Record<string, unknown>;
  city?: Record<string, unknown>;
  apiBaseUrl: string;
  isLoading?: boolean;
};

export type HudSession = {
  /** Whether a session (city in play) exists and the HUD may render */
  visible: boolean;
  apiBaseUrl: string;
  /** Play mode: normal, scenario authoring, or tutorial */
  playMode: number;
  /** Whether operations other than construction are locked because City Hall is missing (before founding the city) */
  isLocked: boolean;
  /** Whether the core runs without Steam authentication (debug runs count as verified) */
  unverified: boolean;
  /** Whether epoch execution is blocked because population exceeds the limit without Steam authentication (the core rejects execution under the same condition) */
  populationLimitReached: boolean;
  /** Debug execution; show the scenario editor entry in normal mode too. */
  debugExecution: boolean;
  cityName: string;
  /** Days since January 1, 1800; null before retrieval. */
  currentDate: number | null;
  /** Realized climate of the most recently started epoch. null before execution or retrieval. */
  epochTemperature: number | null;
  epochSunlight: number | null;
  epochPrecipitation: number | null;
  /** m/s */
  epochWindSpeed: number | null;
  /** Epoch execution in progress; disable all HUD buttons while running. */
  epochExecuting: boolean;
};

export function useHudSession(): HudSession {
  const appWindow = useMemo(() => getCurrentWindow(), []);
  const [visible, setVisible] = useState(false);
  const [apiBaseUrl, setApiBaseUrl] = useState("");
  const [playMode, setPlayMode] = useState<number>(PLAY_MODE_NORMAL);
  const [cityHallPlaced, setCityHallPlaced] = useState(true);
  const [unverified, setUnverified] = useState(false);
  const [populationLimitReached, setPopulationLimitReached] = useState(false);
  const [debugExecution, setDebugExecution] = useState(false);
  const [cityName, setCityName] = useState("");
  const [currentDate, setCurrentDate] = useState<number | null>(null);
  const [epochTemperature, setEpochTemperature] = useState<number | null>(null);
  const [epochSunlight, setEpochSunlight] = useState<number | null>(null);
  const [epochPrecipitation, setEpochPrecipitation] = useState<number | null>(null);
  const [epochWindSpeed, setEpochWindSpeed] = useState<number | null>(null);
  const [epochExecuting, setEpochExecuting] = useState(false);

  useEffect(() => {
    let unlistenSession: (() => void) | undefined;
    let unlistenRename: (() => void) | undefined;
    let unlistenEpoch: (() => void) | undefined;
    let unlistenLoading: (() => void) | undefined;

    listen("initial-loading:finished", () => {
      setVisible(true);
      appWindow.show().catch(console.error);
    }).then((fn) => { unlistenLoading = fn; }).catch(console.error);

    listen<SessionStatePayload>("session-state", (event) => {
      const payload = event.payload;
      const shouldShow = payload.current_save_id != null && !payload.isLoading;
      // Rust (`set_hud_visible`) also switches visibility at session start/end, but
      // reconciling with current state on each poll prevents a missed event from
      // leaving a window permanently hidden or visible.
      if (shouldShow) {
        appWindow.show().catch(() => {});
      } else {
        appWindow.hide().catch(() => {});
      }
      setVisible(shouldShow);
      setApiBaseUrl(payload.apiBaseUrl);
      setPlayMode((payload.session?.play_mode as number) ?? PLAY_MODE_NORMAL);
      setDebugExecution((payload.session?.debug_execution as boolean) ?? false);

      const cityData = payload.city?.city as Record<string, unknown> | undefined;
      setCityName((cityData?.name as string) ?? "");
      setCurrentDate((cityData?.current_date as number) ?? null);
      setEpochTemperature((cityData?.epoch_temperature as number) ?? null);
      setEpochSunlight((cityData?.epoch_sunlight as number) ?? null);
      setEpochPrecipitation((cityData?.epoch_precipitation as number) ?? null);
      setEpochWindSpeed((cityData?.epoch_wind_speed as number) ?? null);
      setCityHallPlaced((cityData?.city_hall_placed as boolean) ?? false);
      const sessionUnverified = payload.session?.verification_status !== GAME_VERIFIED;
      setUnverified(sessionUnverified);
      setPopulationLimitReached(sessionUnverified && ((cityData?.population as number) ?? 0) > MAX_UNVERIFIED_POPULATION);
    })
      .then((fn) => {
        unlistenSession = fn;
      })
      .catch(console.error);

    // Apply city name changes without waiting for the next session poll.
    // (Sent by the city_government window, CityGovernmentWindow.tsx.)
    listen<{ name: string }>("city:renamed", (event) => {
      setCityName(event.payload.name);
    })
      .then((fn) => {
        unlistenRename = fn;
      })
      .catch(console.error);

    // Epoch execution start/end, sent by epoch_confirm (EpochConfirmWindow.tsx).
    appWindow
      .listen<{ executing: boolean }>("epoch:executing", (event) => {
        setEpochExecuting(event.payload.executing);
      })
      .then((fn) => {
        unlistenEpoch = fn;
      })
      .catch(console.error);

    return () => {
      unlistenSession?.();
      unlistenRename?.();
      unlistenEpoch?.();
      unlistenLoading?.();
    };
  }, [appWindow]);

  return {
    visible,
    apiBaseUrl,
    playMode,
    isLocked: !cityHallPlaced && playMode === PLAY_MODE_NORMAL,
    unverified,
    populationLimitReached,
    debugExecution,
    cityName,
    currentDate,
    epochTemperature,
    epochSunlight,
    epochPrecipitation,
    epochWindSpeed,
    epochExecuting,
  };
}

