import { build } from "esbuild";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { Window } from "happy-dom";

const window = new Window();
Object.assign(globalThis, {
  window,
  document: window.document,
  HTMLElement: window.HTMLElement,
  localStorage: window.localStorage,
  IS_REACT_ACT_ENVIRONMENT: true,
});
Object.defineProperty(globalThis, "navigator", { value: window.navigator, configurable: true });
// api.ts は読み込み時に globalThis.fetch を束縛するため、モジュールの読み込み前にディスパッチャを差し込む。
const nativeFetch = globalThis.fetch.bind(globalThis);
globalThis.fetch = (input, init) => (globalThis.__mockFetch ?? nativeFetch)(input, init);
const output = resolve("node_modules/.cache/topmenu-first-launch-tests.mjs");
const runtime = resolve("tools/topmenu-first-launch/runtime.ts");
await build({
  entryPoints: ["tools/topmenu-first-launch/tests.tsx"],
  outfile: output,
  bundle: true,
  platform: "node",
  format: "esm",
  packages: "external",
  loader: { ".css": "empty", ".png": "dataurl", ".svg": "dataurl" },
  alias: {
    "@tauri-apps/api/core": runtime,
    "@tauri-apps/api/event": runtime,
    "@tauri-apps/api/window": runtime,
    "@tauri-apps/plugin-opener": runtime,
  },
});
await import(pathToFileURL(output).href);
