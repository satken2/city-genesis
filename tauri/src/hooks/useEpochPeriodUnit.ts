import { useEffect, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { buildApiUrl, fetchApi } from "../utils/api";
import { epochPeriodUnit } from "../utils/date";

type CityDays = { city?: { simulation_days?: number } };

/**
 * Calendar period one epoch represents in the current city. Pass it as the i18next context of text that names the
 * period: keys hold the 30-day wording, and *_week variants hold the 7-day wording.
 * Follows session-state so long-lived windows such as the tutorial pick up a city switch.
 * undefined before fetching, which falls back to the 30-day wording.
 */
export function useEpochPeriodUnit(apiBaseUrl: string): "month" | "week" | undefined {
  const [unit, setUnit] = useState<"month" | "week">();

  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    let unlisten: (() => void) | undefined;
    const apply = (data: CityDays | null | undefined) => {
      const days = data?.city?.simulation_days;
      if (!controller.signal.aborted && days != null) setUnit(epochPeriodUnit(days));
    };
    void listen<{ city?: CityDays | null }>("session-state", (event) => apply(event.payload.city))
      .then((fn) => {
        if (controller.signal.aborted) fn();
        else unlisten = fn;
      })
      .catch(console.error);
    void fetchApi<CityDays>(buildApiUrl(apiBaseUrl, "/api/v1/city/"), { signal: controller.signal })
      .then(apply)
      .catch(() => {});
    return () => {
      controller.abort();
      unlisten?.();
    };
  }, [apiBaseUrl]);

  return unit;
}
