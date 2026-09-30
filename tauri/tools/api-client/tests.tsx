import assert from "node:assert/strict";
import { Window } from "happy-dom";
import { act } from "react";
import { createRoot } from "react-dom/client";
import definition from "../../../version.json";

const base = "http://127.0.0.1:5000";
const server = { apiStatus: 200,
  requests: [] as { url: string; method: string; contract: string | null; body: string | null }[] };
globalThis.fetch = async (input, init) => {
  const request = new Request(input, init);
  const body = request.method === "GET" ? null : await request.text();
  server.requests.push({ url: request.url, method: request.method, contract: request.headers.get("X-City-Genesis-API"), body });
  if (!request.url.startsWith("http://127.0.0.1:")) return new Response("{}");
  if (server.apiStatus === 409) return Response.json({ code: "API_CONTRACT_MISMATCH", message: "contract mismatch" }, { status: 409 });
  if (request.url.includes("/saves/local~a/thumbnail")) return new Response(new Blob([new Uint8Array([137, 80, 78, 71])], { type: "image/png" }));
  if (request.url.includes("/saves/")) return Response.json({ code: "NOT_FOUND", message: "none" }, { status: 404 });
  return Response.json({ ok: true });
};
const api = await import("../../src/utils/api");

api.configureBackendRequests(base);
assert.equal((await fetch(`${base}/api/v1/city`)).status, 200);
assert.deepEqual(server.requests.map(r => r.url), [`${base}/api/v1/city`], "WebViews send the requested API call without a version probe");
assert.equal(server.requests.at(-1)?.contract, String(definition.api_contract_version));

await fetch("http://example.test/api/v1/city");
assert.equal(server.requests.at(-1)?.contract, null, "the contract header is never sent to other origins");

await api.fetchApi(`${base}/api/v1/city`, { method: "POST", body: JSON.stringify({ a: 1 }) });
assert.deepEqual(server.requests.filter(r => r.method === "POST").map(r => r.body), ["{\"a\":1}"], "the body is sent exactly once");

api.setBackendConnected(false);
const beforeDisconnect = server.requests.length;
for (const method of ["POST", "PATCH", "PUT", "DELETE"]) {
  await assert.rejects(api.fetchApi(`${base}/api/v1/city`, { method }),
    (error: InstanceType<typeof api.ApiError>) => error.code === "BACKEND_DISCONNECTED");
}
await assert.rejects(fetch(new Request(`${base}/api/v1/city`, { method: "POST", body: "once" })),
  (error: InstanceType<typeof api.ApiError>) => error.code === "BACKEND_DISCONNECTED");
assert.equal(server.requests.length, beforeDisconnect, "disconnected mutations never reach the server");
await api.fetchApi(`${base}/api/v1/city`);
await fetch("http://example.test/upload", { method: "POST" });
const beforeReconnect = server.requests.length;
api.setBackendConnected(true);
await new Promise(resolve => setTimeout(resolve, 0));
assert.equal(server.requests.length, beforeReconnect, "reconnection never replays rejected mutations");
await api.fetchApi(`${base}/api/v1/city`, { method: "PATCH", body: "new-operation" });
assert.equal(server.requests.at(-1)?.body, "new-operation", "new operations are allowed after recovery");

const originalNow = Date.now;
try {
  Date.now = () => originalNow() + 60000;
  await Promise.all([api.fetchApi(`${base}/api/v1/city`), api.fetchApi(`${base}/api/v1/sessions`)]);
  assert.ok(server.requests.every(r => !r.url.endsWith("/system/version")), "elapsed time and concurrent requests never trigger version probes");
} finally {
  Date.now = originalNow;
}

