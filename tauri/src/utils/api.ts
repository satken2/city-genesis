/**
 * APIリクエストのヘルパー関数
 */

import { frontendVersion } from "./version";
import { decode } from "@msgpack/msgpack";

/**
 * MessagePackレスポンスのメディアタイプ。
 * バックエンドのBBOX一括取得エンドポイント(paths/parcels/buildings/zones/nodes)は、
 * Acceptヘッダにこの値が含まれていると、座標配列を大量に含む巨大なレスポンスを
 * JSONではなくMessagePackバイナリで返す(転送量とパース負荷の削減)。
 * 未対応のエンドポイントは従来どおりJSONで応答するため、fetchApiの呼び出し側は
 * どちらの形式で返ってきたかを意識する必要がない(Content-Typeで自動判別する)。
 */
const MSGPACK_MEDIA_TYPE = "application/x-msgpack";

/**
 * API URL を構築する
 * baseUrl の末尾スラッシュを除去し、template 内の {id} を entityId で置換する
 */
export function buildApiUrl(
  baseUrl: string,
  template: string,
  entityId?: string
): string {
  const normalizedBase = baseUrl.replace(/\/+$/, "");
  const path = template.replace("{id}", entityId ?? "");
  return `${normalizedBase}${path}`;
}

/**
 * バックエンドから返される標準エラーレスポンスの型。
 * { code: "CREATEPATH_OUTSIDE_COASTLINE", message: "..." } のような形式を期待する。
 */
export interface ApiErrorResponse {
  code: string;
  message: string;
}

/**
 * APIからのエラーレスポンスを表すカスタムエラークラス。
 * バックエンドのCrudError継承クラスと対応し、code と message を保持する。
 */
export class ApiError extends Error {
  code: string;
  status: number;

  constructor(code: string, message: string, status: number) {
    super(message);
    this.code = code;
    this.status = status;
    this.name = "ApiError";
  }
}

const nativeFetch = globalThis.fetch.bind(globalThis);
let configuredBase = "";
let backendConnected = true;

export function setBackendConnected(connected: boolean) {
  backendConnected = connected;
}

/** 設定済みのバックエンドAPIのURLか。版ヘッダーはここにだけ付け、他のオリジンへ送らない。 */
function isBackendApiUrl(url: string): boolean {
  return configuredBase !== "" && url.startsWith(`${configuredBase}/api/v1/`);
}

/**
 * バイナリ取得も含め、バックエンドへの要求に版ヘッダーを付ける。
 * 起動時の互換性確認はRust側で共有し、通常要求の契約番号はコア側で検査する。
 * 各画面が直接書いた fetch もここで版ヘッダーが付く。
 */
export function configureBackendRequests(baseUrl: string) {
  if (!baseUrl || configuredBase === baseUrl.replace(/\/+$/, "")) return;
  configuredBase = baseUrl.replace(/\/+$/, "");
  globalThis.fetch = async (input, init) => {
    const url = input instanceof Request ? input.url : String(input);
    if (!isBackendApiUrl(url)) return nativeFetch(input, init);
    const method = (init?.method ?? (input instanceof Request ? input.method : "GET")).toUpperCase();
    // 更新要求を保留・再送すると、応答だけ失われた操作を二重に実行する危険がある。
    if (!backendConnected && !["GET", "HEAD", "OPTIONS"].includes(method)) {
      throw new ApiError("BACKEND_DISCONNECTED", "BACKEND_DISCONNECTED", 503);
    }
    const headers = new Headers(input instanceof Request ? input.headers : undefined);
    new Headers(init?.headers).forEach((value, key) => headers.set(key, value));
    headers.set("X-City-Genesis-API", String(frontendVersion.api_contract_version));
    return nativeFetch(input, { ...init, headers });
  };
}

/**
 * APIからデータを取得する。
 * 常にAcceptヘッダでMessagePackを受け入れ可能と宣言し、レスポンスの
 * Content-Typeに応じてMessagePack(バイナリ)/JSONを自動でデコードする。
 * レスポンスがokでない場合、レスポンスボディに含まれる {code, message} を
 * 読み取って ApiError を投げる(エラーレスポンスは常にJSON)。
 * ボディのパースに失敗した場合はフォールバックとして HTTP ステータスコードを含む ApiError を投げる。
 */
export async function fetchApi<T>(
  url: string,
  options?: { method?: string; headers?: Record<string, string>; body?: BodyInit; responseType?: "blob"; signal?: AbortSignal; cache?: RequestCache }
): Promise<T> {
  const response = await fetch(url, {
    method: options?.method ?? "GET",
    headers: {
      Accept: `${MSGPACK_MEDIA_TYPE}, application/json`,
      "X-City-Genesis-API": String(frontendVersion.api_contract_version),
      ...options?.headers,
    },
    body: options?.body,
    signal: options?.signal,
    cache: options?.cache,
  });
  if (!response.ok) {
    const errorData: Partial<ApiErrorResponse> = await response
      .json()
      .catch(() => ({}));
    throw new ApiError(
      errorData.code ?? "UNKNOWN_ERROR",
      errorData.message ?? `HTTP ${response.status}`,
      response.status
    );
  }
  if (response.status === 204) return undefined as T;
  if (options?.responseType === "blob") return await response.blob() as T;
  const contentType = response.headers.get("content-type") ?? "";
  if (contentType.includes(MSGPACK_MEDIA_TYPE)) {
    // MessagePackバイナリ: 巨大な座標配列を含む一括取得レスポンスの高速デコード経路。
    // decode() はマップをプレーンオブジェクトに展開する(intキーはJSの仕様で文字列化
    // されるため、JSONと同じキー形になる)。
    return decode(new Uint8Array(await response.arrayBuffer())) as T;
  }
  return response.json();
}
