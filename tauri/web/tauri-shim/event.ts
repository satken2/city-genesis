/** ブラウザ版の `@tauri-apps/api/event`。イベントは Web シェルが Tauri v2 と同じ宛先の規則で配る。 */
import type { EventCallback, Options, UnlistenFn } from "@tauri-apps/api/event";
import type { ListenTarget } from "../shared/shellApi";
import { emitEvent, subscribe } from "./bridge";

export type { Event, EventCallback, EventName, Options, UnlistenFn } from "@tauri-apps/api/event";

export async function listen<T>(event: string, handler: EventCallback<T>, options?: Options): Promise<UnlistenFn> {
  return subscribe<T>(event, toTarget(options?.target), handler);
}

export async function emit<T>(event: string, payload?: T): Promise<void> {
  emitEvent(event, payload);
}

function toTarget(target: Options["target"]): ListenTarget {
  if (typeof target === "string") return { kind: "Window", label: target };
  if (target && "label" in target) return { kind: "Window", label: target.label };
  return { kind: "Any" };
}
