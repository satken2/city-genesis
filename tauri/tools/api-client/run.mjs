import { build } from "esbuild";
import { mkdir } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const output = resolve("node_modules/.cache/api-client-tests.mjs");
const mock = resolve("tools/api-client/mock-runtime.ts");
await mkdir(resolve("node_modules/.cache"), { recursive: true });
await build({ entryPoints: ["tools/api-client/tests.tsx"], outfile: output, bundle: true,
  platform: "node", format: "esm", packages: "external", alias: { "@tauri-apps/api/core": mock } });
await import(pathToFileURL(output).href);
