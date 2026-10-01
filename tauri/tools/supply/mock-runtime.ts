import i18n from "i18next";
import strings from "../../src/i18n/ja/supplyGraph.json";

await i18n.init({ lng: "ja", resources: { ja: { translation: strings } }, interpolation: { escapeValue: false } });
export function useTranslation() { return { t: i18n.t.bind(i18n), i18n }; }
export function useMasterData() { return null; }
export function resolveAgentIcon(variant: number) { return `/variant-${variant}.png`; }
export function resolveResourceIcon() { return "/test.png"; }
export function resolveTransitIcon() { return "/test.png"; }

type Request = { url: string; signal: AbortSignal; resolve: (value: unknown) => void };
export const runtime = { requests: [] as Request[] };
export async function fetchApi(url: string, options: { signal: AbortSignal }) {
  return new Promise((resolve) => runtime.requests.push({ url, signal: options.signal, resolve }));
}
