export const state = {
  calls: [] as { command: string; args: any }[],
  confirmed: true,
  /** コマンドごとの戻り値。未登録のコマンドは `{ error: null }` を返す */
  results: {} as Record<string, unknown>,
  /** コマンドごとの失敗。構造化エラーもそのまま reject する。results より優先する */
  errors: {} as Record<string, unknown>,
  /** emit されたイベント名(他のウィンドウへ知らせたかを確かめる) */
  emitted: [] as string[],
};
// Tauri と同じく、1つのイベントに複数の受け手を登録できる
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
export async function emit(event: string, payload?: unknown) { state.emitted.push(event); deliver(event, payload); }
export async function invoke(command: string, args?: any) {
  state.calls.push({ command, args });
  if (command === "open_registered_window" && args.windowType === "confirm_dialog") {
    queueMicrotask(() => deliver("confirm_dialog:result", {
      requestId: args.params.extra.confirmRequest.requestId, confirmed: state.confirmed,
    }));
  }
  if (command in state.errors) throw state.errors[command];
  return command in state.results ? state.results[command] : { error: null };
}
const translate = (key: string) => key;
/** 言語ごとの翻訳は「言語:キー」を返し、どの言語で引いたかを確かめられるようにする */
const fixedT = (language: string) => (key: string) => `${language}:${key}`;
export function useTranslation() { return { t: translate, i18n: { language: "ja", getFixedT: fixedT } }; }
