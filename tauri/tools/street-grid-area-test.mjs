import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { build } from "esbuild";
import { Window } from "happy-dom";
import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import { mockIPC, mockWindows } from "@tauri-apps/api/mocks";
import { act, createElement } from "react";

const window = new Window();
Object.assign(globalThis, { window, document: window.document, HTMLElement: window.HTMLElement,
  HTMLInputElement: window.HTMLInputElement, ResizeObserver: window.ResizeObserver, IS_REACT_ACT_ENVIRONMENT: true });
mockWindows("city_development");
mockIPC(() => null, { shouldMockEvents: true });
await i18n.use(initReactI18next).init({ lng: "ja", resources: { ja: {
  translation: {
    ...JSON.parse(await readFile("src/i18n/ja/scenarioTools.json", "utf8")),
    ...JSON.parse(await readFile("src/i18n/ja/cityDevelopment.json", "utf8")),
  },
} } });
const { createRoot } = await import("react-dom/client");
const output = resolve("node_modules/.cache/street-grid-area-test.mjs");
await build({ stdin: { contents: `export { CityDevelopmentWindow } from "./src/components/development/CityDevelopmentWindow";
  export { master } from "./tools/path-rebuild/mock-master";`, resolveDir: process.cwd() },
  outfile: output, bundle: true, platform: "node", format: "esm", packages: "external", loader: { ".css": "empty" },
  plugins: [{ name: "street-master", setup(b) {
    b.onResolve({ filter: /\/contexts\/MasterDataContext$/ }, () => ({ path: resolve("tools/path-rebuild/mock-master.ts") }));
  } }],
  define: { "import.meta.env.PROD": "false" } });

// 地形の取得だけを保留し、地図の座標変換・パン・クリック判定は実際のキャンバスを通す。
Object.defineProperties(window.HTMLDivElement.prototype, {
  clientWidth: { get: () => 500 }, clientHeight: { get: () => 500 },
});
window.HTMLCanvasElement.prototype.getBoundingClientRect = () => new window.DOMRect(0, 0, 500, 500);
window.HTMLCanvasElement.prototype.getContext = () => new Proxy({}, { get: () => () => {} });
const requests = [];
globalThis.fetch = async (url, init) => {
  if (url.includes("/city-development")) {
    const body = JSON.parse(init.body);
    requests.push(body);
    return Response.json(url.endsWith("/preview") ? {
      roads: [{ path_variant: body.path_variants[0], points: [[0, 0], [100, 0]] }],
      parcels: [], rejected: {}, area_m2: 10000,
    } : { path_ids: [1], parcel_ids: [], rejected: {} });
  }
  assert.ok(!url.includes("/street-grid"));
  return new Promise(() => {});
};
const { CityDevelopmentWindow, master } = await import(pathToFileURL(output).href);
master.data = { paths: {
  212: { type: 1, frontage_allowed: true, lanes: 1, width: 6 },
  221: { type: 1, frontage_allowed: true, lanes: 2, width: 12 },
  231: { type: 1, frontage_allowed: true, lanes: 4, width: 24, template_only: true },
} };
const host = document.createElement("div");
document.body.append(host);
const root = createRoot(host);
try {
  await act(async () => root.render(createElement(CityDevelopmentWindow, { apiBaseUrl: "http://fixture" })));
  const canvas = host.querySelector("canvas");
  const [clear, preview, create] = host.querySelectorAll("button");
  const pointer = (type, x, y, ctrlKey = false) => act(async () => {
    canvas.dispatchEvent(new window.PointerEvent(type, { bubbles: true, pointerId: 1, button: 0, clientX: x, clientY: y, ctrlKey }));
  });
  const paint = async (x, y) => {
    await pointer("pointerdown", x, y, true);
    await pointer("pointermove", x + 50, y + 50, true);
    await pointer("pointerup", x + 50, y + 50, true);
  };
  const showPlan = () => act(async () => preview.click());
  const guide = name => host.querySelector(`[data-guide="city_development.${name}"]`);
  const algorithm = guide("pattern");
  const select = (input, value) => act(async () => {
    input.value = value;
    input.dispatchEvent(new window.Event("change", { bubbles: true }));
  });
  const roadTypes = () => host.querySelectorAll('input[type="checkbox"]');
  assert.equal(algorithm.value, "arterial");
  assert.equal(algorithm.querySelectorAll("optgroup").length, 0);
  assert.deepEqual([...algorithm.options].map(option => option.value),
    ["grid", "terrain_grid", "arterial", "neighborhood", "branching", "downtown_grid", "subdivision",
      "relaxed_grid", "contour", "organic", "block"]);
  assert.equal(guide("block_pattern"), null);
  assert.equal(roadTypes().length, 2);
  assert.deepEqual([...roadTypes()].map(input => input.checked), [true, true]);

  await pointer("pointerdown", 50, 50);
  await pointer("pointerup", 50, 50);
  assert.ok(preview.disabled);
  await paint(50, 50);
  await showPlan();
  const first = requests.at(-1);
  assert.equal(first.pattern, "arterial");
  assert.equal(first.strokes.length, 1);
  assert.deepEqual(first.strokes[0].points[0], [50, -50]);
  assert.equal(first.parcel_area, 600);
  assert.ok(!create.disabled);

  await select(algorithm, "contour");
  assert.equal(host.querySelector("canvas"), canvas);
  assert.equal(guide("parcel_area"), null);
  assert.ok(create.disabled);
  await showPlan();
  assert.deepEqual(requests.at(-1), {
    pattern: "contour", path_variants: first.path_variants, spacing: first.spacing, angle_deg: null, strokes: first.strokes,
  });
  await act(async () => roadTypes()[1].click());
  assert.ok(create.disabled);
  await paint(300, 300);
  await showPlan();
  assert.deepEqual(requests.at(-1).path_variants, [212]);
  assert.equal(requests.at(-1).strokes.length, 2);

  await select(algorithm, "block");
  assert.ok(create.disabled);
  assert.equal(roadTypes().length, 1);
  const blockPattern = guide("block_pattern");
  assert.deepEqual([...blockPattern.options].map(option => option.value), ["block_grid", "block_strips", "block_perimeter"]);
  await select(blockPattern, "block_strips");
  await showPlan();
  const block = requests.at(-1);
  assert.equal(block.pattern, "block_strips");
  assert.equal(block.parcel_size, 20);
  assert.equal(block.spacing, undefined);
  assert.equal(block.strokes.length, 2);
  await act(async () => create.click());
  assert.deepEqual(requests.at(-1), block);
  assert.ok(preview.disabled);

  await paint(80, 80);
  assert.ok(!preview.disabled);
  await act(async () => clear.click());
  assert.ok(preview.disabled);

  for (const pattern of ["neighborhood", "branching", "downtown_grid", "subdivision"]) {
    await paint(80, 80);
    await select(algorithm, pattern);
    assert.ok(create.disabled);
    assert.ok(guide("parcel_area"));
    await showPlan();
    const request = requests.at(-1);
    assert.equal(request.pattern, pattern);
    assert.equal(request.parcel_area, 600);
    assert.ok(request.strokes.length > 0);
    assert.ok(!create.disabled);
    await act(async () => create.click());
    assert.deepEqual(requests.at(-1), request);
    assert.ok(preview.disabled);
  }
} finally {
  await act(async () => root.unmount());
  await window.happyDOM.close();
}
console.log("PASS: shared brush and preview across city development algorithms and block street styles");
