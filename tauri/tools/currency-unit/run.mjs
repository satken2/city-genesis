import { build } from "esbuild";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { Window } from "happy-dom";

const window = new Window();
Object.assign(globalThis, { window, document: window.document, HTMLElement: window.HTMLElement,
  localStorage: window.localStorage, IS_REACT_ACT_ENVIRONMENT: true });
Object.defineProperty(globalThis, "navigator", { value: window.navigator, configurable: true });
const output = resolve("node_modules/.cache/currency-unit-tests.mjs");
const runtime = resolve("tools/currency-unit/runtime.ts");
await build({ entryPoints: ["tools/currency-unit/tests.tsx"], outfile: output, bundle: true,
  platform: "node", format: "esm", packages: "external", loader: { ".css": "empty" },
  alias: { "@tauri-apps/api/core": runtime, "@tauri-apps/api/event": runtime, "@tauri-apps/plugin-opener": runtime } });
await import(pathToFileURL(output).href);
