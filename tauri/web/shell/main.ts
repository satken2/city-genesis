/**
 * ブラウザ版のページ本体(Web シェル)。exe で Rust(`src-tauri`)が受け持つアプリの制御を行い、
 * 全面に Unity の canvas を置いて、その上に UI の各ウィンドウを iframe として重ねる。
 */
import "./shell.css";
import { SHELL_GLOBAL, type InvokeReply, type ListenTarget, type ShellApi, type WindowOp } from "../shared/shellApi";
import { runCommand } from "./commands";
import { CORE_API_URL } from "./config";
import { addListener, emit, removeListener, removeListenersOf } from "./events";
import * as host from "./host";
import { installGraphicBridge, makeEnvelope, sendToParent } from "./ipc";
import { startGraphic } from "./process";
import { startSessionPolling } from "./session";
import { loadSettings } from "./settings";
import { state } from "./state";
import { definition } from "./version";
import { createStartupWindows, onWindowDestroyed, recordWindowGeometry, relayoutForViewport } from "./windows";

function errorValue(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

async function reply(run: () => unknown): Promise<string> {
  // Tauri の IPC と同じく、呼び出した側の処理が終わってから実行する。
  await Promise.resolve();
  try {
    const value = await run();
    return JSON.stringify({ ok: true, value: value ?? null } satisfies InvokeReply);
  } catch (error) {
    return JSON.stringify({ ok: false, error: errorValue(error) } satisfies InvokeReply);
  }
}

function windowOperation(label: string, op: WindowOp, args: Record<string, unknown>): unknown {
  switch (op) {
    case "show":
      return host.show(label);
    case "hide":
      return host.hide(label);
    case "isVisible":
      return host.isVisible(label);
    case "setSize":
      return host.setSize(label, Number(args.width), Number(args.height));
    case "minimize":
      throw "ブラウザ版ではウィンドウを最小化できません";
    case "moveBy":
      return host.moveBy(label, Number(args.dx), Number(args.dy));
    case "moveEnd":
      return host.moveEnd(label);
    case "activate":
      return host.raise(label);
  }
}

const shellApi: ShellApi = {
  invoke: (label, command, argsJson) => reply(() => runCommand(label, command, JSON.parse(argsJson) as Record<string, unknown>)),
  listen: (label, event, targetJson, handler) => addListener(label, event, JSON.parse(targetJson) as ListenTarget, handler),
  unlisten: (id) => removeListener(id),
  emit: (event, payloadJson) => emit(event, JSON.parse(payloadJson)),
  windowOp: (label, op, argsJson) => reply(() => windowOperation(label, op, JSON.parse(argsJson) as Record<string, unknown>)),
};

function start(): void {
  if (!CORE_API_URL) console.error("[web] コアの URL(VITE_CORE_API_URL)が設定されていません");
  (window as unknown as Record<string, ShellApi>)[SHELL_GLOBAL] = shellApi;
  host.initHost(document.getElementById("windows") as HTMLElement, {
    destroyed: (label) => {
      removeListenersOf(label);
      onWindowDestroyed(label);
    },
    geometryChanged: (label, moved) => recordWindowGeometry(label, moved),
  });

  // exe は IPC を開いた直後に system:ready を送り、Unity が受信を始めるまで共有メモリに溜める。
  installGraphicBridge();
  sendToParent(makeEnvelope("tauri", "parent", "system:ready", definition()));

  const settings = loadSettings();
  state.multiDisplay = settings.multi_display;
  state.popupMonitorIndex = settings.popup_monitor_index;
  createStartupWindows();
  window.addEventListener("resize", relayoutForViewport);

  void startGraphic(document.getElementById("graphic") as HTMLCanvasElement);
  void startSessionPolling();
}

start();
