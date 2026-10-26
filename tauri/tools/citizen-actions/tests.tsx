import assert from "node:assert/strict";
import { act } from "react";
import { createRoot } from "react-dom/client";
import type { TFunction } from "i18next";
import { ConfirmDialogWindow } from "../../src/components/common/ConfirmDialogWindow";
import { runCitizenAction } from "../../src/components/citizen/citizenActions";
import { CONFIRM_DIALOG_RESULT_EVENT, type ConfirmDialogRequest } from "../../src/utils/confirmDialog";
import { listen, state } from "./mock-runtime";

const answers: unknown[] = [];
await listen(CONFIRM_DIALOG_RESULT_EVENT, (event) => answers.push(event.payload));

const container = document.createElement("div");
document.body.append(container);
const root = createRoot(container);
let closed = 0;
const render = (request: ConfirmDialogRequest) =>
  act(async () => root.render(<ConfirmDialogWindow request={request} onClose={() => { closed += 1; }} />));
const buttons = () => [...container.querySelectorAll("button")];
const confirmButton = () => buttons().at(-1)!;
const type = (value: string) => act(async () => {
  const input = container.querySelector("input")!;
  Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, "value")!.set!.call(input, value);
  input.dispatchEvent(new window.Event("input", { bubbles: true }));
});

await render({ requestId: "amount", message: "how much", amountMax: 500 });
assert.equal(confirmButton().disabled, true, "金額が空のうちは確定できない");
for (const value of ["0", "501", "2.5"]) {
  await type(value);
  assert.equal(confirmButton().disabled, true, `${value} は 1〜上限の整数でないので確定できない`);
}
await type("500");
await act(async () => confirmButton().click());
assert.deepEqual(answers.at(-1), { requestId: "amount", confirmed: true, amount: 500 });
assert.equal(closed, 1);

await render({ requestId: "cancelled", message: "how much", amountMax: 500 });
await type("100");
await act(async () => buttons()[0].click());
assert.deepEqual(answers.at(-1), { requestId: "cancelled", confirmed: false, amount: undefined }, "取り消しは金額を返さない");

await render({ requestId: "notice", message: "done", noticeOnly: true });
assert.equal(buttons().length, 1, "お知らせは OK だけ");
assert.equal(container.querySelector("input"), null);
await act(async () => confirmButton().click());
assert.equal(closed, 3);
await act(async () => root.unmount());

const posts: { url: string; body: unknown }[] = [];
let rejectPosts = false;
globalThis.fetch = async (input, init) => {
  const url = String(input);
  if (init?.method === "POST") {
    if (rejectPosts) return Response.json({ code: "VALIDATION_ERROR", message: "no" }, { status: 400 });
    posts.push({ url, body: init.body ? JSON.parse(String(init.body)) : undefined });
    return Response.json({});
  }
  if (url === "http://core/api/v1/city/budgets") return Response.json({ current_reserves: 1234.7 });
  if (url === "http://core/api/v1/agents/77") return Response.json({ economic_agent: { reserves: -20 } });
  throw new Error(`unexpected ${url}`);
};
const t = ((key: string, options?: { returnObjects?: boolean }) =>
  options?.returnObjects ? { 0: "a", 1: "b", 2: "c" } : key) as unknown as TFunction;
const target = { citizenId: 5, name: "Hanako", householdAgentId: 77 };
const dialogs = () => state.calls.filter((call) => call.command === "open_registered_window")
  .map((call) => call.args.params.extra.confirmRequest as ConfirmDialogRequest);
const run = (action: "reward" | "expel" | "donation" | "relocate") => runCitizenAction("http://core", action, target, t, "ja");

state.answer = { confirmed: true, amount: 300 };
assert.equal(await run("reward"), true);
assert.equal(dialogs().at(-1)!.amountMax, 1234, "報奨金の上限は市の現預金");
assert.deepEqual(posts.at(-1), { url: "http://core/api/v1/citizens/5/reward", body: { amount: 300 } });

state.answer = { confirmed: false };
assert.equal(await run("donation"), false);
assert.equal(dialogs().at(-1)!.amountMax, 0, "借金のある世帯には寄付を頼めない");
assert.equal(posts.length, 1, "取り消したら送らない");

for (const action of ["expel", "relocate"] as const) {
  const before = dialogs().length;
  assert.equal(await run(action), true);
  assert.deepEqual(posts.at(-1), { url: `http://core/api/v1/citizens/5/${action}`, body: undefined }, "確認なしで送る");
  assert.equal(dialogs().length, before + 1);
  assert.equal(dialogs().at(-1)!.noticeOnly, true, "結果は OK だけのお知らせで出す");
  assert.match(dialogs().at(-1)!.message, new RegExp(`^citizenAction\\.${action}\\.results\\.[012]$`));
}
assert.ok(state.emitted.some(({ event }) => event === "tracking:changed"), "追放は追跡一覧に知らせる");

rejectPosts = true;
const before = dialogs().length;
assert.equal(await run("relocate"), false);
assert.equal(dialogs().length, before, "失敗したら結果を出さない");

console.log("PASS: amount and notice dialogs, citizen actions send, cancel and report results");
