/**
 * Tauri のイベントの配送。宛先の規則は Tauri v2 と同じで、全体向けの emit は全購読者に、
 * emitTo(label) は「全体向けの購読(Any)」と「その label のウィンドウの購読」に届く。
 *
 * Tauri はイベントを IPC 越しに後から届けるので、ここでも送った処理が終わってから順番に配る。
 * 送った処理の途中で受け手の処理(そこからのコマンド呼び出しを含む)が割り込まないようにするため。
 */
import type { ListenTarget } from "../shared/shellApi";

interface Listener {
  id: number;
  owner: string;
  event: string;
  target: ListenTarget;
  handler: (eventJson: string) => void;
}

const listeners = new Map<number, Listener>();
let nextListenerId = 1;
const queue: { event: string; payload: unknown; to: string | null }[] = [];
let flushScheduled = false;

export function addListener(owner: string, event: string, target: ListenTarget, handler: (eventJson: string) => void): number {
  const id = nextListenerId++;
  listeners.set(id, { id, owner, event, target, handler });
  return id;
}

export function removeListener(id: number): void {
  listeners.delete(id);
}

/** 破棄したウィンドウの購読を消す(WebView の破棄で Tauri が購読を消すのと同じ)。 */
export function removeListenersOf(owner: string): void {
  for (const [id, listener] of listeners) if (listener.owner === owner) listeners.delete(id);
}

export function emit(event: string, payload: unknown = null): void {
  enqueue(event, payload, null);
}

export function emitTo(label: string, event: string, payload: unknown = null): void {
  enqueue(event, payload, label);
}

function enqueue(event: string, payload: unknown, to: string | null): void {
  queue.push({ event, payload, to });
  if (!flushScheduled) {
    flushScheduled = true;
    setTimeout(flush, 0);
  }
}

function flush(): void {
  flushScheduled = false;
  for (const item of queue.splice(0)) {
    for (const listener of [...listeners.values()]) {
      if (listener.event !== item.event || !listeners.has(listener.id)) continue;
      if (item.to !== null && listener.target.kind !== "Any" && listener.target.label !== item.to) continue;
      try {
        listener.handler(JSON.stringify({ event: item.event, id: listener.id, payload: item.payload ?? null }));
      } catch (error) {
        console.error(`[events] ${item.event} の配送に失敗しました (${listener.owner})`, error);
      }
    }
  }
}
