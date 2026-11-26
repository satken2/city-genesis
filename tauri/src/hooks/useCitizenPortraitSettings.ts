import { useEffect, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { buildApiUrl, fetchApi } from "../utils/api";
import type { ClimateSettings } from "../utils/climate";

type CityClimate = { city?: { climate?: ClimateSettings | null } | null };
type CitySession = { language?: string | null };
type PortraitSettings = { climate: ClimateSettings | null; cityLanguage: string | null };

let latest: PortraitSettings | undefined;

/** 肌色に使う気候と服装に使う都市の言語を、共通のセッション通知から取得する。 */
export function useCitizenPortraitSettings(apiBaseUrl: string): PortraitSettings | undefined {
  const [settings, setSettings] = useState(latest);

  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    let unlisten: (() => void) | undefined;
    let receivedSession = false;
    const apply = (data: CityClimate, session: CitySession | null | undefined) => {
      if (controller.signal.aborted || !data.city) return;
      const climate = data.city.climate ?? null;
      const cityLanguage = session?.language ?? null;
      // 同じ都市情報が再配信されるたびにカードや詳細画面を描き直さない。
      if (latest?.cityLanguage !== cityLanguage || JSON.stringify(climate) !== JSON.stringify(latest.climate)) {
        latest = { climate, cityLanguage };
      }
      setSettings(latest);
    };
    void listen<{ city?: CityClimate | null; session?: CitySession | null }>("session-state", ({ payload }) => {
      if (!payload.city?.city) return;
      receivedSession = true;
      apply(payload.city, payload.session);
    })
      .then((fn) => {
        if (controller.signal.aborted) fn();
        else unlisten = fn;
      })
      .catch(console.error);
    if (latest === undefined) {
      // 新しく開いたウィンドウでは、次の配信を待たずに現在の都市情報を取得する。
      const options = { signal: controller.signal };
      void Promise.all([
        fetchApi<CityClimate>(buildApiUrl(apiBaseUrl, "/api/v1/city/"), options),
        fetchApi<CitySession>(buildApiUrl(apiBaseUrl, "/api/v1/sessions"), options).catch(() => null),
      ]).then(([city, session]) => {
        if (!receivedSession) apply(city, session);
      }).catch(() => {});
    }
    return () => {
      controller.abort();
      unlisten?.();
    };
  }, [apiBaseUrl]);

  return settings;
}
