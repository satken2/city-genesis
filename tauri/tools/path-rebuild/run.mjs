import { build } from "esbuild";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { Window } from "happy-dom";

const window = new Window();
Object.assign(globalThis, { window, document: window.document, HTMLElement: window.HTMLElement,
  IS_REACT_ACT_ENVIRONMENT: true, testImageGlob: () => ({}) });
const output = resolve("node_modules/.cache/path-rebuild-tests.mjs");
const mock = resolve("tools/scenarios/mock-runtime.ts");
await build({ entryPoints: ["tools/path-rebuild/tests.tsx"], outfile: output, bundle: true,
  platform: "node", format: "esm", packages: "external", loader: { ".css": "empty", ".png": "empty", ".svg": "empty" },
  define: { "import.meta.glob": "globalThis.testImageGlob", "import.meta.env.DEV": "false", "import.meta.env.PROD": "false" },
  plugins: [{ name: "rebuild-master", setup(build) {
    build.onResolve({ filter: /contexts\/MasterDataContext$/ }, () => ({ path: resolve("tools/path-rebuild/mock-master.ts") }));
  } }],
  alias: { "@tauri-apps/api/core": mock, "@tauri-apps/api/event": mock, "react-i18next": mock } });
await import(pathToFileURL(output).href);
