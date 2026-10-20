import assert from "node:assert/strict";
import React, { act } from "react";
import { createRoot } from "react-dom/client";
import { Window } from "happy-dom";
import { PathShapeEditorPanel } from "../../src/components/path/PathShapeEditorPanel";
import { buildProfileLayout, buildProfileModel, distToX, elevToY } from "../../src/components/path/pathProfile";
import { buildShapeGeometry } from "../../src/components/path/pathShapeOverlay";
import type { PathShapeUpdatePayload } from "../../src/components/path/pathBuildConstants";
import { DEFINITIONS } from "../../src/tutorial/definitions";
import { installUiGuide } from "../../src/tutorial/dom";
import { emit, invocations } from "./mock-runtime";
const browser = new Window();
for (const name of ["window", "document", "navigator", "HTMLElement", "Element", "KeyboardEvent", "MutationObserver", "ResizeObserver"]) Object.defineProperty(globalThis, name, { configurable: true, value: name === "window" ? browser : (browser as any)[name] });
Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true });
browser.HTMLCanvasElement.prototype.getContext = (() => null) as any;
const host = document.createElement("div"); document.body.append(host);
const root = createRoot(host);
const settle = async (fn: () => void | Promise<void>) => { await act(async () => { await fn(); await new Promise(r => setTimeout(r, 0)); }); };
const shape: PathShapeUpdatePayload = { unsent_paths_count: 0, path_type: 4, is_curved: true, phase: "editing", selected_index: 0, selected_points: [[0,0,0],[100,6,0]], start_control: [30,0,0], end_control: [70,6,0], height_editable: true };
const commands = () => invocations.filter(([,args]) => (args as any)?.action === "edit_path:shape_command").map(([,args]) => (args as any).params);
let release = () => {};
try {
  await settle(() => root.render(<PathShapeEditorPanel apiBaseUrl="http://fixture" />));
  await settle(() => emit("edit_path:update_shape", shape));
  const plan = host.querySelector<HTMLCanvasElement>('[data-testid="plan"]')!;
  assert.ok(plan);
  release = installUiGuide({ policy: "strict", allow: [], highlight: [] }, "path_shape_editor");
  await settle(() => plan.dispatchEvent(new browser.PointerEvent("pointerdown", { bubbles: true, clientX: 100, clientY: 100 })));
  assert.equal(commands().length, 0, "strict gate blocks unpermitted shape operations");
  release();
  const step = DEFINITIONS.scenario4.steps[8];
  release = installUiGuide(step.phases.at(-1)!.ui, "path_shape_editor");
  await settle(() => {
    plan.dispatchEvent(new browser.PointerEvent("pointerdown", { bubbles: true, clientX: 100, clientY: 100 }));
    plan.dispatchEvent(new browser.PointerEvent("pointermove", { bubbles: true, clientX: 120, clientY: 100 }));
    plan.dispatchEvent(new browser.PointerEvent("pointerup", { bubbles: true, clientX: 120, clientY: 100 }));
  });
  assert.ok(commands().some(c => c.op === "move_node" && c.x === 20), "step 9 plan dragging sends a node move to Unity");
  await settle(() => emit("edit_path:update_shape", shape));
  await settle(() => {
    plan.dispatchEvent(new browser.PointerEvent("pointerdown", { bubbles: true, clientX: 130, clientY: 100 }));
    plan.dispatchEvent(new browser.PointerEvent("pointermove", { bubbles: true, clientX: 140, clientY: 120 }));
    plan.dispatchEvent(new browser.PointerEvent("pointerup", { bubbles: true, clientX: 140, clientY: 120 }));
  });
  assert.ok(commands().some(c => c.op === "move_control"), "step 9 permits curve control handles");
  await settle(() => emit("edit_path:update_shape", shape));
  const profile = host.querySelector<HTMLCanvasElement>(".pse-profile canvas")!;
  const wrap = profile.parentElement!;
  Object.defineProperties(wrap, { clientWidth: { value: 800 }, clientHeight: { value: 200 } });
  const model = buildProfileModel(shape, buildShapeGeometry(shape))!;
  const layout = buildProfileLayout(model, 800, 200, new Map());
  const x = distToX(layout, model.nodes[1].dist), y = elevToY(layout, 6);
  await settle(() => {
    profile.dispatchEvent(new browser.PointerEvent("pointerdown", { bubbles: true, clientX: x, clientY: y }));
    profile.dispatchEvent(new browser.MouseEvent("mousedown", { bubbles: true, clientX: x, clientY: y }));
    browser.dispatchEvent(new browser.MouseEvent("mousemove", { clientX: x, clientY: y - 20 }));
    browser.dispatchEvent(new browser.MouseEvent("mouseup"));
  });
  assert.ok(invocations.some(([,args]) => (args as any)?.action === "edit_path:change_node" && (args as any)?.params.height > 6), "step 9 profile dragging sends a height change to Unity");
  await settle(() => emit("edit_path:update_shape", shape));
  await settle(() => {
    plan.dispatchEvent(new browser.PointerEvent("pointerdown", { bubbles: true, clientX: 100, clientY: 100 }));
    plan.dispatchEvent(new browser.PointerEvent("pointerup", { bubbles: true, clientX: 100, clientY: 100 }));
    document.activeElement!.dispatchEvent(new browser.KeyboardEvent("keydown", { key: "Delete", bubbles: true }));
  });
  assert.ok(commands().some(c => c.op === "remove_node"), "diagram focus keeps keyboard node editing available");
} finally { release(); await settle(() => root.unmount()); await browser.happyDOM.close(); }
console.log("PASS: step 9 real shape editor plan/control/profile dragging and keyboard editing under tutorial restrictions");
