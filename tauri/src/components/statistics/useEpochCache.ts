/**
 * 統計レスポンスを同じ接続先・エポック内で再利用する。
 * 世代が変われば全件破棄し、地区切替による増加も最大32件に制限する。
 */

import { useEffect, useState } from "react";
import { buildApiUrl, fetchApi } from "../../utils/api";

// 古い世代の応答が遅れて届いても、現在のキャッシュには格納しない。
const CACHE_LIMIT = 32;
let cacheScope = "";
const cache = new Map<string, unknown>();

type CityEpochResponse = { city: { epoch: number } };

export type EpochCacheState<T> = {
  data: T | null;
  loading: boolean;
  error: string | null;
  /** 取得に用いた epoch。未取得時は null。 */
  epoch: number | null;
};

/**
 * 指定したエンドポイントのレスポンスをエポック単位でキャッシュしつつ取得する。
 *
 * @param apiBaseUrl バックエンドのベースURL
 * @param path 取得対象のパス（例: "/api/v1/stats/population"）
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
        // 現在のエポックを軽量に取得する。
        const cityRes = await fetchApi<CityEpochResponse>(
          buildApiUrl(apiBaseUrl, "/api/v1/city/"),
          { signal: controller.signal }
        );
        const epoch = cityRes.city.epoch;
        if (cancelled) return;
        const scope = JSON.stringify([apiBaseUrl, epoch]);
        if (cacheScope !== scope) {
          cache.clear();
          cacheScope = scope;
        }
        const key = path;

        // 同一エポックの取得済みデータがあれば、重いリクエストを発行せずに返す。
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
