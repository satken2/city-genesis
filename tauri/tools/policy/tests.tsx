import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { act } from "react";
import { createRoot } from "react-dom/client";
import i18next from "i18next";
import { I18nextProvider } from "react-i18next";
import { dispatch, opened, invocations } from "./mock-runtime";
import { PolicyPlanningWindow } from "../../src/components/policy/PolicyPlanningWindow";
import { PLAY_MODE_NORMAL, PLAY_MODE_READ_ONLY, PRODUCT_WATER_SUPPLY, PRODUCT_GARBAGE_SERVICE,
  PRODUCT_ELECTRICITY, PRODUCT_HIGH_WATER } from "../../src/constants";

const globals = globalThis as typeof globalThis & { policyPayload: unknown; policyRequests: number };
const i18n = i18next.createInstance();
await i18n.init({ lng: "ja", resources: { ja: { translation: {
  ...JSON.parse(readFileSync("src/i18n/ja/policy.json", "utf8")),
  ...JSON.parse(readFileSync("src/i18n/ja/diagnosis.json", "utf8")),
  ...JSON.parse(readFileSync("src/i18n/ja/notifications.json", "utf8")),
  ...JSON.parse(readFileSync("src/i18n/ja/common.json", "utf8")),
} } } });
globals.policyPayload = { epoch: 2, pending: false, indicators: [
  { id: "unemployment", value: 0.2, unit: "percent", sample: 100, issue: true, roots: [
    { type: "no_vacancies", params: { count: 20, quota: 80, labor: 80 }, actions: ["public_jobs", "population"], children: [
      { type: "public_budget", params: { count: 2 }, actions: ["budget"], children: [] },
    ] },
  ] },
  { id: "income", value: 130.5, unit: "money", sample: 40, issue: false, roots: [] },
  { id: "crime", value: 0.4, unit: "per_thousand", sample: 17500, issue: true, params: { incidents: 12 }, roots: [
    { type: "security_response", params: { count: 7 }, actions: ["police"], children: [
      { type: "police_budget", params: { count: 1 }, actions: ["budget"], children: [] },
    ] },
    { type: "unemployment_pressure", params: { count: 20 }, actions: ["employment"], children: [
      { type: "no_vacancies", params: { count: 20, quota: 80, labor: 80 }, actions: ["public_jobs"], children: [
        { type: "public_budget", params: { count: 2 }, actions: ["budget"], children: [] },
      ] },
    ] },
    { type: "citizen_criminality", params: { count: 30 }, actions: ["criminality"], children: [] },
  ] },
] };
const container = document.createElement("div");
document.body.append(container);
const root = createRoot(container);
const flush = async () => { await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)); }); };
// 診断の再取得はエポック完了に従うため、入力データを差し替えた後も同じ通知経路を通す。
const refreshAfterEpoch = async () => {
  await act(async () => dispatch("epoch:executing", { executing: true }));
  await act(async () => dispatch("epoch:executing", { executing: false }));
  await flush();
};
await act(async () => root.render(<I18nextProvider i18n={i18n}><PolicyPlanningWindow apiBaseUrl="http://test" /></I18nextProvider>));
await flush();
assert.equal(container.querySelectorAll(".policy-indicator").length, 3);
assert.deepEqual([...container.querySelectorAll(".policy-indicator")].map(button => button.textContent), ["犯罪被害⚠️", "失業率⚠️", "所得"],
  "調査ボタンは項目名と問題のある項目の注意マークだけを表示する");
assert.equal(container.querySelectorAll(".policy-finding").length, 5, "抑止側と発生側の原因・別指標への参照を並べる");
assert.ok(container.querySelector('.policy-row[data-depth="2"] [data-guide="policy.action.budget"]'), "対策は該当する原因と同じ行に並ぶ");
assert.equal(container.querySelector(".policy-actions p"), null, "対策の補足文を表示しない");
assert.equal(container.querySelector(".policy-intro"), null);
assert.ok(container.textContent!.includes("1,000人あたり 0.4件"));
assert.ok(container.textContent!.includes("直近1か月の犯罪被害：7件"), "件数と集計期間を説明する");
assert.ok(container.querySelector('.policy-row[data-depth="0"]')?.textContent?.includes("いざこざ 12件"),
  "いざこざの件数は頂点の根拠に出す");
