import { PLAY_MODE_NORMAL, PLAY_MODE_SCEARIO, PLAY_MODE_TUTORIAL } from "../../src/constants";
import assert from "node:assert/strict";
import React, { act } from "react";
import { createRoot } from "react-dom/client";
import { Window } from "happy-dom";
import { Tutorial } from "../../src/components/tutorial/Tutorial";
import { FlatTabs } from "../../src/components/common/FlatTabs";
import { DEFINITIONS } from "../../src/tutorial/definitions";
import { installUiGuide } from "../../src/tutorial/dom";
import { emit, notifications, invocations, windowState } from "./mock-runtime";

const browser = new Window();
for (const name of ["window", "document", "navigator", "localStorage"]) Object.defineProperty(globalThis, name, { configurable: true, value: name === "window" ? browser : (browser as any)[name] });
Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true });
for (const name of ["Element", "HTMLElement", "KeyboardEvent", "MutationObserver"]) Object.assign(globalThis, { [name]: (browser as any)[name] });
const container = document.createElement("div");
document.body.append(container);
const root = createRoot(container);
const settle = async (fn: () => void | Promise<void>) => {
  await act(async () => { await fn(); });
  await act(async () => { await new Promise(r => setTimeout(r, 10)); });
};
const button = (label: string) => [...container.querySelectorAll<HTMLButtonElement>("button")].find(b => b.textContent === label);
const session = async (id: string | null) => settle(() => emit("session-state", { current_save_id: id, apiBaseUrl: "http://fixture", tutorialId: "basic" }));
const show = () => settle(() => emit("tutorial:show", "basic"));
const closeIntro = () => settle(() => emit("tutorial:window_visibility", { label: "tutorial_intro", window: "tutorial_intro", visible: false }));
const introRequests = () => invocations.filter(([command, args]) => command === "open_registered_window" && (args as { windowType?: string }).windowType === "tutorial_intro").length;
const originalFetch = globalThis.fetch;
const requests: { url: string; method?: string; body?: string }[] = [];
let finish: (response: Response) => void;
globalThis.fetch = async (url, options) => {
  requests.push({ url: String(url), method: options?.method, body: options?.body as string | undefined });
  if (options?.method !== "PATCH") return Response.json({ quests: [] });
  return new Promise<Response>(resolve => { finish = resolve; });
};
const patches = () => requests.filter(r => r.method === "PATCH");
try {
  await settle(() => root.render(<Tutorial />));
  await session("first"); await show();
  assert.equal(introRequests(), 1, "a new tutorial opens the intro window once");
  assert.equal(container.querySelector("button"), null, "guidance waits until the intro window is closed");
  await closeIntro();
  assert.equal(button("tutorial.guide.exit"), undefined, "steps have no exit button (the system tray ends the game)");
  for (let step = 0; step < 3; step++) await settle(() => button("tutorial.nextButton")!.click());
  assert.equal(button("tutorial.nextButton"), undefined, "the last step leads to the completion message");
  assert.equal(requests.some(r => r.method === "DELETE"), false, "completion does not end the session");
  await settle(() => button("tutorial.complete.button")!.click());
  assert.equal(button("tutorial.complete.button")!.disabled, true);
  await settle(() => button("tutorial.complete.button")!.click());
  assert.equal(patches().length, 1, "pending finish blocks duplicate requests");
  assert.deepEqual(JSON.parse(patches()[0].body!), { play_mode: PLAY_MODE_NORMAL }, "finish returns to normal mode");
  await settle(() => { finish(Response.json({ code: "FAILED" }, { status: 500 })); });
  assert.equal(notifications.at(-1), "tutorial.guide.finishFailed");
  assert.equal(button("tutorial.complete.button")!.disabled, false, "HTTP failure permits another finish attempt");
  windowState.hidden = false;
  await settle(() => button("tutorial.complete.button")!.click());
  await settle(() => { finish(Response.json({ success: true, play_mode: PLAY_MODE_NORMAL })); });
  assert.equal(container.querySelector("button"), null, "finish hides the tutorial panel");
  assert.equal(windowState.hidden, true, "finish hides the tutorial window itself");
  windowState.hidden = false;
  await show();
  assert.equal(windowState.hidden, true, "a finished tutorial hides its window again when shown");
  assert.equal(container.querySelector("button"), null);
  await session("second"); await show(); await closeIntro();
  assert.equal(introRequests(), 2, "each new save shows its own intro");
  assert.ok(button("tutorial.nextButton"), "next tutorial starts without remounting the window");
  await session(null); await session("second"); await show();
  assert.equal(introRequests(), 2, "a closed intro is not shown again for the same save");
  // 採掘編の実際の許可リストで、貨物タブを開いてから指定カードを選べることを確認する。
  let selected = 0;
  function PlatformSelector() {
    const [active, setActive] = React.useState("track");
    return <>
      <FlatTabs tabs={[
        { id: "track", label: "Track", guide: { name: "path.variant", values: [411] } },
        { id: "passenger", label: "Passenger", guide: { name: "path.variant", values: [462] } },
        { id: "freight", label: "Freight", guide: { name: "path.variant", values: [463, 464] } },
        { id: "busy", label: "Busy", disabled: true, guide: { name: "path.variant", values: [464] } },
      ]} active={active} onChange={setActive} iconOnly idPrefix="platform" />
      {active === "freight" && [463, 464].map(id => <button key={id} data-guide="path.variant" data-guide-value={id} onClick={() => { selected = id; }}>{id}</button>)}
    </>;
  }
  await settle(() => root.render(<PlatformSelector />));
  const ui = DEFINITIONS.mining.steps.find(step => step.id === "mining.platforms")!.phases.find(phase => phase.id === "variant")!.ui;
  const release = installUiGuide(ui, "path_build");
  try {
    const freight = container.querySelector<HTMLButtonElement>("#platform-tab-freight")!;
    assert.equal(freight.inert, false, "the tab containing the permitted platform stays usable");
    assert.ok(freight.hasAttribute("data-tutorial-highlight"), "the prerequisite tab is highlighted");
    for (const id of ["track", "passenger"]) assert.equal(container.querySelector<HTMLButtonElement>(`#platform-tab-${id}`)!.inert, true, "unrelated tabs stay blocked");
    assert.equal(container.querySelector<HTMLButtonElement>("#platform-tab-busy")!.disabled, true, "native disabled is preserved");
    await settle(() => freight.click());
    assert.equal(freight.getAttribute("aria-selected"), "true", "clicking the tab reveals its cards");
    await settle(() => button("463")!.click());
    assert.equal(selected, 0, "another freight platform stays blocked");
    await settle(() => button("464")!.click());
    assert.equal(selected, 464, "the required ore platform can be selected");
    for (const key of ["Home", "End", "ArrowLeft", "ArrowRight"]) {
      await settle(() => { freight.dispatchEvent(new browser.KeyboardEvent("keydown", { key, bubbles: true, cancelable: true }) as unknown as Event); });
      assert.equal(freight.getAttribute("aria-selected"), "true", "keyboard navigation skips blocked and disabled tabs");
    }
  } finally { release(); }
  assert.equal(container.querySelector<HTMLButtonElement>("#platform-tab-track")!.inert, false, "ending the guide releases tabs");
  assert.equal(container.querySelector<HTMLButtonElement>("#platform-tab-busy")!.disabled, true, "ending the guide preserves native disabled");
} finally {
  await settle(() => root.unmount());
  globalThis.fetch = originalFetch;
  await browser.happyDOM.close();
}
console.log("PASS: tutorial component next/finish buttons, repeated sessions and mining platform tab/card restrictions");
