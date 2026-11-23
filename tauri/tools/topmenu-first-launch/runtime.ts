import { loadSettings, saveSettings } from "../../web/shell/settings";

export type InvokedCall = { command: string; args: Record<string, unknown> | undefined };

export const state = {
  invocations: [] as InvokedCall[],
  confirmAnswer: false as boolean | null,
};

const listeners = new Map<string, Set<(event: { payload: unknown }) => void>>();

export function resetRuntime() {
  state.invocations = [];
  state.confirmAnswer = false;
  listeners.clear();
}

export async function listen(event: string, callback: (event: { payload: unknown }) => void) {
  const callbacks = listeners.get(event) ?? new Set();
  callbacks.add(callback);
  listeners.set(event, callbacks);
  return () => {
    callbacks.delete(callback);
  };
}

export async function emit(event: string, payload?: unknown) {
  for (const callback of [...(listeners.get(event) ?? [])]) {
    callback({ payload });
  }
}

export function getCurrentWindow() {
  return {
    label: "topmenu",
    listen,
    show: async () => {},
    hide: async () => {},
    minimize: async () => {},
  };
}

export async function invoke(command: string, args?: Record<string, unknown>) {
  state.invocations.push({ command, args });
  if (command === "get_core_version") return { core_version: "1.0.0" };
  if (command === "load_app_settings") return loadSettings();
  if (command === "save_app_settings") {
    saveSettings(args?.settings);
    return null;
  }
  if (command === "open_registered_window" && args?.windowType === "confirm_dialog" && state.confirmAnswer !== null) {
    const params = args.params as { extra?: { confirmRequest?: { requestId?: string } } } | undefined;
    const requestId = params?.extra?.confirmRequest?.requestId;
    if (requestId) {
      const confirmed = state.confirmAnswer;
      setTimeout(() => {
        void emit("confirm_dialog:result", { requestId, confirmed });
      }, 0);
    }
  }
  return null;
}

export async function openUrl() {}