const window = new Window();
Object.assign(globalThis, { window, document: window.document, HTMLElement: window.HTMLElement, IS_REACT_ACT_ENVIRONMENT: true });
const revoked: string[] = [];
const revokeObjectURL = URL.revokeObjectURL.bind(URL);
URL.revokeObjectURL = (url: string) => { revoked.push(url); revokeObjectURL(url); };
const { ScenarioThumbnail } = await import("../../src/components/common/ScenarioThumbnail");
const container = window.document.createElement("div");
window.document.body.append(container);
const root = createRoot(container);
await act(async () => { root.render(<ScenarioThumbnail apiBaseUrl={base} templateId="local~a" />); });
await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)); });
const src = container.querySelector("img")?.getAttribute("src") ?? "";
assert.ok(src.startsWith("blob:"), "the scenario image is shown as a Blob URL");
assert.ok(server.requests.some(r => r.url === `${base}/api/v1/saves/local~a/thumbnail?template=true`));
await act(async () => { root.unmount(); });
assert.deepEqual(revoked, [src], "Blob URL is revoked on unmount");
const missing = createRoot(container);
await act(async () => { missing.render(<ScenarioThumbnail apiBaseUrl={base} templateId="local~none" className="thumb" placeholderModifier="thumb--empty" />); });
await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)); });
assert.ok(!container.querySelector("img") && container.querySelector(".thumb.thumb--empty"), "a scenario without an image shows the placeholder");
await act(async () => { missing.unmount(); });

// 通常要求の契約不一致はコアが返した理由コードで扱い、版情報の再取得は行わない。
const other = "http://127.0.0.1:5001";
api.configureBackendRequests(other);
server.apiStatus = 409;
await assert.rejects(api.fetchApi(`${other}/api/v1/city`),
  (error: InstanceType<typeof api.ApiError>) => error instanceof api.ApiError && error.code === "API_CONTRACT_MISMATCH" && error.status === 409);
server.apiStatus = 200;
assert.deepEqual(await api.fetchApi(`${other}/api/v1/city`), { ok: true });
assert.ok(server.requests.every(r => !r.url.endsWith("/system/version")), "changing the configured URL or receiving a mismatch never starts a WebView version check");

const { TopMenu } = await import("../../src/components/topmenu/TopMenu");
const { runtimeState, emitSessionState, emitBackendConnection } = await import("./mock-runtime");
const topmenu = createRoot(container);
const requestsBeforeTopmenu = server.requests.length;
const originalSetInterval = globalThis.setInterval;
let intervalCount = 0;
globalThis.setInterval = ((...args: Parameters<typeof setInterval>) => {
  intervalCount += 1;
  return originalSetInterval(...args);
}) as typeof setInterval;
try {
  await act(async () => { topmenu.render(<TopMenu apiBaseUrl={base} />); });
  assert.deepEqual(runtimeState.commands, ["get_core_version"], "TopMenu reads the shared Rust version once");
  assert.equal(intervalCount, 0, "TopMenu never starts a version polling timer");
  await act(async () => { emitSessionState({ current_save_id: null, session: {}, isLoading: false, coreVersion: "1.0.0", backendConnected: true }); });
  assert.ok(container.textContent?.includes("Core v1.0.0"));
  assert.ok(!container.querySelector<HTMLButtonElement>(".topmenu-btn-primary")?.disabled);
  await act(async () => { emitBackendConnection(false); });
  assert.ok(container.querySelector<HTMLButtonElement>(".topmenu-btn-primary")?.disabled,
    "connection waiting disables operations without requiring a session reset");
  await act(async () => { emitBackendConnection(true); });
  assert.ok(!container.querySelector<HTMLButtonElement>(".topmenu-btn-primary")?.disabled,
    "a successful health check resumes operations");
  await act(async () => { emitSessionState({ current_save_id: "save-a", session: {}, isLoading: false, coreVersion: "1.0.0", backendConnected: true }); });
  await act(async () => { emitSessionState({ current_save_id: null, session: null, isLoading: false, coreVersion: "1.0.0", backendConnected: false }); });
  assert.ok(container.textContent?.includes("topmenu.coreConnectionFailed"));
  assert.ok(container.querySelector<HTMLButtonElement>(".topmenu-btn-primary")?.disabled, "disconnection disables game starts");
  assert.equal(server.requests.length, requestsBeforeTopmenu, "connection updates and hidden TopMenu never fetch version information");
} finally {
  globalThis.setInterval = originalSetInterval;
  await act(async () => { topmenu.unmount(); });
}
console.log("PASS: contract headers, Blob URL thumbnails, backend mismatch errors, no WebView version probes or TopMenu timers, session-driven connection display");
