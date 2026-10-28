import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { act } from "react";
import { createRoot } from "react-dom/client";
import i18next from "i18next";
import { I18nextProvider } from "react-i18next";
import { dispatch, opened, invocations } from "./mock-runtime";
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
  { id: "crime", value: 0.4, unit: "per_thousand", sample: 17500, roots: [
    { type: "security_response", params: { count: 7 }, actions: ["police"], children: [
      { type: "police_budget", params: { count: 1 }, actions: ["budget"], children: [] },
    ] },
    { type: "crime_incidents", params: { count: 12, crimes: 7, signs: 5 }, actions: [], children: [
      { type: "unemployment_pressure", params: { count: 20 }, actions: ["employment"], children: [
        { type: "no_vacancies", params: { count: 20, quota: 80, labor: 80 }, actions: ["public_jobs"], children: [
          { type: "public_budget", params: { count: 2 }, actions: ["budget"], children: [] },
        ] },
      ] },
      { type: "citizen_criminality", params: { count: 30 }, actions: ["criminality"], children: [] },
    ] },
  ] },
] };
const container = document.createElement("div");
document.body.append(container);
const root = createRoot(container);
const flush = async () => { await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)); }); };
await act(async () => root.render(<I18nextProvider i18n={i18n}><PolicyPlanningWindow apiBaseUrl="http://test" /></I18nextProvider>));
await flush();
assert.equal(container.querySelectorAll(".policy-indicator").length, 3);
assert.deepEqual([...container.querySelectorAll(".policy-indicator")].map(button => button.textContent), ["犯罪被害⚠️", "失業率⚠️", "所得"],
  "調査ボタンは項目名と問題のある項目の注意マークだけを表示する");
assert.equal(container.querySelectorAll(".policy-finding").length, 6, "抑止側と発生側を分けて原因・別指標への参照を表示する");
assert.ok(container.querySelector('.policy-row[data-depth="2"] [data-guide="policy.action.budget"]'), "対策は該当する原因と同じ行に並ぶ");
assert.equal(container.querySelector(".policy-actions p"), null, "対策の補足文を表示しない");
assert.equal(container.querySelector(".policy-intro"), null);
assert.ok(container.textContent!.includes("1,000人あたり 0.4件"));
assert.ok(container.textContent!.includes("直近1か月の犯罪被害：7件"), "件数と集計期間を説明する");
assert.ok(container.textContent!.includes("直近1か月のいざこざ：12件"));
assert.ok(!/エポック|犯罪の兆候|犯罪性向/.test(container.textContent!), "内部用語をプレイヤー向けの言葉に置き換える");
await act(async () => dispatch("session-state", { city: { city: { simulation_days: 7 } } }));
assert.ok(container.textContent!.includes("直近1週間の犯罪被害：7件"));
assert.ok(container.textContent!.includes("直近1週間のいざこざ：12件"));
assert.ok(container.querySelector('.policy-row[data-depth="0"]')?.textContent?.includes("直近1週間"),
  "人口あたりの犯罪被害も同じ期間を表示する");
assert.ok(container.textContent!.includes("予算不足の警察署：1か所"), "施設数が何を指すかを説明する");
assert.ok(!container.textContent!.includes("現在の雇用枠に空きがない"), "他の指標で調査できる原因を展開しない");
assert.equal(container.querySelectorAll('.policy-row[data-parent-id="1"]').length, 2);
assert.equal(container.querySelectorAll('.policy-row[data-parent-id="1.0"]').length, 0);
assert.ok(container.querySelector('.policy-row[data-parent-id="1"][data-depth="2"] [data-guide="policy.action.criminality"]'),
  "犯罪性向は発生側の下で失業と並列に表示する");
assert.ok(container.textContent!.includes("犯罪傾向のある市民：30人"));
const click = async (selector: string) => {
  const element = container.querySelector<HTMLButtonElement>(selector);
  assert.ok(element, selector);
  await act(async () => element.click());
  await flush();
};
const beforeSwitch = globals.policyRequests;
await click('[data-guide="policy.investigate.unemployment"]');
assert.equal(container.querySelector('[data-guide="policy.indicator.unemployment"]')?.getAttribute("aria-pressed"), "true");
assert.equal(globals.policyRequests, beforeSwitch, "同じ調査結果の失業率タブへ切り替える");
assert.equal(opened.length, 0, "別ウィンドウを開かずに指標を切り替える");
assert.equal(container.querySelectorAll(".policy-finding").length, 3);
assert.ok(container.textContent!.includes("20%"));
await click('[data-guide="policy.action.population"]');
assert.deepEqual(opened, ["citizen_list"], "追放は既存の市民選択画面へ案内する");
await click('[data-guide="policy.action.budget"]');
assert.equal(opened.at(-1), "city_budget", "深い段階の行動もその画面を開ける");
await act(async () => dispatch("session-state", { session: { play_mode: PLAY_MODE_READ_ONLY } }));
assert.ok(container.querySelector<HTMLButtonElement>('[data-guide="policy.action.population"]')!.disabled);
await click('[data-guide="policy.indicator.income"]');
assert.equal(container.querySelectorAll(".policy-finding").length, 1);
assert.ok(container.textContent!.includes("該当する問題はありません"));
assert.equal(container.querySelector('[data-guide="policy.action.welfare"]'), null, "問題のない所得には増額案を出さない");
await click('[data-guide="policy.indicator.crime"]');
assert.ok(!container.querySelector<HTMLButtonElement>('[data-guide="policy.action.criminality"]')!.disabled,
  "見学中も市民の犯罪性向を確認できる");
await click('[data-guide="policy.action.criminality"]');
assert.equal(opened.at(-1), "citizen_list");
assert.ok(!container.querySelector<HTMLButtonElement>('[data-guide="policy.investigate.unemployment"]')!.disabled,
  "見学中でも別の指標を調査できる");
