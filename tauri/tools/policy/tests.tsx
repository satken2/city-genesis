import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { act } from "react";
import { createRoot } from "react-dom/client";
import i18next from "i18next";
import { I18nextProvider } from "react-i18next";
import { dispatch, opened } from "./mock-runtime";
import { PolicyPlanningWindow } from "../../src/components/policy/PolicyPlanningWindow";
import { PLAY_MODE_NORMAL, PLAY_MODE_READ_ONLY } from "../../src/constants";

const globals = globalThis as typeof globalThis & { policyPayload: unknown; policyRequests: number };
const i18n = i18next.createInstance();
await i18n.init({ lng: "ja", resources: { ja: { translation: {
  ...JSON.parse(readFileSync("src/i18n/ja/policy.json", "utf8")),
  ...JSON.parse(readFileSync("src/i18n/ja/common.json", "utf8")),
} } } });
globals.policyPayload = { epoch: 2, pending: false, indicators: [
  { id: "unemployment", value: 0.2, unit: "percent", sample: 100, roots: [
    { type: "no_vacancies", params: { count: 20, quota: 80, labor: 80 }, actions: ["public_jobs", "population"], children: [
      { type: "public_budget", params: { count: 2 }, actions: ["budget"], children: [] },
    ] },
  ] },
  { id: "income", value: 130.5, unit: "money", sample: 40, roots: [] },
  { id: "crime", value: null, unit: "per_thousand", sample: 100, roots: [] },
] };
const container = document.createElement("div");
document.body.append(container);
const root = createRoot(container);
const flush = async () => { await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)); }); };
await act(async () => root.render(<I18nextProvider i18n={i18n}><PolicyPlanningWindow apiBaseUrl="http://test" /></I18nextProvider>));
await flush();
assert.equal(container.querySelectorAll(".policy-indicator").length, 3);
assert.equal(container.querySelectorAll(".policy-finding").length, 2, "親と子の原因が表示される");
assert.ok(container.textContent!.includes("20%"));
const click = async (selector: string) => {
  const element = container.querySelector<HTMLButtonElement>(selector);
  assert.ok(element, selector);
  await act(async () => element.click());
  await flush();
};
await click('[data-guide="policy.action.population"]');
assert.deepEqual(opened, ["citizen_list"], "追放は既存の市民選択画面へ案内する");
await click('[data-guide="policy.action.budget"]');
assert.equal(opened.at(-1), "city_budget", "深い段階の行動もその画面を開ける");
await act(async () => dispatch("session-state", { session: { play_mode: PLAY_MODE_READ_ONLY } }));
assert.ok(container.querySelector<HTMLButtonElement>('[data-guide="policy.action.population"]')!.disabled);
await click('[data-guide="policy.indicator.income"]');
assert.equal(container.querySelectorAll(".policy-finding").length, 0);
assert.ok(container.textContent!.includes("該当する問題を確認できませんでした"));
await click('[data-guide="policy.indicator.crime"]');
assert.ok(container.textContent!.includes("まだエポックの実績がありません"));
await act(async () => dispatch("epoch:executing", { executing: true }));
assert.equal(container.querySelectorAll(".policy-indicator").length, 0, "実行中は以前の診断を表示しない");
assert.ok(container.textContent!.includes("エポック実行中"));
const requests = globals.policyRequests;
await act(async () => dispatch("session-state", { session: { play_mode: PLAY_MODE_NORMAL } }));
await act(async () => dispatch("epoch:executing", { executing: false }));
await flush();
assert.ok(globals.policyRequests > requests, "エポック完了で再調査する");
assert.equal(container.querySelectorAll(".policy-indicator").length, 3);
await act(async () => root.unmount());
const afterUnmount = globals.policyRequests;
await act(async () => dispatch("epoch:executing", { executing: false }));
await flush();
assert.equal(globals.policyRequests, afterUnmount);
console.log("PASS: policy cards, recursive causes, action destinations, missing data, read-only mode and epoch refresh");
