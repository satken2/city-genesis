/**
 * React Context sharing master data within a window. Each WebView has a separate JavaScript environment and
 * fetches separately. Fetch only after a component using useMasterData() mounts, avoiding requests from windows
 * that do not need it (top menu, notifications, etc.).
 *
 * Fetch only while shared monitoring confirms backend connectivity. Do not retry independently; refetch when
 * connectivity recovers (connected becomes true). Master data can differ between cities because the core reloads
 * it on each game start. Remember the session when fetching; discard/refetch when session-state reports a
 * changed session_id.
 */

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { listen } from "@tauri-apps/api/event";
import { fetchApi } from "../utils/api";
import type { MasterData } from "../types";

/**
 * Context values. data: master data itself, null before retrieval or on failure. request: declare that this
 * window needs master data.
 */
type MasterDataContextValue = {
  data: MasterData | null;
  request: () => void;
};

const MasterDataContext = createContext<MasterDataContextValue>({
  data: null,
  request: () => {},
});

/**
 * Backend master data API endpoints. Keys are MasterData property names; values are API paths.
 */
const MASTER_ENDPOINTS: { key: keyof MasterData; path: string; numericKeys?: boolean }[] = [
  { key: "climates", path: "/api/v1/master/climates" },
  { key: "weather", path: "/api/v1/master/weather", numericKeys: false },
  { key: "bd_types", path: "/api/v1/master/bd_types" },
  { key: "skills", path: "/api/v1/master/skills" },
  { key: "vehicle_types", path: "/api/v1/master/vehicle_types" },
  { key: "space_categories", path: "/api/v1/master/space_categories" },
  { key: "product_types", path: "/api/v1/master/product_types" },
  { key: "parcel_types", path: "/api/v1/master/parcel_types" },
  { key: "path_types", path: "/api/v1/master/path_types" },
  { key: "agent_types", path: "/api/v1/master/agent_types" },
  { key: "agents", path: "/api/v1/master/agents" },
  { key: "subbuildings", path: "/api/v1/master/subbuildings" },
  { key: "landmarks", path: "/api/v1/master/landmarks" },
  { key: "products", path: "/api/v1/master/products" },
  { key: "paths", path: "/api/v1/master/paths" },
  { key: "transit", path: "/api/v1/master/transit" },
  { key: "combined_paths", path: "/api/v1/master/combined_paths" },
  { key: "budget_categories", path: "/api/v1/master/budgets" },
  { key: "resource_types", path: "/api/v1/master/resource_types" },
  { key: "prop_types", path: "/api/v1/master/prop_types" },
];

/**
 * Convert API response string keys to numeric keys. JSON object keys are always strings; convert on fetch for
 * convenient numeric ID access.
 */
function toNumericKeys<T>(
  obj: Record<string, T>
): Record<number, T> {
  const result: Record<number, T> = {};
  for (const [key, value] of Object.entries(obj)) {
    result[Number(key)] = value;
  }
  return result;
}

/**
 * Fetch one master dataset and convert keys only for numeric IDs. Backend response format: { "endpoint_name": {
 * "0": {...}, "1": {...} } }. The first level wraps the endpoint name; the second maps IDs to entries. Preserve
 * string IDs such as weather IDs.
 */
async function fetchMasterEndpoint<T>(
  baseUrl: string,
  path: string,
  numericKeys = true,
): Promise<Record<string, T>> {
  type ApiResponse = Record<string, Record<string, T>>;
  const response = await fetchApi<ApiResponse>(`${baseUrl}${path}`);
  const inner = Object.values(response)[0];
  return numericKeys ? toNumericKeys(inner) : inner;
}

/**
 * Provider that fetches master data in bulk and exposes it through Context. Fetch all endpoints in parallel when
 * requested, apiBaseUrl is known, and connectivity is confirmed. Discard/refetch data when apiBaseUrl or the
 * city (session_id) changes.
 */
export function MasterDataProvider({
  apiBaseUrl,
  connected,
  children,
}: {
  apiBaseUrl: string | undefined;
  connected: boolean;
  children: ReactNode;
}) {
  const [masterData, setMasterData] = useState<MasterData | null>(null);
  const [requested, setRequested] = useState(false);
  const prevBaseUrlRef = useRef<string | undefined>(undefined);
  const masterDataRef = useRef(masterData);
  masterDataRef.current = masterData;
  const inFlightRef = useRef(false);
  const disposedRef = useRef(false);
  // City owning the fetched master data (session_id from GET /sessions just before fetching; null if no city).
  const dataSessionRef = useRef<string | null>(null);

  const fetchAll = useCallback(async (base: string) => {
    if (masterDataRef.current !== null || inFlightRef.current) return;

    inFlightRef.current = true;
    try {
      const session = await fetchApi<{ session_id?: string | null }>(`${base}/api/v1/sessions`, { cache: "reload" });
      const results = await Promise.all(
        MASTER_ENDPOINTS.map(async ({ key, path, numericKeys }) => {
          const entries = await fetchMasterEndpoint(base, path, numericKeys);
          return [key, entries] as const;
        })
      );
      if (!disposedRef.current) {
        dataSessionRef.current = session.session_id ?? null;
        setMasterData(Object.fromEntries(results) as unknown as MasterData);
      }
    } catch (error: unknown) {
      console.error("マスターデータの取得に失敗しました(通信の回復後に取り直します):", error);
    } finally {
      inFlightRef.current = false;
    }
  }, []);

  useEffect(() => {
    disposedRef.current = false;
    return () => {
      disposedRef.current = true;
    };
  }, []);

  useEffect(() => {
    const unlisten = listen<{ session?: { session_id?: string | null } | null }>("session-state", ({ payload }) => {
      if (masterDataRef.current !== null && (payload.session?.session_id ?? null) !== dataSessionRef.current) {
        masterDataRef.current = null;
        setMasterData(null);
      }
    });
    return () => { void unlisten.then(off => off()); };
  }, []);

  useEffect(() => {
    if (!apiBaseUrl) return;
    if (prevBaseUrlRef.current !== undefined && prevBaseUrlRef.current !== apiBaseUrl) {
      setMasterData(null);
      masterDataRef.current = null;
    }
    prevBaseUrlRef.current = apiBaseUrl;

    if (masterData === null && requested && connected) {
      void fetchAll(apiBaseUrl.replace(/\/+$/, ""));
    }
  }, [apiBaseUrl, connected, requested, fetchAll, masterData]);

  const request = useCallback(() => setRequested(true), []);
  const contextValue = useMemo(() => ({ data: masterData, request }), [masterData, request]);

  return (
    <MasterDataContext.Provider value={contextValue}>
      {children}
    </MasterDataContext.Provider>
  );
}

/**
 * Hook to read master data, available only within MasterDataProvider. Return MasterData if fetched, otherwise
 * null. The first use in a window starts fetching; rerender when data arrives.
 */
export function useMasterData(): MasterData | null {
  const { data, request } = useContext(MasterDataContext);
  useEffect(() => request(), [request]);
  return data;
}
