/**
 * マスターデータをウィンドウ内で共有するReact Context
 *
 * WebViewはウィンドウごとに別のJS環境なので、取得もウィンドウごとに行う。
 * 参照しないウィンドウ(トップメニュー・通知など)に取得させないため、
 * useMasterData() を呼ぶコンポーネントがマウントされてから取得する。
 *
 * 取得はバックエンドとの通信を共通の監視が確認できている間だけ行う。
 * 失敗しても独自に再試行せず、通信が回復した(connected が true に戻った)ときに取り直す。
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
import { fetchApi } from "../utils/api";
import type { MasterData } from "../types";

/**
 * Contextが提供する値
 * data: マスターデータ本体。未取得・取得失敗時はnull
 * request: このウィンドウでマスターデータが必要であることを伝える
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
 * バックエンドのマスターデータAPIエンドポイント定義
 * キーがMasterDataのプロパティ名、値がAPIパス
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
 * APIレスポンスの文字列キーを数値キーに変換する
 * JSONの仕様上オブジェクトのキーは常に文字列になるため、
 * 数値IDでアクセスしやすいようにフェッチ時に変換を行う
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
 * 単一マスターを取得し、数値IDのマスターだけキーを数値に変換する。
 *
 * バックエンドのレスポンス形式:
 * { "endpoint_name": { "0": {...}, "1": {...} } }
 *
 * 1階層目はエンドポイント名のラッパーで、2階層目がID→エントリのマッピング。
 * 天気のような文字列IDは、そのまま保持する。
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
 * マスターデータを一括取得してContextで提供するプロバイダ
 *
 * 参照の要求があり、apiBaseUrl が決まり、通信が確認できているときに全エンドポイントを並列取得する。
 * apiBaseUrl が変わったら取得済みのデータを捨てて取り直す。
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

  const fetchAll = useCallback(async (base: string) => {
    if (masterDataRef.current !== null || inFlightRef.current) return;

    inFlightRef.current = true;
    try {
      const results = await Promise.all(
        MASTER_ENDPOINTS.map(async ({ key, path, numericKeys }) => {
          const entries = await fetchMasterEndpoint(base, path, numericKeys);
          return [key, entries] as const;
        })
      );
      if (!disposedRef.current) {
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
    if (!apiBaseUrl) return;
    if (prevBaseUrlRef.current !== undefined && prevBaseUrlRef.current !== apiBaseUrl) {
      setMasterData(null);
      masterDataRef.current = null;
    }
    prevBaseUrlRef.current = apiBaseUrl;

    if (requested && connected) {
      void fetchAll(apiBaseUrl.replace(/\/+$/, ""));
    }
  }, [apiBaseUrl, connected, requested, fetchAll]);

  const request = useCallback(() => setRequested(true), []);
  const contextValue = useMemo(() => ({ data: masterData, request }), [masterData, request]);

  return (
    <MasterDataContext.Provider value={contextValue}>
      {children}
    </MasterDataContext.Provider>
  );
}

/**
 * マスターデータを参照するためのフック
 * MasterDataProvider 配下のコンポーネントからのみ使用可能
 *
 * データが取得済みの場合は MasterData を返し、未取得の場合は null を返す。
 * 呼び出したウィンドウで初めて取得が始まり、届いた時点で再描画される。
 */
export function useMasterData(): MasterData | null {
  const { data, request } = useContext(MasterDataContext);
  useEffect(() => request(), [request]);
  return data;
}
