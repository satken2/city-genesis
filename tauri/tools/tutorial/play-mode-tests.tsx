import assert from "node:assert/strict";
import React, { act } from "react";
import { createRoot } from "react-dom/client";
import { Window } from "happy-dom";
import { PLAY_MODE_NORMAL, PLAY_MODE_SCEARIO, PLAY_MODE_TUTORIAL } from "../../src/constants";
import { IconPanel } from "../../src/components/hud/IconPanel";
import { SystemTray } from "../../src/components/hud/SystemTray";
import { usePlayMode } from "../../src/hooks/usePlayMode";
import { emit } from "./mock-runtime";

const browser = new Window();
for (const name of ["window", "document"]) Object.defineProperty(globalThis, name, { configurable: true,
  value: name === "window" ? browser : browser.document });
Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true,
  ResizeObserver: class { observe() {} disconnect() {} } });
const container = document.createElement("div");
document.body.append(container);
const root = createRoot(container);
const originalFetch = globalThis.fetch;
globalThis.fetch = async () => Response.json({ play_mode: PLAY_MODE_NORMAL });
function Probe() {
  const mode = usePlayMode("http://fixture");
  return <output id="mode">{mode}</output>;
}
const update = async (mode: number) => {
  await act(async () => {
    await emit("session-state", { current_save_id: "city", apiBaseUrl: "http://fixture", isLoading: false,
      session: { play_mode: mode, debug_execution: true }, city: { city: { city_hall_placed: false } } });
  });
};
try {
  await act(async () => { root.render(<><IconPanel /><SystemTray /><Probe /></>); });
  await update(PLAY_MODE_NORMAL);
  const normalIcons = [...container.querySelectorAll(".icon-panel button")].map(button => button.id);
  assert.equal(container.querySelector<HTMLButtonElement>("#stat")!.disabled, true, "normal HUD locks before founding");
  await update(PLAY_MODE_SCEARIO);
  for (const id of ["resource_edit", "parameter_edit", "agent_creation", "template_complete", "template_street_grid", "debug_template"]) {
    assert.ok(container.querySelector(`#${id}`), `god mode exposes ${id}`);
  }
  await update(PLAY_MODE_TUTORIAL);
  assert.equal(container.querySelector("#mode")!.textContent, String(PLAY_MODE_TUTORIAL), "shared hook follows mode changes");
  assert.deepEqual([...container.querySelectorAll(".icon-panel button")].map(button => button.id), normalIcons, "tutorial uses the normal icon set");
  assert.equal(container.querySelector<HTMLButtonElement>("#stat")!.disabled, false, "tutorial HUD works before founding");
  for (const id of ["template_complete", "template_street_grid", "debug_template"]) {
    assert.equal(container.querySelector(`#${id}`), null, `tutorial hides ${id}`);
  }
  await update(PLAY_MODE_NORMAL);
  assert.equal(container.querySelector("#mode")!.textContent, String(PLAY_MODE_NORMAL), "tutorial finish reaches reused windows");
  console.log("PASS: normal/god/tutorial HUD menus, founding lock, tray buttons and live play mode updates");
} finally {
  await act(async () => root.unmount());
  globalThis.fetch = originalFetch;
}
