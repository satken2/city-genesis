import assert from "node:assert/strict";
import { act } from "react";
import { createRoot } from "react-dom/client";
import { PropPlacementPanel } from "../../src/components/prop/PropPlacementPanel";
import { emit, state } from "./mock-runtime";

const container = document.createElement("div");
document.body.append(container);
let root = createRoot(container);
let closed = 0;
let failSave = false;
const requests: { url: string; body: any }[] = [];
globalThis.fetch = async (input, init) => {
  requests.push({ url: String(input), body: JSON.parse(String(init?.body)) });
  return failSave ? Response.json({ message: "failed" }, { status: 500 }) : Response.json({ instances: [] });
};
const mount = async () => {
  await act(async () => { root.render(<PropPlacementPanel apiBaseUrl="http://test" onClose={() => closed++} />); });
};
const button = (text: string) => {
  const found = [...container.querySelectorAll<HTMLButtonElement>("button")].find(b => b.textContent === text);
  assert.ok(found, text);
  return found;
};
const click = async (element: HTMLElement) => { await act(async () => { element.click(); }); };
const select = async (name: string) => { await click(container.querySelector<HTMLButtonElement>(`button[title="${name}"]`)!); };
const update = async (instances: unknown[]) => {
  await act(async () => { await emit("edit_prop:update_instances", { instances }); });
};
const actions = () => state.calls.filter(c => c.command === "send_window_action").map(c => c.args);
const tree = { prop_type: 1, x: 10, y: 5.5, z: 20, rotation: 15, scale: 1 };
const rock = { prop_type: 2, x: 30, y: -2.25, z: 40, rotation: 45, scale: 2 };

await mount();
await select("Tree");
await update([tree]);
await select("Rock");
assert.equal(button("prop_placement.confirm").disabled, false, "type selection discarded pending placement");
assert.equal(actions().at(-1).params.prop_type, 2);
await update([tree, rock]);
await select("Tree");
assert.equal(button("prop_placement.confirm").disabled, false);

// 失敗時は混在した仮配置を保ち、再試行で同じ1リクエストを送る。
failSave = true;
await click(button("prop_placement.confirm"));
assert.equal(closed, 0);
assert.equal(actions().some(a => a.action === "edit_prop:finish"), false);
assert.deepEqual(requests[0].body, { instances: [tree, rock] });
failSave = false;
await click(button("prop_placement.confirm"));
assert.equal(closed, 1);
assert.equal(requests.length, 2);
assert.deepEqual(requests[1], { url: "http://test/api/v1/props/instances", body: { instances: [tree, rock] } });
assert.ok(actions().some(a => a.action === "edit_prop:finish"));

await act(async () => { root.unmount(); });
root = createRoot(container);
await mount();
await select("Tree");
await update([tree, rock]);
await click(button("prop_placement.reset"));
assert.equal(button("prop_placement.confirm").disabled, true);
assert.equal(actions().at(-1).action, "edit_prop:reset");

await update([tree]);
await click(button("prop_placement.modeScatter"));
assert.equal(button("prop_placement.confirm").disabled, true);
await act(async () => { await emit("edit_prop:update_shape", { polygon: [[0, 0], [10, 0], [0, 10]], finalized: true }); });
assert.equal(button("prop_placement.confirm").disabled, false);
await select("Rock");
assert.equal(button("prop_placement.confirm").disabled, true, "scatter type change retained old polygon");
await click(button("prop_placement.modeInstance"));
assert.equal(button("prop_placement.confirm").disabled, true);
await update([rock]);
await click(button("prop_placement.cancel"));
assert.equal(container.querySelector(".pc-action-bar"), null);
assert.equal(actions().at(-1).params.state, "STATE_NORMAL");
await act(async () => { root.unmount(); });
console.log("PASS: mixed prop placement, save retry, reset, cancel and mode changes");
