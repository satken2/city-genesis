import assert from "node:assert/strict";
import { Window } from "happy-dom";
import { act } from "react";
import { createRoot } from "react-dom/client";
import definition from "../../../version.json";

const base = "http://127.0.0.1:5000";
const server = { versionStatus: 200,
  requests: [] as { url: string; method: string; contract: string | null; body: string | null }[] };
globalThis.fetch = async (input, init) => {
  const request = new Request(input, init);
  const body = request.method === "GET" ? null : await request.text();
  server.requests.push({ url: request.url, method: request.method, contract: request.headers.get("X-City-Genesis-API"), body });
  if (!request.url.startsWith("http://127.0.0.1:")) return new Response("{}");
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

api.configureBackendRequests(base);
assert.equal((await fetch(`${base}/api/v1/city`)).status, 200);
assert.deepEqual(server.requests.map(r => r.url), [`${base}/api/v1/system/version`, `${base}/api/v1/city`], "the version check runs once before the first request");
assert.equal(server.requests.at(-1)?.contract, String(definition.api_contract_version));

await fetch("http://example.test/api/v1/city");
assert.equal(server.requests.at(-1)?.contract, null, "the contract header is never sent to other origins");

await api.fetchApi(`${base}/api/v1/city`, { method: "POST", body: JSON.stringify({ a: 1 }) });
assert.deepEqual(server.requests.filter(r => r.method === "POST").map(r => r.body), ["{\"a\":1}"], "the body is sent exactly once");

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

// 版の照合に失敗したら版の不一致として失敗する。失敗した照合は共有しない。
const other = "http://127.0.0.1:5001";
api.configureBackendRequests(other);
server.versionStatus = 503;
await assert.rejects(api.fetchApi(`${other}/api/v1/city`),
  (error: InstanceType<typeof api.ApiError>) => error instanceof api.ApiError && error.code === "API_CONTRACT_MISMATCH" && error.status === 503);
server.versionStatus = 200;
assert.deepEqual(await api.fetchApi(`${other}/api/v1/city`), { ok: true }, "the version check runs again after a failure");
console.log("PASS: contract header scoping, body sent once, Blob URL thumbnails and version-check failure");
