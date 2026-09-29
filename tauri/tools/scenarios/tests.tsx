import assert from "node:assert/strict";
import { Window } from "happy-dom";
import { act } from "react";
import { createRoot } from "react-dom/client";
import { ScenarioManagerWindow } from "../../src/components/template/ScenarioManagerWindow";
import { ScenarioMetadataWindow } from "../../src/components/template/ScenarioMetadataWindow";
import { NewGameWindow } from "../../src/components/taskbar/NewGameWindow";
import { errorCode, prepareSaveLoad, saveError } from "../../src/utils/saves";
import { ApiError } from "../../src/utils/api";
import { importedMapExtent, readHeightmapInfo } from "../../src/components/template/heightmapImport";
import { encodeFloat16Array, uint8ToBase64 } from "../../src/components/terrain/terrainData";
import { state } from "./mock-runtime";
import jaScenarios from "../../src/i18n/ja/scenarios.json";
import jaTerrainEditor from "../../src/i18n/ja/terrainEditor.json";

// DOM は run.mjs が React より先に用意する(単体で読み込んだときだけここで作る)。
const window = ((globalThis as { window?: Window }).window ?? new Window()) as Window;
Object.assign(globalThis, { window, document: window.document, HTMLElement: window.HTMLElement,
  IS_REACT_ACT_ENVIRONMENT: true });
const entry = { id: "local~mine", city_name: "City", scenario_title: { en: "My scenario" } as Record<string, string>, author: "Author",
  scenario_description: { en: "Description", ja: "説明" }, scenario_objective: { en: "Objective" }, category: "sandbox", language: "ja",
  scenario_city_name: { ja: "見本市", en: "Sample Town" }, scenario_fixed_options: ["climate"], simulation_days: 30, asset_region: 1,
  terrain_source: "flat.png", map_width: 2000, map_height: 1000, ground_cover: 4 as number | undefined, climate: 7 as number | undefined,
  saved_core_version: "1.0.0", created_core_version: "1.0.0", compatibility: { loadable: true }, origin: "local",
  read_only: false, encrypted: false, required_packs: [], pack_status: {}, official: false };
const entries = [entry,
  { ...entry, id: "workshop~123~copy", origin: "workshop", read_only: true, ground_cover: undefined, climate: undefined },
  { ...entry, id: "dlc~pack~city", origin: "dlc", read_only: true, official: true, encrypted: true,
    required_packs: ["pack"], pack_status: { pack: "not_owned" }, compatibility: { loadable: false, code: "SCENARIO_PACK_NOT_OWNED" } },
  { ...entry, id: "local~future", compatibility: { loadable: false, code: "SAVE_VERSION_NEWER" } }];
let validation: object = { balance_changed: false };
let rejectLoad = false;
let metadataWritten: any;
/** 標高ファイルの取り込み要求(クエリ・本文・Content-Type)。rejectHeightmap なら 422 で断る */
let imported: { query: URLSearchParams; body: unknown; contentType: string | null } | undefined;
let rejectHeightmap = false;
/** シナリオの地形の取得(平面図の元)を失敗させる */
let terrainUnavailable = false;
let thumbnailUploads: { url: string; contentType: string | null; body: unknown }[] = [];
// 3列×2行(20m×10m)の小さな地形。行0が南で、南西だけ海。
const terrainDocument = { settings: { map_width: 20, map_height: 10, near_spacing: 10, far_spacing: 200, cell_size: 500, grid_rows: 2, grid_cols: 3 },
  near_base64: uint8ToBase64(encodeFloat16Array(new Float32Array([-5, 3, 8, 2, 12, 40]))), ground_cover: 2 };
