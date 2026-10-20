import assert from "node:assert/strict";
import { act } from "react";
import { createRoot } from "react-dom/client";
import { ClimateSettingsWindow } from "../../src/components/climate/ClimateSettingsWindow";
import { MasterDataProvider } from "../../src/contexts/MasterDataContext";
import { PLAY_MODE_NORMAL, PLAY_MODE_SCENARIO } from "../../src/constants";
import { copyClimatePreset, moveWeatherBoundary, setWeatherPercentage, type ClimatePreset } from "../../src/utils/climate";
import { emit, state } from "./mock-runtime";

const preset: ClimatePreset = { display_name: "テストの気候", months: Array.from({ length: 12 }, (_, month) => ({
  temperature: month + 5, precipitation: month * 10, sunlight: 70,
  weather: { clear: 60, rain: 30, snow: 10, future_weather: 0 },
})) };
const original = structuredClone(preset);
const copied = copyClimatePreset(preset);
copied.months[0].weather.clear = 1;
assert.deepEqual(preset, original, "copying a preset does not retain mutable master references");
assert.deepEqual(moveWeatherBoundary(preset.months[0].weather, "clear", "rain", 100),
  { clear: 90, rain: 0, snow: 10, future_weather: 0 });
assert.deepEqual(moveWeatherBoundary(preset.months[0].weather, "clear", "rain", -100),
  { clear: 0, rain: 90, snow: 10, future_weather: 0 });
for (const weather of [preset.months[0].weather, { clear: 100, rain: 0, snow: 0, future_weather: 0 }]) {
  for (const id of Object.keys(weather)) for (const requested of [-1, 0, 17.5, 73, 100, 101]) {
    const result = setWeatherPercentage(weather, id, requested);
    assert.equal(Object.values(result).reduce((sum, value) => sum + value, 0), 100);
    assert.ok(Object.values(result).every(value => Number.isInteger(value) && value >= 0 && value <= 100));
    assert.equal(result[id], Math.max(0, Math.min(100, Math.round(requested))));
    assert.deepEqual(Object.keys(result), Object.keys(weather));
  }
}

let playMode = PLAY_MODE_SCENARIO;
let writes: any[] = [];
let reject = false;
const saved = copyClimatePreset({ ...preset, display_name: "作者の気候" });
globalThis.fetch = async (input, init) => {
  const url = String(input);
  if (url.endsWith("/api/v1/city/climate")) {
    writes.push(JSON.parse(String(init?.body)));
    return new Response(JSON.stringify(reject ? { code: "SAVE_CORRUPT" } : { climate: writes.at(-1) }), { status: reject ? 422 : 200 });
  }
  if (url.endsWith("/api/v1/city/")) return new Response(JSON.stringify({ city: { climate: saved } }));
  if (url.endsWith("/api/v1/sessions")) return new Response(JSON.stringify({ play_mode: playMode }));
  const category = url.split("/").at(-1)!;
  const data = category === "climates" ? { 1: preset } : category === "weather"
    ? Object.fromEntries(Object.keys(preset.months[0].weather).map(id => [id, { display_name: id }])) : {};
  return new Response(JSON.stringify({ [category]: data }));
};
const container = document.createElement("div");
document.body.append(container);
let closed = 0;
let root = createRoot(container);
const render = () => <MasterDataProvider apiBaseUrl="http://test" connected><ClimateSettingsWindow apiBaseUrl="http://test" onClose={() => closed++} /></MasterDataProvider>;
const settle = () => act(async () => { await new Promise(resolve => setTimeout(resolve, 20)); });
const button = (key: string) => [...container.querySelectorAll<HTMLButtonElement>("button")].find(element => element.textContent === key)!;
const input = (selector: string) => container.querySelector<HTMLInputElement>(selector)!;
const change = (element: HTMLInputElement | HTMLSelectElement, value: string) => act(async () => {
  const prototype = element.tagName === "SELECT" ? window.HTMLSelectElement.prototype : window.HTMLInputElement.prototype;
  Object.getOwnPropertyDescriptor(prototype, "value")!.set!.call(element, value);
  element.dispatchEvent(new window.Event(element.tagName === "SELECT" ? "change" : "input", { bubbles: true }));
});
await act(async () => { root.render(render()); });
await settle();
assert.equal(input(".climate-settings-name").value, saved.name);
assert.equal(container.querySelectorAll(".climate-weather-row").length, 12);
assert.equal(container.querySelectorAll(".climate-weather-values input").length, 4, "weather fields follow the master, including new identifiers");
await change(container.querySelector("select")!, "1");
assert.equal(input(".climate-settings-name").value, preset.display_name);
await change(input(".climate-settings-name"), " 編集した気候 ");
await change(input(".climate-weather-values label:last-child input"), "25");
assert.equal([...container.querySelectorAll<HTMLInputElement>(".climate-weather-values input")].reduce((sum, value) => sum + Number(value.value), 0), 100);
await change(input(".climate-metric-values label:first-child input"), "31.5");
await act(async () => { container.querySelector(".climate-chart-handle")!.dispatchEvent(new window.KeyboardEvent("keydown", { key: "ArrowUp", bubbles: true })); });
assert.equal(input(".climate-metric-values label:first-child input").value, "32.5");
assert.equal(writes.length, 0, "all edits remain local until Apply");
await act(async () => { button("common.cancel").click(); });
assert.equal(writes.length, 0);
assert.equal(closed, 1);
await act(async () => { root.unmount(); });

root = createRoot(container);
await act(async () => { root.render(render()); });
await settle();
assert.equal(input(".climate-settings-name").value, saved.name, "cancelled draft is discarded");
await change(container.querySelector("select")!, "1");
await change(input(".climate-settings-name"), " 保存する気候 ");
await change(input(".climate-metric-values label:nth-child(3) input"), "55");
const submit = () => act(async () => { container.querySelector("form")!.dispatchEvent(new window.Event("submit", { bubbles: true, cancelable: true })); });
reject = true;
await submit();
assert.ok(container.querySelector("[role=alert]"), "a rejected request keeps the draft open");
assert.equal(closed, 1);
reject = false;
await submit();
assert.equal(writes.at(-1).name, "保存する気候");
assert.equal(writes.at(-1).months[0].sunlight, 55);
assert.ok(!("id" in writes.at(-1)));
assert.equal(writes.at(-1).months.length, 12);
assert.equal(closed, 2);
assert.ok(state.calls.some(call => call.command === "send_window_action" && call.args.action === "session:reload"));
assert.deepEqual(preset, original);
await act(async () => { await emit("session-state", { session: { play_mode: PLAY_MODE_NORMAL } }); });
assert.ok(button("climateSettings.apply").disabled, "normal mode cannot apply climate changes");
assert.ok(input(".climate-settings-name").disabled);
await act(async () => { root.unmount(); });
container.remove();
console.log("PASS: climate preset copies, probability conservation, dynamic weather fields, draft/cancel/apply, chart editing, save errors and scenario mode restriction");
