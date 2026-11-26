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
    ...JSON.parse(await readFile("src/i18n/ja/common.json", "utf8")),
    ...JSON.parse(await readFile("src/i18n/ja/scenarioTools.json", "utf8")),
    ...JSON.parse(await readFile("src/i18n/ja/cityDevelopment.json", "utf8")),
  },
} } });
const { createRoot } = await import("react-dom/client");
const output = resolve("node_modules/.cache/street-grid-area-test.mjs");
await build({ stdin: { contents: `export { CityDevelopmentWindow } from "./src/components/development/CityDevelopmentWindow";
  export { CityExpansionProposalWindow } from "./src/components/development/CityExpansionProposalWindow";
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
  if (url.includes("/city-development/auto-growth")) {
    const body = JSON.parse(init.body);
    requests.push({ url, body });
    return Response.json(url.endsWith("/preview") ? {
      roads: [{ path_variant: body.arterial_variant ?? 221, points: [[0, 0], [100, 0]], is_arterial: true }],
      parcels: [[[0, 10], [20, 10], [20, 30], [0, 30]]],
      rejected: {},
      blocks_added: body.blocks,
      area_m2: 10000,
      estimated_cost: 50000,
      bounds: [0, 0, 100, 30],
    } : { path_ids: [1], parcel_ids: [10], rejected: {}, blocks_added: body.blocks });
  }
  if (url.includes("/city-development/expansion-proposal/")) {
    const body = JSON.parse(init.body);
    requests.push({ url, body });
    return Response.json(url.endsWith("/apply")
      ? { path_ids: [1], parcel_ids: [10], rejected: {}, blocks_added: 4 }
      : { suppressed: Boolean(body.suppress), cooldown_until_epoch: 8, cooldown_epochs: 5 });
  }
  if (url.includes("/city-development")) {
    const body = JSON.parse(init.body);
    requests.push({ url, body });
    return Response.json(url.endsWith("/preview") ? {
      roads: [{ path_variant: body.path_variants[0], points: [[0, 0], [100, 0]] }],
      parcels: [], rejected: {}, area_m2: 10000,
    } : { path_ids: [1], parcel_ids: [], rejected: {} });
  }
  assert.ok(!url.includes("/street-grid"));
  return new Promise(() => {});
};
const { CityDevelopmentWindow, CityExpansionProposalWindow, master } = await import(pathToFileURL(output).href);
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
  const actionButtons = () => host.querySelectorAll(".city-development__actions button");
  const pointer = (type, x, y, ctrlKey = false) => act(async () => {
    canvas.dispatchEvent(new window.PointerEvent(type, { bubbles: true, pointerId: 1, button: 0, clientX: x, clientY: y, ctrlKey }));
  });
  const paint = async (x, y) => {
    await pointer("pointerdown", x, y, true);
    await pointer("pointermove", x + 50, y + 50, true);
    await pointer("pointerup", x + 50, y + 50, true);
  };
  const guide = name => host.querySelector(`[data-guide="city_development.${name}"]`);
  const select = (input, value) => act(async () => {
    input.value = value;
    input.dispatchEvent(new window.Event("change", { bubbles: true }));
  });

  // 1枚目のタブは「自動拡張」。幹線・路地は既定で自動選択、拡張量の既定値は8街区。
  const autoTab = host.querySelector("#city-development-tab-auto_growth");
  const areaTab = host.querySelector("#city-development-tab-area_fill");
  assert.equal(autoTab.getAttribute("aria-selected"), "true");
  assert.equal(areaTab.getAttribute("aria-selected"), "false");
  assert.equal(guide("growth_style").value, "blocks");
  assert.equal(guide("arterial_variant").value, "");
  assert.equal(guide("lane_variant").value, "");
  assert.equal(guide("blocks").value, "8");

  await paint(50, 50);
  await act(async () => actionButtons()[1].click());
  const growthReq = requests.at(-1);
  assert.ok(growthReq.url.endsWith("/city-development/auto-growth/preview"));
  assert.equal(growthReq.body.style, "blocks");
  assert.equal(growthReq.body.blocks, 8);
  assert.equal(growthReq.body.arterial_variant, null);
  assert.equal(growthReq.body.lane_variant, null);
  assert.equal(growthReq.body.strokes.length, 1);
  assert.ok(!actionButtons()[2].disabled);

  // タブ2「範囲開発」へ切り替えても塗った範囲は維持される。
  await act(async () => areaTab.click());
  assert.equal(areaTab.getAttribute("aria-selected"), "true");
  const [clear, preview, create] = actionButtons();
  const showPlan = () => act(async () => preview.click());
  const algorithm = guide("pattern");
  const roadTypes = () => host.querySelectorAll('.city-development__variants input[type="checkbox"]');
  assert.equal(algorithm.value, "planned_city_1");
  assert.equal(algorithm.querySelectorAll("optgroup").length, 0);
  assert.deepEqual([...algorithm.options].map(option => option.value),
    ["grid_1", "grid_2", "sprawl_1", "sprawl_2", "sprawl_3", "planned_city_1", "split_1"]);
  assert.equal(guide("block_length"), null);
  assert.equal(roadTypes().length, 2);
  assert.deepEqual([...roadTypes()].map(input => input.checked), [true, true]);
  assert.ok(!preview.disabled);

  await showPlan();
  const first = requests.at(-1).body;
  assert.equal(first.pattern, "planned_city_1");
  assert.equal(first.strokes.length, 1);
  assert.deepEqual(first.strokes[0].points[0], [50, -50]);
  assert.equal(first.create_parcels, true);
  assert.equal(first.parcel_area, 600);
  assert.equal(first.block_length, undefined);
  assert.ok(!create.disabled);

  // 方式を変えても塗った範囲と地図は残り、確定には取り直したプレビューが要る。分割1だけ街区の長さを送る。
  await select(algorithm, "split_1");
  assert.equal(host.querySelector("canvas"), canvas);
  assert.ok(create.disabled);
  assert.ok(guide("block_length"));
  await showPlan();
  assert.deepEqual(requests.at(-1).body, { ...first, pattern: "split_1", block_length: first.spacing });

  // 「土地区画を作る」を外すと区画の面積の欄が消え、道路だけを計画する。
  await act(async () => guide("create_parcels").click());
  assert.equal(guide("parcel_area"), null);
  assert.ok(create.disabled);
  await act(async () => roadTypes()[1].click());
  await paint(300, 300);
  await showPlan();
  const roadsOnly = requests.at(-1).body;
  assert.equal(roadsOnly.create_parcels, false);
  assert.equal(roadsOnly.parcel_area, undefined);
  assert.deepEqual(roadsOnly.path_variants, [212]);
  assert.equal(roadsOnly.strokes.length, 2);
  await act(async () => create.click());
  assert.deepEqual(requests.at(-1).body, roadsOnly);
  assert.ok(preview.disabled);

  await paint(80, 80);
  assert.ok(!preview.disabled);
  await act(async () => clear.click());
  assert.ok(preview.disabled);

  await act(async () => guide("create_parcels").click());
  for (const pattern of ["grid_1", "grid_2", "sprawl_1", "sprawl_2", "sprawl_3", "planned_city_1"]) {
    await paint(80, 80);
    await select(algorithm, pattern);
    assert.ok(create.disabled);
    assert.equal(guide("block_length"), null);
    assert.ok(guide("parcel_area"));
    await showPlan();
    const request = requests.at(-1).body;
    assert.equal(request.pattern, pattern);
    assert.equal(request.block_length, undefined);
    assert.equal(request.parcel_area, 600);
    assert.ok(request.strokes.length > 0);
    assert.ok(!create.disabled);
    await act(async () => create.click());
    assert.deepEqual(requests.at(-1).body, request);
    assert.ok(preview.disabled);
  }

  // エポック終了時の拡張提案ウィンドウ: しばらく提案しない＋キャンセルで suppress=true、OKで apply を送る。
  let closed = 0;
  const proposal = {
    proposal_id: "prop-1",
    roads: [{ path_variant: 221, points: [[0, 0], [100, 0]], is_arterial: true }],
    parcels: [[[0, 10], [20, 10], [20, 30], [0, 30]]],
    blocks_added: 4,
    estimated_cost: 50000,
    bounds: [0, 0, 100, 30],
    vacant_parcels: 2,
    total_parcels: 40,
    vacant_area_m2: 800,
    cooldown_epochs: 5,
  };
  await act(async () => root.render(createElement(CityExpansionProposalWindow, {
    apiBaseUrl: "http://fixture",
    proposal,
    onClose: () => { closed += 1; },
  })));
  const suppressCheck = host.querySelector('.expansion-proposal__suppress input[type="checkbox"]');
  const [cancelBtn, okBtn] = host.querySelectorAll(".expansion-proposal__actions button");
  assert.equal(suppressCheck.checked, false);
  await act(async () => suppressCheck.click());
  assert.equal(suppressCheck.checked, true);
  await act(async () => cancelBtn.click());
  assert.ok(requests.at(-1).url.endsWith("/expansion-proposal/dismiss"));
  assert.deepEqual(requests.at(-1).body, { proposal_id: "prop-1", suppress: true });
  assert.equal(closed, 1);

  // 別IDの提案で再利用されたらチェックは初期化され、OKで apply を送る。
  await act(async () => root.render(createElement(CityExpansionProposalWindow, {
    apiBaseUrl: "http://fixture",
    proposal: { ...proposal, proposal_id: "prop-2" },
    onClose: () => { closed += 1; },
  })));
  assert.equal(host.querySelector('.expansion-proposal__suppress input[type="checkbox"]').checked, false);
  await act(async () => host.querySelectorAll(".expansion-proposal__actions button")[1].click());
  assert.ok(requests.at(-1).url.endsWith("/expansion-proposal/apply"));
  assert.deepEqual(requests.at(-1).body, { proposal_id: "prop-2" });
  assert.equal(closed, 2);
} finally {
  await act(async () => root.unmount());
  await window.happyDOM.close();
}
console.log("PASS: tabbed city development (auto growth & area fill), shared brush, and expansion proposal window");