globalThis.fetch = async (input, init) => {
  const url = String(input);
  const headers = new Headers(init?.headers);
  if (url.includes("/validation?template=true")) return new Response(JSON.stringify(rejectLoad
    ? { code: "SAVE_CORRUPT", message: "corrupt" } : { compatibility: validation }), { status: rejectLoad ? 422 : 200 });
  if (url.includes("/thumbnail") && init?.method === "PUT") {
    thumbnailUploads.push({ url, contentType: headers.get("Content-Type"), body: init.body });
    return new Response(JSON.stringify({ success: true }));
  }
  if (url.includes("/thumbnail")) return new Response("", { status: 404 });
  if (url.includes("/api/v1/scenarios/from-heightmap?")) {
    imported = { query: new URL(url).searchParams, body: init?.body, contentType: headers.get("Content-Type") };
    if (rejectHeightmap) return new Response(JSON.stringify({ code: "HEIGHTMAP_INVALID", message: "陸地がありません。" }), { status: 422 });
    entries.push({ ...entry, id: "local~new", scenario_title: { en: imported.query.get("title")! } });
    return new Response(JSON.stringify({ id: "local~new", map_width: 2000, map_height: 1000 }));
  }
  if (url.endsWith("/api/v1/scenarios/local~new/terrain")) {
    return terrainUnavailable ? new Response("{}", { status: 500 }) : new Response(JSON.stringify(terrainDocument));
  }
  if (init?.method === "PATCH") { metadataWritten = JSON.parse(String(init.body)); return new Response("{}"); }
  if (url.includes("/saves/")) return new Response(JSON.stringify({ save: entry }));
  return new Response(JSON.stringify({ scenarios: Object.fromEntries(entries.map(value => [value.id, value])), upload_available: false }));
};
const settle = () => act(async () => { await new Promise(resolve => setTimeout(resolve, 0)); });

const container = window.document.createElement("div");
window.document.body.append(container);
const buttons = (text: string) => [...container.querySelectorAll("button")].filter(value => value.textContent === text);
const button = (text: string) => buttons(text)[0]!;
const card = (id: string) => [...container.querySelectorAll<HTMLElement>("article")].find(value => value.dataset.scenarioId === id)!;
const cardButton = (id: string, text: string) => [...card(id).querySelectorAll("button")].find(value => value.textContent === text)!;
const lastCall = (command: string) => state.calls.filter(call => call.command === command).at(-1)?.args;
const setText = (element: HTMLInputElement | HTMLSelectElement, value: string) => {
  const prototype = element.tagName === "SELECT" ? window.HTMLSelectElement.prototype : window.HTMLInputElement.prototype;
  Object.getOwnPropertyDescriptor(prototype, "value")!.set!.call(element, value);
  element.dispatchEvent(new window.Event(element.tagName === "SELECT" ? "change" : "input", { bubbles: true }));
};

// シナリオ管理: 全保存元を1つの一覧に並べ、保存元ごとに操作を出し分ける。
let root = createRoot(container);
await act(async () => { root.render(<ScenarioManagerWindow apiBaseUrl="http://test" />); });
await settle();
assert.deepEqual([...container.querySelectorAll("[role=tab]")].map(tab => tab.textContent), ["scenarios.tabScenarios", "scenarios.tabTerrains"]);
assert.equal(container.querySelectorAll("article").length, 4);
assert.ok(button("scenarios.upload").disabled, "unconfigured upload must be disabled");
assert.equal(buttons("scenarios.editTerrain").length, 2, "only own scenarios can edit terrain");
assert.equal(buttons("scenarios.editCopy").length, 2, "workshop and DLC scenarios are copied before editing");
assert.equal(buttons("scenarios.metadata").length, 2);
assert.ok(!cardButton("workshop~123~copy", "scenarios.editCopy").disabled);
assert.ok(cardButton("dlc~pack~city", "scenarios.editCopy").disabled, "unowned DLC cannot be opened");
assert.ok(card("workshop~123~copy").textContent?.includes("scenarios.origin.workshop"));
assert.ok(card("dlc~pack~city").textContent?.includes("scenarios.official"));
assert.ok(card("dlc~pack~city").querySelector(".required-pack--not_owned"), "pack ownership is shown");
assert.ok(card("dlc~pack~city").textContent?.includes("saveCompatibility.SCENARIO_PACK_NOT_OWNED"));
assert.ok(card("local~future").textContent?.includes("saveCompatibility.SAVE_VERSION_NEWER"));
assert.ok(!container.textContent?.includes("scenarios.play") && !container.textContent?.includes("scenarios.export"));

