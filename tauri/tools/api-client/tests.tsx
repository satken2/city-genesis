import assert from "node:assert/strict";
import { Window } from "happy-dom";
import { act } from "react";
import { createRoot } from "react-dom/client";
import { readFileSync, readdirSync } from "node:fs";
import i18n from "i18next";

const languages = ["ja", "en", "es-419", "fr", "ko", "zh-Hans", "zh-Hant"];
const translations = Object.fromEntries(languages.map(language => {
  const folder = `src/i18n/${language}`;
  const translation = Object.assign({}, ...readdirSync(folder).filter(file => file.endsWith(".json"))
    .map(file => JSON.parse(readFileSync(`${folder}/${file}`, "utf8"))));
  return [language, { translation }];
}));
await i18n.init({ resources: translations, lng: "en", fallbackLng: "ja", interpolation: { escapeValue: false } });

const base = "http://127.0.0.1:5000";
const server = { requests: [] as { url: string; method: string; body: string | null }[] };
globalThis.fetch = async (input, init) => {
  const request = new Request(input, init);
  const body = request.method === "GET" ? null : await request.text();
  server.requests.push({ url: request.url, method: request.method, body });
  if (request.url.includes("/saves/local~a/thumbnail")) return new Response(new Blob([new Uint8Array([137, 80, 78, 71])], { type: "image/png" }));
  if (request.url.includes("/saves/")) return Response.json({ code: "NOT_FOUND", message: "none" }, { status: 404 });
  return Response.json({ ok: true });
};
const api = await import("../../src/utils/api");
const { runtimeState } = await import("./mock-runtime");

api.configureBackendRequests(base);

// HTTP と Rust のエラーを同じ表示関数で翻訳し、表示時の言語を使う。
const structured = { code: "TRANSIT_STOP_INVALID_POSITION", params: { stop_number: 3 } };
let failure: unknown;
try { await api.requireApiSuccess(Response.json(structured, { status: 400 })); } catch (error) { failure = error; }
assert.ok(failure instanceof api.ApiError);
assert.equal(api.describeError(failure), api.describeError(structured));
assert.match(api.describeError(failure), /Stop 3/);
await i18n.changeLanguage("ja");
assert.match(api.describeError(failure), /停留所3/);
await i18n.changeLanguage("en");
for (const value of [new Error("内部の診断文"), "CODE: 内部の診断文", { code: "NEW_UNKNOWN_CODE", message: "内部の診断文" }, null]) {
  assert.equal(api.describeError(value), i18n.t("errors.UNKNOWN_ERROR"));
}
for (const response of [Response.json({ error: "内部の診断文" }, { status: 500 }), new Response("private body", { status: 500 })]) {
  await assert.rejects(api.requireApiSuccess(response), error => api.describeError(error) === i18n.t("errors.UNKNOWN_ERROR"));
}
const referenceErrors = translations.ja.translation.errors;
for (const language of languages) {
  const errors = translations[language].translation.errors;
  assert.deepEqual(Object.keys(errors).sort(), Object.keys(referenceErrors).sort(), `${language}: error codes match`);
  for (const [key, text] of Object.entries(referenceErrors)) {
    const slots = (text as string).match(/\{\{[^}]+\}\}/g) ?? [];
    assert.deepEqual((errors[key] as string).match(/\{\{[^}]+\}\}/g) ?? [], slots, `${language}: ${key} interpolation matches`);
  }
  await i18n.changeLanguage(language);
  assert.ok(!api.describeError(structured).includes("{{"));
  const capacity = api.describeError({ code: "TRANSIT_TRACK_CAPACITY_EXCEEDED", params: {
    overloads: [{ path_id: 7, load: 500, capacity: 400, single_track: true, min_headway: 3.14 }],
  } });
  assert.ok(capacity.includes("7") && capacity.includes("3.2") && !capacity.includes("{{"));
}
await i18n.changeLanguage("en");
assert.equal((await fetch(`${base}/api/v1/city`)).status, 200);
await api.fetchApi(`${base}/api/v1/city`, { method: "POST", body: JSON.stringify({ a: 1 }) });
assert.deepEqual(server.requests.filter(r => r.method === "POST").map(r => r.body), ["{\"a\":1}"], "the body is sent exactly once");

// 通信断の間は、応答だけ失われると二重実行になる更新要求を送らない。読み取りと他のオリジンへの要求は通す。
api.setBackendConnected(false);
const before = server.requests.length;
await assert.rejects(api.fetchApi(`${base}/api/v1/city`, { method: "POST", body: "{}" }),
  (error: InstanceType<typeof api.ApiError>) => error instanceof api.ApiError && error.code === "BACKEND_DISCONNECTED" && error.status === 503);
assert.equal(server.requests.length, before, "an update request is not sent while disconnected");
assert.equal((await fetch(`${base}/api/v1/city`)).status, 200, "a read request is still sent while disconnected");
assert.equal((await fetch("http://example.test/api/v1/city", { method: "POST", body: "{}" })).status, 200, "other origins are not affected");
api.setBackendConnected(true);

const window = new Window();
Object.assign(globalThis, { window, document: window.document, HTMLElement: window.HTMLElement, IS_REACT_ACT_ENVIRONMENT: true });
const revoked: string[] = [];
const revokeObjectURL = URL.revokeObjectURL.bind(URL);
URL.revokeObjectURL = (url: string) => { revoked.push(url); revokeObjectURL(url); };
const { ScenarioThumbnail } = await import("../../src/components/common/ScenarioThumbnail");
const container = window.document.createElement("div");
window.document.body.append(container);
const root = createRoot(container);
await act(async () => { root.render(<ScenarioThumbnail apiBaseUrl={base} scenarioId="local~a" />); });
await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)); });
const src = container.querySelector("img")?.getAttribute("src") ?? "";
assert.ok(src.startsWith("blob:"), "the scenario image is shown as a Blob URL");
assert.ok(server.requests.some(r => r.url === `${base}/api/v1/saves/local~a/thumbnail?scenario=true`));
await act(async () => { root.unmount(); });
assert.deepEqual(revoked, [src], "Blob URL is revoked on unmount");
const missing = createRoot(container);
await act(async () => { missing.render(<ScenarioThumbnail apiBaseUrl={base} scenarioId="local~none" className="thumb" placeholderModifier="thumb--empty" />); });
await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)); });
assert.ok(!container.querySelector("img") && container.querySelector(".thumb.thumb--empty"), "a scenario without an image shows the placeholder");
await act(async () => { missing.unmount(); });
console.log("PASS: body sent once, update requests held back while disconnected, Blob URL thumbnails");
