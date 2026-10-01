import assert from "node:assert/strict";
import { Window } from "happy-dom";
import { act } from "react";
import { createRoot } from "react-dom/client";
import { supplyColumns, type SupplyGraph, type SupplyNode } from "../../src/components/supply/supplyGraph";
import { SupplyChainGraph } from "../../src/components/supply/SupplyChainGraph";
import { useSupplyGraph } from "../../src/hooks/useSupplyGraph";
import { runtime, dispatch, gainFocus } from "./mock-runtime";

const window = new Window();
Object.assign(globalThis, { window, document: window.document, HTMLElement: window.HTMLElement,
  ResizeObserver: class { observe() {} disconnect() {} }, IS_REACT_ACT_ENVIRONMENT: true });
const node = (id: string): SupplyNode => ({ id, kind: "facility", role: "producer", selected: id === "selected",
  state: "ok", product_id: 401, count: 2, active_count: 2, production: 100,
  variants: [{ variant: 1054, count: 2, active_count: 2, production: 100 }], agent_ids: [] });
const graph: SupplyGraph = { selected_id: "selected", has_results: true, truncated: false,
  nodes: [node("selected"), node("plant"), { ...node("water"), state: "missing", kind: "resource", role: "resource" }],
  edges: [{ source: "water", target: "plant", product_id: 5, transport: "resource", state: "missing", cycle: false,
    recipient_count: 2, reachable_count: 0, received: 0, demand: 10, path_ids: [] },
  { source: "plant", target: "selected", product_id: 401, transport: "infrastructure", state: "capacity_limited", cycle: false,
    recipient_count: 1, reachable_count: 1, received: 3, demand: 10, path_ids: [9] }] };
assert.deepEqual(supplyColumns(graph).map(column => column.map(n => n.id)), [["water"], ["plant"], ["selected"]]);
const cycle = { ...graph, edges: [...graph.edges, { ...graph.edges[0], source: "plant", target: "water", cycle: true }] };
assert.equal(supplyColumns(cycle).flat().length, 3, "cycles never create a second facility box");
const container = window.document.createElement("div");
window.document.body.append(container);
const root = createRoot(container);
await act(async () => { root.render(<SupplyChainGraph graph={graph} />); });
assert.equal(container.querySelectorAll(".supply-node").length, 3);
assert.equal(container.querySelectorAll(".supply-node--selected").length, 1);
assert.equal(container.querySelectorAll(".supply-node--missing").length, 1);
assert.equal(container.querySelector(".supply-edge--missing text")?.textContent, "×");
assert.equal(container.querySelector(".supply-edge--capacity_limited text")?.textContent, "!");
await act(async () => { (container.querySelector(".supply-node--selected") as HTMLElement).click(); });
assert.ok(container.querySelector(".supply-details")?.textContent?.includes("容量制約の可能性"));
assert.ok(container.querySelector(".supply-details")?.textContent?.includes("前回受領量：3"));
assert.ok(container.querySelector(".supply-details")?.textContent?.includes("次回需要：10"));

function Harness({ id, enabled = true }: { id: string; enabled?: boolean }) {
  const { data, pending, refresh } = useSupplyGraph("http://core/", id, enabled);
  return <button onClick={refresh}>{pending ? "pending" : data?.epoch ?? "empty"}</button>;
}
await act(async () => { root.render(<Harness id="1" />); });
assert.equal(runtime.requests.length, 1);
await act(async () => { root.render(<Harness id="2" />); });
assert.ok(runtime.requests[0].signal.aborted);
await act(async () => { runtime.requests[1].resolve({ epoch: 2, pending: false }); });
await act(async () => { runtime.requests[0].resolve({ epoch: 1, pending: false }); });
assert.equal(container.textContent, "2", "a late old-agent response cannot replace the selected agent");
runtime.visible = false;
await act(async () => { dispatch("supply:changed"); dispatch("epoch:executing", { executing: true }); });
assert.equal(container.textContent, "pending");
await act(async () => { dispatch("epoch:executing", { executing: false }); });
assert.equal(runtime.requests.length, 2, "hidden panels do not fetch on edits or epoch completion");
runtime.visible = true;
await act(async () => { gainFocus(); });
assert.equal(runtime.requests.length, 3, "a reopened panel refreshes its supply graph");
await act(async () => { dispatch("epoch:executing", { executing: true }); });
assert.ok(runtime.requests[2].signal.aborted);
await act(async () => { dispatch("epoch:executing", { executing: false }); });
assert.equal(runtime.requests.length, 4, "epoch completion resumes analysis");
await act(async () => { runtime.requests[3].resolve({ epoch: 3, pending: true }); });
assert.equal(container.textContent, "pending");
await act(async () => { (container.querySelector("button") as HTMLElement).click(); });
assert.equal(runtime.requests.length, 5, "a server-pending response can be manually retried");
await act(async () => { root.unmount(); });
assert.ok(runtime.requests[4].signal.aborted);
console.log("PASS: supply layout, distinct failure indicators, details and request lifecycle");