await act(async () => { cardButton("local~mine", "scenarios.metadata").click(); });
assert.equal(lastCall("open_registered_window").params.extra.scenarioId, entry.id);

state.results.prepare_scenario_terrain_editor = "editor-1";
await act(async () => { cardButton("local~mine", "scenarios.editTerrain").click(); });
await settle();
assert.deepEqual(lastCall("prepare_scenario_terrain_editor"), { scenarioId: "local~mine" });
assert.deepEqual(lastCall("open_registered_window"), { windowType: "terrain_editor", entityId: "editor-1" });

// 地形エディタを開けないとき: コアの理由コードは翻訳し、コードの無い Rust 側の失敗(日本語固定)は汎用の文言にする。
const logged: unknown[][] = [];
const consoleError = console.error;
console.error = (...args: unknown[]) => { logged.push(args); };
const alertText = () => container.querySelector(".scenario-error")?.textContent;
state.errors.prepare_scenario_terrain_editor = "SCENARIO_READ_ONLY: コミュニティ・DLCのシナリオは変更できません。";
await act(async () => { cardButton("local~mine", "scenarios.editTerrain").click(); });
await settle();
assert.equal(alertText(), "saveCompatibility.SCENARIO_READ_ONLY");
state.errors.prepare_scenario_terrain_editor = "シナリオの地形を取得できません: connection refused";
await act(async () => { cardButton("local~mine", "scenarios.editTerrain").click(); });
await settle();
assert.equal(alertText(), "scenarios.terrainOpenFailed");
assert.ok(logged.some(args => args.includes(state.errors.prepare_scenario_terrain_editor)), "the raw error is kept in the log");
console.error = consoleError;
delete state.errors.prepare_scenario_terrain_editor;

await act(async () => { cardButton("local~mine", "scenarios.editCity").click(); });
await settle();
assert.deepEqual(lastCall("create_session"), { templateId: "local~mine", templateMode: true, acceptBalanceChange: true });
await act(async () => { cardButton("workshop~123~copy", "scenarios.editCopy").click(); });
await settle();
assert.deepEqual(lastCall("create_session"), { templateId: "workshop~123~copy", templateMode: true, acceptBalanceChange: true });

// 地形を取り込む: 画素数・形式は先頭のバイトから読み、マップの大きさはコアと同じく1km単位に切り上げる。
assert.deepEqual([importedMapExtent(1001, 1), importedMapExtent(1002, 1), importedMapExtent(2, 0.5), importedMapExtent(3001, 10), importedMapExtent(11, 100)],
  [1000, 2000, 1000, 30000, 1000]);
const png = (columns: number, rows: number, name = "island.png") => {
  const bytes = new Uint8Array(33);
  bytes.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52]);
  new DataView(bytes.buffer).setUint32(16, columns);
  new DataView(bytes.buffer).setUint32(20, rows);
  return new File([bytes], name, { type: "image/png" });
};
const npy = (header: string, name = "dem.npy", version = 1) => {
  const text = new TextEncoder().encode(header);
  const prefix = version === 1 ? [0x93, 0x4e, 0x55, 0x4d, 0x50, 0x59, 1, 0, text.length & 0xff, text.length >> 8]
    : [0x93, 0x4e, 0x55, 0x4d, 0x50, 0x59, version, 0, text.length & 0xff, text.length >> 8, 0, 0];
  return new File([new Uint8Array(prefix), text, new Uint8Array(8)], name);
};
assert.deepEqual(await readHeightmapInfo(png(201, 101)), { format: "png", columns: 201, rows: 101 });
assert.deepEqual(await readHeightmapInfo(npy("{'descr': '<f4', 'fortran_order': False, 'shape': (101, 201), }")), { format: "npy", columns: 201, rows: 101 });
assert.deepEqual(await readHeightmapInfo(npy("{'descr': '<f4', 'fortran_order': False, 'shape': (7, 9), }", "v2.npy", 2)), { format: "npy", columns: 9, rows: 7 });
assert.deepEqual(await readHeightmapInfo(npy("{'descr': '<f4', 'fortran_order': False, 'shape': (4, 5, 3), }")), { format: "npy", columns: null, rows: null }, "only 2-D arrays have a size");
assert.deepEqual(await readHeightmapInfo(new File([new Uint8Array([1, 2, 3])], "notes.PNG")), { format: "png", columns: null, rows: null }, "unknown bytes fall back to the extension");

