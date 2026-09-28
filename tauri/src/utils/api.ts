import { invoke } from "@tauri-apps/api/core";
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
let versionCheck: Promise<void> | null = null;
let checkedAt = 0;

/** ローカルAPIの認証ヘッダー。コアは欠落・不一致を 401 `API_TOKEN_REQUIRED` で拒否する。 */
const TOKEN_HEADER = "X-City-Genesis-Token";
let apiToken: string | null = null;
let tokenRequest: Promise<string | null> | null = null;

/** Rust の `get_connection_status` で受け取ったトークンを保持する。 */
export function setApiToken(token: string | null | undefined) {
  apiToken = token || null;
}

/** トークンを Rust から取り直す(環境変数→トークンファイル)。同時に届いた 401 の取り直しは1回にまとめる。 */
function refreshApiToken(): Promise<string | null> {
  if (!tokenRequest) {
    tokenRequest = invoke<{ api_token?: string | null }>("get_connection_status")
      .then(status => { setApiToken(status.api_token); return apiToken; })
      .catch(() => apiToken)
      .finally(() => { tokenRequest = null; });
  }
  return tokenRequest;
}

/** 設定済みのバックエンドAPIのURLか。トークンはここにだけ付け、他のオリジンへ漏らさない。 */
function isBackendApiUrl(url: string): boolean {
  return configuredBase !== "" && url.startsWith(`${configuredBase}/api/v1/`);
}

/**
 * トークンを付けて送る。バックエンドの再起動でトークンが変わるため、401 なら取り直して1回だけ再試行する。
 * 401 は認証ミドルウェアが処理前に返すので、POST 等を再送しても二重実行にならない。
 */
async function authorizedFetch(input: RequestInfo | URL, init: RequestInit & { headers: Headers }): Promise<Response> {
  const retryInput = input instanceof Request ? input.clone() : input;
  const token = apiToken ?? await refreshApiToken();
  if (token) init.headers.set(TOKEN_HEADER, token);
  const response = await nativeFetch(input, init);
  if (response.status !== 401) return response;
  const refreshed = await refreshApiToken();
  if (!refreshed) return response;
  init.headers.set(TOKEN_HEADER, refreshed);
  return nativeFetch(retryInput, init);
}

/**
 * 旧画面のバイナリ取得も同じHTTP契約を通す。コア再起動に備え短時間だけ結果を共有する。
 * 各画面が直接書いた fetch もここで版ヘッダーと認証トークンが付く。
 * 版の照合が認証(401 `API_TOKEN_REQUIRED`)・Host 検査(421 `HOST_NOT_ALLOWED`)で断られたときはそのコードで失敗させる。
 * 版の不一致と取り違えると、トークンの問題なのに「配布物を更新して」と案内してしまうため。
 */
export function configureBackendRequests(baseUrl: string) {
  if (!baseUrl || configuredBase === baseUrl.replace(/\/+$/, "")) return;
  configuredBase = baseUrl.replace(/\/+$/, "");
  versionCheck = null; checkedAt = 0;
  globalThis.fetch = async (input, init) => {
    const url = input instanceof Request ? input.url : String(input);
    if (!isBackendApiUrl(url)) return nativeFetch(input, init);
    if (!url.endsWith("/system/version") && !url.includes("/system/shutdown")) {
      if (!versionCheck || (checkedAt !== 0 && Date.now() - checkedAt > 5000)) {
        versionCheck = (async () => {
          const response = await authorizedFetch(`${configuredBase}/api/v1/system/version`, { cache: "no-store", headers: new Headers() });
          if (!response.ok) {
            const body: Partial<ApiErrorResponse> = await response.json().catch(() => ({}));
            if ((response.status === 401 || response.status === 421) && body.code) {
              throw new ApiError(body.code, body.message ?? `HTTP ${response.status}`, response.status);
            }
            throw new ApiError("API_CONTRACT_MISMATCH", "Core version unavailable", response.status);
          }
          const version = await response.json();
          if (version.api_contract_version !== frontendVersion.api_contract_version) {
            throw new ApiError("API_CONTRACT_MISMATCH", `API: core ${version.api_contract_version ?? "?"} / UI ${frontendVersion.api_contract_version}`, 409);
          }
          const frontend = await invoke<{ error: string | null }>("get_frontend_status");
          if (frontend.error) throw new ApiError(frontend.error, frontend.error, 409);
          checkedAt = Date.now();
        })().catch(error => { versionCheck = null; throw error; });
      }
      await versionCheck;
    }
    const headers = new Headers(input instanceof Request ? input.headers : undefined);
    new Headers(init?.headers).forEach((value, key) => headers.set(key, value));
    headers.set("X-City-Genesis-API", String(frontendVersion.api_contract_version));
    return authorizedFetch(input, { ...init, headers });
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
      ...(apiToken && isBackendApiUrl(url) ? { [TOKEN_HEADER]: apiToken } : {}),
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
