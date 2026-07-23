/**
 * 統計ウィンドウ共通のエポック単位キャッシュフック
 *
 * 統計データはエポック（city.epoch）が変わらない限り内容が変化しないため、
 * 一度取得したレスポンスを「エンドポイント × エポック」をキーにメモ化し、
 * 同一エポック内ではバックエンドへの重いリクエストを再発行しないようにする。
 *
 * 仕組み:
 * 1. まず軽量な GET /api/v1/city で現在の epoch を取得する。
 * 2. `${path}@${epoch}` のキャッシュがあればそれを返す（重いリクエストを叩かない）。
 * 3. キャッシュが無ければ path を取得してキャッシュに格納する。
 *
 * キャッシュはモジュールレベルに保持するため、ウィンドウの再マウントやタブ切替を
 * またいでも保持される。新しいエポックに進むと別キーになるため、自然に最新化される。
 */

import { useEffect, useState } from "react";
import { buildApiUrl, fetchApi } from "../../utils/api";

// `${path}@${epoch}` をキーにしたモジュールレベルのキャッシュ。
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
          buildApiUrl(apiBaseUrl, "/api/v1/city"),
          { signal: controller.signal }
        );
        const epoch = cityRes.city.epoch;
        const key = `${path}@${epoch}`;

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
        cache.set(key, data);
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
