import { useCallback, useEffect, useRef, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { IPC_EVENT_WEATHER_STATE, queryWeather, setWeather, type WeatherState } from "../../utils/ipc";
import { AmbienceVolumeSync } from "../../utils/weatherAudio";

export function useWeather(visible: boolean, godMode: boolean) {
  const [weather, updateWeather] = useState<WeatherState | null>(null);
  const [failed, setFailed] = useState(false);
  const previousGodMode = useRef(godMode);

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
      // 問い合わせは表示開始時の1回だけ。Unityは起動時と天候の変化時に自分から weather:state を送る。
      // 周期的に問い合わせると、Unityが不在の間にMMFへ溜まり続けて満杯にする。
      void queryWeather().catch(console.error);
    }).catch(console.error);
    return () => { disposed = true; unlisten?.(); };
  }, [visible]);

  useEffect(() => {
    if (previousGodMode.current && !godMode && visible)
      void setWeather(null).catch(() => setFailed(true));
    previousGodMode.current = godMode;
  }, [godMode, visible]);

  const select = useCallback(async (id: string | null) => {
    if (!godMode || !visible) return;
    setFailed(false);
    try { await setWeather(id); } catch { setFailed(true); }
  }, [godMode, visible]);

  return { weather, failed, select };
}
