import assert from "node:assert/strict";
import { act } from "react";
import { createRoot } from "react-dom/client";
import { ZoneDetailPanel } from "../../src/components/zone/ZoneDetailPanel";
import { ROAD_COLOR_PALETTE, PLAY_MODE_NORMAL } from "../../src/constants";
import { state } from "../scenarios/mock-runtime";

let zone = { zone_id: 7, name: "District", color: "#abcdef", auto_managed: true, area: 100,
  allowed_bd_mask: 0, limit_coverage_ratio: 0.6, limit_volume_ratio: 2, limit_height: 30 };
const updates: Record<string, unknown>[] = [];
let failSave = false;
globalThis.fetch = async (input, init) => {
  if (String(input).endsWith("/sessions")) return Response.json({ play_mode: PLAY_MODE_NORMAL });
  if (init?.method === "PATCH") {
    if (failSave) return Response.json({ message: "failed" }, { status: 500 });
    const changes = JSON.parse(String(init.body));
    updates.push(changes);
    zone = { ...zone, ...changes, auto_managed: changes.auto_managed ?? false };
  }
  return Response.json({ zone });
};
const container = document.createElement("div");
document.body.append(container);
const root = createRoot(container);
await act(async () => { root.render(<ZoneDetailPanel apiBaseUrl="http://test" pathTemplate="/api/v1/zones/{id}" initialEntityId="7" />); });
const button = (label: string) => {
  const found = [...container.querySelectorAll<HTMLButtonElement>("button")].find(b => b.textContent === label);
  assert.ok(found, label);
  return found;
};
const notifications = () => state.calls.filter(c => c.command === "send_window_action");
const checkNotification = (count: number) => {
  assert.equal(notifications().length, count);
  assert.deepEqual(notifications().at(-1)?.args, {
    windowType: "", entity: null, action: "zone:updated", params: { zone_id: 7 },
  });
};
const changeInput = async (input: HTMLInputElement, value: string) => {
  await act(async () => {
    Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, "value")!.set!.call(input, value);
    input.dispatchEvent(new window.Event("input", { bubbles: true }));
  });
};

// 保存された色を使い、同じ色の選択では再送しない。
await act(async () => { container.querySelector<HTMLButtonElement>(".color-swatch-picker__swatch")!.click(); });
assert.equal(zone.color, ROAD_COLOR_PALETTE[0].toLowerCase());
checkNotification(1);
await act(async () => { container.querySelector<HTMLButtonElement>(".color-swatch-picker__swatch")!.click(); });
checkNotification(1);

await act(async () => { container.querySelector<HTMLButtonElement>(".editable-label__icon-btn")!.click(); });
await changeInput(container.querySelector<HTMLInputElement>(".editable-label__input")!, "Renamed");
await act(async () => { button("common.save").click(); });
assert.equal(zone.name, "Renamed");
checkNotification(2);

await changeInput(container.querySelector<HTMLInputElement>(".restriction-input")!, "45");
await act(async () => { button("common.save").click(); });
assert.equal(updates.at(-1)?.limit_coverage_ratio, 0.45);
checkNotification(3);

await act(async () => { container.querySelector<HTMLButtonElement>(".zone-mgmt-toggle-btn")!.click(); });
assert.equal(zone.auto_managed, true);
checkNotification(4);

// 失敗した保存では表示済みの色とUnityのデータを変更しない。
failSave = true;
const savedColor = zone.color;
await act(async () => { container.querySelectorAll<HTMLButtonElement>(".color-swatch-picker__swatch")[1].click(); });
assert.equal(zone.color, savedColor);
assert.ok(container.textContent?.includes("zone.colorUpdateFailed"));
checkNotification(4);
await act(async () => { root.unmount(); });
console.log("PASS: district color, name, restrictions and management saves notify Unity; failed/unchanged saves do not");
