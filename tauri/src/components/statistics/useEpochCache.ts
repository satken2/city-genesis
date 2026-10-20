/**
 * Reuses statistics responses within the same connection, city (session_id) and Epoch.
 * Discards all entries when the generation changes; limits growth from District switching to 32 entries.
 */

import { useEffect, useState } from "react";
import { buildApiUrl, fetchApi } from "../../utils/api";

// Do not store a delayed response from an older generation in the current cache.
const CACHE_LIMIT = 32;
let cacheScope = "";
const cache = new Map<string, unknown>();

type CityEpochResponse = { city: { epoch: number } };
type SessionResponse = { session_id?: string | null };

export type EpochCacheState<T> = {
  data: T | null;
  loading: boolean;
  error: string | null;
  /** Epoch used for the fetch; null before fetching. */
  epoch: number | null;
};

/**
 * Fetches the specified endpoint's response with per-Epoch caching.
 *
 * @param apiBaseUrl Backend base URL.
 * @param path Target path (e.g. "/api/v1/stats/population").
 */
export function useEpochCachedStats<T>(
  apiBaseUrl: string,
  path: string
): EpochCacheState<T> {
  const [state, setState] = useState<EpochCacheState<T>>({
    data: null,
    loading: true,
    error: null,
    epoch: null,
  });

  useEffect(() => {
    const controller = new AbortController();
    let cancelled = false;

    (async () => {
      setState((prev) => ({ ...prev, loading: true, error: null }));
      try {
        // Fetch the current city and Epoch through lightweight requests. Epoch numbers are counted per city,
        // so the city identity is part of the scope.
        const [sessionRes, cityRes] = await Promise.all([
          fetchApi<SessionResponse>(buildApiUrl(apiBaseUrl, "/api/v1/sessions"), { signal: controller.signal }),
          fetchApi<CityEpochResponse>(buildApiUrl(apiBaseUrl, "/api/v1/city/"), { signal: controller.signal }),
        ]);
        const epoch = cityRes.city.epoch;
        if (cancelled) return;
        const scope = JSON.stringify([apiBaseUrl, sessionRes.session_id ?? null, epoch]);
        if (cacheScope !== scope) {
          cache.clear();
          cacheScope = scope;
        }
        const key = path;

        // Return previously fetched data for the same Epoch without issuing a heavy request.
        const cached = cache.get(key) as T | undefined;
        if (cached !== undefined) {
          if (!cancelled) {
            setState({ data: cached, loading: false, error: null, epoch });
          }
          return;
        }

        const data = await fetchApi<T>(buildApiUrl(apiBaseUrl, path), {
          signal: controller.signal,
        });
        if (!cancelled && cacheScope === scope) {
          cache.delete(key);
          cache.set(key, data);
          if (cache.size > CACHE_LIMIT) {
            cache.delete(cache.keys().next().value!);
          }
        }
        if (!cancelled) {
          setState({ data, loading: false, error: null, epoch });
        }
      } catch (err) {
        if (controller.signal.aborted || cancelled) return;
        setState({
          data: null,
          loading: false,
          error: err instanceof Error ? err.message : "Unknown error",
          epoch: null,
        });
      }
    })();

    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [apiBaseUrl, path]);

  return state;
}