// 平面図は canvas で PNG にする(happy-dom の canvas は描けないので、描いた大きさと形式だけを見る)。
const drawn: { width: number; height: number }[] = [];
Object.assign(globalThis, { ImageData: window.ImageData });
window.HTMLCanvasElement.prototype.getContext = function () {
  return { putImageData: (image: ImageData) => { drawn.push({ width: image.width, height: image.height }); } };
} as any;
window.HTMLCanvasElement.prototype.toBlob = function (callback: (blob: Blob | null) => void, type?: string) {
  callback(new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], { type }));
} as any;
const importForm = () => container.querySelector<HTMLFormElement>(".scenario-import")!;
const fileInput = () => importForm().querySelector<HTMLInputElement>("input[type=file]")!;
const fieldInput = (label: string) => [...importForm().querySelectorAll("label")].find(value => value.textContent?.startsWith(label))!.querySelector("input")!;
const createButton = () => button("scenarios.createFromTerrain");
const choose = async (file: File) => {
  await act(async () => {
    Object.defineProperty(fileInput(), "files", { value: [file], configurable: true });
    fileInput().dispatchEvent(new window.Event("change", { bubbles: true }));
  });
  await settle();
};

await act(async () => { button("scenarios.tabTerrains").click(); });
await settle();
assert.equal(fileInput().accept, ".png,.npy");
assert.ok(importForm().textContent?.includes("scenarios.noFileChosen"));
assert.ok(createButton().disabled, "nothing to create before a file is chosen");
assert.ok(!importForm().querySelector(".scenario-import-size"), "the map size is unknown before a file is chosen");
await choose(png(201, 101));
assert.ok(importForm().textContent?.includes("island.png"));
assert.equal(fieldInput("scenarios.importTitle").value, "island", "the title defaults to the file name without its extension");
assert.equal(importForm().querySelector(".scenario-import-size")?.textContent, "2 × 1 km", "the map size is previewed from the PNG header");
assert.ok(!fieldInput("scenarios.importMinHeight").disabled && !fieldInput("scenarios.importMaxHeight").disabled);
assert.ok(!createButton().disabled);
await act(async () => { setText(fieldInput("scenarios.importSpacing"), "200"); });
assert.ok(importForm().textContent?.includes("scenarios.importTooLarge"), "maps over 30 km per side are rejected before sending");
assert.ok(createButton().disabled);
await act(async () => { setText(fieldInput("scenarios.importSpacing"), "10"); });
await act(async () => { setText(fieldInput("scenarios.importMinHeight"), "100"); });
await act(async () => { setText(fieldInput("scenarios.importMaxHeight"), "50"); });
assert.ok(importForm().textContent?.includes("scenarios.importInvalid"), "the highest elevation must be above the lowest");
assert.ok(createButton().disabled);
await act(async () => { setText(fieldInput("scenarios.importMaxHeight"), "900"); });
await act(async () => { setText(fieldInput("scenarios.importSeaLevel"), ""); });
assert.ok(createButton().disabled, "an empty value is not treated as 0");
await act(async () => { setText(fieldInput("scenarios.importSeaLevel"), "5"); });
await act(async () => { setText(fieldInput("scenarios.importTitle"), "Island Town"); });
assert.ok(!createButton().disabled && !importForm().textContent?.includes("scenarios.importInvalid"));

