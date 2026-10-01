export const runtimeState = { commands: [] as string[], events: [] as string[] };
export async function emit(event: string) { runtimeState.events.push(event); }
const listeners = new Map<string, (event: { payload: unknown }) => void>();

export async function invoke(command: string) {
  runtimeState.commands.push(command);
  if (command === "get_core_version") return { core_version: "1.0.0" };
  return { error: null };
}

const appWindow = {
  async listen(event: string, callback: (event: { payload: unknown }) => void) {
    listeners.set(event, callback);
    return () => { listeners.delete(event); };
  },
  async show() {},
  async hide() {},
};
export function getCurrentWindow() { return appWindow; }
export function emitSessionState(payload: unknown) { listeners.get("session-state")?.({ payload }); }
export function emitBackendConnection(connected: boolean) { listeners.get("backend-connection")?.({ payload: { connected } }); }
export async function openUrl() {}
export function useTranslation() { return { t: (key: string) => key }; }
