/**
 * API request helpers.
 */

import { decode } from "@msgpack/msgpack";
import { errorInfo, type ErrorInfo } from "./errors";
export { describeError, errorInfo } from "./errors";

/**
 * MessagePack response media type. Backend BBOX bulk endpoints (paths/parcels/buildings/zones/nodes) return
 * MessagePack instead of JSON when Accept includes this type, reducing transfer and parsing costs for large
 * coordinate arrays. Unsupported endpoints still return JSON; fetchApi callers need not distinguish formats
 * because Content-Type selects decoding automatically.
 */
const MSGPACK_MEDIA_TYPE = "application/x-msgpack";

/**
 * Build an API URL: strip trailing slashes from baseUrl and replace {id} in template with entityId.
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
 * コアのエラー応答。表示文は UI の翻訳リソースで作る。
 */
export type ApiErrorResponse = ErrorInfo;

/**
 * HTTP の状態と理由を保持する。Error.message は診断用のコードで、画面は describeError を使う。
 */
export class ApiError extends Error {
  code: string;
  status: number;
  params: Record<string, unknown>;

  constructor(code: string, params: Record<string, unknown>, status: number) {
    super(code);
    this.code = code;
    this.status = status;
    this.params = params;
    this.name = "ApiError";
  }
}

/** JSON 以外を取得する fetch 呼び出しも、失敗時は同じ契約で解釈する。 */
export async function requireApiSuccess(response: Response): Promise<void> {
  if (response.ok) return;
  const data: unknown = await response.json().catch(() => null);
  const info = errorInfo(data);
  throw new ApiError(info.code, info.params, response.status);
}

const nativeFetch = globalThis.fetch.bind(globalThis);
let configuredBase = "";
let backendConnected = true;

export function setBackendConnected(connected: boolean) {
  backendConnected = connected;
}

/** Whether the URL belongs to the configured backend API. */
function isBackendApiUrl(url: string): boolean {
  return configuredBase !== "" && url.startsWith(`${configuredBase}/api/v1/`);
}

/**
 * Route all backend requests here, including direct fetch calls in screens and binary retrieval. During
 * disconnection, do not send mutations that could execute twice if only the response is lost.
 */
export function configureBackendRequests(baseUrl: string) {
  if (!baseUrl || configuredBase === baseUrl.replace(/\/+$/, "")) return;
  configuredBase = baseUrl.replace(/\/+$/, "");
  globalThis.fetch = async (input, init) => {
    const url = input instanceof Request ? input.url : String(input);
    if (!isBackendApiUrl(url)) return nativeFetch(input, init);
    const method = (init?.method ?? (input instanceof Request ? input.method : "GET")).toUpperCase();
    // Queuing/retrying mutations risks executing an operation twice when only its response was lost.
    if (!backendConnected && !["GET", "HEAD", "OPTIONS"].includes(method)) {
      throw new ApiError("BACKEND_DISCONNECTED", {}, 503);
    }
    return nativeFetch(input, init);
  };
}

/**
 * Fetch API data. Always advertise MessagePack in Accept; decode MessagePack binary or JSON automatically by
 * Content-Type. エラーは code/params のまま保持し、表示時に describeError へ渡す。
 */
export async function fetchApi<T>(
  url: string,
  options?: { method?: string; headers?: Record<string, string>; body?: BodyInit; responseType?: "blob"; signal?: AbortSignal; cache?: RequestCache }
): Promise<T> {
  // Record confirmation diagnostics in existing frontend error logs, distinguishing failures before HTTP dispatch from response processing failures.
  const diagnoseParcelCreation = import.meta.env?.DEV && options?.method === "POST"
    && /\/api\/v1\/parcels\/?(?:\?|$)/.test(url);
  const response = await fetch(url, {
    method: options?.method ?? "GET",
    headers: {
      Accept: `${MSGPACK_MEDIA_TYPE}, application/json`,
      ...options?.headers,
    },
    body: options?.body,
    signal: options?.signal,
    cache: options?.cache,
  });
  if (diagnoseParcelCreation) {
    console.error("[ParcelDiagnostic] HTTP response", {
      requestedUrl: url,
      responseUrl: response.url,
      status: response.status,
      redirected: response.redirected,
      contentType: response.headers.get("content-type"),
    });
  }
  await requireApiSuccess(response);
  if (response.status === 204) return undefined as T;
  if (options?.responseType === "blob") return await response.blob() as T;
  const contentType = response.headers.get("content-type") ?? "";
  if (contentType.includes(MSGPACK_MEDIA_TYPE)) {
    // MessagePack binary: fast decoding for bulk responses with
    // large coordinate arrays. decode() expands maps into plain
    // objects (JavaScript converts integer keys to strings, matching JSON).
    return decode(new Uint8Array(await response.arrayBuffer())) as T;
  }
  return response.json();
}
