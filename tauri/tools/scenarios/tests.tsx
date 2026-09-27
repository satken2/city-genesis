import assert from "node:assert/strict";
import { Window } from "happy-dom";
import { act } from "react";
import { createRoot } from "react-dom/client";
import { ScenarioManagerWindow } from "../../src/components/template/ScenarioManagerWindow";
import { ScenarioMetadataWindow } from "../../src/components/template/ScenarioMetadataWindow";
import { prepareSaveLoad } from "../../src/utils/saves";
import { ApiError } from "../../src/utils/api";
import { state } from "./mock-runtime";

const window = new Window();
Object.assign(globalThis, { window, document: window.document, HTMLElement: window.HTMLElement,
  IS_REACT_ACT_ENVIRONMENT: true });
const entry = { id: "local~mine", city_name: "City", scenario_title: "My scenario", author: "Author",
  scenario_description: "Description", scenario_objective: "Objective", category: "sandbox", language: "ja",
  saved_core_version: "1.0.0", created_core_version: "1.0.0", compatibility: { loadable: true }, origin: "local" };
const entries = [entry, { ...entry, id: "builtin~original", origin: "builtin" },
  { ...entry, id: "downloaded~copy", origin: "downloaded" },
  { ...entry, id: "local~future", compatibility: { loadable: false, code: "SAVE_VERSION_NEWER" } }];
let validation: object = { balance_changed: false };
let rejectLoad = false;
let metadataWritten: any;
globalThis.fetch = async (input, init) => {
  const url = String(input);
  if (url.endsWith("/validation?template=true")) return new Response(JSON.stringify(rejectLoad
    ? { code: "SAVE_CORRUPT", message: "corrupt" } : { compatibility: validation }), { status: rejectLoad ? 422 : 200 });
  if (init?.method === "PATCH") { metadataWritten = JSON.parse(String(init.body)); return new Response("{}"); }
  if (url.includes("/saves/")) return new Response(JSON.stringify({ save: entry }));
  return new Response(JSON.stringify({ scenarios: Object.fromEntries(entries.map(value => [value.id, value])), upload_available: false }));
};

const container = window.document.createElement("div");
window.document.body.append(container);
let root = createRoot(container);
await act(async () => { root.render(<ScenarioManagerWindow apiBaseUrl="http://test" />); });
assert.equal(container.querySelectorAll("article").length, 4);
const button = (text: string) => [...container.querySelectorAll("button")].find(value => value.textContent === text)!;
assert.ok(button("scenarios.upload").disabled, "unconfigured upload must be disabled");
await act(async () => { button("scenarios.local").click(); });
assert.equal(container.querySelectorAll("article").length, 2);
assert.ok(container.textContent?.includes("saveCompatibility.SAVE_VERSION_NEWER"));
await act(async () => { button("scenarios.metadata").click(); });
assert.equal(state.calls.at(-1)?.args.params.extra.scenarioId, entry.id);
await act(async () => { root.unmount(); });

root = createRoot(container);
let closed = false;
await act(async () => { root.render(<ScenarioMetadataWindow apiBaseUrl="http://test" scenarioId={entry.id} onClose={() => { closed = true; }} />); });
assert.equal(container.querySelectorAll("textarea").length, 2);
await act(async () => { container.querySelector("form")!.dispatchEvent(new window.Event("submit", { bubbles: true, cancelable: true })); });
assert.equal(metadataWritten.scenario_title, entry.scenario_title);
assert.equal(metadataWritten.author, entry.author);
assert.ok(closed);
await act(async () => { root.unmount(); });

validation = { balance_changed: true, balance_core_version: "1.0.0", core_version: "1.1.0" };
state.confirmed = false;
assert.equal(await prepareSaveLoad("http://test", entry.id, true, ((key: string) => key) as any), false);
state.confirmed = true;
assert.equal(await prepareSaveLoad("http://test", entry.id, true, ((key: string) => key) as any), true);
rejectLoad = true;
await assert.rejects(prepareSaveLoad("http://test", entry.id, true, ((key: string) => key) as any), ApiError);
console.log("PASS: origin filters, incompatible cards, upload gate, metadata submission and load confirmation");
