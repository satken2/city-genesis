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
  loader: { ".css": "empty", ".png": "dataurl" },
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
  await assert.rejects(fetchSpatialWarnings("http://localhost:5000"), /HTTP 503/,
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
assert.equal(render({ alert: 0, warning: 0 }).innerHTML, "");
assert.equal(render({ alert: sanitation, warning: 0 }).innerHTML, "",
  "alertとwarningのビット領域を混同しない");
console.log("Agent alerts: API source, target IDs, sanitation-only and combined cards, cleared warnings OK");