await act(async () => dispatch("epoch:executing", { executing: true }));
assert.equal(container.querySelectorAll(".policy-indicator").length, 0, "実行中は以前の診断を表示しない");
assert.ok(container.textContent!.includes("シミュレーション中"));
const requests = globals.policyRequests;
await act(async () => dispatch("session-state", { session: { play_mode: PLAY_MODE_NORMAL } }));
await act(async () => dispatch("epoch:executing", { executing: false }));
await flush();
assert.ok(globals.policyRequests > requests, "エポック完了で再調査する");
assert.equal(container.querySelectorAll(".policy-indicator").length, 3);
const incomePayload = structuredClone(globals.policyPayload) as { indicators: { id: string; value: number; roots: unknown[] }[] };
const income = incomePayload.indicators.find(item => item.id === "income")!;
income.value = 24.5;
income.roots = [{ type: "household_unemployment", params: { count: 3 }, actions: ["public_jobs", "welfare"], children: [] }];
globals.policyPayload = incomePayload;
await click(".policy-toolbar > button");
await click('[data-guide="policy.indicator.income"]');
const welfareButton = container.querySelector<HTMLButtonElement>('.policy-row[data-depth="0"] [data-guide="policy.action.welfare"]');
assert.ok(welfareButton);
assert.equal(welfareButton?.textContent, "給付金・生活扶助を増やす", "所得の行に給付増額の対策を表示する");
await act(async () => dispatch("session-state", { session: { play_mode: PLAY_MODE_READ_ONLY } }));
assert.ok(welfareButton.disabled);
await act(async () => dispatch("session-state", { session: { play_mode: PLAY_MODE_NORMAL } }));
await click('.policy-row[data-depth="0"] [data-guide="policy.action.welfare"]');
assert.deepEqual(invocations.at(-1), { windowType: "city_regulation", params: { extra: { initialTab: "welfare" } } },
  "給付増額から条例の社会保障タブを直接開く");
globals.policyPayload = { epoch: 2, pending: false, indicators: [
  { id: "unemployment", value: 0.2, unit: "percent", sample: 100, roots: [
    { type: "unfilled_jobs", params: { count: 20, vacancies: 10 }, actions: ["employment"], children: [
      { type: "education_gap", params: { count: 8 }, actions: [], children: [
        { type: "school_capacity", params: { enrolled: 100, waiting: 12, capacity: 100 }, actions: ["education"], children: [] },
        { type: "immigrant_education_gap", params: { count: 3 }, actions: ["immigration_skills", "immigration_limit"], children: [] },
      ] },
    ] },
  ] },
] };
await click(".policy-toolbar > button");
await click('[data-guide="policy.indicator.unemployment"]');
assert.equal(container.querySelector('.policy-row[data-depth="2"] .policy-actions')?.children.length, 0,
  "教育未修了の求職者の行には対策を表示しない");
assert.ok(container.textContent!.includes("通学中 100人・未就学 12人／定員 100人"));
const educationButton = container.querySelector<HTMLButtonElement>('.policy-row[data-depth="3"] [data-guide="policy.action.education"]');
assert.equal(educationButton?.textContent, "学校を増やす", "学校定員の不足に対して増設を提案する");
await click('[data-guide="policy.action.education"]');
assert.equal(opened.at(-1), "building_construction");
assert.ok(container.textContent!.includes("該当する移民の求職者：3人"));
assert.ok(!container.textContent!.includes("流入"), "現在の人数を流入実績として表示しない");
await act(async () => dispatch("session-state", { session: { play_mode: PLAY_MODE_READ_ONLY } }));
assert.ok(container.querySelector<HTMLButtonElement>('[data-guide="policy.action.immigration_skills"]')!.disabled);
assert.ok(container.querySelector<HTMLButtonElement>('[data-guide="policy.action.immigration_limit"]')!.disabled);
await act(async () => dispatch("session-state", { session: { play_mode: PLAY_MODE_NORMAL } }));
for (const action of ["immigration_skills", "immigration_limit"]) {
  await click(`[data-guide="policy.action.${action}"]`);
  assert.deepEqual(invocations.at(-1), { windowType: "city_regulation", params: { extra: { initialTab: "immigration" } } },
    "移民の対策は条例の移民タブを直接開く");
}

globals.policyPayload = { epoch: 2, pending: false, indicators: [
  { id: "crime", value: 0, unit: "per_thousand", sample: 1000, roots: [
    { type: "crime_incidents", params: { count: 5, crimes: 0, signs: 5 }, actions: [], children: [] },
  ] },
] };
await click(".policy-toolbar > button");
await click('[data-guide="policy.indicator.crime"]');
assert.equal(container.querySelector('[data-guide="policy.indicator.crime"]')?.textContent, "犯罪被害⚠️");
assert.ok(container.textContent!.includes("直近1週間のいざこざ：5件"));
assert.equal(container.querySelector('[data-guide="policy.action.police"]'), null,
  "被害を抑えられた兆候だけなら、警察の抑止不足を表示しない");

globals.policyPayload = { epoch: 1, pending: false, indicators: [
  { id: "crime", value: null, unit: "per_thousand", sample: 0, roots: [] },
] };
await click(".policy-toolbar > button");
await click('[data-guide="policy.indicator.crime"]');
assert.ok(container.textContent!.includes("実績なし"));
await act(async () => root.unmount());
const afterUnmount = globals.policyRequests;
await act(async () => dispatch("epoch:executing", { executing: false }));
await flush();
assert.equal(globals.policyRequests, afterUnmount);
console.log("PASS: compact selectors, aligned actions, terminal indicator references, in-window switching, read-only mode and epoch refresh");