// 失敗の理由コードは翻訳して出し、タブはそのまま。
rejectHeightmap = true;
await act(async () => { createButton().click(); });
await settle();
assert.equal(container.querySelector(".scenario-error[role=alert]")?.textContent, "saveCompatibility.HEIGHTMAP_INVALID");
assert.equal(container.querySelector("[role=tab][aria-selected=true]")?.textContent, "scenarios.tabTerrains");
rejectHeightmap = false;

// 生バイトを送り、作成後は平面図をシナリオ画像にしてからシナリオタブで新しいカードを選択する。
await act(async () => { createButton().click(); });
await settle();
assert.equal(imported?.contentType, "application/octet-stream");
assert.ok(imported?.body instanceof File && imported.body.name === "island.png", "the file itself is the request body");
assert.deepEqual(Object.fromEntries(imported!.query), { spacing: "10", sea_level: "5", min_height: "100", max_height: "900",
  title: "Island Town", language: "ja", source_name: "island.png" });
assert.deepEqual(drawn, [{ width: 512, height: 256 }], "the thumbnail keeps the map's aspect ratio");
assert.equal(thumbnailUploads.length, 1);
assert.ok(thumbnailUploads[0].url.endsWith("/api/v1/saves/local~new/thumbnail?template=true"));
assert.equal(thumbnailUploads[0].contentType, "image/png");
assert.equal(container.querySelector("[role=tab][aria-selected=true]")?.textContent, "scenarios.tabScenarios");
assert.equal(container.querySelector(".scenario-status")?.textContent, "scenarios.created");
assert.equal(card("local~new").getAttribute("aria-current"), "true");
assert.ok(card("local~new").classList.contains("scenario-card--selected"));
entries.pop();

// .npy は値がそのまま標高なので最低・最高標高を送らない。平面図を作れなくても作成は成功する。
await act(async () => { button("scenarios.tabTerrains").click(); });
await settle();
assert.ok(importForm().textContent?.includes("scenarios.noFileChosen"), "the file is cleared after a successful import");
assert.equal(fieldInput("scenarios.importSpacing").value, "10", "conversion settings are kept for the next import");
await choose(npy("{'descr': '<f4', 'fortran_order': False, 'shape': (101, 201), }"));
assert.ok(fieldInput("scenarios.importMinHeight").disabled && fieldInput("scenarios.importMaxHeight").disabled);
assert.ok(importForm().textContent?.includes("scenarios.importNpyHeights"));
assert.equal(importForm().querySelector(".scenario-import-size")?.textContent, "2 × 1 km", "the map size is previewed from the .npy header");
assert.equal(fieldInput("scenarios.importTitle").value, "dem");
terrainUnavailable = true;
thumbnailUploads = [];
const thumbnailErrors: unknown[][] = [];
console.error = (...args: unknown[]) => { thumbnailErrors.push(args); };
await act(async () => { createButton().click(); });
await settle();
console.error = consoleError;
assert.ok(!imported!.query.has("min_height") && !imported!.query.has("max_height"));
assert.equal(imported!.query.get("source_name"), "dem.npy");
assert.equal(thumbnailUploads.length, 0);
assert.ok(thumbnailErrors.length > 0, "a thumbnail failure is logged");
assert.equal(container.querySelector("[role=tab][aria-selected=true]")?.textContent, "scenarios.tabScenarios", "a thumbnail failure does not fail the import");
assert.equal(card("local~new").getAttribute("aria-current"), "true");
terrainUnavailable = false;
await act(async () => { root.unmount(); });
entries.pop();

// DLC 由来の暗号化された自作シナリオは、地形エディタを開けない(作業コピーが平文で残るため)。都市は編集できる。
entries.push({ ...entry, id: "local~dlc-copy", encrypted: true });
root = createRoot(container);
await act(async () => { root.render(<ScenarioManagerWindow apiBaseUrl="http://test" />); });
await settle();
assert.ok(cardButton("local~dlc-copy", "scenarios.editTerrain").disabled, "encrypted scenarios cannot open the terrain editor");
assert.equal(cardButton("local~dlc-copy", "scenarios.editTerrain").title, "scenarios.terrainEncryptedHint");
assert.ok(!cardButton("local~dlc-copy", "scenarios.editCity").disabled);
assert.ok(!cardButton("local~mine", "scenarios.editTerrain").disabled);
assert.equal(cardButton("local~mine", "scenarios.editTerrain").title, "");
await act(async () => { root.unmount(); });
entries.pop();

