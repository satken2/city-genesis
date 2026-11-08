import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import { Window } from "happy-dom";

const directory = new URL("../node_modules/.cache/agent-alerts-tests/", import.meta.url);
await build({
  entryPoints: [
    "src/utils/agentAlerts.ts",
    "src/components/agent/AgentAlertsPanel.tsx",
    "src/constants.ts",
  ].map(path => fileURLToPath(new URL(`../${path}`, import.meta.url))),
  outdir: fileURLToPath(directory),
  entryNames: "[name]",
  outExtension: { ".js": ".mjs" },
  bundle: true, platform: "node", format: "esm", packages: "external",
  loader: { ".css": "empty", ".png": "dataurl", ".svg": "dataurl" },
});
const load = name => import(new URL(`${name}.mjs`, directory).href);
const { buildingWarningMask, fetchSpatialWarnings } = await load("agentAlerts");
const { AgentAlertsPanel } = await load("AgentAlertsPanel");
const constants = await load("constants");
const facility = constants.ALERT_DISPLAY_FACILITY_WARNING;
const sanitation = constants.ALERT_DISPLAY_SANITATION_WARNING;
const infra = constants.ALERT_DISPLAY_INFRA_WARNING;
const building = { agent_id: 69, parcel_id: 193, is_bd: true };
const warnings = [
  { entity_type: "agent", id: 69, warning: facility },
  { entity_type: "parcel", id: 193, warning: sanitation },
  { entity_type: "parcel", id: 193, warning: sanitation },
  { entity_type: "agent", id: 193, warning: infra },
  { entity_type: "parcel", id: 69, warning: infra },
  { entity_type: "resource", id: 69, warning: constants.ALERT_DISPLAY_RESOURCE_WARNING },
];
assert.equal(buildingWarningMask(building, warnings), facility | sanitation,
  "建物自身と所属区画だけを選び、別の種別で同じIDを持つ対象を混ぜない");
assert.equal(buildingWarningMask({ ...building, agent_id: 70, parcel_id: 194 }, warnings), 0,
  "別の建物へ移ったときに元の警告を残さない");
assert.equal(buildingWarningMask({ ...building, is_bd: false }, warnings), 0,
  "建物を持たない経済主体には建物の空間警告を表示しない");
assert.equal(buildingWarningMask({ ...building, parcel_id: undefined }, warnings), facility);
assert.equal(buildingWarningMask({ ...building, parcel_id: 0 }, [
  { entity_type: "parcel", id: 0, warning: sanitation },
]), 0);
assert.equal(buildingWarningMask(building, []), 0, "警告が解消した応答ではカードを消す");

const nativeFetch = globalThis.fetch;
const controller = new AbortController();
try {
  globalThis.fetch = async (url, options) => {
    assert.equal(url, "http://localhost:5000/api/v1/alerts/");
    assert.equal(options.signal, controller.signal);
    return new Response(JSON.stringify({ warnings }), { headers: { "Content-Type": "application/json" } });
  };
  const received = await fetchSpatialWarnings("http://localhost:5000/", controller.signal);
  assert.equal(buildingWarningMask(building, received), facility | sanitation);
  globalThis.fetch = async () => new Response("{}", { headers: { "Content-Type": "application/json" } });
  assert.deepEqual(await fetchSpatialWarnings("http://localhost:5000"), []);
  globalThis.fetch = async () => new Response("{}", { status: 503 });
  await assert.rejects(fetchSpatialWarnings("http://localhost:5000"), error => error.status === 503,
    "警告APIの失敗を警告なしとして扱わない");
} finally {
  globalThis.fetch = nativeFetch;
}

const translations = JSON.parse(await readFile(new URL("../src/i18n/ja/agent.json", import.meta.url), "utf8"));
await i18n.use(initReactI18next).init({
  lng: "ja", resources: { ja: { translation: translations } }, interpolation: { escapeValue: false },
});
const window = new Window();
const render = props => {
  const element = window.document.createElement("div");
  element.innerHTML = renderToStaticMarkup(createElement(AgentAlertsPanel, props));
  return element;
};
const sanitationOnly = render({ alert: 0, warning: buildingWarningMask(building, [warnings[1]]) });
assert.equal(sanitationOnly.querySelectorAll(".agent-detail-alert-card").length, 1,
  "建物自身のalertが0でも敷地の衛生警告を表示する");
assert.equal(sanitationOnly.querySelector(".agent-detail-alert-title").textContent,
  translations.agent.alerts.sanitationWarning.title);
assert.ok(sanitationOnly.textContent.includes("居住世帯"));
assert.ok(sanitationOnly.textContent.includes("下水・廃棄物処理"));
const combined = render({ alert: constants.ALERT_AGENT_ON_FIRE, warning: facility | sanitation | infra });
assert.equal(combined.querySelectorAll(".agent-detail-alert-card").length, 4,
  "既存の建物アラートと空間警告を併記する");
for (const cause of [
  constants.ALERT_AGENT_INFRA_DISCONNECTED,
  constants.ALERT_AGENT_LABOR_SHORTAGE,
  constants.ALERT_AGENT_MATERIAL_SHORTAGE,
  constants.ALERT_AGENT_HIGH_SKILL_SHORTAGE,
  constants.ALERT_AGENT_BUDGET_CAPPED,
]) {
  const explained = render({ alert: cause, warning: facility | sanitation | infra });
  assert.equal(explained.querySelectorAll(".agent-detail-alert-card").length, 3,
    "運営不能の原因と、衛生・インフラの独立した警告を残す");
  assert.ok(!explained.textContent.includes(translations.agent.alerts.facilityWarning.title),
    "原因を表示するときは運営不能の総括を重複表示しない");
}
const unexplained = render({ alert: undefined, warning: facility });
assert.equal(unexplained.querySelectorAll(".agent-detail-alert-card").length, 1,
  "原因ビットがない運営不能の警告は残す");
assert.ok(unexplained.textContent.includes(translations.agent.alerts.facilityWarning.title));
const multipleCauses = render({
  alert: constants.ALERT_AGENT_MATERIAL_SHORTAGE | constants.ALERT_AGENT_LABOR_SHORTAGE,
  warning: facility,
});
assert.equal(multipleCauses.querySelectorAll(".agent-detail-alert-card").length, 2,
  "複数の原因はどちらも残す");
const icon = props => render(props).querySelector(".agent-detail-alert-icon").getAttribute("src");
assert.equal(new Set([
  icon({ alert: constants.ALERT_AGENT_MATERIAL_SHORTAGE }),
  icon({ alert: 0, warning: facility }),
  icon({ alert: constants.ALERT_AGENT_BUDGET_CAPPED }),
]).size, 3, "原材料不足・運営不能・予算不足を異なるアイコンで区別する");
assert.equal(render({ alert: 0, warning: 0 }).innerHTML, "");
assert.equal(render({ alert: sanitation, warning: 0 }).innerHTML, "",
  "alertとwarningのビット領域を混同しない");
console.log("Agent alerts: API source, target IDs, combined cards, facility causes, distinct icons, cleared warnings OK");
