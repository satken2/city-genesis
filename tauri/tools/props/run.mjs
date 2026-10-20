import { build } from "esbuild";
import { mkdir } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { Window } from "happy-dom";

const window = new Window();
Object.assign(globalThis, { window, document: window.document, HTMLElement: window.HTMLElement,
  IS_REACT_ACT_ENVIRONMENT: true, testImageGlob: () => ({}) });
const output = resolve("node_modules/.cache/props-tests.mjs");
const mock = resolve("tools/props/mock-runtime.ts");
await mkdir(resolve("node_modules/.cache"), { recursive: true });
await build({ entryPoints: ["tools/props/tests.tsx"], outfile: output, bundle: true,
  platform: "node", format: "esm", packages: "external", loader: { ".css": "empty" },
  define: { "import.meta.glob": "globalThis.testImageGlob" },
  plugins: [{ name: "props-runtime", setup(build) {
    build.onResolve({ filter: /MasterDataContext$|usePlayMode$|^@tauri-apps\/api\/(core|event|window)$|^react-i18next$/ },
      () => ({ path: mock }));
  } }] });
await import(pathToFileURL(output).href);
