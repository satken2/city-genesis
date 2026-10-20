import assert from "node:assert/strict";
import { act } from "react";
import { createRoot } from "react-dom/client";
import { ScenarioToolsWindow } from "../../src/components/scenario/ScenarioToolsWindow";
import { MasterDataProvider } from "../../src/contexts/MasterDataContext";
import { PLAY_MODE_NORMAL, PLAY_MODE_SCENARIO } from "../../src/constants";
import { emit, state } from "./mock-runtime";

let population = 10;
let reject = false;
const writes: any[] = [];
const snapshot = () => ({ eligible_citizens: population, high_skill_citizens: 0, high_skill_ratio: 0, min_working_age: 18,
  majors: [1, 2, 3, 4, 5].map(skill_id => ({ skill_id, citizens: 0, ratio: 0 })) });
globalThis.fetch = async (input, init) => {
  const url = String(input);
  if (url.endsWith("/api/v1/sessions")) return new Response(JSON.stringify({ play_mode: PLAY_MODE_SCENARIO, debug_execution: false }));
  if (url.endsWith("/api/v1/scenario-tools/citizen-skills")) {
    if (init?.method === "POST") {
      writes.push(JSON.parse(String(init.body)));
      if (reject) return new Response(JSON.stringify({ code: "VALIDATION_ERROR", message: "rejected" }), { status: 400 });
      return new Response(JSON.stringify({ ...snapshot(), high_skill_citizens: 3, high_skill_ratio: 30, updated_citizens: 3,
        majors: [1, 2, 3, 4, 5].map((skill_id, index) => ({ skill_id, citizens: index < 3 ? 1 : 0, ratio: index < 3 ? 100 / 3 : 0 })) }));
    }
    return new Response(JSON.stringify(snapshot()));
  }
  const category = url.split("/").at(-1)!;
  return new Response(JSON.stringify({ [category]: category === "skills"
    ? Object.fromEntries([1, 2, 3, 4, 5].map(id => [id, { display_name: `Skill ${id}` }])) : {} }));
};
const container = document.createElement("div");
document.body.append(container);
let root = createRoot(container);
const render = () => <MasterDataProvider apiBaseUrl="http://test" connected><ScenarioToolsWindow apiBaseUrl="http://test" /></MasterDataProvider>;
const settle = () => act(async () => { await new Promise(resolve => setTimeout(resolve, 20)); });
const button = (key: string) => [...container.querySelectorAll<HTMLButtonElement>("button")].find(element => element.textContent === key)!;
const change = (element: HTMLInputElement, value: string) => act(async () => {
  Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, "value")!.set!.call(element, value);
  element.dispatchEvent(new window.Event("input", { bubbles: true }));
});
await act(async () => { root.render(render()); });
await settle();
for (const [key, type] of [["scenarioTools.editor.streetGrid", "street_grid"], ["taskbar.menuResourceEdit", "resource_edit"], ["taskbar.menuAgentCreation", "agent_creation"]]) {
  await act(async () => { button(key).click(); });
  assert.ok(state.calls.some(call => call.command === "open_registered_window" && call.args.windowType === type));
}
await act(async () => { button("taskbar.menuParameterEdit").click(); });
assert.ok(state.calls.some(call => call.command === "open_parameter_edit_window"));
await act(async () => { button("scenarioTools.editor.skillsTab").click(); });
await settle();
assert.equal(container.querySelectorAll("tbody tr").length, 5);
const highInput = () => container.querySelector<HTMLInputElement>(".scenario-tools__ratio input")!;
const majorInput = () => container.querySelector<HTMLInputElement>("tbody input")!;
await change(highInput(), "30");
assert.equal(writes.length, 0, "draft edits do not write to the city");
assert.equal([...container.querySelectorAll("tbody tr td:last-child")].reduce((sum, cell) => sum + Number(cell.textContent), 0), 3);
await change(majorInput(), "90");
assert.ok(button("scenarioTools.editor.apply").disabled, "invalid totals cannot be applied");
await change(majorInput(), "20");
await act(async () => { button("scenarioTools.editor.apply").click(); });
assert.equal(writes.length, 1);
assert.deepEqual(writes[0], { high_skill_ratio: 30, major_ratios: { 1: 20, 2: 20, 3: 20, 4: 20, 5: 20 } });
assert.ok(state.calls.some(call => call.command === "send_window_action" && call.args.action === "session:reload"));
await change(highInput(), "40");
reject = true;
await act(async () => { button("scenarioTools.editor.apply").click(); });
assert.ok(container.querySelector("[role=alert]"));
assert.equal(highInput().value, "40", "failed requests preserve the draft");
const writesBefore = writes.length;
await act(async () => { await emit("session-state", { session: { play_mode: PLAY_MODE_NORMAL } }); });
assert.ok(button("scenarioTools.editor.apply").disabled);
assert.ok(highInput().disabled);
await act(async () => { button("scenarioTools.editor.apply").click(); });
assert.equal(writes.length, writesBefore);
await act(async () => { root.unmount(); });
population = 0;
root = createRoot(container);
await act(async () => { root.render(render()); });
await settle();
await act(async () => { button("scenarioTools.editor.skillsTab").click(); });
await settle();
assert.ok(button("scenarioTools.editor.apply").disabled, "an empty target population cannot be changed");
await act(async () => { root.unmount(); });
container.remove();
console.log("PASS: scenario tool entry points, local drafts, exact preview counts, Apply-only writes, validation, failure recovery, mode restrictions and empty populations");
