import { build } from "esbuild";
import { mkdir, readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
const output = resolve("node_modules/.cache/tutorial-tests.mjs");
await mkdir(resolve("node_modules/.cache"), { recursive: true });
await build({ entryPoints: ["tools/tutorial/tests.ts"], outfile: output, bundle: true, platform: "node", format: "esm", packages: "external" });
await import(pathToFileURL(output).href + `?run=${Date.now()}`);
const hookOutput = resolve("node_modules/.cache/tutorial-hook-tests.mjs");
const mock = resolve("tools/tutorial/mock-runtime.ts");
await build({ entryPoints: ["tools/tutorial/hook-tests.tsx"], outfile: hookOutput, bundle: true, platform: "node", format: "esm", packages: "external", alias: { "@tauri-apps/api/event": mock }, plugins: [{ name: "tutorial-boundaries", setup(plugin) {
  plugin.onResolve({ filter: /\/(environment|MasterDataContext|ipc)$/ }, args => args.importer.replaceAll("\\", "/").includes("/src/tutorial/") ? { path: mock } : undefined);
} }] });
await import(pathToFileURL(hookOutput).href + `?run=${Date.now()}`);
const componentOutput = resolve("node_modules/.cache/tutorial-component-tests.mjs");
await build({ entryPoints: ["tools/tutorial/component-tests.tsx"], outfile: componentOutput, bundle: true, platform: "node", format: "esm", packages: "external", loader: { ".css": "empty" }, define: { "import.meta.env.DEV": "false" }, alias: { "@tauri-apps/api/event": mock, "@tauri-apps/api/window": mock, "@tauri-apps/api/core": mock, "react-i18next": mock }, plugins: [{ name: "tutorial-component-boundaries", setup(plugin) {
  plugin.onResolve({ filter: /\/(environment|MasterDataContext|ipc)$/ }, args => args.importer.replaceAll("\\", "/").includes("/src/tutorial/") ? { path: mock } : undefined);
  plugin.onResolve({ filter: /\/?(tutorialSteps|tutorialSetup|notification)$/ }, args => args.importer.replaceAll("\\", "/").endsWith("/src/components/tutorial/Tutorial.tsx") ? { path: mock } : undefined);
} }] });
await import(pathToFileURL(componentOutput).href + `?run=${Date.now()}`);
const registry = JSON.parse(await readFile("src-tauri/config/window-registry.json", "utf8"));
for (const name of ["basic", "scenario3"]) {
  const definition = JSON.parse(await readFile(`src/tutorial/definitions/${name}.json`, "utf8"));
  const walk = (value, visit) => { if (value && typeof value === "object") { visit(value); Object.values(value).forEach(v => walk(v, visit)); } };
  for (const lang of ["ja", "en", "es-419", "fr", "ko", "zh-Hans", "zh-Hant"]) {
    const text = JSON.parse(await readFile(`src/i18n/${lang}/tutorial.json`, "utf8"));
    walk(definition, value => {
      if (value.textKey && !value.textKey.split(".").reduce((v, k) => v?.[k], text)) throw new Error(`Missing translation ${lang}:${value.textKey}`);
      if (value.type === "window" && !registry[value.window]) throw new Error(`Unknown window ${value.window}`);
    });
  }
}
let steps = 0;
for (const name of ["basic", "scenario2", "scenario3", "scenario4"]) {
  const text = await readFile(`../docs/tutorials/${name}.md`, "utf8");
  steps += [...text.matchAll(/^ID: `/gm)].length;
}
if (steps !== 32) throw new Error(`Expected 32 design steps, got ${steps}`);
console.log("PASS: definitions, translations, window names and 32-step design inventory");
