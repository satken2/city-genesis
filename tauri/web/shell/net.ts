/** Web シェルからコアへの HTTP。Rust の reqwest と同じく、要求ごとに時間切れを持つ。 */
import { state } from "./state";

/** コアの URL(末尾のスラッシュなし)。 */
export function apiBase(): string {
  return state.apiBaseUrl.replace(/\/+$/, "");
}

export async function fetchWithTimeout(url: string, timeoutMs: number, init: RequestInit = {}): Promise<Response> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    return await fetch(url, { ...init, signal: controller.signal });
  } finally {
    clearTimeout(timer);
  }
}

export function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