assert.ok(!/エポック|犯罪の兆候|犯罪性向/.test(container.textContent!), "内部用語をプレイヤー向けの言葉に置き換える");
await act(async () => dispatch("session-state", { city: { city: { simulation_days: 7 } } }));
assert.ok(container.textContent!.includes("直近1週間の犯罪被害：7件"));
assert.ok(container.querySelector('.policy-row[data-depth="0"]')?.textContent?.includes("直近1週間"),
  "人口あたりの犯罪被害も同じ期間を表示する");
assert.ok(container.textContent!.includes("予算不足の警察署：1か所"), "施設数が何を指すかを説明する");
assert.ok(!container.textContent!.includes("現在の雇用枠に空きがない"), "他の指標で調査できる原因を展開しない");
assert.equal(container.querySelectorAll('.policy-row[data-parent-id="1"]').length, 0, "失業は失業率の調査で止める");
assert.ok(container.querySelector('.policy-row[data-parent-id="root"][data-depth="1"] [data-guide="policy.action.criminality"]'),
  "犯罪性向は失業や警察と並べて表示する");
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
assert.equal(container.querySelectorAll(".policy-finding").length, 0, "問題が無い指標はカードを出さない");
assert.equal(container.querySelector(".policy-clear")?.textContent, "所得の問題は見つかっていません");
assert.equal(container.querySelector('[data-guide="policy.action.welfare"]'), null, "問題のない所得には増額案を出さない");
await click('[data-guide="policy.indicator.crime"]');
await click('.policy-row[data-depth="0"] .policy-finding-open');
assert.deepEqual(invocations.at(-1), {
  windowType: "city_statistics", params: { extra: { initialTab: "accidents", initialSubTab: "crime" } },
}, "見学中でも事件・事故の犯罪タブを直接開く");
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
const incomePayload = structuredClone(globals.policyPayload) as { indicators: { id: string; value: number; issue: boolean; roots: unknown[] }[] };
const income = incomePayload.indicators.find(item => item.id === "income")!;
income.value = 24.5;
income.issue = true;
income.roots = [{ type: "household_unemployment", params: { count: 3 }, actions: ["public_jobs", "welfare"], children: [] }];
globals.policyPayload = incomePayload;
await refreshAfterEpoch();
await click('[data-guide="policy.indicator.income"]');
const welfareButton = container.querySelector<HTMLButtonElement>('.policy-row[data-depth="0"] [data-guide="policy.action.welfare"]');
assert.ok(welfareButton);
assert.equal(welfareButton?.textContent, "給付金・生活扶助を増やす", "所得の行に給付増額の対策を表示する");
await act(async () => dispatch("session-state", { session: { play_mode: PLAY_MODE_READ_ONLY } }));
assert.ok(welfareButton.disabled);
await act(async () => dispatch("session-state", { session: { play_mode: PLAY_MODE_NORMAL } }));
await click('.policy-row[data-depth="0"] [data-guide="policy.action.welfare"]');
assert.deepEqual(invocations.at(-1), { windowType: "city_regulation", params: { extra: { initialTab: "labor" } } },
  "給付増額から条例の労働・社会保障タブを直接開く");
globals.policyPayload = { epoch: 2, pending: false, indicators: [
  { id: "unemployment", value: 0.2, unit: "percent", sample: 100, issue: true, roots: [
    { type: "unfilled_jobs", params: { count: 20, vacancies: 10 }, actions: ["employment"], children: [
      { type: "education_gap", params: { count: 8 }, actions: [], children: [
        { type: "school_capacity", params: { enrolled: 100, waiting: 12, capacity: 100 }, actions: ["education"], children: [] },
        { type: "immigrant_education_gap", params: { count: 3 }, actions: ["immigration_skills", "immigration_limit"], children: [] },
      ] },
    ] },
  ] },
] };
await refreshAfterEpoch();
await click('[data-guide="policy.indicator.unemployment"]');
assert.equal(container.querySelector('.policy-row[data-depth="2"] .policy-actions'), null,
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
  { id: "crime", value: 0, unit: "per_thousand", sample: 1000, issue: true, params: { incidents: 5 }, roots: [] },
] };
await refreshAfterEpoch();
await click('[data-guide="policy.indicator.crime"]');
assert.equal(container.querySelector('[data-guide="policy.indicator.crime"]')?.textContent, "犯罪被害⚠️");
assert.ok(container.textContent!.includes("いざこざ 5件"));
assert.equal(container.querySelector('[data-guide="policy.action.police"]'), null,
  "被害を抑えられた兆候だけなら、警察の抑止不足を表示しない");

