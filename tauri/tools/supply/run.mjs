import { build } from "esbuild";
import { mkdir } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const output = resolve("node_modules/.cache/supply-tests.mjs");
const mock = resolve("tools/supply/mock-runtime.ts");
await mkdir(resolve("node_modules/.cache"), { recursive: true });
await build({ entryPoints: ["tools/supply/tests.tsx"], outfile: output, bundle: true,
  platform: "node", format: "esm", packages: "external", loader: { ".css": "empty", ".png": "empty" },
  alias: { "react-i18next": mock },
  plugins: [{ name: "supply-runtime", setup(builder) {
    builder.onResolve({ filter: /(?:contexts\/MasterDataContext|utils\/(?:api|agentIcon|pathIcon|resourceIcon))$/ }, () => ({ path: mock }));
  } }] });
await import(pathToFileURL(output).href);
