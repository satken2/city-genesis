import { useCallback, useEffect, useRef, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { IPC_EVENT_WEATHER_STATE, queryWeather, setWeather, type WeatherState } from "../../utils/ipc";
import { AmbienceVolumeSync } from "../../utils/weatherAudio";

export function useWeather(visible: boolean, scenarioMode: boolean) {
  const [weather, updateWeather] = useState<WeatherState | null>(null);
  const [failed, setFailed] = useState(false);
  const previousScenarioMode = useRef(scenarioMode);

  useEffect(() => {
    const sound = new AmbienceVolumeSync();
    return () => { sound.destroy(); };
  }, []);

  useEffect(() => {
    if (!visible) { updateWeather(null); setFailed(false); return; }
    let disposed = false;
    let unlisten: (() => void) | undefined;
    void listen<WeatherState>(IPC_EVENT_WEATHER_STATE, ({ payload }) => {
      if (disposed || !payload || typeof payload.id !== "string" || !Array.isArray(payload.presets)) return;
      updateWeather(payload);
      setFailed(false);
    }).then((fn) => {
      if (disposed) { fn(); return; }
      unlisten = fn;
      // Query only once when display starts. Unity sends weather:state on startup and weather changes.
      // Periodic queries would accumulate in MMF while Unity is absent and eventually fill it.
      void queryWeather().catch(console.error);
    }).catch(console.error);
    return () => { disposed = true; unlisten?.(); };
  }, [visible]);

  useEffect(() => {
    if (previousScenarioMode.current && !scenarioMode && visible)
      void setWeather(null).catch(() => setFailed(true));
    previousScenarioMode.current = scenarioMode;
  }, [scenarioMode, visible]);

  const select = useCallback(async (id: string | null) => {
    if (!scenarioMode || !visible) return;
    setFailed(false);
    try { await setWeather(id); } catch { setFailed(true); }
  }, [scenarioMode, visible]);

  return { weather, failed, select };
}
