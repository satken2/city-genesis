import { build } from "esbuild";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { Window } from "happy-dom";

const window = new Window();
Object.assign(globalThis, { window, document: window.document, HTMLElement: window.HTMLElement,
  IS_REACT_ACT_ENVIRONMENT: true });
const output = resolve("node_modules/.cache/citizen-actions-tests.mjs");
const mock = resolve("tools/citizen-actions/mock-runtime.ts");
await build({ entryPoints: ["tools/citizen-actions/tests.tsx"], outfile: output, bundle: true,
  platform: "node", format: "esm", packages: "external", loader: { ".css": "empty", ".svg": "empty" },
  define: { "import.meta.env.DEV": "false" },
  alias: { "@tauri-apps/api/core": mock, "@tauri-apps/api/event": mock, "react-i18next": mock } });
await import(pathToFileURL(output).href);