// 紹介情報: 紹介文と初期の都市名を言語のタブで書き(英語が先頭で必須)、開始設定の値と固定を決める。
const submit = () => act(async () => { container.querySelector("form")!.dispatchEvent(new window.Event("submit", { bubbles: true, cancelable: true })); });
root = createRoot(container);
let closed = false;
await act(async () => { root.render(<ScenarioMetadataWindow apiBaseUrl="http://test" scenarioId={entry.id} onClose={() => { closed = true; }} />); });
await settle();
const languageTabs = [...container.querySelectorAll<HTMLButtonElement>(".localized-text [role=tab]")];
assert.equal(languageTabs.length, 7);
assert.ok(languageTabs[0].textContent?.startsWith("settings.language.en"), "English comes first");
assert.ok(languageTabs[0].textContent?.includes("localizedText.required"));
assert.equal(container.querySelector(".localized-text [aria-selected=true]")?.textContent, "settings.language.ja", "the display language opens first");
assert.equal(container.querySelectorAll("textarea").length, 2);
assert.equal((container.querySelector("textarea") as HTMLTextAreaElement).value, "説明");
assert.equal(container.querySelectorAll(".scenario-start-row").length, 4);
const fixedBox = (option: string) => container.querySelector<HTMLInputElement>(`.scenario-start-row[data-option=${option}] input[type=checkbox]`)!;
assert.ok(fixedBox("climate").checked && !fixedBox("simulation_days").checked);
await act(async () => { fixedBox("simulation_days").click(); });
await act(async () => { setText(container.querySelector<HTMLSelectElement>(".scenario-start-row[data-option=simulation_days] select")!, "7"); });
await submit();
assert.deepEqual(metadataWritten.scenario_title, { en: "My scenario" });
assert.deepEqual(metadataWritten.scenario_description, { ja: "説明", en: "Description" });
assert.deepEqual(metadataWritten.scenario_city_name, { ja: "見本市", en: "Sample Town" });
assert.deepEqual(metadataWritten.scenario_fixed_options, ["simulation_days", "climate"]);
assert.deepEqual([metadataWritten.simulation_days, metadataWritten.climate, metadataWritten.ground_cover, metadataWritten.asset_region], [7, 7, 4, 1]);
assert.equal(metadataWritten.author, entry.author);
assert.ok(!("language" in metadataWritten), "the city language is not editable here");
assert.ok(closed);
await act(async () => { root.unmount(); });

// 英語のタイトルを消すと保存しない。
metadataWritten = undefined;
root = createRoot(container);
await act(async () => { root.render(<ScenarioMetadataWindow apiBaseUrl="http://test" scenarioId={entry.id} onClose={() => {}} />); });
await settle();
await act(async () => { container.querySelector<HTMLButtonElement>(".localized-text [role=tab]")!.click(); });
await act(async () => { setText(container.querySelector<HTMLInputElement>(".localized-text input")!, ""); });
await submit();
assert.equal(metadataWritten, undefined, "a title without English is not sent");
assert.equal(container.querySelector(".scenario-error")?.textContent, "scenarios.englishRequired");
await act(async () => { root.unmount(); });

