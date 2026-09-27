export const state = { calls: [] as { command: string; args: any }[], confirmed: true };
const listeners = new Map<string, (event: any) => void>();
export async function listen(event: string, callback: (event: any) => void) {
  listeners.set(event, callback);
  return () => { listeners.delete(event); };
}
export async function emit(event: string, payload?: unknown) { listeners.get(event)?.({ payload }); }
export async function invoke(command: string, args?: any) {
  state.calls.push({ command, args });
  if (command === "open_registered_window" && args.windowType === "confirm_dialog") {
    queueMicrotask(() => listeners.get("confirm_dialog:result")?.({ payload: {
      requestId: args.params.extra.confirmRequest.requestId, confirmed: state.confirmed,
    } }));
  }
  return { error: null };
}
const translate = (key: string) => key;
export function useTranslation() { return { t: translate }; }
export async function save() { return null; }
