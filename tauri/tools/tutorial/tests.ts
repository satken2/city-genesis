import assert from "node:assert/strict";
import { Window } from "happy-dom";
import { DEFINITIONS, validateDefinition } from "../../src/tutorial/definitions";
import { evaluate, execute, findPhase, entityId } from "../../src/tutorial/engine";
import { acceptSnapshot, allowsPathBody, snapshot } from "../../src/tutorial/bus";
import { installUiGuide } from "../../src/tutorial/dom";
import { createEnvironment } from "../../src/tutorial/environment";
import type { Action, Environment, Snapshot } from "../../src/tutorial/model";
import type { MasterData } from "../../src/types";
import { PATH_STREET } from "../../src/constants";
let checks = 0;
const check = (actual: unknown, expected: unknown, label: string) => { assert.deepEqual(actual, expected, label); checks++; };
const throws = (fn: () => void, label: string) => { assert.throws(fn, undefined, label); checks++; };
const rejects = async (fn: () => Promise<unknown>, label: string) => { await assert.rejects(fn, undefined, label); checks++; };
const fake = (): Environment => ({ bindings: {}, pendingCreates: {}, constants: { TUTORIAL_STREET: 221, STATUS_ACTIVE: 1, STATUS_CONSTRUCTING: 2, STATUS_SCHEDULED_CONSTRUCTION: 3 }, facts: { windows: {} }, read: async () => [], create: async () => 1, complete: async () => {}, reload: async () => {}, checkpoint: () => {}, assertCurrent: () => {} });
const basic = DEFINITIONS.basic.steps[0];
const env = fake();
check(await findPhase(basic, env), 0, "open road panel first");
env.facts.windows.road = { visible: true, window: "path_build" };
check(await findPhase(basic, env), 1, "select variant next");
env.facts.pathVariant = 221;
check(await findPhase(basic, env), 2, "place start");
env.facts.pathShape = { phase: "placing", selected_points: [[-100, 0, 0]] };
check(await findPhase(basic, env), 3, "place end");
env.facts.pathShape = { phase: "placing", selected_points: [[-100, 0, 0], [100, 0, 0]] };
check(await findPhase(basic, env), 4, "right-click confirmation follows left-click endpoint");
env.facts.pathShape = { phase: "editing", selected_points: [[-100, 0, 0], [100, 0, 0]] };
check(await findPhase(basic, env), 5, "panel confirmation follows finalized shape");
env.facts.windows.road.visible = false;
check(await findPhase(basic, env), 0, "closing panel rewinds");
env.facts.windows.road.visible = true; delete env.facts.pathShape;
check(await findPhase(basic, env), 2, "cancel rewinds placement");
env.bindings["basic.road"] = { kind: "paths", id: 9 };
env.read = async () => [{ path_id: 9, path_variant: 221, start_point: [-100, 0, 0], end_point: [100, 0, 0], curved: false, status: 1 }];
check(await findPhase(basic, env), 5, "saved road survives closed panel");
env.bindings["support.housing.building"] = { kind: "agents", id: 30 };
env.facts.windows.detail = { visible: true, window: "agent_detail", entity: 31 };
const house = DEFINITIONS.scenario3.steps[0].phases[0].until;
check(await evaluate(house, env), false, "wrong house cannot pass");
env.facts.windows.detail.entity = 30;
check(await evaluate(house, env), true, "exact house detail passes");
env.facts.windows.detail.visible = false;
check(await evaluate(house, env), false, "hidden detail cannot pass");
check(entityId({ agent_id: 30, parcel_id: 4, zone_id: 2 }, "buildings"), 30, "entity ID is resource-specific");
env.read = async (kind, id) => kind === "buildings" ? id ? [{ agent_id: 30, parcel_id: 4, variant: 1 }] : [{ agent_id: 30, variant: 1 }] : [];
check(await evaluate({ type: "entity", resource: "buildings", where: [{ field: "parcel_id", equals: 4 }], bind: "found" }, env), true, "missing list fields are read from detail");
check(env.bindings.found.id, 30, "detail condition binds correct ID");
env.read = async (kind, id) => kind === "agents" ? [{ infra_node_id: id }] : [{ start_node_id: 1, end_node_id: 2 }, { start_node_id: 3, end_node_id: 4 }];
env.bindings.a = { kind: "agents", id: 1 }; env.bindings.b = { kind: "agents", id: 4 };
const connected = { type: "connected" as const, from: "a", to: "b", terminal: "infra_node_id", where: [] };
check(await evaluate(connected, env), false, "separate incident edges are not a connected route");
env.read = async (kind, id) => kind === "agents" ? [{ infra_node_id: id }] : [{ start_node_id: 1, end_node_id: 2 }, { start_node_id: 2, end_node_id: 4 }];
check(await evaluate(connected, env), true, "connected route passes");
const setup = fake(); let creates = 0, completions = 0, failComplete = true;
setup.create = async () => ++creates; setup.read = async () => [{ path_id: 1 }];
setup.complete = async () => { completions++; if (failComplete) throw new Error("HTTP failure"); };
const actions: Action[] = [{ type: "create", resource: "paths", bind: "road", body: {} }, { type: "completeConstruction", target: "road" }];
await rejects(() => execute(actions, setup), "action failure propagates");
failComplete = false; await execute(actions, setup);
check(creates, 1, "retry does not duplicate completed creation"); check(completions, 2, "failed idempotent completion is retried");
const uncertain = fake(); uncertain.create = async () => { throw new Error("connection lost after POST"); };
await rejects(() => execute(actions, uncertain), "unknown POST outcome stops setup");
uncertain.create = async () => { throw new Error("must not POST again"); };
await rejects(() => execute(actions, uncertain), "unknown creation is not blindly retried");
check(uncertain.pendingCreates.road, true, "unknown creation is journaled");
const state: Snapshot = { active: true, runId: "test1", revision: 1, saveId: "A", scenario: "basic", stepId: basic.id, phaseId: "start", ui: basic.phases[2].ui, world: { ...basic.phases[2].world, path: { ...basic.phases[2].world.path!, variant: 221 } } };
const body = { paths: [{ path_variant: 221, curved: false, start_point: [-100, 0, 0], end_point: [100, 0, 0] }] };
check(allowsPathBody(body, state), true, "exact path accepted");
for (const bad of [{ path_variant: 201 }, { curved: true }, { end_point: [98, 0, 0] }, { end_point: [100, 1, 0] }, { waypoints: [[0, 0, 0]] }, { start_control: [0, 0, 0] }, { embankment_heights: [1, 0] }, { follow_terrain: false }]) check(allowsPathBody({ paths: [{ ...body.paths[0], ...bad }] }, state), false, `reject ${JSON.stringify(bad)}`);
check(allowsPathBody(body, { ...state, world: { mode: "blocked" } }), false, "blocked phase rejects submit");
check(allowsPathBody({ arbitrary: true }, null), true, "normal gameplay unrestricted");
acceptSnapshot(state); acceptSnapshot({ ...state, revision: 0, active: false });
check(snapshot()?.active, true, "old revision ignored");
acceptSnapshot({ ...state, runId: "test2" }); acceptSnapshot({ ...state, revision: 2, active: false });
check(snapshot()?.runId, "test2", "old run cleanup cannot clear new run");
acceptSnapshot({ ...state, runId: "test2", revision: 2, active: false }); acceptSnapshot({ ...state, runId: "test2", revision: 3 });
check(snapshot()?.active, false, "retired run cannot reactivate");
for (const mutate of [
  (d: any) => d.steps[0].phases[0].ui.allow.push("unknown"),
  (d: any) => d.steps[0].phases[0].world.path.variant = { $constant: "TYPO" },
  (d: any) => d.steps[0].complete.target = "typo",
  (d: any) => d.steps[0].phases[0].world.path.points[0][1] = 10,
  (d: any) => d.steps[0].phases[0].ui.allow.push("window.path_build"),
  (d: any) => d.steps.push(d.steps[0]),
  (d: any) => d.steps[0].phases[0].world.camera = { target: [0, 0, 0], distance: 300, yaw: 0, pitch: 0 },
  (d: any) => d.steps[0].phases[0].world.camera = { target: [0, 0], distance: 300, yaw: 0, pitch: 45 },
  (d: any) => delete d.terrainMap,
]) { const d = structuredClone(DEFINITIONS.basic); mutate(d); throws(() => validateDefinition(d), "invalid definition rejected"); }
{ const d = structuredClone(DEFINITIONS.scenario3); d.steps[0].phases[0].until = { type: "window", window: "agent_detail", target: "lesson.house" }; validateDefinition(d); checks++; }
{ const d = structuredClone(DEFINITIONS.scenario3); d.steps[0].phases[0].until = { type: "window", window: "agent_detail", target: "house" }; throws(() => validateDefinition(d), "only core targets may be referenced without a local binding"); }
const browser = new Window();
for (const key of ["document", "Element", "HTMLElement", "KeyboardEvent", "MutationObserver"]) Object.assign(globalThis, { [key]: (browser as any)[key] });
document.body.innerHTML = '<div class="titlebar"><button id="close">×</button></div><button id="road">Road</button><button id="other">Other</button><button id="existing" inert>Inert</button><button data-guide="path.confirm"><span>Confirm</span></button>';
let clicks = 0; document.body.addEventListener("click", () => clicks++);
const other = document.getElementById("other") as HTMLElement;
const cleanup = installUiGuide({ policy: "strict", allow: ["path.confirm"], highlight: ["path.confirm"] }, "path_build");
other.dispatchEvent(new browser.MouseEvent("click", { bubbles: true, cancelable: true }) as unknown as Event);
check(clicks, 0, "capture blocks unrelated click");
document.querySelector("span")!.dispatchEvent(new browser.MouseEvent("click", { bubbles: true }) as unknown as Event);
check(clicks, 1, "nested element of allowed button works");
check((document.getElementById("close") as HTMLElement).inert, false, "close remains available");
check(other.inert, true, "keyboard focus excluded via inert");
check(document.querySelector("[data-tutorial-highlight]")?.getAttribute("data-guide"), "path.confirm", "ordered anchor highlighted");
const appended = document.createElement("button"); document.body.append(appended); await new Promise(r => setTimeout(r, 0));
check(appended.inert, true, "late-mounted controls gated");
cleanup(); check(other.inert, false, "cleanup restores interaction"); check((document.getElementById("existing") as HTMLElement).inert, true, "existing inert preserved");
const related = installUiGuide({ policy: "related", allow: ["window.path_build"], highlight: [] }, "path_build"); check(other.inert, false, "related allows selected window"); related();
const highlight = installUiGuide({ policy: "highlight", allow: [], highlight: ["hud.road"] }, "icon-panel"); check(other.inert, false, "highlight allows other operations"); highlight();
for (const windowType of ["system-tray", "settings", "save_load", "save_list", "new_game", "credits", "confirm_dialog", "tutorial", "notification", "tips"]) {
  document.body.innerHTML = '<button id="settings">Settings</button><button id="save">Save</button><button id="reload">Reload</button><input><button disabled>Busy</button>';
  let handled = 0;
  const release = installUiGuide({ policy: "strict", allow: [], highlight: [] }, windowType);
  for (const button of document.querySelectorAll<HTMLButtonElement>("button:not(:disabled)")) {
    button.addEventListener("click", () => handled++);
    check(button.inert, false, `${windowType}: control stays focusable`);
    button.click();
  }
  check(handled, 3, `${windowType}: strict guide permits application controls`);
  check(document.querySelector<HTMLInputElement>("input")!.inert, false, `${windowType}: form fields stay usable`);
  check(document.querySelector<HTMLButtonElement>("button:disabled")!.disabled, true, `${windowType}: native busy state preserved`);
  release();
}
await browser.happyDOM.close();

