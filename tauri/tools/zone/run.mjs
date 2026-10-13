import { build } from "esbuild";
import { mkdir } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { Window } from "happy-dom";

const window = new Window();
Object.assign(globalThis, { window, document: window.document, HTMLElement: window.HTMLElement,
  IS_REACT_ACT_ENVIRONMENT: true, testImageGlob: () => ({}) });
const output = resolve("node_modules/.cache/zone-tests.mjs");
const mock = resolve("tools/scenarios/mock-runtime.ts");
await mkdir(resolve("node_modules/.cache"), { recursive: true });
await build({ entryPoints: ["tools/zone/tests.tsx"], outfile: output, bundle: true,
  platform: "node", format: "esm", packages: "external", loader: { ".css": "empty", ".png": "empty" },
  define: { "import.meta.glob": "globalThis.testImageGlob", "import.meta.env.PROD": "false" },
  plugins: [{ name: "zone-map-mock", setup(build) {
    build.onResolve({ filter: /^\.\.\/map\/CityMapCanvas$/ }, () => ({ path: resolve("tools/zone/mock-map.tsx") }));
  } }],
  alias: { "@tauri-apps/api/core": mock, "@tauri-apps/api/event": mock, "react-i18next": mock } });
await import(pathToFileURL(output).href);
