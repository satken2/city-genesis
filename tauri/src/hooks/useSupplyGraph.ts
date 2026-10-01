/** ウィンドウを開いた時と表示対象を変えた時に供給図を取得する。 */
import { useEffect, useState } from "react";
import { fetchApi } from "../utils/api";
import type { SupplyResponse } from "../components/supply/supplyGraph";

export function useSupplyGraph(apiBaseUrl: string, agentId: string, enabled: boolean) {
  const [data, setData] = useState<SupplyResponse | null>(null);
  const [error, setError] = useState(false);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    setData(null);
    setError(false);
    setLoading(enabled);
    if (!enabled) return;
    const controller = new AbortController();
    fetchApi<SupplyResponse>(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/agents/${agentId}/diagnosis`,
      { signal: controller.signal })
      .then((result) => { if (!controller.signal.aborted) setData(result); })
      .catch(() => { if (!controller.signal.aborted) setError(true); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [apiBaseUrl, agentId, enabled]);

  return { data, error, loading };
}
