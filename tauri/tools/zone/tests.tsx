import assert from "node:assert/strict";
import { act } from "react";
import { createRoot } from "react-dom/client";
import { ZoneDetailPanel } from "../../src/components/zone/ZoneDetailPanel";
import { ZoneMergeConfirmWindow } from "../../src/components/zone/ZoneMergeConfirmWindow";
import { ZoneEditWindow } from "../../src/components/zone/ZoneEditWindow";
import { ROAD_COLOR_PALETTE, PLAY_MODE_NORMAL } from "../../src/constants";
import { state } from "../scenarios/mock-runtime";
import { buildFrontages, faceKey, findZoneAt, type Ring } from "../../src/components/zone/zoneBrushTargets";
import { PATH_SIDE_LEFT, PATH_SIDE_RIGHT, PATH_STREET } from "../../src/constants";
import { mapProps } from "./mock-map";

const outlines = new Map([
  [10, { polygon: [] as Ring, area: 0 }],
  [20, { polygon: [[0, -100], [100, -100], [100, 100], [0, 100]] as Ring, area: 20000 }],
]);
const frontages = buildFrontages([{ pathId: 1, startNodeId: 1, endNodeId: 2,
  centerline: [[0, 0], [100, 0]], width: 8 }], []);
const faceZones = new Map([[faceKey(1, PATH_SIDE_LEFT), 10], [faceKey(1, PATH_SIDE_RIGHT), 20]]);
assert.equal(findZoneAt(50, 2, outlines, frontages, faceZones), 10);
assert.equal(findZoneAt(50, -2, outlines, frontages, faceZones), 20);
assert.equal(findZoneAt(50, 80, outlines, frontages, faceZones), 20);
assert.equal(findZoneAt(150, 80, outlines, frontages, faceZones), null);
console.log("PASS: district expansion selects empty districts and the correct road side");

// 輪郭のない地区を除外せず、静的描画・拡張先の強調・飛び地の除去を実際のコールバックで検査する。
const paints: { style: string; points: number[][] }[] = [];
const contexts = new WeakMap<HTMLCanvasElement, CanvasRenderingContext2D>();
const originalGetContext = window.HTMLCanvasElement.prototype.getContext;
window.HTMLCanvasElement.prototype.getContext = function () {
  if (!contexts.has(this)) {
    let points: number[][] = [];
    const context = {
      canvas: this, fillStyle: "", strokeStyle: "",
      beginPath: () => { points = []; },
      moveTo: (x: number, z: number) => { points.push([x, z]); },
      lineTo: (x: number, z: number) => { points.push([x, z]); },
      fill() { if (points.length) paints.push({ style: this.fillStyle, points: [...points] }); },
      stroke() { if (points.length) paints.push({ style: this.strokeStyle, points: [...points] }); },
      closePath() {}, setTransform() {}, getTransform: () => ({}), clearRect() {}, drawImage() {},
      save() {}, restore() {}, arc() {},
    };
    contexts.set(this, context as unknown as CanvasRenderingContext2D);
  }
  return contexts.get(this)!;
} as typeof window.HTMLCanvasElement.prototype.getContext;

globalThis.fetch = async () => Response.json({ map_width: 1000, map_height: 1000 });
const editContainer = document.createElement("div");
document.body.append(editContainer);
const editRoot = createRoot(editContainer);
await act(async () => { editRoot.render(<ZoneEditWindow apiBaseUrl="http://test" />); });
const view = { offsetX: 0, offsetY: 0, zoom: 1 };
const canvas = document.createElement("canvas");
const ctx = canvas.getContext("2d")!;
const outer = { zone_id: 20, name: "Outer", color: "#abcdef", polygon: outlines.get(20)!.polygon };
for (const polygon of [null, undefined, [], [[0, 0]], [[0, 0], [1, 1]]] as const) {
  await act(async () => {
    mapProps.onVectorsLoaded!({
      paths: [{ path_id: 1, path_type: PATH_STREET, width: 8,
        start_point: [0, 0, 0], end_point: [100, 0, 0], left_zone_id: 10, right_zone_id: 20 }],
      parcels: [], buildings: [],
      zones: [outer, { zone_id: 10, name: "Empty", color: "#123456", polygon: polygon as Ring | null | undefined }],
    });
  });
  paints.length = 0;
  mapProps.overlayDraw!(ctx, view);
  assert.equal(paints.length, 1);
  assert.equal(paints[0].style, outer.color);

  await act(async () => { editContainer.querySelector<HTMLButtonElement>('[data-guide-value="expand"]')!.click(); });
  await act(async () => { mapProps.onCursorWorldPos!({ x: 50, z: 2 }); });
  assert.ok(editContainer.querySelector(".zone-edit__target")!.textContent?.includes("Empty"));
  paints.length = 0;
  mapProps.animatedOverlayDraw!(ctx, view, 0);
  assert.equal(paints.length, 0, "a district without a polygon must not paint a previous path");

  await act(async () => { mapProps.onCursorWorldPos!({ x: 50, z: -2 }); });
  paints.length = 0;
  mapProps.animatedOverlayDraw!(ctx, view, 0);
  assert.equal(paints.length, 2, "the valid target retains its fill and border");
  assert.ok(paints.every(paint => paint.style === outer.color));
}
await act(async () => { editRoot.unmount(); });
window.HTMLCanvasElement.prototype.getContext = originalGetContext;
console.log("PASS: district editing renders only polygon outlines and still selects empty districts by road side");

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
    zone = { ...zone, ...changes };
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

assert.equal(container.querySelector(".zone-mgmt-toggle-btn"), null);
assert.equal(container.querySelector(".zone-mgmt-badge"), null);
assert.ok(updates.every(changes => !("auto_managed" in changes)));

// 失敗した保存では表示済みの色とUnityのデータを変更しない。
failSave = true;
const savedColor = zone.color;
await act(async () => { container.querySelectorAll<HTMLButtonElement>(".color-swatch-picker__swatch")[1].click(); });
assert.equal(zone.color, savedColor);
assert.ok(container.textContent?.includes("zone.colorUpdateFailed"));
checkNotification(3);
await act(async () => { root.unmount(); });
console.log("PASS: district color, name and restrictions notify Unity; legacy management state is absent; failed/unchanged saves do not");

globalThis.fetch = async () => Response.json({ code: "ZONE_SIZE_ERROR", message: "Too large" }, { status: 400 });
const mergeContainer = document.createElement("div");
document.body.append(mergeContainer);
const mergeRoot = createRoot(mergeContainer);
let closed = false;
const beforeMerge = state.calls.length;
await act(async () => {
  mergeRoot.render(<ZoneMergeConfirmWindow apiBaseUrl="http://test" pendingMerge={{
    targetZoneId: 7, targetZoneName: "Target", sourceZoneId: 8, sourceZoneName: "Source",
  }} onClose={() => { closed = true; }} />);
});
await act(async () => { mergeContainer.querySelector<HTMLButtonElement>(".zone-merge-confirm-btn")!.click(); });
assert.ok(mergeContainer.textContent?.includes("errors.ZONE_SIZE_ERROR"));
assert.equal(closed, false);
assert.equal(state.calls.length, beforeMerge);
await act(async () => { mergeRoot.unmount(); });
console.log("PASS: oversized merge shows the size error without closing or reloading Unity");

await import("./road-brush-tests");