globals.policyPayload = { epoch: 1, pending: false, indicators: [
  { id: "crime", value: null, unit: "per_thousand", sample: 0, issue: false, roots: [] },
] };
await refreshAfterEpoch();
await click('[data-guide="policy.indicator.crime"]');
assert.ok(container.textContent!.includes("実績なし"));

// 左は指標・稼働できていない施設・その他の問題の順に並べる。施設の不調は木の中では参照で止め、施設を選ぶとその原因を表示する。
globals.policyPayload = { epoch: 2, pending: false, problems: [
  { id: "tenant", type: "tenant_blocked_by_parcel_size", severity: "caution", created_date: 10, entity: { type: "zone", id: 2 },
    params: { zone_name: "港町", tenant_name: "製鉄所", required: 100, max_capacity: 50 }, actions: ["parcel"], roots: [] },
], facilities: [
  { id: 31, name: "泉町ポンプ場", variant: 1063, severity: "warning", params: { percent: 0 }, roots: [
    { type: "indicator_reference", domain: "policy", indicator: "water", params: {}, actions: [], children: [] },
    { type: "facility_material_unmet", domain: "policy", params: { product_id: 402, demand: 3909, received: 0 }, actions: [],
      subject: { id: 31, name: "泉町ポンプ場" }, children: [
        { type: "infra_generation_shortage", domain: "diagnosis", params: { product_id: 402, supply: 0, demand: 11888, producers: 1 },
          actions: [], subject: null, children: [
            { type: "facility_ref", domain: "policy", params: {}, actions: [], subject: { id: 30, name: "松葉町ポンプ場" }, children: [] }] }] }] },
  { id: 30, name: "松葉町ポンプ場", variant: 1049, severity: "warning", params: { percent: 12.5 }, roots: [
    { type: "facility_labor_shortage", domain: "policy", params: { quota: 3, total_labor: 1 }, actions: [],
      subject: { id: 30, name: "松葉町ポンプ場" }, children: [
        { type: "indicator_reference", domain: "policy", indicator: "unemployment", params: {}, actions: [], children: [] }] },
    { type: "facility_budget_capped", domain: "policy", params: {}, actions: ["budget"], subject: { id: 30, name: "松葉町ポンプ場" }, children: [] }] },
], indicators: [
  { id: "unemployment", value: 0.2, unit: "percent", sample: 100, issue: true, roots: [] },
  { id: "water", value: 0.023, unit: "percent", sample: 100, issue: true, roots: [
    { type: "service_facility_limited", domain: "policy", params: { count: 2 }, actions: ["facilities"], children: [
      { type: "facility_ref", domain: "policy", params: {}, actions: [], subject: { id: 31, name: "泉町ポンプ場" }, children: [] },
      { type: "facility_ref", domain: "policy", params: {}, actions: [], subject: { id: 30, name: "松葉町ポンプ場" }, children: [] }] }] },
  { id: "sanitation", value: 5, unit: "households", sample: 100, issue: true, roots: [
    { type: "service_shortage", domain: "policy", params: { product_id: 307, percent: 2.3 }, actions: [], children: [
      { type: "service_no_facility", domain: "policy", params: {}, actions: ["service_facility"], children: [] }] }] },
] };
await refreshAfterEpoch();
const rowTitles = () => [...container.querySelectorAll(".policy-finding h3")].map(title => title.textContent);
assert.deepEqual([...container.querySelectorAll(".policy-section-title")].map(title => title.textContent),
  ["指標", "稼働できていない施設", "その他の問題"]);
assert.deepEqual([...container.querySelectorAll(".policy-problem-message")].map(row => row.textContent), [
  "泉町ポンプ場が稼働できていない", "松葉町ポンプ場が稼働できていない",
  "地区「港町」で「製鉄所」が入居できる建物を建てられない（必要100m2、区画の最大50m2）",
], "施設を先に、その他の問題を後に並べる");
assert.equal(container.querySelector('[data-guide="policy.indicator.unemployment"]')?.getAttribute("aria-pressed"), "true",
  "左の並びのとおり、問題のある最初の指標を開く");
