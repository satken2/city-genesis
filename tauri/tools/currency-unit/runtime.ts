import { loadSettings, saveSettings } from "../../web/shell/settings";

export const state = { nextLoad: null as Promise<unknown> | null };
const listeners = new Map<string, Set<(event: any) => void>>();
export async function listen(event: string, callback: (event: any) => void) {
  const callbacks = listeners.get(event) ?? new Set();
  callbacks.add(callback);
  listeners.set(event, callbacks);
  return () => { callbacks.delete(callback); };
}
export async function emit(event: string, payload?: unknown) {
  for (const callback of listeners.get(event) ?? []) callback({ payload });
}
export async function invoke(command: string, args?: any) {
  if (command === "load_app_settings") {
    const pending = state.nextLoad;
    state.nextLoad = null;
    return pending ?? loadSettings();
  }
  if (command === "save_app_settings") saveSettings(args.settings);
  return null;
}
export async function openUrl() {}