// ニューゲーム: 「シナリオを探す」は準備中。地形から直接始める入口は無い。
root = createRoot(container);
await act(async () => { root.render(<NewGameWindow apiBaseUrl="http://test" onClose={() => {}} />); });
await settle();
const newGameTabs = [...container.querySelectorAll<HTMLButtonElement>("[role=tab]")];
assert.deepEqual(newGameTabs.map(tab => tab.textContent), ["newGame.tabSelect", "newGame.tabBrowsenewGame.comingSoon"]);
assert.ok(newGameTabs[1].disabled);
assert.ok(!container.textContent?.includes("newGame.fromTerrain"));
assert.equal(container.querySelectorAll(".new-game-tile--tutorial").length > 0, true);
const tile = (title: string) => [...container.querySelectorAll<HTMLButtonElement>(".new-game-tile")].find(value => value.title === title)!;
assert.equal(container.querySelectorAll(".new-game-tile").length - container.querySelectorAll(".new-game-tile--tutorial").length, 4);
await act(async () => { tile("My scenario").click(); });
assert.ok(!button("newGame.startScenarioButton").disabled);
// 事実欄(大きさ・地表・気候)はすべて一覧の要素(都市が持つ値)から引く。
const facts = () => container.querySelector(".new-game-detail .terrain-facts");
assert.equal(facts()?.querySelector("title")?.textContent, "groundCover.4");
assert.ok(facts()?.textContent?.includes("climate.7"));
assert.ok(facts()?.textContent?.includes("2 × 1 km"), "the map size comes from the entry");
assert.ok(!container.querySelector(".new-game-detail")?.textContent?.includes("flat.png"), "the terrain source file name is not shown");
const workshopTile = [...container.querySelectorAll<HTMLButtonElement>(".new-game-tile")]
  .find(value => value.textContent?.includes("scenarios.origin.workshop"))!;
await act(async () => { workshopTile.click(); });
assert.equal(facts()?.querySelector("title")?.textContent, "groundCover.1", "entries without values use the game's default ground cover");
assert.ok(facts()?.textContent?.includes("climate.1"));
const unloadable = [...container.querySelectorAll<HTMLButtonElement>(".new-game-tile")]
  .filter(value => value.textContent?.includes("scenarios.origin.dlc"));
assert.equal(unloadable.length, 1);
await act(async () => { unloadable[0].click(); });
assert.ok(button("newGame.startScenarioButton").disabled, "unloadable scenarios cannot be started");
assert.ok(container.querySelector(".new-game-detail .required-pack--not_owned"));
await act(async () => { tile("My scenario").click(); });
state.results.load_app_settings = { language: "en" };
await act(async () => { button("newGame.startScenarioButton").click(); });
await settle();
assert.ok(container.textContent?.includes("newGame.closeWindowsNote"));
// 都市の言語の既定はアプリ設定の言語。シナリオと違えば名前の付け直しを知らせ、都市名は初期の都市名をその言語で引く。
const languageSelect = container.querySelector<HTMLSelectElement>(".new-game-confirm-select")!;
const cityNameInput = container.querySelector<HTMLInputElement>(".new-game-confirm-input")!;
assert.equal(languageSelect.value, "en");
assert.ok(container.querySelector(".new-game-confirm-relocalize"), "starting in another language renames the city");
assert.equal(cityNameInput.value, "Sample Town");
await act(async () => { setText(languageSelect, "ja"); });
assert.equal(cityNameInput.value, "見本市");
assert.ok(!container.querySelector(".new-game-confirm-relocalize"));
await act(async () => { setText(cityNameInput, "Mine"); });
await act(async () => { setText(languageSelect, "fr"); });
assert.equal(cityNameInput.value, "Mine", "a typed city name is kept");
// 作者が固定した気候は表示だけ。固定していない項目は選べる。
const optionRow = (option: string) => container.querySelector(`.new-game-confirm-field[data-option=${option}]`)!;
assert.ok(optionRow("climate").querySelector(".new-game-confirm-fixed") && !optionRow("climate").querySelector("select"));
await act(async () => { setText(optionRow("ground_cover").querySelector("select")!, "2"); });
await act(async () => { button("newGame.startButton").click(); });
await settle();
const session = lastCall("create_session");
assert.equal(session.templateId, "local~mine");
assert.equal(session.templateMode, undefined, "new games never start in template mode");
assert.deepEqual([session.language, session.cityName, session.climate, session.groundCover, session.assetRegion, session.simulationDays],
  ["fr", "Mine", null, 2, 1, 30]);
