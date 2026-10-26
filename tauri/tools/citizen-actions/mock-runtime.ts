export const state = {
  calls: [] as { command: string; args: any }[],
  /** 確認ウィンドウの答え。お知らせ(OK だけ)には答えない */
  answer: { confirmed: true } as { confirmed: boolean; amount?: number },
  emitted: [] as { event: string; payload: any }[],
};
const listeners = new Map<string, Set<(event: any) => void>>();
export async function listen(event: string, callback: (event: any) => void) {
  const callbacks = listeners.get(event) ?? new Set();
  callbacks.add(callback);
  listeners.set(event, callbacks);
  return () => { callbacks.delete(callback); };
}
function deliver(event: string, payload?: unknown) {
  for (const callback of [...(listeners.get(event) ?? [])]) callback({ payload });
}
export async function emit(event: string, payload?: unknown) { state.emitted.push({ event, payload }); deliver(event, payload); }
export async function invoke(command: string, args?: any) {
  state.calls.push({ command, args });
  const request = args?.params?.extra?.confirmRequest;
  if (command === "open_registered_window" && request && !request.noticeOnly) {
    queueMicrotask(() => deliver("confirm_dialog:result", { requestId: request.requestId, ...state.answer }));
  }
  return null;
}
export function useTranslation() { return { t: (key: string) => key, i18n: { language: "ja" } }; }
