import assert from "node:assert/strict";
import { Window } from "happy-dom";
import { act } from "react";
import { createRoot } from "react-dom/client";
import { runtime } from "./mock-runtime";
import definition from "../../../version.json";

const base = "http://127.0.0.1:5000";
const server = { token: "new", hostRejected: false, versionStatus: 200,
  requests: [] as { url: string; method: string; token: string | null; body: string | null }[] };
globalThis.fetch = async (input, init) => {
  const request = new Request(input, init);
  const token = request.headers.get("X-City-Genesis-Token");
  const body = request.method === "GET" ? null : await request.text();
  server.requests.push({ url: request.url, method: request.method, token, body });
  if (!request.url.startsWith("http://127.0.0.1:")) return new Response("{}");
  if (server.hostRejected) return Response.json({ code: "HOST_NOT_ALLOWED", message: "host" }, { status: 421 });
  if (token !== server.token) return Response.json({ code: "API_TOKEN_REQUIRED", message: "token" }, { status: 401 });
  if (request.url.endsWith("/system/version")) {
    return server.versionStatus === 200
      ? Response.json({ api_contract_version: definition.api_contract_version })
      : new Response("unavailable", { status: server.versionStatus });
  }
  if (request.url.includes("/saves/local~a/thumbnail")) return new Response(new Blob([new Uint8Array([137, 80, 78, 71])], { type: "image/png" }));
  if (request.url.includes("/saves/")) return Response.json({ code: "NOT_FOUND", message: "none" }, { status: 404 });
  return Response.json({ ok: true });
};
const api = await import("../../src/utils/api");
const refreshes = () => runtime.calls.filter(command => command === "get_connection_status").length;

api.setApiToken("old");
api.configureBackendRequests(base);
assert.equal((await fetch(`${base}/api/v1/city`)).status, 200, "stale token is refreshed and retried");
assert.equal(refreshes(), 1);
assert.deepEqual(server.requests.map(r => r.token), ["old", "new", "new"], "version check and request both retry with the new token");

await fetch("http://example.test/api/v1/city");
assert.equal(server.requests.at(-1)?.token, null, "token is never sent to other origins");

server.token = runtime.token = "rotated";
await api.fetchApi(`${base}/api/v1/city`, { method: "POST", body: JSON.stringify({ a: 1 }) });
const posts = server.requests.filter(r => r.method === "POST");
assert.deepEqual(posts.map(r => [r.token, r.body]), [["new", "{\"a\":1}"], ["rotated", "{\"a\":1}"]], "body is resent once");

server.token = "server-only"; runtime.token = "still-wrong";
const before = server.requests.length;
await assert.rejects(api.fetchApi(`${base}/api/v1/city`), (error: InstanceType<typeof api.ApiError>) => error.status === 401 && error.code === "API_TOKEN_REQUIRED");
assert.equal(server.requests.length - before, 2, "a rejected token is retried only once");

server.token = runtime.token = "shared";
runtime.calls.length = 0;
await Promise.all([1, 2, 3].map(() => fetch(`${base}/api/v1/city`)));
assert.equal(refreshes(), 1, "concurrent 401 responses share one token lookup");

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
assert.ok(server.requests.some(r => r.url === `${base}/api/v1/saves/local~a/thumbnail?template=true` && r.token === "shared"));
await act(async () => { root.unmount(); });
assert.deepEqual(revoked, [src], "Blob URL is revoked on unmount");
const missing = createRoot(container);
await act(async () => { missing.render(<ScenarioThumbnail apiBaseUrl={base} templateId="local~none" className="thumb" placeholderModifier="thumb--empty" />); });
await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)); });
assert.ok(!container.querySelector("img") && container.querySelector(".thumb.thumb--empty"), "a scenario without an image shows the placeholder");
await act(async () => { missing.unmount(); });

// 版の照合が認証・Host 検査で断られたら、版の不一致にせずその理由コードで失敗する。失敗した照合は共有しない。
const other = "http://127.0.0.1:5001";
api.configureBackendRequests(other);
const rejectsWith = (code: string, status: number) => assert.rejects(api.fetchApi(`${other}/api/v1/city`),
  (error: InstanceType<typeof api.ApiError>) => error instanceof api.ApiError && error.code === code && error.status === status);
server.token = "server-only"; runtime.token = "still-wrong";
await rejectsWith("API_TOKEN_REQUIRED", 401);
server.token = runtime.token = "shared"; server.hostRejected = true;
await rejectsWith("HOST_NOT_ALLOWED", 421);
server.hostRejected = false; server.versionStatus = 503;
await rejectsWith("API_CONTRACT_MISMATCH", 503);
server.versionStatus = 200;
assert.deepEqual(await api.fetchApi(`${other}/api/v1/city`), { ok: true }, "the version check runs again after a failure");
console.log("PASS: API token refresh/retry, origin scoping, body resend, shared lookup, authenticated thumbnails and version-check rejection codes");