delete state.results.load_app_settings;

// チュートリアルは同梱の教材の都市から始まるので、地形の情報を出さずにIDだけを送る。
await act(async () => { root.unmount(); });
root = createRoot(container);
await act(async () => { root.render(<NewGameWindow apiBaseUrl="http://test" onClose={() => {}} />); });
await settle();
await act(async () => { container.querySelector<HTMLButtonElement>(".new-game-tile--tutorial")!.click(); });
assert.equal(facts(), null, "tutorials have no map facts");
await act(async () => { button("newGame.startScenarioButton").click(); });
await settle();
await act(async () => { button("newGame.startButton").click(); });
await settle();
const tutorialSession = lastCall("create_session");
assert.deepEqual([tutorialSession.isTutorial, tutorialSession.tutorialId, tutorialSession.templateId], [true, "basic", null]);
assert.ok(!("terrainMapId" in tutorialSession), "no terrain is sent for tutorials");
await act(async () => { root.unmount(); });

validation = { balance_changed: true, balance_core_version: "1.0.0", core_version: "1.1.0" };
state.confirmed = false;
assert.equal(await prepareSaveLoad("http://test", entry.id, true, ((key: string) => key) as any), false);
state.confirmed = true;
assert.equal(await prepareSaveLoad("http://test", entry.id, true, ((key: string) => key) as any), true);
rejectLoad = true;
await assert.rejects(prepareSaveLoad("http://test", entry.id, true, ((key: string) => key) as any), ApiError);

// 理由コードの取り出し: ApiError の code、Rust の「CODE: 本文」、版照合の「"CODE": 本文」。コードの無い本文はそのまま出す。
assert.equal(errorCode(new ApiError("NOT_FOUND", "none", 404)), "NOT_FOUND");
assert.equal(errorCode("TERRAIN_LOCKED: 建設物があります"), "TERRAIN_LOCKED");
assert.equal(errorCode("\"SAVE_CORRUPT\": \"broken\""), "SAVE_CORRUPT");
assert.equal(errorCode("IPC_CONTRACT_MISMATCH"), "IPC_CONTRACT_MISMATCH");
assert.equal(errorCode("HTTP 500 Internal Server Error"), null);
assert.equal(errorCode(new Error("シナリオへ保存できません: timeout")), null);
const echo = ((key: string, options?: { defaultValue?: string }) => `${key}|${options?.defaultValue ?? ""}`) as any;
assert.equal(saveError("SCENARIO_TOO_LARGE: 上限超過", echo), "saveCompatibility.SCENARIO_TOO_LARGE|SCENARIO_TOO_LARGE: 上限超過");
assert.equal(saveError("HTTP 502", echo), "HTTP 502");

// コアが返す理由コードの翻訳(日本語が原本。他の言語との揃いは check_i18n_parity.py が見る)。
for (const code of ["SCENARIO_TOO_LARGE", "SCENARIO_READ_ONLY", "SCENARIO_ENCRYPTED", "NOT_FOUND", "SESSION_ACTIVE", "API_TOKEN_REQUIRED", "HOST_NOT_ALLOWED", "HEIGHTMAP_INVALID"]) {
  assert.ok(code in jaScenarios.saveCompatibility, `saveCompatibility.${code} is translated`);
}
for (const code of ["TERRAIN_LOCKED", "TERRAIN_INVALID", "TERRAIN_SIZE_MISMATCH", "RESOURCE_VALIDATION_ERROR", "PROP_VALIDATION_ERROR"]) {
  assert.ok(code in jaTerrainEditor.terrainEditor.saveErrors, `terrainEditor.saveErrors.${code} is translated`);
}
console.log("PASS: scenario manager tabs and origin actions, encrypted terrain lock, terrain editor open errors, heightmap import (size preview, validation, request, thumbnail), new game tabs and terrain facts, tutorial start, new game language and start options, localized metadata and start settings, load confirmation and error codes");
