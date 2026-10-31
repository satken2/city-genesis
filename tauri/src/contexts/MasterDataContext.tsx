/**
 * React Context sharing master data within a window. Each WebView has a separate JavaScript environment and
 * fetches separately. Fetch only after a component using useMasterData() mounts, avoiding requests from windows
 * that do not need it (top menu, notifications, etc.).
 *
 * Fetch only while shared monitoring confirms backend connectivity. Do not retry independently; refetch when
 * connectivity recovers (connected becomes true). Master data can differ between cities because the core reloads
 * it on each game start. Remember the session_id returned with the master data; discard/refetch when
 * session-state reports a changed session_id. Fetch every kind with one GET /api/v1/master request per window.
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
 * Master kinds in the GET /api/v1/master response. key: MasterData property name; name: response key (the same
 * name as the per-kind /api/v1/master/{name} endpoint).
 */
const MASTER_KINDS: { key: keyof MasterData; name: string; numericKeys?: boolean }[] = [
  { key: "climates", name: "climates" },
  { key: "weather", name: "weather", numericKeys: false },
  { key: "bd_types", name: "bd_types" },
  { key: "skills", name: "skills" },
  { key: "vehicle_types", name: "vehicle_types" },
  { key: "space_categories", name: "space_categories" },
  { key: "product_types", name: "product_types" },
  { key: "parcel_types", name: "parcel_types" },
  { key: "path_types", name: "path_types" },
  { key: "agent_types", name: "agent_types" },
  { key: "agents", name: "agents" },
  { key: "subbuildings", name: "subbuildings" },
  { key: "landmarks", name: "landmarks" },
  { key: "products", name: "products" },
  { key: "paths", name: "paths" },
  { key: "transit", name: "transit" },
  { key: "combined_paths", name: "combined_paths" },
  { key: "budget_categories", name: "budgets" },
  { key: "resource_types", name: "resource_types" },
  { key: "prop_types", name: "prop_types" },
];

/** GET /api/v1/master response: each kind maps IDs to entries; session_id identifies the city owning the data. */
type MasterResponse = { session_id: string | null; [name: string]: unknown };

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
 * Provider that fetches master data in bulk and exposes it through Context. Fetch when requested, apiBaseUrl is
 * known, and connectivity is confirmed. Discard/refetch data when apiBaseUrl or the city (session_id) changes.
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
  // City owning the fetched master data (session_id returned with it; null if no city).
  const dataSessionRef = useRef<string | null>(null);

  const fetchAll = useCallback(async (base: string) => {
    if (masterDataRef.current !== null || inFlightRef.current) return;

    inFlightRef.current = true;
    try {
      const response = await fetchApi<MasterResponse>(`${base}/api/v1/master`, { cache: "reload" });
      const results = MASTER_KINDS.map(({ key, name, numericKeys = true }) => {
        const entries = response[name] as Record<string, unknown>;
        return [key, numericKeys ? toNumericKeys(entries) : entries] as const;
      });
      if (!disposedRef.current) {
        dataSessionRef.current = response.session_id ?? null;
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
