import assert from "node:assert/strict";
import { act } from "react";
import { createRoot } from "react-dom/client";
import { PathRebuildPanel } from "../../src/components/path/PathRebuildPanel";
import { PathOperationPanel } from "../../src/components/path/PathOperationPanel";
import { PATH_REBUILD_CHANGED_EVENT } from "../../src/components/path/pathRebuildApi";
import { PATH_STREET, PATH_TRACK, PATH_TRANSPORT, STATUS_ACTIVE, STATUS_PENDING } from "../../src/constants";
import type { MasterData, PathData } from "../../src/types";
import { master } from "./mock-master";
import { state } from "../scenarios/mock-runtime";

master.data = { paths: {
  201: { type: PATH_STREET, display_name: "Current road" },
  202: { type: PATH_STREET, display_name: "Wider road" },
  203: { type: PATH_STREET, display_name: "Narrow road" },
  601: { type: PATH_TRACK, display_name: "Railway" },
} } as unknown as MasterData;
const path = { path_id: 7, path_variant: 201, path_type: PATH_STREET, status: STATUS_ACTIVE,
  under_maintenance: true } as PathData;
const quote = (variant: number) => ({ path_id: 7, current_variant: 201, rebuild_variant: variant,
  estimated_construction_cost: variant * 100, estimated_duration_days: 12,
  cost_per_simulation_days: 300, affordable: true, required_total: variant * 100,
  available_funds: 100000, removable_parcel_ids: [], shavable_parcel_ids: [] });
let preview = async (variant: number) => Response.json(quote(variant));
let failCommit = false;
const commits: unknown[] = [];
const signals: AbortSignal[] = [];
globalThis.fetch = async (input, init) => {
  if (init?.method !== "POST") return Response.json(path);
  const body = JSON.parse(String(init.body));
  if (body.dry_run) {
    signals.push(init.signal!);
    return preview(body.path_variant);
  }
  commits.push(body);
  return failCommit ? Response.json({ code: "REJECTED", message: "Conditions changed" }, { status: 400 })
    : Response.json(quote(body.path_variant));
};
const container = document.createElement("div");
document.body.append(container);
const root = createRoot(container);
let closed = 0;
const render = async (key: number) => {
  await act(async () => root.render(<PathRebuildPanel key={key} apiBaseUrl="http://test/" initialEntityId="7" onClose={() => { closed++; }} />));
};
const confirm = () => container.querySelector<HTMLButtonElement>('[data-guide="path.rebuild.confirm"]')!;
const select = async (variant: number) => {
  await act(async () => {
    const input = container.querySelector<HTMLSelectElement>("select")!;
    input.value = String(variant);
    input.dispatchEvent(new window.Event("change", { bubbles: true }));
  });
};
await render(1);
assert.ok(confirm().disabled);
assert.deepEqual([...container.querySelectorAll("option")].map(option => option.value), ["", "202", "203"]);
assert.ok(container.textContent?.includes("Current road"));
assert.ok(container.textContent?.includes("path.rebuild.repairNotice"));
await select(202);
assert.equal(confirm().disabled, false);
assert.ok(container.textContent?.includes("Wider road"));
assert.ok(container.querySelector('.money[title="20,200¢"]'));
assert.equal(commits.length, 0, "preview must not place an order");
console.log("PASS: variants stay within the current type; preview shows costs without changing the city");

let resolveOld!: (response: Response) => void;
let resolveLatest!: (response: Response) => void;
preview = variant => new Promise(resolve => { if (variant === 203) resolveOld = resolve; else resolveLatest = resolve; });
await select(203);
assert.ok(confirm().disabled);
await select(202);
assert.ok(signals.at(-2)?.aborted, "switching candidates aborts the previous preview");
await act(async () => { resolveOld(Response.json(quote(203))); });
assert.ok(confirm().disabled, "a stale result must not enable confirmation");
await act(async () => { resolveLatest(Response.json(quote(202))); });
assert.equal(confirm().disabled, false);
console.log("PASS: an old preview cannot enable confirmation for a new candidate");

preview = async variant => Response.json({ ...quote(variant), affordable: false, available_funds: 1 });
await select(203);
assert.ok(confirm().disabled);
assert.ok(container.textContent?.includes("construction.insufficientFunds"));
preview = async () => Response.json({ code: "COLLISION", message: "Path overlaps a building" }, { status: 400 });
await select(202);
assert.ok(confirm().disabled);
assert.ok(container.querySelector('[role="alert"]')?.textContent?.includes("Path overlaps a building"));
assert.equal(commits.length, 0);
console.log("PASS: funding and geometry rejection block confirmation and show the reason");

preview = async variant => Response.json(quote(variant));
await select(203);
failCommit = true;
await act(async () => confirm().click());
assert.equal(closed, 0);
assert.ok(container.querySelector('[role="alert"]')?.textContent?.includes("Conditions changed"));
assert.equal(state.emitted.length, 0, "a rejected order must not notify or close");
failCommit = false;
await act(async () => { confirm().click(); confirm().click(); });
assert.deepEqual(commits, [
  { path_variant: 203, dry_run: false }, { path_variant: 203, dry_run: false },
]);
assert.equal(closed, 1);
assert.ok(state.emitted.includes(PATH_REBUILD_CHANGED_EVENT));
assert.ok(state.calls.some(call => call.args?.action === "session:reload"));
assert.equal(state.calls.some(call => call.args?.action === "world:change_state"), false);
console.log("PASS: failed orders stay open; accepted orders refresh the views once without entering construction mode");

await render(2);
await select(202);
state.errors.send_window_action = "IPC disconnected";
await act(async () => confirm().click());
assert.equal(closed, 1);
assert.equal(confirm(), null, "an accepted order cannot be submitted again if refresh fails");
assert.ok(container.querySelector('[role="alert"]')?.textContent?.includes("path.rebuild.refreshFailed"));
delete state.errors.send_window_action;
await act(async () => root.unmount());
console.log("PASS: refresh failure never repeats an accepted order");

const operationRoot = createRoot(container);
const operations = async (data: PathData) => {
  await act(async () => operationRoot.render(<PathOperationPanel path={data} apiBaseUrl="http://test"
    onActionPerformed={() => {}} onStartTopologyEdit={() => {}} topologyEditMode={null} />));
  return container.querySelector<HTMLButtonElement>('[data-guide="path.rebuild.open"]');
};
const open = await operations(path);
assert.ok(open);
await act(async () => open.click());
assert.deepEqual(state.calls.at(-1), { command: "open_registered_window", args: {
  windowType: "path_rebuild", entityId: "7",
} });
assert.equal(await operations({ ...path, status: STATUS_PENDING }), null);
assert.equal(await operations({ ...path, path_type: PATH_TRANSPORT }), null);
await act(async () => operationRoot.unmount());
console.log("PASS: only active physical paths expose the conversion window with the correct entity");
