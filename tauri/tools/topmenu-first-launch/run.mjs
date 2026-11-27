import { build } from "esbuild";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { Window } from "happy-dom";

const window = new Window({ url: "http://localhost/", settings: { disableIframePageLoading: true } });
Object.assign(globalThis, {
  window,
  document: window.document,
  location: window.location,
  HTMLElement: window.HTMLElement,
  localStorage: window.localStorage,
  IS_REACT_ACT_ENVIRONMENT: true,
});
Object.defineProperty(globalThis, "navigator", { value: window.navigator, configurable: true });
const output = resolve("node_modules/.cache/topmenu-first-launch-tests.mjs");
const runtime = resolve("tools/topmenu-first-launch/runtime.ts");
await build({
  entryPoints: ["tools/topmenu-first-launch/tests.tsx"],
  outfile: output,
  bundle: true,
  platform: "node",
  format: "esm",
  packages: "external",
  define: { "import.meta.env": JSON.stringify({ DEV: true, VITE_CORE_API_URL: "http://localhost" }) },
  loader: { ".css": "empty", ".png": "dataurl", ".svg": "dataurl" },
  alias: {
    "@tauri-apps/api/core": runtime,
    "@tauri-apps/api/event": runtime,
    "@tauri-apps/api/window": runtime,
    "@tauri-apps/plugin-opener": runtime,
  },
});
for (const mode of ["first", "returning", "legacy", "skip", "no-graphic"]) {
  await import(pathToFileURL(output).href + "?" + mode);
}
