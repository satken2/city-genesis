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
{
  const platforms = DEFINITIONS.mining.steps.find(s => s.id === "mining.platforms")!;
  const mining = fake();
  mining.facts.windows.panel = { visible: true, window: "path_build" };
  const phase = async () => platforms.phases[await findPhase(platforms, mining)];
  check([(await phase()).textKey, (await phase()).ui.policy], [platforms.phases[0].textKey, "strict"], "opening the panel keeps the card guide and locks other cards");
  mining.facts.pathVariant = 464;
  check((await phase()).id, "loading", "choosing the platform card moves on to placement");
  mining.facts.pathVariant = -1;
  check((await phase()).ui.policy, "strict", "going back to the cards restores the lock");
}
env.bindings["support.housing.building"] = { kind: "agents", id: 30 };
env.facts.windows.detail = { visible: true, window: "agent_detail", entity: 31 };
const house = { type: "window" as const, window: "agent_detail", target: "support.housing.building" };
check(await evaluate(house, env), false, "wrong house cannot pass");
env.facts.windows.detail.entity = 30;
check(await evaluate(house, env), true, "exact house detail passes");
env.facts.windows.detail.visible = false;
check(await evaluate(house, env), false, "hidden detail cannot pass");
check(entityId({ agent_id: 30, parcel_id: 4, zone_id: 2 }, "buildings"), 30, "entity ID is resource-specific");
env.read = async (kind, id) => kind === "buildings" ? id ? [{ agent_id: 30, parcel_id: 4, variant: 1 }] : [{ agent_id: 30, variant: 1 }] : [];
check(await evaluate({ type: "entity", resource: "buildings", where: [{ field: "parcel_id", equals: 4 }], bind: "found" }, env), true, "missing list fields are read from detail");
check(env.bindings.found.id, 30, "detail condition binds correct ID");
env.bindings.original = { kind: "zones", id: 2 };
env.read = async () => [{ path_id: 5, zone_id: 2 }];
const split = { type: "entity" as const, resource: "paths" as const, where: [{ field: "zone_id", greaterThan: 0 }, { field: "zone_id", notEquals: { $ref: "original" } }] };
check(await evaluate(split, env), false, "notEquals rejects the bound value");
env.read = async () => [{ path_id: 5, zone_id: 7 }];
check(await evaluate(split, env), true, "notEquals accepts another value");
env.read = async (_kind, id) => id ? [{ path_id: 5 }] : [{ path_id: 5 }];
check(await evaluate(split, env), false, "notEquals does not accept a missing field");
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
  (d: any) => d.steps[0].phases[0].ui.highlight.push("hud.road:1"),
  (d: any) => d.steps[0].phases[0].ui.highlight.push("path.variant:street"),
  (d: any) => d.steps[0].phases[0].ui.highlight.push("path.variant:"),
  (d: any) => d.steps[0].phases[0].world.path.variant = { $constant: "TYPO" },
  (d: any) => d.steps[0].complete.target = "typo",
  (d: any) => d.steps[0].phases[0].world.path.points[1] = d.steps[0].phases[0].world.path.points[0],
  (d: any) => d.steps[0].phases[0].ui.allow.push("window.path_build"),
  (d: any) => d.steps.push(d.steps[0]),
  (d: any) => d.steps[0].phases[0].world.camera = { target: [0, 0, 0], distance: 300, yaw: 0, pitch: 0 },
  (d: any) => d.steps[0].phases[0].world.camera = { target: [0, 0], distance: 300, yaw: 0, pitch: 45 },
  (d: any) => d.terrainMap = "flat",
  (d: any) => d.id = "",
]) { const d = structuredClone(DEFINITIONS.basic); mutate(d); throws(() => validateDefinition(d), "invalid definition rejected"); }
{ const d = structuredClone(DEFINITIONS.basic); d.steps[0].phases[0].world.path!.points[0][1] = 10; validateDefinition(d); checks++; }
{
  // 高架の道路は、始終点の高さ(地面からの相対Y)を定義に書いて敷く。
  const highway = DEFINITIONS.highway.steps[0], road = fake();
  const phaseId = async () => highway.phases[await findPhase(highway, road)].id;
  check(await phaseId(), "open", "highway opens the road panel first");
  road.facts.windows.road = { visible: true, window: "path_build" };
  check(await phaseId(), "variant", "highway selects the motorway next");
  road.facts.pathVariant = 100;
  check(await phaseId(), "start", "highway places the start");
  road.facts.pathShape = { phase: "placing", selected_points: [[0, 7, -150]] };
  check(await phaseId(), "end", "highway places the end");
  road.facts.pathShape = { phase: "placing", selected_points: [[0, 7, -150], [0, 7, 150]] };
  check(await phaseId(), "finalize", "highway finalizes the shape by right-click");
  road.facts.pathShape = { phase: "editing", selected_points: [[0, 7, -150], [0, 7, 150]] };
  check(await phaseId(), "confirm", "highway confirms in the panel");
  delete road.facts.pathShape; road.bindings["highway.road"] = { kind: "paths", id: 9 }; road.read = async () => [{ checks: { crossing: true } }];
  check(await phaseId(), "confirm", "saved crossing survives the reset editor");
  const place = highway.phases.find(p => p.id === "start")!;
  const guided: Snapshot = { ...state, scenario: "highway", stepId: highway.id, phaseId: "start", ui: place.ui, world: place.world };
  const line = { path_variant: 100, curved: false, start_point: [0, 7, -150], end_point: [0, 7, 150] };
  check(allowsPathBody({ paths: [line] }, guided), true, "elevated path accepted at the guided height");
  for (const bad of [{ start_point: [0, 0, -150] }, { end_point: [0, 6, 150] }, { embankment_heights: [0, 1] }]) check(allowsPathBody({ paths: [{ ...line, ...bad }] }, guided), false, `reject ${JSON.stringify(bad)}`);
}
{ const d = structuredClone(DEFINITIONS.scenario3); d.steps[0].phases[0].until = { type: "window", window: "agent_detail", target: "lesson.house" }; validateDefinition(d); checks++; }
{ const d = structuredClone(DEFINITIONS.scenario3); d.steps[0].phases[0].until = { type: "window", window: "agent_detail", target: "house" }; throws(() => validateDefinition(d), "only core targets may be referenced without a local binding"); }
{
  // 基本編の区画・建物は、アイコンでパネルを開く段階のあと、パネル内の必要な操作だけを許す段階へ進む。
  for (const [id, window, panel, allowed] of [["basic.parcels", "parcel_creation", "create", ["parcel.mode:along_path", "parcel.confirm"]],
    ["basic.home", "building_construction", "select", ["building.category:1", "building.variant:2001", "building.confirm"]]] as const) {
    const step = DEFINITIONS.basic.steps.find(s => s.id === id)!, shown = fake();
    const phase = async () => step.phases[await findPhase(step, shown)];
    check([(await phase()).id, (await phase()).ui.policy], ["open", "strict"], `${id}: the icon opens the panel first`);
    shown.facts.windows.panel = { visible: true, window };
    check([(await phase()).id, (await phase()).ui.policy], [panel, "strict"], `${id}: the open panel offers only the guided controls`);
    for (const target of allowed) check((await phase()).ui.highlight.includes(target) && (await phase()).ui.allow.includes(target), true, `${id}: ${target} is highlighted and allowed`);
    shown.facts.windows.panel.visible = false;
    check((await phase()).id, "open", `${id}: closing the panel rewinds`);
  }
  // 地区編の step1: 地区編集モードに入るまでは全景でボタンを案内し、入ったらマーカーの道路へカメラが寄る。
  const zoning = DEFINITIONS.scenario2.steps[0], hud = fake();
  const now = async () => zoning.phases[await findPhase(zoning, hud)];
  check((await now()).id, "enter", "district guide starts before the edit mode");
  hud.facts.hudMode = "bulldozer";
  check((await now()).id, "enter", "another HUD mode does not count");
  hud.facts.hudMode = "district";
  check([(await now()).id, (await now()).world.markers], ["create", [[60, 0, 120]]], "district edit mode moves on and shows the road marker");
  check((await now()).world.camera!.distance < zoning.phases[0].world.camera!.distance, true, "the camera approaches the marker");
  delete hud.facts.hudMode;
  check((await now()).id, "enter", "leaving the edit mode rewinds");
  check(await evaluate({ type: "hudMode", mode: "district" }, hud), false, "hudMode is false in the normal state");
  for (const target of ["parcel.mode:along_path", "parcel.confirm", "building.back", "building.cancel", "building.confirm"]) {
    const d = structuredClone(DEFINITIONS.basic); d.steps[0].phases[0].ui.highlight.push(target); validateDefinition(d); checks++;
  }
  for (const target of ["parcel.mode:1", "parcel.mode:Along", "parcel.mode:", "path.variant:along_path", "parcel.confirm:1"]) {
    const d = structuredClone(DEFINITIONS.basic); d.steps[0].phases[0].ui.highlight.push(target); throws(() => validateDefinition(d), `${target} is rejected`);
  }
}
{
  // 建物を建て終えてパネルが閉じても、案内は「パネルを開く段階」へ巻き戻らず最終段階のままにする。
  const home = DEFINITIONS.basic.steps.find(s => s.id === "basic.home")!, built = fake();
  built.constants.VARIANT_APARTMENT = 2001;
  built.bindings["support.housing.building"] = { kind: "agents", id: 30 };
  check(await findPhase(home, built), 0, "no building yet: the panel has to be opened first");
  built.read = async () => [{ agent_id: 31, variant: 2001, status: 3 }];
  check(await findPhase(home, built), 1, "a placed building keeps the last phase after the panel closes");
  built.bindings = {};
  check(await findPhase(home, built), 0, "a condition that cannot be judged yet counts as not achieved");
}
{
  // 建物・道路・公共交通の種別を選ばせるステップは、どの段階も strict にして、案内した操作以外を押せなくする。
  const selecting = /^(building.(category|variant)|path.(type|variant)|transit.(new|type))(:|$)/;
  const covers = (allow: string[], target: string) => allow.includes(target) || allow.includes(target.split(":")[0]);
  let selectingSteps = 0;
  for (const [name, definition] of Object.entries(DEFINITIONS)) for (const step of definition.steps) {
    if (!step.phases.some(p => [...p.ui.allow, ...p.ui.highlight].some(t => selecting.test(t)))) continue;
    selectingSteps++;
    for (const phase of step.phases) {
      check([name, step.id, phase.id, phase.ui.policy], [name, step.id, phase.id, "strict"], "a selection step is strict in every phase");
      check(phase.ui.highlight.filter(t => !t.startsWith("hud.") && !covers(phase.ui.allow, t)), [], `${step.id}/${phase.id}: highlighted controls are allowed`);
    }
  }
  check(selectingSteps >= 33, true, "every building, path and transit selection step was covered");
}
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
const highlight = installUiGuide({ policy: "highlight", allow: [], highlight: ["hud.road"] }, "cockpit"); check(other.inert, false, "highlight allows other operations"); highlight();
const menu = installUiGuide({ policy: "highlight", allow: ["hud.other"], highlight: ["hud.road"] }, "icon-panel");
check([(document.getElementById("road") as HTMLElement).inert, other.inert, (document.getElementById("close") as HTMLElement).inert], [false, false, false], "icon panel keeps highlighted and allowed buttons");
menu();
const menuOnly = installUiGuide({ policy: "highlight", allow: [], highlight: ["hud.road"] }, "icon-panel");
check([(document.getElementById("road") as HTMLElement).inert, other.inert], [false, true], "icon panel disables buttons the step does not use");
menuOnly();
{
  document.body.innerHTML = '<button data-guide="path.variant" data-guide-value="221">Street</button><button data-guide="path.variant" data-guide-value="222" disabled>Lane</button>'
    + '<button data-guide="building.variant" data-guide-value="1054">Solar</button><button data-guide="building.variant" data-guide-value="1062">Substation</button>';
  const lit = () => [...document.querySelectorAll("[data-tutorial-highlight]")].map(el => el.textContent);
  const cards = () => [...document.querySelectorAll<HTMLElement>("[data-guide='building.variant']")].map(el => el.inert);
  const generic = installUiGuide({ policy: "highlight", allow: [], highlight: ["path.variant"] }, "path_build");
  check(lit(), ["Street"], "cards that cannot be pressed are not highlighted");
  document.querySelector<HTMLButtonElement>("[data-guide-value='222']")!.disabled = false; await new Promise(r => setTimeout(r, 0));
  check(lit(), ["Street", "Lane"], "a card enabled later is highlighted");
  generic();
  const exact = installUiGuide({ policy: "highlight", allow: [], highlight: ["building.variant:1054"] }, "building_construction");
  check(lit(), ["Solar"], "a valued target highlights only its own card"); exact();
  const allowOne = installUiGuide({ policy: "strict", allow: ["building.variant:1062"], highlight: [] }, "building_construction");
  check(cards(), [true, false], "a valued target allows only its own card"); allowOne();
  const allowAll = installUiGuide({ policy: "strict", allow: ["building.variant"], highlight: [] }, "building_construction");
  check(cards(), [false, false], "a plain target covers every card of the name"); allowAll();
}
{
  // 区画作成パネル: 「道路沿いに作成」の方法と確定だけが押せて、他の方法・入力は押せない。
  document.body.innerHTML = '<div data-guide="parcel.mode" data-guide-value="along_path"><img alt="">Along</div><div data-guide="parcel.mode" data-guide-value="random">Random</div>'
    + '<input type="radio"><button data-guide="parcel.clear">Clear</button><button data-guide="parcel.confirm">Confirm</button>';
  const inert = () => [...document.querySelectorAll<HTMLElement>("[data-guide], input")].map(el => el.inert);
  const guide = installUiGuide({ policy: "strict", allow: ["parcel.mode:along_path", "parcel.clear", "parcel.confirm"], highlight: ["parcel.mode:along_path", "parcel.confirm"] }, "parcel_creation");
  check(inert(), [false, true, true, false, false], "only the guided parcel controls can be pressed");
  check([...document.querySelectorAll("[data-tutorial-highlight]")].map(el => el.textContent), ["Along", "Confirm"], "the guided tile and confirm button are highlighted");
  guide();
}
{
  // 運賃・運行間隔の入力欄のように data-guide を持つ枠は、枠だけを強調し、中のボタンと入力欄も押せる。
  document.body.innerHTML = '<div data-guide="transit.fare"><button>-</button><input><button>+</button></div><div data-guide="transit.headway"><button>-</button><input></div><button data-guide="transit.confirm">OK</button>';
  const guide = installUiGuide({ policy: "strict", allow: ["transit.fare", "transit.confirm"], highlight: ["transit.fare", "transit.confirm"] }, "transit_creation");
  check([...document.querySelectorAll("[data-tutorial-highlight]")].map(el => el.tagName + ":" + (el.getAttribute("data-guide") ?? "")), ["DIV:transit.fare", "BUTTON:transit.confirm"], "only the frame is highlighted, not the controls inside it");
  check([...document.querySelectorAll<HTMLElement>("button, input")].map(el => el.inert), [false, false, false, true, true, false], "controls inside an allowed frame stay usable and the others are blocked");
  guide();
}
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
{
  const projects = fake();
  const status = { planned: false, land: false, parcel: false, hall: false, track: false, negotiating: false, blocked: false, demolition: false, connected: false };
  projects.read = async () => [{ checks: status }];
  const negotiate = DEFINITIONS.acquisition.steps[1];
  const demolition = DEFINITIONS.acquisition.steps[2];
  projects.bindings["lesson.fire_station.building"] = { kind: "agents", id: 42 };
  projects.facts.windows.detail = { visible: true, window: "agent_detail", entity: 43 };
  check(await findPhase(demolition, projects), 0, "demolition waits for the exact public facility details");
  projects.facts.windows.detail.entity = 42;
  check(await findPhase(demolition, projects), 1, "the public facility details unlock the demolition button guide");
  status.negotiating = true;
  check(await evaluate(negotiate.complete!, projects), false, "acquisition alone cannot skip observing the public obstruction");
  status.blocked = true;
  check(await evaluate(negotiate.complete!, projects), true, "both acquisition and public obstruction unlock demolition");
  check(await evaluate(DEFINITIONS.acquisition.steps[3].complete!, projects), false, "a planned track does not complete the lesson");
  status.connected = true;
  check(await evaluate(DEFINITIONS.acquisition.steps[3].complete!, projects), true, "completed platform connection passes");
  const actions: string[] = [];
  projects.scenarioAction = async (scenario, action) => { actions.push(`${scenario}.${action}`); };
  const land = DEFINITIONS.reclamation.steps[1];
  await execute(land.beforeCheck!, projects);
  check(actions, ["reclamation.commission"], "reclamation commissions through the core action before rechecking land");
  check(await evaluate(land.complete!, projects), false, "an unfinished reclamation remains incomplete after a refused early action");
  status.land = true;
  check(await evaluate(land.complete!, projects), true, "finished reclaimed land passes");
}
{
  const page = fake();
  check(await evaluate({ type: "page", id: "x" }, page), false, "a page is unsent at first");
  page.facts.pages = { x: true };
  check(await evaluate({ type: "page", id: "x" }, page), true, "a sent page passes");
  check(await evaluate({ type: "page", id: "y" }, page), false, "other pages stay unsent");
  const cockpit = DEFINITIONS.controls.steps.find(s => s.id === "controls.cockpit")!, view = fake();
  view.facts.pages = {};
  check(await findPhase(cockpit, view), 0, "the cockpit tour starts at the first page");
  view.facts.pages["cockpit.tools"] = true;
  check(await findPhase(cockpit, view), 1, "a sent page moves to the next phase");
  view.facts.pages["cockpit.services"] = true; view.facts.pages["cockpit.status"] = true;
  check(await findPhase(cockpit, view), cockpit.phases.length - 1, "all pages sent stops at the last phase");
  delete view.facts.pages["cockpit.services"];
  check(await findPhase(cockpit, view), 1, "an unsent page holds the tour back");
  const display = DEFINITIONS.controls.steps.find(s => s.id === "controls.display")!, panel = fake();
  panel.facts.pages = { "display.tabs": true };
  check(await findPhase(display, panel), 0, "the display tour waits for the panel to open");
  panel.facts.windows.display = { window: "display_options", visible: true };
  check(await findPhase(display, panel), 2, "the open panel with its page sent reaches the last phase");
  const icons = DEFINITIONS.controls.steps.find(s => s.id === "controls.icons")!;
  check(icons.phases.map(p => p.ui.highlight.length), icons.phases.map(() => 1), "each icon page highlights exactly one icon");
  check(new Set(icons.phases.map(p => p.ui.highlight[0])).size, icons.phases.length, "icon pages highlight distinct icons");
  check(DEFINITIONS.controls.steps.every(s => s.phases.every(p => p.ui.policy === "highlight" && p.world.mode === "observe")), true, "the controls tutorial only observes");
  for (const mutate of [
    (d: any) => { d.steps[0].phases[d.steps[0].phases.length - 1].until = { type: "page", id: "last" }; },
    (d: any) => { d.steps[0].phases[1].until = d.steps[0].phases[0].until; },
    (d: any) => { d.steps[0].complete = { type: "page", id: "done" }; },
    (d: any) => { d.steps[0].phases[0].until = { type: "all", conditions: [{ type: "page", id: "a" }, { type: "ack" }] }; },
    (d: any) => { d.steps[0].phases[0].until = { type: "page" }; },
  ]) { const d = structuredClone(DEFINITIONS.controls); mutate(d); throws(() => validateDefinition(d), "invalid page usage rejected"); }
}
console.log(`PASS: ${checks} tutorial checks`);
