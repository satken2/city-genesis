/** 供給図は表示時と変更通知で取得し、非表示中とエポック実行中は更新しない。 */
import { useCallback, useEffect, useRef, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { listen } from "@tauri-apps/api/event";
import { fetchApi } from "../utils/api";
import type { SupplyResponse } from "../components/supply/supplyGraph";

export function useSupplyGraph(apiBaseUrl: string, agentId: string, enabled: boolean, refreshToken?: unknown) {
  const [data, setData] = useState<SupplyResponse | null>(null);
  const [error, setError] = useState(false);
  const [loading, setLoading] = useState(false);
  const [executing, setExecuting] = useState(false);
  const [tick, setTick] = useState(0);
  const request = useRef<AbortController | null>(null);
  const refresh = useCallback(() => setTick((value) => value + 1), []);

  useEffect(() => {
    if (!enabled || executing) return;
    const controller = new AbortController();
    request.current = controller;
    setLoading(true);
    setError(false);
    getCurrentWindow().isVisible().then((visible) => {
      if (!visible || controller.signal.aborted) return null;
      return fetchApi<SupplyResponse>(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/agents/${agentId}/diagnosis`,
        { signal: controller.signal });
    })
      .then((result) => { if (result && !controller.signal.aborted) setData(result); })
      .catch(() => { if (!controller.signal.aborted) { setError(true); setData(null); } })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [apiBaseUrl, agentId, enabled, executing, refreshToken, tick]);

  useEffect(() => { setData(null); }, [agentId]);

  useEffect(() => {
    if (!enabled) return;
    let disposed = false;
    const cleanup: (() => void)[] = [];
    const remember = (fn: () => void) => { if (disposed) fn(); else cleanup.push(fn); };
    const refreshVisible = () => {
      void getCurrentWindow().isVisible().then((visible) => { if (visible && !disposed) refresh(); }).catch(console.error);
    };
    for (const event of ["roads:changed", "transit:lines_changed", "supply:changed"]) {
      void listen(event, refreshVisible).then(remember).catch(console.error);
    }
    void listen<{ executing: boolean }>("epoch:executing", (event) => {
      if (event.payload.executing) request.current?.abort();
      setExecuting(event.payload.executing);
      if (!event.payload.executing) refreshVisible();
    }).then(remember).catch(console.error);
    void getCurrentWindow().onFocusChanged((event) => { if (event.payload) refreshVisible(); })
      .then(remember).catch(console.error);
    return () => { disposed = true; cleanup.forEach((fn) => fn()); };
  }, [enabled, refresh]);

  return { data, error, loading, executing, pending: executing || data?.pending === true, refresh };
}
