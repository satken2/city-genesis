/**
 * マスターデータをアプリケーション全体で参照可能にするReact Context
 *
 * Tauri起動時にバックエンドのマスターAPIから全データを一括取得し、
 * 任意のコンポーネントから useMasterData() フックで参照できるようにする。
 * apiBaseUrl が渡されたタイミングでフェッチを開始する。
 *
 * バックエンドの起動が間に合わず初回フェッチに失敗した場合は、
 * useMasterData() が呼ばれたタイミングで自動的にリトライを行う。
 */

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { fetchApi } from "../utils/api";
import type { MasterData } from "../types";

/**
 * Contextが提供する値
 * data: マスターデータ本体。未取得・取得失敗時はnull
 * isLoading: 現在フェッチ中かどうか。リトライの重複防止に使用する
 * refetch: マスターデータの再取得を要求する関数
 */
type MasterDataContextValue = {
  data: MasterData | null;
  isLoading: boolean;
  refetch: () => void;
};

const MasterDataContext = createContext<MasterDataContextValue>({
  data: null,
  isLoading: false,
  refetch: () => {},
});

/**
 * バックエンドのマスターデータAPIエンドポイント定義
 * キーがMasterDataのプロパティ名、値がAPIパス
 */
const MASTER_ENDPOINTS: { key: keyof MasterData; path: string }[] = [
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
  { key: "structures", path: "/api/v1/master/structures" },
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
 * 単一マスターエンドポイントをフェッチし、数値キーのRecordとして返す
 *
 * バックエンドのレスポンス形式:
 * { "endpoint_name": { "0": {...}, "1": {...} } }
 *
 * 1階層目はエンドポイント名のラッパーで、2階層目がID→エントリのマッピング。
 * 1階層目を剥がして数値キーに変換したものを返す。
 */
async function fetchMasterEndpoint<T>(
  baseUrl: string,
  path: string
): Promise<Record<number, T>> {
  type ApiResponse = Record<string, Record<string, T>>;
  const response = await fetchApi<ApiResponse>(`${baseUrl}${path}`);
  const inner = Object.values(response)[0];
  return toNumericKeys(inner);
}

/**
 * マスターデータを一括取得してContextで提供するプロバイダ
 *
 * apiBaseUrlが渡されると全エンドポイントを並列フェッチする。
 * apiBaseUrlがundefinedの間はフェッチを行わず、masterDataはnullのまま。
 * apiBaseUrlが変更された場合は再フェッチする。
 *
 * フェッチに失敗した場合は isLoading が false に戻り、
 * useMasterData() 側で自動リトライがトリガされる。
 */
export function MasterDataProvider({
  apiBaseUrl,
  children,
}: {
  apiBaseUrl: string | undefined;
  children: ReactNode;
}) {
  const [masterData, setMasterData] = useState<MasterData | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const apiBaseUrlRef = useRef(apiBaseUrl);
  apiBaseUrlRef.current = apiBaseUrl;

  const fetchAll = useCallback(async () => {
    const base = (apiBaseUrlRef.current ?? "").replace(/\/+$/, "");
    if (!base) return;

    setIsLoading(true);
    try {
      const results = await Promise.all(
        MASTER_ENDPOINTS.map(async ({ key, path }) => {
          const entries = await fetchMasterEndpoint(base, path);
          return [key, entries] as const;
        })
      );
      setMasterData(Object.fromEntries(results) as unknown as MasterData);
    } catch (error: unknown) {
      console.error("マスターデータの取得に失敗しました:", error);
    } finally {
      setIsLoading(false);
    }
  }, []);

  /**
   * apiBaseUrlが設定・変更された時にフェッチを開始する
   */
  useEffect(() => {
    if (!apiBaseUrl) return;
    let disposed = false;

    if (!disposed) {
      fetchAll();
    }

    return () => {
      disposed = true;
    };
  }, [apiBaseUrl, fetchAll]);

  const contextValue = useRef<MasterDataContextValue>({
    data: null,
    isLoading: false,
    refetch: fetchAll,
  });
  contextValue.current = { data: masterData, isLoading, refetch: fetchAll };

  return (
    <MasterDataContext.Provider value={contextValue.current}>
      {children}
    </MasterDataContext.Provider>
  );
}

/**
 * マスターデータを参照するためのフック
 * MasterDataProvider 配下のコンポーネントからのみ使用可能
 *
 * データが未取得（null）かつフェッチ中でなければ、
 * バックエンドがまだ起動していない可能性があるため自動的にリトライを行う。
 * データが取得済みの場合はそのまま返す。
 */
export function useMasterData(): MasterData | null {
  const { data, isLoading, refetch } = useContext(MasterDataContext);

  useEffect(() => {
    if (data === null && !isLoading) {
      refetch();
    }
  }, [data, isLoading, refetch]);

  return data;
}