await click('[data-guide="policy.indicator.water"]');
assert.ok(container.textContent!.includes("世帯・施設の注文に対する受取率 2.3%"));
await click('.policy-row[data-depth="0"] .policy-finding-open');
assert.deepEqual(invocations.at(-1), { windowType: "public_facilities", params: { extra: { initialProductId: PRODUCT_WATER_SUPPLY } } },
  "水道の事象カードから公共サービスの水道タブを開く");
await click('.policy-row[data-depth="1"] .policy-finding-open');
assert.deepEqual(invocations.at(-1), { windowType: "public_facilities", params: { extra: { initialProductId: PRODUCT_WATER_SUPPLY } } },
  "施設の稼働不足の集計カードは親の品目を引き継ぐ");
const master = JSON.parse(readFileSync("src/i18n/ja/policy.json", "utf8"));
assert.ok(master.policy.nodes.service_facility_limited.title.includes("{{kinds}}"));
assert.deepEqual(rowTitles().slice(2), ["泉町ポンプ場が稼働できていない", "松葉町ポンプ場が稼働できていない"],
  "稼働できていない施設は列挙だけして、その先はたどらない");
assert.equal(container.querySelectorAll('.policy-row[data-depth="3"]').length, 0);
await click('.policy-row[data-depth="2"] .policy-finding-open');
assert.deepEqual(invocations.at(-1), { windowType: "agent_detail", entityId: "31" }, "参照カードから施設詳細を開く");
const beforeInvestigation = opened.length;
await click('.policy-row[data-depth="2"] [data-guide="policy.investigate.facility"]');
assert.equal(opened.length, beforeInvestigation, "カード内の調査ボタンは詳細画面を重ねて開かない");
assert.equal(container.querySelector('[data-guide="policy.facility"][aria-pressed="true"]')?.textContent, "泉町ポンプ場が稼働できていない",
  "施設の行から、その施設を選ぶ");
assert.deepEqual(rowTitles(), [
  "泉町ポンプ場が稼働できていない", "水道の供給が不足している", "泉町ポンプ場がを調達できていない", "の生産量が不足している",
  "松葉町ポンプ場が稼働できていない",
], "施設の木も、別の指標と別の施設の不調は案内で止める");
assert.ok(container.textContent!.includes("生産は計画の 0%"));
assert.ok(!/です|ます|ません/.test([...container.querySelectorAll(".policy-finding")].map(card => card.textContent).join("")),
  "起きていることは常体でそろえる");
await click('.policy-row[data-depth="0"] [data-guide="policy.action.entity_detail"]');
assert.deepEqual(invocations.at(-1), { windowType: "agent_detail", entityId: "31" }, "施設の詳細を開く");
await click('.policy-row[data-depth="3"] [data-guide="policy.investigate.facility"]');
assert.deepEqual(rowTitles(), ["松葉町ポンプ場が稼働できていない", "松葉町ポンプ場が人手不足になっている", "失業率が高い",
  "松葉町ポンプ場が予算不足で計画を縮小している"]);
assert.ok(container.textContent!.includes("失業率 20%"), "指標の数値は他の節と同じく根拠の行に出す");
assert.ok(container.querySelector('.policy-row[data-depth="1"] [data-guide="policy.action.budget"]'), "施設の不調にも対策を並べる");
await click('.policy-row[data-depth="1"] .policy-finding-open');
assert.deepEqual(invocations.at(-1), { windowType: "agent_detail", entityId: "30" }, "原因カードの主語の施設を開く");
const beforeBudget = opened.length;
await click('.policy-row[data-depth="1"] [data-guide="policy.action.budget"]');
assert.equal(opened.length, beforeBudget + 1);
assert.equal(opened.at(-1), "city_budget", "対策ボタンはカードの詳細へ遷移しない");
await click('[data-guide="policy.investigate.unemployment"]');
await click('.policy-row[data-depth="0"] .policy-finding-open');
assert.deepEqual(invocations.at(-1), { windowType: "city_statistics", params: { extra: { initialTab: "employment" } } });
assert.equal(container.querySelector('[data-guide="policy.indicator.unemployment"]')?.getAttribute("aria-pressed"), "true");
assert.ok(![...container.querySelectorAll(".policy-problem")].some(row => row.getAttribute("aria-pressed") === "true"),
  "指標へ移ったら一覧の選択を外す");
