/** ブラウザ版の `@tauri-apps/api/core`。コマンドは Web シェルが Rust の代わりに実行する。 */
import { invokeCommand } from "./bridge";

export async function invoke<T>(command: string, args?: Record<string, unknown>): Promise<T> {
  return (await invokeCommand(command, args)) as T;
}
