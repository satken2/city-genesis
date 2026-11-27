import { addListener, emit as publish, removeListener } from "../../web/shell/events";
import { runCommand } from "../../web/shell/commands";

export async function listen(event: string, callback: (value: { payload: unknown }) => void) {
  const id = addListener("opening-caption", event, { kind: "Window", label: "opening-caption" }, data => callback(JSON.parse(data)));
  return () => removeListener(id);
}

export async function emit(event: string, payload?: unknown) {
  publish(event, payload);
}

export function getCurrentWindow() {
  return { label: "opening-caption", listen };
}

export async function invoke(command: string, args: Record<string, unknown> = {}) {
  return runCommand("opening-caption", command, args);
}

export async function openUrl() {}
