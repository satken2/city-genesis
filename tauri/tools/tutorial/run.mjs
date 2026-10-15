import { build } from "esbuild";
import { mkdir, readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { readSchemaValidator, renderSchemaValidator } from "./generate-schema-validator.mjs";
if (renderSchemaValidator() !== readSchemaValidator()) {
  throw new Error("src/tutorial/schemaValidator.generated.ts is out of date. Run npm run gen:tutorial-validator.");
}
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
  plugin.onResolve({ filter: /\/?(tutorialSteps|useTutorialGuide|notification)$/ }, args => args.importer.replaceAll("\\", "/").endsWith("/src/components/tutorial/Tutorial.tsx") ? { path: mock } : undefined);
} }] });
await import(pathToFileURL(componentOutput).href + `?run=${Date.now()}`);
const playModeOutput = resolve("node_modules/.cache/play-mode-tests.mjs");
await build({ entryPoints: ["tools/tutorial/play-mode-tests.tsx"], outfile: playModeOutput, bundle: true,
  platform: "node", format: "esm", packages: "external", loader: { ".css": "empty", ".svg": "empty" },
  define: { "import.meta.glob": "globalThis.testImageGlob", "import.meta.env.BASE_URL": "\"/\"" },
  alias: { "@tauri-apps/api/event": mock, "@tauri-apps/api/window": mock, "@tauri-apps/api/core": mock, "react-i18next": mock } });
// パネル画像の一覧(import.meta.glob)は空にして、アイコンはメニュー画像の URL に倒す。
globalThis.testImageGlob = () => ({});
await import(pathToFileURL(playModeOutput).href + `?run=${Date.now()}`);
const shapeOutput = resolve("node_modules/.cache/tutorial-shape-editor-tests.mjs");
await build({ entryPoints: ["tools/tutorial/shape-editor-tests.tsx"], outfile: shapeOutput, bundle: true, platform: "node", format: "esm", packages: "external", loader: { ".css": "empty" }, define: { "import.meta.env.DEV": "false" }, alias: { "@tauri-apps/api/event": mock, "@tauri-apps/api/core": mock, "react-i18next": mock }, plugins: [{ name: "shape-editor-map-boundary", setup(plugin) {
  plugin.onResolve({ filter: /\/CityMapCanvas$/ }, () => ({ path: resolve("tools/tutorial/shape-map-mock.tsx") }));
} }] });
await import(pathToFileURL(shapeOutput).href + `?run=${Date.now()}`);
const registry = JSON.parse(await readFile("src-tauri/config/window-registry.json", "utf8"));
const LANGS = ["ja", "en", "es-419", "fr", "ko", "zh-Hans", "zh-Hant"];
const SCENARIOS = ["basic", "controls", "scenario2", "scenario3", "scenario4", "trade", "highway", "mining", "tourism", "reclamation", "acquisition"];
const walk = (value, visit) => { if (value && typeof value === "object") { visit(value); Object.values(value).forEach(v => walk(v, visit)); } };
const lookup = (text, key) => key.split(".").reduce((v, k) => v?.[k], text);
for (const name of SCENARIOS) {
  const definition = JSON.parse(await readFile(`src/tutorial/definitions/${name}.json`, "utf8"));
  for (const lang of LANGS) {
    const text = JSON.parse(await readFile(`src/i18n/${lang}/tutorial.json`, "utf8"));
    walk(definition, value => {
      if (value.textKey && !lookup(text, value.textKey)) throw new Error(`Missing translation ${lang}:${value.textKey}`);
      if (value.type === "window" && !registry[value.window]) throw new Error(`Unknown window ${value.window}`);
    });
    for (const step of definition.steps) {
      const key = `tutorial.${name}.step${step.legacyStep}.title`;
      if (!lookup(text, key)) throw new Error(`Missing step title ${lang}:${key}`);
    }
  }
}
for (const lang of LANGS) {
  const text = JSON.parse(await readFile(`src/i18n/${lang}/tutorial.json`, "utf8"));
  for (const id of [...SCENARIOS, "heading"]) {
    if (!text.tutorial.intro?.[id]) throw new Error(`Missing intro ${lang}:${id}`);
  }
}
if (!registry.tutorial_intro) throw new Error("Intro window is not registered");
console.log("PASS: definitions, translations, step titles, intro texts and window names");
