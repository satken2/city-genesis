import i18n from "i18next";
import strings from "../../src/i18n/ja/supplyGraph.json";

await i18n.init({ lng: "ja", resources: { ja: { translation: strings } }, interpolation: { escapeValue: false } });
export function useTranslation() { return { t: i18n.t.bind(i18n), i18n }; }
export function useMasterData() { return null; }
export function resolveAgentIcon() { return "/test.png"; }
export function resolveResourceIcon() { return "/test.png"; }
export function resolveTransitIcon() { return "/test.png"; }

type Request = { url: string; signal: AbortSignal; resolve: (value: unknown) => void };
export const runtime = { visible: true, requests: [] as Request[] };
const events = new Map<string, (event: { payload: unknown }) => void>();
let focus: ((event: { payload: boolean }) => void) | null = null;
export async function listen(event: string, callback: (event: { payload: unknown }) => void) {
  events.set(event, callback);
  return () => { events.delete(event); };
}
export function dispatch(event: string, payload?: unknown) { events.get(event)?.({ payload }); }
export function gainFocus() { focus?.({ payload: true }); }
export function getCurrentWindow() { return {
  async isVisible() { return runtime.visible; },
  async onFocusChanged(callback: typeof focus) { focus = callback; return () => { focus = null; }; },
}; }
export async function fetchApi(url: string, options: { signal: AbortSignal }) {
  return new Promise((resolve) => runtime.requests.push({ url, signal: options.signal, resolve }));
}