// 実APIの道路応答から必要な項目を抜粋。curvedはJSONのbooleanではなく数値で届く。
const pathResponse = { path_id: 9, path_variant: 221, start_point: [-100, 0, 0], end_point: [100, 0, 0], curved: 0, status: 3 };
const originalFetch = globalThis.fetch;
try {
  globalThis.fetch = async input => Response.json(String(input).endsWith("/paths") ? { paths: [pathResponse] } : pathResponse);
  const master = { paths: { 221: { type: PATH_STREET, lanes: 2, height_editable: false, frontage_allowed: true } } } as unknown as MasterData;
  const live = await createEnvironment("http://fixture", "A", { "basic.road": { kind: "paths", id: 9 } }, {}, { windows: {} }, master, () => {}, () => {});
  check(await evaluate(basic.complete!, live), true, "numeric straight-road API response passes completion");
  check((await live.read("paths"))[0].curved, false, "path list uses the same boolean normalization");
  check(await findPhase(basic, live), 5, "saved API road survives cleared editor facts");
  pathResponse.curved = 1;
  check(await evaluate(basic.complete!, live), false, "curved road remains rejected");
  pathResponse.curved = 0;
  pathResponse.end_point = [99, 0, 0];
  check(await evaluate(basic.complete!, live), false, "wrong endpoint remains rejected after normalization");
} finally { globalThis.fetch = originalFetch; }

// 実HTTPでも同じアダプタを通す。専用のテスト都市を持つプロセスだけを指定する。
if (process.env.TUTORIAL_API_TEST_URL) {
  const base = process.env.TUTORIAL_API_TEST_URL;
  const master = { paths: (await (await fetch(`${base}/api/v1/master/paths`)).json()).paths, agents: (await (await fetch(`${base}/api/v1/master/agents`)).json()).agents } as MasterData;
  const live = await createEnvironment(base, "tutorial-qa-isolated", {}, {}, { windows: {} }, master, () => {}, () => {});
  live.reload = async () => {};
  check(typeof live.bindings["support.housing.building"]?.id, "number", "HTTP environment binds core-built targets");
  const wrongSession = await createEnvironment(base, "another-city", {}, {}, { windows: {} }, master, () => {}, () => {});
  await rejects(() => wrongSession.create("buildings", {}), "HTTP writes rejected for another save");
}
console.log(`PASS: ${checks} tutorial checks`);
