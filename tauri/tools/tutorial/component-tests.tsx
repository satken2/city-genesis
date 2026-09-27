import assert from "node:assert/strict";
import React, { act } from "react";
import { createRoot } from "react-dom/client";
import { Window } from "happy-dom";
import { Tutorial } from "../../src/components/tutorial/Tutorial";
import { emit, notifications } from "./mock-runtime";

const browser = new Window();
for (const name of ["window", "document", "navigator", "localStorage"]) Object.defineProperty(globalThis, name, { configurable: true, value: name === "window" ? browser : (browser as any)[name] });
Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true });
const container = document.createElement("div");
document.body.append(container);
const root = createRoot(container);
const settle = async (fn: () => void | Promise<void>) => {
  await act(async () => { await fn(); });
  await act(async () => { await new Promise(r => setTimeout(r, 10)); });
};
const exitButton = () => [...container.querySelectorAll<HTMLButtonElement>("button")].find(b => b.textContent === "tutorial.guide.exit")!;
const session = async (id: string | null) => settle(() => emit("session-state", { current_save_id: id, apiBaseUrl: "http://fixture", tutorialId: "basic" }));
const show = () => settle(() => emit("tutorial:show", "basic"));
const originalFetch = globalThis.fetch;
let requests = 0, finishExit: (response: Response) => void;
globalThis.fetch = async (_url, options) => {
  assert.equal(options?.method, "DELETE");
  requests++;
  return new Promise<Response>(resolve => { finishExit = resolve; });
};
try {
  await settle(() => root.render(<Tutorial />));
  await session("first"); await show();
  assert.equal(exitButton().disabled, false);
  await settle(() => exitButton().click());
  assert.equal(exitButton().disabled, true);
  await settle(() => exitButton().click());
  assert.equal(requests, 1, "pending exit blocks duplicate requests");
  await settle(() => { finishExit(Response.json({ success: true })); });
  await session(null);
  assert.equal(container.querySelector("button"), null, "session end hides the persistent tutorial window");
  await session("second"); await show();
  assert.equal(exitButton().disabled, false, "next tutorial can exit without remounting the window");
  await settle(() => exitButton().click());
  await settle(() => { finishExit(Response.json({ code: "FAILED" }, { status: 500 })); });
  assert.equal(exitButton().disabled, false, "HTTP failure permits another exit attempt");
  assert.equal(notifications.at(-1), "tutorial.guide.exitFailed");
  globalThis.fetch = async () => { requests++; throw new TypeError("connection lost"); };
  await settle(() => exitButton().click());
  assert.equal(exitButton().disabled, false, "network failure permits another exit attempt");
  globalThis.fetch = async () => Response.json({ success: true });
  await settle(() => exitButton().click());
  assert.equal(exitButton().disabled, true);
  await session("third"); await show();
  assert.equal(exitButton().disabled, false, "save change resets exit even if the null session event was missed");
} finally {
  await settle(() => root.unmount());
  globalThis.fetch = originalFetch;
  await browser.happyDOM.close();
}
console.log("PASS: tutorial component exit, repeated sessions, HTTP and network failure recovery");
