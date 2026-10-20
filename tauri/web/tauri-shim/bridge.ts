/**
 * ブラウザ版で `@tauri-apps/*` の代わりに読み込まれる差し替えモジュールの共通部分。
 * 各ウィンドウ(iframe)から、親のページにある Web シェルの窓口を呼ぶ。
 */
import {
  SHELL_GLOBAL,
  WINDOW_LABEL_ATTRIBUTE,
  type InvokeReply,
  type ListenTarget,
  type PopupMenuRequest,
  type ShellApi,
  type WindowOp,
} from "../shared/shellApi";

export type ShimEvent<T> = { event: string; id: number; payload: T };

/** このウィンドウのラベル。シェルが iframe の要素に付けて渡す。 */
export const currentLabel = window.frameElement?.getAttribute(WINDOW_LABEL_ATTRIBUTE) ?? "";

function shell(): ShellApi {
  const api = (window.parent as unknown as Record<string, ShellApi | undefined>)[SHELL_GLOBAL];
  if (!api) throw new Error("Web シェルが見つかりません");
  return api;
}

function unwrap(replyJson: string): unknown {
  const reply = JSON.parse(replyJson) as InvokeReply;
  if (reply.ok) return reply.value;
  throw reply.error;
}

export async function invokeCommand(command: string, args: unknown): Promise<unknown> {
  return unwrap(await shell().invoke(currentLabel, command, JSON.stringify(args ?? {})));
}

export function subscribe<T>(event: string, target: ListenTarget, handler: (event: ShimEvent<T>) => void): () => void {
  const id = shell().listen(currentLabel, event, JSON.stringify(target), (json) => handler(JSON.parse(json) as ShimEvent<T>));
  return () => shell().unlisten(id);
}

export function emitEvent(event: string, payload: unknown): void {
  shell().emit(event, JSON.stringify(payload ?? null));
}

export async function windowOp(op: WindowOp, args: unknown = {}): Promise<unknown> {
  return unwrap(await shell().windowOp(currentLabel, op, JSON.stringify(args)));
}

export async function popupMenu(request: PopupMenuRequest, onSelect: (id: string) => void, onClose: () => void): Promise<void> {
  unwrap(await shell().popupMenu(currentLabel, JSON.stringify(request), onSelect, onClose));
}

export function closeMenu(id: string): void {
  shell().closeMenu(currentLabel, id);
}

// OS のウィンドウはクリックで前面に出る。iframe でも押した時点でシェルに前面化を頼む。
// ドラッグはマウスを押した位置から測るので、その画面座標も覚えておく。
let lastPointer = { x: 0, y: 0 };
if (currentLabel) {
  window.addEventListener(
    "pointerdown",
    (event) => {
      lastPointer = { x: event.screenX, y: event.screenY };
      void windowOp("activate").catch(() => {});
    },
    true,
  );
}

/**
 * Tauri の startDragging と同じく、押したままのマウスに合わせてウィンドウを動かす。
 * ボタンを押している間のマウスイベントは押した iframe に届き続けるので、ここで受けて移動量をシェルへ送る。
 * iframe 自体が動くので、移動量はウィンドウ内の座標ではなく画面座標の差で測る。
 */
export function startWindowDrag(): void {
  let last = lastPointer;
  const move = (event: MouseEvent) => {
    const dx = event.screenX - last.x;
    const dy = event.screenY - last.y;
    last = { x: event.screenX, y: event.screenY };
    if (dx !== 0 || dy !== 0) void windowOp("moveBy", { dx, dy });
  };
  const end = () => {
    window.removeEventListener("mousemove", move, true);
    window.removeEventListener("mouseup", end, true);
    void windowOp("moveEnd");
  };
  window.addEventListener("mousemove", move, true);
  window.addEventListener("mouseup", end, true);
}
