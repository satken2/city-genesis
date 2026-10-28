import { build } from "esbuild";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { Window } from "happy-dom";

const window = new Window();
Object.assign(globalThis, { window, document: window.document, HTMLElement: window.HTMLElement,
  IS_REACT_ACT_ENVIRONMENT: true, ResizeObserver: class { observe() {} disconnect() {} } });
globalThis.fetch = async url => {
  if (String(url).endsWith("/sessions")) return Response.json({ play_mode: 0 });
  if (String(url).endsWith("/api/v1/city/")) return Response.json({ city: { simulation_days: 30 } });
  globalThis.policyRequests = (globalThis.policyRequests ?? 0) + 1;
  return Response.json(globalThis.policyPayload);
};
const output = resolve("node_modules/.cache/policy-tests.mjs");
const mock = resolve("tools/policy/mock-runtime.ts");
await build({ entryPoints: ["tools/policy/tests.tsx"], outfile: output, bundle: true,
  platform: "node", format: "esm", packages: "external", loader: { ".css": "empty" },
  define: { "import.meta.env.DEV": "false" },
  alias: { "@tauri-apps/api/core": mock, "@tauri-apps/api/event": mock, "@tauri-apps/api/window": mock } });
await import(pathToFileURL(output).href);