await click('[data-guide="policy.problem.tenant_blocked_by_parcel_size"]');
assert.deepEqual(rowTitles(), ["地区「港町」で「製鉄所」が入居できる建物を建てられない（必要100m2、区画の最大50m2）"]);
await click('[data-guide="policy.action.parcel"]');
assert.equal(opened.at(-1), "parcel_creation", "原因まで言っている問題は、打つ手だけを示す");
await click('[data-guide="policy.indicator.sanitation"]');
assert.deepEqual(rowTitles(), ["衛生の問題が発生している", "世帯が十分なを受けられていない", "供給する施設がない"],
  "衛生は届いていないサービスを原因として下げる");
assert.ok(container.textContent!.includes("5世帯で衛生が悪化している") && container.textContent!.includes("世帯の受取は基準量の 2.3%"));
await click('.policy-row[data-depth="0"] .policy-finding-open');
assert.deepEqual(invocations.at(-1), { windowType: "city_statistics", params: { extra: { initialTab: "health" } } });
await click('.policy-row[data-depth="1"] .policy-finding-open');
assert.deepEqual(invocations.at(-1), { windowType: "public_facilities", params: { extra: { initialProductId: PRODUCT_GARBAGE_SERVICE } } });
await click('.policy-row[data-depth="2"] .policy-finding-open');
assert.deepEqual(invocations.at(-1), { windowType: "public_facilities", params: { extra: { initialProductId: PRODUCT_GARBAGE_SERVICE } } },
  "供給する施設がない原因にも祖先の品目を引き継ぐ");
const clearPayload = structuredClone(globals.policyPayload) as { indicators: { id: string; issue: boolean; roots: unknown[] }[] };
Object.assign(clearPayload.indicators.find(item => item.id === "water")!, { issue: false, roots: [] });
globals.policyPayload = clearPayload;
await refreshAfterEpoch();
await click('[data-guide="policy.indicator.water"]');
assert.deepEqual(rowTitles(), []);
assert.equal(container.querySelector(".policy-clear")?.textContent, "水道の供給の問題は見つかっていません");
globals.policyPayload = { epoch: 3, pending: false, indicators: [
  { id: "electricity", value: 0.5, unit: "percent", sample: 10, issue: true, roots: [
    { type: "service_out_of_range", domain: "policy", params: { count: 3, facilities: 2 }, actions: ["service_facility"], children: [] },
  ] },
  { id: "sanitation", value: 3, unit: "households", sample: 10, issue: true, roots: [
    { type: "service_shortage", domain: "policy", params: { product_id: PRODUCT_GARBAGE_SERVICE, percent: 20 }, actions: [], children: [
      { type: "service_out_of_range", domain: "policy", params: { count: 3, facilities: 0 }, actions: [], children: [] },
    ] },
  ] },
  { id: "high_water", value: 0.5, unit: "percent", sample: 2, issue: true, roots: [
    { type: "infra_network_split", domain: "diagnosis", params: { product_id: PRODUCT_HIGH_WATER }, actions: [], children: [] },
  ] },
] };
await refreshAfterEpoch();
await click('[data-guide="policy.indicator.electricity"]');
await click('.policy-row[data-depth="1"] .policy-finding-open');
assert.deepEqual(invocations.at(-1), { windowType: "city_map", params: { extra: { initialInfraProductId: PRODUCT_ELECTRICITY } } },
  "供給範囲外は、その財のインフラ表示付きマップを開く");
await click('[data-guide="policy.indicator.sanitation"]');
await click('.policy-row[data-depth="2"] .policy-finding-open');
assert.deepEqual(invocations.at(-1), { windowType: "city_map", params: { extra: { initialInfraProductId: PRODUCT_GARBAGE_SERVICE } } },
  "衛生の原因をたどった場合も、具体的なサービスの範囲を開く");
await click('[data-guide="policy.indicator.high_water"]');
await click('.policy-row[data-depth="0"] .policy-finding-open');
assert.deepEqual(invocations.at(-1), { windowType: "public_facilities", params: { extra: { initialProductId: PRODUCT_HIGH_WATER } } });
await click('.policy-row[data-depth="1"] .policy-finding-open');
assert.deepEqual(invocations.at(-1), { windowType: "city_map", params: { extra: { initialInfraProductId: PRODUCT_HIGH_WATER } } });
await act(async () => root.unmount());
const afterUnmount = globals.policyRequests;
await act(async () => dispatch("epoch:executing", { executing: false }));
await flush();
assert.equal(globals.policyRequests, afterUnmount);
console.log("PASS: compact selectors, aligned actions, terminal indicator references, in-window switching, read-only mode, epoch refresh and problems");
