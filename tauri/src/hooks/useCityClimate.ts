import { useEffect, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { buildApiUrl, fetchApi } from "../utils/api";
import type { ClimateSettings } from "../utils/climate";

type CityClimate = { city?: { climate?: ClimateSettings | null } | null };

/**
 * Climate settings held per window. The session polling rebroadcasts the city in session-state, so this follows a
 * city switch or a climate edit without its own requests; only a window without a value yet fetches once.
 * A missing city (top menu, failed poll) keeps the last value.
 */
let latest: ClimateSettings | null | undefined;

/** Climate of the city in play. undefined until the first value arrives; null when the city has no climate. */
export function useCityClimate(apiBaseUrl: string): ClimateSettings | null | undefined {
  const [climate, setClimate] = useState(latest);

  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    let unlisten: (() => void) | undefined;
    const apply = (data: CityClimate | null | undefined) => {
      if (!data?.city) return;
      const next = data.city.climate ?? null;
      // Keep the same object while the rebroadcast climate is unchanged, so panels do not re-render every poll.
      if (latest === undefined || JSON.stringify(next) !== JSON.stringify(latest)) latest = next;
      if (!controller.signal.aborted) setClimate(latest);
    };
    void listen<{ city?: CityClimate | null }>("session-state", (event) => apply(event.payload.city))
      .then((fn) => {
        if (controller.signal.aborted) fn();
        else unlisten = fn;
      })
      .catch(console.error);
    if (latest === undefined) {
      void fetchApi<CityClimate>(buildApiUrl(apiBaseUrl, "/api/v1/city/"), { signal: controller.signal })
        .then(apply)
        .catch(() => {});
    }
    return () => {
      controller.abort();
      unlisten?.();
    };
  }, [apiBaseUrl]);

  return climate;
}
