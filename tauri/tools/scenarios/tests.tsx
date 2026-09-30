import assert from "node:assert/strict";
import { Window } from "happy-dom";
import { act } from "react";
import { createRoot } from "react-dom/client";
import { ScenarioManagerWindow } from "../../src/components/template/ScenarioManagerWindow";
import { ScenarioMetadataWindow } from "../../src/components/template/ScenarioMetadataWindow";
import { NewGameWindow } from "../../src/components/taskbar/NewGameWindow";
import { SaveLoadWindow } from "../../src/components/taskbar/SaveLoadWindow";
import { SaveListWindow } from "../../src/components/taskbar/SaveListWindow";
import { errorCode, prepareSaveLoad, saveError } from "../../src/utils/saves";
import { ApiError } from "../../src/utils/api";
import { importedBackgroundExtent, importedMapExtent, readHeightmapInfo } from "../../src/components/template/heightmapImport";
import { BACKGROUND_RGB, renderImportPreview, type PreviewLayer } from "../../src/components/template/heightmapPreview";
import { encodeFloat16Array, uint8ToBase64 } from "../../src/components/terrain/terrainData";
import { emit, state } from "./mock-runtime";
import jaScenarios from "../../src/i18n/ja/scenarios.json";
import jaTerrainEditor from "../../src/i18n/ja/terrainEditor.json";

// DOM は run.mjs が React より先に用意する(単体で読み込んだときだけここで作る)。
const window = ((globalThis as { window?: Window }).window ?? new Window()) as Window;
Object.assign(globalThis, { window, document: window.document, HTMLElement: window.HTMLElement,
  IS_REACT_ACT_ENVIRONMENT: true });
