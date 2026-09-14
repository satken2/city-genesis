import { useCallback, useEffect, useRef, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { IPC_EVENT_WEATHER_STATE, queryWeather, setWeather, type WeatherState } from "../../utils/ipc";
import { AmbienceVolumeSync } from "../../utils/weatherAudio";

export function useWeather(visible: boolean, templateMode: boolean) {
  const [weather, updateWeather] = useState<WeatherState | null>(null);
  const [failed, setFailed] = useState(false);
  const previousTemplate = useRef(templateMode);

  useEffect(() => {
    const sound = new AmbienceVolumeSync();
    return () => { sound.destroy(); };
  }, []);

  useEffect(() => {
    if (!visible) { updateWeather(null); setFailed(false); return; }
    let disposed = false;
    let unlisten: (() => void) | undefined;
    let timer: ReturnType<typeof setInterval> | undefined;
    void listen<WeatherState>(IPC_EVENT_WEATHER_STATE, ({ payload }) => {
      if (disposed || !payload || typeof payload.id !== "string" || !Array.isArray(payload.presets)) return;
      updateWeather(payload);
      setFailed(false);
    }).then((fn) => {
      if (disposed) { fn(); return; }
      unlisten = fn;
      void queryWeather().catch(console.error);
      // Unityの再起動・通知取りこぼしから復帰する。抽選や音の再スタートは行わない。
      timer = setInterval(() => void queryWeather().catch(console.error), 10000);
    }).catch(console.error);
    return () => { disposed = true; unlisten?.(); if (timer) clearInterval(timer); };
  }, [visible]);

  useEffect(() => {
    if (previousTemplate.current && !templateMode && visible)
      void setWeather(null).catch(() => setFailed(true));
    previousTemplate.current = templateMode;
  }, [templateMode, visible]);

  const select = useCallback(async (id: string | null) => {
    if (!templateMode || !visible) return;
    setFailed(false);
    try { await setWeather(id); } catch { setFailed(true); }
  }, [templateMode, visible]);

  return { weather, failed, select };
}
