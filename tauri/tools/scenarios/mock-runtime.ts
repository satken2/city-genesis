export const state = {
  calls: [] as { command: string; args: any }[],
  confirmed: true,
  /** コマンドごとの戻り値。未登録のコマンドは `{ error: null }` を返す */
  results: {} as Record<string, unknown>,
  /** コマンドごとの失敗(Rust の Err(String) と同じく文字列で reject する)。results より優先する */
  errors: {} as Record<string, string>,
};
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
  if (command in state.errors) throw state.errors[command];
  return command in state.results ? state.results[command] : { error: null };
}
const translate = (key: string) => key;
export function useTranslation() { return { t: translate, i18n: { language: "ja" } }; }