const entry = { id: "local~mine", city_name: "City", scenario_title: { en: "My scenario" } as Record<string, string>, author: "Author",
  scenario_description: { en: "Description", ja: "説明" }, scenario_objective: { en: "Objective" }, language: "ja",
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
/** プレビューの要求(クエリ・本文の大きさ)。応答は要求が背景かどうかで決まる3×4の標高(詳細は元が 101×201、背景は 61×101 画素) */
const previewRequests: { query: URLSearchParams; size: number }[] = [];
const previewElevation = uint8ToBase64(encodeFloat16Array(new Float32Array([-5, 3, 8, 2, 12, 40, 6, 1, 9, 7, 4, 3])));
/** 削除要求のURL。rejectDelete なら自作でないシナリオと同じ 422 で断る。listFetches は一覧の取得回数 */
const deleteRequests: string[] = [];
let rejectDelete = false;
let listFetches = 0;
let thumbnailUploads: { url: string; contentType: string | null; body: unknown }[] = [];
/** システムメニュー・セーブ一覧・シナリオとして保存が送った変更系の要求と、応答に使うセッション・セーブの一覧 */
const saveRequests: { method: string; url: string; body: unknown }[] = [];
let currentSaveId: string | null = null;
let savedGames: Record<string, object> = {};
const saveEntry = { id: "", city_name: "港町", save_name: "", population: 1234, current_date: 400, epoch: 3, simulation_days: 30,
  saved_at: "20260930_120000", is_scenario: false, language: "ja", saved_core_version: "1.0.0", created_core_version: "1.0.0",
  compatibility: { loadable: true }, origin: "save", required_packs: [], pack_status: {} };
const playingCity = { name: "港町", scenario_title: { en: "Port" }, scenario_description: {}, scenario_objective: {},
  scenario_city_name: { en: "Port Town" }, scenario_fixed_options: ["climate"], simulation_days: 30, climate: 2, ground_cover: 1, asset_region: 1 };
/** 変更系の要求(保存・シナリオ作成・画像の保留)だけを拾う。一覧・詳細の取得はほかの分岐が返す */
function saveMenuResponse(url: string, init?: RequestInit): Response | null {
  const method = init?.method ?? "GET";
  const saveId = /\/api\/v1\/saves\/([^/?]+)$/.exec(url)?.[1];
  if (url.endsWith("/api/v1/city/")) return new Response(JSON.stringify({ city: playingCity }));
  if (url.endsWith("/api/v1/sessions") && method === "GET") {
    return new Response(JSON.stringify({ current_save_id: currentSaveId, debug_execution: false, language: "ja" }));
  }
  if (url.endsWith("/api/v1/saves") && method === "GET") return new Response(JSON.stringify({ success: true, saves: savedGames }));
  if (url.includes("/validation?template=false")) return new Response(JSON.stringify({ compatibility: { balance_changed: false } }));
  if (saveId && method === "GET") {
    const found = savedGames[decodeURIComponent(saveId)];
    return found ? new Response(JSON.stringify({ save: found })) : new Response(JSON.stringify({ success: false }), { status: 404 });
  }
  if (url.endsWith("/api/v1/templates") || url.endsWith("/api/v1/sessions/thumbnail") || url.endsWith("/api/v1/saves") || saveId) {
    saveRequests.push({ method, url, body: init?.body });
    if (saveId && method === "DELETE") {
      delete savedGames[decodeURIComponent(saveId)];
      return new Response(null, { status: 204 });
    }
    return new Response(JSON.stringify({ success: true, id: "local~created" }), { status: 201 });
  }
  return null;
}
// 3列×2行(20m×10m)の小さな地形。行0が南で、南西だけ海。
const terrainDocument = { settings: { map_width: 20, map_height: 10, near_spacing: 10, far_spacing: 200, cell_size: 500, grid_rows: 2, grid_cols: 3 },
  near_base64: uint8ToBase64(encodeFloat16Array(new Float32Array([-5, 3, 8, 2, 12, 40]))), ground_cover: 2 };
globalThis.fetch = async (input, init) => {
  const url = String(input);
  const headers = new Headers(init?.headers);
  const saveMenu = saveMenuResponse(url, init);
  if (saveMenu) return saveMenu;
  if (url.includes("/validation?template=true")) return new Response(JSON.stringify(rejectLoad
    ? { code: "SAVE_CORRUPT", message: "corrupt" } : { compatibility: validation }), { status: rejectLoad ? 422 : 200 });
  if (url.includes("/thumbnail") && init?.method === "PUT") {
    thumbnailUploads.push({ url, contentType: headers.get("Content-Type"), body: init.body });
    return new Response(JSON.stringify({ success: true }));
  }
  if (url.includes("/thumbnail")) return new Response("", { status: 404 });
  if (url.includes("/api/v1/scenarios/heightmap-preview?")) {
    const query = new URL(url).searchParams;
    previewRequests.push({ query, size: (init?.body as Blob).size });
    return new Response(JSON.stringify({ source_rows: query.has("background") ? 61 : 101, source_cols: query.has("background") ? 101 : 201,
      rows: 3, cols: 4, min_height: -12.34, max_height: 513, heights_base64: previewElevation }));
  }
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
  if (init?.method === "DELETE") {
    deleteRequests.push(url);
    if (rejectDelete) return new Response(JSON.stringify({ code: "SCENARIO_READ_ONLY", message: "read only" }), { status: 422 });
    entries.splice(entries.findIndex(value => url.includes(`/saves/${value.id}?`)), 1);
    return new Response(null, { status: 204 });
  }
  if (url.includes("/saves/")) return new Response(JSON.stringify({ save: entry }));
  listFetches++;
  return new Response(JSON.stringify({ scenarios: Object.fromEntries(entries.map(value => [value.id, value])), upload_available: false }));
};
const settle = () => act(async () => { await new Promise(resolve => setTimeout(resolve, 0)); });

const container = window.document.createElement("div");
window.document.body.append(container);
/** 文字のボタンは表示文字、アイコンだけのボタンは aria-label で探す */
const named = (text: string) => (value: Element) => value.textContent === text || value.getAttribute("aria-label") === text;
const buttons = (text: string) => [...container.querySelectorAll("button")].filter(named(text));
const button = (text: string) => buttons(text)[0]!;
const card = (id: string) => [...container.querySelectorAll<HTMLElement>("article")].find(value => value.dataset.scenarioId === id)!;
const cardButton = (id: string, text: string) => [...card(id).querySelectorAll("button")].find(named(text))!;
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
// タブの下に、シナリオの作り方の説明を1つだけ出す。更新ボタンと「アップロード先が未設定」の常時表示は無い(未設定の理由は upload の title だけ)。
const hints = [...container.querySelectorAll(".scenario-hint")];
assert.deepEqual(hints.map(value => value.textContent), ["scenarios.createHint"]);
assert.ok(container.querySelector("[role=tablist]")!.compareDocumentPosition(hints[0]) & window.Node.DOCUMENT_POSITION_FOLLOWING, "the hint is below the tabs");
assert.ok(hints[0].compareDocumentPosition(container.querySelector(".scenario-grid")!) & window.Node.DOCUMENT_POSITION_FOLLOWING, "the hint is above the cards");
assert.equal(buttons("scenarios.refresh").length, 0, "the list refreshes itself, so there is no refresh button");
assert.ok(!container.textContent?.includes("scenarios.uploadUnconfigured"), "the unconfigured upload note is not shown on the page");
assert.ok(button("scenarios.upload").disabled, "unconfigured upload must be disabled");
assert.equal(buttons("scenarios.editTerrain").length, 2, "only own scenarios can edit terrain");
assert.equal(buttons("scenarios.editCopy").length, 2, "workshop and DLC scenarios are copied before editing");
assert.equal(buttons("scenarios.editMetadata").length, 2);
assert.equal(buttons("common.delete").length, 2, "only own scenarios can be deleted");
assert.ok(!cardButton("workshop~123~copy", "scenarios.editCopy").disabled);
assert.ok(cardButton("dlc~pack~city", "scenarios.editCopy").disabled, "unowned DLC cannot be opened");
assert.ok(card("workshop~123~copy").textContent?.includes("scenarios.origin.workshop"));
assert.ok(card("dlc~pack~city").textContent?.includes("scenarios.official"));
assert.ok(card("dlc~pack~city").querySelector(".required-pack--not_owned"), "pack ownership is shown");
assert.ok(card("dlc~pack~city").textContent?.includes("saveCompatibility.SCENARIO_PACK_NOT_OWNED"));
assert.ok(card("local~future").textContent?.includes("saveCompatibility.SAVE_VERSION_NEWER"));
assert.ok(!container.textContent?.includes("scenarios.play") && !container.textContent?.includes("scenarios.export"));

// 操作はアイコンだけのボタンで、説明は title に出す。左詰めに並べ、削除だけが右端(順序: 紹介情報・地形・都市・アップロード・削除)。
const actionLabels = (id: string) => [...card(id).querySelectorAll<HTMLButtonElement>(".scenario-actions button")].map(value => value.getAttribute("aria-label"));
assert.deepEqual(actionLabels("local~mine"), ["scenarios.editMetadata", "scenarios.editTerrain", "scenarios.editCity", "scenarios.upload", "common.delete"]);
assert.ok([...card("local~mine").querySelectorAll(".scenario-actions button")].every(value => value.textContent === "" && value.querySelector("img")), "own scenario actions are icons only");
assert.ok(cardButton("local~mine", "common.delete").classList.contains("scenario-icon-button--delete"), "delete sits at the right end");
assert.equal(cardButton("local~mine", "scenarios.editCity").title, "scenarios.editCity", "the description shows on mouse over");
assert.equal(cardButton("local~mine", "scenarios.upload").title, "scenarios.upload\nscenarios.uploadUnconfigured", "a disabled reason follows the description");
assert.deepEqual(actionLabels("workshop~123~copy"), [null], "read-only scenarios keep the text button only (no delete)");

// 削除: 確認を断れば何もしない。確認すると(元に戻せない操作なので danger の確認)ファイルを消し、一覧から外して他のウィンドウへ知らせる。
// 読み込めない(壊れた・新しすぎる)シナリオこそ片付けたいので、読み込めなくても削除は押せる。失敗しても一覧を取り直す。
assert.ok(!cardButton("local~future", "common.delete").disabled, "an unloadable scenario can still be deleted");
state.confirmed = false;
await act(async () => { cardButton("local~future", "common.delete").click(); });
await settle();
assert.deepEqual(deleteRequests, [], "declining the confirmation deletes nothing");
assert.equal(lastCall("open_registered_window").params.extra.confirmRequest.tone, "danger");
assert.equal(lastCall("open_registered_window").params.extra.confirmRequest.confirmLabel, "common.delete");
state.confirmed = true;
rejectDelete = true;
const fetchesBefore = listFetches;
await act(async () => { cardButton("local~future", "common.delete").click(); });
await settle();
assert.equal(container.querySelector("[role=alert]")?.textContent, "saveCompatibility.SCENARIO_READ_ONLY");
assert.ok(listFetches > fetchesBefore, "a failed delete refreshes the list");
assert.ok(card("local~future"), "a rejected delete keeps the card");
rejectDelete = false;
entries.push({ ...entry, id: "local~doomed" });
await act(async () => { await emit("scenarios:changed"); });
await settle();
await act(async () => { cardButton("local~doomed", "common.delete").click(); });
await settle();
assert.deepEqual(deleteRequests.slice(-1), ["http://test/api/v1/saves/local~doomed?template=true"], "the scenario is deleted by its origin-qualified ID");
assert.equal(container.querySelectorAll("article").length, 4);
assert.ok(!container.querySelector("[data-scenario-id='local~doomed']"), "the deleted card is gone");
assert.equal(container.querySelector(".scenario-status")?.textContent, "scenarios.deleted");
assert.equal(container.querySelector("[role=alert]")?.textContent, undefined, "a successful delete shows no error");
assert.ok(!container.querySelector(".scenario-card--selected"), "the deleted card is no longer selected");
assert.ok(state.emitted.includes("scenarios:changed"), "other windows are told the list changed");

await act(async () => { cardButton("local~mine", "scenarios.editMetadata").click(); });
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
// 最低・最高標高は1行に並ぶ2つの入力なので、ラベルではなく aria-label(項目名)で探す。
const fieldInput = (label: string) => importForm().querySelector<HTMLInputElement>(`input[aria-label="${label}"]`)
  ?? [...importForm().querySelectorAll("label")].find(value => value.textContent?.startsWith(label))!.querySelector("input")!;
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
assert.equal(fieldInput("scenarios.importMinHeight").parentElement, fieldInput("scenarios.importMaxHeight").parentElement, "the lowest and highest elevations share one row");
assert.equal(fieldInput("scenarios.importMinHeight").parentElement?.textContent, "〜", "a wave dash sits between the two inputs");
assert.equal(importForm().querySelector("label")?.textContent, "scenarios.importTitle", "the title comes first");
const groups = [...importForm().querySelectorAll(".scenario-group")];
assert.equal(groups.length, 2, "the terrain and the background are each framed");
assert.deepEqual(groups.map(value => value.querySelector(".scenario-group-title")?.textContent),
  ["scenarios.importDetail", "scenarios.importBackgroundscenarios.importOptional"], "each area has its own title");
assert.deepEqual(groups.map(value => value.querySelector(".scenario-hint")?.textContent), ["scenarios.importDetailHint", "scenarios.importBackgroundHint"],
  "a short description sits under each title");
assert.deepEqual(groups.map(value => value.querySelector(".scenario-import-row span")?.textContent), ["scenarios.importFile", "scenarios.importFile"]);
assert.ok(groups[0].contains(fileInput()) && groups[1].contains(importForm().querySelectorAll("input[type=file]")[1]) && !groups[0].contains(fieldInput("scenarios.importTitle")));
assert.deepEqual([...importForm().querySelectorAll(".scenario-import-info")].map(value => value.getAttribute("title")), ["scenarios.importFormats", "scenarios.importBackgroundFormats"],
  "the format explanations are tooltips next to the item names");
assert.ok(!container.textContent?.includes("scenarios.importFormats") && !container.textContent?.includes("scenarios.importBackgroundFormats"), "and they are not always shown");
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
drawn.length = 0; // ここまでのプレビューの描画は平面図に数えない
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
assert.ok(!importForm().textContent?.includes("scenarios.importNpyHeights"), "no explanation is written out; the fields show the file's actual range instead");
await act(async () => { await new Promise(resolve => setTimeout(resolve, 350)); });
assert.deepEqual([fieldInput("scenarios.importMinHeight").value, fieldInput("scenarios.importMaxHeight").value], ["-12.3", "513"],
  "a .npy shows its own lowest and highest elevations (one decimal place)");
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

// プレビューと背景地形: 選んだファイルの標高をコアから受け取って描く。標高を取り直すのはファイル・海面の高さ・PNGの標高範囲が変わったときだけで、
// 1画素あたりの距離は配置を変えるだけ。背景は .npy だけで、詳細地形の後ろへ続けて1本の本文で送る。
const backgroundInput = () => importForm().querySelectorAll<HTMLInputElement>("input[type=file]")[1];
const chooseBackground = async (file: File) => {
  await act(async () => {
    Object.defineProperty(backgroundInput(), "files", { value: [file], configurable: true });
    backgroundInput().dispatchEvent(new window.Event("change", { bubbles: true }));
  });
  await settle();
};
const waitPreview = () => act(async () => { await new Promise(resolve => setTimeout(resolve, 350)); });
const clearBackgroundButton = () => [...importForm().querySelectorAll("button")].find(value => value.textContent === "scenarios.clearBackground");
await act(async () => { button("scenarios.tabTerrains").click(); });
await settle();
previewRequests.length = 0;
drawn.length = 0;
assert.equal(backgroundInput().accept, ".npy");
assert.ok(container.querySelector(".scenario-import-preview")?.textContent?.includes("scenarios.previewEmpty"), "nothing to preview before a file is chosen");
assert.ok(!clearBackgroundButton(), "there is no background to remove yet");
assert.ok(fieldInput("scenarios.importBackgroundSpacing").disabled, "the background distance needs a background file");
await choose(png(201, 101));
await waitPreview();
assert.equal(previewRequests.length, 1);
assert.deepEqual(Object.fromEntries(previewRequests[0].query), { sea_level: "5", min_height: "100", max_height: "900" }, "a PNG is read with its elevation range");
assert.equal(previewRequests[0].size, 33, "the file itself is sent");
assert.deepEqual(drawn, [{ width: 768, height: 384 }], "the preview keeps the map's aspect ratio (2 x 1 km)");
await act(async () => { setText(fieldInput("scenarios.importSeaLevel"), "7"); });
await waitPreview();
assert.equal(previewRequests.length, 2);
assert.equal(previewRequests[1].query.get("sea_level"), "7", "the sea level changes the elevations, so they are read again");
await act(async () => { setText(fieldInput("scenarios.importSpacing"), "20"); });
await waitPreview();
assert.equal(previewRequests.length, 2, "the distance per pixel only moves the layout");
await act(async () => { setText(fieldInput("scenarios.importSpacing"), "10"); });
await act(async () => { setText(fieldInput("scenarios.importMaxHeight"), "50"); });
await waitPreview();
assert.equal(previewRequests.length, 2, "an invalid range keeps the previous preview and asks for nothing");
await act(async () => { setText(fieldInput("scenarios.importMaxHeight"), "900"); });
await waitPreview();
assert.equal(previewRequests.length, 3, "a valid range reads the file again");
assert.equal(previewRequests[2].query.get("max_height"), "900");

const backgroundFile = npy("{'descr': '<f4', 'fortran_order': False, 'shape': (61, 101), }", "bg.npy");
await chooseBackground(backgroundFile);
await waitPreview();
assert.equal(previewRequests.length, 4);
assert.deepEqual(Object.fromEntries(previewRequests[3].query), { sea_level: "7", background: "true" }, "a background is read as .npy, without an elevation range");
assert.equal(importForm().querySelectorAll(".scenario-import-size")[1]?.textContent, "5 × 3 km", "the background size is previewed from the .npy header (50 m grid)");
assert.deepEqual(drawn.at(-1), { width: 768, height: 461 }, "the preview fits the background, which is larger than the map");
assert.ok(!fieldInput("scenarios.importBackgroundSpacing").disabled && !!clearBackgroundButton());
assert.deepEqual([importedBackgroundExtent(101, 50), importedBackgroundExtent(101, 2000), importedBackgroundExtent(3, 10)], [5000, 200000, 0],
  "the extent is rounded to the 50 m grid without rounding up to whole kilometres (0 when the grid would have fewer than 2 pixels)");
await chooseBackground(npy("{'descr': '<f4', 'fortran_order': False, 'shape': (61, 201), }", "wide.npy"));
await act(async () => { setText(fieldInput("scenarios.importBackgroundSpacing"), "1000"); });
assert.ok(importForm().textContent?.includes("scenarios.importBackgroundTooLarge"), "backgrounds over 100 km per side are rejected before sending");
assert.ok(createButton().disabled);
await act(async () => { setText(fieldInput("scenarios.importBackgroundSpacing"), "0"); });
assert.ok(importForm().textContent?.includes("scenarios.importBackgroundInvalid"));
assert.ok(createButton().disabled);
await act(async () => { setText(fieldInput("scenarios.importBackgroundSpacing"), "50"); });
await chooseBackground(backgroundFile);
assert.ok(!createButton().disabled && !importForm().textContent?.includes("scenarios.importBackgroundTooLarge"));
await act(async () => { clearBackgroundButton()!.click(); });
assert.ok(!clearBackgroundButton() && fieldInput("scenarios.importBackgroundSpacing").disabled, "removing the background leaves only the terrain");
await chooseBackground(backgroundFile);
await act(async () => { createButton().click(); });
await settle();
assert.equal(imported!.query.get("background_spacing"), "50");
assert.equal(imported!.query.get("background_size"), String(backgroundFile.size));
assert.ok(imported!.body instanceof Blob && !(imported!.body instanceof File), "the background follows the terrain file in one body");
assert.equal((imported!.body as Blob).size, 33 + backgroundFile.size);
entries.pop();
await act(async () => { button("scenarios.tabTerrains").click(); });
await settle();
assert.ok(!clearBackgroundButton(), "the background is cleared with the file after a successful import");
assert.equal(importForm().textContent?.split("scenarios.noFileChosen").length, 3, "both files are cleared");
await act(async () => { root.unmount(); });
entries.pop();

// プレビューの画素: 標高0m以下(海)は透明、詳細地形は標高で色分け、背景は淡い単色。詳細地形の海の下に背景の陸地があればそれが見え、北が上。
const layer = (values: number[], rows: number, cols: number, extent: number): PreviewLayer => ({
  elev: new Float32Array(values), rows, cols, dataWidth: extent, dataHeight: extent, fillWidth: extent, fillHeight: extent });
const previewImage = renderImportPreview({
  detail: layer([-50, -50, 30, 30], 2, 2, 2000), // 行0が南: 南半分が海・北半分が陸
  background: layer([-1, -1, 5, 5], 2, 2, 4000), // 南が海・北が陸(境目は南寄り)
}, 8)!;
assert.deepEqual([previewImage.width, previewImage.height], [8, 8], "the whole background fits: 4000 m / 8 px");
const pixel = (x: number, y: number) => [...previewImage.pixels.subarray((y * 8 + x) * 4, (y * 8 + x) * 4 + 4)];
assert.deepEqual(pixel(0, 7), [0, 0, 0, 0], "the sea of the background is transparent (south-west corner)");
assert.deepEqual(pixel(0, 0), [...BACKGROUND_RGB, 255], "the background land is a flat pale colour (north-west corner)");
assert.equal(pixel(3, 2)[3], 255);
assert.notDeepEqual(pixel(3, 2).slice(0, 3), [...BACKGROUND_RGB], "the terrain is coloured by elevation (north of the centre)");
assert.deepEqual(pixel(3, 5), [...BACKGROUND_RGB, 255], "the background land shows through where the terrain is sea (south of the centre)");
assert.equal(renderImportPreview({ detail: null, background: null }, 8), null);

// DLC 由来の暗号化された自作シナリオは、地形エディタを開けない(作業コピーが平文で残るため)。都市は編集できる。
entries.push({ ...entry, id: "local~dlc-copy", encrypted: true });
root = createRoot(container);
await act(async () => { root.render(<ScenarioManagerWindow apiBaseUrl="http://test" />); });
await settle();
assert.ok(cardButton("local~dlc-copy", "scenarios.editTerrain").disabled, "encrypted scenarios cannot open the terrain editor");
assert.equal(cardButton("local~dlc-copy", "scenarios.editTerrain").title, "scenarios.editTerrain\nscenarios.terrainEncryptedHint");
assert.ok(!cardButton("local~dlc-copy", "scenarios.editCity").disabled);
assert.ok(!cardButton("local~mine", "scenarios.editTerrain").disabled);
assert.equal(cardButton("local~mine", "scenarios.editTerrain").title, "scenarios.editTerrain");
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
assert.ok(container.querySelector(".localized-text__panel")?.contains(container.querySelector("textarea")), "the fields switched by the tabs are framed");
assert.ok(!container.querySelector(".localized-text__panel")?.textContent?.includes("scenarios.author"), "the author is outside the tab frame");
assert.equal(container.querySelector(".scenario-group-title")?.textContent, "scenarios.startSettings", "the start settings title sits inside its frame");
assert.deepEqual([...container.querySelectorAll<HTMLElement>(".scenario-start-row")].map(row => row.dataset.option),
  ["language", "simulation_days", "climate", "ground_cover", "asset_region"], "the city language leads the start settings");
const changeableBox = (option: string) => container.querySelector<HTMLInputElement>(`.scenario-start-row[data-option=${option}] input[type=checkbox]`)!;
assert.ok(changeableBox("language").checked && changeableBox("language").disabled, "players can always change the language");
assert.ok(!changeableBox("climate").checked && changeableBox("simulation_days").checked, "the checkbox shows what players may change");
await act(async () => { changeableBox("simulation_days").click(); });
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
currentSaveId = "unsaved-city";
const callsBeforeNewGame = state.calls.length;
let newGameClosed = 0;
root = createRoot(container);
await act(async () => { root.render(<NewGameWindow apiBaseUrl="http://test" onClose={() => { newGameClosed++; }} />); });
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
// 開始設定は選択画面の上に重なるダイアログで決める。幕を押すか「戻る」で閉じ、選択は残る。
const overlay = () => container.querySelector<HTMLElement>(".new-game-confirm-overlay");
const pressDown = (element: Element) => act(async () => { element.dispatchEvent(new window.MouseEvent("mousedown", { bubbles: true })); });
assert.ok(overlay()?.querySelector("[role=dialog] .new-game-confirm-select"), "the start settings open as a dialog");
assert.equal(overlay()?.querySelector(".new-game-confirm-warning")?.textContent, "newGame.unsavedChangesWarning",
  "an active city needs a discard warning even when it has never been saved");
assert.ok(button("newGame.startButton").classList.contains("save-dialog-button--danger"));
assert.ok(container.querySelector(".new-game-list .new-game-tile") && container.querySelector(".new-game-detail-title"), "the scenario list and detail stay behind the dialog");
assert.ok(!container.textContent?.includes("newGame.closeWindowsNote"), "the dialog does not announce that windows close");
await pressDown(overlay()!.querySelector("[role=dialog]")!);
assert.ok(overlay(), "pressing inside the dialog keeps it open");
await pressDown(overlay()!);
assert.equal(overlay(), null, "pressing the backdrop closes the dialog");
assert.equal(container.querySelector(".new-game-detail-title")?.textContent, "My scenario", "the selection remains");
await act(async () => { button("newGame.startScenarioButton").click(); });
await settle();
await act(async () => { button("newGame.backButton").click(); });
assert.equal(overlay(), null, "back closes the dialog");
await act(async () => { window.dispatchEvent(new window.KeyboardEvent("keydown", { key: "Escape" })); });
await act(async () => { button("common.close").click(); });
assert.equal(newGameClosed, 2, "Esc and the footer can return to the current game");
assert.ok(!state.calls.slice(callsBeforeNewGame).some(call => ["launch_graphic", "create_session", "send_window_action"].includes(call.command)),
  "browsing scenarios and cancelling does not reset Unity or replace the city");
await act(async () => { button("newGame.startScenarioButton").click(); });
await settle();
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
// 開始に失敗したら、理由をダイアログの中に出して開いたままにする。
state.errors.create_session = "SAVE_CORRUPT: corrupt";
await act(async () => { button("newGame.startButton").click(); });
await settle();
assert.ok(overlay()?.querySelector(".new-game-confirm .save-dialog-error"), "a failed start is reported in the dialog");
assert.ok(!button("newGame.startButton").disabled, "the dialog stays usable after a failure");
delete state.errors.create_session;
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
assert.ok(container.querySelector(".new-game-confirm-warning"), "tutorials also warn before replacing the current game");
await act(async () => { await emit("session-state", { current_save_id: null, backendConnected: false }); });
assert.ok(container.querySelector(".new-game-confirm-warning"), "a connection failure does not erase the discard warning");
currentSaveId = null;
await act(async () => { await emit("session-state", { current_save_id: null, backendConnected: true }); });
assert.equal(container.querySelector(".new-game-confirm-warning"), null, "there is nothing to discard from the top menu");
assert.ok(button("newGame.startButton").classList.contains("save-dialog-button--primary"));
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
for (const code of ["SCENARIO_TOO_LARGE", "SCENARIO_READ_ONLY", "SCENARIO_ENCRYPTED", "NOT_FOUND", "SESSION_ACTIVE", "HEIGHTMAP_INVALID"]) {
  assert.ok(code in jaScenarios.saveCompatibility, `saveCompatibility.${code} is translated`);
}
for (const code of ["TERRAIN_LOCKED", "TERRAIN_INVALID", "TERRAIN_SIZE_MISMATCH", "RESOURCE_VALIDATION_ERROR", "PROP_VALIDATION_ERROR"]) {
  assert.ok(code in jaTerrainEditor.terrainEditor.saveErrors, `terrainEditor.saveErrors.${code} is translated`);
}
// シナリオとして保存(紹介情報ウィンドウの作成モード): プレイ中の都市の紹介情報を初期値にし、保存を押すまで何も書かない。
state.results.request_thumbnail_capture = false; // Unityが無いときは撮影を待たずに保存する
const submitDialog = () => act(async () => {
  container.querySelector(".save-dialog")!.dispatchEvent(new window.Event("submit", { bubbles: true, cancelable: true }));
});
root = createRoot(container);
let createClosed = false;
await act(async () => { root.render(<ScenarioMetadataWindow apiBaseUrl="http://test" scenarioId="" create onClose={() => { createClosed = true; }} />); });
await settle();
assert.equal(container.querySelector(".scenario-start-row[data-option=language] .scenario-start-value")?.textContent, "settings.language.ja",
  "the city language comes from the session");
assert.ok(!container.querySelector(".save-version-info"), "an unsaved scenario has no saved format version");
await act(async () => { button("common.cancel").click(); });
assert.ok(createClosed);
assert.equal(saveRequests.length, 0, "cancelling saves nothing");
await act(async () => { root.unmount(); });
root = createRoot(container);
await act(async () => { root.render(<ScenarioMetadataWindow apiBaseUrl="http://test" scenarioId="" create onClose={() => {}} />); });
await settle();
state.emitted.length = 0;
await submit();
await settle();
const createdScenario = saveRequests.find(request => request.url.endsWith("/api/v1/templates"));
assert.ok(createdScenario && createdScenario.method === "POST", "saving creates a new scenario");
const createdBody = JSON.parse(String(createdScenario!.body));
assert.deepEqual([createdBody.scenario_title, createdBody.scenario_city_name, createdBody.scenario_fixed_options, createdBody.climate],
  [{ en: "Port" }, { en: "Port Town" }, ["climate"], 2], "the playing city's introduction is the starting point");
assert.ok(lastCall("request_thumbnail_capture"), "without a chosen image the 3D view is captured first");
assert.ok(state.emitted.includes("scenarios:changed"));
await act(async () => { root.unmount(); });

// システムメニュー: まだ保存していない都市の上書きセーブは別名セーブになり、都市名を名前の初期値にする。保存したら閉じる。
saveRequests.length = 0;
currentSaveId = "fresh";
let menuClosed = 0;
const renderMenu = async () => {
  root = createRoot(container);
  await act(async () => { root.render(<SaveLoadWindow apiBaseUrl="http://test" onClose={() => { menuClosed++; }} />); });
  await settle();
};
const menuItem = (label: string) => [...container.querySelectorAll<HTMLButtonElement>(".save-menu-item")]
  .find(item => item.querySelector(".save-menu-label")?.textContent === label)!;
const saveDialog = () => container.querySelector<HTMLElement>(".save-dialog");
await renderMenu();
await act(async () => { menuItem("saveLoad.overwriteSave").click(); });
assert.equal(saveDialog()?.getAttribute("aria-label"), "saveLoad.saveAs", "a city that was never saved gets a name first");
const nameInput = () => container.querySelector<HTMLInputElement>(".save-dialog-input")!;
assert.equal(nameInput().value, "港町");
assert.equal(saveRequests.length, 0, "nothing is saved before the name is confirmed");
await act(async () => { setText(nameInput(), "  港町 序盤 "); });
await submitDialog();
await settle();
assert.deepEqual(saveRequests.map(request => [request.method, request.url.replace("http://test", "")]), [["POST", "/api/v1/saves"]]);
assert.deepEqual(JSON.parse(String(saveRequests[0].body)), { name: "港町 序盤" });
assert.equal(menuClosed, 1, "the menu closes after saving");
await act(async () => { root.unmount(); });

// 保存済みの都市: 上書きセーブは対象の名前を示して確認してから保存する。
saveRequests.length = 0;
currentSaveId = "slot1";
savedGames = { slot1: { ...saveEntry, id: "slot1", save_name: "港町 序盤" } };
await renderMenu();
assert.equal(menuItem("saveLoad.overwriteSave").querySelector(".save-menu-detail")?.textContent, "港町 序盤");
await act(async () => { menuItem("saveLoad.overwriteSave").click(); });
assert.equal(saveDialog()?.getAttribute("aria-label"), "saveLoad.overwriteSave");
assert.equal(saveRequests.length, 0, "overwriting waits for the confirmation");
await act(async () => { saveDialog()!.parentElement!.dispatchEvent(new window.MouseEvent("mousedown", { bubbles: true })); });
assert.equal(saveDialog(), null, "pressing the backdrop cancels");
await act(async () => { menuItem("saveLoad.overwriteSave").click(); });
await submitDialog();
await settle();
assert.deepEqual(saveRequests.map(request => [request.method, request.url.replace("http://test", "")]), [["PUT", "/api/v1/saves/slot1"]]);
assert.equal(menuClosed, 2);
// シナリオとして保存は作成モードの紹介情報ウィンドウを開くだけで、ここでは保存しない。どのモードでも押せる。
saveRequests.length = 0;
assert.ok(!menuItem("saveLoad.saveAsScenario").disabled);
await act(async () => { menuItem("saveLoad.saveAsScenario").click(); });
await settle();
assert.deepEqual(lastCall("open_registered_window"),
  { windowType: "scenario_metadata", params: { extra: { create: true, titleKey: "saveLoad.saveAsScenario" } } });
assert.equal(saveRequests.length, 0);
assert.equal(menuClosed, 3);
await act(async () => { window.dispatchEvent(new window.KeyboardEvent("keydown", { key: "Escape" })); });
assert.equal(menuClosed, 4, "Esc closes the menu");
await act(async () => { root.unmount(); });

// プレイ中のニューゲーム入口は選択画面を開くだけ。確認・Unityリセットは最終の開始まで行わない。
await renderMenu();
const callsBeforeNewGameMenu = state.calls.length;
const menuClosedBeforeNewGame = menuClosed;
await act(async () => { menuItem("saveLoad.newGame").click(); });
await settle();
assert.equal(saveDialog(), null, "opening the new game window does not ask to discard the current game");
assert.deepEqual(state.calls.slice(callsBeforeNewGameMenu), [{ command: "open_registered_window", args: { windowType: "new_game", params: undefined } }],
  "the new game menu only opens the scenario browser");
assert.equal(menuClosed, menuClosedBeforeNewGame + 1, "the system menu closes after opening the scenario browser");
await act(async () => { root.unmount(); });

// セーブ一覧: 新しいものが先頭。名前(無ければ都市名)・元のシナリオ・言語の国旗を出し、削除は知らせずに一覧から外す。
saveRequests.length = 0;
currentSaveId = "new";
savedGames = {
  old: { ...saveEntry, id: "old", save_name: "古い", saved_at: "20260101_100000", language: "en" },
  new: { ...saveEntry, id: "new", scenario_title: { en: "Port" } },
};
let listClosed = false;
root = createRoot(container);
await act(async () => { root.render(<SaveListWindow apiBaseUrl="http://test" onClose={() => { listClosed = true; }} />); });
await settle();
const saveCards = () => [...container.querySelectorAll<HTMLElement>(".save-card")];
assert.deepEqual(saveCards().map(card => card.querySelector(".save-card-name")?.textContent), ["港町", "古い"]);
assert.deepEqual(saveCards().map(card => card.querySelector(".save-card-flag")?.textContent), ["🇯🇵", "🇺🇸"]);
assert.equal(saveCards()[0].querySelector(".save-card-scenario")?.textContent, "Port");
assert.equal(saveCards()[1].querySelector(".save-card-scenario"), null, "saves without a scenario title show no scenario line");
assert.ok(saveCards()[0].classList.contains("save-card--current"));
await act(async () => { saveCards()[1].querySelector<HTMLButtonElement>(".save-card-delete")!.click(); });
assert.equal(saveDialog()?.getAttribute("aria-label"), "古い");
await act(async () => { [...saveDialog()!.querySelectorAll("button")].find(named("saveList.deleteButton"))!.click(); });
await settle();
assert.deepEqual(saveRequests.map(request => [request.method, request.url.replace("http://test", "")]), [["DELETE", "/api/v1/saves/old"]]);
assert.equal(saveCards().length, 1);
assert.equal(saveDialog(), null, "a finished delete leaves no message behind");
await act(async () => { saveCards()[0].querySelector<HTMLButtonElement>(".save-card-load")!.click(); });
await submitDialog();
await settle();
assert.deepEqual(lastCall("start_load_game"), { saveId: "new", acceptBalanceChange: true });
assert.ok(listClosed);
await act(async () => { root.unmount(); });

console.log("PASS: scenario manager tabs and origin actions, icon buttons and delete, encrypted terrain lock, terrain editor open errors, heightmap import (size preview, validation, request, thumbnail, terrain preview and background terrain), new game tabs and terrain facts, tutorial start, new game language and start options, localized metadata and start settings, load confirmation and error codes, save as scenario, system menu saves, save list");
