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
mockWindows("street_grid");
mockIPC(() => null, { shouldMockEvents: true });
await i18n.use(initReactI18next).init({ lng: "ja", resources: { ja: {
  translation: JSON.parse(await readFile("src/i18n/ja/scenarioTools.json", "utf8")),
} } });
const { createRoot } = await import("react-dom/client");
const output = resolve("node_modules/.cache/street-grid-area-test.mjs");
await build({ stdin: { contents: `export { StreetGridWindow } from "./src/components/path/StreetGridWindow";
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
  if (url.includes("/street-grid")) {
    requests.push(JSON.parse(init.body));
    return Response.json({ segments: [], created: 0, rejected: {}, pattern: "relaxed_grid" });
  }
  return new Promise(() => {});
};
const { StreetGridWindow, master } = await import(pathToFileURL(output).href);
master.data = { paths: { 212: { type: 1, frontage_allowed: true, lanes: 1 } } };
const host = document.createElement("div");
document.body.append(host);
const root = createRoot(host);
try {
  const apiBaseUrl = "http://fixture";
  await act(async () => root.render(createElement(StreetGridWindow, { apiBaseUrl })));
  const canvas = host.querySelector("canvas");
  const area = () => host.querySelector("output").textContent;
  const [clear, preview, create] = host.querySelectorAll("button");
  const pointer = (type, x, y) => act(async () => {
    canvas.dispatchEvent(new window.PointerEvent(type, { bubbles: true, pointerId: 1, button: 0, clientX: x, clientY: y }));
  });
  const click = async (x, y) => { await pointer("pointerdown", x, y); await pointer("pointerup", x, y); };
  const right = (x, y) => act(async () => {
    canvas.dispatchEvent(new window.MouseEvent("contextmenu", { bubbles: true, button: 2, clientX: x, clientY: y }));
  });
  const showPlan = () => act(async () => preview.click());
  await click(50, 50); await click(150, 50); await right(200, 200);
  assert.ok(preview.disabled);
  await pointer("pointermove", 150, 150);
  assert.equal(area(), "選択面積 5,000 m²");
  await click(150, 150);
  assert.ok(preview.disabled);
  await right(50, 50);
  await showPlan();
  assert.deepEqual(requests.at(-1).points, [[50,-50], [150,-50], [150,-150]]);
  assert.ok(!create.disabled);

  await click(98, 102); // 最後の頂点から最初の頂点に戻る辺への射影。
  assert.ok(create.disabled);
  await showPlan();
  assert.deepEqual(requests.at(-1).points.at(-1), [100,-100]);
  await pointer("pointerdown", 100, 100); await pointer("pointermove", 50, 150);
  assert.equal(area(), "選択面積 0.01 km²");
  assert.ok(create.disabled);
  await pointer("pointerup", 50, 150);
  await right(150, 50);
  assert.equal(area(), "選択面積 5,000 m²");
  await click(400, 400); await right(400, 400);
  await pointer("pointerdown", 400, 400); await pointer("pointermove", 430, 420); await pointer("pointerup", 430, 420);
  await showPlan();
  assert.deepEqual(requests.at(-1).points, [[50,-50], [150,-150], [50,-150]]);
  await right(80, 70); // パンした後も同じ頂点を拾い、2点になれば配置を再開する。
  assert.ok(preview.disabled);
  await click(150, 70); await right(400, 400);
  assert.ok(!preview.disabled);
  await showPlan();
  await act(async () => create.click());
  assert.equal(area(), "選択面積 0 m²");
  assert.ok(preview.disabled);
  await click(60, 60); await click(200, 60); await click(200, 160); await right(400, 400);
  await act(async () => clear.click());
  assert.equal(area(), "選択面積 0 m²");
  assert.ok(preview.disabled);
} finally {
  await act(async () => root.unmount());
  await window.happyDOM.close();
}
console.log("PASS: street grid outline placement, editing, area, panning and preview");
